using System;
using Manatee.Core;

namespace Manatee.Core.Tests.Netlist;

/// <summary>
/// Phase-9 hardening: AddIdealTransformer between two already-Ready islands. The
/// transformer's aux-row coupling requires same-matrix membership, so its Add unions
/// the two islands exactly like a wire would (galvanic-style merge, api.md §16 —
/// merge renumbers nothing except the absorbed IslandId), and BOTH sides' Reference
/// rails pin to the datum: the ideal transformer's constraints are purely
/// differential, so an unpinned secondary rail used to float at an arbitrary
/// common-mode offset (V(bp) read V/2, the "ground" −V/2). One datum per GALVANIC
/// sub-component — never per island — so a closed breaker joining two referenced
/// partitions keeps its honest two-wire return drop (the rails stay distinct nodes
/// bridged by the real 1 mΩ branch).
/// </summary>
public sealed class TransformerHotInsertTests
{
    private static ExternalKey K(ulong id) => new(id);

    private static Core.Netlist Dc() => new(new NetlistOptions
    {
        Profile = SolverProfile.Dc(0.5),
        Wiring = WiringPolicy.ExplicitOnly(),
        Debug = DebugLevel.Asserts,
    });

    [Fact]
    public void Hot_insert_between_two_ready_islands_couples_the_secondary()
    {
        var net = Dc();
        NodeId ap, an, bp, bn;
        using (var e = net.Edit())
        {
            ap = e.AddNode(K(1)); an = e.AddReferenceNode(K(2));
            e.AddVoltageSource(ap, an, 10.0, K(10));
            e.AddResistor(ap, an, 100.0, K(11));
            bp = e.AddNode(K(3)); bn = e.AddReferenceNode(K(4));
            e.AddResistor(bp, bn, 50.0, K(12));
        }
        net.SolveOperatingPoint();
        Assert.Equal(2, net.Islands.Count);
        var idA = net.IslandOf(ap);
        var idB = net.IslandOf(bp);
        Assert.NotEqual(idA, idB);

        // Edit-add the bridge: the islands must union at commit (merge, §16).
        using (var e = net.Edit())
            e.AddIdealTransformer(ap, an, bp, bn, 2.0, K(20));
        net.SolveOperatingPoint();

        Assert.Equal(1, net.Islands.Count);
        // Merge handle-survival (§16): node handles renumber NOTHING — the original
        // ids keep reading; exactly one of the two old IslandIds survives.
        Assert.Equal(10.0, net.Solution.Voltage(ap), 9);
        Assert.Equal(0.0, net.Solution.Voltage(an), 9);
        var aLive = net.Islands.Of(idA).Status != IslandStatus.Empty;
        var bLive = net.Islands.Of(idB).Status != IslandStatus.Empty;
        Assert.True(aLive ^ bLive, "a merge keeps the survivor id and retires the absorbed one");

        // The payoff: the secondary reads the turns-ratio voltage against ITS OWN
        // pinned rail (n = 2 ⇒ V_b = V_a / n = 5), not a floating ±2.5 split.
        Assert.Equal(5.0, net.Solution.Voltage(bp), 9);
        Assert.Equal(0.0, net.Solution.Voltage(bn), 9);
        Assert.Equal(0, net.LastTickStats.StaleHandleReads);
    }

    [Fact]
    public void Cold_build_with_both_rails_referenced_reads_the_same_answer()
    {
        // The same circuit built in ONE edit must agree with the hot-insert path.
        var net = Dc();
        NodeId bp, bn;
        using (var e = net.Edit())
        {
            var ap = e.AddNode(K(1)); var an = e.AddReferenceNode(K(2));
            e.AddVoltageSource(ap, an, 10.0, K(10));
            e.AddResistor(ap, an, 100.0, K(11));
            bp = e.AddNode(K(3)); bn = e.AddReferenceNode(K(4));
            e.AddResistor(bp, bn, 50.0, K(12));
            e.AddIdealTransformer(ap, an, bp, bn, 2.0, K(20));
        }
        net.SolveOperatingPoint();
        Assert.Equal(1, net.Islands.Count);
        Assert.Equal(5.0, net.Solution.Voltage(bp), 9);
        Assert.Equal(0.0, net.Solution.Voltage(bn), 9);
    }

    [Fact]
    public void Secondary_load_current_reflects_by_ampere_turns()
    {
        // n = 2: V_b = 5 V into 50 Ω ⇒ i_s = 0.1 A ⇒ primary draw i_p = i_s / n
        // = 0.05 A on top of the local 0.1 A load — read through the source power.
        var net = Dc();
        NodeId ap, an, bp, bn; VSourceId src;
        using (var e = net.Edit())
        {
            ap = e.AddNode(K(1)); an = e.AddReferenceNode(K(2));
            src = e.AddVoltageSource(ap, an, 10.0, K(10));
            e.AddResistor(ap, an, 100.0, K(11));
            bp = e.AddNode(K(3)); bn = e.AddReferenceNode(K(4));
            e.AddResistor(bp, bn, 50.0, K(12));
        }
        net.SolveOperatingPoint();
        using (var e = net.Edit())
            e.AddIdealTransformer(ap, an, bp, bn, 2.0, K(20));
        net.SolveOperatingPoint();

        // Source current: 10/100 (local) + 0.1/2 (reflected) = 0.15 A.
        var iSrc = Math.Abs(net.Solution.Current(src));
        Assert.Equal(0.15, iSrc, 6);
    }

    [Fact]
    public void Breaker_merge_of_two_referenced_partitions_keeps_the_two_wire_return_drop()
    {
        // Regression guard for the datum rule: a CLOSED breaker galvanically joins two
        // partitions that each brought a Reference rail. They are ONE galvanic
        // sub-component, so only the first rail pins; the second stays an ordinary
        // node whose potential shows the honest return-path drop across the 1 mΩ neg
        // bridge — pinning both by fiat would short the two-wire return.
        var net = new Core.Netlist(NetlistOptions.Stationeers(0.5));
        NodeId ap, an, bp, bn;
        using (var e = net.Edit())
        {
            ap = e.AddNode(K(1), NodeRole.Internal, new PartitionKey(1));
            an = e.AddReferenceNode(K(2)); e.SetPartition(an, new PartitionKey(1));
            bp = e.AddNode(K(3), NodeRole.Internal, new PartitionKey(2));
            bn = e.AddReferenceNode(K(4)); e.SetPartition(bn, new PartitionKey(2));
            e.AddVoltageSource(ap, an, 10.0, K(10));
            e.AddResistor(bp, bn, 100.0, K(12));
            e.AddCoupler(CouplerSpec.Breaker(), new CouplerPorts(ap, an, bp, bn), K(20), StateKey.From(K(20)));
        }
        net.SolveOperatingPoint();   // breaker defaults Closed ⇒ merged island

        Assert.Equal(1, net.Islands.Count);
        // Current ≈ 10 / (100 + 2·0.001), returning bn → an through the neg bridge,
        // so bn sits one bridge drop ABOVE the pinned rail an — small but strictly
        // nonzero (two-wire honesty).
        var vbn = net.Solution.Voltage(bn);
        Assert.NotEqual(0.0, vbn);
        Assert.Equal(0.1 * 0.001, vbn, 7);
        // And the load still sees ~the full source voltage across it.
        Assert.Equal(10.0, net.Solution.Voltage(bp) - vbn, 3);
    }
}
