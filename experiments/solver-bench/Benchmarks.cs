using BenchmarkDotNet.Attributes;

namespace Manatee.SolverBench;

public sealed class BenchCase
{
    public required Func<ISolverBackend> Factory { get; init; }
    public required LinearSystem System { get; init; }
    public required string Label { get; init; }
    public override string ToString() => Label;
}

/// <summary>
/// Tier-1 (Solve) and tier-2 (Factorize+Solve) costs per backend × circuit.
/// Solve is the AC-subcycling hot path — the headline number.
/// </summary>
// In-process: the repo tree contains sibling copies of this csproj
// (worktrees, .direnv flake sources) that confuse BDN's project discovery.
[InProcess]
[WarmupCount(3)]
[IterationCount(5)]
[MemoryDiagnoser]
public class SolveBenchmarks
{
    public static IEnumerable<BenchCase> Cases()
    {
        foreach (var system in Circuits.BenchmarkSet())
            foreach (var factory in Registry.Factories)
            {
                var probe = factory();
                try
                {
                    probe.Analyze(system.Dimension, system.Pattern);
                }
                catch (NotSupportedException)
                {
                    continue;
                }
                yield return new BenchCase
                {
                    Factory = factory,
                    System = system,
                    Label = $"{probe.Name}|{system.Name}",
                };
            }
    }

    [ParamsSource(nameof(Cases))]
    public BenchCase Case = null!;

    private ISolverBackend _backend = null!;
    private double[] _solution = null!;

    [GlobalSetup]
    public void Setup()
    {
        _backend = Case.Factory();
        _backend.Analyze(Case.System.Dimension, Case.System.Pattern);
        _backend.Factorize(Case.System.Values);
        _solution = new double[Case.System.Dimension];
        _backend.Solve(Case.System.Rhs, _solution);
    }

    [Benchmark]
    public void Solve() => _backend.Solve(Case.System.Rhs, _solution);

    [Benchmark]
    public void FactorizeSolve()
    {
        _backend.Factorize(Case.System.Values);
        _backend.Solve(Case.System.Rhs, _solution);
    }
}

/// <summary>
/// The Stationeers load-time shape: cold Analyze+Factorize+Solve at 10k
/// (pre-compaction worst case; post-compaction this island is ~100 nodes).
/// </summary>
[InProcess]
[WarmupCount(2)]
[IterationCount(3)]
[MemoryDiagnoser]
public class ColdBuildBenchmarks
{
    public static IEnumerable<BenchCase> Cases()
    {
        var system = Circuits.ColdBuildCase();
        foreach (var factory in Registry.Factories)
        {
            var probe = factory();
            try
            {
                probe.Analyze(system.Dimension, system.Pattern);
            }
            catch (NotSupportedException)
            {
                continue;
            }
            yield return new BenchCase { Factory = factory, System = system, Label = $"{probe.Name}|{system.Name}" };
        }
    }

    [ParamsSource(nameof(Cases))]
    public BenchCase Case = null!;

    [Benchmark]
    public double ColdBuild()
    {
        var backend = Case.Factory();
        backend.Analyze(Case.System.Dimension, Case.System.Pattern);
        backend.Factorize(Case.System.Values);
        var x = new double[Case.System.Dimension];
        backend.Solve(Case.System.Rhs, x);
        return x[0];
    }
}
