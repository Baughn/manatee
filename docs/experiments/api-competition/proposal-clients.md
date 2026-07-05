# Manatee-core Netlist API — "Four Verbs" Proposal

**Lens: client-author ergonomics.** The two integrators (Re-Volt's author on a game
worker thread; the future VS layer) should be unable to *accidentally* do something
expensive, and able to *read the cost of any call off its name and receiver type*
before ever profiling. The design principle throughout: **cost tier = verb**, and the
expensive tiers require holding an object you can only get by asking for it loudly.

## 0. The cost model as a vocabulary

Every mutation in the API is one of four verbs. This is R4 promoted from doc-comment
convention to API grammar:

| Verb | Tier (solver.md) | Solver cost | Where it lives |
| --- | --- | --- | --- |
| `Drive` | 1 — RHS only | back-substitution on cached LU | `Netlist` / `DeviceTickContext` — always legal |
| `Adjust` | 2 — conductance | numeric refactor, symbolic reused | `Netlist` / `DeviceTickContext` — always legal |
| `Reconfigure` | 3-lite — island membership | coupler toggle: incremental merge / coalesced split-rebuild | `Netlist` only — barred inside `EnterSteadyState` |
| *(structural)* | 3 — topology | full island rebuild, batched | **only exists on `StructuralEdit`**, obtained via `netlist.Edit()` |

`Reconfigure` exists because the relay-vs-breaker duality (solver.md, settled
2026-07-05) puts one tier-3 operation — coupler open/close — in the *runtime-normal*
path. Forcing it through `Edit()` would teach clients that opening an edit scope
mid-tick is normal; giving it its own verb keeps `Edit()` meaning "construction".

## 1. C# API sketch

Targets `netstandard2.1` (Re-Volt/Unity 2022.3) — everything below is ns2.1-legal
(`Span`/`IBufferWriter` via System.Memory; `record struct` via LangVersion).

### 1.1 Handles (`Manatee.Core`)

```csharp
// Readonly structs over (index, generation). Cross-netlist or stale use is a debug
// assert; misuse across component kinds fails to compile (sparky's typed-ID pattern).
public readonly record struct NodeId;
public readonly record struct ResistorId;   public readonly record struct VSourceId;
public readonly record struct ISourceId;    public readonly record struct CapacitorId;
public readonly record struct InductorId;   public readonly record struct SwitchId;   // relay contact
public readonly record struct DiodeId;
public readonly record struct CouplerId;    // island-coupling device: breaker, decoupling xfmr, converter
public readonly record struct IslandId;     public readonly record struct ProbeId;

// Client-chosen stable identity (Stationeers RefId, VS block pos hash). Snapshots key
// on this, never on internal ids — internal ids do not survive rebuilds; StateKeys do.
public readonly record struct StateKey(ulong Value);
```

### 1.2 The netlist

```csharp
public sealed class Netlist
{
    public Netlist(in SolverProfile profile);          // analysis regime + numerics policy, fixed at birth

    // ---- Tier 1: Drive — RHS only. Per tick/substep. Zero-alloc. ----
    public void Drive(VSourceId id, double volts);     // source value
    public void Drive(ISourceId id, double amps);

    // ---- Tier 2: Adjust — conductance. Numeric refactor of the island; symbolic reused.
    //      No-ops (and stays tier-free) when |Δ| is below the profile's epsilon —
    //      converged feedback loops (G = P/V²) fall back to cached-LU cost automatically.
    public void Adjust(ResistorId id, double ohms);
    public void Adjust(SwitchId id, bool closed);      // relay contact: stays inside its matrix

    // ---- Tier 3-lite: Reconfigure — island membership of an existing coupler.
    //      Close ⇒ incremental island merge; open ⇒ split-rebuild, COALESCED to next Solve.
    public void Reconfigure(CouplerId id, CouplerState state);   // Open | Closed

    // ---- Tier 3: structural. The only door. ----
    public StructuralEdit Edit();                      // batch scope; commits on Dispose

    // ---- Solve & read ----
    public void SolveOperatingPoint();                 // DC op-pt (caps open, inductors ~short); energize / lesson start
    public void Solve(in TickClock clock);             // one game tick: dirty islands only, serial (small clients)
    public IslandTable Islands { get; }                // per-island scheduling for clients that own threads
    public Solution Solution { get; }                  // committed values of the last completed Solve (double-buffered)

    // ---- Legibility rails ----
    public SteadyStateGuard EnterSteadyState();        // IDisposable region: structural/Reconfigure ops assert (debug)
                                                       // or defer-and-log (release); arms debug allocation tripwire
    public ref readonly TickStats LastTickStats { get; } // what the last Solve actually cost (see §2)
    public DeviceTickContext TickContext(double dt);   // tier-≤2 capability handed to device Tick() (see §1.6)
}

public readonly record struct TickClock(long TickIndex, double Dt);  // determinism + AC phase continuity
```

