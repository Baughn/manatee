# Stationeers Client (with Re-Volt)

Last updated: 2026-07-05
Status: DRAFT — integration design settled with Sukasa; no code yet.

> **Revision pending (do not treat as canon on the contested points).** After
> the 2026-07-06 core build, api.md superseded parts of this doc and flagged it
> for a Sukasa-signed rewrite (api.md §23 item 9). Specifically: the Integration
> Seams section maps manatee phases onto a per-network 3-phase contract, but the
> **one-global-tick-body execution model** (api.md §22.a) supersedes it — a
> single global solve cannot be hosted per-network; the "fires on topology
> change" incremental-hooks claim doesn't match source (the dirty path tracks
> device lists, not cable cuts; `RebuildNetwork` is a whole-network flood needing
> a cable-set diff); and the "galvanic bridge" closed-breaker assertion must
> carry the api.md §7 vanilla-metric derivation and back-feed caveat. Until this
> doc is revised and signed off, **api.md §22.a / §7 / §23 are binding** where
> they disagree with the text below.

Implements design.md R18–R19 plus the core requirements as consumed from
Stationeers. The integration code lives in Re-Volt's repository (MIT),
consuming manatee-core as a git submodule. Engine references are to
Re-Volt's source (`third_party/revolt/Assets/Scripts/`) and, through it, the
decompiled game surface.

## Table of Contents

