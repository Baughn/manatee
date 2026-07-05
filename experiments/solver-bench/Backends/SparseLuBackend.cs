namespace Manatee.SolverBench.Backends;

/// <summary>
/// In-house sparse LU tuned for circuit matrices (KLU-lineage design).
///
/// Analyze:   builds CSC of A, computes a fill-reducing column ordering
///            (plain minimum degree on the pattern of A+Aᵀ), and allocates
///            all fixed-size workspaces.
/// Factorize: the FIRST call runs Gilbert–Peierls left-looking LU with
///            partial pivoting — this discovers the fill pattern of L/U and
///            fixes the row permutation (handles the structurally-zero
///            diagonals of voltage-source branch rows without a separate
///            transversal). It may allocate (growing L/U storage), which the
///            contract permits for symbolic work. Every SUBSEQUENT call is a
///            zero-allocation numeric refactorization on the frozen pattern
///            and pivot order.
/// Solve:     zero-allocation permuted forward/back substitution.
///
/// Factored form: P·A·Q = L·U with L unit-lower (diagonal implicit) and U
/// stored as strict upper part + separate diagonal.
/// </summary>
public sealed class SparseLuBackend : ISolverBackend
{
    public string Name => "sparse-lu";

    private int _n;

    // A in CSC over ORIGINAL indices. _apat maps each CSC slot back to its
    // index in the pattern/values array handed to Factorize.
    private int[] _ap = [];
    private int[] _ai = [];
    private int[] _apat = [];

    private int[] _q = [];    // column permutation: new position j -> original column
    private int[] _pinv = []; // original row -> pivot position (assigned on first Factorize)

    // L (unit diagonal implicit) and U (strictly upper) in CSC by pivot
    // column. Row indices are ORIGINAL during the first factorization, then
    // converted to permuted positions. U columns are sorted ascending (the
    // refactorization pass depends on that); Udiag holds the pivots.
    private int[] _lp = [];
    private int[] _li = [];
    private double[] _lx = [];
    private int[] _up = [];
    private int[] _ui = [];
    private double[] _ux = [];
    private double[] _udiag = [];

    // Workspaces (allocated in Analyze, reused everywhere).
    private double[] _x = [];
    private int[] _visited = [];
    private int[] _nstack = [];
    private int[] _estack = [];
    private int[] _topo = [];

    private bool _numericReady;

    public void Analyze(int dimension, ReadOnlyMemory<MatrixEntry> pattern)
    {
        var n = dimension;
        _n = n;
        var pat = pattern.Span;
        var nnz = pat.Length;

        // ---- CSC of A (counting sort by column) ----
        _ap = new int[n + 1];
        _ai = new int[nnz];
        _apat = new int[nnz];
        var count = new int[n];
        for (var k = 0; k < nnz; k++)
            count[pat[k].Column]++;
        var sum = 0;
        for (var c = 0; c < n; c++)
        {
            _ap[c] = sum;
            sum += count[c];
            count[c] = _ap[c]; // reuse as fill cursor
        }
        _ap[n] = sum;
        for (var k = 0; k < nnz; k++)
        {
            var p = count[pat[k].Column]++;
            _ai[p] = pat[k].Row;
            _apat[p] = k;
        }

        // ---- Fill-reducing column ordering ----
        _q = MinimumDegreeOrder(n, pat);

        // ---- Workspaces and factor skeletons ----
        _pinv = new int[n];
        _lp = new int[n + 1];
        _up = new int[n + 1];
        _udiag = new double[n];
        _x = new double[n];
        _visited = new int[n];
        _nstack = new int[n];
        _estack = new int[n];
        _topo = new int[n];
        var cap = Math.Max(16, 4 * nnz);
        _li = new int[cap];
        _lx = new double[cap];
        _ui = new int[cap];
        _ux = new double[cap];
        _numericReady = false;
    }

    public void Factorize(ReadOnlySpan<double> values)
    {
        if (_numericReady)
            Refactorize(values);
        else
            FactorizeFirst(values);
    }

    public void Solve(ReadOnlySpan<double> rhs, Span<double> solution)
    {
        var n = _n;
        var w = _x;
        var pinv = _pinv;
        for (var i = 0; i < n; i++)
            w[pinv[i]] = rhs[i];

        // Forward: L y = P b (unit diagonal).
        var lp = _lp; var li = _li; var lx = _lx;
        for (var j = 0; j < n; j++)
        {
            var yj = w[j];
            if (yj == 0.0) continue;
            var end = lp[j + 1];
            for (var p = lp[j]; p < end; p++)
                w[li[p]] -= lx[p] * yj;
        }
        // Back: U z = y.
        var up = _up; var ui = _ui; var ux = _ux; var ud = _udiag;
        for (var j = n - 1; j >= 0; j--)
        {
            var zj = w[j] / ud[j];
            w[j] = zj;
            if (zj == 0.0) continue;
            var end = up[j + 1];
            for (var p = up[j]; p < end; p++)
                w[ui[p]] -= ux[p] * zj;
        }
        // x = Q z.
        var q = _q;
        for (var j = 0; j < n; j++)
            solution[q[j]] = w[j];
    }

