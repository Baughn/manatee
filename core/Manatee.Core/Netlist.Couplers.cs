using System;
using System.Collections.Generic;
using System.Diagnostics;
using Manatee.Core.Solver;

namespace Manatee.Core;

// Boundary couplings made real (solver.md Islands "Scheduling and conservation";
// api.md §7, §11). Islands joined by boundary couplers (DecouplingTransformer,
// ConverterTwoPort) form ONE scheduling unit — discovered by a union-find over the
// boundary-coupler edges, DISTINCT from the galvanic island union-find (_nIsland).
// A unit substeps in LOCKSTEP: N = max member N; per substep every member solves,
// then every boundary coupler exchanges. Because the exchange happens inside the
// unit (never across free-running threads) it is deterministic; SERIAL scheduling
// still holds (units execute serially — this file introduces no cross-unit
// parallelism; unit-local state stays unit-local so the deferred parallel contract
// of api.md §9/§21 stays closable).
//
// Energy is conserved BY CONSTRUCTION (design.md Simulation Model): each boundary
// carries a running ledger (InJ − OutJ − ModeledLossJ = SurplusJ); the transfer is
// CLAMPED to what the source island actually delivered (OutJ never exceeds
// InJ − ModeledLossJ), so SurplusJ ≥ 0 and becomes HeatDumpedJ — never stored work.
public sealed partial class Netlist
{
    // Boundary relaxation is exponential smoothing on the exchanged values (α per
    // CouplerSpec, default 0.5). We smooth the AMPLITUDE the primary presents to the
    // secondary and the reflected secondary CURRENT — the two quantities solver.md's
    // "amplitude+phase exchange per substep" transfers (a sinusoid's instantaneous
    // sample carries both amplitude and phase at ≥20 samples/cycle).

    // ── Scheduling-unit union-find (island-slot indexed; distinct from _nIsland). ──
    private bool _unitsDirty = true;
    private int[] _iUnitParent = new int[4];      // union-find over boundary-coupler edges
    private int[] _iUnitLead = new int[4];         // island slot → its unit's LEAD island slot
    private ulong[] _iAnchorHi = new ulong[4];     // min member-node ExternalKey per island (lead tiebreak)
    private ulong[] _iAnchorLo = new ulong[4];
    private bool[] _iUnitHasCoupler = new bool[4]; // lead-indexed: unit contains ≥1 boundary coupler
    private bool[] _iUnitActive = new bool[4];      // lead-indexed: unit has ≥1 CLOSED boundary coupler
    private long[] _iCollectMark = new long[4];     // CollectDirty dedupe epoch, per lead
    private long _collectEpoch;

    // Unit-step scratch (reused; shape-time allocation only).
    private readonly List<int> _unitMembers = new();
    private readonly List<int> _unitCouplers = new();
    private readonly List<IslandRuntime> _unitMemberRt = new();
    private readonly List<bool> _unitMemberFaulted = new();     // faulted THIS tick (stop lockstep + skip publish)
    private readonly List<bool> _unitMemberWasFaulted = new();  // faulted on ENTRY (eligible for retry + recovery)

    // ── Per-coupler exchange runtime (indexed by coupler slot). Persists across ticks
    //    (relaxation + ledger state); the injected-source stamps are refreshed on each
    //    member-island rebuild. The per-substep exchange writes STRUCT/scalar fields
    //    only — zero allocation on the hot path. ──
    private CouplerRuntime?[] _kRuntime = new CouplerRuntime?[4];

    private sealed class CouplerRuntime
    {
        public int IslandA = -1, IslandB = -1;   // resolved member island slots

        // A-side injected current source (reflected secondary load / converter input
        // draw), from APos to ANeg inside island A.
        public ISourceStamp AStamp;
        public bool AStamped;
        public int ALocalPos = -1, ALocalNeg = -1;   // local Circuit node indices in island A

        // B-side injected voltage source (transformer: V_A/n; converter: regulated
        // setpoint), across (BPos,BNeg) inside island B.
        public VSourceStamp BVSource;
        public bool BVStamped;
        public int BLocalPos = -1, BLocalNeg = -1;   // local Circuit node indices in island B

        // Converter DC-link capacitor (real boundary storage) stamped across the B
        // port when island B is built transient; open (unstamped) at DC.
        public CompanionStamp DcLink;
        public bool DcLinkStamped;
        public double DcLinkVPrev;   // BE history state (cap voltage a→b)

        // Relaxation state (smoothed voltage the primary presents, smoothed reflected
        // current) and the values ACTIVE in the two circuits during the last solve.
        public double VSmooth, ISmooth;
        public double LastVB, LastIA;

