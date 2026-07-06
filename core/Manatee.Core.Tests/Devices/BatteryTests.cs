using System;
using System.Buffers;
using Manatee.Core;
using Manatee.Core.Devices;
using static Manatee.Core.Tests.Devices.DevicesTestKit;

namespace Manatee.Core.Tests.Devices;

/// <summary>
/// <see cref="Battery"/>: EMF + Rint + state-of-charge integrator, per-chemistry
/// parameter sets, SoC serialised through the phase-6 seam (api.md §18; design.md
/// battery arc — structure now, chemistry stubbed). Hand-computed discharge.
/// </summary>
public sealed class BatteryTests
{
    private static (Core.Netlist net, DeviceHost host, Battery batt, NodeId a) Rig(in BatteryParams p, double loadOhms)
    {
        var net = Net();
        NodeId a, g;
        using (var e = net.Edit())
        {
            a = e.AddNode(K(1)); g = e.AddReferenceNode(K(2));
            e.AddResistor(a, g, loadOhms, K(11));
        }
        net.SolveOperatingPoint();
        var host = new DeviceHost(net);
        var batt = host.Add(new Battery(p), stackalloc NodeId[2] { a, g }, K(20), StateKey.From(K(20)));
        net.SolveOperatingPoint();
        return (net, host, batt, a);
    }

    [Fact]
    public void Ocv_curve_interpolates_between_empty_and_full()
    {
        var p = new BatteryParams(ocvFull: 10.0, ocvEmpty: 8.0, internalOhms: 1.0, capacityCoulombs: 50.0);
        Assert.Equal(10.0, p.Ocv(1.0), 12);
        Assert.Equal(8.0, p.Ocv(0.0), 12);
        Assert.Equal(9.0, p.Ocv(0.5), 12);   // midpoint
        Assert.Equal(8.0, p.Ocv(-1.0), 12);  // clamped
        Assert.Equal(10.0, p.Ocv(2.0), 12);  // clamped
    }

    [Fact]
    public void Presets_are_distinct_and_ordered()
    {
        Assert.True(BatteryParams.VoltaicPile.OcvFull < BatteryParams.LiIon.OcvFull);
        Assert.True(BatteryParams.LiIon.OcvFull < BatteryParams.LeadAcid.OcvFull);
        Assert.True(BatteryParams.LeadAcid.CapacityCoulombs > BatteryParams.LiIon.CapacityCoulombs);
    }

    [Fact]
    public void Coulomb_counting_drains_soc_and_drops_ocv()
    {
        // 10 V full / 8 V empty, Rint 1 Ω, capacity Q = 50 C, load 9 Ω.
        // At SoC 1: OCV 10 V, I = 10/(1+9) = 1 A, so SoC falls ≈ I·dt/Q each tick.
        var p = new BatteryParams(10.0, 8.0, 1.0, 50.0);
        var (net, host, batt, a) = Rig(p, 9.0);
        const double dt = 0.05;

        // The load current (a→g) equals the battery's Rint current (series loop), which
        // is exactly what the SoC integrator counts. Read it from the SAME Previous
        // solution the device Tick will read, so the hand integral matches bit-close.
        Assert.True(net.TryResolve(K(11), out var loadRef));
        var load = new ResistorId(loadRef.Slot, loadRef.Gen, loadRef.Net);

        var soc = 1.0;
        var chargeDrawn = 0.0;
        long tick = 0;
        for (var i = 0; i < 100; i++)
        {
            chargeDrawn += net.Solution.Current(load) * dt;
            Step(net, host, dt, ref tick);
            Assert.True(batt.StateOfCharge <= soc + 1e-12, "SoC must be monotone non-increasing under discharge");
            soc = batt.StateOfCharge;
        }

        // The integrator is exactly SoC = 1 − ∫I dt / Q (Backward-Euler coulomb count).
        Assert.Equal(1.0 - chargeDrawn / p.CapacityCoulombs, batt.StateOfCharge, 6);
        Assert.True(batt.StateOfCharge < 1.0, "battery should have drained");
        Assert.Equal(p.Ocv(batt.StateOfCharge), batt.OpenCircuitVoltage, 12);
        Assert.True(batt.OpenCircuitVoltage < 10.0, "OCV should have dropped as SoC fell");
        _ = a;
    }

    [Fact]
    public void Soc_serializes_through_the_snapshot_stream()
    {
        var p = new BatteryParams(10.0, 8.0, 1.0, 50.0);
        var (net, host, batt, a) = Rig(p, 9.0);
        long tick = 0;
        for (var i = 0; i < 20; i++) Step(net, host, 0.05, ref tick);
        var soc = batt.StateOfCharge;
        Assert.True(soc < 1.0);

        var w = new ArrayBufferWriter<byte>();
        net.Islands.Of(a).Snapshot(w);

        for (var i = 0; i < 20; i++) Step(net, host, 0.05, ref tick);
        Assert.NotEqual(soc, batt.StateOfCharge);

        var res = net.Islands.Of(a).Restore(w.WrittenSpan);
        Assert.True(res.Ok);
        Assert.Equal(soc, batt.StateOfCharge, 12);
    }
}
