# Accuracy and performance

Last updated: 2026-10-06

**Status.** Target design, not yet implemented. This is the test and benchmark contract for every Manatee backend, and the only home of the accuracy budget table and the reading definitions. `api.md` 3.18 and 3.21 point here; where they differ, this file wins. It replaces `docs/testing-strategy.md`, the lesson schema in `lessons/README-schema.md` and the benchmark plan in `docs/experiments/2026-07-05-backend-competition.md`.

## 1. Purpose

A *backend* is the engine that computes a circuit's answers. Manatee may ship several, and new ones may be written later. This contract scores any backend for **accuracy** (how close its readings are to the truth) and **performance** (how much of the game's tick it uses), and it does so **through the public API only**. The harness is an ordinary client: it builds circuits with `Put`, calls `Step(dt)`, reads `Readings`, `Trace` and events, and never sees how a backend works inside.

Three consequences follow:

- **No test names a numerical method.** A scenario says what the circuit is, how it is driven and what to measure. It never states a solver step size, an analysis type or an internal counter. Backend counters (`BackendCounters`, api.md 3.20) may be printed but never asserted on.
- **Every backend answers the same questions.** Window readings (mean, RMS, peak), AC values, per-step energy and heat, traces sampled at any time in their span, and events with times (api.md 3.21).
- **One runner, three modes.** `verify` passes or fails CI against the budgets; `score` writes the scorecard (section 8); `bench` measures performance only.

## 2. Scenario format

A scenario is a small YAML file that describes one circuit, what happens to it over time, and what to measure. It is our own named-part format: every part has an id, so observations name parts, not screen positions.

### 2.1 Keys

| Key | Meaning |
| --- | --- |
| `name`, `class` | Unique name; the scenario class it belongs to (section 6). |
| `tick` | The game tick the scenario is stepped with, in seconds (0.05 for Vintage Story, 0.5 for Stationeers). |
| `alternate-ticks` | Other ticks it is also scored at, to show where a backend stops being valid. |
| `duration` | Simulated seconds to run. |
| `options` | A subset of `CircuitOptions`: `grounding`, `meter-window`, `ambient`, `seed`, `max-step`. Never a step size or method. |
| `parts` | Part records as in api.md 3.5 and 3.6: `id`, `kind`, and parameters and terminal fields under their record parameter names in camelCase (api.md 3.15). Optional `groups` and `area`. |
| `timeline` | Inputs and edits, each with an `at` time: `put`, `remove`, `closed`, `source`, `demand`, `available`, `shaft-speed`, `rearm`, `reset-state`, `area-loaded`, `save-load` (save, rebuild from the part list, load). |
| `observe` | What to measure and against which truth (2.2). |

**Edits happen at tick boundaries only.** Every `at` must be a whole multiple of `tick` and of each alternate tick, or the loader rejects the file. A switch on 5 Hz AC with a 50 ms tick can therefore close only at 0°, 90°, 180° or 270° of the cycle; to close "mid-cycle" at other angles, a scenario shifts the source's phase instead. In-step event times are a backend capability (`InStepEventTimes`), never a scenario input.

### 2.2 Observations

Each entry names *what*, *when*, *the truth source* and optionally *the teaching tolerance*.

