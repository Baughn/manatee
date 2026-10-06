# Manatee integration guide

Last updated: 2026-10-06

**Status: illustrative.** These walkthroughs code against the API in [api.md](api.md), which is not implemented yet. They will get runnable twins under `examples/` once an implementation exists; until then, treat them as sketches that show each client is served, not as tested code. Engine facts with file:line references live here until `docs/games/vintage-story.md` and `docs/games/stationeers.md` are written, then move there.

Three integrations: Stationeers through Re-Volt (one global DC step per game tick), Vintage Story (voxel cables, chunks, an alternator on a mechanical shaft, tooltips and a scope), and the tablet (a lesson with a two-channel scope). Game method names are the real vanilla and Re-Volt names. Helpers marked `// adapter` are code the mod writes itself.

## 1. Stationeers (Re-Volt): one global DC step per tick

Background: Stationeers runs all electricity on one worker thread once per game tick (`ElectricityManager.ElectricityTick`; tick length `GameManager.GameTickSpeedSeconds`, 0.5 s, `GameManager.cs:334-336`), shared with atmospherics. Vanilla "watts" are really joules per tick. Placing a cable goes through `CableNetwork.Merge` and `CableNetwork.Add` (`CableNetwork.cs:503-595`; Re-Volt defers this, `CablePatches.cs:45-54`); cutting one goes through `RebuildCableNetworkServer`, which creates a new network and leaves the old one smaller (`CableNetwork.cs:660-669`). So any of four calls can change a `CableNetwork`'s membership, and a split changes two. Re-Volt's own adjacency includes cable-tray links. `GetUsedPower`/`GetGeneratedPower` return -1 for "not on this network" only on the base `Device` (`Device.cs:1186-1203`); `Battery` and `AreaPowerControl` return 0 (`Battery.cs:520-545`, `AreaPowerControl.cs:492-521`), so the adapter keeps its own registry of the devices the simulation owns. A `CableFuse` is a device mounted on a straight cable in the same small cell (`CableFuse.cs:7`, `DeviceCableMounted.cs:19-55`), and its `Break()` also breaks that cable (`CableFuse.cs:40-52`).

