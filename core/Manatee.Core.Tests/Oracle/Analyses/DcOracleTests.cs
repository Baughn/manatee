using Manatee.Core;
using Manatee.Oracle;

namespace Manatee.Core.Tests.Oracle.Analyses;

/// <summary>
/// Systematic DC oracle wave (testing-strategy.md): every DC-capable primitive ×
/// the operating-point analysis, pinned against ngspice through the one differ
/// (<see cref="OracleHarness.AssertDcMatches"/>) at 0.1 % rel / 1 µV|µA abs. Each
/// case builds a manatee island, solves the operating point, and diffs every mapped
/// node voltage and V-source branch current against ngspice's solve of the emitted
/// deck. Filter out locally with: dotnet test --filter Category!=Oracle
/// </summary>
[Trait("Category", "Oracle")]
public sealed class DcOracleTests
{
    private static ExternalKey K(ulong id) => new(id);

    private static Core.Netlist Explicit()
        => new(new NetlistOptions
        {
            Profile = SolverProfile.Dc(0.5),
            Wiring = WiringPolicy.ExplicitOnly(),
            Debug = DebugLevel.Asserts,
        });

    private static IslandId Island(Core.Netlist net, NodeId anyNode) => net.IslandOf(anyNode);

    [Fact]
    public void Voltage_divider()
    {
        var net = Explicit();
        NodeId a, b, g;
        using (var e = net.Edit())
        {
            a = e.AddNode(K(1)); b = e.AddNode(K(2)); g = e.AddReferenceNode(K(3));
            e.AddVoltageSource(a, g, 10.0, K(20));
            e.AddResistor(a, b, 1000.0, K(10));
            e.AddResistor(b, g, 2000.0, K(11));
        }
        net.SolveOperatingPoint();
        OracleHarness.AssertDcMatches(net, Island(net, a));
    }

    [Fact]
    public void Wheatstone_bridge()
    {
        // A driven bridge with an unbalanced galvanometer arm across the midpoints.
        var net = Explicit();
        NodeId top, l, r, g;
        using (var e = net.Edit())
        {
            top = e.AddNode(K(1)); l = e.AddNode(K(2)); r = e.AddNode(K(3)); g = e.AddReferenceNode(K(4));
            e.AddVoltageSource(top, g, 12.0, K(20));
            e.AddResistor(top, l, 100.0, K(10));
            e.AddResistor(l, g, 200.0, K(11));
            e.AddResistor(top, r, 150.0, K(12));
            e.AddResistor(r, g, 220.0, K(13));
            e.AddResistor(l, r, 330.0, K(14));   // bridge arm
        }
        net.SolveOperatingPoint();
        OracleHarness.AssertDcMatches(net, Island(net, top));
    }

    [Fact]
    public void Series_aiding_sources()
    {
        // Two sources in series aiding into a resistor: 6 V + 4 V across 500 Ω.
        var net = Explicit();
        NodeId a, mid, g;
        using (var e = net.Edit())
        {
            a = e.AddNode(K(1)); mid = e.AddNode(K(2)); g = e.AddReferenceNode(K(3));
            e.AddVoltageSource(a, mid, 6.0, K(20));
            e.AddVoltageSource(mid, g, 4.0, K(21));
            e.AddResistor(a, g, 500.0, K(10));
        }
        net.SolveOperatingPoint();
        OracleHarness.AssertDcMatches(net, Island(net, a));
    }

    [Fact]
    public void Current_source_into_parallel_resistors()
    {
        // 2 A pushed into a node feeding three parallel resistors to ground.
        var net = Explicit();
        NodeId a, g;
        using (var e = net.Edit())
        {
            a = e.AddNode(K(1)); g = e.AddReferenceNode(K(2));
            e.AddCurrentSource(g, a, 2.0, K(20));   // from ground → a: pumps +2 A into a
            e.AddResistor(a, g, 100.0, K(10));
            e.AddResistor(a, g, 200.0, K(11));
            e.AddResistor(a, g, 300.0, K(12));
        }
        net.SolveOperatingPoint();
        OracleHarness.AssertDcMatches(net, Island(net, a));
    }

