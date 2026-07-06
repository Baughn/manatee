using System;
using System.Buffers;
using CsCheck;
using Manatee.Core;
using Manatee.Core.State;

namespace Manatee.Core.Tests.State;

/// <summary>
/// Per-island snapshot/restore (api.md §14). Restore is ADDITIVE by StateKey
/// (matched / untouched / orphans; never resets an unmatched unit), and the
/// solve → snapshot → restore → step round-trip is BIT-FOR-BIT on RawVector
/// (law 4). The phase-8 device-state plug point round-trips through the same
/// stream.
/// </summary>
public sealed class SnapshotRestoreTests
{
    private static ExternalKey K(ulong id) => new(id);

    private static Core.Netlist Transient(double dt)
        => new(new NetlistOptions
        {
            Profile = SolverProfile.Transient(dt),
            Wiring = WiringPolicy.ExplicitOnly(),
            Debug = DebugLevel.Asserts,
        });

    private static Core.Netlist Mixed(double dt)
        => new(new NetlistOptions
        {
            Profile = SolverProfile.Mixed(dt),
            Wiring = WiringPolicy.ExplicitOnly(),
            Debug = DebugLevel.Asserts,
        });

    private static byte[] Snapshot(IslandHandle isl)
    {
        var w = new ArrayBufferWriter<byte>();
        isl.Snapshot(w);
        return w.WrittenSpan.ToArray();
    }

    [Fact]
    public void Snapshot_size_and_unit_count_reflect_the_stateful_primitives()
    {
        var net = Transient(0.1);
        NodeId a, x, g;
        using (var e = net.Edit())
        {
            a = e.AddNode(K(1)); x = e.AddNode(K(2)); g = e.AddReferenceNode(K(3));
            e.AddVoltageSource(a, g, 10.0, K(20));       // no state
            e.AddResistor(a, x, 1000.0, K(10));          // no state
            e.AddCapacitor(x, g, 1e-3, K(11), StateKey.From(K(11)));   // stateful
            e.AddInductor(x, g, 1.0, K(12), StateKey.From(K(12)));     // stateful
        }
        net.SolveOperatingPoint();

        var isl = net.Islands.Of(a);
        Assert.Equal(2, isl.StateUnitCount);
        var blob = Snapshot(isl);
        Assert.Equal(isl.SnapshotSize, blob.Length);
    }

    [Fact]
    public void Round_trip_restores_capacitor_state_exactly()
    {
        const double dt = 0.05;
        var net = Transient(dt);
        NodeId a, x, g; VSourceId vs;
        using (var e = net.Edit())
        {
            a = e.AddNode(K(1)); x = e.AddNode(K(2)); g = e.AddReferenceNode(K(3));
            vs = e.AddVoltageSource(a, g, 10.0, K(20));
            e.AddResistor(a, x, 1000.0, K(10));
            e.AddCapacitor(x, g, 1e-3, K(11), StateKey.From(K(11)));
        }
        for (var n = 0; n < 50; n++) net.Solve(new TickClock(n, dt));

        var vCharged = net.Solution.Voltage(x);
        Assert.True(vCharged > 5.0, "cap should have charged");
        var blobCharged = Snapshot(net.Islands.Of(a));

        // Discharge the cap by zeroing the source and running to rest.
        net.Drive(vs, 0.0);
        for (var n = 0; n < 400; n++) net.Solve(new TickClock(1000 + n, dt));
        Assert.True(Math.Abs(net.Solution.Voltage(x)) < 0.1, "cap should have discharged");

        // Restore the charged snapshot; re-snapshotting must reproduce it byte-for-byte.
        var res = net.Islands.Of(a).Restore(blobCharged);
        Assert.True(res.Ok);
        Assert.Equal(1, res.Matched);
        var blobAfter = Snapshot(net.Islands.Of(a));
        Assert.Equal(blobCharged, blobAfter);
    }

