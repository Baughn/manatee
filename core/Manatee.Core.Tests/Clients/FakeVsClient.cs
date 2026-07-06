using System;
using Manatee.Core;
using Manatee.Core.Devices;
using Manatee.Core.Reduction;

namespace Manatee.Core.Tests.Clients;

/// <summary>
/// An executable, assertion-driving stand-in for the Vintage Story client (api.md §22.b).
/// It is deliberately more demanding than a lean first implementation needs: one
/// SelfPartitioned Mixed netlist hosting BOTH a subcycled AC island (a swing-lite
/// <see cref="Alternator"/> at ~5 Hz) and a steady DC island, compacted cable runs with
/// interior interpolated probes and an oscilloscope <see cref="WaveformTap"/>, the full
/// closure-free per-tick schedule (CollectDirty → per-unit Step, a serial loop standing
/// in for the client worker pool), a chiseled-cable-run edit that rebuilds a live run,
/// and a limit event routed through <c>Attribute</c> to melt the culprit segment.
///
/// <para>The AC island carries the <see cref="Alternator"/> device and is NEVER edited,
/// so the device's cached handles stay valid (StaleHandleReads == 0). All topology
/// churn (chisel, melt) lands on the DC run, whose client-held source/probe handles are
/// re-pinned by KEY across the rebuild — the §16 re-resolution the reduction layer and
/// document-stable probes make cheap.</para>
/// </summary>
internal sealed class FakeVsClient
{
    // ── AC-island geometry (a cable run the alternator drives through a lamp) ──
    private static readonly JunctionKey AcStart = new(1000), AcEnd = new(1005);
    private static readonly SegmentKey AcMidSeg = new(103);
    private static readonly ExternalKey EarthAKey = new(0xEA_0001);
    private static readonly ExternalKey LampKey = new(0xA10);
    private static readonly ExternalKey AltKey = new(0xA17);

    // ── DC-island geometry (a chiseled + fused cable run) ──
    private static readonly JunctionKey DcSrc = new(2000), DcLoad = new(2004);
    private static readonly SegmentKey DcFuseSeg = new(203);      // melts on overcurrent
    private static readonly SegmentKey DcStubSeg = new(210);      // the chisel target (a dead-end tap)
    private static readonly SegmentKey DcMidSeg = new(202);       // probe here (source side of the fuse)
    private static readonly ExternalKey EarthDKey = new(0xED_0001);
    private static readonly ExternalKey DcSourceKey = new(0xD50);
    private static readonly ExternalKey DcLoadKey = new(0xD10);

    private const double SafeDcVolts = 24.0;    // 24 V / 6 Ω = 4 A < 5 A fuse
    private const double TripDcVolts = 60.0;    // 60 V / 6 Ω = 10 A > 5 A fuse
    private const double Dt = 0.05;

    public Core.Netlist Net { get; }
    public ConductorGraph Graph { get; }
    public DeviceHost Host { get; }
    public Alternator Alt { get; }

    public ProbeId AcProbe { get; private set; }
    public ProbeId DcProbe { get; private set; }
    public WaveformRing AcRing { get; } = new(256);

    private NodeId _earthA;
    private VSourceId _dcSource;   // re-pinned by key across rebuilds

    // Scratch (shape-time allocation only — the tick loop is 0-alloc on these).
    private readonly IslandChange[] _chgBuf = new IslandChange[64];
    private readonly IslandHandle[] _dirtyBuf = new IslandHandle[16];
    private readonly LimitEvent[] _limBuf = new LimitEvent[16];

    private long _tick;

    // Scripted pending events (applied in the next tick's topology phase).
    private bool _pendingChisel, _pendingTripArm;
    private bool _fuseArmed;

    // ── Telemetry the tests assert against ──
    public int Chisels { get; private set; }
    public int Melts { get; private set; }
    public int FullRepins { get; private set; }
    public long TotalStaleReads { get; private set; }
    public int MaxSubsteps { get; private set; }
    public SegmentKey LastMeltedSegment { get; private set; }
    public double LastMeltMargin { get; private set; }

