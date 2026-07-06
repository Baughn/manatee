using System;
using System.Collections.Generic;
using Manatee.Core;
using Manatee.Core.Reduction;

namespace Manatee.Core.Tests.Reduction;

/// <summary>
/// Limit attribution (api.md §19 / R7; compaction.md): a solver limit event on a
/// COLLAPSED equivalent resistor is routed back to the culprit SEGMENT — current
/// density at the narrowest cross-section, PER LIMIT TYPE. Ampacity, i²t mass and
/// melting may each indict a DIFFERENT segment in a mixed-material chain (the
/// lead-fuse-in-a-copper-run case), and an ambient change re-weights the envelope
/// with no matrix change. Limits are evaluated on a tick Solve (never the
/// operating point — no time integral there; api.md §12).
/// </summary>
public sealed class AttributionTests
{
    private static readonly ExternalKey ISrcKey = new(0xE000_0000_0000_0000UL, 7);

    private sealed class Rig
    {
        public Core.Netlist Net = null!;
        public ConductorGraph Graph = null!;
    }

    // 1 —segA— 2 —segB— 3 —segC— 4, a current source forcing `amps` through the
    // collapsed 1→4 chain. Interiors 2,3 collapse ⇒ one equivalent resistor.
    private static Rig BuildChain(double amps, in LimitSpec la, double ra, in LimitSpec lb, double rb,
                                  in LimitSpec lc, double rc)
    {
        var net = new Core.Netlist(new NetlistOptions
        {
            Profile = SolverProfile.Dc(0.5),
            Wiring = WiringPolicy.ExplicitOnly(),
            Partitioning = PartitioningMode.SelfPartitioned,
            Debug = DebugLevel.Asserts,
        });
        var g = new ConductorGraph(net, GraphOptions.SelfPartitioned);
        using (var b = g.BeginBulkBuild(3))
        {
            b.AddSegment(new SegmentKey(10), new JunctionKey(1), new JunctionKey(2), new ConductorSpec(ra, 1, la));
            b.AddSegment(new SegmentKey(20), new JunctionKey(2), new JunctionKey(3), new ConductorSpec(rb, 1, lb));
            b.AddSegment(new SegmentKey(30), new JunctionKey(3), new JunctionKey(4), new ConductorSpec(rc, 1, lc));
        }
        var n1 = g.PortNode(new JunctionKey(1));
        var n4 = g.PortNode(new JunctionKey(4));
        using (var e = net.Edit())
        {
            e.MarkReference(n4);
            e.AddCurrentSource(n1, n4, amps, ISrcKey);
        }
        net.SolveOperatingPoint();
        return new Rig { Net = net, Graph = g };
    }

    private static List<AttributionResult> DrainAttributed(Rig rig)
    {
        var isl = rig.Net.Islands.Of(rig.Net.IslandOf(rig.Graph.PortNode(new JunctionKey(1))));
        var list = new List<AttributionResult>();
        Span<LimitEvent> buf = stackalloc LimitEvent[16];
        int n;
        while ((n = isl.DrainLimitEvents(buf)) > 0)
            for (var i = 0; i < n; i++)
                if (rig.Graph.Attribute(in buf[i], out var a)) list.Add(a);
        return list;
    }

    private static bool Find(List<AttributionResult> hits, LimitKind kind, out AttributionResult a)
    {
        foreach (var h in hits) if (h.Kind == kind) { a = h; return true; }
        a = default; return false;
    }

    [Fact]
    public void Lead_fuse_in_a_copper_run_attributes_overcurrent_to_the_lead_segment()
    {
        // Copper (200 A) — LEAD (10 A) — copper (200 A). Envelope ampacity = min = 10 A.
        // Force 20 A ⇒ the equivalent resistor over-currents; attribution names the LEAD
        // segment (key 20), margin = 20 / 10 = 2.
        var copper = new LimitSpec(200, 0, 0, default);
        var lead = new LimitSpec(10, 0, 0, default);
        var rig = BuildChain(20.0, copper, 0.01, lead, 0.10, copper, 0.01);
        Assert.Equal(1, rig.Graph.LiveChainCount);

        rig.Net.Solve(new TickClock(1, 0.5));    // evaluate limits
        var hits = DrainAttributed(rig);
        Assert.True(Find(hits, LimitKind.OverCurrent, out var a));
        Assert.Equal(new SegmentKey(20), a.Segment);
        Assert.Equal(2.0, a.Margin, 6);
    }

