# Integrating manatee-core: a worked tutorial

Last updated: 2026-07-07
Status: tutorial; the API canon is docs/api.md. Where this document and api.md
disagree, api.md wins. (The one code-vs-canon divergence this tutorial found —
merge-tick reads on the absorbed side — was adjudicated in canon's favor and
fixed 2026-07-07; see §6 and the appendix, edge 4.)

This is the narrated version of `examples/RevoltWalkthrough/` — a small
console project that builds a miniature Re-Volt-shaped client and runs it
through breaker trips, a cable cut, a save/restore across a merge, and a
blown fuse, printing solver telemetry every tick. **The example project is
the authority for every code block below**: all excerpts are lifted from it,
it compiles against the real core, and its final assertions are the contract
this tutorial teaches. Run it:

```
nix develop -c dotnet run --project examples/RevoltWalkthrough
```

The audience is a game-mod programmer wiring manatee-core into an engine for
the first time. No electrical background is assumed; where circuit math
leaks through, it is stated as an invariant to rely on, not something to
verify. The API reference is docs/api.md; section pointers below (§n) are
into that document unless marked "tutorial".

## 1. The mental model

Three ideas carry everything else.

**The Netlist is a retained document; matrices are derived.** `Netlist` is
the circuit as data — components, nodes, values, limits. The solver's
matrices are compiled *from* it at `Solve`, the way a renderer compiles a
scene graph into draw calls. Every mutation you make writes the document;
the expensive derived state (factorizations, island structure) is rebuilt
lazily, coalesced, once per island per tick, no matter how many mutations
piled up. This is why a write is never lost even when the island it targets
is about to be rebuilt: the rebuild restamps from the document (§17).

**The four verbs are a cost grammar.** Every mutation is one of:

| Verb | Tier | Cost | Typical use |
| --- | --- | --- | --- |
| `Drive` | 1 | back-substitution on a cached factorization | source setpoints, every tick |
| `Adjust` | 2 | numeric refactor, structure reused | device conductance updates |
| `Reconfigure` | 3-lite | coupler membership: merge (cheap) or split (rebuild) | breakers |
| `Edit()` | 3 | structural batch; atomic commit; island rebuilds | construction, cuts |

The names are the audit: grep a client for `.Adjust(`, `.Reconfigure(`,
`.Edit(` and you have found every site that can cost more than tier 1. A
steady-state game tick should be tier ≤2 everywhere, and mostly tier 1 —
`TickStats` (§9, tutorial §9) tells you whether that is actually true.
`Meta.*` is tier 0: free, never touches the matrices.

**Islands are the unit of everything.** An island is a connected component
of the circuit: the unit of solving, of failure (`Faulted`), of scheduling,
of snapshot/restore, and of handle invalidation. Rebuilding one island never
disturbs handles, state, or solutions in any other. Your networks map to
islands; couplers are the only bridges between them.

## 2. Construction: options, wiring policy, partitioning

One `Netlist` per game world, configured once, at birth, by a named bundle:

```csharp
_net = new Netlist(NetlistOptions.Stationeers(Dt));            // Dt = 0.5 s
_graph = new ConductorGraph(_net, GraphOptions.PrePartitioned);
_host = new DeviceHost(_net);
```

`NetlistOptions.Stationeers(dt)` pins three decisions you'd otherwise
rediscover the hard way (§5):

- **Profile = Dc(dt).** Pure DC with storage dynamics. No AC subcycling.
  (Do not add a sine source to a DC-profile netlist casually: it is legal
  but single-sampled per tick — deterministic, heavily undersampled, and
  debug builds warn. AC belongs to the `Mixed` profile.)
- **Wiring = ReferenceBound.** Vanilla Stationeers devices are
  single-terminal; the policy binds every return terminal to its
  partition's reference rail automatically. You never write per-device
  grounding code, and there is deliberately no `BindNegative` call: the
  policy fires on `AddVoltageSource`'s `neg` argument, on `NodeRole.Return`
  nodes, and on device `TerminalSpec.ReturnTerminals`.
