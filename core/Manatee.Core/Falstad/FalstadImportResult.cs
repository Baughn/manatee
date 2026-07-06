using System;
using System.Collections.Generic;
using System.Text;

namespace Manatee.Core.Falstad;

/// <summary>
/// One element the importer refused to build. The accept/reject contract
/// (falstad-format.md §7) is that nothing is ever silently dropped: every
/// unsupported dump type, malformed line, or unrepresentable parameter surfaces
/// here with its 1-based line number, the offending first-token code, and a
/// one-line reason. A non-empty set of these aborts the whole import via
/// <see cref="FalstadImportException"/> — the import is all-or-nothing, so a
/// rejection never leaves a half-built <see cref="Netlist"/>.
/// </summary>
public readonly struct FalstadRejection
{
    internal FalstadRejection(int line, string code, string reason, string rawLine)
    {
        Line = line; Code = code; Reason = reason; RawLine = rawLine;
    }

    /// <summary>1-based line number in the source text.</summary>
    public int Line { get; }

    /// <summary>The dump-type token (first token of the line) that was rejected.</summary>
    public string Code { get; }

    /// <summary>Human-readable one-line reason.</summary>
    public string Reason { get; }

    /// <summary>The original line, verbatim.</summary>
    public string RawLine { get; }

    /// <inheritdoc/>
    public override string ToString() => $"line {Line} [{Code}]: {Reason}";
}

/// <summary>
/// A presentation-only line the importer skipped but COUNTED (falstad-format.md
/// §7 "ignore with a logged notice"): scope config, hints, sliders, and afilter
/// leftovers carry no electrical meaning, so hard-rejecting them would make real
/// falstad exports unusable — but a lesson author must still see what was
/// dropped, so they are reported rather than silently eaten.
/// </summary>
public readonly struct FalstadNotice
{
    internal FalstadNotice(int line, string code, string reason)
    {
        Line = line; Code = code; Reason = reason;
    }

    /// <summary>1-based line number in the source text (0 for whole-import notices).</summary>
    public int Line { get; }

    /// <summary>The dump-type token this notice concerns.</summary>
    public string Code { get; }

    /// <summary>Why the line was ignored / what was dropped.</summary>
    public string Reason { get; }

    /// <inheritdoc/>
    public override string ToString() => $"line {Line} [{Code}]: {Reason}";
}

/// <summary>
/// A voltage probe recovered from an <c>O</c> (output marker) or <c>p</c>
/// (voltmeter) element. The tablet/lesson corpus addresses probes by their first
/// post coordinate (lessons/README-schema.md), so both the coordinate and the
/// resolved <see cref="NodeId"/> are exposed.
/// </summary>
public readonly struct FalstadProbe
{
    internal FalstadProbe(int x, int y, NodeId node, ProbeId probe, ExternalKey key)
    {
        X = x; Y = y; Node = node; Probe = probe; Key = key;
    }

    /// <summary>First-post x pixel coordinate (the probe's identity in the netlist).</summary>
    public int X { get; }

    /// <summary>First-post y pixel coordinate.</summary>
    public int Y { get; }

    /// <summary>The node whose voltage this probe reads.</summary>
    public NodeId Node { get; }

    /// <summary>The registered probe handle.</summary>
    public ProbeId Probe { get; }

    /// <summary>The deterministic ExternalKey minted for this probe.</summary>
    public ExternalKey Key { get; }
}

/// <summary>
/// Result of a successful <see cref="FalstadImporter.Import(string, System.Nullable{NetlistOptions})"/>:
/// the constructed <see cref="Netlist"/> plus the maps the lesson corpus and the
/// ngspice oracle need — probe-by-coordinate, node-by-coordinate, the reference
/// node, and the header timestep hint.
/// </summary>
public sealed class FalstadImportResult
{
    private readonly Dictionary<(int X, int Y), NodeId> _coordToNode;
    private readonly List<FalstadProbe> _probes;

    internal FalstadImportResult(
        Netlist netlist,
        NodeId referenceNode,
        bool hasReference,
        double? headerTimeStep,
        Dictionary<(int X, int Y), NodeId> coordToNode,
        List<FalstadProbe> probes,
        List<FalstadNotice> notices)
    {
        Netlist = netlist;
        ReferenceNode = referenceNode;
        HasReference = hasReference;
        HeaderTimeStep = headerTimeStep;
        _coordToNode = coordToNode;
        _probes = probes;
        Notices = notices;
    }

    /// <summary>The constructed document. Not yet solved — the caller solves.</summary>
    public Netlist Netlist { get; }

    /// <summary>The datum node (ground). <see cref="HasReference"/> is false when the
    /// circuit declared no <c>g</c> element and none was synthesized.</summary>
    public NodeId ReferenceNode { get; }

    /// <summary>Whether <see cref="ReferenceNode"/> is meaningful.</summary>
    public bool HasReference { get; }

    /// <summary>The <c>$</c> header's <c>maxTimeStep</c> (seconds), if a header was
    /// present — the transient dt the netlist was born with. Null when no header.</summary>
    public double? HeaderTimeStep { get; }

    /// <summary>Every probe recovered, in element order.</summary>
    public IReadOnlyList<FalstadProbe> Probes => _probes;

    /// <summary>Presentation-only lines that were ignored but counted.</summary>
    public IReadOnlyList<FalstadNotice> Notices { get; }

    /// <summary>Resolve a coordinate to the node that occupies it. False when no
    /// element post sits at <paramref name="x"/>,<paramref name="y"/>.</summary>
    public bool TryGetNodeAt(int x, int y, out NodeId node)
        => _coordToNode.TryGetValue((x, y), out node);

    /// <summary>Resolve a probe by its first-post coordinate (lesson-schema
    /// addressing). It is a corpus lint error for a coordinate to match zero or
    /// more than one probe; <see cref="CountProbesAt"/> lets a caller assert the
    /// exactly-one rule before trusting the result.</summary>
    public bool TryGetProbe(int x, int y, out FalstadProbe probe)
    {
        foreach (var p in _probes)
        {
            if (p.X == x && p.Y == y) { probe = p; return true; }
        }
        probe = default;
        return false;
    }

    /// <summary>Number of probes whose first post is at the given coordinate.</summary>
    public int CountProbesAt(int x, int y)
    {
        var n = 0;
        foreach (var p in _probes)
            if (p.X == x && p.Y == y) n++;
        return n;
    }
}

/// <summary>
/// Thrown when an import is rejected. Carries every <see cref="FalstadRejection"/>
/// found in the pass — the importer collects all problems before throwing so a
/// lesson author sees the full list at once, not one error per re-run. Because the
/// throw happens BEFORE any <see cref="Netlist"/> mutation, a rejected import never
/// produces a partial document.
/// </summary>
public sealed class FalstadImportException : Exception
{
    internal FalstadImportException(IReadOnlyList<FalstadRejection> rejections)
        : base(BuildMessage(rejections))
    {
        Rejections = rejections;
    }

    /// <summary>Every rejection found in the pass (at least one).</summary>
    public IReadOnlyList<FalstadRejection> Rejections { get; }

    private static string BuildMessage(IReadOnlyList<FalstadRejection> rejections)
    {
        var sb = new StringBuilder();
        sb.Append("Falstad import rejected (").Append(rejections.Count).Append(rejections.Count == 1 ? " problem):" : " problems):");
        var shown = 0;
        foreach (var r in rejections)
        {
            sb.Append("\n  ").Append(r.ToString());
            if (++shown >= 12) { sb.Append("\n  …"); break; }
        }
        return sb.ToString();
    }
}
