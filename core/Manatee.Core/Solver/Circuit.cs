using System.Diagnostics;

namespace Manatee.Core.Solver;

/// <summary>
/// The per-island matrix owner: it turns component stamps into an MNA system
/// A·x = b, drives one <see cref="ISolverBackend"/> through the
/// Analyze/Factorize/Solve lifecycle, and publishes the solution. It is the
/// sole thing that talks to a backend; the netlist layer drives <em>this</em>
/// (api.md §2, §17). Domain-neutral readback names — <see cref="ReadPotential"/>
/// / <see cref="ReadFlow"/> — keep the thermal-RC reuse open.
///
/// <para><b>Assembly (duplicate-summing stamps).</b> Each component reserves a
/// fixed set of coordinate VALUE SLOTS (matrix cells) and RHS SLOTS during the
/// build phase. Many slots may target the same (row,col) — two parallel
/// resistors, a companion sharing a node — so at <see cref="Analyze"/> the
/// distinct coordinates are deduplicated into the backend's pattern and a
/// <c>slot → pattern index</c> map is frozen. Every factorization then re-sums
/// the slot values into the deduplicated pattern in place (zero-alloc).</para>
///
/// <para><b>Versioning.</b> The tuple (pattern epoch, values epoch, dt) is the
/// factorization signature. An unchanged tuple skips straight to the cached
/// factors; a values-epoch bump (any <c>Set*</c> touching the matrix, or a dt
/// change) triggers a <see cref="ISolverBackend.Factorize"/>; an RHS-only write
/// triggers neither — the cached LU back-substitutes the new right-hand side.
/// This is the load-bearing tier-1/tier-2 split of solver.md.</para>
///
/// <para><b>Reference handling.</b> The reference (datum) node is ELIMINATED:
/// it is assigned no MNA row or column, so its potential is exactly 0 by
/// construction and every stamp cell touching it is dropped (slot −1). If the
/// caller marks no reference, node 0 is auto-anchored (solver.md: "an
/// explicitly-marked ground node if present, else an arbitrary node"). Every
/// remaining node-potential diagonal carries a gmin = 1e-12 S shunt to the
/// datum, which keeps a floating subgraph — one with no conductive path to the
/// reference — nonsingular. Auxiliary branch rows get NO gmin: a gmin on a
/// constraint diagonal would corrupt the constraint, and those rows are
/// structurally zero on the diagonal by design (the backend pivots through
/// them).</para>
///
/// <para><b>Zero-allocation.</b> Everything after <see cref="Analyze"/> —
/// <c>Set*</c>, <see cref="FactorizeIfDirty"/>, <see cref="Solve"/>, readback —
/// is heap-free (api.md §21). The build phase and Analyze allocate freely.</para>
/// </summary>
internal sealed class Circuit
{
    /// <summary>Floor/ceiling on stamped conductances, in siemens (solver.md
    /// conductance-range policy: 1 GΩ open switch … 1 mΩ closed switch). Doubles
    /// carry ~16 digits; capping the spread at 1e12 keeps assembly off the
    /// conditioning cliff R9 promises to survive.</summary>
    private const double MinConductance = 1e-9;
    private const double MaxConductance = 1e3;

    /// <summary>Shunt conductance placed on every node-potential diagonal to keep
    /// floating subgraphs nonsingular (solver.md).</summary>
    private const double Gmin = 1e-12;

    /// <summary>Debug hygiene threshold: warn once assembly sees stamped
    /// conductances (gmin excluded) spanning more than this ratio.</summary>
    private const double ExtremeRatioWarn = 1e12;

    private readonly ISolverBackend _backend;

    // ---- Node / row layout (fixed at construction; aux rows grow at build). ----
    private readonly int[] _nodeRow;          // node → MNA row, or −1 for the reference
    private readonly int _nodeRowCount;       // rows [0.._nodeRowCount) are node diagonals (gmin-eligible)
    private int _rowCount;                     // node rows + auxiliary rows; = matrix dimension

