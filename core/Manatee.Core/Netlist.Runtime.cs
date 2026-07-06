using System;
using System.Collections.Generic;
using System.Diagnostics;
using Manatee.Core.Solver;

namespace Manatee.Core;

// Phase-3 island runtime: the numeric seam that turns the retained document into
// per-island MNA systems and drives the internal Circuit through
// Analyze/Factorize/Solve (api.md §4, §16, §17; solver.md). One Circuit per
// island slot, rebuilt at Solve from the document (the "rebuild restamps from
// the document" invariant, §17). Verbs push tier-1 (RHS) / tier-2 (conductance)
// values into the live Circuit incrementally; a merge or removal marks the
// island's runtime stale and it is rebuilt once per Solve.
public sealed partial class Netlist
{
    // Stamp-kind tags for the per-component descriptor stored in the SoA. The
    // descriptor's int slots are Circuit value/RHS/aux-row indices, valid ONLY
    // for the island's current runtime (rewritten at each rebuild).
    private const byte StampNone = 0;         // capacitor (DC open), diode (Newton not yet)
    private const byte StampConductance = 1;  // resistor / switch / inductor (DC short)
    private const byte StampVSource = 2;
    private const byte StampISource = 3;
    private const byte StampTransformer = 4;
    private const byte StampCapacitor = 5;    // Backward-Euler companion (transient): G = C/dt ‖ history source
    private const byte StampInductor = 6;     // Backward-Euler companion (transient): G = dt/L ‖ history source
    private const byte StampDiode = 7;        // Newton companion (nonlinear): Geq ‖ Ieq, re-stamped per iteration

    // Companion-kind tags for the transient substep loop (rt.CompKind).
    private const byte CompCap = 0;
    private const byte CompInd = 1;

    // ── Diode / Newton-Raphson constants (solver.md Analyses / Failure Handling) ──
    //
    // Thermal voltage Vt = k·T/q at a FIXED T = 300 K (the model is isothermal — no
    // self-heating): k = 1.380649e-23 J/K, q = 1.602176634e-19 C ⇒
    //   Vt = 1.380649e-23 · 300 / 1.602176634e-19 = 0.025851990… V.
    // The junction is I = Is·(exp(V/(n·Vt)) − 1); n·Vt is the per-diode scale.
    private const double ThermalVoltage300 = 0.025851990;

    // Newton dual-convergence tolerances (SPICE-conventional). A step converges when
    // BOTH hold for every diode: the junction-voltage update is small (reltol·|V| +
    // vntol) AND the junction-current change is small (reltol·|I| + abstol). No
    // transcendental appears in these gates — they are |Δ| ≤ scaled-tolerance
    // comparisons only (determinism rule; the exp/log live in the model, not the gate).
    private const double NewtonReltol = 1e-3;
    private const double NewtonVntol = 1e-6;   // V
    private const double NewtonAbstol = 1e-9;  // A
    private const int MaxNewtonIterations = 50;         // iteration cap (solver.md)
    private const int SourceSteppingLevels = 10;        // fallback-ladder ramp step count (solver.md rung 2)

    // Diode companion conductance is clamped to the solver's legal conductance range
    // (solver.md conductance-range policy: [1e-9, 1e3] S). Reverse bias floors Geq at
    // 1e-9 S (near-open, still nonsingular); a hard-forward exp is ceilinged at 1e3 S.
    private const double DiodeMinConductance = 1e-9;
    private const double DiodeMaxConductance = 1e3;

    // AC subcycle hysteresis half-band (§5, solver.md): N is re-decided only when the
    // required substep rate drifts past ±15% of the rate N was last chosen for, so the
    // substep dt — and every BE companion conductance — holds constant across
    // mechanical-speed jitter. Exposed on SubstepPlan.HysteresisBand for AC islands.
    private const double SubstepHysteresisBand = 0.15;

    // Sine phase accumulators are wrapped to [0, 2π) so precision does not decay over
    // long runs (a determinism requirement): sin(θ) = sin(θ − 2π), so the wrap is a
    // value-preserving modular identity. The wrap is a while-loop so it re-normalizes
    // regardless of increment size — a sine source in a non-Mixed profile solves with
    // n=1 substep and increment ω·tickDt, which can exceed 2π. On the AC hot path the
    // per-substep increment is < 2π (AC islands sample ≥ 20×/cycle), so the loop still
    // executes at most once there — no hot-path cost.
    private const double TwoPi = 2.0 * Math.PI;

    // Modeling resistances shared by the STAMP path (BuildRuntime/StampComponent,
    // closed-coupler branch) and the READBACK path (BranchCurrent). They MUST stay
    // reciprocals of the solver's conductance-range policy (Solver/Circuit.cs
    // MaxConductance = 1e3 S, MinConductance = 1e-9 S), which is what the matrix
    // actually clamps to — a single literal per role keeps stamp and readback from
    // silently diverging (solver.md conductance-range policy).
    private const double ClosedSwitchOhms = 1e-3;    // 1 / MaxConductance
    private const double OpenSwitchOhms = 1e9;        // 1 / MinConductance
    private const double InductorDcShortOhms = 1e-3;  // DC operating point: inductor ≈ 1 mΩ short
    private const double CouplerBridgeOhms = 1e-3;    // closed galvanic breaker series branch (api.md §7)

    // ── The three island-dirty flags (cross-stage invariant) ──
    // An island's rebuild/refactor scheduling is governed by three overlapping
    // flags; keep their meanings distinct:
    //
    //   _iStatus[slot] == Dirty   — needs a numeric pass (factor and/or RHS solve).
    //                               Set by any tier-1/2/3 touch; cleared to Ready by
    //                               a successful NumericSolveIsland.
    //   _iNeedsRebuild[slot]      — CONNECTIVITY must be recomputed from scratch:
    //                               a removal, or a galvanic coupler Open. Handled by
    //                               the structural pass (RebuildIsland) before the
    //                               numeric pass; set by MarkIslandNeedsRebuild,
    //                               inherited across a merge (UnionNodes), cleared by
    //                               RebuildIsland on each resulting island.
    //   _iRuntimeStale[slot]      — the live Circuit no longer covers the membership
    //                               (a merge grew it, or RebuildIsland just replaced
    //                               it): rebuild the Circuit from the document at the
    //                               next numeric pass. Set by UnionNodes/RebuildIsland,
    //                               cleared by BuildRuntime.
    //
    // The load-bearing rule (RuntimeForComp): _iNeedsRebuild || _iRuntimeStale means
    // there is NO live Circuit to accept an incremental tier-1/2 verb write this tick,
    // so the write lands in the document only and the coming rebuild restamps from it
    // (api.md §17). A future editor must set the flag matching the actual obligation:
    // connectivity change ⇒ _iNeedsRebuild; membership-covering-only ⇒ _iRuntimeStale.
    //
    // ── Per-island numeric runtime (indexed by island slot) ──
    private IslandRuntime?[] _iRuntime = new IslandRuntime?[4];
    private bool[] _iRuntimeStale = new bool[4];   // membership changed ⇒ needs a fresh Circuit at Solve
    private FaultDiagnostic[] _iFault = new FaultDiagnostic[4];
    // Second participating component for a ContradictorySources fault (the first is
    // FaultDiagnostic.Worst); DescribeFault emits both (api.md §11, §20).
    private ComponentRef[] _iFaultSecond = new ComponentRef[4];
    private bool[] _iStepping = new bool[4];        // debug single-writer-per-island tripwire

    // ── Per-island AC subcycle plan (indexed by island slot) ──
    // N and its substep dt, plus the raw required samples/tick that N was last decided
    // for (the hysteresis reference). Published via IslandHandle.Plan.
    private int[] _iSubstepN = new int[4];          // 0 ⇒ never planned; 1 ⇒ single step (DC / non-AC)
    private double[] _iSubstepDt = new double[4];
    private double[] _iSubstepRawRef = new double[4];

    // ── Per-component stamp descriptor (parallel to the component SoA) ──
    private byte[] _cStampKind = new byte[8];
    private int[] _cStamp0 = new int[8], _cStamp1 = new int[8], _cStamp2 = new int[8], _cStamp3 = new int[8], _cStamp4 = new int[8], _cStamp5 = new int[8];

    // ── Per-node numeric mapping ──
    private int[] _nCircuitNode = new int[4];   // node slot → local Circuit node index
    private int[] _nRtSlot = new int[4];         // island slot the mapping above is valid for (−1 = none)

    // TickStats sealing: a tick's counters accumulate from its first mutation
    // (drive-phase Adjust no-ops included) through Solve; the tick is "sealed" at
    // Solve end and at the LastTickStats readback barrier (§9). The next mutation
    // or Step after a sealed tick starts a fresh tick.
    private bool _tickStatsSealed;
    private int _stepInFlight;                    // §9 phase assert: no LastTickStats read mid-Step

    // The tick index the CURRENT solve is running at — threaded into the post-solve
    // limit evaluation (LimitEvent.TickIndex, api.md §12).
    private long _evalTickIndex;

    // Build scratch (shape-time only).
    private readonly List<int> _buildNodes = new();
    private readonly List<int> _numericScratch = new();
    private readonly List<int> _buildCompComps = new();
    private readonly List<byte> _buildCompKind = new();
    private readonly List<CompanionStamp> _buildCompStamps = new();
    private readonly List<int> _buildCompLocalA = new();
    private readonly List<int> _buildCompLocalB = new();
    private readonly List<int> _buildSineComps = new();
    private readonly List<VSourceStamp> _buildSineStamps = new();
    private readonly List<int> _buildDiodeComps = new();
    private readonly List<CompanionStamp> _buildDiodeStamps = new();
    private readonly List<int> _buildDiodeLocalA = new();
    private readonly List<int> _buildDiodeLocalC = new();
    private readonly List<int> _buildMemberComps = new();   // ALL island member component slots

    // Current build mode, threaded into StampComponent (shape-time only, single-writer).
    private bool _buildCompanions;
    private double _buildSubstepDt;

