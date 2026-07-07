# Glossary: Programming & Software

*Last updated: 2026-07-07. Part of the [Manatee glossary](../glossary.md) — see the index there for all terms and for how entries are structured.*

Software concepts for readers coming from electrical engineering: the vocabulary the design docs assume.

### Abstract input events
- Toolkit-independent descriptions of user input — pointer down/move/up, wheel, key — that the schematic editor's interaction layer consumes instead of listening to any particular window system.

Every GUI framework (the desktop harness's windowing library, Vintage Story's in-game GUI) reports mouse and keyboard activity in its own native format. Rather than letting the editor logic depend on one of those, each host translates its native input into a small shared vocabulary of plain data records, and layer 2 (the interaction state machine) only ever sees that vocabulary. For an EE, the analogy is a standard connector: the interaction layer defines a fixed pinout, and each host supplies its own adaptor cable from whatever signal it natively produces. The payoff is testability — a test can synthesize a script of these events ("pointer down at the resistor palette, drag, release") and drive the editor headlessly, with no window, GPU, or game running.

**Standard term:** abstracted / synthetic input events (the same idea underlies browser-automation tools like Playwright and the standard adapter pattern).
- **Where it appears:** `docs/harness.md` — Layering (layer 2, Interaction state machine) and Testing Model (interaction tests as scripts of synthetic events).
- **References:** Gamma, Helm, Johnson & Vlissides, *Design Patterns* (1994) — Adapter pattern.

### Accumulator
- A software pattern that banks elapsed real (wall-clock) time and pays it out in fixed-size simulation steps, so the solver always advances by the same dt regardless of frame rate.

Game frames arrive at irregular intervals (16 ms, then 33 ms, then 12 ms...), but the tablet's circuit simulation wants a fixed 10 ms timestep — fixed dt is what keeps it on manatee-core's cheapest fast path and keeps lesson results reproducible. The accumulator is a single running variable: each frame adds the real time elapsed, and the sim executes as many whole 10 ms steps as the balance covers, carrying any remainder forward. For an EE, it behaves like a charge-pump or integrate-and-fire circuit: a continuous, irregular input integrates on a capacitor, and each time the stored quantity crosses a fixed threshold it fires one discrete, uniform event. Sim time is thereby decoupled from world time and frame rate; a slow frame just triggers several steps at once.

Note a separate use of the same word elsewhere in the project: the i²t **thermal accumulator** (solver.md, api.md §19) is a physics state variable integrating current-squared-times-time for fuse/cable overload — the same integrate-and-threshold shape, but simulated heat rather than banked time.

**Standard term:** fixed-timestep accumulator (widely known from Glenn Fiedler's "Fix Your Timestep!" game-loop essay); also called a time accumulator.
- **Where it appears:** `docs/vintage-story.md` — The Tablet Host (fixed sim dt stepped from the client tick via an accumulator).

### active tool
- The editing mode the user has currently selected in the schematic editor — e.g. "place resistor", "draw wire", "select" — held as state by the interaction state machine.

In the 2D harness's three-layer split, layer 2 (the interaction state machine) owns *all* stateful editing behavior: the active tool, any drag in progress, the current selection, hover, and snapping. The active tool determines how the next abstract input event (pointer down/move/up, wheel, key) is interpreted: the same click places a component under one tool and starts a wire under another. Because this state lives in a pure, toolkit-free layer, tool behavior is testable headlessly by feeding synthetic event scripts. For an EE: it is exactly the function dial on a multimeter — one physical probe action (the click) means different things depending on which mode the dial is set to, and the dial position is remembered by the instrument, not by the probes or the display.
- Standard UI/editor terminology (also "current tool", "tool mode"); the underlying structure is the classic State pattern.
- **Where it appears:** `docs/harness.md`, Layering item 2 (interaction state machine).
- **References:** Gamma, Helm, Johnson & Vlissides, *Design Patterns: Elements of Reusable Object-Oriented Software* (State pattern).

### adjacency
- The connection list of a circuit graph: for each cable segment (or node), the list of things it touches — the raw "what is wired to what" data before any electrical values enter the picture.

In computer science this is the *adjacency list*, the standard way to store a graph: each vertex carries a list of its neighbors. For an EE: it is the wiring diagram stripped of all component values — pure topology, the same information a netlist's node columns carry. In Manatee's Stationeers intake, Sukasa's `NetworkExport` supplies adjacency at cable-segment granularity as `CableInfo { RefId, List<int> Connections }`, plus separate device and fuse lists. Agreed additions to the export (negotiated, not yet the shipping baseline) move more information into the graph itself: device port identity per connection, per-cable type/gauge, and fuse positions *in* the adjacency — a fuse becomes an edge with a current rating rather than a side-list entry, so the graph itself knows where protection sits. The reduction layer (compaction) consumes this adjacency, turns segments into resistor edges and junctions into nodes, and collapses series chains.

Standard CS term ("adjacency list"); no project-specific meaning beyond the segment granularity and rated-edge convention.
- **Where it appears:** `docs/stationeers.md` (Graph Construction); `docs/compaction.md` (Client Intake Contracts).
- **References:** Cormen, Leiserson, Rivest & Stein, *Introduction to Algorithms* (CLRS), ch. on elementary graph algorithms / graph representations.

### allocation discipline / zero-allocation / zero-alloc gate / no per-tick allocation
The binding requirement (design.md R8) that the solver's steady-state per-tick hot path allocates **zero** heap bytes from the runtime after warmup, together with the machinery that enforces it.

C# is garbage-collected: memory is handed out from a managed heap and a background collector periodically pauses the program to reclaim it. Those pauses arrive at unpredictable moments — fatal inside a game loop that must finish its work in a fixed slice of every frame, and doubly so on Stationeers' shared background worker, where a hiccup in the power sim delays everything else on that thread. The discipline is the software analogue of designing for steady-state operation with no transients: all buffers, matrices, event rings, and scratch space are sized and claimed once during setup ("warmup" and structural/"shape" changes), and the repeating tick only reuses them. "Steady state" means change-cost tiers 0–2 (value changes, switch flips, numeric refactorization); structural rebuilds (tier 3) have left steady state and may allocate. "After warmup" excludes one-time JIT and first-solve setup costs — the first `Factorize` legitimately allocates the symbolic pattern; the second lands on the frozen-refactor path. Clients share the obligation: drain buffers allocated at shape time, no LINQ or closures inside the tick loop. In EE terms: all the parts are procured up front; the running circuit never places a new order. The constraint even drove backend selection — CSparse.NET was demoted partly because its public API forces an unavoidable 8n+24-byte allocation per refactorization.

Enforcement is two-pronged. The **binding gate** is BenchmarkDotNet's `MemoryDiagnoser` running in CI on CoreCLR: it measures actual bytes allocated per operation for the standing benchmark suites and fails the build if any steady-state path allocates at all, **with no exemptions**. The **in-process tripwire** is the best-effort complement: entering the per-tick drive region via `Netlist.EnterSteadyState()` returns a `SteadyStateGuard` that bars structural edits and arms an `AllocationSentinel`, which snapshots the thread's allocated-bytes counter and asserts a zero delta on exit — like a residual-current breaker that trips the moment current leaks where none should. The tripwire is best-effort because the underlying counter (`GC.GetAllocatedBytesForCurrentThread`) is inert on Mono/Unity (one of the two shipping runtimes), so it self-disarms there via the `ZeroAllocGates.CounterIsReliable` capability probe. That same counter *over-reports* by a few hundred phantom bytes when runtime machinery (background/compacting GC, tiered-JIT recompilation) retires the thread's partially-used allocation quantum mid-measurement; because the perturbation is strictly additive, the sound gating mechanism is **min-over-N-sub-runs** — one clean sub-run (minimum delta == 0) proves the property, whereas a single-shot assertion would flake.

**Standard term:** zero-allocation / allocation-free hot path (a common discipline in game and low-latency .NET programming); "GC-free steady state". The min-over-sub-runs trick and the `SteadyStateGuard`/`AllocationSentinel` names are project conventions.

**Where it appears:** `docs/design.md` R8 ("Thread purity and allocation discipline"); `docs/solver.md` (Numerics; Threading and Allocation Contract); `docs/api.md` §1 (notation `0B` = zero heap after warmup), §8 (`SteadyStateGuard`/`AllocationSentinel`), §16 (0B-after-warmup table), §21; `docs/testing-strategy.md` (Benchmarks — the zero-alloc gate and its flake-isolation note); `docs/integration-tutorial.md` §9 (integration checklist) and sharp edge 14.

**References:** Jones, Hosking & Moss, *The Garbage Collection Handbook* (why GC pauses motivate allocation-free hot loops); BenchmarkDotNet documentation (MemoryDiagnoser).

### Allocation quantum
The block of heap memory the .NET runtime hands each thread so it can satisfy small allocations quickly without taking a global lock.

Instead of negotiating with the shared heap for every object, a thread is given a private chunk (the quantum, also called an *allocation context*) and carves objects out of it locally — the same idea as a device keeping a local reservoir so it doesn't hit the main supply on every draw. The catch for us: the per-thread counter `GC.GetAllocatedBytesForCurrentThread` charges the thread for the whole quantum when the runtime retires a partially-used one mid-measurement (e.g. during a concurrent compacting GC or tiered-JIT recompilation). A step that truly allocated nothing can then show a phantom delta of hundreds of bytes to ~8 KB. Our zero-allocation tests defend against this with the min-over-N-sub-runs pattern: the noise is strictly additive, so a single sub-run measuring zero proves the code path is clean.

**Standard term:** allocation context / thread-local allocation buffer (TLAB in JVM terminology).

**Where it appears:** `docs/testing-strategy.md` (Benchmarks, flake-isolation note for the zero-alloc gate); echoed in `docs/integration-tutorial.md` sharp edge 14.

### Arena / presize hint / geometric growth
Three related memory-allocation tactics manatee-core uses so that building a large circuit does not spend its time asking the runtime for memory: pre-allocated storage blocks (arenas), caller-supplied size estimates (presize hints), and doubling buffers when an estimate proves too small (geometric growth).

An *arena* is one big block of memory allocated up front, from which many small items are placed side by side — the physical analogy is ordering one full reel of wire instead of a thousand individual cut lengths. The *presize hints* (`NetlistOptions.ExpectedIslands` / `ExpectedNodes`, and `BeginBulkBuild(expectedSegments)`) let the client tell the engine roughly how big the circuit will be, so arenas start near the right size; the docs stress these are **hints, not caps** — underestimating never fails, it just costs a little extra work. When staging buffers do overflow the hint, they grow *geometrically* — capacity doubles (2×) each time — so loading a 10k-segment network reallocates only O(log n) times (a handful of doublings) instead of once per segment; a final compaction pass then copies into exact-size arenas with no wasted slack. Decision log §24.17 pins this behavior to a standing benchmark. For the EE reader: this is purely about memory-management efficiency and has no effect on the circuit mathematics.

These are all standard programming techniques; "geometric growth" is also called *amortized doubling* (it is how dynamic arrays like C#'s `List<T>` or C++'s `std::vector` work), and "arena" is also called a *region* or *bump allocator*.

**Where it appears:** api.md §5 (`NetlistOptions.ExpectedIslands/ExpectedNodes`), §6 and §19 (`ConductorGraph.BeginBulkBuild` staging), §24.17 (decision log).

**References:** Cormen, Leiserson, Rivest & Stein, *Introduction to Algorithms* (amortized analysis of table doubling, ch. 17 in the 3rd ed.).

### Assembly / namespace / InternalsVisibleTo
The C#/.NET packaging and visibility mechanisms manatee-core uses to enforce, at compile time, who is allowed to touch what.

An *assembly* is a compiled, shippable unit of code — a `.dll` file; manatee-core ships as exactly one, `Manatee.Core.dll`. A *namespace* is a named grouping of code inside it (`Manatee.Core`, `Manatee.Core.Solver`, `Manatee.Core.Devices`, `Manatee.Core.Reduction`, `Manatee.Core.Diagnostics`). Code can be marked `public` (usable by anyone referencing the assembly) or `internal` (usable only inside the assembly); `InternalsVisibleTo` is an escape hatch that grants named *friend* assemblies access to internals. Manatee grants that friendship only to the test and oracle assemblies, so tests can inspect the solver's matrices while game clients never can — the doc rule is "clients never see a matrix," and, notably, the reduction layer gets **no** friendship either: it must live on the same public API as any game client, so there is no second, untested path into the solver (a binding invariant, decision log §24.16). The EE analogy: the assembly is the sealed instrument enclosure, the public namespaces are its front-panel connectors, `internal` is the circuitry inside, and `InternalsVisibleTo` is the calibration port only the factory test rig may plug into.

These are standard .NET terms (`InternalsVisibleTo` is formally the `InternalsVisibleToAttribute`); the general concept is *friend assemblies*, analogous to C++'s `friend`.

**Where it appears:** api.md §2 (assembly and namespace layout table, visibility rules), §24.16.

**References:** ECMA-335, *Common Language Infrastructure (CLI)*, for the assembly model; Microsoft .NET documentation on `InternalsVisibleToAttribute`.

### Atomic / transactional commit
The rule that a batch of staged circuit edits is applied all-or-nothing: either every change in the batch takes effect together, or — if anything in it is illegal — none of it does.

Structural changes go through a `StructuralEdit` batch: the client stages any number of adds/removes, then calls `Commit()` (or `Dispose()`, which commits). If the batch contains a contract violation — the two named cases are `RemoveNode` on a node that still has components attached (non-degree-0) and an edit that would merge two client-declared partitions (`PartitionMergeException`) — `Commit()` throws and **the whole batch aborts with nothing applied**. The payoff is the invariant that at every observable instant the netlist is a consistent circuit; no client, and no crash, can ever observe a half-applied edit (api.md §17.1: "structure is transactional and immediate; matrices are coalesced and lazy" — the matrix *rebuild* is deferred, but the topology change itself is atomic). For the EE reader: it is like a make-before-break switch bank ganged on one shaft — all contacts move together or the mechanism refuses to actuate; the circuit is never caught in an in-between wiring state.

These are standard terms from database transactions — the A (atomicity) in ACID; "transactional" and "all-or-nothing" are common aliases.

**Where it appears:** api.md §6 (`StructuralEdit.Commit`/`Abort`/`Dispose`), §17.1 (commit-is-atomic rule), §20 (error-model row: throws from `Commit()`, whole batch aborts).

**References:** Gray & Reuter, *Transaction Processing: Concepts and Techniques* (Morgan Kaufmann, 1993), for the atomicity concept.

### Avalonia
- An open-source, cross-platform UI framework for .NET (the C# ecosystem) that we use to build the desktop harness application's window, panels, and controls.

`Manatee.Harness` is an Avalonia app: it hosts the Skia drawing canvas plus authoring conveniences (lesson text pane, component palette, probe readouts) and runs on Linux, Windows, and macOS from one codebase. Crucially, Avalonia sits deliberately *outside* the tested tablet stack: nothing in `Manatee.Schematic` (the pure document / interaction / display-list layers) may reference it, so the shell stays swappable and carries almost no test burden — the layers beneath carry the coverage. For an EE, the analogy is instrumentation architecture: Avalonia is the front panel and enclosure, while the measurement core inside is built and verified independently so the panel could be replaced without re-qualifying the instrument.

The name is standard — it is the framework's actual product name (sometimes styled AvaloniaUI), broadly similar in role to WPF or Qt.

**Where it appears:** `docs/harness.md` (The Desktop Shell); the `Manatee.Harness` project.

### Avalonia.Headless
- Avalonia's official testing mode that runs the UI framework without opening a real window, letting automated tests drive the desktop shell on a machine with no display.

Normally a UI app needs a graphics environment (a desktop session, a window server) to run at all — a problem for automated test machines, which typically have none. Avalonia.Headless substitutes a fake display so the whole application can start, lay out its controls, and respond to simulated input entirely in memory. In this project it enables rare end-to-end tests of the `Manatee.Harness` shell, but the design keeps such tests deliberately scarce: the pure layers below (document, interaction state machine, display list) are where the coverage lives, and the SkiaSharp rasterizer has its own headless pixel tests. The EE analogy: bench-testing an instrument's electronics with the front panel unplugged, exercising it through the test connector instead of the knobs.

The name is standard — it is the official Avalonia testing package.

**Where it appears:** `docs/harness.md` (The Desktop Shell).

### barrier / readback phase
The synchronization point at which every parallel island `Step` for the tick has finished (the barrier), and the phase after it in which the client is allowed to read results back out (the readback phase).

Manatee's tick is phased: topology edits, then device drive + `Step` fanned out across worker threads (one writer per island), then — only after the thread pool's wait call returns (`_pool.Wait(); // readback phase begins only after the barrier`) — the readback phase, where the client drains the solution (voltages, currents, powers for game devices and meters), limit events, and telemetry. A "barrier" is the standard concurrency primitive: no thread proceeds past it until all have arrived, like a checkpoint where every worker must clock in before anyone opens the results. The phase boundary is load-bearing for correctness: `LastTickStats` is aggregated per island in deterministic order *at* the readback barrier (never a shared write during Solve), reading it mid-Step is a phase error (debug assert), and reading a foreign island's `RawVector` is only safe for an island that is not concurrently stepping — own island, or anywhere once under the barrier. For the EE reader: it is the moment the whole network's solution for this timestep is committed and self-consistent, so meters read a single snapshot rather than a mix of two timesteps.

Standard terms (barrier synchronization; "readback" is common GPU/simulation vocabulary for draining results from a compute stage).

**Where it appears:** api.md §9 (TickStats accumulation), §21 (threading contract), §22.b (client-driven Step loop); integration-tutorial.md tick-loop section.

**References:** for barrier synchronization, any standard concurrency text, e.g. Herlihy & Shavit, *The Art of Multiprocessor Programming*.

### BE behavior / block-entity behavior
A pluggable component that Vintage Story attaches to a block entity via JSON configuration — the mechanism by which the Manatee mod adds electrical logic to the game's vanilla chiseled blocks without replacing them.

In Vintage Story, a *block entity* (BE) is the per-position object holding a block's mutable state and tick logic; the engine allows exactly one per position, which rules out placing our own cable entity where a chiseled block already sits. The escape hatch is the behavior seam: block JSON's `entityBehaviors` list attaches extra behavior objects to a vanilla BE (instantiated in `BlockEntity.CreateBehaviors`, vsapi `BlockEntity.cs:103`), and mods can both register new behavior classes and JSON-patch them onto vanilla blocks. For microblocks specifically, `IMicroblockBehavior` (vssurvivalmod) gets called back on every voxel edit (`RebuildCuboidList`), mesh regeneration, and rotation. The vanilla `BEBehaviorMicroblockSnowCover` is the proven template — it keeps its own parallel data, serializes its own state, and overlays its own mesh — so we call this the "snow-cover pattern": Manatee's behavior likewise piggybacks on the chiseled-block BE, scanning `VoxelCuboids`/`BlockIds` on edit to maintain the electrical graph. For the EE reader: the block entity is a standard chassis with one slot per grid position, and a behavior is a daughterboard — it rides the host, receives the host's event signals, and carries its own state, without modifying the chassis.

Standard Vintage Story engine term; note the collision with "BE" for Backward Euler elsewhere in this glossary — context disambiguates (docs about the VS mod vs. docs about numerics). Closest general concept: the composition-over-inheritance component pattern (Gamma et al., *Design Patterns*: Decorator/Strategy family).

**Where it appears:** vintage-story.md sec 1 (microblock integration, verified engine facts with file:line) and sec 2 (mechanical coupling).

### BeginBulkUpdate / BulkUpdate
- A `using`-scoped batching API on the netlist that defers tier-3 (topology) rebuilds until the scope exits, so many add/remove operations trigger one solver rebuild instead of one each.

In our change-cost tiers, adding or removing a component is tier 3 — the most expensive kind of change, requiring a full symbolic + numeric rebuild of the affected island's matrix. `using (netlist.BeginBulkUpdate()) { ... }` marks a region where such changes are collected and coalesced; the rebuild happens once when the block ends (C#'s `Dispose` mechanism guarantees this runs even on early exit). For an EE: it is like making all your wiring changes with the power off and only then re-energizing and re-measuring once, rather than re-solving the whole circuit after each individual wire is moved. For programmers, this is the familiar begin/commit transaction or scoped-batch idiom.

**Standard concept:** batching / deferred recomputation via a disposable scope (the C# `IDisposable`/`using` idiom); analogous to a transaction commit.
- **Where it appears:** `docs/solver.md` "The Netlist API" (tier 3 — topology, batched); tier taxonomy in `docs/solver.md` "Change-Cost Tiers"; the load-path counterpart at the reduction layer is `BeginBulkBuild`.
- **References:** the dispose-pattern/scope idiom is standard C# (Microsoft, *Framework Design Guidelines*, Cwalina & Abrams); conceptually akin to the Command/transaction batching patterns in Gamma et al., *Design Patterns* (1994).

### behavior (IMicroblockBehavior / BE behavior)
- A plug-in component that Vintage Story lets mods attach to an existing block entity ("BE"), which is how our electrical semantics ride along on the game's vanilla chiseled-block entity without replacing it.

In Vintage Story, a *block entity* is the per-position object holding a block's runtime state; a *behavior* is a smaller component attached to it that adds capabilities. Requirement R17 attaches a mod behavior implementing `IMicroblockBehavior` (from `vssurvivalmod/Systems/Microblock/IMicroblockBehavior.cs`) to the vanilla chiseled-block entity via a JSON patch — the same mechanism vanilla uses to put snow cover on blocks, hence "the snow-cover pattern." The behavior scans the block's voxel data for cable materials and carries all electrical logic, because the engine allows only one block entity per position and gives material blocks themselves no runtime hooks. For an EE: the chiseled block is a standard chassis, and the behavior is an option card slotted into it — the chassis stays vanilla, the card adds the electrical function, and both occupy the same physical slot.

**Standard concept:** the composition-over-inheritance / component pattern; "behavior" is Vintage Story's own engine term for it.
- **Where it appears:** `docs/design.md` R17 (Vanilla microblock integration), `docs/vintage-story.md` (engine facts with file:line references).
- **References:** Gamma et al., *Design Patterns* (1994) — Decorator/Strategy are the closest classical patterns; the entity-component style is standard in game engines (e.g. Nystrom, *Game Programming Patterns*, "Component").

### BenchmarkDotNet
- The standard .NET benchmarking framework; Manatee uses it to measure how fast (and how allocation-free) solver operations are, with the rigor of a lab instrument rather than a stopwatch.

Benchmarking code naively (run it once, time it) gives noisy, misleading numbers because of JIT warmup, CPU frequency scaling, and garbage collection. BenchmarkDotNet is the well-established open-source tool that handles this properly: it warms up the code, runs many iterations, and reports statistically sound means and error bars — for an EE, the difference between eyeballing a scope trace and doing a proper repeated measurement with stated uncertainty. Manatee inherited this discipline from the sparky prototype and ships it as `scripts/bench.sh` (`smoke`/`run`/`compare`), which also diffs results against the parent commit so a performance regression shows up as a concrete before/after delta.

This is the standard, widely used framework of that name (benchmarkdotnet.org), not project code.
- **Where it appears:** testing-strategy.md (Toolchain, Benchmarks), design.md (Testing), CLAUDE.md (Practical notes), `scripts/bench.sh`.

### BenchmarkDotNet / MemoryDiagnoser
- The CI benchmarking harness plus its memory-measurement plugin, which together form Manatee's *binding* zero-allocation gate: proof that steady-state ticking allocates no heap memory.

MemoryDiagnoser is a BenchmarkDotNet component that counts exactly how many bytes each benchmarked operation allocates on the managed heap. Manatee's threading contract requires that steady-state ticking (tiers 1–2) allocate zero bytes after warmup — allocations eventually trigger garbage-collection pauses, which would stutter a game. For an EE: allocation is like drawing transient current from a shared supply — each draw is tiny, but the supply periodically browns out to recover (a GC pause), so the contract is "no draw at all in the hot loop," verified by an ammeter (MemoryDiagnoser) rather than by trust. The MemoryDiagnoser gate in CI is the *binding* enforcement with no exemptions; the in-process allocation sentinel that integrators can enable is only a best-effort tripwire (tutorial appendix). The harness is jj-aware: `scripts/bench.sh compare` benchmarks the current commit (`@`) against its parent (`@-`) and diffs Mean and Allocated columns.

Both names are standard: they are the real BenchmarkDotNet framework and its stock MemoryDiagnoser attribute.
- **Where it appears:** solver.md (Threading and Allocation Contract), testing-strategy.md (Benchmarks, incl. flake-isolation note for the zero-alloc gate), design.md (Testing), integration-tutorial.md (sharp-edges appendix), CLAUDE.md.

### BFS (breadth-first search)
- A graph traversal algorithm that explores outward from a starting point in expanding "rings" — all neighbors first, then neighbors-of-neighbors — until a boundary or size limit stops it.

For an EE, BFS is the algorithmic form of a flood fill: pour dye into one node and watch it spread one hop at a time through every connection, never leaking through barriers. It is the standard way to answer "what is connected to what," which makes it the natural tool both for discovering electrical networks (which components form one island) and for spatial queries. In Manatee's docs the specific citation is Vintage Story's room detection: the engine runs a BFS bounded to a 14×14×14 volume outward from a position, treating heat-retaining walls as barriers, to decide whether the position is inside an enclosed room (`vsessentialsmod/Systems/RoomRegistry.cs` — the traversal lives in `FindRoomForPosition`, invoked via the public cache-lookup entry point `GetRoomForPosition`). The bound matters — it caps worst-case cost regardless of world size, the same reason our solver caps other per-tick work.

Standard CS term; the flood-fill framing is the same algorithm applied to a grid.
- **Where it appears:** vintage-story.md §3 (Rooms and Heat); the same connected-component idea underlies island discovery in solver.md.
- **References:** Cormen, Leiserson, Rivest & Stein (CLRS), *Introduction to Algorithms*, ch. on elementary graph algorithms.

### Blittable
A C#/.NET term for a struct whose in-memory bytes can be copied verbatim — it contains only plain fixed-size values (numbers, enums, other blittable structs), never strings or references to other objects.

If a struct is blittable, storing or transmitting it is a raw memory copy: no pointer-chasing, no garbage-collector involvement, no per-item heap allocation. Manatee's `TopologyJournal` requires this — it is a fixed-capacity ring of blittable event structs, so appending an event and replaying thousands of them costs zero allocations and constant time per event, which the solver's allocation budget demands. The "no strings" consequence is deliberate: an event identifies things by numeric IDs and keys, never by name. For an EE the analogy is a fixed-width binary telemetry frame versus a free-text log line: every frame has the same byte layout, so a recorder can DMA it straight to a buffer, whereas variable-length text needs parsing and dynamic storage.

**Standard term:** yes — "blittable type" is the official .NET term (from "blit", block transfer); the closest general CS notion is a POD (plain-old-data) type in C/C++.

**Where it appears:** `docs/api.md` §15 (TopologyJournal: "fixed-capacity ring of blittable structs").

**References:** Microsoft .NET documentation, "Blittable and Non-Blittable Types" (interop marshaling).

### boxing
- Boxing is what the .NET runtime does when a small stack-allocated value (a struct) has to be wrapped in a heap-allocated object — for example, to call it through an interface naively — and it is forbidden on manatee-core's hot paths because every box is a fresh heap allocation.
- In C#, value types (our 12-byte component handles like `ResistorId`) normally live inline, cost nothing to copy, and never involve the garbage collector. But if code treats one *as* its interface type (`IComponentId`), the runtime silently copies it into a new heap object first — that copy is the "box". A physical analogy: it is like being unable to hand someone a coin directly, so you buy a padded shipping box for it every single time; the coin is fine, but the boxes pile up and someone (the garbage collector) must periodically haul them away, causing pauses — unacceptable inside a game's per-tick solve loop. The project's binding rule (api.md §3, decision log #7) is that generic readbacks such as `Solution.Current<TId>` must use the *constrained-callvirt* pattern (`where TId : struct, IComponentId`), which the JIT compiles to a direct call on the value type with provably zero boxing — on both .NET and Unity's Mono runtime. This is what keeps tier-1 readbacks at 0 bytes allocated.
- Standard .NET term; the inverse operation is "unboxing". See also "constrained-callvirt" (the IL instruction prefix that avoids it).
- **Where it appears:** `docs/api.md` §3 (the binding implementation note on `IComponentId`), §8 ref-struct notes, decision log #7; enforced by the CI MemoryDiagnoser benchmarks.
- **References:** ECMA-335 Common Language Infrastructure standard, Partition III (the `constrained.` prefix and `box` instruction); Richter, "CLR via C#" (4th ed., Microsoft Press) ch. 5 on boxing.

### cable-set diff
- Comparing the set of cables a network contained before and after a change to work out which cables were actually added or removed.

Manatee's incremental update path wants targeted notifications ("this cable was cut"), but the relevant Stationeers hook, `RebuildNetwork`, is a whole-network flood: it re-derives the entire membership of a `CableNetwork` from scratch without saying what changed. To feed the incremental path, the adaptor must therefore snapshot the previous cable set, take the new one, and compute the set difference — the removed cables are `before − after`, the added ones `after − before`. An EE analogy: it is like being handed two complete netlists of the same board, before and after a rework, and having to deduce the rework yourself by comparing them, because nobody recorded which trace was cut. This is flagged in `docs/stationeers.md` as a revision-pending correction to the earlier claim that the game's hooks fire per topology change.

Standard set-difference computation; "diff" is the common programming term for it (as in file diffs).
- **Where it appears:** `docs/stationeers.md` revision-pending header note (the `RebuildNetwork` caveat).
- **References:** Set operations are standard; see e.g. Cormen, Leiserson, Rivest & Stein, *Introduction to Algorithms* for the underlying data-structure operations.

### canChisel
- A Vintage Story block attribute (`"canChisel": true` in a block's JSON definition) that forces the engine to accept that block as chisel material.

Vintage Story's chisel tool normally consults a whitelist to decide which blocks may be carved into microblocks (`ItemChisel.IsValidChiselingMaterial`, `ItemChisel.cs:309–361`); setting `canChisel` on a block bypasses that check and forces acceptance (`:331`). It matters for us because cable-material blocks (copper wire, heating wire, insulation) become legal chisel materials, so cable voxels ride the vanilla microblock pipeline for free. Note the flag only governs first-time conversion: once a block is already a microblock, `ItemChisel.IsChiselingAllowedFor` (`:298`) permits further chiseling only if the block is a `BlockChisel` instance, and it never consults `canChisel` for microblocks — so a cable-bearing block must actually remain a `BlockChisel` to stay editable (the chosen architecture uses the vanilla `BlockEntityChisel`/`BlockChisel`, which satisfies this). For an EE: this is a declarative configuration flag, like a jumper on a board that enables a factory-disabled feature — data the engine reads, not code we write.

**Where it appears:** `docs/vintage-story.md` §1 (chisel-material and re-chiselability facts, with engine file:line references); `docs/design.md` R17 (chosen microblock architecture).

### capability probe
- A small runtime test that checks whether a platform feature actually works before relying on it, instead of trusting version numbers or documentation.

Our concrete instance is `ZeroAllocGates.CounterIsReliable` (see `core/Manatee.Core.Tests/Solver/ZeroAllocationTests.cs`): the zero-allocation test gates depend on .NET's per-thread allocation counter, `GC.GetAllocatedBytesForCurrentThread()`, but on some runtimes (historically Mono, which game engines embed) that counter is inert and always reads zero — which would make every allocation test pass vacuously. The probe deliberately allocates a 4 KB array and checks that the counter moved; if it did not, the alloc gates auto-skip rather than report false confidence. For an EE this is exactly a self-test or instrument calibration check: before trusting the ammeter, pass a known current through it and confirm the needle moves — a reading of zero from a dead meter is worse than no reading.

**Standard term:** feature detection / runtime capability check (idiomatic in cross-platform code; contrast with version sniffing).

**Where it appears:** `docs/testing-strategy.md` (Benchmarks section flake-isolation note and Game-Layer Testing, referencing the §8 probe); `ZeroAllocationTests.cs` in the core test suite; the binding zero-alloc enforcement itself is BenchmarkDotNet MemoryDiagnoser in CI (`docs/api.md` §21).

### Category=Oracle
- The test label (an xUnit "trait") that marks tests which depend on the external ngspice simulator, so they can be run or excluded as a group.

  In .NET's xUnit test framework, a trait is a key/value tag attached to a test — think of it as a colored sticker on a lab notebook page that lets you pull out just the pages of one kind. Tests carrying `Category=Oracle` run manatee's solver against ngspice (the reference simulator, our "oracle") and diff the results. Two policies hang off the tag. First, the fast inner development loop excludes them: `dotnet test --filter 'Category!=Oracle'` runs everything else without needing ngspice installed. Second, when the full suite *does* run, Oracle tests **hard-fail** if ngspice is missing rather than politely skipping — a silently skipped oracle is the one failure mode the testing strategy cannot afford, because these tests are the primary evidence that the circuit math is right. For an EE: it is the difference between "the calibration standard was unavailable, measurement postponed" (loud failure) and "we shipped without calibrating and nobody noticed" (silent skip).

  `Category` is the conventional xUnit/VSTest trait name for grouping tests; "oracle" is standard software-testing vocabulary for an independent source of expected results.
- **Where it appears:** `docs/testing-strategy.md` (Toolchain — Oracle policy; Oracle Tests section); project `CLAUDE.md` build/test instructions; the trait itself lives on tests under the `Oracle/` test tree.
- **References:** the ngspice manual (ngspice.sourceforge.io) for the oracle itself; xUnit.net documentation for traits and test filtering.

### Central package management / warnings-are-errors
Two build-hygiene policies for the C# solution: every dependency's version is pinned in one shared file, and any compiler warning fails the build outright.

*Central package management* is a .NET feature where a single `Directory.Packages.props` file at the repository root declares the version of every third-party package; individual projects reference packages by name only. For an EE: it is the difference between every schematic sheet naming its own part numbers and one master bill of materials the whole design draws from — no two sheets can disagree about which revision of a part is in use. *Warnings-are-errors* (`TreatWarningsAsErrors`) means the compiler's advisory diagnostics — "this looks suspicious but is legal" — are promoted to hard build failures, so the codebase stays at zero warnings permanently instead of accumulating ignored ones; think of it as a test bench where any out-of-tolerance reading, not just a dead short, fails the unit. Both are standard, widely used .NET practices, adopted here across `Manatee.Core` (which targets both `netstandard2.1` for Unity/Re-Volt and `net8.0`) and all dev-side projects.

Standard terms: NuGet "Central Package Management" (CPM) and MSBuild `TreatWarningsAsErrors`.

**Where it appears:** `docs/testing-strategy.md` (Toolchain — targets and build policy); enforced by `Directory.Packages.props` and the project files.

### chiseledblock / BlockChisel
- The vanilla Vintage Story block type (code id `chiseledblock`, C# class `BlockChisel`) that backs all chiseled/microblock geometry — the container our cable voxels live inside.

When a player chisels a block, the engine replaces it with a `chiseledblock` whose block entity holds the voxel-level shape data; the original blocks survive only as "materials" referenced for textures, light, and sounds. `BlockChisel` is a *class* in the object-oriented sense: a template defining behavior, of which every chiseled block in the world is an instance — for an EE, think of it as the standard footprint/package that any carved block must conform to. Two consequences matter to us. First, once a position is a `chiseledblock`, the engine invokes behavior only on that container, never on the material blocks inside it. Second, re-chiselability: for microblocks, `ItemChisel.IsChiselingAllowedFor` short-circuits to permit only `BlockChisel` instances, so other microblock types cannot be edited further — cable-bearing blocks must remain `BlockChisel` to stay player-editable. (The `canChisel` attribute is a different lever: it governs whether a *non-microblock* block may be converted into a `chiseledblock` in the first place, not re-editing an existing microblock.)
- **Where it appears:** `docs/vintage-story.md` sec 1 (engine facts with file:line into `vssurvivalmod`); `docs/design.md` (dual-occupancy note near line 203).

### Chunk-baked mesh
- Geometry that is merged into a chunk's single static 3D model when the chunk is (re)built, instead of being drawn separately every frame — the efficient rendering path Manatee uses for voxel cable segments.

A "mesh" is the triangle model the graphics card draws; "baking" means computing it once, up front, and reusing the result — precomputation, like memoizing an expensive function. Vintage Story rebuilds each chunk's combined mesh only when something in the chunk changes, then draws the whole chunk in very few GPU calls; per-frame renderers, by contrast, pay their cost 30–60 times a second forever. The EE analogy: baked geometry is like a printed circuit board (laid out once, then just *there*), while per-frame rendering is like re-breadboarding the circuit every time you power it on. Manatee's voxel cable meshes are chunk-baked via the `OnTesselation` + `mesher.AddMeshData` hook, with `MarkDirty(true)` forcing a re-bake when the cable's visible state changes; long catenary wire spans use a separate live renderer instead, since they cross chunk boundaries.
- **Where it appears:** `docs/vintage-story.md` "Wire Rendering" (voxel-cable bullet, with vanilla precedents at file:line).

### Chunk repacket / repacket spam
- The network cost Vintage Story pays when a block entity is marked dirty: the server re-serializes that block entity's data and re-sends it to every client in range — one packet per dirtied block entity, per call.

Calling `MarkDirty()` on a block entity resends that single block entity's serialized data to all in-range clients (the containing chunk is only flagged dirty for *disk save*, not re-sent over the network; the genuinely chunk-sized cost is the client-side re-tesselation triggered by `MarkDirty(true)`, a rendering cost). Fine for occasional edits — ruinous if done every tick for live telemetry. "Repacket spam" is our shorthand for that failure mode: per-tick dirtying of many electrical block entities would flood clients with redundant per-entity packets. The EE analogy is measurement bandwidth: you would not re-transmit an instrument's entire serialized state to every observer 20 times a second just to update one meter reading; you'd stream just the reading, at a rate the display actually needs. Accordingly, Manatee's tooltip/instrument values go over a throttled custom network channel (`mna-telemetry`, ~0.5–1 s per-island probe broadcasts, following the mechanical mod's ~800 ms precedent), with a temporary high-rate stream negotiated only for the oscilloscope's single probe point.
- **Where it appears:** `docs/vintage-story.md` sec "Tooltips and Instruments (R15)" (the `MarkDirty()` gotcha and telemetry-channel plan).

### CI
- Short for **continuous integration**: the automated build-and-test run that executes on every change to the repository, with no human in the loop.

In the curriculum context, CI is the machinery that keeps the lessons honest. Every tablet lesson ships a Falstad-format netlist (`lesson.txt`) plus machine-readable numeric expectations (probe, time, value, tolerance) in its front matter; on every build, CI parses each lesson, solves it with manatee-core, and checks the results against both ngspice (the reference simulator) and the numbers the lesson's narrative states. Design requirement R20 makes this binding: "a lesson that stops being true fails the build." For an EE, think of CI as an automated test bench that re-measures every reference circuit after any change to the instrument, and refuses to ship if a measurement drifts outside tolerance.

**Standard term:** continuous integration (universal software-engineering usage).

**Where it appears:** `docs/curriculum.md` (Format section — `lesson.txt` "parseable by CI", front-matter expectations), `docs/design.md` R20 (lesson corpus as goldens), `docs/testing-strategy.md`.

**References:** M. Fowler, "Continuous Integration" (martinfowler.com, 2006); P. M. Duvall, S. Matyas & A. Glover, *Continuous Integration: Improving Software Quality and Reducing Risk* (Addison-Wesley, 2007).

### CI (continuous integration)
- The always-on automated test pipeline; in the compaction docs specifically, the place where the resync-backstop validation and the equivalence/golden suites run continuously.

CI runs every test on every change, so regressions are caught the moment they are introduced rather than in play-testing. The compaction layer leans on this hard: because incremental maintenance of the reduced circuit against a live stream of game mutations "WILL have edge-case bugs" (the docs' own words), a validation mode rebuilds the compacted representation from scratch in the background and diffs it against the incrementally-maintained state, logging any discrepancy. That mode ships as a debug config in-game and runs *continuously in CI*, converting silent divergence into concrete bug reports. The EE analogy: a second, independently-built instrument permanently wired in parallel with the production one, with an alarm on any disagreement between their readings.

**Standard term:** continuous integration.

**Where it appears:** `docs/compaction.md` (Incremental Maintenance → Resync backstop), `docs/design.md` (testing section), `docs/testing-strategy.md` (incremental equivalence).

**References:** M. Fowler, "Continuous Integration" (martinfowler.com, 2006).

### CI-friendly / CI
- "CI-friendly" means a test can run unattended in the continuous-integration pipeline: deterministic, headless (no display, GPU, or human), and identical in result on every machine.

The harness is architected so the tablet UI is testable this way despite being interactive graphics. Interaction tests feed scripted synthetic events (pointer down, drag, release) into the pure interaction state machine and assert on the resulting document — no window ever opens. Display-list snapshots compare the drawing *commands* (stable text) rather than pixels, sidestepping font and GPU nondeterminism; only a deliberately small pixel-snapshot suite rasterizes through headless SkiaSharp with a bundled font. Lesson-corpus scenarios (build, edit, save/load, fault) likewise run headless in CI. The EE analogy: designing a circuit with a built-in test header so the whole thing can be exercised by ATE on the production line, instead of needing a technician with scope probes — the three-pure-layer split is that test header.

**Standard term:** continuous integration; "CI-friendly" is common informal usage for "suitable for automated headless runs".

**Where it appears:** `docs/harness.md` (Testing Model — interaction tests, display-list snapshots, pixel snapshots, lesson-corpus integration), `docs/testing-strategy.md`.

**References:** M. Fowler, "Continuous Integration" (martinfowler.com, 2006).

### Client-side only
- A deployment choice for the tablet: its circuit simulations run entirely on the player's own machine (the game *client*) and never involve the game server.

Multiplayer games split work between a shared *server* (the authoritative world everyone sees) and each player's *client* (their local view). The tablet — the in-game teaching device — keeps its islands purely local: lessons and the sandbox are private to one player, so nothing about them needs to be agreed on over the network. An EE analogy: it is a bench instrument on your own desk, not part of the plant's shared SCADA system — no one else's readings depend on it, so it needs no telemetry link. This is safe only because the solver core is deterministic by contract (same inputs → bit-identical outputs, per solver.md), which means a lesson's recorded golden results behave identically on any player's machine and in the CI test farm, without a server arbitrating.

**Where it appears:** `docs/vintage-story.md` "The Tablet Host" (first bullet of the proposed defaults).

### Closure-free / cached delegate schedule
- The project's mandatory pattern for running per-island work on multiple threads without allocating any memory during the tick: the "what to run" function objects are created once at setup and reused every tick.

In C#, a *delegate* is a first-class reference to a method, and a *closure* is what the compiler builds when a lambda captures surrounding variables — a small hidden object allocated on the heap each time. Naively writing `Parallel.For(..., i => worker(i, localState))` therefore allocates every tick, which violates the core's steady-state 0-bytes-per-tick contract (api.md §21) and triggers garbage-collection pauses — the very frame hitches a game must avoid. The normative idiom instead allocates `IslandWorker` objects and their `Run` delegates once at *shape time* (when the circuit's structure changes), then each tick merely fills in fields (which island, which clock) and dispatches the pre-built delegates over client-owned worker state. An EE analogy: instead of soldering a fresh test harness onto the board for every measurement, you build the fixture once and only flip which channel it points at. "Closure-free" is the requirement; "cached delegate" is the mechanism.

**Standard concepts:** closure/lambda capture avoidance, delegate caching, object pooling — standard .NET zero-allocation practice; the phrase "closure-free schedule" is this project's shorthand.

**Where it appears:** `docs/api.md` §21 ("Parallel scheduling idiom (normative example)") and the worked example in §22.b.

### Cloth / rope system / mass-spring physics
- Vintage Story's built-in simulation of ropes and cloth (`ClothSystem`, in `vsessentialsmod/Systems/Cloth/`), which Manatee studies as the engine's precedent for rendering geometry strung between two fixed endpoints — but deliberately does not adopt wholesale for wires.

A *mass-spring* model represents a rope as a chain of point masses connected by springs and integrates their motion every frame — physically lively, but it costs CPU continuously, wobbles, and can "rip" (springs overstretch and the rope breaks apart). For hanging electrical wires none of that dynamism is wanted, so the plan is a *static catenary mesh*: the sagging-cable curve (the catenary — the shape a chain naturally hangs in, which an EE knows from transmission-line sag calculations) is computed once and drawn as fixed geometry in Manatee's own renderer. What we do borrow from the cloth code is its solved plumbing: the per-segment transform math, the two-endpoint pinning/activation model (`ClothSystem.CreateRope`, `ClothPoint.PinTo`), and its known constraint that spans must not cross map-region boundaries (`ClothManager.cs:583-585`), which wire persistence must also respect. In programming terms: reuse the rendering and lifecycle scaffolding, replace the live simulation with a precomputed lookup.

**Standard terms:** mass-spring (particle-spring) simulation; catenary curve.

**Where it appears:** `docs/vintage-story.md` "Wire Rendering" (catenary spans between posts).

**References:** for the catenary and conductor sag, any transmission-line engineering text; mass-spring cloth simulation follows Provot (1995), "Deformation Constraints in a Mass-Spring Model to Describe Rigid Cloth Behavior", Graphics Interface.

### Coalesced (rebuild)
- Merging many pending island rebuilds into a single one: no matter how many cable segments are removed in a tick, the affected island is rebuilt at most once, at solve time.

In the compaction layer, any segment *removal* invalidates its island's reduced netlist and requires a rebuild (there is no incremental split detection — design.md R11). Rather than rebuilding immediately per removal, removals merely mark the island dirty; the actual rebuild is deferred until the next solve. A deconstruction burst — a player tearing out fifty cable blocks in one tick — therefore costs one rebuild, not fifty. This is the same idea as coalescing writes in a cache or debouncing a noisy input: absorb a flurry of triggers, act once on the final state. For an EE, it is exactly switch debouncing — many contact bounces, one registered event — applied to a data-structure rebuild instead of a logic edge. Correctness is unaffected because only the state at solve time matters; intermediate topologies are never solved.

**Standard terms:** batching / debouncing / dirty-flag deferred recomputation; "coalescing" is standard OS/kernel vocabulary for the same idea (e.g. interrupt or write coalescing).

**Where it appears:** `docs/compaction.md` "Incremental Maintenance" ("Any removal ⇒ island rebuild, **coalesced**").

### Cold start
- Re-initializing a device's or island's dynamic state from default (zero) values when saved state cannot be matched at load, so a bad snapshot degrades the simulation instead of failing the whole game load.

When a save is restored, snapshot blobs are matched to state units by `StateKey`; anything no blob matches simply stays at its built-in cold defaults (e.g. a battery's charge integrator at zero, a capacitor discharged). `Restore` never throws on a miss — it reports, and the client decides. Detection is deliberately **aggregate**: no single `Restore` call can say what cold-started; only after all blobs are offered does `StateUnitCount − sum(Matched)` give the true count of cold-started units, which the client logs. "Cold-started" is thus a *derived* property, not a destructive operation — restore is additive and never clobbers already-matched state. For an EE, this is exactly powering a circuit up from rest: energy-storage elements begin discharged and the transient settles from there; annoying, but physical and safe, unlike a corrupted load.

This is the standard software sense of *cold start* (starting with empty state, as in cold-starting a cache or service), applied to simulation state restore.
- **Where it appears:** `docs/api.md` §14 (Restore semantics, StateKey), §22.a; `docs/stationeers.md` Persistence ("a failed restore degrades to cold start of that island, never a failed load"); `docs/integration-tutorial.md` §7 and sharp-edges item 10.

### CommitEdit / atomic commit / batch
- Structural changes are staged in an `Edit()` batch and applied all-or-nothing at commit; within one commit, all removals are applied before any additions.

`Edit()` opens a batch of topology changes (add/remove components, couplers, segments); `Dispose`/`Commit` — internally `Netlist.CommitEdit` — applies the whole batch atomically, so at every observable instant the netlist is a consistent circuit and a failed batch rolls back entirely. "Atomic" here is the database-transaction sense: no outside observer ever sees a half-applied edit. The **removals-before-additions** ordering (decision log #28) is what makes the breaker-move pattern legal: `RemoveCoupler` + `AddCoupler` with the *same* `ExternalKey` in one batch retires the old entry first, so the key resolves to the new handle afterward, and journal replayers see `Removed(K)` before `Added(K)` — never a transient duplicate key. For an EE: it is the difference between rewiring a live board one clip at a time and powering down, making all changes on the bench, and energizing once — the solver only ever sees the before and after circuits, never the mid-rewire mess.

Standard concepts: atomic commit / transaction (databases), batch mutation; the fixed intra-commit ordering is our specific contract.
- **Where it appears:** `docs/api.md` §6 (StructuralEdit), §17.1 (commit is atomic), decision log #28; `docs/integration-tutorial.md` sharp-edges item 17; `core/Manatee.Core/StructuralEdit.cs` and `Netlist.Internal.cs` (`CommitEdit`).
- **References:** Gray & Reuter, *Transaction Processing: Concepts and Techniques* (1993), for the atomic-commit/ACID vocabulary.

### compacting GC / background GC
- Two behaviors of the .NET runtime's automatic memory manager (garbage collector, "GC") that run at unpredictable moments and disturb our memory-allocation measurements.

  A garbage collector periodically reclaims memory the program no longer uses. A **compacting** collection additionally slides the surviving data together to eliminate gaps (think defragmenting a disk); **background** GC does part of this work on separate threads, concurrently with the program, and can fire long after the test that caused the pressure has finished. For an EE: it is housekeeping machinery inside the runtime that switches on by itself, like a thermostat-driven cooling fan injecting noise into a sensitive measurement — you cannot schedule it, only design the measurement to reject it. In Manatee this matters for the zero-allocation gates in the test suite: the per-thread allocation counter (`GC.GetAllocatedBytesForCurrentThread`) over-reports by a few hundred bytes when GC or JIT machinery retires the thread's partially-used allocation block mid-measurement. Concurrent compacting GCs from sibling tests are suppressed by serializing the tests (the `ZeroAlloc` collection), but process-wide background GC survives serialization — so the sound mechanism is min-over-N sub-runs: the perturbation is strictly additive, so one clean sub-run (min == 0) proves the zero-allocation property.

  These are standard .NET runtime terms ("background GC" is Microsoft's name for the concurrent generation-2 collection mode; compaction is the standard GC concept).
- **Where it appears:** `docs/testing-strategy.md`, Benchmarks section ("Flake isolation for the zero-alloc gate"); the `ZeroAllocCollection` and `TierBudgetGateTests` in the test suite.
- **References:** Jones, Hosking & Moss, *The Garbage Collection Handbook* (CRC Press) for GC compaction and concurrent collection; Microsoft's .NET documentation on "Background garbage collection".

### ComponentId
- The common supertype of all typed component handles, letting one `Remove` accept a resistor ID, capacitor ID, or any other component ID interchangeably.

Each component kind in the netlist API has its own ID type (`ResistorId`, `VSourceId`, `CapacitorId`, ...) so that passing the wrong kind to a mutator is a compile error — like keyed connectors that physically cannot plug into the wrong socket. But some operations genuinely apply to *any* component, removal being the canonical one, and forcing eight overloads would be noise. `ComponentId` is the shared parent type those operations accept: `Remove(ComponentId id)` in `docs/solver.md`. For an EE: think of the typed IDs as part numbers with a family prefix — the prefix stops you fitting a capacitor where a resistor goes, while the family lets generic tooling (removal, metadata) handle all of them. In the as-built API (`docs/api.md` §3) this role is realized as the `IComponentId` interface implemented by every typed ID struct, with generic methods constrained `where TId : struct, IComponentId` so no boxing (hidden heap allocation) occurs; `docs/solver.md` writes it as the simpler `ComponentId` supertype. See also **ComponentRef** for the runtime kind-tagged form.

**Where it appears:** `docs/solver.md` "The Netlist API" (`Remove(ComponentId id)`); as-built as `IComponentId` in `docs/api.md` §3 and the generic `Remove<TId>` / readback methods.

**References:** Gamma, Helm, Johnson & Vlissides, *Design Patterns* (program to an interface, not an implementation).

### compute/commit split (off-thread tier-3)
- The rule that expensive rebuild work may run on a background thread, but the results only become visible through a short hand-off performed on the simulation thread.

Tier-3 (topology-changing) operations involve heavy work — symbolic analysis of the matrix structure and numeric factorization — that can take long enough to justify offloading, e.g. a Stationeers load-time build or a runtime rebuild of one island while others keep ticking. The split says: the *compute* half (analysis, factorization) may run on a background task against staged, private inputs; the *commit* half — every mutation of netlist-global structures such as generation reissue, journal appends, `IslandTable`/key-map updates, and the `Building→Ready` status flip — happens **on the sim thread, in a topology phase**, never concurrent with any solve Step. Until adoption, the island runs on last-good values (`IsLive == false`). For the EE: it is like fabricating a replacement subassembly on the side bench while the rig keeps running, then swapping it in during a scheduled shutdown window — the running system never sees a half-installed part. This discipline is what keeps the lock-free shared-read contract of §21 true: readers during Solve never race a structural writer. The pattern is a form of double-buffered staging / read-copy-update: prepare off to the side, publish atomically from the single writer.

**Where it appears:** `docs/api.md` §21 ("Off-thread tier-3 (binding compute/commit split)") and decision log §24 #26; background-offload motivation in `docs/stationeers.md`.

**References:** the general pattern is read-copy-update / single-writer publication; see McKenney's RCU literature or Herlihy & Shavit, *The Art of Multiprocessor Programming*, for the underlying memory-publication discipline.

### ConductorGraph
- The as-built C# class that implements Manatee's reduction layer — the code that turns raw conductor geometry into a minimal netlist and keeps the two in sync as the game world changes.

It lives in `core/Manatee.Core/Reduction/` (`ConductorGraph.cs`, `ConductorGraph.Compaction.cs`, `ConductorGraph.Drift.cs`, plus `Keys.cs`) and implements: region building (grouping equipotential geometry into single nodes), series-chain collapse, probe interpolation (reading voltages "inside" collapsed runs), Pareto-minimal i²t limit envelopes for attributing limit events to specific segments, and the Diff/Resync backstop that continuously validates incremental maintenance against a from-scratch rebuild. Clients construct one against a `Netlist` (e.g. `new ConductorGraph(_net, GraphOptions.PrePartitioned)`) and feed cable runs through `BeginBulkBuild`/`AddSegment` rather than editing the netlist directly. For an EE: a class is a self-contained module bundling data and the operations on it — this one is the machine that extracts a schematic from a layout and keeps the extraction current as the layout is edited.

- **Where it appears:** `core/Manatee.Core/Reduction/ConductorGraph*.cs`; contract in api.md §19; design rationale in `docs/compaction.md`; usage walkthrough in `docs/integration-tutorial.md` §2–3.

### config-scalable / config option
- A behavior whose intensity or presence is tunable through the mod's configuration file rather than fixed in code, so players and server admins can trade realism, fidelity, and performance without a rebuild.

- A *config option* is a named setting the mod reads at startup (or on change); *config-scalable* means a behavior is designed so a config value can smoothly dial it up or down. Examples in our docs: the `mna-telemetry` broadcast rate (how often per-island probe readings are sent to game clients, ~0.5–1 s by default, scalable down for cheaper servers or up for snappier meters), and design.md R15's option to require the physical multimeter item before hover tooltips show volts/amps — realism purists opt in to harder fault-finding. For an EE: it is the front-panel knob or DIP switch of software — the circuit (code) is fixed, but its operating point is field-adjustable. The design intent is that difficulty and cost knobs live in configuration, never hard-coded.
- Standard programming vocabulary; aliases include *configuration setting*, *tunable*, *feature flag* (for on/off options).
- **Where it appears:** `docs/vintage-story.md` §4 (Tooltips/telemetry channel), `docs/design.md` R15 (multimeter-required option).

### connected component
- The graph-theory term for a maximal group of nodes that are all reachable from one another; in our solver, each connected component of the electrical network is one *island*.

- Treat the circuit as a graph: nodes are electrical junctions, edges are components that conduct between them. A connected component is the largest set you can grow from any node by following edges — add everything reachable, stop when nothing more connects. "Maximal" matters: a component is not just *a* connected blob but the *whole* blob; two components share no path between them. For an EE this is exactly galvanic connectivity: two nodes are in the same component when current could flow between them through some chain of parts, and separate components are electrically independent circuits that can be solved as separate, smaller matrices. solver.md states the identity directly: an island = one connected component = one matrix = one factorization = the unit of parallelism. Standard graph term; computed with union-find or graph traversal (BFS/DFS).
- **Where it appears:** `docs/solver.md` (Islands section), `docs/design.md` R5.
- **References:** Cormen, Leiserson, Rivest & Stein, *Introduction to Algorithms* (CLRS), chapters on elementary graph algorithms and disjoint-set data structures.

### connected-component management
- The bookkeeping that keeps track of which nodes belong to which island as the circuit is edited, without recomputing everything from scratch on every change.

- Design requirement R5 makes this a core solver feature with an asymmetric strategy: *merges are incremental, splits trigger a rebuild*. When a new component bridges two islands, a union-find structure (a classic disjoint-set data structure: cheap "are these connected?" queries and cheap "join these groups" updates) merges them in near-constant time. When a component is *removed*, the island might have split in two — but detecting that is expensive, so we do not try: the affected island is simply rebuilt from its nodes (cheap at game-circuit sizes; explicitly ruled in design.md R11). For an EE: this is the machinery that notices when cutting one wire turns one circuit into two independent circuits, each thereafter solved on its own — which is also why islands are the unit of parallelism and why per-island matrices stay small. In the API, coupling devices (transformers, breakers) decide per device type whether they merge islands galvanically or keep them as separate coupled matrices.
- Standard concept; the literature calls the incremental variant *dynamic connectivity* (our merge-only-incremental choice is the standard "incremental" special case).
- **Where it appears:** `docs/design.md` R5, `docs/solver.md` (Islands: union-find over the netlist, rebuild-on-removal).
- **References:** CLRS on disjoint-set forests (union by rank, path compression); Tarjan (1975), "Efficiency of a Good But Not Linear Set Union Algorithm", *JACM* 22(2).

### CsCheck
- The C# library Manatee uses for property-based testing: instead of checking one hand-written example, a test states a rule that must hold for *every* input, and the library generates thousands of randomized inputs trying to break it.

  In our suite, CsCheck generates random circuits — seeded R/V/I ladders and meshes guaranteed to be connected, random diode circuits swept through the knee, randomized constant-power load sets under falling supply — and asserts invariants over them: solve or fault legibly (never NaN, never hang), stay within oracle tolerance, settle without unbounded oscillation. The EE analogy: rather than testing a design at a few chosen operating points, it is like an automated sweep rig that exercises the design across the whole legal operating envelope and reports the smallest stimulus that provokes a failure ("shrinking"). Seeding makes the randomness reproducible, so a failure found overnight can be replayed exactly.
- This is a standard, real library (by Anthony Lloyd), a C# descendant of the QuickCheck family; the general technique is called **property-based testing** or **fuzzing**.
- **Where it appears:** `docs/testing-strategy.md` (Toolchain; Property and Fuzz Tests); the test projects in `Manatee.sln`.
- **References:** Claessen & Hughes (2000), "QuickCheck: A Lightweight Tool for Random Testing of Haskell Programs", ICFP — the origin of the property-based testing approach CsCheck implements.

### CSparse.NET
- A third-party C# sparse-matrix library that Manatee keeps only as a development-time cross-check; it is never shipped inside any mod.

  It is a .NET port of Tim Davis's CSparse, solving the same job as our own solver backend: LU-factoring the sparse MNA matrix. Benchmarks (2026-07-06) showed our in-house KLU-style sparse LU strictly dominates it — 2–4× faster, and CSparse.NET's public API forces a small heap allocation on every refactorization, which our zero-allocation hot path cannot tolerate. So it was demoted from candidate production backend to an *equivalence oracle*: in dev and CI it independently solves the same matrices so disagreements expose bugs in our solver, like a second, slower instrument used only to calibrate the primary one. Demotion also solves a licensing problem: CSparse.NET is LGPL, and keeping it dev/test-only means no LGPL DLL is distributed with the MIT-licensed mods.
- The library name is standard; "equivalence oracle" is our role for it — a reference implementation used as ground truth in tests.
- **Where it appears:** `docs/solver.md` (Numerics — backend decision), `docs/design.md` (Licensing), `CLAUDE.md`, `docs/api.md` §"one shipping assembly".
- **References:** Davis, "Direct Methods for Sparse Linear Systems" (SIAM, 2006) — the book the original CSparse accompanies.

### cursor / OpenCursorAt / Overflowed
- A cursor is an independent read position into the `TopologyJournal` (the fixed-size ring of topology-change events); `OpenCursorAt(seq)` opens one at a historical sequence number, and `Overflowed` reports that the cursor has been lapped and its reader must fully resync.

  The journal is a fixed-capacity ring buffer with a monotonically increasing sequence number, and each derived layer (the compaction/reduction layer, renderers, debug tooling) keeps its own cursor and catches up by replaying events — never by callbacks. `OpenCursor()` starts at the head; `OpenCursorAt(seq)` positions at a past point and is *the* way to replay an `EditReceipt` window after a commit (a head cursor opened after the commit would see nothing). For EEs: a cursor is like a playback head on a loop of tape that the writer keeps recording over — each listener has its own head and its own position. Because the tape is a loop, a reader that falls too far behind gets recorded over: that is being **lapped**, and `Overflowed(cursor)` returning true means the reader has provably missed events and MUST discard its derived state and rebuild from scratch (resync) — an explicit failure, never a silent one. The oversized-single-commit case is flagged up front via `EditReceipt.WindowLapped`. Game clients never babysit cursors; cursors serve the compaction layer.

  **Standard term:** ring-buffer / log consumer cursor (also "read offset", as in Kafka consumer offsets, or a database cursor); "lapped" is the standard single-producer ring-buffer overrun condition.
- **Where it appears:** `docs/api.md` §15 (`TopologyJournal`: `OpenCursor`, `OpenCursorAt`, `TryRead`, `TryReadRange`, `Overflowed`, `Head`), §24.25 (decision log: positionable cursors and up-front single-commit overflow), and the failure-mode table (overflow ⇒ reducer resyncs).

### DebugLevel.Asserts / StaleHandleException
A development-build mode in which contract violations crash loudly and immediately, instead of being quietly absorbed — and the specific crash you get when code uses a handle that the solver has already invalidated.

`DebugLevel` is a per-netlist option (`Off | Asserts | AllocationSentinel` in `NetlistOptions`). With `Asserts` on, using a stale handle (see *generational handle*) throws a `StaleHandleException` that names the exact problem: the handle's kind and slot, the expected vs. actual generation, and the journal event that invalidated it — e.g. "stale ResistorId slot 41 gen 3, live gen 4, invalidated by IslandRebuilt seq 8123". In a release (shipped-game) build the same mistake is instead a defined, counted no-op: reads return 0.0, writes do nothing, and `TickStats.StaleHandleReads` increments — a player's game never crashes on a stale read, but a nonzero counter tells the developer they have a re-pin bug. For an EE, the analogy is a lab instrument with a self-test switch: with self-test on, probing a disconnected test point trips an alarm that tells you *which* point and *when* it was disconnected; with self-test off, the meter just reads zero and increments a fault counter. This is the standard fail-fast assertion pattern: catch misuse at the moment of the mistake in development, degrade gracefully in production.

**Where it appears:** `docs/api.md` §20 (error channels table, `NetlistOptions.Debug`), §16 (handle-survival table); `docs/integration-tutorial.md` §6 (the survival-rules digest).

**References:** the fail-fast/assertion discipline is standard software practice — see e.g. Hunt & Thomas, *The Pragmatic Programmer* ("Assertive Programming"), and Meyer, *Object-Oriented Software Construction* (Design by Contract).

### decompilation / ILSpy output / decompile caveat
Reconstructed, human-readable source code recovered from the compiled Stationeers game binary, used to verify facts (especially threading) about how the game actually works — with explicit warnings wherever the reconstruction itself might be wrong.

Stationeers ships as compiled .NET bytecode (`Assembly-CSharp.dll`) with no public source; the ILSpy decompiler translates that bytecode back into approximate C#. For an EE, this is like reverse-engineering a potted module: you can recover the schematic well enough to design an interface to it, but some component values are inferred, so you double-check anything critical on the bench. Design claims graduated into `docs/` from this source carry file:line references *to the ILSpy output*, and a **decompile caveat** flags places where the reconstructed code may be an artifact of the decompiler rather than the real program — e.g. the game's tick-pacing loop appears to test `Elapsed.Milliseconds` (the 0–999 component) instead of `TotalMilliseconds`, which could be a real upstream bug or an ILSpy 9.1 artifact; the rule is to check the raw bytecode (`ilspycmd -il`) before relying on behavior that hinges on such a detail. The decompiled tree (`third_party/stationeers-decomp/`) is generated, gitignored, and never committed or copied into Manatee code — it is read-only evidence.

**Where it appears:** `docs/stationeers.md` (Threading, "verified against decompiled game source"); `third_party/CLAUDE.md` (decompile notes and caveats); regenerated by `third_party/import.sh`.

### delegate
- A C# language feature: a variable that holds a reference to a function, so the function to run can be chosen, swapped, or hooked at runtime.

Where ordinary code calls a function by name, code that calls through a delegate calls *whatever function was plugged into that slot* — like a socket on a test bench where you can wire in different instruments without rebuilding the bench. Vintage Story uses this as its sanctioned extension point for food spoilage: an inventory exposes a perish-rate delegate (`Inventory.OnAcquireTransitionSpeed`), and our Freezer's container block-entity hooks it, returning a spoilage rate scaled by the freezer's electrical duty. That lets the mod alter spoilage without patching the engine's own spoilage code. Delegates are C#'s type-safe version of what other languages call function pointers or callbacks.

The term is standard C# vocabulary; common aliases in other languages are *function pointer*, *callback*, and *first-class function*.

**Where it appears:** `docs/vintage-story.md` sec. 3 (Freezer: hooking the vanilla perish-rate delegate at `InWorldContainer.cs:84,160`).

**References:** ECMA-334 (C# Language Specification), §Delegates; Gamma, Helm, Johnson & Vlissides, *Design Patterns* (1994) — the Observer/callback patterns delegates commonly implement.

### determinism / determinism by contract
- The guarantee that manatee-core produces bit-identical results from identical inputs, promised in `solver.md` and *relied on* by clients — most visibly so that the tablet's lesson goldens behave the same in-game as in CI.

A deterministic system is one whose output is a pure function of its inputs: run it twice with the same netlist and stimulus and you get the same numbers, down to the last bit. "By contract" means this is a documented API promise, not an observed accident — like a component datasheet parameter you may design against, versus a typical value that might drift between production batches. The Vintage Story tablet leans on it hard: tablet islands run client-side only, and lesson goldens (recorded expected waveforms) checked into CI must match what a player's machine computes; that only works if the solver is deterministic everywhere it runs. Breaking determinism is therefore a breaking API change, not a mere performance regression. See *deterministic (numerics)* for what the solver does internally to keep the promise.

Determinism is standard vocabulary; "by contract" echoes design-by-contract — the property is a specified obligation of the interface.

**Where it appears:** `docs/vintage-story.md` (The Tablet Host: "Core determinism holds by contract (solver.md)"); the promise itself in `docs/solver.md` (Numerics, Threading and Allocation Contract); `docs/testing-strategy.md` (lesson corpus as CI goldens).

**References:** Meyer, *Object-Oriented Software Construction* (2nd ed., 1997) — design by contract.

### deterministic (numerics)
- The concrete rules inside manatee-core that make its arithmetic reproducible: no wall-clock reads, no ambient random-number generation, and identical inputs give identical outputs across runs *and* across thread interleavings.

Floating-point math is exact but order-sensitive — sum the same numbers in a different order and the last bits can differ — so reproducibility has to be engineered, not assumed. Manatee's rules: the solver never consults the system clock or any implicit randomness (any needed randomness would come in as a seeded input); and threading is arranged so scheduling can't change results — each island's state is confined to that island, islands joined by boundary couplings substep in lockstep as one scheduling unit, and only fully independent islands run on separate workers. Consequently the OS scheduler can shuffle which island runs when without changing a single output bit, much as a properly synchronized clocked circuit gives the same answer regardless of which gate happens to switch first inside a clock period. The regression corpus (goldens compared bit-for-bit in CI) depends on this property and would flag any violation.

The term is standard; the noteworthy strength of our variant is determinism *under arbitrary thread interleaving*, which many "deterministic" simulators only offer single-threaded.

**Where it appears:** `docs/solver.md` (Numerics: "All numerics deterministic"; Threading and Allocation Contract: "Determinism per island regardless of thread interleaving"); relied on by `docs/vintage-story.md` (Tablet Host) and `docs/testing-strategy.md`.

**References:** Goldberg (1991), "What Every Computer Scientist Should Know About Floating-Point Arithmetic", *ACM Computing Surveys* 23(1) — why floating-point reproducibility must be designed for.

### Deterministic / seeded RNG
- The rule that identical inputs always produce identical outputs — and that any test which uses randomness records the seed that generated it, so the exact run can be replayed.

manatee-core is deterministic by contract: given the same netlist, same edits, and same tick sequence, it produces bit-identical results every time — no wall-clock time, no thread-scheduling luck, no hidden randomness influences the answer (with one documented carve-out: cross-island reads under a parallel schedule are scheduling-dependent unless taken in the readback phase). The test suite leans on this: randomized tests (random circuit ladders, random edit sequences) draw from a pseudo-random number generator initialized with a fixed *seed* — a starting number that fully determines the whole "random" sequence, the way one initial condition determines a whole simulation trajectory. So a failing test reproduces from its inputs alone: rerun with the same seed and you get the same circuit, the same failure. For an EE, the analogy is a signal generator with a repeatable arbitrary-waveform program rather than a true noise source — it looks random, but you can dial up the exact same waveform tomorrow.

Both halves are standard practice; common aliases are "reproducible builds/tests" and "PRNG seeding".

**Where it appears:** `docs/testing-strategy.md` (Principles: "Deterministic and reproducible"; seeded random circuit generation and edit sequences); the determinism contract itself is stated in `docs/solver.md` and echoed in `docs/api.md` §21 (`DeviceTickContext.Previous` determinism note).

**References:** Knuth, *The Art of Computer Programming*, Vol. 2: *Seminumerical Algorithms* (pseudo-random number generation); the property-based-testing tradition of Claessen & Hughes, "QuickCheck" (ICFP 2000), which popularized automatic counterexample shrinking — a related-but-distinct reproduction discipline from seed-replay.

### devshell / flake.nix / flake.lock
- The project's pinned development environment: a Nix "flake" that gives every developer machine and the CI server the exact same compiler (dotnet-sdk 8) and the exact same test-oracle binary (ngspice).

`flake.nix` is a recipe file declaring which tools the project needs; `flake.lock` records the exact versions resolved, so builds are reproducible byte-for-byte across machines and time; the *devshell* is the shell environment you get by running `nix develop`, with those pinned tools on the path. CI runs `nix develop --command ...`, so "works on my machine" and "works in CI" are the same statement. For an EE: this is like specifying a test bench down to the instrument firmware revision — every measurement (here: an ngspice oracle comparison) is taken with the identical instrument everywhere, so a tolerance failure is a real regression, not instrument drift. The pinning matters specifically because oracle tests hard-fail when ngspice is missing, by design.

**Standard terms:** these are standard Nix ecosystem names (flake, lock file, dev shell); the lock file plays the same role as `package-lock.json` or `Cargo.lock` in other ecosystems.
- **Where it appears:** `docs/testing-strategy.md` (Toolchain); repo root `flake.nix` / `flake.lock`; project CLAUDE.md practical notes.

### dirty / dirties (the envelope) / dirty-region tracking
- To "dirty" something is to mark cached derived data as stale so it gets recomputed before its next use; in the compaction layer this appears two ways — an environment change *dirties the limit envelope* (a metadata-only recompute that never touches the solver matrix), and *dirty-region tracking* records *which spatial areas* of the world changed so only those are recompacted.
- **Envelope:** The compaction layer collapses a chain of cable segments into one equivalent resistor and caches an *envelope* alongside it: the per-limit-type worst-case thresholds (ampacity, i²t, melting) over the constituent segments, adjusted for ambient temperature. When the ambient changes (Stationeers spans −150 °C to +800 °C), those thresholds must be re-derived — but the electrical answer (the resistance stamped into the matrix) is unchanged. So the change only sets a "stale" marker on the envelope; the cheap recompute happens via `SetAmbient` → `Meta.SetLimits`, and the expensive matrix machinery is never invoked. For the EE reader: this is the software equivalent of knowing a temperature swing shifts your fuse's trip curve but not your network equations — you re-read the derating table, you don't re-solve the circuit.
- **Dirty-region tracking:** Incremental maintenance records not just *that* something changed but *which* region, at the client's natural chunk granularity — 32×32×32-block chunks in Vintage Story, one `CableNetwork` in Stationeers (implemented in `ConductorGraph`). Only touched regions are marked dirty; recompaction then diffs shadow geometry against the realized netlist inside the dirty area only. A pure addition that grows one region takes a fast path; anything that might bridge multiple regions, or any removal, escalates to rebuilding the affected island — with removals coalesced so a demolition burst costs one rebuild per tick, not one per segment. The from-scratch resync backstop sits just below it. For the EE reader: like re-checking only the section of a schematic page you just edited, at the resolution of pages, with anything ambiguous (a wire crossing page boundaries, an erasure) triggering a full re-check of that circuit.
- **Standard term:** dirty flag / cache invalidation (a classic lazy-recomputation idiom); the "dirty region"/"dirty rectangle" variant comes from 2D graphics, where only changed screen rectangles are redrawn.
- **Where it appears:** `docs/compaction.md` Responsibilities #4 ("an environment change dirties the envelope") and Incremental Maintenance (dirty-region tracking, mark-dirty-and-coalesce, from-scratch resync backstop, implemented in `ConductorGraph`); `docs/api.md` §12 area (`SetAmbient`).
- **References:** the Dirty Flag pattern is standard game-programming lore, e.g. Nystrom, *Game Programming Patterns* (2014), "Dirty Flag" chapter.

### dirty / Dirty (island status) / Dirty island / change tracking
- `Dirty` is the solver-core side of the dirty-flag idiom: bookkeeping that records which parts of a circuit changed since the last solve so only those get recomputed. It lives at two granularities — a named island-lifecycle *state* (`IslandStatus.Dirty`) and finer per-mutation *change tracking* tagged by change-cost tier.
- **Island status:** `Dirty` is one value of the `IslandStatus` enum (`Empty, Building, Ready, Dirty, Faulted`) and a node in an explicit state machine (api.md §11): any `Drive`/`Adjust`/`Reconfigure`/`Edit` touching a `Ready` island flips it `Ready → Dirty`; `Dirty → Ready` when the next `Solve` succeeds; `Dirty → Faulted` when the solve fails (singular matrix after gmin, or the Newton retry ladder is exhausted); and `Faulted → Dirty` on any tier-2/3 change — an automatic retry path with no manual reset verb. An `Edit` commit atomically moves the affected islands to `Dirty` (or `Building` for background loads). Crucially, being `Dirty` does not blank the outputs: reads return the last *published* solution ("last-good", `IsLive` false) until the re-solve lands — like a spreadsheet showing its previous results while marked for recalculation.
- **Scheduling face:** an island is one electrically independent subcircuit; the integration tutorial's tick loop runs `CollectDirty` to get one handle per scheduling unit, then a single `Solve` covers all of them — so a burst of N changes in one tick still costs one solve pass, not N. Ignoring `IsLive` while an island is Building/Dirty/Faulted (and thus reading stale last-good values) is a listed integration sharp edge.
- **Change tracking:** underneath the status, every netlist mutation is tagged with a change-cost tier and the netlist remembers what kind of change hit which island. At the next `Solve`, an untouched island reuses everything cached; a tier-1 change reuses the cached LU factorization and only re-solves the right-hand side; tier-2 refactorizes numbers but keeps the matrix pattern; only tier-3 rebuilds structure. Stamps are versioned so an island whose (pattern, values, dt) is unchanged skips straight to the cached factorization. For the EE reader: `Dirty` is bookkeeping, not physics — it is how the software knows which subcircuits changed since the last operating-point/transient step, so only those get recomputed, like a cache with fine-grained invalidation.
- **Standard term:** dirty flag / invalidation, here promoted from a boolean to a named state in a lifecycle state machine and to per-tier change tracking; common aliases "dirty bit", "cache invalidation tracking".
- **Where it appears:** `docs/api.md` §11 (the `IslandStatus` state machine and `CollectDirty`), §6 (`Commit` → islands→Dirty), §17 (read semantics while Dirty: last-published values, `IsLive` false); `docs/solver.md` — Layering (the `netlist` layer owns change tracking) and Change-Cost Tiers (every netlist mutator is doc-tagged with its tier); `docs/integration-tutorial.md` §5–6 (tick loop: "one Solve for every dirty island") and §8 (auto-retry on Dirty, `IsLive` pitfalls).
- **References:** Gamma, Helm, Johnson & Vlissides, *Design Patterns* (1994) — Observer for invalidation/notification; Nystrom, *Game Programming Patterns* (2014), "Dirty Flag".

### dirty / dirty flag / MarkDirty
- A dirty flag is a marker meaning "this object's state changed and must be re-synchronized or recomputed"; in the Vintage Story client the concrete API is the engine's `MarkDirty()` on a block or block entity, which triggers a network re-send to clients and (with `MarkDirty(true)`) a mesh retesselation.
- The idiom: instead of continuously pushing every value everywhere, you cheaply set a flag when something changes and let one downstream pass do the expensive work (send the packet, rebuild the mesh) — the EE analogy is an interrupt or status flag serviced later, rather than polling every register constantly. Two usage rules our docs record from engine source. First, the cost model: `MarkDirty` repackets the whole chunk, so calling it every tick to keep live volt/ampere tooltips fresh would flood the network ("chunk repacket spam"); the plan is instead a throttled custom channel (`mna-telemetry`, ~0.5–1 s broadcasts, following the mechanical mod's ~800 ms precedent), reserving `MarkDirty` for genuine state changes. Second, `MarkDirty(true)` is the correct way to trigger retesselation (mesh rebuild) when a cable block's visual state actually changes (e.g. it burns out). For the EE reader: a dirty flag is like a "needs calibration" tag on an instrument — setting the tag is free; the recalibration (or here, the network round-trip) is the expensive part you batch and rate-limit.
- **Standard term:** dirty flag / invalidation / change notification (standard CS pattern); `MarkDirty` is the Vintage Story engine's name for it.
- **Where it appears:** `docs/vintage-story.md` §4, Tooltips and Instruments (the "do NOT MarkDirty per tick" gotcha) and Wire Rendering (`MarkDirty(true)` for retesselation).

### Dirty-driven / dirty path
Describes code that runs only when a "dirty" marker says something changed — as opposed to running unconditionally every tick — and, by extension, the code path that maintains and consumes those markers.

In the Stationeers integration, Re-Volt's `Initialize_New` phase is dirty-driven: it fires when a cable network's topology is flagged as changed rather than every power tick, and Manatee maps it to a tier-3 island rebuild/patch. The important project caveat (stationeers.md's revision note): the game's dirty path tracks *device lists*, not cable cuts — `RebuildNetwork` is a whole-network flood, so detecting which cables actually changed needs a separate cable-set diff. For the EE reader: think of a protection relay that only trips its reconfiguration routine when a status contact changes state, versus continuously re-surveying the whole network; "dirty-driven" is the first style, and the "dirty path" is the wiring that carries that status signal. The caveat is that the game's status signal reports the wrong quantity for our purposes.

Standard idiom; aliases include "invalidation-driven", "event-driven recomputation", "lazy recomputation".

**Where it appears:** `docs/stationeers.md` — Integration Seams (`Initialize_New` mapping) and the revision-pending note at the top; contrasts with the always-runs tier-1 work in `docs/solver.md` Change-Cost Tiers.

### Dispatch
Choosing which piece of code handles a given input, based on a key — here, which element constructor runs for each line of a Falstad circuit file.

In the Falstad/circuitjs1 text format, the reader looks at the first character of the first token on each line: a single letter selects one element type directly (e.g. `r` for resistor), while a leading digit means the whole token is a decimal "dump type" number (e.g. `172`, `403`) looked up in a numeric table. Several non-element line types (`o` scope config, `$` options header, `.` subcircuit model, ...) are handled before element dispatch. For the EE reader: dispatch is the software equivalent of a selector switch or a part-number lookup — the leading key routes the rest of the line to the one handler that knows how to build that component; the code never runs a long chain of "is it this? is it that?" over every possibility.

Standard term; this specific style is often called "dispatch on a tag" or a "dispatch table" (contrast with a language's built-in dynamic/virtual dispatch, which is the same idea keyed on an object's type).

**Where it appears:** `docs/falstad-format.md` §1 (File model and tokenization), documenting `CircuitLoader.readCircuit` in the circuitjs1 source (`CircuitLoader.java:145`, `172-173`); it constrains what Manatee's importer must accept.

**References:** Gamma et al., *Design Patterns* (1994) — Factory Method / Strategy cover the "key selects handler" structure; the circuitjs1 source tree (github.com/pfalstad/circuitjs1) is the authoritative format reference.

### Display list
A flat, ordered sequence of drawing commands ("stroke this path", "fill this shape", "draw these glyphs") describing what a schematic view should look like, produced as plain data before any pixels exist.

In the harness, the view layer renders the circuit document plus interaction state into a display list expressed in schematic coordinates. Because it is just data, it can be snapshotted and diffed in tests (the project's *primary* visual golden — stable text, reviewable diffs, no font or GPU nondeterminism) and rasterized by any backend: SkiaSharp on desktop and in headless pixel tests, the Vintage Story GUI in-game. One contract, multiple rasterizers. For the EE reader: it is the drawing's *parts-and-operations list* rather than the drawing itself — like a CNC program or plotter tape you can inspect, compare line-by-line against a known-good copy, and feed to any compatible machine; disagreements are caught by comparing the tapes, not by comparing finished workpieces under a microscope.

Standard term (from classic computer graphics); aliases include "retained draw command buffer", "command list", or "scene description". Our usage matches the classic immediate-list sense — a flat replayable sequence, not a hierarchical scene graph.

**Where it appears:** `docs/harness.md` — Layering item 3 (view layer), Testing Model (display-list snapshots via Verify), Rendering Backends; `docs/testing-strategy.md` — Toolchain (snapshot goldens).

**References:** Foley, van Dam, Feiner & Hughes, *Computer Graphics: Principles and Practice* — display lists/display files in retained-mode graphics; the term dates to early vector-display systems (e.g. Sutherland's Sketchpad lineage) and survives in OpenGL's `glNewList` API.

### Display-list snapshots
- The tablet/harness's primary visual regression test: the display list (the flat sequence of draw commands the view layer emits) is serialized to stable text and diffed against a stored "golden" copy.

Because the display list is plain data — "stroke this path, fill that rectangle, place this glyph run at these schematic coordinates" — it can be written out as text and compared byte-for-byte, with no fonts, GPU, or rasterizer involved. A rendering change shows up as a readable text diff a reviewer can approve or reject, much like checking a schematic netlist against a known-good copy instead of comparing two printed plots by eye. This dodges the classic failure mode of pixel-comparison tests, where a trivial font-hinting or driver change invalidates hundreds of stored images; Manatee keeps only a deliberately small secondary suite of true pixel snapshots for rasterizer-level bugs. Most rendering regressions are caught at the display-list level.
- **Project-coined term**, closest standard concept: snapshot testing / golden-master testing (applied to a display list rather than pixels).
- **Where it appears:** `docs/harness.md` (Testing Model, View layer).
- **References:** Feathers, "Working Effectively with Legacy Code" (Prentice Hall, 2004) discusses characterization tests, the general idea behind golden-master testing.

### Document model
- Layer 1 of the schematic harness: the data structure holding the schematic itself — components, wires, junctions, and probe placements — plus the Falstad importer and the binding to manatee-core.

In UI architecture, the "document" is the thing being edited, kept strictly separate from how it is edited (the interaction state machine, layer 2) and how it is drawn (the view layer, layer 3). For an EE, the document model is the master schematic drawing: it records what components exist and how they connect, and everything else — cursor state, selection highlights, rendered pixels — is derived from or layered over it. It also owns the two translations at its boundary: importing Falstad/circuitjs1 text files into schematics, and extracting a netlist for manatee-core plus reading solutions back for meters and scope traces. The layer is pure (no UI-framework imports), which is what makes the tablet testable without a display.
- **Standard term:** document/model layer, as in Model–View–Controller and related patterns.
- **Where it appears:** `docs/harness.md` (Layering, item 1); lives in `Manatee.Schematic`.
- **References:** Gamma, Helm, Johnson & Vlissides, "Design Patterns" (Addison-Wesley, 1994) — the Observer/MVC discussion of separating a document from its views; Fowler, "Patterns of Enterprise Application Architecture" (Addison-Wesley, 2002) on presentation/model separation.

### document mutations
- The changes to the document model (add a component, move a wire, delete a selection, ...) that the interaction state machine emits as its primary output.

Rather than letting input handlers edit the schematic directly, layer 2 consumes abstract input events (pointer down/move/up, wheel, key) and produces two things: document mutations and transient view state (hover, drag ghosts). For an EE, think of the interaction layer as an operator at a drafting table and the mutations as the actual pencil strokes committed to the master drawing — everything else (where the hand hovers, a part being dragged) is scratch work that never marks the page. Making mutations an explicit output keeps the schematic's edit history well-defined (each mutation is an undoable step) and makes the whole editing stack testable headlessly: a test feeds synthetic events in and asserts on the mutations and resulting document, no screen required.
- **Standard term:** this is ordinary usage of "mutation" (a state-changing operation); closely related patterns are Command objects and edit operations in undo systems.
- **Where it appears:** `docs/harness.md` (Layering, item 2: Interaction state machine; Testing Model, interaction tests).
- **References:** Gamma, Helm, Johnson & Vlissides, "Design Patterns" (Addison-Wesley, 1994) — the Command pattern, the classic way edit operations are reified for undo/redo.

### Double-buffering / back buffer / release-fenced flip
- Keeping two copies of each island's solution vector — one being written, one visible to readers — and atomically swapping which is which when a solve finishes.

In manatee-core every island owns two solution buffers. During `Step` the solver writes into the hidden one (the **back buffer**); at the end of the Step a single index write, executed with a *release fence*, makes it the visible one (the **flip**). The release fence is a CPU-level ordering guarantee: all the numbers written into the buffer are guaranteed to be in memory *before* the index that points readers at them changes — so a reader that sees the new index sees a complete, consistent vector, with no locks. An EE analogy: it is a sample-and-hold ahead of an ADC — the measurement circuit works on live signals, but the output only ever presents fully settled values, updated in one clean switch event. The sharp edge documented in the API: after a flip, the previously visible buffer is recycled as the *next* back buffer, so a `RawVector` span held across another (foreign) island's Step can watch that memory being overwritten and observe a **mixed-generation vector** — part old solve, part new. The rule is therefore to read `RawVector` only for an island that is not concurrently mid-Step. The docs also note the flip is per-island, which is why cross-island reads through `Previous` are scheduling-dependent: preserving every island's last-tick data globally would require triple-buffering, which we deliberately do not pay for.

Standard terms: **double buffering** (universal in graphics and real-time programming) and **release/acquire memory ordering** (the C#/C++ memory model).

**Where it appears:** `docs/api.md` §10 (`Solution.RawVector` validity), §21 (Solution publication, threading model).

**References:** For memory-ordering semantics, ECMA-335 CLI memory model and Herlihy & Shavit, *The Art of Multiprocessor Programming*; double buffering is described in any real-time graphics text (e.g. Akenine-Möller et al., *Real-Time Rendering*).

### Drag-in-progress
- The transient state the tablet UI keeps between "pointer pressed" and "pointer released" while the user is dragging something — a component being placed, a wire being routed, a selection box being stretched.

In the harness's layered design, all editing state lives in layer 2, the **interaction state machine**, and drag-in-progress is one of its stateful parts alongside active tool, selection, hover, and snapping. A drag is genuinely a state-machine excursion: pointer-down enters the dragging state, each pointer-move updates provisional coordinates, and pointer-up either commits a document mutation (e.g. "resistor now exists at (140, 60), plus an undo entry") or cancels. Nothing is written to the schematic document until release; until then the drag exists only as transient view state that the renderer draws as a ghost/preview. An EE analogy: it is the difference between wiggling a trimmer while watching the meter and actually soldering the part in — the circuit (document) is only changed at the commit. Because the state machine consumes abstract input events rather than toolkit widgets, drags can be tested headlessly by feeding synthetic down/move/up scripts.

This is standard UI-programming vocabulary, not project-coined; related standard terms are "drag state" and "gesture recognition".

**Where it appears:** `docs/harness.md`, Layering item 2 (Interaction state machine) and the Testing Model's interaction-test scripts.

### Drain (into caller span)
- The API's uniform *pull* pattern for variable-length event data: the caller hands the core a fixed-size buffer (a `Span`), the core copies pending items into it and empties its internal queue as it goes.

Manatee-core never calls back into game code and never returns internally-owned variable-length arrays; instead the client drains: `IslandTable.DrainChanges` (island merges/splits/rebuilds), `Solution.DrainLimitEvents` (fuse-blow-style limit crossings), `RestoreResult.DrainOrphans` (save-blob entries that matched nothing on load), and fault detail via `DescribeFault`. Each call returns how many items it wrote; a too-small span drains *partially*, so the contract is to call again until it returns 0. Internally the events sit in fixed-capacity rings, and overflow is counted rather than hidden — `DrainChanges` reports `lost == true` (escalating the client to a full re-pin) and `DrainLimitEvents` has an `out long dropped` overload — so a client that skipped a drain learns events were lost instead of failing silently. The EE analogy: the core is a data logger with a finite FIFO and an overrun flag; you clock records out at your own pace, and if you poll too slowly the overrun flag tells you the record is incomplete rather than quietly gapping. This design keeps the hot path allocation-free (the caller owns and reuses the buffer) and makes the core's threading simple (no reentrant callbacks). Draining changes each tick is a stated correctness obligation of game clients, not an optional telemetry nicety.

Standard concepts: this is the **caller-allocated buffer / pull-based polling** idiom (cf. POSIX `read` into a caller buffer, or .NET's `Span<T>`-filling `TryCopyTo`/`Read` patterns), combined with a bounded ring buffer with overrun accounting.

**Where it appears:** `docs/api.md` §6 (journal-drain obligation), §11 (`DrainChanges`), §12 (`DrainLimitEvents`, limit-event rings), §14 (`RestoreResult.DrainOrphans`); the canonical tick loop in §22.a shows the drain phases in order.

### Draw call
- One submission of geometry from the CPU to the GPU; each submission has a fixed overhead, so rendering many objects efficiently means batching them into as few draw calls as possible.

A GPU renders whatever vertex data a draw call hands it, but *issuing* the call — binding shaders, textures, and buffers, then crossing the driver boundary — costs roughly the same whether the call draws one cable segment or ten thousand. The EE analogy: it is per-transaction bus overhead — address/setup cycles dominate if you transfer one word at a time, so you use block transfers. The fix is **instanced rendering**: upload one copy of a shape's mesh plus an array of per-instance transforms (position/rotation/scale), and draw all instances in a single call. In the Vintage Story mod this is the stated efficiency goal for wire rendering: the engine's own `MechNetworkRenderer` (`MechanicalPower/Renderer/MechNetworkRenderer.cs:14`) does one draw call per shape with per-instance transforms, and Manatee's many identical cable segments follow that precedent.

This is standard graphics-programming vocabulary; "instancing" / "instanced rendering" is the standard name for the batching technique.

**Where it appears:** `docs/vintage-story.md`, Wire Rendering section (voxel cable meshes; catenary spans also use instanced rendering via the cloth-system precedent).

**References:** Akenine-Möller, Haines & Hoffman, *Real-Time Rendering* (batching and instancing); the OpenGL specification's `glDrawArraysInstanced` family documents the mechanism.

### Draw commands
- The individual primitive entries that make up the tablet's display list: "stroke this path", "fill this shape", "place this run of glyphs (text)", each with coordinates and style.

In the harness's layered design, the view layer (layer 3) does not paint pixels; it renders the schematic document plus interaction state into a **display list**, which is a flat sequence of draw commands in schematic coordinates. Each command is plain data — a small record saying what to draw, where, and how — with no reference to any windowing toolkit or GPU. That data-ness is the point: the same command sequence can be snapshotted and diffed in tests (the primary visual golden), or handed to any rasterizer backend — SkiaSharp on desktop, the Vintage Story GUI in-game. The EE analogy: draw commands are to rendered pixels what a netlist is to a fabricated board — a complete, machine-readable description of the artifact that you can inspect, compare, and hand to different fabrication processes, checked long before anything physical exists. Because commands are in schematic coordinates, zoom and pan are the backend's problem, not the view layer's.

Standard terms: entries of a **display list** (classic graphics term, dating to vector-display hardware); modern equivalents are "command buffer" or "retained-mode draw list" (e.g. Dear ImGui's `ImDrawList`).

**Where it appears:** `docs/harness.md`, Layering item 3 (View layer) and the display-list snapshot testing model; see also the glossary's *display list* entry.

**References:** Foley, van Dam, Feiner & Hughes, *Computer Graphics: Principles and Practice* (display lists / display files).

### drift detection
A testing-and-runtime technique: compute the same state two ways — incrementally (patch by patch) and from scratch — and verify the two agree; any disagreement is "drift" and is a bug in the incremental path.

In Manatee this exists in two forms that share one comparison. As a CI test ("incremental equivalence" in testing-strategy.md), randomized seeded edit sequences are applied incrementally, then the final state is rebuilt from scratch; both must yield identical islands and solutions, stated as byte-equality of the `SaveNormalized` serialization. As a shipped runtime backstop (R11), the same comparison runs in the background in-game, since real player mutation streams will exercise edge cases tests missed. The general pattern for an EE reader: whenever a system keeps a running tally instead of recomputing from source data (for speed), you must periodically recompute from source and compare, because small bookkeeping errors otherwise accumulate invisibly — the same reason a lab periodically recalibrates an instrument against a reference standard rather than trusting its running calibration forever.

**Standard term:** yes — drift detection / reconciliation; the test form is a differential (equivalence) test between two implementations.

**Where it appears:** `docs/design.md` (R11, Testing and Validation); `docs/testing-strategy.md` (Equivalence Tests, "Incremental equivalence"); `docs/api.md` §14 law 3.

**References:** the differential-testing idea traces to W. M. McKeeman, "Differential Testing for Software", Digital Technical Journal 10(1), 1998.

### dump / undump
- circuitjs1's (Falstad's circuit simulator's) own names for its save and load routines: `dump()` writes a circuit element out as a line of text, and `undump` (plus the element constructors) reads it back.

In programming terms these are **serialize / deserialize**: converting an in-memory object (a resistor with its position, flags, and resistance) to a flat text representation and back. The Falstad text format has no written specification — the `dump()` methods on each element class and the corresponding readers *are* the only spec that exists, so `docs/falstad-format.md` was produced by reading them (e.g. `CircuitElm.dump()` defines the general element line; `Scope.undump` reads the heavily-versioned scope-configuration lines). For an EE: it is like a proprietary instrument file format documented only by the firmware that writes it — to interoperate, you reverse-engineer the firmware. Notably, upstream's readers are silently lossy (unrecognized lines are skipped with only a console log), a posture our importer explicitly must not copy. In circuitjs1 4.x the text *writers* are being deleted in favor of XML while the text *readers* remain, so "dump" increasingly refers to legacy behavior.

**Standard term:** serialize / deserialize (also marshal/unmarshal). "Dump" is circuitjs1's vocabulary, which we adopt when discussing its source.

**Where it appears:** `docs/falstad-format.md` §1 (dispatch and error posture), §3 (`CircuitElm.dump()` general form), §4 (`Scope.undump`, per-element dump bodies with file:line citations).

### equivalence oracle / cross-agreement
- A correctness check where two independently written solver implementations are run on the same problem in CI and must produce the same answer.

Manatee's production backend is an in-house sparse LU solver — fast but complex. To catch bugs in it without anyone hand-verifying the math, CI also solves the same systems with a naive dense LU (the "test referee") and, at dev time, with the third-party CSparse.NET library, and asserts agreement. Because the implementations share no code and use different algorithms, a bug would have to appear identically in independent codebases to slip through — the same reasoning as N-version redundancy in fault-tolerant systems. For the EE: it is like checking a measurement with two instruments from different manufacturers; agreement is strong evidence neither is broken. This is part of the project's core stance that "the math is treated as untrusted input": correctness rests on oracles, invariants, and equivalences, never on trusting a derivation.

**Standard term:** differential testing (McKeeman); "oracle" is standard test-oracle vocabulary. "Cross-agreement" is the project's informal phrasing for the same idea.

**Where it appears:** `docs/solver.md` Numerics (dense LU as test referee, "cross-agreement in CI"; CSparse.NET "demoted to a dev-time equivalence oracle"); `docs/testing-strategy.md` Principles.

**References:** W. M. McKeeman, "Differential Testing for Software", *Digital Technical Journal* 10(1), 1998; E. J. Weyuker, "On Testing Non-Testable Programs", *The Computer Journal* 25(4), 1982 (the test-oracle problem).

### equivalence oracle / equivalence test (CSparse.NET role)
- The specific use of the third-party CSparse.NET library as a dev/test-only cross-check against Manatee's in-house sparse solver — never shipped to players.

After the 2026-07-06 benchmark revision, the in-house sparse LU became the sole production backend and CSparse.NET was "demoted to a dev-time equivalence oracle": it exists only so tests can solve the same matrices with independent code and assert agreement. This role also resolves a licensing constraint — CSparse.NET is LGPL while manatee-core is MIT, so keeping it as a test-only dependency means no LGPL DLL is ever distributed with a mod. For the EE: think of a calibrated lab reference instrument that stays on the bench and never ships inside the product; it validates the product's readings during development. The dense LU referee plays the same role but remains in the in-repo test suite.

**Standard term:** test oracle / differential testing against a reference implementation.

**Where it appears:** `docs/design.md` Licensing ("CSparse.NET (LGPL) is a dev/test-only dependency (equivalence oracle)"); `docs/solver.md` Numerics; `docs/api.md` sec on shipping assemblies.

**References:** W. M. McKeeman, "Differential Testing for Software", *Digital Technical Journal* 10(1), 1998.

### equivalence spot-checks
- A small, selective set of tests confirming that the in-game Vintage Story GUI renderer draws the tablet's display list the same way the SkiaSharp renderer does.

The tablet's view layer emits a *display list* — a backend-neutral description of what to draw — and multiple rasterizers turn it into pixels: SkiaSharp on desktop and in headless pixel tests, and the Vintage Story GUI in-game. Because the pure layers beneath (document, interaction state machine, display-list generation) are already fully tested, the VS backend only needs sampled comparisons against Skia output plus manual protocols, not exhaustive coverage — hence "spot-checks" rather than a full golden-image suite. For the EE: like verifying a second plotter against a reference plotter by drawing a handful of representative test patterns instead of re-qualifying every drawing. The principle is one contract, multiple implementations, agreement asserted on samples.

**Standard term:** equivalence tests (sampled); closely related to golden-image / snapshot testing of rendering backends.

**Where it appears:** `docs/harness.md`, Rendering Backends.

### equivalence test
- A standing automated test asserting that two different ways of computing the same thing produce identical results — one of the three pillars (oracles, invariants, equivalences) Manatee's correctness rests on.

The pattern: build the same answer along two independent paths and diff them. The main instances are **reduction equivalence** (a circuit solved raw vs. after series-collapse compaction must give identical terminal voltages/currents, with probe interpolation matching the raw interior), **incremental equivalence** (any sequence of live edits vs. a from-scratch rebuild of the final state must yield identical islands and solutions — the same diff ships in-game as the resync backstop), and **snapshot round-trip** (solve → save → restore → step must match a never-saved run bit-for-bit). The power of the technique is that no one needs to know the *right* answer, only that two routes to it must agree — crucial for a team with no EE. For the EE: it is the software analogue of checking that a Thévenin-reduced circuit behaves identically to the original at its terminals; the reduction is only legal if it is semantically invisible.

**Standard term:** equivalence testing; a form of metamorphic testing (and of differential testing when the two paths are separate implementations), used where no independent oracle exists.

**Where it appears:** `docs/testing-strategy.md`, Equivalence Tests; `docs/compaction.md`, Invariants and Incremental Maintenance; `docs/api.md` (e.g. `LimitEventEquivalenceTests`).

**References:** T. Y. Chen, S. C. Cheung & S. M. Yiu, "Metamorphic Testing: A New Approach for Generating Next Test Cases", HKUST-CS98-01, 1998; W. M. McKeeman, "Differential Testing for Software", *Digital Technical Journal* 10(1), 1998.

### Executable laws / property test
- A **property test** is a test that asserts a general rule holds for *all* inputs (often randomly generated), rather than checking one hand-picked example; Manatee's **executable laws** are five such rules about serialization and state, stated as equalities and run as standing CI gates.

Where an example test says "for this circuit, the output is 4.7 V", a property test says "for *any* circuit, saving and reloading yields the same bytes" — the test framework (CsCheck, in our stack) hunts for a counterexample. For an EE, the analogy is a conservation law versus a spot measurement: KCL must hold at every node of every circuit, and a checker that verifies it on any circuit you feed it is worth far more than one measured example. The five laws (api.md §14): (1) `SaveCanonical(FromCanonical(x)) == x` byte-equal; (2) `SaveNormalized` is a fixpoint — normalizing an already-normalized save changes nothing; (3) any edit sequence and its from-scratch rebuild agree under `SaveNormalized` — this is requirement R11's incremental-maintenance drift detector stated as an equality; (4) solve → snapshot → restore → step matches a never-snapshotted run bit-for-bit on the raw solution vector (within one runtime/arch/build); (5) the two-level drift backstop — cheap fingerprint every N ticks, full normalized diff and `Resync` only on mismatch. The laws run over the same lesson corpus as the ngspice oracle harness (§22.c), so every golden circuit exercises them.

**Standard term:** property-based testing (popularized by QuickCheck); "executable laws" is our phrasing for algebraic laws (round-trip, fixpoint, commuting-paths equalities) promoted to permanent CI properties.

**Where it appears:** `docs/api.md` §14 (the five laws) and §22.c (laws 3–4 run in the oracle harness); `docs/testing-strategy.md` Property and Fuzz Tests.

**References:** Claessen & Hughes (2000), "QuickCheck: A Lightweight Tool for Random Testing of Haskell Programs", ICFP.

### fan-out
A software pattern where one event or call is dispatched to many independent listeners; in our docs it names how Vintage Story's microblock entity notifies every attached behavior when its voxels change.

When a player chisels a block, the engine's `BEMicroBlock` calls `RebuildCuboidList` once, and that single notification fans out to each `IMicroblockBehavior` attached to the block entity (dispatch point at `BEMicroBlock.cs:907`). Our cable behavior is one such listener: it rescans the voxel data on every edit to keep the electrical graph current. For an EE, the analogy is literal — "fan-out" is borrowed from digital electronics, where it means the number of gate inputs one output drives. Here one "output" (the edit event) drives several "inputs" (behavior callbacks), each of which reacts independently without knowing about the others. The practical consequence noted in the docs: behaviors are notified *after* the change, so fan-out gives observation, not veto power.

**Standard term:** standard in both fields — event fan-out / one-to-many dispatch in software (cf. the Observer pattern), gate fan-out in digital electronics.

**Where it appears:** `docs/vintage-story.md` section 1 (the BE-behavior seam; chosen architecture, representation 1).

**References:** E. Gamma, R. Helm, R. Johnson & J. Vlissides, *Design Patterns* (1994) — Observer, the pattern behind event fan-out; Horowitz & Hill, *The Art of Electronics*, for the digital-logic sense of fan-out.

### First-class / proven
- Software phrasing meaning an integration point is an officially supported, demonstrated mechanism of the host engine — not a fragile workaround.

"First-class" says the mechanism is a deliberate, supported part of the engine's design (mods are *meant* to plug in there); "proven" says we are not guessing — a shipping vanilla feature already uses the exact same seam, so we know it works in practice. For an EE, the analogy is the difference between connecting through a manufacturer-provided terminal block that the vendor's own accessories already use, versus soldering a jumper onto an internal trace: the former is documented, stable across revisions, and known-good. In `docs/vintage-story.md` the phrase certifies the BE-behavior seam: Manatee attaches electrical semantics to chiseled microblocks via `IMicroblockBehavior`, and the vanilla snow-cover behavior (`BEBehaviorMicroblockSnowCover`) is the existing template that demonstrates every capability we need (own state, own serialization, own mesh overlay, tick listeners).
- Both words are standard software idiom: "first-class" (from "first-class citizen" in programming languages) means fully supported rather than bolted on; "proven" here means demonstrated by an existing in-tree user.
- **Where it appears:** `docs/vintage-story.md` §1, the BE-behavior seam (with engine file:line citations).

### Fixture
- Recorded input data — a captured voxel/export snapshot or netlist — saved into the test suite so a bug found in the live game can be replayed automatically forever after, without the game running.

In testing jargon, a fixture is the fixed, known starting state a test sets up before exercising the code. Manatee's game-layer fixtures are recordings: when a bug appears in-game, the offending world geometry or exported netlist is captured to a file, and a unit test feeds that file through the extraction/solver code and asserts the correct result. The EE analogy is a test fixture on the bench — a jig that holds the device under test in a precisely reproducible configuration so the same measurement can be repeated on demand; here the "jig" is data instead of hardware. The project's fixture rule: every in-game bug that *can* be captured this way becomes a regression test; only genuinely engine-interactive bugs stay as manual test protocols.
- Standard software-testing term ("test fixture"); our specific usage overlaps with what is sometimes called a "golden" or "recorded" input.
- **Where it appears:** `docs/testing-strategy.md`, Game-Layer Testing (fixture rule).
- **References:** Meszaros, "xUnit Test Patterns: Refactoring Test Code" (Addison-Wesley, 2007) — the standard treatment of test fixtures.

### Flags bitfield
- A single integer in which each binary digit (bit) independently encodes a yes/no option, so one number carries many switches at once.

  Programmers pack booleans this way for compactness: bit 1 means one option, bit 2 another, bit 4 another, and you test them with bitwise AND (`flags & 4`). For an EE, it is exactly a bank of DIP switches read as one binary word — each switch position is a bit. In the Falstad file format every line starts (after the geometry) with such a field. On the `$` header line the bits toggle display settings (current dots, small grid, hide voltage colors, show power, hide values — some bits inverted-sense). On element lines the bits are per-element-type and sometimes gate whether later parameters exist at all (see *flag-gated params*), which is why our spec warns that a parser must always decode flags rather than skip them.

- **Standard term:** yes — "bitfield", "bit flags", or "bitmask" are all common names for the same idea.
- **Where it appears:** `docs/falstad-format.md` §2 (`$` header line), §3 (element-line grammar), §4 (per-element flag meanings).
- **References:** Kernighan & Ritchie, "The C Programming Language" (bitwise operators and flag idioms, §2.9).

### Flood-fill / discovery
- A way of finding everything connected to a starting point by repeatedly spreading to neighbors, like paint poured on one spot flowing outward until it has covered the whole connected region.

In programming terms this is graph traversal (breadth- or depth-first search): start at one element, visit each connected neighbor, mark it visited, and repeat until nothing new is reachable — the set of visited elements is the discovered network. For an EE, it is exactly how you'd trace a net on a schematic with a finger: follow every wire from a starting terminal until you've touched everything conductively joined to it. In Manatee's Vintage Story integration the term describes the engine's *mechanical* power network discovery: only producers (e.g. a windmill) create networks, and discovery flood-fills through each block's `HasMechPowerConnectorAt` check to enumerate every shaft, gear, and rotor attached to that producer. The same idea underlies electrical island detection in the solver — connected-component discovery over the conductor graph decides which components share one matrix.

Standard terms: flood fill (from raster graphics), graph traversal / BFS / DFS, connected-component labeling.

**Where it appears:** `docs/vintage-story.md` §2 "Network mechanics" (`BEBehaviorMPBase.cs:171,419,486`); conceptually behind island partitioning in `docs/solver.md`.

**References:** Cormen, Leiserson, Rivest & Stein, *Introduction to Algorithms* (breadth-first and depth-first search, connected components).

### .Forget()
A C#/UniTask idiom for starting an asynchronous operation and deliberately not waiting for it to finish — "fire and forget."

In Unity games, work scheduled off the main thread must eventually hand its results back on the main thread. UniTask (the async library Stationeers uses as house style) lets you write that handoff as an async task; calling `.Forget()` on it says "run this, don't make me stand here holding the result, and don't silently swallow errors." For an EE, the analogy is dispatching a work order into another department's inbox: you drop it off and go back to your own bench rather than waiting at the counter — the marshaling machinery guarantees it gets processed on the right "shift" (the main thread). Manatee follows the game's existing `SetPowerFromThread` pattern (`Device.cs:1333`): expensive island rebuilds run on a background task, then results marshal back via `UniTask.SwitchToMainThread()` and `.Forget()`, with the network running on its previous solution (or the scalar fallback) for one tick until the rebuild lands.

Standard term: this is the general "fire-and-forget" async pattern; `.Forget()` is the UniTask-specific spelling (Cysharp's UniTask library).

**Where it appears:** `docs/stationeers.md` (Performance / design consequences of the threading model).

### front-matter
- A small structured data block placed at the very top of a Markdown document, meant to be read by programs while the rest of the file is prose for humans.

The convention (popularized by static-site generators like Jekyll) is that a Markdown file may open with a fenced key–value section — typically YAML between `---` lines — that tools parse as data before rendering the human-readable body. In Manatee, each lesson's README carries front-matter listing expected observations as probe/time/value/tolerance tuples; CI's narrative test pass reads that block and checks the numbers against the solver's actual output, so a lesson whose story stops being true fails the build. EE analogy: it is like the specifications table printed at the head of a datasheet — a machine-checkable summary of the claims the prose goes on to explain, kept physically attached to that prose in the same document.

**Standard term:** front matter (commonly "YAML front matter"); our usage is the standard one, applied to lesson expectations.

**Where it appears:** `docs/testing-strategy.md` (The Lesson Corpus as Goldens, narrative pass) and `docs/curriculum.md` (lesson format).

### fuzz axis / fuzzing
- Fuzzing is testing by feeding a program large volumes of randomized (but seeded, reproducible) inputs to shake out failures no hand-written test would think of; a fuzz axis is one specific input dimension the randomization sweeps.

In Manatee's test suite, one fuzz axis sweeps component values across the entire legal stamped-conductance range, [1e-9, 1e3] siemens — from a 1 GΩ open switch to a 1 mΩ closed switch — precisely because that range was chosen to avoid the numerical conditioning cliff, and the claim needs adversarial evidence, not trust. Other fuzz dimensions include randomized circuit topologies (seeded R/V/I ladders and meshes, cross-checked against ngspice) and randomized edit sequences. All fuzzing is seeded and deterministic, so any failure replays exactly. For an EE, fuzzing is the software analogue of parameter-sweep stress testing on a bench: instead of verifying one nominal operating point, you sweep supply, load, and temperature across the full rated envelope and confirm the device never misbehaves — a "fuzz axis" is one knob in that sweep. The project uses the CsCheck library for property/fuzz tests.

Standard term ("fuzzing", "fuzz testing"); closely related to property-based testing (QuickCheck lineage). "Fuzz axis" is our shorthand for one sweep dimension.

**Where it appears:** `docs/solver.md` Numerics (conductance-range policy); `docs/testing-strategy.md` "Property and Fuzz Tests".

**References:** Claessen & Hughes (2000), "QuickCheck: A Lightweight Tool for Random Testing of Haskell Programs", ICFP (origin of property-based testing); Miller, Fredriksen & So (1990), "An Empirical Study of the Reliability of UNIX Utilities", CACM (origin of the term "fuzz").

### tick / game tick / power tick / tick rate / tick listener
The host game's fixed-size simulation step — the periodic heartbeat that advances the world in discrete equal slices and calls `Netlist.Solve(clock)` exactly once each time it fires.

Games do not simulate continuously; they advance the world in discrete, equal time slices, like a clocked digital system rather than analog. Each slice is a *tick*, and its length is the *tick rate*. Manatee never runs on its own schedule: the host game's tick calls `Netlist.Solve(clock)` once, and the solver integrates the circuit forward by exactly that tick's duration (`Dt`). Within one game tick the solver may take several smaller internal substeps for AC — those happen inside one `Solve` and are invisible to the game. The two hosts have different heartbeats. Vintage Story uses a 50 ms electrical tick that our mod owns via a *tick listener* it registers itself — VS mods choose their interval, there is no engine-wide electrical tick; a block behavior registers tick listeners in its `Initialize` method and the engine then calls the supplied function on the requested interval (you don't poll, the clock edge comes to you). Stationeers' vanilla *power tick* — the 0.5 s step on which all electrical devices update — is replaced wholesale by the Re-Volt mod, which runs the Manatee tick body in its place. Because different clocks coexist (e.g. the engine's mechanical-power mod runs a 20 ms tick with torque re-summed only every ~5 ticks), the docs treat cross-domain inputs like shaft speed as piecewise-constant per electrical tick — sampled, not continuous. For an EE: the tick is the sampling interval of a discrete-time simulation; anything faster than it is invisible, which is why the curriculum's component values are deliberately scaled (the Electrical Age trick) so time constants land at human-visible scales (0.1–10 s) where a 50 ms step resolves them. Design perf targets are stated per tick (e.g. ≤ 5 ms on-thread for the VS tick).

Standard game-programming term; aliases *fixed timestep*, *simulation step*, *update loop iteration*; a tick listener is a periodic form of the Observer/callback pattern (*timer callback*, *scheduled update*).

**Where it appears:** `docs/api.md` §4 (`Solve(in TickClock)`), §22 consumer walkthroughs; `docs/design.md` (Simulation Model, perf targets, transient dt); `docs/vintage-story.md` §1–2 and Tablet Host (tick listeners, behavior `Initialize`, mechanical 20 ms tick); `docs/curriculum.md` (Format — value scaling); `docs/stationeers.md` (Re-Volt's replaced power tick).

**References:** Glenn Fiedler, "Fix Your Timestep!" (gafferongames.com); Robert Nystrom, *Game Programming Patterns* (Game Loop, Observer chapters).

### GC.GetAllocatedBytesForCurrentThread
- A built-in .NET counter that reports how many bytes of memory the current thread has ever allocated, used by our zero-allocation test gates.

Our "zero-alloc" tests read this counter before and after a solver step and assert the delta is zero — proof that steady-state ticking (tiers 1–2 after warmup) creates no garbage for the runtime's collector to clean up. For an EE: it is like a per-channel charge counter on an instrument — you read it twice and difference the readings, but the instrument itself occasionally injects a small offset. Specifically, the counter *over-reports* when the runtime's garbage collector or JIT compiler retires the thread's partially-used allocation quantum mid-measurement, showing a phantom hundreds-of-bytes delta for a step that truly allocated nothing. Because this perturbation is strictly additive, our sound gating mechanism is min-over-N sub-runs: one clean sub-run (min == 0) proves the property, while a single-shot delta assertion is a flake. Tests also warm up first and auto-skip on runtimes where the counter is inert.

**Where it appears:** `docs/testing-strategy.md` § Benchmarks (Flake isolation for the zero-alloc gate); the `ZeroAlloc` test collection and `TierBudgetGateTests` pattern; capability probe `ZeroAllocGates.CounterIsReliable`.

**References:** Microsoft .NET API documentation, `System.GC.GetAllocatedBytesForCurrentThread`.

### GearedRatio
- A per-node scaling factor in Vintage Story's mechanical power system that converts between a node's local shaft speed/torque and the shared network quantities, based on the gearing between them.

Vintage Story models windmills, gearboxes, and querns as a "mechanical network": one shared network speed, with each attached node (device) related to it through its gear train. `GearedRatio` is that gear train's ratio, stored on the node. When the engine sums up the network each update (`MechanicalNetwork.updateNetwork`), a node's torque contribution is multiplied by `GearedRatio` and its resistance by `|GearedRatio|` (plus a speed-squared drag term), and a node's local speed is the network speed times its ratio. For a programmer: it is a per-edge conversion coefficient applied at aggregation time, exactly like a unit conversion applied when folding values into a shared accumulator. For an EE: it is the mechanical analogue of a transformer turns ratio — speed scales up as torque scales down. Manatee's alternator and motor devices live behind this contract, so shaft speed reaching the electrical solver has already been scaled by it; our docs also note it as one lever for reaching realistic electrical frequencies (R14).

**Where it appears:** `docs/vintage-story.md` §2 (Mechanical Network Coupling, the `IMechanicalPowerNode` contract); `docs/experiments/2026-07-05-vs-mechanics.md` with engine file:line references. It is Vintage Story engine vocabulary (`vssurvivalmod` MechanicalPower code), not a Manatee coinage.

### generation (Gen)
- The version counter carried inside every Manatee handle, used to detect that the handle has gone stale because the solver rebuilt the thing it pointed at.

Every handle (e.g. `ResistorId`) is a small value of three fields: `Slot` (which storage cell), `Gen` (which *issue* of that cell), and `Net` (which netlist). When an island is rebuilt — after a wire cut, a breaker trip, or a resync — the solver bumps the generation on the affected slots, so any old handle a game adaptor is still holding no longer matches and is rejected instead of silently reading the wrong component. For an EE: think of Slot as a terminal position on a patch panel and Gen as a dated inspection tag — if the panel was rewired since your tag was issued, the tag mismatch tells you your old note about that terminal is void, even though the physical position still exists. Recovery is never guesswork: adaptors re-resolve by their stable `ExternalKey` (the tutorial's `Repin` pattern) and receive a fresh `(Slot, Gen, Net)`.

**Standard term:** the *generational index* (or generational handle/slot-map) pattern, common in game-engine entity systems.

**Where it appears:** `docs/integration-tutorial.md` §4 (the `Repin` example constructing `new ResistorId(c.Slot, c.Gen, c.Net)`); defined normatively in `docs/api.md` §3. See also **generation (Gen) / generational reissue** for the full invalidation rules.

### generation (Gen) / generational reissue
- The 32-bit version counter in each Manatee handle, and the act ("reissue") of bumping that counter on every handle rooted in an island when the island is rebuilt.

A handle is `(Slot, Gen, Net)`: the slot may be reused for a different component later, so the generation distinguishes issues of the same slot — a stale handle fails a simple integer compare rather than aliasing onto the new occupant. "Reissue" is the island-scoped bulk form: a rebuild (triggered by any removal, a galvanic coupler `Open`, or a journal-overflow/drift resync) walks the island's member slots and bumps each one's generation — per-member, not a contiguous range, because merges interleave slots. Two binding refinements: reissue happens at the next `Solve`, not at edit commit (only the removed component's own slot dies at commit; the doomed-but-usable window is safe because verbs write the retained document and the rebuild restamps from it — decision log #1), and it is island-scoped, so handles into other islands are untouched. Couplers and probes are document-level registrations, exempt from reissue. Detection is uniform: `StaleHandleException` in debug, a defined sentinel plus a `TickStats` counter in release. Gen is 32-bit; a slot reused every 50 ms tick wraps in ~6.8 years (debug warns at 2^31). For an EE: reissue is like re-serializing every tag on a panel after a rewiring job, so no pre-rewiring paperwork can be mistaken for current.

**Standard term:** *generational index / generational arena*; the island-wide reissue policy is project-specific.

**Where it appears:** `docs/api.md` §3 (handle definition), §16 (handle-survival table), §24 decision log #1 (bump timing); `docs/integration-tutorial.md` §4/§6 (re-pin recovery).

### git submodule
A git feature that embeds one repository inside another at a fixed, recorded revision — the mechanism by which the Re-Volt mod's repository consumes manatee-core.

A submodule is a pointer, not a copy: the outer repository (Re-Volt's) records "the `manatee-core` directory is *that other repository*, pinned at exactly *this* commit." Anyone checking out Re-Volt gets a reproducible snapshot of manatee-core, and upgrading to a newer solver is an explicit, reviewable one-line change to the pin. The EE-world analogy is buying in a qualified subassembly by exact part number and revision letter: the parent design doesn't absorb the schematic, it references a specific certified revision, and a rev bump is a deliberate engineering-change decision rather than something that drifts silently. This is why the Stationeers integration lives in Re-Volt's repo while the solver stays here, and it is part of why manatee-core is MIT-licensed (Re-Volt is MIT and vendors us in).

This is the standard git term; the same idea appears elsewhere as "vendored dependency at a pinned revision" (Mercurial *subrepos*, `git subtree` is a related alternative).

**Where it appears:** docs/stationeers.md intro; docs/design.md (delivery/licensing notes); project CLAUDE.md.

**References:** Chacon & Straub, *Pro Git*, 2nd ed., ch. 7 "Git Tools — Submodules".

### Glyph run
A single draw command in the display list that says "render this sequence of text characters, with this font and position" — text as data, not yet pixels.

In the harness's view layer, rendering produces a **display list**: a flat sequence of draw commands (stroke path, fill, glyph run, ...) in schematic coordinates. A glyph run is the text-shaped one: the resolved glyphs (character shapes) to draw and where, deferred until some backend actually rasterizes them. Keeping text as a glyph-run record rather than baked pixels is what makes display-list snapshots the primary visual golden in testing — a diff shows "the label text changed from '5 V' to '5V'" instead of a blob of differing pixels, with no font or GPU nondeterminism. For an EE, the analogy is a schematic annotation callout on a drawing layer: the drawing stores "REF DES 'R1' at grid E4 in the standard font," and only the plotter turns that into ink; you can compare two drawings by their callout tables without printing either.

This is standard text-rendering vocabulary (DirectWrite, Skia, HarfBuzz all use "glyph run" for a positioned sequence of glyphs sharing font properties); a *glyph* is the visual shape of a character, distinct from the character code itself.

**Where it appears:** docs/harness.md, Layering item 3 (View layer / display list) and the Testing Model that snapshots it.

### golden test / golden / lesson corpus / CI truth
A golden test compares a program's output against a stored, human-approved expected value — the *golden* — and fails on any mismatch. It does not recompute what the right answer *should* be; it pins what the right answer *was* when a human last approved it, forcing every change in behavior to be noticed and deliberately re-approved.

For an EE, a golden is a certified reference standard in a cal lab: you keep it on the shelf and verify the instrument against it rather than re-deriving "correct" each time — and you do not adjust the standard to match the instrument; a disagreement is by definition an instrument problem until a human rules otherwise.

**The flagship goldens are the tablet lessons** (design.md R20, "lesson corpus as goldens"). Each lesson is a directory in `lessons/` (Electrical Age `docs/examples` style) holding a Falstad-format netlist, a Markdown narrative that states concrete numbers in prose ("about 2.7 V after 4.5 s"), and a machine-readable front-matter block listing those expectations as (probe, time, value, tolerance) tuples. CI consumes each lesson **twice**: an *oracle pass* (the netlist solved by both manatee-core and ngspice, node voltages diffed within tolerance — establishes the physics is right) and a *narrative pass* (the front-matter expectations checked against the manatee solution — establishes the teaching content still matches the physics). A solver change that shifts a reading from 2.7 V to 2.9 V breaks the build even if ngspice agrees with the new value, because the lesson text now teaches a falsehood — the golden pins the prose, not just the math. This is design.md R20's "one corpus, three consumers": the same file is tutorial content shown to players, a documentation example, and a stored reference measurement — so "*a lesson that stops being true fails the build*" (curriculum.md, Format) and the teaching material can never silently drift from what the solver computes. Because the tablet runs client-side with contractual solver determinism, lesson goldens behave identically in-game and in CI (`docs/vintage-story.md`, Tablet Host). As of 2026-07-06 the gate auto-discovers lesson directories; the corpus holds lessons 01–02 (DC) and 04 (RC transient), the rest being deferred authoring, so the goldens assert only what is in the corpus today.

**"CI truth"** is project phrasing for those files being the authoritative expected answers. In `falstad-format.md` §7 that status drives the importer's strict error posture: because the corpus defines expected results, the importer may drop *nothing* silently — every element is accepted with full electrical semantics, ignored with a logged-and-counted notice (presentation-only lines), or rejected loudly with line number and reason. The word is also used for other snapshot artifacts: emitted SPICE deck text (see *Verify*), harness display lists, and bounded pixel goldens (see *golden PNGs / visual golden*). The trade-off is deliberate: goldens catch *any* change, so a legitimate improvement requires a human to review the diff and re-bless the stored answer.

**Standard term:** golden / golden-file test; common aliases **snapshot test** (Jest, the Verify library), **characterization test** (Feathers), **approval test**. "CI truth" is project phrasing.

**Where it appears:** `docs/design.md` R20 and Testing and Validation; `docs/testing-strategy.md` (The Lesson Corpus as Goldens — the two CI passes and corpus status; Toolchain); `docs/curriculum.md` (Format); `docs/falstad-format.md` §7 (importer accept/reject contract, oracle note); `docs/vintage-story.md` (Tablet Host); exercised in `docs/api.md` §22.c.

**References:** Feathers, *Working Effectively with Legacy Code* (2004) — "characterization tests", the same idea under another name.

### golden PNGs / visual golden
A stored, known-good reference output that automated tests compare fresh output against — either as stable text or, for the raster tier, pixel by pixel — to detect unintended changes.

The harness's rendering tests come in two tiers. *Display-list snapshots* — the drawing-command sequence (stroke path, fill, glyph run, ...) serialized as stable text — are the **primary visual golden**, giving reviewable diffs with no font or GPU nondeterminism. A deliberately small secondary suite of *pixel goldens* — reference PNGs checked into the repo — is compared byte-for-byte against fresh output: the display list is rasterized through headless SkiaSharp with a font bundled in test assets, and the resulting bitmap must match the stored golden PNG. For an EE: the display list is the schematic, the PNG is a photograph of the assembled board — you mostly review schematics, but a few photographs catch assembly-level (rasterizer-level) defects. The pixel suite is kept small on purpose, avoiding the classic failure mode where a font-hinting or GPU-driver change invalidates hundreds of image goldens at once; bundling the font and rendering headlessly removes those nondeterminism sources. Updating a golden is a deliberate, reviewed act — it means "the new output is the new correct answer."

**Standard term:** golden images / screenshot tests / pixel-snapshot tests (also "characterization test", "approval test").

**Where it appears:** `docs/harness.md` (Testing Model — pixel snapshots vs. display-list snapshots); lesson-corpus goldens also appear in `docs/testing-strategy.md`.

**References:** Feathers, *Working Effectively with Legacy Code* (2004) — the closely related characterization-test technique.

### Greedy-merged cuboids
- A compressed way of storing voxel geometry: instead of recording every tiny cube individually, runs of adjacent same-material voxels are merged into the largest boxes (cuboids) that fit, and only the boxes are stored.

Vintage Story's microblock entity (`BEMicroBlock.cs`) stores chiseled-block geometry this way: a `List<uint> VoxelCuboids` where each 32-bit entry packs a box's min corner, max corner, and a material index — not a dense 16×16×16 array. "Greedy" refers to the merging strategy: the algorithm grabs the biggest box it can at each step rather than searching for a globally optimal packing. For an EE, the analogy is run-length encoding of a signal, extended to three dimensions: you store "this whole region is copper" once instead of one sample per point. The payoff is memory and iteration speed; the cost is that a single voxel edit can force re-merging (VS fires `RebuildCuboidList` on every edit). Manatee reads this representation directly when extracting cable geometry from microblocks.

**Standard term:** the output of *greedy meshing* (a standard voxel-engine technique); the storage idea is a 3D analogue of run-length encoding.

**Where it appears:** `docs/vintage-story.md` §1 "Engine facts — Storage" (with file:line references into `vssurvivalmod`).

### Greedy-meshed prisms
- The intermediate representation in the Vintage Story intake pipeline: adjacent cable voxels merged into larger rectangular boxes (prisms) before being turned into electrical regions.

The compaction layer's VS intake runs sparse voxel storage → greedy-meshed prisms → regions — a pipeline inherited from sparky, our earlier prototype. Greedy meshing is the standard voxel-engine algorithm: sweep through the voxel grid and greedily grow each box as large as adjacent identical voxels allow, so a straight 12-voxel cable run becomes one prism instead of 12 cells. In circuit terms this is a first, purely geometric round of series collapse: it shrinks the element count before the electrical reduction layer (series compaction proper) ever sees the graph. It is lossless — the prisms cover exactly the same voxels, just in fewer records — so the semantic-invisibility invariant of the reduction layer is unaffected.

**Standard term:** greedy meshing (voxel/game-engine literature); "prism" here just means an axis-aligned box, same object as the *greedy-merged cuboids* VS itself stores.

**Where it appears:** `docs/compaction.md`, "Client Intake Contracts" (VS voxel world); pipeline design originates in `third_party/sparky/`.

### handle
- A small (12-byte) value that refers to a live object inside the solver — a component, node, island, probe, or coupler — without being a direct pointer to it; the caller holds the handle and presents it on every read or mutation.
- Each handle is a (Slot, Gen, Net) triple: which storage slot, which *generation* of occupant in that slot, and which netlist it belongs to. Because validity is checked on every use, a handle can be held cheaply across ticks and saved-game boundaries in client code, but it can *die*: island rebuilds (splits, removals, journal-overflow resync) invalidate the affected handles at the next `Solve`, and the client re-resolves them from its own stable `ExternalKey`s (`TryResolve`/`Repin`). Using a stale handle throws `StaleHandleException` in debug builds; in release it degrades to a counted no-op (reads return 0.0, mutations do nothing, `TickStats.StaleHandleReads` increments) so a shipped game never crashes on a stale read. For the EE: a handle is like a terminal designation on a schematic revision — "R7 pin 2" stays meaningful only while that revision is current; after a redesign you must look the part up again in the parts list (the external key), and the system tells you loudly (debug) or shrugs safely (release) if you use an outdated designation.
- **Standard term:** generational handle / generational index (also "weak handle"), a well-known pattern in game-engine entity systems.
- **Where it appears:** `docs/solver.md` (Netlist API: `ComponentId`, `NodeId`, …); `docs/api.md` §3 and the §16 survival table; `docs/integration-tutorial.md` §6 ("Handles: the survival rules, digested").

### handle (generational typed handle)
- The specific form Manatee's handles take: a distinct 12-byte value type per kind of thing (`ResistorId`, `NodeId`, `IslandId`, …), each carrying (Slot, Gen, Net) — the core software-identity primitive of the API.
- Three failure modes are engineered away by the layout. *Cross-kind misuse fails to compile*: because each kind is its own C# struct type, passing a `NodeId` where a `ResistorId` is expected is a type error, caught before the program runs (sparky's typed-ID pattern). *Stale or generation-wrapped use fails fast*: `Gen` is 32-bit, so a slot reused every 50 ms tick wraps only after ~6.8 years (debug warns at 2^31); a mismatched generation raises `StaleHandleException` in debug and degrades to a defined sentinel plus a `TickStats.StaleHandleReads` counter in release. *Cross-netlist use is caught by value*: `Net` is a process-wide netlist id, so a handle from another `Netlist` is rejected outright rather than accidentally aliasing a slot. A binding implementation rule keeps generic readback allocation-free on both CoreCLR and Mono (the constrained-callvirt pattern on `IComponentId` — never boxing). For the EE: this is like giving resistors, capacitors, and nodes physically incompatible connector shapes — you cannot plug the wrong kind in at all, and a connector from an obsolete board revision is detected the moment you try it.
- **Standard term:** generational index / typed handle; the compile-time-kind aspect is the "strongly-typed identifier" (newtype) idiom.
- **Where it appears:** `docs/api.md` §3 (definitions and wrap bound), §16 (invalidation/survival table and release-mode sentinel semantics), §24.6.

### Harmony injection / Harmony-injected / Harmony-substituted
- Using the Harmony runtime-patching library to intercept or replace a running game's compiled methods and classes in memory at load time — without editing the game's files — so mod code runs where game code would have; "Harmony-injected"/"Harmony-substituted" mark a method, class, or subclass that exists only because of such a rewrite.

Harmony is the standard .NET modding library for patching compiled methods as the game loads: prefix hooks (run my code first, optionally skip the original), postfixes, and full replacements, with no change to the game's files on disk. The physical analogy for an EE: the game ships as a sealed unit, and Harmony lets you clip probe leads and splice a relay in series onto internal test points at power-up without opening the case — remove the mod and the original circuit is untouched. Manatee inherits two such seams from Re-Volt's Stationeers integration. A Harmony-injected `RevoltTick : PowerTick` replaces the vanilla power tick wholesale, intercepting the `Initialise` → `CalculateState` → `ApplyState` phases per cable network (Manatee replaces the arithmetic between `CalculateState` and `ApplyState` with a real MNA solve). A Harmony-substituted `MNACableNetwork` subclass stands in for the game's `CableNetwork`, additionally carrying the mapping from each vanilla cable network to its Manatee solver nodes, so the game's per-network power calls can resolve to "which nodes do I stamp." This is what makes the integration removable: all persistent state keeps flowing through vanilla calls, so uninstalling the mod restores vanilla behavior intact — and failure is graceful by construction, since if the Harmony injection fails entirely Re-Volt's existing prefix pattern falls back to the vanilla `PowerTick`, giving the degradation ladder manatee → Re-Volt scalar → vanilla.
- "Harmony" names the real library (pardeike/Harmony, standard in .NET game modding), not project-coined; the general technique is *runtime method patching* / *monkey patching*. "Harmony-substituted" is our shorthand for a Harmony-installed subclass replacement.
- **Where it appears:** `docs/stationeers.md` (Integration Seams; Failure and Fallback); `docs/design.md` Stationeers section.

### headless
- Running software that normally has a screen and a user without either — no window, no GPU, no game engine — so it can execute automatically on a build server.

The "head" is the display; a headless run keeps all the logic but replaces human input with scripted events and visual output with inspectable data. The EE analogy is bench testing a board on ATE instead of in the finished product: same circuit, but stimulus comes from a test fixture and outputs go to a logger rather than a front panel. In Manatee, the 2D harness (the tablet's schematic engine) is deliberately built as pure layers so its scenarios — build a circuit, edit it, save/load, inject a fault — run headless in CI: interaction tests feed synthetic pointer events to the interaction state machine, rendering is checked first as display-list text snapshots, and a small secondary suite rasterizes pixels through headless SkiaSharp with a bundled font. This is what lets the harness serve as the integration test bed for netlist extraction, solving, and probe readback with no game engine in the loop.
- Standard software term ("headless browser", "headless server"); no project-specific twist.
- **Where it appears:** `docs/testing-strategy.md` (Game-Layer Testing; Toolchain); `docs/harness.md` (Testing Model, Rendering Backends, The Desktop Shell — Avalonia.Headless noted as possible but rare).

### Hinting
- In font rendering, the automatic adjustment of a glyph's outline to align with the pixel grid so small text stays crisp — a process whose output can change subtly between font or rasterizer versions.

Manatee's harness docs cite hinting as the classic reason *not* to build a visual test suite on pixel-perfect screenshots: because hinted glyph shapes depend on the exact font file, rasterizer library, and platform, any upgrade can shift text by fractions of a pixel and invalidate hundreds of "golden" reference images at once, even though nothing meaningful changed. The tablet harness therefore makes **display-list snapshots** (a stable textual description of what would be drawn) the primary visual golden, and keeps only a deliberately small secondary suite of pixel snapshots, rasterized through headless SkiaSharp with a font bundled in the test assets so the rasterization environment is pinned. For an EE analogy: hinting is like a measurement instrument whose least-significant digit drifts with calibration — you don't write acceptance tests against that digit; you test the specified quantity (the display list) and keep one calibrated end-to-end check.

This is the standard typography/rasterization term (also called "grid-fitting").

**Where it appears:** `docs/harness.md` §Testing Model (justification for the display-list-first golden strategy).

**References:** Knuth, *Digital Typography* (CSLI, 1999) discusses the pixel-grid rasterization problem; hinting/grid-fitting is specified in the OpenType and TrueType font standards (Microsoft/Adobe OpenType specification).

### host-only / RunSimulation
The rule that the circuit solver executes only on the machine hosting the game session; every other player's machine just displays results it is sent.

In a multiplayer game, one process (the host or dedicated server) owns the authoritative world state, and the other players run *clients* that mirror it over the network. Stationeers expresses this with a property Manatee inherits: `RunSimulation => !NetworkManager.IsClient` — literally "run the simulation only if this process is not a client." Clients never solve the circuit; they receive serialized power state (voltages, on/off, etc.) from the host and merely render it. For an EE, the analogy is a plant with one real control system and several remote read-only mimic panels: the panels show measurements but contain no control loop of their own, so there is exactly one source of truth and no risk of two solvers disagreeing about the same circuit.

The standard names for this pattern are **server-authoritative simulation** or authoritative-server networking; "host-only" is our shorthand and `RunSimulation` is the specific Stationeers property that enforces it.

**Where it appears:** `docs/stationeers.md` (Threading section, verified against the game decompilation).

### hot path
The small stretch of code that runs so often — every simulation substep, every game tick — that its cost dominates total performance, so it must be kept ruthlessly cheap.

Most code in a system runs rarely (setup, editing, error handling); a hot path runs thousands of times per second, so a small inefficiency there is multiplied by the execution count into a large real cost. In Manatee the hot path is the per-substep/per-tick solve, and the working rules are that it stays allocation-free (no allocation in the per-tick steady state — no creating garbage for the memory manager to clean up mid-tick) and cheap in absolute terms: post-compaction tier-1/2 solves are budgeted at microsecond scale, small on its own terms rather than merely small relative to the tick budget. Design decisions are priced against it explicitly: Newton iteration for nonlinear parts is kept *out* of the world-island hot path where a cheaper behavioral model serves, and the ~1 MΩ grounding leak was benchmarked at a 1.5× hot-path multiplier before being accepted. An EE analogy: it is the signal path of an amplifier, as opposed to its bias and protection circuitry — you can afford slow, elaborate circuits everywhere except in the path the signal actually traverses at full bandwidth.

This is standard programming vocabulary (also "critical path," "inner loop," "fast path").

**Where it appears:** `docs/design.md` (Simulation Model — nonlinearity budget; Grounding model — leak-cost benchmark), `docs/solver.md` (tier-1 solve costs), `docs/api.md`.

### hover
The transient user-interface state recording which on-screen element is currently under the mouse pointer, typically used to highlight it before any click happens.

In the schematic editor's layered design, hover lives in the *interaction state machine* alongside the active tool, drag-in-progress, selection, and snapping: it is editing state, not part of the document (the circuit itself) — moving the pointer changes what is hovered without changing the schematic at all. It is "transient" in that it is derived moment-to-moment from pointer position and is never saved; the view layer consumes it to draw highlight feedback. For an EE, hover is like the *seeking* indication on a test probe before you commit: touching the probe near a node lights an annunciator showing what you *would* measure, but nothing in the circuit under test changes. Keeping hover in the pure interaction layer (fed abstract pointer events, no widget toolkit) is what lets the same editor logic run headless in tests and inside two different games.

Standard UI term (also "mouse-over").

**Where it appears:** `docs/harness.md` (Layering, layer #2 — Interaction state machine).

### HUD
Heads-up display: the in-game overlay that shows live information (here, tooltip lines like voltage, current, and temperature) on top of the world view without opening a menu.

The term comes from aircraft heads-up displays — instrument readouts projected in the pilot's line of sight — and in games it means any always-visible overlay. The Manatee-relevant gotcha is architectural: in Vintage Story the HUD reads from the *client's* local copy of a block's data (see host-only — the solver runs on the server), so any value the HUD is to display must first be sent over the network. Naively flagging the block as changed every tick (`MarkDirty()` per tick) would spam full chunk re-sends, so the plan is a throttled telemetry channel broadcasting probe values every ~0.5–1 s, with the oscilloscope item negotiating a temporary high-rate stream for a single probe point. In EE terms: the HUD is a remote meter at the end of a low-bandwidth telemetry link — the display can only be as fresh as the telemetry you budget for, and streaming every sample of every signal would saturate the link.

**Standard term** (aliases in modding circles: tooltip overlay, "WAILA" — What Am I Looking At).

**Where it appears:** `docs/vintage-story.md` §4 (Tooltips and Instruments, R15).

### hysteresis (N quantization)
The rule that an AC island's substep count N is only recomputed when the required sampling rate drifts more than ~±15% from the value N was last chosen for, so N — and therefore the substep timestep dt — stays put under small speed jitter.

On a subcycled-AC island, N is derived from the highest source frequency (≥ 20 samples/cycle). Frequency comes from the mechanical network (a spinning generator), which jitters. Because every Backward Euler companion conductance is a function of dt (e.g. G = C/dt), changing N re-stamps the matrix — a deliberate, rare tier-2 event — so we don't want it happening every tick. The hysteresis band (±15%, `HysteresisBand` in `SubstepPlan`) means dt and all BE conductances hold constant across mechanical-speed jitter, while the source driver still tracks the *exact* frequency cheaply through its per-substep phase increment (tier 1). For the EE: it is the same two-threshold anti-chatter idea as a Schmitt trigger, applied to an integer configuration parameter instead of a logic level. For programmers: think of it as rate-limited cache invalidation — the expensive rebuild fires only when the input has moved far enough to matter.

The hysteresis concept is standard; applying it to substep-count selection is a project design choice, not a named technique in the literature.

**Where it appears:** `docs/solver.md` Analyses (Subcycled AC); `docs/design.md` doc-review round; `docs/api.md` §11 (`SubstepPlan.HysteresisBand`, solver-owned N).

### IBufferWriter
- The standard .NET output interface (`IBufferWriter<byte>`) that manatee-core writes serialized bytes into when saving a netlist or snapshotting island state — the caller supplies and owns the buffer.

Instead of the solver allocating a byte array and handing it back, the client hands in a "sink" object: the writer asks it for a chunk of writable memory, fills it, and reports how much it used, repeating as needed. This inverts ownership — the game engine decides where the bytes land (a pooled buffer, a file stream, a network packet) and the solver core allocates nothing, which is how `Snapshot` stays 0-bytes-allocated core-side. An EE analogy: rather than the instrument printing results onto its own paper and mailing you the page, you plug your own chart recorder into its output jack — the instrument just drives whatever medium you connected. In our API it appears on `SaveCanonical`, `SaveNormalized` (whole-netlist serialization, api.md §4) and `Island.Snapshot` (per-island versioned binary state, §11/§14); it is listed among the client-owned buffers in the ownership table.

This is the standard `System.Buffers.IBufferWriter<T>` interface from the .NET base class library, not a project type.
- **Where it appears:** `docs/api.md` §4 (`SaveCanonical`/`SaveNormalized`), §11 (`Island.Snapshot`), and the buffer-ownership table (§ "client-owned").
- **References:** Microsoft .NET documentation, `System.Buffers.IBufferWriter<T>` (introduced with the .NET Core 2.1 `System.Memory` pipelines work).

### IComponentId / constrained-callvirt
- `IComponentId` is the small interface every typed component handle implements (one method, `AsRef()`), and *constrained-callvirt* is the specific, binding calling technique generic code must use to invoke it without allocating memory.

Every component handle (`ResistorId`, `CapacitorId`, …) is a tiny value type — 12 bytes copied around like a number, not an object on the heap. Generic methods such as `Current<TId>(TId branch)` accept any of them via the constraint `where TId : struct, IComponentId` and need to call `AsRef()` on whatever handle they were given. The naive way to call an interface method on a value type first *boxes* it — copies it into a fresh heap object — which would put a hidden allocation inside the hottest readback path. The constrained-callvirt pattern (the CLI's `constrained.` prefix on the call instruction, which the C# compiler emits automatically for this generic shape) instead dispatches directly on the value type itself: zero allocation, and provably so from the method signature on both runtimes we ship to (CoreCLR and Mono). The alternative trick — an `Unsafe.As` type-switch that relies on the JIT deleting dead branches — is explicitly forbidden, because Mono's JIT may not perform that elimination. An EE analogy: boxing is transcribing a reading onto a form before filing it; constrained-callvirt is reading the meter directly — same information, no per-reading paperwork, and the no-paperwork property is guaranteed by the wiring, not by hoping an optimizer notices.

Both halves are standard .NET terminology: `IBufferWriter`-style interface constraints and the `constrained.` IL prefix are documented platform mechanisms; only the *binding rule* (this pattern is mandatory for all generic readbacks) is project policy.
- **Where it appears:** `docs/api.md` §3 (handle/interface definitions and the binding implementation note), §24 decision log #7; enforced on `Netlist.Remove<TId>`, `Island.Current<TId>`, `Island.Power<TId>`.
- **References:** ECMA-335, Common Language Infrastructure, Partition III (the `constrained.` call prefix); Microsoft .NET documentation on generic constraints and value-type boxing.

### IDeviceStateUnit
The C# interface a behavioral device implements so its internal dynamic state (battery charge, rotor angle, controller memory) gets saved into and restored from island snapshots.

An *interface* is a contract: any class that implements it promises to provide the listed members. Here the contract is four members — `Key` (a `StateKey`, the client-stable identity the saved bytes are filed under), `BlobSize` (a fixed byte count, stable between topology-changing operations), `Save` (write exactly that many bytes), and `Restore` (read them back). The core never interprets the bytes; it only routes each opaque blob by its `StateKey`, the same way the built-in capacitor/inductor/sine state units are handled. That opacity is what keeps restore *additive*: a device's blob restores its own key and nothing else, so if islands merged or split between save and load, each resulting island takes only its matching units. For an EE: it is the device's "state variables" (like a capacitor's voltage) packaged so the simulator can checkpoint and resume them without knowing their physics. For bit-for-bit round-trip a device must serialize everything that influences a future `Tick`.

**Where it appears:** `core/Manatee.Core/State/StateUnit.cs:51`; `docs/api.md` §14 (state/snapshot) ; `docs/integration-tutorial.md` §7 (Save/load); registered via `Netlist.RegisterDeviceState`.

### IDisposable / using / Dispose==Commit
A C# language pattern for deterministic cleanup — an object's `Dispose()` method runs automatically at the end of a `using` block — which Manatee repurposes so that "the block ended" *means* "the operation completed".

In C#, `using (var x = ...) { ... }` guarantees `x.Dispose()` runs when the block exits, even on error — the software equivalent of a spring-return switch that cannot be left in a half-thrown position. Manatee binds transactional semantics to it in three places. `StructuralEdit.Dispose()` **is** `Commit()`: edits stage while the scope is open and apply atomically at Dispose (unless `Abort()` was called first), so a half-applied topology edit can never be observed by a solve — think of it as making all the wiring changes on a de-energized bench, then energizing once. `SteadyStateGuard` (a stack-only `ref struct`) uses `using`/Dispose to bracket the no-structural-changes region of a tick, and its Dispose also runs any release-mode deferred structural ops. `BulkBuild.Dispose()` runs the single compaction pass for the whole load. The common invariant: scope exit is the commit point, and forgetting it is a compile-visible shape error rather than a silent leak.

**Standard terms:** the .NET dispose pattern / RAII (Resource Acquisition Is Initialization, from C++); Dispose-as-Commit is a project convention layered on it (closest standard concept: a transaction scope with commit-on-close).

**Where it appears:** `docs/api.md` §6 (`StructuralEdit`: "Dispose == Commit() unless aborted"), §8 (`SteadyStateGuard`), §19 (`BulkBuild`); `docs/integration-tutorial.md` §6 and §8.

**References:** Microsoft, .NET documentation, "Implementing a Dispose method" / C# `using` statement; Stroustrup, "The C++ Programming Language" (RAII, the ancestral idiom).

### IHeatSource
A Vintage Story engine interface (implemented by vanilla firepits and forges) whose heat warms *player body temperature only* — it never raises a room's temperature, spoilage rate, or crop growth.

This is a verified engine fact that shapes the mod's heat design: vanilla VS rooms have no temperature state at all — perish rates are computed on demand from world climate — and `IHeatSource` feeds only the entity body-temperature behavior (`BehaviorBodyTemperature.cs:354`). So an electrical heater that only implemented `IHeatSource` would keep the player warm while their cellar food spoiled at the ambient rate. Manatee therefore builds its own mod-owned heat registry for room/spoilage effects, and electrical heaters *optionally also* implement `IHeatSource` so they warm players the way a firepit does. For an EE: the engine has two disconnected "thermal buses" — one to the player, one (nonexistent in vanilla) to the room — and this interface is a connection to the player bus only.

**Standard term:** it is a real Vintage Story API interface, used with its engine meaning; the project-specific content is the *scope caveat* (entity-only) and the dual-publication convention.

**Where it appears:** `docs/vintage-story.md` §3 (Rooms and Heat, with engine file:line evidence); device design notes for heaters/freezers.

### IMechanicalPowerNode / GetTorque
The Vintage Story engine contract every node on a mechanical power network (windmill, gear, our alternator) implements; `GetTorque(tick, speed, out resistance)` is how each node tells the network what it contributes or consumes.

The convention: *sources* (windmills, rotors) return a signed torque and zero resistance; *consumers* (querns, our alternator) return zero torque and a positive `resistance` (a drag term). Every network update (~100 ms, server-authoritative), `MechanicalNetwork.updateNetwork` sums all torques and resistances across nodes — scaled by each node's `GearedRatio` — and integrates the network's shaft speed with a momentum/drag model. For an EE, this is a lumped single-shaft model: torques superpose like currents into a node, `resistance` acts like an opposing load/drag torque subtracted from the summed drive torque (the actual speed-dependent damping comes from a separate speed-squared air-resistance term the network itself adds), and the network solves one rotational dynamics equation for the shared speed. Manatee's alternator is a consumer whose `GetResistance()` returns counter-torque as a function of electrical load — re-read every update, so the electrical sim back-couples live onto the mechanical one — while the resulting `TrueSpeed` feeds electrical frequency and EMF the other way. Loose two-way coupling at mismatched tick rates, and it's stable.

**Standard term:** this is the actual VS engine interface (`Network/IMechanicalPowerNode.cs:11`), used as-is; closest textbook concept is a lumped-parameter torque-balance co-simulation interface.

**Where it appears:** `docs/vintage-story.md` §2 (Mechanical Network Coupling, R14 — "The contract"), with `vssurvivalmod` file:line references; `docs/design.md` (Mechanical co-simulation).

### IMicroblockBehavior
- The Vintage Story engine interface a mod implements to be notified whenever a chiseled (voxel-carved) block changes shape — the hook where Manatee keeps its electrical graph in sync with the geometry.

In Vintage Story, players carve blocks into arbitrary voxel shapes with a chisel; the engine represents the result as a "microblock" block entity. An *interface* is a programming contract: a named list of callbacks a class promises to provide, like a standard terminal block whose pinout the engine already knows how to wire up. `IMicroblockBehavior` (declared in `vssurvivalmod/Systems/Microblock/IMicroblockBehavior.cs`) receives `RebuildCuboidList` after every voxel edit (fan-out at `BEMicroBlock.cs:907`), plus `RegenMesh` and `RotateModel`. Manatee attaches such a behavior to the vanilla chiseled block via a JSON patch; on each `RebuildCuboidList` it rescans the voxel data (`VoxelCuboids` / `BlockIds`) for cable-material voxels and updates the conductor graph — this is what lets cable voxels coexist with chiseled stone (the "wire in the oven groove" scenario). Important asymmetry: the callback fires *after* the edit, so there is no pre-edit veto — protecting or reacting to a chiseled-away live wire happens post hoc.

This is a standard engine extension-point name (defined by Vintage Story, not by this project); the pattern is the classic Observer pattern.

**Where it appears:** `docs/vintage-story.md` §1 (BE-behavior seam, chosen architecture rep. 1); `docs/design.md` (VS integration).

**References:** Gamma, Helm, Johnson & Vlissides, *Design Patterns* (1994) — Observer; Vintage Story survival-mod source in `third_party/vssurvivalmod/`.

### immutability / versioned format
- The project's rule that persisted or cached binary data carries an explicit version number, so old or mismatched data is *recognized and rejected* (or migrated) rather than silently misread.

A versioned format prepends a small header saying "this blob was written under layout version N." Readers check the number before trusting the bytes — like a schematic's revision block: you check the rev before building from the drawing, because pin 3 may mean something different in rev B. Manatee applies this in two places. First, save data: the per-island snapshot is a versioned binary (`SnapshotVersion` = 3), and `RestoreIsland` gates *strictly* — any other version is rejected loudly, never misinterpreted; new state kinds ride as a version bump, not a silent wire-format change. Second, caches: matrix stamps are "versioned" so that an unchanged (pattern, values, dt) combination skips straight to the cached LU factorization — here the version acts as a cache-validity tag, the same idea aimed at speed instead of safety. The shared invariant is *immutability of meaning*: once bytes are written under version N, their interpretation never changes; evolution happens by minting N+1.

**Standard term:** schema/format versioning; the cache use is a validity tag or generation counter. Both are ubiquitous engineering practice rather than project inventions.

**Where it appears:** `docs/solver.md` (Change-Cost Tiers — versioned stamps; State, Limits, Probes — versioned snapshot); `docs/api.md` §14 (snapshot laws) and open question 6 (migration policy).

### In-band / in-band with atmospherics
- Code that runs *inside* a shared sequential loop, so every microsecond it takes directly lengthens that loop for everyone else sharing it.

Stationeers, it turns out (verified against decompiled game source), has no dedicated power thread: one 500 ms self-clocked loop runs all atmospherics steps, then the electricity tick, then logic/rooms, sequentially on a single thread-pool thread. Manatee's per-tick solve slots into that electricity step, so it is "in-band with atmospherics" — its cost adds to the same global 500 ms cycle that gas simulation consumes, rather than being hidden on a parallel worker. The design consequence: the solve must be small in *absolute* terms, and expensive paths (tier-3 island rebuilds, load-time rebuild-everything) are pushed *out-of-band* to a background task with a one-tick handoff. An EE analogy: in-band work is a component in series with everything else on the loop — its delay adds directly; out-of-band work is a parallel branch that doesn't lengthen the critical path. The in-band/out-of-band vocabulary is borrowed from telecommunications (in-band signaling shares the channel with the payload).

**Where it appears:** `docs/stationeers.md` (Performance → Threading, "Design consequences").

### Incremental equivalence (test)
- A standing test asserting that the incrementally-maintained compaction/island state always equals what a from-scratch rebuild would produce.
- The test drives a random (seeded) sequence of topology edits (tier-3 changes: adds, removes, merges) through the incremental-maintenance path, then rebuilds the final geometry from scratch and demands the two agree exactly — identical islands and identical solutions. It is the CI face of the in-game **resync backstop**: the same diff logic ships as a debug config that rebuilds in the background and logs discrepancies, converting incremental-state bugs from mystery corruption into bug reports. For an EE: this is like checking a running total against a full recount — the fast bookkeeping is trusted only because an independent, slower computation is continuously compared against it. In testing terms it is a differential/property test: two implementations of the same function (incremental vs. batch) must be extensionally equal on any input sequence.
- **Standard term:** differential testing / property-based equivalence testing.
- **Where it appears:** `docs/testing-strategy.md` (Equivalence Tests, "Incremental equivalence"); `docs/compaction.md` (Incremental Maintenance, resync backstop); `docs/design.md` R11.
- **References:** McKeeman (1998), "Differential Testing for Software", Digital Technical Journal 10(1); Claessen & Hughes (2000), "QuickCheck: A Lightweight Tool for Random Testing of Haskell Programs", ICFP.

### Incremental maintenance
- Keeping the compacted netlist and island bookkeeping up to date under a live stream of world edits, instead of rebuilding everything from scratch every game tick.
- Players constantly place and remove cables; rebuilding the whole reduced circuit each tick would waste nearly all its work on unchanged geometry. Instead, the compaction layer (implemented in `ConductorGraph`) tracks dirty regions at the client's natural chunk size, stages new geometry in a shadow copy, and diffs the recompaction against the realized netlist so only the changed part of the circuit is touched. Pure additions that grow an existing region take a fast path; any removal marks the island dirty and triggers one coalesced rebuild per tick at solve time. For an EE: think of it as editing only the affected rows of a large schematic rather than redrawing the sheet — with the crucial caveat that edit-in-place bookkeeping accumulates bugs, so a from-scratch rebuild always exists as a reconciliation path (the resync backstop, verified in CI by the incremental equivalence test).
- **Standard term:** incremental computation / online update (cf. incremental view maintenance in databases).
- **Where it appears:** `docs/compaction.md` (Incremental Maintenance section); `docs/design.md` R11; `docs/solver.md` (Islands).

### Incremental (maintenance/hooks)
- Updating solver and topology state *in place* when the circuit changes slightly, instead of throwing everything away and rebuilding from scratch.

When a player adds a cable or two networks merge, Manatee patches its existing island and matrix structures (cheap); only cable *removal* triggers a rebuild of the affected island, because detecting whether a removal split the network isn't worth the complexity at our island sizes (design.md R11). "Incremental hooks" are the specific game callbacks that feed these edits in — in Stationeers, `CableNetwork.Add/Remove`, `AddDevice/RemoveDevice`, `Merge`. The EE analogy: rather than re-deriving the whole system of equations when one component changes, you amend just the affected entries — like re-stamping one element in an MNA matrix instead of rebuilding the matrix. Because incrementally-maintained state can drift from ground truth, R11 pairs it with a periodic full-rebuild resync backstop (see *incremental equivalence*).

This is the standard software-engineering sense of "incremental computation"; the term is not project-coined.
- **Where it appears:** `docs/design.md` R11; `docs/stationeers.md` (Integration Seams — incremental hooks; Islands — breaker close merges incrementally, open rebuilds).

### Incremental merge
- The cheap path taken when a new connection joins two previously separate islands: their connectivity is unified in place, without re-tracing either from scratch.
- Two layers are involved, and only one is fully incremental. At the islands layer, connected components (see *Island*) are tracked with a union-find structure over the netlist, so joining two of them is a near-constant-time bookkeeping operation — this is what `docs/solver.md` means by "merge is incremental". At the compaction layer, a pre-check asks whether the added geometry would bridge more than one existing region: if it stays within a single region, the incremental recompaction path applies; if it bridges more than one region — as a two-island bridge does — the affected island's compaction is fully rebuilt. This is deliberately asymmetric with removal: cutting a connection *might* have split an island, and detecting that incrementally is not worth the complexity at our island sizes, so any removal invalidates and rebuilds the affected island instead (design decision R11). For an EE: connecting two circuits is easy to account for locally; you only need to know they are now one. Disconnecting requires re-tracing continuity to learn what remains connected — so we just re-trace.
- **Standard term:** incremental connectivity update; the merge itself is a union-find `union` operation.
- **Where it appears:** `docs/design.md` R5, R11; `docs/solver.md` (Islands); `docs/compaction.md` (Responsibilities #5, Incremental Maintenance).
- **References:** union-find (disjoint-set) data structure: Cormen, Leiserson, Rivest & Stein, *Introduction to Algorithms* (CLRS), ch. "Data Structures for Disjoint Sets"; Tarjan (1975), "Efficiency of a Good But Not Linear Set Union Algorithm", JACM 22(2).

### Incremental split detection
- Working out, without a full rebuild, whether removing a piece of geometry has cut one island into two or more — something this project deliberately does **not** do.
- Detecting a split incrementally is the hard half of dynamic connectivity: after a removal you cannot know locally whether the two sides are still joined by some other path, so a correct incremental answer requires sophisticated data structures. Design decision R11 rules that this complexity is not worth it at our island sizes: any removal simply invalidates the affected island and triggers a full rebuild of it, coalesced to at most one rebuild per tick even under a deconstruction burst. The from-scratch build is cheap at island scale, so paying it on the rare removal is simpler and safer than maintaining fragile split-tracking state. For an EE: after cutting one wire, the only sure way to know what is still connected to what is to re-trace continuity — so we re-trace, but at most once per tick.
- **Standard term:** the general problem is *dynamic (decremental) graph connectivity*; our stance is "rebuild-on-split" rather than solving it.
- **Where it appears:** `docs/design.md` R11; `docs/compaction.md` (Responsibilities #5, Incremental Maintenance); `docs/solver.md` (Islands).
- **References:** Holm, de Lichtenberg & Thorup (2001), "Poly-logarithmic deterministic fully-dynamic algorithms for connectivity...", JACM 48(4) — representative of the complexity R11 chooses to avoid.

### Incremental topology maintenance
- Requirement R11: updating the circuit's connectivity state (compaction and islands) incrementally on cable adds and network merges, while removals fall back to rebuilding the affected island.
- This is the umbrella requirement that names the whole scheme: additions and merges are the common, cheap operations and are handled in place (see *incremental merge* and *incremental maintenance*); removals rebuild, because split detection is not worth its complexity at our island sizes (see *incremental split detection*). R11 also mandates the reconciliation path — a periodic or on-demand full rebuild acting as a resync backstop with drift detection, on the SRE instinct that any incrementally-maintained state will drift and needs an authoritative recount to diff against. The scheme was proven in sparky, our earlier Vintage Story prototype, and is verified continuously by the incremental equivalence test in CI. For an EE: the topology here is which nodes and branches exist and how they connect — the netlist's graph — not component values, which change through cheaper tiers (see *change-cost tiers*).
- **Standard term:** incremental/online update of graph connectivity state; "topology" is used in the standard circuit-theory sense (the interconnection graph).
- **Where it appears:** `docs/design.md` R11 and System Overview; `docs/compaction.md` (Incremental Maintenance); `docs/solver.md` (Islands).

### independently lockable
A property of Manatee's islands: because no two islands share any mutable state, a client may lock, schedule, or tick each island on its own, on any worker thread, without coordinating with the others.

In Manatee, all solver state (matrix, factorization, element states, solution vectors) is confined to a single island. That confinement is what makes the guarantee possible: taking exclusive access to island A says nothing about island B, so a game host can hand different islands to different worker threads and never deadlock or race between them. Note the contract is *lockable*, not *locked* — manatee-core itself takes no locks; it promises single-writer-per-island (enforced by debug asserts) and leaves the actual locking/scheduling policy to the client (Re-Volt's worker thread, VS's tick loop). The EE analogy: two circuits with no wire between them cannot affect each other, so you can measure or modify one without powering down the other; islands are exactly those galvanically separate circuits, and the threading rule is the software mirror of that physical independence. Islands joined by boundary couplings are the exception — they form one scheduling unit and must be ticked together.

**Where it appears:** `docs/solver.md`, "Threading and Allocation Contract" (with the determinism and single-writer rules); island definition in the "Islands" section.

**References:** Herlihy & Shavit, *The Art of Multiprocessor Programming* (Morgan Kaufmann) — background on lock granularity and independence of disjoint data.

### Instanced rendering
- A GPU technique for drawing many copies of the same shape in one command, with each copy given its own position/rotation, instead of issuing one draw command per copy.
- Talking to the GPU has a high fixed cost per command (a "draw call"), much like the setup overhead of a lab instrument: measuring one point costs almost as much as sweeping a thousand once the sweep is programmed. Instanced rendering uploads one mesh (say, a straight wire segment) plus an array of per-instance transforms, and the GPU stamps out all copies in a single call. For Manatee this matters because a Vintage Story base can contain thousands of visually identical cable segments; Vintage Story's own mechanical-power renderer (`MechNetworkRenderer.cs:14` — one draw call per shape, per-instance transforms) is our efficiency precedent, and the vanilla cloth/rope system uses the same pattern for catenary spans. This is a rendering-performance concern only; it has no effect on the electrical solve.
- This is the standard graphics term (aliases: geometry instancing, hardware instancing; e.g. OpenGL's `glDrawElementsInstanced`).
- **Where it appears:** `docs/vintage-story.md` Wire Rendering (voxel cable meshes and catenary spans).

### Interaction state machine
- Layer 2 of the schematic tablet's three pure layers: the component that turns raw user input into edits, and the only place editing state lives.

It consumes *abstract* input events — pointer down/move/up, wheel, key — and holds every stateful part of editing: the active tool, a drag in progress, selection, hover, snapping. Its outputs are document mutations (changes to the schematic, layer 1) plus transient view state for the renderer (layer 3). Crucially it never touches a UI toolkit; the desktop harness and the in-game Vintage Story tablet each adapt their native input into the same abstract events. For an EE: think of it as the control logic of a panel — the buttons and knobs (any brand) feed a single well-defined controller, and only that controller decides what the machine does next; because it is isolated, it can be exercised on the bench (headless tests) without the front panel attached. "State machine" here is the standard computer-science sense: a component whose behavior depends on an explicit current state plus the incoming event.

**Standard term:** finite state machine / input controller (cf. the controller role in Model–View–Controller).
- **Where it appears:** `docs/harness.md` (Layering #2, Testing Model); the `Manatee.Schematic` library.
- **References:** Gamma, Helm, Johnson & Vlissides, *Design Patterns* (State pattern); Hopcroft, Motwani & Ullman, *Introduction to Automata Theory, Languages, and Computation* (finite state machines).

### Interaction tests
- Automated tests that simulate a user editing a schematic by feeding scripted fake input events to the interaction state machine and checking what changed.

Each test is a script of synthetic events — e.g. "pointer down on the resistor palette, drag to (140, 60), release" — followed by assertions on the outcome: a resistor now exists in the document, and an undo entry was recorded. Because the interaction state machine takes abstract events and touches no UI framework, these tests run headless (no window, no GPU), are fully deterministic, and run in CI; they carry the tablet's behavioral coverage. For an EE: this is the software equivalent of driving a device-under-test with a signal generator instead of a human on the front panel — same stimulus every run, so any change in response is a real regression. The docs call this the "Playwright-like answer without a browser": the same style of end-to-end UI scripting popular in web testing, but against pure code.

**Standard term:** headless UI / end-to-end interaction testing (cf. Playwright, Selenium-style scripted tests).
- **Where it appears:** `docs/harness.md` (Testing Model), alongside display-list snapshots and pixel snapshots.
- **References:** Meszaros, *xUnit Test Patterns: Refactoring Test Code* (scripted, deterministic test design).

### invariant
A property that must hold at all times, no matter what the system does — and which the test suite therefore checks constantly rather than only in specific scenarios.

In programming, an invariant is a standing promise: "this condition is true before and after every operation." Tests built on invariants do not check particular expected outputs; they check that the promise was never broken, which catches whole classes of bugs without anyone predicting them. For an EE, the closest analogy is a conservation law: you may not know what every branch current is, but you know Kirchhoff's laws must hold at every node, always — an invariant is that idea applied to software state. Manatee's flagship example is the compaction layer's invariant that reduction is *semantically invisible*: a circuit solved raw and the same circuit solved after series-collapse must produce identical terminal behavior, and probes into collapsed interiors must agree with the raw answer. This is enforced by standing equivalence tests, so the reduction layer can be aggressive without anyone trusting it on faith.

This is a standard software-engineering term (as in loop invariants, class invariants, design-by-contract).

**Where it appears:** `docs/compaction.md` (Invariants), `docs/solver.md` throughout, `docs/design.md` (Testing and Validation), `docs/integration-tutorial.md` §8–9.

**References:** Cormen, Leiserson, Rivest & Stein, *Introduction to Algorithms* (loop invariants); Meyer, *Object-Oriented Software Construction* (class invariants, design by contract).

### invariant / invariant check / CheckInvariants / InvariantReport / standing invariant test
Manatee's machine-checkable circuit truths — conservation laws and safety properties the solver verifies mechanically after every solve, always on in debug builds, assertable in any test, and exposed as a public API call. (For the general *invariant* concept and the compaction reduction-equivalence invariant, see the entry above.)

The checks are physical truths a correct solver can never violate, so any failure is a bug by definition: **KCL residual** (currents into every node sum to ~0 — Kirchhoff's current law; catches component-stamp bugs immediately), **energy audit** (over any transient window, source energy = dissipated + Δstored within integration error — catches sign errors and companion-model state bugs), **coupling conservation** (island boundaries and the mech↔elec coupling never create free energy; sharpened to windowed energy *ledgers* under oscillating loads), **finiteness** (no NaN/Inf ever appears in a solution vector), and **coupling dissipativity** (a closed motor→shaft→alternator→motor loop, given any initial spin, must monotonically lose energy). For a programmer these are runtime assertions derived from physics rather than a spec, like array-bounds checks whose asserted property is a conservation law; for an EE they are Kirchhoff's laws and conservation of energy wired up as automatic pass/fail meters on every computed answer.

Three of them (KCL, Finiteness, Energy) are exposed as public API (`docs/api.md` §11): `CheckInvariants(InvariantChecks which)` runs the selected checks — the flags enum is exactly `Kcl`, `Finiteness`, `Energy` — and returns an `InvariantReport` with `MaxKclResidual` and `WorstKclNode` ("which node leaks current"), `AllFinite`/`FirstNonFiniteRow`, and `EnergyResidual` (source − dissipated − Δstored). The deliberate design point is **one code path, three consumers**: CI assertions, the resync backstop that verifies incremental state against a from-scratch rebuild, and the tablet's educational error messages ("current doesn't balance at this node") all call the same function, so the check that teaches a player is the same check that gates a release. The call is 0-byte (allocation-free) after warmup, cheap enough to use liberally. The coupling conservation/dissipativity invariants are *not* in that flag set; they are enforced by property tests and debug-build asserts (`docs/testing-strategy.md`) — the flagship being the motor→shaft→alternator loop-decay test, which pinpoints the counter-torque averaging window guarding against 2f torque aliasing (both integrators are individually dissipative, so any energy *gain* implicates the coupling code). Unlike an example-based test ("this circuit gives 5 V"), an invariant test constrains *every* behavior at once. These checks are central to the project's stance that "the math is untrusted input": since no one hand-verifies derivations, correctness rests on oracles (ngspice) plus these invariants.

**Standard concepts** — SPICE-family simulators use the same residual and conservation diagnostics internally (KCL residual, Tellegen/energy balance); the always-on-assertion packaging follows defensive-programming practice, and "invariants as public readback" was adopted from the API competition's Proposal 3. The loop-decay property is what an EE calls **passivity/dissipativity**.

**Where it appears:** `docs/api.md` §11 (`CheckInvariants`, `InvariantChecks`, `InvariantReport`, `EnergyAudit`); `docs/testing-strategy.md` (Invariant Checks, "coupling dissipativity"); `docs/design.md` (Testing and Validation); `docs/vintage-story.md` (Mechanical Network Coupling, averaging-window paragraph); `docs/integration-tutorial.md` ("Invariants as a debugging tool").

**References:** Vlach & Singhal, *Computer Methods for Circuit Analysis and Design* (KCL and network equations); Penfield, Spence & Duinker, *Tellegen's Theorem and Electrical Networks*, MIT Press (1970) (energy balance as a topology-only law); Pillage, Rohrer & Visweswariah, *Electronic Circuit and System Simulation Methods* (residuals and convergence checking); the ngspice manual.

### InvariantCulture
A .NET setting that fixes number formatting to one universal convention — decimal point `.`, no thousands separators — regardless of the language/region settings of the machine the code runs on.

By default, .NET parses and prints numbers according to the operating system's regional settings ("culture"): on a German or Norwegian machine, `3.3` might print as `3,3`, and parsing `3.3` could silently fail or give the wrong value. `CultureInfo.InvariantCulture` opts out of all that: numbers are read and written in one fixed, locale-independent format. Manatee's Falstad importer mandates it when parsing doubles, so a circuit file authored anywhere on Earth parses identically everywhere — the numeric equivalent of agreeing that all schematics use SI units, so a resistor value never changes meaning when the drawing crosses a border. It is unrelated to the "invariants" of the testing strategy; the shared word is a coincidence.

Standard .NET term (`System.Globalization.CultureInfo.InvariantCulture`).

**Where it appears:** `docs/falstad-format.md` §7 (importer accept/reject contract: "Parse doubles with InvariantCulture").

**References:** Microsoft .NET documentation, `System.Globalization.CultureInfo.InvariantCulture`.

### IRenderer
A Vintage Story engine interface (a plug-in contract) that lets a mod draw its own custom 3D geometry each frame, outside the engine's normal block-drawing pipeline.

In C#, an interface named with a leading `I` is a contract: any class implementing `IRenderer` promises a method the engine will call every frame, at a stage the implementer chooses, to issue its own draw calls. It is how a mod says "I will paint this part of the world myself." Manatee plans to use its own `IRenderer` to draw long wire spans as static **catenary** meshes — the natural sagging-chain curve a real conductor hangs in — stretched between the two endpoint block entities, borrowing segment-transform math from the engine's rope/cloth system but without live physics (no per-frame mass-spring simulation: too costly, and wires should not wobble or rip). Short spans render as ordinary voxel geometry instead; the wire-voxel-count rule in design.md selects which representation each span gets. For an EE: think of the engine as a chart recorder that normally plots only standard traces, and `IRenderer` as the socket where you attach your own pen.

Standard Vintage Story modding API term (`Vintagestory.API.Client.IRenderer`); the `I*` naming is the standard .NET interface convention.

**Where it appears:** `docs/vintage-story.md` (Wire Rendering: catenary spans between posts).

### island rebuild coalescing
Batching many rebuild-triggering edits in the same tick so the expensive rebuild work runs once, not once per edit.

Removals do not rebuild immediately; they merely mark the affected island *dirty*. The rebuild itself is deferred to solve time and runs at most once per tick per island, so a deconstruction burst — a player tearing out a whole cable run — costs one rebuild instead of one per removed segment. This is the same idea as debouncing or write coalescing in software (collect a burst of invalidations, act once), or — in EE terms — like a sample-and-hold: the circuit description absorbs edits continuously, but the solver only re-derives its matrices at the tick boundary. Importantly, the *event stream* is not coalesced away: every `IslandId` that ceases to exist is still reported through `DrainChanges`, even when the underlying matrix work was merged into a single rebuild.

**Standard term:** batching / debouncing of invalidations (deferred, once-per-frame recomputation — the common "dirty flag" pattern in game engines).

**Where it appears:** `docs/design.md` R4 ("topology changes — full rebuild, batched"), `docs/compaction.md` Incremental Maintenance ("coalesced ... at most once per tick, at solve time"), `docs/solver.md` change-cost table.

### jj-aware @ vs @-
- The benchmark comparison mode in `scripts/bench.sh` that measures the current change against its immediate parent, using the Jujutsu version-control system's names for those two revisions.

Jujutsu (jj) is the version-control tool this repo uses; in its revision syntax, `@` means "the change I am working on right now" and `@-` means "its parent" — for the EE, think of comparing a device-under-test against the same board before the modification. `bench.sh compare` runs the BenchmarkDotNet suite on `@`, uses `jj new @-` to materialize the parent's code in the working copy, benchmarks that, restores the original state with `jj undo` (guarded by a shell trap so an aborted run still restores), then diffs the two result CSVs on the Mean (time) and Allocated (memory) columns and prints the deltas to stdout as a table — an ephemeral report, not something persisted to history. "jj-aware" distinguishes it from a naive git-stash workflow: the script speaks jj's model of the working copy directly. Its purpose is regression detection — every performance-relevant change can get an A/B measurement against exactly its parent.
- **Project-coined term** (the script mode's name), built from standard jj revset syntax: `@` and `@-` are standard Jujutsu notation, analogous to git's `HEAD` and `HEAD~1`.
- **Where it appears:** `docs/testing-strategy.md` (Benchmarks); implemented in `scripts/bench.sh` (`compare` mode).

### Job System / Task.Run
- Two off-the-shelf ways to run work in parallel — Unity's Job System and .NET's `Task.Run` — which Manatee's Stationeers integration explicitly does *not* use, because the game's simulation code uses neither.

The Unity Job System is the game engine's built-in framework for scheduling small units of work across CPU cores; `Task.Run` is the standard .NET call that hands a function to a thread pool to execute in the background. For the EE: both are like standard plug-in modules for "do this on another processor" — you would normally reach for one rather than wiring your own. Reading the Stationeers decompilation showed that the game's simulation path contains neither: the house style is UniTask (a third-party Unity library), with hops like `UniTask.SwitchToThreadPool()` to move work off the main thread and `UniTask.SwitchToMainThread()` to marshal results back. The doc's point is a match-the-host rule: when Manatee offloads expensive work (tier-3 island rebuilds, load-time rebuilds) to the background, it must use the game's own UniTask idioms — there is no existing Job System or `Task.Run` machinery in the sim path to piggyback on. This mirrors an EE instinct: use the bus the board already has, don't bolt on a second incompatible one.
- **Standard terms:** both are standard (Unity C# Job System; .NET Task Parallel Library's `Task.Run`); the project-specific content is the verified *absence* of both from Stationeers' sim path and the resulting UniTask requirement.
- **Where it appears:** `docs/stationeers.md` (Performance / threading model, with decompile file:line evidence, e.g. `PowerTick.cs:59-70`, `Device.cs:1333`).

### JSON patch
- A small data file that declares edits to another mod's or the base game's JSON configuration, applied by the Vintage Story engine at load time — modification without touching engine or vanilla code.

Vintage Story defines its blocks in JSON files, and the engine ships a patching system: a mod can say "in file X, at path Y, add/replace/remove value Z," and the engine applies those edits when assets load. Manatee uses this (R17) to append its `IMicroblockBehavior` to the vanilla `chiseledblock` definition's `entityBehaviors` list, so every chiseled block gains electrical semantics — the same technique vanilla's snow-cover behavior demonstrates. For EEs: it is like a factory-sanctioned ECO (engineering change order) sheet — instead of re-fabricating the board, you file a small documented delta ("add this component at this location") that the assembly line applies automatically, and removing the sheet restores the original.

**Standard term:** the general concept matches IETF RFC 6902 "JavaScript Object Notation (JSON) Patch"; Vintage Story's implementation is its own dialect of the same idea, applied to game asset files.

**Where it appears:** `docs/design.md` R17; `docs/vintage-story.md` §1 (BE-behavior seam, Chosen architecture).

### Lesson corpus integration (tests)
- The end-to-end tier of the 2D harness test suite: scripted scenarios that drive whole lessons — build the circuit, edit it, save and reload it, introduce a fault — headlessly in CI, checking the full pipeline from schematic to solver to meter readback.
- Where the corpus goldens (see *lesson corpus as goldens*) verify that a lesson's *circuit* solves correctly, these tests verify that the *tablet software around it* behaves: netlist extraction from the schematic document, solving in manatee-core, and probe readback into meters and scope traces, exercised together rather than layer by layer. "Headless" means no window or graphics hardware is involved — the harness's three pure layers (document model, interaction state machine, display-list view) accept synthetic input events and emit inspectable data, so a full editing session runs as an ordinary automated test. The docs call this the "Playwright-like answer without a browser": for an EE, the analogy is automated production test on the assembled instrument — pressing its buttons via a fixture and reading its display electrically — as opposed to bench-testing each board alone.
- **Standard term:** integration testing (a.k.a. end-to-end testing), applied to the lesson corpus as its scenario source.
- **Where it appears:** `docs/harness.md` (Testing Model, final bullet), backed by the layering rules earlier in the same doc and by `docs/testing-strategy.md`.
- **References:** G. J. Myers, "The Art of Software Testing", Wiley, for the unit/integration testing distinction.

### Main thread / server main thread
- The single thread on which a game server runs its core logic; any solver read the game performs from there must be safe and cheap, because everything else in the game is waiting behind it.

Game engines like Vintage Story process world updates on one designated thread; code touching game state generally must run there, and anything slow on it stalls the whole server (an EE analogy: it is the one shared bus every subsystem must arbitrate for — hog it and the entire system's cycle time suffers). This constrains Manatee two ways. First, R8's thread-purity rule: the solver core is pure C# with no game-engine types, so it *can* run off the main thread with well-defined handoff points. Second, some reads are demanded *on* the main thread at arbitrary times — VS's mechanical network calls `GetResistance()` out of band (`BEBehaviorWindmillRotor.cs:197`) — so those must be pure reads of a cached value (specifically the mean over electrical substeps since the last mechanical read, per the normative averaging window), never a computation or a lock that could block or observe a half-updated solve. Stationeers has the analogous notion: its `ThreadedManager.IsThread` distinguishes worker threads from the main thread, and results are marshalled back to it.
- Standard term across game and UI programming; related aliases: "game thread", "UI thread", "main loop".
- **Where it appears:** `docs/vintage-story.md` §2 averaging window; `docs/stationeers.md` threading model; `docs/design.md` R8.

### map (back-map)
- A lookup table produced by the compaction (reduction) layer alongside the minimal netlist, recording how each element of the reduced circuit corresponds to the original game geometry.

Compaction collapses thousands of voxels or cable segments into a netlist of a few hundred nodes; the solver then only knows about the reduced circuit. The back-maps are what let results flow the other way: a probe placed on an eliminated interior voxel is answered by interpolating along the collapsed chain it belonged to, and a limit event (say, an overcurrent trip) on a collapsed equivalent resistor is attributed back to the specific physical segment that should melt. In programming terms this is an ordinary mapping table or index — a dictionary from reduced-circuit identity to original geometry. The EE analogy: after you replace a resistor ladder with its Thévenin equivalent for analysis, the back-map is the bookkeeping that tells you which physical resistor inside the original ladder your computed stress actually lands on.

**Standard term:** mapping table / index; the surrounding technique is standard netlist reduction with bookkeeping for de-reduction of results.

**Where it appears:** `docs/compaction.md` intro ("a minimal netlist plus the maps needed to route observations and events back to geometry") and its Probe Interpolation and Limit Attribution responsibilities; as-built contract in `docs/api.md` §19.

### Mono
- An alternative open-source implementation of the .NET runtime (the virtual machine that executes C# programs), historically used where Microsoft's official runtime wasn't available — including inside many game engines.

A C# program doesn't run directly on the hardware; it runs on a runtime that provides memory management, and different runtimes implement the diagnostic APIs to different fidelity. Manatee's zero-allocation CI gates work by reading `GC.GetAllocatedBytesForCurrentThread` — a per-thread odometer of heap memory allocated — before and after a solver step and asserting the delta is zero. On Mono that odometer has historically been inert (it always reads the same value), so the assertion would vacuously pass and prove nothing. The test suite therefore runs a capability probe (`ZeroAllocGates.CounterIsReliable`) at startup and **auto-skips the zero-alloc gates on any runtime where the counter is dead**, rather than reporting a false green. For an EE: it's like an ammeter model whose needle is glued at zero — a "no current" reading from that meter is meaningless, so the test procedure checks the meter first and marks the measurement N/A if it fails.

This matters for Manatee because game hosts don't guarantee a modern runtime; the gates must degrade explicitly, not silently.

**Where it appears:** `docs/testing-strategy.md`, Benchmarks section (flake isolation for the zero-alloc gate) and the §8 capability probe.

### monotonic Seq
- The strictly increasing serial number stamped on every event in the TopologyJournal, so that readers can tell exactly where they are in the event stream and detect when they have fallen too far behind.

The TopologyJournal is a fixed-capacity ring buffer of topology-change events (component added, islands merged, …) that derived layers replay to stay in sync with the netlist. Each `TopologyEvent` carries a `long Seq` that only ever counts up and is never reused — a global odometer, not a slot index. That gives two guarantees: a cursor can be positioned at an exact historical point (`OpenCursorAt(seq)`, the way an `EditReceipt`'s event window is replayed), and **lapping is detectable** — because the ring has finite capacity, new events eventually overwrite old ones, and a reader whose cursor Seq predates the oldest surviving event knows its history is gone (`Overflowed()` returns true) and must do a full resync rather than silently miss events. For an EE: like sequentially numbered pages on a strip-chart recorder with a finite paper loop — a gap in the numbering is proof you missed data, prompting a fresh full reading rather than trusting the chart.

This is a standard technique; the same idea appears as *log sequence numbers (LSNs)* in database write-ahead logs and as sequence numbers in replication streams.

**Where it appears:** `docs/api.md` §15 (`TopologyJournal`, `TopologyEvent.Seq`, `OpenCursorAt`, `Overflowed`).

**References:** log sequence numbers and log-based recovery: Mohan et al. (1992), "ARIES: A Transaction Recovery Method...", ACM TODS; also covered in Ramakrishnan & Gehrke, *Database Management Systems*.

### Multitarget (netstandard2.1 / net8.0)
- Compiling the same manatee-core source code twice, once for each of two .NET "target frameworks", so a single codebase can be loaded by two very different host environments.

A .NET target framework is the contract between compiled code and its runtime — roughly, which standard library and language features the code is allowed to assume. `Manatee.Core` targets both `netstandard2.1` and `net8.0`: the netstandard2.1 build exists because Stationeers' Re-Volt mod runs inside Unity 2022.3, whose runtime can only load libraries built against that older, narrower contract; the net8.0 build serves Vintage Story mods and all dev-side projects (tests, benchmarks, harness) with the modern runtime. An EE analogy: it is like manufacturing one circuit design in two package footprints so it drops into two different sockets — same schematic, two physical fits. The build system produces both from one project file; there are not two codebases to keep in sync.

This is standard .NET practice; the common alias is "multi-targeting" (`<TargetFrameworks>` in the project file).

**Where it appears:** `docs/testing-strategy.md` (Toolchain section, line ~50); `Manatee.Core.csproj`.

**References:** Microsoft, ".NET Standard" and "Target frameworks in SDK-style projects" documentation (learn.microsoft.com).

### Mutator
- Any netlist API method that changes the circuit's state, as opposed to methods that merely read it out.

In Manatee the mutators are the netlist's runtime verbs — `Drive` (tier 1, source values), `Adjust` (tier 2, conductance: resistors, switches, capacitors, inductors, diodes, transformers), `Reconfigure` (tier 3-lite, coupler membership) — plus structural edits made through the batch scope returned by `Edit()` (`AddResistor`, `AddVoltageSource`, `Remove`, etc., tier 3): everything that edits the circuit the solver will next be asked to solve. They stand in contrast to read-only accessors such as `Solution.Voltage`, `Solution.Current`, and `Solution.Power`, which are like meter probes: touching them never changes the circuit. The distinction matters here because every mutator carries a change-cost tier (tier 1 = source value, cheap; tier 2 = conductance change; tier 3 = topology change, expensive and batched inside the `Edit()` scope), so callers can see the performance price of an edit at the call site. For an EE: a mutator is turning a knob, throwing a switch, or resoldering the board; an accessor is reading a meter.

This is standard programming vocabulary (aliases: "setter", "command" in command/query separation); the tier tagging is the project-specific part.

**Where it appears:** `docs/api.md` — the binding as-built surface (§4 the four verbs, §6 StructuralEdit, §10 Solution); `core/Manatee.Core/Netlist.cs`, `StructuralEdit.cs`, `Solution.cs`.

**References:** Bertrand Meyer, *Object-Oriented Software Construction* (command–query separation).

### NaN propagation
- The failure mode where a single non-finite value (NaN) spreads through subsequent arithmetic until the whole solution is meaningless — contractually forbidden from ever leaving Manatee's API.

Because nearly any operation involving a NaN produces another NaN, one division-by-zero deep inside a solve can turn every node voltage in the answer into NaN a few steps later — the numerical equivalent of a short at one node dragging an entire unfused bus down, except silently: nothing crashes, the numbers are simply garbage. Requirement R9 ("legible failure") mandates that degenerate circuits — floating nodes, contradictory sources, non-convergence — produce diagnosable errors and events instead. Concretely, per `docs/solver.md` Failure Handling: a singular or non-convergent island enters a `Faulted` state with a named diagnostic, the previous good solution is held, and debug builds assert the moment any non-finite value appears anywhere in the solution vector, so propagation is caught at the source rather than observed at the API boundary. The justification is gameplay: players build pathological circuits as a hobby, and "why doesn't this work" must be answerable in-game.

Standard numerics/programming vocabulary (also called "NaN poisoning"); the hard API-boundary contract is the project-specific part.

**Where it appears:** `docs/design.md` R9; `docs/solver.md` (Failure Handling); enforced by debug asserts in `Manatee.Core`.

**References:** IEEE Std 754-2019 (NaN propagation semantics); David Goldberg, "What Every Computer Scientist Should Know About Floating-Point Arithmetic", ACM Computing Surveys 23(1), 1991.

### Net channel / telemetry broadcast
A named server-to-client network message stream in Vintage Story, used here to sync live electrical readings (volts, amps, temperature) to players' screens at a deliberately slow, throttled rate.

In a multiplayer game the simulation runs on the server, but tooltips render on each client, so displayed values must be shipped over the network ("net" here means *network*, not electrical net). Doing that via the engine's per-block dirty mechanism every tick would re-send whole chunk data constantly; instead the engine lets mods register custom channels — independent message streams identified by name. The precedent is the vanilla mechanical-power mod's telemetry broadcast every ~800 ms (`MechanicalPowerMod.cs:218`). Manatee plans an `mna-telemetry` channel broadcasting per-island probe values every ~0.5–1 s (config-scalable), with the oscilloscope item separately negotiating a temporary high-rate waveform stream for a single probe point. For an EE: it is a low-bandwidth telemetry link from the plant to remote gauges — panel meters update a couple of times a second, and only the scope gets a dedicated fast channel, one signal at a time.

Standard networking concepts: publish/subscribe channel; throttling / rate limiting.

**Where it appears:** `docs/vintage-story.md` §4 (Tooltips and Instruments, R15).

### Net (netlist id)
A small process-wide identifier stamped into every solver handle that records *which* Netlist (circuit description) the handle belongs to.

Every handle in manatee-core is a 12-byte triple `(int Slot, uint Gen, ushort Net)`: `Slot` is the array index, `Gen` is a generation counter that detects reuse of that index, and `Net` is a unique-per-process id of the owning Netlist. The `Net` field exists so that presenting a handle to the *wrong* Netlist is caught by comparing values, not by the accident of whether that slot number happens to be occupied there — without it, a handle from circuit A could silently read circuit B's component that landed in the same slot. Detection fails fast: an exception in debug builds, a defined sentinel plus a diagnostic counter in release. For an EE: this is like serial-numbering every probe lead with the instrument it belongs to, so plugging a lead into a different instrument is rejected outright instead of quietly measuring the wrong channel.

**Caution:** despite the name, this `Net` is *not* an electrical net/node — it identifies the whole netlist object. The standard concepts are the generational-index / slot-map pattern (common in game engines and entity-component systems) extended with an owner tag.

**Where it appears:** `docs/api.md` §3 (Handles and keys) and §24.6 (decision log: Gen widened to `uint`, `Net` embeds the netlist id).

### net8.0
The "target framework moniker" telling the C# build tools that an assembly is compiled for .NET 8, the 2023 version of Microsoft's .NET runtime and standard library.

A .NET project declares one or more targets like `net8.0`; the compiler then allows only the language features and library APIs that runtime provides, and the output runs anywhere .NET 8 (or later) is installed. In Manatee it identifies the platform of `Manatee.Schematic` — the assembly holding the three pure schematic layers (document model, interaction state machine, display-list view) shared by the desktop harness and, later, the Vintage Story tablet. For an EE: a target framework is like the supply-voltage and pinout rating on a part — it declares which environment the compiled code is guaranteed to plug into, and mixing incompatible ratings fails at assembly time rather than in the field. (Not to be confused with electrical "net": this `net` abbreviates .NET, the platform name.)

Standard term: .NET target framework moniker (TFM), per Microsoft's .NET documentation.

**Where it appears:** `docs/harness.md` (Layering section); the `Manatee.Schematic` project file.

### netstandard2.1 / net8.0 / Mono / CoreCLR / Unity
- The set of .NET "runtimes" manatee-core must run on: the same compiled library ships to two very different execution environments, and several project rules exist only because of that.

For an EE: think of a runtime as the substrate a program executes on — the same schematic (source code) fabricated on two different processes with different tolerances. **netstandard2.1** is a compatibility ceiling (an API contract, not a runtime): code targeting it runs anywhere that implements at least that surface. That ceiling is forced by **Unity 2022.3**, the game engine Stationeers is built on, whose scripting runtime is **Mono** — an older, less optimizing .NET implementation. **net8.0 / CoreCLR** is the modern .NET runtime used for CI, tests, and benchmarks. Manatee-core multitargets both (`netstandard2.1;net8.0`) and is written to the weaker runtime's behavior: Mono's JIT may not eliminate dead code the way CoreCLR does (hence the binding constrained-callvirt rule for generic readbacks, api.md §3 and decision log #7), and Mono's per-thread allocation counter is historically inert or unreliable (hence the in-process allocation sentinel is best-effort with a self-disarming capability probe, while BenchmarkDotNet MemoryDiagnoser on CoreCLR is the *binding* zero-allocation gate — §8, decision log #10).

These are all standard industry terms (aliases: ".NET Standard 2.1", ".NET 8", "the Mono runtime", "the CoreCLR runtime", "the Unity engine").

**Where it appears:** `docs/api.md` §1 (ground rules), §3 (constrained-callvirt rule), §8 (tripwire reliability), §24 decision log #7 and #10; also §23.2 (on-device Mono verification is an open item).

**References:** ECMA-335, *Common Language Infrastructure (CLI)*, the standard both runtimes implement.

### ngspice
- The mature open-source SPICE circuit simulator that Manatee uses as its correctness oracle: an independent implementation whose answers our solver must match.

Because the project's working agreement treats Claude-authored circuit math as untrusted input, correctness is established by comparison rather than review: a test-only translator emits SPICE decks from manatee netlists, ngspice runs them (`.op` analysis for DC, `.tran` for transient), and node voltages and branch currents are diffed within stated tolerances (DC 0.1% relative or 1 µV absolute near zero; transient looser, because Backward Euler and ngspice's default integrator legitimately differ). Every component type, every lesson in the tablet curriculum, and a nightly sample of randomized circuits go through this gate. For the EE: ngspice is the direct descendant of Berkeley SPICE, the industry-standard reference simulator. For the programmer: it is a golden-master test against an independent implementation — like validating a new database engine by running the same queries against SQLite. It is a dev/CI dependency only, never shipped with any mod, and the oracle tests hard-fail (rather than skip) when it is missing, because a silently skipped oracle is the one failure mode the strategy cannot tolerate.

**Where it appears:** `docs/testing-strategy.md` (Toolchain; Oracle Tests), `docs/design.md` (R20; Testing), `docs/falstad-format.md` §7 (oracle note on sign conventions), `CLAUDE.md` (devshell pins it; `Category!=Oracle` fast loop).

**References:** the ngspice manual (ngspice.sourceforge.io); Nagel (1975), *SPICE2: A Computer Program to Simulate Semiconductor Circuits*, UCB/ERL-M520 (the ancestor program).

### on-demand / computed on demand
- A software pattern in which a value is calculated fresh at the moment somebody asks for it, rather than stored somewhere and kept up to date.
- The alternative is *stateful*: store the value and run update logic whenever an input changes — which is faster to read but creates a synchronization burden (stale data, save/load of the state, ordering bugs). Vintage Story's engine consistently chooses on-demand for environmental values: room temperature is never stored anywhere; whenever spoilage math needs it, `InWorldContainer.GetPerishRate()` recomputes it from world climate, skylight, cooling walls, and cellar constants (vintage-story.md sec 3 — "rooms have NO temperature state"). Likewise the beehive computes its `roomness` greenhouse bonus (+5 °C) on demand at query time. Manatee's heater plan follows the same convention: devices publish dissipation into a mod-owned heat registry, and consumers *query and fold it in* when they need a perish/comfort number, instead of the mod trying to maintain a persistent per-room temperature that vanilla has no slot for. EE analogy: it is the difference between reading a live meter whenever you need the value (on-demand) versus logging to a chart recorder and trusting the log stayed synchronized with reality (stored state).
- **Standard term:** computed/derived on demand; closely related standard notions are *lazy evaluation* and derived (non-materialized) values, as opposed to cached/materialized state.
- **Where it appears:** vintage-story.md sec 3 "Rooms and Heat" (perish rate, beehive `roomness`, and the heat-registry plan).
- **References:** the general derived-vs-stored-state trade-off is textbook software design; see e.g. Kleppmann, *Designing Data-Intensive Applications* (materialized views vs recomputation).

### on-thread / off-thread / off-band
- Labels for *where* a piece of solver work runs: inline in the game's simulation tick ("on-thread", also "in-band"), or handed to a background task that runs concurrently ("off-thread" / "off-band").
- The game advances in fixed ticks — Stationeers' power/atmospherics cycle is 500 ms, our Vintage Story tick is 50 ms. Work done on-thread sits directly inside that cycle, so every microsecond it takes delays everything else in the tick; work done off-band runs on a separate worker in parallel and only its *result* is merged back in on a later tick (see **one-tick handoff**). The EE analogy: on-thread work is a series element — its delay adds directly to the loop — while off-band work is a parallel path whose output is sampled at the next clock edge. The project's budgets are stated per lane (design.md: ≤ 5 ms on-thread, ≤ 20 ms off-thread for VS), and cheap steady-state solves stay on-thread while expensive island rebuilds go off-band. Crucially, "off-band" does not license mutating shared solver state from the background: api.md §21's compute/commit split requires all global mutations to commit back on the sim thread.
- **Standard term:** synchronous vs. asynchronous / background (worker-thread) execution; "in-band/out-of-band" is borrowed from communications and systems jargon.
- **Where it appears:** stationeers.md Performance ("in-band"/"off-band"), design.md Performance Targets, api.md §21 (off-thread tier-3 compute/commit split) and §9 (off-band scheduler using `CostOfAdjust`/`CostOfReconfigure`).

### one-tick handoff
- The offload pattern where an expensive solver rebuild runs in the background while the game keeps ticking on stale-but-valid data, and the fresh result is adopted at the start of the next tick.
- When a tier-3 event (island rebuild, load-time rebuild-everything) would blow the tick budget, its inputs are snapshotted under lock, the heavy computation (symbolic analysis, factorization) runs off-band, and until it finishes the affected network keeps using the previous solution — or Re-Volt's scalar fallback if there is no previous solution yet (`Solution.IsLive == false`). The completed result is then committed on the sim thread in the next tick's topology phase; no global solver structure is ever mutated from the background (api.md §21's compute/commit split). Players see at most one tick of slightly stale electrical values instead of a stall. The EE analogy is a sample-and-hold: the output holds the last valid sample while the converter works on the next one, and the new value appears at the next clock edge.
- **Standard term:** double-buffered / pipelined asynchronous update (compute in background, swap at a synchronization point); a one-frame-latency background job in game-engine terms.
- **Where it appears:** stationeers.md Performance (design consequences), api.md §21 (off-thread tier-3 compute/commit split).
- **References:** for the general pattern of decoupling computation from presentation via buffered state, see Nystrom, "Game Programming Patterns" (Double Buffer).

### oracle
- A trusted external authority whose answers are treated as ground truth when checking that our code produced the right result.

The hard part of testing a simulator is knowing what the *correct* answer even is; an oracle solves this by delegating the question to something already known to be right. In Manatee the oracle is ngspice, a mature open-source SPICE simulator: our solver and ngspice are handed the same circuit, and the test fails if they disagree beyond tolerance. This is the load-bearing beam of the whole testing strategy, because nobody on the team is an electrical engineer — correctness rests on oracles, invariants, and equivalences rather than on "we derived the math right." For an EE, an oracle is like checking a home-built instrument against a calibrated reference meter: you don't re-derive the meter's internals, you just require agreement. The policy has teeth: tests tagged `Category=Oracle` hard-fail (rather than silently skip) when ngspice is missing.

**Standard term:** test oracle (standard software-testing vocabulary).
- **Where it appears:** `docs/testing-strategy.md` (Principles; Oracle Tests (ngspice)); the `Oracle/` suite in `core/Manatee.Core.Tests`.
- **References:** Weyuker (1982), "On Testing Non-Testable Programs", *The Computer Journal* 25(4) — the classic treatment of the oracle problem; ngspice manual (the oracle itself).

### Out-of-band call
- A method invocation that arrives outside the normal, scheduled update rhythm of the system — at an unpredictable moment rather than on the regular tick.

Game engines usually update everything on a fixed cadence (Vintage Story's mechanical network re-sums torque roughly every 100 ms), and it is tempting to write code that assumes it will only ever be called at those moments. But the engine also invokes some methods at other times — for example, the windmill triggers an extra `MechanicalNetwork.updateNetwork` from its own listener (`BEBehaviorWindmillRotor.cs:197`), which in turn re-reads every node's `GetResistance()`. For an EE, the analogy is an asynchronous interrupt or an unsynchronized sample: a measurement taken at an arbitrary phase of the cycle, not at the clock edge you designed for. Manatee's rule is therefore that `GetResistance()` must be a pure read of a pre-computed cached value (the averaged counter-torque), valid at any time on the server main thread, doing no computation and no state mutation — so an out-of-band call returns a correct answer instead of a half-updated or phase-aliased one.

This is a standard software term; related standard notions are "asynchronous call" and re-entrancy safety. ("Out-of-band" originates in communications, meaning data sent outside the main channel.)

**Where it appears:** `docs/vintage-story.md`, Mechanical Network Coupling — the normative averaging-window rule for the alternator's `GetResistance()`.

### P/Invoke
- Platform Invocation Services: the .NET mechanism that lets C# code call functions in native (typically C) libraries.

C# normally runs inside the .NET runtime, insulated from the operating system; P/Invoke is the escape hatch that declares a C function's signature in C# and calls into a compiled native library (`.dll`/`.so`/`.dylib`). For an EE, it is like wiring an off-the-shelf ASIC into a board you otherwise built from your own logic: you gain a proven high-performance block, but now you must stock the right part for every board variant. That variant problem is exactly why Manatee rejected it: using the battle-tested native KLU/SuiteSparse sparse-LU solver via P/Invoke would require shipping a separate native binary per platform (Windows/Linux/macOS, per architecture) inside game mods, a distribution and support burden mod users would bear. Instead Manatee's in-house pure-C# KLU-style sparse LU is the sole production backend — which the backend-competition benchmarks showed also wins on the hot path.

This is the standard Microsoft term (also written "platform invoke"); the general concept is a foreign function interface (FFI).

**Where it appears:** `docs/solver.md`, Numerics (backend plan — "Native KLU/SuiteSparse via P/Invoke stays rejected").

### packet 1010
- The specific numbered network message in Vintage Story that carries chisel (microblock) edits from client to server, making the server the authority on what actually changed.

In Vintage Story, multiplayer games have one server that owns the true world state; when a player chisels a voxel out of a block, the client does not just edit its local copy — it sends a message, and each message type has a numeric ID. Packet 1010 is the ID for chisel edits (handled in `BEChisel.cs:200, :224` in the engine source). We cite it as evidence that chiseling is *server-mediated*: the edit happens where our cable simulation also lives, so intercepting or vetoing a chisel edit must happen in the server-side handlers (by overriding `SetVoxel`/`UpdateVoxel` or supplying a custom `ChiselMode`), because behaviors are only notified *after* the change — there is no pre-edit veto hook. For an EE analogy: it is like a numbered command code on a shared control bus — the ID tells the receiver which handler routine to dispatch to, and the master (server) decides whether the command takes effect.

**Standard term:** this is standard game-networking practice (a packet/opcode ID); "1010" is Vintage Story's specific value, verified from engine source, not a Manatee invention.

**Where it appears:** `docs/vintage-story.md` §1, "Chiseling has no pre-edit veto" (with `BEChisel.cs` file:line references).

### pad loop
- The small delay loop inside Stationeers' simulation tick that "pads out" a fast tick with 1 ms sleeps until the full 500 ms cycle has elapsed.

Stationeers runs its power/atmospherics simulation on a self-clocked cadence: do the tick's work, then repeatedly sleep 1 ms and check a stopwatch until 500 ms total have passed, then start the next tick (`DefaultTickSpeedMs = 500`, `GameManager.cs:150`). It is not an OS timer or per-frame callback — the loop itself is the clock, like a firmware main loop that busy-waits on a hardware timer to hold a fixed sample rate. This matters to Manatee because a slow tick simply stretches the cycle (there is no watchdog or deadline), so our solver's per-tick cost directly delays *everything* sharing that loop. Decompile caveat, flagged in the docs: the pad loop appears to test `Elapsed.Milliseconds` — the 0–999 milliseconds *component* of the elapsed time, which wraps every second — rather than `TotalMilliseconds`, the full duration. A tick body longer than 1 s could therefore see a small remainder and re-enter the padding; this may be an artifact of the ILSpy decompiler rather than the real code, and is noted as worth flagging upstream rather than treated as fact.

**Standard term:** no single canonical name; the pattern is a fixed-rate loop with sleep-based padding (cf. a game "fixed timestep" loop). "Pad loop" is our shorthand for this specific loop in the decompiled game code.

**Where it appears:** `docs/stationeers.md`, Threading section (cadence and decompile-caveat bullets).

### parser fuzz
- Stress-testing our Falstad circuit-file importer by feeding it hundreds of varied real-world files and checking that it never crashes, mis-parses, or silently accepts garbage.

A parser reads a text file (here, Falstad/circuitjs1's circuit dump format) and turns it into structured data; "fuzzing" means bombarding it with many diverse inputs to shake out edge cases the author never imagined. Our variant is corpus-driven rather than random: circuitjs1 ships ~hundreds of example circuits in `public/circuits/`, and between them they exercise every historical quirk of the format — old vs. new switch booleans, 4-parameter vs. 6-parameter voltage-source lines, flag-gated optional parameters. Running the importer over all of them, plus round-trip (parse → re-emit → re-parse) checks, is how we validate the accept/reject contract in the spec. An EE analogy: it is burn-in testing — you don't prove a design correct by inspection, you subject it to the full range of real operating conditions and watch for failures. Licensing note from the doc: the corpus is GPLv2, so the files are fetched at test time, never vendored into our MIT repo.

**Standard term:** **fuzz testing / fuzzing**; strictly, classic fuzzing uses generated or mutated inputs, while ours is closer to **corpus-based regression/conformance testing** — the docs use "fuzz" loosely for both.

**Where it appears:** `docs/falstad-format.md` §7 ("Free test material"), supporting the importer accept/reject contract; testing philosophy in `docs/testing-strategy.md`.

### pass-through
The compaction layer's handling for input that is already minimal: forward it to the solver essentially unchanged instead of trying to reduce it further.

Manatee's reduction layer (`docs/compaction.md`) normally shrinks large physical cable graphs — thousands of voxel or cable segments — into a small equivalent netlist before solving. But the tablet / 2D schematic harness hands over hand-drawn schematics whose wires are already near-minimal, so there is nothing worth collapsing: the layer passes them through, treating each schematic wire as an ideal node (zero resistance, following the Falstad circuit-simulator convention), with optional per-wire resistance for advanced lessons. This is the common software pattern of an adapter with an identity fast path: when the transformation would be a no-op, skip the machinery rather than run it for nothing. The EE analogy is a matching network you omit because the source and load are already matched — inserting one would add cost without changing behavior.

"Pass-through" is ordinary engineering vocabulary (as in pass-through wiring or a pass-through function), used here in its usual sense.

**Where it appears:** `docs/compaction.md`, Client Intake Contracts (the tablet/2D-harness bullet); `docs/harness.md` for the schematic side.

### per-substep sampling / ring buffer
- The oscilloscope mechanism: a probe can subscribe to have its voltage or current recorded at every solver substep into a fixed-size circular buffer, so instruments can display real waveforms.

On a subcycled AC island the solver takes many small time steps ("substeps") inside each game tick; a probe that only read the tick boundary would see one point per tick and miss the sine wave entirely. Per-substep sampling captures the value at every substep. A *ring buffer* (also called a circular buffer) is a fixed-capacity array that wraps around, overwriting the oldest sample — exactly what an EE knows as the acquisition memory of a digital storage oscilloscope: bounded, always holding the most recent window, never growing. In the API this is `WaveformTap.Attach(netlist, probeId, ring)` writing into a caller-owned `WaveformRing`; attaching is a cold (tier-0) operation. The stated UI contract is **two probes at a time** — phase comparisons need a pair, and two keeps the per-substep cost bounded. Note the related but distinct rule for limit events: those are *evaluated* per substep too, but their emission coalesces to one event per tick — only waveform taps stream every sample.

If our term IS standard: "ring buffer" / "circular buffer" is the standard CS term; per-substep sampling corresponds to a scope's sample clock versus the display refresh.
- **Where it appears:** `docs/solver.md` (State, limits, probes — Probes bullet); `docs/api.md` §13 (`WaveformTap`, `WaveformRing`).
- **References:** Knuth, *The Art of Computer Programming*, Vol. 1 (circular queues); the ngspice manual (transient-analysis timestep vs. output points, an analogous sampling distinction).

### perish rate / OnAcquireTransitionSpeed
- Vintage Story's built-in mechanism for how fast stored food spoils, exposed as a hook (`Inventory.OnAcquireTransitionSpeed`) that a container can override to speed up or slow down spoilage — the lever our freezer pulls.

In VS, every food item transitions toward "rotten" at a rate the engine computes on demand from world climate (there is no stored room temperature — the docs verified this against engine source: `InWorldContainer.GetPerishRate()`, `InWorldContainer.cs:173`). `OnAcquireTransitionSpeed` is a *delegate*: a function slot the engine calls whenever it needs the rate, which a mod can fill with its own function — the EE analogy is a socket on a board where the engine normally plugs in its stock module and we plug in ours instead. The Manatee freezer's block entity hooks this delegate and returns a perish rate scaled by the freezer's *electrical duty* (how much real power the solver says it is drawing), so an underpowered or browned-out freezer genuinely keeps food worse. The docs call this the "vanilla-blessed lever": it is the engine's intended extension point, not a fragile patch. (The engine also offers a sibling lever — subclassing `InWorldContainer` and overriding `GetPerishRate()` directly, which is what the base game's `ConstantPerishRateContainer` does; `InWorldContainer` wires the delegate to call `GetPerishRate`, so the two paths meet in the same computation. The freezer uses the delegate.)

If our term IS standard: `OnAcquireTransitionSpeed` is the Vintage Story API's own name; "delegate" is the standard C# term for a first-class function reference (a callback).
- **Where it appears:** `docs/vintage-story.md` §Rooms and Heat (Freezer bullet, with engine file:line references); `docs/design.md` (heat section).
- **References:** Gamma, Helm, Johnson & Vlissides, *Design Patterns* (Strategy/Observer — the callback-slot pattern delegates implement).

### Persistence hook / save-load hook
- Our own serialization path for solver and network state in the Stationeers integration, needed because the game rebuilds cable networks from scratch on every load and only saves per-device state.
- Stationeers' vanilla save system ("the vanilla driver") persists what each device remembers about itself — breaker positions, battery charge — but throws away the network graph and everything the solver knows (capacitor voltages, integrator state) and reconstructs the wiring on load. A *hook* is a designated extension point where our mod's code gets called during the game's save and load sequence; through it we write and read manatee-core state snapshots (requirement R6), keyed by the game's stable network identifiers (RefIds). The failure contract is graceful: a snapshot that fails to restore degrades that island to a cold start (as if freshly built), never a failed save-load. An EE analogy: the game saves each instrument's front-panel settings but not the test-bench wiring or the charge sitting on the capacitors; our hook photographs the bench state so the experiment resumes mid-transient instead of restarting from zero.
- **Standard terms:** *hook* is the standard programming term for such an extension point; the snapshot itself is an application of the Memento pattern.
- **Where it appears:** `docs/design.md` R6; `docs/stationeers.md` Persistence section and integration-seams list.
- **References:** Gamma, Helm, Johnson & Vlissides, *Design Patterns* (1994) — Memento pattern.

### Persistence / player attributes
- The save mechanism for the tablet: lesson progress and the player's sandbox circuits are stored on the Vintage Story *player attributes* system, encoded as Falstad text.
- Player attributes are a per-player key–value store that the VS engine already saves and loads with the world — think of it as a labelled drawer the game keeps for each player, so we never write our own save file. Instead of inventing a binary format, a sandbox circuit is serialized as Falstad text (the plain-text circuit-description format of Falstad's circuitjs1 simulator, see `docs/falstad-format.md`): it is small, human-readable, and the tablet's importer already parses it, so save/load reuses the exact code path that loads lesson files. For an EE, the analogy is keeping your schematic as the netlist listing itself rather than a proprietary CAD blob — anyone (and any tool) can read it back. Note this covers the *tablet* only; world-cable and Stationeers state use different mechanisms (see "persistence hook / save-load hook").
- **Where it appears:** `docs/vintage-story.md`, Tablet Host section ("Persistence" bullet); format defined in `docs/falstad-format.md`.

### Phase discipline / phase error
- The rule that every simulation tick executes its work in one canonical order — topology → re-pin → drive → solve → readback — and that certain operations are only legal in their designated phase; doing one outside its phase is a *phase error* (caught by a debug assert).
- Note: "phase" here is the scheduling sense (a stage of the tick), unrelated to AC waveform phase above. The one load-bearing rule is that structural mutation (`Edit`, `Reconfigure`, `RemoveSegment`) must never overlap any island's `Step`; because netlist-global structures (handle maps, journal, island table, published solutions) are only mutated by structural ops and by publish, barring those during the solve phase makes all the concurrent reads that island-worker threads perform lock-free and safe *by contract*, not by luck. Similarly, `LastTickStats` is written per island during `Step` and aggregated at a defined point, so reading it while any `Step` is in flight is a phase error — it belongs to the readback phase. An EE analogy: it is a clocked synchronous design rather than asynchronous logic — signals may only be sampled on the defined clock edge, and sampling mid-transition reads garbage; the debug asserts are the setup/hold-time checker. In CS terms it is phased-execution concurrency control (akin to bulk-synchronous parallel supersteps): correctness comes from *when* you act, enforced by assertions instead of locks.
- **Project-coined term**, closest standard concepts: phased execution / the Bulk Synchronous Parallel model; "phase error" by analogy with protocol-violation assertions.
- **Where it appears:** `docs/api.md` §9 (`TickStats` window), §17 (canonical tick order), §21 (Threading, phases, spans, allocation — the binding statement); mirrored in the §22 walkthroughs.
- **References:** Valiant (1990), "A Bridging Model for Parallel Computation", CACM 33(8) — the bulk-synchronous parallel model this discipline resembles.

### pinned endpoint / two-endpoint activation
- A pinned endpoint is a fixed anchor a hanging span attaches to; "two-endpoint activation" is the pattern where a span only exists (and renders) once both of its anchors are established.

The terms come from Vintage Story's vanilla rope/cloth system (`vsessentialsmod/Systems/Cloth/`), which spans geometry across arbitrary distances between two anchors — `ClothPoint.PinTo` fixes an end of the simulated rope to a block or entity, and a rope is created between two such pins (`ClothSystem.CreateRope`). For an EE, a pin is exactly what it sounds like mechanically: the point where the cable is clamped to a pole; the sag between two clamps is a catenary. For a programmer, two-endpoint activation is a lifecycle rule — the span object is keyed by an (endpointA, endpointB) pair and becomes active only when both block entities exist, which also settles ownership, persistence, and teardown when either end is removed. Manatee reuses the *activation model and segment-transform math* for long wire spans between endpoint block entities, but deliberately not the live mass-spring physics (cost, wobble, rip risk): wires render as a static catenary mesh in our own `IRenderer`. Known gotchas inherited from the cloth code: long spans need a generous `RenderRange`, and the cloth code does not actually handle spans crossing map-region boundaries — persistence is keyed to only the first endpoint's region (an open TODO in `ClothManager.cs`, not an enforced limit) — so cross-region wire persistence is an unresolved edge case Manatee must handle explicitly.
- **Standard term:** "pinned" is standard in cloth/particle simulation (a pinned/fixed particle, i.e. a fixed boundary condition); "two-endpoint activation" is Vintage Story-derived vocabulary for the paired-anchor lifecycle, closest general concept: an object whose lifetime is gated on both ends of an edge existing.
- **Where it appears:** vintage-story.md, Wire Rendering (catenary spans between posts), with file:line references into `third_party/vsessentialsmod/Systems/Cloth/`.

### Pixel snapshots
A deliberately small secondary test suite in which the tablet's display list is rasterized to an actual image (via headless SkiaSharp with a font bundled in the test assets) and compared byte-for-byte against stored "golden" PNGs.

A snapshot test records a known-good output once and fails whenever the current output differs — like keeping a reference oscilloscope trace and flagging any new trace that deviates from it. Manatee's harness has two tiers of visual golden: display-list snapshots (structured text describing the draw commands, the primary tier) catch most rendering regressions with reviewable diffs, while pixel snapshots cover the last mile — bugs in the rasterizer itself that only show up in actual pixels. The suite is kept intentionally small, and the font is pinned in test assets, to avoid the classic failure mode of image-based testing: one font-hinting or GPU-driver change invalidating hundreds of goldens at once and training everyone to rubber-stamp updates.

**Standard term:** pixel/image snapshot testing, also called screenshot testing or visual regression testing.

**Where it appears:** `docs/harness.md` § Testing Model (secondary tier under display-list snapshots); rasterization backend in § Rendering Backends (SkiaSharp).

**References:** Meszaros, *xUnit Test Patterns* (Golden Master / characterization testing).

### Playwright-like
An analogy the harness docs use for the tablet's interaction-testing approach: tests are scripts of user actions driven against the UI logic, in the style of the Playwright browser-automation tool — but fully headless, with no browser or widget toolkit involved.

Playwright is a widely used tool that tests web applications by scripting a real browser: click here, type this, assert that appears. Manatee borrows the shape of that workflow, not the machinery: because the tablet UI is built as three pure layers (document, interaction state machine, display-list view), a test can feed synthetic input events straight into the interaction layer — "pointer down at the resistor palette, drag to (140, 60), release" — and assert on the resulting document mutations and undo entries, deterministically and in ordinary CI. For an EE, the analogy is exercising a device through its terminal pins on a bench fixture rather than in the finished product: same stimulus, same observable response, none of the packaging. The point of the phrase is that Manatee gets Playwright-style behavioral coverage without Playwright's cost (a real rendering engine, timing nondeterminism).

**Project usage note:** this is an informal analogy in the docs, not a dependency — Playwright itself is not used. Closest standard concepts: scripted end-to-end UI testing; headless testing against a humble-object/MVU-style UI core.

**Where it appears:** `docs/harness.md` § Testing Model ("the 'Playwright-like' answer, without a browser in sight"), describing the interaction tests.

**References:** Meszaros, *xUnit Test Patterns* (Humble Object pattern, which is what makes the headless variant possible).

### Pooled sealed class / rented scope
An object-lifetime pattern where a single reusable object is borrowed ("rented") from its owner for the duration of one operation and handed back when done, instead of being newly created each time.

In manatee-core, `StructuralEdit` — the batch object through which all wiring changes are made — is a **pooled sealed class rented from the netlist**: `Edit()` hands you the pooled instance, you make your changes, and `Dispose()` commits atomically and returns it to the pool. `sealed` means no subclassing (the behavior cannot be altered from outside); *pooled* means the same instance is recycled, so the allocation cost is paid only at "shape time" (when the circuit's topology changes), never in the steady-state solve loop. Opening a second edit while one is open (nesting) throws immediately. The design deliberately rejects making the batch a copyable value type (struct): a copyable batch is a footgun, because two copies could each think they own the pending changes and commit or abort independently. For the EE: think of it as a single physical work permit for the panel — one crew signs it out, does the rewiring, signs it back in; permits are never photocopied, and nobody opens a second permit while one is live.

**Standard terms:** object pool pattern; the rent/return usage mirrors .NET's `ArrayPool<T>.Rent`. "Rented scope" as a phrase is project house style for pool + `IDisposable` scope combined.

**Where it appears:** `docs/api.md` §6 (the `StructuralEdit` class and the paragraph after it) and §24 decision log entry 12.

**References:** Gamma, Helm, Johnson & Vlissides, *Design Patterns* (object creation/lifecycle patterns); object pooling is treated in Kircher & Jain, *Pattern-Oriented Software Architecture Vol. 3: Patterns for Resource Management* (Pooling pattern).

### pre-edit veto
- A software hook that gets to inspect a proposed change *before* it happens and can reject it — like an inspector who must sign off before any modification is made, rather than being told about it afterward.

Many engines offer both styles of event: "about to change, may I?" (vetoable, pre-edit) and "has changed, FYI" (notification, post-edit). Vintage Story's chiseling system offers only the second: the interaction flows from `ItemChisel.OnBlockInteract` through `UpdateVoxel`/`SetVoxel`, and block-entity behaviors are notified *after* the voxels have already changed. That matters for Manatee because cable voxels embedded in a chiseled block must not be silently carved away — and since there is no veto hook, protection requires overriding `SetVoxel`/`UpdateVoxel` in a subclass (replacing the block-entity class) or supplying a custom `ChiselMode`, i.e. intercepting the write path itself. EE analogy: a post-edit notification is like an alarm that trips after the fault current has already flowed; a pre-edit veto is the interlock that prevents the switch from closing in the first place.

Standard software concept; commonly seen as "vetoable events" or cancellable pre-events (e.g. Java's `VetoableChangeListener`, cancellable events in game-mod APIs). The specific finding — that VS chiseling lacks one — is a verified engine fact with file:line references.
- **Where it appears:** `docs/vintage-story.md` §1, "Chiseling has no pre-edit veto" (with `ItemChisel.cs` / `BEChisel.cs` line references).

### prefab (name)
- In Unity (the game engine Stationeers is built on), a *prefab* is a reusable object template; its name is a string that identifies which kind of object an instance was stamped from.

For an EE, a prefab is like a part number in a catalog: every placed cable in the game world is an instance of some prefab, and the prefab name tells you exactly which cable product it is — heavy vs. standard, its gauge, its ratings. Manatee's Stationeers intake uses the per-cable prefab name as the lookup key for electrical parameters: `CableSpecs.For(c.PrefabName)` (api.md §22.a) maps the name to a `CableSpec` carrying resistance-per-length and current rating, from which each cable segment's resistor edge and thermal/fuse envelope are derived. The prefab name is one of the export additions agreed with Sukasa, since the raw `NetworkExport` does not carry it.

**Where it appears:** `docs/stationeers.md` (Graph Construction), `docs/api.md` §19 (intake normalization) and §22.a (Re-Volt walkthrough).

**References:** Unity documentation on Prefabs; the pattern is an instance of the Prototype pattern, Gamma, Helm, Johnson & Vlissides, *Design Patterns* (Addison-Wesley, 1994).

### prefix (pattern)
- A *prefix* is a Harmony patch — a piece of code injected to run immediately *before* an existing game method — which can either let the original method run or skip it entirely.

Harmony is the standard library modders use to rewrite a game's compiled code at load time without touching its files; a prefix returning `false` replaces the original method, while returning `true` lets it run. For an EE, it is a bypass switch wired in front of a stage: the mod's circuit can take over the signal path, but flipping the switch routes everything through the original equipment untouched. Re-Volt already uses this on Stationeers' `PowerTick`: its prefix runs Re-Volt's power logic instead of vanilla's, but returns `true` (vanilla runs) whenever injection fails. Manatee inherits this as the bottom rung of the graceful-degradation ladder — manatee island → Re-Volt scalar distribution → vanilla `PowerTick` — so the game never stalls or breaks on solver failure (`docs/stationeers.md`, Failure and Fallback).

**Standard term:** Harmony "prefix patch"; conceptually a form of method interception / runtime detouring (related to aspect-oriented programming's before-advice).

**Where it appears:** `docs/stationeers.md` (Failure and Fallback; also the Harmony injection discussion).

**References:** Harmony library documentation (pardeike/Harmony, harmony.pardeike.net) — Prefix patches.

### probe readback
- Pulling measured values out of the solved circuit at each probe placement — the final read step that turns solver output into the numbers shown on meters and scope traces.

After a solve, the harness (and later the games) queries each probe via `Solution.Read(ProbeId)` to populate multimeter readouts and oscilloscope buffers. "Readback" names the direction of data flow: document → netlist → solver → *back out* to the document's instruments. The lesson-corpus integration tests exercise this whole pipeline end to end — build a schematic, extract the netlist, solve, then read the probes and check the values against the lesson's machine-readable expectations (probe, time, value, tolerance) — so a broken readback path fails CI even if the solver itself is correct. For an EE: readback is simply *taking the measurement* after the circuit settles; the term emphasizes that in software this is a distinct, testable step (like reading a register back after writing a device, to confirm the round trip), not something that happens automatically when the circuit is solved.

**Standard term:** "readback" is common systems-programming usage (register/GPU readback); in SPICE terms it corresponds to retrieving output vectors after simulation.

**Where it appears:** harness.md Testing Model (lesson corpus integration, "exercising netlist extraction, solving, and probe readback end to end"); testing-strategy.md narrative pass; api.md `Solution.Read`.

### property / fuzz test
A test that, instead of checking one hand-picked example, generates many randomized inputs (from a recorded seed, so failures replay exactly) and asserts that a stated *property* holds for every one of them.

An ordinary unit test says "this circuit gives 5 V here"; a property test says "for *any* randomly generated connected R/V/I ladder, the solution satisfies KCL and matches ngspice within tolerance" and then throws hundreds of machine-generated circuits at that claim. The "fuzz" flavor emphasizes hostile or extreme inputs — diode sources swept through the knee, component values spanning the full legal conductance range, randomized load steps across island boundaries — where the asserted properties are survival ones: converge or Fault legibly, never NaN, never hang, never ring unboundedly. For an EE, this is Monte Carlo verification applied to software: rather than trusting a few worked examples, you sample the input space broadly and check that an invariant (a conservation law, a stability bound) holds everywhere sampled. Manatee uses the CsCheck library on top of xUnit; every run is seeded, so a failing random circuit can be reproduced deterministically.

Standard terms: **property-based testing** (popularized by QuickCheck) and **fuzzing**; the docs use them together since our suites blend both styles.

**Where it appears:** `docs/testing-strategy.md`, "Property and Fuzz Tests" and "Toolchain" (CsCheck).

**References:** Claessen & Hughes (2000), "QuickCheck: A Lightweight Tool for Random Testing of Haskell Programs", ICFP 2000.

### pure-addition fast path
The cheap route through incremental compaction taken when an edit only *adds* geometry to one existing region: only the dirtied area is reprocessed, and nothing is rebuilt.

When a client adds cable segments, `ConductorGraph` first runs a merge pre-check: would the new geometry bridge more than one existing region? If not, and no removals are involved, the pure-addition fast path applies — the reducer re-collapses only the dirty region (tracked at the client's natural chunk size: 16³ blocks in Vintage Story, per-CableNetwork in Stationeers) and leaves the rest of the island's compacted form untouched. Removals never qualify: any removal marks the island dirty and forces a rebuild, coalesced to at most once per tick at solve time. "Fast path" is standard programming vocabulary for the optimized branch handling the common easy case, with a slower general branch as fallback. For an EE, this is like extending an existing PCB trace: you only re-verify the net you touched, whereas cutting a trace forces you to re-derive the whole board's connectivity, since a cut can split one net into two in ways local inspection can't rule out. The resync backstop (background rebuild-and-diff) exists to catch any bugs this incremental path develops against the live mutation stream.

Standard concepts: **fast path / slow path** dispatch and incremental (dirty-region) recomputation; the addition/removal asymmetry mirrors incremental dynamic-connectivity, where edge insertions (union-find merges) are cheap but deletions are not.

**Where it appears:** `docs/compaction.md`, "Incremental Maintenance" (with the merge pre-check, removal-coalescing rule, and resync backstop).

**References:** Cormen, Leiserson, Rivest & Stein, "Introduction to Algorithms" (disjoint-set union-find, which supports incremental merges but not splits).

### pure layers / purity
Code layers that depend on no UI framework and touch no hidden global state, so the same inputs always produce the same outputs — which makes them fully testable without a screen.

In `Manatee.Schematic`, all three tablet layers — the document model, the interaction state machine, and the display-list view layer — are pure in this sense: they consume plain data (documents, abstract pointer/key events) and produce plain data (document mutations, a flat list of draw commands). Nothing in them may reference a widget toolkit; Avalonia lives strictly outside, in the swappable desktop shell. This one rule is what makes the tablet testable: interaction tests are just scripts of synthetic events, and rendering tests are diffs of display-list data, all deterministic and headless in CI. For an EE, purity is the software analogue of a memoryless, drift-free instrument: the reading depends only on what you feed it, never on ambient conditions or measurement history, so bench verification of the module guarantees in-circuit behavior.

Standard term: **pure / side-effect-free code** (from functional programming); the layering discipline is a form of hexagonal / ports-and-adapters architecture.

**Where it appears:** `docs/harness.md`, "Layering", "Testing Model", and "The Desktop Shell".

**References:** Alistair Cockburn, "Hexagonal Architecture" (ports-and-adapters), the origin of the framework-outside-the-core layering discipline.

### pure read / cached mean
The discipline that a getter must only return an already-stored value — no recomputation, no side effects — applied to the alternator's counter-torque: `GetResistance()` returns a precomputed average, and merely calling it changes nothing.

The Vintage Story mechanical network reads back load resistance on its own schedule (~100 ms re-sums, plus out-of-band `updateNetwork` calls — `BEBehaviorWindmillRotor.cs:197`). Because single-phase AC power pulsates at twice the electrical frequency, an *instantaneous* sample would alias into a phase-dependent phantom torque; the solver therefore maintains, as it steps, the **mean counter-torque over all electrical substeps since the previous mechanical read** — the cached mean. `GetResistance()` is required to be a pure read of that cache: valid at any time on the server main thread, safe to call twice, never triggering a solve. For an EE, the cached mean is exactly a hardware averaging/anti-aliasing filter placed before an asynchronous sampler, and "pure read" means the measurement is non-loading — probing the value doesn't disturb the circuit. For a programmer, it's the rule that property getters must be idempotent, side-effect-free O(1) reads of state maintained elsewhere.

Standard concepts: side-effect-free (pure) getter / idempotent read; the averaging is standard anti-aliasing by pre-filtering before decimation.

**Where it appears:** `docs/vintage-story.md` §2, "The averaging window is normative"; the standing invariant test (closed-loop monotonic decay) in `docs/testing-strategy.md`.

**References:** Oppenheim & Schafer, "Discrete-Time Signal Processing" (aliasing and pre-filtering before rate reduction).

### rasterize / rasterizer
Converting a resolution-independent description of a picture (shapes, paths, text) into an actual grid of pixels; a *rasterizer* is the component that does it.

In the harness, the view layer emits a **display list** — a flat sequence of draw commands in schematic coordinates, which is pure data. Each rendering backend is then a rasterizer of that one shared contract: SkiaSharp rasterizes it for the desktop harness and for headless pixel-snapshot tests, and the Vintage Story GUI backend rasterizes the same commands onto an in-game texture. For an EE analogy: the display list is like a schematic netlist of the picture, and the rasterizer is the fab that turns it into physical silicon — many fabs, one design. Splitting things this way means behavior and layout are tested on the display list (stable, diffable text), while only a deliberately small suite of pixel snapshots guards against rasterizer-level regressions.

This is the standard graphics term; related standard vocabulary: *scan conversion*, and *display list* (a retained sequence of drawing commands, a term dating to early vector-graphics systems).

**Where it appears:** `docs/harness.md` — Testing Model (pixel snapshots via headless SkiaSharp) and Rendering Backends ("one contract, multiple rasterizers").

**References:** Foley, van Dam, Feiner & Hughes, *Computer Graphics: Principles and Practice* (rasterization and display lists).

### ref struct
- A C# type declaration meaning the value may live only on the stack: it can never be placed on the garbage-collected heap, boxed into an object, captured by a closure, stored in a class field, or carried across an `await`.

The compiler enforces all of this at build time, which makes a ref struct a machine-checked guarantee of two properties Manatee's hot path depends on: **zero heap allocation** (creating one costs nothing the garbage collector ever sees) and **non-escape** (the value cannot outlive the scope that created it). For an EE, the closest analogy is a signal that exists only inside one stage of a pipeline and physically cannot be latched or fed back — its lifetime is bounded by construction, not by convention. The project uses ref structs for `SteadyStateGuard` and `AllocationSentinel` (api.md §8: the scope that forbids structural mutation and asserts the tick allocated 0 bytes) and for `DeviceTickContext` (api.md §18: the per-tick capability handed to a device's `Tick`, declared `readonly ref struct` so it "cannot escape the tick"). One documented consequence (§8): because the guard cannot hold heap references beyond its stack frame, deferred structural ops are stored in a netlist-owned pre-allocated ring instead of inside the guard itself.

**Standard term:** yes — C# `ref struct` (introduced in C# 7.2, associated with `Span<T>`); also described as a "stack-only" or "byref-like" type.

**Where it appears:** api.md §8 (`SteadyStateGuard`, `AllocationSentinel`), §18 (`DeviceTickContext`), §21 (allocation and span-validity rules these types enforce).

**References:** Microsoft C# language reference, "ref struct types" (docs.microsoft.com / learn.microsoft.com); ECMA-334 C# Language Specification.

### Region-boundary span / map-region persistence
- A constraint from Vintage Story's world streaming: the game saves and loads the world in large tiles called map regions, and long objects (like our catenary wire spans) that stretch across a tile boundary are troublesome to persist correctly.

Vintage Story streams the world in and out of memory in units — chunks and, above them, map regions — much the way a paged data structure loads one page at a time. Anything that must be saved (persisted) is owned by exactly one such unit, and an object physically spanning two regions can find one of its endpoints unloaded while the other is live. The engine's own rope/cloth system persists each cloth span into the single map region containing its *start point* (`Event_MapRegionUnloaded`, `ClothManager.cs:588-606` in `vsessentialsmod`, keyed on `cs.FirstPoint.Pos`), and its own comments (`ClothManager.cs:583-585`) explicitly flag the cross-region case as an unhandled open question ("What about cloth points that cross a region border?") — there is no guard preventing such spans, just single-region ownership that leaves them exposed. For an EE, the analogy is a distributed measurement whose two probes are on separately-powered racks: unless both racks are up, the reading is undefined. Our wire rendering plan borrows the cloth system's two-pinned-endpoint pattern for long catenary spans between posts, so wire persistence must not inherit that gap: either avoid region-boundary spans or explicitly handle a half-loaded span.
- **Where it appears:** `docs/vintage-story.md`, Wire Rendering Gotchas (catenary spans bullet); the engine precedent is `vsessentialsmod/Systems/Cloth/`.

### Regression corpus
- A permanent, ever-growing collection of automated tests where each test reproduces one specific bug that was once fixed, so the bug can never silently return.

The project rule ("regression tests first") is that every bug fix lands together with the test that reproduces it, and that test joins a persistent corpus run on every change — a habit inherited from the sparky prototype's `testdata/regression` directory. In lab terms, it is like keeping every failing sample that ever exposed a fault in a hardware design and re-running the full sample tray against each new board revision: passing the tray proves none of the old faults have crept back. The corpus also serves double duty as input to broader checks — invariant and equivalence tests are run over the whole regression corpus, not just fresh fixtures. "Regression" here is the standard software sense (a previously-working behavior breaking again), unrelated to statistical regression.
- **Standard term:** regression test suite (regression testing).
- **Where it appears:** `docs/testing-strategy.md` (Principles; referenced again under oracle and equivalence testing).
- **References:** regression testing is standard practice; see e.g. Myers, *The Art of Software Testing*, or Fowler's writing on self-testing code in *Refactoring* (1999).

### Rendering Backends
The interchangeable rasterizers that turn the tablet UI's display list into actual pixels: SkiaSharp on desktop and in headless tests, and the Vintage Story GUI in-game.

The tablet's view layer emits a **display list** — a flat sequence of abstract draw commands ("stroke this path, fill that shape, place these glyphs") in schematic coordinates. That list is pure data with one contract, and a *backend* is any component that executes those commands onto a surface. SkiaSharp (a 2D graphics library) serves the Avalonia desktop harness and the small pixel-snapshot test suite; a Vintage Story backend renders the same commands to an in-game texture/dialog, spot-checked for equivalence against the Skia output. For the EE: the display list is like a schematic netlist for a drawing — a device-independent description — and each backend is a different plotter that can reproduce it; because the description is the tested artifact, swapping plotters cannot silently change what the circuit diagram *means*. This is what lets nearly all UI testing run headless, with no game engine involved.

Standard architecture; the display-list-plus-backends split is a classic separation of scene description from rasterization (cf. the Bridge pattern — decoupling abstraction from implementation).

**Where it appears:** harness.md (Rendering Backends section; View layer and Testing Model sections define the display-list contract the backends implement).

**References:** Gamma, Helm, Johnson & Vlissides, *Design Patterns* (Bridge; Command for the draw-command sequence).

### RenderRange
- A property on Vintage Story renderers that sets the distance from the player (in blocks) within which the game bothers to draw that object.

In principle this is a distance-cull dial; in practice the VS API source documents `IRenderer.RenderRange` as “currently not used!” (`third_party/vsapi/Client/API/IRenderer.cs:80`), so for a custom `IRenderer` — exactly our catenary-span case — the engine performs no distance cull on this property today. This is a performance dial — like an instrument that only registers signals above a threshold, the engine ignores geometry too far away to matter. It bites for wire spans because a catenary wire is anchored at one endpoint but can stretch far beyond it: if `RenderRange` is tuned for a one-block object, a long span pops out of existence while its far end is still plainly in view. Our plan (voxel geometry for short runs, catenary meshes for long spans between posts) therefore sets a generous `RenderRange` on the span renderer, sized to the span length rather than to the anchor block — but must treat culling as its own responsibility rather than relying on the engine honoring the property.

This is a Vintage Story engine API name, used as-is; the general graphics concept is *distance culling* / *draw distance*.

**Where it appears:** docs/vintage-story.md, "Wire Rendering" gotchas (catenary spans between posts).

### retained-mode
- A style of API in which the client builds a persistent description of what it wants once, keeps it alive, and thereafter edits it in place — instead of re-describing everything from scratch on every use.

In Manatee, the `Netlist` is a retained-mode circuit **document**: you add nodes and components once, hold onto the strongly-typed handles you get back, and later mutate the document incrementally (change a source value, toggle a switch, add a component). Matrices, factorizations, and islands are *derived* from that document by the solver — clients never touch them. The alternative is "immediate mode", where the caller would resubmit the whole circuit every tick; retained mode is what makes the change-cost tiers meaningful, because the solver can see exactly *which* small edit happened and pay only for that. For an EE, the physical analogy is a real breadboard versus a fresh schematic each time: the circuit stays built on the bench, and you only move the one wire you changed.

The term is borrowed from graphics-API vocabulary (retained-mode vs. immediate-mode rendering, e.g. a scene graph vs. per-frame draw calls) and is standard there; our use for a circuit netlist is a direct transplant of that meaning.

**Where it appears:** `docs/solver.md` (The Netlist API), `docs/api.md` §4 ("`Netlist` is the retained-mode circuit document"), `docs/integration-tutorial.md` ("the Netlist is a retained document; matrices are derived").

### retained mode / document write
- The design rule that all mutations update a persistent model of the circuit (the *document*) rather than being applied directly to the derived numerical machinery; the matrices are recomputed from the document, never edited as the primary copy.

"Retained mode" is a term from graphics APIs: instead of issuing draw commands that take effect immediately and are then forgotten (*immediate mode*), the client edits a persistent scene description that the system renders from. Manatee applies the same split to circuit simulation: `Netlist` is the retained-mode circuit **document**; the verbs (`Drive`, `Adjust`, `Edit`, `Reconfigure`) write the document, and the MNA matrices are *derived* from it at `Solve` (api.md §4). A **document write** is any such verb call, and the payoff is the guarantee in api.md §17: a verb call is never lost even if the island is about to be rebuilt, because the pending rebuild restamps from the document — "anything you `Drive`/`Adjust` meanwhile is written to the document, not the doomed matrix." Handles marked *document-stable* (§16), like `CouplerId` and `ProbeId`, live at the document level and therefore survive every matrix rebuild. EE analogy: the document is the schematic, the matrix is the breadboard built from it — you edit the schematic, and the breadboard is rebuilt to match; nothing penciled onto the schematic can be lost just because the breadboard is being torn down.
- **Standard term:** retained mode (vs. immediate mode), from graphics/UI APIs; the document/derived-state split is also the classic model–view separation, and "single source of truth" in modern UI frameworks.
- **Where it appears:** api.md §4 (the four verbs), §16 (document-stable handles), §17 (the document/matrix rule and the "doomed-but-usable window"); integration-tutorial.md's opening mental model ("the Netlist is a retained document; matrices are derived"); solver.md's netlist API section.

### ring / fixed-capacity ring / lapped / overflow-counted
A fixed-size buffer that writes wrap around in a circle, used throughout manatee-core for event streams (topology journal, change events, limit events, waveform samples) — with the rule that when it fills up, the loss is always reported, never silent.

A ring (circular) buffer is an array of fixed length where a writer keeps appending; when it reaches the end it wraps to the start and begins overwriting the oldest entries. In hardware terms it behaves like a chart recorder with a fixed loop of paper: you always have the most recent history, and anything older than one loop is gone. We say the ring has **lapped** a reader when the writer has gone all the way around and overwritten data that reader never consumed. The project's binding rule (requirement R9, "degrades legibly") is that lapping is a *declared* condition, not silent data loss: `EditReceipt.WindowLapped` says up front that a single commit emitted more events than the journal can replay, `TopologyJournal.Overflowed(cursor)` tells a lagging reader it must do a full resync, and `DrainChanges` reports `out bool lost` and `DrainLimitEvents(..., out dropped)` counts exactly how many limit events were shed. Consumers react by falling back to a full rebuild/resync rather than proceeding on incomplete data — and for the change ring specifically, `lost == true` carries a stronger obligation: the client must re-resolve *every* held handle, not just recent ones (a listed integration sharp edge, and the first-tick build-burst can itself overflow). Rings are pre-allocated at construction so steady-state operation allocates zero bytes (the one exception: the netlist-owned deferred-structural-ops ring *grows* on overflow rather than dropping, because those ops must never be lost). For an EE: the fixed rings behave like an annunciator panel with a fixed number of lamps plus an "events missed" counter — bounded hardware, honest about saturation.

**Standard term:** ring buffer / circular buffer (also called a circular queue); "lapped" and "overflow-counted" are our shorthand for the overwrite-oldest overflow policy plus explicit loss reporting.

**Where it appears:** `docs/api.md` — journal cursors and `Overflowed()` (§15), `EditReceipt.WindowLapped` (§11), `IslandTable.DrainChanges`/`DrainLimitEvents` with `lost`/`dropped` (§12), `WaveformRing`, and the deferred-op ring (§11); `docs/integration-tutorial.md` §5 (re-pin on `lost`), §8 (limit-event drain), appendix §16 (overflow obligations, first-tick build-burst overflow).

**References:** Knuth, *The Art of Computer Programming*, Vol. 1 (circular queues); the pattern is ubiquitous in OS kernels and audio/DSP pipelines.

### room detection / RoomRegistry
A Vintage Story engine system (not our code) that discovers enclosed indoor spaces by flood-filling outward from a position, which the Manatee VS mod builds on for its heating and freezer features.

`RoomRegistry` (in `vsessentialsmod/Systems/RoomRegistry.cs`, entry point `GetRoomForPosition` at line 346) runs a breadth-first search — a flood fill, like water spreading from a starting point until it hits walls — bounded to a 14×14×14 block volume, treating heat-retaining walls as the boundary. The resulting room record carries counts of skylight blocks, cooling walls, and exits (holes in the enclosure), but critically it holds **no temperature state**: temperature in vanilla VS is always recomputed on demand from world climate, and no vanilla block can warm a room. That gap is why the Manatee mod defines its own heat convention — a mod-owned heat registry of per-room heater strength — layered on top of the engine's room identification.

For the EE reader: BFS (breadth-first search) is a systematic way of exploring connected space one neighbor-shell at a time, the same algorithm family we use to find electrically connected islands in a circuit graph; here the "conductors" are air blocks and the "insulators" are walls.

**Standard term:** flood fill / breadth-first search over a voxel grid; `RoomRegistry` is the Vintage Story engine's class name.

**Where it appears:** `docs/vintage-story.md`, section "Rooms and Heat", with engine file:line citations.

**References:** Cormen, Leiserson, Rivest & Stein, *Introduction to Algorithms* (breadth-first search).

### roomness
Vintage Story's own name for a cached room-quality flag computed by the vanilla beehive — the one vanilla example of a block deriving a thermal effect from room properties, which the docs cite as the precedent our electric heaters extend.

In the beehive block entity, `roomness` is a stored field (`BEBeehive.cs:169`): a periodic scan asks the RoomRegistry about its surroundings and sets it to 1 if the space is skylit and has no exits (a proper greenhouse), else 0 (`:207`); it is even persisted to the save tree (`:415`, `:470`). The flag is then consumed elsewhere: when the beehive evaluates temperature, `roomness > 0` grants a +5 °C bonus (`:177`) — so `roomness` is the cached room-quality *flag*, and the +5 °C is the *bonus* derived from it. The genuinely important invariant (see *room detection / RoomRegistry*) is that the Room object itself carries no temperature state: thermal effects are always derived from room properties by the interested block, never written back into the room. Manatee's heaters follow the same shape — devices publish heat into a mod-owned registry, and consumers (crops, perishables, comfort) query it and fold it into their own math — instead of trying to mutate a temperature field that does not exist. For the EE reader: the room is a passive network you measure; each device keeps its own (possibly cached, periodically refreshed) reading rather than anyone maintaining a shared temperature node.

**Standard term:** the identifier `roomness` is Vintage Story engine vocabulary (vanilla source), not ours; the closest general concepts are the game's greenhouse bonus and, structurally, a periodically refreshed cached predicate.

**Where it appears:** `docs/vintage-story.md`, section "Rooms and Heat" (Greenhouse bonus precedent).

### Round-trip (property test)
- An automated test asserting that saving simulator state and loading it back reproduces exactly the same state — the acceptance check for snapshot/restore.

Manatee's snapshot serializes each island's dynamic state (capacitor voltages, inductor currents, device `Tick()` state, source phase) to a versioned binary format. The round-trip property test, run in CI, generates or takes circuit states, does snapshot → restore → step, and requires the result to match a run that never snapshotted. "Property test" means the assertion is a universal invariant checked over many generated inputs, not a handful of hand-picked cases — the testing analogue of proving a law rather than spot-checking examples. For the EE: think of it as verifying that pausing and resuming an experiment mid-transient is indistinguishable from never pausing; any state the snapshot forgets to save shows up as a discrepancy after the resume.

Standard terminology: "round-trip property" is the usual name in property-based testing (e.g. `decode(encode(x)) == x`); the technique traces to QuickCheck.
- **Where it appears:** `docs/solver.md` (State, Limits, Probes — Snapshot/restore R6); `docs/testing-strategy.md` (Snapshot round-trip invariant).
- **References:** Claessen & Hughes (2000), "QuickCheck: A Lightweight Tool for Random Testing of Haskell Programs", ICFP 2000.

### Round-trip test
- A test that parses a circuit file and re-emits (or re-parses) it to confirm nothing was lost or corrupted along the way.

For the Falstad-format importer, the round-trip tests run over circuitjs1's bundled example corpus (~hundreds of real files, used as fetched-not-vendored fixtures because they are GPLv2): read a file into Manatee's representation, write it back out, and check the result is equivalent to the input. This catches silent data loss — a field the parser skipped, a number reformatted wrong (e.g. emitting `+` in an exponent, which the format forbids), a versioning quirk mishandled. For the EE: it is like translating a schematic into another CAD tool and back, then diffing against the original — any component value or connection that changed reveals a defect in the translator, not the circuit. It is the file-format cousin of the snapshot round-trip property test.
- **Where it appears:** `docs/falstad-format.md` §7 (importer error posture; "Free test material" — parser fuzz/round-trip fixtures).

### save/load
- Writing the tablet's schematic document out to a stored form and later reconstructing it exactly — one of the scripted scenarios the test suite runs end to end.

Serialization (save) turns the in-memory circuit document into text; deserialization (load) rebuilds an equivalent document from that text. For an EE, the physical analogy is photographing your breadboard, tearing it down, and rebuilding it from the photo — the tests verify the rebuilt circuit behaves identically to the original. In Manatee, save/load appears as one of the four scripted harness scenario types (build, edit, save/load, fault) that the lesson-corpus integration tests run headless in CI, exercising netlist extraction, solving, and probe readback with no game engine in the loop. In-game, the same machinery persists lesson progress and sandbox netlists as Falstad text in player attributes. Because solver determinism holds by contract, a saved-then-loaded circuit must produce identical readings — making save/load a testable invariant, not just a convenience feature.

Standard programming vocabulary; also called **serialization/deserialization** or **persistence** (both terms also appear in our docs).

**Where it appears:** `docs/harness.md` (Testing Model — lesson corpus integration), `docs/testing-strategy.md` (Game-Layer Testing), `docs/vintage-story.md` (Tablet Host persistence).

### schematic coordinates
- The coordinate space of the schematic document itself — the grid the circuit is drawn on — as opposed to the pixels of any particular screen.

In the harness architecture, the view layer emits a display list (a flat sequence of draw commands: stroke this path, fill that shape, place this glyph run) whose positions and sizes are expressed in schematic coordinates. Converting those coordinates to actual device pixels — applying pan, zoom, and screen resolution — is the rasterizer's job, and different backends (SkiaSharp on desktop, the Vintage Story GUI in-game) each do that conversion themselves. For an EE, the analogy is a schematic on paper: a resistor sits at grid position (140, 60) regardless of whether the drawing is later photocopied at 50% or 200% scale; the drawing's own coordinates are the source of truth, and scaling happens at reproduction time. Keeping draw commands in schematic coordinates is what makes display-list snapshots stable, testable goldens: the same document always yields the same list, independent of window size, DPI, or GPU.

This is the standard graphics-programming split usually called **world/user/model space** vs. **device/screen space**; "schematic coordinates" is simply our name for the world space of the schematic document.

- **Where it appears:** `docs/harness.md` — Layering (layer 3, the view layer / display list) and the Testing Model (display-list snapshots).
- **References:** Foley, van Dam, Feiner & Hughes, *Computer Graphics: Principles and Practice* (world vs. device coordinate systems and the viewing transformation).

### selection
- The set of document elements (components, wires, probes) the user currently has selected, held as part of the editor's interaction state.

In the 2D schematic harness, selection lives in layer 2, the *interaction state machine* — not in the document itself. The document model is the schematic's persistent truth (what components exist, how they connect); selection is transient editing state alongside the active tool, drag-in-progress, hover, and snapping. Keeping it there means saving a schematic never accidentally saves "which things were highlighted," and it means selection behavior can be tested headlessly by feeding synthetic pointer/key events and asserting on the resulting state. For the EE reader: think of the schematic as the circuit on the bench and the selection as which parts you currently have your probe clips on — it changes what your next action affects, but it is not part of the circuit. This split of persistent data from transient editing state is the standard document/view separation in UI architecture (Model–View–Controller and descendants).

**Where it appears:** `docs/harness.md` (Layering, layer 2: interaction state machine); exercised by the interaction tests described in the same doc.

**References:** Gamma, Helm, Johnson & Vlissides, *Design Patterns* (1994) — the Observer/MVC discussion of separating application state from presentation.

### self-clocked / stopwatch-paced
- A loop that controls its own timing: after each pass it checks an elapsed-time counter (a "stopwatch") and sleeps in small increments until the target period has passed, rather than being triggered by an operating-system timer or a once-per-frame callback.

This describes how Stationeers runs its simulation tick, verified from the decompilation: the whole sim (atmospherics, then electricity, then logic) runs in one loop that measures its own duration and pads with 1 ms delays out to a 500 ms cycle (`DefaultTickSpeedMs = 500`). For the EE reader, it is an astable oscillator built from the load itself — the "period" is *work time + padding*, so if the work takes longer than 500 ms the cycle simply stretches; there is no fixed external clock, no watchdog, and no deadline that cuts the work short. The design consequence for Manatee is that solver cost is *in-band*: every microsecond spent solving directly lengthens everyone's tick, so the solve must be cheap in absolute terms, not merely "cheap relative to 500 ms." (The docs also flag a decompile-level caveat: the pad loop appears to test the milliseconds *component* of elapsed time rather than total milliseconds, so a >1 s tick could mis-pad — possibly an ILSpy artifact.)

**Standard term:** this is a rate limiter / minimum-period loop (a purely sleep-based frame cap). Note it is distinct from the canonical fixed-timestep loop ("Fix Your Timestep"), which holds dt constant and runs extra catch-up steps to stay on wall-clock — Stationeers instead lets the cycle stretch when work overruns. "Self-clocked / stopwatch-paced" is our descriptive phrasing for Stationeers' specific implementation.

**Where it appears:** `docs/stationeers.md` (Threading section, with `GameManager.cs` file:line references into the decompilation).

### server-authoritative
- A multiplayer-game architecture rule: one machine (the server) owns the true value of some piece of state, and everyone else's copy is a follower that may lag but never disagrees for long.
- In Manatee this describes Vintage Story's mechanical network: the torque re-sum for shafts and windmills runs on the server every ~100 ms, and that server-side result is the authoritative shaft speed. The electrical simulation consumes it as a piecewise-constant source parameter each tick, and the alternator's counter-torque is reported back time-averaged — a loose, stable coupling because both sides carry inertia. For an EE, the analogy is a master oscillator or reference source: many instruments may display the signal, but exactly one source defines it, and disputes are settled by re-reading the source rather than by negotiation between displays. The alternative (client-authoritative) invites divergence and cheating, which is why game servers keep physical truth on the server.
- **Standard term** in multiplayer game architecture; contrast "client-authoritative" and "client-side prediction".
- **Where it appears:** `docs/design.md` (Simulation Model, mechanical co-simulation bullet); the VS integration in `docs/vintage-story.md`.

### server authority / server-authoritative
- A game-networking design rule: the server's copy of the world is the single source of truth, and clients may only *request* changes and *display* synced copies.

In a multiplayer game every player's machine (client) holds a local copy of the world, but copies drift and players cheat, so one machine — the server — is designated authoritative: all state changes are validated and applied there, then broadcast. For an EE, it is like a system with one master reference and many meters — the meters show what the reference publishes, never their own guess, and adjustments go through the master. Manatee relies on this in two documented Vintage Story facts: chisel edits reach the server via packet 1010 (so voxel-cable topology changes are applied server-side), and the mechanical-power network ticks server-side at 20 ms with telemetry broadcast every ~800 ms. The practical consequence for us is the tooltip gotcha: the HUD reads the *client's* copy of a block entity, so live V/I/temperature readouts are garbage unless explicitly synced — the plan is a throttled `mna-telemetry` channel (~0.5–1 s) rather than per-tick dirty-marking, which would spam chunk repackets.

This is standard game-networking vocabulary (also seen as "authoritative server"; contrast "client-authoritative" / "client prediction").

**Where it appears:** `docs/vintage-story.md` — chiseling (§ on chisel interaction, packet 1010, `BEChisel.cs:200,:224`), mechanical power (server-authoritative 20 ms mod tick, `MechanicalPowerMod.cs:92`), and Tooltips/Instruments (R15 sync gotcha).

### single-writer-per-island
Manatee-core's concurrency rule: at any moment, at most one thread may be mutating a given island (an electrically independent sub-circuit), and this is checked by debug assertions rather than enforced by locks.

All solver state is confined to its island, and islands are independently schedulable, so the host game — not the solver — decides which thread steps which island and when. The contract is: the client guarantees no two threads touch the same island's mutating API concurrently; in debug builds the library asserts this and crashes loudly on violation, while release builds pay zero synchronization cost. In exchange, the sim is deterministic per island regardless of how the client interleaves threads: same inputs, same tick sequence, bit-identical results. Islands joined by boundary couplers form one scheduling unit and substep in lockstep, so even their exchanges stay deterministic; committed solution reads are safe cross-thread via double-buffering. For an EE audience: it is like a rule that only one technician may have their hands inside a given cabinet at a time — no interlock hardware is installed, but the procedure is audited during commissioning (debug builds), and because cabinets share nothing, work in different cabinets can proceed in parallel without coordination.

**Project-coined phrasing**; closest standard concepts: the single-writer principle / external synchronization contract ("not thread-safe; caller synchronizes"), combined with shared-nothing partitioning for parallelism.

**Where it appears:** `docs/solver.md` (Threading and Allocation Contract); `docs/api.md` §1, §9, §11 `Step`, §21, and the tutorial's tick loop in `docs/integration-tutorial.md`.

### SkiaSharp
- The 2D drawing library the project uses to turn the tablet's display list into actual pixels, both on screen and in automated tests.

SkiaSharp is the .NET (C#) binding to Skia, Google's open-source 2D graphics engine (the renderer inside Chrome and Android). For an EE: it is the "output stage" of the rendering pipeline — the pure layers of the harness compute *what* to draw (a display list, essentially a parts list of shapes and text), and SkiaSharp is one interchangeable driver that converts that description into pixels. It serves two roles here: the canvas inside the Avalonia desktop harness app, and the headless rasterizer for the small secondary suite of pixel-snapshot tests (display list rendered with a bundled font, compared against golden PNGs — catching rasterizer-level regressions the text-based display-list snapshots cannot). Deliberately, it is added as a dependency only when the view layer starts, and the in-game Vintage Story renderer is a separate backend spot-checked for equivalence against the Skia output.
- Standard, real-world library name (`SkiaSharp` on NuGet, wrapping the Skia graphics library).
- **Where it appears:** `docs/harness.md` (Rendering Backends, Headless Testing, The Desktop Shell).

### slot
- The array-index part of a handle: the number that says *where* a component, node, or probe is stored inside the netlist's internal arrays.

Every Manatee handle is a 12-byte triple `(int Slot, uint Gen, ushort Net)`. The slot is the physical address — think of it as a socket position on a board: it identifies a storage location, not the part currently plugged into it. When a component is removed, its slot is freed immediately at commit and will later be reused for a new component; the generation (`Gen`) counter is what distinguishes the old occupant from the new one, so a stale handle to the freed slot fails fast instead of silently reading the wrong component. Slots also matter for persistence: `SaveCanonical` is slot-preserving (old handles line up with the reloaded arrays), whereas after `FromCanonical` probe slots are reissued and must be re-resolved by key. Deterministic outputs (e.g. `Verify` text) are ordered by slot.
- Standard term for this pattern's index field; the whole scheme is commonly called a *generational arena* / *slot map* (games/ECS literature).
- **Where it appears:** `docs/api.md` §3 (handles and keys), §16 (handle-survival table and free timing), §14 (slot-preserving serialization); `docs/integration-tutorial.md` §4.

### snapping
- Editor behavior that automatically aligns a component or wire being placed or dragged to the nearest grid point or connection terminal, instead of leaving it exactly where the pointer is.

In the schematic editor, snapping is one of the stateful concerns owned by the interaction state machine (layer 2 of the harness's three pure layers), alongside the active tool, drag-in-progress, selection, and hover. As the user drags, the state machine computes the snapped position from abstract pointer events and emits document mutations at the aligned coordinates — the document model itself never sees un-snapped positions. For an EE: it is the same behavior as any PCB/CAD tool where a part "jumps" onto the grid or a wire end "sticks" to a pin, so connections land exactly rather than almost. Because it lives in a pure, UI-free layer, snapping is testable headlessly by feeding synthetic pointer events and checking the resulting document.

This is the standard UI/CAD term; common aliases are "grid snap" and "snap-to-grid" / "snap-to-pin".

**Where it appears:** `docs/harness.md`, Layering, item 2 (Interaction state machine).

### Snapshot round-trip
- A CI test proving that saving and reloading a circuit's state loses nothing: a run that is snapshotted, restored, and stepped must match a run that was never snapshotted, bit-for-bit.

The test drives the solver to some point, takes a snapshot (serializes all dynamic state — see *snapshot/restore*), restores it into a fresh instance, steps both the restored and the original never-interrupted run forward, and compares results with exact floating-point equality, not a tolerance. Bit-for-bit is the strong form of the claim behind requirement R6 (lossless state serialization): if any state was omitted, rounded, or re-derived slightly differently on reload, the two runs diverge and the test fails. For an EE, the analogy is checkpointing a transient analysis mid-run and demanding the resumed waveform be indistinguishable from an uninterrupted one — any missing initial condition (a capacitor voltage, an inductor current, a source's phase) shows up as a discontinuity. In our docs it runs as a CI property test over generated circuits, alongside the reduction-equivalence and incremental-equivalence tests.

This is a standard testing idiom ("round-trip test" for serialization: encode → decode → compare) applied to solver state; the term itself is generic, not project-coined.

**Where it appears:** `docs/testing-strategy.md` (Equivalence Tests), `docs/design.md` R6, `docs/solver.md` (State, Limits, Probes — "Round-trip is a CI property test").

### SoA (struct of arrays)
- A memory layout where each field of a record is stored in its own parallel array, instead of storing whole records one after another ("array of structs", AoS).

If a component has fields `value`, `nodeA`, `nodeB`, AoS stores them interleaved per component; SoA keeps three separate arrays, all indexed by the same row number. The payoff is cache efficiency and allocation discipline: a loop that only touches `value` streams through one dense array with no wasted bytes, and the arrays are allocated once at commit time rather than as many small objects — which matters for requirement R8's no-allocation-per-tick rule. For an EE, the analogy is a data table organized by column instead of by row: to sweep one parameter across all components you read a single contiguous column, rather than skipping through every row. Manatee uses SoA for the engine's component tables and for the flat thermal-envelope pair storage (`Meta.SetThermalEnvelope`), where each component's 1..k `(rating, melt, tau)` pairs live in flat parallel arrays.

**Standard term:** yes — "structure of arrays" (SoA) vs. "array of structures" (AoS) is standard performance-engineering vocabulary, also central to data-oriented design and SIMD programming.

**Where it appears:** `docs/api.md` §12 (thermal envelopes: "flat SoA storage engine-side") and §21 allocation-bucket table ("SoA component tables").

### solver backend (interface)
- An abstraction boundary (`ISolverBackend`) separating "what linear system to solve" from "which algorithm/library solves it", so implementations can be swapped and cross-checked without touching the rest of the solver.

MNA reduces each circuit island to a system of linear equations, `Ax = b`. The backend interface — an Analyze/Factorize/Solve lifecycle proven in the solver benchmark experiments — is the plug socket that any linear-solve implementation fits behind. For an EE: it is like specifying a test fixture's connector so any of several instruments can be plugged in and their readings compared. The plan was settled with the interface present from day one, and the *roles flipped on benchmark evidence* (2026-07-05): an in-house KLU-style sparse LU beat both optimized dense and CSparse.NET at every island size tested, so it became the sole production backend; the naive dense LU survives as a test referee (CI cross-agreement), and CSparse.NET is a dev-time equivalence oracle that ships in no mod (which also keeps its LGPL license out of distribution).

**Standard term:** this is the standard *Strategy* / pluggable-implementation pattern applied to the linear solver; "backend" is common usage in numerical software.
- **Where it appears:** `docs/solver.md` Numerics (backend plan), `docs/design.md` doc-review round, `docs/api.md` §on `Manatee.Core.Solver` (`ISolverBackend`, internal), `docs/experiments/2026-07-05-backend-competition.md`.
- **References:** Gamma, Helm, Johnson & Vlissides, *Design Patterns* (Strategy); Davis, *Direct Methods for Sparse Linear Systems*, SIAM (the KLU/CSparse lineage the backends draw on).

### span / span validity window
A *span* (C# `Span<T>`/`ReadOnlySpan<T>`) is a temporary, zero-copy window onto a stretch of memory someone else owns; the *validity window* is the explicitly documented interval during which that view is safe to read.

For the EE: a span is like probing a live bus with a scope — you are looking directly at the owner's storage, not at a copy, so it is fast and allocation-free, but the reading is only meaningful while the owner isn't rewriting that storage. Manatee's API contract (binding, per §21) has three rules. First, no storable struct has a span *field* — spans cannot outlive a stack frame safely, so result structs (`EditReceipt`, `RestoreResult`, `FaultDiagnostic`, `DriftReport`) carry scalars plus "drain" methods that copy variable-length data into a buffer the *caller* supplies (e.g. `DrainLimitEvents(Span<LimitEvent>)`). Second, each span-*returning* member states its validity window: `Solution.RawVector` is valid until that island's next publish (and only while the island is not concurrently mid-Step — otherwise you can observe a mixed-generation vector); `IslandTable.Ids` is valid until the next structural commit or `Reconfigure`; `WaveformRing.Samples` is caller-owned memory, so the caller manages it. Third, never cache a span across a tick — copy scalars out. These rules are what let steady-state ticking allocate zero bytes while remaining memory-safe under Re-Volt's worker-thread model.

The term is standard .NET vocabulary (`System.Span<T>`, a *ref struct*); "validity window" is this project's name for the documented lifetime contract, akin to iterator-invalidation rules in C++ containers.

**Where it appears:** `docs/api.md` §21 (Threading, phases, spans, allocation — the binding "Span validity" paragraph), §6 (storable structs), §24.8 (design-rule recap); drain-style methods throughout the API surface.

**References:** Microsoft .NET documentation for `System.Span<T>` and `System.ReadOnlySpan<T>`; Stephen Toub, "Span<T>: Arbitrary memory in a strongly-typed, memory-safe way" (MSDN Magazine, January 2018).

### sparse voxel storage
Storing only the small cable-carrying cells of the Vintage Story world, keyed by position, instead of allocating an entry for every cell in the world grid.

Vintage Story's world is a huge 3D grid of voxels (small cubic cells), but cables occupy a vanishingly tiny fraction of it. A dense representation — one array slot per cell — would waste nearly all its memory on empty space; sparse storage keeps a map of only the occupied positions, like a dictionary keyed by coordinates. For the EE reader: it is the same economy as a sparse matrix — you record where the material *is*, not every place it could be. In Manatee this is the first stage of the VS client-intake pipeline: sparse voxel storage → greedy-meshed prisms → regions (the pipeline inherited from the sparky prototype), and three block representations (microblock-hosted cable voxels, dedicated cable block entities, decor-layer surface wiring) all feed this one intake before the compaction layer reduces the geometry to an electrical graph.

This is a standard technique; common aliases in graphics/game programming include *sparse voxel grid* and (for hierarchical variants) *sparse voxel octree*.

**Where it appears:** `docs/compaction.md` Client Intake Contracts (VS voxel world), `docs/vintage-story.md` (R17 intake).

### spec-driven
The testing principle that tests are written from the design documents (design.md, solver.md), never from reading the implementation — and if the spec is too unclear to write the test, the spec gets fixed first.

A test derived from the code can only confirm that the code does what the code does; a test derived from the spec checks that the code does what was *promised*. For the EE reader: it is the difference between verifying a circuit against its datasheet versus verifying it against a measurement of itself — the latter passes by construction and catches nothing. The clause "unclear spec ⇒ fix the spec first" makes ambiguity a first-class bug: rather than the test author guessing intent (and silently canonizing the guess), the design document is amended so both implementation and test trace to the same written contract. In Manatee this pairs with the sibling principles "regression tests first" and "trust nothing derived" (every stamp or companion model needs an oracle test), and with treating `docs/api.md` as the binding as-built surface.

This is a standard notion, closely related to *specification-based testing* / *black-box testing* in the testing literature; our phrasing adds the spec-repair obligation.

**Where it appears:** `docs/testing-strategy.md` Principles.

**References:** G. J. Myers, *The Art of Software Testing*, Wiley (specification-based/black-box testing).

### Split detection
- Figuring out, when a connection is removed, whether the network it belonged to has broken into two separate pieces — something Manatee deliberately does **not** do.

Islands (connected groups of components, each solved as one matrix) are tracked with a union-find data structure, which is excellent at answering "did these two groups just merge?" but structurally bad at the reverse question: union-find has no efficient "un-union". Detecting a split after a removal would require either a graph traversal or a fancier dynamic-connectivity structure. Manatee's decision is to skip the analysis entirely: any removal simply rebuilds the affected island from scratch, which is cheap at the island sizes both games produce (design.md R11: "split detection is not worth the complexity at our island sizes"). For the EE reader: cutting a wire *might* leave one circuit or two, and instead of reasoning about which, we re-derive the whole circuit's connectivity — the simulation result is identical either way; only the bookkeeping cost differs. This is a classic engineering trade: correctness by re-derivation instead of correctness by incremental maintenance, chosen because the incremental version is the hard direction.

The standard CS problem this sidesteps is **decremental/dynamic graph connectivity**; the incremental (merge-only) half is handled by the union-find (disjoint-set) structure.

**Where it appears:** `docs/solver.md` (Islands section — "split detection not attempted"), `docs/design.md` R5 and R11.

**References:** Cormen, Leiserson, Rivest & Stein, *Introduction to Algorithms* (disjoint-set forests, ch. 21 in the 3rd ed.); Tarjan (1975) "Efficiency of a Good But Not Linear Set Union Algorithm", *JACM* 22(2).

### splitmix64
- A small, fast, well-studied bit-scrambling function Manatee uses to turn a probe's geometric identity into a unique 64-bit key.

Reduction-owned probes need a stable identity that can be re-derived after a reload: the key is computed from `(SegmentKey, along, ordinal)` — which conductor segment, how far along it (quantized to 1e-9), and which of several co-located probes it is. Those inputs are folded through the splitmix64 *finalizer*, an "avalanche" mixer built only from shifts, XORs, and integer multiplies: flip one input bit and roughly half the output bits flip, so nearby placements get wildly different keys. Being integer-only, it is bit-identical on every runtime the core targets (the determinism rule — no floating point, no libm). For the EE reader: think of it as a deterministic scrambler, like a maximal-length LFSR's output stage — same input always gives the same output, but the mapping looks random. Two different probes could in principle scramble to the same key; that risk is *birthday-scale* in a 64-bit space (~2⁻³² even at 100k probes), and a collision surfaces as a loud debug duplicate-key throw at `AddProbe`, never as silent aliasing.

This is the standard splitmix64 mixer (the finalizer from Steele, Lea & Flood's SplittableRandom / Vigna's `splitmix64.c`), itself a variant of the MurmurHash3 finalizer; commonly used to seed xoshiro-family generators.

**Where it appears:** `docs/api.md` §13 (ProbeKey derivation), `core/Manatee.Core/Reduction/ConductorGraph.cs` (`ProbeKey` / `Mix64`).

**References:** Steele, Lea & Flood (2014) "Fast Splittable Pseudorandom Number Generators", OOPSLA 2014; Sebastiano Vigna's public-domain `splitmix64.c` reference implementation.

### Stamps versioned / fast path
An optimization (inherited from our earlier Sparky prototype) where the solver remembers what it stamped into the matrix last time, and if nothing changed, skips rebuilding the matrix entirely and reuses the cached factorization.

Every component "stamps" its contribution into the circuit matrix before a solve. We attach a version to those stamps: if the tuple (sparsity **pattern**, numeric **values**, timestep **dt**) is identical to the previous solve, assembly and factorization are both skipped and the solve drops straight to forward/back substitution on the cached LU factors — the cheapest tier (tier 1) in the change-cost table. For a programmer this is memoization keyed on the matrix's inputs: a cache hit bypasses the expensive build step. For an EE: a linear island under fixed-dt Backward Euler has constant companion conductances, so in steady operation it never leaves this path — which is the load-bearing performance fact behind subcycled AC. dt is part of the key because the companion-model conductances (e.g. G = C/dt) depend on it; changing dt silently would make the cached factorization wrong.

**Project phrasing** ("sparky's fast path"); the underlying technique is standard — separating symbolic analysis from numeric factorization and reusing factors when values are unchanged, as in KLU and SPICE-family simulators.

**Where it appears:** `docs/solver.md` — Change-Cost Tiers (implementation notes) and Numerics; the tier-1 row of the cost table.

**References:** Davis & Palamadai Natarajan (2010), "Algorithm 907: KLU, A Direct Sparse Solver for Circuit Simulation Problems", ACM TOMS; Nagel (1975), SPICE2 memo UCB/ERL-M520.

### State snapshot/restore
The ability to serialize everything in a circuit that evolves over time — capacitor voltages, inductor currents, per-device state — into bytes, and later load those bytes back so the simulation resumes exactly where it left off (requirement R6).

A circuit's *topology* (what is connected to what) is rebuilt from game data on load, but its *dynamic state* — the charge on every capacitor, the current through every inductor, a battery's state of charge, a fuse's partial melt — exists only inside the solver and would otherwise be lost. `Snapshot` writes that state per island as a versioned binary blob keyed by `StateKey`; `Restore` matches blob entries back to live state units by key. For an EE: this is capturing the circuit's initial conditions mid-run so integration can continue as if never interrupted. For a programmer: it is the memento pattern — the guarantee is lossless round-tripping, i.e. solve → snapshot → restore → step is bit-for-bit identical to never having paused (Law 4 in `docs/api.md` §14). Restore is *additive*: it only overwrites units the blob has entries for, which makes it composable when islands merged or split between save and load. Drives VS chunk pause/resume and Stationeers save/load (Stationeers never persists cable networks itself, so this hook is the only way state survives a reload).

**Standard term:** serialization / persistence of simulation state; the pattern is the *memento* (Gamma et al.).

**Where it appears:** `docs/design.md` R6 and System Overview; `docs/api.md` §14 (laws) and §11 (`Snapshot`/`Restore` on `IslandHandle`); `docs/stationeers.md` Persistence.

**References:** Gamma, Helm, Johnson & Vlissides, "Design Patterns" (Memento).

### steady state (per-tick)
- The normal operating regime of the solver once a circuit is built and running: the same tick repeated over and over, during which the code is required to allocate **no** heap memory (requirement R8).

In garbage-collected languages like C#, every object created at runtime ("allocation") is a debt the garbage collector later stops the world to reclaim; in a game running the solver 20+ times per second, per-tick allocations cause visible stutter. Manatee therefore splits its life into two regimes: **setup/rebuild** (loading a save, editing topology — allocation is fine, it happens rarely) and **per-tick steady state** (the repeated solve — zero bytes allocated after warmup). For an EE, this is the difference between commissioning a plant and running it: you may crane in new equipment during commissioning, but once the plant is on-line, every cycle must run with the parts already installed. "After warmup" means the first few ticks may still populate caches; the zero-allocation contract holds from then on and is enforced mechanically by BenchmarkDotNet's MemoryDiagnoser in CI, not by code review.
- Note the sibling entry **steady-state** (circuits sense): the two coincide in practice — a tick that stays in tiers 0–1 both allocates nothing and refactorizes nothing (tier 2 is by definition a numeric refactorization, though still allocation-free) — but this entry is the *memory* contract, that one is the *numerical-work* contract.
- **Where it appears:** `docs/design.md` R8; `docs/api.md` §16 (0B-after-warmup table); enforced via `AllocationSentinel` and CI benchmarks.
- **References:** Jones, Hosking & Moss, *The Garbage Collection Handbook* (CRC Press) for why GC pauses motivate allocation-free hot loops.

### Step(dt) / Tick
- The per-timestep advance of the simulation: `Step(dt)` tells the solver "time has moved forward by `dt` seconds, compute the new circuit state," and **tick** is the game-side name for the periodic heartbeat that makes that call.

Games run as a loop that fires at a fixed rate (Stationeers' power tick: every 0.5 s; the VS tablet: 0.05 s); each iteration is a *tick*. On each tick the integrator pushes updated inputs (source values, switch states) and then calls the solver's step, which performs one transient solve: storage elements (capacitors, inductors) are advanced by Backward Euler companion models using the previous state, so `dt` is the discretization timestep in the EE sense. The convention `dt <= 0` means "solve the DC operating point instead" — no time advance, capacitors open, inductors near-shorts — the same distinction as SPICE's `.OP` versus `.TRAN`. Because `dt` is fixed per profile, companion conductances never change tick-to-tick, which is what lets a quiet circuit stay in cheap tier-1 solves (see **steady-state**). For a programmer: `Step` is the state-transition function of a discrete-time state machine; the tick is the clock edge.
- Naming note: `Step(dt)` is the design-doc sketch (`docs/solver.md`); the as-built API (`docs/api.md`, binding) spells it `Netlist.Solve(in TickClock)` for the whole-netlist per-tick call and `IslandHandle.Step(in TickClock)` for the per-island, any-thread variant. Unrelated: devices may also expose a behavioral `Tick(dt)` hook for parameter updates (e.g. battery discharge curves) — same word, device layer.
- **Where it appears:** `docs/solver.md` (Netlist API, Analyses), `docs/design.md` (Performance Targets), `docs/api.md` §4 (`Solve`) and §11 (`IslandHandle.Step`).
- **References:** Nagel (1975), *SPICE2: A Computer Program to Simulate Semiconductor Circuits*, UCB/ERL memo M520 (transient analysis and companion models); Vlach & Singhal, *Computer Methods for Circuit Analysis and Design* (Van Nostrand Reinhold), ch. on numerical integration of circuit equations.

### StringTokenizer
- The Java standard-library class that circuitjs1 (Falstad) uses to chop each line of a saved circuit file into words, and whose choice of separator characters explains several quirks of the file format we must reproduce.

  A tokenizer is a text splitter: given a line and a set of delimiter characters, it yields the pieces between them — like cutting a sentence apart at every space. circuitjs1 tokenizes each dump line with the delimiter set `" +\t\n\r\f"` (`CircuitLoader.java:142`), which crucially includes `+`. That is a legacy of circuits being pasted out of URLs, where a space is encoded as `+`. The consequence Manatee inherits: a number written `1e+5` splits into two tokens and breaks parsing, so our exporter must never emit `+` in exponents. Element constructors also read their parameters positionally from the same tokenizer, with missing trailing tokens falling back to defaults — that try-a-token-or-default behavior is the format's entire versioning mechanism. For an EE: think of a bench instrument's remote-command parser that treats both space and a stray `+` as field separators — harmless until a number carries an explicit `+` sign.

  This is the standard Java class `java.util.StringTokenizer`; our docs use the name literally, to pin the exact upstream splitting behavior our importer must match.

- **Where it appears:** `docs/falstad-format.md` §1 (tokenization rules and the `+`-delimiter quirk) and §3 (positional parameter reading as the versioning mechanism); the importer's accept/reject contract in §7 follows from it.
- **References:** `java.util.StringTokenizer`, Java Platform SE API documentation (Oracle); Falstad/circuitjs1 source, `CircuitLoader.java`.

### stroke path
- A drawing command that renders the outline of a path — a sequence of connected line or curve segments drawn as a pen line of some width — as opposed to filling in the region the path encloses.

  In 2D graphics, most drawing reduces to two verbs over the same geometry: *stroke* (trace the path with a pen — used for wires, component outlines, scope traces) and *fill* (paint the enclosed area solid — used for solid shapes and highlights). In Manatee's 2D harness, the view layer emits a **display list**: a flat sequence of such draw commands (stroke path, fill, glyph run, ...) in schematic coordinates. Because the display list is plain data, it can be snapshotted, diffed in tests, and rasterized by any backend — the desktop harness or the Vintage Story in-game tablet. For an EE: a stroke path is the instruction "move the plotter pen along these points with the pen down," while a fill is "shade in this enclosed region"; the display list is the whole plotter tape.

  Standard graphics terminology (PostScript/PDF, HTML Canvas, SVG all use *stroke* vs. *fill* in exactly this sense).

- **Where it appears:** `docs/harness.md`, Layering item 3 (the view layer and its display list).
- **References:** Adobe Systems, *PostScript Language Reference Manual* (the origin of the stroke/fill painting model); the WHATWG HTML Canvas 2D specification (`stroke()`/`fill()` on paths).

### subclass / attached behavior / override / wrap
- Four object-oriented techniques for changing what a game-engine object does without editing the engine itself; the Vintage Story integration doc chooses among them per seam (per attachment point).

  **Subclass**: register our own class derived from the engine's, replacing the whole object — we inherit everything and change only what we override (e.g. our Alternator is a `BEBehaviorMPConsumer` subclass). **Attached behavior**: instead of replacing the object, bolt an add-on component onto the stock one — Vintage Story block entities carry a list of `BlockEntityBehavior` instances attached via JSON, each of which can hold state, save/load itself, and register tick listeners; this is Manatee's preferred first-class seam (all electrical semantics live on the block entity or an attached BE behavior). **Override**: within a subclass, supply a replacement for one specific method so the engine calls our version. **Wrap**: intercept a method but still call the original inside our replacement, adding logic before/after rather than substituting it — used e.g. for the farmland growth update under sun lamps. For an EE, the analogy is modifying a piece of bench equipment: subclassing is building your own variant of the instrument from the manufacturer's design; an attached behavior is a plug-in expansion card in a standard slot; an override is replacing one internal board; a wrap is splicing your circuit in series with the original board so signals pass through both.

  All four are standard OOP vocabulary ("wrap" corresponds to the Decorator pattern; "attached behavior" is the component pattern common in game engines — Vintage Story's own term is `BlockEntityBehavior`).

- **Where it appears:** `docs/vintage-story.md` §1 (the BE-behavior seam, subclass-vs-behavior trade-offs for chisel hooks) and §3 (sun lamps: override/wrap the farmland growth update via subclass or attached behavior).
- **References:** Gamma, Helm, Johnson & Vlissides, *Design Patterns: Elements of Reusable Object-Oriented Software* (Decorator, Template Method); Nystrom, *Game Programming Patterns* (Component pattern).

### SwitchToThreadPool / SwitchToMainThread
- A pair of UniTask calls (an async library used by Stationeers) that move the currently running code off Unity's main thread onto a background worker thread, and later back again.

`await UniTask.SwitchToThreadPool()` is a mid-function thread hop: everything after that line executes on a thread-pool worker, leaving the main thread free to render frames and handle input; `await UniTask.SwitchToMainThread()` hops back so results can safely touch game objects (Unity requires that only the main thread do so). For an EE, think of a technician stepping away from the control panel to run a long bench measurement, then returning to the panel to record the result — the panel (main thread) is never blocked, and only the technician at the panel may turn its knobs. Stationeers' own game loop uses exactly this pattern: `GameManager.GameTick` switches to the pool, runs atmospherics + electricity + logic sequentially on one worker, then switches back. Manatee reuses the same house idiom to offload expensive tier-3 island rebuilds: snapshot inputs, compute off-band on a pool thread, marshal results back via `SwitchToMainThread()` with a one-tick handoff. Note the pool gives no stable thread identity — a different worker may run each cycle, so nothing may key state to a thread ID.

These are standard UniTask APIs (Cysharp's UniTask library), not project-coined; the general concept is thread-affinity switching in async/await code.

**Where it appears:** `docs/stationeers.md` — Threading (verified against the game's decompiled `GameManager.cs`) and the Performance design-consequences paragraph.

### synthetic events
- Input events (pointer down/move/up, wheel, key) generated by test code rather than by a real mouse or keyboard, used to drive the schematic editor's interaction tests.

The schematic engine's interaction layer consumes *abstract* input events, never raw OS or widget-toolkit input. A test is therefore just a script of these events fed to that layer — "pointer down on the resistor palette, drag to (140, 60), release" — followed by assertions on the resulting document (a resistor exists, an undo entry was recorded). Because no display, window, or hardware is involved, the tests are deterministic and run headless in CI. For an EE: it is exactly like exercising a device with a signal generator and checking the output on a scope, instead of waiting for real-world stimuli — same interface, controlled stimulus, repeatable measurement.

The term is standard in UI testing; common aliases are "simulated events" and "programmatic events" (browser test tools like Playwright and React's event system use the same phrase).
- **Where it appears:** harness.md, Testing Model (interaction tests fed to layer 2, the interaction state machine).

### tesselation / OnTesselation / retesselate
The Vintage Story engine's mesh-baking step: converting a block's logical description into the triangles the GPU actually draws; `OnTesselation` is the engine callback where a block entity contributes its mesh, and "retesselate" means rebuild that mesh because the block's state changed.

Game worlds are drawn from *meshes* — lists of triangles in 3D space. Rather than rebuilding these every frame, Vintage Story bakes them once per chunk (a 32×32×32 block region) and redraws the baked result until something changes; think of it as compiling geometry ahead of time and caching the compiled output. For Manatee's voxel cable rendering, cable meshes are chunk-baked via the `OnTesselation` callback plus `mesher.AddMeshData`, and when a cable's visual state changes (e.g. it burns out) the code calls `MarkDirty(true)` to invalidate the cache and trigger a retesselation. An EE analogy: tesselation is like laying out a PCB from a schematic — a one-time expensive translation from the logical description to physical geometry — and retesselation is a re-spin triggered only when the schematic changes, not on every glance at the board. (Vintage Story spells it "tesselation" with one *l*; the usual English spelling is "tessellation".)

This is standard game-engine vocabulary (aliases: *meshing*, *mesh baking*, *chunk remeshing*); the specific `OnTesselation` name is Vintage Story API surface.

**Where it appears:** `docs/vintage-story.md` (Wire Rendering), with engine precedent in `third_party/vssurvivalmod/Systems/MechanicalPower/BlockEntityBehavior/BEBehaviorCreativeRotor.cs` (class `BEBehaviorMPCreativeRotor`: `OnTesselation` at line 60, `AddMeshData` at line 85, `MarkDirty(true)` at line 53). Note: `docs/vintage-story.md` cites this file as `BEBehaviorMPCreativeRotor.cs`, but the actual filename is `BEBehaviorCreativeRotor.cs`.

### text escaping / escape–unescape
circuitjs1's scheme for smuggling arbitrary text (labels, model names) through its space-and-`+`-delimited file format by replacing dangerous characters with backslash codes.

The Falstad text format splits each line into tokens on spaces (and treats `+` as a delimiter too), so any user-written text containing those characters would shatter into multiple tokens and corrupt parsing. The fix, implemented in `CustomLogicModel.escape/unescape`, rewrites: `\`→`\\`, newline→`\n`, space→`\s`, `+`→`\p`, `=`→`\q`, `#`→`\h`, `&`→`\a`, carriage return→`\r`, and the empty string becomes the sentinel `\0`. Unescaping is the exact inverse applied on load. It is used by `x` text annotations (when `FLAG_ESCAPE` is set), diode model names, switch labels, and `172` slider labels. An EE analogy: this is like putting a signal through a line code before transmission — certain bit patterns are forbidden on the wire (here, the delimiter characters), so the encoder substitutes safe patterns and the decoder restores the original exactly. Manatee's Falstad importer must implement the unescape side faithfully to accept real-world files.

This is a standard programming concept (aliases: *character escaping*, *quoting*); the specific substitution table is circuitjs1's own.

**Where it appears:** `docs/falstad-format.md` §4 ("Text escaping" paragraph, citing `CustomLogicModel.java:257-262`).

### Thread purity / pure C#
- Requirement R8's rule that manatee-core is plain, self-contained C# — depending only on the .NET base class library, with no calls into any game engine — so host games can safely run it on a background thread. "Pure C#" (no engine references) is the cause; "thread purity" (safe to run off the main thread) is the effect.

Game engines (Unity for Stationeers, Vintage Story's API) generally only allow their functions to be called from the main thread; code that calls them from elsewhere crashes or corrupts state. The usual reason code *must* run on the main thread is exactly that it calls engine APIs — so manatee-core touches nothing but its own data (no engine assemblies, no global services; it multitargets `netstandard2.1` for Unity/Re-Volt and `net8.0`), which makes it relocatable wherever the host chooses: Stationeers runs its entire simulation body, including the power tick that hosts Manatee, on a ThreadPool thread, and Vintage Story clients can dispatch to workers. R8 pairs this with allocation discipline (no memory allocation in the per-tick steady state), justified by exactly this deployment reality plus VS's tight frame budget. For an EE, the analogy is an isolated module: a sealed instrument with only its declared terminals and no hidden ground connection to the chassis — nothing inside reaches out to touch the bench, so you can mount it in any enclosure. Note this is about having *no external entanglements*, not about locking: the core is not claimed to be safe for simultaneous access from multiple threads.

**Project-coined term** ("thread purity"); the "pure C#" half is conventional .NET usage (a managed, dependency-free class library). Closest standard concepts: engine-independence, freedom from ambient dependencies, thread-affinity-free code.
- **Where it appears:** `docs/design.md` R8 and Performance Targets; `docs/api.md` §1 (multitargeting rationale); `docs/solver.md` opening ("game-agnostic, pure C#, no engine references").

### ThreadPool thread / worker thread / worker pool
- A background execution lane on which the solver runs — a thread supplied by the *game client* (manatee-core never creates or owns threads), used to solve independent islands concurrently. In Stationeers the entire game simulation (atmospherics, electricity, logic) runs on one such borrowed thread each cycle.

A "thread" is an independent line of execution; the .NET *ThreadPool* is a shared, recycled set of them that work is handed to rather than spawning a fresh thread each time, and a *worker pool* is that reusable crew. In Manatee the division of labor is strict: the client owns all scheduling, while the solver guarantees each island's state is confined to that island and results are deterministic regardless of which thread runs which island. The API is single-writer-per-island, enforced by debug assertions rather than locks; only fully independent islands run on separate workers (islands joined by boundary couplings substep in lockstep as one scheduling unit). Two decompile-verified facts about the Stationeers host shape the adaptor: `GameManager.GameTick` calls `UniTask.SwitchToThreadPool()` and then runs the whole self-clocked 500 ms simulation body sequentially on whichever pool thread it landed on — there is no dedicated power thread — and *thread identity is not stable*: each cycle may land on a different pool thread, so nothing may key state to a thread ID (no thread-local caches, no "am I on thread N" checks). Re-Volt's background worker thread is the strictest acceptance target, which also drives the zero-allocation steady-state contract. An EE analogy: the pool is a bank of interchangeable bench stations — your experiment gets *a* station each session, never a guarantee of the *same* one, so nothing may be left taped to a particular bench.

**Standard term:** thread pool / worker thread / background thread (standard .NET and OS terminology; see `System.Threading.ThreadPool`).
- **Where it appears:** `docs/stationeers.md` Threading (verified against the decompiled game, file:line cited there); `docs/solver.md` (Islands; Threading and Allocation Contract); `docs/design.md` R8 and the Stationeers/VS threading rows.
- **References:** Microsoft .NET documentation, `System.Threading.ThreadPool`; the UniTask library (Cysharp) provides `SwitchToThreadPool`; Goetz et al., *Java Concurrency in Practice* (Addison-Wesley, 2006) and Schmidt et al., *Pattern-Oriented Software Architecture, Vol. 2* (thread-pool patterns).

### three-phase contract / 3-phase contract
Re-Volt's per-`CableNetwork` power-tick lifecycle — the three method calls `Initialise` → `CalculateState` → `ApplyState` that the game invokes on every power network each tick, and into which manatee slots its own work.

**Warning: "phase" here means an execution stage, not an electrical AC phase.** The contract is a fixed calling sequence, like a checklist run once per tick per network: first `Initialise` (react to topology changes — Manatee maps this to tier-3 graph rebuild/patch), then `CalculateState` (gather each device's supply and demand — tier-1/2 stamp updates), then `ApplyState` (push the computed results back into the devices). For an EE, think of it as the three stages of one bench measurement: set up the fixture, take the reading, write it in the log — always in that order, once per timestep. Re-Volt intercepts these calls via a Harmony-injected `RevoltTick : PowerTick`, and Manatee replaces the arithmetic between `CalculateState` and `ApplyState` with a real MNA solve. Note the doc's revision banner: the per-network framing is partly superseded by api.md §22.a's one-global-tick-body model — a single global solve cannot literally be hosted inside each network's three calls, so where they disagree, api.md is binding.

**Project/Re-Volt-coined term** — no standard equivalent; closest programming concept: a template-method lifecycle (setup → compute → commit). Deliberately unrelated to electrical three-phase power.

**Where it appears:** `docs/stationeers.md` (Integration Seams; revision-pending banner), `RevoltTick.cs` in Re-Volt's source.

**References:** Gamma, Helm, Johnson & Vlissides, *Design Patterns* (1994) — Template Method, the pattern this lifecycle instantiates.

### Tiered JIT recompilation
- A .NET runtime behavior where frequently-executed methods are automatically recompiled at a higher optimization level while the program is running.

.NET first compiles methods quickly with minimal optimization (tier 0), then, if a method proves hot, recompiles it in the background with full optimization (tier 1). This background compilation allocates memory on runtime-owned threads, process-wide and at unpredictable times. In Manatee it matters only as a measurement hazard: the zero-allocation CI gates read a per-thread allocation counter, and tiered recompilation (like background GC) can perturb that counter after warmup, producing small phantom deltas in a step that truly allocated nothing. The docs' conclusion is that serializing tests reduces but cannot eliminate this noise, so the gates use a min-over-N-sub-runs pattern instead of single-shot assertions. An EE analogy: it is like an instrument whose reference oscillator occasionally retrims itself mid-measurement — you cannot schedule around it, so you take the minimum over repeated readings.
**Standard term:** tiered compilation (also "TieredPGO" / adaptive optimization in .NET documentation).
- **Where it appears:** `docs/testing-strategy.md` (Benchmarks — flake isolation for the zero-alloc gate).
- **References:** Microsoft .NET runtime documentation on tiered compilation; Aycock (2003), "A Brief History of Just-In-Time", ACM Computing Surveys 35(2), for JIT compilation generally.

### Token delimiter
- A character that a parser treats as a boundary between the meaningful chunks ("tokens") of a line of text, rather than as part of any token.

When reading a text file, a tokenizer walks each line and splits it wherever it sees a delimiter character — like reading a comma-separated parts list, where the commas separate the fields but are not themselves data. In the Falstad/circuitjs1 file format that Manatee's importer must accept, the delimiter set is `" +\t\n\r\f"` (Java `StringTokenizer`, `CircuitLoader.java:142`) — space, tab, newline, carriage return, form feed, and, unusually, the plus sign. The `+` is a legacy of circuits being pasted out of URLs, where a space is encoded as `+`. The practical consequence documented in our spec: a number written `1e+5` splits into two tokens and breaks parsing, so exponents must never be written with an explicit `+` — circuitjs itself never emits one, and neither may we.
**Standard term:** yes — also called a separator; the `+`-in-the-set behavior is a quirk of this specific format, not of tokenizing generally.
- **Where it appears:** `docs/falstad-format.md` §1 (tokenization rules) and the sharp-edges list in §"accept/reject contract" (item 4).
- **References:** Aho, Lam, Sethi & Ullman, *Compilers: Principles, Techniques, and Tools* (lexical analysis, ch. 3).

### transient view state
- Short-lived visual state produced by the schematic editor's interaction layer — the hover highlight, the drag preview, the snapping guide — which the renderer needs this frame but which is never saved as part of the document.

In the harness's three-layer design, the interaction state machine consumes abstract input events (pointer down/move/up, wheel, key) and emits two kinds of output: *document mutations*, which permanently change the schematic and go through undo/save, and *transient view state*, which merely tells the view layer what to draw right now. The split is an invariant: anything transient can be discarded at any moment (close the tablet mid-drag and nothing is corrupted), and the saved document never contains half-finished gestures. Here "transient" is the ordinary English/UI sense — ephemeral — not the circuit-analysis sense elsewhere in this glossary. Physical analogy for an EE: it is the difference between the pencil marks on your working sketch (the drag preview) and the finished schematic you file — or between a meter's live needle position and the logged reading; only the latter persists.

**Standard term:** common UI-architecture usage — often called "ephemeral state" or "UI state" as opposed to persisted "application/document state" (the same distinction MVC-family and unidirectional-data-flow architectures draw).

**Where it appears:** harness.md, Layering, layer 2 (interaction state machine): "Output is document mutations plus transient view state."

### tree attributes
- Vintage Story's built-in serialization structure: a nested key-value tree the engine uses to save and network block-entity state, which Manatee rides for persisting cable data.

A tree attribute is a dictionary whose values are typed primitives (ints, floats, strings, byte arrays) or further nested dictionaries — structurally like JSON, but binary and engine-native. For an EE, think of it as the standardized connector and pinout for saved state: any block entity that writes its state into this structure gets persistence to disk and client-server sync for free, without inventing its own format. Relevant to Manatee: the vanilla microblock block entity serializes its geometry as two tree attributes, `"cuboids"` (the greedy-merged voxel cuboid list) and `"materials"` (the block-id palette) — verified at `BEMicroBlock.cs:1201`. Sparky's block-entity format was a reimplementation of exactly this structure, so the two formats are 1:1 compatible.

This is standard Vintage Story API vocabulary (`ITreeAttribute` in `vsapi`), not a Manatee coinage; the closest general concepts are a serialized nested dictionary / property tree (cf. JSON, NBT in Minecraft).

**Where it appears:** `docs/vintage-story.md` §1 (Engine facts, Storage), with file:line references into `third_party/vssurvivalmod/` and `third_party/vsapi/`.

### TrueSpeed / TargetSpeed / shaft speed
- Vintage Story's mechanical-power API properties for how fast a rotating shaft network is actually turning (`TrueSpeed`) versus how fast a rotor is trying to drive it (`TargetSpeed`) — the seam where Manatee's electrical simulation couples to the game's mechanical one.

VS simulates windmills, gears, and shafts as networks with a momentum/drag model; each network integrates a single speed from summed torque and resistance. For the **alternator** (a mechanical consumer), Manatee reads `TrueSpeed` (`BEBehaviorMPConsumer.cs:23`) to derive electrical frequency and EMF — the engine integrates shaft angle with ω = 5·speed rad/s, giving f ≈ 0.8 × speed × polePairs Hz, with pole-pair count as the honest (hand-smithed, no fudge factor) knob — and reports counter-torque back via `GetResistance()`. For the **motor** (a mechanical producer), electrical input drives the `TargetSpeed` / `TorqueFactor` / `Resistance` virtuals on `BEBehaviorMPRotor`. For an EE: this is exactly the electromechanical coupling of a real machine, except the two simulations run on different clocks (~100 ms mechanical vs. faster electrical ticks), so shaft speed is treated as piecewise-constant per electrical tick and counter-torque is returned as a mean over all electrical substeps since the last mechanical read — a normative rule that prevents the 2f power pulsation of single-phase AC from aliasing into a phantom DC torque.

These are standard Vintage Story engine identifiers (not Manatee coinages); "shaft speed" corresponds to rotor/shaft angular velocity in machine theory.

**Where it appears:** `docs/vintage-story.md` §2 (Alternator/Motor, Cadence consequence, Frequency arithmetic), with file:line references into `third_party/vssurvivalmod/` (the mechanical-power subsystem lives in `Systems/MechanicalPower/`).

**References:** Fitzgerald, Kingsley & Umans, *Electric Machinery*, for frequency = pole pairs × mechanical speed and single-phase power pulsation.

### TryResolve(key, out ...)
- The universal recovery call: given a component's permanent `ExternalKey`, it looks up that component's *current* live handle, returning `false` (instead of crashing) if nothing with that key exists right now.

Handles (`ResistorId`, `NodeId`, ...) are fast but perishable — an island rebuild invalidates them (see *handle survival*). Keys are the opposite: slow-path but immortal. The `Try...` prefix is a standard C# naming convention meaning "this may fail, and failure is reported as a `false` return value plus an unset `out` parameter, never as a thrown exception." So the recovery loop for anything you cache is: keep the key forever, and whenever handles may have died, call `TryResolve(key, out var c)` to get fresh ones — this is exactly what the tutorial's `Repin` does. Sibling overloads cover the other handle families: `TryResolveNode`, `TryResolveCoupler`, and `TryResolveProbe` (the probe re-resolution path). All are cost-tier 0 (pure reads, no structural effect), so recovery can never trigger the rebuild it is recovering from. For the EE: it is like looking a part up by its schematic reference designator (R17) after the board has been re-laid-out — the physical location changed, the designator did not.

**Standard term:** the C# **Try-Parse pattern** (a.k.a. Tester-Doer alternative); the key→handle map itself is an ordinary dictionary lookup.
- **Where it appears:** `docs/api.md` §Netlist reads (`TryResolve`/`TryResolveNode`/`TryResolveCoupler`/`TryResolveProbe`) and the handle-survival table (§17); `docs/integration-tutorial.md` §3, §4 (`Repin`), §6, and the sharp-edges list ("Keys survive everything").
- **References:** Cwalina & Abrams, *Framework Design Guidelines* (Addison-Wesley) — the Try-Parse pattern.

### Typed IDs
- The pattern of giving every kind of entity its own distinct handle type — a `ResistorId` cannot be passed where a `CapacitorId` is expected — so misuse is impossible to even write.

Carried forward from sparky (our earlier VS prototype, whose *design* is a reference even though its code is not reused), and realized in manatee-core as generational typed handles: each ID is a small struct carrying `(Slot, Gen, Net)` — an array index, a generation counter that detects reuse of a slot after deletion, and a netlist identifier that catches handles from the wrong document. Cross-kind misuse fails at *compile time*; stale or cross-netlist use fails fast at runtime (`StaleHandleException` in debug). For an EE: it is like keying every connector differently — a probe lead physically cannot plug into a power socket, so a whole class of wiring mistakes is prevented before power-on rather than diagnosed after. The compiler plays the role of the connector keying.

**Standard term:** strongly-typed identifiers / handles; the `(Slot, Gen)` part is the generational-index (slot-map) pattern common in game engines and ECS designs.
- **Where it appears:** design.md (Non-Goals: sparky's design as reference), solver.md (netlist API), api.md §2 (handle definitions and failure-mode table §21).

### Typed IDs / typed component handles
- Strongly-typed opaque identifiers (`NodeId`, `ResistorId`, `VSourceId`, `CapacitorId`, ...) that the netlist's `Add*` methods return, and that you must present back to name that component later.

Each kind of component gets its own ID *type*, not just a shared integer. That means the compiler itself rejects mistakes like passing a resistor's handle to `SetSourceValue` — the error is caught before the program ever runs ("misuse fails to typecheck"), a pattern carried over from the sparky prototype. For the EE reader: think of it as each component having a uniquely-keyed connector on its wiring harness — a resistor's plug physically cannot seat in a voltage-source socket, so miswiring is impossible rather than merely detectable. "Opaque" means the client never looks inside the value; it is a claim ticket, but a *generational* one: it stays valid only until the next island rebuild (any topology change kills it at the following Solve), and it is not persisted across save/load. Long-lived identity lives elsewhere — the client-stable `ExternalKey`/`StateKey` survives rebuilds and reloads, and is what you use to re-resolve fresh typed handles afterward. Caching a typed handle across a rebuild is exactly the stale-handle bug the integration tutorial warns against.
- **Standard term:** this is the standard *strongly-typed identifier* / *newtype* / *type-safe handle* idiom; also seen as "phantom-typed IDs."
- **Where it appears:** `docs/solver.md` (The Netlist API, Layering); the `Add*`/`Set*`/`Remove` surface in `docs/api.md`; handle-survival rules in `docs/integration-tutorial.md`.

### Undo entry
- One recorded, reversible edit in the schematic document's undo history — the unit that a single "undo" keypress rolls back.

Every user-visible edit to the harness's circuit document (place a resistor, drag a wire, delete a component) pushes an entry onto an undo stack: a record with enough information to restore the document to its prior state. Our interaction tests assert on these — a scripted test drives synthetic pointer events ("pointer down at the resistor palette, drag, release") and then checks both that the resistor exists in the document *and* that exactly one undo entry was recorded, catching edits that mutate state without being undoable. For the EE reader: it is like a strip-chart recorder on the editing session — each pen mark is one operation, and you can rewind the chart one mark at a time to reproduce any earlier state.
- **Standard term:** standard usage; the mechanism is the classic *undo stack* (often implemented via the Command or Memento pattern).
- **Where it appears:** `docs/harness.md` (Testing Model — interaction tests assert "a resistor in the document and an undo entry").
- **References:** Gamma, Helm, Johnson & Vlissides, *Design Patterns* (1994) — Command and Memento patterns, whose motivating example is undoable operations.

### Union-find
- A classic algorithmic data structure that groups items into sets and answers "are these two in the same set?" almost instantly; Manatee uses it everywhere connectivity must be tracked as things merge.

It supports two operations: *union* (merge the sets containing two items) and *find* (which set does this item belong to?), both effectively constant-time. For the EE reader: it answers continuity-tester questions — "is there a conductive path between these two points?" — without re-tracing the whole circuit each time; touching two nets together is one `union`, and any later continuity check is one `find`. Manatee uses it at several granularities: the compaction layer unions equipotential conductor geometry (voxels, cable segments) into single electrical nodes; the islands layer maintains a union-find over the netlist so each connected component becomes one island (one matrix, one factorization); and on the Stationeers side, islands are a union-find over vanilla `CableNetwork`s joined by closed couplers (breakers, bus-ties). One asymmetry matters: merging is incremental and cheap, but union-find cannot efficiently *split* — so removals invalidate and rebuild the affected island rather than attempting split detection (design.md R11).
- **Standard term:** *disjoint-set union (DSU)*, also called the *union-find* or *merge-find* data structure.
- **Where it appears:** `docs/compaction.md` (Responsibilities #1 region building, #5 island bookkeeping), `docs/solver.md` (Islands), `docs/stationeers.md` (Islands and Coupling Devices), `docs/design.md` (Stationeers integration), `docs/api.md` §19 intake.
- **References:** Cormen, Leiserson, Rivest & Stein, *Introduction to Algorithms* (CLRS), ch. "Data Structures for Disjoint Sets"; Tarjan (1975), "Efficiency of a Good But Not Linear Set Union Algorithm", *JACM* 22(2).

### UnityMainThreadDispatcher.Enqueue
- A Unity mechanism for handing a callback from a background thread to the game's main thread: you enqueue a piece of work, and the main thread runs it on its next pass.

Unity's engine objects may only be touched from the main thread, so any computation done off-band must deliver its results through a hand-back mechanism. `UnityMainThreadDispatcher.Enqueue(callback)` appends the callback to a thread-safe queue that the main thread drains each frame — a producer/consumer queue where the consumer is the game loop itself. In `docs/stationeers.md` it is listed as the alternative to UniTask's `SwitchToMainThread()` for marshaling Manatee's background solve results (rebuilt factorizations, load-time rebuilds) back into the simulation; the game's own `SetPowerFromThread` pattern does the same job. For the EE reader: it is an in-tray on the operator's desk — field crews (background threads) cannot touch the control panel directly, so they drop work orders in the tray and the one operator (main thread) executes them in sequence on their next round, guaranteeing no two hands are on the panel at once.
- **Standard term:** standard Unity-community pattern, the *main-thread dispatcher* — a concrete instance of the general message-queue / event-loop marshaling idiom (compare `Control.Invoke` in WinForms or `Dispatcher.Invoke` in WPF).
- **Where it appears:** `docs/stationeers.md` (Performance / threading design consequences), alongside `UniTask.SwitchToMainThread()` as the two sanctioned marshaling routes.

### Verify / Verify golden
- A .NET snapshot-testing library: a deterministic text artifact is saved as a "golden" file the first time (on first run the test fails and Verify writes a `.received` file; a human accepts it, turning it into the stored `.verified` file), and later runs fail with a reviewable diff if the output no longer matches.

We use Verify for goldens whose value is *stability of a rendered artifact* rather than a computed number: the tablet view layer's display lists, and — most importantly — the SPICE decks our test-only translator emits for ngspice (`SpiceDeck.Emit`, api.md §22.c: `await Verify(deck.Text)`, `DeckResult.Text`). The deck text uses deterministic, slot-ordered names, so the exact same circuit always serializes to the exact same text. The payoff is sequencing: a stamp refactor that changes how a component is written into the SPICE deck appears as a *reviewable text diff before it becomes an oracle delta* — you see "the deck changed, here is how" in code review instead of first discovering "ngspice now disagrees by 0.3%" and reverse-engineering why. Display-list snapshots via Verify are the *primary* visual golden for the tablet, deliberately preferred over pixel comparisons, which are kept as a small secondary suite because font/GPU nondeterminism makes pixel goldens brittle. (Lesson-corpus values are checked separately, by tolerance comparison against the manatee solution rather than by snapshot.) For an EE: it is like keeping a signed-off reference oscillogram in the drawer — every production run is compared against it, and any deviation forces a human to look before the reference is re-stamped.

The name is standard — the actual open-source library (VerifyTests, `Verify` on NuGet, an evolution of the approval-testing lineage exemplified by ApprovalTests.Net); the general technique is snapshot / golden-file / approval testing.
- **Where it appears:** `docs/api.md` §22.c (`await Verify(deck.Text)`, `DeckResult.Text`); `docs/testing-strategy.md` (Toolchain: frameworks); `docs/harness.md` (Testing Model: display-list snapshots); `docs/design.md` R20 for the corpus it runs over.

### Vestigial
- Code that still exists and even runs, but no longer does anything useful — left behind by a refactor, like a vestigial organ.

In our docs the word appears in the verified threading analysis of Stationeers: the game's old `ThreadedManager`-based Electricity and Atmospherics worker threads are still spawned at startup, but neither overrides `ThreadedWork()`, so they run an empty work loop forever. The *actual* simulation moved into a single sequential body on `GameManager.GameTick`. This matters for integration: a naive reading of the class names would suggest power runs on its own thread, when in fact the threads are decorative. For an EE: it is the abandoned wiring still bolted into the panel after a plant retrofit — energized-looking, connected to nothing, and dangerous mainly because it misleads whoever reads the schematic next. Standard usage; related terms are "dead code" (never executed) — vestigial code is slightly different in that it *does* execute, just to no effect.
- **Where it appears:** `docs/stationeers.md` § Threading (verified against the decompiled game source, with file:line references).
- **References:** Fowler, *Refactoring: Improving the Design of Existing Code*, catalogs removal of such leftovers (e.g. the "Remove Dead Code" refactoring).

### View layer
- Layer 3 of the tablet/harness architecture: the pure function that turns the schematic document plus the current interaction state into a **display list** — a flat sequence of draw commands (stroke path, fill, glyph run, ...) in schematic coordinates.

The key property is that the view layer's output is *data*, not pixels: the display list can be snapshotted, diffed in tests, and handed to any rasterizer backend (SkiaSharp on desktop and in headless pixel tests, the Vintage Story GUI in-game). The layer below it (the interaction state machine) never touches drawing, and the view layer never touches input — each layer has one job and a testable boundary. For an EE: think of it as the stage that produces a plot file or Gerber — a complete, device-independent description of what to draw — while separate "printers" (backends) turn that description into ink. Because the description is inspectable, most rendering regressions are caught as text diffs on the display list, without rendering a single pixel.

The term is standard (as in model–view separation / MVC's "view"); our specific realization — view emits a retained display list rather than drawing immediately — is the classic display-list / retained-mode rendering pattern.
- **Where it appears:** `docs/harness.md` (Layering, item 3; Testing Model; Rendering Backends).
- **References:** Gamma, Helm, Johnson & Vlissides, *Design Patterns* (the MVC discussion in the introduction); display lists are a long-standing computer-graphics structure, described in Foley, van Dam, Feiner & Hughes, *Computer Graphics: Principles and Practice*.

### VoxelCuboids
- VoxelCuboids is the Vintage Story engine's storage field for microblock geometry: a list of merged rectangular boxes, not a full 16³ grid of individual voxels.

Concretely it is a `List<uint>` on `BlockEntityMicroBlock` (BEMicroBlock.cs:98) where each 32-bit integer encodes one cuboid: bits 0–11 are the min x/y/z corner, bits 12–23 the max−1 corner, and bits 24–31 an index into a per-block material palette (up to 256 materials). "Greedy-merged" means adjacent same-material voxels are coalesced into the fewest boxes — a compression scheme, like run-length encoding generalized to 3D. For the EE: this is the raw geometry database Manatee's extraction layer scans to discover which conductor voxels touch which, i.e. the layout from which the netlist is extracted. Sparky's block-entity format reimplemented exactly this structure, so the two are 1:1 compatible.

Standard concept: bit-packed bounding boxes produced by greedy meshing/merging (a common voxel-engine compression technique); the name `VoxelCuboids` is the engine's own identifier.

**Where it appears:** vintage-story.md sec 1 (Storage; bit layout with file:line references, encode `ToUint` :1229, decode `FromUint` :1247); the extraction layer in vintage-story.md scans it to build the electrical graph.

### WAILA / GetPlacedBlockInfo / GetBlockInfo
- "WAILA" ("What Am I Looking At") is the modding-community name for the hover tooltip showing information about the block under the player's crosshair; `GetPlacedBlockInfo`/`GetBlockInfo` are the Vintage Story engine hooks that fill it.

The engine calls `Block.GetPlacedBlockInfo` (vsapi Block.cs:2268), which delegates to `BlockEntity.GetBlockInfo(forPlayer, StringBuilder)` (:2279); Manatee overrides the latter on its block entities/behaviors to append live voltage, current, and temperature lines — this is the basic instrument of design.md R15 (readings visible before you can afford a multimeter). For the EE: it is the panel meter you get by just looking at a component. One gotcha documented in vintage-story.md: the tooltip is rendered from the *client's* copy of the data, so values must be network-synced — Manatee plans a throttled telemetry channel (~0.5–1 s) rather than marking the block dirty every tick, which would spam full chunk updates. "WAILA" originates as the name of a Minecraft mod; here it is used generically for the hover-info mechanism.

**Where it appears:** vintage-story.md sec "Tooltips and Instruments (R15)" (with engine file:line references); design.md R15.

### warmup
- Warmup is the initial phase of running a circuit — construction and the first ticks — during which the solver is still allowed to allocate memory; the zero-allocation guarantee applies only to steady-state ticking *after* warmup.

Manatee-core promises that steady-state ticking (change-cost tiers 0–2) allocates zero heap bytes (design.md R8, solver.md "Threading and Allocation Contract"): once a circuit's shape is settled, every tick reuses pre-sized buffers, so the garbage collector never pauses the game mid-simulation. Allocation is legal only during warmup and at structural-commit ("shape") time — api.md's member annotations distinguish `0B` (zero bytes after warmup) from `shape` and `cold` paths. The boundary is made explicit and machine-checkable: `Netlist.EnterSteadyState()` returns a `SteadyStateGuard` that bars structural edits and arms an allocation tripwire, and BenchmarkDotNet's MemoryDiagnoser enforces the guarantee in CI with no exemptions. For the EE: warmup is like an instrument's settling time after power-on — transient behavior is tolerated briefly, then the steady-state spec applies strictly.

Standard usage: matches the common performance-engineering sense of "warmup" (JIT compilation, cache/pool filling before measurement); our specific contract-boundary use is project-defined.

**Where it appears:** solver.md "Threading and Allocation Contract"; api.md §1 (notation: `0B` = zero heap allocation after warmup), §8 (SteadyStateGuard/AllocationSentinel), §21 allocation table; enforced by the benchmark suite (`scripts/bench.sh`).

### watchdog / deadline
A safety timeout that would forcibly abort or skip a computation that runs too long — which Stationeers' simulation loop explicitly does **not** have, a fact Manatee's integration budget is built around.

In general software, a *watchdog* is a supervisor that kills or restarts work that exceeds a time budget, and a *deadline* is that budget — think of a lab power supply's over-current trip, but for CPU time instead of amps. Our reading of the Stationeers decompilation found the whole simulation (atmospherics, then electricity, then logic) runs sequentially inside one self-clocked 500 ms loop with **no watchdog and no deadline**: if one tick takes too long, nothing aborts it — the entire cycle simply stretches, delaying everything sharing that loop body. Exceptions are caught and logged, and the loop continues. The design consequence for Manatee is that a slow solve isn't clipped or preempted, it silently degrades the whole game's tick rate — so per-tick solver cost must be kept small in *absolute* terms (microseconds for steady-state tiers), not merely small relative to the 500 ms budget.

Both terms are standard systems vocabulary (aliases: watchdog timer, timeout, time budget).

**Where it appears:** `docs/stationeers.md` (Threading section, "No watchdog, no deadline" finding with decompile file:line evidence, and the Design Consequences that follow).

**References:** the watchdog-timer concept is standard embedded/OS practice; see e.g. Tanenbaum & Bos, *Modern Operating Systems*, on timers and timeouts.

### widget toolkit
- A software library that supplies ready-made on-screen controls — buttons, sliders, text boxes, canvases — and delivers the user's mouse/keyboard input to them.

In the 2D harness design, the three pure layers (document model, interaction state machine, view/display-list layer) are forbidden from touching any widget toolkit. Native input is adapted into abstract events (pointer down/move/up, wheel, key) before it reaches the interaction state machine, and rendering output is a data-only display list rather than toolkit draw calls. This keeps the whole tablet stack headless-testable and lets the same code run under two very different hosts: the Avalonia desktop shell and the Vintage Story in-game tablet. The desktop shell's toolkit (Avalonia) is deliberately outside the tested stack — nothing in `Manatee.Schematic` may reference it. For an EE: the toolkit is like the front panel of an instrument — knobs, display glass, connectors — while the pure layers are the measurement circuitry inside; we design the circuitry so any front panel can be bolted on, and we test the circuitry at its terminals without needing a panel at all.
- **Standard term:** UI framework / GUI toolkit (examples: Avalonia, Qt, GTK, WinForms).
- **Where it appears:** `docs/harness.md` (Layering item 2, The Desktop Shell).

### ZeroAlloc collection / DisableParallelization
- An xUnit test grouping (`ZeroAllocCollection`, marked `DisableParallelization = true`) that forces all zero-alloc gate tests to run one at a time instead of concurrently with other tests.
- xUnit, the project's test framework, normally runs tests in parallel on multiple worker threads — like running several bench experiments simultaneously on a shared power supply. That sharing corrupts the zero-alloc measurement: promotion-heavy sibling tests on other threads trigger concurrent compacting garbage collections, which make the per-thread allocation counter over-report on the measured thread. Placing every alloc-gate test in this serialized collection removes that one noise source. Crucially, the docs pin this as **best-effort noise reduction, not the correctness mechanism** — process-wide background GC and tiered-JIT activity outlive any collection boundary, so soundness still comes from the min-over-N-sub-runs pattern (see *allocation discipline / zero-alloc gate*), and serialization merely makes clean sub-runs more likely.
- Standard term: standard xUnit features (`[Collection]` attribute, `CollectionDefinition(DisableParallelization = true)`); the name `ZeroAlloc` is ours.
- **Where it appears:** `docs/testing-strategy.md` Benchmarks, "Flake isolation for the zero-alloc gate" — required reading before adding a ZeroAlloc test.
- **References:** xUnit.net documentation on test collections and parallelization.
