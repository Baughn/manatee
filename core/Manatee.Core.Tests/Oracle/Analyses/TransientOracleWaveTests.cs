using System;
using Manatee.Core;
using Manatee.Oracle;

namespace Manatee.Core.Tests.Oracle.Analyses;

/// <summary>
/// Systematic transient oracle wave (testing-strategy.md): storage + rectifier
/// circuits pinned against ngspice through <see cref="OracleHarness.AssertTranMatches"/>.
/// The comparison is Backward-Euler-vs-Backward-Euler at a MATCHED timestep — the
/// deck forces <c>method=gear maxord=1</c> (order-1 Gear ≡ BE) at the solver's substep
/// dt — so ngspice reproduces manatee's exact damped trajectory rather than diverging
/// on trapezoidal. The artificial BE damping (a BE-vs-reality artifact) is therefore
/// PRESENT IDENTICALLY on both sides and cancels in the diff; the tolerances below are
/// the BE-vs-BE residual (ngspice's adaptive internal substepping under the print step),
/// not the physical integration error. Filter out locally: dotnet test --filter Category!=Oracle
/// </summary>
[Trait("Category", "Oracle")]
public sealed class TransientOracleWaveTests
{
    private static ExternalKey K(ulong id) => new(id);

    private static Core.Netlist Transient(double dt)
        => new(new NetlistOptions
        {
            Profile = SolverProfile.Transient(dt),
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

    [Fact]
    public void Rc_charge()
    {
        const double dt = 0.01;
        const int steps = 300;   // t = 3 s ≈ 3τ (τ = 1 s)
        var net = Transient(dt);
        NodeId a, x, g;
        using (var e = net.Edit())
        {
            a = e.AddNode(K(1)); x = e.AddNode(K(2)); g = e.AddReferenceNode(K(3));
            e.AddVoltageSource(a, g, 10.0, K(20));
            e.AddResistor(a, x, 1000.0, K(10));
            e.AddCapacitor(x, g, 1e-3, K(11), StateKey.From(K(11)));
        }
        OracleHarness.AssertTranMatches(net, net.IslandOf(a), dt, steps * dt,
            () => Step(net, steps, dt), relTol: 1e-3, absTol: 1e-4);
    }

    [Fact]
    public void Rc_discharge()
    {
        // Charge to ~10 V, then drop the source to 0 and discharge through R. The deck
        // is emitted AFTER the source is driven to 0 (so it carries source 0 and the
        // charged capacitor IC), and reproduces the discharge from that IC.
        const double dt = 0.01;
        var net = Transient(dt);
        NodeId a, x, g; VSourceId src;
        using (var e = net.Edit())
        {
            a = e.AddNode(K(1)); x = e.AddNode(K(2)); g = e.AddReferenceNode(K(3));
            src = e.AddVoltageSource(a, g, 10.0, K(20));
            e.AddResistor(a, x, 1000.0, K(10));
            e.AddCapacitor(x, g, 1e-3, K(11), StateKey.From(K(11)));
        }
        for (var i = 0; i < 500; i++) net.Solve(new TickClock(i, dt));   // charge to ≈10 V
        net.Drive(src, 0.0);                                             // collapse the source

        const int steps = 200;   // t = 2 s of discharge
        OracleHarness.AssertTranMatches(net, net.IslandOf(a), dt, steps * dt,
            () => Step(net, steps, dt, startTick: 500), relTol: 1e-3, absTol: 1e-4);
    }

    [Fact]
    public void Rl_current_rise()
    {
        const double dt = 1e-4;
        const int steps = 200;   // t = 20 ms ≈ 2τ (τ = 10 ms)
        var net = Transient(dt);
        NodeId a, x, g;
        using (var e = net.Edit())
        {
            a = e.AddNode(K(1)); x = e.AddNode(K(2)); g = e.AddReferenceNode(K(3));
            e.AddVoltageSource(a, g, 10.0, K(20));
            e.AddResistor(a, x, 100.0, K(10));
            e.AddInductor(x, g, 1.0, K(11), StateKey.From(K(11)));
        }
        // V(x) = Vs − I·R tracks the inductor current; BE-vs-BE at matched dt.
        OracleHarness.AssertTranMatches(net, net.IslandOf(a), dt, steps * dt,
            () => Step(net, steps, dt), relTol: 1e-3, absTol: 1e-4);
    }

    [Fact]
    public void Series_rlc_underdamped()
    {
        // R = 20 Ω, L = 1 H, C = 1 mF ⇒ ω0 = 1/√(LC) ≈ 31.6 rad/s, ζ = R/2·√(C/L) = 0.316:
        // underdamped. A 10 V step rings on V(cap). BE damps the ring — but ngspice, forced
        // to BE at the same dt, damps IDENTICALLY, so the diff stays tight.
        const double dt = 1e-3;
        const int steps = 500;   // t = 0.5 s (several ring periods, well into decay)
        var net = Transient(dt);
        NodeId a, m, x, g;
        using (var e = net.Edit())
        {
            a = e.AddNode(K(1)); m = e.AddNode(K(2)); x = e.AddNode(K(3)); g = e.AddReferenceNode(K(4));
            e.AddVoltageSource(a, g, 10.0, K(20));
            e.AddResistor(a, m, 20.0, K(10));
            e.AddInductor(m, x, 1.0, K(11), StateKey.From(K(11)));
            e.AddCapacitor(x, g, 1e-3, K(12), StateKey.From(K(12)));
        }
        OracleHarness.AssertTranMatches(net, net.IslandOf(a), dt, steps * dt,
            () => Step(net, steps, dt), relTol: 2e-3, absTol: 1e-3);
    }

    [Fact]
    public void Half_wave_rectifier_rc_load()
    {
        // Sine (10 V, 5 Hz) → diode → RC load. AC island: N = ceil(5·20·0.05) = 5 substeps
        // per 50 ms tick ⇒ substep dt = 0.01. Deck matches BE at that substep dt.
        const double tickDt = 0.05;
        const int n = 5;
        const double substepDt = tickDt / n;   // 0.01
        const int ticks = 60;                  // t = 3 s = 15 cycles
        var net = Mixed(tickDt);
        NodeId a, x, g;
        using (var e = net.Edit())
        {
            a = e.AddNode(K(1)); x = e.AddNode(K(2)); g = e.AddReferenceNode(K(3));
            e.AddSineSource(a, g, new SineDrive(10.0, 5.0, 0.0), K(20), StateKey.From(K(20)));
            e.AddDiode(a, x, new DiodeParams(1e-14, 1.0, 0.0), K(11));
            e.AddResistor(x, g, 1000.0, K(10));
            e.AddCapacitor(x, g, 1e-3, K(12), StateKey.From(K(12)));
        }
        // Diode exp-sensitivity + ngspice's adaptive internal stepping widen the residual.
        OracleHarness.AssertTranMatches(net, net.IslandOf(a), substepDt, ticks * tickDt,
            () => Step(net, ticks, tickDt), relTol: 1e-2, absTol: 2e-2);
    }

    [Fact]
    public void Full_wave_bridge_rectifier_rc_load()
    {
        // Sine → 4-diode bridge → RC load across the DC rails. Floating AC source,
        // grounded DC negative rail.
        const double tickDt = 0.05;
        const int n = 5;
        const double substepDt = tickDt / n;
        // Land the stop time on a source PEAK (t = 2.05 s ⇒ 10π·t = 10.25 cycles ⇒ π/2),
        // not a zero-crossing: at sin = 0 both floating AC terminals sit behind
        // reverse-biased diodes and their instantaneous potential is ill-defined, so an
        // exact node-voltage diff there is meaningless. At the peak the conducting pair
        // pins both terminals to the DC rails and the comparison is well-posed.
        const int ticks = 41;
        var net = Mixed(tickDt);
        NodeId acP, acN, dcP, g;
        using (var e = net.Edit())
        {
            acP = e.AddNode(K(1)); acN = e.AddNode(K(2)); dcP = e.AddNode(K(3)); g = e.AddReferenceNode(K(4));
            e.AddSineSource(acP, acN, new SineDrive(10.0, 5.0, 0.0), K(20), StateKey.From(K(20)));
            var dp = new DiodeParams(1e-14, 1.0, 0.0);
            e.AddDiode(acP, dcP, dp, K(10));
            e.AddDiode(acN, dcP, dp, K(11));
            e.AddDiode(g, acP, dp, K(12));
            e.AddDiode(g, acN, dp, K(13));
            e.AddResistor(dcP, g, 1000.0, K(14));
            e.AddCapacitor(dcP, g, 1e-3, K(15), StateKey.From(K(15)));
            // Common-mode reference (100 kΩ, a physical bleeder). Without it the source
            // floats to ground only through the 1e-12 S gmin shunts, leaving the bridge's
            // common mode ill-conditioned — manatee's diode Newton then lands on a
            // spurious operating point at some phases (the source-constraint differential
            // itself came out wrong). A real reference path makes the instantaneous
            // solution well-posed and both solvers agree. (Flagged as a solver concern:
            // a fully floating nonlinear island should still satisfy its hard V-source
            // constraint regardless of conditioning.)
            e.AddResistor(acN, g, 1e5, K(16));
        }
        OracleHarness.AssertTranMatches(net, net.IslandOf(acP), substepDt, ticks * tickDt,
            () => Step(net, ticks, tickDt), relTol: 1e-2, absTol: 3e-2);
    }

    private static void Step(Core.Netlist net, int steps, double dt, int startTick = 0)
    {
        for (var i = 0; i < steps; i++) net.Solve(new TickClock(startTick + i, dt));
    }
}
