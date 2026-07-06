using System;
using System.Buffers;
using Manatee.Core;

namespace Manatee.Core.Tests.Devices;

/// <summary>
/// The phase-6 LAW-4 CARRY (api.md §14): the per-island snapshot now also rides the
/// evolving state it used to reset — each boundary coupler's persistent runtime
/// (DC-link voltage, droop/ledger integrators; anchored on the A-side island) and each
/// limits-bearing component's i²t melting integral + trip latch. With the carry, law 4
/// (solve → snapshot → restore → step, bit-for-bit) holds for a CONVERTER-COUPLED island
/// and a MID-MELT fuse, not only a StateKey-keyed RLC island.
/// </summary>
public sealed class Law4CarryTests
{
    private static ExternalKey K(ulong id) => new(id);

    private static Core.Netlist Mixed(double dt)
        => new(new NetlistOptions
        {
            Profile = SolverProfile.Mixed(dt),
            Wiring = WiringPolicy.ExplicitOnly(),
            Partitioning = PartitioningMode.SelfPartitioned,
            Debug = DebugLevel.Asserts,
        });

    private static byte[] Snap(IslandHandle isl)
    {
        var w = new ArrayBufferWriter<byte>();
        isl.Snapshot(w);
        return w.WrittenSpan.ToArray();
    }

    private static void AssertBitEqual(ReadOnlySpan<double> a, ReadOnlySpan<double> b, string what)
    {
        Assert.Equal(a.Length, b.Length);
        for (var i = 0; i < a.Length; i++)
            Assert.True(BitConverter.DoubleToInt64Bits(a[i]) == BitConverter.DoubleToInt64Bits(b[i]),
                $"{what} row {i}: {a[i]:R} vs {b[i]:R} not bit-identical");
    }

    [Fact]
    public void Converter_coupled_unit_snapshot_restore_step_is_bit_for_bit()
    {
        const double dt = 0.05; const int k = 8;
        var net = Mixed(dt);
        NodeId aPos, aGnd, bPos, bGnd;
        CouplerId cpl;
        using (var e = net.Edit())
        {
            aPos = e.AddNode(K(1)); aGnd = e.AddReferenceNode(K(2));
            bPos = e.AddNode(K(3)); bGnd = e.AddReferenceNode(K(4));
            e.AddVoltageSource(aPos, aGnd, 100.0, K(10));
            e.AddResistor(bPos, bGnd, 5.0, K(11));   // island-B load drawn from the DC-link cap
            var eff = EfficiencyCurve.Points((0.25, 0.85), (1.0, 0.95));
            cpl = e.AddCoupler(CouplerSpec.ConverterTwoPort(eff, dcLinkFarads: 0.02, outputVolts: 48.0, ratedWatts: 2000.0),
                new CouplerPorts(aPos, aGnd, bPos, bGnd), K(30), StateKey.From(K(30)));
        }
        net.SolveOperatingPoint();
        net.Reconfigure(cpl, CouplerState.Closed);

        // Settle the coupled unit (DC-link cap charges, droop/ledger integrators evolve).
        long tick = 0;
        for (var i = 0; i < 40; i++) net.Solve(new TickClock(++tick, dt));

        // Snapshot BOTH islands mid-transient. The coupler runtime rides on the A-side
        // island's snapshot; the DC-link cap history it carries drives island B.
        var blobA = Snap(net.Islands.Of(aPos));
        var blobB = Snap(net.Islands.Of(bPos));

        // Reference: K more ticks WITHOUT snapshotting.
        for (var i = 0; i < k; i++) net.Solve(new TickClock(1000 + i, dt));
        var refA = net.Solution.RawVector(net.IslandOf(aPos)).ToArray();
        var refB = net.Solution.RawVector(net.IslandOf(bPos)).ToArray();

        // Restore rewinds the coupled unit; the same K ticks must reproduce it exactly.
        Assert.True(net.Islands.Of(aPos).Restore(blobA).Ok);
        net.Islands.Of(bPos).Restore(blobB);
        for (var i = 0; i < k; i++) net.Solve(new TickClock(1000 + i, dt));
        var testA = net.Solution.RawVector(net.IslandOf(aPos)).ToArray();
        var testB = net.Solution.RawVector(net.IslandOf(bPos)).ToArray();

        AssertBitEqual(refA, testA, "island A");
        AssertBitEqual(refB, testB, "island B");
    }

    [Fact]
    public void Mid_melt_fuse_survives_snapshot_restore_and_trips_at_the_same_tick()
    {
        const double dt = 0.05; const int k = 60;
        var net = Mixed(dt);
        NodeId a, x, g;
        using (var e = net.Edit())
        {
            a = e.AddNode(K(1)); x = e.AddNode(K(2)); g = e.AddReferenceNode(K(3));
            e.AddVoltageSource(a, g, 10.0, K(10));
            // Fuse: 1 Ω, rated 1 A, melts at 50 A²·s. At |I| ≈ 5 A the heating rate is
            // (25 − 1) = 24 A²/s, so from cold it would trip at ≈ 2.08 s ≈ tick 42.
            e.AddResistor(a, x, 1.0, K(11), new LimitSpec(1.0, 0.0, 0.0, new I2tParams(50.0, 1e9)));
            e.AddResistor(x, g, 1.0, K(12));                       // load
            e.AddCapacitor(x, g, 1e-3, K(13), StateKey.From(K(13)));   // RLC state for the RawVector carry
        }
        net.SolveOperatingPoint();

        // Warm the fuse partway (NOT yet tripped), then snapshot.
        long tick = 0;
        for (var i = 0; i < 20; i++) net.Solve(new TickClock(++tick, dt));
        var blob = Snap(net.Islands.Of(a));

        var refTrip = RunAndFindTrip(net, a, k, dt, 1000);
        var refVec = net.Solution.RawVector(net.IslandOf(a)).ToArray();

        // Restore the mid-melt snapshot. Without the i²t carry the accumulator would still
        // be at its far-advanced live value and trip immediately; WITH the carry it resumes
        // exactly mid-melt and trips at the same relative tick, and the RLC state is bit-equal.
        Assert.True(net.Islands.Of(a).Restore(blob).Ok);
        var testTrip = RunAndFindTrip(net, a, k, dt, 1000);
        var testVec = net.Solution.RawVector(net.IslandOf(a)).ToArray();

        Assert.True(refTrip >= 0, "fuse should trip in the reference window");
        Assert.Equal(refTrip, testTrip);
        AssertBitEqual(refVec, testVec, "fuse island");
    }

    // Run k ticks; return the 0-based offset of the first ThermalI2t trip, or -1.
    private static int RunAndFindTrip(Core.Netlist net, NodeId a, int k, double dt, long baseTick)
    {
        var trip = -1;
        Span<LimitEvent> evs = stackalloc LimitEvent[8];
        for (var i = 0; i < k; i++)
        {
            net.Solve(new TickClock(baseTick + i, dt));
            var n = net.Islands.Of(a).DrainLimitEvents(evs);
            for (var j = 0; j < n; j++)
                if (evs[j].Kind == LimitKind.ThermalI2t && trip < 0) trip = i;
        }
        return trip;
    }
}
