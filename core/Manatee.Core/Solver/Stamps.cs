namespace Manatee.Core.Solver;

// Stamp handles: opaque slot descriptors a component gets back from
// Circuit.Add*, then hands to Circuit.Set* to drive its values. Each carries
// the fixed VALUE-SLOT indices (matrix contributions) and RHS-SLOT indices
// (right-hand-side contributions) the Add call reserved. A slot of −1 means
// "dropped": one of the endpoints is the eliminated reference node, so that
// matrix cell / RHS entry does not exist (Circuit reference handling). All are
// blittable readonly structs — storable in the netlist layer's SoA tables, 0B
// to pass.

/// <summary>
/// A two-terminal conductance contribution (resistor, switch, or the
/// conductance half of a storage/diode companion). Its four value slots hold
/// the symmetric stamp +g,+g,−g,−g at (a,a),(b,b),(a,b),(b,a). Tier 2:
/// changing g rewrites these four slots and forces a refactorization.
/// </summary>
internal readonly struct ConductanceStamp
{
    public ConductanceStamp(int aa, int bb, int ab, int ba)
    {
        Aa = aa; Bb = bb; Ab = ab; Ba = ba;
    }

    /// <summary>Diagonal slot for terminal a, or −1 if a is the reference.</summary>
    public int Aa { get; }
    /// <summary>Diagonal slot for terminal b, or −1 if b is the reference.</summary>
    public int Bb { get; }
    /// <summary>Off-diagonal (a,b), or −1 if either endpoint is the reference.</summary>
    public int Ab { get; }
    /// <summary>Off-diagonal (b,a), or −1 if either endpoint is the reference.</summary>
    public int Ba { get; }
}

/// <summary>
/// Companion-model stamp SHAPE for capacitor / inductor / diode: a
/// conductance in parallel with a current source (Norton form). Backward-Euler
/// storage uses the conductance form — capacitor G = C/dt, inductor G = dt/L,
/// each with a history current source — so NO auxiliary branch row is needed
/// for an inductor (solver.md: "Capacitor: G = C/dt … Inductor: mirror").
/// The VALUES are supplied per step by the analyses layer via
/// <see cref="Circuit.SetCompanion"/>; only the slot shape exists at build time.
/// </summary>
internal readonly struct CompanionStamp
{
    public CompanionStamp(in ConductanceStamp g, int rhsA, int rhsB)
    {
        G = g; RhsA = rhsA; RhsB = rhsB;
    }

    /// <summary>The parallel conductance slots.</summary>
    public ConductanceStamp G { get; }
    /// <summary>RHS slot receiving +Ieq at terminal a, or −1 if a is reference.</summary>
    public int RhsA { get; }
    /// <summary>RHS slot receiving −Ieq at terminal b, or −1 if b is reference.</summary>
    public int RhsB { get; }
}

/// <summary>
/// An independent voltage source: one auxiliary branch row carrying the branch
/// current, with static ±1 incidence already stamped. The source value is a
/// tier-1 RHS write (<see cref="Circuit.SetVSourceValue"/>); the branch current
/// is read back with <see cref="Circuit.ReadFlow"/> at <see cref="AuxRow"/>.
/// </summary>
internal readonly struct VSourceStamp
{
    public VSourceStamp(int auxRow, int rhsSlot)
    {
        AuxRow = auxRow; RhsSlot = rhsSlot;
    }

    /// <summary>The branch-current MNA row; also the ReadFlow argument.</summary>
    public int AuxRow { get; }
    /// <summary>RHS slot holding the source voltage.</summary>
    public int RhsSlot { get; }
}

/// <summary>
/// An independent current source: RHS only, no matrix contribution and no
/// auxiliary row. Positive amps flows from → to (injected at <c>to</c>,
/// extracted at <c>from</c>). Tier 1.
/// </summary>
internal readonly struct ISourceStamp
{
    public ISourceStamp(int rhsFrom, int rhsTo)
    {
        RhsFrom = rhsFrom; RhsTo = rhsTo;
    }

    /// <summary>RHS slot at the source's <c>from</c> node (−I), or −1 if reference.</summary>
    public int RhsFrom { get; }
    /// <summary>RHS slot at the source's <c>to</c> node (+I), or −1 if reference.</summary>
    public int RhsTo { get; }
}

/// <summary>
/// An ideal transformer two-port: the one multi-terminal primitive (api.md §6).
/// TWO auxiliary rows — a voltage-ratio constraint carrying the primary branch
/// current, and a current-ratio constraint carrying the secondary branch
/// current. Magnetic coupling is inexpressible as a composition of independent
/// two-terminal elements, hence the coupled aux rows. The turns ratio n enters
/// three value slots; changing it is tier 2.
/// </summary>
internal readonly struct TransformerStamp
{
    public TransformerStamp(int primaryAuxRow, int secondaryAuxRow, int slotPbPos, int slotPbNeg, int slotSp)
    {
        PrimaryAuxRow = primaryAuxRow; SecondaryAuxRow = secondaryAuxRow;
        SlotPbPos = slotPbPos; SlotPbNeg = slotPbNeg; SlotSp = slotSp;
    }

    /// <summary>Primary branch-current row (ReadFlow: i_p).</summary>
    public int PrimaryAuxRow { get; }
    /// <summary>Secondary branch-current row (ReadFlow: i_s).</summary>
    public int SecondaryAuxRow { get; }
    /// <summary>Value slot for (primary-row, bPos) = −n, or −1 if bPos is reference.</summary>
    public int SlotPbPos { get; }
    /// <summary>Value slot for (primary-row, bNeg) = +n, or −1 if bNeg is reference.</summary>
    public int SlotPbNeg { get; }
    /// <summary>Value slot for (secondary-row, primary-row) = +n.</summary>
    public int SlotSp { get; }
}
