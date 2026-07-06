using System;
using System.Collections.Generic;

namespace Manatee.Core.Reduction;

// The compaction pass (compaction.md): equipotential region-building (perfect-
// conductor union), series-chain collapse of no-tap runs, and reconciliation of the
// result into the target Netlist through the public API only. Deterministic
// throughout — union-find representatives are min-key (with a protected-junction
// preference so ports/reference rails stay their own node), and every iteration that
// assigns identity or emits an edit runs in sorted key order.
public sealed partial class ConductorGraph
{
    private void Recompact()
    {
        _compactionPasses++;
        _dirty = false;
        _slotMapDirty = true;

        BuildRegions();
        var edges = BuildResistiveEdges(out var adjacency, out var degree);
        var boundary = ComputeBoundary(adjacency, degree, edges);
        var chains = BuildChains(boundary, adjacency);

        // Envelopes before stamping (AddResistor carries the LimitSpec).
        foreach (var c in chains) c.Envelope = ComputeEnvelope(c);

        ReconcileNetlist(chains, boundary);

        _chains = chains;
        _segToChain.Clear();
        for (var i = 0; i < chains.Count; i++)
            foreach (var s in chains[i].Segments) _segToChain[s] = i;

        ReaimProbes();
    }

    // ── Region-building: union perfect conductors; representative = min protected
    // junction, else min junction, in the region. ──
    private void BuildRegions()
    {
        _regionOf.Clear();
        var parent = new Dictionary<JunctionKey, JunctionKey>();

        JunctionKey Find(JunctionKey x)
        {
            while (!parent[x].Equals(x))
            {
                parent[x] = parent[parent[x]];
                x = parent[x];
            }
            return x;
        }
        void Add(JunctionKey x) { if (!parent.ContainsKey(x)) parent[x] = x; }
        void Union(JunctionKey a, JunctionKey b)
        {
            Add(a); Add(b);
            var ra = Find(a); var rb = Find(b);
            if (ra.Equals(rb)) return;
            if (KeyLess(rb, ra)) parent[ra] = rb; else parent[rb] = ra;   // min-key root (structural)
        }

        foreach (var kv in _segs)
        {
            Add(kv.Value.A); Add(kv.Value.B);
            if (kv.Value.Ohms <= 0) Union(kv.Value.A, kv.Value.B);        // perfect conductor
        }
        foreach (var j in _protected) Add(j);

        // Group members by structural root, then pick the protected-preferring rep.
        var members = new Dictionary<JunctionKey, List<JunctionKey>>();
        foreach (var j in parent.Keys)
        {
            var r = Find(j);
            if (!members.TryGetValue(r, out var list)) { list = new List<JunctionKey>(); members[r] = list; }
            list.Add(j);
        }
        foreach (var list in members.Values)
        {
            JunctionKey rep = default; var have = false; JunctionKey repProt = default; var haveProt = false;
            foreach (var m in list)
            {
                if (!have || KeyLess(m, rep)) { rep = m; have = true; }
                if (_protected.Contains(m) && (!haveProt || KeyLess(m, repProt))) { repProt = m; haveProt = true; }
            }
            var chosen = haveProt ? repProt : rep;
            foreach (var m in list) _regionOf[m] = chosen;
        }
    }

    private readonly struct Edge
    {
        public Edge(in SegmentKey seg, in JunctionKey ra, in JunctionKey rb, double r)
        { Seg = seg; RA = ra; RB = rb; R = r; }
        public readonly SegmentKey Seg;
        public readonly JunctionKey RA, RB;
        public readonly double R;
        public JunctionKey Other(in JunctionKey from) => RA.Equals(from) ? RB : RA;
    }

    // Cross-region resistive edges; per-region adjacency (sorted by segment key for
    // deterministic chain walks) and region degree (self-edges excluded).
    private List<Edge> BuildResistiveEdges(
        out Dictionary<JunctionKey, List<Edge>> adjacency, out Dictionary<JunctionKey, int> degree)
    {
        var edges = new List<Edge>();
        adjacency = new Dictionary<JunctionKey, List<Edge>>();
        degree = new Dictionary<JunctionKey, int>();

        // Ensure every region rep appears (isolated protected/reference regions too).
        foreach (var rep in _regionOf.Values)
        {
            if (!adjacency.ContainsKey(rep)) adjacency[rep] = new List<Edge>();
            if (!degree.ContainsKey(rep)) degree[rep] = 0;
        }

        // Deterministic edge order: sort segment keys.
        var segKeys = new List<SegmentKey>(_segs.Keys);
        segKeys.Sort(CompareSeg);
        foreach (var sk in segKeys)
        {
            var s = _segs[sk];
            if (s.Ohms <= 0) continue;                       // perfect conductor: unioned, not an edge
            var ra = _regionOf[s.A]; var rb = _regionOf[s.B];
            if (ra.Equals(rb)) continue;                     // shorted resistor: 0 V, dropped (probe reads region)
            var e = new Edge(sk, ra, rb, s.Ohms);
            edges.Add(e);
            adjacency[ra].Add(e); adjacency[rb].Add(e);
            degree[ra]++; degree[rb]++;
        }
        // Adjacency lists in a deterministic order.
        foreach (var list in adjacency.Values) list.Sort((x, y) => CompareSeg(x.Seg, y.Seg));
        return edges;
    }

