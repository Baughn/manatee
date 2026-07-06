using System;
using Manatee.Core;
using Manatee.Core.Devices;
using static Manatee.Core.Tests.Devices.DevicesTestKit;

namespace Manatee.Core.Tests.Devices;

/// <summary>
/// <see cref="AdaptedSource"/>: an EMF behind an internal series resistance with an
/// across-tick advertised-power current clamp (api.md §18). The supply-side dual of
/// <see cref="AdaptedLoad"/> — it droops its EMF so it never sustains more than its
/// advertised power into a heavy load.
/// </summary>
public sealed class AdaptedSourceTests
{
    [Fact]
    public void Light_load_sees_the_nominal_emf()
    {
        // 24 V, Rint 1 Ω, 100 W cap; light 100 Ω load draws ~0.24 A ⇒ ~5.7 W ≪ cap,
        // so no droop: V_out ≈ 24·100/101 ≈ 23.76 V.
        var net = Net();
        NodeId a, g;
        using (var e = net.Edit())
        {
            a = e.AddNode(K(1)); g = e.AddReferenceNode(K(2));
            e.AddResistor(a, g, 100.0, K(11));
        }
        net.SolveOperatingPoint();
        var host = new DeviceHost(net);
        host.Add(new AdaptedSource(24.0, 1.0, 100.0), stackalloc NodeId[2] { a, g }, K(20), StateKey.From(K(20)));

        long tick = 0;
        for (var i = 0; i < 50; i++) Step(net, host, 0.05, ref tick);
        Assert.Equal(24.0 * 100.0 / 101.0, net.Solution.Voltage(a), 2);
    }

    [Fact]
    public void Heavy_load_is_clamped_to_the_advertised_power()
    {
        // 24 V, Rint 1 Ω, 100 W cap; a stiff 1 Ω load would pull 144 W uncapped
        // (I = 12 A, V = 12 V). The EMF droops so the delivered power settles at the
        // advertised 100 W ⇒ V_out ≈ 10 V (P = V²/R = 100/1).
        var net = Net();
        NodeId a, g;
        using (var e = net.Edit())
        {
            a = e.AddNode(K(1)); g = e.AddReferenceNode(K(2));
            e.AddResistor(a, g, 1.0, K(11));
        }
        net.SolveOperatingPoint();
        var host = new DeviceHost(net);
        host.Add(new AdaptedSource(24.0, 1.0, 100.0), stackalloc NodeId[2] { a, g }, K(20), StateKey.From(K(20)));

        long tick = 0;
        for (var i = 0; i < 300; i++) Step(net, host, 0.05, ref tick);

        var v = net.Solution.Voltage(a);
        var pOut = v * v / 1.0;
        Assert.True(pOut <= 100.0 * 1.05, $"delivered {pOut:F2} W exceeds the 100 W advertised cap");
        Assert.True(pOut >= 100.0 * 0.90, $"delivered {pOut:F2} W is far under the cap (over-drooped)");
    }
}
