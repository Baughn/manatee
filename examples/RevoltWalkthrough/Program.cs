using System.Buffers;
using Manatee.Core;
using Manatee.Core.Devices;
using Manatee.Core.Reduction;

namespace Manatee.Example.RevoltWalkthrough;

/// <summary>
/// The runnable half of docs/integration-tutorial.md: a miniature Re-Volt-shaped
/// client, built in the tutorial's section order. Three "cable networks" on one
/// ClientPartitioned DC netlist; built-in AdaptedLoads on the quiet network,
/// key-re-pinnable TutorialLoads on the two churning ones; a breaker coupler; and
/// a ~60-tick run with scripted mid-run events (breaker close/open, a snapshot +
/// additive restore across the merge, a cable cut, a fuse overload melted through
/// limit attribution). Every tick prints TickStats so you can watch the cost
/// tiers move. Run: nix develop -c dotnet run --project examples/RevoltWalkthrough
/// </summary>
public static class Program
{
    public static int Main()
    {
        var w = new Walkthrough();
        w.Construct();          // tutorial §2 — options bundle, wiring policy, partitioning
        w.BulkLoadGrid();       // tutorial §3 — ConductorGraph intake, BulkBuild, keys
        w.AttachDevices();      // tutorial §4 — devices, two populations, one contract
        w.FirstSolve();         // tutorial §3/§9 — op-pt, first-boot re-pin, cost queries
        w.RunScriptedTicks(50); // tutorial §5–§8 — the canonical tick loop + events
        w.RunSteadyEpilogue(12);// tutorial §9 — prove the settled fleet lives in tier ≤1
        return w.VerifyTelemetry();
    }
}

internal sealed class Walkthrough
{
    // ── Grid shape ─────────────────────────────────────────────────────────────
    private const double Dt = 0.5;                     // the Stationeers half-second tick
    private const double MainsVolts = 120.0;
    private const int BusLen = 12;                     // segments per active cable run
    private static readonly int[] TapAt = { 4, 8 };    // load taps along each run

    private const int NetA = 1;   // quiet network: raw nodes, built-in AdaptedLoads, never churns
    private const int NetB = 2;   // active network: cable graph; suffers a cable cut
    private const int NetC = 3;   // active network: cable graph; blows its fuse

    // ── The two long-lived objects a client owns ──────────────────────────────
    private Netlist _net = null!;
    private ConductorGraph _graph = null!;
    private DeviceHost _host = null!;                  // serial driver for the built-in devices

    // ── Device populations (tutorial §4: two populations, one contract) ───────
    private readonly List<AdaptedLoad> _quietLoads = new();     // private handles; static island only
    private readonly List<TutorialLoad> _activeLoads = new();   // key-re-pinnable; churn-safe

    private CouplerId _breaker;                        // document-stable: NEVER needs re-pinning (§16)
    private NodeId _quietRail;                         // quiet island never rebuilds ⇒ safe to cache
    private readonly NodeId[] _quietTaps = new NodeId[2];

    // ── Client-owned drain buffers: allocated once, at shape time (§9 zero-alloc) ──
    private readonly IslandChange[] _chgBuf = new IslandChange[64];
    private readonly LimitEvent[] _limBuf = new LimitEvent[16];

    // ── Scripting + telemetry ──────────────────────────────────────────────────
    private readonly Queue<(string Label, Action Op)> _pending = new();
    private readonly List<string> _notes = new();
    private long _tick;
    private byte[]? _blobB, _blobC;
    private long _totalStale;
    private int _melts, _cuts, _restoreMatched, _restoreOrphans;
    private TickStats _lastStats;

    // ── Keys: the client-stable identity scheme (tutorial §3). Disjoint ranges per
    //    concept; here they're synthetic, in Re-Volt they pack the game's RefIds. ──
    private static PartitionKey Part(int n) => new((ulong)n);
    private static JunctionKey J(int n, int i) => new((ulong)(n * 1000 + i));
    private static SegmentKey Seg(int n, int i) => new((ulong)(n * 1000 + i));
    private static ExternalKey SourceKey(int n) => new(0x50, (ulong)n);
    private static ExternalKey LoadKey(int n, int k) => new(0xA0, (ulong)(n * 10 + k));
    private static ExternalKey QuietRailKey => new(0x25, 1);
    private static ExternalKey QuietNodeKey(int j) => new(0x20, (ulong)j);
    private static ExternalKey QuietWireKey(int j) => new(0x26, (ulong)j);
    private static ExternalKey BreakerKey => new(0xB0, 1);

