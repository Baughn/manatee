using System;
using System.Collections.Generic;
using Manatee.Core;

namespace Manatee.Core.Tests.Netlist;

/// <summary>
/// Document-layer semantics (api.md §3–§6, §11, §15, §16, §17). These pin the
/// retained-mode document + structural machinery independently of the (stage-2)
/// numeric solve: handle survival, key re-resolution, atomic rollback, journal
/// overflow, and union-find islanding vs brute-force connectivity.
/// </summary>
public sealed class NetlistDocumentTests
{
    private static ExternalKey K(ulong id) => new(id);
    private static PartitionKey P(ulong id) => new(id);

    private static Core.Netlist Debug(PartitioningMode mode = PartitioningMode.SelfPartitioned, int journalCap = 0)
        => new(new NetlistOptions
        {
            Profile = SolverProfile.Dc(0.5),
            Wiring = WiringPolicy.ExplicitOnly(),
            Partitioning = mode,
            Debug = DebugLevel.Asserts,
            JournalCapacity = journalCap,
        });

    // ---------------------------------------------------------------- staleness

    [Fact]
    public void Removal_rebuild_invalidates_surviving_sibling_handles_at_Solve()
    {
        var net = Debug();
        NodeId a, b, c; ResistorId r1, r2;
        using (var e = net.Edit())
        {
            a = e.AddNode(K(1)); b = e.AddNode(K(2)); c = e.AddNode(K(3));
            r1 = e.AddResistor(a, b, 100, K(10));
            r2 = e.AddResistor(b, c, 100, K(11));
        }
        net.SolveOperatingPoint();               // merge-only: no gen reissue

        // r2 is fully usable up to Solve (document write survives the rebuild).
        net.Adjust(r2, 50);                       // no throw

        using (var e = net.Edit()) e.Remove(r1);  // schedules the island rebuild
        net.SolveOperatingPoint();                // rebuild reissues member gens

        var ex = Assert.Throws<StaleHandleException>(() => net.Adjust(r2, 25));
        Assert.Equal(r2.Slot, ex.Slot);
        Assert.Equal(r2.Gen, ex.ExpectedGen);
        Assert.NotEqual(ex.ExpectedGen, ex.ActualGen);
        Assert.Equal(ComponentKind.Resistor, ex.Kind);
        Assert.Equal(TopologyEventKind.IslandRebuilt.ToString(), ex.InvalidatingEvent);
    }

    [Fact]
    public void Key_resolves_to_fresh_handle_across_removal_rebuild()
    {
        var net = Debug();
        NodeId a, b, c; ResistorId r2;
        using (var e = net.Edit())
        {
            a = e.AddNode(K(1)); b = e.AddNode(K(2)); c = e.AddNode(K(3));
            e.AddResistor(a, b, 100, K(10));
            r2 = e.AddResistor(b, c, 100, K(11));
        }
        net.SolveOperatingPoint();
        var r1 = ResolveResistor(net, K(10));
        using (var e = net.Edit()) e.Remove(r1);
        net.SolveOperatingPoint();

        Assert.False(net.TryResolve(K(10), out _));         // removed component is gone
        Assert.True(net.TryResolve(K(11), out var live));    // survivor re-resolves
        Assert.Equal(ComponentKind.Resistor, live.Kind);
        Assert.NotEqual(r2.Gen, live.Gen);                   // fresh generation after rebuild
    }

    private static ResistorId ResolveResistor(Core.Netlist net, ExternalKey key)
    {
        Assert.True(net.TryResolve(key, out var c));
        return new ResistorId(c.Slot, c.Gen, c.Net);
    }

    [Fact]
    public void Stale_read_in_release_is_a_counted_no_op()
    {
        var net = new Core.Netlist(new NetlistOptions
        {
            Profile = SolverProfile.Dc(0.5),
            Wiring = WiringPolicy.ExplicitOnly(),
            Debug = DebugLevel.Off,     // release-mode sentinel
        });
        NodeId a, b, c; ResistorId r1, r2;
        using (var e = net.Edit())
        {
            a = e.AddNode(K(1)); b = e.AddNode(K(2)); c = e.AddNode(K(3));
            r1 = e.AddResistor(a, b, 100, K(10));
            r2 = e.AddResistor(b, c, 100, K(11));
        }
        net.SolveOperatingPoint();
        using (var e = net.Edit()) e.Remove(r1);
        net.SolveOperatingPoint();

        net.Adjust(r2, 25);   // stale, but no exception in release
        Assert.Equal(1, net.LastTickStats.StaleHandleReads);
    }

    // ------------------------------------------------------------ atomic commit