    // ---- Coordinate stamp storage (grows during build, frozen after Analyze). ----
    private int[] _slotRow;
    private int[] _slotCol;
    private double[] _valueSlots;
    private int _valueSlotCount;

    private int[] _rhsSlotRow;
    private double[] _rhsSlots;
    private int _rhsSlotCount;

    // ---- Frozen assembly (built in Analyze). ----
    private MatrixEntry[] _pattern = [];
    private int[] _slotToPattern = [];        // value slot → pattern index (duplicate-summing target)
    private int[] _nodeDiagPattern = [];      // node row → pattern index of its (r,r) diagonal
    private double[] _patternValues = [];     // assembled values aligned 1:1 with _pattern
    private double[] _rhs = [];               // assembled RHS aligned 1:1 with rows

    // ---- Double-buffered published solution. ----
    private double[] _solutionFront = [];
    private double[] _solutionBack = [];

    // ---- Versioning / fault state. ----
    private long _patternEpoch;               // bumped by Analyze (topology commit)
    private long _valuesEpoch;                // bumped by any matrix-value write
    private double _dt;
    private bool _analyzed;
    private bool _numericReady;               // backend holds valid factors
    private long _factoredPatternEpoch = -1;
    private long _factoredValuesEpoch = -1;
    private double _factoredDt = double.NaN;
    private FaultInfo _fault = FaultInfo.None;

    // Debug conductance-spread envelope, reset each Analyze.
    private double _minSetG = double.PositiveInfinity;
    private double _maxSetG = 0.0;
    private bool _ratioWarned;

    /// <param name="backend">The linear backend this circuit drives.</param>
    /// <param name="nodeCount">Number of node potentials (excludes aux rows).</param>
    /// <param name="referenceNode">Datum node to eliminate, or −1 to auto-anchor node 0.</param>
    /// <param name="expectedValueSlots">Presize hint for coordinate storage.</param>
    /// <param name="expectedRhsSlots">Presize hint for RHS storage.</param>
    public Circuit(ISolverBackend backend, int nodeCount, int referenceNode,
                   int expectedValueSlots = 16, int expectedRhsSlots = 8)
    {
        if (nodeCount < 0) throw new ArgumentOutOfRangeException(nameof(nodeCount));
        if (referenceNode >= nodeCount) throw new ArgumentOutOfRangeException(nameof(referenceNode));

        _backend = backend;

        var reference = referenceNode >= 0 ? referenceNode : (nodeCount > 0 ? 0 : -1);
        _nodeRow = new int[Math.Max(1, nodeCount)];
        var row = 0;
        for (var n = 0; n < nodeCount; n++)
            _nodeRow[n] = n == reference ? -1 : row++;
        _nodeRowCount = row;
        _rowCount = row;

        var vcap = Math.Max(4, expectedValueSlots);
        var rcap = Math.Max(4, expectedRhsSlots);
        _slotRow = new int[vcap];
        _slotCol = new int[vcap];
        _valueSlots = new double[vcap];
        _rhsSlotRow = new int[rcap];
        _rhsSlots = new double[rcap];
    }

    /// <summary>Matrix dimension = non-reference node rows + auxiliary rows.</summary>
    public int Dimension => _rowCount;

    /// <summary>Number of node-potential rows (the gmin-eligible diagonals).</summary>
    public int NodeRowCount => _nodeRowCount;

    /// <summary>True once a factorize/solve left this island singular or
    /// non-finite; the previously published solution is being held. Cleared by
    /// the next successful <see cref="FactorizeIfDirty"/>.</summary>
    public bool Faulted => _fault.Status != SolveStatus.Ok;

    /// <summary>Details of the current fault (see <see cref="Faulted"/>).</summary>
    public FaultInfo Fault => _fault;

    // ================================================================= build

    /// <summary>Backward-Euler step size. Part of the factorization signature
    /// (companion conductances are G = C/dt etc.), so changing it forces a
    /// refactorization — the analyses layer sets dt and the companion value
    /// slots together.</summary>
    public double Dt
    {
        get => _dt;
        set => _dt = value;
    }

