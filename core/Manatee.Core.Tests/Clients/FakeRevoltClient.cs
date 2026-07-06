using System;
using System.Collections.Generic;
using Manatee.Core;
using Manatee.Core.Devices;
using Manatee.Core.Reduction;

namespace Manatee.Core.Tests.Clients;

/// <summary>
/// An executable, assertion-laden stand-in for the Stationeers Re-Volt client
/// (api.md §22.a). One ClientPartitioned Netlist spans a 40-network base and runs the
/// ONE-global-tick-body execution model (topology → re-pin → drive-under-guard → solve →
/// per-partition ApplyState → post), scripting the demanding cases a lean solver could
/// paint itself out of: breaker trips, cable cuts, device add/remove, a save-blob restore
/// across a merged boundary, a forced DrainChanges ring overflow (lost ⇒ full re-pin), and
/// a Faulted-island scalar fallback.
///
/// <para><b>Two load populations, one contract.</b> The ~80 built-in
/// <see cref="AdaptedLoad"/> devices live on 34 QUIET networks built on raw nodes in a
/// single pre-solve edit — the shape their own tests exercise — so their private handles
/// survive the initial build and are never invalidated (StaleHandleReads stays 0). The 6
/// ACTIVE networks are the ones that suffer topology churn: their cable runs are compacted
/// through the reduction layer (thousands of BulkBuild segments) and their loads are
/// <see cref="RevoltAdaptor"/>s re-pinned by KEY (api.md §16) — a re-resolution that
/// triggers no rebuild and so never churns — every time a cut or breaker rebuilds the
/// island. Both populations must read a live handle on every tick.</para>
/// </summary>
internal sealed class FakeRevoltClient
{
    private const double Dt = 0.5;
    private const double SourceVolts = 120.0;
    public const int NetworkCount = 40;
    private const int FirstActive = 34;                 // networks 34..39 are active (industrial)
    private const int ActiveBusLen = 400;               // long compacted cable runs
    private const int ActiveTapStride = 90;             // ⇒ ~4 adaptors per active network

    public Core.Netlist Net { get; }
    public ConductorGraph Graph { get; }
    public DeviceHost QuietHost { get; }

    // Per-partition device populations (the ApplyState guard applies ONLY these).
    private readonly Dictionary<int, List<AdaptedLoad>> _quietLoads = new();
    private readonly Dictionary<int, List<RevoltAdaptor>> _activeLoads = new();
    private readonly List<RevoltAdaptor> _allActive = new();

    private readonly Dictionary<int, ExternalKey> _sourceKey = new();
    private readonly Dictionary<int, VSourceId> _source = new();     // re-pinned by key
    private readonly Dictionary<int, CouplerId> _breakers = new();   // keyed by lower active-network index

    // Scratch (shape-time only).
    private readonly IslandChange[] _chgBuf = new IslandChange[128];
    private readonly LimitEvent[] _limBuf = new LimitEvent[16];

    private long _tick;
    private int _overflowCounter = 900_000_000;
    private readonly Queue<Action> _pending = new();

    /// <summary>The §22.a churn-tick discipline (documented in api.md §22.a): on a
    /// churn tick the island rebuild happens INSIDE Solve (api.md §16/§17) and
    /// reissues every member handle, so the client must re-pin AFTER Solve too —
    /// before the ApplyState readback — not only in the pre-drive phase. Tests set
    /// this false to prove the step is load-bearing: a churn tick without it reads
    /// this tick's freshly-reissued handles and the StaleHandleReads tripwire counts
    /// them (the counter is a live signal, not a manufactured zero).</summary>
    public bool PostSolveRepin { get; set; } = true;

    // ── Telemetry ──
    public long TotalStaleReads { get; private set; }
    public int FullRepins { get; private set; }
    public int Cuts { get; private set; }
    public int BreakerOps { get; private set; }
    public int Faults { get; private set; }
    public int ScalarFallbacks { get; private set; }
    public int Melts { get; private set; }
    public int LastRefactorizations { get; private set; }
    public int LastRebuilds { get; private set; }
    public bool LastDrainLost { get; private set; }
    public bool LastApplyDisjoint { get; private set; }
    public int LastApplyCount { get; private set; }

    public FakeRevoltClient()
    {
        Net = new Core.Netlist(NetlistOptions.Stationeers(Dt));
        Graph = new ConductorGraph(Net, GraphOptions.PrePartitioned);
        QuietHost = new DeviceHost(Net);
        BuildBase();
    }

