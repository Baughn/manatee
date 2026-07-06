using System;
using Manatee.Core;

namespace Manatee.Core.Tests.Netlist;

/// <summary>
/// Boundary couplings made real (solver.md Islands; api.md §7, §11). Two islands
/// joined by a DecouplingTransformer / ConverterTwoPort form ONE lockstep scheduling
/// unit; per-substep amplitude+phase exchange with relaxation; per-boundary energy
/// ledgers conserved by construction; transfer clamped to what the source delivered;
/// faulted propagation; closed-breaker signed through-flow.
///
/// <para>Behavioral coupling has no ngspice oracle (testing-strategy.md "What ngspice
/// cannot oracle") — correctness here is invariants + hand-computed steady states with
/// the arithmetic in the comments.</para>
/// </summary>
public sealed class BoundaryCouplerTests
{
    private static ExternalKey K(ulong id) => new(id);

    private static Core.Netlist Net()
        => new(new NetlistOptions
        {
            Profile = SolverProfile.Mixed(0.05),
            Wiring = WiringPolicy.ExplicitOnly(),
            Partitioning = PartitioningMode.SelfPartitioned,
            Debug = DebugLevel.Asserts,
        });

    // A two-island transformer rig: island A is a stiff 10 V source; island B is a
    // resistive load. The DecouplingTransformer (turns ratio n) bridges them.
    private sealed class XfmrRig
    {
        public Core.Netlist Net = null!;
        public NodeId APos, AGnd, BPos, BGnd;
        public VSourceId Src;
        public ResistorId Load;
        public CouplerId Coupler;
    }

    private static XfmrRig BuildTransformer(double vSource, double turns, double loadOhms,
        double alpha = 0.5, double magnetizingOhms = 0.0)
    {
        var net = Net();
        var r = new XfmrRig { Net = net };
        using (var e = net.Edit())
        {
            r.APos = e.AddNode(K(1)); r.AGnd = e.AddReferenceNode(K(2));
            r.BPos = e.AddNode(K(3)); r.BGnd = e.AddReferenceNode(K(4));
            r.Src = e.AddVoltageSource(r.APos, r.AGnd, vSource, K(10));
            r.Load = e.AddResistor(r.BPos, r.BGnd, loadOhms, K(11));
            r.Coupler = e.AddCoupler(
                CouplerSpec.DecouplingTransformer(new TransformerParams(turns, 0.0, magnetizingOhms), alpha),
                new CouplerPorts(r.APos, r.AGnd, r.BPos, r.BGnd), K(20), StateKey.From(K(20)));
        }
        net.SolveOperatingPoint();
        net.Reconfigure(r.Coupler, CouplerState.Closed);   // enable the exchange
        return r;
    }

    private static void Settle(Core.Netlist net, int ticks, double dt = 0.05)
    {
        for (var t = 0; t < ticks; t++) net.Solve(new TickClock(t + 1, dt));
    }

    // ------------------------------------------------------ steady amplitude ratio

    [Fact]
    public void Transformer_settles_to_turns_ratio_and_conserves_power()
    {
        // n = 2, Vs = 10 V, load R = 5 Ω on the secondary.
        // Steady state (behavioral, frequency-agnostic): V_B = V_A / n = 10/2 = 5 V;
        // i_B = V_B / R = 5/5 = 1 A; i_A = i_B / n = 0.5 A (ampere-turns).
        // P_A = V_A·i_A = 10·0.5 = 5 W = V_B·i_B = 5·1 = 5 W  ⇒ conserved.
        var r = BuildTransformer(vSource: 10.0, turns: 2.0, loadOhms: 5.0);
        Settle(r.Net, 400);

        var view = r.Net.Islands.Of(r.APos).Exchange(r.Coupler);
        // Amplitude ratio A:B == turns ratio, within relaxation tolerance.
        Assert.Equal(2.0, view.AmplitudeA / view.AmplitudeB, 3);
        Assert.Equal(10.0, view.AmplitudeA, 3);
        Assert.Equal(5.0, view.AmplitudeB, 3);

        // Secondary port voltage really is V_A/n on island B.
        Assert.Equal(5.0, r.Net.Solution.Voltage(r.BPos), 3);

        // Power in ≈ power out (ideal transformer, no modeled loss): the instantaneous
        // through-power settles to 5 W on both sides.
        Assert.Equal(5.0, view.PowerA2B, 2);
        Assert.True(r.Net.TryReadConverterPowers(r.Coupler, out var pIn, out var pOut));
        Assert.Equal(5.0, pIn, 2);
        Assert.Equal(5.0, pOut, 2);
        Assert.Equal(pIn, pOut, 2);
    }

