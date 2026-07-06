using System;
using System.Collections.Generic;
using Manatee.Core;
using Manatee.Core.Reduction;

namespace Manatee.Core.Tests.Reduction;

/// <summary>
/// Incremental-equivalence tests (compaction.md Resync backstop; testing-strategy.md;
/// api.md §19 R11): a seeded random edit script (add/remove segments, merges, cuts)
/// vs a from-scratch bulk build of the SAME final geometry must produce identical
/// islands and solutions — checked as a <c>SaveNormalized</c> byte-equality (the
/// drift detector stated as an equality, §14) plus terminal-voltage agreement.
/// </summary>
public sealed class IncrementalEquivalenceTests
{
    private static RedFx.Case RandomFinalGeometry(Random rng, out int n)
    {
        n = 6 + rng.Next(16);
        var c = new RedFx.Case();
        ulong sk = 1000;
        for (var i = 1; i < n; i++)
            c.Seg(sk++, (ulong)i, (ulong)(i + 1), 10 + rng.NextDouble() * 300);
        var extra = rng.Next(n);
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
        for (var i = 2; i < n; i++) if (i % 3 == 0) c.Port((ulong)i);   // deterministic port subset
        return c;
    }

    [Theory]
    [InlineData(1)]
    [InlineData(5)]
    [InlineData(13)]
    [InlineData(64)]
    [InlineData(777)]
    [InlineData(9001)]
    public void Incremental_edit_script_matches_from_scratch(int seed)
    {
        var rng = new Random(seed);
        var c = RandomFinalGeometry(rng, out var n);

        var fs = RedFx.BuildReduced(c);
        var inc = RedFx.BuildReducedIncremental(c, seed);

        // Identical minimal netlist (islands + topology + values), slot-independent.
        Assert.Equal(RedFx.Normalized(fs.Net), RedFx.Normalized(inc.Net));

        // Identical island count and terminal voltages.
        Assert.Equal(fs.Net.Islands.Count, inc.Net.Islands.Count);
        for (var i = 1UL; i <= (ulong)n; i++)
        {
            var j = RedFx.J(i);
            var vf = fs.Net.Solution.Voltage(fs.Graph.PortNode(j));
            var vi = inc.Net.Solution.Voltage(inc.Graph.PortNode(j));
            RedFx.Close(vf, vi, $"junction {i}", 1e-9);
        }
    }

    [Fact]
    public void Cut_coalesces_to_one_rebuild_at_next_solve()
    {
        // Build a run 1-2-3-4-5 (all backbone), solve, then cut THREE segments in a
        // burst: the netlist rebuild is coalesced to a single one at the next Solve
        // (compaction.md; api.md §19 "N cuts = 1 rebuild").
        var net = RedFx.NewNet();
        var g = new ConductorGraph(net, GraphOptions.SelfPartitioned);
        using (var b = g.BeginBulkBuild(6))
        {
            b.AddSegment(RedFx.S(10), RedFx.J(1), RedFx.J(2), new ConductorSpec(100, 1));
            b.AddSegment(RedFx.S(11), RedFx.J(2), RedFx.J(3), new ConductorSpec(100, 1));
            b.AddSegment(RedFx.S(12), RedFx.J(3), RedFx.J(4), new ConductorSpec(100, 1));
            b.AddSegment(RedFx.S(13), RedFx.J(4), RedFx.J(5), new ConductorSpec(100, 1));
            b.AddSegment(RedFx.S(14), RedFx.J(1), RedFx.J(5), new ConductorSpec(100, 1)); // keeps 1..5 connected
        }
        var n1 = g.PortNode(RedFx.J(1));
        var n5 = g.PortNode(RedFx.J(5));
        using (var e = net.Edit()) { e.MarkReference(n5); e.AddVoltageSource(n1, n5, 10.0, new ExternalKey(0xE000_0000_0000_0000UL, 1)); }
        net.SolveOperatingPoint();

        // Burst of cuts, then ONE solve.
        g.RemoveSegment(RedFx.S(11));
        g.RemoveSegment(RedFx.S(12));
        g.RemoveSegment(RedFx.S(13));
        net.SolveOperatingPoint();

        Assert.Equal(1, net.LastTickStats.IslandRebuilds);
    }
}
