using System;
using System.Collections.Generic;

namespace Manatee.Core;

// Internal document machinery for Netlist: slot arenas, staging, atomic commit,
// union-find islanding, the Solve-time rebuild (generation reissue), readback,
// and the island/solution/meta seams the public structs delegate to.
public sealed partial class Netlist
{
    private const double Gmin = 1e-12;

    // ============================================================ slot arenas

    private void EnsureNodeCap(int min)
    {
        if (_nGen.Length >= min) return;
        var cap = Math.Max(min, _nGen.Length * 2);
        Array.Resize(ref _nGen, cap); Array.Resize(ref _nAlive, cap); Array.Resize(ref _nRole, cap);
        Array.Resize(ref _nPart, cap); Array.Resize(ref _nKey, cap); Array.Resize(ref _nIsland, cap);
        Array.Resize(ref _nDegree, cap); Array.Resize(ref _nInvalidSeq, cap); Array.Resize(ref _nInvalidKind, cap);
        EnsureNodeMapCap(cap);
    }

    private int ReserveNodeSlot()
    {
        int slot;
        if (_nFree.Count > 0) { slot = _nFree.Pop(); }
        else { slot = _nCount++; EnsureNodeCap(_nCount); _nGen[slot] = FirstGen; }
        _nRtSlot[slot] = -1;   // no runtime mapping until the island is built
        _nHeldPotential[slot] = 0.0;   // never a prior occupant's merge-held last-good value
        return slot;
    }

    private void FreeNodeSlot(int slot, TopologyEventKind inv, long seq)
    {
        _nAlive[slot] = false; _nGen[slot]++; _nInvalidSeq[slot] = seq; _nInvalidKind[slot] = (byte)inv;
        _nFree.Push(slot);
    }

    private void EnsureCompCap(int min)
    {
        if (_cGen.Length >= min) return;
        var cap = Math.Max(min, _cGen.Length * 2);
        Array.Resize(ref _cGen, cap); Array.Resize(ref _cAlive, cap); Array.Resize(ref _cKind, cap);
        Array.Resize(ref _cA, cap); Array.Resize(ref _cB, cap); Array.Resize(ref _cC, cap); Array.Resize(ref _cD, cap);
        Array.Resize(ref _cValue, cap); Array.Resize(ref _cStateVar, cap); Array.Resize(ref _cStatePrev, cap);
        Array.Resize(ref _cKey, cap); Array.Resize(ref _cState, cap);
        Array.Resize(ref _cDiode, cap); Array.Resize(ref _cSine, cap); Array.Resize(ref _cIsSine, cap);
        Array.Resize(ref _cLimits, cap); Array.Resize(ref _cInvalidSeq, cap); Array.Resize(ref _cInvalidKind, cap);
        EnsureStampCap(cap);
        EnsureLimitCap(cap);
    }

    private int ReserveCompSlot()
    {
        int slot;
        if (_cFree.Count > 0) { slot = _cFree.Pop(); }
        else { slot = _cCount++; EnsureCompCap(_cCount); _cGen[slot] = FirstGen; }
        // Evolved limit state is slot-parallel, not staged by the Add verbs: a slot
        // reused from the free list would otherwise hand the NEW component the removed
        // one's melting integral / trip latch / thermal envelope (phase-9 audit fix) —
        // or its same-tick instantaneous-event coalescing stamp (§12).
        _cI2t[slot] = 0.0; _cI2tTripped[slot] = false; _cEnvCount[slot] = 0;
        _cHeldFlow[slot] = 0.0;   // never a prior occupant's merge-held aux flow
        for (var k = 0; k < CoalescedKinds; k++)
        { _limSeenTick[slot * CoalescedKinds + k] = -1; _limRingPos[slot * CoalescedKinds + k] = 0; }
        return slot;
    }

    private void FreeCompSlot(int slot, TopologyEventKind inv, long seq)
    {
        _cAlive[slot] = false; _cGen[slot]++; _cInvalidSeq[slot] = seq; _cInvalidKind[slot] = (byte)inv;
        _cFree.Push(slot);
    }

    // Coupler snapshot identity: the CLIENT-passed StateKey (api.md §14). Declared here
    // (not in the Netlist.cs SoA block) alongside its staging/resize plumbing.
    private StateKey[] _kState = new StateKey[4];

    private void EnsureCouplerCap(int min)
    {
        if (_kGen.Length >= min) return;
        var cap = Math.Max(min, _kGen.Length * 2);
        Array.Resize(ref _kGen, cap); Array.Resize(ref _kAlive, cap); Array.Resize(ref _kSpec, cap);
        Array.Resize(ref _kStateA, cap); Array.Resize(ref _kAPos, cap); Array.Resize(ref _kANeg, cap);
        Array.Resize(ref _kBPos, cap); Array.Resize(ref _kBNeg, cap); Array.Resize(ref _kKey, cap);
        Array.Resize(ref _kState, cap);
    }

    private int ReserveCouplerSlot()
    {
        int slot;
        if (_kFree.Count > 0) { slot = _kFree.Pop(); }
        else { slot = _kCount++; EnsureCouplerCap(_kCount); _kGen[slot] = FirstGen; }
        _kAlive[slot] = true;
        return slot;
    }

    private void FreeCouplerSlot(int slot)
    {
        _kAlive[slot] = false; _kGen[slot]++; _kFree.Push(slot);
        // Drop the exchange runtime so a reused slot starts with a fresh ledger /
        // relaxation state rather than inheriting the removed coupler's accumulators.
        if (slot < _kRuntime.Length) _kRuntime[slot] = null;
        _unitsDirty = true;
    }

    private void EnsureProbeCap(int min)
    {
        if (_pGen.Length >= min) return;
        var cap = Math.Max(min, _pGen.Length * 2);
        Array.Resize(ref _pGen, cap); Array.Resize(ref _pAlive, cap); Array.Resize(ref _pA, cap);
        Array.Resize(ref _pB, cap); Array.Resize(ref _pT, cap); Array.Resize(ref _pKey, cap);
    }

    private int ReserveProbeSlot()
    {
        int slot;
        if (_pFree.Count > 0) { slot = _pFree.Pop(); }
        else { slot = _pCount++; EnsureProbeCap(_pCount); _pGen[slot] = FirstGen; }
        _pAlive[slot] = true;
        return slot;
    }

