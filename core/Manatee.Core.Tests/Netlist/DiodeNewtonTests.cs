using System;
using Manatee.Core;

namespace Manatee.Core.Tests.Netlist;

/// <summary>
/// Phase-4 nonlinear pipeline (solver.md Analyses / Failure Handling; api.md §4, §9,
/// §11, §20): the Newton-Raphson diode solve. The junction physics is pinned against
/// ngspice in <see cref="Manatee.Core.Tests.Oracle.DiodeOracleTests"/> (the EE policy —
/// correctness by oracle, never assertion); here the loop's *machinery* is pinned through
/// the public API — the operating point against an independent hand-Newton, the converged
/// re-solve staying refactor-free (the tier mechanism), and the fallback ladder faulting
/// legibly (never NaN) then auto-recovering.
/// </summary>
public sealed class DiodeNewtonTests
{
    private static ExternalKey K(ulong id) => new(id);

    // k·T/q at T = 300 K — the SAME constant the solver uses (Netlist.Runtime.cs
    // ThermalVoltage300), so the reference hand-Newton below and the matrix Newton
    // linearize identically. n·Vt is the per-diode scale.
    private const double Vt = 0.025851990;

    private static Core.Netlist Dc()
        => new(new NetlistOptions
        {
            Profile = SolverProfile.Dc(0.5),
            Wiring = WiringPolicy.ExplicitOnly(),
            Debug = DebugLevel.Asserts,
        });

    private static Core.Netlist Mixed(double tickDt, int samples = 20)
        => new(new NetlistOptions
        {
            Profile = SolverProfile.Mixed(tickDt, samples),
            Wiring = WiringPolicy.ExplicitOnly(),
            Debug = DebugLevel.Asserts,
        });

    // I(V) = Is·(exp(V/(n·Vt)) − 1), anode→cathode.
    private static double Id(double v, double is_, double n) => is_ * (Math.Exp(v / (n * Vt)) - 1.0);

    /// <summary>Independent scalar Newton for the node-x voltage of Vs —[R]— x —[diode]— gnd.
    /// KCL at x:  f(Vx) = (Vs − Vx)/R − Is·(exp(Vx/(n·Vt)) − 1) = 0.
    /// Newton:  Vx ← Vx − f/f′,  f′ = −1/R − Is·exp(Vx/(n·Vt))/(n·Vt).
    /// Converged to machine precision — the "hand-computed Newton solution" the matrix
    /// solver is checked against (arithmetic above; a worked value: Vs=1, R=1k, Is=1e-14,
    /// n=1 ⇒ Vx ≈ 0.6291466 V, I ≈ 0.37085 mA).</summary>
    private static double ReferenceVx(double vs, double r, double is_, double n)
    {
        double vx = 0.0;
        for (var it = 0; it < 100; it++)
        {
            var e = Math.Exp(vx / (n * Vt));
            var f = (vs - vx) / r - is_ * (e - 1.0);
            var df = -1.0 / r - is_ * e / (n * Vt);
            var step = f / df;
            vx -= step;
            if (Math.Abs(step) < 1e-15) break;
        }
        return vx;
    }

    // -------------------------------------------------- DC operating point (cold + warm)

    [Fact]
    public void Diode_resistor_dc_operating_point_matches_hand_newton_from_cold_start()
    {
        const double vs = 1.0, r = 1000.0, is_ = 1e-14, n = 1.0;
        var net = Dc();
        NodeId a, x, g; DiodeId d; ResistorId res;
        using (var e = net.Edit())
        {
            a = e.AddNode(K(1)); x = e.AddNode(K(2)); g = e.AddReferenceNode(K(3));
            e.AddVoltageSource(a, g, vs, K(20));
            res = e.AddResistor(a, x, r, K(10));
            d = e.AddDiode(x, g, new DiodeParams(is_, n, 0.0), K(11));   // anode x, cathode gnd
        }

        net.SolveOperatingPoint();                                       // cold start: Vd seeded at 0

        var vx = net.Solution.Voltage(x);
        var vxRef = ReferenceVx(vs, r, is_, n);                          // ≈ 0.6291466 V
        Assert.True(net.Solution.IsLive(net.IslandOf(x)));
        Assert.Equal(vxRef, vx, 3);                                      // within 1e-3 V of the hand-Newton
        Assert.InRange(vx, 0.55, 0.70);                                  // a silicon forward drop

        // Nonlinear KCL at x holds physically: resistor current = true diode current.
        var iRes = (vs - vx) / r;
        var iDiode = net.Solution.Current(d);                            // I(Vx), anode→cathode
        Assert.True(iDiode > 0, "forward-biased diode conducts a positive anode→cathode current");
        Assert.True(Math.Abs(iRes - iDiode) < 1e-5, $"KCL residual {Math.Abs(iRes - iDiode):E3} A too large");
        Assert.Equal(net.Solution.Current(res), iDiode, 6);             // series ⇒ same current

        // Converged linear system is finite everywhere (api.md §20).
        var inv = net.Islands.Of(x).CheckInvariants(InvariantChecks.All);
        Assert.True(inv.AllFinite);
        Assert.True(inv.MaxKclResidual < 1e-9);
    }

