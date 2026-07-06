using System;
using Manatee.Core;

namespace Manatee.Core.Tests.Netlist;

/// <summary>
/// Faulted reads are DE-ENERGIZED, STATUS-SCOPED, and enforced at the read path
/// (api.md §10/§17.4/§20, decision log 27 — adjudicated 2026-07-07). While an
/// island's status is Faulted, every Solution read of it — potentials, branch
/// currents, powers — reports 0. The scope is the status, not a taint: the
/// moment a repairing tier-2/3 change or a merge flips the island back to
/// Dirty, reads revert to ordinary last-good — the last successfully PUBLISHED
/// vector, which never contains fault output (failed solves hold the front
/// buffer; the Newton driver restores its pre-iteration capture). These tests
/// pin the published-then-faulted shape, which the public device vocabulary
/// cannot reach deterministically (gmin + milliohm switches keep values-only
/// systems nonsingular; structural contradictions rebuild the runtime first,
/// zeroing the published buffer), via the TryFaultIslandForTest seam — the
/// island keeps its converged published vector while its status says Faulted,
/// exactly what a wild Newton divergence produces. Both merge-union
/// orientations are pinned so "which side is larger" stays unobservable
/// (the never-published-Faulted merge case lives in MergeLastGoodReadbackTests).
/// </summary>
public sealed class FaultedReadGateTests
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
    // Pre-fault: V(s) = V, V(m) = V/2. Key layout: s = base+1, m = base+2, g = base+3,
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

    // Filler chain off a rail (carries no current): grows an island's node count
    // so the union orientation (larger side survives) is chosen deliberately.
    private static void AddFiller(StructuralEdit e, NodeId rail, ulong keyBase, int count)
    {
        var prev = rail;
        for (var i = 0; i < count; i++)
        {
            var x = e.AddNode(K(keyBase + (ulong)i));
            e.AddResistor(prev, x, 100.0, K(keyBase + 50 + (ulong)i));
            prev = x;
        }
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

    private static ResistorId Resistor(Core.Netlist net, ulong key)
    {
        Assert.True(net.TryResolve(K(key), out var c), $"resistor key {key} must resolve");
        return new ResistorId(c.Slot, c.Gen, c.Net);
    }

    [Fact]
    public void Faulted_reads_zero_while_faulted_then_last_published_again_once_dirty()
    {
        // Publish real physics, fault the island WITHOUT touching its published
        // vector (the seam mimics a values-only Newton divergence), and pin all
        // three phases of the contract: converged reads → de-energized 0 while
        // Faulted → the SAME pre-fault values again the moment a tier-2 Drive
        // flips it to Dirty (retry pending) → fresh physics after the retry.
        var net = Net();
        using (var e = net.Edit()) AddDivider(e, 200, 30.0, out _, out _, out _);
        net.SolveOperatingPoint();
        var s = Node(net, 201); var m = Node(net, 202);
        var vs = Source(net, 204); var r1 = Resistor(net, 205);
        var preS = net.Solution.Voltage(s);
        var preM = net.Solution.Voltage(m);
        var preI = net.Solution.Current(vs);
        var preIr = net.Solution.Current(r1);
        Assert.Equal(30.0, preS, 6);
        Assert.Equal(15.0, preM, 6);
        Assert.NotEqual(0.0, preI);

        Assert.True(net.TryFaultIslandForTest(m));
        var isl = net.Islands.Of(m);
        Assert.Equal(IslandStatus.Faulted, isl.Status);
        Assert.False(net.Solution.IsLive(isl.Id));
        // The read gate: the pre-fault vector is still held internally (retry
        // warm-start) but is NOT readable — potentials, aux flows, derived
        // currents, and powers all report de-energized 0 while Faulted.
        Assert.Equal(0.0, net.Solution.Voltage(s), 9);
        Assert.Equal(0.0, net.Solution.Voltage(m), 9);
        Assert.Equal(0.0, net.Solution.Current(vs), 9);
        Assert.Equal(0.0, net.Solution.Power(vs), 9);
        Assert.Equal(0.0, net.Solution.Current(r1), 9);

        // Any tier-2/3 change re-arms the retry (Faulted → Dirty, api.md §11) and
        // reads revert BIT-EXACT to the last successfully published vector — the
        // de-energized window is scoped to the status, never a taint on the data.
        net.Drive(vs, 31.0);
        Assert.Equal(IslandStatus.Dirty, net.Islands.Of(m).Status);
        Assert.Equal(preS, net.Solution.Voltage(s));
        Assert.Equal(preM, net.Solution.Voltage(m));
        Assert.Equal(preI, net.Solution.Current(vs));
        Assert.Equal(preIr, net.Solution.Current(r1));

        // The retry publishes the new drive: fresh physics, Ready, live.
        net.Solve(new TickClock(1, 0.5));
        Assert.Equal(IslandStatus.Ready, net.Islands.Of(m).Status);
        Assert.True(net.Solution.IsLive(net.IslandOf(m)));
        Assert.Equal(15.5, net.Solution.Voltage(m), 6);
    }

    [Fact]
    public void Published_then_faulted_absorbed_side_reads_pre_fault_last_good_through_merge_window()
    {
        // Orientation 1: the Faulted island is SMALLER and is absorbed. X: healthy
        // 10 V divider + 4 filler nodes (7 nodes — survives the union). Y: 30 V
        // divider (3 nodes), published then seam-faulted. The merge is the
        // tier-2/3 change that flips the union to Dirty, so Y's reads revert from
        // de-energized 0 to its pre-fault LAST-PUBLISHED values through the
        // window — captured at merge commit through the ungated readers.
        var net = Net();
        CouplerId br;
        using (var e = net.Edit())
        {
            AddDivider(e, 100, 10.0, out _, out var m1, out var g1);
            AddFiller(e, g1, 110, 4);
            AddDivider(e, 200, 30.0, out _, out var m2, out var g2);
            br = e.AddCoupler(CouplerSpec.Breaker(), new CouplerPorts(m1, g1, m2, g2),
                              K(900), StateKey.From(K(900)));
        }
        net.Reconfigure(br, CouplerState.Open);   // breakers are born Closed; split first
        net.SolveOperatingPoint();
        var mx = Node(net, 102); var my = Node(net, 202);
        var vsY = Source(net, 204);
        var preMx = net.Solution.Voltage(mx);
        var preMy = net.Solution.Voltage(my);
        var preIy = net.Solution.Current(vsY);
        Assert.Equal(15.0, preMy, 6);
        Assert.NotEqual(0.0, preIy);
        var idX = net.IslandOf(mx);

        Assert.True(net.TryFaultIslandForTest(my));
        Assert.Equal(IslandStatus.Faulted, net.Islands.Of(my).Status);
        Assert.Equal(0.0, net.Solution.Voltage(my), 9);    // de-energized while Faulted
        Assert.Equal(0.0, net.Solution.Current(vsY), 9);

        net.Reconfigure(br, CouplerState.Closed);          // X absorbs the Faulted Y

        Assert.Equal(idX, net.IslandOf(mx));               // orientation pinned: X survived
        Assert.Equal(net.IslandOf(mx), net.IslandOf(my));
        Assert.Equal(IslandStatus.Dirty, net.Islands.Of(my).Status);
        Assert.Equal(preMy, net.Solution.Voltage(my));     // pre-fault last-published, bit-exact
        Assert.Equal(preIy, net.Solution.Current(vsY));
        Assert.Equal(preMx, net.Solution.Voltage(mx));     // survivor unchanged
        Assert.False(net.Solution.IsLive(net.IslandOf(mx)));

        // (10 − v) + (30 − v) = 2v ⇒ mids ≈ 10 after the merged publish.
        net.Solve(new TickClock(1, 0.5));
        Assert.Equal(IslandStatus.Ready, net.Islands.Of(my).Status);
        Assert.True(Math.Abs(net.Solution.Voltage(my) - 10.0) < 0.01);
        Assert.True(Math.Abs(net.Solution.Voltage(mx) - 10.0) < 0.01);
    }

    [Fact]
    public void Published_then_faulted_survivor_side_reads_pre_fault_last_good_through_merge_window()
    {
        // Orientation 2: the Faulted island is LARGER and SURVIVES the union
        // (UnionNodes keeps the bigger side; the merge flips it Faulted → Dirty).
        // Same client-visible behavior as orientation 1 — the survivor reads its
        // pre-fault vector through its still-valid mappings, the healthy absorbed
        // side through the merge-commit capture — so which side survives is
        // unobservable, exactly as the tutorial demands of adaptors.
        var net = Net();
        CouplerId br;
        using (var e = net.Edit())
        {
            AddDivider(e, 100, 10.0, out _, out var m1, out var g1);
            AddDivider(e, 200, 30.0, out _, out var m2, out var g2);
            AddFiller(e, g2, 210, 4);   // Y grows to 7 nodes and wins the union
            br = e.AddCoupler(CouplerSpec.Breaker(), new CouplerPorts(m1, g1, m2, g2),
                              K(900), StateKey.From(K(900)));
        }
        net.Reconfigure(br, CouplerState.Open);
        net.SolveOperatingPoint();
        var mx = Node(net, 102); var my = Node(net, 202);
        var vsY = Source(net, 204);
        var preMx = net.Solution.Voltage(mx);
        var preMy = net.Solution.Voltage(my);
        var preIy = net.Solution.Current(vsY);
        var idY = net.IslandOf(my);

        Assert.True(net.TryFaultIslandForTest(my));
        Assert.Equal(0.0, net.Solution.Voltage(my), 9);    // de-energized while Faulted
        Assert.Equal(0.0, net.Solution.Current(vsY), 9);

        net.Reconfigure(br, CouplerState.Closed);          // Faulted Y absorbs healthy X

        Assert.Equal(idY, net.IslandOf(my));               // orientation pinned: Y survived
        Assert.Equal(net.IslandOf(mx), net.IslandOf(my));
        Assert.Equal(IslandStatus.Dirty, net.Islands.Of(my).Status);
        Assert.Equal(preMy, net.Solution.Voltage(my));     // pre-fault last-published, bit-exact
        Assert.Equal(preIy, net.Solution.Current(vsY));
        Assert.Equal(preMx, net.Solution.Voltage(mx));     // absorbed healthy side holds last-good
        Assert.False(net.Solution.IsLive(net.IslandOf(my)));

        net.Solve(new TickClock(1, 0.5));
        Assert.Equal(IslandStatus.Ready, net.Islands.Of(my).Status);
        Assert.True(Math.Abs(net.Solution.Voltage(my) - 10.0) < 0.01);
        Assert.True(Math.Abs(net.Solution.Voltage(mx) - 10.0) < 0.01);
    }
}