        // Running energy ledger (api.md §7 EnergyLedger). InJ/OutJ/ModeledLossJ are the
        // integrated primitives; SurplusJ/HeatDumpedJ/Residual are derived at readback.
        public double InJ, OutJ, ModeledLossJ;

        // Latest instantaneous exchange (api.md §7 ExchangeView) + test seams.
        public double AmplitudeA, PhaseA, AmplitudeB, PhaseB, PowerA2B;
        public double LastPIn, LastPOut;
    }

    // ============================================================ unit union-find

    private void EnsureUnitsFresh()
    {
        if (_unitsDirty) RebuildUnits();
    }

    private void EnsureUnitCap(int min)
    {
        if (_iUnitParent.Length >= min) return;
        var cap = Math.Max(min, _iUnitParent.Length * 2);
        Array.Resize(ref _iUnitParent, cap); Array.Resize(ref _iUnitLead, cap);
        Array.Resize(ref _iAnchorHi, cap); Array.Resize(ref _iAnchorLo, cap);
        Array.Resize(ref _iUnitHasCoupler, cap); Array.Resize(ref _iUnitActive, cap);
        Array.Resize(ref _iCollectMark, cap);
    }

    private int UnitFind(int x)
    {
        while (_iUnitParent[x] != x) { _iUnitParent[x] = _iUnitParent[_iUnitParent[x]]; x = _iUnitParent[x]; }
        return x;
    }

    private void UnitUnion(int a, int b)
    {
        int ra = UnitFind(a), rb = UnitFind(b);
        if (ra != rb) _iUnitParent[rb] = ra;
    }

    // Recompute the scheduling-unit partition and each unit's deterministic lead
    // (the member with the smallest min-node ExternalKey — rebuild-stable, matching
    // Islands.Ids ordering). Allocation-free (preallocated arrays), so CollectDirty
    // stays 0B. Bounded by island + coupler + node count; runs only when _unitsDirty.
    private void RebuildUnits()
    {
        EnsureUnitCap(_iSlotCount);

        for (var s = 0; s < _iSlotCount; s++)
        {
            if (!_iAlive[s]) continue;
            _iUnitParent[s] = s;
            _iAnchorHi[s] = ulong.MaxValue; _iAnchorLo[s] = ulong.MaxValue;
            _iUnitHasCoupler[s] = false; _iUnitActive[s] = false;
        }

        // Min node key per island → the anchor used to pick a deterministic lead.
        for (var n = 0; n < _nCount; n++)
        {
            if (!_nAlive[n]) continue;
            var isl = _nIsland[n];
            if (isl < 0) continue;
            if (KeyLessThanAnchor(_nKey[n], isl))
            { _iAnchorHi[isl] = _nKey[n].Hi; _iAnchorLo[isl] = _nKey[n].Lo; }
        }

        // Union over boundary-coupler edges (existence-based: a boundary coupler binds
        // its two islands into one unit whether Open or Closed, so toggling exchange is
        // "exchange only" and never reshapes the schedule — api.md §4/§7).
        for (var k = 0; k < _kCount; k++)
        {
            if (!_kAlive[k] || _kSpec[k].IsGalvanic) continue;
            var a = _nIsland[_kAPos[k]]; var b = _nIsland[_kBPos[k]];
            if (a < 0 || b < 0) continue;
            UnitUnion(a, b);
        }

        // Resolve the lead per set: min-anchor member (tiebreak by slot via the ≤ in
        // AnchorLess so lower slot wins ties). Path-compress first.
        for (var s = 0; s < _iSlotCount; s++) if (_iAlive[s]) _iUnitParent[s] = UnitFind(s);
        for (var s = 0; s < _iSlotCount; s++) if (_iAlive[s]) _iUnitLead[s] = -1;
        for (var s = 0; s < _iSlotCount; s++)
        {
            if (!_iAlive[s]) continue;
            var r = _iUnitParent[s];
            if (_iUnitLead[r] < 0 || AnchorLess(s, _iUnitLead[r])) _iUnitLead[r] = s;
        }
        for (var s = 0; s < _iSlotCount; s++)
            if (_iAlive[s]) _iUnitLead[s] = _iUnitLead[_iUnitParent[s]];

        // Mark units that carry a boundary coupler (route through StepUnit) and units
        // with a live (Closed) exchange (re-solve every tick until settled).
        for (var k = 0; k < _kCount; k++)
        {
            if (!_kAlive[k] || _kSpec[k].IsGalvanic) continue;
            var a = _nIsland[_kAPos[k]]; var b = _nIsland[_kBPos[k]];
            if (a < 0 || b < 0) continue;
            var lead = _iUnitLead[a];
            _iUnitHasCoupler[lead] = true;
            if (_kStateA[k] == CouplerState.Closed) _iUnitActive[lead] = true;
        }

        _unitsDirty = false;
    }