    private void FreeProbeSlot(int slot) { _pAlive[slot] = false; _pGen[slot]++; _pFree.Push(slot); }

    private void EnsureIslandCap(int min)
    {
        if (_iGen.Length >= min) return;
        var cap = Math.Max(min, _iGen.Length * 2);
        Array.Resize(ref _iGen, cap); Array.Resize(ref _iAlive, cap); Array.Resize(ref _iStatus, cap);
        Array.Resize(ref _iNeedsRebuild, cap); Array.Resize(ref _iNodeCount, cap);
        EnsureRuntimeIslandCap(cap);
        EnsureLimitEvalTickCap(cap);   // keep the per-substep limit stamp off the alloc path
    }

    private int ReserveIslandSlot()
    {
        int slot;
        if (_iFree.Count > 0) { slot = _iFree.Pop(); }
        else { slot = _iSlotCount++; EnsureIslandCap(_iSlotCount); _iGen[slot] = FirstGen; }
        _iAlive[slot] = true; _iStatus[slot] = (byte)IslandStatus.Dirty; _iNeedsRebuild[slot] = false; _iNodeCount[slot] = 0;
        _iSubstepN[slot] = 0; _iSubstepDt[slot] = 0.0; _iSubstepRawRef[slot] = 0.0;   // fresh subcycle plan
        _iAliveCount++; _idsDirty = true; _unitsDirty = true;
        return slot;
    }

    private void FreeIslandSlot(int slot)
    {
        _iAlive[slot] = false; _iGen[slot]++; _iAliveCount--; _iFree.Push(slot); _idsDirty = true; _unitsDirty = true;
        _iRuntime[slot] = null; _iRuntimeStale[slot] = false; _iFault[slot] = default;   // drop the numeric runtime
    }

    // ============================================================ resolve

    private bool ResolveComp(in ComponentRef c, out int slot)
    {
        if (c.Net == _netId && (uint)c.Slot < (uint)_cCount && _cAlive[c.Slot]
            && _cGen[c.Slot] == c.Gen && (byte)c.Kind == _cKind[c.Slot])
        {
            slot = c.Slot; return true;
        }
        slot = -1; OnStaleComp(c); return false;
    }

    private bool ProbeComp(in ComponentRef c, out int slot)
    {
        if (c.Net == _netId && (uint)c.Slot < (uint)_cCount && _cAlive[c.Slot] && _cGen[c.Slot] == c.Gen)
        {
            slot = c.Slot; return true;
        }
        slot = -1; return false;
    }

    private bool ResolveNode(NodeId n, out int slot)
    {
        if (n.Net == _netId && (uint)n.Slot < (uint)_nCount && _nAlive[n.Slot] && _nGen[n.Slot] == n.Gen)
        {
            slot = n.Slot; return true;
        }
        slot = -1; OnStaleNode(n); return false;
    }

    private bool ResolveCoupler(CouplerId id, out int slot)
    {
        if (id.Net == _netId && (uint)id.Slot < (uint)_kCount && _kAlive[id.Slot] && _kGen[id.Slot] == id.Gen)
        {
            slot = id.Slot; return true;
        }
        slot = -1;
        if (_debug)
        {
            if (_editActive) _editFaulted = true;   // abort the batch on Dispose, don't commit it partial
            var live = (uint)id.Slot < (uint)_kCount ? _kGen[id.Slot] : 0u;
            throw new StaleHandleException((ComponentKind)0, id.Slot, id.Gen, live, -1, nameof(StructuralEdit.RemoveCoupler));
        }
        _lastTickStats.StaleHandleReads++;
        return false;
    }

    private bool ProbeCoupler(CouplerId id, out int slot)
    {
        if (id.Net == _netId && (uint)id.Slot < (uint)_kCount && _kAlive[id.Slot] && _kGen[id.Slot] == id.Gen)
        {
            slot = id.Slot; return true;
        }
        slot = -1; return false;
    }

    private void OnStaleComp(in ComponentRef c)
    {
        if (_debug)
        {
            if (_editActive) _editFaulted = true;   // abort the batch on Dispose, don't commit it partial
            var live = (uint)c.Slot < (uint)_cCount ? _cGen[c.Slot] : 0u;
            var seq = (uint)c.Slot < (uint)_cCount ? _cInvalidSeq[c.Slot] : -1;
            var kind = (uint)c.Slot < (uint)_cCount ? (TopologyEventKind)_cInvalidKind[c.Slot] : TopologyEventKind.ComponentRemoved;
            throw new StaleHandleException(c.Kind, c.Slot, c.Gen, live, seq, kind.ToString());
        }
        _lastTickStats.StaleHandleReads++;
    }

    private void OnStaleNode(NodeId n)
    {
        if (_debug)
        {
            if (_editActive) _editFaulted = true;   // abort the batch on Dispose, don't commit it partial
            var live = (uint)n.Slot < (uint)_nCount ? _nGen[n.Slot] : 0u;
            var seq = (uint)n.Slot < (uint)_nCount ? _nInvalidSeq[n.Slot] : -1;
            var kind = (uint)n.Slot < (uint)_nCount ? (TopologyEventKind)_nInvalidKind[n.Slot] : TopologyEventKind.NodeRemoved;
            throw new StaleHandleException((ComponentKind)0, n.Slot, n.Gen, live, seq, kind.ToString());
        }
        _lastTickStats.StaleHandleReads++;
    }

    private int RequireNode(NodeId n)
        => ResolveNode(n, out var slot) ? slot : ((uint)n.Slot < (uint)_nCount ? n.Slot : 0);

    // ============================================================ staging

    internal NodeId StageAddNode(StructuralEdit e, in ExternalKey key, NodeRole role, PartitionKey part)
    {
        var slot = ReserveNodeSlot();
        _nAlive[slot] = true; _nKey[slot] = key; _nRole[slot] = (byte)role; _nPart[slot] = part.Value;
        _nIsland[slot] = -1; _nDegree[slot] = 0;
        e.AddedNodes.Add(slot);
        return new NodeId(slot, _nGen[slot], _netId);
    }

