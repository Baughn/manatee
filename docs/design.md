# Manatee — Design Document

Last updated: 2026-07-05
Status: Requirements interview complete; layer docs in progress.

**Manatee** (from MNA) is an electrical-simulation core built on Modified
Nodal Analysis, shared by mods for two games: **Vintage Story** (the Manatee
mod: early-industrial AC, voxel cables) and **Stationeers** (modern DC, via
the Re-Volt mod). The shared library is **manatee-core**. Spiritual successor
to Minecraft's Electrical Age.

## Table of Contents

1. [Purpose](#purpose)
2. [The Three Clients](#the-three-clients)
3. [Requirements](#requirements)
4. [Non-Goals](#non-goals)
5. [System Overview](#system-overview)
6. [Simulation Model](#simulation-model)
7. [Vintage Story: Game Design](#vintage-story-game-design)
8. [Stationeers: Integration](#stationeers-integration)
9. [Performance Targets](#performance-targets)
10. [Testing and Validation](#testing-and-validation)
11. [Repository Layout and Licensing](#repository-layout-and-licensing)
12. [Open Questions](#open-questions)

Detailed layer docs (to be written): [solver.md](solver.md),
[compaction.md](compaction.md), [vintage-story.md](vintage-story.md),
[stationeers.md](stationeers.md), [testing-strategy.md](testing-strategy.md),
[curriculum.md](curriculum.md).

---

## Purpose

**Electricity in games is usually a lie.** Power is a number that teleports
from generators to machines. This project instead simulates real circuits —
voltages, currents, resistance, heat — with an MNA solver, the approach that
made Electrical Age unique.

**The educational thesis:** realism hard enough that you need to understand
real-world electricity is a *feature*. The intended difficulty is **mental,
not physical**: placing components must be near-frictionless (cable
pathfinding, generous placement aids), so that all of the challenge lives in
*understanding the circuit you are building*. What a player learns in these
mods — Ohm's law, why wire gauge matters, why their undersized cable caught
fire, what a transformer does, why the lights flicker at 5 Hz — is true
outside the game.

Audience: **curious adult gamers.** No math background assumed; rigor emerges
through play. Classroom use is a possible later bonus, not a design driver.

Failure model corollary: hazards (fires, melted cables, shocks) must always
trace back to **player mistakes** — never to random external events. A player
whose house burned down should be able to say what they got wrong. The
mistake need not be *visible in advance* item-by-item, but the rule that
makes it a mistake must be knowable (see the li-ion loot policy under
Ruins loot: the class property is taught; the siting is the mistake).

Mastery corollary (settled 2026-07-06): **every learned skill gets an
automation off-ramp.** A mechanic that is a thrill the first five times
and a chore forever after ships a craftable convenience once mastered —
the pre-wired elevator controller, DC lighting versus flicker, a
sync-check breaker that blocks out-of-phase closure while the player
still matches speed by ear. The off-ramp is a convenience, never a
requirement; this is the difference between a curriculum and a
treadmill.

## The Three Clients

The core is game-agnostic and serves three clients:

1. **Vintage Story world** — voxel-based cables and devices in the survival
   world. AC-centric (waterwheel/windmill era); frequency is a physical
   quantity derived from the mechanical network's shaft speed.
2. **Vintage Story "gaming tablet"** — a ruins-loot relic containing a 2D
   schematic simulator (Falstad-style) with guided lessons and a free-play
   sandbox. The lessons "just happen" to be a tutorial for the mod — and for
   real electricity. Runs its own solver islands, decoupled from the world.
3. **Stationeers (Re-Volt)** — DC simulation over the game's existing cable
   networks, integrated via the Re-Volt mod's replaced power tick.
   Collaboration with Sukasa (Re-Volt's author).

The tablet client existed in embryo as sparky's standalone "handbook" GTK
editor; a 2D schematic client is also the primary dev/test harness.

## Requirements

Each requirement is justified by the purpose above.

### Core solver

- **R1. Real circuit simulation via MNA.** Node voltages and branch currents
  from Kirchhoff's laws; resistors, sources, capacitors, inductors, switches,
  diodes-as-needed. *Justification: the entire educational thesis.*
- **R2. One netlist, multiple analyses.** DC operating point, timestepped
  transient (Backward Euler companion models), and subcycled time-domain AC.
  Components provide stamps per analysis. *Justification: three clients with
  different fidelity needs; SPICE precedent.*
- **R3. Waveform-first AC.** AC islands are simulated by subcycling
  time-domain steps within the game tick (linear islands: cached LU,
  RHS-only substeps). Phasor (complex) analysis is a later optimization for
  metering, never the primary mode. *Justification: 5 Hz waterwheel lamp
  flicker is deliberately instructive; the oscilloscope item needs real
  waveforms.*
- **R4. Explicit change-cost tiers in the API.** (1) RHS-only value changes —
  back-substitution only; (2) conductance changes — numeric refactorization,
  reused symbolic; (3) topology changes — full rebuild, batched.
  *Justification: performance model must be legible to client authors; both
  games' integration seams map onto these tiers.*
- **R5. Islands as a core feature.** Connected-component management with
  incremental merge and rebuild-on-split, solved independently (parallelism
  point). Islands may span game-level network boundaries via coupling devices
  (Stationeers breakers/transformers). *Justification: both games need it;
  transformers are the thread-isolation points in VS.*
- **R6. State snapshot/restore.** Capacitor voltages, inductor currents, and
  device state serialize losslessly. *Justification: VS chunk pause/resume;
  Stationeers save/load (networks rebuild every load; state must survive via
  our own persistence hook).*
- **R7. Limit events with attribution.** The solver reports overcurrent /
  overvoltage / overpower against configured limits; the client layer maps
  events back to source geometry (which voxel melts, which fuse pops).
  *Justification: emergent fuses, cable fires, and breaker trips are the
  gameplay expression of real ratings.*
- **R8. Thread purity and allocation discipline.** Pure C#, no game-engine
  API, no allocation in the per-tick steady state. *Justification: Stationeers
  runs power on a background worker thread; VS budget is tight
  (see [Performance Targets](#performance-targets)).*
- **R9. Legible failure.** Degenerate circuits (floating nodes, contradictory
  sources, non-convergence) produce diagnosable errors/events, never NaN
  propagation. *Justification: players will build pathological circuits as a
  hobby; "why doesn't this work" must be answerable in-game.*

### Shared reduction layer ("compaction")

- **R10. Series-chain collapse.** Long runs of conductor (VS: voxel regions;
  Stationeers: cable segments) reduce to single resistors before solving,
  transparently — including interpolating eliminated node voltages back out
  for probing/metering. *Justification: a 2×2×40 copper bus must cost one
  resistor; Stationeers bases reach ~10k cable segments.*
- **R11. Incremental topology maintenance.** Voxel/cable add and network
  merge are incremental; removal rebuilds the affected island (split
  detection is not worth the complexity at our island sizes). Periodic or
  on-demand full rebuild acts as a resync backstop with drift detection.
  *Justification: sparky proved the incremental scheme; SRE instinct says
  incremental state needs a reconciliation path.*

### Vintage Story client

- **R12. Voxel cables with physical semantics.** Cross-sectional area =
  ampacity; material resistivity is real; insulation is a voxel material
  (tree resin as the fiction); bare conductors short, ground when wet, and
  shock entities relative to global ground. *Justification: wire gauge,
  insulation, and fusing become emergent, teachable physics rather than
  special cases — e.g. a lead segment in a copper run IS a fuse.*
- **R13. Frictionless placement.** Cable-laying via pathfinding (as in
  sparky); the physical act of building must not be the hard part. The
  pathfinder routes conductor *pairs* by default (two-wire idiom — see
  Grounding model); one gesture lays supply and return. The mandate
  extends to **maintenance** (settled 2026-07-06): consumables — zinc
  plates, carbon rods, sun-lamp elements, burned cable sections — swap
  in one interaction. Realism decides *that* parts wear; interaction
  cost must never be where the difficulty lives.
- **R14. Mechanical coupling.** Alternators load the mechanical network with
  torque proportional to electrical load; motors do the reverse. Frequency =
  shaft speed × pole pairs. *Justification: VS's mechanical network gives AC
  a physical origin story EA never had.*
- **R15. Instruments.** Multimeter item; V/I/temperature in the block hover
  tooltip by default (config option to require the item, for realism
  purists); an oscilloscope item (themed as a pencil-servo plotter) showing
  real waveforms. **Readouts teach** (settled 2026-07-06): tooltips name
  the condition in words and point upstream, never just numbers — "0 A —
  circuit incomplete", "8.2 V (rated 12 V) — undervoltage", "14 A through
  an 8 A-rated section" (attribution already knows the weakest voxel);
  electrode tooltips attribute contact-resistance drop and wetness;
  alternators show live volts and hertz; consumables show mass remaining.
  Breakers report V and I at the moment of trip — V/I ≈ distance to the
  short, one division, so fault-locating is a taught skill rather than a
  mystery walk.
- **R16. The tablet.** Falstad-netlist import; guided lessons + sandbox;
  finding it may gate advanced recipes, completing lessons gates nothing.
  **The vanilla survival handbook is the floor; the tablet is the ceiling**
  (settled 2026-07-06): every mod mechanic ships a handbook entry — the
  handbook is the only teaching channel that reaches tablet-refusers, and
  authoring it is an explicit workstream, not polish.
- **R17. Vanilla microblock integration.** Cable voxels inside chiseled
  blocks are real microblock voxels: cable materials are chisel materials
  (`canChisel: true`), and electrical semantics live in a mod BE *behavior*
  (`IMicroblockBehavior`) attached to the vanilla chiseled-block entity via
  JSON patch — the snow-cover pattern. Pure-cable positions may use a
  dedicated block entity (sparky-style) as an optimization, and face-mounted
  surface wiring may use the decor layer; the extraction layer consumes all
  three representations uniformly. *Justification: feasibility confirmed
  against engine source (2026-07-02); enables the oven-as-chiseled-grooves
  mechanic and dual-occupancy with decorative chiseling. Per the up-front
  decision rule: feasible ⇒ built in from the start.*
  Known constraints from the engine: one block entity per position (hard
  limit — a separate cable BE can never share a position with chiseled
  stone); material blocks get no runtime hooks (all logic on the behavior);
  no pre-edit chisel veto (protecting cable voxels needs a
  `BlockEntityChisel.SetVoxel` override or custom chisel mode); non-chisel
  microblocks aren't re-chiselable (cable-bearing blocks must remain
  `BlockChisel`).

### Stationeers client (with Re-Volt)

- **R18. Adaptor for the vanilla 4-call power API.** Unconverted devices are
  constant-power elements, linearized across ticks (G = P/V_prev², clamped;
  undervoltage ⇒ brownout dropout **with hysteresis** — drop at V_low,
  rejoin at V_high, per-device staggered rejoin delays, and recloser-style
  lockout after repeated brownouts in a short window (manual reset), so an
  overloaded net settles or collapses legibly instead of strobing at tick
  rate — settled 2026-07-06). Native conversions replace the adaptor
  per-device, gradually. Vanilla state stays maintained so removing the mod
  reverts cleanly. Each adapted device carries an across-tick **energy
  ledger** (deliverable this tick = advertised − accumulated debt), so the
  linearization lag cannot be pumped for free energy by oscillating loads
  (adversarial review 2026-07-05; see the energy-accounting rule below).
- **R19. Real voltage tiers.** Voltage is player-facing (device ratings,
  transformer steps), not an internal detail under vanilla watt semantics.
  (Settled with Sukasa, 2026-07-02.)

### Educational content

- **R20. One corpus, three consumers.** Lessons are Falstad-format netlists
  plus teaching narrative (EA `docs/examples` style). The same corpus is:
  (a) tablet tutorial content, (b) documentation examples, (c) golden tests —
  each lesson's netlist is solved in CI and compared against ngspice and
  against the expected values stated in its own text. A lesson that stops
  being true fails the build.

## Non-Goals

- **Frequency-domain-only AC** (phasor as primary mode) — see R3.
- **Semiconductor-level power electronics in VS gameplay** — rectifiers and
  such may exist as behavioral devices; the VS tech fiction is
  early-industrial. (The tablet's curriculum ceiling is AC power:
  transformers, impedance, power factor, synchronization.)
- **Lightning and other no-fault hazards** — vetoed; failures are earned.
- **Random component failure** — wear/degradation must be visible and
  causal (e.g. sun-lamp burnout is expected economics, telegraphed).
- **In-world circuit design tools** — the tablet is a tutorial/sandbox, not a
  CAD tool; the world itself is the design tool and placement is easy (R13).
- **Reusing sparky's code.** Its *design* is the reference (layering, typed
  IDs, incremental compaction); the code is not.
(Microblock dual-occupancy was resolved as feasible and promoted to R17.)

## System Overview

```
                    ┌────────────────────────────────────────┐
                    │              solver core                │
                    │  netlist · stamps · LU · analyses       │
                    │  islands · limits · state snapshot      │
                    └───────┬───────────────┬────────────────┘
                    ┌───────┴───────┐   (same API)
                    │  compaction   │
                    │ series chains │
                    │  attribution  │
                    └─┬───────────┬─┘
             ┌────────┴──┐   ┌────┴────────┐   ┌──────────────┐
             │ VS voxel  │   │ VS tablet   │   │ Stationeers  │
             │ extraction│   │ 2D schematic│   │ adaptor +    │
             │ + devices │   │ + lessons   │   │ MNA networks │
             └───────────┘   └─────────────┘   └──────────────┘
```

Dependency arrows point strictly toward the core; the core knows nothing of
games. (Layering inherited from sparky, where it worked.)

## Simulation Model

Summary here; the full treatment goes in [solver.md](solver.md).

- **DC (Stationeers):** dt = 0.5 s (the vanilla power tick), Backward Euler
  for storage dynamics. No subcycling needed.
- **Transient (VS DC-side):** dt = the 50 ms electrical tick; component values chosen so time
  constants live at human-visible scales (0.1–10 s), where BE at ~50 ms is
  accurate — the EA trick, now documented in EA's own examples README.
- **AC (VS):** islands driven by sinusoidal sources at the generator's
  mechanical frequency subcycle N steps per game tick (N chosen per island
  from its highest frequency; ~20 samples/cycle). Linear islands pay only
  back-substitutions per substep. AC↔DC boundaries (rectifier-flavored
  devices, if/when they exist) are behavioral two-ports coupling islands, so
  each island stays linear in its own domain.
- **Island coupling (transformers):** two device classes, EA-style
  (settled 2026-07-05). *Idealized* transformers (small, local) are
  same-matrix two-ports; *decoupling* transformers (utility-scale,
  pole-to-network) are island boundaries. Gameplay steers long-distance
  transfer toward decoupling types — exactly where thread isolation pays.
  Boundaries exchange amplitude+phase per substep, damped by explicit
  relaxation (solver.md, Islands); lessons that teach reactive behavior
  use idealized transformers (curriculum.md).
- **Nonlinearity budget:** Newton iteration exists (diodes in the tablet
  curriculum need it) but is kept out of the per-substep hot path in world
  islands wherever a behavioral model serves.
- **Mechanical co-simulation:** VS's mechanical network re-sums torque every
  ~100 ms (server-authoritative). Shaft speed enters the electrical sim as a
  piecewise-constant source parameter per tick; the alternator's
  counter-torque reports back time-averaged. Loose coupling is stable —
  both sides have inertia.
- **Generator paralleling:** swing-equation-lite — each generator carries a
  phase angle + angular velocity state with spring-damper coupling through
  the network. Synchronization is a gameplay skill.
- **Energy accounting (design rule, settled 2026-07-05):** energy is
  conserved by construction everywhere — solver, island boundaries, the
  Stationeers adaptor — and modeled inefficiency is *explicit*, dumped as
  heat where the loss physically lives. Accounting surpluses (adaptor
  lag, boundary relaxation) are debited or converted to device heat,
  never stored work; per-boundary and per-adapted-device energy ledgers
  enforce this at runtime, and conservation-under-oscillation is a
  standing property test. Doubly interesting in Stationeers, where the
  dumped heat lands in a real thermal simulation.

## Vintage Story: Game Design

### Progression arcs

**Arc 1 — foundations:** waterwheel/windmill alternator, cables, switches,
the voltaic pile / Daniell cell (weak, high internal resistance, *consumable*
zinc — you burn metal for electricity, which teaches why storage mattered),
resistive lighting (AC + DC), electrified fences, space heaters (greenhouse
economics), the oven, freezer (magic block initially), doorbell/annunciator,
instruments, the tablet.

**Arc 2 — power engineering:** transformers and distribution (voltage tiers,
long runs, real line loss), lead-acid accumulators (the "advanced" battery,
1859-authentic), motors (elevator, quern drive), relays, relay-logic control
circuits (see elevator below), the telegraph (long-distance Morse over real
copper; relays as repeaters when line loss demands them), arc lamps
(brilliant, loud, eat carbon rods, require series ballast — negative
differential resistance as a lesson), generator synchronization, sun lamps
(expensive, wear out — trading ores for food as endgame greenhouse option),
high-pole-count alternators (see frequency below).

**Arc 3 — heavy industry (later):** ore processing, smelting, arc furnaces,
electrolysis/electroplating (Faraday's laws as economics; may pull forward
into arc 2 if a light use case appears). Power-hungry; deliberately deferred.

**Ruins loot:** lithium-ion batteries from the world-before — enormous
capacity, missing all their safeties, and **all of them doomed** (settled
2026-07-06): every looted cell eventually fails violently; age and abuse
only decide when. Per-cell degradation is deliberately *invisible* — the
art style can't honestly telegraph it, and inspection-lottery gameplay is
not the point. The player's mistake is **siting**: deployed in a stone
basement with good ventilation, a cell's inevitable death is a contained
loss; built into a wooden wall, it's the house. The handbook and tablet
both state the class property outright — the intended loop is a temporary
bypass of early bootstrapping for players who already understand power,
paid for in containment, and the standing lesson is why modern cells ship
with the safeties these ones lost. (Also fictional cover for battery
models being behavioral: all batteries ship as electrical models with
chemistry stubbed out; the future chemistry arc may deepen them.)

A future separate arc (chemical/phase-change simulation à la Stationeers) may
reuse the solver: thermal networks are RC circuits (temperature ≡ voltage,
heat flow ≡ current), so the core should avoid electrical-only naming in its
deepest layers. ❓ Scope impact TBD; noted, not designed.

### Device notes

- **Oven:** not a custom block — chisel tracks into stone, lay resistance
  wire (a chisel material, per R17) in the grooves, and the block heats food.
  Confirmed workable against the engine's microblock implementation.
- **Electrified fences:** fall out of bare-wire semantics. Bare wire need not
  be supported at every voxel; a span between two posts renders as a light
  catenary, with the renderer choosing representation by the number of wire
  voxels between supports.
- **Heat is universal:** every device that dissipates power should dump heat,
  making overheating a real design constraint, not a scripted event. Space
  heaters are just the honest version. Engine reality (see
  [vintage-story.md](vintage-story.md)): vanilla rooms have *no* temperature
  state — temperature is computed on demand from climate, and no vanilla
  block warms a room. The mod therefore owns a heat convention: a
  position-indexed heat registry fed by device dissipation, consumed via the
  perish-rate delegate (freezers/food), our own systems, and optionally
  `IHeatSource` (player warmth, firepit-style).
- **Sun lamps need custom growth code:** vanilla crop growth reads sunlight
  only; artificial light does nothing. The sun lamp must override/wrap the
  farmland growth update to inject its light. Costed as part of arc 2.

### Voltage and frequency standards

Manufactured devices carry nameplate ratings; matching them is the player's
job via wheel gearing, pole count, and transformers.

- **Voltage tiers:** 12 V DC is the starter tier — easy and safe to work
  with, but its voltage-drop problems over cottage-scale runs are real and
  pedagogically useful. 48 V and 240 V are the advanced tiers for
  distribution and heavy loads. Devices tolerate roughly ±25% with
  efficiency/lifetime penalties toward the edges (exact curves per device).
- **Frequency:** ~5 Hz is the *natural* frequency of waterwheel/windmill
  alternators — visible lamp flicker included, teaching why the real world
  settled on 50 Hz. Players cannot chase 50 Hz through shaft speed (the
  mechanical network overheats past speed 4.5); the path is
  **high-pole-count alternators** (electrical frequency = shaft speed × pole
  pairs), an advanced craft. The reward is real: transformer iron size and
  cost scale inversely with frequency, so 5 Hz transformers are enormous.
  The easy fix for flicker, meanwhile, is DC lighting. Resistive lamps
  themselves are frequency-indifferent; frequency matters through flicker,
  transformers, and (later) motors.

  **Flicker accessibility (settled 2026-07-05):** 3–30 Hz rhythmic
  flashing is the photosensitive-epilepsy trigger band, and 5 Hz lamps
  sit in it. The client therefore presents a **forced choice on first
  boot, before any devices exist**: render lamp luminance faithfully, or
  low-pass it. The simulation is untouched either way — the oscilloscope
  still shows the true waveform, flicker remains the lesson at default
  fidelity for those who opt in, and DC lighting remains the in-fiction
  fix.

### Grounding model

Settled 2026-07-05; **revised same day** after adversarial review C1
(the SWER-first version failed arithmetic at 12 V: tens-of-ohms
electrodes starve any real starter load) and a two-wire benchmark
(docs/experiments/2026-07-05-backend-competition.md, addendum).

**The default wiring idiom in Vintage Story is two-wire.** The cable
pathfinder routes conductor *pairs* — one gesture lays supply and
return, so R13's frictionless-placement mandate absorbs the extra
conductor entirely. There is no inherent grounding: the solver matrix
is kept grounded by an implicit high-resistance leak (~1 MΩ) from each
device negative to earth — benchmarked at machine-epsilon conditioning
cost, and a 1.5× hot-path / 2.3× refactor multiplier on realistic
(ladder-shaped, post-compaction) circuits, which the budget absorbs
without noticing.

**Earth is still an ordinary solver node** reached through
**per-electrode contact resistance** (tens of ohms, better when wet):
grounding-rod quality stays a real, teachable parameter, and "bare
conductors ground when wet" falls out of the same mechanism. Electrodes
carry their own limit envelope (R7, settled 2026-07-06): sustained
overload dries and then glassifies the surrounding soil, *raising*
contact resistance — honest negative feedback that caps earth's ampacity
per electrode, so electrode farms cost real material and land instead of
deleting the wire-gauge economics. Deliberate wetting (a pool over the
rod) stays legal; it's real practice and a fair reward.
**Ground-return (SWER) becomes a user-visible choice, not the
default** — it earns its keep exactly where the real arithmetic works:
high-voltage distribution (240 V long runs), and milliamp signalling
(telegraph, doorbell, electric fences — historically accurate SWER
territory). The curriculum lesson reframes accordingly: not "this is
how wiring works" but *"copper is expensive — when can you get away
with one wire?"*, with the electrode-loss arithmetic as its
predict-then-observe core. The 12 V starter tier survives unchanged; a
copper return fixes its arithmetic.

Deliberately *floating* systems (isolation transformer + no earth
reference) are now buildable by default, so the safety pedagogy is
stated carefully: one wire of a **verified** floating system is safe to
touch (shock there requires two points). Verification is a taught
ritual — the multimeter reads wire-to-earth voltage from day one, and
an isolation-monitor device (a lamp pair to earth; actual historical
practice) is the advanced build's companion. Rain-wetted bare spans can
silently re-ground a floating system; the monitor is how you know.

**Stationeers has no wiring choice** — no cable voxels, and vanilla
devices have a single power terminal, so a SWER-vs-two-wire decision
cannot exist there. Re-Volt therefore uses implicit ground-return with
a near-ideal return conductance per network: numerically identical to
modeling the return conductor, while the fiction describes an ordinary
two-wire cable bundle. The full double-wire solve is never paid.
**API consequence:** the wiring model is a per-client choice the core
must express — netlist construction is explicit about both device
terminals, and a client either binds negatives to routed return nodes
(VS) or to its network's reference node (Stationeers). The API design
(experiments/api-competition/synthesis.md) carries this forward.

### Elevator control: pure electromechanics

The elevator's control system is not a separate signal network — it is just
more MNA at low voltage: supply, call buttons, relays, limit switches, wired
by the player into an actual relay-logic controller. (EA precedent: its
low-voltage components were lightweight skins over the same solver; here we
skip the skin.) "I built the elevator's brain out of relays" is the peak
educational payoff of the mod. Because that is genuinely hard, the tablet
ships an elevator-control lesson, and a craftable pre-wired controller block
exists as training wheels — a convenience, never a requirement.

### Hazards

EA-flavored, minus the TNT: overloaded cables smoke, then melt/ignite (VS
wooden buildings make fire a genuine threat). Sustained low-level overload
makes surface voxels of the cable disappear — visibly ruining insulation and
worsening the problem until it burns through or burns the house down.
Degradation is the warning. Shock damage uses voltage relative to global
ground with realistic thresholds, and first-contact damage scales with
voltage: 12 V control wiring is a tingle, 48 V a bite, 240 V lethal.

**Hidden conductors telegraph themselves** (settled 2026-07-06): block
info on a chiseled block discloses an energized conductor inside (R15's
default-on surface — a guest must never die to wiring the block itself
wouldn't admit to), and energized AC conductors hum audibly at close
range. The hum is diegetic, real, and a free lesson — DC's silence is
itself diagnostic.

**Cable voxels are never protected from the chisel.** Chiseling out cable
material salvages it at a loss (wire bits, not intact wire); insulation
chisels separately (it is a separate material). Chiseling into a *live*
conductor shocks you — the protection mechanism is turning off the breaker
first, i.e. lockout-tagout as an emergent skill. If a player wants to chop
their cable in half, they presumably have a reason; if they told a friend to
do it, the reason is presumably amusement. Salvage and burn-off are lossy
in **mass**, not just convenience: wire bits re-smelt to less metal than
went in, and overload-destroyed insulation is simply gone — no
strip-and-resmelt loop (settled 2026-07-06).

Griefing beyond physics — sabotage of unclaimed long runs, energized
traps — is a **server-governance problem**: vanilla claims, block
logging, and bans. The mod contributes legible fault-finding (R15's
breaker trip reports, the AC hum) and adds no protection special cases.

### The tablet

Ruins-loot relic: a surviving educational device from the world-before, which
fits VS's fallen-civilization fiction exactly. Lessons follow the EA examples
structure (netlist + narrative + predict-then-observe Q/A), ordered DC
fundamentals → RC/RL → AC → transformers → power factor → synchronization.
See [curriculum.md](curriculum.md).

## Stationeers: Integration

Summary; full treatment in [stationeers.md](stationeers.md).

- Graph at cable-segment granularity from Re-Volt's export
  (`CableInfo {RefId, Connections}` + device port identity, cable type, and
  fuse positions — additions agreed with Sukasa). Series compaction (R10) is
  critical: big bases reach ~10k cable segments, ~100 devices.
- Solver islands = union-find over CableNetworks joined by closed coupling
  devices (breakers/transformers); vanilla networks never merge.
- Incremental hooks: CableNetwork Add/Remove(cable), Add/RemoveDevice, Merge.
  Whole save rebuilds all networks on load — initial build is a perf case of
  its own.
- Persistence: device state rides the vanilla driver; network/solver state
  needs our own save/load hook (Sukasa confirms feasible).
- Legacy-device adaptor per R18; Harmony-injected `MNACableNetwork` carries
  the network→nodes mapping.

## Performance Targets

| Context | Target |
| --- | --- |
| VS electrical tick (50 ms — our own tick listener; VS mods choose their interval) | ≤ 5 ms on-thread, ≤ 20 ms off-thread; aspire to less on-thread |
| VS largest sensible base | ~few hundred components post-compaction (mostly cable resistors, ~a dozen motors) |
| VS threading | transformers are isolation points; islands schedule across workers |
| Stationeers | fit inside Re-Volt's half-second power tick on its worker thread; ~10k raw cables compact to a graph in the low hundreds |
| Pathological builds | degrade legibly (R9); players who build to infinity are told to bring a wide CPU |

## Testing and Validation

Summary; full treatment in [testing-strategy.md](testing-strategy.md).

- **Oracle tests:** netlists solved by our core and by ngspice, node voltages
  diffed within tolerance. This is how a team without an EE ships a correct
  solver. (Approach independently validated by current EA development.)
- **Lesson corpus as goldens** (R20): every tablet lesson is a CI test.
- **Invariant checks:** KCL residual ≈ 0 at every node after every solve;
  energy conservation over transient runs. Cheap, always-on in debug builds.
- **Drift detection:** incremental topology state periodically diffed against
  a from-scratch rebuild (the resync backstop, R11).
- **Benchmarks:** sparky's discipline carries over (BenchmarkDotNet,
  compare-against-parent-commit tooling).

## Repository Layout and Licensing

The repository is **manatee** (jj, colocated git). Layout: `core/`
(manatee-core: solver + shared reduction layer, plus tests/oracle/
benchmarks), `tablet/` (the schematic engine `Manatee.Schematic` and the
Avalonia dev harness — see [harness.md](harness.md)), `lessons/` (the
R20 corpus), `vintagestory/` (the Manatee mod, later), `docs/`, and
`third_party/` for the reference checkouts (`ElectricalAge/`, `revolt/`,
`vsapi/`, `vsessentialsmod/`, `vssurvivalmod/`, `sparky/`) — reference
material, not built, not licensed as part of this project. The Stationeers
integration lives in Re-Volt's repository, consuming manatee-core as a git
submodule.

Licensing: Re-Volt is MIT, so manatee-core is MIT. CSparse.NET (LGPL) is a
dev/test-only dependency (equivalence oracle — solver.md Numerics, revised
2026-07-06) and ships in no mod, so no LGPL artifact reaches players.

## Delivery Order

1. **manatee-core + the 2D schematic harness** — the harness is
   simultaneously the dev/test bed, the tablet's engine, and the lesson
   authoring tool.
2. **Stationeers (Re-Volt) DC integration** — simplest electrical model,
   harshest runtime constraints; stress-tests the core API while Sukasa is
   engaged, before a VS layer exists to be disrupted by API churn.
3. **Vintage Story (the Manatee mod)** — the largest integration layer,
   built against a core API that has survived contact with reality.

## Open Questions

None blocking. Resolved in the final interview round (2026-07-02): project
name (Manatee / manatee-core), cable-voxel chisel semantics (no protection;
salvage at a loss; live-wire chiseling shocks — see Hazards), battery fiction
(voltaic pile → lead-acid → ruins-loot li-ion, chemistry stubbed behind
behavioral models), arc placement for arc lamps / relays / telegraph /
electrolysis / doorbell, elevator control philosophy (pure relay
electromechanics + tablet lesson + optional pre-wired controller block),
voltage/frequency standards (12 V / 48 V / 240 V; 5 Hz natural, 50 Hz via
pole count), and delivery order. Earlier rounds resolved microblock
dual-occupancy (→ R17) and the room-heat and catenary-rendering surfaces
(→ vintage-story.md).

Doc-review round (2026-07-05) resolved: electrical tick = 50 ms (VS mods
own their tick listener interval; the earlier 33 ms figure was spurious);
transformer types split idealized (same-matrix) vs decoupling (island
boundary); the relay-vs-breaker duality (netlist switch vs island-coupling
device — solver.md); switch on/off resistances tightened to
SPICE-conventional values plus an explicit conductance-range policy
(solver.md, Numerics); AC substep count quantized with hysteresis; adapted
Stationeers generators get internal series resistance and an across-tick
current clamp; island rebuilds coalesce to once per tick; limit
attribution is a per-limit-type, environment-adjusted envelope
(compaction.md); the oscilloscope contract is two probes. A same-day
follow-up resolved the remainder: boundary couplings exchange
amplitude+phase per substep with explicit relaxation (physical transformer
storage is modeled but not relied on; DC converter two-ports carry a real
DC-link capacitor — solver.md, Islands); grounding was first settled as
SWER-the-starter-idiom, then revised the same day to two-wire-default
with SWER as a user-visible choice (see Grounding model); and the solver backend is an
interface — first settled as dense-primary with CSparse fallback, then
revised 2026-07-06 on benchmark evidence to in-house sparse LU as the sole
production backend (solver.md, Numerics).

Deferred by design: chemistry-arc details (batteries ship as behavioral
electrical models; manatee-core avoids electrical-only naming in its deepest
layers so thermal-RC reuse stays open), and exact device parameter tuning
(playtesting).