    private sealed class IslandRuntime
    {
        public Circuit Circuit = null!;
        public int[] NodeSlots = Array.Empty<int>();  // local index → node slot (fault/readback reverse)
        public int NodeCount;
        public int[] AuxRows = Array.Empty<int>();     // aux row → owning component slot (fault attribution)
        public int[] AuxComps = Array.Empty<int>();
        public int AuxCount;

        // ── Transient (Backward-Euler) state ──
        public bool HasCompanions;   // built with cap/inductor companions (transient), vs DC operating point
        public double SubstepDt;     // dt companion conductances were stamped at (= Circuit.Dt)

        // Companion elements, contiguous for the zero-alloc substep loop (SoA):
        // component slot, kind (cap/inductor), its CompanionStamp, and local circuit
        // node indices of the a/b terminals (for post-solve state readback).
        public int[] CompComps = Array.Empty<int>();
        public byte[] CompKind = Array.Empty<byte>();
        public CompanionStamp[] CompStamps = Array.Empty<CompanionStamp>();
        public int[] CompLocalA = Array.Empty<int>();
        public int[] CompLocalB = Array.Empty<int>();
        public int CompCount;

        // Sine sources driven per substep: component slot + its VSource branch stamp.
        public int[] SineComps = Array.Empty<int>();
        public VSourceStamp[] SineStamps = Array.Empty<VSourceStamp>();
        public int SineCount;

        // ── Nonlinear (diode / Newton-Raphson) state ──
        // The diodes stamped into this Circuit as linearized companions (Geq ‖ Ieq),
        // contiguous for the Newton loop. DiodeVd carries the working junction voltage
        // (warm-started from the component's persisted state each solve point);
        // DiodeGeq/DiodeIeq are the LAST-STAMPED values so an unchanged relinearization
        // skips the matrix write (value-identical fast path ⇒ a converged nonlinear
        // island re-solves in one iteration with NO refactor — solver.md tier note).
        public bool HasNonlinear;
        public int[] DiodeComps = Array.Empty<int>();
        public CompanionStamp[] DiodeStamps = Array.Empty<CompanionStamp>();
        public int[] DiodeLocalA = Array.Empty<int>();   // local circuit node of the anode (−1 = reference)
        public int[] DiodeLocalC = Array.Empty<int>();   // local circuit node of the cathode (−1 = reference)
        public double[] DiodeVd = Array.Empty<double>();
        public double[] DiodeVnew = Array.Empty<double>();   // post-solve junction voltage, staged per iteration
        public double[] DiodeGeq = Array.Empty<double>();
        public double[] DiodeIeq = Array.Empty<double>();
        public int DiodeCount;

        // ALL component slots whose A-terminal node belongs to this island (built once
        // per rebuild). The per-substep energy audit and the limit scan iterate THIS
        // instead of the whole component arena, so an island's per-tick cost scales with
        // ITS size, not the whole netlist's (island-independence — the finder's concern).
        public int[] MemberComps = Array.Empty<int>();
        public int MemberCount;

        // Last-good published vector, captured before a Newton solve point so a
        // diverged solve can hold it (solver.md Failure Handling). Sized to the
        // Circuit dimension at build; only allocated for nonlinear islands.
        public double[] NewtonSave = Array.Empty<double>();

        // Context the Newton fallback ladder needs but that is not on the hot path:
        // the owning island slot (for the source-stepping component scan) and whether
        // the current solve is the DC operating point (sine sources pinned to 0).
        public int IslandSlot;
        public bool OperatingPoint;
    }

    // ==================================================================== ticks

    /// <summary>Begin (reset) the tick's counters iff the previous tick was
    /// sealed. Called at every mutation/solve entry so drive-phase counters and
    /// Solve counters share one tick window (§9).</summary>
    private void BeginTick()
    {
        if (_tickStatsSealed) { _lastTickStats = default; _tickStatsSealed = false; }
    }

    // ================================================================ verb push

    // The island runtime a component's tier-1/2 write should reach, or null when
    // the island has no live Circuit for this write (never built, pending a
    // rebuild, or membership-stale from a merge) — in which case the write lands
    // in the document only and the coming rebuild restamps from it (§17).
    private IslandRuntime? RuntimeForComp(int compSlot)
    {
        var isl = _nIsland[_cA[compSlot]];
        if (isl < 0) return null;
        if (_iNeedsRebuild[isl] || _iRuntimeStale[isl]) return null;
        return _iRuntime[isl];
    }

    private void PushConductance(int slot, double ohms)
    {
        var rt = RuntimeForComp(slot);
        if (rt is null || _cStampKind[slot] != StampConductance) return;
        rt.Circuit.SetConductance(ConductanceOf(slot), 1.0 / ohms);
    }

    private void PushSwitch(int slot, bool closed)
    {
        var rt = RuntimeForComp(slot);
        if (rt is null || _cStampKind[slot] != StampConductance) return;
        rt.Circuit.SetSwitch(ConductanceOf(slot), closed);
    }

    private void PushVSource(int slot, double volts)
    {
        var rt = RuntimeForComp(slot);
        if (rt is null || _cStampKind[slot] != StampVSource) return;
        rt.Circuit.SetVSourceValue(new VSourceStamp(_cStamp0[slot], _cStamp1[slot]), volts);
    }

    private void PushISource(int slot, double amps)
    {
        var rt = RuntimeForComp(slot);
        if (rt is null || _cStampKind[slot] != StampISource) return;
        rt.Circuit.SetCurrentSource(new ISourceStamp(_cStamp0[slot], _cStamp1[slot]), amps);
    }

    private void PushTransformerRatio(int slot, double ratio)
    {
        var rt = RuntimeForComp(slot);
        if (rt is null || _cStampKind[slot] != StampTransformer) return;
        rt.Circuit.SetTransformerRatio(
            new TransformerStamp(_cStamp0[slot], _cStamp1[slot], _cStamp2[slot], _cStamp3[slot], _cStamp4[slot]), ratio);
    }

    private ConductanceStamp ConductanceOf(int slot)
        => new(_cStamp0[slot], _cStamp1[slot], _cStamp2[slot], _cStamp3[slot]);

    private CompanionStamp CompanionOf(int slot)
        => new(new ConductanceStamp(_cStamp0[slot], _cStamp1[slot], _cStamp2[slot], _cStamp3[slot]),
               _cStamp4[slot], _cStamp5[slot]);

    // Tier-2: a capacitor/inductor VALUE change (Adjust). At DC the element is open
    // (cap) or a fixed short (inductor), so only a live transient runtime with the
    // companion in-matrix pays — its conductance G = C/dt or dt/L is restamped for the
    // runtime's substep dt and a single refactor follows. In DC-op mode the write lands
    // in the document only (BE history at the next transient tick picks up _cValue).
    private void PushStorageValue(int slot)
    {
        var rt = RuntimeForComp(slot);
        if (rt is null || !rt.HasCompanions) return;
        var kind = _cStampKind[slot];
        if (kind == StampCapacitor)
            rt.Circuit.SetCompanionConductance(CompanionOf(slot), _cValue[slot] / rt.SubstepDt);
        else if (kind == StampInductor)
            rt.Circuit.SetCompanionConductance(CompanionOf(slot), rt.SubstepDt / _cValue[slot]);
        else return;
        MarkComponentDirty(slot);
    }

    // ============================================================= solve driver

    /// <summary>The solve pipeline shared by <see cref="SolveOperatingPoint"/> and
    /// <see cref="Solve(in TickClock)"/>: run the structural rebuild pass (§16/§17
    /// — one rebuild per island per tick, rebuild supersedes merge), then the
    /// numeric pass (build/refactor/solve/publish) over dirty units in the
    /// rebuild-stable <c>Islands.Ids</c> order (§9).</summary>
    private void RunSolve(bool operatingPoint, double dt, long tickIndex)
    {
        BeginTick();
        _evalTickIndex = tickIndex;

        // ── Structural pass: one rebuild per island scheduled for it. ──
        _dirtyScratch.Clear();
        for (var s = 0; s < _iSlotCount; s++)
            if (_iAlive[s] && _iNeedsRebuild[s])
                _dirtyScratch.Add(s);
        foreach (var s in _dirtyScratch)
        {
            if (!_iAlive[s] || !_iNeedsRebuild[s]) continue;
            RebuildIsland(s);
            _lastTickStats.IslandRebuilds++;
        }

        // ── Numeric pass: dirty (or all) islands, deterministic Ids order. ──
        EnsureUnitsFresh();
        var ids = IslandIdsOrdered();
        // Copy slots out first: NumericSolveIsland can allocate but never mutates
        // island membership, so the Ids span stays valid across the loop.
        _numericScratch.Clear();
        for (var i = 0; i < ids.Length; i++) _numericScratch.Add(ids[i].Slot);
        foreach (var slot in _numericScratch)
        {
            if (!_iAlive[slot]) continue;
            if (RouteAsUnit(slot))
            {
                // Boundary-coupled islands are one scheduling unit: only the lead solves
                // it (in lockstep); non-lead members are covered by the lead's StepUnit.
                if (!IsUnitLead(slot)) continue;
                if (operatingPoint || UnitNeedsSolve(slot)) StepUnit(slot, dt, operatingPoint);
                continue;
            }
            // Solo island: a transient one (sine/storage) advances time every tick, so
            // it solves even when untouched (status Ready) — not just dirty (solver.md).
            // A stale runtime (e.g. right after FromCanonical, which nulls every island's
            // Circuit) means there is no live matrix to read: force a build+solve
            // regardless of status, matching the Step() gate below — otherwise a Ready,
            // non-transient island loaded from a memento would be skipped and read 0 V.
            if (operatingPoint || _iStatus[slot] == (byte)IslandStatus.Dirty || _iRuntimeStale[slot] || IslandIsTransient(slot))
                NumericSolveIsland(slot, dt, operatingPoint);
        }

        // ── Limits pass: every Ready island, every tick (post-solve; a fuse carrying
        //    a steady overload keeps heating even if the solver did not re-solve a
        //    static island). Skipped at the operating point (no time integral). ──
        if (!operatingPoint)
            for (var s = 0; s < _iSlotCount; s++)
                if (_iAlive[s] && _iStatus[s] == (byte)IslandStatus.Ready
                    && !LimitsEvaluatedThisTick(s, tickIndex))
                    EvaluateIslandLimits(s, dt, tickIndex);

        _tickStatsSealed = true;
    }