    /// <summary>Tier-3 (resistor/switch). Reserves the four-cell conductance
    /// stamp and sets its initial value (clamped). Build phase only.</summary>
    public ConductanceStamp AddResistor(int a, int b, double ohms)
    {
        var stamp = AddConductanceCells(a, b);
        SetConductance(stamp, 1.0 / ohms);
        return stamp;
    }

    /// <summary>Tier-3. A relay contact is an in-matrix conductance: closed ⇒
    /// <see cref="MaxConductance"/>, open ⇒ <see cref="MinConductance"/>
    /// (solver.md: 1 mΩ / 1 GΩ). Stays in the matrix; toggling is tier 2.</summary>
    public ConductanceStamp AddSwitch(int a, int b, bool closed)
    {
        var stamp = AddConductanceCells(a, b);
        SetSwitch(stamp, closed);
        return stamp;
    }

    /// <summary>Tier-3. Companion SHAPE for capacitor/inductor/diode: a
    /// conductance in parallel with a history current source. Values are set
    /// per step by the analyses layer (<see cref="SetCompanion"/>); the stamp
    /// starts as an open circuit (g = 0, Ieq = 0).</summary>
    public CompanionStamp AddCompanion(int a, int b)
    {
        var g = AddConductanceCells(a, b);
        var rhsA = AddRhsSlot(Row(a));
        var rhsB = AddRhsSlot(Row(b));
        return new CompanionStamp(g, rhsA, rhsB);
    }

    /// <summary>Tier-3. Independent voltage source pos→neg: one auxiliary branch
    /// row with static ±1 incidence. The value is a tier-1 RHS write.</summary>
    public VSourceStamp AddVoltageSource(int pos, int neg, double volts)
    {
        var rp = Row(pos);
        var rn = Row(neg);
        var br = AllocAuxRow();
        // Incidence: branch current leaves pos, enters neg (KCL columns);
        // constraint row reads V(pos) − V(neg) = value (RHS).
        AddValueCell(rp, br, 1.0);
        AddValueCell(br, rp, 1.0);
        AddValueCell(rn, br, -1.0);
        AddValueCell(br, rn, -1.0);
        var rhs = AddRhsSlot(br);
        var stamp = new VSourceStamp(br, rhs);
        SetVSourceValue(stamp, volts);
        return stamp;
    }

    /// <summary>Tier-3. Independent current source, RHS only.</summary>
    public ISourceStamp AddCurrentSource(int from, int to, double amps)
    {
        var rhsFrom = AddRhsSlot(Row(from));
        var rhsTo = AddRhsSlot(Row(to));
        var stamp = new ISourceStamp(rhsFrom, rhsTo);
        SetCurrentSource(stamp, amps);
        return stamp;
    }

    /// <summary>Tier-3. Ideal transformer with turns ratio n (V_a = n·V_b): two
    /// coupled auxiliary rows (api.md §6). Primary port a = (aPos,aNeg),
    /// secondary port b = (bPos,bNeg). Branch currents flow into each pos
    /// terminal; power into the device sums to zero by the two constraints.</summary>
    public TransformerStamp AddIdealTransformer(int aPos, int aNeg, int bPos, int bNeg, double turnsRatio)
    {
        var raP = Row(aPos); var raN = Row(aNeg);
        var rbP = Row(bPos); var rbN = Row(bNeg);
        var p = AllocAuxRow();   // primary branch current i_p
        var s = AllocAuxRow();   // secondary branch current i_s

        // KCL incidence: i_p into aPos/out aNeg, i_s into bPos/out bNeg.
        AddValueCell(raP, p, 1.0);
        AddValueCell(raN, p, -1.0);
        AddValueCell(rbP, s, 1.0);
        AddValueCell(rbN, s, -1.0);

        // Voltage-ratio constraint (row p): V(aPos) − V(aNeg) − n·(V(bPos) − V(bNeg)) = 0.
        AddValueCell(p, raP, 1.0);
        AddValueCell(p, raN, -1.0);
        var slotPbPos = AddValueCell(p, rbP, 0.0);   // set to −n below
        var slotPbNeg = AddValueCell(p, rbN, 0.0);   // set to +n below

        // Current-ratio constraint (row s): i_s + n·i_p = 0.
        AddValueCell(s, s, 1.0);
        var slotSp = AddValueCell(s, p, 0.0);        // set to +n below

        var stamp = new TransformerStamp(p, s, slotPbPos, slotPbNeg, slotSp);
        SetTransformerRatio(stamp, turnsRatio);
        return stamp;
    }