### 1.3 Structural edits, analyses, couplers

```csharp
public sealed class StructuralEdit : IDisposable      // all tier-3; island rebuilds coalesce to Dispose
{
    public NodeId      AddNode();
    public NodeId      AddGround();                    // marks island reference (VS: the literal earth node)
    public ResistorId  AddResistor(NodeId a, NodeId b, double ohms, in LimitSpec limits = default, StateKey key = default);
    public VSourceId   AddVoltageSource(NodeId pos, NodeId neg, double volts, StateKey key = default);
    public ISourceId   AddCurrentSource(NodeId from, NodeId to, double amps, StateKey key = default);
    public CapacitorId AddCapacitor(NodeId a, NodeId b, double farads, StateKey key = default);
    public InductorId  AddInductor(NodeId a, NodeId b, double henries, StateKey key = default);
    public SwitchId    AddSwitch(NodeId a, NodeId b, bool closed);
    public DiodeId     AddDiode(NodeId anode, NodeId cathode, in DiodeParams p);
    public VSourceId   AddSineSource(NodeId pos, NodeId neg, in SineSpec s); // marks the island AC ⇒ subcycles
    public CouplerId   AddCoupler(in CouplerSpec spec, in CouplerPorts ports, StateKey key = default);
    public void        Remove<TId>(TId id) where TId : struct, IComponentId;
    public void        Dispose();                      // commit; affected islands → Dirty
    public EditReceipt Receipt { get; }                // after Dispose: nodes added, islands invalidated, est. rebuild size
}

public readonly struct SolverProfile
{
    public static SolverProfile Dc(double dt);                 // Stationeers: dt=0.5; DC + BE storage dynamics
    public static SolverProfile Transient(double dt);          // VS DC-side / tablet: dt=0.05
    public static SolverProfile Mixed(double tickDt, int acSamplesPerCycle = 20);
    // Mixed: per-island regime chosen by content — islands containing sine sources subcycle
    // (N per island, quantized with hysteresis per solver.md); pure-DC islands step once.
}

public readonly struct CouplerSpec
{
    public static CouplerSpec Breaker();                                    // closed=galvanic merge, open=separate islands
    public static CouplerSpec DecouplingTransformer(in TransformerParams p); // always a boundary; amp+phase per substep, relaxed
    public static CouplerSpec ConverterTwoPort(in EfficiencyCurve e, double dcLinkFarads); // Stationeers xfmr/charger
}
```

### 1.4 Islands, readback, limits, faults