    /// <summary>Ensure the island's Circuit is current, then factor (tier 2 iff
    /// values moved) and back-substitute (tier 1), publishing on success. A
    /// singular factorization or a non-finite solve drives the island to
    /// <see cref="IslandStatus.Faulted"/> (api.md §20) and holds last-good.</summary>
    private void NumericSolveIsland(int slot, double tickDt, bool operatingPoint)
    {
        ScanIsland(slot, out var hasSine, out var hasStorage, out _, out var maxFreq);

        // Build mode: operating point (caps open, inductors short) vs transient
        // (Backward-Euler companions). A storage-free island stamps identically in
        // both modes, so HasCompanions — the rebuild discriminator — never flips for
        // it and the SolveOperatingPoint↔Solve alternation costs no rebuild (§17).
        var wantCompanions = !operatingPoint && hasStorage;
        var isAc = !operatingPoint && _profileKind == SolverProfile.Regime.Mixed && hasSine;

        int n; double substepDt;
        if (isAc) { n = PlanSubstepN(slot, tickDt, maxFreq); substepDt = tickDt / n; }
        else
        {
            // Non-AC pass (operating point, non-Mixed regime, or a sine-free island):
            // one step. INVALIDATE the hysteresis anchor so a later transient AC solve
            // re-plans N from the true rate. Without this, an op-point between transient
            // ticks writes _iSubstepN=1 while _iSubstepRawRef still holds the prior AC
            // rate; PlanSubstepN then sees drift≈1.0, keeps N=1, and the resumed island
            // undersamples the sine forever. A genuinely non-AC island already has
            // reference 0, so this is a no-op there.
            n = 1; substepDt = tickDt; _iSubstepRawRef[slot] = 0.0;
        }
        _iSubstepN[slot] = n; _iSubstepDt[slot] = operatingPoint ? _profileDt : substepDt;

        var rt = _iRuntime[slot];
        if (rt is null || _iRuntimeStale[slot] || rt.HasCompanions != wantCompanions)
        {
            BuildRuntime(slot, wantCompanions, substepDt);
            rt = _iRuntime[slot]!;
        }
        else if (wantCompanions && rt.SubstepDt != substepDt)
        {
            // N (hence substep dt) changed: a deliberate tier-2 event — restamp every
            // companion conductance for the new dt; one refactor follows (§5, solver.md).
            RestampSubstepDt(rt, substepDt);
        }

        // The Newton fallback ladder reads this off the runtime (sine sources are
        // pinned to their DC offset of 0 at the operating point — §5).
        rt.OperatingPoint = operatingPoint;

        // Operating-point convention (§5 lesson start): sine sources sit at their DC
        // offset (0). A storage-free sine island keeps one runtime across op↔transient,
        // so its source may still hold the last transient sample — pin it to 0 here.
        if (operatingPoint)
            for (var i = 0; i < rt.SineCount; i++)
                rt.Circuit.SetVSourceValue(rt.SineStamps[i], 0.0);

        var wasFaulted = _iStatus[slot] == (byte)IslandStatus.Faulted;
        var singleShot = operatingPoint || (n == 1 && !hasSine && !wantCompanions);
        var status = singleShot
            ? SolveOnce(rt)                                   // DC / plain resistive: one factor+solve
            : StepTransient(rt, n, substepDt);               // BE substep loop (sine + companions)

        if (status != SolveStatus.Ok) { FaultIsland(slot, rt, status, wasFaulted); return; }

        _iStatus[slot] = (byte)IslandStatus.Ready;
        // Energy audit + oscilloscope taps for the single-shot path (the substep
        // loop accumulates its own). Limits are a SEPARATE per-tick pass (a fuse
        // heats even on a static island the solver did not re-solve — RunSolve).
        if (singleShot && !operatingPoint) { AccumulateIslandEnergy(slot, tickDt); SampleTaps(slot); }
        if (wasFaulted)
        {
            _iFault[slot] = default;
            var id = new IslandId(slot, _iGen[slot], _netId);
            _journal.Append(TopologyEventKind.IslandRecovered, default, default, id, default);
            RecordChange(IslandChangeKind.Recovered, id, default);
        }
    }

    /// <summary>One factorize (tier 2 iff the signature moved) + back-substitution
    /// (tier 1). The DC / plain-resistive path — or a full Newton solve when the
    /// island holds nonlinear elements (diodes).</summary>
    private SolveStatus SolveOnce(IslandRuntime rt)
        => rt.HasNonlinear ? NewtonSolvePoint(rt) : FactorAndSolve(rt);

    /// <summary>One linear factorize (tier 2 iff the signature moved) +
    /// back-substitution (tier 1), with the tier accounting. Shared by the linear
    /// solve paths and by each iteration of the Newton loop.</summary>
    private SolveStatus FactorAndSolve(IslandRuntime rt)
    {
        var fs = rt.Circuit.FactorizeIfDirty();
        if (rt.Circuit.DidFactor) _lastTickStats.Refactorizations++;
        if (fs != SolveStatus.Ok) return fs;
        var ss = rt.Circuit.Solve();
        if (rt.Circuit.DidSolve) _lastTickStats.RhsSolves++;
        return ss;
    }

    // ============================================================ Newton-Raphson

    /// <summary>Solve one nonlinear operating point at the island's CURRENT source /
    /// storage-companion RHS state (diodes treated nonlinearly), with the solver.md
    /// fallback ladder on non-convergence:
    /// <list type="number">
    /// <item>Newton from the last converged operating point (the warm start held on
    ///   <c>rt.DiodeVd</c>, never overwritten on failure — "reuse last op point");</item>
    /// <item>source-stepping continuation (ramp all independent sources 0→1);</item>
    /// <item>give up ⇒ <see cref="SolveStatus.Diverged"/>, previous published solution
    ///   held.</item>
    /// </list>
    /// A hard linear fault inside the loop (a singular Jacobian / non-finite solve —
    /// e.g. contradictory ideal sources) short-circuits to that status directly.
    /// Never NaN (junction limiting keeps every iterate finite), never hangs (the
    /// iteration cap).</summary>
    private SolveStatus NewtonSolvePoint(IslandRuntime rt)
    {
        // Hold the last-good vector so a diverged ladder can restore it (0-alloc copy).
        rt.Circuit.CaptureSolution(rt.NewtonSave);

        // Rung 1: Newton from the persisted (warm) junction voltages.
        var s = TryNewton(rt);
        if (s == SolveStatus.Ok) { PersistDiodeState(rt); return SolveStatus.Ok; }
        if (s == SolveStatus.Singular || s == SolveStatus.NonFinite)
        {
            rt.Circuit.RestoreSolution(rt.NewtonSave);   // structural linear fault; hold previous
            return s;
        }

        // Rung 2: source-stepping ramp. Restart the junction voltages from the last
        // converged state so the ramp begins from a clean guess.
        for (var i = 0; i < rt.DiodeCount; i++) rt.DiodeVd[i] = _cStateVar[rt.DiodeComps[i]];
        if (TrySourceStepping(rt) == SolveStatus.Ok) { PersistDiodeState(rt); return SolveStatus.Ok; }

        // Rung 3: diverged. Discard the failed iterate; re-expose the previous solution.
        rt.Circuit.RestoreSolution(rt.NewtonSave);
        return SolveStatus.Diverged;
    }

    /// <summary>The Newton loop: relinearize every diode companion at its working
    /// junction voltage, refactor (tier 2) + back-substitute (tier 1), then test dual
    /// convergence (scaled voltage step AND scaled current change) and limit the next
    /// iterate. The conductance write is ε-gated (log-conductance ratio band, §9): a
    /// converged re-solve relinearizes to the SAME Geq, the gate absorbs it, no epoch
    /// bump ⇒ no refactor — the mechanism that keeps a settled nonlinear island cheap.
    /// Returns <see cref="SolveStatus.Ok"/> on convergence, <see cref="SolveStatus.Diverged"/>
    /// on hitting the cap, or a linear fault (Singular/NonFinite) verbatim.</summary>
    private SolveStatus TryNewton(IslandRuntime rt)
    {
        for (var iter = 0; iter < MaxNewtonIterations; iter++)
        {
            for (var i = 0; i < rt.DiodeCount; i++)
            {
                var slot = rt.DiodeComps[i];
                DiodeLinearize(rt.DiodeVd[i], _cDiode[slot], out var geq, out var id);
                // ε-no-op conductance gate (holds the stamped baseline, no ratchet — the
                // resistor-Adjust rationale, §9): only a super-ε move refactors.
                if (!IsConductanceNoOp(rt.DiodeGeq[i], geq))
                {
                    rt.Circuit.SetCompanionConductance(rt.DiodeStamps[i], geq);
                    rt.DiodeGeq[i] = geq;
                }
                // Companion current-source injection (RHS only — never forces a refactor):
                // arg = Geq·Vd − I(Vd) (see StampDiodeCompanion). Always written so the
                // RHS tracks the exact junction current even when Geq is ε-frozen (chord step).
                rt.Circuit.SetCompanionCurrent(rt.DiodeStamps[i], geq * rt.DiodeVd[i] - id);
            }

            var st = FactorAndSolve(rt);
            if (st != SolveStatus.Ok) return st;
            _lastTickStats.NewtonIterations++;

            var converged = true;
            for (var i = 0; i < rt.DiodeCount; i++)
            {
                var slot = rt.DiodeComps[i];
                var va = rt.DiodeLocalA[i] >= 0 ? rt.Circuit.ReadPotential(rt.DiodeLocalA[i]) : 0.0;
                var vc = rt.DiodeLocalC[i] >= 0 ? rt.Circuit.ReadPotential(rt.DiodeLocalC[i]) : 0.0;
                var vNew = va - vc;
                var vOld = rt.DiodeVd[i];
                var iNew = DiodeCurrent(vNew, _cDiode[slot]);
                var iOld = DiodeCurrent(vOld, _cDiode[slot]);
                // Dual gate: both the junction-voltage update and the junction-current
                // change must be within scaled tolerance. |Δ| ≤ reltol·max(|·|) + tol —
                // comparisons only, no transcendental in the gate (determinism rule).
                if (Math.Abs(vNew - vOld) > NewtonReltol * Math.Max(Math.Abs(vNew), Math.Abs(vOld)) + NewtonVntol)
                    converged = false;
                if (Math.Abs(iNew - iOld) > NewtonReltol * Math.Max(Math.Abs(iNew), Math.Abs(iOld)) + NewtonAbstol)
                    converged = false;
                rt.DiodeVnew[i] = vNew;   // stage the post-solve voltage; applied only if we iterate again
            }
            // On convergence, LEAVE the linearization point (rt.DiodeVd) where it was
            // stamped: it agrees with the solution to tolerance, and holding it means the
            // next re-solve relinearizes to the SAME Geq so the ε-gate skips the refactor
            // (the settled-island cheapness mechanism). Only a non-converged step advances
            // the linearization point (with junction limiting) for the next iteration.
            if (converged) return SolveStatus.Ok;
            for (var i = 0; i < rt.DiodeCount; i++)
                rt.DiodeVd[i] = DiodeLimit(rt.DiodeVnew[i], rt.DiodeVd[i], _cDiode[rt.DiodeComps[i]]);
        }
        return SolveStatus.Diverged;
    }

