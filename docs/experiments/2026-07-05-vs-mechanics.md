# Mechanical network facts for R14 — verified against engine source

All paths relative to `/home/svein/dev/manatee/third_party/vssurvivalmod/Systems/MechanicalPower/` unless noted. These findings extend (and in a few places correct/sharpen) the existing "Mechanical Network Coupling (R14)" section of `docs/vintage-story.md` (lines 117–154); existing citations there checked out (`updateNetwork` at `Network/MechanicalNetwork.cs:203`, 20 ms mod tick at `MechanicalPowerMod.cs:92`, `TrueSpeed` at `BlockEntityBehavior/BEBehaviorMPConsumer.cs:23` — all confirmed).

## 1. Joining a network and reading speed

- A device is a `BlockEntityBehavior` subclass of `BEBehaviorMPBase`, which implements `IMechanicalPowerDevice`/`IMechanicalPowerNode` (`BlockEntityBehavior/BEBehaviorMPBase.cs:55`).
- **Producers** set `OutFacingForNetworkDiscovery` (e.g. rotor ctor, `BEBehaviorMPRotor.cs:57`); on server `Initialize` they call `CreateJoinAndDiscoverNetwork` (`BEBehaviorMPBase.cs:171-174`, method at `:419`), which either creates a network via `manager.CreateNetwork` (`:433`; `MechanicalPowerMod.cs:354`) or joins a neighbor's, then flood-fills via `spreadTo` → `JoinAndSpreadNetworkToNeighbours` (`BEBehaviorMPBase.cs:516`, `:486`), gated by `IMechanicalPowerBlock.HasMechPowerConnectorAt` (`:530`).
- **Consumers/passive blocks never create networks** (design comment, `MechanicalPowerMod.cs:30`); they get swept up by a producer's discovery flood, or connect on placement via `WasPlaced` → `tryConnect` (`BEBehaviorMPBase.cs:187-198`, `:200-251`).
- Membership = `JoinNetwork` (`BEBehaviorMPBase.cs:253`) → `MechanicalNetwork.Join` inserts into a `Dictionary<BlockPos, IMechanicalPowerNode> nodes` (`Network/MechanicalNetwork.cs:91-99`).
- **Reading speed:** `Network.Speed` is the raw signed network speed (`MechanicalNetwork.cs:64-68`); each node's local speed is `Speed × GearedRatio`. `BEBehaviorMPConsumer.TrueSpeed` = `Math.Abs(Network?.Speed * GearedRatio ?? 0f)` (`BEBehaviorMPConsumer.cs:23`) — **absolute value, direction discarded**; direction is available via `IsRotationReversed()` (`BEBehaviorMPBase.cs:129-134`), used exactly that way by the quern (`vssurvivalmod/Block/BlockQuern.cs:141`). Consumer wiring pattern: `BEQuern.cs:212-227` grabs the behavior with `GetBehavior<BEBehaviorMPConsumer>()` and uses the `OnConnected`/`OnDisconnected` hooks (`BEBehaviorMPConsumer.cs:15,56-66`).

## 2. The torque/resistance contract

- Single interface method: `float GetTorque(long tick, float speed, out float resistance)` (`Network/IMechanicalPowerNode.cs:19`).
- **Sign convention** (doc comment `:13-17`): positive torque = clockwise when looking north or east; consumers return 0 torque and a positive `resistance`. Resistance is unsigned brake drag — it enters the sum as `Math.Abs(r) * resistance` (`MechanicalNetwork.cs:225`), so a consumer **cannot push the shaft**; motoring requires returning torque like a rotor.
- Call site: `updateNetwork` iterates all nodes, passing `speed = networkSpeed × node.GearedRatio` (signed) (`MechanicalNetwork.cs:219-227`). Contribution: `totalTorque += GearedRatio * t`; `totalResistance += |GearedRatio| * resistance + speed²·GearedRatio²/1000` (per-node "air resistance", `:226`).
- Base-class default: `GetTorque` returns 0 and delegates to abstract `GetResistance()` (`BEBehaviorMPBase.cs:403-409`); `BEBehaviorMPConsumer` overrides `GetResistance` to return its `Resistance` field (`BEBehaviorMPConsumer.cs:68-71`) — override that for the alternator's load-dependent counter-torque.
- **Cadence:** server tick listener registered at 20 ms (`MechanicalPowerMod.cs:92`), `ServerTick` calls `updateNetwork` every 5th tick ≈ 100 ms (`MechanicalNetwork.cs:160-163`). Listener intervals are best-effort ("may call your method slightly later", `vsapi/Common/API/IEventAPI.cs:181-187`).
- **Thread:** server main thread (game tick listener). No locking anywhere in `MechanicalNetwork`/`MechanicalPowerMod` — the subsystem assumes single-threaded server access.
- **Out-of-band calls exist:** `BEBehaviorWindmillRotor.cs:197` calls `network.updateNetwork(...)` directly when sail count changes. `GetTorque`/`GetResistance` must therefore be cheap, re-entrant-safe reads of cached state at any time on the server main thread — never trigger an electrical solve inline.

## 3. The actual speed-update equation (`MechanicalNetwork.cs:203-278`)

Not a physical inertia integrator — an ad-hoc clamped relaxation:

