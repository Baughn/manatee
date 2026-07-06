using System;
using Manatee.Core;

namespace Manatee.Core.Tests.Netlist;

/// <summary>
/// Phase-3 numeric pipeline (api.md §4, §9, §10, §16, §17, §20; solver.md DC): the
/// document wired to the internal Circuit end-to-end. Hand-computed DC solves
/// through the PUBLIC API, wiring policies verified by solve results, the
/// Reconfigure-to-Solve story, tier-budget prototypes, the Faulted flow, and
/// per-island Step.
/// </summary>
public sealed class NetlistNumericTests
{
    private static ExternalKey K(ulong id) => new(id);

    private static Core.Netlist Net(WiringPolicy wiring, PartitioningMode mode = PartitioningMode.SelfPartitioned)
        => new(new NetlistOptions
        {
            Profile = SolverProfile.Dc(0.5),
            Wiring = wiring,
            Partitioning = mode,
            Debug = DebugLevel.Asserts,
        });

    // ------------------------------------------------------- end-to-end DC solve

    [Fact]
    public void Voltage_divider_through_public_api_matches_hand_math()
    {
        // A —[R1=1k]— B —[R2=2k]— GND ; 10 V source A→GND.
        // V(B) = 10·2000/3000 = 6.6667 V ; I(R1)=(10−6.6667)/1000 = 3.3333 mA ;
        // source branch current = −3.3333 mA (leaves + node).
        var net = Net(WiringPolicy.ExplicitOnly());
        NodeId a, b, g; VSourceId src; ResistorId r1, r2;
        using (var e = net.Edit())
        {
            a = e.AddNode(K(1)); b = e.AddNode(K(2)); g = e.AddReferenceNode(K(3));
            src = e.AddVoltageSource(a, g, 10.0, K(20));
            r1 = e.AddResistor(a, b, 1000.0, K(10));
            r2 = e.AddResistor(b, g, 2000.0, K(11));
        }
        net.SolveOperatingPoint();

        var sol = net.Solution;
        Assert.True(sol.IsLive(net.IslandOf(a)));
        Assert.Equal(10.0, sol.Voltage(a), 6);
        Assert.Equal(20.0 / 3.0, sol.Voltage(b), 6);
        Assert.Equal(0.0, sol.Voltage(g), 9);                   // reference reads exactly 0
        Assert.Equal((10.0 - 20.0 / 3.0) / 1000.0, sol.Current(r1), 9);
        Assert.Equal(-1.0 / 300.0, sol.Current(src), 9);
        // Power absorbed by R2 = I²R = (10/3000)²·2000.
        var i = (10.0 - 20.0 / 3.0) / 1000.0;
        Assert.Equal(i * i * 2000.0, sol.Power(r2), 9);
    }

    [Fact]
    public void Drive_updates_rhs_and_republishes_without_refactor()
    {
        var net = Net(WiringPolicy.ExplicitOnly());
        NodeId a, b, g; VSourceId src; ResistorId r1, r2;
        using (var e = net.Edit())
        {
            a = e.AddNode(K(1)); b = e.AddNode(K(2)); g = e.AddReferenceNode(K(3));
            src = e.AddVoltageSource(a, g, 10.0, K(20));
            r1 = e.AddResistor(a, b, 1000.0, K(10));
            r2 = e.AddResistor(b, g, 2000.0, K(11));
        }
        net.SolveOperatingPoint();
        Assert.Equal(20.0 / 3.0, net.Solution.Voltage(b), 6);

        net.Drive(src, 30.0);                       // tier-1 RHS write
        net.Solve(new TickClock(1, 0.5));
        Assert.Equal(30.0 * 2.0 / 3.0, net.Solution.Voltage(b), 6);
        var s = net.LastTickStats;
        Assert.Equal(0, s.Refactorizations);        // RHS-only ⇒ no numeric refactor
        Assert.Equal(0, s.IslandRebuilds);
        Assert.True(s.RhsSolves >= 1);
    }

