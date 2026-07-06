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
// Energy conservation is PHYSICAL, not clerical (api.md §7 ruling, 2026-07-06). The
// running ledger RECORDS (InJ − OutJ − ModeledLossJ = SurplusJ, OutJ clamped so the
// public books close and Residual ≈ 0), but it does NOT license: the physics is enforced
// by two honest mechanisms — the transformer DEBT DROOP (a debit on the B feed-forward
// when the boundary over-delivers; ExchangeTransformer) and the converter's SAGGING
// DC-link capacitor (the B port IS the cap; ExchangeConverter). Whether a boundary
// actually conserved is a windowed physical audit from public readbacks (design.md
// Simulation Model; ConservationAuditTests), never EnergyLedger.Residual.
public sealed partial class Netlist
{
    // Boundary relaxation is exponential smoothing on the exchanged values (α per
    // CouplerSpec, default 0.5). We smooth the AMPLITUDE the primary presents to the
    // secondary and the reflected secondary CURRENT — the two quantities solver.md's
    // "amplitude+phase exchange per substep" transfers (a sinusoid's instantaneous
    // sample carries both amplitude and phase at ≥20 samples/cycle).

    // ── Conservation-ruling tunables (api.md §7, ruled 2026-07-06). All are plain
    //    constants used in transcendental-free comparisons/multiplies on the substep
    //    path, so the exchange stays deterministic and bit-identical across runtimes. ──

    // Transformer debt droop (api.md §7 ruling). The B-side feed-forward physically
    // over-delivers into island B during a relaxation-lag transient; the accumulated
    // deficit D is debited by DROOPING the coupling. scale = 1 − min(1, D / E_ref), where
    // E_ref = throughput-envelope · horizon makes the trip point scale-free (works at 5 W or
    // 5 kW). The droop scales BOTH coupler sides (see ExchangeTransformer), so it is a pure
    // throughput reduction that conserves power at any scale — which is why the debt needs
    // a leak to recover and the reference must be an envelope (both below).
    private const double DebtDroopHorizon = 2.0;   // E_ref = throughput envelope · horizon
    // Deadband: a debt up to DebtDeadbandFrac·E_ref draws NO droop. Wide ON PURPOSE. A single
    // load step / fault-recovery deposits a bounded one-relaxation over-delivery (measured
    // ≈ 0.9·E_ref); keeping the deadband above it means an ISOLATED transient never chokes — it
    // stays at scale≈1 where the balance detector reads "settled" and leaks the debt away
    // (recovering the exact turns ratio). A SUSTAINED oscillation instead accumulates debt
    // WITHOUT bound (it never settles, so the leak never runs), so it crosses any fixed deadband
    // and chokes regardless. The width thus sets the ONE-TIME over-delivery tolerated before a
    // driven pump is caught (bounded, then slope→0) — the price of not choking legitimate steps.
    private const double DebtDeadbandFrac = 1.5;
    private const double DebtScaleEmaBeta = 0.5;   // low-pass on the scale itself → smooth ramp

    // Debt reference E_ref = peak-hold envelope of the UN-DROOPED throughput · horizon
    // (ExchangeTransformer divides the measured input throughput by the current scale to recover
    // what the boundary WOULD carry at scale=1 — a scale-INVARIANT measure). Peak-hold (instant
    // attack, slow decay) so it does not chase noise; un-drooped so it does NOT collapse to 0 as
    // the droop chokes throughput. A reference that collapsed at choke would make the deadband
    // vanish, so any residual debt would read as "excess" and either revive the pump or dead-lock
    // recovery — both observed with a plain EMA/peak-hold of the raw (drooped) throughput. The
    // un-drooped envelope still falls to 0 when the coupler is genuinely IDLE (no load).
    private const double DebtRefDecay = 0.02;      // peak-hold envelope decay (per substep); slow

    // Debt LEAK (per substep) — the RESTORING FORCE that returns the droop to scale=1, GATED on
    // a genuinely balanced boundary (see OverEmaJ/DebtBalancedFrac). Scaling both sides
    // preserves the instantaneous power balance, so a scaled coupler shows NO fresh
    // over-delivery and the debt would otherwise latch, never recovering. A geometric leak
    // bleeds it back once the LOAD has settled: at rest it recovers scale=1 and the turns ratio
    // is restored EXACTLY. It runs fast (τ = 1/DebtLeak ≈ 50 substeps) BECAUSE it is gated —
    // fast recovery when settled, but never while a sustained oscillation keeps the imbalance
    // ratio elevated (that is the pump the gate defends against; an ungated leak launders it).
    private const double DebtLeak = 0.02;

    // Scale-invariant balance detector (2026-07-06 phase-5 review). The debt leaks only when the
    // smoothed imbalance |over| is ≤ this fraction of smoothed throughput. At a settled boundary
    // the ratio → 0 (leak on, scale → 1); under sustained oscillation it stays O(10 %) at ANY
    // droop scale (both EMAs scale together), so the leak stays off. Transcendental-free.
    private const double DebtBalancedFrac = 0.005; // |over|_ema ≤ this·tput_ema ⇒ settled ⇒ leak
    private const double DebtBalanceBeta = 0.05;   // EMA weight for the balance detector (τ ≈ 20)
    private const double DroopScaleMin = 1e-4;     // scale floor: keeps the balance ratio non-degenerate

