using System;
using Manatee.Core;

namespace Manatee.Core.Tests.Netlist;

/// <summary>
/// RemoveNode vs the references _nDegree cannot see (2026-07-06 final-wave fix,
/// api.md §20): a COUPLER PORT on the removed node aborts the whole batch (the port
/// would otherwise alias the freed slot's next occupant and corrupt the islanding
/// union-find); a PROBE aim is invalidated at commit to a dangling endpoint that
/// reads 0 until re-aimed — never the slot's next occupant.
/// </summary>
public sealed class RemoveNodeReferenceTests
{
    private static ExternalKey K(ulong id) => new(id);

    private static Core.Netlist Net()
        => new(new NetlistOptions
        {
            Profile = SolverProfile.Dc(0.5),
            Wiring = WiringPolicy.ExplicitOnly(),
            Debug = DebugLevel.Asserts,
        });

    [Fact]
    public void Removing_a_probed_node_leaves_the_probe_reading_zero_not_the_slots_next_occupant()
    {
        var net = Net();
        NodeId iso; ProbeId probe;
        using (var e = net.Edit())
        {
            iso = e.AddNode(K(1));            // isolated (degree 0) — legal to remove
            probe = e.AddProbe(iso, K(2));
        }
        using (var e = net.Edit()) e.RemoveNode(iso);

        // Reuse the freed slot: a new node pinned at 5 V by a source.
        NodeId fresh, g;
        using (var e = net.Edit())
        {
            fresh = e.AddNode(K(3));          // takes the freed slot (LIFO free list)
            g = e.AddReferenceNode(K(4));
            e.AddVoltageSource(fresh, g, 5.0, K(5));
        }
        net.SolveOperatingPoint();
        Assert.Equal(iso.Slot, fresh.Slot);   // the alias hazard is real: same slot
        Assert.True(net.Solution.Voltage(fresh) > 4.9, "rig sanity: new occupant at ~5 V");

        Assert.Equal(0.0, net.Solution.Read(probe));   // dangling aim reads 0, never 5 V
    }

    [Fact]
    public void Removing_a_coupler_port_node_aborts_the_whole_batch()
    {
        var net = Net();
        NodeId aPos, aGnd, bPos, bGnd;
        using (var e = net.Edit())
        {
            aPos = e.AddNode(K(1)); aGnd = e.AddReferenceNode(K(2));
            bPos = e.AddNode(K(3)); bGnd = e.AddReferenceNode(K(4));
            e.AddCoupler(CouplerSpec.DecouplingTransformer(new TransformerParams(1.0), 0.5),
                new CouplerPorts(aPos, aGnd, bPos, bGnd), K(10), StateKey.From(K(10)));
        }

        // All four ports are degree 0 (coupler ports carry no _nDegree), so the old
        // degree-only validation would have freed the node and armed the alias.
        var e2 = net.Edit();
        e2.RemoveNode(bPos);
        Assert.Throws<InvalidOperationException>(() => e2.Commit());

        // The batch aborted atomically: the node is still live and resolvable.
        Assert.True(net.TryResolveNode(K(3), out var still));
        Assert.Equal(bPos, still);
    }

    [Fact]
    public void Removing_the_coupler_and_its_port_node_in_one_batch_is_legal()
    {
        var net = Net();
        NodeId aPos, aGnd, bPos, bGnd; CouplerId c;
        using (var e = net.Edit())
        {
            aPos = e.AddNode(K(1)); aGnd = e.AddReferenceNode(K(2));
            bPos = e.AddNode(K(3)); bGnd = e.AddReferenceNode(K(4));
            c = e.AddCoupler(CouplerSpec.DecouplingTransformer(new TransformerParams(1.0), 0.5),
                new CouplerPorts(aPos, aGnd, bPos, bGnd), K(10), StateKey.From(K(10)));
        }
        using (var e = net.Edit())
        {
            e.RemoveCoupler(c);
            e.RemoveNode(bPos);
        }
        Assert.False(net.TryResolveNode(K(3), out _));
    }
}
