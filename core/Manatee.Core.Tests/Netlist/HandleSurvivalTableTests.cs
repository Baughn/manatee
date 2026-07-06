using System;
using Manatee.Core;

namespace Manatee.Core.Tests.Netlist;

/// <summary>
/// The handle-survival table (api.md §16) rendered as a test suite: one test per
/// implementable cell. Columns exercised: Drive [T1], Adjust [T2], Reconfigure
/// Close (merge), Reconfigure Open (split→rebuild), removal→island rebuild,
/// snapshot/restore (StateKey re-homing), and drift-resync (probe survival +
/// derived-key re-resolution).
///
/// Detection convention (debug builds): a surviving internal handle resolves
/// silently; a stale one throws <see cref="StaleHandleException"/>. Document-level
/// registrations (CouplerId, ProbeId) and keys re-resolve through TryResolve*.
/// </summary>
public sealed class HandleSurvivalTableTests
{
    private static ExternalKey K(ulong id) => new(id);

    private static Core.Netlist Net()
        => new(new NetlistOptions
        {
            Profile = SolverProfile.Dc(0.5),
            Wiring = WiringPolicy.ExplicitOnly(),
            Partitioning = PartitioningMode.SelfPartitioned,
            Debug = DebugLevel.Asserts,
        });

    // A one-island divider: A —[r1]— B —[r2]— GND, 10 V A→GND.
    private static Core.Netlist Divider(out NodeId a, out NodeId b, out NodeId g,
                                        out VSourceId src, out ResistorId r1, out ResistorId r2)
    {
        var net = Net();
        NodeId la, lb, lg; VSourceId ls; ResistorId lr1, lr2;
        using (var e = net.Edit())
        {
            la = e.AddNode(K(1)); lb = e.AddNode(K(2)); lg = e.AddReferenceNode(K(3));
            ls = e.AddVoltageSource(la, lg, 10.0, K(20));
            lr1 = e.AddResistor(la, lb, 1000.0, K(10));
            lr2 = e.AddResistor(lb, lg, 2000.0, K(11));
        }
        net.SolveOperatingPoint();
        a = la; b = lb; g = lg; src = ls; r1 = lr1; r2 = lr2;
        return net;
    }

    // Two islands X={ap,an}, Y={bp,bn} plus a breaker (Closed at Add ⇒ merged).
    private static Core.Netlist BreakerPair(out NodeId ap, out NodeId an, out NodeId bp, out NodeId bn,
                                            out ResistorId rx, out CouplerId br)
    {
        var net = Net();
        NodeId lap, lan, lbp, lbn; ResistorId lrx; CouplerId lbr;
        using (var e = net.Edit())
        {
            lap = e.AddNode(K(1)); lan = e.AddNode(K(2)); lbp = e.AddNode(K(3)); lbn = e.AddNode(K(4));
            lrx = e.AddResistor(lap, lan, 100.0, K(10));
            e.AddResistor(lbp, lbn, 100.0, K(11));
            lbr = e.AddCoupler(CouplerSpec.Breaker(), new CouplerPorts(lap, lan, lbp, lbn), K(20), StateKey.From(K(20)));
        }
        net.SolveOperatingPoint();
        ap = lap; an = lan; bp = lbp; bn = lbn; rx = lrx; br = lbr;
        return net;
    }

    private static void AssertAlive(Core.Netlist net, NodeId n) => net.IslandOf(n);
    private static void AssertAlive(Core.Netlist net, ResistorId r) => net.Solution.Current(r);
    private static void AssertStale(Core.Netlist net, NodeId n) => Assert.Throws<StaleHandleException>(() => net.IslandOf(n));
    private static void AssertStale(Core.Netlist net, ResistorId r) => Assert.Throws<StaleHandleException>(() => net.Solution.Current(r));

    // ------------------------------------------------------------------- NodeId

    [Fact]
    public void Node_survives_Drive()
    {
        var net = Divider(out _, out var b, out _, out var src, out _, out _);
        net.Drive(src, 12.0);
        AssertAlive(net, b);
    }

    [Fact]
    public void Node_survives_Adjust()
    {
        var net = Divider(out _, out var b, out _, out _, out var r1, out _);
        net.Adjust(r1, 1500.0);
        AssertAlive(net, b);
    }

