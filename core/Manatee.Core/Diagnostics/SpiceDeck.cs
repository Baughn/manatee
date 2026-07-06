using System;
using System.Collections.Generic;

namespace Manatee.Core.Diagnostics;

/// <summary>
/// The SPICE analysis a <see cref="SpiceDeck"/> emits: a DC operating point
/// (<see cref="Op"/>) or a transient run (<see cref="Tran"/>) with a fixed print
/// step and stop time (api.md §22.c).
/// </summary>
public readonly struct SpiceAnalysis
{
    /// <summary>Which analysis this selects.</summary>
    public enum AnalysisKind : byte { Op, Tran }

    private SpiceAnalysis(AnalysisKind kind, double step, double stop)
    {
        Kind = kind; Step = step; Stop = stop;
    }

    /// <summary>The analysis kind.</summary>
    public AnalysisKind Kind { get; }

    /// <summary>Transient print step (seconds); 0 for <see cref="Op"/>.</summary>
    public double Step { get; }

    /// <summary>Transient stop time (seconds); 0 for <see cref="Op"/>.</summary>
    public double Stop { get; }

    /// <summary>DC operating point (<c>.op</c>).</summary>
    public static SpiceAnalysis Op => new(AnalysisKind.Op, 0.0, 0.0);

    /// <summary>Transient (<c>.tran step stop uic</c>).</summary>
    public static SpiceAnalysis Tran(double step, double stop) => new(AnalysisKind.Tran, step, stop);
}

/// <summary>
/// Options for <see cref="SpiceDeck.Emit"/> (api.md §22.c). <see cref="MatchBackwardEuler"/>
/// forces ngspice onto <c>method=gear maxord=1</c> (order-1 Gear ≡ Backward Euler)
/// with a fixed <c>.tran</c> step matching the solver's substep dt, so an ngspice
/// transient reproduces the manatee Backward-Euler trajectory rather than diverging
/// on the trapezoidal default (testing-strategy.md).
/// </summary>
public readonly struct SpiceEmitOptions
{
    /// <summary>The analysis to emit.</summary>
    public SpiceAnalysis Analysis { get; init; }

    /// <summary>Match the solver's Backward-Euler integrator (gear maxord=1).</summary>
    public bool MatchBackwardEuler { get; init; }
}

/// <summary>
/// The emitted deck plus the maps a differ needs to diff ngspice output against a
/// manatee <see cref="Solution"/> without name-guessing (api.md §22.c).
/// </summary>
public readonly struct DeckResult
{
    internal DeckResult(string text, ComponentRef[] unrepresentable,
                        IReadOnlyList<(NodeId Node, string Column)> nodeNames,
                        IReadOnlyList<(ComponentRef Component, string Column)> branchNames)
    {
        Text = text;
        _unrepresentable = unrepresentable;
        NodeNames = nodeNames;
        BranchNames = branchNames;
    }

    private readonly ComponentRef[] _unrepresentable;

    /// <summary>The full, self-contained deck. Deterministic (slot-ordered names)
    /// so it is a stable Verify golden: a stamp refactor is a reviewable diff before
    /// it is an oracle delta.</summary>
    public string Text { get; }

    /// <summary>Behavioral devices ngspice cannot oracle — the differ knows its own
    /// fidelity and skips these, covering them by invariants + hand cases instead.</summary>
    public ReadOnlyMemory<ComponentRef> Unrepresentable => _unrepresentable;

    /// <summary>Rawfile voltage column ↔ NodeId, for every non-datum member node.
    /// The datum node maps to ngspice ground (0) and is omitted (it reads 0 on both
    /// sides by construction).</summary>
    public IReadOnlyList<(NodeId Node, string Column)> NodeNames { get; }

    /// <summary>Rawfile current column ↔ the voltage-source component whose branch it
    /// ammeters. Extends the api.md §22.c sketch so the differ can also diff branch
    /// currents "where the deck exposes them via V-source ammeters" (task item 2).</summary>
    public IReadOnlyList<(ComponentRef Component, string Column)> BranchNames { get; }
}

/// <summary>
/// Emits a ngspice deck from one island of a manatee <see cref="Netlist"/>
/// (api.md §2, §22.c). Public, cold-path — never called in a tick. Names are
/// deterministic (component/node arena slots), so the deck text is a stable Verify
/// golden and the returned maps let a differ compare ngspice output to a manatee
/// <see cref="Solution"/> column-by-column without guessing node names.
/// </summary>
public static class SpiceDeck
{
    /// <summary>Emit the deck for <paramref name="island"/> under <paramref name="opts"/>.</summary>
    public static DeckResult Emit(Netlist n, IslandId island, in SpiceEmitOptions opts)
    {
        if (n is null) throw new ArgumentNullException(nameof(n));
        return n.EmitSpiceDeck(island, opts);
    }
}