    public FakeVsClient()
    {
        Net = new Core.Netlist(NetlistOptions.VintageStory(Dt));
        Graph = new ConductorGraph(Net, GraphOptions.SelfPartitioned);
        Host = new DeviceHost(Net);
        Alt = BuildAlternator();

        BuildGeometry();
        WireSourcesAndDevices();
        Net.SolveOperatingPoint();
        PlaceProbesAndTap();
    }

    private static Alternator BuildAlternator()
        // Strong governor holds the rotor near ω ≈ 2π·5 rad/s ⇒ ~5 Hz electrical.
        => new(inertia: 0.4, damping: 0.05, polePairs: 1.0, emfPerOmega: 0.38,
               seriesOhms: 0.5, governorGain: 10.0, initialOmega: 2.0 * Math.PI * 5.0);

    private void BuildGeometry()
    {
        using var b = Graph.BeginBulkBuild(16);
        // AC run: J1000 - J1001 - ... - J1005 (1 Ω each ⇒ 5 Ω of cable).
        for (ulong i = 0; i < 5; i++)
            b.AddSegment(new SegmentKey(101 + i), new JunctionKey(1000 + i), new JunctionKey(1001 + i),
                         new ConductorSpec(1.0, 1.0));
        // DC run: J2000 - J2001 - J2002 -(FUSE)- J2003 - J2004, plus a stub tap J2002 - J3000.
        b.AddSegment(new SegmentKey(201), new JunctionKey(2000), new JunctionKey(2001), new ConductorSpec(1.0, 1.0));
        b.AddSegment(new SegmentKey(202), new JunctionKey(2001), new JunctionKey(2002), new ConductorSpec(1.0, 1.0));
        b.AddSegment(DcFuseSeg, new JunctionKey(2002), new JunctionKey(2003),
                     new ConductorSpec(1.0, 1.0, new LimitSpec(5.0, 0.0, 0.0, default)));   // 5 A fuse
        b.AddSegment(new SegmentKey(204), new JunctionKey(2003), new JunctionKey(2004), new ConductorSpec(1.0, 1.0));
        b.AddSegment(DcStubSeg, new JunctionKey(2002), new JunctionKey(3000), new ConductorSpec(1.0, 1.0));
    }

    private void WireSourcesAndDevices()
    {
        using (var e = Net.Edit())
        {
            _earthA = e.AddReferenceNode(EarthAKey);
            var earthD = e.AddReferenceNode(EarthDKey);
            // DC source + load close the DC run against its own earth.
            _dcSource = e.AddVoltageSource(Graph.PortNode(DcSrc), earthD, SafeDcVolts, DcSourceKey);
            e.AddResistor(Graph.PortNode(DcLoad), earthD, 2.0, DcLoadKey);
        }
        // AC island: the alternator drives the run; a lamp returns it to earthA. The
        // alternator lives on this island, which is never edited afterwards.
        Host.Add(Alt, stackalloc NodeId[2] { Graph.PortNode(AcStart), ResolveNode(EarthAKey) }, AltKey, StateKey.From(AltKey));
        using (var e = Net.Edit())
            e.AddResistor(Graph.PortNode(AcEnd), ResolveNode(EarthAKey), 5.0, LampKey);
    }

    private void PlaceProbesAndTap()
    {
        AcProbe = Graph.AddProbe(AcMidSeg, 0.5);
        DcProbe = Graph.AddProbe(DcMidSeg, 0.5);
        Net.SolveOperatingPoint();
        // Cold, once, at setup (§13): the tap samples per substep into a caller-owned ring.
        WaveformTap.Attach(Net, AcProbe, AcRing);
    }

    private NodeId ResolveNode(in ExternalKey key)
    {
        Net.TryResolveNode(key, out var n);
        return n;
    }