    private HashSet<JunctionKey> ComputeBoundary(
        Dictionary<JunctionKey, List<Edge>> adjacency, Dictionary<JunctionKey, int> degree, List<Edge> edges)
    {
        var boundary = new HashSet<JunctionKey>();
        foreach (var rep in adjacency.Keys)
        {
            var deg = degree[rep];
            if (deg != 2 || _protected.Contains(rep)) boundary.Add(rep);
        }

        // A component of all-degree-2, unprotected regions (a pure ring) has no
        // boundary to anchor chains — promote its min-key region so no segment is lost
        // and the walk terminates.
        var parent = new Dictionary<JunctionKey, JunctionKey>();
        JunctionKey Find(JunctionKey x) { while (!parent[x].Equals(x)) { parent[x] = parent[parent[x]]; x = parent[x]; } return x; }
        void Add(JunctionKey x) { if (!parent.ContainsKey(x)) parent[x] = x; }
        foreach (var rep in adjacency.Keys) Add(rep);
        foreach (var e in edges)
        {
            Add(e.RA); Add(e.RB);
            var ra = Find(e.RA); var rb = Find(e.RB);
            if (!ra.Equals(rb)) { if (KeyLess(rb, ra)) parent[ra] = rb; else parent[rb] = ra; }
        }
        var compHasBoundary = new Dictionary<JunctionKey, bool>();
        var compMin = new Dictionary<JunctionKey, JunctionKey>();
        foreach (var rep in adjacency.Keys)
        {
            var root = Find(rep);
            if (!compMin.TryGetValue(root, out var mn) || KeyLess(rep, mn)) compMin[root] = rep;
            if (boundary.Contains(rep)) compHasBoundary[root] = true;
        }
        foreach (var kv in compMin)
            if (!compHasBoundary.TryGetValue(kv.Key, out var has) || !has) boundary.Add(kv.Value);

        return boundary;
    }

    private List<Chain> BuildChains(HashSet<JunctionKey> boundary, Dictionary<JunctionKey, List<Edge>> adjacency)
    {
        var chains = new List<Chain>();
        var visited = new HashSet<SegmentKey>();

        var starts = new List<JunctionKey>(boundary);
        starts.Sort((a, b) => CompareJunction(a, b));

        foreach (var b in starts)
        {
            if (!adjacency.TryGetValue(b, out var incident)) continue;
            foreach (var edge0 in incident)
            {
                if (visited.Contains(edge0.Seg)) continue;
                var ordered = new List<SegmentKey>();
                var prev = b;
                var cur = edge0;
                JunctionKey endC;
                while (true)
                {
                    visited.Add(cur.Seg);
                    ordered.Add(cur.Seg);
                    var other = cur.Other(prev);
                    if (boundary.Contains(other)) { endC = other; break; }
                    // interior degree-2 region: cross to its other edge
                    var next = OtherEdge(adjacency[other], cur.Seg);
                    if (next is null) { endC = other; break; }   // safety
                    prev = other;
                    cur = next.Value;
                }
                chains.Add(FinalizeChain(b, endC, ordered));
            }
        }
        return chains;
    }

    private static Edge? OtherEdge(List<Edge> edges, in SegmentKey exclude)
    {
        foreach (var e in edges) if (!e.Seg.Equals(exclude)) return e;
        return null;
    }

    // Orient the walked segment list into canonical P→Q order (P ≤ Q by region key),
    // then walk the region sequence to derive per-segment direction and cumulative R.
    private Chain FinalizeChain(in JunctionKey b, in JunctionKey c, List<SegmentKey> walked)
    {
        var chain = new Chain();
        JunctionKey p, q;
        if (KeyLess(c, b)) { p = c; q = b; walked.Reverse(); }
        else { p = b; q = c; }

        chain.P = p; chain.Q = q; chain.Loop = p.Equals(q);
        chain.Segments.AddRange(walked);

        var rep = walked[0];
        foreach (var s in walked) if (CompareSeg(s, rep) < 0) rep = s;
        chain.Rep = rep;
        chain.EquivKey = rep.External();

        var n = walked.Count;
        chain.CumRStart = new double[n];
        var cur = p;
        double cum = 0;
        for (var i = 0; i < n; i++)
        {
            var s = _segs[walked[i]];
            var ra = _regionOf[s.A]; var rb = _regionOf[s.B];
            var forward = ra.Equals(cur);                 // entering from the A side ⇒ A→B
            if (!forward && !rb.Equals(cur))
            {
                // Defensive: region sequence broke (should not happen); anchor to A side.
                forward = true;
            }
            chain.Forward.Add(forward);
            chain.CumRStart[i] = cum;
            cum += s.Ohms;
            cur = forward ? rb : ra;
        }
        chain.TotalR = cum;
        return chain;
    }

