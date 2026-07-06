using System;
using System.Collections.Generic;
using Manatee.Core;
using Manatee.Core.Reduction;

namespace Manatee.Core.Tests.Reduction;

/// <summary>
/// THE payoff test of the Pareto-set envelope ruling (api.md §12/§19, 2026-07-06):
/// raw-vs-reduced LIMIT-EVENT equivalence over mixed-material series chains. The same
/// geometry is built twice — RAW (one resistor per segment, each carrying its own
/// LimitSpec) and REDUCED (ConductorGraph collapse to one equivalent resistor with a
/// registered thermal envelope) — and driven through the same seeded current script.
///
/// The equivalence contract (semantic invisibility for hazards, R7/R9):
/// <list type="bullet">
/// <item>NO FICTION: every reduced ThermalI2t event maps (via Attribute + PairIndex)
///   to a segment that raw tripped at the SAME tick with the SAME threshold — a
///   reduced pair is bit-for-bit that segment's own melting integral;</item>
/// <item>NO DELAY: the FIRST thermal trip happens on the same tick on both sides
///   (the Pareto frontier retires only pairs that trip no later at every current, so
///   the earliest raw trip always survives into the reduced set). Post-first-trip
///   the game acts (pops/melts) — the scripts end there, like a client would;</item>
/// <item>instantaneous OverCurrent: the reduced equivalent fires exactly when at
///   least one raw segment fires, attributed to a segment raw indicts too.</item>
/// </list>
/// Includes the review's original repro: X=10 A/no-melt + Y=100 A/melt-50 driven at
/// 20 A — the retired hybrid pair tripped tick 1; the raw graph never trips.
/// </summary>
public sealed class LimitEventEquivalenceTests
{
    private static readonly ExternalKey SrcKey = new(0xE000_0000_0000_0000UL, 7);

    private sealed class Material
    {
        public double Ohms, Rating, Melt, Tau;
        public Material(double ohms, double rating, double melt, double tau)
        { Ohms = ohms; Rating = rating; Melt = melt; Tau = tau; }
    }

    // Palette: copper-ish backbones (no melt), fuses/thin wires (melt), duplicates on
    // purpose (dedupe → min-key culprit), one dominated material (trips later than the
    // 10 A fuse at every current — pruned from the envelope, still raw-equivalent
    // through the first trip).
    private static readonly Material[] Palette =
    {
        new(0.01, 200, 0, 0),                 // heavy copper, never melts
        new(0.05, 50, 0, 0),                  // thin copper, never melts
        new(0.10, 10, 50, 1e9),               // lead fuse (frontier)
        new(0.10, 10, 300, 20.0),             // DOMINATED by the lead fuse: rating 10 ≤ 10, melt 50 ≤ 300, tau 1e9 ≥ 20 — pruned in every seed (the tau leg has its own deterministic fixtures below)
        new(0.02, 100, 50, 1e9),              // the reviewer's Y
        new(0.08, 25, 120, 5.0),              // mid fuse, fast cooling
    };

    private sealed class Rig
    {
        public Core.Netlist RawNet = null!, RedNet = null!;
        public ConductorGraph Graph = null!;
        public ISourceId RawSrc, RedSrc;
        public NodeId RawProbe, RedProbe;                       // island lookups
        public readonly Dictionary<int, SegmentKey> RawSlotToSeg = new();
        public readonly Dictionary<SegmentKey, Material> Mat = new();
    }

    private static Core.Netlist NewNet() => new(new NetlistOptions
    {
        Profile = SolverProfile.Dc(0.5),
        Wiring = WiringPolicy.ExplicitOnly(),
        Partitioning = PartitioningMode.SelfPartitioned,
        Debug = DebugLevel.Asserts,
    });

