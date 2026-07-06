using System;
using Manatee.Core;

namespace Manatee.Core.Tests.Netlist;

/// <summary>
/// api.md §17 rule 4, absorbed-side merge case (fixed 2026-07-07 after the
/// integration tutorial exposed the violation): on the tick a galvanic merge
/// relabels an island's nodes into the survivor, the ABSORBED side must keep
/// reading its pre-merge LAST-GOOD published values through <c>Solution</c> —
/// never 0.0 — until the merged island first publishes. That covers node
/// potentials AND voltage-source aux flows (<c>Current</c>/<c>Power</c> on a
/// source). The survivor keeps its own published vector (as it always did) and
/// <c>IsLive</c> stays false for the merged island until it publishes. Faulted
/// reads are de-energized 0 STATUS-SCOPED (api.md §17.4/§20, decision log 27):
/// the merge flips the union to Dirty, so a previously-Faulted side reads its
/// last successfully PUBLISHED values through the window — 0 if it never
/// published (fault output itself is never published, so it can never leak).
/// Mechanism: last-good capture into per-node / per-component held-value arrays
/// at merge commit (Netlist.Internal.UnionNodes / Netlist.Runtime.NodePotential
/// / VSourceFlowLastGood). The published-then-faulted shapes are pinned in
/// FaultedReadGateTests.
/// </summary>
public sealed class MergeLastGoodReadbackTests
{
    private static ExternalKey K(ulong id) => new(id);

    private static Core.Netlist Net() => new(new NetlistOptions
    {
        Profile = SolverProfile.Dc(0.5),
        Wiring = WiringPolicy.ExplicitOnly(),
        Partitioning = PartitioningMode.SelfPartitioned,
        Debug = DebugLevel.Asserts,
    });

    // One divider island per keyBase: g(ref) ← r(100) ← m ← r(100) ← s ← vsrc(V).
    // Pre-merge: V(s) = V, V(m) = V/2. Key layout: s = base+1, m = base+2, g = base+3,
    // src = base+4, r1 = base+5, r2 = base+6.
    private static void AddDivider(StructuralEdit e, ulong keyBase, double volts,
                                   out NodeId s, out NodeId m, out NodeId g)
    {
        s = e.AddNode(K(keyBase + 1));
        m = e.AddNode(K(keyBase + 2));
        g = e.AddReferenceNode(K(keyBase + 3));
        e.AddVoltageSource(s, g, volts, K(keyBase + 4));
        e.AddResistor(s, m, 100.0, K(keyBase + 5));
        e.AddResistor(m, g, 100.0, K(keyBase + 6));
    }

    private static NodeId Node(Core.Netlist net, ulong key)
    {
        Assert.True(net.TryResolveNode(K(key), out var n), $"node key {key} must resolve");
        return n;
    }

    private static VSourceId Source(Core.Netlist net, ulong key)
    {
        Assert.True(net.TryResolve(K(key), out var c), $"source key {key} must resolve");
        return new VSourceId(c.Slot, c.Gen, c.Net);
    }

    // Two divider islands X (10 V) and Y (30 V) joined by a breaker on the mid
    // nodes, opened before the first solve so both islands publish independently.
    // Handles are re-resolved by key after the open-split rebuild.
    private static Core.Netlist TwoIslands(out NodeId mx, out NodeId my, out CouplerId br)
    {
        var net = Net();
        using (var e = net.Edit())
        {
            AddDivider(e, 100, 10.0, out _, out var m1, out var g1);
            AddDivider(e, 200, 30.0, out _, out var m2, out var g2);
            br = e.AddCoupler(CouplerSpec.Breaker(), new CouplerPorts(m1, g1, m2, g2),
                              K(900), StateKey.From(K(900)));
        }
        net.Reconfigure(br, CouplerState.Open);   // breakers are born Closed; split first
        net.SolveOperatingPoint();                // open-split rebuild reissues member gens
        mx = Node(net, 102); my = Node(net, 202);
        return net;
    }

