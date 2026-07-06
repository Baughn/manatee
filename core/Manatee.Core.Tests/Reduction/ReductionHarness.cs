using System;
using System.Collections.Generic;
using Manatee.Core;
using Manatee.Core.Reduction;

namespace Manatee.Core.Tests.Reduction;

// Shared harness for the reduction equivalence tests (testing-strategy.md Equivalence
// Tests): build the SAME geometry two ways — a RAW netlist (one resistor per segment,
// interior nodes intact) and a REDUCED netlist (via ConductorGraph, series chains
// collapsed) — attach identical sources/grounds at the declared ports, solve both, and
// assert identical terminal voltages and interior probe reads. Reduction is
// semantically invisible (compaction.md Invariants), so the two must agree.
internal static class RedFx
{
    internal static JunctionKey J(ulong id) => new(id);
    internal static SegmentKey S(ulong id) => new(id);

    // Distinct high keys so the source / raw split resistors never collide with a
    // junction (node map) or segment (component map) key from a fixture.
    private static readonly ExternalKey SourceKey = new(0xE000_0000_0000_0000UL, 1);

    internal sealed class SegDef
    {
        public SegmentKey Key;
        public JunctionKey A, B;
        public double Ohms;
        public LimitSpec Limits;
        public SegDef(SegmentKey key, JunctionKey a, JunctionKey b, double ohms, LimitSpec limits = default)
        { Key = key; A = a; B = b; Ohms = ohms; Limits = limits; }
    }

    internal sealed class Case
    {
        public readonly List<SegDef> Segs = new();
        public readonly HashSet<JunctionKey> Ports = new();
        public readonly List<(SegmentKey seg, double along)> Probes = new();
        public JunctionKey Src, Gnd;
        public double Volts = 10.0;

        public Case Seg(ulong key, ulong a, ulong b, double ohms, LimitSpec limits = default)
        { Segs.Add(new SegDef(S(key), J(a), J(b), ohms, limits)); return this; }
        public Case Port(ulong j) { Ports.Add(J(j)); return this; }
        public Case Probe(ulong seg, double along) { Probes.Add((S(seg), along)); return this; }
        public Case Source(ulong src, ulong gnd, double volts) { Src = J(src); Gnd = J(gnd); Volts = volts; return this; }
    }

    internal static Core.Netlist NewNet() => new(new NetlistOptions
    {
        Profile = SolverProfile.Dc(0.5),
        Wiring = WiringPolicy.ExplicitOnly(),
        Partitioning = PartitioningMode.SelfPartitioned,
        Debug = DebugLevel.Asserts,
    });

    internal sealed class Reduced
    {
        public Core.Netlist Net = null!;
        public ConductorGraph Graph = null!;
        public readonly Dictionary<JunctionKey, NodeId> Ports = new();
        public readonly List<ProbeId> Probes = new();
    }

    internal static Reduced BuildReduced(Case c)
    {
        var r = new Reduced { Net = NewNet() };
        r.Graph = new ConductorGraph(r.Net, GraphOptions.SelfPartitioned);
        using (var b = r.Graph.BeginBulkBuild(c.Segs.Count))
            foreach (var s in c.Segs)
                b.AddSegment(s.Key, s.A, s.B, new ConductorSpec(s.Ohms, 1.0, s.Limits));
        return Finalize(r, c);
    }

    // Same final geometry as BuildReduced, but applied through incremental
    // AddSegment/RemoveSegment in a shuffled order with transient "noise" segments —
    // the input to the incremental-equivalence test.
    internal static Reduced BuildReducedIncremental(Case c, int seed)
    {
        var r = new Reduced { Net = NewNet() };
        r.Graph = new ConductorGraph(r.Net, GraphOptions.SelfPartitioned);
        var rng = new Random(seed);
        var order = new List<SegDef>(c.Segs);
        for (var i = order.Count - 1; i > 0; i--) { var j = rng.Next(i + 1); (order[i], order[j]) = (order[j], order[i]); }

        ulong noiseKey = 5_000_000;
        foreach (var s in order)
        {
            if (rng.Next(3) == 0)   // transient noise segment on the same endpoints, then cut
            {
                var nk = S(noiseKey++);
                r.Graph.AddSegment(nk, s.A, s.B, new ConductorSpec(37.0, 1.0));
                r.Graph.RemoveSegment(nk);
            }
            r.Graph.AddSegment(s.Key, s.A, s.B, new ConductorSpec(s.Ohms, 1.0, s.Limits));
        }
        return Finalize(r, c);
    }