    /// <summary>Fallback rung 2 (solver.md): ramp every independent source from 0 to
    /// full in <see cref="SourceSteppingLevels"/> equal steps, Newton-solving at each
    /// level and carrying the junction voltages forward. The final level (α = 1)
    /// restores full source values, so on success the published solution is the true
    /// operating point. Cold diagnostic path — allocation-free but not hot.</summary>
    private SolveStatus TrySourceStepping(IslandRuntime rt)
    {
        for (var level = 1; level <= SourceSteppingLevels; level++)
        {
            var alpha = (double)level / SourceSteppingLevels;
            ScaleIndependentSources(rt, alpha);
            if (TryNewton(rt) != SolveStatus.Ok)
            {
                ScaleIndependentSources(rt, 1.0);   // leave the circuit at full source values
                return SolveStatus.Diverged;
            }
        }
        return SolveStatus.Ok;
    }

    /// <summary>Scale every independent V/I source in the island's live Circuit by
    /// <paramref name="alpha"/> for source-stepping continuation. RHS-only writes.</summary>
    private void ScaleIndependentSources(IslandRuntime rt, double alpha)
    {
        var islandSlot = rt.IslandSlot;
        for (var c = 0; c < _cCount; c++)
        {
            if (!_cAlive[c] || _nIsland[_cA[c]] != islandSlot) continue;
            var kind = (ComponentKind)_cKind[c];
            if (kind == ComponentKind.VSource && _cStampKind[c] == StampVSource)
            {
                var full = _cIsSine[c]
                    ? (rt.OperatingPoint ? 0.0 : _cSine[c].AmplitudeV * Math.Sin(_cStateVar[c]))
                    : _cValue[c];
                rt.Circuit.SetVSourceValue(new VSourceStamp(_cStamp0[c], _cStamp1[c]), alpha * full);
            }
            else if (kind == ComponentKind.ISource && _cStampKind[c] == StampISource)
            {
                rt.Circuit.SetCurrentSource(new ISourceStamp(_cStamp0[c], _cStamp1[c]), alpha * _cValue[c]);
            }
        }
    }

    /// <summary>Persist each converged junction voltage back to its component's state
    /// variable (the warm start for the next solve and the serializable diode state).</summary>
    private void PersistDiodeState(IslandRuntime rt)
    {
        for (var i = 0; i < rt.DiodeCount; i++)
            _cStateVar[rt.DiodeComps[i]] = rt.DiodeVd[i];
    }

    // Conductance-domain ε-no-op gate (log-conductance ratio band, §9): the same pinned
    // [_ratioLo, _ratioHi] the resistor Adjust uses, applied to a relinearized Geq. One
    // IEEE division + two compares; no transcendental (determinism / dual-arch goldens).
    private bool IsConductanceNoOp(double gOld, double gNew)
    {
        if (!(gOld > 0.0) || !(gNew > 0.0)) return gOld == gNew;
        var ratio = gNew / gOld;
        return ratio >= _ratioLo && ratio <= _ratioHi;
    }

    // ── Diode device model: SPICE-conventional exponential junction (isothermal 300 K) ──

    /// <summary>Junction current I(Vd) = Is·(exp(Vd/(n·Vt)) − 1), anode→cathode.</summary>
    private static double DiodeCurrent(double vd, in DiodeParams p)
        => p.SaturationCurrent * (Math.Exp(vd / (p.Emission * ThermalVoltage300)) - 1.0);

    /// <summary>Linearize the junction at <paramref name="vd"/>: the small-signal
    /// conductance Geq = dI/dV = Is·exp(Vd/(n·Vt))/(n·Vt) (clamped to the legal
    /// conductance range) and the operating-point current I(Vd).</summary>
    private static void DiodeLinearize(double vd, in DiodeParams p, out double geq, out double id)
    {
        var nvt = p.Emission * ThermalVoltage300;
        var e = Math.Exp(vd / nvt);
        id = p.SaturationCurrent * (e - 1.0);
        geq = p.SaturationCurrent * e / nvt;
        if (geq < DiodeMinConductance) geq = DiodeMinConductance;
        else if (geq > DiodeMaxConductance) geq = DiodeMaxConductance;
    }

    /// <summary>SPICE junction-voltage limiting (Nagel <c>pnjlim</c>): bounds the
    /// per-iteration forward-bias step so the exponential cannot overshoot into
    /// overflow, which is what makes the Newton loop finite (no NaN) on cold or
    /// hard-driven starts. <paramref name="vcrit"/> is the voltage of maximum
    /// curvature Vcrit = n·Vt·ln(n·Vt/(√2·Is)); a forward step past it is compressed
    /// to a logarithmic move. Reverse and small steps pass through unchanged.</summary>
    private static double DiodeLimit(double vnew, double vold, in DiodeParams p)
    {
        var nvt = p.Emission * ThermalVoltage300;
        var vcrit = nvt * Math.Log(nvt / (Math.Sqrt(2.0) * p.SaturationCurrent));
        if (vnew > vcrit && Math.Abs(vnew - vold) > 2.0 * nvt)
        {
            if (vold > 0.0)
            {
                var arg = 1.0 + (vnew - vold) / nvt;
                vnew = arg > 0.0 ? vold + nvt * Math.Log(arg) : vcrit;
            }
            else
            {
                // Normally vnew > vcrit > 0 so the log argument is positive. vcrit > 0 holds
                // only when Is < nVt/√2 (≈18 mA); DiodeParams.SaturationCurrent is accepted
                // unvalidated, so a physically absurd large Is can make vcrit ≤ 0 and vnew ≤ 0
                // here. Guard the argument (mirroring the vold>0 branch's arg>0 fallback) so
                // Log never sees a non-positive value — the "never NaN" guarantee (api.md §20)
                // must not depend on an unvalidated device parameter.
                vnew = vnew > 0.0 ? nvt * Math.Log(vnew / nvt) : vcrit;
            }
        }
        return vnew;
    }

    /// <summary>THE AC/transient hot path (api.md §21). Per substep: advance each sine
    /// phase and write its RHS, write each companion's Backward-Euler history current,
    /// factorize-if-dirty (a no-op after the first substep — companion conductances are
    /// constant while dt is constant), back-substitute, then update companion state from
    /// the fresh solution. Zero heap allocation, no per-substep refactor after warmup.</summary>
    private SolveStatus StepTransient(IslandRuntime rt, int n, double substepDt)
    {
        for (var k = 0; k < n; k++)
        {
            // ── Sources: advance phase to this substep's end (BE samples t_{n+1}). ──
            for (var i = 0; i < rt.SineCount; i++)
            {
                var c = rt.SineComps[i];
                var omega = TwoPi * _cSine[c].FreqHz;
                var phase = _cStateVar[c] + omega * substepDt;     // continuous accumulator
                while (phase >= TwoPi) phase -= TwoPi;             // wrap (value-preserving; idempotent for any increment)
                _cStateVar[c] = phase;
                rt.Circuit.SetVSourceValue(rt.SineStamps[i], _cSine[c].AmplitudeV * Math.Sin(phase));
            }

            // ── Companions: history current from prior state (tier-1 RHS). ──
            for (var i = 0; i < rt.CompCount; i++)
            {
                var c = rt.CompComps[i];
                double iEq;
                if (rt.CompKind[i] == CompCap) iEq = (_cValue[c] / substepDt) * _cStateVar[c];  // G_c·V_prev
                else iEq = -_cStateVar[c];                                                      // −I_prev
                rt.Circuit.SetCompanionCurrent(rt.CompStamps[i], iEq);
            }

            // Linear islands: one factorize (constant companion G ⇒ a no-op after the
            // first substep) + back-substitution. Nonlinear (diode) islands run the
            // full Newton loop at this substep's source/companion RHS state.
            var st = rt.HasNonlinear ? NewtonSolvePoint(rt) : FactorAndSolve(rt);
            if (st != SolveStatus.Ok) return st;
            _lastTickStats.Substeps++;

            // ── Post-solve: advance companion state (cap V = Va−Vb; inductor
            //    I_{n+1} = G_L·(Va−Vb) + I_n). _cStatePrev keeps V_n for cap readback. ──
            for (var i = 0; i < rt.CompCount; i++)
            {
                var c = rt.CompComps[i];
                var va = rt.CompLocalA[i] >= 0 ? rt.Circuit.ReadPotential(rt.CompLocalA[i]) : 0.0;
                var vb = rt.CompLocalB[i] >= 0 ? rt.Circuit.ReadPotential(rt.CompLocalB[i]) : 0.0;
                _cStatePrev[c] = _cStateVar[c];
                _cStateVar[c] = rt.CompKind[i] == CompCap
                    ? va - vb
                    : (substepDt / _cValue[c]) * (va - vb) + _cStateVar[c];
            }

            // Energy audit (0B scalar accumulation) + oscilloscope taps + limit scan,
            // all per substep. Limits MUST run per substep on a subcycled-AC island: i²t
            // is a genuine ∫I²dt over the cycle (a per-tick scan sees one phase sample ×
            // the full tick dt — phase-biased, and a fuse whose tick-boundary lands at a
            // sine zero-crossing would never heat), and the instantaneous OverCurrent/
            // OverVoltage/OverPower checks must see the waveform peak, not the tick edge.
            AccumulateIslandEnergy(rt.IslandSlot, substepDt);
            SampleTaps(rt.IslandSlot);
            EvaluateIslandLimits(rt.IslandSlot, substepDt, _evalTickIndex);
        }
        return SolveStatus.Ok;
    }

