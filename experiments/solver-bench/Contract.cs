namespace Manatee.SolverBench;

/// <summary>One coordinate of a sparse matrix pattern. Patterns contain no duplicates.</summary>
public readonly record struct MatrixEntry(int Row, int Column);

/// <summary>
/// A linear-solver contestant for MNA systems A·x = b.
///
/// The lifecycle mirrors solver.md's change-cost tiers:
///   Analyze   — once per topology (tier 3). May allocate and precompute freely.
///   Factorize — per conductance change (tier 2). MUST NOT allocate.
///   Solve     — per RHS change (tier 1; the AC-subcycling hot path,
///               called ~20–100× per game tick). MUST NOT allocate.
///
/// Contract:
///   • The values passed to Factorize align 1:1 with the pattern given to
///     Analyze (values[k] belongs at pattern[k]).
///   • Structurally zero diagonals occur (voltage-source branch rows).
///     A no-pivot LU will divide by zero on family-B systems — plan for
///     pivoting, or a static permutation chosen during Analyze, or both.
///   • Matrices are nonsingular and unsymmetric in general; family-A
///     systems (no voltage sources) are symmetric positive definite.
///   • dimension ≤ ~10⁴; ≤ ~7 entries per row (circuit-shaped sparsity).
///   • Implementations may reorder rows/columns internally at Analyze time.
///   • Throw NotSupportedException from Analyze to opt out of a case
///     (e.g. a dense backend refusing dimension > 4000); the harness skips.
/// </summary>
public interface ISolverBackend
{
    string Name { get; }
    void Analyze(int dimension, ReadOnlyMemory<MatrixEntry> pattern);
    void Factorize(ReadOnlySpan<double> values);
    void Solve(ReadOnlySpan<double> rhs, Span<double> solution);
}