    // ── scripting ──
    public void QueueChisel() => _pendingChisel = true;
    public void QueueFuseTrip() => _pendingTripArm = true;

    /// <summary>One VS electrical tick in the canonical §22.b order.</summary>
    public void Tick()
    {
        var clock = new TickClock(_tick++, Dt);

        // 1. Topology phase (chisel / any structural edits) BEFORE the schedule.
        if (_pendingChisel)
        {
            Graph.RemoveSegment(DcStubSeg);   // player chisels the dead-end tap off the live run
            _pendingChisel = false;
            Chisels++;
        }
        if (_pendingTripArm)
        {
            // Re-pin the DC source by key (it may have been reissued by the chisel rebuild),
            // then drive it into overcurrent — the arming edit is a tier-1 RHS write.
            RepinDcSource();
            Net.Drive(_dcSource, TripDcVolts);
            _pendingTripArm = false;
            _fuseArmed = true;
        }

        // 2. Re-pin phase — drain membership changes; a lost ring obliges a full re-pin.
        var n = Net.Islands.DrainChanges(_chgBuf, out var lost);
        if (lost) { RepinDcSource(); FullRepins++; }
        // (Probes are document-stable across rebuilds — no re-resolve needed here; §16.)

        // 3. Drive phase — the alternator advances its rotor and re-drives the sine,
        //    which re-dirties the AC island so CollectDirty picks it up (device-driven
        //    schedule). Serial stand-in for the client worker pool.
        Alt.SetMechanical(shaftSpeed: 2.0 * Math.PI * 5.0, availableTorque: 0.2);
        Host.Tick(Dt);

        // 4. Schedule phase — CollectDirty gives one handle per scheduling unit; Step each.
        var dirty = Net.Islands.CollectDirty(_dirtyBuf);
        for (var i = 0; i < dirty; i++) _dirtyBuf[i].Step(clock);

        // 5. Post-tick — limit events → Attribute → melt; telemetry.
        DrainLimitsAndMelt();
        ref readonly var stats = ref Net.LastTickStats;
        TotalStaleReads += stats.StaleHandleReads;
        if (stats.Substeps > MaxSubsteps) MaxSubsteps = stats.Substeps;
    }

    private void RepinDcSource()
    {
        if (Net.TryResolve(DcSourceKey, out var c))
            _dcSource = new VSourceId(c.Slot, c.Gen, c.Net);
    }

    private void DrainLimitsAndMelt()
    {
        if (!_fuseArmed) return;
        // Scan every live island's post-solve limit ring; attribute an equivalent-element
        // event to the culprit segment and melt it (RemoveSegment) — R7 legibility.
        var count = Net.Islands.Count;
        for (var idx = 0; idx < count; idx++)
        {
            var isl = Net.Islands[idx];
            var got = isl.DrainLimitEvents(_limBuf);
            for (var k = 0; k < got; k++)
            {
                if (_limBuf[k].Kind != LimitKind.OverCurrent) continue;
                if (!Graph.Attribute(in _limBuf[k], out var attr)) continue;
                Graph.RemoveSegment(attr.Segment);   // the segment melts and opens the run
                LastMeltedSegment = attr.Segment;
                LastMeltMargin = attr.Margin;
                Melts++;
                _fuseArmed = false;
                return;
            }
        }
    }

    // ── readback helpers for the tests ──
    public double ReadAcProbe() => Net.Solution.Read(AcProbe);
    public double ReadDcProbe() => Net.Solution.Read(DcProbe);

    public InvariantReport AcInvariants()
        => Net.Islands.Of(Graph.PortNode(AcStart)).CheckInvariants(InvariantChecks.Kcl | InvariantChecks.Finiteness);

    public InvariantReport DcInvariants()
        => Net.Islands.Of(Graph.PortNode(DcSrc)).CheckInvariants(InvariantChecks.Kcl | InvariantChecks.Finiteness);
}