    // Scan an island once for the content-driven regime decision (§5): whether it holds
    // a sine source, whether it holds storage (cap/inductor), and the highest sine
    // frequency (drives N). Bounded by island component count; off the substep hot path.
    private void ScanIsland(int slot, out bool hasSine, out bool hasStorage, out bool hasNonlinear, out double maxFreq)
    {
        hasSine = false; hasStorage = false; hasNonlinear = false; maxFreq = 0.0;
        for (var c = 0; c < _cCount; c++)
        {
            if (!_cAlive[c] || _nIsland[_cA[c]] != slot) continue;
            if (_cIsSine[c]) { hasSine = true; if (_cSine[c].FreqHz > maxFreq) maxFreq = _cSine[c].FreqHz; }
            var kind = (ComponentKind)_cKind[c];
            if (kind == ComponentKind.Capacitor || kind == ComponentKind.Inductor) hasStorage = true;
            else if (kind == ComponentKind.Diode) hasNonlinear = true;
        }
    }

    // N from the highest source frequency at ≥ acSamplesPerCycle samples/cycle,
    // quantized with ±15% hysteresis (§5, solver.md): the raw requirement is
    // rawRate = maxFreq · samplesPerCycle · tickDt substeps/tick; N is only re-decided
    // when rawRate drifts past the band around the value N was last chosen for, so the
    // substep dt (and every companion conductance) holds constant across speed jitter.
    private int PlanSubstepN(int slot, double tickDt, double maxFreq)
    {
        var rawRate = maxFreq * _acSamplesPerCycle * tickDt;
        var current = _iSubstepN[slot];
        var reference = _iSubstepRawRef[slot];
        if (current >= 1 && reference > 0.0)
        {
            var drift = rawRate / reference;
            if (drift >= 1.0 - SubstepHysteresisBand && drift <= 1.0 + SubstepHysteresisBand)
                return current;   // within band ⇒ keep N (dt unchanged)
        }
        var n = (int)Math.Ceiling(rawRate);
        if (n < 1) n = 1;
        _iSubstepRawRef[slot] = rawRate;   // this rate becomes the new hysteresis reference
        return n;
    }

    // Tier-2 substep-dt change (N changed) without a topology rebuild: point the Circuit
    // at the new dt and restamp every companion conductance for it. The dt move is part
    // of the factorization signature, so exactly one refactor follows (§5).
    private void RestampSubstepDt(IslandRuntime rt, double newDt)
    {
        rt.Circuit.Dt = newDt;
        rt.SubstepDt = newDt;
        for (var i = 0; i < rt.CompCount; i++)
        {
            var c = rt.CompComps[i];
            var g = rt.CompKind[i] == CompCap ? _cValue[c] / newDt : newDt / _cValue[c];
            rt.Circuit.SetCompanionConductance(rt.CompStamps[i], g);
        }
    }

    private void FaultIsland(int slot, IslandRuntime rt, SolveStatus status, bool wasFaulted)
    {
        var kind = status switch
        {
            SolveStatus.NonFinite => FaultKind.NonFinite,
            SolveStatus.Diverged => FaultKind.NewtonDiverged,
            _ => FaultKind.Singular,
        };
        ComponentRef worst = default; NodeId worstNode = default; int compCount = 0, nodeCount = 0;
        _iFaultSecond[slot] = default;

        // A singular factorization is often two ideal voltage sources fighting over the
        // same node pair (parallel V-sources — the constraint rows are inconsistent or
        // redundant, both singular). Detect that specific, diagnosable case and name the
        // pair (solver.md Failure Handling; api.md §20) instead of a bare Singular row.
        if (kind == FaultKind.Singular && DetectContradictorySources(slot, out var cs1, out var cs2))
        {
            _iFault[slot] = new FaultDiagnostic(FaultKind.ContradictorySources, cs1, default, 2, 0);
            _iFaultSecond[slot] = cs2;
            _iStatus[slot] = (byte)IslandStatus.Faulted;
            if (wasFaulted) return;
            var cid = new IslandId(slot, _iGen[slot], _netId);
            _journal.Append(TopologyEventKind.IslandFaulted, default, default, cid, default);
            RecordChange(IslandChangeKind.Faulted, cid, default);
            return;
        }

        if (kind == FaultKind.NewtonDiverged)
        {
            // Attribute to the nonlinear elements that failed to converge (solver.md
            // iteration diagnostics — the participating diodes; the iteration count is
            // in TickStats.NewtonIterations). Worst = the first diode; ComponentCount =
            // how many diodes are in the divergent island.
            if (rt.DiodeCount > 0)
            {
                var cs = rt.DiodeComps[0];
                worst = new ComponentRef(ComponentKind.Diode, cs, _cGen[cs], _netId);
                compCount = rt.DiodeCount;
            }
        }
        else
        {
            var row = rt.Circuit.Fault.Row;
            if (row >= 0 && row < rt.Circuit.NodeRowCount)
            {
                var local = rt.Circuit.NodeForRow(row);
                if (local >= 0 && local < rt.NodeCount)
                {
                    var ns = rt.NodeSlots[local];
                    worstNode = new NodeId(ns, _nGen[ns], _netId); nodeCount = 1;
                }
            }
            else if (row >= 0)
            {
                for (var k = 0; k < rt.AuxCount; k++)
                    if (rt.AuxRows[k] == row)
                    {
                        var cs = rt.AuxComps[k];
                        worst = new ComponentRef((ComponentKind)_cKind[cs], cs, _cGen[cs], _netId); compCount = 1;
                        break;
                    }
            }
        }

        _iFault[slot] = new FaultDiagnostic(kind, worst, worstNode, compCount, nodeCount);
        _iStatus[slot] = (byte)IslandStatus.Faulted;
        // Terminal event ONLY on the transition into Faulted — mirrors the
        // wasFaulted gate on the IslandRecovered edge. A standing fault re-solved
        // each tick (still-singular signature) must not re-emit IslandFaulted /
        // RecordChange(Faulted): duplicates corrupt journal replay and, under a
        // driver that re-dirties the island every tick, overflow the change ring
        // into a spurious full re-pin (api.md §15 completeness / §20).
        if (wasFaulted) return;
        var id = new IslandId(slot, _iGen[slot], _netId);
        _journal.Append(TopologyEventKind.IslandFaulted, default, default, id, default);
        RecordChange(IslandChangeKind.Faulted, id, default);
    }

    // Scan the island for two ideal voltage sources across the SAME (unordered) node
    // pair — the canonical over-determined singularity (parallel V-sources). Cold
    // fault path; O(V²) in the island's source count. Names the first two found.
    private bool DetectContradictorySources(int islandSlot, out ComponentRef c1, out ComponentRef c2)
    {
        c1 = default; c2 = default;
        for (var i = 0; i < _cCount; i++)
        {
            if (!_cAlive[i] || (ComponentKind)_cKind[i] != ComponentKind.VSource || _nIsland[_cA[i]] != islandSlot) continue;
            for (var j = i + 1; j < _cCount; j++)
            {
                if (!_cAlive[j] || (ComponentKind)_cKind[j] != ComponentKind.VSource || _nIsland[_cA[j]] != islandSlot) continue;
                var same = (_cA[i] == _cA[j] && _cB[i] == _cB[j]) || (_cA[i] == _cB[j] && _cB[i] == _cA[j]);
                if (!same) continue;
                c1 = new ComponentRef(ComponentKind.VSource, i, _cGen[i], _netId);
                c2 = new ComponentRef(ComponentKind.VSource, j, _cGen[j], _netId);
                return true;
            }
        }
        return false;
    }

    // =============================================================== build

    // Scratch for the per-build galvanic-datum selection (shape-time only).
    private readonly Dictionary<int, int> _buildGalvParent = new();
    private readonly HashSet<int> _buildDatums = new();

