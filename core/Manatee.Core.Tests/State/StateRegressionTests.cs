using System;
using System.Buffers;
using Manatee.Core;

namespace Manatee.Core.Tests.State;

/// <summary>
/// Regressions for state fixes surfaced by review: the FromCanonical numeric-pass
/// gate (a loaded static island must actually solve, not read 0), and the evolved
/// state the memento previously dropped (i²t melting integral, converter DC-link).
/// Plus the per-substep limit evaluation that makes AC fuses melt at all.
/// </summary>
public sealed class StateRegressionTests
{
    private static ExternalKey K(ulong id) => new(id);

    private static NetlistOptions Transient(double dt) => new()
    {
        Profile = SolverProfile.Transient(dt),
        Wiring = WiringPolicy.ExplicitOnly(),
        Debug = DebugLevel.Asserts,
    };

    private static NetlistOptions Mixed(double dt) => new()
    {
        Profile = SolverProfile.Mixed(dt),
        Wiring = WiringPolicy.ExplicitOnly(),
        Partitioning = PartitioningMode.SelfPartitioned,
        Debug = DebugLevel.Asserts,
    };

    private static byte[] Canonical(Core.Netlist n)
    {
        var w = new ArrayBufferWriter<byte>();
        n.SaveCanonical(w);
        return w.WrittenSpan.ToArray();
    }

    [Fact]
    public void FromCanonical_static_dc_island_solves_and_reads_its_voltage()
    {
        // 20 V across 1 Ω: a static, non-transient island. FromCanonical nulls every
        // island runtime and restores status Ready; the numeric-pass gate must still
        // force a build+solve (else V(a) reads 0 forever after load).
        const double dt = 0.1;
        var opts = Transient(dt);
        var net = new Core.Netlist(opts);
        using (var e = net.Edit())
        {
            var a = e.AddNode(K(1)); var g = e.AddReferenceNode(K(2));
            e.AddVoltageSource(a, g, 20.0, K(20));
            e.AddResistor(a, g, 1.0, K(10));
        }
        net.SolveOperatingPoint();

        var net2 = Core.Netlist.FromCanonical(Canonical(net), opts);
        net2.Solve(new TickClock(1, dt));   // the path that used to silently skip the island
        Assert.True(net2.TryResolveNode(K(1), out var a2));
        Assert.Equal(20.0, net2.Solution.Voltage(a2), 6);
    }

    [Fact]
    public void Canonical_preserves_the_i2t_melting_integral_so_trip_timing_survives()
    {
        // rating 10 A, MeltI2t 300, driven at 20 A ⇒ +30 A²·s/tick; trips at tick 10.
        const double dt = 0.1;
        var opts = Transient(dt);
        var net = new Core.Netlist(opts);
        using (var e = net.Edit())
        {
            var a = e.AddNode(K(1)); var g = e.AddReferenceNode(K(2));
            e.AddVoltageSource(a, g, 20.0, K(20));
            e.AddResistor(a, g, 1.0, K(10), new LimitSpec(10.0, 0.0, 0.0, new I2tParams(300.0, 100.0)));
        }
        // Heat to ~150 A²·s (half way), then save mid-melt and reload.
        for (var k = 1; k <= 5; k++) net.Solve(new TickClock(k, dt));
        var net2 = Core.Netlist.FromCanonical(Canonical(net), opts);

        // The reloaded fuse must trip within the SAME 5 remaining ticks (accumulator
        // preserved), not restart from cold and take 10.
        var tripped = false;
        Span<LimitEvent> evs = stackalloc LimitEvent[8];
        for (var k = 6; k <= 10 && !tripped; k++)
        {
            net2.Solve(new TickClock(k, dt));
            var n = net2.Islands.Of(ResolveA(net2)).DrainLimitEvents(evs);
            for (var i = 0; i < n; i++) if (evs[i].Kind == LimitKind.ThermalI2t) tripped = true;
        }
        Assert.True(tripped, "a mid-melt fuse must keep its i²t integral across a canonical round-trip");
    }

