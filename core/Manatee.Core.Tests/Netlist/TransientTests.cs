using System;
using Manatee.Core;

namespace Manatee.Core.Tests.Netlist;

/// <summary>
/// Phase-4 transient pipeline (solver.md Analyses; api.md §4, §5, §11): Backward-Euler
/// capacitor/inductor companions, the phase-continuous sine driver, AC subcycle planning
/// with hysteresis, and the tier accounting of the substep loop. Trajectories are pinned
/// against the exact BE difference equation (the ORACLE stage checks the physics against
/// ngspice); phase continuity and N hysteresis are pinned exactly through the public API.
/// </summary>
public sealed class TransientTests
{
    private static ExternalKey K(ulong id) => new(id);

    private static Core.Netlist Transient(double dt)
        => new(new NetlistOptions
        {
            Profile = SolverProfile.Transient(dt),
            Wiring = WiringPolicy.ExplicitOnly(),
            Debug = DebugLevel.Asserts,
        });

    private static Core.Netlist Mixed(double tickDt, int samples = 20)
        => new(new NetlistOptions
        {
            Profile = SolverProfile.Mixed(tickDt, samples),
            Wiring = WiringPolicy.ExplicitOnly(),
            Debug = DebugLevel.Asserts,
        });

    // ------------------------------------------------------------- BE capacitor

    [Fact]
    public void Rc_charge_follows_the_backward_euler_recurrence_exactly()
    {
        // Vs = 10 V through R = 1 kΩ into C = 1 mF to ground; cap starts at 0 (uic).
        // BE (dt = 0.1 s): G_r = 1/R = 1e-3 S, G_c = C/dt = 1e-2 S, and
        //   V_x^{n+1} = (G_r·10 + G_c·V_x^n) / (G_r + G_c).
        const double vs = 10.0, r = 1000.0, c = 1e-3, dt = 0.1;
        var net = Transient(dt);
        NodeId a, x, g; CapacitorId cap;
        using (var e = net.Edit())
        {
            a = e.AddNode(K(1)); x = e.AddNode(K(2)); g = e.AddReferenceNode(K(3));
            e.AddVoltageSource(a, g, vs, K(20));
            e.AddResistor(a, x, r, K(10));
            cap = e.AddCapacitor(x, g, c, K(11), StateKey.From(K(11)));
        }

        double gr = 1.0 / r, gc = c / dt, expected = 0.0;
        for (var n = 0; n < 30; n++)
        {
            net.Solve(new TickClock(n, dt));
            expected = (gr * vs + gc * expected) / (gr + gc);   // the BE recurrence, stepped alongside
            // Matches the closed-form BE step; the only slack is the 1e-12 S gmin leakage
            // on the high-impedance cap node (a few ×1e-9 V), far under this bound.
            Assert.True(Math.Abs(expected - net.Solution.Voltage(x)) < 1e-6,
                $"step {n}: BE recurrence {expected} vs solver {net.Solution.Voltage(x)}");
        }
        Assert.True(net.Solution.Voltage(x) > 6.0, "cap did not charge toward the source");

        // Capacitor branch current i = C·dV/dt = G_c·(V − V_prev); near steady state it → 0.
        Assert.True(Math.Abs(net.Solution.Current(cap)) < 1e-3, "cap current should be near zero at settle");
    }

    // -------------------------------------------------------------- BE inductor

    [Fact]
    public void Rl_current_rise_follows_the_backward_euler_mirror_exactly()
    {
        // Vs = 10 V through R = 100 Ω and L = 1 H in series to ground; I starts at 0.
        // BE (dt = 1 ms): I^{n+1} = (I^n + Vs·dt/L) / (1 + dt·R/L); steady I = Vs/R = 0.1 A.
        const double vs = 10.0, r = 100.0, l = 1.0, dt = 1e-3;
        var net = Transient(dt);
        NodeId a, x, g; InductorId ind;
        using (var e = net.Edit())
        {
            a = e.AddNode(K(1)); x = e.AddNode(K(2)); g = e.AddReferenceNode(K(3));
            e.AddVoltageSource(a, g, vs, K(20));
            e.AddResistor(a, x, r, K(10));
            ind = e.AddInductor(x, g, l, K(11), StateKey.From(K(11)));
        }

        double expected = 0.0;
        for (var n = 0; n < 40; n++)
        {
            net.Solve(new TickClock(n, dt));
            expected = (expected + vs * dt / l) / (1.0 + dt * r / l);
            Assert.Equal(expected, net.Solution.Current(ind), 8);   // state IS the inductor current (a→b)
        }
        Assert.True(net.Solution.Current(ind) > 0.05, "inductor current did not rise toward Vs/R");
    }