    // Partition the island's members into GALVANIC sub-components — connectivity over
    // every stamped element EXCEPT the ideal transformer's cross-port link (its two
    // windings are branches, but primary and secondary share no conductor) — and mark
    // the first Reference-role node (ascending node slot) of each sub-component as
    // that sub-component's datum. Result lands in _buildDatums.
    private void GalvanicDatums(int islandSlot)
    {
        _buildGalvParent.Clear();
        _buildDatums.Clear();
        foreach (var n in _buildNodes) _buildGalvParent[n] = n;

        int Find(int x)
        {
            while (_buildGalvParent[x] != x)
            {
                _buildGalvParent[x] = _buildGalvParent[_buildGalvParent[x]];
                x = _buildGalvParent[x];
            }
            return x;
        }
        void Uni(int a, int b)
        {
            if (!_buildGalvParent.ContainsKey(a) || !_buildGalvParent.ContainsKey(b)) return;
            int ra = Find(a), rb = Find(b);
            if (ra != rb) _buildGalvParent[rb] = ra;
        }

        for (var c = 0; c < _cCount; c++)
        {
            if (!_cAlive[c] || _nIsland[_cA[c]] != islandSlot) continue;
            Uni(_cA[c], _cB[c]);                    // every 2-terminal element is a branch
            if ((ComponentKind)_cKind[c] == ComponentKind.IdealTransformer)
            {
                if (_cC[c] >= 0 && _cD[c] >= 0) Uni(_cC[c], _cD[c]);   // secondary winding
                // NO union across ports: galvanic isolation is the device's point.
            }
            else
            {
                if (_cC[c] >= 0) Uni(_cA[c], _cC[c]);
                if (_cD[c] >= 0) Uni(_cA[c], _cD[c]);
            }
        }
        for (var kc = 0; kc < _kCount; kc++)
        {
            if (!_kAlive[kc] || !_kSpec[kc].IsGalvanic || _kStateA[kc] != CouplerState.Closed) continue;
            if (_nIsland[_kAPos[kc]] != islandSlot) continue;
            Uni(_kAPos[kc], _kBPos[kc]);
            Uni(_kANeg[kc], _kBNeg[kc]);
        }

        // First reference per root, ascending node slot (deterministic: _buildNodes is
        // an ascending slot scan).
        foreach (var n in _buildNodes)
        {
            if (_nRole[n] != (byte)NodeRole.Reference) continue;
            var root = Find(n);
            var taken = false;
            foreach (var d in _buildDatums)
                if (Find(d) == root) { taken = true; break; }
            if (!taken) _buildDatums.Add(n);
        }
    }

    /// <summary>Construct (or reconstruct) an island's Circuit from the current
    /// document: assign local node indices, pick the reference datum, stamp every
    /// member primitive and closed-galvanic-coupler branch, apply the wiring
    /// policy (§5), and Analyze. Shape-time — allocation is expected here.</summary>
    private void BuildRuntime(int islandSlot, bool companions, double substepDt)
    {
        _buildNodes.Clear();
        for (var n = 0; n < _nCount; n++)
            if (_nAlive[n] && _nIsland[n] == islandSlot) _buildNodes.Add(n);
        var k = _buildNodes.Count;

        // Local-index assignment with ONE PINNED DATUM PER GALVANIC SUB-COMPONENT
        // (2026-07-06 hardening; TransformerHotInsertTests). An island can hold
        // galvanically-disjoint pieces tied only by an ideal transformer's aux-row
        // constraints (AddIdealTransformer between two Ready islands merges them,
        // §16, and brings BOTH sides' Reference rails along). Those constraints are
        // purely DIFFERENTIAL, so a side whose rail is not pinned floats at an
        // arbitrary common-mode offset (the secondary read V/2, its "ground" −V/2).
        // Fix: the FIRST Reference node of EACH galvanic sub-component aliases onto
        // the single eliminated datum row — a tie across sub-components that carries
        // no current (no galvanic return path exists between them, by construction).
        // Within one sub-component additional references keep the old behavior
        // (first wins; the rest are ordinary nodes): two partitions merged by a
        // closed breaker keep their honest two-wire return drop across the neg
        // bridge — never shorted by rail fiat. Single-datum islands are unchanged.
        GalvanicDatums(islandSlot);

        var refLocal = -1;
        var dim = 0;
        for (var i = 0; i < k; i++)
        {
            var ns = _buildNodes[i];
            _nRtSlot[ns] = islandSlot;
            if (_buildDatums.Contains(ns))
            {
                if (refLocal < 0) refLocal = dim++;
                _nCircuitNode[ns] = refLocal;   // this sub-component's datum: pinned at 0
            }
            else _nCircuitNode[ns] = dim++;
        }

        var circuit = new Circuit(new SparseLuBackend(), dim, refLocal);
        circuit.Dt = companions ? substepDt : _profileDt;
        // Reverse map local index → node slot (fault/readback). The shared reference
        // index maps to the FIRST reference node in slot order (deterministic; the
        // eliminated datum row reads 0 V for every aliased reference regardless).
        var nodeSlots = new int[dim];
        for (var i = 0; i < dim; i++) nodeSlots[i] = -1;
        for (var i = 0; i < k; i++)
        {
            var li = _nCircuitNode[_buildNodes[i]];
            if (nodeSlots[li] < 0) nodeSlots[li] = _buildNodes[i];
        }

        var auxRows = new List<int>();
        var auxComps = new List<int>();
        _buildCompanions = companions; _buildSubstepDt = substepDt;
        _buildCompComps.Clear(); _buildCompKind.Clear(); _buildCompStamps.Clear();
        _buildCompLocalA.Clear(); _buildCompLocalB.Clear();
        _buildSineComps.Clear(); _buildSineStamps.Clear();
        _buildDiodeComps.Clear(); _buildDiodeStamps.Clear();
        _buildDiodeLocalA.Clear(); _buildDiodeLocalC.Clear();
        _buildMemberComps.Clear();

        for (var c = 0; c < _cCount; c++)
        {
            if (!_cAlive[c] || _nIsland[_cA[c]] != islandSlot) continue;
            _buildMemberComps.Add(c);
            StampComponent(circuit, c, auxRows, auxComps);
        }

        // Closed galvanic couplers stamp a 1 mΩ series branch per port pair inside
        // the merged matrix (api.md §7) — no client handle.
        for (var kc = 0; kc < _kCount; kc++)
        {
            if (!_kAlive[kc] || !_kSpec[kc].IsGalvanic || _kStateA[kc] != CouplerState.Closed) continue;
            if (_nIsland[_kAPos[kc]] != islandSlot) continue;
            circuit.AddResistor(_nCircuitNode[_kAPos[kc]], _nCircuitNode[_kBPos[kc]], CouplerBridgeOhms);
            circuit.AddResistor(_nCircuitNode[_kANeg[kc]], _nCircuitNode[_kBNeg[kc]], CouplerBridgeOhms);
        }

        // Boundary couplers (DecouplingTransformer / ConverterTwoPort) inject their
        // per-side elements — a current source on A, a voltage source (+ DC-link cap)
        // on B — and record the stamps for the per-substep exchange (Netlist.Couplers.cs).
        StampBoundaryCouplers(circuit, islandSlot, companions, substepDt);

        StampWiring(circuit, islandSlot, dim, refLocal);

        circuit.Analyze();

        var rt = _iRuntime[islandSlot];
        rt ??= new IslandRuntime();
        rt.Circuit = circuit; rt.NodeSlots = nodeSlots; rt.NodeCount = dim;
        rt.AuxRows = auxRows.ToArray(); rt.AuxComps = auxComps.ToArray(); rt.AuxCount = auxRows.Count;
        rt.HasCompanions = companions; rt.SubstepDt = substepDt;
        rt.CompComps = _buildCompComps.ToArray(); rt.CompKind = _buildCompKind.ToArray();
        rt.CompStamps = _buildCompStamps.ToArray();
        rt.CompLocalA = _buildCompLocalA.ToArray(); rt.CompLocalB = _buildCompLocalB.ToArray();
        rt.CompCount = _buildCompComps.Count;
        rt.SineComps = _buildSineComps.ToArray(); rt.SineStamps = _buildSineStamps.ToArray();
        rt.SineCount = _buildSineComps.Count;

        // ── Nonlinear (diode) runtime ──
        rt.DiodeComps = _buildDiodeComps.ToArray(); rt.DiodeStamps = _buildDiodeStamps.ToArray();
        rt.DiodeLocalA = _buildDiodeLocalA.ToArray(); rt.DiodeLocalC = _buildDiodeLocalC.ToArray();
        rt.DiodeCount = _buildDiodeComps.Count;
        rt.HasNonlinear = rt.DiodeCount > 0;
        rt.DiodeVd = new double[rt.DiodeCount];
        rt.DiodeVnew = new double[rt.DiodeCount];
        rt.DiodeGeq = new double[rt.DiodeCount];
        rt.DiodeIeq = new double[rt.DiodeCount];
        for (var i = 0; i < rt.DiodeCount; i++)
        {
            var cs = rt.DiodeComps[i];
            var vd = _cStateVar[cs];                       // warm start (persisted junction voltage; 0 cold)
            rt.DiodeVd[i] = vd;
            DiodeLinearize(vd, _cDiode[cs], out var g0, out var id0);
            rt.DiodeGeq[i] = g0;                           // baseline matching the build-time companion stamp
            rt.DiodeIeq[i] = g0 * vd - id0;
        }
        rt.NewtonSave = rt.HasNonlinear ? new double[circuit.Dimension] : Array.Empty<double>();
        rt.MemberComps = _buildMemberComps.ToArray(); rt.MemberCount = _buildMemberComps.Count;
        rt.IslandSlot = islandSlot;

        _iRuntime[islandSlot] = rt;
        _iRuntimeStale[islandSlot] = false;
        ResetIslandEnergy(islandSlot);   // restart the audit window on a fresh Circuit
    }