    [Fact]
    public void Merge_tick_absorbed_reads_last_good_survivor_unchanged_islive_false()
    {
        var net = TwoIslands(out var mx, out var my, out var br);
        Assert.Equal(5.0, net.Solution.Voltage(mx), 6);
        Assert.Equal(15.0, net.Solution.Voltage(my), 6);
        var preMx = net.Solution.Voltage(mx);
        var preMy = net.Solution.Voltage(my);
        var preSx = net.Solution.Voltage(Node(net, 101));
        var preSy = net.Solution.Voltage(Node(net, 201));
        var vsX = Source(net, 104);
        var vsY = Source(net, 204);
        var preIx = net.Solution.Current(vsX);
        var preIy = net.Solution.Current(vsY);
        var prePx = net.Solution.Power(vsX);
        var prePy = net.Solution.Power(vsY);
        Assert.NotEqual(0.0, preIy);   // 30 V across 200 Ω: a real branch current
        Assert.NotEqual(0.0, prePy);
        var idX = net.IslandOf(mx);
        var idY = net.IslandOf(my);

        net.Reconfigure(br, CouplerState.Closed);   // the merge tick, pre-Solve

        // BOTH sides hold last-good (§17 rule 4), BIT-EXACT against the pre-merge
        // published reads: the survivor through its still-live published vector, the
        // absorbed side through the merge-commit capture. A 0.0 on either side is
        // the pre-fix physics lie (G = P/V² adaptors stamp shorts).
        Assert.Equal(preMx, net.Solution.Voltage(mx));
        Assert.Equal(preMy, net.Solution.Voltage(my));
        Assert.Equal(preSx, net.Solution.Voltage(Node(net, 101)));
        Assert.Equal(preSy, net.Solution.Voltage(Node(net, 201)));

        // Aux flows hold too (fixed with the same 2026-07-07 batch): a source's
        // branch current lives on an aux row minted for the absorbed island's dead
        // Circuit, so without the per-component capture the absorbed side's
        // Solution.Current/Power read a fictional one-tick 0 on every breaker
        // close — the exact dip shape the potential hold eliminates, just on a
        // different readback (battery charge controllers key on source current).
        Assert.Equal(preIx, net.Solution.Current(vsX));
        Assert.Equal(preIy, net.Solution.Current(vsY));
        Assert.Equal(prePx, net.Solution.Power(vsX));
        Assert.Equal(prePy, net.Solution.Power(vsY));

        // One merged island; exactly one prior id survived; NOT live until it publishes.
        var merged = net.IslandOf(mx);
        Assert.Equal(merged, net.IslandOf(my));
        Assert.True(merged == idX || merged == idY);
        Assert.False(net.Solution.IsLive(merged));
        Assert.False(net.Solution.IsLive(merged == idX ? idY : idX));   // absorbed id retired

        // After Solve both read the merged publish: mids tie via the 1 mΩ bridge,
        // (10 − v) + (30 − v) = 2v ⇒ v = 10 (± the milliohm bridge drops).
        net.Solve(new TickClock(1, 0.5));
        Assert.True(net.Solution.IsLive(net.IslandOf(mx)));
        var vx = net.Solution.Voltage(mx);
        var vy = net.Solution.Voltage(my);
        Assert.True(Math.Abs(vx - 10.0) < 0.01, $"merged mid X read {vx}");
        Assert.True(Math.Abs(vy - 10.0) < 0.01, $"merged mid Y read {vy}");
        Assert.Equal(0, net.LastTickStats.StaleHandleReads);
    }

