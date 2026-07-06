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

/// <summary>Restore coverage report (api.md §14). Restore is ADDITIVE by
/// <see cref="StateKey"/>: it overwrites exactly the units the blob names and
/// leaves every other unit untouched, so both drift directions work (merge:
/// offer each blob in turn; split: offer the same blob to every resulting
/// island). Coverage is checked IN AGGREGATE — <c>sum(Matched)</c> across all
/// blobs offered, against <see cref="IslandHandle.StateUnitCount"/> — never per
/// call.</summary>
public readonly struct RestoreResult
{
    private readonly Netlist? _net;
    private readonly long _token;

    internal RestoreResult(Netlist net, long token, int matched, int untouched, int orphansInBlob)
    {
        _net = net; _token = token;
        Matched = matched; Untouched = untouched; OrphansInBlob = orphansInBlob;
    }

    /// <summary>State units this blob restored (matched a live unit in the island).</summary>
    public int Matched { get; }

    /// <summary>Live units in the island this blob carried no entry for — left
    /// as-is, never reset.</summary>
    public int Untouched { get; }

    /// <summary>Blob entries with no live unit here (world edited between save and
    /// load; expected in the split pattern, not an error).</summary>
    public int OrphansInBlob { get; }

    /// <summary>True iff every blob entry found a home in this island.</summary>
    public bool Ok => OrphansInBlob == 0;

    /// <summary>0B; copies the unmatched blob entries' <see cref="StateKey"/>s
    /// into <paramref name="into"/> (up to its length), returning the count
    /// written. Valid only until the next <see cref="IslandHandle.Restore"/> on
    /// this netlist (the scratch is single-slot; a later Restore returns 0 here).</summary>
    public int DrainOrphans(Span<StateKey> into)
        => _net is null ? 0 : _net.DrainOrphans(_token, into);
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

    /// <summary>0B; drains post-solve limit events and reports how many the fixed-cap
    /// ring dropped since the last full drain (api.md §12 — overflow counted, R9
    /// degrades legibly). <paramref name="dropped"/> &gt; 0 ⇒ events were lost and the
    /// count is cleared once the ring is emptied (call again until 0).</summary>
    public int DrainLimitEvents(Span<LimitEvent> into, out long dropped)
        => _net.DrainLimitEvents(_slot, _gen, into, out dropped);

    // ── Snapshot/restore (api.md §14) ──

    /// <summary>Serialized snapshot size in bytes — stable between
    /// topology-changing ops (re-read after any <see cref="IslandChange"/>).</summary>
    public int SnapshotSize => _net.SnapshotSize(_slot, _gen);

    /// <summary>Live state units in this island — the aggregate restore-coverage
    /// denominator (api.md §14): <c>sum(Matched)</c> vs this, after ALL blobs are
    /// offered, is how many units truly cold-started.</summary>
    public int StateUnitCount => _net.StateUnitCount(_slot, _gen);

    /// <summary>Versioned binary snapshot of this island's dynamic state (0B
    /// core-side; the destination buffer is caller-owned).</summary>
    public void Snapshot(System.Buffers.IBufferWriter<byte> into) => _net.Snapshot(_slot, _gen, into);

    /// <summary>Restore by <see cref="StateKey"/> — additive; misses cold-start
    /// and are reported (<see cref="RestoreResult.OrphansInBlob"/>), never thrown.</summary>
    public RestoreResult Restore(ReadOnlySpan<byte> b) => _net.Restore(_slot, _gen, b);

    /// <summary>0B; invariants as API — one path for CI, resync, and the tablet's
    /// educational messages (api.md §11): KCL residual (+ worst node), finiteness,
    /// and the energy residual (source − dissipated − Δstored − boundary − heat).</summary>
    public InvariantReport CheckInvariants(InvariantChecks which)
        => _net.CheckInvariants(_slot, _gen, which);

    /// <summary>Running source/dissipated/Δstored/boundary/heat energy ledger
    /// (api.md §11), integrated per substep on the hot path into per-island
    /// doubles (0B); the stored/boundary/heat terms are reconstructed on read.</summary>
    public EnergyAudit Energy => _net.EnergyAuditOf(_slot, _gen);

    /// <summary>Boundary-coupler exchange instrumentation (api.md §7): last-substep
    /// amplitudes/phases and signed A→B power. For a Closed galvanic breaker,
    /// <see cref="ExchangeView.PowerA2B"/> is the signed through-flow from the 1 mΩ
    /// bridge branch. Coupler-scoped (not island-scoped) — the same value for any
    /// handle in the netlist.</summary>
    public ExchangeView Exchange(CouplerId c) => _net.ExchangeViewOf(c);

    /// <summary>Boundary-coupler running energy ledger (api.md §7): InJ/OutJ/
    /// ModeledLossJ integrated per substep. The LOAD-BEARING no-free-energy invariant is
    /// SurplusJ = InJ−OutJ−ModeledLossJ ≥ 0 (genuine now that ModeledLossJ is computed
    /// independently from the efficiency curve, not synthesized as In−Out); HeatDumpedJ =
    /// ModeledLossJ + SurplusJ is the dumped heat (never stored work), ≥ 0 by construction.
    /// Residual is the ledger's CLOSURE identity (InJ = OutJ + HeatDumpedJ), ≈ 0 by
    /// construction — it audits the readback arithmetic, not that the physical exchange
    /// conserved (that bound is the OutJ clamp).</summary>
    public EnergyLedger Ledger(CouplerId c) => _net.LedgerOf(c);
}