    internal void StageMarkReference(NodeId n)
    {
        if (ResolveNode(n, out var slot)) _nRole[slot] = (byte)NodeRole.Reference;
    }

    internal void StageSetPartition(NodeId n, PartitionKey p)
    {
        if (ResolveNode(n, out var slot)) _nPart[slot] = p.Value;
    }

    internal int StageAddTwoTerminal(StructuralEdit e, ComponentKind kind, NodeId a, NodeId b, double value,
                                     in ExternalKey key, in StateKey state, in LimitSpec limits)
    {
        // Source-return wiring is derived from `kind` (VSource) at build time in
        // StampWiring — there is no per-add "is this the source negative" flag.
        var sa = RequireNode(a); var sb = RequireNode(b);
        var slot = ReserveCompSlot();
        _cAlive[slot] = true; _cKind[slot] = (byte)kind; _cA[slot] = sa; _cB[slot] = sb; _cC[slot] = -1; _cD[slot] = -1;
        _cValue[slot] = value; _cKey[slot] = key; _cState[slot] = state; _cIsSine[slot] = false; _cLimits[slot] = limits;
        _cStateVar[slot] = 0.0; _cStatePrev[slot] = 0.0;   // storage: cap V / inductor I start at 0 (uic; §14)
        e.AddedComponents.Add(slot);
        return slot;
    }

    internal int StageAddDiode(StructuralEdit e, NodeId anode, NodeId cathode, in DiodeParams p, in ExternalKey key)
    {
        var sa = RequireNode(anode); var sb = RequireNode(cathode);
        var slot = ReserveCompSlot();
        _cAlive[slot] = true; _cKind[slot] = (byte)ComponentKind.Diode; _cA[slot] = sa; _cB[slot] = sb; _cC[slot] = -1; _cD[slot] = -1;
        _cValue[slot] = 0; _cKey[slot] = key; _cState[slot] = StateKey.From(key); _cIsSine[slot] = false; _cDiode[slot] = p;
        e.AddedComponents.Add(slot);
        return slot;
    }

    internal int StageAddSine(StructuralEdit e, NodeId pos, NodeId neg, in SineDrive d, in ExternalKey key, in StateKey state)
    {
        // A sine source in a NON-Mixed profile is legal but single-sampled per tick
        // (no ≥20-samples/cycle guarantee) — canon ruled it accepted+warned, same
        // family as the floating-Return footgun (api.md §5 note, 2026-07-06).
        if (_debug && _profileKind != SolverProfile.Regime.Mixed)
            System.Diagnostics.Debug.WriteLine(
                $"AddSineSource in a non-Mixed profile ({_profileKind}): the source is single-sampled " +
                "per tick (heavily undersampled — deterministic and phase-wrapped, but no subcycling). " +
                "Use SolverProfile.Mixed for AC islands (api.md §5).");
        var sa = RequireNode(pos); var sb = RequireNode(neg);
        var slot = ReserveCompSlot();
        _cAlive[slot] = true; _cKind[slot] = (byte)ComponentKind.VSource; _cA[slot] = sa; _cB[slot] = sb; _cC[slot] = -1; _cD[slot] = -1;
        _cValue[slot] = d.AmplitudeV; _cKey[slot] = key; _cState[slot] = state; _cIsSine[slot] = true; _cSine[slot] = d;
        // Phase accumulator seeded at the drive's phase offset (normalized to [0, 2π)
        // — sin-preserving), advanced ω·dt per substep and carried across ticks and N
        // changes (phase-continuous, §4).
        var ph0 = d.PhaseRad - TwoPi * Math.Floor(d.PhaseRad / TwoPi);
        _cStateVar[slot] = ph0; _cStatePrev[slot] = ph0;
        e.AddedComponents.Add(slot);
        return slot;
    }

    internal int StageAddTransformer(StructuralEdit e, NodeId aPos, NodeId aNeg, NodeId bPos, NodeId bNeg,
                                     double turnsRatio, in ExternalKey key)
    {
        var sap = RequireNode(aPos); var san = RequireNode(aNeg);
        var sbp = RequireNode(bPos); var sbn = RequireNode(bNeg);
        var slot = ReserveCompSlot();
        _cAlive[slot] = true; _cKind[slot] = (byte)ComponentKind.IdealTransformer;
        _cA[slot] = sap; _cB[slot] = san; _cC[slot] = sbp; _cD[slot] = sbn;
        _cValue[slot] = turnsRatio; _cKey[slot] = key; _cState[slot] = StateKey.From(key); _cIsSine[slot] = false;
        e.AddedComponents.Add(slot);
        return slot;
    }

    internal CouplerId StageAddCoupler(StructuralEdit e, in CouplerSpec spec, in CouplerPorts ports,
                                       in ExternalKey key, in StateKey state)
    {
        var slot = ReserveCouplerSlot();
        _kSpec[slot] = spec; _kKey[slot] = key;
        // Coupler state units key on the CLIENT-passed StateKey (api.md §14) — a
        // default(StateKey) falls back to the ExternalKey-derived identity so the
        // snapshot stream never carries an all-zero key.
        _kState[slot] = state == default ? StateKey.From(key) : state;
        _kStateA[slot] = spec.IsGalvanic ? CouplerState.Closed : CouplerState.Open;  // breaker default closed
        _kAPos[slot] = RequireNode(ports.APos); _kANeg[slot] = RequireNode(ports.ANeg);
        _kBPos[slot] = RequireNode(ports.BPos); _kBNeg[slot] = RequireNode(ports.BNeg);
        e.AddedCouplers.Add(slot);
        return new CouplerId(slot, _kGen[slot], _netId);
    }

    internal void StageRemoveComponent(StructuralEdit e, in ComponentRef c)
    {
        if (ResolveComp(c, out var slot)) e.RemovedComponents.Add(slot);
    }

    internal void StageRemoveNode(StructuralEdit e, NodeId n)
    {
        if (ResolveNode(n, out var slot)) e.RemovedNodes.Add(slot);
    }

    internal void StageRemoveCoupler(StructuralEdit e, CouplerId id)
    {
        if (ResolveCoupler(id, out var slot)) e.RemovedCouplers.Add(slot);
    }