    // ═══════════════════════════════════ §2 construction ═══════════════════════

    /// <summary>One Netlist per game world, fixed at birth by the options bundle.
    /// Stationeers(dt): DC profile, ReferenceBound wiring (single-terminal vanilla
    /// devices bind their returns to the partition's reference rail — no per-device
    /// grounding code, ever), ClientPartitioned (vanilla CableNetworks must never
    /// merge except through a coupler; a commit that would is thrown out atomically).</summary>
    public void Construct()
    {
        _net = new Netlist(NetlistOptions.Stationeers(Dt));
        _graph = new ConductorGraph(_net, GraphOptions.PrePartitioned);
        _host = new DeviceHost(_net);
        Console.WriteLine("== construct: Stationeers bundle (DC dt=0.5, ReferenceBound, ClientPartitioned)");
    }

    // ═══════════════════════════════════ §3 intake ═════════════════════════════

    /// <summary>Cable geometry goes through the reduction layer, never through raw
    /// Edit: ConductorGraph collapses series chains to equivalent resistors and keeps
    /// the maps that make limit events attributable back to individual segments.
    /// BulkBuild = ONE compaction pass at Dispose — the save-load path.
    /// Under ClientPartitioned every AddSegment MUST carry its partition.</summary>
    public void BulkLoadGrid()
    {
        using (var b = _graph.BeginBulkBuild(expectedSegments: 2 * BusLen))
            foreach (var n in new[] { NetB, NetC })
                for (var i = 0; i < BusLen; i++)
                {
                    // Network C's first segment is the fuse: 6 A overcurrent limit.
                    // Limits ride the ConductorSpec; the collapsed equivalent carries
                    // the min envelope and Attribute() maps a trip back to THIS segment.
                    var limits = n == NetC && i == 0
                        ? new LimitSpec(6.0, 0.0, 0.0, default)
                        : default;
                    b.AddSegment(Seg(n, i), J(n, i), J(n, i + 1),
                                 new ConductorSpec(0.05, 1.0, limits), Part(n));
                }
        Console.WriteLine($"== intake: 2 runs x {BusLen} segments bulk-built, islands={_net.Islands.Count}");
    }

    // ═══════════════════════════════════ §4 devices ════════════════════════════