    // ================================================================= drive

    /// <summary>Tier 2. Set a resistor's conductance (clamped to the legal
    /// range). Rewrites the four conductance slots and bumps the values epoch.</summary>
    public void SetConductance(in ConductanceStamp s, double siemens)
    {
        var g = Clamp(siemens);
        TrackConductance(g);
        WriteSlot(s.Aa, g);
        WriteSlot(s.Bb, g);
        WriteSlot(s.Ab, -g);
        WriteSlot(s.Ba, -g);
        _valuesEpoch++;
    }

    /// <summary>Tier 2. Relay contact toggle (in-matrix; solver.md).</summary>
    public void SetSwitch(in ConductanceStamp s, bool closed)
        => SetConductance(s, closed ? MaxConductance : MinConductance);

    /// <summary>Tier 2. A companion's parallel conductance (Norton form). g is
    /// NOT range-clamped — a stiff C/dt is legal (it is tracked for the debug
    /// spread warning). For FIXED dt this is set once at setup: the conductance
    /// then holds constant and only <see cref="SetCompanionCurrent"/> runs per
    /// step, so a linear storage island lives entirely in tier 1 (solver.md).</summary>
    public void SetCompanionConductance(in CompanionStamp c, double siemens)
    {
        TrackConductance(siemens);
        WriteSlot(c.G.Aa, siemens);
        WriteSlot(c.G.Bb, siemens);
        WriteSlot(c.G.Ab, -siemens);
        WriteSlot(c.G.Ba, -siemens);
        _valuesEpoch++;
    }

    /// <summary>Tier 1. A companion's history current source Ieq (RHS only, no
    /// refactor): injected at terminal a, extracted at b. The analyses layer owns
    /// its sign and updates it every step from the prior state (V_prev / I_prev).</summary>
    public void SetCompanionCurrent(in CompanionStamp c, double iEq)
    {
        SetRhsSlot(c.RhsA, iEq);
        SetRhsSlot(c.RhsB, -iEq);
    }

    /// <summary>Convenience for setup or a dt change: set both companion halves
    /// at once (tier 2, since the conductance moves).</summary>
    public void SetCompanion(in CompanionStamp c, double siemens, double iEq)
    {
        SetCompanionConductance(c, siemens);
        SetCompanionCurrent(c, iEq);
    }

    /// <summary>Tier 1. Independent voltage source value (RHS only).</summary>
    public void SetVSourceValue(in VSourceStamp s, double volts)
        => SetRhsSlot(s.RhsSlot, volts);

    /// <summary>Tier 1. Independent current source value (RHS only).</summary>
    public void SetCurrentSource(in ISourceStamp s, double amps)
    {
        SetRhsSlot(s.RhsFrom, -amps);
        SetRhsSlot(s.RhsTo, amps);
    }

    /// <summary>Tier 2. Transformer turns ratio (three coupled value slots).</summary>
    public void SetTransformerRatio(in TransformerStamp s, double turnsRatio)
    {
        WriteSlot(s.SlotPbPos, -turnsRatio);
        WriteSlot(s.SlotPbNeg, turnsRatio);
        WriteSlot(s.SlotSp, turnsRatio);
        _valuesEpoch++;
    }

    // =============================================================== analyze