    [Fact]
    public void Canonical_preserves_a_settled_converter_dc_link_no_cold_restart()
    {
        const double dt = 0.05;
        var opts = Mixed(dt);
        var net = new Core.Netlist(opts);
        var eff = EfficiencyCurve.Points((0.25, 0.80), (0.5, 0.90), (1.0, 0.95));
        CouplerId c;
        using (var e = net.Edit())
        {
            var aPos = e.AddNode(K(1)); var gnd = e.AddReferenceNode(K(2));
            var bPos = e.AddNode(K(3)); var bGnd = e.AddReferenceNode(K(4));
            e.AddVoltageSource(aPos, gnd, 100.0, K(10));
            e.AddResistor(bPos, bGnd, 5.0, K(11));
            c = e.AddCoupler(CouplerSpec.ConverterTwoPort(eff, 0.01, 50.0, 1000.0),
                new CouplerPorts(aPos, gnd, bPos, bGnd), K(20), StateKey.From(K(20)));
        }
        net.SolveOperatingPoint();
        net.Reconfigure(c, CouplerState.Closed);
        for (var i = 0; i < 400; i++) net.Solve(new TickClock(1 + i, dt));

        Assert.True(net.TryResolveNode(K(3), out var bPos1));
        var vSteady = net.Solution.Voltage(bPos1);
        Assert.True(vSteady > 5.0, "the converter output should have charged");

        var net2 = Core.Netlist.FromCanonical(Canonical(net), opts);
        net2.Solve(new TickClock(1000, dt));   // one tick from the reloaded state
        Assert.True(net2.TryResolveNode(K(3), out var bPos2));
        var vReload = net2.Solution.Voltage(bPos2);

        // The DC-link cap history was serialized, so the reloaded output resumes near
        // steady state rather than cold-starting at 0 with a fresh charge transient.
        Assert.True(Math.Abs(vReload - vSteady) < 0.05 * vSteady + 1.0,
            $"reloaded V_B={vReload} should resume near steady V_B={vSteady}, not cold-restart");
    }

    [Fact]
    public void Ac_fuse_melts_the_per_substep_i2t_integral_is_not_phase_blind()
    {
        // freq·dt = 1 (freq 10 Hz, dt 0.1 s): every tick boundary lands at a sine zero
        // crossing, so a per-TICK limit scan would observe |I| = 0 forever and never heat.
        // A 30 V amp across 1 Ω is ~21 A RMS (RMS² = 450 ≫ rating² = 100) and must melt.
        const double dt = 0.1;
        var opts = Mixed(dt);
        var net = new Core.Netlist(opts);
        using (var e = net.Edit())
        {
            var a = e.AddNode(K(1)); var g = e.AddReferenceNode(K(2));
            e.AddSineSource(a, g, new SineDrive(30.0, 10.0, 0.0), K(20), StateKey.From(K(20)));
            e.AddResistor(a, g, 1.0, K(10), new LimitSpec(10.0, 0.0, 0.0, new I2tParams(300.0, 100.0)));
        }
        net.SolveOperatingPoint();

        var melted = false;
        Span<LimitEvent> evs = stackalloc LimitEvent[8];
        for (var k = 1; k <= 200 && !melted; k++)
        {
            net.Solve(new TickClock(k, dt));
            Assert.True(net.TryResolveNode(K(1), out var a1));
            var n = net.Islands.Of(a1).DrainLimitEvents(evs);
            for (var i = 0; i < n; i++) if (evs[i].Kind == LimitKind.ThermalI2t) melted = true;
        }
        Assert.True(melted, "an AC overload must accumulate i²t per substep and pop the fuse");
    }

    private static NodeId ResolveA(Core.Netlist n)
    {
        Assert.True(n.TryResolveNode(K(1), out var a));
        return a;
    }
}
