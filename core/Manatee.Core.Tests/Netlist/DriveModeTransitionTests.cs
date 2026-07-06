using System;
using Manatee.Core;

namespace Manatee.Core.Tests.Netlist;

/// <summary>
/// Drive mode transitions on a voltage source (api.md §17 "a verb call is never
/// lost"; 2026-07-06 final-wave fix). The live runtime's sine list is frozen at
/// BuildRuntime, so without care a DEMOTE (Drive(id, volts) on a sine source) is
/// overwritten every substep by the stale sine evaluation, and a PROMOTE
/// (Drive(id, SineDrive) on a plain source) never oscillates because the source is
/// not in the runtime's sine list and no rebuild is scheduled. Demote is a pure
/// tier-1 skip; promote is a one-time runtime-stale mode change that also seeds the
/// phase accumulator from the drive's PhaseRad (an already-sine re-Drive IGNORES
/// PhaseRad — phase continuity).
/// </summary>
public sealed class DriveModeTransitionTests
{
    private static ExternalKey K(ulong id) => new(id);

    private static Core.Netlist Mixed(double dt)
        => new(new NetlistOptions
        {
            Profile = SolverProfile.Mixed(dt),
            Wiring = WiringPolicy.ExplicitOnly(),
            Debug = DebugLevel.Asserts,
        });

    // Sine source across (a, g), series R into a storage cap — a transient island
    // whose runtime persists across ticks (the frozen-list failure mode's habitat).
    private static Core.Netlist StorageIslandWithSource(bool sine, out VSourceId src, out NodeId a)
    {
        var net = Mixed(0.05);
        NodeId x, g;
        using (var e = net.Edit())
        {
            a = e.AddNode(K(1)); x = e.AddNode(K(2)); g = e.AddReferenceNode(K(3));
            src = sine
                ? e.AddSineSource(a, g, new SineDrive(12.0, 5.0, 0.0), K(10), StateKey.From(K(10)))
                : e.AddVoltageSource(a, g, 5.0, K(10));
            e.AddResistor(a, x, 100.0, K(11));
            e.AddCapacitor(x, g, 1e-3, K(12), StateKey.From(K(12)));
        }
        return net;
    }

    [Fact]
    public void Demoting_a_sine_source_to_a_constant_holds_the_constant()
    {
        var net = StorageIslandWithSource(sine: true, out var src, out var a);
        for (var i = 0; i < 10; i++) net.Solve(new TickClock(i, 0.05));

        net.Drive(src, 5.0);   // demote: constant 5 V from now on
        for (var i = 10; i < 20; i++)
        {
            net.Solve(new TickClock(i, 0.05));
            var v = net.Solution.Voltage(a);
            Assert.True(Math.Abs(v - 5.0) < 1e-9,
                $"tick {i}: demoted source read {v} V (stale sine evaluation overwrote the constant)");
        }
    }

    [Fact]
    public void Promoting_a_constant_source_to_sine_oscillates()
    {
        var net = StorageIslandWithSource(sine: false, out var src, out var a);
        for (var i = 0; i < 10; i++) net.Solve(new TickClock(i, 0.05));
        Assert.True(Math.Abs(net.Solution.Voltage(a) - 5.0) < 1e-9, "constant phase sanity");

        net.Drive(src, new SineDrive(12.0, 5.0, 0.0));   // promote: 12 V, 5 Hz sine
        double min = double.PositiveInfinity, max = double.NegativeInfinity;
        for (var i = 10; i < 50; i++)
        {
            net.Solve(new TickClock(i, 0.05));
            var v = net.Solution.Voltage(a);
            if (v < min) min = v;
            if (v > max) max = v;
        }
        // 5 Hz sampled at 0.05 s tick boundaries hits ±12 V; a lost promotion stays
        // pinned at the old 5 V constant forever (the reported failure).
        Assert.True(max > 6.0, $"promoted source never swung high (max {max} V — promotion lost)");
        Assert.True(min < -6.0, $"promoted source never swung low (min {min} V — promotion lost)");
    }

    [Fact]
    public void Promotion_seeds_phase_from_the_drive_offset_but_reDrive_keeps_phase_continuous()
    {
        var net = StorageIslandWithSource(sine: false, out var src, out _);
        net.SolveOperatingPoint();

        // Promote with a phase offset: the accumulator seeds from PhaseRad.
        net.Drive(src, new SineDrive(12.0, 5.0, Math.PI / 2.0));
        Assert.True(net.TryReadSineState(src, out var phase0, out _), "promoted source must be sine");
        Assert.True(Math.Abs(phase0 - Math.PI / 2.0) < 1e-12,
            $"promotion should seed phase from PhaseRad (got {phase0})");

        for (var i = 0; i < 5; i++) net.Solve(new TickClock(i, 0.05));
        Assert.True(net.TryReadSineState(src, out var phaseRun, out _));

        // Re-Drive an already-sine source with a DIFFERENT PhaseRad: the accumulator
        // is untouched (phase-continuous; PhaseRad ignored on re-Drive).
        net.Drive(src, new SineDrive(12.0, 5.0, 3.0));
        Assert.True(net.TryReadSineState(src, out var phaseAfter, out _));
        Assert.True(phaseAfter == phaseRun,
            $"re-Drive must not reset the phase accumulator ({phaseRun} → {phaseAfter})");
    }
}
