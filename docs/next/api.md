# Manatee API

Last updated: 2026-10-06

**Status:** design target; not implemented yet. Written as if the API existed. Open questions, each with a leaning, are in [questions.md](questions.md). Worked integrations are in [guide.md](guide.md). How every backend is tested and scored is in [accuracy-and-performance.md](accuracy-and-performance.md).

## 1. What Manatee is

Manatee is a C# library that simulates real electrical circuits inside games. You describe what the player built (cables, lamps, batteries, generators, breakers), each under an id you choose. You call `Step(dt)` once per game tick. Manatee then tells you what each part measured (volts, amps, watts, heat) and what failed (a blown fuse, a tripped breaker, a melted cable).

**Mental model: a physics engine for electricity.** In a physics engine you create bodies and joints, call `Step(dt)`, read back positions and handle collision events, and you never see the solver. Manatee works the same way, with *parts* instead of bodies, *nodes* instead of joints, *readings* instead of positions and *protection events* instead of collisions.

```
 game tick:   edits (Put / Remove / setters)  ->  Step(dt)  ->  published readings + events
                    applied at the start of the step                describe the step just finished
```

- **Your game owns the topology.** Your game data is the source of truth, and you can always rebuild the circuit from it.
- **Manatee owns the physics,** including protection: fuses, breakers and cables open themselves inside the simulation.
- **Your game owns the consequences:** fire, smoke, dropped items, removed blocks. Removing a melted cable comes back to Manatee as an ordinary edit.
- **The method is hidden.** The engine that computes the answers (the *backend*) is replaceable. The API, tests and benchmarks never depend on which one runs.

### 1.1 Quick start

```csharp
var o = new CircuitOptions { Grounding = Grounding.CommonReturn };
o.CableTypes.Register("copper-2.5", new CableType("copper", Units.Mm2(2.5)));
var c = Circuit.Create(o);
NodeId bat = new(10), lamp = new(11);
c.Put(new(1), new Battery(bat, FullVoltage: 12.6, EmptyVoltage: 11, EnergyCapacity: Units.Wh(100)));
c.Put(new(2), new Cable(bat, lamp, "copper-2.5", Length: 20));
c.Put(new(3), new Lamp(lamp, NodeId.CommonReturn, RatedVoltage: 12, RatedPower: 60));
c.Step(0.05);                                                       // once per game tick
using (Readings r = c.AcquireReadings()) Console.WriteLine($"{r.Part(new(3)).Power:0} W");
var events = new CircuitEvent[16];
for (int i = 0, n = c.DrainEvents(events); i < n; i++) Console.WriteLine(events[i].Kind);
```

## 2. Core concepts

New to electricity? Read [concepts.md](concepts.md) (planned) first; it explains RMS and peak, phase, power factor, ratings and I²t. This table names the API's nouns.

| Term | Meaning |
| --- | --- |
| **Circuit** | One simulated world. Instances are independent and nothing is static, so a server world and a tablet can share a process. |
| **Id** | A 64-bit number you choose for every part, node, group and load area. Valid until you remove that object. Manatee never issues ids. |
| **Part** | Anything electrical: a cable piece, a lamp, a battery, a resistor. An immutable record of its parameters and its terminal wiring. |
| **Terminal** | A connection point on a part (`A`/`B`, `Plus`/`Minus`...). In the record, each terminal is a field holding a node id. |
| **Node** | A point where terminals meet; everything on it is at the same voltage (schematic tools say "net"). It exists while any terminal names it. |
| **Cable piece** | One grid cell of cable (a Stationeers cable cell, a VS voxel). Each piece is both a short wire and a junction: neighbouring pieces link to it, and a device plugs into its centre via `pieceId.Node`. That is why parts and nodes share one id space: a piece's id doubles as its junction's id. |
| **Group** | Your tag for replacing many parts at once ("this Stationeers `CableNetwork`", "the cable voxels in this block"). A part may be in several. Groups never affect physics. |
| **Network** | The parts connected through any part's terminals, computed by Manatee and read-only. All terminals of one part are in the same network, so a transformer, converter or two-port battery joins the networks on both of its sides. Reference nodes and meters never join networks (3.12). Failures stay inside one network. |
| **Load area** | A key for a piece of the world that can unload. In VS, one chunk column (not a VS map region, which is 16×16 columns). A network touching an unloaded area is frozen. |
| **Reading** | What a meter would show over a *window* of recent simulated time (default 0.2 s): mean, RMS and peak, AC value, energy and heat. |
| **AC value** | RMS, frequency and phase angle of an alternating quantity: the wave described without listing its points (a phasor, in EE terms). |
| **Trace** | An oscilloscope channel: values at individual instants. The only source of instantaneous values, and only on request. |
| **Rating** | A part's nameplate limits: continuous current, voltage, and how much overload heating it can take. |
| **Quality** | How fresh a reading is: `Live`, `Stale` (waiting for a linked part), `Frozen` (unloaded area), `LastGood` (network unsolvable), `Missing`. |
| **Network status** | `Live`, `Stale`, `Frozen` or `Unsolvable`. An unsolvable circuit is a legal circuit with no answer (two ideal sources fighting), not an error. |
| **Diagnosis** | Why a network is unsolvable or suspicious, plus the culprit part ids. |
| **Event** | Something discrete that happened in a step (a fuse blew, a load dropped out), naming your ids. |

**Group, network, load area.** A group is your tag (you set it; physics ignores it). A network is what is actually connected (computed, like the connected components of a graph). A load area is what is loaded (like a chunk). A Stationeers `CableNetwork` is a group, not a network: a closed breaker joins two of them into one network.

**Units are SI everywhere:** V, A, Ω, W, J, s, m, m², Hz, rad, rad/s, N·m; temperatures in °C; angles in radians. Game-specific conversions live in the game adapter. Helpers:

```csharp
public static class Units    { public static double Mm2(double), Wh(double), Kw(double), Ms(double); }   // to SI
public static class Angles   { public static double ToDegrees(double), FromDegrees(double), Wrap(double); } // Wrap: to (−π, π]
public static class SiFormat { public static string V(double), A(double), W(double), J(double); }          // "1.2 kV"
```

**Sign convention.** Current is positive flowing into a part's first terminal and out of its second. Power is positive when the part absorbs it: a lamp reads positive watts, a generator supplying power reads negative watts (the standard *passive sign convention*).

**Unlimited means infinity.** A cap that should not limit anything is `double.PositiveInfinity`, never 0. Zero means zero: a solar panel at night offers 0 W.

## 3. API reference

C#-flavoured sketches targeting netstandard2.1 (Unity Mono) and net8.0. They show shape, not final names. Anything not listed is not part of the contract.

### 3.1 Construction and options

