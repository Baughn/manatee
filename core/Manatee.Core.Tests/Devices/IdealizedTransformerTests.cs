using Manatee.Core;
using Manatee.Core.Devices;
using static Manatee.Core.Tests.Devices.DevicesTestKit;

namespace Manatee.Core.Tests.Devices;

/// <summary>
/// The <see cref="IdealizedTransformer"/> composed over the same-matrix
/// <c>AddIdealTransformer</c> primitive (api.md §18; §6). The ideal core enforces
/// V_secondary = V_primary / n and ampere-turns exactly; the magnetizing shunt is an
/// honest device element. NOT a boundary coupler — one shared island, no exchange.
/// </summary>
public sealed class IdealizedTransformerTests
{
    [Fact]
    public void Voltage_ratio_is_the_turns_ratio_and_power_is_conserved()
    {
        // n = 2 step-down, stiff 10 V primary, 5 Ω secondary load.
        //   V_sec = V_pri / n = 10 / 2 = 5 V
        //   i_sec = 5 / 5 = 1 A ;  i_pri = i_sec / n = 0.5 A (ampere-turns)
        //   P_in = 10·0.5 = 5 W = 5·1 = 5 W = P_out  ⇒ conserved.
        var net = Net();
        NodeId aPos, bPos, g;
        VSourceId src; ResistorId load;
        using (var e = net.Edit())
        {
            aPos = e.AddNode(K(1)); g = e.AddReferenceNode(K(2)); bPos = e.AddNode(K(3));
            src = e.AddVoltageSource(aPos, g, 10.0, K(10));
            load = e.AddResistor(bPos, g, 5.0, K(11));
        }

        var host = new DeviceHost(net);
        var xf = new IdealizedTransformer(new TransformerParams(2.0));
        Span<NodeId> terms = stackalloc NodeId[4] { aPos, g, bPos, g };
        host.Add(xf, terms, K(20), StateKey.From(K(20)));
        net.SolveOperatingPoint();

        Assert.Equal(10.0, net.Solution.Voltage(aPos), 6);
        Assert.Equal(5.0, net.Solution.Voltage(bPos), 6);

        // Secondary load power and primary source current confirm ideal conservation.
        var pLoad = net.Solution.Voltage(bPos) * net.Solution.Voltage(bPos) / 5.0;
        Assert.Equal(5.0, pLoad, 6);
        Assert.Equal(0.5, System.Math.Abs(net.Solution.Current(src)), 6);
        _ = load;
    }

    [Fact]
    public void Magnetizing_shunt_draws_extra_primary_current()
    {
        // A magnetizing shunt across the primary draws V_pri/Rmag on top of the
        // reflected load current. With Rmag = 100 Ω and 10 V primary that is 0.1 A,
        // so the primary source now sources 0.5 + 0.1 = 0.6 A.
        var net = Net();
        NodeId aPos, bPos, g;
        VSourceId src;
        using (var e = net.Edit())
        {
            aPos = e.AddNode(K(1)); g = e.AddReferenceNode(K(2)); bPos = e.AddNode(K(3));
            src = e.AddVoltageSource(aPos, g, 10.0, K(10));
            e.AddResistor(bPos, g, 5.0, K(11));
        }

        var host = new DeviceHost(net);
        host.Add(new IdealizedTransformer(new TransformerParams(2.0, 0.0, MagnetizingOhms: 100.0)),
            stackalloc NodeId[4] { aPos, g, bPos, g }, K(20), StateKey.From(K(20)));
        net.SolveOperatingPoint();

        Assert.Equal(0.6, System.Math.Abs(net.Solution.Current(src)), 6);
    }
}
