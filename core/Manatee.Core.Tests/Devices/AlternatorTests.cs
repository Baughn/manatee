using System;
using Manatee.Core;
using Manatee.Core.Devices;
using static Manatee.Core.Tests.Devices.DevicesTestKit;

namespace Manatee.Core.Tests.Devices;

/// <summary>
/// <see cref="Alternator"/>: a swing-equation-lite rotor (angle + angular velocity)
/// driving a sine source at frequency ω·polePairs, loaded by the electrical
/// counter-torque read back from the solve (api.md §18; solver.md Component Set). The
/// headline property is PARALLELING / PHASE-LOCK: two machines started out of phase on
/// one bus converge to a bounded rotor-angle difference (never strobe).
/// </summary>
public sealed class AlternatorTests
{
    [Fact]
    public void Single_machine_settles_under_governor_and_sees_counter_torque()
    {
        var net = Net();
        NodeId bus, g;
        using (var e = net.Edit())
        {
            bus = e.AddNode(K(1)); g = e.AddReferenceNode(K(2));
            e.AddResistor(bus, g, 10.0, K(11));   // electrical load
        }
        var host = new DeviceHost(net);
        var alt = new Alternator(inertia: 2.0, damping: 0.2, polePairs: 1.0, emfPerOmega: 2.0,
            seriesOhms: 2.0, governorGain: 1.0, initialOmega: 8.0);
        host.Add(alt, stackalloc NodeId[2] { bus, g }, K(20), StateKey.From(K(20)));
        net.SolveOperatingPoint();

        long tick = 0;
        double omegaMid = 0.0;
        for (var i = 0; i < 1000; i++)
        {
            alt.SetMechanical(shaftSpeed: 10.0, availableTorque: 0.0);
            Step(net, host, 0.05, ref tick);
            if (i == 700) omegaMid = alt.Omega;
        }

        // Governor pulls toward 10 rad/s; it settles at a steady speed BELOW the target
        // (balanced against the electrical + damping load), genuinely loaded (nonzero
        // counter-torque and frequency), and CONVERGED (steady between tick 700 and 1000,
        // within the small per-tick AC-sampling ripple).
        Assert.InRange(alt.Omega, 5.0, 10.0);
        Assert.True(Math.Abs(omegaMid - alt.Omega) < 0.1, $"not converged: {omegaMid:F3} → {alt.Omega:F3}");
        Assert.True(Math.Abs(alt.CounterTorque) > 1e-3, "loaded machine must see counter-torque");
        Assert.True(alt.FrequencyHz > 0.5);
    }

    [Fact]
    public void Two_paralleled_machines_phase_lock_to_a_bounded_angle_difference()
    {
        // Two alternators on one bus, started at DIFFERENT speeds AND angles. The faster
        // machine has the larger back-EMF (amplitude = k·ω), sources more current, and is
        // decelerated by more counter-torque; the slower one motors and accelerates. With
        // the governors and electrical damping the speeds converge, so Δθ = ∫Δω dt settles
        // to a bounded constant — synchronisation (documented in Alternator's summary).
        var net = Net();
        NodeId bus, g;
        using (var e = net.Edit())
        {
            bus = e.AddNode(K(1)); g = e.AddReferenceNode(K(2));
            e.AddResistor(bus, g, 8.0, K(11));
        }
        var host = new DeviceHost(net);
        var m1 = new Alternator(2.0, 0.3, 1.0, 2.0, 2.0, governorGain: 0.8, initialOmega: 12.0, initialTheta: 0.0);
        var m2 = new Alternator(2.0, 0.3, 1.0, 2.0, 2.0, governorGain: 0.8, initialOmega: 8.0, initialTheta: 1.2);
        host.Add(m1, stackalloc NodeId[2] { bus, g }, K(20), StateKey.From(K(20)));
        host.Add(m2, stackalloc NodeId[2] { bus, g }, K(30), StateKey.From(K(30)));
        net.SolveOperatingPoint();

        var dOmega0 = Math.Abs(m1.Omega - m2.Omega);
        long tick = 0;
        double maxAngleGapLate = 0.0;
        for (var i = 0; i < 1500; i++)
        {
            m1.SetMechanical(10.0, 0.0);
            m2.SetMechanical(10.0, 0.0);
            Step(net, host, 0.05, ref tick);
            if (i > 1200) maxAngleGapLate = Math.Max(maxAngleGapLate, Math.Abs(m1.Theta - m2.Theta));
        }

        // Speeds converge (the synchronising mechanism) …
        var dOmegaFinal = Math.Abs(m1.Omega - m2.Omega);
        Assert.True(dOmegaFinal < 0.05, $"speeds did not lock: Δω {dOmegaFinal:F4} (started {dOmega0:F2})");
        Assert.True(dOmegaFinal < dOmega0);
        // … so the rotor-angle difference is BOUNDED late in the run (no unbounded drift,
        // no strobing) — the phase-lock property.
        Assert.True(maxAngleGapLate < 50.0, $"rotor-angle gap unbounded: {maxAngleGapLate:F2} rad");
        Assert.True(Math.Abs(m1.CounterTorque) > 1e-3 && Math.Abs(m2.CounterTorque) > 1e-3,
            "both machines must be electrically coupled to the bus");
    }