    [Fact]
    public void Diode_operating_point_matches_from_a_warm_start_after_a_source_step()
    {
        const double r = 1000.0, is_ = 1e-14, n = 1.0;
        var net = Dc();
        NodeId a, x, g; DiodeId d;
        using (var e = net.Edit())
        {
            a = e.AddNode(K(1)); x = e.AddNode(K(2)); g = e.AddReferenceNode(K(3));
            e.AddVoltageSource(a, g, 1.0, K(20));
            e.AddResistor(a, x, r, K(10));
            d = e.AddDiode(x, g, new DiodeParams(is_, n, 0.0), K(11));
        }
        net.SolveOperatingPoint();
        var srcRef = net.TryResolve(K(20), out var scomp) ? scomp : default;
        var src = new VSourceId(srcRef.Slot, srcRef.Gen, srcRef.Net);

        // Step the source to 2 V and re-solve: this starts Newton WARM from the 1 V
        // operating point (Vd persisted) and must land on the new hand-Newton solution.
        net.Drive(src, 2.0);
        net.SolveOperatingPoint();

        var vx = net.Solution.Voltage(x);
        Assert.Equal(ReferenceVx(2.0, r, is_, n), vx, 3);               // ≈ 0.6623 V
        Assert.True(Math.Abs((2.0 - vx) / r - net.Solution.Current(d)) < 1e-5);
    }

    // ---------------------------------------------- converged re-solve: 1 iter, no refactor

    [Fact]
    public void Converged_diode_island_resolves_in_one_iteration_with_no_refactor()
    {
        // THE tier mechanism (solver.md): a settled nonlinear island relinearizes to the
        // SAME Geq, the ε-gate absorbs it, and no epoch bump ⇒ no refactor. Re-solving a
        // converged diode circuit is therefore one Newton iteration and zero tier-2 work.
        var net = Dc();
        NodeId a, x, g;
        using (var e = net.Edit())
        {
            a = e.AddNode(K(1)); x = e.AddNode(K(2)); g = e.AddReferenceNode(K(3));
            e.AddVoltageSource(a, g, 1.0, K(20));
            e.AddResistor(a, x, 1000.0, K(10));
            e.AddDiode(x, g, DiodeParams.Default, K(11));
        }

        net.SolveOperatingPoint();                                       // cold: several refactors as Vd ramps
        Assert.True(net.LastTickStats.Refactorizations >= 1);
        for (var i = 0; i < 8; i++) net.SolveOperatingPoint();          // settle to the fixed point

        net.SolveOperatingPoint();                                       // the settled re-solve
        Assert.Equal(0, net.LastTickStats.Refactorizations);            // ε-gate froze Geq ⇒ tier 1 only
        Assert.Equal(0, net.LastTickStats.IslandRebuilds);
        Assert.InRange(net.LastTickStats.NewtonIterations, 1, 2);       // converges immediately
    }

    // ---------------------------------------------------------- half-wave rectifier (transient)

    [Fact]
    public void Half_wave_rectifier_runs_200_substeps_and_rectifies()
    {
        // 5 Hz, 5 V sine → diode → out; RC smoothing load (R=1k ‖ C=100 µF) to ground.
        // Mixed profile ⇒ the sine island subcycles (5 substeps/tick); 40 ticks = 200
        // substeps. The diode conducts only on the positive half, so `out` is rectified:
        // it stays non-negative and never exceeds the source amplitude. (The ORACLE stage
        // owns the physics; here we sign-check the trajectory and prove it never blows up.)
        const double tickDt = 0.05, amp = 5.0, freq = 5.0;
        var net = Mixed(tickDt);
        NodeId a, outN, g; DiodeId d;
        using (var e = net.Edit())
        {
            a = e.AddNode(K(1)); outN = e.AddNode(K(2)); g = e.AddReferenceNode(K(3));
            e.AddSineSource(a, g, new SineDrive(amp, freq, 0.0), K(20), StateKey.From(K(20)));
            d = e.AddDiode(a, outN, DiodeParams.Default, K(11));        // anode = source, cathode = out
            e.AddResistor(outN, g, 1000.0, K(10));
            e.AddCapacitor(outN, g, 1e-4, K(12), StateKey.From(K(12)));
        }

        double maxOut = double.MinValue, minOut = double.MaxValue;
        var substeps = 0;
        for (var t = 0; t < 40; t++)
        {
            net.Solve(new TickClock(t, tickDt));
            substeps += net.LastTickStats.Substeps;
            var vo = net.Solution.Voltage(outN);
            var va = net.Solution.Voltage(a);
            Assert.True(double.IsFinite(vo) && double.IsFinite(va), $"tick {t}: non-finite output");
            maxOut = Math.Max(maxOut, vo);
            minOut = Math.Min(minOut, vo);
            // The diode never sources current backwards (cathode→anode).
            Assert.True(net.Solution.Current(d) > -1e-6, $"tick {t}: diode conducted in reverse");
        }

        Assert.Equal(200, substeps);                                    // 40 ticks × 5 substeps
        Assert.True(maxOut > 1.0, $"rectified output never charged (peak {maxOut:F3} V)");
        Assert.True(minOut > -1e-3, $"rectified output went negative (min {minOut:E3} V)");
        Assert.True(maxOut < amp + 1e-3, $"output {maxOut:F3} V exceeded the source amplitude");
    }