    // ── key helpers (disjoint ranges) ──
    private static PartitionKey Part(int n) => new((ulong)(n + 1));
    private static JunctionKey J(int n, int i) => new((ulong)(n * 1_000_000 + i));
    private static SegmentKey Seg(int n, int i) => new((ulong)(n * 1_000_000 + i));
    private static ExternalKey SourceKeyOf(int n) => new(0x5000_0000_0000_0000UL, (ulong)n);
    private static ExternalKey QuietNodeKey(int n, int j) => new(0x2000_0000_0000_0000UL, (ulong)(n * 1000 + j));
    private static ExternalKey QuietRailKey(int n) => new(0x2500_0000_0000_0000UL, (ulong)n);
    private static ExternalKey QuietLoadKey(int n, int k) => new(0xA000_0000_0000_0000UL + (ulong)n, (ulong)k);
    private static ExternalKey ActiveLoadKey(int n, int k) => new(0xC000_0000_0000_0000UL + (ulong)n, (ulong)k);
    private static ExternalKey BreakerKeyOf(int n) => new(0xB000_0000_0000_0000UL, (ulong)n);
    // A dedicated raw fault island (two contradictory ideal sources ⇒ ContradictorySources).
    private static readonly PartitionKey FaultPartition = new(0x00FF_0000UL);
    private static ExternalKey FaultRefKey => new(0xF000_0000_0000_0000UL, 1);
    private static ExternalKey FaultNodeKey => new(0xF000_0000_0000_0000UL, 2);
    private static ExternalKey FaultKey => new(0xF000_0000_0000_0000UL, 3);
    private static ExternalKey FaultKey2 => new(0xF000_0000_0000_0000UL, 4);

    private static bool IsActive(int n) => n >= FirstActive;