- **Partitioning = ClientPartitioned.** Every node belongs to a
  `PartitionKey` (your CableNetwork id), and the API *enforces* that
  partitions never merge except through a coupler: an `Edit` that would
  bridge two partitions with an ordinary component throws
  `PartitionMergeException` and the whole batch rolls back atomically.
  This is "vanilla networks never merge" as a compile-against contract
  rather than adaptor discipline.

`ConductorGraph` is the reduction layer (§19): cable geometry goes in,
a minimal netlist comes out, and it maintains the maps that make limit
events attributable back to your segments. `DeviceHost` is the built-in
serial driver for stock devices (§18).

## 3. Intake: ConductorGraph, BulkBuild, and the two keys

Cable runs go through the reduction layer, never through raw `Edit`:

```csharp
using (var b = _graph.BeginBulkBuild(expectedSegments: 2 * BusLen))
    foreach (var n in new[] { NetB, NetC })
        for (var i = 0; i < BusLen; i++)
        {
            var limits = n == NetC && i == 0
                ? new LimitSpec(6.0, 0.0, 0.0, default)     // a 6 A fuse segment
                : default;
            b.AddSegment(Seg(n, i), J(n, i), J(n, i + 1),
                         new ConductorSpec(0.05, 1.0, limits), Part(n));
        }
```

- `BeginBulkBuild` is the load path: segments stage, and Dispose runs ONE
  compaction pass. Outside a bulk scope, each `AddSegment`/`RemoveSegment`
  recompacts eagerly (correct, currently O(graph) — a perf item, not a
  semantic one).
- Under `ClientPartitioned` the `partition` argument is **mandatory** on
  every `AddSegment` — the default value throws. The partition's reference
  rail is created on first use, so `_graph.ReferenceNode(part)` always
  resolves afterwards.
- Series chains collapse: 12 segments with two taps become 3 equivalent
  resistors. You still address everything by *your* keys:
  `_graph.PortNode(junctionKey)` gives the live node where a device
  attaches; limits ride through as the collapsed element's envelope, and
  `Attribute` maps a trip back to the culprit segment (tutorial §8).

**ExternalKey vs StateKey — why both exist (§3).** Both are 128-bit
client-stable identities, and both are mandatory parameters where they
apply, but they answer different questions:

- `ExternalKey` = *topological* identity: "which component/node is this?"
  Mandatory on every Add. It is the only identity that survives an island
  rebuild, and `TryResolve(key, out ...)` is the re-resolution path for
  everything you cache.
- `StateKey` = identity of a *unit of serializable dynamic state*: "whose
  saved blob is this?" One device (one StateKey) may own many components
  (many ExternalKeys) — a battery is an EMF source plus a resistor plus an
  SoC integrator, but one blob. Snapshot/restore keys on StateKey and
  nothing else. For a bare one-component device, `StateKey.From(key)`
  reuses the ExternalKey bits.

In Re-Volt both usually pack a RefId: `new ExternalKey(refId)`. The example
uses disjoint synthetic ranges — the discipline that matters is uniqueness
per netlist (debug-asserted) and determinism across sessions, because keys
are how a reloaded world finds its components again.

## 4. Devices: two populations, one contract

The contract: **every readback must hit a live handle, every tick.** How
hard that is depends on whether the device's island ever rebuilds.

**Built-in devices on static islands.** The stock `AdaptedLoad` (R18
constant-power load with brownout hysteresis, staggered rejoin, lockout,
and an anti-free-energy ledger) is driven by `DeviceHost`:

```csharp
var load = new AdaptedLoad(advertisedWatts: 200.0, gMin: 1e-6, gMax: 1e3,
    brownoutLowVolts: 50.0, brownoutHighVolts: 70.0,
    lockoutCount: 100, staggerBaseTicks: 2, staggerSpreadTicks: 6);
_host.Add(load, new[] { _quietTaps[k], _quietRail },
          LoadKey(NetA, k), StateKey.From(LoadKey(NetA, k)));
```

`DeviceHost.Add` opens one Edit, builds the device's components under
derived keys (`baseKey.Derive(ordinal)` — deterministic, which is what
makes restore-by-key work for multi-component devices), and registers its
state. **But**: built-in devices cache their component *and terminal-node*
handles privately and expose no re-pin surface (§18). They are only safe on
islands whose topology never churns. Put them on raw, static networks — in
the example, the quiet network A, built with one plain `Edit`.

