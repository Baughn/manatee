# Testing Strategy

Last updated: 2026-07-06
Status: IMPLEMENTED — the suite described here is built and standing
(Oracle/, Lessons/, Solver/, Netlist/, Reduction/, State/, Devices/ in
`core/Manatee.Core.Tests`); the toolchain bullets below are as-shipped.

The team's constraint shapes the whole strategy: nobody here is an
electrical engineer. Correctness therefore never rests on "we derived it
right" — it rests on **oracles** (mature simulators), **invariants**
(conservation laws checked mechanically), and **equivalences** (two paths to
the same answer must agree). The math is treated as untrusted input.

## Table of Contents

1. [Principles](#principles)
2. [Toolchain](#toolchain)
3. [Oracle Tests (ngspice)](#oracle-tests-ngspice)
3. [The Lesson Corpus as Goldens](#the-lesson-corpus-as-goldens)
4. [Invariant Checks](#invariant-checks)
5. [Equivalence Tests](#equivalence-tests)
6. [Property and Fuzz Tests](#property-and-fuzz-tests)
7. [Benchmarks](#benchmarks)
8. [Game-Layer Testing](#game-layer-testing)

---

## Principles

(Inherited from the dessplay playbook, adapted.)

- **Deterministic and reproducible.** Seeded RNG everywhere; manatee-core is
  deterministic by contract (solver.md), so every failure reproduces from
  inputs alone.
- **Spec-driven.** Tests are written from design.md/solver.md, not from the
  implementation. Unclear spec ⇒ fix the spec first.
- **Regression tests first.** A bug fix lands with the test that reproduces
  it, in a persistent regression corpus (sparky's `testdata/regression`
  habit).
- **Trust nothing derived.** Any stamp, companion model, or convergence
  trick must be pinned by at least one oracle test before it ships.

## Toolchain

Settled 2026-07-05, implemented and in daily use by the full suite:

- **Devshell** (`flake.nix`): `dotnet-sdk_8` + `ngspice`, pinned by
  `flake.lock`; CI runs `nix develop --command`, so CI and local use the
  same compiler and the same oracle binary.
- **Targets:** `Manatee.Core` multitargets `netstandard2.1;net8.0` —
  the ns2.1 ceiling comes from Re-Volt's Unity 2022.3, net8.0 from VS
  mods and all dev-side projects. Central package management
  (`Directory.Packages.props`); warnings are errors.
- **Frameworks:** xUnit for tests, **CsCheck** for property/fuzz tests,
  **Verify** for snapshot goldens (display lists, deck emission),
  BenchmarkDotNet for benches.
- **Oracle policy:** tests tagged `Category=Oracle` **hard-fail** when
  ngspice is missing — a silently skipped oracle is the one failure mode
  this strategy cannot afford. Fast inner loop:
  `dotnet test --filter 'Category!=Oracle'`.
- The tablet/harness testing model (event scripts, display-list
  snapshots, bounded pixel goldens) is specified in
  [harness.md](harness.md).

## Oracle Tests (ngspice)

The primary correctness mechanism. A test-only translator emits SPICE decks
from manatee netlists; ngspice runs them (`.op` for DC, `.tran` for
transient); node voltages and branch currents are diffed within tolerance.

- Tolerances: DC 0.1% relative (or 1 µV absolute near zero); transient
  compared at matched timesteps with looser tolerance (Backward Euler vs
  ngspice's default integrator differ legitimately — force `.options
  method=gear maxord=1`-style matching where exactness matters, or compare
  settled values only).
- ngspice is a dev/CI dependency only, never shipped. On NixOS:
  `nix run nixpkgs#ngspice`; CI pins the version.
- Coverage target: every component type × every analysis it supports, plus
  every lesson netlist (below), plus the regression corpus.
- What ngspice cannot oracle (behavioral two-ports, swing-lite coupling,
  limit events): covered by invariants + hand-computed cases documented
  inline with their arithmetic, so a reviewer can check by calculator.

## The Lesson Corpus as Goldens

Design.md R20 — one corpus, three consumers. Each lesson is a directory
(EA `docs/examples` format): a Falstad-format netlist + a Markdown narrative
containing **expected observations with numbers** ("the capacitor should
read about 2.7 V after 4.5 s").

CI consumes it twice:

1. **Oracle pass** — lesson netlist through manatee and ngspice; must agree.
2. **Narrative pass** — machine-readable expectations (a small front-matter
   block per lesson: probe, time, value, tolerance) checked against the
   manatee solution. *A lesson that stops being true fails the build.*

**Corpus population status (2026-07-06):** the gate mechanism is complete and
auto-discovers new lesson directories; the corpus currently holds lessons
01–02 (DC) and 04 (RC transient — this is the lesson exercising the transient
narrative and transient-oracle branches, including the flagship "about 2.7 V
left across the resistor at 4.5 s" number). The remaining curriculum.md
lessons (03, 05–17, including the AC/subcycled arc) are **deferred authoring
work**, not standing coverage — the goldens above assert only what is in the
corpus today.

The Falstad importer (netlist parser) is thus core infrastructure, not
tablet polish — it's written first, with the EA examples as its initial
test data.

## Invariant Checks

Always-on in debug builds, assertable in any test:

- **KCL residual**: currents into every node sum to ~0 after every solve.
  Catches stamp bugs immediately and cheaply.
- **Energy audit**: over any transient window, source energy = dissipated +
  Δstored, within integration error. Catches sign errors and companion-model
  state bugs.
- **Coupling conservation**: island-coupling devices never transfer more
  energy than the source island delivered (no free energy at boundaries).
  Sharpened 2026-07-05: boundary and adaptor energy *ledgers* must stay
  within tolerance over any window — the property tests drive oscillating
  loads (square waves near 1/α, tick-rate toggles against adapted
  generators) and assert net energy conservation over the window, not
  just per-exchange clamping.
- **Finiteness**: no NaN/Inf anywhere in a solution vector, ever (solver.md
  failure handling).
- **Coupling dissipativity** (added 2026-07-06, the 2f-aliasing guard): a
  closed motor→shaft→alternator→motor loop must monotonically decay from
  any initial condition. Both integrators are dissipative, so any failure
  indicts the mech↔elec coupling — specifically the counter-torque
  averaging window (vintage-story.md, Mechanical Network Coupling).

## Equivalence Tests

Two paths, same answer — these carry the compaction layer:

- **Reduction equivalence**: any graph solved raw vs. series-collapsed must
  produce identical terminal voltages/currents, and probe interpolation
  must match the raw interior nodes. Run over the whole regression corpus
  and over generated random ladders.
- **Incremental equivalence** (drift detection as a test): any sequence of
  tier-3 edits vs. a from-scratch rebuild of the final state must produce
  identical islands and solutions. Randomized edit sequences, seeded. The
  same comparison ships in-game as the background resync backstop (R11).
- **Snapshot round-trip**: solve → snapshot → restore → step; must match
  never-snapshotted run bit-for-bit.

## Property and Fuzz Tests

- Random linear circuit generation (seeded): random R/V/I ladders and
  meshes, guaranteed connected, solved and invariant-checked; a sample also
  cross-checked against ngspice nightly.
- Newton robustness: randomized diode circuits sweeping source values
  through the knee; must converge or Fault legibly — never NaN, never hang.
- Adaptor stability (Stationeers): randomized constant-power load sets
  under falling supply; must settle or brown out within k ticks, never
  oscillate unboundedly. The same harness covers island-coupling
  boundaries (decoupling transformers, converter two-ports; per-substep
  amplitude+phase exchange with relaxation — solver.md, Islands):
  randomized load steps across a boundary must settle, never ring
  unboundedly.
- Conductance-range sweep: random circuits with component values spanning
  the full legal conductance range (solver.md numerics policy); solutions
  must stay finite and within oracle tolerance across the spread.

## Benchmarks

Sparky's discipline carries over and is shipped as `scripts/bench.sh`
(`smoke`/`run`/`compare`): BenchmarkDotNet with MemoryDiagnoser, a jj-aware
`@` vs `@-` compare that diffs the two report CSVs on Mean and Allocated, and
jj commit trailers recording the delta against the parent commit.

Standing suites: ladder DC (tier-1 path), switch-toggle refactor (tier-2),
bulk topology build (tier-3 + Stationeers load-time case, 10k segments),
subcycled AC island per-tick cost, and the zero-allocation steady-state
assertion (fails CI if tiers 1–2 allocate after warmup).

**Flake isolation for the zero-alloc gate (operational fact — read before
adding a ZeroAlloc test).** The alloc assertions read
`GC.GetAllocatedBytesForCurrentThread`, and that per-thread counter *over-reports*
when GC/JIT machinery retires this thread's partially-used allocation quantum
mid-measurement — a step that truly allocates nothing can show a small
(hundreds-of-bytes) phantom delta. Two sources drive it: (1) concurrent
compacting GCs from promotion-heavy sibling tests on other xUnit worker threads,
and (2) *process-wide* background GC and tiered-JIT recompilation that outlive
any collection boundary (heavy siblings like the ngspice Oracle suites leave
background GC threads that fire during the measured turn). Every such test lives
in the serialized `ZeroAlloc` collection (`ZeroAllocCollection`,
`DisableParallelization = true`), which removes source (1) only. Serialization is
therefore **best-effort noise reduction, not the correctness mechanism.** The
sound mechanism — required for every gate in the collection — is
**min-over-N-sub-runs** (the `TierBudgetGateTests` pattern): the perturbation is
strictly additive, so a single clean sub-run (min == 0) proves the zero-alloc
property, whereas a single-shot delta assertion is a flake, not a gate. Also
warm up first (the first `Factorize` allocates the symbolic pattern; the second
lands on the frozen-refactor path), and note the gates auto-skip where
`GC.GetAllocatedBytesForCurrentThread` is inert (historically Mono) — see the
§8 capability probe, `ZeroAllocGates.CounterIsReliable`.

## Game-Layer Testing

- **The 2D harness is the integration test bed** — it exercises netlist,
  devices, reduction, persistence, and rendering-adjacent probe APIs with
  no game engine in the loop. Scripted harness scenarios (build, edit,
  save/load, fault) run headless in CI.
- **VS and Stationeers layers** get thin automated coverage (extraction
  unit tests against recorded voxel/export fixtures) plus documented manual
  test protocols. In-game verification is manual by nature; the sparky
  `read-vs-logs` pattern (user plays, logs are read back) continues.
- Fixture rule: every in-game bug that can be captured as an extraction
  fixture or netlist becomes a regression test; only genuinely
  engine-interactive bugs stay manual.