    private void StampComponent(Circuit circuit, int slot, List<int> auxRows, List<int> auxComps)
    {
        var kind = (ComponentKind)_cKind[slot];
        var a = _nCircuitNode[_cA[slot]];
        var b = _cB[slot] >= 0 ? _nCircuitNode[_cB[slot]] : -1;
        switch (kind)
        {
            case ComponentKind.Resistor:
            {
                var s = circuit.AddResistor(a, b, _cValue[slot]);
                SetConductanceStamp(slot, s);
                break;
            }
            case ComponentKind.Switch:
            {
                var s = circuit.AddSwitch(a, b, _cValue[slot] != 0.0);
                SetConductanceStamp(slot, s);
                break;
            }
            case ComponentKind.Inductor:
            {
                if (_buildCompanions)
                {
                    // Backward-Euler companion (transient): G = dt/L, history source
                    // iEq = −I_prev (conductance-form mirror of the capacitor; solver.md).
                    var g = _buildSubstepDt / _cValue[slot];
                    StampCompanion(circuit, slot, a, b, CompInd, g, -_cStateVar[slot]);
                }
                else
                {
                    // DC operating point: an inductor is a ~1 mΩ short (solver.md).
                    var s = circuit.AddResistor(a, b, InductorDcShortOhms);
                    SetConductanceStamp(slot, s);
                }
                break;
            }
            case ComponentKind.Capacitor:
            {
                if (_buildCompanions)
                {
                    // Backward-Euler companion (transient): G = C/dt, history source
                    // iEq = G·V_prev (Norton form; solver.md).
                    var g = _cValue[slot] / _buildSubstepDt;
                    StampCompanion(circuit, slot, a, b, CompCap, g, g * _cStateVar[slot]);
                }
                else
                {
                    // DC operating point: a capacitor is open — no stamp (gmin keeps the
                    // node nonsingular).
                    _cStampKind[slot] = StampNone;
                }
                break;
            }
            case ComponentKind.VSource:
            {
                // A plain source stamps its value; a sine source stamps its DC offset
                // (0) and is driven per substep in transient mode (§5 lesson-start
                // convention: sine sources sit at 0 at the operating point).
                var volts = _cIsSine[slot] ? 0.0 : _cValue[slot];
                var s = circuit.AddVoltageSource(a, b, volts);
                _cStampKind[slot] = StampVSource; _cStamp0[slot] = s.AuxRow; _cStamp1[slot] = s.RhsSlot;
                auxRows.Add(s.AuxRow); auxComps.Add(slot);
                if (_cIsSine[slot]) { _buildSineComps.Add(slot); _buildSineStamps.Add(s); }
                break;
            }
            case ComponentKind.ISource:
            {
                var s = circuit.AddCurrentSource(a, b, _cValue[slot]);
                _cStampKind[slot] = StampISource; _cStamp0[slot] = s.RhsFrom; _cStamp1[slot] = s.RhsTo;
                break;
            }
            case ComponentKind.IdealTransformer:
            {
                var cc = _nCircuitNode[_cC[slot]];
                var dd = _nCircuitNode[_cD[slot]];
                var s = circuit.AddIdealTransformer(a, b, cc, dd, _cValue[slot]);
                _cStampKind[slot] = StampTransformer;
                _cStamp0[slot] = s.PrimaryAuxRow; _cStamp1[slot] = s.SecondaryAuxRow;
                _cStamp2[slot] = s.SlotPbPos; _cStamp3[slot] = s.SlotPbNeg; _cStamp4[slot] = s.SlotSp;
                auxRows.Add(s.PrimaryAuxRow); auxComps.Add(slot);
                auxRows.Add(s.SecondaryAuxRow); auxComps.Add(slot);
                break;
            }
            case ComponentKind.Diode:
                // Nonlinear: a Norton companion (Geq ‖ Ieq) whose values the Newton
                // loop re-stamps per iteration. Stamped in BOTH DC and transient modes
                // (a diode is nonlinear regardless of dt), independent of _buildCompanions.
                StampDiodeCompanion(circuit, slot, a, b);
                break;
        }
    }

    private void SetConductanceStamp(int slot, in ConductanceStamp s)
    {
        _cStampKind[slot] = StampConductance;
        _cStamp0[slot] = s.Aa; _cStamp1[slot] = s.Bb; _cStamp2[slot] = s.Ab; _cStamp3[slot] = s.Ba;
    }

    // Reserve and set a Backward-Euler companion (Norton conductance ‖ history source)
    // for a capacitor/inductor, record its stamp in the per-component descriptor (so a
    // later Adjust can find it) and in the build's companion lists (the substep loop's
    // contiguous SoA). Local a/b indices are captured for post-solve state readback.
    private void StampCompanion(Circuit circuit, int slot, int a, int b, byte kind, double g, double iEq)
    {
        var s = circuit.AddCompanion(a, b);
        circuit.SetCompanion(s, g, iEq);
        _cStampKind[slot] = kind == CompCap ? StampCapacitor : StampInductor;
        _cStamp0[slot] = s.G.Aa; _cStamp1[slot] = s.G.Bb; _cStamp2[slot] = s.G.Ab; _cStamp3[slot] = s.G.Ba;
        _cStamp4[slot] = s.RhsA; _cStamp5[slot] = s.RhsB;
        _buildCompComps.Add(slot); _buildCompKind.Add(kind); _buildCompStamps.Add(s);
        _buildCompLocalA.Add(a); _buildCompLocalB.Add(b);
    }

    /// <summary>Reserve and set a diode's linearized Newton companion (Norton form:
    /// conductance Geq ‖ history current source Ieq), record its stamp for the Newton
    /// loop, and capture the local anode/cathode indices for junction-voltage readback.
    /// Seeded from the persisted junction voltage (warm start; 0 cold). The companion
    /// current-source injection is Geq·Vd − I(Vd): the branch current a→c is
    /// Geq·(Va−Vc) + Ieq with Ieq = I(Vd) − Geq·Vd, and <see cref="Circuit.SetCompanion"/>
    /// injects +arg at a / −arg at c, so arg = −Ieq = Geq·Vd − I(Vd) (verified by the
    /// diode oracle: at convergence the branch current equals I(Vd)).</summary>
    private void StampDiodeCompanion(Circuit circuit, int slot, int a, int c)
    {
        var s = circuit.AddCompanion(a, c);
        var vd = _cStateVar[slot];
        DiodeLinearize(vd, _cDiode[slot], out var geq, out var id);
        circuit.SetCompanion(s, geq, geq * vd - id);
        _cStampKind[slot] = StampDiode;
        _cStamp0[slot] = s.G.Aa; _cStamp1[slot] = s.G.Bb; _cStamp2[slot] = s.G.Ab; _cStamp3[slot] = s.G.Ba;
        _cStamp4[slot] = s.RhsA; _cStamp5[slot] = s.RhsB;
        _buildDiodeComps.Add(slot); _buildDiodeStamps.Add(s);
        _buildDiodeLocalA.Add(a); _buildDiodeLocalC.Add(c);
    }

    /// <summary>The construction-time wiring policy (api.md §5): a leak
    /// (TwoWireLeak) or return conductance (ReferenceBound) from every Return-role
    /// node and every source-negative node to the island datum, exactly one per
    /// node. ExplicitOnly stamps nothing (gmin keeps floating lessons finite).</summary>
    private void StampWiring(Circuit circuit, int islandSlot, int k, int refLocal)
    {
        if (_opts.Wiring.Kind == WiringPolicy.Mode.ExplicitOnly || k == 0) return;
        var datum = refLocal >= 0 ? refLocal : 0;
        var wireOhms = _opts.Wiring.Kind == WiringPolicy.Mode.TwoWireLeak
            ? _opts.Wiring.Parameter          // leak resistance (Ω)
            : 1.0 / _opts.Wiring.Parameter;   // return conductance (S) → resistance

        Span<bool> wire = k <= 256 ? stackalloc bool[k] : new bool[k];
        wire.Clear();
        for (var n = 0; n < _nCount; n++)
            if (_nAlive[n] && _nIsland[n] == islandSlot && _nRole[n] == (byte)NodeRole.Return)
                wire[_nCircuitNode[n]] = true;
        for (var c = 0; c < _cCount; c++)
            if (_cAlive[c] && (ComponentKind)_cKind[c] == ComponentKind.VSource && _nIsland[_cA[c]] == islandSlot)
                wire[_nCircuitNode[_cB[c]]] = true;

        for (var i = 0; i < k; i++)
            if (wire[i] && i != datum)
                circuit.AddResistor(i, datum, wireOhms);
    }

    // =============================================================== step (§11)

    internal void StepIslandNumeric(int slot, uint gen, in TickClock clock)
    {
        _ = gen;
        if ((uint)slot >= (uint)_iSlotCount || !_iAlive[slot]) return;

        if (_tickStatsSealed) { _lastTickStats = default; _tickStatsSealed = false; }
        _evalTickIndex = clock.TickIndex;

        Debug.Assert(!_iStepping[slot], "Single-writer-per-island violated: concurrent Step on the same island (api.md §11, §21).");
        _iStepping[slot] = true;
        _stepInFlight++;
        try
        {
            if (_iNeedsRebuild[slot]) { RebuildIsland(slot); _lastTickStats.IslandRebuilds++; }
            if (!_iAlive[slot]) return;   // a split may have retired this slot

            EnsureUnitsFresh();
            if (RouteAsUnit(slot))
            {
                // Step on a non-lead member of a coupled unit is a scheduling error:
                // the client must Step the unit's lead (api.md §11). Debug-assert; in
                // release, no-op (the lead's Step covers the whole unit in lockstep).
                Debug.Assert(IsUnitLead(slot),
                    "Step on a non-lead member of a boundary-coupled scheduling unit (api.md §11) — Step the unit lead.");
                if (!IsUnitLead(slot)) return;
                if (UnitNeedsSolve(slot)) StepUnit(slot, clock.Dt, operatingPoint: false);
                // Limits for every unit member this Step touched (post-solve) — unless the
                // member already self-scanned per substep this tick (transient members do).
                for (var m = 0; m < _iSlotCount; m++)
                    if (_iAlive[m] && _iUnitLead[m] == slot && _iStatus[m] == (byte)IslandStatus.Ready
                        && !LimitsEvaluatedThisTick(m, clock.TickIndex))
                        EvaluateIslandLimits(m, clock.Dt, clock.TickIndex);
                return;
            }

            // Solo island. A transient island (sine/storage) advances time every tick,
            // so it steps even when the document is untouched (Ready) — subcycled AC.
            if (_iStatus[slot] == (byte)IslandStatus.Dirty || _iRuntimeStale[slot] || IslandIsTransient(slot))
                NumericSolveIsland(slot, clock.Dt, operatingPoint: false);
            if (_iAlive[slot] && _iStatus[slot] == (byte)IslandStatus.Ready
                && !LimitsEvaluatedThisTick(slot, clock.TickIndex))
                EvaluateIslandLimits(slot, clock.Dt, clock.TickIndex);
        }
        finally
        {
            _stepInFlight--;
            _iStepping[slot] = false;
        }
    }

