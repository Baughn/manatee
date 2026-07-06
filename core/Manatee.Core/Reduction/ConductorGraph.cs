using System;
using System.Collections.Generic;

namespace Manatee.Core.Reduction;

/// <summary>
/// The reduction layer (api.md §19; compaction.md; design.md R10/R11). Clients
/// describe conductor GEOMETRY at natural granularity — voxel runs, cable segments,
/// schematic wires — and this layer delivers a MINIMAL netlist plus the maps needed
/// to route observations and limit events back to that geometry:
///
/// <list type="bullet">
/// <item>equipotential region-building (perfect-conductor union),</item>
/// <item>series-chain collapse of no-tap runs into single equivalent resistors,</item>
/// <item>per-limit-type limit ENVELOPES on collapsed elements (the min over
///   constituents, per limit type — ampacity, i²t mass and melting can each pick a
///   <i>different</i> segment: the lead-fuse-in-a-copper-run case),</item>
/// <item>probe interpolation by cumulative-resistance fraction so instruments read
///   "inside" a compacted run,</item>
/// <item>attribution of an equivalent-element limit event to the culprit segment,</item>
/// <item>a two-level drift backstop (<see cref="Diff"/> / <see cref="Resync"/>).</item>
/// </list>
///
/// <para><b>Binding invariant (api.md §2):</b> this is an ORDINARY PUBLIC CLIENT of
/// <see cref="Netlist"/>. It holds no internal handle, gets no
/// <c>InternalsVisibleTo</c> friendship, and uses only the public Core surface —
/// <c>Edit</c>/<c>Meta</c>/<c>Solution</c>/<c>TryResolve*</c>/journal/snapshot. There
/// is no second, untested path into the solver.</para>
///
/// <para><b>Maintenance model.</b> The from-scratch compaction of the affected graph
/// is the correctness backbone (compaction.md: "always available and cheap at island
/// scale"). Incremental edits update a shadow and recompact; the resulting netlist
/// document changes coalesce into ONE matrix rebuild at the netlist's next Solve, so
/// a deconstruction burst still costs a single rebuild. Not thread-safe: drive a
/// graph from one thread (single-writer-per-island, api.md §21).</para>
/// </summary>
public sealed partial class ConductorGraph
{
    /// <summary>Rating reference temperature (K) — thresholds in a
    /// <see cref="LimitSpec"/> are stated at this ambient (25 °C ≈ 300 K).</summary>
    public const double NominalAmbientK = 300.0;

    // Standing conductor temperature ceiling used by the ambient derating model. A
    // documented module constant (not per-material yet): the ampacity of a run scales
    // with the square root of the head-room to this ceiling, the i²t melt integral
    // linearly — so a hot room lowers both without editing any LimitSpec.
    private const double MeltRefK = 1300.0;

    private readonly Netlist _net;
    private readonly bool _prePartitioned;

    // ── Shadow geometry: the layer's own source of truth ──
    private struct Seg
    {
        public JunctionKey A, B;
        public double Ohms;              // resistive; 0 ⇒ perfect conductor (region union)
        public LimitSpec Limits;
        public ulong Partition;          // PrePartitioned only
        public double AmbientK;
    }

    private readonly Dictionary<SegmentKey, Seg> _segs = new();
    private readonly Dictionary<JunctionKey, ulong> _junctionPartition = new();
    private readonly HashSet<JunctionKey> _protected = new();          // ports + references + refs (never collapsed)
    private readonly HashSet<JunctionKey> _referenceJunctions = new(); // subset of _protected: reference rails
    private readonly Dictionary<ulong, JunctionKey> _refJunction = new();

    // ── Realization (result of the last compaction pass) ──
    private sealed class Chain
    {
        public ExternalKey EquivKey;
        public SegmentKey Rep;
        public readonly List<SegmentKey> Segments = new();
        public readonly List<bool> Forward = new();   // segment traversed A→B (P-side is A)?
        public double[] CumRStart = Array.Empty<double>();
        public double TotalR;
        public JunctionKey P, Q;          // boundary region reps, P ≤ Q by key
        public bool Loop;                 // P == Q ⇒ shorted self-loop; not stamped
        public LimitSpec Envelope;        // instantaneous classes (exact minima); Thermal stays default
        // Thermal envelope: the Pareto-minimal (rating, melt, tau) set over the
        // constituents (api.md §19 ruling 2026-07-06) + the representative culprit
        // segment per pair (min segment key among identical materials), ordered by
        // that segment key so same membership ⇒ same pair indices across recomputes.
        public I2tPair[] EnvPairs = Array.Empty<I2tPair>();
        public SegmentKey[] EnvSegs = Array.Empty<SegmentKey>();
    }

