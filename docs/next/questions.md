# Questions for the user

Last updated: 2026-10-06

**Status.** The single list of open decisions for the re-plan. It replaces `questions-api.md`, the open questions in `accuracy-and-performance.md` §10 and `doc-plan.md` §6, merged and deduplicated. Each item states a leaning, so "yes" accepts it; answer by label (for example "T3 yes, B5 no: ..."). Other docs refer to items here by label ("questions.md B4"). Answered items become dated rows in `decisions.md`.

Sections: **Top questions** (the ones that shape the most work), **A.** defaults the draft assumes without asking, **B.** API decisions, **C.** accuracy and testing, **D.** docs, **E.** items to relay to Sukasa. Within each section, items are ordered by impact.

## Top questions

T1. **How accurate must the game be?** Every backend is scored against two columns of one accuracy table: *Lesson* (what the tablet's lessons need) and *Game* (what tooltips, meters and gameplay in VS and Stationeers need). The Lesson column is fixed at 1% on meters, 2° of phase, ±0.02 power factor and 3% waveform error. The current engine fails it at 5 Hz: an ideal inductor reads 81° instead of 90° and power factor 0.156 instead of 0. The Game column is the bar a backend must clear before it ships in a game. Leaning: 5% on meters, 5° of phase, ±0.05 power factor, 10% waveform error.

T2. **Can attaching a scope change gameplay?** A backend could compute more precisely wherever an oscilloscope is attached. That would mean a player holding probes could change whether a fuse blows. Leaning: no, bit-identical. Readings, events, trips, torques and network grouping are always the same with or without a trace; a trace only adds display detail. This rules out "extra accuracy where observed" as a backend strategy.

T3. **Build a slow reference backend as the source of truth?** ngspice (the standard circuit simulator) is our main source of correct answers, but it cannot model shaft-driven generators and motors, fuse heating (I²t) or appliances that drop out at low voltage. Leaning: yes. Ship a slow in-repo backend written for clarity, not speed, as the truth for those parts. It is itself checked against ngspice on every scenario ngspice can run, and it is the only backend required to offer the `Strict` fidelity level.

T4. **Measure before choosing the next backend, at both 5 Hz and 50 Hz?** VS uses 5 Hz as its main frequency, with 50 Hz reachable through generators with many pole pairs. A backend specialised for AC gains little at 5 Hz with a 50 ms tick (about 1.3-1.7× net, not the 30× of the waveform report). Leaning: yes. Keep 5 Hz as VS's main frequency, benchmark both frequencies on the scorecard, and pick the next backend from those numbers. The cheapest known fix for the phase and power-factor failures in T1 is a better time-stepping rule in the current engine, not a new engine.

T5. **Must every backend load every other backend's saves?** A save holds named physical state (capacitor voltage, battery charge, fuse heat, generator angle), never backend internals. Leaning: required for every shipped backend. A save must load into any backend and any later version: state within the accuracy budget immediately, readings within budget after one meter window. No backend promises a bit-exact round trip.

T6. **Game time or real time?** VS speeds up its calendar while players sleep. Leaning: `Step(dt)` follows real elapsed time. A later `Step(dt, slowStateScale)` lets slow state (battery self-discharge, cooling, controller timers) follow the calendar instead. Your call on the gameplay: should batteries drain faster while players sleep at all? Leaning there: yes, slow state only.

T7. **Readings: borrowed frame or plain object?** Readings come as a frame you borrow and give back (`using (var r = c.AcquireReadings())`). This keeps steady steps allocation-free on Unity Mono, at the cost of a `Dispose` call. A plain snapshot object is simpler to use but allocates a frame every step. Leaning: borrowed frame.

T8. **What does Manatee own about heat?** Leaning: Manatee owns the temperature of conductors and parts and the overload heat that ratings need, and reports heat output per part. Rooms, climate and fire stay in the game. Heat flowing between neighbouring parts is a later step.

T9. **Is Re-Volt still second in line?** The delivery order is core and tablet harness, then Stationeers (Re-Volt), then the VS mod. Re-Volt needs a set of behaviour changes signed off by Sukasa (the relay list at the end of this file). Leaning: confirm Sukasa is still engaged before writing the Stationeers game doc in full. Then send him that relay list plus the 38-item audit grid directly, and keep a "Pending Sukasa sign-off" section at the end of `games/stationeers.md`. If he has moved on, cut that doc to about 60 lines of verified engine facts and reconsider the order.

T10. **Order of work.** Leaning: (a) glossary and `decisions.md`, which fix the vocabulary for everything else; (b) `api.md`; (c) `concepts.md` and `accuracy-and-performance.md`; (d) `games/` and `tablet.md`; (e) `backends/`; (f) retire the old files in one commit (whether to delete or archive them is asked separately). Each step lands as one jj change.

## A. Defaults baked into the draft: confirm or object

The draft assumes these without asking. Leaning on every one: keep. Object by label.

A1. **One id per object, chosen by the game.** A 64-bit id per part, node, group and load area, valid until that object is removed. No handles, re-pinning or library-issued keys. Parts and nodes share one number space, because a cable piece's id is also its junction's id.

A2. **One `Circuit` holds both game parts and textbook parts.** A shared catalog (cable, lamp, battery, breaker, generator...) and textbook primitives (resistor, capacitor, source, diode). The tablet and both games use the same catalog, so a lesson's lamp behaves like a world's lamp.

A3. **Manatee owns protection physics; the game owns consequences.** Fuses, breakers and cables open themselves inside the simulation and stay open until `Rearm`. Fire, dropped items and removed blocks are the game's job, and come back as ordinary edits.

A4. **No custom equations, only composition and controllers.** Mods build custom parts from catalog parts and primitives, plus a controller that runs between steps. Kinds, materials and cable types are registered by name. This reverses the old rule "no callbacks into clients, ever"; new equation-level parts are a backend matter.

A5. **The game owns topology; snapshots are optional.** After loading, the game rebuilds the circuit from its own data. A snapshot only restores physical state by id, tolerates parts that changed, and reports what started from rest.

A6. **Grounding is a construction option.** `Explicit` (tablet: you place ground symbols), `CommonReturn` (Stationeers: one-wire devices) or `Earth` (VS: two wires, optional ground rods).

A7. **Our own scenario format, Falstad import only.** One file format serves lessons, tests and benchmarks. It is specified once, in `accuracy-and-performance.md` §2; `tablet.md` documents only the lesson extras. Falstad files are imported by the schematic/tablet layer, never by the core, and never exported.

A8. **No client-visible tearing.** No decoupling transformers, client-declared partitions or relaxation coupling; one `Transformer` part. Which parts form one network is computed and read-only. A query answers which game networks (Stationeers `CableNetwork`s) share one electrical network, and fallback and health apply to that whole set.

A9. **Standard names over Electrical Age and old-project names.** "Network" (with game docs always saying `CableNetwork` or "mechanical network" for the game's own), "part" in the API, "device" only for game objects, "Unsolvable" not "Faulted". `concepts.md` gets a "Coming from Electrical Age" box.

A10. **One global `Step(dt)` per game tick.** Edits made between steps (or during one, from another thread) apply at the start of the next step, in any order; a link to a part that has not arrived yet is allowed and waits.

A11. **Readings are meter values over a window.** Mean, RMS, peak and AC values over the last 0.2 s, plus energy and heat per step. Instantaneous values come only from an attached trace; clients draw lamp flicker from AC values.

A12. **The method is hidden.** Backends are pluggable behind a fidelity request (`Game`, `Lesson`, `Strict`). The current engine becomes backend #1, scored like any other. The old rule "phasor is never the primary mode" is deleted.

A13. **Failure stays inside one network.** A network with no answer is `Unsolvable`, keeps its last good readings flagged `LastGood`, and does not stop others. `Step` never throws on circuit content and no reading is NaN. This reverses the old "Faulted networks read 0 V", which made appliances see a dead short.

A14. **An unloaded chunk freezes its whole network.** It resumes from where it stopped, with no catch-up on reload.

A15. **The server is authoritative.** Clients never simulate world circuits. Results are bit-identical only for the same backend, version and runtime; lessons and tests compare within tolerances.

A16. **The game owns the shaft.** It supplies speed (and owns inertia and friction); Manatee tracks electrical angle from that speed and returns the mean counter-torque.

A17. **Steady steps allocate nothing.** A release gate on Unity Mono for every shipped backend.

## B. API decisions

B1. **Long ticks after a server hitch.** A `Step(dt)` longer than `MaxStep` is split into slices; past `MaxCatchUpSlices` (4) slices the rest is dropped and reported, so a hitch never turns into a catch-up spiral. Leaning: yes, with `MaxStep` 0.1 s for VS, 0.5 s for Stationeers, 0.25 s by default.

B2. **One backend per circuit.** Auto picks one backend when the circuit is created and never switches mid-run. Mixing backends per network would need state hand-off every time a breaker joins or splits networks. Leaning: per circuit now; revisit only with a measured need.

B3. **Big edits land in one step.** A 10,000-cable load or a large merge is taken in during the next `Step`, which can make that step slow. Spreading it over several steps would make networks read `Stale` for a number of steps that depends on machine speed, so gameplay would depend on hardware. Leaning: always apply at the next step; make the worst step of the 10k cold build a scorecard row with a budget; revisit only if a backend misses it.

B4. **Multiplayer feed.** A `ChangeFeed` with thresholds is the only replication hook, and the game filters per player. Leaning: yes. Stationeers starts with the vanilla `CableNetwork` fields only and adds per-device readings later for tooltips.

B5. **VS mechanical edge cases.** (a) While an electrical network is frozen but its mechanical network keeps running, the alternator holds its last mean counter-torque, flagged `Held`. (b) VS consumers cannot drive a shaft, so a motor reports signed torque and the VS adapter maps it to a rotor (`BEBehaviorMPRotor`). (c) Vanilla stops asking for torque while a mechanical network is not fully loaded, so the adapter gives each mechanical network a load area and freezes the alternator's electrical network with it. Leaning: yes to all three.

B6. **Shock and floating circuits.** Leaning: shock damage uses RMS volts to earth; insulation breakdown and overvoltage trips use peak. A section isolated from earth is flagged `Floating` and reads 0 V to earth for shock purposes.

B7. **Bug-report export.** Leaning: yes. Any network or whole circuit can be written as a scenario file, so a player can attach a reproducible "why did it explode" case. Exact replay needs each step's `dt` and the edits applied at its start, so the exporter keeps that journal; no recorder is added to the API now.

B8. **VS ids.** Leaning: (a) events and readings always name the specific voxel, even after internal merging, as a contract; (b) the id packing published in `Manatee.VintageStory` is how any mod's block names a connection point; (c) v1 refuses to start on maps larger than the packing allows (x and z up to 2^20, y up to 2^10; engine maxima still to verify) and does not electrify blocks in a non-zero dimension. Optional sugar `VoxelCables.FaceTerminal(pos, face, u, v)` for other mods (EA v1's `faceConnections`).

B9. **Earth in VS.** Leaning: all ground rods meet at one ideal earth node, so soil resistance between separate rods is not modelled. The VS adapter adds a ground rod (`EarthElectrode`) per bare voxel touching soil or water and sets its resistance from wetness; drying and glassification under load are worked out by the mod from per-part heat and current, with no library event.

B10. **How detailed v1 part models are.** Leaning: generators and motors are synchronous machines (an induction motor with slip is a later kind); batteries apply round-trip efficiency half on charge and half on discharge and have no self-discharge; parameters are scalars, with battery and generator curve tables added later if a game asks.

B11. **Signals.** EA v1 had a separate signal domain (0..1 levels, a 16-channel bus). Leaning: no separate domain; signals are ordinary low-voltage DC, with a VS-side helper mapping 0..1.

## C. Accuracy and testing

C1. **Splitting a tick.** Must `Step(a + b)` equal `Step(a); Step(b)`? Leaning: within the accuracy budget, never bit-identical. The scorecard also runs each scenario at other tick lengths to show where a backend stops being valid; those runs are reported, not folded into the headline error ratio.

C2. **Unrelated frequencies on one network** (a 5 Hz and a 50 Hz source together). Leaning: every backend must run them, flagged `Approximate`, rather than refuse. They are scored against the Game column even when flagged; the Lesson column does not apply.

C3. **Scorecard additions and CI cadence.** Leaning: (a) add a sliding one-cycle comparison of AC values against the truth, next to waveform error; (b) generate ngspice reference results in CI, cached by a hash of scenario, ngspice version and options, keeping "ngspice missing is a hard failure"; (c) every commit runs the mandatory scenarios plus one per class; long-run drift and the 50 Hz sweep run nightly.

C4. **Trust indicator.** Leaning: the per-network energy-balance error and the `Approximate` flags are the tablet's "is this simulation trustworthy" indicator and never affect gameplay. No API to tell the library what is being watched beyond attaching a trace.

C5. **Scope detail at 5 Hz.** Leaning: a trace rebuilt from AC values is acceptable as long as switching events visibly appear; switching only every 90° of the cycle at a 50 ms tick is acceptable; the tablet uses a finer tick for the synchronisation lesson.

C6. **Bandwidth rules.** Leaning: (a) a waveform observation that asks for more detail than a backend's declared `WaveformBandwidth` is N/A, but a backend cannot ship for `Lesson` with any N/A trace observation in the mandatory or steady-AC scenarios, so it cannot pass by declaring little detail; (b) `Peak` is taken after low-passing the true waveform at 100 Hz for Game and 1 kHz for Lesson and Strict.

C7. **Which runtime gates performance.** Leaning: net8.0 in CI catches regressions; a Unity Mono run gates zero allocation before each Re-Volt release.

C8. **Lesson pass tolerances.** Leaning: a lesson's `teach` tolerance must be at least three times the Lesson budget for that quantity, so a backend that passes the scorecard never fails a lesson by luck.

## D. Docs

D1. **Delete or archive the old docs?** Experiments, API-competition rounds, reviews, the overnight report, old `api.md`, `solver.md`, `compaction.md`, the integration tutorial. Leaning: delete. jj history is the archive and `decisions.md` keeps what is still live; an archive folder invites reading old text as current.

D2. **Glossary size.** Leaning: one modder-facing `glossary.md` of 40-60 entries plus about 15 implementer terms in `backends/README.md`; delete the 900-entry `glossary/` tree. More than 60 entries would mean the API still leaks method vocabulary.

D3. **`api.md` length.** The plan targets 400 lines; the draft is about 620, with walkthroughs already split out into `guide.md`. Leaning: keep `guide.md` separate; once these questions are answered, trim `api.md` towards 450 lines by moving part equations to `backends/part-models.md` and reading definitions to `accuracy-and-performance.md`.

D4. **Game design in this repo?** Leaning: both `games/vintage-story.md` and `games/stationeers.md` live here for now. The Stationeers doc is marked "integration notes; Re-Volt owns gameplay" and moves to Re-Volt's repo once Sukasa takes it.

D5. **Runnable examples.** Leaning: every walkthrough in `guide.md` gets a runnable twin under `examples/` that CI compiles, replacing `RevoltWalkthrough`, once the API has an implementation. Until then the walkthroughs are marked "illustrative".

D6. **Your waveform report.** Leaning: commit it unchanged under `docs/research/` with a short header listing the corrected premises (5 Hz, 50 ms tick, EA's `interSystemOverSampling` repeats pre-step work and is not oversampling, net speedup about 1.3-1.7×). `backends/candidates.md` carries the corrected conclusions and links to it.

## E. For Sukasa (relay; not the user's call alone)

Each item is written to be pasted to him as is. Our leaning is stated; his answer decides.

E1. **The Re-Volt behaviour bundle, as one package.** One Harmony prefix on `ElectricityManager.ElectricityTick` replaces per-network `RevoltTick`, and `ApplyState` only reads results. The one-tick-lag energy accumulators on batteries, transformers, breakers and APCs go away: a device on two networks is one part with terminals on both. Breakers trip on amps through a trip curve instead of watts against `Setting`. `CableFuse.PowerBreak` and `Cable.MaxVoltage` become amp ratings, and a fuse trips on the current through its own cable, so placement matters. The vanilla transformer becomes a converter: `Setting` is a power cap, output voltage is regulated. Battery charge and discharge caps map to max charge and discharge power. The "trip all breakers instead of burning" rule, the 20 W anti-flap band, the `Lerp(0.1)` heat window and the client-side G = P/V² loop are removed; restores are staggered instead. `NetworkExport.ThingInfo.RefId` widens from `int` to `long`. Details: `guide.md` §1.1. Our leaning: yes, as one package.

E2. **Back-feed.** A closed breaker conducts both ways, so power can flow from its output network into its input network; today power only moves one way. Our leaning: accept it; it is real and teaches something.

E3. **Which cable burns.** Today a seeded random pick among the weakest cables burns. Proposed: the hottest cable burns, decided by physics, with optional seeded variation per part (from the world seed and the part id) instead of a per-network `System.Random`. Our leaning: yes; Re-Volt sets some variation, VS and the tablet set none.

E4. **Loads run or drop out.** Vanilla devices expect `wanted × ratio`. Proposed: an appliance either gets full power or drops out below its dropout voltage and returns after a delay, one load per network per step; only storage and converter inputs (chargers, APC and battery inputs) accept partial power. When supply cannot meet total demand, loads drop out one at a time. Our leaning: largest demand drops first, ties by lowest id (the alternative, lowest voltage first, is more physical but harder to predict), with a 1 s restore delay.

E5. **Sources always have some internal resistance.** Batteries, generators and power-limited sources get an internal resistance derived from their nameplate, so two batteries at different charge on one cable never form an unsolvable loop. Our leaning: a battery's short-circuit current is about 20 times its one-hour discharge current; generators the same way from rated voltage and power.

E6. **Who owns battery charge.** Our leaning: the simulation owns it and writes `PowerStored` back every tick; the game re-seeds it only on load and when a network leaves fallback.

E7. **Voltage drop.** Our leaning: computed per cable from its gauge, plus a coarse "attach to this `CableNetwork`" mode for devices whose cable is unknown.

E8. **Logic values.** An opening breaker must never change IC10 data connectivity. Our leaning: the Re-Volt adapter documents which reading feeds `PowerActual`, `PowerPotential`, `Ratio` and `Charge` (through-power and per-`CableNetwork` totals); the core stays out of logic.

E9. **Wireless power, umbilicals, bus ties.** Our leaning: `PowerTransmitter`/`PowerReceiver` become a converter with an adapter-set efficiency for distance; rocket umbilicals and `PowerConnector` become switches; a bus tie is a plain wire; HeavyBreaker port membership stays in Re-Volt and reaches the core as a terminal rewire.

E10. **Rollout and tick budget.** Turning Re-Volt on in an existing save needs only seeding from `PowerStored` and breaker `Mode`, with the cold-start report logged. Stepping runs serially inside the prefix because the power thread is shared with atmospherics. Our leaning: the electrical step may use 10% of the 0.5 s power tick (50 ms) at p99, reported but never acted on at runtime.

---

**Trivia defaults assumed** (object if any matters): a link to a part not yet arrived waits 2 steps before counting as open; lamp flicker depth is computed client-side from AC values; setters on a custom part's id are ignored (its controller owns it); converters are one-way; a cable-piece link exists if either piece lists it; a cable piece heats and melts as one unit; a network's reference AC frequency comes from its largest generator, else its lowest-id sine source; relative error is floored at 1% of the scenario's full scale; scorecards compare only same-machine runs; `AGENTS.md` becomes a symlink to `CLAUDE.md`; `decisions.md` keeps reversed rows for one doc cycle.