    internal ProbeId StageAddProbe(StructuralEdit e, NodeId a, NodeId b, double t, in ExternalKey key)
    {
        var sa = RequireNode(a); var sb = RequireNode(b);
        var slot = ReserveProbeSlot();
        _pA[slot] = sa; _pB[slot] = sb; _pT[slot] = t; _pKey[slot] = key;
        e.AddedProbes.Add(slot);
        return new ProbeId(slot, _pGen[slot], _netId);
    }

    // ============================================================ commit / abort

    internal void AbortEdit(StructuralEdit e)
    {
        RollbackAdds(e);
        _editActive = false;
    }

    // Consume the "a staging call threw inside this open edit" flag (api.md §6):
    // Dispose reads it to Abort instead of committing a partial batch.
    internal bool TakeEditFaulted()
    {
        var f = _editFaulted;
        _editFaulted = false;
        return f;
    }

    private void RollbackAdds(StructuralEdit e)
    {
        // Added slots were reserved and written to the SoA but never registered
        // (no key map / island / journal), so freeing them restores the document
        // to its pre-edit shape (api.md §6 atomic-rollback).
        foreach (var slot in e.AddedComponents) { _cAlive[slot] = false; _cGen[slot]++; _cFree.Push(slot); }
        foreach (var slot in e.AddedNodes) { _nAlive[slot] = false; _nGen[slot]++; _nFree.Push(slot); }
        foreach (var slot in e.AddedCouplers) { _kAlive[slot] = false; _kGen[slot]++; _kFree.Push(slot); }
        foreach (var slot in e.AddedProbes) { _pAlive[slot] = false; _pGen[slot]++; _pFree.Push(slot); }
    }

    internal EditReceipt CommitEdit(StructuralEdit e)
    {
        BeginTick();

        // ---- Phase A: validate (pure; no semantic mutation yet) ----
        Validate(e);   // throws + rolls back on failure

        // ---- Phase B: apply (cannot fail) ----
        var jFrom = _journal.Head;

        foreach (var slot in e.AddedNodes)
        {
            RegisterKey(_nKeyMap, _nKey[slot], slot);
            var isl = ReserveIslandSlot();
            _nIsland[slot] = isl; _iNodeCount[isl] = 1;
            var iid = new IslandId(isl, _iGen[isl], _netId);
            _journal.Append(TopologyEventKind.IslandCreated, default, _nKey[slot], iid, default);
            RecordChange(IslandChangeKind.Created, iid, default);
            _journal.Append(TopologyEventKind.NodeAdded, default, _nKey[slot], iid, default);
        }

        foreach (var slot in e.AddedComponents)
        {
            RegisterKey(_cKeyMap, _cKey[slot], slot);
            IncDeg(_cA[slot]); IncDeg(_cB[slot]);
            if (_cC[slot] >= 0) IncDeg(_cC[slot]);
            if (_cD[slot] >= 0) IncDeg(_cD[slot]);
            UnionEndpoints(slot);
            var isl = _nIsland[_cA[slot]];
            var iid = new IslandId(isl, _iGen[isl], _netId);
            var kind = (ComponentKind)_cKind[slot];
            _journal.Append(TopologyEventKind.ComponentAdded,
                new ComponentRef(kind, slot, _cGen[slot], _netId), _cKey[slot], iid, default);
            MarkIslandDirty(isl);
        }

        foreach (var slot in e.AddedCouplers)
        {
            RegisterKey(_kKeyMap, _kKey[slot], slot);
            // A boundary coupler's exchange runtime is created AT COMMIT (shape-time),
            // not lazily at the first Solve: its state unit must exist from the moment
            // the coupler does, so SnapshotSize/StateUnitCount depend only on document
            // state and stay stable between IslandChanges (api.md §11/§14; decision
            // log #15). The matrix stamps inside it are resolved by the next
            // StampBoundaryCouplers as before.
            if (!_kSpec[slot].IsGalvanic) EnsureCouplerRuntime(slot);
            if (_kSpec[slot].IsGalvanic && _kStateA[slot] == CouplerState.Closed)
            {
                UnionNodes(_kAPos[slot], _kBPos[slot]);
                UnionNodes(_kANeg[slot], _kBNeg[slot]);
            }
        }

        foreach (var slot in e.AddedProbes)
        {
            RegisterKey(_pKeyMap, _pKey[slot], slot);
            var isl = _nIsland[_pA[slot]];
            var iid = isl >= 0 ? new IslandId(isl, _iGen[isl], _netId) : default;
            _journal.Append(TopologyEventKind.ProbeAdded, default, _pKey[slot], iid, default);
        }

        foreach (var slot in e.RemovedComponents)
        {
            var isl = _nIsland[_cA[slot]];
            DecDeg(_cA[slot]); DecDeg(_cB[slot]);
            if (_cC[slot] >= 0) DecDeg(_cC[slot]);
            if (_cD[slot] >= 0) DecDeg(_cD[slot]);
            _cKeyMap.Remove(_cKey[slot]);
            var iid = new IslandId(isl, _iGen[isl], _netId);
            var kind = (ComponentKind)_cKind[slot];
            var seq = _journal.Append(TopologyEventKind.ComponentRemoved,
                new ComponentRef(kind, slot, _cGen[slot], _netId), _cKey[slot], iid, default);
            FreeCompSlot(slot, TopologyEventKind.ComponentRemoved, seq);
            MarkIslandNeedsRebuild(isl);
        }

        foreach (var slot in e.RemovedNodes)
        {
            var isl = _nIsland[slot];
            _nKeyMap.Remove(_nKey[slot]);
            var iid = new IslandId(isl, _iGen[isl], _netId);
            var seq = _journal.Append(TopologyEventKind.NodeRemoved, default, _nKey[slot], iid, default);
            _iNodeCount[isl]--;
            // Probes are observers, not topology — they do not hold the node alive.
            // Invalidate any aim at the removed node to a dangling (-1) endpoint that
            // reads 0 until re-aimed (Meta.SetProbeInterpolation), so the freed slot's
            // reuse can never alias the probe onto the new occupant (§13/§20).
            for (var p = 0; p < _pCount; p++)
            {
                if (!_pAlive[p]) continue;
                if (_pA[p] == slot) _pA[p] = -1;
                if (_pB[p] == slot) _pB[p] = -1;
            }
            FreeNodeSlot(slot, TopologyEventKind.NodeRemoved, seq);
            if (_iNodeCount[isl] <= 0)
            {
                _journal.Append(TopologyEventKind.IslandRemoved, default, default, iid, default);
                RecordChange(IslandChangeKind.Removed, iid, default);
                FreeIslandSlot(isl);
            }
            else MarkIslandNeedsRebuild(isl);
        }

        foreach (var slot in e.RemovedCouplers)
        {
            _kKeyMap.Remove(_kKey[slot]);
            var wasBridge = _kSpec[slot].IsGalvanic && _kStateA[slot] == CouplerState.Closed;
            var isl = wasBridge ? _nIsland[_kAPos[slot]] : -1;
            FreeCouplerSlot(slot);
            if (wasBridge && isl >= 0) MarkIslandNeedsRebuild(isl);
        }

        foreach (var slot in e.RemovedProbes)
        {
            _pKeyMap.Remove(_pKey[slot]);
            _journal.Append(TopologyEventKind.ProbeRemoved, default, _pKey[slot], default, default);
            FreeProbeSlot(slot);
        }

        var jTo = _journal.Head;
        _idsDirty = true; _unitsDirty = true;
        _editActive = false;

        var lapped = jTo - jFrom > _journal.Capacity;
        var estDim = e.AddedNodes.Count + e.AddedComponents.Count;
        return new EditReceipt(jFrom, jTo, e.AddedNodes.Count, e.AddedComponents.Count, e.RemovedComponents.Count,
            CountDirtyIslands(), estDim, estDim, lapped);
    }