    // --------------------------------------------------------- wiring policies

    [Fact]
    public void TwoWireLeak_stamps_exactly_one_leak_per_return_node()
    {
        // pos —[R=100]— return ; 10 V pos→earth. Return leaks to earth via 1 MΩ.
        // One leak ⇒ V(return) = 10·1e6/(1e6+100) = 9.99900 V. Two leaks (bug) ⇒ 9.99800.
        var net = Net(WiringPolicy.TwoWireLeak(1e6));
        NodeId earth, pos, ret; ResistorId load;
        using (var e = net.Edit())
        {
            earth = e.AddReferenceNode(K(1));
            pos = e.AddNode(K(2));
            ret = e.AddNode(K(3), NodeRole.Return);
            e.AddVoltageSource(pos, earth, 10.0, K(20));
            load = e.AddResistor(pos, ret, 100.0, K(10));
        }
        net.SolveOperatingPoint();

        Assert.Equal(10.0 * 1e6 / (1e6 + 100.0), net.Solution.Voltage(ret), 4);
        Assert.Equal(10.0 / (100.0 + 1e6), net.Solution.Current(load), 9);
    }

    [Fact]
    public void ReferenceBound_binds_return_to_reference()
    {
        // Return bound to reference through 1 mΩ ⇒ V(return) ≈ 0 (pinned to datum).
        var net = Net(WiringPolicy.ReferenceBound(1e3));
        NodeId earth, pos, ret;
        using (var e = net.Edit())
        {
            earth = e.AddReferenceNode(K(1));
            pos = e.AddNode(K(2));
            ret = e.AddNode(K(3), NodeRole.Return);
            e.AddVoltageSource(pos, earth, 10.0, K(20));
            e.AddResistor(pos, ret, 100.0, K(10));
        }
        net.SolveOperatingPoint();

        // 10 V across (100 Ω + 1 mΩ): the return node sits at ~1e-3/100 · 10 ≈ 1e-4 V.
        Assert.True(net.Solution.Voltage(ret) < 1e-3, $"return not bound: {net.Solution.Voltage(ret)}");
        Assert.True(net.Solution.Voltage(ret) >= 0.0);
    }

    [Fact]
    public void ExplicitOnly_floats_but_stays_finite_via_gmin()
    {
        // No reference, no ground path: a 1 A source across a resistor. gmin anchors
        // the otherwise-floating subgraph so the solve is finite and non-faulted.
        var net = Net(WiringPolicy.ExplicitOnly());
        NodeId a, b;
        using (var e = net.Edit())
        {
            a = e.AddNode(K(1)); b = e.AddNode(K(2));
            e.AddResistor(a, b, 100.0, K(10));
            e.AddCurrentSource(a, b, 0.001, K(20));
        }
        net.SolveOperatingPoint();

        var isl = net.IslandOf(a);
        Assert.True(net.Solution.IsLive(isl));
        var inv = net.Islands.Of(a).CheckInvariants(InvariantChecks.Kcl | InvariantChecks.Finiteness);
        Assert.True(inv.AllFinite);
        Assert.True(inv.MaxKclResidual < 1e-6);
    }

    // ------------------------------------------------------------- tier budget

    [Fact]
    public void Epsilon_no_op_adjust_loop_keeps_refactorizations_zero()
    {
        var net = Net(WiringPolicy.ExplicitOnly());
        NodeId a, b, g; ResistorId r1;
        using (var e = net.Edit())
        {
            a = e.AddNode(K(1)); b = e.AddNode(K(2)); g = e.AddReferenceNode(K(3));
            e.AddVoltageSource(a, g, 10.0, K(20));
            r1 = e.AddResistor(a, b, 1000.0, K(10));
            e.AddResistor(b, g, 2000.0, K(11));
        }
        net.SolveOperatingPoint();                  // warmup: first factorization

        for (var k = 0; k < 16; k++)
        {
            net.Adjust(r1, 1000.0 * (1.0 + 1e-9));  // within ε ⇒ tier-0 document write
            net.Solve(new TickClock(k, 0.5));
        }
        var s = net.LastTickStats;
        Assert.Equal(0, s.Refactorizations);        // the standing tier-budget assertion
        Assert.Equal(0, s.IslandRebuilds);

        // Control: a real conductance change DOES refactor.
        net.Adjust(r1, 500.0);
        net.Solve(new TickClock(99, 0.5));
        Assert.Equal(1, net.LastTickStats.Refactorizations);
    }

