namespace Manatee.Core;

// Generational typed handles: (Slot, Gen, Net). Cross-kind misuse fails to
// COMPILE (each id is a distinct type). Stale / wrapped / cross-netlist use
// fails FAST — StaleHandleException in debug, a defined sentinel + a TickStats
// counter in release (api.md §3, §16, §20). Gen is 32-bit (a slot reused every
// 50 ms tick wraps in ~6.8 years); Net is a process-wide netlist id so a handle
// from another Netlist is caught by value, never by accident of slot reuse.
//
// Gen 0 is reserved: default(TId) is therefore never a live handle (live slots
// start at gen 1), so an unset handle is caught by the same generation compare.

/// <summary>Node handle. Tier: identity; 0B to pass.</summary>
public readonly record struct NodeId(int Slot, uint Gen, ushort Net);

/// <summary>Resistor handle (api.md §3).</summary>
public readonly record struct ResistorId(int Slot, uint Gen, ushort Net) : IComponentId
{
    /// <summary>0B; constrained-callvirt readback path (api.md §3 binding note).</summary>
    public ComponentRef AsRef() => new(ComponentKind.Resistor, Slot, Gen, Net);
}

/// <summary>Independent voltage-source handle.</summary>
public readonly record struct VSourceId(int Slot, uint Gen, ushort Net) : IComponentId
{
    public ComponentRef AsRef() => new(ComponentKind.VSource, Slot, Gen, Net);
}

/// <summary>Independent current-source handle.</summary>
public readonly record struct ISourceId(int Slot, uint Gen, ushort Net) : IComponentId
{
    public ComponentRef AsRef() => new(ComponentKind.ISource, Slot, Gen, Net);
}

/// <summary>Capacitor handle (stateful primitive).</summary>
public readonly record struct CapacitorId(int Slot, uint Gen, ushort Net) : IComponentId
{
    public ComponentRef AsRef() => new(ComponentKind.Capacitor, Slot, Gen, Net);
}

/// <summary>Inductor handle (stateful primitive).</summary>
public readonly record struct InductorId(int Slot, uint Gen, ushort Net) : IComponentId
{
    public ComponentRef AsRef() => new(ComponentKind.Inductor, Slot, Gen, Net);
}

/// <summary>Relay-contact switch handle (in-matrix conductance).</summary>
public readonly record struct SwitchId(int Slot, uint Gen, ushort Net) : IComponentId
{
    public ComponentRef AsRef() => new(ComponentKind.Switch, Slot, Gen, Net);
}

/// <summary>Diode handle (nonlinear companion).</summary>
public readonly record struct DiodeId(int Slot, uint Gen, ushort Net) : IComponentId
{
    public ComponentRef AsRef() => new(ComponentKind.Diode, Slot, Gen, Net);
}

/// <summary>Ideal transformer two-port handle (the one multi-terminal primitive, §6).</summary>
public readonly record struct TransformerId(int Slot, uint Gen, ushort Net) : IComponentId
{
    public ComponentRef AsRef() => new(ComponentKind.IdealTransformer, Slot, Gen, Net);
}

/// <summary>Coupler handle (breaker / decoupling transformer / converter).
/// DOCUMENT-STABLE across rebuilds and merges (api.md §16): only
/// <see cref="StructuralEdit.RemoveCoupler"/> invalidates it.</summary>
public readonly record struct CouplerId(int Slot, uint Gen, ushort Net);

/// <summary>Island handle id. Volatile: absorbed by a merge, replaced by a
/// rebuild; the rebuild-stable ordering key is <c>ExternalKey</c> (§16).</summary>
public readonly record struct IslandId(int Slot, uint Gen, ushort Net);

/// <summary>Probe handle. DOCUMENT-STABLE across rebuilds and merges (§16); only
/// removal (or a drift resync of reduction-owned probes) invalidates it.</summary>
public readonly record struct ProbeId(int Slot, uint Gen, ushort Net);

/// <summary>The eight solver primitive kinds. Couplers and probes are
/// document-level registrations, not <see cref="ComponentKind"/>s.</summary>
public enum ComponentKind : byte
{
    Resistor, VSource, ISource, Capacitor, Inductor, Switch, Diode, IdealTransformer,
}

/// <summary>Kind-tagged component reference — the generic readback/removal
/// currency (<see cref="IComponentId.AsRef"/>). 0B; blittable.</summary>
public readonly record struct ComponentRef(ComponentKind Kind, int Slot, uint Gen, ushort Net);

/// <summary>Value-type interface for generic readback/removal. Implementations
/// return their <see cref="ComponentRef"/> by direct interface call on the value
/// type (constrained-callvirt) — never boxing (api.md §3 binding note), which is
/// what keeps tier-1 generic readback 0B on CoreCLR and Mono.</summary>
public interface IComponentId
{
    ComponentRef AsRef();
}

/// <summary>
/// Client-stable TOPOLOGICAL identity — mandatory on every Add. Drives
/// <c>TryResolve</c>, drift diffs, canonical/normalized serialization, and
/// <c>IslandTable.Ids</c> ordering. The ONLY identity that survives an island
/// rebuild (api.md §3, §16). 128-bit so clients pack dimension/position/material
/// (VS), a RefId (Stationeers), or an element id (tablet) without collision
/// anxiety; per-netlist uniqueness is debug-asserted at Add.
/// </summary>
public readonly record struct ExternalKey(ulong Hi, ulong Lo)
{
    /// <summary>Stationeers convenience: a bare RefId in the low word.</summary>
    public ExternalKey(ulong refId) : this(0, refId) { }

    /// <summary>Deterministic sub-key for a device's component roles (api.md §18
    /// key-allocation contract). Same <paramref name="ordinal"/> always derives
    /// the same key, so restore-by-key and drift diffs work for multi-primitive
    /// devices across rebuilds.</summary>
    public ExternalKey Derive(ushort ordinal)
        => new(Hi, Lo + 0x9E3779B97F4A7C15UL * (ordinal + 1UL));
}

/// <summary>
/// Client-stable identity of a unit of SERIALIZABLE DYNAMIC STATE — a whole
/// device, or a bare cap/inductor/sine source. Mandatory where state exists.
/// Snapshot blobs key on this and nothing else (api.md §3, §14). Distinct from
/// <see cref="ExternalKey"/> because one device owns many components but one
/// state blob.
/// </summary>
public readonly record struct StateKey(ulong Hi, ulong Lo)
{
    public StateKey(ulong refId) : this(0, refId) { }

    /// <summary>A bare stateful primitive reuses its topological key's bits.</summary>
    public static StateKey From(in ExternalKey k) => new(k.Hi, k.Lo);
}

/// <summary>
/// ClientPartitioned network id (Stationeers CableNetwork). The default value
/// <see cref="None"/> is a reserved sentinel: SelfPartitioned nodes and
/// coupler-spanning islands report it (api.md §3, §11).
/// </summary>
public readonly record struct PartitionKey(ulong Value)
{
    /// <summary>The reserved sentinel — <c>default(PartitionKey)</c>.</summary>
    public static PartitionKey None => default;

    /// <summary>True for the reserved sentinel.</summary>
    public bool IsNone => Value == 0;
}
