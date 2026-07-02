# manatee-core: Solver

Last updated: 2026-07-02
Status: DRAFT — API shape and analysis design; no code yet.

Implements design.md requirements R1–R9. Game-agnostic, pure C#, no engine
dependencies. Sparky's solver (`sparky/src/mna/`) is the design reference —
its layering, typed IDs, and numerics choices carry over; its code does not.

## Table of Contents

1. [Layering](#layering)
2. [The Netlist API](#the-netlist-api)
3. [Change-Cost Tiers](#change-cost-tiers)
4. [Analyses](#analyses)
5. [Component Set](#component-set)
6. [Islands](#islands)
7. [State, Limits, Probes](#state-limits-probes)
8. [Numerics](#numerics)
9. [Failure Handling](#failure-handling)
10. [Threading and Allocation Contract](#threading-and-allocation-contract)

---

## Layering

```
manatee-core
├── solver     — matrices, LU, stamps, per-island Circuit
├── netlist    — typed component handles, change tracking, islands
├── devices    — behavioral device models (subcircuits + Tick())
└── reduction  — series-chain collapse, probe interpolation  (see compaction.md)
```

Clients (VS extraction, tablet, Stationeers adaptor) talk to `netlist` and
`devices`; only `netlist` talks to `solver`. Nothing in manatee-core names a
game. Deep layers avoid electrical-only naming where cheap (node potential /
flow, not voltage/current) to keep thermal-RC reuse open (design.md).

## The Netlist API

Retained-mode circuit document with strongly-typed IDs (sparky's pattern:
misuse fails to typecheck).

```csharp
NodeId       AddNode();
ResistorId   AddResistor(NodeId a, NodeId b, double ohms);
VSourceId    AddVoltageSource(NodeId pos, NodeId neg, double volts);
ISourceId    AddCurrentSource(NodeId from, NodeId to, double amps);
CapacitorId  AddCapacitor(NodeId a, NodeId b, double farads);
InductorId   AddInductor(NodeId a, NodeId b, double henries);
SwitchId     AddSwitch(NodeId a, NodeId b, bool closed);   // netlist-level: G toggles 1e9/1e-9
DiodeId      AddDiode(NodeId anode, NodeId cathode, DiodeParams p);
void         Remove(ComponentId id);                        // any typed id

// Tier 1 — RHS only
void SetSourceValue(VSourceId id, double volts);
void SetSourceValue(ISourceId id, double amps);

// Tier 2 — conductance
void SetResistance(ResistorId id, double ohms);
void SetSwitch(SwitchId id, bool closed);

// Tier 3 — topology (batched)
using (netlist.BeginBulkUpdate()) { ... }

void   Step(double dt);          // transient step; dt <= 0 ⇒ DC operating point
double VoltageAt(NodeId n);      // also: CurrentThrough(id), PowerIn(id)
```

Every mutator is tagged in doc-comments with its tier; the tier taxonomy is a
public API concept, not an implementation detail. Grounding: each island
designates node 0 as its reference (see [Islands](#islands)); clients may
mark a node as ground explicitly (VS: the literal earth).

The `devices` layer sits above this: a device is terminals +
`CreateComponents(netlist, terminalNodes)` + `RemoveComponents()` +
optional `Tick(dt)` for behavioral parameter updates (battery discharge
curves, converter two-ports, adaptor loads). Device authors never see
matrices.

## Change-Cost Tiers

| Tier | Examples | Solver cost | Expected frequency |
| --- | --- | --- | --- |
| 1. RHS value | source values; BE companion history terms | forward/back substitution on cached LU | every tick/substep |
| 2. Conductance | switch toggle, variable resistor, Newton iteration, fuse blow | numeric refactorization; symbolic (pattern) reused | occasional; per-Newton-iteration |
| 3. Topology | add/remove component or node | full symbolic + numeric rebuild of the island | construction churn; batched |

Implementation notes:
- Matrix built as coordinate storage with duplicate-summing stamps
  (CSparse); stamps versioned so an unchanged (pattern, values, dt) skips
  straight to the cached factorization (sparky's fast path).
- Tier 2 requires separating symbolic analysis from numeric factorization
  (KLU-style refactor). Sparky refactored from scratch; we don't.
- Fixed dt keeps BE companion conductances constant ⇒ a linear island in
  steady operation lives entirely in tier 1. This is the load-bearing
  performance fact for subcycled AC.

## Analyses

One netlist, multiple analyses (R2). Components provide a time-domain stamp;
a complex-frequency stamp is a later addition (phasor metering optimization
only — R3 makes waveform the primary mode).

**DC operating point** (`dt <= 0`): capacitors open, inductors ~1 nΩ shorts.
Stationeers runs exclusively here plus storage dynamics at dt = 0.5 s.

**Transient**: Backward Euler companion models (L-stable; the "boring but
never blows up" choice). Capacitor: G = C/dt, I = G·V_prev. Inductor:
mirror. State updated post-solve. Trapezoidal is explicitly out of scope
until a concrete accuracy need appears.

**Subcycled AC (VS)**: an island whose sources include sinusoids at
frequency f runs N substeps per game tick, N chosen per island from its
highest source frequency at ≥ 20 samples/cycle (5 Hz → 100 Hz substep rate →
5 substeps per 50 ms tick; 50 Hz via pole count → ~50 substeps). Linear
islands pay tier-1 costs only. Sources are updated per substep by a source
driver (phase-continuous across ticks; frequency is a piecewise-constant
input from the mechanical network, updated at its ~100 ms cadence).

**Nonlinear elements**: Newton-Raphson, re-stamping linearized companions
per iteration (tier 2 each), dual convergence test (scaled step norm AND
residual), iteration cap with diagnosable failure. Diode limiting per SPICE
practice. Policy: nonlinear devices are legal everywhere but discouraged in
world AC islands — converter-style devices ship as behavioral two-ports
(see below) precisely to keep hot islands linear.

**Generator paralleling**: swing-equation-lite lives in the `devices` layer,
not the solver — each generator device integrates rotor angle/velocity with
spring-damper coupling and drives its source's phase. The solver just sees
tier-1 source updates.

## Component Set

Solver primitives: resistor, V/I sources, capacitor, inductor, switch,
diode. Everything else is a `devices`-layer composition:

- **Transformer**: ideal two-port (turns ratio via coupled auxiliary
  equations) + leakage/magnetizing elements as parameters. Doubles as the
  island *coupling* device where thread isolation is wanted (VS) — see
  compaction.md.
- **Converter two-ports** (rectifier-flavored, chargers): behavioral power
  transfer with efficiency curve between two islands; enforce own limits;
  keep both sides linear. No semiconductor-level simulation in world
  circuits (design.md non-goal).
- **Batteries**: behavioral — EMF + internal resistance + state-of-charge
  integrator with per-chemistry parameter sets (pile / lead-acid / li-ion).
  Chemistry arc later deepens parameters, not structure.
- **Machines** (lamps, heaters, motors, alternator): resistive or
  source-like stamps with device `Tick()` computing parameters from game
  state (shaft speed → EMF; filament temp → resistance if we want inrush).

## Islands

An island = one connected component = one matrix = one factorization = the
unit of parallelism. The islands layer:

- Maintains connectivity union-find over the netlist; merge is incremental,
  removal triggers island rebuild (cheap at our sizes; split detection not
  attempted — design.md R11).
- Accepts **pre-partitioned** input (Stationeers hands us CableNetworks) or
  **self-partitioned** (VS/tablet) — islanding is core but optional per
  client.
- **Coupling devices** (transformers, converter two-ports, Stationeers
  breakers) join islands *logically* while keeping them separate matrices
  when the device is a power-transfer boundary, or merge them when it is a
  direct electrical bridge (closed breaker = same matrix). The distinction
  is per-device-type: galvanic connection ⇒ same island; power-transfer
  boundary ⇒ separate islands, coupled by exchanged P/V values with one-tick
  lag. One-tick-lag coupling is stable because both sides have storage; the
  coupling device clamps transfer to what its source island actually
  delivered (no free energy).
- Islands solve in parallel (worker pool supplied by the client — the core
  never owns threads).

Each island anchors its own ground: an explicitly-marked ground node if
present, else an arbitrary node, with gmin (1e-12 S) shunts on non-ground
diagonals to keep floating subgraphs non-singular.

## State, Limits, Probes

- **Snapshot/restore (R6)**: per-island serialization of capacitor voltages,
  inductor currents, device `Tick()` state, and source phase. Versioned
  binary format with a JSON debug form. Round-trip is a CI property test.
- **Limits (R7)**: per-component `LimitConfig` (max |I|, |V|, P, and a
  thermal accumulator for slow-overload modeling — i²t, so fuses and cable
  heating share a mechanism). Violations raise `LimitEvent`s after the
  solve; the client maps them to geometry (compaction layer attributes to
  the weakest element — see compaction.md). The solver never mutates the
  circuit in response; consequences are the client's job.
- **Probes**: named observation points surviving reduction. The reduction
  layer registers interpolators for eliminated nodes so instruments can read
  "inside" compacted runs. Oscilloscope support: a probe can subscribe to
  per-substep sampling into a ring buffer (one probe at a time is the
  expected UI contract; cost is bounded).

## Numerics

- **CSparse.NET** for sparse LU (LGPL — separate unmodified DLL; MIT core).
- Dense in-place LU with partial pivoting below ~100 unknowns or above ~0.18
  density (sparky's measured crossover; re-benchmark, don't assume).
- Symbolic/numeric factorization split for tier 2 (this is the main numeric
  improvement over sparky).
- Complex-valued solve path deferred until phasor metering is wanted; the
  stamp interface reserves room for it (R2/R3).
- All numerics deterministic: no wall-clock, no ambient RNG; identical
  inputs produce identical outputs across runs (regression corpus depends
  on this).

## Failure Handling

R9: players build pathological circuits as a hobby.

- Singular after gmin ⇒ island enters a `Faulted` state with a diagnostic
  (which nodes/components), reported as an event; previous solution is held;
  the island retries on the next tier-2/3 change. No NaN ever leaves the
  API (debug builds assert on non-finite anywhere in the solution vector).
- Newton non-convergence ⇒ per-island fallback ladder: (1) reuse last
  operating point, (2) source-stepping ramp, (3) `Faulted` with diagnostic.
- Contradictions (ideal V-sources in parallel disagreeing) are detected as
  singularity, reported with the participating components named.
- Client-facing rule: a Faulted island reads as de-energized, and the game
  layer decides how to present it (VS: "something smells wrong"; tablet:
  an actual error message with the diagnosis — it's educational).

## Threading and Allocation Contract

The acceptance bar is Re-Volt's worker thread (the stricter environment):

- No engine/Unity/VS API anywhere in manatee-core.
- Steady-state ticking (tiers 1–2) allocates zero bytes after warmup;
  BenchmarkDotNet MemoryDiagnoser enforces this in CI.
- All solver state is confined to its island; islands are independently
  lockable; the API is single-writer-per-island, enforced by debug asserts
  rather than locks (clients own scheduling).
- Determinism per island regardless of thread interleaving (islands don't
  share state; coupling values exchange at tick boundaries only).
