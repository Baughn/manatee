# Documentation plan: condensing the Manatee docs

Last updated: 2026-10-06
Status: proposal for user review. Nothing in this plan has been applied yet.
Companion to the dream-API drafts, which are written separately and land as `docs/api.md`.

> **Note (after review):** `docs/next/api.md` was revised after this plan was written. Where names differ, the API wins: Region became *load area* (`SetAreaLoaded`), `Reference` fidelity became `Strict`, "AC envelope" became *AC value*, `Reset` split into `Rearm`/`ResetState`, `DescriptorConflict` became `PartConflict`, `PowerSource` became `PowerLimitedSource`, and the capability flags are now `MixedFrequency` and `InStepEventTimes`. The size target for `api.md` is about 450-600 lines, with walkthroughs in `guide.md`. Section 6's questions now live in `questions.md` (section D), and questions.md wins wherever the two differ.

## 0. Ground rules for the rewrite

- **Audience.** Game-mod developers who know volts, amps and ohms from games. They are not solver authors. Any circuit idea gets one sentence in systems terms the first time it appears.
- **No method names outside `docs/backends/`.** Words like MNA, stamp, LU, companion model, substep, gmin, Newton, relaxation, phasor or EMT may appear only in backend docs and code comments. Library docs, game docs and tests describe what you can observe.
- **One fact, one home.** Every other doc links to that home. Game design lives in `docs/games/` and library docs never depend on it. Engine facts with file:line references live with their game.
- **History lives in jj.** A doc says what is true now and carries no "settled 2026-07-05, revised same day" narration. Decisions that are still live become one line each in `decisions.md`, with a date.
- **Standard words.** Use EE, SPICE or game vocabulary. A project-coined term is allowed only when no standard term exists, and it must be defined in the glossary. If a term needs a "project-coined" disclaimer, that usually means the API is wrong.

---

## 1. Inventory of current docs

Line counts come from `wc -l`. The tree holds 17,441 lines under `docs/`: 9,592 in the glossary and about 7,850 elsewhere, plus about 230 lines outside `docs/`.

Fates are: KEEP, MERGE (fold the named parts into a new doc, then delete), REWRITE, DELETE (jj history is the archive), HAND-TO-SUKASA and CREATE.

| File | Lines | Fate | Reason / destination |
|---|---:|---|---|
| `docs/design.md` | 634 | MERGE, then delete | Mixes three things: library requirements, VS game design, and solver method. About 40 lines of requirements go to `README.md` and `decisions.md` (restated in §4). About 200 lines of VS game design go to `games/vintage-story.md`, and about 30 lines of Stationeers rules go to `games/stationeers.md`. The Simulation Model (:271-313), System Overview (:250-266) and Open Questions (:598-634) sections are method or process, so they are deleted. |
| `docs/api.md` | 2,041 | DELETE (replaced) | The as-built MNA-shaped API. Being rewritten as the dream API by the drafters. Its §24 decision log (28 rulings) is triaged in §4.3. |
| `docs/solver.md` | 309 | MERGE, then delete | About 40 lines of backend-neutral contract go to `api.md` and `concepts.md`: library knows no games, parts are terminals plus behaviour, I²t is shared by fuses and cables, two-channel scope, determinism, client owns threads. About 80 lines of method (tiers, Backward Euler, substep count, Newton, gmin, conductance range, sparse LU plan) go to `backends/transient-mna.md`. The API sketch at :47-71 and `LimitConfig` at :228 are phantoms, so they are deleted. |
| `docs/compaction.md` | 87 | MERGE, then delete | About 10 lines of observable contract go to `api.md` under Cables: results per original segment, probes anywhere, a reduced build must equal the raw one, ratings adjusted for ambient. The mechanism (series reduction, union-find, Pareto I²t set) goes to `backends/transient-mna.md` and code comments. |
| `docs/integration-tutorial.md` | 570 | REWRITE as `guide.md` (or fold into `api.md`, see Q4) | Keep the tick-loop shape (:221-294), the split between "exceptions are your bug" and "unsolvable is a legal circuit" (:395-401), and the save/load split (:352-393). The handle-survival and re-pin machinery (:148-219, :296-350) disappears. The 17 sharp edges in the appendix (:475-570) become a checklist in `accuracy-and-performance.md`: the dream API must make each one impossible. |
| `examples/RevoltWalkthrough/` (code) | n/a | REWRITE or delete | The tutorial calls it "the runnable authority". It dies with the old API. Rewrite it against the dream API once that exists, so that each walkthrough in the docs has a runnable twin (Q10). |
| `docs/testing-strategy.md` | 214 | REWRITE as `accuracy-and-performance.md` | Keep the principles: reference simulator, invariants, equivalence tests, fuzz axes, toolchain. Drop `MatchBackwardEuler` (:196-199), benchmarks named by tier (:299-302), the essay on flaky zero-allocation tests (:304-324, move to a code comment), the stale "written from design.md/solver.md" (:159-160) and the duplicate TOC "3." (:142-143). |
| `docs/harness.md` | 86 | MERGE into `tablet.md` | Keep the three pure layers (document / interaction state machine / display list), the headless testing model and the rendering backends. Fix the claim about where the Falstad importer lives (:26-28). |
| `docs/curriculum.md` | 85 | MERGE into `tablet.md` | Keep the 17-lesson arc and the authoring rules. Drop the Backward Euler justification for the "EA trick" (:26-27) and the "decoupling boundary launders reactive power" rule (:82-85). Fix the lesson file naming (:17-21). |
| `docs/falstad-format.md` | 124 | MERGE about 25 lines into `tablet.md` | Keep the importer's accept / ignore-with-notice / reject contract (:106-118), the rule that corpus files must load in falstad.com (:120), and the GPL note (:96, :124). The field-by-field tables (:15-104) become comments next to the importer source. |
| `docs/vintage-story.md` | 283 | MERGE into `games/vintage-story.md` | Keep the verified engine facts (microblocks, mechanical power, rooms and heat, tooltips, wire rendering). Delete the Tablet Host section's "fixed 10 ms, tier-1 fast path" (:273-275). Fix the source paths: the checkouts are in `third_party/`, not "repo root" (:9-10). |
| `docs/stationeers.md` | 222 | REWRITE as `games/stationeers.md` | It carries its own "do not treat as canon" banner (:7-17). Keep the verified threading facts (:165-213), persistence and failure/fallback. Drop "Islands and Coupling Devices" and the adaptor energy ledger. Merge in the Re-Volt audit's integration points. |
| `docs/waveform-report.md` | 141 | KEEP as dated input in `docs/research/` + conclusions into `backends/candidates.md` (about 40 lines) (Q11) | User's own research. Uncommitted (`A` in git status). Its premises are wrong for this repo: 1 kHz solver, 30 Hz tick, 50/60 Hz carrier, Apache-Commons path. Correct them to a 50 ms step and 5 Hz natural frequency. The real speedup at the default is 1.3-1.7× net CPU, not ~30× and not the raw ~5× solve count. Its "Phase 1 cheap wins" are already done. Keep the hybrid-backend idea and "detail where observed". Correct the EA reading: `interSystemOverSampling` is not oversampling. |
| `docs/glossary.md` | 1,132 | REWRITE (about 180 lines) | Index plus a nomenclature table with known errors (§3.3). The new file has 40-60 entries for modders. |
| `docs/glossary/circuits.md` | 2,192 | DELETE (about 25 terms survive) | The surviving terms move into the new `glossary.md`. |
| `docs/glossary/programming.md` | 2,134 | DELETE | Teaches CS to EEs, an audience that no longer exists. |
| `docs/glossary/numerics.md` | 759 | DELETE (about 15 terms to `backends/README.md`) | Solver internals. |
| `docs/glossary/project-1.md` / `project-2.md` | 1,475 / 1,499 | DELETE (about 12 terms survive) | 343 project-coined entries, mostly MNA mechanism, with many duplicates: tier ×6, Faulted ×5, island ×~21. |
| `docs/glossary/games.md` | 401 | DELETE (short per-game sections survive) | VS and Stationeers terms go to `glossary.md`'s game section or the game docs. |
| `docs/experiments/2026-07-05-backend-competition.md` | 149 | MERGE about 15 lines, then delete | Baseline perf numbers (10k cold build ~1.8 ms; worst AC n=500 ≈ 460 µs per step) go to `accuracy-and-performance.md` as the baseline to beat. Its line "amendment not yet applied" (:59-60) is stale. |
| `docs/experiments/2026-07-05-adversarial-playtest.md` | 90 | MERGE the table, then delete | All findings were already folded into design.md. Keep the "frustration walk" table (:217-227) in `games/vintage-story.md` as the source of the readouts requirements. M3 was decided against the suggestion. |
| `docs/experiments/2026-07-05-ea-examples.md` | 67 | MERGE 5 lines into `tablet.md`, then delete | EA seeds cover only lessons 1, 2, 4 and 5. Relay and AC lessons need a controlled switch and AC expectation kinds. The "near-verbatim" claim is stale. |
| `docs/experiments/2026-07-05-vs-mechanics.md` | 53 | DELETE | Duplicates vintage-story.md:118-183. Its "deserves a design.md note" (:99, :119) is already resolved. |
| `docs/experiments/api-competition/` (4 .md + judge-verdicts.json) | 1,467 + 149 | DELETE | Process record. The one surviving idea, grounding as a construction-time option (synthesis.md:205-216), is already in the dream API. |
| `docs/reviews/2026-07-04-revolt-audit.md` | 940 | HAND-TO-SUKASA + MERGE | The 38-item bug grid (:101-790) is QA on Re-Volt's code. Send it to Sukasa or the Re-Volt repo. Merge about 40 lines of integration points (:791-940) into `games/stationeers.md`. |
| `docs/reviews/2026-07-06-overnight-build.md` | 138 | DELETE (its items become test scenarios) | The judgment items the user never reviewed become named scenarios in the accuracy suite (§5, item 22). The commit ledger is history. |
| `CLAUDE.md` | 74 | REWRITE (about 40) | Stale: "api.md is the binding as-built surface", "Next: 2D harness", "All major decisions live here". Keep the working agreements, the nix/jj/oracle notes and the Sukasa relay rule. |
| `AGENTS.md` | 74 | Replace with a symlink to CLAUDE.md (Q6) | Byte-identical to CLAUDE.md. |
| `lessons/README.md` + `lessons/README-schema.md` | 13 + 67 | REWRITE | The schema moves into the scenario-format spec (Q3). README becomes a 5-line pointer. The `analysis: dc\|transient` field and "timestep from the `$` header" are dropped. |
| `third_party/CLAUDE.md` | 36 | KEEP | Agent notes on the reference checkouts. Not part of the published doc set. |
| `README.md` (repo root) | does not exist | CREATE | Front door: what Manatee is, who uses it, status, build commands, licence, doc map. |

