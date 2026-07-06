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
    private const byte StampNone = 0;         // capacitor (DC open), diode (phase 4)
    private const byte StampConductance = 1;  // resistor / switch / inductor (DC short)
    private const byte StampVSource = 2;
    private const byte StampISource = 3;
    private const byte StampTransformer = 4;

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
    private bool[] _iStepping = new bool[4];        // debug single-writer-per-island tripwire

    // ── Per-component stamp descriptor (parallel to the component SoA) ──
    private byte[] _cStampKind = new byte[8];
    private int[] _cStamp0 = new int[8], _cStamp1 = new int[8], _cStamp2 = new int[8], _cStamp3 = new int[8], _cStamp4 = new int[8];

    // ── Per-node numeric mapping ──
    private int[] _nCircuitNode = new int[4];   // node slot → local Circuit node index
    private int[] _nRtSlot = new int[4];         // island slot the mapping above is valid for (−1 = none)

    // TickStats sealing: a tick's counters accumulate from its first mutation
    // (drive-phase Adjust no-ops included) through Solve; the tick is "sealed" at
    // Solve end and at the LastTickStats readback barrier (§9). The next mutation
    // or Step after a sealed tick starts a fresh tick.
    private bool _tickStatsSealed;
    private int _stepInFlight;                    // §9 phase assert: no LastTickStats read mid-Step

    // Build scratch (shape-time only).
    private readonly List<int> _buildNodes = new();
    private readonly List<int> _numericScratch = new();

    private sealed class IslandRuntime
    {
        public Circuit Circuit = null!;
        public int[] NodeSlots = Array.Empty<int>();  // local index → node slot (fault/readback reverse)
        public int NodeCount;
        public int[] AuxRows = Array.Empty<int>();     // aux row → owning component slot (fault attribution)
        public int[] AuxComps = Array.Empty<int>();
        public int AuxCount;
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

    // ============================================================= solve driver

    /// <summary>The solve pipeline shared by <see cref="SolveOperatingPoint"/> and
    /// <see cref="Solve(in TickClock)"/>: run the structural rebuild pass (§16/§17
    /// — one rebuild per island per tick, rebuild supersedes merge), then the
    /// numeric pass (build/refactor/solve/publish) over dirty units in the
    /// rebuild-stable <c>Islands.Ids</c> order (§9).</summary>
    private void RunSolve(bool forceAll, double dt)
    {
        BeginTick();

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
        var ids = IslandIdsOrdered();
        // Copy slots out first: NumericSolveIsland can allocate but never mutates
        // island membership, so the Ids span stays valid across the loop.
        _numericScratch.Clear();
        for (var i = 0; i < ids.Length; i++) _numericScratch.Add(ids[i].Slot);
        foreach (var slot in _numericScratch)
        {
            if (!_iAlive[slot]) continue;
            if (forceAll || _iStatus[slot] == (byte)IslandStatus.Dirty)
                NumericSolveIsland(slot, dt);
        }

        _tickStatsSealed = true;
    }

    /// <summary>Ensure the island's Circuit is current, then factor (tier 2 iff
    /// values moved) and back-substitute (tier 1), publishing on success. A
    /// singular factorization or a non-finite solve drives the island to
    /// <see cref="IslandStatus.Faulted"/> (api.md §20) and holds last-good.</summary>
    private void NumericSolveIsland(int slot, double dt)
    {
        _ = dt;   // DC islands: dt is not part of the (resistive) signature (§ solver.md)
        if (_iRuntime[slot] is null || _iRuntimeStale[slot]) BuildRuntime(slot);
        var rt = _iRuntime[slot]!;
        var wasFaulted = _iStatus[slot] == (byte)IslandStatus.Faulted;

        var fs = rt.Circuit.FactorizeIfDirty();
        if (rt.Circuit.DidFactor) _lastTickStats.Refactorizations++;
        if (fs != SolveStatus.Ok) { FaultIsland(slot, rt, fs, wasFaulted); return; }

        var ss = rt.Circuit.Solve();
        if (rt.Circuit.DidSolve) _lastTickStats.RhsSolves++;
        if (ss != SolveStatus.Ok) { FaultIsland(slot, rt, ss, wasFaulted); return; }

        _iStatus[slot] = (byte)IslandStatus.Ready;
        if (wasFaulted)
        {
            _iFault[slot] = default;
            var id = new IslandId(slot, _iGen[slot], _netId);
            _journal.Append(TopologyEventKind.IslandRecovered, default, default, id, default);
            RecordChange(IslandChangeKind.Recovered, id, default);
        }
    }

    private void FaultIsland(int slot, IslandRuntime rt, SolveStatus status, bool wasFaulted)
    {
        var kind = status == SolveStatus.NonFinite ? FaultKind.NonFinite : FaultKind.Singular;
        var row = rt.Circuit.Fault.Row;
        ComponentRef worst = default; NodeId worstNode = default; int compCount = 0, nodeCount = 0;
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

    // =============================================================== build

    /// <summary>Construct (or reconstruct) an island's Circuit from the current
    /// document: assign local node indices, pick the reference datum, stamp every
    /// member primitive and closed-galvanic-coupler branch, apply the wiring
    /// policy (§5), and Analyze. Shape-time — allocation is expected here.</summary>
    private void BuildRuntime(int islandSlot)
    {
        _buildNodes.Clear();
        for (var n = 0; n < _nCount; n++)
            if (_nAlive[n] && _nIsland[n] == islandSlot) _buildNodes.Add(n);
        var k = _buildNodes.Count;

        var refLocal = -1;
        for (var i = 0; i < k; i++)
        {
            var ns = _buildNodes[i];
            _nCircuitNode[ns] = i; _nRtSlot[ns] = islandSlot;
            if (refLocal < 0 && _nRole[ns] == (byte)NodeRole.Reference) refLocal = i;
        }

        var circuit = new Circuit(new SparseLuBackend(), k, refLocal);
        circuit.Dt = _profileDt;
        var nodeSlots = new int[k];
        for (var i = 0; i < k; i++) nodeSlots[i] = _buildNodes[i];

        var auxRows = new List<int>();
        var auxComps = new List<int>();

        for (var c = 0; c < _cCount; c++)
        {
            if (!_cAlive[c] || _nIsland[_cA[c]] != islandSlot) continue;
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

        StampWiring(circuit, islandSlot, k, refLocal);

        circuit.Analyze();

        var rt = _iRuntime[islandSlot];
        rt ??= new IslandRuntime();
        rt.Circuit = circuit; rt.NodeSlots = nodeSlots; rt.NodeCount = k;
        rt.AuxRows = auxRows.ToArray(); rt.AuxComps = auxComps.ToArray(); rt.AuxCount = auxRows.Count;
        _iRuntime[islandSlot] = rt;
        _iRuntimeStale[islandSlot] = false;
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
                // DC operating point: an inductor is a ~1 mΩ short (solver.md).
                var s = circuit.AddResistor(a, b, InductorDcShortOhms);
                SetConductanceStamp(slot, s);
                break;
            }
            case ComponentKind.Capacitor:
                // DC operating point: a capacitor is open — no stamp. Phase 4 adds
                // the Backward-Euler companion.
                _cStampKind[slot] = StampNone;
                break;
            case ComponentKind.VSource:
            {
                // DC value: sine sources contribute their 0-offset DC point here;
                // the subcycled AC drive is phase 4.
                var volts = _cIsSine[slot] ? 0.0 : _cValue[slot];
                var s = circuit.AddVoltageSource(a, b, volts);
                _cStampKind[slot] = StampVSource; _cStamp0[slot] = s.AuxRow; _cStamp1[slot] = s.RhsSlot;
                auxRows.Add(s.AuxRow); auxComps.Add(slot);
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
                // Nonlinear: the Newton companion is phase 4. Contributes nothing
                // at DC (gmin keeps the node nonsingular).
                _cStampKind[slot] = StampNone;
                break;
        }
    }

    private void SetConductanceStamp(int slot, in ConductanceStamp s)
    {
        _cStampKind[slot] = StampConductance;
        _cStamp0[slot] = s.Aa; _cStamp1[slot] = s.Bb; _cStamp2[slot] = s.Ab; _cStamp3[slot] = s.Ba;
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
        if (IslandHasSine(slot))
            throw new NotSupportedException("Step on an AC island requires the subcycled AC pipeline (phase 4).");

        if (_tickStatsSealed) { _lastTickStats = default; _tickStatsSealed = false; }

        Debug.Assert(!_iStepping[slot], "Single-writer-per-island violated: concurrent Step on the same island (api.md §11, §21).");
        _iStepping[slot] = true;
        _stepInFlight++;
        try
        {
            if (_iNeedsRebuild[slot]) { RebuildIsland(slot); _lastTickStats.IslandRebuilds++; }
            // A split may have retired this slot; re-check before the numeric pass.
            if (_iAlive[slot] && (_iStatus[slot] == (byte)IslandStatus.Dirty || _iRuntimeStale[slot]))
                NumericSolveIsland(slot, clock.Dt);
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
                return (NodePotential(a) - NodePotential(b)) / InductorDcShortOhms;
            case ComponentKind.ISource:
                return _cValue[compSlot];   // driven from→to
            case ComponentKind.VSource:
            {
                var isl = _nIsland[a];
                var rt = isl >= 0 ? _iRuntime[isl] : null;
                if (rt is null || _cStampKind[compSlot] != StampVSource) return 0.0;
                return rt.Circuit.ReadFlow(_cStamp0[compSlot]);
            }
            default:
                return 0.0;   // capacitor (open DC), diode/transformer readback: phase 4
        }
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

        double kcl = 0.0;
        if ((which & InvariantChecks.Kcl) != 0) kcl = rt.Circuit.MaxNodeKclResidual();

        var allFinite = true; var firstBad = -1;
        if ((which & InvariantChecks.Finiteness) != 0)
        {
            var v = rt.Circuit.PublishedVector;
            for (var r = 0; r < v.Length; r++)
                if (double.IsNaN(v[r]) || double.IsInfinity(v[r])) { allFinite = false; firstBad = r; break; }
        }
        // Energy (which & Energy): PHASE-6 — returns 0 residual.
        return new InvariantReport(kcl, default, allFinite, firstBad, 0.0);
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
        if (f.NodeCount > 0 && nodes.Length > 0) { nodes[0] = f.WorstNode; }
        return packed;
    }

    // ============================================================ arena growth

    private void EnsureRuntimeIslandCap(int min)
    {
        if (_iRuntime.Length >= min) return;
        var cap = Math.Max(min, _iRuntime.Length * 2);
        Array.Resize(ref _iRuntime, cap); Array.Resize(ref _iRuntimeStale, cap);
        Array.Resize(ref _iFault, cap); Array.Resize(ref _iStepping, cap);
    }

    private void EnsureStampCap(int min)
    {
        if (_cStampKind.Length >= min) return;
        var cap = Math.Max(min, _cStampKind.Length * 2);
        Array.Resize(ref _cStampKind, cap);
        Array.Resize(ref _cStamp0, cap); Array.Resize(ref _cStamp1, cap);
        Array.Resize(ref _cStamp2, cap); Array.Resize(ref _cStamp3, cap); Array.Resize(ref _cStamp4, cap);
    }

    private void EnsureNodeMapCap(int min)
    {
        if (_nCircuitNode.Length >= min) return;
        var cap = Math.Max(min, _nCircuitNode.Length * 2);
        Array.Resize(ref _nCircuitNode, cap); Array.Resize(ref _nRtSlot, cap);
    }
}