| Form | What it reads | When |
| --- | --- | --- |
| `{ part, quantity }` | A field of `PartReading`, by its path: `Current.Rms`, `Voltage.Mean`, `Voltage.Peak`, `Ac.PowerFactor`, `Ac.CurrentAngle`, `HeatPower`, `StepHeat`, `Temperature`, `OverloadHeat`, `Condition`... Each path is a member of api.md's `Quantity` enum with the dots removed (`Current.Rms` is `Quantity.CurrentRms`). | `at`: read at the first step boundary at or after it (at a coarse alternate tick the actual time, and the wider window, are recorded in the scorecard). `at: steady` reads after `Settle` (api.md 3.8). Window quantities cover the meter window ending there; `Peak` follows its definition in section 4.1. |
| `{ node, quantity }` | A field of `NodeReading` | `at` or `at: steady` |
| `{ network-of, quantity }` | A field of `NetworkInfo`: `Status`, `Diagnosis`, `Available`, `Delivered`, `EnergyResidual` | `at`, or `from`/`to` for "at every step in this span" |
| `{ trace: { voltage \| current \| power }, bandwidth, sample-rate }` | A trace channel, compared as a waveform; `bandwidth` is the detail the scenario needs, in Hz; `sample-rate` is required, so every backend's trace lands on the same sample times | `from`/`to` |
| `{ torque: part }` | Mean shaft torque from a `TorqueMeter` | `from`/`to` |
| `{ event: { kind, part } }` | That event happens, at the truth's time; `none: true` means it must not happen | the whole run, or `from`/`to` |

- `truth:` is `analytic` (with `value:`), `ngspice`, or `reference` (section 3). Discrete expectations (`Status`, `Condition`, `none: true`) use `expect:` and are pass/fail.
- `teach:` is the tolerance a student would accept on a meter (relative; `teach-abs:` for absolute). It decides whether a *lesson* passes. A *backend* is never scored against `teach`; it is scored by its error divided by the accuracy budget (section 4).
- Traces named in `observe` are attached at time 0. The invariance check in section 5 reruns the scenario without them.

### 2.3 A complete example

```yaml
name: rl-switch-on-5hz
class: switch-mid-cycle
tick: 0.05
alternate-ticks: [0.1, 0.5]
duration: 3.0
options: { grounding: Explicit, meter-window: 0.2 }
parts:
  - { id: 1, kind: VoltageSource, plus: 10, minus: 9,
      waveform: { sine: { rms: 12, frequency: 5, phase: 0.0 } } }   # vary phase to sweep closing angle
  - { id: 2, kind: Switch, a: 10, b: 11, closed: false }
  - { id: 3, kind: Resistor, resistance: 2, a: 11, b: 12 }
  - { id: 4, kind: Inductor, inductance: 0.2, a: 12, b: 9 }
  - { id: 5, kind: Fuse, a: 9, b: 13, rating: { current: 5.0 } }  # in the return path
  - { id: 6, kind: Wire, a: 13, b: 0 }
  - { id: 7, kind: Ground, node: 0 }
timeline:
  - { at: 1.0, closed: { id: 2, value: true } }
observe:
  - { part: 4, quantity: Current.Peak, at: 1.2, truth: ngspice }
  - { trace: { current: 4 }, from: 1.0, to: 1.4, bandwidth: 25, sample-rate: 200, truth: ngspice }   # inrush
  - { part: 4, quantity: Ac.PowerFactor, at: 3.0, truth: analytic, value: 0.303, teach-abs: 0.06 }
  - { part: 4, quantity: Current.Rms, at: 3.0, truth: analytic, value: 1.82, teach: 0.05 }
  - { network-of: 1, quantity: EnergyResidual, from: 0, to: 3.0 }                 # invariant, no truth
  - { event: { kind: FuseBlown, part: 5 }, none: true }
```

The analytic values follow from the impedance: `X = 2π·5·0.2 = 6.28 Ω`, `|Z| = 6.59 Ω`, `I = 12 / 6.59 = 1.82 A`, power factor `2 / 6.59 = 0.303`.

### 2.4 Lessons and imports

**A lesson is a scenario plus narrative.** The lesson file adds the predict prompt, the explanation and the in-world payoff (see `tablet.md`); the physics, timeline and observations are an ordinary scenario, so every lesson is also a CI test. Lesson 04 (RC charging) converts like this: the pixel probe `[192, 128]` becomes part id 3 (the capacitor); `analysis: transient` is dropped; `stop: 4.5` becomes `duration: 4.5`; each expectation's `value` becomes an analytic `value:` with `truth: analytic`, and its `tol: 0.02` becomes `teach-abs: 0.02` (the old tolerances were absolute volts); and the step size from the Falstad `$` header is ignored.