    // Chain 1..n+1 with per-segment materials; a current source drives junction 1 →
    // junction n+1 (the reference), so every segment carries exactly the source current.
    private static Rig Build(IReadOnlyList<Material> mats, double amps)
    {
        var rig = new Rig();
        var n = mats.Count;

        // RAW: one node per junction, one resistor per segment with its own LimitSpec.
        rig.RawNet = NewNet();
        using (var e = rig.RawNet.Edit())
        {
            var nodes = new NodeId[n + 1];
            for (var j = 0; j <= n; j++) nodes[j] = e.AddNode(new ExternalKey(0, (ulong)(j + 1)));
            e.MarkReference(nodes[n]);
            for (var i = 0; i < n; i++)
            {
                var m = mats[i];
                var key = new ExternalKey(0xA000_0000_0000_0000UL, (ulong)(10 + i));
                e.AddResistor(nodes[i], nodes[i + 1], m.Ohms,
                    key, new LimitSpec(m.Rating, 0, 0, new I2tParams(m.Melt, m.Tau)));
            }
            e.AddCurrentSource(nodes[0], nodes[n], amps, SrcKey);
            rig.RawProbe = nodes[0];
        }
        rig.RawNet.SolveOperatingPoint();
        Assert.True(rig.RawNet.TryResolve(SrcKey, out var sr));
        rig.RawSrc = new ISourceId(sr.Slot, sr.Gen, sr.Net);
        for (var i = 0; i < n; i++)
        {
            var key = new ExternalKey(0xA000_0000_0000_0000UL, (ulong)(10 + i));
            Assert.True(rig.RawNet.TryResolve(key, out var c));
            rig.RawSlotToSeg[c.Slot] = new SegmentKey(0xA000_0000_0000_0000UL, (ulong)(10 + i));
        }

        // REDUCED: the same geometry through the ConductorGraph.
        rig.RedNet = NewNet();
        rig.Graph = new ConductorGraph(rig.RedNet, GraphOptions.SelfPartitioned);
        using (var b = rig.Graph.BeginBulkBuild(n))
            for (var i = 0; i < n; i++)
            {
                var m = mats[i];
                var sk = new SegmentKey(0xA000_0000_0000_0000UL, (ulong)(10 + i));
                b.AddSegment(sk, new JunctionKey((ulong)(i + 1)), new JunctionKey((ulong)(i + 2)),
                    new ConductorSpec(m.Ohms, 1.0, new LimitSpec(m.Rating, 0, 0, new I2tParams(m.Melt, m.Tau))));
                rig.Mat[sk] = m;
            }
        var p1 = rig.Graph.PortNode(new JunctionKey(1));
        var pEnd = rig.Graph.PortNode(new JunctionKey((ulong)(n + 1)));
        using (var e = rig.RedNet.Edit())
        {
            e.MarkReference(pEnd);
            e.AddCurrentSource(p1, pEnd, amps, SrcKey);
            rig.RedProbe = p1;
        }
        rig.RedNet.SolveOperatingPoint();
        Assert.True(rig.RedNet.TryResolve(SrcKey, out var ss));
        rig.RedSrc = new ISourceId(ss.Slot, ss.Gen, ss.Net);
        return rig;
    }

    private readonly record struct Ev(long Tick, LimitKind Kind, SegmentKey Seg, double Threshold);

    private static List<Ev> DrainRaw(Rig rig, long tick)
    {
        var list = new List<Ev>();
        Span<LimitEvent> buf = stackalloc LimitEvent[16];
        var isl = rig.RawNet.Islands.Of(rig.RawNet.IslandOf(rig.RawProbe));
        int k;
        while ((k = isl.DrainLimitEvents(buf)) > 0)
            for (var i = 0; i < k; i++)
            {
                Assert.True(rig.RawSlotToSeg.TryGetValue(buf[i].Source.Slot, out var seg),
                    "raw event from an unknown component");
                list.Add(new Ev(tick, buf[i].Kind, seg, buf[i].Threshold));
            }
        return list;
    }

    private static List<Ev> DrainReduced(Rig rig, long tick)
    {
        var list = new List<Ev>();
        Span<LimitEvent> buf = stackalloc LimitEvent[16];
        var isl = rig.RedNet.Islands.Of(rig.RedNet.IslandOf(rig.RedProbe));
        int k;
        while ((k = isl.DrainLimitEvents(buf)) > 0)
            for (var i = 0; i < k; i++)
            {
                Assert.True(rig.Graph.Attribute(in buf[i], out var a),
                    $"reduced {buf[i].Kind} event did not attribute to a segment");
                list.Add(new Ev(tick, buf[i].Kind, a.Segment, buf[i].Threshold));
            }
        return list;
    }