    [Fact]
    public void Merge_plus_removal_same_tick_rebuild_supersedes_merge_window_still_last_good()
    {
        var net = Net();
        ResistorId extra;
        CouplerId br;
        using (var e = net.Edit())
        {
            AddDivider(e, 100, 10.0, out _, out var m1, out var g1);
            AddDivider(e, 200, 30.0, out var s2, out var m2, out var g2);
            extra = e.AddResistor(s2, m2, 1e6, K(299));   // removable; keeps Y connected
            br = e.AddCoupler(CouplerSpec.Breaker(), new CouplerPorts(m1, g1, m2, g2),
                              K(900), StateKey.From(K(900)));
        }
        net.Reconfigure(br, CouplerState.Open);
        net.SolveOperatingPoint();
        var mx = Node(net, 102);
        var my = Node(net, 202);
        Assert.True(net.TryResolve(K(299), out var c));
        extra = new ResistorId(c.Slot, c.Gen, c.Net);

        var preMx = net.Solution.Voltage(mx);
        var preMy = net.Solution.Voltage(my);
        Assert.Equal(5.0, preMx, 2);
        Assert.Equal(15.0, preMy, 2);   // the 1 MΩ extra shifts the divider ~0.75 mV

        // Same tick: a removal (schedules the absorbed island's rebuild) AND the
        // merge. §17 rule 3: rebuild supersedes merge — but only the MATRIX work is
        // superseded, never the read contract of the window.
        using (var e = net.Edit()) e.Remove(extra);
        net.Reconfigure(br, CouplerState.Closed);

        // The pre-Solve window: handles are still valid on both sides and read
        // last-good — the pending rebuild changes nothing about this window (§17
        // rule 4; the writes-go-to-the-document rule is its mutation twin).
        Assert.Equal(preMx, net.Solution.Voltage(mx));
        Assert.Equal(preMy, net.Solution.Voltage(my));

        // At Solve the inherited rebuild runs (rebuild supersedes merge) and reads
        // then follow the REBUILD rules: every member handle is reissued —
        // document-restamped at Solve — so the old handles are stale (defined
        // sentinel / throw under Asserts), and re-resolution by key reads the
        // merged, restamped solve. Last-good holds are NOT consulted after the
        // rebuild publishes.
        net.Solve(new TickClock(1, 0.5));
        Assert.Equal(1, net.LastTickStats.IslandRebuilds);
        Assert.Throws<StaleHandleException>(() => net.Solution.Voltage(mx));
        Assert.Throws<StaleHandleException>(() => net.Solution.Voltage(my));
        var vx = net.Solution.Voltage(Node(net, 102));
        var vy = net.Solution.Voltage(Node(net, 202));
        Assert.True(Math.Abs(vx - 10.0) < 0.01, $"rebuilt+merged mid X read {vx}");
        Assert.True(Math.Abs(vy - 10.0) < 0.01, $"rebuilt+merged mid Y read {vy}");
    }