```csharp
public static class Power                                     // Re-Volt plugin load, server only
{
    public static Circuit Sim;  public static Readings Frame;  // Frame: what this tick's ApplyState calls read
    public static readonly ConcurrentQueue<CircuitEvent> Pending = new();
    public static readonly CircuitEvent[] Events = new CircuitEvent[256];
    public static readonly HashSet<long> Owned = new();        // multi-network devices the simulation owns (filled by Adapt)
    static readonly HashSet<CableNetwork> Dirty = new();
    public static double Tick => GameManager.GameTickSpeedSeconds;

    public static void Start()
    {
        var o = new CircuitOptions { Grounding = Grounding.CommonReturn, MaxStep = Tick };   // Scheduler null: serial
        foreach (var (name, mm2) in new[] { ("normal", 2.5), ("heavy", 16.0), ("superHeavy", 95.0) })
            o.CableTypes.Register(name, new CableType("copper", Units.Mm2(mm2)));
        Sim = Circuit.Create(o);
    }

    // Postfixes on CableNetwork.Add, Remove, Merge and RebuildNetwork: mark every network they touch, including the
    // one a merge empties and the smaller one a split leaves behind.
    public static void Touched(CableNetwork net) { lock (Dirty) Dirty.Add(net); }   // adapter
    public static void SyncGroups()                            // adapter: once per tick, before Step
    {
        using (Sim.BeginEdits()) lock (Dirty)                  // groups reconcile on final membership, so devices
        {                                                      //   moving between CableNetworks keep their state
            foreach (var net in Dirty)
                if (net.CableList.Count == 0) Sim.RemoveGroup(new GroupId(net.ReferenceId));
                else Sim.ReplaceGroup(new GroupId(net.ReferenceId), Entries(net));
            Dirty.Clear();
        }
    }
    // Entries(net) (adapter, reused buffer):
    //   cable:  new CablePiece(TypeOf(c), CellLength, NeighbourIds(c))          neighbours include tray links
    //             { Fuse = c.SmallCell?.Device is CableFuse f ? new Rating { Current = FuseAmps(f) } : null }
    //           The fuse is the cable's rating, not a part of its own: a second piece would be an unfused parallel path.
    //   device: Adapt(d), from the device alone, so both groups of a two-network device give the same record.
    static Part Adapt(Device d) => d switch {   // M = Manatee namespace: the game has its own Battery, Transformer, Cable
        Battery b        => Own(b, new M.Battery(                  // Own (adapter): add to Owned, return the part
                                PieceOf(b, b.OutputNetwork).Node, FullV(b), EmptyV(b), b.PowerMaximum,
                                MaxChargePower: ChargeCapW(b), MaxDischargePower: DischargeCapW(b))
                                { ChargeInput = PieceOf(b, b.InputNetwork).Node }),
        CircuitBreaker k => Own(k, new Breaker(PieceOf(k, k.InputNetwork).Node, PieceOf(k, k.OutputNetwork).Node,
                                TripAmps(k), IsSmart(k) ? TripCurve.Instant : TripCurve.C)),
        Transformer t    => Own(t, new Converter(PieceOf(t, t.InputNetwork).Node, PieceOf(t, t.OutputNetwork).Node,
                                MaxPower: t.Setting / Tick) { OutputVoltage = VoltageClass(t) }),
        AreaPowerControl a => Own(a, new Custom("revolt:apc", ApcProps(a))),   // ApcProps (adapter): converter plus an
                                                                                //   inner battery when its slot is filled
        // PowerTransmitter: its output network is wireless and has no cables, so it is a Converter to the
        // receiver's piece, with an adapter-set efficiency for distance (questions.md E9).
        _ when IsGenerator(d) => new PowerLimitedSource(PieceOf(d).Node, VoltageClass(d)),
        _ => new ConstantPowerLoad(PieceOf(d).Node, VoltageClass(d), 0.8 * VoltageClass(d), 0.9 * VoltageClass(d)),  // dropout, restore
    };
}

static void Prefix()   // Harmony prefix on ElectricityManager.ElectricityTick (worker thread)
{
    var sim = Power.Sim;  double tick = Power.Tick;
    Power.SyncGroups();
    foreach (CableNetwork net in CableNetwork.AllCableNetworks)
        foreach (Device d in net.PowerDeviceList)
        {
            if (Power.Owned.Contains(d.ReferenceId)) continue;                  // the simulation owns these now
            float used = d.GetUsedPower(net), gen = d.GetGeneratedPower(net);    // -1: not on this network
            if (used >= 0) sim.SetDemand(new PartId(d.ReferenceId), used / tick);   // J per tick -> W; shedding sends 0
            if (gen >= 0)  sim.SetAvailable(new PartId(d.ReferenceId), gen / tick);
        }
    foreach (CircuitBreaker k in Breakers)                                       // adapter registry
        sim.SetClosed(new PartId(k.ReferenceId), k.Mode == CircuitBreaker.MODE_ON);   // a tripped one stays open
    while (RearmRequests.TryDequeue(out long id)) sim.Rearm(new PartId(id));      // from OnOff on a TRIPPED breaker

    sim.Step(tick);
    Power.Frame.Dispose();  Power.Frame = sim.AcquireReadings();                 // pooled: no allocation per tick
    for (int n; (n = sim.DrainEvents(Power.Events)) > 0;)
        for (int i = 0; i < n; i++) Power.Pending.Enqueue(Power.Events[i]);      // vanilla objects: main thread only
    Fallback.Update(Power.Frame);   // adapter: flags every CableNetwork of a non-Live network (GroupsIn); when one
                                    // returns to Live, re-seeds its batteries with SetStoredEnergy(PowerStored)
}

public void ApplyState_New()        // Re-Volt's per-network ApplyState is now readback only
{
    if (Fallback.IsActive(CableNetwork)) { ApplyScalarBudget(); return; }   // old ratio model; also before the first step
    Readings f = Power.Frame;  double tick = Power.Tick;
    foreach (Device d in Devices)
    {
        PartReading r = f.Part(new PartId(d.ReferenceId));
        if (Power.Owned.Contains(d.ReferenceId))                      // no vanilla power calls: their overrides run game logic
        { if (d is Battery b) b.PowerStored = (float)r.StoredEnergy; continue; }   // the sim is authoritative; vanilla saves it
        if (r.StepEnergy > 0) d.ReceivePower(CableNetwork, (float)r.StepEnergy);   // J this tick = vanilla "watts"; consumed
        if (r.StepEnergy < 0) d.UsePower(CableNetwork, (float)-r.StepEnergy);      // supplied
        if (r.Powered != d.Powered && d.AllowSetPower(CableNetwork))
            d.SetPowerFromThread(CableNetwork, r.Powered).Forget();   // Thing.Powered is get-only; this hops to main thread
    }
    GroupReading g = f.Group(new GroupId(CableNetwork.ReferenceId));  // synced to clients and read by logic
    CableNetwork.RequiredLoad  = (float)(g.Demand * tick);
    CableNetwork.CurrentLoad   = (float)(g.Delivered * tick);
    CableNetwork.PotentialLoad = (float)(g.Available * tick);
}

// HeavyBreaker ports change inside OnPowerTick, after the step. Postfix: Rewire(hbId, terminal: 1, newPiece.Node) (next step).

static void DrainPending()          // main thread, every frame: consequences
{
    while (Power.Pending.TryDequeue(out var e)) switch (e.Kind)
    {
        case EventKind.ConductorMelted when FindThing<Cable>(e.Part) is { } c:          c.Break(); break;  // already open
        case EventKind.FuseBlown when FindThing<Cable>(e.Part)?.SmallCell?.Device is CableFuse fz: fz.Break(); break;
        case EventKind.BreakerTripped  when FindThing<CircuitBreaker>(e.Part) is { } k: k.Trip();  break;
    }   // Break() triggers a vanilla rebuild, which comes back through Touched and ReplaceGroup.
}
// World load: PowerStored and breaker Mode already persist; after the rebuilds, SetStoredEnergy(id, b.PowerStored) per battery.
```