    // TRANSFORMER exchange-damping stability clamp (2026-07-06 final-wave fix). The
    // boundary exchange is an EXPLICIT damped fixed-point iteration (Jacobi-style:
    // each side solves against the other side's previous substep), so it converges
    // only while the loop's damped reflection gain stays below 1. A GAIN-CAPABLE
    // two-transformer loop (n1·n2 ≤ 1) at low damping (α ≥ ~0.8) crosses that
    // boundary and grows geometrically — and because the debt droop's reference
    // E_ref is a peak-hold of the (also geometrically growing) input throughput,
    // the deadband inflates in lockstep and the droop engages only after
    // astronomical transients (measured: 1e15–1e73 J on the audit-grid corners,
    // all α ≥ 0.8 with high store R). The droop bounds ENERGY ACCOUNTING, not the
    // iteration's spectral radius; the iteration parameter itself must be bounded.
    // α is therefore clamped to 0.7 for transformer exchanges — the highest value
    // the exhaustive stability grid (ConservationAuditTests: α × ra × rb × n1 × n2,
    // gain-capable loops included) verifies dissipative at every corner of the
    // declared domain. Callers may still pass α ∈ (0,1]; values above the clamp
    // trade nothing but a slightly slower relaxation. The converter path is NOT
    // clamped: its charge controller is a provably contracting loop (see
    // ExchangeConverter) and shows no such instability.
    private const double TransformerAlphaMax = 0.7;

    // Converter DC-link default sizing: a converter with an unspecified (≤0) DC-link is
    // physically nonsensical now that the port IS the cap (ruling), so synthesise an
    // honestly-sized one — a capacitance whose stored energy is one DcLinkDefaultTau of
    // rated throughput: C = ratedWatts/OutputVolts² · τ (i.e. G_rated·τ). τ≈one tick
    // keeps regulation tight (settles in a few ticks) without a stiff matrix.
    private const double DcLinkDefaultTau = 0.05;
    private const double DcLinkMinFarads = 1e-6;   // floor so C/dt is finite & the port is never open

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

        // B-side injected element (TRANSFORMER ONLY): a voltage source at V_A/n across
        // (BPos,BNeg). The converter no longer stamps a B voltage source — its B port IS
        // the DC-link capacitor, driven by a charge-controller current source (below).
        public VSourceStamp BVSource;
        public bool BVStamped;
        public int BLocalPos = -1, BLocalNeg = -1;   // local Circuit node indices in island B

        // Converter DC-link capacitor (CONVERTER ONLY): the B port IS this cap (api.md §7
        // conservation ruling 2026-07-06). Stamped as a real Backward-Euler companion
        // across (BPos,BNeg) whenever island B is built for stepping; the charge
        // controller injects into it and the cap voltage SAGS honestly under deficit.
        public CompanionStamp DcLink;
        public bool DcLinkStamped;
        public double DcLinkVPrev;   // BE history state (cap voltage, previous substep)
        public double DcLinkG;       // cap companion conductance C/dt active in the matrix

        // Converter charge-controller current source (CONVERTER ONLY): injects charging
        // current into BPos (from BNeg) targeting OutputVolts, bounded by deliverable
        // power. Replaces the removed ideal setpoint voltage source.
        public ISourceStamp BChargeSource;
        public bool BChargeStamped;
        public double LastICharge;   // charge current ACTIVE during the last B solve

        // Relaxation state (smoothed voltage the primary presents, smoothed reflected
        // current) and the values ACTIVE in the two circuits during the last solve.
        public double VSmooth, ISmooth;
        public double LastVB, LastIA;

        // TRANSFORMER debt-droop feedback (api.md §7 conservation ruling 2026-07-06):
        // the B-side feed-forward voltage source injects V_A/n regardless of what A has
        // delivered, so a relaxation-lag transient physically over-delivers into B. DebtJ
        // is the incrementally-tracked accumulated over-delivery deficit
        //   D = max(0, ∫(deliveredToB − deliveredByA) dt)   [cumulative, floored at 0]
        // and DroopScale (a smooth ramp on the B feed-forward amplitude) debits it back over
        // subsequent substeps until the boundary balances. TputPeakJ is the peak-hold of the
        // UN-DROOPED throughput — the debt reference E_ref (scale-invariant; see
        // ExchangeTransformer). TputEmaJ is a fast EMA of throughput used ONLY by the balance
        // detector. See ExchangeTransformer for the law and its defence.
        public double DebtJ, TputEmaJ, TputPeakJ;
        public double DroopScale = 1.0;   // 1 = no droop (balanced); → 0 chokes B feed-forward