    [Fact]
    public void Double_merge_chain_in_one_tick_holds_last_good_on_every_absorbed_side()
    {
        // Three divider islands A (10 V), B (20 V), C (30 V); close B–C then A–B in
        // ONE tick. A carries filler nodes so it out-sizes the B+C pair and the
        // second union ABSORBS the already-merged island: B's nodes are captured
        // through B's still-live (stale-flagged) runtime, while C's nodes — held
        // since merge 1 — are re-captured THROUGH the fallback (a held node
        // re-captures its own held value). That is the chained-merge composition
        // path.
        var net = Net();
        CouplerId brAb, brBc;
        using (var e = net.Edit())
        {
            AddDivider(e, 100, 10.0, out _, out var mA, out var gA);
            AddDivider(e, 200, 20.0, out _, out var mB, out var gB);
            AddDivider(e, 300, 30.0, out _, out var mC, out var gC);
            // Filler chain off A's rail (carries no current; A grows to 7 nodes so
            // it survives the union against the 6-node merged B+C pair).
            var prev = gA;
            for (var i = 0; i < 4; i++)
            {
                var x = e.AddNode(K((ulong)(110 + i)));
                e.AddResistor(prev, x, 100.0, K((ulong)(120 + i)));
                prev = x;
            }
            brAb = e.AddCoupler(CouplerSpec.Breaker(), new CouplerPorts(mA, gA, mB, gB),
                                K(900), StateKey.From(K(900)));
            brBc = e.AddCoupler(CouplerSpec.Breaker(), new CouplerPorts(mB, gB, mC, gC),
                                K(901), StateKey.From(K(901)));
        }
        net.Reconfigure(brAb, CouplerState.Open);
        net.Reconfigure(brBc, CouplerState.Open);
        net.SolveOperatingPoint();
        var mAr = Node(net, 102); var mBr = Node(net, 202); var mCr = Node(net, 302);
        Assert.Equal(5.0, net.Solution.Voltage(mAr), 6);
        Assert.Equal(10.0, net.Solution.Voltage(mBr), 6);
        Assert.Equal(15.0, net.Solution.Voltage(mCr), 6);
        var preA = net.Solution.Voltage(mAr);
        var preB = net.Solution.Voltage(mBr);
        var preC = net.Solution.Voltage(mCr);
        var vsA = Source(net, 104); var vsB = Source(net, 204); var vsC = Source(net, 304);
        var preIa = net.Solution.Current(vsA);
        var preIb = net.Solution.Current(vsB);
        var preIc = net.Solution.Current(vsC);

        net.Reconfigure(brBc, CouplerState.Closed);   // merge 1: B absorbs C
        net.Reconfigure(brAb, CouplerState.Closed);   // merge 2: A absorbs the B+C pair

        Assert.Equal(preA, net.Solution.Voltage(mAr));   // survivor of both merges
        Assert.Equal(preB, net.Solution.Voltage(mBr));   // captured via B's stale-flagged runtime
        Assert.Equal(preC, net.Solution.Voltage(mCr));   // re-captured from its own held value
        // Aux flows compose across the chain the same way: C's source was captured
        // at merge 1 and RE-captured from its own held flow at merge 2.
        Assert.Equal(preIa, net.Solution.Current(vsA));
        Assert.Equal(preIb, net.Solution.Current(vsB));
        Assert.Equal(preIc, net.Solution.Current(vsC));
        var merged = net.IslandOf(mAr);
        Assert.Equal(merged, net.IslandOf(mBr));
        Assert.Equal(merged, net.IslandOf(mCr));
        Assert.False(net.Solution.IsLive(merged));

        // (10 − v) + (20 − v) + (30 − v) = 3v ⇒ all mids ≈ 10 after the merged solve.
        net.Solve(new TickClock(1, 0.5));
        Assert.True(net.Solution.IsLive(net.IslandOf(mAr)));
        foreach (var n in new[] { mAr, mBr, mCr })
        {
            var v = net.Solution.Voltage(n);
            Assert.True(Math.Abs(v - 10.0) < 0.01, $"triple-merged mid read {v}");
        }
        Assert.Equal(0, net.LastTickStats.StaleHandleReads);
    }