    private List<Chain> _chains = new();
    private readonly Dictionary<SegmentKey, int> _segToChain = new();
    private readonly Dictionary<JunctionKey, JunctionKey> _regionOf = new();   // junction → region rep

    // What is currently realized in the netlist, so recompaction can diff against it
    // and touch ONLY genuinely-changed nodes/chains — a redundant recompaction (e.g.
    // declaring a port on an already-boundary junction) then emits zero edits and so
    // triggers no island rebuild, keeping client-held handles valid.
    private readonly HashSet<ExternalKey> _liveNodeKeys = new();
    private readonly Dictionary<ExternalKey, ChainSig> _liveChains = new();

    private readonly struct ChainSig
    {
        public ChainSig(in JunctionKey p, in JunctionKey q, double r, in LimitSpec env,
                        I2tPair[] pairs, SegmentKey[] pairSegs)
        { P = p; Q = q; R = r; Env = env; Pairs = pairs; PairSegs = pairSegs; }
        public readonly JunctionKey P, Q;
        public readonly double R;
        public readonly LimitSpec Env;
        public readonly I2tPair[] Pairs;
        public readonly SegmentKey[] PairSegs;
        public bool SameShape(in ChainSig o) => P.Equals(o.P) && Q.Equals(o.Q) && R == o.R;

        public bool SameEnvelope(in ChainSig o)
        {
            if (!Env.Equals(o.Env) || Pairs.Length != o.Pairs.Length) return false;
            for (var i = 0; i < Pairs.Length; i++)
                if (!Pairs[i].Equals(o.Pairs[i]) || !PairSegs[i].Equals(o.PairSegs[i])) return false;
            return true;
        }
    }

    // ── Probes ──
    private sealed class ProbeRec
    {
        public SegmentKey Seg;
        public double Along;
        public ProbeId Id;
        public ExternalKey Key;
    }

    private readonly Dictionary<ExternalKey, ProbeRec> _probes = new();
    private readonly List<ProbeRec> _probeList = new();

    // ── Attribution cache (component slot → chain index) ──
    private readonly Dictionary<int, int> _slotToChain = new();
    private bool _slotMapDirty = true;

    private bool _dirty;
    private bool _bulk;
    private int _compactionPasses;

    // Journal sync cursor (SyncFromReceipt): the standing cursor + overflow flag.
    private JournalCursor _cursor;
    private bool _cursorOpen;
    private bool _resyncNeeded;

    private static readonly ExternalKey RefKeyHiTag = new(0xFFFF_FFFF_FFFF_FFFDUL, 0);

    /// <param name="target">The netlist this graph populates. Its
    /// <see cref="PartitioningMode"/> must match <paramref name="opts"/>.</param>
    public ConductorGraph(Netlist target, in GraphOptions opts)
    {
        _net = target ?? throw new ArgumentNullException(nameof(target));
        _prePartitioned = opts.Partitioning == GraphPartitioning.PrePartitioned;
        _cursor = target.Journal.OpenCursor();
        _cursorOpen = true;
    }

    // ============================================================ introspection

    /// <summary>Whether <see cref="SyncFromReceipt"/> has seen a lapped window and a
    /// <see cref="Resync"/> is owed (api.md §19/§20).</summary>
    public bool ResyncNeeded => _resyncNeeded;

    internal int CompactionPasses => _compactionPasses;
    internal int LiveNodeCount => _liveNodeKeys.Count;
    internal int LiveChainCount => _liveChains.Count;
    internal int ChainCount => _chains.Count;

    // ============================================================ bulk build

    /// <summary>Open a load scope (api.md §19). Segments staged inside it skip eager
    /// recompaction; a SINGLE compaction pass runs at <see cref="BulkBuild.Dispose"/>.
    /// The <paramref name="expectedSegments"/> hint presizes the staging maps.</summary>
    public BulkBuild BeginBulkBuild(int expectedSegments = 0)
    {
        if (_bulk) throw new InvalidOperationException("A BulkBuild is already open (nesting is forbidden).");
        if (expectedSegments > _segs.Count) _segs.EnsureCapacity(expectedSegments);
        _bulk = true;
        return new BulkBuild(this);
    }

