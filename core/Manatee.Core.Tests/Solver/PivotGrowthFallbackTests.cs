using Manatee.Core.Solver;

namespace Manatee.Core.Tests.Solver;

/// <summary>
/// The pivot-growth monitor (solver.md Numerics): when a tier-2 value swing
/// collapses a frozen pivot, the refactorization must abandon the frozen order
/// and re-run a full partial-pivoting factorization from scratch rather than
/// divide by the dead pivot. A genuinely singular refactor still throws.
///
/// The fixtures here are hand-built 2×2 matrices — not circuit generators —
/// because the trigger has to be reasoned about exactly. Pattern is the dense
/// 2×2 in row-major order [(0,0),(0,1),(1,0),(1,1)].
/// </summary>
public sealed class PivotGrowthFallbackTests
{
    private static readonly MatrixEntry[] Dense2x2 =
        [new(0, 0), new(0, 1), new(1, 0), new(1, 1)];

    [Fact]
    public void Fallback_re_pivots_when_a_frozen_pivot_collapses_to_zero()
    {
        var backend = new SparseLuBackend();
        backend.Analyze(2, Dense2x2);

        // First: diagonally dominant ⇒ the frozen pivot order picks the diagonal.
        //   [[1, 1], [1e-9, 1]]
        backend.Factorize([1.0, 1.0, 1e-9, 1.0]);
        Assert.Equal(1, backend.FullFactorizations);

        // Swing to the anti-diagonal ⇒ BOTH frozen diagonal pivots are now zero,
        // but the matrix [[0,1],[1,0]] is nonsingular. The monitor must fall back.
        var rhs = new double[] { 3.0, 5.0 };
        var x = new double[2];
        backend.Factorize([0.0, 1.0, 1.0, 0.0]);
        backend.Solve(rhs, x);

        Assert.Equal(2, backend.FullFactorizations); // one fresh re-pivot happened
        // [[0,1],[1,0]]·x = [3,5] ⇒ x = [5, 3].
        Assert.Equal(5.0, x[0], 12);
        Assert.Equal(3.0, x[1], 12);
    }

    [Fact]
    public void Fallback_still_throws_on_a_genuinely_singular_refactor()
    {
        var backend = new SparseLuBackend();
        backend.Analyze(2, Dense2x2);
        backend.Factorize([1.0, 1.0, 1e-9, 1.0]);   // clean freeze

        // [[1,1],[1,1]] is singular. The frozen refactor collapses a pivot, the
        // monitor falls back, and the from-scratch pass reports the singularity.
        Assert.Throws<InvalidOperationException>(() =>
            backend.Factorize([1.0, 1.0, 1.0, 1.0]));
    }

    [Fact]
    public void A_benign_refactor_stays_on_the_frozen_path()
    {
        var backend = new SparseLuBackend();
        backend.Analyze(2, Dense2x2);
        backend.Factorize([2.0, 1.0, 1e-9, 2.0]);
        Assert.Equal(1, backend.FullFactorizations);

        // A mild value change keeps the frozen order sound: no fresh factorization.
        var x = new double[2];
        backend.Factorize([3.0, 1.0, 1e-9, 3.0]);
        backend.Solve([1.0, 0.0], x);
        Assert.Equal(1, backend.FullFactorizations);
    }
}