    private void BuildBase()
    {
        // ── 34 QUIET raw networks: ~80 built-in AdaptedLoads on stable raw nodes ──
        var placed = 0;
        for (var n = 0; n < FirstActive; n++)
        {
            var loadsHere = n < 12 ? 3 : 2;   // 12×3 + 22×2 = 80 AdaptedLoads
            var taps = new NodeId[loadsHere];
            NodeId rail;
            using (var e = Net.Edit())
            {
                rail = e.AddReferenceNode(QuietRailKey(n));
                var prev = e.AddNode(QuietNodeKey(n, 0), NodeRole.Internal, Part(n));
                _sourceKey[n] = SourceKeyOf(n);
                _source[n] = e.AddVoltageSource(prev, rail, SourceVolts, SourceKeyOf(n));
                for (var j = 1; j <= loadsHere; j++)
                {
                    var node = e.AddNode(QuietNodeKey(n, j), NodeRole.Internal, Part(n));
                    e.AddResistor(prev, node, 0.5, new ExternalKey(0x2600_0000_0000_0000UL, (ulong)(n * 1000 + j)));
                    taps[j - 1] = node;
                    prev = node;
                }
            }
            for (var k = 0; k < loadsHere; k++, placed++)
            {
                var load = new AdaptedLoad(advertisedWatts: 200.0, gMin: 1e-6, gMax: 1e3,
                    brownoutLowVolts: 50.0, brownoutHighVolts: 70.0,
                    lockoutCount: 100, staggerBaseTicks: 2, staggerSpreadTicks: 6);
                QuietHost.Add(load, new NodeId[2] { taps[k], rail }, QuietLoadKey(n, k), StateKey.From(QuietLoadKey(n, k)));
                _quietLoads.GetOrAdd(n).Add(load);
            }
        }
        AdaptedLoadCount = placed;

        // ── 6 ACTIVE graph networks: thousands of BulkBuild segments, re-pinnable adaptors ──
        using (var b = Graph.BeginBulkBuild((NetworkCount - FirstActive) * ActiveBusLen))
        {
            for (var n = FirstActive; n < NetworkCount; n++)
                for (var i = 0; i < ActiveBusLen; i++)
                    b.AddSegment(Seg(n, i), J(n, i), J(n, i + 1), new ConductorSpec(0.05, 1.0), Part(n));
        }

        // Active sources (graph feed → rail). Resolve all graph nodes OUTSIDE the edit.
        var feeds = new NodeId[NetworkCount];
        var rails = new NodeId[NetworkCount];
        for (var n = FirstActive; n < NetworkCount; n++) { feeds[n] = Graph.PortNode(J(n, 0)); rails[n] = Graph.ReferenceNode(Part(n)); }
        // Re-resolve after all protections settled (each PortNode may recompact).
        for (var n = FirstActive; n < NetworkCount; n++) { feeds[n] = Graph.PortNode(J(n, 0)); rails[n] = Graph.ReferenceNode(Part(n)); }
        using (var e = Net.Edit())
            for (var n = FirstActive; n < NetworkCount; n++)
            {
                _sourceKey[n] = SourceKeyOf(n);
                _source[n] = e.AddVoltageSource(feeds[n], rails[n], SourceVolts, SourceKeyOf(n));
            }

        // Active adaptors at taps (graph-attached ⇒ re-pinned by key in the tick loop).
        for (var n = FirstActive; n < NetworkCount; n++)
        {
            var k = 0;
            for (var i = ActiveTapStride; i < ActiveBusLen; i += ActiveTapStride, k++)
            {
                var a = new RevoltAdaptor(Part(n), J(n, i), ActiveLoadKey(n, k), watts: 200.0);
                a.Build(Net, Graph);
                _activeLoads.GetOrAdd(n).Add(a);
                _allActive.Add(a);
            }
        }

        // Breakers between consecutive active networks (resolve ports outside the edit).
        for (var n = FirstActive; n < NetworkCount - 1; n++) { feeds[n] = Graph.PortNode(J(n, 0)); rails[n] = Graph.ReferenceNode(Part(n)); feeds[n + 1] = Graph.PortNode(J(n + 1, 0)); rails[n + 1] = Graph.ReferenceNode(Part(n + 1)); }
        using (var e = Net.Edit())
            for (var n = FirstActive; n < NetworkCount - 1; n++)
                _breakers[n] = e.AddCoupler(CouplerSpec.Breaker(),
                    new CouplerPorts(feeds[n], rails[n], feeds[n + 1], rails[n + 1]),
                    BreakerKeyOf(n), StateKey.From(BreakerKeyOf(n)));
        // Breakers default CLOSED — open them so each active network starts as its own island.
        foreach (var br in _breakers.Values) Net.Reconfigure(br, CouplerState.Open);

        Net.SolveOperatingPoint();
        // The active graph handles were reissued by the build; refresh them now (the quiet raw
        // handles are already stable). This is the §22.a "restore-by-key" first-boot re-pin.
        RepinSources();
        RepinActive();
        // Consume the construction's island-change backlog so the first game tick starts with a
        // clean change ring (otherwise the first DrainChanges laps and forces a spurious full
        // re-pin — correct behaviour, but it would muddy the scripted overflow assertion).
        while (Net.Islands.DrainChanges(_chgBuf, out _) > 0) { }
    }

    // ── receipts ──
    public int AdaptedLoadCount { get; private set; }
    public int ActiveLoadCount => _allActive.Count;
    public int LiveNodeCount => Graph.LiveNodeCount;
    public int IslandCount => Net.Islands.Count;

    // ================================================================ scripting

    public void QueueBreaker(int lowerActiveNet, CouplerState state)
        => _pending.Enqueue(() => { if (_breakers.TryGetValue(lowerActiveNet, out var br)) { Net.Reconfigure(br, state); BreakerOps++; } });

    public void QueueCut(int net, int segIndex)
        => _pending.Enqueue(() => { Graph.RemoveSegment(Seg(net, segIndex)); Cuts++; });

    public RevoltAdaptor QueueAddAdaptor(int activeNet, int tapIndex, int slot, double watts)
    {
        var a = new RevoltAdaptor(Part(activeNet), J(activeNet, tapIndex), ActiveLoadKey(activeNet, 100 + slot), watts);
        _pending.Enqueue(() => { a.Build(Net, Graph); _activeLoads.GetOrAdd(activeNet).Add(a); _allActive.Add(a); });
        return a;
    }

    public void QueueRemoveAdaptor(RevoltAdaptor a)
        => _pending.Enqueue(() => { a.Retire(Net); _activeLoads.GetOrAdd(ActiveNetOf(a)).Remove(a); _allActive.Remove(a); });

