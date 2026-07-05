using System.Globalization;
using BenchmarkDotNet.Running;
using Manatee.Oracle;
using Manatee.SolverBench;

if (args.Length > 0 && args[0] == "verify")
    return Verifier.Run(oracle: Environment.GetEnvironmentVariable("MANATEE_ORACLE") != "0");

BenchmarkSwitcher.FromAssembly(typeof(Registry).Assembly).Run(args);
return 0;

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