    private LimitSpec ComputeEnvelope(Chain chain)
    {
        var maxI = double.PositiveInfinity; var hasI = false;
        var meltMin = double.PositiveInfinity; double tauSel = 0; var hasMelt = false;
        var critV = double.PositiveInfinity; var hasV = false;
        var critP = double.PositiveInfinity; var hasP = false;

        foreach (var sk in chain.Segments)
        {
            var s = _segs[sk];
            if (s.Limits.MaxCurrent > 0)
            {
                var eff = s.Limits.MaxCurrent * DerateCurrent(s.AmbientK);
                if (eff < maxI) maxI = eff;
                hasI = true;
            }
            if (s.Limits.Thermal.MeltI2t > 0)
            {
                var em = s.Limits.Thermal.MeltI2t * DerateEnergy(s.AmbientK);
                if (em < meltMin) { meltMin = em; tauSel = s.Limits.Thermal.Tau; }
                hasMelt = true;
            }
            if (s.Limits.MaxVoltage > 0 && s.Ohms > 0)
            {
                var cc = s.Limits.MaxVoltage / s.Ohms;
                if (cc < critV) critV = cc;
                hasV = true;
            }
            if (s.Limits.MaxPower > 0 && s.Ohms > 0)
            {
                var cc = s.Limits.MaxPower / s.Ohms;
                if (cc < critP) critP = cc;
                hasP = true;
            }
        }

        return new LimitSpec(
            hasI ? maxI : 0.0,
            hasV ? chain.TotalR * critV : 0.0,
            hasP ? chain.TotalR * critP : 0.0,
            new I2tParams(hasMelt ? meltMin : 0.0, hasMelt ? tauSel : 0.0));
    }

    private void PushEnvelope(Chain chain)
    {
        chain.Envelope = ComputeEnvelope(chain);
        if (!chain.Loop && _net.TryResolve(chain.EquivKey, out var c))
            _net.Meta.SetLimits(c, chain.Envelope);
    }