    public void QueueInduceFault()
        => _pending.Enqueue(() =>
        {
            // A fresh raw island with two contradictory ideal sources on one node pair ⇒
            // a diagnosable ContradictorySources singularity (api.md §20). Building it as its
            // OWN island keeps the fault from rebuilding — and staling — any device island.
            using var e = Net.Edit();
            var fref = e.AddReferenceNode(FaultRefKey);
            var fa = e.AddNode(FaultNodeKey, NodeRole.Internal, FaultPartition);
            e.AddVoltageSource(fa, fref, 10.0, FaultKey);
            e.AddVoltageSource(fa, fref, 5.0, FaultKey2);
        });

    public void QueueClearFault()
        => _pending.Enqueue(() =>
        {
            using var e = Net.Edit();
            if (Net.TryResolve(FaultKey, out var c1)) e.Remove(new VSourceId(c1.Slot, c1.Gen, c1.Net));
            if (Net.TryResolve(FaultKey2, out var c2)) e.Remove(new VSourceId(c2.Slot, c2.Gen, c2.Net));
        });

    public IslandStatus FaultIslandStatus()
    {
        if (!Net.TryResolveNode(FaultNodeKey, out var node)) return IslandStatus.Empty;
        return Net.Islands.Of(node).Status;
    }

    public void QueueOverflow(int islands = 320)
        => _pending.Enqueue(() =>
        {
            using var b = Graph.BeginBulkBuild(islands);
            for (var i = 0; i < islands; i++)
                b.AddSegment(new SegmentKey((ulong)(_overflowCounter + i)),
                             new JunctionKey((ulong)(7_000_000_000UL + (ulong)i * 2)),
                             new JunctionKey((ulong)(7_000_000_000UL + (ulong)i * 2 + 1)),
                             new ConductorSpec(1.0, 1.0), new PartitionKey((ulong)(_overflowCounter + i)));
            _overflowCounter += islands * 2;
        });

    private int ActiveNetOf(RevoltAdaptor a)
    {
        foreach (var kv in _activeLoads) if (kv.Value.Contains(a)) return kv.Key;
        return FirstActive;
    }

    // ================================================================ the tick

    /// <summary>One global Re-Volt power tick in the canonical §22.a order.</summary>
    public void Tick()
    {
        var clock = new TickClock(_tick++, Dt);

        // 1. Topology phase — before the guard.
        var hadTopology = _pending.Count > 0;
        while (_pending.Count > 0) _pending.Dequeue()();

        // 2. Re-pin phase — drained ONCE per global tick; a lost ring obliges a full re-pin.
        // A topology op applied THIS tick reissues the touched handles at its recompaction
        // commit (before this tick's Solve), so re-pin whenever the drain reports changes OR
        // we just mutated topology — otherwise the drive phase would read the freshly-stale
        // handles (their DrainChanges arrives only at the coming Solve).
        var n = Net.Islands.DrainChanges(_chgBuf, out var lost);
        LastDrainLost = lost;
        var repin = lost || n > 0 || hadTopology;
        if (lost) { FullRepinAll(); FullRepins++; }
        else if (repin) { RepinSources(); RepinActive(); }

        // 3. Drive phase under the steady-state guard (tier-3 barred; alloc tripwire armed).
        using (Net.EnterSteadyState())
        {
            QuietHost.Tick(Dt);                                    // the ~80 AdaptedLoads (stable handles)
            var ctx = Net.TickContext(Dt);
            for (var i = 0; i < _allActive.Count; i++) _allActive[i].Tick(in ctx);
        }

        // 4. ONE solve for every dirty island.
        Net.Solve(clock);

        // The Solve reissued the handles of every island it rebuilt this tick (the
        // rebuild runs INSIDE Solve, §16/§17); refresh the active adaptors again so the
        // ApplyState readback below reads live handles (the quiet AdaptedLoads never
        // rebuild, so their handles stay valid). This post-Solve re-pin is PART OF the
        // §22.a contract (api.md §22.a churn-tick note), not an extra belt-and-braces
        // step: without it a churn tick's ApplyState reads exactly the handles this
        // tick's Solve invalidated — proven live by the PostSolveRepin=false test.
        if (repin && PostSolveRepin) { RepinSources(); RepinActive(); }

        // 5. Per-partition ApplyState (the vanilla single-writer guard).
        ApplyStatePerPartition();

        // 6. Post-tick — faulted fallback, limit attribution, telemetry.
        PostTick();
    }

    private void FullRepinAll()
    {
        RepinSources();
        RepinActive();
    }

