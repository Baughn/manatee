# Glossary: Project-Coined Terms (part 2)

*Last updated: 2026-07-07. Part of the [Manatee glossary](../glossary.md) — see the index there for all terms and for how entries are structured.*

Manatee-specific vocabulary, each mapped to the closest standard concept in the literature.
### Lesson

One unit of the tablet's educational curriculum: a directory containing a small circuit, a narrative explaining it, and machine-checkable numeric expectations.

Each lesson is one directory (format seeded from Electrical Age's `docs/examples`): `lesson.txt`, a Falstad-format netlist that both the in-game tablet can import and CI can parse; `README.md`, a predict-then-observe narrative ("what do you expect the meter to read?") with numeric expectations stated in the text; and a machine-readable front-matter block (probe, time, value, tolerance) so CI verifies the narrative against the actual solver — *a lesson that stops being true fails the build*. For the EE: each lesson is a lab exercise with an automated grader that grades the textbook, not the student. For programmers: the lesson corpus doubles as a golden-test suite (testing-strategy.md), so pedagogy and regression testing are the same artifact. The arc runs 17 lessons from series voltage drops to generator synchronization, ceiling at AC power; component values are scaled so time constants are visible at game tick rates.

**Where it appears:** `docs/curriculum.md` (format, arc, authoring rules); `docs/testing-strategy.md` (lesson corpus as CI goldens); `docs/design.md` R16/R20.

### Lesson corpus
- The single shared collection of lesson directories — Falstad-format netlists plus Markdown narratives — that serves simultaneously as the tablet's tutorial content, the documentation's worked examples, and the project's golden test suite.
- "One corpus, three consumers" (design.md R20) is the organizing idea: rather than maintaining tutorials, doc examples, and regression fixtures as three drifting copies, Manatee keeps one authored set and points all three consumers at it. Each lesson is a directory in Electrical Age's `docs/examples` style: a `lesson.txt` circuit, a `README.md` narrative with numeric expectations, and a machine-readable front-matter block (probe, time, value, tolerance). For a programmer this is doctest taken seriously — documentation that executes. For an EE it is a lab-manual whose every stated meter reading is re-measured on each build. Because the corpus is CI truth, the Falstad importer that parses it is core infrastructure, not UI polish. As of 2026-07-06 the gate mechanism auto-discovers lesson directories, but the corpus holds only lessons 01–02 and 04; lessons 03 and 05–17 are deferred authoring work.
- **Project-coined term**, closest standard concepts: a golden-file test corpus (CS) combined with a lab-manual exercise set (EE); the single-source-of-truth discipline is akin to literate programming/doctests.
- **Where it appears:** `docs/testing-strategy.md` (The Lesson Corpus as Goldens); `docs/design.md` R20; `docs/harness.md` (Layering item 1, Testing Model); `docs/curriculum.md`.

### Lesson corpus as goldens
- The testing policy (design.md R20) that every tablet lesson is also a CI test: each lesson's circuit is solved automatically on every build and its answers compared both against ngspice and against the numbers the lesson's own text promises the player.
- CI consumes each lesson twice. The *oracle pass* runs the lesson netlist through manatee-core and through ngspice (the reference circuit simulator) and requires the node voltages to agree within tolerance — this is how a team without an EE ships a correct solver. The *narrative pass* reads the lesson's machine-readable front-matter (probe, time, value, tolerance — e.g. the RC lesson's capacitor probe reading about 9.32 V at 4.5 s) and checks it against the manatee solution. The slogan is: *a lesson that stops being true fails the build.* In programming terms, these are golden tests — expected outputs pinned in the repository, any divergence breaking CI — with the twist that the goldens are the player-facing teaching text itself, so pedagogy and solver can never silently drift apart. In lab terms: every worked example in the manual is re-run on the bench before each release, against a trusted second instrument.
- **Project-coined term**, closest standard concepts: golden-master (characterization) testing plus differential testing against a reference implementation (ngspice as oracle).
- **Where it appears:** `docs/design.md` R20 and the testing summary ("Lesson corpus as goldens: every tablet lesson is a CI test"); `docs/testing-strategy.md` (The Lesson Corpus as Goldens).
- **References:** the ngspice manual (ngspice project) for the oracle simulator; W. McKeeman, "Differential Testing for Software", Digital Technical Journal, 1998, for testing against a reference implementation.

### Lesson file (`lesson.txt`) and the corpus rule
- Each lesson directory contains a `lesson.txt`: the lesson's circuit written in Falstad text format, authored under Manatee's "corpus rule" so the same file works in the tablet, in CI, and in the original falstad.com/circuitjs simulator.
- The corpus rule (`docs/falstad-format.md` §6–7) is a deliberately narrow authoring profile of the Falstad format: an EA-style header line (`$ 1 <dt> 10 50 5`), `#` comment lines carrying narrative, coordinates on multiples of 16, DC values in the `maxVoltage` field, probes written as `O`, and only components from the importer's accept-list. The payoff is a compatibility invariant: every lesson file loads unmodified in falstad.com for authoring and debugging (circuitjs merely logs console noise on the `#` lines). For a programmer, this is like writing to a strict lint profile so one source file compiles under three toolchains; for an EE, it is like drawing schematics to a house drafting standard so any CAD package can open them. Upstream circuitjs example files are GPLv2, so our corpus is hand-written rather than vendored.
- **Where it appears:** `docs/falstad-format.md` §6 (Electrical Age dialect — "the corpus style R20 adopts") and the "Corpus rule for our own lessons" paragraph in §7; `docs/curriculum.md` Format section.

### Lesson / curriculum / phase lessons
- A *lesson* is one guided tutorial circuit on the in-game tablet; the *curriculum* is the ordered arc of 17 of them (DC foundations through AC power); the *phase lessons* are the two late-curriculum AC lessons (15: impedance, 17: synchronization) that put two traces on the oscilloscope at once so the player can see one waveform leading or lagging the other.
- Each lesson is a small circuit plus a teaching narrative in predict-then-observe form: the text asks the player to guess a number ("what will the capacitor read after 4.5 s?") before the meter reveals it. For an EE this is a lab exercise with a lab manual; for a programmer it is best understood as an executable specification, because per R20 every lesson's stated numbers are also checked in CI — a lesson whose claims stop matching the solver fails the build. The phase lessons deliberately avoid the word "phasor": phase relationships are shown as two simultaneous scope traces (the tablet scope is the primary consumer of the solver's two-probe contract), not taught as complex arithmetic. "Phase lessons" here means "the lessons about phase", not phases of the curriculum.
- **Where it appears:** `docs/curriculum.md` (the full 17-lesson arc and authoring rules); `docs/design.md` R16 (the tablet) and R20 (one corpus, three consumers); `docs/vintage-story.md` (Tablet Host, Instruments — curriculum 15/17 comparing two traces).

### lesson engine
- The UI-free component of the tablet that runs a lesson: it sequences narrative pages and predict-then-observe questions around a live circuit, without knowing anything about how it is drawn or clicked.

The tablet harness is built as three pure layers (schematic document, interaction state machine, display-list view); the lesson engine sits *beside* them as "document-plus-content orchestration, equally UI-free." It owns the pedagogical flow — show a page of narrative, ask the player to predict a value, then run the circuit and compare — while the circuit itself is an ordinary document that the other layers edit and render. Because it contains no widget-toolkit or rendering code, it can be driven headlessly in CI just like the other layers, which is what lets lesson expectations become build-failing tests. For an EE: think of it as the lab-manual script for a bench exercise, kept separate from both the instruments and the circuit — it says what to build, what to predict, and what to measure, but is not itself any of those things.

**Project-coined term**, closest standard concepts: a tutorial/scenario scripting system; in software-architecture terms, application logic separated from presentation (as in model–view separation).

**Where it appears:** `docs/harness.md` (Layering, closing paragraph); lesson content and authoring rules in `docs/curriculum.md`.

### lesson.txt
- The file, one per lesson directory, that holds the lesson's circuit as a Falstad-format text netlist.

Each curriculum lesson is a directory containing `lesson.txt` (the circuit), a `README.md` (the narrative with predict-then-observe Q/A and stated numeric expectations), and a machine-readable front-matter block of expectations. `lesson.txt` is dual-use by design: the tablet can import it so the player opens the exact circuit the lesson discusses, and CI parses and simulates it to verify the narrative's numbers against the solver — "a lesson that stops being true fails the build." The format is the Falstad/circuitjs1 text format (see `docs/falstad-format.md`), following the layout Electrical Age used in its `docs/examples` corpus, which seeds ours. For a programmer: it is a fixture file that doubles as shippable content. For an EE: it is the schematic capture file, in a plain-text netlist dialect rather than a binary CAD format.

**Project file convention** (the filename); the format itself is the standard Falstad/circuitjs1 text format.

**Where it appears:** `docs/curriculum.md` (Format); format spec in `docs/falstad-format.md`; CI role in `docs/testing-strategy.md`.

### limit attribution
- Answering "which physical piece actually fails?" when the solver flags an overload on a simplified stand-in for many pieces.

For speed, the reduction layer collapses long runs of cable — thousands of voxels or segments — into single equivalent resistors before solving (see series collapse). When the solver then raises a limit event (overcurrent, overvoltage, overpower, or i²t thermal) on such an equivalent, that event names a component that does not physically exist. Limit attribution maps it back to the specific constituent segment that melts, burns, or pops, using current density at the narrowest cross-section as the rule, evaluated *per limit type* — instantaneous ampacity, i²t thermal mass, and melting threshold can each pick a different segment in a mixed-material chain (exactly the lead-fuse-in-a-copper-run case). The solver never mutates the circuit in response; it reports, attribution localizes, and the game client applies consequences. Programmer's analogy: symbolication — the solver reports a fault against "optimized code," and attribution is the source map back to the original line. In the API this is `ConductorGraph.Attribute(in LimitEvent, out AttributionResult)`.

The division of labor is strict: the solver detects and reports the event (R7, "it reports, you act"), the reduction layer attributes it, and the client mutates (e.g. `RemoveSegment` to melt a fuse) — attribution itself is a reverse index from an aggregate back to its constituents, using per-segment data the reduction layer retained at collapse time.

**Project-coined term**, closest standard concept: fault localization. SPICE-family simulators have no analog — they simulate the unreduced netlist and have no ratings model, so the question never arises.