    // ------------------------------------------------------- companion tier accounting

    [Fact]
    public void Steady_rc_ticking_stays_in_tier_1_and_adjust_c_refactors_once()
    {
        const double c = 1e-3, dt = 0.1;
        var net = Transient(dt);
        NodeId a, x, g; CapacitorId cap;
        using (var e = net.Edit())
        {
            a = e.AddNode(K(1)); x = e.AddNode(K(2)); g = e.AddReferenceNode(K(3));
            e.AddVoltageSource(a, g, 10.0, K(20));
            e.AddResistor(a, x, 1000.0, K(10));
            cap = e.AddCapacitor(x, g, c, K(11), StateKey.From(K(11)));
        }

        net.Solve(new TickClock(0, dt));                 // warmup: build + first factorization
        Assert.True(net.LastTickStats.Refactorizations >= 1);

        for (var n = 1; n < 12; n++)
        {
            net.Solve(new TickClock(n, dt));             // constant dt ⇒ companion G constant ⇒ RHS-only
            Assert.Equal(0, net.LastTickStats.Refactorizations);
            Assert.Equal(0, net.LastTickStats.IslandRebuilds);
            Assert.True(net.LastTickStats.RhsSolves >= 1);
        }

        // A capacitance change moves the companion conductance ⇒ exactly one refactor.
        net.Adjust(cap, c * 2.0);
        net.Solve(new TickClock(99, dt));
        Assert.Equal(1, net.LastTickStats.Refactorizations);
    }

    // ------------------------------------------------------ sine phase continuity

    [Fact]
    public void Sine_phase_is_continuous_across_ticks_and_across_an_n_change()
    {
        const double tickDt = 0.05, amp = 10.0;
        var net = Mixed(tickDt);
        NodeId a, g; VSourceId src;
        using (var e = net.Edit())
        {
            a = e.AddNode(K(1)); g = e.AddReferenceNode(K(2));
            src = e.AddSineSource(a, g, new SineDrive(amp, 5.0, 0.0), K(20), StateKey.From(K(20)));
            e.AddResistor(a, g, 100.0, K(10));
        }

        // Replicate the solver's per-substep accumulation and wrap (same ops, same
        // order) ⇒ bit-exact.
        const double twoPi = 2.0 * Math.PI;
        double expPhase = 0.0;
        var omega1 = twoPi * 5.0;

        for (var tick = 0; tick < 4; tick++)
        {
            net.Solve(new TickClock(tick, tickDt));
            var n = net.Islands.Of(a).Plan.Substeps;
            Assert.Equal(5, n);                                         // ceil(5·20·0.05) = 5
            var subDt = tickDt / n;
            Assert.True(omega1 * subDt <= twoPi / 20.0 + 1e-9);         // ≥ 20 samples/cycle: dense
            for (var k = 0; k < n; k++) { expPhase += omega1 * subDt; if (expPhase >= twoPi) expPhase -= twoPi; }

            Assert.True(net.TryReadSineState(src, out var phase, out var value));
            Assert.Equal(expPhase, phase, 12);                          // no reset: accumulator carried
            Assert.Equal(amp * Math.Sin(phase), value, 12);
        }

        var nBefore = net.Islands.Of(a).Plan.Substeps;

        // Frequency step to 6 Hz (raw rate 6 vs the reference 5 ⇒ past the ±15% band ⇒ N changes),
        // phase held continuous: tier-1 Drive keeps the accumulator.
        net.Drive(src, new SineDrive(amp, 6.0, 0.0));
        net.Solve(new TickClock(4, tickDt));
        var nAfter = net.Islands.Of(a).Plan.Substeps;
        Assert.NotEqual(nBefore, nAfter);                               // 6 substeps now

        var omega2 = twoPi * 6.0;
        var subDt2 = tickDt / nAfter;
        Assert.True(omega2 * subDt2 <= twoPi / 20.0 + 1e-9);
        for (var k = 0; k < nAfter; k++) { expPhase += omega2 * subDt2; if (expPhase >= twoPi) expPhase -= twoPi; }

        Assert.True(net.TryReadSineState(src, out var phase2, out var value2));
        Assert.Equal(expPhase, phase2, 12);                            // continued from prior phase, no jump
        Assert.Equal(amp * Math.Sin(phase2), value2, 12);
    }