    /// <summary>Load scope for <see cref="ConductorGraph.BeginBulkBuild"/> (api.md §19).
    /// One compaction pass at <see cref="Dispose"/>.</summary>
    public sealed class BulkBuild : IDisposable
    {
        private readonly ConductorGraph _g;
        private bool _open;

        internal BulkBuild(ConductorGraph g) { _g = g; _open = true; }

        /// <summary>Stage one segment (identical signature to
        /// <see cref="ConductorGraph.AddSegment"/>); no netlist edit until Dispose.</summary>
        public void AddSegment(in SegmentKey k, in JunctionKey a, in JunctionKey b, in ConductorSpec spec,
                               PartitionKey partition = default)
            => _g.StageSegment(k, a, b, spec, partition);

        public void Dispose()
        {
            if (!_open) return;
            _open = false;
            _g._bulk = false;
            _g.EnsureCompacted();
        }
    }

    // ============================================================ geometry verbs

    /// <summary>Add or replace one segment (api.md §19). Outside a
    /// <see cref="BeginBulkBuild"/> scope this recompacts eagerly so the netlist is
    /// coherent before the client's next <c>Solve</c>. ClientPartitioned:
    /// <paramref name="partition"/> is MANDATORY — a junction claimed by two
    /// partitions throws <see cref="PartitionMergeException"/>, and the partition's
    /// reference rail is created on first use so <see cref="ReferenceNode"/> always
    /// resolves. SelfPartitioned: <paramref name="partition"/> is ignored.</summary>
    public void AddSegment(in SegmentKey k, in JunctionKey a, in JunctionKey b, in ConductorSpec spec,
                           PartitionKey partition = default)
    {
        StageSegment(k, a, b, spec, partition);
        EnsureCompacted();
    }

    /// <summary>Remove one segment (api.md §19). Dirties the graph; the resulting
    /// netlist rebuild coalesces to the next Solve (N cuts ⇒ 1 rebuild).</summary>
    public void RemoveSegment(in SegmentKey k)
    {
        if (_segs.Remove(k)) _dirty = true;
        EnsureCompacted();
    }

    private void StageSegment(in SegmentKey k, in JunctionKey a, in JunctionKey b, in ConductorSpec spec,
                              PartitionKey partition)
    {
        if (a == b) throw new ArgumentException($"Segment {Fmt(k)} is a self-loop (a == b).", nameof(a));

        ulong part = 0;
        if (_prePartitioned)
        {
            if (partition.IsNone)
                throw new InvalidOperationException(
                    $"ClientPartitioned: segment {Fmt(k)} needs a non-default partition (api.md §19).");
            part = partition.Value;
            TagJunction(a, part);
            TagJunction(b, part);
            EnsureReferenceRail(part);
        }

        _segs[k] = new Seg
        {
            A = a, B = b, Ohms = spec.Resistance, Limits = spec.Limits, Partition = part,
            AmbientK = NominalAmbientK,
        };
        _dirty = true;
    }

    private void TagJunction(in JunctionKey j, ulong part)
    {
        if (_junctionPartition.TryGetValue(j, out var existing))
        {
            if (existing != part && existing != 0)
                throw new PartitionMergeException(new PartitionKey(existing), new PartitionKey(part));
        }
        _junctionPartition[j] = part;
    }

    private void EnsureReferenceRail(ulong part)
    {
        if (_refJunction.ContainsKey(part)) return;
        var refJ = new JunctionKey(RefKeyHiTag.Hi, part);
        _refJunction[part] = refJ;
        _referenceJunctions.Add(refJ);
        _protected.Add(refJ);
        _junctionPartition[refJ] = part;
        _dirty = true;
    }

    // ============================================================ node access

