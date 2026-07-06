using System;
using System.Collections.Generic;
using CsCheck;
using Manatee.Core;

namespace Manatee.Core.Tests.Netlist;

/// <summary>
/// Conservation as a WINDOWED PHYSICAL AUDIT, not a ledger read (api.md §7 ruling,
/// 2026-07-06: "Conservation is physical, not clerical"). The audit integrates, per
/// substep across BOTH islands of a coupled unit, energy flows reconstructed ONLY from
/// public node-voltage readbacks — real source energy in (V·I·dt), resistor dissipation
/// (I²R·dt), Δ stored (caps ½CV², inductors ½LI², including the converter DC-link cap),
/// and the coupler's reported HeatDumpedJ — and asserts
///   |sources − dissipated − Δstored − heat| &lt; tolerance
/// over EVERY window. EnergyLedger.Residual is deliberately NOT used (it is a ≈0 closure
/// identity that can never signal a violation — ruling).
///
/// <para><b>Tolerance derivation.</b> Over a window the imbalance is bounded by two honest
/// terms: (a) the ONE-SUBSTEP LAG — the injected boundary source can physically
/// over-deliver by at most one substep of throughput before the debt droop / cap sag
/// debits it back, so |ΔOut_recorded − ΔOut_physical| ≤ P̂·dt for peak boundary power P̂;
/// and (b) BACKWARD-EULER integration error — reconstructing Δstored from ½CV² endpoints
/// while integrating dissipation by the rectangle rule leaves ½C·Σ(ΔV)² per cap
/// (BE's numerical dissipation), which → 0 as the unit settles (ΔV → 0). Resistive rigs
/// (no caps) have term (b) = 0 exactly, so their tolerance is purely one substep of lag.
/// We assert a tolerance a comfortable factor above these bounds; a NON-conservative
/// boundary (e.g. the pre-ruling ledger-only clamp, which let a boundary over-deliver ~2 J
/// persistently) blows straight through it.</para>
/// </summary>
public sealed class ConservationAuditTests
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

    // ─────────────────────────────────────────────────── windowed physical audit

    // Reconstructs energy flows from PUBLIC readbacks only (Solution.Voltage +
    // Ledger.HeatDumpedJ) and validates the balance over every window. Elements are
    // registered by ExternalKey and re-read each tick, so it survives handle churn.
    private sealed class PhysicalAudit
    {
        private readonly Core.Netlist _net;
        private readonly NodeId _probe;          // any node, to reach the coupler's ledger
        private readonly CouplerId _coupler;

        // Ideal source fed through a series resistor: srcNode is pinned at its setpoint,
        // the series resistor carries (and reveals) the source current.
        private readonly List<(NodeId src, NodeId port, double rs)> _sources = new();
        private readonly List<(NodeId a, NodeId b, double r)> _resistors = new();     // pure dissipators
        private readonly List<(NodeId a, NodeId b, double c)> _caps = new();           // ½CV² stores

        private readonly List<double> _cumSource = new();
        private readonly List<double> _cumDiss = new();
        private readonly List<double> _stored = new();
        private readonly List<double> _cumHeat = new();

        private double _sourceAcc, _dissAcc;

        public PhysicalAudit(Core.Netlist net, NodeId probe, CouplerId coupler)
        { _net = net; _probe = probe; _coupler = coupler; }

        public void AddSource(NodeId srcNode, NodeId port, double seriesOhms)
            => _sources.Add((srcNode, port, seriesOhms));
        public void AddResistor(NodeId a, NodeId b, double ohms) => _resistors.Add((a, b, ohms));
        public void AddCap(NodeId a, NodeId b, double farads) => _caps.Add((a, b, farads));

        // A resistive load whose value TOGGLES during the run (the oscillation handle). Its
        // dissipation is reconstructed at whatever value UpdateOscLoad last set — call it in
        // lockstep with net.Adjust so the audit sees the same R the solver did.
        private NodeId _oscA, _oscB; private double _oscR; private bool _hasOsc;
        public void AddOscLoad(NodeId a, NodeId b, double ohms) { _oscA = a; _oscB = b; _oscR = ohms; _hasOsc = true; }
        public void UpdateOscLoad(double ohms) => _oscR = ohms;

        private double V(NodeId n) => _net.Solution.Voltage(n);

        // Sample one solved tick: integrate the flows, snapshot the stores and heat.
        public void Sample(double dt)
        {
            foreach (var (src, port, rs) in _sources)
            {
                var i = (V(src) - V(port)) / rs;   // source current == series-resistor current (KCL)
                _sourceAcc += V(src) * i * dt;      // real source energy delivered
                _dissAcc += i * i * rs * dt;        // series-resistance dissipation
            }
            foreach (var (a, b, r) in _resistors)
            {
                var v = V(a) - V(b);
                _dissAcc += v * v / r * dt;
            }
            if (_hasOsc)
            {
                var v = V(_oscA) - V(_oscB);
                _dissAcc += v * v / _oscR * dt;
            }
            var stored = 0.0;
            foreach (var (a, b, c) in _caps)
            {
                var v = V(a) - V(b);
                stored += 0.5 * c * v * v;
            }
            _cumSource.Add(_sourceAcc);
            _cumDiss.Add(_dissAcc);
            _stored.Add(stored);
            _cumHeat.Add(_net.Islands.Of(_probe).Ledger(_coupler).HeatDumpedJ);
        }

        // Assert conservation over EVERY window [i,j] with i ≥ warmup, j−i ≥ minSpan.
        public void AssertConserved(int warmup, int minSpan, double absTol, double relTol)
        {
            var n = _cumSource.Count;
            var worst = 0.0; var worstI = -1; var worstJ = -1;
            for (var i = warmup; i < n; i++)
                for (var j = i + minSpan; j < n; j++)
                {
                    var dSource = _cumSource[j] - _cumSource[i];
                    var dDiss = _cumDiss[j] - _cumDiss[i];
                    var dStored = _stored[j] - _stored[i];
                    var dHeat = _cumHeat[j] - _cumHeat[i];
                    var imbalance = Math.Abs(dSource - dDiss - dStored - dHeat);
                    var tol = absTol + relTol * Math.Abs(dSource);
                    if (imbalance - tol > worst) { worst = imbalance - tol; worstI = i; worstJ = j; }
                }
            Assert.True(worst <= 0.0,
                $"window [{worstI},{worstJ}] violated conservation by {worst:G6} J beyond tolerance");
        }

        // Sustained-oscillation gate (2026-07-06 phase-5 review). The single-window test above
        // cannot see a DRIVEN pump whose free energy is a small FRACTION of throughput (it hides
        // under relTol) but accumulates without bound. So here we assert the imbalance does not
        // GROW with window length: the per-substep imbalance SLOPE over the post-warmup tail is
        // ~0. A conservative boundary (transient over-delivery, then choked/settled) has a flat
        // tail (slope→0); an unbounded pump has a persistent nonzero slope that this catches
        // regardless of how small a fraction of throughput it is. Warmup runs PAST the debt-droop
        // choke transient so the tail reflects steady behaviour, not the one-time catch.
        public void AssertNoSustainedGrowth(int warmup, double slopeTolPerSample)
        {
            double Imb(int i) => _cumSource[i] - _cumDiss[i] - _stored[i] - _cumHeat[i];
            var n = _cumSource.Count;
            Assert.True(n - warmup >= 100, "not enough samples past warmup to fit a slope");
            // Least-squares slope of Imb over [warmup, n) — robust to per-cycle ripple.
            double sx = 0, sy = 0, sxx = 0, sxy = 0; var m = 0;
            for (var i = warmup; i < n; i++)
            {
                double x = i - warmup, y = Imb(i);
                sx += x; sy += y; sxx += x * x; sxy += x * y; m++;
            }
            var slope = (m * sxy - sx * sy) / (m * sxx - sx * sx);
            Assert.True(Math.Abs(slope) <= slopeTolPerSample,
                $"sustained imbalance slope {slope:E3} J/sample exceeds {slopeTolPerSample:E3} " +
                $"(net {Imb(n - 1) - Imb(warmup):G4} J over {m} samples — a driven pump)");
        }
    }

    // ─────────────────────────────────────── gate 1: gain-capable loop DECAYS

    [Fact]
    public void Gain_capable_two_coupler_loop_decays_from_seeded_energy()
    {
        // THE acceptance gate for the debt droop (api.md §7 ruling). The bidirectional
        // two-transformer loop, driven PAST the stability boundary — gain-capable turns
        // n1·n2 ≤ 1 (loop voltage gain 1/(n1·n2) ≥ 1, up to 4 at 0.5/0.5) — GREW seeded
        // energy without bound before the ruling (the documented 8.6 J → 85.5 J failure in
        // BoundaryStabilityProperties' dissipativity test note). With the debt droop the
        // over-delivery that pumped the loop is now debited back, so the seeded energy must
        // DECAY (monotone envelope) instead of ringing up.
        var gen =
            from alpha in Gen.Double[0.3, 1.0]
            from ra in Gen.Double[50.0, 300.0]
            from rb in Gen.Double[50.0, 300.0]
            from n1 in Gen.Double[0.5, 1.0]
            from n2 in Gen.Double[0.5, 1.0]
            where n1 * n2 <= 1.0
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

                e.AddCapacitor(aStore, aGnd, 1e-3, K(10), StateKey.From(K(10)));
                e.AddResistor(aStore, aGnd, ra, K(11));
                e.AddResistor(aPort, aStore, 10.0, K(12));
                seed = e.AddCurrentSource(aGnd, aStore, 0.5, K(13));

                e.AddCapacitor(bStore, bGnd, 1e-3, K(20), StateKey.From(K(20)));
                e.AddResistor(bStore, bGnd, rb, K(21));
                e.AddResistor(bPort, bStore, 10.0, K(22));

                c1 = e.AddCoupler(CouplerSpec.DecouplingTransformer(new TransformerParams(n1), alpha),
                    new CouplerPorts(aStore, aGnd, bPort, bGnd), K(30), StateKey.From(K(30)));
                c2 = e.AddCoupler(CouplerSpec.DecouplingTransformer(new TransformerParams(n2), alpha),
                    new CouplerPorts(bStore, bGnd, aPort, aGnd), K(31), StateKey.From(K(31)));
            }
            net.SolveOperatingPoint();
            net.Reconfigure(c1, CouplerState.Closed);
            net.Reconfigure(c2, CouplerState.Closed);

            for (var i = 0; i < 60; i++) net.Solve(new TickClock(5000 + i, 0.05));
            net.Drive(seed, 0.0);   // remove the drive; the caps hold their charge

            double Energy()
            {
                var va = net.Solution.Voltage(aStore);
                var vb = net.Solution.Voltage(bStore);
                return 0.5 * 1e-3 * va * va + 0.5 * 1e-3 * vb * vb;
            }

            const int post = 240;
            var en0 = Energy();
            Assert.True(en0 > 1e-6, $"seed too small ({en0:G6})");
            var floor = 1e-4 * en0;
            var prev = double.PositiveInfinity;
            var peak = 0.0;
            for (var i = 0; i < post; i++)
            {
                net.Solve(new TickClock(6000 + i, 0.05));
                var isl = net.Islands.Of(aStore);
                var rep = isl.CheckInvariants(InvariantChecks.Kcl | InvariantChecks.Finiteness);
                Assert.True(rep.AllFinite, $"n1={n1} n2={n2}: non-finite at t={i}");
                var en = Energy();
                Assert.True(double.IsFinite(en), $"n1={n1} n2={n2}: non-finite energy at t={i}");
                peak = Math.Max(peak, en);
                if (i >= 40)
                    Assert.True(en <= prev * (1.0 + 1e-6) + floor,
                        $"n1={n1} n2={n2} a={alpha}: gain-capable loop GREW ({prev:G6} → {en:G6}) at t={i}");
                prev = en;
            }
            // Bounded throughout (a gain loop would ring up toward and past the seed) and
            // strongly decayed by the end — the droop made the loop dissipative.
            Assert.True(peak <= en0 * 3.0 + 1e-12, $"n1={n1} n2={n2}: energy ran away (e0={en0:G6} peak={peak:G6})");
            Assert.True(prev < en0 * 0.5 + 1e-12, $"n1={n1} n2={n2}: energy did not decay (e0={en0:G6} final={prev:G6})");
        }, iter: 40, seed: "0000000000010");
    }

    // ─────────────────────────────── gate 2: 5→1 Ω step's windowed audit balances

    [Fact]
    public void Transformer_five_to_one_ohm_step_passes_the_windowed_physical_audit()
    {
        // The escalated failure (review item a): the transformer boundary over-delivered
        // ~2 J PERSISTENTLY on a 5→1 Ω step (the B feed-forward injects V_A/n regardless of
        // what A delivered). The debt droop debits it back, so the WINDOWED PHYSICAL AUDIT
        // — reconstructed purely from node voltages + HeatDumpedJ — now balances to within
        // one substep of lag. A stiff 10 V source feeds island A through a 0.5 Ω series
        // resistance (so its current is auditable); island B is a resistive load stepped
        // 5 Ω → 1 Ω. No caps ⇒ zero BE error ⇒ the tolerance is purely one substep of lag.
        var net = Net();
        NodeId src, aPort, gnd, bPos, bGnd; ResistorId load; CouplerId c;
        using (var e = net.Edit())
        {
            src = e.AddNode(K(1)); gnd = e.AddReferenceNode(K(2));
            aPort = e.AddNode(K(3));
            bPos = e.AddNode(K(4)); bGnd = e.AddReferenceNode(K(5));
            e.AddVoltageSource(src, gnd, 10.0, K(10));
            e.AddResistor(src, aPort, 0.5, K(11));                 // auditable source series R
            load = e.AddResistor(bPos, bGnd, 5.0, K(12));
            c = e.AddCoupler(CouplerSpec.DecouplingTransformer(new TransformerParams(2.0), 0.5),
                new CouplerPorts(aPort, gnd, bPos, bGnd), K(20), StateKey.From(K(20)));
        }
        net.SolveOperatingPoint();
        net.Reconfigure(c, CouplerState.Closed);

        var audit = new PhysicalAudit(net, aPort, c);
        audit.AddSource(src, aPort, 0.5);
        audit.AddResistor(bPos, bGnd, 5.0);   // load R is re-set below; see note

        for (var i = 0; i < 200; i++) net.Solve(new TickClock(1 + i, 0.05));   // settle at 5 Ω

        // Peak boundary power ≈ V_A·i ≈ 10·(10/1 through n) … a generous one-substep-lag
        // bound: at the post-step operating point P̂ ≈ 50 W, dt = 0.05 ⇒ ~2.5 J. We assert
        // a 0.5 J absolute floor + 5% of window throughput, comfortably above the true
        // per-window lag (which decays) yet far under the pre-ruling ~2 J PERSISTENT leak.
        var run = new PhysicalAudit(net, aPort, c);
        run.AddSource(src, aPort, 0.5);
        run.AddResistor(bPos, bGnd, 1.0);     // audited load value AFTER the step

        net.Adjust(load, 1.0);                // the 5→1 Ω step
        for (var i = 0; i < 300; i++)
        {
            net.Solve(new TickClock(2000 + i, 0.05));
            run.Sample(0.05);
        }
        run.AssertConserved(warmup: 20, minSpan: 40, absTol: 0.5, relTol: 0.05);
    }

    // ─────────────────── gate 2a: SUSTAINED oscillation does not pump (the phase-5 escalation)

    [Theory]
    // Both turns directions (step-DOWN n=2 and the amplified step-UP n=0.5, the worst case), a
    // period sweep spanning tick-rate to slow, and an ASYMMETRIC duty cycle. dutyHigh/dutyLow are
    // the tick counts at rLo / rHi (equal ⇒ symmetric square wave).
    [InlineData(2.0, 2, 2)]  [InlineData(2.0, 3, 3)]  [InlineData(2.0, 4, 4)]
    [InlineData(2.0, 6, 6)]  [InlineData(2.0, 8, 8)]  [InlineData(2.0, 12, 12)]
    [InlineData(0.5, 2, 2)]  [InlineData(0.5, 3, 3)]  [InlineData(0.5, 4, 4)]
    [InlineData(0.5, 6, 6)]  [InlineData(0.5, 8, 8)]  [InlineData(0.5, 12, 12)]
    [InlineData(2.0, 2, 6)]  [InlineData(0.5, 2, 6)]  // asymmetric duty
    public void Transformer_sustained_load_oscillation_does_not_pump(double turns, int dutyHigh, int dutyLow)
    {
        // THE escalated blocker (review items a/finding-1 & finding-4): the debt droop killed the
        // single-step over-delivery, but an OSCILLATING B load defeated the first design — each
        // half-cycle's over-delivery hid below the deadband and the per-substep leak bled the debt
        // before it could accumulate, so the droop never engaged and free energy leaked out
        // linearly (~0.02 J/tick, unbounded; ~2.6 % of throughput). This gate drives exactly that:
        // a lossless transformer whose B load toggles 1 Ω ↔ 5 Ω forever. The windowed PHYSICAL
        // audit (source = V·I through the series R; sinks = series-R + B-load dissipation + coupler
        // HeatDumpedJ; no caps ⇒ no Δstored) must show the imbalance does NOT grow with time once
        // the droop has engaged — a bounded one-time transient, then slope → 0.
        var net = Net();
        NodeId src, aPort, gnd, bPos, bGnd; ResistorId load; CouplerId c;
        const double rs = 0.5, dt = 0.05, rLo = 1.0, rHi = 5.0;
        using (var e = net.Edit())
        {
            src = e.AddNode(K(1)); gnd = e.AddReferenceNode(K(2));
            aPort = e.AddNode(K(3));
            bPos = e.AddNode(K(4)); bGnd = e.AddReferenceNode(K(5));
            e.AddVoltageSource(src, gnd, 10.0, K(10));
            e.AddResistor(src, aPort, rs, K(11));
            load = e.AddResistor(bPos, bGnd, rHi, K(12));
            c = e.AddCoupler(CouplerSpec.DecouplingTransformer(new TransformerParams(turns), 0.5),
                new CouplerPorts(aPort, gnd, bPos, bGnd), K(20), StateKey.From(K(20)));
        }
        net.SolveOperatingPoint();
        net.Reconfigure(c, CouplerState.Closed);
        for (var i = 0; i < 200; i++) net.Solve(new TickClock(1 + i, dt));   // settle

        var run = new PhysicalAudit(net, aPort, c);
        run.AddSource(src, aPort, rs);
        run.AddOscLoad(bPos, bGnd, rHi);

        // Drive the oscillation. The audit warms up PAST the debt-droop choke transient (the
        // bounded one-time over-delivery the wide deadband tolerates) and then asserts the tail
        // imbalance slope ~ 0 — no ONGOING pump under continued oscillation, either direction.
        var tick = 3000; var high = false; var phase = 0; var curR = rHi;
        const int total = 8000, warmup = 3500;
        for (var i = 0; i < total; i++)
        {
            if (phase == 0) { high = !high; curR = high ? rLo : rHi; net.Adjust(load, curR); phase = high ? dutyHigh : dutyLow; }
            phase--;
            net.Solve(new TickClock(tick++, dt));
            run.UpdateOscLoad(curR);
            run.Sample(dt);
            var isl = net.Islands.Of(aPort);
            var rep = isl.CheckInvariants(InvariantChecks.Kcl | InvariantChecks.Finiteness);
            Assert.True(rep.AllFinite, $"turns={turns} duty={dutyHigh}/{dutyLow}: non-finite at i={i}");
        }
        // Slope tolerance: 2e-4 J/sample is ~1 % of the ORIGINAL pump (~0.02 J/tick) — comfortably
        // above the settled numerical/relaxation floor (the static control conserves to ~1e-11
        // J/sample) yet an order under any real leak. A pre-ruling boundary blows straight through.
        run.AssertNoSustainedGrowth(warmup, slopeTolPerSample: 2e-4);
    }

    // ─────────────────────────────── gate 2b: converter windowed audit (with DC-link cap)

    [Fact]
    public void Converter_load_step_passes_the_windowed_physical_audit_including_dc_link()
    {
        // The converter audit exercises the Δstored term on the real DC-link CAPACITOR
        // (½CV²). A stiff 100 V source (0.5 Ω series) feeds A; the converter regulates B to
        // 50 V through an explicit 0.01 F DC-link; the B load is stepped. Balance holds
        // within one substep of lag + BE's ½C·Σ(ΔV)² (which vanishes as the cap settles).
        var eff = EfficiencyCurve.Points((0.25, 0.80), (0.5, 0.90), (1.0, 0.95));
        var net = Net();
        NodeId src, aPort, gnd, bPos, bGnd; ResistorId load; CouplerId c;
        const double dcLink = 0.01;
        using (var e = net.Edit())
        {
            src = e.AddNode(K(1)); gnd = e.AddReferenceNode(K(2));
            aPort = e.AddNode(K(3));
            bPos = e.AddNode(K(4)); bGnd = e.AddReferenceNode(K(5));
            e.AddVoltageSource(src, gnd, 100.0, K(10));
            e.AddResistor(src, aPort, 0.5, K(11));
            load = e.AddResistor(bPos, bGnd, 10.0, K(12));
            c = e.AddCoupler(CouplerSpec.ConverterTwoPort(eff, dcLink, 50.0, 1000.0),
                new CouplerPorts(aPort, gnd, bPos, bGnd), K(20), StateKey.From(K(20)));
        }
        net.SolveOperatingPoint();
        net.Reconfigure(c, CouplerState.Closed);
        for (var i = 0; i < 300; i++) net.Solve(new TickClock(1 + i, 0.05));   // settle + charge DC-link

        var run = new PhysicalAudit(net, aPort, c);
        run.AddSource(src, aPort, 0.5);
        run.AddResistor(bPos, bGnd, 5.0);       // audited load value AFTER the step
        run.AddCap(bPos, bGnd, dcLink);         // the DC-link cap store

        net.Adjust(load, 5.0);                  // 10 Ω → 5 Ω (P_out 250 → 500 W)
        for (var i = 0; i < 300; i++)
        {
            net.Solve(new TickClock(2000 + i, 0.05));
            run.Sample(0.05);
        }
        // Slightly looser than the resistive rig: the BE ½C(ΔV)² term is nonzero mid-step.
        run.AssertConserved(warmup: 20, minSpan: 40, absTol: 1.0, relTol: 0.05);
    }

    // ─────────────────────────────── gate 3: converter brownout (undersized) sags + recovers

    [Fact]
    public void Undersized_converter_browns_out_then_recovers_when_load_is_shed()
    {
        // A converter rated BELOW demand: rated 100 W, but a 2.5 Ω load wants 50²/2.5 =
        // 1000 W at the 50 V setpoint. The output is rating-limited, so the DC-link cap
        // SAGS to the honest brownout point (no deadlock — a resistive load sheds as V
        // drops: equilibrium at V²/R = rated ⇒ V ≈ √(100·2.5) ≈ 15.8 V). No NaN, the ledger
        // stays balanced (Residual ≈ 0, HeatDumpedJ ≥ 0), and shedding the load recovers
        // the setpoint.
        var eff = EfficiencyCurve.Points((0.25, 0.80), (0.5, 0.90), (1.0, 0.95));
        var net = Net();
        NodeId aPos, gnd, bPos, bGnd; ResistorId load; CouplerId c;
        using (var e = net.Edit())
        {
            aPos = e.AddNode(K(1)); gnd = e.AddReferenceNode(K(2));
            bPos = e.AddNode(K(3)); bGnd = e.AddReferenceNode(K(4));
            e.AddVoltageSource(aPos, gnd, 100.0, K(10));
            load = e.AddResistor(bPos, bGnd, 2.5, K(11));    // demands ~1000 W ≫ 100 W rating
            c = e.AddCoupler(CouplerSpec.ConverterTwoPort(eff, 0.02, 50.0, ratedWatts: 100.0),
                new CouplerPorts(aPos, gnd, bPos, bGnd), K(20), StateKey.From(K(20)));
        }
        net.SolveOperatingPoint();
        net.Reconfigure(c, CouplerState.Closed);

        for (var i = 0; i < 300; i++)
        {
            net.Solve(new TickClock(1 + i, 0.05));
            var isl = net.Islands.Of(aPos);
            var rep = isl.CheckInvariants(InvariantChecks.Kcl | InvariantChecks.Finiteness);
            Assert.True(rep.AllFinite, $"brownout non-finite at t={i}");
            var led = isl.Ledger(c);
            Assert.True(double.IsFinite(led.HeatDumpedJ) && led.HeatDumpedJ >= -1e-9, $"heat at t={i}: {led.HeatDumpedJ}");
            Assert.True(Math.Abs(led.Residual) < 1e-9, $"residual drift at t={i}: {led.Residual}");
        }

        // Sagged well below setpoint (brownout), finite, and near the resistive equilibrium
        // √(rated·R) ≈ 15.8 V (the controller holds P_out at the 100 W rating).
        var vSag = net.Solution.Voltage(bPos);
        Assert.True(double.IsFinite(vSag), "brownout produced a non-finite voltage");
        Assert.True(vSag < 45.0, $"under-rated converter did NOT sag: V_B = {vSag:F3}");
        Assert.True(vSag > 5.0, $"brownout collapsed implausibly far: V_B = {vSag:F3}");
        Assert.True(Math.Abs(vSag - Math.Sqrt(100.0 * 2.5)) < 3.0, $"brownout not near √(P·R): V_B = {vSag:F3}");

        // Shed the load (2.5 Ω → 50 Ω, demand 50 W < 100 W rating) ⇒ recover to setpoint.
        net.Adjust(load, 50.0);
        for (var i = 0; i < 300; i++) net.Solve(new TickClock(3000 + i, 0.05));
        Assert.Equal(50.0, net.Solution.Voltage(bPos), 2);
        var ledEnd = net.Islands.Of(aPos).Ledger(c);
        Assert.True(Math.Abs(ledEnd.Residual) < 1e-9, $"post-recovery residual {ledEnd.Residual:G6}");
        Assert.True(ledEnd.HeatDumpedJ >= -1e-9, $"post-recovery heat {ledEnd.HeatDumpedJ:G6}");
    }
}