    /// <summary>Two device populations, one contract (every readback must hit a live
    /// handle): built-in AdaptedLoads are only safe on islands that never rebuild
    /// (their handles are private — api.md §18), so they live on a raw quiet network;
    /// the churning cable networks get TutorialLoads, which re-resolve by key.</summary>
    public void AttachDevices()
    {
        // ── Quiet network A: raw nodes, one Edit, built-in devices via DeviceHost. ──
        using (var e = _net.Edit())
        {
            _quietRail = e.AddReferenceNode(QuietRailKey);       // the partition's return rail
            var feed = e.AddNode(QuietNodeKey(0), NodeRole.Internal, Part(NetA));
            e.AddVoltageSource(feed, _quietRail, MainsVolts, SourceKey(NetA));
            var prev = feed;
            for (var j = 1; j <= 2; j++)
            {
                var node = e.AddNode(QuietNodeKey(j), NodeRole.Internal, Part(NetA));
                e.AddResistor(prev, node, 0.5, QuietWireKey(j)); // feeder wire
                _quietTaps[j - 1] = node;
                prev = node;
            }
        }
        // Handles minted by the edit are valid immediately after Commit (§17 rule 1).
        for (var k = 0; k < 2; k++)
        {
            var load = new AdaptedLoad(advertisedWatts: 200.0, gMin: 1e-6, gMax: 1e3,
                brownoutLowVolts: 50.0, brownoutHighVolts: 70.0,
                lockoutCount: 100, staggerBaseTicks: 2, staggerSpreadTicks: 6);
            _host.Add(load, new[] { _quietTaps[k], _quietRail },
                      LoadKey(NetA, k), StateKey.From(LoadKey(NetA, k)));
            _quietLoads.Add(load);
        }

        // ── Active sources. SHARP EDGE: resolve graph nodes OUTSIDE the Edit, and
        //    resolve them ALL before using ANY — PortNode on a new junction protects
        //    it, which recompacts the run and can reissue nodes resolved a line ago.
        //    Two passes: the first settles the protections, the second reads stable
        //    handles. (The fake client in Manatee.Core.Tests does exactly this.) ──
        var feed2 = new NodeId[2];
        var rail2 = new NodeId[2];
        for (var pass = 0; pass < 2; pass++)
            for (var x = 0; x < 2; x++)
            {
                var n = x == 0 ? NetB : NetC;
                feed2[x] = _graph.PortNode(J(n, 0));
                rail2[x] = _graph.ReferenceNode(Part(n));
            }
        using (var e = _net.Edit())
        {
            e.AddVoltageSource(feed2[0], rail2[0], MainsVolts, SourceKey(NetB));
            e.AddVoltageSource(feed2[1], rail2[1], MainsVolts, SourceKey(NetC));
        }

        // ── Active loads: build, then FIRST-BOOT RE-PIN. Each Build's PortNode may
        //    recompact the run and stale the handles the EARLIER Builds cached. ──
        foreach (var n in new[] { NetB, NetC })
            for (var k = 0; k < TapAt.Length; k++)
            {
                var load = new TutorialLoad(Part(n), J(n, TapAt[k]), LoadKey(n, k), watts: 200.0);
                load.Build(_net, _graph);
                _activeLoads.Add(load);
            }
        RepinActive();

        // ── The breaker coupler between B's and C's feeds. Couplers are the ONLY
        //    sanctioned cross-partition join under ClientPartitioned; CouplerId is
        //    document-stable — cache it forever, it survives every rebuild (§16).
        //    J(*,0) is already protected (the sources attach there), so these
        //    resolves cannot recompact — a single pass is stable now. ──
        using (var e = _net.Edit())
            _breaker = e.AddCoupler(CouplerSpec.Breaker(),
                new CouplerPorts(_graph.PortNode(J(NetB, 0)), _graph.ReferenceNode(Part(NetB)),
                                 _graph.PortNode(J(NetC, 0)), _graph.ReferenceNode(Part(NetC))),
                BreakerKey, StateKey.From(BreakerKey));
        _net.Reconfigure(_breaker, CouplerState.Open);   // breakers are born Closed; start split

        Console.WriteLine($"== devices: {_quietLoads.Count} built-in AdaptedLoads (quiet), " +
                          $"{_activeLoads.Count} TutorialLoads (active), 1 breaker (Open)");
    }

    // ════════════════════════ §3/§9 first solve + cost queries ═════════════════

    public void FirstSolve()
    {
        _net.SolveOperatingPoint();      // energize: DC operating point for every island
        RepinActive();                   // construction rebuilds reissued handles — re-pin once more

        // Drain the construction backlog so the first game tick starts with a clean
        // change ring; otherwise tick 0 reports lost==true (a construction burst) and
        // forces a spurious — correct but noisy — full re-pin.
        while (_net.Islands.DrainChanges(_chgBuf, out _) > 0) { }

        Console.WriteLine($"== first solve: {_net.Islands.Count} islands");
        for (var i = 0; i < _net.Islands.Count; i++)
        {
            var isl = _net.Islands[i];
            Console.WriteLine($"   island {i}: status={isl.Status} partition={isl.Partition.Value} " +
                              $"stateUnits={isl.StateUnitCount}");
        }
        // Cost queries: ask what a mutation WOULD cost before paying it (§9).
        Console.WriteLine($"   CostOfReconfigure(breaker, Closed) = " +
                          $"{_net.CostOfReconfigure(_breaker, CouplerState.Closed)}");   // Topology
        var l0 = _activeLoads[0];
        Console.WriteLine($"   CostOfAdjust(load0, same ohms)     = " +
                          $"{_net.CostOfAdjust(l0.Resistor, l0.AppliedOhms)}");          // Metadata: ε-no-op
    }

    // ═══════════════════════════ §5 the canonical tick loop ════════════════════