    private static Reduced Finalize(Reduced r, Case c)
    {
        var ports = new HashSet<JunctionKey>(c.Ports) { c.Src, c.Gnd };
        foreach (var j in ports) r.Ports[j] = r.Graph.PortNode(j);
        foreach (var (seg, along) in c.Probes) r.Probes.Add(r.Graph.AddProbe(seg, along));

        using (var e = r.Net.Edit())
        {
            e.MarkReference(r.Ports[c.Gnd]);
            e.AddVoltageSource(r.Ports[c.Src], r.Ports[c.Gnd], c.Volts, SourceKey);
        }
        r.Net.SolveOperatingPoint();
        return r;
    }

    internal static byte[] Normalized(Core.Netlist net)
    {
        var w = new System.Buffers.ArrayBufferWriter<byte>();
        net.SaveNormalized(w);
        return w.WrittenSpan.ToArray();
    }

    internal sealed class Raw
    {
        public Core.Netlist Net = null!;
        public readonly Dictionary<JunctionKey, NodeId> Ports = new();
        public readonly List<NodeId> ProbeNodes = new();
    }

    internal static Raw BuildRaw(Case c)
    {
        var raw = new Raw { Net = NewNet() };

        // Region union-find over perfect conductors (identical equipotential model to
        // the reducer, so the test isolates SERIES collapse).
        var parent = new Dictionary<JunctionKey, JunctionKey>();
        JunctionKey Find(JunctionKey x) { while (!parent[x].Equals(x)) { parent[x] = parent[parent[x]]; x = parent[x]; } return x; }
        void Add(JunctionKey x) { if (!parent.ContainsKey(x)) parent[x] = x; }
        void Union(JunctionKey a, JunctionKey b)
        {
            Add(a); Add(b); var ra = Find(a); var rb = Find(b);
            if (ra.Equals(rb)) return;
            if (Less(rb, ra)) parent[ra] = rb; else parent[rb] = ra;
        }
        foreach (var s in c.Segs) { Add(s.A); Add(s.B); if (s.Ohms <= 0) Union(s.A, s.B); }
        JunctionKey Rep(JunctionKey j) => Find(j);

        var probeOf = new Dictionary<SegmentKey, double>();
        foreach (var (seg, along) in c.Probes) probeOf[seg] = along;

        var node = new Dictionary<JunctionKey, NodeId>();
        var probeInterior = new Dictionary<SegmentKey, NodeId>();
        ulong splitKey = 0xC000_0000_0000_0000UL;

        using (var e = raw.Net.Edit())
        {
            foreach (var j in parent.Keys)
            {
                var rep = Rep(j);
                if (!node.ContainsKey(rep)) node[rep] = e.AddNode(rep.External());
            }
            foreach (var s in c.Segs)
            {
                if (s.Ohms <= 0) continue;
                var ra = node[Rep(s.A)]; var rb = node[Rep(s.B)];
                if (Rep(s.A).Equals(Rep(s.B))) { if (probeOf.ContainsKey(s.Key)) probeInterior[s.Key] = ra; continue; }

                if (probeOf.TryGetValue(s.Key, out var along) && along > 0 && along < 1)
                {
                    var mid = e.AddNode(new ExternalKey(splitKey++, s.Key.Lo));
                    e.AddResistor(ra, mid, s.Ohms * along, new ExternalKey(splitKey++, s.Key.Lo));
                    e.AddResistor(mid, rb, s.Ohms * (1 - along), new ExternalKey(splitKey++, s.Key.Lo));
                    probeInterior[s.Key] = mid;
                }
                else
                {
                    e.AddResistor(ra, rb, s.Ohms, s.Key.External());
                    if (probeOf.TryGetValue(s.Key, out var a0)) probeInterior[s.Key] = a0 <= 0 ? ra : rb;
                }
            }

            e.MarkReference(node[Rep(c.Gnd)]);
            e.AddVoltageSource(node[Rep(c.Src)], node[Rep(c.Gnd)], c.Volts, SourceKey);
        }

        var ports = new HashSet<JunctionKey>(c.Ports) { c.Src, c.Gnd };
        foreach (var j in ports) raw.Ports[j] = node[Rep(j)];
        foreach (var (seg, _) in c.Probes) raw.ProbeNodes.Add(probeInterior[seg]);

        raw.Net.SolveOperatingPoint();
        return raw;
    }

    private static bool Less(in JunctionKey a, in JunctionKey b) => a.Hi != b.Hi ? a.Hi < b.Hi : a.Lo < b.Lo;

    internal static void Close(double expected, double actual, string what, double tol = 1e-6)
    {
        var diff = Math.Abs(expected - actual);
        var scale = Math.Max(1.0, Math.Max(Math.Abs(expected), Math.Abs(actual)));
        Assert.True(diff <= tol * scale, $"{what}: expected {expected:R}, got {actual:R} (|Δ|={diff:R})");
    }
}
