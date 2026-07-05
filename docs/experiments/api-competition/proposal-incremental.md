# manatee-core Netlist API — Proposal C (incremental topology / compaction lens)

Design stance: the reduction layer is the netlist's most demanding client, and it must be an
*ordinary* client — every capability it needs (interior probes, limit metadata, island rehoming,
drift detection) is a public, typed API that the tablet and Stationeers adaptor could also use.
Three mechanisms carry the whole design:

1. **Generational handles + client-stable `ExternalKey`s** — internal identity is allowed to be
   unstable (rebuilds, merges); external identity never is. Stale handles fail fast; diffing,
   snapshot/restore, and resync all match on keys, never slots.
2. **Transactional topology batches + a pollable journal** — topology changes are atomic
   commits that emit an ordered event log. Derived state (compaction tables) is maintained by
   *replaying the journal*, which makes "provably in sync" a replay property, not a prayer.
3. **Cost tiers as *types*, not doc-comments** — each tier is a separate facade; the tier of any
   call is visible at the call site and greppable in review.

---

## 1. C# API sketch

### 1.1 Identity

```csharp
// All handles are (slot, generation). Any use after invalidation throws
// StaleHandleException in debug, returns a defined error in release. This is the
// load-bearing safety net for incremental maintenance: a compaction bug surfaces
// as "stale ResistorId at chain rehome", not as silent wrong answers.
public readonly record struct NodeId(int Slot, ushort Gen);
public readonly record struct ResistorId(int Slot, ushort Gen);
public readonly record struct VSourceId(int Slot, ushort Gen);
public readonly record struct ISourceId(int Slot, ushort Gen);
public readonly record struct CapacitorId(int Slot, ushort Gen);
public readonly record struct InductorId(int Slot, ushort Gen);
public readonly record struct SwitchId(int Slot, ushort Gen);
public readonly record struct DiodeId(int Slot, ushort Gen);
public readonly record struct ProbeId(int Slot, ushort Gen);
public readonly record struct IslandId(int Slot, ushort Gen);

// Type-erased handle for tables, events, and heterogeneous storage.
// Every typed id converts implicitly to ComponentRef; downcast is checked.
public enum ComponentKind : byte { Resistor, VSource, ISource, Capacitor, Inductor, Switch, Diode }
public readonly record struct ComponentRef(ComponentKind Kind, int Slot, ushort Gen);

// Client-stable identity, REQUIRED on every Add. The client owns the meaning
// (VS: hash of region-representative voxel; Stationeers: RefId; tablet: element id).
// Uniqueness per netlist is asserted in debug. This is what makes rebuilds,
// restores, and drift diffs canonical: internal ids never appear in any
// serialized or compared form.
public readonly record struct ExternalKey(ulong Hi, ulong Lo);
```

### 1.2 The netlist and its tier facades

```csharp
public sealed class Netlist {
    public Netlist(NetlistOptions opts);              // arena sizing hints, partitioning mode, debug level

    // ── Tier facades (see §2). Structs wrapping the netlist; zero-cost. ──
    public DriveFacade Drive { get; }                 // Tier 1: RHS-only writes
    public TuneFacade  Tune  { get; }                 // Tier 2: conductance writes
    public MetaFacade  Meta  { get; }                 // Tier 0: metadata, no solver cost
    public TopologyBatch BeginEdit();                 // Tier 3: sole gateway to topology change

    // ── Readback (valid after any solve; reads last committed solution) ──
    public double VoltageAt(NodeId n);                // node potential vs island ground
    public double CurrentThrough(ComponentRef c);     // signed branch flow
    public double PowerIn(ComponentRef c);            // instantaneous dissipation/injection
    public double ReadProbe(ProbeId p);               // direct or interpolated observation

    // ── Identity services ──
    public bool TryResolve(ExternalKey key, out ComponentRef c);  // key → live handle
    public bool TryResolveNode(ExternalKey key, out NodeId n);
    public IslandId IslandOf(NodeId n);               // current island (gen-checked)

    // ── Islands & journal ──
    public IslandTable Islands { get; }               // enumerate live islands; get Island by id
    public TopologyJournal Journal { get; }           // ordered event log (see 1.4)

    // ── Drift detection primitives (see 1.7) ──
    public ulong Fingerprint(IslandId island);        // deterministic hash of canonical form
    public void  Export(IslandId island, NetlistExportWriter w);  // canonical, key-sorted dump
}
```