**Adaptors on churning islands.** Anything attached to a cable graph that
suffers cuts and breaker trips must be able to re-resolve every handle it
holds. That is `TutorialLoad` (examples/RevoltWalkthrough/TutorialLoad.cs,
a commented copy of the test suite's `RevoltAdaptor`): identity lives in
keys, handles are refreshed by `Repin`:

```csharp
public void Repin(Netlist net, ConductorGraph graph)
{
    if (_retired) return;
    if (net.TryResolve(Key, out var c))
        _r = new ResistorId(c.Slot, c.Gen, c.Net);
    _pos = graph.PortNode(Port);
    _neg = graph.ReferenceNode(Partition);
    net.RegisterDeviceState(_pos, this);     // re-anchor the state unit
}
```

Re-pinning is pure key re-resolution — tier 0, no structural edit, so it
can never trigger the rebuild it is recovering from, and it is idempotent.

**What Tick may do is capped by type.** A device's per-tick update receives
a `DeviceTickContext`, which exposes `Drive` and `Adjust` overloads and
*nothing else* (§18). A structural change inside the device loop does not
compile. `ctx.Previous` is the last published solution — deterministic for
the device's own island (its drive phase precedes its island's step).

The per-tick body of the constant-power adaptor is the R18 linearization —
present the load as a conductance computed from last tick's voltage:

```csharp
var v = ctx.Previous.Voltage(_pos) - ctx.Previous.Voltage(_neg);
// shed to GMin if |v| reads dead (see §6 and the appendix — load-bearing!)
g = Watts / (absV * absV);                   // then clamp to [GMin, GMax]
ctx.Adjust(_r, 1.0 / g);                     // ε-no-op once converged
```

Once the voltage settles, successive `Adjust` calls land within
`AdjustEpsilon` of the stamped value and degrade to free tier-0 document
writes — `TickStats.AdjustNoOps` counts them, and a converged fleet costs
zero refactorizations. You do not write convergence detection; the ε-gate
is the solver's job (§9).

**Retiring.** When the game removes a device: one `Edit` that removes its
components (its handle dies at commit), `UnregisterDeviceState`, and never
touch it again. Surviving siblings on that island keep working — their
handles die only at the next `Solve`, and re-pin covers them (tutorial §6).

## 5. The tick loop

This is the heart of the integration. One global body per game power tick,
in this exact order (§17, §22.a):

```csharp
// 1. TOPOLOGY: apply the game's queued world changes first, before the guard.
var hadTopology = _pending.Count > 0;                    // capture BEFORE the drain — always false after it
while (_pending.Count > 0)                               // Reconfigure / RemoveSegment / Edit
{
    var (label, op) = _pending.Dequeue();
    op();
}

// 2. RE-PIN: drain membership changes ONCE per global tick.
var changes = _net.Islands.DrainChanges(_chgBuf, out var lost);
var repin = lost || changes > 0 || hadTopology;
if (repin) RepinActive();                                // lost==true ⇒ re-pin EVERYTHING

// 3. DRIVE, under the guard: devices may only Drive/Adjust.
using (_net.EnterSteadyState())
{
    _host.Tick(Dt);
    var ctx = _net.TickContext(Dt);
    for (var i = 0; i < _activeLoads.Count; i++) _activeLoads[i].Tick(in ctx);
}

// 4. ONE Solve for every dirty island.
_net.Solve(clock);

// 4b. CHURN-TICK RE-PIN: rebuilds ran INSIDE Solve and reissued handles.
if (repin) RepinActive();

// 5. READBACK: apply actuals per partition, drain limit events, telemetry.
ApplyStateAndPostTick();
```

Why this order — what breaks if you shuffle it:

1. **Topology first.** Breaker trips and cable cuts must land before
   devices compute, so this tick's solve reflects this tick's world. A
   breaker `Reconfigure` through a *cached* `CouplerId` is always safe here
   — coupler handles are document-stable and survive every rebuild (§16),
   which is precisely why the topology phase may run before re-pin.
