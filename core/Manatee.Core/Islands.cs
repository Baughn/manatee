using System.Diagnostics;

namespace Manatee.Core;

/// <summary>Island lifecycle (api.md §11). Client-visible invariant:
/// Empty→Building/Dirty on commit; Ready↔Dirty on touch/Solve; Dirty→Faulted on
/// a failed solve; Faulted→Dirty on any tier-2/3 change.</summary>
public enum IslandStatus : byte { Empty, Building, Ready, Dirty, Faulted }

/// <summary>Membership change reported through <see cref="IslandTable.DrainChanges"/>
/// (api.md §11). The game client's re-pin trigger (never the journal).</summary>
public enum IslandChangeKind : byte
{
    Created,
    Merged,    // A=survivor, B=absorbed
    Rebuilt,   // A=old, B=new
    Removed,
    Faulted,
    Recovered,
}

/// <summary>One membership delta (api.md §11).</summary>
public struct IslandChange
{
    public IslandChangeKind Kind;
    public IslandId A, B;
}

/// <summary>Fault classification when a solve fails (api.md §11, §20).</summary>
public enum FaultKind : byte { Singular, ContradictorySources, NewtonDiverged, NonFinite }

/// <summary>Scalar fault summary; details drain via
/// <see cref="IslandHandle.DescribeFault"/> (api.md §11).</summary>
public readonly record struct FaultDiagnostic(
    FaultKind Kind, ComponentRef Worst, NodeId WorstNode, int ComponentCount, int NodeCount);

/// <summary>Which invariants to check (api.md §11). This stage supports
/// <see cref="Kcl"/> and <see cref="Finiteness"/>; <see cref="Energy"/> is
/// zeroed until the solve pipeline lands.</summary>
[Flags]
public enum InvariantChecks : byte { Kcl = 1, Finiteness = 2, Energy = 4, All = 0xFF }

/// <summary>Result of <see cref="IslandHandle.CheckInvariants"/> (api.md §11).</summary>
public readonly record struct InvariantReport(
    double MaxKclResidual, NodeId WorstKclNode, bool AllFinite, int FirstNonFiniteRow, double EnergyResidual);

/// <summary>Running source/dissipated/Δstored ledger (api.md §11). Zeroed until
/// the solve pipeline lands.</summary>
public readonly record struct EnergyAudit(
    double SourceJ, double DissipatedJ, double StoredJ, double BoundaryNetJ, double HeatDumpedJ, double ResidualJ);

/// <summary>Read-only AC subcycle plan (api.md §11). Solver owns N; this stage
/// reports a single DC step.</summary>
public readonly record struct SubstepPlan(int Substeps, double SubstepDt, double HysteresisBand);

/// <summary>Restore coverage report (api.md §14). Snapshot/restore is phase 6;
/// declared here for the island surface.</summary>
public readonly record struct RestoreResult(int Matched, int Untouched, int OrphansInBlob)
{
    public bool Ok => OrphansInBlob == 0;
}

/// <summary>
/// Per-island scheduling view for clients that own threads (api.md §11). A thin
/// value over the netlist's island tables — 0B to obtain and copy.
/// </summary>
public readonly struct IslandTable
{
    private readonly Netlist _net;

    internal IslandTable(Netlist net) => _net = net;

    /// <summary>Number of live islands.</summary>
    public int Count => _net.IslandCount;

    /// <summary>Live island by dense index (0..Count). O(Count) per access (it
    /// scans alive slots) — a <c>for (i=0; i&lt;Count; i++) this[i]</c> loop is
    /// O(Count²). For bulk iteration use <see cref="Ids"/> (the dense, rebuild-stable
    /// order) and <see cref="Of(IslandId)"/>.</summary>
    public IslandHandle this[int i] => _net.IslandHandleByIndex(i);

    /// <summary>The island containing a node (gen-checked).</summary>
    public IslandHandle Of(NodeId n) => _net.IslandHandleOfNode(n);

    /// <summary>The island for an id (gen-checked).</summary>
    public IslandHandle Of(IslandId id) => _net.IslandHandleOfId(id);

    /// <summary>THE deterministic iteration order: ascending by each island's
    /// minimum node <see cref="ExternalKey"/> — rebuild-stable (api.md §11).
    /// Valid until the next structural commit / Reconfigure.</summary>
    public ReadOnlySpan<IslandId> Ids => _net.IslandIdsOrdered();

    /// <summary>0B worklist of the dirty scheduling units (api.md §11).</summary>
    public int CollectDirty(Span<IslandHandle> into) => _net.CollectDirty(into);

    /// <summary>0B; drains membership changes since the last drain. <paramref name="lost"/>
    /// true ⇒ the ring overflowed and the client is OBLIGATED to a full re-pin
    /// (api.md §11, §20).</summary>
    public int DrainChanges(Span<IslandChange> into, out bool lost) => _net.DrainChanges(into, out lost);
}

