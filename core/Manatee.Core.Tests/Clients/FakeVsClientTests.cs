using System;
using Manatee.Core;

namespace Manatee.Core.Tests.Clients;

/// <summary>
/// Drives <see cref="FakeVsClient"/> through the §22.b scenario (api.md §22.b;
/// testing-strategy.md "the 2D harness is the integration test bed"): 500 electrical
/// ticks of a subcycled AC island beside a steady DC island, a chiseled cable run
/// mid-run, and a fuse melt routed through limit attribution — with KCL + finiteness
/// invariants sampled every 50 ticks and the standing StaleHandleReads == 0 telemetry
/// contract asserted on every tick.
/// </summary>
public sealed class FakeVsClientTests
{
    // Run the full 500-tick scenario once; return the driven client for assertions.
    private static FakeVsClient RunScenario()
    {
        var c = new FakeVsClient();
        for (var t = 0; t < 500; t++)
        {
            if (t == 150) c.QueueChisel();
            if (t == 300) c.QueueFuseTrip();
            c.Tick();

            if (t % 50 == 0)
            {
                var ac = c.AcInvariants();
                Assert.True(ac.AllFinite, $"tick {t}: AC island went non-finite");
                Assert.True(ac.MaxKclResidual < 1e-6, $"tick {t}: AC KCL residual {ac.MaxKclResidual:R}");
                var dc = c.DcInvariants();
                Assert.True(dc.AllFinite, $"tick {t}: DC island went non-finite");
                Assert.True(dc.MaxKclResidual < 1e-6, $"tick {t}: DC KCL residual {dc.MaxKclResidual:R}");
            }
        }
        return c;
    }

    [Fact]
    public void Full_scenario_holds_invariants_and_never_reads_a_stale_handle()
    {
        var c = RunScenario();

        // The device-bearing AC island is never edited, so its cached handles stay valid:
        // not one stale-handle read across 500 ticks (api.md §16, §22.b telemetry).
        Assert.Equal(0, c.TotalStaleReads);

        // The AC island genuinely subcycled (a ~5 Hz sine at dt = 0.05 s ⇒ N ≈ 5).
        Assert.True(c.MaxSubsteps >= 3, $"AC island did not subcycle (MaxSubsteps={c.MaxSubsteps})");
        Assert.InRange(c.Alt.FrequencyHz, 2.0, 9.0);
    }

    [Fact]
    public void Alternator_drives_an_ac_waveform_the_scope_tap_captures()
    {
        var c = RunScenario();

        // The setup-time WaveformTap filled its ring per substep (0 B on the hot path).
        var samples = c.AcRing.Samples;
        Assert.True(samples.Length > 10, $"scope ring underfilled ({samples.Length} samples)");

        // A genuine AC swing: the interior probe went both clearly positive and negative.
        double max = double.NegativeInfinity, min = double.PositiveInfinity;
        foreach (var v in samples) { if (v > max) max = v; if (v < min) min = v; }
        Assert.True(max > 0.2 && min < -0.2, $"probe did not swing AC (min={min:R}, max={max:R})");
        Assert.True(Math.Abs(c.ReadAcProbe()) < 100.0 && !double.IsNaN(c.ReadAcProbe()));
    }

    [Fact]
    public void Chisel_rebuilds_the_run_but_the_interior_probe_keeps_reading()
    {
        var c = new FakeVsClient();
        for (var t = 0; t < 150; t++) c.Tick();
        var before = c.ReadDcProbe();
        Assert.True(before > 10.0, $"DC probe should read the energized run (~18 V), got {before:R}");

        c.QueueChisel();
        c.Tick();                       // chisel commits; the run rebuilds this tick
        for (var t = 0; t < 3; t++) c.Tick();

        Assert.Equal(1, c.Chisels);
        // The dead-end stub was chiseled off; the main path is untouched, so the
        // document-stable probe re-aims itself and reads essentially the same potential.
        var after = c.ReadDcProbe();
        Assert.True(Math.Abs(after - before) < 1.0, $"probe drifted after chisel: {before:R} → {after:R}");
        Assert.Equal(0, c.TotalStaleReads);
    }

    [Fact]
    public void Overcurrent_is_attributed_to_the_fuse_and_melting_it_clears_the_fault()
    {
        var c = new FakeVsClient();
        for (var t = 0; t < 100; t++) c.Tick();

        c.QueueFuseTrip();
        c.Tick();                       // arm (60 V) + solve (10 A) + attribute + melt, this tick
        Assert.Equal(1, c.Melts);
        Assert.Equal(new Manatee.Core.Reduction.SegmentKey(203), c.LastMeltedSegment);
        Assert.True(c.LastMeltMargin > 1.0, $"culprit overload margin should exceed 1, got {c.LastMeltMargin:R}");

        // After the melt opens the run, the source-side probe re-solves to a LEGIBLE value
        // (the open-circuit source potential, no runaway), and no further events fire.
        for (var t = 0; t < 5; t++) c.Tick();
        var v = c.ReadDcProbe();
        Assert.True(v > 0.0 && v < TripSanityCeiling, $"post-melt DC probe not legible: {v:R}");
        Assert.Equal(1, c.Melts);       // melting cleared the fault — it does not re-trip
        Assert.Equal(0, c.TotalStaleReads);
    }

    private const double TripSanityCeiling = 100.0;
}
