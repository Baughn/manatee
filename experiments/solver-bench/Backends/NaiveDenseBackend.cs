namespace Manatee.SolverBench.Backends;

/// <summary>
/// The referee: unoptimized dense LU with partial pivoting. Zero-alloc in
/// Factorize/Solve, no cleverness anywhere else. Every contestant must
/// agree with this; every contestant should beat it.
/// </summary>
public sealed class NaiveDenseBackend : ISolverBackend
{
    private const int MaxDimension = 4000; // 128 MB of doubles is the line

    private int _n;
    private MatrixEntry[] _pattern = [];
    private double[] _a = [];   // row-major n×n, becomes LU in place
    private int[] _pivot = [];

    public string Name => "naive-dense";

    public void Analyze(int dimension, ReadOnlyMemory<MatrixEntry> pattern)
    {
        if (dimension > MaxDimension)
            throw new NotSupportedException($"{Name} refuses n={dimension} (> {MaxDimension}).");
        _n = dimension;
        _pattern = pattern.ToArray();
        _a = new double[dimension * dimension];
        _pivot = new int[dimension];
    }

    public void Factorize(ReadOnlySpan<double> values)
    {
        var n = _n;
        var a = _a;
        Array.Clear(a);
        for (var k = 0; k < _pattern.Length; k++)
            a[_pattern[k].Row * n + _pattern[k].Column] += values[k];

        for (var k = 0; k < n; k++)
        {
            // Partial pivot: largest |a[i,k]|, i ≥ k.
            var p = k;
            var max = Math.Abs(a[k * n + k]);
            for (var i = k + 1; i < n; i++)
            {
                var v = Math.Abs(a[i * n + k]);
                if (v > max) { max = v; p = i; }
            }
            if (max == 0.0)
                throw new InvalidOperationException($"{Name}: singular at column {k}.");
            _pivot[k] = p;
            if (p != k)
                for (var j = 0; j < n; j++)
                    (a[k * n + j], a[p * n + j]) = (a[p * n + j], a[k * n + j]);

            var d = a[k * n + k];
            for (var i = k + 1; i < n; i++)
            {
                var m = a[i * n + k] / d;
                a[i * n + k] = m;
                if (m == 0.0) continue;
                for (var j = k + 1; j < n; j++)
                    a[i * n + j] -= m * a[k * n + j];
            }
        }
    }

    public void Solve(ReadOnlySpan<double> rhs, Span<double> solution)
    {
        var n = _n;
        var a = _a;
        rhs.CopyTo(solution);

        for (var k = 0; k < n; k++)
        {
            var p = _pivot[k];
            if (p != k)
                (solution[k], solution[p]) = (solution[p], solution[k]);
        }
        // Forward: L (unit diagonal).
        for (var i = 1; i < n; i++)
        {
            double sum = 0;
            for (var j = 0; j < i; j++)
                sum += a[i * n + j] * solution[j];
            solution[i] -= sum;
        }
        // Back: U.
        for (var i = n - 1; i >= 0; i--)
        {
            double sum = 0;
            for (var j = i + 1; j < n; j++)
                sum += a[i * n + j] * solution[j];
            solution[i] = (solution[i] - sum) / a[i * n + i];
        }
    }
}
