using System;
using System.Buffers;
using BenchmarkDotNet.Attributes;
using Manatee.Core;
using Manatee.Core.Reduction;

namespace Manatee.Benchmarks;

/// <summary>
/// LadderDcTick — the tier-1 steady path (testing-strategy.md Benchmarks; api.md §9).
/// After warmup a Drive-then-Solve tick must be a frozen-pivot RHS solve: zero
/// refactorizations, zero bytes. MemoryDiagnoser is the human-facing measurement; the
/// CI-shaped 0 B gate is the [Fact] in Tests/Clients/TierBudgetGateTests.
/// </summary>
[MemoryDiagnoser]
public class LadderDcTickBenchmarks
{
    [Params(10, 100, 1000)] public int N;

    private Core.Netlist _net = null!;
    private VSourceId _src;
    private long _tick;

    [GlobalSetup]
    public void Setup()
    {
        (_net, _src) = BenchFixtures.BuildLadder(N, 0.5);
        // Warm the frozen-pivot path: two solves so the symbolic factor is cached.
        _net.Drive(_src, 12.0); _net.Solve(new TickClock(_tick++, 0.5));
        _net.Drive(_src, 12.5); _net.Solve(new TickClock(_tick++, 0.5));
    }

    [Benchmark]
    public double Tick()
    {
        _net.Drive(_src, 12.0 + (_tick & 1));
        _net.Solve(new TickClock(_tick++, 0.5));
        return _net.Solution.Current(_src);   // cheap touch to defeat dead-code elimination
    }
}

/// <summary>
/// SwitchToggleRefactor — the tier-2 path (testing-strategy.md Benchmarks). Toggling a
/// switch changes the active conductance set, forcing exactly one refactorization per
/// toggle. The number to watch is the delta over a pure tier-1 tick.
/// </summary>
[MemoryDiagnoser]
public class SwitchToggleRefactorBenchmarks
{
    private Core.Netlist _net = null!;
    private SwitchId _sw;
    private long _tick;
    private bool _closed = true;

    [GlobalSetup]
    public void Setup()
    {
        (_net, _sw) = BenchFixtures.BuildSwitchLoop(0.5);
        _net.Solve(new TickClock(_tick++, 0.5));
    }

    [Benchmark]
    public void Toggle()
    {
        _closed = !_closed;
        _net.Adjust(_sw, _closed);
        _net.Solve(new TickClock(_tick++, 0.5));
    }
}

/// <summary>
/// BulkBuild10k — the tier-3 load-time path plus the reduction layer (testing-strategy.md;
/// compaction.md). A 10k-segment chain with periodic branch stubs compacts to a few
/// hundred nodes in ONE pass; this measures that whole build + compaction, the
/// Stationeers save-load case.
/// </summary>
[MemoryDiagnoser]
public class BulkBuild10kBenchmarks
{
    [Params(10_000)] public int Segments;

    [Benchmark]
    public int BuildAndCompact()
    {
        var net = new Core.Netlist(BenchFixtures.Dc(0.5));
        var g = new ConductorGraph(net, GraphOptions.SelfPartitioned);
        ulong branchJ = 5_000_000, stub = 20_000_000;
        using (var b = g.BeginBulkBuild(Segments))
        {
            for (var i = 1; i <= Segments; i++)
                b.AddSegment(new SegmentKey((ulong)i), new JunctionKey((ulong)i), new JunctionKey((ulong)(i + 1)),
                             new ConductorSpec(0.1, 1));
            for (var i = 100; i < Segments; i += 100)
                b.AddSegment(new SegmentKey(stub++), new JunctionKey((ulong)i), new JunctionKey(branchJ++),
                             new ConductorSpec(0.1, 1));
        }
        return net.Islands.Count;
    }
}

/// <summary>
/// AcIslandTick — the subcycled AC per-tick cost (testing-strategy.md Benchmarks;
/// api.md §22.b). At 5 Hz with dt = 0.05 s the island subcycles N ≈ 5 substeps; each
/// substep advances the sine phase and back-substitutes on a frozen pivot.
/// </summary>
[MemoryDiagnoser]
public class AcIslandTickBenchmarks
{
    [Params(10, 100)] public int N;

    private Core.Netlist _net = null!;
    private long _tick;

    [GlobalSetup]
    public void Setup()
    {
        (_net, _) = BenchFixtures.BuildAcLadder(N, 0.05, 5.0);
        for (var i = 0; i < 3; i++) _net.Solve(new TickClock(_tick++, 0.05));   // warm the subcycle
    }

    [Benchmark]
    public void Tick() => _net.Solve(new TickClock(_tick++, 0.05));
}

/// <summary>
/// SnapshotRoundTrip — the per-island save/restore path (testing-strategy.md;
/// api.md §14). Snapshot an RLC island to a reusable buffer and restore it; the
/// round-trip is bit-for-bit (law 4) and its cost bounds the save-load hitch.
/// </summary>
[MemoryDiagnoser]
public class SnapshotRoundTripBenchmarks
{
    private Core.Netlist _net = null!;
    private IslandId _island;
    private readonly ArrayBufferWriter<byte> _buf = new();
    private byte[] _blob = Array.Empty<byte>();

    [GlobalSetup]
    public void Setup()
    {
        _net = new Core.Netlist(BenchFixtures.Mixed(0.05));
        NodeId a;
        using (var e = _net.Edit())
        {
            a = e.AddNode(BenchFixtures.K(1));
            var x = e.AddNode(BenchFixtures.K(2));
            var g = e.AddReferenceNode(BenchFixtures.K(3));
            e.AddSineSource(a, g, new SineDrive(12.0, 5.0, 0.0), BenchFixtures.K(20), StateKey.From(BenchFixtures.K(20)));
            e.AddResistor(a, x, 100.0, BenchFixtures.K(10));
            e.AddCapacitor(x, g, 1e-3, BenchFixtures.K(11), StateKey.From(BenchFixtures.K(11)));
            e.AddInductor(x, g, 1.0, BenchFixtures.K(12), StateKey.From(BenchFixtures.K(12)));
        }
        _net.SolveOperatingPoint();
        for (var i = 0; i < 10; i++) _net.Solve(new TickClock(i, 0.05));
        _island = _net.IslandOf(a);
        _buf.Clear();
        _net.Islands.Of(_island).Snapshot(_buf);
        _blob = _buf.WrittenSpan.ToArray();
    }

    [Benchmark]
    public int Snapshot()
    {
        _buf.Clear();
        _net.Islands.Of(_island).Snapshot(_buf);
        return _buf.WrittenCount;
    }

    [Benchmark]
    public int Restore() => _net.Islands.Of(_island).Restore(_blob).Matched;
}