    private void Validate(StructuralEdit e)
    {
        // RemoveNode must be degree-0 at commit (final degree over the batch).
        foreach (var rn in e.RemovedNodes)
        {
            var deg = _nDegree[rn];
            foreach (var ac in e.AddedComponents) deg += EndpointCount(ac, rn);
            foreach (var rc in e.RemovedComponents) deg -= EndpointCount(rc, rn);
            if (deg != 0)
            {
                RollbackAdds(e); _editActive = false;
                throw new InvalidOperationException(
                    $"RemoveNode: node slot {rn} has degree {deg} at commit (must be 0). The edit was rolled back.");
            }

            // Coupler ports do not contribute to _nDegree, so degree-0 alone would let
            // a port node be freed — and the slot's later reuse silently ALIASES the
            // dangling port onto the new occupant: ApplyReconfigure/RebuildIsland then
            // union the wrong nodes and corrupt the islanding union-find itself. Per
            // the §20 RemoveNode row the whole batch aborts, unless the referencing
            // coupler is removed in the SAME batch. (Probes are observers, not
            // topology: their dangling aims are invalidated at apply time instead —
            // see CommitEdit — so they read 0 until re-aimed, never alias.)
            for (var k = 0; k < _kCount; k++)
            {
                if (!_kAlive[k] || e.RemovedCouplers.Contains(k)) continue;
                if (_kAPos[k] == rn || _kANeg[k] == rn || _kBPos[k] == rn || _kBNeg[k] == rn)
                {
                    RollbackAdds(e); _editActive = false;
                    throw new InvalidOperationException(
                        $"RemoveNode: node slot {rn} is a port of live coupler slot {k} " +
                        "(remove the coupler in the same batch first). The edit was rolled back.");
                }
            }
        }

        // ClientPartitioned: no non-coupler edge may merge two distinct partitions.
        if (_opts.Partitioning == PartitioningMode.ClientPartitioned)
            ValidatePartitions(e);
    }

    private void ValidatePartitions(StructuralEdit e)
    {
        var parent = new Dictionary<int, int>();
        var part = new Dictionary<int, ulong>();
        var removed = new HashSet<int>(e.RemovedComponents);

        void Seed(int n)
        {
            if (!parent.ContainsKey(n)) { parent[n] = n; part[n] = _nPart[n]; }
        }
        int Find(int x) { while (parent[x] != x) { parent[x] = parent[parent[x]]; x = parent[x]; } return x; }
        void Union(int a, int b)
        {
            Seed(a); Seed(b);
            int ra = Find(a), rb = Find(b);
            if (ra == rb) return;
            ulong pa = part[ra], pb = part[rb];
            if (pa != 0 && pb != 0 && pa != pb)
            {
                RollbackAdds(e); _editActive = false;
                throw new PartitionMergeException(new PartitionKey(pa), new PartitionKey(pb));
            }
            parent[rb] = ra; part[ra] = pa != 0 ? pa : pb;
        }
        void UnionComp(int c)
        {
            int a = _cA[c], b = _cB[c], cc = _cC[c], dd = _cD[c];
            Union(a, b);
            if (cc >= 0) Union(a, cc);
            if (dd >= 0) Union(a, dd);
        }

        for (var c = 0; c < _cCount; c++)
            if (_cAlive[c] && !removed.Contains(c)) UnionComp(c);
        foreach (var c in e.AddedComponents) UnionComp(c);
    }

    private int EndpointCount(int comp, int node)
    {
        var n = 0;
        if (_cA[comp] == node) n++;
        if (_cB[comp] == node) n++;
        if (_cC[comp] == node) n++;
        if (_cD[comp] == node) n++;
        return n;
    }

    private void RegisterKey(Dictionary<ExternalKey, int> map, in ExternalKey key, int slot)
    {
        if (_debug && map.ContainsKey(key))
            throw new InvalidOperationException($"Duplicate ExternalKey ({key.Hi:X}:{key.Lo:X}) — keys must be unique per netlist.");
        map[key] = slot;
    }

    private void IncDeg(int node) => _nDegree[node]++;
    private void DecDeg(int node) => _nDegree[node]--;

    // ============================================================ islanding

    private void UnionEndpoints(int comp)
    {
        int a = _cA[comp], b = _cB[comp], c = _cC[comp], d = _cD[comp];
        UnionNodes(a, b);
        if (c >= 0) UnionNodes(a, c);
        if (d >= 0) UnionNodes(a, d);
    }

