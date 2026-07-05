namespace Manatee.SolverBench.Backends;

/// <summary>
/// Bandwidth-oriented contestant: reverse Cuthill-McKee ordering at Analyze
/// time, then banded LU with partial pivoting in LAPACK-style band storage
/// (dgbtf2/dgbtrs, unblocked). Storage has 2*kl+ku+1 rows per column so the
/// fill produced by row interchanges stays inside the band.
///
/// Hypothesis under test: post-compaction game circuits are nearly
/// one-dimensional, so O(n*b^2) factorization beats general sparse. Systems
/// whose RCM bandwidth exceeds MaxBandwidth are refused (NotSupportedException)
/// — meshy/expander topologies belong to other solvers.
///
/// Family-B note: voltage-source branch rows are ordinary vertices to RCM and
/// carry structurally zero diagonals; partial pivoting within the band handles
/// them (the pivot row is always within kl of the diagonal, so the interchange
/// stays representable).
/// </summary>
public sealed class BandedLuBackend : ISolverBackend
{
    private const int MaxBandwidth = 200;

    private int _n;
    private int _kl;        // lower bandwidth after RCM
    private int _ku;        // upper bandwidth after RCM
    private int _ldab;      // 2*kl + ku + 1 rows per column (fill space included)
    private int[] _perm = [];      // old index -> new (RCM) index
    private int[] _bandIndex = []; // pattern slot k -> flat index into _ab
    private double[] _ab = [];     // column-major band storage, ldab x n
    private int[] _ipiv = [];
    private double[] _work = [];   // permuted rhs / solution scratch

    public string Name => "banded-rcm";