```csharp
// Tier 1 — forward/back substitution on cached LU. Legal every tick/substep.
public readonly struct DriveFacade {
    public void SetSourceValue(VSourceId id, double volts);
    public void SetSourceValue(ISourceId id, double amps);
    public void SetSineDrive(VSourceId id, double amplitudeV, double freqHz); // phase-continuous driver; freq is piecewise-constant input
}

// Tier 2 — numeric refactorization, symbolic pattern reused. Occasional / per-Newton-iteration.
public readonly struct TuneFacade {
    public void SetResistance(ResistorId id, double ohms);
    public void SetSwitch(SwitchId id, bool closed);          // relay contact; stays in-matrix
    public void SetCapacitance(CapacitorId id, double farads); // BE companion conductance changes
    public void SetInductance(InductorId id, double henries);
}

// Tier 0 — metadata only; never touches matrix or RHS. Free at solve time.
public readonly struct MetaFacade {
    public void SetLimits(ComponentRef c, in LimitConfig cfg);          // envelope updates (ambient temp shifts, etc.)
    public void SetProbeInterpolation(ProbeId p, NodeId a, NodeId b, double t); // re-aim an interior probe after re-collapse
    public void SetDebugName(ComponentRef c, string name);              // diagnostics only; may allocate (debug paths)
}
```

### 1.3 Topology batches (Tier 3)

```csharp
// Transactional: all-or-nothing. Dispose without Commit rolls back and (debug) asserts
// unless Abort() was called explicitly — a half-applied topology change cannot exist,
// so the netlist is consistent at every observable moment. Single writer; nesting throws.
public struct TopologyBatch : IDisposable {
    public NodeId AddNode(ExternalKey key);
    public void   MarkGround(NodeId n);               // VS: the literal earth; merged islands may share it
    public ResistorId  AddResistor(NodeId a, NodeId b, double ohms, ExternalKey key);
    public VSourceId   AddVoltageSource(NodeId pos, NodeId neg, double volts, ExternalKey key);
    public ISourceId   AddCurrentSource(NodeId from, NodeId to, double amps, ExternalKey key);
    public CapacitorId AddCapacitor(NodeId a, NodeId b, double farads, ExternalKey key);
    public InductorId  AddInductor(NodeId a, NodeId b, double henries, ExternalKey key);
    public SwitchId    AddSwitch(NodeId a, NodeId b, bool closed, ExternalKey key);
    public DiodeId     AddDiode(NodeId anode, NodeId cathode, in DiodeParams p, ExternalKey key);
    public void Remove(ComponentRef c);               // frees slot; bumps generation
    public void RemoveNode(NodeId n);                 // must be degree-0 at commit, else commit fails

    public ProbeId AddProbe(NodeId n, ExternalKey key);                       // direct observation
    public ProbeId AddInterpolatedProbe(NodeId a, NodeId b, double t, ExternalKey key); // V = Va + t·(Vb−Va): eliminated interior nodes
    public void    RemoveProbe(ProbeId p);

    public void SetBridged(CouplingId c, bool bridged); // breaker: true ⇒ islands merge; false ⇒ rebuild-split. Placement on the batch IS the tier-3 cost signal.

    public EditReceipt Commit();                      // applies atomically; appends to journal; may allocate (shape time)
    public void Abort();                              // explicit rollback; Dispose after Abort is silent
}

// What the commit did to island structure — the client's cue for rehoming derived tables.
public readonly struct EditReceipt {
    public long JournalFrom { get; }                  // first journal seq of this commit
    public long JournalTo   { get; }                  // last (inclusive); replay [From..To] to sync
    public ReadOnlySpan<IslandChange> IslandChanges { get; } // created / merged / rebuilt / removed
}
```

### 1.4 The topology journal