    [Fact]
    public void Faulted_never_published_absorbed_island_reads_zero_across_merge()
    {
        // X: healthy 3-node divider (survivor — larger). Y: 2 nodes with
        // contradictory ideal sources (20 V ∥ 5 V on the same pair) ⇒ Faulted at
        // its FIRST solve, so it never publishes. Ruling (api.md §17.4/§20,
        // decision log 27): Faulted reads are de-energized 0 while the status
        // stands, and the merge captures the island's last successfully PUBLISHED
        // vector — for a never-published island that is the zero buffer, so its
        // nodes read 0 through the window too. (A published-then-faulted island
        // instead carries its pre-fault last-published values across the merge —
        // FaultedReadGateTests.) Either way no fault output is readable: failed
        // solves never publish.
        var net = Net();
        CouplerId br;
        using (var e = net.Edit())
        {
            AddDivider(e, 100, 10.0, out _, out var m1, out var g1);
            var ya = e.AddNode(K(201));
            var gy = e.AddReferenceNode(K(202));
            e.AddVoltageSource(ya, gy, 20.0, K(203));
            e.AddVoltageSource(ya, gy, 5.0, K(204));   // the contradiction
            br = e.AddCoupler(CouplerSpec.Breaker(), new CouplerPorts(m1, g1, ya, gy),
                              K(900), StateKey.From(K(900)));
        }
        net.Reconfigure(br, CouplerState.Open);
        net.SolveOperatingPoint();
        var mx = Node(net, 102);
        var ya2 = Node(net, 201);
        Assert.Equal(IslandStatus.Faulted, net.Islands.Of(ya2).Status);
        Assert.Equal(0.0, net.Solution.Voltage(ya2), 9);   // de-energized while Faulted
        var preMx = net.Solution.Voltage(mx);
        Assert.Equal(5.0, preMx, 6);

        net.Reconfigure(br, CouplerState.Closed);   // absorb the Faulted island

        Assert.Equal(0.0, net.Solution.Voltage(ya2), 9);   // last-published IS 0: never published
        Assert.Equal(preMx, net.Solution.Voltage(mx));     // healthy survivor holds last-good
        Assert.Equal(net.IslandOf(mx), net.IslandOf(ya2));

        // The contradiction is now the merged island's problem: it faults at Solve
        // and the whole island reads de-energized (fresh Circuit, nothing published).
        net.Solve(new TickClock(1, 0.5));
        Assert.Equal(IslandStatus.Faulted, net.Islands.Of(mx).Status);
        Assert.False(net.Solution.IsLive(net.IslandOf(mx)));
        Assert.Equal(0.0, net.Solution.Voltage(ya2), 9);
        Assert.Equal(0.0, net.Solution.Voltage(mx), 9);
    }

    [Fact]
    public void Snapshot_taken_in_the_merge_window_covers_both_sides_and_restores()
    {
        // Snapshot/restore must be unaffected by the merge window: membership is
        // committed at Reconfigure (§17 rule 1), so a snapshot of the merged island
        // taken BEFORE its first publish already carries both sides' state units.
        var net = Net();
        CapacitorId capX, capY;
        CouplerId br;
        using (var e = net.Edit())
        {
            AddDivider(e, 100, 10.0, out _, out var m1, out var g1);
            AddDivider(e, 200, 30.0, out _, out var m2, out var g2);
            capX = e.AddCapacitor(m1, g1, 1e-3, K(150), StateKey.From(K(150)));
            capY = e.AddCapacitor(m2, g2, 1e-3, K(250), StateKey.From(K(250)));
            br = e.AddCoupler(CouplerSpec.Breaker(), new CouplerPorts(m1, g1, m2, g2),
                              K(900), StateKey.From(K(900)));
        }
        net.Reconfigure(br, CouplerState.Open);
        net.SolveOperatingPoint();
        for (var t = 0; t < 3; t++) net.Solve(new TickClock(t, 0.5));   // charge the caps
        var mx = Node(net, 102);
        Assert.True(net.TryResolve(K(150), out var cx));
        Assert.True(net.TryResolve(K(250), out var cy));
        Assert.True(net.TryReadStorageState(cx, out var vCapX) && vCapX > 1.0);
        Assert.True(net.TryReadStorageState(cy, out var vCapY) && vCapY > 1.0);

        net.Reconfigure(br, CouplerState.Closed);   // the merge window

        var isl = net.Islands.Of(mx);               // the merged island, pre-publish
        Assert.Equal(2, isl.StateUnitCount);        // both sides' cap units are members
        var w = new System.Buffers.ArrayBufferWriter<byte>(isl.SnapshotSize);
        isl.Snapshot(w);
        Assert.True(w.WrittenCount > 0);

        // Publish the merged solve (cap states move on), then restore the window
        // blob: additive by StateKey, both units re-home bit-exact.
        for (var t = 3; t < 10; t++) net.Solve(new TickClock(t, 0.5));
        var res = net.Islands.Of(mx).Restore(w.WrittenSpan);
        Assert.Equal(2, res.Matched);
        Assert.Equal(0, res.OrphansInBlob);
        Assert.True(net.TryReadStorageState(cx, out var backX));
        Assert.True(net.TryReadStorageState(cy, out var backY));
        Assert.Equal(vCapX, backX);
        Assert.Equal(vCapY, backY);
    }
}