    [Fact]
    public void RemoveNode_non_degree0_aborts_whole_batch()
    {
        var net = Debug();
        NodeId a, b;
        using (var e = net.Edit())
        {
            a = e.AddNode(K(1)); b = e.AddNode(K(2));
            e.AddResistor(a, b, 100, K(10));
        }

        var bad = net.Edit();
        bad.RemoveNode(a);                        // a still has degree 1
        Assert.Throws<InvalidOperationException>(() => bad.Commit());

        // Nothing applied: node and resistor survive.
        Assert.True(net.TryResolveNode(K(1), out _));
        Assert.True(net.TryResolve(K(10), out _));

        // Legal path: drop the resistor first, then both degree-0 nodes.
        using (var e = net.Edit()) e.Remove(ResolveResistor(net, K(10)));
        using (var e = net.Edit()) { e.RemoveNode(a); e.RemoveNode(b); }
        Assert.False(net.TryResolveNode(K(1), out _));
        Assert.False(net.TryResolveNode(K(2), out _));
    }

    [Fact]
    public void ClientPartitioned_cross_partition_merge_throws_and_rolls_back()
    {
        var net = Debug(PartitioningMode.ClientPartitioned);
        NodeId a, b;
        using (var e = net.Edit())
        {
            a = e.AddNode(K(1), partition: P(1));
            b = e.AddNode(K(2), partition: P(2));
        }
        Assert.Equal(2, net.Islands.Count);

        var bad = net.Edit();
        bad.AddNode(K(3), partition: P(1));       // also staged — must not survive
        bad.AddResistor(a, b, 100, K(10));         // the illegal cross-partition edge
        Assert.Throws<PartitionMergeException>(() => bad.Commit());

        // NOTHING applied: no new node, no resistor, island count unchanged.
        Assert.False(net.TryResolveNode(K(3), out _));
        Assert.False(net.TryResolve(K(10), out _));
        Assert.Equal(2, net.Islands.Count);
    }

    [Fact]
    public void ClientPartitioned_coupler_may_join_partitions()
    {
        var net = Debug(PartitioningMode.ClientPartitioned);
        NodeId ap, an, bp, bn;
        using (var e = net.Edit())
        {
            ap = e.AddNode(K(1), partition: P(1)); an = e.AddNode(K(2), partition: P(1));
            bp = e.AddNode(K(3), partition: P(2)); bn = e.AddNode(K(4), partition: P(2));
            e.AddCoupler(CouplerSpec.Breaker(), new CouplerPorts(ap, an, bp, bn), K(10), StateKey.From(K(10)));
        }

        // Closed galvanic coupler unions (ap~bp) and (an~bn): 4 islands → 2.
        Assert.Equal(2, net.Islands.Count);
        // A coupler-spanning island reports the None partition sentinel.
        Assert.True(net.Islands.Of(ap).Partition.IsNone);
    }

    // ------------------------------------------------------------------- journal

    [Fact]
    public void Standing_cursor_reports_overflow_after_ring_laps()
    {
        var net = Debug(journalCap: 8);
        var cursor = net.Journal.OpenCursor();
        for (var i = 0; i < 20; i++)
            using (var e = net.Edit()) e.AddNode(K(100 + (ulong)i));   // 2 events each ⇒ laps 8

        Assert.True(net.Journal.Overflowed(cursor));
        Assert.False(net.Journal.TryRead(ref cursor, out _));
    }

    [Fact]
    public void OpenCursorAt_lapped_seq_is_overflowed()
    {
        var net = Debug(journalCap: 8);
        for (var i = 0; i < 20; i++)
            using (var e = net.Edit()) e.AddNode(K(100 + (ulong)i));

        var c = net.Journal.OpenCursorAt(0);
        Assert.True(net.Journal.Overflowed(c));
        Assert.False(net.Journal.TryRead(ref c, out _));
    }

    [Fact]
    public void Oversized_single_commit_reports_WindowLapped()
    {
        var net = Debug(journalCap: 8);
        var big = net.Edit();
        for (var i = 0; i < 10; i++) big.AddNode(K(200 + (ulong)i));    // 20 events > cap 8
        var receipt = big.Commit();
        Assert.True(receipt.WindowLapped);

        var small = net.Edit();
        small.AddNode(K(999));
        var r2 = small.Commit();
        Assert.False(r2.WindowLapped);
    }

    [Fact]
    public void Receipt_window_replays_this_commits_events()
    {
        var net = Debug();
        EditReceipt r;
        using (var e = net.Edit())
        {
            e.AddNode(K(1));
            e.AddNode(K(2));
            r = e.Commit();
        }
        var c = net.Journal.OpenCursorAt(r.JournalFrom);
        var kinds = new List<TopologyEventKind>();
        while (net.Journal.TryReadRange(ref c, r.JournalTo, out var ev)) kinds.Add(ev.Kind);

        // Two nodes ⇒ two (IslandCreated, NodeAdded) pairs.
        Assert.Equal(4, kinds.Count);
        Assert.Equal(2, kinds.FindAll(k => k == TopologyEventKind.NodeAdded).Count);
        Assert.Equal(2, kinds.FindAll(k => k == TopologyEventKind.IslandCreated).Count);
    }

