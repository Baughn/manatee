using System;
using System.Collections.Generic;
using Manatee.Core;
using Manatee.Core.Reduction;

namespace Manatee.Core.Tests.Reduction;

/// <summary>
/// Reduction-owned probe keys are DETERMINISTIC and TOPOLOGICAL (api.md §3/§13/§19;
/// 2026-07-06 final-wave fix): derived from (SegmentKey, along, ordinal) via
/// <see cref="ConductorGraph.ProbeKey"/>, never a per-graph call-order counter — so
/// two graphs built from the same geometry mint identical keys, and a client can
/// re-derive the key to TryResolveProbe after a from-scratch rebuild (re-driven
/// intake / FromCanonical reload).
/// </summary>
public sealed class ProbeKeyTests
{
    private static readonly ExternalKey SrcKey = new(0xE000_0000_0000_0000UL, 1);

    private static (Core.Netlist net, ConductorGraph g, ProbeId p) Build(double along, int extraProbes = 0)
    {
        var net = RedFx.NewNet();
        var g = new ConductorGraph(net, GraphOptions.SelfPartitioned);
        using (var b = g.BeginBulkBuild(3))
        {
            b.AddSegment(new SegmentKey(10), new JunctionKey(1), new JunctionKey(2), new ConductorSpec(100, 1));
            b.AddSegment(new SegmentKey(20), new JunctionKey(2), new JunctionKey(3), new ConductorSpec(200, 1));
            b.AddSegment(new SegmentKey(30), new JunctionKey(3), new JunctionKey(4), new ConductorSpec(150, 1));
        }
        var n1 = g.PortNode(new JunctionKey(1));
        var n4 = g.PortNode(new JunctionKey(4));
        using (var e = net.Edit()) { e.MarkReference(n4); e.AddVoltageSource(n1, n4, 9.0, SrcKey); }
        var p = g.AddProbe(new SegmentKey(20), along);
        for (var i = 0; i < extraProbes; i++) g.AddProbe(new SegmentKey(20), along);
        net.SolveOperatingPoint();
        return (net, g, p);
    }

    [Fact]
    public void Two_graphs_from_the_same_geometry_mint_the_same_probe_key()
    {
        var (netA, _, pA) = Build(0.25);
        var (netB, _, pB) = Build(0.25);

        var key = ConductorGraph.ProbeKey(new SegmentKey(20), 0.25);
        Assert.True(netA.TryResolveProbe(key, out var ra) && ra == pA,
            "graph A's probe must resolve by the client-derived key");
        Assert.True(netB.TryResolveProbe(key, out var rb) && rb == pB,
            "graph B's probe must resolve by the client-derived key");

        // Same geometry, same probe placement ⇒ the same interior reading too.
        Assert.Equal(netA.Solution.Read(pA), netB.Solution.Read(pB), 12);
    }

    [Fact]
    public void Rebuilt_from_scratch_intake_converges_to_the_same_resolvable_probe()
    {
        // "Rebuild-every-load": the client re-drives geometry into a FRESH net+graph
        // and re-resolves its probe purely from the key it can re-derive — the §13
        // re-resolution path that a call-order counter made unusable.
        var (_, _, _) = Build(0.6);
        var (net2, _, p2) = Build(0.6);
        Assert.True(net2.TryResolveProbe(ConductorGraph.ProbeKey(new SegmentKey(20), 0.6), out var found));
        Assert.Equal(p2, found);
    }

    [Fact]
    public void Colocated_probes_disambiguate_by_ordinal()
    {
        var (net, _, first) = Build(0.5, extraProbes: 1);
        Assert.True(net.TryResolveProbe(ConductorGraph.ProbeKey(new SegmentKey(20), 0.5, 0), out var p0));
        Assert.True(net.TryResolveProbe(ConductorGraph.ProbeKey(new SegmentKey(20), 0.5, 1), out var p1));
        Assert.Equal(first, p0);
        Assert.NotEqual(p0, p1);
    }
}