    // ------------------------------------------------------------- reverse-bias sweep

    [Fact]
    public void Reverse_bias_sweep_stays_finite_and_blocks()
    {
        // Pathological sweep: drive the source from deep reverse to forward and back. The
        // diode blocks in reverse (current ≈ −Is, node ≈ the reverse source) and conducts
        // in forward; every point is finite (junction limiting keeps iterates bounded).
        const double r = 1000.0;
        var net = Dc();
        NodeId a, x, g; DiodeId d;
        using (var e = net.Edit())
        {
            a = e.AddNode(K(1)); x = e.AddNode(K(2)); g = e.AddReferenceNode(K(3));
            e.AddVoltageSource(a, g, 0.0, K(20));
            e.AddResistor(a, x, r, K(10));
            d = e.AddDiode(x, g, DiodeParams.Default, K(11));
        }
        net.TryResolve(K(20), out var sref);
        var src = new VSourceId(sref.Slot, sref.Gen, sref.Net);

        foreach (var vs in new[] { -5.0, -2.0, -1.0, -0.1, 0.0, 0.3, 0.6, 0.8, 1.0, 3.0, -3.0, -5.0 })
        {
            net.Drive(src, vs);
            net.SolveOperatingPoint();
            var vx = net.Solution.Voltage(x);
            var i = net.Solution.Current(d);
            Assert.True(double.IsFinite(vx) && double.IsFinite(i), $"Vs={vs}: non-finite");
            if (vs <= -0.1)
            {
                Assert.True(i < 1e-9, $"Vs={vs}: reverse diode should not conduct (I={i:E3})");
                Assert.True(vx < 0.0 && vx > vs - 1e-6, $"Vs={vs}: reverse node {vx} out of band");
            }
            else if (vs >= 0.6)
            {
                Assert.True(i > 0, $"Vs={vs}: forward diode should conduct");
                Assert.InRange(vx, 0.4, 0.8);                           // clamped near the forward drop
            }
        }
    }

    // ------------------------------------------------- unvalidated device parameter: never NaN

    [Fact]
    public void Absurd_saturation_current_never_produces_a_nan()
    {
        // DiodeParams.SaturationCurrent is accepted unvalidated. With an unphysically large
        // Is (≥ nVt/√2 ≈ 18 mA) the junction-limiting critical voltage
        //   Vcrit = nVt·ln(nVt/(√2·Is))
        // goes ≤ 0 (here Is = 1 A ⇒ Vcrit ≈ −0.103 V), so a Newton iterate can enter the
        // vold≤0 limiting branch with a non-positive argument to Log. The "never NaN"
        // guarantee (api.md §20) must hold regardless of the parameter: every solved point
        // stays finite. (Physics is meaningless here; only finiteness is asserted.)
        // Is = 1 A ⇒ Vcrit ≈ −0.103 V; R = 1 Ω. A deep-reverse warm start followed by a
        // move to mild reverse makes a Newton iterate land in the (Vcrit, 0) window with a
        // more-negative linearization point, so junction limiting takes the vold≤0 branch
        // with a non-positive Log argument (verified by instrumentation: this exact path
        // fires the branch). Before the guard, Log(negative) = NaN poisoned the iterate and
        // the outer NonFinite handler faulted this *solvable* island (holding Vx ≈ −4 V).
        // The guard keeps the iterate finite so the island converges to its true mild-reverse
        // operating point (Vx ≈ −0.07 V) — Ready, never NaN (api.md §20).
        const double r = 1.0, is_ = 1.0, n = 1.0;
        var net = Dc();
        NodeId a, x, g; DiodeId d;
        using (var e = net.Edit())
        {
            a = e.AddNode(K(1)); x = e.AddNode(K(2)); g = e.AddReferenceNode(K(3));
            e.AddVoltageSource(a, g, 0.0, K(20));
            e.AddResistor(a, x, r, K(10));
            d = e.AddDiode(x, g, new DiodeParams(is_, n, 0.0), K(11));
        }
        net.TryResolve(K(20), out var sref);
        var src = new VSourceId(sref.Slot, sref.Gen, sref.Net);

        net.Drive(src, -5.0);
        net.SolveOperatingPoint();                                   // deep-reverse warm start
        net.Drive(src, -1.0);
        net.SolveOperatingPoint();                                   // step to mild reverse ⇒ limiting branch

        var isl = net.Islands.Of(x);
        Assert.Equal(IslandStatus.Ready, isl.Status);                // solvable: converges, not spuriously faulted
        var vx = net.Solution.Voltage(x);
        var i = net.Solution.Current(d);
        Assert.True(double.IsFinite(vx), "non-finite node voltage (NaN from Log of a non-positive argument)");
        Assert.True(double.IsFinite(i), "non-finite diode current");
        Assert.InRange(vx, -0.15, 0.0);                              // mild-reverse operating point (≈ −0.07 V)
        foreach (var v in net.Solution.RawVector(isl.Id))
            Assert.True(double.IsFinite(v), "published vector held a non-finite entry");
    }

