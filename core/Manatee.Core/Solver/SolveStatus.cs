namespace Manatee.Core.Solver;

/// <summary>
/// Outcome of a <see cref="Circuit"/> factorize/solve. The solve path NEVER
/// throws (solver.md, Failure Handling; api.md §20): a legal-but-degenerate
/// circuit is data, not an exception. <see cref="Ok"/> published a fresh
/// solution; any other value means the previous published solution was
/// RETAINED and the circuit is <see cref="Circuit.Faulted"/>.
/// </summary>
internal enum SolveStatus : byte
{
    /// <summary>Factorization/solve succeeded and (for Solve) published.</summary>
    Ok = 0,

    /// <summary>Matrix singular after gmin — a floating datum, contradictory
    /// ideal sources, or a redundant constraint. The backend's pivot search
    /// failed; the offending matrix row is in <see cref="FaultInfo.Row"/>
    /// (mapping row → node/component is the netlist layer's job).</summary>
    Singular = 1,

    /// <summary>The solve produced a non-finite entry (NaN/±∞). No such vector
    /// is ever published (api.md §20: "no NaN ever leaves the API"); the
    /// previous solution stays visible. Debug builds assert as well.</summary>
    NonFinite = 2,

    /// <summary>A nonlinear (Newton) solve exhausted its iteration cap and the
    /// fallback ladder without converging (solver.md Failure Handling; api.md §20
    /// <see cref="FaultKind.NewtonDiverged"/>). Not produced by the linear
    /// <see cref="Circuit"/> itself — the netlist's Newton driver raises it and
    /// holds the previous published solution. No non-finite vector is ever left
    /// behind: junction-voltage limiting keeps every iterate finite.</summary>
    Diverged = 3,
}

/// <summary>
/// The last fault a <see cref="Circuit"/> observed. <see cref="Row"/> is the
/// MNA row the fault localized to (a node-potential row or an auxiliary
/// branch row); the netlist layer resolves it to a node or component. Plain
/// value struct — 0B to read.
/// </summary>
internal readonly struct FaultInfo
{
    public FaultInfo(SolveStatus status, int row)
    {
        Status = status;
        Row = row;
    }

    /// <summary>The fault classification; <see cref="SolveStatus.Ok"/> when none.</summary>
    public SolveStatus Status { get; }

    /// <summary>Offending MNA row, or −1 when unlocalized. For a singular
    /// system this is the original matrix column the backend could not pivot;
    /// for a contradictory-source fault it names one of the branch rows.</summary>
    public int Row { get; }

    public static FaultInfo None => new(SolveStatus.Ok, -1);
}
