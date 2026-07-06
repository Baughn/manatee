using Manatee.Core;
using Manatee.Oracle;

namespace Manatee.Core.Tests.Oracle;

/// <summary>
/// Pins the Backward-Euler capacitor and inductor companions against ngspice (the EE
/// content policy: correctness is established by the oracle, never by assertion). The
/// unit tests (<see cref="Manatee.Core.Tests.Netlist.TransientTests"/>) pin the exact BE
/// difference equation; here the manatee transient is stepped to a physical point and
/// compared to ngspice's transient solution of the same circuit. dt is small enough that
/// the BE trajectory sits within tolerance of the true (continuous) response ngspice
/// tracks, so a wrong companion sign/scale/time-constant fails the comparison.
/// Filter out locally with: dotnet test --filter Category!=Oracle
/// </summary>
[Trait("Category", "Oracle")]
public sealed class TransientOracleTests
{
    private static ExternalKey K(ulong id) => new(id);

    [Fact]
    public void Rc_charge_matches_ngspice_transient()
    {
        // RC: Vs = 10, R = 1k, C = 1mF ⇒ τ = 1 s. Step manatee 1000×1 ms to t = 1 s and
        // compare V(out) to ngspice's transient at the same stop time.
        const double vs = 10.0, r = 1000.0, c = 1e-3, dt = 1e-3;
        const int steps = 1000;

        var raw = new NgspiceRunner().Run(
            "rc charge",
            """
            V1 in 0 DC 10
            R1 in out 1k
            C1 out 0 1m ic=0
            """,
            "tran 1m 1s uic");
        var spice = raw.Get("v(out)", raw.PointCount - 1);

        var mine = SolveRcNode(vs, r, c, dt, steps);

        // Both settle to 10·(1−e⁻¹) ≈ 6.32 V at t = τ; BE vs ngspice's integrator agree
        // to well under 0.5 % at this step size.
        OracleAssert.Close(spice, mine, relativeTolerance: 5e-3);
        OracleAssert.Close(10.0 * (1.0 - System.Math.Exp(-1.0)), mine, relativeTolerance: 1e-2);
    }

    [Fact]
    public void Rl_current_rise_matches_ngspice_transient()
    {
        // RL: Vs = 10, R = 100, L = 1H ⇒ τ = 10 ms. Step manatee 1000×10 µs to t = 10 ms
        // and compare the inductor current to ngspice at the same stop time.
        const double vs = 10.0, r = 100.0, l = 1.0, dt = 1e-5;
        const int steps = 1000;

        var raw = new NgspiceRunner().Run(
            "rl rise",
            """
            V1 in 0 DC 10
            R1 in mid 100
            L1 mid 0 1 ic=0
            """,
            "tran 10u 10m uic");
        var spice = raw.Get("i(v1)", raw.PointCount - 1);   // series current; source reads −I

        var mine = SolveRlCurrent(vs, r, l, dt, steps);

        // I(τ) = (Vs/R)(1−e⁻¹) ≈ 6.32 mA. ngspice's i(v1) is the (negated) series current.
        OracleAssert.Close(System.Math.Abs(spice), mine, relativeTolerance: 5e-3);
        OracleAssert.Close((vs / r) * (1.0 - System.Math.Exp(-1.0)), mine, relativeTolerance: 1e-2);
    }

    private static double SolveRcNode(double vs, double r, double c, double dt, int steps)
    {
        var net = new Core.Netlist(new NetlistOptions
        {
            Profile = SolverProfile.Transient(dt),
            Wiring = WiringPolicy.ExplicitOnly(),
            Debug = DebugLevel.Asserts,
        });
        NodeId a, x, g;
        using (var e = net.Edit())
        {
            a = e.AddNode(K(1)); x = e.AddNode(K(2)); g = e.AddReferenceNode(K(3));
            e.AddVoltageSource(a, g, vs, K(20));
            e.AddResistor(a, x, r, K(10));
            e.AddCapacitor(x, g, c, K(11), StateKey.From(K(11)));
        }
        for (var n = 0; n < steps; n++) net.Solve(new TickClock(n, dt));
        return net.Solution.Voltage(x);
    }

    private static double SolveRlCurrent(double vs, double r, double l, double dt, int steps)
    {
        var net = new Core.Netlist(new NetlistOptions
        {
            Profile = SolverProfile.Transient(dt),
            Wiring = WiringPolicy.ExplicitOnly(),
            Debug = DebugLevel.Asserts,
        });
        NodeId a, x, g; InductorId ind;
        using (var e = net.Edit())
        {
            a = e.AddNode(K(1)); x = e.AddNode(K(2)); g = e.AddReferenceNode(K(3));
            e.AddVoltageSource(a, g, vs, K(20));
            e.AddResistor(a, x, r, K(10));
            ind = e.AddInductor(x, g, l, K(11), StateKey.From(K(11)));
        }
        for (var n = 0; n < steps; n++) net.Solve(new TickClock(n, dt));
        return net.Solution.Current(ind);
    }
}