    [Fact]
    public void Operating_point_puts_sine_sources_at_their_dc_offset()
    {
        // §5 lesson-start convention: SolveOperatingPoint drives a sine source to 0,
        // even after transient stepping has advanced its phase and value.
        const double tickDt = 0.05;
        var net = Mixed(tickDt);
        NodeId a, g; VSourceId src;
        using (var e = net.Edit())
        {
            a = e.AddNode(K(1)); g = e.AddReferenceNode(K(2));
            src = e.AddSineSource(a, g, new SineDrive(10.0, 5.0, 0.0), K(20), StateKey.From(K(20)));
            e.AddResistor(a, g, 100.0, K(10));
        }

        // One tick (5 substeps, quarter cycle) lands the phase at π/2 ⇒ V(a) = 10·sin(π/2) = 10.
        net.Solve(new TickClock(0, tickDt));
        Assert.True(Math.Abs(net.Solution.Voltage(a)) > 1e-6);            // sine is nonzero mid-run

        net.SolveOperatingPoint();
        Assert.Equal(0.0, net.Solution.Voltage(a), 9);                    // pinned to the DC offset
    }

    [Fact]
    public void Operating_point_between_transient_ticks_does_not_collapse_N()
    {
        // Regression: SolveOperatingPoint is the documented lesson-start / snapshot verb,
        // so a transient AC island can be stepped, op-pointed, then stepped again. The
        // op-point pass forces N=1; it must invalidate the hysteresis anchor so the
        // resumed transient re-plans the physical N. Previously the stale anchor made
        // PlanSubstepN see drift≈1.0 and keep N=1 — 1 sample/tick, undersampling the sine.
        const double tickDt = 0.05, amp = 10.0;
        var net = Mixed(tickDt);
        NodeId a, g; VSourceId src;
        using (var e = net.Edit())
        {
            a = e.AddNode(K(1)); g = e.AddReferenceNode(K(2));
            src = e.AddSineSource(a, g, new SineDrive(amp, 5.0, 0.0), K(20), StateKey.From(K(20)));
            e.AddResistor(a, g, 100.0, K(10));
        }

        net.Solve(new TickClock(0, tickDt));
        Assert.Equal(5, net.Islands.Of(a).Plan.Substeps);                 // ceil(5·20·0.05) = 5

        net.SolveOperatingPoint();                                        // op-point forces N=1
        Assert.Equal(1, net.Islands.Of(a).Plan.Substeps);

        net.Solve(new TickClock(1, tickDt));                              // resume transient
        Assert.Equal(5, net.Islands.Of(a).Plan.Substeps);                 // N re-planned, not stuck at 1
        Assert.Equal(5, net.LastTickStats.Substeps);                      // 5 samples this tick, not 1
    }

    [Fact]
    public void Sine_phase_stays_wrapped_when_the_per_step_increment_exceeds_two_pi()
    {
        // Determinism invariant (the phase accumulator must not drift): the wrap must be
        // idempotent for ANY increment. A sine source in a non-Mixed (Transient) profile
        // solves with a single substep of dt = tickDt, so its per-tick phase increment is
        // ω·tickDt = 2π·25·0.05 ≈ 7.85 rad > 2π. A single conditional subtraction would
        // leave the accumulator above 2π and let it grow without bound; the while-loop
        // keeps it in [0, 2π) every tick so sin(phase) never loses precision.
        const double tickDt = 0.05;
        var net = Transient(tickDt);
        NodeId a, g; VSourceId src;
        using (var e = net.Edit())
        {
            a = e.AddNode(K(1)); g = e.AddReferenceNode(K(2));
            src = e.AddSineSource(a, g, new SineDrive(10.0, 25.0, 0.0), K(20), StateKey.From(K(20)));
            e.AddResistor(a, g, 100.0, K(10));
        }

        const double twoPi = 2.0 * Math.PI;
        for (var tick = 0; tick < 200; tick++)
        {
            net.Solve(new TickClock(tick, tickDt));
            Assert.True(net.TryReadSineState(src, out var phase, out _));
            Assert.InRange(phase, 0.0, twoPi);                        // never drifts past one turn
        }
    }

    // ---------------------------------------------------------------- N hysteresis

