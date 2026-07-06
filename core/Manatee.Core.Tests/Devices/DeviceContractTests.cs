using System;
using System.Buffers;
using Manatee.Core;
using Manatee.Core.Devices;
using static Manatee.Core.Tests.Devices.DevicesTestKit;

namespace Manatee.Core.Tests.Devices;

/// <summary>
/// The device base contract (api.md §18): terminal arity, the key-allocation
/// contract (baseKey.Derive(ordinal) with stable per-role ordinals), and the
/// StateSize/SaveState/RestoreState wiring into the phase-6 StateUnit seam (one unit
/// per device under its StateKey).
/// </summary>
public sealed class DeviceContractTests
{
    private static (Core.Netlist net, DeviceHost host, NodeId a, NodeId g) Rig()
    {
        var net = Net();
        NodeId a, g;
        using (var e = net.Edit())
        {
            a = e.AddNode(K(1)); g = e.AddReferenceNode(K(2));
            e.AddVoltageSource(a, g, 24.0, K(10));
        }
        net.SolveOperatingPoint();
        return (net, new DeviceHost(net), a, g);
    }

    [Fact]
    public void Add_rejects_wrong_terminal_arity()
    {
        var (_, host, a, _) = Rig();
        var threw = false;
        try
        {
            Span<NodeId> one = stackalloc NodeId[1] { a };   // Battery needs 2 terminals
            host.Add(new Battery(BatteryParams.LiIon), one, K(100), StateKey.From(K(100)));
        }
        catch (ArgumentException) { threw = true; }
        Assert.True(threw);
    }

    [Fact]
    public void Device_component_keys_derive_from_baseKey_by_stable_ordinal()
    {
        var (net, host, a, g) = Rig();
        // A Battery mints ordinals 0 (EMF source), 1 (Rint), 2 (internal node).
        var baseKey = K(200);
        Span<NodeId> terms = stackalloc NodeId[2] { a, g };
        host.Add(new Battery(BatteryParams.LiIon), terms, baseKey, StateKey.From(baseKey));

        Assert.True(net.TryResolve(baseKey.Derive(0), out var emf));
        Assert.Equal(ComponentKind.VSource, emf.Kind);
        Assert.True(net.TryResolve(baseKey.Derive(1), out var rint));
        Assert.Equal(ComponentKind.Resistor, rint.Kind);
        Assert.True(net.TryResolveNode(baseKey.Derive(2), out _));   // internal node
    }

    [Fact]
    public void Device_state_registers_one_unit_and_round_trips_through_the_snapshot_stream()
    {
        // Dedicated battery + load rig (no competing stiff source, so it discharges).
        var net = Net();
        NodeId a, g;
        using (var e = net.Edit())
        {
            a = e.AddNode(K(1)); g = e.AddReferenceNode(K(2));
            e.AddResistor(a, g, 10.0, K(11));   // load
        }
        net.SolveOperatingPoint();
        var host = new DeviceHost(net);

        var baseKey = K(300);
        Span<NodeId> terms = stackalloc NodeId[2] { a, g };
        var batt = host.Add(new Battery(BatteryParams.LiIon, initialSoc: 0.9), terms, baseKey, StateKey.From(baseKey));
        net.SolveOperatingPoint();

        var isl = net.Islands.Of(a);
        Assert.Equal(1, isl.StateUnitCount);   // exactly ONE device unit

        var w = new ArrayBufferWriter<byte>();
        isl.Snapshot(w);
        Assert.Equal(isl.SnapshotSize, w.WrittenCount);

        // Mutate the SoC, then restore — it must come back exactly (one memcpy).
        long tick = 0;
        for (var i = 0; i < 5; i++) Step(net, host, 0.05, ref tick);
        Assert.True(batt.StateOfCharge < 0.9);

        var res = net.Islands.Of(a).Restore(w.WrittenSpan);
        Assert.Equal(1, res.Matched);
        Assert.True(res.Ok);
        Assert.Equal(0.9, batt.StateOfCharge, 12);
    }