    // Drive both sides through the same current script; stop after the first thermal
    // trip (the game acts there). Assert the equivalence contract every tick.
    // Returns the first-trip tick, or -1 when the script ends with no thermal trip
    // (callers that REQUIRE a trip assert on it so a mis-tuned script can't pass
    // vacuously).
    private static long RunScript(Rig rig, IReadOnlyList<double> ampsPerTick)
    {
        for (var t = 0; t < ampsPerTick.Count; t++)
        {
            var tick = t + 1;
            rig.RawNet.Drive(rig.RawSrc, ampsPerTick[t]);
            rig.RedNet.Drive(rig.RedSrc, ampsPerTick[t]);
            rig.RawNet.Solve(new TickClock(tick, 0.5));
            rig.RedNet.Solve(new TickClock(tick, 0.5));

            var raw = DrainRaw(rig, tick);
            var red = DrainReduced(rig, tick);

            // ── OverCurrent: reduced fires iff raw fires; culprit ∈ raw's set. ──
            var rawOc = raw.FindAll(e => e.Kind == LimitKind.OverCurrent);
            var redOc = red.FindAll(e => e.Kind == LimitKind.OverCurrent);
            Assert.True(redOc.Count <= 1, "the equivalent coalesces to one OverCurrent per tick");
            Assert.Equal(rawOc.Count > 0, redOc.Count == 1);
            if (redOc.Count == 1)
                Assert.Contains(rawOc, e => e.Seg.Equals(redOc[0].Seg));

            // ── ThermalI2t: no fiction (every reduced event is a raw event — same
            //    tick, same segment, same threshold) and no delay (a first trip on
            //    either side is a first trip on both). ──
            var rawTh = raw.FindAll(e => e.Kind == LimitKind.ThermalI2t);
            var redTh = red.FindAll(e => e.Kind == LimitKind.ThermalI2t);
            foreach (var e in redTh)
            {
                Assert.Contains(rawTh, r => r.Seg.Equals(e.Seg));
                var match = rawTh.Find(r => r.Seg.Equals(e.Seg));
                Assert.Equal(match.Threshold, e.Threshold, 9);
            }
            Assert.Equal(rawTh.Count > 0, redTh.Count > 0);   // same first-trip tick
            if (rawTh.Count > 0) return tick;                 // the client acts here
        }
        return -1;
    }

    // ------------------------------------------------------------ hand fixtures

    [Fact]
    public void Reviewers_repro_no_hybrid_trip_at_20A()
    {
        // X = 10 A / NO melt; Y = 100 A / melt 50 A²·s. At 20 A the raw graph
        // over-currents X forever and never melts anything; the retired hybrid
        // envelope (10 A rating with Y's 50 A²·s melt) tripped on tick 1.
        var mats = new List<Material>
        {
            new(0.10, 10, 0, 0),               // X
            new(0.02, 100, 50, 1e9),           // Y
        };
        var rig = Build(mats, 20.0);
        var script = new double[60];
        for (var i = 0; i < script.Length; i++) script[i] = 20.0;
        RunScript(rig, script);

        // And the equivalent's envelope really is Y's single pair.
        Assert.True(rig.RedNet.TryResolve(new ExternalKey(0xA000_0000_0000_0000UL, 10), out var eq));
        Assert.Equal(1, rig.RedNet.ThermalEnvelopeCount(eq));
    }

    [Fact]
    public void Same_rating_different_melt_trips_the_frontier_pair_on_the_raw_tick()
    {
        // A (10 A, melt 50, τ ∞) and B (10 A, melt 300, τ ∞): B is dominated (same
        // rating, higher melt, same cooling) — pruned. At 20 A both raw segments heat
        // 150 A²·s per tick: A trips on tick 1 — the reduced side must trip the SAME
        // tick, attributed to A, and stop there (post-trip the client acts).
        var mats = new List<Material>
        {
            new(0.10, 10, 50, 1e9),
            new(0.10, 10, 300, 1e9),
        };
        var rig = Build(mats, 20.0);
        RunScript(rig, new double[] { 20.0, 20.0, 20.0 });
    }

