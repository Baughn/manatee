using System;

namespace Manatee.Core;

/// <summary>
/// A caller-owned ring of probe samples (api.md §13). The oscilloscope contract:
/// a <see cref="WaveformTap"/> writes one sample per solver substep into this
/// ring; the UI reads <see cref="Samples"/>. Fixed capacity, oldest overwritten —
/// bounded memory, no reallocation on the hot path.
/// </summary>
public sealed class WaveformRing
{
    private readonly double[] _buf;
    private int _head;     // next write index
    private int _count;

    public WaveformRing(int capacity)
    {
        if (capacity < 1) throw new ArgumentOutOfRangeException(nameof(capacity));
        _buf = new double[capacity];
    }

    /// <summary>Ring capacity.</summary>
    public int Capacity => _buf.Length;

    /// <summary>Live sample count (≤ <see cref="Capacity"/>).</summary>
    public int Count => _count;

    /// <summary>The samples oldest-first. Allocates a small copy (a UI-read
    /// convenience, off the hot path); the sampling write itself is 0B.</summary>
    public ReadOnlySpan<double> Samples
    {
        get
        {
            var outp = new double[_count];
            var start = ((_head - _count) % _buf.Length + _buf.Length) % _buf.Length;
            for (var i = 0; i < _count; i++) outp[i] = _buf[(start + i) % _buf.Length];
            return outp;
        }
    }

    // 0B: a single struct store into the preallocated buffer.
    internal void Push(double v)
    {
        _buf[_head] = v;
        _head = (_head + 1) % _buf.Length;
        if (_count < _buf.Length) _count++;
    }

    /// <summary>Discard all samples.</summary>
    public void Clear() { _head = 0; _count = 0; }
}

/// <summary>
/// Per-substep sampling of one probe into a caller-owned <see cref="WaveformRing"/>
/// (api.md §13). <see cref="Attach"/> is a COLD, call-once subscribe — it allocates
/// the tap and must live in setup, never the tick loop; thereafter each substep
/// costs one struct store (0B), and it works during AC subcycling (a sample per
/// substep, not per tick). Two taps at a time is the expected UI contract (phase
/// comparisons need a pair).
/// </summary>
public sealed class WaveformTap
{
    private readonly Netlist _net;
    private readonly ProbeId _probe;
    private readonly WaveformRing _ring;
    private bool _attached;

    private WaveformTap(Netlist net, ProbeId probe, WaveformRing ring)
    {
        _net = net; _probe = probe; _ring = ring; _attached = true;
    }

    /// <summary>Cold subscribe (tier 0): begin sampling <paramref name="p"/> into
    /// <paramref name="ring"/> each substep. Call in setup.</summary>
    public static WaveformTap Attach(Netlist n, ProbeId p, WaveformRing ring)
    {
        if (n is null) throw new ArgumentNullException(nameof(n));
        if (ring is null) throw new ArgumentNullException(nameof(ring));
        var tap = new WaveformTap(n, p, ring);
        n.AttachTap(tap);
        return tap;
    }

    /// <summary>Stop sampling. Idempotent.</summary>
    public void Detach()
    {
        if (!_attached) return;
        _attached = false;
        _net.DetachTap(this);
    }

    internal ProbeId Probe => _probe;
    internal WaveformRing Ring => _ring;
    internal bool Attached => _attached;
}
