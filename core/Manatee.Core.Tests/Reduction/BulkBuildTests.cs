using System;
using System.Diagnostics;
using Manatee.Core;
using Manatee.Core.Reduction;
using Xunit.Abstractions;

namespace Manatee.Core.Tests.Reduction;

/// <summary>
/// Bulk-build smoke test (api.md §19; compaction.md: ~10k segments → low-hundreds of
/// nodes). Correctness + the ONE-compaction-pass guarantee at Dispose; wall-time is
/// logged (the perf gate itself is phase 10).
/// </summary>
public sealed class BulkBuildTests
{
    private readonly ITestOutputHelper _out;
    public BulkBuildTests(ITestOutputHelper output) => _out = output;

    [Fact]
    public void Bulk_build_10k_segments_is_one_pass_and_collapses_to_few_nodes()
    {
        const int segments = 10_000;
        const int tapEvery = 100;   // a branch stub every 100 segments ⇒ ~100 surviving taps

        var net = RedFx.NewNet();
        var g = new ConductorGraph(net, GraphOptions.SelfPartitioned);

        var sw = Stopwatch.StartNew();
        ulong branchJ = 5_000_000;
        using (var b = g.BeginBulkBuild(segments))
        {
            for (var i = 1; i <= segments; i++)
                b.AddSegment(new SegmentKey((ulong)i), new JunctionKey((ulong)i), new JunctionKey((ulong)(i + 1)),
                             new ConductorSpec(0.1, 1));
            // Branch stubs make interior junctions into degree-3 taps (they survive).
            ulong stubKey = 20_000_000;
            for (var i = tapEvery; i < segments; i += tapEvery)
                b.AddSegment(new SegmentKey(stubKey++), new JunctionKey((ulong)i), new JunctionKey(branchJ++),
                             new ConductorSpec(0.1, 1));
        }
        sw.Stop();

        // ONE compaction pass at Dispose (the load-scope guarantee).
        Assert.Equal(1, g.CompactionPasses);

        // ~100 taps + their stub ends + the two chain ends ⇒ low hundreds, not 10k.
        Assert.True(g.LiveNodeCount < 400, $"node count {g.LiveNodeCount} not collapsed");
        Assert.True(g.LiveNodeCount >= 100, $"expected the taps to survive, got {g.LiveNodeCount}");
        _out.WriteLine($"10k bulk build: {segments} segments → {g.LiveNodeCount} nodes, " +
                       $"{g.LiveChainCount} chains, {sw.Elapsed.TotalMilliseconds:F1} ms (1 pass)");

        // Correctness: energize end-to-end and read the far endpoint.
        var n1 = g.PortNode(new JunctionKey(1));
        var nEnd = g.PortNode(new JunctionKey(segments + 1));
        using (var e = net.Edit())
        {
            e.MarkReference(nEnd);
            e.AddVoltageSource(n1, nEnd, 12.0, new ExternalKey(0xE000_0000_0000_0000UL, 1));
        }
        net.SolveOperatingPoint();

        Assert.True(net.Solution.IsLive(net.IslandOf(g.PortNode(new JunctionKey(1)))));
        Assert.Equal(12.0, net.Solution.Voltage(g.PortNode(new JunctionKey(1))), 6);
        Assert.Equal(0.0, net.Solution.Voltage(g.PortNode(new JunctionKey(segments + 1))), 6);

        // A probe mid-run reads a sensible interior potential (a monotone divider).
        var probe = g.AddProbe(new SegmentKey(5000), 0.5);
        net.SolveOperatingPoint();
        var vMid = net.Solution.Read(probe);
        Assert.True(vMid > 0.0 && vMid < 12.0, $"mid probe {vMid} out of range");
    }
}