    [Fact]
    public void Never_cooling_pair_survives_dominance_and_trips_on_the_raw_tick()
    {
        // The tau ≤ 0 regression (adjudicated 2026-07-06). The ENGINE treats tau ≤ 0
        // as "never cools" (Netlist.Limits cools only when tau > 0) — the SLOWEST
        // cooling — but a numeric tau comparison ranks 0 as the FASTEST and let the
        // cooling fuse (10 A, melt 100, tau 5) retire the never-cooling segment
        // (12 A, melt 200, tau 0). Under a pulsed script — one 15 A tick, forty 1 A
        // cooling ticks, repeated — the fuse resets in every gap (·0.9^40 ≈ ×0.015)
        // while the never-cooling segment ratchets 40.5 A²·s per cycle and trips RAW
        // at tick 165 (5 × 40.5 = 202.5 ≥ 200); the wrongly-pruned reduced side never
        // tripped — a melt with no equivalent event, violating no-delay (api.md §19).
        // Both pairs must survive: the tau leg of the dominance test is load-bearing.
        var mats = new List<Material>
        {
            new(0.10, 10, 100, 5.0),           // cooling fuse (lower rating AND melt)
            new(0.05, 12, 200, 0.0),           // never-cooling (tau ≤ 0 ⇒ compares as ∞)
        };
        var rig = Build(mats, 15.0);

        // Deterministic 2-member frontier, previously reachable only by lucky seeds.
        Assert.True(rig.RedNet.TryResolve(new ExternalKey(0xA000_0000_0000_0000UL, 10), out var eq));
        Assert.Equal(2, rig.RedNet.ThermalEnvelopeCount(eq));

        var script = new List<double>();
        for (var cycle = 0; cycle < 6; cycle++)
        {
            script.Add(15.0);                                  // pulse: both segments over rating
            for (var i = 0; i < 40; i++) script.Add(1.0);      // gap: fuse resets, tau=0 holds
        }
        var tripTick = RunScript(rig, script);                 // tick-for-tick equivalence to the trip
        Assert.Equal(165, tripTick);                           // 5th pulse: 5 × 40.5 = 202.5 ≥ 200
    }

    [Fact]
    public void Envelope_accumulators_survive_snapshot_restore()
    {
        // Reduced chain: 10 A / 250 A²·s fuse in a copper run, driven at 20 A ⇒
        // +150 A²·s per 0.5 s tick ⇒ trips on tick 2 (acc 300 ≥ 250 with margin —
        // the melt threshold deliberately does NOT land exactly on a tick edge,
        // where an ulp of solve rounding could move the trip a tick). Snapshot
        // after tick 1 (part-melted), heat to the trip, restore the tick-1
        // snapshot, and heat again: the trip must land exactly one tick later BOTH
        // times — the per-pair melting integral rides the island snapshot
        // (EnvI2t unit, api.md §14).
        var mats = new List<Material>
        {
            new(0.01, 200, 0, 0),
            new(0.10, 10, 250, 1e9),
        };
        var rig = Build(mats, 20.0);

        rig.RedNet.Solve(new TickClock(1, 0.5));
        Assert.Empty(DrainReduced(rig, 1).FindAll(e => e.Kind == LimitKind.ThermalI2t));
        var w = new System.Buffers.ArrayBufferWriter<byte>();
        rig.RedNet.Islands.Of(rig.RedNet.IslandOf(rig.RedProbe)).Snapshot(w);
        var blob = w.WrittenSpan.ToArray();

        rig.RedNet.Solve(new TickClock(2, 0.5));
        var first = DrainReduced(rig, 2).FindAll(e => e.Kind == LimitKind.ThermalI2t);
        Assert.Single(first);
        Assert.Equal(new SegmentKey(0xA000_0000_0000_0000UL, 11), first[0].Seg);

        // Rewind to the half-melted state and heat again: same one-tick fuse.
        var res = rig.RedNet.Islands.Of(rig.RedNet.IslandOf(rig.RedProbe)).Restore(blob);
        Assert.True(res.Matched >= 1, "the envelope unit must restore");
        rig.RedNet.Solve(new TickClock(3, 0.5));
        var again = DrainReduced(rig, 3).FindAll(e => e.Kind == LimitKind.ThermalI2t);
        Assert.Single(again);
        Assert.Equal(new SegmentKey(0xA000_0000_0000_0000UL, 11), again[0].Seg);
    }