    private bool KeyLessThanAnchor(in ExternalKey k, int isl)
    {
        if (k.Hi != _iAnchorHi[isl]) return k.Hi < _iAnchorHi[isl];
        if (k.Lo != _iAnchorLo[isl]) return k.Lo < _iAnchorLo[isl];
        return false;
    }

    // Island a's anchor strictly-or-tie-less-than island b's (slot breaks ties so the
    // lead is a total, deterministic function of membership — never dictionary order).
    private bool AnchorLess(int a, int b)
    {
        if (_iAnchorHi[a] != _iAnchorHi[b]) return _iAnchorHi[a] < _iAnchorHi[b];
        if (_iAnchorLo[a] != _iAnchorLo[b]) return _iAnchorLo[a] < _iAnchorLo[b];
        return a < b;
    }

    // True iff `slot` should be scheduled through StepUnit (its unit carries a boundary
    // coupler). Non-unit islands keep the unchanged solo path (no regression).
    private bool RouteAsUnit(int slot)
    {
        var lead = _iUnitLead[slot];
        return lead >= 0 && _iUnitHasCoupler[lead];
    }

    private bool IsUnitLead(int slot) => _iUnitLead[slot] == slot;

    // ============================================================ unit stepping

    // Solve one scheduling unit in lockstep. Called on the unit LEAD only (RunSolve /
    // Step route non-leads away; a stray non-lead Step debug-asserts). Every member
    // does one substep solve, then every boundary coupler exchanges, N times.
    private void StepUnit(int leadSlot, double tickDt, bool operatingPoint)
    {
        GatherUnit(leadSlot);

        // Content scan across members → unit regime (max frequency, any storage).
        var anySine = false; var maxFreq = 0.0;
        for (var i = 0; i < _unitMembers.Count; i++)
        {
            ScanIsland(_unitMembers[i], out var hs, out _, out _, out var mf);
            if (hs) { anySine = true; if (mf > maxFreq) maxFreq = mf; }
        }

        var isAc = !operatingPoint && _profileKind == SolverProfile.Regime.Mixed && anySine;
        int n; double substepDt;
        if (isAc) { n = PlanSubstepN(leadSlot, tickDt, maxFreq); substepDt = tickDt / n; }
        else { n = 1; substepDt = operatingPoint ? _profileDt : tickDt; _iSubstepRawRef[leadSlot] = 0.0; }

        // Ensure every member's runtime is current for the UNIT's substep dt (so
        // exchanges align in time), record per-member runtimes + entry fault state.
        // A member Faulted ON ENTRY is eligible for RETRY this tick (mirroring the solo
        // NumericSolveIsland path, which re-solves a faulted transient island every tick
        // and recovers on success) — so the in-loop skip flag starts FALSE and the entry
        // status is captured separately to gate the IslandRecovered edge in publish.
        _unitMemberRt.Clear(); _unitMemberFaulted.Clear(); _unitMemberWasFaulted.Clear();
        for (var i = 0; i < _unitMembers.Count; i++)
        {
            var m = _unitMembers[i];
            ScanIsland(m, out _, out var hasStorage, out _, out _);
            var wantCompanions = !operatingPoint && hasStorage;
            var rt = EnsureUnitMemberRuntime(m, operatingPoint, wantCompanions, substepDt);
            _iSubstepN[m] = n; _iSubstepDt[m] = operatingPoint ? _profileDt : substepDt;
            _unitMemberRt.Add(rt);
            _unitMemberFaulted.Add(false);   // in-loop "faulted THIS tick" — retry entry-faulted members
            _unitMemberWasFaulted.Add(_iStatus[m] == (byte)IslandStatus.Faulted);
        }

        // Refresh cached coupler endpoints (stamps updated during BuildRuntime).
        for (var i = 0; i < _unitCouplers.Count; i++)
        {
            var kc = _unitCouplers[i];
            var rt = _kRuntime[kc];
            if (rt is null) continue;
            rt.IslandA = _nIsland[_kAPos[kc]]; rt.IslandB = _nIsland[_kBPos[kc]];
        }

        var steps = operatingPoint ? 1 : n;
        for (var k = 0; k < steps; k++)
        {
            // 1. Each member does one substep solve (order = Gather order = ascending
            //    member slot ⇒ deterministic).
            for (var i = 0; i < _unitMembers.Count; i++)
            {
                if (_unitMemberFaulted[i]) continue;
                var m = _unitMembers[i];
                var rt = _unitMemberRt[i];
                var wasFaulted = _iStatus[m] == (byte)IslandStatus.Faulted;
                var st = AdvanceOneSubstep(rt, substepDt, operatingPoint);
                if (st != SolveStatus.Ok)
                {
                    FaultIsland(m, rt, st, wasFaulted);
                    _unitMemberFaulted[i] = true;
                }
            }

            // 2. Every boundary coupler exchanges. A faulted member faults the UNIT's
            //    exchange for couplers touching it (transfer reports zero); the rest of
            //    the unit keeps solving (item 5).
            for (var i = 0; i < _unitCouplers.Count; i++)
                DoExchange(_unitCouplers[i], substepDt);
        }

        // Publish member statuses (mirror NumericSolveIsland's tail). Skip only members
        // faulted THIS tick; a member faulted on ENTRY but retried successfully this tick
        // publishes Ready and emits the IslandRecovered edge (gated on the captured entry
        // flag so the IslandFaulted/IslandRecovered pairing stays balanced — api.md §15/§20).
        for (var i = 0; i < _unitMembers.Count; i++)
        {
            var m = _unitMembers[i];
            if (_unitMemberFaulted[i]) continue;
            var wasFaulted = _unitMemberWasFaulted[i];
            _iStatus[m] = (byte)IslandStatus.Ready;
            if (wasFaulted)
            {
                _iFault[m] = default;
                var id = new IslandId(m, _iGen[m], _netId);
                _journal.Append(TopologyEventKind.IslandRecovered, default, default, id, default);
                RecordChange(IslandChangeKind.Recovered, id, default);
            }
        }
    }