    // ------------------------------------------------------- union-find vs brute

    [Theory]
    [InlineData(1)]
    [InlineData(7)]
    [InlineData(42)]
    [InlineData(1234)]
    public void Islanding_matches_brute_force_connectivity(int seed)
    {
        const int n = 12;
        var net = Debug();
        var nodes = new NodeId[n];
        using (var e = net.Edit())
            for (var i = 0; i < n; i++) nodes[i] = e.AddNode(K(1000 + (ulong)i));

        var rng = new Random(seed);
        var active = new Dictionary<ulong, (int, int)>();
        var keys = new List<ulong>();
        ulong nextKey = 5000;

        for (var step = 0; step < 300; step++)
        {
            var add = active.Count == 0 || rng.NextDouble() < 0.62;
            if (add)
            {
                int i = rng.Next(n), j = rng.Next(n);
                if (i == j) continue;
                var ck = nextKey++;
                using (var e = net.Edit()) e.AddResistor(nodes[i], nodes[j], 100, new ExternalKey(ck));
                active[ck] = (i, j); keys.Add(ck);
            }
            else
            {
                var idx = rng.Next(keys.Count);
                var ck = keys[idx];
                keys.RemoveAt(idx);
                using (var e = net.Edit())
                    if (net.TryResolve(new ExternalKey(ck), out var cr))
                        e.Remove(new ResistorId(cr.Slot, cr.Gen, cr.Net));
                active.Remove(ck);
            }
        }

        net.SolveOperatingPoint();     // executes all pending split-rebuilds

        // Re-resolve node handles (rebuilds reissue generations).
        for (var i = 0; i < n; i++)
            Assert.True(net.TryResolveNode(K(1000 + (ulong)i), out nodes[i]));

        // Brute-force connected components over the surviving edge set.
        var uf = new int[n];
        for (var i = 0; i < n; i++) uf[i] = i;
        int Find(int x) { while (uf[x] != x) { uf[x] = uf[uf[x]]; x = uf[x]; } return x; }
        foreach (var (i, j) in active.Values) uf[Find(i)] = Find(j);

        for (var i = 0; i < n; i++)
            for (var j = i + 1; j < n; j++)
            {
                var bruteSame = Find(i) == Find(j);
                var islandSame = net.IslandOf(nodes[i]) == net.IslandOf(nodes[j]);
                Assert.Equal(bruteSame, islandSame);
            }
    }

    // ------------------------------------------------------------- misc semantics

    [Fact]
    public void Adjust_below_epsilon_is_a_counted_no_op()
    {
        var net = Debug();
        NodeId a, b; ResistorId r;
        using (var e = net.Edit()) { a = e.AddNode(K(1)); b = e.AddNode(K(2)); r = e.AddResistor(a, b, 100.0, K(10)); }
        net.SolveOperatingPoint();

        Assert.Equal(Tier.Metadata, net.CostOfAdjust(r, 100.0 + 1e-9));
        net.Adjust(r, 100.0 + 1e-9);
        Assert.Equal(1, net.LastTickStats.AdjustNoOps);

        Assert.Equal(Tier.Conductance, net.CostOfAdjust(r, 200.0));
    }

    [Fact]
    public void Reconfigure_open_splits_a_galvanic_bridge_at_Solve()
    {
        var net = Debug();
        NodeId ap, an, bp, bn; CouplerId br;
        using (var e = net.Edit())
        {
            ap = e.AddNode(K(1)); an = e.AddNode(K(2)); bp = e.AddNode(K(3)); bn = e.AddNode(K(4));
            e.AddResistor(ap, an, 100, K(10));
            e.AddResistor(bp, bn, 100, K(11));
            br = e.AddCoupler(CouplerSpec.Breaker(), new CouplerPorts(ap, an, bp, bn), K(20), StateKey.From(K(20)));
        }
        net.SolveOperatingPoint();
        Assert.Equal(1, net.Islands.Count);        // closed breaker ⇒ one island

        Assert.Equal(Tier.Topology, net.CostOfReconfigure(br, CouplerState.Open));
        net.Reconfigure(br, CouplerState.Open);
        net.SolveOperatingPoint();
        Assert.Equal(2, net.Islands.Count);        // opened ⇒ two islands

        // CouplerId is document-stable across the rebuild.
        Assert.True(net.TryResolveCoupler(K(20), out var live));
        Assert.Equal(br, live);
    }

