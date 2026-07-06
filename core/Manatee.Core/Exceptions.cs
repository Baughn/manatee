namespace Manatee.Core;

/// <summary>
/// Thrown in DEBUG builds on use of a stale / wrapped / cross-netlist internal
/// handle (api.md §16, §20). Carries the offending ref, expected-vs-actual
/// generation, and the journal event that invalidated it, so a compaction bug
/// reads as "stale ResistorId slot 41 gen 3, live gen 4, invalidated by
/// IslandRebuilt seq 8123" — never silent aliasing. In RELEASE this condition
/// is a defined sentinel (reads return 0, mutations no-op, TryResolve false)
/// plus <c>TickStats.StaleHandleReads</c>; a shipped game never crashes on a
/// stale read.
/// </summary>
public sealed class StaleHandleException : InvalidOperationException
{
    public StaleHandleException(
        ComponentKind kind, int slot, uint expectedGen, uint actualGen,
        long invalidatingSeq, string invalidatingEvent)
        : base($"Stale handle: kind={kind} slot={slot} gen={expectedGen}, live gen={actualGen}, " +
               $"invalidated by {invalidatingEvent} seq {invalidatingSeq}")
    {
        Kind = kind;
        Slot = slot;
        ExpectedGen = expectedGen;
        ActualGen = actualGen;
        InvalidatingSeq = invalidatingSeq;
        InvalidatingEvent = invalidatingEvent;
    }

    /// <summary>Component/handle kind (a node reports <see cref="ComponentKind.Resistor"/>
    /// as a placeholder tag — the <see cref="Slot"/>/<see cref="ExpectedGen"/>
    /// pair is the identifying pair).</summary>
    public ComponentKind Kind { get; }

    public int Slot { get; }
    public uint ExpectedGen { get; }
    public uint ActualGen { get; }

    /// <summary>Journal seq of the event that invalidated the handle, or −1.</summary>
    public long InvalidatingSeq { get; }

    /// <summary>Human-readable name of the invalidating event.</summary>
    public string InvalidatingEvent { get; }
}

/// <summary>
/// Thrown from <c>StructuralEdit.Commit</c> (and <c>Reconfigure</c>) under
/// <see cref="PartitioningMode.ClientPartitioned"/> when an edit would merge two
/// distinct non-sentinel partitions other than through a coupler (api.md §11,
/// §20). The WHOLE batch rolls back atomically — nothing is applied.
/// </summary>
public sealed class PartitionMergeException : InvalidOperationException
{
    public PartitionMergeException(PartitionKey a, PartitionKey b)
        : base($"ClientPartitioned: illegal merge of partitions {a.Value} and {b.Value} " +
               "(only a coupler may join partitions). The edit was rolled back.")
    {
        PartitionA = a;
        PartitionB = b;
    }

    public PartitionKey PartitionA { get; }
    public PartitionKey PartitionB { get; }
}
