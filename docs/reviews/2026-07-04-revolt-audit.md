# Re-Volt Codebase Audit — Findings Report

_Generated 2026-07-04 by a multi-agent audit (10 Opus finder agents, one per
subsystem area; every bug/security finding then independently adversarially
verified). 60 raw findings → 52 kept (46 confirmed, 6 uncertain), 1 refuted._

_This codebase has no formal design spec — Sukasa keeps intent in his head.
Findings are graded by **certainty** (definitely-bug / likely-bug /
unclear-intent) rather than checked against a spec — see "How to read this
report" below._

_Revision: commit `d6421bb3b5a8af30471e4d03a0c81531ecd6b2ae` (github.com/Sukasa/ReVolt, branch main). Scope: whole codebase._

<!-- audit-revision
mode: whole
commit: d6421bb3b5a8af30471e4d03a0c81531ecd6b2ae
generated: 2026-07-04
-->

## How to read this report

There's no design doc to check this codebase against — you (Sukasa) hold the
intent. So instead of the usual "spec says X, code does Y" framing, every
finding is graded on how confident an outside reader can be that something is
actually wrong:

- **Definitely Bugs** — the code contradicts itself, contradicts its own
  comments, or is a plain incomplete stub. No credible reading makes the
  current behavior intentional.
- **Likely Bugs** — looks wrong against the Steam feature description or
  ordinary correctness expectations (missing bounds check, stale cache,
  non-idempotent math), but a deliberate simplification can't be fully ruled
  out.
- **Unclear Intent** — genuinely ambiguous whether this is a bug or a
  deliberate (if surprising) design call. These are "ask yourself" flags, not
  assertions that something is wrong — treat them as a five-second gut-check
  list, not a to-do list.

Within each bucket: 🔴 High / 🟠 Medium / ⚪ Low. There are **zero High-severity
items in the bug grid** — the one High-severity finding in this report is an
integration gap (see the dedicated section at the end), not a live bug.

Every finder was also told this is hand-authored Harmony/BepInEx mod code,
not AI-generated — the usual "AI code smell" framing was dropped in favor of
a generic architecture/async/logic/quality lens (see the adapted rubric used
for this run, filed alongside the workflow script if you want it).

## Executive summary

Overall the codebase is in solid shape for its stated "feature-complete
maintenance" status — no crashes-on-every-load, no obvious exploit reopened,
nothing that screams "this was never tested." The findings skew toward edge
cases: topology-change races, cache-invalidation gaps (a value cached on
"count changed" instead of "anything changed" recurs three separate times —
`RevoltTick.PowerData`, `HeavyBreaker.DirtyLists`, `HeavyBreaker`'s power-vs-data
connection-refresh asymmetry), and a handful of async/Harmony patterns that
work today only because nothing currently exercises the unguarded path.

The two things worth your attention regardless of bucket:

1. **`NetworkExportCommand` is a complete stub** (#48/#49) — empty loop body,
   returns `null`. This is the one item that's both a clear bug *and* a
   blocker: Manatee's design doc names this export format as its graph-
   construction intake, and right now it produces nothing, and even the DTO
   shape is missing fields the doc says you and the design team already
   agreed on (port identity, cable gauge, fuses-as-edges). See the
   integration section.
2. **A recurring shape**: "rebuild triggered on count change" instead of "on
   any change" (`PowerData`, `DirtyLists`) and "flag set on some paths but not
   the symmetric one" (`HeavyBreaker`'s data vs. power connection guards,
   `_allFuses` vs. `_allCables` clearing, `CanLogicRead`/`CanLogicWrite` on
   `LoadCenter`). None of these are dramatic individually, but the pattern
   suggests it's worth a dedicated pass specifically hunting for "did I
   handle both directions of this" the next time you touch cache/dirty-flag
   code.

### Fix-first order