    [Fact]
    public void Transformer_ledger_conserves_energy_in_equals_out_plus_losses()
    {
        var r = BuildTransformer(vSource: 10.0, turns: 2.0, loadOhms: 5.0);
        Settle(r.Net, 600);

        var led = r.Net.Islands.Of(r.APos).Ledger(r.Coupler);
        // Conserved by construction: In = Out + ModeledLoss + HeatDumped, residual ≈ 0.
        Assert.Equal(0.0, led.Residual, 9);
        // Ideal transformer ⇒ no modeled loss; all the residue is relaxation-lag heat.
        Assert.Equal(0.0, led.ModeledLossJ, 12);
        Assert.Equal(led.InJ - led.OutJ, led.HeatDumpedJ, 9);
        // Steady state: the lag heat is a tiny fraction of the throughput.
        Assert.True(led.InJ > 0.0);
        Assert.True(led.HeatDumpedJ >= 0.0);
        Assert.True(led.HeatDumpedJ < 0.02 * led.InJ, $"heat {led.HeatDumpedJ} vs in {led.InJ}");
    }

    // -------------------------------------------------------------- the clamp

    [Fact]
    public void Transformer_transfer_clamps_under_a_sudden_load_step()
    {
        // Settle, then STEP the secondary load (5 Ω → 1 Ω, a 5× current jump). Through
        // the relaxation transient the ledger must NEVER show free energy: OutJ ≤ InJ
        // and HeatDumpedJ ≥ 0 on EVERY tick (solver.md "clamps transfer to what its
        // source island actually delivered").
        var r = BuildTransformer(vSource: 10.0, turns: 2.0, loadOhms: 5.0);
        Settle(r.Net, 300);

        r.Net.Adjust(r.Load, 1.0);   // sudden load step (tier 2, dirties island B)

        var isl = r.Net.Islands.Of(r.APos);
        for (var t = 0; t < 200; t++)
        {
            r.Net.Solve(new TickClock(1000 + t, 0.05));
            var led = isl.Ledger(r.Coupler);
            Assert.True(led.HeatDumpedJ >= 0.0, $"heat went negative at t={t}: {led.HeatDumpedJ}");
            Assert.True(led.OutJ <= led.InJ + 1e-9, $"delivered more than received at t={t}: out={led.OutJ} in={led.InJ}");
            Assert.True(led.Residual is >= -1e-9 and <= 1e-9, $"residual drift at t={t}: {led.Residual}");
        }

        // After the step it re-settles to the turns ratio at the new load
        // (V_B = 5 V, i_B = 5 A, i_A = 2.5 A, P = 25 W both sides).
        var view = isl.Exchange(r.Coupler);
        Assert.Equal(2.0, view.AmplitudeA / view.AmplitudeB, 3);
        Assert.True(r.Net.TryReadConverterPowers(r.Coupler, out var pIn, out var pOut));
        Assert.Equal(25.0, pIn, 1);
        Assert.Equal(25.0, pOut, 1);
    }

    // ------------------------------------------------------- converter efficiency