    // ---------------------------------------------------------------- first
    // Gilbert–Peierls left-looking LU with partial pivoting. May allocate
    // (L/U storage growth). Fixes _pinv and the L/U patterns for all later
    // refactorizations.
    private void FactorizeFirst(ReadOnlySpan<double> values)
    {
        var n = _n;
        Array.Fill(_pinv, -1);
        Array.Fill(_visited, -1);
        var lnz = 0;
        var unz = 0;

        for (var j = 0; j < n; j++)
        {
            _lp[j] = lnz;
            _up[j] = unz;
            var col = _q[j];

            // Reach of A(:, q[j]) in the graph of the partial L, in reverse
            // postorder (topological for the triangular solve).
            var top = n;
            var aEnd = _ap[col + 1];
            for (var p = _ap[col]; p < aEnd; p++)
            {
                var i = _ai[p];
                if (_visited[i] != j)
                    top = Dfs(i, j, top);
            }

            // Clear reach slots, then scatter the column of A.
            for (var t = top; t < n; t++)
                _x[_topo[t]] = 0.0;
            for (var p = _ap[col]; p < aEnd; p++)
                _x[_ai[p]] += values[_apat[p]];

            // Sparse lower-triangular solve in topological order.
            for (var t = top; t < n; t++)
            {
                var i = _topo[t];
                var k = _pinv[i];
                if (k < 0) continue; // not yet pivotal: an L candidate
                var xi = _x[i];
                if (xi == 0.0) continue;
                var end = _lp[k + 1];
                for (var p = _lp[k]; p < end; p++)
                    _x[_li[p]] -= _lx[p] * xi;
            }

            // Partition into U entries (already-pivotal rows) and pivot
            // candidates; choose the largest-magnitude candidate.
            var ipiv = -1;
            var pmax = -1.0;
            for (var t = top; t < n; t++)
            {
                var i = _topo[t];
                var k = _pinv[i];
                if (k >= 0)
                {
                    if (unz == _ui.Length) GrowU();
                    _ui[unz] = k;
                    _ux[unz] = _x[i];
                    unz++;
                }
                else
                {
                    var a = Math.Abs(_x[i]);
                    if (a > pmax) { pmax = a; ipiv = i; }
                }
            }
            if (ipiv < 0 || pmax == 0.0)
                throw new InvalidOperationException($"{Name}: singular at column {j}.");

            _pinv[ipiv] = j;
            var pivot = _x[ipiv];
            _udiag[j] = pivot;

            for (var t = top; t < n; t++)
            {
                var i = _topo[t];
                if (_pinv[i] >= 0) continue; // pivotal rows (incl. ipiv) are done
                if (lnz == _li.Length) GrowL();
                _li[lnz] = i; // original index; converted below
                _lx[lnz] = _x[i] / pivot;
                lnz++;
            }

            // Refactorization requires U columns sorted by pivot position.
            Array.Sort(_ui, _ux, _up[j], unz - _up[j]);
        }
        _lp[n] = lnz;
        _up[n] = unz;

        // Convert L row indices to permuted positions and sort columns.
        for (var p = 0; p < lnz; p++)
            _li[p] = _pinv[_li[p]];
        for (var j = 0; j < n; j++)
            Array.Sort(_li, _lx, _lp[j], _lp[j + 1] - _lp[j]);

        _numericReady = true;
    }

    /// <summary>Iterative DFS over the partial-L graph (cs_dfs style). Nodes are
    /// original row indices; a pivotal row continues through its L column.
    /// Emits reverse postorder into _topo[top..n).</summary>
    private int Dfs(int root, int stamp, int top)
    {
        var head = 0;
        _nstack[0] = root;
        while (head >= 0)
        {
            var i = _nstack[head];
            var col = _pinv[i];
            if (_visited[i] != stamp)
            {
                _visited[i] = stamp;
                _estack[head] = col < 0 ? 0 : _lp[col];
            }
            var done = true;
            if (col >= 0)
            {
                var end = _lp[col + 1];
                for (var p = _estack[head]; p < end; p++)
                {
                    var r = _li[p];
                    if (_visited[r] != stamp)
                    {
                        _estack[head] = p + 1;
                        _nstack[++head] = r;
                        done = false;
                        break;
                    }
                }
            }
            if (done)
            {
                head--;
                _topo[--top] = i;
            }
        }
        return top;
    }