    [Fact]
    public void Stateless_device_registers_no_state_unit()
    {
        var (net, host, a, g) = Rig();
        Span<NodeId> terms = stackalloc NodeId[2] { a, g };
        host.Add(new AdaptedSource(24.0, 1.0, 100.0), terms, K(400), StateKey.From(K(400)));
        net.SolveOperatingPoint();
        Assert.Equal(0, net.Islands.Of(a).StateUnitCount);   // AdaptedSource is stateless
    }

    // A release-sentinel netlist (Debug.Off): a stale internal handle degrades to a
    // COUNTED no-op instead of throwing, so this test can observe the DeviceHost re-pin
    // boundary directly (api.md §16/§18).
    private static Core.Netlist ReleaseNet(double dt = 0.05)
        => new(new NetlistOptions
        {
            Profile = SolverProfile.Mixed(dt),
            Wiring = WiringPolicy.ExplicitOnly(),
            Partitioning = PartitioningMode.SelfPartitioned,
            Debug = DebugLevel.Off,
        });

    [Fact]
    public void DeviceHost_drives_a_static_topology_island_with_no_stale_reads()
    {
        // The SUPPORTED case (DeviceHost's documented scope): no topology edits under a
        // live host ⇒ handles never reissue ⇒ the device is driven cleanly forever.
        var net = ReleaseNet();
        NodeId a, g;
        using (var e = net.Edit())
        {
            a = e.AddNode(K(1)); g = e.AddReferenceNode(K(2));
            e.AddVoltageSource(a, g, 24.0, K(10));
        }
        net.SolveOperatingPoint();
        var host = new DeviceHost(net);
        host.Add(new AdaptedLoad(50.0, brownoutLowVolts: 1.0, brownoutHighVolts: 2.0),
            stackalloc NodeId[2] { a, g }, K(20), StateKey.From(K(20)));
        net.SolveOperatingPoint();

        long tick = 0;
        for (var i = 0; i < 20; i++)
        {
            Step(net, host, 0.05, ref tick);
            Assert.Equal(0, net.LastTickStats.StaleHandleReads);   // never stale on a static island
        }
    }

    [Fact]
    public void DeviceHost_does_not_re_pin_after_a_co_island_rebuild_the_caller_owns_that()
    {
        // The DOCUMENTED LIMITATION (api.md §18 re-pin contract is the CALLER's, not
        // DeviceHost's): a device shares an island with a sibling primitive; removing the
        // sibling rebuilds the island and reissues the device's cached handles; DeviceHost
        // keeps ticking through them. In release that is a COUNTED no-op (StaleHandleReads
        // > 0) — never a crash — which is exactly what §16's stale sentinel promises and
        // what a real integration's re-pin loop (walkthrough §22) exists to prevent.
        var net = ReleaseNet();
        NodeId a, g;
        ComponentRef sibling;
        using (var e = net.Edit())
        {
            a = e.AddNode(K(1)); g = e.AddReferenceNode(K(2));
            e.AddVoltageSource(a, g, 24.0, K(10));
            e.AddResistor(a, g, 100.0, K(11));   // sibling on the SAME island
        }
        net.SolveOperatingPoint();
        var host = new DeviceHost(net);
        host.Add(new AdaptedLoad(50.0), stackalloc NodeId[2] { a, g }, K(20), StateKey.From(K(20)));
        net.SolveOperatingPoint();

        // Drive cleanly first (static): no stale reads yet.
        long tick = 0;
        Step(net, host, 0.05, ref tick);
        Assert.Equal(0, net.LastTickStats.StaleHandleReads);

        // Remove the sibling ⇒ island rebuild at the next Solve, reissuing the device's
        // (cached) resistor + terminal-node handles.
        Assert.True(net.TryResolve(K(11), out sibling));
        using (var e = net.Edit())
            e.Remove(new ResistorId(sibling.Slot, sibling.Gen, sibling.Net));
        net.Solve(new TickClock(++tick, 0.05));
        Assert.Equal(1, net.LastTickStats.IslandRebuilds);

        // Now the host ticks through STALE handles. It does not throw (release sentinel);
        // the stale reads/adjusts are counted — the device has silently stopped being
        // driven, which the caller (not DeviceHost) is responsible for repairing.
        host.Tick(0.05);
        Assert.True(net.LastTickStats.StaleHandleReads > 0,
            "a co-island rebuild leaves the device's handles stale; DeviceHost does not re-pin");
    }
}