    [Theory]
    [InlineData(5.0, 0.5, 0.90)]    // P_out = 50·(50/5)  = 500 W ⇒ fraction 0.5  ⇒ η 0.90
    [InlineData(2.5, 1.0, 0.95)]    // P_out = 50·(50/2.5) = 1000 W ⇒ fraction 1.0 ⇒ η 0.95
    [InlineData(10.0, 0.25, 0.80)]  // P_out = 50·(50/10) = 250 W ⇒ fraction 0.25 ⇒ η 0.80
    public void Converter_honors_efficiency_curve_at_load_points(double loadOhms, double fraction, double expectedEff)
    {
        // Curve breakpoints (0.25,0.80) (0.5,0.90) (1.0,0.95); B held at 50 V; rated
        // 1000 W. At steady state P_out/P_in must equal the curve efficiency exactly,
        // and P_in − P_out is the explicit modeled loss dumped as heat.
        var eff = EfficiencyCurve.Points((0.25, 0.80), (0.5, 0.90), (1.0, 0.95));
        var net = Net();
        NodeId aPos, aGnd, bPos, bGnd; CouplerId c;
        using (var e = net.Edit())
        {
            aPos = e.AddNode(K(1)); aGnd = e.AddReferenceNode(K(2));
            bPos = e.AddNode(K(3)); bGnd = e.AddReferenceNode(K(4));
            e.AddVoltageSource(aPos, aGnd, 100.0, K(10));
            e.AddResistor(bPos, bGnd, loadOhms, K(11));
            c = e.AddCoupler(CouplerSpec.ConverterTwoPort(eff, 0.0, outputVolts: 50.0, ratedWatts: 1000.0),
                new CouplerPorts(aPos, aGnd, bPos, bGnd), K(20), StateKey.From(K(20)));
        }
        net.SolveOperatingPoint();
        net.Reconfigure(c, CouplerState.Closed);
        Settle(net, 400);

        Assert.Equal(50.0, net.Solution.Voltage(bPos), 6);          // regulated setpoint held
        Assert.True(net.TryReadConverterPowers(c, out var pIn, out var pOut));
        var pOutExpected = 50.0 * (50.0 / loadOhms);
        Assert.Equal(pOutExpected, pOut, 3);
        Assert.Equal(fraction, pOut / 1000.0, 3);
        Assert.Equal(expectedEff, pOut / pIn, 4);                    // efficiency curve honored

        var led = net.Islands.Of(aPos).Ledger(c);
        Assert.Equal(0.0, led.Residual, 9);                          // conserved by construction
        Assert.True(led.ModeledLossJ > 0.0);                        // efficiency loss is EXPLICIT
        Assert.Equal(led.ModeledLossJ, led.HeatDumpedJ, 9);         // …and becomes heat
    }

    [Fact]
    public void Converter_heat_never_goes_negative_through_a_load_up_transient()
    {
        // Regression for the negative-heat bug: the B port is a regulated 50 V setpoint
        // source that delivers to island B immediately, while the A-side input draw is
        // exponentially relaxed and LAGS a rising load. When ModeledLoss was synthesized
        // as (P_in − P_out) it went NEGATIVE during the lag, so HeatDumpedJ = ModeledLossJ
        // dropped below zero — the ledger reported CREATED energy as negative heat and the
        // ≈0 Residual identity hid it. With ModeledLoss computed independently from the
        // curve (P_out·(1/η − 1) ≥ 0), HeatDumpedJ ≥ 0 is guaranteed BY CONSTRUCTION.
        var eff = EfficiencyCurve.Points((0.25, 0.80), (0.5, 0.90), (1.0, 0.95));
        var net = Net();
        NodeId aPos, bPos; ResistorId load; CouplerId c;
        using (var e = net.Edit())
        {
            aPos = e.AddNode(K(1)); var aGnd = e.AddReferenceNode(K(2));
            bPos = e.AddNode(K(3)); var bGnd = e.AddReferenceNode(K(4));
            e.AddVoltageSource(aPos, aGnd, 100.0, K(10));
            load = e.AddResistor(bPos, bGnd, 500.0, K(11));   // light load first
            c = e.AddCoupler(CouplerSpec.ConverterTwoPort(eff, 0.0, outputVolts: 50.0, ratedWatts: 1000.0),
                new CouplerPorts(aPos, aGnd, bPos, bGnd), K(20), StateKey.From(K(20)));
        }
        net.SolveOperatingPoint();
        net.Reconfigure(c, CouplerState.Closed);
        _ = bPos;
        Settle(net, 300);

        net.Adjust(load, 2.5);   // hard load-up step (500 Ω → 2.5 Ω): the pre-fix trough

        var isl = net.Islands.Of(aPos);
        for (var t = 0; t < 300; t++)
        {
            net.Solve(new TickClock(5000 + t, 0.05));
            var led = isl.Ledger(c);
            Assert.True(led.ModeledLossJ >= -1e-12, $"negative modeled loss at t={t}: {led.ModeledLossJ}");
            Assert.True(led.HeatDumpedJ >= -1e-12, $"negative heat (created energy) at t={t}: {led.HeatDumpedJ}");
            Assert.True(led.SurplusJ >= -1e-9, $"negative surplus at t={t}: {led.SurplusJ}");
        }

        // Re-settled at 2.5 Ω: P_out = 50·(50/2.5) = 1000 W ⇒ fraction 1.0 ⇒ η 0.95.
        // The modeled loss is the INDEPENDENT curve value P_out·(1/η−1), not In−Out.
        Assert.True(net.TryReadConverterPowers(c, out var pIn, out var pOut));
        Assert.Equal(1000.0, pOut, 1);
        Assert.Equal(0.95, pOut / pIn, 3);
        var expectedLossW = pOut * (1.0 / 0.95 - 1.0);   // ≈ 52.6 W dissipated in the converter
        Assert.Equal(expectedLossW, pIn - pOut, 1);
    }