Readback uses `StepEnergy`, the joules of exactly this tick, so nothing depends on the meter window. Cable `HeatPower` can feed rooms the way vanilla devices feed atmospherics through `EnergyToHeatRatio`.

### 1.1 Behaviour changes for Re-Volt (for Sukasa's sign-off)

1. **One global step.** One prefix on `ElectricityTick` replaces per-network `RevoltTick`; `ApplyState` only reads results.
2. **No lag accumulators.** The one-tick-lag energy accumulators on batteries, transformers, breakers and area power controllers go away: a two-network device is one part with terminals on both networks.
3. **Back-feed.** A closed breaker is a conductor, so power can flow from its output network to its input network. Today power moves one way only.
4. **Breaker setting in amps.** Today the breaker compares watts against `Setting` and rolls `(transferred/Setting)^1.25 − 1`. It becomes an amps-based trip curve, randomised only through `Rating.Spread`.
5. **Fuse and cable ratings in amps.** `CableFuse.PowerBreak` and `Cable.MaxVoltage` change from watt ratings to amp ratings. A fuse now trips on the current through the cable it sits on, not on the network's total flow, so where you place it matters.
6. **Transformer.** The vanilla transformer becomes a `Converter`: its `Setting` dial becomes a power cap, and its output voltage is regulated.
7. **Battery caps.** The charge and discharge caps (0.2% and 0.7% of capacity per tick) map to `MaxChargePower`/`MaxDischargePower`.
8. **Which cable burns.** The random pick among the weakest cable group (seeded per network) becomes "the hottest cable burns", with optional seeded `Spread` from `(Seed, PartId)`.
9. **No breaker override.** The "trip all breakers instead of burning" rule is removed; a correctly sized breaker trips first by physics.
10. **Loads.** Proportional `wanted × ratio` sharing becomes full power or undervoltage dropout, with a fixed drop-out order under overload (questions.md E4).
11. **Gone:** the 20 W anti-flap band, the `Lerp(0.1)` heat window and the client-side G = P/V² loop. Restores are staggered instead: one load per network per step, after `RestoreDelay`.
12. **Ids.** `NetworkExport.ThingInfo.RefId` must widen from `int` to `long`, because `Thing.ReferenceId` is `long`.

