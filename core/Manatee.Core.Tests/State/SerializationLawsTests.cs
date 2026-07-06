using System;
using System.Buffers;
using CsCheck;
using Manatee.Core;

namespace Manatee.Core.Tests.State;

/// <summary>
/// The whole-netlist serialization laws (api.md §14): SaveCanonical (slot-
/// preserving) / SaveNormalized (ExternalKey-sorted minimal) / FromCanonical, and
/// the cheap structural Fingerprint. Law 3 — a random edit sequence and its
/// from-scratch rebuild agree under SaveNormalized — is R11's drift detector
/// stated as an equality.
/// </summary>
public sealed class SerializationLawsTests
{
    private static ExternalKey K(ulong id) => new(id);

    private static NetlistOptions Opts() => new()
    {
        Profile = SolverProfile.Mixed(0.05),
        Wiring = WiringPolicy.ExplicitOnly(),
        Partitioning = PartitioningMode.SelfPartitioned,
        Debug = DebugLevel.Asserts,
    };

    private static byte[] Canonical(Core.Netlist n)
    {
        var w = new ArrayBufferWriter<byte>();
        n.SaveCanonical(w);
        return w.WrittenSpan.ToArray();
    }

    private static byte[] Normalized(Core.Netlist n)
    {
        var w = new ArrayBufferWriter<byte>();
        n.SaveNormalized(w);
        return w.WrittenSpan.ToArray();
    }

    // A moderately rich netlist: two islands, a converter boundary coupler (exercises
    // the EfficiencyCurve serialization), caps, a limit envelope, and probes.
    private static Core.Netlist Build()
    {
        var net = new Core.Netlist(Opts());
        using var e = net.Edit();
        var aPos = e.AddNode(K(1)); var g = e.AddReferenceNode(K(2));
        var mid = e.AddNode(K(3));
        var bPos = e.AddNode(K(4)); var bg = e.AddReferenceNode(K(5));
        e.AddVoltageSource(aPos, g, 100.0, K(20));
        e.AddResistor(aPos, mid, 470.0, K(10), new LimitSpec(0.5, 0.0, 60.0, new I2tParams(2.0, 5.0)));
        e.AddCapacitor(mid, g, 1e-3, K(11), StateKey.From(K(11)));
        e.AddResistor(bPos, bg, 10.0, K(12));
        var eff = EfficiencyCurve.Points((0.25, 0.8), (0.5, 0.9), (1.0, 0.95));
        e.AddCoupler(CouplerSpec.ConverterTwoPort(eff, 0.01, 50.0, 1000.0),
            new CouplerPorts(aPos, g, bPos, bg), K(30), StateKey.From(K(30)));
        e.AddInterpolatedProbe(aPos, mid, 0.5, K(40));
        e.AddProbe(bPos, K(41));
        return net;
    }

    [Fact]
    public void Law1_canonical_round_trips_byte_equal()
    {
        var net = Build();
        net.SolveOperatingPoint();   // solved state (charged caps) is part of the memento
        var b1 = Canonical(net);
        var net2 = Core.Netlist.FromCanonical(b1, Opts());
        var b2 = Canonical(net2);
        Assert.Equal(b1, b2);
    }

    [Fact]
    public void Law2_normalized_is_a_fixpoint_across_a_canonical_round_trip()
    {
        var net = Build();
        var norm1 = Normalized(net);
        var net2 = Core.Netlist.FromCanonical(Canonical(net), Opts());
        var norm2 = Normalized(net2);
        Assert.Equal(norm1, norm2);
    }

    [Fact]
    public void Law3_edit_sequence_and_from_scratch_rebuild_agree_under_normalized()
    {
        // Path A: build directly.
        var a = Build();

        // Path B: build with a DIFFERENT add order and a churned slot (add then remove a
        // throwaway resistor), so the slot layout differs — yet the LOGICAL circuit is
        // identical, so SaveNormalized must be byte-equal.
        var b = new Core.Netlist(Opts());
        using (var e = b.Edit())
        {
            var bPos = e.AddNode(K(4)); var bg = e.AddReferenceNode(K(5));
            var mid = e.AddNode(K(3));
            var aPos = e.AddNode(K(1)); var g = e.AddReferenceNode(K(2));
            e.AddResistor(bPos, bg, 10.0, K(12));
            e.AddCapacitor(mid, g, 1e-3, K(11), StateKey.From(K(11)));
            e.AddResistor(aPos, mid, 470.0, K(10), new LimitSpec(0.5, 0.0, 60.0, new I2tParams(2.0, 5.0)));
            e.AddVoltageSource(aPos, g, 100.0, K(20));
            var eff = EfficiencyCurve.Points((0.25, 0.8), (0.5, 0.9), (1.0, 0.95));
            e.AddCoupler(CouplerSpec.ConverterTwoPort(eff, 0.01, 50.0, 1000.0),
                new CouplerPorts(aPos, g, bPos, bg), K(30), StateKey.From(K(30)));
            e.AddProbe(bPos, K(41));
            e.AddInterpolatedProbe(aPos, mid, 0.5, K(40));
        }
        // Churn a slot to force a different layout than path A.
        ResistorId throwaway;
        using (var e = b.Edit())
        {
            var t1 = e.AddNode(K(90)); var t2 = e.AddNode(K(91));
            throwaway = e.AddResistor(t1, t2, 5.0, K(92));
        }
        using (var e = b.Edit())
        {
            e.Remove(throwaway);
            Assert.True(b.TryResolveNode(K(90), out var t1));
            Assert.True(b.TryResolveNode(K(91), out var t2));
            e.RemoveNode(t1); e.RemoveNode(t2);
        }

        Assert.Equal(Normalized(a), Normalized(b));
    }

    [Fact]
    public void Fingerprint_is_structural_stable_and_value_sensitive()
    {
        var net = Build();
        var isl = net.IslandOf(net.TryResolveNode(K(1), out var n1) ? n1 : default);

        var fpS = net.Fingerprint(isl, FingerprintScope.Structural);
        Assert.Equal(fpS, net.Fingerprint(isl, FingerprintScope.Structural));   // stable within a process run

        // Survives a canonical round-trip (same structure ⇒ same hash).
        var net2 = Core.Netlist.FromCanonical(Canonical(net), Opts());
        var isl2 = net2.IslandOf(net2.TryResolveNode(K(1), out var m1) ? m1 : default);
        Assert.Equal(fpS, net2.Fingerprint(isl2, FingerprintScope.Structural));

        // Full scope mixes in values; Structural does not — a value change moves Full
        // but not Structural.
        var fpFull = net.Fingerprint(isl, FingerprintScope.Full);
        Assert.True(net.TryResolve(K(10), out var r));
        net.Adjust(new ResistorId(r.Slot, r.Gen, r.Net), 999.0);
        Assert.Equal(fpS, net.Fingerprint(isl, FingerprintScope.Structural));
        Assert.NotEqual(fpFull, net.Fingerprint(isl, FingerprintScope.Full));
    }
}
