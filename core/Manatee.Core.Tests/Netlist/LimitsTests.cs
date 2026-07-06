using System;
using Manatee.Core;

namespace Manatee.Core.Tests.Netlist;

/// <summary>
/// Post-solve limit evaluation (api.md §12; solver.md Limits). The solver never
/// mutates the circuit on a limit (R7): it scans the solved island, compares |I| /
/// |V| / P against each component's LimitSpec, integrates the i²t thermal
/// accumulator, and drops events into a fixed per-island ring (overflow counted).
/// The i²t fuse timing is hand-computed against the model in Netlist.Limits.cs.
/// </summary>
public sealed class LimitsTests
{
    private static ExternalKey K(ulong id) => new(id);

    private static Core.Netlist Transient(double dt)
        => new(new NetlistOptions
        {
            Profile = SolverProfile.Transient(dt),
            Wiring = WiringPolicy.ExplicitOnly(),
            Debug = DebugLevel.Asserts,
        });

    [Fact]
    public void Overcurrent_event_fires_with_the_offending_component_and_observed_value()
    {
        // 10 V across 1 Ω ⇒ 10 A through the resistor; MaxCurrent = 5 A ⇒ OverCurrent.
        const double dt = 0.1;
        var net = Transient(dt);
        NodeId a, g; ResistorId r;
        using (var e = net.Edit())
        {
            a = e.AddNode(K(1)); g = e.AddReferenceNode(K(2));
            e.AddVoltageSource(a, g, 10.0, K(20));
            r = e.AddResistor(a, g, 1.0, K(10), new LimitSpec(5.0, 0.0, 0.0, default));
        }
        net.Solve(new TickClock(7, dt));

        Span<LimitEvent> evs = stackalloc LimitEvent[8];
        var n = net.Islands.Of(a).DrainLimitEvents(evs);
        Assert.True(n >= 1, "expected an OverCurrent event");
        var ev = evs[0];
        Assert.Equal(LimitKind.OverCurrent, ev.Kind);
        Assert.Equal(r.AsRef(), ev.Source);
        Assert.Equal(10.0, ev.Observed, 3);
        Assert.Equal(5.0, ev.Threshold, 6);
        Assert.Equal(7, ev.TickIndex);

        // Drain empties the ring (call-again-until-0 contract).
        Assert.Equal(0, net.Islands.Of(a).DrainLimitEvents(evs));
    }

    [Fact]
    public void Overvoltage_and_overpower_are_independent_classes()
    {
        // 10 V, 1 Ω, 10 A, 100 W. MaxVoltage 5 V and MaxPower 50 W both trip.
        const double dt = 0.1;
        var net = Transient(dt);
        NodeId a, g;
        using (var e = net.Edit())
        {
            a = e.AddNode(K(1)); g = e.AddReferenceNode(K(2));
            e.AddVoltageSource(a, g, 10.0, K(20));
            e.AddResistor(a, g, 1.0, K(10), new LimitSpec(0.0, 5.0, 50.0, default));
        }
        net.Solve(new TickClock(1, dt));

        Span<LimitEvent> evs = stackalloc LimitEvent[8];
        var n = net.Islands.Of(a).DrainLimitEvents(evs);
        var sawV = false; var sawP = false;
        for (var i = 0; i < n; i++)
        {
            if (evs[i].Kind == LimitKind.OverVoltage) sawV = true;
            if (evs[i].Kind == LimitKind.OverPower) sawP = true;
        }
        Assert.True(sawV, "expected OverVoltage");
        Assert.True(sawP, "expected OverPower");
    }

    [Fact]
    public void No_events_when_within_limits()
    {
        const double dt = 0.1;
        var net = Transient(dt);
        NodeId a, g;
        using (var e = net.Edit())
        {
            a = e.AddNode(K(1)); g = e.AddReferenceNode(K(2));
            e.AddVoltageSource(a, g, 10.0, K(20));
            e.AddResistor(a, g, 1.0, K(10), new LimitSpec(50.0, 50.0, 500.0, default));
        }
        net.Solve(new TickClock(1, dt));
        Span<LimitEvent> evs = stackalloc LimitEvent[8];
        Assert.Equal(0, net.Islands.Of(a).DrainLimitEvents(evs));
    }

    [Fact]
    public void I2t_fuse_at_twice_rating_pops_at_the_hand_computed_time()
    {
        // The worked example from Netlist.Limits.cs: rating 10 A, MeltI2t 300 A²·s.
        // A steady 20 A (2× rating) heats at (20² − 10²) = 300 A²/s, so the melting
        // integral reaches 300 at t = 300/300 = 1.0 s. With dt = 0.1 s the accumulator
        // is 300·k·dt after k ticks, crossing 300 on the 10th tick (t = 1.0 s).
        const double dt = 0.1;
        var net = Transient(dt);
        NodeId a, g;
        using (var e = net.Edit())
        {
            a = e.AddNode(K(1)); g = e.AddReferenceNode(K(2));
            e.AddVoltageSource(a, g, 20.0, K(20));            // 20 V / 1 Ω = 20 A = 2× rating
            e.AddResistor(a, g, 1.0, K(10), new LimitSpec(10.0, 0.0, 0.0, new I2tParams(300.0, 100.0)));
        }

        var popTick = -1;
        Span<LimitEvent> evs = stackalloc LimitEvent[8];
        for (var k = 1; k <= 20 && popTick < 0; k++)
        {
            net.Solve(new TickClock(k, dt));
            var n = net.Islands.Of(a).DrainLimitEvents(evs);
            for (var i = 0; i < n; i++)
                if (evs[i].Kind == LimitKind.ThermalI2t)
                {
                    popTick = k;
                    Assert.True(evs[i].I2tFraction >= 1.0, $"I2tFraction {evs[i].I2tFraction} should be ≥ 1 at the pop");
                    break;
                }
            // The OverCurrent event fires every tick too (20 A > 10 A rating) — expected.
        }
        // Accumulator reaches 300 exactly at k = 10 (t = 1.0 s).
        Assert.Equal(10, popTick);
    }

    [Fact]
    public void I2t_below_rating_does_not_trip()
    {
        // 8 A < 10 A rating: the accumulator only decays, never reaching MeltI2t.
        const double dt = 0.1;
        var net = Transient(dt);
        NodeId a, g;
        using (var e = net.Edit())
        {
            a = e.AddNode(K(1)); g = e.AddReferenceNode(K(2));
            e.AddVoltageSource(a, g, 8.0, K(20));
            e.AddResistor(a, g, 1.0, K(10), new LimitSpec(10.0, 0.0, 0.0, new I2tParams(300.0, 100.0)));
        }
        Span<LimitEvent> evs = stackalloc LimitEvent[8];
        var sawThermal = false;
        for (var k = 1; k <= 50; k++)
        {
            net.Solve(new TickClock(k, dt));
            var n = net.Islands.Of(a).DrainLimitEvents(evs);
            for (var i = 0; i < n; i++) if (evs[i].Kind == LimitKind.ThermalI2t) sawThermal = true;
        }
        Assert.False(sawThermal, "an 8 A load under a 10 A rating must never pop the i²t fuse");
    }
}
