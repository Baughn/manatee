using System.Collections.Generic;

namespace Manatee.Core;

/// <summary>
/// Receipt for one atomic <see cref="StructuralEdit.Commit"/> (api.md §6). Plain
/// readonly struct — no span fields (Decision log #8); variable-length payloads
/// are drained via the journal window [<see cref="JournalFrom"/>..<see cref="JournalTo"/>]
/// or <c>IslandTable.DrainChanges</c>.
/// </summary>
public readonly struct EditReceipt
{
    internal EditReceipt(long journalFrom, long journalTo, int nodesAdded, int componentsAdded,
                         int componentsRemoved, int islandsTouched, int estimatedRebuildDim,
                         int estimatedRebuildRows, bool windowLapped)
    {
        JournalFrom = journalFrom; JournalTo = journalTo;
        NodesAdded = nodesAdded; ComponentsAdded = componentsAdded; ComponentsRemoved = componentsRemoved;
        IslandsTouched = islandsTouched; EstimatedRebuildDim = estimatedRebuildDim;
        EstimatedRebuildRows = estimatedRebuildRows; WindowLapped = windowLapped;
    }

    /// <summary>Replay [From..To) to sync a derived layer.</summary>
    public long JournalFrom { get; }
    public long JournalTo { get; }
    public int NodesAdded { get; }
    public int ComponentsAdded { get; }
    public int ComponentsRemoved { get; }
    public int IslandsTouched { get; }

    /// <summary>Scheduler hint: does the coming rebuild fit the tick budget?</summary>
    public int EstimatedRebuildDim { get; }
    public int EstimatedRebuildRows { get; }

    /// <summary>This single commit emitted more events than the journal capacity:
    /// [From..To] is unreplayable and the reducer must Resync — declared up front,
    /// never discovered as silence (api.md §6, §15).</summary>
    public bool WindowLapped { get; }
}

/// <summary>
/// The only door to topology change (api.md §6). Pooled sealed class (a copyable
/// batch is a footgun; pool churn is shape-time only). Staging is deferred:
/// added handles are valid immediately (their slots are reserved), but islanding,
/// key registration, wiring-policy stamping, and journalling all happen
/// atomically at <see cref="Commit"/> — all-or-nothing, including RemoveNode
/// degree-0 enforcement and ClientPartitioned <see cref="PartitionMergeException"/>
/// rollback. Nesting throws.
/// </summary>
public sealed class StructuralEdit : IDisposable
{
    private readonly Netlist _net;
    private bool _open;

    // Staged slots (payload already written to the netlist SoA at Add time so the
    // returned handles are usable within the batch; semantic apply is deferred).
    internal readonly List<int> AddedNodes = new();
    internal readonly List<int> AddedComponents = new();
    internal readonly List<int> AddedCouplers = new();
    internal readonly List<int> AddedProbes = new();
    internal readonly List<int> RemovedComponents = new();
    internal readonly List<int> RemovedNodes = new();
    internal readonly List<int> RemovedCouplers = new();
    internal readonly List<int> RemovedProbes = new();

    internal StructuralEdit(Netlist net) => _net = net;

    internal void Reopen()
    {
        _open = true;
        AddedNodes.Clear(); AddedComponents.Clear(); AddedCouplers.Clear(); AddedProbes.Clear();
        RemovedComponents.Clear(); RemovedNodes.Clear(); RemovedCouplers.Clear(); RemovedProbes.Clear();
    }

    private void EnsureOpen()
    {
        if (!_open) throw new InvalidOperationException("StructuralEdit already committed/aborted.");
    }

    // ── Nodes & reference ──

    public NodeId AddNode(in ExternalKey key, NodeRole role = NodeRole.Internal, PartitionKey partition = default)
    {
        EnsureOpen();
        return _net.StageAddNode(this, key, role, partition);
    }

    public NodeId AddReferenceNode(in ExternalKey key)
    {
        EnsureOpen();
        return _net.StageAddNode(this, key, NodeRole.Reference, default);
    }

    public void MarkReference(NodeId n)
    {
        EnsureOpen();
        _net.StageMarkReference(n);
    }

    public void SetPartition(NodeId n, PartitionKey p)
    {
        EnsureOpen();
        _net.StageSetPartition(n, p);
    }

    // ── Primitives ──

    public ResistorId AddResistor(NodeId a, NodeId b, double ohms, in ExternalKey key, in LimitSpec limits = default)
    {
        EnsureOpen();
        var slot = _net.StageAddTwoTerminal(this, ComponentKind.Resistor, a, b, ohms, key, StateKey.From(key), limits);
        return new ResistorId(slot, _net.CompGen(slot), _net.NetId);
    }

