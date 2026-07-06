using System;
using Manatee.Core;
using Manatee.Core.Reduction;

namespace Manatee.Benchmarks;

/// <summary>
/// Shared circuit builders for the standing benchmark suites (testing-strategy.md
/// Benchmarks). The suites measure the change-cost tiers the API promises (api.md §4,
/// §9): a Drive-only tick (tier 1, no refactor), a switch toggle (tier 2, one
/// refactor), a bulk topology build (tier 3 + compaction), a subcycled AC island, and
/// a snapshot round-trip. Each fixture is deterministic so the numbers are comparable
/// across commits (scripts/bench.sh diffs @ against @-).
/// </summary>
internal static class BenchFixtures
{
    internal static ExternalKey K(ulong id) => new(id);

    internal static NetlistOptions Dc(double dt) => new()
    {
        Profile = SolverProfile.Dc(dt),
        Wiring = WiringPolicy.ExplicitOnly(),
        Partitioning = PartitioningMode.SelfPartitioned,
    };

    internal static NetlistOptions Mixed(double dt) => new()
    {
        Profile = SolverProfile.Mixed(dt),
        Wiring = WiringPolicy.ExplicitOnly(),
        Partitioning = PartitioningMode.SelfPartitioned,
    };

    /// <summary>A resistive ladder: a source at node A0, then N rungs of
    /// series-R + shunt-R to the reference. A well-conditioned SPD system whose
    /// tier-1 (Drive-then-Solve) tick freezes the pivot after warmup. Returns the
    /// source so the caller can Drive it.</summary>
    internal static (Core.Netlist net, VSourceId src) BuildLadder(int rungs, double dt)
    {
        var net = new Core.Netlist(Dc(dt));
        VSourceId src;
        using (var e = net.Edit())
        {
            var g = e.AddReferenceNode(K(0));
            var prev = e.AddNode(K(1));
            src = e.AddVoltageSource(prev, g, 12.0, K(1_000_000));
            for (var i = 0; i < rungs; i++)
            {
                var next = e.AddNode(K(2 + (ulong)i));
                e.AddResistor(prev, next, 10.0, K(10_000 + (ulong)i));   // series
                e.AddResistor(next, g, 1000.0, K(20_000 + (ulong)i));    // shunt
                prev = next;
            }
        }
        net.SolveOperatingPoint();
        return (net, src);
    }

    /// <summary>A ladder driven by a sine source (Mixed) — an AC island that
    /// subcycles. At 5 Hz with dt = 0.05 s and 20 samples/cycle the solver plans
    /// N ≈ 5 substeps per tick.</summary>
    internal static (Core.Netlist net, VSourceId sine) BuildAcLadder(int rungs, double dt, double freqHz)
    {
        var net = new Core.Netlist(Mixed(dt));
        VSourceId sine;
        using (var e = net.Edit())
        {
            var g = e.AddReferenceNode(K(0));
            var prev = e.AddNode(K(1));
            sine = e.AddSineSource(prev, g, new SineDrive(12.0, freqHz, 0.0), K(1_000_000), StateKey.From(K(1_000_000)));
            for (var i = 0; i < rungs; i++)
            {
                var next = e.AddNode(K(2 + (ulong)i));
                e.AddResistor(prev, next, 10.0, K(10_000 + (ulong)i));
                e.AddResistor(next, g, 1000.0, K(20_000 + (ulong)i));
                prev = next;
            }
        }
        net.SolveOperatingPoint();
        return (net, sine);
    }

    /// <summary>A single-loop circuit with a series switch: a source, a resistor,
    /// and a switch back to the reference. Toggling the switch is a tier-2 refactor
    /// (the conductance leaves/returns the matrix pattern's active set).</summary>
    internal static (Core.Netlist net, SwitchId sw) BuildSwitchLoop(double dt)
    {
        var net = new Core.Netlist(Dc(dt));
        SwitchId sw;
        using (var e = net.Edit())
        {
            var g = e.AddReferenceNode(K(0));
            var a = e.AddNode(K(1));
            var b = e.AddNode(K(2));
            e.AddVoltageSource(a, g, 12.0, K(1_000_000));
            e.AddResistor(a, b, 100.0, K(10));
            sw = e.AddSwitch(b, g, true, K(11));
        }
        net.SolveOperatingPoint();
        return (net, sw);
    }
}