    // The LIVE islanding union: eager relabel-all-members (O(_nCount) per union),
    // deliberately distinct from the transient path-halving scratch union-finds in
    // ValidatePartitions and RebuildIsland — it maintains the persistent _nIsland map
    // and emits merge journal/change events, so the two styles are not accidental
    // divergence.
    private void UnionNodes(int a, int b)
    {
        int ia = _nIsland[a], ib = _nIsland[b];
        if (ia == ib) return;
        if (_iNodeCount[ia] < _iNodeCount[ib]) { (ia, ib) = (ib, ia); }
        // Last-good capture for the ABSORBED side (api.md §17 rule 4, fixed
        // 2026-07-07): the absorbed island's Circuit dies with FreeIslandSlot below,
        // but its nodes' and voltage sources' handles survive the merge — they must
        // keep reading their pre-merge published potentials and aux flows (branch
        // currents), not 0.0, until the merged island first publishes. Capture
        // BEFORE relabeling, through the UNGATED last-good readers, so (a) a node or
        // source whose mapping is already stale (a prior merge this same tick)
        // simply re-captures its own held value — chained merges compose — and (b) a
        // Faulted absorbed island carries its last successfully PUBLISHED vector, or
        // 0 if it never published: the merge is the tier-2/3 change that flips
        // Faulted→Dirty (state machine, api.md §11), and Dirty reads last-good on
        // BOTH union orientations — the de-energized window is scoped to the Faulted
        // STATUS (gated in NodePotential/BranchCurrent), never baked into captures,
        // so which side survives stays unobservable. No fault output can be
        // laundered either way: failed solves never publish (Circuit.Solve holds the
        // front buffer; the Newton driver restores its pre-iteration capture). The
        // writes are slot-parallel array stores: no allocation, deterministic order.
        for (var c = 0; c < _cCount; c++)
            if (_cAlive[c] && (ComponentKind)_cKind[c] == ComponentKind.VSource && _nIsland[_cA[c]] == ib)
                _cHeldFlow[c] = VSourceFlowLastGood(c, ib);
        for (var n = 0; n < _nCount; n++)
            if (_nAlive[n] && _nIsland[n] == ib)
            {
                _nHeldPotential[n] = NodePotentialLastGood(n, ib);
                _nIsland[n] = ia;
            }
        _iNodeCount[ia] += _iNodeCount[ib];
        // Carry a pending split-rebuild across the merge: if the absorbed island
        // was scheduled to recompute connectivity (a removal), the survivor must
        // inherit that obligation, else the split would be silently dropped.
        if (_iNeedsRebuild[ib]) _iNeedsRebuild[ia] = true;
        var survivor = new IslandId(ia, _iGen[ia], _netId);
        var absorbed = new IslandId(ib, _iGen[ib], _netId);
        FreeIslandSlot(ib);
        _journal.Append(TopologyEventKind.IslandsMerged, default, default, survivor, absorbed);
        RecordChange(IslandChangeKind.Merged, survivor, absorbed);
        // Membership grew: the survivor's Circuit no longer covers all members, so
        // it is rebuilt numerically at the next Solve (rebuild supersedes merge if
        // a removal also scheduled a rebuild — §17). One tick's merges are counted.
        _iRuntimeStale[ia] = true;
        _lastTickStats.MergesApplied++;
        MarkIslandDirty(ia);
    }

    private void MarkIslandDirty(int isl)
    {
        _iStatus[isl] = (byte)IslandStatus.Dirty;
    }

    private void MarkIslandNeedsRebuild(int isl)
    {
        _iNeedsRebuild[isl] = true;
        _iStatus[isl] = (byte)IslandStatus.Dirty;
        _idsDirty = true; _unitsDirty = true;
    }

    private void MarkComponentDirty(int slot)
    {
        var isl = _nIsland[_cA[slot]];
        if (isl >= 0) MarkIslandDirty(isl);
    }

    /// <summary>Solve-time rebuild of one Dirty-for-rebuild island: recompute
    /// connectivity of its member nodes, reissue the generation of every
    /// island-rooted handle (component + node slots), mint the new island id(s),
    /// and emit one IslandRebuilt per resulting island (api.md §16, §17).</summary>
    private void RebuildIsland(int slot)
    {
        var members = new List<int>();
        for (var n = 0; n < _nCount; n++)
            if (_nAlive[n] && _nIsland[n] == slot) members.Add(n);

        var oldId = new IslandId(slot, _iGen[slot], _netId);
        if (members.Count == 0)
        {
            _journal.Append(TopologyEventKind.IslandRemoved, default, default, oldId, default);
            RecordChange(IslandChangeKind.Removed, oldId, default);
            FreeIslandSlot(slot);
            return;
        }

        var memberSet = new HashSet<int>(members);
        var parent = new Dictionary<int, int>();
        foreach (var n in members) parent[n] = n;
        int Find(int x) { while (parent[x] != x) { parent[x] = parent[parent[x]]; x = parent[x]; } return x; }
        void Uni(int a, int b) { int ra = Find(a), rb = Find(b); if (ra != rb) parent[rb] = ra; }

        for (var c = 0; c < _cCount; c++)
        {
            if (!_cAlive[c] || !memberSet.Contains(_cA[c])) continue;
            Uni(_cA[c], _cB[c]);
            if (_cC[c] >= 0) Uni(_cA[c], _cC[c]);
            if (_cD[c] >= 0) Uni(_cA[c], _cD[c]);
        }
        for (var k = 0; k < _kCount; k++)
        {
            if (!_kAlive[k] || !_kSpec[k].IsGalvanic || _kStateA[k] != CouplerState.Closed) continue;
            if (memberSet.Contains(_kAPos[k]) && memberSet.Contains(_kBPos[k])) Uni(_kAPos[k], _kBPos[k]);
            if (memberSet.Contains(_kANeg[k]) && memberSet.Contains(_kBNeg[k])) Uni(_kANeg[k], _kBNeg[k]);
        }

        // `orderedRoots` fixes the group order deterministically: roots are appended
        // the first time they are seen while scanning `members` in ascending node-slot
        // order. Dictionary enumeration order must NEVER drive output (which group
        // keeps `slot`, and the emission order of IslandRebuilt journal / DrainChanges
        // events — both client-observable) — the project determinism rule and Re-Volt's
        // Mono runtime (no insertion-order guarantee) forbid it.
        var groups = new Dictionary<int, List<int>>();
        var orderedRoots = new List<int>();
        foreach (var n in members)
        {
            var r = Find(n);
            if (!groups.TryGetValue(r, out var l)) { l = new List<int>(); groups[r] = l; orderedRoots.Add(r); }
            l.Add(n);
        }

        var rootToSeq = new Dictionary<int, long>();
        var first = true;
        foreach (var root in orderedRoots)
        {
            var g = groups[root];
            int gslot; uint newGen;
            if (first) { gslot = slot; newGen = _iGen[slot] + 1; }
            else { gslot = ReserveIslandSlot(); newGen = _iGen[gslot]; }

            var newId = new IslandId(gslot, newGen, _netId);
            var seq = _journal.Append(TopologyEventKind.IslandRebuilt, default, default, oldId, newId);
            RecordChange(IslandChangeKind.Rebuilt, oldId, newId);
            rootToSeq[root] = seq;

            if (first) _iGen[slot] = newGen;
            _iAlive[gslot] = true; _iStatus[gslot] = (byte)IslandStatus.Dirty;
            _iNeedsRebuild[gslot] = false; _iNodeCount[gslot] = g.Count;
            _iRuntimeStale[gslot] = true;   // fresh Circuit built in the numeric pass
            _iFault[gslot] = default;

            foreach (var n in g)
            {
                _nIsland[n] = gslot; _nGen[n]++;
                _nInvalidSeq[n] = seq; _nInvalidKind[n] = (byte)TopologyEventKind.IslandRebuilt;
            }
            first = false;
        }

        for (var c = 0; c < _cCount; c++)
        {
            if (!_cAlive[c] || !memberSet.Contains(_cA[c])) continue;
            var seq = rootToSeq[Find(_cA[c])];
            _cGen[c]++; _cInvalidSeq[c] = seq; _cInvalidKind[c] = (byte)TopologyEventKind.IslandRebuilt;
        }

        _idsDirty = true; _unitsDirty = true;
    }

