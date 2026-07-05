using BenchmarkDotNet.Attributes;

namespace Manatee.Benchmarks;

/// <summary>
/// Placeholder keeping the benchmark pipeline runnable until the solver
/// exists. The standing suites (ladder DC, switch-toggle, bulk build,
/// AC substep, zero-allocation assertion) replace this — testing-strategy.md.
/// </summary>
[MemoryDiagnoser]
public class PlaceholderBenchmarks
{
    private readonly double[] _values = Enumerable.Range(0, 1024).Select(i => (double)i).ToArray();

    [Benchmark]
    public double Sum()
    {
        double sum = 0;
        foreach (var value in _values)
            sum += value;
        return sum;
    }
}
