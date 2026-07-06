namespace Manatee.Core.Solver;

/// <summary>One coordinate of a sparse matrix pattern. Patterns contain no
/// duplicates. Domain-neutral by design (Row/Column, not node/branch): the
/// bottom layer solves A·x = b and knows nothing of potentials or flows.</summary>
internal readonly record struct MatrixEntry(int Row, int Column);

/// <summary>
/// The internal linear-solver bottom layer for MNA systems A·x = b. Proven in
/// <c>experiments/solver-bench/</c> (backend competition, 2026-07-05) and ported
/// here as the production contract.
///
/// The lifecycle mirrors solver.md's change-cost tiers:
///   Analyze   — once per topology (tier 3). May allocate and precompute freely.
///   Factorize — per conductance change (tier 2). MUST NOT allocate after warmup.
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
///   • A genuinely singular system (after gmin, at the caller) throws
///     <see cref="System.InvalidOperationException"/> from Factorize. The
///     island layer catches that and enters its Faulted state (solver.md,
///     Failure Handling); the backend never emits a non-finite solution.
/// </summary>
internal interface ISolverBackend
{
    /// <summary>Human-readable backend name (diagnostics, benchmark labels).</summary>
    string Name { get; }

    /// <summary>Tier 3. Symbolic phase: fixes dimension and pattern, computes a
    /// fill-reducing ordering, and allocates all fixed-size workspaces.</summary>
    void Analyze(int dimension, ReadOnlyMemory<MatrixEntry> pattern);

    /// <summary>Tier 2. Numeric factorization for a new value vector on the frozen
    /// pattern. Zero-allocation after warmup (the first call may allocate).</summary>
    void Factorize(ReadOnlySpan<double> values);

    /// <summary>Tier 1. Forward/back substitution on the cached factors.
    /// Zero-allocation.</summary>
    void Solve(ReadOnlySpan<double> rhs, Span<double> solution);

    /// <summary>Original matrix column the most recent <see cref="Factorize"/>
    /// failed to pivot, set immediately before it threw; −1 when no
    /// factorization has thrown. The island layer reads this to localize a
    /// singular fault to a row (→ node/component) instead of parsing the
    /// exception message (solver.md, Failure Handling).</summary>
    int LastSingularColumn { get; }
}