    private void ApplyReconfigure(CouplerId id, CouplerState state)
    {
        if (!ResolveCoupler(id, out var slot)) return;
        if (_kStateA[slot] == state) return;
        var galvanic = _kSpec[slot].IsGalvanic;
        _kStateA[slot] = state;
        _unitsDirty = true;   // exchange-active state changed; unit membership is existence-based but recheck is cheap
        if (!galvanic) return;   // boundary: exchange toggle only (the exchange itself runs in StepUnit)

        if (state == CouplerState.Closed)
        {
            UnionNodes(_kAPos[slot], _kBPos[slot]);
            UnionNodes(_kANeg[slot], _kBNeg[slot]);
        }
        else
        {
            var isl = _nIsland[_kAPos[slot]];
            if (isl >= 0) MarkIslandNeedsRebuild(isl);
        }
    }

    // ============================================================ ε-no-op gate

    private bool IsResistanceNoOp(double oldOhms, double newOhms)
    {
        double gOld = 1.0 / oldOhms, gNew = 1.0 / newOhms;
        bool oldLow = !(gOld > Gmin), newLow = !(gNew > Gmin);
        if (oldLow && newLow) return true;
        if (oldLow != newLow) return false;
        if (!(gOld > 0.0) || !(gNew > 0.0)) return false;
        var ratio = gNew / gOld;                       // one IEEE division + two compares (api.md §9)
        return ratio >= _ratioLo && ratio <= _ratioHi;
    }

    // ============================================================ change ring

    private void RecordChange(IslandChangeKind kind, IslandId a, IslandId b)
    {
        var idx = (int)(_chgHead % _chg.Length);
        _chg[idx] = new IslandChange { Kind = kind, A = a, B = b };
        _chgHead++;
        if (_chgCount < _chg.Length) _chgCount++;
        else _chgLost = true;
    }

    internal int DrainChanges(Span<IslandChange> into, out bool lost)
    {
        lost = _chgLost;
        var n = Math.Min(into.Length, _chgCount);
        var start = _chgHead - _chgCount;
        for (var i = 0; i < n; i++)
            into[i] = _chg[(int)((start + i) % _chg.Length)];
        _chgCount -= n;
        if (_chgCount == 0) _chgLost = false;
        return n;
    }

    // ============================================================ island seams

    internal int IslandCount => _iAliveCount;

    internal bool IslandGenLive(int slot, uint gen)
        => (uint)slot < (uint)_iSlotCount && _iAlive[slot] && _iGen[slot] == gen;

    internal IslandHandle IslandHandleByIndex(int i)
    {
        var k = 0;
        for (var s = 0; s < _iSlotCount; s++)
        {
            if (!_iAlive[s]) continue;
            if (k == i) return new IslandHandle(this, s, _iGen[s]);
            k++;
        }
        throw new ArgumentOutOfRangeException(nameof(i));
    }

    internal IslandHandle IslandHandleOfNode(NodeId n)
    {
        if (!ResolveNode(n, out var slot)) return default;
        var isl = _nIsland[slot];
        return new IslandHandle(this, isl, _iGen[isl]);
    }

    internal IslandHandle IslandHandleOfId(IslandId id) => new(this, id.Slot, id.Gen);

    // Status/Partition gen-check like IslandIsLive: a stale IslandId whose slot was
    // freed and reissued to a DIFFERENT island must read as Empty/None, never report
    // the new occupant's state (api.md §11 — Of(IslandId) is gen-checked).
    internal IslandStatus IslandStatusOf(int slot, uint gen)
        => (uint)slot < (uint)_iSlotCount && _iAlive[slot] && _iGen[slot] == gen
            ? (IslandStatus)_iStatus[slot] : IslandStatus.Empty;

    internal PartitionKey IslandPartitionOf(int slot, uint gen)
    {
        if ((uint)slot >= (uint)_iSlotCount || !_iAlive[slot] || _iGen[slot] != gen) return PartitionKey.None;
        var seen = false; ulong p = 0;
        for (var n = 0; n < _nCount; n++)
        {
            if (!_nAlive[n] || _nIsland[n] != slot) continue;
            var np = _nPart[n];
            if (np == 0) return PartitionKey.None;
            if (!seen) { p = np; seen = true; }
            else if (p != np) return PartitionKey.None;
        }
        return new PartitionKey(p);
    }