**Falstad (circuitjs1) files import into scenarios** through the schematic layer, never the core. The importer assigns ids deterministically (in file order), so re-importing the same file gives the same scenario. Parts Falstad cannot express (generator, fuse, constant-power load, breaker) are added in the scenario format directly.

Any network or whole circuit can also be **exported as a scenario** (questions.md B7), so a player's "why did it explode" case becomes a reproducible test.

## 3. Truth

### 3.1 Truth policy

Every scored observation has a truth source, chosen in this order:

1. **Analytic.** A closed-form answer: dividers, step responses, sinusoidal steady state from impedances, I²t trip times at constant current. Exact and free.
2. **ngspice at accurate settings.** The reference simulator, run with its default integration, tight tolerances and a maximum time step far below the circuit's fastest time constant and the AC period. It is **checked for convergence**: the harness reruns with the maximum step halved and requires every observed value to move by less than a tenth of its budget. If it still moves, the scenario is marked unconverged and fails until fixed. ngspice is never told anything about the backend under test.
3. **A slow reference backend.** An in-repo backend written for clarity, not speed, used as truth for parts ngspice cannot express: shaft-coupled generators and motors, I²t fuses and cable heating, constant-power loads with dropout. It is itself validated against ngspice on every scenario ngspice can express, so a bug it shares with a production backend is caught there (questions.md T3).

The scorecard records which source scored each observation.

**Caching.** Truth runs are cached by a hash of (canonical scenario text, truth tool and version, its options). CI generates missing entries and commits only small summaries, never full waveforms. If ngspice is needed and missing, the run is a hard failure, never a skip.

### 3.2 Why the old oracle is retired