1. 🔴 **NetworkExport DTO + command are both unbuilt** — `Assets/Scripts/Commands/NetworkExportCommand.cs` (`Execute`) / `Assets/Scripts/NetworkExport.cs`. Blocks the Manatee graph-construction intake entirely; see integration section for the agreed-but-missing fields.
2. 🟠 **`_allFuses` never cleared on rebuild** — `RevoltTick.cs:94` (`Initialize_New`). Stale/duplicate fuse data after any topology edit; one-line fix.
3. 🟠 **`PowerData` staleness guard only checks length** — `RevoltTick.cs:124`. A same-count device swap misapplies LoadCenter category gating to the wrong device.
4. 🟠 **`DeserializeSave` indexes `ConnectionRefs[0..2]` with no length check** — `CircuitBreaker.cs:576`. A malformed/old save throws `IndexOutOfRangeException` and fails to load that Thing.
5. 🟠 **`DeferredCableRegistration` has no post-yield guards** — `CablePatches.cs:45`. A cable destroyed in the intervening frame gets merged into a network anyway.
6. 🟠 **`dirtyProviderList` not set on the pure-shrink path** — `RevoltTick.cs:264`. Reflection-pushed `Providers[]`/`InputOutputDevices[]` arrays go stale, which also feeds a wrong recursive-provider check when that safety net is enabled.
7. 🟠 **`MatchCables` is accidentally O(open-ends²)** — `CableTray.cs:104`. Dead loop variable means the same full scan re-runs once per open end on the hot rebuild path.
8. ⚪ **"Enable Recursive Network Check" does nothing** — `RevoltTick.cs:273` / `PowerTickPatches.cs`. The reverse-patched detection populates lists `ApplyState_New` never reads; the config option's own description promises force-burning a cable and it doesn't happen.
9. ⚪→🟠 **Two guard-asymmetries worth a deliberate look**: `CablePatches`' exception-unsafe static-flag handshake (#5) and `HeavyBreaker`'s missing data-network membership fallback (#17) — both Medium, both "Unclear Intent," both plausible sources of a hard-to-repro "my breaker/tray stopped working until I reconnected it" bug report.

## Bucket × severity index

| Bucket | 🔴 | 🟠 | ⚪ | Total |
|---|---:|---:|---:|---:|
| Definitely Bugs | 0 | 1 | 1 | 2 |
| Likely Bugs | 0 | 6 | 6 | 12 |
| Unclear Intent | 0 | 2 | 22 | 24 |
| **Total (bug grid)** | **0** | **9** | **29** | **38** |
| Manatee integration points *(not in grid)* | 1 | 2 | 11 | 14 |

---

# Definitely Bugs

### 🟠 MEDIUM · `_allFuses` is never cleared on a dirty rebuild — fuses accumulate and go stale

**`Assets/Scripts/RevoltTick.cs:94`** · _bug_ · Tick Engine

`Initialize_New` clears `Devices`, `Fuses`, and `_allCables` at the top of
the dirty-rebuild path, but there is no matching `_allFuses.Clear()`. Every
rebuild re-adds the network's current fuses into the already-populated
`_allFuses`, so removed fuses stay referenced forever and surviving fuses get
duplicated into their `PowerBreak` bucket. `TestBlowFuse` reads
`_allFuses.Keys[0]` as the weakest-fuse trip threshold and
`_allFuses.Values[0].Pick()` for which fuse actually blows — both now see
stale/duplicate data.

- **Steam feature / doc touched:** "Fuses still work... that fuse will
  ALWAYS blow instead of a cable."
- **Suggested fix:** Add `_allFuses.Clear();` next to the existing
  `_allCables.Clear();` at line 94.

<details><summary>Verification trail</summary>

Confirmed by direct trace: `_allFuses` (declared line 30) is only ever Added
to (108-113) and read (185-193), never Cleared. The asymmetry with
`_allCables.Clear()` at line 94 is real and the failure scenario (a fuse
replaced by a higher-rated one leaves the old, lower threshold active) is
reachable.

</details>

### ⚪ LOW · Dead variable `count_before` in the network-registration merge path

**`Assets/Scripts/Patches/CableNetworkPatches.cs:144`** · _quality_ · Cable Network Graph

`var count_before = __result.Count;` is assigned and never read — likely a
leftover from an intended check (e.g. whether tray-matching actually added a
network). Harmless, but dead code on a hot registration path, and the kind of
loose end worth cleaning up before it's mistaken for load-bearing.

- **Suggested fix:** Remove the line, or finish whatever check it was meant
  to gate.

---

# Likely Bugs

### 🟠 MEDIUM · `PowerData` device classification is only rebuilt on a device-count change

**`Assets/Scripts/RevoltTick.cs:124`** · _bug_ · Tick Engine

`PowerData` (the per-device `PowerClass` cache driving LoadCenter category
gating) rebuilds only when `PowerData is null || PowerData.Length !=
Devices.Count`. `Devices` is fully repopulated from
`CableNetwork.PowerDeviceList` on every dirty rebuild — if the *count* is
unchanged but composition or order differs (e.g. one Door deconstructed and
one Light built the same tick), `PowerData[idx].Category` no longer matches
`Devices[idx]`. A LoadCenter toggle then force-unpowers/powers the wrong
device, and per-category logic sums are wrong.

- **Steam feature / doc touched:** "Turn your lights, atmospherics, doors,
  equipment, or logic devices on and off en masse."
- **Suggested fix:** Rebuild `PowerData` whenever dirty (it only runs on the
  dirty path anyway) instead of gating on length equality.

<details><summary>Verification trail</summary>

Confirmed: the Length-only guard at 124-125 is real; `Category` is consumed
index-keyed at 220/334/340/344 and shared to `LoadCenter` at 136. Requires a
same-tick, same-count composition/order change to trigger — plausible but not
the common case, hence Medium rather than High.

</details>

### 🟠 MEDIUM · `DeferredCableRegistration` has no guards after its one-frame yield

**`Assets/Scripts/Patches/CablePatches.cs:45`** · _bug_ · Cable Network Graph

Base `Cable.OnRegistered` guards its work with `GameState != Loading &&
RunSimulation`, and `RebuildNetwork` skips cables with `IsBeingDestroyed`.
`DeferredCableRegistration` awaits one frame (`UniTask.Yield()`) then
unconditionally calls `CableNetwork.Merge(...)`/`.Add(cable)` with **no**
re-check of any of those conditions. A cable destroyed or the sim torn down
in that one frame gets merged into a network anyway.

- **Suggested fix:** After the await, bail if `cable == null ||
  cable.IsBeingDestroyed || GameManager.GameState == GameState.Loading ||
  !GameManager.RunSimulation`.

<details><summary>Verification trail</summary>

Confirmed: lines 45-54 have zero guards, while the same file's
`BeforeOnRegistered` prefix (24-25) and `CableNetworkPatches.RebuildNetwork`
(77) both guard on exactly these conditions — establishing this as the
project's own pattern, absent here.

</details>

### 🟠 MEDIUM · `MatchCables` re-scans every open end once per open end (dead loop variable)

**`Assets/Scripts/Prefabs/CableTray.cs:104`** · _bug_ · Cable Tray

The outer `for (var index = Tray.OpenEnds.Count - 1; index >= 0; index--)`
never references `index`. Its body resets `count = 0` and re-runs the
*entire* `FillConnected<Cable>` scan every pass, producing the identical
cable set `OpenEnds.Count` times — correctness survives only via the
`!Result.Contains(...)` dedup guard. This is on the hot rebuild path
(`RebuildNetworkPatch` calls `MatchCables` per connected tray of every
dequeued cable), so the redundancy multiplies on large tray runs.

- **Steam feature / doc touched:** "Trays connect cables with matching
  colour and watt limits."
- **Suggested fix:** Drop the outer loop; call `FillConnected<Cable>` once
  per tray and iterate the result a single time.

<details><summary>Verification trail</summary>

Confirmed: `index` is unused in the loop body; the same file's own
single-call pattern at lines 46-55/65-69 shows the intended shape.

</details>

### 🟠 MEDIUM · `DeserializeSave` indexes `ConnectionRefs[0..2]` without a length check

**`Assets/Scripts/Prefabs/CircuitBreaker.cs:576`** · _bug_ · Circuit Breaker Core

The guard checks only `saveData.ConnectionRefs != null`, then unconditionally
reads indices 0, 1, 2. A normally-serialized breaker always writes length 3,
so the happy path is fine — but XML deserialization of an older-version
save, a hand-edited save, or a partially-written array yields a shorter
non-null array, throwing `IndexOutOfRangeException` and failing to load that
Thing.

- **Suggested fix:** Guard on length too:
  `saveData.ConnectionRefs.Length >= 3`, or copy `Math.Min(3, Length)`
  elements.

<details><summary>Verification trail</summary>

Confirmed as described; the parallel `SerializeOnJoin`/`DeserializeOnJoin`
path uses fixed-count reads and is safe — only the XML save path is
data-driven and fragile.

</details>

### 🟠 MEDIUM · `NetworkExportCommand.Execute` is a complete stub (empty loop, returns `null`)

**`Assets/Scripts/Commands/NetworkExportCommand.cs:13`** · _bug_ · Network Export

Registered with real help text ("Dump all cable networks to a .json file"),
but `Execute` builds an empty `List<NetworkExport>`, loops over every
`CableNetwork` with a **completely empty body**, and returns `null`. No DTO
is ever populated, no serialization happens, no file is written. This is
also the entire producer for the data Manatee's design doc names as its
graph-construction intake — see the integration section for the bigger
picture (#49).

- **Suggested fix:** Implement the loop (populate `CablesByType`/
  `FusesByType`/`Devices` per network, recovering per-cable `Connections`
  adjacency from `OpenEnds` before the network builder discards it),
  serialize to JSON and write a file — or remove the dead console verb if
  it's been superseded.

<details><summary>Verification trail</summary>

Confirmed directly: lines 11 (empty list), 13-16 (empty `foreach` body), 18
(`return null`), 21 (the advertised help text). Nothing else references the
DTO type outside its own definition.

</details>

### 🟠 MEDIUM · Provider-list shrink doesn't set `dirtyProviderList`, so the reflection-pushed arrays go stale

**`Assets/Scripts/RevoltTick.cs:264`** · _bug_ · Tick Engine

In `CalculateState_New`, `dirtyProviderList` is set only on a slot mismatch
or an appended provider. The pure-shrink path
(`PowerProviders.RemoveRange(...)`) trims the list but never sets the flag —
and the reflection setters that push the trimmed data into base `PowerTick`'s
`Providers[]`/`InputOutputDevices[]` only fire when the flag is true. When a
provider is dropped from the *tail* (every earlier slot still matches), the
mismatch check never trips, so the cached arrays keep the removed provider
until an unrelated topology change re-dirties the list. This feeds the
Network Analyzer display and, when the recursive-provider check is enabled,
a stale/phantom I/O device into that check too.

- **Suggested fix:** Set `dirtyProviderList = true` whenever the
  `RemoveRange` branch runs, or unconditionally recompute the reflection
  arrays whenever `PowerProviders.Count` changed this tick.

<details><summary>Verification trail</summary>

Confirmed with a specific reachable ordering: dropped provider at a
later slot than one that still matches → mismatch never trips → shrink
still executes → flag stays false. The downstream impact on the recursive
check is real given `CheckForRecursiveProviders` reads `InputOutputDevices`.

</details>

### ⚪ LOW · "Enable Recursive Network Check" detects loops but performs no action

**`Assets/Scripts/RevoltTick.cs:273`** · _bug_ · Tick Engine

With the config flag on, `CalculateState_New` calls the reverse-patched
`CheckForRecursiveProviders`, whose only effect is appending to *base*
`PowerTick.BreakableFuses`/`BreakableCables` — lists vanilla drains in its own
`ApplyState`, which Re-Volt fully replaces with `ApplyState_New`.
`ApplyState_New` never reads those lists; it runs its own independent
`TestBurnCable`/`TestBlowFuse` model. The config description (`ReVolt.cs:87`)
explicitly promises the flag will "force-burn cables out" on a detected
loop — it doesn't.

- **Spec:** `ReVolt.cs:85-87` config description: force-burn on recursive
  loop detection.
- **Suggested fix:** Either consume `BreakableFuses`/`BreakableCables` in
  `ApplyState_New` (call `Break()` on them), or reimplement the recursion
  action against Re-Volt's own burn model.

<details><summary>Verification trail</summary>

Confirmed end-to-end: grepped the whole mod for `BreakableFuses`/
`BreakableCables`/`BreakSingle*` — zero references outside the reverse-patch
population point. The config's own stated intent is contradicted by the
code, which is why this is "likely" rather than "definitely" (it's possible
the intent quietly changed and the description is what's stale).

</details>

### ⚪ LOW · `HeavyBreaker.OnMemberRemoved` runs `CheckConnections` twice and only clears the first matching slot

**`Assets/Scripts/Prefabs/HeavyBreaker.cs:390`** · _quality_ · Switchgear / Heavy Breaker

`OnMemberRemoved` calls `UpdateEntryLists(true)` then `CheckConnections()` —
but `UpdateEntryLists` already calls `CheckConnections` internally at its own
end, so it runs twice per member removal (wasted reflective setter
invocations and `AddDevice`/`RemoveDevice` churn). Separately, clearing the
removed member's ref id uses `Array.IndexOf` and only clears the *first*
matching slot — if the same device occupies two slots (e.g. input == output),
the second slot keeps a dangling ref id.

- **Suggested fix:** Drop the explicit `CheckConnections()` call (redundant);
  loop over all `ConnectionRefIds` slots when clearing a removed member.

### ⚪ LOW · `LoadCenter.CanLogicRead` delegates to `base.CanLogicWrite` (copy-paste)

**`Assets/Scripts/Prefabs/LoadCenter.cs:171`** · _bug_ · Load Center

The default arm of the slot-based read-capability override calls
`base.CanLogicWrite(...)` instead of `base.CanLogicRead(...)` — a clear
copy-paste typo, since the sibling `CanLogicWrite` override correctly calls
`base.CanLogicWrite` in its own default arm. A read-capability query returns
the write-capability answer instead.

- **Steam feature / doc touched:** "...and also supports data networking."
- **Suggested fix:** `_ => base.CanLogicRead(logicSlotType, slotId)`.

### ⚪ LOW · `LoadCenter.GetLogicValue` casts `LogicType.PowerActual` into the unrelated `LogicSlotType` enum

**`Assets/Scripts/Prefabs/LoadCenter.cs:231`** · _bug_ · Load Center

`(LogicSlotType)LogicType.PowerActual` resolves to `LogicSlotType.ReferenceId`
(both enums happen to have index 26 for different members), so the
category-power-summation branch fires for `Quantity` **or** `ReferenceId`
reads, and the intended `PowerActual` read is unreachable through this
overload. A `ReferenceId` slot read would return summed category power
instead of an object reference id. Impact is low because `ReferenceId` reads
are likely gated off elsewhere by `CanLogicRead`'s whitelist.

- **Suggested fix:** Drop the cross-enum cast; expose the power read through
  the `On`/`Quantity` slot mechanism or an explicit, correctly-typed channel.

### ⚪ LOW · Battery/transformer capacity rescale in `Prefab.LoadAll` is non-idempotent

**`Assets/Scripts/Patches/PrefabPatcherPatches.cs:17`** · _bug_ · Battery / Transformer / APC Patches

Vanilla `Prefab.LoadAll` opens with an idempotency guard
(`if (PrefabsGameObject != null) return;`), but this Harmony prefix runs on
**every** call regardless. The battery branch does
`BatteryPrefab.PowerMaximum *= configBatteryCapacityFactor.Value`, mutating
the persistent source prefab in place — a second invocation would compound
it (2×, 4×, 8×...). `LoadAll` is only called once today, so this is dormant,
but the base game's own guard existing signals re-invocation is a real
possibility it's designed to tolerate.

- **Suggested fix:** Guard the prefix the same way vanilla does, or scale
  from a stored base value rather than compounding the live field.

### ⚪ LOW · Battery charge-efficiency clamp is non-monotonic below a 500-unit threshold

**`Assets/Scripts/Patches/StationaryBatteryPatches.cs:27`** · _bug_ · Battery / Transformer / APC Patches

`charged = efficiency * powerAdded`, then `if (charged < 500) charged =
powerAdded` — discarding the efficiency loss entirely below that threshold.
With efficiency < 1 this is non-monotonic: input 999 at efficiency 0.5 stores
all 999 (since `499.5 < 500` triggers the bypass), while input 1001 stores
only 500.5 — *more* input produces *less* stored charge. Any packet whose
post-efficiency amount falls under 500 is charged at 100% efficiency,
partially defeating the feature. With the default efficiency of 1.0 this is
a no-op, which is why it's Low rather than Medium.

- **Spec:** config description: "lose energy to charging inefficiencies."
- **Suggested fix:** Remove the threshold (the surrounding clamp already
  handles small values safely), or restrict the exception to "within one
  packet of full" and document the constant.

---

# Unclear Intent

### 🟠 MEDIUM · `CablePatches`' static registration-handshake flags aren't exception-safe

**`Assets/Scripts/Patches/CablePatches.cs:32`** · _bug_ · Cable Network Graph

`GateTriggerRepeatRegistration`/`RetriggerRegistration` are two
unsynchronized process-global static bools coordinating a defer/retry dance
between `CablePatches` and `CableNetworkPatches.ConnectedNetworks` for
tray-mediated registration. Both are cleared **only** in the
`AfterOnRegistered` postfix — which does not run if `Cable.OnRegistered`
throws (no `try/finally`, no Harmony finalizer). A throw after the flags flip
leaves both stuck `true` globally: the next unrelated cable to register gets
a partial (tray-edge-dropping) network list and a spurious deferred
re-registration.

- **Suggested fix:** Reset the flags in a `try/finally` (or a Harmony
  finalizer) around registration, and scope the retrigger decision to the
  specific cable rather than two process-global bools.

<details><summary>Verification trail</summary>

Mechanism confirmed exactly as described. Marked "unclear intent" rather
than "likely bug" because the one open question — whether
`Cable.OnRegistered` (external RocketWerkz code) ever actually throws in
practice — isn't something the audit could confirm from this side of the
boundary.

</details>

### 🟠 MEDIUM · `HeavyBreaker`'s data-network re-registration is missing the membership fallback the power path has

**`Assets/Scripts/Prefabs/HeavyBreaker.cs:348`** · _bug_ · Switchgear / Heavy Breaker

The power-network branches re-add the breaker when the target network is new
**or** the breaker is missing from `PowerDeviceList`
(`!previousNetworks.Contains(X) || !X.PowerDeviceList.Contains(this)`). The
data branch checks only the first clause. Since `HeavyBreaker` is a
reflectively-injected pseudo-device, an in-place network rebuild that
repopulates a *retained* `DataNetwork`'s device list from grid cables could
silently drop the breaker while `previousNetworks` still contains that same
network object — taking the Dirty-only branch and never re-adding it. This
looks like exactly the kind of case the power-side guard was added to
handle, just not mirrored on the data side.

- **Spec:** "Heavy Breakers can form modular switchgear" — data connectivity
  should survive topology changes the same way power does.
- **Suggested fix:** Mirror the power guard on the data branch:
  `if (DataNetwork != null && (!previousNetworks.Contains(DataNetwork) ||
  !DataNetwork.DataDeviceList.Contains(this))) DataNetwork.AddDevice(...)`.

### ⚪ LOW · `AssessPower` prefix suppresses vanilla instead of falling back to it on injection failure

**`Assets/Scripts/Patches/DevicePatches.cs:23`** · _bug_ · Tick Engine

When `cableNetwork.PowerTick is not RevoltTick`, this prefix returns `false`
(suppressing vanilla `Device.AssessPower` entirely) rather than `true`. Every
other injection-failure fallback in the codebase (`PowerTickPatches`'
three prefixes) correctly returns `true` in this situation, matching the
documented "manatee → Re-Volt scalar → vanilla" degradation ladder. Since
`Inject` runs in every patched `CableNetwork` constructor, this path is
effectively dead today — but if injection ever fails, devices get no power
on/off assessment at all, silently.

- **Spec:** stationeers.md Failure and Fallback: "the existing prefix
  pattern already falls back to vanilla PowerTick."
- **Suggested fix:** Return `true` in that branch, matching
  `PowerTickPatches`' convention.

### ⚪ LOW · `ConnectedNetworks` dereferences `Get<Cable>()` without the null guard its sibling `RebuildNetwork` uses

**`Assets/Scripts/Patches/CableNetworkPatches.cs:130`** · _bug_ · Cable Network Graph

`var cable1 = span[index].Get<Cable>(); if (!__result.Contains(cable1.CableNetwork))`
dereferences with no null check, while the sibling BFS in
`RebuildNetworkPatch` guards every `Get<Cable>()` call. Mirrors vanilla
(so not a regression), but inconsistent with the guarded rebuild path right
next to it.

- **Suggested fix:** `if (cable1 == null) continue;`, matching
  `RebuildNetworkPatch`.

### ⚪ LOW · `RebuildNetworkPatch` heap-allocates its traversal buffer despite being marked `unsafe`

**`Assets/Scripts/Patches/CableNetworkPatches.cs:45`** · _quality_ · Cable Network Graph

Vanilla and the sibling `ConnectedNetworks` both use
`stackalloc SmallCellRef[32]`. `RebuildNetworkPatch` uses `new
SmallCellRef[32]` twice, yet still carries the now-unnecessary `unsafe`
modifier — two avoidable heap allocations per rebuild on a path that can run
once per connected cable during splits/merges.

- **Suggested fix:** Use `stackalloc` as vanilla does; drop `unsafe`.

### ⚪ LOW · `CableTray.RebuildExclusions` is a shared static dedup set with no rebuild-boundary reset

**`Assets/Scripts/Prefabs/CableTray.cs:58`** · _bug_ · Cable Tray

`RebuildExclusions` is a `static readonly HashSet<int>` shared across
**every** `CableTray` instance, keyed only on `colorIndex + voltage*64`, and
cleared only inside `OnMemberAdded`/`OnMemberRemoved` — never at a rebuild
boundary. `ReplaceMembers` fires `OnMembersChanged` for every member
including no-diff ones, where no clear happens. Today every call path that
reaches this (tray place/remove) always produces a diff on the added/removed
member, so a clear always happens in practice — this is a latent land mine
for a future no-diff or reentrant rebuild path, not an active bug.

- **Suggested fix:** Make the dedup set instance-scoped, or clear it
  deterministically at the top of the logical rebuild rather than relying on
  an incidental member-diff clear.

### ⚪ LOW · `CableTray.OnMemberRemoved` severs cables unconditionally while the rebuild that would reattach them bails during loading

**`Assets/Scripts/Prefabs/CableTray.cs:43`** · _bug_ · Cable Tray

`OnMemberRemoved` sets `cable.CableNetwork = null` for every connected cable
with no `GameState`/`RunSimulation` guard, while `OnMembersChanged` — the
step that would rebuild those networks — returns early during loading or
when the sim isn't running. If a tray is deregistered mid-load or while
paused, its cables end up orphaned with nothing to reattach them (the
deferred visual-only task doesn't rebuild networks). Not reachable via any
current call site (load only produces `OnMemberAdded`), but a real gap if
that ever changes.

- **Suggested fix:** Apply the same guard to `OnMemberRemoved`, or defer the
  sever-and-rebuild together.

### ⚪ LOW · `DeferredUJC`'s per-frame poll isn't cancelled on tray deregistration

**`Assets/Scripts/Prefabs/CableTray.cs:202`** · _bug_ · Cable Tray

This visual-update loop busy-polls every frame during loading with no
`CancellationToken` tied to `OnDeregistered`. A tray removed mid-load keeps
polling and, once loading finishes, calls `UpdateJunctionConnections` against
a deregistered object. Unity's fake-null checks on the renderer references
mostly shield an actual crash, so impact is wasted work rather than a hard
failure.

- **Suggested fix:** Tie the loop to a `CancellationToken` cancelled in
  `OnDeregistered`.

### ⚪ LOW · `HeavyBreaker.DirtyLists` is only invalidated on membership join/leave, not on a member's `ComponentType` changing in place

**`Assets/Scripts/Prefabs/HeavyBreaker.cs:374`** · _bug_ · Switchgear / Heavy Breaker

Cached `PowerEntries`/`DataEntries` (filtered by `ComponentType`) only
recompute when `DirtyLists` is set, which only happens on membership change.
`BusTie.ComponentType` reads a mutable public field (`BusType`) — if that
field were ever flipped at runtime without a network join/leave, the cached
lists would go stale silently. Currently `BusType` is never written in C#
anywhere in the codebase (only set via prefab serialization), so this is
fully latent today.

- **Suggested fix:** Document/enforce that `ComponentType` is immutable per
  instance lifetime, or have `BusType`'s setter notify the pseudo-network.

### ⚪ LOW · The `Connections` open-end filter is duplicated across four files, and one copy uses `==` where another uses a bitwise flag test

**`Assets/Scripts/Prefabs/HeavyBreaker.cs:104`** · _quality_ · Switchgear / Heavy Breaker

The same block is copy-pasted in `HeavyBreaker.cs`, `BusTie.cs`, `Wireway.cs`
(comment: "literally just Tom's reference code") and `CableTray.cs`. Three
copies test `openEnd.ConnectionType == X`; `CableTray`'s tests
`(openEnd.ConnectionType & X) != NetworkType.None`. If `ConnectionType` is
ever a composite flag value, the `==` copies would silently fail to match
combined-type open ends while `CableTray` keeps working.

- **Suggested fix:** Extract one shared helper; standardize on the bitwise
  test.

### ⚪ LOW · `DeserializeSave` array-shape crash is separate from the persistent `_deficit` accumulator concern

**`Assets/Scripts/Prefabs/CircuitBreaker.cs:157`** · _bug_ · Circuit Breaker Core

The cross-tick `_deficit` float accumulates every tick and gates
`CanSupplyPower`/`GetGeneratedPower`, but — unlike `_transferred` — is never
zeroed at end of tick, and it's absent from every serialization surface
(`SerializeSave`, `SerializeOnJoin`). It silently resets to 0 across
save/load and client join, so server/client state can diverge and long-
session float drift is possible. Flagged for review rather than asserted
wrong — it may be intentionally transient.

- **Suggested fix:** If `_deficit` is meant to be a per-tick transient term,
  reset it each `OnPowerTick` like `_transferred`; if it needs to persist,
  add it to the serialization payloads.

### ⚪ LOW · `CanLogicWrite` authorizes writes on `canRemoteControl`, but `SetLogicValue` silently drops them unless `isSmartBreaker` is also true

**`Assets/Scripts/Prefabs/CircuitBreaker.cs:664`** · _bug_ · Circuit Breaker Core

Two independent public fields gate the same feature at different points: a
prefab with `canRemoteControl = true, isSmartBreaker = false` would advertise
a writable logic pin (via `CanLogicWrite`) yet every write silently no-ops in
`SetLogicValue`, with no player feedback. Harmless if every remote-
controllable breaker prefab is also smart — which may simply be true today.

- **Spec:** "Smart Breakers!" (remote/logic control framed as a smart-breaker
  feature).
- **Suggested fix:** Gate `CanLogicWrite` on `canRemoteControl &&
  isSmartBreaker`, or drop the `isSmartBreaker` early-return.

### ⚪ LOW · Dead `value` juggling in `SetLogicValue`'s `On` branch, immediately overwritten by `UpdateMode()`

**`Assets/Scripts/Prefabs/CircuitBreaker.cs:667`** · _quality_ · Circuit Breaker Core

The `LogicType.On` branch computes a `Mode` value that's entirely overwritten
by the trailing `UpdateMode()` call, which recomputes `Mode` purely from
`Error`/`OnOff`. No effect today, but reads like a toggle and could mislead a
future edit to `UpdateMode()`.

- **Suggested fix:** Drop the dead computation; let `UpdateMode()` be the
  sole source of `Mode`.

### ⚪ LOW · `Setting`'s hard-coded 1000W floor can conflict with a prefab's `MinTripCurrent`

**`Assets/Scripts/Prefabs/CircuitBreaker.cs:63`** · _quality_ · Circuit Breaker Core

The `Setting` setter clamps to `[1000.0f, MaxTripCurrent]`, while
`Awake`/`OnFinishedLoad`/the cycle-down UI all treat `MinTripCurrent` as the
intended floor. If any prefab sets `MinTripCurrent` below 1000, the setter
silently overrides it — harmless only if every prefab's `MinTripCurrent >=
1000`.

- **Suggested fix:** Clamp to `[MinTripCurrent, MaxTripCurrent]` instead of
  the literal, or document 1000 as a deliberate global floor.

### ⚪ LOW · `MaxInterruptCurrent` is a serialized field with zero readers anywhere

**`Assets/Scripts/Prefabs/CircuitBreaker.cs:26`** · _quality_ · Circuit Breaker Core

The name and the real-world concept (a breaker's max fault current before it
fails to clear) suggest an intended "breaker fails destructively above its
interrupt rating" mechanic that either never got implemented or was
superseded. Currently the trip path always trips cleanly regardless of
magnitude.

- **Suggested fix:** Either implement the interrupt-rating behavior or
  remove the unused field.

### ⚪ LOW · Two LoadCenters on one network fail open (every category force-powered) with no deterministic winner

**`Assets/Scripts/RevoltTick.cs:132`** · _spec-drift_ · Load Center / Tick Engine

When more than one `ILoadCenter` is present, `_loadCenter` stays null, every
LoadCenter gets `HasConflict = true`, and every `PowerClass` is forced
powered — a reasonable fail-open choice, but two side effects worth
confirming as intended: (1) a previously-controlling LoadCenter instantly
loses control the moment a second one joins the network — anything it had
switched off turns back on; (2) conflicting LoadCenters keep their last
`LoadControlData` reference (never nulled), so logic `Quantity` reads can
report stale sums, possibly against a reallocated array.

- **Spec:** "control entire sets of devices without needing a separate power
  network."
- **Suggested fix:** If deterministic behavior is wanted, pick a stable
  winner (e.g. lowest `ReferenceId`) instead of disabling all control, and
  clear `LoadControlData` on conflicting units.

### ⚪ LOW · `ErrorBlink`'s duplicate async loop, uncached `GetComponent`, and a null-guard gap

**`Assets/Scripts/Components/AssignableBinaryPoweredMaterialChanger.cs:48`** · _quality_ · Load Center

`ErrorBlink()` duplicates `BreakerStatusScreen.TrippedAnim`'s hand-rolled
500ms toggle loop near-verbatim (the two can drift independently).
`RefreshState` also calls `GetComponent<MeshFilter>()` on every invocation
instead of caching, and dereferences `parentThing.Powered`/`.Error` with no
null guard even though `ErrorBlink` itself guards `parentThing != null` —
`Init` can legitimately leave `parentThing` null, so a `RefreshState` call in
that state would throw.

- **Suggested fix:** Extract one shared blink helper for both changers;
  cache the `MeshFilter`; guard `parentThing` at the top of `RefreshState`.

### ⚪ LOW · Battery `ReceivePower` override has no config gate, unlike its sibling rate-limit patches

**`Assets/Scripts/Patches/StationaryBatteryPatches.cs:23`** · _quality_ · Battery / Transformer / APC Patches

`LimitMaxChargeRate`/`LimitMaxDischargeRate` are gated behind `enableBatteryLimitsPatch`,
but `ChargeEfficiencyControl` (the `ReceivePower` prefix) always takes over
the method with no flag check. Turning off "Enable Battery Limits" doesn't
restore vanilla charging — it just keeps applying
`configBatteryChargeEfficiency` (harmless only because the default is 1.0).

- **Suggested fix:** Gate `ChargeEfficiencyControl` behind
  `enableBatteryLimitsPatch` (or its own flag), returning `true` when
  disabled.

### ⚪ LOW · Transformer's `GetGeneratedPower` fix can go negative; the analogous APC fix clamps to zero

**`Assets/Scripts/Patches/TransformerExploitPatch.cs:25`** · _bug_ · Battery / Transformer / APC Patches

`Mathf.Min(Setting, InputNetwork.PotentialLoad - ____powerProvided)` has no
lower clamp; the sibling `AreaPowerControllerPatches.AvailablePowerGetterPatch`
guards the identical shape with `Math.Max(0f, ...)`. If `____powerProvided`
ever exceeds `PotentialLoad` in a transient, this returns a negative
generated-power contribution. Reachability of that transient in the tick
ordering wasn't confirmed either way.

- **Spec:** "transformers are less exploitable now... properly apply their
  own power requirements to the upstream."
- **Suggested fix:** Wrap the second operand in `Mathf.Max(0f, ...)` to
  match the APC patch.

### ⚪ LOW · Transformer capacity rescale keys on exact vanilla magic numbers (50000f/25000f)

**`Assets/Scripts/Patches/PrefabPatcherPatches.cs:22`** · _quality_ · Battery / Transformer / APC Patches

The switch matches `OutputMaximum` against literal `50000f`/`25000f`. If a
future Stationeers patch changes either base value by any amount, the arm
silently stops matching — no error, no log, the transformer is just quietly
left at vanilla scaling. Contrast the battery branch, which keys off
component type instead of a magic value.

- **Suggested fix:** Identify the transformer tier by prefab name/hash
  instead of exact `OutputMaximum`, or at minimum log a warning when neither
  arm matches.

### ⚪ LOW · `ThingPatches.HasState` unconditionally overrides state resolution for every `Thing`, not just Re-Volt types

**`Assets/Scripts/Patches/ThingPatches.cs:34`** · _bug_ · Plugin Init / Construction Plumbing

This postfix runs on *every* `Thing`'s `HasState`, not just Re-Volt's own.
Vanilla returns false for state names hashing to Slot1/Slot2/Button4/Button5;
this patch forces them true whenever a matching interactable exists. Any
vanilla or third-party `Thing` that happens to have both a Slot1/Slot2
interactable *and* an animator/save state literally named that would resolve
differently than vanilla intends. Whether any such vanilla device actually
exists wasn't confirmed either way — the sole current caller in the
decompiled base game is a save-state-restore path, not an active-animation
path, so the practical blast radius may be smaller than it looks.

- **Suggested fix:** Gate the extra cases on the instance actually being a
  Re-Volt type (e.g. `__instance is ILoadCenter`), or only override when the
  original result was false/unset.

### ⚪ LOW · `MultiConstructor`/`SingleConstructor` are empty subclasses; one carries the author's own `// TODO?`

**`Assets/Scripts/Prefabs/MultiConstructor.cs:8`** · _quality_ · Plugin Init / Construction Plumbing

Both are empty subclasses whose only content (in `MultiConstructor`) is a
`// TODO?` comment — your own flagged uncertainty about whether overridden
behavior is still needed. Neither is referenced elsewhere in the C# source
(they attach to prefab assets not in this repo checkout).

- **Suggested fix:** Resolve your own TODO — confirm whether this is
  intentionally a plain type marker (drop the comment) or still needs
  constructor-behavior overrides.

### ⚪ LOW · `ReVolt.OnLoaded` has no error isolation around `PatchAll()` and content registration

**`Assets/Scripts/ReVolt.cs:114`** · _quality_ · Plugin Init / Construction Plumbing

`harmony.PatchAll()`, the prefab registration loop, and
`AddSaveDataType`/`Networking.Required` all run with no `try/catch`. Harmony
applies patches class-by-class, so if a future Stationeers update
renames/removes a single patched target, `PatchAll` throws after already
applying earlier patches — the mod ends up half-patched, and everything
after that line (including save-data registration and the networking-
required flag) never runs, with no diagnostic pointing at which target
failed. For a mod entering feature-complete maintenance, base-game version
drift is the most likely future trigger for exactly this.

- **Suggested fix:** Wrap `PatchAll` and content registration so a failure
  is logged (and ideally unpatches what succeeded), or at minimum log which
  patch target failed before rethrowing.

### ⚪ LOW · `NetworkExport.ThingsById` can cache a half-built dictionary if a RefId collides

**`Assets/Scripts/NetworkExport.cs:42`** · _bug_ · Network Export

The lazy-build getter assigns `_things = new()` **before** populating it via
`Dictionary.Add(...)` calls across fuses/cables/devices.
`Dictionary.Add` throws on a duplicate key — and since `_things` is already
non-null when that throw happens, the next access short-circuits past the
rebuild and silently returns the incomplete dictionary. Whether RefIds are
guaranteed unique across all three collections is a design assumption this
audit couldn't independently confirm.

- **Suggested fix:** Build into a local dictionary and assign to `_things`
  only after it's fully populated; or use `TryAdd` with an explicit warning
  on collision.

---

## Manatee integration points

Not bugs — pointers relevant to the planned Manatee integration
(`docs/stationeers.md`), collected from a dedicated cross-cutting pass that
checked every concrete claim in that doc against the current code.

### Graph Construction intake (the one that matters most)

**🔴 HIGH — `NetworkExport` DTO is missing the fields the design doc says are
already agreed, and has no producer at all**

`Assets/Scripts/NetworkExport.cs:9` / `Assets/Scripts/Commands/NetworkExportCommand.cs`

`docs/stationeers.md`'s Graph Construction section lists three "agreed
additions" to the intake format: **device port identity** per connection,
**per-cable type/gauge** (prefab name), and **fuse positions in the
adjacency** (a fuse as a rated edge, not a side-list entry). The current DTO
has none of these: `CableInfo` carries only `List<int> Connections` with no
port identity or gauge field, and fuses live in `FusesByType` as a side-list
keyed by type string rather than as edges. Combined with
`NetworkExportCommand` being a total stub (see Likely Bugs above), **the
entire Graph Construction intake pipeline is unbuilt** — including the
"recovered from per-cable OpenEnds before the network builder discards them"
step the doc assumes is already possible.

If someone implements the command against today's DTO shape without
revisiting this, the export will be missing data Manatee's design assumes it
receives.

### Tick-phase & reflection seams

- **`RevoltTick.cs:41-42,285`** — The named seams
  (`Initialize_New`/`CalculateState_New`/`ApplyState_New`, and the scalar
  `_powerRatio` solve point at line 285) are all confirmed accurate. One
  nuance: `RevoltTick` declares its own private `_powerRatio`/`_isPowerMet`
  that **shadow** same-named private fields on base `PowerTick`.
  `ApplyState_New` first calls the reverse-patched base `CacheState` (writing
  the *base* fields with vanilla's formula), then recomputes the *shadow*
  fields with different semantics (a 20W anti-flapping buffer). Device
  power distribution reads the shadow field. **Whoever wires in the MNA
  solve must target `RevoltTick`'s shadow field, not the base one.**
- **`RevoltTick.cs:67-68,96-97,269-270` / `HeavyBreaker.cs:28-29,344,355,365`**
  — Reflection field pushes (`Providers`/`InputOutputDevices` via cached
  `PropertyInfo`; `HeavyBreaker`'s `DataCables`/`PowerCables` via
  `AccessTools`) bypass the documented manatee→scalar→vanilla fallback
  ladder: the ladder covers *injection* failure, not a *mid-tick* reflection
  failure. If a future base-game update renames any of these properties, the
  resolved `PropertyInfo`/`MethodInfo` is null at static init while injection
  still succeeds — every tick then throws with no vanilla fallback. Also:
  `HeavyBreaker.CheckConnections` runs from `OnPowerTick` on the shared
  ThreadPool sim thread and mutates device-cable topology (`AddDevice`/
  `RemoveDevice`) *during* the electricity tick — Manatee's per-network
  island cache can't assume topology is immutable within a tick body.

### Network graph rebuild/merge

- **`CableNetworkPatches.cs:44-117,120-154`** — Vanilla
  `RebuildNetwork`/`ConnectedNetworks` are fully replaced (prefixes return
  `false`); the Re-Volt versions inject tray-mediated edges via
  `CableTray.MatchCables`. Manatee's incremental hooks
  (`CableNetwork.Add/Remove/AddDevice/RemoveDevice/Merge`) are still the
  vanilla methods and are called from these replacements — any hook placed on
  vanilla `RebuildNetwork`/`ConnectedNetworks` directly would never fire.
  Tray-mediated edges are computed dynamically at rebuild time and are **not**
  persisted as cable `OpenEnds`, so `NetworkExport` adjacency must include
  them explicitly. Note also: `RebuildNetworkPatch` does not add the seed
  cable itself (the `new CableNetwork(cable)` constructor does), and
  `CablePatches` defers tray re-registration by one frame via
  `UniTask.Yield` — "topology settled" is not synchronous with a single
  `Add`.
- **`CableNetworkPatches.cs:34-41,162`** — The design doc cites line 43 as
  the RevoltTick injection point; that line is currently blank. The actual
  injection is three constructor postfixes calling `Inject()` (line 162),
  which reflectively overwrites the vanilla `readonly PowerTick PowerTick`
  field on every `CableNetwork` construction. Worth a doc correction, and the
  reflection lookup has the same silent-failure-mode caveat as above.

### Islands and coupling devices

- **`HeavyBreaker.cs:264-272`** — `OnAddCableNetwork`/`OnRemoveCableNetwork`
  are fully stubbed (no-ops) to avoid a documented reentrancy hazard into
  `CheckConnections`. Consequence: a `HeavyBreaker` does **not** self-heal via
  the standard base-game callback when a network is torn down externally;
  re-resolution flows only through `BusTie`-driven notifications plus a
  one-shot repatch on the first `OnPowerTick` and `InitializeDevice`. Treat
  `OnPowerTick`'s first-call `CheckConnections` as the load-time reattach
  point.
- **`CableTray.cs:46,65,92`** — Every tray/network traversal buffer is a
  fixed 32-slot `stackalloc`, matching the same cap in
  `CableNetworkPatches`. Base-game `FillConnected` has no bounds check, so
  exceeding 32 throws and aborts the rebuild rather than truncating quietly.
  Safe today (trays have ~11-12 fixed open ends), but any redesign letting a
  cell host more matched cells must size the buffer dynamically or
  bounds-check.

### Circuit breaker persistence & seams

- **`CircuitBreaker.cs:77,133,474,529,556`** — Three parallel serialization
  surfaces (`SerializeSave`/`DeserializeSave`, `SerializeOnJoin`/
  `DeserializeOnJoin`, `BuildUpdate`/`ProcessUpdate`), each independently
  carrying `ConnectionRefIds[0..2]` and `Mode`/`Setting`. RNG is
  `new System.Random((int)ReferenceId)` — reproducible per-`ReferenceId`, but
  note the `long → int` truncation. `OnPowerTick` is the per-device power
  phase; trip-vs-burn arbitration lives in `RevoltTick.ApplyState_New`
  iterating `IBreaker.CanSupplyPower`/`LimitCurrent`/`Trip` — any power-sim
  replacement must preserve both hooks.

### Load Center thread-hop contract

- **`ILoadCenter` (`ILoadCenter.cs:9`) / `LoadCenter.cs:44-83`** —
  `RevoltTick.CalculateState_New` reads `EnableLights`/`Doors`/`Atmos`/
  `Equip`/`Logic` every tick on the power thread; those getters read live
  `Interactable.State` off the main thread with no synchronization (tolerated
  as a plain `int` read). `LoadControlData` points at the same in-place-
  mutated `PowerData` array the power thread writes into and the main thread
  reads from for logic. `LoadCenter.RefreshAnimStateFromThread` is the
  established `ThreadedManager.IsThread` → `UniTask.SwitchToMainThread()`
  pattern for getting tick results back onto the main thread before mutating
  animator/mesh state — any reimplementation of the tick loop must preserve
  this hop for all animator/mesh writes.

### Battery/Transformer/APC accounting seams

- **`AreaPowerControllerPatches.cs:52-83` / `TransformerExploitPatch.cs:46-63`
  / `StationaryBatteryPatches.cs:21-33`** — Six patches reimplement the
  per-tick power-accounting phase for batteries/transformers/APCs, three of
  them reading/writing the base game's private `_powerProvided` via
  `ref ____powerProvided` as load-bearing inter-tick state (e.g.
  `AreaPowerControl.UsePower` branches on `_powerProvided == 0` to decide
  whether to draw from the APC battery). This accounting is host-
  authoritative and lives in these methods, not a central pass; any
  co-existing Manatee patch on the same base methods must coordinate prefix
  ordering, since these are full replacements (`return false`).
- **`PrefabPatcherPatches.cs:10-30`** — Battery `PowerMaximum` and
  Transformer `OutputMaximum` are mutated on the *source prefabs* at
  `Prefab.LoadAll` time. Any capacity/rating value Manatee reads off a prefab
  or instance is already Re-Volt-adjusted, not vanilla — including the
  clamp range for `Setting` on save load.

### Plugin init

- **`ReVolt.cs:17-18,114,117-138`** — `OnLoaded` is the single init seam:
  the two `PseudoNetworkType` registrations happen at static construction
  (before `OnLoaded` runs), `harmony.PatchAll()` wires every patch in one
  shot, prefab registration is gated on `enablePrefabContent`, and
  `AddSaveDataType<CircuitBreakerSaveData>()` plus `Networking.Required =
  true` run unconditionally after. If you hook Manatee's own init in here,
  do it after line 138 so all of the above is already established. (See the
  Unclear Intent section above for the lack of error isolation around this
  same call.)
