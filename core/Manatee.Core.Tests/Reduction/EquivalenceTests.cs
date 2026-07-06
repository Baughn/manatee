using System;
using System.Collections.Generic;
using Manatee.Core;
using Manatee.Core.Reduction;

namespace Manatee.Core.Tests.Reduction;

/// <summary>
/// Equivalence tests (testing-strategy.md; compaction.md Invariants; api.md §19):
/// RAW (one resistor per segment) vs REDUCED (series chains collapsed) must produce
/// identical terminal voltages, and probes must agree with the raw interior nodes —
/// over hand fixtures AND seeded random ladders/meshes with random tap positions.
/// </summary>
public sealed class EquivalenceTests
{
    private static void AssertEquivalent(RedFx.Case c, double tol = 1e-6)
    {
        var red = RedFx.BuildReduced(c);
        var raw = RedFx.BuildRaw(c);

        foreach (var kv in red.Ports)
        {
            // Re-resolve the port by key after solve (handles may have been reissued
            // by an island rebuild — the api.md §16/§17 re-pin-by-key contract).
            var vr = red.Net.Solution.Voltage(red.Graph.PortNode(kv.Key));
            var vraw = raw.Net.Solution.Voltage(raw.Ports[kv.Key]);
            RedFx.Close(vraw, vr, $"port {kv.Key.Lo}", tol);
        }
        for (var i = 0; i < red.Probes.Count; i++)
        {
            var vr = red.Net.Solution.Read(red.Probes[i]);
            var vraw = raw.Net.Solution.Voltage(raw.ProbeNodes[i]);
            RedFx.Close(vraw, vr, $"probe {i}", tol);
        }
    }

    // ------------------------------------------------------------- hand fixtures

    [Fact]
    public void Series_chain_collapses_to_one_resistor_and_probe_reads_interior()
    {
        // 1 —100— 2 —100— 3 —100— 4 ; 10 V 1→4. Interiors 2,3 collapse to ONE 300 Ω.
        var c = new RedFx.Case()
            .Seg(10, 1, 2, 100).Seg(20, 2, 3, 100).Seg(30, 3, 4, 100)
            .Source(1, 4, 10.0)
            .Probe(20, 0.5);    // middle of the middle segment ⇒ cumulative R = 150/300 ⇒ V = 5

        var red = RedFx.BuildReduced(c);
        Assert.Equal(2, red.Graph.LiveNodeCount);    // only the two endpoints survive
        Assert.Equal(1, red.Graph.LiveChainCount);   // one equivalent resistor
        Assert.Equal(5.0, red.Net.Solution.Read(red.Probes[0]), 6);

        AssertEquivalent(c);
    }

    [Fact]
    public void Branch_junction_is_preserved_interior_of_stub_collapses()
    {
        // 1 —100— 2 —100— 3 ; stub 2 —100— 4 —100— 5. Junction 2 is degree-3 (a TAP,
        // preserved); 4 is a no-tap interior (collapses: 2→5 becomes one 200 Ω).
        var c = new RedFx.Case()
            .Seg(10, 1, 2, 100).Seg(20, 2, 3, 100).Seg(30, 2, 4, 100).Seg(40, 4, 5, 100)
            .Port(2)
            .Source(1, 3, 10.0)
            .Probe(40, 0.25);

        var red = RedFx.BuildReduced(c);
        // Nodes: 1, 2, 3, 5 survive; 4 collapses.
        Assert.Equal(4, red.Graph.LiveNodeCount);
        AssertEquivalent(c);
    }

    [Fact]
    public void Perfect_conductor_unions_endpoints_into_one_equipotential_node()
    {
        // 1 —100— 2 ==0== 3 —100— 4 ; the 0 Ω link unions 2 and 3 into one region.
        var c = new RedFx.Case()
            .Seg(10, 1, 2, 100).Seg(20, 2, 3, 0).Seg(30, 3, 4, 100)
            .Port(2).Port(3)
            .Source(1, 4, 10.0);

        var red = RedFx.BuildReduced(c);
        // Regions: {1}, {2,3}, {4} ⇒ 3 nodes. Divider midpoint = 5 V.
        Assert.Equal(3, red.Graph.LiveNodeCount);
        Assert.Equal(5.0, red.Net.Solution.Voltage(red.Graph.PortNode(RedFx.J(2))), 6);
        // Ports 2 and 3 resolve to the SAME node (equipotential).
        Assert.Equal(red.Graph.PortNode(RedFx.J(2)), red.Graph.PortNode(RedFx.J(3)));

        AssertEquivalent(c);
    }

    [Fact]
    public void Parallel_segments_between_two_taps_stay_two_equivalents()
    {
        // 1 —100— 2, and TWO parallel runs 2⇒3 (each 2-seg, 100+100=200 Ω), 3 —100— 4.
        var c = new RedFx.Case()
            .Seg(10, 1, 2, 100)
            .Seg(20, 2, 5, 100).Seg(21, 5, 3, 100)     // run A: 2-5-3
            .Seg(30, 2, 6, 100).Seg(31, 6, 3, 100)     // run B: 2-6-3
            .Seg(40, 3, 4, 100)
            .Port(2).Port(3)
            .Source(1, 4, 12.0);

        var red = RedFx.BuildReduced(c);
        // 2 and 3 are degree-3 taps; 5,6 collapse ⇒ two 200 Ω equivalents in parallel.
        Assert.Equal(4, red.Graph.LiveNodeCount);   // 1,2,3,4
        Assert.Equal(4, red.Graph.LiveChainCount);  // seg10, runA, runB, seg40
        AssertEquivalent(c);
    }

