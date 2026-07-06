using System;
using Manatee.Core;

namespace Manatee.Core.Tests.Netlist;

/// <summary>
/// Instantaneous limit events coalesce to AT MOST ONE event per (component, kind)
/// per tick on subcycled islands, carrying the WORST observed substep value
/// (api.md §12 ruling 2026-07-06). Before the fix, a sustained AC overload emitted
/// one event per substep — N per tick — saturating the 16-slot ring with a single
/// component and dropping everyone else's events. The evaluation itself stays
/// per-substep (i²t integrates the true ∫I²dt; the checks see the waveform peak);
/// only the emission coalesces.
/// </summary>
public sealed class LimitCoalescingTests
{
    private static ExternalKey K(ulong id) => new(id);

    private static Core.Netlist Mixed(double dt)
        => new(new NetlistOptions
        {
            Profile = SolverProfile.Mixed(dt),
            Wiring = WiringPolicy.ExplicitOnly(),
            Debug = DebugLevel.Asserts,
        });

    [Fact]
    public void Sustained_ac_overload_emits_one_overcurrent_event_per_tick()
    {
        // The finder's repro: 1 Ω resistor limited at 1 A across a 12 V 5 Hz sine —
        // over-limit on essentially every substep (5 substeps/tick at dt = 0.05).
        var net = Mixed(0.05);
        NodeId a, g;
        using (var e = net.Edit())
        {
            a = e.AddNode(K(1)); g = e.AddReferenceNode(K(2));
            e.AddSineSource(a, g, new SineDrive(12.0, 5.0, 0.0), K(10), StateKey.From(K(10)));
            e.AddResistor(a, g, 1.0, K(11), new LimitSpec(1.0, 0.0, 0.0, default));
        }
        net.SolveOperatingPoint();

        var evs = new LimitEvent[32];
        for (var tick = 0; tick < 8; tick++)
        {
            net.Solve(new TickClock(tick, 0.05));
            var n = net.Islands.Of(a).DrainLimitEvents(evs, out var dropped);
            Assert.Equal(0, dropped);
            var over = 0; var worst = 0.0;
            for (var i = 0; i < n; i++)
                if (evs[i].Kind == LimitKind.OverCurrent) { over++; worst = evs[i].Observed; }
            Assert.True(over <= 1, $"tick {tick}: {over} OverCurrent events (must coalesce to ≤ 1/tick)");
            if (over == 1)
            {
                // The coalesced event carries the WORST substep value: well above the
                // tick-boundary sample and never above the 12 A physical peak.
                Assert.True(worst > 1.0 && worst <= 12.0 + 1e-9,
                    $"tick {tick}: coalesced Observed {worst} A out of range");
            }
        }
    }

    [Fact]
    public void Coalescing_keeps_other_components_events_visible_under_flood()
    {
        // One island: a heavily over-limit resistor (would flood 5 events/tick
        // uncoalesced) plus a second, mildly over-limit resistor. Both must surface
        // every tick — the flood victim scenario the §12 ruling closed.
        var net = Mixed(0.05);
        NodeId a, g;
        ResistorId r2;
        using (var e = net.Edit())
        {
            a = e.AddNode(K(1)); g = e.AddReferenceNode(K(2));
            e.AddSineSource(a, g, new SineDrive(12.0, 5.0, 0.0), K(10), StateKey.From(K(10)));
            e.AddResistor(a, g, 1.0, K(11), new LimitSpec(0.5, 0.0, 0.0, default));   // the flooder
            r2 = e.AddResistor(a, g, 2.0, K(12), new LimitSpec(1.0, 0.0, 0.0, default));
        }
        net.SolveOperatingPoint();

        var evs = new LimitEvent[32];
        for (var tick = 0; tick < 6; tick++)
        {
            net.Solve(new TickClock(tick, 0.05));
            var n = net.Islands.Of(a).DrainLimitEvents(evs, out var dropped);
            Assert.Equal(0, dropped);
            var sawR2 = false;
            for (var i = 0; i < n; i++)
                if (evs[i].Kind == LimitKind.OverCurrent && net.TryResolve(K(12), out var c)
                    && evs[i].Source.Slot == c.Slot) sawR2 = true;
            Assert.True(sawR2, $"tick {tick}: second component's OverCurrent was drowned out");
        }
    }
}