    // ------------------------------------------------- absurd circuit: legible fault + recovery

    [Fact]
    public void Opposing_ideal_sources_fault_legibly_hold_the_solution_and_auto_recover()
    {
        // Two ideal voltage sources pinning node a to +5 V AND −5 V through diodes: a
        // contradictory system (two rank-deficient constraint rows on the same node ⇒
        // singular, independent of the values — driving them equal does NOT help). It must
        // fault LEGIBLY — no NaN anywhere — hold the previous solution, and the fault must
        // clear on the next tier-2/3 change (state machine: Faulted → Dirty → retry).
        var net = Dc();
        NodeId a, b, g; VSourceId v2; ResistorId r;
        DiodeId d;
        using (var e = net.Edit())
        {
            a = e.AddNode(K(1)); b = e.AddNode(K(2)); g = e.AddReferenceNode(K(3));
            e.AddVoltageSource(a, g, 5.0, K(20));
            v2 = e.AddVoltageSource(a, g, -5.0, K(21));                 // opposes the first on the same node
            d = e.AddDiode(a, b, DiodeParams.Default, K(11));
            r = e.AddResistor(b, g, 1000.0, K(10));
        }

        net.SolveOperatingPoint();

        var isl = net.Islands.Of(a);
        Assert.Equal(IslandStatus.Faulted, isl.Status);
        var fault = isl.Fault;
        Assert.True(fault.Kind == FaultKind.NewtonDiverged || fault.Kind == FaultKind.Singular,
            $"expected a Newton/Singular fault, got {fault.Kind}");
        Assert.False(net.Solution.IsLive(isl.Id));                      // de-energized

        // No NaN/∞ ever left the API — scan every readable value (api.md §20).
        Assert.True(double.IsFinite(net.Solution.Voltage(a)));
        Assert.True(double.IsFinite(net.Solution.Voltage(b)));
        Assert.True(double.IsFinite(net.Solution.Current(d)));
        foreach (var v in net.Solution.RawVector(isl.Id))
            Assert.True(double.IsFinite(v), "faulted island published a non-finite vector entry");

        // A tier-2 Adjust re-arms the retry (Faulted → Dirty) but cannot repair the
        // structural contradiction, so it re-faults — still legibly, still finite.
        net.Adjust(r, 2000.0);
        net.SolveOperatingPoint();
        Assert.Equal(IslandStatus.Faulted, net.Islands.Of(a).Status);
        Assert.True(double.IsFinite(net.Solution.Voltage(a)));

        // Remove the redundant opposing source (the actual fix) and re-solve: the island
        // rebuilds, retries, and converges to a real operating point — auto-recovery. The
        // rebuild reissues handles, so re-resolve node/diode by their ExternalKeys (§16).
        using (var e = net.Edit()) e.Remove(v2);
        net.SolveOperatingPoint();
        Assert.True(net.TryResolveNode(K(1), out var a2));
        Assert.True(net.TryResolve(K(11), out var dRef));
        var isl2 = net.Islands.Of(a2);
        Assert.Equal(IslandStatus.Ready, isl2.Status);
        Assert.True(net.Solution.IsLive(isl2.Id));
        Assert.Equal(5.0, net.Solution.Voltage(a2), 6);                 // the surviving source pins a = 5 V
        Assert.True(net.Solution.Current(new DiodeId(dRef.Slot, dRef.Gen, dRef.Net)) > 0);   // forward diode conducts
        Assert.True(net.TryResolveNode(K(2), out var b2) && double.IsFinite(net.Solution.Voltage(b2)));
    }
}