    [Fact]
    public void Mixed_chain_i2t_picks_a_different_segment_than_instantaneous_ampacity()
    {
        // seg10: MaxCurrent 200, no thermal.   (copper backbone)
        // seg20 (X): MaxCurrent 10, no thermal. (thin — narrowest AMPACITY)
        // seg30 (Y): MaxCurrent 100, MeltI2t 50 A²·s, τ huge. (least THERMAL MASS)
        // Pareto-set ruling (api.md §19, 2026-07-06): Y's melting integral runs
        // against Y's OWN 100 A rating — never against X's 10 A (the old hybrid pair,
        // which tripped fires the raw graph never would). Force 120 A:
        // instantaneous 120 > 10 ⇒ OverCurrent names X (seg20); Y heats at
        // (120² − 100²)·0.5 = 2200 A²·s per 0.5 s tick ⇒ crosses 50 A²·s on the
        // first tick ⇒ ThermalI2t names Y (seg30) — its own pair, its own segment.
        var backbone = new LimitSpec(200, 0, 0, default);
        var thin = new LimitSpec(10, 0, 0, default);
        var lowMass = new LimitSpec(100, 0, 0, new I2tParams(50, 1e9));
        var rig = BuildChain(120.0, backbone, 0.01, thin, 0.01, lowMass, 0.01);

        for (var t = 1; t <= 2; t++) rig.Net.Solve(new TickClock(t, 0.5));   // integrate i²t
        var hits = DrainAttributed(rig);

        Assert.True(Find(hits, LimitKind.OverCurrent, out var oc));
        Assert.Equal(new SegmentKey(20), oc.Segment);      // narrowest ampacity = X
        Assert.True(Find(hits, LimitKind.ThermalI2t, out var th));
        Assert.Equal(new SegmentKey(30), th.Segment);      // least thermal mass = Y
        Assert.NotEqual(oc.Segment, th.Segment);           // the whole point
    }

    [Fact]
    public void Reduced_chain_does_not_trip_the_fictional_hybrid_pair()
    {
        // THE reviewer's repro (api.md §19 ruling, 2026-07-06): X = 10 A ampacity,
        // NO melt; Y = 100 A, MeltI2t 50 A²·s. The retired hybrid envelope
        // (rating = 10 from X, melt = 50 from Y) tripped ThermalI2t at 20 A on the
        // first tick — an event the raw graph never emits (X never melts; Y is at
        // a fifth of its rating). The Pareto set carries only Y's pair, so 20 A
        // must over-current X every tick and NEVER melt anything.
        var backbone = new LimitSpec(200, 0, 0, default);
        var thin = new LimitSpec(10, 0, 0, default);                       // X: no melt
        var lowMass = new LimitSpec(100, 0, 0, new I2tParams(50, 1e9));    // Y
        var rig = BuildChain(20.0, backbone, 0.01, thin, 0.01, lowMass, 0.01);
        Assert.Equal(1, rig.Graph.LiveChainCount);

        for (var t = 1; t <= 40; t++)
        {
            rig.Net.Solve(new TickClock(t, 0.5));
            var hits = DrainAttributed(rig);
            Assert.True(Find(hits, LimitKind.OverCurrent, out var oc));   // X is over, every tick
            Assert.Equal(new SegmentKey(20), oc.Segment);
            Assert.False(Find(hits, LimitKind.ThermalI2t, out _),
                $"fictional hybrid ThermalI2t fired at tick {t} — no raw segment melts at 20 A");
        }
    }

    [Fact]
    public void Ambient_change_makes_a_hot_segment_the_weakest_and_reattributes()
    {
        // seg20 (A): 12 A ampacity. seg30 (B): 15 A ampacity. Cold: A is weakest (12).
        var backbone = new LimitSpec(200, 0, 0, default);
        var a12 = new LimitSpec(12, 0, 0, default);
        var b15 = new LimitSpec(15, 0, 0, default);
        var rig = BuildChain(20.0, backbone, 0.01, a12, 0.01, b15, 0.01);

        rig.Net.Solve(new TickClock(1, 0.5));
        Assert.True(Find(DrainAttributed(rig), LimitKind.OverCurrent, out var cold));
        Assert.Equal(new SegmentKey(20), cold.Segment);    // A is weakest cold

        // Heat B to 800 K (Vulcan). DerateCurrent(800) = sqrt((1300−800)/1000) = 0.7071,
        // so B's effective ampacity = 15 · 0.7071 ≈ 10.6 A < 12 A ⇒ B is now weakest.
        rig.Graph.SetAmbient(new SegmentKey(30), 800.0);
        rig.Net.Solve(new TickClock(2, 0.5));              // re-evaluate with the new envelope
        Assert.Equal(0, rig.Net.LastTickStats.IslandRebuilds);       // envelope-only: no topology rebuild
        Assert.Equal(0, rig.Net.LastTickStats.Refactorizations);     // …and no matrix change at all (the stronger claim)

        Assert.True(Find(DrainAttributed(rig), LimitKind.OverCurrent, out var hot));
        Assert.Equal(new SegmentKey(30), hot.Segment);     // B, the hot segment, now indicted
    }
}