    // ---------------------------------------------------- breaker signed through-flow

    [Fact]
    public void Closed_breaker_reports_signed_through_flow_that_flips_with_direction()
    {
        // A galvanic breaker merges its two sides into ONE island; the 1 mΩ bridge
        // carries the through-current. Exchange.PowerA2B is signed: source on A ⇒ A→B
        // (positive); move the source to B ⇒ B→A (negative). Magnitude ≈ 10·(10/5) = 20 W.
        var pA = BreakerPowerA2B(sourceOnA: true);
        var pB = BreakerPowerA2B(sourceOnA: false);

        Assert.True(pA > 0.0, $"A→B should be positive, was {pA}");
        Assert.True(pB < 0.0, $"B→A should be negative, was {pB}");
        Assert.Equal(20.0, pA, 1);
        Assert.Equal(-20.0, pB, 1);
    }

    private static double BreakerPowerA2B(bool sourceOnA)
    {
        var net = Net();
        NodeId aPos, aGnd, bPos, bGnd; CouplerId c;
        using (var e = net.Edit())
        {
            aPos = e.AddNode(K(1)); aGnd = e.AddReferenceNode(K(2));
            bPos = e.AddNode(K(3)); bGnd = e.AddNode(K(4));
            if (sourceOnA)
            {
                e.AddVoltageSource(aPos, aGnd, 10.0, K(10));
                e.AddResistor(bPos, bGnd, 5.0, K(11));
            }
            else
            {
                e.AddResistor(aPos, aGnd, 5.0, K(11));
                e.AddVoltageSource(bPos, bGnd, 10.0, K(10));
            }
            c = e.AddCoupler(CouplerSpec.Breaker(), new CouplerPorts(aPos, aGnd, bPos, bGnd), K(20), StateKey.From(K(20)));
        }
        net.SolveOperatingPoint();   // closed breaker ⇒ one merged island
        Assert.Equal(1, net.Islands.Count);
        return net.Islands.Of(aPos).Exchange(c).PowerA2B;
    }

    // ------------------------------------------------------- lockstep determinism

    [Fact]
    public void Unit_lockstep_is_bit_identical_across_runs()
    {
        // Same inputs twice ⇒ bit-identical outputs (determinism rule; the exchange
        // happens at fixed points inside the unit's substep loop).
        var (v1a, v1b, in1, out1, heat1) = RunTransformerToState();
        var (v2a, v2b, in2, out2, heat2) = RunTransformerToState();

        Assert.Equal(v1a, v2a);      // exact (no precision arg) — bit-for-bit
        Assert.Equal(v1b, v2b);
        Assert.Equal(in1, in2);
        Assert.Equal(out1, out2);
        Assert.Equal(heat1, heat2);
    }

    private static (double, double, double, double, double) RunTransformerToState()
    {
        var r = BuildTransformer(vSource: 10.0, turns: 2.0, loadOhms: 5.0);
        Settle(r.Net, 137);
        var led = r.Net.Islands.Of(r.APos).Ledger(r.Coupler);
        return (r.Net.Solution.Voltage(r.APos), r.Net.Solution.Voltage(r.BPos), led.InJ, led.OutJ, led.HeatDumpedJ);
    }

