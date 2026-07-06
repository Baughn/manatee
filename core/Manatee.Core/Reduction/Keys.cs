using System.Collections.Generic;

namespace Manatee.Core.Reduction;

// Public surface types for the reduction layer (api.md §19). The layer is an
// ordinary public client of Manatee.Core (§2 binding invariant): it holds no
// internal reference and uses only the public Netlist API.

/// <summary>Client-stable identity of one conductor SEGMENT — a voxel run, a cable
/// segment, or a schematic wire (api.md §19). 128-bit like <see cref="ExternalKey"/>
/// so clients pack coordinates/material/RefId without collision anxiety.</summary>
public readonly record struct SegmentKey(ulong Hi, ulong Lo)
{
    /// <summary>Stationeers convenience: a bare RefId in the low word.</summary>
    public SegmentKey(ulong refId) : this(0, refId) { }

    /// <summary>The topological identity this segment's realized element carries.</summary>
    internal ExternalKey External() => new(Hi, Lo);

    /// <summary>Canonical lexicographic (Hi, Lo) order (matches <see cref="ExternalKey"/>).</summary>
    internal int CompareTo(in SegmentKey other)
    {
        if (Hi != other.Hi) return Hi < other.Hi ? -1 : 1;
        if (Lo != other.Lo) return Lo < other.Lo ? -1 : 1;
        return 0;
    }
}

/// <summary>Client-stable identity of one JUNCTION where segments meet (api.md §19).
/// Junctions become netlist nodes (after equipotential region-merge and series
/// collapse).</summary>
public readonly record struct JunctionKey(ulong Hi, ulong Lo)
{
    public JunctionKey(ulong refId) : this(0, refId) { }

    internal ExternalKey External() => new(Hi, Lo);

    /// <summary>Canonical lexicographic (Hi, Lo) order (matches <see cref="ExternalKey"/>).</summary>
    internal int CompareTo(in JunctionKey other)
    {
        if (Hi != other.Hi) return Hi < other.Hi ? -1 : 1;
        if (Lo != other.Lo) return Lo < other.Lo ? -1 : 1;
        return 0;
    }
}

/// <summary>
/// Per-segment conductor description (api.md §19). Resistance = <see cref="OhmsPerLength"/>
/// × <see cref="Length"/>. A zero (or negative) resistance marks a PERFECT CONDUCTOR
/// whose endpoints union into one equipotential region — which is what makes a lead
/// segment in a copper run <i>be</i> a fuse with no special-casing. <see cref="Limits"/>
/// carries the per-segment ampacity / i²t envelope contribution.
/// </summary>
public readonly struct ConductorSpec
{
    public ConductorSpec(double ohmsPerLength, double length, in LimitSpec limits = default)
    {
        OhmsPerLength = ohmsPerLength; Length = length; Limits = limits;
    }

    public readonly double OhmsPerLength;
    public readonly double Length;
    public readonly LimitSpec Limits;

    /// <summary>Ω of this segment (≥ 0). A perfect conductor is exactly 0.</summary>
    public double Resistance => OhmsPerLength * Length > 0.0 ? OhmsPerLength * Length : 0.0;

    /// <summary>True iff this segment unions its endpoints (perfect conductor).</summary>
    public bool IsPerfect => !(OhmsPerLength * Length > 0.0);
}

/// <summary>Which segment a solver limit event actually indicts, per limit type
/// (api.md §19 / R7). <see cref="Margin"/> is the culprit segment's overload factor
/// (observed ÷ its own environment-adjusted threshold; &gt; 1 ⇒ over).</summary>
public readonly struct AttributionResult
{
    public AttributionResult(in SegmentKey segment, LimitKind kind, double margin)
    {
        Segment = segment; Kind = kind; Margin = margin;
    }

    public readonly SegmentKey Segment;
    public readonly LimitKind Kind;
    public readonly double Margin;
}

/// <summary>Graph partitioning mode (api.md §19): PrePartitioned mirrors a
/// ClientPartitioned Netlist (Stationeers — partition mandatory, reference rail per
/// partition); SelfPartitioned mirrors a SelfPartitioned Netlist (VS / tablet).</summary>
public enum GraphPartitioning : byte { SelfPartitioned, PrePartitioned }