    [Fact]
    public void Envelope_snapshot_restores_accumulators_at_pair_index_1()
    {
        // Same rewind shape as the test above, but the mid-melt fuse sits at PAIR
        // INDEX 1: a second frontier member with a smaller culprit segment key
        // occupies index 0 (it survives dominance because tau ≤ 0 = never cools —
        // slower than the fuse's 1e9), guarding the POSITIONAL EnvI2t restore path
        // for envelopes wider than one pair.
        var mats = new List<Material>
        {
            new(0.01, 12, 1e6, 0.0),           // index 0: huge melt, never trips here
            new(0.10, 10, 250, 1e9),           // index 1: the fuse under test
        };
        var rig = Build(mats, 20.0);
        Assert.True(rig.RedNet.TryResolve(new ExternalKey(0xA000_0000_0000_0000UL, 10), out var eq));
        Assert.Equal(2, rig.RedNet.ThermalEnvelopeCount(eq));

        rig.RedNet.Solve(new TickClock(1, 0.5));               // fuse acc: 150 of 250
        Assert.Empty(DrainReduced(rig, 1).FindAll(e => e.Kind == LimitKind.ThermalI2t));
        var w = new System.Buffers.ArrayBufferWriter<byte>();
        rig.RedNet.Islands.Of(rig.RedNet.IslandOf(rig.RedProbe)).Snapshot(w);
        var blob = w.WrittenSpan.ToArray();

        rig.RedNet.Solve(new TickClock(2, 0.5));               // 300 ≥ 250 ⇒ trips
        var first = DrainReduced(rig, 2).FindAll(e => e.Kind == LimitKind.ThermalI2t);
        Assert.Single(first);
        Assert.Equal(new SegmentKey(0xA000_0000_0000_0000UL, 11), first[0].Seg);

        var res = rig.RedNet.Islands.Of(rig.RedNet.IslandOf(rig.RedProbe)).Restore(blob);
        Assert.True(res.Matched >= 1, "the envelope unit must restore");
        rig.RedNet.Solve(new TickClock(3, 0.5));               // 150 + 150 again
        var again = DrainReduced(rig, 3).FindAll(e => e.Kind == LimitKind.ThermalI2t);
        Assert.Single(again);
        Assert.Equal(new SegmentKey(0xA000_0000_0000_0000UL, 11), again[0].Seg);
    }

    // ------------------------------------------------------ seeded random chains

    [Theory]
    [InlineData(1)]
    [InlineData(7)]
    [InlineData(23)]
    [InlineData(101)]
    [InlineData(4242)]
    [InlineData(90210)]
    public void Random_mixed_chain_limit_events_are_equivalent(int seed)
    {
        var rng = new Random(seed);
        var n = 2 + rng.Next(6);
        var mats = new List<Material>();
        for (var i = 0; i < n; i++) mats.Add(Palette[rng.Next(Palette.Length)]);

        // Current script: phases of steady drive — quiet (cooling), simmering
        // (between ratings: some raw segments over, some not), and hard overload
        // (heats the fuses toward a trip). Seeded, so trips land on varied ticks.
        double minRating = double.PositiveInfinity, maxRating = 0;
        foreach (var m in mats)
        {
            minRating = Math.Min(minRating, m.Rating);
            maxRating = Math.Max(maxRating, m.Rating);
        }
        var script = new List<double>();
        var phases = 2 + rng.Next(4);
        for (var p = 0; p < phases; p++)
        {
            var len = 3 + rng.Next(12);
            var kind = rng.Next(3);
            var amps = kind switch
            {
                0 => minRating * (0.2 + 0.6 * rng.NextDouble()),                    // quiet / cooling
                1 => minRating + (maxRating - minRating) * rng.NextDouble(),        // simmering
                _ => maxRating * (1.2 + rng.NextDouble()),                          // hard overload
            };
            for (var i = 0; i < len; i++) script.Add(amps);
        }
        // Always finish hot so most seeds reach a first trip.
        for (var i = 0; i < 25; i++) script.Add(maxRating * 1.5);

        var rig = Build(mats, script[0]);
        RunScript(rig, script);
    }
}
