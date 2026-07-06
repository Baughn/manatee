using Manatee.Core.Solver;

namespace Manatee.Core.Tests.Solver;

/// <summary>
/// The sparse backend freezes its pivot order at the first Factorize (KLU-style).
/// This is the ported stress mode from <c>experiments/solver-bench/</c>: hold one
/// pattern, then refactorize repeatedly with conductances redrawn across the full
/// legal range (a maximal tier-2 swing). The frozen order must stay numerically
/// sound — either because refactorization succeeds, or because the growth monitor
/// falls back to a fresh re-pivot — and the result must match a freshly-analyzed
/// backend on the same values.
/// </summary>
public sealed class FrozenPivotStressTests
{
    [Fact]
    public void Frozen_pivots_stay_sound_across_full_range_redraws()
    {
        const int rounds = 60;
        var proto = SystemFixtures.LadderExtreme(500, seed: 0);

        var frozen = new SparseLuBackend();
        frozen.Analyze(proto.Dimension, proto.Pattern);
        frozen.Factorize(proto.Values);

        var xFrozen = new double[proto.Dimension];
        var xFresh = new double[proto.Dimension];
        double worstResidual = 0, worstDisagree = 0;

        for (var round = 1; round <= rounds; round++)
        {
            var system = SystemFixtures.LadderExtreme(500, seed: round);
            // The generator must produce an identical pattern for every seed —
            // otherwise the "one frozen pattern, swinging values" premise is void.
            Assert.True(system.Pattern.AsSpan().SequenceEqual(proto.Pattern),
                $"round {round}: pattern drifted between seeds — generator bug");

            frozen.Factorize(system.Values);   // frozen refactor, or monitored fallback
            frozen.Solve(system.Rhs, xFrozen);
            var residual = system.ResidualInfNorm(xFrozen) / system.Scale();
            worstResidual = Math.Max(worstResidual, residual);

            // A freshly-analyzed backend re-pivots for these exact values.
            var fresh = new SparseLuBackend();
            fresh.Analyze(system.Dimension, system.Pattern);
            fresh.Factorize(system.Values);
            fresh.Solve(system.Rhs, xFresh);

            worstDisagree = Math.Max(worstDisagree, SolverTestHarness.ScaledMaxDiff(xFrozen, xFresh));
        }

        Assert.True(worstResidual < 1e-6, $"worst frozen scaled residual {worstResidual:E2} ≥ 1e-6");
        Assert.True(worstDisagree < 1e-6, $"worst frozen-vs-fresh scaled diff {worstDisagree:E2} ≥ 1e-6");
    }
}