**Where it appears:** `docs/compaction.md` (Responsibilities #4), `docs/solver.md` (State, Limits, Probes), `docs/design.md` (R7), `docs/api.md` §12 (`LimitEvent`), §19 (`Attribute`, `AttributionResult`), `docs/integration-tutorial.md` §8 (worked fuse-melt example).

### limit envelope
- The combined rating a collapsed equivalent element carries: for each kind of limit, the minimum over all the real segments it replaced, so the weakest link still governs.

When series collapse replaces a chain of segments with one equivalent resistor, each segment's ratings (max current, voltage, power, i²t thermal) must not be lost — a chain fails when its *weakest* member does. The envelope is the per-limit-type minimum over the constituents; because different limit types can bottom out at different segments, it is a small record of minima, not one number. The i²t component is further ruled (2026-07-06) to be a Pareto-minimal *set* of (rating, melt) pairs rather than a single hybrid pair — mixing one segment's rating with another's melt point would trip when no real segment would. Envelopes are environment-adjusted per segment (ambient temperature spans −150 °C to +800 °C across Stationeers worlds); an ambient change dirties only the envelope, a metadata recompute that never touches the solver matrix. The same concept covers grounding electrodes in `design.md`, whose envelope models soil drying/glassification under sustained overload. Programmer's analogy: an aggregate invariant cached on a compressed node — like a segment-tree node storing the min of its range — kept so queries against the compressed form remain exact.

**Project-coined term**, closest standard concept: rating/derating aggregation of series elements (weakest-link ampacity); there is no standard single term.

**Where it appears:** `docs/compaction.md` (Responsibilities #4), `docs/design.md` (R7, R15, grounding model, and the decision-log "per-limit-type, environment-adjusted envelope" summary), `docs/stationeers.md` (Graph Construction), `docs/api.md` §12 (`SetThermalEnvelope`, `I2tPair`) and §19.

### limit envelope / LimitSpec
- `LimitSpec` is the C# struct that states one element's ratings (max current, voltage, power, plus i²t thermal parameters); the limit envelope is what those specs become after series collapse merges many elements into one.

`LimitSpec` is how a client declares ratings when adding a component or a conductor segment: `public readonly struct LimitSpec { double MaxCurrent, MaxVoltage, MaxPower; I2tParams Thermal; }` (`docs/api.md` §12). A fuse needs no special component type — it is just a segment whose `LimitSpec` caps current, e.g. `new LimitSpec(6.0, 0.0, 0.0, default)` for a 6 A fuse in the tutorial's walkthrough (zeros mean "unlimited" for that field). When the reduction layer collapses a chain of such segments into one equivalent resistor, the individual specs ride through as that equivalent's *envelope* — the per-limit-type minimum over the constituents — so the solver checks one compact record and limit attribution later names the actual culprit segment. The pair is spec-in, envelope-out: `LimitSpec` is the source of truth per real element; the envelope is the derived, cached aggregate on the collapsed form. For an EE: `LimitSpec` is the nameplate rating of one part; the envelope is the derated rating of the assembled run, governed by its weakest part. (An older doc name, `LimitConfig`, appears in `docs/solver.md`; the as-built type in `docs/api.md` is `LimitSpec`.)

**Project-coined API type**; closest standard concepts: component ratings / fuse i²t let-through characteristics as tabulated in manufacturer data and wiring codes.

**Where it appears:** `docs/integration-tutorial.md` §3 and §8, `docs/api.md` §12 and §19 (`LimitSpec`, `ConductorSpec.Limits`, `SetLimits`), `docs/compaction.md` (Responsibilities #4).

**References:** Horowitz & Hill, *The Art of Electronics* (fuse ratings and i²t behavior in the real-world chapters).

### limit event
- A report the solver emits after a solve when some component exceeded one of its configured ratings (too much current, voltage, power, or accumulated heat), so the game can decide what breaks.

Each component may carry limits (design.md R7): maximum current, voltage, power, and an i²t thermal accumulator for slow overloads. After each solve the solver checks these and queues a `LimitEvent` per violation; it **never changes the circuit itself** — it reports, the client acts (pops the fuse, melts the cable, trips the breaker). For programmers: it is an event queue drained by polling, like a fixed-capacity ring buffer of diagnostics — overflow is counted, never allocated. For the EE: this is the protection-coordination layer of the simulator, except relaying and tripping are deliberately left to the game so failures become gameplay. Because the reduction layer collapses many physical segments into one equivalent resistor, an event on an equivalent must be mapped back to the culprit segment via attribution (see *limit attribution*). Fuses and Stationeers breaker trip curves are built entirely on these events rather than as special-cased devices.
- **Where it appears:** design.md R7; solver.md "State, Limits, Probes"; compaction.md Responsibilities #4; stationeers.md (fuses/breakers ride limit events); api.md §11 (`DrainLimitEvents`), §12 (`LimitEvent`); integration-tutorial.md §8; testing-strategy.md (equivalence tests).
- **References:** the mechanism-vs-policy split is classic OS design (canonically R. Levin, E. Cohen, W. Corwin, F. Pollack, W. Wulf, "Policy/mechanism separation in Hydra", SOSP 1975); i²t fuse ratings are standard practice, see e.g. IEC 60269 / Horowitz & Hill, "The Art of Electronics", on fusing.

### limit type
One of the independent categories of rating a conductor or component can violate — instantaneous ampacity, i²t thermal mass, or melting threshold — each of which can trip on its own.

When the reduction layer collapses a chain of segments into one equivalent resistor, it keeps a **limit envelope** aggregated per limit type, not once overall: in a mixed-material chain, the segment with the lowest instantaneous current rating, the one with the least thermal mass, and the one with the lowest melting threshold can be three *different* segments. This is exactly what makes a thin lead segment spliced into a copper run behave as a fuse with no special-casing — it just happens to be weakest for the melting type. For programmers: for the scalar dimensions (instantaneous ampacity, melting threshold) the envelope is a small struct of per-field minima, each field aggregated independently; the i²t dimension is deliberately *not* a single independently-minimized scalar — it is kept as a **Pareto-minimal set of (rating, melt) pairs**, one per distinct material in the chain, because a hybrid built from one segment's rating and another's melt energy would trip when no raw segment would (ruled 2026-07-06, `docs/compaction.md` #4 / `docs/api.md` §19). When a limit event fires, attribution is answered per type, so the "which segment melts" lookup depends on *which kind* of limit tripped. For the EE: this is the standard distinction between a wire's ampacity, its i²t (let-through energy) withstand, and its fusing point, kept separate rather than folded into one number.

**Project-coined term**, closest standard concepts: ampacity, i²t (Joule-integral) withstand rating, and fusing current — treated as independent trip criteria.

**Where it appears:** `docs/compaction.md` (Responsibilities #4, limit attribution); the trip categories surface in the API as `LimitKind` (`docs/api.md` §12).

**References:** i²t (Joule-integral) let-through and fuse melting characteristics are specified in IEC 60269 (*Low-voltage fuses*) and in fuse manufacturers' published time–current and i²t characteristic curves.

### LimitConfig
The design-doc name for a component's per-component limit configuration: maximum |I|, |V|, P, plus a thermal accumulator for slow-overload modeling.

Every component may carry limits stating the largest current magnitude, voltage magnitude, and power it tolerates, plus an i²t thermal accumulator so that fuses and slowly overheating cables share one mechanism (a small sustained overload integrates heat over time instead of tripping instantly). The solver checks these after each solve and raises `LimitEvent`s when they are exceeded — it reports, it never mutates the circuit itself (design requirement R7). For the EE: this is the component's rating plate, with the thermal part being a standard i²t (Joule integral) model. For programmers: it is a plain configuration value attached per component, read by a post-solve validation pass.

**Note on naming:** `LimitConfig` is the name used in `docs/solver.md`; the as-built API type is **`LimitSpec`** — api.md's decision log (#26) explicitly retired `LimitConfig` as a "phantom type" (`Meta.SetLimits` takes `LimitSpec`). Treat the two names as the same concept, with `LimitSpec` being canonical in code.

**Where it appears:** `docs/solver.md` (State, Limits, Probes section); `docs/design.md` R7; realized as `LimitSpec` in `docs/api.md` §12.

### LimitEvent
A notification the solver emits after a solve when a component has exceeded one of its configured limits; the client — not the solver — decides what breaks.

A `LimitEvent` is a fixed-size record carrying the source component, the `LimitKind` that tripped (overcurrent, overvoltage, overpower, or thermal i²t), the observed value versus the threshold, the i²t fraction, substep time and tick index (so replays diff cleanly), and for thermal envelopes a `PairIndex` naming which accumulator crossed its melt threshold. Clients call `DrainLimitEvents(Span<LimitEvent>)` after `Solve`: events are drained per island into the caller-supplied buffer from a fixed-capacity ring, and overflow is counted (surfaced via an `out long dropped` overload) rather than growing the ring, so a client that skipped a drain learns events were lost and the per-tick drain stays allocation-free (R8). The defining contract (R7): **the solver never mutates the circuit in response to a limit** — popping the fuse or melting the voxel is the client's follow-up edit (via `Adjust`/`Reconfigure` or by removing the segment), after the reduction layer attributes the event on a collapsed equivalent back to the culprit segment. For the EE: the solver is pure instrumentation, like a protection relay that only signals — the breaker mechanism lives in the game layer. For programmers: it is an event-queue/observer pattern with strict separation between detection and consequence, plus bounded, allocation-free delivery.

**Project-coined term**, closest standard concepts: a simulator convergence/limit warning crossed with a protection-relay trip signal.

**Where it appears:** `docs/solver.md` (State, Limits, Probes); `docs/design.md` R7; `docs/api.md` §12 (struct definition) and `DrainLimitEvents`, §20 (error model, "client pops the fuse"); attribution in `docs/compaction.md` and api.md §19; worked usage in `docs/integration-tutorial.md` §8.

### LimitKind (OverCurrent, etc.)
The enum tag on a `LimitEvent` saying *which kind* of limit was violated: `OverCurrent`, `OverVoltage`, `OverPower`, or `ThermalI2t`.

When the solver reports a limit violation, the event's `Kind` field distinguishes an instantaneous current excess from a voltage excess, a power excess, or a thermal-accumulator (i²t) trip. Clients dispatch on it: the tutorial's fuse-melting loop, for example, filters for `LimitKind.OverCurrent` before asking the reduction layer to attribute the event to a segment and removing it. For programmers: it is an ordinary discriminant enum (`byte`-backed for compactness) on an event record. For the EE: it is the trip cause — the same distinction a protection engineer makes between an instantaneous overcurrent element, an overvoltage element, and a thermal (i²t) element in a relay. The `ThermalI2t` kind additionally carries a `PairIndex` identifying which envelope accumulator tripped.

**Where it appears:** `docs/api.md` §12 (`public enum LimitKind : byte { OverCurrent, OverVoltage, OverPower, ThermalI2t }`); `docs/integration-tutorial.md` §8 (dispatch example).

### LimitSpec / LimitKind / I2tPair
The trio of API types that configure a component's limits, name what tripped, and describe one thermal-envelope accumulator, respectively.

**`LimitSpec`** is the as-built configuration struct: `MaxCurrent`, `MaxVoltage`, `MaxPower`, plus a `Thermal` field (i²t parameters) for slow-overload modeling. It is passed at add-time (e.g. `AddResistor(..., in LimitSpec limits)`) or later via `Meta.SetLimits` — a tier-0, allocation-free metadata edit that never touches the matrix. **`LimitKind`** is the enum on the resulting events (`OverCurrent`, `OverVoltage`, `OverPower`, `ThermalI2t`). **`I2tPair(RatingAmps, MeltI2t, Tau)`** describes one thermal-envelope accumulator: above `RatingAmps` the accumulator heats by (I² − rating²)·dt, below it it cools with time constant `Tau`, and it trips when it crosses `MeltI2t`. A component may register 1..k pairs via `Meta.SetThermalEnvelope` (a Pareto-minimal set, one per distinct material in a collapsed chain); a registered envelope supersedes `LimitSpec.Thermal`, and plain-`LimitSpec` users are simply the k=1 case. For the EE: `LimitSpec` is the rating plate, `I2tPair` is one leaky-integrator thermal model (an RC-style heat accumulator against a melt energy). For programmers: config struct, event discriminant, and one accumulator cell of a per-component array whose index (`PairIndex`) round-trips through the event for attribution; accumulators are part of saved state, so a half-melted cable reloads half-melted.

**Project-coined types**, closest standard concepts: device ratings plus a fuse's i²t (Joule-integral) melting characteristic with exponential cooling. (Design docs' older name `LimitConfig` = `LimitSpec`.)

**Where it appears:** `docs/api.md` §12 (definitions and thermal-envelope semantics), §4 (`Meta.SetLimits`, `Meta.SetThermalEnvelope`), §19 (attribution); `docs/integration-tutorial.md` (fuse-segment example).

**References:** i²t fuse melting/let-through characteristics: IEC 60269 (*Low-voltage fuses*) and fuse manufacturers' published i²t and time–current curves.

### LiveFloorVolts / live-floor shed
- A small voltage threshold, chosen by the game-side client (e.g. 1 V in the tutorial example), below which a constant-power load adaptor treats its bus as genuinely dead and backs off to near-zero conductance instead of ramping up.

The problem it solves: a constant-power device is modeled as an adjustable conductance G = P/V² — to deliver fixed power P at low voltage V it must raise G. If the bus legitimately reads ~0 V (a de-energized island, an island that is Faulted right now, or one that has never published a solution), a naive adaptor seeks its maximum conductance and stamps what amounts to a fictional dead short into the matrix; the tutorial's first draft popped a real 6 A fuse with a phantom 597 A surge this way. The live-floor shed is a one-line guard: `if (absV < LiveFloorVolts) g = GMin;` — below the floor, shed to the minimum conductance and never seek GMax. Note this is client-side defensive code, not a solver feature: even with the merge-window last-good-read fixes (api.md §17.4), a bus can still *legitimately* read ~0 V, so adaptors must tolerate a zero read. For an EE, it is undervoltage load-shedding applied to a companion-model conductance; for a programmer, it is input validation on a feedback loop whose failure mode is divide-by-small-number blowup.

**Project-coined term**, closest standard concepts: undervoltage load shedding / undervoltage lockout (UVLO) applied to a constant-power-load companion model.

**Where it appears:** `docs/integration-tutorial.md` §6 (merge-window reads) and sharp-edges appendix edge 4; `examples/RevoltWalkthrough/TutorialLoad.cs` (the `LiveFloorVolts` constant and the shed branch).

### lost (== true)
- The overflow flag returned by `DrainChanges(buffer, out bool lost)`: when true, the solver's fixed-size ring of island-membership change events overflowed and some events were silently dropped — and the client is now **obligated** to re-resolve every handle it holds.

`DrainChanges` is the game client's one notification channel for island membership changes (never the journal, which serves the reduction layer). The events live in a fixed ring buffer; a burst of changes larger than the ring drops the oldest entries and sets `lost`. Because the client can no longer know *which* handles went stale, the contract escalates from "re-pin the handles named in these events" to "re-pin everything by key" — the tutorial is emphatic that this is *an obligation, not a warning* (sharp-edges appendix, edge 2). The recommended client shape makes compliance trivial: one idempotent `RepinActive()` that re-resolves all held handles, called whenever `lost || changes > 0 || hadTopology`, since re-pinning is cheap. A known benign case: the construction burst at world load overflows the ring, so clients should loop `DrainChanges` empty before the first game tick (edge 16) to avoid a spurious full re-pin. For an EE, it is like a chart recorder whose paper ran out mid-event: you don't guess what you missed — you go re-measure every point from scratch. The pattern is standard for bounded event queues (drop-plus-resync, as in Linux inotify's `IN_Q_OVERFLOW`); a full state resync is the only sound recovery from lossy notification.

**Where it appears:** `docs/integration-tutorial.md` §5 (tick-loop step 2) and appendix edges 2 and 16; `docs/api.md` §11 (`DrainChanges` contract); `examples/RevoltWalkthrough/` (`RepinActive`, `FirstSolve`).

### Machine (device)
- Our devices-layer category for the electromechanical and heat-producing gameplay devices — lamps, heaters, motors, alternators — as opposed to wiring, batteries, or converters.

A machine is a device whose electrical footprint is simple (a resistive stamp, or a source-like EMF-plus-resistance stamp) but whose *parameters* are computed each tick from game state by the device's `Tick()` method: an alternator turns shaft speed into EMF, a lamp could turn filament temperature into resistance (for inrush), a motor reports counter-torque back to the mechanical simulation. For programmers: the machine is an adapter between the game's world state and a handful of numbers stamped into the matrix — the solver never knows it is a "motor", it only sees a resistor or a source whose value changed. For the EE: these are ordinary lumped elements whose values are externally scheduled, not new component physics. Machines sit in the devices layer, above the netlist; device authors never see matrices.
- **Project usage note:** "machine" here is a gameplay/devices-layer grouping, not an EE term of art (it loosely echoes "electrical machine" = motor/generator, but our usage also covers lamps and heaters).
- **Where it appears:** `docs/solver.md` Component Set ("Machines (lamps, heaters, motors, alternator)"); the mechanical-coupling material in `docs/vintage-story.md`.

### Machine-readable expectations
- Structured assertions embedded in each tablet lesson — tuples of (probe, time, value, tolerance) — that CI runs against the real solver, so a lesson whose narrative stops being true fails the build.

Each lesson pairs a human story (README with predict-then-observe questions and stated numeric expectations) with a front-matter block restating those numbers in a form a test harness can parse: which probe to read, at what simulated time, what value to expect, within what tolerance. CI imports the lesson's Falstad-format netlist, runs it through manatee-core, and checks every tuple; a failure breaks the build exactly like a failing unit test. For the EE: this is the same discipline as keeping a bench measurement log next to a schematic, except an automated technician re-runs the measurements on every change and refuses to ship if the readings drift. This turns the entire lesson corpus into a regression-test suite for the solver (the "lesson corpus as CI goldens" idea) — the pedagogy and the correctness guarantee are the same artifact.
- **Project-coined term**, closest standard concepts: golden-file / characterization tests, executable specifications, doctests.
- **Where it appears:** `docs/curriculum.md` Format (front-matter block); `docs/testing-strategy.md` (lesson corpus as CI goldens).

### manatee-core
- The shared C# solver library at the heart of the project: the circuit-simulation engine (Modified Nodal Analysis plus the reduction/compaction layer) that both game integrations consume.

Everything game-specific sits above it: the Vintage Story mod, the Stationeers Re-Volt integration (which pulls manatee-core in as a git submodule), and the 2D schematic harness are all *clients* of this one library. In EE terms, manatee-core is the SPICE-like engine — netlist in, node voltages and branch currents out — while the games supply the geometry, UI, and gameplay. In programming terms, it is the dependency-free core of a layered architecture: deterministic, testable without any game running, and deliberately free of game-engine references. Licensing is a hard constraint on its contents: manatee-core is MIT (because Re-Volt is MIT), so GPL-licensed material such as upstream Falstad example files must never be vendored into its shipped test corpus, and the LGPL CSparse.NET dependency is dev/test-only and never shipped.

**Project-coined term**, closest standard concept: a circuit-simulation engine/library (analogous to the core of SPICE or ngspice, exposed as a reusable library rather than a standalone program).

**Where it appears:** `docs/design.md` (header, System Overview, Repository Layout, Delivery Order), `docs/testing-strategy.md` (Principles), `docs/falstad-format.md` §7 (licensing rule), and as the code under `core/Manatee.Core/`; `docs/api.md` is its binding as-built surface.

**References:** Ho, Ruehli & Brennan (1975), "The Modified Nodal Approach to Network Analysis", IEEE Trans. Circuits and Systems (the analysis method the core implements); Nagel (1975), SPICE2: A Computer Program to Simulate Semiconductor Circuits, UCB/ERL-M520 (the archetype of such an engine).

### Manatee.Harness
- The desktop application project (built on the Avalonia UI framework) that hosts the 2D schematic editor for development, testing, and lesson authoring.

It is the "shell": an Avalonia app wrapping a Skia-rendered canvas plus authoring conveniences — a lesson text pane, a component palette, probe readouts. All the actual schematic logic lives one layer down in `Manatee.Schematic`; the harness only adapts native windowing and input to that layer. For an EE, the analogy is a scope's front panel versus its acquisition engine: the harness is the panel — knobs, screen bezel, power switch — and is deliberately kept dumb and swappable, while the engine underneath does the real work. By design rule, nothing in `Manatee.Schematic` may reference Avalonia, so the tested tablet stack carries no dependency on this shell; end-to-end tests through the shell exist but stay rare.

**Project-coined term** (a project/assembly name), closest standard concept: an application shell or test harness hosting a reusable UI engine.

**Where it appears:** `docs/harness.md`, "The Desktop Shell" section.

### Manatee.Schematic
- The .NET 8 library that contains the entire schematic-editor engine as three "pure" layers — document model, interaction state machine, and view layer — with no UI-framework dependency.

The three layers are: (1) the **document model** — components, wires, junctions, probes, the Falstad importer, and the binding to manatee-core for netlist extraction and solution readback; (2) the **interaction state machine** — consumes abstract input events (pointer down/move/up, wheel, key) and owns all editing state such as tools, drags, selection, and snapping; (3) the **view layer** — renders document plus interaction state into a display list of draw commands. "Pure" here means side-effect-free of any widget toolkit: for an EE, think of a well-designed instrument whose measurement core is fully specified and testable on the bench, independent of whatever front panel gets bolted on. That purity is what lets the same assembly power both the desktop harness (`Manatee.Harness`) and, later, the in-game Vintage Story tablet, and be tested headlessly without a display.

**Project-coined term** (an assembly name), closest standard concept: a UI-framework-agnostic model/controller/view core, in the spirit of the Model-View-Controller separation.

**Where it appears:** `docs/harness.md`, "Layering" and "The Desktop Shell" sections.

**References:** Gamma, Helm, Johnson & Vlissides, *Design Patterns* (1994), for the separation-of-concerns patterns the layering draws on.

### Matched / OrphansInBlob / StateUnitCount
The three numbers that together tell you how well a save/load restore went: how many state units a blob restored, how many blob entries had nothing to restore into, and how many state units the island has in total.

When a saved blob is offered to an island, `Restore` returns `Matched` (state units — capacitor voltages, device internals, melting integrals — that this blob overwrote) and `OrphansInBlob` (entries in the blob whose `StateKey` no longer exists in this island — expected after an island split, since the same blob is offered to every resulting island, not an error). `StateUnitCount` on the island is the denominator: after *all* blobs have been offered, `StateUnitCount − sum(Matched)` is the number of genuinely cold-started units, i.e. units that fell back to power-up defaults. The binding rule is that coverage is **aggregate, never per call** — because restore is additive (a blob touches only its own keys and never resets anything else), no single `Restore` call can know whether some later blob will cover the units it missed. An EE analogy: it's like restoring initial conditions onto a circuit from several partial measurement logs — you can only declare a node "unknown, assume zero" after every log has been consulted, not after the first one.

**Project-coined terms**, closest standard concept: restore/deserialization coverage accounting (checkpoint-restart bookkeeping).

**Where it appears:** `RestoreResult` in `docs/api.md` §14 (fields at api.md:922-926), `IslandHandle.StateUnitCount` (api.md:767); worked example in `docs/integration-tutorial.md` §7 and sharp-edge #10 in its appendix.

### MaxVoltage (vanilla)
A field on Stationeers' vanilla cables that is named like a voltage rating but actually stores a power (watt) rating — a base-game misnomer that Manatee's Re-Volt integration deliberately reinterprets as a genuine voltage rating.

Vanilla Stationeers has no real voltage model: its "electricity" is watt bookkeeping, so the cable field called `MaxVoltage` in fact caps power throughput in watts. Under the Manatee/Re-Volt scheme (settled with Sukasa), circuits get real, player-facing voltage tiers — device nameplate ratings, transformer steps — and the field is repurposed to mean what its name says: the cable's actual voltage rating, above which insulation-failure consequences apply. For EEs: the fix restores the ordinary distinction between a conductor's voltage withstand rating and its ampacity/power limit, which vanilla conflated into one number. For programmers: it's a classic misleading-identifier refactor — the variable name was aspirational, and the new system makes the name true rather than renaming it (the field name is fixed by the shipped game's data model).

**Standard term:** n/a — this entry flags a vanilla-game misnomer; the standard concepts it straddles are rated voltage (insulation rating) vs. power rating.

**Where it appears:** `docs/stationeers.md` "Voltage Tiers" (stationeers.md:141-148). Distinct from the Falstad-format `maxVoltage` source-amplitude field — same spelling, unrelated meaning.

### Mechanical network / mechanical power subsystem
- Vintage Story's built-in rotational-power system — shafts, gears, windmills, waterwheels — which Manatee treats as the physical origin of AC electricity.

It is the engine's own code (`vssurvivalmod/Systems/MechanicalPower/`), not ours: each connected assembly of shafts is a "network" whose nodes report signed torque (sources) or a resistance (consumers) via `IMechanicalPowerNode.GetTorque`; the engine sums these every ~100 ms and integrates a single network speed with a momentum/drag model. For the EE: it is a lumped one-state rotational model per network — one shared angular velocity, torques summed like currents into a node. For the programmer: it is a pluggable observer system we join by subclassing behaviors — an alternator is a consumer whose `GetResistance()` returns counter-torque as a function of electrical load, a motor is a rotor driven from electrical input; no engine changes needed. Shaft speed read from it (`TrueSpeed`) sets electrical frequency: the engine integrates angle so ω = 5·speed rad/s, giving f ≈ 0.8 × speed × polePairs Hz, with pole-pair count as the honest design knob (no fudge factor).
- Aliases: the docs use "mechanical network", "mechanical power subsystem", and "shaft network" interchangeably.
- **Where it appears:** vintage-story.md §"Mechanical Network Coupling (R14)" (with engine file:line references); design.md R14 and the Simulation Model.

### Memento
- Our name for the whole-netlist serialized form produced by `SaveCanonical`: a byte blob that captures the circuit *and* its evolving state so a reloaded world resumes mid-story rather than cold-starting.

Unlike a plain description of the circuit (what components exist and how they connect), the memento also carries state that only exists because time has passed: partly-filled melting integrals, per-pair envelope accumulators and trip latches, coupler runtime state, settled converter operating points, capacitor voltages. For the programmer: it is the Memento pattern applied to a simulator — an opaque snapshot that restores the object to exactly where it was, slot-preserving so replay and per-island snapshots remain compatible. For the EE: reloading from it is like closing a breaker onto an already-energized, already-warm system — no inrush, no re-convergence transient, a partway-melted fuse is still partway melted. Its counterpart `SaveNormalized` deliberately drops the evolved state (keeping configuration like envelope pairs) so drift-equality checks compare circuits, not histories. Round-trip law: `SaveCanonical(FromCanonical(x)) == x` byte-equal.
- **Project-coined usage**, closest standard concepts: serialized snapshot / checkpoint; the name borrows the Memento design pattern.
- **Where it appears:** api.md §12 and §14 (what rides the v3 memento), and the `SaveCanonical`/`SaveNormalized` contract (api.md, "Two whole-netlist serializations"); integration-tutorial.md §7 for the per-island snapshot sibling.
- **References:** Gamma, Helm, Johnson & Vlissides, "Design Patterns: Elements of Reusable Object-Oriented Software" (Addison-Wesley, 1994) — the Memento pattern.

### merge pre-check
A quick test run on newly added cable geometry before deciding how to update the circuit: would this new piece connect (bridge) more than one existing region of the network?

When a player places a wire segment, the reduction layer must fold it into its compacted view of the circuit. Most placements just grow one existing region, and those take a cheap incremental path that touches only the dirty area. But if the new geometry touches two or more previously separate regions, it fuses them, and the bookkeeping for that is complicated enough that we don't attempt it incrementally — we rebuild the affected island from scratch instead. The pre-check is the branch condition that picks between those two paths. In programming terms it is a guard on a fast path: like a cache that answers simple lookups directly but falls back to full recomputation when an update would cross shard boundaries. For an EE: it is the moment the software notices that one new wire has turned two circuits into one, which changes the whole network's structure rather than just extending it. Rebuild-at-island-scale is deliberately cheap in this design, so falling back is safe (see resync backstop).

**Where it appears:** `docs/compaction.md`, Incremental Maintenance section; implemented in `ConductorGraph`.

### merge window
The brief interval, after two islands (independent sub-circuits) are merged by closing a coupler such as a breaker, before the newly combined island has produced its first solved result.

When a galvanic coupler closes, the solver immediately updates the *structure* — the two islands become one — but the *numbers* (voltages, currents) for the combined circuit don't exist until the next `Solve` runs and publishes. The merge window is that gap. Manatee's rule for the gap is that reads never invent values: everything keeps reporting its last successfully published (last-good) result until the merged island publishes for the first time. This mirrors the project's general document/matrix split: structure is transactional and immediate, numbers are last-tick's until you solve. A programmer can think of it as an eventually-consistent read replica: writes (the merge) are acknowledged instantly, but queries serve the previous consistent snapshot until the next commit lands. An EE can think of it as the instant after throwing a breaker, before the network settles enough for the meters to show the new operating point — except here "settling" is one simulation solve, and the meters deliberately hold their previous readings rather than showing garbage.

**Project-coined term**, closest standard concept: the interval between a topology change and the next converged operating-point solution; the read behavior is a form of snapshot/eventual consistency.

**Where it appears:** `docs/api.md` §17.4 (reads between mutation and Solve) and decision log #27; load-bearing across the netlist merge path and the coupler `Reconfigure` story (§7).

### merge-window last-good
The read guarantee in force during the merge window: nodes absorbed into a surviving island — and the branch currents and powers of voltage sources on the absorbed side — keep reading their pre-merge published values until the merged island first publishes a solve.

When island A absorbs island B, B's nodes are relabeled into A, but the combined circuit hasn't been solved yet. Without this rule, absorbed-side reads would briefly report zero or stale-labeled nonsense — an in-game ammeter would flicker to 0 A for one tick every time a breaker closed. Instead, the pre-merge published potentials, and the voltage-source auxiliary flows (`Solution.Current`/`Power` on a source, captured per-component at merge commit), are held and served until the first publish of the merged island. The rule is symmetric across both union orientations, and it covers a previously-Faulted side too: the merge flips the union to `Dirty`, so a Faulted side's reads revert from de-energized 0 to its last successfully *published* values (0 if it never published) — failed solves never publish, so no fault output can leak out this way. Think of it as a write-through cache invariant: the visible value is always the last committed one, never a partially applied intermediate. This behavior was fixed on 2026-07-07 after the integration tutorial exposed a violation.

**Project-coined term**, closest standard concept: serving the last converged solution vector across a topology change (a snapshot-consistency read guarantee).

**Where it appears:** `docs/api.md` §17.4 (rule 4, "absorbed side of a merge") and decision log #27.

### Meta / tier-0 facade
The `Netlist.Meta` property: a small grouped API (`MetaFacade`) collecting the operations that never touch the solver's matrix or right-hand side, and are therefore "visibly free."

Manatee grades every mutation by how much solver work it triggers (tier 0 through tier 3). Tier 0 is the floor: `Meta.SetLimits`, `Meta.SetThermalEnvelope`, `Meta.SetProbeInterpolation`, and `Meta.SetDebugName` update attached metadata — limit thresholds, thermal envelopes, probe aim, debug labels — without changing the equations being solved at all. Grouping them behind one property is a legibility device: a code reviewer who is not an EE can grep a client for `.Adjust(`/`.Reconfigure(`/`.Edit(` to find every costly call, and anything routed through `.Meta` is guaranteed cheap by construction. In software terms this is the Facade pattern used as a cost label rather than a complexity hider. For an EE: these calls change what the instrumentation and protection logic *say about* the circuit (what current trips a fuse, where a probe reads), never the circuit itself — like re-labeling a fuse's rating on the panel schedule versus rewiring the panel. Decision log #11 records that `Meta` is the *only* facade: a rejected proposal had one facade per tier, and the verbs stay named methods on `Netlist`.

**Project-coined term**, closest standard concepts: the Facade design pattern (Gamma et al.), applied to what SPICE-family simulators would treat as simulation-inert annotation changes.

**Where it appears:** `docs/api.md` §4 (`MetaFacade` declaration) and decision log #11.

**References:** Gamma, Helm, Johnson & Vlissides, *Design Patterns: Elements of Reusable Object-Oriented Software* (1994) — Facade pattern.

### metadata-only recompute
Recomputing derived attributes of a circuit element — such as its limit thresholds — without touching the solver matrix's structure or values; the cheapest class of change in the system.

The canonical example is an ambient-temperature change: each collapsed cable run carries a limit envelope (ampacity, i²t thermal set, melting threshold) whose thresholds are environment-adjusted per segment — Stationeers spans −150 °C on Europa to +800 °C on Vulcan. When the environment changes, those thresholds must be recalculated, but the resistances and equations being solved are unchanged, so the update dirties only the envelope and never triggers a matrix refactor or rebuild. In programming terms it is invalidating a derived, cached view while the underlying source data stays put — recompute the summary, not the database. For an EE: the wire's electrical behavior hasn't changed, only the conditions under which its protection would trip; the simulation's math is untouched, and only the trip curves are re-derated. This corresponds to `Tier.Metadata` (tier 0) in the API's cost model — `Meta.SetLimits` is documented as an "envelope/ambient recompute" — and an ε-no-op `Adjust` degrades to the same tier.

**Where it appears:** `docs/compaction.md`, Responsibilities #4 (limit attribution); `docs/api.md` §4 (`Tier.Metadata`, `MetaFacade`) and §12.

### min-over-N-sub-runs
- The measurement pattern that makes our zero-allocation performance gates sound: run the step under test N times, take the *minimum* allocation delta across the sub-runs, and assert that minimum is zero.
- The gates read .NET's per-thread allocation counter (`GC.GetAllocatedBytesForCurrentThread`), which can *over-report*: background garbage-collector and JIT activity occasionally retires the thread's partially-used allocation quantum mid-measurement, so a step that truly allocates nothing can show a small phantom delta of a few hundred bytes. Crucially, this noise is strictly additive — it can only inflate the reading, never hide a real allocation. So if even one of N sub-runs measures a delta of exactly 0, the step provably allocates nothing; a single-shot assertion, by contrast, is a flake generator. For the EE reader: it is like measuring a signal contaminated by occasional positive-only interference spikes — the minimum over repeated readings recovers the true floor. Serializing these tests (the `ZeroAlloc` xUnit collection) reduces the noise but is explicitly *not* the correctness mechanism; the min-over-N pattern is. Warm-up runs are also required so first-time JIT and symbolic-factorization allocations land before measurement.
- **Project-coined term**, closest standard concept: min-of-N-repetitions noise rejection in microbenchmarking (the same reason benchmark harnesses report minima for one-sided noise).
- **Where it appears:** `docs/testing-strategy.md` (Benchmarks / zero-alloc gate section, the `TierBudgetGateTests` pattern); every test in the `ZeroAlloc` collection is required to use it.

### mna-telemetry channel
- A planned custom Vintage Story network channel that periodically broadcasts per-island probe values (voltages, currents, temperatures) from the server to clients, so tooltips and HUD readouts can show live electrical numbers.
- The problem it solves: VS block-info tooltips poll the *client's* copy of a block entity, so any value we want displayed must be synced from the server — but syncing via the engine's per-block `MarkDirty()` every tick would spam whole chunk repackets. Instead we follow the mechanical-power mod's precedent (`MechanicalPowerMod.cs:218`, its ~800 ms telemetry broadcast): a dedicated channel pushes a compact batch of probe values per solver island at ~0.5–1 s, with the rate config-scalable. For the EE reader: it is a low-rate telemetry bus multiplexing many slow meter readings, separate from the signal path — the simulation is unaffected whether anyone is watching. The oscilloscope item is the exception: it negotiates a temporary high-rate waveform stream, but only for one probe point at a time.
- **Project-coined term** (it is literally the channel's planned name); closest standard concepts: a publish/subscribe telemetry topic (programming) or a SCADA telemetry link (EE).
- **Where it appears:** `docs/vintage-story.md` §Tooltips and Instruments (R15).

### MNACableNetwork
- A Harmony-substituted subclass of Stationeers' vanilla `CableNetwork` class that carries the mapping from each vanilla cable network to its manatee-core nodes.
- Stationeers' own power code hands a `CableNetwork` object to each phase of its per-tick power API; by substituting our subclass at network-creation time (via the Harmony runtime-patching library, which rewrites the game's compiled code without modifying its files), that same parameter now also answers the integration's central lookup question: "which solver nodes do I stamp for this network?" The extra data rides along on the object the game already passes everywhere, so no side-table keyed by network identity is needed. For the EE reader: it is like replacing a connector block with a pin-compatible one that has extra labeled terminals — everything vanilla plugs in unchanged, but our wiring now has somewhere to land. The design's removability requirement holds: all device state keeps flowing through the vanilla calls, so uninstalling Manatee/Re-Volt leaves vanilla saves intact and vanilla mechanics resume.
- **Project-coined term** (a class name); closest standard concept: subclass substitution — a pin-compatible subclass that extends the base class with extra state — applied via runtime patching.
- **Where it appears:** `docs/stationeers.md` §Integration Seams; `docs/design.md` Stationeers-integration summary (R18 adaptor bullet).
- **References:** Gamma, Helm, Johnson & Vlissides, *Design Patterns* (subclassing to extend behavior/state).

### Native manatee model / native conversion
A game device that has been reimplemented as a first-class solver component — with its own real MNA stamps and a per-tick `Tick()` update — instead of being approximated through the Legacy-Device Adaptor.

In the Stationeers/Re-Volt integration, unconverted vanilla devices are handled by the adaptor (R18): the solver sees them only as a linearized constant-power blob derived from the game's 4-call power API. A *native conversion* replaces that treatment for one device type at a time: the device gets an honest electrical model (its own resistor/source/switch stamps and any internal state advanced in `Tick()`), so its terminal behavior comes from the physics rather than from a power-number translation. For a programmer: it is like migrating one endpoint at a time from a compatibility shim onto the real API — both populations coexist indefinitely behind the same interface, and the long tail (lights and doors) may stay adapted forever. Conversion priority goes to devices whose electrical behavior is load-bearing for gameplay: batteries, transformers, breakers.

**Project-coined term**, closest standard concept: a full SPICE device model (built-in stamped model) as opposed to a behavioral/black-box approximation.

**Where it appears:** `docs/stationeers.md` (Legacy-Device Adaptor section); `docs/design.md` R18.

### Netlist (as document)
- The design stance that the `Netlist` object is a retained, authoritative description of the circuit, and the solver's matrices are merely derived from it — never the other way around.

Every mutation verb (Drive, Adjust, Reconfigure, Edit) writes the document; at `Solve`, matrices are (re)derived from it. This is what makes writes durable across structural churn: a verb call is never lost even if its island is about to be rebuilt, because the rebuild restamps from the document (api.md §4, carrying the §17 merge-window story). For a programmer this is the retained-mode pattern — like a DOM or scene graph from which render state is compiled, versus immediate-mode drawing. For an EE: the netlist stays the master copy of the circuit; the MNA matrix is a disposable compiled artifact, like a fresh SPICE deck parse, except kept incrementally in sync.

**Project-coined term**, closest standard concept: retained-mode scene/model (vs. immediate mode); also the general derived-state/single-source-of-truth pattern.

**Where it appears:** docs/api.md §4 ("`Netlist` is the retained-mode circuit **document**") and §17 (merge-window semantics).

### NetlistOptions
- The one-shot configuration bundle passed to the `Netlist` constructor; everything in it is fixed at birth and cannot be changed afterwards.

It carries the solver `Profile` (Dc | Transient | Mixed — which analyses run and at what tick step), the `Wiring` policy (a `WiringPolicy`: how return terminals and grounding are handled per client), the `Partitioning` mode (a `PartitioningMode`: SelfPartitioned vs ClientPartitioned), the `AdjustEpsilon` threshold below which an Adjust degrades to a free document write, arena presize hints (`ExpectedIslands`/`ExpectedNodes` — hints, not caps), `JournalCapacity`, and `Debug` (a `DebugLevel`). Three named presets — `Stationeers()`, `VintageStory()`, `Tablet()` — encode each client's correct combination so integrators do not assemble it by hand; api.md calls these "the misuse-resistant defaults". For an EE: this is like choosing the analysis type and simulator options at the top of a SPICE deck, except deliberately locked once the circuit object exists — a birth-time invariant, so no code path has to handle the regime changing mid-flight.

**Project-coined term**, closest standard concept: simulator/analysis options (SPICE `.options` and analysis cards), realized as the options-object constructor pattern.

**Where it appears:** docs/api.md §5 (definition and presets), §2 (public surface), §14 (`FromCanonical` takes one); integration-tutorial.md setup steps.

### NetlistOptions.Stationeers(dt)
- A factory method that builds the ready-made configuration bundle for a Stationeers/Re-Volt netlist, pinning three birth-time decisions in one call.

A `Netlist` is configured exactly once, when it is constructed, and `NetlistOptions.Stationeers(dt)` is the named preset for the Stationeers integration (default `dt = 0.5` s, the game's power-tick period). It pins: **Profile = Dc(dt)** — pure DC solving with storage dynamics, no AC subcycling; **Wiring = ReferenceBound** — every device's return terminal is automatically bound to its partition's reference rail, so single-terminal vanilla devices need no per-device grounding code; and **Partitioning = ClientPartitioned** — every node carries a `PartitionKey` (the CableNetwork id) and the API enforces that partitions never merge except through a coupler, throwing `PartitionMergeException` and rolling the whole edit batch back otherwise. For programmers: it is a named preset object, like a config profile, that turns integration folklore into defaults you cannot get wrong. For an EE: it fixes the analysis type (DC transient with timestep dt), the grounding convention, and the rule that separate cable networks stay galvanically separate unless bridged by a designated coupling device.

**Project-coined term**, closest standard concepts: a factory method returning a configuration preset (Gamma et al.'s Factory Method pattern); the electrical choices it pins correspond to SPICE's `.TRAN` analysis setup and a fixed grounding convention.

**Where it appears:** `docs/api.md` §10 (`NetlistOptions` definition) and §22.a walkthrough; `docs/integration-tutorial.md` §2, which narrates each pinned decision.

**References:** Gamma, Helm, Johnson & Vlissides, *Design Patterns* (1994), for the factory/preset idiom.

### NetworkExport / CableInfo
- Sukasa's concrete export format that manatee's Stationeers intake consumes: per-cable adjacency records (`CableInfo`) plus device and fuse lists.

`CableInfo { RefId, List<int> Connections }` describes the network at **cable-segment granularity**: each record is one physical cable piece, identified by the game's `RefId`, listing which other cables it touches. The full export adds device and fuse lists, and three additions already agreed with Sukasa (Re-Volt's author): device **port identity** per connection, **per-cable type/gauge** (prefab name, which sets resistance per length), and **fuse positions in the adjacency** — a fuse is an edge with a rating, not a side-list entry. Downstream, segments become resistor edges, junctions become nodes, and the shared reduction layer collapses series chains (~10k segments → low-hundreds of nodes). For programmers: it is an adjacency list keyed by opaque game ids, i.e. the graph in its rawest serialized form. For an EE: it is a wire-level connectivity extraction where each record is a length of physical cable, before any equivalent-circuit reduction.

**Project/partner-coined format** (owned by Re-Volt); closest standard concept: an adjacency-list graph serialization feeding netlist extraction.

**Where it appears:** `docs/stationeers.md` "Graph Construction"; consumed via the intake normalizer per `docs/api.md` §19.

### ε-no-op (AdjustEpsilon)
- An `Adjust` call (a resistor-value change) whose new value is so close to the old one that the solver skips the expensive numeric work entirely, degrading to a free tier-0 bookkeeping write.
- Normally, changing a component value costs tier 2: the cached LU factorization is discarded and recomputed. But adaptor loops that regulate a device by repeatedly setting G = P/V² keep calling `Adjust` every tick even after they have converged, with each new value differing from the last by a rounding whisker. The ε-no-op rule catches this: an `Adjust` is a no-op iff old and new values map to finite, nonzero, same-sign conductances and the ratio `G_new/G_old` lies within pinned bounds `[RatioLo, RatioHi]` (semantically `exp(∓ε)`, default ε = 1e-4); conductances at or below gmin compare equal, and switch toggles are no-ops only on exact state equality. Think of it as a write-through cache with a tolerance check: writes inside the tolerance update the document but leave the factorization untouched. For the default ε the bounds are stored as exact double literals (`DefaultRatioLo/Hi`, the values of `exp(∓1e-4)`); a custom `AdjustEpsilon` derives its bounds at runtime via the deterministic polynomial `1 ∓ ε + ε²/2` — either way **no transcendental function (Math.Exp/Math.Log) is ever evaluated in the gate** — so classification is bit-identical on every CPU architecture, which the cross-platform tier-budget goldens depend on. Skipped Adjusts are counted in `TickStats.AdjustNoOps`, and `CostOfAdjust` reports `Metadata(0)` for them.
- **Project-coined term**, closest standard concepts: relative-tolerance change detection / hysteresis deadband; related in spirit to SPICE's "bypass" of device re-evaluation when values are unchanged within tolerance.
- **Where it appears:** `docs/api.md` §4 (`Adjust`, `CostOfAdjust`), §5 (`NetlistOptions.AdjustEpsilon`, default 1e-4), §9 (pinned definition), TickStats `AdjustNoOps`; `docs/solver.md` change-cost tiers.

### node potential / flow (domain-neutral aliases)
- Deliberately game-neutral, non-electrical names used in manatee-core's deepest layers for what EEs call voltage and current: the solver layer (`Manatee.Core.Solver`) names its solution quantities **Potential** (per node) and **Flow** (per branch), and the public API exposes `Potential(NodeId)` as an alias of `Voltage(NodeId)`.

The point is reuse: an MNA solver does not care what physical domain it is solving, and thermal-RC networks map exactly onto it (temperature ≡ voltage/potential, heat flow ≡ current/flow, thermal resistance ≡ resistance, heat capacity ≡ capacitance), a possible future arc à la Stationeers' atmospherics. To keep that door open cheaply, the innermost layers name their quantities "node potential" and "flow" instead of "voltage" and "current"; the client-facing electrical vocabulary lives in the outer layers where games talk to the netlist. Only the naming is generalized today — the thermal arc itself is TBD, noted not designed. For an EE this is the standard across-variable / through-variable (effort/flow) abstraction from network theory — the same equations describe electrical, thermal, and hydraulic networks. For a programmer it is generic naming for interface reuse: the engine speaks in domain-neutral types, and each application binds its own units on top.

**Standard terms:** voltage / current (electrical domain); the generalization is the across-variable / through-variable pair of generalized network analysis. Note our "flow" is what that literature calls the through variable, and physics texts already use "potential" for voltage.

- **Where it appears:** `docs/solver.md` §2 (layer naming rule) and Layering; `docs/api.md` (Solver namespace "Names are Potential/Flow"; `Potential(NodeId)` alias, §10); `docs/design.md` (Simulation Model; thermal-RC reuse rationale / future chemical/thermal arc note).
- **References:** Ho, Ruehli & Brennan (1975), "The Modified Nodal Approach to Network Analysis", IEEE Trans. Circuits and Systems (the domain-agnostic formulation we implement); Karnopp, Margolis & Rosenberg, *System Dynamics: Modeling and Simulation of Mechatronic Systems* (effort/flow variables across physical domains).

### NodeRole (Internal / Return / Reference)
- A per-node tag, set when the node is created, that tells the wiring policy how to treat the node — most nodes are `Internal`, `Return` marks a negative/return-side node, and `Reference` marks the island's ground datum.

`NodeRole` is a three-value enum (`Internal`, `Return`, `Reference`) passed to `AddNode` (default `Internal`). It exists so grounding is declarative rather than procedural: instead of clients calling a per-device "connect this to ground" function, they tag nodes once, and the active `WiringPolicy` acts on the tags at commit time. `Return`-role nodes get the policy's automatic binding — under `ReferenceBound` (Stationeers) a near-ideal conductance to the partition's reference rail, under `TwoWireLeak` (Vintage Story) an auto-stamped ~1 MΩ leak resistor to the island reference; under `ExplicitOnly` (tablet) tags cause no implicit stamps. `Reference` marks the datum node (conventionally SPICE's node 0) — the zero-volt point every other node voltage is measured against; its row and column are eliminated from the MNA matrix rather than solved for. For an EE: the roles encode which side of a device is the return conductor and where ground is, as data instead of wiring. For a programmer: it is metadata driving a policy object — tag the graph, let one strategy apply the rule uniformly; debug builds warn when a mistagged floating subgraph results.

**Project-coined term**, closest standard concepts: the `Reference` role is the standard SPICE ground/reference node (node 0); `Return` corresponds to the return/negative conductor of a supply; there is no standard per-node role enum in SPICE-family netlists.

- **Where it appears:** `docs/api.md` §5 (WiringPolicy — where the policy fires) and §6 (`AddNode` signature); `docs/integration-tutorial.md` §2.

### NodeRole.Return / TerminalSpec.ReturnTerminals
- The two ways to mark something as the "negative side" — a tag on a hand-made node (`NodeRole.Return`) or a declaration on a device's terminal list (`TerminalSpec.ReturnTerminals`) — which the `ReferenceBound` wiring policy uses to ground it automatically.

Both are metadata, not actions: nothing happens at the tagging site itself. At commit, the active wiring policy scans for return markings and applies its grounding rule uniformly. The policy fires in exactly three places — the `neg` argument of `AddVoltageSource`/`AddSineSource`, every node created with `NodeRole.Return`, and every terminal index a `Device` lists in its `TerminalSpec.ReturnTerminals` span — and under `ReferenceBound` each such point is bound to its partition's reference node through a near-ideal return conductance (default 1 kS), which is numerically identical to modeling the return conductor without paying for a full double-wire solve. There is deliberately no imperative `BindNegative` call: for a programmer, this is declarative configuration over a mutation API (tag once, one policy enforces the invariant everywhere); for an EE, it is declaring which terminals sit on the return rail so the chassis/ground bond is stamped for you instead of drawn by hand.

**Project-coined API surface**, closest standard concept: the implicit ground-return connection of single-wire (chassis-return) distribution; in SPICE terms, an automatic conductance from each marked terminal to node 0.

- **Where it appears:** `docs/integration-tutorial.md` §2 (Wiring = ReferenceBound); `docs/api.md` §5 (where the policy fires), §18 (`TerminalSpec.ReturnTerminals`), and the §16 sharp-edges list (mistagged-Return footgun).

### Nonlinearity budget
- The project's design stance on where iterative (Newton) solving is allowed to spend time: it exists, but is kept out of the hottest code path.

Newton iteration is implemented because the tablet curriculum needs it (diodes), but each iteration is a tier-2 re-stamp and re-factorization — expensive relative to the tier-1 back-substitution a linear island enjoys. The budget rule: in world islands (real in-game power networks, especially subcycled AC ones running many substeps per tick), nonlinear solves are avoided wherever a behavioral model serves — converter-style devices ship as behavioral two-ports that update their parameters between ticks rather than iterating inside the solve. For an EE: this is choosing a quasi-static (operating-point) linearization refreshed each tick over full nonlinear DC iteration in performance-critical networks — the physics fidelity is deliberately traded for keeping hot islands linear. For a programmer: it's a performance budget, like "no allocation on the hot path" — expensive machinery is fenced to cold paths (the educational tablet) where its cost is affordable.

**Project-coined term**, closest standard concept: restricting where full Newton-Raphson nonlinear analysis runs, substituting behavioral/macromodels in its place.

**Where it appears:** `docs/design.md` — Simulation Model ("Nonlinearity budget" bullet); enforced by policy in `docs/solver.md` — Analyses, "Nonlinear elements".

### one-global-tick-body execution model
- The as-built finding (api.md §22.a) that in the Stationeers/Re-Volt integration, all of Manatee's work for a tick runs as one global body — a single edit/re-pin/drive/`Solve` pass covering every island — rather than being hosted separately inside each cable network's own tick phases.
- The original stationeers.md design mapped Manatee's phases onto Re-Volt's per-`CableNetwork` three-phase contract (`Initialise` → `CalculateState` → `ApplyState`), i.e. one small solve hosted inside each network's tick. The 2026-07-06 core build superseded this: `Netlist.Solve` is one call that advances *all* islands at once, so it cannot be sliced up and hosted per network. Instead the Manatee body is hoisted ahead of the game's per-network loop, and each network's `ApplyState` merely distributes the already-computed results for its own partition. The EE analogy: instead of each subcircuit being measured by its own instrument on its own schedule, the whole board is solved once per clock and each subcircuit just reads its assigned outputs. Where the older per-network text in stationeers.md disagrees, §22.a is binding until that doc's Sukasa-signed rewrite lands.
- **Project-coined term**, closest standard concept: a single global simulation step (batch solve) versus per-component callback hosting.
- **Where it appears:** stationeers.md revision-pending header note; api.md §22.a (the Re-Volt tick-loop walkthrough) and §23 item 1 (pending Sukasa sign-off).

### one work unit / scheduling unit (lockstep)
- The atomic unit of solve scheduling: either a single island, or a group of islands joined by a boundary coupling (transformer, DC converter, closed breaker), which must be solved together in lockstep as one indivisible piece of work on one thread.
- Islands are normally independent solves that may run on separate worker threads. But islands connected through a coupling device must exchange power values *every substep*, and letting two free-running threads exchange mid-solve would make the result depend on thread timing. So coupled islands are fused into **one work unit**: within it, all member islands take substep 1, exchange, take substep 2, exchange, and so on — the exchange happens inside the unit, never across threads. This preserves both per-substep energy exchange (physics) and per-island determinism (same inputs → bit-identical outputs, regardless of how the OS schedules threads). Only fully independent islands are parallelized across workers. An EE analogy: two magnetically coupled circuits cannot be analyzed on independent clocks — they share state each instant — whereas galvanically isolated circuits can; it is co-simulation, where subcircuits linked through a defined interface must advance on a common time grid.
- The scheduling API is expressed in these units: `CollectDirty` returns exactly one `IslandHandle` per dirty scheduling unit (coupled islands dedupe to a single handle), `Step` on that handle solves the whole unit, and `TickStats` accumulates per unit.
- **Project-coined term**, closest standard concepts: task granularity in fork/join parallel scheduling — the coupled islands form one strongly-interacting subproblem (a strongly connected component / the transitive closure of the "must run together" relation over the coupler graph) that cannot be topologically parallelized; a co-simulation group with lockstep synchronization.
- **Where it appears:** `docs/solver.md` Islands ("Scheduling and conservation", settled 2026-07-05) and the Threading and Allocation Contract; `docs/api.md` §9 (couplers/lockstep), §11 (`CollectDirty`, `Step`), §22.b and the tick-loop example; echoed in `docs/stationeers.md` Islands and Coupling Devices.

### OpenEnds
- A per-cable list of unconnected connection points that the Stationeers game engine keeps on each cable piece, which Manatee's intake pipeline mines for wiring detail before the game throws it away.

In vanilla Stationeers, each cable segment records where its ends are and which of them are still open; the game's network builder consumes this endpoint data when it merges cables into a single `CableNetwork`, then discards it — the finished network no longer remembers which segment touched which. Manatee needs the finer picture: its solver models each cable segment as a resistor and each junction as a circuit node, so the export format (`NetworkExport` / `CableInfo`) must recover segment-level adjacency from the per-cable OpenEnds *before* the builder erases it. For a programmer, this is a classic "capture the intermediate representation before the lossy compile step"; for an EE, it is the difference between having the full wiring diagram and having only the statement "these 10,000 cables are one net." One caveat recorded in the Re-Volt audit: tray-mediated edges are computed at rebuild time and are *not* persisted as OpenEnds, so the export must add them explicitly.

**Project-context term** (a field name from the Stationeers game code, not an electrical or CS term of art); closest standard concept: unbound terminals / dangling edge endpoints in a graph before contraction.
- **Where it appears:** `docs/stationeers.md` (Graph Construction), `docs/api.md` §19 intake-normalizer note, `docs/reviews/2026-07-04-revolt-audit.md` (intake-pipeline findings).

### OQ1–OQ4 (open questions)
- The four design questions left unresolved by the API-competition synthesis, each of which `docs/api.md` answers in a dedicated section.

Manatee's public API was chosen by running a competition between rival proposals and synthesizing a winner (`docs/experiments/api-competition/`); the synthesis document ended with four questions no proposal had settled (its "Open questions carried forward", numbered plainly 1–4), which `docs/api.md` tracks under the labels OQ1–OQ4. The as-built API resolves each one: **OQ1** — which handles stay valid across an island rebuild (the handle-survival table, §16); **OQ2** — how to enforce "no structural changes and no heap allocation during the hot loop" (SteadyStateGuard and AllocationSentinel, §8); **OQ3** — how mid-tick reconfiguration relates to solving (the Reconfigure-to-Solve story: structure is transactional and immediate, matrices are coalesced and lazy, §17); **OQ4** — how solver options and grounding conventions are set without per-device boilerplate (options, profiles, wiring policy, §5). For an EE, think of them as the four contested clauses in a standards committee's draft, each closed with a recorded ruling; unresolved leftovers live separately in §23 Open items.

**Project-coined term** (the "OQ" numbering is api.md's convention for the synthesis's questions 1–4); closest standard concept: tracked open issues / RFC unresolved questions in a design document.
- **Where it appears:** `docs/api.md` intro and section headings §5, §8, §16, §17; originally `docs/experiments/api-competition/` synthesis.

### oracle / oracle note / Oracle test / ngspice oracle
- The project's primary correctness mechanism and the doc annotation that flags where it is owed: an **oracle test** judges manatee-core's output by comparing it against a trusted external simulator (ngspice) rather than against a hand-derived expected answer, and an **oracle note** marks specific claims in a document that must be pinned by such a cross-check rather than taken on the document's word.

This is chosen because nobody on the team can independently verify circuit math by hand: the user is a programmer, not an EE, and Claude's derivations are explicitly treated as untrusted input ("trust nothing derived"; the working agreement is "the math is untrusted input"). Instead of asserting "this RC circuit should read 2.7 V because I did the algebra," an oracle test solves the same circuit in both manatee-core and ngspice — the open-source descendant of Berkeley SPICE — and requires the answers to agree within tolerance; the strategy mandates that every stamp, companion model, or convergence trick be pinned by at least one oracle test before it ships. For an EE, ngspice plays a calibrated reference instrument on the bench; for a programmer, this is differential testing against a reference implementation.

**The concrete ngspice mechanism.** A test-only translator (`SpiceDeck`) emits a SPICE deck from one island of a manatee netlist, ngspice solves it, and the two simulators' node voltages and branch currents are diffed within tolerance: DC circuits go through ngspice's `.op` analysis (tolerance 0.1% relative, or 1 µV absolute near zero) and transients through `.tran`, compared at matched timesteps with looser tolerance — manatee's Backward Euler and ngspice's default integrator legitimately differ, so where exactness matters the tests force matching integrator settings (`method=gear maxord=1`) or compare only settled values. Two policy points are load-bearing. First, tests tagged `Category=Oracle` **hard-fail** (not skip) when ngspice is missing, because a silently skipped oracle is the one failure mode this strategy cannot afford; the pinned Nix devshell guarantees CI and local runs use the same ngspice binary, and the fast inner loop excludes them via `dotnet test --filter 'Category!=Oracle'`. Second, ngspice is a dev/CI dependency only — it never ships in either game. What ngspice cannot express (behavioral two-ports, limit events) is covered instead by conservation invariants and hand-computed cases documented with their arithmetic inline.

**The oracle note (annotation).** An *oracle note* is a `TODO(verify-against-oracle)` with teeth: it names the exact facts in a document that most deserve a cross-check. The one in `docs/falstad-format.md` §7 names two — voltage-source polarity (post 2 = +) and current-source direction (positive `currentValue` drives current from post 1 to post 2 through the source), both read out of circuitjs1 source (`CurrentElm.java:107-110`) — because sign conventions are precisely the kind of fact where a plausible-sounding derivation can be silently backwards; the note says write a golden test that checks these against the real simulators before relying on them.

**Standard term:** differential testing / testing against a reference oracle. "Test oracle" in the software-testing literature means any source of expected results; our usage narrows it to "a mature external simulator," and the ngspice deck-diff setup is our specific instantiation.

- **Where it appears:** `docs/testing-strategy.md` (Principles, "Oracle Tests (ngspice)" and Toolchain); `docs/design.md` Testing and Validation, R20; `CLAUDE.md` working agreements (test filter, devshell); `docs/falstad-format.md` §7 (the "Oracle note"); test code under `Oracle/`, tagged `Category=Oracle`.
- **References:** W. M. McKeeman, "Differential Testing for Software", Digital Technical Journal 10(1), 1998; E. J. Weyuker, "On Testing Non-testable Programs", The Computer Journal 25(4), 1982 (the oracle problem); L. W. Nagel, "SPICE2: A Computer Program to Simulate Semiconductor Circuits", UCB/ERL-M520, 1975 (the lineage ngspice implements); the ngspice manual (ngspice.sourceforge.io) for `.op`/`.tran` and integrator options.

### Oracle pass / narrative pass
- The two automated checks that CI runs over every lesson in the tutorial corpus: the *oracle pass* confirms manatee agrees with ngspice, and the *narrative pass* confirms the lesson's stated numbers are still true.

Each lesson directory pairs a circuit netlist with a Markdown narrative containing concrete expected observations (e.g. "about 2.7 V after 4.5 s"). CI consumes each lesson twice. The **oracle pass** runs the lesson's netlist through both manatee-core and ngspice (a mature reference simulator) and requires the results to agree within tolerance — this catches solver bugs. The **narrative pass** checks a machine-readable front-matter block (probe, time, expected value, tolerance) against the manatee solution — this catches lessons whose prose has drifted from what the simulator actually produces. The design intent is design.md's R20 slogan "a lesson that stops being true fails the build": the teaching material is itself a regression test suite. For an EE, think of it as every worked example in a textbook being re-derived on every print run; for a programmer, it is golden-file testing where the goldens are the curriculum.

**Project-coined term**, closest standard concepts: differential testing against a reference implementation (oracle pass) and golden/snapshot testing with executable documentation (narrative pass).

- **Where it appears:** `docs/testing-strategy.md` "The Lesson Corpus as Goldens"; `docs/design.md` R20; the narrative-pass API surface in `docs/api.md`.
- **References:** W. M. McKeeman, "Differential Testing for Software", Digital Technical Journal 10(1), 1998 (the oracle-pass idea); D. Knuth, "Literate Programming", The Computer Journal, 1984 (docs-as-executable-artifacts is the same spirit as the narrative pass).

### Oscilloscope / scope
- In project usage, "the scope" is the waveform-viewing instrument as a *client of the solver*: the component whose data needs shape the probe API and the telemetry design.

Two integration details define it. **Probe subscription:** a named probe can subscribe to per-substep sampling into a ring buffer, and the expected UI contract is at most two simultaneous probes — enough for phase comparison between two traces (the tablet scope is the primary consumer of this contract, driving lessons 15 and 17), with cost bounded by construction. Probes survive the compaction layer via registered interpolators, so the scope can read "inside" a reduced series run. **Telemetry negotiation (Vintage Story):** routine instrument readouts ride a throttled ~0.5–1 s per-island broadcast channel, but that cadence cannot draw a waveform, so the oscilloscope item negotiates a *temporary high-rate waveform stream for one probe point only* — an explicit exception to the throttling, scoped to one point so it cannot become a bandwidth hazard. For an EE: this mirrors a real scope's finite channel count and the bandwidth cost of deep capture. For a programmer: it is a bounded pub/sub subscription with a per-subscriber ring buffer, plus a QoS upgrade path in the network layer.

The instrument name is standard ("scope" is the universal EE colloquialism); the two-probe cap and the one-point high-rate stream negotiation are project design decisions, not standard terminology.

- **Where it appears:** `docs/solver.md` Probes (ring buffer, two-probe contract); `docs/vintage-story.md` telemetry gotcha and "The Tablet Host" (Instruments); `docs/design.md` R15.
- **References:** Horowitz & Hill, "The Art of Electronics", 3rd ed., Cambridge University Press, 2015 (oscilloscope practice).

### Output / probe marker (`O`)
- The Falstad text-format element that marks a single node whose voltage should be displayed — Manatee imports it as a probe marker, a named measurement point with no electrical effect.

In the Falstad/circuitjs1 format, `O` is a one-post element (`OutputElm`): it attaches to a single node and shows that node's voltage on screen, with an optional display `scale` parameter. Electrically it is invisible — like an ideal voltmeter to ground, it draws no current and changes nothing about the solve. Manatee's importer maps both `O` and `p` (upstream's voltmeter element) to probe markers used by the tablet UI and by the golden-test corpus, ignoring the meter/scale/resistance display parameters since they affect only presentation. For a programmer: a probe is a logging statement or watchpoint — observability attached to the data, not part of the computation. One trap documented in the format spec: codes are case-sensitive, and lowercase `o` is an entirely different thing (a scope-configuration line, not an element); Electrical Age's parser lowercased codes and conflated them, a bug Manatee deliberately does not copy. Our own lesson files use `O` as the standard way to declare probes.

**Standard term:** Falstad/circuitjs1 `OutputElm`; the general concept is a voltage probe (as in SPICE's `.PRINT V(node)` outputs).

**Where it appears:** `docs/falstad-format.md` §4 (element table), §7 (importer accept list and corpus rule); `docs/curriculum.md` lesson authoring rules.

### Over-rev / overheat ceiling
- Vintage Story's built-in mechanical hazard: any shaft network spinning faster than node speed 4.5 starts to overheat and take damage, putting a hard cap on rotation speed.

The check lives in the base game (`MechanicalPowerMod.cs:168-189`), not in Manatee — the engine damages over-speeded mechanical networks, exactly like the redline on an engine or the maximum RPM rating on a real machine. Manatee leans on it twice. First, it is a natural over-rev hazard for electrically driven motors: a motor commanded too fast destroys its own drivetrain, no scripted failure needed. Second, and by design, it closes off the cheap route to mains frequency: since electrical frequency = shaft speed × pole pairs (about 0.8 × speed × polePairs Hz given the engine's ω = 5·speed integration), and speed is capped below 4.5, players cannot reach 50 Hz by spinning faster. The sanctioned path is high-pole-count alternators — hand-smithed pole pieces, an advanced craft — mirroring how real low-RPM hydro alternators achieve grid frequency. For a programmer: the ceiling is an engine-enforced invariant we treat as a fixed constraint and design the progression around, like an API rate limit that shapes your architecture.

**Project usage note:** "over-rev" (over-revving past redline) and "overheat" are both ordinary mechanical-engineering terms; the specific 4.5 threshold is a Vintage Story engine fact.

**Where it appears:** `docs/vintage-story.md`, Mechanical Network Coupling (cadence consequence); `docs/design.md`, Voltage and frequency standards.

### partition / PartitionKey
A partition is a client-assigned grouping that every node in the circuit belongs to — in Stationeers, it is simply the game's CableNetwork id, handed to manatee-core as a `PartitionKey`.

Under `ClientPartitioned` mode the partition is not advisory metadata: it is an enforced boundary. The game already knows which cable network each device sits on, so instead of letting the solver rediscover that grouping, the client stamps it on every node, and the core *enforces* the game's rule that separate networks never electrically merge except through a coupler (a deliberate bridge device such as a transformer). The `partition` argument is mandatory on every `ConductorGraph.AddSegment` — the default value throws — and any `Edit` that would bridge two partitions with an ordinary component throws `PartitionMergeException` with the whole batch rolled back. For a programmer, this is a foreign key with a referential-integrity constraint; for an EE, it is a declaration that two nets are galvanically isolated, checked by the tool rather than by discipline.

**Project-coined term**, closest standard concept: network / subnet id (a client-declared net grouping; related to, but stricter than, the connected components a simulator would find on its own).

**Where it appears:** `docs/integration-tutorial.md` §2–3 and appendix item 12; `docs/api.md` §3 (`PartitionKey` type), §5 (`NetlistOptions`), §11 (couplers), §19 (`ConductorGraph.AddSegment`).

### PartitioningMode (SelfPartitioned / ClientPartitioned)
A once-at-construction `Netlist` option choosing who decides how the circuit divides into independent networks: the solver core itself, or the game client.

Under `SelfPartitioned` (Vintage Story and the tablet), the core discovers islands — connected components — on its own by graph traversal, exactly as a circuit simulator normally would; nodes carry no partition id (`PartitionKey.None`). Under `ClientPartitioned` (Stationeers), the client supplies a `PartitionKey` for every node, and the core enforces that these partitions never merge except through a coupler: a commit that would bridge two partitions with an ordinary component throws `PartitionMergeException` and rolls back atomically. Think of it as choosing between automatic garbage-collected grouping and manually declared ownership with a type-checker enforcing the declarations — or, in EE terms, between letting the netlister find the nets and asserting up front which nets are isolated from which. The named option bundles pick the mode for you: `NetlistOptions.Stationeers` is `ClientPartitioned`; `VintageStory` and `Tablet` are `SelfPartitioned`.

**Project-coined term**, closest standard concept: connected-component (net) discovery vs. a client-declared subnet/network id with an enforced isolation invariant.

**Where it appears:** `docs/api.md` §5 (`NetlistOptions.Partitioning`, the `PartitioningMode` enum), §11 (couplers as the only cross-partition join, `PartitionMergeException`), §19 (`ConductorGraph` intake); `docs/integration-tutorial.md` §2.

### PartitionKey
The small value type (`readonly record struct PartitionKey(ulong Value)`) that names a client partition — in practice, a Stationeers CableNetwork id — under `ClientPartitioned` mode.

It is a typed wrapper around a 64-bit integer: wrapping the raw id in a distinct type means the compiler stops you from passing an arbitrary number where a partition is expected, the same way labeling a wire prevents probing the wrong net. The default value, `PartitionKey.None`, is a *reserved sentinel*, not a real partition: it is what `SelfPartitioned` nodes carry (where partitions do not exist), and what `IslandHandle.Partition` reports for an island that legitimately spans more than one partition through a closed coupler. Because `None` is reserved, passing the default `PartitionKey` to `AddSegment` under `ClientPartitioned` throws at add time — a deliberate make-the-mistake-loud choice. Per-partition addressing (e.g. the reference rail via `ConductorGraph.ReferenceNode(p)`) always takes a real key and is unaffected by coupler state.

**Project-coined term**, closest standard concept: a network/subnet identifier, with `None` as a null-object sentinel.

**Where it appears:** `docs/api.md` §3 (type definition and sentinel), §11 (`IslandHandle.Partition`), §19 (`AddSegment`, `ReferenceNode`); `docs/integration-tutorial.md` §2–3.

### PartitionMergeException
The exception manatee-core throws when an edit would electrically join two different client partitions in any way other than through a coupler.

Under `ClientPartitioned` mode, "vanilla networks never merge" is a hard contract, not a convention — but it is enforced at two different surfaces depending on which API you drove. On the raw four-verb path, stamping an ordinary component (a resistor, source — anything but a coupler) between nodes of two different `PartitionKey`s throws `PartitionMergeException` from `Netlist.Commit()`, and the *entire* edit batch rolls back atomically. On the reduction layer, the violations are geometric — a cable segment bridging two partitions, or the same junction claimed by two partitions — and `ConductorGraph` has no client-called `Commit()`: the check throws eagerly from `ConductorGraph.AddSegment`, or, during bulk load, at `BulkBuild.Dispose` (load time, explicitly never at first `Solve`). In every case nothing was applied — the caller fixes the edit and retries rather than repairing half-applied state. For an EE, this is the tool refusing a netlist that shorts two nets you declared galvanically isolated; for a programmer, it is a database transaction aborting on a constraint violation instead of committing corrupt data. It is classified as contract misuse (a caller bug), not a runtime fault like `Faulted`.

**Where it appears:** `docs/integration-tutorial.md` §2 and appendix item 12; `docs/api.md` §11 (atomic rollback rule), §19 (`ConductorGraph.AddSegment` / `BulkBuild` checks), and the §20 failure-handling table (raw-edit merge throws from `Commit()`, whole batch aborts).

### per-chemistry parameter sets
- The design rule that all battery types share one behavioral model, and a battery's chemistry (voltaic pile, lead-acid, li-ion) is expressed purely as a bundle of parameter values plugged into that model.

The solver's battery component is always the same structure: an EMF source plus internal resistance plus a state-of-charge integrator. Choosing a chemistry selects numbers — how strong the EMF is, how large the internal resistance is (the pile sags badly under load; lead-acid much less), how much capacity it stores — never a different circuit topology or code path. For a programmer this is one class with three constructor configs rather than three subclasses; for an EE it is a fixed Thevenin-plus-coulomb-counting equivalent model with swappable datasheet values. The payoff is stated explicitly in the docs: a future "chemistry arc" of gameplay can deepen the *parameters* (aging, temperature effects) without changing the *structure* the solver stamps. Game-side, the chemistries map to progression: consumable-zinc piles first, 1859-authentic lead-acid accumulators later, li-ion only as rare ruins loot with a taught hazard policy.

**Project-coined term**, closest standard concept: a battery *equivalent-circuit model* (Thevenin/Rint model) parameterized per cell chemistry — standard practice in battery management literature.
- **Where it appears:** `docs/solver.md` (Component Set, Batteries bullet); `docs/design.md` (Arc 1/Arc 2 progression, Ruins loot li-ion policy, deferred chemistry arc); `docs/api.md` §"Battery".
- **References:** Horowitz & Hill, *The Art of Electronics* (battery internal resistance and Thevenin equivalents); Plett, *Battery Management Systems, Vol. I: Battery Modeling* (equivalent-circuit cell models).

### PortNode
The reduction-layer lookup that answers "at which live solver node does a device attach here?" for a given junction key.

`ConductorGraph.PortNode(JunctionKey)` maps a client-stable junction identity to the current `NodeId` in the compacted netlist. It exists because compaction rewrites the graph underneath you — 12 cable segments with two taps may collapse to 3 equivalent resistors — so the raw junction is not guaranteed to be a node anymore; `PortNode` guarantees it is, protecting the junction from being collapsed away and returning the node that survives. One sharp edge is load-bearing: resolving a not-yet-protected junction can trigger a recompaction, which internally opens its own `StructuralEdit` — doing that inside *your* open Edit throws a nested-edit `InvalidOperationException`, and each new protection can invalidate node ids you resolved a moment earlier. Hence the protect-then-resolve discipline: call `PortNode` for all attachment points first, resolve them all again, and only then open the Edit. For the EE: it is like asking the panel schedule for the physical terminal block your device lands on after the electricians have consolidated the wiring — ask before you start work, because asking can itself reshuffle the blocks.

**Project-coined term**, closest standard concept: terminal/node mapping across circuit reduction (node aliasing after series/parallel collapse).

**Where it appears:** `docs/integration-tutorial.md` §3–§4 (device attach flow) and sharp-edges appendix item 15 (protect-then-resolve); `docs/api.md` §19 (`ConductorGraph` surface).

### PortNode / ReferenceNode (the post-compaction attachment pair)
The two key-resolved lookups on `ConductorGraph` that give clients their attachment points after reduction: `PortNode(JunctionKey)` is where a device's terminal lands, and `ReferenceNode(PartitionKey)` is the partition's return-rail datum — the node Stationeers-style loads use as their negative side.

Together they are the whole addressing contract between game code and the compacted netlist: the client never holds raw interior nodes, only these two resolutions of its own stable keys. `PortNode` is junction-scoped (see its own entry); `ReferenceNode` is **partition**-scoped — one per Stationeers cable network — and is created on first use, so it always resolves once any segment of that partition exists. Crucially, `ReferenceNode` is partition- not island-scoped and is **independent of coupler state**: when a closed coupler (breaker) merges two partitions into one solver island, each partition still has its own reference rail and `ReferenceNode(p)` keeps answering correctly (an island spanning multiple partitions reports `PartitionKey.None`, deliberately pushing per-partition addressing onto this call). For the programmer: two stable-key indexes over a structure that is rebuilt underneath them. For the EE: the local ground bus of each panel — the panels may be tied together upstream by a closed breaker, but each keeps its own return bar, and you always wire a load between its port and its own panel's bar.

**Project-coined pairing**; closest standard concepts: device terminal node, and the local reference/return node of a subnetwork ("reference node" itself is the standard MNA term for the datum whose row is eliminated).

**Where it appears:** `docs/api.md` §19 (`ConductorGraph.PortNode`/`ReferenceNode` signatures), §11 (`IslandHandle.Partition` and the coupler-independence note), §22.a (Re-Volt walkthrough: adapted loads wired `PortNode` → `ReferenceNode`).

**References:** Ho, Ruehli & Brennan (1975), "The Modified Nodal Approach to Network Analysis", *IEEE Trans. Circuits and Systems* (the role of the reference node in MNA).

### positional-with-defaults
- The Falstad/circuitjs1 file format's entire compatibility scheme: parameters on a line are read left-to-right by position, any missing trailing parameters fall back to built-in defaults, and any extra trailing tokens are silently ignored.

There is no version number in a Falstad element line. Instead, upstream element constructors read fields positionally with a `StringTokenizer`, wrapping the trailing reads in try/catch or a `hasMoreTokens` check; when the file ends early, the code substitutes a default value. New parameters are only ever *appended* (sometimes gated on a new flag bit), so old files load in new versions and new files load in old versions with the unread tail dropped. For an EE reading this: it is like a legacy instrument command protocol where fields have fixed order and you may always omit the last ones — the receiver assumes factory defaults. Programmers will recognize it as an informal, schema-less version of "optional trailing arguments": forward- and backward-compatible, but brittle, since nothing detects a field inserted in the middle or a semantics change. Our importer must handle this deliberately rather than copying upstream's silently-lossy posture.

**Project-coined term** (our name for upstream circuitjs1's implicit convention), closest standard concepts: optional positional parameters with default values; schema evolution by append-only fields (as in Protocol Buffers' compatibility rules).
- **Where it appears:** `docs/falstad-format.md` §3 (element line general form) and §5.1 ("Compatibility is purely positional-with-defaults"); it shapes the importer accept/reject contract in §7.

### post-compaction
- An adjective describing a circuit's size or solve cost *after* the reduction layer's series collapse has run, i.e. the size the solver actually sees.

Manatee never solves the raw as-built network. The reduction layer (compaction) first collapses series chains — long runs of cable segments become single equivalent resistors — so a base with thousands of placed cable voxels may present only a few hundred unknowns to the matrix solver. "Post-compaction size" is that reduced count, and it is the number that matters for performance claims: steady-state solves at post-compaction sizes are microseconds, island rebuilds triggered by a breaker opening are cheap because the halves being rebuilt are already small post-compaction. For an EE: it is the network after routine series-equivalent reduction, the same simplification done by hand before writing node equations. For a programmer: it is the cost model *after* the cache/compression layer, analogous to quoting query cost against an indexed, deduplicated table rather than the raw log.

**Project-coined term**, closest standard concept: circuit-size reduction via series/parallel equivalent combination (network reduction) before nodal analysis.
- **Where it appears:** `docs/stationeers.md` (Islands: breaker-opening rebuilds "cheap post-compaction"; Performance: tier-1/2 solves at post-compaction sizes), `docs/design.md` (perf targets), `docs/api.md` (`PortNode` — where devices attach post-compaction). The mechanism itself is `docs/compaction.md`.

### power-transfer boundary
- An interface between two solver islands where energy crosses but the two circuits stay mathematically separate — coupled by exchanging summary values instead of sharing one matrix.

Some devices connect two circuits without a direct wire between them: a transformer couples magnetically, a converter couples through power electronics. Manatee exploits this: each side stays its own island (its own MNA matrix and factorization), and the coupling device passes amplitude+phase (AC) or power/voltage values (DC) across the boundary each substep or tick, with explicit relaxation (exponential smoothing) to damp oscillation and a clamp so no more energy crosses than the source side actually delivered. The price is bounded lag — one substep behind, versus a whole tick. The contrast is with a *galvanic bridge* (e.g. a closed breaker), where the two sides genuinely merge into one matrix. Programming analogy: it is message-passing between two independently-scheduled services versus sharing one database transaction — decoupled, parallelizable, eventually consistent within a known staleness bound.

**Project-coined term**, closest standard concepts: co-simulation partitioning / waveform relaxation at a weakly-coupled interface. In EE terms the physical devices are ordinary transformers and converters; the "boundary" names our simulation strategy, not a circuit element.
- **Where it appears:** `docs/solver.md` Islands (coupling devices, exchange scheme); `docs/stationeers.md` (transformer = power-transfer boundary); `docs/api.md` `CouplerSpec.DecouplingTransformer` / `ConverterTwoPort`.
- **References:** for the relaxation-based coupling idea, Lelarasmee, Ruehli & Sangiovanni-Vincentelli (1982), "The Waveform Relaxation Method for Time-Domain Analysis of Large Scale Integrated Circuits", IEEE Trans. CAD.

### _powerRatio / scalar ratio distribution
The power-sharing scheme Stationeers (and Re-Volt today) uses instead of a circuit solve: compute one number, supply ÷ demand, and give every load that same fraction of what it asked for.

`_powerRatio` is a field in Re-Volt's `RevoltTick` (`RevoltTick.cs:285`); it is the single scalar by which every device's requested power is multiplied when total supply cannot cover total demand. There is no notion of voltage, resistance, or where devices sit on the network — it is a proportional rationing rule, like a rate limiter that gives every client the same fraction of its requested bandwidth regardless of network position. For an EE: it is not a physical model at all, just conservation of energy enforced by uniform scaling. In Manatee it plays two roles: it is the exact point in Re-Volt's tick that the MNA solve replaces, and it is the per-tick degradation fallback — if a manatee island faults (singular matrix, non-convergence), that network runs one tick on the scalar distribution and logs a diagnostic, so the game never stalls on the solver. The full graceful-degradation ladder is manatee → Re-Volt scalar → vanilla `PowerTick`.

**Project-coined term** (Re-Volt/vanilla implementation detail), closest standard concept: proportional load shedding / pro-rata power rationing.

**Where it appears:** `docs/stationeers.md` (Integration Seams; Failure and Fallback); Re-Volt's `RevoltTick.cs`.

### pre-partitioned / self-partitioned
- The two ways an island layout can reach the solver: the game client hands us circuits already split into separate networks (pre-partitioned), or the core figures out for itself which components are wired together (self-partitioned).

An island is one connected circuit = one matrix = one unit of parallel work, and someone has to decide where the islands are. Stationeers already maintains its own `CableNetwork` objects, so the Re-Volt integration passes those groupings in ready-made — pre-partitioned. Vintage Story and the tablet have no such structure, so manatee-core discovers connectivity itself by maintaining a union-find over the netlist — self-partitioned. The distinction is purely about *who computes the connected components*; the islands machinery downstream (merging, coupling devices, per-island solves) is identical, and islanding is core functionality but optional per client. EE analogy: it is the difference between being handed pre-drawn schematic sheets, one per isolated circuit, versus receiving one big netlist and having to trace continuity yourself to find the isolated circuits.

**Project-coined term**, closest standard concept: externally- vs internally-computed graph partitioning (connected-component decomposition of the circuit graph).
- **Where it appears:** `docs/solver.md` Islands; `docs/stationeers.md` (CableNetworks as the pre-partitioned source).
- **References:** union-find / disjoint-set connectivity: Cormen, Leiserson, Rivest & Stein, *Introduction to Algorithms* (CLRS), ch. on disjoint-set data structures.

### predict-then-observe
- The pedagogical form used throughout the tablet curriculum: a lesson asks the player to predict a specific number *before* the simulation reveals it, so understanding is tested by play rather than by derivation.

Every lesson (curriculum.md, Format and Authoring Rules) states numeric expectations in its narrative and poses Q/A in this form — "what will the meter read?" precedes the meter reading. The point is that the audience is curious adult gamers with no math assumed: formulas appear as patterns to verify against the meter, never as derivations to perform. The same numbers are duplicated in a machine-readable front-matter block (probe, time, expected value, tolerance) so CI runs the solver against them — a lesson whose predictions stop being true fails the build. Programmers can think of each lesson as a golden test where the human plays the assertion first; the EE analogy is a lab exercise where you compute the expected reading before touching the instrument. It even shapes design decisions elsewhere: the SWER lesson in design.md is framed around electrode-loss arithmetic as its "predict-then-observe core".

**Standard term:** predict-observe-explain (POE), a well-known technique in science education; our form drops the mandatory "explain" step and adds the CI-verified numeric expectation.

**Where it appears:** `docs/curriculum.md` (Format, Authoring Rules), `docs/design.md` (grounding model / SWER lesson, The tablet), `docs/harness.md` (lesson engine), `docs/testing-strategy.md` (lesson corpus as CI goldens).

**References:** White & Gunstone, *Probing Understanding* (Falmer Press, 1992) — the origin of the predict-observe-explain technique.

### probe / interpolated probe / oscilloscope tap (api.md)
- The three faces of measurement in the manatee-core API: a plain probe on a real node, an interpolated probe that reads a *virtual* point between two nodes, and a waveform tap that streams a probe into a ring buffer for the oscilloscope.
- `AddProbe(node, key)` pins a readout to an existing node. `AddInterpolatedProbe(a, b, t, key)` reads a voltage that no node holds: V = Va + t·(Vb − Va), a linear blend between two live nodes — this is how a point *inside* a series-collapsed run stays observable, with t derived from the cumulative resistance fraction along the original chain (for the EE: exactly the voltage-divider position along the chain; for the programmer: linear interpolation, same formula as a graphics `lerp`). After any rebuild or re-collapse, the reduction layer calls `Meta.SetProbeInterpolation(p, a, b, t)` to *re-aim* the same `ProbeId` at fresh endpoints — the handle the client holds never changes, only its aim does, so a tablet's scope subscription stays valid across topology churn. `WaveformTap.Attach(netlist, probe, ring)` is the cold-path oscilloscope subscription: per-substep samples land in a ring buffer, bounded by the two-probe contract. Reads go through `Read(ProbeId)` and are interpolated transparently.
- **Project-coined term**, closest standard concepts: saved output node (SPICE `.save`), voltage-divider tap, and oscilloscope channel; the re-aimable interpolator has no standard EE name.
- **Where it appears:** api.md §13 (Probes and the oscilloscope tap), §6 (`Meta.SetProbeInterpolation`, tier-0 re-aim), §16 (`ConductorGraph.AddProbe(segment, along)` surviving collapse via cumulative-R interpolation).

### probe interpolation
- Recovering the voltage at a point that the reduction layer optimized away, by computing where it falls between the two surviving endpoints, so meters can still read "inside" a compacted wire run.

When series-chain collapse (design.md R10) replaces a long run of resistive segments with one equivalent resistor, the interior nodes vanish from the solved system — the solver never computes their voltages. But a player may clip a multimeter to the middle of that run. In a pure series chain the voltage varies linearly with cumulative resistance (a resistive chain is a voltage divider), so the eliminated node's voltage is exactly `V_a + t·(V_b − V_a)` where `t` is the fraction of the chain's total resistance between endpoint `a` and the probe point. No information is lost by the optimization; the value is reconstructed on demand. Programming analogy: it is like reading a field of a struct the compiler optimized into registers — a debug-info map tells you how to reconstruct the value from what still exists. For an EE: it is the voltage-divider relation applied in reverse to a Thévenin-collapsed chain. Equivalence tests require the interpolated readings to match the raw (uncollapsed) interior node voltages exactly.

**Project-coined term**, closest standard concept: node-voltage back-substitution after network reduction (recovering eliminated unknowns from a reduced system, as in Kron reduction / Gaussian-elimination back-substitution).

**Where it appears:** design.md R10; compaction.md Responsibilities #3; solver.md Layering and State/Limits/Probes; api.md §13 and `Solution.Read(ProbeId)`; testing-strategy.md Equivalence Tests; integration tutorial §3.

**References:** Vlach & Singhal, *Computer Methods for Circuit Analysis and Design* (network reduction and back-substitution); Horowitz & Hill, *The Art of Electronics* (voltage dividers).

### interpolator (probe)
- The small reconstruction rule the reduction layer registers for each probe whose node was eliminated by series collapse, telling the solver how to compute that probe's reading from nodes that still exist.

Every interior probe inside a collapsed chain gets an interpolator: a triple `(a, b, t)` naming the two surviving endpoint nodes and the cumulative-resistance fraction `t` at which the probe sits between them. When a client calls `Solution.Read(probe)`, the solver evaluates `V_a + t·(V_b − V_a)` instead of looking up a (nonexistent) matrix row — a tier-1 read that allocates nothing (0B). The reduction layer owns these registrations because only it knows the chain geometry; the solver just stores and evaluates them. Programming analogy: it is a registered getter/callback that virtualizes a field which no longer physically exists — like a computed property backed by other state, or a virtual column in a database view. For an EE: the interpolator caches the divider ratio of the eliminated tap so the meter reading is exact for the DC/instantaneous solution of the chain.

**Standard term:** none exact; the mechanism is back-substitution into a reduced network, packaged as a per-probe record.

**Where it appears:** solver.md Layering ("reduction — series-chain collapse, probe interpolation") and the Probes bullet under State/Limits/Probes; design.md R10; registered/updated via `Meta.SetProbeInterpolation` (api.md §4).

### re-aim (probe interpolation)
- Repointing an existing probe's interpolator at new endpoint nodes after the reduction layer re-collapses a chain, without invalidating the probe handle the client holds.

Topology edits (adding a tap, removing a segment, a merge, a drift `Resync`) can force the reduction layer to re-run series collapse, which changes which nodes survive and where a given probe falls between them. Rather than tearing the probe down and issuing a new handle, the reduction layer calls `Meta.SetProbeInterpolation(p, a, b, t)` — a tier-0, zero-allocation call — to update the probe's targets on the same `ProbeId`. The client's handle (e.g. the tablet's oscilloscope reference) stays valid across rebuilds, merges, and resyncs; only the probe's *aim* — its interior interpolation targets — is repaired. Only a whole-netlist `FromCanonical` reload re-mints handles and forces re-resolution via `TryResolveProbe(key)`. Programming analogy: it is pointer fix-up after a compacting GC — the reference you hold is stable, and the runtime rewrites where it points. For an EE: the meter clip stays on the same physical spot on the wire; only the bookkeeping that maps that spot into the reduced circuit is refreshed.

**Project-coined term**, closest standard concept: handle/pointer fix-up (relocation) after incremental recomputation; no standard EE term exists.

**Where it appears:** api.md §13 (Probes and the oscilloscope tap), §4 `MetaFacade.SetProbeInterpolation`, §19 (reduction layer contract), and the handle-survival table (§16 column notes).

### probe / ProbeId / ProbeKey (integration tutorial)
- The identity story for measurement points: `ProbeId` is the live handle a client holds, and `ProbeKey` is the deterministic recipe that re-mints the probe's identity from geometry alone, so probes can always be found again.
- `ProbeId` is *document-stable*: it survives rebuilds, merges, splits, and drift resyncs — only explicitly removing the probe (or a whole-netlist reload via `FromCanonical`) invalidates it, so integrators can cache probe handles indefinitely, like breaker handles. `ProbeKey(segment, along, ordinal)` is a pure function (a splitmix64 hash under a reserved tag) of *where the probe is*: which segment, how far along it (0..1), and an ordinal distinguishing co-located probes (0 for the first). Because the key is minted deterministically from geometry, it is never serialized — after loading a save the client re-drives its world geometry, calls `ProbeKey` with the same arguments, gets the same key, and re-resolves via `TryResolveProbe(key)`. For the EE: it is as if every test point's label were computed from its physical position on the board, so a rebuilt board yields the same labels; for the programmer: content-addressed identity, like a hash-derived cache key, versus `ProbeId` as the ephemeral-but-durable pointer. After a drift `Resync`, probes survive re-aimed on the same handle; only `FromCanonical` forces the key-based re-resolve.
- **Project-coined term**, closest standard concepts: stable handle + content-derived (deterministic) key; no EE equivalent.
- **Where it appears:** integration-tutorial.md §6 (document-stable couplers and probes), §7 (re-resolve after `FromCanonical`; keys are never serialized), appendix sharp-edge 11 ("Probe keys are deterministic — re-derive to re-resolve"); api.md §13 and §16 (`ConductorGraph.ProbeKey`).

### probe / two-probe contract / probe values (Vintage Story)
- How the Vintage Story client consumes probes: throttled per-island "probe values" for tooltips and telemetry, and a "two-probe contract" that caps the oscilloscope at comparing exactly two live traces.
- **Probe values** are the ordinary readouts (voltage, current, temperature) that feed R15's instruments — multimeter, hover tooltips, the tablet. To avoid per-tick network spam, the server broadcasts them on a throttled `mna-telemetry` channel at roughly 0.5–1 s per island (modeled on the mechanical mod's ~800 ms telemetry), and the oscilloscope item separately negotiates a temporary high-rate waveform stream for one probe point. The **two-probe contract** is the solver's promise and the UI's budget rolled into one: at most two probes at a time may subscribe to per-substep waveform sampling into ring buffers. Two is not arbitrary — phase lessons (curriculum 15/17) need to compare a *pair* of traces to see phase shift, and capping at two bounds the sampling cost. For the programmer: it is a rate-limited pub/sub topic plus a hard subscriber quota; for the EE: it is a two-channel oscilloscope, which is exactly how it is themed in-game (a pencil-servo plotter).
- **Project-coined terms** ("two-probe contract", "probe values"); closest standard concept: a two-channel oscilloscope and periodic telemetry sampling.
- **Where it appears:** vintage-story.md §4 (telemetry channel, scope stream) and the Tablet Host section (tablet scope as the primary two-probe consumer); design.md R15 (instruments); solver.md "State, Limits, Probes" (the pair bound and its cost rationale).

### ProbeKey / derived topological identity
The stable name a reduction-minted probe gets, computed deterministically from *where the probe sits in the geometry* rather than from when or in what order it was created.

`ConductorGraph.ProbeKey(SegmentKey, along, ordinal)` hashes the segment's key, the fractional position along it, and an ordinal (which counts earlier co-located probes at the same spot, 0 for the first) through a splitmix64 bit-mixer under a reserved `Hi` tag, producing an `ExternalKey`. Because the inputs are purely topological — the same wire segment and position always yield the same key — two graphs built from the same geometry mint identical keys, and a client can **re-derive** the key itself to re-resolve its probe after a save/load or a re-driven intake (`TryResolveProbe(key)`), with no lookup table to persist. For an EE: it is like labeling a test point by its physical location on the board ("25% along trace R12–R13, first probe there") instead of by the order you clipped leads on — anyone re-reading the board assigns the same label. For a programmer: a content-addressed / deterministic ID, call-order-independent by construction. Collisions are birthday-scale in a 64-bit space and are surfaced by a debug duplicate-key throw at `AddProbe`, never silently.

**Project-coined term**, closest standard concepts: deterministic (content-derived) identifier / stable hash key; the underlying mixer is Steele et al.'s SplitMix64 finalizer.

**Where it appears:** `docs/api.md` §13 (probe survival across rebuilds) and §19 (`ConductorGraph.ProbeKey` signature); `docs/compaction.md` (probe interpolation).

**References:** Steele, Lea & Flood (2014), "Fast Splittable Pseudorandom Number Generators", OOPSLA 2014 (origin of the SplitMix64 mixing function).

### pure-cable block
A dedicated Vintage Story block type that contains only cable material — the cheap, common case for running wires through the world, as opposed to wires embedded in player-sculpted stone.

Manatee's VS mod supports cables in three world representations; the pure-cable block is representation #2. It is a dedicated block entity (a chunk of per-block game state) borrowed in design from our earlier `sparky` prototype and storing its shape in the same cuboid format the engine's chisel system uses, but *without* any chisel interaction surface: players cannot carve it, so the mod skips the machinery needed to coexist with player sculpting, making scanning the voxels for the electrical graph cheaper. The trade-off is exclusivity — a VS engine limit means it cannot share a world position with chiseled stone, which is acceptable because a pure cable run "owns its positions." Think of it as the fast path in a two-path design: shared-occupancy chiseled blocks handle the fancy cases (wire in a carved groove inside an oven wall), pure-cable blocks handle the 99% case of a plain cable run. For an EE: same conductor physics either way; this distinction is purely about how the game world stores the geometry.

**Project-coined term**, closest standard concept: none electrical — it is a game-data-layout choice (a specialized container for the common case, like a dense array beside a general sparse structure).

**Where it appears:** `docs/vintage-story.md` §1 "Chosen architecture", item 2; feeds the shared intake described in `docs/compaction.md` (Client Intake Contracts).

### push switch (EA `p`)
Electrical Age's importer misreads the Falstad element code `p` as a momentary push-button switch, when in the original Falstad format `p` means a two-terminal voltmeter probe — a known divergence that Manatee deliberately does **not** copy.

The Falstad circuit file format identifies each element by a short type code on its line; upstream circuitjs defines `p` as a probe/voltmeter (`ProbeElm`). The Electrical Age mod's parser (`FalstadNetlist.kt:618-621`) instead constructs a push switch from `p` lines, so any EA-corpus file using `p` means something electrically different from the same file loaded in falstad.com. For a programmer, this is a protocol dialect fork: two implementations assign different semantics to the same opcode, and silently accepting both would corrupt round-tripping. Manatee's importer follows the upstream meaning — `p` (like `O`) becomes a probe marker for the tablet and golden tests — and the format doc explicitly flags EA's reading as "a plain divergence from circuitjs, don't copy it."

**Standard term:** voltmeter / probe (the upstream meaning of dump type `p`).

**Where it appears:** `docs/falstad-format.md` §6 (EA dialect, parser-behavior notes) and the §7 accept list (`p` → probe marker).

**References:** Falstad/circuitjs1 source (`ProbeElm.java`), which the format doc cites line-by-line.

### R10 / R11
Two numbered design requirements from `docs/design.md` that together define Manatee's reduction ("compaction") layer: R10 = series-chain collapse, R11 = incremental topology maintenance.

**R10 (series-chain collapse)** requires that long unbranched runs of conductor — voxel regions in Vintage Story, cable segments in Stationeers — be reduced to single equivalent resistors before the solver sees them, transparently: voltages at the eliminated interior points are interpolated back out so probes and meters still read "inside" a collapsed run. The justification is cost: a 2×2×40 copper bus must cost one resistor, and Stationeers bases reach ~10k segments. **R11 (incremental topology maintenance)** requires that adding cable and merging networks update the compacted graph incrementally, while any removal simply rebuilds the affected island (detecting splits incrementally isn't worth the complexity at our island sizes), with a periodic or on-demand from-scratch rebuild acting as a *resync backstop* with drift detection. For a programmer this is a classic cache-with-reconciliation design; for an EE, R10 is just series-resistance combination (R_eq = ΣR_i) plus voltage-divider interpolation, mechanized and kept consistent as the circuit is edited live.

These are standard-style numbered requirements (like RFC "MUST" clauses), not electrical terms — do not confuse them with resistor reference designators (R10, R11 on a schematic).

**Where it appears:** `docs/design.md` Requirements ("Shared reduction layer"); `docs/compaction.md` intro ("Implements design.md R10–R11") and Responsibilities #5; implemented in `core/Manatee.Core/Reduction/ConductorGraph*` per api.md §19.

**References:** series/parallel reduction and node elimination are textbook — e.g. Pillage, Rohrer & Visweswariah, *Electronic Circuit and System Simulation Methods* (McGraw-Hill, 1995).

### R16 / R20
Two numbered design requirements from `docs/design.md` that jointly make the Falstad-format importer core infrastructure: R16 = the in-game tablet, R20 = one lesson corpus consumed three ways.

**R16 (the tablet)** specifies the in-game educational device: it imports Falstad-format netlists and hosts guided lessons plus a sandbox; finding it may gate advanced recipes, but completing lessons gates nothing, and the vanilla survival handbook remains the floor for teaching (the tablet is the ceiling). **R20 (one corpus, three consumers)** specifies that lessons are Falstad-format netlists plus teaching narrative in Electrical Age's `docs/examples` style, and that the identical corpus serves as (a) tablet tutorial content, (b) documentation examples, and (c) golden tests — every lesson's netlist is solved in CI against ngspice and against the expected values stated in its own prose, so "a lesson that stops being true fails the build." Because both requirements route through the importer, `docs/falstad-format.md` derives a strict contract from them: nothing may be silently dropped on import. For an EE, think of R20 as making the teaching examples double as regression test fixtures; for a programmer, it's docs-as-tests.

These are project requirement IDs (like ticket numbers), not resistor designators.

**Where it appears:** `docs/design.md` Requirements (R16 under "Vintage Story client", R20 under "Educational content"); invoked in `docs/falstad-format.md` §6–7 to justify the importer's accept/reject contract; `docs/testing-strategy.md` (lesson corpus as CI goldens).

### (rating, melt) pair
One element of a compacted chain's i²t limit envelope: a constituent segment's current rating together with its melt threshold, kept as a pair rather than blended into a single average.

When a run of cable segments is collapsed into one equivalent resistor (series-chain collapse), the solver still needs to know when any real segment along it would overheat. Overheating here is an i²t (current-squared × time) budget: heat accumulates while current exceeds a segment's rating, and the segment fails when the accumulated excess crosses its melt threshold. Different materials in a mixed chain can have a *different* worst segment for each criterion, so the envelope is a Pareto-minimal **set** of (rating, melt) pairs, not a single hybrid pair — a hybrid of segment X's rating and segment Y's melt would trip when no raw segment actually would (ruled 2026-07-06). Because every segment in a series chain carries the identical current, accumulating the i²t budget per pair is exactly equivalent to simulating the segments uncollapsed. For a programmer: it is like tracking per-tenant rate-limit buckets over one shared request stream instead of one merged bucket — merging the buckets changes who trips first. This dovetails with limit attribution: when a pair trips, the layer answers which physical voxel/segment melts.

**Project-coined term**, closest standard concepts: the I²t (let-through energy) rating used for fuses and wire protection, combined with Pareto-front bookkeeping over multiple constraints.

**Where it appears:** `docs/compaction.md` (Responsibilities #4, limit attribution); `docs/api.md` §19.

### RawVector
The solver's raw solution vector for one island — every node voltage and auxiliary current exactly as the matrix solve produced them, in a fixed internal order — exposed read-only as the unit of bit-for-bit comparison in tests.

`Solution.RawVector(IslandId)` returns a read-only view (a `ReadOnlySpan<double>`) over the published solution buffer, in MNA order fixed at symbolic time — i.e., the ordering of unknowns is frozen when the matrix structure is built, so the same circuit always yields the same layout. Its main job is determinism testing: canonicalization law 4 requires that solve → snapshot → restore → step matches a never-snapshotted run **bit-for-bit** on this vector (within one runtime/architecture/build). For an EE, this is like probing the solver's internal state vector directly rather than through calibrated front-panel readings (`Voltage`, `Current`, which may be interpolated through compaction). The view is perishable: it stays valid only until that island's next publish, and must only be read while that island is not concurrently mid-Step — after a publish the previously visible buffer is recycled as the next back buffer, so a span held across a foreign island's Step can observe a mixed-generation vector. The rule of thumb in the docs: never cache a span across a tick; copy scalars out.

**Project-coined term**, closest standard concept: the MNA solution vector **x** in **Ax = b** (node voltages plus branch/auxiliary currents).

**Where it appears:** `docs/api.md` §10 (`Solution` struct), §14 executable law 4 (bit-for-bit snapshot round-trip), §21 (span-validity and publication rules).

**References:** Ho, Ruehli & Brennan (1975), "The Modified Nodal Approach to Network Analysis", *IEEE Trans. Circuits and Systems* (the vector's contents and ordering come from MNA).

### Repin / re-pin / re-pin phase
Re-resolving the device handles a game client holds after the solver rebuilt an island and reissued them — looking each device up again by its stable `ExternalKey`/key and caching the fresh handle.

Handles into the solver (`ResistorId`, node ids, …) are fast direct pointers into solver storage that carry a generation and die when their island is rebuilt — old ones go stale, like array indices after the array was reshuffled. Identity survives the rebuild only in **keys**, so recovery is mechanical: for each held handle, re-resolve its key (`TryResolve`, `PortNode`, `ReferenceNode`, …) and store the fresh handle. Crucially, re-pinning is *pure tier-0 lookup* — it performs no structural edit, so it can never trigger the very rebuild it is recovering from, and it is idempotent, so re-pinning more often than strictly necessary is merely cheap, never wrong.

Re-pinning is a first-class client contract (§18), not optional hygiene: after the topology phase, an adaptor must consume `IslandTable.DrainChanges` (or its own `EditReceipt`) and, for every `Rebuilt` island, retire devices whose primitives were removed and re-resolve the survivors' handles by key. If `DrainChanges` reports `lost == true` (its change ring overflowed), the obligation escalates to a **full re-pin** — re-resolve *every* held handle. The canonical tick order runs a dedicated **re-pin phase** right after the topology phase, so no handle is invalidated underneath the running device loop. The one step integrators forget: island rebuilds also run *inside* `Solve`, so on a churn tick you must re-pin **again after Solve**, before reading results (tutorial phase 4b, its "most-forgotten step"). A missed re-pin is a counted no-op, not a crash: `TickStats.StaleHandleReads != 0` is the tell-tale (the separate `AdjustNoOps` counter is the opposite — converged devices, healthy). Couplers and probes are document-stable and never need re-pinning. For an EE: handles are like scope-probe clips on a board that got re-laid-out — the labels (keys) are permanent, the physical pads (handles) moved, and the re-pin phase is the scheduled moment to re-attach every clip.

**Project-coined term**, closest standard concept: handle re-resolution / rebinding after invalidation (cf. generational indices / weak references, and the handle-table pattern in game-engine and OS design, that must be re-acquired).

**Where it appears:** `docs/api.md` §16 (stale-handle sentinel, `DrainChanges` trigger), §17 (canonical tick order: topology → re-pin → drive → solve → readback), §18 (adaptor re-pin contract; `DeviceHost` explicitly does *not* implement it), §22.a (`RevoltAdaptor` pattern); `docs/integration-tutorial.md` §4–§5 and appendix sharp-edge 1; `Repin` methods on adaptors in `examples/RevoltWalkthrough/` (e.g. `TutorialLoad.Repin`).

### Re-Volt
The Stationeers mod, authored by Sukasa, that replaces the game's power tick with real DC circuit simulation by integrating manatee-core over the game's existing cable networks.

Re-Volt is one of manatee-core's three clients (alongside the 2D schematic harness and the Vintage Story mod) and the first game integration in the delivery order. The integration lives in Re-Volt's own repository, consuming manatee-core as a git submodule; on the Manatee side, `examples/RevoltWalkthrough/` is a runnable example that deliberately mimics Re-Volt's shape, and the "canonical tick order (Re-Volt shape)" in the API doc — topology → re-pin → drive → solve → readback — is named after it. Its constraints shape core requirements: DC only, fit inside Re-Volt's half-second power tick on its worker thread, compact ~10k raw cable segments to a graph in the low hundreds, and use implicit ground-return since Stationeers circuits have no explicit ground. Its MIT license is why manatee-core is MIT. Integration decisions affecting Re-Volt are relayed to Sukasa for sign-off. (Unrelated to the 1999 racing video game of the same name.)

**Where it appears:** `docs/design.md` (The Three Clients, Stationeers client, perf targets, Licensing), `docs/stationeers.md` (the full integration design), `docs/integration-tutorial.md` and `examples/RevoltWalkthrough/`, `docs/api.md` §17/§22; reference checkout at `third_party/revolt/`.

### read-vs-logs
A manual test pattern inherited from our earlier `sparky` prototype: the user plays the game normally, and Claude (or a developer) reads the resulting log files afterwards to verify the engine-interactive behavior worked.

Some behavior only exists when a real human drives a real game client — placing blocks, breaking cables mid-tick, saving and reloading a world. Rather than trying to script the game engine (fragile and expensive), the mod writes detailed logs during play, and correctness is judged by reading those logs back against expectations. It is the testing equivalent of a lab notebook: the experimenter runs the apparatus by hand, and the recorded traces are what gets analyzed. The pattern is explicitly a fallback, bounded by the fixture rule: any in-game bug that *can* be captured as an extraction fixture or netlist graduates into an automated regression test; only genuinely engine-interactive bugs stay in read-vs-logs territory.

**Project-coined term**, closest standard concept: manual/exploratory testing with log-based verification (a form of trace-based oracle).

**Where it appears:** `docs/testing-strategy.md` (Game-Layer Testing).

### realized netlist
The netlist that has actually been built and handed to the solver — the current live circuit description — as opposed to staged or proposed geometry that has not been committed yet.

When the game world changes (a voxel of cable placed, a segment removed), the compaction layer stages the new geometry in a shadow structure and then computes a *diff* against the realized netlist, so unchanged nodes and series chains keep their identities and the solver only pays for what actually changed. "Realized" here has its ordinary software meaning: made real, materialized. For an EE, the analogy is the as-built drawing versus the proposed revision — the realized netlist is the circuit as it currently exists on the bench, and edits are reconciled against it rather than rebuilding from the blueprints each time. For a programmer, this is the classic staged-vs-committed pattern (like a virtual-DOM diff or a git index): shadow geometry is the staging area, the realized netlist is HEAD.

**Where it appears:** `docs/compaction.md` (Incremental Maintenance — shadow-geometry staging in `ConductorGraph`), `docs/api.md` (recompaction diff).

### rebuild-on-split / rebuild the affected island
A deliberate design choice: when removing something *might* have split a connected circuit (an island) into pieces, we throw the affected island away and rebuild it from scratch instead of trying to figure out incrementally whether and where it split.

Growing connectivity is easy to track incrementally — adding a wire can only merge things, and a union-find structure handles merges in near-constant time. Removal is the hard direction: deciding whether deleting one edge disconnected the graph is the *dynamic connectivity* problem, whose incremental solutions are genuinely complicated. Manatee's islands are small enough (post-compaction, hundreds of nodes even for a ~10k-segment Stationeers base) that a from-scratch rebuild is cheap, so R5/R11 settle on the asymmetric policy: adds and merges are incremental, any removal rebuilds the affected island. For an EE, the analogy is choosing to re-derive the whole node equation set after cutting a wire rather than proving which equations survived — at small sizes the re-derivation is faster than the proof. The same cheap from-scratch build doubles as the resync backstop that validates the incremental path.

Operationally, removal never patches the live island: it marks the island dirty, and the rebuild runs at most once per tick, at solve time — so a deconstruction burst (a player tearing out fifty cable segments) costs one rebuild, not fifty. Breaker opening is the same event in coupler clothing: a closed breaker galvanically bridges two game networks into one solver island, and opening it rebuilds the two halves as separate islands. This is affordable because rebuilds happen *post-compaction* and are rare (breakers are safety devices, not switches flipped every tick). Think of it as cache invalidation with coarse granularity: the island is the cache line, and any removal invalidates the whole line because the line is small enough that precision would not pay for itself.

**Project-coined term**, closest standard concept: recompute-over-incremental for decremental graph connectivity (avoiding the dynamic-connectivity problem; edge insertions handled by disjoint-set union).

**Where it appears:** `docs/design.md` (R5, R11), `docs/solver.md` (Islands), `docs/compaction.md` (Island bookkeeping; Incremental Maintenance — coalesced rebuilds), `docs/stationeers.md` (Integration Seams; Islands and Coupling Devices — breaker open transition).

**References:** Cormen, Leiserson, Rivest & Stein, *Introduction to Algorithms* (disjoint-set/union-find data structures, which handle the merge direction we do keep incremental).

### recompaction diff
- The comparison between newly staged (shadow) compacted geometry and the netlist the solver currently holds, yielding the minimal set of solver edits needed to reconcile them.

When the game world changes (a cable placed or cut), Manatee does not tear down and rebuild the electrical network. Instead the reduction layer re-runs compaction on the affected dirty region into a *shadow* copy, then diffs that shadow against the realized netlist — the elements actually stamped into the solver — and emits only the additions, removals, and value changes that differ. This keeps the expensive Tier-3 structural work proportional to what actually changed, not to island size (subject to the rules in Incremental Maintenance: pure additions take a fast path; any removal coalesces into one island rebuild per tick). For an EE, the analogy is updating an as-built schematic: you mark up only the changed nets rather than redrafting the whole sheet. For programmers, it is the virtual-DOM pattern — build the desired state cheaply off to the side, diff against the live state, apply the minimal patch.

**Project-coined term**, closest standard concepts: incremental view maintenance / tree diffing (virtual-DOM reconciliation).
- **Where it appears:** `docs/compaction.md`, Incremental Maintenance — implemented in `ConductorGraph` as "shadow-geometry staging + recompaction diff against the realized netlist".

### Reconfigure
- The Netlist verb that changes the open/closed state of an *existing* coupler (e.g. a breaker or relay), the one topology-changing operation allowed in the normal runtime path.

Most operations that change circuit topology go through `Edit()`, the Tier-3 "construction" door with its batch/commit semantics. But relays and breakers open and close constantly during normal play — the *relay-vs-breaker duality*: electrically it is construction (the connectivity graph changes), operationally it is routine. `Reconfigure(CouplerId, CouplerState)` carves that one case out as its own verb: it costs nothing at the call site (0 bytes into the matrix), and the actual work is deferred to the next `Solve` — a galvanic coupler closing triggers an incremental island merge, opening triggers a split-rebuild (coalesced), while a boundary coupler (transformer/converter) merely toggles the exchange with no rebuild at all. It is sometimes called "Tier 3-lite": it *reports* as `Tier.Topology` (tier 3) in cost accounting rather than being a fifth tier, and `CostOfReconfigure` lets a client ask in advance whether a given call would be a cheap RHS change or a real topology change. Programming analogy: a dedicated fast-path API for the one mutation your hot loop needs, so the general-purpose transaction machinery keeps its strict meaning. EE analogy: operating a breaker on a live panel versus rewiring the panel — same physics, very different procedure.

**Project-coined term** (verb name), closest standard concept: switch/breaker state change in a simulator, handled in SPICE-family tools as a time-varying switch element rather than a topology edit.
- **Where it appears:** `docs/api.md` §4 (verb definition and comments), §7 (deferral inside island solves), §17 (the Reconfigure-to-Solve story), §24.11; `docs/integration-tutorial.md` §1, §5.

### rectilinear / diagonal element
- A geometry classification for imported Falstad-format elements: *rectilinear* elements run parallel to a grid axis (horizontal or vertical) and are accepted; *diagonal* two-terminal elements slant across the grid and are rejected if the harness grid requires axis alignment.

Falstad-format elements carry two endpoint coordinates on a drawing grid. The upstream circuitjs simulator happily draws components at any angle, but Manatee's 2D schematic harness (the tablet) may constrain its grid to axis-aligned placement, in which case the importer rejects diagonal two-terminal elements loudly — with line number, offending token, and reason — per the importer's nothing-silently-dropped contract. This has precedent: Electrical Age's own Falstad parser also rejects diagonal components (`FalstadNetlist.kt:556-558`). Note this is purely about *drawing* geometry, not electrical behavior — a diagonal resistor is electrically identical to a horizontal one; the constraint exists so the tablet's layout and hit-testing model stays simple. For an EE: it is the same rule most schematic-capture CAD packages enforce, where wires and parts snap to horizontal/vertical runs.

"Rectilinear" is standard terminology in layout/CAD (rectilinear geometry, Manhattan routing); "diagonal element" is the plain descriptive opposite.
- **Where it appears:** `docs/falstad-format.md` §6 (EA parser behavior) and §7 (reject list: "diagonal 2-terminal elements if the harness grid needs rectilinear").

### Reduction layer / compaction
- The shared layer of manatee-core (shipped as `ConductorGraph` in `Manatee.Core.Reduction`) that collapses long runs of conductor geometry into single equivalent resistors before the solver sees them, and interpolates the eliminated detail back out so nothing observable is lost — the *same* layer serves both integrations. See **compaction / reduction layer / ConductorGraph** (Project-Coined Terms part 1) for the five responsibilities, the R10/R11 incremental-maintenance contract, and the equivalence-test correctness story; this entry records the project-2-facing facts.

Clients describe conductors at their natural granularity — voxels in Vintage Story, cable segments in Stationeers, wires in a schematic — and get back a minimal netlist plus the maps to route observations and events back to geometry (series-chain collapse: ~10k segments become low-hundreds of nodes; probe interpolation; limit attribution). Because the layer is *shared*, its correctness burden is carried once — by the reduction-equivalence tests — rather than per game: Vintage Story feeds it voxel conductor regions (self-partitioned mode), Stationeers feeds it cable segments per `CableNetwork` (pre-partitioned mode), and both get back a form where a 2×2×40 copper bus or a 10k-segment base costs the solver a handful of resistors (design.md R10). Architecturally it is deliberately an **ordinary public client of Core**: it holds no internal reference and uses only the public `Manatee.Core` API, so there is no second, untested path into the solver (binding invariant, api.md §2). Probes are "named observation points surviving reduction"; the i²t part of each collapsed element's limit envelope is a Pareto-minimal set.

**Project-coined term**, closest standard concept: network reduction / series-parallel reduction (of resistive networks); related to node elimination (Kron reduction) in the literature. "Compaction" is project vocabulary inherited from the sparky prototype.

**Where it appears:** `docs/solver.md` (Layering; State/Limits/Probes); `docs/design.md` R10–R11 and the "Shared reduction layer ('compaction')" requirement group; `docs/compaction.md`; `docs/stationeers.md` Graph Construction; api.md §2 (visibility rules) and §19 (intake contract, `ConductorGraph`).

**References:** Series/parallel reduction is textbook material, e.g. Horowitz & Hill, *The Art of Electronics*, ch. 1; node-elimination reduction is treated in Vlach & Singhal, *Computer Methods for Circuit Analysis and Design*.

### Reduction equivalence
- The equivalence test guaranteeing that compaction is semantically invisible: a circuit solved raw and the same circuit solved after series collapse must give identical answers.

Concretely (testing-strategy.md, Equivalence Tests): any graph solved raw versus series-collapsed must produce identical terminal voltages and currents, and probe interpolation into a collapsed chain must match the voltages of the corresponding raw interior nodes. The test runs over the whole regression corpus and over generated random ladder networks. This is the project's "two paths, same answer" discipline applied to the reduction layer — for a programmer, a differential test between an optimized and an unoptimized implementation of the same function; for an EE, a check that the equivalent resistor really is equivalent, including the internal voltage-divider points. It is one of three equivalence families (alongside incremental equivalence and snapshot round-trip), reflecting the project's stance that the math is untrusted input verified by oracles, invariants, and equivalences rather than derivation.

**Project-coined term**, closest standard concept: differential/metamorphic testing of an optimization against a reference path; the circuit fact being tested is standard equivalent-network theory.

**Where it appears:** `docs/testing-strategy.md`, Equivalence Tests section; exercises the layer specified in `docs/compaction.md` and api.md §19.

**References:** For equivalent networks: Horowitz & Hill, *The Art of Electronics*, ch. 1 (Thévenin/series-parallel equivalents). For differential testing as a method: W. M. McKeeman, "Differential Testing for Software", *Digital Technical Journal*, 1998.

### RefId
Stationeers' own stable identifier for an in-game object (cable, device, network), which the Re-Volt integration reuses as its durable key into the solver.

Every entity in Stationeers carries a `RefId` that survives saves and loads, which makes it the natural identity to build on rather than inventing a parallel ID scheme. In graph intake, Sukasa's `NetworkExport` format delivers cable adjacency as `CableInfo { RefId, List<int> Connections }` — each cable segment is named by its RefId. In persistence, cable networks are rebuilt from scratch every load, but solver dynamic state (battery state-of-charge integrators, capacitor voltages) is snapshotted and restored keyed by network RefIds; a failed restore degrades to a cold start of that island, never a failed load. For an EE: a RefId is like a component's serial number — the same physical part keeps the same number across teardowns and reassemblies, so records about it can be re-attached. This is a Stationeers-native term, not a manatee-core concept; inside manatee-core the corresponding role is played by `ExternalKey`/`StateKey`.

**Where it appears:** `docs/stationeers.md` — Graph Construction (`CableInfo` intake) and Persistence (snapshot keying).

### Region
A group of conductor geometry (voxels, cable pieces) that the reduction layer treats as a single electrical node because everything in it is at the same voltage.

Game clients describe conductors at their natural granularity — individual voxels or cable segments — which is far finer than the solver needs. Geometry made of perfect conductors is merged into one region (all of it is one point, electrically); resistive materials instead form their own regions, connected to neighbors by inter-region resistances computed from material resistivity and contact cross-section. That rule is what makes a lead segment spliced into a copper run *be* a fuse with zero special-casing. Regions are also the unit of incremental maintenance: dirty-region tracking, a merge pre-check ("would this new geometry bridge more than one existing region?"), and a pure-addition fast path for growing a region without removals. Programming analogy: a region is an equivalence class under "perfectly connected", exactly the sets maintained by a union-find structure.

**Project-coined term**, closest standard concept: **node** (an equipotential set of connection points); merging conductors into one node is related to, but not the same as, the textbook "supernode" of nodal analysis (which is a source-spanning analysis trick, not geometry grouping).

**Where it appears:** `docs/compaction.md` — Responsibilities #1 (region building) and Incremental Maintenance; implemented in `core/Manatee.Core/Reduction/ConductorGraph*`.

### Region building
The first stage of the reduction layer: grouping equipotential conductor geometry into single solver nodes using a union-find data structure.

Each piece of conductor geometry starts as its own set; adjacent perfect conductors are unioned together until every maximal blob of perfectly-connected material is one set — one region, one solver node. Resistive materials are deliberately *not* unioned across material boundaries: they become their own regions, joined by resistances derived from resistivity and contact cross-section. The output is a drastically smaller node set (before series-chain collapse shrinks it further: ~10k cable segments end up as low-hundreds of nodes). Union-find (a.k.a. disjoint-set) is the classic near-constant-time structure for exactly this "merge groups, ask which group" workload, and its incremental-merge / rebuild-on-removal asymmetry is why additions are cheap while any removal triggers an island rebuild. For an EE: this is the step that turns physical copper into circuit-diagram nodes — deciding which points on the schematic are literally the same wire.

**Project-coined term**, closest standard concept: **node identification / merging of equipotential points** (netlist extraction); the algorithmic tool is the standard disjoint-set (union-find) structure.

**Where it appears:** `docs/compaction.md` — Responsibilities #1 and Client Intake Contracts; implemented in `core/Manatee.Core/Reduction/ConductorGraph*`.

**References:** Cormen, Leiserson, Rivest & Stein, *Introduction to Algorithms* (disjoint-set forests); Tarjan (1975), "Efficiency of a Good But Not Linear Set Union Algorithm", JACM.

### RegisterDeviceState / UnregisterDeviceState
- The pair of Netlist calls a game-side device uses to attach (and later detach) its saveable state to the circuit, anchored on one of its nodes.

A device that has state worth saving — a battery's charge, an integrator's accumulator — packages it as one *state unit* (an `IDeviceStateUnit`, sized by `StateSize` with `SaveState`/`RestoreState` doing one memcpy each) and calls `RegisterDeviceState(anchorNode, unit)` to anchor it on a live node, typically terminal 0. The anchor tells the solver which island the state belongs to, so snapshot/restore (requirement R6) can find and route it; think of it as bolting the device's logbook to a specific terminal so whoever saves that circuit section takes the logbook along. Re-registration replaces a stale anchor, which is exactly what the re-pin path does after an island rebuild — `Repin` re-resolves handles by key and calls `RegisterDeviceState` again, an idempotent tier-0 operation. When the game removes a device, its retirement sequence is: one `Edit` removing its components, then `UnregisterDeviceState`, then never touch it again.
- **Project-coined term**, closest standard concept: registering a serialization callback / participant with a save system (cf. the Memento pattern for externalized object state).
- **Where it appears:** `docs/integration-tutorial.md` §4 (re-pin and retirement), `docs/api.md` §14 and the `DeviceHost` section; implemented in `core/Manatee.Core/Netlist.State.cs`; worked example in `examples/RevoltWalkthrough/TutorialLoad.cs`.
- **References:** Gamma, Helm, Johnson & Vlissides, *Design Patterns* (1994) — Memento, for the idea of a device handing out an opaque state blob.

### relay contact (as netlist switch)
Manatee's cheap kind of switch: a relay's contact is modeled as an ordinary two-terminal switch element that lives inside its island's matrix, so toggling it is a fast (tier-2) operation.

This is one half of the **relay-vs-breaker duality** (settled 2026-07-05): same switching physics, different API citizenship, chosen per device type. A relay contact is `AddSwitch`/`Adjust(SwitchId, closed)` — 1 mΩ closed / 1 GΩ open, a conductance change that never restructures anything, which makes it the hot path for elevator-style control logic that may toggle every tick. A *breaker*, by contrast, is an island-coupling device: closing merges two islands into one matrix, opening triggers an island rebuild (tier 3 — the honest cost), acceptable because breakers are safety devices that change rarely. For programmers: the relay contact is an in-place field update on a live data structure; the breaker is a topology change that invalidates and rebuilds it. For the EE: both are just switches — the split is purely about which solver bookkeeping their state change touches.

**Standard term:** the modeling itself is the SPICE-conventional voltage-controlled-switch idiom (finite on/off resistances); the tier-2/coupling-device split is project-specific.

**Where it appears:** `docs/solver.md` (Islands, relay-vs-breaker duality; netlist API `AddSwitch`), `docs/design.md` (doc-review round), `docs/api.md` (`SwitchId`, tier-2 `Adjust`).

**References:** ngspice manual (the `SW` switch model's on/off resistance convention).

### Relay-vs-breaker duality
A project decision (settled 2026-07-05): a relay contact and a circuit breaker are physically the same thing — a switch — but get different API citizenship in the solver, chosen per device type.

A *relay contact* is a **netlist switch** living inside one island's matrix: toggling it is a cheap tier-2 `Adjust(SwitchId, bool)` (stamped as 1 mΩ closed / 1 GΩ open), because relay logic toggles constantly and must be the hot path. A *breaker* is an **island-coupling device** sitting *between* islands: closed means the two islands are merged into one solve; opening it splits them, which costs an island rebuild (tier 3 — the honest cost). That cost is acceptable because breakers are safety devices that change state rarely. For programmers: it is the same trade-off as choosing between mutating a value inside a data structure versus repartitioning the data structure itself — same logical operation, very different cost class, so the API forces the caller to pick the right representation per device. For the EE: electrically both are just contacts; the distinction is purely about which solver bookkeeping they participate in. In the API the two sides go through different verbs: the relay contact is `Adjust(SwitchId, bool)` (tier 2), while the breaker toggle is its own verb, `Reconfigure(CouplerId, CouplerState)` — a dedicated topology operation reported as `Tier.Topology` — so the honest cost is visible in the signature rather than hidden inside `Adjust`.

**Project-coined term**, closest standard concepts: ideal-switch modeling within one MNA system vs. circuit partitioning / subnetwork tearing at the switch boundary.

**Where it appears:** solver.md (Islands section), stationeers.md (Islands and Coupling Devices — Re-Volt's `IBreaker` maps to the coupling side), design.md (doc-review decisions), api.md (`Adjust(SwitchId, ...)` and `Reconfigure(CouplerId, ...)`).

### Removability / mod removability
The requirement that uninstalling Manatee/Re-Volt leaves a save file fully functional: vanilla game state is intact and vanilla electrical mechanics simply resume.

The integration achieves this by never making the mod the system of record. All device state keeps flowing through the vanilla API calls: after each solve, the legacy-device adaptor reports *actuals* back through the game's own provide/draw calls, so vanilla bookkeeping (charge levels, power flags, network membership) stays continuously correct even while Manatee is doing the real physics. Remove the mod and the vanilla code picks up exactly where its own records say things are. For programmers: this is the write-through cache discipline — Manatee computes results but always writes them back through the authoritative store, never keeping divergent private state the save depends on. For the EE: it is like an instrumentation retrofit designed so the plant runs unchanged if you unbolt it — no rewiring was hidden inside the add-on. This constrains the design: Harmony hooks substitute behavior, but persistent state stays vanilla-shaped.

This is a standard modding-community expectation (a "safely removable" or "clean-uninstall" mod), not project-coined, though the docs elevate it to an explicit hard requirement.

**Where it appears:** stationeers.md (Integration Seams — stated as "Removability requirement"; Legacy-Device Adaptor — actuals distribution keeps "mod removability intact").

### Renderer-selection rule
The project rule for how a wire in Vintage Story gets drawn: the representation is chosen by the number of wire voxels between supports — short spans render as voxel geometry, long spans as hanging catenary curves.

A wire fastened along a wall is shown as literal voxel-built cable mesh, while a bare wire strung between two distant posts (an electrified fence, a distribution run) is shown as a single sagging curve — a *catenary*, the shape a hanging chain naturally takes. Electrically nothing changes: the netlist sees the same conductor either way; only the visual representation switches. For programmers: it is a level-of-detail policy keyed on span length, like switching from per-tile rendering to a parametric mesh once a run exceeds a threshold. For the EE: it mirrors how real installations look — conduit-fastened cable up close, sagging spans between poles over distance. The catenary side is planned as a static baked mesh (borrowing the vanilla cloth/rope system's segment-transform math and two-endpoint activation) rather than live rope physics, avoiding cost and wobble.

**Project-coined term** (a rendering level-of-detail policy specific to this mod); "catenary" itself is the standard term for the hanging-cable curve.

**Where it appears:** design.md (device notes — electrified fences), vintage-story.md (Wire Rendering — catenary spans between posts, where the rule "plugs in").

### ReproBundle / CommandRecorder / Replay
- An opt-in, debug-only recording facility: `CommandRecorder` logs the stream of commands fed to the solver, `ReproBundle` packages that log into a single artifact, and `Replay` re-executes it to reproduce a problem exactly.

Because the solver's inputs are a deterministic command stream (edits, per-tick drives/adjusts, solves), capturing that stream captures everything needed to recreate a failure — the software analogue of a flight data recorder, or of logging every stimulus applied to a device under test so the bench setup can be rebuilt later. The recorder is debug-gated and ring-buffered ("ring-wrap"), with rebasing done off the simulation thread so recording never slows a game tick. Two very different failure sites share one artifact format: a CI test failure and an in-game island that entered the Faulted state both export a ReproBundle, so a bug found by a player replays on a developer's machine under a debugger. A key design choice makes bundles small: exchanges across an island's boundary are recorded as ordinary tier-1 commands (value updates), so a single island replays faithfully *without* its neighbors.

**Project-coined term**, closest standard concepts: deterministic record-and-replay debugging (cf. rr) and the Command pattern (Gamma et al.).

**Where it appears:** docs/api.md §2 (`Manatee.Core.Diagnostics` — public, cold-path) and §22.c; docs/solver.md failure handling (Faulted-island export).

**References:** Gamma, Helm, Johnson & Vlissides, *Design Patterns* (1994), Command pattern.

### Resistance wire / heating wire
- A cable material deliberately made from a high-resistivity conductor so that current flowing through it dissipates useful heat instead of being transmitted efficiently.

In Manatee's Vintage Story mod, heating wire is one of the cable-material blocks that can be laid as voxels inside a vanilla chiseled block (it is a legal chisel material under R17, alongside copper wire and insulation). Its electrical behavior needs no special code: the same resistivity-times-geometry rule that gives copper wire low resistance gives heating wire high resistance, so it heats up under load by the ordinary I²R mechanism the solver already models. The flagship use is the oven — not a custom block, but stone with chiseled grooves and resistance wire laid in them; the block heats food because the wire genuinely dissipates power. For programmers: think of it as the same class with a different constant, where the emergent behavior (a heater) falls out of the physics rather than an `if (isOven)` branch. This mirrors real-world resistance wire (e.g. nichrome heating elements in toasters and kilns).

**Where it appears:** `docs/vintage-story.md` §1 (chisel-material integration), `docs/design.md` (oven under progression arcs; R17).
- **References:** Horowitz & Hill, *The Art of Electronics*, on resistive power dissipation (P = I²R).

### Restore (additive)
- The rule that loading saved state into an island overwrites exactly the state units the save blob has entries for, and never touches or resets anything else.

A snapshot blob is a bag of (StateKey → state) entries — capacitor voltages, device blobs, melting integrals, coupler runtimes. `Restore` matches entries to the island's current units by `StateKey`; matched units are overwritten, unmatched units in the island are left exactly as they were, and blob entries with no matching unit are reported as `OrphansInBlob` rather than treated as errors. Because StateKeys are netlist-global while blobs are per-island, this composes across topology drift between save and load: if two islands merged since the save, offer both old blobs to the merged island in turn and nothing clobbers; if an island split, offer the same blob to every resulting island and each takes its matches. For an EE: it is like reconnecting labeled test leads after rewiring a bench — each lead finds its own labeled terminal, and terminals without a lead keep whatever they had. A consequence spelled out in the tutorial: no single `Restore` call can tell you what cold-started; coverage is aggregate, computed after all blobs are offered (`StateUnitCount − ΣMatched`).

**Project-coined term**, closest standard concept: selective / idempotent deserialization (merge-on-load rather than replace-on-load).

**Where it appears:** `docs/integration-tutorial.md` §7 and sharp-edges appendix item 10; binding contract in `docs/api.md` §14.

### Resync / resync backstop / drift detection
- The trio of mechanisms (requirement R11) that keeps Manatee's incrementally-maintained compacted topology honest: periodically or on demand, rebuild the reduced circuit from scratch, compare it against the live incrementally-updated version, and repair any divergence. The from-scratch build "is always available and cheap at island scale" (compaction.md), which is what lets it serve as the safety net under all incremental maintenance.

**The backstop / validation mode.** A validation mode rebuilds an island in the background, diffs the result against the incrementally-maintained state, and logs discrepancies — shipped as a **debug config in-game** and run **continuously in CI** (testing-strategy.md's incremental-equivalence test is the same comparison: randomized edit sequences vs. a from-scratch rebuild of the final state). The premise is an engineering admission, not optimism: incremental maintenance against a live game's mutation stream *will* have edge-case bugs, and the backstop converts them from silent mystery corruption into actionable bug reports. Programming analogy: the `assert(cache == recompute())` pattern institutionalized — derived state is only trusted because a full recomputation stands behind it. EE analogy: periodically re-deriving a Thévenin/equivalent circuit from the full network to confirm the shortcut you have been using still matches.

**Drift detection.** "Drift" is any divergence between what the incremental update path believes the reduced circuit looks like and what a clean rebuild from the same source geometry produces — the accumulated residue of edge-case bugs in applying a long stream of small edits. Detection is two-level for cost (api.md §19): a structural `Fingerprint` is compared every N ticks, and the expensive canonical diff (`ConductorGraph.Diff`) is computed only on mismatch, producing a `DriftReport` whose typed `DriftEntry`/`DriftKind` records (missing-in-live, value mismatch, envelope mismatch, probe-weight mismatch, …) are each named by `ExternalKey` — "a bug report with coordinates, never mystery corruption."

**Resync (the repair).** `ConductorGraph.Resync(island, truth)` (api.md §19) executes **snapshot → rebuild from the shadow graph → restore by key**: dynamic state is snapshotted, the island's reduced form is reconstructed from scratch out of the geometry the client asserts is true, and state is matched back on by `StateKey` rather than by internal slot number (`ExternalKey` is the sibling key `Diff`/adopt uses to re-bind *handles*; `StateKey` is what evolved physics state re-homes on — api.md §14 and the §16 survival table). Reduction-owned probes are **kept and re-aimed** on their existing handles — `ProbeId`s outlive a resync (api.md §13), and `ProbeKey(segment, along, ordinal)` mints the same key for the same geometry — which is strictly stronger survival than tearing probes down and re-creating them. A resync is a shape-cost operation (it allocates and triggers an island rebuild), so it is triggered deliberately: by drift detection (`Diff` mismatch), by journal-cursor overflow (`Overflowed()` — the reducer lapped its own event window), or on demand in debug/CI modes. The island rebuild it causes invalidates that island's component/node handles at the next Solve (tutorial §6) — couplers and probes, being document-level, survive.
- **Project-coined vocabulary**; closest standard concepts: reconciliation / periodic full rebuild (verification by recomputation); checksum-verified rebuild; anti-entropy repair of incrementally-maintained replicated state.
- **Where it appears:** design.md R11 and its testing section; compaction.md "Incremental Maintenance" (Resync backstop paragraph); testing-strategy.md "Equivalence Tests"; integration-tutorial.md §6 and sharp-edges appendix item 11; api.md §19 (`Diff`, `Resync`, `DriftReport`, `DriftKind`), §13 (probe survival), §14 (snapshot/restore laws).
- **References:** Demers et al. (1987), "Epidemic Algorithms for Replicated Database Maintenance", ACM PODC — the classic statement of anti-entropy reconciliation for incrementally-maintained replicated state.

### Same-island
- A constraint that two circuit elements live within a single solver island — the same independently-solved subcircuit, with no decoupling transformer boundary between them.

Manatee partitions the world's circuitry into islands: electrically separate subnetworks that the solver treats as independent problems (and can solve in parallel). Decoupling transformers act as island boundaries: they transfer average power between islands but "launder" reactive power — the moment-to-moment give-and-take between sources and capacitors/inductors — so phase relationships do not survive the crossing. (Idealized transformers, by contrast, are same-matrix two-ports: they stay within one island and preserve phase.) The curriculum authoring rule therefore requires that any lesson demonstrating power-factor or impedance behavior place source, load, and meters same-island, where the solver computes the full waveform interaction faithfully. Programming analogy: an island is one consistency domain, like a single database shard — queries spanning shards see only a relaxed, summarized view, so anything needing exact instantaneous consistency must stay within one shard.

**Project-coined term**, closest standard concept: same connected component of the circuit graph (in SPICE-family simulators, one matrix partition / subcircuit solved as a unit).
- **Where it appears:** `docs/curriculum.md` (Authoring Rules); islands themselves are defined in `docs/solver.md` (Islands).

### sandbox
- The tablet's free-play mode: build any circuit you like on the in-game gaming tablet, with no lesson goals attached.

The Vintage Story tablet has two modes: guided **lessons** (the 17-lesson curriculum with goals and goldens) and the sandbox, where the player experiments freely. Both run entirely client-side — sandbox circuits never touch the game server, so nothing you build there can affect other players or the world simulation. For an EE, think of it as a personal breadboard bench versus a structured lab exercise. Sandbox netlists persist across sessions: they are saved into player attributes as Falstad text (the same small, human-readable circuit format the importer already parses), so a circuit built in one play session is still there in the next.

The word carries its ordinary gaming/software sense (an unstructured, consequence-free play area); it is *not* the security-isolation sense of "sandbox" from systems programming, though the client-only rule gives a similar no-side-effects property.

**Where it appears:** `docs/vintage-story.md` (The Tablet Host — client-side-only and persistence bullets), alongside `docs/curriculum.md` for the lesson mode it contrasts with.

### SaveCanonical / SaveNormalized / FromCanonical
- The netlist's two whole-circuit serialization forms plus the single deserializer: `SaveCanonical` writes the entire circuit to bytes for faithful snapshots, `SaveNormalized` renumbers into a canonical form for comparing circuits, and `FromCanonical` rebuilds a fresh `Netlist` from canonical bytes. This is the "serialize everything" path, as opposed to the per-island state blobs used for incremental save/load.

The two save forms answer different questions. **`SaveCanonical`** is slot-preserving — internal numbering survives the round trip, so a recorded command log or snapshot taken before the save still replays against the reloaded netlist; the executable law is `SaveCanonical(FromCanonical(x)) == x`, byte-equal. **`SaveNormalized`** instead renumbers everything into a minimal form sorted by `ExternalKey` (the stable client-assigned names), so two netlists built by *different edit histories* of the same circuit produce identical bytes — a canonical form in the mathematical sense, and itself a fixpoint (normalizing twice changes nothing). That equality is the project's drift detector (requirement R11): any edit sequence must match its from-scratch rebuild under `SaveNormalized`, and the periodic drift backstop diffs normalized forms to produce a `DriftReport`. An EE analogy: `SaveCanonical` is a photograph of your breadboard exactly as wired, jumper colors and all; `SaveNormalized` redraws it as a tidy standard schematic so two differently-wired boards of the same circuit yield the same drawing.

**`FromCanonical`** rebuilds a netlist from canonical bytes and **re-mints every handle**, rebuilding everything including probe slots, so any `CouplerId` or `ProbeId` you held before the load is dead — you must re-resolve them by their stable external keys via `TryResolveCoupler(key)` / `TryResolveProbe(key)`. In EE terms: the reloaded netlist is the same circuit, but every old "wire label" you were holding now points at nothing; you look each one up again by its permanent name. Geometry is deliberately *not* serialized — the game world is the source of truth and is re-driven into a fresh `ConductorGraph` at load.

- **Project-coined terms**, closest standard concepts: serialization / canonicalization (normal form); the rebuild-and-re-resolve pattern is a form of the Memento pattern. "Canonical" here means *slot-faithful*, which inverts the common usage where "canonical" means the normalized form — the docs' `SaveNormalized` is what an outsider would call canonicalization.
- **Where it appears:** `docs/api.md` §4 (the `Netlist` surface) and §14 (the five executable serialization laws, and the handle-survival matrix); `docs/integration-tutorial.md` §7 and sharp-edges appendix item 11.
- **References:** Gamma, Helm, Johnson & Vlissides, *Design Patterns* (1994) — the Memento pattern.

### scalar fallback / scalar solve
- Re-Volt's pre-existing single-number power distribution scheme, kept as the safety net that a Stationeers network drops to when Manatee's real circuit solve is unavailable.

Before Manatee, Re-Volt distributed power by computing one ratio per network — available supply over total demand — and scaling every consumer by it (the `_powerRatio` point in `RevoltTick.cs:285`). That is a "scalar" solve: one number for the whole network, no voltages, no wire resistance, no topology. Manatee replaces it with a genuine MNA solve, but the scalar path stays wired in as the middle rung of a graceful-degradation ladder: **manatee → Re-Volt scalar → vanilla**. If a Manatee island faults for a tick (matrix singularity, non-convergence), that network falls back to the scalar ratio distribution for the tick and logs a diagnostic — the game never stalls on the solver. The same fallback covers the one-tick handoff while an expensive rebuild runs on a background task. For programmers, this is the standard degraded-mode pattern: a cheap, always-available approximation behind the accurate service, like serving stale cache when the database is down.
- **Where it appears:** `docs/stationeers.md` (Performance; Failure and Fallback).

### seam / integration seam
- A well-defined point where the mod plugs into a game engine's existing code without forking or patching the engine itself.

A seam is a place the engine already agreed to be extended: a documented interface, a registration hook, a subclass point. The canonical Manatee example is the BE-behavior seam in Vintage Story — `IMicroblockBehavior` lets a mod behavior attach to the vanilla chiseled-block entity via JSON, receive `RebuildCuboidList` on every voxel edit, and keep its own state, all without replacing engine classes; the mechanical-coupling seam (a `BEBehavior` subclass) works the same way. For an EE, a seam is like a designated test point or expansion connector on a board: the designer left you a clean tap, so you connect there instead of soldering onto arbitrary traces — which would break on every board revision, just as forked engine code breaks on every game update. Design requirement R4 notes that both games' integration seams map onto the solver's change-cost tiers: each seam delivers a particular class of change (value tweak, conductance change, topology edit), and the API prices each class explicitly so client authors can reason about cost.

The word is standard software-engineering vocabulary: Michael Feathers defines a seam as "a place where you can alter behavior in your program without editing in that place." Our usage matches, specialized to mod-vs-engine boundaries.

- **Where it appears:** `docs/vintage-story.md` §1 (BE-behavior seam) and §2 (mechanical coupling); `docs/design.md` R4; `docs/stationeers.md` (Re-Volt seams).
- **References:** Michael Feathers, *Working Effectively with Legacy Code* (Prentice Hall, 2004), ch. 4 "Seams".

### semantically invisible
- The binding requirement that the reduction (compaction) layer never changes any observable result: a circuit solved in reduced form must behave exactly as if it had been solved raw.

Concretely, two things must hold: raw and reduced graphs produce identical terminal voltages and currents, and probes placed *inside* a collapsed run still read correctly (via probe interpolation), agreeing with the raw interior nodes. Compaction is thus a pure performance optimization with no license to approximate — the same contract a compiler optimization has: the optimized program must be observationally indistinguishable from the unoptimized one. For the EE reader, this is just the statement that replacing a series chain with its Thévenin-equivalent resistance is *exact*, plus the extra promise that measurement points inside the chain are preserved. The requirement is not taken on faith: it is enforced by standing equivalence tests (raw vs. series-collapsed over the whole regression corpus and generated random ladders) and, in-game, by the resync backstop that diffs incrementally-maintained state against a from-scratch rebuild.

**Project-coined term**, closest standard concepts: behavioral/observational equivalence (in programming-language terms) or exact network equivalence (in circuit terms); also "transparency" of an optimization.

**Where it appears:** `docs/compaction.md` (Invariants), `docs/api.md` §19 (as-built reduction contract), `docs/testing-strategy.md` (Equivalence Tests).

### series-chain collapse / series compaction
- The project's name for the whole reduction feature and the manatee-core subsystem behind it (the `reduction` layer in `docs/solver.md`'s layering diagram, specified in `docs/compaction.md`): collapse long tap-free conductor runs — voxel cable runs in Vintage Story, cable segments in Stationeers — into single equivalent resistors before solving, then transparently reconstruct the eliminated interior voltages whenever something wants to probe "inside" the collapsed run.

Game worlds describe conductors at their natural granularity: a large Stationeers base reaches ~10k cable segments, and a 2×2×40 copper bus in Vintage Story is 160 voxels. Solved naively, every segment is a matrix row. Series compaction (design.md R10) collapses these chains so ~10k segments become low-hundreds of solver nodes, which is what makes per-tick solves microseconds at post-compaction sizes — critical in Stationeers, where the solve runs in-band with the game's shared 500 ms tick. Its contract is transparency in both directions: clients add and remove raw segments and address everything by their own keys, never seeing the collapsed form; and instruments placed mid-run still read correct values, because interior node voltages interpolate by resistance ratio along the chain (like reading a materialized view — the cache is consulted, but answers are indistinguishable from querying the raw data; see *probe interpolation*). The collapse is exact (see *semantically invisible* for the contract) and loses no information: per-element limit envelopes preserve which specific segment melts or trips when the equivalent resistor is overloaded. The layer also maintains this incrementally as voxels/cables are added and removed (R11), with a full-rebuild resync backstop — and for the EE reader, the engineering content is less the R1+R2 arithmetic than maintaining it *incrementally* against a live stream of build/deconstruct edits, continuously checked by that backstop.

**Project-coined term**, closest standard concept: series-resistance reduction / network reduction (with node elimination / Kron reduction as the general form; the probe-interpolation half resembles back-substitution recovering eliminated unknowns). The docs use "series compaction," "series collapse," and "series-chain collapse" interchangeably; "compaction" is the sparky-inherited name for the layer as a whole.

**Where it appears:** `docs/solver.md` (Layering; State/Limits/Probes); `docs/design.md` R10 and the Stationeers section; `docs/compaction.md` (the authoritative spec); `docs/stationeers.md` (Graph Construction, Performance — "the load-bearing optimization"); `docs/integration-tutorial.md` §3, §8; `docs/api.md` §19; implemented in `core/Manatee.Core/Reduction/ConductorGraph*`.

**References:** Vlach & Singhal, *Computer Methods for Circuit Analysis and Design* (network reduction and node elimination); Pillage, Rohrer & Visweswariah, *Electronic Circuit and System Simulation Methods* (1995).

### SetPowerFromThread pattern
- The idiom Stationeers/Re-Volt already uses for handing a result computed on a background thread back to the game's main thread, which Manatee copies for its own off-band work.
- Unity (Stationeers' engine) only allows game-object state to be touched from the main thread, so any computation done elsewhere must marshal its results back. The pattern — named after the method at `Device.cs:1333` in the game's decompiled source — is: snapshot inputs under a lock, compute on a background task, then hop back to the main thread via `UniTask.SwitchToMainThread()` / `.Forget()` (or `UnityMainThreadDispatcher.Enqueue`) and write the computed power there. Manatee follows the same shape for its expensive paths (tier-3 island rebuilds, load-time full rebuilds): the network runs on the previous solution for one tick while the rebuild lands. The EE analogy is a sample-and-hold feeding a synchronizer: the fast asynchronous stage produces a value whenever it finishes, but the value only enters the clocked system at a safe clock edge.
- **Project usage of a game-specific method name**; closest standard concepts: thread marshalling / posting to a UI (main-thread) dispatcher, double-buffered handoff.
- **Where it appears:** `docs/stationeers.md` (Performance → design consequences), citing `Device.cs:1333` and `PowerTick.cs:59-70` in the decompilation.
- **References:** Gamma, Helm, Johnson & Vlissides, *Design Patterns* (1994) — related to the general producer/consumer and command-queue idioms behind main-thread dispatchers.

### shadow-geometry staging
- The `ConductorGraph` editing discipline: incoming geometry changes from the game are first applied to the shadow (the pre-compaction copy), then the shadow is recompacted and the result diffed against the realized netlist, so only genuine differences become netlist edits.
- On each non-bulk `AddSegment`, the graph updates the shadow, recompacts it, and diffs the compacted result against what the netlist currently contains; unchanged regions produce zero edits, which is what lets device handles and solver state survive geometry churn. During load, `BeginBulkBuild` batches all staged segments and runs one compaction pass at Dispose instead of one per segment. A programmer will recognize the shape as a staging buffer or double-buffering: mutate the back copy freely, then commit a minimal diff to the live copy. An EE analogy is redlining a master schematic and re-deriving the reduced equivalent from it, only re-soldering the parts of the breadboard that actually changed. Per the docs, the eager per-edit cost is currently O(graph); a localized fast path is scheduled perf work, not a semantic difference.
- **Project-coined term**, closest standard concept: double-buffering / staging buffer with diff-based commit.
- **Where it appears:** `docs/compaction.md` (Incremental Maintenance, ConductorGraph note); `docs/api.md` §19 (`ConductorGraph.AddSegment` / `BeginBulkBuild` commentary).

### shadow (graph)
- The reduction layer's private, pre-compaction copy of the raw conductor geometry, kept alongside the compacted netlist so edits can be applied to the full-detail picture and then diffed down.
- `ConductorGraph` ingests every cable segment the game reports and stores it in this shadow; compaction (series collapse etc.) runs over the shadow, and the result is diffed against the currently realized netlist so that unchanged nodes and chains emit no edits — handles survive, and a redundant pass is a no-op. The shadow is deliberately NOT serialized (ruled 2026-07-06): the game client owns geometry as its source of truth, and at load the client re-drives all segments into a fresh graph, which converges to the same observable state because every reduction-owned identity (equivalent resistors, region nodes, probes) is derived from client geometry rather than stored. For an EE: the shadow is the full schematic and the netlist is the series-reduced (compacted) equivalent network actually handed to the solver — resistor chains collapsed into single equivalent resistors, with port nodes and probes preserved; the shadow exists so the reduction can be redone incrementally and reproducibly. For a programmer: it is a materialized intermediate representation between the client's world data and the solver's matrix, rebuilt from scratch cheaply at island scale (the resync backstop).
- **Project-coined term**, closest standard concepts: shadow copy / staging representation; also related to a compiler's IR between source and generated code.
- **Where it appears:** `docs/api.md` §14 (Resync: "rebuild from shadow") and the "shadow is deliberately NOT serialized" ruling near §19; `docs/compaction.md` (Incremental Maintenance).

### Shape / shape-time
- Project notation marking API members that are allowed to allocate memory — but only at the moment the circuit's *structure* changes ("shape" time), never during a simulation tick.

Manatee's per-tick hot path is required to be allocation-free after warmup (`0B` in the same notation), because garbage-collector pauses inside a game's frame loop cause visible stutter. All heap allocation is therefore corralled into the moments where the circuit's topology is being rebuilt anyway: `Edit.Commit`, full rebuilds, `BulkBuild` dispose, and `Resync`. Those operations size and allocate everything the ticks will later use — the matrix, LU workspace, solution buffers, component tables, compaction maps — so the steady-state loop just reuses them. For an EE, the analogy is fabrication versus operation: you may re-lay the PCB (slow, disruptive, done on the bench), but once the board is running, nothing about its physical layout changes — signals just flow through what was built. The API docs tag each member with `0B`, `shape`, or `cold` so the allocation budget is auditable per call, and CI enforces the buckets with a memory diagnoser.

**Project-coined term**, closest standard concepts: construction/initialization time vs. steady-state, or "structural commit time"; the underlying discipline is the standard real-time/game-dev rule of zero allocation on the hot path (object pooling, preallocation).

- **Where it appears:** `docs/api.md` — the notation key near the top (`shape` = allocates, but only at structural-commit time, never in a tick), §6 `StructuralEdit`, and the §21 allocation-bucket audit table.

### Silent-extra-tokens
- Upstream Falstad/circuitjs1's habit of silently ignoring unrecognized trailing tokens on a line — which Manatee's importer deliberately rejects instead.

In the original Java code, each element's constructor reads its parameters positionally and simply ignores anything left over on the line; combined with try/catch defaults for *missing* trailing params, this is the format's entire versioning mechanism (new fields are only ever appended). It is forgiving, but it is also how dialect drift starts: a producer can append tokens the consumer never validates, meanings quietly fork, and no one notices until files disagree. For an EE, it is like an instrument that accepts any out-of-range input and displays *something* rather than flagging a fault — convenient until you trust a wrong reading. Because Manatee's lesson corpus is CI truth (nothing may be silently dropped), the importer's contract is the opposite posture: any trailing tokens it does not recognize on an accepted element cause a loud rejection with line number, offending token, and reason.

**Project-coined term** (a label for the upstream behavior we reject); closest standard concepts: lenient/permissive parsing, Postel's law ("be liberal in what you accept") — which Manatee explicitly declines here in favor of strict validation / fail-fast parsing.

- **Where it appears:** `docs/falstad-format.md` §7 (importer accept/reject contract; "silent-extra-tokens is how dialect drift starts"), with the upstream behavior documented in §5 (positional-with-defaults compatibility).

### silently lossy
The error posture of the upstream Falstad/circuitjs1 file reader — bad or unknown input lines are simply skipped, with at most a console log — which Manatee's importer is explicitly forbidden to imitate.

In circuitjs1, a line with an unrecognized dump type, or one that throws any exception while being parsed, is dropped and loading continues (`CircuitLoader.java:200-207`); the user gets a circuit that is missing pieces with no visible error. For an interactive toy that is tolerable; for Manatee, imported lesson files are CI truth and core infrastructure, so the importer contract is the opposite: every input line is either accepted with full electrical meaning, ignored-with-a-counted-notice (presentation-only lines like scope configs), or rejected loudly with line number, offending token, and reason. In EE terms, it is the difference between a fuse that opens visibly and a joint that quietly goes high-resistance: the second failure mode corrupts results without announcing itself. "Silent-extra-tokens" tolerance is also called out as how format dialects drift apart.

**Project-coined term**, closest standard concepts: silent data loss / fail-silent behavior, versus fail-fast error handling.

**Where it appears:** `docs/falstad-format.md` §1 (upstream reader behavior) and §7 (Manatee importer accept/ignore/reject contract).

### Snapshot / restore (state)
- manatee-core's per-island save/load mechanism (requirement R6): `Snapshot` writes all of an island's evolving dynamic state into one versioned binary blob, and `Restore` writes such a blob back, matching entries by `StateKey` — so a game can pause, save, or unload a world and resume the simulation exactly where it left off.

The payload (SnapshotVersion 3) covers everything that evolves over time rather than being rebuildable from the netlist description: capacitor voltages, inductor currents, each device's `Tick` state (e.g. a battery's state of charge), source phase (so AC waveforms resume mid-cycle without a jump), i²t melting integrals and trip latches (including per-pair envelope integrals for reduced cables), and boundary-coupler runtime scalars. The netlist *topology* is deliberately **not** in the snapshot — both games rebuild topology from world data on load (Stationeers rebuilds networks every load) — so the snapshot carries only what cannot be re-derived, keyed by a client-stable `StateKey` so a rebuilt circuit finds its saved blobs. The format is versioned binary with a JSON debug form for human inspection. For an EE these are exactly the initial conditions of the differential equations; forget one and the resumed run has a discontinuity.

The binding rule is that **restore is additive**: a blob overwrites exactly the units whose keys it carries and never resets anything else — like a dictionary `merge`, not a `replace`. That makes restore compose when topology drifted between save and load: islands that merged accept multiple old blobs in turn; a split island is offered the same blob everywhere, with unmatched entries reported as `OrphansInBlob` rather than treated as errors. Coverage is checked in aggregate (`sum(Matched)` vs `StateUnitCount`), never per call, and an executable CI law guarantees solve → snapshot → restore → step is bit-for-bit identical to a never-snapshotted run.

**Two client policies build on this mechanism.** *Tablet "paused while closed" (Vintage Story):* the tablet runs client-side on a fixed 10 ms sim dt and only steps while its GUI is open; on close it snapshots its dynamic state and on reopen `Restore` puts every unit back exactly, so a half-charged capacitor is still half-charged and RC/resonance lessons are not ruined by a cold restart (vintage-story.md, The Tablet Host). *Stationeers/Re-Volt persistence:* because the game reconstructs cable networks from scratch every load, evolving solver state (battery state-of-charge integrators, capacitor voltages) survives only through this hook — Re-Volt calls per-island `Snapshot` on save and `Restore` on load through an external save/load hook (confirmed feasible by Sukasa), with `StateKey`s derived from the game's stable network `RefId`s so identity survives the rebuild; a failed restore degrades to a **cold start of that one island** (a battery reads freshly initialized), never a failed game load (stationeers.md, Persistence).

**Standard term:** checkpoint/restore (systems); SPICE's `.ic`/`.nodeset` and ngspice's `savebias`/`loadbias` are loose analogues (they capture only node voltages / bias points — our snapshot is broader and lossless), and the additive-merge semantics are the project's own design. See also **Snapshot / SnapshotSize** for the concrete `Snapshot(IBufferWriter<byte>)`/`SnapshotSize` API pair.

**Where it appears:** `docs/api.md` §14 (laws, `RestoreResult`), §11 (`IslandHandle.Snapshot`/`Restore`/`SnapshotSize`); `docs/solver.md` (State, Limits, Probes); `docs/design.md` R6 (which cites Stationeers save/load as a justification); `docs/integration-tutorial.md` §3 (StateKey) and §7; `docs/vintage-story.md` (The Tablet Host, "Paused while closed"); `docs/stationeers.md` (Persistence).

### Snapshot / SnapshotSize
- The concrete API pair on `IslandHandle` for saving an island: `Snapshot(IBufferWriter<byte>)` writes the island's state blob, and `SnapshotSize` tells the caller how many bytes to expect.

`Snapshot` writes one versioned binary blob containing every state unit in the island — capacitor voltages, device blobs (`IDeviceStateUnit.Save`), melting integrals, coupler runtimes — each entry keyed by `StateKey`. It allocates nothing on the core side (0 B); the caller supplies the buffer via the standard .NET `IBufferWriter<byte>` interface, so the host game controls memory. `SnapshotSize` is stable between topology-changing operations (edit commits, galvanic merges/splits): between such events you may cache it, but after any `IslandChange` you must re-read it, because the set of state units may have changed. For an EE: `SnapshotSize` is like a parts-count on the state vector — fixed while the circuit's structure is fixed, invalidated the moment you rewire.

**Where it appears:** `docs/integration-tutorial.md` §7 (Save/load); `docs/api.md` §11 (`IslandHandle`) and §14 (payload contents and laws).

### Snow-cover pattern
- The integration recipe Manatee copies from Vintage Story's own code for attaching electrical behavior to chiseled (microblock) blocks: do exactly what the vanilla snow-cover feature does, because the engine demonstrably supports it.

Vintage Story ships `BEBehaviorMicroblockSnowCover`, a behavior attached to the standard chiseled-block entity that keeps its own parallel list of voxel cuboids, serializes its own state, overlays its own mesh on top of the block's, and can register tick listeners — all without replacing or forking the vanilla block. Manatee's cable behavior needs precisely this shape: its own cable-voxel data alongside the player's decorative chiseling, its own persistence, its own rendered wires, and periodic updates. Attaching via JSON patch to the vanilla entity (rather than substituting a custom block) is what makes cables coexist with ordinary chiseling (requirement R17). For an EE, the analogy is a proven reference design: rather than arguing from the datasheet that the engine *should* allow this, we point at a shipping vanilla feature that already does it, with file:line evidence.

**Project-coined term**, closest standard concept: following a reference implementation / an existing extension-point exemplar (in design-patterns language, using the engine's Decorator-like behavior seam the way first-party code does).

**Where it appears:** `docs/vintage-story.md` §1 (engine facts, with file:line cites), `docs/design.md` R17.

### Solution / RawVector
- `Solution` is the published result of the last solve — the API through which clients read node voltages, branch currents, and probe values — and `RawVector(island)` is its low-level escape hatch: the island's raw array of solved numbers.

`Solution` offers safe scalar readers (`Voltage(node)`, `Current(branch)`, `Power(component)`, `Read(probe)`), each returning one number with zero allocation. `RawVector(IslandId)` instead returns a read-only *view* (`ReadOnlySpan<double>`) directly over the island's solution buffer, in the fixed MNA ordering established at symbolic-analysis time; it is the unit used for bit-for-bit determinism comparisons in tests. The view has a strict validity window: it is valid only until that island's next publish, and only safely readable while that island is not concurrently mid-Step, because after a publish the previously visible buffer is recycled as the next back buffer — a held span could observe a half-overwritten, mixed-generation vector. For an EE: think of it as probing a live bus that gets rewired between measurements — take your reading and let go. The tutorial's rule: copy scalars out; never store the span in a field or cache it across ticks.

**Standard concept mapping:** the underlying data is the MNA solution vector (node voltages plus branch currents); `RawVector` as a lifetime-limited buffer view is a programming construct (double buffering).

- **Where it appears:** `docs/api.md` §10 (Solution and readback), §21 (validity windows); `docs/integration-tutorial.md` §5–§6 and sharp-edges appendix items 5–6.
- **References:** Ho, Ruehli & Brennan (1975), "The Modified Nodal Approach to Network Analysis", IEEE Trans. Circuits and Systems, for the MNA vector layout.

### Solution / readback
- Our name for the last phase of every tick, in which the game reads the freshly published `Solution` snapshot and pushes the results (actual powers, limit events, telemetry) back into game devices.

After `Solve` publishes results, the tick enters the readback phase: a pure polling pass — the solver never calls back into client code; the client walks its devices and asks the `Solution` what happened. The `Solution` snapshot is immutable once published and safe to read while *other* islands are still stepping, which is what lets readback be threaded; the readback *barrier* (all island steps complete) is the point after which even cross-island `RawVector` reads become safe and deterministic. For a programmer this is a classic publish/subscribe-free design: one immutable snapshot per generation, readers poll it. For an EE: the solver runs the circuit, then everyone reads the meters afterwards — nobody adjusts knobs mid-measurement. Structural reactions decided during readback (pop a fuse, open a breaker) are legal but take effect in the *next* tick's solve.

**Project-coined term**, closest standard concept: post-processing / output phase of a simulation timestep; the snapshot itself is the operating-point or transient solution.

- **Where it appears:** `docs/api.md` §10 (Solution and readback), §17 (tick phase order: topology → re-pin → drive → solve → readback), §21 (thread-safety windows); `docs/integration-tutorial.md` §5 step 5; `docs/harness.md` Layering #1 (solution readback for meters and scope traces).

### Solve
- The one call per game tick that recomputes every circuit that changed: `Netlist.Solve(clock)` finds all dirty islands and solves each of them once, after coalescing all queued changes.

`Solve` is deliberately singular. All mutations made since the last tick — component edits, breaker reconfigures, drive updates — are batched, and when `Solve` runs, each dirty island gets exactly one rebuild-and-solve no matter how many changes hit it: N cable cuts on one island are still one rebuild. This batching is why the integration rule is "one global tick body, one Solve" — solving per-network would multiply cost and mis-drain the change-notification queue. A crucial side effect: island rebuilds run *inside* Solve and reissue every member device's handle, so any tick that touched topology must re-resolve its handles again after Solve returns (the tutorial's step 4b, its "most-forgotten step"). For an EE: Solve is the moment the simulator advances the circuit one timestep; everything before it is setting up conditions, everything after is reading meters. A companion call, `SolveOperatingPoint()`, computes the DC operating point instead (used to energize a circuit or start a lesson).

**Standard concept mapping:** one transient-analysis timestep (SPICE's `.TRAN` inner loop), plus incremental re-elaboration of changed subcircuits.

- **Where it appears:** `docs/api.md` (Netlist surface, §17 tick order); `docs/integration-tutorial.md` §5 (tick loop steps 4 and 4b); `docs/solver.md` (analyses, change-cost tiers).
- **References:** Nagel (1975), *SPICE2: A Computer Program to Simulate Semiconductor Circuits*, UCB/ERL-M520, for the classic per-timestep solve loop.

### solve time
- The moment within a game tick when the solver actually runs its numerical step — and, by design, the single point where deferred rebuild work is executed.

Manatee batches expensive structural work instead of doing it eagerly. When cable segments are removed, the affected island is only *marked dirty*; the actual island rebuild is coalesced and performed at most once per tick, at solve time. The payoff is that a deconstruction burst — a player tearing out fifty cable segments in one tick — costs one rebuild rather than fifty. For programmers this is ordinary write coalescing / lazy invalidation (dirty flag now, recompute at the next read). For an EE, the analogy is a sampled system: the circuit description may be edited many times between samples, but the physics is only re-derived once per sample instant, so only the net change matters.

**Standard term:** none specific; the underlying technique is standard *lazy evaluation / batched invalidation* (a dirty-flag pattern).
- **Where it appears:** `docs/compaction.md`, Incremental Maintenance (coalesced removal rebuilds); the tick loop in `docs/solver.md` and the tutorial define when the solve step happens.

### SolverProfile (Dc / Transient / Mixed)
- The analysis regime fixed at `Netlist` birth, per `docs/api.md`: `Dc(dt)` (Stationeers: dt=0.5 s, DC with backward-Euler storage dynamics — capacitors and batteries charge/discharge over time, but no AC subcycling, exactly one solver step per tick), `Transient(dt)` (VS DC-side / tablet: dt=0.05 s), and `Mixed(tickDt, acSamplesPerCycle=20)` for anything with AC.

The key nonstandard design point is `Mixed`: one netlist hosts *both* DC and AC islands simultaneously, and the regime is chosen **per island, by content** — an island containing a sine source subcycles (N sub-steps per tick, quantized with hysteresis, never client-settable), while a pure-DC island in the same netlist steps once. This is a binding invariant (api.md decision 13); the alternative one-regime-per-netlist reading was explicitly rejected. The tutorial's sharp edge: adding a sine source to a non-`Mixed` (e.g. `Dc`) netlist is *legal* but the source is single-sampled per tick — deterministic and phase-wrapped, yet heavily undersampled (think a 60 Hz signal sampled at 2 Hz: aliased nonsense, reproducibly), and debug builds warn at Add time. For programmers: the profile is an immutable configuration flag fixed at object birth, a whitelist of behaviors whose actual per-island regime is dispatched dynamically on island contents — like a runtime choosing an execution strategy per work item. For an EE: unlike SPICE, where you request one analysis (`.OP`, `.TRAN`) for the whole deck per run, here the "deck" permanently carries its regime, and `Mixed` lets different sub-circuits effectively run different time resolutions in the same simulation.

**Project-coined term**, closest standard concept: SPICE *analysis modes* (DC operating point vs. transient analysis), bound to the netlist at creation rather than requested per run, combined with per-island multirate time stepping.
- **Where it appears:** `docs/api.md` §5 (struct definition inside `NetlistOptions`) and §24.13 (one-Mixed-netlist invariant); `docs/solver.md` (subcycling, islands); `docs/integration-tutorial.md` §2 and sharp-edges appendix item 8.
- **References:** Nagel (1975), SPICE2 memo UCB/ERL-M520 (analysis modes); Pillage, Rohrer & Visweswariah, *Electronic Circuit and System Simulation Methods* (transient integration, multirate ideas).

### source driver
The piece of the solver that updates a sinusoidal (AC) source's instantaneous value on every substep, keeping its phase continuous from one game tick to the next.

An AC source is described by amplitude, frequency, and phase (`SineDrive(AmplitudeV, FreqHz, PhaseRad)`), but the solver only ever sees one instantaneous voltage at a time. The source driver is the update function that turns the description into that per-substep value: it advances an internal phase accumulator by an exact per-substep increment (derived from the frequency and the deterministic `TickClock`) rather than recomputing phase from wall-clock time, so the waveform never jumps or glitches at tick boundaries even when frequency changes (frequency is a piecewise-constant input, e.g. from a generator's mechanical rotor). For programmers: it is a small deterministic state machine whose only state is the accumulated phase. For EEs: it is the time-domain source evaluation step of a transient analysis, applied as a tier-1 (right-hand-side-only) update — no matrix refactorization. The client-facing entry point is `Drive(VSourceId, in SineDrive)`, documented in the API as the "phase-continuous source driver".

**Project-coined term**, closest standard concept: the transient source/waveform evaluation function in a SPICE-style simulator (e.g. evaluating a `SIN(...)` source at each timepoint).

**Where it appears:** `docs/solver.md` (Analyses — subcycled AC), `docs/api.md` §7 (`Drive(VSourceId, in SineDrive)`) and the AC walkthrough in §22.b.

**References:** Nagel (1975), *SPICE2: A Computer Program to Simulate Semiconductor Circuits*, UCB/ERL-M520 (time-domain independent-source waveforms).

### sparky
The team's earlier Vintage Story electrical prototype, kept in `third_party/sparky/` purely as a design reference — its ideas are reused, its code deliberately is not.

Sparky proved out several patterns that Manatee adopts by re-implementation: the layered architecture separating simulation from game glue, the typed-ID handle pattern (distinct strongly-typed identifiers instead of bare integers — like using differently-keyed connectors so you physically cannot plug the wrong wire in), incremental voxel-conductor compaction (recomputing only the changed part of the wire graph when a block changes), and a standalone "handbook" GTK editor that is the embryo of the tablet client. Its testing discipline also carries over: the persistent regression corpus and the BenchmarkDotNet benchmarking setup now shipped as `scripts/bench.sh`. Reusing sparky's *code* is an explicit non-goal in `docs/design.md`; manatee-core re-implements the numeric core from scratch (including a symbolic/numeric factorization split, per `docs/solver.md`). When docs say "sparky-style" or "carried over from sparky", they mean the design was validated there first.

**Project-coined term** (a codebase name, not a technical concept).

**Where it appears:** `docs/design.md` (Non-Goals — "Reusing sparky's code", System Overview, R11, Testing), `docs/compaction.md` (intro, Client Intake), `docs/testing-strategy.md` (Principles, Benchmarks, Game-Layer Testing), `docs/api.md` §3 (typed-ID pattern), root and `third_party/` CLAUDE.md files.

### SpiceDeck / DeckResult / Unrepresentable
Manatee's diagnostics emitter that converts one island of a live netlist into deterministic ngspice input text, returning a `DeckResult` that carries the text, a NodeId-to-rawfile-column map, and the list of components it could not express (`Unrepresentable`).

`SpiceDeck.Emit(netlist, islandId, options)` is the bridge between Manatee's world and the ngspice oracle: it writes a SPICE *deck* (the traditional name for a simulator input file, from the punched-card era) describing the island's circuit and the requested analysis (e.g. transient with `MatchBackwardEuler`, mapping to ngspice's `method=gear maxord=1` so both simulators integrate the same way). The returned `DeckResult` struct has three fields: `Text` — deck text with deterministic, slot-ordered names, so it doubles as a Verify golden and a stamp refactor shows up as a reviewable text diff *before* it becomes a numeric oracle delta; `NodeNames` — an explicit `(NodeId, column)` map into ngspice's rawfile output, so comparisons never guess at names; and `Unrepresentable` — the components that have no faithful SPICE equivalent. Behavioral two-ports (converters, swing-lite, boundary couplings) land in `Unrepresentable` rather than being approximated: the differ knows its own fidelity, and those devices are covered by conservation invariants and hand-computed inline cases instead of the oracle. Lesson-corpus tests assert `Unrepresentable` is empty — teaching circuits must be fully oracle-able. It is a cold-path API, never called inside a tick.

**Project-coined term** (the API triple); closest standard concepts: a *SPICE netlist/deck exporter* plus an explicit fidelity manifest. "Deck" itself is standard SPICE vocabulary.

**Where it appears:** `docs/api.md` §22.c (the ngspice oracle harness, with the full type signatures) and the `Manatee.Core.Diagnostics` namespace table; `docs/testing-strategy.md` Oracle Tests.

**References:** L. W. Nagel, "SPICE2: A Computer Program to Simulate Semiconductor Circuits", UCB/ERL-M520, 1975; the ngspice manual (rawfile format, `.tran`/`.op` analyses, Gear integration options).

### StaleHandleException / StaleHandleReads / sentinel
- The two faces of Manatee's stale-handle policy: in debug builds, using an expired handle throws a loud, self-describing exception; in release builds it silently degrades to a safe, defined placeholder value ("sentinel") and bumps a counter — so a shipped game never crashes on a stale read.
- Handles into the netlist are generational (slot + generation); when a rebuild or removal reissues a slot, old handles go stale. With `DebugLevel.Asserts`, stale use throws `StaleHandleException` carrying the handle kind, slot, expected vs actual generation, and the journal event that invalidated it — a compaction bug reads as *"stale ResistorId slot 41 gen 3, live gen 4, invalidated by IslandRebuilt seq 8123"*, not silent aliasing. In release, the same misuse becomes a defined sentinel: reads return 0.0, mutations no-op, `TryResolve` returns false, and `TickStats.StaleHandleReads` increments so the bug is still countable. For an EE: it is like a meter whose leads have fallen off a renumbered terminal block — on the bench (debug) an alarm names the exact terminal; in the field (release) the meter just reads zero and logs the fault rather than blowing up the panel. For a programmer: it is a use-after-free detector whose release-mode behavior is a checked, counted null-object rather than undefined behavior.
- **Project-coined term**, closest standard concepts: generational indices / handle validation (game-engine slot maps), fail-fast assertions degrading to the Null Object pattern in production.
- **Where it appears:** `docs/api.md` §3 (handle definition), §16 (uniform detection rule), §18 (re-pin contract), §20 (error-channel table); `docs/integration-tutorial.md` §6.
- **References:** Woolf, “Null Object” (in *Pattern Languages of Program Design 3*, Addison-Wesley, 1997) — the release behavior is a counted variant of the Null Object pattern; generational handles follow the widely used slot-map pattern from game-engine architecture.

### StaleHandleReads
- A per-tick counter in `TickStats` that records how many times a stale (expired) handle was used this tick; in a correct integration it is always zero.
- In release builds, reading or mutating through a stale handle is a defined no-op — the read returns 0.0, the write does nothing — and this counter increments instead of anything crashing. That makes it the observable symptom of exactly one bug class: a missed **re-pin** (the client failed to re-resolve its handles by `ExternalKey` after an island rebuild). The docs are emphatic that nonzero *always* means a re-pin bug, never a legitimate condition, and the tutorial's example asserts `StaleHandleReads == 0` on every tick; the test suite proves the assertion is load-bearing in both directions (skipping the post-Solve re-pin trips the counter). For an EE: it is a fault-event counter on a protection relay — the equipment rode through the fault, but any count above zero means the installation is miswired. For a programmer: it is a metric that converts silent use-after-free into a monitorable SLO.
- **Project-coined term**, closest standard concept: an error/telemetry counter over a null-object fallback path.
- **Where it appears:** `docs/api.md` §16 (sentinel semantics), §18 (re-pin contract), §9 (`TickStats` field; cross-referenced in the §20 error-channel table); `docs/integration-tutorial.md` §6 and §9 (the `stale=0` health line).

### StateKey
A client-chosen 128-bit identifier naming one unit of serializable dynamic state — a whole device, or a bare capacitor/inductor/sine source — used as the sole lookup key when saving and restoring circuit state.

Snapshot blobs key on `StateKey` and nothing else: at restore time, each blob entry finds its live state unit purely by this identity, which survives island rebuilds, merges, splits, and every other structural upheaval (it is the restore match key). It is deliberately distinct from `ExternalKey` (topological identity) because the granularities differ: one device — one `StateKey` — may own many components, each with its own `ExternalKey`; a bare stateful primitive may just reuse its ExternalKey bits via `StateKey.From(key)`. The API makes it a mandatory, non-optional parameter wherever state exists, so forgetting it is a compile error and device state can never silently cold-start. For an EE: it is like the reference designator ("C3", "B1") on a schematic — the stable name by which you know *which* capacitor's voltage a recorded number belongs to, regardless of how the netlist was renumbered. For a programmer: the primary key of the state table, owned by the client rather than the solver so it stays stable across process restarts.

**Project-coined term**, closest standard concept: a stable primary/foreign key for persisted state (database primary key; a schematic reference designator on the EE side).

**Where it appears:** `docs/api.md` §3 (definition), §14 (snapshot laws), §18 (device key-allocation contract); `docs/integration-tutorial.md` §3, §7.

### StateUnit / StateUnitCount / cold-start
A *StateUnit* is one saveable item of dynamic state under a `StateKey`; *StateUnitCount* is how many such items an island currently has; a unit is *cold-started* if, after loading, no saved blob ever supplied data for it and it therefore kept its fresh-build defaults.

Each StateUnit is one entry in the snapshot payload — a capacitor's voltage, an inductor's current, one device's `Tick` state, a fuse's melting integral. `IslandHandle.StateUnitCount` is the denominator for restore coverage: because `Restore` is additive (it only touches units its blob has entries for, never resetting the rest), no single call can tell you whether the load was complete. Instead, coverage is checked in aggregate — after *all* blobs have been offered, `sum(Matched)` versus `StateUnitCount` gives the number of units that genuinely cold-started. "Cold-started" is thus a *derived* fact, not an action: units are born at cold defaults when built, and a restore either overwrote those defaults or it did not — like a database row created with default column values that no import file ever updated. For an EE: a cold-started capacitor starts at 0 V initial conditions instead of its saved voltage, so an unexpectedly high cold-start count after loading a save means part of the circuit will show a spurious startup transient. The §22.a load loop logs this residue rather than discarding results.

**Project-coined terms**; closest standard concepts: a serialized state record (StateUnit), and default/zero initial conditions in circuit simulation (cold-start — cf. a SPICE transient run without a saved operating point).

**Where it appears:** `docs/api.md` §11 (`StateUnitCount` on `IslandHandle`), §14 (additive-restore law, aggregate coverage, `RestoreResult`); `State/StateUnit.cs` (`StateUnitKind` enum).

### SteadyStateGuard / EnterSteadyState
- The `ref struct` returned by `netlist.EnterSteadyState()` that, for the duration of the game's per-tick drive phase, forbids structural (tier-3) changes to the circuit and arms a memory-allocation tripwire (`AllocationSentinel`) — turning "someone rebuilt the world mid-loop" from a heisenbug into an assert. See **EnterSteadyState / steady-state guard** (Project-Coined Terms part 1) for the full entry: ref-struct scoping, the debug-assert / release-defer-to-`Dispose` behavior, `TickStats.DeferredStructuralOps`, the Unity/Mono self-disarm, and the CI enforcement.
- **Where it appears:** `docs/api.md` §8, §4, §16 (0B table); `docs/integration-tutorial.md` §9; ratified decisions §22.

### Subcycled AC / subcycling / substep
- Manatee's way of simulating AC: instead of one solver step per game tick, an AC island takes N smaller time-domain steps ("substeps") inside each tick so the sinusoid is actually sampled as a waveform — and, explicitly, this is *not* done in the Stationeers (DC) profile.

A game tick (50 ms in Vintage Story) is far too coarse to resolve a 5–50 Hz sine wave, so the solver divides the tick into N equal substeps, choosing N per island from its highest source frequency at ≥ 20 samples per cycle (5 Hz → 5 substeps per 50 ms tick; ~50 Hz → ~50 substeps). For a programmer: an inner loop running at a higher rate than the outer game loop, like a physics engine taking fixed internal steps per rendered frame. The key performance fact is that a *linear* island pays only tier-1 cost per substep — the matrix factorization is cached, so each substep is just a right-hand-side update plus back-substitution, not a re-factorization. The source driver updates each sine's phase per substep, phase-continuous across ticks, and N itself is quantized with hysteresis so it changes only rarely (a deliberate tier-2 event). Clients never set N; the solver owns it.

The split is a construction-time profile choice. The Stationeers integration runs pure DC with storage dynamics at dt = 0.5 s and performs **no subcycling** — one solver step per power tick, which is why its per-tick cost model is so simple; `Dc(dt)` never subcycles (a sine source there is legal but single-sampled and debug-warned), while the `Mixed` profile subcycles islands containing sine sources. Think of it as a sampling-rate decision baked into configuration, like choosing an audio buffer's sample rate up front.

**Project-coined term**, closest standard concept: oversampled time-domain transient analysis — an inner time-stepping loop nested inside an outer (game-tick) loop. "Subcycling" is also used in co-simulation literature for integrating one subsystem at a finer step than another.
- **Where it appears:** `docs/solver.md` (Analyses, "Subcycled AC (VS)"), `docs/design.md` R3 and Simulation Model, `docs/stationeers.md` (Performance: "no subcycling"), `docs/integration-tutorial.md` §2 (profile choice), `docs/api.md` §11 (`SubstepPlan`).
- **References:** Nagel (1975), *SPICE2: A Computer Program to Simulate Semiconductor Circuits*, UCB/ERL-M520 (time-domain transient analysis); Vlach & Singhal, *Computer Methods for Circuit Analysis and Design* (time-step selection in transient simulation).

### SubstepPlan / hysteresis band
- `SubstepPlan` is a small read-only record the solver publishes per island — `(int Substeps, double SubstepDt, double HysteresisBand)` — describing how that island is currently being subcycled; the hysteresis band is the tolerance that keeps the plan from flapping.

- The solver, not the client, owns the substep count: `N = ceil(maxFreq · acSamplesPerCycle · tickDt)`, with `SubstepDt = tickDt / N` (api.md §11 `IslandHandle.Plan`). N is re-decided only when the island's required sample rate drifts past ±`HysteresisBand` (0.15, i.e. ±15%) of the value N was last chosen for; the band is 0 for DC or non-AC islands. The record exists so clients can *observe* the plan (for diagnostics, benchmarks, and waveform-tap ring sizing) without being able to change it — there is deliberately no client-callable AC substep control. For an EE, this is a classic Schmitt-trigger arrangement: the mechanical frequency input jitters continuously, but the derived discrete setting only switches when the input leaves a dead band, avoiding chatter. For a programmer, it is a cached derived value with an invalidation threshold, exposed as an immutable snapshot.

**Project-coined term**, closest standard concepts: adaptive timestep selection with hysteresis / dead band (Schmitt trigger behavior). Note the deliberate inversion of usual SPICE practice: SPICE varies dt continuously per local truncation error, whereas Manatee *pins* dt between rare plan changes so cached LU factors and BE companion conductances stay valid.

**Where it appears:** api.md §11 (`IslandHandle.Plan`), §22.b (the VS AC island walkthrough), §23.4; solver.md "Subcycled AC (VS)" (the quantized-with-hysteresis rule).

### sun lamp
- A planned electrical grow-light device for the Vintage Story mod: a lamp that lets crops grow under artificial light, at a real power cost.

- It is gameplay motivation for sustained AC load (design.md progression arcs), but it carries a flagged design cost: vanilla Vintage Story crop growth reads **sunlight only** (`EnumLightLevelType.OnlySunLight`; the farmland growth factors are computed in a closed base class — `BEFarmland.cs:64-69, 119`), so simply emitting light does nothing for crops. The sun lamp must therefore override or wrap the farmland growth update — via a block-entity subclass or an attached behavior — and inject its artificial-light contribution into the growth calculation itself (vintage-story.md §3). For an EE, this is the software equivalent of a sealed instrument with no external input terminal: you cannot feed it a new signal, so you must replace the stage that reads the sensor. The device itself is otherwise an ordinary resistive-ish load on an AC island.

**Where it appears:** vintage-story.md §3 ("Sun lamps require custom growth code", with engine file:line evidence); design.md progression arcs and Risks ("Sun lamps need custom growth code").

### Survivor / absorbed island
- When two electrical islands are joined into one (e.g. a breaker closes between them), the **survivor** is the island whose identity persists and the **absorbed** is the island that is folded into it.

Manatee tracks each connected circuit as an *island* with its own `IslandId`. A merge picks one side as the survivor: its `IslandId` remains valid, while the absorbed side's `IslandId` becomes stale and must not be used again (the `IslandsMerged` journal event reports `A = survivor, B = absorbed`). Crucially, only the island label is renumbered — every component and node handle on *both* sides survives the merge unchanged, so client code holding a resistor or node reference keeps working. During the merge window (before the merged island first publishes a solution), both sides continue to read their pre-merge last-good voltages and currents, on either union orientation — a fix (2026-07-07) that prevents adaptors from seeing a fictional 0 V on the absorbed side and stamping a phantom dead short. For an EE: this is bookkeeping about which "circuit name" wins when two separate circuits become one; the physics is unaffected. For a programmer: it is a disjoint-set union where one representative is retained and the other is relabeled.
- **Project-coined term**, closest standard concept: the canonical representative in a union-find / disjoint-set-union merge.
- **Where it appears:** `docs/integration-tutorial.md` §6 (handle survival rules and the merge-window fix); `docs/api.md` §16–17 (`IslandsMerged` event, handle-survival table).
- **References:** Cormen, Leiserson, Rivest & Stein, *Introduction to Algorithms* (disjoint-set data structures), for the union-with-representative idea.

### swing-equation-lite
- The project's reduced model of generator rotor dynamics: each generator device carries a rotor phase angle and angular velocity, integrated with a spring-damper coupling to the network, and uses that angle to drive the phase of its electrical source.

Crucially for the architecture, this lives in the `devices` layer, **not** in the solver (solver.md, Analyses). Each tick the generator device integrates its own two-variable rotor state (angle, angular velocity) like a tiny physics engine — a spring pulling it toward lockstep with the network's electrical phase and a damper resisting oscillation — then pushes the resulting phase into its voltage source as an ordinary tier-1 parameter update. From the solver's point of view nothing special exists: it just sees a sine source whose phase changes between solves. For an EE: this is a heavily reduced swing equation (rotor inertia + synchronizing and damping torque), deliberately stripped of full synchronous-machine detail because the gameplay goal is teaching synchronization, not power-systems fidelity. For a programmer: the electromechanical feedback loop is a device-side state machine composed on top of the solver rather than a new solver feature — the same layering used for batteries and behavioral two-ports.
- **Project-coined term**, closest standard concept: the swing equation of synchronous-machine (rotor-angle) dynamics.
- **Where it appears:** `docs/solver.md` (Analyses, "Generator paralleling"), `docs/design.md` (Simulation Model), `docs/api.md` §18 (`Alternator` built-in device: "swing-lite rotor state → `Drive(SineDrive)`").
- **References:** Kundur, *Power System Stability and Control* (McGraw-Hill, 1994) — the standard treatment of the swing equation this model reduces.

### tablet / gaming tablet / tablet host
- Three names around one artifact: the **tablet** (or **gaming tablet**) is the in-game 2D schematic simulator itself; the **tablet host** is the Vintage Story client-side integration that embeds and runs it.

The tablet is a ruins-loot relic hosting guided lessons plus a free-play sandbox, running its own solver islands separate from the world simulation. The tablet host (vintage-story.md §6) is the surrounding plumbing with settled defaults: client-side only (lessons never touch the server — core determinism guarantees lesson goldens behave identically in-game and in CI); a fixed simulation dt stepped from the client tick via an accumulator, keeping the tablet on the solver's tier-1 fast path; paused while the GUI is closed, resuming from a snapshot; and persistence of lesson progress and sandbox circuits as Falstad text riding player attributes. For programmers: the host is the adapter/runtime shell around a pure embeddable engine — think of a container hosting an application. For an EE: the tablet is the instrument; the host is the bench power, enclosure, and data logging around it.
- **Where it appears:** vintage-story.md §6, The Tablet Host; design.md, The Three Clients and R16; api.md §5, `NetlistOptions.Tablet()`.

### Tablet islands (decoupled solver islands)
- The circuits a player builds inside the Vintage Story "gaming tablet" run as their own solver islands, completely separate from the circuits of the game world.

An *island* is an electrically self-contained circuit that the solver treats as an independent problem (its own matrix, its own timestep). The tablet — an in-game 2D schematic simulator used for lessons and sandbox play — feeds its schematics into the same manatee-core solver as the voxel world, but its islands never connect to world islands: a wire drawn on the tablet screen shares no node with a cable in the ground. For a programmer, this is process isolation: same engine, disjoint state, no shared memory. For an EE, it is simply two circuits with no galvanic connection — a bench simulator that happens to use the production solver. The payoff is that lessons behave identically to the world (one solver, one set of physics) while a tablet experiment can never brown out or blow up anything the player actually built.

**Project-coined term**, closest standard concept: independent connected components of a circuit graph, each solved as a separate system (what SPICE would treat as separate circuits/decks).

**Where it appears:** `docs/design.md` — "The Three Clients" (client 2, the gaming tablet: "Runs its own solver islands, decoupled from the world"); island mechanics in `docs/solver.md` and `docs/api.md`; the tablet engine itself in `docs/harness.md`.

### text annotation (`x`)
The Falstad/circuitjs1 element type `x`: a free-floating text label placed on the schematic canvas, carrying no electrical meaning.

Its file-format line is `x <position/flags fields> size text`, and the text payload has **two encodings** distinguished by flag bit 4 (`FLAG_ESCAPE`, i.e. `flags & 4`). New style: the text is a single escaped token using circuitjs1's escape scheme (see *text escaping*), so spaces inside the label become `\s` and the tokenizer sees one word. Old style (no flag): the label was simply written with its spaces, so a reader must join all remaining tokens on the line back together with spaces, additionally decoding `%2b` back to `+` (because `+` is a tokenizer delimiter in this format). For programmers this is a classic serialization-versioning hazard: the same field has two wire encodings selected by a flag bit. For EEs: it is just a silkscreen label — decoration on the schematic, never part of the netlist's electrical graph. Manatee's importer accepts both encodings.

This is standard circuitjs1 format vocabulary (source: `TextElm.java`), not project-coined.

**Where it appears:** `docs/falstad-format.md` §4 (element table, `x` row; text-escaping paragraph) and §5 (Versioning quirks, quirk 5); importer accept list in the same doc.

### the EA trick
Manatee's (inherited-from-Electrical-Age) practice of choosing component values so that circuit time constants land in the 0.1–10 s range — slow enough to watch with the naked eye and slow enough that the solver's coarse 50 ms time steps stay accurate.

Real electronics often changes state in microseconds; a game that ticks its electrical simulation every 50 ms (and a player watching the screen) can resolve neither. Instead of speeding up the solver, the trick scales the components: pick R, L, and C values whose RC/RL time constants live at human-visible scales, so charging a capacitor takes seconds, not microseconds. This simultaneously keeps Backward Euler — the numerical integration method, whose error grows when the step size is large relative to the time constant — accurate at ~50 ms steps: for programmers, it is like choosing a polling interval well below the rate of change you need to observe, except run in reverse (slow the phenomenon to fit the fixed polling rate). Electrical Age proved the pedagogy works at these scales, and the technique is now documented in EA's own examples README. In Manatee, curriculum lessons state their scaled values per lesson.

**Project-coined term**, closest standard concept: component-value scaling / time-scaling of a circuit so the integration step is small relative to every time constant (no single standard name; related to step-size vs. time-constant accuracy analysis for implicit integration).

**Where it appears:** `docs/design.md` (Simulation Model, transient/VS bullet), `docs/curriculum.md` (Format section: "Component values follow the EA trick").

### The four verbs
- The complete set of ways a client is allowed to change a live circuit: `Drive`, `Adjust`, `Reconfigure`, and `Edit` — every mutation of a `Netlist` is exactly one of these.

The API deliberately has no other mutation surface, so the grammar doubles as a cost model. `Drive` (tier 1) changes only what a source is pushing — for an EE, turning the knob on a supply; for the solver, a right-hand-side update solved by back-substitution on a cached factorization. `Adjust` (tier 2) changes a component's value (a resistance, a capacitance, a switch state) — the matrix's numbers change but its sparsity pattern does not. `Reconfigure` (tier 3-lite) opens or closes an existing coupler, i.e. changes which islands are connected. `Edit` (tier 3) is structural construction/demolition — adding or removing components — and is only reachable through an explicit `StructuralEdit` batch scope you must ask for loudly. Because the verbs are named methods, a reviewer can grep for `.Adjust(`, `.Reconfigure(`, `.Edit(` and mechanically audit how expensive a client's tick loop is, without understanding the circuit math. All verbs write the retained circuit *document*; matrices are derived from it at Solve, so a verb call is never lost even if the island is about to rebuild.

**Project-coined term**, closest standard concept: a command/tiered-mutation API over a retained-mode document; the tiers correspond to SPICE-style distinctions between changing source values, element values, and netlist topology.

**Where it appears:** `docs/api.md` §1 (ground rules) and §4 ("The four verbs + tier-0 Meta"); the methods live on `Netlist` in `Manatee.Core`.

**References:** Gamma, Helm, Johnson & Vlissides, *Design Patterns* (1994) — the Command pattern is the nearest programming ancestor of "every mutation is a named, auditable operation".

### The tablet
- An in-game 2D schematic circuit simulator — a "gaming tablet" found as ruins loot in Vintage Story — that teaches real electricity through guided lessons and a free-play sandbox, and whose engine doubles as the project's primary development and test harness.

Fictionally it is a surviving educational device from the world-before, which fits Vintage Story's fallen-civilization setting. Practically it is a Falstad-style schematic editor (netlist import per `docs/falstad-format.md`) running its own solver islands, fully decoupled from the voxel world: crashing a lesson circuit cannot brown out your base. The lesson arc (17 lessons, DC fundamentals → RC/RL → AC → transformers → power factor → synchronization; `docs/curriculum.md`) "just happens" to be a tutorial for both the mod and real electricity, in predict-then-observe form. Per requirement R16, finding the tablet may gate advanced recipes but completing lessons gates nothing, and the vanilla survival handbook remains the teaching floor — the tablet is the ceiling. For an EE: think of it as a shipped, story-wrapped circuit-simulator front end (like CircuitJS) whose example library is also the project's regression-test suite — every lesson is a CI golden.

**Project-coined term** (one of "the three clients" alongside the VS voxel world and Stationeers/Re-Volt).

**Where it appears:** `docs/design.md` ("The Three Clients", R16, "The tablet" section), `docs/curriculum.md`, `docs/harness.md` (the underlying `Manatee.Schematic` engine).

### Thermal envelope as Pareto set
- The rule that when a series chain of conductors is collapsed into one equivalent resistor, its thermal (i²t) limit is carried as a *set* of undominated `(rating, meltI2t, tau)` triples from the original segments — never blended into a single hybrid number.

The naive collapse — take the minimum rating from segment X and the minimum melt threshold from segment Y — creates a fictional component that trips when no real segment would, violating the invariant that reduced and raw circuits behave identically (and the design rule that every hazard must trace to a player mistake). The fix exploits physics: a series chain carries one shared current, so integrating each segment's accumulator against its own rating is *exactly* raw behavior. The engine therefore keeps one integral per surviving pair and trips when any pair trips; the event's `PairIndex` names the culprit segment for attribution. Pairs that can never trip first are pruned: pair *d* retires pair *s* only if *d*'s rating ≤, melt ≤, AND *d* cools no faster than *s* — tau participates because a slower-cooling pair can out-ratchet a nominally weaker one under pulsed load (`tau ≤ 0`, never-cools, ranks as +∞, the slowest). For a programmer: it is the Pareto frontier of a multi-objective comparison, like keeping only cache entries no other entry beats on every axis. The set is bounded by distinct materials in the chain, so k stays small. A standing CI test (LimitEventEquivalenceTests) asserts raw-vs-reduced first-trip equivalence.

**Project-coined term**, closest standard concept: **Pareto frontier / non-dominated set**, applied to fuse time-current characteristics.

**Where it appears:** `docs/api.md` §12 (`Meta.SetThermalEnvelope`, `I2tPair`) and §19 (2026-07-06 ruling + implementation contract); `docs/compaction.md` (limit attribution, responsibility #4).

### Thermal-RC reuse
- The project's intended second life for the solver core: because thermal networks are mathematically RC circuits, the same engine could someday simulate heat instead of electricity.

design.md notes that a possible future chemical/phase-change arc (Stationeers-style) could feed temperature and heat-flow networks into the existing MNA solver unchanged, with temperature standing in for voltage and heat flow for current. This is explicitly *noted, not designed* — deferred by design alongside the chemistry arc. Its only binding consequence today is a coding convention: the deepest solver layers avoid electrical-only naming where cheap (node **potential** / **flow** rather than voltage / current), so nothing structural has to be renamed later. The public API even carries a domain-neutral alias, `Solution.Potential(NodeId)`, for exactly this reason. For an EE: think of it as keeping the schematic symbols generic so the same drawing can later be read as a thermal diagram. For a programmer: it's the cheap half of generalization — neutral names now, no abstraction machinery until a second client actually exists.

**Project-coined term**, closest standard concept: applying the thermal–electrical analogy via a shared network solver.
- **Where it appears:** `docs/design.md` (future arc note, "Deferred by design"), `docs/solver.md` (Layering), `docs/api.md` §1 and `Potential()` alias, api.md open-questions item on `ConductorSpec.OhmsPerLength` naming.

### tick loop
Manatee's fixed-order sequence of operations executed once per game tick — the prescribed shape of every integration's per-tick body.

The order is: **topology** (apply queued cuts, breaker `Reconfigure`s, edits) → **re-pin** (drain island-membership changes and re-resolve device handles by key) → **drive under guard** (inside `EnterSteadyState`, every device adjusts its sources/conductances) → **one `Solve`** for all islands → **churn-tick re-pin** (re-resolve again, because island rebuilds run *inside* Solve and reissue every member handle) → **readback** (devices read the published `Solution`). The order is load-bearing, like the phases of a two-phase commit: topology must precede drive so this tick's solve reflects this tick's world; the guard makes accidental structural work impossible during drive; and skipping the churn-tick re-pin means readback dereferences exactly the handles this tick's Solve invalidated. It is one *global* body per game tick — never one solve per network — and the executable fake proves the ordering (with it, `StaleHandleReads == 0` across a scripted run; without it, cut ticks trip the counter).

**Project-coined term**, closest standard concept: a fixed-timestep game loop body with an explicit phase protocol (compare read-modify-write phases in double-buffered simulation loops).

**Where it appears:** `docs/integration-tutorial.md` §5 ("The tick loop", the heart of the integration); `docs/api.md` §17 (canonical tick order) and §22.a (Re-Volt walkthrough); runnable in `examples/RevoltWalkthrough/`.

### TickClock
A tiny two-field value — `(TickIndex, Dt)` — passed into every `Solve` and `Step` call, telling the solver which tick this is and how long it lasts.

Declared as `public readonly record struct TickClock(long TickIndex, double Dt)` in `Manatee.Core`. `Dt` is the integration timestep for this tick (0.5 s Stationeers, 50 ms VS); `TickIndex` is a monotonically increasing tick counter. Carrying both explicitly serves two goals stated in the API: **determinism** — the solver derives simulated time from the counter rather than any wall clock, so a replayed sequence of identical calls produces bit-identical results — and **AC phase continuity** — a sine source's phase is computed from accumulated tick time, so sinusoids stay continuous across ticks instead of restarting each solve. For a programmer, it is a value object threading time as an explicit parameter instead of hidden global state; for an EE, it is the simulation timebase handed in from outside, like an external clock reference, guaranteeing the waveform generator never glitches its phase between samples. In per-island threading, the same `TickClock` goes to each island's `Step`.

**Project-coined term**, closest standard concepts: the simulation timestep/timepoint bookkeeping of a transient analysis (SPICE's internal timebase), plus the logical-clock idea of ordering by counter rather than wall time.

**Where it appears:** `docs/api.md` §4 (`Solve(in TickClock)`, definition near line 248), §11 (`IslandHandle.Step(in TickClock)`), §22 walkthroughs (`_clock.Next(0.5)`, `new TickClock(i, 0.05)`).

### TickStats
A small struct of per-tick counters that reports what work the solver actually did during the last `Solve`, turning the solver into a monitorable system.

After every tick, `Netlist.LastTickStats` exposes counters such as `Substeps`, `RhsSolves` (tier-1 back-substitutions), `Refactorizations` (tier-2 numeric refactors), `IslandRebuilds` (tier-3), `NewtonIterations`, `AdjustNoOps` (parameter writes small enough to skip refactoring), `StaleHandleReads` (safe-but-suspicious reads through invalidated handles), `DeferredStructuralOps` (structural edits the release-mode guard postponed), and `BytesAllocated` (heap allocation, expected 0 in steady state). For a programmer this is standard service telemetry — the equivalent of request/cache-miss/GC counters on a server, and CI asserts budgets against it. For an EE, think of it as a panel of counters wired onto the simulator itself: instead of trusting that a circuit change was "cheap", you read off exactly how many expensive operations (matrix refactorizations, rebuilds) it triggered. Counters accumulate per scheduling unit under the single-writer-per-island rule and are aggregated in deterministic island order at a defined readback point; reading mid-tick is a phase error.

**Project-coined term**, closest standard concept: performance counters / telemetry metrics (cf. SPICE's per-analysis operation statistics).

**Where it appears:** `docs/api.md` §9 (definition, thread-safety, tier-budget assertions), §21 (shared-structure mutation windows), §22.a; `TickStats` struct in `Manatee.Core`.

### TickStats / LastTickStats
`LastTickStats` is the property on `Netlist` that hands back the `TickStats` counters for the most recently completed tick — the ground truth for whether that tick stayed in its expected cost tier.

The integration tutorial's rule is "TickStats is the truth": log `LastTickStats` every tick and interpret it directly. `Refactorizations > 0` in steady state means some device parameter is still moving (an `Adjust` not converging); `IslandRebuilds > 0` means a coupler or wire cut fired; `StaleHandleReads > 0` is always a re-pin bug in the host mod; `AdjustNoOps` makes converged devices visible (their writes cost nothing); `DeferredStructuralOps > 0` means client code attempted a structural edit at an illegal time and the release guard deferred it. Reading it is cheap — a `ref readonly` struct read, no allocation — so game mods log it per tick for free. The analogy for an EE: it is the bench multimeter permanently clipped across the simulator; for a programmer: the health-check endpoint you alert on.

**Project-coined term**, closest standard concept: per-iteration run statistics of a simulator, exposed as an API.

**Where it appears:** `docs/integration-tutorial.md` §1, §5, §9 ("TickStats is the truth" sharp-edges appendix); `docs/api.md` §9 (`Netlist.LastTickStats`, aggregation barrier).

### tier 1 / tier 2 / tier 3 / fast path / change-cost tier
Shorthand adjectives for the three change-cost tiers — the project's public taxonomy (design.md R4) of how expensive a given change to the circuit is for the solver. In MNA the circuit is a matrix equation `A·x = b`: `A` encodes the network's conductances and topology, `b` (the right-hand side, RHS) encodes source values and companion-model history terms.

**Tier 1 (RHS value) — the fast path:** only a source value or a companion-model history term changed; the earlier LU factorization of `A` is still valid and the new solution costs a single forward/back substitution — a cache hit versus a rebuild, the same trick as reusing a compiled query plan with new parameters. This happens every tick/substep. Because the solver runs with fixed timestep `dt`, capacitor/inductor companion conductances stay constant too, so a linear island in steady operation never leaves tier 1 — the Vintage Story tablet host relies on exactly this ("fixed dt keeps the tablet on the tier-1 fast path"). Counted as `TickStats.RhsSolves`. **Tier 2 (conductance):** a component's conductance changed (switch toggle, variable resistor, a Newton iteration, a fuse blowing); the matrix's numeric values are refactorized, but its sparsity pattern (the symbolic analysis) is reused — a partial cache invalidation. **Tier 3 (topology):** components or nodes were added or removed; the island's matrix is rebuilt from scratch, symbolic and numeric, batched via `BeginBulkUpdate` — a cold rebuild. Every mutator in the API is documented with its tier, and events are described as "a tier-2 event" etc. throughout the docs. EE framing: tier 1 changes the excitation of a fixed network, tier 2 changes element values in a fixed topology, tier 3 changes the network itself.

**Project-coined term**, closest standard concept: SPICE-family solve-reuse levels — RHS-only re-solve on a cached LU, numeric refactorization with reused symbolic pattern (KLU-style `refactor`), and full symbolic+numeric rebuild.

**Where it appears:** `docs/solver.md` (Change-Cost Tiers table and throughout); `docs/design.md` R4; `docs/vintage-story.md` (Tablet Host, fixed-dt note); `docs/testing-strategy.md` (standing tier-1/tier-2 benchmark suites); `docs/api.md` §9 (per-tier counters, `TickStats.RhsSolves`).

**References:** Ho, Ruehli & Brennan (1975), "The Modified Nodal Approach to Network Analysis", IEEE Trans. Circuits and Systems (the MNA formulation); Golub & Van Loan, *Matrix Computations* (LU factorization and back-substitution cost); Davis & Palamadai Natarajan (2010), "Algorithm 907: KLU, A Direct Sparse Solver for Circuit Simulation Problems", ACM Trans. Math. Software 37(3) (the symbolic/numeric refactorization split); Nagel (1975), SPICE2 memo UCB/ERL-M520 (factorization-reuse practice).

### Tier-3 island rebuild
- The most expensive kind of solver update — reconstructing an island's circuit graph after its wiring topology changes — which Manatee runs on a background task instead of inside the game's simulation tick.

Manatee grades circuit changes by cost: tier 1 (value tweaks) and tier 2 (switch flips and similar structural toggles) are cheap enough to run in-band, but tier 3 means the connectivity itself changed — cables added or removed — so the island's node graph, compaction, and matrix structure must be rebuilt from scratch. In the Stationeers integration this work (and the load-time "rebuild everything" case when a save loads) is offloaded to a background task with a one-tick handoff: the network keeps running on the previous solution (or the scalar fallback) until the rebuild lands. For a programmer this is a classic cache-rebuild-off-the-hot-path pattern; for an EE, think of it as re-deriving the network equations only when the schematic itself changes, while serving stale-but-consistent results in the meantime.
**Project-coined term**, closest standard concept: incremental re-analysis after topology change (full symbolic refactorization of the MNA system), scheduled asynchronously.
- **Where it appears:** `docs/stationeers.md` (Performance, and the `Initialize_New` → tier-3 mapping); change-cost tiers defined in `docs/solver.md`; benchmarked as the "bulk topology build" suite in `docs/testing-strategy.md`.

### tier budget (CI assertion)
The project's steady-state performance model made executable: CI tests that assert, from `TickStats`, that a circuit in steady operation performed zero expensive solver work.

The standing test drives a linear AC island into steady operation and then asserts `Refactorizations == 0`, `IslandRebuilds == 0`, and `BytesAllocated == 0` on `LastTickStats`. That encodes the load-bearing performance claim — "a linear island in steady state lives entirely in tier 1" — as a hard pass/fail check rather than a hope, so any regression that silently bumps steady-state work into tier 2 or 3 (or starts allocating per tick) breaks the build. For a programmer this is a performance regression test built on counters instead of wall-clock time, which makes it deterministic across machines (the `AdjustEpsilon` no-op gate is deliberately bit-identical across architectures so these counts don't diverge between x64 and arm64 CI). For an EE: it is a spec-compliance test on the simulator itself — like asserting a supposedly quiescent circuit draws zero current — where the "current" is refactorizations and memory allocation.

**Project-coined term**, closest standard concept: counter-based performance budget assertion in CI (a form of performance regression testing).

**Where it appears:** `docs/api.md` §9 (the assertions and the AdjustEpsilon determinism rationale), §22.a, §22.c; `docs/testing-strategy.md` standing suites.

### TopologyBuilder
A component of Sparky — our earlier Vintage Story prototype — that turned voxel-level cable geometry into circuit topology; kept as the proven *design reference* for Manatee's compaction layer on the VS side.

Sparky lives in `third_party/sparky/` and its code is explicitly not reused; what carries over is the shape of the solution: how to walk voxel geometry, group equipotential conductor voxels into regions, and maintain that grouping incrementally as players place and break blocks. Manatee's reduction/compaction layer (region building via union-find, series collapse, probe interpolation) is the re-design of the same job with the lessons baked in. For an EE, the analogy is a validated breadboard prototype you keep on the shelf while laying out the production PCB — you copy the topology decisions, not the board. For a programmer, it is prior art consulted for architecture, not a dependency.

**Where it appears:** `docs/compaction.md` intro ("Sparky's `TopologyBuilder` is the proven design reference for the VS side"); `third_party/sparky/` per `third_party/CLAUDE.md`.

### TopologyJournal / journal
Manatee's record of every structural change to the netlist: a fixed-capacity ring buffer of small, fixed-size event structs, each stamped with a monotonically increasing sequence number (`Seq`).

Derived layers — the reduction/compaction layer, the VS wire renderer, debug tooling — stay consistent with the netlist by *replaying* these events through their own independent read cursors, never by receiving callbacks. Events cover component/node/probe add/remove and island lifecycle (`IslandCreated`, `IslandsMerged`, `IslandRebuilt`, `IslandRemoved`, `IslandFaulted`, `IslandRecovered`); each entry carries the client-meaningful `ExternalKey` and exactly two island-id fields. Because the ring is fixed-size, a reader that falls too far behind is "lapped": it detects its **own** overflow (`Overflowed(cursor)`) and falls back to a full resync — explicit, never a silent gap. Even a single oversized commit that laps its own window is flagged up front (`EditReceipt.WindowLapped`). A binding completeness rule: every island that ceases to exist appears in exactly one terminal event, even when the matrix work was coalesced away. Game clients ignore the journal entirely — their re-pin trigger is `IslandTable.DrainChanges` / `EditReceipt`. EE analogy: a chart recorder logging every wiring change on the bench, so anyone with a copy of yesterday's schematic can bring it up to date by reading the tape forward; if the tape has been overwritten, you re-survey the bench from scratch.

**Project-coined term**, closest standard concepts: event log / change journal (as in journaling filesystems and event sourcing), implemented as a single-writer multi-reader ring buffer.

**Where it appears:** `docs/api.md` §15 (full contract), §4 (`Netlist.Journal`), §16 (handle survival on the events it reports); consumed by the compaction layer (`docs/compaction.md`).

### Torque
Rotational force on a shaft — the quantity Vintage Story's mechanical power network sums and integrates, and the physical currency through which Manatee's AC generation couples to windmills and waterwheels (R14).

In VS's mechanical model, each node implements `IMechanicalPowerNode.GetTorque(tick, speed, out resistance)`: sources return signed torque, consumers return zero torque plus a positive `resistance`; the network sums both across nodes (scaled by gear ratio) and integrates shaft speed with a momentum/drag model. Manatee's **alternator** is a consumer whose resistance is the *counter-torque* — it grows with electrical load, so drawing more current physically slows the waterwheel (Lenz's law made playable); shaft speed × pole pairs sets the AC frequency. **Motors** do the reverse, producing torque from electrical input. For a programmer: torque is the flow variable of a second simulation loosely co-simulated with the electrical one — mechanical speed is treated as piecewise-constant per electrical tick, and the counter-torque reported back is normatively the *mean over all electrical substeps* since the last mechanical read, because a point sample would alias the 2×-line-frequency power pulsation into a phantom DC torque offset. This is standard rotational mechanics vocabulary; the project-specific content is the coupling and averaging contract.

**Where it appears:** `docs/vintage-story.md` "Mechanical Network Coupling (R14)" (with VS engine file:line references); `docs/design.md` R14.

**References:** Horowitz & Hill, *The Art of Electronics* (generators, back-EMF and loading); Fitzgerald, Kingsley & Umans, *Electric Machinery* (electromechanical energy conversion, torque–load coupling).

### TryResolve / TryResolveNode / TryResolveCoupler / TryResolveProbe
- The four lookup methods that turn a client's durable name (`ExternalKey`) back into a live solver handle (`ComponentRef`, `NodeId`, `CouplerId`, or `ProbeId`), returning `false` on a miss instead of throwing.

Manatee hands out cheap integer-style handles that can be **invalidated** when the netlist is rebuilt (topology commits, reduction, save/load, drift resync — the survival rules are tabulated in api.md §16). The `TryResolve*` family is the recovery path: the client stamped each object with a stable `ExternalKey` at creation, and after any invalidating event it asks the netlist to map that key back to a fresh handle. For an EE, it's like relocating a test point by its label after the board has been re-laid-out: the label persists, the physical coordinates don't. The `Try` prefix is the standard C# convention (as in `Dictionary.TryGetValue`): the method reports success as a boolean and writes the result to an `out` parameter, so a missing key is an ordinary answer the caller handles (cold start, device removed), never an exception. All four are 0B — zero heap allocation after warmup — so schedulers may call them every tick, and they are safe for concurrent reads.

**Project-coined term** (the specific method family), closest standard concepts: the C# `TryParse`/`TryGetValue` pattern combined with stable-external-ID re-resolution (a form of handle/indirection table lookup).

**Where it appears:** `docs/api.md` §4 (the `Netlist` facade, "Identity re-resolution"), §16 (handle-survival table and stale/removed-handle sentinel), §20 (key-not-found: `TryResolve*` returns false); exercised end-to-end in `examples/RevoltWalkthrough/` and `docs/integration-tutorial.md`.

### TutorialLoad / RevoltAdaptor
- The reference implementation of a device that survives on a *churning* island: it keeps its identity in permanent keys and refreshes every cached handle via a `Repin` method after each island rebuild.

`RevoltAdaptor` lives in the test suite; `TutorialLoad` (`examples/RevoltWalkthrough/TutorialLoad.cs`) is a commented copy of it that the integration tutorial narrates. The pattern answers a specific hazard: anything attached to a cable graph that suffers cuts and breaker trips will have its component and node handles invalidated at the next Solve, so the device must be able to re-resolve everything it holds. `Repin(net, graph)` does exactly that — `TryResolve(Key)` for the component handle, `graph.PortNode`/`ReferenceNode` for the terminal nodes, and `RegisterDeviceState` to re-anchor its saved-state unit. Re-pinning is pure key re-resolution (tier 0, no structural edit), so it cannot trigger the rebuild it is recovering from, and it is idempotent. Contrast with built-in devices like `AdaptedLoad`, which cache handles privately, expose no re-pin surface, and are therefore only safe on static islands. For the EE: the keys are the schematic reference designators; `Repin` is re-reading the layout after a board respin to find where each designator landed.

**Project-coined term** (example/test class names); closest standard concepts: the Adapter pattern plus handle/lease renewal after cache invalidation.
- **Where it appears:** `docs/integration-tutorial.md` §4 ("Devices: two populations, one contract") and the `RepinActive` loop in §5-6; `examples/RevoltWalkthrough/TutorialLoad.cs`.
- **References:** Gamma, Helm, Johnson & Vlissides, *Design Patterns* (Adapter).

### Two-probe contract
- The agreement between the UI and the solver that at most **two** oscilloscope probes sample per-substep waveforms at the same time.

Ordinary probes are cheap named read-points, but an oscilloscope probe subscribes to *per-substep* sampling into a ring buffer — a store on every AC substep, potentially hundreds per game tick. The contract caps that hot-path cost: two simultaneous waveform taps is the expected UI maximum, chosen because two is exactly what the pedagogy needs (phase-comparison lessons require seeing a *pair* of waveforms against each other) and enough to keep the cost bounded and predictable. For an EE: this is like a real two-channel scope — the instrument's channel count is a deliberate resource limit, not a physics restriction. For a programmer: it is a rate-limit baked into the API contract so the observability feature cannot blow the per-tick budget. Sampling costs one struct store per substep and zero allocation.

**Project-coined term**, closest standard concept: a two-channel oscilloscope, expressed as an API resource bound.
- **Where it appears:** solver.md (Probes), api.md §13 (`WaveformTap`), design.md (doc-review round: "the oscilloscope contract is two probes"), vintage-story.md (tablet scope UI).

### Two-wire idiom
- The default Vintage Story wiring style: laying a cable routes a conductor *pair* — supply and return — in one gesture, so no circuit depends on the earth as its return path.

Because R13 (frictionless placement) makes the pathfinder route pairs automatically, the extra conductor costs the player no interaction — the mandate "building must not be the hard part" absorbs it. The consequence for the solver is that islands **float**: both terminals of every device connect to real routed nodes, there is no inherent ground, and the matrix is kept well-posed only by an implicit high-resistance (~1 MΩ) leak from each device negative to earth, benchmarked at negligible conditioning and performance cost. This default was a same-day *reversal* of an earlier SWER-first design, after adversarial review showed the arithmetic fails at 12 V (tens-of-ohms electrodes starve any real load). For an EE: this is just ordinary two-wire circuit practice made the world's default, with SWER demoted to a deliberate choice. For a programmer: the pair-routing is an abstraction that keeps the physical model honest without adding user-facing friction.

**Project-coined term**, closest standard concept: conventional two-conductor (line + return / metallic-return) wiring.
- **Where it appears:** design.md (R13, Grounding model), api.md §5 ("VS: two-wire; supply AND return are real routed nodes, so islands float") and §22.b (worked VS island example).

### validation mode
- A debug mode that rebuilds the compacted circuit from scratch in the background and diffs the result against the incrementally-maintained version, logging any mismatch.

Manatee maintains its reduced (compacted) netlist incrementally as players add and remove cables, because that is fast; but incremental maintenance against a live stream of game mutations will inevitably have edge-case bugs. Validation mode exploits the fact that a from-scratch rebuild is always available and cheap at island scale: it runs the full rebuild alongside the incremental state and compares the two, so a divergence becomes a logged bug report instead of silent "mystery corruption" of the simulation. It ships as an in-game debug config and runs continuously in CI as the incremental-equivalence check. For the EE: this is like periodically re-deriving a circuit's reduced equivalent from the full schematic to confirm your running shorthand hasn't drifted. For programmers: a shadow-write/read-compare consistency check, the same pattern as dual-writing to a new datastore and diffing before cutover.

**Project-coined term**, closest standard concepts: shadow testing / differential testing (comparing an optimized implementation against a trusted reference oracle).

**Where it appears:** `docs/compaction.md` §Incremental Maintenance ("Resync backstop"); `docs/testing-strategy.md` (incremental equivalence).

### vanilla 4-call power API / four inconsistently-overridden calls
- The docs' full characterization of Stationeers' device power interface: four virtual methods — available/needed and provide/draw, in joules per half-second — which stock devices override inconsistently.

"Inconsistently-overridden" is the load-bearing caveat: each Stationeers device class is free to override any subset of the four calls with its own semantics, so there is no single uniform contract the adaptor can assume — it must treat the quartet as the observable behavior of each device and translate that into solver terms. The adaptor's translation (stationeers.md): a legacy load's *needed* power becomes a conductance G = P_wanted/V_prev² stamped into the matrix (a companion-model linearization that converges over a few ticks), legacy generators become voltage sources with a current limit derived from their *available* power, and post-solve the adaptor reports actuals back through provide/draw so vanilla bookkeeping stays true. For the EE: the game's interface is pure energy accounting with no notion of voltage — the adaptor is where P-per-tick meets I = P/V nonlinearity. For programmers: it is duck-typed legacy behavior wrapped by an adapter that owns the impedance mismatch (literally).

**Project-coined phrasing**; closest standard concepts: the Adapter pattern over a legacy interface, plus SPICE-style companion-model linearization of a nonlinear (constant-power) load.

**Where it appears:** `docs/stationeers.md` §The Legacy-Device Adaptor (opening paragraph); `docs/design.md` R18.

**References:** Pillage, Rohrer & Visweswariah, *Electronic Circuit and System Simulation Methods* (companion models / linearization of nonlinear elements); Gamma, Helm, Johnson & Vlissides, *Design Patterns* (Adapter).

### Verified-floating ritual
- The taught safety procedure for isolated (floating) electrical systems: touching one wire of a floating system is safe *only after* a wire-to-earth multimeter check confirms the system really is still floating.

A floating system (fed through an isolation transformer, with no earth reference) cannot shock you through a single point of contact — a shock needs two points, and earth is not one of them. But that safety property is an invariant that can silently break: in our Vintage Story world, a rain-wetted bare span can re-ground the system without any visible change. The ritual is therefore "measure, then trust": the multimeter reads wire-to-earth voltage before any touch, and the advanced build adds a continuous isolation monitor (a lamp pair to earth — actual historical practice). For programmers: the safety claim is a cached precondition, and the meter check is the validation you run because the cache can be invalidated by the environment behind your back. This is a curriculum/pedagogy concept, not a solver feature — the solver just makes the physics honest enough for the ritual to matter.

**Project-coined term**, closest standard concept: isolation verification / live-dead-live testing on IT (isolé-terre / unearthed) systems; the lamp-pair monitor corresponds to a real-world insulation monitoring device (IMD).
- **Where it appears:** `docs/curriculum.md` (Appendices: the hazard reference); `docs/design.md` § Grounding model (floating systems and the isolation monitor).
- **References:** IEC 60364 defines the IT earthing system and insulation monitoring requirements; Horowitz & Hill, *The Art of Electronics*, discusses grounding and isolation practice.

### voxel cable
- A voxel cable is Manatee's Vintage Story cable: an electrical conductor built from ordinary chiselable microblock voxels whose physical properties are taken seriously.

Rather than a special "wire block" with hand-tuned stats, a cable is a run of voxels of conductor material (plus optional insulation voxels — tree resin in the game fiction). Cross-sectional area equals ampacity, material resistivity is the real value, so wire gauge, fusing, and insulation become emergent physics: a thin section overheats first, and a lead segment in a copper run *is* a fuse (design.md R12). Because cable voxels are real vanilla microblock voxels (R17), the engine meshes, culls, and chisels them for free — and chiseling away insulation genuinely exposes the conductor. For the programmer: the voxel geometry is the source of truth, and the solver's netlist is a derived view kept in sync incrementally (R11), like an index rebuilt from a primary datastore.

**Project-coined term**, closest standard concepts: physical wire gauge/ampacity modeling, plus netlist extraction from layout geometry (as in IC/PCB layout-versus-schematic tools).

**Where it appears:** design.md header, R12, R17, Hazards, Device notes; vintage-story.md sec 1; compaction.md (voxel regions collapse to single resistors per R10).

**References:** Horowitz & Hill, *The Art of Electronics* (wire gauge, ampacity, and fusing as physical resistive phenomena).

### waveform-first AC
The project's stance (requirement R3) that AC circuits are simulated as actual time-domain waveforms — the sine wave is really computed sample by sample — with phasor (complex-number) analysis relegated to a possible later metering optimization, never the primary mode.

Concretely, AC islands are advanced by *subcycling*: several small time-domain solver steps within each game tick, so the instantaneous voltages and currents genuinely wiggle. For linear islands this is cheap — the factored matrix (LU) is cached and each substep is a right-hand-side update plus back-substitution. The payoff is gameplay fidelity: a 5 Hz waterwheel generator makes lamps visibly flicker (deliberately instructive), and the oscilloscope item has real waveforms to display rather than a synthesized picture. For programmers: phasor analysis is like replacing a loop over time samples with a closed-form steady-state answer — a great cache when you only need averages, useless when the UI needs the per-sample sequence. For EEs: we run transient analysis for AC where a textbook would reach for sinusoidal steady-state analysis, because the transient detail *is* the product. "Frequency-domain-only AC" is an explicit non-goal in design.md.

**Project-coined term**, closest standard concept: time-domain (transient) analysis vs. phasor / sinusoidal steady-state (AC small-signal) analysis.

**Where it appears:** `docs/design.md` R3 (and the non-goals list); the subcycling mechanics live in `docs/solver.md` and `docs/api.md` §12–13.

**References:** Nagel (1975), *SPICE2: A Computer Program to Simulate Semiconductor Circuits*, memo UCB/ERL-M520 (transient vs. AC analyses); Pillage, Rohrer & Visweswariah, *Electronic Circuit and System Simulation Methods*.

### waveform stream / high-rate stream
A temporary, elevated-rate network feed of one probe point's samples from server to client, negotiated by the Vintage Story oscilloscope item — as opposed to the slow background telemetry that feeds tooltips.

The VS integration plans two data rates. Ordinary tooltip readouts (voltage/current lines when you look at a block) are served by a throttled `mna-telemetry` channel broadcasting per-island probe values every ~0.5–1 s — cheap enough to run always, following the mechanical mod's ~800 ms broadcast precedent, because the HUD polls the *client's* copy of the block entity and per-tick sync would spam chunk repackets. When a player actually uses the oscilloscope, that item negotiates a high-rate stream **for one probe point only**: the server temporarily samples and ships that point fast enough to draw a real trace, then tears the stream down. For an EE, the analogy is a data logger's slow trend log versus temporarily clipping a scope onto one node; for a programmer, it's an on-demand subscription upgrade on a single key, with the low-rate channel as the always-on default.

**Project-coined term**, closest standard concepts: on-demand telemetry subscription / adaptive-rate streaming.

**Where it appears:** `docs/vintage-story.md` §4 (Tooltips and Instruments, R15). The server-side sampling machinery it would draw from is the `WaveformTap`/`WaveformRing` API (`docs/api.md` §13).

### WaveformTap.Attach
- The setup-time API call that subscribes an oscilloscope-style waveform capture to a probe, so the solver streams per-substep voltage samples into a buffer the caller owns.

`WaveformTap.Attach(netlist, probeId, ring)` is a **cold, call-once subscribe**: it allocates the tap object and wires the given probe's per-substep samples into a caller-owned `WaveformRing` (a fixed-capacity circular buffer). Because it allocates, it belongs at *shape time* — the setup phase where the circuit's structure is built — never inside the per-tick loop, where the core guarantees zero allocation after warmup (R8). Once attached, sampling costs one struct store per substep and zero bytes; the tap survives rebuilds because `ProbeId` handles are document-stable. For an EE: this is the moment you clip the scope probe onto the node — you do it once while wiring the bench, not on every trigger sweep. For a programmer: it is the observer-pattern subscribe call, deliberately split from the hot notification path.
- **Where it appears:** `docs/api.md` §13 (Probes and the oscilloscope tap), `docs/integration-tutorial.md` §9 (performance contract), `examples/RevoltWalkthrough/`.
- **References:** Gamma, Helm, Johnson & Vlissides, *Design Patterns* (1994) — Observer, for the subscribe/notify split.

### WaveformTap / WaveformRing
The manatee-core API pair for oscilloscope sampling: a `WaveformTap` subscribes one probe to be sampled every AC substep, writing into a `WaveformRing` — a fixed-capacity, caller-owned circular sample buffer.

`WaveformTap.Attach(netlist, probeId, ring)` is a **cold, call-once subscribe**: it allocates the tap object and belongs in setup code, never in the per-tick loop (attaching per tick would allocate every tick and break the solver's zero-bytes-per-tick contract; §22.b's walkthrough calls this out explicitly). Once attached, the solver stores one sample per substep into the caller's ring — one struct store, zero allocation. The ring is *caller-owned*: the client constructs it with a capacity, the solver only fills it, and the exposed `Samples` span must never be cached across ticks. The UI contract is **two taps**, because the teaching scope compares phase between a pair of signals. For an EE, this is literally clipping a scope probe onto a node — Attach is connecting the probe, the ring is the scope's acquisition memory. For a programmer, it's an observer subscription writing into a ring buffer, with strict setup-time-only registration.

**Project-coined API names**, closest standard concepts: probe/trace subscription plus a ring (circular) buffer; the taps sample per substep, so this is transient-waveform capture, not RMS metering.

**Where it appears:** `docs/api.md` §13 (definition, probe-survival semantics across rebuilds) and §22.b (walkthrough showing correct setup-time Attach); memory-ownership table in api.md lists `WaveformRing` as client-owned.

**References:** ring/circular buffers are standard CS material — see e.g. Cormen, Leiserson, Rivest & Stein, *Introduction to Algorithms* (queue implementations); the subscribe pattern is the Observer pattern, Gamma et al., *Design Patterns* (1994).

### wiring policy / ReferenceBound / BindNegative
- The per-client rule, fixed when a `Netlist` is created, that decides where device *return* terminals connect — so integration code never writes per-device grounding logic.

`ReferenceBound` is the Stationeers mode: vanilla Stationeers devices are effectively single-terminal, so the policy automatically binds every return terminal to its partition's reference rail through a near-ideal return conductance (default 1e3 S), numerically equivalent to modeling the return conductor without paying for a full two-wire solve. The key API design point is that there is deliberately **no `BindNegative` call** a client could invoke per device: the policy fires *declaratively* at the points where returns are already identified — the `neg` argument of `AddVoltageSource`/`AddSineSource`, any node created with `NodeRole.Return`, and terminals a `Device` lists in `TerminalSpec.ReturnTerminals`. For programmers: it is dependency-injection-style configuration replacing boilerplate at every call site — the rule lives in one place and is applied by the framework. For the EE: it answers "where does current return to the source?" once per installation instead of once per component, like choosing a chassis-ground convention for a whole product line.

**Project-coined term**, closest standard concepts: a grounding/return convention plus automatic ground-net assignment in schematic capture; `ReferenceBound` approximates a common ground rail with finite return-path conductance.

**Where it appears:** `docs/integration-tutorial.md` §2; `docs/api.md` §5 (`WiringPolicy`); `docs/design.md` (Grounding model).

### WiringPolicy (ReferenceBound / TwoWireLeak / ExplicitOnly)
- A three-mode, construction-time setting on `NetlistOptions` that tells the solver how each client's circuits stay referenced to ground, so no client writes per-device grounding boilerplate.

The core itself is agnostic to wiring convention; it only requires one reference node (the MNA datum) per island and keeps floating subgraphs nonsingular with tiny gmin shunts. The policy fills the gap per client: **`ReferenceBound(g = 1e3 S)`** (Stationeers) binds every return terminal to its partition's reference through a near-ideal conductance — no leak stamped; **`TwoWireLeak(1e6 Ω)`** (Vintage Story, where supply *and* return are real routed nodes and islands would otherwise float) auto-stamps a ~1 MΩ leak from every Return-role node to the island's reference (the literal earth) at commit — the leak resistor is never hand-authored and has no client handle; **`ExplicitOnly()`** (tablet) stamps nothing implicit, so deliberately floating teaching circuits stay floating and ground is marked explicitly with `AddReferenceNode`. The policy is immutable after `Netlist` birth and each named client bundle (`NetlistOptions.Stationeers/VintageStory/Tablet`) picks the right one. For programmers: it is a strategy object selected once at construction, like choosing a storage engine when opening a database. For the EE: it is the choice among bonded-return, earth-leakage-referenced, and explicitly-grounded wiring conventions, made per installation rather than per device.

**Project-coined term**, closest standard concepts: grounding/earthing convention; `TwoWireLeak` resembles SPICE's practice of adding large resistors to ground to make floating nodes solvable (cf. gmin), applied systematically at Return nodes.

**Where it appears:** `docs/api.md` §5 (type definition and where the policy fires) and the sharp-edges list (§24 area, item 9); `docs/design.md` (Grounding model); `docs/integration-tutorial.md` §2.

**References:** Ho, Ruehli & Brennan (1975), "The Modified Nodal Approach to Network Analysis", IEEE Trans. Circuits and Systems (the datum-node requirement the policy exists to satisfy); the ngspice manual (gmin and floating-node handling).

### XML dialect / `<cir>`
- The file format that Falstad's circuitjs1 4.x (2025-06 onward, tagged 4.1.2) saves circuits in: an XML document rooted at `<cir>`, replacing the classic line-oriented text format — and one Manatee's importer deliberately rejects.

XML is a generic markup format (nested tags with attributes, like HTML for data). In circuitjs1 4.x, `dumpCircuit` emits a `<cir>` document whose elements carry terse attribute names — `r` (resistance), `c`/`iv` (capacitor value / initial voltage), `l`/`ic` (inductor / initial current), `maxv`/`freq`/`wf`/`b`/`ph` (source parameters), `p`/`mm` (switch) — and the classic text *writer* (`String dump()`) has been deleted from many element classes, while the text *readers* all remain. So files freshly exported from falstad.com are XML, but the entire installed base, all historical files, `cct=`/`ctz=` URLs, and the bundled example corpus are classic text. Manatee's Falstad importer accepts only the classic text format; a file starting with `<` is rejected loudly with the message "export as classic text" — which in practice means using an older circuitjs1 (a pre-XML revision such as `ba02ba90`, the last generation whose writer emitted classic text), because current builds have removed the text writers from many elements even though their readers still parse the format. Electrical Age's importer proves the XML dialect is parseable if it is ever wanted. For the EE: this is a schematic file-format generation change, like a CAD package switching native formats while still opening legacy files.

This is upstream circuitjs1's own format, not a Manatee coinage; "classic text format" is the older counterpart.

**Where it appears:** `docs/falstad-format.md` §5.2 (the 4.x XML pivot, with circuitjs1 file:line citations) and §7 (the importer's reject list).