    /// <summary>Tier 3. Deduplicate all coordinate slots into a backend pattern,
    /// freeze the slot→pattern and node-diagonal maps, size the value/RHS/solution
    /// buffers, and run <see cref="ISolverBackend.Analyze"/>. Allocates freely;
    /// after this call the hot paths are heap-free. No stamps may be added after
    /// Analyze.</summary>
    public void Analyze()
    {
        var dim = _rowCount;
        var coord = new Dictionary<(int, int), int>(_valueSlotCount + _nodeRowCount);
        var entries = new List<MatrixEntry>(_valueSlotCount + _nodeRowCount);

        int Intern(int r, int c)
        {
            if (!coord.TryGetValue((r, c), out var idx))
            {
                idx = entries.Count;
                coord[(r, c)] = idx;
                entries.Add(new MatrixEntry(r, c));
            }
            return idx;
        }

        // Node diagonals first so gmin always has a home, even for a node no
        // stamp touches on its diagonal (a purely floating node).
        _nodeDiagPattern = new int[_nodeRowCount];
        for (var r = 0; r < _nodeRowCount; r++)
            _nodeDiagPattern[r] = Intern(r, r);

        _slotToPattern = new int[_valueSlotCount];
        for (var k = 0; k < _valueSlotCount; k++)
            _slotToPattern[k] = Intern(_slotRow[k], _slotCol[k]);

        _pattern = entries.ToArray();
        _patternValues = new double[_pattern.Length];
        _rhs = new double[dim];
        _solutionFront = new double[dim];
        _solutionBack = new double[dim];

        _backend.Analyze(dim, _pattern.AsMemory());

        _patternEpoch++;
        _analyzed = true;
        _numericReady = false;
        _factoredPatternEpoch = -1;
        _factoredValuesEpoch = -1;
        _factoredDt = double.NaN;
        _fault = FaultInfo.None;
        _minSetG = double.PositiveInfinity;
        _maxSetG = 0.0;
        _ratioWarned = false;
    }

    // ================================================================ solve

    /// <summary>Tier 2. Refactorize iff the (pattern, values, dt) signature moved
    /// since the last factorization. Assembles values (gmin + duplicate-summed
    /// slots), then factors. NEVER throws: a singular matrix (after gmin) is
    /// caught and surfaced as <see cref="SolveStatus.Singular"/> with the
    /// previous solution retained and <see cref="Faulted"/> set.</summary>
    public SolveStatus FactorizeIfDirty()
    {
        Debug.Assert(_analyzed, "FactorizeIfDirty before Analyze");

        // Signature match is computed independently of _numericReady so an
        // UNCHANGED *faulted* island short-circuits too. solver.md Failure
        // Handling: a singular island holds its previous solution and retries
        // only on the next tier-2/3 change — re-running the (allocating) cold
        // re-pivot every tick on a pathological circuit is exactly the R9 cliff
        // to avoid. The singular path below records this signature; a repairing
        // change bumps an epoch (or dt), flipping this false to force the retry.
        var signatureUnchanged = _patternEpoch == _factoredPatternEpoch
                                 && _valuesEpoch == _factoredValuesEpoch
                                 && _dt.Equals(_factoredDt);
        if (signatureUnchanged && (_numericReady || _fault.Status != SolveStatus.Ok))
            return _fault.Status;   // Ok, or a still-standing fault on an unchanged system

        AssembleValues();
        try
        {
            _backend.Factorize(_patternValues);
        }
        catch (InvalidOperationException)
        {
            // Singular after gmin (solver.md Failure Handling). Hold the last
            // published solution; the netlist layer maps the row to a component.
            // Record the attempted signature so this faulted island does NOT
            // re-factorize on an unchanged tick — only a repairing tier-2/3
            // change (which bumps an epoch or dt) triggers the retry.
            _numericReady = false;
            _fault = new FaultInfo(SolveStatus.Singular, _backend.LastSingularColumn);
            _factoredPatternEpoch = _patternEpoch;
            _factoredValuesEpoch = _valuesEpoch;
            _factoredDt = _dt;
            return SolveStatus.Singular;
        }

        _numericReady = true;
        _factoredPatternEpoch = _patternEpoch;
        _factoredValuesEpoch = _valuesEpoch;
        _factoredDt = _dt;
        _fault = FaultInfo.None;
        return SolveStatus.Ok;
    }