    // ---------------------------------------------------------- CollectDirty dedupe

    [Fact]
    public void CollectDirty_returns_one_handle_per_unit_at_the_lead()
    {
        var r = BuildTransformer(vSource: 10.0, turns: 2.0, loadOhms: 5.0);
        Settle(r.Net, 50);

        // Dirty BOTH member islands (source side and load side).
        r.Net.Drive(r.Src, 12.0);   // dirties island A
        r.Net.Adjust(r.Load, 4.0);  // dirties island B

        var buf = new IslandHandle[8];
        var n = r.Net.Islands.CollectDirty(buf);

        Assert.Equal(1, n);                                          // one unit, not two islands
        var lead = buf[0];
        // The lead is island A (smallest member-node ExternalKey).
        Assert.Equal(r.Net.IslandOf(r.APos), lead.Id);
    }

    // -------------------------------------------------------- non-lead Step routing

    [Fact]
    public void Step_on_a_non_lead_member_debug_asserts()
    {
        // Step on a non-lead member of a boundary-coupled unit debug-asserts: the client
        // must Step the unit LEAD (api.md §11). The test host translates the failing
        // Debug.Assert into a throwable exception; assert it fires with the guidance.
        var r = BuildTransformer(vSource: 10.0, turns: 2.0, loadOhms: 5.0);
        Settle(r.Net, 50);
        r.Net.Drive(r.Src, 11.0);

        var bId = r.Net.IslandOf(r.BPos);
        Assert.NotEqual(r.Net.IslandOf(r.APos), bId);   // island A is the lead; B is not
        var handle = r.Net.Islands.Of(bId);
        var ex = Assert.ThrowsAny<Exception>(() => handle.Step(new TickClock(1, 0.05)));
        Assert.Contains("non-lead member", ex.Message);
    }

    [Fact]
    public void Step_on_the_unit_lead_solves_every_member_in_lockstep()
    {
        // The complement of the assert: Stepping the LEAD drives the whole unit — both
        // the dirty source island and the dirty load island go Ready in one Step.
        var r = BuildTransformer(vSource: 10.0, turns: 2.0, loadOhms: 5.0);
        Settle(r.Net, 50);
        r.Net.Drive(r.Src, 11.0);    // dirties island A (lead)
        r.Net.Adjust(r.Load, 4.0);   // dirties island B (non-lead)

        var leadId = r.Net.IslandOf(r.APos);
        var bId = r.Net.IslandOf(r.BPos);
        Assert.Equal(IslandStatus.Dirty, r.Net.Islands.Of(leadId).Status);
        Assert.Equal(IslandStatus.Dirty, r.Net.Islands.Of(bId).Status);

        r.Net.Islands.Of(leadId).Step(new TickClock(1, 0.05));

        Assert.Equal(IslandStatus.Ready, r.Net.Islands.Of(leadId).Status);
        Assert.Equal(IslandStatus.Ready, r.Net.Islands.Of(bId).Status);
    }

    // ------------------------------------------------------ member fault RETRY + recovery