    private void GatherUnit(int leadSlot)
    {
        _unitMembers.Clear(); _unitCouplers.Clear();
        for (var s = 0; s < _iSlotCount; s++)
            if (_iAlive[s] && _iUnitLead[s] == leadSlot) _unitMembers.Add(s);
        for (var k = 0; k < _kCount; k++)
        {
            if (!_kAlive[k] || _kSpec[k].IsGalvanic) continue;
            var a = _nIsland[_kAPos[k]];
            if (a >= 0 && _iUnitLead[a] == leadSlot) _unitCouplers.Add(k);
        }
    }

    // True iff the unit must run this tick: at an operating point, if any member is
    // dirty or time-dependent, or if the unit has a live (Closed) boundary coupler —
    // a coupled boundary is a damped fixed-point iterated across ticks/substeps, so an
    // active exchange keeps the unit solving (tier-1 RHS once settled) until it agrees.
    private bool UnitNeedsSolve(int leadSlot)
    {
        if (_iUnitActive[leadSlot]) return true;
        for (var s = 0; s < _iSlotCount; s++)
        {
            if (!_iAlive[s] || _iUnitLead[s] != leadSlot) continue;
            if (_iStatus[s] == (byte)IslandStatus.Dirty || IslandIsTransient(s)) return true;
        }
        return false;
    }

    private IslandRuntime EnsureUnitMemberRuntime(int slot, bool operatingPoint, bool wantCompanions, double substepDt)
    {
        var rt = _iRuntime[slot];
        if (rt is null || _iRuntimeStale[slot] || rt.HasCompanions != wantCompanions)
        {
            BuildRuntime(slot, wantCompanions, substepDt);
            rt = _iRuntime[slot]!;
        }
        else if (wantCompanions && rt.SubstepDt != substepDt)
        {
            RestampSubstepDt(rt, substepDt);
        }
        rt.OperatingPoint = operatingPoint;
        if (operatingPoint)
            for (var i = 0; i < rt.SineCount; i++)
                rt.Circuit.SetVSourceValue(rt.SineStamps[i], 0.0);
        return rt;
    }

    // One substep of a single island (the unit-lockstep analogue of StepTransient's
    // loop body — same numerics: advance sine phase, write BE history, factorize-if-
    // dirty + back-substitute, advance companion state). Zero-alloc.
    private SolveStatus AdvanceOneSubstep(IslandRuntime rt, double substepDt, bool operatingPoint)
    {
        if (!operatingPoint)
            for (var i = 0; i < rt.SineCount; i++)
            {
                var c = rt.SineComps[i];
                var omega = TwoPi * _cSine[c].FreqHz;
                var phase = _cStateVar[c] + omega * substepDt;
                while (phase >= TwoPi) phase -= TwoPi;
                _cStateVar[c] = phase;
                rt.Circuit.SetVSourceValue(rt.SineStamps[i], _cSine[c].AmplitudeV * Math.Sin(phase));
            }

        for (var i = 0; i < rt.CompCount; i++)
        {
            var c = rt.CompComps[i];
            double iEq;
            if (rt.CompKind[i] == CompCap) iEq = (_cValue[c] / substepDt) * _cStateVar[c];
            else iEq = -_cStateVar[c];
            rt.Circuit.SetCompanionCurrent(rt.CompStamps[i], iEq);
        }

        var st = rt.HasNonlinear ? NewtonSolvePoint(rt) : FactorAndSolve(rt);
        if (st != SolveStatus.Ok) return st;
        _lastTickStats.Substeps++;

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
        return SolveStatus.Ok;
    }