```csharp
// Fixed-capacity ring of structs, monotonic sequence numbers, multiple independent
// read cursors. Derived layers (compaction, VS renderer, debug tooling) stay in sync
// by REPLAY, not by callbacks: no allocation, deterministic order, and a lagging
// reader detects its own overflow (LostEvents) and falls back to full resync —
// the failure mode is explicit, never silent.
public sealed class TopologyJournal {
    public JournalCursor OpenCursor();                // starts at current head
    public bool TryRead(ref JournalCursor c, out TopologyEvent e); // false ⇒ caught up
    public bool Overflowed(in JournalCursor c);       // reader too slow ⇒ must resync
}

public enum TopologyEventKind : byte {
    ComponentAdded, ComponentRemoved, NodeAdded, NodeRemoved,
    IslandCreated, IslandsMerged,     // (Survivor, Absorbed): absorbed id is now stale
    IslandRebuilt,                    // (Old, New): removal-triggered; component handles SURVIVE, island handle does not
    IslandRemoved, IslandFaulted, IslandRecovered
}
public readonly struct TopologyEvent {                // fixed-size struct; no strings
    public long Seq { get; }
    public TopologyEventKind Kind { get; }
    public ComponentRef Component { get; }            // valid per Kind
    public ExternalKey Key { get; }
    public IslandId IslandA { get; }                  // survivor / old / subject
    public IslandId IslandB { get; }                  // absorbed / new
}
```

### 1.5 Islands, analyses, limits

```csharp
// Island lifecycle is an explicit small state machine:
//
//   Ready ──(tier-3 commit touching it)──▶ Ready' (same slot; gen bumps on rebuild/merge-absorb)
//   Ready ──(singular after gmin / Newton ladder exhausted)──▶ Faulted
//   Faulted ──(next tier-2/3 change ⇒ retry succeeds)──▶ Ready
//
// Faulted islands hold their previous solution and read as de-energized.
public enum IslandState : byte { Ready, Faulted }

public sealed class Island {
    public IslandId Id { get; }
    public IslandState State { get; }
    public FaultInfo Fault { get; }                   // participating nodes/components, cause enum; empty when Ready

    // ── Analyses (single-writer per island; client owns threads/scheduling) ──
    public void SolveDc();                            // operating point: caps open, inductors ~1 mΩ (dt ≤ 0 semantics)
    public void Step(double dt);                      // one Backward-Euler transient step
    public void RunTick(double gameDt);               // subcycled AC: N substeps per current plan; linear islands pay tier-1 only
    public SubstepPlan Plan { get; }                  // read: N, substep dt, hysteresis band; N changes are deliberate tier-2 events
    public void SetSubstepPolicy(in SubstepPolicy p); // samples/cycle floor, hysteresis width

    // ── Limits: drained after solve; solver never mutates the circuit ──
    public LimitEventQueue Limits { get; }            // struct ring; overflow counts drops, never allocates

    // ── Snapshot/restore (R6): keyed by ExternalKey, so state survives rebuilds
    //    and from-scratch reconstruction (Stationeers load, VS chunk resume, resync adoption).
    public int  SnapshotSize { get; }
    public void Snapshot(Span<byte> dst);             // cap V, inductor I, device state, source phase; versioned binary
    public RestoreReport Restore(ReadOnlySpan<byte> src); // matches keys; reports orphans both ways (never throws on mismatch)
}

public struct LimitEvent {                            // fixed-size; attribution happens above (reduction layer)
    public ComponentRef Source; public LimitKind Kind; // OverCurrent | OverVoltage | OverPower | ThermalI2t
    public double Observed, Threshold, SubstepTime;
}
public struct LimitConfig { public double MaxAbsCurrent, MaxAbsVoltage, MaxPower; public I2tParams Thermal; }
```

### 1.6 Coupling devices and partitioning intake

