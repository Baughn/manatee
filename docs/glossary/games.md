# Glossary: Game Integration

*Last updated: 2026-07-07. Part of the [Manatee glossary](../glossary.md) — see the index there for all terms and for how entries are structured.*

Vintage Story and Stationeers concepts: engine facts, modding seams, game-side vocabulary.

### annunciator
- A signal-panel device in Vintage Story's Arc 1 (foundations): a low-voltage electric doorbell/indicator that shows *which* of several call points rang.

Historically, an annunciator was the panel in a large house or hotel where each room's bell-pull dropped a labeled flag or pointer, telling the servants where the summons came from — the canonical first practical use of low-voltage electricity in buildings, driven off weak batteries like the voltaic pile the same arc introduces. In Manatee it appears in the Arc 1 device list as "doorbell/annunciator": an early, period-authentic load that a player can wire with high-internal-resistance cells, teaching signaling circuits before power circuits. For programmers: it is an interrupt line with a latched source ID — the bell is the interrupt, the dropped flag is the status register you read to find who raised it.

The term is standard (electrical/building trade); no aliases in project use.

**Where it appears:** `docs/design.md` §Progression arcs, Arc 1 — foundations.

**References:** Horowitz & Hill, *The Art of Electronics*, for the general low-voltage signaling background; period electrician's handbooks (e.g., Hasluck-era bell-wiring manuals) describe annunciator practice.

### Block entity (BE)
The Vintage Story engine's object for attaching per-position runtime state and logic to a block in the world — the only place a block at a given coordinate can keep mutable data and run code.

Most blocks in the voxel world are just a static type ID in a big array; a block *entity* is an actual live object the engine creates at one position, with fields, tick callbacks, save/load, and tooltip hooks. The hard engine limit Manatee designs around: **one BE per position** — there is no multi-BE mechanism, so a standalone "cable block entity" cannot coexist with, say, the chiseled-block BE already occupying that spot. Consequence (design.md R17): all electrical semantics must live on the position's existing BE or on an attached *BE behavior* (`BlockEntityBehavior`), the engine's mixin/plug-in mechanism that lets a mod add logic to a BE it doesn't own. For an EE: the BE is the one equipment cabinet allowed at each grid location; behaviors are cards you can slot into someone else's cabinet, because you can't install a second cabinet.

**Standard term:** `BlockEntity` (Vintage Story engine class; the analogous Minecraft concepts are "block entity" / older "tile entity").

**Where it appears:** `docs/vintage-story.md` §1 (engine facts with file:line references, one-BE-per-position limit, behaviors, tooltips); `docs/design.md` R17 (microblock integration); `docs/compaction.md` client intake (VS).

### cable segment
- One individual piece of cable as Stationeers places it in the world — the natural unit of conductor granularity that Re-Volt's `NetworkExport` hands us.

Stationeers players build wiring one grid-cell cable at a time, so a large base's power network arrives as an adjacency list of ~10,000 `CableInfo` entries (each with a RefId and its connections), plus device and fuse lists. Each segment becomes a resistor edge in the raw graph (resistance = ohms-per-length × length, with ohms-per-length looked up from the cable type); junctions become nodes. The shared reduction layer then collapses series chains — runs of segments with nothing tapped off them — so ~10k segments compact to a low-hundreds-of-nodes netlist the solver actually sees. Individual segments stay observable and attributable after collapse: probes interpolate voltage inside a compacted run, and when a limit event fires the compaction layer answers *which segment* melts. For an EE: the segment is the physical conductor stick; the solver works on the electrically equivalent reduced network, exactly as you would hand-reduce series resistances, but mechanically and reversibly.

**Where it appears:** `docs/design.md` R10 and the Stationeers/Performance sections; `docs/compaction.md` (Responsibilities #2, Client Intake); `docs/stationeers.md`, "Graph Construction"; `SegmentKey`/`AddSegment` in `docs/api.md` §19.

### CableNetwork / CableInfo / RefId / ElectricityTick / OnPowerTick (Stationeers engine objects)
- The native Stationeers engine objects and identifiers that anchor the whole Re-Volt integration at the Manatee boundary: the game's merged cable-network class, its per-cable adjacency record, the stable per-thing ID, and the built-in power-tick machinery Manatee hooks into (all verified by reading the game's decompiled source).

