using System;
using System.Collections.Generic;
using Manatee.Core.State;

namespace Manatee.Core.Devices;

/// <summary>
/// A device's terminal contract (api.md §18). <see cref="Count"/> is how many
/// <see cref="NodeId"/>s the host must supply to <see cref="DeviceHost.Add"/>;
/// <see cref="ReturnTerminals"/> names the indices the WiringPolicy treats as
/// returns (§5) — backed by a STATIC per-device-type array (cold), never a
/// per-instance allocation, so obtaining a <see cref="TerminalSpec"/> is 0 B.
/// </summary>
public readonly struct TerminalSpec
{
    private readonly int[]? _returns;

    /// <param name="count">Terminal arity (the span length <see cref="DeviceHost.Add"/> requires).</param>
    /// <param name="returnTerminals">A STATIC array of return-terminal indices, or null for none.</param>
    public TerminalSpec(int count, int[]? returnTerminals = null)
    {
        Count = count;
        _returns = returnTerminals;
    }

    /// <summary>Number of terminals the device binds.</summary>
    public int Count { get; }

    /// <summary>Indices the WiringPolicy treats as returns (api.md §5). Empty when none.</summary>
    public ReadOnlySpan<int> ReturnTerminals => _returns is null ? default : _returns;
}

/// <summary>
/// Behavioural device base (api.md §18). A device composes solver primitives at
/// tier-3 build time, updates their parameters at tiers 1–2 in <see cref="Tick"/>
/// (capped BY TYPE through <see cref="DeviceTickContext"/> — a structural change in
/// the hot loop does not compile), and serialises exactly its own dynamic state as
/// ONE unit under its <see cref="StateKey"/> (the phase-6 <see cref="IDeviceStateUnit"/>
/// seam).
///
/// <para>KEY-ALLOCATION CONTRACT (binding). A device mints every component key as
/// <c>baseKey.Derive(ordinal)</c>, where <c>ordinal</c> is a stable, documented
/// constant per component role of the concrete class (each model documents its
/// ordinals). Because <see cref="ExternalKey.Derive"/> is deterministic, the same
/// role always maps to the same key across rebuilds — which is what makes
/// restore-by-key and drift diffs work for multi-primitive devices.</para>
/// </summary>
public abstract class Device : IDeviceStateUnit
{
    private ExternalKey _baseKey;
    private StateKey _stateKey;

    /// <summary>The device's terminal contract (static; 0 B to read).</summary>
    public abstract TerminalSpec Terminals { get; }

    /// <summary>The device's client-assigned base key — component keys derive from it.</summary>
    protected ExternalKey BaseKey => _baseKey;

    /// <summary>Compose the device's primitives at tier-3 time. <paramref name="terminals"/>
    /// are the host-supplied external nodes (length == <see cref="Terminals"/>.Count);
    /// <paramref name="baseKey"/>/<paramref name="state"/> are the device's identities.
    /// Mint component keys as <c>baseKey.Derive(ordinal)</c> with a stable per-role ordinal.</summary>
    protected abstract void Build(StructuralEdit e, ReadOnlySpan<NodeId> terminals,
                                  in ExternalKey baseKey, in StateKey state);

    /// <summary>Tear-down (tier 3). Default removes nothing; models that mint
    /// internal nodes/components override to remove them.</summary>
    public virtual void RemoveComponents(StructuralEdit e) { }

    /// <summary>Per-tick parameter update, capped at tiers 1–2 BY TYPE. Default no-op.</summary>
    public virtual void Tick(in DeviceTickContext ctx) { }

    /// <summary>Serialized state size in bytes (0 ⇒ stateless; no unit registered).</summary>
    public virtual int StateSize => 0;

    /// <summary>Write exactly <see cref="StateSize"/> bytes of this device's state.</summary>
    public virtual void SaveState(Span<byte> dst) { }

    /// <summary>Read exactly <see cref="StateSize"/> bytes of state back.</summary>
    public virtual void RestoreState(ReadOnlySpan<byte> src) { }

    // Host entry point: records the identities, then builds. Internal so DeviceHost
    // can drive it without exposing Build publicly (api.md §18 keeps Build protected).
    internal void HostBuild(StructuralEdit e, ReadOnlySpan<NodeId> terminals,
                            in ExternalKey baseKey, in StateKey state)
    {
        _baseKey = baseKey;
        _stateKey = state;
        Build(e, terminals, baseKey, state);
    }