    [Fact]
    public void Coupled_member_faulted_on_entry_is_retried_without_event_churn_and_recovers()
    {
        // A unit member Faulted ON ENTRY must be RE-ADVANCED each tick (mirroring the solo
        // NumericSolveIsland path, which re-solves a faulted island and recovers on success),
        // NOT frozen. Before the fix, StepUnit seeded the in-loop skip flag from the entry
        // status, so an entry-faulted member was skipped in BOTH the substep and publish
        // loops every tick: it could never be retried, and the publish-loop recovery block
        // was unreachable dead code (no unit member ever emitted IslandRecovered).
        //
        // This exercises the retry path deterministically: island A holds two contradictory
        // ideal sources (5 V ∥ 10 V) ⇒ singular ⇒ Faulted. The coupler is Closed, so the
        // unit is active and StepUnit runs EVERY tick, re-advancing (retrying) the faulted
        // member each time. The gate on FaultIsland's wasFaulted arg must hold under the
        // retry: exactly ONE IslandFaulted is journaled across many ticks (no per-tick
        // re-emit churn), while island B keeps solving. Removing the contradiction recovers A.
        var net = Net();
        NodeId aPos, aGnd, bPos, bGnd; CouplerId c; VSourceId bad;
        using (var e = net.Edit())
        {
            aPos = e.AddNode(K(1)); aGnd = e.AddReferenceNode(K(2));
            bPos = e.AddNode(K(3)); bGnd = e.AddReferenceNode(K(4));
            e.AddVoltageSource(aPos, aGnd, 5.0, K(9));
            bad = e.AddVoltageSource(aPos, aGnd, 10.0, K(10));   // contradiction ⇒ island A singular
            e.AddResistor(bPos, bGnd, 5.0, K(11));
            c = e.AddCoupler(CouplerSpec.DecouplingTransformer(new TransformerParams(2.0), 0.5),
                new CouplerPorts(aPos, aGnd, bPos, bGnd), K(20), StateKey.From(K(20)));
        }
        net.SolveOperatingPoint();
        net.Reconfigure(c, CouplerState.Closed);

        var from = net.Journal.OpenCursorAt(0);
        for (var i = 0; i < 25; i++) net.Solve(new TickClock(1 + i, 0.05));   // A faulted + retried every tick
        Assert.Equal(IslandStatus.Faulted, net.Islands.Of(aPos).Status);
        Assert.Equal(IslandStatus.Ready, net.Islands.Of(bPos).Status);        // healthy member keeps solving

        int faulted = 0, recovered = 0;
        var cur = from;
        while (net.Journal.TryRead(ref cur, out var ev))
        {
            if (ev.Kind == TopologyEventKind.IslandFaulted) faulted++;
            if (ev.Kind == TopologyEventKind.IslandRecovered) recovered++;
        }
        Assert.Equal(1, faulted);        // retrying a still-faulted member must NOT re-emit each tick
        Assert.Equal(0, recovered);      // no recovery yet

        // Remove the contradiction ⇒ island A rebuilds and recovers to Ready.
        using (var e = net.Edit()) e.Remove(bad);
        NodeId aNow = default;
        for (var i = 0; i < 10; i++)
        {
            net.Solve(new TickClock(100 + i, 0.05));
            Assert.True(net.TryResolveNode(K(1), out aNow));
        }
        Assert.Equal(IslandStatus.Ready, net.Islands.Of(aNow).Status);
    }

    // ------------------------------------------------------ faulted propagation

    [Fact]
    public void Faulted_member_zeroes_the_units_exchange_others_keep_solving()
    {
        // Island A holds two contradictory ideal sources (10 V and 20 V in parallel) ⇒
        // singular ⇒ Faulted. The coupler must stop transferring (exchange reports zero)
        // while island B keeps solving (de-energized, no injection). (item 5)
        var net = Net();
        NodeId aPos, aGnd, bPos, bGnd; CouplerId c;
        using (var e = net.Edit())
        {
            aPos = e.AddNode(K(1)); aGnd = e.AddReferenceNode(K(2));
            bPos = e.AddNode(K(3)); bGnd = e.AddReferenceNode(K(4));
            e.AddVoltageSource(aPos, aGnd, 10.0, K(10));
            e.AddVoltageSource(aPos, aGnd, 20.0, K(12));   // contradiction ⇒ singular island A
            e.AddResistor(bPos, bGnd, 5.0, K(11));
            c = e.AddCoupler(CouplerSpec.DecouplingTransformer(new TransformerParams(2.0), 0.5),
                new CouplerPorts(aPos, aGnd, bPos, bGnd), K(20), StateKey.From(K(20)));
        }
        net.SolveOperatingPoint();
        net.Reconfigure(c, CouplerState.Closed);
        Settle(net, 30);

        Assert.Equal(IslandStatus.Faulted, net.Islands.Of(aPos).Status);   // A faulted
        Assert.Equal(IslandStatus.Ready, net.Islands.Of(bPos).Status);     // B still solves

        // The exchange reports zero transfer (couplers stop transferring on a fault).
        var view = net.Islands.Of(bPos).Exchange(c);
        Assert.Equal(0.0, view.PowerA2B, 9);
        Assert.Equal(0.0, net.Solution.Voltage(bPos), 6);                   // B de-energized (no injection)
    }
}