    // ============================================================ the exchange

    private void DoExchange(int k, double substepDt)
    {
        var rt = _kRuntime[k];
        if (rt is null || !rt.AStamped || !rt.BVStamped) return;
        if (rt.IslandA < 0 || rt.IslandB < 0) return;

        // Degenerate self-coupling: if a Closed galvanic breaker has merged this coupler's
        // two ports into ONE island (same _nIsland), the boundary exchange would stamp its
        // A-side current source and B-side voltage source into the SAME circuit and drive a
        // self-referential transfer with a meaningless port current and a bogus ledger. The
        // galvanic 1 mΩ bridge already carries the real through-current, so short-circuit
        // the boundary exchange entirely while the sides are merged (no injection, no ledger
        // accrual). It re-arms automatically when the breaker Opens and the islands split.
        if (rt.IslandA == rt.IslandB) return;
        var rtA = _iRuntime[rt.IslandA]; var rtB = _iRuntime[rt.IslandB];
        if (rtA is null || rtB is null) return;

        var faulted = _kStateA[k] != CouplerState.Closed
                      || _iStatus[rt.IslandA] == (byte)IslandStatus.Faulted
                      || _iStatus[rt.IslandB] == (byte)IslandStatus.Faulted;

        // Solved values of the substep just completed.
        var vA = rtA.Circuit.ReadPotential(rt.ALocalPos) - rtA.Circuit.ReadPotential(rt.ALocalNeg);
        // Secondary current delivered into island B: positive when the B-source pushes
        // current out of its + terminal into the load. ReadFlow is +1-incidence at pos
        // (current INTO the branch from pos), so a delivering source reads negative ⇒
        // negate to get the delivered load current (verified by the coupler DC tests).
        var iSecondary = -rtB.Circuit.ReadFlow(rt.BVSource.AuxRow);

        // Power of the substep just solved, at the source values ACTIVE during it.
        var deliveredByA = vA * rt.LastIA;           // energy leaving island A into the coupler
        var deliveredToB = rt.LastVB * iSecondary;   // energy the coupler delivered into island B

        if (faulted)
        {
            // A faulted member (or an Open coupler) stops all transfer: hold both
            // injected sources at zero and report zero exchange. Ledger integrators
            // freeze (no In/Out accrues while faulted). The rest of the unit keeps
            // solving — a faulted member faults only its UNIT's exchange (item 5).
            rtB.Circuit.SetVSourceValue(rt.BVSource, 0.0);
            rtA.Circuit.SetCurrentSource(rt.AStamp, 0.0);
            rt.VSmooth = 0.0; rt.ISmooth = 0.0; rt.LastVB = 0.0; rt.LastIA = 0.0;
            rt.PowerA2B = 0.0; rt.AmplitudeA = 0.0; rt.AmplitudeB = 0.0;
            rt.LastPIn = 0.0; rt.LastPOut = 0.0;
            return;
        }

        var alpha = _kSpec[k].RelaxationAlpha;
        if (!(alpha > 0.0) || alpha > 1.0) alpha = 0.5;

        if (_kSpec[k].Kind == CouplerSpec.Family.DecouplingTransformer)
            ExchangeTransformer(k, rt, rtA, rtB, substepDt, vA, iSecondary, deliveredByA, deliveredToB, alpha);
        else
            ExchangeConverter(k, rt, rtA, rtB, substepDt, vA, iSecondary, deliveredByA, deliveredToB, alpha);
    }