    public void Analyze(int dimension, ReadOnlyMemory<MatrixEntry> pattern)
    {
        var n = dimension;
        var entries = pattern.Span;

        // ---- Build symmetrized, deduplicated adjacency (CSR) ----------------
        // MNA patterns are structurally symmetric, but symmetrize anyway; a
        // marker pass removes the duplicates this creates.
        var rawCount = new int[n];
        for (var k = 0; k < entries.Length; k++)
        {
            var e = entries[k];
            if (e.Row == e.Column) continue;
            rawCount[e.Row]++;
            rawCount[e.Column]++;
        }
        var rawStart = new int[n + 1];
        for (var i = 0; i < n; i++) rawStart[i + 1] = rawStart[i] + rawCount[i];
        var rawAdj = new int[rawStart[n]];
        var cursor = new int[n];
        rawStart.AsSpan(0, n).CopyTo(cursor);
        for (var k = 0; k < entries.Length; k++)
        {
            var e = entries[k];
            if (e.Row == e.Column) continue;
            rawAdj[cursor[e.Row]++] = e.Column;
            rawAdj[cursor[e.Column]++] = e.Row;
        }
        // Dedupe per node with a lastSeen marker.
        var adjStart = new int[n + 1];
        var adj = new int[rawAdj.Length];
        var lastSeen = new int[n];
        Array.Fill(lastSeen, -1);
        var w = 0;
        for (var i = 0; i < n; i++)
        {
            adjStart[i] = w;
            for (var p = rawStart[i]; p < rawStart[i + 1]; p++)
            {
                var v = rawAdj[p];
                if (lastSeen[v] == i) continue;
                lastSeen[v] = i;
                adj[w++] = v;
            }
        }
        adjStart[n] = w;
        var degree = new int[n];
        for (var i = 0; i < n; i++) degree[i] = adjStart[i + 1] - adjStart[i];

        // ---- Reverse Cuthill-McKee ------------------------------------------
        var order = new int[n];      // Cuthill-McKee visit order (pre-reversal)
        var visited = new bool[n];
        var level = new int[n];      // scratch for pseudo-peripheral BFS
        var queue = new int[n];
        var count = 0;

        while (count < n)
        {
            // Component start: unvisited node of minimum degree.
            var root = -1;
            for (var i = 0; i < n; i++)
            {
                if (visited[i]) continue;
                if (root < 0 || degree[i] < degree[root]) root = i;
            }

            // Pseudo-peripheral refinement: walk to a min-degree node of the
            // deepest BFS level until eccentricity stops growing.
            var ecc = -1;
            for (var attempt = 0; attempt < 8; attempt++)
            {
                var (newEcc, lastLevelStart, size) = Bfs(root, adjStart, adj, visited, level, queue);
                if (newEcc <= ecc) break;
                ecc = newEcc;
                var candidate = queue[lastLevelStart];
                for (var p = lastLevelStart; p < size; p++)
                    if (degree[queue[p]] < degree[candidate]) candidate = queue[p];
                root = candidate;
            }

            // Cuthill-McKee BFS: neighbors appended in ascending-degree order.
            var head = count;
            order[count++] = root;
            visited[root] = true;
            while (head < count)
            {
                var u = order[head++];
                var before = count;
                for (var p = adjStart[u]; p < adjStart[u + 1]; p++)
                {
                    var v = adj[p];
                    if (visited[v]) continue;
                    visited[v] = true;
                    order[count++] = v;
                }
                // Insertion sort the freshly appended slice by degree.
                for (var a = before + 1; a < count; a++)
                {
                    var node = order[a];
                    var b = a - 1;
                    while (b >= before && degree[order[b]] > degree[node])
                    {
                        order[b + 1] = order[b];
                        b--;
                    }
                    order[b + 1] = node;
                }
            }
        }

        // Reverse: perm[old] = new position.
        var perm = new int[n];
        for (var p = 0; p < n; p++)
            perm[order[p]] = n - 1 - p;

        // ---- Bandwidth under the permutation --------------------------------
        int kl = 0, ku = 0;
        for (var k = 0; k < entries.Length; k++)
        {
            var d = perm[entries[k].Row] - perm[entries[k].Column];
            if (d > kl) kl = d;
            if (-d > ku) ku = -d;
        }
        if (Math.Max(kl, ku) > MaxBandwidth)
            throw new NotSupportedException(
                $"{Name}: RCM bandwidth {Math.Max(kl, ku)} exceeds {MaxBandwidth}; this system belongs to a general sparse solver.");

        // ---- Band storage + precomputed scatter targets ----------------------
        var ldab = 2 * kl + ku + 1;
        var kv = kl + ku; // row offset of the diagonal within a band column
        var bandIndex = new int[entries.Length];
        for (var k = 0; k < entries.Length; k++)
        {
            var i = perm[entries[k].Row];
            var j = perm[entries[k].Column];
            bandIndex[k] = j * ldab + (kv + i - j);
        }

        _n = n;
        _kl = kl;
        _ku = ku;
        _ldab = ldab;
        _perm = perm;
        _bandIndex = bandIndex;
        _ab = new double[(long)ldab * n <= int.MaxValue ? ldab * n : throw new NotSupportedException($"{Name}: band storage too large.")];
        _ipiv = new int[n];
        _work = new double[n];
    }

    /// <summary>
    /// BFS over one component. Returns (eccentricity, index in queue where the
    /// last level starts, nodes reached). Uses <paramref name="visited"/> as a
    /// read-only component mask via <paramref name="level"/> scratch.
    /// </summary>
    private static (int Ecc, int LastLevelStart, int Size) Bfs(
        int root, int[] adjStart, int[] adj, bool[] visited, int[] level, int[] queue)
    {
        // level doubles as the "seen this BFS" marker: -1 = unseen.
        // Only reset entries we touched last time by tracking via queue reuse:
        // simplest correct approach is a full reset of reachable-from-visited
        // nodes; Analyze-time cost is acceptable.
        Array.Fill(level, -1);
        var size = 0;
        queue[size++] = root;
        level[root] = 0;
        var head = 0;
        var lastLevelStart = 0;
        var ecc = 0;
        while (head < size)
        {
            var u = queue[head++];
            var lu = level[u];
            for (var p = adjStart[u]; p < adjStart[u + 1]; p++)
            {
                var v = adj[p];
                if (level[v] >= 0 || visited[v]) continue;
                level[v] = lu + 1;
                if (lu + 1 > ecc)
                {
                    ecc = lu + 1;
                    lastLevelStart = size;
                }
                queue[size++] = v;
            }
        }
        return (ecc, lastLevelStart, size);
    }

