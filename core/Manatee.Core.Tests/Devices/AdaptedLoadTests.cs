using System;
using Manatee.Core;
using Manatee.Core.Devices;
using static Manatee.Core.Tests.Devices.DevicesTestKit;

namespace Manatee.Core.Tests.Devices;

/// <summary>
/// R18 <see cref="AdaptedLoad"/>: constant power via G = P/V_prev² clamped, brownout
/// with hysteresis + staggered rejoin + recloser lockout, and the across-tick energy
/// ledger that stops an oscillating advertised power from pumping free energy.
/// </summary>
public sealed class AdaptedLoadTests
{
    // A source with internal resistance so the load can actually sag its own voltage.
    private static (Core.Netlist net, DeviceHost host, VSourceId src, NodeId a, AdaptedLoad load)
        Rig(AdaptedLoad load, double vSource = 100.0, double rSource = 1.0)
    {
        var net = Net();
        NodeId a, mid, g;
        VSourceId src;
        using (var e = net.Edit())
        {
            mid = e.AddNode(K(1)); a = e.AddNode(K(2)); g = e.AddReferenceNode(K(3));
            src = e.AddVoltageSource(mid, g, vSource, K(10));
            e.AddResistor(mid, a, rSource, K(11));   // source internal resistance
        }
        var host = new DeviceHost(net);
        host.Add(load, stackalloc NodeId[2] { a, g }, K(20), StateKey.From(K(20)));
        net.SolveOperatingPoint();
        return (net, host, src, a, load);
    }

    [Fact]
    public void Constant_power_settles_to_the_advertised_watts()
    {
        // Vs 100 V, Rs 1 Ω, advertised 100 W. Operating point solves
        //   V_a² − 100·V_a + 100 = 0  ⇒  V_a ≈ 99 V, I ≈ 1.01 A, P ≈ 100 W.
        var (net, host, _, a, load) = Rig(new AdaptedLoad(advertisedWatts: 100.0, gMin: 1e-9, gMax: 100.0));
        long tick = 0;
        for (var i = 0; i < 200; i++) Step(net, host, 0.05, ref tick);

        var p = net.Solution.Power(load.ConductanceResistor);
        Assert.Equal(100.0, p, 1);
        Assert.True(net.Solution.Voltage(a) > 95.0);
    }

    [Fact]
    public void Brownout_has_hysteresis_and_rejoins_after_the_stagger_delay()
    {
        var load = new AdaptedLoad(advertisedWatts: 100.0, gMin: 1e-9, gMax: 100.0,
            brownoutLowVolts: 20.0, brownoutHighVolts: 40.0, lockoutCount: 100,
            staggerBaseTicks: 3, staggerSpreadTicks: 5);
        var (net, host, src, _, _) = Rig(load);
        long tick = 0;
        for (var i = 0; i < 50; i++) Step(net, host, 0.05, ref tick);
        Assert.Equal(0, load.Mode);   // Live

        // Collapse the supply → the load must brown out (drops below V_low = 20 V).
        net.Drive(src, 5.0);
        for (var i = 0; i < 10; i++) Step(net, host, 0.05, ref tick);
        Assert.Equal(1, load.Mode);   // BrownedOut

        // Restore the supply. Hysteresis: it must NOT rejoin instantly — it waits the
        // full deterministic stagger delay of continuous above-V_high before going Live.
        net.Drive(src, 100.0);
        var delay = load.RejoinDelayTicks;
        Assert.InRange(delay, 3, 7);   // base 3 + spread [0,5)
        for (var i = 0; i < delay - 1; i++) { Step(net, host, 0.05, ref tick); Assert.Equal(1, load.Mode); }
        // Within a couple more ticks it rejoins.
        for (var i = 0; i < 3; i++) Step(net, host, 0.05, ref tick);
        Assert.Equal(0, load.Mode);   // Live again
    }