The previous suite configured ngspice to use the same integration method and step as the solver under test (Backward Euler at the solver's own step, comparing only the last point). That checked that the method was implemented as written, not that the answer was right: both sides made the same error and it cancelled out. At 20 samples per cycle that method makes an ideal inductor look 81° out of phase instead of 90°, so a pure inductive load read **power factor 0.156 instead of 0**, and lesson 16 (power factor) would have taught the wrong number. No test could see it. Under this contract the pure-reactance scenario (section 6) fails such a backend at the Lesson column, as it should. Method-matched runs may survive only as private regression tests of one backend.

## 4. Accuracy budgets

Against the truth in section 3. `Strict` is ten times tighter than `Lesson`. Each row is one field of api.md's `AccuracyClass`. Budgets are on observables against the truth; how a backend meets them is not specified.

| Observable | Lesson | Game (questions.md T1) |
| --- | --- | --- |
| DC steady state | 1e-6 relative | 1e-6 relative |
| Meter values (mean, RMS) and power | 1% | 5% |
| Peak (window; 4.1) | 2% | 10% |
| AC phase | 2° (0.035 rad) | 5° |
| Power factor | ±0.02 | ±0.05 |
| Trace waveform below `WaveformBandwidth` (NRMSE: RMS error ÷ RMS of the truth) | 3% | 10% |
| Energy balance per network | 0.5% of throughput | 0.5% of throughput |
| Overload heat input (each step's ∫i² dt per rated part) | 1% | 5% |
| Protection trip and melt time | ±1 step | ±1 step |
| Mean shaft torque | 1% | 5% |

**Error ratio.** Every observation produces `ratio = error ÷ budget` for each fidelity column the backend claims. A ratio ≤ 1 passes; the Markdown matrix also shows 1 to 3 as a warning band. How the error is computed, per observable class:

- **DC steady state, meters and power.** `|backend − truth| ÷ max(|truth|, floor)`. The floor is 1% of the scenario's full scale for that unit (the largest truth magnitude of that unit in the scenario), so a 0 A open-circuit current is not divided by zero (questions.md, trivia defaults).
- **AC phase.** The difference of the two angles, wrapped to (−π, π], in radians. Skipped when the amplitude is below the floor, where phase has no meaning.
- **Power factor.** Absolute difference.
- **Waveform.** Both the truth and the backend's trace are passed through the same fixed low-pass filter at the observation's `bandwidth` and compared at the trace's sample times (whole multiples of `1 / SampleRate`, api.md 3.10). NRMSE is the RMS of the difference over the window divided by the RMS of the truth. The scorecard also splits it into amplitude, phase and residual parts. If the backend's declared `WaveformBandwidth` is below the scenario's `bandwidth`, the observation is N/A, so a backend cannot pass by declaring little detail (questions.md C6).
- **AC values over time.** A one-cycle sliding DFT at the network's frequency, run over the truth waveform, gives the true amplitude and phase at every step. Each step's `AcValue` from the backend is compared with it, and so is the same DFT run over the backend's trace. This shows *where in time* a backend's AC values stop being valid (after a switch, during a frequency ramp). Scored with the meter and phase budgets.
- **Energy balance.** Per network and step, the energy residual is the sum of all parts' `StepEnergy` (sources count negative under the passive sign convention, so the sum should be zero). It is divided by the throughput: the energy delivered by sources over the same span. Needs no truth source, so it runs on every scenario.
- **Peak.** Relative error of the backend's `Peak` against the truth's peak, both under the definition in 4.1.
- **Overload heat input.** Each step's ∫i² dt per rated part against the truth's, as a relative error with the usual floor; this is what drives every backend's shared thermal model (api.md 3.4).
- **Trip and melt timing.** The truth's trip time is mapped to the step that contains it; the backend's `FuseBlown`, `BreakerTripped`, `ConductorMelted` or `PartBurnedOut` (including overvoltage failures, `Cause.Overvoltage`) event must be within one step of it. A missed trip or a false trip is a failure with ratio infinity.
- **Shaft torque.** Mean torque over the window, from `TorqueMeter`, as a relative error against the truth (always the reference backend).

### 4.1 Reading definitions

What every backend's readings mean, defined on the true waveform; a backend may compute them any way it likes. The truth source is read under the same definitions.

- **Window.** The most recent whole steps whose total length is at least the meter window `W` (api.md 3.8 rule 5). A circuit younger than `W`, or one just loaded, uses the history that exists.
- **Mean and RMS** of the true waveform over the window.
- **Peak.** The largest absolute value over the window of the true waveform after a fixed low-pass at the contract peak bandwidth for the fidelity: 100 Hz at Game, 1 kHz at Lesson and Strict (questions.md C6). It never depends on a backend's own bandwidth or on whether a trace is attached. The spike of an interrupted inductive current does not count towards other parts' peaks (api.md 3.4).
- **AC value.** The sine at the network's frequency (api.md 3.12) that best fits the true waveform over the window (least squares), with its angle expressed against circuit time at the end of the window.
- **Floating networks.** Node voltages are measured from the network's lowest-id node; the harness compares node voltages in floating networks only as differences.

## 5. Backend-neutral checks

These run on every scenario and every backend. They need no truth source.

| Check | What is compared | Must be |
| --- | --- | --- |
| Energy balance | Per-network residual, per step and over the run | within budget |
| Node balance | Currents into each node sum to zero: mean currents on DC; on AC the `AcValue` currents (`CurrentRms` at `CurrentAngle`) added as complex numbers | within the meter budget |
| Finiteness | No reading is NaN or infinite, ever | exact |
| Determinism | The same scenario (same steps, same edits per step) run twice | bit-identical |
| Scheduler invariance | Serial vs 1, 2, 8 worker threads | bit-identical |
| Observation invariance | With and without the scenario's traces: every reading, event, trip, torque and network grouping | bit-identical (questions.md T2) |
| Put-order invariance | `Put` order and group-member order within each step's batch shuffled (seeded) | bit-identical |
| Rebuild equivalence | The timeline's end state built from scratch vs reached by edits | within budget once transients settle |
| Snapshot round-trip | Save, rebuild, load, step vs stepping straight on | state within budget at once; readings within budget after one meter window, on every backend; loaded into another backend, after the slowest electrical transient settles |
| Hitch splitting | `Step(dt)` with `dt > MaxStep` vs the same equal slices called by hand (api.md 3.8 rule 8) | bit-identical |
| Caller splitting | `Step(a + b)` vs `Step(a); Step(b)` | within budget (questions.md C1); shown by alternate ticks |
| Robustness | Degenerate circuits of ideal parts: shorted sources, contradictory sources, a `CurrentSource` with no path, floating sections | a diagnosis and `LastGood` readings, no exception, no NaN; recovers within 2 steps of a repairing edit |

**Fuzzing.** A seeded generator builds random circuits from the supported part kinds and runs the checks above. A failure shrinks to a minimal circuit and is written out as a scenario file, which becomes a permanent regression test.

## 6. Scenario classes

Each class holds one or more scenarios. Together they are chosen to **discriminate between backends**: a backend that is good at steady AC and bad at sudden switching, or fast at DC and slow at 50 Hz, shows up. **M** marks the mandatory scenarios every shipped backend must pass. Truth: A analytic, S ngspice, R reference backend.

| Class | Circuit and timeline | Scored observables | Truth |
| --- | --- | --- | --- |
| steady-dc | Dividers, Wheatstone bridge, random ladders and meshes; lessons 01 and 02 | Node voltages, part currents, power | A, S |
| rc-rl-transient | RC and RL charge and discharge, τ from 0.1 s to 10 s, at ticks 0.05 and 0.5 (tick close to τ); lesson 04 | Voltage and current at each step, `StepStored`, energy balance | A |
| rlc-ring-down | An LC loop with a little resistance, charged and released | Trace NRMSE, envelope decay per cycle (any artificial damping shows here), energy balance | A |
| steady-ac-5hz | R, RC, RL and LC loads at 5 Hz | RMS, phase, power factor, real and reactive power, trace NRMSE | A |
| steady-ac-50hz | The same at 50 Hz | As above; cost per tick vs 5 Hz | A |
| **M** pure-reactance-pf | An ideal inductor alone, and an ideal capacitor alone, on 5 Hz | Power factor ≤ 0.02 at Lesson, phase 90°, real power and `HeatPower` near 0 | A |
| ramping-generator | A generator whose shaft speed ramps (spin-up, wind drift); a source whose amplitude ramps | `Ac.Frequency`, `Ac.VoltageRms` over time, AC-value error over time, mean torque | R |
| switch-mid-cycle | Closing into RL and RC loads at source phase 0°, 90°, 180°, 270° | Peak current, trace over the first cycles, heat in the first 0.5 s, settled RMS | S |
| **M** inductive-interruption | A breaker opening under transformer and motor load | Network stays `Live`; heat in the opening part equals ½ L I² of the interrupted loop; no other part fails on the spike | A, R |
| rectifier | Half-wave and full-bridge rectifiers with RC smoothing | DC mean, ripple peak-to-peak, trace NRMSE, AC-side RMS | S |
| **M** floating-nonlinear | A diode network with no ground | Either correct values (S, with a ground added where it changes nothing) or `Unsolvable` with a diagnosis; never silently wrong | S |
| unsynchronized-generators | Two generators on one bus, frequency difference swept 0.05 to 2 Hz | Beat envelope, circulating current RMS, mean torque on each, angle difference across the open breaker | R |
| **M** out-of-phase-close | Closing a breaker between two generators 180° apart | A real surge: peak current, heat, the breaker or fuse event at the right step; torque spike | R |
| mixed-frequencies | 5 Hz and 50 Hz sources on one network | `Approximate` set, total RMS, heat, energy balance; must run, not refuse (questions.md C2) | S |
| **M** fuse-rated-i2t | A fuse at 1.0×, 2× and 5× its rated current | `FuseBlown` time ±1 step; no trip at or just under rating | A, R |
| **M** cable-overload | A cable run with one thinner piece, overloaded; a breaker sized for the cable | `ConductorOverheating` then `ConductorMelted` on the hottest piece; temperature trajectory; the breaker trips first when sized correctly | R |
| **M** constant-power-collapse | Constant-power loads demanding more than a weak source through long cable can give (brownout) | Legible collapse: the scored drop-out set and order, `LoadDroppedOut` events, staggered restores and no strobing (bounded state changes per second), `Delivered` ≤ `Available`, energy balance | R |
| **M** sources-never-fight | A battery shorted through a fuse and cable; two batteries at different charge on one piece | `FuseBlown` (never `Unsolvable`); a finite circulating current with energy balance | A, R |
| **M** oscillating-load-pump | A load toggled every tick, with and without constant-power loads | Energy into loads ≤ energy from sources plus stored energy released; energy balance | invariant |
| transformer | Turns ratio, load step on the secondary, energisation at varied phase | Secondary voltage, primary current, efficiency, energy balance | S |
| mechanical-loop | Motor drives a shaft that drives a generator feeding the motor (shaft modelled by the harness) | Mean torque signs; total energy decays, never grows | invariant |
| **M** bulk-load-10k | 10,000 cable pieces and about 100 constant-power loads, Stationeers style, `Put` in random order | Put-order invariance; readings vs the in-order build; cold build time | self |
| chunk-freeze-resume | A network spanning two load areas; one unloads mid-run and later reloads | `Quality.Frozen` while unloaded; temperature, charge and overload heat unchanged while frozen; afterwards equals a never-frozen run shifted in time | self |
| **M** edit-during-step | A `Put` from another thread while `Step` runs | Applies at the next step; results bit-identical to the same edit made between steps | self |
| **M** remove-readd-same-id | `Remove` then `Put` of the same id in one batch | Bit-identical to a single replacing `Put` | self |
| **M** phantom-surge-on-merge | Two networks with constant-power loads joined by closing a breaker; the same after a save and reload | No 0 V reading on a live network; load current bounded by demand ÷ dropout voltage; no fuse event (the old build blew one at 597 A) | self, R |
| **M** hot-cable-reload | A cable heated to 90% of its I²t, saved, rebuilt and loaded | Trips at the same step as an uninterrupted run, ±1 step; `Temperature` and `OverloadHeat` restored | self |
| long-run-drift | One hour of game time, steady AC and DC | Phase drift against analytic, cumulative energy residual, finiteness (nightly, not per commit) | A |
| robustness | The degenerate circuits of section 5 | Pass/fail only | none |

Coverage today: only lessons 01, 02 and 04 exist as files. Every AC, transformer, generator and protection scenario is new authoring work; section 9 lists what can be lifted from the old suite.

## 7. Performance

All numbers come from the public API: `StepStats` returned by `Step` (wall time, allocated bytes, dropped time) and `Telemetry`. Runs record machine, runtime and backend version; numbers are compared only on the same machine and runtime.

| Metric | How | Budget |
| --- | --- | --- |
| Step time p50, p99, max | 1,000+ steady steps after warm-up, at the scenario's game tick; shown as a share of the game's budget, and per simulated second as a secondary column | VS: 5 ms of its 50 ms tick (10%). Stationeers: a stated share of the 0.5 s power tick, whose thread also runs atmospherics (questions.md E10). Tablet: as VS |
| Allocation per steady step | `StepStats.AllocatedBytes` and the runtime's allocation counter, on Unity Mono and net8.0 | 0 bytes for any shipped backend (api.md 3.19); reported only for experimental ones |
| Cold build | `Create`, all `Put`s, first published step, for the bulk-load-10k scenario; also the worst single step during it | well inside one 0.5 s tick; the worst step within the Stationeers share (questions.md B3) |
| Edit-step cost | The step after: a switch toggle; a part parameter change; a cable cut; a breaker merge; `ReplaceGroup` of 1,000 pieces; resending unchanged inputs | reported as a multiple of a steady step; resending unchanged inputs must cost no more than a steady step |
| Scaling exponent | Log-log fit of p50 over 10, 100, 1,000 and 10,000 parts, for a ladder, a mesh and a two-wire VS run; and over the number of separate networks | reported; a jump between commits is a regression |
| Trace cost | Steady step with a two-channel trace attached vs without | reported |
| Snapshot | Size and save/load time for the bulk-load-10k scenario | reported |

**Zero allocation is measured with care.** The runtime's allocation counter can over-report by a few kilobytes under concurrent garbage collection. The gate therefore runs N short sub-runs and passes if the minimum is zero; the reasoning lives in a code comment beside the gate.

**Baseline to beat.** From the old build, measured *below* the API (linear solving only, without device updates or nonlinear iteration) on a Ryzen 9 7950X3D with .NET 8.0.28. These are a lower bound on cost, not a system figure; the first scorecard run replaces them.

| Case | Old build |
| --- | --- |
| 10,000-node cold build (Stationeers load) | 1.8 ms general sparse solver (0.56 ms best specialised), 5.5 MB allocated |
| Worst realistic AC case: a 500-node network at the old build's highest internal rate, one 50 ms tick | about 460 µs per tick |
| 100-node network: one solve / one solve after a switch toggle | 0.9 µs / 2.1 µs |
| Steady-step allocation | 0 bytes |

Against a 5 ms VS budget this leaves an order of magnitude of headroom, which is why accuracy, not speed, decides the next backend.

## 8. The scorecard

`score` writes one `scorecard.json` per backend and commit, plus a Markdown matrix.

```json
{
  "backend": { "id": "manatee.default", "version": "3.1.0" },
  "commit": "9875b20", "runtime": "net8.0", "machine": "ryzen9-7950x3d", "date": "2026-10-06",
  "scenarios": [{
    "name": "rl-switch-on-5hz", "class": "switch-mid-cycle", "tick": 0.05,
    "observations": [{
      "what": "part 4 Ac.PowerFactor @2.9", "truth": "analytic", "truthHash": "…",
      "expected": 0.303, "actual": 0.311, "error": 0.008,
      "ratio": { "Game": 0.16, "Lesson": 0.40 }, "teachPass": true
    }],
    "checks": { "energyBalance": 0.0004, "observationInvariance": "pass", "putOrder": "pass" },
    "perf": { "p50Ms": 0.012, "p99Ms": 0.020, "maxMs": 0.031, "allocBytes": 0 },
    "na": []
  }],
  "errorRatio": { "Game": 0.41, "Lesson": 1.30 },
  "invariants": "pass"
}
```

- Each observation is scored at the game tick and at each alternate tick; alternate ticks appear as separate entries.
- An N/A entry gives its reason: unsupported part kind, a `bandwidth` above the backend's, or a missing capability (`MixedFrequency`). N/A is not a failure, but a mandatory scenario that is N/A for a part kind the backend claims to support is.

**The Markdown matrix** has one row per scenario class and one column per backend. Each cell shows the worst ratio per fidelity (≤ 1 pass, 1 to 3 warn, above 3 fail), p50 and p99 step time as a share of budget, and bytes per steady step. `scripts/bench.sh compare` diffs two scorecards on both axes (the current change against its parent, or backend A against backend B).

**`BackendInfo.ErrorRatio`** (api.md 3.18) is written from the scorecard of the release commit: for each fidelity, the largest ratio over all scored observations at their game tick. A backend ships for a fidelity only when that number is at most 1, every check in section 5 passes, and no mandatory scenario fails.

## 9. What happens to the existing suite

About 280 tests exist today. Roughly a third survive as scenarios, about 40% test the old low-level API and go with it, and the rest test one method's internals.

**Becomes scenarios** (keep the circuit and the assertion; replace the harness and the truth):

- The lesson corpus (`LessonCorpusTests`): lessons 01, 02, 04 converted as in 2.4.
- DC oracle tests (`DcOracleTests`, `RandomLinearOracleTests`, `DiodeOracleTests`, `VoltageDividerOracleTests`): steady-dc and rectifier; DC comparisons were always method-independent.
- `TransientOracleWaveTests` and `AcOracleTests`: rc-rl-transient, rlc-ring-down, steady-ac-5hz, rectifier, now against converged truth and the full waveform.
- Invariants (`LinearInvariantProperties`, `EnergyAuditTests`, `ConservationAuditTests`): section 5 checks, with tolerances from the budget instead of the old method's numerical losses.
- Devices: `AlternatorTests` (unsynchronized-generators; its loose 50 rad angle bound is replaced by a reference trajectory), constant-power load tests (constant-power-collapse), transformer and battery tests.
- Protection: `LimitsTests` (fuse-rated-i2t), attribution tests (cable-overload), one-event-per-tick coalescing.
- Robustness: `CircuitFaultTests` and the finite-answer parts of `DiodeNewtonTests`.
- State: rebuild-equivalence and snapshot-restore laws (section 5).
- The fake-client timelines (`FakeRevoltClientTests`, `FakeVsClientTests`): their breaker and overload timelines become scenarios as they are.
- Falstad importer tests: move with the importer to the schematic layer.

**Becomes backend-private tests** (in that backend's own test project, never part of this contract): anything asserting refactor, rebuild or internal-step counts; the step recurrence of one method; linear-algebra tests (pivoting, singular matrices, solver cross-agreement); method-matched ngspice decks, if kept at all. The pattern "an optimisation switched on vs off gives identical readings" is worth keeping as a template for any backend's internal options.

**Deleted:** handle-survival, merge-window, journal, partition and network-reduction-internals tests; deck golden files that bake in method options; the per-tier benchmarks, replaced by section 7.

### 9.1 Sharp-edges checklist

The old integration tutorial listed 17 traps. The new API must make each impossible; this list is the regression check.

| Old trap | Now |
| --- | --- |
| Re-pinning handles after a rebuild; deterministic probe keys; resolving nodes before an edit | Gone: ids are chosen by the client and never change |
| Change-ring overflow; draining the backlog before the first tick | Events wait until drained (api.md 3.11); bulk-load-10k drains every event |
| Reading networks that are not live; dead buses reading 0 | `Quality` on every reading; phantom-surge-on-merge |
| Caching spans across ticks; reading the raw vector mid-step | Gone: readings are an immutable lease (api.md 3.9); a test holds one across 100 steps and sees no change |
| Edits inside the steady-state guard | Gone; edit-during-step |
| A sine source single-sampled outside the AC profile | Gone (no profiles); steady-ac at the game tick |
| Relaxation clamp on coupled networks | Gone from the API; transformer energy balance |
| Restore cannot say what started cold | `LoadReport.StartedAtRest`; hot-cable-reload checks it |
| Partition keys on every cable piece | Gone; bulk-load-10k in random order |
| Free no-op adjustments | Setters are idempotent; edit-step cost for unchanged inputs |
| Allocation counter noise | Min-over-N gate (section 7) |
| Moving a breaker's ports | remove-readd-same-id |

## 10. Open questions

The open questions on testing and scoring are in [questions.md](questions.md): section C, plus T1 (the Game column), T3 (reference backend), T4 (5 Hz and 50 Hz benchmarks) and E10 (the Stationeers budget share). Minor defaults (the relative-error floor, no machine normalisation) are in its trivia line. Answers fold back into this document.