/// <summary>
/// One island's scheduling + readback surface (api.md §11). Solve/Step drive the
/// island's numeric work — that pipeline is stage 2; this stage exposes the
/// structural status machine and the invariant/snapshot surface, delegating
/// numeric members to the (deferred) island runtime seam.
/// </summary>
public readonly struct IslandHandle
{
    private readonly Netlist _net;
    private readonly int _slot;
    private readonly uint _gen;

    internal IslandHandle(Netlist net, int slot, uint gen)
    {
        _net = net; _slot = slot; _gen = gen;
    }

    /// <summary>Whether this handle names a live island of its netlist.</summary>
    public bool IsValid => _net is not null && _net.IslandGenLive(_slot, _gen);

    public IslandId Id => new(_slot, _gen, _net.NetId);

    public IslandStatus Status => _net.IslandStatusOf(_slot, _gen);

    /// <summary>ClientPartitioned only; an island spanning >1 partition through a
    /// Closed coupler returns <see cref="PartitionKey.None"/> (api.md §11).</summary>
    public PartitionKey Partition => _net.IslandPartitionOf(_slot, _gen);

    /// <summary>Solve THIS island's scheduling unit. Single-writer-per-island,
    /// debug-asserted. The numeric solve is stage 2; this stage advances the
    /// structural status machine through the island runtime seam.</summary>
    public void Step(in TickClock clock)
    {
        _net.StepIsland(_slot, _gen, clock);
    }

    /// <summary>AC subcycle plan (solver-owned N): substep count, substep dt, and the
    /// hysteresis band (api.md §11). DC / non-AC islands report a single step.</summary>
    public SubstepPlan Plan => _net.IslandPlan(_slot, _gen);

    /// <summary>Scalar fault summary.</summary>
    public FaultDiagnostic Fault => _net.IslandFault(_slot, _gen);

    /// <summary>0B; packs fault component/node detail into caller spans.</summary>
    public int DescribeFault(Span<ComponentRef> comps, Span<NodeId> nodes)
        => _net.DescribeFault(_slot, _gen, comps, nodes);

    /// <summary>0B; drains this island's post-solve limit events.</summary>
    public int DrainLimitEvents(Span<LimitEvent> into) => _net.DrainLimitEvents(_slot, _gen, into);

    // ── Snapshot/restore — phase 6 ──

    /// <summary>Stable between topology-changing ops (api.md §11, §14). Phase 6.</summary>
    public int SnapshotSize => throw NotYet();

    /// <summary>Live state units in this island — the restore-coverage
    /// denominator (api.md §14). Phase 6.</summary>
    public int StateUnitCount => throw NotYet();

    public void Snapshot(System.Buffers.IBufferWriter<byte> into) => throw NotYet();

    public RestoreResult Restore(ReadOnlySpan<byte> b) => throw NotYet();

    /// <summary>0B; invariants as API — one path for CI, resync, and the tablet's
    /// educational messages (api.md §11). Kcl/Finiteness this stage; the residual
    /// values require a solved vector (stage 2), so they report zero/true here.</summary>
    public InvariantReport CheckInvariants(InvariantChecks which)
        => _net.CheckInvariants(_slot, _gen, which);

    /// <summary>Running energy ledger. Zeroed until the solve pipeline lands.</summary>
    public EnergyAudit Energy => default;

    /// <summary>Boundary-coupler exchange instrumentation (phase 5).</summary>
    public ExchangeView Exchange(CouplerId c) => default;

    /// <summary>Boundary-coupler energy ledger (phase 5).</summary>
    public EnergyLedger Ledger(CouplerId c) => default;

    private static NotSupportedException NotYet()
        => new("Snapshot/restore is phase 6 (api.md §14); not implemented in the document stage.");
}