    // ---------------------------------------------------------------- refactor
    // Numeric refactorization on the frozen pattern and pivot order.
    // Strictly zero-allocation.
    private void Refactorize(ReadOnlySpan<double> values)
    {
        var n = _n;
        var x = _x;
        var lp = _lp; var li = _li; var lx = _lx;
        var up = _up; var ui = _ui; var ux = _ux;
        var ap = _ap; var ai = _ai; var apat = _apat;
        var pinv = _pinv; var q = _q;

        for (var j = 0; j < n; j++)
        {
            var uStart = up[j];
            var uEnd = up[j + 1];
            var lStart = lp[j];
            var lEnd = lp[j + 1];

            // Clear exactly the pattern slots this column touches.
            for (var p = uStart; p < uEnd; p++) x[ui[p]] = 0.0;
            x[j] = 0.0;
            for (var p = lStart; p < lEnd; p++) x[li[p]] = 0.0;

            // Scatter A(:, q[j]) into permuted row positions.
            var col = q[j];
            var aEnd = ap[col + 1];
            for (var p = ap[col]; p < aEnd; p++)
                x[pinv[ai[p]]] += values[apat[p]];

            // Eliminate: U pattern is sorted ascending, so each x[k] is
            // final when consumed.
            for (var p = uStart; p < uEnd; p++)
            {
                var k = ui[p];
                var xk = x[k];
                ux[p] = xk;
                if (xk == 0.0) continue;
                var end = lp[k + 1];
                for (var pp = lp[k]; pp < end; pp++)
                    x[li[pp]] -= lx[pp] * xk;
            }

            var pivot = x[j];
            if (pivot == 0.0)
                throw new InvalidOperationException($"{Name}: zero pivot at column {j} during refactorization.");
            _udiag[j] = pivot;
            for (var p = lStart; p < lEnd; p++)
                lx[p] = x[li[p]] / pivot;
        }
    }

    private void GrowL()
    {
        var cap = _li.Length * 2;
        Array.Resize(ref _li, cap);
        Array.Resize(ref _lx, cap);
    }

    private void GrowU()
    {
        var cap = _ui.Length * 2;
        Array.Resize(ref _ui, cap);
        Array.Resize(ref _ux, cap);
    }

    // ---------------------------------------------------------------- ordering
    /// <summary>Plain minimum degree on the pattern of A+Aᵀ: eliminate the
    /// minimum-degree vertex, connect its neighborhood into a clique, repeat.
    /// Bucketed degree lists for O(1) selection. Analyze-time only, so the
    /// HashSet churn is acceptable.</summary>
    private static int[] MinimumDegreeOrder(int n, ReadOnlySpan<MatrixEntry> pattern)
    {
        var adj = new HashSet<int>[n];
        for (var i = 0; i < n; i++)
            adj[i] = new HashSet<int>();
        for (var k = 0; k < pattern.Length; k++)
        {
            var r = pattern[k].Row;
            var c = pattern[k].Column;
            if (r == c) continue;
            adj[r].Add(c);
            adj[c].Add(r);
        }

        var deg = new int[n];
        var next = new int[n];
        var prev = new int[n];
        var head = new int[n + 1];
        Array.Fill(head, -1);
        for (var v = n - 1; v >= 0; v--) // reverse insert: ties pop in index order
        {
            var d = adj[v].Count;
            deg[v] = d;
            next[v] = head[d];
            prev[v] = -1;
            if (head[d] >= 0) prev[head[d]] = v;
            head[d] = v;
        }

        var order = new int[n];
        var nbr = new int[n];
        var mind = 0;
        for (var it = 0; it < n; it++)
        {
            while (head[mind] < 0) mind++;
            var v = head[mind];
            head[mind] = next[v];
            if (next[v] >= 0) prev[next[v]] = -1;
            order[it] = v;

            var av = adj[v];
            var m = 0;
            foreach (var u in av)
                nbr[m++] = u;
            for (var a = 0; a < m; a++)
                adj[nbr[a]].Remove(v);
            for (var a = 0; a < m; a++)
            {
                var ua = nbr[a];
                var sa = adj[ua];
                for (var b = a + 1; b < m; b++)
                {
                    var ub = nbr[b];
                    if (sa.Add(ub))
                        adj[ub].Add(ua);
                }
            }
            for (var a = 0; a < m; a++)
            {
                var u = nbr[a];
                var nd = adj[u].Count;
                if (nd == deg[u]) continue;
                var pu = prev[u];
                var nu = next[u];
                if (pu >= 0) next[pu] = nu; else head[deg[u]] = nu;
                if (nu >= 0) prev[nu] = pu;
                deg[u] = nd;
                next[u] = head[nd];
                prev[u] = -1;
                if (head[nd] >= 0) prev[head[nd]] = u;
                head[nd] = u;
                if (nd < mind) mind = nd;
            }
            adj[v] = null!;
        }
        return order;
    }
}
