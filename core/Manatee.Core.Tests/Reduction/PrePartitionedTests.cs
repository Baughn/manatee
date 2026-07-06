using System;
using Manatee.Core;
using Manatee.Core.Reduction;

namespace Manatee.Core.Tests.Reduction;

/// <summary>
/// PrePartitioned (Stationeers) intake (api.md §19): partition is mandatory, every
/// junction a segment touches is partition-tagged, a junction claimed by two
/// partitions throws <see cref="PartitionMergeException"/>, and each partition's
/// reference rail is created on first use so <see cref="ConductorGraph.ReferenceNode"/>
/// always resolves.
/// </summary>
public sealed class PrePartitionedTests
{
    private static readonly PartitionKey P1 = new(1001);
    private static readonly PartitionKey P2 = new(2002);

    private static Core.Netlist StationeersNet() => new(NetlistOptions.Stationeers(0.5));

    [Fact]
    public void Two_partitions_get_distinct_reference_rails_and_solve_independently()
    {
        var net = StationeersNet();
        var g = new ConductorGraph(net, GraphOptions.PrePartitioned);
        using (var b = g.BeginBulkBuild(3))
        {
            b.AddSegment(new SegmentKey(1), new JunctionKey(1), new JunctionKey(2), new ConductorSpec(50, 1), P1);
            b.AddSegment(new SegmentKey(2), new JunctionKey(2), new JunctionKey(3), new ConductorSpec(50, 1), P1);
            b.AddSegment(new SegmentKey(3), new JunctionKey(10), new JunctionKey(11), new ConductorSpec(50, 1), P2);
        }

        var ref1 = g.ReferenceNode(P1);
        var ref2 = g.ReferenceNode(P2);
        Assert.NotEqual(ref1, ref2);

        // A source + load in P1: 10 V across (chain 1→3 = 100 Ω) + (load 100 Ω) ⇒ V(3) = 5.
        var p1 = g.PortNode(new JunctionKey(1));
        var p3 = g.PortNode(new JunctionKey(3));
        using (var e = net.Edit())
        {
            e.AddVoltageSource(p1, ref1, 10.0, new ExternalKey(0xE000_0000_0000_0000UL, 1));
            e.AddResistor(p3, ref1, 100.0, new ExternalKey(0xE000_0000_0000_0000UL, 2));
        }
        net.SolveOperatingPoint();

        Assert.Equal(P1, net.Islands.Of(g.PortNode(new JunctionKey(1))).Partition);
        Assert.Equal(5.0, net.Solution.Voltage(g.PortNode(new JunctionKey(3))), 3);
    }

    [Fact]
    public void Missing_partition_throws()
    {
        var net = StationeersNet();
        var g = new ConductorGraph(net, GraphOptions.PrePartitioned);
        Assert.Throws<InvalidOperationException>(() =>
            g.AddSegment(new SegmentKey(1), new JunctionKey(1), new JunctionKey(2), new ConductorSpec(50, 1)));
    }

    [Fact]
    public void Junction_claimed_by_two_partitions_throws_PartitionMergeException()
    {
        var net = StationeersNet();
        var g = new ConductorGraph(net, GraphOptions.PrePartitioned);
        g.AddSegment(new SegmentKey(1), new JunctionKey(1), new JunctionKey(2), new ConductorSpec(50, 1), P1);
        // Junction 2 already belongs to P1; claiming it for P2 is illegal.
        Assert.Throws<PartitionMergeException>(() =>
            g.AddSegment(new SegmentKey(2), new JunctionKey(2), new JunctionKey(3), new ConductorSpec(50, 1), P2));
    }

    [Fact]
    public void ReferenceNode_is_PrePartitioned_only()
    {
        var net = RedFx.NewNet();
        var g = new ConductorGraph(net, GraphOptions.SelfPartitioned);
        Assert.Throws<InvalidOperationException>(() => g.ReferenceNode(P1));
    }

    [Fact]
    public void SyncFromReceipt_advances_cursor_without_demanding_resync()
    {
        var net = StationeersNet();
        var g = new ConductorGraph(net, GraphOptions.PrePartitioned);
        g.AddSegment(new SegmentKey(1), new JunctionKey(1), new JunctionKey(2), new ConductorSpec(50, 1), P1);

        EditReceipt r;
        using (var e = net.Edit())
        {
            e.AddResistor(g.PortNode(new JunctionKey(1)), g.ReferenceNode(P1), 100.0, new ExternalKey(0xD000UL, 1));
            r = e.Commit();
        }
        g.SyncFromReceipt(in r);
        Assert.False(g.ResyncNeeded);
    }
}
