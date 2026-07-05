using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Manatee.SolverBench.Backends;

/// <summary>
/// Optimized dense LU with partial pivoting — the same algorithm class as
/// the naive-dense referee, engineered for throughput:
///   • Analyze precomputes flat scatter indices so Factorize assembly is a
///     single indexed pass with no multiplies.
///   • The elimination update (the O(n³) kernel) is row-major/cache-friendly,
///     vectorized with Vector&lt;double&gt;, and blocked four rows at a time so
///     each pivot-row vector load is reused across four target rows.
///   • Blocks whose four multipliers are all exactly zero are skipped — on
///     circuit-shaped (banded/sparse) matrices most of the trailing matrix
///     never sees fill, which turns the dense cube into something much
///     cheaper without changing a single bit of the result.
///   • Both triangular solves run as vectorized dot products over contiguous
///     rows (Solve is the AC-subcycling hot path).
/// Zero allocation in Factorize/Solve; refuses n &gt; 4000.
/// </summary>
public sealed class DenseLuBackend : ISolverBackend
{
    private const int MaxDimension = 4000; // 128 MB of doubles is the line

    private int _n;
    private int[] _scatter = [];  // flat index (row*n + col) per pattern entry
    private double[] _a = [];     // row-major n×n, becomes LU in place
    private int[] _pivot = [];

    public string Name => "dense-opt";

    public void Analyze(int dimension, ReadOnlyMemory<MatrixEntry> pattern)
    {
        if (dimension > MaxDimension)
            throw new NotSupportedException($"{Name} refuses n={dimension} (> {MaxDimension}).");
        _n = dimension;
        var p = pattern.Span;
        _scatter = new int[p.Length];
        for (var k = 0; k < p.Length; k++)
            _scatter[k] = p[k].Row * dimension + p[k].Column;
        _a = new double[dimension * dimension];
        _pivot = new int[dimension];
    }

    public void Factorize(ReadOnlySpan<double> values)
    {
        var n = _n;
        var a = _a;
        var scatter = _scatter;
        var pivot = _pivot;
        Array.Clear(a);
        for (var k = 0; k < scatter.Length; k++)
            a[scatter[k]] += values[k];

        ref var a0 = ref MemoryMarshal.GetArrayDataReference(a);
        var vw = Vector<double>.Count;

        for (var k = 0; k < n; k++)
        {
            var rowK = k * n;

            // Partial pivot: largest |a[i,k]|, i ≥ k (stride-n column scan).
            var p = k;
            var max = Math.Abs(Unsafe.Add(ref a0, rowK + k));
            for (var i = k + 1; i < n; i++)
            {
                var v = Math.Abs(Unsafe.Add(ref a0, i * n + k));
                if (v > max) { max = v; p = i; }
            }
            if (max == 0.0)
                throw new InvalidOperationException($"{Name}: singular at column {k}.");
            pivot[k] = p;

            if (p != k)
            {
                ref var rk = ref Unsafe.Add(ref a0, rowK);
                ref var rp = ref Unsafe.Add(ref a0, p * n);
                var j = 0;
                for (; j + vw <= n; j += vw)
                {
                    var vk = Vector.LoadUnsafe(ref rk, (nuint)j);
                    var vp = Vector.LoadUnsafe(ref rp, (nuint)j);
                    vp.StoreUnsafe(ref rk, (nuint)j);
                    vk.StoreUnsafe(ref rp, (nuint)j);
                }
                for (; j < n; j++)
                    (Unsafe.Add(ref rk, j), Unsafe.Add(ref rp, j)) =
                        (Unsafe.Add(ref rp, j), Unsafe.Add(ref rk, j));
            }

            ref var pk = ref Unsafe.Add(ref a0, rowK);
            var d = Unsafe.Add(ref pk, k);

            // Rank-1 update of the trailing matrix, four rows per pass so the
            // pivot-row vectors are loaded once and reused 4×.
            var i2 = k + 1;
            for (; i2 + 4 <= n; i2 += 4)
            {
                ref var r0 = ref Unsafe.Add(ref a0, i2 * n);
                ref var r1 = ref Unsafe.Add(ref r0, n);
                ref var r2 = ref Unsafe.Add(ref r1, n);
                ref var r3 = ref Unsafe.Add(ref r2, n);

                var m0 = Unsafe.Add(ref r0, k) / d;
                var m1 = Unsafe.Add(ref r1, k) / d;
                var m2 = Unsafe.Add(ref r2, k) / d;
                var m3 = Unsafe.Add(ref r3, k) / d;
                Unsafe.Add(ref r0, k) = m0;
                Unsafe.Add(ref r1, k) = m1;
                Unsafe.Add(ref r2, k) = m2;
                Unsafe.Add(ref r3, k) = m3;
                if ((m0 == 0.0) & (m1 == 0.0) & (m2 == 0.0) & (m3 == 0.0))
                    continue; // no fill from this pivot into these rows

                var vm0 = new Vector<double>(m0);
                var vm1 = new Vector<double>(m1);
                var vm2 = new Vector<double>(m2);
                var vm3 = new Vector<double>(m3);
                var j = k + 1;
                for (; j + vw <= n; j += vw)
                {
                    var vp = Vector.LoadUnsafe(ref pk, (nuint)j);
                    (Vector.LoadUnsafe(ref r0, (nuint)j) - vm0 * vp).StoreUnsafe(ref r0, (nuint)j);
                    (Vector.LoadUnsafe(ref r1, (nuint)j) - vm1 * vp).StoreUnsafe(ref r1, (nuint)j);
                    (Vector.LoadUnsafe(ref r2, (nuint)j) - vm2 * vp).StoreUnsafe(ref r2, (nuint)j);
                    (Vector.LoadUnsafe(ref r3, (nuint)j) - vm3 * vp).StoreUnsafe(ref r3, (nuint)j);
                }
                for (; j < n; j++)
                {
                    var pj = Unsafe.Add(ref pk, j);
                    Unsafe.Add(ref r0, j) -= m0 * pj;
                    Unsafe.Add(ref r1, j) -= m1 * pj;
                    Unsafe.Add(ref r2, j) -= m2 * pj;
                    Unsafe.Add(ref r3, j) -= m3 * pj;
                }
            }
            for (; i2 < n; i2++)
            {
                ref var ri = ref Unsafe.Add(ref a0, i2 * n);
                var m = Unsafe.Add(ref ri, k) / d;
                Unsafe.Add(ref ri, k) = m;
                if (m == 0.0) continue;

                var vm = new Vector<double>(m);
                var j = k + 1;
                for (; j + vw <= n; j += vw)
                {
                    var vp = Vector.LoadUnsafe(ref pk, (nuint)j);
                    (Vector.LoadUnsafe(ref ri, (nuint)j) - vm * vp).StoreUnsafe(ref ri, (nuint)j);
                }
                for (; j < n; j++)
                    Unsafe.Add(ref ri, j) -= m * Unsafe.Add(ref pk, j);
            }
        }
    }