    // ------------------------------------------------- same-batch key replacement
    // CommitEdit applies removals BEFORE additions, so one atomic batch may retire
    // an entry and re-add its ExternalKey — the canonical "move a breaker's ports
    // across a client-side network merge". These pin the key map ending up on the
    // NEW slot (adds-first registered the key and then had the removal's cleanup
    // delete that very entry; DebugLevel.Asserts additionally rejected the batch
    // as a duplicate key).

    [Fact]
    public void Coupler_remove_plus_readd_same_key_in_one_batch_resolves_to_the_new_slot()
    {
        var net = Debug();
        NodeId ap, an, bp, bn, cp, cn; CouplerId br;
        using (var e = net.Edit())
        {
            ap = e.AddNode(K(1)); an = e.AddNode(K(2));
            bp = e.AddNode(K(3)); bn = e.AddNode(K(4));
            cp = e.AddNode(K(5)); cn = e.AddNode(K(6));
            e.AddResistor(ap, an, 100, K(10));
            e.AddResistor(bp, bn, 100, K(11));
            e.AddResistor(cp, cn, 100, K(12));
            br = e.AddCoupler(CouplerSpec.Breaker(), new CouplerPorts(ap, an, bp, bn), K(20), StateKey.From(K(20)));
        }
        net.SolveOperatingPoint();
        Assert.Equal(2, net.Islands.Count);        // A+B bridged, C alone

        CouplerId moved;
        using (var e = net.Edit())
        {
            e.RemoveCoupler(br);                    // retire the A↔B bridge…
            moved = e.AddCoupler(CouplerSpec.Breaker(), new CouplerPorts(ap, an, cp, cn),
                K(20), StateKey.From(K(20)));       // …and re-key it onto A↔C
        }
        net.SolveOperatingPoint();

        Assert.True(net.TryResolveCoupler(K(20), out var live));
        Assert.Equal(moved, live);
        Assert.NotEqual(br, live);
        Assert.Equal(2, net.Islands.Count);        // A+C bridged now, B alone
    }

    [Fact]
    public void Component_remove_plus_readd_same_key_in_one_batch_resolves_to_the_new_slot()
    {
        var net = Debug();
        NodeId a, b; ResistorId r;
        using (var e = net.Edit()) { a = e.AddNode(K(1)); b = e.AddNode(K(2)); r = e.AddResistor(a, b, 100, K(10)); }
        net.SolveOperatingPoint();

        ResistorId replacement;
        using (var e = net.Edit())
        {
            e.Remove(r);
            replacement = e.AddResistor(a, b, 50, K(10));
        }
        Assert.True(net.TryResolve(K(10), out var live));
        Assert.Equal(replacement.Slot, live.Slot);
        Assert.Equal(replacement.Gen, live.Gen);
        net.SolveOperatingPoint();                 // the batch leaves a solvable document
    }

    [Fact]
    public void Probe_added_on_a_node_removed_in_the_same_batch_dangles_and_reads_zero()
    {
        // Removals apply first, so the staged probe's aim is invalidated to -1
        // before the ProbeAdded journal entry is built — the commit must tolerate
        // the dangling aim (no island to attribute) and the probe reads 0 (§13).
        var net = Debug();
        NodeId a, g, c; ProbeId p;
        using (var e = net.Edit())
        {
            a = e.AddNode(K(1)); g = e.AddReferenceNode(K(2)); c = e.AddNode(K(3));
            e.AddVoltageSource(a, g, 10.0, K(20));
        }
        net.SolveOperatingPoint();

        using (var e = net.Edit())
        {
            p = e.AddProbe(c, K(30));
            e.RemoveNode(c);                       // degree-0; the staged aim dangles
        }
        net.SolveOperatingPoint();
        Assert.Equal(0.0, net.Solution.Read(p));
    }

    [Fact]
    public void DrainChanges_reports_created_and_merged()
    {
        var net = Debug();
        using (var e = net.Edit())
        {
            var a = e.AddNode(K(1));
            var b = e.AddNode(K(2));
            e.AddResistor(a, b, 100, K(10));   // merges the two singleton islands
        }
        Span<IslandChange> buf = stackalloc IslandChange[16];
        var n = net.Islands.DrainChanges(buf, out var lost);
        Assert.False(lost);
        var merged = 0; var created = 0;
        for (var i = 0; i < n; i++)
        {
            if (buf[i].Kind == IslandChangeKind.Created) created++;
            if (buf[i].Kind == IslandChangeKind.Merged) merged++;
        }
        Assert.Equal(2, created);
        Assert.Equal(1, merged);
    }
}