    public void Factorize(ReadOnlySpan<double> values)
    {
        var n = _n;
        var kl = _kl;
        var ku = _ku;
        var kv = kl + ku;
        var ldab = _ldab;
        var ab = _ab;
        var ipiv = _ipiv;
        var bandIndex = _bandIndex;

        Array.Clear(ab);
        for (var k = 0; k < bandIndex.Length; k++)
            ab[bandIndex[k]] += values[k];

        // Unblocked banded LU with partial pivoting (LAPACK dgbtf2 layout:
        // A[i,j] lives at ab[j*ldab + kv + i - j]; rows 0..kl-1 of each column
        // are fill space for pivoting-induced upper-band growth).
        var ju = 0; // last column touched by interchanges so far
        for (var j = 0; j < n; j++)
        {
            var colBase = j * ldab + kv;
            var km = Math.Min(kl, n - 1 - j); // subdiagonal count in column j

            // Partial pivot among rows j..j+km.
            var jp = 0;
            var max = Math.Abs(ab[colBase]);
            for (var i = 1; i <= km; i++)
            {
                var v = Math.Abs(ab[colBase + i]);
                if (v > max) { max = v; jp = i; }
            }
            ipiv[j] = j + jp;
            if (max == 0.0)
                throw new InvalidOperationException($"{Name}: singular at column {j}.");

            ju = Math.Max(ju, Math.Min(j + ku + jp, n - 1));

            if (jp != 0)
            {
                // Swap rows j and j+jp across columns j..ju. Row r of column c
                // sits at ab[c*ldab + kv + r - c]; both stay in [0, ldab).
                for (var c = j; c <= ju; c++)
                {
                    var ia = c * ldab + kv + j - c;
                    var ib = ia + jp;
                    (ab[ia], ab[ib]) = (ab[ib], ab[ia]);
                }
            }

            if (km > 0)
            {
                var inv = 1.0 / ab[colBase];
                for (var i = 1; i <= km; i++)
                    ab[colBase + i] *= inv;

                // Rank-1 update on the trailing band: for each column c in
                // (j, ju], subtract L(:,j) * U(j,c).
                for (var c = j + 1; c <= ju; c++)
                {
                    var uBase = c * ldab + kv + j - c; // U(j,c)
                    var f = ab[uBase];
                    if (f == 0.0) continue;
                    for (var i = 1; i <= km; i++)
                        ab[uBase + i] -= ab[colBase + i] * f;
                }
            }
        }
    }

    public void Solve(ReadOnlySpan<double> rhs, Span<double> solution)
    {
        var n = _n;
        var kl = _kl;
        var kv = kl + _ku;
        var ldab = _ldab;
        var ab = _ab;
        var ipiv = _ipiv;
        var perm = _perm;
        var x = _work;

        // Permute the RHS into RCM order: B = P A P^T, solve B (Px) = P b.
        for (var i = 0; i < n; i++)
            x[perm[i]] = rhs[i];

        // Forward: apply interchanges and L (unit diagonal, band kl).
        for (var j = 0; j < n; j++)
        {
            var p = ipiv[j];
            if (p != j)
                (x[j], x[p]) = (x[p], x[j]);
            var xj = x[j];
            if (xj == 0.0) continue;
            var colBase = j * ldab + kv;
            var km = Math.Min(kl, n - 1 - j);
            for (var i = 1; i <= km; i++)
                x[j + i] -= ab[colBase + i] * xj;
        }

        // Back: U has upper bandwidth kl+ku after pivoting.
        for (var j = n - 1; j >= 0; j--)
        {
            var colBase = j * ldab + kv;
            var xj = x[j] / ab[colBase];
            x[j] = xj;
            if (xj == 0.0) continue;
            var top = Math.Max(0, j - kv);
            for (var i = top; i < j; i++)
                x[i] -= ab[colBase + i - j] * xj;
        }

        // Un-permute.
        for (var i = 0; i < n; i++)
            solution[i] = x[perm[i]];
    }
}