```csharp
// Coupling: logical joins that are NOT matrix merges (decoupling transformers,
// converter two-ports). Value exchange is double-buffered per substep with explicit
// relaxation; the device clamps transfer to delivered power (no free energy).
public readonly record struct CouplingId(int Slot, ushort Gen);
public sealed class CouplingTable {
    public CouplingId AddCoupling(PortSpec a, PortSpec b, CouplingKind kind, in CouplingParams p, ExternalKey key);
    public void SetRelaxation(CouplingId c, double alpha);       // tier 0
    public ExchangeView LastExchange(CouplingId c);              // amplitude+phase / P,V — instrumentation
}

// Partitioning mode (NetlistOptions): SelfPartitioned (VS/tablet: union-find is ours) or
// ClientPartitioned (Stationeers: nodes carry PartitionKey = CableNetwork id; a commit
// that would merge across partition keys FAILS — vanilla networks never merge, and the
// API enforces the game's invariant instead of trusting the adaptor).
public enum PartitioningMode : byte { SelfPartitioned, ClientPartitioned }
```

### 1.7 The reduction layer as a typed client

```csharp
// Everything below uses ONLY the public API above: batches, probes, Meta.SetLimits,
// journal replay, Export/Fingerprint. No internal access.
public sealed class Reducer {
    public Reducer(Netlist net, ReducerOptions opts);

    // ── Incremental intake. GeometryDelta = adds/removes of conductor elements at the
    //    client's natural granularity (voxels / cable segments / schematic wires).
    public void ApplyDelta(in GeometryDelta delta);
    // Per-island dirty state machine (explicit, queryable):
    //   Clean ──pure add, no bridge──▶ Clean            (incremental fast path, applied immediately)
    //   Clean ──add bridging >1 region──▶ Clean         (chain splice / island merge, still incremental)
    //   Clean ──any removal──▶ RebuildPending           (coalesced)
    //   RebuildPending ──more edits──▶ RebuildPending   (absorbed; a deconstruction burst costs ONE rebuild)
    //   RebuildPending ──FlushRebuilds()──▶ Clean
    public ReducerDirtyState DirtyState(IslandId island);
    public void FlushRebuilds();                      // call once per tick, before solving; runs coalesced rebuilds

    // ── Attribution: solver's limit event on an equivalent → which segment melts.
    //    Per-limit-type envelope: ampacity, i²t mass, melting threshold can each pick a
    //    different constituent (lead fuse in a copper run). Pure table lookup; zero-alloc.
    public void Attribute(in LimitEvent e, out Attribution a);   // a.SegmentKey, a.LimitKind, a.Margin
    public void SetAmbient(GeomKey region, double celsius);      // dirties envelope only ⇒ Meta.SetLimits; never a matrix change

    // ── Interior observation: geometry position → probe on the collapsed chain.
    public ProbeId ProbeAt(GeomKey where);            // creates/repoints an interpolated probe (t = cumulative-R fraction)

    // ── Resync backstop (R11): shadow rebuild + canonical diff + adoption. ──
    public ReductionExport ExportLive(IslandId island);                    // canonical, ExternalKey-sorted
    public static ReductionExport BuildShadow(IGeometrySource truth, in ReducerOptions o); // from-scratch, no live netlist touched
    public DriftReport Diff(IslandId island, in ReductionExport shadow);   // typed diffs (see below)
    public void Resync(IslandId island, in ReductionExport shadow);        // snapshot → rebuild island from shadow → restore by key
}

public readonly struct DriftReport {                  // every entry is actionable — a bug report, not a mystery
    public bool IsEmpty { get; }
    public ReadOnlySpan<DriftEntry> Entries { get; }  // MissingInLive | MissingInShadow | ValueMismatch(key, live, shadow)
}                                                     //   | EnvelopeMismatch | ProbeWeightMismatch | IslandPartitionMismatch
```

---

## 2. How the tiers appear on the API surface

The tier of an operation is decided by **which type you had to go through to say it** — you
cannot state an expensive operation in cheap-looking code:

| Tier | Surface | Call-site shape | How you know before profiling |
| --- | --- | --- | --- |
| 0 metadata | `net.Meta.*` | `net.Meta.SetLimits(r, cfg)` | facade name; documented "never touches matrix" |
| 1 RHS | `net.Drive.*` | `net.Drive.SetSourceValue(v, 12.0)` | facade name; only sources live here |
| 2 conductance | `net.Tune.*` | `net.Tune.SetSwitch(sw, closed: true)` | facade name; refactorization documented on the facade type |
| 3 topology | `TopologyBatch` only | `using var e = net.BeginEdit(); … e.Commit();` | you cannot add/remove anything without opening a batch |