    // ----------------------------------------------------------- faulted flow

    [Fact]
    public void Contradictory_sources_fault_then_recover_on_removal()
    {
        // Two ideal sources A→GND disagree (10 V vs 5 V): parallel ideal V-sources on the
        // same node pair ⇒ the singularity is diagnosed as ContradictorySources, naming BOTH
        // participating sources (api.md §12/§20; solver.md Failure Handling).
        var net = Net(WiringPolicy.ExplicitOnly());
        NodeId a, g; VSourceId s1, s2;
        using (var e = net.Edit())
        {
            a = e.AddNode(K(1)); g = e.AddReferenceNode(K(2));
            s1 = e.AddVoltageSource(a, g, 10.0, K(20));
            s2 = e.AddVoltageSource(a, g, 5.0, K(21));
        }
        net.SolveOperatingPoint();

        var islId = net.IslandOf(a);
        var isl = net.Islands.Of(a);
        Assert.Equal(IslandStatus.Faulted, isl.Status);
        Assert.False(net.Solution.IsLive(islId));
        Assert.Equal(FaultKind.ContradictorySources, isl.Fault.Kind);
        Span<ComponentRef> comps = stackalloc ComponentRef[4];
        Span<NodeId> nodes = stackalloc NodeId[4];
        var packed = isl.DescribeFault(comps, nodes);
        Assert.Equal(2, packed);   // both fighting sources are named
        Assert.True((comps[0] == s1.AsRef() && comps[1] == s2.AsRef())
                 || (comps[0] == s2.AsRef() && comps[1] == s1.AsRef()),
            "ContradictorySources must name both participating voltage sources");

        // Journal recorded the fault.
        var cur = net.Journal.OpenCursorAt(0);
        var sawFault = false;
        while (net.Journal.TryRead(ref cur, out var ev))
            if (ev.Kind == TopologyEventKind.IslandFaulted) sawFault = true;
        Assert.True(sawFault);

        // Recover via a tier-3 change: drop the contradictory source.
        using (var e = net.Edit()) e.Remove(s2);
        net.SolveOperatingPoint();
        Assert.True(net.TryResolveNode(K(1), out a));
        var live = net.IslandOf(a);
        Assert.True(net.Solution.IsLive(live));
        Assert.Equal(10.0, net.Solution.Voltage(a), 6);
    }

    // --------------------------------------------------------------- per-island Step

    [Fact]
    public void IslandHandle_Step_solves_one_dc_unit()
    {
        var net = Net(WiringPolicy.ExplicitOnly());
        NodeId a, b, g; VSourceId src;
        using (var e = net.Edit())
        {
            a = e.AddNode(K(1)); b = e.AddNode(K(2)); g = e.AddReferenceNode(K(3));
            src = e.AddVoltageSource(a, g, 10.0, K(20));
            e.AddResistor(a, b, 1000.0, K(10));
            e.AddResistor(b, g, 2000.0, K(11));
        }
        net.SolveOperatingPoint();

        net.Drive(src, 30.0);                       // dirties the island
        net.Islands.Of(a).Step(new TickClock(1, 0.5));   // solve THIS unit
        Assert.Equal(30.0 * 2.0 / 3.0, net.Solution.Voltage(b), 6);
        var s = net.LastTickStats;
        Assert.True(s.RhsSolves >= 1);
        Assert.Equal(0, s.Refactorizations);
    }