        // Scale-INVARIANT balance detector (2026-07-06 phase-5 review: sustained-oscillation
        // pump). OverEmaJ is a fast EMA of |fresh over-delivery|; it is compared against
        // DebtBalancedFrac·TputEmaJ (the matching throughput EMA above). The debt leaks
        // (recovers scale→1) ONLY when OverEmaJ ≤ frac·TputEmaJ — i.e. the imbalance is a small
        // FRACTION of throughput. Both EMAs scale together with DroopScale, so the ratio is
        // scale-invariant: a sustained oscillation keeps the ratio high even as the coupler
        // chokes (the lag mismatch is a fixed fraction of throughput at any scale), so the leak
        // stays OFF and the debt holds the droop — no laundering, and no choke↔re-arm limit
        // cycle. Recovery fires only when the LOAD genuinely settles.
        public double OverEmaJ;

        // Running energy ledger (api.md §7 EnergyLedger). InJ/OutJ/ModeledLossJ are the
        // integrated primitives; SurplusJ/HeatDumpedJ/Residual are derived at readback.
        // The ledger RECORDS (closure bookkeeping, OutJ clamped so the public books
        // close); it does NOT license — physical conservation is the debt droop
        // (transformer) and the sagging DC-link cap (converter). Residual is a closure
        // identity ≈0, never a conservation signal (LedgerOf).
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
            // A converter's B island carries the DC-link capacitor (a coupler-owned BE
            // companion invisible to ScanIsland), so force the transient/companion build
            // for it even when the island holds no netlist storage — otherwise the cap is
            // never stamped and the "B port IS the cap" ruling degenerates (api.md §7).
            var wantCompanions = !operatingPoint && (hasStorage || MemberIsConverterB(m));
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
        // A converter-B island whose substep dt changed needs a full REBUILD, not a
        // RestampSubstepDt: the coupler-owned DC-link cap companion is invisible to
        // RestampSubstepDt (it only walks netlist companions), so its conductance C/dt
        // would otherwise go stale. Rebuild re-stamps the cap at the new dt (fixed-dt runs
        // never hit this; it keeps variable-dt converters correct).
        var capDtChanged = wantCompanions && rt is not null && rt.SubstepDt != substepDt && MemberIsConverterB(slot);
        if (rt is null || _iRuntimeStale[slot] || rt.HasCompanions != wantCompanions || capDtChanged)
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
                if (_cIsSine[rt.SineComps[i]])   // demoted-to-constant sources keep their driven value
                    rt.Circuit.SetVSourceValue(rt.SineStamps[i], 0.0);
        return rt;
    }

    // One substep of a single island — THE transient loop body, shared verbatim by
    // the solo path (StepTransient delegates here) and the unit-lockstep path
    // (StepUnit): advance sine phase, write BE history, factorize-if-dirty +
    // back-substitute, advance companion state, energy/taps/limits tail. One body ⇒
    // the two schedules stay bit-identical by construction. Zero-alloc.
    private SolveStatus AdvanceOneSubstep(IslandRuntime rt, double substepDt, bool operatingPoint)
    {
        if (!operatingPoint)
            for (var i = 0; i < rt.SineCount; i++)
            {
                var c = rt.SineComps[i];
                // DEMOTED since the runtime was built (Drive(id, volts) on a sine
                // source, api.md §17): the document says constant now — Drive already
                // pushed the RHS; do NOT overwrite it with a stale sine evaluation.
                // The phase accumulator freezes (a re-promotion reseeds it).
                if (!_cIsSine[c]) continue;
                var omega = TwoPi * _cSine[c].FreqHz;
                var phase = _cStateVar[c] + omega * substepDt;     // continuous accumulator
                while (phase >= TwoPi) phase -= TwoPi;             // wrap (value-preserving; idempotent for any increment)
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

        AccumulateIslandEnergy(rt.IslandSlot, substepDt);
        SampleTaps(rt.IslandSlot);
        // Per-substep limit scan (see StepTransient): a subcycled boundary-unit member
        // integrates i²t and catches instantaneous peaks over the cycle, not the tick edge.
        if (!operatingPoint) EvaluateIslandLimits(rt.IslandSlot, substepDt, _evalTickIndex);
        return SolveStatus.Ok;
    }

    // True iff `slot` is the B (output) island of a ConverterTwoPort in this unit — its
    // DC-link cap is a coupler-owned element ScanIsland cannot see, so StepUnit forces
    // the companion (transient) build for it (api.md §7 "the B port IS the cap").
    private bool MemberIsConverterB(int slot)
    {
        for (var i = 0; i < _unitCouplers.Count; i++)
        {
            var kc = _unitCouplers[i];
            if (_kSpec[kc].Kind != CouplerSpec.Family.ConverterTwoPort) continue;
            if (_nIsland[_kBPos[kc]] == slot) return true;
        }
        return false;
    }

    // The DC-link capacitance actually stamped: the spec value, or — because the port IS
    // the cap now (ruling) and a zero-storage port is nonsensical — an honestly-sized
    // default C = ratedWatts/OutputVolts²·τ (G_rated·τ), floored so C/dt stays finite.
    private double EffectiveDcLinkFarads(int k)
    {
        var spec = _kSpec[k];
        if (spec.DcLinkFarads > 0.0) return spec.DcLinkFarads;
        var v = spec.OutputVolts;
        if (v > 0.0 && spec.RatedWatts > 0.0)
        {
            var def = spec.RatedWatts / (v * v) * DcLinkDefaultTau;
            return def > DcLinkMinFarads ? def : DcLinkMinFarads;
        }
        return DcLinkMinFarads;
    }

    // ============================================================ the exchange

    private void DoExchange(int k, double substepDt)
    {
        var rt = _kRuntime[k];
        if (rt is null || !rt.AStamped) return;
        if (rt.IslandA < 0 || rt.IslandB < 0) return;

        // Degenerate self-coupling: if a Closed galvanic breaker has merged this coupler's
        // two ports into ONE island (same _nIsland), the boundary exchange would stamp its
        // A-side current source and B-side element into the SAME circuit and drive a
        // self-referential transfer with a meaningless port current and a bogus ledger. The
        // galvanic 1 mΩ bridge already carries the real through-current, so short-circuit
        // the boundary exchange entirely while the sides are merged (no injection, no ledger
        // accrual). It re-arms automatically when the breaker Opens and the islands split.
        if (rt.IslandA == rt.IslandB) return;
        var rtA = _iRuntime[rt.IslandA]; var rtB = _iRuntime[rt.IslandB];
        if (rtA is null || rtB is null) return;

        var isConverter = _kSpec[k].Kind == CouplerSpec.Family.ConverterTwoPort;
        // B-side readiness differs by family: the transformer drives a B voltage source;
        // the converter drives a B charge-current source into its DC-link cap.
        if (isConverter ? !rt.BChargeStamped : !rt.BVStamped) return;

        var faulted = _kStateA[k] != CouplerState.Closed
                      || _iStatus[rt.IslandA] == (byte)IslandStatus.Faulted
                      || _iStatus[rt.IslandB] == (byte)IslandStatus.Faulted;

        // Solved value of the substep just completed (A port voltage), and A's actual
        // delivery at the current source value ACTIVE during it (a current source injects
        // exactly its set value, so deliveredByA = V_A · i_A is the true energy leaving A).
        var vA = rtA.Circuit.ReadPotential(rt.ALocalPos) - rtA.Circuit.ReadPotential(rt.ALocalNeg);
        var deliveredByA = vA * rt.LastIA;

        if (faulted)
        {
            // A faulted member (or an Open coupler) stops all transfer: hold the injected
            // sources at zero and report zero exchange. Ledger integrators freeze (no
            // In/Out accrues while faulted). The rest of the unit keeps solving — a faulted
            // member faults only its UNIT's exchange (item 5). The converter's DC-link cap
            // is left as-is; with no injection it simply discharges through B's load.
            if (isConverter) rtB.Circuit.SetCurrentSource(rt.BChargeSource, 0.0);
            else rtB.Circuit.SetVSourceValue(rt.BVSource, 0.0);
            rtA.Circuit.SetCurrentSource(rt.AStamp, 0.0);
            rt.VSmooth = 0.0; rt.ISmooth = 0.0; rt.LastVB = 0.0; rt.LastIA = 0.0; rt.LastICharge = 0.0;
            rt.PowerA2B = 0.0; rt.AmplitudeA = 0.0; rt.AmplitudeB = 0.0;
            rt.LastPIn = 0.0; rt.LastPOut = 0.0;
            return;
        }

        var alpha = _kSpec[k].RelaxationAlpha;
        if (!(alpha > 0.0) || alpha > 1.0) alpha = 0.5;
        // Stability clamp (see TransformerAlphaMax): the transformer exchange is an
        // explicit fixed-point iteration whose gain-capable-loop convergence is only
        // verified up to α = 0.7; higher α diverges geometrically faster than the
        // debt droop can debit. Converter α is unclamped (contracting controller).
        if (!isConverter && alpha > TransformerAlphaMax) alpha = TransformerAlphaMax;

        if (isConverter)
            ExchangeConverter(k, rt, rtA, rtB, substepDt, vA, deliveredByA, alpha);
        else
        {
            // Secondary current delivered into island B: positive when the B source pushes
            // current out of its + terminal into the load. ReadFlow is +1-incidence at pos
            // (current INTO the branch from pos), so a delivering source reads negative ⇒
            // negate to get the delivered load current (verified by the coupler DC tests).
            var iSecondary = -rtB.Circuit.ReadFlow(rt.BVSource.AuxRow);
            var deliveredToB = rt.LastVB * iSecondary;   // energy the coupler delivered into B
            ExchangeTransformer(k, rt, rtA, rtB, substepDt, vA, iSecondary, deliveredByA, deliveredToB, alpha);
        }
    }

    // DecouplingTransformer (solver.md Islands): amplitude+phase feed-forward and
    // reflected-current feedback, both damped by α, then both WEAKENED by the debt droop:
    //   V_B ← scale · relax(V_A) / n   (secondary voltage = primary / turns)
    //   i_A ← scale · relax(i_B) / n   (primary current = secondary / turns; ampere-turns)
    //
    // DEBT DROOP (api.md §7 conservation ruling, 2026-07-06; hardened after the phase-5 review
    // found a sustained-oscillation pump the first design missed). The B feed-forward injects
    // V_A/n regardless of what A has delivered, so a relaxation-lag transient physically
    // OVER-DELIVERS into B (delivered-to-B momentarily exceeds drawn-from-A). The ledger's OutJ
    // clamp only closes the public books; it does not undo the physical injection. So we
    // accumulate the raw over-delivery into a debt D (floored ≥ 0) and DEBIT it via `scale`:
    //
    //   scale = max(scaleMin, 1 − min(1, max(0, D − deadband·E_ref) / E_ref))
    //
    // The droop weakens BOTH sides in lockstep, preserving the instantaneous power balance
    // (P_A = P_B at any scale — a pure throughput reduction) yet, as scale → scaleMin, all but
    // disconnecting the coupler and so breaking a gain-capable loop. R18's "deliverable =
    // advertised − accumulated debt" applied to the boundary. THREE mechanisms make it robust
    // against a DRIVEN pump (an oscillating B load), not just an isolated transient or a gain
    // loop — the failure classes the review exercised:
    //  1. E_ref is the peak-hold of the UN-DROOPED throughput (input ÷ scale), a scale-invariant
    //     reference that neither collapses at choke (which would revive the pump / dead-lock
    //     recovery) nor is inflated away by the droop.
    //  2. The restoring leak that recovers scale=1 is GATED on a scale-invariant BALANCE detector
    //     (smoothed |over| ≤ frac·smoothed throughput). A sustained oscillation never balances,
    //     so it cannot launder its accumulating debt below the deadband — the original pump.
    //  3. A WIDE deadband lets an isolated step/fault-recovery sit at scale=1 (settled ⇒ leak ⇒
    //     exact turns ratio) while a sustained oscillation, accumulating debt without bound,
    //     still crosses it and chokes. Over any window this bounds the DRIVEN over-delivery to a
    //     one-time transient (slope → 0), not the unbounded ramp the flat clamp permitted.
    // See ConservationAuditTests for the acceptance gates. All arithmetic is transcendental-free
    // (min/max/÷/±/×) ⇒ bit-identical across runtimes.
    private void ExchangeTransformer(int k, CouplerRuntime rt, IslandRuntime rtA, IslandRuntime rtB,
        double dt, double vA, double iSecondary, double deliveredByA, double deliveredToB, double alpha)
    {
        var n = _kSpec[k].Transformer.TurnsRatio;
        if (!(Math.Abs(n) > 0.0)) n = 1.0;

        AccrueLedger(rt, dt, deliveredByA, deliveredToB, modeledLoss: 0.0);

        // ── debt-droop feedback ──
        // Integrate the TRUE net over-delivery. The cumulative floor keeps it ≥ 0 (never banks
        // net under-delivery as spendable credit: that energy was already dissipated as heat, so
        // re-delivering it would itself pump). The restoring LEAK is GATED on a settled boundary
        // (scale-invariant balance detector below), so a sustained oscillation can neither hide
        // its accumulation below a leak nor launder it away once the droop engages.
        var over = (deliveredToB - deliveredByA) * dt;    // raw physical over-delivery this substep

        // Throughput reference is INPUT-side (deliveredByA = what A actually sourced), NOT the
        // output. In the voltage-GAIN direction (step-up, n<1) the over-delivery INFLATES the
        // output, so an output-based reference inflates the deadband/choke threshold exactly
        // when the pump is strongest and the choke crawls. The input side is the honest
        // "available power" — unaffected by the over-delivery — so it gives a tight, symmetric
        // trip point that chokes step-up and step-down alike.
        var tputIn = (deliveredByA >= 0.0 ? deliveredByA : -deliveredByA) * dt;

        // Debt reference E_ref = peak-hold envelope of INPUT throughput (deliveredByA) · horizon.
        // Peak-hold (instant attack, slow decay) so a driven pump's debt is measured against the
        // real recent throughput, not chased down by the droop. INPUT-side (not output) so the
        // reference is the honest "available power" — in the voltage-GAIN direction (step-up,
        // n<1) the OVER-delivery inflates the OUTPUT, and an output reference would inflate the
        // trip point exactly when the pump is strongest; the input side gives a symmetric,
        // fast trip for step-up and step-down alike. Under a sustained pump the debt saturates
        // debt/E_ref (scale → floor) and HOLDS there; the leak (gated on settled) recovers only
        // when the load steadies. NOTE: this is deliberately NOT the "un-drooped" throughput
        // (tputIn ÷ scale): dividing by a collapsing scale is a positive feedback that, in a
        // runaway GAIN LOOP (growing tputIn, shrinking scale), blows E_ref up and disengages the
        // droop — a divergence. Raw input peak-hold has no such feedback.
        var decayed = (1.0 - DebtRefDecay) * rt.TputPeakJ;
        rt.TputPeakJ = tputIn > decayed ? tputIn : decayed;
        var eref = rt.TputPeakJ * DebtDroopHorizon;

        // Scale-invariant balance detector (recovery gate): a FAST EMA of |over| vs a fast EMA
        // of input throughput. Both scale with DroopScale, so the ratio survives choking — a
        // driven pump reads "not settled" at ANY scale (the lag mismatch is a fixed fraction of
        // throughput), a genuinely steady load reads "settled" (ratio → 0). The leak (recovery)
        // fires ONLY when settled, so a sustained oscillation neither hides its accumulation
        // below a leak nor launders it away once drooped, yet a one-off transient recovers.
        var absOver = over >= 0.0 ? over : -over;
        rt.OverEmaJ = DebtBalanceBeta * absOver + (1.0 - DebtBalanceBeta) * rt.OverEmaJ;
        rt.TputEmaJ = DebtBalanceBeta * tputIn + (1.0 - DebtBalanceBeta) * rt.TputEmaJ;
        var settled = rt.OverEmaJ <= DebtBalancedFrac * rt.TputEmaJ;

        rt.DebtJ += over;                                  // accumulate net over-delivery
        // Restoring leak (recovery) fires ONLY when the boundary is SETTLED (scale-invariant
        // |over|/tput ratio below threshold). This single gate closes both laundering channels:
        //  • a SUB-deadband oscillation never settles (its imbalance ratio stays elevated), so it
        //    cannot bleed its own sawtooth debt away — the debt accumulates until the droop bites;
        //  • a sustained pump that has driven the coupler to the scale floor STILL reads "not
        //    settled" (the ratio is scale-invariant — see the floor below), so the choke holds and
        //    there is no choke↔revive limit cycle. A one-off transient, or a genuinely steadied
        //    load, settles and recovers to scale=1 exactly.
        if (settled) rt.DebtJ *= (1.0 - DebtLeak);
        if (rt.DebtJ < 0.0) rt.DebtJ = 0.0;               // cumulative floor: surplus → heat, never banked

        // Deadband: a debt up to DebtDeadbandFrac·E_ref draws NO droop. The inherent one-substep
        // startup/step lag produces a small bounded debt that self-corrects; a gain loop / driven
        // pump instead drives D FAR past the deadband, so it is debited hard.
        var excess = rt.DebtJ - DebtDeadbandFrac * eref;
        // Smooth proportional droop. Because E_ref is the un-drooped envelope (does not collapse
        // under choke), this needs no special-casing: a sustained pump holds the debt above the
        // deadband (not settled ⇒ no leak) so the scale stays choked; a steadied load leaks the
        // debt below the deadband so excess ≤ 0 and the scale recovers to 1 — smoothly, via the
        // debt, with no abrupt jumps to chatter the boundary. A truly idle coupler has eref → 0
        // and takes the excess ≤ 0 branch (no droop).
        var target = excess > 0.0 && eref > 1e-15 ? 1.0 - Math.Min(1.0, excess / eref) : 1.0;
        rt.DroopScale = DebtScaleEmaBeta * target + (1.0 - DebtScaleEmaBeta) * rt.DroopScale;

        // Scale FLOOR. The droop never fully disconnects: a tiny residual coupling keeps the
        // balance detector out of its 0/0 degeneracy (at exactly scale=0 throughput and |over|
        // both vanish, the |over|/tput ratio is undefined and a driven pump would read "settled"
        // and revive). At the floor the ratio stays scale-invariant, so a continuing oscillation
        // reads "not settled" (choke holds) while a steadied load reads "settled" (recovers). The
        // floor is small enough (0.0001) that any gain loop still decays (loop gain × floor ≪ 1)
        // and the residual throughput is a negligible fraction of the pump it replaces.
        if (rt.DroopScale < DroopScaleMin) rt.DroopScale = DroopScaleMin;

        rt.PowerA2B = deliveredByA;
        rt.AmplitudeA = vA; rt.AmplitudeB = rt.LastVB;
        // Phase attribution is deferred (a boundary couples multiple possible sine
        // sources); the instantaneous amplitude sample carries the transferred value at
        // ≥20 samples/cycle. Reported as 0 until a phase-lock estimator lands.
        rt.PhaseA = 0.0; rt.PhaseB = 0.0;
        rt.LastPIn = deliveredByA; rt.LastPOut = deliveredToB;

        rt.VSmooth = alpha * vA + (1.0 - alpha) * rt.VSmooth;
        rt.ISmooth = alpha * iSecondary + (1.0 - alpha) * rt.ISmooth;

        // The droop weakens the coupler CONSISTENTLY on BOTH sides — the delivered voltage
        // AND the reflected current draw — like raising its source impedance / lowering the
        // coupling. Scaling both preserves the instantaneous power balance P_A = V_A·i_A =
        // V_B·i_B = P_B (each side × scale), so it only reduces THROUGHPUT, never invents a
        // mismatch. Scaling V_B ALONE is not enough: the A-side current source would keep
        // pumping a gain loop even with V_B choked to 0 (the failure this fixes). As
        // scale → 0 the coupler disconnects, breaking the loop; RC losses then bleed it down.
        var newVB = rt.DroopScale * (rt.VSmooth / n);
        var newIA = rt.DroopScale * (rt.ISmooth / n);
        rtB.Circuit.SetVSourceValue(rt.BVSource, newVB);
        rtA.Circuit.SetCurrentSource(rt.AStamp, newIA);
        rt.LastVB = newVB; rt.LastIA = newIA;
    }

    // ConverterTwoPort (Stationeers charger/xfmr): behavioral P-transfer whose B port IS
    // the DC-link capacitor (api.md §7 conservation ruling, 2026-07-06) — there is NO
    // ideal setpoint source. A charge controller (this method, NOT a netlist element)
    // injects current into the cap targeting OutputVolts, with the injected power BOUNDED
    // by min(rating, η·what A actually delivered last substep). B's load draws from the
    // cap; when the bound bites (starved A or a converter rated below demand) the cap
    // voltage SAGS — an honest brownout that sheds a resistive load naturally (no
    // deadlock). The efficiency loss stays independent-curve-computed (P_out·(1/η−1) ≥ 0),
    // so HeatDumpedJ ≥ 0 by construction. Everything here is one linear stamp per substep.
    //
    // Controller (R-agnostic, provably contracting): the load current is MEASURED by KCL
    //   i_load = i_charge_prev − i_cap,   i_cap = G_cap·(V − V_prev)   (BE cap current)
    // and fed forward, plus a proportional charge-up G_cap·(V_target − V). With gain G_cap
    // the closed loop is V_err[k+1] = [G_load/(G_cap+G_load)]·V_err[k] (factor < 1 for any
    // load ⇒ monotone, no overshoot) and ZERO steady-state error (feed-forward exact at
    // rest ⇒ V = V_target to machine precision). Under the power bound it becomes a
    // constant-power source into the cap: (G_cap±G_load)/(G_cap+G_load) < 1 ⇒ still stable.
    private void ExchangeConverter(int k, CouplerRuntime rt, IslandRuntime rtA, IslandRuntime rtB,
        double dt, double vA, double deliveredByA, double alpha)
    {
        var spec = _kSpec[k];
        var vTarget = spec.OutputVolts;

        // Just-solved DC-link (port) voltage and the cap current that flowed this substep.
        var vLink = rtB.Circuit.ReadPotential(rt.BLocalPos) - rtB.Circuit.ReadPotential(rt.BLocalNeg);
        var gCap = rt.DcLinkG;
        var iCap = gCap * (vLink - rt.DcLinkVPrev);         // BE: i_cap = C/dt·(V−V_prev)
        var iLoad = rt.LastICharge - iCap;                  // KCL at BPos: injected = cap + load

        // Power actually delivered into B this substep (charge source at the port voltage).
        var pOutNow = vLink * rt.LastICharge;
        var pInNow = deliveredByA;

        // Efficiency at the output operating point (load fraction = P_out / rated), and the
        // INDEPENDENT modeled loss P_out·(1/η − 1) ≥ 0 (never synthesized as In−Out).
        var loadFraction = spec.RatedWatts > 0.0 ? Math.Abs(pOutNow) / spec.RatedWatts : 0.0;
        var eff = spec.Efficiency.EfficiencyAt(loadFraction);
        if (!(eff > 0.0)) eff = 1.0;
        var modeledLoss = Math.Abs(pOutNow) * (1.0 / eff - 1.0);
        AccrueLedger(rt, dt, pInNow, pOutNow, modeledLoss);

        rt.PowerA2B = pInNow;
        rt.AmplitudeA = vA; rt.AmplitudeB = vLink;
        rt.PhaseA = 0.0; rt.PhaseB = 0.0;   // phase attribution deferred (see ExchangeTransformer)
        rt.LastPIn = pInNow; rt.LastPOut = pOutNow; rt.LastVB = vLink;

        // Advance the DC-link BE history for the next substep (Ieq = G_cap·V).
        if (rt.DcLinkStamped)
        {
            rtB.Circuit.SetCompanionCurrent(rt.DcLink, gCap * vLink);
            rt.DcLinkVPrev = vLink;
        }

        // ── charge controller: regulate toward V_target, bounded by deliverable power ──
        var iChargeDesired = iLoad + gCap * (vTarget - vLink);

        // (1) RATING bound (the converter's own hard output limit): an undersized converter
        // (ratedWatts < demand) cannot output more, so beyond it the cap sags = brownout.
        var iChargeRated = iChargeDesired;
        if (spec.RatedWatts > 0.0 && vLink > 1e-9 && vLink * iChargeRated > spec.RatedWatts)
            iChargeRated = spec.RatedWatts / vLink;
        if (iChargeRated < 0.0) iChargeRated = 0.0;

        // A draws the input for the RATING-limited DESIRED output (P_in = P_out/η; the
        // curve-independent loss lands as heat). Sizing the A draw off the desired — not off
        // the A-delivery-bounded injection below — is what breaks the ratchet: A is asked
        // for the full amount, delivers it (a stiff A), and the conservation bound opens to
        // exactly that, so a healthy boundary reaches setpoint with zero steady droop. α
        // damps the draw; the DC-link cap buffers the lag (its physical job).
        var pOutRated = vLink * iChargeRated;
        var pInTarget = pOutRated / eff;
        var rawIA = Math.Abs(vA) > 1e-12 ? pInTarget / vA : 0.0;
        rt.ISmooth = alpha * rawIA + (1.0 - alpha) * rt.ISmooth;

        // (2) CONSERVATION bound: the actual injection may not exceed η·(what A ACTUALLY
        // delivered last substep) — so when A is starved the cap sags honestly instead of
        // the converter inventing energy. Spin-up grace (pInNow>0): at cold start A has not
        // been asked yet, so this would wrongly choke the reactive cap charge-up to 0 and
        // deadlock; the DC-link charges under the rating bound alone until A is delivering.
        // For a stiff A at steady state η·pInNow = P_out exactly ⇒ this is a no-op.
        var iCharge = iChargeRated;
        if (pInNow > 0.0 && vLink > 1e-9)
        {
            var pFromA = eff * pInNow;
            if (vLink * iCharge > pFromA) iCharge = pFromA / vLink;   // A-limited ⇒ sag
        }
        if (iCharge < 0.0) iCharge = 0.0;   // a converter sources only; it cannot back-feed A

        rtB.Circuit.SetCurrentSource(rt.BChargeSource, iCharge);
        rtA.Circuit.SetCurrentSource(rt.AStamp, rt.ISmooth);
        rt.LastICharge = iCharge; rt.LastIA = rt.ISmooth;
    }

    // Integrate the boundary ledger for one substep and CLAMP the RECORDED OutJ so the
    // public books close (SurplusJ ≥ 0, HeatDumpedJ ≥ 0, Residual ≈ 0 — LedgerOf). Per the
    // 2026-07-06 ruling the clamp is BOOKKEEPING ONLY: it records, it does not license.
    // Physical conservation is enforced separately and honestly — the transformer debt
    // droop (ExchangeTransformer) and the sagging DC-link cap (ExchangeConverter) — and is
    // audited by the windowed physical audit from public readbacks, never by Residual.
    private static void AccrueLedger(CouplerRuntime rt, double dt, double deliveredByA, double deliveredToB, double modeledLoss)
    {
        rt.InJ += deliveredByA * dt;
        rt.OutJ += deliveredToB * dt;
        rt.ModeledLossJ += modeledLoss * dt;
        var cap = rt.InJ - rt.ModeledLossJ;
        if (rt.OutJ > cap) rt.OutJ = cap;   // recorded OutJ closes the books; physics is enforced upstream
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
            if (islandSlot == islB) { rt.BVStamped = false; rt.BChargeStamped = false; rt.DcLinkStamped = false; rt.BLocalPos = rt.BLocalNeg = -1; }

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

            // B side (secondary / output). TRANSFORMER: a voltage source at the (drooped)
            // transformed amplitude. CONVERTER: the B port IS the DC-link cap (a BE
            // companion) driven by the charge-controller current source — NO ideal setpoint
            // source (api.md §7 conservation ruling, 2026-07-06).
            if (islandSlot == islB
                && _nIsland[_kBNeg[k]] == islB && _nRtSlot[_kBPos[k]] == islB)
            {
                var bPos = _nCircuitNode[_kBPos[k]];
                var bNeg = _nCircuitNode[_kBNeg[k]];
                rt.BLocalPos = bPos; rt.BLocalNeg = bNeg;

                if (_kSpec[k].Kind == CouplerSpec.Family.ConverterTwoPort)
                {
                    // Charge-controller current source (+ injects into BPos from BNeg).
                    rt.BChargeSource = circuit.AddCurrentSource(bNeg, bPos, rt.LastICharge);
                    rt.BChargeStamped = true;

                    // The DC-link cap, stamped ALWAYS on the transient (companion) build so
                    // the port has real storage that sags honestly. At the pure DC operating
                    // point (companions=false) the cap is open — init only, before Close.
                    if (companions)
                    {
                        var cEff = EffectiveDcLinkFarads(k);
                        var g = cEff / substepDt;
                        var s = circuit.AddCompanion(bPos, bNeg);
                        circuit.SetCompanion(s, g, g * rt.DcLinkVPrev);
                        rt.DcLink = s; rt.DcLinkStamped = true; rt.DcLinkG = g;
                    }
                }
                else
                {
                    rt.BVSource = circuit.AddVoltageSource(bPos, bNeg, rt.LastVB);
                    rt.BVStamped = true;
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
            var iBridge = (vAPos - vBPos) / CouplerBridgeOhms;   // A→B positive when V(APos) > V(BPos)
            var vPort = vAPos - vANeg;
            var powerA2B = vPort * iBridge;
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
        // Residual is a CLOSURE IDENTITY, NOT a conservation signal (api.md §7 ruling,
        // 2026-07-06). HeatDumpedJ = ModeledLossJ + max(Surplus,0) with the AccrueLedger
        // clamp holding OutJ ≤ InJ − ModeledLossJ ⇒ SurplusJ ≥ 0 and Residual =
        // InJ − OutJ − HeatDumpedJ ≈ 0 BY CONSTRUCTION. It only audits the readback
        // arithmetic — it can never report a physical violation and must never be used to
        // do so. Whether the boundary actually conserved is a WINDOWED PHYSICAL AUDIT
        // (source energy = dissipation + Δstored + coupler heat, summed over both islands
        // from public node-voltage readbacks; see the conservation-audit tests). Physical
        // conservation itself is enforced upstream — the transformer debt droop and the
        // sagging DC-link cap — not by this clamp, which is bookkeeping. NOTE: the clamp
        // bounds the RECORDED OutJ; a relaxation-lag transient can still physically
        // over-deliver by O(one substep of lag) before the droop/sag debits it back.
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