```csharp
public sealed class Circuit : IDisposable
{
    public static Circuit Create(CircuitOptions options);   // the only way in; no global state
    public CircuitOptions Options { get; }  public BackendInfo Backend { get; }   // what is running (3.18)
    public double Time { get; }  public void Dispose();     // Time: simulated s at the end of the last published step
}

public sealed class CircuitOptions
{
    // You will set
    public Grounding Grounding = Grounding.Explicit;
    public CableTypeRegistry CableTypes = new();             // named material + cross-section pairs; empty by default
    public double MeterWindow = 0.2;                         // s
    public double MaxStep = 0.25;                            // longest Step() run as one slice; a game-side hitch rule,
                                                             //   not an internal solver time step (3.8 rule 8)
    public Fidelity Fidelity = Fidelity.Game;                // the accuracy you need, not a method (3.18)
    // Advanced
    public int MaxCatchUpSlices = 4;  public int PendingLinkSteps = 2;   // 3.8 rule 8; 3.3
    public IBackendFactory? Backend = null;                  // null = Auto (3.18)
    public IJobScheduler? Scheduler = null;                  // null = all work on the thread that calls Step
    public TimeSpan? StepBudget = null;                      // overruns are reported, never acted on
    public double Ambient = 20;  public ulong Seed = 0;      // °C parts cool towards; seed for Rating.Spread only
    public MaterialRegistry Materials = MaterialRegistry.WithDefaults();   // copper, aluminium, iron, lead, tin...
    public PartKindRegistry Kinds = PartKindRegistry.WithDefaults();       // catalog, primitives, your own kinds
}
public interface IJobScheduler { void Run(int jobCount, IJob job); }   // run job.Execute(i) for each i; return when done
public interface IJob { void Execute(int index); }
```

**Which grounding do I pick?** It decides what "0 V" means. The physics of ground, earth and common return is in concepts.md.

| `Grounding` | Use when | 0 V is | You must wire |
| --- | --- | --- | --- |
| `Explicit` | the tablet, lessons | every `Ground` part | a `Ground` part in every separate section; a section without one is diagnosed `NoGround` |
| `CommonReturn` | one-wire devices (Stationeers) | the shared return, `NodeId.CommonReturn` | nothing: one-terminal parts return through it |
| `Earth` | two-wire circuits with optional earthing (VS) | the earth, `NodeId.Earth`, reached through `EarthElectrode` parts | both wires; a circuit with no electrode floats, which is normal here |

Under `CommonReturn`, a cable stands for a supply-and-return pair: its resistance is 2 · ρ · Length ÷ area, its current rating is per conductor and its `HeatPower` counts both conductors. This is exact when every load bridges a supply node to the return, as one-terminal parts do.

### 3.2 Ids

```csharp
public readonly record struct PartId(long Value) { public NodeId Node => new(Value); }   // a CablePiece's junction
public readonly record struct NodeId(long Value)
{ public static readonly NodeId None = default, Ground = new(-1), Earth = new(-2), CommonReturn = new(-3); }
public readonly record struct GroupId(long Value);
```

- You choose every id: a Stationeers `Thing.ReferenceId`, a packed VS world position, a tablet element number. An id is valid from the `Put` that creates it until the `Remove` that ends it; nothing inside the library reissues or invalidates it. Reusing a number after a step has applied its `Remove` gives a new object with no history. Within one step's batch of edits, a `Remove` followed by a `Put` of the same kind is a replace and keeps state.
- **Parts and nodes share one number space per circuit** (see Cable piece in §2). Groups and load areas have their own. Zero and negative ids are reserved for the library: `NodeId.None` (0) means "not connected", and each reference node is valid only in its own `Grounding` mode (`Ground` in Explicit, `CommonReturn` in CommonReturn, `Earth` in Earth).
- Your id *is* your user data; map it back with your own dictionary. An unknown id reads as `Quality.Missing` and is ignored by setters, never an exception.

### 3.3 Adding, replacing and removing parts

```csharp
public bool Put(PartId id, Part part, in Placement placement = default);   // add or replace; true if newly created
public bool Put(PartId id, string kind, Properties props, in Placement placement = default);   // = Put(id, new Custom(kind, props))
public bool Remove(PartId id);
public void Rewire(PartId id, int terminal, NodeId node);                  // move one terminal; state kept
public void ReplaceGroup(GroupId group, ReadOnlySpan<GroupEntry> parts);   // the group now lists exactly these
public void RemoveGroup(GroupId group);                                    // removes parts no other group lists
public EditScope BeginEdits();               // using (c.BeginEdits()) { ... }: all of it lands in the same step

public readonly record struct GroupEntry(PartId Id, Part Part, Placement Placement);
public readonly record struct Placement(long Area = 0, long SecondArea = 0);   // load areas; 0 = always loaded (3.13)
```

- **Order never matters.** A terminal may name a node nobody uses yet: an open end until its neighbour arrives. Results depend only on the final set of parts, so chunk loads and a 10,000-cable load need no sequencing. Every edit applies at the start of the next step (3.8). `BeginEdits` exists only to keep edits made from another thread together in one step (3.8 rule 4); it is not a speed tool.
- **Where the work runs.** Taking in edits happens inside the next `Step`, on its thread and the `Scheduler`. A large edit may make that step slower (`StepStats` shows it) but never changes its results (questions.md B3). `Put`'s return value is decided against the queued view: every edit made earlier in call order, including ones still waiting for the next step.
- **Two cable models, one physics.** Use `CablePiece` when your game describes wiring as cells touching neighbours; devices attach to a piece's junction. Use `Cable` when you know both end nodes (a tablet wire, a long pole line). Manatee may merge a straight run of pieces internally, but readings, `Along` and events always name your ids; a melt event names the specific hottest piece.
- **Missing links.** A `CablePiece` link to an id that has not arrived is *pending*. If it crosses into an unloaded load area, the network is frozen (3.13). Otherwise the network reads `Stale` for up to `PendingLinkSteps` steps, then the link counts as open until the target arrives. No network waits indefinitely.
- **Groups.** `ReplaceGroup` works out the difference against each group's final membership at the step boundary, so a part moving between groups keeps its state whatever order the calls came in. A part listed by several groups (a battery on two `CableNetwork`s) is removed only when no group lists it. If two calls give one id different records in one step, the last call wins and a `PartConflict` event names the id once.
- **Parameters versus state.** Re-putting the same kind with new parameters keeps its state: capacitor voltage, battery charge, temperature, overload heat, latched trips. Fields named `Initial...` apply only at creation or after `ResetState`, so a game that re-sends every record every tick never clobbers anything. Changing a part's kind restarts it from rest.
- **Errors.** Ill-formed input throws `ArgumentException` at `Put`: a negative resistance, an unknown material or cable type, a terminal left `None` that must be wired (an unset `Minus` means `NodeId.CommonReturn` under CommonReturn and is an error in other modes), a reference node outside its `Grounding` mode, or a limit set both on the part and in its `Rating`. A physically impossible circuit is not an error; it is network status (3.12).

### 3.4 Ratings and protection

One shape for every rated part. A field left at NaN is derived from the material, cable type or nameplate; `double.PositiveInfinity` means no limit.