    public void RunScriptedTicks(int ticks)
    {
        Console.WriteLine("== tick loop: topology -> re-pin -> drive-under-guard -> Solve -> re-pin -> readback");
        for (var t = 0; t < ticks; t++)
        {
            ScheduleEventsFor(t);
            Tick();
        }
    }

    /// <summary>After the scripted churn, run quiet ticks to show the steady-state
    /// contract: a converged fleet refactorizes zero times, rebuilds zero islands,
    /// and every per-load Adjust is an ε-no-op (§9's tier budget, executable).</summary>
    public void RunSteadyEpilogue(int ticks)
    {
        Console.WriteLine("== steady-state epilogue (no events)");
        for (var t = 0; t < ticks; t++) Tick();
    }

    /// <summary>One global power tick in the canonical §22.a order. The order is the
    /// contract — each phase exists because the previous one can invalidate what the
    /// next one reads. See docs/integration-tutorial.md §5 for the why of each step.</summary>
    private void Tick()
    {
        var clock = new TickClock(_tick++, Dt);
        _notes.Clear();

        // ── 1. Topology phase: apply the game's queued world changes FIRST, before
        //    the guard. Breaker Reconfigures are safe through the cached CouplerId
        //    even if a sibling cut rebuilt its island last tick (document-stable). ──
        var hadTopology = _pending.Count > 0;
        while (_pending.Count > 0)
        {
            var (label, op) = _pending.Dequeue();
            _notes.Add(label);
            op();
        }

        // ── 2. Re-pin phase: drain island membership changes ONCE per global tick.
        //    lost==true means the ring overflowed — the obligation escalates to a
        //    full re-pin of every held handle (api.md §11). We also re-pin when we
        //    just mutated topology ourselves: an eager recompaction commits before
        //    this tick's Solve and its DrainChanges may arrive only afterwards. ──
        var changes = _net.Islands.DrainChanges(_chgBuf, out var lost);
        var repin = lost || changes > 0 || hadTopology;
        if (lost) _notes.Add("change ring OVERFLOWED -> full re-pin");
        if (repin) RepinActive();

        // ── 3. Drive phase, under the steady-state guard: tier-3 is now impossible
        //    (Edit() throws; Reconfigure is deferred-and-counted in release, asserted
        //    in debug) and the allocation tripwire is armed where the runtime
        //    supports it. Devices may only Drive/Adjust — enforced by type. ──
        using (_net.EnterSteadyState())
        {
            _host.Tick(Dt);                                     // built-in AdaptedLoads
            var ctx = _net.TickContext(Dt);
            for (var i = 0; i < _activeLoads.Count; i++) _activeLoads[i].Tick(in ctx);
        }

        // ── 4. ONE Solve for every dirty island. Island rebuilds run INSIDE Solve
        //    and reissue member handles — hence the post-Solve re-pin below. ──
        _net.Solve(clock);

        // ── 4b. CHURN-TICK RE-PIN (the most-forgotten step, api.md §22.a): on any
        //    tick that touched topology, the handles re-pinned in phase 2 were JUST
        //    reissued by this Solve's rebuild. Re-resolve again before any readback,
        //    or ApplyState dereferences exactly the handles this tick invalidated
        //    (a counted no-op: StaleHandleReads > 0, silently wrong power values). ──
        if (repin) RepinActive();

        // ── 5. Readback phase: apply actuals per partition, drain limits, telemetry. ──
        ApplyStateAndPostTick();
        PrintTick(clock.TickIndex);
    }

    /// <summary>Re-resolve every active-load handle by key. Sources are the lazy
    /// alternative: we never cache their handles at all — DriveSource resolves by
    /// key at each use, trading a hash lookup for zero re-pin bookkeeping.</summary>
    private void RepinActive()
    {
        for (var i = 0; i < _activeLoads.Count; i++) _activeLoads[i].Repin(_net, _graph);
    }