    // =========================================================== readback (§10)

    private double NodePotential(int nodeSlot)
    {
        var isl = _nIsland[nodeSlot];
        if (isl < 0) return 0.0;
        var rt = _iRuntime[isl];
        if (rt is null || _nRtSlot[nodeSlot] != isl || _nCircuitNode[nodeSlot] >= rt.NodeCount) return 0.0;
        return rt.Circuit.ReadPotential(_nCircuitNode[nodeSlot]);
    }

    private double BranchCurrent(int compSlot)
    {
        var kind = (ComponentKind)_cKind[compSlot];
        var a = _cA[compSlot]; var b = _cB[compSlot];
        switch (kind)
        {
            case ComponentKind.Resistor:
                return (NodePotential(a) - NodePotential(b)) / _cValue[compSlot];
            case ComponentKind.Switch:
                return (NodePotential(a) - NodePotential(b)) / (_cValue[compSlot] != 0.0 ? ClosedSwitchOhms : OpenSwitchOhms);
            case ComponentKind.Inductor:
                // Transient: the state variable IS the inductor current (a→b). DC: fixed short.
                return _cStampKind[compSlot] == StampInductor
                    ? _cStateVar[compSlot]
                    : (NodePotential(a) - NodePotential(b)) / InductorDcShortOhms;
            case ComponentKind.Capacitor:
                // Transient: i = C·dV/dt = C/dt·(V − V_prev). DC: open (0). Needs the
                // runtime's substep dt, held on the island's transient runtime.
                if (_cStampKind[compSlot] == StampCapacitor)
                {
                    var islc = _nIsland[a];
                    var rtc = islc >= 0 ? _iRuntime[islc] : null;
                    if (rtc is null || !rtc.HasCompanions) return 0.0;
                    return _cValue[compSlot] / rtc.SubstepDt * (_cStateVar[compSlot] - _cStatePrev[compSlot]);
                }
                return 0.0;
            case ComponentKind.ISource:
                return _cValue[compSlot];   // driven from→to
            case ComponentKind.VSource:
            {
                var isl = _nIsland[a];
                var rt = isl >= 0 ? _iRuntime[isl] : null;
                if (rt is null || _cStampKind[compSlot] != StampVSource) return 0.0;
                return rt.Circuit.ReadFlow(_cStamp0[compSlot]);
            }
            case ComponentKind.Diode:
                // The junction current at the converged operating point, anode→cathode.
                // _cStateVar holds the persisted junction voltage (0 before any solve).
                return _cStampKind[compSlot] == StampDiode
                    ? DiodeCurrent(_cStateVar[compSlot], _cDiode[compSlot])
                    : 0.0;
            default:
                return 0.0;   // transformer readback: not yet
        }
    }

    /// <summary>Test seam (internal): the current phase-accumulator and instantaneous
    /// value of a sine source, for continuity assertions. Not a public surface.</summary>
    internal bool TryReadSineState(VSourceId id, out double phase, out double value)
    {
        if (ResolveComp(id.AsRef(), out var slot) && _cIsSine[slot])
        {
            phase = _cStateVar[slot];
            value = _cSine[slot].AmplitudeV * Math.Sin(phase);
            return true;
        }
        phase = 0.0; value = 0.0; return false;
    }

    /// <summary>Test seam (internal): a capacitor/inductor's integrated state variable
    /// (cap voltage a→b, or inductor current a→b).</summary>
    internal bool TryReadStorageState(in ComponentRef c, out double state)
    {
        if (ResolveComp(c, out var slot)) { state = _cStateVar[slot]; return true; }
        state = 0.0; return false;
    }

    internal double ReadVoltageNumeric(NodeId n)
        => ResolveNode(n, out var slot) ? NodePotential(slot) : 0.0;

    internal double ReadCurrentNumeric(in ComponentRef c)
        => ResolveComp(c, out var slot) ? BranchCurrent(slot) : 0.0;

    internal double ReadPowerNumeric(in ComponentRef c)
    {
        if (!ResolveComp(c, out var slot)) return 0.0;
        var v = NodePotential(_cA[slot]) - (_cB[slot] >= 0 ? NodePotential(_cB[slot]) : 0.0);
        return v * BranchCurrent(slot);   // power absorbed at the a→b convention
    }

    internal ReadOnlySpan<double> RawVectorNumeric(IslandId i)
    {
        if ((uint)i.Slot >= (uint)_iSlotCount || !_iAlive[i.Slot] || _iGen[i.Slot] != i.Gen) return ReadOnlySpan<double>.Empty;
        var rt = _iRuntime[i.Slot];
        return rt is null ? ReadOnlySpan<double>.Empty : rt.Circuit.PublishedVector;
    }

    internal InvariantReport CheckInvariantsNumeric(int slot, uint gen, InvariantChecks which)
    {
        _ = gen;
        if ((uint)slot >= (uint)_iSlotCount || !_iAlive[slot]) return new InvariantReport(0.0, default, true, -1, 0.0);
        var rt = _iRuntime[slot];
        if (rt is null || _iStatus[slot] != (byte)IslandStatus.Ready)
            return new InvariantReport(0.0, default, true, -1, 0.0);

        double kcl = 0.0; NodeId worstNode = default;
        if ((which & InvariantChecks.Kcl) != 0)
        {
            kcl = rt.Circuit.MaxNodeKclResidual(out var worstRow);
            var local = rt.Circuit.NodeForRow(worstRow);
            if (local >= 0 && local < rt.NodeCount)
            {
                var ns = rt.NodeSlots[local];
                worstNode = new NodeId(ns, _nGen[ns], _netId);
            }
        }

        var allFinite = true; var firstBad = -1;
        if ((which & InvariantChecks.Finiteness) != 0)
        {
            var v = rt.Circuit.PublishedVector;
            for (var r = 0; r < v.Length; r++)
                if (double.IsNaN(v[r]) || double.IsInfinity(v[r])) { allFinite = false; firstBad = r; break; }
        }

        var energyResidual = (which & InvariantChecks.Energy) != 0 ? EnergyResidualOf(slot) : 0.0;
        return new InvariantReport(kcl, worstNode, allFinite, firstBad, energyResidual);
    }

    /// <summary>The island's current AC subcycle plan (api.md §11): N substeps, the
    /// substep dt they run at, and the hysteresis band (nonzero only for AC islands —
    /// those in a Mixed netlist holding a sine source). Reflects the last solve; before
    /// any solve, reports a single step at the profile dt.</summary>
    internal SubstepPlan IslandPlanNumeric(int slot, uint gen)
    {
        _ = gen;
        if ((uint)slot >= (uint)_iSlotCount || !_iAlive[slot]) return new SubstepPlan(1, _profileDt, 0.0);
        var n = _iSubstepN[slot] >= 1 ? _iSubstepN[slot] : 1;
        var dt = _iSubstepDt[slot] > 0.0 ? _iSubstepDt[slot] : _profileDt;
        var band = _profileKind == SolverProfile.Regime.Mixed && IslandHasSine(slot) ? SubstepHysteresisBand : 0.0;
        return new SubstepPlan(n, dt, band);
    }

    internal FaultDiagnostic IslandFaultNumeric(int slot, uint gen)
    {
        _ = gen;
        return (uint)slot < (uint)_iSlotCount && _iAlive[slot] ? _iFault[slot] : default;
    }

    internal int DescribeFaultNumeric(int slot, uint gen, Span<ComponentRef> comps, Span<NodeId> nodes)
    {
        _ = gen;
        if ((uint)slot >= (uint)_iSlotCount || !_iAlive[slot]) return 0;
        var f = _iFault[slot];
        var packed = 0;
        if (f.ComponentCount > 0 && comps.Length > 0) { comps[0] = f.Worst; packed++; }
        // A ContradictorySources fault names BOTH participating sources (§20).
        if (f.Kind == FaultKind.ContradictorySources && f.ComponentCount > 1 && comps.Length > 1)
        { comps[1] = _iFaultSecond[slot]; packed++; }
        if (f.NodeCount > 0 && nodes.Length > 0) { nodes[0] = f.WorstNode; }
        return packed;
    }

    // ============================================================ arena growth

    private void EnsureRuntimeIslandCap(int min)
    {
        if (_iRuntime.Length >= min) return;
        var cap = Math.Max(min, _iRuntime.Length * 2);
        Array.Resize(ref _iRuntime, cap); Array.Resize(ref _iRuntimeStale, cap);
        Array.Resize(ref _iFault, cap); Array.Resize(ref _iFaultSecond, cap); Array.Resize(ref _iStepping, cap);
        Array.Resize(ref _iSubstepN, cap); Array.Resize(ref _iSubstepDt, cap); Array.Resize(ref _iSubstepRawRef, cap);
        EnsureEnergyCap(cap);
    }

    private void EnsureStampCap(int min)
    {
        if (_cStampKind.Length >= min) return;
        var cap = Math.Max(min, _cStampKind.Length * 2);
        Array.Resize(ref _cStampKind, cap);
        Array.Resize(ref _cStamp0, cap); Array.Resize(ref _cStamp1, cap);
        Array.Resize(ref _cStamp2, cap); Array.Resize(ref _cStamp3, cap);
        Array.Resize(ref _cStamp4, cap); Array.Resize(ref _cStamp5, cap);
    }

    private void EnsureNodeMapCap(int min)
    {
        if (_nCircuitNode.Length >= min) return;
        var cap = Math.Max(min, _nCircuitNode.Length * 2);
        Array.Resize(ref _nCircuitNode, cap); Array.Resize(ref _nRtSlot, cap);
    }
}