    [Fact]
    public void Step_on_ac_island_runs_n_substeps()
    {
        // 5 Hz sine in a Mixed island at 20 samples/cycle, tick dt 0.05 s ⇒
        // N = ceil(5·20·0.05) = 5 substeps per Step (solver-owned N; api.md §11).
        var net = new Core.Netlist(new NetlistOptions
        {
            Profile = SolverProfile.Mixed(0.05),
            Wiring = WiringPolicy.ExplicitOnly(),
            Debug = DebugLevel.Asserts,
        });
        NodeId a, g;
        using (var e = net.Edit())
        {
            a = e.AddNode(K(1)); g = e.AddReferenceNode(K(2));
            e.AddSineSource(a, g, new SineDrive(12.0, 5.0, 0.0), K(20), StateKey.From(K(20)));
            e.AddResistor(a, g, 100.0, K(10));
        }
        net.Islands.Of(a).Step(new TickClock(0, 0.05));

        Assert.Equal(5, net.Islands.Of(a).Plan.Substeps);
        Assert.Equal(5, net.LastTickStats.Substeps);
    }

    // ------------------------------------------------- Reconfigure-to-Solve story (§17)

    [Fact]
    public void Doomed_window_structure_immediate_numbers_last_published_writes_survive()
    {
        var net = Net(WiringPolicy.ExplicitOnly());
        NodeId a, b, g; VSourceId src; ResistorId r1;
        using (var e = net.Edit())
        {
            a = e.AddNode(K(1)); b = e.AddNode(K(2)); g = e.AddReferenceNode(K(3));
            src = e.AddVoltageSource(a, g, 10.0, K(20));
            r1 = e.AddResistor(a, b, 1000.0, K(10));
            e.AddResistor(b, g, 2000.0, K(11));
            e.AddResistor(a, b, 1e9, K(99));         // negligible parallel dummy, removed below
        }
        net.SolveOperatingPoint();
        var oldIsl = net.IslandOf(b);
        Assert.True(net.Solution.IsLive(oldIsl));
        Assert.Equal(20.0 / 3.0, net.Solution.Voltage(b), 3);

        var dummy = Resolve(net, K(99));
        using (var e = net.Edit()) e.Remove(dummy);  // schedules a rebuild

        // Structure is immediate: the removed component is already gone.
        Assert.False(net.TryResolve(K(99), out _));
        // Numbers are last-published; the island is not live (Dirty).
        Assert.Equal(20.0 / 3.0, net.Solution.Voltage(b), 3);
        Assert.False(net.Solution.IsLive(oldIsl));

        // Writes in the doomed window (surviving handles) are NOT lost: the rebuild
        // restamps from the document.
        net.Drive(src, 20.0);
        net.Adjust(r1, 2000.0);
        net.SolveOperatingPoint();

        Assert.True(net.TryResolveNode(K(2), out b));    // re-resolve after rebuild
        Assert.Equal(20.0 * 2000.0 / 4000.0, net.Solution.Voltage(b), 3);   // = 10.0
    }

    [Fact]
    public void Deconstruction_burst_is_exactly_one_rebuild()
    {
        var net = Net(WiringPolicy.ExplicitOnly());
        NodeId a, b;
        using (var e = net.Edit())
        {
            a = e.AddNode(K(1)); b = e.AddNode(K(2));
            e.AddResistor(a, b, 100.0, K(10));       // keeps a–b connected through the burst
            e.AddResistor(a, b, 100.0, K(11));
            e.AddResistor(a, b, 100.0, K(12));
            e.AddResistor(a, b, 100.0, K(13));
        }
        net.SolveOperatingPoint();

        using (var e = net.Edit())
        {
            e.Remove(Resolve(net, K(11)));
            e.Remove(Resolve(net, K(12)));
            e.Remove(Resolve(net, K(13)));
        }
        net.SolveOperatingPoint();
        Assert.Equal(1, net.LastTickStats.IslandRebuilds);
    }

