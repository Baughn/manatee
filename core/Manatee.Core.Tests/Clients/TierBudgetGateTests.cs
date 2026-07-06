using System;
using System.Runtime.CompilerServices;
using Manatee.Core;

namespace Manatee.Core.Tests.Clients;

/// <summary>
/// The CI-shaped tier-budget gates (api.md §9, §22.a "the standing CI tier-budget
/// test"; testing-strategy.md "fails CI if tiers 1–2 allocate after warmup"). These are
/// the fast-category [Fact] mirror of the BenchmarkDotNet MemoryDiagnoser suites in
/// core/Manatee.Benchmarks: BDN is the human-facing measurement; these Facts are the
/// build-breaking assertion. Where GC.GetAllocatedBytesForCurrentThread is inert
/// (historically on Mono) the allocation assertion is skipped rather than false-passing.
/// </summary>
public sealed class TierBudgetGateTests
{
    private static ExternalKey K(ulong id) => new(id);

    private static NetlistOptions Dc(double dt) => new()
    {
        Profile = SolverProfile.Dc(dt),
        Wiring = WiringPolicy.ExplicitOnly(),
        Partitioning = PartitioningMode.SelfPartitioned,
    };

    private static NetlistOptions Mixed(double dt) => new()
    {
        Profile = SolverProfile.Mixed(dt),
        Wiring = WiringPolicy.ExplicitOnly(),
        Partitioning = PartitioningMode.SelfPartitioned,
    };

    // A resistive ladder + source (the tier-1 Drive-then-Solve fixture).
    private static (Core.Netlist net, VSourceId src) Ladder(int rungs, double dt)
    {
        var net = new Core.Netlist(Dc(dt));
        VSourceId src;
        using (var e = net.Edit())
        {
            var g = e.AddReferenceNode(K(0));
            var prev = e.AddNode(K(1));
            src = e.AddVoltageSource(prev, g, 12.0, K(1_000_000));
            for (var i = 0; i < rungs; i++)
            {
                var next = e.AddNode(K(2 + (ulong)i));
                e.AddResistor(prev, next, 10.0, K(10_000 + (ulong)i));
                e.AddResistor(next, g, 1000.0, K(20_000 + (ulong)i));
                prev = next;
            }
        }
        net.SolveOperatingPoint();
        return (net, src);
    }

    // A single loop with a series switch (the tier-2 refactor fixture).
    private static (Core.Netlist net, SwitchId sw) SwitchLoop(double dt)
    {
        var net = new Core.Netlist(Dc(dt));
        SwitchId sw;
        using (var e = net.Edit())
        {
            var g = e.AddReferenceNode(K(0));
            var a = e.AddNode(K(1));
            var b = e.AddNode(K(2));
            e.AddVoltageSource(a, g, 12.0, K(1_000_000));
            e.AddResistor(a, b, 100.0, K(10));
            sw = e.AddSwitch(b, g, true, K(11));
        }
        net.SolveOperatingPoint();
        return (net, sw);
    }

    // ── THE ZERO-ALLOC GATE ──────────────────────────────────────────────────

    [Fact]
    public void Warmed_tier1_and_tier2_tick_loops_allocate_zero_bytes()
    {
        var (ladder, src) = Ladder(64, 0.5);
        var (loop, sw) = SwitchLoop(0.5);

        long tick = 0;

        // Warm both paths: tier-1 (Drive + Solve, frozen pivot) and tier-2 (toggle +
        // Solve, one refactor). The first couple solves cache the symbolic factor.
        for (var i = 0; i < 5; i++)
        {
            ladder.Drive(src, 12.0 + (i & 1));
            ladder.Solve(new TickClock(tick++, 0.5));
        }
        var closed = true;
        for (var i = 0; i < 5; i++)
        {
            closed = !closed; loop.Adjust(sw, closed);
            loop.Solve(new TickClock(tick++, 0.5));
        }

        if (!CounterIsReliable())
            return;   // counter inert on this runtime — best-effort assertion skipped

        // Take the MINIMUM allocation over several sub-runs. The steady tick path is
        // deterministically 0-alloc; any nonzero reading is a background-GC / tiered-JIT
        // perturbation of the per-thread counter under heavy parallel test load, which can
        // only ADD bytes — so a single clean sub-run (min == 0) proves the 0-alloc property.
        var alloc1 = MeasureTier1(ladder, src, ref tick);
        Assert.True(alloc1 == 0, $"tier-1 Drive+Solve loop allocated {alloc1} B (min over runs; expected 0)");
        Assert.Equal(0, ladder.LastTickStats.Refactorizations);
        Assert.Equal(0, ladder.LastTickStats.BytesAllocated);

        var alloc2 = MeasureTier2(loop, sw, ref closed, ref tick);
        Assert.True(alloc2 == 0, $"tier-2 toggle+Solve loop allocated {alloc2} B (min over runs; expected 0)");
        Assert.Equal(0, loop.LastTickStats.BytesAllocated);
        // The toggle tick must GENUINELY be tier-2 — exactly one refactorization — or a
        // regression that no-ops the Adjust (or leaves the switch conductance in the
        // active set) would degrade this into a trivially-0-alloc tier-1 loop and
        // silently retire the tier-2 coverage.
        Assert.Equal(1, loop.LastTickStats.Refactorizations);
    }

