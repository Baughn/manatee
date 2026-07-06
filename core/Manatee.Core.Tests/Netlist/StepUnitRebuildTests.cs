using System;
using Manatee.Core;

namespace Manatee.Core.Tests.Netlist;

/// <summary>
/// Client-driven Step mode (api.md §22.b) structural correctness for boundary-coupled
/// scheduling units, plus stale-IslandHandle inertness (2026-07-06 final-wave fixes):
/// a pending connectivity rebuild of a NON-LEAD unit member must execute inside the
/// lead's Step (RunSolve's structural pass never runs for §22.b clients — a removed
/// component would otherwise keep conducting forever), and every IslandHandle seam is
/// gen-checked so a reissued slot is never read or stepped through a stale handle.
/// </summary>
public sealed class StepUnitRebuildTests
{
    private static ExternalKey K(ulong id) => new(id);

    private static Core.Netlist Net()
        => new(new NetlistOptions
        {
            Profile = SolverProfile.Mixed(0.05),
            Wiring = WiringPolicy.ExplicitOnly(),
            Partitioning = PartitioningMode.SelfPartitioned,
            Debug = DebugLevel.Asserts,
        });

    [Fact]
    public void Step_driven_unit_executes_a_non_lead_members_pending_rebuild()
    {
        // Island A (min keys ⇒ the unit lead): stiff 10 V source. Island B: two 10 Ω
        // resistors in parallel (5 Ω ⇒ 20 W at 10 V), joined by a 1:1 transformer.
        var net = Net();
        NodeId aPos, aGnd, bPos, bGnd; ResistorId r2; CouplerId c;
        using (var e = net.Edit())
        {
            aPos = e.AddNode(K(1)); aGnd = e.AddReferenceNode(K(2));
            bPos = e.AddNode(K(5)); bGnd = e.AddReferenceNode(K(6));
            e.AddVoltageSource(aPos, aGnd, 10.0, K(10));
            e.AddResistor(bPos, bGnd, 10.0, K(20));
            r2 = e.AddResistor(bPos, bGnd, 10.0, K(21));
            c = e.AddCoupler(CouplerSpec.DecouplingTransformer(new TransformerParams(1.0), 0.5),
                new CouplerPorts(aPos, aGnd, bPos, bGnd), K(30), StateKey.From(K(30)));
        }
        net.SolveOperatingPoint();
        net.Reconfigure(c, CouplerState.Closed);

        // Client-driven schedule: Step the unit lead each tick (island A holds the
        // min node key, so it is the lead; Step on a lead is legal every tick).
        void StepLead(long tick)
        {
            var dirty = new IslandHandle[8];
            var n = net.Islands.CollectDirty(dirty);
            for (var i = 0; i < n; i++) dirty[i].Step(new TickClock(tick, 0.05));
            // A settled active unit may collect empty; keep it stepping like a real
            // §22.b client that steps its live units every tick.
            if (n == 0) net.Islands.Of(aPos).Step(new TickClock(tick, 0.05));
        }

        for (var t = 0; t < 60; t++) StepLead(t);
        var pBefore = net.Islands.Of(aPos).Exchange(c).PowerA2B;
        Assert.True(Math.Abs(pBefore - 20.0) < 1.0, $"settle sanity: expected ~20 W, got {pBefore}");

        // Remove one parallel resistor in the NON-LEAD island. This schedules a
        // connectivity rebuild for island B that only Step(lead) can execute here.
        using (var e = net.Edit()) e.Remove(r2);

        var sawRebuilt = false;
        var changes = new IslandChange[16];
        for (var t = 60; t < 120; t++)
        {
            StepLead(t);
            var m = net.Islands.DrainChanges(changes, out _);
            for (var i = 0; i < m; i++)
                if (changes[i].Kind == IslandChangeKind.Rebuilt) sawRebuilt = true;
        }

        var pAfter = net.Islands.Of(aPos).Exchange(c).PowerA2B;
        Assert.True(Math.Abs(pAfter - 10.0) < 1.0,
            $"removed resistor still conducting through the boundary: {pAfter} W (want ~10 W)");
        Assert.True(sawRebuilt, "no IslandRebuilt change was emitted for the non-lead member (§15 completeness)");
    }

    [Fact]
    public void Stale_island_handle_is_inert_on_every_seam()
    {
        var net = Net();
        NodeId a, g; ResistorId r;
        using (var e = net.Edit())
        {
            a = e.AddNode(K(1)); g = e.AddReferenceNode(K(2));
            e.AddVoltageSource(a, g, 5.0, K(10));
            r = e.AddResistor(a, g, 10.0, K(11));
        }
        net.SolveOperatingPoint();
        var stale = net.Islands.Of(a);
        Assert.True(stale.IsValid);

        // Force a rebuild: remove a component, solve — the island's gen is reissued.
        using (var e = net.Edit()) e.Remove(r);
        net.Solve(new TickClock(1, 0.05));

        Assert.False(stale.IsValid, "handle should be invalidated by the rebuild");
        // Every seam reads the defined sentinel / no-ops — never the slot's occupant.
        Assert.Equal(default, stale.Fault);
        var plan = stale.Plan;
        Assert.Equal(1, plan.Substeps);
        var rep = stale.CheckInvariants(InvariantChecks.All);
        Assert.True(rep.AllFinite && rep.MaxKclResidual == 0.0);
        Assert.Equal(0, stale.DescribeFault(new ComponentRef[2], new NodeId[2]));
        stale.Step(new TickClock(2, 0.05));   // must be an inert no-op, no assert/throw
        Assert.Equal(IslandStatus.Empty, stale.Status);
    }
}