/// <summary>Birth configuration for a <see cref="ConductorGraph"/> (api.md §19).
/// The two named values are the misuse-resistant bundles used in the walkthroughs
/// (<c>GraphOptions.PrePartitioned</c> / <c>GraphOptions.SelfPartitioned</c>).</summary>
public readonly struct GraphOptions
{
    /// <summary>Partitioning mode; must agree with the target Netlist's mode.</summary>
    public GraphPartitioning Partitioning { get; init; }

    /// <summary>Stationeers bundle.</summary>
    public static GraphOptions PrePartitioned => new() { Partitioning = GraphPartitioning.PrePartitioned };

    /// <summary>VS / tablet bundle.</summary>
    public static GraphOptions SelfPartitioned => new() { Partitioning = GraphPartitioning.SelfPartitioned };
}

/// <summary>A typed drift finding (api.md §19). Names an <see cref="ExternalKey"/>
/// — a bug report with coordinates, never mystery corruption.</summary>
public enum DriftKind : byte
{
    MissingInLive, MissingInShadow, ValueMismatch, EnvelopeMismatch, ProbeWeightMismatch, PartitionMismatch,
}

/// <summary>One drift finding (api.md §19).</summary>
public readonly struct DriftEntry
{
    public DriftEntry(DriftKind kind, in ExternalKey key, double live, double shadow)
    {
        Kind = kind; Key = key; Live = live; Shadow = shadow;
    }

    public readonly DriftKind Kind;
    public readonly ExternalKey Key;
    public readonly double Live, Shadow;
}

/// <summary>Canonical-diff result (api.md §19): the cold, SaveNormalized-grade full
/// diff between incrementally-maintained live state and the shadow truth. Value
/// snapshot over a captured entry array — <see cref="Drain"/> copies (does not
/// consume), so a caller sizes its span to <see cref="Count"/>.</summary>
public readonly struct DriftReport
{
    private readonly DriftEntry[]? _entries;
    private readonly int _count;

    internal DriftReport(DriftEntry[]? entries, int count) { _entries = entries; _count = count; }

    public bool IsEmpty => _count == 0;
    public int Count => _count;

    /// <summary>Copies up to <c>into.Length</c> entries; returns the number written.</summary>
    public int Drain(System.Span<DriftEntry> into)
    {
        var n = _count < into.Length ? _count : into.Length;
        for (var i = 0; i < n; i++) into[i] = _entries![i];
        return n;
    }
}

/// <summary>Resync outcome (api.md §19): the level-2 drift backstop rebuilt live
/// state from the shadow truth and restored by key.</summary>
public readonly struct ResyncReport
{
    public ResyncReport(int segments, int nodes, int chains, int probesReaimed, bool ok)
    {
        Segments = segments; Nodes = nodes; Chains = chains; ProbesReaimed = probesReaimed; Ok = ok;
    }

    public readonly int Segments, Nodes, Chains, ProbesReaimed;
    public readonly bool Ok;
}

/// <summary>One authoritative segment from an <see cref="IGeometrySource"/>.</summary>
public readonly struct GeometrySegment
{
    public GeometrySegment(in SegmentKey key, in JunctionKey a, in JunctionKey b, in ConductorSpec spec,
                           PartitionKey partition = default, double ambientKelvin = ConductorGraph.NominalAmbientK)
    {
        Key = key; A = a; B = b; Spec = spec; Partition = partition; AmbientKelvin = ambientKelvin;
    }

    public readonly SegmentKey Key;
    public readonly JunctionKey A, B;
    public readonly ConductorSpec Spec;
    public readonly PartitionKey Partition;
    public readonly double AmbientKelvin;
}

/// <summary>The shadow-geometry truth the drift backstop diffs and resyncs against
/// (api.md §19). A game supplies its live voxel/cable adjacency; CI supplies a
/// deterministic fixture.</summary>
public interface IGeometrySource
{
    IEnumerable<GeometrySegment> Segments { get; }
}