```csharp
public readonly struct IslandTable
{
    public int Count { get; }
    public IslandHandle this[int i] { get; }
    public IslandHandle Of(NodeId n);
    public int CollectDirty(Span<IslandHandle> into); // zero-alloc worklist for client-owned schedulers
}

public enum IslandStatus { Empty, Building, Ready, Dirty, Faulted }
// State machine (the client-visible invariant):
//   Ready --Drive/Adjust/Reconfigure/Edit--> Dirty --Step ok--> Ready
//   Dirty --Step fails--> Faulted --any tier-2/3 change--> Dirty (retry)
//   Building (background construction): reads return last-good; Step is a no-op.
//   Faulted reads as de-energized; Solution.IsLive(island)==false in both cases.

public readonly struct IslandHandle
{
    public IslandId Id { get; }
    public IslandStatus Status { get; }
    public void Step(in TickClock clock);              // solve THIS island; callable from any thread.
                                                       // Contract: single writer per island (debug-asserted, not locked).
    public FaultDiagnostic Fault { get; }              // Kind + offending ComponentRefs/NodeIds + human message (R9)
    public int DrainLimitEvents(Span<LimitEvent> into);// post-solve; returns count; fixed-cap ring, overflow counted
    public void Snapshot(IBufferWriter<byte> into);    // caps, inductors, device state, source phase — keyed by StateKey
    public RestoreResult Restore(ReadOnlySpan<byte> b);// match by StateKey; unmatched ⇒ cold-start that element, reported
}

public readonly struct Solution                        // committed snapshot; safe to read while other islands Step
{
    public double Voltage(NodeId n);
    public double Current<TId>(TId branch) where TId : struct, IComponentId;
    public double Power<TId>(TId component) where TId : struct, IComponentId;
    public double Read(ProbeId p);                     // interpolated inside compacted runs (compaction.md)
    public bool   IsLive(IslandId i);                  // false ⇒ values are last-good (Building) or zero (Faulted)
}

public readonly struct LimitSpec { /* MaxCurrent, MaxVoltage, MaxPower, I2tRating */ }
public struct LimitEvent { /* ComponentRef Source; LimitKind Kind; double Actual, Threshold, I2tFraction; */ }
// The solver never mutates the circuit on a limit (R7); popping the fuse is the client's Adjust/Reconfigure.

public sealed class WaveformTap                        // oscilloscope: per-substep sampling, caller-owned ring
{
    public static WaveformTap Attach(Netlist n, ProbeId p, WaveformRing ring); // two taps = the expected UI contract
    public void Detach();
}
```

### 1.5 Reduction layer (`Manatee.Reduction`) — what game clients actually talk to

```csharp
public sealed class ConductorGraph            // geometry in, minimal netlist out (R10/R11)
{
    public ConductorGraph(Netlist target, in GraphOptions opts);  // opts: PrePartitioned (Stationeers) | SelfPartitioned (VS)

    public BulkBuild BeginBulkBuild();        // load-time scope: ONE compaction pass at Dispose, not one per segment
    public void AddSegment(SegmentKey k, JunctionKey a, JunctionKey b, in ConductorSpec spec);
                                              // incremental; merge pre-check decides fast path vs island rebuild
    public void RemoveSegment(SegmentKey k);  // dirties the island; rebuild coalesced to next Solve (burst = 1 rebuild)
    public NodeId PortNode(JunctionKey j);    // where devices attach post-compaction
    public ProbeId AddProbe(SegmentKey k, double along); // survives collapse via resistance-ratio interpolation
    public void SetAmbient(SegmentKey k, double kelvin); // limit-envelope recompute ONLY — never touches the matrix
    public AttributionResult Attribute(in LimitEvent e); // equivalent-resistor event → which segment melts/pops (R7)
    public ResyncReport Resync();             // from-scratch rebuild + diff: the drift backstop (R11), debug/CI mode
}
public readonly struct ConductorSpec { /* OhmsPerMeter, Meters, LimitSpec Limits (per cable type / voxel material) */ }
```

### 1.6 Devices layer (`Manatee.Devices`)

```csharp
public abstract class Device
{
    protected abstract void Build(StructuralEdit e, ReadOnlySpan<NodeId> terminals); // create primitives once
    public virtual void Tick(in DeviceTickContext ctx) { }                           // per-tick parameter updates
}

// The capability a device Tick receives. It exposes ONLY tiers 1–2:
// a device author (including future Re-Volt contributors converting legacy devices)
// CANNOT write a structural change into the hot loop — it doesn't compile.
public readonly ref struct DeviceTickContext
{
    public Solution Previous { get; }                  // last tick's committed solution
    public double Dt { get; }
    public void Drive(VSourceId id, double volts);
    public void Drive(ISourceId id, double amps);
    public void Adjust(ResistorId id, double ohms);
    public void Adjust(SwitchId id, bool closed);
}
```