/// <summary>The coupled-substep zero-alloc gate, in the serialized ZeroAlloc
/// collection — see <see cref="ZeroAllocCollection"/> for the root cause (a
/// concurrent compacting GC makes the per-thread counter over-report by up to an
/// allocation quantum).</summary>
[Collection(ZeroAllocCollection.Name)]
public sealed class CoupledZeroAllocTests
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

    [Fact]
    public void Coupled_unit_steady_substep_path_allocates_nothing()
    {
        // The debt droop and the converter charge-controller/DC-link exchange run on the
        // per-substep path, which must stay 0 B (api.md §8/§21). Best-effort in-process
        // check (skipped where the counter is inert, matching ZeroAllocationTests).
        if (!ZeroAllocGates.CounterIsReliable()) return;

        foreach (var useConverter in new[] { false, true })
        {
            var eff = EfficiencyCurve.Points((0.25, 0.80), (0.5, 0.90), (1.0, 0.95));
            var net = Net();
            NodeId aPos, gnd, bPos, bGnd; CouplerId c;
            using (var e = net.Edit())
            {
                aPos = e.AddNode(K(1)); gnd = e.AddReferenceNode(K(2));
                bPos = e.AddNode(K(3)); bGnd = e.AddReferenceNode(K(4));
                e.AddVoltageSource(aPos, gnd, useConverter ? 100.0 : 10.0, K(10));
                e.AddResistor(bPos, bGnd, 5.0, K(11));
                c = useConverter
                    ? e.AddCoupler(CouplerSpec.ConverterTwoPort(eff, 0.01, 50.0, 1000.0),
                        new CouplerPorts(aPos, gnd, bPos, bGnd), K(20), StateKey.From(K(20)))
                    : e.AddCoupler(CouplerSpec.DecouplingTransformer(new TransformerParams(2.0), 0.5),
                        new CouplerPorts(aPos, gnd, bPos, bGnd), K(20), StateKey.From(K(20)));
            }
            net.SolveOperatingPoint();
            net.Reconfigure(c, CouplerState.Closed);
            for (var i = 0; i < 400; i++) net.Solve(new TickClock(1 + i, 0.05));   // warm to steady state

            // Min over sub-runs (see ZeroAllocCollection): background GC / tiered JIT
            // can add phantom bytes; the perturbation is additive, so min == 0 proves it.
            long best = long.MaxValue;
            var tick = 1000L;
            for (var run = 0; run < 8 && best != 0; run++)
            {
                var before = GC.GetAllocatedBytesForCurrentThread();
                for (var i = 0; i < 200; i++) net.Solve(new TickClock(tick++, 0.05));
                var d = GC.GetAllocatedBytesForCurrentThread() - before;
                if (d < best) best = d;
            }
            Assert.True(best == 0,
                $"converter={useConverter}: steady coupled substep loop allocated {best} B over 200 ticks (min over runs; expected 0)");
        }
    }
}