    [Fact]
    public void N_holds_under_small_wobble_and_changes_once_under_a_sustained_step()
    {
        const double tickDt = 0.05, amp = 10.0;
        var net = Mixed(tickDt);
        NodeId a, g; VSourceId src;
        using (var e = net.Edit())
        {
            a = e.AddNode(K(1)); g = e.AddReferenceNode(K(2));
            src = e.AddSineSource(a, g, new SineDrive(amp, 5.0, 0.0), K(20), StateKey.From(K(20)));
            e.AddResistor(a, g, 100.0, K(10));
        }

        net.Solve(new TickClock(0, tickDt));
        var n0 = net.Islands.Of(a).Plan.Substeps;                      // N chosen for 5 Hz (raw rate 5.0)
        Assert.Equal(5, n0);

        // ±10 % wobble around 5 Hz: raw rate stays inside the ±15 % band ⇒ N unchanged.
        foreach (var f in new[] { 5.5, 4.5, 5.4, 4.6 })
        {
            net.Drive(src, new SineDrive(amp, f, 0.0));
            net.Solve(new TickClock(1, tickDt));
            Assert.Equal(n0, net.Islands.Of(a).Plan.Substeps);
            Assert.Equal(0, net.LastTickStats.Refactorizations);       // dt unchanged ⇒ no refactor
        }

        // +20 % sustained (6 Hz): past the band ⇒ N changes exactly once, then holds.
        net.Drive(src, new SineDrive(amp, 6.0, 0.0));
        net.Solve(new TickClock(2, tickDt));
        var n1 = net.Islands.Of(a).Plan.Substeps;
        Assert.Equal(6, n1);

        net.Drive(src, new SineDrive(amp, 6.0, 0.0));
        net.Solve(new TickClock(3, tickDt));
        Assert.Equal(n1, net.Islands.Of(a).Plan.Substeps);            // stays put (drift back to ~1.0)
    }

}

/// <summary>The zero-alloc AC-loop gate, in the serialized ZeroAlloc collection —
/// see <see cref="ZeroAllocCollection"/> for the root cause (a concurrent compacting
/// GC makes the per-thread counter over-report by an allocation quantum).</summary>
[Collection(ZeroAllocCollection.Name)]
public sealed class TransientZeroAllocTests
{
    private static ExternalKey K(ulong id) => new(id);

    [Fact]
    public void Ac_steady_substep_loop_allocates_nothing()
    {
        if (!ZeroAllocGates.CounterIsReliable())
            return;   // GC counter inert on this runtime (historically Mono) — best-effort skip (api.md §8)

        const double tickDt = 0.05;
        var net = new Core.Netlist(new NetlistOptions
        {
            Profile = SolverProfile.Mixed(tickDt, 20),
            Wiring = WiringPolicy.ExplicitOnly(),
            Debug = DebugLevel.Asserts,
        });
        NodeId a, g;
        using (var e = net.Edit())
        {
            a = e.AddNode(K(1)); g = e.AddReferenceNode(K(2));
            e.AddSineSource(a, g, new SineDrive(10.0, 5.0, 0.0), K(20), StateKey.From(K(20)));
            e.AddResistor(a, g, 100.0, K(10));
            e.AddCapacitor(a, g, 1e-4, K(11), StateKey.From(K(11)));
        }

        for (var i = 0; i < 6; i++) net.Solve(new TickClock(i, tickDt));   // warmup: build, first factor, id cache
        _ = net.LastTickStats;                                             // seal the tick

        // Min over sub-runs (see ZeroAllocCollection): background GC / tiered JIT can
        // add phantom bytes; the perturbation is additive, so min == 0 is the proof.
        long best = long.MaxValue;
        var tick = 100L;
        for (var run = 0; run < 8 && best != 0; run++)
        {
            var before = GC.GetAllocatedBytesForCurrentThread();
            for (var i = 0; i < 50; i++) net.Solve(new TickClock(tick++, tickDt));
            var d = GC.GetAllocatedBytesForCurrentThread() - before;
            if (d < best) best = d;
        }

        Assert.True(best == 0, $"steady AC substep loop allocated {best} B over 50 ticks (min over runs; expected 0)");
    }
}

/// <summary>Shared helper for the GC-counter gates.</summary>
internal static class ZeroAllocGates
{
    internal static bool CounterIsReliable()
    {
        var before = GC.GetAllocatedBytesForCurrentThread();
        var probe = new byte[4096];
        GC.KeepAlive(probe);
        return GC.GetAllocatedBytesForCurrentThread() - before > 0;
    }
}