    /// <summary>Tier 1. Back-substitute the current RHS on the cached factors and
    /// publish. If factorization is not valid (a standing singular fault), the
    /// previous solution is held and the fault returned. A non-finite result is
    /// never published (api.md §20): the previous solution stays and the island
    /// faults <see cref="SolveStatus.NonFinite"/>.</summary>
    public SolveStatus Solve()
    {
        Debug.Assert(_analyzed, "Solve before Analyze");

        if (!_numericReady)
            return _fault.Status == SolveStatus.Ok ? SolveStatus.Singular : _fault.Status;

        AssembleRhs();
        _backend.Solve(_rhs, _solutionBack);

        var badRow = FirstNonFiniteRow(_solutionBack);
        if (badRow >= 0)
        {
            Debug.Assert(false, "non-finite entry in solution vector");
            _fault = new FaultInfo(SolveStatus.NonFinite, badRow);
            return SolveStatus.NonFinite;   // do NOT publish; front buffer retained
        }

        // Publish: flip the double buffer.
        (_solutionFront, _solutionBack) = (_solutionBack, _solutionFront);
        _fault = FaultInfo.None;
        return SolveStatus.Ok;
    }

    // =============================================================== readback

    /// <summary>Node potential from the published solution. The reference node
    /// reads exactly 0 (eliminated); an unsolved/faulted island reads its last
    /// published (or zero) value.</summary>
    public double ReadPotential(int node)
    {
        var r = _nodeRow[node];
        return r < 0 ? 0.0 : _solutionFront[r];
    }

    /// <summary>Branch current at an auxiliary row (a voltage source's branch,
    /// or a transformer's i_p / i_s). Signed by the incidence convention: a
    /// voltage-source branch current is positive when leaving the + node through
    /// the source.</summary>
    public double ReadFlow(int auxRow) => _solutionFront[auxRow];

    // ================================================================ testing

    /// <summary>Test/diagnostic: worst |(A·x − b)| over the NODE rows of the
    /// published solution — the Kirchhoff current-law residual, gmin shunt
    /// included as a real branch. ~machine precision on a solved island.</summary>
    internal double MaxNodeKclResidual()
    {
        AssembleValues();     // reflect current values; cheap, zero-alloc
        AssembleRhs();
        var x = _solutionFront;
        Span<double> ax = _rowCount <= 256 ? stackalloc double[_rowCount] : new double[_rowCount];
        ax.Clear();
        for (var k = 0; k < _pattern.Length; k++)
            ax[_pattern[k].Row] += _patternValues[k] * x[_pattern[k].Column];
        double worst = 0;
        for (var r = 0; r < _nodeRowCount; r++)
            worst = Math.Max(worst, Math.Abs(ax[r] - _rhs[r]));
        return worst;
    }

    /// <summary>Test/diagnostic: current view of the published solution vector in
    /// raw MNA order (front buffer).</summary>
    internal ReadOnlySpan<double> PublishedVector => _solutionFront;

    // ================================================================ internals

    private int Row(int node) => node < 0 ? -1 : _nodeRow[node];

    private int AllocAuxRow() => _rowCount++;

    /// <summary>Reserve the four cells of a two-terminal conductance; drops any
    /// cell touching the reference row.</summary>
    private ConductanceStamp AddConductanceCells(int a, int b)
    {
        var ra = Row(a);
        var rb = Row(b);
        var aa = AddValueCell(ra, ra, 0.0);
        var bb = AddValueCell(rb, rb, 0.0);
        var ab = AddValueCell(ra, rb, 0.0);
        var ba = AddValueCell(rb, ra, 0.0);
        return new ConductanceStamp(aa, bb, ab, ba);
    }