## 2. Vintage Story: voxel cables, chunks, an alternator, tooltips and a scope

Background: the game is server-authoritative, and the block-info tooltip runs on the *client* copy of a block entity (`Block.cs:2268` → `BlockEntity.cs:400`), so tooltips need the replication feed. Mods can own a thread (`IAsyncServerSystem`). Chiseled blocks report voxel edits through `IMicroblockBehavior.RebuildCuboidList`, which runs on placement and on edits (on both sides), but not when a block loads from disk (`BEMicroBlock.cs:773`, fan-out at `:907-911`; `Initialize` at `:194-197` only calls the base). The mechanical network asks consumers for drag through `GetTorque(tick, speed, out resistance)` with a signed speed, and adds `resistance` as opposing torque; it calls `GetTorque` from `updateNetwork` (`MechanicalNetwork.cs:222`), only while the network is fully loaded (`MechanicalPowerMod.cs:159`). The shaft turns at ω = 5 · speed rad/s: `UpdateAngle(speed * dt * 50f)` (`MechanicalNetwork.cs:158`) adds `speed / 10` radians (`:185-187`). The alternator must use exactly that factor so its frequency matches the visible rotation.

The voxel-to-cable conversion lives in a VS-side package (`Manatee.VintageStory`), not in the core. It publishes one id packing that any mod can call, so devices, cables and other mods' connection points agree:

```
bit 63       0 (ids ≤ 0 belong to the library)            bit 62       0 = cable voxel, 1 = device slot
bits 61..42  block x (20 bits)                          bits 41..32  block y (10 bits)
bits 31..12  block z (20 bits)                          bits 11..0   voxel index x + 16y + 256z, or slot number
```

- One `CablePiece` per conducting voxel, linked to its conducting neighbours, including across block faces. Ids are absolute world positions, so both neighbouring chunks derive the same id in any load order.
- `VoxelCables.IdAt(pos, x, y, z)` and `DeviceId(pos, slot)` implement the packing. A device terminal names the cable voxel it touches in the neighbouring block (`IdAt(...).Node`). A voxel on a chunk-column border names that column as `Area` and the neighbour as `SecondArea`.
- The packing has no dimension bits (`BlockPos.dimension`; `InternalY = Y + dimension × 32768`, `BlockPos.cs:28-40`) and assumes map sizes within its field widths. `VoxelCables` checks `MapSizeX`/`MapSizeZ` ≤ 2^20 and `MapSizeY` ≤ 2^10 at startup (engine maxima still to verify) and refuses to start otherwise; blocks in a non-zero dimension are not electrified in v1 (questions.md B8).

