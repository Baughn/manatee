using Manatee.Core;
using Manatee.Oracle;

namespace Manatee.Core.Tests.Oracle.Analyses;

/// <summary>
/// Subcycled-AC oracle wave (api.md §5, §11; testing-strategy.md): a Mixed netlist's
/// AC island subcycles N substeps per tick, and the emitted deck runs ngspice at that
/// substep dt under Backward Euler. Includes the design's flagship case — a 5 Hz island
/// at N = 5 substeps per 50 ms tick — pinned explicitly. Filter out locally:
/// dotnet test --filter Category!=Oracle
/// </summary>
[Trait("Category", "Oracle")]
public sealed class AcOracleTests
{
    private static ExternalKey K(ulong id) => new(id);

    private static Core.Netlist Mixed(double tickDt, int samples = 20)
        => new(new NetlistOptions
        {
            Profile = SolverProfile.Mixed(tickDt, samples),
            Wiring = WiringPolicy.ExplicitOnly(),
            Debug = DebugLevel.Asserts,
        });

    private static void Step(Core.Netlist net, int ticks, double tickDt)
    {
        for (var i = 0; i < ticks; i++) net.Solve(new TickClock(i, tickDt));
    }

    [Fact]
    public void Sine_resistive_divider_tracks_ngspice()
    {
        // Purely resistive ⇒ the node voltage is an algebraic fraction of the source at
        // every instant, so integrator choice is irrelevant: a tight match. N = 5 ⇒
        // phase advances exactly π/2 per tick; 21 ticks (≡1 mod 4) lands on a +peak.
        const double tickDt = 0.05;
        const int ticks = 21;
        var net = Mixed(tickDt);
        NodeId a, b, g;
        using (var e = net.Edit())
        {
            a = e.AddNode(K(1)); b = e.AddNode(K(2)); g = e.AddReferenceNode(K(3));
            e.AddSineSource(a, g, new SineDrive(10.0, 5.0, 0.0), K(20), StateKey.From(K(20)));
            e.AddResistor(a, b, 1000.0, K(10));
            e.AddResistor(b, g, 3000.0, K(11));
        }
        OracleHarness.AssertTranMatches(net, net.IslandOf(a), tickDt / 5.0, ticks * tickDt,
            () => Step(net, ticks, tickDt), relTol: 2e-3, absTol: 1e-4);
    }

    [Fact]
    public void Sine_rc_phase_shift_tracks_ngspice()
    {
        // Sine → R → C to ground: the capacitor node lags the source. Backward-Euler on
        // both sides at the matched substep dt ⇒ the lagged waveform agrees.
        const double tickDt = 0.05;
        const int ticks = 21;
        var net = Mixed(tickDt);
        NodeId a, x, g;
        using (var e = net.Edit())
        {
            a = e.AddNode(K(1)); x = e.AddNode(K(2)); g = e.AddReferenceNode(K(3));
            e.AddSineSource(a, g, new SineDrive(10.0, 5.0, 0.0), K(20), StateKey.From(K(20)));
            e.AddResistor(a, x, 1000.0, K(10));
            e.AddCapacitor(x, g, 5e-5, K(11), StateKey.From(K(11)));   // R·C = 0.05 s ≈ ω⁻¹: visible lag
        }
        OracleHarness.AssertTranMatches(net, net.IslandOf(a), tickDt / 5.0, ticks * tickDt,
            () => Step(net, ticks, tickDt), relTol: 5e-3, absTol: 2e-3);
    }

    [Fact]
    public void Flagship_5hz_island_at_n5_substeps_per_50ms_tick()
    {
        // The design's flagship subcycle (api.md §5): 5 Hz sine, 50 ms tick, 20 samples
        // per cycle ⇒ N = ceil(5·20·0.05) = 5 substeps, substep dt = 10 ms. Pin the plan
        // explicitly, then oracle-match a loaded RL AC island at that subcycle.
        const double tickDt = 0.05;
        const int ticks = 21;
        var net = Mixed(tickDt);
        NodeId a, x, g;
        using (var e = net.Edit())
        {
            a = e.AddNode(K(1)); x = e.AddNode(K(2)); g = e.AddReferenceNode(K(3));
            e.AddSineSource(a, g, new SineDrive(12.0, 5.0, 0.0), K(20), StateKey.From(K(20)));
            e.AddResistor(a, x, 50.0, K(10));
            e.AddInductor(x, g, 1.0, K(11), StateKey.From(K(11)));
        }
        net.Solve(new TickClock(0, tickDt));
        var plan = net.Islands.Of(a).Plan;
        Assert.Equal(5, plan.Substeps);
        Assert.Equal(0.01, plan.SubstepDt, 12);

        // Continue from tick 1 (already stepped tick 0); emit captures current state, so
        // rebuild a fresh netlist to keep the from-cold IC = 0 invariant of the harness.
        var fresh = Mixed(tickDt);
        NodeId a2, x2, g2;
        using (var e = fresh.Edit())
        {
            a2 = e.AddNode(K(1)); x2 = e.AddNode(K(2)); g2 = e.AddReferenceNode(K(3));
            e.AddSineSource(a2, g2, new SineDrive(12.0, 5.0, 0.0), K(20), StateKey.From(K(20)));
            e.AddResistor(a2, x2, 50.0, K(10));
            e.AddInductor(x2, g2, 1.0, K(11), StateKey.From(K(11)));
        }
        OracleHarness.AssertTranMatches(fresh, fresh.IslandOf(a2), tickDt / 5.0, ticks * tickDt,
            () => Step(fresh, ticks, tickDt), relTol: 5e-3, absTol: 2e-3);
    }
}