2. **Re-pin second.** Last tick's Solve may have rebuilt islands (a cut
   you applied last tick, a melt from the readback phase); `DrainChanges`
   is the game client's one notification channel — never the journal, which
   serves the reduction layer. If `lost == true`, the fixed ring
   overflowed and you are *obligated* to a full re-pin of every held handle
   (§11); the example's `RepinActive` is already exactly that, because
   re-pinning is cheap and idempotent.
3. **Drive under the guard.** `EnterSteadyState()` makes tier-3 impossible
   for the region: `Edit()` throws in all build modes; a `Reconfigure` is
   an assert in debug and deferred-to-guard-Dispose (same tick, before
   Solve, counted in `TickStats.DeferredStructuralOps`) in release (§8).
   The guard is the machine-checked version of "nobody rebuilds the world
   inside the device loop", and it arms the allocation tripwire where the
   runtime supports one.
4. **One Solve.** All dirty islands, all coalesced work: N cuts on one
   island are still one rebuild. Never solve per network — the execution
   model is one global body per game tick (§22.a; a per-network solve
   mis-drains DrainChanges and multiplies cost).
5. **Churn-tick re-pin (4b) — the most-forgotten step.** Island rebuilds
   run *inside* `Solve` and reissue every member handle. On any tick that
   touched topology, the handles you re-pinned in phase 2 are already stale
   again by phase 5. Re-resolve before reading. The test suite proves this
   is load-bearing in both directions: with the re-pin the whole scripted
   run holds `StaleHandleReads == 0`; without it, the cut tick trips the
   counter (`FakeRevoltClientTests.Skipping_the_post_solve_repin_...`).