    public VSourceId AddVoltageSource(NodeId pos, NodeId neg, double volts, in ExternalKey key)
    {
        EnsureOpen();
        var slot = _net.StageAddTwoTerminal(this, ComponentKind.VSource, pos, neg, volts, key, StateKey.From(key), default);
        return new VSourceId(slot, _net.CompGen(slot), _net.NetId);
    }

    public ISourceId AddCurrentSource(NodeId from, NodeId to, double amps, in ExternalKey key)
    {
        EnsureOpen();
        var slot = _net.StageAddTwoTerminal(this, ComponentKind.ISource, from, to, amps, key, StateKey.From(key), default);
        return new ISourceId(slot, _net.CompGen(slot), _net.NetId);
    }

    public CapacitorId AddCapacitor(NodeId a, NodeId b, double farads, in ExternalKey key, in StateKey state)
    {
        EnsureOpen();
        var slot = _net.StageAddTwoTerminal(this, ComponentKind.Capacitor, a, b, farads, key, state, default);
        return new CapacitorId(slot, _net.CompGen(slot), _net.NetId);
    }

    public InductorId AddInductor(NodeId a, NodeId b, double henries, in ExternalKey key, in StateKey state)
    {
        EnsureOpen();
        var slot = _net.StageAddTwoTerminal(this, ComponentKind.Inductor, a, b, henries, key, state, default);
        return new InductorId(slot, _net.CompGen(slot), _net.NetId);
    }

    public SwitchId AddSwitch(NodeId a, NodeId b, bool closed, in ExternalKey key)
    {
        EnsureOpen();
        var slot = _net.StageAddTwoTerminal(this, ComponentKind.Switch, a, b, closed ? 1.0 : 0.0, key, StateKey.From(key), default);
        return new SwitchId(slot, _net.CompGen(slot), _net.NetId);
    }

    public DiodeId AddDiode(NodeId anode, NodeId cathode, in DiodeParams p, in ExternalKey key)
    {
        EnsureOpen();
        var slot = _net.StageAddDiode(this, anode, cathode, p, key);
        return new DiodeId(slot, _net.CompGen(slot), _net.NetId);
    }

    /// <summary>Stateful (phase); marks the island AC ⇒ subcycles (stepping is phase 4).</summary>
    public VSourceId AddSineSource(NodeId pos, NodeId neg, in SineDrive d, in ExternalKey key, in StateKey state)
    {
        EnsureOpen();
        var slot = _net.StageAddSine(this, pos, neg, d, key, state);
        return new VSourceId(slot, _net.CompGen(slot), _net.NetId);
    }

    /// <summary>Same-matrix coupled two-port (api.md §6). The turns-ratio
    /// constraint stamps two auxiliary rows at solve time (stage 2).</summary>
    public TransformerId AddIdealTransformer(NodeId aPos, NodeId aNeg, NodeId bPos, NodeId bNeg,
                                             double turnsRatio, in ExternalKey key)
    {
        EnsureOpen();
        var slot = _net.StageAddTransformer(this, aPos, aNeg, bPos, bNeg, turnsRatio, key);
        return new TransformerId(slot, _net.CompGen(slot), _net.NetId);
    }

    public CouplerId AddCoupler(in CouplerSpec spec, in CouplerPorts ports, in ExternalKey key, in StateKey state)
    {
        EnsureOpen();
        return _net.StageAddCoupler(this, spec, ports, key, state);
    }

    public void Remove<TId>(TId id) where TId : struct, IComponentId
    {
        EnsureOpen();
        _net.StageRemoveComponent(this, id.AsRef());
    }

    public void RemoveNode(NodeId n)
    {
        EnsureOpen();
        _net.StageRemoveNode(this, n);
    }

    public void RemoveCoupler(CouplerId id)
    {
        EnsureOpen();
        _net.StageRemoveCoupler(this, id);
    }

    // ── Probes ──

    public ProbeId AddProbe(NodeId n, in ExternalKey key)
    {
        EnsureOpen();
        return _net.StageAddProbe(this, n, n, 0.0, key);
    }

    public ProbeId AddInterpolatedProbe(NodeId a, NodeId b, double t, in ExternalKey key)
    {
        EnsureOpen();
        return _net.StageAddProbe(this, a, b, t, key);
    }

    // ── Lifecycle ──

    public EditReceipt Commit()
    {
        EnsureOpen();
        _open = false;
        return _net.CommitEdit(this);
    }

    public void Abort()
    {
        if (!_open) return;
        _open = false;
        _net.AbortEdit(this);
    }

    public void Dispose()
    {
        if (!_open) return;
        // If a staging call threw inside the using body (a debug stale-handle resolve),
        // the block is unwinding on an exception — Abort the partial batch rather than
        // Commit it: "a half-applied edit cannot be observed" (api.md §6). Normal exit
        // leaves the fault flag clear and commits.
        if (_net.TakeEditFaulted()) { Abort(); return; }
        Commit();
    }
}