    private void ApplyStateAndPostTick()
    {
        // In Re-Volt this is ApplyState per CableNetwork: each network hands ONLY its
        // own partition's devices their actual power from the published Solution.
        var sol = _net.Solution;
        _ = sol;   // (readbacks below go through helpers; kept explicit for the shape)

        // Limit events: drained per island, post-solve. The solver NEVER mutates the
        // circuit on a limit (R7) — popping the fuse is OUR RemoveSegment, and
        // Attribute maps the collapsed equivalent's event back to the culprit segment.
        for (var i = 0; i < _net.Islands.Count; i++)
        {
            var isl = _net.Islands[i];
            if (isl.Status == IslandStatus.Faulted)
            {
                _notes.Add($"island FAULTED ({isl.Fault.Kind}) -> reads de-energized; run scalar fallback");
                continue;
            }
            var got = isl.DrainLimitEvents(_limBuf);
            for (var k = 0; k < got; k++)
            {
                if (_limBuf[k].Kind != LimitKind.OverCurrent) continue;
                if (!_graph.Attribute(in _limBuf[k], out var hit)) continue;
                _graph.RemoveSegment(hit.Segment);   // the fuse melts; rebuild coalesces to next Solve
                _melts++;
                _notes.Add($"OverCurrent {_limBuf[k].Observed:F1}A > {_limBuf[k].Threshold:F1}A " +
                           $"-> melted segment {hit.Segment.Lo}");
            }
        }

        ref readonly var s = ref _net.LastTickStats;
        _lastStats = s;
        _totalStale += s.StaleHandleReads;           // the standing contract: 0 on EVERY tick
    }

    // ═══════════════════════════════ scripted events ═══════════════════════════

    private void ScheduleEventsFor(int t)
    {
        switch (t)
        {
            case 8:
                _pending.Enqueue(("snapshot islands B and C (pre-merge)", SnapshotBAndC));
                break;
            case 10:
                // Reconfigure(Closed) = incremental merge: component handles SURVIVE,
                // only the absorbed IslandId dies. The merged island spans two
                // partitions, so its Partition reads the None sentinel.
                // Expect a ONE-TICK 0 W dip on the ABSORBED side only (here C; B, the
                // survivor, keeps its last-good published vector and never dips): until
                // the merged island first publishes, the absorbed island's nodes read
                // 0.0 (not last-good) through Solution / Previous, so C's loads shed
                // for one tick. Which side survives is an implementation detail —
                // defend both (sharp-edges appendix, edge 4).
                _pending.Enqueue(("breaker CLOSE -> B+C merge (one-tick readback dip)",
                    () => _net.Reconfigure(_breaker, CouplerState.Closed)));
                break;
            case 14:
                _pending.Enqueue(("additive restore of both pre-merge blobs into the merged island",
                    RestoreIntoMerged));
                break;
            case 20:
                // Cutting a far-end segment (past every tap) rebuilds B's run without
                // disconnecting anything — pure handle churn, which is the point.
                _pending.Enqueue(("cable cut: B segment 10 -> island rebuild + re-pin",
                    () => { _graph.RemoveSegment(Seg(NetB, 10)); _cuts++; }));
                break;
            case 24:
                _pending.Enqueue(("drive A source to 20 V -> AdaptedLoads brown out",
                    () => DriveSource(NetA, 20.0)));
                break;
            case 30:
                // Reconfigure(Open) = split: a FULL rebuild of both resulting islands;
                // every member component/node handle is reissued at Solve.
                _pending.Enqueue(("breaker OPEN -> split rebuild",
                    () => _net.Reconfigure(_breaker, CouplerState.Open)));
                break;
            case 32:
                _pending.Enqueue(("drive A source back to 120 V -> staggered rejoin",
                    () => DriveSource(NetA, MainsVolts)));
                break;
            case 38:
                // Constant-power loads at low voltage pull MORE current (G = P/V²):
                // the fuse segment's 6 A limit trips within a tick or two.
                _pending.Enqueue(("drive C source to 30 V -> overcurrent, fuse will melt",
                    () => DriveSource(NetC, 30.0)));
                break;
            case 44:
                _pending.Enqueue(("drive C source back to 120 V (loads stay isolated behind the melt)",
                    () => DriveSource(NetC, MainsVolts)));
                break;
        }
    }

    /// <summary>Drive = tier 1, legal in any phase outside Step. Resolving by key at
    /// each use (instead of caching the VSourceId) sidesteps re-pinning entirely.</summary>
    private void DriveSource(int n, double volts)
    {
        if (_net.TryResolve(SourceKey(n), out var c))
            _net.Drive(new VSourceId(c.Slot, c.Gen, c.Net), volts);
    }