```csharp
public class ManateeSystem : ModSystem
{
    public Circuit Sim;  public VoxelCables Voxels;  ChangeFeed feed;  IServerNetworkChannel channel;
    readonly CircuitEvent[] events = new CircuitEvent[128];  readonly ChangedReading[] changed = new ChangedReading[1024];

    public override void StartServerSide(ICoreServerAPI sapi)
    {
        var o = new CircuitOptions { Grounding = Grounding.Earth, MeterWindow = 0.2,   // one full cycle at 5 Hz
                                     MaxStep = 0.1, Scheduler = new VsWorkerScheduler(sapi) };
        foreach (var block in sapi.World.Blocks)                  // conductors from block JSON; other mods add theirs
            if (block.Attributes?["manatee"] is { Exists: true } m) o.Materials.Register(block.Code.ToString(), VsMaterials.From(m));
        Sim = Circuit.Create(o);  Voxels = new VoxelCables(Sim, sapi.World.BlockAccessor);   // checks the map size
        sapi.Server.AddServerThread("manatee", new StepThread(Sim));
        // Autosave suspends the server and calls ToTreeAttributes; whether mod threads pause too is unverified, so pause ours.
        sapi.Event.ServerSuspend += () => { sapi.Server.PauseThread("manatee"); return EnumSuspendState.Ready; };
        sapi.Event.ServerResume  += () => sapi.Server.ResumeThread("manatee");
        sapi.Event.ChunkColumnLoaded += (col, _) =>               // mark loaded once the column's block entities are up
            sapi.Event.RegisterCallback(_ => Sim.SetAreaLoaded(VoxelCables.AreaOf(col), true), 0);
        sapi.Event.ChunkColumnUnloaded += col => {                // fires for every column on shutdown: ignore it then
            if (!sapi.Server.IsShuttingDown) Sim.SetAreaLoaded(VoxelCables.AreaOf(col), false); };
        feed = Sim.CreateChangeFeed(new ChangeThreshold(Relative: 0.02));
        channel = sapi.Network.RegisterChannel("manatee").RegisterMessageType<ReadingPacket>();
        sapi.Event.RegisterGameTickListener(_ => {                // main thread: consequences and telemetry
            for (int n; (n = Sim.DrainEvents(events)) > 0;) for (int i = 0; i < n; i++) Hazards.Apply(events[i], this);
            TelemetryRelay.SendToNearbyPlayers(channel, changed.AsSpan(0, feed.Drain(changed)));   // per-player filter
            TelemetryRelay.SendNewcomers(channel, Sim);           // parts entering a player's interest: Readings.Compact(id)
        }, 250);
    }
}

class StepThread : IAsyncServerSystem                              // Step(dt) every 50 ms on the mod's own thread
{
    readonly Circuit sim;  readonly Stopwatch clock = Stopwatch.StartNew();  public StepThread(Circuit s) => sim = s;
    public int OffThreadInterval() => 50;
    public void OnSeparateThreadTick()                             // after a pause, one ordinary step, not one huge one
    { double dt = Math.Min(clock.Elapsed.TotalSeconds, 0.1); clock.Restart(); sim.Step(dt); }
    public void ThreadDispose() => sim.Dispose();
}

public class BEBehaviorCableVoxels : BlockEntityBehavior, IMicroblockBehavior
{
    ManateeSystem Mod => Api.ModLoader.GetModSystem<ManateeSystem>();
    BlockEntityMicroBlock Be => (BlockEntityMicroBlock)Blockentity;
    public override void Initialize(ICoreAPI api, JsonObject props)   // load from disk: RebuildCuboidList does not run
    {
        base.Initialize(api, props);
        if (api.Side != EnumAppSide.Server || Be.BlockIds == null || Be.VoxelCuboids.Count == 0) return;  // placement: WasPlaced
        Be.ConvertToVoxels(out var voxels, out var materials);
        Mod.Voxels.SetBlock(Blockentity.Pos, voxels, materials, Be.BlockIds);         // -> ReplaceGroup(block, pieces)
    }
    public void RebuildCuboidList(BoolArray16x16x16 voxels, byte[,,] voxelMaterial)  // placement and chisel edits
    { if (Api.Side == EnumAppSide.Server) Mod.Voxels.SetBlock(Blockentity.Pos, voxels, voxelMaterial, Be.BlockIds); }
    public override void OnBlockRemoved() { if (Api.Side == EnumAppSide.Server) Mod.Voxels.RemoveBlock(Blockentity.Pos); }
    // OnBlockUnloaded: nothing. The area freeze covers it; the parts and their heat wait for the chunk.
    public void RotateModel(int degrees, EnumAxis? flip) { }   public void RegenMesh() { }
}

public class BEBehaviorAlternator : BEBehaviorMPConsumer
{
    PartId id;  TorqueMeter torque;  byte[] saved;            // saved: from FromTreeAttributes
    ManateeSystem Mod => Api.ModLoader.GetModSystem<ManateeSystem>();
    const double OmegaPerSpeed = 5.0, ResistancePerNm = 0.01;   // the engine's own factor (never tuned); the one
                                                                //   tuning constant, N·m to VS drag units
    public override void Initialize(ICoreAPI api, JsonObject props)
    {
        base.Initialize(api, props);  var p = Blockentity.Pos;
        id = VoxelCables.DeviceId(p, slot: 0);                // the client copy needs it for the tooltip
        if (api.Side != EnumAppSide.Server) return;
        var below = p.DownCopy();                             // terminals: cable voxels touching its bottom face
        bool created = Mod.Sim.Put(id, new Generator(VoxelCables.IdAt(below, 7, 15, 7).Node, VoxelCables.IdAt(below, 8, 15, 7).Node,
                PolePairs: 3, RatedVoltage: 24, RatedSpeed: 10, RatedPower: 400),   // 10 rad/s = VS speed 2; about 4.8 Hz
            new Placement(Area: VoxelCables.AreaOf(p), SecondArea: MechArea(this)));
        if (created && saved != null) Mod.Sim.LoadState(saved);   // server restart, not a chunk reload
    }
    // MechArea (adapter): one load area per mechanical network, marked unloaded while that network is not fullyLoaded,
    // because vanilla then stops calling GetTorque; the alternator freezes instead of generating at a stale speed
    // (questions.md B5). Re-Put with the new area when the alternator joins another mechanical network.

    public override float GetTorque(long tick, float speed, out float resistance)   // main thread, irregular; speed is signed
    {
        Mod.Sim.SetShaftSpeed(id, speed * OmegaPerSpeed);
        using (Readings r = Mod.Sim.AcquireReadings())
            resistance = (float)(Math.Max(0, torque.Update(r.Part(id)).Torque) * ResistancePerNm);   // held, never a false 0
        return 0f;                                                // an alternator only brakes the shaft
    }
    // ToTreeAttributes: tree.SetBytes("manatee", bytes from Mod.Sim.SaveState(writer, new[] { id })). Each block's snapshot
    //   carries its own Time, so phase relations between machines survive only a whole-circuit save (for example from a
    //   world-save handler) taken while stepping is paused.

    public override void GetBlockInfo(IPlayer forPlayer, StringBuilder dsc)   // client: reads the telemetry cache
    {
        if (!TelemetryCache.TryGet(id, out ChangedReading r)) return;
        if (r.Quality == Quality.Frozen) { dsc.AppendLine(Lang.Get("manatee:frozen")); return; }
        dsc.AppendLine(r.Condition switch {
            PartCondition.OpenCircuit => "0 A: the circuit is incomplete",
            PartCondition.Overloaded  => $"{SiFormat.A(r.CurrentRms)}: {r.Loading:P0} of rating",
            _ => $"{SiFormat.V(r.VoltageRms)}  {SiFormat.A(r.CurrentRms)}  {SiFormat.W(-r.Power)}  {r.AcFrequency:0.0} Hz" });
    }
}
// No load: torque ≈ 0 and the windmill spins freely. Shorted output: large drag, and the windmill visibly slows.
// Lamp flicker: drawn on the client from (VoltageRms, AcFrequency, AcPhaseOffset); no samples cross the network.

// Scope item: attached on the server while a player holds the probes; Copy() to that player ~10×/s; RemoveTrace when stowed.
Trace scope = Mod.Sim.AddTrace(new TraceSpec(new[] { Probe.Voltage(probeA), Probe.Current(alternatorId) }, Span: 2.0));  // to earth

static void Apply(in CircuitEvent e, ManateeSystem mod)        // Hazards: the library says what failed; the mod shows it
{
    var at = VoxelCables.PosOf(e.Part);
    if (e.Kind == EventKind.ConductorOverheating) Smoke.At(at, e.Temperature);
    if (e.Kind == EventKind.ConductorMelted) { mod.Voxels.RemoveVoxel(e.Part); Fire.Maybe(at); }  // returns as ReplaceGroup
    if (e.Kind == EventKind.FuseBlown) Sound.Pop(at);
}
// Shock: when an entity touches a bare live voxel, read Node(id.Node).VoltsToEarth.Rms (a floating section reads 0).
```