    [Fact]
    public void Node_survives_Reconfigure_Close_merge()
    {
        var net = BreakerPair(out var ap, out _, out _, out _, out _, out _);
        // The Close/merge happened at AddCoupler commit; the pre-existing node
        // handle survives it, and the ensuing numeric rebuild too.
        AssertAlive(net, ap);
        net.SolveOperatingPoint();
        AssertAlive(net, ap);
    }

    [Fact]
    public void Node_invalidated_by_Reconfigure_Open_at_Solve()
    {
        var net = BreakerPair(out var ap, out _, out _, out _, out _, out var br);
        net.Reconfigure(br, CouplerState.Open);
        AssertAlive(net, ap);                 // still usable until Solve
        net.SolveOperatingPoint();            // split rebuild reissues member gens
        AssertStale(net, ap);
    }

    [Fact]
    public void Node_invalidated_by_removal_rebuild_at_Solve()
    {
        var net = Divider(out _, out var b, out _, out _, out var r1, out _);
        using (var e = net.Edit()) e.Remove(r1);
        AssertAlive(net, b);                  // survives the doomed window
        net.SolveOperatingPoint();
        AssertStale(net, b);
        Assert.True(net.TryResolveNode(K(2), out _));   // re-resolves by key
    }

    // ---------------------------------------------------------------- component id

    [Fact]
    public void Component_survives_Drive_and_Adjust()
    {
        var net = Divider(out _, out _, out _, out var src, out var r1, out var r2);
        net.Drive(src, 12.0); AssertAlive(net, r1);
        net.Adjust(r2, 2500.0); AssertAlive(net, r1);
    }

    [Fact]
    public void Removed_component_dies_at_commit_sibling_dies_at_Solve()
    {
        var net = Divider(out _, out _, out _, out _, out var r1, out var r2);
        using (var e = net.Edit()) e.Remove(r1);
        AssertStale(net, r1);                 // the removed one's own handle dies immediately
        AssertAlive(net, r2);                 // the sibling survives the doomed window
        net.SolveOperatingPoint();
        AssertStale(net, r2);                 // …and is reissued at the rebuild
    }

    [Fact]
    public void Component_survives_merge_invalidated_by_open()
    {
        var net = BreakerPair(out _, out _, out _, out _, out var rx, out var br);
        AssertAlive(net, rx);                 // merge renumbers nothing but the absorbed IslandId
        net.Reconfigure(br, CouplerState.Open);
        net.SolveOperatingPoint();
        AssertStale(net, rx);                 // the split rebuild reissues it
        Assert.True(net.TryResolve(K(10), out _));
    }

    // -------------------------------------------------- unaffected island is untouched

    [Fact]
    public void Handles_in_an_unaffected_island_survive_a_rebuild_elsewhere()
    {
        var net = Net();
        NodeId a, c; ResistorId ra;
        using (var e = net.Edit())
        {
            a = e.AddNode(K(1)); var b = e.AddNode(K(2));
            c = e.AddNode(K(3)); var d = e.AddNode(K(4));
            ra = e.AddResistor(a, b, 100.0, K(10));      // island X
            e.AddResistor(c, d, 100.0, K(11));           // island Y
            e.AddResistor(c, d, 100.0, K(12));           // removable, keeps Y connected
        }
        net.SolveOperatingPoint();

        using (var e = net.Edit()) e.Remove(Res(net, K(12)));   // rebuild Y only
        net.SolveOperatingPoint();

        AssertAlive(net, a);      // X untouched
        AssertAlive(net, ra);
        AssertStale(net, c);      // Y rebuilt
    }

    // -------------------------------------------------------------- CouplerId (§16)

    [Fact]
    public void Coupler_is_document_stable_across_open_rebuild()
    {
        var net = BreakerPair(out _, out _, out _, out _, out _, out var br);
        net.Reconfigure(br, CouplerState.Open);
        net.SolveOperatingPoint();            // the rebuild that reissues node/component gens

        Assert.True(net.TryResolveCoupler(K(20), out var live));
        Assert.Equal(br, live);               // same handle — never reissued by a rebuild
        net.Reconfigure(br, CouplerState.Closed);   // still usable in any phase order
        net.SolveOperatingPoint();
        Assert.Equal(1, net.Islands.Count);
    }

    [Fact]
    public void Coupler_dies_only_on_RemoveCoupler()
    {
        var net = BreakerPair(out _, out _, out _, out _, out _, out var br);
        using (var e = net.Edit()) e.RemoveCoupler(br);
        Assert.False(net.TryResolveCoupler(K(20), out _));
        Assert.Throws<StaleHandleException>(() => net.Reconfigure(br, CouplerState.Open));
    }