    [Fact]
    public void Ideal_transformer_2to1_loaded()
    {
        // 2:1 step-down (turnsRatio n = Vp/Vs = 2) with a non-ideal primary source so
        // the reflected load current shapes the node voltages — verifying BOTH the
        // voltage-ratio and current-ratio constraints through voltages alone.
        //
        //   Vsrc=10 → Rp=10 Ω → aPos ; primary (aPos,g). secondary (bPos,g), RL=50 Ω.
        //   Reflected load = n²·RL = 4·50 = 200 Ω. i_p = 10/(10+200) = 0.0476190 A.
        //   V(aPos) = 10 − i_p·10 = 9.523810 V ; V(bPos) = V(aPos)/2 = 4.761905 V.
        var net = Explicit();
        NodeId src, aPos, bPos, g;
        using (var e = net.Edit())
        {
            src = e.AddNode(K(1)); aPos = e.AddNode(K(2)); bPos = e.AddNode(K(3)); g = e.AddReferenceNode(K(4));
            e.AddVoltageSource(src, g, 10.0, K(20));
            e.AddResistor(src, aPos, 10.0, K(10));
            e.AddIdealTransformer(aPos, g, bPos, g, 2.0, K(30));
            e.AddResistor(bPos, g, 50.0, K(11));
        }
        net.SolveOperatingPoint();

        // Hand calc pin (the arithmetic above), independent of ngspice.
        OracleAssert.Close(9.523809523809524, net.Solution.Voltage(aPos), 1e-6);
        OracleAssert.Close(4.761904761904762, net.Solution.Voltage(bPos), 1e-6);

        OracleHarness.AssertDcMatches(net, Island(net, aPos));
    }

    [Fact]
    public void Switch_closed_conducts()
    {
        var net = Explicit();
        NodeId a, b, g;
        using (var e = net.Edit())
        {
            a = e.AddNode(K(1)); b = e.AddNode(K(2)); g = e.AddReferenceNode(K(3));
            e.AddVoltageSource(a, g, 5.0, K(20));
            e.AddResistor(a, b, 100.0, K(10));
            e.AddSwitch(b, g, closed: true, K(30));
        }
        net.SolveOperatingPoint();
        OracleHarness.AssertDcMatches(net, Island(net, a));
    }

    [Fact]
    public void Switch_open_blocks()
    {
        var net = Explicit();
        NodeId a, b, g;
        using (var e = net.Edit())
        {
            a = e.AddNode(K(1)); b = e.AddNode(K(2)); g = e.AddReferenceNode(K(3));
            e.AddVoltageSource(a, g, 5.0, K(20));
            e.AddResistor(a, b, 100.0, K(10));
            e.AddSwitch(b, g, closed: false, K(30));
        }
        net.SolveOperatingPoint();
        // Open switch is a 1 GΩ modeling resistor on both sides — the deck models it
        // identically, so v(b) matches (a tiny divider, not exactly the source).
        OracleHarness.AssertDcMatches(net, Island(net, a), relTol: 1e-3, absTol: 1e-6);
    }

    [Fact]
    public void Diode_resistor_operating_point()
    {
        var net = Explicit();
        NodeId a, x, g;
        using (var e = net.Edit())
        {
            a = e.AddNode(K(1)); x = e.AddNode(K(2)); g = e.AddReferenceNode(K(3));
            e.AddVoltageSource(a, g, 1.0, K(20));
            e.AddResistor(a, x, 1000.0, K(10));
            e.AddDiode(x, g, new DiodeParams(1e-14, 1.0, 0.0), K(11));
        }
        net.SolveOperatingPoint();
        // Diode current is exp-sensitive; node voltage matches tightly, current looser.
        OracleHarness.AssertDcMatches(net, Island(net, a), relTol: 2e-3, absTol: 5e-4);
    }

    [Fact]
    public void Diode_bridge_rectifier_dc()
    {
        // A 4-diode bridge with a DC source across the AC terminals and a resistive
        // load across the DC rails: two diodes conduct, two block.
        var net = Explicit();
        NodeId acP, acN, dcP, g;
        using (var e = net.Edit())
        {
            acP = e.AddNode(K(1)); acN = e.AddNode(K(2)); dcP = e.AddNode(K(3)); g = e.AddReferenceNode(K(4));
            e.AddVoltageSource(acP, acN, 5.0, K(20));
            e.AddResistor(acN, g, 1.0, K(15));            // tie acN near ground so the island has a datum path
            var dp = new DiodeParams(1e-14, 1.0, 0.0);
            e.AddDiode(acP, dcP, dp, K(10));              // top-left → +rail
            e.AddDiode(acN, dcP, dp, K(11));              // top-right → +rail
            e.AddDiode(g, acP, dp, K(12));                // −rail(gnd) → bottom-left
            e.AddDiode(g, acN, dp, K(13));                // −rail(gnd) → bottom-right
            e.AddResistor(dcP, g, 1000.0, K(14));         // load across the DC rails
        }
        net.SolveOperatingPoint();
        OracleHarness.AssertDcMatches(net, Island(net, acP), relTol: 2e-3, absTol: 5e-4);
    }
}