    public void Solve(ReadOnlySpan<double> rhs, Span<double> solution)
    {
        var n = _n;
        var a = _a;
        var pivot = _pivot;
        rhs.CopyTo(solution);

        for (var k = 0; k < n; k++)
        {
            var p = pivot[k];
            if (p != k)
                (solution[k], solution[p]) = (solution[p], solution[k]);
        }

        ref var a0 = ref MemoryMarshal.GetArrayDataReference(a);
        ref var x0 = ref MemoryMarshal.GetReference(solution);
        var vw = Vector<double>.Count;

        // Forward: L (unit diagonal), vectorized dot of row prefix with x.
        for (var i = 1; i < n; i++)
        {
            ref var ri = ref Unsafe.Add(ref a0, i * n);
            var acc = Vector<double>.Zero;
            var j = 0;
            for (; j + vw <= i; j += vw)
                acc += Vector.LoadUnsafe(ref ri, (nuint)j) * Vector.LoadUnsafe(ref x0, (nuint)j);
            var sum = Vector.Sum(acc);
            for (; j < i; j++)
                sum += Unsafe.Add(ref ri, j) * Unsafe.Add(ref x0, j);
            Unsafe.Add(ref x0, i) -= sum;
        }

        // Back: U, vectorized dot of row suffix with x.
        for (var i = n - 1; i >= 0; i--)
        {
            ref var ri = ref Unsafe.Add(ref a0, i * n);
            var acc = Vector<double>.Zero;
            var j = i + 1;
            for (; j + vw <= n; j += vw)
                acc += Vector.LoadUnsafe(ref ri, (nuint)j) * Vector.LoadUnsafe(ref x0, (nuint)j);
            var sum = Vector.Sum(acc);
            for (; j < n; j++)
                sum += Unsafe.Add(ref ri, j) * Unsafe.Add(ref x0, j);
            Unsafe.Add(ref x0, i) = (Unsafe.Add(ref x0, i) - sum) / Unsafe.Add(ref ri, i);
        }
    }
}