    // ---------------------------------------------------------------- ProbeId (§16)

    [Fact]
    public void Probe_is_document_stable_across_removal_rebuild()
    {
        var net = Net();
        ProbeId p; NodeId b;
        using (var e = net.Edit())
        {
            var a = e.AddNode(K(1)); b = e.AddNode(K(2)); var g = e.AddReferenceNode(K(3));
            e.AddVoltageSource(a, g, 10.0, K(20));
            e.AddResistor(a, b, 1000.0, K(10));
            e.AddResistor(b, g, 2000.0, K(11));
            e.AddResistor(a, b, 1e9, K(99));       // removable dummy
            p = e.AddProbe(b, K(30));
        }
        net.SolveOperatingPoint();

        using (var e = net.Edit()) e.Remove(Res(net, K(99)));
        net.SolveOperatingPoint();                 // rebuild the island

        Assert.True(net.TryResolveProbe(K(30), out var live));
        Assert.Equal(p, live);                     // same handle survives the rebuild
        Assert.True(net.TryResolveNode(K(2), out b));
        net.Meta.SetProbeInterpolation(p, b, b, 0.0);   // still usable (re-aim on the same handle)
    }

    // --------------------------------------------------------------- IslandId (§16)

    [Fact]
    public void IslandId_absorbed_by_merge_survivor_survives()
    {
        var net = Net();
        NodeId ap, bp;
        using (var e = net.Edit())
        {
            ap = e.AddNode(K(1)); var an = e.AddNode(K(2));
            bp = e.AddNode(K(3)); var bn = e.AddNode(K(4));
            e.AddResistor(ap, an, 100.0, K(10));
            e.AddResistor(bp, bn, 100.0, K(11));
        }
        net.SolveOperatingPoint();
        var idA = net.IslandOf(ap);
        var idB = net.IslandOf(bp);
        Assert.NotEqual(idA, idB);

        using (var e = net.Edit()) e.AddResistor(ap, bp, 100.0, K(20));   // merge
        // Exactly one of the two prior ids is now absorbed (invalid); the other
        // survives, and every live node maps to the survivor.
        var survivor = net.IslandOf(ap);
        Assert.True(survivor == idA || survivor == idB);
        var absorbed = survivor == idA ? idB : idA;
        Assert.False(net.Solution.IsLive(absorbed));   // absorbed IslandId is stale
        Assert.Equal(survivor, net.IslandOf(bp));       // node handles all point at the survivor
    }

    [Fact]
    public void IslandId_replaced_by_removal_rebuild()
    {
        var net = Divider(out _, out var b, out _, out _, out var r1, out _);
        var oldId = net.IslandOf(b);
        Assert.True(net.Solution.IsLive(oldId));

        using (var e = net.Edit()) e.Remove(r1);
        net.SolveOperatingPoint();

        Assert.False(net.Solution.IsLive(oldId));   // old id invalid after the rebuild
        Assert.True(net.TryResolveNode(K(2), out b));
        var newId = net.IslandOf(b);
        Assert.NotEqual(oldId, newId);              // a fresh id
        Assert.True(net.Solution.IsLive(newId));
    }

    // --------------------------------------------------------- ExternalKey (§16)

    [Fact]
    public void ExternalKey_re_resolves_across_every_rebuild()
    {
        var net = Divider(out _, out _, out _, out _, out var r1, out _);
        using (var e = net.Edit()) e.Remove(r1);
        net.SolveOperatingPoint();

        Assert.True(net.TryResolve(K(11), out var r2live));   // survivor resistor by key
        Assert.True(net.TryResolveNode(K(2), out _));          // node by key
        Assert.True(net.TryResolve(K(20), out _));             // source by key
        Assert.Equal(ComponentKind.Resistor, r2live.Kind);
    }

    // ---------------------------------------------------- PHASE-6 / PHASE-7 columns

