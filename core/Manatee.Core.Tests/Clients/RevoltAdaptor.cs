using System;
using System.Buffers.Binary;
using Manatee.Core;
using Manatee.Core.Reduction;
using Manatee.Core.State;

namespace Manatee.Core.Tests.Clients;

/// <summary>
/// The "legacy-device adaptor" the Re-Volt client wraps around a raw game device
/// (stationeers.md; api.md §22.a). It presents a constant-power load to the linear MNA
/// solve as a conductance G = P/V_prev² clamped to a legal range, and — unlike the
/// built-in <see cref="Manatee.Core.Devices.AdaptedLoad"/>, whose terminal/component
/// handles are private — it re-pins itself across an island rebuild by KEY
/// re-resolution (api.md §16), so it can live on a network that is cut and re-breakered
/// without ever reading through a stale handle. It rides the island snapshot as one
/// <see cref="IDeviceStateUnit"/> keyed on its <see cref="StateKey"/>.
/// </summary>
internal sealed class RevoltAdaptor : IDeviceStateUnit
{
    private const double GMin = 1e-6, GMax = 1e3, VFloor = 1e-3;

    public PartitionKey Partition { get; }
    public JunctionKey Port { get; }
    public ExternalKey ResistorKey { get; }   // the load conductance-resistor's key
    public StateKey StateKey { get; }
    public double Watts { get; set; }

    // Live, re-pinnable handles.
    private ResistorId _r;
    private NodeId _pos, _neg;
    private bool _retired;

    // Serializable runtime state.
    private long _ticks;
    private double _appliedG = GMin;

    public bool Retired => _retired;
    public ResistorId Resistor => _r;

    public RevoltAdaptor(PartitionKey partition, in JunctionKey port, in ExternalKey resistorKey, double watts)
    {
        Partition = partition; Port = port; ResistorKey = resistorKey; Watts = watts;
        StateKey = StateKey.From(resistorKey);
    }

    /// <summary>Tier-3 build: resolve the terminals through the reduction layer and stamp
    /// the load resistor, then register the state unit anchored on the live port node.</summary>
    public void Build(Core.Netlist net, ConductorGraph g)
    {
        _pos = g.PortNode(Port);
        _neg = g.ReferenceNode(Partition);
        using (var e = net.Edit())
            _r = e.AddResistor(_pos, _neg, 1.0 / GMin, ResistorKey);
        net.RegisterDeviceState(_pos, this);
    }

    /// <summary>Churn-free re-pin (api.md §16): re-resolve the resistor + terminals by key
    /// after an island rebuild reissued their generations — NO structural edit, so it
    /// cannot itself trigger the rebuild it is recovering from. Re-anchors the state unit.</summary>
    public void Repin(Core.Netlist net, ConductorGraph g)
    {
        if (_retired) return;
        if (net.TryResolve(ResistorKey, out var c)) _r = new ResistorId(c.Slot, c.Gen, c.Net);
        _pos = g.PortNode(Port);
        _neg = g.ReferenceNode(Partition);
        net.RegisterDeviceState(_pos, this);   // re-registration replaces the stale anchor (api.md §14)
    }

    /// <summary>Tier-3 retire (device removed from the world): drop the resistor by key
    /// and unregister the state unit.</summary>
    public void Retire(Core.Netlist net)
    {
        if (_retired) return;
        _retired = true;
        using (var e = net.Edit())
            if (net.TryResolve(ResistorKey, out var c)) e.Remove(new ResistorId(c.Slot, c.Gen, c.Net));
        net.UnregisterDeviceState(StateKey);
    }

    /// <summary>Tier-≤2 per-tick update: G = P/V_prev² clamped. ε-no-op once converged
    /// (the built-in Adjust degrades a sub-threshold change to tier 0), so a settled load
    /// costs no refactorization.</summary>
    public void Tick(in Manatee.Core.Devices.DeviceTickContext ctx)
    {
        if (_retired) return;
        _ticks++;
        var v = ctx.Previous.Voltage(_pos) - ctx.Previous.Voltage(_neg);
        var absV = v < 0.0 ? -v : v;
        var vr = absV < VFloor ? VFloor : absV;
        var g = Watts / (vr * vr);
        if (g < GMin) g = GMin; else if (g > GMax) g = GMax;
        ctx.Adjust(_r, 1.0 / g);
        _appliedG = g;
    }

    /// <summary>Power the adaptor drew last solve — the ApplyState readback.</summary>
    public double Power(in Solution sol) => _retired ? 0.0 : sol.Power(_r);

    // ── IDeviceStateUnit (rides the island snapshot; restore is additive by StateKey) ──
    StateKey IDeviceStateUnit.Key => StateKey;
    public int BlobSize => 16;

    public void Save(Span<byte> dst)
    {
        BinaryPrimitives.WriteInt64LittleEndian(dst, _ticks);
        BinaryPrimitives.WriteInt64LittleEndian(dst.Slice(8), BitConverter.DoubleToInt64Bits(_appliedG));
    }

    public void Restore(ReadOnlySpan<byte> src)
    {
        _ticks = BinaryPrimitives.ReadInt64LittleEndian(src);
        _appliedG = BitConverter.Int64BitsToDouble(BinaryPrimitives.ReadInt64LittleEndian(src.Slice(8)));
    }

    public long Ticks => _ticks;
    public double AppliedG => _appliedG;
}