- `unusedTorque = |totalTorque| − totalResistance`; `torqueSign = sign(totalTorque)` (`:237-238`).
- Pseudo-inertia: `drag = max(1, nodeCount^0.25)`, `step = 1/drag` (`:240-241`) — more blocks = slower speed changes. There is **no stored momentum state**; "inertia" is purely this per-update rate limit.
- Accelerating (`unusedTorque > 0`, turning with the torque): `speed += min(0.05, step·unusedTorque)·torqueSign` (`:246`) — acceleration hard-capped at 0.05 per update.
- Decelerating / wrong direction: `change = unusedTorque` (or `−totalResistance` if turning against torque), clamped to `≥ −|speed|` ("momentum effect", `:250-252`), then `speed = max(1e-6, |speed| + step·change)·sign(speed)` (`:256`); a dead-stopped network restarts at `torqueSign/1e6` (`:260`).
- `totalAvailableTorque` is a separately slewed value (±`step` per update, decays ×0.9 when saturated, `:264-275`) — it is a smoothed telemetry/display quantity, **not** the instantaneous torque sum; don't use it as physics input.
- **Units:** `ServerTick` does `UpdateAngle(speed·dt·50)` and `UpdateAngle` adds `arg/10` radians (`:156-158`, `:185-189`), so ω = 5·speed rad/s: a typical speed of 1.0 ≈ 0.8 rev/s. Electrical frequency `f = 5·speed·polePairs/(2π)` Hz — realistic 50 Hz at speed 1.0 would need ~63 pole pairs, so R14 needs gearing (GearedRatio), a fat pole count, or an admitted frequency scale factor. This deserves a design.md note.

## 4. Server/client split

- **Server-authoritative.** `updateNetwork` runs only inside `ServerTick` (`MechanicalNetwork.cs:156-169`), driven by the server-side-only tick listener (`MechanicalPowerMod.cs:91-99,152-162`). `GetTorque` is never called client-side.
- **Sync, two channels:**
  1. Network state: `broadcastData` every 40th server tick ≈ 800 ms (`MechanicalNetwork.cs:165-168,171-183`) sends `MechNetworkPacket` {speed, direction, angle, totalAvailableTorque, networkResistance, networkTorque} over channel `"vsmechnetwork"` (`MechanicalPowerMod.cs:80-99`). Client applies it in `UpdateFromPacket`, storing `|speed|` (client speed is always positive; direction is a separate enum) (`MechanicalNetwork.cs:280-297`).
  2. Membership: `NetworkId` rides the block entity's tree attributes (`BEBehaviorMPBase.cs:357-364`); the client joins/leaves its local network mirror in `FromTreeAttributes` (client-only branch, `:321-345`; explicit comment at `:326` that networkId is never trusted from the tree server-side).
- Client animation: `ClientTick` runs from the render loop (`MechanicalPowerMod.cs:78`, `OnRenderFrame` `:376-384`); `clientSpeed` slews toward server speed at ≤0.01 per 20 ms-equivalent and the angle is nudged toward the server angle (`MechanicalNetwork.cs:140-152`). **Client-side `TrueSpeed` is thus a smoothed, up-to-~800 ms-stale visual value** — fine for rendering the alternator, unusable as electrical-sim input. The electrical sim must run server-side and ship its own telemetry (matches the existing plan in `docs/vintage-story.md`).

## 5. Complications for the design.md plan

The plan (shaft speed as piecewise-constant source parameter per ~100 ms mech tick; alternator counter-torque reported back time-averaged) survives, with these sharp edges:

1. **The ~100 ms cadence is nominal, not guaranteed.** Tick listeners can slip (`IEventAPI.cs:181`), and `updateNetwork` can fire out-of-band (`BEBehaviorWindmillRotor.cs:197`). `GetResistance`/`GetTorque` must be pure reads of an atomically-updated cached counter-torque, valid whenever called.
2. **Resistance can't motor.** The `|r|·resistance` summation (`MechanicalNetwork.cs:225`) makes consumer resistance direction-agnostic drag. A generator that should sometimes motor (grid-driven synchronous machine) must instead be a rotor-style node returning signed torque via `GetTorque`. For a plain alternator this is fine; note it for any future motor-generator block.
3. **Direction is lossy at the consumer.** `TrueSpeed` is `|speed|`; if phase sequence ever matters, recover sign from `IsRotationReversed()`/`network.TurnDir`.
4. **Don't model the shaft dynamics.** The speed law is explicitly unphysical and tuned by eye (`BEBehaviorMPRotor.cs:93-95`: "found by empirical testing of what looks good and plausible in-game, not science"), with hard caps (0.05 accel per update), a `max(1e-6,...)` floor, and anti-oscillation clamps from 1.14rc5 (`MechanicalNetwork.cs:253-256`). Treat shaft speed strictly as a black-box input. Helpful bound: a resistance step can remove at most `step·|speed|` per update (`:252`), so a sudden electrical load crashes frequency smoothly rather than instantly — good for the loose coupling's stability.
5. **Network identity churns.** Any node removal invalidates and rediscovers the whole network — new `networkId`, new `MechanicalNetwork` object, speed/angle carried over with possible sign flip (`MechanicalPowerMod.cs:233-280`, esp. `:272-275`). The alternator must rebind via `OnConnected`/`OnDisconnected` (`BEBehaviorMPConsumer.cs:15`) and never cache the network object across ticks without a null/id check.
6. **Overheat cadence and thresholds:** checked every 10th server tick (200 ms); smoke at node speed > 4.5, `OverheatValue` accumulates above 5.5, fire ignition currently commented out upstream (`MechanicalPowerMod.cs:163-190`). Still a usable over-rev hazard hook, but don't rely on the vanilla fire consequence.
7. **Unit/frequency mismatch** (see section 3): ω = 5·speed rad/s means realistic mains frequency requires a large pole-pair count or a declared in-game frequency scale — a design.md decision, not a code detail.
8. **`totalAvailableTorque` in the broadcast packet is slewed telemetry** (`MechanicalNetwork.cs:264-275`), not the torque sum; any UI showing "shaft torque" should use `networkTorque`/`networkResistance` instead.