    // §16 snapshot/restore column: StateKey-keyed units restore additively onto the
    // same island (the deep laws live in State/SnapshotRestoreTests; this pins the
    // survival-table cell itself). Landed with phase 6; the stub skip is retired.
    [Fact]
    public void StateKey_survives_snapshot_restore()
    {
        var net = Net();
        NodeId a, g; CapacitorId cap;
        using (var e = net.Edit())
        {
            a = e.AddNode(K(1)); g = e.AddReferenceNode(K(2));
            e.AddVoltageSource(a, g, 10.0, K(20));
            var x = e.AddNode(K(3));
            e.AddResistor(a, x, 1000.0, K(10));
            cap = e.AddCapacitor(x, g, 1e-3, K(11), StateKey.From(K(11)));
        }
        for (var t = 0; t < 5; t++) net.Solve(new TickClock(t, 0.5));
        Assert.True(net.TryReadStorageState(cap.AsRef(), out var charged) && charged > 0.1);

        var h = net.Islands.Of(a);
        var w = new System.Buffers.ArrayBufferWriter<byte>(h.SnapshotSize);
        h.Snapshot(w);

        for (var t = 5; t < 40; t++) net.Solve(new TickClock(t, 0.5));   // keep charging
        Assert.True(net.TryReadStorageState(cap.AsRef(), out var later) && later > charged);

        var res = net.Islands.Of(a).Restore(w.WrittenSpan);
        Assert.True(res.Matched >= 1, $"expected the cap unit to match; matched={res.Matched}");
        Assert.True(net.TryReadStorageState(cap.AsRef(), out var restored));
        Assert.Equal(charged, restored);   // bit-exact: the unit re-homed by StateKey
    }

    // §16 drift-resync column: a Resync KEEPS reduction-owned probes and re-aims
    // them on the SAME handle, and the probe's derived key (ConductorGraph.ProbeKey)
    // re-resolves to that handle. Landed with the reduction layer + deterministic
    // probe keys; the stub skip is retired.
    [Fact]
    public void Probe_re_resolves_after_drift_resync()
    {
        var net = Net();
        var g = new Manatee.Core.Reduction.ConductorGraph(net, Manatee.Core.Reduction.GraphOptions.SelfPartitioned);
        var seg = new Manatee.Core.Reduction.SegmentKey(10);
        var segB = new Manatee.Core.Reduction.SegmentKey(20);
        var j1 = new Manatee.Core.Reduction.JunctionKey(1);
        var j2 = new Manatee.Core.Reduction.JunctionKey(2);
        var j3 = new Manatee.Core.Reduction.JunctionKey(3);
        using (var b = g.BeginBulkBuild(2))
        {
            b.AddSegment(seg, j1, j2, new Manatee.Core.Reduction.ConductorSpec(100, 1));
            b.AddSegment(segB, j2, j3, new Manatee.Core.Reduction.ConductorSpec(100, 1));
        }
        var n1 = g.PortNode(j1);
        var n3 = g.PortNode(j3);
        using (var e = net.Edit()) { e.MarkReference(n3); e.AddVoltageSource(n1, n3, 10.0, K(99)); }
        var probe = g.AddProbe(seg, 0.5);
        net.SolveOperatingPoint();
        var before = net.Solution.Read(probe);
        Assert.True(Math.Abs(before - 7.5) < 1e-6, $"mid-first-segment read {before}");

        // Drift, then resync against the truth. The ProbeId must survive (re-aimed)
        // and the derived key must resolve to it.
        g.DebugCorruptSegmentOhms(segB, 150.0);
        var truth = new[]
        {
            new Manatee.Core.Reduction.GeometrySegment(seg, j1, j2, new Manatee.Core.Reduction.ConductorSpec(100, 1)),
            new Manatee.Core.Reduction.GeometrySegment(segB, j2, j3, new Manatee.Core.Reduction.ConductorSpec(100, 1)),
        };
        var rep = g.Resync(net.IslandOf(g.PortNode(j1)), new ArrayGeometry(truth));
        Assert.True(rep.Ok);
        net.SolveOperatingPoint();

        Assert.Equal(before, net.Solution.Read(probe), 9);   // same handle, same interior read
        Assert.True(net.TryResolveProbe(
            Manatee.Core.Reduction.ConductorGraph.ProbeKey(seg, 0.5), out var resolved));
        Assert.Equal(probe, resolved);
    }

    private sealed class ArrayGeometry : Manatee.Core.Reduction.IGeometrySource
    {
        private readonly Manatee.Core.Reduction.GeometrySegment[] _segs;
        public ArrayGeometry(Manatee.Core.Reduction.GeometrySegment[] segs) => _segs = segs;
        public System.Collections.Generic.IEnumerable<Manatee.Core.Reduction.GeometrySegment> Segments => _segs;
    }

    private static ResistorId Res(Core.Netlist net, ExternalKey key)
    {
        Assert.True(net.TryResolve(key, out var c));
        return new ResistorId(c.Slot, c.Gen, c.Net);
    }
}
