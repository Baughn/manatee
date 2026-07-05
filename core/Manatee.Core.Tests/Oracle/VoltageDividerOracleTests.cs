using Manatee.Oracle;

namespace Manatee.Core.Tests.Oracle;

/// <summary>
/// End-to-end proof of the oracle pipeline: deck → ngspice subprocess →
/// rawfile → tolerance assert. Every future component stamp gets pinned
/// through this same path. Filter out locally with:
///   dotnet test --filter Category!=Oracle
/// </summary>
[Trait("Category", "Oracle")]
public class VoltageDividerOracleTests
{
    [Fact]
    public void Ngspice_solves_a_voltage_divider()
    {
        var raw = new NgspiceRunner().Run(
            "voltage divider",
            """
            V1 in 0 DC 10
            R1 in out 1k
            R2 out 0 1k
            """,
            "op");

        OracleAssert.Close(10.0, raw.Get("v(in)"));
        OracleAssert.Close(5.0, raw.Get("v(out)"));
        // SPICE convention: source current flows + terminal → through source,
        // so a delivering source reads negative.
        OracleAssert.Close(-0.005, raw.Get("i(v1)"));
    }

    [Fact]
    public void Ngspice_runs_a_transient_rc_charge()
    {
        // RC charge: V=10, R=1k, C=1mF → τ = 1 s. At t = 1 s the capacitor
        // sits at 10·(1−e⁻¹) ≈ 6.321 V. Settled-value comparison per
        // testing-strategy.md; BE-vs-trap integrator drift stays inside 1%.
        var raw = new NgspiceRunner().Run(
            "rc charge",
            """
            V1 in 0 DC 10
            R1 in out 1k
            C1 out 0 1m ic=0
            """,
            "tran 10m 1s uic");

        var final = raw.Get("v(out)", raw.PointCount - 1);
        OracleAssert.Close(10.0 * (1 - Math.Exp(-1)), final, relativeTolerance: 1e-2);
    }
}