```csharp
public readonly record struct Rating
{
    public double Current { get; init; } = double.NaN;              // A RMS carried forever (per conductor)
    public double Voltage { get; init; } = double.NaN;              // V peak (3.9) before insulation fails
    public double ThermalTimeConstant { get; init; } = double.NaN;  // s: cable tens of s, fuse under 1 s
    public double LimitI2t { get; init; } = double.NaN;             // A²·s of overload heat before it opens or trips
    public double Spread { get; init; }  public double Ambient { get; init; } = double.NaN;   // 0..1 seeded variation; °C
    public FailAction OnFail { get; init; }                         // Open (default: stop conducting, latched) or Report
}
public sealed record TripCurve   // how long each multiple of rated current may last before the breaker trips
{ public static TripCurve Instant, B, C, D;  public static TripCurve Table(params (double multiple, double seconds)[] points); }
```

- **Overload heat.** Every rated part keeps one overload-heat state: its current squared pours heat in, and it cools with `ThermalTimeConstant` (a first-order model). At rated current it settles just at `LimitI2t`; above it, it gets there sooner the bigger the overload. So any two of `Current`, `ThermalTimeConstant` and `LimitI2t` fix the third (`LimitI2t = Current² × ThermalTimeConstant`); set at most two. The model is driven by each step's ∫i² dt per part, so every backend feeds it the same quantity; it is scored in accuracy-and-performance.md. `PartReading.OverloadHeat` shows how full it is.
- **Trip curves** give time to trip against current ÷ `RatedCurrent`, at constant current; under varying current, the overload heat is what accumulates and the curve is its calibration. `B`, `C` and `D` are the IEC 60898-1 classes, with band values from the standard. `Table` interpolates log-log, never trips below its first multiple and holds the last time beyond its last point. `Instant` trips in the step whose RMS current exceeds the rating. `Spread` scales the rated current.
- **Opening.** Past its limit, an `Open` part opens by the end of the step at the latest (at the crossing instant on backends with `InStepEventTimes`) and never conducts in the next step. If several parts cross in one step, only the earliest crossing opens (each part's crossing time is estimated by linear interpolation of its heat over the step); the others are credited heat only up to that instant and re-checked at the next boundary under the current that then flows. So a correctly sized breaker trips before its cable melts, as in a real panel. Overvoltage failure compares the window `Peak` (3.9) with `Rating.Voltage`.
- **Interrupting inductive current is always legal.** When a part opens with current flowing in a transformer, motor, generator or inductor, the network stays `Live`; the stored energy (½ L I² of the interrupted loop) becomes `StepHeat` of the opening part, and the interruption spike does not count towards any other part's peak or overvoltage failure.
- `Spread` varies each part's limits by up to that fraction, decided once from `(Options.Seed, PartId)`: same seed, same parts, same burns. **Which part fails** is otherwise physics: the hottest first. `OnFail = Report` is for failures you script yourself.
- **Latched protection.** A blown fuse, tripped breaker or melted cable stays open whatever records or setters you keep sending; `SetClosed(true)` after a trip does nothing. Only `Rearm(id)` (the player replaces the fuse or re-arms the breaker) or `Remove` clears it.

### 3.5 Catalog parts

Game parts are plain parameter records deriving from `Part`, shared by the tablet and every game, so a lesson's lamp and a world's lamp behave the same. Every part also has `Rating? Rating { get; init; }` (null = derived) and `double MeterWindow { get; init; }` (0 = circuit default). Terminal fields come first; their order is the `terminal` index for `Rewire`. Exact equations per kind, for backend authors, go in a part-models spec (`backends/part-models.md`, to be written; machine type and battery efficiency are questions.md B10); this section says what each part does.

```csharp
public abstract record Part;
// Conductors and switching
record Cable(NodeId A, NodeId B, string CableType, double Length);            // R = resistivity × length ÷ area
record CablePiece(string CableType, double Length, PartId[] Links)            // a grid cell: conductor and junction
    { public Rating? Fuse { get; init; } }                                    // set: a weak piece that blows, not melts
record Wire(NodeId A, NodeId B);  record Switch(NodeId A, NodeId B, bool Closed = false);   // Wire: ideal connection
record ChangeoverSwitch(NodeId Common, NodeId A, NodeId B, bool ToB = false); // two-way switch (SPDT): Common to A or B
record Breaker(NodeId A, NodeId B, double RatedCurrent, TripCurve Curve, bool Closed = true);
record Fuse(NodeId A, NodeId B, double RatedCurrent, double LimitI2t = double.NaN);   // NaN = derived
record Relay(NodeId CoilA, NodeId CoilB, NodeId Common, NodeId NormallyOpen, NodeId NormallyClosed,
        double PullInVoltage, double DropoutVoltage, double CoilResistance);
// Loads
record Lamp(NodeId A, NodeId B, double RatedVoltage, double RatedPower);      // filament: resistance rises as it warms
record Heater(NodeId A, NodeId B, double RatedVoltage, double RatedPower);    // all power becomes heat
record ConstantPowerLoad(NodeId Plus, double RatedVoltage, double DropoutVoltage, double RestoreVoltage,
        double RestoreDelay = 1) { public NodeId Minus { get; init; } }      // an appliance drawing SetDemand watts
record Motor(NodeId A, NodeId B, int PolePairs, double RatedVoltage, double RatedPower);   // has a shaft (3.14)
// Sources and storage (InternalResistance NaN = derived from the nameplate, questions.md E5)
record PowerLimitedSource(NodeId Plus, double RatedVoltage, double InternalResistance = double.NaN)
    { public NodeId Minus { get; init; } }                                    // up to SetAvailable watts
record Generator(NodeId A, NodeId B, int PolePairs, double RatedVoltage, double RatedSpeed, double RatedPower,
        double InternalResistance = double.NaN, double InternalInductance = 0);   // voltage follows the shaft
record Battery(NodeId Plus, double FullVoltage, double EmptyVoltage, double EnergyCapacity,   // J (Units.Wh)
        double InternalResistance = double.NaN, double MaxChargePower = double.PositiveInfinity,
        double MaxDischargePower = double.PositiveInfinity, double Efficiency = 1, double InitialStateOfCharge = 1)
    { public NodeId Minus { get; init; }  public NodeId ChargeInput { get; init; } }   // set: charges only from here
// Two-ports and the rest
record Transformer(NodeId PrimaryA, NodeId PrimaryB, NodeId SecondaryA, NodeId SecondaryB,
        double PrimaryVoltage, double SecondaryVoltage, double RatedPower, double DesignFrequency);
record Converter(NodeId InPlus, NodeId OutPlus, double MaxPower, double Efficiency = 1, double QuiescentPower = 0)
{   public double OutputVoltage { get; init; } = double.NaN;   // regulated output (charger, power supply)...
    public double VoltageRatio { get; init; } = double.NaN;    // ...or output V = VoltageRatio × input V; set exactly one
    public NodeId InMinus { get; init; }  public NodeId OutMinus { get; init; } }
record EarthElectrode(NodeId Node, double ResistanceToEarth);  // a ground rod: Node to NodeId.Earth through soil
record Meter(NodeId A, NodeId B, PartId Clamp = default);      // voltmeter A to B, or a clamp on a part
record Custom(string Kind, Properties Props);                  // a registered kind (3.15), usable in groups
```

- **Defaults that teach.** Lamps and heaters share physics, but a lamp's filament warms up and burns out above about 1.3 × rated voltage. Transformers pass nothing at DC and overheat when run well below `DesignFrequency` (too little frequency for the core); that is a teaching point, not a bug.
- **Cable pieces.** Each piece is a star: an arm of `Length / 2` from its junction towards each link, using the piece's own cable type, so a link between two pieces is two arms in series and mixed cable types join naturally. A link exists when either end lists it, and a piece heats and melts as one unit on the sum of its arms' heat (both in questions.md, trivia defaults); its `Current` reading is the unsigned equivalent current √(`HeatPower` ÷ the piece's end-to-end resistance), which for a straight run is simply the current through it.
- **Constant-power loads** draw their demand at any voltage above `DropoutVoltage`; on AC, demand is mean power. Below it they switch off (`LoadDroppedOut`) and come back after the voltage has stayed above `RestoreVoltage` for `RestoreDelay`, at most one load per network per step. When the supply cannot meet total demand, loads drop out one at a time in a fixed order until the rest can run. They never take partial power; storage and converter inputs can (questions.md E4).
- **Power-limited sources** are a voltage source behind their internal resistance, delivering up to `SetAvailable` watts. At the cap they deliver exactly the available power and their voltage falls. `SetAvailable(0)` makes them an open circuit.
- **Converters** hold their output at `OutputVoltage` (or `VoltageRatio` × input voltage) behind a small output resistance, up to `MaxPower`; past it the output voltage falls so output power stays at `MaxPower`. The input draws output power ÷ `Efficiency` + `QuiescentPower`. Power flows input to output only (questions.md, trivia defaults). Ratio mode works on AC or DC and keeps the input frequency; regulated mode outputs DC. When the input cannot supply enough, the output sags rather than dropping out.
- **Catalog sources never fight.** They always have some internal resistance, so two batteries at different charge on one piece, or a battery shorted by a wire, draw a large but finite current and protection acts. Only loops of ideal parts (a `Wire`, a closed `Switch`, a `VoltageSource` with no internal resistance) can be diagnosed `ShortedSource` or `SourceConflict`.
- **Meters never change the circuit.** No current drawn, no resistance added, never joining networks. A clamp on a part that has not arrived reads `Quality.Missing` until it does.

### 3.6 Primitives

The textbook parts, for the tablet, tests and custom kinds. Same ids, readings and events as the catalog.

```csharp
record Resistor(NodeId A, NodeId B, double Resistance);
record Capacitor(NodeId A, NodeId B, double Capacitance, double InitialVoltage = 0);
record Inductor(NodeId A, NodeId B, double Inductance, double SeriesResistance = 0, double InitialCurrent = 0);
record VoltageSource(NodeId Plus, NodeId Minus, Waveform Waveform, double InternalResistance = 0);
record CurrentSource(NodeId From, NodeId To, Waveform Waveform);
record Diode(NodeId Anode, NodeId Cathode, double ForwardVoltage = 0.7);   // a one-way valve for current
record Ground(NodeId Node);                                                 // Explicit grounding: this node is 0 V

public abstract record Waveform
{
    public static Waveform Dc(double value);
    public static Waveform Sine(double rms, double frequency, double phase = 0, double offset = 0);
    public static Waveform Piecewise(params (double time, double value)[] points);   // linear between points
}
```

- Sources are described by shape (DC, sine, piecewise) so that every backend can run them. To vary a source, use `SetSource` or a controller.
- **Waveforms start when applied.** A sine's `phase` applies when its source is created; a later `SetSource` or re-`Put` with a new sine continues from the current phase, so changing frequency never jumps the phase. Piecewise times count from when the waveform was applied, and the last value holds after the last point.

### 3.7 Inputs: what the game changes every tick

Parameters are what a part *is*; change them by `Put`ting it again. Inputs are what the player or game *does* to it. Setters are idempotent, so a game may resend its whole state every tick; the library batches. A setter on a custom kind's id is ignored (questions.md, trivia defaults); its controller owns its inner parts.

```csharp
public void SetClosed(PartId id, bool closed);          // Switch, Breaker handle, ChangeoverSwitch (closed = ToB)
public void Rearm(PartId id);                           // clear latched protection only: re-arm a breaker, replace a fuse,
                                                        //   restore a melted cable; every other state is kept
public void ResetState(PartId id);                      // back to rest; re-applies Initial... fields
public void SetDemand(PartId id, double watts);         // ConstantPowerLoad: what the appliance wants now
public void SetAvailable(PartId id, double watts);      // PowerLimitedSource: what fuel, sun or wind allows now
public void SetEnabled(PartId id, bool enabled);        // any part: false = disconnected (load shedding)
public void SetSource(PartId id, Waveform waveform);    // VoltageSource, CurrentSource; phase-continuous (3.6)
public void SetShaftSpeed(PartId id, double radPerSec); // Generator, Motor (3.14)
public void SetStoredEnergy(PartId id, double joules);  // Battery, Capacitor: seed from game-owned save data
public void SetAmbient(PartId id, double celsius);      // the temperature this part cools towards
```

### 3.8 Time contract and threads

A **step** is one `Step` call, or one slice of it when it is split (rule 8). In normal use you make one `Step` call per game tick.

1. The circuit has a simulated clock `Time` in seconds, starting at 0. Only stepping advances it; it never goes back.
2. `StepStats Step(double dt)` advances it from `T` to `T + dt`. `dt` may differ on every call.
3. Every `Put`, `Remove`, `Rewire`, group call, setter and `LoadState` made since the previous step applies at the instant `T`, in call order (last write wins), and then holds constant for the whole step.
4. Edits made *while a step runs* (from another thread) queue for the next step and never tear the running one. Use `BeginEdits()` when several edits must land in the same step.
5. When `Step` returns, a new frame of readings is published atomically:

```
                 earlier steps          T            T + dt
 point value                                            •         the instant T + dt
 per-step value                         [===============]         step energy and heat, events
 window value    [======================================]         whole steps back from T + dt totalling at least W
```

   Window values cover the most recent whole steps whose total length is at least `W`, so a window may span several steps and is never shorter than the last step (with a 0.5 s tick, a 0.2 s window is the last step). Mean, RMS, peak, energy and AC values are kept per step and combined over the window.
6. Switching happens at step boundaries: with a 50 ms step on a 5 Hz supply, a switch can close only every 90° of the cycle. Finer event times are a capability (`InStepEventTimes`), not a promise.
7. Controllers (3.15) run only at step boundaries, never inside a step.
8. If `dt > MaxStep`, the call is split into equal slices of at most `MaxStep`, with results as if you had called `Step` for each. Beyond `MaxCatchUpSlices` slices, the rest is dropped and reported (`StepStats.DroppedSeconds`, a `StepTimeDropped` event); a server hitch never becomes a catch-up spiral. A split call publishes one frame: `StepLength` is the time actually simulated, `StepIndex` advances by the number of slices, per-step values cover the whole call, and controllers run at every slice boundary.

`bool Settle(double maxSeconds, double relativeChange)` advances until every reading changes by less than `relativeChange` per step, or `maxSeconds` of simulated time have passed, and returns whether it settled ("jump to steady state"). A backend may get there by any means; the result is within budget of stepping.

Call `Step` from one thread at a time; any thread will do. Every other member is thread-safe. Results never depend on the `Scheduler` (3.19). With a `StepBudget`, overruns are reported in `StepStats.OverBudget`; the library never lowers accuracy to fit, because that could change who trips. Game time running faster than real time (VS sleeping) is open (questions.md T6).

### 3.9 Readings

```csharp
public Readings AcquireReadings();   // the latest published frame, as a pooled lease (questions.md T7)

public readonly struct Readings : IDisposable      // immutable; any thread
{
    public double Time { get; }  public double StepLength { get; }  public long StepIndex { get; }
    public PartReading Part(PartId id);  public PartReading Part(PartId id, string inner);   // inner: custom kinds
    public NodeReading Node(NodeId id);  public NodeReading Along(PartId cable, double fraction);   // 0 = A .. 1 = B
    public GroupReading Group(GroupId group);  public NetworkInfo NetworkOf(PartId id);   // also of a NodeId or GroupId
    public int Networks(Span<NetworkInfo> into);  public int PartsIn(NetworkKey net, Span<PartId> into);
    public int GroupsIn(NetworkKey net, Span<GroupId> into);  public int Culprits(NetworkKey net, Span<PartId> into);
    public double Measure(PartId id, Quantity q);  public double Measure(NodeId id, Quantity q);   // a field by name
    public ChangedReading Compact(PartId id);      // for the replication feed (3.17)
    public void Dispose();                         // idempotent
}
```

A frame you hold never changes, however many steps run meanwhile. Frames come from a pool: the library keeps one more buffer than the most frames ever leased at once, so holding a frame across `Step` stays allocation-free after warm-up. Each lease carries a generation number; a read through a disposed lease, or through a copy of one, returns `Quality.Missing` and zeros, never another frame's data.

`PartReading` fields, all `double` unless noted. `WindowStats` is `(Mean, Rms, Peak)` over the window.

| Use | Fields |
| --- | --- |
| Trust | `Quality`; `Approximate` (bool: the backend left its declared accuracy here); `AsOf` (Time of the last step that advanced this part) |
| Tooltips | `Voltage`, `Current` (`WindowStats`, first terminal to second); `Power` (mean W absorbed); `Loading` (worst of current, voltage and heat against rating; 1 = at rating); `Condition` (`PartCondition`); `Powered` (bool: on and not dropped out); `Temperature` (°C) |
| AC displays | `Ac` (`AcValue`) |
| Accounting | `StepEnergy`, `StepHeat`, `StepStored` (J over the last step); `TotalEnergy`, `TotalHeat` (J since creation, never reset); `HeatPower` (W); `ThroughPower` (W from input to output of a two-port); `StoredEnergy` (J) |
| Storage and protection | `StateOfCharge` (0..1); `OverloadHeat` (0..1 of `LimitI2t`; recovers as the part cools) |
| Machines | `ShaftImpulse` (N·m·s, running counter; 3.14) |

```csharp
public readonly struct AcValue
{   public bool Present;  public double Frequency;   // false on a DC network (other fields 0); Hz, the network's (3.12)
    public double VoltageRms, CurrentRms;        // of the main sine component (the fundamental)
    public double VoltageAngle, CurrentAngle;    // rad, absolute (below)
    public double RealPower;                     // W: the average power actually used
    public double ReactivePower;                 // var: power flowing back and forth each cycle; + when current lags (inductive)
    public double PowerFactor; }                 // |RealPower| ÷ (VoltageRms × CurrentRms); 1 = all useful
public readonly struct NodeReading
{   public Quality Quality;  public bool Approximate;  public AcValue Ac;   // Ac current fields 0
    public WindowStats Voltage;        // to the Grounding option's reference (floating networks: below)
    public WindowStats VoltsToEarth;   // Grounding.Earth
    public bool Floating; }            // Grounding.Earth: isolated from earth; VoltsToEarth reads 0 (questions.md B6)
public readonly struct GroupReading { public double Demand, Available, Delivered, Loss;  public NetworkStatus Status; }  // W; worst
public enum Quality { Live, Stale, Frozen, LastGood, Missing }   // Missing: unknown id, not stepped yet, or a disposed lease
public enum PartCondition { Normal, OpenCircuit, Undervoltage, Overvoltage, Overloaded, SwitchedOff,
                            Tripped, Blown, Melted, BurnedOut, DroppedOut, Unsupported }
public enum Quantity { VoltageMean, VoltageRms, VoltagePeak, CurrentRms, Power, AcPowerFactor, /* every numeric field */ }
```

`Quantity` has one member per numeric reading field, named by its path with the dots removed: `Quantity.CurrentRms` is `Current.Rms`, `Quantity.AcPowerFactor` is `Ac.PowerFactor`. Scenario files use the dotted path.

**What the values mean.** Precise definitions, which every backend must meet, are in accuracy-and-performance.md ("Reading definitions").

- **Mean, RMS, peak** are over the window (3.8 rule 5). RMS is the value that heats a resistor. `Peak` is the largest absolute value of the true waveform in the window, after a fixed contract bandwidth for the fidelity; it never depends on what a trace shows, so a scope may show a spike above it. A circuit younger than `W` uses the history that exists.
- **AC values** describe the true waveform's main sine component over the window, at the network's frequency (3.12); a backend may compute them any way it likes. With several unrelated frequencies on one network, `Approximate` is set.
- **Angles are absolute,** measured against circuit time. The phase difference between two readings, even on networks either side of an open breaker, is `Angles.Wrap` of the difference of their angles; that is what a synchroscope shows. A part's own phase is `CurrentAngle − VoltageAngle`. **Drawing the wave yourself** (lamp flicker, dials): `v(t) = √2 · VoltageRms · cos(VoltageAngle + 2π · Frequency · (t − Time))`. Brightness follows power, which pulses at twice the supply frequency; never stream samples to clients for flicker.
- **Step values and counters.** `StepStored` is the change in the part's stored energy over the step, so step values balance per network to within the energy budget; `NetworkInfo.EnergyResidual` reports the remainder. Counters let a reader with its own cadence lose nothing: `(r2.TotalHeat − r1.TotalHeat) / (r2.AsOf − r1.AsOf)` is the mean heat power between two reads.
- **Two-port group totals** are split by port: each port's power counts in the group that lists the piece or node its terminal touches. A battery charging from one `CableNetwork` group and supplying another adds to `Demand` in the first and to `Available` and `Delivered` in the second. `ThroughPower` gives the signed flow for logic readouts.
- **Floating networks.** Under Explicit or Earth, node voltages in a network with no reference are measured from its lowest-id node, which reads 0. Part voltages are differences and are unaffected.

### 3.10 Oscilloscope traces

```csharp
public Trace AddTrace(TraceSpec spec);   public void RemoveTrace(Trace trace);   // costs work only while attached
public sealed record TraceSpec(Probe[] Channels, double Span = 1.0, double SampleRate = 0);   // Span: s kept; 0 = auto
public readonly struct Probe
{
    public static Probe Voltage(NodeId node);  public static Probe Voltage(NodeId node, NodeId reference);   // default: Grounding's
    public static Probe VoltageAlong(PartId cable, double fraction);  public static Probe Power(PartId part);   // absorbed
    public static Probe Current(PartId part, int terminal = 0);  // into that terminal
}
public sealed class Trace
{
    public double SampleRate { get; }  public double WaveformBandwidth { get; }   // what you get; Hz of faithful detail
    public double Sample(int channel, double t);                                  // any t in [Time − Span, Time]
    public int Copy(int channel, Span<double> times, Span<double> values);        // oldest first; allocation-free
}
```

- Every backend must provide traces; `WaveformBandwidth` says honestly how much detail is real.
- Samples sit at whole multiples of `1 / SampleRate` in circuit time, so traces requested at the same explicit `SampleRate` compare without resampling. At a step boundary the value is the one *after* that boundary's edits. History begins when the trace is attached.
- **Observation never changes gameplay.** A trace may make the backend compute extra waveform detail, but readings, events, trips, torques and network grouping are bit-identical with or without it (questions.md T2).

### 3.11 Events

```csharp
public int DrainEvents(Span<CircuitEvent> into);   // oldest first; thread-safe; events wait until drained

public readonly struct CircuitEvent
{   public EventKind Kind;  public PartId Part;  public long StepIndex;
    public NetworkKey Network;                                   // valid in the frame with this StepIndex (3.12)
    public double Time;                                          // inside the step; exact only with InStepEventTimes
    public double Current, Voltage, Power, I2t, Temperature;     // observed (A RMS, V peak, W, A²·s, °C)
    public FailCause Cause; }                                    // Overcurrent, Overvoltage, Overheat
public enum EventKind
{   FuseBlown, BreakerTripped,       // the part has already opened itself
    ConductorOverheating,            // Loading crossed 0.8 (smoke, hum); once per crossing
    ConductorMelted, PartBurnedOut,  // a cable or piece / any other part failed open (Cause says why)
    LoadDroppedOut, LoadRestored, RelayChanged, NetworkStatusChanged, StepTimeDropped, PartConflict, ControllerFailed }
```

**The game owns consequences.** On `ConductorMelted`, spawn fire or remove the voxel, then `Remove` the part (or `Put` a replacement). On `BreakerTripped`, flip the block's handle. The library only marks a failed part open; nothing happens to your world unless you do it.

### 3.12 Networks, status and diagnosis

Which flag answers which question:

| Question | Look at |
| --- | --- |
| Is this number current? | `Quality` |
| Is it within the backend's declared accuracy? | `Approximate` |
| What is this part doing? (show this in tooltips) | `PartCondition` |
| Is the part on? | `Powered` |
| Can its network be computed? | `NetworkInfo.Status` |
| Why not, and who is to blame? | `Diagnosis` and `Culprits` |

```csharp
public readonly record struct NetworkKey(long Value);

public readonly struct NetworkInfo
{
    public NetworkKey Key;  public NetworkStatus Status;  public Diagnosis Diagnosis;   // a diagnosis may warn while Live
    public int PartCount, GroupCount, CulpritCount;  public double Frequency;           // Hz; 0 = DC (below)
    public PartId WeakestLink;                          // the part with the highest Loading this step
    public double Available, Demand, Delivered, Loss;   // W: sources could give, loads asked for, loads got, heat
    public double EnergyResidual;                       // J: this step's energy-balance error; diagnostic only
}
public enum NetworkStatus { Live, Stale, Frozen, Unsolvable }
public enum Diagnosis
{   None, OpenCircuit, NoGround,           // warnings: no closed path (0 A); Explicit mode: a section without a Ground
    ShortedSource, SourceConflict,         // ideal parts only (3.5): a source shorted; ideal sources disagree in a loop
    CurrentSourceNoPath,                   // a CurrentSource has nowhere to push current
    PendingLink, Undervoltage, Overload,   // warnings: waiting for a linked part; loads dropped out; above rating
    NoSolution, UnsupportedPart }          // no consistent answer (culprits a best guess); a kind the backend cannot run
```

- **What a network is.** The parts connected through any part's terminals; all terminals of one part are in the same network. The reference nodes (`Ground`, `CommonReturn`, `Earth`) and meters never join networks. Leaving the reference nodes out is exact, not an approximation: a section whose only connection to the rest is one ideal 0 V node carries no net current through it. (Soil resistance between separate earth electrodes is not modelled: questions.md B9.) A network is a physical fact, not a scheduling unit, so toggling a switch can change it.
- **Keys.** A `NetworkKey` is the lowest part id in the network, not counting meters (a custom kind counts by its own id). It is valid in the frame it came from; later frames may not resolve it, so look it up again with `NetworkOf`.
- **Frequency.** A network's frequency is that of its reference AC source, a generator or sine source chosen by a fixed ranking (questions.md, trivia defaults). With no AC source it is the frequency carrying the most power in the window (0 if DC dominates), and AC values are `Approximate`.
- **Failures stay inside one network.** An `Unsolvable` network keeps its last good readings (`LastGood`), does not advance and is re-checked every step; others are unaffected. `Step` never throws because of circuit contents, and no reading is ever NaN.
- **`Stale` means only that a pending link is waiting** (3.3); it never depends on how fast a backend takes in an edit. **Slow state freezes while a network is not Live.** Battery charge, temperatures and overload heat do not advance while it is `Stale`, `Frozen` (3.13) or `Unsolvable`, so they cannot drift from a fallback model the game runs meanwhile.
- **Fallback hand-off.** While a network is not `Live` (including before its first step, when readings are `Missing`), a game may run its own fallback. Apply it to every group the network touches (`GroupsIn`), never just one. When the network returns to `Live`, seed game-owned state back with `SetStoredEnergy` before the next step.
- Diagnoses are an enum plus culprit ids; your client writes and translates the words ("0 A: the circuit is incomplete").

### 3.13 Load areas: chunk loading

`public void SetAreaLoaded(long area, bool loaded);` Areas start unloaded, except area 0, which is always loaded; games without chunks (Stationeers, the tablet) leave everything there.

- A network touching any unloaded area is **frozen whole**: it does not step, its readings keep their values with `Quality.Frozen`, and its slow state does not advance. When every area it touches is loaded, it resumes from the frozen state, with no catch-up. (VS's own mechanical networks also stop while not fully loaded, but rebuild on reload; Manatee resumes.)
- A part crossing an area border names both (`Area`, `SecondArea`), so a half-loaded network is frozen rather than simulated as a false open circuit. On unload, leave the parts in place; they keep their state.

### 3.14 Mechanical port

Generators and motors have a shaft. Your game owns the shaft (speed, inertia, friction, the mechanical network); Manatee sees only the speed you give it and reports the torque the electrical side pushes back with.

- **In:** `SetShaftSpeed(id, radPerSec)`, held until changed. Voltage and frequency follow the speed's magnitude; Manatee tracks the electrical angle from the speeds given, which yields frequency, phase and synchronisation. If your mechanical side stops updating a machine, freeze it (put it in a load area you mark unloaded) rather than leave a stale speed.
- **Out:** `PartReading.ShaftImpulse`, a running counter, signed against the direction of rotation: positive always brakes the shaft (generating), negative drives it (motoring). Difference it yourself or use the helper:

```csharp
public struct TorqueMeter { public MeanTorque Update(in PartReading machine); }   // one per reader; allocation-free
public readonly record struct MeanTorque(double Torque, double Seconds, bool Held);
// Mean counter-torque since this meter's last Update, over Seconds of simulated time. Seconds == 0 (no step
// completed since) or a Frozen network: the previous mean repeats with Held = true (questions.md B5).
```

Each reader keeps its own meter, so two readers never steal each other's interval, and a mechanical system that asks at irregular times never reads a false zero.

### 3.15 Custom parts, controllers and registries

Your own devices are built like prefabs: combine catalog parts and primitives, and add a **controller**, a small script that runs between steps, reads its own inner parts' meters, and flips their switches or changes their demand (a thermostat, a charge controller). New equation-level parts are a backend matter ([backends/README.md](backends/README.md)).

```csharp
public interface IPartKind { string Name { get; } void Build(ref PartBuilder b, Properties props); }  // "mymod:arc-furnace"
public ref struct PartBuilder
{   public PartId Id { get; }  public NodeId Terminal(string name);   // = props.Node(name): where the client wired it
    public NodeId Internal(int index);             // a private node unique to this instance
    public void Add(string inner, Part part);      // an inner part, addressed by local name
    public void Control(IController controller, double rateHz); }
public interface IController { void Update(ref ControllerContext ctx); }
public ref struct ControllerContext
{
    public double Time { get; }  public double Dt { get; }  public PartReading Read(string inner);   // Dt: since last run
    public void SetClosed(string inner, bool closed);  public void SetDemand(string inner, double watts);   // own inner parts only
    public void SetSource(string inner, Waveform waveform);  public void Put(string inner, Part part);
    public Span<byte> State { get; }                           // up to 256 bytes, saved and restored with the part
}
public sealed class Properties                                 // neutral property bag; no JSON library in the core
{
    public double Number(string key, double fallback = 0);  public bool Flag(string key);  public bool Has(string key);
    public string? Text(string key);  public NodeId Node(string key);    // and Set(key, value) for each of those types
}
public sealed record Material(double Resistivity, double TemperatureCoefficient, double MeltTemperature,
    double HeatCapacityPerVolume, double CurrentDensity, bool Insulator = false);
    // Ω·m at 20 °C, 1/K, °C, J/(m³·K), A/m² carried forever in free air (sets default cable ratings)
public sealed record CableType(string Material, double CrossSection, bool Insulated = true, Rating? Rating = null);
// options.Materials.Register("mymod:silver", m);  options.CableTypes.Register("heavy", t);  options.Kinds.Register(kind);
```

- `Put(id, "mymod:arc-furnace", props)` runs the kind's `Build`. Terminals are `Properties` entries holding node ids. Read inner parts with `readings.Part(id, "element")`; `Remove(id)` removes them all; saves need only the composite's id.
- **Built-in kinds are registered by name too** (`"manatee:lamp"`...). Their property keys are their record parameter names in camelCase (`resistance`, `a`, `b`, `ratedVoltage`), the same keys as scenario files, so a lesson file, a VS block JSON and a C# record describe a part the same way. Game adapters build `Properties` from their own data.
- **When controllers run.** At each step boundary (and each slice boundary of a split call), after the client's edits for that boundary, in id order, whenever `1 / rateHz` has passed since their last run. They see the latest readings (inside a split call, the previous slice's), and their writes apply at that boundary. They do not run while their network is not `Live`; `Dt` then spans the gap. A rate faster than the step means once per step. A controller that throws is disabled and reported (`ControllerFailed`).

### 3.16 Persistence

The game owns the topology: after loading, rebuild with `Put`. Manatee optionally saves *physical state* by part id.

```csharp
public void SaveState(IBufferWriter<byte> output, ReadOnlySpan<PartId> ids = default);   // default = everything
public LoadReport LoadState(ReadOnlySpan<byte> input);   // after rebuilding; queued like an edit; never throws on content
public sealed record LoadReport(int Restored, double SavedAtTime, string FormatVersion,
    IReadOnlyList<PartId> StartedAtRest, IReadOnlyList<PartId> Ignored);   // present but not saved; saved but gone
```

A snapshot holds named physical fields only, never backend internals, so it loads into any backend and later version.

| Part kind | Saved state |
| --- | --- |
| Capacitor | voltage |
| Inductor; Transformer, Generator, Motor (each winding) | current |
| Battery | stored energy, temperature |
| Fuse, Breaker, Cable, CablePiece, Lamp, Heater | overload heat, temperature, latched open |
| Generator, Motor; sine and piecewise sources | electrical angle; a sine's accumulated phase, a piecewise source's time since applied |
| Relay, switches | contact position |
| ConstantPowerLoad | dropped out or not, restore timer |
| Custom kinds | inner parts as above, plus each controller's `State` bytes |

- **When.** `SaveState` captures the state at the last published step boundary (`SavedAtTime`) and never blocks a running step. `LoadState` queues like an edit, after earlier queued edits, and its report is computed against the part set as it will be once those apply.
- **Time never goes back.** Loading never changes `Time`, and running counters keep running. Saved angles are re-expressed against the current `Time`, so phase relations survive among the parts in one `SaveState` call; per-part snapshots taken at different times cannot keep phase relations with each other (machines resume at their saved angle). To start over (a tablet's "reset lesson"), create a fresh `Circuit`.
- **After a load,** meter windows and trace history start empty. A part with no saved state starts at rest: capacitors empty, no winding current, batteries at `InitialStateOfCharge`, ambient temperature, protection closed. State is within the accuracy budget at once; readings are within budget after one window `W` (loaded into a different backend: once the circuit's slowest electrical transient has settled).
- Games may skip snapshots. Game circuits settle within seconds from rest; battery charge and blown fuses are what players notice, and games can keep those themselves (`SetStoredEnergy`, seeding when `Put` returns `true`).

### 3.17 Replication feed

The server is authoritative; clients receive readings and never simulate world circuits. Multiplayer tooltips run on clients, so they need this feed.

```csharp
public ChangeFeed CreateChangeFeed(in ChangeThreshold threshold);   // one per consumer
public sealed class ChangeFeed : IDisposable { public int Drain(Span<ChangedReading> into); }  // moved since last Drain
public readonly record struct ChangeThreshold(double Relative = 0.02, double AbsoluteVolts = 0.1, double AbsoluteAmps = 0.01,
    double AbsoluteRadians = 0.05, double RelativeHz = 0.005);
public readonly record struct ChangedReading(PartId Id, double AsOf, float VoltageRms, float VoltageMean, float CurrentRms,
    float CurrentMean, float Power, float Temperature, float Loading, float AcFrequency, float AcPhaseOffset,
    PartCondition Condition, Quality Quality);
```

- `AcPhaseOffset` is `Angles.Wrap(VoltageAngle − 2π · Frequency · AsOf)`, constant in steady state, so phase drift replicates without flooding. A client draws `v(t) = √2 · VoltageRms · cos(AcPhaseOffset + 2π · AcFrequency · t)` in server circuit time, estimating the server's `Time` from `AsOf`. The means keep DC polarity.
- A feed reports changes only; a condition or quality change always counts. When a part enters a player's interest set (join, approach, meter equipped, chunk sent), send `Readings.Compact(id)` for it. Filtering by player is the game's job.

### 3.18 Backends and fidelity

```csharp
public enum Fidelity { Game, Lesson, Strict }   // the accuracy you need, not how to get it
public static class Backends { public static IReadOnlyList<IBackendFactory> Available { get; } }
public interface IBackendFactory { BackendInfo Info { get; } }   // writing one: backends/README.md

public sealed class BackendInfo
{
    public string Id, Version;                                        // e.g. "manatee.default" "3.1.0"
    public IReadOnlyCollection<string> PartKinds;  public IReadOnlyCollection<Fidelity> Fidelities;
    public IReadOnlyDictionary<Fidelity, AccuracyClass> Accuracy;     // declared error per level
    public IReadOnlyDictionary<Fidelity, double> ErrorRatio;          // measured worst error ÷ budget, from the scorecard
    public IReadOnlyDictionary<Fidelity, double> WaveformBandwidth;   // Hz of faithful trace detail
    public IReadOnlyDictionary<Fidelity, double> MinTimeConstant;     // s: anything faster shows only through its energy (§5)
    public bool MixedFrequency;      // unrelated frequencies on one network without Approximate
    public bool InStepEventTimes;    // events and openings at the exact instant inside a step
}
public sealed record AccuracyClass(double DcRelative, double MeterRelative, double PeakRelative, double PhaseRadians,
    double PowerFactor, double WaveformNrmse, double EnergyBalance, double I2tRelative, int TripTimeSteps,
    double ShaftTorqueRelative);   // one field per row of the budget table
```

- **Auto** (the default) picks one backend per circuit at creation, from the fidelity and options, among backends that support every built-in kind, and never switches during a run (questions.md B2). Tests and benchmarks name backends explicitly.
- **Fidelity is a request.** The backend meets it however it likes and sets `Approximate` on readings where it cannot (out-of-range frequency, mixed frequencies).
- **Unsupported parts.** A named backend throws `NotSupportedException` at `Put` for a kind it cannot run. Under Auto this never happens for built-in kinds; a network reads `UnsupportedPart` only for a kind added by a backend plugin that the chosen backend cannot run.
- **Accuracy budgets** live in one table in [accuracy-and-performance.md](accuracy-and-performance.md) §4, one `AccuracyClass` field per row; `Strict` is ten times tighter than `Lesson`, and the Game column is open (questions.md T1). A backend ships for a fidelity only when its scorecard passes that column; `ErrorRatio` is the worst ratio over all scored observations, and the scorecard has the breakdown.

### 3.19 Determinism and allocation

- **Same backend, version, runtime and `Seed`, and the same sequence of steps (each step's `dt` and the edits applied at its start): bit-identical results,** whatever the scheduler or thread count. Backends split work only along partitions that do not depend on the thread count, and `Put` order within one step's batch never matters. Edits from other threads land in whichever step they reach (3.8 rule 4), so replaying a player's case needs a record of each step's `dt` and edits (questions.md B7).
- **Different runtimes** (net8.0 in CI, the VS server, Unity Mono) and **different backends** agree within their accuracy classes. Lessons and tests compare with tolerances, never bit-for-bit.
- **Zero allocation is a release contract on Unity Mono.** A steady `Step`, the setters in 3.7, `AcquireReadings` and every read on a frame, `DrainEvents`, `ChangeFeed.Drain`, `Trace.Copy` and `TorqueMeter.Update` allocate zero managed bytes, with up to K frames held at once once K has been reached. Rebuild-time calls may allocate: `Put` of a new part, `ReplaceGroup`, `AddTrace`, `SaveState` and `LoadState`.

### 3.20 Telemetry

```csharp
public readonly struct StepStats { public double WallSeconds, DroppedSeconds;  public long AllocatedBytes;  public bool OverBudget;
    public int NetworksLive, NetworksStale, NetworksFrozen, NetworksUnsolvable; }   // returned by Step
public sealed class TelemetrySummary { public double P50Wall, P99Wall;  public IReadOnlyList<NetworkKey> CostliestNetworks; }   // rolling
public TelemetrySummary Telemetry { get; }
public int BackendCounters(Span<(string Key, double Value)> into);  // backend-specific; profiling only
```

Telemetry reports outcomes (time, memory, status), never mechanics. Tests may print backend counters but never assert on them.

### 3.21 Testing and scoring backends

The test and benchmark harness is an ordinary client of this API, so every backend, including one written later, is scored for accuracy and speed the same way. From every backend it needs: `Trace.Sample` at any time in span, window readings including AC values and peaks, per-step energy, heat and stored change, events with times, and `Readings.Measure` for every `Quantity`. The scenario format (which is also the lesson format), truth sources, accuracy budgets, reading definitions, backend-neutral checks and the scorecard are specified in [accuracy-and-performance.md](accuracy-and-performance.md).

## 4. Walkthroughs

Worked integrations for Stationeers (Re-Volt), Vintage Story and the tablet, including the behaviour changes Re-Volt needs Sukasa's sign-off for, are in [guide.md](guide.md).

## 5. What the API deliberately does not promise

These are backend choices. Clients must not depend on them; tests must not assert on them.

- **How answers are computed:** internal time resolution, how AC is represented, iteration, how networks are split, reduced or cached. A topology change may make the next step slower; `StepStats` shows how much, and nothing else does.
- **Detail the contract does not cover:** instantaneous values outside traces; trace detail above `WaveformBandwidth`, and whether a trace value was computed at that instant or rebuilt from AC values; events faster than `Backend.MinTimeConstant` (the surge when a motor starts, a spark, two generators connected out of step), which show up through their energy rather than a detailed waveform: the energy they dissipate (for example ½ C ΔV² when a capacitor is switched onto a source) becomes heat in the resistive parts of the path in that step; peaks of spikes faster than the contract peak bandwidth; event times inside a step unless `InStepEventTimes`.
- **Bit-exactness across runtimes, versions or backends** (3.19), or which network key an id has after a topology change (ask `NetworkOf` each time).
- **Game-side physics:** shaft inertia and speed; room heat, climate, fire, sound and rendering. Manatee reports torque and heat per part and stops there.

## 6. Open decisions

The open decisions on this API, each with a leaning, are in [questions.md](questions.md). They are referred to here by label, as "questions.md B3"; "questions.md, trivia defaults" points to the one-line list of minor defaults at its end.