1. [Integration Seams](#integration-seams)
2. [Graph Construction](#graph-construction)
3. [Islands and Coupling Devices](#islands-and-coupling-devices)
4. [The Legacy-Device Adaptor](#the-legacy-device-adaptor)
5. [Voltage Tiers](#voltage-tiers)
6. [Persistence](#persistence)
7. [Performance](#performance)
8. [Failure and Fallback](#failure-and-fallback)

---

## Integration Seams

Re-Volt already replaces the vanilla power tick wholesale: a Harmony-injected
`RevoltTick : PowerTick` intercepts the three-phase contract
(`Initialise` → `CalculateState` → `ApplyState`) per `CableNetwork`
(`RevoltTick.cs`, `Patches/PowerTickPatches.cs`, injection in
`Patches/CableNetworkPatches.cs:43`). Manatee slots into exactly these
phases:

- **`Initialize_New`** (dirty-driven, fires on topology change) → tier-3:
  rebuild/patch the island graph for that network.
- **`CalculateState_New`** (gather demand/supply) → tier-1/2: adaptor
  updates stamps from device wants.
- The scalar `_powerRatio` solve point (`RevoltTick.cs:285`) → the MNA
  solve.
- **`ApplyState_New`** (distribute) → write actuals back through the
  vanilla device calls.

Additionally a Harmony-substituted **`MNACableNetwork`** subclass carries
the mapping from each vanilla `CableNetwork` to its manatee nodes, so the
per-network parameter in the vanilla 4-call power API resolves to "which
nodes do I stamp." Removability requirement: if Manatee/Re-Volt is removed,
vanilla state is intact and vanilla mechanics resume (all device state keeps
flowing through the vanilla calls).

Incremental hooks: `CableNetwork.Add/Remove` (cables),
`AddDevice/RemoveDevice`, `Merge`. Semantics per design.md R11: adds and
merges are incremental; cable removal rebuilds the affected island. The
whole save rebuilds every network on load — initial build is a measured perf
case of its own, not an afterthought.

## Graph Construction

Sukasa's `NetworkExport` format is the intake:
`CableInfo { RefId, List<int> Connections }` adjacency at **cable-segment
granularity** (recovered from per-cable OpenEnds before the network builder
discards them), plus device and fuse lists. Agreed additions: **device port
identity** per connection, **per-cable type/gauge** (prefab name), and
**fuse positions in the adjacency** (a fuse is an edge with a rating, not a
side-list entry).

Pipeline: segments become resistor edges (resistance from cable type ×
length); junctions become nodes; the shared reduction layer collapses series
chains (compaction.md) — the same code path VS uses for voxel runs. Expected
reduction: ~10k segments → low-hundreds of nodes. Devices attach at their
recorded ports.

Fuses and breaker trip curves ride manatee-core limit events, including the
i²t thermal accumulator, so Re-Volt's existing delayed-burn behavior is
reproduced by mechanism rather than by special case. Ratings are
ambient-temperature-adjusted through the compaction layer's limit envelope
(compaction.md): Europa's −150 °C and Vulcan's +800 °C move real
thresholds, not special cases.

## Islands and Coupling Devices

Vanilla merges each connected cable run into one `CableNetwork`; multi-node
structure *between* networks comes from devices that straddle two networks
(breakers, transformers, bus-ties — Re-Volt's `IBreaker`,
`InputNetwork`/`OutputNetwork`).

- **Closed breaker / bus-tie** = galvanic bridge ⇒ the manatee island spans
  both CableNetworks (union-find over networks keyed on coupler state).
  Vanilla network identities never merge; only the solver island does.
- **Transformer** = power-transfer boundary ⇒ separate islands coupled by
  exchanged P/V per tick (solver.md).
- A breaker is a *coupling device*, not a netlist switch (solver.md's
  relay-vs-breaker duality, settled 2026-07-05): closed ⇒ the two
  CableNetworks' islands merge (incremental); open ⇒ they are separate,
  and the opening transition rebuilds the two halves — cheap
  post-compaction, and rare by the device's role as a safety device.

## The Legacy-Device Adaptor

The vanilla device power API is four inconsistently-overridden calls
(available/needed, provide/draw; joules per half-second). Unconverted
devices are therefore **constant-power elements**, which are nonlinear
(I = P/V). The adaptor linearizes **across game ticks**:

- Stamp each legacy load as G = P_wanted / V_prev² (companion-model
  pattern; converges over a few ticks in normal operation).
- **Brownout clamp**: below a voltage threshold, the device browns out —
  load dropped, vanilla told it received nothing. The voltage-collapse
  death spiral above the threshold is real physics and a shipped feature.
- Post-solve, distribute *actuals*: the adaptor calls the vanilla
  provide/draw with what the solve actually delivered, keeping vanilla
  bookkeeping (and mod removability) intact.
- Legacy generators get the mirror-image treatment: stamped as voltage
  sources at their tier's nominal voltage, with a current limit derived from
  the power they advertise as available. Post-solve, the adaptor reports
  what was actually drawn back through the vanilla provide call.
- Every adapted source carries a small internal series resistance, so
  paralleled generators are well-posed (two disagreeing ideal sources in
  parallel is a designed-in singularity — solver.md failure handling); the
  advertised-power current limit is enforced by an across-tick clamp,
  adaptor-style, not by in-solve mode switching. (Settled 2026-07-05.)

Devices are converted to native manatee models one at a time (real stamps,
device `Tick()`); the two populations coexist indefinitely. Conversion
priority: the devices whose electrical behavior is load-bearing for gameplay
(batteries, transformers, breakers) first; lights-and-doors long tail may
stay adapted forever.

## Voltage Tiers

Settled with Sukasa: **real voltage tiers**, player-facing — device
nameplate ratings, transformer steps — not vanilla watt semantics with MNA
hidden underneath. Vanilla's cable "MaxVoltage" (actually a watt rating)
gets reinterpreted as a genuine rating in the new scheme. Specific tier
values are Re-Volt's design call (modern setting; likely 120/480/5k-style),
independent of VS's 12/48/240.

## Persistence

- Device state (breaker positions, etc.): rides the vanilla driver, as
  today.
- Cable networks are **not** persisted by the game — rebuilt every load.
  Solver dynamic state (battery SoC integrators live in devices; capacitor
  voltages if/when they exist) persists via manatee-core snapshots (R6)
  through an external save/load hook (Sukasa: feasible). Keyed by network
  RefIds; a failed restore degrades to cold start of that island, never a
  failed load.

## Performance

- Cadence: dt = 0.5 s, DC + storage dynamics; no subcycling.

### Threading (verified against decompiled game source, 2026-07-03)

Decompiled from the shipping `Assembly-CSharp.dll`; file:line references are
to the ILSpy output.

- **There is no dedicated power thread.** The entire simulation runs inside
  one async loop, `GameManager.GameTick` (`GameManager.cs:714`): it does
  `UniTask.SwitchToThreadPool()` (:745), then runs **all atmospherics steps,
  then `ElectricityManager.ElectricityTick()` (:793), then logic/rooms/
  cartridges, sequentially on that one ThreadPool thread**, then switches
  back to the main thread. `PowerTick.Initialise/CalculateState/ApplyState`
  run contiguously per network inside `CableNetwork.OnPowerTick()`
  (`CableNetwork.cs:277`). The old `ThreadedManager`-based Electricity/
  Atmospherics worker threads still spawn but run an empty work loop —
  vestigial after a refactor (neither overrides `ThreadedWork()`).
- **Cadence is a self-clocked 500 ms** (`DefaultTickSpeedMs = 500`,
  `GameManager.cs:150`): a stopwatch-paced loop padded with 1 ms delays to
  500 ms. Not an OS timer, not per-frame.
- **No watchdog, no deadline.** A long tick just stretches the cycle — for
  power AND atmospherics AND everything else in the shared body. Exceptions
  are caught, logged, and the loop continues. (Decompile caveat: the pad
  loop tests `Elapsed.Milliseconds` — the 0–999 component — not
  `TotalMilliseconds`, so a >1 s body could re-enter the pad loop; possibly
  an ILSpy artifact, worth flagging upstream.)
- **Thread identity is not stable**: the sim body may land on a different
  pool thread each cycle. Nothing may key state to thread ID;
  `ThreadedManager.IsThread` works because it tests "not the main thread,"
  not a specific worker.
- **Sim is host-only**: `RunSimulation => !NetworkManager.IsClient`; clients
  receive serialized power state. The solver runs only on the host/server.

**Design consequences.** Manatee's per-tick solve is in-band with
atmospherics, so its cost directly extends the global 500 ms cycle: keep it
small in absolute terms, not relative to 500 ms. Steady-state tier-1/2
solves at post-compaction sizes are microseconds — invisible. The expensive
paths (tier-3 island rebuilds; the load-time rebuild-everything case) get
offloaded to a background task with a one-tick handoff: the network runs on
the previous solution (or the scalar fallback) until the rebuild lands.
The game's own idioms cover the mechanics — snapshot inputs under lock as
`PowerTick.Initialise` already does (`PowerTick.cs:59-70`), compute
off-band, marshal results back via `UniTask.SwitchToMainThread()` /
`.Forget()` (the `SetPowerFromThread` pattern, `Device.cs:1333`) or
`UnityMainThreadDispatcher.Enqueue`. There is no Job System or `Task.Run`
in the sim path to piggyback on; UniTask is the house style.
- Scale: Sukasa's saves show 100–200 cables/network; large bases reach ~10k
  cables and ~100 devices. Series compaction is the load-bearing
  optimization; load-time full rebuild of all networks is benchmarked
  explicitly.

## Failure and Fallback

- If a manatee island faults (singularity, non-convergence), that network
  falls back to Re-Volt's existing scalar ratio distribution for the tick
  and logs a diagnostic — the game never stalls on the solver.
- If the Harmony injection fails entirely, Re-Volt's existing prefix
  pattern already falls back to vanilla `PowerTick` (returns true, vanilla
  runs). Manatee inherits this graceful-degradation ladder:
  manatee → Re-Volt scalar → vanilla.