    private void RepinActive()
    {
        for (var i = 0; i < _allActive.Count; i++) _allActive[i].Repin(Net, Graph);
    }

    private void RepinSources()
    {
        foreach (var kv in _sourceKey)
            if (Net.TryResolve(kv.Value, out var c))
                _source[kv.Key] = new VSourceId(c.Slot, c.Gen, c.Net);
    }

    private readonly HashSet<int> _globallyApplied = new();

    private void ApplyStatePerPartition()
    {
        _globallyApplied.Clear();
        var ok = true; var total = 0;
        var sol = Net.Solution;
        for (var n = 0; n < NetworkCount; n++)
        {
            if (_quietLoads.TryGetValue(n, out var ql))
                foreach (var l in ql)
                {
                    _ = sol.Power(l.ConductanceResistor);
                    if (!_globallyApplied.Add(System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(l))) ok = false;
                    total++;
                }
            if (_activeLoads.TryGetValue(n, out var al))
                foreach (var a in al)
                {
                    if (a.Retired) continue;
                    _ = a.Power(in sol);
                    if (!_globallyApplied.Add(System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(a))) ok = false;
                    total++;
                }
        }
        LastApplyDisjoint = ok;
        LastApplyCount = total;
    }

    private void PostTick()
    {
        var count = Net.Islands.Count;
        var faultedThisTick = 0;
        for (var idx = 0; idx < count; idx++)
        {
            var isl = Net.Islands[idx];
            if (isl.Status == IslandStatus.Faulted) { ScalarFallbacks++; faultedThisTick++; continue; }
            var got = isl.DrainLimitEvents(_limBuf);
            for (var k = 0; k < got; k++)
                if (_limBuf[k].Kind == LimitKind.OverCurrent && Graph.Attribute(in _limBuf[k], out var attr))
                { Graph.RemoveSegment(attr.Segment); Melts++; }
        }
        if (faultedThisTick > Faults) Faults = faultedThisTick;

        ref readonly var s = ref Net.LastTickStats;
        LastRefactorizations = s.Refactorizations;
        LastRebuilds = s.IslandRebuilds;
        TotalStaleReads += s.StaleHandleReads;
    }

    // ── readbacks the tests use ──
    public void DriveSource(int net, double volts)
    {
        if (Net.TryResolve(_sourceKey[net], out var c))
            Net.Drive(new VSourceId(c.Slot, c.Gen, c.Net), volts);
    }

    public bool QuietNetworkIsLive(int net)
    {
        Net.TryResolveNode(QuietNodeKey(net, 0), out var node);
        return Net.Solution.IsLive(Net.IslandOf(node));
    }

    public bool ActiveNetworkIsLive(int net) => Net.Solution.IsLive(Net.IslandOf(Graph.PortNode(J(net, 0))));

    public IslandStatus ActiveNetworkStatus(int net) => Net.Islands.Of(Graph.PortNode(J(net, 0))).Status;

    public double AdaptedLoadPower(int net, int index) => Net.Solution.Power(_quietLoads[net][index].ConductanceResistor);
    public int AdaptedLoadMode(int net, int index) => _quietLoads[net][index].Mode;

    public IslandHandle ActiveIsland(int net) => Net.Islands.Of(Graph.PortNode(J(net, 0)));

    public IslandHandle QuietIsland(int net)
    {
        Net.TryResolveNode(QuietNodeKey(net, 0), out var node);
        return Net.Islands.Of(node);
    }

    public byte[] SnapshotActive(int net)
    {
        var w = new System.Buffers.ArrayBufferWriter<byte>();
        Net.Islands.Of(Graph.PortNode(J(net, 0))).Snapshot(w);
        return w.WrittenSpan.ToArray();
    }

    public RestoreResult RestoreActive(int net, byte[] blob) => Net.Islands.Of(Graph.PortNode(J(net, 0))).Restore(blob);

    public IReadOnlyList<RevoltAdaptor> ActiveLoadsOf(int net)
        => _activeLoads.TryGetValue(net, out var l) ? l : Array.Empty<RevoltAdaptor>();
}

internal static class DictListExtensions
{
    public static List<TV> GetOrAdd<TK, TV>(this Dictionary<TK, List<TV>> d, TK k) where TK : notnull
    {
        if (!d.TryGetValue(k, out var l)) { l = new List<TV>(); d[k] = l; }
        return l;
    }
}