**Chunk loading.** On unload, the column's area is marked unloaded and every network touching it freezes whole, keeping readings and state; nothing catches up on reload. Vanilla mechanical networks also stop while not fully loaded, but they track 3D chunks (`MechanicalNetwork.cs:94-98`) and rebuild on reload (`MechanicalPowerMod.cs:326-349`); Manatee resumes instead. On reload, block entities `Put` their parts again under the same ids, which keeps their state: cable voxels from `Initialize` (above), devices from their own `Initialize`. The area is marked loaded one main-thread tick after `ChunkColumnLoaded`, once the column's block entities have initialised; the vanilla-style alternative is the first `ChunkDirty(NewlyLoaded)` of the column (`MechanicalPowerMod.cs:126`). `AreaOf` takes the `Vec2i` of the load event or the `Vec3i` (Y = 0) of the unload event (`IEventAPI.cs:47`, `:53`) and is offset so that no column maps to area 0, which is always loaded. On first load, a border voxel's link into a still-unloaded column freezes its network rather than cutting it; once the column loads, a link to a voxel with no conductor counts as open after `PendingLinkSteps`.

## 3. The tablet: an RL lesson at 5 Hz with a two-channel scope

The tablet runs inside VS on the client, in the desktop harness and in CI, all with the same code. The schematic layer (`Manatee.Schematic`) owns drawing, wire geometry, undo and the Falstad importer, and turns wires into node ids. The core never sees coordinates. Lesson 05: a 12 V, 5 Hz source `V1` (n10 to ground), `R2` 10 Ω (n10 to n11), an inductor (n11 to ground) and a switch `S4` across the inductor.

