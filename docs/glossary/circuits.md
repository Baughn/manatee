# Glossary: Circuits & Electrical Engineering

*Last updated: 2026-07-07. Part of the [Manatee glossary](../glossary.md) — see the index there for all terms and for how entries are structured.*

Electrical concepts for readers coming from software: components, laws, AC/DC behavior, hazards.

### AddVoltageSource
- The netlist API call that adds an ideal voltage source between two nodes, forcing a fixed voltage difference (`volts`) between its `pos` and `neg` terminals.

Signature: `VSourceId AddVoltageSource(NodeId pos, NodeId neg, double volts, in ExternalKey key)` on `StructuralEdit`. For programmers: a voltage source is a constraint, not a producer of a value — it pins the *difference* between two node voltages and lets the solver work out whatever current is needed to satisfy that, much like an assertion the solver must keep true. In MNA that costs one extra matrix row per source (its unknown branch current). The `neg` argument matters beyond polarity: under the `ReferenceBound` wiring policy (used for Stationeers, where devices are single-terminal), `neg` is one of the declarative hooks the policy fires on, automatically binding the source's return side to the partition's reference rail — there is deliberately no separate `BindNegative` call. The sibling `AddSineSource` is the AC (time-varying) counterpart.

This is a standard SPICE-style netlist operation (SPICE's `V` element), exposed as a typed method rather than a text card.
- **Where it appears:** `docs/api.md` §6 (StructuralEdit) and §5 (wiring policy firing points); `docs/integration-tutorial.md` §2.
- **References:** Ho, Ruehli & Brennan (1975), "The Modified Nodal Approach to Network Analysis", *IEEE Trans. Circuits and Systems* — the origin of the extra-row treatment of voltage sources; Nagel (1975), *SPICE2: A Computer Program to Simulate Semiconductor Circuits*, UCB/ERL-M520.

### adjustable rail (`172`)
- A Falstad/circuitjs element type: a one-post voltage source ("rail") whose voltage is bound to an on-screen slider, dumped as type code `172`.

In the Falstad text format, a *rail* (`R`) is a single-terminal voltage source — think "labeled supply pin" rather than a two-terminal battery. Type `172` (`VarRailElm`) is the variant with a UI slider: its dump line carries the usual voltage-source parameters plus a slider label (escaped text). For programmers: it is a voltage source with a live-editable parameter and attached presentation metadata. Manatee's importer **rejects `172` loudly** (with line number, token, and reason) rather than importing it — the slider semantics are UI state we do not model. Electrical Age's importer instead substitutes a fixed DC source at the current value (`FalstadNetlist.kt:167-181`); that substitution is documented as EA-dialect behavior, not something Manatee copies.

Standard within the Falstad/circuitjs1 ecosystem (`VarRailElm`); no meaning outside that format.
- **Where it appears:** `docs/falstad-format.md` §4 (element table), §6 (EA dialect notes), §7 (accept/reject contract — reject list).

### adjustable slider (`38`)
- A Falstad/circuitjs *non-element* line, type code `38`, that attaches a UI slider to some other element's editable parameter — pure presentation, with no electrical meaning of its own.

Unlike `172` (which *is* a component), a `38` line is metadata: `elmIndex [Fflags] editItem min max [shared] sliderLabel...` (`Adjustable.java:47-65`), saying "draw a slider controlling parameter *editItem* of element number *elmIndex*, ranged min–max." For programmers: it is a data-binding declaration in the save file, like a UI widget spec pointing at another object's field by index. Because these lines appear in virtually every real Falstad export, Manatee's importer does not reject them: it **ignores them with a logged notice**, counting and reporting what was dropped so a lesson author can see it. The circuit's electrical behavior is identical with or without them (the bound parameter's current value is stored on the element itself).

Standard within the Falstad/circuitjs1 ecosystem (the `Adjustable` class); no meaning outside that format.
- **Where it appears:** `docs/falstad-format.md` §4 (non-element line types), §7 (accept/reject contract — ignore-with-notice list).

### admittance
- The complex-number generalization of conductance: how easily current flows through an element when both magnitude and phase shift matter, written Y = 1/Z (the reciprocal of impedance).

Plain conductance (G = 1/R, in siemens) is a single real number: "this much current per volt." Under sinusoidal AC, capacitors and inductors also shift the *timing* of current relative to voltage, and admittance captures both effects in one complex value — the real part (conductance) and the imaginary part (susceptance). For a programmer: conductance is a `double`, admittance is the same field widened to a `Complex`; every MNA stamping rule carries over unchanged, just over complex arithmetic. In Manatee this is mostly future tense: the docs say "conductance" throughout because the shipped solve path is real-valued time-domain (Backward Euler companion models turn capacitors and inductors into conductances plus sources each timestep). A complex-frequency (phasor) stamp path is explicitly deferred until phasor metering is wanted; the stamp interface reserves room for it.

Standard term (universal in circuit theory); the imaginary part is called *susceptance*, the units are siemens.

**Where it appears:** implied by `docs/solver.md` (Analyses: "a complex-frequency stamp is a later addition"; Numerics: "Complex-valued solve path deferred until phasor metering is wanted").

**References:** Horowitz & Hill, *The Art of Electronics* (impedance/admittance treatment); Vlach & Singhal, *Computer Methods for Circuit Analysis and Design* (complex MNA formulation); Ho, Ruehli & Brennan (1975), "The Modified Nodal Approach to Network Analysis," *IEEE Trans. Circuits and Systems*.

### Alternation
The tablet-curriculum name (Lesson 11) for what alternating current looks like: voltage and current swinging back and forth in a repeating wave rather than holding a steady value.

Lesson 11 is the oscilloscope lesson opening the AC arc: the player puts a scope on an AC line and sees the sine wave for the first time. It is deliberately taught as an observation before any theory — the shape on the screen, the fact that the value passes through zero twice per cycle, and the payoff explanation of an effect the player has already noticed in-world: lamps on the game's 5 Hz AC grid visibly flicker, because 5 Hz is slow enough that each zero-crossing dims the lamp perceptibly (real 50/60 Hz grids cross zero too fast to see). In programming terms: the lesson shows the raw signal trace first, the way you would show a log of raw samples before introducing the function that generates them; frequency and rotation come in Lesson 12.

**Project-coined usage** (as a lesson topic name), standard term: alternating current (AC) waveform; the word "alternation" also has a narrow textbook meaning — one half-cycle of an AC wave — which is not how the curriculum uses it.

**Where it appears:** `docs/curriculum.md`, Lesson Arc III (AC power), Lesson 11.

**References:** Horowitz & Hill, *The Art of Electronics*, ch. 1 (sine waves and AC signals).

### Alternator
- The AC generator: a rotating machine that converts shaft rotation into an alternating voltage, which in Manatee's Vintage Story client is implemented as a consumer plugin on the game's mechanical-power network.

An alternator produces an EMF (source voltage) whose magnitude tracks shaft speed and whose electrical frequency equals shaft speed times its pole-pair count (in VS the verified arithmetic is f ≈ 0.8 × speed × polePairs Hz, no fudge factor — pole count is the honest upgrade knob, from ~3–6 Hz starter machines to ~40–60 Hz high-pole-count ones). For programmers: it is an adaptor between two independently ticking simulations — it reads shaft speed from the mechanical network as a piecewise-constant input parameter and reports drag back. Concretely it subclasses `BEBehaviorMPConsumer` and overrides `GetResistance()`, so from the game engine's point of view it is just another mechanical load; from manatee-core's point of view it is just another AC source device. For EEs: this is the standard synchronous machine picture, deliberately simplified (generator paralleling uses a "swing-equation-lite" phase-angle + angular-velocity state per generator).

This is the standard EE term (synonym: AC generator; "dynamo" historically means the DC counterpart).

**Where it appears:** `docs/design.md` R14/R15 and Arc 1; `docs/vintage-story.md` §2 (Mechanical Network Coupling); `Manatee.Core.Devices.Alternator` in `docs/api.md`.

**References:** Horowitz & Hill, *The Art of Electronics*, for AC source basics; Fitzgerald, Kingsley & Umans, *Electric Machinery*, for synchronous machines.

### Alternator / counter-torque
- Counter-torque is the mechanical drag an alternator puts on its driving shaft, proportional to the electrical power being drawn from it — the mechanism by which electrical load becomes physically felt by waterwheels and windmills.

Energy conservation across the mechanical/electrical boundary requires that generating power costs shaft power; the alternator reports this cost to the mechanical network as a resistance-like value via `GetResistance()`. The reporting is deliberately **time-averaged, and the averaging window is normative**: single-phase AC power pulsates at twice electrical frequency (~10 Hz at the 5 Hz base), right at the mechanical network's ~100 ms torque re-sum cadence, so an instantaneously *sampled* counter-torque would alias into a phase-dependent DC torque offset — phantom (or free) torque, i.e. a perpetual-motion exploit. The rule (settled 2026-07-06): report the mean over all electrical substeps since the previous mechanical read, as a pure read of a cached value valid at any time. For programmers: this is a classic sampling-vs-integration bug across two loops with nearly matched periods, guarded by a standing CI invariant — a closed motor→shaft→alternator→motor loop must monotonically decay from any initial condition, so any failure indicts the coupling. For EEs: counter-torque itself is the ordinary electromagnetic braking torque of a loaded generator; the novelty here is only the anti-aliasing contract in the co-simulation.

Standard EE aliases: electromagnetic (braking) torque, load torque, back-torque.

**Where it appears:** `docs/design.md` R14 and Simulation Model (mechanical co-simulation); `docs/vintage-story.md` Mechanical Network Coupling (the normative averaging window); `docs/testing-strategy.md` Invariant Checks ("coupling dissipativity").

### Ampacity
- The maximum continuous current a conductor can carry without overheating, measured in amperes.

A real conductor's ampacity grows with cross-sectional area (more metal, less resistance, more surface to shed heat), and Manatee's Vintage Story fiction makes that literal: for voxel cables, cross-sectional area *is* ampacity (R12), so wire gauge is emergent physics the player can see. Ampacity is one of several quantities in a compacted run's **limit envelope**, alongside i²t thermal mass and melting threshold — and in a mixed-material chain each may be governed by a *different* segment, which is exactly how a lead segment in a copper run behaves as a fuse with zero special-casing. It also appears in the grounding model: overloaded earth electrodes dry and glassify the surrounding soil, raising contact resistance — honest negative feedback that caps earth's effective ampacity per electrode. For programmers: think of ampacity as a per-edge rate limit whose enforcement (limit events) is attributed back to the specific weakest segment, like a pipeline throttled by its narrowest stage.

This is the standard term (a portmanteau of "ampere capacity"), ubiquitous in electrical codes; aliases: current-carrying capacity, current rating.

**Where it appears:** `docs/design.md` R12 and the Grounding model; `docs/compaction.md` Responsibilities #4; `docs/api.md` §19 (per-limit-type envelope).

**References:** NFPA 70 (National Electrical Code), Table 310.16 ampacity tables; Horowitz & Hill, *The Art of Electronics*, on wire gauge and current limits.

### Amplitude+phase
- The pair of numbers two separately-solved AC islands exchange each substep to describe the sinusoidal quantity coupling them: how big the wave is and where it is in its cycle.

When a coupling device (a decoupling transformer) is a power-transfer boundary, the islands on each side keep separate matrices and communicate only through these exchanged values — with bounded lag (one substep = 1/20 cycle, versus a quarter cycle if exchanged per tick at 5 Hz) and damped by explicit relaxation (exponential smoothing, α tunable per device type). The coupling clamps transfer to what the source island actually delivered, and energy-ledger property tests enforce conservation over windows, not just per exchange. For EEs: a sinusoid at a known frequency is fully described by amplitude and phase — this is the phasor, so the exchange is a relaxed phasor handoff between co-simulated subcircuits, akin to a Gauss–Seidel-style waveform-relaxation boundary. For programmers: it is message passing between two otherwise isolated state machines, where each side solves against a slightly stale, smoothed snapshot of the other; the smoothing plus the conservation clamp is what keeps the feedback loop from ringing, and the adaptor-stability property tests verify it.

**Standard term:** phasor (magnitude and phase angle) representation; the exchange scheme is closest to relaxation-based co-simulation / waveform relaxation.

**Where it appears:** `docs/solver.md` Islands (exchange scheme, settled 2026-07-05); `docs/design.md` Simulation Model (island coupling); `docs/testing-strategy.md` property tests; `docs/api.md` (power-transfer boundaries).

**References:** Vlach & Singhal, *Computer Methods for Circuit Analysis and Design*, on phasor/AC analysis; Lelarasmee, Ruehli & Sangiovanni-Vincentelli (1982), "The Waveform Relaxation Method for Time-Domain Analysis of Large Scale Integrated Circuits", IEEE Trans. CAD, for relaxation-based decoupled simulation.

### amplitude+phase exchange
- The message-passing scheme used at power-transfer boundaries between islands: instead of sharing one big matrix, the two sides exchange a compact description of the coupled AC quantity — its amplitude and phase — once per substep, smoothed by relaxation.

When a decoupling transformer (or DC converter two-port) joins two islands, each island keeps its own independent matrix and solve; the coupling device injects into each side a source whose amplitude and phase were read from the other side on the previous substep. To a programmer this is two services exchanging small state snapshots on a fixed cadence instead of sharing memory — one substep of lag (1/20 cycle at ~20 samples/cycle) is the price of thread isolation. Explicit relaxation (exponential smoothing on the exchanged values, α tunable per device type) damps the feedback loop this creates; the transformer's own leakage/magnetizing storage is modeled honestly but not relied on for stability. Coupled islands are scheduled as one work unit substepping in lockstep, and each boundary carries an energy ledger so accounting surpluses become device heat, never free energy.

**Project-coined term**, closest standard concepts: waveform relaxation / co-simulation coupling with under-relaxation (Gauss–Seidel-style iteration between subsystem solvers).

**Where it appears:** `docs/solver.md` (Islands, exchange scheme), `docs/design.md` (Simulation Model — island coupling; Open Questions resolutions), `docs/api.md` §on coupling (`AmplitudeA/PhaseA/AmplitudeB/PhaseB/PowerA2B` exchange record), `docs/testing-strategy.md` (boundary-coupling stability property test).

**References:** Lelarasmee, Ruehli & Sangiovanni-Vincentelli (1982), "The Waveform Relaxation Method for Time-Domain Analysis of Large Scale Integrated Circuits", IEEE Trans. CAD — the classic treatment of solving coupled subcircuits by iterated value exchange rather than one monolithic matrix.

### analysis (one netlist, multiple analyses)
- A mode of solving: the same circuit description (netlist) can be evaluated as a DC operating point, as a timestepped transient, or as subcycled AC, without being rebuilt.

Requirement R2: the netlist is the single source of truth, and each analysis is a different interpreter over it. Components provide a time-domain stamp — their contribution to the equations — and the analysis mode tells them how to stamp: at DC (`dt <= 0`) capacitors become opens and inductors near-shorts; in transient, storage elements become their Backward Euler companion models; subcycled AC is transient run at N substeps per game tick driven by sinusoidal sources. To a programmer, this is one data model with multiple query planners; to an EE, it is exactly SPICE's `.op` versus `.tran` over one deck. Stationeers uses DC plus 0.5 s storage dynamics; Vintage Story uses transient and subcycled AC.

This IS the standard SPICE notion of an *analysis type* (operating point, transient); our "subcycled AC" is a game-loop-scheduled transient analysis, not SPICE's small-signal `.ac` frequency sweep — waveforms stay primary (R3).

**Where it appears:** `docs/solver.md` §Analyses; `docs/design.md` R2.

**References:** Nagel (1975), *SPICE2: A Computer Program to Simulate Semiconductor Circuits*, UCB/ERL memo M520; the ngspice manual (analysis chapters); Vlach & Singhal, *Computer Methods for Circuit Analysis and Design*.

### Auxiliary rows / auxiliary equations
- Extra rows (and matching unknowns) added to the MNA system for components whose behavior cannot be expressed as a simple conductance between two nodes — in manatee-core, voltage sources and the ideal transformer.

Plain MNA has one equation per circuit node ("current in = current out") with node voltages as the unknowns; that works for anything resistor-shaped. But some elements constrain a *current* or impose a relationship between voltages (a voltage source pins the difference between two nodes; an ideal transformer ties two port voltages together by the turns ratio), and those constraints don't fit the per-node template. The fix is to enlarge the system: add the element's branch current as an extra unknown and add an extra row stating its constraint equation. In programming terms, it's like extending a uniform record schema with a side table for the elements that don't fit the schema — the solver treats the enlarged matrix uniformly. Note that inductors and capacitors do *not* need auxiliary rows here: manatee-core handles them with Backward Euler companion models — an equivalent conductance plus a history current source (Norton form) — so they stamp like resistor-shaped elements (a textbook impedance-form MNA would give inductors branch-current unknowns; the companion-model formulation avoids that). In manatee-core, the ideal transformer two-port specifically requires *coupled* auxiliary rows, because magnetic coupling is not expressible as any composition of independent two-terminal elements.

**Standard term:** yes — this is the defining feature of Modified Nodal Analysis. SPICE literature calls elements that get such rows "group 2" elements; the extra unknowns are "branch currents".

**Where it appears:** `docs/solver.md` (Component Set — transformer; Islands) and `docs/api.md` §6 (transformer stamping).

**References:** Ho, Ruehli & Brennan (1975), "The Modified Nodal Approach to Network Analysis", IEEE Trans. Circuits and Systems; Vlach & Singhal, *Computer Methods for Circuit Analysis and Design*.

### Back-feed
Power flowing "backward" through a closed coupler (e.g. a breaker) — from the side conventionally regarded as the output into the side regarded as the input.

In real electricity a closed breaker is just a low-resistance metallic connection: current flows whichever way the voltages push it, so back-feed is ordinary physics, not an anomaly. Vanilla Stationeers, by contrast, models its breaker family as a *directional* Input→Output transfer device — power can only move one way, like a one-way queue. Manatee's closed breaker deliberately stamps a galvanic, bidirectional 1 mΩ series branch into the merged matrix, so the coupler's reported through-flow is *signed*, and the Re-Volt adaptor re-derives vanilla's directional metrics (accumulated throughput, trip comparisons, analyzer readouts) from that signed flow. The consequence — back-feed becomes possible where vanilla forbade it — is a flagged design divergence requiring Sukasa sign-off, and any doc asserting the breaker is a "galvanic bridge" must carry this caveat. Programming analogy: we replaced a unidirectional channel with a shared variable, then reconstructed the old channel's counters as a view over it.

**Standard term:** back-feed / reverse power flow (standard in power engineering, e.g. distributed generation feeding "upstream").

**Where it appears:** `docs/api.md` §7 (Breaker CouplerSpec) and §23 items 1, 9; `docs/stationeers.md` revision-pending note.

**References:** Horowitz & Hill, *The Art of Electronics* (for the underlying point that a closed switch has no direction).

### ballast (series ballast)
A current-limiting element placed in series with a load that cannot regulate its own current — in our curriculum, the resistor that keeps an arc lamp from destroying itself.

An arc lamp has *negative differential resistance*: once the arc strikes, drawing more current makes the arc's voltage drop *lower*, which draws still more current — a positive-feedback runaway. A series ballast (classically a resistor or, in AC systems, an inductor) breaks the loop: as current rises, the ballast drops more voltage, leaving less for the arc, so the combination settles at a stable operating point. For programmers: the arc alone is an unstable fixed point, like a retry loop whose backoff *shrinks* on failure; the ballast adds the damping term that makes the feedback converge. In Manatee this is gameplay — Arc 2 of the design doc makes arc lamps "require series ballast" precisely so negative differential resistance becomes a lesson players hit in practice.

Standard term; "ballast resistor" and (for fluorescent/discharge lighting) just "ballast" are the common forms.

**Where it appears:** design.md, Arc 2 (power engineering) — arc lamps.

**References:** Horowitz & Hill, *The Art of Electronics* (negative resistance and biasing/stability).

### Behavioral two-port / behavioral device
- A device simulated by a prescribed input/output rule at its terminals — power in, power out, an efficiency curve, a state-of-charge integrator — rather than by the physics or semiconductors inside it; a **behavioral two-port** is the two-terminal-pair form used to couple two islands.

A battery, for example, ships as EMF + internal resistance + a state-of-charge integrator — a recipe for terminal behavior — rather than an electrochemical model; converters and chargers transfer power by a rule rather than simulating switching electronics. Each game tick the device's optional `Tick(dt)` reads its terminal quantities, applies its own rules (efficiency curve, SoC integration, its own limits), and updates the simple linear elements it presents to the solver (sources, resistors), which cannot tell the difference — only the contract (terminal voltages/currents, conservation laws) is tested. For a programmer this is a mock or stub that honors the interface without implementing the internals; the payoff is cost control: physics-level semiconductor models are nonlinear and would drag Newton iteration into the per-substep hot path, so world islands use behavioral models wherever one serves and stay linear (semiconductor-level power electronics is an explicit design.md non-goal for VS gameplay).

The **behavioral two-port** applies this at a device with two terminal pairs (ports) whose port-to-port relationship is a prescribed rule — our standard way to couple two islands, e.g. a rectifier-flavored converter bridging an AC island and a DC island. Each side stamps into its *own* island's matrix as an ordinary source/load, and the rule (e.g. "power drawn on port A appears, minus losses, as regulated output on port B") is applied between solves, so the AC side never sees the nonlinearity a real rectifier would introduce and each island stays linear in its own domain. Programmers can read it as two services connected by a message contract instead of shared memory — each island solves independently and the two-port exchanges a small summary (power, or amplitude+phase for decoupling transformers) per substep. Contrast the *idealized transformer*, a same-matrix two-port whose ports live in one matrix coupled by a turns-ratio constraint; transformers are deliberately *not* on the behavioral list (the local one is the ideal two-port primitive, the utility-scale one a decoupling island boundary). A testing consequence: ngspice has no equivalent element, so these devices cannot be oracled against it — they are covered by conservation invariants and hand-computed cases, and the exporter reports them as `Unrepresentable`.

**Standard term:** behavioral model / macromodel (SPICE's B-sources and subcircuit macromodels are the standard analogues); "two-port" is the standard EE term for a device characterized by two terminal pairs. Our coinage adds the project-specific role of a behavioral two-port being an island-coupling boundary evaluated between solves — closer to co-simulation's relaxation/waveform-coupling boundary than to a classical two-port stamp.
- **Where it appears:** `docs/design.md` Non-Goals and Simulation Model (nonlinearity budget, AC↔DC boundaries, island coupling), battery/loot sections; `docs/solver.md` (devices layer, component-set sections, converter two-ports, coupling devices); `docs/testing-strategy.md` Oracle Tests ("what ngspice cannot oracle"); `docs/api.md` (converter P-transfer, `ConverterTwoPort`, ngspice-export `Unrepresentable` list, §6).
- **References:** ngspice manual, chapter on B-source (behavioral) devices; Vlach & Singhal, *Computer Methods for Circuit Analysis and Design*, on macromodeling and two-port formulations; Pillage, Rohrer & Visweswariah, *Electronic Circuit and System Simulation Methods* (relaxation-based coupling of subcircuits); Nagel (1975), SPICE2 memo UCB/ERL-M520.

### Bias
- A constant DC offset added to a source's output waveform — the "center line" the waveform rides on.

In the Falstad file format (and therefore our importer), a voltage source line carries `waveform freq maxVoltage bias phaseShift`. For a DC source the output is `maxVoltage + bias`; for an AC source it is `maxVoltage·sin(2πft + phaseShift) + bias`, where `maxVoltage` is the *peak amplitude, not RMS*. For a programmer: bias is a plain additive constant applied after the waveform function — `f(t) + bias` — like an offset applied after a computed value. A quirk worth knowing: because DC output is the *sum* of the two fields, a plain 5 V DC source can legally be written with the 5 in either field; our own lesson corpus rule mandates putting the DC value in `maxVoltage` and leaving `bias` at 0, so files stay unambiguous and load identically in circuitjs.

The term is standard EE usage ("DC bias," "DC offset" — the same knob is labeled *offset* on a bench function generator), here pinned to Falstad's specific field semantics.
- **Where it appears:** falstad-format.md §4 (voltage-source semantics, importer accept-list, corpus rule); curriculum.md lesson files.
- **References:** Horowitz & Hill, *The Art of Electronics*, on DC offset/bias in signal sources; Falstad circuitjs1 source (`VoltageElm.java`), which the format doc cites line-by-line.

### Boundary coupler / power-transfer boundary
- A coupling device that connects two islands (independently solved sub-circuits) *without* merging them into one matrix: the islands stay separate, and the device passes power between them by exchanging measured values each substep — as opposed to a *galvanic* coupler (closed breaker, bus-tie) that merges both sides into one shared matrix.

Manatee-core has one coupler abstraction, but two behaviors hide behind it. A *galvanic bridge* puts both sides in one matrix (real wire-level connection) and carries no solver-side state, so replacing it is cheap. A *power-transfer boundary* — `DecouplingTransformer` (AC) and `ConverterTwoPort` (Stationeers-style DC transformer/charger) — **always** keeps the two islands as separate matrices and instead exchanges amplitude and phase per substep (power/voltage per tick on the DC side), mirroring the physics of a transformer or converter that transfers power with no metallic connection. Two mechanisms keep the back-and-forth handoff from oscillating: the transformer exchange is damped by an explicit relaxation factor α (`RelaxationAlpha`, exponential smoothing, clamped at 0.7 — the proven stability bound), while the DC converter two-port is deliberately *unclamped*, its charge-controller loop being provably contracting. Islands joined by boundary couplers are scheduled as one lockstep unit — they substep together, so each side is never more than one substep stale (see *bounded lag*) — and each boundary carries an energy ledger so any bookkeeping surplus becomes heat in the coupler, never free energy.

Because a real transformer core and a DC converter's DC-link capacitor store energy, a boundary coupler also carries **real core state** in core: the `CouplerRuntime` scalars. This has a practical consequence for integrators (tutorial appendix, sharp edge 17): when re-routing a boundary coupler you must snapshot the A-side island's state before the edit and restore it after — same-`StateKey` matching re-attaches the runtime — whereas a galvanic coupler carries nothing over. Programming analogy: a galvanic coupler is a stateless proxy you can recreate freely; a boundary coupler is a stateful connection object whose session state must survive reconnection, and message-passing between two independent solvers whose message rate (per substep) and low-pass filter (α) are chosen so the distributed system stays stable.

**Standard term:** the general technique is *co-simulation / waveform relaxation* (partitioned solvers exchanging boundary values iteratively); "boundary coupler" and "power-transfer boundary" are the project's names for the device role. Contrast the general umbrella **coupling device**, which covers both the galvanic and boundary kinds.
- **Where it appears:** `docs/api.md` §7 (`CouplerSpec.DecouplingTransformer`, `ConverterTwoPort`, `AddCoupler`, `CouplerId`, `CouplerRuntime`, `StateKey`), §11 (scheduling units in `IslandTable`); `docs/solver.md` Islands; `docs/stationeers.md` (transformer = power-transfer boundary); `docs/design.md` island coupling; `docs/integration-tutorial.md` §7 (sharp edges 9 and 17).
- **References:** Lelarasmee, Ruehli & Sangiovanni-Vincentelli (1982), "The Waveform Relaxation Method for Time-Domain Analysis of Large Scale Integrated Circuits", IEEE Trans. CAD.

### branch current / signed flow / aux flow
- The current flowing through a single component (a "branch" of the circuit), reported with a sign that says which direction it flows relative to the component's declared terminal order.
- `Solution.Current(id)` returns this as a plain `double`: positive means flow in the component's convention direction (first terminal → second terminal), negative means the reverse — like a bank-account delta, magnitude plus direction in one number. For programmers: MNA (our matrix formulation) solves primarily for node *voltages*, so most branch currents are derived values (Ohm's law on the solved voltages); but voltage sources and inductors get an extra unknown — an **auxiliary current row** in the matrix — because their current cannot be computed from voltages alone (a voltage source pins the voltage and lets current be whatever the circuit demands). We call the solved value of that extra row the component's **aux flow**. It matters operationally: during a merge window (the tick between a breaker closing and the merged island's first solve), node voltages are held at last-good values, and since 2026-07-07 the aux flows of absorbed-side sources are captured too — otherwise a source would read a fictitious 0 A for one tick right after a breaker close.
- **Standard terms:** "branch current" is standard; the auxiliary row is standard MNA "group 2" / auxiliary-current-variable treatment. "Signed flow" and "aux flow" are project shorthand for these.
- **Where it appears:** `docs/api.md` §10 (`Solution.Current`, tier-1, 0-allocation), §17.4 and decision log #27 (merge-window last-good capture of aux flows).
- **References:** Ho, Ruehli & Brennan (1975), "The Modified Nodal Approach to Network Analysis", IEEE Trans. Circuits and Systems, CAS-22(6) — the origin of the auxiliary current rows; Vlach & Singhal, "Computer Methods for Circuit Analysis and Design", ch. 4.

### breaker
- A switch-like safety device that connects or disconnects two whole sub-circuits, which in Manatee is modeled as an island *coupling device*: closed means the two islands are merged into one solver matrix, open means they are simulated separately.
- For an EE this is just a circuit breaker; the project-specific part is its API citizenship. Clients drive a breaker via `Reconfigure` through a `CouplerId` handle — and coupler handles are *document-stable*: unlike ordinary component handles, they survive every island rebuild and merge, so the tutorial's rule is "cache your breaker handles forever". Closing is an incremental merge (cheap, preserves everyone else's handles); opening is a tier-3 island rebuild of both halves (the honest cost of splitting a matrix in two — accepted because breakers, being safety devices, change state rarely). For programmers: think of an island as one in-memory database, and a breaker as the operation that merges two databases (fast, additive) or splits one (a rebuild that reissues that island's row handles). Two sharp edges from the docs: a removed-and-re-added breaker defaults to **Closed** and its Open/Closed state is the client's to re-apply (core does not carry it over); and moving a breaker's ports is done as one batch (remove + re-add same key), which works because removals apply before additions (decision log #28).
- **Standard term:** circuit breaker. The "coupling device" framing — a breaker lives at the islands layer, not inside a matrix — is project-specific; contrast "relay", which *is* an in-matrix netlist switch (the relay-vs-breaker duality).
- **Where it appears:** `docs/design.md` R15 and Hazards; `docs/stationeers.md` "Islands and Coupling Devices"; `docs/integration-tutorial.md` §5–6 and appendix item 17; API surface: `CouplerId`, `Reconfigure`, `CostOfReconfigure`.
- **References:** Horowitz & Hill, "The Art of Electronics" (3rd ed.) — practical treatment of breakers and fault protection.

### brownout / dropout hysteresis
- The R18 undervoltage behavior of an adapted (legacy Stationeers) device: below a low threshold `V_low` it drops its load and reports to the vanilla game that it received nothing, and it only rejoins once voltage recovers past a higher threshold `V_high` — the deliberate gap between the two being hysteresis.

In real power systems a brownout is a sustained undervoltage — lights dim instead of going dark — and Manatee ships that condition as real physics (a net sagging under overload). The term also names the adaptor's *response*. Unconverted devices are modeled as constant-power loads, which under a sagging supply draw *more* current and drag voltage down further (the voltage-collapse death spiral, kept as honest physics above the threshold); below `V_low` the adaptor sheds the device instead. If drop and rejoin used the *same* threshold, an overloaded net would strobe: drop, voltage recovers, rejoin, voltage collapses, repeat once per tick — a feedback loop with no stable fixed point, the electrical analogue of two processes livelocking on a shared resource. The two-threshold gap breaks it, the same idea as a thermostat that turns on at 18° and off at 22° so it doesn't chatter; for a programmer it is debouncing — a two-state machine whose transitions require crossing *different* thresholds in each direction, so the device's connect state depends on its history, not just the instantaneous voltage. R18 (settled 2026-07-06) breaks the loop three ways: separated drop/rejoin thresholds (hysteresis), rejoin delays staggered per device so the whole population doesn't slam back simultaneously (a thundering-herd guard), and a recloser-style lockout that trips after repeated brownouts in a short window and requires manual reset. Together these guarantee the legible outcomes the design wants: the net either settles at reduced load or collapses honestly — never flickers at tick rate. The fuzz-test contract is that randomized constant-power loads under a falling supply must settle or brown out within k ticks, never oscillate unboundedly.

Standard term; the electronics-design equivalent is **undervoltage lockout (UVLO)** with hysteresis (Schmitt-trigger thresholds), the grid-scale equivalent is **undervoltage load shedding**, and the lockout mirrors utility **autoreclosers**.
- **Where it appears:** design.md R18 (settled 2026-07-06); testing-strategy.md "Property and Fuzz Tests" (adaptor stability); stationeers.md Legacy-Device Adaptor; integration-tutorial.md §4; the `AdaptedLoad` model and its constructor parameters in the API (`brownoutLowVolts`, `brownoutHighVolts`, `staggerBaseTicks`, `staggerSpreadTicks`, `lockoutCount`, `lockoutWindowTicks`) — e.g. `new AdaptedLoad(advertisedWatts: 200.0, gMin: 1e-6, gMax: 1e3, brownoutLowVolts: 50.0, brownoutHighVolts: 70.0, ...)`.
- **References:** Horowitz & Hill, *The Art of Electronics* (comparator hysteresis / Schmitt trigger, the canonical two-threshold anti-chatter technique).

### bus-tie
- A switch that joins two otherwise separate power buses (distribution networks) into one, or splits them apart again.

In real switchgear, a bus-tie (or bus coupler / tie breaker) lets an operator run two bus sections as one system or isolate them. In our Stationeers integration, a closed bus-tie is a **galvanic bridge**: it electrically merges the two vanilla `CableNetwork`s into a single solver island (tracked by union-find over networks, keyed on coupler state), even though the game's own network identities never merge. Opening it splits the island back into two, which triggers a rebuild of both halves — acceptable because, like a breaker, it is a coupling *device* rather than an in-matrix switch element. For a programmer: it is the edge in a union-find structure that, when present, unions two components; removing it forces recomputing connectivity from scratch.

This IS a standard power-engineering term; common aliases: bus coupler, tie breaker, bus sectionalizer (closely related). Our specific framing of it as a "coupling device" that merges/splits solver islands is a project design choice.

**Where it appears:** `docs/stationeers.md`, "Islands and Coupling Devices" (alongside breakers and transformers); the coupler API in `docs/api.md`.

**References:** Turan Gönen, *Electric Power Distribution System Engineering* (bus arrangements and sectionalizing); the IEEE C37 series of switchgear standards covers bus-tie/coupler equipment.

### cable type/gauge
- The kind of cable a segment is (identified by its Stationeers prefab name), which determines its per-length resistance and its current rating.

In real wiring, gauge (e.g. AWG) sets a conductor's cross-section, hence its resistance per meter and how much current it can carry before overheating. Our Stationeers pipeline mirrors this: the intake format carries a per-cable prefab name (an agreed addition to Re-Volt's `NetworkExport`, negotiated with Sukasa), and `CableSpecs.For(prefabName)` maps it to a `ConductorSpec` giving resistance (resistance = ohms-per-length × length, where the prefab name selects the ohms-per-length value) and limit ratings (ampacity plus an i²t thermal model, from which melting/fusing behavior is derived rather than stored as a separate field). For a programmer: the prefab name is a key into a lookup table of material properties — the same role a CSS class plays for styling, applied per edge of the graph. Ratings feed the compaction layer's limit envelopes, so a thin cable spliced into a heavy run behaves like the fuse it physically is.

**Standard term:** wire gauge / conductor size and ampacity (e.g. AWG tables); "prefab name" is game-engine vocabulary for a reusable object template.

**Where it appears:** `docs/stationeers.md`, "Graph Construction" (agreed export additions); `CableSpecs.For` in `docs/api.md` §22.a; `ConductorSpec`/`LimitSpec` in `docs/api.md` §12 and §19; `docs/compaction.md` (limit envelopes).

**References:** NFPA 70 (National Electrical Code), Article 310 ampacity tables — the standard source for conductor sizes and current ratings.

### call button
- A momentary push-button in the tablet's elevator-controller lesson that requests the elevator car to a floor.

In Lesson II.10 (the relay-logic capstone), call buttons are the user inputs: pressing one closes a switch only while held, so the circuit needs a latching relay to remember the request after the finger lifts — the lesson's whole point is building that memory out of relays, interlocks, and limit switches, mirroring the in-world elevator build. Electrically it is just a normally-open momentary switch (a tier-2 conductance change when toggled). For programmers: a momentary button is an edge-triggered event, and the latch that captures it is a one-bit state machine implemented in hardware; the lesson is literally "build a flip-flop from relays".
- Standard term from real elevator practice ("hall call button"); electrically a normally-open momentary pushbutton.
- **Where it appears:** `docs/curriculum.md` (Lesson Arc II.9–10), `docs/design.md` (elevator progression arc).
- **References:** Horowitz & Hill, *The Art of Electronics* (switches, relay logic, and latching circuits).

### capacitor
- A two-terminal component that stores energy in an electric field: it accumulates charge when voltage is applied and releases it later.

A capacitor's defining relation is `i = C·dv/dt` — current flows only while the voltage across it is *changing*, so it blocks steady DC once charged and passes AC more easily the higher the frequency (its impedance falls as frequency rises). For a programmer: it is a stateful component, an accumulator variable — the voltage across it is memory that persists between simulation ticks, which is why circuits with capacitors need transient analysis (time-stepped integration via companion models) rather than a single static solve. In the tablet curriculum it appears twice: Lesson 4 uses the RC charging curve to introduce time-dependent behavior (voltage approaching its target exponentially, like a low-pass-filtered or exponentially smoothed value), and Lesson 15 uses capacitors and inductors on AC to introduce impedance and phase.

**Standard term:** yes — universal EE vocabulary; unit is the farad (F).

**Where it appears:** `docs/curriculum.md` Lesson Arc, lessons 4 (RC charging curve) and 15 (impedance on AC); solved as a companion-model device in the transient analysis of `docs/solver.md`.

**References:** Horowitz & Hill, *The Art of Electronics* (capacitors and RC circuits, ch. 1); Nagel (1975), *SPICE2: A Computer Program to Simulate Semiconductor Circuits*, UCB/ERL-M520 (companion-model integration of capacitors in transient analysis).

### capacitor (`c`)
- The Falstad text-format element line, dump type `c`, that encodes a capacitor: a two-terminal component that stores energy in an electric field and whose defining property is capacitance, in farads.

  For programmers: a capacitor is the circuit world's stateful variable — its voltage is an integral of past current, so unlike a resistor it carries memory between timesteps (charge builds up like a level in a leaky bucket). The `c` line carries the static parameter `capacitance` (F) plus a *state* field `voltdiff` (the voltage across it at save time — Falstad files snapshot live simulation state, not just topology), an optional `initialVoltage`, and an optional `seriesResistance` that is only present when flag bit 4 is set (a positional-append versioning quirk of the format). Our importer honors `voltdiff`/`initialVoltage` as the initial condition and realizes `seriesResistance` as a genuine series resistor; an EE would recognize that last one as ESR, the small unavoidable resistance of a real capacitor.

  Standard term throughout; `c` is Falstad/circuitjs1's dump type for `CapacitorElm`, and the SPICE equivalent element letter is `C`.
- **Where it appears:** `docs/falstad-format.md` §4 (field list, with `CapacitorElm.java` line cites), §5 (the `flags&4` gating), §7 (importer accept contract).
- **References:** Horowitz & Hill, *The Art of Electronics* (capacitor fundamentals); Nagel (1975), *SPICE2: A Computer Program to Simulate Semiconductor Circuits*, UCB/ERL-M520 (capacitor companion models in simulators); falstad.com/circuit (circuitjs1) source, `CapacitorElm.java`.

### capacitor voltage
- The voltage currently across a capacitor — the piece of dynamic simulation state that a capacitor contributes, which must survive save/load exactly.

  Physically, a capacitor's voltage records its accumulated charge (V = Q/C), so it is the one number that summarizes the component's entire history; the next timestep's behavior depends on it. In programming terms it is mutable state, not configuration: capacitance is a constant you could recompute from the netlist, but capacitor voltage is like a counter mid-run — lose it and the simulation restarts that element from cold. Requirement R6 therefore demands that capacitor voltages (alongside inductor currents and device state) serialize losslessly in snapshots. This matters especially for Stationeers, where the game rebuilds cable networks from scratch on every load: topology is reconstructible, but capacitor voltages must ride manatee-core's own snapshot/restore hook, keyed by network RefIds, with a failed restore degrading to a cold start of that island rather than a failed load.

  Standard concept; simulators call the same thing a capacitor's *initial condition* or *state variable*.
- **Where it appears:** `docs/design.md` R6 (State snapshot/restore); `docs/stationeers.md` Persistence; also surfaces in the Falstad format as the `voltdiff` field on `c` lines.
- **References:** Pillage, Rohrer & Visweswariah, *Electronic Circuit and System Simulation Methods* (state variables and initial conditions in transient analysis); Vlach & Singhal, *Computer Methods for Circuit Analysis and Design*.

### Center-off
An optional middle position on a Falstad SPDT switch in which the moving contact touches *neither* output — Manatee's importer rejects circuits that use it.

An SPDT (single-pole double-throw) switch normally connects one common terminal to exactly one of two "throw" terminals — electrically a two-state selector. The center-off variant adds a third, neutral state where the common terminal is connected to nothing, making the switch a three-state device: throw 0, throw 1, or open. In the Falstad classic text format this is encoded as bit 0 of the `S` element's flags field (`flags&1`). For a programmer: it turns a boolean selector into a nullable one — an enum `{A, B, None}` instead of `{A, B}`. Manatee's Falstad importer rejects the center-off flag loudly (alongside `link != 0` ganging and `throwCount > 2`) rather than silently approximating it, per the importer's accept/reject contract.

The term is standard EE/switch-catalog vocabulary; such switches are commonly listed as "ON-OFF-ON" or SPDT center-off.

**Where it appears:** `docs/falstad-format.md` §4 (the `S` / SPDT switch element, `flags&1`) and §7 (importer accept/reject list).

**References:** Horowitz & Hill, *The Art of Electronics* (switch types and conventions).

### companion model
- The standard circuit-simulation trick of replacing an element the linear solver cannot handle directly (a capacitor, inductor, or nonlinear device) with an equivalent resistor-plus-source whose values are recomputed each timestep or iteration.

  MNA solves linear equations, so anything with memory or curvature must be locally linearized. Discretizing a capacitor with Backward Euler yields a conductance G = C/dt in parallel with a current source I = G·V_prev (inductors are the mirror image); the source term carries the element's history. For a programmer: it is memoized local linearization — each element exposes a "pretend I'm a resistor and a source" interface, with state threaded through the source value like an accumulator in a fold. In Manatee (R2), Backward Euler companion models are the transient discretization (chosen for L-stability: "boring but never blows up"); because dt is held fixed, the companion conductances are constant and a linear island in steady operation only updates right-hand-side values (tier 1, the load-bearing performance fact for subcycled AC). The same pattern covers nonlinearity: Newton–Raphson re-stamps linearized companions per iteration, and the Stationeers legacy-device adaptor stamps constant-power loads as G = P_wanted/V_prev² — a companion-model-style linearization applied *across game ticks* rather than within a Newton loop. Sign or history errors in companion models are caught by the energy-conservation invariant checks, not by inspection.

  This is standard SPICE terminology; also called the *discretized companion circuit*, *companion network*, or (for the linear-source form) *Norton/Thévenin equivalent* of the discretized element.
- **Where it appears:** `docs/design.md` R2; `docs/solver.md` Analyses and Change-Cost Tiers; `docs/stationeers.md` Legacy-Device Adaptor; `docs/testing-strategy.md` Invariant Checks.
- **References:** Nagel (1975), *SPICE2: A Computer Program to Simulate Semiconductor Circuits*, UCB/ERL memo M520; Pillage, Rohrer & Visweswariah, *Electronic Circuit and System Simulation Methods* (McGraw-Hill); Vlach & Singhal, *Computer Methods for Circuit Analysis and Design*.

### complex-valued / phasor solve path
- A deliberately deferred solver mode that would compute steady-state AC quantities with complex arithmetic in one shot, instead of stepping the waveform through time.

  A *phasor* represents a sinusoid by a single complex number (amplitude and phase), turning the differential equations of steady-state AC into ordinary algebra — one complex linear solve replaces many timesteps. For a programmer: it is a closed-form fast path that answers "what is the steady state" without running the simulation, valid only when everything is linear and sinusoidal at one frequency — like replacing a loop with its closed-form sum. Manatee explicitly does *not* build this yet: R3 makes waveform (time-domain, subcycled) simulation the primary AC mode, because visible 5 Hz lamp flicker and a real oscilloscope trace are the educational point, and phasor analysis would compute them away. The complex-valued path is reserved as a later optimization for *metering only* (RMS/power readouts on stable islands); the component stamp interface reserves room for a complex-frequency stamp alongside the time-domain one (R2), so adding it later is an extension, not a rework.

  Standard terminology: this is SPICE's **AC small-signal analysis** (`.AC`); "phasor analysis" is the standard EE name. Our nonstandard bit is only the priority inversion — most simulators treat frequency-domain AC as a primary mode, we treat it as an optional cache in front of the waveform simulation.
- **Where it appears:** `docs/solver.md` Analyses ("complex-frequency stamp is a later addition") and Numerics ("Complex-valued solve path deferred"); `docs/design.md` R3 and non-goals ("Frequency-domain-only AC").
- **References:** Ho, Ruehli & Brennan (1975), "The Modified Nodal Approach to Network Analysis," *IEEE Trans. Circuits and Systems*; the ngspice manual (AC analysis chapter); Horowitz & Hill, *The Art of Electronics*, for phasors and complex impedance.

### compliance
- The voltage limit of a current source: the maximum voltage the source will develop across its terminals while trying to force its set current through the circuit.

An ideal current source pushes its programmed current no matter what, which means its terminal voltage rises to whatever the circuit demands — including absurd values if the path is nearly open. Real lab current sources have a *compliance voltage*: a ceiling above which they give up on regulating current. In programming terms, compliance is a saturating clamp on the source's output — the device holds the invariant "current = setpoint" only while the required voltage stays under the ceiling, then degrades to "voltage = ceiling". In Manatee this appears only in the Falstad importer: post-2025 circuitjs1 files may carry a `maxVoltage` compliance parameter on the `i` (current source) element, and our importer **rejects any nonzero value loudly** rather than silently modeling a piecewise source — our current sources are ideal.

This is standard EE vocabulary ("compliance voltage"); common in datasheets and lab-instrument manuals.

**Where it appears:** `docs/falstad-format.md` §4 (the `i` element's `[maxVoltage]` parameter) and §7 (the accept/reject contract: reject nonzero compliance).

**References:** Horowitz & Hill, *The Art of Electronics*, 3rd ed. (current sources and their compliance range, ch. 2); Falstad/circuitjs1 source, `CurrentElm.java`.

### components
- The circuit elements — resistors, capacitors, sources, switches, and so on — that a user places into a schematic.

In the harness/tablet context, components are entries in the **document model** (layer 1 of the schematic engine): pure data records with a kind, parameters (say, 4.7 kΩ), a position, and terminals that wires connect to. They are what gets drawn, selected, dragged, and undone by the interaction layer, and what the netlist-extraction step translates into solver `Add*` calls. For programmers: a component in the document is like a node in a scene graph or an element in a DOM — declarative state, no behavior of its own; the physics only happens after extraction into manatee-core. For the EE this is just the ordinary meaning of the word — the parts on the schematic — with the caveat that in our layered design a schematic component and its solver counterpart are distinct objects linked by the extraction binding.

This is the standard term; SPICE-family tools say "element" or "device" for the netlist-side counterpart.

**Where it appears:** `docs/harness.md` "Layering" #1 (document model); solver-side counterparts throughout `docs/solver.md` and `docs/api.md`.

### Conductance (G) / siemens
- Conductance G = 1/R (unit: siemens, S) is the reciprocal of resistance — how easily current flows through an element — and the native quantity the solver actually writes into the matrix and mutates through its API.

Users and devices think in ohms, but MNA stamps G: a resistor of conductance G between nodes a and b adds +G to the two diagonal entries and −G to the two off-diagonals. Programming analogy: ohms are the user-facing API type and siemens the internal (canonical wire) representation — the netlist converts at the boundary, and everything downstream is defined in the conductance domain because that is what the math consumes. The legal stamped range is [1e-9, 1e3] S — a 1 GΩ open switch to a 1 mΩ closed switch / DC inductor short, SPICE-conventional values — with gmin = 1e-12 S; the cap exists so extreme ratios cannot push an island over the conditioning cliff, and a fuzz axis sweeps random circuits across the full legal range in CI.

Three API surfaces make the unit choice load-bearing. First, changing a conductance is by definition a **Tier-2** change (`Adjust`): the matrix values move but its sparsity pattern does not, so the solver refactorizes numerically while reusing the symbolic plan. Second, the `AdjustEpsilon` **ε-no-op gate** decides whether an `Adjust` is a free no-op or a real refactorization by testing whether G_new/G_old lies within pinned bounds semantically equal to exp(∓ε), default ε = 1e-4 in the log-conductance domain — a *ratio* test, because "1% change" should mean the same thing at 1 mΩ and 1 GΩ; the bounds are stored double constants and the gate is one IEEE division plus two comparisons, so classification is bit-identical across CPU architectures (no transcendental is ever evaluated), with anything at or below gmin comparing equal. Third, `WiringPolicy.ReferenceBound(returnConductanceSiemens = 1e3)` names its parameter in siemens: the near-ideal return path each Stationeers device gets to its network's reference node. Conductance is also how a nonlinear "give me N watts" load is expressed to the linear solver: the R18 adaptor reads last tick's voltage and stamps G = P/V_prev² (clamped to the legal range), so within one tick the device is just a resistor — see **constant-power load / linearization (G = P/V_prev²)**.

**Standard term:** conductance (symbol G, unit siemens; "mho" in older literature). The ratio-based ε-gate is project-specific API design, not an EE term.
- **Where it appears:** design.md R4, R18, Grounding model; solver.md Change-Cost Tiers, Numerics (conductance-range policy), Analyses; testing-strategy.md conductance-range sweep; api.md §4 (Tier 2 `Adjust`, `CostOfAdjust`), §9 (`AdjustEpsilon` pinned definition), §5 (`ReferenceBound(returnConductanceSiemens)`).
- **References:** Ho, Ruehli & Brennan (1975), "The Modified Nodal Approach to Network Analysis", IEEE Trans. Circuits and Systems; Vlach & Singhal, *Computer Methods for Circuit Analysis and Design*; Horowitz & Hill, *The Art of Electronics*; the ngspice manual.

### constant-power load / linearization (G = P/V_prev²)
- A circuit element that draws (or supplies) a fixed number of watts regardless of the voltage across it, so its current is I = P/V — nonlinear — and the technique the Stationeers adaptor uses to feed it to the linear solver: each game tick, present it as a plain conductance G = P/V_prev² sized so that *at last tick's voltage* it would draw exactly the wanted power.

A resistor's current is proportional to voltage; a constant-power element's current goes *up* as voltage goes down, because it insists on its wattage. That inverse relationship cannot be written as a fixed conductance, so a linear solver can't stamp it directly. In Manatee this is the model for every unconverted vanilla Stationeers device (R18): the vanilla contract is four inconsistently-overridden power calls (available/needed, provide/draw), which the Legacy-Device Adaptor reduces to a single advertised-watts number (`AdaptedLoad.SetAdvertised`) and translates into something the linear-algebra core can stamp. Since P = V²·G for a conductance, setting G = P_wanted / V_prev² makes the stamped resistor consume the advertised watts if the voltage doesn't move; when it does move, next tick's re-stamp corrects the aim. This is "linearized **across ticks**": instead of iterating to convergence inside one solve (as SPICE's Newton loop does), Manatee spreads the iterations over game ticks — one cheap linear solve per tick, converging over a few ticks. For a programmer it is a fixed-point iteration where the game loop *is* the iteration loop, and each step is a tier-0 `Adjust` that becomes a free ε-no-op once settled (counted in `TickStats.AdjustNoOps`), so a converged fleet of loads costs zero refactorizations — you never write convergence detection yourself. In the integration tutorial this is a four-line per-tick body: read last tick's voltage from `ctx.Previous`, compute `g = Watts/(V·V)`, clamp to `[GMin, GMax]`, and `ctx.Adjust` the device's resistor to `1/g`.

The raw update is guarded three ways: G is **clamped** to `[GMin, GMax]` (shed to `GMin` when the read voltage is effectively dead, else `V_prev ≈ 0` stamps a near-short); **brownout hysteresis** drops the load below `V_low` and rejoins above `V_high` (with staggered per-device rejoin delays and recloser-style lockout) so an overloaded net collapses legibly instead of strobing at tick rate; and an across-tick **energy ledger** prevents the one-tick lag from being pumped for free energy. Manatee ships the destabilizing physics on purpose — because a constant-power load pulls *more* current as voltage sags (I = P/V), a group of them on a weakening supply is prone to the voltage-collapse death spiral, an intended gameplay feature — but the *simulation* must fail gracefully, so a dedicated property test requires that randomized sets of constant-power loads under a falling supply either settle or brown out within k ticks, never oscillating unboundedly (exercising the clamps, hysteresis, staggered rejoin, and lockout together). The stock `AdaptedLoad` wraps all of this. For the EE: this is the standard constant-power load (CPL) — the "P" leg of the ZIP load model, notorious for its negative incremental resistance (dV/dI < 0) — handled by a companion-model/successive-substitution linearization spread across game ticks rather than Newton iterations within one solve.

**Standard term:** constant-power load (CPL); the constant-P component of the ZIP load model. The per-step relinearization is the companion-model / successive-substitution pattern from circuit simulation applied per game tick — a fixed-point update, not a Newton step.
- **Where it appears:** design.md R18; stationeers.md, The Legacy-Device Adaptor; testing-strategy.md, Property and Fuzz Tests ("Adaptor stability"); integration-tutorial.md §4 (the runnable per-tick body, `AdaptedLoad` constructor, ε-no-op behavior); runnable authority in `examples/RevoltWalkthrough/`.
- **References:** Vlach & Singhal, *Computer Methods for Circuit Analysis and Design* (nonlinear element handling); Pillage, Rohrer & Visweswariah, *Electronic Circuit and System Simulation Methods* (companion models and linearization); Nagel (1975), *SPICE2: A Computer Program to Simulate Semiconductor Circuits*, UCB/ERL-M520 (the in-solve Newton alternative this deliberately trades away).

### contact cross-section
The area over which two adjacent conductor regions physically touch, which — combined with material resistivity — determines the electrical resistance between them.

When the compaction layer groups voxels or cable segments into equipotential regions, each boundary between two resistive regions gets a resistance computed from the material's resistivity and the size of the touching face: a big contact area means low resistance, a narrow one means high resistance (the same reason a thin wire resists more than a thick one). The narrowest cross-section along a run is also where limit attribution points: current density (amps per unit area) peaks there, so when a collapsed equivalent resistor trips a limit, "which voxel melts?" is answered by finding the constituent with the smallest cross-section, evaluated per limit type. For programmers: think of it as the bottleneck edge in a flow network — capacity is proportional to area, and overload always manifests at the choke point. This is what makes a lead segment in a copper run *be* a fuse with zero special-casing.

This is standard physics usage (resistance R = ρ·L/A, where A is the cross-sectional area); "contact cross-section" simply names the A at a region boundary.

**Where it appears:** `docs/compaction.md` Responsibilities #1 (region building) and #4 (limit attribution); `docs/api.md` §19 (attribution rule); underpins R12's "cross-sectional area = ampacity" in `docs/design.md`.

**References:** Horowitz & Hill, *The Art of Electronics*, ch. 1 (resistivity and wire resistance).

### contact resistance / electrode
The grounding electrode's soil-contact resistance treated as a full-fledged circuit element: it has a value (tens of ohms, lower when wet) *and* its own limit envelope, so overloading it has physical consequences.

Beyond simply existing (see *contact resistance (per-electrode)*), the electrode's contact resistance participates in the limits system like any conductor: sustained overload first dries and then glassifies the surrounding soil, *raising* the contact resistance. This is honest negative feedback — the harder you push current into one rod, the worse it gets — which caps earth's ampacity per electrode and makes electrode farms cost real material and land instead of deleting the wire-gauge economics. Deliberate wetting (a pool over the rod) is legal and rewarded, because it is real practice. In programming terms: the electrode is not a constant but a stateful object whose resistance degrades under abuse, closing an exploit the way rate-limiting closes a free-resource loophole.

The resistance itself is standard EE (earth electrode resistance; soil drying/glassification under fault current is a real phenomenon); wiring it into a per-element **limit envelope** is project machinery (settled 2026-07-06 under R7).

**Where it appears:** `docs/design.md` Grounding model (the glassification rule); electrode tooltips in R15, referenced by `docs/vintage-story.md` Tooltips.

**References:** IEEE Std 80, *Guide for Safety in AC Substation Grounding* (soil resistivity and electrode behavior under current).

### contact resistance (per-electrode)
The resistance — typically tens of ohms, lower when wet — between a grounding electrode and the surrounding earth, modeled as an ordinary resistor per electrode.

Earth is not a magic zero-ohm sink in Manatee: it is an ordinary solver node, and every grounding rod reaches it through its own contact resistance. That resistance is a real, teachable parameter: rod quality and soil wetness change it, which is why "bare conductors ground when wet" falls out of the same mechanism rather than being a special case. It also carries pedagogical weight — electrode tooltips (R15) attribute the voltage drop across the contact resistance and report wetness, so a player can see *why* their ground return is losing volts. In systems terms: instead of hard-coding ground as a global constant, the model makes every path to it go through an explicit, inspectable edge with a cost — no hidden shortcuts, so the economics of single-wire earth return (SWER) versus a copper return wire are computed, not scripted.

This is standard EE usage: real grounding electrodes have earth resistance in exactly this range, and "ground resistance" / "earth electrode resistance" are common aliases.

**Where it appears:** `docs/design.md` Grounding model; R15 electrode tooltips; `docs/api.md` §5 (the VS client stamps it as an ordinary resistor to the earth reference node); the SWER lesson in `docs/curriculum.md`.

**References:** Horowitz & Hill, *The Art of Electronics* (grounding practice); IEEE Std 142 ("Green Book"), *Grounding of Industrial and Commercial Power Systems*.

### contradiction (source)
A circuit that demands two incompatible things at once — canonically, two ideal voltage sources wired in parallel with disagreeing values — which the solver detects and reports rather than crashing on.

An ideal voltage source is a hard constraint: "this pair of nodes differs by exactly V volts." Put two such constraints on the same node pair with different values and the system of equations has no solution — mathematically this surfaces as a singular (non-invertible) matrix during the solve. Manatee detects the singularity and, per R9 ("legible failure"), reports it as an event with the participating components *named*, so the game layer can tell the player which sources are fighting instead of showing NaNs. For programmers: it is the constraint-solver equivalent of `x = 5` and `x = 7` in the same scope — an over-constrained system, caught and diagnosed rather than thrown as an exception. The affected island enters `Faulted`, holds its previous solution, and retries on the next structural change.

**Standard term:** an *inconsistent* or *over-constrained* network (a voltage-source loop / parallel ideal sources); SPICE-family simulators reject these as singular-matrix or topology errors.

**Where it appears:** `docs/solver.md` Failure Handling ("detected as singularity, reported with the participating components named"); `docs/design.md` R9.

**References:** Vlach & Singhal, *Computer Methods for Circuit Analysis and Design* (network solvability and singular systems); the ngspice manual (topology-check errors for voltage-source loops).

### contradictory sources
The named fault kind (`FaultKind.ContradictorySources`) an island reports when its circuit is over-constrained — e.g. two ideal voltage sources fighting — and therefore has no consistent solution.

Where *contradiction (source)* describes the phenomenon, `ContradictorySources` is its slot in the error model: one of four `FaultKind` values (alongside `Singular`, `NewtonDiverged`, `NonFinite`) carried in the `FaultDiagnostic` struct, which also names the worst component and node involved. Crucially it is a *fault kind, not an exception*: the API never throws for a bad circuit, because players build pathological circuits as a hobby (R9). Instead the island flips to `Faulted`, reads as de-energized at the read path, keeps its pre-fault solution internally for a warm-start retry, and auto-retries on the next tier-2/3 change. An EE can read this as the simulator's equivalent of a protective trip with a labeled cause on the annunciator panel, rather than the whole plant halting; a programmer can read it as an error *value* on one island rather than an exception unwinding the tick.

**Standard term:** inconsistent / over-constrained network (parallel ideal voltage sources, voltage-source loops); the enum name is project vocabulary for that condition.

**Where it appears:** `docs/api.md` §11 (`FaultKind`, `FaultDiagnostic`) and §20 Error model (the fault-handling row: held solution, de-energized reads, auto-retry); `docs/integration-tutorial.md` Faulted islands.

**References:** Pillage, Rohrer & Visweswariah, *Electronic Circuit and System Simulation Methods* (MNA formulation and solvability).

### Converter two-port
- A behavioral device (rectifier-flavored converters, chargers — the Stationeers transformer/charger family) that moves *power* between two separate islands through an efficiency curve, rather than connecting them electrically.

It has an A port on one island and a B port on another; the islands stay separate matrices. B is regulated toward an output voltage; the power actually delivered at B is measured, the efficiency curve gives η at the current load fraction, and P_in = P_out/η is drawn from A. The modeled loss P_out·(1/η−1) is computed from the curve itself (never synthesized as In−Out) and dumped as heat. The device enforces its own rated limits and clamps transfer to what island A actually delivered — no free energy — and both sides remain *linear*: neither island's matrix ever sees a nonlinear semiconductor model (simulating one is an explicit design.md non-goal for world circuits). For a programmer: it is a message-passing boundary between two independently solved subsystems — two services exchanging bounded values per substep instead of sharing one database — with a conservation audit (energy ledger) on the channel. For an EE: it is a behavioral power-processing block, like an averaged converter model, not a switching-level circuit.

**Standard term:** the "two-port" label is standard network theory; the modeling style corresponds to a *behavioral / averaged converter model*. Our specific coupling discipline (separate matrices, per-substep exchange, energy-ledger + debt enforcement) is project-defined.
- **Where it appears:** `docs/solver.md` Component Set and Islands; `docs/design.md` Simulation Model; `docs/api.md` §7 (`CouplerSpec.ConverterTwoPort`, `CouplerId`).
- **References:** Two-port formalism: Vlach & Singhal, *Computer Methods for Circuit Analysis and Design*. Averaged/behavioral converter modeling: Erickson & Maksimović, *Fundamentals of Power Electronics*.

### Converter two-port / DC-link capacitor
- The design rule that the converter two-port's output (B) port *is* a real capacitor in the B-side matrix — the DC-link capacitor — rather than an ideal regulated source, so a starved converter browns out honestly instead of lying or deadlocking.

In a real converter, a DC-link capacitor sits between the input and output stages and smooths the power transfer; we model that capacitor as an actual, honestly-sized element (`dcLinkFarads`). The A side charges it with power bounded by the efficiency curve *and by what island A actually delivered*; B's terminal voltage is simply the capacitor's voltage. When demand on B exceeds what A can supply, the capacitor drains and B's voltage sags — a legible brownout, the physically honest failure mode. The "regulated output voltage" (`outputVolts`) is the charge controller's *target*, never an ideal source: the solver may fall short of it but can never invent energy to hold it. For a programmer: the capacitor is a buffer with real depletion semantics — like a bounded queue whose fill level is the visible output, so backpressure shows up as a sagging level rather than as fabricated data. This construction also gives the island boundary its stabilizing storage "by construction" (solver.md), and unlike the transformer boundary the converter's charge-controller loop is provably contracting, so it needs no relaxation clamp.

**Standard term:** *DC-link capacitor* is standard power-electronics vocabulary; using it as the literal B-port state variable of a behavioral inter-island coupler is our project design.
- **Where it appears:** `docs/api.md` §7 (`ConverterTwoPort`, "the converter's B port IS the DC-link capacitor") and §14 (`CouplerRuntime` snapshot of the cap voltage); `docs/solver.md` Islands ("storage by construction"); `docs/design.md` grounding/storage note.
- **References:** DC-link capacitors and converter energy buffering: Erickson & Maksimović, *Fundamentals of Power Electronics*.

### coupling coefficient / couplingCoef
- A number k between 0 and 1 describing how completely a transformer's two windings share their magnetic field; in the Falstad file format it is the optional fifth parameter of the `T` (transformer) element, defaulting to 0.999.

k = 1 would be a perfect transformer: every bit of magnetic flux produced by one winding links the other, so energy transfers with no "leakage." Real transformers have k slightly below 1; the shortfall behaves like a small series inductance (leakage inductance) that softens the coupling. For programmers: k is like the reliability of a channel between two processes — at 1.0 every message gets through instantly; slightly below 1.0 some effort is diverted into a local buffer, adding lag and droop under load. Numerically, k = 1 exactly makes the transformer's two-winding inductance matrix singular (not invertible), which is why simulators — Falstad and our importer alike — default to 0.999 rather than 1. Our Falstad importer reads `couplingCoef` from the `T` element's dump line per the circuitjs1 source (`TransformerElm.java`).
- Standard term; also called the *magnetic coupling factor*. Related quantity: mutual inductance M = k·√(L1·L2).
- **Where it appears:** `docs/falstad-format.md` §4, `T` element row.
- **References:** Horowitz & Hill, *The Art of Electronics* (transformers); the ngspice manual, coupled (mutual) inductors `K` statement.

### coupling device
- Our term for a device that joins two islands (independently-solved circuit pieces), in one of two modes: merging them into a single matrix, or keeping them as separate matrices that trade values each substep.

The mode is fixed per device type. A *galvanic bridge* — a closed breaker or bus-tie, i.e. an actual metallic connection — makes the two islands one connected component, so they share one matrix and one factorization (in Stationeers, the game's two `CableNetwork`s keep their identities; only the solver island merges, via union-find keyed on coupler state). A *power-transfer boundary* — a transformer or DC converter, where energy crosses but electrons do not — keeps the islands as separate matrices coupled by exchanged power/voltage (AC: amplitude+phase per substep) with bounded lag, damped by explicit relaxation and audited by an energy ledger (see *coupling conservation*). For programmers: the first mode is merging two databases into one; the second is two services exchanging messages with at-most-one-substep latency. Note the relay-vs-breaker duality: a relay contact is deliberately *not* a coupling device — it is an in-matrix switch (tier 2), while a breaker is a coupling device whose open/close is an island merge/rebuild (tier 3), acceptable because breakers change state rarely.
- **Project-coined term**, closest standard concepts: subcircuit partitioning / tearing (diakoptics) with boundary elements; in co-simulation literature, a coupling interface between separately-integrated subsystems.
- **Where it appears:** `docs/design.md` R5 and Simulation Model; `docs/solver.md` Islands; `docs/stationeers.md` Islands and Coupling Devices.
- **References:** Vlach & Singhal, *Computer Methods for Circuit Analysis and Design* (subnetwork/tearing methods).

### current density
- Current divided by the cross-sectional area it flows through (amps per square metre); the same current is more dangerous in a thin wire than a thick one, exactly as the same water flow is more violent through a narrow pipe.

  Manatee's compaction layer collapses a chain of physical cable voxels/segments into one equivalent resistor, so when the solver raises a limit event on that resistor (overcurrent, i²t thermal, melt), something must decide *which* physical segment actually burns. The attribution rule is: evaluate current density at the narrowest cross-section, per limit type — since the whole series chain carries the same current, the segment with the smallest area has the highest density and fails first. In programming terms, it is the tie-breaker function that maps an event on the aggregated object back to the responsible member of the underlying collection — like blaming the slowest hop when an aggregated route times out. Note that different limit types (instantaneous ampacity, i²t, melt) can each pick a *different* segment in a mixed-material chain.
- Standard EE/physics term (symbol **J**, `J = I/A`); the standard usage is ours — what is project-specific is only its role as the limit-attribution rule.
- **Where it appears:** `docs/compaction.md` (Responsibilities #4, limit attribution), `docs/api.md` §19.
- **References:** Horowitz & Hill, "The Art of Electronics" (3rd ed., Cambridge, 2015) for wire ampacity practice; any introductory electromagnetics text (e.g. Griffiths, "Introduction to Electrodynamics") for the definition.

### current dots
- The animated moving dots that Falstad's circuitjs1 simulator draws along wires to visualize current flow — direction and speed of the dots indicate direction and magnitude of the current.

  In the Falstad text file format that Manatee's importer parses, this is the value-1 bit (the low bit, bit 0) of the `flags` bitfield on the `$` options header line ("show current dots"). It is purely a display preference — a rendering flag with no electrical meaning whatsoever, like a syntax-highlighting setting saved alongside source code. The importer must recognize the flag to parse files correctly, but it changes nothing about the circuit being simulated. For an EE, it is the file-format equivalent of a note saying "the oscilloscope's trace intensity knob was set here" — recorded, but irrelevant to the circuit.
- Standard terminology within the Falstad/circuitjs1 community; no textbook equivalent because it is a UI feature, not a circuit concept.
- **Where it appears:** `docs/falstad-format.md` §2 (options header, `flags` bitfield, with `CirSim.java`/`UIManager.java` line references).

### current limit
- A cap on how much current a power source is allowed to deliver, derived in our Stationeers integration from the power a vanilla generator advertises as available.

  In the Legacy-Device Adaptor, an unconverted Stationeers generator is stamped into the solver as a voltage source at its tier's nominal voltage; its current limit comes from the power it claims it can supply (roughly I_max = P_advertised / V_nominal). Crucially, we enforce the limit **across ticks**: after a solve, if the source delivered more than its limit, the adaptor adjusts what it stamps for the *next* game tick, rather than switching the element between "voltage-source mode" and "current-source mode" inside a single solve. For programmers: this is the same pattern as a rate limiter that observes usage and throttles the next request window, instead of rejecting mid-request — it keeps each solve a plain linear problem with no in-solve branching. For EEs: real bench supplies do this with CV/CC mode switching inside the regulation loop; we deliberately defer the mode change to the tick boundary (settled 2026-07-05), accepting one tick of overshoot in exchange for solver simplicity and robustness. Every adapted source also carries a small internal series resistance so paralleled generators remain well-posed.

  **Standard term:** current limiting / CC (constant-current) compliance limit on a power supply. Our across-tick enforcement is a project-specific relaxation of the usual in-solve mode switching.
- **Where it appears:** `docs/stationeers.md`, "The Legacy-Device Adaptor" (legacy generators bullet points).
- **References:** Horowitz & Hill, *The Art of Electronics*, on current-limited supplies and CV/CC operation.

### current source (`i`)
- A circuit element that forces a fixed current (in amperes) to flow through itself, regardless of what voltage that requires — the dual of a voltage source; `i` is its type code in the Falstad text format.

  Where a voltage source pins the voltage *across* its terminals and supplies whatever current results, a current source pins the current *through* itself and lets the voltage across it be whatever the rest of the circuit dictates. For programmers: a voltage source is like fixing a variable's value and letting the computation adapt; a current source fixes a *flow rate* (think: a pump that always moves N liters/second, developing whatever pressure it must). In the Falstad dialect, a positive `currentValue` drives current from post 1 to post 2 through the source (arrow toward post 2) — a sign convention we pin with an ngspice golden test rather than trusting the docs, per the "math is untrusted input" working agreement. Post-2025 circuitjs added an optional `maxVoltage` **compliance** parameter (a cap on the voltage the source will develop before it stops behaving ideally, mirroring real lab sources); our importer rejects files with a nonzero compliance value rather than approximating it.

  **Standard term:** independent current source; the `maxVoltage` cap is standard "compliance voltage" from bench instrumentation. In MNA a current source is the simplest stamp of all — two entries in the right-hand-side vector.
- **Where it appears:** `docs/falstad-format.md` §4 (element table), §7 (importer accept list — `i` accepted, nonzero compliance rejected; oracle note on sign convention).
- **References:** Ho, Ruehli & Brennan (1975), "The Modified Nodal Approach to Network Analysis", IEEE Trans. Circuits and Systems (current sources are the base case of MNA stamping); Horowitz & Hill, *The Art of Electronics* (current sources and compliance).

### currentSpeed
- An integer field in the Falstad file header (`$` line) that controls how fast the animated "current dots" move in the circuitjs UI — pure display, no electrical meaning.

  The Falstad `$` header packs simulator settings into one line: `$ flags maxTimeStep simSpeed currentSpeed voltageRange [powerBarValue [minTimeStep]]`. `currentSpeed` is the fourth field and only scales the animation of the moving dots that visualize current flow in the original simulator; it changes nothing about the solved circuit. Our importer validates the header's arity and numeric form but ignores this field (along with the other display fields), consuming only `maxTimeStep` as a transient-timestep hint. For EEs: this is like the sweep-speed knob on a scope display — it affects what you see, never the circuit under test. For programmers: it's presentation state serialized alongside model state, and we deliberately drop the presentation part on import.
- **Where it appears:** `docs/falstad-format.md` §2 (header field list); §7 (importer contract: display fields ignored).

### custom logic model (`!`)
- A line type in Falstad/circuitjs1 saved-circuit files that defines a user-authored digital logic gate; Manatee's importer refuses these files with a loud, specific error.

Falstad circuit files are line-oriented text: most lines describe circuit elements, but a few line types carry metadata instead, dispatched before element parsing (`CircuitLoader.java:149-193` upstream). A line starting with `!` declares a *custom logic model* — a truth-table-style definition for a homemade digital gate that later element lines can reference. Manatee simulates analog electrical behavior only; digital logic is out of scope, so `!` lines land in the importer's "reject loudly" bucket: the import fails with the line number, the offending token, and a one-line reason, rather than silently dropping part of the circuit. Think of it as a file-format feature we deliberately don't support, like a document reader refusing an embedded macro instead of pretending it ran.

The term is Falstad's own vocabulary; there is no independent EE meaning — it is a save-format construct, roughly analogous to a behavioral/truth-table digital primitive in mixed-signal simulators.
- **Where it appears:** `docs/falstad-format.md` §1 (non-element line types) and §7 (accept/reject contract).

### DC-link capacitor
- A real, honestly-sized capacitor built into Manatee's DC converter two-ports, giving them energy storage and smoothing the same way real power converters do.

When two islands are joined by a converter (e.g. Stationeers' DC-to-DC couplers), each side needs a little energy buffering so that a momentary mismatch between what one side supplies and the other draws doesn't destabilize the coupled solve. AC transformers get some of this from their own leakage/magnetizing storage, but Manatee doesn't rely on those parasitics for stability (their time constants are shorter than a substep); DC converter two-ports instead get storage *by construction*: an explicit capacitor across the converter's internal DC bus. This mirrors real hardware — every real converter has a DC-link cap doing exactly this smoothing job — so the model stays honest rather than adding a fudge factor. In systems terms, it's a small buffer between a producer and a consumer running on separate schedules: it absorbs per-step jitter so the two loops don't have to be lockstep-perfect. In the as-built API, the converter's B port *is* the DC-link capacitor, and its voltage is save/loaded as `CouplerRuntime` state.

**Standard term:** yes — "DC-link capacitor" is standard power-electronics vocabulary (also "DC bus capacitor").
- **Where it appears:** `docs/solver.md` (Islands / boundary-coupling exchange scheme); `docs/design.md` doc-review round and Open Questions; `docs/api.md` §7-ish converter two-port and `CouplerRuntime`.
- **References:** Horowitz & Hill, *The Art of Electronics* (power supply smoothing/filter capacitors); Erickson & Maksimović, *Fundamentals of Power Electronics*.

### DC operating point
- The steady-state solve of a circuit: the set of node voltages and branch currents the circuit settles into when nothing is changing over time.

In Manatee, calling `Step(dt)` with `dt <= 0` requests a DC operating point instead of a time step. Time-dependent behavior is switched off by construction: capacitors are treated as open circuits (no current flows once they're charged) and inductors as near-shorts (~1 mΩ, per the conductance-range policy in solver.md's Numerics section — real solvers avoid exact zeros/infinities the way careful code avoids division by zero). For a programmer: it's like asking a state machine for its fixed point — the state that maps to itself — rather than running it one tick. The Stationeers/Re-Volt client runs exclusively in this mode, layering battery/storage dynamics on top at dt = 0.5 s; the transient and subcycled-AC analyses share the same netlist (requirement R2).

**Standard term:** yes — this is SPICE's `.op` analysis (also "quiescent point" or "bias point" in EE usage).
- **Where it appears:** `docs/design.md` R2 and Simulation Model; `docs/solver.md` Analyses; `docs/integration-tutorial.md` §2 (`Profile = Dc`); `Step(double dt)` in the API.
- **References:** Nagel (1975), *SPICE2: A Computer Program to Simulate Semiconductor Circuits*, UCB/ERL-M520; Vlach & Singhal, *Computer Methods for Circuit Analysis and Design*; the ngspice manual (`.op` analysis).

### deck / SPICE deck / deck emission
A plain-text circuit description in the input format of SPICE-family simulators; in this project, a file our test suite generates so ngspice can simulate the same circuit our solver just did.

A SPICE deck lists a circuit line by line ("R1 node1 node2 1k", a voltage source, an analysis command like `.op` or `.tran`) — the name dates from when each line was literally a punched card in a card deck. Manatee never reads or ships decks; a **test-only translator** converts our in-memory netlists into decks, ngspice runs them, and the resulting node voltages and branch currents are diffed against our solver's answers within tolerance (DC 0.1% relative). This is the project's primary correctness mechanism — the *oracle tests* — because the EE math is treated as untrusted input that must be checked against an independent reference implementation. "Deck emission" (the translator's output) is itself snapshot-tested with the Verify library, so an unintended change to the generated text fails CI — the programmer's analogy for an EE: like keeping a signed reference printout of a test procedure so any silent edit to it is caught before it corrupts the measurements it drives.

**Standard term:** SPICE netlist / SPICE deck — fully standard vocabulary; "netlist" and "deck" are used interchangeably in the SPICE world.

**Where it appears:** `docs/testing-strategy.md` (Toolchain, Oracle Tests).

**References:** Nagel (1975), *SPICE2: A Computer Program to Simulate Semiconductor Circuits*, UCB/ERL-M520; the ngspice manual (netlist syntax, `.op`/`.tran` analyses).

### diode
- A two-terminal component that conducts current easily in one direction and almost not at all in the other — the electrical one-way valve.

Unlike a resistor, a diode's current is an *exponential* function of the voltage across it, so a circuit containing one cannot be solved in a single linear step: the solver must guess, linearize around the guess, solve, and repeat (Newton–Raphson iteration) until the answer stops changing — each iteration is a tier-2 re-stamp in our cost model. For programmers: it is like solving a system where one equation's coefficients depend on the answer, forcing an iterate-until-fixed-point loop instead of one matrix solve. In Manatee, the diode is a solver primitive (design.md R1 lists "diodes-as-needed"); design.md's nonlinearity-budget note names diodes in the tablet curriculum as the reason Newton iteration exists at all, while deliberately keeping diodes out of the per-substep hot path in world AC islands, where behavioral two-port models serve instead.
- **Where it appears:** `docs/design.md` R1 and the nonlinearity-budget note, `docs/solver.md` (Component Set, Nonlinear elements); API surface `AddDiode(anode, cathode, DiodeParams)`.
- **References:** Horowitz & Hill, *The Art of Electronics* (diode behavior); Nagel (1975), *SPICE2: A Computer Program to Simulate Semiconductor Circuits*, UCB/ERL-M520 (the standard simulation treatment).

### diode (`d`)
- The element line code for a diode in the Falstad/circuitjs1 text format that Manatee's importer reads.

After the shared positional fields (coordinates and flags), a `d` line carries one of three tails, selected by flag bits: if `flags&2` (FLAG_MODEL) is set, an *escaped model name* referencing a named model — either a built-in (`default`, `default-zener`) or one defined earlier in the file by a `34` line; else if `flags&1` (FLAG_FWDROP) is set, a single forward-drop value in volts; else nothing, meaning the default drop of 0.805904783 V (`DiodeElm.java:48-68`). "Escaped" means the name uses circuitjs's text escaping (space→`\s`, etc.) so it survives the whitespace-tokenized format. Manatee's importer accepts all three forms, maps a `34`-defined model's Is/Rs/N/BV onto our `DiodeParams`, and rejects any other named model loudly — a parser dispatch table where flag bits act as a discriminated union tag on the trailing parameters.
- **Where it appears:** `docs/falstad-format.md` §4 element table and the importer accept/reject contract (§ "accept"); consumed by the tablet's Falstad importer.
- **References:** circuitjs1 source (`DiodeElm.java`, `DiodeModel.java`), the format's de facto specification.

### diode limiting
- A standard SPICE technique that caps how far a diode's voltage may move in one Newton iteration, so the exponential diode equation never blows up mid-solve.

A diode's current grows exponentially with voltage, so a Newton step that overshoots by even a volt or two can produce astronomically large intermediate currents — numerical overflow, or wild oscillation that never converges. The fix is to clamp each iteration's proposed junction-voltage change to a safe region before re-linearizing (SPICE's `pnjlim` routine is the classic implementation). For programmers, this is exactly step-size limiting in an iterative solver — a trust region or rate limiter on the update, trading raw step size for guaranteed progress. Manatee applies it "per SPICE practice" as part of its Newton-Raphson loop for nonlinear elements, alongside the dual convergence test and iteration cap.
- Common aliases: junction-voltage limiting, `pnjlim` (after the SPICE routine).
- **Where it appears:** `docs/solver.md` (Analyses, "Nonlinear elements").
- **References:** Nagel (1975), *SPICE2: A Computer Program to Simulate Semiconductor Circuits*, UCB/ERL-M520; Vlach & Singhal, *Computer Methods for Circuit Analysis and Design* (Newton convergence aids); the ngspice manual (device convergence options).

### diode model definition (`34`)
- A non-element line in the Falstad/circuitjs1 text format that defines a named, reusable set of diode parameters, which later `d` (diode) and `z` (zener) lines reference by name.

The line reads `34 escapedName flags Is Rs N BV forwardCurrent` (`DiodeModel.java:336-339`): a name (escaped so it survives whitespace tokenizing), then the physical parameters — saturation current Is, series resistance Rs, emission coefficient N, and breakdown voltage BV. It works like a named constant or type declaration in a program: define once at the top of the file, then any diode line with FLAG_MODEL set (`flags&2`) points at it by name instead of repeating the numbers — the direct analogue of a SPICE `.MODEL` card. Manatee's importer accepts `34` lines and maps Is/Rs/N/BV onto our diode parameters; a `d`/`z` line naming a model that was neither built in (`default`, `default-zener`) nor defined by a preceding `34` line is rejected loudly.
- **Standard term:** the closest standard concept is a SPICE `.MODEL D(...)` statement; `34` is circuitjs1's line-type number for it.
- **Where it appears:** `docs/falstad-format.md` §4 table (`34` row, `d`/`z` rows) and the importer accept list; §2 non-element line dispatch.
- **References:** circuitjs1 source (`DiodeModel.java`); the ngspice manual, diode model (`.MODEL` type D) for the parameter meanings.

### DiodeParams / diode-as-needed
- `DiodeParams` is the small parameter record that configures a diode — the one genuinely nonlinear component in manatee-core — and "diodes-as-needed" is the design stance that this nonlinearity exists only because the tablet curriculum teaches it, not because game circuits require it.
- A diode is a one-way valve for current: it conducts easily in one direction and almost not at all in the other, following the SPICE-conventional exponential law `I = Is·(exp(V/(n·Vt)) − 1)` with the thermal voltage `Vt` fixed at 300 K (the model is isothermal — no self-heating). Because that law is not a straight line, the solver cannot stamp it once and be done; each solve runs Newton iteration, re-linearizing the diode around the current guess (a per-iteration companion conductance + current source, with junction-voltage limiting to keep the exponential from blowing up). For programmers: think of Newton iteration as a fixed-point loop over successively better linear approximations, converging on the self-consistent answer. `DiodeParams(SaturationCurrent, Emission, SeriesResistance)` carries `Is`, the ideality factor `n`, and a series resistance `Rs`; `DiodeParams.Default` is a silicon-ish `(1e-14 A, 1, 0)`. **Caveat:** `Rs` is pinned in the struct but *not yet stamped* (it would need a per-diode internal node); the default 0 is exact, a nonzero value is currently ignored — a documented limitation. Parameters are changed live via the tier-2 verb `Adjust(DiodeId, in DiodeParams)`.
- The names `Is` (saturation current), `n` (emission coefficient / ideality factor), and `Rs` are the standard SPICE diode model parameters; `DiodeParams` is just our typed subset of them. "Diode-as-needed" is project phrasing from requirement R1.
- **Where it appears:** `docs/design.md` R1 ("diodes-as-needed"); `docs/api.md` §3–4 (`AddDiode`, `Adjust(DiodeId, …)`), §23.4 (parameter-struct pinning); `docs/solver.md` (Newton/diode limiting); struct definition in `core/Manatee.Core/Primitives.cs:36`.
- **References:** Nagel (1975), *SPICE2: A Computer Program to Simulate Semiconductor Circuits*, UCB/ERL-M520 (the diode model and junction-voltage limiting); Vlach & Singhal, *Computer Methods for Circuit Analysis and Design* (Newton–Raphson for nonlinear networks); Horowitz & Hill, *The Art of Electronics* (practical diode behavior).

### dissipation (power dissipation)
- The power a device converts irreversibly into heat, measured in watts.

Whenever current flows through anything with resistance, electrical energy is lost as heat at a rate P = I²R (equivalently V·I across the element); it cannot be recovered, unlike energy *stored* in a capacitor or inductor, which can flow back out. For programmers: dissipation is the "write-off" column in an energy ledger — every joule entering a circuit is either still stored, delivered as useful work, or dissipated, and Manatee's `EnergyAudit` (SourceJ, DissipatedJ, StoredJ, ...) tracks exactly this split so conservation is checkable at runtime. In the games this is not just bookkeeping: dissipated power is dumped as heat where the loss physically lives, so an undersized cable or overloaded device genuinely gets hot. This is the standard EE term; "ohmic loss", "Joule heating", and "I²R loss" are common aliases for the resistive case.
- **Where it appears:** `docs/design.md` (energy accounting rule, device notes), `docs/api.md` (`EnergyAudit`, per-tick energy residual, dissipativity gates).
- **References:** Horowitz & Hill, "The Art of Electronics", 3rd ed. (Cambridge University Press, 2015), ch. 1 on power in resistive circuits.

### efficiency curve

A small table describing how efficient a power converter is at each load level, used to compute how much input power it must draw — and how much heat it must shed — for a given output.

Real converters (transformers, chargers, DC-DC supplies) waste a fraction of the power passing through them, and that fraction varies with load: typically poor at very light load, best somewhere in the middle. In manatee-core the `EfficiencyCurve` is 1–4 `(loadFraction, efficiency)` breakpoints — effectively a tiny piecewise-linear lookup table, the same structure as any interpolated config table a programmer would write. The behavioral `ConverterTwoPort` coupler uses it directly: measured output power P_out at load fraction P_out/ratedWatts yields η from the curve, the input side draws P_in = P_out/η, and the loss P_out·(1/η−1) is computed *from the curve* (deliberately not synthesized as In−Out, so accounting slop can't masquerade as physics) and dumped as `HeatDumpedJ`. Under the design's energy-accounting rule, that loss is explicit heat where it physically lives — which matters in Stationeers, where dumped heat enters a real thermal simulation. For programmers: the invariant is conservation — every joule in is either delivered or appears on the heat ledger, never silently dropped.

Standard EE concept; datasheets publish the same thing as an efficiency-vs-load curve (η vs. percent of rated load).

**Where it appears:** `EfficiencyCurve` and `CouplerSpec.ConverterTwoPort` in docs/api.md §7 (and the pinned-struct list, §23.4); docs/solver.md component set (converter "transfer with efficiency curve between two islands"); docs/design.md energy-accounting rule.

**References:** Horowitz & Hill, *The Art of Electronics* (3rd ed.), on power-conversion efficiency; Erickson & Maksimović, *Fundamentals of Power Electronics*, for loss modeling of converters.

### electrical frequency

The rate, in cycles per second (hertz), at which an alternator's AC output voltage swings back and forth — set by how fast the shaft spins times how many magnetic pole pairs the machine has.

This is the standard relationship for a synchronous machine: electrical frequency = mechanical rotation rate × pole pairs, because each pole pair passing a coil produces one full AC cycle. In the Vintage Story integration the mechanical engine integrates shaft angle such that ω = 5·speed rad/s (verified at `MechanicalNetwork.cs:156-158,185-189`), so f ≈ (5/2π)·speed·polePairs ≈ **0.8 × shaft speed × pole pairs Hz** — a derived constant, explicitly "no fudge factor." The gameplay consequence is deliberate: ordinary waterwheel/windmill alternators at 2–4 pole pairs give the ~5 Hz "natural" frequency (with visible lamp flicker), and since the mechanical network overheats past speed 4.5, players cannot reach 50 Hz by spinning faster — the honest path is a high-pole-count machine (~12–20 pairs → ~40–60 Hz), the same shape as real low-speed hydro alternators. For programmers: pole-pair count is a hardware multiplier on a fixed clock — you can't overclock the shaft, so you widen the datapath instead; the reward is real because transformer iron size scales inversely with frequency.

Standard term; also called *line frequency* or *synchronous frequency* in the grid context (f = P·N/120 with P poles and N rpm is the textbook form).

**Where it appears:** docs/vintage-story.md §2 (frequency arithmetic, with engine file:line evidence); docs/design.md (voltage and frequency standards; 5 Hz natural, 40–60 Hz via pole count).

**References:** Fitzgerald, Kingsley & Umans, *Electric Machinery*, on synchronous machine frequency and pole count; Horowitz & Hill, *The Art of Electronics*, on AC line power basics.

### electrode
A grounding rod: a device that connects a circuit to the earth node through a contact resistance of tens of ohms, better (lower) when the surrounding soil is wet.

In Manatee, earth is an ordinary solver node, and an electrode is the only legitimate way to reach it: an ordinary resistor whose value models the rod-to-soil contact. That resistance is a real, teachable gameplay parameter — grounding-rod quality and wetness matter, and "bare conductors ground when wet" falls out of the same mechanism. Electrodes carry their own limit envelope (R7): sustained overload dries and then glassifies the surrounding soil, *raising* the contact resistance — honest negative feedback that caps how much current one electrode can dump into the earth, so "electrode farms" cost real material and land. Deliberately wetting the rod (a pool over it) is legal and rewarded, as in real practice. For the programmer: the electrode is a stateful edge in the graph whose weight degrades under abuse — a resource with a rate limit and a damage model. Tooltips attribute the contact-resistance voltage drop and wetness (R15).

**Standard term:** grounding electrode / earth electrode (ground rod); the resistance is *earth-electrode resistance*. The overload behavior mirrors real soil drying/vitrification around overloaded electrodes.

**Where it appears:** `docs/design.md` (Grounding model; R7 limit envelopes; R15 instruments), `docs/api.md` §on grounding, `docs/curriculum.md` Lesson 14.

**References:** IEEE Std 142 ("Green Book"), *Grounding of Industrial and Commercial Power Systems*, covers earth-electrode resistance; Horowitz & Hill, *The Art of Electronics*, treats grounding practice.

### electrode-loss arithmetic
The simple predict-then-observe calculation of how much voltage is lost across the tens-of-ohms grounding electrodes at each end of a single-wire earth-return (SWER) link — the calculation that decides when using the earth as your return conductor is viable.

With earth return, the load current flows through both electrodes' contact resistances, so the voltage lost is I × (R_electrode1 + R_electrode2). At 12 V with tens-of-ohms electrodes, any real load's current makes that loss eat essentially the whole supply — SWER fails, and this failed arithmetic is what forced the project's grounding-model revision (two-wire is the default idiom). Raising the voltage helps because the fractional loss for a given power scales as P·R/V²: real-world SWER runs at medium voltage (typically ~12.7 kV or 19.1 kV line-to-earth), where electrode loss is genuinely negligible; the game's 240 V distribution tier is the game-scale stand-in — much better than 12 V, though still material for full power loads. At milliamp signalling currents (telegraph, doorbell, electric fence) the loss is negligible outright — historically accurate SWER territory. Curriculum Lesson 14 uses this as its predict-then-observe core, reframing the question as "copper is expensive — when can you get away with one wire?" For the programmer: it is a back-of-envelope cost model — two fixed per-hop costs in series with your payload — that tells you which operating points amortize the overhead. For the EE: it is ordinary voltage-divider/line-loss arithmetic applied to earth-electrode resistance.

**Project-coined term**, closest standard concept: earth-electrode (grounding) resistance loss in SWER system design.

**Where it appears:** `docs/curriculum.md` Lesson Arc III, Lesson 14; `docs/design.md` Grounding model (revised 2026-07-05 after the 12 V arithmetic failure).

### EMF (electromotive force)
- Electromotive force: a source's internal driving voltage — the "push" behind the current — as distinct from the voltage you actually measure at its terminals under load, which sags as current through the source's own internal resistance eats part of the EMF.

EMF is the ideal-voltage-source half of every behavioral source model in the solver's component set: batteries are EMF + internal series resistance + a state-of-charge integrator, and machines like the alternator compute their EMF each tick from game state. In the Vintage Story alternator EMF is not a fixed constant — it is derived from mechanical state: shaft speed (read from the engine's mechanical network as `TrueSpeed`) sets the source amplitude (EMF), while shaft speed times pole pairs sets the electrical frequency. Because the mechanical simulation updates at ~100 ms granularity while the electrical solver ticks faster, shaft speed enters the electrical side as a piecewise-constant parameter per tick. The distinction between EMF and terminal voltage matters because terminal voltage sags under load — which is exactly why the voltaic pile in Lesson 6 droops. For programmers: EMF is the source's *declared* output value (a config value the device declares), and the observed terminal voltage is the runtime measurement after the system pushes back — like a server's advertised capacity versus throughput under contention; the gap is proportional to load. "EMF" is standard; "source voltage" and "open-circuit voltage" (the terminal voltage at zero current, which equals the EMF) are common near-synonyms.
- **Where it appears:** `docs/vintage-story.md` §2 (Alternator, mechanical co-simulation cadence); `docs/solver.md` (Component Set: Batteries, Machines); `docs/api.md` §18 (built-in device models).
- **References:** Horowitz & Hill, *The Art of Electronics* (Thévenin sources); Vlach & Singhal, *Computer Methods for Circuit Analysis and Design* (source models in nodal analysis); any electric-machines text for speed-proportional generated EMF.

### EMF / Rint / SoC
- The three internals of Manatee's behavioral battery model: an electromotive-force voltage source (EMF), an internal series resistance (Rint), and a state-of-charge integrator (SoC), parameterized per chemistry (voltaic pile, lead-acid, li-ion).

The battery is not simulated electrochemically; it is composed from two circuit primitives plus one piece of bookkeeping. EMF is the driving voltage, Rint is the resistance in series with it (this is what makes weak batteries sag under load), and SoC is an accumulator that integrates charge in and out over time — for programmers, a running counter updated each tick, like a metered quota; for the EE, a coulomb counter. Chemistry differences change the *parameters* (EMF curve, Rint, capacity), never the *structure* — the planned chemistry arc deepens parameters only. In the API, the `Battery` device mints its component keys at stable ordinals (0 = EMF source, 1 = Rint, ...) so handles and saved state survive rebuilds deterministically.
- **Standard term:** this composite is the standard "Rint model" (internal-resistance equivalent-circuit model) with coulomb-counting state-of-charge estimation; EMF, internal resistance, and SoC are all standard vocabulary. (Note: the battery literature's "Thévenin model" is a distinct, more detailed model that adds one or more parallel RC pairs for transient/relaxation dynamics — Manatee does not use it.)
- **Where it appears:** `docs/solver.md` (Component Set: Batteries); `docs/api.md` §18 (built-in `Battery` model and its key-allocation ordinals).
- **References:** Horowitz & Hill, *The Art of Electronics* (real battery behavior, internal resistance).

### energy sloshing
Informal name, used in the tablet curriculum, for energy moving back and forth between storage elements instead of being consumed — the ring-down of an RLC circuit, and the non-working current of poor power factor.

In lesson I.5 (RLC ring-down), energy alternates between the capacitor's electric field and the inductor's magnetic field, gradually damped away by resistance — like a pendulum trading height for speed while friction slowly wins. In lesson III.16 (power factor), the same idea appears on AC mains: reactive loads exchange energy with the source every cycle, and that back-and-forth current heats the cables but performs no net work. For a programmer, think of a buffer that is repeatedly filled and drained without ever being consumed downstream: the copying costs real bandwidth (cable heating, I²R) even though no useful throughput results. The phrasing deliberately avoids EE jargon (no "phasor", no "VAR") per the curriculum's authoring rules; it also matches Electrical Age's original lesson wording.

**Standard term:** reactive energy exchange — LC ring-down / damped natural response (lesson I.5), and reactive power / power factor (lesson III.16).

**Where it appears:** `docs/curriculum.md` (Lesson Arc I.5 and III.16); `docs/experiments/2026-07-05-ea-examples.md` (the phrasing's EA lineage).

**References:** Horowitz & Hill, *The Art of Electronics* (LC circuits, reactive power).

### equipotential
- Conductor geometry that is all at the same voltage, and can therefore be represented by a single node in the solver's netlist.

In a circuit, a perfect (zero-resistance) conductor has no voltage difference anywhere along it — every point on it is "equi-potential." That means the solver does not need one variable per voxel or cable segment; the whole connected blob of conductor collapses into one node. In Manatee this is Responsibility #1 of the compaction layer ("region building"): a union-find data structure merges touching perfect-conductor geometry into single regions, while resistive materials get their own regions joined by resistances derived from resistivity and contact cross-section. For programmers: this is deduplication by an equivalence relation — "connected by zero resistance" partitions the geometry, and each partition becomes one solver variable. For the EE: it is the ordinary node-identification step of netlist extraction, applied to voxel/segment geometry instead of a schematic.

This is the standard physics/EE term (an "equipotential surface/region"); no project-specific twist.

**Where it appears:** `docs/compaction.md`, Responsibilities #1 (region building via union-find).

**References:** Horowitz & Hill, *The Art of Electronics* (nodes and ideal wires); Halliday, Resnick & Walker, *Fundamentals of Physics* (equipotential surfaces).

### Equivalent resistor
- The single resistor that a collapsed series chain of conductor segments is replaced by in the solver's netlist.

When the reduction layer collapses a run of series segments (a copper cable run, a chain of voxels) into one element, that element is an equivalent resistor: its resistance is the sum of the segments' resistances, so the solver computes exactly the same currents while working on a far smaller matrix (~10k segments become low hundreds of nodes). For a programmer, this is lossless compression with a decompression map kept on the side: the layer retains enough per-segment data to interpolate voltages at eliminated interior points (probe interpolation) and, when the solver raises a limit event on the equivalent resistor, to attribute the event back to the specific constituent segment that actually melts, burns, or pops — current density at the narrowest cross-section, evaluated per limit type. The equivalent resistor also carries a limit envelope: the per-limit-type minimum over its constituents (with i²t kept as a Pareto-minimal set of pairs, not a single hybrid).

This is the standard EE notion of series equivalent resistance (R_eq = ΣRᵢ); our twist is that the equivalent element remains the addressable unit for limit events, with attribution deferred to the reduction layer.

**Where it appears:** `docs/compaction.md` (Responsibilities #2 series-chain collapse, #3 probe interpolation, #4 limit attribution); as-built contract in `docs/api.md` §19.

**References:** Any circuits text covers series equivalence, e.g. Horowitz & Hill, *The Art of Electronics*; the reduction technique is a special case of network reduction discussed in Pillage, Rohrer & Visweswariah, *Electronic Circuit and System Simulation Methods*.

### Faraday's laws (electrolysis/electroplating)
The physical laws stating that the amount of material deposited or dissolved in an electrochemical cell is proportional to the total electric charge passed through it; in Manatee they are the economic rule behind Arc 3's electrolysis and electroplating gameplay.

Faraday's first law of electrolysis says mass deposited is proportional to charge (current integrated over time); the second says the proportionality constant depends on the substance (its molar mass divided by valence, via the Faraday constant, ~96,485 C/mol). For the game this means electro-industry output is priced directly in amp-hours: run twice the current for the same time, or the same current twice as long, and you plate twice the metal — no shortcuts, and losses elsewhere in the circuit are wasted product. Programming analogy: the cell is an integrator whose accumulated state variable is charge, and yield is a pure linear function of that accumulator — exactly like metering a resource by bytes transferred rather than connection time. design.md places this in Arc 3 (heavy industry, deliberately deferred, possibly pulled forward into Arc 2 if a light use case appears), consistent with the project's premise that real electrical law *is* the game economy.

This is a standard physics term used in its standard sense (Michael Faraday, 1834).

**Where it appears:** `docs/design.md`, progression arcs (Arc 3 — heavy industry).

**References:** any general physics or physical-chemistry text covers Faraday's laws of electrolysis, e.g. Atkins & de Paula, *Physical Chemistry*; original: M. Faraday, "Experimental Researches in Electricity — Seventh Series", *Phil. Trans. R. Soc.* (1834).

### fault
- One of the scripted-scenario types in the harness test corpus: a deliberately broken or degenerate circuit, played through end to end to prove the error path works as well as the happy path.

The harness's lesson-corpus integration tests run scripted scenarios headless in CI — the doc lists them as "build, edit, save/load, fault". A *fault* scenario constructs a circuit the solver is designed to refuse (e.g. contradictory sources or a singular configuration), then checks the whole pipeline: netlist extraction, the solve failing into the `Faulted` island status, the diagnostic surfacing, and probe readback showing the de-energized values. In testing terms it is the negative-path fixture: like a unit test that feeds a parser malformed input and asserts on the error message rather than the parse tree. For an EE: it is the simulated equivalent of wiring a known-bad bench setup to verify the protection and indication circuits, not the load.

In standard software-testing vocabulary this is **negative testing / error-path testing**; note that in EE usage "fault" usually means a physical failure (short circuit, ground fault) — here it names the *test scenario* that provokes the solver's failure handling.

**Where it appears:** `docs/harness.md` (Testing Model, lesson-corpus integration list), `docs/testing-strategy.md` (lesson corpus as CI goldens).

### Floating (subgraph / node / system)
- A node, or a connected piece of a circuit, that has no conductive path to the island's reference (ground) node, so its absolute voltage is mathematically undefined until something pins it down.

MNA solves for node voltages *relative to* a reference; a floating subgraph is like a value with no defined origin — only voltage *differences* within it are determined, so the equations admit infinitely many solutions differing by a constant offset, which makes the matrix singular (the linear-algebra equivalent of a divide-by-zero, or an uninitialized variable that turns downstream computation into NaN). A single node connected only through a capacitor — or through nothing — is the degenerate one-node case. Programmers can read it as an unanchored coordinate frame or a dangling subtree with no path to the root. The solver handles it numerically rather than rejecting it: each island anchors its own ground (an explicit ground node if present, else an arbitrary node) and stamps tiny gmin shunts (1e-12 S) on non-ground diagonals so floating subgraphs stay non-singular — the SPICE-standard trick — and in Vintage Story the `TwoWireLeak` wiring policy additionally auto-stamps an implicit ~1 MΩ leak from every device's Return-role node to the island reference (the literal earth), so the matrix stays well-conditioned without per-device grounding boilerplate. Floating can be deliberate: under the tablet's `ExplicitOnly` wiring policy, floating-by-default lessons stay floating (gmin only, no implicit leak), because isolation is the thing being taught. Requirement R9 ("legible failure") additionally requires that genuinely degenerate cases surface as diagnosable errors or events, never NaN — players build pathological circuits as a hobby — so debug builds warn when a subgraph has no leak, no reference, and no source path to one (the *accidental* case, typically a mistagged Return terminal). A known sharp edge (api.md §23.8a): floating *nonlinear* islands held up only by gmin can converge to spurious operating points.

Separately, "floating" is also a *gameplay* concept — an intentionally earth-isolated system with no ground reference where touching one wire is safe — covered under **Floating system**.

This is standard SPICE-world vocabulary; classic simulators often reject netlists with floating nodes at parse time ("no DC path to ground"), whereas we make them solvable and warn. The numerical fix is SPICE's GMIN.
- **Where it appears:** `docs/api.md` §5 (WiringPolicy, `ExplicitOnly`, `TwoWireLeak(1e6)`, the floating-subgraph debug warning), §23.8a (floating nonlinear islands); `docs/solver.md` (Islands — per-island ground anchoring and gmin shunts; Failure Handling); `docs/design.md` R9 and the Grounding model.
- **References:** Ho, Ruehli & Brennan (1975), "The Modified Nodal Approach to Network Analysis", IEEE Trans. Circuits and Systems (why an undetermined reference makes the system singular); Nagel (1975), SPICE2 memo UCB/ERL-M520 (GMIN); the ngspice manual (gmin and floating-node handling); Vlach & Singhal, *Computer Methods for Circuit Analysis and Design*.

### Floating system
- A whole electrical installation with no connection to earth — typically fed through an isolation transformer — so no single point of it has a defined voltage relative to the ground you're standing on.

The safety consequence is the reason it matters in our curriculum: touching *one* wire of a floating system cannot shock you, because a shock requires a complete circuit and there is no return path through the earth. But that guarantee is conditional, and the docs state it carefully: one wire of a **verified** floating system is safe to touch. Verification is a taught ritual — a wire-to-earth multimeter check, plus an isolation-monitor device (a lamp pair to earth, actual historical practice) — because a rain-wetted bare cable span can silently re-ground the system, invalidating the assumption without any visible change. For programmers: it is an invariant ("no earth path exists") that the environment can break behind your back, so it must be checked at time-of-use, never cached — a classic TOCTOU hazard, in copper.

**Standard term:** floating / ungrounded system; in IEC wiring-code language, an **IT system** (isolé-terre). Real installations pair it with an insulation monitoring device, exactly as our advanced build does.

**Where it appears:** `docs/design.md` Grounding model (deliberately-floating systems buildable by default, isolation monitor, rain re-grounding); `docs/curriculum.md` Appendices (the verified-floating ritual in the hazard reference).

**References:** Horowitz & Hill, *The Art of Electronics* (grounding and safety); IEC 60364 earthing-system classification (TN/TT/IT).

### forward drop / fwdrop
- The voltage a diode "eats" when conducting in its forward direction — and the name of the optional parameter (`fwdrop`) that carries that value in a Falstad-format diode line.

A real diode is a one-way valve for current, but not a free one: while conducting it holds roughly a fixed voltage across itself (about 0.6–0.8 V for silicon), called the forward voltage drop. In the Falstad text format, a diode element (`d`) stores this as an optional field, with the flags checked in a fixed order: if bit 1 of the element's flags is set (`FLAG_MODEL`, `flags&2`), a named model supplies the parameters; else if bit 0 is set (`FLAG_FWDROP`, `flags&1`), the line carries an explicit `fwdrop` value in volts; with neither flag, the format's baked-in default of 0.805904783 V applies. For programmers: think of it as an optional field in a serialization format, with a flag bit acting as the presence marker and a hard-coded default when absent — our importer accepts the no-flag default, the explicit `fwdrop` value, and a small whitelist of model names.

**Standard term:** forward voltage drop, often written V_F or V_D in datasheets; "fwdrop" is Falstad/circuitjs1's field name for it.

**Where it appears:** `docs/falstad-format.md` §4 (the `d` element row, citing `DiodeElm.java:48-73`) and the importer accept/reject contract in the same doc.

**References:** Horowitz & Hill, *The Art of Electronics* (diode forward-drop behavior); the circuitjs1 source (`DiodeElm.java`) for the format's field semantics.

### fuse
- A deliberately weakest link in a circuit: a conductor sized so that it melts and opens the circuit first when too much current flows, sacrificing itself to protect everything downstream.

A fuse works because current heats a conductor in proportion to I²·R; a thin or low-melting-point segment reaches its melting threshold before the cables it protects do. Choosing *where* the weak link sits is the design act — Lesson 7 of the tablet curriculum is literally titled "Switches, fuses by design (the weakest link is a choice)." Manatee's distinctive move is that fuses are **emergent, not special-cased**: a lead segment spliced into a copper run *is* a fuse, because the compaction layer's limit envelope takes the per-limit-type minimum over a chain's constituents, and lead's lower ampacity and melting point win. When the solver raises an over-current limit event (R7), limit attribution names the exact segment, and the client removes it — the melt is a topology edit performed by game code, never a mutation the solver does on its own. In Stationeers, Re-Volt models a fuse as a cable edge with a rating, not a device. For programmers: a fuse is a circuit breaker in the software-resilience sense, except one-shot — it trips by destroying itself, and "resetting" means crafting a new one.

Standard term; standard in EE. Related: circuit breaker (resettable), i²t rating (slow-overload characteristic).

**Where it appears:** `docs/curriculum.md` Lesson Arc II.7; `docs/design.md` R7 (limit events with attribution), R12; `docs/compaction.md` Responsibilities #1 and #4; `docs/stationeers.md` (fuses as rated edges in the adjacency).

**References:** Horowitz & Hill, *The Art of Electronics*, 3rd ed. (fusing and overcurrent protection).

### fuse / ampacity
- Ampacity is the maximum continuous current a conductor can carry without damage; a fuse is a segment whose ampacity is deliberately the lowest in the run, so it fails first.

In Manatee these are two views of one mechanism. Every conductor segment carries a limit envelope (instantaneous ampacity, i²t thermal mass, melting threshold); in Vintage Story, R12 ties ampacity to physical cross-sectional area and material, so wire gauge is real physics rather than a stat. When current through a compacted run exceeds the envelope's weakest constituent, the solver emits an over-current limit event; the client calls `ConductorGraph.Attribute` to find which segment is responsible and `RemoveSegment` to melt it — the integration tutorial's worked example drives ~12 A through a 6 A fuse segment (a `LimitSpec(6.0, …)` on one segment) and watches exactly that segment pop. Because attribution is per-limit-type minimum over the chain, a lead segment in a copper run *is* a fuse with zero special-casing. For programmers: ampacity is a per-edge capacity constraint, like a rate limit on a queue; the fuse is the edge you intentionally gave the lowest limit so violations fail at a known, cheap-to-replace point.

Standard terms; "ampacity" is the standard electrical-code word (NEC/IEC usage) for current-carrying capacity, also called "current rating."

**Where it appears:** `docs/integration-tutorial.md` §3 (the 6 A fuse `LimitSpec`) and §8 (limit-event drain, `Attribute`, `RemoveSegment`); `docs/design.md` R12; `docs/compaction.md` limit attribution; `docs/api.md` §19 (i²t envelope).

**References:** Horowitz & Hill, *The Art of Electronics*, 3rd ed. (conductor sizing and protection).

### gain-capable loop
- A closed ring of coupled islands whose round-trip voltage gain is ≥ 1 — for example two transformer couplers in a loop where one steps voltage up — so any numerical error or non-conservative bookkeeping in the coupling exchange can amplify itself each cycle instead of dying out.

Manatee couples separately-solved islands through boundary couplers (transformers, converters) that exchange energy once per substep. In a loop with gain ≥ 1, an exchange that isn't strictly conservative becomes a positive-feedback amplifier: the phase-5 review demonstrated a gain-capable two-coupler loop growing a seeded 8.6 J to 85 J, and pre-clamp measurements at relaxation α ≥ 0.8 showed transients of 1e15–1e73 J. This makes gain-capable loops the designated stress case for two independent gates: the **conservation audit** (windowed physical energy accounting from public readbacks — the dissipativity gate deliberately runs at gain-capable turns ratios, where a leak "demonstrably rings up") and the **numerical stability grid**, which exhaustively sweeps α × resistance × turns ratio and is why effective transformer damping is clamped to min(α, 0.7). For programmers: it is a feedback loop with amplification ≥ 1 — like a retry storm where each failure spawns more than one retry — so correctness must be proven at the loop level, not per component; for EEs, it is the classical instability condition of loop gain reaching unity.

**Project-coined term**, closest standard concept: a feedback loop with loop gain ≥ 1 (the Barkhausen/ Nyquist instability condition applied to the inter-island coupling iteration); the numerical side is convergence of a damped fixed-point iteration, which requires the iteration map to be a contraction.

**Where it appears:** `docs/api.md` §7 (energy ledger, debt droop, the α ≤ 0.7 clamp and the stability grid); `docs/integration-tutorial.md` sharp edge #9; enforced by `ConservationAuditTests.Gain_capable_loop_stability_grid_has_no_divergent_corner` and `...sustained_load_oscillation_does_not_pump`.

**References:** Vlach & Singhal, *Computer Methods for Circuit Analysis and Design* (fixed-point/relaxation methods and their convergence conditions).

### Galvanic bridge / bidirectional breaker
- Manatee's breaker model: when Closed, both sides are merged into a single solver matrix and power flows through it in whichever direction the physics dictates — unlike the vanilla Stationeers breaker, which is a one-way Input→Output transfer device.

While Closed, the breaker is stamped as a conventional closed-switch series branch (1 mΩ) between its A and B ports inside the merged matrix. Because it is an ordinary circuit element, the solver reports *signed* through-flow (`Exchange(c).PowerA2B`): positive one way, negative the other — so back-feed is possible where vanilla forbade it. The Re-Volt adaptor derives vanilla's bookkeeping (accumulated throughput, directional trip comparisons, analyzer readouts) from that signed flow rather than the other way around; this divergence is flagged for Sukasa's sign-off (api.md §23 item 1). Opening the breaker is the expensive transition: a tier-3 island rebuild of both halves — the honest cost, since the design deliberately does not attempt cheap split detection, and breakers (as safety devices) change state rarely. State changes go through `Reconfigure`, not the structural `Edit` path.
- **Standard term:** a closed breaker is simply a galvanic (conductive) connection; "bidirectional" is only notable relative to vanilla Stationeers' directional-transfer abstraction, not relative to real electrical engineering, where all conductors carry current both ways.
- **Where it appears:** `docs/api.md` §7 (`CouplerSpec.Breaker()`), §23.1 (sign-off items), §24.18 (coupler document-stability decision); `docs/stationeers.md` Islands and Coupling Devices.

### Galvanic connection
- A direct electrical connection — an actual conductive (metal-to-metal) path along which current can flow, as opposed to a coupling that transfers power without a shared conductor (e.g. through a transformer's magnetic field).

In Manatee this distinction is the rule that partitions the circuit into islands (independent matrices): a galvanic connection means both sides must live in the *same* island and be solved simultaneously, while a power-transfer boundary (decoupling transformer, converter two-port) keeps the sides in *separate* islands that merely exchange values per substep with bounded lag. The classification is per-device-type, decided once, not per-solve. Programming analogy: galvanic connection is shared mutable memory — both sides see the same state and must be updated under one lock (one matrix solve); a power-transfer boundary is message passing between two processes, each with its own state and a small, explicit protocol between them.
- **Standard term:** galvanic connection / galvanic coupling; its absence is the standard EE notion of *galvanic isolation* (see any transformer or opto-isolator treatment).
- **Where it appears:** `docs/solver.md` (Islands, coupling devices); `docs/api.md` §7.
- **References:** Horowitz & Hill, *The Art of Electronics* (transformers and isolation).

### Generator synchronization / paralleling
- Running multiple AC generators onto the same network so they share load, which requires them to be *synchronized* — matched in frequency and phase — before and while connected; in Manatee this is a taught gameplay skill, not an automated background detail.

Two AC generators feeding the same wires must produce their voltage waves in step; closing the connection while they are out of phase is like merging onto a highway at the wrong speed — the mismatch is absorbed violently. In real power systems paralleled machines then pull each other into step through electromagnetic torque: a generator slightly ahead in phase delivers more power, which slows it, and vice versa, so the fleet behaves like masses joined by damped springs. Manatee models exactly that mechanical picture, deliberately outside the circuit solver: each generator *device* carries rotor angle and angular velocity state, integrated with spring–damper coupling through the network ("swing-equation-lite", a simplified form of the classical swing equation — see **Swing equation / swing-lite**), and each tick it drives its voltage source's phase from that state. For a programmer: the solver's interface is unchanged — it only ever sees cheap tier-1 source updates (new phase/amplitude values), while all the rotor dynamics live in a per-device state machine one layer up, like a controller feeding setpoints to a plant. Connecting out-of-phase is the classic hazard; Arc 2 teaches the player to match speed by ear, and the mastery-corollary off-ramp is a craftable **sync-check breaker** that refuses to close while the machines are out of phase — a convenience once the skill is learned, never a requirement.

This is standard power-engineering vocabulary; also called *synchronizing* or *paralleling generators*, and the real off-ramp device is a *synchro-check (25) relay*.
- **Where it appears:** `docs/solver.md` §Analyses (Generator paralleling); `docs/design.md` §Simulation Model, the Mastery corollary, and the Arc 2 progression (lessons culminating in synchronization); `docs/curriculum.md` lesson 17.
- **References:** Bergen & Vittal, *Power Systems Analysis*, and Kundur, *Power System Stability and Control* (the swing equation); Horowitz & Hill, *The Art of Electronics* (AC sources, phase).

### Global ground
The world's shared reference potential — effectively "the earth" — against which shock voltage on bare conductors is measured when an entity touches them.

Electric shock depends not on a wire's voltage in the abstract but on the voltage *between* the two things you're bridging; a creature standing on the ground bridges the conductor to earth. Manatee therefore computes shock damage from the touched conductor's voltage relative to this single world-wide reference, with realistic thresholds (12 V control wiring tingles, 48 V bites, 240 V is lethal — Hazards section, requirement R12). For a programmer: global ground is the shared zero-point of the coordinate system all island node voltages are ultimately comparable in for hazard purposes — like a common epoch that lets timestamps from different machines be compared. Importantly, earth is not free magic in our model: it is an ordinary solver node reached through per-electrode contact resistance, so a deliberately *floating* system (isolation transformer, no earth reference) genuinely has no shock path from a single touched wire — a taught safety lesson, verified in-game with a multimeter or isolation monitor.

Standard concept: this is what electricians call *earth* / *ground potential*; "global" here just emphasizes it is one shared reference across the whole game world, as opposed to each island's internal per-matrix reference node.

**Where it appears:** docs/design.md (R12; Hazards; Grounding/SWER section).

**References:** Horowitz & Hill, *The Art of Electronics*, on grounding; IEC 60479 is the real-world basis for shock-current effect thresholds.

### Ground (`g`)
- The Falstad text-format element type `g`: a one-post element that pins the node it touches to 0 volts, serving as the circuit's voltage reference.

In the Falstad/circuitjs1 dump format our importer reads, `g` is a single-terminal element (most elements have two posts; ground has one). Its only parameter after `flags` is an optional `symbolType`, which is purely cosmetic — it selects which ground symbol is drawn (added to the reader post-2025; older files in the wild omit it, and our importer must accept both forms). Electrically it means "this node is the 0 V reference"; for a programmer, it is like declaring the origin of a coordinate system — every voltage in the circuit is a difference measured against it, and without one the numbers have no anchor. Multiple `g` elements simply tie several nodes to the same reference.

**Standard term:** ground / earth / common; in SPICE netlists the equivalent is naming a node `0`.

**Where it appears:** `docs/falstad-format.md` §4 (element table, citing `GroundElm.java:20-27`) and §7 (reader-compatibility note on the `symbolType` addition).

**References:** ngspice manual (node 0 as global ground); Horowitz & Hill, *The Art of Electronics*, on ground conventions.

### History term / companion history
- The part of a companion model that carries a storage element's previous-timestep state (its last voltage or current) into the current timestep's equations, as an equivalent current source on the right-hand side of the system.

When the solver discretizes a capacitor or inductor with Backward Euler, the element becomes a *companion model*: a fixed conductance (e.g. G = C/dt for a capacitor) plus a current source whose value depends on the previous solution (I = G·V_prev; the inductor is the mirror case). That current source is the **history term**. The conductance lands in the matrix; the history term lands only in the right-hand-side vector — and that split is load-bearing for performance. In Manatee's change-cost tiers, updating a history term is tier 1: the matrix pattern and values are untouched, so the cached LU factorization is reused and each tick costs only a forward/back substitution. With fixed dt, a linear island in steady operation lives entirely in this tier, which is what makes subcycled AC affordable. For programmers: the companion conductance is like compiled code (rebuilt rarely, cached aggressively) and the history term is the per-frame input data fed through it; it is the one piece of mutable state a storage element hands forward each step, updated post-solve.

Standard SPICE terminology — "history term" and "companion model" (also "associated discrete circuit model" or "Norton equivalent of the integration formula") are the textbook names.

**Where it appears:** `docs/solver.md` §Change-Cost Tiers (tier 1: "BE companion history terms") and §Analyses (Backward Euler companion models, state updated post-solve).

**References:** Nagel (1975), *SPICE2: A Computer Program to Simulate Semiconductor Circuits*, UCB/ERL-M520; Vlach & Singhal, *Computer Methods for Circuit Analysis and Design*; Pillage, Rohrer & Visweswariah, *Electronic Circuit and System Simulation Methods* (companion/associated models for numerical integration).

### i²t (i-squared-t)
A measure of accumulated heating energy in a conductor or fuse: the integral of current squared over time, used as the "how close is this thing to melting" quantity.

Resistive heating power is I²R, so for a fixed element the energy deposited is proportional to ∫I²·dt — a fuse or cable does not fail at an instantaneous current, it fails when this integral crosses its melting threshold. Manatee uses i²t as one of the per-limit-type quantities in the compaction layer's limit envelope: when a run of mixed-material cable is collapsed into one equivalent resistor, instantaneous ampacity, i²t thermal mass, and melting threshold can each pick a *different* weakest segment, which is exactly how a lead segment in a copper run behaves as a fuse with no special-casing. The i²t part of the envelope is kept as a **Pareto-minimal set of (rating, melt) pairs**, one per distinct material, not a single hybrid pair — a hybrid of one segment's rating and another's melt threshold would trip when no raw segment would. For programmers: i²t is a leaky-bucket counter driven by I², and the Pareto set is like keeping the full skyline of dominating (rate-limit, burst-budget) pairs instead of collapsing them into one lossy pair.

**Standard term:** I²t rating / Joule integral (also "let-through energy") — standard fuse-datasheet terminology.

**Where it appears:** `docs/compaction.md` Responsibilities #4 (limit attribution, Pareto envelope); `docs/api.md` §19.

**References:** Horowitz & Hill, *The Art of Electronics* (fuses and overcurrent protection); any manufacturer fuse datasheet's I²t rating.

### i²t (i2t) accumulator / melting integral
The running per-component counter that models slow overload heating: it grows while current exceeds the element's rating, decays while under it, and fires a limit event when it crosses the melt threshold.

The update rule (api.md §12): when over rating, add `(I² − rating²)·dt`; when under, cool by `acc·dt/τ` (exponential decay with time constant τ); trip when the accumulator crosses `MeltI2t`. One mechanism therefore covers both a fast fuse pop and a cable that smokes after minutes of mild overload — R7's design goal that fuses and cable heating share a mechanism. On subcycled-AC islands the integral is accumulated **per substep**, making it a true ∫I²dt over the waveform rather than a phase-blind tick-boundary sample. An envelope-carrying component (see the Pareto entry) runs one accumulator *per pair*; accumulators are serialized (`EnvI2t` state unit), so a part-melted cable saves and reloads part-melted. The solver only *reports* the trip — actually popping the fuse is the client's edit next tick. For programmers: it is precisely a leaky-bucket rate limiter (token debt grows with excess I², leaks with time constant τ, hard limit = melt).

Our cooling term is a first-order (exponential) thermal model, standard for approximate fuse/thermal-breaker curves; the accumulator-with-decay formulation itself matches common "thermal memory" overload models rather than any single textbook equation. "Melting integral" is our informal name for the accumulator.

**Where it appears:** `docs/api.md` §12 (`I2tPair`, `LimitKind.ThermalI2t`, `I2tFraction`) and §19; `docs/solver.md` State, Limits, Probes (R7); `docs/design.md` R7.

**References:** Horowitz & Hill, *The Art of Electronics* (fuse behavior and I²t); IEC 60269 / fuse-datasheet I²t practice.

### i²t thermal accumulator
The same ∫I²dt heating counter, viewed from the Stationeers/Re-Volt side: it is the manatee-core mechanism that reproduces Re-Volt's existing delayed-burn cable behavior.

In the Re-Volt integration (stationeers.md, Graph Construction), fuses and breaker trip curves ride ordinary manatee-core limit events; because those events include the i²t thermal accumulator, a cable that Re-Volt would previously burn "after a while" under overload now burns by mechanism — the accumulated ∫(I²−rating²)dt crossing the melt threshold — rather than by a special-cased timer. Ratings are ambient-temperature-adjusted through the compaction layer's limit envelope, so Europa's −150 °C and Vulcan's +800 °C move real thresholds instead of being special cases. For programmers: the accumulator is solver-owned state (rides the per-island snapshot as an `EnvI2t` unit); `LimitEvent` only surfaces `I2tFraction`, reporting how close to melt — so the game mod consumes a fraction-of-doom number instead of reimplementing thermal bookkeeping.

**Standard term:** I²t / thermal memory (as in fuse and thermal-magnetic breaker trip curves).

**Where it appears:** `docs/stationeers.md` Graph Construction; backed by `docs/api.md` §12 (`LimitEvent.I2tFraction`) and §14 (snapshot state units), plus `docs/compaction.md` #4 (ambient adjustment).

### ideal / idealized transformer (AddIdealTransformer)
- `AddIdealTransformer` is the netlist call that creates the solver's one multi-terminal primitive: a lossless two-winding transformer (a four-terminal element — two ports of two terminals each, the `ideal transformer two-port` of solver.md) that enforces a fixed turns-ratio constraint between two ports inside a single island's matrix.

Mechanically it stamps two auxiliary rows into the MNA system expressing the turns-ratio constraint (secondary voltage = ratio × primary voltage, primary current = ratio × secondary current), exactly like a voltage source's branch-current row. It has to be a primitive: magnetic coupling is mathematically inexpressible as any composition of independent two-terminal R/L/C/V/I elements, so without it the devices layer could not build the canon-required `IdealizedTransformer` device or the tablet's AC curriculum. For programmers it is a foreign-key constraint linking two otherwise separate table rows — an invariant enforced *by the equation system itself* rather than by any component's local behavior — and you cannot fake the linkage by adding more independent rows. Real-transformer imperfections (leakage inductance, magnetizing branch) are *not* baked in; the device layer composes them around the primitive as ordinary L/R elements. It is SPICE-representable as a VCVS/CCCS pair, which keeps transformer lessons testable against the ngspice oracle. It rounds out the solver's closed component set (resistor, V/I sources, capacitor, inductor, switch, diode, and this); everything fancier is a devices-layer composition. Note the naming layers: the *primitive* is `AddIdealTransformer` (ideal, textbook sense); the *device* built on it is `IdealizedTransformer` (see that entry); neither is a coupler.

The underlying concept is the standard EE **ideal transformer**, and "two-port" is standard EE vocabulary; the two-auxiliary-row MNA stamp is the textbook treatment of such constraint elements.
- **Where it appears:** `docs/api.md` §6 (`AddIdealTransformer`), §18 (`IdealizedTransformer` device), §24 decision log #23; `docs/solver.md` Component Set (the ideal transformer two-port).
- **References:** Ho, Ruehli & Brennan (1975), "The Modified Nodal Approach to Network Analysis", IEEE Trans. Circuits and Systems; Vlach & Singhal, *Computer Methods for Circuit Analysis and Design* (MNA stamps for controlled sources and ideal transformers); Pillage, Rohrer & Visweswariah, *Electronic Circuit and System Simulation Methods* (two-port and constraint-element formulations); Horowitz & Hill, *The Art of Electronics* (transformer basics).

### idealized transformer
- Our name for the small/local transformer *device class*: an ideal transformer two-port plus explicit leakage and magnetizing parameters, all living inside one island's matrix — as opposed to the *decoupling* transformer, which is an island boundary.

Manatee models transformers as two distinct classes (an Electrical Age precedent, settled 2026-07-05). An **idealized** transformer is for small, local use: the devices layer builds it (`IdealizedTransformer`, api.md §18) from the `AddIdealTransformer` primitive plus ordinary L/R elements for leakage inductance and the magnetizing branch, so the whole thing is solved in the same matrix, same tick, with full electrical fidelity — reactive power, impedance transformation, and phase all behave honestly. A **decoupling** transformer is for utility-scale links: it splits the circuit into two independent islands (separate matrices, potentially separate threads) that exchange amplitude and phase per substep, which scales better but launders away the reactive detail. That is why the tablet curriculum uses idealized transformers throughout — power-factor lessons must be same-island. For programmers: idealized = one consistent transaction over one datastore; decoupling = two services exchanging messages, eventually consistent. Gameplay steers long-distance transfer toward decoupling types, exactly where thread isolation pays.

**Project-coined term**, closest standard concept: an **ideal transformer** augmented with a standard lumped equivalent-circuit model (leakage inductances + magnetizing branch). "Idealized" here distinguishes the device *class* from the decoupling class — it does not mean lossless.
- **Where it appears:** `docs/design.md` Simulation Model (island coupling); `docs/solver.md` Component Set and Islands; `docs/api.md` §18 (`IdealizedTransformer` device, explicitly *not* a coupler); `docs/curriculum.md` authoring rules.
- **References:** Horowitz & Hill, *The Art of Electronics* (transformer equivalent circuits); Vlach & Singhal, *Computer Methods for Circuit Analysis and Design*.

### Idealized vs decoupling transformer
- Manatee splits transformers into two device classes (an Electrical Age precedent, settled 2026-07-05): an *idealized* transformer is a small, local device solved inside a single island's matrix, while a *decoupling* transformer is a utility-scale device that forms the boundary between two separately-solved islands.

An **idealized** transformer is a same-matrix two-port: its turns-ratio constraint is stamped via coupled auxiliary equations, with leakage and magnetizing behavior as ordinary L/R element parameters, all inside one island's system — so both sides are solved simultaneously and exactly, which is what the tablet curriculum's reactive-power and power-factor lessons rely on (they must be same-island). A **decoupling** transformer instead ends one island and starts another: the two windings belong to separate islands (separate matrices, potentially separate threads) that exchange only amplitude and phase per substep, damped by explicit relaxation so the exchange converges instead of oscillating — a thread-isolation point that launders the reactive detail away. Gameplay deliberately steers long-distance power transfer toward decoupling types, which is exactly where the parallelism pays. For programmers: idealized = one consistent transaction over one datastore under a single lock; decoupling = two services exchanging messages, eventually consistent, cheap isolation. For EEs: both are transformers; the distinction is purely which solver partition each winding lives in, not the physics modeled.

**Project-coined terms**, closest standard concepts: an ideal-transformer MNA stamp augmented with the standard lumped equivalent-circuit model (coupled-inductor / mutual-inductance, T-equivalent) for *idealized*, versus a partitioned / waveform-relaxation coupling boundary between subcircuits for *decoupling* ("decoupling transformer" is not a distinct transformer type in the literature). "Idealized" distinguishes the device *class*; it does not mean lossless.
- **Where it appears:** `docs/design.md` Simulation Model ("Island coupling (transformers)"; R5; decision log 2026-07-05); `docs/solver.md` (Component Set, Islands); `docs/api.md` §18 (`IdealizedTransformer` device, explicitly *not* a coupler); `docs/vintage-story.md` and `docs/curriculum.md` (frequency/transformer gameplay, authoring rules).
- **References:** Ho, Ruehli & Brennan (1975), "The Modified Nodal Approach to Network Analysis", IEEE Trans. Circuits and Systems (stamping two-ports into the nodal matrix); Lelarasmee, Ruehli & Sangiovanni-Vincentelli (1982), "The Waveform Relaxation Method for Time-Domain Analysis of Large Scale Integrated Circuits", IEEE Trans. CAD (relaxation-coupled partitions); Vlach & Singhal, *Computer Methods for Circuit Analysis and Design*.

### impedance
- The AC generalization of resistance: how much a component opposes alternating current, where capacitors and inductors oppose it in a frequency-dependent way and also shift the current's timing relative to the voltage.

For a resistor, opposition is one number (ohms) and current tracks voltage instantly. Capacitors and inductors instead respond to *change*: a capacitor passes more current the faster the voltage wiggles, an inductor less — so their effective "resistance" depends on frequency, and the current waveform leads or lags the voltage waveform. For a programmer: a resistor is a pure function of the present input, while capacitors and inductors carry internal state, so their output depends on input *history* — the phase shift is that statefulness made visible on a scope. In Manatee this is tablet Lesson 15, taught by direct time-domain observation ("phase, without the word 'phasor'"): the solver runs waveforms sample by sample rather than in the frequency domain, so students see the lead/lag on the oscilloscope instead of manipulating complex numbers. Impedance also marks the curriculum ceiling — AC power topics (transformers, impedance, power factor, synchronization) are as far as the tablet goes.

This is the standard EE term (symbol Z, measured in ohms; frequency-domain treatments use complex numbers/phasors, which the curriculum deliberately avoids naming).

**Where it appears:** `docs/curriculum.md` Lesson Arc III, Lesson 15 (and authoring rule: demonstrate same-island because idealized transformers launder reactive power); `docs/design.md` Non-Goals (curriculum ceiling).

**References:** Horowitz & Hill, *The Art of Electronics*, ch. 1 (impedance and reactance); Vlach & Singhal, *Computer Methods for Circuit Analysis and Design*.

### inductor
A two-terminal component (symbol L, unit henry, H) that stores energy in the magnetic field created by the current flowing through it, and resists changes to that current.

Where a capacitor remembers a *voltage*, an inductor remembers a *current*: its state variable is the current through it, and that current cannot jump instantaneously — changing it takes time and voltage, just as changing a capacitor's voltage takes time and current. For programmers: an inductor is a stateful object whose field is "current," with an update rule that integrates applied voltage over time (v = L·di/dt); it is the dual of the capacitor, with the roles of voltage and current swapped. In the curriculum this shows up twice: Lesson I.5 uses an RLC circuit's ring-down to show energy sloshing back and forth between the capacitor's voltage-state and the inductor's current-state (like two coupled variables in a damped oscillator), and Lesson III.15 shows that on AC an inductor presents frequency-dependent opposition to current (impedance) with the current lagging the voltage — taught deliberately "without the word phasor." In the solver an inductor is a reactive element handled by a companion model during transient analysis.

**Where it appears:** `docs/curriculum.md` Lessons I.5 (RLC ring-down) and III.15 (impedance on AC); as element `l` in the Falstad importer (`docs/falstad-format.md`); companion-model treatment in `docs/solver.md`.

**References:** Horowitz & Hill, *The Art of Electronics*, 3rd ed., ch. 1 (inductors, reactance); Pillage, Rohrer & Visweswariah, *Electronic Circuit and System Simulation Methods* (McGraw-Hill, 1995) — companion models for reactive elements.

### inductor (`l`)
The Falstad/circuitjs1 text-format element code for an inductor, which Manatee's importer accepts with restrictions.

In the classic Falstad text dialect, an `l` line carries the element's inductance in henries plus a state value — the current through the inductor in amperes — and, in newer (2025–2026) files, two optional parameters: `initialCurrent` (the current to reset to) and `saturationCurrent` (a nonlinearity threshold above which the real device's inductance would collapse). For programmers: think of the line as a serialized object — one constructor parameter (inductance), one field of runtime state (current), two optional config values. Manatee's importer honors the inductance and the initial current as the element's starting state, but **rejects** any file with a nonzero `saturationCurrent`, because a saturating (nonlinear) inductor is out of scope for the solver; rejection is loud rather than silent per the importer's accept/reject contract. The parameter layout is verified against the circuitjs1 source (`InductorElm.java:38-47,50-52`).

**Where it appears:** `docs/falstad-format.md` §4 (element table) and §7 (importer accept/reject contract).

**References:** Paul Falstad & Iain Sharp, circuitjs1 source (https://github.com/sharpie7/circuitjs1), `InductorElm.java` — the format has no formal spec; the source is the authority, as `docs/falstad-format.md` documents.

### inrush
The brief surge of extra current a load draws at the instant it is switched on, before it settles to its steady operating current.

The canonical example, and the one Manatee cares about, is an incandescent lamp: a cold tungsten filament has roughly a tenth of its hot resistance, so at switch-on it briefly draws roughly ten times its rated current until it heats up and its resistance rises. For programmers: the lamp is a component whose "resistance" field is a function of an internal temperature state, and inrush is the transient you see while that state converges — a warm-up phase, like a cache running cold until it fills. In Manatee this is optionally modeled behaviorally rather than physically: machine components have a device `Tick()` that recomputes their stamp parameters from game state each tick (`docs/solver.md`: "filament temp → resistance if we want inrush"), so inrush emerges from a temperature integrator updating a resistor value, not from any special solver feature. Inrush matters for gameplay realism because it is what trips fuses and sags supplies when large loads start.

**Where it appears:** `docs/solver.md`, Component Set — "Machines" (lamps, heaters, motors), as an optional consequence of temperature-dependent resistance in `Tick()`.

**References:** Horowitz & Hill, *The Art of Electronics*, 3rd ed. — discusses lamp cold-filament resistance and switch-on surge in the context of fusing and soft-start.

### inter-region resistance
- The resistance the reduction layer inserts between two adjacent conductor regions, computed from the materials' resistivity and the cross-section of their contact.

When the compaction layer groups conductor geometry into regions (union-find over equipotential material), perfect conductors merge into one node, but resistive materials each form their own region. Where two regions touch, the layer emits a resistor whose value comes straight from physical data: material resistivity and the contact area between them. The payoff is that behavior emerges from data rather than code paths — a segment of lead spliced into a copper run automatically becomes a high-resistance, low-melting-point link, i.e. a fuse, with zero special-casing. For programmers: it is like deriving edge weights in a graph from node attributes instead of hand-labeling them; for EEs: it is the ordinary R = ρl/A treated per material boundary. The term itself is project phrasing for that per-boundary derived resistance; the underlying concept is standard resistivity-based lumped resistance.

- **Where it appears:** `docs/compaction.md` (Responsibilities #1, region building); realized in `core/Manatee.Core/Reduction/ConductorGraph*`.
- **References:** Horowitz & Hill, *The Art of Electronics* (resistivity and wire/fuse basics).

### interior node
- A circuit node that sits in the middle of a series chain of resistors and is removed by series-chain collapse, yet stays readable through probe interpolation.

Series collapse replaces a run of resistors with no side branches by one equivalent resistor; every node strictly inside that run — connected to exactly its two chain neighbors — is an interior node and disappears from the netlist the solver actually sees. The reduction layer keeps them observable anyway: since the same current flows through the whole chain, the voltage at any interior point is recovered by linear interpolation weighted by resistance along the chain (a voltage divider, computed after the fact). For programmers: it is like a field elided by a compiler optimization that the debugger can still reconstruct on demand from surrounding state — eliminated from the hot path, not from the observable model. This is why an in-game meter can read "inside" a compacted cable run of thousands of segments even though the solver only ever saw one resistor.

**Standard term:** internal node eliminated by series reduction (closely related to Gaussian/Kron elimination of internal nodes in network reduction).
- **Where it appears:** `docs/compaction.md` (Responsibilities #2–#3, series collapse and probe interpolation); api.md §19.
- **References:** Vlach & Singhal, *Computer Methods for Circuit Analysis and Design* (network reduction and node elimination).

### interlock
- A relay-logic wiring pattern that makes two (or more) conflicting states physically impossible to activate at the same time.

The classic example: two relays each routed through the other's normally-closed contact, so energizing one breaks the path that could energize the other — a motor cannot be commanded forward and reverse simultaneously no matter what the buttons say. The guarantee is enforced by the wiring itself, not by any controller checking a rule. For programmers this is exactly a mutual-exclusion invariant (a mutex) implemented in hardware: the "lock" is a contact in series with the competitor's coil, and it cannot be forgotten or raced past. In the curriculum it appears in Lesson II.9 alongside the latch, as relay-logic building blocks the player needs before the elevator-controller capstone (Lesson II.10), where interlocks keep call logic and travel direction consistent.

The term is standard in industrial control and relay logic (also seen as "electrical interlock" or, for the motor case, "cross-interlocking").
- **Where it appears:** `docs/curriculum.md` (Lesson Arc II.9, relay logic).
- **References:** Horowitz & Hill, *The Art of Electronics* (relays and switching); Petruzella, *Electric Motors and Control Systems* (interlocking in motor control).

### internal resistance
The resistance inside a power source itself, which makes its output voltage sag as you draw more current from it.

No real battery or generator delivers its rated voltage regardless of load: the chemistry and conductors inside have their own resistance, so the harder you pull current, the more voltage is lost internally before it ever reaches the terminals. In Manatee this is a modeled, gameplay-visible parameter: batteries in the solver's component set are behavioral models of "EMF + internal resistance + state-of-charge", and Arc 1's voltaic pile is deliberately given a *high* internal resistance so early-game power is weak and saggy — curriculum Lesson I.6 ("batteries are not ideal") teaches exactly this. For programmers: it is like a server with limited throughput — the advertised capacity is only what you see at zero load, and response degrades as demand rises; the sag is not a bug, it is the resource model. This is a completely standard EE concept (also called *source resistance* or, generalized, *output impedance*); a real source is modeled as an ideal source in series with this resistor (the Thévenin picture).

**Where it appears:** `docs/solver.md` (Component Set: battery model), `docs/design.md` (Arc 1 — the voltaic pile), `docs/curriculum.md` (Lesson I.6).

**References:** Horowitz & Hill, *The Art of Electronics* (Thévenin equivalent sources and source impedance).

### internal series resistance
A small resistor Manatee deliberately places in series with every adapted (legacy Stationeers) power source so that generators wired in parallel produce a solvable circuit instead of a mathematical contradiction.

The legacy-device adaptor stamps Stationeers generators into the solver as ideal voltage sources at their tier's nominal voltage. Two *ideal* voltage sources in parallel that disagree even slightly demand infinite current — the equations have no solution, and the solver would flag it as a designed-in singularity (see solver.md failure handling). Giving each source a small series resistance makes the system well-posed: disagreeing sources now share load through their resistances instead of fighting to infinity. For programmers, this is like adding a tiny timeout or backoff to break a deadlock between two processes that each insist on owning the same resource — a small, physically honest perturbation that turns "no valid state exists" into a graceful compromise. It doubles as physical realism, since real generators have internal resistance anyway (see *internal resistance*). Settled 2026-07-05 together with the across-tick current clamp that enforces advertised-power limits.

This is standard practice in circuit simulation; SPICE-family tools use the same trick (a source's series resistance, sometimes a solver-inserted minimum conductance) to avoid singular matrices.

**Where it appears:** `docs/stationeers.md` (Legacy-Device Adaptor), `docs/design.md` (Doc-review round 2026-07-05).

**References:** Vlach & Singhal, *Computer Methods for Circuit Analysis and Design* (ill-posed source configurations and matrix singularity); the ngspice manual (source series resistance, gmin).

### Is / Rs / N / BV
Four of the standard SPICE diode-model parameters — the DC-curve subset that Falstad's `34` model line carries and that manatee-core maps: saturation current (Is), series resistance (Rs), emission coefficient (N), and breakdown voltage (BV).

The full SPICE diode model has a dozen-plus parameters (junction capacitance, transit time, temperature coefficients, ...); these four are the ones that determine the DC current–voltage curve, and they are what the Falstad format transports. **Is** (saturation current, amperes) sets the tiny leakage scale of the exponential law `I = Is·(e^(V/(N·Vt)) − 1)`; smaller Is means the diode "turns on" at a higher voltage. **N** (emission or ideality coefficient, dimensionless, ~1–2) stretches that exponential — silicon junctions sit near 1, Schottky and LED-like devices higher. **Rs** (series resistance, ohms) is the ohmic resistance in series with the ideal junction, dominating at high current. **BV** (breakdown voltage, volts) is where the reverse-biased diode starts conducting hard — the parameter that makes Zener diodes work on purpose. For a programmer: the diode element is a class, and `34` model-definition lines in a Falstad file are named constructor argument sets — `34 escapedName flags Is Rs N BV forwardCurrent`, so the line carries a fifth numeric field beyond these four; `d`/`z` elements reference a model by name. Manatee's Falstad importer accepts `34` lines and maps Is/Rs/N/BV onto manatee-core's own diode parameters; other named models are rejected loudly.

Standard SPICE diode-model parameter names (SPICE also calls N "ideality factor"; thermal voltage Vt ≈ 25.85 mV at room temperature).

**Where it appears:** `docs/falstad-format.md` §4 (dump type `34`, `DiodeModel.java` field order) and §7 (accepted `d`/`z`/`34` handling).

**References:** Nagel (1975), *SPICE2: A Computer Program to Simulate Semiconductor Circuits*, UCB/ERL-M520 (the canonical diode model and parameter set); the ngspice manual, diode model chapter; Horowitz & Hill, *The Art of Electronics* (diode behavior in practice).

### isolation transformer
- A transformer used not to change voltage but to break the electrical connection between a circuit and earth, producing a *floating* system.

A transformer transfers power between two windings through a magnetic field with no wire joining them, so the output side has no conductive path to the input side — and, if you deliberately leave it unconnected to earth, no path to ground either. In Manatee's grounding model this is the standard way players build floating systems, which are buildable by default: with a verified floating system, touching any *one* wire is safe, because a shock requires a complete circuit and there is no return path through earth back to the source. The curriculum teaches verification as a ritual (multimeter reads wire-to-earth voltage; an isolation-monitor lamp pair is the advanced companion build), because a floating system can silently re-ground itself — in-game, via rain-wetted bare spans. For a programmer: an isolation transformer is like a message queue between two services — data (power) flows through, but neither side shares the other's address space (ground reference), so a fault in one cannot reach into the other.

This is the standard EE term; also called a *galvanic isolation* transformer, and the resulting supply an *ungrounded* or *IT* (isolated-terra) system in wiring-standards vocabulary.

- **Where it appears:** `docs/design.md`, grounding model section (floating systems and the isolation-monitor pedagogy).
- **References:** Horowitz & Hill, *The Art of Electronics* (transformers and grounding practice).

### joules per half-second
- The unit in which Stationeers' vanilla power API expresses power: the amount of energy (in joules) a device provides or draws during one 0.5-second game tick.

Stationeers runs its power logic once every half second, and the vanilla device calls (available/needed, provide/draw) pass energy quantities per tick rather than a rate. Since power = energy / time, a value of X joules per half-second is simply 2·X watts — the same quantity in a tick-shaped wrapper. Manatee's Legacy-Device Adaptor converts between this per-tick energy bookkeeping and the solver's continuous view (constant-power loads linearized as conductances), then reports back *actuals* in the same per-tick units so vanilla accounting stays intact. For programmers: this is the classic "amount per fixed timestep" vs "rate" distinction, like bytes-per-poll-interval versus bytes-per-second.

For EEs: this is just power sampled at dt = 0.5 s and pre-multiplied by dt; there is no exotic physics here, only a unit convention imposed by the game's fixed tick.

**Standard term:** power (watts), expressed as energy per fixed timestep; equivalently E = P·Δt with Δt = 0.5 s.

**Where it appears:** `docs/stationeers.md` (Legacy-Device Adaptor); the adaptor is the only place the unit crosses into manatee-core's world.

### junctions
- Points in the schematic document where wires meet and connect — the places that become electrical nodes when the drawing is turned into a netlist.

In the harness's document model (the first of its three pure layers), a schematic is components, wires, junctions, and probe placements. A junction records that wires crossing or ending at the same grid point are actually joined, not merely drawn overlapping — the classic schematic-capture distinction marked by a connection dot. During netlist extraction, each maximal set of wires joined through junctions collapses to a single node handed to manatee-core. For programmers: junctions define the edges of a union-find problem — "these wire endpoints are the same electrical point" — and each resulting equivalence class is one node, like interned strings where many references collapse to one canonical object.

**Standard term:** junction / connection point in schematic capture; the merged result is a node (as in nodal analysis).

**Where it appears:** `docs/harness.md` (Layering, layer 1: document model); downstream as nodes in `docs/solver.md`.

**References:** node/branch terminology per Ho, Ruehli & Brennan (1975), "The Modified Nodal Approach to Network Analysis," IEEE Trans. Circuits and Systems.

### KCL / KCL residual
- Kirchhoff's Current Law: at every node the currents flowing in must sum to exactly zero (charge doesn't pile up at a junction); the *residual* is how far a computed solution actually is from that zero, checked after every solve.

KCL is the physical conservation law MNA is built on — each node contributes one KCL (current-balance) row to the matrix; MNA additionally augments it with branch-current rows for voltage sources and inductors, and those extra rows are constitutive equations (e.g. V_a − V_b = E) rather than KCL. After solving, Manatee re-sums the currents into each node from the computed voltages via each component's own i–v law; the leftover (the residual) should be ~0 up to floating-point noise (e.g. < 1e-9 A), and any larger value means the solver produced a non-physical answer — a bad stamp, a factorization problem, a bug. It is the cheapest, most immediate detector of *stamp bugs*: a component that wrote wrong entries into the matrix shows up as a node that "leaks current." Crucially this is an independent recomputation of nodal balance from the published voltages — *not* the linear-solve residual **b − Ax**, which any accurate solve drives to ~0 by construction regardless of whether the stamps were physically correct, and so cannot catch stamp bugs. For a programmer: KCL is a conservation invariant like "debits equal credits" in double-entry bookkeeping, and the residual is the balance check after every transaction batch, treating the math as untrusted input. It is an always-on check in debug builds, assertable in any test via `CheckInvariants(InvariantChecks.Kcl)`, exposes the worst offender directly (`InvariantReport.MaxKclResidual`, `WorstKclNode`), and serves as one leg of Newton–Raphson's dual convergence test for nonlinear circuits.

**Standard term:** Kirchhoff's Current Law (KCL); the residual check corresponds to the residual norm used in SPICE-family convergence testing.
- **Where it appears:** `docs/design.md` R1 and Testing (invariant checks); `docs/solver.md` (Newton dual convergence test); `docs/testing-strategy.md` (invariants); `docs/api.md` §11 (`CheckInvariants`, `InvariantChecks`, `InvariantReport.MaxKclResidual` / `WorstKclNode`); `docs/integration-tutorial.md` §8 (invariants as a debugging tool).
- **References:** Ho, Ruehli & Brennan (1975), "The Modified Nodal Approach to Network Analysis," IEEE Trans. Circuits and Systems; Nagel (1975), SPICE2 memo UCB/ERL-M520; Pillage, Rohrer & Visweswariah, *Electronic Circuit and System Simulation Methods*.

### Kirchhoff's laws
- The two conservation laws of circuit theory — current in equals current out at every junction, and voltages around any loop sum to zero — from which the entire solver's math is derived.

Kirchhoff's Current Law (KCL) says charge doesn't accumulate at a node: all currents flowing into a junction sum to zero. Kirchhoff's Voltage Law (KVL) says the electric potential is single-valued: walking any closed loop, the voltage rises and drops cancel out. Together with each component's own current–voltage relationship (Ohm's law for a resistor, etc.), they turn a circuit diagram into a system of simultaneous equations; MNA (R1) is a systematic recipe for assembling and solving exactly that system, yielding every node voltage and branch current. For a programmer: these are the global invariants of the domain — analogous to "debits equal credits" in double-entry bookkeeping or flow conservation in a max-flow graph — and because they must hold in any valid solution, Manatee also uses them *after* solving as runtime checks (see **KCL / KCL residual**).

Standard textbook terms (KCL/KVL); no project-specific twist.
- **Where it appears:** `docs/design.md` requirement R1 ("node voltages and branch currents from Kirchhoff's laws").
- **References:** Horowitz & Hill, *The Art of Electronics*; Ho, Ruehli & Brennan (1975), "The Modified Nodal Approach to Network Analysis", IEEE Trans. Circuits and Systems.

### knee
- The sharp bend in a diode's current–voltage curve — the narrow voltage region where the diode transitions from "essentially off" to "conducting hard" — which is exactly where the solver's iterative method struggles most.

A diode's current grows exponentially with voltage: below roughly 0.6–0.7 V (silicon) it passes almost nothing; just above, current explodes. Plotted, the curve looks like a hockey stick, and the bend is the knee. The solver handles such nonlinear components with Newton–Raphson iteration — repeatedly linearizing the curve at the current guess and re-solving — and near the knee the linearization changes drastically between iterations, so this is where Newton can overshoot, oscillate, or diverge. Programming analogy: it is the worst-case input for a fixed-point iteration, the region where each retry lands you somewhere very different, like binary-searching a function with a near-discontinuity. Our testing strategy deliberately targets it: the Newton-robustness fuzz tests sweep randomized diode-circuit source values *through the knee* and require the solver to either converge or Fault legibly — never emit NaN, never hang.

Standard EE colloquialism (also "knee voltage"; related standard terms: cut-in/threshold voltage, forward voltage drop).
- **Where it appears:** `docs/testing-strategy.md` Property and Fuzz Tests (Newton robustness sweep).
- **References:** Horowitz & Hill, *The Art of Electronics* (diode I–V behavior); Nagel (1975), *SPICE2: A Computer Program to Simulate Semiconductor Circuits*, UCB/ERL-M520 (Newton–Raphson treatment of diode nonlinearity).

### Ladder / mesh (random circuit)
- Two families of randomly generated test-circuit shapes — chains of components ("ladders") and cross-connected grids ("meshes") — used as seeded inputs for property tests and equivalence tests.

In circuit vocabulary, a *ladder* is a chain of alternating series and shunt elements (like the side rails and rungs of a ladder), and a *mesh* topology adds cross-links so the graph contains loops — the two shapes exercise very different code paths: ladders are the ideal case for series-collapse reduction, while meshes force the solver to handle genuine simultaneous equations. In our test suite these are generated from a random seed as connected graphs of resistors, voltage sources, and current sources (R/V/I), then solved and checked against invariants (conservation laws) and, for a nightly sample, against ngspice. For a programmer: this is standard property-based testing (à la QuickCheck) — generate structured random inputs from a seed so failures are reproducible, and assert properties that must hold for *any* input, e.g. "raw solve and series-collapsed solve give identical terminal answers." A standing benchmark suite ("ladder DC") also uses the ladder shape to time the cheapest solve path. The terms *ladder network* and *mesh* are standard EE usage; our specific twist is only that they name random-generator families in the test harness.

**Where it appears:** `docs/testing-strategy.md` (Equivalence Tests; Property and Fuzz Tests; Benchmarks — the "ladder DC" standing suite).

**References:** Horowitz & Hill, *The Art of Electronics*, Cambridge University Press (ladder/mesh network basics); Claessen & Hughes (2000), "QuickCheck: A Lightweight Tool for Random Testing of Haskell Programs", ICFP (the property-based-testing model).

### latch
- A relay wiring pattern where the relay's own contact keeps its coil energized, so the circuit stays on after the button that triggered it is released.

Press a momentary button: current flows through the coil, the relay pulls in, and one of its own contacts closes a parallel path around the button. Now the button can be released — the relay feeds itself through that contact until something (typically a normally-closed "stop" or limit-switch contact in series) breaks the holding path. For programmers: this is a one-bit set/reset flip-flop built from a switch and feedback — the same idea as an SR latch in digital logic, implemented in electromechanics. It is the foundational memory element of relay logic, taught in Lesson 9 as the prerequisite for the elevator-controller capstone.

**Standard term:** latching (seal-in / holding) circuit; the parallel contact is the *seal-in contact*. Digital-logic cousin: SR latch.

**Where it appears:** `docs/curriculum.md`, Lesson Arc II, Lesson 9 ("Relay logic: latch, interlock").

**References:** Horowitz & Hill, *The Art of Electronics*, 3rd ed. (flip-flops and latches); classic industrial-control ladder-logic texts treat the seal-in circuit as the canonical motor-starter pattern.

### latching relay
- In this project: an ordinary relay wired so that it holds its own state — the seal-in latch of Lesson 9 put to work as the memory element of the elevator controller.

The Lesson 10 capstone ("call buttons, limit switches, latching relay — mirrors the in-world build") uses no special hardware: it is the same relay component from Lesson 8, latched via the seal-in feedback technique taught in Lesson 9. A call button momentarily energizes the coil; the relay's own contact then holds the coil energized, remembering which floor was requested until a limit switch breaks the holding path and clears it. This matches `docs/design.md`'s framing of the elevator as "pure electromechanics" — just more MNA at low voltage, relays wired by the player into an actual relay-logic controller. For programmers: the relay by itself is a level-triggered signal; wrapped in the seal-in feedback loop it becomes a stored variable that persists until explicitly overwritten.

Terminology note: in industry, *latching relay* often names a distinct physical device that holds state mechanically without coil current (also called an *impulse relay*, *bistable relay*, or *keep relay*). The curriculum does not ship such a component — here the phrase means a relay used in a latching (seal-in) configuration.

**Where it appears:** `docs/curriculum.md`, Lesson Arc II, Lesson 10 (elevator controller); `docs/design.md`, "Elevator control: pure electromechanics".

**References:** Horowitz & Hill, *The Art of Electronics*, 3rd ed. (relays and bistable elements).

### lead-acid accumulator
- The rechargeable battery of Arc 2 — the real 1859 Planté chemistry, presented in-game as the "advanced" storage step up from the consumable voltaic pile.

Historically the first practical rechargeable battery (Gaston Planté, 1859; still what starts most cars), it fits the mod's tech-progression fiction exactly: the Arc 1 voltaic pile burns zinc for electricity, and the accumulator is the answer to "why storage mattered." In manatee-core it is a *behavioral* electrical model, not a chemistry simulation: the solver sees an EMF source with internal resistance and a state-of-charge variable, the way a programmer would stub a complex subsystem behind a small interface that exposes only terminal voltage, current, and stored energy. The battery fiction arc is voltaic pile → lead-acid → ruins-loot li-ion, with chemistry explicitly stubbed behind behavioral models throughout.

Standard term; "accumulator" is the traditional (especially British/European) word for a rechargeable secondary cell — modern usage says *lead-acid battery*.

**Where it appears:** `docs/design.md`, Arc 2 (power engineering) and the resolved battery-fiction decision under Open Questions.

**References:** Horowitz & Hill, *The Art of Electronics*, 3rd ed. (battery characteristics and internal resistance).

### Leakage / magnetizing elements

The imperfections of a real transformer — a little inductance that doesn't couple between windings (leakage) and a branch representing the energy needed to magnetize the core (magnetizing) — modeled in Manatee as ordinary circuit elements attached to the ideal transformer.

A textbook-ideal transformer transfers power perfectly and instantly; real ones leak a bit of magnetic flux and spend some current just keeping the core magnetized. Both of Manatee's transformer device classes expose these parasitics through the same parameter set (`TransformerParams`: turns ratio, leakage ohms, magnetizing ohms — api.md). For the *idealized* (small, same-island) transformer they are simply honest inductor/resistor elements stamped into the same matrix as everything else. For the *decoupling* (utility-scale) transformer, which is an island *boundary*, the key project decision applies (solver.md, Islands): although this leakage/magnetizing storage is physically real, we do **not** rely on it to stabilize the coupling between islands — its time constants are around a millisecond at rated load, shorter than one solver substep, so any smoothing it provides is invisible at our timestep. Programming analogy: it is like a small hardware buffer that genuinely exists but is too small to paper over scheduling gaps, so the software adds its own explicit smoothing (relaxation on the exchanged boundary values) instead of trusting it.

This is standard EE vocabulary — the elements of the classic transformer equivalent circuit (leakage inductance, magnetizing inductance / magnetizing branch); our only twist is the explicit "present but not load-bearing for stability" stance at island boundaries.

**Where it appears:** `docs/solver.md` (Component Set — two transformer classes and their parameters; Islands — the boundary-stability discussion), `docs/api.md` §6 (ideal transformer two-port), §7 (`DecouplingTransformer` coupler), `TransformerParams`.

**References:** Horowitz & Hill, *The Art of Electronics*, transformer coverage; any machines text's transformer equivalent circuit (e.g. Fitzgerald, Kingsley & Umans, *Electric Machinery*).

### limit switch
- A switch operated by the physical position of a machine part — for example, a contact that opens or closes when the elevator car arrives at a floor.

In the VS mod's elevator design (design.md, "Elevator control: pure electromechanics") limit switches are ordinary switch components in the same MNA netlist as everything else: the control system is "just more MNA at low voltage" — supply, call buttons, relays, and limit switches wired by the player into a relay-logic controller. The tablet's capstone lesson (curriculum.md, Lesson Arc II.10) has the player combine call buttons, limit switches, and a latching relay into the elevator's brain. For programmers: a limit switch is a hardware sensor input that terminates a motion, the physical analog of a loop's boundary condition or a watchdog that fires on reaching an end state; electrically it is nothing more than a switch whose actuator is mechanical position. This is the standard industrial-controls term (aliases: end switch, position switch, travel switch).
- **Where it appears:** design.md "Elevator control: pure electromechanics"; curriculum.md Lesson Arc II.10 (the elevator controller capstone).
- **References:** standard relay-logic / industrial-controls material, e.g. Petruzella, "Electric Motors and Control Systems"; IEC 60947-5-1 covers position switches as control-circuit devices.

### Line loss
- The power and voltage you lose to the resistance of the wire itself over a long run — in Manatee, deliberately real and player-visible rather than abstracted away.

Every real conductor has resistance, so current flowing through a long cable drops voltage (V = I·R) and dissipates power (P = I²·R) before reaching the load. Because loss scales with the *square* of current, transmitting the same power at higher voltage (hence lower current) cuts loss dramatically — this is the entire reason power grids use high-voltage transmission. Manatee treats this as a teachable gameplay mechanic, not a nuisance: Arc 2 (design.md) has players suffer the "12 V cottage problem" on long runs, then discover transformers and high-voltage distribution as the fix, and use relays as telegraph repeaters where loss kills the signal. For programmers: think of it as unavoidable per-hop cost in a network — you can't remove it, but you can re-encode the payload (higher voltage, lower current) so each hop taxes you less.

Standard term; also called **transmission loss**, **resistive loss**, **I²R loss**, or **copper loss**.
- **Where it appears:** design.md Arc 2 (transformers and distribution, telegraph repeaters); curriculum.md Lesson 14 ("Line loss and why we transmit high-voltage", including the SWER ground-return coda).
- **References:** Horowitz & Hill, *The Art of Electronics*, 3rd ed. (Ohm's law, power dissipation basics).

### Linearize / linearization
- Replacing a nonlinear device with a linear, resistor-like stand-in so the circuit can be solved as one system of linear equations.

MNA solves linear systems: everything in the matrix must behave like resistors and sources. A device whose current is not proportional to its voltage (a diode, or a constant-power load where I = P/V) doesn't fit, so the solver substitutes a linear approximation valid near the device's current operating point, solves, and repeats until the approximation and the answer agree. Manatee does this in two places at two speeds: the core solver linearizes diodes and similar devices *within* a solve via Newton–Raphson (see **linearized companion**), while the Stationeers legacy-device adaptor linearizes constant-power loads *across game ticks* using the previous tick's voltage (see **linearization (across ticks)**). Programming analogy: it's like approximating an arbitrary function by its tangent line at the last known point, then re-deriving the tangent as the point moves — Newton's method, where each iteration's "model" is straight even though the real function is not.

Standard term throughout circuit simulation; the SPICE literature says "linearize" in exactly this sense.
- **Where it appears:** solver.md Analyses (Newton–Raphson); design.md R18; stationeers.md Legacy-Device Adaptor.
- **References:** Nagel (1975), *SPICE2: A Computer Program to Simulate Semiconductor Circuits*, UCB/ERL-M520; Vlach & Singhal, *Computer Methods for Circuit Analysis and Design*.

### Linearized companion
- The small linear circuit — a conductance in parallel with a current source — that stands in for a nonlinear device during one Newton iteration of the solve.

When the solver hits a nonlinear device (e.g. a diode), each Newton–Raphson step replaces it with its tangent-line equivalent at the current voltage guess: a conductance equal to the slope of the device's I–V curve there (g = dI/dV) plus a current source making the line pass through the guessed operating point. That pair *is* the linearized companion. It is re-stamped into the matrix on every iteration (a tier-2 change each time, per solver.md's change-cost tiers) until the dual convergence test — scaled step norm AND residual — passes. For programmers: it's the per-iteration snapshot of Newton's method rendered as circuit parts, the same way each step of root-finding uses a fresh tangent line. Note the sibling term **companion model** in this project usually refers to the *time-discretization* equivalent of capacitors/inductors; "linearized companion" is specifically the *Newton* equivalent of a nonlinear device — same stamp shape, different source of the approximation.

Standard concept; the literature calls this the **companion model** (or linearized equivalent / Norton equivalent) of a nonlinear element under Newton–Raphson.
- **Where it appears:** solver.md Analyses ("Newton-Raphson, re-stamping linearized companions per iteration").
- **References:** Nagel (1975), SPICE2 memo UCB/ERL-M520; Pillage, Rohrer & Visweswariah, *Electronic Circuit and System Simulation Methods*; Vlach & Singhal, *Computer Methods for Circuit Analysis and Design*.

### Link (gang)
- A parameter on Falstad's SPDT switch element (`S`) that ties multiple switches together so flipping one flips them all; Manatee's importer rejects any circuit that uses it.

In the Falstad/circuitjs1 text format, an `S` line carries a `link` integer (Switch2Elm.java:46-49): all switches sharing the same nonzero `link` value operate as one — physically, a multi-pole switch where one handle throws several contacts at once (an EE would recognize a DPDT switch built from two linked SPDTs). Manatee's importer accept/reject contract (falstad-format.md §7) rejects `link != 0` loudly, along with `throwCount > 2` and the center-off flag, because ganged switch groups add cross-element coupling the lesson corpus doesn't need. For programmers: `link` is a group ID creating shared mutable state across otherwise-independent elements — the importer refues the whole file rather than silently importing the switches as independent, which would change the circuit's behavior.

**Standard term:** ganged switch / multi-pole switch (e.g. DPDT); `link` is Falstad's file-format name for the gang group ID.
- **Where it appears:** falstad-format.md §4 (`S` element table) and §7 (importer accept/reject contract).

### maxVoltage (source amplitude)
The Falstad file-format parameter that holds a voltage source's output level — and, for AC sources, it is the **peak** of the sine wave, not the RMS value, despite what the name might suggest to some readers.

In a `v`/`R` source line the output is `maxVoltage + bias` for DC and `maxVoltage·sin(2πft + phaseShift) + bias` for AC (per circuitjs source, `VoltageElm.java:152-170`). So an AC source written with `maxVoltage = 170` produces a 170 V-peak sine, which an electrician would call "120 V" (RMS). This matters because household voltages are conventionally quoted as RMS — the DC-equivalent heating value, √2 lower than the peak for a sine. For programmers: RMS vs. peak is like average vs. maximum load on a server — both describe the same waveform, but you must know which convention a config field uses before comparing numbers. Manatee's importer takes the field at circuitjs's word (peak), and the project's own lesson corpus puts DC values in `maxVoltage` (not `bias`).

**Standard term:** peak amplitude (V<sub>peak</sub>), not V<sub>RMS</sub>. The Falstad name is a mild misnomer we inherit unchanged from the file format.

**Where it appears:** `docs/falstad-format.md` §4 (voltage-source semantics, with `VoltageElm.java` line references) and the §7 corpus rule; a separate, unrelated `maxVoltage` also appears on current sources as a compliance limit, which Manatee rejects if nonzero.

**References:** Horowitz & Hill, *The Art of Electronics* (RMS vs. peak amplitude conventions); Falstad/circuitjs1 source (`VoltageElm.java`).

### Melting integral
- A per-conductor accumulator that tracks overload heating over time and decides when a segment melts — the state behind fuses and slow cable-overload failure.

Each thermal envelope pair `(rating, melt, tau)` gets its own accumulator: when current exceeds the rating it heats by `(I² − rating²)·dt`; below the rating it cools exponentially (`acc·dt/τ`); crossing the pair's melt threshold trips a limit event. For the programmer: it is a leaky-bucket rate limiter — brief bursts over the limit are tolerated, sustained overload fills the bucket, and quiet periods drain it. For the EE: this is the familiar I²t (Joule-integral) fuse model with a cooling time constant added, evaluated per substep on subcycled AC islands so it integrates the true waveform (∫I²dt over the cycle) rather than sampling tick boundaries. Crucially the integral is *state that persists*: it rides the per-island snapshot and the canonical memento, so a partway-melted fuse saves and reloads partway-melted instead of resetting cold. The solver never melts anything itself (R7) — it raises the event; the client removes the segment.
- **Standard term:** I²t (Joule integral) thermal accumulation, as in fuse let-through ratings; "melting integral" is our name for the persisted accumulator.
- **Where it appears:** integration-tutorial.md §7 (save/load lists melting integrals as saved state) and §8 (the fuse-melt example); api.md §12 (semantics) and §14 (snapshot/memento persistence); compaction.md Responsibilities #4 (Pareto envelopes).
- **References:** IEC 60269 / fuse-datasheet I²t practice; Horowitz & Hill, "The Art of Electronics" (3rd ed., Cambridge, 2015) on fuse ratings.

### Melting threshold
- The accumulated-heat level at which a conductor segment actually melts — the trip point that the melting integral is compared against.

It matters as a *distinct limit type* in the reduction layer: when many physical segments are collapsed into one equivalent resistor, that resistor carries a limit envelope holding the per-limit-type minimum over its constituents, and instantaneous ampacity, i²t thermal mass, and melting threshold can each pick a **different** weakest segment in a mixed-material chain (a lead segment in a copper run is a fuse precisely because its melt point is lowest, even if its momentary ampacity is not). For the programmer: think of it as one field in a multi-key minimum — you cannot reduce the chain to a single "weakest link" because "weakest" differs per failure mode, which is why the i²t envelope is a Pareto-minimal *set* of (rating, melt) pairs rather than one hybrid pair (a hybrid of X's rating and Y's melt would trip when no real segment would). Thresholds are environment-adjusted per segment (ambient temperature from −150 °C to +800 °C across Stationeers worlds); an ambient change is a metadata-only recompute, never a matrix change.
- **Where it appears:** compaction.md Responsibilities #4 (limit attribution, Pareto envelopes); api.md §12 (the `melt` field of `(rating, melt, tau)` pairs); design docs' hazard/limit discussions.

### Meters
- Readout widgets in the schematic tablet/harness that display instantaneous solved values — a probe's voltage or a branch's current — driven by reading back the solver's latest solution.

In our layering, meters live in the document model's binding to manatee-core: after each solve, the document reads the solution vector back ("solution readback") and pushes the numbers at each probe placement into meter displays, the way a dashboard widget polls a metrics store rather than computing anything itself. Meters show a single instantaneous number; their time-history sibling is the scope trace (oscilloscope-style plot). For the EE: these are the in-game multimeter/ammeter readouts. For the programmer: meters are pure views over solver output — they never mutate the circuit, and because the document layer is UI-framework-free, they are testable headlessly. The curriculum's appendix on "reading the multimeter/oscilloscope" teaches players to use them.

**Where it appears:** `docs/harness.md` (Layering #1 — solution readback for meters and scope traces; Desktop Shell — probe readouts), `docs/curriculum.md` (Appendices).

### Milliamp signalling
- Uses of electricity that carry information rather than power — telegraph, doorbell, electric fence — drawing only thousandths of an amp, which makes them the one low-voltage niche where single-wire earth return (SWER) actually works.

The physics: our grounding model gives every earth electrode a contact resistance of tens of ohms. A power load (say a 12 V lamp drawing amps) loses almost all its voltage across those electrodes and starves; a signalling load drawing milliamps drops only millivolts across the same electrodes, so the losses are negligible. Programming analogy: it's the difference between a control-plane heartbeat and a bulk data transfer over the same lossy link — the heartbeat survives bandwidth (here, resistance) that kills the transfer. Curriculum Lesson 14's ground-return coda uses this arithmetic to answer "when can you get away with one wire?": SWER works for 240 V distribution and milliamp signalling, and fails for 12 V loads. Historically accurate — real telegraphs and fence energizers used earth return.

Standard descriptive phrase, not project jargon; the EE literature would file it under signalling circuits / earth-return telegraphy (and, separately, industrial 4–20 mA current loops share the same "milliamps tolerate loop resistance" logic).

**Where it appears:** `docs/curriculum.md` (Lesson Arc III.14), `docs/design.md` (Grounding model — SWER as user-visible choice).

### MNA (Modified Nodal Analysis)
- The circuit-solving method at the heart of manatee-core: it turns a circuit into a system of linear equations built from Kirchhoff's laws, whose solution is every node's voltage plus the currents through certain branches.
- Plain nodal analysis writes one equation per node ("current in = current out", Kirchhoff's current law) with node voltages as the unknowns; the *modification* adds extra unknowns and rows for elements nodal analysis can't express directly — ideal voltage sources, inductors, and anything whose current you need explicitly. Each component contributes a small fixed pattern of entries (a "stamp") to a sparse matrix, and solving `Ax = b` yields the whole circuit's state at once. For the programmer: think of MNA as a compiler target — devices lower themselves into matrix stamps, and one sparse linear solve replaces any ad-hoc power-propagation logic. That uniformity is why the design docs say relay logic is "just more MNA at low voltage": control circuits aren't a separate rules engine, they're the same matrix with more rows. Nonlinear devices and time-dependent ones (capacitors, inductors) fit via iteration and companion models, but the core loop is always: stamp, factor, solve.
- Standard term; this is the formulation used by SPICE and essentially every modern circuit simulator. Also seen as "modified nodal approach".
- **Where it appears:** everywhere — `docs/design.md` (Purpose, R1), `docs/solver.md` (layering, analyses), and by reference in every other doc; manatee-core *is* an MNA engine.
- **References:** Ho, Ruehli & Brennan (1975), "The Modified Nodal Approach to Network Analysis", IEEE Trans. Circuits and Systems; Nagel (1975), SPICE2 memo UCB/ERL-M520; Vlach & Singhal, *Computer Methods for Circuit Analysis and Design*; Pillage, Rohrer & Visweswariah, *Electronic Circuit and System Simulation Methods*.

### momentary switch
- A switch that only stays in its pressed position while you hold it, springing back to its rest position when released — like a doorbell button rather than a light switch.

In circuitjs1/Falstad files, the SPST switch element (`s`) carries a `momentary` boolean alongside its position (where, counterintuitively, position 0 = closed, 1 = open). In a live Falstad session, a momentary switch closes on mouse-down and reopens on mouse-up. Manatee's importer, however, has no interactive mouse semantics to attach that behavior to, so per the accept/reject contract it **accepts the flag but treats the switch as a normal toggle and emits a warning** — the imported circuit works, but the spring-return behavior is dropped. For a programmer: think of it as a key-down/key-up event pair being flattened into a persistent boolean, with a lint warning that the edge-triggered semantics were lost.

This is a standard term; hardware catalogs also call it *push-to-make* (or push-to-break for the normally-closed variant), versus a *latching* or *toggle* switch.

**Where it appears:** `docs/falstad-format.md` §4 (the `s` element's `momentary` field, `SwitchElm.java:52-66`) and §7 (importer accept list: "accept `momentary` but treat as a normal toggle, warn").

**References:** Horowitz & Hill, *The Art of Electronics* (3rd ed.), for switch taxonomy (SPST, momentary vs. latching).

### Nameplate rating
- The manufacturer's stated operating specification for a device — the voltage, current, power, and (for AC) frequency it is designed to run at, traditionally printed on a metal plate on the device.

In Manatee's game designs, manufactured devices carry real nameplate ratings, and matching them is explicitly the player's job: in Vintage Story via wheel gearing, alternator pole count, and transformers (against the 12/48/240 V tiers and the ~5 Hz "natural" alternator frequency); in Stationeers/Re-Volt via device ratings and transformer steps (tier values are Re-Volt's call). Devices tolerate roughly ±25% deviation with efficiency and lifetime penalties toward the edges. For a programmer: a nameplate rating is a device's declared interface contract — the range of inputs it accepts — and running off-nameplate is calling an API out of contract: it may still respond, but degraded, and persistently doing so wears it out. It is a rating, not a measurement: what the device *wants*, not what the circuit is currently delivering.

This is the standard power-engineering term (aliases: "rated voltage/current/power", "ratings").

**Where it appears:** `docs/design.md` (R15 and "Voltage and frequency standards"); `docs/stationeers.md` (Voltage Tiers); context for instrument readouts.

**References:** Horowitz & Hill, *The Art of Electronics* (component ratings); IEC 60034-1 (rating plates for rotating machines) as the real-world convention.

### Negative differential resistance
A property of some devices (in our game, arc lamps) where drawing *more* current makes the voltage across the device go *down*, the opposite of a resistor.

Ordinary resistance is a positive slope: push more current, see more voltage (V = IR). In a device with negative differential resistance the local slope dV/dI is negative over part of its operating curve — an electric arc conducts better the hotter it gets, so as current rises its voltage drops. Connected straight to a stiff supply this is an unstable feedback loop: more current → lower device voltage → even more current, a runaway. The classic fix is a series *ballast* resistor whose positive slope dominates, giving the combination a single stable operating point. For a programmer: it is a system whose feedback gain is locally positive — unstable on its own — that you stabilize by composing it with a damping element; think of a retry loop that speeds up under failure until you wrap it in backoff. In Manatee this is deliberately a *lesson*: Arc 2 arc lamps require a series ballast, and the phenomenon is taught rather than hidden.

This is the standard EE term (often abbreviated NDR); the same behavior appears in tunnel diodes, neon lamps, and gas-discharge tubes.

**Where it appears:** `docs/design.md` Arc 2 (arc lamps, curriculum progression).

**References:** Horowitz & Hill, *The Art of Electronics* (negative resistance, tunnel diodes); Ayrton, *The Electric Arc* (1902) is the classic study of the arc's falling V–I characteristic.

### netlist
- A description of a circuit as a list of components and which nodes each one connects to — the input a circuit solver works from, as opposed to a picture of the schematic.

In the wider world a netlist is usually a text file (SPICE decks are the canonical example: one line per component, naming its nodes and value). Manatee uses the word in three related senses. (1) Generically, any such circuit description. (2) The `Netlist` class in manatee-core: a live, mutable, in-memory version of that list — a typed graph of component handles with change tracking, islands, and the four mutation verbs; it is the only API surface game clients talk to (they never see a matrix). (3) The lesson corpus: tablet lessons are stored as Falstad-format text netlists (R20), imported by the harness and solved in CI against ngspice. For a programmer: think of it as the circuit's source-of-truth data model, from which the solver's matrices are compiled on demand.

The term is standard (SPICE and EDA usage); our in-memory `Netlist` object is an extension of it — see "Netlist (as document)".

**Where it appears:** design.md R2/R20 and System Overview; solver.md "The Netlist API"; docs/api.md throughout; testing-strategy.md (oracle tests, lesson corpus); harness.md (extraction); curriculum.md (lesson format).

**References:** Nagel (1975), *SPICE2: A Computer Program to Simulate Semiconductor Circuits*, UCB/ERL-M520; the ngspice manual (netlist syntax).

### netlist extraction
- Converting the drawn schematic — the tablet's document of components, wires, and junctions — into a manatee-core `Netlist` that can actually be solved.

The schematic document model stores what the user sees: placed components, wire segments, probe placements. The solver needs something different: electrical nodes (sets of points connected by wire) and components attached between them. Extraction is the translation step: trace wire connectivity, collapse each connected wire run into one node, and issue the corresponding `Add*` calls to manatee-core. It also sets up the reverse binding — solution readback for meters and scope traces. For a programmer, it is a compilation pass from the presentation model to the solver's model; for an EE, it is the step schematic-capture tools perform when they generate a SPICE netlist from a drawing (usually called "netlist generation").

A terminology caveat: in strict EDA usage, "extraction" more often means recovering a netlist from physical *layout* (netlist/parasitic extraction, as in LVS flows), while the schematic-to-netlist step is called netlist generation or schematic-capture output. Manatee uses "extraction" in the looser schematic-to-netlist sense.

**Where it appears:** harness.md, Layering #1 (document model's binding to manatee-core) and Testing Model (lesson-corpus scenarios exercise extraction, solving, and probe readback end to end).

### node
- A connection point in the circuit where component terminals meet; the solver's job is to compute one voltage per node.

In MNA, node voltages are the primary unknowns of the linear system: every wire junction becomes a node, and each component "stamps" its contribution into the matrix rows/columns of the nodes it touches. For programmers: a node is a vertex in the circuit graph, and the solution vector is a map from node → voltage — components are edges annotated with physics. In Manatee, earth/ground is not special-cased: it is an ordinary solver node (each island designates one node as its reference datum — the zero-volt point every other node voltage is measured against, whose row and column are eliminated from the MNA matrix rather than solved for), and in Vintage Story the literal earth is reached through ordinary contact resistors. Nodes are created via `AddNode` (with an optional `NodeRole`), identified by `NodeId`, and read back with `VoltageAt(NodeId)`. The deepest solver layers deliberately avoid electrical-only naming for what a node carries (see *node potential / flow*) so the same machinery can later solve thermal networks.

This is the standard EE term; "net" is a common alias in schematic-capture tools (Falstad's format leaves nodes implicit — connectivity is geometric, with element posts sharing a coordinate being the same node — which our importer resolves into solver nodes).

- **Where it appears:** throughout `docs/solver.md` (netlist API, Grounding, Islands); `docs/stationeers.md` (Graph Construction, Integration Seams); `docs/falstad-format.md` §4 (the `O` output element reads a node voltage) and §7; `docs/api.md` §6 (`AddNode`).
- **References:** Ho, Ruehli & Brennan (1975), "The Modified Nodal Approach to Network Analysis", IEEE Trans. Circuits and Systems; Vlach & Singhal, *Computer Methods for Circuit Analysis and Design*.

### node voltage / branch current
- The two kinds of solved quantities the simulator produces — the voltage at each connection point, and the current through each component — and the values our oracle tests check against ngspice.

A node voltage is the potential at a circuit node relative to the island's reference (ground); a branch current is the current flowing through one component (one edge of the circuit graph). Together they are the complete observable output of an MNA solve: node voltages are the primary unknowns, and MNA adds branch currents as extra unknowns for components (like ideal voltage sources) whose current cannot be derived from voltages alone. For programmers: think of them as the two fields of the solver's result record — per-vertex values and per-edge values. In our testing strategy, both are diffed against ngspice running the same netlist, within stated tolerances (DC: 0.1% relative or 1 µV absolute near zero; transient: matched timesteps with looser tolerance because we use Backward Euler while ngspice defaults differ). Passing this diff is what "the math is right" means in this project — correctness is established by oracle agreement, never by inspection.

Both terms are standard EE/SPICE vocabulary.

- **Where it appears:** `docs/testing-strategy.md` (Oracle Tests: ngspice comparison and tolerances); read back via `VoltageAt(NodeId)` / `CurrentThrough(id)` in the API.
- **References:** Nagel (1975), *SPICE2: A Computer Program to Simulate Semiconductor Circuits*, UCB/ERL memo M520; Ho, Ruehli & Brennan (1975), "The Modified Nodal Approach to Network Analysis", IEEE Trans. Circuits and Systems; the ngspice manual.

### Nominal voltage
- The designated reference voltage of a voltage tier — the voltage a device or cable class is *supposed* to run at, as opposed to the voltage the solver actually computes at any instant.

In the Stationeers integration, cables and devices belong to player-facing voltage tiers (e.g. a 120 V tier vs. a 480 V tier, exact values being Re-Volt's call). Each tier has one nominal voltage, and the Legacy-Device Adaptor uses it as the stamping value for unconverted generators: a legacy generator is stamped as a voltage source *at its tier's nominal voltage*, with a current limit derived from the power it advertises. The actual solved voltage will sag below nominal under load (through the source's small internal series resistance) — that difference is the physics the mod exists to expose. Programming analogy: nominal voltage is like a declared API contract or a configured default, while solved voltage is the runtime value; devices are rated against the contract, but behavior follows the runtime value.

This is the standard EE usage — "nominal" (or "rated") voltage as found on equipment nameplates, distinct from the actual operating voltage.

**Where it appears:** `docs/stationeers.md` — Legacy-Device Adaptor (generator stamping) and Voltage Tiers.

**References:** Horowitz & Hill, *The Art of Electronics* (nameplate/rated vs. operating values is standard practice terminology).

### Nonlinear
- Describes a circuit element whose current is not simply proportional to the voltage across it, so it cannot be captured by a single fixed conductance in the matrix.

A plain resistor is linear: I = G·V, one number, stamp it once, solve directly. A nonlinear element — a diode, or Stationeers' constant-power legacy devices where I = P/V — has an I–V relationship that curves, so the solver must linearize around a guessed operating point and iterate (Newton-Raphson, re-stamping the linearized companion each iteration) until the guess is self-consistent. Programming analogy: linear elements are data you can resolve in one pass; nonlinear elements introduce a circular dependency (the element's effective conductance depends on the answer), forcing a fixed-point iteration. Our Legacy-Device Adaptor sidesteps in-solve iteration for legacy loads by linearizing *across game ticks* instead: stamp G = P_wanted / V_prev² using last tick's voltage, converging over a few ticks. Policy: nonlinear devices are legal everywhere but discouraged in world AC islands (see nonlinearity budget).

Standard EE/mathematics term.

**Where it appears:** `docs/stationeers.md` — Legacy-Device Adaptor (constant-power loads); `docs/solver.md` — Analyses, "Nonlinear elements".

**References:** Nagel (1975), SPICE2 memo UCB/ERL-M520; Vlach & Singhal, *Computer Methods for Circuit Analysis and Design* (Newton-Raphson treatment of nonlinear networks).

### Norton / Thevenin equivalent (implied)
- The classical theorem that any linear one-port — however complicated inside — behaves identically, as seen from two terminals, to a single ideal voltage source in series with a resistance (Thévenin) or an ideal current source in parallel with a conductance (Norton), the two forms interconvertible by source transformation — the pattern our companion models and legacy adaptor quietly rely on.

Manatee never names these in the docs, but they underlie two load-bearing mechanisms. First, Backward-Euler **companion models** (R2): a capacitor at each timestep is stamped as a conductance G = C/dt in parallel with a history current source I = G·V_prev — that pair *is* a Norton equivalent of the capacitor's behavior over the step (the inductor is the mirror case), which is what lets a dynamic element be stamped into the same linear matrix as resistors. Second, the Re-Volt **legacy-device adaptor** (R18): an adapted generator stamped as a voltage source at nominal voltage behind a small internal series resistance is exactly a Thévenin form (and that series resistance is what makes paralleled generators well-posed instead of a singular contradiction), while an unconverted constant-power load is linearized per tick into an effective conductance G = P/V_prev² — a source-plus-conductance stand-in. Programming analogy: both forms are interchangeable canonical representations of the same interface behavior, like two isomorphic encodings of one data structure — the solver picks whichever stamps cleanly (Norton forms stamp directly as conductance + RHS current entries), and the stub's parameters are refreshed each tick/iteration like re-deriving a cached view.

Standard terms (Thévenin's and Norton's theorems); the timestep application is the standard SPICE "companion model" / "associated discrete circuit model".
- **Where it appears:** implicit throughout `docs/design.md` R2 and R18; `docs/solver.md` Analyses (Transient: BE companion models; Nonlinear elements: linearized companions per Newton iteration) and Change-Cost Tiers (history terms as tier-1 RHS updates); `docs/stationeers.md` Legacy-Device Adaptor (source + internal series resistance).
- **References:** Vlach & Singhal, *Computer Methods for Circuit Analysis and Design* (companion models and equivalents); Pillage, Rohrer & Visweswariah, *Electronic Circuit and System Simulation Methods*; Horowitz & Hill, *The Art of Electronics* (Thévenin/Norton at practitioner level); Nagel (1975), SPICE2 memo UCB/ERL-M520; Ho, Ruehli & Brennan (1975), "The Modified Nodal Approach to Network Analysis", IEEE Trans. Circuits and Systems.

### operating point / SolveOperatingPoint
- The DC steady state a circuit settles into if you hold its sources constant and wait forever; `SolveOperatingPoint()` is the manatee-core call that computes it in one shot.

Instead of stepping the circuit through time, an operating-point solve asks "where does everything end up?": capacitors are treated as fully charged (open circuits, no current flows through them), and inductors as fully settled (near-perfect wires — we stamp them as ~1 mΩ resistors rather than ideal shorts, to keep the matrix well-behaved). In programming terms it is computing the fixed point of the system directly, rather than iterating a simulation loop until it stops changing — like initializing a cache to its converged contents instead of warming it. Manatee uses it in exactly two roles: energizing a circuit when it first comes alive (game load, island rebuild) and giving tablet lessons a sensible starting state, before per-tick transient/AC stepping (`Solve`) takes over. This is the same analysis SPICE calls `.op`, and every transient simulation classically begins with one.

"Operating point" is the standard term (aliases: DC operating point, bias point, quiescent point / Q-point).
- **Where it appears:** `docs/api.md` §4 (the `Netlist.SolveOperatingPoint()` verb) and the §22 consumer walkthroughs, where both the Re-Volt (§22.a) and VS AC-island (§22.b) examples call it once after construction.
- **References:** Nagel (1975), *SPICE2: A Computer Program to Simulate Semiconductor Circuits*, UCB/ERL memo M520 (the `.op` analysis); ngspice manual, DC operating-point analysis; Vlach & Singhal, *Computer Methods for Circuit Analysis and Design*.

### P/V exchange
- The per-tick handoff of power (P) and voltage (V) values across a transformer boundary between two solver islands, so each island can be solved as an independent matrix.

Manatee treats a transformer in Stationeers as a power-transfer boundary rather than a direct wire: the circuits on its two sides stay separate islands (separate matrices), and the transformer device couples them by exchanging scalar values each tick — how much power one side is delivering, at what voltage — instead of appearing as entries in a single shared matrix. For a programmer, this is message passing between two otherwise independent state machines, versus a closed breaker which is shared state (one merged matrix). The exchanged values are one step stale by construction, so the scheme is hardened per `docs/solver.md`: coupled islands are scheduled as one work unit and substep in lockstep, exchanged values are damped by exponential smoothing (relaxation factor α per device type), transfer is clamped to what the source island actually delivered (no free energy), and a running energy ledger dumps any accounting surplus into the device's heat output. For DC Stationeers the exchange is literally P and V; AC boundaries (Vintage Story) exchange amplitude+phase per substep instead — "P/V exchange" is the stationeers.md shorthand for the DC case of this general boundary-coupling scheme.

**Project-coined term**, closest standard concepts: waveform-relaxation / co-simulation partitioning between subcircuits, and the "PV boundary" idea from power-flow analysis (specified power and voltage at a bus) — though our usage is a tick-by-tick dynamic handoff, not a steady-state power-flow constraint.

**Where it appears:** `docs/stationeers.md`, Islands and Coupling Devices; the general exchange scheme (AC amplitude+phase variant, relaxation, energy ledger, lockstep scheduling) in `docs/solver.md`, coupling devices.

**References:** waveform relaxation for partitioned circuit simulation is surveyed in Pillage, Rohrer & Visweswariah, "Electronic Circuit and System Simulation Methods" (McGraw-Hill, 1995).

### parallel branch currents
- The topic of tablet Lesson 2: when several components sit side-by-side between the same two nodes, the total current splits among them, and the branch with the lower resistance carries the larger share.

Components "in parallel" all see the same voltage (they connect the same pair of nodes), so each branch draws current independently per Ohm's law: I = V/R, meaning half the resistance draws twice the current. This is the current-divider counterpart to Lesson 1's series voltage divider — series shares one current and splits the voltage; parallel shares one voltage and splits the current. For programmers: it behaves like load balancing across servers with different capacities — total demand is fixed by the source, and each path takes traffic in inverse proportion to how much it resists. This lesson also seeds the later hazard intuition: adding parallel loads *lowers* total resistance and *raises* total current, which is why plugging too much into one supply overloads it.

**Standard term:** the standard concept is the **current divider** (Kirchhoff's current law applied to parallel resistances); our phrase just names the lesson topic.

**Where it appears:** `docs/curriculum.md`, Lesson Arc I.2 (DC foundations, mirroring Electrical Age's proven teaching order).

**References:** Horowitz & Hill, *The Art of Electronics* (voltage/current dividers, ch. 1).

### Phase
- The timing offset between two AC waveforms of the same frequency — in our curriculum, specifically the shift between voltage and current that capacitors and inductors introduce.
- In a purely resistive AC circuit, current and voltage peak at the same instant. Add a capacitor or inductor (reactance) and the current waveform slides earlier or later relative to the voltage: they are "out of phase." Phase is measured as a fraction of a cycle, conventionally in degrees or radians (90° = a quarter cycle). For a programmer: two periodic signals with the same period but different start offsets, like two cron jobs on the same interval firing at different minutes — phase is the offset between them. Lesson 15 teaches this by showing two oscilloscope traces side by side (the two-probe contract exists partly for this) and deliberately avoids the word "phasor" — the complex-number formalism EEs use to calculate with phase — because the lesson only needs the observable timing shift, not the algebra.
- **Standard term** — universal EE usage; the formalism we deliberately omit is *phasor analysis*.
- **Where it appears:** `docs/curriculum.md` Lesson Arc III, lesson 15 (and lesson 16 power factor, lesson 17 synchronization build on it).
- **References:** Horowitz & Hill, *The Art of Electronics*, 3rd ed., ch. 1 (AC signals, reactance and phase).

### Phase continuity / phase-continuous
- The guarantee that a sine source's waveform advances smoothly across ticks and substeps — no jumps or glitches in the output — even though its frequency input arrives as a step-changing (piecewise-constant) value.
- The source driver keeps a running phase accumulator: each substep it adds `2π·f·dt` to the stored phase and evaluates the sine there, rather than recomputing `sin(2π·f·t)` from absolute time. When the mechanical network updates the frequency (at its ~100 ms cadence), the *rate* of phase advance changes but the accumulated phase does not — so the waveform bends instead of jumping. For a programmer this is the classic "integrate the rate, don't sample the formula" pattern: like a monotonically increasing counter whose increment can change, versus recomputing position from `speed × wall-clock time`, which teleports on every speed change. For an EE it is exactly how a real alternator behaves — the rotor angle is physical state and cannot discontinue; a phase jump would be an artificial transient injecting spurious energy into the circuit. `TickClock` exists partly to make this deterministic across save/restore.
- **Standard term** — the same property required of direct digital synthesis (DDS) and FM synthesis; sometimes called *continuous-phase* frequency modulation.
- **Where it appears:** `docs/solver.md` Subcycled AC (source driver); `docs/api.md` §4 (`SineDrive`, `Drive` "phase-continuous source driver", `TickClock`) and §22.b walkthrough.

### phase-continuous
- The property that a sinusoidal source's phase angle carries over smoothly across game-tick and substep boundaries instead of resetting to zero each tick.

Each AC source keeps its accumulated phase as persistent state; a new tick resumes the sine wave exactly where the last one left off, and a frequency change (a piecewise-constant input from the mechanical network) alters the future *slope* of the phase, never causing a jump in the waveform value. Without this, every tick boundary would inject a step discontinuity — spurious transients, flicker artifacts, and non-physical energy — much like an audio synthesizer clicking when an oscillator restarts. In systems terms: the source driver is a tiny state machine whose invariant is "the output waveform is C0-continuous in time"; frequency is an input, phase is the state, and ticks are just batching boundaries invisible to the physics. This is why source phase is part of saved/restored solver state (it appears alongside inductor currents in the versioned state the docs enumerate), and why `TickClock` exists in the API — determinism plus AC phase continuity.

**Standard term:** standard usage — same sense as "phase-continuous" in frequency-shift keying (FSK) and signal generation: frequency changes without phase discontinuities.

**Where it appears:** `docs/solver.md` § Analyses (subcycled-AC source driver) and § Islands (source phase in exchanged/persisted state); `docs/api.md` (`Drive` is documented as the "phase-continuous source driver"; `TickClock`, `SineDrive.PhaseRad`).

### phase increment
- The fixed amount a sinusoidal source's phase angle advances on each solver substep — how the source driver hits the *exact* requested frequency even though the timestep dt is held constant.

In subcycled AC, an island's substep count N (and therefore dt) is quantized with hysteresis so that Backward Euler companion conductances stay constant and the island can live in tier 1 (cheap, right-hand-side-only updates). That means dt is deliberately *not* retuned to the generator's momentary speed. Instead, the source driver accumulates phase: each substep it adds Δφ = 2π·f·dt to a running phase and evaluates `amplitude·sin(phase)`. Adjusting Δφ is a value-only change (tier 1), so frequency can wobble with the mechanical network at zero refactorization cost. In programming terms: the frequency is data fed to an accumulator, not a parameter baked into the system matrix — like a software oscillator or DDS (direct digital synthesis) counter whose step size, not its clock, encodes the pitch.

This is the standard technique of a numerically-controlled oscillator's phase accumulator; "phase increment" is the usual name for the per-step delta in DDS literature.

**Where it appears:** `docs/solver.md` § Analyses (Subcycled AC — "the source driver tracks the *exact* frequency through its per-substep phase increment (tier 1)"); realized by `Drive(VSourceId, in SineDrive)` in `docs/api.md`.

### phaseShift
- The phase-offset field of a Falstad-format voltage source: a constant angle, stored **in radians**, added inside the sine when the source is evaluated.

In the circuitjs1 text format our importer accepts, a `v` element's parameter list is `waveform freq maxVoltage bias phaseShift [dutyCycle]`, and an AC source outputs `maxVoltage·sin(2πft + phaseShift) + bias`. Two traps the spec calls out: `maxVoltage` is the peak amplitude, not RMS, and `phaseShift` is radians in the file even though Falstad's UI displays degrees — a classic serialization-vs-presentation mismatch, like a config file storing seconds while the dialog shows minutes. There is also a legacy flag (`flags&2`, FLAG_COS) that older files used to mean "cosine"; the loader rewrites it to a phaseShift of π/2 so only one representation survives parsing.

**Standard term:** the EE concept is simply the **phase angle / initial phase (φ₀)** of a sinusoid; `phaseShift` is Falstad/circuitjs1's field name (`VoltageElm.java`), which we adopt when discussing the import format.

**Where it appears:** `docs/falstad-format.md` §4, voltage-source semantics (with `VoltageElm.java` line references); maps onto `SineDrive.PhaseRad` in `docs/api.md`.

### phasor
- A complex number that represents a sinusoid's amplitude and phase in one value — standard EE shorthand for AC analysis, and a word the tablet curriculum **deliberately never uses**.

In conventional AC theory, a sinusoid `A·sin(ωt + φ)` at a known frequency is compressed into the complex number `A∠φ`, turning calculus on waveforms into algebra on complex numbers. It is a lossy-but-sufficient encoding for steady state — analogous to storing a periodic signal as (magnitude, offset) metadata instead of samples, valid only while the frequency is fixed and the system linear. Manatee's curriculum teaches the underlying phenomenon concretely instead: Lesson 15 covers capacitors and inductors on AC as *phase* — current leading or lagging voltage, visible on the in-game oscilloscope — "without the word 'phasor'". The abstraction is skipped, not the physics; the solver likewise simulates real waveforms rather than phasors (see the separate entry on phasor analysis).

**Standard term:** entirely standard EE vocabulary (also "complex amplitude"); our only nonstandard move is intentionally omitting the word from player-facing lessons.

**Where it appears:** `docs/curriculum.md`, Lesson Arc III, Lesson 15 (listed as an avoided term).

**References:** Horowitz & Hill, *The Art of Electronics*; C. P. Steinmetz originated the complex representation of AC quantities (1890s); any circuits text, e.g. Vlach & Singhal, *Computer Methods for Circuit Analysis and Design*.

### phasor (complex) analysis
- Frequency-domain AC analysis: solve the circuit once per frequency using complex-valued amplitudes instead of stepping real waveforms through time — explicitly a **non-goal as Manatee's primary AC mode** (design.md Non-Goals, R3), permitted later only as a metering optimization.

The technique replaces every sinusoidal quantity with a complex phasor and every reactive component with a complex impedance, so one complex linear solve yields the steady-state amplitudes and phases at a given frequency — this is SPICE's `.AC` small-signal analysis. It is far cheaper than transient simulation, but it assumes linear steady state and produces no actual waveform. Manatee's R3 ("Waveform-first AC") rejects it as the primary mode because the gameplay thesis needs real time-domain waveforms: 5 Hz waterwheel lamp flicker is deliberately instructive, and the oscilloscope item must show genuine samples. So AC islands run subcycled time-domain steps (cached LU factorization, RHS-only substeps), and components provide time-domain stamps only; a complex-frequency stamp is deferred until phasor *metering* (e.g. cheap RMS/power-factor readouts) is wanted (`solver.md` § Analyses, Numerics). Analogy: it is precomputing a closed-form summary versus running the simulation — the summary is fast but can't show you the animation.

**Standard term:** **AC (small-signal) analysis / frequency-domain phasor analysis** — SPICE's `.AC`.

**Where it appears:** `docs/design.md` R3 and Non-Goals ("Frequency-domain-only AC"); `docs/solver.md` § Analyses and Numerics (deferred complex solve path).

**References:** Nagel (1975), *SPICE2: A Computer Program to Simulate Semiconductor Circuits*, UCB/ERL-M520 (AC small-signal analysis); Vlach & Singhal, *Computer Methods for Circuit Analysis and Design*; the ngspice manual (§ AC analysis).

### piecewise-constant (input)
- An input value that holds steady for a stretch of time and then jumps to a new value, rather than varying smoothly — a staircase, not a ramp.

In Manatee the canonical piecewise-constant input is AC source frequency (equivalently, shaft speed) fed from Vintage Story's mechanical network: the mechanical sim re-sums torque only every ~100 ms, so between those updates the electrical solver sees frequency as a fixed constant, then a step to the next value. For a programmer this is exactly a cached value refreshed on a slower cadence — the fast loop reads a snapshot, and staleness within one refresh interval is accepted by design. For an EE it is the standard zero-order-hold picture from sampled-data systems: a slowly sampled signal reconstructed as flat segments. The payoff is numerical: because the value is constant across each electrical tick, the source driver only advances phase per substep (a tier-1, right-hand-side-only cost), and step size dt — hence every Backward Euler companion conductance — stays fixed, keeping steady AC operation on the cheap cached-factorization path.
- Standard term; in control/DSP literature the reconstruction is called a **zero-order hold (ZOH)**, and "piecewise-constant" is the usual mathematical description.
- **Where it appears:** solver.md Analyses (subcycled AC source driver); design.md Simulation Model (mechanical co-simulation); api.md §22.
- **References:** Franklin, Powell & Workman, *Digital Control of Dynamic Systems* (zero-order hold, sampled-data systems).

### Polarity / sign convention
- The agreed-upon rule for which terminal of a part counts as "+" and which direction of current counts as positive, so that every number in the system means the same thing everywhere.

In the Falstad import format, a two-terminal element is defined by two "posts" (endpoints) at `(x1,y1)` and `(x2,y2)`. Our convention, read directly from the circuitjs1 source: post 2 `(x2,y2)` is the **+ terminal** of a voltage source — the source constrains `V(post2) − V(post1) = value` — and for a current source, a positive `currentValue` drives current from post 1 to post 2 through the source (arrow toward post 2). Get a sign convention wrong and everything still "runs," but every downstream number is silently negated — the classic off-by-minus-one. Programmers can think of it like byte order (endianness): an arbitrary choice that is harmless only if every producer and consumer agrees, and catastrophic-but-quiet if they don't. Because the project treats circuit math as untrusted input, these two sign facts are explicitly flagged for pinning with an ngspice/circuitjs golden test rather than being trusted from source-reading alone.

This is standard EE vocabulary; the related textbook idea for elements is the **passive sign convention** (current defined as entering the + terminal), though our doc concerns itself specifically with the Falstad file format's post ordering.
- **Where it appears:** `docs/falstad-format.md` §4 (voltage source semantics, post 2 = +) and the §7 oracle note; the working agreement in `CLAUDE.md`/`docs/testing-strategy.md` that math is untrusted.
- **References:** Nagel (1975), *SPICE2: A Computer Program to Simulate Semiconductor Circuits*, UCB/ERL-M520 (SPICE's source/branch sign conventions); the ngspice manual (source polarity and branch-current conventions).

### Pole pairs / pole pieces / pole count
- An alternator (or motor) design parameter — how many magnetic north/south pole pairs the rotor carries — which multiplies shaft rotation speed into electrical frequency: **electrical frequency = shaft speed × pole pairs**. (Strictly, "pole count" is the number of poles P = 2 × pole pairs; the docs use "pole count" and "pole pairs" interchangeably for the frequency multiplier.)

A one-pole-pair alternator produces one full AC cycle per shaft revolution; a ten-pole-pair alternator produces ten, because each pole pair sweeping past a coil completes one cycle. For programmers it is a fixed gear ratio — a hardware clock multiplier — between the mechanical and electrical domains: the *only* knob, with deliberately no hidden fudge factor, so you cannot overclock the shaft, you widen the datapath instead. In the Vintage Story frequency arithmetic this is f ≈ 0.8 × speed × polePairs Hz, where the 0.8 falls out of the engine's shaft-angle integration (ω = 5·speed rad/s, verified against engine source). Several facets hang off it:

- **Solver cost (subcycled AC).** Each AC island's substep count N is chosen from its highest source frequency at ≥ 20 samples/cycle, so a ~5 Hz waterwheel alternator needs ~5 substeps per 50 ms game tick while a 50 Hz machine pushes N to ~50 — a real, deliberate performance cost. The frequency arrives as a piecewise-constant input from the mechanical network via R14's coupling rule.
- **Pedagogy (tablet Lesson 12, "Frequency comes from rotation").** Sitting right after the oscilloscope lesson that shows the player their 5 Hz lamp flicker, Lesson 12's payload is the single relation electrical frequency = shaft speed × pole pairs, plus the historical punchline of why the real world settled on 50 Hz.
- **Design progression-gating (R14).** ~5 Hz is the natural output of waterwheel/windmill alternators (visible lamp flicker included), and the player *cannot* chase 50 Hz through shaft speed because the mechanical network overheats past speed 4.5. The only route up is crafting high-pole-count alternators, whose extra pole pairs multiply a modest shaft speed into 40–60 Hz. The reward is physically honest: transformer iron size and cost scale inversely with frequency, so 5 Hz transformers are enormous.
- **Economics (smithing).** A starter alternator has 2–4 pole pairs (~3–6 Hz); a mains-like machine needs an advanced 12–20-pair build. The individual **pole pieces** (the shaped iron/magnet parts, one per pole) are hand-smithed on the anvil in VS's smithing minigame, so high pole count costs labor as well as metal — matching how real high-pole-count hydro alternators are shaped.

Standard EE term (aliases: number of poles = 2 × pole pairs; "salient poles" for the protruding pole-piece construction). The textbook synchronous-machine relation is f = (P/2)·n/60 (or f = p·n/120) with P poles, n in RPM — the same formula in game-native units.
- **Where it appears:** `docs/solver.md` Analyses (Subcycled AC, the ~50-substep example); `docs/design.md` R14 and "Voltage and frequency standards" (5 Hz natural, 40–60 Hz via pole count); `docs/vintage-story.md` §Frequency arithmetic (the f ≈ 0.8 × speed × polePairs rule and smithing economics); `docs/curriculum.md` Lesson Arc III, Lessons 12–13.
- **References:** Fitzgerald, Kingsley & Umans, *Electric Machinery* (synchronous-machine frequency–speed–poles relation; hydro machines as the low-speed/high-pole case); Horowitz & Hill, *The Art of Electronics* (AC power basics).

### post
- A connection terminal of a circuit element, located at an integer canvas coordinate in a Falstad circuit file.

In the Falstad/circuitjs1 format each element line carries coordinates `(x1,y1)` and `(x2,y2)`; for ordinary 2-terminal parts these are the element's two posts — the points where wires attach. Some parts deviate: single-post parts (ground `g`, rail `R`, output marker `O`) use the second point only as the drawing direction of a stub, while transformers `T` have four posts at the corners of the bounding box the two points span. Posts carry electrical meaning beyond geometry: for a voltage source, post 2 `(x2,y2)` is the + terminal, and a positive current source drives current from post 1 toward post 2 — sign conventions we pin with oracle tests rather than trust. For programmers: a post is the format's join key — two elements are connected exactly when their posts land on the same pixel coordinate, the way rows join on an equal column value; there is no separate node list.

**Standard term:** terminal (also pin, lead). "Post" is circuitjs1's own vocabulary (e.g. `CircuitElm.getPostCount()`), which we adopt when discussing that format.
- **Where it appears:** `docs/falstad-format.md` §3 (coordinates and posts), §4 (per-element post counts and polarity), §7 (oracle note on post-2-positive convention).

### power bar / powerBarValue
- An optional field in the Falstad file header that scales the on-screen power-dissipation display; it has no effect on the simulation.

The circuitjs1 header line is `$ flags maxTimeStep simSpeed currentSpeed voltageRange [powerBarValue [minTimeStep]]`. When the "show power" flag (bit 8) is set, the simulator colors elements by how much power they dissipate, and `powerBarValue` sets the full-scale value of that color ramp — like choosing the range knob on a meter, it changes what the display shows, not what the circuit does. It is one of the two optional trailing header fields absorbed by try/catch (see positional-with-defaults). We record it for format completeness; an importer aimed at circuit semantics can ignore it, since `maxTimeStep` is the only header field with electrical meaning.

**Standard term:** none — this is circuitjs1 UI state (a display scale for power dissipation), not a circuit-theory concept.
- **Where it appears:** `docs/falstad-format.md` §2 (header line), sourced from `CirSim.dumpOptions()` / `CircuitLoader.readOptions` in the circuitjs1 source.

### power factor
- The fraction of the current flowing in an AC circuit that actually delivers useful energy — the rest just "sloshes" back and forth, heating the cables while doing no work.

On AC, capacitive and inductive loads shift current out of step with voltage. The in-step portion transfers real power; the out-of-step portion is reactive — energy borrowed from the source each half-cycle and handed back the next, yet it still flows through the wires and dissipates in their resistance. Power factor is the ratio of real power to total apparent power (1.0 = all current is useful). A programming analogy: it is like a network link where only some fraction of the packets carry payload and the rest are retransmissions — the link is saturated (and heats up) at full bandwidth, but goodput is lower. In Manatee this is Lesson 16 of the tablet curriculum ("the sloshing current that heats cables but does no work"), near the top of the curriculum's AC-power ceiling: the tablet teaches up through AC power (transformers, impedance, power factor, synchronization) and no further, with the tier's capstone being Lesson 17, synchronization.

This is the standard EE term; in the literature it equals cos φ for sinusoidal waveforms, where φ is the voltage–current phase angle.
- **Where it appears:** `docs/curriculum.md` Lesson Arc III.16; `docs/design.md` Non-Goals (curriculum ceiling) and progression arc.
- **References:** Horowitz & Hill, *The Art of Electronics*, 3rd ed., Cambridge University Press (AC power section).

### probe / probe placements
- A named, user-placed observation point in a circuit whose voltage can be read out of the solved solution — feeding meters, tooltips, and oscilloscope traces — and which keeps working even after the solver has simplified the circuit underneath it.

In the schematic document model, probe placements are first-class document objects alongside components, wires, and junctions: where the player has clipped a virtual multimeter or scope lead. In manatee-core each becomes a `ProbeId` — a named voltage observation point bound to a node, read via `Solution.Read(p)` or streamed per-substep through a `WaveformTap` for the oscilloscope. The key property is that probes *survive reduction*: when the compaction layer collapses a long chain of resistive segments into one equivalent resistor (R10), the interior nodes are deleted from the matrix, but a probe placed inside the chain still answers correctly because the reduction layer registers an interpolator that reconstructs the eliminated voltage from the chain's endpoints and resistance ratios. So an in-game meter can read "inside" a compacted cable run of thousands of segments even though the solver only ever saw one resistor. Current is not read through a probe: current measurements come from `Solution.Current` on a component's branch handle, so the ammeter case is a branch read, not a `ProbeId`. Probes are strictly observational — reading one never disturbs the solution. For an EE this is the familiar test point / voltmeter or scope probe, with the twist that the node it touches may no longer physically exist in the solved matrix; for a programmer it is a stable read-only subscription handle onto the solver's output vector — like a watch expression in a debugger — with a small adapter (the interpolator) re-wired on each rebuild so reads stay correct.

**Standard term:** test point / probe (as in a multimeter or oscilloscope probe; in SPICE terms a saved output node, ngspice's `.save`/`.print` or PSpice/HSPICE's `.PROBE`).
- **Where it appears:** `docs/design.md` R10 and R15; `docs/solver.md` "State, Limits, Probes"; `docs/compaction.md` Responsibility #3 (probe interpolation) and its invariants; `docs/harness.md` Layering #1 (document model) and Testing Model; `docs/api.md` §13 (`ProbeId`, `AddProbe`, `WaveformTap`); `docs/curriculum.md` lesson front-matter (machine-checked probe/time/value expectations).
- **References:** the ngspice manual (`.save`, `.probe`, `.print` output directives); Horowitz & Hill, *The Art of Electronics* (bench measurement practice).

### probe / voltmeter (`p`) (Falstad format)
- The two-post meter element in Falstad/circuitjs text netlists, element code `p`, which Manatee's importer accepts and flattens into a plain probe marker.
- Upstream in circuitjs, `p` is a voltmeter/probe with two posts and display parameters `[meter [scale [resistance]]]` (`ProbeElm.java:43-50`): `meter` selects the displayed quantity (0 = voltage), `scale` is a display setting, and `resistance` — an optional finite meter resistance instead of an ideal open circuit — is a 2026 addition most files in the wild predate. Manatee deliberately ignores all three display parameters and treats `p` (like the one-post `O` output marker) purely as a probe marker for the tablet and CI goldens: the importer records *where to measure*, and Manatee's own probe machinery does the measuring. For the EE: an ideal voltmeter across two nodes, reduced to a labeled test point; for the programmer: the importer keeps the location and drops the presentation attributes. One trap documented in our spec: Electrical Age's old parser misread `p` as a *push switch* — a plain divergence from circuitjs that we explicitly do not copy. Our own lesson corpus uses `O` for probes, keeping lesson files loadable in falstad.com unmodified.
- **Standard term:** voltmeter / probe (circuitjs element `ProbeElm`); ideal voltmeter in EE usage.
- **Where it appears:** falstad-format.md §4 element table (`p` row), §7 importer accept-list (`O` and `p` → probe markers, display params ignored) and the EA-divergence note; curriculum.md corpus rule (probes as `O`).
- **References:** the circuitjs1 source (`ProbeElm.java`), from which the format spec was derived.

### rail (`R`)
In the Falstad circuit file format, dump type `R` is a one-post voltage source: a terminal held at a specified voltage relative to ground, without a drawn second terminal.

An ordinary voltage source (`v`) is a two-terminal element setting the voltage *difference* between its posts; a rail is the same element with its second terminal implicitly tied to the circuit's ground reference, so it appears in a schematic as a single connection point at a fixed potential — like the "+5 V" or "VCC" labels on real schematics. In the Falstad source this is literal: `RailElm` extends `VoltageElm` and takes the same parameter list (waveform, frequency, max voltage, bias, phase), so it can be a DC rail or an AC source equally (`RailElm.java:22,38`). For a programmer: it is a global constant exposed at a point, versus `v`'s constraint between two arbitrary variables — same implementation class, different wiring convenience. Manatee's importer accepts `R` under the same waveform restrictions as `v` (DC or sine only; waveforms 2–7 rejected loudly).

**Standard term:** grounded (single-ended) voltage source; commonly "supply rail" or "DC rail" on schematics. "Rail" here is Falstad's element name, not the physical bus-bar sense.

**Where it appears:** `docs/falstad-format.md` §4 element table (with `172`, the adjustable rail with slider, which Manatee rejects) and the §7 accept list.

**References:** Falstad/circuitjs1 source (`RailElm.java`); Horowitz & Hill, *The Art of Electronics*, for supply-rail conventions on schematics.

### RC charging curve
The characteristic exponential curve traced by the voltage across a capacitor as it charges through a resistor — fast at first, then ever slower, approaching the supply voltage without quite reaching it.

When a voltage source charges a capacitor C through a resistor R, the capacitor voltage follows v(t) = V·(1 − e^(−t/RC)); the product RC is the *time constant* τ, the time to reach about 63% of the final value (and ~99% after 5τ). For programmers: it behaves like exponential backoff or a low-pass smoothed moving average — each step closes a fixed *fraction* of the remaining gap, so the state converges asymptotically to its target. In Manatee this is Lesson 4 of the tablet curriculum (arc I, "DC foundations"), the first lesson where time and state matter rather than instantaneous Ohm's-law relationships; per the curriculum's authoring rules, component values are scaled so the time constant is visible at game tick rates.

This is the standard term (also seen as "RC charging/discharging", "exponential rise", "first-order step response").

**Where it appears:** `docs/curriculum.md`, Lesson Arc I.4.

**References:** Horowitz & Hill, *The Art of Electronics*, ch. 1 (RC circuits and time constants).

### reactive behavior
The part of a circuit's AC behavior that comes from energy *storage* (capacitors, inductors) rather than energy consumption — current and voltage get out of step with each other, and power sloshes back and forth instead of being used up.

A resistor turns every joule it receives into heat; a capacitor or inductor instead borrows energy on one part of the AC cycle and pays it back on another. The visible symptoms are phase shift (the current waveform leads or lags the voltage waveform) and "reactive power" — power that circulates without doing work, but still loads the wires carrying it. A programming analogy: a resistor is a pure function of its input, while a reactive element carries state between steps — its behavior this instant depends on accumulated history (stored charge or built-up current), like an integrator or a buffer that fills and drains each cycle. In Manatee this matters for lesson design: our *decoupling* transformers exchange only amplitude+phase per substep across an island boundary, which launders reactive power away, so lessons teaching reactive behavior and power factor must use *idealized* (same-matrix) transformers so the whole demonstration lives in one island.

This is the standard EE term (see also *reactance*, *reactive power*, *power factor*).

**Where it appears:** `docs/design.md` (Simulation Model, island coupling / transformers), `docs/curriculum.md` (Authoring Rules — transformer lessons use the idealized class).

**References:** Horowitz & Hill, *The Art of Electronics*, ch. 1 (reactance, phase, power in reactive circuits).

### recloser-style lockout
- A protection behavior where a device that has browned out repeatedly in a short time window latches itself off and stays off until a player manually resets it.

In Manatee's Stationeers adaptor (R18), an unconverted device that sees undervoltage drops off the net (a brownout), then tries to rejoin once the voltage recovers. Hysteresis and staggered rejoin delays already damp oscillation, but a genuinely overloaded net could still cycle drop/rejoin forever at tick rate — visually strobing and computationally noisy. The lockout is the escape hatch: after N brownouts within a window, the device latches into an off state requiring manual reset, so the net settles into a legible steady state instead. The name borrows from utility practice: a *recloser* is a grid breaker that automatically re-energizes a line a few times after a fault, then "goes to lockout" and waits for a line crew if the fault persists. For programmers: it is a retry policy with a circuit-breaker pattern on top — bounded automatic retries, then a latched failure state cleared only by operator action, preventing a retry storm.

The term is standard utility-engineering vocabulary (recloser lockout); we apply it per-device rather than per-line.
- **Where it appears:** `docs/design.md` R18 (the adaptor's brownout behavior); `docs/api.md` §24 built-in models — `AdaptedLoad` implements hysteresis + staggered rejoin + recloser lockout.
- **References:** IEEE Std C37.60 (automatic circuit reclosers); Nygard, *Release It!* (2007) for the software circuit-breaker/retry-storm analogy.

### rectifier / AC↔DC boundary
- A rectifier converts alternating current to direct current; in Manatee, rectifier-flavored devices are modeled as behavioral two-ports that couple an AC island to a DC island, so each island stays linear in its own domain.

A real rectifier is built from diodes, which are nonlinear — simulating one at the semiconductor level would force Newton iteration into the per-substep hot path of every island that contains one. Manatee sidesteps this: if AC↔DC conversion devices exist, they sit *between* islands as behavioral two-ports (a component described by its terminal behavior — power in, power out — rather than its internals), exchanging values across the boundary each substep like transformers do. The AC island remains a purely linear sinusoidal problem and the DC island a purely linear transient problem, each solvable by cheap back-substitution. This is explicitly not a VS gameplay focus: the design non-goals rule out semiconductor-level power electronics in the world (the tech fiction is early-industrial). Programming analogy: an adapter at a service boundary that translates between two protocols so neither service needs to understand the other's internals.

Standard EE terms throughout (rectifier, two-port network); the island-boundary coupling mechanism is a project design choice akin to co-simulation partitioning.
- **Where it appears:** `docs/design.md` Simulation Model (AC bullet) and Non-Goals (semiconductor-level power electronics); full island-coupling treatment in `docs/solver.md`.
- **References:** Horowitz & Hill, *The Art of Electronics*, ch. 1 (rectification); Vlach & Singhal, *Computer Methods for Circuit Analysis and Design* (two-port networks, nonlinear device iteration).

### Reference node / datum / MNA row 0
- The one node per island whose voltage is defined to be exactly zero, so every other node's voltage is a well-defined number measured relative to it — and, in Manatee's API, the anchor the wiring policy routes device return paths *toward*.

Voltages are only ever *differences*; the equations have no absolute zero until you pick one, so the MNA matrix for a circuit with no fixed reference is singular (infinitely many solutions differing by a constant offset, like timestamps with no epoch). MNA picks the datum by simply deleting that node's row and column from the matrix (hence "row 0" — its equation is never written), which is also what makes the system solvable at all. In programming terms it is the origin — or base address — of the voltage coordinate space; every reading is an offset from it, and code that assumes two islands share a base without a connecting element is reading the wrong address space. Manatee makes the choice explicit and client-shaped: `AddReferenceNode` / `MarkReference` designates it — the literal earth node in Vintage Story, each partition's return rail in Stationeers — and if an island has no marked reference the solver silently auto-anchors an arbitrary node so it still solves. When islands merge, multiple explicit references coalesce into one (legal and silent — real earth is one node). As a belt-and-braces measure, gmin shunts (1e-12 S to reference) are stamped on non-ground diagonals so floating subgraphs with no conductive path to the datum still yield a non-singular matrix (flagged in debug builds).

The construction-time **wiring policy** routes device negative terminals *toward* the reference declaratively — never by shorting them to it. Vintage Story's `TwoWireLeak` binds negatives to routed return-conductor nodes that reach the earth reference only through per-electrode contact resistance; Stationeers' `ReferenceBound` connects each partition's devices to that partition's **reference rail** through a near-ideal (~1e3 S) return conductance, resolved via `ConductorGraph.ReferenceNode(PartitionKey)`. That rail is a lazily-created per-partition singleton (created the first time the partition is used), and the policy fires automatically on `AddVoltageSource`'s `neg` argument, on `NodeRole.Return` nodes, and on a device's declared `ReturnTerminals`, so device adaptors just write `_neg = graph.ReferenceNode(Partition)` with no per-device boilerplate to get wrong. For an EE this rail is the chassis/ground bus of each isolated network.

**Standard term:** ground node / datum node (SPICE calls it node 0; aliases: ground, earth, datum, "rail" for a shared common conductor). Standard MNA practice is to delete the reference node's row and column. Note our reference need not be the fictional earth — in Stationeers it is a network's return rail.
- **Where it appears:** `docs/design.md` (Grounding model, API consequence); `docs/api.md` §5 (Reference nodes, `WiringPolicy`, `WiringPolicy.ReferenceBound`) and `StructuralEdit.AddReferenceNode`/`MarkReference`, §19 (`ConductorGraph.ReferenceNode`); `docs/solver.md` ("The Netlist API" grounding contract: node 0 per island; "Islands" per-island ground anchoring, gmin shunts, auto-anchoring); `docs/integration-tutorial.md` §2–§4.
- **References:** Ho, Ruehli & Brennan (1975), "The Modified Nodal Approach to Network Analysis", IEEE Trans. Circuits and Systems; Vlach & Singhal, *Computer Methods for Circuit Analysis and Design*; the ngspice manual (gmin and the node-0 convention).

### relay
An electromechanical switch: a small control current through a coil magnetically pulls a contact open or closed, so one circuit can switch another.

For programmers: a relay is a physical `if` — the coil circuit is the condition, the contact circuit is the body — and because a relay's contact can feed another relay's coil, relays compose into latches, interlocks, and full controllers (the historical ancestor of logic gates). In Manatee they appear in Arc 2 as the player's control-logic primitive (the elevator controller is wired from relays, call buttons, and limit switches — all just more MNA at low voltage, not a separate signal system) and as telegraph repeaters: when line loss has attenuated a Morse signal too far, a relay regenerates it from a fresh local supply, the same job a repeater does in a network link. Curriculum Lesson 8 introduces it as "a switch driven by a circuit — control as circuitry."

The term is standard EE/industrial vocabulary; no aliases needed.

**Where it appears:** `docs/design.md` (Arc 2, Elevator control), `docs/curriculum.md` Lesson Arc II.8.

**References:** Horowitz & Hill, *The Art of Electronics* (relays among switching elements).

### relay logic
Building control behavior — memory, sequencing, mutual exclusion — purely out of relays wired together; the topic of tablet Lesson 9 and the prerequisite for the Lesson 10 elevator-controller capstone.

Because a relay's contact can energize (or cut) another relay's coil, relays compose into the classic control primitives the lesson teaches: the *latch* (a relay whose own contact keeps its coil energized after the start button is released — a physical set/reset flip-flop, i.e. one bit of state) and the *interlock* (contacts wired so two things cannot be on at once — a physical mutex). For programmers this is Boolean logic and state machines implemented in copper and springs, which is literally how pre-electronic elevators, telephone exchanges, and factory controls worked; for the EE it is the familiar predecessor of ladder-logic PLC programming. In Manatee there is no separate signal-network abstraction: the relay-logic controller is ordinary low-voltage MNA circuitry the player wires by hand, with a craftable pre-wired controller block as optional training wheels.

**Standard term:** relay logic; the industrial notation descended from it is *ladder logic* (IEC 61131-3 Ladder Diagram).

**Where it appears:** `docs/curriculum.md` Lesson Arc II.9 (and II.10 capstone), `docs/design.md` (Arc 2; "Elevator control: pure electromechanics").

### Relay-logic control / relay logic
Building a control system — most famously the elevator's brain — entirely out of relays, switches, and wire, so the "logic" is just another low-voltage circuit solved by the same MNA solver as everything else.

A relay is a switch operated by an electromagnet: energize its coil and its contacts close (or open). Wire several relays together and you get latches, interlocks, and sequencing — the pre-transistor way real elevators, telephone exchanges, and factory controls were built. For programmers: relay logic is boolean logic implemented in hardware, where a self-holding relay is a 1-bit flip-flop and an interlock is a mutex; the whole controller is a physical state machine whose "variables" are which coils are energized. The project point is architectural: there is *no separate signal network or scripting layer*. Call buttons, limit switches, and relay coils are ordinary netlist components at low voltage in the same simulation, so control circuits obey the same physics (voltage drop, line loss) as power circuits. This is Arc 2's peak educational payoff ("I built the elevator's brain out of relays"), taught by curriculum lessons 8–9 and an elevator-control tablet lesson, with a pre-wired controller block as optional training wheels.

This is the standard historical term (also: *relay ladder logic* in industrial control, ancestor of PLC ladder diagrams).

**Where it appears:** design.md (Arc 2 progression; "Elevator control: pure electromechanics"), curriculum.md lessons 8–10.

**References:** Horowitz & Hill, *The Art of Electronics* (relays, switch logic); relay logic is the direct ancestor of ladder logic in IEC 61131-3 PLC programming.

### resistance ratio
- The fraction of a collapsed resistor chain's total resistance that lies between one end and a given interior point, used to reconstruct that point's voltage after the chain has been reduced to a single resistor.

Compaction replaces a run of series resistors (a voxel cable run, a string of cable segments) with one equivalent resistor, deleting the interior nodes from the solved system. But instruments may still probe "inside" the run, so the deleted voltages must remain recoverable. In a series chain the same current flows through every segment, so voltage drops in direct proportion to accumulated resistance: an interior node sitting past 30% of the chain's total resistance is at 30% of the way between the endpoint voltages. Probe interpolation therefore stores, per interior node, its cumulative-resistance fraction and computes `V = V_a + ratio × (V_b − V_a)` on demand. For a programmer: the ratio is a precomputed interpolation key, like keeping the byte offset of a record after compressing a file — the detail is gone from the hot representation but reconstructible exactly. This is standard voltage-divider physics (exact for the DC/resistive chain, not an approximation), applied as the read-back rule of our reduction layer.

**Where it appears:** docs/compaction.md, Responsibilities #3 (probe interpolation over series-chain collapse).

**References:** Horowitz & Hill, *The Art of Electronics*, on voltage dividers; Vlach & Singhal, *Computer Methods for Circuit Analysis and Design*, on network reduction.

### Resistivity
- An intrinsic material property (symbol ρ, units Ω·m) saying how strongly a material resists current flow, independent of the shape of the piece you cut from it.

Resistance of an actual conductor segment is derived from it: R = ρ × length / cross-sectional area. In Manatee this is taken seriously per R12 — every cable material carries a real resistivity, and the compaction layer computes inter-region resistances from material resistivity and contact cross-section when building the graph. For programmers: resistivity is the per-material constant, resistance is the per-instance value computed from constant × geometry — like a class attribute versus a field derived from it at construction. Because the numbers are real, wire gauge, line loss, and fusing are emergent physics: a lead segment spliced into a copper run has much higher resistivity, heats fastest under overload, and melts first — it *is* a fuse, with zero special-casing.

This is the standard EE/physics term. Its reciprocal is conductivity (σ).

**Where it appears:** `docs/design.md` R12; `docs/compaction.md` Responsibilities #1 (region building).
- **References:** Horowitz & Hill, *The Art of Electronics*, ch. 1; any introductory physics text (e.g. Halliday, Resnick & Walker, *Fundamentals of Physics*).

### Resistor edge
- An edge in the pre-solver circuit graph that carries a resistance value — the graph-theoretic representation of a run of physical cable.

When game geometry is translated into a circuit, the graph is built at cable-segment granularity — every segment endpoint is a graph node, and each physical segment becomes a resistor edge, with resistance computed from material/type × length (Stationeers: cable type × segment length; Vintage Story: voxel material resistivity × geometry). The compaction layer then collapses runs of resistor edges with no taps into single equivalent resistors — the series-chain collapse that shrinks ~10k cable segments to low-hundreds of nodes before the MNA solver ever sees them. For an EE: this is just series resistance combination, applied mechanically over a graph. For programmers: it is edge contraction on a weighted graph where the contracted weight is the sum, plus bookkeeping (probe interpolation, limit envelopes) so the eliminated interior points stay observable.

The phrase is our shorthand for "weighted graph edge representing a resistor"; the underlying concepts (netlist branch, series reduction) are standard.

**Where it appears:** `docs/compaction.md` Responsibilities #2; `docs/stationeers.md` Graph Construction; `docs/api.md` §on graph building.
- **References:** Pillage, Rohrer & Visweswariah, *Electronic Circuit and System Simulation Methods*, on netlist graph representations and network reduction.

### Resistor (`r`)
- The Falstad text-format element code for a plain resistor: a two-terminal component whose only parameter is its resistance in ohms.

In the Falstad/circuitjs1 dump format that Manatee's importer accepts, each element is one line starting with a type token; `r` denotes a resistor and carries a single parameter, `resistance` in Ω, after the standard position-and-flags fields (per `ResistorElm.java` in the circuitjs1 source). For an EE this is the most ordinary component there is — V = IR, no state, no nonlinearity. For programmers: it is the simplest record in the file format, a struct with one float, and the importer maps it directly onto manatee-core's resistor element with no accept/reject subtleties (unlike, say, `v` sources, where only some waveforms are accepted).

**Where it appears:** `docs/falstad-format.md` §4 element table and the importer accept list.
- **References:** the circuitjs1 source (`ResistorElm.java`), which `docs/falstad-format.md` cites by file and line; Horowitz & Hill, *The Art of Electronics*, ch. 1.

### return conductance / leak resistor
- The two implicit, auto-stamped elements that keep otherwise-floating circuits mathematically solvable, chosen per client by the `WiringPolicy`: a near-ideal **return conductance** (Stationeers) or a ~1 MΩ **leak resistor** to earth (Vintage Story).

MNA needs every node to have some path to the reference node, or the matrix is singular — like a graph algorithm that requires the graph to be connected. Stationeers' `ReferenceBound` policy binds every device's return terminal to its partition's reference through a near-ideal conductance (default 1000 S, i.e. ~1 mΩ); this is numerically identical to actually modeling the return conductor, so the fiction of a two-wire cable bundle holds while the full double-wire solve is never paid. Vintage Story's `TwoWireLeak` policy is different: there both supply *and* return are real routed nodes, so islands genuinely float; at commit the solver auto-stamps a ~1 MΩ leak from every `Return`-role node to the island's earth reference — a resistance high enough to be electrically invisible (benchmarked at ~machine-epsilon conditioning cost) but enough to pin the island's potential. Both are infrastructure, not circuit content: they are never hand-authored, have no client handle, and never appear in probe results as components.

**Standard term:** the leak resistor is a coarse cousin of SPICE's **gmin** (a small conductance added to keep matrices nonsingular), applied topologically at Return nodes rather than across every junction; "leakage resistance to ground" is also a real physical concept in insulation. The near-ideal return conductance is standard circuit-reduction practice (replacing an explicit return conductor with its equivalent conductance to the reference).

**Where it appears:** `docs/api.md` §5 (`WiringPolicy.ReferenceBound` / `TwoWireLeak`), `docs/design.md` (grounding model: two-wire default, SWER discussion).

**References:** ngspice manual (the `gmin` option and gmin stepping); Ho, Ruehli & Brennan (1975), "The Modified Nodal Approach to Network Analysis", IEEE Trans. Circuits and Systems (why a reference node is required).

### RLC ring-down
The damped oscillation you see when a circuit containing a resistor (R), inductor (L), and capacitor (C) is disturbed and then left alone: the response "rings" like a struck bell and dies away.

A capacitor stores energy in an electric field, an inductor in a magnetic field, and when the two are connected the energy sloshes back and forth between them — the curriculum's phrasing — at the circuit's natural frequency, while the resistor bleeds a little energy off as heat on every swing, so each oscillation is smaller than the last. For programmers: it is a second-order system, exactly analogous to a mass on a spring with friction, and the resistance sets whether the system is underdamped (visible ringing), critically damped, or overdamped (no oscillation, just a slow settle). In Manatee this is Lesson 5 of the tablet curriculum (the first lesson with two energy-storage elements), and it is also a shape the transient solver must reproduce faithfully — the docs elsewhere use "rings up" for the failure mode where a non-conservative numerical model *adds* energy each cycle instead of losing it.

**Standard term:** damped oscillation / ringing / underdamped second-order (RLC) transient response; "ring-down" is standard usage in physics and RF engineering.

**Where it appears:** `docs/curriculum.md`, Lesson Arc I, lesson 5.

**References:** Horowitz & Hill, *The Art of Electronics* (RLC transients and damping); any circuits text covering second-order natural response, e.g. Nilsson & Riedel, *Electric Circuits*.

### rotor
The rotating part of a generator — the spinning shaft-and-magnet assembly whose angle and speed determine the phase and frequency of the electricity produced.

In a real AC generator, the rotor spins inside the stationary part (the stator), and the electrical output's phase tracks the rotor's mechanical angle directly. Manatee uses this in the synchronization capstone lesson and in the generator-paralleling model: when two generators feed one grid, physics couples their rotors as if "by a spring between the rotors" — if one rotor pulls ahead in angle, electrical power flows from it to the other, tugging them back toward lockstep, exactly like two flywheels joined by a torsion spring with a damper. For programmers: each generator device is a tiny simulation object holding two state variables (rotor angle, angular velocity) integrated per tick with spring-damper coupling; the electrical solver never sees any of this — it just sees a voltage source whose phase the device layer drives (`docs/solver.md`, "swing-equation-lite"). The lesson payoff is why grids must be synchronized before closing the tie switch, and why they can be.

**Standard term:** rotor (universal EE/mechanical usage). The spring-between-rotors intuition is a simplified form of the **swing equation** from power-system stability analysis; the docs call our version "swing-equation-lite".

**Where it appears:** `docs/curriculum.md` Lesson Arc III, lesson 17 (Synchronization); `docs/solver.md` "Generator paralleling"; `docs/design.md` generator-paralleling decision.

**References:** Kundur, *Power System Stability and Control* (swing equation, synchronous machine dynamics); Chapman, *Electric Machinery Fundamentals* (rotor/stator construction).

### Rotor angle / angular velocity
- The two numbers of mechanical state each generator carries: where its spinning rotor is in its rotation (angle, i.e. phase) and how fast it is turning (angular velocity, i.e. speed).

In Manatee's "swing-equation-lite" model, each generator device integrates this (angle, velocity) pair over time and uses the angle to drive the phase of its AC voltage source. When several generators feed the same network, spring-damper coupling between their angles models the real physics of paralleled machines: a generator lagging the network gets pulled forward, one leading gets pushed back, so synchronization emerges (and is a gameplay skill). For programmers: this is a tiny per-device physics integrator — two floats of state updated each tick — living entirely in the `devices` layer, not the solver; the solver only ever sees cheap tier-1 source-phase updates. For the EE: it is the classical swing equation of power-system stability, deliberately simplified — hence "-lite".

These are the standard power-engineering terms (rotor angle is also called "power angle" or "torque angle", symbol δ; angular velocity ω).
- **Where it appears:** `docs/solver.md` (Analyses, "Generator paralleling"); `docs/design.md` (Simulation Model).
- **References:** Kundur, *Power System Stability and Control*, McGraw-Hill, 1994 (the swing equation and rotor-angle stability).

### Sag under load
- The drop in a power source's terminal voltage as current is drawn from it, caused by the source's internal resistance.

No real battery or generator is an ideal voltage source; each behaves like an ideal source in series with a small internal resistance. Every ampere drawn loses some volts across that internal resistance (V = I·R), so the voltage actually available at the terminals "sags" as load increases — the source promises 12 V but delivers less the harder you pull. For programmers: it is like a service whose advertised throughput degrades under concurrent load because of a fixed internal bottleneck — the spec value is the unloaded value. Curriculum lesson I.6 ("Batteries are not ideal") uses this to teach why the voltaic pile's output droops under load and why its zinc is consumed.

**Standard term:** terminal-voltage droop (also "voltage sag" / "voltage drop under load"); the underlying model is a Thévenin equivalent source — EMF in series with internal resistance. (In utility-power jargon, "voltage sag" also names a specific short-duration supply dip; our usage is the simpler internal-resistance meaning.)
- **Where it appears:** `docs/curriculum.md` Lesson Arc I.6.
- **References:** Horowitz & Hill, *The Art of Electronics*, 3rd ed., §1.2 (Thévenin equivalents and source resistance).

### samples/cycle
- Shorthand notation for **samples per cycle** (see that entry): the ≥ 20-substeps-per-sinusoid-cycle rule that sets an AC island's substep count.

`docs/solver.md` states it as: an island whose sources include sinusoids at frequency f runs N substeps per game tick, N chosen from the island's *highest* source frequency at ≥ 20 samples/cycle. Concretely, 5 Hz needs a 100 Hz substep rate, i.e. 5 substeps inside each 50 ms game tick; a 50 Hz machine (via alternator pole count) needs ~50. For an EE this is simply the transient time step expressed as a fraction of the signal period; for a programmer it is a resolution budget — how many frames you render per animation cycle. Note the documented footgun: a sine source placed in a non-Mixed solver profile is legal but single-sampled per tick, with *no* samples/cycle guarantee — deterministic and phase-wrapped, but heavily undersampled (debug builds warn).

The term is standard simulator vocabulary (also seen as "points per cycle" or "points per period").

**Where it appears:** `docs/solver.md` (Analyses → Subcycled AC), `docs/design.md` (Simulation Model), `docs/api.md` (`SolverProfile.Mixed` comments).
- **References:** ngspice manual, transient analysis chapter; Vlach & Singhal, *Computer Methods for Circuit Analysis and Design* (time-step selection in transient simulation).

### saturationCurrent
- An optional parameter on Falstad-format inductor (`l`) and transformer (`T`) lines that models magnetic-core saturation; Manatee's importer **rejects** files where it is nonzero.

In a real inductor or transformer, the iron core can only hold so much magnetic flux; past a certain current the inductance effectively collapses — the component's defining parameter stops being a constant and becomes a function of its own state. Falstad/circuitjs1 added `saturationCurrent` (a 2025–2026 addition to its text readers) to model this nonlinearity. Manatee's design explicitly scopes out nonlinear magnetics: our inductors and transformers are linear elements, so the importer's accept/reject contract loudly rejects any nonzero `saturationCurrent` rather than silently dropping it — the same fail-loud policy applied to other unsupported Falstad features. For a programmer: it is an input-validation rule guarding an invariant ("all magnetics in the netlist are linear"), not a solver feature. Note this is inductor-core saturation — unrelated to the identically-named *diode* saturation current (Is) in the Shockley equation, which Manatee *does* support via diode model parameters.

**Standard term:** the underlying physics is standard (magnetic/core saturation; saturation current of an inductor); the camelCase spelling is Falstad's serialized field name.

**Where it appears:** `docs/falstad-format.md` §4 (element tables for `l` and `T`, with `InductorElm.java`/`TransformerElm.java` line references), §7 (importer accept/reject contract).
- **References:** Horowitz & Hill, *The Art of Electronics* (inductors and transformers, core saturation); the falstad/circuitjs1 source (`InductorElm.java`, `TransformerElm.java`).

### scope config (`o`)
- A line type in the Falstad/circuitjs1 text format that configures an on-screen oscilloscope panel — which element to plot, at what speed and scale — rather than describing a circuit element.

In a Falstad export, most lines each describe one component; a lowercase `o` line instead says "show a scope trace of element N with these display settings" (e.g. `o 4 64 0 4099 20 0.05 0 2 4 3`). The format is heavily versioned — circuitjs1 has changed the field list many times, and `Scope.undump` parses it through a serializer — and it is purely display configuration with no electrical meaning. Our importer therefore ignores `o` lines with a logged notice: they appear in virtually every real Falstad export, so rejecting them would make the format useless, but they are counted and reported so a lesson author sees what was dropped. One documented trap: Electrical Age's parser lowercases all element codes, which conflates scope-config `o` with the capital-`O` voltage-probe element (`FalstadNetlist.kt:541, :617`) — a latent bug EA got away with only because its corpus never contained `o` lines. We do not copy that behavior; case is significant.

- **Where it appears:** `docs/falstad-format.md` — §4 line-type table, §6 (EA parser caveats), §7 (importer accept/ignore/reject contract).
- **References:** the circuitjs1 source (Paul Falstad / Iain Sharp, `CircuitLoader.java`, `Scope.java`) is the only spec; the format has no formal standard.

### scope traces / scope
- Time-series plots of probed circuit signals — voltage or current versus time — drawn the way an oscilloscope screen draws them.

In the harness, scope traces are produced by the document model's binding to manatee-core: after each solve step, probe values are read back from the solution vector and appended to a rolling history that the view layer renders as a graph. For a programmer, a scope trace is a ring buffer of (time, value) samples plotted live — structurally the same as a metrics dashboard graph, except the samples come from the circuit solver instead of a production system. For an EE, this is exactly the familiar oscilloscope display, just fed by simulation readback instead of a physical probe; the tablet's lessons use it for predict-then-observe exercises (what will this waveform look like when I close the switch?). "Scope" is the universal shorthand.

**Standard term:** oscilloscope trace (aliases: waveform display, scope channel).

- **Where it appears:** `docs/harness.md` — Layering (layer 1, document model: "solution readback for meters and scope traces"); the Falstad format's scope configuration lines are covered separately under scope config (`o`) in `docs/falstad-format.md`.
- **References:** Horowitz & Hill, *The Art of Electronics*, 3rd ed. (oscilloscope usage throughout).

### series collapse / series-chain collapse
- Replacing a run of conductor segments that has no side connections ("taps") with a single equivalent resistor — the sum of the chain — before the solver ever sees it; an exact simplification, not an approximation.

Electrically this is just the textbook rule that series resistances add (R_eq = R1 + R2 + …): a chain of N tap-free segments behaves, at its two endpoints, exactly like one resistor (for uniform cable, type resistivity × length), and the interior junctions carry no other connections so eliminating them changes nothing the rest of the circuit can observe. Manatee exploits this at scale (requirement R10) because Stationeers bases reach ~10k cable segments and Vintage Story has long voxel runs; collapsing tap-free chains shrinks the graph to low hundreds of nodes — the difference between a solvable matrix and a per-tick perf disaster. For programmers it is loop-invariant hoisting / dead-intermediate elimination on a graph: interior nodes are variables used exactly once, so they fold away, and the solver is handed the smaller equivalent with the guarantee that raw and collapsed graphs produce *identical* terminal behavior. The collapse is lossless for gameplay too: voltages at eliminated interior nodes remain recoverable on demand by interpolating along the chain in proportion to resistance (see *probe interpolation*), and each collapsed resistor carries the per-limit-type envelope of its constituents (the weakest segment's ampacity, plus a Pareto-minimal set of i²t pairs), so a lead fuse hiding inside a copper run still blows exactly when it should and limit attribution maps a trip on the equivalent back to the culprit segment. Correctness is enforced by the reduction-equivalence tests: any graph solved raw vs. series-collapsed must match exactly.

**Standard term:** series resistance reduction / combining resistors in series — a special case of network reduction; the general node-elimination technique is Kron reduction / Gaussian elimination on the conductance matrix. "Collapse" is our verb for the incremental data-structure operation.
- **Where it appears:** `docs/design.md` R10; `docs/compaction.md` Responsibilities #2 (and #3–4 for interpolation and limit envelopes); `docs/solver.md` layering (the `reduction` layer); `docs/testing-strategy.md` (Equivalence Tests); `docs/integration-tutorial.md` §3; `docs/api.md` §19 (the i²t envelope ruling); implemented in `core/Manatee.Core/Reduction/ConductorGraph*`.
- **References:** Vlach & Singhal, *Computer Methods for Circuit Analysis and Design* (network reduction); Horowitz & Hill, *The Art of Electronics* (series/parallel combination); Pillage, Rohrer & Visweswariah, *Electronic Circuit and System Simulation Methods*.

### series voltage drops
- The topic of the tablet curriculum's first lesson: when components sit one after another on a single current path, the supply voltage divides among them, and the bigger resistance takes the bigger share.

In a series circuit there is exactly one path, so the same current flows through every element (an invariant, like a single-lane pipeline where every stage processes the identical stream). Ohm's law (V = I·R) then makes each resistor's voltage drop proportional to its resistance: with the current fixed, doubling a resistor's R doubles the voltage measured across it, and all the drops sum back to the source voltage (Kirchhoff's voltage law). For programmers: think of the total voltage as a fixed budget split among stages in proportion to a per-stage weight — change one weight and every stage's share is redistributed, but the total is conserved. This is the classic "voltage divider" and the foundation the rest of the DC lesson arc builds on.

This is entirely standard EE vocabulary; the related standard concept is the **voltage divider**.

**Where it appears:** `docs/curriculum.md`, Lesson Arc I.1 ("Series voltage drops (one path; dividers; bigger R drops more)"), mirroring Electrical Age's proven teaching order.

**References:** Horowitz & Hill, *The Art of Electronics* (voltage dividers, ch. 1).

### seriesResistance
- An optional parameter on a capacitor in the Falstad/circuitjs1 text format giving a real resistor wired in series with the capacitor, which Manatee's importer honors as exactly that.

Real capacitors are not ideal: their leads, plates, and electrolyte add a small resistance, conventionally modeled as a single resistor in series with an ideal capacitor. Falstad's `c` element grew a `seriesResistance` field for this in a 2025–2026 update, gated on flag bit 4 (`flags&4`) so old files without the field still parse — the format's compatibility scheme only ever *appends* parameters, sometimes guarded by a new flag bit, like a versioned wire format where new optional fields default when absent. Manatee's importer accept/reject contract honors the value by actually instantiating the series resistor rather than ignoring it, so imported circuits keep their damping/charging behavior.

**Standard term:** ESR — equivalent series resistance (of a capacitor). Our name is just Falstad's field name for the same thing.

**Where it appears:** `docs/falstad-format.md` §4 (the `c` capacitor element, `CapacitorElm.java:41-51,69-72`) and §7 (importer contract: "honor `seriesResistance` as a real series resistor").

**References:** Horowitz & Hill, *The Art of Electronics* (capacitor non-idealities); the falstad/circuitjs1 source (`CapacitorElm.java`).

### Shaft speed
- The rotational speed of a generator's mechanical drive (its spinning shaft), usually given in revolutions per minute or radians per second.

In an AC generator, the shaft spins magnets past coils, and every pass produces one cycle of the alternating output — so shaft speed, multiplied by the machine's number of pole pairs, directly sets the electrical frequency: f = (pole pairs) × (revolutions per second). For a programmer: frequency is not a free parameter you configure, it is a *derived value* of the mechanical simulation, like a frame rate that falls out of how fast the physics loop actually runs. This is why Manatee's curriculum ties the tablet's early "5 Hz flicker" mystery to rotation: a slow waterwheel makes low-frequency AC, and reaching a steady 50 Hz means governing the shaft to a fixed speed. Standard term in EE; also called rotational or mechanical speed, with "synchronous speed" the specific shaft speed that matches a target line frequency.

- **Where it appears:** `docs/curriculum.md`, Lesson Arc III.12 ("Frequency comes from rotation — shaft speed, pole pairs; why 50 Hz").
- **References:** Fitzgerald, Kingsley & Umans, *Electric Machinery*, McGraw-Hill (frequency/speed/pole relations for synchronous machines).

### Short circuit
- An unintended near-zero-resistance path across a source, letting current bypass the load and grow very large.

By Ohm's law, current is voltage divided by resistance, so as the path's resistance approaches zero the current is limited only by whatever small resistance remains — the wire itself and the source's internal resistance. The source's power then dissipates as heat in that remaining resistance (P = I²R), split between the wire and the source's own internal resistance — which is why shorted wires get hot, melt insulation, and start fires, and why shorted batteries themselves heat up and can rupture; fuses and breakers exist precisely to interrupt this. For a programmer, it is the electrical analogue of an unguarded tight loop: remove the thing that was throttling the flow and the system saturates until something burns out — and the protective fuse is the watchdog that kills it first. In Manatee's curriculum this is Lesson 3, the first hazard lesson: the tablet lets the player create the short and watch the wire heat safely in simulation, explaining a mishap they have likely already caused in the game world. Standard EE term; "short" and "dead short" are common aliases, and note the unrelated programming term "short-circuit evaluation" shares only the name.

- **Where it appears:** `docs/curriculum.md`, Lesson Arc I.3 ("The shocking truth about short circuits"); hazards discussion in `docs/design.md`.
- **References:** Horowitz & Hill, *The Art of Electronics*, Cambridge University Press.

### SI base units convention
- The rule that every numeric value in a Falstad circuit file is a plain number in SI units — ohms, farads, henries, volts, amps, hertz, seconds — with no engineering suffixes.

Electrical engineers habitually write component values with metric prefixes ("10k" for 10,000 Ω, "5u" for 0.000005 F), and SPICE netlists accept those suffixes directly. The Falstad text format does not: values are serialized as raw Java doubles (decimal point, `E`/`e` exponents, never a `+` sign in the exponent), so a 10 kΩ resistor is stored as `10000.0`. The friendly suffixed forms exist only in the editor UI, which formats and parses them at the display layer. For a programmer this is the familiar separation of wire format from presentation — like storing timestamps as epoch seconds and rendering "3 days ago" in the UI; for an EE it means the file is what a calculator sees, not what a schematic label shows. Manatee's importer honors this convention exactly and, when writing, additionally never emits `+` in exponents.

- **Where it appears:** `docs/falstad-format.md` §4 (units convention) and §7 (importer contract: parse doubles with InvariantCulture, no `+` in exponents on write).
- **References:** the SPICE convention it contrasts with is documented in the ngspice manual (netlist number formats and scale suffixes).

### simSpeed
A number in the Falstad file's `$` header line that controls how fast the circuitjs1 UI *animates* the simulation — it has no electrical meaning and Manatee ignores it.

The Falstad header is `$ flags maxTimeStep simSpeed currentSpeed voltageRange ...`. Of these, only `maxTimeStep` (the transient timestep in seconds) affects the mathematics; `simSpeed` is a double that circuitjs1 maps back onto a UI slider via `log(10*sp)*24+61.5` (`CircuitLoader.java:258-260`), pacing how much simulated time elapses per screen frame. For programmers: it is a playback-rate setting, like a video speed slider — change it and the answers are identical, only the wall-clock pacing differs. For EEs: it is the knob on the display, not a component in the circuit. Manatee's importer validates the header's arity and numerics but consumes only `maxTimeStep`, discarding `simSpeed` and the other display fields.

**Where it appears:** `docs/falstad-format.md` §2 (header line), §7 (importer treatment).

### SineDrive
The small immutable value type `SineDrive(double AmplitudeV, double FreqHz, double PhaseRad)` that tells a sinusoidal voltage source what waveform to produce.

It is passed to `AddSineSource(...)` when creating an AC source (which marks the island as AC, so it subcycles) and to `Netlist.Drive(VSourceId, in SineDrive)` to retune a live source. Changing a drive is cost tier 1 (right-hand-side only): the source's *value* changes but the circuit's connectivity and conductances do not, so no matrix refactorization is needed. The solver's source driver is *phase-continuous*: it keeps an exact per-source phase accumulator advanced each substep, so changing `FreqHz` mid-run bends the waveform smoothly instead of jumping — frequency is treated as a piecewise-constant input to a running phase integrator, the way a counter keeps counting when you change its increment. `PhaseRad` sets the phase offset; phase itself is source state and is carried through save/load via the source's `StateKey`.

**Project-specific type name**; the standard concept is a sinusoidal (SPICE `SIN`) independent-source specification — amplitude, frequency, phase.

**Where it appears:** `docs/api.md` §4 (type definition, line 249), §6 `AddSineSource`, `Netlist.Drive(VSourceId, in SineDrive)` (line 185, mirrored on the tier-1 capability object), and the frequency-change semantics in §22.b.

**References:** ngspice manual, independent sources (`SIN` transient specification); Nagel (1975), *SPICE2: A Computer Program to Simulate Semiconductor Circuits*, UCB/ERL-M520.

### single-phase AC
Alternating current delivered over a single sinusoidal voltage waveform (one "phase"), as opposed to three-phase systems where three offset waveforms overlap — everything in Manatee's Vintage Story AC design is single-phase.

The property that matters to us: with one phase, instantaneous power is not constant but pulsates at *twice* the electrical frequency. This is inherent to multiplying two sinusoids of the same frequency — p(t) = v(t)·i(t) always contains a 2f term, whatever the phase relationship between voltage and current; in the special case of a purely resistive (unity-power-factor) load, the power dips all the way to zero, and with a reactive load it can even swing negative, but it pulsates at 2f regardless. (In a three-phase system the phases' pulsations cancel and total power is smooth.) At the mod's 5 Hz electrical frequency that is a ~10 Hz power ripple — dangerously close to the ~100 ms mechanical network re-sum cadence. For programmers: sampling a 10 Hz oscillation at 10 Hz is a classic aliasing bug — you can land on the peaks, the zeros, or anywhere between, and read a phase-dependent constant offset that looks like phantom (or free) torque. This is exactly why the alternator's counter-torque read is defined as the mean over all electrical substeps since the last mechanical read (the normative averaging window), never an instantaneous sample.

The term is fully standard; the pulsating-power fact is textbook AC power theory.

**Where it appears:** `docs/vintage-story.md` §2 (mechanical coupling, "the averaging window is normative"), with the closed-loop monotonic-decay invariant test in `docs/testing-strategy.md`.

**References:** any introductory power-systems text on instantaneous vs. average power in single- and three-phase circuits.

### State-of-charge (SoC) integrator
- The part of a battery device that tracks its state of charge (SoC) — how "full" it is — by accumulating the current flowing in or out over time, a running total of charge.

Manatee models a battery behaviorally, not electrochemically: the solver sees an EMF source plus internal resistance, and the device's `Tick(dt)` accumulates `current × dt` into a stored charge value, which in turn moves the EMF and internal resistance along per-chemistry parameter curves (voltaic pile / lead-acid / li-ion). "Integrator" is the continuous-math name for a running sum; for a programmer it is an accumulator variable updated once per tick (`soc += I * dt`), the battery's one piece of mutable state, like a metered quota; for the EE it is standard coulomb counting. Because SoC is dynamic device state, it is part of the R6 snapshot/restore payload: a half-discharged battery saves and reloads half-discharged. In Stationeers, which does not persist cable networks, the network is rebuilt cold on load but battery SoC is restored from manatee-core snapshots keyed by network RefIds, with a failed restore degrading to a cold start of that island rather than a failed load. The chemistry progression arc later deepens the parameter sets, not the structure. The tutorial uses this to motivate the ExternalKey/StateKey split: the battery is *many components* (source, resistor, integrator — many ExternalKeys) but *one unit of saved state* (one StateKey, one snapshot blob).

**Standard term:** state of charge (SoC), universal in battery engineering; the accumulation technique is *coulomb counting*.
- **Where it appears:** `docs/solver.md` (Component Set: Batteries — "EMF + internal resistance + state-of-charge"); `docs/api.md` §18 (built-in `Battery` device); `docs/integration-tutorial.md` §3; `docs/stationeers.md` (Persistence — SoC integrators live in devices); snapshot/persistence requirement R6 in `docs/design.md`.
- **References:** Horowitz & Hill, *The Art of Electronics* (battery basics and internal resistance); coulomb counting is standard battery-management practice.

### solver primitives
- The fixed, closed set of circuit elements the solver core knows how to stamp directly into its equations: resistor, voltage source, current source, capacitor, inductor, switch, diode, and the ideal transformer two-port.

Each primitive has a *stamp* — a fixed recipe for adding its physics into the MNA matrix (the ideal transformer needs auxiliary rows for its turns-ratio constraint, because magnetic coupling cannot be built from independent two-terminal parts). Everything else players interact with — batteries, generators, converters, realistic transformers — is a `devices`-layer *composition* of these primitives plus behavioral logic, never a new matrix element. For programmers: the primitives are the instruction set of a small VM, and devices are programs written in it; keeping the instruction set closed keeps the numerical core small, testable against ngspice, and stable. For an EE: this mirrors SPICE's built-in element set, deliberately trimmed — no semiconductor-level device models in world circuits (a stated non-goal).

**Standard term:** SPICE calls these built-in *elements* (or element types); "primitive" is common in simulator-internals literature.
- **Where it appears:** `docs/solver.md` Component Set; stamps live in `Manatee.Core.Solver` (`docs/api.md`).
- **References:** Nagel (1975), *SPICE2: A Computer Program to Simulate Semiconductor Circuits*, UCB/ERL-M520; Ho, Ruehli & Brennan (1975), "The Modified Nodal Approach to Network Analysis", IEEE Trans. Circuits and Systems; Vlach & Singhal, *Computer Methods for Circuit Analysis and Design* (element stamps).

### SPDT switch (`S`)
A single-pole double-throw switch: one common terminal that connects to exactly one of two alternative terminals, selected by the switch position — Falstad element code `S`.

Where an ordinary on/off (SPST) switch is a boolean — connected or not — an SPDT switch is a one-of-two selector: think of a variable holding a reference to one of two targets, never both, never neither (unless the center-off flag adds a third "null" position). Electrically it routes the *pole* (common) to *throw 0* or *throw 1*. In the Falstad text format the `S` line carries the SPST parameters plus `link [throwCount]`: posts are `(x1,y1)` = common and `(x2,y2)` = throw 0, with throw 1 mirrored across the axis; a nonzero `link` gangs multiple switches so they move together; `flags&1` means center-off; `throwCount>2` means multi-throw. Manatee's importer accept/reject contract is deliberately narrow: it **rejects** `link != 0`, `throwCount > 2`, and the center-off flag, honoring only the plain two-throw form. (Electrical Age's importer instead substituted `S` with two complementary SPST switches — noted in our docs as their behavior, not ours.)

SPDT is the standard EE abbreviation (single-pole double-throw); a common alias is *changeover switch*.

**Where it appears:** `docs/falstad-format.md` §4 element table (`S` row) and §7 accept/reject contract.

**References:** P. Horowitz & W. Hill, *The Art of Electronics*, 3rd ed., Cambridge University Press, 2015 (switch nomenclature); the falstad/circuitjs1 source (`Switch2Elm.java`) is the format authority.

### Spring-damper coupling
- The model that keeps multiple generators on the same network rotating in step: each rotor behaves as if connected to the others by a spring (which pulls phases together) plus a shock absorber (which kills oscillation).

Each generator device carries a tiny mechanical state — rotor phase angle and angular velocity — integrated in the `devices` layer, outside the matrix solver. When two paralleled generators drift out of phase, the electrical power flowing between them acts like a restoring torque proportional to the phase difference (the "spring"), and a dissipative term proportional to the velocity difference (the "damper") drains the resulting swing so the pair settles into lockstep instead of oscillating forever. The solver never sees any of this mechanics: each generator just updates its voltage source's phase every tick — a cheap tier-1 (values-only) change. For the programmer: it's a PD controller, or two coupled damped oscillators converging to a shared equilibrium; the design calls the whole scheme "swing-equation-lite". Synchronization is deliberately a gameplay skill, so the dynamics must be stable but not invisible.

**Standard term:** this is the classical **swing equation** of power-system stability with explicit damping — synchronizing torque (the spring) and damping torque (the damper); "spring-damper" is the mechanical-analogy phrasing.

**Where it appears:** `docs/solver.md` (Analyses — "Generator paralleling"), `docs/design.md` (Simulation Model, same heading).

**References:** Kundur, *Power System Stability and Control*, McGraw-Hill, 1994 (swing equation, synchronizing and damping torque); Bergen & Vittal, *Power Systems Analysis*, 2nd ed., Prentice Hall, 2000.

### SPST switch (`s`)
- The Falstad text-format element code for a simple on/off switch — single pole, single throw: one input, one output, connected or not.

In the Falstad/circuitjs file format that Manatee's importer accepts, an `s` line carries the parameters `position momentary [label]` (label only if `flags&4`). The trap is the position encoding: **0 means closed (conducting), 1 means open** — inverted from the intuition that 1 = on. For the programmer: `position` is really an index into the switch's throw positions (which throw the pole rests on), not a boolean "is on" flag, which is why the SPDT `S` element reuses the same field for throw selection; treating it as `bool isOn` is the bug waiting to happen. A second quirk: legacy files write `true`/`false` in the position slot instead of `0`/`1`, and the canonical corpus contains both spellings, so the parser must accept either. Manatee honors `position`, and accepts `momentary` (a push-button that springs back) but treats it as a normal toggle with a warning, since the importer targets static circuits.

**Standard term:** SPST — single-pole single-throw switch, the universal EE name (aliases: on/off switch, toggle). The element code, field order, and inverted position sense are Falstad-format specifics, not ours.

**Where it appears:** `docs/falstad-format.md` §4 (element table, with `SwitchElm.java` line cites), §5.6 (true/false legacy quirk), §7 (importer accept posture).

**References:** Horowitz & Hill, *The Art of Electronics*, 3rd ed., Cambridge University Press, 2015 (switch nomenclature); the circuitjs1 source (`SwitchElm.java`), which is the format's only authoritative spec.

### Staggered rejoin / recloser lockout
- Two anti-oscillation mechanisms on browned-out loads: each dropped device waits a randomized delay before reconnecting (staggered rejoin), and a device that browns out repeatedly in a short window locks itself out until manually reset (recloser lockout).

Both guard against the same failure mode on an overloaded network. Without them, every constant-power load drops at the undervoltage threshold, the voltage recovers, every load rejoins simultaneously, the voltage collapses again — and the network strobes at tick rate. Staggered rejoin breaks the synchrony: each device's rejoin delay is `staggerBaseTicks` plus a per-device random spread, so loads trickle back and a marginally-overloaded net can find a stable subset. Lockout handles the genuinely-overloaded case: after `lockoutCount`-many brownouts in a short window, the device stops retrying and stays off until a player resets it, so the net collapses *legibly and stays collapsed* until load is shed. For the programmer: this is exactly retry with jitter plus a circuit breaker (the software-reliability pattern) applied to electrical loads — the same cure for thundering-herd retry storms. Both mechanisms live in the R18 adaptor's `AdaptedLoad`, alongside brownout hysteresis (drop at V_low, rejoin at V_high) and the anti-free-energy ledger.

**Standard terms:** staggered rejoin mirrors real load-shedding / cold-load-pickup practice; "recloser lockout" is taken directly from utility **automatic circuit reclosers**, which go to lockout after a set number of trip-reclose attempts. In distribution practice the recloser is protecting a *line*; ours guards a *load's* retry behavior — same state machine, different attachment point.

**Where it appears:** `docs/design.md` R18, `docs/integration-tutorial.md` §4 (`AdaptedLoad` parameters), `docs/api.md` (AdaptedLoad summary); origin in `docs/experiments/2026-07-05-adversarial-playtest.md`.

**References:** IEEE Std C37.60 (automatic circuit reclosers, lockout behavior); Nygard, *Release It!*, Pragmatic Bookshelf, 2007 (the Circuit Breaker stability pattern).

### stamp
- A component's fixed-shape contribution to the circuit equations: the small pattern of numbers a resistor, source, or capacitor adds into the MNA system matrix and right-hand-side vector.
- In Modified Nodal Analysis, the whole circuit's equations are assembled by having each component "stamp" its conductances and source terms into known matrix positions determined by its terminal nodes — a resistor of conductance G between nodes a and b adds +G to the (a,a) and (b,b) diagonal entries and −G to (a,b) and (b,a). For a programmer: a stamp is a component's `visit` contribution in an accumulate-into-a-shared-data-structure pattern; the matrix is built as coordinate storage with duplicate-summing stamps, and stamps are **versioned** so an unchanged (pattern, values, dt) triple skips straight to the cached factorization — a cache key over the assembly step. In Manatee, components provide a *time-domain* stamp per analysis (DC and Backward-Euler transient); a complex-frequency (phasor) stamp is reserved in the interface for a later metering optimization but not implemented. Because the summed stamps encode Kirchhoff's current law, the testing strategy treats KCL residual checks as the standing tripwire for stamp bugs.
- **Standard term** — universal in SPICE-family simulator literature ("element stamps", "stamping").
- **Where it appears:** `docs/design.md` R2; `docs/solver.md` (Layering, Analyses, Change-Cost Tiers, Numerics); `docs/stationeers.md` (Integration Seams, Legacy-Device Adaptor); `docs/testing-strategy.md` (KCL invariant checks).
- **References:** Ho, Ruehli & Brennan (1975), "The Modified Nodal Approach to Network Analysis", *IEEE Trans. Circuits and Systems*; Vlach & Singhal, *Computer Methods for Circuit Analysis and Design*; Pillage, Rohrer & Visweswariah, *Electronic Circuit and System Simulation Methods* (tabulates standard element stamps).

### stamp / restamp (verbs)
- The act of writing components' contributions into the solver's matrix and right-hand side ("stamping"), and of doing it over from scratch after a rebuild ("restamping").
- Manatee keeps the retained **document** (the netlist description) as the sole source of truth; matrices are derived artifacts, stamped from the document at `Solve`. This ordering carries a load-bearing guarantee: mutation verbs (`Drive`, `Adjust`, `Reconfigure`) write the document, so when an island is torn down and rebuilt, the rebuild *restamps* from the document and **no verb write is ever lost** — even writes made through handles on an island already marked for rebuild land safely, because the restamp reads the document, not the doomed matrix. For a programmer: the document is the database and the stamped matrix is a materialized view — writes go to the database, and rebuilding the view can never drop a committed write. For an EE: the schematic is authoritative and the solver's internal equations are always regenerated from the schematic, never patched in a way the schematic doesn't reflect.
- **Standard term** ("stamp" as a verb is standard SPICE vocabulary); "restamp" is our shorthand for re-running assembly after a tier-3 rebuild, which any simulator does but rarely names.
- **Where it appears:** `docs/api.md` §2 (Solver namespace), §4 (document-is-truth rule), §16–§17 (rebuild windows and the Reconfigure-to-Solve story); `docs/integration-tutorial.md` §1.
- **References:** Ho, Ruehli & Brennan (1975), "The Modified Nodal Approach to Network Analysis", *IEEE Trans. Circuits and Systems*; Nagel (1975), *SPICE2: A Computer Program to Simulate Semiconductor Circuits*, UCB/ERL-M520.

### steady-state
- The converged quiet regime of a running circuit, where each tick costs only cheap right-hand-side back-substitutions on an already-factorized matrix — the target the performance contract is written against.

Mechanically: the solver factorizes the circuit's matrix (an expensive LU decomposition) only when conductances or topology change. If nothing changes but source values and stored energy (tier 1), each tick reuses the cached factorization and does only a forward/back substitution — like re-running a compiled query with new parameters instead of recompiling it. Our executable definition is `Refactorizations == 0 && IslandRebuilds == 0` in `TickStats` on quiet ticks; CI asserts it and integrators are told to log it. At post-compaction sizes these steady-state solves are microseconds. A nonzero `Refactorizations` in steady state means some device's per-tick parameter update is not converging; a nonzero `IslandRebuilds` means a breaker/coupler fired.
- **Relation to the standard EE term:** in circuit theory, *steady state* means the circuit's response after transients have decayed (DC steady state, or sinusoidal steady state for AC). Our usage is the computational shadow of that: a circuit in electrical steady state induces the tier-1-only solver regime, but we define the term operationally by the counters, not by the waveforms. See also **steady state (per-tick)** for the companion zero-allocation contract.
- **Where it appears:** `docs/stationeers.md` (Performance / design consequences), `docs/integration-tutorial.md` §9 ("The performance contract"), `docs/design.md` R8, `docs/solver.md` (Change-Cost Tiers).
- **References:** Pillage, Rohrer & Visweswariah, *Electronic Circuit and System Simulation Methods* (McGraw-Hill), on factorization reuse vs. re-solve cost; Davis, *Direct Methods for Sparse Linear Systems* (SIAM), on symbolic/numeric factorization separation.

### subcircuit model (`.`)
- In the Falstad/circuitjs1 text format, a line beginning with `.` defines a reusable subcircuit — a named block of circuitry that can be instantiated multiple times, like a function in code or a packaged module on a board. Manatee's importer rejects these lines loudly.

  A subcircuit lets a file define, say, a filter stage once and then place several instances of it, each expanding into the full internal circuit at load time. circuitjs1 handles `.` lines in its pre-element dispatch (`CircuitLoader.java:149-193`) alongside other non-element line types. Manatee does not support subcircuits: our importer's contract is to *reject loudly* — with line number, offending token, and a one-line reason — rather than skip silently, because silently ignored content is how dialect drift and corrupted-looking circuits start. For a programmer: it is a macro/function definition in a format where we only accept straight-line code, and we fail the whole import with a clear error instead of executing half a program.

  Standard concept: this is the same idea as a SPICE `.SUBCKT` definition (hierarchical netlist reuse); the `.` line prefix is circuitjs1's own encoding of it.

- **Where it appears:** `docs/falstad-format.md` §1 (non-element line types) and §7 (the reject-loudly list in the importer accept/reject contract).
- **References:** Nagel (1975), *SPICE2: A Computer Program to Simulate Semiconductor Circuits*, UCB/ERL-M520 (the `.SUBCKT` lineage); the ngspice manual, chapter on subcircuits.

### subcycled AC / subcycling
- Manatee's way of simulating an alternating-current island: within each coarse game tick (~50 ms), the solver runs N small time-domain substeps — enough to trace the actual voltage/current waveforms — rather than solving the AC behavior symbolically with phasors.

The game tick is far too coarse to resolve a sine wave, so each AC island runs N substeps per tick, with N chosen per island from its highest source frequency at ≥ 20 samples per cycle (a 5 Hz waterwheel needs ~5 substeps per tick; ~50 Hz needs ~50). This is waveform-first by design (R3): a 5 Hz lamp visibly flickering, an oscilloscope showing real waveforms, and power pulsating at 2f as it does physically are the educational point — phasor (complex-number, steady-state) analysis is only a later metering optimization. Performance rests on a cache invariant: with a fixed substep dt the Backward Euler companion conductances stay constant, so a linear island keeps its LU factorization cached and each substep is a cheap right-hand-side update (tier 1) — like re-running a query against a warm cache instead of rebuilding the index. N is quantized with hysteresis (it changes only when the required rate drifts past ~±15%), so mechanical-speed jitter never invalidates that cache; the source driver instead tracks exact frequency through its phase-continuous per-substep phase increment. A change of N is a deliberate, rare tier-2 (refactorization) event.

Several contracts hang off the substep: current limits and the i²t integral are evaluated *per substep* (instantaneous-trip events coalesce to at most one per tick carrying the worst substep value); `WaveformTap`s sample per substep into caller-owned rings; coupled islands substep in lockstep as one work unit. N is solver-owned and never client-settable — the read-only `SubstepPlan` (N, `SubstepDt`, hysteresis band; band 0 for DC) is exposed on the island handle.

**Project-coined term**, closest standard concept: oversampled time-domain (transient) simulation with inner time-stepping — ordinary SPICE-style transient analysis (`.tran`, not `.ac`) run at a finer step inside each outer game step. "Subcycling" elsewhere in numerical literature usually means *different parts* of one system advancing at different rates (multirate integration); ours is the simpler whole-island-at-a-finer-rate case.
- **Where it appears:** `docs/design.md` R2, R3, Simulation Model (AC), and Performance; `docs/solver.md` Analyses (Subcycled AC) and Change-Cost Tiers; `docs/api.md` §5 (Mixed profile), §11 (`SubstepPlan`), §12 (per-substep limit evaluation), §13 (`WaveformTap` / oscilloscope tap).
- **References:** Nagel (1975), *SPICE2: A Computer Program to Simulate Semiconductor Circuits*, UCB/ERL-M520 (time-domain transient analysis with companion models); Pillage, Rohrer & Visweswariah, *Electronic Circuit and System Simulation Methods* (transient vs. phasor/AC analysis); Horowitz & Hill, *The Art of Electronics* (AC waveform behavior).

### SWER / two-wire / ground-return
- Three wiring idioms describing how current gets back to its source: over a second routed conductor (**two-wire**), through the earth itself (**SWER** — single-wire earth return, also "ground-return"/"earth-return"), or through an implicit reference node (Stationeers). SWER uses one metallic conductor for supply and the earth as the return path.

Every circuit needs a complete loop; SWER saves copper by letting current return through the ground, entering and leaving the soil via grounding electrodes. In Manatee earth is an ordinary solver node reached through per-electrode contact resistance (tens of ohms, lower when wet), so SWER is simulated honestly rather than as a free return path — each electrode is a fixed "tax" in series with the load. Whether SWER works is therefore pure arithmetic: at 240 V distribution the load resistance dwarfs the electrode resistance, and at milliamp signalling levels (telegraph, doorbell, electric fence — historically real SWER territory) the current is too small to matter; but a 12 V lamp of a few ohms behind two ~20 Ω electrodes receives only a few percent of its rated power. Curriculum Lesson 14 makes the player run exactly this calculation ("copper is expensive — when can you get away with one wire?") after they have personally suffered the 12 V version. For a programmer: the electrodes are an unavoidable per-hop overhead, so the scheme only pays when the payload is large relative to the overhead — the same shape as batching over a high-latency link.

The three idioms differ per client. **Vintage Story defaults to two-wire**: the cable pathfinder routes conductor *pairs*, so one placement gesture lays both supply and return with no extra friction (R13), both are real solver nodes, and islands float (a ~1 MΩ implicit leak per device negative keeps the matrix mathematically grounded). SWER remains a deliberate, user-visible choice there — the grounding model was revised (2026-07-05, adversarial finding C1) after a SWER-first version failed the 12 V arithmetic, so SWER now earns its keep only where it does in reality. **Stationeers** offers no wiring choice at all: vanilla devices have a single power terminal, so Re-Volt uses an *implicit* ground-return with a near-ideal return conductance per network — numerically identical to modeling the return conductor while the fiction describes a normal cable bundle. For a programmer: two-wire vs SWER is explicit versus shared-implicit resource routing — SWER "deduplicates" the return path onto a lossy shared medium whose loss (electrode resistance) is the whole trade-off; VS exposes that cost as gameplay, Stationeers zeroes it out by design.

"SWER" and "earth/ground-return" are standard power-engineering terms; "two-wire idiom" is our phrasing for the routed-pair default. See also **electrode-loss arithmetic** (the predict-then-observe calculation) and **Milliamp signalling** (the low-current niche).
- **Where it appears:** `docs/curriculum.md` Lesson Arc III, Lesson 14 (the ground-return coda); `docs/design.md` "Grounding model" (the settled-and-revised decision — VS choice, electrode physics, Stationeers implicit ground-return — R12/R13); `docs/api.md` §5 (netlist terminal binding: routed return nodes vs reference node).
- **References:** L. Mandeno, "Rural Power Supply Especially in Back Country Areas," *Proceedings of the New Zealand Institution of Engineers* (1947) — the classic paper on rural SWER distribution; Horowitz & Hill, *The Art of Electronics* (grounds and returns).

### Swing equation / swing-lite
- A simplified model of a spinning generator's rotor dynamics, used so that connecting a second AC generator onto a live network (generator *paralleling*) requires real synchronization skill rather than working automatically.

The textbook *swing equation* describes how a synchronous generator's rotor angle accelerates or decelerates with the imbalance between mechanical input power and electrical output power. Manatee's **swing-lite** keeps just the essential state — each generator carries a phase angle and an angular velocity — coupled to the network through a spring-damper law: connect while nearly in phase and the spring gently pulls the rotors into lockstep (the real phenomenon of pulling into synchronism); connect badly out of phase and the spring yanks violently, producing the large transient currents and torques that make real switchboard operators sweat. In the API the `Alternator` device holds this rotor state and drives a `SineDrive` from it, so the electrical waveform follows the mechanical rotor rather than an ideal clock; the model lives in the `devices` layer, outside the core solver, which only ever sees cheap tier-1 source-phase updates. For a programmer: each generator is a small ODE-integrated state machine (angle, velocity), and "the grid" is the message bus through which their restoring forces couple — synchronization is convergence of these state machines, which is why joining two badly-matched generators is like force-merging two divergent replicas: the reconciliation is violent. "Synchronization is a gameplay skill" (design.md) means the player, like a real operator, must match phase before closing the switch.

Because ngspice simulates circuits, not rotating machinery, **swing-lite coupling is one of the things the ngspice oracle explicitly *cannot* verify**: the rotor integration and its spring-damper pull toward network phase have no SPICE-netlist expression, so (alongside behavioral two-ports and limit events) it falls in the oracle gap and is covered instead by conservation invariants and hand-computed cases documented inline with their arithmetic, so a reviewer can recheck them by calculator.

Standard term: the **swing equation** (classical second-order generator model) is standard power-systems vocabulary; **swing-lite** / **swing-equation-lite** is the project's name for this deliberately reduced gameplay version.
- **Where it appears:** `docs/design.md` "Generator paralleling" (Simulation Model); `docs/solver.md` (Analyses, devices layer); `docs/api.md` §18 (`Alternator`: swing-lite rotor state → `Drive(SineDrive)`); `docs/curriculum.md` lesson 17; `docs/testing-strategy.md` (Oracle Tests, "what ngspice cannot oracle").
- **References:** P. Kundur, *Power System Stability and Control*, McGraw-Hill (1994) — the standard treatment of the swing equation and generator synchronization; the ngspice manual (for what the oracle can and cannot express).

### switch on/off resistance
- The finite resistances the solver uses to represent a switch's two states — 1 mΩ closed, 1 GΩ open — because in MNA a switch is never a true short or a true break, just a very small or very large resistor.

An ideal open switch (infinite resistance) would delete equations from the system (singular matrix) and an ideal closed one (zero resistance) would merge two nodes; both would be topology changes. Using finite values instead keeps the switch permanently in the matrix, so toggling it is a cheap conductance update (a tier-2 change — see **switch (relay contact) / netlist switch**) rather than a rebuild. For a programmer: it is clamping a boolean into a saturating numeric range so downstream arithmetic never divides by zero or loses a row. The specific values are policy-driven, not SPICE defaults (ngspice ships RON = 1 Ω, ROFF = 1 TΩ): a doc-review round (2026-07-05) tightened them from an earlier 1e9 S / 1e-12 S spread — 21 decades, which "hits the conditioning cliff" on exactly the pathological player-built circuits R9 promises to survive — to the endpoints of the conductance-range policy, [1e-9, 1e3] S, deliberately spanning only 12 decades so double-precision arithmetic (~16 significant digits) stays well-conditioned. Mixing numbers 21 orders of magnitude apart in one linear solve is like adding a tiny float to a huge one — the small contribution rounds away — so narrowing the spread is an input-sanitization invariant on the matrix. The same ~1 mΩ short also stands in for an inductor at DC operating point, where an ideal inductor is a plain wire.

**Standard term:** switch on-resistance / off-resistance (SPICE `RON`/`ROFF` on the S/W switch elements); the finite-resistor approach is standard SPICE practice, though our values are policy-driven rather than SPICE defaults.
- **Where it appears:** `docs/solver.md` (`AddSwitch` in The Netlist API; the conductance-range policy in Numerics; the DC operating point in Analyses; the relay-vs-breaker duality in Islands); `docs/design.md` (Open Questions / doc-review resolutions: "switch on/off resistances tightened to SPICE-conventional values").
- **References:** Nagel (1975), "SPICE2: A Computer Program to Simulate Semiconductor Circuits", UCB/ERL-M520; the ngspice manual (voltage-controlled switch, RON/ROFF parameters); Vlach & Singhal, "Computer Methods for Circuit Analysis and Design" (conditioning of nodal matrices); Pillage, Rohrer & Visweswariah, *Electronic Circuit and System Simulation Methods*.

### switch (relay contact) / netlist switch
- Manatee's in-matrix switch element: a two-terminal component that lives as an ordinary element *inside* one island's matrix, toggled at runtime via `Adjust(SwitchId, bool)` by changing its resistance (1 mΩ closed / 1 GΩ open) — as opposed to a coupling device, whose open/close changes which islands exist.

Because the component never leaves the system of equations — only a number in the matrix changes — toggling it is a **tier-2** change (numeric refactorization, no structural rebuild), cheap enough to be the hot path for elevator-style relay-logic control. This is one half of the project's **relay-vs-breaker duality** (settled 2026-07-05): same physics, different API citizenship. A *relay contact* is this netlist switch, cheap and in-matrix; a *breaker* is instead a coupling device whose opening splits islands apart — an honest tier-3 island rebuild, acceptable because breakers, as safety devices, change state rarely. For a programmer: the relay is a value update in an existing data structure (a cheap in-place field update), the breaker is a schema change (it changes the partitioning of the whole dataset). In SPICE terms a netlist switch is the standard voltage/current-controlled switch element (`SW`/`CSW`) modeled as two resistance states (Ron/Roff), with the on/off values chosen per the conductance-range policy (see **switch on/off resistance**).

The "netlist switch" term is a project shorthand for the API role, contrasted with "breaker"; the underlying model is the standard SPICE switch.
- **Where it appears:** `docs/solver.md` §3 (`AddSwitch`), Numerics, and the relay-vs-breaker duality bullet; `docs/stationeers.md` "Islands and Coupling Devices" ("a breaker is a coupling device, not a netlist switch"); `docs/testing-strategy.md` (duality equivalence test); `docs/api.md` §4 (`SwitchId`, `[CostTier(2)] Adjust(SwitchId, bool)`); `docs/design.md` §4/§7 (elevator relay logic).
- **References:** the ngspice manual (the S/W switch elements, modeled as Ron/Roff resistances); Nagel (1975), *SPICE2: A Computer Program to Simulate Semiconductor Circuits*, memo UCB/ERL-M520.

### synchronization
- The act of bringing two AC generators onto the same grid so they spin in lockstep at the same frequency and phase — the capstone skill of the tablet curriculum (Lesson 17).

Two generators feeding one grid must agree on frequency and phase; if a generator is connected while out of phase, large corrective currents flow to yank it into step (violently, in the worst case). Manatee models this with a "swing-equation-lite" scheme: each generator carries a phase angle and angular velocity as state, coupled to the network through a spring-damper (design.md, Generator paralleling). For programmers: think of two clocks that must be phase-locked before you can join their outputs — the electrical network acts like a spring pulling the rotors toward agreement, and connecting them badly is like force-merging two divergent replicas: the reconciliation traffic is the damage. In the game, performing this correctly is a deliberate gameplay skill, not automated away.

This is the standard power-engineering term (also "paralleling" or "synchronizing" a generator); the underlying rotor dynamics are the classical *swing equation*.
- **Where it appears:** curriculum.md Lesson Arc III, Lesson 17 ("the spring between rotors"); design.md, Generator paralleling; vintage-story.md, The tablet (curriculum ordering).
- **References:** P. Kundur, *Power System Stability and Control*, McGraw-Hill, 1994 (swing equation, synchronization).

### Tap
- A point along a run of conductor where something else connects — a branch, a device terminal, or a probe attachment that must remain a real node.

In the compaction (reduction) layer, long chains of series resistor edges are collapsed into single equivalent resistors to shrink the solver matrix. A chain is only collapsible if its interior is *unobserved and unbranched*: every interior node has exactly two connections, both part of the chain. A tap is any interior node that violates this — a third wire joins there, or a device attaches there — so the chain must be split at the tap and only the tap-free sub-runs collapse. Programmers can think of it as inlining a function: you may inline straight-line code, but any point another caller jumps into must stay a real label. EEs will recognize the everyday sense: a tap on a line (or a transformer winding tap) is a mid-span connection point, and here it marks exactly the nodes series reduction must preserve. Note that purely *observational* taps are cheaper than they look: probe interpolation lets instruments read voltages inside a collapsed run without keeping the node, so only genuine electrical connections block collapse.

The term is standard EE usage (a mid-conductor connection point, as in "tapping a line"); in graph terms it is an interior vertex of degree > 2 that terminates a series-reducible path.

**Where it appears:** `docs/compaction.md` — Responsibilities #2 (series-chain collapse: "runs of resistor edges with no taps reduce to single equivalent resistors") and #3 (probe interpolation); as-built contract in `docs/api.md` §19.

**References:** Series/parallel graph reduction is classical network analysis; see Vlach & Singhal, *Computer Methods for Circuit Analysis and Design*, and Pillage, Rohrer & Visweswariah, *Electronic Circuit and System Simulation Methods* (node-reduction techniques).

### Terminal
- A connection point of a component or device — the places where external wiring may attach, such as a battery's positive and negative, a diode's anode and cathode, or a transformer's four port nodes.

In manatee-core the word carries API weight: a `Device` (the layer above raw netlist primitives) declares its terminals via `TerminalSpec`, which states how many connection points the device has (`Count`) and which of them are *return* terminals (`ReturnTerminals`) — the indices the per-client `WiringPolicy` treats as the current-return side, e.g. auto-binding them to the reference node in Stationeers or stamping the 1 MΩ earth leak in Vintage Story. The caller supplies the actual node IDs for each terminal when the device is built (`Build(..., ReadOnlySpan<NodeId> terminals, ...)`), and those terminal nodes are *caller-owned* — the device wires its internal components between them but does not create them. For a programmer: a terminal list is a function signature — the device's public parameters — while everything inside `Build` is local variables; `TerminalSpec` is the type declaration that lets policy be applied declaratively rather than per-device. This is the standard EE meaning of *terminal* (aliases: pin, lead, port node); the docs also use "two-terminal element" in the standard sense when noting that a transformer's magnetic coupling cannot be composed from independent two-terminal R/L/C/V/I primitives.

**Where it appears:** `docs/api.md` §5 (WiringPolicy and where `TerminalSpec.ReturnTerminals` fires), §6 (primitives; two-terminal vs. multi-terminal), §18 (devices-layer contract: `TerminalSpec`, `Device.Terminals`, `Build`); `docs/solver.md` (devices layer = terminals + create/remove components).

**References:** Ho, Ruehli & Brennan (1975), "The Modified Nodal Approach to Network Analysis", *IEEE Trans. Circuits and Systems* (elements defined by their terminal nodes); Vlach & Singhal, *Computer Methods for Circuit Analysis and Design*.

### terminal behavior
The externally observable electrical response of a network at its connection points (terminals) — the voltages and currents anything plugged into it can see, ignoring everything about its interior.

In Manatee this is the correctness contract for the compaction (reduction) layer: reduction must be *semantically invisible*, meaning the raw graph and the reduced graph produce identical terminal behavior, and probes into the reduced interior must agree with the raw interior. For programmers: it is the same idea as an interface contract — two implementations (raw vs. reduced circuit) are interchangeable as long as every observation through the public API (the terminals and probes) is identical; the reduction is a cache/compression of internal state that must never leak. For EEs this is the standard equivalence notion behind Thévenin/Norton equivalents and series/parallel collapse: replace a subnetwork with a smaller one that presents the same V–I relationship at its ports. Both directions of the guarantee are enforced by standing equivalence tests, not by trusting the math.

This is a standard EE term; related standard vocabulary: *port behavior*, *terminal equivalence*, *V–I characteristic at the terminals*.

**Where it appears:** `docs/compaction.md` (Invariants section) and `docs/api.md` §on reduction invariants; enforced by equivalence tests described in `docs/testing-strategy.md`.

**References:** Vlach & Singhal, *Computer Methods for Circuit Analysis and Design* (network equivalence and reduction); Horowitz & Hill, *The Art of Electronics* (Thévenin equivalents as terminal-equivalent replacements).

### Terminal behavior boundary (and "device negative")
- The device's connection points form the boundary at which the simulation's answers are guaranteed exact: compaction may rewrite everything *inside* a conductor run, but what a device sees at its terminals must be unchanged.

The reduction layer's core invariant is that compaction is *semantically invisible*: the raw geometry graph and the reduced netlist must produce identical terminal behavior — same voltages and currents at every point where a device or source attaches — and probe readings inside collapsed runs must agree with what the raw interior would show. In programming terms, terminals are the public API and collapsed interior nodes are private implementation: the optimizer (compaction) may transform the implementation freely so long as the observable interface contract holds, and standing equivalence tests in CI enforce exactly that. An EE will recognize this as Thévenin/Norton one-port (two-terminal) equivalence: the collapsed chain reduces to a two-terminal equivalent indistinguishable from the original at its terminals. "Device negative" is the specific terminal on the return side of a device (declared via `TerminalSpec.ReturnTerminals`, or the `neg` argument of a source); it matters because the grounding model hangs policy on it — in Vintage Story every device negative gets an implicit ~1 MΩ leak to earth to keep the matrix grounded, and in Stationeers it binds to the reference node through a near-ideal return conductance. Both stamps attach *at the terminal*, so they survive any interior compaction.

**Standard term:** terminal equivalence of a reduced network (Thévenin/Norton one-port equivalents); "device negative" is the ordinary negative terminal / return lead.

**Where it appears:** `docs/compaction.md` — Invariants ("identical terminal behavior"); `docs/api.md` §19 (same invariant, as-built) and §5 (`NodeRole.Return`, `WiringPolicy`); `docs/design.md` — Grounding model (leak from each device negative to earth).

**References:** Thévenin/Norton equivalence and network reduction: Vlach & Singhal, *Computer Methods for Circuit Analysis and Design*; Horowitz & Hill, *The Art of Electronics*, ch. 1 (equivalent circuits at a pair of terminals).

### Thermal accumulator (i²t)
- A per-component running integral of overload heating — accumulating roughly current-squared times time — used to model things that fail from sustained overcurrent (fuses melting, cables overheating) rather than from a single instantaneous spike.

Real conductors have thermal inertia: a wire tolerates 2× its rated current for a moment but melts if that persists, and fuses are specified by exactly this "i²t" (ampere-squared-seconds) let-through energy. Manatee models this with one shared mechanism: each tick, if current exceeds the component's rating the accumulator heats by `(I² − rating²)·dt`; below rating it cools exponentially (`acc·dt/τ`); crossing the melt threshold raises a `ThermalI2t` limit event after the solve. For a programmer: it is a leaky-bucket rate limiter — sustained excess fills the bucket, idle time drains it, and overflow fires an event. The solver never mutates the circuit in response; consequences (the fuse pops, the cable burns) are the client's job. Because fuses and cable heating share this mechanism, a weak lead segment spliced into a copper run *is* a fuse with zero special-casing. Accumulator state rides the per-island snapshot so partial melt survives save/load.

Standard term: this is the standard fuse/protection concept **i²t (Joule integral / let-through energy)**; the leaky-bucket cooling term is our addition over the textbook adiabatic model.

**Where it appears:** `docs/solver.md` (State, Limits, Probes — `LimitConfig`), `docs/api.md` §12 (`LimitSpec.Thermal`, `LimitKind.ThermalI2t`), `docs/compaction.md` (envelope aggregation).

**References:** Horowitz & Hill, *The Art of Electronics*, 3rd ed. (fuse ratings and i²t); IEC 60269 (low-voltage fuses) specifies fuse behavior in i²t terms.

### Thermal mass
- A conductor's capacity to absorb heat before it melts — how much sustained overload energy it can soak up — as distinct from the instantaneous current it can carry continuously (its ampacity).

In Manatee this is expressed through the i²t limit: a component's melt threshold is the size of its "heat budget", while its rating sets where the budget starts draining. The distinction matters for compaction: in a mixed-material series chain, instantaneous ampacity, i²t thermal mass, and melting threshold can each be governed by a *different* segment, so the collapsed element's limit envelope tracks them separately per limit type (and the i²t case becomes a Pareto set — see that entry). For a programmer: ampacity is a hard rate limit, thermal mass is buffer capacity — a thin wire may reject a spike instantly while a thick one absorbs the same spike but eventually overheats under sustained load. This is exactly why a small lead segment in a copper run acts as a fuse: less thermal mass, so its heat budget exhausts first.

Standard term: **heat capacity / thermal capacitance** of the conductor; in protection engineering it appears as the conductor's **i²t withstand** (adiabatic short-circuit rating).

**Where it appears:** `docs/compaction.md` (Responsibilities #4, limit attribution); underlies the i²t accumulator in `docs/solver.md` and `docs/api.md` §12.

**References:** Horowitz & Hill, *The Art of Electronics*, 3rd ed. (wire ratings and fusing); IEC 60269 for fuse i²t withstand.

### Thermal network / thermal-RC
- Modeling heat flow with the same mathematics as an electrical circuit: temperature plays the role of voltage, heat flow the role of current, thermal resistance and heat capacity the roles of resistors and capacitors.

Heat conduction and electrical conduction obey the same linear equations, so a network of thermal masses connected by conductive paths is literally an RC circuit to the solver — no new math is required, only new labels on the numbers. For programmers: this is classic interface reuse — the solver operates on an abstract "potential/flow" graph, and electrical and thermal are just two concrete instantiations of the same data structure and algorithm. EEs use exactly this trick routinely (e.g. computing a transistor's junction temperature from a datasheet's thermal-resistance model). In Manatee this is a *future* possibility, not a current feature: a deferred chemical/phase-change arc might reuse the solver for heat, and its main present-day effect is a naming rule — the core's deepest layers say "potential" and "flow" rather than "voltage" and "current" where that is cheap.

**Standard term:** thermal–electrical analogy (thermal RC network, Cauer/Foster thermal models).
- **Where it appears:** `docs/design.md` (Ruins loot / future arc, Deferred by design), `docs/solver.md` (Layering), `docs/api.md` §1 and the `Potential(NodeId)` alias.
- **References:** Horowitz & Hill, *The Art of Electronics* (thermal resistance / heat-sink calculations use this analogy); Incropera & DeWitt, *Fundamentals of Heat and Mass Transfer* (thermal circuits).

### throwCount
A field in Falstad's `S` (SPDT switch) element line giving the number of *throws* — the number of distinct output contacts the switch's common terminal can connect to.

In switch terminology, "poles" are how many independent circuits a switch controls and "throws" are how many positions each pole can select: a light switch is single-throw (on/off), an SPDT changeover switch has two throws, and a rotary selector has many. In the Falstad text format the `S` element carries `link [throwCount]` parameters; `throwCount > 2` marks a multi-throw (rotary) switch. For a programmer, a throw count is just the size of the enum a selector variable can take — SPST is a boolean, SPDT picks between two branches, a rotary switch is an n-way `switch` statement wired in copper. Manatee's importer **rejects** `throwCount > 2` (along with ganged switches (`link != 0`) and the center-off flag): our accept set stops at plain SPST/SPDT.

**Standard term:** this is standard switch vocabulary — the "T" in SPST/SPDT/DPDT stands for throw; `throwCount` is Falstad's field name for it (`Switch2Elm.java`).

**Where it appears:** `docs/falstad-format.md` §4 (the `S` element row) and §7 (importer accept/reject contract).

**References:** Horowitz & Hill, *The Art of Electronics*, 3rd ed. (switch nomenclature, ch. 1); the falstad/circuitjs1 source (`Switch2Elm.java`).

### Time constant
- The characteristic time (written τ, "tau") over which a circuit with storage — a capacitor or inductor — charges or decays toward its steady state.

For a resistor–capacitor pair τ = RC; for a resistor–inductor pair τ = L/R. After one time constant the response has covered about 63% of the way to its final value, and after ~5τ it is effectively settled — think of it as the half-life of the circuit's transient behavior, or in programming terms the decay rate of an exponentially-smoothed moving average. Manatee cares about time constants relative to the solver's timestep: Vintage Story lesson component values are deliberately scaled ("the EA trick") so τ lands at human-visible 0.1–10 s, where Backward Euler at a 50 ms tick is accurate and players can watch capacitors charge. Conversely, transformer parasitics have ~ms time constants — shorter than an AC substep — so they are modeled honestly but never relied on for inter-island stability.
**Standard term:** yes — universally standard in circuit theory.
- **Where it appears:** `docs/design.md` (Simulation Model, transient), `docs/solver.md` (Islands — transformer parasitics), `docs/curriculum.md` (Format — component value scaling).
- **References:** Horowitz & Hill, *The Art of Electronics*, ch. 1 (RC circuits); any introductory circuits text (e.g. Nilsson & Riedel, *Electric Circuits*).

### Topology change
A structural edit to the circuit graph itself — adding or removing components, nodes, or cables — as opposed to merely changing a value on an existing component.

In MNA terms, topology determines which rows and columns the system matrix has and where its nonzeros sit (the sparsity pattern); a topology change therefore forces a full symbolic + numeric rebuild of the affected island's factorization, the most expensive tier of edit. Manatee makes this cost legible as **tier 3** of the change-cost tiers (R4): tier 1 (value changes) reuses the cached LU, tier 2 (conductance changes) refactors numerically with the pattern reused, tier 3 rebuilds everything and is *batched* — collected and applied once per tick (`BeginBulkUpdate` / the `Edit` verb). Programming analogy: tier 1 is updating a row in a table, tier 2 is rebuilding an index, tier 3 is a schema migration — you batch schema migrations. Note our tier boundary: a switch/relay toggle is modeled as a conductance change (tier 2), not a topology change, even though electrically it "opens the circuit"; only edits that add/remove matrix rows or nonzero positions are tier 3. A *breaker* is deliberately on the other side of that line: per solver.md's relay-vs-breaker duality, a breaker is an island coupling device, not a netlist switch — tripping it splits or joins islands, which is an island rebuild (tier 3, "the honest cost"), acceptable because breakers are safety devices that change state rarely. In the Re-Volt integration, the game's dirty-driven `Initialize` phase maps to tier 3.

This is a standard concept in circuit simulation (changes to the network graph / sparsity pattern); our contribution is naming its cost tier explicitly in the public API.

**Where it appears:** `docs/design.md` R4, R11; `docs/solver.md` "Change-Cost Tiers", the netlist API, and the relay-vs-breaker duality; `docs/stationeers.md` integration seams; `docs/api.md` §6 (Edit/receipts).

**References:** Ho, Ruehli & Brennan (1975), "The Modified Nodal Approach to Network Analysis", IEEE Trans. Circuits and Systems; Pillage, Rohrer & Visweswariah, *Electronic Circuit and System Simulation Methods* (symbolic vs. numeric factorization).

### trace
- One curve drawn on an oscilloscope display: the plotted history of one measured signal (voltage or current) over time.

In Manatee a trace is what one probe produces when its samples are drawn on the tablet's scope: a time series rendered as a waveform curve. Programmers can think of it as a live line chart backed by a ring buffer of probe samples; EEs should recognize it as exactly the standard scope-channel sense of the word. The term matters to us because the oscilloscope contract is fixed at **two probes** (design.md doc-review, 2026-07-05), which exists precisely so the phase lessons (curriculum lessons 15 and 17) can overlay two traces and let the player see one waveform leading or lagging the other. The plural is load-bearing: comparing phase requires two simultaneous traces, hence two probes.

Standard oscilloscope terminology; also called a *channel* or *waveform* on real scopes.

**Where it appears:** `docs/vintage-story.md` (Tablet Host, Instruments — "the phase lessons (curriculum 15/17) compare two traces"); the two-probe contract in `docs/solver.md` (Probes) and `docs/curriculum.md` lessons 15/17.

### transformer
- A device that transfers AC power between two circuits through a shared magnetic field, changing voltage by its turns ratio — and, in Manatee's world simulation, the canonical *island boundary*: the place where one solver matrix ends and another begins.

Physically, a transformer is two coils of wire wound on a common iron core: alternating current in the primary winding induces a proportional voltage in the secondary, scaled by the ratio of turn counts, with no wire connecting the two sides. Manatee exploits that electrical isolation architecturally. The two sides of a decoupling transformer are solved as **separate islands** (independent matrices, independently schedulable across threads) that exchange a small summary — power/voltage per tick in the Stationeers DC integration, amplitude+phase per substep with explicit relaxation in VS AC — instead of sharing a matrix. A programming analogy: it is a message-passing boundary between two otherwise share-nothing processes, where relaxation damping plays the role of backpressure that keeps the exchange loop stable. Note the class split (design.md, settled 2026-07-05): *idealized* transformers (small, local, used in all tablet lessons) are ordinary same-matrix two-ports; only *decoupling* transformers (utility-scale) are island boundaries. Gameplay deliberately steers long-distance transmission toward the decoupling type, so thread isolation lands exactly where it pays.

Standard EE term; the same-matrix vs boundary split is a project design decision, not standard usage — in SPICE-family simulators a transformer is normally always an in-matrix coupled-inductor or ideal two-port stamp.

**Where it appears:** `docs/design.md` (Simulation Model, Island coupling); `docs/stationeers.md` (Islands and Coupling Devices); `docs/curriculum.md` (lesson III.13, Authoring Rules); `docs/solver.md` (Islands).

**References:** Horowitz & Hill, *The Art of Electronics*, 3rd ed. (transformer basics); Vlach & Singhal, *Computer Methods for Circuit Analysis and Design* (coupled-inductor and ideal-transformer models in nodal analysis).

### transformer iron
- The magnetic core material of a transformer — the mass of laminated iron the windings are wrapped around — whose required size scales *inversely* with operating frequency.

A transformer core must carry the magnetic flux of each half-cycle without saturating; the lower the frequency, the longer each half-cycle lasts and the more flux accumulates, so the core (and its cost and weight) must grow roughly in proportion to 1/frequency. This single fact is a load-bearing gameplay lever in the Vintage Story design: waterwheel alternators naturally run at ~5 Hz, and a 5 Hz transformer needs on the order of ten times the iron of a 50 Hz one — enormous and expensive. The intended reward for mastering high-pole-count alternators (electrical frequency = shaft speed × pole pairs) is that 50 Hz makes transformers small and cheap, teaching why the real world standardized on 50/60 Hz. Systems analogy: frequency is like batch rate in a pipeline — a slower rate means each batch (half-cycle) is bigger, so every buffer (the core) along the path must be sized up to hold it. Curriculum lesson III.13 makes this explicit for the player.

Standard physics (transformer EMF/core-sizing relationship); "iron" for the core is common EE shorthand (as in *iron losses*).

**Where it appears:** `docs/design.md` (frequency standards: "transformer iron size and cost scale inversely with frequency, so 5 Hz transformers are enormous"); motivates the pole-count arc in `docs/vintage-story.md` and curriculum lesson III.13.

**References:** Horowitz & Hill, *The Art of Electronics*, 3rd ed. (transformers and core sizing); Fitzgerald, Kingsley & Umans, *Electric Machinery* (transformer flux and core design).

### transformer step
- The voltage-ratio conversion a transformer performs between two voltage tiers — e.g. stepping 240 V down to 48 V — treated in Manatee as something the player sees and reasons about, not a hidden bookkeeping detail.

A transformer converts AC power between voltage levels in a fixed ratio set by its winding turns: "step-up" raises voltage (and lowers current proportionally), "step-down" does the opposite, with power roughly conserved. In our design this is player-facing under R19 ("Real voltage tiers"): device ratings, the 12 V / 48 V / 240 V tiers, and the transformer steps between them are gameplay concepts, deliberately unlike vanilla Stationeers' pure-wattage semantics where voltage is invisible. For a programmer, a transformer step is like an adapter at a typed interface boundary: it converts between two incompatible "units" (voltage levels) at a fixed exchange rate, and the boundary itself is architecturally significant — transformers are also where solver islands couple or decouple (design.md, island coupling; stationeers.md, where a transformer bounds separate islands). The term is ordinary EE usage (step-up/step-down transformer); we just use "step" as a noun for the tier-to-tier conversion.

**Where it appears:** design.md R19 and Arc 2 (power engineering: transformers and distribution, voltage tiers); stationeers.md (transformer steps vs vanilla watt semantics; transformer = island coupling boundary).

**References:** Horowitz & Hill, *The Art of Electronics*, ch. 1 (transformers and AC power).

### transformer (`T`)
- The Falstad/circuitjs1 text-format element for a transformer: a 4-post coupled-inductor pair, serialized on a line starting with `T`.

In the Falstad dialect our importer parses (`docs/falstad-format.md` §4), a `T` line carries `inductance ratio current0 current1 [couplingCoef [saturationCurrent]]`. `inductance` is the primary winding's inductance in henries; `ratio` **in the file is N2/N1** — the UI displays its reciprocal N1/N2, a classic Falstad trap — and the secondary inductance is derived as `inductance × ratio²`. `current0`/`current1` are the two winding currents, saved as element *state* so a running circuit resumes mid-waveform (like serializing a variable's current value, not just its declaration). `couplingCoef` defaults to 0.999, i.e. a nearly-ideal but not perfect magnetic link. Flags encode drawing concerns (polarity-dot flip, orientation); the element has 4 posts at the corners of its bounding box, two per winding. This is the on-disk exchange format's transformer; Manatee's own decoupling/idealized transformer classes (see **transformer**) are a separate concept in the solver.

Standard: this is circuitjs1's `TransformerElm`, a linear coupled-inductor (mutual-inductance) model.

**Where it appears:** `docs/falstad-format.md` §4 element table (with `TransformerElm.java` line references); consumed by the Falstad importer.

### transient
- In our API, the solver regime that advances the circuit through time in fixed steps, computing how voltages and currents evolve — one of the three `SolverProfile` regimes (`Dc`, `Transient`, `Mixed`).

Where DC analysis asks "what does this circuit settle to?", transient asks "what does it do next, given where it is now?" — capacitor voltages and inductor currents are carried as state from step to step, so charging curves, flicker, and decay are actually simulated rather than skipped over. `SolverProfile.Transient(dt)` selects this regime for a whole netlist (Vintage Story DC-side and the tablet use dt = 0.05 s); `Mixed` chooses per-island between transient stepping and subcycled AC. For a programmer: a transient solve is a state machine update — `state' = f(state, inputs, dt)` — where DC is instead a fixed-point computation. Note the word also appears in its everyday sense elsewhere in the docs ("a bounded one-time transient" meaning a short-lived disturbance); this entry is about the analysis regime.

**Standard term:** yes — "transient analysis" is the universal SPICE name (`.tran`).

**Where it appears:** api.md §5 `SolverProfile.Transient`; design.md R2; solver.md Analyses.

**References:** Nagel (1975), *SPICE2: A Computer Program to Simulate Semiconductor Circuits*, UCB/ERL-M520; ngspice manual, transient analysis chapter.

### transient analysis
- Timestepped time-domain simulation: repeatedly solving the circuit at successive instants, dt apart, so that quantities that change over time (capacitor charge, inductor current, sine waveforms) actually evolve — as opposed to a single DC operating-point solve.

This is one of the analyses R2 requires a single netlist to support ("one netlist, multiple analyses": DC operating point, transient, and subcycled time-domain AC — each component provides a stamp per analysis, per SPICE precedent). Our transient uses Backward Euler companion models: each capacitor/inductor is replaced, for one step, by a resistor-plus-source whose values encode its state from the previous step, turning the differential equations into a plain linear solve per step. Crucially we use a *fixed* dt, unlike SPICE's adaptive stepping — fixed dt keeps those companion conductances constant, so a linear island in steady operation costs only a right-hand-side update per step (tier 1), the load-bearing performance fact for subcycled AC. Programming analogy: transient analysis is a game loop for the circuit — `Step(dt)` advances the world one frame; DC analysis is instead solving for the end state directly. Invariant checks (energy conservation over transient runs) run always-on in debug builds.

**Standard term:** yes — SPICE `.tran`; also "time-domain analysis".

**Where it appears:** design.md R2 and Simulation Model; solver.md Analyses (`Step(double dt)`; dt ≤ 0 ⇒ DC operating point); falstad-format.md §2, §7 (the imported `maxTimeStep` is the transient-dt hint).

**References:** Nagel (1975), SPICE2 memo UCB/ERL-M520; Vlach & Singhal, *Computer Methods for Circuit Analysis and Design* (integration methods, companion models); Pillage, Rohrer & Visweswariah, *Electronic Circuit and System Simulation Methods*.

### transistor model (`32`)
- A line type in Falstad/circuitjs1 saved-circuit files that defines a named parameter set for transistors; our importer rejects it with an explicit error rather than parsing it.

Falstad's text format is mostly one element per line, but a few line types carry metadata instead of circuit elements. Type `32` is one of these: it stores a reusable transistor model definition (like `34` does for diodes), which transistor elements elsewhere in the file refer to by name — think of it as a named configuration record, or in EE terms a `.MODEL` card in SPICE. Manatee's importer accepts only the small element subset our solver supports; transistors are out of scope entirely, so a `32` line (like the transistor elements themselves) is **rejected loudly** — with line number, offending token, and a one-line reason — instead of being silently skipped. Loud rejection is deliberate: silently ignoring unknown tokens is how format dialects drift apart.

This term is standard within the Falstad/circuitjs1 ecosystem; the closest general concept is a SPICE model card (`.MODEL` statement).

**Where it appears:** `docs/falstad-format.md` §1 (non-element line types, from `CircuitLoader.java:149-193`) and §7 (importer accept/reject contract).

**References:** Falstad/circuitjs1 source (`CircuitLoader.java`); Nagel (1975), *SPICE2: A Computer Program to Simulate Semiconductor Circuits*, UCB/ERL-M520, for the model-card concept.

### trip curve
- The rule relating how much overcurrent a breaker or fuse tolerates to how long it tolerates it: a small overload is allowed for seconds, a large one trips almost instantly.

Real protective devices don't open at a single current threshold; they follow a current-versus-time characteristic, because what actually destroys a conductor is accumulated heating, not the instantaneous current. In programming terms it's a leaky-bucket rate limiter: current above the rating fills the bucket at a rate that grows with the square of the current, cooling drains it, and the device trips when the bucket overflows. In Manatee this is not modeled as a special case per device: fuse and breaker trip curves in the Stationeers/Re-Volt integration ride manatee-core's generic **limit events**, including the i²t thermal accumulator, so Re-Volt's existing delayed-burn behavior is reproduced by mechanism. Ratings are ambient-temperature-adjusted through the compaction layer's limit envelope, so Europa's −150 °C and Vulcan's +800 °C genuinely move the thresholds.

Standard EE term (also "time-current characteristic" or "time-current curve" in breaker datasheets); our usage matches the standard meaning.

**Where it appears:** `docs/stationeers.md` (Graph Construction); the underlying mechanism (limit events, i²t accumulator) in `docs/solver.md` and `docs/compaction.md`.

**References:** Horowitz & Hill, *The Art of Electronics*, on fuses and overcurrent protection; manufacturer time-current curve conventions (e.g. IEC 60269 for fuses).

### turns ratio
- The single number that defines what an ideal transformer does: the ratio between the turn counts of its two windings, which sets how it scales voltage (up by the ratio) and current (down by the same ratio) between its sides.

A transformer is two coils of wire sharing a magnetic core; if one winding has N1 turns and the other N2, voltages transform by N2/N1 and currents by N1/N2, so power passes through unchanged — that pairing is why the ideal transformer conserves energy by construction. In Manatee it is literally the one constraint parameter of the ideal-transformer primitive: `AddIdealTransformer(..., double turnsRatio, ...)` stamps a turns-ratio constraint into the matrix as two auxiliary rows, and `Adjust(TransformerId, turnsRatio)` changes it at cost tier 2 with zero allocation (only the coupled-row values change, not the matrix shape). For the programmer: think of it as a lossless unit-conversion constraint between two subcircuits — an invariant `V2 = r·V1, I1 = r·I2` enforced by the solver, not a device that computes anything per tick. Tablet Lesson 13 teaches it (and why it only works on AC).

This is the standard term, universal in the literature; also seen as the constant *n* or *a* of an "ideal transformer."
- **Where it appears:** `docs/solver.md` Component Set and Islands (idealized vs decoupling transformer classes); `docs/api.md` §6 (`AddIdealTransformer`, `Adjust(TransformerId)`) and the conservation-audit notes; `docs/curriculum.md` Arc III Lesson 13.
- **References:** Horowitz & Hill, *The Art of Electronics* (transformers); Vlach & Singhal, *Computer Methods for Circuit Analysis and Design* (ideal-transformer stamps via auxiliary equations).

### turns ratio (file `ratio`)
- In the Falstad/circuitjs1 text format's transformer line (`T`), the field named `ratio` is stored as **N2/N1** (secondary over primary) — the reciprocal of what the Falstad UI displays and of the common N1/N2 convention.

This is a serialization gotcha we pinned after reading the circuitjs1 source: the `T` element's dump writes `inductance ratio current0 current1 [couplingCoef [saturationCurrent]]`, where `inductance` is the *primary* inductance in henries and file `ratio` = N2/N1 (`TransformerElm.java:53-62`). The UI meanwhile shows the user N1/N2 = 1/ratio (`TransformerElm.java:331,355`), and the secondary inductance is derived as `inductance · ratio²` (`TransformerElm.java:242`). For the programmer: a classic wire-format-vs-display-format reversal, like a file storing a timestamp in UTC while the UI shows local time — our importer must honor the file convention, not the on-screen one. Our accept/reject contract does exactly that: honor `ratio = N2/N1`, `couplingCoef`, and the reverse-polarity flag; reject nonzero saturation.

**Standard term:** turns ratio — but conventionally quoted as N1/N2 (primary:secondary), so the file value is its reciprocal. A reversal worth pinning.
- **Where it appears:** `docs/falstad-format.md` §4 (the `T` element row, with `TransformerElm.java` line references) and §7 (importer accept/reject contract).

### Two-port / two-port coupling
- A device with two terminal-*pairs* (four terminals grouped as two ports) that couples two parts of a circuit — in Manatee, transformers and converter-style devices that link two islands or two electrical domains (AC↔DC).

Standard circuit theory: a *port* is a pair of terminals where the current entering one equals the current leaving the other, and a two-port is characterized entirely by the relationship it enforces between its port voltages and currents — an interface contract, in programming terms, that hides everything inside. Manatee uses the concept at two distinct implementation levels, and the distinction matters. **Same-matrix two-ports** (the ideal transformer, `AddIdealTransformer`) live inside one island's matrix as a hard algebraic constraint stamped via auxiliary rows, so both windings are solved together, exactly — this is what the tablet's reactive-power lessons rely on. **Island-boundary two-ports** (decoupling transformers, DC converter two-ports, AC↔DC boundaries) keep the two sides as separate matrices — separate solves, potentially separate threads — and couple them by exchanging summarized values per substep (amplitude + phase for AC), smoothed by explicit relaxation, with transfer clamped to what the source side actually delivered (a no-free-energy clamp). These ship as *behavioral* two-ports (a power transfer with an efficiency curve) precisely to keep hot world islands linear. For programmers: the first is a shared constraint inside one solve; the second is two cooperating services exchanging messages each substep — bounded lag, damped to stay stable — and the two islands sharing a coupler substep in lockstep as one scheduling unit, never across free-running threads. So "two-port" names the terminal shape; whether it merges matrices or bounds them is a per-device-type decision.

This is the standard term (also "two-port network," "4-terminal/quadripole network," characterized by Z/Y/ABCD parameters); our island-boundary variant is nonstandard only in *implementation* — closest simulation concepts are behavioral/macro models and co-simulation/relaxation coupling, not the parameter-matrix formalism.
- **Where it appears:** `docs/design.md` Simulation Model (AC boundaries, island coupling); `docs/solver.md` Component Set, Analyses, and Islands (coupling devices, exchange scheme); `docs/api.md` §6 (`AddIdealTransformer`, `TransformerId`) and `CouplerSpec.ConverterTwoPort`; `docs/testing-strategy.md` (boundary-coupling stability tests).
- **References:** Vlach & Singhal, *Computer Methods for Circuit Analysis and Design* (two-port representations and stamps); Ho, Ruehli & Brennan (1975), "The Modified Nodal Approach to Network Analysis", IEEE Trans. Circuits and Systems (auxiliary-row treatment of multi-terminal elements); Horowitz & Hill, *The Art of Electronics*.

### V/I
- Shorthand for "voltage and current" — the two primary live readouts the game shows players for any probed wire or device.

In Manatee's Vintage Story client, hovering a block surfaces live V/I (and temperature) lines through the engine's block-info hook (`BlockEntity.GetBlockInfo`, the WAILA-style tooltip), fed by a throttled telemetry channel rather than per-tick chunk syncs. For programmers: voltage and current are the two state variables the MNA solve produces per node/branch; V/I is just the display projection of that solution vector — the equivalent of a metrics dashboard reading gauges off the live system. For the EE: this is exactly the standard V and I pair; nothing exotic, just the notation used in docs for tooltip/telemetry content. Standard aliases: voltage/current readout, volts and amps.

**Where it appears:** `docs/vintage-story.md` §Tooltips and Instruments (R15) — V/I/temperature tooltip lines; the `mna-telemetry` broadcast channel plan.

**References:** Horowitz & Hill, *The Art of Electronics* (voltage/current fundamentals, Ch. 1).

### voltage-collapse death spiral
- A real positive-feedback instability: when supply voltage sags, constant-power loads draw *more* current to keep their power up, which sags the voltage further, until the network collapses.

A constant-power device obeys I = P/V, so falling V means rising I — the opposite of a resistor's self-limiting behavior. In the Re-Volt legacy-device adaptor, every unconverted vanilla device is modeled as exactly such a constant-power load, so an overloaded network genuinely exhibits this runaway. We ship it on purpose: above the brownout threshold the spiral is real physics and an intentional gameplay feature; below the threshold, per-device brownout with hysteresis (drop at V_low, rejoin at V_high, staggered rejoin, recloser-style lockout) makes the collapse settle legibly instead of strobing at tick rate. For programmers: it is a positive-feedback loop like a retry storm — a struggling server makes clients retry harder, which makes the server struggle more — and the brownout clamp is the load-shedding circuit breaker that ends the storm.

**Standard term:** voltage collapse / voltage instability (power-systems literature); "death spiral" is our colorful gloss.
- **Where it appears:** `docs/stationeers.md` "The Legacy-Device Adaptor"; `docs/design.md` R18 area; `docs/experiments/2026-07-05-adversarial-playtest.md` (the hysteresis decision).
- **References:** Kundur, "Power System Stability and Control" (McGraw-Hill, 1994), ch. on voltage stability; Van Cutsem & Vournas, "Voltage Stability of Electric Power Systems" (Springer, 1998).

### voltage divider
- Two (or more) resistors in series across a source, splitting the source voltage between them in proportion to their resistances.

With resistors R1 and R2 in series, the same current flows through both, so the voltage across each is I×R — the bigger resistor drops the bigger share, and the midpoint sits at Vsource × R2/(R1+R2). It is the first structural idea in the tablet curriculum: Lesson I.1 ("Series voltage drops") has players predict and then measure that "bigger R drops more." For a programmer, think of it as proportional allocation under a shared-rate invariant: one flow (the current) is fixed by the total, and each stage's "cost" (voltage drop) is its weight times that shared flow — like dividing a fixed latency budget across pipeline stages in proportion to their per-unit cost. The curriculum calls these simply "dividers."

This is the standard term (also "potential divider" in British usage).
- **Where it appears:** `docs/curriculum.md` (Lesson Arc I.1).
- **References:** Horowitz & Hill, *The Art of Electronics*, ch. 1 (the voltage divider is its foundational example).

### voltage source
- An ideal circuit element that holds a fixed voltage across its two terminals regardless of how much current flows through it.

Where a resistor's voltage depends on its current, an ideal voltage source is a hard constraint: "the difference between these two node voltages equals V, whatever it takes." In MNA that constraint gets its own equation row and an extra unknown (the source's branch current) — the classic reason Modified Nodal Analysis exists at all. For a programmer: it is an invariant the solver must satisfy exactly, not a component with behavior — like a `WHERE`-clause constraint rather than a computed column. In the Stationeers legacy-device adaptor, vanilla generators that only "advertise watts" are stamped into the solve as voltage sources at their tier's nominal voltage, with a current limit derived from their advertised power and a small internal series resistance so paralleled generators stay well-posed (two disagreeing *ideal* sources in parallel is a designed-in singularity).

This is the standard term (an "independent voltage source" in SPICE vocabulary).
- **Where it appears:** `docs/stationeers.md` (Legacy-Device Adaptor); `docs/solver.md` (failure handling for parallel ideal sources).
- **References:** Ho, Ruehli & Brennan (1975), "The Modified Nodal Approach to Network Analysis", *IEEE Trans. Circuits and Systems* — voltage sources are why the "modified" part exists; ngspice manual, independent sources chapter.

### voltage source (`v`)
- The Falstad/circuitjs text-format element code for a two-terminal voltage source, whose parameters encode DC and waveform behavior.

A `v` line constrains **V(post2) − V(post1) = value**, with post 2 (the second coordinate pair) as the + terminal. Its parameter list is `waveform freq maxVoltage bias phaseShift [dutyCycle]`. The waveform field selects DC (0), sine (1), square, triangle, sawtooth, pulse, noise, or variable (2–7); our importer accepts **only DC and sine** and rejects the rest loudly. Two sharp edges the entry exists to record: for DC the output is `maxVoltage + bias` and the frequency field is *still present* in the line; for AC the output is `maxVoltage·sin(2πft + phaseShift) + bias`, where `maxVoltage` is the **peak amplitude, not RMS** and `phaseShift` is stored in **radians**. The related code `R` is a one-post "rail" sharing the same parameters. For a programmer: `v` is a serialization record whose field meanings are position-dependent and whose names lie slightly (`maxVoltage` holds the DC value) — treat the spec table, not the field names, as the schema. Our own lesson files put the DC value in `maxVoltage`, never `bias`.

The element code and field layout are Falstad/circuitjs conventions, documented from source (`VoltageElm.java`).
- **Where it appears:** `docs/falstad-format.md` (§4 element table; voltage-source semantics; importer accept list), `docs/curriculum.md` (corpus rule for lesson files).

### voltageRange
- A field in the Falstad circuit file's `$` header line giving the full-scale value, in volts, for the simulator's node-voltage color display.

In the Falstad/circuitjs1 text format the header line is `$ flags maxTimeStep simSpeed currentSpeed voltageRange [powerBarValue [minTimeStep]]`; `voltageRange` sets how many volts map to the extremes of the green/red voltage coloring on the schematic. It is presentation-only — it changes what the picture looks like, never what the circuit computes (of the header fields, only `maxTimeStep` has electrical meaning). Our importer must parse it to round-trip files faithfully, but the solver ignores it. For programmers: it is a rendering config value, like the min/max of a heat-map color scale; for EEs: it is the volts-per-division knob on the display, not a component value.

**Standard term:** this is Falstad/circuitjs1's own field name (written by `CirSim.dumpOptions()`, read by `CircuitLoader.readOptions`).
- **Where it appears:** `docs/falstad-format.md` §2 (header line).

### voltaic pile
- The earliest practical battery (Alessandro Volta, 1800): a stack of alternating zinc and copper discs separated by brine-soaked cloth, and the "pile" of tablet lesson 6.

In our curriculum it is the concrete object behind the lesson "batteries are not ideal": the pile has high internal resistance, so its terminal voltage sags visibly under load, and its zinc electrode is physically consumed — you are burning metal to make electricity. In Vintage Story Arc 1 it is also an early electrical source (arriving after the waterwheel/windmill alternator, cables, and switches), which motivates why storage and better chemistries mattered historically. For programmers: internal resistance means the source is not an ideal constant — model it as an ideal voltage source with a series resistor, so drawing more current costs you voltage, like a service whose latency rises under load. The consumable zinc is modeled behaviorally (an energy budget that depletes), with the actual chemistry stubbed.

The term is standard historical EE vocabulary; no aliases in project use.
- **Where it appears:** `docs/curriculum.md` Lesson Arc I.6; `docs/design.md` Arc 1.
- **References:** Volta's 1800 letter to the Royal Society (Phil. Trans. R. Soc.); Horowitz & Hill, "The Art of Electronics" (3rd ed.), on real-source internal resistance / Thévenin models.

### voltaic pile / Daniell cell
- The pair of early consumable-zinc battery devices that open Vintage Story's Arc 1: Volta's pile (1800) and Daniell's improved copper-sulfate cell (1836), which fixed the pile's rapid polarization and gave a steadier voltage.

Both ship as **behavioral electrical models with the chemistry stubbed**: electrically they are a voltage source with high internal resistance and a consumable-zinc energy budget, and that is all the solver sees — no electrochemistry is simulated. They are deliberately weak (sag hard under load, eat metal), which teaches why the pile-to-lead-acid-to-li-ion progression happened; the full battery fiction in design.md runs voltaic pile → lead-acid (Arc 2) → ruins-loot li-ion. For programmers: "chemistry stubbed behind behavioral models" is exactly an interface with a mock implementation — the device honors the electrical contract (V, R_internal, remaining charge) without implementing the underlying process. For EEs: these are ordinary Thévenin-equivalent source models with a state-of-charge integrator, plus a game-item resource cost.

Standard historical device names; the pairing is just our progression shorthand.
- **Where it appears:** `docs/design.md` Arc 1 and Open Questions (battery fiction); `docs/curriculum.md` lesson 6.
- **References:** Horowitz & Hill, "The Art of Electronics" (3rd ed.), on modeling real sources with internal resistance.

### waveform
The shape of a voltage or current source's output over time — in the Falstad file format, an integer field on `v`/`R` source lines selecting that shape.

For an EE this is just "what the source puts out": DC, sine, square, triangle, and so on. For a programmer, treat it as an enum tag in the netlist file: `WF_DC=0`, `WF_AC=1` (sine), `WF_SQUARE=2`, `WF_TRIANGLE=3`, `WF_SAWTOOTH=4`, `WF_PULSE=5`, `WF_NOISE=6`, `WF_VAR=7` (constants from circuitjs1's `VoltageElm.java`). The accompanying fields give frequency (Hz), `maxVoltage` (**peak amplitude, not RMS**), DC bias, and phase (radians); the frequency field is present even for DC. Manatee's Falstad importer accepts **only waveforms 0 (DC) and 1 (sine)** and rejects 2–7 loudly — a deliberate accept/reject contract, since the solver's source models cover DC and sinusoidal drive.

Standard term; "waveform" is universal EE vocabulary, and the enum values are Falstad/circuitjs1's own.

**Where it appears:** `docs/falstad-format.md` §4 (voltage-source semantics, with circuitjs1 file:line cites) and §7 (importer accept/reject list: "waveform 0 (DC) or 1 (sine) only — reject waveforms 2-7 loudly").

**References:** Falstad/circuitjs1 source (`VoltageElm.java`); Horowitz & Hill, *The Art of Electronics*, ch. 1 on signal waveforms.

### wire = ideal node (Falstad convention)
- The convention, inherited from Falstad/circuitjs1, that a wire drawn on the schematic is not a component at all — it is a perfect, zero-resistance connection, so everything a wire touches is one and the same electrical node.

Under this convention the solver never sees wires: at intake, connected wire runs are merged into single nodes, so a schematic's "wiring" is purely a drawing-and-topology concern. Our tablet/2D harness follows it — the compaction layer notes that schematic wires are already near-minimal and passes them through as ideal nodes — with one deliberate extension: advanced lessons may assign an optional per-wire resistance, at which point that wire stops being an ideal node and becomes a small resistor (real wires do have resistance, and the curriculum eventually teaches that). For a programmer: merging wires into nodes is union-find over drawn segments — a wire is an assertion that two points share identity, like aliasing two variable names to one storage location. For an EE: this is the ordinary ideal-conductor assumption of schematic capture, the same one SPICE netlists make implicitly by naming nodes rather than drawing wires. Contrast with the voxel-world clients (Vintage Story, Stationeers), where cables have real per-segment resistance and the compaction layer earns its keep collapsing them.
- **Where it appears:** `docs/compaction.md` (Client Intake Contracts, tablet/2D harness bullet); `docs/falstad-format.md` (the `w` element).
- **References:** Falstad/circuitjs1 (`WireElm.java`); node-based netlist semantics per Nagel (1975), *SPICE2: A Computer Program to Simulate Semiconductor Circuits*, UCB/ERL-M520.

### wire (`w`)
- The Falstad file-format element for a plain connecting wire: a zero-resistance link between two points, with no parameters at all.

In the Falstad/circuitjs1 text format each element is one line, `TYPE x1 y1 x2 y2 flags [params...]`; for type `w` the parameter list is empty (confirmed against `WireElm.java`) — the line carries only the two endpoint coordinates and the flags field. Electrically a `w` element is an ideal conductor: both ends are at the same voltage, and it drops nothing. For a programmer: it is the simplest record in the format, effectively a typed edge with no payload — importing one just unions the two endpoints into the same electrical node (see *wire = ideal node*). Our Falstad importer must accept it since nearly every schematic in the lesson corpus is stitched together with them.
- **Where it appears:** `docs/falstad-format.md` §4 (element table, first row); consumed by the Falstad importer feeding the lesson corpus (`docs/testing-strategy.md`).
- **References:** Falstad/circuitjs1 source, `WireElm.java` (the format spec in our docs cites exact revisions).

### wires
- In the schematic document model, a wire is a drawn connection between component terminals — the on-screen line that says "these two points are the same electrical node."

Wires live in the tablet/harness **document model** (layer 1 of the three pure layers), alongside components, junctions, and probe placements. They are pure data about the drawing: when the document is bound to manatee-core, netlist extraction collapses every set of terminals joined by wires and junctions into a single solver node, so a wire never appears in the solver as a component with resistance — it is topology, not physics. For programmers: think of wires as edges in a graph whose connected components become the solver's nodes (a union-find over terminals). For the EE: these are ideal schematic-capture wires, exactly as in any CAD tool — zero impedance, purely notational. (Physical in-game cables with real resistance are a different thing: those are conductors handled by the reduction layer.)

This is the standard schematic-capture sense of the word; Falstad/circuitjs1 calls the same element a wire (`w`).

**Where it appears:** `docs/harness.md` (Layering, layer 1 — document model); the Falstad importer maps circuitjs wire elements onto it (`docs/falstad-format.md`).

### zener (`z`)
- The Falstad text-format element code for a Zener diode — a diode designed to conduct *backwards* at a precise, fixed voltage, commonly used as a cheap voltage reference or clamp.
- A normal diode blocks reverse current until it fails; a Zener is built so that reverse conduction at a specific "breakdown" voltage is a documented, non-destructive operating mode — think of it as a diode with a deliberate, calibrated overflow path rather than an error condition. In the Falstad/circuitjs file format, `z` lines behave like `d` (diode) lines except that the legacy non-model form carries a `zvoltage` parameter (the breakdown voltage, default 5.6 V) instead of naming a diode model. Our importer accepts the no-flag default, `FLAG_FWDROP` values, the built-in `default`/`default-zener` model names, or any model defined by a preceding `34` line, mapping Is/Rs/N/BV onto our diode parameters. Note that Electrical Age's parser simply substituted a plain diode for `z` (losing the breakdown behavior); Manatee may do the same where a full Zener model is not warranted.
- Standard term: this IS the standard component (Zener diode); `z` is the circuitjs1 element code for it.
- **Where it appears:** `docs/falstad-format.md` §4 element table (`ZenerElm.java` reference); §7 importer accept/reject contract; EA parser behavior noted in §6.
- **References:** Horowitz & Hill, *The Art of Electronics* (Zener references and clamps); Falstad circuitjs1 source (`ZenerElm.java`).