    // ── Reconcile the desired compaction into the netlist, diffing against what is
    // already realized so only genuine changes emit edits (a redundant pass is a
    // no-op ⇒ no island rebuild ⇒ client handles survive). ──
    private void ReconcileNetlist(List<Chain> chains, HashSet<JunctionKey> boundary)
    {
        // Desired region nodes (boundary reps). PrePartitioned tags each with its
        // partition and flags reference rails.
        var desired = new Dictionary<ExternalKey, (ulong part, bool isRef)>();
        foreach (var rep in boundary)
        {
            var part = _prePartitioned && _junctionPartition.TryGetValue(rep, out var pv) ? pv : 0UL;
            desired[rep.External()] = (part, _referenceJunctions.Contains(rep));
        }

        // Desired chains (stamped only) and their signatures.
        var wantChains = new Dictionary<ExternalKey, Chain>();
        foreach (var ch in chains)
            if (!ch.Loop) wantChains[ch.EquivKey] = ch;

        // Classify chains: removed / shape-changed (remove+re-add) / envelope-only / added.
        var removeChains = new List<ExternalKey>();
        var addChains = new List<Chain>();
        var envOnly = new List<Chain>();
        foreach (var kv in _liveChains)
        {
            if (!wantChains.TryGetValue(kv.Key, out var ch)) { removeChains.Add(kv.Key); continue; }
            var sig = new ChainSig(ch.P, ch.Q, ch.TotalR, ch.Envelope);
            if (!sig.SameShape(kv.Value)) { removeChains.Add(kv.Key); addChains.Add(ch); }
            else if (!sig.Env.Equals(kv.Value.Env)) envOnly.Add(ch);
        }
        foreach (var kv in wantChains)
            if (!_liveChains.ContainsKey(kv.Key)) addChains.Add(kv.Value);

        var removeNodes = new List<ExternalKey>();
        foreach (var nk in _liveNodeKeys) if (!desired.ContainsKey(nk)) removeNodes.Add(nk);
        var addNodes = new List<ExternalKey>();
        foreach (var kv in desired) if (!_liveNodeKeys.Contains(kv.Key)) addNodes.Add(kv.Key);

        // EDIT 1 — removals (chains whose shape changed / vanished, then now-unused
        // non-port nodes; those are touched only by removed chains ⇒ degree hits 0).
        if (removeChains.Count > 0 || removeNodes.Count > 0)
        {
            removeChains.Sort(CompareExternal);
            removeNodes.Sort(CompareExternal);
            using var e = _net.Edit();
            foreach (var ck in removeChains)
                if (_net.TryResolve(ck, out var c)) e.Remove(new ResistorId(c.Slot, c.Gen, c.Net));
            foreach (var nk in removeNodes)
                if (_net.TryResolveNode(nk, out var n)) e.RemoveNode(n);
            e.Commit();
            foreach (var ck in removeChains) _liveChains.Remove(ck);
            foreach (var nk in removeNodes) _liveNodeKeys.Remove(nk);
        }

        // EDIT 2 — additions (new nodes, then new / re-shaped chains).
        if (addNodes.Count > 0 || addChains.Count > 0)
        {
            addNodes.Sort(CompareExternal);
            addChains.Sort((x, y) => CompareExternal(x.EquivKey, y.EquivKey));
            var nodeIds = new Dictionary<ExternalKey, NodeId>();
            using var e = _net.Edit();
            foreach (var key in addNodes)
            {
                var info = desired[key];
                var nid = e.AddNode(key, info.isRef ? NodeRole.Reference : NodeRole.Internal, new PartitionKey(info.part));
                nodeIds[key] = nid;
                _liveNodeKeys.Add(key);
            }
            foreach (var ch in addChains)
            {
                var np = ResolveDesiredNode(ch.P.External(), nodeIds);
                var nq = ResolveDesiredNode(ch.Q.External(), nodeIds);
                e.AddResistor(np, nq, ch.TotalR, ch.EquivKey, ch.Envelope);
                _liveChains[ch.EquivKey] = new ChainSig(ch.P, ch.Q, ch.TotalR, ch.Envelope);
            }
            e.Commit();
        }

        // Envelope-only changes ride the tier-0 path (no matrix/topology change).
        foreach (var ch in envOnly)
            if (_net.TryResolve(ch.EquivKey, out var c))
            {
                _net.Meta.SetLimits(c, ch.Envelope);
                _liveChains[ch.EquivKey] = new ChainSig(ch.P, ch.Q, ch.TotalR, ch.Envelope);
            }
    }

    private NodeId ResolveDesiredNode(in ExternalKey key, Dictionary<ExternalKey, NodeId> staged)
    {
        if (staged.TryGetValue(key, out var n)) return n;    // just added in this edit
        _net.TryResolveNode(key, out n);                     // persistent live node
        return n;
    }

    private void ReaimProbes()
    {
        foreach (var rec in _probeList)
        {
            if (!_segs.ContainsKey(rec.Seg)) continue;       // segment gone: leave probe as-is (reads stale/0)
            ComputeProbeAim(rec, out var repP, out var repQ, out var t);
            _net.TryResolveNode(repP.External(), out var na);
            _net.TryResolveNode(repQ.External(), out var nb);
            _net.Meta.SetProbeInterpolation(rec.Id, na, nb, t);
        }
    }

    private void ComputeProbeAim(ProbeRec rec, out JunctionKey repP, out JunctionKey repQ, out double t)
    {
        if (_segToChain.TryGetValue(rec.Seg, out var ci))
        {
            var chain = _chains[ci];
            if (chain.Loop)
            {
                repP = repQ = chain.P; t = 0; return;
            }
            var idx = chain.Segments.IndexOf(rec.Seg);
            var segR = _segs[rec.Seg].Ohms;
            var frac = chain.Forward[idx] ? rec.Along : 1.0 - rec.Along;
            var posR = chain.CumRStart[idx] + frac * segR;
            t = chain.TotalR > 0 ? posR / chain.TotalR : 0.0;
            repP = chain.P; repQ = chain.Q;
            return;
        }
        // Shorted segment (both endpoints equipotential): read the region node.
        var s = _segs[rec.Seg];
        repP = repQ = _regionOf.TryGetValue(s.A, out var r) ? r : s.A;
        t = 0;
    }

    // ── Deterministic comparers ──
    private static int CompareExternal(ExternalKey a, ExternalKey b)
        => a.Hi != b.Hi ? a.Hi.CompareTo(b.Hi) : a.Lo.CompareTo(b.Lo);
    private static int CompareSeg(SegmentKey a, SegmentKey b)
        => a.Hi != b.Hi ? a.Hi.CompareTo(b.Hi) : a.Lo.CompareTo(b.Lo);
    private static int CompareJunction(JunctionKey a, JunctionKey b)
        => a.Hi != b.Hi ? a.Hi.CompareTo(b.Hi) : a.Lo.CompareTo(b.Lo);
}