    [Fact]
    public void Governor_off_machine_is_bounded_only_because_the_counter_torque_brakes()
    {
        // NEGATIVE CONTROL for the counter-torque SIGN (the phase-lock test above cannot
        // catch a sign flip — its Δω→0 is driven by the symmetric governors, not the
        // electrical coupling). Here governorGain:0, so the ONLY speed-limiting feedback
        // is the electrical counter-torque (plus tiny damping). Parameters are chosen so
        // the electrical braking coefficient (~0.5·k²/R_total ≈ 0.2) exceeds the damping
        // (0.05): with the CORRECT (braking) sign the speed settles; a flipped sign is net
        // POSITIVE feedback and the rotor runs away without bound.
        var net = Net();
        NodeId bus, g;
        using (var e = net.Edit())
        {
            bus = e.AddNode(K(1)); g = e.AddReferenceNode(K(2));
            e.AddResistor(bus, g, 8.0, K(11));
        }
        var host = new DeviceHost(net);
        var alt = new Alternator(inertia: 2.0, damping: 0.05, polePairs: 1.0, emfPerOmega: 2.0,
            seriesOhms: 2.0, governorGain: 0.0, initialOmega: 1.0);
        host.Add(alt, stackalloc NodeId[2] { bus, g }, K(20), StateKey.From(K(20)));
        net.SolveOperatingPoint();

        long tick = 0;
        double omegaMid = 0.0;
        for (var i = 0; i < 2000; i++)
        {
            alt.SetMechanical(shaftSpeed: 0.0, availableTorque: 2.0);   // shaftSpeed irrelevant (governor off)
            Step(net, host, 0.05, ref tick);
            if (i == 1500) omegaMid = alt.Omega;
            // A wrong-sign runaway diverges without bound; bail before it feeds an
            // ever-growing frequency into the AC solve (which would spin, not fail).
            if (alt.Omega > 100.0 || double.IsNaN(alt.Omega)) break;
        }

        // Bounded and converged — the electrical brake alone balances the prime mover.
        Assert.True(alt.Omega < 40.0, $"speed ran away: {alt.Omega:F2} rad/s (counter-torque sign wrong?)");
        Assert.True(Math.Abs(omegaMid - alt.Omega) < 0.1, $"not converged: {omegaMid:F3} → {alt.Omega:F3}");
        // The counter-torque OPPOSES rotation (strictly positive) while delivering positive
        // power; a flipped sign both inverts this AND drives the runaway ruled out above.
        Assert.True(alt.CounterTorque > 1e-3, $"counter-torque must brake (be positive): {alt.CounterTorque:F4}");
    }

    [Fact]
    public void Shared_bus_synchronising_power_pulls_two_governor_off_machines_together()
    {
        // NEGATIVE CONTROL for the SYNCHRONISING torque itself, isolating it from the
        // governors (governorGain:0). Two machines with DIFFERENT prime-mover torques
        // settle FAR APART when decoupled — the SEPARATE-bus control, identical machines
        // and loads but no shared node. On a SHARED bus the electrical coupling adds
        // synchronising power (the leader exports through the phase difference to the
        // laggard, which motors), which MATERIALLY compresses the speed spread. The
        // "lite" swing model does not pin the angle, so it does not drive Δω fully to
        // zero — but the compression is large and DIRECTIONAL: remove or sign-flip the
        // counter-torque and the shared case runs away / stops compressing, so it can no
        // longer beat the control (measured: shared ≈ 3.51, control ≈ 8.07).
        var shared = RunPair(sharedBus: true);
        var control = RunPair(sharedBus: false);

        Assert.True(control > 5.0, $"decoupled machines with unequal torque should stay far apart: Δω {control:F3}");
        Assert.True(shared < 0.55 * control,
            $"shared-bus coupling did not materially synchronise: shared Δω {shared:F3} vs control {control:F3}");
    }

    // Two governor-off machines (torques 3 and 1) either on one shared bus+load or on two
    // separate identical buses+loads; returns the final |Δω| after settling.
    private static double RunPair(bool sharedBus)
    {
        var net = Net();
        NodeId b1, b2, g;
        using (var e = net.Edit())
        {
            g = e.AddReferenceNode(K(2));
            b1 = e.AddNode(K(1));
            e.AddResistor(b1, g, 8.0, K(11));
            if (sharedBus) { b2 = b1; }
            else { b2 = e.AddNode(K(3)); e.AddResistor(b2, g, 8.0, K(12)); }
        }
        var host = new DeviceHost(net);
        var m1 = new Alternator(2.0, 0.05, 1.0, 2.0, 2.0, governorGain: 0.0, initialOmega: 12.0, initialTheta: 0.0);
        var m2 = new Alternator(2.0, 0.05, 1.0, 2.0, 2.0, governorGain: 0.0, initialOmega: 4.0, initialTheta: 0.7);
        host.Add(m1, stackalloc NodeId[2] { b1, g }, K(20), StateKey.From(K(20)));
        host.Add(m2, stackalloc NodeId[2] { b2, g }, K(30), StateKey.From(K(30)));
        net.SolveOperatingPoint();

        long tick = 0;
        for (var i = 0; i < 3000; i++)
        {
            m1.SetMechanical(0.0, 3.0);   // more prime-mover torque
            m2.SetMechanical(0.0, 1.0);   // less — different decoupled equilibria
            Step(net, host, 0.05, ref tick);
            // Guard against a wrong-sign runaway feeding an unbounded frequency to the AC
            // solve (keeps this a fast clean assertion failure, not a spin).
            if (Math.Abs(m1.Omega) > 200.0 || Math.Abs(m2.Omega) > 200.0
                || double.IsNaN(m1.Omega) || double.IsNaN(m2.Omega)) break;
        }
        return Math.Abs(m1.Omega - m2.Omega);
    }
}