## 2. How the tiers appear on the surface (R4)

Four independent mechanisms, so the cost model survives every way a client author
encounters the API:

1. **Grammar (compile time).** `Drive`/`Adjust`/`Reconfigure` are the only mutators on
   `Netlist`. Structural methods *do not exist* there — you must hold a
   `StructuralEdit`, and `DeviceTickContext` caps device authors at tier 2 by type.
   Grepping a client codebase for `\.Edit\(` and `Reconfigure` finds every tier-3 call
   site; a code review can audit the cost profile without running anything.
2. **Guard region (debug runtime).** `using (net.EnterSteadyState())` around the
   per-device fan-out makes tier-3 an assert in debug and a deferred-with-log in
   release. This is the "no structural changes past this line" invariant made
   executable — the integration bug it catches ("someone's event handler rebuilt the
   world inside CalculateState") is exactly the one that would otherwise ship.
3. **Receipts (observability).** `EditReceipt` (what an edit invalidated) and
   `TickStats { Substeps, RhsSolves, Refactorizations, IslandRebuilds, NewtonIterations,
   BytesAllocated }` after every Solve. An SRE-shaped owner gets the solver as a
   monitorable system: `Refactorizations > 0` in steady state means some `Adjust`
   isn't converging; `IslandRebuilds > 0` means a coupler or cut fired. Re-Volt can
   log these per tick for free.
4. **Docs convention.** Every mutator carries `[CostTier(n)]` + a doc-comment first
   line stating the tier — enabling a later Roslyn analyzer ("tier-3 call inside a
   method named `*Tick`") without committing to one now.

A fifth, implicit mechanism: **epsilon no-op on `Adjust`.** The Stationeers adaptor
calls `Adjust` on ~100 loads every tick, but G = P/V² converges in a few ticks; once
converged, `Adjust` detects |Δ|<ε and the island stays on its cached LU. The cost
model *rewards* convergence without the client doing anything — and `TickStats`
shows it happening.

## 3. Worked example: the Re-Volt integration

All on the game's sim ThreadPool thread (stationeers.md: no dedicated power thread;
cost extends the global 500 ms cycle, so tier-3 goes off-band).

### 3.1 Save load — the 10k-segment build (background task, one-tick handoff)

```csharp
var net   = new Netlist(SolverProfile.Dc(dt: 0.5));
var graph = new ConductorGraph(net, GraphOptions.PrePartitioned);   // CableNetworks arrive pre-split

// Runs on a background task; islands sit in Building, game runs Re-Volt's scalar
// fallback until they flip to Ready (Solution.IsLive == false meanwhile).
using (var build = graph.BeginBulkBuild())
{
    foreach (var c in export.Cables)      // Sukasa's NetworkExport: RefId adjacency + type + fuses
        build.AddSegment(new SegmentKey(c.RefId), Junction(c.A), Junction(c.B),
                         CableSpecs.For(c.PrefabName));   // ampacity/i²t limits ride the spec
}   // Dispose: ONE compaction pass. build.Receipt: 10_212 segments → 143 nodes, 7 islands.

using (var e = net.Edit())
{
    foreach (var d in export.Devices)     // ~100 adapted constant-power devices
        _adapted.Add(AdaptedLoad.Create(e, graph.PortNode(d.Port), d, key: new StateKey(d.RefId)));
    foreach (var b in export.Breakers)
        _couplers[b.RefId] = e.AddCoupler(CouplerSpec.Breaker(), PortsOf(b), key: new StateKey(b.RefId));
}
net.SolveOperatingPoint();                // energize

foreach (var (islandKey, blob) in saveHook.LoadBlobs())          // R6 via external hook
    _ = net.Islands.Of(NodeFor(islandKey)).Restore(blob);        // StateKey-matched; misses cold-start, never fail the load
```

### 3.2 The adapted device (R18)

```csharp
sealed class AdaptedLoad : Device
{
    ResistorId _g; NodeId _n; IDeviceVanilla _vanilla;
    protected override void Build(StructuralEdit e, ReadOnlySpan<NodeId> t)
        => _g = e.AddResistor(t[0], t[1], NominalOhms, key: Key);

    public override void Tick(in DeviceTickContext ctx)      // tier ≤2 by construction
    {
        double v = ctx.Previous.Voltage(_n);
        double p = _vanilla.PowerWanted;                      // vanilla 4-call API, snapshot phase
        if (v < BrownoutVolts || p <= 0) { ctx.Adjust(_g, OpenOhms); return; }   // dropout
        ctx.Adjust(_g, Clamp(v * v / p));                     // G = P/V_prev² — converges ⇒ Adjust becomes a no-op
    }
}
```

### 3.3 The half-second tick

```csharp
void CalculateState_New()                                     // Harmony seam, sim thread
{
    // Queued game events first — the tier-3 zone, before the guard:
    while (_pendingBreakers.TryDequeue(out var b))
        net.Reconfigure(_couplers[b.RefId], b.Closed ? CouplerState.Closed : CouplerState.Open);
        // close ⇒ incremental merge of two islands; open ⇒ split-rebuild, coalesced to Solve
    while (_pendingCuts.TryDequeue(out var refId))
        graph.RemoveSegment(new SegmentKey(refId));           // cable cut: island Dirty; N cuts = still 1 rebuild

    using (net.EnterSteadyState())                            // from here down, tier-3 is impossible
    {
        var ctx = net.TickContext(dt: 0.5);
        foreach (var d in _adapted.AsSpan()) d.Tick(in ctx);  // ~100 × Adjust (mostly ε-no-ops once converged)
    }

    net.Solve(_clock.Next(0.5));                              // 7 islands, refactor only where Adjust moved
}

void ApplyState_New()
{
    foreach (var d in _adapted.AsSpan()) d.Apply(net.Solution);   // actuals → vanilla provide/draw (removability)
    foreach (var island in _islands)
    {
        if (island.Status == IslandStatus.Faulted)
            { _scalar.FallbackTick(island); Log(island.Fault); continue; }   // never stall the game
        int n = island.DrainLimitEvents(_limitBuf);
        for (int i = 0; i < n; i++)
            _fuses.Blow(graph.Attribute(in _limitBuf[i]).Segment);           // which segment pops (R7)
    }
    _telemetry.Record(net.LastTickStats);                     // Refactorizations, IslandRebuilds → Re-Volt's log
}
```

## 4. The zero-alloc story (R8)

**Rule: after the last `Edit()`/`BulkBuild` commits, the tick loop allocates zero
bytes.** Enforced by BenchmarkDotNet MemoryDiagnoser in CI and, in debug, by an
allocation tripwire armed by `EnterSteadyState` (thread-local allocated-bytes delta
asserted at guard Dispose).

Buffer residency — everything the loop touches is preallocated and *sized at
structural-commit time*, because only tier-3 changes problem dimensions:

| Buffer | Owner | Sized when |
| --- | --- | --- |
| Matrix, LU workspace, RHS, solution + prev-solution (double buffer), Newton scratch | `Island` | `Edit()`/rebuild commit |
| Limit-event ring (fixed cap; overflow → counter, not growth) | `Island` | commit |
| Component tables (SoA), handle→row maps, dirty-island bitset | `Netlist` | commit |
| Compaction maps, probe interpolators, limit envelopes | `ConductorGraph` | build/rebuild |
| `_limitBuf` (Span), dirty-island worklist, snapshot `IBufferWriter` | **client** | client's choice |

The steady-state call sequence, annotated:

```csharp
net.Reconfigure(...)            // only if a breaker moved — flags rebuild, allocates nothing itself
graph.RemoveSegment(...)        // only if geometry changed — flags rebuild (the rebuild itself, at
                                //   Solve, allocates: that IS tier 3 and TickStats says so)
ctx.Drive(...) / ctx.Adjust(...) // 0 B — writes into preallocated RHS / value tables
net.Solve(clock)                // 0 B after warmup on the in-house dense path: substep loop is
                                //   RHS rebuild + back-substitution in place; tier-2 numeric
                                //   refactor runs in the preallocated LU workspace
net.Solution.Voltage(n)         // 0 B — indexed read of committed vector
island.DrainLimitEvents(span)   // 0 B — copies structs into the caller's span
island.Snapshot(writer)         // 0 B core-side — serializes into the caller's IBufferWriter
```

No C# `event`, no callbacks, no LINQ, no iterator methods anywhere on the hot
surface: **all solver→client communication is post-solve polling of structs.**
Callbacks from a solver running on a game worker thread are a reentrancy and
allocation trap; polling keeps the threading contract trivially auditable
(single-writer-per-island, reads of the committed double-buffered `Solution` are
always safe). The CSparse large-island fallback is exempt per solver.md; `TickStats.
BytesAllocated` makes the exemption visible rather than silent.

## 5. Rationale, trade-offs, open questions

**Rationale.**
- *Verbs over setters:* `SetResistance` vs `SetSourceValue` look identical in a code
  review; `Adjust` vs `Drive` do not. The vocabulary teaches the R4 cost model to
  every reader of client code, including future Re-Volt contributors who never read
  solver.md.
- *Capability objects over documentation:* the two audiences most likely to violate
  the cost model — device authors and tick-loop maintainers — physically can't
  (`DeviceTickContext` caps at tier 2; structural ops exist only on `StructuralEdit`).
- *Explicit island state machine:* `Empty/Building/Dirty/Ready/Faulted` with
  stale-read semantics gives the SRE owner the system as a legible state machine, and
  gives Stationeers its one-tick background-rebuild handoff (stationeers.md) for free:
  `Building` islands read last-good and skip `Step`.
- *StateKey:* Stationeers rebuilds every network on every load; VS rebuilds islands on
  any removal. Persisted state keyed to internal ids would be wrong by design; keying
  to client identity makes `Restore`-after-rebuild the default correct path (R6).
- *Scheduler inversion:* the core never owns threads (solver.md contract);
  `IslandHandle.Step` from any thread + `CollectDirty(Span)` supports both Re-Volt's
  single sim thread and VS's worker pool with the same surface.

**Trade-offs.**
- `Reconfigure` as a fourth verb adds one concept beyond the doc's three tiers. I
  judge the duality (relay=`Adjust`, breaker=`Reconfigure`) worth naming, since it is
  precisely the distinction solver.md says clients must get right — but a synthesis
  could fold it into `Edit()` at the cost of normalizing mid-tick edit scopes.
- Handles are index+generation structs: stale-handle misuse is a debug assert, not a
  compile error. Full compile-time safety would need ownership types C# doesn't have.
- ε-no-op `Adjust` makes per-tick cost data-dependent. Mitigated by `TickStats`; the
  alternative (always refactor) is strictly worse.
- Polling-only limit events with a fixed ring cap can drop events under pathological
  storms (counted, not silent). Acceptable: R9 says degrade legibly, and a melting
  base does not need every event.

**Open questions.**
1. Should `Reconfigure` be legal inside `EnterSteadyState`? Current answer: no —
   breaker consequences of *this* tick's limit events apply next tick, matching the
   across-tick philosophy of the adaptor. Needs Sukasa's sign-off on the one-tick
   breaker latency.
2. Does `ConductorGraph` wrap or expose its `Netlist`? Tablet/2D clients want the raw
   netlist (wires are already minimal); world clients should perhaps *only* get the
   graph facade, so nothing bypasses attribution. Proposal: constructor takes the
   netlist, convention says world clients never touch it directly — enforce or trust?
3. `Solution` reads across islands during parallel `Step` are safe (double buffer),
   but a device reading a *coupler peer* island mid-solve sees last tick by design.
   Is per-substep boundary exchange (AC amplitude+phase) inside one thread's island
   group, or does it need a rendezvous API? Affects VS worker scheduling, not
   Stationeers.
4. Snapshot format versioning policy (versioned binary + JSON debug form per
   solver.md) — where does the JSON form live so it stays zero-cost in release?
5. Thermal-RC reuse (design.md): `Drive/Adjust` verbs and `NodeId` are already
   domain-neutral; is `ConductorSpec.OhmsPerMeter` the one name that should
   generalize now, or later?