    internal void StepIsland(int slot, uint gen, in TickClock clock)
        => StepIslandNumeric(slot, gen, clock);

    private bool IslandHasSine(int slot)
    {
        for (var c = 0; c < _cCount; c++)
            if (_cAlive[c] && _cIsSine[c] && _nIsland[_cA[c]] == slot) return true;
        return false;
    }

    // Whether the island carries time-dependent content (a sine source or a
    // capacitor/inductor) and so must re-solve every tick even when its document is
    // untouched — the storage integrates / the sine advances (solver.md transient/AC).
    private bool IslandIsTransient(int slot)
    {
        for (var c = 0; c < _cCount; c++)
        {
            if (!_cAlive[c] || _nIsland[_cA[c]] != slot) continue;
            if (_cIsSine[c]) return true;
            var kind = (ComponentKind)_cKind[c];
            if (kind == ComponentKind.Capacitor || kind == ComponentKind.Inductor) return true;
        }
        return false;
    }

    internal FaultDiagnostic IslandFault(int slot, uint gen) => IslandFaultNumeric(slot, gen);

    internal SubstepPlan IslandPlan(int slot, uint gen) => IslandPlanNumeric(slot, gen);

    internal int DescribeFault(int slot, uint gen, Span<ComponentRef> comps, Span<NodeId> nodes)
        => DescribeFaultNumeric(slot, gen, comps, nodes);

    internal int DrainLimitEvents(int slot, uint gen, Span<LimitEvent> into)
        => DrainLimitEventsImpl(slot, gen, into);

    internal int DrainLimitEvents(int slot, uint gen, Span<LimitEvent> into, out long dropped)
        => DrainLimitEventsImpl(slot, gen, into, out dropped);

    internal InvariantReport CheckInvariants(int slot, uint gen, InvariantChecks which)
        => CheckInvariantsNumeric(slot, gen, which);

    internal int CollectDirty(Span<IslandHandle> into)
    {
        // One handle per SCHEDULING UNIT: boundary-coupled islands dedupe to their unit
        // lead (api.md §11). A unit is emitted once when ANY member is Dirty; the handle
        // is the lead (the only member the client may Step). 0B: preallocated dedupe
        // marks, no per-call allocation.
        EnsureUnitsFresh();
        _collectEpoch++;
        var n = 0;
        for (var s = 0; s < _iSlotCount && n < into.Length; s++)
        {
            if (!_iAlive[s] || _iStatus[s] != (byte)IslandStatus.Dirty) continue;
            var lead = _iUnitLead[s];
            if (lead < 0) lead = s;
            if (_iCollectMark[lead] == _collectEpoch) continue;   // unit already emitted
            _iCollectMark[lead] = _collectEpoch;
            into[n++] = new IslandHandle(this, lead, _iGen[lead]);
        }
        return n;
    }

    private int CountDirtyIslands()
    {
        var n = 0;
        for (var s = 0; s < _iSlotCount; s++)
            if (_iAlive[s] && _iStatus[s] == (byte)IslandStatus.Dirty) n++;
        return n;
    }

    internal ReadOnlySpan<IslandId> IslandIdsOrdered()
    {
        if (_idsDirty) RebuildIdsCache();
        return _idsCache.AsSpan(0, _idsCount);
    }

    private void RebuildIdsCache()
    {
        // Minimum node ExternalKey per island → rebuild-stable ordering (api.md §11).
        var minKey = new Dictionary<int, ExternalKey>();
        for (var n = 0; n < _nCount; n++)
        {
            if (!_nAlive[n]) continue;
            var isl = _nIsland[n];
            if (!minKey.TryGetValue(isl, out var mk) || KeyLess(_nKey[n], mk)) minKey[isl] = _nKey[n];
        }
        var slots = new List<int>();
        for (var s = 0; s < _iSlotCount; s++) if (_iAlive[s]) slots.Add(s);
        slots.Sort((x, y) =>
        {
            minKey.TryGetValue(x, out var kx); minKey.TryGetValue(y, out var ky);
            return KeyCompare(kx, ky);
        });
        if (_idsCache.Length < slots.Count) _idsCache = new IslandId[Math.Max(slots.Count, 4)];
        for (var i = 0; i < slots.Count; i++) _idsCache[i] = new IslandId(slots[i], _iGen[slots[i]], _netId);
        _idsCount = slots.Count;
        _idsDirty = false;
    }

    // Thin adapters over ExternalKey's canonical CompareTo (Handles.cs) — one
    // ordering definition for every deterministic sort/min in the core.
    private static bool KeyLess(in ExternalKey a, in ExternalKey b) => a.CompareTo(b) < 0;

    private static int KeyCompare(in ExternalKey a, in ExternalKey b) => a.CompareTo(b);

    // ============================================================ readback

    internal double ReadVoltage(NodeId n) => ReadVoltageNumeric(n);
    internal double ReadCurrent(in ComponentRef c) => ReadCurrentNumeric(c);
    internal double ReadPower(in ComponentRef c) => ReadPowerNumeric(c);

    internal bool IslandIsLive(IslandId i)
        => (uint)i.Slot < (uint)_iSlotCount && _iAlive[i.Slot] && _iGen[i.Slot] == i.Gen
           && _iStatus[i.Slot] == (byte)IslandStatus.Ready;

    internal ReadOnlySpan<double> RawVector(IslandId i) => RawVectorNumeric(i);

    // ============================================================ meta seams

    internal void SetLimits(in ComponentRef c, in LimitSpec cfg)
    {
        if (ResolveComp(c, out var slot)) _cLimits[slot] = cfg;
    }

    internal void SetProbeInterpolation(ProbeId p, NodeId a, NodeId b, double t)
    {
        if (p.Net != _netId || (uint)p.Slot >= (uint)_pCount || !_pAlive[p.Slot] || _pGen[p.Slot] != p.Gen) return;
        _pA[p.Slot] = RequireNode(a); _pB[p.Slot] = RequireNode(b); _pT[p.Slot] = t;
    }

    internal void SetDebugName(in ComponentRef c, string name) { _ = c; _ = name; }  // debug-only; no-op in the document stage
}
