# manatee-core Netlist API — Proposal C: "Every bug ships its own repro"

**Design thesis.** The netlist is a *reducer over a command log*. There is exactly one
mutation choke point (`Apply(in Command)`); every typed convenience method is sugar that
builds a `Command`. Solve calls are commands too. Consequences fall out structurally
rather than being bolted on:

- **Replayability**: base snapshot + command log ⇒ bit-identical reconstruction,
  including every intermediate solution. IDs are dense sequence numbers assigned in
  application order, so replay allocates the *same IDs* — no ID-mapping tables.
- **Flight recorder**: a preallocated ring of 32-byte blittable `Command` structs is
  cheap enough to leave on in production. When an island Faults, it exports a
  `ReproBundle` (base + log + state + env fingerprint). SRE framing: crash artifacts
  are self-contained, like a core dump that actually replays.
- **Tier legibility (R4)**: cost class is part of the *type system*, not doc-comments —
  tier-1/2 mutators live on separate facade structs and tier 3 is unreachable without
  holding a `TopologyBatch` token (§2).
- **Invariants as API** (not just debug asserts): KCL residual, energy audit,
  finiteness, and structural hashes are public readbacks any layer (tests, the resync
  backstop, an in-game debug HUD) can query.

Both games use the same surface: Stationeers is `SolveDc`+`Step(0.5)` on
pre-partitioned islands; VS is `Step(0.05)`/`StepAc` on self-partitioned islands. The
lens sets emphasis; nothing here is tablet-only.

---

## 1. C# API sketch

Signatures + one-line docs; no bodies. Namespace `Manatee.Core` unless noted.

### 1.1 IDs and the command union

```csharp
// Dense, order-of-creation, replay-stable. record structs: misuse fails to typecheck.
public readonly record struct NodeId(int Value);
public readonly record struct ComponentId(int Value);        // erased view of any typed id
public readonly record struct ResistorId(int Value);          // likewise VSourceId, ISourceId,
public readonly record struct CapacitorId(int Value);         // InductorId, SwitchId, DiodeId
public readonly record struct IslandId(int Value);
public readonly record struct ProbeId(int Value);

public enum Tier : byte { Metadata = 0, Rhs = 1, Conductance = 2, Topology = 3 }

/// One unit of the log: a mutation OR a solve. 32 bytes, blittable, ring-buffer friendly.
public readonly struct Command {
    public CommandOp Op;          // AddResistor, SetResistance, SetSwitch, Remove, MarkGround,
                                  // SetDrive, SetLimits, BoundaryExchange, SolveDc, Step, StepAc, ...
    public int A, B;              // raw node/component ids (typed at the facade, erased here)
    public double X, Y;           // parameters (ohms, volts, dt, …)
    public Tier Tier { get; }     // derived from Op — a total function, unit-tested as such
}
```

Solve calls being log entries is the load-bearing trick: a log replays the *entire
history that affects state*, so "reproduce tick 4711" is `Replay.RunTo(bundle, 4711)`,
not archaeology. Boundary-coupling exchanges (solver.md, Islands) are recorded as
tier-1 `BoundaryExchange` commands *on the receiving island* — so a single Faulted
island replays offline without simulating its neighbors.

### 1.2 Netlist: construction, recording, serialization

```csharp
public sealed class Netlist {
    public static Netlist FromSnapshot(ReadOnlySpan<byte> canonicalBytes);  // ID-preserving load
    public void Apply(in Command c);      // THE mutation entry point; facades route here
    public Tier CostOf(in Command c);     // what Apply would cost, without applying

    public Tier1Edits Values { get; }             // §2 — RHS-only mutators
    public Tier2Edits Conductances { get; }       // §2 — refactor-class mutators
    public TopologyBatch BeginTopology();         // §2 — the only door to tier 3

    public void AttachRecorder(CommandRecorder r);  // every Apply (incl. solves) lands here

    // Canonical, versioned binary; components in ID order, doubles as raw bits.
    public void SaveCanonical(IBufferWriter<byte> dst);   // ID-preserving ⇒ replay-compatible
    public void SaveNormalized(IBufferWriter<byte> dst);  // renumbered minimal form ⇒ golden/corpus comparisons
    public NetlistHash StructuralHash { get; }            // incrementally maintained; resync-backstop diff unit

    public IslandTable Islands { get; }
    public ProbeId AddProbe(NodeId n, string name);       // tier 0; survives reduction via interpolators
    public void SetTag(ComponentId c, long clientTag);    // tier 0; geometry backref — no strings in core
    public void SetLimits(ComponentId c, in LimitConfig cfg);  // tier 0; metadata only, never touches matrix
}
```