    /// <summary>The netlist node a junction resolves to after compaction (api.md §19).
    /// Devices attach here. Marks the junction a TAP (protected from collapse) and
    /// recompacts if needed, so the returned node always exists. Must not be called
    /// inside a client <c>Edit</c> for a junction that is currently collapsed (the
    /// split needs its own edit); a genuine node (endpoint / branch / prior tap)
    /// resolves without an edit.</summary>
    public NodeId PortNode(in JunctionKey j)
    {
        if (_protected.Add(j)) _dirty = true;          // newly-protected ⇒ recompaction owes it a node
        EnsureCompacted();
        var rep = RegionRepOf(j);
        if (_net.TryResolveNode(rep.External(), out var n)) return n;
        // Junction had no segments and was not yet realized — recompaction just added it.
        EnsureCompacted();
        _net.TryResolveNode(RegionRepOf(j).External(), out n);
        return n;
    }

    /// <summary>The partition's reference rail node (api.md §19; Stationeers datum).
    /// PrePartitioned only.</summary>
    public NodeId ReferenceNode(PartitionKey p)
    {
        if (!_prePartitioned)
            throw new InvalidOperationException("ReferenceNode is PrePartitioned-only (SelfPartitioned has no rail).");
        if (p.IsNone) throw new ArgumentException("ReferenceNode needs a real partition.", nameof(p));
        EnsureReferenceRail(p.Value);
        EnsureCompacted();
        _net.TryResolveNode(_refJunction[p.Value].External(), out var n);
        return n;
    }

    private JunctionKey RegionRepOf(in JunctionKey j)
        => _regionOf.TryGetValue(j, out var rep) ? rep : j;

    // ============================================================ probes

    /// <summary>Place an interpolated probe at fractional position
    /// <paramref name="along"/> ∈ [0,1] of segment <paramref name="k"/> (api.md §19).
    /// It survives series collapse: the read interpolates by cumulative-resistance
    /// fraction along the collapsed chain, so an instrument reads "inside" a compacted
    /// run. The <see cref="ProbeId"/> is document-stable (re-aimed, never re-issued,
    /// across ordinary recompaction AND a <see cref="Resync"/>; §16). The probe's
    /// <see cref="ExternalKey"/> is DERIVED from <c>(segment, along, ordinal)</c> —
    /// see <see cref="ProbeKey"/> — so a client can re-derive it and re-resolve via
    /// <c>TryResolveProbe</c> after a <c>FromCanonical</c> reload or a re-driven
    /// intake (api.md §13/§19).</summary>
    public ProbeId AddProbe(in SegmentKey k, double along)
    {
        if (!_segs.ContainsKey(k))
            throw new ArgumentException($"AddProbe: unknown segment {Fmt(k)}.", nameof(k));
        along = along < 0 ? 0 : along > 1 ? 1 : along;
        EnsureCompacted();

        // Ordinal disambiguates co-located probes (same segment, same quantized
        // along) in AddProbe call order; the first is ordinal 0, so the common
        // one-probe-per-spot client can re-derive its key from geometry alone.
        var ord = 0;
        var q = QuantizeAlong(along);
        foreach (var pr in _probeList)
            if (pr.Seg.Equals(k) && QuantizeAlong(pr.Along) == q) ord++;

        var key = ProbeKey(k, along, ord);
        var rec = new ProbeRec { Seg = k, Along = along, Key = key };
        ComputeProbeAim(rec, out var repP, out var repQ, out var t);

        using (var e = _net.Edit())
        {
            _net.TryResolveNode(repP.External(), out var na);
            _net.TryResolveNode(repQ.External(), out var nb);
            rec.Id = e.AddInterpolatedProbe(na, nb, t, key);
        }
        _probes[key] = rec;
        _probeList.Add(rec);
        return rec.Id;
    }

    /// <summary>The deterministic, TOPOLOGICAL identity a reduction-owned probe
    /// carries (api.md §3/§13/§19): derived from <c>(segment, along, ordinal)</c>, so
    /// two graphs built from the same geometry mint the same key and a client can
    /// reconstruct it — never a call-order counter. <paramref name="along"/> is
    /// quantized to 1e-9 (finer than any physical placement); <paramref name="ordinal"/>
    /// counts earlier co-located probes (0 for the first). Collision posture: the low
    /// word is a splitmix64 avalanche of the 192 input bits under the reserved
    /// <c>0xFFFF_FFFF_FFFF_FFFC</c> Hi tag — a same-graph collision is birthday-scale
    /// (~2⁻³² at 100k probes) and surfaces as the debug duplicate-ExternalKey throw at
    /// AddProbe, never as silent aliasing.</summary>
    public static ExternalKey ProbeKey(in SegmentKey k, double along, int ordinal = 0)
    {
        var q = QuantizeAlong(along < 0 ? 0 : along > 1 ? 1 : along);
        var h = Mix64(k.Hi ^ 0x9E37_79B9_7F4A_7C15UL);
        h = Mix64(h ^ k.Lo);
        h = Mix64(h ^ q);
        h = Mix64(h ^ (ulong)(uint)ordinal);
        return new ExternalKey(ProbeKeyHiTag, h);
    }