    // ── IDeviceStateUnit bridge (phase-6 StateUnit seam). A device that owns dynamic
    //    state registers ITSELF as the one unit under its StateKey; the core routes
    //    the blob by key and never interprets it. ──
    StateKey IDeviceStateUnit.Key => _stateKey;
    int IDeviceStateUnit.BlobSize => StateSize;
    void IDeviceStateUnit.Save(Span<byte> dst) => SaveState(dst);
    void IDeviceStateUnit.Restore(ReadOnlySpan<byte> src) => RestoreState(src);
}

/// <summary>
/// A serial device tick-DRIVER over a public <see cref="Netlist"/> (api.md §18):
/// builds devices, registers their state units, and drives their per-tick updates in
/// a deterministic, serial order. Holds NO internal netlist handle — it is a pure
/// consumer of the public Core API.
///
/// <para><b>Scope: does NOT implement the §18 adaptor re-pin contract.</b> A device
/// caches its component and TERMINAL node handles in <see cref="Device.Build"/>. After
/// any co-island topology edit (a sibling removal / coupler <c>Open</c> that rebuilds
/// the island) the survivors' handles are reissued at the next <see cref="Netlist.Solve"/>,
/// and this driver keeps ticking through the now-stale handles — a counted no-op
/// (<see cref="TickStats.StaleHandleReads"/>&gt;0, §16), never a crash, but the device
/// stops being driven. The §18 re-pin obligation is the INTEGRATING CALLER's: on an
/// <c>IslandChange</c> (from <c>IslandTable.DrainChanges</c>) it must re-Build affected
/// devices with FRESH terminals and <c>Restore</c> their state by <see cref="StateKey"/>.
/// This driver cannot do it alone — a device's terminal nodes are caller-owned and are
/// not resolvable from the device's own key. It is therefore complete only for
/// STATIC-topology device islands (what the built-in models and their tests exercise);
/// a dynamic-topology integration wires the re-pin loop itself (walkthrough §22).</para>
///
/// <para>Determinism: devices tick in <see cref="Add"/> order every tick, each
/// reading <see cref="DeviceTickContext.Previous"/> (last published solution) and
/// writing tier-1/2 parameter updates. The caller drives the numeric
/// <see cref="Netlist.Solve"/> after <see cref="Tick"/>.</para>
/// </summary>
public sealed class DeviceHost
{
    private readonly Netlist _net;
    private readonly List<Device> _devices = new();

    public DeviceHost(Netlist net) => _net = net ?? throw new ArgumentNullException(nameof(net));

    /// <summary>Number of installed devices.</summary>
    public int Count => _devices.Count;

    /// <summary>Build <paramref name="device"/> over its <paramref name="terminals"/>,
    /// register its state unit (if any) anchored on terminal 0, and enlist it for
    /// <see cref="Tick"/>. Opens and commits ONE structural edit. Returns the device
    /// for fluent capture of its handles.</summary>
    public T Add<T>(T device, ReadOnlySpan<NodeId> terminals, in ExternalKey baseKey, in StateKey state)
        where T : Device
    {
        if (device is null) throw new ArgumentNullException(nameof(device));
        var spec = device.Terminals;
        if (terminals.Length != spec.Count)
            throw new ArgumentException(
                $"Device expects {spec.Count} terminals, got {terminals.Length}.", nameof(terminals));

        using (var e = _net.Edit())
            device.HostBuild(e, terminals, baseKey, state);

        // A device that owns dynamic state joins the snapshot/restore stream keyed by
        // its StateKey, anchored on terminal 0 (its primary/live node) so the unit
        // follows a merge/split exactly like a built-in primitive (api.md §14, §18).
        if (device.StateSize > 0)
            _net.RegisterDeviceState(terminals[0], device);

        _devices.Add(device);
        return device;
    }

    /// <summary>Tick every device once, in <see cref="Add"/> order, with a tier-≤2
    /// capability object. Zero-alloc: the context is a ref struct and the loop is
    /// over a struct enumerator. Call once per game tick BEFORE
    /// <see cref="Netlist.Solve"/>.</summary>
    public void Tick(double dt)
    {
        var ctx = _net.TickContext(dt);
        for (var i = 0; i < _devices.Count; i++)
            _devices[i].Tick(ctx);
    }
}
