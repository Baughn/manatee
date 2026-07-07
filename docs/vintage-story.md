# Vintage Story Client

Last updated: 2026-07-05
Status: DRAFT — engine-integration facts recorded; tablet-host defaults
reviewed 2026-07-05.

Companion to [design.md](design.md). This doc covers the VS-specific layer:
voxel extraction, engine integration seams, devices, and the tablet host.
Engine references are against the public source checkouts at repo root
(`vsapi/`, `vssurvivalmod/`, `vsessentialsmod/`), read 2026-07-02.

## Table of Contents

1. [Microblock Integration (R17)](#microblock-integration-r17)
2. [Mechanical Network Coupling (R14)](#mechanical-network-coupling-r14)
3. [Rooms and Heat](#rooms-and-heat)
4. [Tooltips and Instruments (R15)](#tooltips-and-instruments-r15)
5. [Wire Rendering](#wire-rendering)
6. [The Tablet Host](#the-tablet-host)

---

## Microblock Integration (R17)

Feasibility was confirmed against engine source on 2026-07-02. Facts below
are read directly from the code, not inferred, except where noted.

### Engine facts

**Storage.** `BlockEntityMicroBlock`
(`vssurvivalmod/Systems/Microblock/BEMicroBlock.cs`) stores geometry as
`List<uint> VoxelCuboids` (line 98) — greedy-merged cuboids, not a dense
16³ array. Bit layout (lines 91–97; encode `ToUint` :1229, decode `FromUint`
:1247): bits 0–11 = min x/y/z, 12–23 = max−1 x/y/z, **24–31 = material
index**. Materials are full vanilla block ids in `int[] BlockIds` (:107),
palette up to 256 per block. Serialized as `"cuboids"` + `"materials"` tree
attributes (:1201). Sparky's block-entity format was a reimplementation of
exactly this structure, so the formats are 1:1 compatible.

**One block entity per position is a hard engine limit**
(`vsapi/Common/API/IBlockAccessor.cs:502`; one BE per chunk index3d). There
is no multi-BE mechanism. Consequence: a standalone cable block entity can
never share a position with a vanilla chiseled block. Dual occupancy is only
achievable *inside* the microblock BE.

**Any block can be a chisel material.** `BlockEntityChisel.AddMaterial`
(`BEChisel.cs:457`) appends any block id; the whitelist only governs what can
be *converted into* a chiseled block (`ItemChisel.IsValidChiselingMaterial`,
`ItemChisel.cs:309–361`), and a block attribute `"canChisel": true` forces
acceptance (:331). So cable-material blocks (copper wire, heating wire,
insulation) can be legal chisel materials, and cable voxels become ordinary
microblock voxels — meshed, culled, selected, and collided by the vanilla
pipeline for free (`CreateMesh`, `BEMicroBlock.cs:1507`).

**Material blocks get no runtime hooks.** Once a position is a
`chiseledblock`, the position's Block is the microblock; material blocks are
dereferenced only for textures/light/sounds/resistance. No tick, no
interaction, no behaviors are ever invoked on them. **All electrical
semantics must live on the block entity or an attached BE behavior.**

**The BE-behavior seam is first-class and proven.**
`IMicroblockBehavior` (`vssurvivalmod/Systems/Microblock/IMicroblockBehavior.cs`)
receives `RebuildCuboidList` on every voxel edit (fan-out at
`BEMicroBlock.cs:907`), `RegenMesh`, and `RotateModel`. Behaviors attach to
the vanilla BE via block-JSON `entityBehaviors` (instantiated in
`BlockEntity.CreateBehaviors`, `vsapi/.../BlockEntity.cs:103`) — registrable
by mods (`RegisterBlockEntityBehaviorClass`; vanilla example registration at
`vssurvivalmod/Systems/Core.cs:732`) and patchable onto `chiseledblock` with
a JSON patch. `BEBehaviorMicroblockSnowCover`
(`BEBehaviorMicroblockSnowCover.cs`) is the vanilla template: it keeps its
own parallel cuboid list, serializes its own state, and overlays its own mesh
(:236–309). A behavior can register tick listeners in `Initialize`.

**Chiseling has no pre-edit veto.** Interaction flows
`ItemChisel.OnBlockInteract` (`ItemChisel.cs:364`) →
`BlockEntityChisel.UpdateVoxel` (`BEChisel.cs:151`) → `ChiselMode.Apply` →
`SetVoxel` (`BEChisel.cs:300`, base `BEMicroBlock.cs:674`); behaviors are
notified *after* the change. Protecting cable voxels from chiseling requires
overriding `SetVoxel`/`UpdateVoxel` (subclass, replacing the entity class) or
a custom `ChiselMode`. Server authority runs through packet 1010
(`BEChisel.cs:200, :224`).

**Re-chiselability.** `ItemChisel.IsChiselingAllowedFor` (:298) only permits
chiseling `BlockChisel` instances — plain microblocks can't be re-chiseled.
Cable-bearing blocks must remain `BlockChisel` to stay editable
(`canChisel` only governs converting a non-microblock block into a
`chiseledblock`, not re-editing an existing microblock).

**Decor layer.** The microblock BE implements `IAcceptsDecor`
(`BEMicroBlock.cs:74, :2485–2500`); the chunk decor layer supports per-face
16×16 subposition attachment (`DecorBits`,
`vsapi/.../DecorBits.cs:49–75`). Decor is a face overlay (~1/32 thick), not
volumetric — suitable for surface-mounted wiring on arbitrary blocks, not
for interior runs.

### Chosen architecture

Three coexisting representations, consumed uniformly by the extraction layer:

1. **Shared blocks (dual occupancy):** cable voxels are microblock voxels of
   cable materials inside the vanilla `BlockEntityChisel`; a mod
   `IMicroblockBehavior` attached by JSON patch scans `VoxelCuboids` /
   `BlockIds` on `RebuildCuboidList` to maintain the electrical graph, and
   ticks for heat. This is what makes the oven (wire in chiseled grooves)
   work.
2. **Pure-cable blocks:** a dedicated block entity (sparky-style, same cuboid
   format) as the common fast path for cable runs — no chisel interaction
   surface needed, cheaper scanning. Cannot share a position with chiseled
   stone (engine limit), which is fine: it owns its positions.
3. **Surface wiring (optional/later):** decor-layer attachment for
   face-mounted conduit aesthetics.

Chisel semantics are settled (design.md, Hazards): cable voxels are never
protected from the chisel; chiseled-out cable salvages at a loss (wire
bits); insulation is a separate material and chisels separately; chiseling
a live conductor shocks you — lockout-tagout as an emergent skill.

## Mechanical Network Coupling (R14)

The mechanical power subsystem
(`vssurvivalmod/Systems/MechanicalPower/`) is pluggable via
`BlockEntityBehavior` subclasses — no engine changes needed.

**The contract** is `IMechanicalPowerNode.GetTorque(tick, speed, out
resistance)` (`Network/IMechanicalPowerNode.cs:11`): sources return signed
torque; consumers return 0 torque and a positive `resistance`.
`MechanicalNetwork.updateNetwork` (`Network/MechanicalNetwork.cs:203`) sums
torque and resistance across nodes (scaled by `GearedRatio`) and integrates
network speed with a momentum/drag model.

**Alternator** = `BEBehaviorMPConsumer` subclass
(`BlockEntityBehavior/BEBehaviorMPConsumer.cs:68`): override
`GetResistance()` to return counter-torque as a function of electrical load —
it is re-read every network update, so back-coupling is live. Read
`TrueSpeed` (`:23`) for shaft speed → electrical frequency (× pole pairs)
and EMF.

**Motor** = `BEBehaviorMPRotor` subclass (`BEBehaviorMPRotor.cs:10`): drive
the `TargetSpeed` / `TorqueFactor` / `Resistance` virtuals from electrical
input; `GetTorque` (:88) already implements spin-up/over-speed behavior.
Reference implementations: creative rotor (constant), windmill (variable,
recomputed on a 1 s listener).

**Network mechanics:** only producers create networks; discovery flood-fills
through `HasMechPowerConnectorAt` (`BEBehaviorMPBase.cs:171,419,486`).
Server-authoritative: 20 ms mod tick (`MechanicalPowerMod.cs:92`), torque
re-summed every 5 ticks (~100 ms), telemetry broadcast every ~800 ms.

**Cadence consequence for the solver:** mechanical speed (→ AC frequency and
source EMF) updates at ~100 ms granularity while the electrical sim ticks
faster; treat shaft speed as piecewise-constant per electrical tick, and
report the alternator's counter-torque back as an averaged value — the two
simulations co-simulate loosely coupled, which is stable because both ends
have inertia. Overheat exists at node speed > 4.5
(`MechanicalPowerMod.cs:168-189`) — a natural over-rev hazard for motors.

**The averaging window is normative** (settled 2026-07-06, closing the
2f-aliasing hole): single-phase AC power pulsates at twice electrical
frequency — ~10 Hz at 5 Hz, right at the ~100 ms mechanical re-sum
cadence — so a *sampled* counter-torque would alias into a
phase-dependent DC torque offset (phantom or free torque).
`GetResistance()` therefore returns the **mean over all electrical
substeps since the previous mechanical read**, never an instantaneous
sample; it must also be a pure read of that cached mean, valid at any
time on the server main thread (out-of-band `updateNetwork` calls exist —
`BEBehaviorWindmillRotor.cs:197`). The standing invariant test: a closed
motor→shaft→alternator→motor loop must monotonically decay from any
initial condition (testing-strategy.md).

**Frequency arithmetic** (verified 2026-07-05, decided 2026-07-06): the
engine integrates shaft angle such that ω = 5·speed rad/s
(`MechanicalNetwork.cs:156-158,185-189`), so electrical frequency
f ≈ 0.8 × speed × polePairs Hz. **No fudge factor.** Pole-pair count is
the honest knob: a starter alternator at 2–4 pole pairs gives ~3–6 Hz at
ordinary shaft speeds; the advanced high-pole-count machine at ~12–20
pairs reaches ~40–60 Hz near (but under) the overheat ceiling — the same
shape as real hydro alternators. The economics are deliberate:
**pole pieces are hand-smithed on the anvil** (VS's smithing minigame),
so high pole count costs labor as well as metal, and the future workshop
arc (1800s-tech powered machinery — drop hammers, rolling mills, gang
punches; historically exactly how repeated identical parts like pole
laminations were made) becomes the earned escape from hand-forging.

## Rooms and Heat

**Room detection** exists (`vsessentialsmod/Systems/RoomRegistry.cs`;
BFS ≤14³ over heat-retaining walls, `GetRoomForPosition` :346), and rooms
carry skylight/cooling-wall/exit counts — **but rooms have NO temperature
state.** Temperature is always computed on demand from world climate
(`InWorldContainer.GetPerishRate()`,
`vsessentialsmod/Inventory/InWorldContainer.cs:173`), blending climate temp,
skylight, cooling walls, and a constant cellar temp. No vanilla block raises
a room's temperature: `IHeatSource` (firepit, forge) affects only *entity
body temperature* (`BehaviorBodyTemperature.cs:354`), never spoilage or
crops.

**Consequence: the mod defines its own heat convention.** Plan:

- A mod-owned **heat registry** (position-indexed heater strength per room,
  maintained by heat-dissipating device behaviors — every electrical device
  publishes its dissipation, satisfying design.md's "heat is universal").
- **Freezer:** container BE hooks the perish-rate delegate
  (`Inventory.OnAcquireTransitionSpeed`, `InWorldContainer.cs:84,160`) and
  returns a rate scaled by its electrical duty. This is the vanilla-blessed
  lever; `ConstantPerishRateContainer` is the minimal pattern.
- **Space heater / ambient effects:** other containers and our own systems
  query the heat registry and fold it into their perish/comfort math.
  Optionally also implement `IHeatSource` so heaters warm players like a
  firepit does.
- **Greenhouse bonus** precedent: beehive computes
  `roomness` (skylit + no exits ⇒ +5 °C on demand, `BEBeehive.cs:175-177,
  :207`). Our heaters can extend the same on-demand pattern.

**Sun lamps require custom growth code.** Crop growth and the warmth proxy
read **sunlight only** (`EnumLightLevelType.OnlySunLight`; farmland growth
factors computed in the closed base class, surface at `BEFarmland.cs:64-69,
119`). Vanilla artificial light does nothing for crops. The sun lamp must
override/wrap the farmland growth update (subclass or attached behavior)
and inject its light contribution — flagged as a design cost in design.md.

## Tooltips and Instruments (R15)

`Block.GetPlacedBlockInfo` (`vsapi/Common/Collectible/Block/Block.cs:2268`)
→ `BlockEntity.GetBlockInfo(forPlayer, StringBuilder)` (:2279) is the
WAILA-equivalent hook: override it on our BEs/behaviors to append live
V/I/temperature lines (mech precedent: `BEBehaviorMPBase.GetBlockInfo`
:366).

**Gotcha:** the HUD polls on the **client's** copy of the BE, so displayed
values must be synced. Do NOT `MarkDirty()` per tick (chunk repacket spam);
use a throttled custom net channel like the mechanical mod's ~800 ms
telemetry broadcast (`MechanicalPowerMod.cs:218`). Plan: an `mna-telemetry`
channel broadcasting per-island probe values at ~0.5–1 s, config-scalable;
the oscilloscope item negotiates a temporary high-rate waveform stream for
one probe point only.

## Wire Rendering

Two mechanisms, both needed:

- **Voxel cable meshes:** chunk-baked via `OnTesselation` +
  `mesher.AddMeshData` (pattern: `BEBehaviorMPCreativeRotor.cs:60`);
  retesselate on state change with `MarkDirty(true)`. For many identical
  segments, the instanced-rendering pattern of `MechNetworkRenderer`
  (`MechanicalPower/Renderer/MechNetworkRenderer.cs:14` — one draw call per
  shape, per-instance transforms) is the efficiency precedent.
- **Catenary spans between posts:** the rope/cloth system
  (`vsessentialsmod/Systems/Cloth/`) is the vanilla precedent for geometry
  spanning arbitrary distance between two pinned endpoints
  (`ClothSystem.CreateRope` :142, `ClothPoint.PinTo` :103/:117), with
  map-region persistence and instanced rendering. For wires we likely do
  NOT want live mass-spring physics (cost, wobble, rip): preferred plan is
  a static catenary mesh in our own `IRenderer` between endpoint BEs,
  borrowing the cloth code's segment-transform math and
  two-endpoint activation model. Design.md's renderer-selection rule
  (representation chosen by wire-voxel count between supports) plugs in
  here: short spans render as voxel geometry, long spans as catenary curves.
  Gotchas: generous `RenderRange` for long spans; the cloth system
  persists each span into the single map region containing its start
  point (`ClothManager.cs:588-606`, keyed on `cs.FirstPoint.Pos`), and
  its own comments (`ClothManager.cs:583-585`) flag cross-region spans
  as an unhandled open question — wire persistence must not inherit that
  gap: either avoid region-boundary spans or handle them explicitly.

## The Tablet Host

Proposed defaults (2026-07-05):

- **Client-side only.** The tablet's islands run entirely on the client;
  lessons and the sandbox never touch the server. Core determinism holds
  by contract (solver.md), so lesson goldens behave identically in-game
  and in CI.
- **Fixed sim dt (10 ms)**, stepped from the client tick via an
  accumulator; sim time is decoupled from world time and frame rate.
  Fixed dt keeps the tablet on the tier-1 fast path.
- **Paused while closed.** The sim steps only while the GUI is open;
  reopening resumes from a snapshot (R6).
- **Persistence:** lesson progress and sandbox netlists ride player
  attributes as Falstad text — small, human-readable, and the same format
  the importer already parses.
- **Instruments:** the tablet scope is the primary consumer of the
  two-probe contract (solver.md, Probes) — the phase lessons (curriculum
  15/17) compare two traces.