Consequences that make this legible in practice:

- **Greppable cost audits.** "Nothing above tier 1 in the substep loop" is
  `grep -n '\.Tune\.\|BeginEdit' HotPath.cs` — a review rule a non-EE SRE can enforce
  mechanically, and a Roslyn analyzer can enforce automatically later.
- **Scoped capability, not convention.** Device `Tick()` receives `(DriveFacade, TuneFacade)`
  and *no netlist reference* — a behavioral device cannot commit topology mid-tick even by
  accident; the misuse fails to typecheck (sparky's house rule, applied to cost).
- **The odd cases sit on the honest side of the line.** Relay contact = `Tune.SetSwitch`
  (tier 2, in-matrix, hot path legal). Breaker = `batch.SetBridged` (tier 3; opening a breaker
  is an island rebuild and the API makes you hold the tier-3 token to say it). AC substep-count
  changes are not client-triggerable at all — `RunTick` decides, with hysteresis, and `Plan`
  lets you observe when it happened.
- **Shape time vs run time.** `Commit()` is documented as the only allocation point (§4);
  tier ⇒ allocation behavior is one rule, not a per-method footnote.

---

## 3. Worked example: one voxel, three consequences

Scenario: player places a copper cable voxel at `P`. It extends chain **A** (live island `I_A`)
*and* touches the free end of chain **B** (island `I_B`) — an incremental splice plus an island
merge. Later, the periodic backstop verifies the incremental state.

```csharp
// ── VS extraction layer (runs on the electrical tick, before solving) ─────────
public void OnVoxelPlaced(VoxelKey p, MaterialId copper) {
    _delta.Clear();
    _delta.Add(p, copper);                       // struct list, reused
    _reducer.ApplyDelta(in _delta);
}

// ── Inside Reducer.ApplyDelta — the bridge path (shown as it would be written) ──
// Geometry first: which existing regions does P touch?  (pure voxel math, no netlist)
int n = _regions.CollectNeighbors(p, _touchBuf); // → chain A's far end, chain B's far end
// Merge pre-check: >1 region touched and no removals pending ⇒ still incremental —
// a splice replaces two equivalents with one; nothing else in either island moves.
ChainRecord a = _chains[_touchBuf[0]], b = _chains[_touchBuf[1]];
double rP = Resistivity(copper, CrossSection(p));

using var edit = _net.BeginEdit();
edit.Remove(a.Equivalent);                        // both old equivalents go…
edit.Remove(b.Equivalent);
edit.RemoveNode(a.NearNode);                      // interior junction nodes now degree-0
edit.RemoveNode(b.NearNode);
var merged = edit.AddResistor(a.FarNode, b.FarNode,
    a.TotalOhms + rP + b.TotalOhms,
    key: ChainKey(a, p, b));                      // deterministic: min constituent voxel key
EditReceipt rc = edit.Commit();                   // ← the ONLY point where I_A and I_B merge

// Sync derived tables by replaying exactly this commit's journal window:
for (var c = rc.JournalFrom; _net.Journal.TryReadRange(ref c, rc.JournalTo, out var ev);) {
    if (ev.Kind == TopologyEventKind.IslandsMerged)
        _perIsland.Rehome(from: ev.IslandB, into: ev.IslandA);   // I_B's chain index moves under I_A
}

// Chain bookkeeping — all tier 0 / local:
ChainRecord m = ChainRecord.Splice(a, p, rP, b);  // prefix-sum of R over constituents (probe t values)
_net.Meta.SetLimits(merged, m.Envelope.Combined); // per-limit-type minima over constituents; envelope
                                                  // remembers WHICH segment holds each minimum
foreach (ref var probe in m.InteriorProbes)       // interior instruments keep reading correctly:
    _net.Meta.SetProbeInterpolation(probe.Id, a.FarNode, b.FarNode, m.TOf(probe.Where));
```

State-machine summary of what just happened: reducer stayed `Clean → Clean` (pure add, splice
path); netlist emitted `ComponentRemoved ×2, NodeRemoved ×2, ComponentAdded, IslandsMerged` as
one atomic journal window; island `I_B`'s handle is now stale — any code still holding it gets
`StaleHandleException`, not garbage.

Had the player instead *removed* a voxel: `ApplyDelta` flips that island to `RebuildPending`,
further edits accumulate, and `FlushRebuilds()` (once, at solve time) snapshots state, rebuilds
from geometry, and restores by `ExternalKey` — a deconstruction burst costs one rebuild.

```csharp
// ── Resync backstop (debug config in-game; continuous in CI) ─────────────────
// Two-level: cheap fingerprint every N ticks; full canonical diff only on mismatch.
public void BackstopCheck(IslandId island) {
    ReductionExport shadow = Reducer.BuildShadow(_world.GeometrySnapshot(island), _opts);
    if (_net.Fingerprint(island) == shadow.NetlistFingerprint) return;    // in sync — the common case

    DriftReport drift = _reducer.Diff(island, in shadow);                 // keyed, typed differences
    foreach (ref readonly var d in drift.Entries)
        _log.DriftEntry(island, d);              // e.g. "ValueMismatch key=Chain(1281,64,977) live=0.0431Ω shadow=0.0442Ω"
    _reducer.Resync(island, in shadow);          // snapshot → adopt shadow → restore by key: caps/phase survive
}
```

Because exports are canonical (sorted by `ExternalKey`, node identity expressed as node *keys*,
never slots), the diff is stable across arbitrary internal renumbering — incremental and
from-scratch builds are compared in a representation where "same circuit" means byte-equal.
Every drift entry names a client-meaningful key: a bug report with coordinates, not corruption.

---

## 4. The zero-alloc story (R8)

**One rule: allocation happens at shape time (`Commit`, `FlushRebuilds`, `Resync`), never at
run time.** Every buffer the tick loop touches is owned by a per-island arena sized at the last
tier-3 commit:

| Buffer | Lives in | Sized at |
| --- | --- | --- |
| Matrix + LU storage (dense, in-place) | `IslandArena` | commit |
| RHS + solution vectors, Newton workspace | `IslandArena` | commit |
| Probe table, interpolation weights | `IslandArena` | commit |
| `LimitEventQueue` ring (fixed cap; drops counted) | `IslandArena` | commit |
| Journal ring (fixed cap; overflow ⇒ reader resyncs) | `Netlist` | construction |
| `ExternalKey → ComponentRef` map | `Netlist` | commit (only changes then) |
| Coupling exchange double-buffers | `CouplingTable` | commit |
| Snapshot scratch (`SnapshotSize` is stable between commits) | client | after commit |

Steady-state tick, per island, on the client's worker thread:

```csharp
// Warm loop: zero bytes allocated. Tier-1/2 facades write into preallocated slots;
// RunTick does RHS fill + back-substitution in place (tier 1); a relay toggle earlier
// in the tick means one in-place numeric refactorization (tier 2), same storage.
foreach (var dev in island.Devices) dev.Tick(net.Drive, net.Tune, dt);  // battery curves, alternator EMF…
island.RunTick(GameDt);                                    // N substeps; sine phase advances internally
while (island.Limits.TryDequeue(out LimitEvent e)) {       // struct dequeue
    reducer.Attribute(in e, out Attribution hit);          // table lookup, out-struct
    consequences.Enqueue(in hit);                          // client-owned fixed ring
}
double vScope = net.ReadProbe(scopeProbe);                 // array index + fma (interpolated)
```

Enforcement is layered, per the SRE instinct that contracts need monitors: (a) BenchmarkDotNet
`MemoryDiagnoser` gates CI on the canonical tick loop; (b) debug builds wrap `RunTick` in an
`AllocationSentinel` (`GC.GetAllocatedBytesForCurrentThread` delta must be 0) so a regression
fires on the *first* offending tick in ordinary dev play, not at the next benchmark run;
(c) the CSparse.NET large-island fallback is explicitly exempt (solver.md) — `NetlistOptions`
exposes `OnFallbackEngaged` so the exemption is observable, not silent.

---

## 5. Rationale, trade-offs, open questions

**Why generational handles when sparky's plain `record struct(int)` worked?** Sparky's ids were
never invalidated by a live incremental layer. Here the compaction layer *routinely* deletes and
recreates components under long-lived client references; slot reuse without generations turns
every compaction bug into silent aliasing. Generations make the failure mode "throw with a
name" — the difference between debuggable and cursed. Cost: 2 bytes per handle; zero on the hot
path (checks compile to one compare, and elide in release readback if we choose).

**Why mandatory `ExternalKey` on every Add?** It is the single mechanism behind four features:
canonical drift diffs, snapshot/restore across rebuilds, `Resync` adoption without state loss,
and Stationeers save/load (networks rebuild every load; keys are the only stable identity).
Making it optional would fork every one of those paths. Cost: clients must mint deterministic
keys — real design work for VS (region representative must be edit-stable; proposed: minimum
voxel coordinate in the region, which changes exactly when the region's identity meaningfully
changes and the diff will say so). 128-bit because VS needs dimension+position+kind without
collision anxiety.

**Why a pollable journal instead of C# events?** Zero-alloc (structs in a ring), deterministic
replay order, multiple independent consumers, and — decisively — an explicit overflow story:
a lagging consumer *knows* it lost events and falls back to resync. Callbacks fail by silently
not being subscribed yet, or by re-entering the netlist mid-commit. Trade-off: consumers must
remember to drain; mitigated by `EditReceipt` carrying the exact window so the common pattern
("sync after my own commit") needs no standing cursor.

**Why transactional batches?** The alternative — apply-as-you-go with a "please batch" fence —
means an exception mid-edit leaves a half-spliced netlist that the journal cannot describe.
Atomic commit keeps two invariants trivially true: *the netlist is always a consistent circuit*
and *the journal is a complete history of consistent states*. The resync backstop then has a
well-defined thing to compare against. Trade-off: `Commit` must stage state (shape-time
allocation, acceptable) and very large batches (initial Stationeers load, ~10k segments) stage
a lot — mitigated by `NetlistOptions` bulk-build mode for the from-empty case (journal emits
one `IslandCreated` summary instead of 10k adds).

**Why facades for tiers instead of doc-comment tags (solver.md's current sketch)?** Doc tags
inform; types constrain. The device-Tick capability scoping (no topology mid-tick, by
signature) and the greppable audit rule both fall out for free. Trade-off: three extra tiny
types and slightly longer call sites; `net.Drive.SetSourceValue` reads as intent, so I take it.

**Trade-offs accepted knowingly:** RemoveNode requiring degree-0 pushes cleanup bookkeeping onto
clients (the Reducer tracks junction degree anyway; the tablet deletes wires before nodes
naturally). Rebuild preserves component handles but not island handles — an asymmetry, but the
right one: components have client meaning, islands are solver artifacts, and per-island derived
tables must refresh on rebuild regardless. Ground marking survives merges (SWER: every island
touches earth) but two *explicit* grounds merging is legal and silently fine — real earth is one
node.

**Open questions for the panel/synthesis:**
1. Should readback (`VoltageAt` et al.) move onto `Island` for single-writer thread-locality
   symmetry, at the cost of clients resolving `IslandOf` first? Current placement favors
   ergonomics; debug asserts cover cross-thread misuse either way.
2. `Fingerprint` scope: structure + parameters is clearly in; should it also cover Tier-0
   metadata (limit envelopes, probe weights)? Compaction drift *can* live purely in envelopes
   (compaction.md's environment-adjustment path), which argues yes — at the cost of ambient-temp
   updates churning the fingerprint. Proposal: two fingerprints (`Structural`, `Full`).
3. Is `TopologyBatch` a `struct` (zero-alloc, but copyable — a copied batch is a footgun) or a
   pooled `sealed class`? Lean: pooled class rented from the netlist; batches are shape-time
   anyway.
4. Does the tablet want undo/redo built on journal replay + snapshots (it comes surprisingly
   close to free), or is that the harness document-layer's job? Lean: harness owns undo;
   core guarantees only that replay is deterministic.
5. `ClientPartitioned` merge rejection: fail the whole commit (proposed — atomicity) or fail
   only the offending edge? Needs Sukasa's read on adaptor ergonomics.