Totals: about 17,700 lines today, about 2,100 after the rewrite.

---

## 2. Proposed doc set

```
README.md                         front door
CLAUDE.md   (AGENTS.md -> symlink)
docs/
  concepts.md                     electricity for mod developers
  api.md                          the dream API (drafted separately)
  guide.md                        integration walkthroughs (or folded into api.md, Q4)
  accuracy-and-performance.md     test and benchmark contract, scenario format, scorecard
  decisions.md                    register of live decisions, one line each, dated
  glossary.md                     40-60 entries
  tablet.md                       schematic editor, tablet, curriculum, lesson format, Falstad import
  backends/
    README.md                     what a backend must provide; implementer glossary
    transient-mna.md              the current backend
    candidates.md                 hybrid, dynamic-phasor and integrator options
  games/
    vintage-story.md              VS game design and verified engine facts
    stationeers.md                Re-Volt integration, game rules, items pending Sukasa
```

The library docs are README, concepts, api, guide, accuracy-and-performance, decisions, glossary and backends/. They never cite game design as a requirement. They may use games as examples.

| File | Purpose | Audience | Target | Sources |
|---|---|---|---:|---|
| `README.md` | What Manatee is (real circuits for games; EA's spiritual successor), the three consumers, status, build and test commands (`nix develop`, fast vs reference-simulator suites), licence (MIT; CSparse is dev-only LGPL), delivery order, map of the docs, a backend-neutral diagram. | everyone | 80 | design.md Purpose (:31-85), Licensing (:575-583), Delivery Order (:585-594), CLAUDE.md |
| `CLAUDE.md` | Agent instructions: working agreements, jj, nix, `git add` before nix, the Sukasa relay rule, `third_party/` pointer. Links to README for everything else. | agents | 40 | current CLAUDE.md |
| `docs/concepts.md` | Short primer: node (net), terminal, ground vs earth vs common return, source, load, series and parallel, short and open circuit, DC vs AC, RMS vs peak, frequency and phase, real, reactive and apparent power and power factor, capacitor and inductor as stored energy with a time constant, ratings and I²t, why a constant-power load collapses at low voltage, what "unsolvable" means, why readings over a window beat instantaneous ones. A "Coming from Electrical Age" box maps SubSystem, InterSystem and Line to the new terms. | mod devs | 150 | solver.md contract parts, design.md grounding and energy rules, terminology.md §3, curriculum appendices |
| `docs/api.md` | The dream API as if it existed: Circuit, ids, parts catalogue and primitives, edits, Step and scheduling, readings and freshness, oscilloscope traces, protection events, networks and status, mechanical port, snapshots, controllers and registries, fidelity request and backend capability record, grounding convention. | integrators | ≤400 | API drafters; compaction.md observables; solver.md contract |
| `docs/guide.md` | Three worked integrations, each under 50 lines: a Stationeers tick, a VS world plus mechanical shaft, and a tablet lesson run. Save and load. A short "when things go wrong" section (exceptions vs Unsolvable vs events). | integrators | 150 | integration-tutorial.md (shape only), revolt.md, vintagestory.md, tablet.md readers |
| `docs/accuracy-and-performance.md` | The backend-neutral contract. (1) Scenario format: named parts, stimulus timeline at step boundaries, observations as (observable, time or window, value, tolerance), no timestep or analysis field, AC observables (RMS, phase, PF, waveform). (2) Truth sources: analytic first, then ngspice at accurate settings with a convergence check, then a slow reference backend. (3) Accuracy table: DC steady state ≤1e-6 relative; meters and power ≤1%; phase ≤2°; PF ≤0.02; scope ≤3% NRMSE; energy balance ≤0.5% of throughput; trip time ±1 step. (4) Invariants: KCL, energy balance, finiteness. Equivalences: incremental vs from-scratch, snapshot round-trip. (5) Perf budgets and the baseline. (6) Mandatory scenarios (see below). (7) Per-backend scorecard format. (8) Sharp-edges regression checklist. | library devs, backend authors, reviewers | 150 | testing-strategy.md, critic §2 tolerance table, backend-competition.md, design.md perf targets (:543-551), overnight report, integration-tutorial appendix |
| `docs/decisions.md` | Register of decisions that are still live: date, one-line decision, one-line reason, status (live / open / pending Sukasa / reversed-from). No rounds, judges or narration. Open API unknowns appear as `open` rows: game time vs wall time, step-hitch clamping, replication feed, thermal scope, randomness in burns. | maintainers | 80 | §4 of this plan; api.md §24; design.md resolutions |
| `docs/glossary.md` | 40-60 one-sentence entries with columns: term, meaning, standard name if different, EA or game alias. Sections: electricity (about 25), Manatee API (about 12), Vintage Story (about 6), Stationeers (about 6). Ends with a "clashes to avoid" box. | everyone | 180 | §3 of this plan |
| `docs/tablet.md` | The tablet and the dev schematic editor: three pure layers; headless testing (event scripts, display-list goldens); rendering backends; the 17-lesson arc and authoring rules (predict-observe-explain, arithmetic only, in-world payoff, truthfulness review); lesson file layout (lesson = scenario plus narrative); Falstad/circuitjs1 import contract (import only; the core never sees coordinates); tablet runtime (client-side, paused while closed, resumes from snapshot). | tablet and lesson authors | 180 | harness.md, curriculum.md, falstad-format.md, lessons/README-schema.md, ea-examples.md, vintage-story.md:265-283, tablet reader |
| `docs/backends/README.md` | What a backend must provide: the capability record, the fidelity levels it supports, the scorecard it must pass, threading and allocation rules. Plus about 15 implementer glossary entries: timestep, transient (EMT) vs quasi-static phasor vs dynamic phasor/SFA, convergence, singular system, stiffness, passivity, partitioning, series reduction, operating point. | backend authors | 60 | solver-strategy reader; numerics.md (pruned) |
| `docs/backends/transient-mna.md` | The current backend, stated honestly: sparse LU with factorization reuse, Backward Euler, fixed sub-stepping per network, Newton for diodes, gmin, conductance range, series reduction and probe reconstruction, rebuild-on-split, and known limits (Backward Euler phase error, spurious operating points on floating nonlinear networks). Allowed to use method vocabulary. | backend authors | 120 | solver.md (:84-135, :219-268), compaction.md mechanism, backend-competition.md numbers |
| `docs/backends/candidates.md` | Next-backend options with corrected premises: trapezoidal with damping or TR-BDF2 (the cheapest fix for the phase and PF accuracy rows), and a hybrid dynamic-phasor/SFA backend. Its weak assumption at a 5 Hz carrier with a 20 Hz step is spelled out. Net speedup 1.3-1.7×. When the scorecard would justify building it. | backend authors | 60 | waveform-report.md (corrected), solver-strategy reader |
| `docs/games/vintage-story.md` | VS game design: thesis, difficulty that is mental rather than physical, mistakes rather than randomness, automation off-ramps; progression arcs; devices; 12/48/240 V and 5 Hz natural frequency with pole-count progression; flicker accessibility choice; grounding (two-wire default, SWER opt-in, electrodes, insulation test); hazards, hum, chisel semantics, griefing; tablet and handbook fiction; "readouts teach" with the frustration-walk table. Then engine facts with file:line refs: microblocks, mechanical power contract (no inertia; speed is arbitrary units; `resistance` slot; nothing persisted), rooms and heat, sleep time speed-up, chunk unload, threading, wire rendering. Then how VS maps onto the API: voxel-cuboid to segment adapter, mechanical port, tooltips from diagnoses. | VS mod devs | 280 | design.md game sections (:317-523), vintage-story.md, adversarial-playtest table, vintagestory reader, critic §1 and §3 |
| `docs/games/stationeers.md` | Re-Volt integration: verified threading and tick model; injection and accounting points (from the audit, :791-940); how CableNetworks map to electrical networks, including two CableNetworks becoming one network through a closed breaker; constant-power load rules (undervoltage dropout with hysteresis, staggered reconnect, lockout); voltage tiers; vanilla watt-semantic misnomers (Cable.MaxVoltage, fuse PowerBreak, vanilla Transformer = power gate); persistence; fallback scope; server-authoritative replication. Ends with "Pending Sukasa sign-off" (Q8). | Re-Volt devs, Sukasa | 140 | stationeers.md, revolt-audit.md:791-940, design.md R18-R19, revolt reader, critic §4.10 |

Total: about 2,090 lines across 14 files, down from about 17,700.

**Mandatory scenarios** for `accuracy-and-performance.md`, each one backend-neutral:
- **Pure-reactance power factor.** A pure inductor and a pure capacitor at 5 Hz must read PF ≤ 0.02. The current Backward Euler at 20 samples per cycle reads about 0.156.
- **Phantom surge.** A merge or reload must not produce a 0 V reading that makes a constant-power load draw unbounded current. The overnight build popped a fuse at 597 A this way.
- **Hot cable reload.** The I²t state of a heated cable must survive a snapshot and reload.
- **Floating nonlinear network.** A diode network with no ground must be reported Unsolvable or solved correctly, never silently wrong.
- **Constant-power load collapse.** An overloaded constant-power network must collapse legibly, without strobing and without creating energy.
- **Oscillating-load energy pump.** A rapidly toggled load must not draw more energy than the sources supplied.
- **Out-of-phase generator close.** Closing a switch between unsynchronized generators must produce a real surge.
- **Fuse at the rated I²t.** The fuse must open within ±1 step of the reference trip time.
- **Remove and re-add the same id in one batch.** Must behave as a replace.
- **10k-segment bulk load in random order.** Must load with no ordering obligations.
- **Edit made during a running step.** Must apply at the next step.

---

## 3. Terminology

Verdicts:
- **RENAME →** use the standard term.
- **KEEP** — no standard term exists and the concept is needed (reason given).
- **VANISHES** — exists only because the old API was shaped around the solver method.
- **BACKEND-ONLY** — standard term, but used only in `docs/backends/`.
- **GAME** — belongs in a games doc.

The EA column names the Electrical Age class or concept where one exists. Column 1 lists only the term and its aliases, not the locations where it appears.

### 3.1 Identity and structure

| Current term (aliases) | Verdict | Use instead / reason | EA equivalent |
|---|---|---|---|
| Netlist (the live object) | RENAME → **Circuit** | "Netlist" stays only for text formats (SPICE, circuitjs1). | `Simulator` / `RootSystem` |
| `Circuit` (old per-island usage in glossary.md) | VANISHES | "Circuit" now means the whole world object. Don't reuse it for a subset. | — |
| island (IslandId/Table/Handle/Status, same-island) | RENAME → **network** in the API and modder docs; island is BACKEND-ONLY | Electrically connected component. "Island" is real power-systems vocabulary, so it is fine in backend docs. Game docs always say *CableNetwork* (Stationeers) or *mechanical network* (VS) for the game's own groupings (Q7). | `SubSystem` |
| partition / PartitionKey / Self-/ClientPartitioned / pre-partitioned | VANISHES | Networks are computed, read-only. The need behind it is a query: "which CableNetworks share this electrical network". | — |
| ExternalKey | RENAME → **id** | One client-chosen 64-bit id per object. | — (EA used node UUIDs) |
| StateKey / StateUnit / StateUnitCount | VANISHES | Existed because one device expanded into many solver elements. State is keyed by the same id. | — |
| handle / ComponentRef / generation (Slot, Gen, Net) | VANISHES | Ids never go stale. | — |
| re-pin / Repin / TryResolve* / StaleHandleException / DrainChanges(lost) | VANISHES | Existed because a rebuild invalidated handles. | — |
| region / region building | RENAME → **node** (or **net**); BACKEND-ONLY | Not "supernode" (see §3.3). The API exposes junctions and terminals. | `ElectricalLoad` (misnomer: "load" means a power sink to an EE) |
| post | RENAME → **terminal** | | — |
| Device / DeviceHost / TerminalSpec / DeviceTickContext | RENAME → **part** (API); "device" only for game objects | DeviceHost and DeviceTickContext vanish. Custom behaviour goes in a **controller**. The final word depends on the API drafts (Q7). | `Descriptor` (part kind) + element |
| two-terminal / one-terminal primitive | KEEP as "two-terminal part" | Standard. | `Bipole` / `Monopole` |
| NodeRole.Reference / reference node / reference rail / ReferenceNode | RENAME → **ground** (tablet) / **common return** (Stationeers) | SPICE node 0. | — |
| NodeRole.Return | RENAME → **return conductor** | | — |
| PortNode | VANISHES | Artifact of series reduction. | — |
| extraction / intake / electrical graph / client intake contract | RENAME → **building the circuit** / cable graph input | | — |
| ConductorGraph / ConductorSpec / GeometrySegment | VANISHES | Cables are parts: segments with material, cross-section and length. | — |
| segment / junction (SegmentKey, JunctionKey) | KEEP | "Cable segment" is already a game term. Junction = connection point. | — |
| prism (sparky) | RENAME → **cuboid** | VS's own term (`VoxelCuboids`). GAME. | — |

### 3.2 Changing the circuit

| Current term | Verdict | Use instead / reason | EA |
|---|---|---|---|
| change-cost tier T0-T3 / `enum Tier` / CostTier / CostOfAdjust / fast path | VANISHES (BACKEND-ONLY: "RHS-only re-solve / numeric refactorization / symbolic re-analysis") | The LU-reuse ladder. A phasor backend has a different cost structure. Never use "tier" in library docs: it clashes with Stationeers voltage tiers. | — |
| Drive | VANISHES → plain setter (`source.Voltage = …`) | Also clashes with "motor drive". | — |
| Adjust | VANISHES → plain setter | | — |
| AdjustEpsilon / ε-gate / ε-no-op / AdjustNoOps | VANISHES | A change deadband whose only job was to skip refactorization. **Not** a convergence tolerance (glossary.md:28 is wrong). | — |
| Reconfigure | VANISHES → `breaker.Closed = …` | | — |
| relay-vs-breaker duality ("API citizenship") | VANISHES | Both are switches. A relay is a switch driven by a coil; a breaker is a switch with a trip. | — |
| Edit / StructuralEdit / BulkBuild / BeginBulkUpdate / coalescing | VANISHES → add/remove at any time between steps; library batches | Bulk intake out of order, with dangling references tolerated. | — |
| Meta / tier-0 facade | VANISHES | Labels are plain properties. | — |
| shape / shape-time / cold path | RENAME → **setup** | | — |
| 0B / zero-bytes | RENAME → **allocation-free** | Kept as a perf gate, not API grammar. | — |
| SteadyStateGuard / EnterSteadyState / AllocationSentinel | VANISHES (test tooling: "allocation check") | Clashes with EE "steady state". | — |
| TopologyJournal / journal / EditReceipt / WindowLapped / cursor | VANISHES | If multiplayer needs a feed, it is a **changed-readings feed** (open). | — |
| merge / split / rebuild-on-split / survivor/absorbed | BACKEND-ONLY | | `breakSystems` (RootSystem.java:338) |
| removes-before-adds (decision #28) | VANISHES as a rule | Order inside a batch doesn't matter; remove + re-add of the same id = replace. | — |

### 3.3 Fixes to the old nomenclature table (glossary.md:20-112) and to reader notes

1. **:28 AdjustEpsilon → "convergence tolerance" is wrong.** It is a change deadband for skipping refactorization. VANISHES.
2. **:31 "averaging window / merge window" lumps two unrelated things.** The averaging window is VS counter-torque smoothing. Keep it as "mean counter-torque over the interval since the last read". The merge window is the stale-read gap after a topology merge. VANISHES.
3. **:65 island → "connected component"** treats island as a mere graph term. Island is established power-systems vocabulary. Keep it for backends; use "network" for modders.
4. **:88 region → "node / supernode" is wrong.** Supernode is a different concept in nodal analysis. Correct: node, or net for EDA readers.
5. **"decoupling transformer → isolation/boundary transformer" is wrong.** An isolation transformer is a real device with a different meaning (galvanic safety isolation). The project term was a solver partition. VANISHES; there is one transformer part.
6. **"Netlist (as document) → retained-mode scene/model"** is not an EE term. Use Circuit.
7. **"Circuit (per-island Circuit)"** collides with the new top-level Circuit. Delete the row.
8. **Reader error (terminology.md):** EA `interSystemOverSampling` is **not** sub-stepping. RootSystem.java:228-232 loops only the pre-step processes (InterSystem boundary updates) that many times; `stepCalc` runs once per step. EA has no equivalent of AC sub-stepping. electricalage.md's "boundary relaxation iteration count" is correct.
9. **Glossary size:** the category files hold 932 `###` entries, while the commit message says "1,070 terms". Cite neither as exact.

### 3.4 Breakers, transformers, converters

| Current term | Verdict | Use instead / reason | EA |
|---|---|---|---|
| coupler / coupling device / CouplerSpec.Family | VANISHES → **breaker**, **transformer**, **power converter** as ordinary parts | "Coupling" also clashes with the coupling coefficient k. | `InterSystem` / `InterSystemAbstraction` |
| galvanic bridge / galvanic coupler | RENAME → **closed breaker** (galvanic connection) | Keep "galvanic" only where isolation matters. | — |
| decoupling transformer vs idealized transformer | VANISHES → **transformer** (turns ratio, rating, optional leakage and magnetizing); **ideal transformer** as a primitive for lessons | Solver-partition strategy, not physics. | `Transformer` (mna component) |
| boundary coupler / power-transfer boundary / decoupling boundary / island boundary | VANISHES (BACKEND-ONLY: "partition interface") | | `InterSystem` |
| amplitude+phase exchange / ExchangeView / RelaxationAlpha / α ≤ 0.7 clamp | VANISHES (BACKEND-ONLY: "under-relaxation factor") | | `Delay` source + `interSystemOverSampling` |
| EnergyLedger / energy-debt droop / choke latch / gain-capable loop / launders reactive power | VANISHES | Patches for the partition scheme. Energy conservation becomes a tested invariant. | — |
| scheduling unit / lockstep | VANISHES → the injectable **scheduler** runs networks as jobs | | — |
| ConverterTwoPort / behavioral two-port | RENAME → **power converter** (averaged model) / **rectifier** / **charger** | | — |

### 3.5 AC, time and measurement

| Current term | Verdict | Use instead / reason | EA |
|---|---|---|---|
| subcycled AC / subcycling / substep / SubstepPlan / acSamplesPerCycle / hysteresis band | VANISHES from the API; BACKEND-ONLY "sub-stepping" | The client asks for a **fidelity** level and never a substep count. | none (see §3.3, item 8) |
| SolverProfile {Dc, Transient, Mixed} / Regime | VANISHES → **fidelity** (Game / Lesson / Reference) + backend choice (Auto) | "Dc" was actually a 0.5 s Backward Euler transient, which is misleading. | — |
| waveform-first AC | VANISHES | Replaced by an observable requirement (§4, L3). | — |
| observer-gated fidelity | RENAME → **detail where observed** (level of detail) | Defined term: more waveform detail where a scope is attached, never different outcomes. | — |
| (new) capability record | KEEP, define | What a backend reports it supports: fidelity levels, scope synthesis, sub-step event timing, nonlinear parts. | — |
| source driver / SineDrive | RENAME → **AC source** / **sine source** | SPICE `SIN`. | — |
| alternation | RENAME → **AC** / **cycle** | In textbooks, "alternation" means a half-cycle. | — |
| TickClock / electrical tick | RENAME → **step** (`Step(dt)`, dt in seconds) | The game tick is the game's term. | — |
| the EA trick | RENAME → **time scaling** (component values slowed to visible time constants); GAME/pedagogy | Justified by teaching, not by integrator accuracy. | EA examples README |
| swing-equation-lite / swing-lite coupling | RENAME → **generator synchronization** (API); "simplified swing equation" BACKEND-ONLY | VS has no physical inertia, so the library only tracks electrical angle from the given speed. | — |
| averaging window (counter-torque) / back-coupling / bounded lag | KEEP the concept as **mean counter-torque since last read** | Standard loose coupling between the electrical and mechanical simulations. | — |
| 2f-aliasing guard | test name only | | — |
| WaveformTap / WaveformRing / two-probe contract | RENAME → **oscilloscope trace** / **scope channels (≥2)** | The backend samples or synthesizes. | — |
| probe / ProbeId | KEEP | Standard. | — |
| probe interpolation / re-aim / SetProbeInterpolation | VANISHES | A probe anywhere on a cable is just a reading. | — |
| meter / RMS / peak / average over window | KEEP | Readings over fixed per-meter windows. | — |
| "envelope" (AC) | KEEP only as **AC envelope** (amplitude/RMS, frequency, phase, P/Q/PF) | Don't also use it for ratings (see limit envelope). | — |
| RawVector | VANISHES | The solver's solution vector. | — |
| Solution / Previous | VANISHES → **readings** (published atomically per step) | | — |
| readback phase / three-phase contract / phase discipline | RENAME → **tick stages** | Clashes with electrical phase. Re-Volt's Initialise → CalculateState → ApplyState are GAME. | — |
| TickStats {RhsSolves, Refactorizations, Substeps, NewtonIterations, IslandRebuilds, MergesApplied, AdjustNoOps, DeferredStructuralOps} | RENAME → **step statistics** (time, allocations) + backend-specific diagnostics bag | | `Profiler` |
| node potential / flow (domain-neutral names) | VANISHES → voltage / current | YAGNI thermal-RC reuse. | — |

### 3.6 Ratings, protection, failure, loads

| Current term | Verdict | Use instead / reason | EA |
|---|---|---|---|
| limit / LimitSpec / LimitKind | RENAME → **rating** (current, voltage, power, I²t) | | `*Watchdog` (ResistorCurrentWatchdog, VoltageWatchdog, ResistorPowerWatchdog) |
| LimitEvent | RENAME → **protection event** (FuseBlown, BreakerTripped, CableOverheated, OverVoltage) | The library opens fuses and breakers. The game owns consequences. | `Watchdog` → destroys the block itself (EA conflated trip and consequence) |
| melting integral / i²t accumulator | KEEP **I²t** | Standard (Joule integral). | Watchdog timeout with grace |
| limit envelope / thermal envelope / Pareto set / I2tPair / PairIndex | VANISHES | Artifact of series reduction. Clashes with the AC envelope. | — |
| limit attribution / Attribute (weakest voxel) | VANISHES as a mechanism; the need stays | Events name the original segment id. | — |
| derating / ambient adjustment | KEEP **ambient derating** | Standard. | ThermalLoad ambient |
| Faulted / fault / FaultKind / de-energized | RENAME → **Unsolvable** + **diagnosis** code + culprit ids | Network status is Live / Stale / Unsolvable. Last good readings are kept. "Fault" in EE means a short circuit, which the game also simulates. | — |
| IslandStatus Building / Dirty / Ready / Empty / IsLive / merge window / last-good read | RENAME → **status** Live / Stale / Unsolvable + reading **freshness** | "Building" also clashes with game usage. | — |
| legible failure / legibility rails | RENAME → **clear diagnostics** | Plain words. | — |
| energy audit / EnergyAudit / conservation audit | RENAME → **energy-balance check** | Clashes with a building energy audit. | — |
| AdaptedLoad / adaptor / legacy-device adaptor / device wants | RENAME → **constant-power load** | The "P" in the standard ZIP load model. | `PowerSource` (constant-power source) |
| AdaptedSource | RENAME → **power-limited source** | | `PowerSource` |
| across-tick clamp / G=P/V_prev² linearization | VANISHES | The load is solved within the step. | — |
| brownout clamp / LiveFloorVolts / live-floor shed / brownout dropout | RENAME → **undervoltage dropout** (with hysteresis) | Standard: undervoltage lockout / load shedding. | `NodeElectricalGateInputHysteresisProcess` (hysteresis precedent) |
| staggered rejoin | RENAME → **staggered reconnect** | | — |
| lockout (after repeated brownouts) | KEEP **lockout** | Standard recloser lockout. | — |
| electrical duty / duty | RENAME → **load factor** | Clashes with PWM duty cycle. | — |
| heat registry | GAME | Mod-side heat map. | `ThermalLoad` |

### 3.7 Grounding and conductors

| Current term | Verdict | Use instead / reason | EA |
|---|---|---|---|
| WiringPolicy {ReferenceBound, TwoWireLeak, ExplicitOnly} | RENAME → **grounding convention**: *common return* (Stationeers), *two-wire with earth* (VS), *explicit ground* (tablet) | One line in EE terms with no stamp-valued parameters. The drafters choose the exact enum names. | — |
| implicit high-resistance leak (~1 MΩ) / gmin / conductance-range policy | BACKEND-ONLY | "Leak" also clashes with transformer leakage. | `highImpedance` / `pullDown` |
| two-wire idiom | RENAME → **two-wire circuit** (supply and return) | | — |
| SWER / earth electrode / ground rod | KEEP | Standard. | — |
| compaction / reduction layer / series-chain collapse | BACKEND-ONLY → **series reduction** | | `Line` (component/Line.java, `IAbstractor`) |
| drift / DriftReport / resync backstop / Fingerprint | RENAME → **reconciliation test** (incremental vs from-scratch); test-only | | — |
| semantically invisible | RENAME → **exactly equivalent** | | — |
| verified-floating ritual | GAME → **insulation test** | | — |
| electrode glassification | GAME | | — |
| dual occupancy | KEEP (VS-specific) | Cable voxels sharing a chiseled block. No standard term. | — |
| voxel cable | KEEP (GAME) | | EA cable blocks |

### 3.8 State, extension, tests, process

| Current term | Verdict | Use instead / reason | EA |
|---|---|---|---|
| Snapshot / Restore / additive restore / RestoreResult / OrphansInBlob / cold-start | RENAME → **state snapshot**, **restore**, **cold-start report** | | NBT save |
| Memento / SaveCanonical / SaveNormalized | VANISHES from docs (internal) | | — |
| IProcess-style per-step hooks; "no callbacks into clients, ever" | RENAME → **controller** (runs between steps at a declared rate) | Reverses the old "no callbacks" rule. | `IProcess` (slow/fast process) |
| part kind registry / material registry | KEEP (new) | Part kinds registered by string id for data-driven blocks. | `Descriptor` |
| oracle / ngspice oracle | RENAME → **reference simulator** (ngspice); "test oracle" OK in the testing doc | | — |
| MatchBackwardEuler | VANISHES from the shared contract | It compared against our method, not against physics. | — |
| referee (naive-dense) | RENAME → **reference backend** | | — |
| lesson corpus / goldens | RENAME → **lessons** + **golden tests** | | EA `docs/examples` |
| EA dialect / Falstad format | RENAME → **circuitjs1 text format** (import only) | The canonical format is our own scenario format. | — |
| predict-then-observe | RENAME → **predict-observe-explain (POE)** | Standard pedagogy term. | — |
| 2D harness / Desktop Shell / Tablet Engine / Manatee.Schematic | RENAME → **schematic editor** (dev app); **tablet** (in-game item) | | — |
| canon / canon-pending / binding / settled / decision log | RENAME → **decision** (decisions.md) | | — |
| R1-R20, OQ1-OQ4 | RENAME → L-numbers for library requirements (§4); game requirements are not numbered | | — |
| seam / integration seam | RENAME → **integration point** | | — |
| thread purity | RENAME → **no engine dependencies** | | — |
| readouts teach | KEEP (game-design slogan) | | — |
| tablet / handbook | KEEP (game items) | | — |
| 12 V cottage problem | KEEP as a scenario name | | — |

### 3.9 Clashes with established meanings: rename, never keep

fault / Faulted · de-energized · steady state (SteadyStateGuard) · phase (tick stages, re-pin phase) · envelope (ratings vs AC envelope) · tier (cost tier vs Stationeers voltage tier) · decoupling / isolation transformer · energy audit · alternation · duty · leak (bleeder vs leakage inductance) · Drive (motor drive) · coupling (vs coupling coefficient k) · region (VS map region) · Building (island state) · load (EA `ElectricalLoad` means a node).

Stationeers vanilla misnomers to flag in `games/stationeers.md`:
- `Cable.MaxVoltage` is a power rating in watts.
- `CableFuse.PowerBreak` is in watts.
- The CircuitBreaker "trip current" setting is in watts.
- The vanilla "Transformer" is a power gate, not a voltage transformer.
- PowerTick Potential / Required / Consumed mean supply / demand / delivered.

---

## 4. Requirements and "settled" decisions

### 4.1 design.md R1-R20

L-numbers are the new library requirements, listed in README and decisions.md.

| Old | Gist | Fate | Restatement (backend-neutral) |
|---|---|---|---|
| R1 | Real circuit simulation via MNA | RESTATE → L1 | **Real circuit physics.** Kirchhoff's current and voltage laws hold. Results match an analytic answer or a reference simulator at accurate settings, within the published tolerance for the requested fidelity. |
| R2 | One netlist, multiple analyses; Backward Euler; stamps per analysis | RESTATE → L2 | **One circuit for every situation.** The same circuit handles steady DC, slowly changing DC (charging, batteries) and AC. Parts are described by physical and nameplate parameters, never by how a solver represents them. |
| R3 | Waveform-first AC via subcycling; phasor never primary | RESTATE → L3; delete the phasor non-goal | **Waveforms are real where they are seen.** The oscilloscope shows true instantaneous waveforms, including switching transients. Lamps flicker at supply frequency. RMS, phase and power factor meet the accuracy table. Transients with time constants of 0.1 s or more are reproduced; faster spikes are reported as peaks. Attaching an instrument may add display detail but never changes gameplay outcomes. |
| R4 | Explicit change-cost tiers in the API | DIES | Replaced by a performance budget in `accuracy-and-performance.md`. Steady-state steps meet the budget; edits may cost more; cost is reported in time and allocations only. |
| R5 | Islands as a core feature; coupling devices span game networks | RESTATE → L5 (narrowed) | **Independent networks.** Electrically separate networks are solved independently, may run in parallel, and fail independently. The library computes the grouping and exposes it read-only, with a query for which game objects share a network. No client-declared partitions; the Stationeers boundary is Q9. |
| R6 | Snapshot of capacitor V, inductor I | RESTATE → L6 | **Optional state snapshot keyed by id.** Physical state (capacitor voltage, inductor current, battery charge, I²t heat, generator angle) round-trips by part id. Restore tolerates parts that changed, never resets parts missing from the snapshot, and reports what started cold. The game owns topology and rebuilds it. |
| R7 | Limit events with attribution | RESTATE → L7 | **Ratings and protection.** Parts and cable segments carry ratings (current, voltage, power, I²t) with ambient derating. Ratings are judged on the physically meaningful quantity (peak, RMS or I²t), regardless of internal sampling. Fuses and breakers open inside the simulation. Typed events name the client id (down to the segment) and the observed values. The game owns consequences such as fire, drops and voxel removal, and feeds them back as edits. |
| R8 | Thread purity, no allocation in steady state | RESTATE → L8 | **Embeddable.** Pure C# (netstandard2.1 and net8.0), no engine API, no static state (many circuits per process), injectable scheduler with serial default. Readings publish atomically per step, so other threads can read during the next step; edits made during a step apply at the next. Steady-state steps allocate nothing: a hard gate for shipped backends, reported for experimental ones. |
| R9 | Legible failure; no NaN | RESTATE → L9 | **Clear failure, contained.** A network that cannot be solved is marked Unsolvable with a diagnosis code and culprit ids. Its last good readings stay readable and flagged. No NaN reaches the game. Other networks keep running. |
| R10 | Series-chain collapse before solving | RESTATE → L10 | **Long cables are cheap and readable.** The budget holds for about 10k cable segments. Readings and events are per original segment. How it is achieved is backend-private. |
| R11 | Incremental topology with resync backstop | RESTATE → L11 | **Edits anywhere, anytime, exactly.** Add and remove at any time between steps, in bulk, in any order, with dangling references held until resolved. The result always equals a from-scratch build of the same circuit (a reconciliation test). |
| R12 | Voxel cables with physical semantics | → `games/vintage-story.md` | Library side is covered by the cable-segment part (material, cross-section, length, ratings). |
| R13 | Frictionless placement and maintenance | → games/vintage-story.md | — |
| R14 | Mechanical coupling | SPLIT | Library (`api.md`, L14): **shaft port.** The game supplies shaft speed; the library returns mean counter-torque over the interval since the last read and integrates electrical angle from the given speed. Game: alternators and motors, frequency = speed × pole pairs, VS `resistance` slot. |
| R15 | Instruments; readouts teach; V/I at trip | SPLIT | Library: at least two scope channels, meters over windows, diagnoses with structured facts, observed values in trip events. Game: tooltip wording, items, plotter fiction. |
| R16 | The tablet; handbook floor | → `tablet.md` (runtime) + games/vintage-story.md (fiction) | — |
| R17 | Vanilla microblock integration | → games/vintage-story.md | — |
| R18 | Adaptor for the vanilla 4-call power API, energy ledger | SPLIT | Library (L18): **constant-power load part** with undervoltage dropout, hysteresis, staggered reconnect and lockout, solved within the step so it can never create energy. The energy ledger dies. Stationeers: which devices use it, thresholds, the 4-call mapping. |
| R19 | Real voltage tiers | → games/stationeers.md | Goes into the Sukasa bundle (vanilla Transformer becomes a voltage ratio). |
| R20 | One corpus, three consumers | RESTATE → L20 | **One scenario format, three uses.** A lesson is a scenario (named parts, stimulus timeline, observations with tolerance) plus narrative. The same file is tablet content, a documentation example and a CI test against the reference and its own stated values. No timestep or analysis-type field. |

### 4.2 Other design.md content

| Item | Fate |
|---|---|
| Non-goal "frequency-domain-only AC" | DIES (it is what blocks implementation-agnostic backends). |
| Non-goals: semiconductor-level power electronics in VS, lightning, random failure, in-world CAD | → games/vintage-story.md. Random failure conflicts with Re-Volt's seeded random burn (Sukasa bundle). |
| Non-goal "reusing sparky's code" | DIES (history). |
| System overview ("netlist · stamps · LU") | Redrawn backend-neutral in README: game adapters → Circuit API → backend(s). |
| Simulation model: DC/transient/AC bullets, Backward Euler, substeps | → `backends/transient-mna.md`. |
| Simulation model: EA time scaling "because Backward Euler is accurate at 50 ms" | Kept as a teaching choice in games/tablet docs, without the numerics justification. |
| Simulation model: idealized vs decoupling transformers; "gameplay steers long-distance transfer to decoupling types" | DIES, both the class split and the game rule motivated by solver performance. |
| Simulation model: nonlinearity budget | → backend docs. |
| Simulation model: mechanical co-simulation | → api.md shaft port (L14) + VS engine facts. |
| Simulation model: generator paralleling | Library: phase difference readable; out-of-phase closing produces a real surge. Game: synchronization as a skill. |
| Energy accounting rule | RESTATE → L21: **Energy is conserved.** Every modelled loss shows up as heat on the part where it physically occurs. Checked per backend by the energy-balance invariant. Ledgers die. |
| Performance targets | → accuracy-and-performance.md budgets: VS ≤5 ms on-thread / ≤20 ms off-thread per 50 ms step for a few hundred parts; Stationeers 10k cables within the 500 ms worker tick (fraction to be set). "Transformers are isolation points" dies. |
| Testing: "validated by current EA development" (:557-559) | DIES (false; no spice in the EA tree). |
| Licensing | → README. |
| Delivery order | → README + decisions.md (confirm Sukasa is engaged, Q8). |
| Open Questions: "none blocking" | DIES. Live unknowns become `open` rows in decisions.md. |
| Thermal-RC domain-neutral naming | DIES (YAGNI). |

### 4.3 Dated "settled" decisions and api.md §24

**Survive** as decisions.md rows, restated and keeping their original date:
- Project name, MIT licence, CSparse dev/test-only (2026-07-02 and 2026-07-06).
- Grounding convention as a construction-time option in EE terms (api §24 #9, synthesis.md:205-216). The tablet stays floating by default.
- Restore is additive and tolerant, with a cold-start report (#19).
- The transformer is a first-class part, and the ideal transformer a primitive (#23).
- One global step per Re-Volt `ElectricityTick` (#24). Becomes "one `Step(dt)` for the whole circuit" (library) plus the Re-Volt prefix hook (Sukasa).
- CI allocation measurement is binding; runtime tripwires are best-effort (#10). Kept as a perf gate.
- Determinism scope (#26), restated: deterministic per backend + version + runtime; lessons compare within tolerance, never bit-exact; the multiplayer server is authoritative.
- Remove + re-add of the same id in one batch = replace (#28, restated without an ordering rule).
- At least two scope channels (2026-07-05).
- Ratings per limit type, adjusted for ambient (2026-07-05), restated per L7.
- Energy accounting rule (2026-07-05), restated as L21.

Game rows that keep their dates: 12/48/240 V and 5 Hz natural, 50 Hz via pole count; two-wire default with SWER opt-in; battery fiction; relay-logic elevator; chisel semantics; readouts teach; handbook floor and tablet ceiling; frictionless maintenance; flicker accessibility choice; Stationeers undervoltage dropout with hysteresis, stagger and lockout; voltage tiers with Sukasa (2026-07-02). The 50 ms electrical tick is a VS game fact; the library accepts any dt.

**Reversed.** Each gets a row "reversed 2026-10-06 (pending user)":
- #27 *Faulted reads de-energized* → last good values retained and flagged Unsolvable. A silent 0 V makes a constant-power load see a dead short.
- *In-house sparse LU as the sole production backend* (2026-07-06) → backends are pluggable; the current one becomes backend #1, scored like any other.
- *Phasor is never the primary mode* → method is not specified.
- *Idealized vs decoupling transformer split* (2026-07-05) → one transformer part.
- *Relay-vs-breaker duality* (2026-07-05) → both are switches.
- *"No callbacks into clients, ever"* (api.md:58-60) → controllers run between steps at a declared rate.

**Become backend-internal**, with no decisions.md row (they belong in `backends/transient-mna.md` if anywhere): switch on/off resistances and the conductance-range policy; AC substep count with hysteresis; island rebuilds coalesced once per step; the across-tick current clamp on adapted generators.

**Die outright** (mechanics of the old API): #1 handle-invalidation timing, #2 two keys, #3 Reconfigure-Open rebuild, #4 merge preserves handles, #5 guard-violation deferral, #6 12-byte handles, #7 constrained callvirt, #8 span fields, #11 verbs on Netlist, #12 pooled StructuralEdit, #13 Mixed netlist, #14 re-pin obligation, #15 SnapshotSize stability, #16 reduction uses only public API, #17 bulk staging growth, #18 couplers and probes document-stable (moot, since all ids are stable), #20 DrainChanges overflow, #21 ε-gate, #22 TickStats per scheduling unit, #25 journal cursors. Also from design.md: boundary amplitude+phase exchange with relaxation, the adaptor energy ledger, "Mixed" profiles, and the tablet's fixed 10 ms step.

---

## 5. Contradictions and stale facts to fix during the rewrite

1. **Status lines disagree.** design.md:3-4 says "layer docs in progress" and :27 says "(to be written)", while CLAUDE.md says "implemented". Fix: README carries the only status line.
2. **"All major decisions live here [design.md]" is false.** api.md §24 holds 28 rulings and the tutorial adds more. Fix: `decisions.md` is the only register.
3. **Stale CLAUDE.md claims:** "api.md is the binding as-built surface" and "Next: 2D schematic harness". Fix: rewrite CLAUDE.md.
4. **Stationeers integration facts are superseded.** design.md:525-541 says "CableNetwork Add/Remove hooks" and "Harmony-injected MNACableNetwork", against api.md §23.9 and the audit: the dirty path tracks device lists, `RebuildNetwork` is a whole-network flood, and injection uses constructor postfixes (revolt-audit:860-866). stationeers.md has a do-not-trust banner (:7-17). Fix: games/stationeers.md is rebuilt from the audit's verified facts.
5. **Phantom API in solver.md.** The sketch at :47-71 never matched code, and `LimitConfig` (:228) is a phantom type. Fix: delete.
6. **Zero-allocation scope disagrees.** solver.md:299 says "tiers 1-2"; integration-tutorial:459 says "tiers 0-2". Fix: one gate, "steady-state steps allocate nothing".
7. **Boundary lag claim disagrees.** solver.md:186-188 says "one substep of lag" while the overnight report:78-80 measured 15-25 J of over-delivery. Fix: moot, the mechanism is deleted.
8. **Lesson file names disagree.** curriculum.md:17-21 and falstad-format.md:120 say `lesson.txt`/`README.md`, but lessons/README-schema.md and the shipped lessons use `circuit.txt`/`lesson.md`. Fix: tablet.md states the real layout.
9. **Falstad importer location.** harness.md:26-28 puts it in the tablet document layer, but the code is in `core/Manatee.Core/Falstad/`. Fix: decided location is the schematic/tablet layer (Q3 confirms). Code move is a follow-up.
10. **`Manatee.Schematic` is missing.** design.md:572 and harness.md:22 reference it, and Manatee.Harness.csproj:22 references it, but it has no source and is not in Manatee.sln. Fix: tablet.md says "planned", and the csproj reference gets fixed.
11. **testing-strategy.md is stale.** "Tests written from design.md/solver.md" (:159-160) and a duplicate TOC "3." (:142-143). Fix: rewritten.
12. **"Open Questions: None blocking"** (design.md:598) contradicts api.md §23 (9 open items) and the overnight report's unreviewed judgment list. Fix: open rows in decisions.md.
13. **Stale experiment notes.** backend-competition:59-60 says "amendment not yet applied" (it has been), and vs-mechanics:99, :119 "deserves a design.md note" is resolved. Fix: delete the files.
14. **waveform-report premises are wrong** (1 kHz, 30 Hz tick, 50/60 Hz, Apache Commons) against our 50 ms step and 5 Hz natural frequency. The speedup claims conflict: 30× (report), about 5× (counting solves), 1.3-1.7× net CPU. Fix: candidates.md uses the net figure.
15. **Two tick rates.** The report's 30 TPS server tick and design.md:610-611's "33 ms spurious" are both true in different senses (server tick vs the mod's own tick listener). Fix: one sentence in games/vintage-story.md.
16. **EA misread.** The report and terminology.md treat `interSystemOverSampling` as oversampling. RootSystem.java:228-232 shows it only repeats pre-step processes; `stepCalc` runs once; the default is 50 (Eln.java:352). Fix: corrected in §3.3, item 8.
17. **Tablet timestep conflict.** vintage-story.md:273-275 says the tablet uses a fixed 10 ms step "on the tier-1 fast path", while api.md's Tablet profile says 50 ms. Fix: delete both. The tablet passes its frame dt like any client.
18. **Backend accuracy hole.** Backward Euler at 20 samples per cycle gives about 9° phase error, so a pure inductor or capacitor reads PF ≈ 0.156 (81°). At 40 samples per cycle it reads 85.5° and PF 0.078. Curriculum lesson 16 (power factor) is therefore wrong at the default. No current test can catch it, because the reference runs are forced to match Backward Euler (`method=gear maxord=1`, Netlist.SpiceEmit.cs:211-232; `MatchBackwardEuler = true` and last point only, OracleHarness.cs:56-63). Fix: scorecard row plus a mandatory scenario; the reference simulator runs at accurate settings.
19. **Floating nonlinear networks can converge to wrong answers** (api.md §23.8a). This is a correctness hole for tablet free play, scattered across design.md and solver.md. Fix: a mandatory scenario plus a known-limit note in transient-mna.md.
20. **Lesson 01 voltage.** It uses 12 V while the EA seed is 3 V. That is deliberate, so only the "near-verbatim" claim in ea-examples is stale. Fix: no action beyond deleting ea-examples.
21. **Faulted vs fault.** "Faulted" (solver failure) sits next to EE fault events (short circuits). Fix: renamed Unsolvable.
22. **Overnight report items the user never reviewed.** Droop choke latch, α ≤ 0.7 proven only on a grid, audit-tuned droop constants, I²t positional reset reloading a hot cable cold, spurious Newton points, unimplemented parallel Step, O(graph) AddSegment. Fix: the mechanism items are moot; the behaviours become the mandatory scenarios in §2.
23. **VS mechanical contract misread** (prior-art). `GetTorque(tick, speed, out resistance)` does not return dTorque/dSpeed. An alternator returns torque 0 and puts its counter-torque in `resistance` (BEBehaviorMPBase.cs:403-407; MechanicalNetwork.cs:222-234). Fix: games/vintage-story.md states this with file:line refs.
24. **VS mechanical power has no physical inertia.** Speed follows a heuristic (MechanicalNetwork.cs:200-260), its units are arbitrary, and nothing is persisted: `Event_SaveGameLoaded` creates a new MechPowerData (MechanicalPowerMod.cs:223-226). Fix: no swing equation with game inertia. The library integrates only electrical angle from the speed it is given.
25. **Stationeers id truncation.** `Thing.ReferenceId` is `long` (Thing.cs:702), but `NetworkExport.ThingInfo.RefId` is `int` (NetworkExport.cs:16), and NetworkExport is an empty stub (NetworkExportCommand.cs:13-16). Fix: Sukasa bundle.
26. **Re-Volt's burn uses seeded randomness.** `System.Random` is seeded by network ReferenceId and picks a random cable among the lowest-rated ones (RevoltTick.cs:39, 89, 179-182). A real solver burns the hottest segment, which is a gameplay change. Fix: Sukasa bundle; also an `open` row (randomness in burns).
27. **Chunk unload is essentially undocumented.** The only mention is design.md:117. The "freeze the whole network" proposal is new, not canon. Fix: an `open` row in decisions.md plus an engine-facts paragraph in games/vintage-story.md.
28. **Glossary size claims disagree.** 932 entries by count vs "1,070 terms" in the commit message. Fix: don't cite either; the file is replaced.
29. **Wrong source paths.** vintage-story.md:9-10 says engine sources are "at repo root" when they are in `third_party/`. Fix: corrected in the move.
30. **AGENTS.md is byte-identical to CLAUDE.md.** Fix: symlink (Q6).
31. **CSparse.** Still in Directory.Packages.props (4.4.0) as dev-only LGPL, which is consistent with the licensing text. Fix: README keeps the one-line licence note; no change.

---

## 6. Open questions for the user (doc set only)

**Merged into [questions.md](questions.md); answer there.** Map: Q1→D1, Q2→D2, Q3→A7, Q4→D3, Q5→D4, Q6→trivia, Q7→A9, Q8→T9, Q9→A8, Q10→D5, Q11→D6, Q12→trivia, Q13→T10. The list below is kept so this plan's Q-references still resolve.

API unknowns (game time vs wall time, step-hitch clamping, replication feed, thermal scope, randomness in burns) belong to the drafters and will appear as `open` rows in decisions.md.

1. **Delete or archive?** Should process docs (experiments/, api-competition/, reviews/, the overnight report, old api.md, solver.md, compaction.md, integration-tutorial.md) be deleted outright and left to jj history, or moved to `docs/archive/` with a one-paragraph index? *Leaning: delete.* jj history is the archive and decisions.md carries what is still live. An archive folder invites people to treat old text as current.
2. **Glossary scope.** One modder-facing `glossary.md` with 40-60 entries, plus about 15 implementer terms in `backends/README.md`, and delete `programming.md` and the rest of `glossary/`? *Leaning: yes.* Any number above 60 is a sign the API still leaks.
3. **Where is the scenario format specified?** It is used by both lessons (tablet) and the scorecard. *Leaning: specify it once in `accuracy-and-performance.md`, since it is the scorecard's input. `tablet.md` documents only the lesson-specific extras (narrative, predict prompts) and links to it.* The Falstad importer lives in the tablet/schematic layer, not core.
4. **Integration guide: separate `guide.md` or walkthroughs inside `api.md`?** *Leaning: separate `guide.md` (about 150 lines),* so `api.md` stays a reference under 400 lines. Fold them together if the API ends up small enough that the walkthroughs are most of it.
5. **Game design in this repo?** Should `docs/games/vintage-story.md` and `docs/games/stationeers.md` live here, separate from library docs, or should Stationeers game rules move to Re-Volt's repo? *Leaning: both here for now.* The Stationeers doc is marked "integration notes; Re-Volt owns gameplay" and can move once Sukasa takes it.
6. **AGENTS.md: symlink or 3-line pointer?** *Leaning: symlink to CLAUDE.md,* so the two can't drift again. Use a pointer only if some tool can't follow symlinks.
7. **User-facing words.** "Network" for an electrically connected component, given that Stationeers already says CableNetwork and VS says mechanical network? "Part" vs "device" for things you add to a Circuit? *Leaning: "network" in the API, with game docs always qualifying the game's own ("CableNetwork", "mechanical network"); "part" in the API, "device" only for game objects.* This must match whatever the API drafters choose; the glossary follows the API.
8. **The Sukasa bundle and Re-Volt's place.** Where does the bundle live, and should we confirm he is still engaged before writing 140 lines of `games/stationeers.md` and keeping Re-Volt second in the delivery order? The bundle covers: closed-breaker back-feed, transformer as voltage ratio, one global step prefix, constant-power loads solved within the step, library-owned fuse and breaker opening, physically chosen burns, `int` RefId, NetworkExport stub. The 38-item audit grid goes with it. *Leaning: a "Pending Sukasa sign-off" section at the end of `games/stationeers.md`, plus the audit grid sent to him directly. Confirm engagement first. If he is gone, keep `games/stationeers.md` to about 60 lines of verified engine facts.*
9. **Stationeers network boundary in the docs.** When a closed breaker merges two CableNetworks into one electrical network, should the docs describe fallback and health per electrical network, using a "which CableNetworks share this network" query, rather than client-declared partitions? *Leaning: yes, the computed grouping plus a query.* This removes the last reason for the partition API. Partly an API question, but it decides how R5 is restated.
10. **Runnable examples.** Should every walkthrough in `guide.md` have a runnable twin under `examples/` (replacing `RevoltWalkthrough`) that CI compiles? *Leaning: yes, once the dream API has an implementation.* Until then the walkthroughs are marked "illustrative".
11. **waveform-report.md fate.** It is your research, so it stays. Commit it unchanged as a dated input under `docs/research/` with a short header listing the corrected premises (5 Hz, 50 ms tick, EA's `interSystemOverSampling` is boundary relaxation, net speedup about 1.3-1.7× at 5 Hz). `backends/candidates.md` carries the corrected conclusions and links to it. *Leaning: yes.*
12. **decisions.md format.** One line per decision with date, status and a short reason, including reversed rows that point to what they replaced? Or only live decisions? *Leaning: include reversals for one cycle* so the user can see what this re-plan overturned, then prune them at the next doc pass.
13. **Order of work.** Proposed: (a) glossary + decisions.md, which fix vocabulary for everything else; (b) api.md (drafters); (c) concepts.md and accuracy-and-performance.md; (d) games/ and tablet.md; (e) backends/; (f) delete the old files in one commit. *Leaning: this order, landed as one jj change per step.*
