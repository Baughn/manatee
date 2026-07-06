using System;
using CsCheck;
using Manatee.Core;

namespace Manatee.Core.Tests.Netlist;

/// <summary>
/// Boundary-coupling stability + conservation property/fuzz harness
/// (testing-strategy.md "Property and Fuzz Tests" + "Invariant Checks";
/// solver.md Islands "Scheduling and conservation"; design.md energy-accounting
/// rule). Behavioral coupling has NO ngspice oracle (testing-strategy.md "What
/// ngspice cannot oracle") — correctness here is invariants + conservation
/// ledgers + boundedness under randomized, adversarial drive. Fast category
/// (no ngspice); the whole suite runs in the inner loop.
///
/// <para>Five guards, each running <see cref="AssertFinite"/> (KCL + finiteness,
/// testing-strategy.md) after EVERY solve:</para>
/// <list type="number">
///   <item>Boundary stability: seeded load steps + square-wave toggles across a
///   transformer and a converter two-port SETTLE (ringing envelope decays) and
///   never grow unboundedly; α swept over its legal range, load over 3 decades,
///   square waves near the relaxation time constant (design.md's "pump" case).</item>
///   <item>Conservation under oscillation: |InJ − OutJ − HeatDumpedJ| ≈ 0 and
///   SurplusJ ⇒ HeatDumpedJ (no silently stored work) on every tick under
///   tick-rate load toggles (design.md energy-accounting rule).</item>
///   <item>Dissipativity: a closed loop of two islands coupled BOTH directions
///   monotonically decays from seeded initial energy — no gain loop (the core-level
///   version of the 2f-aliasing guard).</item>
///   <item>Faulted member mid-run: a contradictory source injected via Edit ⇒ the
///   unit's couplers report zero transfer, no NaN, the other member keeps solving,
///   and removal recovers.</item>
///   <item>KCL + finiteness after every solve, everywhere above.</item>
/// </list>
/// </summary>
public sealed class BoundaryStabilityProperties
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

    // Run CheckInvariants after a solve (testing-strategy.md Invariant Checks) and
    // enforce FINITENESS — the one mechanical guard that catches a divergence / NaN /
    // stamp blow-up with no oracle, valid on EVERY tick (item 5).
    //
    // NOTE on KCL mid-transient: CheckInvariants' KCL residual reassembles the RHS,
    // which on a boundary-coupled island already holds the NEXT-substep injection that
    // DoExchange staged AFTER the solve (the relaxation fixed-point's pending delta). So
    // the residual is ≈0 only at STEADY STATE, not mid-transient — it is the exchange
    // relaxation error, not a physical KCL violation. Hence every tick asserts the
    // residual is FINITE (never NaN/Inf); the ≈0 magnitude is asserted separately once
    // the unit has settled (AssertSettledKcl / the dedicated steady-state fact).
    private static void AssertFinite(Core.Netlist net, NodeId probe, CouplerId coupler, string where)
    {
        var isl = net.Islands.Of(probe);
        var rep = isl.CheckInvariants(InvariantChecks.Kcl | InvariantChecks.Finiteness);
        Assert.True(rep.AllFinite, $"{where}: non-finite matrix row {rep.FirstNonFiniteRow}");
        Assert.True(double.IsFinite(rep.MaxKclResidual), $"{where}: non-finite KCL residual");

        var view = isl.Exchange(coupler);
        Assert.True(double.IsFinite(view.AmplitudeA) && double.IsFinite(view.AmplitudeB)
                    && double.IsFinite(view.PowerA2B), $"{where}: non-finite ExchangeView");

        var led = isl.Ledger(coupler);
        Assert.True(double.IsFinite(led.InJ) && double.IsFinite(led.OutJ)
                    && double.IsFinite(led.ModeledLossJ) && double.IsFinite(led.HeatDumpedJ)
                    && double.IsFinite(led.Residual), $"{where}: non-finite EnergyLedger");
    }

    // At a settled operating point the pending injection delta has relaxed to zero, so
    // the boundary-coupled island's KCL residual collapses to machine precision.
    private static void AssertSettledKcl(Core.Netlist net, NodeId probe, string where)
    {
        var rep = net.Islands.Of(probe).CheckInvariants(InvariantChecks.Kcl | InvariantChecks.Finiteness);
        Assert.True(rep.AllFinite, $"{where}: non-finite at settle");
        Assert.True(rep.MaxKclResidual < 1e-6, $"{where}: settled KCL residual {rep.MaxKclResidual:G6} A");
    }

    // ------------------------------------------------------------------- rigs

    // Stiff 10 V source on island A; resistive load on island B; a DecouplingTransformer
    // (turns n) bridges them into one lockstep unit. The load resistor is adjustable
    // (the step / square-wave handle).
    private sealed class XfmrRig
    {
        public Core.Netlist Net = null!;
        public NodeId APos, AGnd, BPos;
        public ResistorId Load;
        public CouplerId Coupler;
    }

    private static XfmrRig BuildXfmr(double turns, double loadOhms, double alpha)
    {
        var net = Net();
        var r = new XfmrRig { Net = net };
        using (var e = net.Edit())
        {
            r.APos = e.AddNode(K(1)); r.AGnd = e.AddReferenceNode(K(2));
            r.BPos = e.AddNode(K(3)); var bGnd = e.AddReferenceNode(K(4));
            e.AddVoltageSource(r.APos, r.AGnd, 10.0, K(10));
            r.Load = e.AddResistor(r.BPos, bGnd, loadOhms, K(11));
            r.Coupler = e.AddCoupler(
                CouplerSpec.DecouplingTransformer(new TransformerParams(turns), alpha),
                new CouplerPorts(r.APos, r.AGnd, r.BPos, bGnd), K(20), StateKey.From(K(20)));
        }
        net.SolveOperatingPoint();
        net.Reconfigure(r.Coupler, CouplerState.Closed);
        return r;
    }

    // 100 V source on island A; converter regulates island B to 50 V, rated 1000 W,
    // efficiency curve (0.25,0.80)(0.5,0.90)(1.0,0.95). Adjustable load on B.
    private sealed class ConvRig
    {
        public Core.Netlist Net = null!;
        public NodeId APos, BPos;
        public ResistorId Load;
        public CouplerId Coupler;
    }

    private static ConvRig BuildConv(double loadOhms, double alpha)
    {
        var eff = EfficiencyCurve.Points((0.25, 0.80), (0.5, 0.90), (1.0, 0.95));
        var net = Net();
        var r = new ConvRig { Net = net };
        using (var e = net.Edit())
        {
            r.APos = e.AddNode(K(1)); var aGnd = e.AddReferenceNode(K(2));
            r.BPos = e.AddNode(K(3)); var bGnd = e.AddReferenceNode(K(4));
            e.AddVoltageSource(r.APos, aGnd, 100.0, K(10));
            r.Load = e.AddResistor(r.BPos, bGnd, loadOhms, K(11));
            r.Coupler = e.AddCoupler(
                CouplerSpec.ConverterTwoPort(eff, dcLinkFarads: 0.0, outputVolts: 50.0, ratedWatts: 1000.0, relaxationAlpha: alpha),
                new CouplerPorts(r.APos, aGnd, r.BPos, bGnd), K(20), StateKey.From(K(20)));
        }
        net.SolveOperatingPoint();
        net.Reconfigure(r.Coupler, CouplerState.Closed);
        return r;
    }

    // ============================================================ 1. boundary stability

    [Fact]
    public void Transformer_ringing_envelope_decays_after_a_seeded_load_step()
    {
        // α over its legal (0,1] range, load over 3 decades (1 Ω … 1000 Ω). Settle,
        // STEP the load, then assert the exchanged-power ringing envelope is
        // NON-INCREASING across the transient (a later window's swing ≤ an earlier
        // window's) and every sample is finite and bounded.
        var gen =
            from alpha in Gen.Double[0.03, 1.0]
            from rA in Gen.Double[1.0, 10.0]
            from rB in Gen.Double[100.0, 1000.0]
            from turns in Gen.Double[0.5, 4.0]
            from stepUp in Gen.Bool
            select (alpha, r0: stepUp ? rA : rB, r1: stepUp ? rB : rA, turns);

        gen.Sample(t =>
        {
            var (alpha, r0, r1, turns) = t;
            var rig = BuildXfmr(turns, r0, alpha);
            for (var i = 0; i < 200; i++)
            {
                rig.Net.Solve(new TickClock(i + 1, 0.05));
                AssertFinite(rig.Net, rig.APos, rig.Coupler, $"settle t={i}");
            }

            rig.Net.Adjust(rig.Load, r1);   // the step

            const int post = 80;
            var p = new double[post];
            for (var i = 0; i < post; i++)
            {
                rig.Net.Solve(new TickClock(1000 + i, 0.05));
                AssertFinite(rig.Net, rig.APos, rig.Coupler, $"post t={i}");
                p[i] = rig.Net.Islands.Of(rig.APos).Exchange(rig.Coupler).PowerA2B;
                Assert.True(double.IsFinite(p[i]), $"alpha={alpha}: non-finite power at t={i}");
            }

            // Bounded: V_A is a stiff 10 V, so the through-power can never exceed
            // 10 V · (worst reflected current) by a wide margin. 1e4 W is orders above
            // any legitimate value here (max ~100 W) — it fires only on a runaway.
            for (var i = 0; i < post; i++)
                Assert.True(Math.Abs(p[i]) < 1e4, $"alpha={alpha}: power ran away to {p[i]} at t={i}");

            var pFinal = p[post - 1];
            var envEarly = EnvelopeAround(p, 1, 20, pFinal);
            var envLate = EnvelopeAround(p, 60, 79, pFinal);
            // Ringing decays: the late swing about the settled value is no larger than
            // the early swing (exponential relaxation). Tolerance covers the α≈1 case
            // where both windows are already at machine-noise.
            Assert.True(envLate <= envEarly * (1.0 + 1e-6) + 1e-6 * Math.Max(Math.Abs(pFinal), 1.0),
                $"alpha={alpha}: envelope grew (early={envEarly:G6} late={envLate:G6})");
        }, iter: 48, seed: "0000000000001");
    }

    [Fact]
    public void Converter_input_draw_settles_after_a_seeded_load_step()
    {
        var gen =
            from alpha in Gen.Double[0.05, 1.0]
            from rA in Gen.Double[2.5, 10.0]      // heavy load (fraction ≈ 0.25 … 1.0)
            from rB in Gen.Double[50.0, 500.0]    // light load
            from stepUp in Gen.Bool
            select (alpha, r0: stepUp ? rA : rB, r1: stepUp ? rB : rA);

        gen.Sample(t =>
        {
            var (alpha, r0, r1) = t;
            var rig = BuildConv(r0, alpha);
            // Pre-step settle window: 500 ticks, not 200. Under the api.md §7 ruling the B port is
            // the DC-link CAPACITOR driven by a charge controller, not the old ideal setpoint
            // source that pinned V_B = 50 V from tick 1. The controller reaches the setpoint with
            // ZERO steady-state error but through a real RC-like transient whose rate is set by the
            // α-smoothed A-draw: the SLOWEST corner the generator samples (α = 0.05 at the heaviest
            // 2.5 Ω load) needs ~500 ticks to hold V_B to 3 decimals (measured: err 7.8e-4 at 400,
            // 5.0e-5 at 500). 200 ticks left that corner at ~0.2 V and read as a flaky red. This is
            // the intended cap settling time, not a defect — the assert below bounds it.
            for (var i = 0; i < 500; i++)
            {
                rig.Net.Solve(new TickClock(i + 1, 0.05));
                AssertFinite(rig.Net, rig.APos, rig.Coupler, $"settle t={i}");
            }
            // Regulated setpoint held (to 3 decimals) once the cap has settled.
            Assert.Equal(50.0, rig.Net.Solution.Voltage(rig.BPos), 3);

            rig.Net.Adjust(rig.Load, r1);

            const int post = 80;
            var pin = new double[post];
            for (var i = 0; i < post; i++)
            {
                rig.Net.Solve(new TickClock(2000 + i, 0.05));
                AssertFinite(rig.Net, rig.APos, rig.Coupler, $"post t={i}");
                Assert.True(rig.Net.TryReadConverterPowers(rig.Coupler, out var pIn, out _));
                pin[i] = pIn;
                Assert.True(double.IsFinite(pIn) && Math.Abs(pIn) < 1e5,
                    $"alpha={alpha}: converter input draw {pIn} at t={i}");
            }

            var pFinal = pin[post - 1];
            var envEarly = EnvelopeAround(pin, 1, 20, pFinal);
            var envLate = EnvelopeAround(pin, 60, 79, pFinal);
            Assert.True(envLate <= envEarly * (1.0 + 1e-6) + 1e-6 * Math.Max(Math.Abs(pFinal), 1.0),
                $"alpha={alpha}: converter draw envelope grew (early={envEarly:G6} late={envLate:G6})");
        }, iter: 36, seed: "0000000000002");
    }

    [Fact]
    public void Transformer_square_wave_near_time_constant_stays_bounded()
    {
        // The design's called-out "pump" case: toggle the load as a square wave with
        // a period near the relaxation time constant τ ≈ −1/ln(1−α) substeps. A
        // positive-feedback bug would let the exchanged amplitude ratchet up cycle
        // over cycle; the smoothing + ledger clamp keep it bounded. Assert the second
        // half's peak swing is no worse than the first half's (no cycle-over-cycle growth).
        var gen =
            from alpha in Gen.Double[0.1, 0.9]
            from rLo in Gen.Double[2.0, 8.0]
            from rHi in Gen.Double[80.0, 400.0]
            from mult in Gen.Int[1, 3]
            select (alpha, rLo, rHi, mult);

        gen.Sample(t =>
        {
            var (alpha, rLo, rHi, mult) = t;
            var rig = BuildXfmr(2.0, rHi, alpha);
            var tau = Math.Max(1.0, -1.0 / Math.Log(1.0 - alpha));
            var half = Math.Max(1, (int)Math.Round(tau * mult));   // half-period near τ

            // Toggle the load as a square wave and report the peak |exchanged power| over
            // a span of `spanCycles`, asserting finiteness + a hard physical bound every
            // tick (the real "never grows unboundedly" guard: with V_A a stiff 10 V and
            // R ≥ 2 Ω the through-power tops out near ~13 W, so 200 W fires only on a
            // genuine runaway — which a pump would reach and exceed within the run).
            double DrivePeak(int spanCycles, ref int idx, ref bool high)
            {
                var peak = 0.0;
                for (var c = 0; c < spanCycles * 2; c++)
                {
                    high = !high;
                    rig.Net.Adjust(rig.Load, high ? rLo : rHi);
                    for (var s = 0; s < half; s++)
                    {
                        rig.Net.Solve(new TickClock(3000 + idx, 0.05));
                        AssertFinite(rig.Net, rig.APos, rig.Coupler, $"sq t={idx}");
                        var pw = rig.Net.Islands.Of(rig.APos).Exchange(rig.Coupler).PowerA2B;
                        Assert.True(double.IsFinite(pw) && Math.Abs(pw) < 200.0,
                            $"alpha={alpha}: pump ran away to {pw} at t={idx}");
                        peak = Math.Max(peak, Math.Abs(pw));
                        idx++;
                    }
                }
                return peak;
            }

            var i = 0;
            var high = false;
            // Warm the periodic orbit to steady state first (a pump would already blow the
            // per-tick bound here), THEN compare two equal spans: at steady orbit the later
            // span's peak must not exceed the earlier span's — no cycle-over-cycle ratchet.
            DrivePeak(30, ref i, ref high);
            var peakEarly = DrivePeak(8, ref i, ref high);
            var peakLate = DrivePeak(8, ref i, ref high);
            Assert.True(peakLate <= peakEarly * 1.05 + 1e-6,
                $"alpha={alpha}: square-wave pump grew (early={peakEarly:G6} late={peakLate:G6})");
        }, iter: 30, seed: "0000000000003");
    }

    // Max |sample − reference| over an inclusive index window.
    private static double EnvelopeAround(double[] p, int lo, int hi, double reference)
    {
        var env = 0.0;
        for (var i = lo; i <= hi && i < p.Length; i++)
            env = Math.Max(env, Math.Abs(p[i] - reference));
        return env;
    }

    // ============================================================ 2. conservation

    [Fact]
    public void Ledger_conserves_energy_under_tick_rate_load_oscillation()
    {
        // design.md energy-accounting rule: drive with tick-rate load toggles (the
        // worst case for the accounting) and assert on EVERY tick that the ledger
        // balances — In = Out + HeatDumped (Residual ≈ 0), no free energy (Out ≤ In),
        // and SurplusJ ≥ 0 folded into HeatDumpedJ (never silently stored work).
        var gen =
            from useConverter in Gen.Bool
            from alpha in Gen.Double[0.1, 1.0]
            from rLo in Gen.Double[2.0, 9.0]
            from rHi in Gen.Double[60.0, 600.0]
            select (useConverter, alpha, rLo, rHi);

        gen.Sample(t =>
        {
            var (useConverter, alpha, rLo, rHi) = t;
            Core.Netlist net; NodeId aPos; ResistorId load; CouplerId coupler;
            if (useConverter)
            {
                var rig = BuildConv(rHi, alpha);
                net = rig.Net; aPos = rig.APos; load = rig.Load; coupler = rig.Coupler;
            }
            else
            {
                var rig = BuildXfmr(2.0, rHi, alpha);
                net = rig.Net; aPos = rig.APos; load = rig.Load; coupler = rig.Coupler;
            }

            var isl = net.Islands.Of(aPos);
            var high = false;
            for (var i = 0; i < 220; i++)
            {
                high = !high;
                net.Adjust(load, high ? rLo : rHi);   // tick-rate toggle
                net.Solve(new TickClock(4000 + i, 0.05));
                AssertFinite(net, aPos, coupler, $"osc t={i}");

                var led = isl.Ledger(coupler);
                // THE by-construction conservation identity (design.md): every joule is
                // accounted — In = Out + HeatDumped, Residual ≈ 0 — on EVERY tick, under
                // tick-rate oscillation, for BOTH families. This is "no silently stored
                // work": the accounting closes exactly, nothing is unattributed.
                Assert.True(Math.Abs(led.Residual) < 1e-9,
                    $"conv={useConverter} alpha={alpha}: residual {led.Residual:G6} at t={i}");
                // SurplusJ (the accounting residue folded into heat) is never negative:
                // the clamp guarantees InJ − OutJ − ModeledLossJ ≥ 0 for both families.
                Assert.True(led.SurplusJ >= -1e-12,
                    $"conv={useConverter}: negative surplus {led.SurplusJ} at t={i}");

                // Load-bearing for BOTH families (no converter carve-out): HeatDumpedJ is
                // never negative, and the ledger never records delivering more than it
                // received net of the modeled loss. For the converter this is real ONLY
                // because ModeledLossJ is now computed independently from the efficiency
                // curve (P_out·(1/η−1) ≥ 0) rather than synthesized as In−Out — the old
                // synthesis made HeatDumpedJ = In−Out go NEGATIVE during a load-up transient
                // (the relaxation lag delivers to B before drawing from A). See the
                // no-negative-heat fact below.
                Assert.True(led.HeatDumpedJ >= -1e-12,
                    $"conv={useConverter}: negative heat {led.HeatDumpedJ} at t={i}");
                Assert.True(led.OutJ <= led.InJ + led.ModeledLossJ + 1e-9,
                    $"conv={useConverter}: out {led.OutJ} > in {led.InJ} + loss {led.ModeledLossJ} at t={i}");
            }
        }, iter: 40, seed: "0000000000004");
    }

    // ============================================================ 3. dissipativity

    [Fact]
    public void Bidirectional_two_island_loop_decays_from_seeded_energy()
    {
        // A → B → A through TWO DecouplingTransformers (a closed coupling loop). Each
        // island is an RC store fed through a series link from the coupler's pinned
        // output port. Seed island A's store with a current source, then remove the
        // drive (Drive → 0 A = open, leaving the caps charged) and assert the total
        // stored energy (½CV² over both caps) is bounded and MONOTONICALLY decays to
        // ~0 — a gain loop would instead grow it without bound (the core-level 2f-alias
        // guard). The two turns ratios are RANDOMIZED (n1,n2 ∈ [1.5,2.5]) rather than pinned
        // at 2.0, so the loop voltage gain 1/(n1·n2) sweeps a band rather than sitting at one
        // point. Decay across the band must come from the RC/series-resistor dissipation.
        //
        // NOTE (RESOLVED by the api.md §7 conservation ruling, 2026-07-06): pushing PAST the
        // stability boundary — gain-capable turns n1·n2 ≤ 1 (e.g. 0.5/0.5 ⇒ loop gain 4) —
        // used to make this loop GROW (seeded 8.6 J → 85.5 J), because the relaxation-lag
        // boundary exchange was not conservative and a voltage-gain loop pumped the lag
        // residue. The transformer DEBT DROOP now debits that over-delivery, forcing the
        // loop dissipative; the gain-capable case is its own acceptance gate,
        // ConservationAuditTests.Gain_capable_two_coupler_loop_decays_from_seeded_energy.
        // This test keeps the well-damped turns range so it isolates plain RC dissipation.
        var gen =
            from alpha in Gen.Double[0.2, 1.0]
            from ra in Gen.Double[50.0, 400.0]
            from rb in Gen.Double[50.0, 400.0]
            from n1 in Gen.Double[1.5, 2.5]
            from n2 in Gen.Double[1.5, 2.5]
            select (alpha, ra, rb, n1, n2);

        gen.Sample(t =>
        {
            var (alpha, ra, rb, n1, n2) = t;
            var net = Net();
            NodeId aStore, aPort, aGnd, bStore, bPort, bGnd;
            ISourceId seed; CouplerId c1, c2;
            using (var e = net.Edit())
            {
                aGnd = e.AddReferenceNode(K(1)); bGnd = e.AddReferenceNode(K(2));
                aStore = e.AddNode(K(3)); aPort = e.AddNode(K(4));
                bStore = e.AddNode(K(5)); bPort = e.AddNode(K(6));

                // Island A: RC store + series link to the coupler-fed port + seed source.
                e.AddCapacitor(aStore, aGnd, 1e-3, K(10), StateKey.From(K(10)));
                e.AddResistor(aStore, aGnd, ra, K(11));
                e.AddResistor(aPort, aStore, 10.0, K(12));
                seed = e.AddCurrentSource(aGnd, aStore, 0.5, K(13));

                // Island B: symmetric RC store + series link.
                e.AddCapacitor(bStore, bGnd, 1e-3, K(20), StateKey.From(K(20)));
                e.AddResistor(bStore, bGnd, rb, K(21));
                e.AddResistor(bPort, bStore, 10.0, K(22));

                // c1: A store → B port. c2: B store → A port. Two transformers close the loop.
                c1 = e.AddCoupler(CouplerSpec.DecouplingTransformer(new TransformerParams(n1), alpha),
                    new CouplerPorts(aStore, aGnd, bPort, bGnd), K(30), StateKey.From(K(30)));
                c2 = e.AddCoupler(CouplerSpec.DecouplingTransformer(new TransformerParams(n2), alpha),
                    new CouplerPorts(bStore, bGnd, aPort, aGnd), K(31), StateKey.From(K(31)));
            }
            net.SolveOperatingPoint();
            // Both couplers must be Closed for the loop to be live.
            net.Reconfigure(c1, CouplerState.Closed);
            net.Reconfigure(c2, CouplerState.Closed);

            // Warm-up: charge the stores through the loop with the seed live.
            for (var i = 0; i < 60; i++)
            {
                net.Solve(new TickClock(5000 + i, 0.05));
                AssertFinite(net, aStore, c1, $"warm t={i}");
            }

            net.Drive(seed, 0.0);   // remove the drive; caps hold their charge

            double Energy()
            {
                var va = net.Solution.Voltage(aStore);
                var vb = net.Solution.Voltage(bStore);
                return 0.5 * 1e-3 * va * va + 0.5 * 1e-3 * vb * vb;
            }

            const int post = 160;
            var en0 = Energy();
            Assert.True(en0 > 1e-6, $"alpha={alpha}: warm-up left too little seed energy ({en0:G6}) to test decay");

            // Growth floor: below ~1e-4·E0 the caps are effectively discharged and the
            // only motion is the relaxation registers sloshing a picojoule back and forth
            // — irrelevant to "no gain loop". Above the floor, energy must never rise. A
            // real gain loop grows toward E0-scale, far above the floor: caught either way.
            var floor = 1e-4 * en0;
            var prev = double.PositiveInfinity;
            var peak = 0.0;
            for (var i = 0; i < post; i++)
            {
                net.Solve(new TickClock(6000 + i, 0.05));
                AssertFinite(net, aStore, c1, $"decay t={i}");
                var en = Energy();
                Assert.True(double.IsFinite(en), $"alpha={alpha}: non-finite energy at t={i}");
                peak = Math.Max(peak, en);

                // Monotone tail (after the register→cap settling margin): energy never
                // rises above the noise floor. A gain loop violates this immediately.
                if (i >= 30)
                    Assert.True(en <= prev * (1.0 + 1e-6) + floor,
                        $"alpha={alpha}: energy rose in tail ({prev:G6} → {en:G6}) at t={i}");
                prev = en;
            }

            // Bounded throughout (no runaway) and strongly decayed by the end (dissipative).
            Assert.True(peak <= en0 * 3.0 + 1e-15, $"alpha={alpha}: energy ran away (e0={en0:G6} peak={peak:G6})");
            Assert.True(prev < en0 * 0.01 + 1e-15, $"alpha={alpha}: energy did not decay (e0={en0:G6} final={prev:G6})");
        }, iter: 24, seed: "0000000000005");
    }

    // ============================================================ 4. faulted member

    [Fact]
    public void Faulted_member_injected_mid_run_zeroes_transfer_and_recovers_on_removal()
    {
        // Settle a healthy transformer unit, then INJECT a contradictory source into
        // island A via Edit: a voltage-source LOOP aPos→X→aPos with a net 10 V EMF
        // around a zero-impedance loop ⇒ structurally singular ⇒ Faulted. The unit's
        // coupler must report zero transfer with no NaN while island B keeps solving;
        // REMOVING the contradiction recovers the unit and transfer resumes. (item 5)
        //
        // (A parallel same-node-pair source — the build-time contradiction — is instead
        // silently absorbed by the mid-run INCREMENTAL add path and does NOT fault; the
        // source-loop contradiction faults reliably through that path. See concerns.)
        var rig = BuildXfmr(2.0, 5.0, 0.5);
        for (var i = 0; i < 120; i++)
        {
            rig.Net.Solve(new TickClock(i + 1, 0.05));
            AssertFinite(rig.Net, rig.APos, rig.Coupler, $"pre t={i}");
        }
        // Healthy transfer before the fault, and a settled unit satisfies KCL exactly.
        Assert.True(Math.Abs(rig.Net.Islands.Of(rig.APos).Exchange(rig.Coupler).PowerA2B) > 0.1);
        AssertSettledKcl(rig.Net, rig.APos, "pre-fault settle A");
        AssertSettledKcl(rig.Net, rig.BPos, "pre-fault settle B");

        // Inject the contradiction: a voltage-source loop aPos→X→aPos (net 10 V EMF).
        VSourceId bad1, bad2;
        using (var e = rig.Net.Edit())
        {
            var x = e.AddNode(K(98));
            bad1 = e.AddVoltageSource(rig.APos, x, 5.0, K(99));
            bad2 = e.AddVoltageSource(x, rig.APos, 5.0, K(100));
        }

        for (var i = 0; i < 30; i++)
        {
            rig.Net.Solve(new TickClock(7000 + i, 0.05));
            // Island A is faulted (singular); island B must keep solving healthily.
            Assert.Equal(IslandStatus.Faulted, rig.Net.Islands.Of(rig.APos).Status);
            AssertFinite(rig.Net, rig.BPos, rig.Coupler, $"faulted t={i}");

            // Coupler reports ZERO transfer, no NaN, from the tick the fault is seen.
            var view = rig.Net.Islands.Of(rig.BPos).Exchange(rig.Coupler);
            Assert.Equal(0.0, view.PowerA2B, 9);
            Assert.True(double.IsFinite(view.AmplitudeA) && double.IsFinite(view.AmplitudeB));
        }
        // B is de-energized: the fault zeroes B's injected source, which propagates to
        // B's node voltage on its next solve (the exchange stages after B's substep). One
        // extra tick makes the settled state observable.
        Assert.Equal(0.0, rig.Net.Solution.Voltage(rig.BPos), 6);

        // Remove the contradiction ⇒ recovery. The removal rebuilds island A and reissues
        // its node handles (the orphaned loop node is collected); the rebuild is coalesced
        // to the next Solve, so re-resolve APos/BPos by their stable ExternalKeys each tick
        // — node IDENTITY (the key) survives, the handle generation does not.
        using (var e = rig.Net.Edit())
        {
            e.Remove(bad1);
            e.Remove(bad2);
        }

        NodeId aPos = default, bPos = default;
        for (var i = 0; i < 120; i++)
        {
            rig.Net.Solve(new TickClock(8000 + i, 0.05));
            Assert.True(rig.Net.TryResolveNode(K(1), out aPos));
            Assert.True(rig.Net.TryResolveNode(K(3), out bPos));
            AssertFinite(rig.Net, aPos, rig.Coupler, $"recover t={i}");
        }
        Assert.Equal(IslandStatus.Ready, rig.Net.Islands.Of(aPos).Status);
        // Transfer resumed at the pre-fault steady state (V_B = 5 V, i_B = 1 A ⇒ 5 W).
        var v = rig.Net.Islands.Of(aPos).Exchange(rig.Coupler);
        Assert.True(v.PowerA2B > 0.1, $"transfer did not resume after recovery: {v.PowerA2B}");
        Assert.Equal(5.0, rig.Net.Solution.Voltage(bPos), 2);
        AssertSettledKcl(rig.Net, aPos, "post-recovery settle A");
        AssertSettledKcl(rig.Net, bPos, "post-recovery settle B");
    }

    // ============================================================ 5. steady-state KCL

    [Fact]
    public void Settled_boundary_coupled_islands_satisfy_kcl_to_machine_precision()
    {
        // The magnitude complement of the per-tick finiteness guard: once the exchange
        // relaxation has converged, BOTH members of a boundary-coupled unit satisfy KCL
        // to machine precision (the staged injection delta has decayed to ~0). Both
        // families, at a well-damped α with a long settle.
        var xf = BuildXfmr(2.0, 5.0, 0.5);
        for (var i = 0; i < 600; i++) xf.Net.Solve(new TickClock(i + 1, 0.05));
        AssertSettledKcl(xf.Net, xf.APos, "xfmr settle A");
        AssertSettledKcl(xf.Net, xf.BPos, "xfmr settle B");

        var cv = BuildConv(5.0, 0.5);
        for (var i = 0; i < 600; i++) cv.Net.Solve(new TickClock(i + 1, 0.05));
        AssertSettledKcl(cv.Net, cv.APos, "conv settle A");
        AssertSettledKcl(cv.Net, cv.BPos, "conv settle B");
    }
}