    [Fact]
    public void Restore_is_additive_matched_untouched_orphans()
    {
        // Source netlist: two caps K(11), K(12). Snapshot its island.
        var src = Transient(0.05);
        NodeId a, x, g;
        using (var e = src.Edit())
        {
            a = e.AddNode(K(1)); x = e.AddNode(K(2)); g = e.AddReferenceNode(K(3));
            e.AddVoltageSource(a, g, 10.0, K(20));
            e.AddResistor(a, x, 1000.0, K(10));
            e.AddCapacitor(x, g, 1e-3, K(11), StateKey.From(K(11)));
            e.AddCapacitor(x, g, 2e-3, K(12), StateKey.From(K(12)));
        }
        for (var n = 0; n < 40; n++) src.Solve(new TickClock(n, 0.05));
        var blob = Snapshot(src.Islands.Of(a));

        // Target netlist: caps K(11) (matches) and K(99) (untouched); K(12) is an ORPHAN.
        var dst = Transient(0.05);
        NodeId a2, x2, g2;
        using (var e = dst.Edit())
        {
            a2 = e.AddNode(K(1)); x2 = e.AddNode(K(2)); g2 = e.AddReferenceNode(K(3));
            e.AddVoltageSource(a2, g2, 10.0, K(20));
            e.AddResistor(a2, x2, 1000.0, K(10));
            e.AddCapacitor(x2, g2, 1e-3, K(11), StateKey.From(K(11)));
            e.AddCapacitor(x2, g2, 3e-3, K(99), StateKey.From(K(99)));
        }
        dst.SolveOperatingPoint();

        var isl = dst.Islands.Of(a2);
        Assert.Equal(2, isl.StateUnitCount);
        var res = isl.Restore(blob);
        Assert.Equal(1, res.Matched);      // K(11)
        Assert.Equal(1, res.Untouched);    // K(99) left as-is
        Assert.Equal(1, res.OrphansInBlob);// K(12) had no home
        Assert.False(res.Ok);

        Span<StateKey> orphans = stackalloc StateKey[4];
        var drained = res.DrainOrphans(orphans);
        Assert.Equal(1, drained);
        Assert.Equal(StateKey.From(K(12)), orphans[0]);
    }

    // ── Phase-8 device-state plug point ──

    private sealed class CounterDevice : IDeviceStateUnit
    {
        public long Ticks;
        public double Charge;
        private readonly StateKey _key;
        public CounterDevice(StateKey key) => _key = key;
        public StateKey Key => _key;
        public int BlobSize => 16;
        public void Save(Span<byte> dst)
        {
            System.Buffers.Binary.BinaryPrimitives.WriteInt64LittleEndian(dst, Ticks);
            System.Buffers.Binary.BinaryPrimitives.WriteInt64LittleEndian(dst.Slice(8), BitConverter.DoubleToInt64Bits(Charge));
        }
        public void Restore(ReadOnlySpan<byte> src)
        {
            Ticks = System.Buffers.Binary.BinaryPrimitives.ReadInt64LittleEndian(src);
            Charge = BitConverter.Int64BitsToDouble(System.Buffers.Binary.BinaryPrimitives.ReadInt64LittleEndian(src.Slice(8)));
        }
    }

    [Fact]
    public void Device_state_unit_round_trips_through_the_snapshot_stream()
    {
        var net = Transient(0.05);
        NodeId a, g;
        using (var e = net.Edit())
        {
            a = e.AddNode(K(1)); g = e.AddReferenceNode(K(2));
            e.AddVoltageSource(a, g, 10.0, K(20));
            e.AddResistor(a, g, 1000.0, K(10));
        }
        net.SolveOperatingPoint();

        var dev = new CounterDevice(StateKey.From(K(50))) { Ticks = 1234, Charge = 6.75 };
        net.RegisterDeviceState(a, dev);

        var isl = net.Islands.Of(a);
        Assert.Equal(1, isl.StateUnitCount);   // the device unit
        var blob = Snapshot(isl);

        // Mutate the device, then restore — it must come back exactly.
        dev.Ticks = 0; dev.Charge = 0.0;
        var res = isl.Restore(blob);
        Assert.Equal(1, res.Matched);
        Assert.True(res.Ok);
        Assert.Equal(1234, dev.Ticks);
        Assert.Equal(6.75, dev.Charge, 12);
    }

    // ── Coupler snapshot identity: the CLIENT StateKey (api.md §14; phase-9 fix —
    //    StageAddCoupler used to discard it and key on StateKey.From(externalKey)) ──

    private static Core.Netlist ConverterRig(ulong couplerKey, in StateKey state, out NodeId aPos, out CouplerId c)
    {
        var net = Mixed(0.05);
        var eff = EfficiencyCurve.Points((0.25, 0.80), (0.5, 0.90), (1.0, 0.95));
        using (var e = net.Edit())
        {
            aPos = e.AddNode(K(1)); var gnd = e.AddReferenceNode(K(2));
            var bPos = e.AddNode(K(3)); var bGnd = e.AddReferenceNode(K(4));
            e.AddVoltageSource(aPos, gnd, 100.0, K(10));
            e.AddResistor(bPos, bGnd, 5.0, K(11));
            c = e.AddCoupler(CouplerSpec.ConverterTwoPort(eff, 0.01, 50.0, 1000.0),
                new CouplerPorts(aPos, gnd, bPos, bGnd), K(couplerKey), state);
        }
        net.SolveOperatingPoint();
        net.Reconfigure(c, CouplerState.Closed);
        return net;
    }