    private const ulong ProbeKeyHiTag = 0xFFFF_FFFF_FFFF_FFFCUL;

    private static ulong QuantizeAlong(double along)
        => (ulong)System.Math.Round(along * 1_000_000_000.0);

    // splitmix64 finalizer: shifts/xors/multiplies only — bit-identical on every
    // runtime the core targets (the determinism rule; no libm, no FP).
    private static ulong Mix64(ulong z)
    {
        z = (z ^ (z >> 30)) * 0xBF58_476D_1CE4_E5B9UL;
        z = (z ^ (z >> 27)) * 0x94D0_49BB_1331_11EBUL;
        return z ^ (z >> 31);
    }

    // ============================================================ ambient / limits

    /// <summary>Set one segment's ambient temperature (K) (api.md §19). Envelope
    /// recompute ONLY — the equivalent element's <see cref="LimitSpec"/> is re-pushed
    /// through <c>Meta.SetLimits</c> (tier 0); never a matrix change.</summary>
    public void SetAmbient(in SegmentKey k, double kelvin)
    {
        if (!_segs.TryGetValue(k, out var s)) return;
        s.AmbientK = kelvin;
        _segs[k] = s;
        EnsureCompacted();
        if (_segToChain.TryGetValue(k, out var ci))
            PushEnvelope(_chains[ci]);
    }

    // ============================================================ attribution

    /// <summary>Answer WHICH segment a solver limit event on an equivalent element
    /// indicts (api.md §19 / R7): current density at the narrowest cross-section, per
    /// limit type. Ampacity, i²t mass and melting may each name a DIFFERENT segment
    /// in a mixed-material chain. 0 heap allocation. False if the event's source is
    /// not one of this graph's equivalent resistors.</summary>
    public bool Attribute(in LimitEvent e, out AttributionResult a)
    {
        RefreshSlotMap();
        if (!_slotToChain.TryGetValue(e.Source.Slot, out var ci)) { a = default; return false; }
        var chain = _chains[ci];

        // ThermalI2t: the event names the tripping Pareto pair (api.md §12/§19
        // ruling 2026-07-06) and the pair maps straight to its culprit segment —
        // the accumulator IS that segment's melting integral, so no scan and no
        // narrowest-threshold heuristic. Margin = observed integral ÷ its own
        // effective melt threshold (the event carries both).
        if (e.Kind == LimitKind.ThermalI2t && (uint)e.PairIndex < (uint)chain.EnvSegs.Length)
        {
            var m = e.Threshold > 0 ? e.Observed / e.Threshold : 0.0;
            a = new AttributionResult(chain.EnvSegs[e.PairIndex], e.Kind, m);
            return true;
        }

        SegmentKey culprit = default;
        var found = false;
        var best = double.PositiveInfinity;
        double threshold = 0;

        foreach (var sk in chain.Segments)
        {
            var s = _segs[sk];
            double crit;
            double th;
            switch (e.Kind)
            {
                case LimitKind.OverCurrent:
                    if (!(s.Limits.MaxCurrent > 0)) continue;
                    th = s.Limits.MaxCurrent * DerateCurrent(s.AmbientK);
                    crit = th;                                    // narrowest ampacity
                    break;
                case LimitKind.ThermalI2t:
                    // Fallback only (no envelope mapping — e.g. a stale event drained
                    // across a resync): least effective thermal mass melts first.
                    if (!(s.Limits.Thermal.MeltI2t > 0)) continue;
                    th = s.Limits.Thermal.MeltI2t * DerateEnergy(s.AmbientK);
                    crit = th;
                    break;
                case LimitKind.OverVoltage:
                    if (!(s.Limits.MaxVoltage > 0) || !(s.Ohms > 0)) continue;
                    th = s.Limits.MaxVoltage;
                    crit = th / s.Ohms;                           // smallest critical current
                    break;
                default: // OverPower
                    if (!(s.Limits.MaxPower > 0) || !(s.Ohms > 0)) continue;
                    th = s.Limits.MaxPower;
                    crit = th / s.Ohms;                           // smallest MaxP/R
                    break;
            }
            if (crit < best) { best = crit; culprit = sk; threshold = th; found = true; }
        }

        if (!found) { a = default; return false; }
        var margin = SegmentMargin(e, chain, culprit, threshold);
        a = new AttributionResult(culprit, e.Kind, margin);
        return true;
    }

