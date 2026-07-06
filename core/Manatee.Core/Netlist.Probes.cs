using System;
using System.Collections.Generic;

namespace Manatee.Core;

// Probe readback and the oscilloscope tap (api.md §13; solver.md Probes). A probe
// is a document-stable observation point: a NODE probe reads one node; an
// INTERPOLATED probe reads V = Va + t·(Vb − Va) between two nodes (the reduction
// layer re-aims t via Meta.SetProbeInterpolation after a series collapse so an
// instrument can read "inside" a compacted run). ProbeId survives rebuilds/merges
// AND a drift Resync (re-aimed on the same handle); only a whole-netlist
// FromCanonical reissues probe slots, after which the client re-resolves via
// TryResolveProbe(key).
public sealed partial class Netlist
{
    // Active oscilloscope taps (few; sampled after every substep). A List indexed
    // by position — no enumerator allocation on the sample path.
    private readonly List<WaveformTap> _taps = new();

    /// <summary>Interpolated probe read (api.md §13): V = Va + t·(Vb − Va). A node
    /// probe (a == b, t = 0) reads its single node. 0B; reads the published
    /// solution (or last-good). A stale/removed probe reads 0.</summary>
    internal double ReadProbe(ProbeId p)
    {
        if (p.Net != _netId || (uint)p.Slot >= (uint)_pCount || !_pAlive[p.Slot] || _pGen[p.Slot] != p.Gen)
            return 0.0;
        var slot = p.Slot;
        var na = _pA[slot]; var nb = _pB[slot];
        // Aim-liveness (§13/§20): a probe whose aimed node was removed carries a
        // dangling (-1) aim (invalidated at CommitEdit) and reads 0 until re-aimed —
        // never the freed slot's next occupant. The _nAlive check is belt-and-braces.
        if (na < 0 || nb < 0 || !_nAlive[na] || !_nAlive[nb]) return 0.0;
        var va = NodePotential(na);
        var vb = NodePotential(nb);
        return va + _pT[slot] * (vb - va);
    }

    internal void AttachTap(WaveformTap tap) => _taps.Add(tap);

    internal void DetachTap(WaveformTap tap) => _taps.Remove(tap);

    // Sample every tap whose probe lives in the just-solved island. Called after
    // each substep back-substitution (the front buffer holds that substep's
    // solution). Zero-alloc: index loop + a struct store per matching tap.
    private void SampleTaps(int islandSlot)
    {
        if (_taps.Count == 0) return;
        for (var i = 0; i < _taps.Count; i++)
        {
            var tap = _taps[i];
            var p = tap.Probe;
            if (p.Net != _netId || (uint)p.Slot >= (uint)_pCount || !_pAlive[p.Slot] || _pGen[p.Slot] != p.Gen)
                continue;
            var na = _pA[p.Slot];
            var nb = _pB[p.Slot];
            if (na < 0 || nb < 0 || !_nAlive[na] || !_nAlive[nb] || _nIsland[na] != islandSlot)
                continue;   // probe not in this island / dangling aim (§13)
            var va = NodePotential(na);
            var vb = NodePotential(nb);
            tap.Ring.Push(va + _pT[p.Slot] * (vb - va));
        }
    }
}