    [Fact]
    public void Rebuild_supersedes_merge_but_the_merge_event_still_fires()
    {
        var net = Net(WiringPolicy.ExplicitOnly());
        NodeId a, b, c, d;
        using (var e = net.Edit())
        {
            a = e.AddNode(K(1)); b = e.AddNode(K(2)); c = e.AddNode(K(3)); d = e.AddNode(K(4));
            e.AddResistor(a, b, 100.0, K(10));       // island X = {a,b}
            e.AddResistor(a, b, 100.0, K(11));       // removable, keeps X connected via K(10)
            e.AddResistor(c, d, 100.0, K(12));       // island Y = {c,d}
        }
        net.SolveOperatingPoint();                   // seals the tick (isolates the counters below)
        Assert.Equal(2, net.Islands.Count);

        using (var e = net.Edit())
        {
            e.AddResistor(b, c, 100.0, K(20));       // MERGE X∪Y
            e.Remove(Resolve(net, K(11)));           // and schedule a REBUILD of the survivor
        }
        net.SolveOperatingPoint();

        var s = net.LastTickStats;
        Assert.Equal(1, s.IslandRebuilds);           // one rebuild, not merge-then-rebuild
        Assert.Equal(1, s.MergesApplied);

        // Coalescing discards matrix work only, never events: the Merged terminal
        // event is still observable.
        Span<IslandChange> buf = stackalloc IslandChange[32];
        var n = net.Islands.DrainChanges(buf, out _);
        var sawMerged = false;
        for (var i = 0; i < n; i++) if (buf[i].Kind == IslandChangeKind.Merged) sawMerged = true;
        Assert.True(sawMerged);
        Assert.Equal(1, net.Islands.Count);
    }

    // ---------------------------------------------------- guard defers Reconfigure

    [Fact]
    public void Release_guard_defers_reconfigure_to_dispose_before_solve()
    {
        // Release build (Debug.Off): a Reconfigure inside the steady-state guard is
        // deferred to the guard's Dispose (same tick, before Solve) and counted,
        // never dropped (api.md §8, Decision log #5).
        var net = new Core.Netlist(new NetlistOptions
        {
            Profile = SolverProfile.Dc(0.5),
            Wiring = WiringPolicy.ExplicitOnly(),
            Debug = DebugLevel.Off,
        });
        NodeId ap, an, bp, bn; CouplerId br;
        using (var e = net.Edit())
        {
            ap = e.AddNode(K(1)); an = e.AddNode(K(2)); bp = e.AddNode(K(3)); bn = e.AddNode(K(4));
            e.AddResistor(ap, an, 100.0, K(10));
            e.AddResistor(bp, bn, 100.0, K(11));
            br = e.AddCoupler(CouplerSpec.Breaker(), new CouplerPorts(ap, an, bp, bn), K(20), StateKey.From(K(20)));
        }
        net.SolveOperatingPoint();
        Assert.Equal(1, net.Islands.Count);          // closed breaker ⇒ merged

        int countInside;
        using (net.EnterSteadyState())
        {
            net.Reconfigure(br, CouplerState.Open);  // barred ⇒ deferred, not applied yet
            countInside = net.Islands.Count;         // capture (asserting here would allocate under the sentinel)
        }
        Assert.Equal(1, countInside);                // still merged inside the region
        // Deferred op ran at Dispose (before any Solve) and was counted.
        Assert.Equal(1, net.LastTickStats.DeferredStructuralOps);

        net.SolveOperatingPoint();                   // the scheduled split rebuild lands
        Assert.Equal(2, net.Islands.Count);
    }

    private static ResistorId Resolve(Core.Netlist net, ExternalKey key)
    {
        Assert.True(net.TryResolve(key, out var c));
        return new ResistorId(c.Slot, c.Gen, c.Net);
    }
}