    /// <summary>Reserve one coordinate value slot; returns −1 when either index
    /// is the eliminated reference (the cell does not exist).</summary>
    private int AddValueCell(int row, int col, double value)
    {
        if (row < 0 || col < 0) return -1;
        if (_analyzed) throw new InvalidOperationException("Circuit: stamp added after Analyze");
        if (_valueSlotCount == _valueSlots.Length)
        {
            var cap = _valueSlots.Length * 2;
            Array.Resize(ref _slotRow, cap);
            Array.Resize(ref _slotCol, cap);
            Array.Resize(ref _valueSlots, cap);
        }
        var slot = _valueSlotCount++;
        _slotRow[slot] = row;
        _slotCol[slot] = col;
        _valueSlots[slot] = value;
        return slot;
    }

    /// <summary>Reserve one RHS slot; −1 when the row is the reference.</summary>
    private int AddRhsSlot(int row)
    {
        if (row < 0) return -1;
        if (_analyzed) throw new InvalidOperationException("Circuit: stamp added after Analyze");
        if (_rhsSlotCount == _rhsSlots.Length)
        {
            var cap = _rhsSlots.Length * 2;
            Array.Resize(ref _rhsSlotRow, cap);
            Array.Resize(ref _rhsSlots, cap);
        }
        var slot = _rhsSlotCount++;
        _rhsSlotRow[slot] = row;
        _rhsSlots[slot] = 0.0;
        return slot;
    }

    private void WriteSlot(int slot, double value)
    {
        if (slot >= 0) _valueSlots[slot] = value;
    }

    private void SetRhsSlot(int slot, double value)
    {
        if (slot >= 0) _rhsSlots[slot] = value;
    }

    /// <summary>Sum stamp slots into the deduplicated pattern, gmin on every node
    /// diagonal. Zero-alloc after Analyze.</summary>
    private void AssembleValues()
    {
        var pv = _patternValues;
        Array.Clear(pv, 0, pv.Length);
        for (var r = 0; r < _nodeRowCount; r++)
            pv[_nodeDiagPattern[r]] = Gmin;
        var map = _slotToPattern;
        var vs = _valueSlots;
        for (var k = 0; k < _valueSlotCount; k++)
            pv[map[k]] += vs[k];
        WarnOnExtremeSpread();
    }

    /// <summary>Sum RHS slots into the assembled right-hand side. Zero-alloc.</summary>
    private void AssembleRhs()
    {
        var rhs = _rhs;
        Array.Clear(rhs, 0, rhs.Length);
        var rows = _rhsSlotRow;
        var vs = _rhsSlots;
        for (var k = 0; k < _rhsSlotCount; k++)
            rhs[rows[k]] += vs[k];
    }

    private static double Clamp(double siemens)
    {
        if (siemens < MinConductance) return MinConductance;
        if (siemens > MaxConductance) return MaxConductance;
        return siemens;
    }

    private void TrackConductance(double g)
    {
        if (!(g > 0.0)) return;          // ignore zero/negative (an open companion)
        if (g < _minSetG) _minSetG = g;
        if (g > _maxSetG) _maxSetG = g;
    }

    [Conditional("DEBUG")]
    private void WarnOnExtremeSpread()
    {
        if (_ratioWarned || _minSetG == double.PositiveInfinity || _minSetG <= 0.0) return;
        if (_maxSetG / _minSetG > ExtremeRatioWarn)
        {
            _ratioWarned = true;
            Debug.WriteLine(
                $"Circuit: extreme conductance ratio {_maxSetG / _minSetG:E1} " +
                $"(min {_minSetG:E2} S, max {_maxSetG:E2} S) exceeds {ExtremeRatioWarn:E0} — conditioning risk.");
        }
    }

    private static int FirstNonFiniteRow(ReadOnlySpan<double> v)
    {
        for (var i = 0; i < v.Length; i++)
        {
            var d = v[i];
            if (double.IsNaN(d) || double.IsInfinity(d)) return i;
        }
        return -1;
    }
}
