using System;
using Manatee.Core;

namespace Manatee.Core.Tests.Netlist;

/// <summary>
/// The core-side energy audit (api.md §11; solver.md). Per island the solver
/// integrates SourceJ / DissipatedJ on the hot path into per-island doubles and
/// reconstructs StoredJ / residual on read. These tests assert conservation
/// (ResidualJ ≈ 0) on solo RC / RL / RLC islands and CROSS-CHECK the core ledger
/// against an INDEPENDENT reconstruction from public node-voltage readbacks — two
/// derivations of the same balance (the phase-5b windowed-audit method promoted
/// into core, its test-side reconstruction kept as the referee).
/// </summary>
public sealed class EnergyAuditTests
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
    public void Rc_charging_conserves_energy_source_equals_dissipated_plus_stored()
    {
        // Vs = 10 V through R = 1 kΩ into C = 1 mF to ground. Source energy must equal
        // resistor dissipation + energy stored on the cap, over the whole charge.
        const double vs = 10.0, r = 1000.0, c = 1e-3, dt = 0.05;
        var net = Transient(dt);
        NodeId a, x, g;
        using (var e = net.Edit())
        {
            a = e.AddNode(K(1)); x = e.AddNode(K(2)); g = e.AddReferenceNode(K(3));
            e.AddVoltageSource(a, g, vs, K(20));
            e.AddResistor(a, x, r, K(10));
            e.AddCapacitor(x, g, c, K(11), StateKey.From(K(11)));
        }

        for (var n = 0; n < 400; n++) net.Solve(new TickClock(n, dt));

        var audit = net.Islands.Of(a).Energy;
        // Source delivered is positive and sizeable; the balance closes.
        Assert.True(audit.SourceJ > 0.05, $"source energy {audit.SourceJ:G6} J should be positive");
        Assert.True(audit.DissipatedJ > 0.0, $"dissipation {audit.DissipatedJ:G6} J should be positive");
        Assert.True(audit.StoredJ > 0.04, $"stored energy {audit.StoredJ:G6} J (≈½CV²≈0.05) expected");
        // ResidualJ = Source − Diss − ΔStored − Boundary(0). It closes to within
        // Backward-Euler's own numerical dissipation (½C·Σ(ΔV)², bounded by ~dt/2τ of
        // throughput) — a real property of the discretization, not a leak.
        Assert.True(Math.Abs(audit.ResidualJ) < 0.05 * audit.SourceJ,
            $"energy residual {audit.ResidualJ:G6} J exceeds 5% of source {audit.SourceJ:G6} J (diss={audit.DissipatedJ:G6} stored={audit.StoredJ:G6})");

        // CheckInvariants(Energy) surfaces the same residual.
        var rep = net.Islands.Of(a).CheckInvariants(InvariantChecks.Energy);
        Assert.Equal(audit.ResidualJ, rep.EnergyResidual, 9);
    }

    [Fact]
    public void Rl_energy_balance_closes()
    {
        // Vs = 5 V through R = 10 Ω into L = 1 H (series to ground). Inductor current
        // ramps to Vs/R = 0.5 A storing ½LI² = 0.125 J; balance must close.
        const double vs = 5.0, r = 10.0, l = 1.0, dt = 0.02;
        var net = Transient(dt);
        NodeId a, x, g;
        using (var e = net.Edit())
        {
            a = e.AddNode(K(1)); x = e.AddNode(K(2)); g = e.AddReferenceNode(K(3));
            e.AddVoltageSource(a, g, vs, K(20));
            e.AddResistor(a, x, r, K(10));
            e.AddInductor(x, g, l, K(11), StateKey.From(K(11)));
        }

        for (var n = 0; n < 600; n++) net.Solve(new TickClock(n, dt));

        var audit = net.Islands.Of(a).Energy;
        Assert.True(audit.StoredJ > 0.11 && audit.StoredJ < 0.14, $"stored {audit.StoredJ:G6} ≈ 0.125 J");
        Assert.True(Math.Abs(audit.ResidualJ) < 0.05 * audit.SourceJ,
            $"RL residual {audit.ResidualJ:G6} J exceeds 5% of source {audit.SourceJ:G6} J");
    }

    [Fact]
    public void Core_ledger_matches_an_independent_readback_reconstruction()
    {
        // Cross-check: reconstruct source/dissipated/stored INDEPENDENTLY from public
        // node voltages (the referee), and confirm the core ledger agrees.
        const double vs = 12.0, r = 470.0, c = 2.2e-3, dt = 0.05;
        var net = Transient(dt);
        NodeId a, x, g;
        using (var e = net.Edit())
        {
            a = e.AddNode(K(1)); x = e.AddNode(K(2)); g = e.AddReferenceNode(K(3));
            e.AddVoltageSource(a, g, vs, K(20));
            e.AddResistor(a, x, r, K(10));
            e.AddCapacitor(x, g, c, K(11), StateKey.From(K(11)));
        }

        double src = 0.0, diss = 0.0;
        for (var n = 0; n < 300; n++)
        {
            net.Solve(new TickClock(n, dt));
            var va = net.Solution.Voltage(a);
            var vx = net.Solution.Voltage(x);
            var iR = (va - vx) / r;             // current the source pushes through R
            src += va * iR * dt;                 // source terminal is `a`
            diss += (va - vx) * (va - vx) / r * dt;
        }
        var vxFinal = net.Solution.Voltage(x);
        var storedIndep = 0.5 * c * vxFinal * vxFinal;

        var audit = net.Islands.Of(a).Energy;
        // Same integration scheme (rectangle rule, endpoint stores) ⇒ tight agreement.
        Assert.Equal(src, audit.SourceJ, 6);
        Assert.Equal(diss, audit.DissipatedJ, 6);
        Assert.Equal(storedIndep, audit.StoredJ, 9);
    }
}