    [Fact]
    public void Direct_parallel_duplicate_segments_stay_two_distinct_equivalents()
    {
        // 1 —100— 2, then TWO *direct* parallel edges 2⇒3 (seg20, seg21, no interior
        // node between them), then 3 —100— 4. Each parallel edge keeps its own segment
        // key, so the reducer must emit TWO separate equivalents between 2 and 3 (not
        // silently dedup them into one) — the parallel-duplicate case the mesh fuzzers
        // only hit by chance.
        var c = new RedFx.Case()
            .Seg(10, 1, 2, 100)
            .Seg(20, 2, 3, 100).Seg(21, 2, 3, 100)     // two direct parallel edges
            .Seg(40, 3, 4, 100)
            .Port(2).Port(3)
            .Source(1, 4, 12.0);

        var red = RedFx.BuildReduced(c);
        Assert.Equal(4, red.Graph.LiveNodeCount);   // 1,2,3,4
        Assert.Equal(4, red.Graph.LiveChainCount);  // seg10, seg20, seg21, seg40 — the duplicates stay distinct
        AssertEquivalent(c);
    }

    [Fact]
    public void Probe_on_a_shorted_resistive_segment_reads_the_equipotential_region()
    {
        // 1 —100— 2 —100— 3 —100— 4, but 2 and 3 are ALSO tied by a 0 Ω bridge (seg25),
        // so the resistive seg20 between them becomes a SHORTED resistor: its endpoints
        // are equipotential, the reducer drops it, and a probe on it must read the merged
        // region node (ComputeProbeAim's shorted branch) — still agreeing with raw.
        var c = new RedFx.Case()
            .Seg(10, 1, 2, 100).Seg(20, 2, 3, 100).Seg(25, 2, 3, 0).Seg(30, 3, 4, 100)
            .Port(2).Port(3)
            .Source(1, 4, 10.0)
            .Probe(20, 0.5);   // probe on the shorted (equipotential) resistive segment

        var red = RedFx.BuildReduced(c);
        Assert.Equal(3, red.Graph.LiveNodeCount);                     // {1},{2,3},{4}
        Assert.Equal(5.0, red.Net.Solution.Read(red.Probes[0]), 6);   // midpoint of the 100+100 divider
        AssertEquivalent(c);
    }

    // ------------------------------------------------------ seeded random ladders

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(7)]
    [InlineData(42)]
    [InlineData(1337)]
    public void Random_ladder_reduced_matches_raw(int seed)
    {
        var rng = new Random(seed);
        var n = 6 + rng.Next(20);
        var c = new RedFx.Case();

        ulong sk = 1000;
        for (var i = 1; i < n; i++)                       // backbone path 1..n (connected)
            c.Seg(sk++, (ulong)i, (ulong)(i + 1), 5 + rng.NextDouble() * 500);

        var extra = rng.Next(n);                          // random rungs/branches
        for (var j = 0; j < extra; j++)
        {
            var a = (ulong)(1 + rng.Next(n));
            var b = (ulong)(1 + rng.Next(n));
            if (a == b) continue;
            c.Seg(sk++, a, b, 5 + rng.NextDouble() * 500);
        }

        c.Source(1, (ulong)n, 10.0 + rng.NextDouble() * 40);
        // Declare a random subset of interior junctions as ports (the rest collapse).
        for (var i = 2; i < n; i++) if (rng.Next(3) == 0) c.Port((ulong)i);

        // A few interior probes on DISTINCT backbone segments (the raw reference
        // exposes one interior node per segment).
        var probeSegs = new HashSet<ulong>();
        var probes = 1 + rng.Next(4);
        for (var p = 0; p < probes; p++)
        {
            var seg = 1000 + (ulong)rng.Next(n - 1);
            if (probeSegs.Add(seg)) c.Probe(seg, 0.05 + rng.NextDouble() * 0.9);
        }

        AssertEquivalent(c, 1e-5);
    }

    [Theory]
    [InlineData(3)]
    [InlineData(11)]
    [InlineData(99)]
    [InlineData(2024)]
    public void Random_mesh_reduced_matches_raw(int seed)
    {
        var rng = new Random(seed);
        var n = 8 + rng.Next(16);
        var c = new RedFx.Case();

        ulong sk = 1000;
        for (var i = 1; i < n; i++)
            c.Seg(sk++, (ulong)i, (ulong)(i + 1), 10 + rng.NextDouble() * 300);

        var extra = n + rng.Next(2 * n);                  // denser mesh
        var seen = new HashSet<(ulong, ulong)>();
        for (var j = 0; j < extra; j++)
        {
            var a = (ulong)(1 + rng.Next(n));
            var b = (ulong)(1 + rng.Next(n));
            if (a == b) continue;
            var key = a < b ? (a, b) : (b, a);
            if (!seen.Add(key)) continue;
            c.Seg(sk++, a, b, 10 + rng.NextDouble() * 300);
        }

        c.Source(1, (ulong)n, 24.0);
        for (var i = 2; i < n; i++) if (rng.Next(4) == 0) c.Port((ulong)i);

        AssertEquivalent(c, 1e-5);
    }
}
