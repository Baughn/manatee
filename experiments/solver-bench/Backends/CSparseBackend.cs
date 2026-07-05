using CSparse;
using CSparse.Double;
using CSparse.Double.Factorization;
using CSparse.Ordering;
using CSparse.Storage;

namespace Manatee.SolverBench.Backends;

/// <summary>
/// The design doc's interim large-island fallback, exactly as shipped:
/// CSparse.NET SparseLU with AMD(A+A') fill-reducing ordering and partial
/// pivoting (tol = 1.0, so family-B zero diagonals are handled by row
/// pivoting inside the factorization).
///
/// Lifecycle mapping:
///   Analyze   — build the CCS skeleton (sorted columns), keep a
///               pattern-position → storage-slot map, compute the AMD
///               ordering (pattern-only; values are irrelevant to AMD).
///   Factorize — scatter values through the slot map, then
///               SparseLU.Create on first call / SparseLU.Refactorize after
///               (reuses L/U storage and the symbolic ordering).
///   Solve     — SparseLU.Solve span overload; zero-alloc.
///
/// Known allocation (documented, not worked around): CSparse's private
/// numeric factorization kernel allocates a workspace `new int[2n]` on
/// every (re)factorization — measured at exactly 8n + 24 bytes per
/// Factorize through the public API (e.g. 4024 B at n = 500).
/// Solve is allocation-free. We set
/// CompressedColumnStorage.AutoTrimStorage = false so the L/U arrays are
/// not trimmed after each factorization (trimming would force a
/// reallocating Resize on every subsequent refactorization).
/// </summary>
public sealed class CSparseBackend : ISolverBackend
{
    /// <summary>1.0 = classic partial pivoting; anything laxer risks the
    /// structurally-zero diagonals of voltage-source branch rows.</summary>
    private const double PivotTolerance = 1.0;

    private int _n;
    private SparseMatrix _matrix = null!;
    private double[] _values = [];   // backing store of _matrix.Values
    private int[] _slotOf = [];      // pattern position k → CCS storage slot
    private int[] _ordering = [];    // AMD(A+A') column permutation
    private SparseLU? _lu;

    public string Name => "csparse";

    public void Analyze(int dimension, ReadOnlyMemory<MatrixEntry> pattern)
    {
        // Keep L/U storage stable across Refactorize calls (see class doc).
        CompressedColumnStorage<double>.AutoTrimStorage = false;

        _n = dimension;
        var entries = pattern.Span;
        var nnz = entries.Length;

        // Sort pattern positions by (column, row) to get canonical CCS order.
        var keys = new long[nnz];
        var position = new int[nnz];
        for (var k = 0; k < nnz; k++)
        {
            keys[k] = ((long)entries[k].Column << 32) | (uint)entries[k].Row;
            position[k] = k;
        }
        Array.Sort(keys, position);

        var columnPointers = new int[dimension + 1];
        var rowIndices = new int[nnz];
        _values = new double[nnz];
        _slotOf = new int[nnz];
        for (var slot = 0; slot < nnz; slot++)
        {
            var e = entries[position[slot]];
            rowIndices[slot] = e.Row;
            columnPointers[e.Column + 1]++;
            _slotOf[position[slot]] = slot;
        }
        for (var c = 0; c < dimension; c++)
            columnPointers[c + 1] += columnPointers[c];

        _matrix = new SparseMatrix(dimension, dimension, _values, rowIndices, columnPointers);

        // Fill-reducing ordering depends on the pattern only; do it here so
        // Factorize carries just the numeric work.
        _ordering = AMD.Generate(_matrix, ColumnOrdering.MinimumDegreeAtPlusA);

        _lu = null;
    }

    public void Factorize(ReadOnlySpan<double> values)
    {
        var slotOf = _slotOf;
        var storage = _values;
        for (var k = 0; k < values.Length; k++)
            storage[slotOf[k]] = values[k];

        if (_lu is null)
            _lu = SparseLU.Create(_matrix, _ordering, PivotTolerance);
        else
            _lu.Refactorize(_matrix, PivotTolerance);
    }

    public void Solve(ReadOnlySpan<double> rhs, Span<double> solution)
    {
        _lu!.Solve(rhs, solution);
    }
}
