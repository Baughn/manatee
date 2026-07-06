using Manatee.Core.Solver;

namespace Manatee.Core.Tests.Solver;

/// <summary>Shared plumbing: run a backend end-to-end on a <see cref="LinearSystem"/>
/// and measure agreement between two solutions, scaled by the solution magnitude.</summary>
internal static class SolverTestHarness
{
    public static double[] SolveWith(ISolverBackend backend, LinearSystem system)
    {
        backend.Analyze(system.Dimension, system.Pattern);
        backend.Factorize(system.Values);
        var x = new double[system.Dimension];
        backend.Solve(system.Rhs, x);
        return x;
    }

    /// <summary>max_i |a[i] − b[i]| / max(1, ‖b‖∞): a relative-to-magnitude component
    /// difference that does not explode on near-zero components.</summary>
    public static double ScaledMaxDiff(ReadOnlySpan<double> a, ReadOnlySpan<double> b)
    {
        double scale = 1.0;
        for (var i = 0; i < b.Length; i++)
            scale = Math.Max(scale, Math.Abs(b[i]));
        double worst = 0;
        for (var i = 0; i < a.Length; i++)
            worst = Math.Max(worst, Math.Abs(a[i] - b[i]) / scale);
        return worst;
    }
}
