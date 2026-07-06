using System;
using System.Collections.Generic;
using Manatee.Core;
using Manatee.Core.Reduction;
using Manatee.Core.Tests.Netlist;

namespace Manatee.Core.Tests.Reduction;

/// <summary>
/// api.md §19 / compaction.md: <c>SetAmbient</c> is a tier-0 envelope recompute —
/// project rule: zero alloc on tiers 0–2 after warmup. With unchanged frontier
/// membership (the common case: same pairs, re-derated thresholds) the recompute
/// must rewrite the chain's existing envelope arrays IN PLACE, not mint fresh ones
/// per call. Adjudicated 2026-07-06; the min-over-runs pattern follows
/// <see cref="Manatee.Core.Tests.ZeroAllocCollection"/>.
/// </summary>
[Collection(Manatee.Core.Tests.ZeroAllocCollection.Name)]
public sealed class AmbientRederateAllocTests
{
    [Fact]
    public void Ambient_rederate_with_unchanged_membership_allocates_nothing()
    {
        if (!ZeroAllocGates.CounterIsReliable())
            return;   // GC counter inert on this runtime — best-effort skip (api.md §8)

        var net = new Core.Netlist(new NetlistOptions
        {
            Profile = SolverProfile.Dc(0.5),
            Wiring = WiringPolicy.ExplicitOnly(),
            Partitioning = PartitioningMode.SelfPartitioned,
        });
        var graph = new ConductorGraph(net, GraphOptions.SelfPartitioned);

        // A two-material chain whose Pareto frontier keeps BOTH pairs at every
        // ambient this test sweeps (the never-cooling tau=0 segment can't be retired
        // by the faster-cooling fuse, and 12 A never dips under the derated 10 A).
        using (var b = graph.BeginBulkBuild(2))
        {
            b.AddSegment(new SegmentKey(1), new JunctionKey(1), new JunctionKey(2),
                new ConductorSpec(0.10, 1.0, new LimitSpec(10, 0, 0, new I2tParams(100, 5.0))));
            b.AddSegment(new SegmentKey(2), new JunctionKey(2), new JunctionKey(3),
                new ConductorSpec(0.05, 1.0, new LimitSpec(12, 0, 0, new I2tParams(200, 0.0))));
        }
        var p1 = graph.PortNode(new JunctionKey(1));
        var p3 = graph.PortNode(new JunctionKey(3));
        using (var e = net.Edit())
        {
            e.MarkReference(p3);
            e.AddCurrentSource(p1, p3, 5.0, new ExternalKey(0xE000_0000_0000_0000UL, 1));
        }
        net.SolveOperatingPoint();

        var seg = new SegmentKey(1);
        // Warmup: first re-derates may grow scratch lists / dictionary internals.
        for (var i = 0; i < 8; i++) graph.SetAmbient(seg, 300.0 + i);

        long best = long.MaxValue;
        for (var run = 0; run < 8 && best != 0; run++)
        {
            var before = GC.GetAllocatedBytesForCurrentThread();
            for (var i = 0; i < 100; i++) graph.SetAmbient(seg, 310.0 + (i & 7));
            var d = GC.GetAllocatedBytesForCurrentThread() - before;
            if (d < best) best = d;
        }
        Assert.True(best == 0,
            $"tier-0 ambient re-derate allocated {best} B over 100 SetAmbient calls (min over runs; expected 0)");
    }
}