`SaveCanonical` vs `SaveNormalized`: the first preserves ID holes so a log recorded
against the live netlist replays against the snapshot; the second renumbers for
structural-equality goldens ("these two build paths made the same circuit"). Round-trip
laws, enforced as CI property tests: `SaveCanonical(FromSnapshot(x)) == x` byte-equal;
`SaveNormalized` is a fixpoint; any edit sequence and its from-scratch rebuild agree
under `SaveNormalized` (this *is* R11's drift detector, exposed as API).

### 1.3 Tiered facades

```csharp
public readonly struct Tier1Edits {   // every method: back-substitution only, safe per-substep
    public void SetSourceVoltage(VSourceId id, double volts);
    public void SetSourceCurrent(ISourceId id, double amps);
    public void SetDrive(VSourceId id, in SineDrive d);   // amplitude/frequency/phase — RHS via source driver
}
public readonly struct Tier2Edits {   // every method: numeric refactor, symbolic reused
    public void SetResistance(ResistorId id, double ohms);
    public void SetSwitch(SwitchId id, bool closed);      // relay contact; breakers are coupling devices
    public void SetDiodeParams(DiodeId id, in DiodeParams p);
    public void SetAcSubstepCount(IslandId id, int n);    // the deliberate, hysteresis-gated N change
}
public sealed class TopologyBatch : IDisposable {  // tier 3; Dispose commits ⇒ ONE coalesced rebuild
    public NodeId      AddNode();
    public ResistorId  AddResistor(NodeId a, NodeId b, double ohms);
    public VSourceId   AddVoltageSource(NodeId pos, NodeId neg, double volts);
    public ISourceId   AddCurrentSource(NodeId from, NodeId to, double amps);
    public CapacitorId AddCapacitor(NodeId a, NodeId b, double farads);
    public InductorId  AddInductor(NodeId a, NodeId b, double henries);
    public SwitchId    AddSwitch(NodeId a, NodeId b, bool closed);
    public DiodeId     AddDiode(NodeId anode, NodeId cathode, in DiodeParams p);
    public void        Remove(ComponentId id);
    public void        MarkGround(NodeId n);              // island reference; VS: the literal earth node
    public void        Dispose();                          // commit; rebuilds affected islands exactly once
}
```

### 1.4 Islands, analyses, readback, state, invariants

```csharp
public sealed class IslandTable {
    public int Count { get; }
    public ReadOnlySpan<IslandId> Ids { get; }             // ascending — THE deterministic iteration order
    public Island this[IslandId id] { get; }
    public IslandId IslandOf(NodeId n);
    public int DrainChanges(Span<IslandChange> dst);       // merges/rebuilds since last drain; client re-pins work
}

public sealed class Island {
    public IslandId Id { get; }
    public IslandStatus Status { get; }                    // Ok | Faulted (holds last good solution)
    public ulong Tick { get; }                             // logical tick counter; serialized; replay-comparable
    public ulong Generation { get; }                       // bumps per solve; guards stale Solution reads (debug)

    // The three analyses (R2). All record themselves into the command log.
    public SolveResult SolveDc();                          // operating point (caps open, inductors ~short)
    public SolveResult Step(double dt);                    // one Backward Euler transient step
    public SolveResult StepAc(double tickDt);              // N phase-continuous substeps; tier 1 when linear

    public Solution Solution { get; }                      // view over island-owned buffers; no copies
    public int DrainLimitEvents(Span<LimitEvent> dst);     // returns count; caller owns the buffer
    public int DescribeFault(Span<FaultDetail> dst);       // R9: named nodes/components, machine-readable

    // State snapshot/restore (R6): caps, inductor currents, device state, source phase, Tick.
    public int  StateSizeBytes { get; }                    // fixed after tier-3 commit ⇒ caller preallocates
    public void SaveState(Span<byte> dst);                 // raw double bits; versioned header; no text detour
    public void RestoreState(ReadOnlySpan<byte> src);      // exact companion-history reconstruction
    public StateHash StateHash { get; }                    // cheap divergence probe for A/B runs

    // Invariants as API surface — callable by tests, the backstop, and in-game debug HUDs.
    public InvariantReport CheckInvariants(InvariantChecks which);
    public EnergyAudit Energy { get; }                     // running source/dissipated/Δstored accumulator
    public TickStats DrainStats();                         // tier-1 writes, tier-2 refactors, tier-3 rebuilds, Newton iters

    public void ExportRepro(IBufferWriter<byte> dst);      // flight recorder → ReproBundle (cold path; may allocate)
}

public readonly struct Solution {
    public double VoltageAt(NodeId n);                     // interpolated if n was compacted away (R10)
    public double CurrentThrough(ComponentId c);
    public double PowerIn(ComponentId c);
    public ReadOnlySpan<double> RawVector { get; }         // MNA order — the bit-for-bit comparison unit
}

[Flags] public enum InvariantChecks { Kcl = 1, Finiteness = 2, Energy = 4, All = ~0 }
public readonly struct InvariantReport {
    public double MaxKclResidual;  public NodeId WorstKclNode;     // "which node leaks current"
    public bool   AllFinite;       public int FirstNonFiniteIndex;
    public double EnergyResidual;                                   // source − dissipated − Δstored
}
public struct TickStats { public int Tier1Writes, Tier2Refactors, Tier3Rebuilds, NewtonIterations, AcSubsteps; }
```

### 1.5 Limits (R7)

```csharp
public struct LimitConfig { public double MaxAbsCurrent, MaxAbsVoltage, MaxPower; public I2tCurve Thermal; }
public readonly struct LimitEvent {   // solver reports; client maps to geometry via tags + compaction envelope
    public ComponentId Component; public LimitKind Kind;
    public double Observed, Threshold; public ulong Tick;  // Tick ⇒ events are replay-diffable artifacts
}
```

### 1.6 Devices (statically fenced to tiers 1–2)

```csharp
public interface IDevice {
    void Instantiate(TopologyBatch b, ReadOnlySpan<NodeId> terminals);  // topology only at build time
    void Tick(in DeviceContext ctx, double dt);   // ctx exposes ONLY Tier1Edits/Tier2Edits + own state span
    int  StateSizeBytes { get; }
    void SaveState(Span<byte> dst); void RestoreState(ReadOnlySpan<byte> src);
}
```

A device *cannot* mutate topology mid-tick — the capability isn't in scope. The
Stationeers R18 adaptor, batteries, alternators, and swing-lite generators are all
`IDevice.Tick` writing tier-1/2 values; their writes are logged like anyone else's.

### 1.7 Recording, replay, deck emission (`Manatee.Core.Diagnostics`)

```csharp
public sealed class CommandRecorder {
    public CommandRecorder(int capacity);                 // preallocated ring of Command (32 B each)
    public ulong FirstSeq { get; } public ulong NextSeq { get; }
    public ReadOnlySpan<Command> Since(ulong seq);
    public void SaveLog(IBufferWriter<byte> dst);         // versioned; the replayable artifact
}
public readonly struct ReproBundle {
    public ReadOnlyMemory<byte> BaseNetlist;              // SaveCanonical at ring start (auto-rebased, §5)
    public ReadOnlyMemory<byte> CommandLog;               // mutations + solves + boundary exchanges
    public ReadOnlyMemory<byte> State;                    // island state at capture
    public EnvFingerprint Env;                            // runtime, arch, build hash — the bit-for-bit domain
    public static ReproBundle Load(ReadOnlySpan<byte> src);
    public void Save(IBufferWriter<byte> dst);
}
public static class Replay {
    public static Netlist Rebuild(in ReproBundle b);                    // base + log ⇒ identical netlist, same IDs
    public static void RunTo(Netlist n, in ReproBundle b, ulong tick);  // re-executes commands incl. solves
}
public static class SpiceDeck {
    public static DeckResult Emit(Netlist n, in SpiceEmitOptions opts); // deterministic names (R7 = resistor id 7)
}
public readonly struct SpiceEmitOptions { public SpiceAnalysis Analysis; public bool MatchBackwardEuler; } // .options method=gear maxord=1
public readonly struct DeckResult {
    public string Text;                                   // the .cir deck
    public ReadOnlyMemory<ComponentId> Unrepresentable;   // behavioral devices ngspice can't oracle — differ knows fidelity
    public IReadOnlyList<(NodeId, string)> NodeNames;     // rawfile column ↔ NodeId map for the differ
}
```

---

## 2. How the change-cost tiers appear in the API surface (R4)

Four mechanisms, layered from compile time to CI:

1. **Capability shape (compile time).** Tier 3 does not exist as instance methods —
   `AddResistor` lives only on `TopologyBatch`, obtainable only via
   `using var b = net.BeginTopology()`. You cannot pay for a rebuild by accident; the
   `using` block is the visible "this costs a rebuild" bracket, and Dispose coalesces
   the whole batch into one rebuild (compaction.md's coalescing rule, surfaced).
   Devices get a `DeviceContext` that carries only `Tier1Edits`/`Tier2Edits` — the hot
   path is fenced by what the type *can't* express.
2. **Call-site legibility (read time).** Tier-1/2 mutators are namespaced facades:
   `net.Values.SetSourceVoltage(...)` vs `net.Conductances.SetSwitch(...)`. The cost
   class is grep-able in client code without opening docs — a reviewer scanning a VS
   tick handler sees `Conductances.` inside a substep loop and knows it's wrong.
3. **Programmatic query (design time).** `net.CostOf(in Command)` answers "what would
   this cost" without applying, for client authors writing schedulers (e.g. Re-Volt
   deciding whether an edit fits the remaining tick budget).
4. **Enforced budgets (test time).** `island.DrainStats()` reports actual tier
   traffic. The steady-state contract becomes an assertion, not folklore:
   `Assert.Equal(0, stats.Tier2Refactors); Assert.Equal(0, stats.Tier3Rebuilds);`
   in the standing "linear AC island in steady operation lives in tier 1" test, and in
   the CI zero-alloc benchmark. When a future edit accidentally demotes a tier-1 path,
   CI says so before a profiler does.

Tier 0 (metadata: tags, limits, probes) is called out explicitly so client authors know
`SetLimits` on an environment change (Europa vs Vulcan ambient) is free of matrix cost —
compaction.md's envelope recompute stays a metadata operation on this surface too.

---

## 3. Worked examples

### 3.1 Failing lesson golden, reproduced offline from serialized inputs alone

CI reports lesson `l07-rc-charge` failing its narrative expectation at t = 4.5 s. The
test harness — which always runs with a `CommandRecorder` attached — saved
`l07-fail.mrb` as a test artifact. No game, no harness, no ngspice needed to debug:

```csharp
var bundle = ReproBundle.Load(File.ReadAllBytes("l07-fail.mrb"));
var net    = Replay.Rebuild(in bundle);            // same IDs as the CI run, guaranteed
Replay.RunTo(net, in bundle, tick: 89);            // deterministic: 89 recorded Step(0.05) commands

var island = net.Islands[net.Islands.Ids[0]];
island.Step(0.05);                                 // tick 90 — the failing one

var inv = island.CheckInvariants(InvariantChecks.All);
// inv.MaxKclResidual = 3.2e-4 at inv.WorstKclNode = NodeId(7)
// → a stamp bug at node 7, localized before reading a single line of solver code.

// And the narrative expectation itself, replayed exactly as CI evaluated it:
double vCap = island.Solution.VoltageAt(lesson.Probe("cap_top"));   // 2.31 V, expected 2.7 ± 0.05
```

Because solves are log entries, "tick 89" means the same thing in CI, locally, and in a
bisect script. The same `.mrb` format is what a Faulted island exports in-game
(`ExportRepro`) — a player's pathological build and a CI failure are the same artifact.

### 3.2 Snapshot/restore round-trip, bit-for-bit (the R6 CI property test)

```csharp
var a = BuildLessonNetlist();                       // any corpus/fuzz netlist
for (int i = 0; i < 100; i++) aIsland.Step(0.05);

Span<byte> state = stackalloc byte[aIsland.StateSizeBytes];
aIsland.SaveState(state);
var buf = new ArrayBufferWriter<byte>(); a.SaveCanonical(buf);

var b = Netlist.FromSnapshot(buf.WrittenSpan);      // fresh netlist, identical IDs
var bIsland = b.Islands[b.Islands.Ids[0]];
bIsland.RestoreState(state);

for (int i = 0; i < 50; i++) {                      // diverge? then restore is lossy — fail loudly
    aIsland.Step(0.05); bIsland.Step(0.05);
    Assert.True(aIsland.Solution.RawVector.SequenceEqual(bIsland.Solution.RawVector)); // double-bit equality
    Assert.Equal(aIsland.StateHash, bIsland.StateHash);
}
```

Bit-for-bit is achievable because state serializes as raw double bits (no decimal text
detour), companion history is part of state, and `EnvFingerprint` scopes the guarantee
to one runtime/arch (§5). This is exactly VS chunk pause/resume and Stationeers
save/load, exercised as a property test over the whole regression corpus.

### 3.3 Emitting an ngspice deck from a live netlist for the oracle differ

```csharp
var deck = SpiceDeck.Emit(net, new SpiceEmitOptions {
    Analysis = SpiceAnalysis.Tran(step: 0.05, stop: 5.0),
    MatchBackwardEuler = true,                       // .options method=gear maxord=1
});
Assert.Empty(deck.Unrepresentable.Span.ToArray());   // lesson circuits must be fully oracle-able
File.WriteAllText(tmp + "/l07.cir", deck.Text);      // deterministic text: Verify-snapshottable golden

RunNgspice(tmp + "/l07.cir", out RawFile raw);       // devshell-pinned binary; hard-fail if absent
foreach (var (node, col) in deck.NodeNames)          // rawfile column ↔ NodeId, no name guessing
    OracleDiff.Assert(raw[col], ManateeTrace(node), relTol: 1e-3);
```

Deck text is deterministic (names derived from IDs, components in ID order), so decks
themselves are Verify snapshot goldens: a stamp refactor that changes emission shows up
as a reviewable text diff before it shows up as an oracle delta.

---

## 4. The zero-alloc story for the steady-state tick loop (R8)

**Where buffers live** — everything sized at tier-3 commit (island build), the one
place allocation is legal:

| Buffer | Owner | Allocated at | Notes |
| --- | --- | --- | --- |
| MNA matrix, LU factors, pivots, RHS, solution vector | `Island` | `TopologyBatch.Dispose()` | in-house dense LU: refactor & solve in place |
| Companion state (cap V, ind I), source phases | `Island` | tier-3 commit | doubles; also the `SaveState` payload |
| Device state | device, as a slice of the island state block | `Instantiate` | one block ⇒ one `SaveState` memcpy |
| Command ring | `CommandRecorder` | construction | fixed capacity × 32 B; overwrite oldest |
| Limit-event / fault-detail buffers | **client** | client init | drained via `Span<T>`; worst-case sized |
| Probe ring buffers (oscilloscope) | `Island` | probe subscription (tier 0, cold) | two-probe UI contract bounds it |

Strings (probe names) exist only at construction; the hot path traffics in ids and
doubles exclusively. `ExportRepro`, `SaveCanonical`, deck emission are declared cold
paths and may allocate — they are never inside a tick.

**The actual per-tick call sequence** (client worker thread; one island; VS AC case —
the Stationeers case is the same shape with `Step(0.5)`):

```csharp
// warmup complete; BenchmarkDotNet MemoryDiagnoser asserts 0 B/op on this whole block
island.BeginTick();                                  // ++Tick, reset TickStats; no alloc

foreach (var dev in devices)                         // client-owned array, index loop
    dev.Tick(in ctx, dt);                            // tier-1 writes → prealloc'd RHS slots;
                                                     // rare tier-2 write just sets a dirty flag
island.Values.SetDrive(altId, in drive);             // mech-network frequency, phase-continuous

var r = island.StepAc(0.050);                        // if tier-2 dirty: one in-place numeric refactor;
                                                     // then N substeps of {RHS update, back-substitution}
                                                     // — commands recorded into the ring, 1 struct write each

int n = island.DrainLimitEvents(_limitBuf);          // _limitBuf: client field, sized at build
for (int i = 0; i < n; i++) MapToGeometry(in _limitBuf[i]);   // tag → voxel/segment via compaction envelope

#if DEBUG
var inv = island.CheckInvariants(InvariantChecks.Kcl | InvariantChecks.Finiteness);  // returns struct
Debug.Assert(inv.MaxKclResidual < 1e-9 && inv.AllFinite);
#endif
double v = island.Solution.VoltageAt(probeNode);     // span read into island-owned memory; no copy
```

Recording costs one 32-byte struct store per command — tier-1 traffic in a 5-substep AC
island is tens of stores per tick, noise next to the back-substitutions. The flight
recorder therefore stays on in production, which is the whole point: the repro exists
*before* the bug is noticed.

---

## 5. Rationale, trade-offs, open questions

**Rationale.**
- *Single choke point + logged solves* turns testing-strategy.md's "every failure
  reproduces from inputs alone" from a discipline into a type-system fact. Fuzz tests,
  the in-game flight recorder, and CI artifacts share one format (`ReproBundle`), one
  replayer, one CLI (`manatee-repro run bundle.mrb --to-tick N --check-invariants`).
- *Sequence-numbered IDs* make replay exact without ID translation, and make canonical
  serialization trivial (ID order *is* the canonical order). Client geometry mapping
  uses opaque `long` tags, keeping strings and game concepts out of core.
- *Boundary exchanges as logged tier-1 commands* solves the nastiest repro problem —
  multi-island coupling bugs — by letting one island replay against its recorded
  environment.
- *Invariant readbacks as API* means the resync backstop, the tablet's educational
  error messages ("current doesn't balance at this node"), and CI asserts are the same
  code path — legible to an SRE as "the system exports its own SLIs."

**Trade-offs.**
- The `Command` union is less idiomatic C# than plain methods. Mitigated: clients only
  ever touch the typed facades; the union is the wire/log format. Cost: facade and
  union must stay in lockstep (one switch, exhaustiveness-tested).
- `TopologyBatch` ceremony for a single add is mild friction — accepted, because it
  makes tier 3 visible and coalesced by construction.
- Ring capacity bounds repro depth. Policy: auto-rebase — when the ring wraps, the
  recorder snapshots `SaveCanonical` + `SaveState` as the new base (cold-path
  allocation, amortized over thousands of ticks). A bundle is then "recent history,"
  which matches how bugs are actually chased.
- Two serializations (canonical vs normalized) is extra surface; the alternative —
  one form trying to serve both replay and structural equality — quietly breaks one of
  them (renumbering breaks logs; hole-preservation breaks equality goldens).
- Bit-for-bit determinism is scoped to an `EnvFingerprint` (same runtime/arch/build).
  Cross-platform runs get tolerance-based comparison, same as the ngspice differ. This
  is honest: .NET JIT FMA contraction varies by architecture.

**Open questions.**
1. Cross-platform FP: do we additionally forbid FMA-contractible patterns in the LU
   kernel (small perf cost) to widen bit-for-bit beyond one fingerprint, or is
   fingerprint-scoped enough? Lean: fingerprint-scoped; CI runs the corpus on both
   x64 and arm64 with tolerance diffing to catch real divergence.
2. Should reduction-layer (geometry-level) edits also be command-logged, or do voxel
   fixtures (testing-strategy.md game-layer rule) suffice? Lean: core logs
   post-reduction commands only; the reduction layer records its own intake fixtures —
   two layers, two logs, each replayable in isolation.
3. Does `SpiceDeck` ship in `Manatee.Core.Diagnostics` inside the main DLL (my lean:
   yes — tiny, no dependencies, and in-game "export my circuit to SPICE" is a
   delightful tablet easter egg) or in a test-only assembly?
4. `StepAc` records one command per substep vs one per tick with the substep schedule
   implied by island config. Lean: one per tick (`StepAc(tickDt)`) since N is
   deterministic from logged config — 5× less ring traffic, replay still exact.
5. Whether `Solution.RawVector` ordering (MNA order) is stable across a tier-2 change
   with pivoting. Lean: yes by construction (ordering fixed at symbolic time), but this
   must be a stated contract because §3.2's bit-for-bit test depends on it.