    // DecouplingTransformer (solver.md Islands): amplitude+phase feed-forward and
    // reflected-current feedback, both damped by α. n = TurnsRatio (primary:secondary):
    //   V_B ← relax(V_A) / n   (secondary voltage = primary / turns)
    //   i_A ← relax(i_B) / n   (primary current = secondary / turns; ampere-turns)
    // The reflected current makes island A source EXACTLY the (transformed) current B
    // drew — the clamp's meaning: A cannot deliver less than B pulled, and the ledger
    // dumps the relaxation-lag residue as heat rather than storing it. Current-source
    // reflection is chosen over conductance reflection because it preserves phase for a
    // reactive secondary load (a reflected resistor would assume B is purely resistive).
    private void ExchangeTransformer(int k, CouplerRuntime rt, IslandRuntime rtA, IslandRuntime rtB,
        double dt, double vA, double iSecondary, double deliveredByA, double deliveredToB, double alpha)
    {
        var n = _kSpec[k].Transformer.TurnsRatio;
        if (!(Math.Abs(n) > 0.0)) n = 1.0;

        AccrueLedger(rt, dt, deliveredByA, deliveredToB, modeledLoss: 0.0);
        rt.PowerA2B = deliveredByA;
        rt.AmplitudeA = vA; rt.AmplitudeB = rt.LastVB;
        // Phase attribution is deferred (a boundary couples multiple possible sine
        // sources); the instantaneous amplitude sample carries the transferred value at
        // ≥20 samples/cycle. Reported as 0 until a phase-lock estimator lands.
        rt.PhaseA = 0.0; rt.PhaseB = 0.0;
        rt.LastPIn = deliveredByA; rt.LastPOut = deliveredToB;

        rt.VSmooth = alpha * vA + (1.0 - alpha) * rt.VSmooth;
        rt.ISmooth = alpha * iSecondary + (1.0 - alpha) * rt.ISmooth;

        var newVB = rt.VSmooth / n;
        var newIA = rt.ISmooth / n;
        rtB.Circuit.SetVSourceValue(rt.BVSource, newVB);
        rtA.Circuit.SetCurrentSource(rt.AStamp, newIA);
        rt.LastVB = newVB; rt.LastIA = newIA;
    }

    // ConverterTwoPort (Stationeers charger/xfmr): behavioral P-transfer. The B port is
    // held at the regulated setpoint (an ideal regulator; the real DC-link capacitor is
    // the honest boundary storage, stamped across B when transient). P_out is measured,
    // efficiency comes from the curve at the load fraction, and P_in = P_out/η is drawn
    // from A. The efficiency loss is EXPLICIT ModeledLossJ → HeatDumpedJ (item 3).
    private void ExchangeConverter(int k, CouplerRuntime rt, IslandRuntime rtA, IslandRuntime rtB,
        double dt, double vA, double iSecondary, double deliveredByA, double deliveredToB, double alpha)
    {
        var spec = _kSpec[k];
        var pOutNow = deliveredToB;
        var pInNow = deliveredByA;

        // Efficiency at the current output operating point (load fraction = P_out / rated).
        var loadFraction = spec.RatedWatts > 0.0 ? Math.Abs(pOutNow) / spec.RatedWatts : 0.0;
        var eff = spec.Efficiency.EfficiencyAt(loadFraction);
        if (!(eff > 0.0)) eff = 1.0;

        // Modeled efficiency loss computed INDEPENDENTLY from the curve — NOT synthesized as
        // (P_in − P_out). Delivering P_out needs P_out/η at the input, so the conversion
        // dissipates P_out·(1/η − 1) as heat. Because this is ≥ 0 by construction (η ≤ 1),
        // ModeledLossJ never goes negative, so HeatDumpedJ = ModeledLossJ + max(SurplusJ,0)
        // is ≥ 0 by construction — the relaxation-lag transient can no longer drive the
        // ledger to report CREATED energy as NEGATIVE heat (design.md energy rule). At the
        // settled operating point P_in = P_out/η, so this equals (P_in − P_out) exactly and
        // the clamp is a no-op; only the load-up transient (where P_in lags) differs.
        var modeledLoss = Math.Abs(pOutNow) * (1.0 / eff - 1.0);
        AccrueLedger(rt, dt, pInNow, pOutNow, modeledLoss);
        rt.PowerA2B = pInNow;
        rt.AmplitudeA = vA; rt.AmplitudeB = rt.LastVB;
        rt.PhaseA = 0.0; rt.PhaseB = 0.0;   // phase attribution deferred (see ExchangeTransformer)
        rt.LastPIn = pInNow; rt.LastPOut = pOutNow;

        // Advance the DC-link capacitor (real boundary storage) — it is voltage-pinned
        // by the regulated source in steady state, so its branch current is ~0 there;
        // it carries genuine stored energy 0.5·C·V² and smooths setpoint transients.
        if (rt.DcLinkStamped)
        {
            var vLink = rtB.Circuit.ReadPotential(rt.BLocalPos) - rtB.Circuit.ReadPotential(rt.BLocalNeg);
            var g = spec.DcLinkFarads / dt;
            rt.DcLinkVPrev = vLink;
            rtB.Circuit.SetCompanionCurrent(rt.DcLink, g * vLink);
        }

        // Stage the next substep: B held at setpoint; A draws P_out/η at V_A.
        var pInTarget = pOutNow / eff;
        var rawIA = Math.Abs(vA) > 1e-12 ? pInTarget / vA : 0.0;
        rt.ISmooth = alpha * rawIA + (1.0 - alpha) * rt.ISmooth;

        var newVB = spec.OutputVolts;
        var newIA = rt.ISmooth;
        rtB.Circuit.SetVSourceValue(rt.BVSource, newVB);
        rtA.Circuit.SetCurrentSource(rt.AStamp, newIA);
        rt.LastVB = newVB; rt.LastIA = newIA;
    }

