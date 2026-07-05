using System.Globalization;
using BenchmarkDotNet.Running;
using Manatee.Oracle;
using Manatee.SolverBench;

if (args.Length > 0 && args[0] == "verify")
    return Verifier.Run(oracle: Environment.GetEnvironmentVariable("MANATEE_ORACLE") != "0");
if (args.Length > 0 && args[0] == "stress")
    return PivotStress.Run();

BenchmarkSwitcher.FromAssembly(typeof(Registry).Assembly).Run(args);
return 0;

/// <summary>
/// Frozen-pivot stress: sparse-lu freezes its pivot order at the first
/// Factorize (KLU-style). Does that order stay numerically sound when
/// every conductance is redrawn across the full legal range (a maximal
/// tier-2 swing)? Compares refactorize-with-frozen-pivots against a
/// fresh re-pivoted factorization of the same values.
/// </summary>
internal static class PivotStress
{
    public static int Run()
    {
        const int rounds = 200;
        var proto = Circuits.LadderExtreme(500, seed: 0);

        var frozen = new Manatee.SolverBench.Backends.SparseLuBackend();
        frozen.Analyze(proto.Dimension, proto.Pattern);
        frozen.Factorize(proto.Values);

        double worstFrozen = 0, worstFresh = 0, worstDisagree = 0;
        var failures = 0;
        var xFrozen = new double[proto.Dimension];
        var xFresh = new double[proto.Dimension];

        for (var round = 1; round <= rounds; round++)
        {
            var system = Circuits.LadderExtreme(500, seed: round);
            if (!system.Pattern.AsSpan().SequenceEqual(proto.Pattern))
                throw new InvalidOperationException("pattern drifted between seeds — generator bug");

            var scale = Math.Max(1.0, system.Rhs.Max(Math.Abs));
            double frozenResidual;
            try
            {
                frozen.Factorize(system.Values);
                frozen.Solve(system.Rhs, xFrozen);
                frozenResidual = system.ResidualInfNorm(xFrozen) / scale;
            }
            catch (InvalidOperationException e)
            {
                // A legible refusal (zero pivot) is a *good* outcome vs silent garbage.
                Console.WriteLine($"  round {round}: frozen refactor refused: {e.Message}");
                failures++;
                continue;
            }

            var fresh = new Manatee.SolverBench.Backends.SparseLuBackend();
            fresh.Analyze(system.Dimension, system.Pattern);
            fresh.Factorize(system.Values);
            fresh.Solve(system.Rhs, xFresh);
            var freshResidual = system.ResidualInfNorm(xFresh) / scale;

            double disagree = 0;
            for (var i = 0; i < system.Dimension; i++)
                disagree = Math.Max(disagree, Math.Abs(xFrozen[i] - xFresh[i]) /
                    Math.Max(1e-9, Math.Abs(xFresh[i])));

            worstFrozen = Math.Max(worstFrozen, frozenResidual);
            worstFresh = Math.Max(worstFresh, freshResidual);
            worstDisagree = Math.Max(worstDisagree, disagree);
            if (frozenResidual > 1e-6) failures++;
        }

        Console.WriteLine($"rounds: {rounds}");
        Console.WriteLine($"worst scaled residual, frozen pivots : {worstFrozen:E2}");
        Console.WriteLine($"worst scaled residual, fresh pivots  : {worstFresh:E2}");
        Console.WriteLine($"worst frozen-vs-fresh solution diff  : {worstDisagree:E2}");
        Console.WriteLine($"failures (residual > 1e-6 or refusal): {failures}");
        Console.WriteLine(failures == 0 ? "STRESS PASS" : "STRESS FAIL — frozen pivots need a growth monitor");
        return failures == 0 ? 0 : 1;
    }
}

internal static class Verifier
{
    public static int Run(bool oracle)
    {
        var systems = Circuits.VerificationSet().ToArray();
        var failures = 0;

        // Reference solutions from the referee.
        var reference = new Dictionary<string, double[]>();
        foreach (var system in systems)
        {
            var referee = new Manatee.SolverBench.Backends.NaiveDenseBackend();
            referee.Analyze(system.Dimension, system.Pattern);
            referee.Factorize(system.Values);
            var x = new double[system.Dimension];
            referee.Solve(system.Rhs, x);
            reference[system.Name] = x;

            var residual = system.ResidualInfNorm(x);
            Report(ref failures, residual < Tolerance(system), $"referee residual {system.Name}: {residual:E2}");

            if (oracle && system.SpiceNetlist is not null)
            {
                var raw = new NgspiceRunner().Run(system.Name, system.SpiceNetlist, "op");
                var worst = 0.0;
                for (var i = 0; i < system.Dimension; i++)
                {
                    var expected = raw.Get(system.SpiceVariables![i]);
                    var scale = Math.Max(1e-6, Math.Abs(expected));
                    worst = Math.Max(worst, Math.Abs(expected - x[i]) / scale);
                }
                Report(ref failures, worst < 1e-3, $"ngspice oracle {system.Name}: worst rel err {worst:E2}");
            }
        }

        // Every contestant: residual, agreement with referee, allocation.
        foreach (var factory in Registry.Factories)
        {
            var name = factory().Name;
            if (name == "naive-dense") continue;

            foreach (var system in systems)
            {
                var backend = factory();
                try
                {
                    backend.Analyze(system.Dimension, system.Pattern);
                }
                catch (NotSupportedException)
                {
                    Console.WriteLine($"  skip  {name} on {system.Name} (opted out)");
                    continue;
                }

                backend.Factorize(system.Values);
                var x = new double[system.Dimension];
                backend.Solve(system.Rhs, x);

                var residual = system.ResidualInfNorm(x);
                Report(ref failures, residual < Tolerance(system), $"{name} residual {system.Name}: {residual:E2}");

                var reference_ = reference[system.Name];
                var worst = 0.0;
                for (var i = 0; i < system.Dimension; i++)
                    worst = Math.Max(worst, Math.Abs(x[i] - reference_[i]) / Math.Max(1e-9, Math.Abs(reference_[i])));
                Report(ref failures, worst < 1e-6, $"{name} vs referee {system.Name}: worst rel diff {worst:E2}");

                // Zero-allocation gate on the tier-1/2 paths (post-warmup).
                backend.Factorize(system.Values);
                backend.Solve(system.Rhs, x);
                var before = GC.GetAllocatedBytesForCurrentThread();
                for (var r = 0; r < 20; r++)
                {
                    backend.Factorize(system.Values);
                    for (var s = 0; s < 5; s++)
                        backend.Solve(system.Rhs, x);
                }
                var allocated = GC.GetAllocatedBytesForCurrentThread() - before;
                // csparse's 8n+24 B/refactor is a documented library limitation
                // (workspace int[2n] inside SparseLU) — report, don't fail.
                if (name == "csparse")
                    Console.WriteLine($"  info  {name} alloc {system.Name}: {allocated} B over 20 refactor + 100 solve (known library allocation)");
                else
                    Report(ref failures, allocated == 0, $"{name} alloc {system.Name}: {allocated} B over 20 refactor + 100 solve");
            }
        }

        Console.WriteLine(failures == 0 ? "\nVERIFY PASS" : $"\nVERIFY FAIL ({failures} failures)");
        return failures == 0 ? 0 : 1;
    }

    private static double Tolerance(LinearSystem system) =>
        1e-8 * Math.Max(1.0, system.Rhs.Max(Math.Abs));

    private static void Report(ref int failures, bool ok, string message)
    {
        if (!ok) failures++;
        Console.WriteLine($"  {(ok ? " ok " : "FAIL")}  {message}");
    }
}