    [Fact]
    public void Rejoin_delay_is_deterministic_and_key_staggered()
    {
        // Same StateKey ⇒ identical delay across independent builds (no RNG).
        var a1 = BuildLoad(StateKey.From(K(20)));
        var a2 = BuildLoad(StateKey.From(K(20)));
        Assert.Equal(a1.RejoinDelayTicks, a2.RejoinDelayTicks);

        // Different keys spread the fleet: at least two distinct delays across a batch.
        var seen = new System.Collections.Generic.HashSet<int>();
        for (ulong k = 100; k < 120; k++) seen.Add(BuildLoad(StateKey.From(new ExternalKey(k))).RejoinDelayTicks);
        Assert.True(seen.Count >= 2, "staggered rejoin must spread devices across delays");

        static AdaptedLoad BuildLoad(StateKey sk)
        {
            var load = new AdaptedLoad(100.0, staggerBaseTicks: 2, staggerSpreadTicks: 8);
            var net = Net();
            NodeId a, g;
            using (var e = net.Edit()) { a = e.AddNode(K(1)); g = e.AddReferenceNode(K(2)); }
            new DeviceHost(net).Add(load, stackalloc NodeId[2] { a, g }, K(20), sk);
            return load;
        }
    }

    [Fact]
    public void Recloser_locks_out_after_k_brownouts_and_reset_recovers()
    {
        // Fast rejoin (delay 0) so we can cycle quickly; lock out after 3 brownouts.
        var load = new AdaptedLoad(100.0, gMin: 1e-9, gMax: 100.0,
            brownoutLowVolts: 20.0, brownoutHighVolts: 40.0,
            lockoutCount: 3, lockoutWindowTicks: 10_000,
            staggerBaseTicks: 0, staggerSpreadTicks: 1);
        var (net, host, src, _, _) = Rig(load);
        long tick = 0;
        for (var i = 0; i < 20; i++) Step(net, host, 0.05, ref tick);
        Assert.Equal(0, load.Mode);

        // Cycle the supply low/high; each low edge is one brownout. Three ⇒ lockout.
        for (var cycle = 0; cycle < 3; cycle++)
        {
            net.Drive(src, 5.0);
            for (var i = 0; i < 3; i++) Step(net, host, 0.05, ref tick);
            net.Drive(src, 100.0);
            for (var i = 0; i < 3; i++) Step(net, host, 0.05, ref tick);
        }
        Assert.Equal(2, load.Mode);   // LockedOut

        // A locked-out load stays shed even with healthy supply, until a manual Reset().
        net.Drive(src, 100.0);
        for (var i = 0; i < 10; i++) Step(net, host, 0.05, ref tick);
        Assert.Equal(2, load.Mode);
        load.Reset();
        for (var i = 0; i < 5; i++) Step(net, host, 0.05, ref tick);
        Assert.Equal(0, load.Mode);   // Live
    }

    [Fact]
    public void Energy_ledger_bounds_delivery_under_a_pumping_supply()
    {
        // The pump: hold advertised power CONSTANT at 100 W but oscillate the supply as
        // a square wave (80/120 V). Linearising at V_prev means the load over-draws on
        // the ticks the supply steps UP (actual = P·(V_now/V_prev)² > P). The ledger must
        // claw it back so cumulative delivered energy never exceeds advertised + a small
        // bounded slack — it cannot harvest free energy from the oscillation.
        var load = new AdaptedLoad(advertisedWatts: 100.0, gMin: 1e-9, gMax: 100.0);
        var (net, host, src, _, _) = Rig(load, vSource: 100.0, rSource: 1.0);
        const double dt = 0.05;
        long tick = 0;
        for (var i = 0; i < 20; i++) Step(net, host, dt, ref tick);   // warm up

        double actualJ = 0.0, advertisedJ = 0.0, maxDebt = 0.0;
        const int n = 400;
        for (var i = 0; i < n; i++)
        {
            net.Drive(src, (i % 2 == 0) ? 120.0 : 80.0);   // pumping supply
            actualJ += net.Solution.Power(load.ConductanceResistor) * dt;   // last solve's delivery
            advertisedJ += 100.0 * dt;
            Step(net, host, dt, ref tick);
            if (load.DebtJoules > maxDebt) maxDebt = load.DebtJoules;
        }

        // Ledger-bounded: delivered ≤ advertised + O(one tick of peak power). Peak power
        // here is well under a few hundred W, so a 10 J slack is generous.
        Assert.True(actualJ <= advertisedJ + 10.0,
            $"delivered {actualJ:F2} J exceeds advertised {advertisedJ:F2} J + slack (free-energy pump!)");
        // Debt stays BOUNDED (never a runaway) — the load pays back what it over-drew.
        Assert.True(maxDebt < 20.0, $"debt {maxDebt:F2} J is unbounded");
    }
}