    [Fact]
    public void Coupler_state_unit_keys_on_the_client_StateKey_not_the_external_key()
    {
        // Source rig: coupler ExternalKey K(30), CLIENT StateKey S(777). Settle, snapshot.
        var clientKey = new StateKey(777);
        var src = ConverterRig(30, clientKey, out var aPosSrc, out _);
        for (var i = 0; i < 200; i++) src.Solve(new TickClock(1 + i, 0.05));
        var blob = Snapshot(src.Islands.Of(aPosSrc));

        // Target rig: DIFFERENT ExternalKey K(31), SAME client StateKey S(777). The
        // coupler runtime unit must MATCH by the client key — under the old
        // StateKey.From(externalKey) keying it would orphan.
        var dst = ConverterRig(31, clientKey, out var aPosDst, out var cDst);
        dst.Solve(new TickClock(1, 0.05));
        var res = dst.Islands.Of(aPosDst).Restore(blob);
        Assert.True(res.Matched >= 1, "the coupler unit must restore by CLIENT StateKey");
        Assert.Equal(0, res.OrphansInBlob);

        // And the restored DC-link/droop state is live: the ledger resumes from the
        // snapshotted totals rather than zero.
        var led = dst.Islands.Of(aPosDst).Ledger(cDst);
        Assert.True(led.InJ > 0.0, "restored coupler ledger should carry the snapshotted energy history");
    }

    [Fact]
    public void Coupler_state_unit_with_mismatched_client_key_orphans()
    {
        var src = ConverterRig(30, new StateKey(777), out var aPosSrc, out _);
        for (var i = 0; i < 50; i++) src.Solve(new TickClock(1 + i, 0.05));
        var blob = Snapshot(src.Islands.Of(aPosSrc));

        var dst = ConverterRig(30, new StateKey(888), out var aPosDst, out _);   // same ExternalKey!
        dst.Solve(new TickClock(1, 0.05));
        var res = dst.Islands.Of(aPosDst).Restore(blob);
        // Identity is the CLIENT key: the same ExternalKey no longer matches it.
        Assert.Equal(0, res.Matched);
        Assert.True(res.OrphansInBlob >= 1);
    }

    // ── Law 4: solve → snapshot → restore → step is bit-for-bit on RawVector ──

    [Fact]
    public void Law4_snapshot_restore_step_matches_never_snapshotted_bit_for_bit()
    {
        var gen =
            from amp in Gen.Double[1.0, 100.0]
            from freq in Gen.Double[1.0, 8.0]
            from r1 in Gen.Double[50.0, 5000.0]
            from r2 in Gen.Double[50.0, 5000.0]
            from cF in Gen.Double[1e-4, 5e-3]
            from lH in Gen.Double[0.1, 5.0]
            from m in Gen.Int[3, 15]
            select (amp, freq, r1, r2, cF, lH, m);

        gen.Sample(t =>
        {
            var (amp, freq, r1, r2, cF, lH, m) = t;
            const double dt = 0.05; const int k = 7;

            var net = Mixed(dt);
            NodeId a, x, g;
            using (var e = net.Edit())
            {
                a = e.AddNode(K(1)); x = e.AddNode(K(2)); g = e.AddReferenceNode(K(3));
                e.AddSineSource(a, g, new SineDrive(amp, freq, 0.3), K(20), StateKey.From(K(20)));
                e.AddResistor(a, x, r1, K(10));
                e.AddResistor(x, g, r2, K(11));
                e.AddCapacitor(x, g, cF, K(12), StateKey.From(K(12)));
                e.AddInductor(x, g, lH, K(13), StateKey.From(K(13)));
            }
            net.SolveOperatingPoint();

            // Advance M ticks, snapshot mid-phase.
            for (var i = 0; i < m; i++) net.Solve(new TickClock(1 + i, dt));
            var blob = Snapshot(net.Islands.Of(a));

            // Reference: continue K ticks WITHOUT snapshotting.
            for (var i = 0; i < k; i++) net.Solve(new TickClock(100 + i, dt));
            var vecRef = net.Solution.RawVector(net.IslandOf(a)).ToArray();

            // Restore rewinds to the snapshot; the same K ticks must reproduce it exactly.
            var res = net.Islands.Of(a).Restore(blob);
            Assert.True(res.Ok, "self-restore should match every unit");
            for (var i = 0; i < k; i++) net.Solve(new TickClock(100 + i, dt));
            var vecTest = net.Solution.RawVector(net.IslandOf(a)).ToArray();

            Assert.Equal(vecRef.Length, vecTest.Length);
            for (var i = 0; i < vecRef.Length; i++)
                Assert.True(BitConverter.DoubleToInt64Bits(vecRef[i]) == BitConverter.DoubleToInt64Bits(vecTest[i]),
                    $"row {i}: {vecRef[i]:R} vs {vecTest[i]:R} (not bit-identical)");
        }, iter: 100, seed: "0000000000001");
    }
}
