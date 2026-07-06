using System;
using System.Collections.Generic;
using Manatee.Core;
using Manatee.Core.Reduction;

namespace Manatee.Core.Tests.Reduction;

/// <summary>
/// The drift backstop (api.md §19 R11; compaction.md Resync backstop): a deliberately
/// corrupted live map is caught by <see cref="ConductorGraph.Diff"/> as typed entries
/// naming an <see cref="ExternalKey"/>, and <see cref="ConductorGraph.Resync"/> repairs
/// it so the solution matches a from-scratch build of the shadow truth.
/// </summary>
public sealed class DriftTests
{
    private sealed class TruthSource : IGeometrySource
    {
        private readonly List<GeometrySegment> _segs;
        public TruthSource(List<GeometrySegment> segs) => _segs = segs;
        public IEnumerable<GeometrySegment> Segments => _segs;
    }

    private static readonly ExternalKey SrcKey = new(0xE000_0000_0000_0000UL, 1);

    // Backbone 1-2-3-4-5 (interiors collapse), one branch 3-6 keeping 3 a tap.
    private static List<GeometrySegment> Truth() => new()
    {
        new GeometrySegment(new SegmentKey(10), new JunctionKey(1), new JunctionKey(2), new ConductorSpec(100, 1)),
        new GeometrySegment(new SegmentKey(20), new JunctionKey(2), new JunctionKey(3), new ConductorSpec(200, 1)),
        new GeometrySegment(new SegmentKey(30), new JunctionKey(3), new JunctionKey(4), new ConductorSpec(150, 1)),
        new GeometrySegment(new SegmentKey(40), new JunctionKey(4), new JunctionKey(5), new ConductorSpec(120, 1)),
        new GeometrySegment(new SegmentKey(50), new JunctionKey(3), new JunctionKey(6), new ConductorSpec(300, 1)),
    };

    private static (Core.Netlist net, ConductorGraph g) BuildFrom(List<GeometrySegment> segs)
    {
        var net = RedFx.NewNet();
        var g = new ConductorGraph(net, GraphOptions.SelfPartitioned);
        using (var b = g.BeginBulkBuild(segs.Count))
            foreach (var s in segs) b.AddSegment(s.Key, s.A, s.B, s.Spec);
        var n1 = g.PortNode(new JunctionKey(1));
        var n5 = g.PortNode(new JunctionKey(5));
        using (var e = net.Edit()) { e.MarkReference(n5); e.AddVoltageSource(n1, n5, 10.0, SrcKey); }
        net.SolveOperatingPoint();
        return (net, g);
    }

    private static IslandId IslandOf(Core.Netlist net, ConductorGraph g)
        => net.IslandOf(g.PortNode(new JunctionKey(1)));

    [Fact]
    public void Clean_graph_diffs_empty()
    {
        var truth = Truth();
        var (net, g) = BuildFrom(truth);
        var report = g.Diff(IslandOf(net, g), new TruthSource(truth));
        Assert.True(report.IsEmpty);
        Assert.Equal(0, report.Count);
    }

    [Fact]
    public void Value_corruption_is_reported_then_resynced()
    {
        var truth = Truth();
        var (net, g) = BuildFrom(truth);

        // Corrupt segment 20's resistance in the live map (and recompact ⇒ the realized
        // equivalent resistor now diverges from the truth).
        g.DebugCorruptSegmentOhms(new SegmentKey(20), 999.0);
        net.SolveOperatingPoint();

        var report = g.Diff(IslandOf(net, g), new TruthSource(truth));
        Assert.False(report.IsEmpty);
        Span<DriftEntry> buf = stackalloc DriftEntry[8];
        var n = report.Drain(buf);
        var found = false;
        for (var i = 0; i < n; i++)
            if (buf[i].Kind == DriftKind.ValueMismatch && buf[i].Key.Equals(new SegmentKey(20).External()))
            {
                Assert.Equal(999.0, buf[i].Live, 6);
                Assert.Equal(200.0, buf[i].Shadow, 6);
                found = true;
            }
        Assert.True(found, "ValueMismatch on segment 20 not reported");

        // Resync from truth repairs; solution now equals a from-scratch build.
        g.Resync(IslandOf(net, g), new TruthSource(truth));
        net.SolveOperatingPoint();

        Assert.True(g.Diff(IslandOf(net, g), new TruthSource(truth)).IsEmpty);
        AssertMatchesFromScratch(net, g, truth);
    }

    [Fact]
    public void Missing_segment_is_reported_as_MissingInLive_then_resynced()
    {
        var truth = Truth();
        var (net, g) = BuildFrom(truth);

        g.DebugDropSegment(new SegmentKey(40));   // live loses 4-5; truth still has it
        net.SolveOperatingPoint();

        var report = g.Diff(IslandOf(net, g), new TruthSource(truth));
        Span<DriftEntry> buf = stackalloc DriftEntry[8];
        var n = report.Drain(buf);
        var found = false;
        for (var i = 0; i < n; i++)
            if (buf[i].Kind == DriftKind.MissingInLive && buf[i].Key.Equals(new SegmentKey(40).External()))
                found = true;
        Assert.True(found, "MissingInLive on segment 40 not reported");

        g.Resync(IslandOf(net, g), new TruthSource(truth));
        net.SolveOperatingPoint();
        Assert.True(g.Diff(IslandOf(net, g), new TruthSource(truth)).IsEmpty);
        AssertMatchesFromScratch(net, g, truth);
    }

    private static void AssertMatchesFromScratch(Core.Netlist net, ConductorGraph g, List<GeometrySegment> truth)
    {
        var (fsNet, fsG) = BuildFrom(truth);
        // Compare at boundary junctions only (1,5 ports; 3 a tap; 6 a stub end) — a
        // collapsed interior has no node to read without splitting the run.
        foreach (var j in new ulong[] { 1, 3, 5, 6 })
        {
            var live = net.Solution.Voltage(g.PortNode(new JunctionKey(j)));
            var fs = fsNet.Solution.Voltage(fsG.PortNode(new JunctionKey(j)));
            RedFx.Close(fs, live, $"junction {j}", 1e-9);
        }
    }
}
