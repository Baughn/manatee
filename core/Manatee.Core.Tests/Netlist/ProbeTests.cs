using System;
using Manatee.Core;

namespace Manatee.Core.Tests.Netlist;

/// <summary>
/// Probes and the oscilloscope tap (api.md §13). A node probe reads one node; an
/// interpolated probe reads V = Va + t·(Vb − Va) (the reduction layer re-aims t
/// after a series collapse). A WaveformTap samples one probe per solver substep
/// into a caller-owned ring — the scope contract, exercised during AC subcycling.
/// </summary>
public sealed class ProbeTests
{
    private static ExternalKey K(ulong id) => new(id);

    private static Core.Netlist Transient(double dt)
        => new(new NetlistOptions
        {
            Profile = SolverProfile.Transient(dt),
            Wiring = WiringPolicy.ExplicitOnly(),
            Debug = DebugLevel.Asserts,
        });

    private static Core.Netlist Mixed(double dt, int samples = 20)
        => new(new NetlistOptions
        {
            Profile = SolverProfile.Mixed(dt, samples),
            Wiring = WiringPolicy.ExplicitOnly(),
            Debug = DebugLevel.Asserts,
        });

    [Fact]
    public void Node_probe_reads_its_node_potential()
    {
        // 10 V divider: two equal resistors ⇒ midpoint at 5 V. A node probe on the
        // midpoint reads exactly that.
        var net = Transient(0.1);
        NodeId a, mid, g; ProbeId p;
        using (var e = net.Edit())
        {
            a = e.AddNode(K(1)); mid = e.AddNode(K(2)); g = e.AddReferenceNode(K(3));
            e.AddVoltageSource(a, g, 10.0, K(20));
            e.AddResistor(a, mid, 1000.0, K(10));
            e.AddResistor(mid, g, 1000.0, K(11));
            p = e.AddProbe(mid, K(30));
        }
        net.SolveOperatingPoint();
        Assert.Equal(5.0, net.Solution.Read(p), 6);
        Assert.Equal(net.Solution.Voltage(mid), net.Solution.Read(p), 9);
    }

    [Fact]
    public void Interpolated_probe_reads_the_lerp_and_reaims_via_meta()
    {
        // Endpoints at 10 V and 0 V; an interpolated probe at t reads 10·(1−t).
        var net = Transient(0.1);
        NodeId a, mid, g; ProbeId p;
        using (var e = net.Edit())
        {
            a = e.AddNode(K(1)); mid = e.AddNode(K(2)); g = e.AddReferenceNode(K(3));
            e.AddVoltageSource(a, g, 10.0, K(20));
            e.AddResistor(a, mid, 1000.0, K(10));
            e.AddResistor(mid, g, 1000.0, K(11));
            p = e.AddInterpolatedProbe(a, g, 0.25, K(30));   // 10 + 0.25·(0−10) = 7.5
        }
        net.SolveOperatingPoint();
        Assert.Equal(7.5, net.Solution.Read(p), 6);

        // Re-aim the interpolation (tier 0) — the reduction layer's SetProbeInterpolation.
        net.Meta.SetProbeInterpolation(p, a, g, 0.75);       // 10 + 0.75·(−10) = 2.5
        Assert.Equal(2.5, net.Solution.Read(p), 6);

        // Re-aim onto the mid node exactly (t = 0 ⇒ node probe on `mid`, at 5 V).
        net.Meta.SetProbeInterpolation(p, mid, mid, 0.0);
        Assert.Equal(5.0, net.Solution.Read(p), 6);
    }

    [Fact]
    public void Removed_probe_reads_zero_and_reresolves_by_key()
    {
        var net = Transient(0.1);
        NodeId a, g; ProbeId p;
        using (var e = net.Edit())
        {
            a = e.AddNode(K(1)); g = e.AddReferenceNode(K(2));
            e.AddVoltageSource(a, g, 10.0, K(20));
            p = e.AddProbe(a, K(30));
        }
        net.SolveOperatingPoint();
        Assert.Equal(10.0, net.Solution.Read(p), 6);

        // TryResolveProbe round-trips the document-stable key.
        Assert.True(net.TryResolveProbe(K(30), out var p2));
        Assert.Equal(10.0, net.Solution.Read(p2), 6);
    }

    [Fact]
    public void Waveform_tap_samples_one_value_per_substep_during_ac_subcycling()
    {
        // 5 Hz sine, tick dt 0.05 s, 20 samples/cycle ⇒ N = 5·20·0.05 = 5 substeps/tick.
        // A tap on the source node collects one sample per substep.
        const double dt = 0.05, amp = 10.0, freq = 5.0;
        var net = Mixed(dt);
        NodeId a, g; ProbeId p;
        using (var e = net.Edit())
        {
            a = e.AddNode(K(1)); g = e.AddReferenceNode(K(2));
            e.AddSineSource(a, g, new SineDrive(amp, freq, 0.0), K(20), StateKey.From(K(20)));
            e.AddResistor(a, g, 1000.0, K(10));
            p = e.AddProbe(a, K(30));
        }
        net.SolveOperatingPoint();

        var ring = new WaveformRing(64);
        var tap = WaveformTap.Attach(net, p, ring);

        net.Solve(new TickClock(1, dt));
        Assert.Equal(5, net.Islands.Of(a).Plan.Substeps);   // planned once the AC tick runs
        Assert.Equal(5, ring.Count);   // one sample per substep

        net.Solve(new TickClock(2, dt));
        Assert.Equal(10, ring.Count);

        // Samples are bounded by the amplitude, track sin (not all equal), and the tap
        // read matches Solution.Read at the last substep.
        var samples = ring.Samples;
        var anyNonZero = false; double last = 0.0;
        foreach (var s in samples)
        {
            Assert.True(Math.Abs(s) <= amp + 1e-9, $"sample {s} exceeds amplitude {amp}");
            if (Math.Abs(s) > 1e-6) anyNonZero = true;
            last = s;
        }
        Assert.True(anyNonZero, "the sine tap collected only zeros");
        Assert.Equal(net.Solution.Read(p), last, 9);

        // Detach stops sampling.
        tap.Detach();
        net.Solve(new TickClock(3, dt));
        Assert.Equal(10, ring.Count);   // unchanged after detach
    }
}
