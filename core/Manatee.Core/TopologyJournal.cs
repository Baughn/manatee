namespace Manatee.Core;

/// <summary>The event kinds a <see cref="TopologyJournal"/> records (api.md §15).
/// There is deliberately NO 3-id split event: a coupler Open that splits an
/// island emits ONE <see cref="IslandRebuilt"/> per resulting island, and
/// <see cref="TopologyEvent"/> carries exactly two island-id fields.</summary>
public enum TopologyEventKind : byte
{
    ComponentAdded, ComponentRemoved, NodeAdded, NodeRemoved, ProbeAdded, ProbeRemoved,
    IslandCreated,

    /// <summary>(A=survivor, B=absorbed). Component/node handles SURVIVE; the
    /// absorbed IslandId is stale.</summary>
    IslandsMerged,

    /// <summary>(A=old, B=new). Removal-, coupler-Open-, or resync-triggered;
    /// all internal handles rooted in A are invalidated (couplers/probes
    /// excepted — document-stable, §16).</summary>
    IslandRebuilt,

    IslandRemoved, IslandFaulted, IslandRecovered,
}

/// <summary>A fixed-size, string-free journal record (api.md §15). Exactly two
/// island-id fields, so the rejected 3-id split event cannot be reintroduced.</summary>
public readonly struct TopologyEvent
{
    internal TopologyEvent(long seq, TopologyEventKind kind, ComponentRef component,
                           ExternalKey key, IslandId a, IslandId b)
    {
        Seq = seq; Kind = kind; Component = component; Key = key; IslandA = a; IslandB = b;
    }

    /// <summary>Monotonic sequence number.</summary>
    public long Seq { get; }

    public TopologyEventKind Kind { get; }

    /// <summary>The component subject (default for node/island-only events).</summary>
    public ComponentRef Component { get; }

    /// <summary>Client-meaningful identity on every entry.</summary>
    public ExternalKey Key { get; }

    /// <summary>Survivor / old / subject island.</summary>
    public IslandId IslandA { get; }

    /// <summary>Absorbed / new island.</summary>
    public IslandId IslandB { get; }
}

/// <summary>An independent read cursor over a <see cref="TopologyJournal"/>.
/// Positioned by seq; copyable (value type). 0B to advance.</summary>
public struct JournalCursor
{
    internal JournalCursor(long next) => Next = next;

    /// <summary>The next seq this cursor will read.</summary>
    internal long Next;
}

/// <summary>
/// Fixed-capacity ring of blittable events with a monotonic <see cref="Head"/>
/// and multiple independent read cursors (api.md §15). Derived layers stay in
/// sync by REPLAY, never callbacks; a lagging reader detects its OWN overflow
/// (<see cref="Overflowed"/>) and resyncs — explicit, never silent. Game clients
/// never babysit cursors; their re-pin trigger is
/// <c>IslandTable.DrainChanges</c>.
/// </summary>
public sealed class TopologyJournal
{
    internal const int DefaultCapacity = 4096;

    private readonly TopologyEvent[] _ring;
    private long _head;          // next seq to write == count ever written
    private long _minAvailable;  // lowest seq still resident in the ring

    internal TopologyJournal(int capacity)
    {
        _ring = new TopologyEvent[capacity <= 0 ? DefaultCapacity : capacity];
        _head = 0;
        _minAvailable = 0;
    }

    /// <summary>Ring capacity (fixed at construction).</summary>
    public int Capacity => _ring.Length;

    /// <summary>Seq one past the last written event.</summary>
    public long Head => _head;

    /// <summary>Cursor starting at the head (reads only future events).</summary>
    public JournalCursor OpenCursor() => new(_head);

    /// <summary>0B; position at a historical seq — THE way to replay an
    /// EditReceipt window. If <paramref name="seq"/> has been lapped, the first
    /// <see cref="TryRead"/> returns false and <see cref="Overflowed"/> is true.</summary>
    public JournalCursor OpenCursorAt(long seq) => new(seq);

    /// <summary>0B; false ⇒ caught up (or lapped — check <see cref="Overflowed"/>).</summary>
    public bool TryRead(ref JournalCursor c, out TopologyEvent e)
    {
        if (c.Next < _minAvailable || c.Next >= _head)
        {
            e = default;
            return false;
        }
        e = _ring[(int)(c.Next % _ring.Length)];
        c.Next++;
        return true;
    }

    /// <summary>0B; like <see cref="TryRead"/> but stops at <paramref name="toSeq"/>
    /// (exclusive) — the EditReceipt-window replay path.</summary>
    public bool TryReadRange(ref JournalCursor c, long toSeq, out TopologyEvent e)
    {
        if (c.Next >= toSeq)
        {
            e = default;
            return false;
        }
        return TryRead(ref c, out e);
    }

    /// <summary>True iff this cursor's next seq has been lapped ⇒ MUST resync.</summary>
    public bool Overflowed(in JournalCursor c) => c.Next < _minAvailable;

    // ---------------------------------------------------------------- internal

    /// <summary>Append one event, returning its seq. Shape-time (Edit commit /
    /// rebuild); the ring itself is allocated once at construction.</summary>
    internal long Append(TopologyEventKind kind, ComponentRef component, ExternalKey key, IslandId a, IslandId b)
    {
        var seq = _head;
        _ring[(int)(seq % _ring.Length)] = new TopologyEvent(seq, kind, component, key, a, b);
        _head = seq + 1;
        if (_head - _minAvailable > _ring.Length)
            _minAvailable = _head - _ring.Length;
        return seq;
    }
}