```csharp
var lesson = Lesson.Load("lessons/05-rl-at-5hz.yaml");          // a scenario plus narrative (accuracy-and-performance.md §2)
var options = new CircuitOptions { Grounding = Grounding.Explicit, Fidelity = Fidelity.Lesson };

// Interactive: the circuit the student plays with.
Circuit c = lesson.Build(options);                               // element ids are part ids, schematic nets node ids
var scope = c.AddTrace(new TraceSpec(new[] { Probe.Voltage(new NodeId(10)), Probe.Current(new PartId(2)) }, Span: 1.0));
double owed = 0;
void OnFrame(double realDt)                                      // UI thread; the slider scales simulated time
{
    if (!playing) return;
    for (owed += realDt * speed; owed >= lesson.Tick; owed -= lesson.Tick) c.Step(lesson.Tick);   // same result at any frame rate
    using Readings r = c.AcquireReadings();
    meterLabel.Text = $"{r.Part(new(3)).Current.Rms:0.000} A   PF {r.Part(new(1)).Ac.PowerFactor:0.00}";
    int n = scope.Copy(0, times, volts); scope.Copy(1, times, amps); DrawScope(times[..n], volts[..n], amps[..n]);
    if (r.NetworkOf(new PartId(1)) is { Diagnosis: not Diagnosis.None } net)
        Highlight(culprits[..r.Culprits(net.Key, culprits)], Phrase(net.Diagnosis));
        // e.g. the student wires n10 straight to ground: "V1 is shorted" (ShortedSource; culprits V1 and the wire)
}
void OnToggle(long id, bool on) => c.SetClosed(new PartId(id), on);   // applies at the next step boundary
void OnEdit()  => c.Put(new(2), new Resistor(new(10), new(11), 5));   // new value; the inductor keeps its current
void OnReset() { c.Dispose(); c = lesson.Build(options); }            // Time never goes back: start a fresh circuit
void OnJumpToSteady() => c.Settle(maxSeconds: 30, relativeChange: 1e-4);

// Checking: a separate circuit, with identical code in the tablet and in CI.
using Circuit g = lesson.Build(options);
foreach (var x in lesson.Observations)                          // { part, quantity, at or steady, value, teach }
{
    if (x.AtSteady) g.Settle(30, 1e-4); else while (g.Time < x.At - 1e-9) g.Step(lesson.Tick);
    using Readings r = g.AcquireReadings();
    double got = r.Measure(x.Part, x.Quantity);                 // Quantity.CurrentRms is "Current.Rms" in the file
    report.Add(x, got, passed: Math.Abs(got - x.Value) <= x.TeachTolerance);   // teach or teach-abs, as an absolute
}
```

The `teach` tolerance decides whether the student passes. CI runs the same file on every backend and scores the error against the accuracy budget instead (accuracy-and-performance.md), never against `teach`. Pausing is not calling `Step`. There is no separate DC or transient mode to choose.
