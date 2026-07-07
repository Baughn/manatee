# manatee-core: Netlist API

Last updated: 2026-07-07
Status: CANON pending owner review.

The public surface of manatee-core: the contract every client (Re-Volt
adaptor, VS extraction, tablet, and the reduction layer itself) programs
against. Synthesized from the API competition
(docs/experiments/api-competition/): base is Proposal 1 "Four Verbs", with
the judges' steal lists folded in as requirements and the rejected list kept
rejected. The four open questions of synthesis.md are resolved here (§16,
§8, §17, §5). Contested calls are recorded in the [Decision log](#24-decision-log);
unresolved residue is in [Open items](#23-open-items).

Notation on members: `[Tn]` = change-cost tier (0 meta / 1 RHS /
2 conductance / 3 topology); `0B` = zero heap allocation on that call after
warmup; `shape` = allocates, but only at structural-commit ("shape") time,
never in a tick; `cold` = diagnostic path, never in a tick, may allocate.

## Table of Contents

1. [Ground rules](#1-ground-rules)
2. [Assembly and namespace layout](#2-assembly-and-namespace-layout)
3. [Handles and keys](#3-handles-and-keys)
4. [The four verbs + tier-0 Meta](#4-the-four-verbs--tier-0-meta)
5. [Options, profiles, wiring policy (OQ4)](#5-options-profiles-wiring-policy-oq4)
6. [StructuralEdit](#6-structuraledit)
7. [Coupling devices](#7-coupling-devices)
8. [SteadyStateGuard and AllocationSentinel (OQ2)](#8-steadystateguard-and-allocationsentinel-oq2)
9. [TickStats and cost queries](#9-tickstats-and-cost-queries)
10. [Solution and readback](#10-solution-and-readback)
11. [Islands](#11-islands)
12. [Limits](#12-limits)
13. [Probes and the oscilloscope tap](#13-probes-and-the-oscilloscope-tap)
14. [Snapshot, restore, serialization laws](#14-snapshot-restore-serialization-laws)
15. [TopologyJournal](#15-topologyjournal)
16. [Handle-survival table (OQ1)](#16-handle-survival-table-oq1)
17. [The Reconfigure-to-Solve story (OQ3)](#17-the-reconfigure-to-solve-story-oq3)
18. [Devices-layer contract](#18-devices-layer-contract)
19. [Reduction-layer intake contract](#19-reduction-layer-intake-contract)
20. [Error model](#20-error-model)
21. [Threading, phases, spans, allocation](#21-threading-phases-spans-allocation)
22. [Consumer walkthroughs](#22-consumer-walkthroughs)
23. [Open items](#23-open-items)
24. [Decision log](#24-decision-log)

---

## 1. Ground rules

Binding, inherited from design.md/solver.md and the synthesis:

- Pure C#, `netstandard2.1;net8.0` multitarget (ns2.1 ceiling = Re-Volt's
  Unity 2022.3). No engine API anywhere.
- **Zero heap allocation in tiers 0–2 after warmup** (R8). Enforcement is
  layered (§21): BenchmarkDotNet MemoryDiagnoser in CI is the *binding* gate;
  the in-process debug sentinel is best-effort where the runtime supports it.
- **No callbacks into clients, ever.** All solver→client communication is
  post-solve polling of structs. No C# `event`, no LINQ, no iterator methods
  on any hot surface.
- **Single writer per island**, enforced by debug asserts, not locks; clients
  own threads and scheduling. Deterministic per island regardless of thread
  interleaving.
- **Every mutation is one of four verbs** — `Drive` (T1), `Adjust` (T2),
  `Reconfigure` (T3-lite, coupler membership), or a structural edit that
  exists only on a `StructuralEdit` scope you must ask for loudly. R4
  promoted from documentation to grammar.
- Deep layers avoid electrical-only naming where cheap (potential/flow, not
  voltage/current) so thermal-RC reuse stays open.
- Explicitly rejected (stays rejected): always-on command recording in the
  hot path; journal-drain as a correctness obligation for game clients;
  client-callable AC substep control.

## 2. Assembly and namespace layout

One shipping assembly, `Manatee.Core.dll`. CSparse.NET and the ngspice
harness are referenced only by test/oracle projects (LGPL never ships).

| Namespace | Contents | Visibility |
| --- | --- | --- |
| `Manatee.Core` | Handles, keys, `Netlist`, the four verbs, `Meta`, `StructuralEdit`, `NetlistOptions`/`SolverProfile`/`WiringPolicy`, `IslandTable`/`IslandHandle`, `Solution`, `SteadyStateGuard`, `TickStats`, `TickClock`, limits, probes, `TopologyJournal`, invariants, snapshot | **public** |
| `Manatee.Core.Solver` | Per-island `Circuit` (matrix + companion state), `ISolverBackend` (the proven Analyze/Factorize/Solve lifecycle from `experiments/solver-bench/Contract.cs`), in-house sparse LU (sole production backend), dense LU (test referee), stamps, gmin/pivot logic. Names are **Potential/Flow**. | **internal** (`InternalsVisibleTo` test + oracle assemblies only) |
| `Manatee.Core.Devices` | `Device`, `TerminalSpec`, `DeviceTickContext`, built-in models (battery, alternator, idealized transformer, `AdaptedLoad`/`AdaptedSource`), `EnergyLedger` | **public** |
| `Manatee.Core.Reduction` | `ConductorGraph`, `ConductorSpec`, `SegmentKey`/`JunctionKey`, attribution, `DriftReport`, resync | **public** |
| `Manatee.Core.Diagnostics` | `SpiceDeck`, `NetlistSerializer` helpers, `CommandRecorder`/`ReproBundle`/`Replay` (opt-in), JSON debug form of snapshots | **public, cold-path** |

Rules enforced by visibility:

- **Clients never see a matrix.** Only `Netlist` talks to `Solver`.
- **The reduction layer is an ordinary public client** — it holds no internal
  reference, gets no `InternalsVisibleTo` friendship, and uses *only* the
  public `Manatee.Core` surface (journal replay, probes, `Meta.SetLimits`,
  `Fingerprint`/`SaveNormalized`, snapshot/restore). If reduction needs a
  capability, it becomes public API the tablet and the Stationeers adaptor
  may equally use. There is no second, untested path into the solver.
  *(Binding maintainer invariant.)*

## 3. Handles and keys

```csharp
namespace Manatee.Core;

// Generational typed handles: (Slot, Gen, Net). Misuse across kinds fails to
// COMPILE (sparky's typed-ID pattern). Stale, wrapped, or cross-netlist use
// fails FAST: StaleHandleException in debug; a defined sentinel + TickStats
// counter in release (§20). Gen is 32-bit: a slot reused every 50 ms tick
// wraps in ~6.8 years (documented bound; debug warns at 2^31). Net is a
// process-wide netlist id so a handle from another Netlist is caught by
// value, not by accident of slot reuse.
public readonly record struct NodeId(int Slot, uint Gen, ushort Net);
public readonly record struct ResistorId(int Slot, uint Gen, ushort Net) : IComponentId;
public readonly record struct VSourceId(int Slot, uint Gen, ushort Net) : IComponentId;
public readonly record struct ISourceId(int Slot, uint Gen, ushort Net) : IComponentId;
public readonly record struct CapacitorId(int Slot, uint Gen, ushort Net) : IComponentId;
public readonly record struct InductorId(int Slot, uint Gen, ushort Net) : IComponentId;
public readonly record struct SwitchId(int Slot, uint Gen, ushort Net) : IComponentId;  // relay contact
public readonly record struct DiodeId(int Slot, uint Gen, ushort Net) : IComponentId;
public readonly record struct TransformerId(int Slot, uint Gen, ushort Net) : IComponentId; // ideal two-port (§6)
public readonly record struct CouplerId(int Slot, uint Gen, ushort Net);  // breaker / decoupling xfmr / converter
                                                                          // — document-stable across rebuilds (§16)
public readonly record struct IslandId(int Slot, uint Gen, ushort Net);
public readonly record struct ProbeId(int Slot, uint Gen, ushort Net);

public enum ComponentKind : byte { Resistor, VSource, ISource, Capacitor, Inductor, Switch, Diode, IdealTransformer }
public readonly record struct ComponentRef(ComponentKind Kind, int Slot, uint Gen, ushort Net);

// Value-type interface for generic readbacks/removal. IMPLEMENTATION NOTE
// (binding): generic methods constrained `where TId : struct, IComponentId`
// MUST reach the handle via the constrained-callvirt pattern (direct
// interface call on the value type) — never boxing, never an Unsafe.As
// type-switch that relies on JIT dead-code elimination Mono may not do.
// This is the load-bearing rule that keeps tier-1 readback 0B on both runtimes.
public interface IComponentId { ComponentRef AsRef(); }
```

Two client-stable keys, with distinct granularity (see Decision log #2):

```csharp
// EXTERNALKEY — client-stable TOPOLOGICAL identity. MANDATORY on every Add.
// Drives TryResolve, drift diffs, canonical/normalized serialization,
// IslandTable.Ids ordering. 128-bit: VS packs dimension+position+material
// without collision anxiety; Stationeers packs a RefId; tablet packs an
// element id. Uniqueness per netlist is debug-asserted. This is the ONLY
// topological identity that survives an island rebuild.
public readonly record struct ExternalKey(ulong Hi, ulong Lo)
{
    public ExternalKey(ulong refId) : this(0, refId) { }        // Stationeers convenience
    public ExternalKey Derive(ushort ordinal);                  // deterministic sub-key (device components, §18)
}

// STATEKEY — client-stable identity of a unit of SERIALIZABLE DYNAMIC STATE:
// a whole device, or a bare cap/inductor/sine source. MANDATORY where state
// exists (non-optional parameter ⇒ forgetting it is a compile error; device
// state can never silently cold-start). Snapshot blobs key on StateKey and
// nothing else. Distinct from ExternalKey because one device (one StateKey)
// owns many components (many ExternalKeys). A bare stateful primitive may
// reuse its ExternalKey bits: StateKey.From(key).
public readonly record struct StateKey(ulong Hi, ulong Lo)
{
    public StateKey(ulong refId) : this(0, refId) { }
    public static StateKey From(in ExternalKey k);
}

public readonly record struct PartitionKey(ulong Value);        // ClientPartitioned: CableNetwork id.
                                                                // default(PartitionKey) == PartitionKey.None,
                                                                // a reserved sentinel (SelfPartitioned nodes;
                                                                // coupler-spanning islands — §11)
```

## 4. The four verbs + tier-0 Meta

`Netlist` is the retained-mode circuit **document**. The verbs write the
document; matrices are *derived* from it at Solve. This distinction carries
the whole OQ3 story (§17): a verb call is never lost even if the island is
about to rebuild, because the rebuild restamps from the document.

```csharp
public sealed class Netlist
{
    public Netlist(in NetlistOptions opts);   // regime, wiring, partitioning, hints — fixed at birth

    // ── Tier 1: Drive — RHS only; back-substitution on cached LU. Every tick/substep. ──
    [CostTier(1)] public void Drive(VSourceId id, double volts);      // 0B
    [CostTier(1)] public void Drive(ISourceId id, double amps);       // 0B
    [CostTier(1)] public void Drive(VSourceId id, in SineDrive d);    // 0B; phase-continuous source driver

    // ── Tier 2: Adjust — conductance; numeric refactor, symbolic pattern reused. ──
    // ε-no-op (pinned definition, §9): the call degrades to a tier-0 document write
    // when the change is below AdjustEpsilon — converged G=P/V² adaptor loops stop
    // paying tier 2 without client involvement. TickStats.AdjustNoOps shows it.
    [CostTier(2)] public void Adjust(ResistorId id, double ohms);     // 0B
    [CostTier(2)] public void Adjust(SwitchId id, bool closed);       // 0B; relay contact, stays in-matrix
    [CostTier(2)] public void Adjust(CapacitorId id, double farads);  // 0B; BE companion G changes
    [CostTier(2)] public void Adjust(InductorId id, double henries);  // 0B
    [CostTier(2)] public void Adjust(DiodeId id, in DiodeParams p);   // 0B
    [CostTier(2)] public void Adjust(TransformerId id, double turnsRatio); // 0B; coupled-row values change

    // ── Tier 3-lite: Reconfigure — membership of an EXISTING coupler. Its own verb
    // (not Edit) because relay-vs-breaker duality puts one tier-3 op in the
    // runtime-normal path; Edit() keeps meaning "construction". Galvanic coupler:
    // Closed ⇒ incremental merge; Open ⇒ split-rebuild, coalesced to next Solve.
    // Boundary coupler (xfmr/converter): toggles exchange only — no rebuild.
    // Barred inside EnterSteadyState (§8).
    [CostTier(3)] public void Reconfigure(CouplerId id, CouplerState state);  // 0B itself; rebuild deferred & counted

    // ── Tier 3: structural. The only door. ──
    [CostTier(3)] public StructuralEdit Edit();          // pooled batch scope; atomic commit on Dispose (§6)

    // ── Tier 0: Meta facade — never touches matrix or RHS; visibly free. ──
    public MetaFacade Meta { get; }

    // ── Solve & read ──
    public void SolveOperatingPoint();       // DC op-pt (caps open, inductors ~1 mΩ); energize / lesson start
    public void Solve(in TickClock clock);   // one game tick: dirty scheduling units only, serial (small clients)
    public IslandTable Islands { get; }      // per-island scheduling for clients that own threads (§11)
    public Solution Solution { get; }        // committed values of the last published solve (§10, §21)

    // ── Legibility rails ──
    public SteadyStateGuard EnterSteadyState();          // §8; ref struct, 0B
    public ref readonly TickStats LastTickStats { get; } // §9
    public DeviceTickContext TickContext(double dt);     // tier-≤2 capability for device Tick (§18)

    // ── Identity re-resolution (the durable path across rebuilds, §16) ──
    public bool TryResolve(in ExternalKey key, out ComponentRef c);   // 0B; false on miss, never throws
    public bool TryResolveNode(in ExternalKey key, out NodeId n);     // 0B
    public bool TryResolveCoupler(in ExternalKey key, out CouplerId c);// 0B
    public bool TryResolveProbe(in ExternalKey key, out ProbeId p);   // 0B; the probe re-resolution path
                                                                      // (drift resync, FromCanonical — §13, §16)
    public IslandId IslandOf(NodeId n);                               // 0B; gen-checked

    // ── Cost queries for client schedulers (§9) ──
    public Tier CostOfAdjust(ResistorId id, double ohms);        // Metadata(0) if ε-no-op, else Conductance(2)
    public Tier CostOfReconfigure(CouplerId id, CouplerState s); // Rhs(1)=no-change | Topology(3)=merge/split

    // ── Journal (compaction's sync channel; game clients ignore it — §15) ──
    public TopologyJournal Journal { get; }

    // ── Drift / serialization primitives (§14) ──
    public ulong Fingerprint(IslandId island, FingerprintScope scope);   // 0B; drift backstop level 1
    public void SaveCanonical(IBufferWriter<byte> dst);    // cold; slot-preserving ⇒ replay/snapshot-compatible
    public void SaveNormalized(IBufferWriter<byte> dst);   // cold; key-sorted minimal ⇒ equality goldens / drift diff
    public static Netlist FromCanonical(ReadOnlySpan<byte> src, in NetlistOptions opts);
}

public enum Tier : byte { Metadata = 0, Rhs = 1, Conductance = 2, Topology = 3 }
public enum FingerprintScope : byte { Structural, Full }   // Full adds tier-0 envelopes/probe weights
public enum CouplerState : byte { Open, Closed }
public readonly record struct TickClock(long TickIndex, double Dt);   // determinism + AC phase continuity
public readonly record struct SineDrive(double AmplitudeV, double FreqHz, double PhaseRad);

public readonly struct MetaFacade
{
    [CostTier(0)] public void SetLimits(ComponentRef c, in LimitSpec cfg);     // 0B; envelope/ambient recompute
                                                                               // (same LimitSpec as Add-time, §12)
    [CostTier(0)] public void SetThermalEnvelope(ComponentRef c, ReadOnlySpan<I2tPair> pairs);
                                                                               // register/replace the Pareto thermal
                                                                               // SET (§12/§19); empty span clears;
                                                                               // supersedes LimitSpec.Thermal
    [CostTier(0)] public void SetProbeInterpolation(ProbeId p, NodeId a, NodeId b, double t); // 0B; re-aim interior probe
    [CostTier(0)] public void SetDebugName(ComponentRef c, string name);       // debug builds only; may alloc
}

[AttributeUsage(AttributeTargets.Method)]
public sealed class CostTierAttribute : Attribute { public CostTierAttribute(int tier) { } }
```

Verbs are named methods on `Netlist` (P1 base): `\.Adjust(`, `\.Reconfigure(`,
`\.Edit(` grep to every tier-≥2 site, so a non-EE reviewer can audit a
client's cost profile mechanically. `Reconfigure` reports as `Tier.Topology`
in cost queries; it is not a fifth tier, just the one topology op with a
runtime-normal name.

## 5. Options, profiles, wiring policy (OQ4)

The core is **agnostic** to wiring convention. It requires exactly one
reference node (MNA datum) per island, keeps floating subgraphs nonsingular
with gmin (1e-12 S) shunts, and applies a **construction-time wiring policy**
so that neither client writes per-device grounding boilerplate.

```csharp
public readonly struct NetlistOptions
{
    public SolverProfile Profile { get; init; }         // Dc | Transient | Mixed
    public WiringPolicy Wiring { get; init; }
    public PartitioningMode Partitioning { get; init; } // SelfPartitioned | ClientPartitioned
    public double AdjustEpsilon { get; init; }          // ε-no-op threshold (§9; default 1e-4)
    public int ExpectedIslands, ExpectedNodes { get; init; }  // arena presize hints, not caps
    public int JournalCapacity { get; init; }           // fixed ring; 0 ⇒ default
    public DebugLevel Debug { get; init; }              // Off | Asserts | AllocationSentinel

    // Named client bundles — the misuse-resistant defaults.
    public static NetlistOptions Stationeers(double dt = 0.5) => new() {
        Profile = SolverProfile.Dc(dt), Wiring = WiringPolicy.ReferenceBound(),
        Partitioning = PartitioningMode.ClientPartitioned };
    public static NetlistOptions VintageStory(double tickDt = 0.05) => new() {
        Profile = SolverProfile.Mixed(tickDt), Wiring = WiringPolicy.TwoWireLeak(1e6),
        Partitioning = PartitioningMode.SelfPartitioned };
    public static NetlistOptions Tablet(double tickDt = 0.05) => new() {
        Profile = SolverProfile.Mixed(tickDt), Wiring = WiringPolicy.ExplicitOnly(),
        Partitioning = PartitioningMode.SelfPartitioned };
}

public readonly struct SolverProfile
{
    public static SolverProfile Dc(double dt);            // Stationeers: dt=0.5, DC + BE storage dynamics
    public static SolverProfile Transient(double dt);     // VS DC-side / tablet: dt=0.05
    public static SolverProfile Mixed(double tickDt, int acSamplesPerCycle = 20);
    // Mixed: per-island regime chosen BY CONTENT — one Netlist hosts both DC and AC
    // islands; islands containing sine sources subcycle (N per island, quantized
    // with hysteresis per solver.md — never client-settable), pure-DC islands step once.
    // A sine source in a NON-Mixed profile is legal but single-sampled per tick
    // (no >=20-samples/cycle guarantee — deterministic and phase-wrapped, but
    // heavily undersampled). Debug builds warn at Add time (ruled 2026-07-06:
    // accepted user choice, warned footgun — same family as the floating-Return
    // warning in §5).
}

// The per-client wiring surface. Three modes; fixed at Netlist birth.
public readonly struct WiringPolicy
{
    // Stationeers: single-terminal vanilla devices; every return terminal binds to
    // its partition's reference node through a near-ideal return conductance —
    // numerically identical to modeling the return conductor; the full double-wire
    // solve is never paid. No leak stamped.
    public static WiringPolicy ReferenceBound(double returnConductanceSiemens = 1e3);

    // VS: two-wire; supply AND return are real routed nodes, so islands float.
    // At commit, auto-stamp `leakOhms` (~1 MΩ) from every Return-role node to the
    // island's reference (the literal earth). Benchmarked at ~machine-eps
    // conditioning cost. The leak resistor is never hand-authored and has no
    // client handle.
    public static WiringPolicy TwoWireLeak(double leakOhms = 1e6);

    // Tablet: ideal nodes, no implicit stamps at all. Ground is marked explicitly
    // (AddReferenceNode); deliberately floating lessons stay floating (gmin only).
    public static WiringPolicy ExplicitOnly();
}

public enum NodeRole : byte { Internal, Return, Reference }
public enum PartitioningMode : byte { SelfPartitioned, ClientPartitioned }
```

**Where the policy fires — declaratively, never per-device:** on the `neg`
argument of `AddVoltageSource`/`AddSineSource`, on every node created with
`NodeRole.Return`, and on every device terminal a `Device` declares in
`TerminalSpec.ReturnTerminals` (§18). There is deliberately **no
`BindNegative` call**. Raw netlist construction (tablet free-play) tags roles
by hand or uses `ExplicitOnly`.

**Reference nodes.** `AddReferenceNode` / `MarkReference` designates the
island reference (MNA row 0). VS: the literal earth node, reached in
gameplay through per-electrode contact resistance (an ordinary resistor the
client stamps). Stationeers: each partition's return rail,
`ConductorGraph.ReferenceNode(PartitionKey)`. If no reference is marked, the
island auto-anchors an arbitrary node (solver.md). On merge, explicit
references coalesce — real earth is one node; legal and silent. Debug builds
warn on a floating subgraph that has no leak, no reference, and no source
path to one (the mistagged-Return footgun made visible).

## 6. StructuralEdit

```csharp
public sealed class StructuralEdit : IDisposable   // pooled, rented from the netlist; not copyable; nesting throws
{
    // ── Nodes & reference ──
    public NodeId AddNode(in ExternalKey key, NodeRole role = NodeRole.Internal,
                          PartitionKey partition = default);
    public NodeId AddReferenceNode(in ExternalKey key);
    public void   MarkReference(NodeId n);
    public void   SetPartition(NodeId n, PartitionKey p);   // ClientPartitioned: CableNetwork id

    // ── Primitives. ExternalKey mandatory on every Add; StateKey mandatory where
    //    serializable dynamic state exists. LimitSpec optional. ──
    public ResistorId  AddResistor(NodeId a, NodeId b, double ohms, in ExternalKey key, in LimitSpec limits = default);
    public VSourceId   AddVoltageSource(NodeId pos, NodeId neg, double volts, in ExternalKey key);
    public ISourceId   AddCurrentSource(NodeId from, NodeId to, double amps, in ExternalKey key);
    public CapacitorId AddCapacitor(NodeId a, NodeId b, double farads, in ExternalKey key, in StateKey state);
    public InductorId  AddInductor(NodeId a, NodeId b, double henries, in ExternalKey key, in StateKey state);
    public SwitchId    AddSwitch(NodeId a, NodeId b, bool closed, in ExternalKey key);
    public DiodeId     AddDiode(NodeId anode, NodeId cathode, in DiodeParams p, in ExternalKey key);
    public VSourceId   AddSineSource(NodeId pos, NodeId neg, in SineDrive d, in ExternalKey key, in StateKey state);
                                     // stateful (phase); marks the island AC ⇒ subcycles
    public TransformerId AddIdealTransformer(NodeId aPos, NodeId aNeg, NodeId bPos, NodeId bNeg,
                                             double turnsRatio, in ExternalKey key);
                                     // SAME-MATRIX coupled two-port: the turns-ratio constraint stamps two
                                     // auxiliary rows (solver.md's idealized transformer). The one
                                     // multi-terminal primitive — magnetic coupling is mathematically
                                     // inexpressible as a composition of independent two-terminal R/L/C/V/I
                                     // elements, so without it the devices layer could not build the
                                     // canon-required IdealizedTransformer (§18) or the tablet AC curriculum.
                                     // Leakage/magnetizing storage are ordinary L/R elements the device
                                     // composes around it. SPICE-representable as a VCVS/CCCS pair, so
                                     // transformer lessons stay oracle-able (§22.c).
    public CouplerId   AddCoupler(in CouplerSpec spec, in CouplerPorts ports, in ExternalKey key, in StateKey state);

    public void Remove<TId>(TId id) where TId : struct, IComponentId;  // frees slot NOW; island rebuild at Solve (§17)
    public void RemoveNode(NodeId n);            // must be degree-0 at commit, else Commit throws (whole batch aborts)
    public void RemoveCoupler(CouplerId id);

    // ── Probes ──
    public ProbeId AddProbe(NodeId n, in ExternalKey key);
    public ProbeId AddInterpolatedProbe(NodeId a, NodeId b, double t, in ExternalKey key);  // V = Va + t·(Vb−Va)

    public EditReceipt Commit();   // atomic apply + journal window + islands→Dirty; shape (may alloc)
    public void        Abort();    // explicit rollback; Dispose after Abort is silent
    public void        Dispose();  // == Commit() unless aborted; a half-applied edit cannot be observed
}

// Plain readonly struct — storable in fields (no span members; see §21 span rule).
// Variable-length payloads are drained: island changes via IslandTable.DrainChanges
// or by replaying [JournalFrom..JournalTo].
public readonly struct EditReceipt
{
    public long JournalFrom { get; }        // replay [From..To] to sync a derived layer
    public long JournalTo   { get; }
    public int NodesAdded { get; }
    public int ComponentsAdded { get; }
    public int ComponentsRemoved { get; }
    public int IslandsTouched { get; }
    public int EstimatedRebuildDim { get; } // scheduler hint: does the coming rebuild fit the tick budget?
    public int EstimatedRebuildRows { get; }
    public bool WindowLapped { get; }       // this single commit emitted more events than JournalCapacity:
                                            // [JournalFrom..JournalTo] is unreplayable and the reducer must
                                            // Resync — declared up front, never discovered as silence (§15)
}
```

`StructuralEdit` is a pooled sealed class, not a struct: shape-time only, and
a copyable batch is a footgun. Bulk construction goes through
`ConductorGraph.BeginBulkBuild` (§19), whose staging buffers grow
**geometrically (2×)** from the presize hint — the 10k-segment load-time case
reallocs O(log n) times, never per segment, and the final compaction pass
allocates exact-size arenas.

## 7. Coupling devices

```csharp
public readonly struct CouplerSpec
{
    // Galvanic bridge: Closed = both sides share ONE matrix (incremental merge);
    // Open = separate islands; the opening transition is an island REBUILD
    // (tier 3, the honest cost — solver.md; breakers are safety devices that
    // change rarely). There is no cheap "membership re-partition": canon does
    // not attempt split detection.
    // While Closed, the breaker stamps a closed-switch-conventional series
    // branch (1 mΩ) between its A and B ports inside the merged matrix, so
    // IslandHandle.Exchange(c).PowerA2B reports SIGNED through-flow. That is
    // the hook for vanilla bookkeeping: Stationeers' breaker family is a
    // directional Input→Output transfer device (ElectricalInputOutput —
    // one-way accumulated throughput, directional trip, cartridge reads);
    // manatee's closed breaker is deliberately GALVANIC AND BIDIRECTIONAL
    // (real physics — back-feed exists), and the Re-Volt adaptor derives the
    // vanilla metrics (_transferred as positive-direction energy, trip
    // comparisons, analyzer readouts) from the signed branch flow. The
    // divergence from vanilla (back-feed possible where vanilla forbade it)
    // is a flagged design change requiring Sukasa sign-off (§23).
    public static CouplerSpec Breaker();

    // Power-transfer boundary: ALWAYS separate islands, exchanged amplitude+phase
    // per substep with explicit relaxation α. Modeled leakage/magnetizing storage
    // is honest but not relied on for stability.
    public static CouplerSpec DecouplingTransformer(in TransformerParams p, double relaxationAlpha = 0.5);

    // Stationeers xfmr/charger: behavioral P-transfer. The B port is regulated to
    // `outputVolts`; P_out is measured, the efficiency curve gives η at load fraction
    // P_out/`ratedWatts`, and P_in = P_out/η is drawn from A. The modeled efficiency
    // loss P_out·(1/η−1) is computed INDEPENDENTLY from the curve (not synthesized as
    // In−Out) and dumped as HeatDumpedJ ≥ 0. `dcLinkFarads` is the intended boundary
    // storage; `relaxationAlpha` damps the per-substep exchange. Both sides stay linear.
    public static CouplerSpec ConverterTwoPort(in EfficiencyCurve e, double dcLinkFarads,
        double outputVolts, double ratedWatts, double relaxationAlpha = 0.5);
}

public readonly struct CouplerPorts { public NodeId APos, ANeg, BPos, BNeg; }

// Tier-0 instrumentation: last exchange + the boundary energy ledger.
public readonly struct ExchangeView
{ public double AmplitudeA, PhaseA, AmplitudeB, PhaseB, PowerA2B; }
public readonly struct EnergyLedger
{ public double InJ, OutJ, ModeledLossJ, SurplusJ, HeatDumpedJ; public double Residual; }
```

Islands joined by boundary couplings form **one scheduling unit** and substep
in lockstep (solver.md): the per-substep exchange happens inside the unit,
never across free-running threads, so it is deterministic. Each boundary
carries a running energy ledger; `SurplusJ` is never stored as work — it is
converted to `HeatDumpedJ` (design.md energy rule). Under
`ClientPartitioned`, couplers are the **only** sanctioned cross-partition
join.

**Conservation is physical, not clerical (ruled 2026-07-06, after the
phase-5 review found the ledger-only clamp non-conservative — a
gain-capable two-coupler loop grew a seeded 8.6 J to 85 J).** The ledger
records; it does not license. Two mechanisms make the physics honest, both
instances of design.md's existing debit rule:

- **Transformer boundaries carry an energy-debt feedback**: when cumulative
  delivered-to-B exceeds drawn-from-A minus modeled loss (the one-substep
  lag's over-delivery), the deficit **debits** the B-side feed-forward
  (droop on the injected amplitude) until the boundary balances — the same
  mechanism R18 prescribes for the adaptor ("deliverable = advertised −
  accumulated debt"). The droop must survive not just a single step and a
  gain loop but a **driven** pump — an oscillating B load (a blinking
  switch) — which the phase-5 review showed defeats a naive
  deadband+leak (each half-cycle's over-delivery hides below the deadband
  and the per-substep leak bleeds the debt before it accumulates, so the
  droop never engages and free energy leaks out **linearly, unbounded**,
  ~2.6% of throughput). The hardened droop closes this with three
  scale-invariant pieces: a peak-hold **input-throughput** reference (not
  output — step-up over-delivery inflates the output), a restoring leak
  **gated on a settled-boundary detector** (smoothed |over| ≤ frac·smoothed
  throughput — a sustained oscillation never settles, so it cannot launder
  its debt), and a **wide deadband** so an isolated transient recovers to
  the exact turns ratio while a sustained pump accumulates past it and
  chokes. Guarantee: over any window the **driven** over-delivery is a
  bounded one-time transient (imbalance **slope → 0**; no unbounded ramp),
  not literally one substep — a sustained pump is caught after a bounded
  catch-up, then held choked until the load steadies. (A boundary that has
  been driven into a sustained pump stays choked until the drive stops —
  the conservative direction; it never invents energy.) See
  `ConservationAuditTests.Transformer_sustained_load_oscillation_does_not_pump`.
- **The converter's B port IS the DC-link capacitor** (solver.md said so
  all along: "storage by construction"): A charges it with power bounded by
  the efficiency curve and A's actual delivery; B's voltage is the cap
  voltage and **sags under deficit** — brownout at a starved converter is
  the honest, legible behavior, not a deadlock. The regulated setpoint is
  the charge controller's target, never an ideal source.

**Transformer relaxation α is clamped to ≤ 0.7 at the exchange (ruled
2026-07-06, final-wave fix).** The boundary exchange is an explicit damped
fixed-point iteration; its gain-capable-loop convergence is a NUMERICAL
stability bound, not something the debt droop can enforce (a runaway grows
the droop's own peak-hold reference geometrically in lockstep, so the
deadband inflates with the divergence — measured 1e15–1e73 J transients at
α ≥ 0.8 with high store R before the clamp). `RelaxationAlpha` still
accepts (0, 1]; effective transformer damping is `min(α, 0.7)` — the
highest value the exhaustive stability grid verifies dissipative at every
corner of the declared domain (α × R × turns, gain-capable loops included;
`ConservationAuditTests.Gain_capable_loop_stability_grid_has_no_divergent_corner`).
The converter is unclamped: its charge controller is a provably
contracting loop.

Conservation tests are **windowed physical audits** (source energy =
dissipated + Δstored + coupler heat, summed over both islands from public
readbacks), not `EnergyLedger.Residual` — Residual is a definitional
closure identity (~0 by construction) and can never signal a violation.
The dissipativity gate runs at **gain-capable turns ratios** (loop voltage
gain ≥ 1), where a non-conservative exchange demonstrably rings up.

## 8. SteadyStateGuard and AllocationSentinel (OQ2)

```csharp
// A ref struct: stack-only, non-boxable, cannot be heap-captured or carried
// across await. `using` binds to the public Dispose via the C#8 pattern
// (ns2.1-legal on ref structs). ZERO heap allocation per tick.
public ref struct SteadyStateGuard
{
    // Constructed only by Netlist.EnterSteadyState(). On construct: (a) sets the
    // netlist's no-structural flag; (b) arms the allocation tripwire IF the
    // runtime supports it (see reliability note below).
    public void Dispose();
    // Dispose: restore the flag; debug-assert allocated-bytes delta == 0 and no
    // tier-3/Reconfigure was attempted in-region; then RUN any release-deferred
    // structural ops (see below).
}

public ref struct AllocationSentinel        // standalone tripwire; auto-armed inside the guard
{
    public static AllocationSentinel Arm(); // captures GC.GetAllocatedBytesForCurrentThread()
    public void Dispose();                  // debug: assert delta == 0
    public static bool IsReliable { get; }  // one-time runtime capability probe (below)
}
```

**Guard semantics for tier-3 attempts in-region.** Debug: assert
(fail-fast — this is the "someone rebuilt the world inside CalculateState"
bug). Release: the op is **deferred to the guard's `Dispose`**, executed
there exactly as if called at that point (same handle semantics, §17), logged,
and counted in `TickStats.DeferredStructuralOps`. **Exception (ratified
2026-07-06):** `Edit()` throws `InvalidOperationException` in *all* build
modes — a live `StructuralEdit` scope cannot be coherently deferred; the
deferral machinery models `Reconfigure` (coupler id + state) only. The deferral is therefore
bounded *within the same tick, before `Solve`* — a breaker `Open` attempted
inside the guard still lands this tick; an island is never left energized
across a tick through a breaker the game believes is open. Deferred ops are
never dropped.

**Deferred-op storage (binding).** The guard is a stack-only ref struct and
cannot hold the records; deferred ops live in a **netlist-owned ring
pre-allocated at construction** (capacity is a `NetlistOptions` sizing hint,
default 64). On overflow the buffer **grows** — a one-time allocation counted
in `TickStats.BytesAllocated`, explicitly *outside* the 0B steady-state
claim. This is the honest reconciliation of "never dropped" with "0B": a tick
that defers structural ops has already left steady state (each deferral is a
tier-3 attempt and `DeferredStructuralOps != 0` is itself a bug signal), so
the unconditional guarantee is *never dropped*, and 0B holds on every tick
that contains no deferral. `EnterSteadyState` itself remains 0B.

**Tripwire reliability (binding).** `GC.GetAllocatedBytesForCurrentThread()`
is historically unreliable or inert on Unity/Mono — one of the two shipping
runtimes. Therefore: at first arm, the sentinel runs a one-time capability
probe (allocate a small object, check the counter moved); if inert, the
sentinel disarms process-wide, sets `IsReliable == false`, and logs once.
**The binding zero-alloc enforcement is BenchmarkDotNet MemoryDiagnoser in CI
(CoreCLR), with no exemptions**; the in-process sentinel is best-effort
developer tooling where the API works.

## 9. TickStats and cost queries

```csharp
public struct TickStats   // via Netlist.LastTickStats after every Solve; the solver as a monitorable system
{
    public int Substeps;                // AC substeps executed
    public int RhsSolves;               // tier-1 back-substitutions
    public int Refactorizations;        // tier-2 numeric refactors (converged steady state ⇒ 0)
    public int IslandRebuilds;          // tier-3 rebuilds (steady state ⇒ 0)
    public int MergesApplied;           // incremental merges
    public int NewtonIterations;
    public int AdjustNoOps;             // ε-no-op Adjusts that stayed on cached LU
    public int StaleHandleReads;        // release-mode sentinel reads (should be 0; nonzero = a re-pin bug)
    public int DeferredStructuralOps;   // release guard deferrals (§8; should be 0)
    public long BytesAllocated;         // 0 in steady state (where measurable; §8)
}
```

**Accumulation and thread-safety (binding).** Counters are accumulated
**per scheduling unit** during that unit's `Step` — covered by
single-writer-per-island, never a shared write — and aggregated into
`LastTickStats` in deterministic order (ascending `Islands.Ids`) at a
defined point: `Netlist.Solve` aggregates before returning; clients that
drive `Step` themselves (§22.b) get aggregation at the first `LastTickStats`
read after their barrier. Reading `LastTickStats` while any `Step` of this
netlist is in flight is a phase error (debug assert) — it belongs to the
readback phase. This puts `TickStats` in §21's list of shared structures
with a defined mutation window; without it, the tier-budget CI assertions
below would be reading racily-written fields.

The steady-state perf model becomes executable CI assertions (tier budget):
`Assert.Equal(0, stats.Refactorizations)`, `Assert.Equal(0, stats.IslandRebuilds)`,
`Assert.Equal(0, stats.BytesAllocated)` on the standing "linear AC island in
steady operation lives in tier 1" test. Re-Volt logs `LastTickStats` per tick
for free: `Refactorizations > 0` in steady state means some `Adjust` is not
converging; `IslandRebuilds > 0` means a coupler or cut fired.

**AdjustEpsilon — pinned definition** (oracle-affecting; decides *which*
Adjust refactors, hence cross-platform determinism of the tier-budget
goldens): an `Adjust` is a no-op iff old and new values map to finite,
nonzero, same-sign conductances and the ratio `G_new / G_old` lies within
`[RatioLo, RatioHi]`; conductances at or below gmin (1e-12 S) compare equal;
switch toggles are no-ops only on state equality. `RatioLo`/`RatioHi` are
**stored double constants** — semantically `exp(∓ε)`, pinned as exact bit
patterns at implementation time, never recomputed — so the gate is one IEEE
division (correctly rounded everywhere) plus two comparisons, and
classification is bit-identical on every runtime/arch. **No transcendental
is ever evaluated in the gate**: `Math.Log`/`Math.Exp` are not
correctly-rounded and vary per platform libm, which would let a value
converging near the ε boundary classify as no-op on x64 and refactor on
arm64, diverging the `Refactorizations`/`BytesAllocated` goldens §23.5's
dual-arch CI depends on. Default ε `1e-4` (relative, log-conductance
domain); the default is benchmark-tunable, the *definition* is not. As
built: the default-ε bounds are the correctly-rounded literals of
`exp(∓1e-4)` (`Netlist.cs` `DefaultRatioLo/Hi`); a custom `AdjustEpsilon`
derives its bounds with the deterministic polynomial `1 ∓ ε + ε²/2`
(IEEE ± and × only — agrees with `exp(∓ε)` to within `ε³/6`, and stays
bit-identical across runtimes, which is the property that matters).

`CostOfAdjust` / `CostOfReconfigure` (§4) answer "what would this cost"
without applying — Re-Volt's off-band scheduler uses them plus
`EditReceipt.EstimatedRebuildDim` to decide whether a rebuild fits the
remaining tick budget or goes to the background task.

## 10. Solution and readback

```csharp
public readonly struct Solution   // published snapshot; safe to read while OTHER islands Step (§21)
{
    [CostTier(1)] public double Voltage(NodeId n);                    // 0B; interpolated if compacted away
    public double Potential(NodeId n) => Voltage(n);                  // domain-neutral alias (thermal-RC reuse)
    [CostTier(1)] public double Current<TId>(TId branch) where TId : struct, IComponentId;  // 0B; signed flow
    [CostTier(1)] public double Power<TId>(TId c) where TId : struct, IComponentId;         // 0B
    [CostTier(1)] public double Read(ProbeId p);                      // 0B; interpolated inside compacted runs
    public bool IsLive(IslandId i);        // false ⇒ last-good (Building/Dirty) or de-energized (Faulted)
    public ReadOnlySpan<double> RawVector(IslandId i);  // MNA order (fixed at symbolic time) — the
                                                        // bit-for-bit comparison unit. Valid until that
                                                        // island's next publish AND only safely readable
                                                        // while that island is not concurrently mid-Step
                                                        // (own island, or any island in the readback
                                                        // phase / under the barrier): after a publish the
                                                        // previously-visible buffer is recycled as the
                                                        // next back buffer, so an in-Step foreign span
                                                        // read can observe a mixed-generation vector (§21)
}
```

## 11. Islands

```csharp
public readonly struct IslandTable
{
    public int Count { get; }
    public IslandHandle this[int i] { get; }
    public IslandHandle Of(NodeId n);
    public IslandHandle Of(IslandId id);

    // THE deterministic iteration order: ascending by each island's minimum node
    // ExternalKey. REBUILD-STABLE — decoupled from volatile IslandId values, so
    // replay, golden tests, and CI iterate identically across rebuilds.
    // Validity: until the next structural commit / Reconfigure (§21).
    public ReadOnlySpan<IslandId> Ids { get; }

    public int CollectDirty(Span<IslandHandle> into);   // 0B worklist; one handle per SCHEDULING UNIT
                                                        // (boundary-coupled islands dedupe to their unit lead)
    public int DrainChanges(Span<IslandChange> into, out bool lost);
                                                        // 0B; merges/rebuilds since last drain ⇒ the game
                                                        // client's re-pin trigger (never the journal).
                                                        // Fixed ring sized at shape time. `lost == true`
                                                        // means a burst (or a skipped drain) overflowed
                                                        // the ring: the client is OBLIGATED to a full
                                                        // re-pin — re-resolve every held device handle by
                                                        // ExternalKey — exactly the journal's explicit-
                                                        // overflow discipline (§15), never a silent drop.
                                                        // A too-small span drains partially; call again
                                                        // until it returns 0. (§20 table row.)
}

public struct IslandChange { public IslandChangeKind Kind; public IslandId A, B; }
public enum IslandChangeKind : byte { Created, Merged /*A=survivor,B=absorbed*/, Rebuilt /*A=old,B=new*/, Removed, Faulted, Recovered }

public enum IslandStatus : byte { Empty, Building, Ready, Dirty, Faulted }
// State machine (client-visible invariant):
//   Empty --Edit commit--> Building (bg load) | Dirty
//   Ready --Drive/Adjust/Reconfigure/Edit touching it--> Dirty --Solve ok--> Ready
//   Dirty --Solve fails (singular after gmin / Newton ladder exhausted)--> Faulted
//   Faulted --any tier-2/3 change--> Dirty (retry)
//   Building: background construction; reads return last-good; Step is a no-op.
//   Faulted reads as de-energized. IsLive==false for Building and Faulted.

public readonly struct IslandHandle
{
    public IslandId Id { get; }
    public IslandStatus Status { get; }
    public PartitionKey Partition { get; }  // ClientPartitioned only. An island spanning >1 partition
                                            // through a Closed coupler returns PartitionKey.None (the
                                            // sentinel); per-partition addressing stays on
                                            // ConductorGraph.ReferenceNode(p), which is partition- not
                                            // island-scoped and unaffected by coupler state.

    public void Step(in TickClock clock);   // solve THIS island's scheduling unit; callable from ANY thread.
                                            // Single-writer-per-island, DEBUG-ASSERTED (not locked).
                                            // Step on a non-lead member of a coupled unit debug-asserts.
    public SubstepPlan Plan { get; }        // read-only: SubstepPlan(int Substeps, double SubstepDt,
                                            // double HysteresisBand). Solver owns N: N = ceil(maxFreq ·
                                            // acSamplesPerCycle · tickDt), re-decided only when the required
                                            // rate drifts past ±HysteresisBand (0.15) of the value N was last
                                            // chosen for; SubstepDt = tickDt / N. Band is 0 for DC / non-AC.
    public FaultDiagnostic Fault { get; }   // scalars; drain details via DescribeFault
    public int DescribeFault(Span<ComponentRef> comps, Span<NodeId> nodes);   // 0B; returns counts packed

    public int DrainLimitEvents(Span<LimitEvent> into);   // 0B; post-solve; fixed ring, overflow counted
    public int DrainLimitEvents(Span<LimitEvent> into, out long dropped);   // 0B; `dropped` = events the ring
                                            // shed since the last full drain (R9 degrades legibly, §12);
                                            // cleared once the client catches up (ring emptied)

    // Snapshot/restore (§14)
    public int SnapshotSize { get; }        // stable between TOPOLOGY-CHANGING ops (Edit commit, galvanic
                                            // Reconfigure, resync) — re-read after any IslandChange
    public int StateUnitCount { get; }      // live state units in this island — the aggregate restore-
                                            // coverage denominator (§14): sum(Matched) vs this, after
                                            // ALL blobs are offered, is how many units truly cold-started
    public void Snapshot(IBufferWriter<byte> into);        // 0B core-side; versioned binary
    public RestoreResult Restore(ReadOnlySpan<byte> b);    // by StateKey; misses cold-start + report, never throw

    // Invariants as API: one code path for CI, the resync backstop, and the
    // tablet's educational error messages.
    public InvariantReport CheckInvariants(InvariantChecks which);   // 0B
    public EnergyAudit Energy { get; }                    // running source/dissipated/Δstored ledger
    public ExchangeView Exchange(CouplerId c);            // boundary instrumentation
    public EnergyLedger Ledger(CouplerId c);              // boundary energy ledger
}

public readonly struct FaultDiagnostic { public FaultKind Kind; public ComponentRef Worst; public NodeId WorstNode; public int ComponentCount, NodeCount; }
public enum FaultKind : byte { Singular, ContradictorySources, NewtonDiverged, NonFinite }

[Flags] public enum InvariantChecks : byte { Kcl = 1, Finiteness = 2, Energy = 4, All = 0xFF }
public readonly struct InvariantReport
{
    public double MaxKclResidual; public NodeId WorstKclNode;   // "which node leaks current"
    public bool AllFinite; public int FirstNonFiniteRow;
    public double EnergyResidual;                                // source − dissipated − Δstored
}
public readonly struct EnergyAudit { public double SourceJ, DissipatedJ, StoredJ, BoundaryNetJ, HeatDumpedJ, ResidualJ; }
```

`Building` gives Stationeers its one-tick background-rebuild handoff for
free: the 10k-segment load runs on a background task, islands sit `Building`,
the game runs Re-Volt's scalar fallback (`IsLive == false`) until they flip.
**ClientPartitioned:** a commit that would merge across `PartitionKey`s other
than through a coupler throws `PartitionMergeException` and the whole edit
rolls back atomically — the API enforces "vanilla networks never merge"
instead of trusting the adaptor.

## 12. Limits

```csharp
public readonly struct LimitSpec { public double MaxCurrent, MaxVoltage, MaxPower; public I2tParams Thermal; }
public readonly record struct I2tPair(double RatingAmps, double MeltI2t, double Tau);  // one envelope accumulator (§19)
public struct LimitEvent   // fixed size; attribution to geometry is the reduction layer's job (§19)
{
    public ComponentRef Source; public LimitKind Kind;
    public double Observed, Threshold, I2tFraction, SubstepTime; public long TickIndex;  // replay-diffable
    public int PairIndex;  // ThermalI2t on an envelope-carrying component: WHICH pair tripped (0 otherwise)
}
public enum LimitKind : byte { OverCurrent, OverVoltage, OverPower, ThermalI2t }
```

**Thermal envelopes (Pareto sets, ruled 2026-07-06 — see §19).** A component
may carry 1..k `(rating, melt, tau)` pairs registered via
`Meta.SetThermalEnvelope` (flat SoA storage engine-side; k is small — bounded
by distinct materials in the collapsed chain). The engine then integrates one
melting accumulator PER PAIR against that pair's own rating — over it: heat
`(I²−rating²)·dt`; under it: cool `acc·dt/τ` — and trips when ANY pair
crosses its melt threshold; the event's `PairIndex` names the pair so §19
attribution maps it to the culprit segment. A registered envelope
**supersedes** the component's `LimitSpec.Thermal`; plain-`LimitSpec` clients
never register one and keep the exact single-accumulator path (zero change —
they are the k=1 case). Re-registering with the SAME pair count updates
thresholds in place and preserves the accumulators by index (an ambient
re-derate keeps partial melt, matching raw semantics where the integral
belongs to the segment); a count change resets them. Envelope accumulators
ride the per-island snapshot (an `EnvI2t` unit keyed on the component's
`StateKey`) and the canonical memento, so a part-melted reduced cable
reloads part-melted (§14).

Drained per island into a caller span; fixed-cap ring, overflow counted, not
grown (a melting base needn't emit every event — R9 degrades legibly). The
overflow count is **surfaced** through the `out long dropped` drain overload
(and cleared once the caller has drained the ring empty), so a client that
skipped a drain learns events were lost rather than degrading silently. **The
solver never mutates the circuit on a limit** (R7); popping the fuse is the
client's `Adjust`/`Reconfigure` on the following phase or tick. The i²t
thermal accumulator makes fuses and slow cable overload share one mechanism.
Limits are evaluated **per substep** on a subcycled-AC island (the i²t integral
is a true ∫I²dt over the cycle and the instantaneous checks see the waveform
peak, not the tick boundary — a tick-boundary-only scan is phase-blind and a
fuse whose boundary lands on a sine zero-crossing would never heat); a static
island with no substeps is still evaluated once per tick. **Instantaneous
event classes coalesce to at most one event per (component, kind) per tick,
carrying the worst observed substep value** (ruled 2026-07-06: per-substep
emission floods the ring on any sustained AC overload, drowning the signal
R9 wants legible; the integral classes were already edge-latched). The
evaluation is per-substep either way — only the *emission* coalesces.

## 13. Probes and the oscilloscope tap

```csharp
public sealed class WaveformTap   // per-substep sampling into a CALLER-owned ring; two taps = the UI contract
{
    public static WaveformTap Attach(Netlist n, ProbeId p, WaveformRing ring); // cold subscribe (tier 0)
    public void Detach();
}
public sealed class WaveformRing
{ public WaveformRing(int capacity); public ReadOnlySpan<double> Samples { get; } public int Count { get; } }
```

Interior probes survive series collapse: the reduction layer registers an
interpolator (t = cumulative-resistance fraction) and re-aims it via
`Meta.SetProbeInterpolation` after any re-collapse. `ProbeId` is
**document-stable across rebuilds and merges** (§16): the handle a client
holds (the tablet's `scope`) stays valid; only the probe's *aim* — the
interior interpolation targets — needs reduction-layer repair after a
rebuild, on the same handle. A drift `Resync` KEEPS reduction-owned probes
and re-aims them on their existing handles (as built — strictly stronger
survival than a tear-down/re-create: `ProbeId`s outlive a resync). Only
`FromCanonical` (which re-mints every handle) forces the client to
re-resolve via `TryResolveProbe(key)`; reduction-minted probe keys are
DERIVED deterministically from `(SegmentKey, along, ordinal)` —
`ConductorGraph.ProbeKey`, a splitmix64 mix under a reserved Hi tag; the
ordinal counts earlier co-located probes, 0 for the first — so the key is
topological (§3), two graphs built from the same geometry mint identical
keys, and a client can RE-DERIVE the key to re-resolve after a reload or a
re-driven intake (collision posture: birthday-scale in a 64-bit space,
surfaced by the debug duplicate-key throw at `AddProbe`, never silent).
Sampling costs one struct store per substep, 0B,
bounded by the two-probe contract (phase comparisons need a pair).
`WaveformTap.Attach` is a **cold, call-once subscribe** — it allocates the
tap object and must live in setup, never in the tick loop (§22.b).

## 14. Snapshot, restore, serialization laws

Per-island **snapshot** payload (`SnapshotVersion` = 3), keyed by `StateKey`,
versioned binary (JSON debug form lives in `Manatee.Core.Diagnostics`, zero-cost
in release): capacitor voltages, inductor currents, device `Tick` state, source
phase — **and the evolving state a per-island snapshot used to reset**:
(a) each limits-bearing component's i²t melting integral + trip latch, keyed on
the component's `ExternalKey`-derived `StateKey` (`StateKey.From(key)`), plus the
global ambient i²t threshold scale (v2); (b) each boundary coupler's persistent
`CouplerRuntime` scalars (DC-link cap voltage, debt-droop / energy-ledger /
relaxation integrators), keyed on the coupler's **client-passed `StateKey`** —
`== StateKey.From(couplerKey)` unless the client chose otherwise — and emitted by
the coupler's **A-side island** (the documented anchor) (v2); and (c) each
registered thermal ENVELOPE's per-pair melting integrals + trip latches (§12/§19
Pareto sets), keyed on the component's `StateKey`, matched positionally only when
the live pair count agrees (v3) — so a part-melted REDUCED cable reloads
part-melted. These ride as raw kind bytes (6 = i²t, 7 = coupler-runtime,
8 = envelope-i²t) so the built-in `StateUnitKind` enum (`State/StateUnit.cs`) is
untouched — a versioned-binary bump, not a wire contract; restore gates strictly
on the version byte, so pre-bump blobs are rejected loudly, never misread. **Law
4 below therefore extends** to converter/transformer-coupled islands, mid-melt
fuses, and mid-melt reduced cables, not only `StateKey`-keyed RLC islands: solve
→ snapshot → restore → step is bit-for-bit for those too. The whole-netlist
**`SaveCanonical`** memento (v3) remains the slot-preserving form that carries
the same evolving state — plus, as of v3, the coupler's client `StateKey`
(previously discarded at build) and each component's envelope pairs +
accumulators — so a partway-melted fuse and a settled converter resume without a
cold-start transient. `SaveNormalized` includes envelope PAIRS (configuration —
they supersede `LimitSpec.Thermal`, §12) but not accumulators, so R11's drift
equality sees envelope drift while staying blind to evolved state, as intended.

```csharp
public readonly struct RestoreResult    // plain struct; orphans drained, not stored as spans
{
    public int Matched;            // state units restored by THIS blob
    public int Untouched;          // units in the island THIS blob had no entry for — left as-is, never reset
    public int OrphansInBlob;      // blob entries with no live unit (world edited between save and load)
    public bool Ok => OrphansInBlob == 0;
    public int DrainOrphans(Span<StateKey> into);   // 0B; which blob entries went unmatched
}
```

**Restore is additive (binding).** `Restore` overwrites exactly the units
whose `StateKey` matches an entry in the given blob and **leaves every other
unit untouched** — it never resets a unit it carries no data for. Units come
into existence at cold defaults when built; "cold-started" is a *derived*
aggregate fact ("no blob ever matched this unit"), not something a Restore
call does. This is what makes restore composable when topology drifted
between save and load — `StateKey` identity is netlist-global while blobs
are per-island, so both mismatch directions must work:

- **Merged since save** (two saved islands are now one): offer each blob to
  the merged island in turn; each restores its own keys and earlier restores
  are not clobbered. (Destructive cold-start-the-unmatched semantics would
  make the last blob win and silently zero all earlier blobs' state.)
- **Split since save** (one saved island is now several): offer the *same*
  blob to every resulting island; each takes its matching keys and reports
  the rest as `OrphansInBlob` — expected in this pattern, not an error.

Coverage is therefore checked **in aggregate, never per call**: after all
blobs are offered, `sum(Matched)` against `IslandHandle.StateUnitCount`
(§11) is the number of genuinely cold-started units; the §22.a load loop
does exactly this and logs the residue instead of discarding results.

Two whole-netlist serializations: **`SaveCanonical`** preserves slots so a
recorded command log or snapshot replays against it; **`SaveNormalized`**
renumbers to an ExternalKey-sorted minimal form so two build paths that made
the same circuit compare byte-equal.

**Executable laws (standing CI property tests):**

1. `SaveCanonical(FromCanonical(x)) == x` byte-equal.
2. `SaveNormalized` is a fixpoint.
3. Any edit sequence and its from-scratch rebuild agree under
   `SaveNormalized` — **this is R11's drift detector stated as an equality**.
4. Solve → `Snapshot` → `Restore` → `Step` matches a never-snapshotted run
   **bit-for-bit** on `Solution.RawVector` (scoped to one
   runtime/arch/build — the `EnvFingerprint` domain; cross-platform runs use
   oracle tolerances).
5. Drift backstop, two-level: `Fingerprint(island, Structural)` every N
   ticks; on mismatch only, full `SaveNormalized` diff → typed `DriftReport`
   → `Resync` (§19).

## 15. TopologyJournal

```csharp
// Fixed-capacity ring of blittable structs, monotonic Seq, multiple independent
// read cursors. Derived layers (reduction, VS renderer, debug tooling) stay in
// sync by REPLAY, never callbacks. A lagging reader detects its OWN overflow and
// falls back to full resync — explicit, never silent. Serves the COMPACTION
// layer; game clients never babysit cursors (rejected list) — their re-pin
// trigger is IslandTable.DrainChanges / EditReceipt.
public sealed class TopologyJournal
{
    public JournalCursor OpenCursor();                                // starts at head
    public JournalCursor OpenCursorAt(long seq);                      // 0B; position at a historical seq —
                                                                      // THE way to replay an EditReceipt
                                                                      // window (a head cursor opened after
                                                                      // the commit would read nothing). If
                                                                      // seq has been lapped, the first
                                                                      // TryRead returns false and
                                                                      // Overflowed(c) is true ⇒ resync.
    public bool TryRead(ref JournalCursor c, out TopologyEvent e);    // 0B; false ⇒ caught up
    public bool TryReadRange(ref JournalCursor c, long toSeq, out TopologyEvent e);  // EditReceipt windows
    public bool Overflowed(in JournalCursor c);                       // lapped ⇒ MUST resync
    public long Head { get; }
}

public enum TopologyEventKind : byte
{
    ComponentAdded, ComponentRemoved, NodeAdded, NodeRemoved, ProbeAdded, ProbeRemoved,
    IslandCreated,
    IslandsMerged,   // (A=survivor, B=absorbed). Component/node handles SURVIVE; absorbed IslandId is stale.
    IslandRebuilt,   // (A=old, B=new). Removal-, coupler-Open-, or resync-triggered; ALL internal
                     // handles rooted in A are invalidated (couplers and probes excepted —
                     // document-stable, §16). A coupler Open that splits an island
                     // emits ONE IslandRebuilt per resulting island. There is NO 3-id split event,
                     // by canon: split detection is not attempted (solver.md; R11).
    IslandRemoved, IslandFaulted, IslandRecovered
}
public readonly struct TopologyEvent   // fixed size, no strings; two island-id fields exactly
{
    public long Seq { get; }
    public TopologyEventKind Kind { get; }
    public ComponentRef Component { get; }
    public ExternalKey Key { get; }        // client-meaningful identity on every entry
    public IslandId IslandA { get; }       // survivor / old / subject
    public IslandId IslandB { get; }       // absorbed / new
}
```

Bulk builds (`BeginBulkBuild`) emit one `IslandCreated` summary per island,
not 10k adds. The common pattern — "sync after my own commit" — is
`OpenCursorAt(receipt.JournalFrom)` + `TryReadRange(.., receipt.JournalTo)`
and needs no standing cursor. Two overflow cases, both explicit:

- **Lagged reader:** a standing cursor (or an `OpenCursorAt` on an old seq)
  that has been lapped reports `Overflowed()` ⇒ full resync.
- **Single oversized commit:** a non-bulk `Edit` may emit more per-component
  events than `JournalCapacity` (a large chunk load outside `BeginBulkBuild`);
  its window is lapped the instant the commit returns. The receipt says so up
  front — `EditReceipt.WindowLapped` (§6) — and the reducer resyncs
  deterministically instead of silently missing events.

**Event-stream completeness (binding).** Every `IslandId` that ceases to
exist appears as the subject of exactly one terminal event — `IslandsMerged`
(absorbed), `IslandRebuilt` (old), or `IslandRemoved` — in both the journal
and `DrainChanges`, even when the underlying matrix work was coalesced away
(§17 rule 3). Coalescing is a matrix-work optimization; it never edits
history.

## 16. Handle-survival table (OQ1)

Binding rules:

- **Internal ids do NOT survive a rebuild.** "Rebuild" = from-scratch
  reconstruction of an island, triggered by any removal, by a galvanic
  coupler `Open`, or by a journal-overflow/drift resync. A rebuild reissues
  the generation of **every island-rooted handle** — component and node
  slots (the implementation walks the island's member slots — merges
  interleave slots, so this is per-member gen reissue, not a
  contiguous-range bump). Couplers and probes are *not* island-rooted; see
  below.
- **Couplers and probes are document-level registrations, not island-rooted
  slots.** A `CouplerId` or `ProbeId` is invalidated only by removing that
  coupler/probe (or by whole-netlist reload / drift resync for
  reduction-owned probes) — a rebuild or merge re-wires their internal
  plumbing (port rows, interpolation targets) *without* reissuing the
  client-held handle. Rationale (adversarial review, 2026-07-06): a boundary
  coupler straddles two islands, so island-rooted identity was never
  coherent for it — and worse, the canonical tick order runs the topology
  phase *before* the re-pin phase, so a breaker whose island was rebuilt by
  a sibling cut last tick would be `Reconfigure`d through a stale handle:
  in release that is a defined no-op, i.e. a dropped breaker `Open` and an
  island left energized — the exact §8 violation. Document-stable coupler
  identity removes the hazard structurally: a cached breaker handle is
  *always* safe to `Reconfigure`, in any phase order.
- **Island scoping is first-class:** a rebuild invalidates handles in *that
  island only*. Handles into every other island are untouched. "Did my
  handle survive?" has one answer per island, checkable by one gen compare.
- **Merge (coupler `Close`, incremental splice) renumbers nothing** except
  the absorbed `IslandId`. If merges invalidated component handles,
  "incremental" would be a lie.
- **Timing:** the removed component's own slot is freed at commit (its handle
  dies immediately); the island-wide generation reissue for *surviving
  siblings* happens **at the next `Solve`, when the rebuild actually runs**.
  Until then, surviving handles remain fully usable for tier-1/2
  `Drive`/`Adjust` — and those writes are *not* lost, because verbs write the
  document and the rebuild restamps from it (§17).
- **`ExternalKey`/`StateKey` survive everything** and are the sole
  re-resolution path (`TryResolve*` returns `false` on a miss, never throws).

| Handle / key | `Drive` [T1] | `Adjust` [T2] | `Reconfigure Close` (merge) | `Reconfigure Open` (split → rebuild) | removal → island rebuild | snapshot / restore | journal-overflow / drift resync |
| --- | --- | --- | --- | --- | --- | --- | --- |
| `NodeId` (in the affected island) | survives | survives | **survives** | **invalidated** at Solve → re-resolve `TryResolveNode(key)` | **invalidated** at Solve; re-resolve by key | not serialized; re-resolve by key after restore | **invalidated**; re-resolve by key |
| component ids (`ResistorId`…`DiodeId`) | survives | survives | **survives** | **invalidated** at Solve; re-resolve `TryResolve(key)` | **invalidated** at Solve (**the removed one dies at commit**) | re-resolve by key | **invalidated**; re-resolve by key |
| same handles, in an **unaffected island** | survives | survives | survives | survives | survives | survives | survives |
| `CouplerId` | survives | survives | survives (subject of the op) | survives (subject of the op) | **survives** (document-level; only `RemoveCoupler` kills it) | re-resolve `TryResolveCoupler(key)` after `FromCanonical` | **survives** (resync never touches client-added couplers) |
| `ProbeId` | survives | survives | survives | **survives**; reduction re-aims interpolation on the same handle | **survives**; re-aimed on the same handle | re-resolve `TryResolveProbe(key)` after `FromCanonical` | **survives**; reduction re-aims interpolation on the same handle (only a whole-netlist `FromCanonical` reissues probe slots) |
| `IslandId` | survives | survives | absorbed → **invalid**; survivor survives | each resulting island gets a **new** id; old **invalid** (`IslandRebuilt` per result) | **new** id; old **invalid** | islands re-derived on load | **invalidated** |
| `ExternalKey` | survives | survives | survives | **survives** — the re-resolution key | **survives** | survives | **survives** — the diff/adopt key |
| `StateKey` | survives | survives | survives | survives (state re-homed by key) | survives | **survives** — the restore match key | survives |

Detection is uniform: a stale internal handle throws `StaleHandleException`
in debug (naming kind, slot, expected vs actual generation, and the journal
event that invalidated it); in release it degrades to a defined sentinel
(reads return 0.0, mutations no-op, `TryResolve` returns false) plus
`TickStats.StaleHandleReads` — a shipped game never crashes on a stale read.
The game client's standing re-pin trigger is `IslandTable.DrainChanges` (and
`EditReceipt` for its own edits); only the reduction layer reads the journal.

## 17. The Reconfigure-to-Solve story (OQ3)

One reconciled story: **structure is transactional and immediate; matrices
are coalesced and lazy.** P1's coalescing and P2's transactional commit
govern different layers, so both hold.

1. **Commit is atomic (document).** `Edit()` stages; `Dispose`/`Commit`
   applies all-or-nothing. Each `Reconfigure` and reduction `RemoveSegment`
   applies its membership/geometry change immediately. At every observable
   instant the netlist is a consistent circuit. Immediately after commit —
   before any Solve — `TryResolve`, `IslandOf`, `Islands.Ids`, node degree,
   `Fingerprint`, and the journal window all reflect the committed topology.
   Added handles are valid immediately; the removed component's slot is
   freed immediately. Within one commit, **removals apply before
   additions**, so a single batch may retire an entry and re-add its
   `ExternalKey` on new endpoints — the breaker-move case:
   `RemoveCoupler` + `AddCoupler` with the same key in one batch, after
   which the key resolves to the new handle (decision log #28). Journal
   replayers correspondingly see `Removed(K)` before `Added(K)` within a
   commit window, never a transient duplicate key.

2. **Matrices are coalesced.** Commit does not refactor. Affected islands go
   `Dirty`; the symbolic+numeric rebuild runs at the next `Solve`, **once
   per island per tick** no matter how many removals/opens accumulated (a
   deconstruction burst = one rebuild). `Reconfigure` and `RemoveSegment`
   themselves allocate nothing; the rebuild allocates, and
   `TickStats.IslandRebuilds` counts it.

3. **Rebuild supersedes merge.** If an island is incrementally merged and
   also scheduled for rebuild in the same tick, only the rebuild runs; the
   merge's incremental matrix work is discarded. Intra-tick op order never
   changes the committed topology — only how much matrix work is skipped.
   **Coalescing discards matrix work only, never events:** the absorbed
   island's `IslandsMerged` (and every other membership event of the tick)
   is still emitted to the journal and `DrainChanges` — no `IslandId` is
   ever retired without a terminal event (§15 completeness invariant).

4. **What reads observe between mutation and Solve:**
   - `Solution.*` returns the last **published** solve: Building/Dirty
     islands report `IsLive == false` and hold last-good values; a
     **Faulted** island reports `IsLive == false` and reads **de-energized
     0** (§10/§20 — enforced at the read path, and scoped to the Faulted
     *status*: the fault's numbers were never published, and the
     internally-held pre-fault vector becomes readable again as ordinary
     last-good once a repairing change or a merge flips the island back to
     Dirty).
     Last-good explicitly covers the **absorbed side of a merge** (fixed
     2026-07-07 after the integration tutorial exposed a violation): nodes
     relabeled into the survivor keep reading their pre-merge last-good
     potentials — and voltage sources their branch currents / powers (aux
     flows) — until the merged island first publishes. This holds on BOTH
     union orientations, including a previously-Faulted side: the merge is
     the tier-2/3 change that flips the union to Dirty, so a Faulted
     side's reads revert from de-energized 0 to its last successfully
     published values (0 if it never published). No fault output can be
     laundered this way — failed solves never publish.
   - Structure queries reflect committed topology (rule 1).
   - **Surviving handles in a to-be-rebuilt island stay usable for tier-1/2
     `Drive`/`Adjust` this tick.** These are *document* writes: the pending
     rebuild restamps from the document, so nothing written in this window
     is discarded. (This closes the "doomed-but-usable window" objection:
     the window exists, but nothing in it can be lost.)
   - Reads *through the removed component's own handle* are stale (§16).
   - Limit events are produced only by `Solve`.

5. **At Solve:** each Dirty island rebuilds or refactors; on rebuild, the
   island's internal handles go stale (per-member generation reissue) and
   the journal emits `IslandRebuilt(old, new)` per resulting island. Clients
   re-resolve by key after observing `DrainChanges`.

The sentence a client remembers: *after a mutation the **shape** is already
true; the **numbers** are last-tick's until you Solve; Solve pays at most one
rebuild per island; and anything you `Drive`/`Adjust` meanwhile is written to
the document, not the doomed matrix.*

Canonical tick order (Re-Volt shape): **topology phase** (drain game events →
`Reconfigure`/`RemoveSegment`) → **re-pin phase** (`DrainChanges`; retire or
re-resolve affected devices — §18) → **drive phase** (`EnterSteadyState` +
device fan-out, tier ≤2) → **`Solve`** → **readback phase** (apply actuals,
drain limits, telemetry). No handle is ever invalidated *underneath* the
running device loop.

## 18. Devices-layer contract

```csharp
namespace Manatee.Core.Devices;

public readonly struct TerminalSpec
{
    public int Count { get; }
    public ReadOnlySpan<int> ReturnTerminals { get; }   // indices the WiringPolicy treats as returns (§5);
}                                                       // backed by a static per-device-type array (cold)

public abstract class Device
{
    public abstract TerminalSpec Terminals { get; }

    // Build once, at tier-3 time. `baseKey` is the device's client-assigned
    // ExternalKey; `state` its StateKey. KEY-ALLOCATION CONTRACT (binding):
    // the device mints its component keys as baseKey.Derive(ordinal), where the
    // ordinal is a stable, documented constant per component role of the device
    // class (Battery: 0=EMF source, 1=Rint, ...). Deterministic across rebuilds
    // by construction — this is what makes restore-by-key and drift diffs work
    // for multi-primitive devices.
    protected abstract void Build(StructuralEdit e, ReadOnlySpan<NodeId> terminals,
                                  in ExternalKey baseKey, in StateKey state);
    public virtual void RemoveComponents(StructuralEdit e) { }        // tear-down (tier 3)

    // Per-tick parameter updates, capped at tiers 1–2 BY TYPE: a device author —
    // including a future Re-Volt contributor converting a legacy device — cannot
    // write a structural change into the hot loop; it does not compile.
    public virtual void Tick(in DeviceTickContext ctx) { }

    // R6 device state, one unit under the device's StateKey (one memcpy each).
    public virtual int  StateSize => 0;
    public virtual void SaveState(Span<byte> dst) { }
    public virtual void RestoreState(ReadOnlySpan<byte> src) { }
}

public readonly ref struct DeviceTickContext   // ref struct: 0B, cannot escape the tick
{
    public Solution Previous { get; }   // last published values. DETERMINISTIC (= previous tick) only for
                                        // the device's OWN island — its unit's drive phase precedes its
                                        // unit's Step. Cross-island reads are scheduling-dependent under
                                        // a parallel schedule (§21): couple the islands (lockstep unit)
                                        // or read in the readback phase for deterministic foreign values.
    public double Dt { get; }
    // Mirrors the FULL Netlist tier-1/2 verb set (§4): a device owning a
    // capacitor, inductor, diode, or transformer must never need to reach
    // outside its capability object — that would defeat "cannot exceed its
    // tier by type".
    [CostTier(1)] public void Drive(VSourceId id, double volts);
    [CostTier(1)] public void Drive(ISourceId id, double amps);
    [CostTier(1)] public void Drive(VSourceId id, in SineDrive d);
    [CostTier(2)] public void Adjust(ResistorId id, double ohms);
    [CostTier(2)] public void Adjust(SwitchId id, bool closed);
    [CostTier(2)] public void Adjust(CapacitorId id, double farads);
    [CostTier(2)] public void Adjust(InductorId id, double henries);
    [CostTier(2)] public void Adjust(DiodeId id, in DiodeParams p);
    [CostTier(2)] public void Adjust(TransformerId id, double turnsRatio);
}
```

**Adaptor re-pin contract (first-class, not example lore).** A game-facing
adaptor holds device handles across ticks. After the topology phase it must
consume `IslandTable.DrainChanges` (or its own `EditReceipt`) and, for every
`Rebuilt` island: (a) **retire** devices whose primitives were removed this
tick (their handles died at commit — do not `Tick` them); (b) **re-resolve**
the surviving devices' handles by `ExternalKey` (`TryResolve`, or
re-`Build` + `Restore` by `StateKey` where the device was torn down). The
release-mode stale-handle sentinel (§16) makes a missed re-pin a counted
no-op, not a crash — but `TickStats.StaleHandleReads != 0` is a bug.
Couplers and probes never need re-pinning after rebuilds (document-stable,
§16); the contract covers device component/node handles only. If
`DrainChanges` reports `lost == true`, the obligation escalates to a **full
re-pin**: re-resolve every held device handle by `ExternalKey` (§11).

Built-in models: `AdaptedLoad` (R18: G = P/V_prev² clamped, brownout with
hysteresis + staggered rejoin + recloser lockout, across-tick energy-debt
ledger — the ledger settlement is exact under a **fixed timestep**, the only mode
the codebase drives), `AdaptedSource` (V source + internal series R +
advertised-power across-tick current clamp; **stateless** — `StateSize=0`,
registers no unit — its clamp reads the previous solution's load resistance and
targets the exact EMF, converging in one step for resistive loads), `Battery`
(EMF + Rint + SoC integrator, per-chemistry parameters), `Alternator` (swing-lite
rotor state → `Drive(SineDrive)`), `IdealizedTransformer` (same-matrix two-port
built on `AddIdealTransformer` §6 plus ordinary leakage/magnetizing elements —
*not* a coupler), `DecouplingTransformer`/`ConverterTwoPort` (couplers, §7).

**`DeviceHost`** is the built-in serial driver: `DeviceHost(Netlist)` builds
devices over a public `Netlist` and drives them. `Add<T>(T device,
ReadOnlySpan<NodeId> terminals, in ExternalKey baseKey, in StateKey state)` opens
ONE `Edit` → `Build` → (when `StateSize>0`) `RegisterDeviceState` anchored on
terminal 0, and enlists the device; `Tick(double dt)` ticks every enlisted device
once in `Add` order through a tier-≤2 `DeviceTickContext`. It holds no internal
handle (pure public-API consumer). **It does NOT implement the re-pin contract
above** — a device caches its component AND terminal-node handles at `Build`, and
`DeviceHost` cannot re-resolve the *caller-owned* terminal nodes from the device's
own key — so it is complete only for STATIC-topology device islands (what the
built-in models' tests exercise). After a co-island rebuild it keeps ticking
through stale handles: a counted no-op (`StaleHandleReads`>0), never a crash. A
dynamic-topology integration owns the re-pin loop itself (§22), and its
devices must be re-pinnable: wrap the load in a key-re-resolving adaptor
(re-resolve component + terminal handles by `ExternalKey` after each rebuild
— the §22.a `RevoltAdaptor` pattern) rather than a built-in model whose
handles are private. Open item: give `AdaptedLoad` (and siblings) a public
re-resolve-by-key surface if graph-attached built-ins on churning islands
are ever wanted (§23).

## 19. Reduction-layer intake contract

```csharp
namespace Manatee.Core.Reduction;

// Geometry in, minimal netlist + maps out (R10/R11). Constructed over a public
// Netlist; holds NO internal handle; uses ONLY public Core API (§2 invariant).
public sealed class ConductorGraph
{
    public ConductorGraph(Netlist target, in GraphOptions opts);   // PrePartitioned (SN) | SelfPartitioned (VS)

    public BulkBuild BeginBulkBuild(int expectedSegments = 0);     // load scope: ONE compaction pass at Dispose;
                                                                   // staging grows geometrically from the hint;
                                                                   // islands sit Building
    public void AddSegment(in SegmentKey k, in JunctionKey a, in JunctionKey b, in ConductorSpec spec,
                           PartitionKey partition = default);
                                                                   // eager: recompacts the shadow, then DIFFS the result
                                                                   // against the realized netlist so unchanged nodes/chains
                                                                   // emit NO edits (handles survive; a redundant pass is a
                                                                   // no-op). Cost today is O(graph) per non-bulk edit — the
                                                                   // localized fast path is scheduled perf work against the
                                                                   // 10k-segment benchmark, not a semantic difference.
                                                                   // ClientPartitioned: `partition` is MANDATORY (default
                                                                   // throws at add time) — every junction the segment
                                                                   // touches is partition-tagged, a junction claimed by
                                                                   // two partitions throws PartitionMergeException, and
                                                                   // the partition's reference rail is created on first
                                                                   // use (ReferenceBound wiring) so ReferenceNode(p)
                                                                   // always resolves. The bulk path uses this same
                                                                   // signature; its partition/merge checks run at
                                                                   // BulkBuild Dispose (load time), never first Solve.
                                                                   // SelfPartitioned: `partition` is ignored.
    public void RemoveSegment(in SegmentKey k);                    // dirties island; rebuild coalesced to next Solve
    public NodeId PortNode(in JunctionKey j);                      // where devices attach post-compaction (key-resolved)
    public NodeId ReferenceNode(PartitionKey p);                   // the network's reference rail (Stationeers datum)
    public ProbeId AddProbe(in SegmentKey k, double along);        // survives collapse via cumulative-R interpolation
    public static ExternalKey ProbeKey(in SegmentKey k, double along, int ordinal = 0);
                                                                   // the probe's DERIVED topological identity (§13):
                                                                   // client-re-derivable, call-order-independent
    public void SetAmbient(in SegmentKey k, double kelvin);        // [T0] envelope recompute ONLY — never a matrix change
    public bool Attribute(in LimitEvent e, out AttributionResult a); // equivalent event → which segment melts/pops (R7); 0B

    // Journal consumption — the reducer's sole sync path: replay its own
    // EditReceipt window after each commit; a standing cursor + Overflowed()
    // drives the resync fallback when it lags foreign edits.
    public void SyncFromReceipt(in EditReceipt r);

    // Drift backstop (R11), two-level: Fingerprint every N ticks; canonical diff
    // only on mismatch; Resync = snapshot → rebuild from shadow → restore by key.
    public DriftReport Diff(IslandId island, IGeometrySource truth);     // cold
    public ResyncReport Resync(IslandId island, IGeometrySource truth);  // shape
}

public readonly struct ConductorSpec { public double OhmsPerLength, Length; public LimitSpec Limits; }
public readonly struct AttributionResult { public SegmentKey Segment; public LimitKind Kind; public double Margin; }

public readonly struct DriftReport   // entries name ExternalKeys: a bug report with coordinates, never mystery corruption
{
    public bool IsEmpty { get; }
    public int Count { get; }
    public int Drain(Span<DriftEntry> into);
}
public readonly struct DriftEntry { public DriftKind Kind; public ExternalKey Key; public double Live, Shadow; }
public enum DriftKind : byte { MissingInLive, MissingInShadow, ValueMismatch, EnvelopeMismatch, ProbeWeightMismatch, PartitionMismatch }
```

Segments become resistor edges (resistance = type × length); junctions become
nodes; series chains with no taps collapse to one equivalent resistor
carrying the **per-limit-type envelope** — the minimum over constituents,
where ampacity, i²t mass, and melting threshold may each pick a *different*
segment (the lead-fuse-in-a-copper-run case). Attribution is current density
at the narrowest cross-section, per limit type, environment-adjusted.
Ambient changes dirty the envelope only (`SetAmbient` → `Meta.SetLimits`).
Reduction is *semantically invisible*: raw and collapsed graphs produce
identical terminal behavior, and probes agree with the raw interior — both
standing equivalence tests (testing-strategy.md), now expressible as
`SaveNormalized` equalities (§14).

**i²t envelopes are Pareto *sets*, not hybrid pairs (ruled 2026-07-06).**
The phase-7 review proved the single-pair envelope (rating = min ampacity
from segment X, melt threshold = min from segment Y) fires trips the raw
graph never would — a fictional hybrid component, violating both semantic
invisibility and the hazards-trace-to-player-mistakes rule (a false-positive
fire indicts nobody). Resolution: a series chain carries one shared current,
so per-segment i²t accumulation is *exactly* raw behavior. The collapsed
element's thermal envelope is therefore the **Pareto-minimal set of
(rating, meltI2t) pairs** over its constituents (pair s is dominated when
another pair trips no later at every current: rating ≤ and melt ≤); the
limit engine accumulates one integral per surviving pair and trips when any
pair trips; attribution names the tripping pair's segment. The set is
bounded by the chain's distinct materials in practice, and **limit-event
equivalence raw-vs-reduced becomes a standing test** (it was previously
unstatable). Instantaneous classes keep the plain minimum — they were
always exact.

*Implementation contract (built 2026-07-06).* Dominance includes the
cooling constant: pair *d* retires pair *s* only when `rating_d ≤ rating_s`
AND `melt_d ≤ melt_s` AND *d* cools no faster than *s* — a slower-cooling
pair can out-trip a nominally weaker one under pulsed load, so tau
participates in the frontier. Tau compares by the ENGINE's cooling
semantics, not numerically: `tau ≤ 0` means the accumulator never cools
(§12), i.e. the SLOWEST cooling, ranked as +∞ — ranking it as the numeric
minimum let a cooling fuse retire a never-cooling segment that out-ratchets
it under pulsed load (a raw melt the reduced side would never report; the
tau≤0 regression test pins this). Identical `(rating, melt, tau)` triples collapse to one pair
whose culprit is the min segment key; the surviving pairs order by culprit
segment key, so unchanged membership keeps stable pair indices across
recomputes. The reduction layer registers the set through
`Meta.SetThermalEnvelope` (the equivalent's own `LimitSpec.Thermal` stays
default — the envelope supersedes it, §12); an ambient re-derate with
unchanged membership re-registers in place and PRESERVES the per-pair
melting integrals; a membership change clears first (integrals reset — a
topology-grade envelope change, not a tier-0 re-derate). `Attribute` routes
a `ThermalI2t` event by its `PairIndex` straight to the pair's segment — no
narrowest-threshold scan, no hybrid margin. The standing equivalence test
(LimitEventEquivalenceTests) drives raw and reduced builds of seeded
mixed-material chains through one current script and asserts: no fiction
(every reduced thermal event is a raw event — same tick, segment,
threshold), no delay (first-trip ticks equal; the frontier always contains
the earliest-tripping raw segment), and OverCurrent parity. Post-first-trip
streams are NOT compared: the client acts on the first trip, and a
dominated segment's later raw trip is exactly what the Pareto pruning
declares unobservable before then.

**The shadow is deliberately NOT serialized (ruled 2026-07-06).**
`ConductorGraph` has no save/load: geometry is the CLIENT's truth, re-driven
into a fresh graph at load — matching stationeers.md's rebuild-every-load
model and compaction.md's from-scratch-is-cheap invariant. What makes the
re-driven intake converge to the same observable state is that every
reduction-owned identity is DERIVED from client geometry: equivalent
resistors key on the chain's min segment key, region nodes on their
junction keys, and probes on `ProbeKey(segment, along, ordinal)` (§13) — so
`TryResolve*`/`TryResolveProbe` re-bind client handles after a re-drive with
no reduction-layer persistence at all. (Evolved SOLVER state — melting
integrals, coupler runtimes, storage state — persists through the
netlist-side snapshot/canonical channels of §14, keyed by `StateKey`, which
is why those keys are geometry-derived too.)

Intake per client: VS voxels → greedy-meshed prisms → union-find regions
(three block representations, one intake — vintage-story.md); Stationeers
`NetworkExport` adjacency (segments + ports + fuses-as-rated-edges); tablet
wires pass through (ideal node per Falstad convention).

**Stationeers intake normalization (required pre-step, not yet built).**
The raw export is *not* segment-endpoint shaped: `NetworkExport.CableInfo`
carries only `{ RefId, MaxVoltage, List<int> Connections }` (cable→cable
adjacency), `DeviceInfo` only `{ RefId, StructureTypeName }`, and the export
command itself is currently a stub. Before any `AddSegment`, Re-Volt's
intake normalizer must: (a) recover **junctions** from per-cable OpenEnds /
shared grid cells (before the network builder discards them) and rewrite
cable adjacency as `(JunctionKey a, JunctionKey b)` edges; (b) resolve each
**device to the junction of its power port**; (c) express **fuses as rated
edges** in the adjacency. These plus per-cable prefab/gauge are the export
additions already agreed with Sukasa (stationeers.md) — they and the
normalizer must exist before the 10k-segment intake benchmark is
meaningful. Walkthrough §22.a shows the **post-normalization** shape
(`c.A`/`c.B`/`c.PrefabName`/`d.Port` are normalizer outputs, not raw export
fields).

## 20. Error model

Three channels, never blended:

1. **Exceptions — programmer/contract errors** (fail fast, indict client code).
2. **`Faulted` island state — legal-but-degenerate circuits** (R9: data, not
   exceptions; players build these as a hobby and the game must not crash).
3. **Polled post-solve events — normal runtime signals** (fixed rings,
   overflow counters; no callbacks).

| Condition | Channel | Surfaced as | Client contract |
| --- | --- | --- | --- |
| Cross-kind handle misuse | compile time | does not compile (typed handles) | — |
| Stale / wrapped / cross-netlist handle | 1 | `StaleHandleException` (debug); defined sentinel + `TickStats.StaleHandleReads` (release) | re-resolve by `ExternalKey` |
| Key not found | — | `TryResolve*` returns `false` | client decides (cold-start, skip) |
| Nested `Edit()`; T3/Reconfigure inside guard (debug); missing mandatory key | 1 | `InvalidOperationException` / assert | fix the call site |
| `RemoveNode` non-degree-0; cross-partition merge (`PartitionMergeException`) | 1 | **throws from `Commit()` — whole batch aborts atomically** | fix the edit; nothing was applied |
| Single-writer-per-island violation | 1 | debug assert (never a lock) | fix the scheduler |
| Singular after gmin / contradictory sources / Newton ladder exhausted | 2 | island → `Faulted` + `FaultDiagnostic` (named nodes/components); previous solution held internally (retry warm-start); auto-retry on next T2/T3 change | reads as de-energized **while Faulted** (status-scoped, enforced at the read path — §17.4; the held pre-fault vector reads again as last-good once a change flips it to Dirty); game presents it (VS: "something smells wrong"; tablet: the actual diagnosis) |
| Non-finite in a solution vector | 2 | debug assert; release → `Faulted(NonFinite)` | **no NaN ever leaves the API** |
| Limit exceeded | 3 | `LimitEvent` (drained) | client pops the fuse via `Adjust`/`Reconfigure` |
| Island merged/rebuilt | 3 | `IslandChange` via `DrainChanges` | re-pin by key (§18) |
| Change-ring overflow (burst / skipped drain) | 3 | `DrainChanges(..., out lost)` ⇒ `lost == true` | **full re-pin**: re-resolve every held device handle by key (§11/§18) |
| Journal cursor overflow / lapped `OpenCursorAt` / `EditReceipt.WindowLapped` | 3 | `Overflowed()` polled / receipt flag | reducer resyncs |

`StaleHandleException` carries the offending ref, expected vs actual
generation, and the last journal event that invalidated it — a compaction bug
reads as *"stale ResistorId slot 41 gen 3, live gen 4, invalidated by
IslandRebuilt seq 8123"*, not silent aliasing.

## 21. Threading, phases, spans, allocation

**Phase discipline (per netlist, per tick).** The canonical order is
topology → re-pin → drive → solve → readback (§17). One rule makes it
enforceable: structural mutation (`Edit`, `Reconfigure`, `RemoveSegment`)
must not overlap any island `Step` (debug-asserted). Tiers 1–2 write only
island-local document state and are covered by single-writer-per-island —
which is also why creating a `DeviceTickContext` is legal at **any** time:
the context is a capability wrapper, and each `Drive`/`Adjust` through it is
asserted against *its target island's* writer, not against a netlist-global
"no Step in flight" flag. (The sanctioned per-unit parallel schedule —
drive-then-Step inside each worker, §22.b — necessarily overlaps one unit's
drive phase with another unit's Step; that is safe by single-writer and MUST
NOT assert. A netlist-global TickContext-vs-Step assert would fire on the
recommended idiom while protecting nothing the per-island assert doesn't.)

**Shared read structures under parallel Solve (binding).** The netlist-global
structures — the `ExternalKey`→handle map, `TopologyJournal`, `IslandTable`,
published solution buffers — are mutated **only** by structural operations
and by publish. Since structural ops are barred during the Step phase,
concurrent `TryResolve*`, journal reads, and `Solution` reads from multiple
island-worker threads during Solve are safe and lock-free. This is a stated
contract, not an accident. `TickStats` joins this list with its own defined
window (§9): per-scheduling-unit blocks written only by their unit's Step,
aggregated into `LastTickStats` at the readback barrier — never a shared
write during Solve.

**Off-thread tier-3 (binding compute/commit split).** The background offload
stationeers.md describes (load-time build; big rebuilds) does **not** license
mutating netlist-global structures off-thread. The split is: heavy symbolic
analysis + numeric factorization may run on a background task against staged
inputs, but every global mutation — generation reissue, journal append,
`IslandTable`/key-map updates, `Building→Ready` flips — commits **on the sim
thread, in a topology phase**, never concurrent with any Step. The initial
all-`Building` load is trivially safe (no Ready islands exist); a *runtime*
rebuild of one island while others tick uses the same handoff — compute in
the background, adopt at the next tick's topology phase, run on last-good
values meanwhile (`IsLive == false`). This is what keeps the lock-free
shared-read contract above true.

**Solution publication.** Solutions are double-buffered per island. An
island's visible buffer flips at the end of its `Step` (release-fenced index
write); `Netlist.Solve` flips all its islands at the end. Consequences:
`DeviceTickContext.Previous` reads the previous tick's published values
**deterministically for the device's own island** — its unit's drive phase
precedes its unit's Step. There is **no cross-island determinism through
`Previous`**: under a parallel schedule there is no global
drive-before-any-Step, so a foreign-island read through `Previous` sees that
island's last publish, which depends on scheduling order — exactly the
foreign-`Solution` caveat below. (Double-buffering with per-island flips
physically *cannot* preserve foreign last-tick data once that island has
stepped — the back buffer is overwritten; a global guarantee would require
triple-buffering or a pre-tick global snapshot, which we do not pay for.)
Reading a *foreign* island's `Solution` scalars during the Step phase is
legal (memory-safe: solution doubles are 8-byte aligned, and aligned 8-byte
loads are atomic on both shipping 64-bit targets) but scheduling-dependent
as above. `RawVector` spans are stricter: after a publish, the
previously-visible buffer becomes the next back buffer, so a span held
across a foreign island's Step can observe a **mixed-generation vector** —
read `RawVector` only for an island that is not concurrently Stepping (own
island, or under the readback barrier; §10). Deterministic use of
cross-island values requires reading in the readback phase or coupling the
islands (the exchange is inside the lockstep scheduling unit). Boundary lag
is a modeled physical quantity, not a race.

**Span validity (binding).** No storable struct in this API has a span
*field*; variable-length data is drained into caller spans. Span-*returning*
members are views over owner-managed buffers with these windows:
`Solution.RawVector` — until that island's next publish, and only while that
island is not concurrently mid-Step (see publication rules above);
`IslandTable.Ids` — until the next structural commit or Reconfigure;
`WaveformRing.Samples` — caller-owned, caller-managed. Never cache a span
across a tick; copy scalars out.

**Allocation buckets** (the audit table):

| Bucket | Contents |
| --- | --- |
| shape (Edit/rebuild commit, BulkBuild dispose, Resync; journal ring at construction) | matrix + LU workspace + RHS + double-buffered solutions + Newton scratch; limit-event rings; SoA component tables; key→handle map; dirty bitsets; compaction maps, interpolators, envelopes |
| client-owned | limit/change/drift drain buffers, dirty worklists, snapshot `IBufferWriter`, `WaveformRing` |
| 0B after warmup | the four verbs, `Meta.*`, `Solution.*`, `TryResolve*`, `CostOf*`, `DrainLimitEvents`/`DrainChanges`, `Snapshot` (core side), `CheckInvariants`, `Fingerprint`, `EnterSteadyState`, journal reads |
| cold, never in a tick | `Edit`/`Commit`, rebuilds, `SaveCanonical`/`SaveNormalized`, `SpiceDeck.Emit`, `ReproBundle`, `Diff`, `SetDebugName` |

Enforcement: BenchmarkDotNet MemoryDiagnoser in CI (binding, no exemptions —
the in-house sparse LU meets the gate at every island size); the debug
`AllocationSentinel` where the runtime supports it (§8).

**Parallel scheduling idiom (normative example).** The per-tick fan-out must
be closure-free: collect the dirty worklist once, then dispatch **cached**
delegates over client-owned worker state — never `Parallel.For` with a
capturing lambda (a per-tick closure allocation, and spans can't be
captured). See walkthrough §22.b.

## 22. Consumer walkthroughs

### 22.a Re-Volt tick loop (worker thread; 10k-segment Building load; restore-by-key)

```csharp
// Construction (host only; the sim runs on a rotating ThreadPool thread).
var net   = new Netlist(NetlistOptions.Stationeers(dt: 0.5));   // DC, ReferenceBound, ClientPartitioned
var graph = new ConductorGraph(net, GraphOptions.PrePartitioned);

// Save load — background UniTask; islands sit Building, game runs Re-Volt's
// scalar fallback until Ready (Solution.IsLive == false meanwhile).
// `norm` is the intake NORMALIZER's output (§19): the raw NetworkExport has
// no junction endpoints, gauges, or device ports — c.A/c.B/c.PrefabName/
// d.Port below are post-normalization fields, not raw export fields.
using (var build = graph.BeginBulkBuild(expectedSegments: norm.Cables.Count))
    foreach (var c in norm.Cables)
        build.AddSegment(new SegmentKey(c.RefId), J(c.A), J(c.B), CableSpecs.For(c.PrefabName),
                         new PartitionKey(c.NetworkRefId));     // partition MANDATORY (ClientPartitioned);
                                                                // reference rails created per partition (§19)
// Dispose: ONE compaction pass + partition/merge checks. Receipt: 10_212 segments → 143 nodes, 7 islands.

using (var e = net.Edit())
{
    // DEVICE-POPULATION CAVEAT (verified by the executable fake, Tests/Clients):
    // the BUILT-IN AdaptedLoad caches its component AND terminal handles privately
    // at Build and exposes no re-pin surface (§18), so it is only safe on islands
    // whose topology never churns. A load attached to a graph tap on a network that
    // suffers cuts/breaker trips must instead be a KEY-RE-RESOLVING adaptor
    // (stationeers.md "legacy-device adaptor"; the fake's RevoltAdaptor) that
    // re-pins by ExternalKey per §16 in the re-pin phases below. `_adapted` here
    // stands for that adaptor population.
    foreach (var d in norm.Devices)                             // ~100 adapted constant-power devices
        _adaptedByNet[d.NetworkRefId].Add(AdaptedLoad.Create(e, graph.PortNode(J(d.Port)),
                     graph.ReferenceNode(new PartitionKey(d.NetworkRefId)),   // negative = reference rail
                     baseKey: new ExternalKey(d.RefId), state: new StateKey(d.RefId)));
    foreach (var b in norm.Breakers)
        _couplers[b.RefId] = e.AddCoupler(CouplerSpec.Breaker(), PortsOf(b),
                     key: new ExternalKey(b.RefId), state: new StateKey(b.RefId));
}
net.SolveOperatingPoint();
int restored = 0;
foreach (var (netKey, blob) in saveHook.LoadBlobs())            // R6 external hook
    if (net.TryResolveNode(new ExternalKey(netKey), out var n))
        restored += net.Islands.Of(n).Restore(blob).Matched;    // ADDITIVE (§14): islands merged since the
                                                                // save take multiple blobs without clobbering;
                                                                // a split island takes the same blob twice
// Coverage is aggregate, never per blob (§14): units no blob matched stayed at cold defaults.
LogIfNonZero(TotalStateUnits(net.Islands) - restored, "cold-started state units");

// ── The half-second tick. EXECUTION MODEL (reconciled against verified game
// structure): the game runs Initialise→CalculateState→ApplyState contiguously
// PER CableNetwork (CableNetwork.OnPowerTick, invoked per network by
// ElectricityManager.ElectricityTick's ForEach) — there is no vanilla
// "all networks gathered" point. One ClientPartitioned Netlist spans all
// networks, so the manatee tick body must NOT live inside the per-network
// phases (it would solve N times, mis-drain DrainChanges on the first
// network, and push actuals at foreign networks' devices, which vanilla
// guards reject). Re-Volt already replaces the power tick wholesale; it
// therefore runs the manatee body ONCE per global ElectricityTick — a
// Harmony patch ahead of the per-network ForEach — and each network's
// ApplyState_New distributes ONLY its own partition's devices from the
// published Solution. Needs Sukasa sign-off (§23.1). All on the sim thread.
void GlobalPowerTick()                                          // once per 0.5 s tick, before the network loop
{
    // 1. Topology phase — BEFORE the guard. Producers: _pendingBreakers from
    // breaker state changes; _pendingCuts from Re-Volt's cable-topology signal.
    // NOTE the game emits no per-cable deltas — RebuildNetwork is a whole-
    // network flood (and the device-list dirty path doesn't cover cable cuts),
    // so Re-Volt diffs old vs new cable sets to feed Add/RemoveSegment, or
    // falls back to a whole-partition rebuild on any cable change.
    while (_pendingBreakers.TryDequeue(out var b))
        net.Reconfigure(_couplers[b.RefId], b.Closed ? CouplerState.Closed : CouplerState.Open);
        // CouplerId is document-stable (§16): safe even if a sibling cut rebuilt its island last tick
    while (_pendingCuts.TryDequeue(out var refId))
        graph.RemoveSegment(new SegmentKey(refId));             // N cuts = still 1 rebuild at Solve

    // 2. Re-pin phase — the first-class adaptor contract (§18), drained ONCE per global tick:
    int nc = net.Islands.DrainChanges(_changeBuf, out bool lost);
    if (lost) _adapted.FullRepin(net, graph);                   // ring overflowed: re-resolve everything by key
    else for (int i = 0; i < nc; i++)
        if (_changeBuf[i].Kind == IslandChangeKind.Rebuilt)
            _adapted.RetireRemovedAndRebind(_changeBuf[i], net, graph);  // retire cut devices; TryResolve the rest

    // 3. Drive phase — tier-3 now impossible; alloc tripwire armed (where reliable).
    using (net.EnterSteadyState())
    {
        var ctx = net.TickContext(dt: 0.5);
        for (int i = 0; i < _adapted.Count; i++) _adapted[i].Tick(in ctx);  // ~100 × Adjust, ε-no-ops once converged
    }
    net.Solve(_clock.Next(0.5));                                // ONE solve for all 7 islands

    // 4. CHURN-TICK RE-PIN (binding; adjudicated 2026-07-06). Island rebuilds run
    // INSIDE Solve (§16/§17) and reissue every member component/node handle, so on
    // any tick that mutated topology or drained changes, the phase-2 re-pin above is
    // NOT sufficient: the handles it resolved were just reissued by this Solve.
    // Re-resolve the churned adaptors by key AGAIN — here, before any ApplyState_New
    // reads the Solution — or the readback dereferences exactly the handles this
    // tick invalidated (a counted no-op: StaleHandleReads > 0, and the post-tick
    // assert below fires). The executable fake proves both directions: with this
    // re-pin the whole scripted run holds StaleHandleReads == 0; without it, a cut
    // tick trips the counter (FakeRevoltClientTests).
    if (topologyChanged || changesDrained) _adapted.Repin(net, graph);
}
void ApplyState_New(CableNetwork network)                       // per network, inside its OnPowerTick phases
{
    var mine = _adaptedByNet[network.RefId];                    // ONLY this partition's devices — vanilla
    for (int i = 0; i < mine.Count; i++)                        // ReceivePower/UsePower guards reject
        mine[i].Apply(net.Solution);                            // foreign-network calls
}
void GlobalPostTick()                                           // once, after the per-network loop
{
    for (int i = 0; i < net.Islands.Count; i++)
    {
        var isl = net.Islands[i];
        if (isl.Status == IslandStatus.Faulted) { _scalar.FallbackTick(isl); Log(isl.Fault); continue; }
        int n = isl.DrainLimitEvents(_limitBuf);
        for (int k = 0; k < n; k++)
            if (graph.Attribute(in _limitBuf[k], out var hit)) _fuses.Blow(hit.Segment);   // which segment pops (R7)
    }
    _telemetry.Record(net.LastTickStats);
    Debug.Assert(net.LastTickStats.StaleHandleReads == 0);      // a nonzero count is a re-pin bug
}
```

Steady-state contract, executable: after convergence,
`Assert.Equal(0, stats.Refactorizations); Assert.Equal(0, stats.IslandRebuilds);
Assert.Equal(0, stats.BytesAllocated);` — the standing CI tier-budget test.

### 22.b VS AC island: two-wire construction, closure-free parallel schedule

```csharp
var net   = new Netlist(NetlistOptions.VintageStory(tickDt: 0.05));   // Mixed, TwoWireLeak(1e6), SelfPartitioned
var graph = new ConductorGraph(net, GraphOptions.SelfPartitioned);

using (var e = net.Edit())
{
    var earth = e.AddReferenceNode(EarthKey);                   // the literal earth node
    // Pathfinder laid a conductor PAIR (R13); the return terminal is Return-role,
    // so TwoWireLeak auto-stamps the 1 MΩ earth leak at commit — no per-device code.
    var lampPos = graph.PortNode(J(supplyEnd));
    var lampNeg = e.AddNode(LampNegKey, NodeRole.Return);
    e.AddResistor(lampPos, lampNeg, LampOhms, key: LampKey, limits: LampLimits);
    _alt = e.AddSineSource(altPos, altNeg, new SineDrive(12.0, 5.0, 0.0),
                           key: AltKey, state: StateKey.From(AltKey));   // island is AC ⇒ subcycles (N≈5 @5 Hz)
}
net.SolveOperatingPoint();
var scope = graph.AddProbe(new SegmentKey(midRun), along: 0.5);  // reads INSIDE a compacted bus (interpolated).
                                                                 // ProbeId is document-stable across rebuilds
                                                                 // (§16); re-resolve only after a drift Resync
                                                                 // (TryResolveProbe).
var tapA = WaveformTap.Attach(net, scope, _ringA);   // oscilloscope subscribe: COLD, ONCE, at setup (§13) —
                                                     // Attach in the tick loop would allocate a tap per tick
                                                     // and violate the very 0B contract this walkthrough models

// ── Per electrical tick (50 ms), client-owned worker pool, closure-free ──
// IslandWorker instances and their Run delegates are allocated ONCE at shape time.
sealed class IslandWorker
{
    public IslandHandle Handle; public TickClock Clock; public Netlist Net;
    public DeviceList Devices;                       // client-owned array per island
    public readonly Action Run;                      // cached delegate — no per-tick closure
    public IslandWorker() => Run = Execute;
    void Execute()
    {
        var ctx = Net.TickContext(Clock.Dt);         // drive phase for THIS island's unit
        for (int i = 0; i < Devices.Count; i++) Devices[i].Tick(in ctx);
        Handle.Step(Clock);                          // single-writer-per-island (debug-asserted)
    }
}

int n = net.Islands.CollectDirty(_dirtyBuf);         // one handle per SCHEDULING UNIT (coupled islands dedupe)
for (int i = 0; i < n; i++) { _workers[i].Handle = _dirtyBuf[i]; _workers[i].Clock = clock; }
_pool.Dispatch(_workers, n);                          // client pool; core never owns threads
_pool.Wait();                                         // readback phase begins only after the barrier

double vMid = net.Solution.Read(scope);               // interpolated interior V for the multimeter tooltip;
                                                      // _ringA filled per substep by the setup-time tap (0B)
#if DEBUG
var inv = net.Islands.Of(_lampPos).CheckInvariants(InvariantChecks.Kcl | InvariantChecks.Finiteness);
Debug.Assert(inv.MaxKclResidual < 1e-9 && inv.AllFinite);   // stamp-bug tripwire in ordinary dev play
#endif
```

Frequency is a piecewise-constant input from the mechanical network (~100 ms
cadence); the source driver tracks exact phase per substep (tier 1); N
changes only past the hysteresis band — a rare, solver-owned tier-2 event,
observable on `Plan`, never client-settable.

### 22.c The ngspice oracle harness

```csharp
var net = BuildLessonNetlist("l07-rc-charge");        // Falstad importer (R20 corpus) → Edit()
var islId = net.Islands.Ids[0];                       // canonical, rebuild-stable iteration order
var isl = net.Islands.Of(islId);
for (int i = 0; i < 90; i++) isl.Step(new TickClock(i, 0.05));   // manatee trace

var deck = SpiceDeck.Emit(net, islId, new SpiceEmitOptions {
    Analysis = SpiceAnalysis.Tran(step: 0.05, stop: 4.5), MatchBackwardEuler = true });  // method=gear maxord=1
Assert.Empty(deck.Unrepresentable.ToArray());         // lesson circuits must be fully oracle-able
await Verify(deck.Text);                              // deterministic text ⇒ Verify golden: a stamp refactor
                                                      // is a reviewable diff BEFORE it is an oracle delta
RunNgspice(WriteTmp(deck.Text), out RawFile raw);     // devshell-pinned; Category=Oracle hard-fails if absent
foreach (var (node, col) in deck.NodeNames)           // rawfile column ↔ NodeId — no name guessing
    OracleDiff.Assert(raw[col], net.Solution.Voltage(node), relTol: 1e-3, absTol: 1e-6);

// Narrative pass (R20): machine-readable front-matter against the same solve.
foreach (var exp in lesson.Expectations)              // {probe, time, value, tol}
    Assert.InRange(net.Solution.Read(exp.Probe), exp.Value - exp.Tol, exp.Value + exp.Tol);

// The serialization laws (§14) run over the same corpus:
AssertNormalizedEqual(net, RebuildFromScratch(lesson));          // law 3 — R11's drift detector as equality
AssertBitEqualAfterSnapshotRoundTrip(isl);                       // law 4 — RawVector double-bit equality
```

```csharp
public static class SpiceDeck   // Manatee.Core.Diagnostics
{
    public static DeckResult Emit(Netlist n, IslandId island, in SpiceEmitOptions opts);
}
public readonly struct SpiceEmitOptions { public SpiceAnalysis Analysis; public bool MatchBackwardEuler; }
public readonly struct DeckResult
{
    public string Text;                                  // deterministic names (slot order) ⇒ Verify golden
    public ReadOnlyMemory<ComponentRef> Unrepresentable; // behavioral devices ngspice can't oracle — the
                                                         // differ knows its own fidelity
    public IReadOnlyList<(NodeId Node, string Column)> NodeNames;  // rawfile column ↔ NodeId
}
```

Behavioral two-ports (converters, swing-lite, boundary couplings) land in
`Unrepresentable` and are covered by invariants + hand-computed inline cases
instead. `ReproBundle`/`CommandRecorder` (opt-in, debug-gated, ring-wrap
rebase off the sim thread; boundary exchanges recorded as tier-1 commands so
one island replays without its neighbors) share the same artifact format
between a CI failure and an in-game Faulted island export.

## 23. Open items

Carried explicitly. The core is BUILT (2026-07-06); these are residual
follow-ups and integration-time verifications, not blockers.

1. **Sukasa sign-off:** `Reconfigure` barred inside `EnterSteadyState`
   (breaker consequences of this tick's limit events land next tick in the
   intended pattern; the release-mode deferral bound is same-tick-before-Solve,
   §8). Also `ClientPartitioned` whole-commit atomic rejection ergonomics;
   the **one-global-tick-body execution model** (§22.a — manatee body hoisted
   ahead of the per-network loop, per-network ApplyState distributes own
   partition only); and **closed-breaker bidirectionality** (§7 — galvanic
   merge vs vanilla's directional Input→Output transfer; back-feed becomes
   possible, vanilla trip/analyzer metrics re-derived from signed coupler
   branch flow).
2. **Mono/Unity allocation-counter verification:** the §8 capability probe is
   specified, but actual behavior of `GC.GetAllocatedBytesForCurrentThread`
   on Re-Volt's Unity 2022.3 Mono must be verified on-device as an early
   integration task. The CI MemoryDiagnoser gate is binding regardless.
3. **AdjustEpsilon default (1e-4)** is a guess; the *definition* (§9) is
   pinned, the default is tuned by benchmark/oracle before the tier-budget
   goldens are authored.
4. **Parameter structs: all now pinned** (item retained for the record).
   `SubstepPlan` = `Substeps`/`SubstepDt`/`HysteresisBand` (§11);
   `TransformerParams` = `(TurnsRatio, LeakageOhms, MagnetizingOhms)`;
   `EfficiencyCurve` = 1–4 `(loadFraction, efficiency)` breakpoints;
   `DiodeParams` — see `Primitives.cs`/`Couplers.cs`. `GraphOptions` and the
   128-bit `SegmentKey`/`JunctionKey` layouts are pinned in
   `Reduction/Keys.cs`; `DebugLevel` in `Options.cs`; `I2tParams` is realized
   as the `LimitSpec.Thermal` pair plus the envelope record `I2tPair` (§12).
5. **Cross-platform FP:** bit-for-bit determinism is scoped to one
   `EnvFingerprint` (runtime/arch/build); CI runs the corpus on x64 and
   arm64 with tolerance diffing. Whether to forbid FMA-contractible patterns
   in the LU kernel to widen the domain is deferred until evidence demands.
6. **Snapshot format versioning policy:** versioned binary header shipped —
   `SnapshotVersion`/`CanonicalVersion` are `3` (`Netlist.State.cs`), and
   `RestoreIsland` gates *strictly* (rejects any version `!= SnapshotVersion`);
   the JSON debug form lives in Diagnostics. What stays open is the *migration*
   policy — how many versions back `Restore` should accept once a shipped save
   must survive a format bump — deferred until a released client needs it.
7. **Thermal-RC generalization of `ConductorSpec.OhmsPerLength`** naming:
   deferred (design.md defers the chemistry/thermal arc); the solver-layer
   naming is already domain-neutral.
8. **Stationeers intake normalization is unbuilt** (§19): `NetworkExport`
   lacks the agreed additions (junction endpoints, gauge, device ports,
   fuse-in-adjacency) and `NetworkExportCommand.Execute` is a stub. The
   normalizer + export additions gate the 10k-segment intake benchmark.
8a. **Floating nonlinear islands can converge to spurious operating points**
   (found by the phase-4 oracle wave): a full-wave diode bridge grounded only
   by gmin (possible under `ExplicitOnly` wiring) reached a Newton solution
   violating an ideal V-source constraint by volts. `ReferenceBound`/
   `TwoWireLeak` topologies carry real reference paths and are immune in
   practice; the oracle test works around it with a physical bleeder.
   Candidate fixes to evaluate: post-converge constraint-residual check ⇒
   `Faulted`, or gmin ramping. Until then `CheckInvariants(Kcl)` after
   suspect solves is the tablet-side mitigation.
9. **stationeers.md follow-up revision needed** (found in adversarial review,
   fixed here in the walkthrough but not yet in that doc): its Integration
   Seams section maps manatee phases onto the per-network 3-phase contract,
   which cannot host a single global solve (§22.a model supersedes it); its
   incremental-hooks claim ("fires on topology change") doesn't match the
   source — the dirty path tracks device lists, not cable cuts, and
   `RebuildNetwork` is a whole-network flood requiring a cable-set diff; and
   its "galvanic bridge" breaker assertion must carry the §7 vanilla-metric
   derivation and back-feed caveat.

## 24. Decision log

Contested calls, with the losing position noted:

1. **Generation-bump timing (OQ1/OQ3 nexus): island-wide handle invalidation
   happens at Solve, not at commit; only the removed component's own slot
   dies at commit.** Two judges required this (the commit-time bump makes the
   same-tick device loop a latent `StaleHandleException`); the maintainer
   judge preferred eager-at-commit, objecting to a "doomed-but-usable"
   window. Resolved in favor of defer-to-Solve *plus* the document/matrix
   rule (§4, §17): verbs write the retained document and the rebuild restamps
   from it, so nothing written in the window can be lost — the window is safe
   by construction, not by luck.
2. **Two keys, not one:** `ExternalKey` (topological identity, every Add) +
   `StateKey` (serializable-state unit). The winning draft and the testing
   draft used one key; two judges required the split for the real granularity
   gap (one device owns many components but one state blob — Battery, R18
   adaptor, alternator are all in R1–R11 scope). The one-key camp's footgun
   concern is answered by both keys being non-optional parameters and by
   `StateKey.From(ExternalKey)` for bare primitives.
3. **Reconfigure-Open is a full island rebuild** (canon: solver.md tier 3,
   split detection not attempted). The testing draft's cheaper "membership
   rebuild" that preserved component handles is rejected as canon-incompatible,
   along with its 3-id `IslandSplit` journal event: an Open emits one
   `IslandRebuilt(old,new)` per resulting island, and `TopologyEvent` carries
   exactly two island ids so the non-canon event cannot be reintroduced.
4. **Merge preserves component/node handles** (only the absorbed `IslandId`
   dies). Asymmetric with Open by design: merge is additive; if it
   invalidated handles, incremental VS voxel placement would be a lie.
5. **Release-mode guard violations defer to guard `Dispose`** (same tick,
   before Solve), logged and counted — not dropped, not deferred to an
   unspecified later point. Resolves the breaker-safety risk (a deferred
   Open cannot leave an island energized across a tick) and the
   "deferral point unspecified" risk simultaneously.
6. **Handles are 12 bytes: `(int Slot, uint Gen, ushort Net)`.** Gen widened
   from the drafts' `ushort` (65536 reuses wraps in ~55 min of pathological
   churn) to `uint` (~6.8 years); `Net` embeds the netlist id so cross-netlist
   handles are caught by value (testing-draft steal).
7. **Generic readbacks are pinned to the constrained-callvirt pattern** on
   `where TId : struct, IComponentId` — provably non-boxing from the
   signature; type-switch/`Unsafe.As` implementations are forbidden (Mono
   may not eliminate them).
8. **No span fields in storable structs** (they don't compile as
   `readonly struct`, and ref-structs can't be stored): `EditReceipt`,
   `RestoreResult`, `FaultDiagnostic`, `DriftReport` carry scalars plus
   drain-into-caller-span methods. Span-returning *members* get explicit
   validity windows (§21).
9. **Wiring policy has three modes** — `ReferenceBound` (Stationeers),
   `TwoWireLeak` (VS), `ExplicitOnly` (tablet; the winning draft's
   `ReferenceBound` tablet default is overruled — floating-by-default lessons
   must stay floating). Policy fires declaratively on source negatives,
   `NodeRole.Return` nodes, and `TerminalSpec.ReturnTerminals`; there is no
   `BindNegative`.
10. **Allocation tripwire is best-effort; CI MemoryDiagnoser is binding.**
    The sentinel self-probes at first arm and disarms loudly on runtimes
    where the counter is inert (Unity/Mono risk, flagged by all judges).
11. **The verbs live on `Netlist` as named methods** (P1 base), with `Meta`
    as the one tier-0 facade; P2's full facade-per-tier surface stays
    rejected. `Reconfigure` reports as `Tier.Topology` — no fifth tier value.
12. **`StructuralEdit` is a pooled sealed class** (copyable-struct batch is a
    footgun; pool churn is shape-time only).
13. **One `Mixed` netlist hosts both DC and AC islands** (regime chosen
    per island by content) — the alternative one-regime-per-netlist reading
    is rejected; Stationeers stays on the pure `Dc` profile.
14. **The adaptor re-pin obligation is a first-class contract row** (§18)
    keyed off `IslandTable.DrainChanges`/`EditReceipt` — never journal
    cursors (rejected list) — including the retire-removed-devices rule for
    primitives cut this tick.
15. **`SnapshotSize` stability is defined against all topology-changing ops**
    including galvanic `Reconfigure` (not just Edit commits), closing the
    buffer-mis-sizing gap after a breaker trip.
16. **Reduction uses only public Core API** — no `InternalsVisibleTo`
    friendship (lifecycle steal, promoted to a binding invariant in §2).
17. **Bulk-build staging grows geometrically (2×) from a presize hint**;
    the 10k-segment load-time case reallocs O(log n) times, measured by the
    standing tier-3 benchmark.

Adversarial-review round (2026-07-06, four reviewers; all 24 findings
verified and applied — none rejected):

18. **Couplers and probes are document-stable** (a rebuild never invalidates
    `CouplerId`/`ProbeId`; §16). The review's kill sequence: topology phase
    runs before re-pin, so a breaker whose island a sibling cut rebuilt last
    tick would be `Reconfigure`d through a stale handle — in release a
    counted no-op, i.e. a dropped breaker `Open` and an island left
    energized, violating §8. The alternative (re-pin phase first + coupler
    re-resolution) was rejected as ordering-fragile; island-rooted identity
    was incoherent for boundary objects anyway. `TryResolveProbe` added as
    the resync/reload re-resolution path.
19. **Restore is additive** (§14): a blob never resets units it lacks;
    coverage is aggregated against `StateUnitCount`. The prior
    cold-start-the-unmatched semantics made multi-blob restore into a
    merged island last-blob-wins (silent state loss) and gave the split
    case no path at all.
20. **`DrainChanges` gets the journal's explicit-overflow discipline**
    (`out bool lost` ⇒ full re-pin obligation; §11, §20). The
    safety-critical re-pin channel had strictly weaker guarantees than the
    journal serving the non-critical reducer.
21. **The ε-no-op gate is transcendental-free** (§9): pinned ratio bounds +
    one IEEE division. `Math.Log` in the gate would let near-boundary
    Adjusts classify differently across libms and diverge the dual-arch
    tier-budget goldens.
22. **`TickStats` accumulates per scheduling unit**, aggregated at the
    readback barrier in `Islands.Ids` order (§9, §21); a netlist-global
    accumulator under parallel Step was a torn-write race on the exact
    counters CI asserts on.
23. **`AddIdealTransformer` is a first-class primitive** (§6): a turns-ratio
    constraint is inexpressible as independent two-terminal R/L/C/V/I
    composition, so the canon-required `IdealizedTransformer` (solver.md
    component set; tablet AC curriculum) was unbuildable through the public
    surface. `DeviceTickContext.Adjust` now mirrors the full `Netlist.Adjust`
    set for the same capability-completeness reason. (Resolved: solver.md's
    component-set section names the ideal transformer two-port.)
24. **One global manatee tick per `ElectricityTick`** (§22.a): the game runs
    the 3-phase contract contiguously per network with no gather point;
    running the body per network would solve N times, mis-drain
    `DrainChanges`, and hit vanilla foreign-network guards. Rejected
    alternative: per-partition Step with lead election (more moving parts,
    still needs a global drain point).
25. **Journal cursors are positionable** (`OpenCursorAt`; lapped ⇒
    `Overflowed` ⇒ resync) and single-commit overflow is declared up front
    (`EditReceipt.WindowLapped`; §15) — the receipt-window replay pattern
    was inexpressible with head-only cursors, and §2 forbids the reducer
    internal seeking.
26. **Determinism claims narrowed to their true scope** (§21): `Previous`
    and `RawVector` are deterministic/safe same-island only; the
    netlist-global TickContext-vs-Step assert is dropped (it fired on the
    sanctioned §22.b schedule and protected nothing single-writer doesn't);
    off-thread tier-3 is a compute/commit split with all global mutations
    committed on the sim thread; guard-deferred structural ops live in a
    pre-sized netlist ring (grow-on-overflow, counted, never dropped);
    coalesced merges still emit their membership events; `Meta.SetLimits`
    takes `LimitSpec` (the phantom `LimitConfig` type is gone).

Merge-window review round (2026-07-07, adversarial verify of the
§17.4 last-good fix):

27. **Faulted reads are de-energized, status-scoped, enforced at the read
    path** (§10/§17.4/§20). Adjudicates the standing §20 tension: the code
    held the pre-fault published vector readable while Faulted; canon (§10
    `IsLive` doc, §11 state machine, §20 table) said de-energized. Canon
    wins: `NodePotential`/`BranchCurrent` gate on `IslandStatus.Faulted`.
    The scope is the *status*, not a taint — leaving Faulted (retry or
    merge → Dirty) restores ordinary last-good reads of the last
    successfully *published* vector, which never contains fault output
    (failed solves never publish). This makes the merge ruling automatic
    and symmetric: captures read through ungated inner readers, so both
    union orientations behave identically and no `absorbedFaulted` special
    case exists. Merge-window last-good now also covers **voltage-source
    aux flows** (`Solution.Current`/`Power` on a source): captured
    per-component at merge commit, same lifetime as held node potentials —
    an absorbed-side source no longer reads a one-tick 0 A on breaker
    close.

Commit-order round (2026-07-07):

28. **Within-commit apply order is removals-then-additions** (§17.1). The
    overnight build applied adds first — incidental, no dependency
    required it (slots are reserved at staging, so orders can't collide;
    validation is already batch-net; a removal's pending rebuild mark is
    carried across a later add-union by `UnionNodes`). Adds-first broke
    the same-batch remove + re-add-same-`ExternalKey` pattern: the
    removal's key-map cleanup deleted the *new* registration (release) or
    the duplicate-key assert rejected the batch (debug), forcing the
    breaker-move operation into two commits for no reason. Removes-first
    fixes that and gives journal replayers clean replace semantics. One
    guard rode along: a probe staged in the same batch as its aimed
    node's removal has its aim invalidated to −1 *before* `ProbeAdded` is
    journaled, so the apply path tolerates a dangling aim (reads 0 until
    re-aimed, §13 — same contract as post-commit invalidation).
