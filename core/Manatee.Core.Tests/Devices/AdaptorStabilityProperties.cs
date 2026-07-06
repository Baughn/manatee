using System;
using CsCheck;
using Manatee.Core;
using Manatee.Core.Devices;
using static Manatee.Core.Tests.Devices.DevicesTestKit;

namespace Manatee.Core.Tests.Devices;

/// <summary>
/// R18 adaptor-STABILITY harness (testing-strategy.md): randomised (seeded) sets of
/// constant-power <see cref="AdaptedLoad"/>s on a soft supply, driven through a
/// collapse and recovery. Every run must SETTLE or BROWN OUT — never STROBE. Bare
/// constant-power loads on a soft bus limit-cycle (all on → bus sags → all off → bus
/// recovers → all on …); the hysteresis + per-device stagger + recloser lockout stack
/// is precisely what bounds the mode-flip count per window. That bound is the assertion.
/// </summary>
public sealed class AdaptorStabilityProperties
{
    private sealed class Rig
    {
        public Core.Netlist Net = null!;
        public DeviceHost Host = null!;
        public VSourceId Src;
        public AdaptedLoad[] Loads = null!;
        public int[] Flips = null!;
        public int[] LastMode = null!;
    }

    private static Rig Build(int n, double[] powers, double rSource)
    {
        var net = Net();
        NodeId src, bus, g;
        VSourceId vs;
        using (var e = net.Edit())
        {
            src = e.AddNode(K(1)); bus = e.AddNode(K(2)); g = e.AddReferenceNode(K(3));
            vs = e.AddVoltageSource(src, g, 100.0, K(10));
            e.AddResistor(src, bus, rSource, K(11));   // soft supply
        }
        var host = new DeviceHost(net);
        var loads = new AdaptedLoad[n];
        var terms = new NodeId[2] { bus, g };
        for (var i = 0; i < n; i++)
        {
            // Distinct StateKeys ⇒ distinct deterministic rejoin staggers.
            loads[i] = new AdaptedLoad(powers[i], gMin: 1e-9, gMax: 100.0,
                brownoutLowVolts: 40.0, brownoutHighVolts: 60.0,
                lockoutCount: 3, lockoutWindowTicks: 30,
                staggerBaseTicks: 2, staggerSpreadTicks: 12);
            var key = new ExternalKey(100 + (ulong)i);
            host.Add(loads[i], terms, key, StateKey.From(key));
        }
        net.SolveOperatingPoint();
        return new Rig
        {
            Net = net, Host = host, Src = vs, Loads = loads,
            Flips = new int[n], LastMode = new int[n],
        };
    }

    [Fact]
    public void Random_constant_power_load_sets_settle_or_brownout_without_strobing()
    {
        var gen =
            from n in Gen.Int[3, 6]
            from powers in Gen.Double[200.0, 1200.0].Array[n]
            from rs in Gen.Double[2.0, 8.0]
            select (n, powers, rs);

        gen.Sample(t =>
        {
            var (n, powers, rs) = t;
            var rig = Build(n, powers, rs);
            const double dt = 0.05;
            long tick = 0;

            for (var i = 0; i < n; i++) rig.LastMode[i] = rig.Loads[i].Mode;

            // Phase 1: full supply (some loads may already overload the bus).
            // Phase 2: brown the supply DOWN (forces brownouts). Phase 3: recover.
            const int window = 60;   // trailing window whose flip count we bound
            var flipsInWindow = new int[n];
            const int total = 260;
            for (var step = 0; step < total; step++)
            {
                var v = step < 90 ? 100.0 : step < 170 ? 30.0 : 100.0;
                rig.Net.Drive(rig.Src, v);
                rig.Host.Tick(dt);
                rig.Net.Solve(new TickClock(++tick, dt));

                for (var i = 0; i < n; i++)
                {
                    var m = rig.Loads[i].Mode;
                    if (m != rig.LastMode[i])
                    {
                        rig.Flips[i]++;
                        if (step >= total - window) flipsInWindow[i]++;
                    }
                    rig.LastMode[i] = m;
                }
            }

            // No strobing: within the trailing window a device flips only a handful of
            // times — the recloser latches repeat offenders (≤ 3 brownouts per 30-tick
            // window ⇒ ≤ 6 flips per window before lockout), the stagger spreads rejoins.
            // A limit-cycling BARE load would instead flip ~ once per 1–2 ticks (≈ 30–60 in
            // this window); the bound of 12 cleanly separates "settled" from "strobing".
            for (var i = 0; i < n; i++)
                Assert.True(flipsInWindow[i] <= 12,
                    $"load {i} strobed: {flipsInWindow[i]} mode-flips in the last {window} ticks");

            // No NaN/instability leaked: the bus voltage stays finite and non-negative.
            var vbus = rig.Net.Solution.Voltage(rig.Net.TryResolveNode(K(2), out var b) ? b : default);
            Assert.True(double.IsFinite(vbus) && vbus >= -1e-6, $"bus voltage went unstable: {vbus}");
        }, iter: 200, seed: "0000000000001");
    }
}