    // Overload factor at the culprit segment: observed ÷ its own threshold (> 1 ⇒ over).
    private double SegmentMargin(in LimitEvent e, Chain chain, in SegmentKey culprit, double threshold)
    {
        if (!(threshold > 0)) return 0;
        var s = _segs[culprit];
        switch (e.Kind)
        {
            case LimitKind.OverCurrent:
                return e.Observed / threshold;                    // Observed = |I| through the series chain
            case LimitKind.ThermalI2t:
                return e.Observed / threshold;                    // Observed = i²t accumulator
            case LimitKind.OverVoltage:
            {
                // Observed = |V| across the equivalent = I·TotalR ⇒ segment V = Observed·Rseg/TotalR.
                var segV = chain.TotalR > 0 ? e.Observed * s.Ohms / chain.TotalR : e.Observed;
                return segV / threshold;
            }
            default:
            {
                // Observed = I²·TotalR ⇒ segment P = Observed·Rseg/TotalR.
                var segP = chain.TotalR > 0 ? e.Observed * s.Ohms / chain.TotalR : e.Observed;
                return segP / threshold;
            }
        }
    }

    private void RefreshSlotMap()
    {
        if (!_slotMapDirty) return;
        _slotToChain.Clear();
        for (var i = 0; i < _chains.Count; i++)
        {
            var c = _chains[i];
            if (c.Loop) continue;
            if (_net.TryResolve(c.EquivKey, out var cref)) _slotToChain[cref.Slot] = i;
        }
        _slotMapDirty = false;
    }

    // ============================================================ journal sync

    /// <summary>Advance the standing journal cursor over the receipt window and flag
    /// a resync if the reducer has lagged past the ring (api.md §19). The shadow is
    /// authoritative, so this tracks the cursor and overflow rather than replaying
    /// events into maps.</summary>
    public void SyncFromReceipt(in EditReceipt r)
    {
        if (r.WindowLapped) { _resyncNeeded = true; return; }
        if (!_cursorOpen) { _cursor = _net.Journal.OpenCursorAt(r.JournalFrom); _cursorOpen = true; }
        if (_net.Journal.Overflowed(_cursor)) { _resyncNeeded = true; return; }
        var c = _net.Journal.OpenCursorAt(r.JournalFrom);
        while (_net.Journal.TryReadRange(ref c, r.JournalTo, out _)) { }
        if (_net.Journal.Overflowed(c)) { _resyncNeeded = true; return; }
        _cursor = c;
    }

    // ============================================================ ambient model

    // Ampacity derates with the square root of head-room to the temperature ceiling;
    // the i²t melt integral derates linearly (available ΔT before melting). Both are
    // 1.0 at NominalAmbientK and monotonically fall as ambient rises (a hotter room
    // trips a cable/fuse sooner) — the exact formulas the fuse/ambient tests compute.
    internal static double DerateCurrent(double kelvin)
    {
        var head = (MeltRefK - kelvin) / (MeltRefK - NominalAmbientK);
        return head > 0 ? Math.Sqrt(head) : 0;
    }

    internal static double DerateEnergy(double kelvin)
    {
        var head = (MeltRefK - kelvin) / (MeltRefK - NominalAmbientK);
        return head > 0 ? head : 0;
    }

    // ============================================================ helpers

    private void EnsureCompacted()
    {
        if (_dirty && !_bulk) Recompact();
    }

    private static bool KeyLess(in JunctionKey a, in JunctionKey b) => a.CompareTo(b) < 0;

    private static string Fmt(in SegmentKey k) => $"{k.Hi:X}:{k.Lo:X}";
}