6. **Readback last.** Apply actual powers from `_net.Solution` to game
   devices (each network touches only its own partition's devices), drain
   limit events, record `LastTickStats`. Structural reactions decided here
   (pop a fuse, open a breaker) are legal immediately — they coalesce into
   the *next* tick's Solve — or can be queued into the next topology phase.

## 6. Handles: the survival rules, digested

Handles are 12-byte generational ids: cheap to hold, checked on every use.
The full table is §16; the digest a client needs:

- **Merge preserves component/node handles.** A breaker `Close` splices two
  islands; nothing is renumbered except the absorbed `IslandId`.
- **Rebuild invalidates that island's component/node handles** — and
  rebuilds are triggered by any removal, a coupler `Open`, or a resync. The
  reissue happens **at the next Solve**, not at the mutation, so handles
  stay usable for `Drive`/`Adjust` in the window between (those writes go
  to the document and are never lost).
- **Couplers and probes are document-stable.** A `CouplerId`/`ProbeId`
  survives every rebuild and merge; only explicit removal (or whole-netlist
  reload) kills it. Cache your breaker handles forever.
- **Keys survive everything.** `TryResolve*(key)` is the universal recovery
  path; it returns `false` on a miss and never throws.
- **In release, stale use is a defined, counted no-op** — reads return 0,
  mutations do nothing, and `TickStats.StaleHandleReads` increments. A
  shipped game never crashes on a stale handle; instead **watch that
  counter — nonzero means a re-pin bug**, and the example asserts it is
  zero on every tick. (With `DebugLevel.Asserts`, stale use throws
  `StaleHandleException` naming the handle and the journal event that
  killed it.)

**Merge-tick reads hold last-good on BOTH sides (fixed 2026-07-07).** An
earlier build violated api.md §17 rule 4 on the absorbed side of a merge:
on the tick a breaker `Reconfigure(Closed)` was applied, the absorbed
island's nodes read 0.0 through perfectly valid handles until the merged
island first published — and a naive `G = P/V²` adaptor that trusted the
zero stamped a fictional dead short (this example's first draft popped the
6 A fuse with a phantom 597 A surge on the merge tick). That is fixed and
the canon promise now holds as written: through the merge window, the
**survivor** keeps its last-good published vector and the **absorbed**
island's nodes carry their pre-merge last-good potentials — and its
voltage sources their branch currents and powers — across the relabel;
both sides read those values via `Solution`/`ctx.Previous` (with
`IsLive == false`) until the merged island first publishes at the next
Solve. A **Faulted** island reads de-energized 0 only *while it is
Faulted* (api.md §17.4/§20): the merge itself flips the union to Dirty, so
from the merge commit a previously-Faulted side reads its last
successfully *published* values again — on either union orientation — and
0 if it never published. No fault output can leak that way; failed solves
never publish. Adaptors should still shed below a live floor — genuinely
dead, Faulted, or never-published buses do legitimately read ~0 V
(sharp-edges appendix, edge 4):

```csharp
if (absV < LiveFloorVolts)   // genuinely dead/Faulted bus: shed, never seek GMax
    g = GMin;
```

The scripted run shows the contract holding: no dip at all on the merge
tick (`B= 400W C= 400W`) — both sides' loads keep their converged
conductance through the window, and the run's telemetry check pins it.

## 7. Save/load

**Saving** is per island: `island.Snapshot(IBufferWriter<byte>)` writes one
versioned blob of every state unit in the island — capacitor voltages,
device blobs (`IDeviceStateUnit.Save`), melting integrals, coupler
runtimes — each keyed by `StateKey` (§14). `SnapshotSize` is stable between
topology-changing ops; re-read it after any `IslandChange`.

**Restore is additive (§14, binding).** A blob overwrites exactly the units
whose `StateKey` it carries and leaves every other unit untouched — it
never resets a unit it has no entry for. Because StateKeys are
netlist-global while blobs are per-island, this composes across topology
drift between save and load:

- **Merged since save:** offer each old blob to the merged island in turn;
  each restores its own keys; nothing clobbers.
- **Split since save:** offer the *same* blob to every resulting island;
  each takes its matches and reports the rest as `OrphansInBlob` — expected
  in this pattern, not an error.

The example does the merged case live — snapshot both islands, close the
breaker, then:

```csharp
var isl = _net.Islands.Of(_graph.PortNode(J(NetB, 0)));   // the merged island
var rB = isl.Restore(_blobB);
var rC = isl.Restore(_blobC);
var coldStarted = isl.StateUnitCount - (rB.Matched + rC.Matched);
```

**Coverage is aggregate, never per call.** After all blobs are offered,
`sum(Matched)` against `StateUnitCount` is the number of genuinely
cold-started units. Log that residue; do not treat any single call's
misses as failure.

The whole-netlist forms (`SaveCanonical`/`FromCanonical`) rebuild
everything including probe slots — after `FromCanonical`, re-resolve
couplers and probes by key too. Geometry itself is *not* serialized
anywhere in the core: your world data is the truth, you re-drive it into a
fresh `ConductorGraph` at load, and all derived identities (equivalent
resistors, probe keys) mint deterministically from it, which is what makes
the re-driven world match its saved state (§19).

## 8. When things go wrong

Three channels, never blended (§20): exceptions indict *your code*
(contract misuse — nested Edit, cross-partition merge, tier-3 in the
guard); `Faulted` islands are *legal-but-degenerate circuits* (players
build these; the game must not crash); polled events are *normal runtime
signals* (limits, membership changes).

**Faulted islands.** A circuit the solver cannot solve (contradictory
sources, singular after all remedies) marks its island `Faulted`; every
read of it is de-energized (`IsLive == false`, voltages and currents 0)
for as long as the status stands — enforced at the read path, api.md
§17.4/§20. Neighbors keep solving. `island.Fault` names the worst
node/component — feed it to your "something smells wrong" UI. Any tier-2/3
change to the island marks it Dirty again and it retries automatically
(from that flip until the retry publishes, reads revert to the island's
last successfully published values — fault output itself is never
published, so it is never readable). The example's readback checks
status first and would run a scalar fallback, mirroring Re-Volt's plan:

```csharp
if (isl.Status == IslandStatus.Faulted) { /* scalar fallback + log isl.Fault */ continue; }
```

**Limit events.** The solver *never* mutates the circuit on a limit (R7) —
it reports, you act. Events are drained per island from a fixed ring
(overflow is counted, not grown — use the `out long dropped` overload if
you might skip drains), and the reduction layer attributes an event on a
collapsed equivalent back to the culprit segment:

```csharp
var got = isl.DrainLimitEvents(_limBuf);
for (var k = 0; k < got; k++)
{
    if (_limBuf[k].Kind != LimitKind.OverCurrent) continue;
    if (!_graph.Attribute(in _limBuf[k], out var hit)) continue;
    _graph.RemoveSegment(hit.Segment);       // the fuse melts; rebuild coalesces to next Solve
}
```

In the example run this fires exactly once: driving network C to 30 V makes
its constant-power loads pull ~12 A through the 6 A fuse segment, the event
names it, and the melt isolates the loads (~0 W thereafter) — the hazard
traces to a player-legible cause.

**Invariants as a debugging tool.** `island.CheckInvariants(...)` is the
same machinery CI uses, callable in dev builds after suspect solves:
`MaxKclResidual` answers "which node leaks current" (a stamp bug indicts a
specific component), `AllFinite` catches NaN escapes (release policy: no
NaN ever leaves the API — a non-finite solve faults the island instead).

## 9. The performance contract

The steady-state model is executable, not aspirational (§9):

- **Steady state lives in tier 1.** A converged base costs RHS solves only.
  The standing CI assertion — and the example's final check — is
  `Refactorizations == 0 && IslandRebuilds == 0` on quiet ticks.
- **TickStats is the truth.** Log `LastTickStats` per tick; it is designed
  to be cheap enough to always record. `Refactorizations > 0` in steady
  state = some Adjust is not converging (check your device's math before
  suspecting the solver — the ε-gate should be absorbing it).
  `IslandRebuilds > 0` = a coupler or cut fired. `StaleHandleReads > 0` =
  a re-pin bug, always. `AdjustNoOps` = your converged devices, visible.
- **Zero allocation in tiers 0–2 after warmup** is a core guarantee (R8),
  enforced by BenchmarkDotNet in the core's CI. Your side of the bargain:
  allocate drain buffers and worker state at shape time (the example's
  `_chgBuf`/`_limBuf` fields), never in the tick; no LINQ or closures in
  the loop; `WaveformTap.Attach` and friends are setup-time calls.
- **`EnterSteadyState` around the drive phase** costs nothing (a ref
  struct) and converts "someone did something structural mid-loop" from a
  heisenbug into an assert/counter.
- **Ask before paying:** `CostOfAdjust`/`CostOfReconfigure` classify a
  mutation without applying it, and `EditReceipt.EstimatedRebuildDim` sizes
  a coming rebuild — enough to decide "this tick or background task".

A converged example tick prints `refac=0 rhs=0 noop=6 stale=0`: six devices
ticking, six ε-no-ops, nothing else moving. That line is what "healthy"
looks like; deviations name their cause.

## Appendix: sharp edges

Each of these is a real scar — from the overnight build's review ledger or
from building the example itself. One line of what, one of why.

1. **Forgotten churn-tick re-pin.** Rebuilds run *inside* Solve; handles
   re-pinned before Solve on a topology tick are stale again after it.
   Re-pin post-Solve too, before readback (tutorial §5 step 4b).
2. **`DrainChanges` `lost == true` is an obligation, not a warning.** The
   change ring overflowed; you missed events; re-resolve *every* held
   handle by key (§11). The example folds this into one idempotent
   `RepinActive`.
3. **Ignoring `IsLive`.** Building/Dirty/Faulted islands read stale or
   zero values. Gate meaningful readbacks (UI, game logic, adaptor math)
   on it, and write adaptors to tolerate a zero read (edge 4).
4. **Shed below a live floor — genuinely dead or Faulted buses read 0.**
   Merge-tick reads hold last-good on both sides, potentials and source
   currents alike (fixed 2026-07-07, api.md §17.4; tutorial §6), so a
   breaker Close no longer manufactures zeros — but a bus can still
   legitimately read ~0 V: a de-energized island, an island that is
   Faulted *right now* (the de-energized read is status-scoped; a merge
   or retry flips it to Dirty and its last-published values read again),
   or the window before an island's first publish. A G = P/V² adaptor
   that seeks GMax at V ≈ 0 stamps a fictional short and pops real fuses;
   keep the live-floor shed (`if (absV < LiveFloorVolts) g = GMin;`).
5. **Caching spans across ticks.** `Islands.Ids`, `RawVector`, drain spans
   — all are views over owner-managed buffers with defined validity
   windows (§21). Copy scalars out; never store a span in a field.
6. **`RawVector` during a foreign island's Step.** After a publish the old
   buffer is recycled as the next back buffer — a held span can observe a
   mixed-generation vector. Read it only for a non-stepping island (own
   island, or under the readback barrier) (§10/§21).
7. **`Edit()` inside the steady-state guard throws — in all build modes.**
   Release defers `Reconfigure` to guard-Dispose (same tick, counted), but
   a live edit scope cannot be deferred (§8). Structural reactions belong
   in the topology or readback phase.
8. **A sine source in a non-Mixed profile is single-sampled.** Legal,
   deterministic, heavily undersampled; debug warns at Add. AC islands
   need `SolverProfile.Mixed` (§5).
9. **`RelaxationAlpha` on transformer couplers clamps at 0.7.** Configured
   α up to 1.0 is accepted; effective damping is `min(α, 0.7)` — the
   proven stability bound for gain-capable loops (§7). Don't tune past it
   expecting faster settling.
10. **Restore is additive, so cold-start detection is aggregate.** No
    single `Restore` call can tell you what cold-started; only
    `sum(Matched)` vs `StateUnitCount` after all blobs are offered can
    (§14, tutorial §7).
11. **Probe keys are deterministic — re-derive to re-resolve.**
    `ConductorGraph.ProbeKey(segment, along, ordinal)` mints the same key
    for the same geometry; after a drift `Resync` probes survive re-aimed,
    and only `FromCanonical` forces `TryResolveProbe` by that re-derived
    key (§13).
12. **Every `AddSegment` needs its partition under `ClientPartitioned`.**
    The default `PartitionKey` throws at add time; a junction claimed by
    two partitions throws `PartitionMergeException` (§19). Bulk builds
    check at Dispose — load time, never first Solve.
13. **ε-no-op Adjusts are free — rely on it.** Converged G = P/V² loops
    cost tier 0 without any client-side "did it change enough" logic, and
    `CostOfAdjust` answers Metadata/Conductance if a scheduler wants to
    know first (§9). Do not build your own epsilon gate on top.
14. **GC-counter allocation assertions need isolation.** The thread
    allocation counter over-reports by up to ~8 KB under concurrent
    compacting GC (a real runtime phenomenon, root-caused during the
    build); the in-process sentinel is best-effort and self-disarms on
    Mono. The binding zero-alloc gate is BenchmarkDotNet in CI — see
    testing-strategy.md's note before writing your own.
15. **Resolve graph nodes before you need them, not mid-Edit.**
    `PortNode` on a junction not yet protected triggers a recompaction,
    which opens its own Edit — inside your open `Edit` scope that is a
    nested-edit `InvalidOperationException`; and each new protection can
    reissue nodes you resolved a line earlier. Protect-then-resolve: call
    `PortNode` for all attachment points first, then resolve them all
    again, then open the Edit (the example's two-pass loop; also why
    device Builds are followed by a first-boot re-pin).
16. **Drain the construction backlog before the first game tick.** The
    build burst overflows the change ring; the first `DrainChanges` then
    reports `lost == true` and forces a spurious (correct, but confusing)
    full re-pin. Loop `DrainChanges` empty at the end of load
    (tutorial: `FirstSolve`).
17. **Moving a breaker's ports is one batch: remove + re-add the same
    key.** Coupler ports are fixed at `AddCoupler`, so re-routing a
    breaker (a network merge moved one of its sides) is `RemoveCoupler` +
    `AddCoupler` with the *same* `ExternalKey`/`StateKey`, legal in one
    atomic batch — removals apply before additions inside a commit
    (api.md §17.1, decision log #28), so the key resolves to the new
    handle afterward. For a galvanic breaker nothing else carries over in
    core: Open/Closed is yours to re-apply, and a re-added breaker
    defaults **Closed** — if it was tripped open, `Reconfigure(newId,
    Open)` immediately after the commit, before the next `Solve` (the
    transient union and the split fold into one coalesced rebuild). Trip
    accumulation lives in your adaptor, keyed by the game device; it
    survives untouched — just cache the new `CouplerId`. A *boundary*
    coupler (transformer/converter) does carry core state
    (`CouplerRuntime` scalars): snapshot the A-side island before the
    edit and restore after — same-`StateKey` matching re-attaches it
    (§7 of this tutorial).