    // Integrate the boundary ledger for one substep and CLAMP: OutJ never exceeds
    // InJ − ModeledLossJ, so the accounting SurplusJ = InJ − OutJ − ModeledLossJ is
    // ≥ 0 and becomes HeatDumpedJ — energy is conserved by construction, no free energy
    // at the boundary (design.md; solver.md "clamps transfer to what its source island
    // actually delivered").
    private static void AccrueLedger(CouplerRuntime rt, double dt, double deliveredByA, double deliveredToB, double modeledLoss)
    {
        rt.InJ += deliveredByA * dt;
        rt.OutJ += deliveredToB * dt;
        rt.ModeledLossJ += modeledLoss * dt;
        var cap = rt.InJ - rt.ModeledLossJ;
        if (rt.OutJ > cap) rt.OutJ = cap;   // never deliver more than was delivered (minus modeled loss)
    }

    // ============================================================ stamping

    // Stamp the boundary-coupler injected elements for `islandSlot` into the freshly
    // built Circuit and record their stamps in the coupler runtime. Called from
    // BuildRuntime BEFORE Analyze (these add matrix cells / an aux row).
    private void StampBoundaryCouplers(Circuit circuit, int islandSlot, bool companions, double substepDt)
    {
        for (var k = 0; k < _kCount; k++)
        {
            if (!_kAlive[k] || _kSpec[k].IsGalvanic) continue;
            var islA = _nIsland[_kAPos[k]]; var islB = _nIsland[_kBPos[k]];
            var rt = EnsureCouplerRuntime(k);
            rt.IslandA = islA; rt.IslandB = islB;

            // Reset ONLY the side owned by the island being (re)built — the two sides
            // live in separate island circuits and are stamped by separate BuildRuntime
            // calls; a whole-runtime reset here would wipe the other island's live stamp
            // (both sides share one CouplerRuntime).
            if (islandSlot == islA) { rt.AStamped = false; rt.ALocalPos = rt.ALocalNeg = -1; }
            if (islandSlot == islB) { rt.BVStamped = false; rt.DcLinkStamped = false; rt.BLocalPos = rt.BLocalNeg = -1; }

            // A side (primary / input): a current source that loads the A port.
            if (islandSlot == islA
                && _nIsland[_kANeg[k]] == islA && _nRtSlot[_kAPos[k]] == islA)
            {
                var aPos = _nCircuitNode[_kAPos[k]];
                var aNeg = _nCircuitNode[_kANeg[k]];
                rt.AStamp = circuit.AddCurrentSource(aPos, aNeg, rt.LastIA);
                rt.AStamped = true; rt.ALocalPos = aPos; rt.ALocalNeg = aNeg;

                // Magnetizing / core-loss branch: an honest shunt resistor across the
                // primary port (solver.md "modeled as honest device elements"). DC-safe.
                if (_kSpec[k].Kind == CouplerSpec.Family.DecouplingTransformer
                    && _kSpec[k].Transformer.MagnetizingOhms > 0.0)
                    circuit.AddResistor(aPos, aNeg, _kSpec[k].Transformer.MagnetizingOhms);
            }

            // B side (secondary / output): a voltage source (transformed amplitude or
            // regulated setpoint), plus the converter's real DC-link capacitor.
            if (islandSlot == islB
                && _nIsland[_kBNeg[k]] == islB && _nRtSlot[_kBPos[k]] == islB)
            {
                var bPos = _nCircuitNode[_kBPos[k]];
                var bNeg = _nCircuitNode[_kBNeg[k]];
                rt.BVSource = circuit.AddVoltageSource(bPos, bNeg, rt.LastVB);
                rt.BVStamped = true; rt.BLocalPos = bPos; rt.BLocalNeg = bNeg;

                if (_kSpec[k].Kind == CouplerSpec.Family.ConverterTwoPort
                    && companions && _kSpec[k].DcLinkFarads > 0.0)
                {
                    var s = circuit.AddCompanion(bPos, bNeg);
                    var g = _kSpec[k].DcLinkFarads / substepDt;
                    circuit.SetCompanion(s, g, g * rt.DcLinkVPrev);
                    rt.DcLink = s; rt.DcLinkStamped = true;
                }
            }
        }
    }

