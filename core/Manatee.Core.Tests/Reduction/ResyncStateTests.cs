using System;
using System.Collections.Generic;
using Manatee.Core;
using Manatee.Core.Reduction;

namespace Manatee.Core.Tests.Reduction;

/// <summary>
/// Resync preserves evolved state (api.md §19 "Resync = snapshot → rebuild from
/// shadow → restore by key"; §14 "a part-melted reduced cable reloads part-melted";
/// 2026-07-06 final-wave fix). A drifted chain is removed and re-added as a fresh
/// component by the resync recompaction, so without the surrounding snapshot/restore
/// its thermal-envelope melting integral cold-starts — the cable un-melts across the
/// very backstop that promises to preserve it.
/// </summary>
public sealed class ResyncStateTests
{
    private sealed class TruthSource : IGeometrySource
    {
        private readonly List<GeometrySegment> _segs;
        public TruthSource(List<GeometrySegment> segs) => _segs = segs;
        public IEnumerable<GeometrySegment> Segments => _segs;
    }

    private static readonly ExternalKey SrcKey = new(0xE000_0000_0000_0000UL, 1);

    // Fuse (0.5 Ω, rated 10 A, melt 300 A²s, never cools) in series with plain
    // copper (0.5 Ω): one collapsed chain, envelope = the fuse's single pair.
    private static List<GeometrySegment> Truth() => new()
    {
        new GeometrySegment(new SegmentKey(10), new JunctionKey(1), new JunctionKey(2),
            new ConductorSpec(0.5, 1.0, new LimitSpec(10.0, 0.0, 0.0, new I2tParams(300.0, 0.0)))),
        new GeometrySegment(new SegmentKey(20), new JunctionKey(2), new JunctionKey(3),
            new ConductorSpec(0.5, 1.0)),
    };

    [Fact]
    public void Mid_melt_accumulator_survives_a_shape_changing_resync()
    {
        var truth = Truth();
        var net = RedFx.NewNet();
        var g = new ConductorGraph(net, GraphOptions.SelfPartitioned);
        using (var b = g.BeginBulkBuild(truth.Count))
            foreach (var s in truth) b.AddSegment(s.Key, s.A, s.B, s.Spec);
        var n1 = g.PortNode(new JunctionKey(1));
        var n3 = g.PortNode(new JunctionKey(3));
        using (var e = net.Edit()) { e.MarkReference(n3); e.AddVoltageSource(n1, n3, 20.0, SrcKey); }
        net.SolveOperatingPoint();

        // Drift the shadow: copper reads 0.5 + 1e-7 Ω. The chain's R changes, so a
        // later Resync against truth reshapes the chain (remove + re-add) — while the
        // 20 A overload is mid-melt. (The corrupted R leaves the current at ~20 A.)
        g.DebugCorruptSegmentOhms(new SegmentKey(20), 0.5 + 1e-7);

        // Heat: |I| = 20 A vs rating 10 ⇒ (400 − 100)·dt = 15 A²s per 0.05 s tick.
        // 10 ticks ⇒ acc ≈ 150 of 300 — half melted. (The rating doubles as the
        // instantaneous MaxCurrent limit, so OverCurrent events fire throughout;
        // only ThermalI2t matters here.)
        const double dt = 0.05;
        var evs = new LimitEvent[16];
        for (var t = 0; t < 10; t++)
        {
            net.Solve(new TickClock(1 + t, dt));
            var n = net.Islands.Of(g.PortNode(new JunctionKey(1))).DrainLimitEvents(evs);
            for (var i = 0; i < n; i++)
                Assert.True(evs[i].Kind != LimitKind.ThermalI2t, $"premature melt at tick {t}");
        }

        // The backstop: diff shows the drift; resync repairs it — and must carry the
        // half-melted integral across the chain's remove/re-add.
        var island = net.IslandOf(g.PortNode(new JunctionKey(1)));
        Assert.False(g.Diff(island, new TruthSource(truth)).IsEmpty);
        var rep = g.Resync(island, new TruthSource(truth));
        Assert.True(rep.Ok);

        // Remaining 150 A²s at 15 A²s/tick ⇒ trip on the ~10th post-resync tick. A
        // cold-started accumulator would need ~20. Assert the trip lands in the
        // preserved window and NOT in the cold-start window.
        var tripTick = -1;
        for (var t = 0; t < 20 && tripTick < 0; t++)
        {
            net.Solve(new TickClock(100 + t, dt));
            var n = net.Islands.Of(g.PortNode(new JunctionKey(1))).DrainLimitEvents(evs);
            for (var i = 0; i < n; i++)
                if (evs[i].Kind == LimitKind.ThermalI2t) tripTick = t;
        }
        Assert.True(tripTick >= 0, "fuse never tripped after resync");
        Assert.True(tripTick >= 7 && tripTick <= 12,
            $"trip at post-resync tick {tripTick}: expected ~10 (preserved half-melt); ~20 would mean the resync un-melted the cable");
    }
}
