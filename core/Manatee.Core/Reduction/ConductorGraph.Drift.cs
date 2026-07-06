using System.Collections.Generic;

namespace Manatee.Core.Reduction;

// The two-level drift backstop (api.md §19; compaction.md "Resync backstop").
// Level 1 is the caller's cheap Netlist.Fingerprint check every N ticks; on a
// mismatch it escalates to Diff (the cold, canonical, SaveNormalized-grade compare
// against the shadow truth), and Resync rebuilds live state from that truth and
// restores by key. Incremental maintenance against a live mutation stream WILL have
// edge-case bugs; this converts them from mystery corruption into a bug report with
// coordinates (an ExternalKey per entry).
public sealed partial class ConductorGraph
{
    /// <summary>Full canonical diff of the incrementally-maintained LIVE geometry
    /// against the shadow <paramref name="truth"/> (api.md §19). Cold. Entries name
    /// an <see cref="ExternalKey"/>; sorted deterministically by key. The
    /// <paramref name="island"/> scopes the caller's intent; this build compares the
    /// whole graph's segment set (whole-graph compaction).</summary>
    public DriftReport Diff(IslandId island, IGeometrySource truth)
    {
        _ = island;
        var truthSegs = new Dictionary<SegmentKey, GeometrySegment>();
        foreach (var g in truth.Segments) truthSegs[g.Key] = g;

        var all = new HashSet<SegmentKey>();
        foreach (var k in _segs.Keys) all.Add(k);
        foreach (var k in truthSegs.Keys) all.Add(k);
        var keys = new List<SegmentKey>(all);
        keys.Sort(CompareSeg);

        var entries = new List<DriftEntry>();
        foreach (var k in keys)
        {
            var inLive = _segs.TryGetValue(k, out var live);
            var inTruth = truthSegs.TryGetValue(k, out var t);
            if (inTruth && !inLive)
                entries.Add(new DriftEntry(DriftKind.MissingInLive, k.External(), 0.0, t.Spec.Resistance));
            else if (inLive && !inTruth)
                entries.Add(new DriftEntry(DriftKind.MissingInShadow, k.External(), live.Ohms, 0.0));
            else
            {
                if (live.Ohms != t.Spec.Resistance)
                    entries.Add(new DriftEntry(DriftKind.ValueMismatch, k.External(), live.Ohms, t.Spec.Resistance));
                if (_prePartitioned && live.Partition != t.Partition.Value)
                    entries.Add(new DriftEntry(DriftKind.PartitionMismatch, k.External(),
                        live.Partition, t.Partition.Value));
            }
        }
        return new DriftReport(entries.ToArray(), entries.Count);
    }

    /// <summary>Rebuild live geometry from the shadow <paramref name="truth"/> and
    /// recompact, restoring probes by their document-stable ids (api.md §19). The
    /// backstop of last resort; after it, the live netlist equals a from-scratch
    /// build of the truth. Clears the pending <see cref="ResyncNeeded"/> flag.</summary>
    public ResyncReport Resync(IslandId island, IGeometrySource truth)
    {
        _ = island;
        _segs.Clear();
        _junctionPartition.Clear();
        _refJunction.Clear();
        _referenceJunctions.Clear();

        foreach (var g in truth.Segments)
        {
            var part = 0UL;
            if (_prePartitioned)
            {
                if (g.Partition.IsNone)
                    throw new System.InvalidOperationException(
                        $"Resync: PrePartitioned truth segment {Fmt(g.Key)} has no partition.");
                part = g.Partition.Value;
                TagJunction(g.A, part);
                TagJunction(g.B, part);
                EnsureReferenceRail(part);
            }
            _segs[g.Key] = new Seg
            {
                A = g.A, B = g.B, Ohms = g.Spec.Resistance, Limits = g.Spec.Limits,
                Partition = part, AmbientK = g.AmbientKelvin > 0 ? g.AmbientKelvin : NominalAmbientK,
            };
        }

        _dirty = true;
        Recompact();
        _resyncNeeded = false;
        return new ResyncReport(_segs.Count, _liveNodeKeys.Count, _liveChains.Count, _probeList.Count, true);
    }

    // ── Internal test hooks (api.md §19 drift test: "corrupt the graph's internal
    // map deliberately"). Visible only to Manatee.Core.Tests. They mutate the shadow
    // AND recompact so the LIVE realization diverges from truth — exactly the failure
    // Diff/Resync exist to catch and repair. ──

    internal void DebugCorruptSegmentOhms(in SegmentKey k, double ohms)
    {
        if (!_segs.TryGetValue(k, out var s)) return;
        s.Ohms = ohms;
        _segs[k] = s;
        _dirty = true;
        Recompact();
    }

    internal void DebugDropSegment(in SegmentKey k)
    {
        if (!_segs.Remove(k)) return;
        _dirty = true;
        Recompact();
    }
}