**`CableNetwork`** is the vanilla Stationeers game class (RocketWerkz's Stationeers) representing one connected run of cables: whenever cables touch, the game unions them into a single `CableNetwork` object and runs its power tick per network. Crucially, vanilla network identities **never merge across coupling devices** — a breaker or transformer straddling two networks leaves them as two distinct `CableNetwork` objects. Manatee respects this as a hard invariant: a solver *island* may electrically span several CableNetworks when a breaker is closed (union-find over networks keyed on coupler state), but the game-side objects stay separate. A `CableNetwork`'s identity becomes the `PartitionKey` under the API's `ClientPartitioned` mode, so the solver enforces the "vanilla networks never merge" rule as an API contract rather than adaptor discipline — rejecting any edit that would bridge two partitions with an ordinary component. Re-Volt substitutes a Harmony-injected `MNACableNetwork` subclass that carries the mapping from each vanilla network to its manatee nodes. For an EE: a CableNetwork is one physically contiguous harness; galvanic connection *between* harnesses only ever happens through a named coupling device, never by the harnesses fusing.

**`CableInfo`** is the per-cable adjacency record. Stationeers keeps `CableNetwork` objects but discards per-cable connectivity after building them; Sukasa's `NetworkExport` recovers it and hands the core `CableInfo { RefId, List<int> Connections }` for every cable segment — adjacency at cable-segment granularity — plus device and fuse lists. `CableInfo` feeds graph construction one cable segment at a time: each becomes a resistor edge with resistance from cable type × length before the compaction layer collapses the series chains. Agreed additions to the export (not yet built) are device **port identity** per connection, **per-cable type/gauge** (prefab name), and **fuse positions in the adjacency** (a fuse is an edge with a rating, not a side-list entry). For a programmer: `CableNetwork` is the game's aggregate object, `CableInfo` the serialized edge list we rebuild the real graph from.

**`RefId`** is the game's stable identifier for every placed thing (every device, cable, and network has one); because it is unique and deterministic across sessions, Re-Volt packs it into both of Manatee's identity types — `ExternalKey` (topological/component identity: "which component is this?") and usually `StateKey` (saved-state identity: "whose blob is this?") — which is how a reloaded world finds its components again; the network's identity likewise maps to `PartitionKey`. For a programmer: RefId is the primary key everything else foreign-keys on. For an EE: it is the reference designator (like "R17" on a schematic) that stays the same across every rebuild of the netlist. (See the ExternalKey and StateKey glossary entries for the Manatee side.)

**The power-tick machinery:** once per 500 ms simulation cycle, `ElectricityManager.ElectricityTick()` loops over every `CableNetwork`; each network's `CableNetwork.OnPowerTick()` then runs the game's three-phase power contract — `Initialise` → `CalculateState` → `ApplyState` — contiguously for that one network. There is no point in the vanilla loop where "all networks" are gathered at once, which is why Manatee's global solve must be patched in *ahead of* the per-network loop (`docs/api.md` §22.a) rather than inside it — running it per-network would solve N times and mis-apply results across partitions. For an EE: the tick sequence is the fixed test procedure the bench runs on each harness every half second.

Game-native names (Stationeers vanilla and Re-Volt's export format), mapped onto Manatee's standard key concepts — not EE or Manatee coinages.
- **Where it appears:** `docs/stationeers.md` throughout (Graph Construction; Threading, with `CableNetwork.cs`/`GameManager.cs` file:line references; Persistence — snapshots keyed by RefIds); `docs/design.md` Stationeers section; `docs/compaction.md` Incremental Maintenance ("Stationeers: per-CableNetwork" resync scope); `docs/api.md` §19 (keys) and §22.a (execution model, in prose); `docs/integration-tutorial.md` §3 (the two-keys discussion, "In Re-Volt both usually pack a RefId").

### catenary
- The gentle hanging-curve shape a wire naturally takes between two supports, used in the Vintage Story mod as the lightweight way to render long bare-wire spans.

  Mathematically a catenary is the curve of a flexible cable hanging under its own weight (a hyperbolic cosine — what real power lines sag into between pylons). In our renderer it is purely cosmetic: the VS mod picks a wire's visual representation by counting the wire voxels between supports — short spans render as actual voxel geometry, long spans as a catenary curve. This is a classic level-of-detail trade familiar to programmers: same electrical model underneath, two render paths chosen by span length. The plan deliberately avoids the engine's live cloth/rope physics (mass-spring simulation would cost CPU, wobble, and can rip): instead a *static* catenary mesh is drawn in our own `IRenderer` between endpoint block entities, borrowing the vanilla cloth system's segment-transform math and two-endpoint activation model. Catenary spans are what make electrified fences fall out of bare-wire semantics — a span between two posts renders light and menacing without needing a wire voxel at every position.

  Standard term (surveying/civil engineering: catenary sag of overhead lines); our usage matches, applied to rendering only.
- **Where it appears:** `docs/design.md` Device notes (electrified fences, renderer-selection rule); `docs/vintage-story.md` Wire rendering (catenary spans between posts, cloth-system precedent with file:line cites, region-boundary gotcha).

### chisel / chiseled block
A Vintage Story game mechanic: the chisel tool carves an ordinary block into a *chiseled block*, a block whose interior is a 16×16×16 grid of tiny "microblock" voxels, each of which can be a different material — and Manatee's in-world cables are made of exactly such voxels.

For a programmer, a chiseled block is a block-sized 3D bitmap the player edits voxel-by-voxel; for an EE, think of it as hand-routing conductors through a solid at sub-block resolution, like carving traces into a substrate. Requirement R17 makes cable materials legal chisel materials (`canChisel: true`) so cable voxels are *real* microblock voxels inside the vanilla chiseled-block entity, with electrical semantics attached via a mod behavior rather than a custom block — this enables the oven mechanic (chisel grooves into stone, lay resistance wire in them, the block heats food) and lets decorative chiseling coexist with wiring in one block. Two verified engine facts constrain the design (`docs/vintage-story.md`, with file:line into engine source): chiseling has no pre-edit veto (behaviors are notified only *after* a voxel changes), and non-`BlockChisel` microblocks cannot be re-chiseled. The hazard follows deliberately: cable voxels are never protected from the chisel, chiseling out cable material salvages it at a loss, and chiseling into a *live* conductor shocks you — the real protection is turning off the breaker first, i.e. lockout-tagout as an emergent, taught skill.

Standard game term (Vintage Story vanilla mechanic); "microblock" is the engine's name for the resulting sub-voxel block entity.

**Where it appears:** `docs/design.md` R17, Hazards ("Cable voxels are never protected from the chisel"), device notes (the oven); `docs/vintage-story.md` microblock integration (engine facts with file:line references).

### Chunk size / natural chunk size
- The unit of spatial bookkeeping each host game already uses, which Manatee's compaction layer adopts as its granularity for tracking dirty (changed) regions of cable geometry.

Games divide their worlds into fixed administrative units — Vintage Story into 32×32×32-block cubes ("chunks"), Stationeers into per-`CableNetwork` groupings — and all their loading, saving, and change notification happens at that unit. Rather than invent its own spatial index, Manatee's incremental maintenance marks dirty regions at the client's natural chunk size: when cable geometry changes, we record *which chunk* changed and recompact only there. This is the same trick as a write-back cache or a page-granular dirty bitmap: track modifications at the granularity the underlying system already reports, trading some precision for zero extra bookkeeping machinery. For an EE, it is like fault-isolating a plant by panel rather than by individual terminal — coarser, but it matches how the plant is already sectioned. "Natural" signals that the size is whatever the host game gives us, not a constant we chose.
- **Where it appears:** `docs/compaction.md` "Incremental Maintenance" (dirty-region tracking bullet); implemented in `ConductorGraph`.

### deconstruction burst
A rapid run of removals hitting the circuit in quick succession — typically a player tearing down a stretch of cabling segment by segment within one game tick.

In Manatee's incremental-maintenance design, any removal invalidates the affected island (there is no incremental split detection — design.md R11), so the island must be rebuilt from its geometry. Naively, ripping out twenty cable segments would trigger twenty rebuilds. Instead removals only *mark the island dirty*, and the rebuild is **coalesced**: it runs at most once per tick, at solve time, so a deconstruction burst costs one rebuild rather than one per removed segment. This is the classic dirty-flag / write-coalescing pattern — the same reason a text editor doesn't re-render the whole document per keystroke. For an EE: it's debouncing — many closely spaced input transitions are treated as a single event before the expensive downstream response fires.

**Project-motivated phrase**, not a term of art; the underlying technique is standard **event coalescing / dirty-flag batching**.

**Where it appears:** `docs/compaction.md` (Incremental Maintenance — "any removal ⇒ island rebuild, coalesced").

### Decor layer / surface wiring
- The **decor layer** is a Vintage Story engine feature for attaching thin decorative overlays to block faces (normally moss, snow, paintings); Manatee optionally reuses it for **surface wiring** — face-mounted cable/conduit aesthetics on arbitrary blocks.

The engine lets a block face carry a decorative overlay about 1/32 of a block thick — a chunk-level side channel supporting per-face 16×16 subposition attachment (`DecorBits`), with the vanilla chiseled-block entity accepting decor (`BEMicroBlock` implements `IAcceptsDecor`, verified at `BEMicroBlock.cs:74, :2485–2500` and `vsapi/.../DecorBits.cs:49–75`). Because decor is a face overlay rather than a volumetric occupant, it suits wiring *mounted on* a surface — conduit-on-a-wall aesthetics — but not interior cable runs through a block's volume. For an EE: think of it as cable trays and surface-mount raceway clipped onto a wall, as opposed to conduit buried inside it. Architecturally it is the third of three coexisting cable representations — (1) cable voxels inside chiseled microblocks, (2) dedicated pure-cable block entities, (3) decor-layer surface wiring — and the extraction layer (the code that turns world blocks into a circuit graph) consumes all three uniformly, so the solver never knows which representation a wire came from. The programming pattern is a common interface over multiple storage backends: three data formats, one consumer. Surface wiring is flagged optional/later in the chosen architecture.

"Decor layer" is standard Vintage Story engine/modding vocabulary; "surface wiring" is our name for this particular use of it.

- **Where it appears:** `docs/vintage-story.md` §1 (Decor layer; Chosen architecture item 3, with engine file:line references), `docs/design.md` R17, `docs/compaction.md` Client Intake Contracts (VS).

### Falstad / circuitjs1
- Paul Falstad's browser-based 2D circuit simulator (open-sourced as **circuitjs1**, GPLv2), which Manatee's tablet imitates in spirit and whose text file format Manatee imports.

- It is the animated schematic simulator at falstad.com/circuit — the de facto teaching simulator the tablet's guided lessons are modeled on. Manatee does not reuse its code (it is Java/GWT, GPL); we use it as (a) the reference for the tablet's look-and-feel and lesson style, and (b) the authority for the text format the lesson corpus is authored in, established by reading its `dump`/`undump` source since no written spec exists. One modeling convention matters for correctness: in Falstad, a wire is not a resistor — everything joined by wires is one ideal node, and Manatee's importer preserves that ("ideal node per wire"). For programmers: think of circuitjs1 as the upstream project whose serialization format we implement a compatible reader for; for the EE: think of it as the SPICE-with-a-schematic-editor that students actually use.
- The two names are interchangeable in our docs; "Falstad" usually means the format or the app, "circuitjs1" the specific GitHub codebase (pinned commit cited in `docs/falstad-format.md`).
- **Where it appears:** `docs/falstad-format.md` (primary sources section); `docs/api.md` (ideal-node convention, ~line 1429); `docs/design.md` The Three Clients (tablet described as "Falstad-style").
- **References:** `github.com/pfalstad/circuitjs1` (GPLv2); deployed app at falstad.com/circuit.

### Falstad format
- The line-oriented plain-text file format that circuitjs1 reads and (historically) writes to describe a circuit, which Manatee's importer consumes as the source format for the lesson and test corpus.

- Each line is one element or directive: a `$` header line (timestep, display options), then element lines of the form `TYPE x1 y1 x2 y2 flags params...` — e.g. `r` for resistor with a resistance in ohms, `c` for capacitor, `w` for wire — where the coordinates are schematic canvas pixels. Crucially, **no written specification exists**: the only authority is circuitjs1's own dump/undump source code, which `docs/falstad-format.md` reverse-engineers with file:line citations (including traps like `+` being a token delimiter, per-element flag bits that change how many parameters follow, and upstream's silently-lossy error handling, which our importer deliberately does not copy). For programmers: it is an ad-hoc positional serialization whose grammar lives only in the parser — our spec doc is the recovered grammar plus an explicit accept/reject contract. For the EE: it is like a SPICE deck that also encodes where each part sits on the page.
- Standard aliases: circuitjs1 text format, Falstad export text. (Newer circuitjs1 versions write an XML dialect; the classic text format is what we target.)
- **Where it appears:** `docs/falstad-format.md` (the full spec, title and §1); `docs/design.md` R16/R20; Electrical Age's importer in `third_party/ElectricalAge/` is a prior-art reference, including its `#`-comment extension.
- **References:** `github.com/pfalstad/circuitjs1`, `CircuitLoader.java` (reader) and per-element `dump()` methods — cited by pinned commit and line in `docs/falstad-format.md`.

### Flicker (5 Hz)
- The visible pulsing of lamp brightness when powered by low-frequency AC — in our game, a deliberate teaching device rather than a defect.

  A resistive lamp's power follows the square of the instantaneous current (P = i²R), which is why brightness peaks twice per AC cycle — on a 5 Hz sine wave it rises and falls ~10 times a second, slow enough for the eye to see, unlike real-world 50/60 Hz mains. Waterwheel and windmill alternators in the game naturally produce ~5 Hz, so early AC lighting flickers, motivating the whole progression: switch to DC lighting (the easy fix), or pursue high-pole-count alternators to raise frequency (the advanced fix), and learn why the real world settled on 50 Hz. This is also why R3 mandates *waveform-first* AC simulation — flicker only exists if the solver produces the actual time-domain waveform, not a phasor summary; think computing the real signal versus caching only its average. **Accessibility:** 3–30 Hz rhythmic flashing is the photosensitive-epilepsy trigger band and 5 Hz sits in it, so the client forces a first-boot choice between faithful lamp rendering and a low-passed (smoothed) rendering; the simulation and oscilloscope are untouched either way.

- **Standard term:** flicker is the standard lighting-engineering term for luminance modulation from supply variation; our 5 Hz operating point is a deliberate game-design choice.
- **Where it appears:** `docs/design.md` R3 (waveform-first AC justification) and the voltage/frequency-standards section (including the settled accessibility decision, 2026-07-05); `docs/curriculum.md` lessons build on it.

### Gaming tablet / tablet
- The educational in-game device in the Vintage Story mod: a ruins-loot relic containing a 2D Falstad-style schematic simulator with guided lessons and a free-play sandbox — one of manatee-core's three clients.

The fiction is that it is a surviving educational device from the world-before, which fits Vintage Story's fallen-civilization setting. Under the hood it hosts the same `Manatee.Schematic` engine as the desktop dev harness — three pure, UI-framework-free layers (document model, interaction state machine, display-list view) rendered into the game's GUI — and runs its own solver islands, fully decoupled from the survival world's circuits (a safe simulator-within-the-simulator, like a lab bench where nothing can actually catch fire; circuits drawn on the tablet never touch world wiring). Its 17-lesson curriculum (curriculum.md, DC fundamentals up through AC power and synchronization, requirement R16) "just happens" to be a tutorial for both the mod and real electricity. It is also load-bearing for development: every lesson doubles as a CI golden test (R20), and the same schematic engine is the primary dev/test harness. In the solver API it has its own configuration bundle, `NetlistOptions.Tablet()` (api.md §5), whose wiring policy is `ExplicitOnly` — ideal nodes, no hidden stamps, ground marked explicitly — so deliberately floating teaching circuits stay floating. Lessons and sandbox circuits are stored as Falstad text (falstad-format.md).
- **Project-coined term**, closest standard concept: an in-game interactive tutorial / embedded circuit-simulator sandbox.
- **Where it appears:** `docs/design.md` (client #2 / The Three Clients, R16, "The tablet"); `docs/harness.md` (tablet engine architecture); `docs/curriculum.md` (the lesson arc); `docs/api.md` §5 (`NetlistOptions.Tablet()` / `WiringPolicy.ExplicitOnly()`); `docs/falstad-format.md` §7 (Falstad text storage); `docs/vintage-story.md` (The Tablet Host).

### handbook (survival handbook)
- Vintage Story's built-in in-game reference manual, which mods extend with their own pages; in Manatee, every mod mechanic ships a handbook entry.
- The design principle (R16, settled 2026-07-06) is "the handbook is the floor; the tablet is the ceiling": the handbook is the only teaching channel guaranteed to reach players who never pick up the educational tablet, so authoring handbook entries is an explicit workstream, not polish. Where the tablet teaches circuit theory interactively, the handbook states the essential facts outright — e.g., the ruins-loot lithium-ion pages state the class property directly (every looted cell eventually fails violently; siting is the player's real decision). For the EE: think of the handbook as the equipment datasheet every user gets, versus the tablet as the optional textbook — the datasheet must be sufficient on its own.
- This is the standard Vintage Story feature name ("survival handbook"); analogous to recipe books / codexes in other games.
- **Where it appears:** `docs/design.md` R16 (floor/ceiling principle) and the Ruins loot section (li-ion policy stated in both channels).

### Harmony patch
- A specific hook installed via the Harmony library: a piece of mod code registered to run before, after, or instead of one of the game's own methods.

Harmony is the de-facto standard patching library for .NET game mods (Stationeers and RimWorld on Unity, Vintage Story on its own standalone .NET engine — mods for all of them use it). A patch names a target method and supplies replacement or wrap-around code; Harmony rewrites the method in memory when the game loads, leaving the installed game files untouched. For an EE: it is a factory-approved tap point — you solder onto a designated pad rather than cutting traces, and desoldering restores the stock board. In this project the patches are Re-Volt's: `RevoltTick` patches the game's electricity tick ahead of the per-network loop (so Manatee's solve replaces the vanilla scalar power distribution), and the `MNACableNetwork` patch substitutes a subclass that carries each network's mapping to Manatee solver nodes. If a patch fails to install, the vanilla code path simply runs.
- Standard modding term (the pardeike/Harmony library); generic name: *runtime method patch*.
- **Where it appears:** `docs/design.md` Stationeers section; `docs/stationeers.md` Integration Seams (`Patches/PowerTickPatches.cs`, `Patches/CableNetworkPatches.cs` in Re-Volt's repo).

### Insulation (as voxel material)
- The gameplay-facing side of Manatee's insulation design: insulation exists in the world as chiselable voxels of a dedicated block material (tree resin, in the fiction), so its presence or absence is visible, buildable, and destroyable like any other block geometry.
- For a programmer: instead of storing `insulated: bool` on a cable, the world's voxel data *is* the source of truth — if the resin voxels are there, the conductor is insulated; if they were chiseled off or burned away by overload, it is bare. For an EE: the game models the physical sheath, not an abstraction of it. Consequences are driven by that physical state (R12, Hazards): bare conductors short, ground when wet, and deliver shock damage scaled by voltage relative to global ground (12 V tingles, 240 V is lethal); sustained overload makes surface voxels visibly disappear, degrading the insulation before the wire itself fails. Chiseling into a live conductor shocks the player, making "turn off the breaker first" (lockout-tagout) an emergent skill rather than a scripted rule.
- Vintage Story engine support: any block can serve as a chisel material (`BlockEntityChisel.AddMaterial`, `BEChisel.cs:457`), which is what makes a separate insulation material implementable at all.
- **Where it appears:** `docs/design.md` R12 and Hazards; `docs/vintage-story.md` chisel semantics.

### Isolation-monitor device
- An advanced in-game build — a pair of lamps wired to earth — that reveals when a deliberately floating (ungrounded) electrical system has silently become grounded again.

Manatee's design lets players build floating systems (isolation transformer, no earth reference), and teaches that one wire of a *verified* floating system is safe to touch, since a shock there needs two contact points. The catch is that isolation is invisible state that can rot: a rain-wetted bare wire span somewhere out of sight can silently re-ground the system, turning the "safe" wire lethal. The isolation monitor is the surface that exposes that hidden state — with the system floating, both lamps glow dimly; when one side faults to earth, one lamp brightens and the other dims or goes dark, telling you which side re-grounded. In programming terms it is a health check on an invariant you would otherwise only discover was violated by the failure itself; the game pairs it with the multimeter's wire-to-earth reading as a taught verification ritual. The lamp-pair design is actual historical practice, not a game invention.
- **Standard term:** insulation monitoring device (IMD) / ground-fault detector for ungrounded (IT) systems; the lamp version is the classic "ground detector lamps" arrangement.
- **Where it appears:** `docs/design.md` (grounding/SWER section, safety pedagogy for floating systems); motivated in `docs/experiments/2026-07-05-adversarial-playtest.md`.
- **References:** Ground-detector lamps on ungrounded systems are long-standing practice, discussed in industrial power texts such as Beeman (ed.), *Industrial Power Systems Handbook* (McGraw-Hill, 1955); modern equivalents are standardized as insulation monitoring devices (IEC 61557-8).

### Lithium-ion (ruins-loot li-ion)
- In the Vintage Story mod's fiction, high-capacity battery cells salvaged from ruins of the world-before — enormous storage, missing every safety mechanism, and **all of them doomed to eventually fail violently**.

The design (settled 2026-07-06) makes the failure a class property, not a per-cell lottery: every looted cell will die violently someday; age and abuse only decide *when*. Per-cell degradation is deliberately invisible — the art style can't honestly telegraph it, and inspecting cells for hidden state is not the intended gameplay. Instead, the player's mistake is **siting**: a cell in a ventilated stone basement dies as a contained loss, a cell built into a wooden wall takes the house with it. This satisfies the project's failure-model corollary (hazards always trace to knowable player mistakes, never random events): the handbook and tablet state the class property outright, so the rule is knowable even though individual timing is not. For programmers, think of it as a resource with a guaranteed-eventual-failure contract and no observable health field — the correct engineering response is blast containment, not monitoring. The fiction also covers an implementation fact: all batteries ship as *behavioral* electrical models with the chemistry stubbed out; a future chemistry arc may deepen them. Real li-ion cells do ship with the safeties these ones lost (protection circuits, vents, thermal-shutdown separators) — teaching why is the standing lesson.

**Where it appears:** `docs/design.md` (Failure model corollary; Ruins loot under progression arcs; Open Questions battery-fiction resolution), `docs/solver.md` (per-chemistry battery parameter sets: pile / lead-acid / li-ion).

### Live game mutation stream
- The unending, unpredictable sequence of geometry changes (block placed, cable chiseled, regions merged or split) that a running game feeds into the solver's reduction layer — as opposed to a circuit built once and left alone.

The compaction layer maintains its reduced circuit *incrementally*: rather than rebuilding from scratch after every change, it patches only the affected part (pure additions touch the dirty area; removals coalesce into at most one island rebuild per tick). The docs are candid that incremental maintenance against a live game mutation stream **will** have edge-case bugs — the interesting failures live in rare interleavings of adds, removes, and merges that no test author anticipated. The mitigation is the resync backstop: a from-scratch rebuild is always available and cheap at island scale, and a validation mode continuously rebuilds in the background and diffs against the incrementally-maintained state, converting silent corruption into logged bug reports. For an EE, the analogy is a running checksum against a trusted reference measurement: the fast path is used, the slow path audits it. This is a standard pattern for incremental/online algorithms — maintain a delta-updated structure, verify against batch recomputation.

**Where it appears:** `docs/compaction.md` (Incremental Maintenance, Resync backstop), `docs/testing-strategy.md` (incremental equivalence testing).

### Lockout-tagout
- The real-world electrical-safety discipline of de-energizing and securing equipment before working on it, which the mod deliberately makes an *emergent in-game skill*: turn off the breaker before chiseling a live conductor.

In Vintage Story, cable voxels are never protected from the chisel (design.md Hazards, settled 2026-07-06): chiseling salvages material at a loss, and chiseling into a *live* conductor shocks you. There is no game-mechanical guard — the protection mechanism is the player's own procedure of opening the breaker first, exactly as real electricians de-energize, lock the disconnect, and tag it before touching a circuit. This fits the project's failure-model corollary: the hazard traces to a knowable player mistake (working live), and the safe procedure is teachable. The curriculum's hazard-reference appendix teaches it explicitly, alongside its known sharp edge (adversarial playtest, edge 8): a switch wired in the return leg leaves the device hot while reading "off," so the lesson must include verifying with the multimeter before trusting a switch — trust the measurement, not the label. For programmers, the analogy is acquiring the lock before mutating shared state: the discipline lives in the caller, and skipping it fails intermittently and badly.

**Standard term:** lockout-tagout (LOTO) — standard industrial-safety practice (codified in the US as OSHA 29 CFR 1910.147); our usage matches the real concept, transplanted into gameplay.

**Where it appears:** `docs/design.md` (Hazards, chisel semantics), `docs/vintage-story.md` §1 (chisel semantics), `docs/curriculum.md` (Appendices, hazard reference), `docs/experiments/2026-07-05-adversarial-playtest.md` (edge cases).

### Mechanical coupling / co-simulation
- The bidirectional link between Vintage Story's rotational-power (shaft) simulation and Manatee's electrical simulation: alternators drag on the shafts in proportion to electrical load, and motors push on them from electrical input.

The two simulations run at different rates and neither embeds the other — they *co-simulate*, exchanging a few summary numbers instead of solving one combined system. Shaft speed enters the electrical side as a piecewise-constant source parameter per electrical tick (it sets AC frequency and generator EMF, per R14: frequency = shaft speed × pole pairs); the alternator's counter-torque is reported back to the mechanical side as the **mean over all electrical substeps since the previous mechanical read** (~100 ms cadence), never an instantaneous sample — a sampled value would alias the 2×-line-frequency power pulsation into phantom torque. For programmers: this is two services integrating through a narrow, rate-limited API with time-averaged telemetry rather than sharing state. For the EE: this is textbook loosely-coupled co-simulation with relaxation, stable here because both subsystems have inertia (rotational mass on one side, storage dynamics on the other) that damps the exchange delay.

The standing invariant test: a closed motor→shaft→alternator→motor loop must monotonically decay from any initial condition — no perpetual motion via coupling artifacts.
- **Standard term:** co-simulation (weak/loose coupling); the electromechanical model itself is a simplified swing-equation treatment.
- **Where it appears:** design.md R14 and the Simulation Model section ("Mechanical co-simulation"); vintage-story.md §2 "Mechanical Network Coupling (R14)" with the engine file:line contract.
- **References:** Kundur, "Power System Stability and Control" (McGraw-Hill, 1994) for generator torque/swing dynamics; the loose-coupling scheme is standard multirate co-simulation practice.

### Microblock
- Vintage Story's sub-voxel geometry system: a single world block can be "chiseled" into a 16×16×16 grid of smaller voxels, each assigned a material — and our cables are stored as exactly such voxels made of cable materials.

In the engine, a chiseled position holds a `BlockEntityMicroBlock` storing its shape as a compact list of material-tagged cuboids (`VoxelCuboids`); the vanilla pipeline meshes, culls, and collides that geometry for free. Per requirement R17, cable materials are chisel materials, and the electrical semantics live in a mod behavior (`IMicroblockBehavior`) attached to the vanilla chiseled-block entity via JSON patch — the same pattern vanilla uses for snow cover. The electrical graph is extracted by scanning the microblock's cuboid list for cable-material voxels; the compaction layer's VS intake consumes this alongside two optimized representations (dedicated cable block entities, decor-layer surface wiring) uniformly. For the EE: think of a block as a small 3D breadboard where copper can be sculpted into the same physical volume as decorative stone (dual occupancy). Known engine constraints (one block entity per position, no pre-edit chisel veto, material blocks get no runtime hooks) are documented with file:line evidence.

This is the game community's standard term (aliases: chiseled block, chisel voxels); it is Vintage Story-specific, not an electrical or general CS concept.

**Where it appears:** `docs/design.md` (R17 and oven-as-chiseled-grooves device notes), `docs/vintage-story.md` §1 (Microblock Integration, with engine file:line references), `docs/compaction.md` (Client Intake Contracts — VS).

### multimeter
- The in-game hand-held instrument item for reading electrical quantities — voltage, current, and temperature — off cables and devices, mirroring the real bench tool of the same name.

Per requirement R15, those readouts appear in the block hover tooltip *by default* (a config option can require actually holding the multimeter item, for realism purists), and readouts teach: they name the condition in words and point upstream ("0 A — circuit incomplete", "8.2 V (rated 12 V) — undervoltage") rather than showing bare numbers. Its distinctive pedagogical job is the **wire-to-earth voltage reading**: in the grounding model, deliberately floating systems (isolation transformer, no earth reference) are buildable, and "one wire of a floating system is safe to touch" holds only after verification — the taught ritual is measuring wire-to-earth with the multimeter, because rain-wetted bare spans can silently re-ground a floating system. For a programmer: it is the read-only debug inspector for the live circuit state — the query interface players use in every predict-then-observe curriculum exercise, backed by the solver's probe API.

The term is standard EE/electrician vocabulary (also *volt-ohm meter*, VOM, or DMM for the digital kind); our in-game item deliberately matches the real tool, down to the floating-system verification practice.

**Where it appears:** `docs/design.md` R15 (Instruments) and the Grounding model section (verified-floating ritual); `docs/curriculum.md` Appendices (reading the multimeter/oscilloscope; hazard reference).

**References:** Horowitz & Hill, *The Art of Electronics* (3rd ed.), on basic test instruments and measurement practice.

### NetworkExport
- The data dump Stationeers/Re-Volt produces to describe its cable networks — cable segments, device ports, and fuses — which is the Stationeers-side input to manatee's reduction (compaction) layer.

Manatee never reads game state directly; each game client hands the reduction layer a description of its wiring in its own native shape, and for Stationeers that shape is `NetworkExport`: an adjacency listing of cable segments plus device and fuse lists attached alongside. (One of the additions agreed with Sukasa — but not yet built — will move fuses out of the side list and into the wiring itself as rated edges.) For programmers: it is the serialized graph at the boundary between two codebases — Re-Volt owns producing it, and a Re-Volt-side intake normalizer (api.md §19) reshapes it into endpoint form before manatee's reduction layer consumes it. For an EE: it is the netlist-extraction step — the game's physical cable layout captured as a connectivity list, before manatee collapses series runs into equivalent resistors. It sits alongside the other two client intakes (Vintage Story's voxel-prism pipeline and the tablet's pass-through schematic wires) as one of three front doors into the same reduction code.

**Standard term:** none exactly; it is Re-Volt's export format. Closest standard concept: netlist extraction / a serialized graph interchange format.

**Where it appears:** `docs/compaction.md` "Client Intake Contracts" (Stationeers bullet); consumed per `docs/stationeers.md` "Graph Construction".

### NetworkExport / CableInfo / intake normalizer
- The (not yet built) Re-Volt-side translation step that reshapes the raw Stationeers export into the endpoint-based form manatee's `AddSegment` API actually requires.

The raw export is the wrong shape for direct consumption: `NetworkExport.CableInfo` carries only `{ RefId, MaxVoltage, List<int> Connections }` — cable→cable adjacency, saying which cables touch but not *where* — and `DeviceInfo` carries only `{ RefId, StructureTypeName }`; the export command itself is currently a stub (`NetworkExportCommand.Execute`). Manatee's `AddSegment` instead wants edges between named junction endpoints. So before any `AddSegment`, the normalizer must (a) recover **junctions** from per-cable OpenEnds / shared grid cells (before the game's network builder discards them) and rewrite adjacency as `(JunctionKey a, JunctionKey b)` edges, (b) resolve each **device to the junction of its power port**, and (c) express **fuses as rated edges**. For programmers: a classic adaptor/normalization layer between two data models — edge-list-of-touching-items in, endpoint graph out. For an EE: the raw export says "these two wires are soldered together somewhere"; the solver needs actual node identities, so the normalizer reconstructs the nodes. Fields like `c.A`/`c.B`/`c.PrefabName`/`d.Port` in the §22.a walkthrough are *normalizer outputs*, not raw export fields; the normalizer plus the agreed export additions gate the 10k-segment intake benchmark (open item §23.8).

**Project-coined term** ("intake normalizer"), closest standard concepts: an Adapter (Gamma et al.) performing graph re-representation / node recovery, akin to netlist extraction's node identification step.

**Where it appears:** `docs/api.md` §19 (intake contract), §22.a (post-normalization walkthrough), §23.8 (unbuilt status); `docs/stationeers.md` "Graph Construction" for the agreed export additions.

### Oscilloscope
- The instrument — both a real-world device and, here, an in-game item and tablet lesson — that draws voltage as a curve over time, so alternating current can be *seen* rather than inferred from a single number.

In the real world an oscilloscope plots a probed voltage against time on a screen; it is the standard tool for looking at waveforms. In Manatee it appears twice. As a **lesson**, it anchors curriculum arc III: lesson 11 ("What alternation looks like") uses the scope to explain the deliberately instructive 5 Hz waterwheel lamp flicker, and the appendices teach reading it alongside the multimeter. As an **item**, requirement R15 specifies an oscilloscope themed as a pencil-servo plotter (a pen driven across paper — period-appropriate for Vintage Story) that shows *real* waveforms, which is why requirement R3 makes the solver waveform-first: AC is simulated as actual time-domain samples, not as a summary phasor, precisely so the scope has something true to draw. Its solver contract is **two probes at a time**, because the phase lessons (15 and 17) need to compare two traces side by side. Even when a player enables the photosensitivity low-pass option for lamp rendering, the scope still shows the true waveform — display accessibility never falsifies the instrument. For a programmer: it is a live line chart fed by a ring buffer of per-substep samples; the two-probe limit is a deliberately bounded subscription API.

The term is fully standard EE vocabulary (alias: "scope"); only the pencil-servo-plotter theming and the two-probe cap are ours.

- **Where it appears:** `docs/design.md` R3, R15, flicker-accessibility note, adversarial-review resolution ("the oscilloscope contract is two probes"); `docs/curriculum.md` lesson 11 and Appendices.
- **References:** Horowitz & Hill, "The Art of Electronics", 3rd ed., Cambridge University Press, 2015 (scope usage throughout, esp. the introductory chapters).

### pathfinding (cable-laying)
- The placement mechanic in which the game automatically finds and lays a cable route between two points from a single player gesture, instead of the player placing every cable block by hand.

Manatee's design thesis is that difficulty should be *mental, not physical*: understanding the circuit is the challenge, so physically building it must be near-frictionless (requirement R13, following the earlier sparky prototype). Pathfinding here is the classic graph-search sense a programmer knows from A* in games — the engine computes a valid path through the voxel world and places conductor blocks along it. For the EE reader: it is the equivalent of a CAD auto-router for in-world wiring. A project-specific twist: by default the pathfinder routes conductor *pairs* — one gesture lays both supply and return — because the grounding model uses an explicit two-wire idiom (there is no implicit ground; a circuit missing its return conductor simply does not conduct). The frictionless mandate extends to maintenance: consumables like zinc plates or burned cable sections swap in one interaction.

If our term IS standard: "pathfinding" is the standard game-programming term (A*, Dijkstra); the EE-adjacent analogue is PCB auto-routing.
- **Where it appears:** `docs/design.md` — R13 (Frictionless placement), the educational thesis ("mental, not physical"), and the Grounding model section (two-wire pair routing).
- **References:** Hart, Nilsson & Raphael (1968), "A Formal Basis for the Heuristic Determination of Minimum Cost Paths" (the A* paper), IEEE Trans. Systems Science and Cybernetics; Cormen, Leiserson, Rivest & Stein, *Introduction to Algorithms* (shortest-path algorithms).

### perish-rate delegate
- Shorthand for the food-spoilage callback described above, viewed from the game-design side: the channel through which the mod's electrical/heat simulation actually changes food behavior in Vintage Story.

Because vanilla VS rooms have no temperature state (temperature is recomputed from climate every time it is asked for, and no vanilla block warms a room), the mod maintains its own **heat registry** — a position-indexed record of heat published by every dissipating electrical device. The perish-rate delegate is one of the registry's consumers: freezers hook it to slow spoilage in proportion to electrical duty, and other containers can query the registry and fold ambient heating into their perish math. For a programmer: the registry is the data source, the delegate is the sink the engine polls. For an EE: the heat registry is our own measurement bus, and the perish-rate delegate is the one terminal the stock instrument exposes for injecting a reading. This is how "heat is universal" (every device dissipates) becomes player-visible consequences — food keeps or rots — without modifying the engine.

**Project-shorthand usage** of the standard VS hook `Inventory.OnAcquireTransitionSpeed`; "delegate" is standard C# vocabulary for a callback function.
- **Where it appears:** `docs/vintage-story.md` §Rooms and Heat (heat registry, Freezer, Space heater bullets); `docs/design.md` (heat convention paragraph, "Device notes").

### Port
A device's named connection point in the Stationeers network export — the specific spot on a machine where a cable attaches.

Re-Volt hands manatee-core the game's wiring as a `NetworkExport` adjacency: cable segments, fuses, and device **ports**. Recording *which* port a device connects through (agreed as an addition to Sukasa's format) matters because a device can have several electrically distinct terminals; the port identity is what lets the adaptor attach the device's model at the correct node after the reduction layer has collapsed cable chains. For the programmer: a port is like a named socket or interface endpoint on a service — the adjacency list says "cable 42 plugs into port `PowerIn` of device 7", not just "cable 42 touches device 7". For the EE: it is simply a device terminal, as recorded in the game's wiring data rather than on a schematic.

Note the term is narrower than the EE textbook "port" (a *pair* of terminals treated together, as in two-port network theory) — here it is a single attachment point. Coupler ports (`CouplerPorts` in the API) are the same word used for the coupler's fixed attachment nodes.

**Where it appears:** `docs/compaction.md` §Client Intake Contracts (Stationeers bullet); `docs/stationeers.md` §Graph Construction ("device port identity per connection", "devices attach at their recorded ports").

### power tick (0.5 s)
- Stationeers' built-in half-second heartbeat for updating its power grid — the game loop slot that Re-Volt hijacks and where Manatee's DC solve runs.

The vanilla game recomputes electrical state once every 0.5 seconds per cable network, through a three-phase contract (`Initialise` → `CalculateState` → `ApplyState`). Re-Volt already replaces this tick wholesale via a Harmony-injected `RevoltTick : PowerTick`, and Manatee slots into exactly those phases: topology rebuilds on `Initialize_New`, stamp updates on `CalculateState_New`, the MNA solve at the old `_powerRatio` solve point, and write-back in `ApplyState_New`. The 0.5 s period is also the simulation timestep (dt) for the Stationeers DC analysis — battery charge and other storage dynamics integrate in half-second steps, with no subcycling. For an EE: this is a fixed-step discrete-time simulation with dt = 0.5 s, chosen by the host game rather than by the solver; it runs in-band within the game's single simulation loop, on a ThreadPool worker (thread identity not stable), not on a dedicated power thread and not on the main thread — so nothing may key state to thread ID.

Standard game term ("tick" = one iteration of a fixed-rate update loop); the specific 0.5 s value is Stationeers' convention, verified against the decompiled game source.
- **Where it appears:** `docs/stationeers.md` Integration Seams, Performance (Cadence), Threading; `docs/design.md` The Three Clients.

### predict-then-observe Q/A
- The question-and-answer element inside a tablet lesson: a prompt that asks the player for a predicted value, then reveals the simulated result for comparison.

In `docs/harness.md` (Layering), the lesson engine that drives these Q/A prompts — along with narrative pages — sits *beside* the harness's three pure layers (document, interaction state machine, display-list view) as "document-plus-content orchestration, equally UI-free". That means the Q/A flow is data and logic with no widget-toolkit dependency, so it is testable headlessly the same way the interaction layer is. For an EE: the lesson engine is like the script of a lab exercise, kept separate from the bench equipment (the rendering backends) so the same script runs on any bench. Each Q/A's expected number also lives in the lesson's machine-readable front-matter, which CI checks against the solver (see *predict-then-observe*).

**Where it appears:** `docs/harness.md` (Layering, closing paragraph), `docs/curriculum.md` (Format: README Q/A section), `docs/design.md` (The tablet).

### prefab / gauge
- Per-cable metadata — the Unity prefab name identifying the cable type, and its wire gauge — that Re-Volt's intake normalizer must attach to every cable segment before Manatee can build the electrical graph.

Stationeers' raw `NetworkExport` carries only `{ RefId, MaxVoltage, Connections }` per cable — enough to know *that* cables connect, not *what* they are electrically. Per-cable prefab/gauge is one of the export additions agreed with Sukasa (alongside junction endpoints, device port identity, and fuses-as-rated-edges): the normalizer supplies it so segment resistance (cable type × length) and current/thermal ratings can be computed. For a programmer: it is the type tag that selects which `CableSpec` record parameterizes the edge. For an EE: it is the wire's AWG-style size and construction, without which you cannot assign resistance or ampacity. As of api.md §23 note 8, the export additions and normalizer are agreed but not yet built, and they gate the 10k-segment intake benchmark.

**Where it appears:** `docs/api.md` §19 (Stationeers intake normalization; `c.PrefabName` in the §22.a walkthrough is a post-normalization field, not a raw export field), `docs/stationeers.md` (Graph Construction).

### schematic wire
- A wire drawn in the 2D tablet/harness schematic editor, which — following the usual schematic convention — is an ideal (zero-resistance) connection between the points it joins.

Manatee's compaction layer exists because game worlds produce absurdly redundant electrical graphs (a hundred voxels of cable that are electrically one conductor), and those must be collapsed before solving. Schematic wires are the degenerate easy case: a circuit drawn by hand on the tablet is already near-minimal, so the compaction layer passes it through essentially unchanged. Each wire is treated as an ideal node — everything it touches is the same electrical point, per the Falstad simulator convention — with optional per-wire resistance available for advanced lessons that teach wire losses. For a programmer: the three intake clients (Vintage Story voxels, Stationeers cable networks, the 2D schematic) feed one normalization pipeline, and the schematic client is the identity-transform case that keeps the pipeline honest. For an EE: this is just the standard idealization that schematic wires are perfect conductors unless you explicitly model otherwise.

- **Where it appears:** `docs/compaction.md` — Client Intake Contracts (Tablet/2D harness); the editor itself is specified in `docs/harness.md` (document model, layer 1).

### Stationeers / Re-Volt
- The second host game for manatee-core and the mod that carries it there: **Stationeers** is a space-base survival game, and **Re-Volt** (author: Sukasa) is the mod that replaces its vanilla power tick with a real DC circuit solve.

Stationeers already has player-built cable networks and a fixed 0.5 s power tick; Re-Volt consumes manatee-core as a git submodule and feeds those existing networks into the solver instead of the game's simple power bookkeeping. The integration is DC-only (no AC waveforms), configured as `NetlistOptions.Stationeers`: the `Dc` solver profile (DC operating point each tick, plus Backward-Euler storage dynamics for batteries), `ReferenceBound` wiring (the game has one-wire cables, so every device implicitly returns current to its network's reference node — there is no second wire to run), and `ClientPartitioned` islanding (the game's own `CableNetwork` ids define the partitions, rather than the solver discovering connectivity itself). Unconverted vanilla devices are bridged by a constant-power adaptor (R18), so the mod can be adopted incrementally and removed cleanly. For an EE: think of Re-Volt as the test fixture and wiring loom that connects an existing plant (the game) to our analyzer (manatee-core). Integration decisions affecting Re-Volt are relayed to Sukasa for sign-off; Re-Volt lives in its own repository.
- **Where it appears:** `docs/stationeers.md` (the whole integration doc), `docs/design.md` (delivery order, R18, Stationeers client section), `docs/api.md` (`NetlistOptions.Stationeers`, worked example), `docs/integration-tutorial.md` / `examples/RevoltWalkthrough/`.

### Sukasa
- The author of Re-Volt (the Stationeers electricity-overhaul mod) and Manatee's collaboration partner for everything on the Stationeers side of the integration.

- Re-Volt consumes manatee-core as a git submodule inside its own repository, so the seam between the two projects is jointly owned: integration decisions that affect Re-Volt — the `NetworkExport` intake format and additions to it (breaker/fuse positions), real player-facing voltage tiers, the one-global-tick threading model, the external save/load hook, and API changes such as bidirectional breakers or `Reconfigure` semantics — are explicitly settled with, or flagged as requiring sign-off from, Sukasa (design.md; stationeers.md; api.md §23.1). Several api.md items carry a "needs Sukasa sign-off" marker as an open gate. In engineering terms, Sukasa is the counterparty on an interface contract: the docs treat his agreement the way one would treat approval from the owner of a downstream system before changing a shared protocol.

**Where it appears:** CLAUDE.md working agreements; design.md (delivery order, per-network model, Simulation Model); stationeers.md throughout; api.md §23 sign-off items.

### sync-check breaker
- A craftable in-game device that refuses to close a breaker connecting two AC sources unless they are in phase — the game-design "automation off-ramp" for the generator-synchronization skill.

Connecting two running AC generators is only safe when their voltages, frequencies, and phase angles match; closing the tie out of phase causes a violent transient (in Manatee's world, a hazard the player learns to avoid). Initially the player synchronizes manually — matching speed by ear and judging the moment — which is a genuine thrill the first few times and a chore forever after. The sync-check breaker embodies the design's mastery corollary (*every learned skill gets an automation off-ramp*): once crafted, it blocks out-of-phase closure automatically, while the player still does the coarse speed-matching. It is a convenience, never a requirement. Programming analogy: it is a precondition guard or interlock — an assertion at the API boundary that rejects an illegal call rather than letting the system corrupt itself — retrofitted after the developer has internalized why the precondition exists.

The device is real-world standard practice: a **synchronism-check (sync-check) relay**, ANSI/IEEE device number 25, supervising a breaker close. Our usage matches the real device; "sync-check breaker" names the breaker-plus-relay combination as one craftable item.

**Where it appears:** `docs/design.md` — Mastery corollary (settled 2026-07-06); origin in `docs/experiments/2026-07-05-adversarial-playtest.md`.

**References:** IEEE Std C37.2 (standard electrical power system device function numbers; device 25 = synchronizing/synchronism-check); Horowitz & Hill, *The Art of Electronics*, for general AC background.

### Tablet Engine
- The name for the `Manatee.Schematic` codebase in its role as the engine inside the in-game gaming tablet — the same code that powers the desktop dev harness and the lesson authoring tool.

harness.md's central claim is that one codebase is three things at once: the dev/test bed for manatee-core, the engine inside the VS tablet, and the lesson authoring tool. That works because `Manatee.Schematic` is three *pure* layers — document model, interaction state machine, and a view layer emitting a display list — none of which import a UI framework. When embedded in Vintage Story, only the thin edges differ: VS input is adapted into the same abstract events, and the display list is rasterized to an in-game texture/dialog instead of a desktop Skia canvas. For an EE: it is one reference design dropped into different enclosures — same board, different front panels — so validation done on the bench (headless CI tests) carries over to the fielded unit.

**Project-coined term**, closest standard concept: a reusable embeddable UI component / engine with pluggable rendering backends (the "humble object" / ports-and-adapters style of keeping logic UI-free).
- **Where it appears:** harness.md, title and intro ("The 2D Schematic Harness (and Tablet Engine)"); Layering and Rendering Backends sections.

### Telegraph
- A Vintage Story Arc 2 build: long-distance Morse signalling over real simulated copper, with relays acting as repeaters when line loss demands them.

The telegraph is a curriculum-bearing game system, not a decoration: because the mod simulates real resistance, a long line genuinely attenuates the signalling current, and past some distance a receiving sounder can no longer pull in — at which point the player rebuilds the historical solution, a relay used as a repeater (a weak incoming current closes a relay that re-drives the next line segment from a fresh local battery; a digital signal regenerator, in systems terms). It is also one of the sanctioned uses of ground-return (SWER) wiring: telegraphy is milliamp signalling, exactly the regime where tens-of-ohms earth-electrode resistance is affordable, so the historically accurate single-wire-plus-earth line actually works — unlike power delivery, where the design mandates two-wire. For programmers: the telegraph arc teaches attenuation, thresholds, and repeaters the way a networking course teaches why long links need regeneration. The word also appears in the docs in its ordinary English sense ("telegraphed" = made visible in advance); this entry covers the device.

**Where it appears:** `docs/design.md` — Arc 2 (progression arcs) and the Grounding model section (SWER's legitimate milliamp territory: telegraph, doorbell, electric fences); relay lessons in `docs/curriculum.md`.

**References:** For relay/repeater practice in historical telegraphy, any standard history of telegraph engineering applies; the underlying circuit behavior (line resistance, relay pull-in thresholds) is covered qualitatively in Horowitz & Hill, *The Art of Electronics*.

### tick / game tick / electrical tick / power tick
One discrete step of a game's simulation loop — the fixed heartbeat at which the world (and our circuit solve) advances. It is the *outer* clock inside which Manatee runs, and the unit in which all performance is measured.

Games do not simulate continuously; they advance the world in fixed increments, and each host game hands Manatee its own fixed cadence. Vintage Story's **electrical tick** is 50 ms — a project-coined name for the VS-side cadence, an interval our own mod chooses via its tick listener (VS mods each pick their own; a 2026-07-05 doc review settled 50 ms, an earlier 33 ms figure was spurious). Stationeers' **power tick** is 0.5 s (500 ms) — the vanilla game's own name for the step on which all its electrical machinery updates, which Re-Volt already owns, and it is *self-clocked*: the worker thread sleeps out the remainder of each 500 ms after doing its work, rather than being driven by an OS timer or the render frame. "tick" / "game tick" is standard game-dev vocabulary (aliases: simulation step, update step); "electrical tick" and "power tick" are the two host-specific names for the same outer clock.

Manatee's binding execution model is **one global body per game tick**: all pending topology changes, device drive updates, one `Solve`, and all readback happen in a single fixed-order pass, even when the game itself iterates its networks one at a time (Stationeers' `ElectricityTick` visits each `CableNetwork` separately, so Re-Volt hooks in *before* that per-network loop and runs the Manatee body once). The game tick is the outer loop; the solver may take several internal time-domain steps per game tick when accuracy demands it — AC islands *subcycle* N steps per game tick (~20 samples per cycle of the highest frequency present; e.g. 5 substeps at 5 Hz, ~50 at 50 Hz inside a single `Solve`). The tick length also serves as the solver's integration timestep dt (Backward Euler at 0.5 s for Stationeers DC, 50 ms for VS DC-side transients), except for those subcycled AC islands, where the numerical timestep is the substep, not the tick. Distinguish a tick from a solver *substep* (see subcycling), which is strictly finer.

For an EE, a tick is exactly the timestep h of a discretized transient analysis — the interval at which the companion models are re-evaluated — except the game, not the solver, chooses it and it never adapts; subcycle steps are the actual integration timesteps running underneath, and results only surface to the game at tick boundaries. For a programmer, the game tick is the frame budget and subcycling is an inner loop amortized inside it. Several Manatee mechanisms are defined *across* ticks rather than within one: the legacy-device adaptor's constant-power linearization uses last tick's voltage (G = P/V_prev², clamped), generators carry an across-tick current clamp, adapted devices keep an across-tick energy ledger, and dirty-island rebuilds coalesce to at most once per tick.

Because the tick is the fixed unit of work, every performance number is denominated in it: benchmarks (testing-strategy.md) measure per-tick cost (subcycled AC island solve time, zero-allocation steady state), and design.md's budgets are per-tick ceilings — VS: ≤ 5 ms on-thread / ≤ 20 ms off-thread; Stationeers: fit inside the half-second tick on its worker thread. The EE framing is a fixed-rate DSP loop: the tick is the sampling period of the whole system and the budget is the requirement that all computation completes within one sample. Component values in VS are deliberately chosen so time constants sit at 0.1–10 s, where Backward Euler at 50 ms stays accurate.

**Standard term:** standard game-development vocabulary (aliases: simulation step, update step); corresponds to the fixed timestep h of numerical integration and is distinct from a solver *substep* (strictly finer). "electrical tick" is project-coined for our VS 50 ms cadence; "power tick" is Stationeers' own name for its 0.5 s electrical update step.

**Where it appears:** throughout the docs — `docs/design.md` (Simulation Model, Performance Targets table, R18), `docs/stationeers.md` (throughout), `docs/solver.md` (subcycled AC), `docs/compaction.md` (Incremental Maintenance — once-per-tick rebuild coalescing), `docs/testing-strategy.md` (Benchmarks), `docs/integration-tutorial.md` §5 (the tick loop), `docs/api.md` §22.a; open-questions notes (the 33 ms figure was spurious; 50 ms is settled).

**References:** Nagel (1975), SPICE2 memo UCB/ERL-M520 (timestep-driven transient analysis, the fixed-step analogue of which a tick is).

### Tooltip / block hover tooltip
The small text panel Vintage Story shows when you look at a block, which Manatee uses as its default in-world instrument: live volts, amps, and temperature on every electrical block (R15).

Mechanically it is the VS `Block.GetPlacedBlockInfo` → `BlockEntity.GetBlockInfo` hook (the "WAILA-equivalent"): our block entities append lines to the hover text. The design rule is that **readouts teach** — a tooltip names the condition in words and points upstream, never just a number: "0 A — circuit incomplete", "8.2 V (rated 12 V) — undervoltage", "14 A through an 8 A-rated section". For an EE, think of it as a free clamp meter plus a fault annunciator built into looking at the equipment; for a programmer, it is a structured log line derived from solver state, rendered at the point of interest. A gotcha documented in vintage-story.md: the HUD polls the *client's* copy of the block entity, so displayed values must be synced over a throttled telemetry channel (~0.5–1 s per island), not pushed every tick. A config option can require the multimeter item instead, for realism purists.

**Where it appears:** `docs/design.md` R15; `docs/vintage-story.md` "Tooltips and Instruments (R15)" (with engine file:line references).

### UniTask
- The third-party async/await library (Cysharp's UniTask) that Stationeers is built on for moving work between the main thread and background threads; it is the "house style" Manatee must follow inside Re-Volt.

Unity games have one privileged *main thread* that owns all engine objects; heavy work must run elsewhere and its results handed back. UniTask is an allocation-light replacement for C#'s built-in Task that makes those handoffs explicit: `UniTask.SwitchToThreadPool()` hops the current code onto a background worker, `SwitchToMainThread()` hops it back, and `.Forget()` fires work without awaiting it. The decompiled game shows the *entire* simulation (atmospherics, then power, then logic) running sequentially inside one such loop, `GameManager.GameTick`, on a pool thread at a self-clocked 500 ms cadence. Since there is no Job System or `Task.Run` in the sim path, Manatee's off-band work (tier-3 rebuilds, load-time rebuild-everything) uses the same idiom: compute on the pool, marshal results back via `SwitchToMainThread()`/`.Forget()`. For the EE reader: it is the plant's changeover switchgear — a formal, low-overhead procedure for transferring a job between the one "control room" (main thread) and the "shop floor" (worker pool), and everything in this facility uses that one procedure.
- **Standard term:** standard usage — UniTask is a real, widely-used open-source library (Cysharp/UniTask) implementing C# async/await for Unity.
- **Where it appears:** `docs/stationeers.md` (Threading, Performance — verified against the decompiled game, file:line cited there), `docs/api.md` (save/load runs as a background UniTask).

### vanilla
- The unmodified game as shipped by its developers — its stock devices, behaviors, and code paths, before any mod (including ours) changes anything.

This is standard gaming/modding vocabulary: "vanilla Stationeers" means the game with no mods, a "vanilla device" is one the mod has not converted to a native Manatee model. In our docs the word carries real design weight: unconverted vanilla Stationeers devices are driven through an adaptor as constant-power elements (design.md R18); vanilla bookkeeping is deliberately kept up to date so uninstalling the mod reverts a save cleanly; and the API enforces the invariant "vanilla networks never merge" — the game's own cable-network identities stay separate even when the solver electrically couples them, with guards that reject edits which would merge two vanilla networks/partitions (a junction claimed by two partitions throws `PartitionMergeException`; foreign-island *reads* remain legal via last-good values). For the EE: think of vanilla as the plant's legacy control system that we must keep energized and consistent while our new instrumentation runs in parallel, so the old system can take over untouched if ours is removed.

**Standard term** in game modding; alias: "unmodded", "stock".

**Where it appears:** `docs/stationeers.md` (Legacy-Device Adaptor, Islands, Persistence); `docs/design.md` R18; `docs/api.md` §7 (vanilla-metric derivation), §11, §22 (partition/foreign-network guards).

### vanilla 4-call power API
- Stationeers' stock interface for powering devices: four method calls per device per power tick, which Manatee's adaptor drives so unmodified devices keep working under the real solver.

The four calls are the game's question-and-answer protocol: a device reports how much energy it can *provide* or *needs*, and is then told how much it actually *provided* or *drew*, all in joules per half-second tick. Requirement R18 says every unconverted ("vanilla") device is represented in the solver as a constant-power element via this interface, linearized across ticks with a brownout clamp — a single voltage threshold below which the load is cut off; above it, the voltage-collapse death spiral is real physics and a shipped feature. After each solve the adaptor writes the *actual* delivered energy back through the same calls. Keeping the vanilla side of the ledger accurate is what makes the mod cleanly removable — vanilla state never goes stale. For programmers: it is a legacy API kept as the system of record while a new engine computes the truth, an anti-corruption/adapter layer in the Gamma et al. sense. For the EE: the game speaks only in energy-per-tick bookkeeping (watt-hours, effectively); voltage and current exist only on our side of the adaptor.

**Project-coined name** for a specific game interface; closest standard concepts: legacy API adaptation via the Adapter pattern.

**Where it appears:** `docs/design.md` R18; `docs/stationeers.md` §Legacy-Device Adaptor; `docs/integration-tutorial.md` §2 (single-terminal vanilla devices, `Wiring = ReferenceBound`).

**References:** Gamma, Helm, Johnson & Vlissides, *Design Patterns* (Adapter).

### Vintage Story GUI
- The in-game rendering backend that draws the tablet's display list onto a Vintage Story texture or dialog, so the schematic tablet appears inside the game.

The harness architecture ends in a *display list* — a flat sequence of drawing commands ("stroke this path, fill that shape, draw these glyphs") that is pure data with no opinion about how pixels get made. Multiple rasterizers consume that one contract: SkiaSharp on the desktop and in headless tests, and the Vintage Story GUI backend in-game, written only when the VS layer starts. Because the layers beneath it are already fully tested, the VS backend itself only needs *equivalence spot-checks* against the Skia output plus manual protocols — the same display list rendered two ways should look the same. For an EE: this is like having one schematic netlist that can be plotted by two different plotters; you verify the plotters agree on a few test plots rather than re-verifying the netlist per plotter.

- **Where it appears:** `docs/harness.md` (Rendering Backends); implemented in the Vintage Story mod layer, not manatee-core.

### Vintage Story (VS)
- One of the two host games manatee-core serves: a voxel survival game for which the Manatee mod adds AC electricity — physical voxel cables, generation tied to the mechanical (windmill/waterwheel) network, and the educational "gaming tablet".

Vintage Story actually contributes *two* of the project's three clients: the survival world (voxel cables where cross-section is ampacity, insulation is a material, and frequency is a real quantity derived from shaft speed) and the in-game tablet (a 2D Falstad-style schematic simulator with the lesson curriculum, running its own solver islands decoupled from the world). In solver configuration terms, the world client uses the **Mixed** profile (subcycled AC plus DC), the **TwoWireLeak** wiring policy (two-wire circuits kept numerically grounded by an implicit ~1 MΩ leak from each device negative to earth), and **SelfPartitioned** mode (the core's ConductorGraph discovers electrical islands itself, since VS has no pre-existing network objects) — `NetlistOptions.VintageStory(tickDt)` bundles all three. This contrasts point-for-point with Stationeers/Re-Volt, which is DC, ground-return, and client-partitioned along the game's existing cable networks.
- **Where it appears:** `docs/design.md` (The Three Clients; Vintage Story: Game Design; Grounding model); `docs/vintage-story.md` (the full integration doc); `docs/api.md` (`NetlistOptions.VintageStory`, wiring/partitioning enums). The game itself is by Anego Studios; "VS" is our shorthand throughout the docs.

### voltage tier
- A named, player-facing nominal voltage level in the game world — the voltage a device's nameplate claims and a transformer steps between (e.g. Vintage Story's 12 V / 48 V / 240 V).

In both game integrations, voltage is real gameplay vocabulary rather than an internal solver detail. Requirement R19 (settled with Sukasa, 2026-07-02) commits Re-Volt to real tiers instead of keeping vanilla Stationeers' watt-only semantics with MNA hidden underneath; vanilla's cable "MaxVoltage" (which is actually a watt rating) is reinterpreted as a genuine voltage rating. Specific tier values are per-game design calls: Re-Volt will likely use modern 120/480/5k-style tiers, while Vintage Story uses 12 V DC as the safe starter tier and 48 V / 240 V for distribution, with devices tolerating roughly ±25% off-nameplate at an efficiency/lifetime cost. For programmers: a tier is like an API version or wire protocol — components on both ends must agree on it, and a transformer is the explicit adapter between versions. For EEs: this is just standard nominal system voltage / voltage class practice, made into a game mechanic.

**Standard term:** nominal (system) voltage, voltage class/level — "tier" is our gameplay word for the same idea.
- **Where it appears:** `docs/design.md` R19 and "Voltage and frequency standards"; `docs/stationeers.md` "Voltage Tiers".

### voxel
- A voxel is the smallest unit cube of block geometry in a voxel game — the 3D equivalent of a pixel.

In Vintage Story, each world block can be subdivided into 16×16×16 microblock voxels via the chisel system, and Manatee builds its cables out of exactly those voxels. A voxel is not merely visual: cable voxels carry electrical semantics, so the cross-sectional voxel area of a conductor run determines its ampacity (current-carrying capacity), its material sets resistivity, and hazard attribution maps solver events back to specific voxels ("which voxel melts, which fuse pops", design.md Hazards). For the EE, think of the voxel grid as the physical layout: geometry the extraction layer scans and converts into a circuit netlist, much as a PCB layout tool extracts a schematic from copper geometry. Standard game-development term (portmanteau of "volume element"); Vintage Story calls the 1/16-scale ones *microblock voxels*.

**Where it appears:** design.md R10–R12, R17, Hazards; vintage-story.md sec 1 (Microblock Integration) and Wire Rendering; compaction.md throughout (voxel regions are what series collapse reduces).