    private void SnapshotBAndC()
    {
        _blobB = SnapshotIsland(NetB);
        _blobC = SnapshotIsland(NetC);
        _notes.Add($"blobs: B={_blobB.Length}B C={_blobC.Length}B; " +
                   $"load0 TicksSeen={_activeLoads[0].TicksSeen}");
    }

    private byte[] SnapshotIsland(int n)
    {
        var w = new ArrayBufferWriter<byte>();
        _net.Islands.Of(_graph.PortNode(J(n, 0))).Snapshot(w);
        return w.WrittenSpan.ToArray();
    }

    /// <summary>The merged-since-save pattern (api.md §14): offer EACH pre-merge blob
    /// to the one merged island. Restore is additive — each blob overwrites exactly
    /// the units whose StateKey it carries and leaves the rest untouched, so the
    /// second restore cannot clobber the first. Coverage is checked in AGGREGATE:
    /// sum(Matched) vs StateUnitCount, after all blobs are offered.</summary>
    private void RestoreIntoMerged()
    {
        var isl = _net.Islands.Of(_graph.PortNode(J(NetB, 0)));   // == C's island while merged
        var rB = isl.Restore(_blobB);
        var rC = isl.Restore(_blobC);
        _restoreMatched = rB.Matched + rC.Matched;
        _restoreOrphans = rB.OrphansInBlob + rC.OrphansInBlob;
        var coldStarted = isl.StateUnitCount - _restoreMatched;
        _notes.Add($"restore: matched B={rB.Matched} C={rC.Matched}, orphans={_restoreOrphans}, " +
                   $"units={isl.StateUnitCount}, cold-started={coldStarted}; " +
                   $"load0 TicksSeen rewound to {_activeLoads[0].TicksSeen}");
    }

    // ═══════════════════════════════ readbacks + print ═════════════════════════

    private double QuietPower()
    {
        var sol = _net.Solution;
        var sum = 0.0;
        foreach (var l in _quietLoads) sum += sol.Power(l.ConductanceResistor);
        return sum;
    }

    private double ActivePower(int n)
    {
        var sol = _net.Solution;
        var sum = 0.0;
        foreach (var l in _activeLoads)
            if (l.Partition == Part(n)) sum += l.Power(in sol);
        return sum;
    }

    private void PrintTick(long t)
    {
        ref readonly var s = ref _net.LastTickStats;
        var line = $"t={t,2} isl={_net.Islands.Count} reb={s.IslandRebuilds} refac={s.Refactorizations} " +
                   $"rhs={s.RhsSolves} noop={s.AdjustNoOps} stale={s.StaleHandleReads} | " +
                   $"A={QuietPower(),4:F0}W m={_quietLoads[0].Mode}{_quietLoads[1].Mode} " +
                   $"B={ActivePower(NetB),4:F0}W C={ActivePower(NetC),4:F0}W";
        Console.WriteLine(_notes.Count == 0 ? line : line + "  << " + string.Join("; ", _notes));
    }

    // ═══════════════════════════════ final contract ════════════════════════════

    /// <summary>The telemetry contract, checked the way a real client's CI would.</summary>
    public int VerifyTelemetry()
    {
        var ok = true;
        ok &= Check(_totalStale == 0, $"StaleHandleReads total == 0 (got {_totalStale})");
        ok &= Check(_cuts == 1, "the scripted cable cut ran");
        ok &= Check(_melts == 1, $"the fuse melted exactly once (got {_melts})");
        ok &= Check(_restoreMatched == 4 && _restoreOrphans == 0,
            $"additive restore matched all 4 load units, 0 orphans (got {_restoreMatched}/{_restoreOrphans})");
        ok &= Check(_lastStats.Refactorizations == 0 && _lastStats.IslandRebuilds == 0,
            "steady-state tail lives in tier <=1 (0 refactorizations, 0 rebuilds)");
        Console.WriteLine(ok ? "== ALL CHECKS PASSED" : "== CHECKS FAILED");
        return ok ? 0 : 1;
    }

    private static bool Check(bool cond, string what)
    {
        Console.WriteLine($"   [{(cond ? "ok" : "FAIL")}] {what}");
        return cond;
    }
}