    private CouplerRuntime EnsureCouplerRuntime(int k)
    {
        if (_kRuntime.Length <= k)
        {
            var cap = Math.Max(k + 1, _kRuntime.Length * 2);
            Array.Resize(ref _kRuntime, cap);
        }
        var rt = _kRuntime[k];
        if (rt is null) { rt = new CouplerRuntime(); _kRuntime[k] = rt; }
        return rt;
    }

    // ============================================================ readbacks (§7, §11)

    internal ExchangeView ExchangeViewOf(CouplerId id)
    {
        if (!ProbeCoupler(id, out var slot)) return default;

        // Closed galvanic breaker: SIGNED through-flow from the 1 mΩ bridge branch
        // (api.md §7 — phase-3 stamped the bridge; this wires the readback). The
        // breaker is in-matrix (merged island), so read node potentials directly.
        if (_kSpec[slot].IsGalvanic)
        {
            if (_kStateA[slot] != CouplerState.Closed) return default;
            var isl = _nIsland[_kAPos[slot]];
            if (isl < 0 || _iRuntime[isl] is null || _nRtSlot[_kAPos[slot]] != isl) return default;
            var c = _iRuntime[isl]!.Circuit;
            var vAPos = c.ReadPotential(_nCircuitNode[_kAPos[slot]]);
            var vBPos = c.ReadPotential(_nCircuitNode[_kBPos[slot]]);
            var vANeg = c.ReadPotential(_nCircuitNode[_kANeg[slot]]);
            var vBNeg = c.ReadPotential(_nCircuitNode[_kBNeg[slot]]);
            var iBridge = (vAPos - vBPos) / CouplerBridgeOhms;   // A→B positive when V(APos) > V(BPos)
            var vPort = vAPos - vANeg;
            var powerA2B = vPort * iBridge;
            _ = vBNeg;
            return new ExchangeView(vAPos, 0.0, vBPos, 0.0, powerA2B);
        }

        var rt = _kRuntime[slot];
        if (rt is null) return default;
        return new ExchangeView(rt.AmplitudeA, rt.PhaseA, rt.AmplitudeB, rt.PhaseB, rt.PowerA2B);
    }

    internal EnergyLedger LedgerOf(CouplerId id)
    {
        if (!ProbeCoupler(id, out var slot) || _kSpec[slot].IsGalvanic) return default;
        var rt = _kRuntime[slot];
        if (rt is null) return default;
        // SurplusJ ≥ 0 is the LOAD-BEARING no-free-energy invariant: the AccrueLedger clamp
        // holds OutJ ≤ InJ − ModeledLossJ, and — because ModeledLossJ is now computed
        // INDEPENDENTLY from the efficiency curve (ExchangeConverter), not synthesized as
        // In−Out — SurplusJ = InJ − OutJ − ModeledLossJ is a genuine residual rather than an
        // algebraic 0. HeatDumpedJ = ModeledLossJ + max(Surplus,0) is ≥ 0 by construction.
        //
        // Residual is the ledger's own CLOSURE identity (InJ = OutJ + HeatDumpedJ), ≈ 0 by
        // construction — it audits the readback arithmetic, NOT that the physical exchange
        // conserved. The physical no-over-transfer bound is the OutJ clamp itself. NOTE: the
        // clamp bounds the RECORDED OutJ; during a relaxation-lag transient the injected B
        // source can momentarily deliver more than A supplied (a bounded idealization that
        // washes out — see the converter/transformer over-delivery note escalated to canon).
        var surplus = rt.InJ - rt.OutJ - rt.ModeledLossJ;
        var heat = rt.ModeledLossJ + (surplus > 0.0 ? surplus : 0.0);
        var residual = rt.InJ - rt.OutJ - heat;
        return new EnergyLedger(rt.InJ, rt.OutJ, rt.ModeledLossJ, surplus, heat, residual);
    }

    // Test/diagnostic seam: the coupler's last instantaneous input/output power.
    internal bool TryReadConverterPowers(CouplerId id, out double pIn, out double pOut)
    {
        pIn = 0.0; pOut = 0.0;
        if (!ProbeCoupler(id, out var slot) || _kSpec[slot].IsGalvanic) return false;
        var rt = _kRuntime[slot];
        if (rt is null) return false;
        pIn = rt.LastPIn; pOut = rt.LastPOut; return true;
    }
}