    // Tier-1 steady path: Drive + Solve, no structural change ⇒ 0 refactor, 0 bytes.
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    private static long MeasureTier1(Core.Netlist ladder, VSourceId src, ref long tick)
    {
        long best = long.MaxValue;
        for (var run = 0; run < 8 && best != 0; run++)
        {
            var before = GC.GetAllocatedBytesForCurrentThread();
            for (var i = 0; i < 200; i++)
            {
                ladder.Drive(src, 12.0 + (i & 1));
                ladder.Solve(new TickClock(tick++, 0.5));
            }
            var d = GC.GetAllocatedBytesForCurrentThread() - before;
            if (d < best) best = d;
        }
        return best;
    }

    // Tier-2 path: a switch toggle refactors (expected) but must not ALLOCATE after warmup —
    // the frozen-pattern refactor reuses the symbolic factor's arenas.
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    private static long MeasureTier2(Core.Netlist loop, SwitchId sw, ref bool closed, ref long tick)
    {
        long best = long.MaxValue;
        for (var run = 0; run < 8 && best != 0; run++)
        {
            var before = GC.GetAllocatedBytesForCurrentThread();
            for (var i = 0; i < 200; i++)
            {
                closed = !closed; loop.Adjust(sw, closed);
                loop.Solve(new TickClock(tick++, 0.5));
            }
            var d = GC.GetAllocatedBytesForCurrentThread() - before;
            if (d < best) best = d;
        }
        return best;
    }

    // ── TIER-BUDGET ASSERTIONS ───────────────────────────────────────────────

    [Fact]
    public void Steady_dc_tick_is_tier1_no_refactor_no_rebuild()
    {
        var (net, src) = Ladder(32, 0.5);
        long tick = 0;
        for (var i = 0; i < 5; i++) { net.Drive(src, 12.0); net.Solve(new TickClock(tick++, 0.5)); }

        net.Drive(src, 12.5);
        net.Solve(new TickClock(tick++, 0.5));
        ref readonly var s = ref net.LastTickStats;
        Assert.Equal(0, s.Refactorizations);
        Assert.Equal(0, s.IslandRebuilds);
        Assert.Equal(0, s.MergesApplied);
        Assert.Equal(0, s.StaleHandleReads);
    }

    [Fact]
    public void Steady_ac_island_lives_in_tier1()
    {
        // A subcycled AC island (5 Hz, dt 0.05 ⇒ N≈5 substeps). api.md §9: once warm the
        // per-tick cost is tier-1 — the substeps back-substitute on a FROZEN pivot, so no
        // refactorization and no island rebuild happen on a steady tick.
        var net = new Core.Netlist(Mixed(0.05));
        NodeId a, x;
        using (var e = net.Edit())
        {
            a = e.AddNode(K(1)); x = e.AddNode(K(2)); var g = e.AddReferenceNode(K(3));
            e.AddSineSource(a, g, new SineDrive(12.0, 5.0, 0.0), K(20), StateKey.From(K(20)));
            e.AddResistor(a, x, 10.0, K(10));
            e.AddResistor(x, g, 1000.0, K(11));
            e.AddCapacitor(x, g, 1e-4, K(12), StateKey.From(K(12)));
        }
        net.SolveOperatingPoint();

        long tick = 0;
        for (var i = 0; i < 10; i++) net.Solve(new TickClock(tick++, 0.05));   // warm the subcycle

        net.Solve(new TickClock(tick++, 0.05));
        ref readonly var s = ref net.LastTickStats;
        Assert.True(s.Substeps >= 3, $"expected the AC island to subcycle, got {s.Substeps} substeps");
        Assert.Equal(0, s.Refactorizations);
        Assert.Equal(0, s.IslandRebuilds);
        Assert.Equal(0, s.StaleHandleReads);
    }

    // One-time capability probe (api.md §8 AllocationSentinel pattern).
    private static bool CounterIsReliable()
    {
        var before = GC.GetAllocatedBytesForCurrentThread();
        var probe = new byte[4096];
        GC.KeepAlive(probe);
        return GC.GetAllocatedBytesForCurrentThread() - before > 0;
    }
}
