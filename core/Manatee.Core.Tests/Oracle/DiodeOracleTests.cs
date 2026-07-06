using Manatee.Core;
using Manatee.Oracle;

namespace Manatee.Core.Tests.Oracle;

/// <summary>
/// Pins the SPICE-conventional exponential diode (Newton companion: Geq ‖ Ieq, junction
/// limiting) against ngspice — the EE-content policy: the junction physics is established
/// by the oracle, never by assertion (CLAUDE.md). The unit tests
/// (<see cref="Manatee.Core.Tests.Netlist.DiodeNewtonTests"/>) pin the loop machinery; here
/// a diode-resistor operating point is compared to ngspice's DC solution of the same deck.
/// ngspice runs at 26.85 °C (= 300.00 K) so its thermal voltage matches the solver's fixed
/// Vt(300 K) — a wrong companion sign, scale, or Vt fails the comparison.
/// Filter out locally with: dotnet test --filter Category!=Oracle
/// </summary>
[Trait("Category", "Oracle")]
public sealed class DiodeOracleTests
{
    private static ExternalKey K(ulong id) => new(id);

    [Theory]
    [InlineData(1.0)]
    [InlineData(2.0)]
    [InlineData(0.7)]
    public void Diode_resistor_operating_point_matches_ngspice(double vs)
    {
        const double r = 1000.0, is_ = 1e-14, n = 1.0;

        // ngspice at T = TNOM = 26.85 °C = 300.00 K, so its Vt = k·T/q matches the solver's
        // fixed Vt(300 K). Rs defaults to 0 (the ideal junction the phase-4 model stamps).
        var raw = new NgspiceRunner().Run(
            "diode-resistor op",
            $"""
            V1 in 0 DC {vs}
            R1 in out 1k
            D1 out 0 DMOD
            .model DMOD D(Is=1e-14 N=1)
            .options temp=26.85 tnom=26.85
            """,
            "op");
        var vOutSpice = raw.Get("v(out)");
        var iSpice = System.Math.Abs(raw.Get("i(v1)"));   // series current; source reads −I

        var net = new Core.Netlist(new NetlistOptions
        {
            Profile = SolverProfile.Dc(0.5),
            Wiring = WiringPolicy.ExplicitOnly(),
            Debug = DebugLevel.Asserts,
        });
        NodeId a, x, g; DiodeId d;
        using (var e = net.Edit())
        {
            a = e.AddNode(K(1)); x = e.AddNode(K(2)); g = e.AddReferenceNode(K(3));
            e.AddVoltageSource(a, g, vs, K(20));
            e.AddResistor(a, x, r, K(10));
            d = e.AddDiode(x, g, new DiodeParams(is_, n, 0.0), K(11));
        }
        net.SolveOperatingPoint();

        var vOut = net.Solution.Voltage(x);
        var iMine = net.Solution.Current(d);

        // Node voltage: a forward drop matches to well under a mV. Current is
        // exp-sensitive, so compare at a slightly looser relative tolerance.
        OracleAssert.Close(vOutSpice, vOut, relativeTolerance: 2e-3, absoluteTolerance: 5e-4);
        OracleAssert.Close(iSpice, iMine, relativeTolerance: 1e-2);
    }
}
