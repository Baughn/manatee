using System;

namespace Manatee.Core;

// Energy audit as a first-class core observable (api.md §11; solver.md; the
// windowed physical-audit math promoted from the phase-5b test infra). Per
// island we integrate the honest energy flows ON THE HOT PATH into plain
// per-island doubles — SourceJ (independent sources delivered) and DissipatedJ
// (resistive I²R) — reconstructed from the just-solved node potentials each
// substep. StoredJ (½CV² + ½LI²), the boundary net, and coupler heat are
// reconstructed cheaply AT READBACK. This is 0 B on the substep path (scalar
// accumulation into island-indexed arrays, no per-substep publish) and gives
// CheckInvariants(Energy) a real residual:
//
//   ResidualJ = SourceJ − DissipatedJ − (StoredJ − Stored0J) − BoundaryNetJ
//
// which is ≈ 0 for a solo island (Backward-Euler conserves up to its numerical
// dissipation, → 0 as the island settles). The test-side PhysicalAudit stays an
// INDEPENDENT reconstruction (public node-voltage readbacks) cross-checking this
// core accumulation — two derivations of the same balance (api.md §7 ruling).
public sealed partial class Netlist
{
    // ── Per-island energy accumulators (island-slot indexed). ──
    private double[] _iEnergySourceJ = new double[4];
    private double[] _iEnergyDissJ = new double[4];
    private double[] _iEnergyStored0 = new double[4];   // stored energy at the window start (build)

    private void EnsureEnergyCap(int min)
    {
        if (_iEnergySourceJ.Length >= min) return;
        var cap = Math.Max(min, _iEnergySourceJ.Length * 2);
        Array.Resize(ref _iEnergySourceJ, cap); Array.Resize(ref _iEnergyDissJ, cap);
        Array.Resize(ref _iEnergyStored0, cap);
    }

    // Reset the audit window for an island whose Circuit was just (re)built: zero
    // the integrals and capture the stored-energy baseline so the residual measures
    // conservation from this build forward (a rebuild legitimately restarts it).
    private void ResetIslandEnergy(int islandSlot)
    {
        EnsureEnergyCap(_iSlotCount);
        _iEnergySourceJ[islandSlot] = 0.0;
        _iEnergyDissJ[islandSlot] = 0.0;
        _iEnergyStored0[islandSlot] = StoredEnergy(islandSlot);
    }

    // Instantaneous stored energy in the island: ½CV² per capacitor, ½LI² per
    // inductor (the state variable IS V / I). Cold readback; also the window
    // baseline. Storage-free islands return 0.
    private double StoredEnergy(int islandSlot)
    {
        var e = 0.0;
        var rt = (uint)islandSlot < (uint)_iRuntime.Length ? _iRuntime[islandSlot] : null;
        if (rt is not null && !_iRuntimeStale[islandSlot])
            for (var i = 0; i < rt.MemberCount; i++) e += StoredComp(rt.MemberComps[i]);
        else
            for (var c = 0; c < _cCount; c++)
                if (_cAlive[c] && _nIsland[_cA[c]] == islandSlot) e += StoredComp(c);
        return e;
    }

    private double StoredComp(int c)
    {
        var kind = (ComponentKind)_cKind[c];
        // The state variable IS V (cap) / I (inductor): ½CV² and ½LI² share this form.
        if (kind == ComponentKind.Capacitor || kind == ComponentKind.Inductor)
            return 0.5 * _cValue[c] * _cStateVar[c] * _cStateVar[c];
        return 0.0;
    }

    // Accumulate one substep's source delivery and resistive dissipation into the
    // island doubles, reconstructed from the just-solved potentials. Zero-alloc.
    // Called from the substep loops (StepTransient / AdvanceOneSubstep) and the DC
    // solve tail, with that step's dt.
    private void AccumulateIslandEnergy(int islandSlot, double dt)
    {
        if (dt <= 0.0) return;
        var src = 0.0; var diss = 0.0;
        var rt = (uint)islandSlot < (uint)_iRuntime.Length ? _iRuntime[islandSlot] : null;
        if (rt is not null && !_iRuntimeStale[islandSlot])
            for (var i = 0; i < rt.MemberCount; i++) AccumulateEnergyComp(rt.MemberComps[i], ref src, ref diss);
        else
            for (var c = 0; c < _cCount; c++)
                if (_cAlive[c] && _nIsland[_cA[c]] == islandSlot) AccumulateEnergyComp(c, ref src, ref diss);
        _iEnergySourceJ[islandSlot] += src * dt;
        _iEnergyDissJ[islandSlot] += diss * dt;
    }

    private void AccumulateEnergyComp(int c, ref double src, ref double diss)
    {
        var kind = (ComponentKind)_cKind[c];
        var va = NodePotential(_cA[c]);
        var vb = _cB[c] >= 0 ? NodePotential(_cB[c]) : 0.0;
        var v = va - vb;
        switch (kind)
        {
            case ComponentKind.Resistor:
                diss += v * v / _cValue[c];
                break;
            case ComponentKind.Switch:
                diss += v * v / (_cValue[c] != 0.0 ? ClosedSwitchOhms : OpenSwitchOhms);
                break;
            case ComponentKind.VSource:
                // Delivered by the source = −(V·i_branch): a positive branch current
                // is A→source (absorbing), so a delivering source has i_branch < 0.
                src += -(v * BranchCurrent(c));
                break;
            case ComponentKind.ISource:
                // Drives its value from `to` back to `from`; delivered = (V_to−V_from)·I.
                src += (vb - va) * _cValue[c];
                break;
            case ComponentKind.Diode:
                diss += v * BranchCurrent(c);   // junction dissipation (absorbed a→c)
                break;
        }
    }

    // Net energy FLOWING OUT of the island across boundary couplers (positive =
    // leaving): the A (input) side sheds InJ; the B (output) side receives OutJ.
    private double BoundaryNetOut(int islandSlot)
    {
        var net = 0.0;
        for (var k = 0; k < _kCount; k++)
        {
            if (!_kAlive[k] || _kSpec[k].IsGalvanic) continue;
            var rt = k < _kRuntime.Length ? _kRuntime[k] : null;
            if (rt is null) continue;
            if (_nIsland[_kAPos[k]] == islandSlot) net += rt.InJ;    // A delivers into the coupler
            if (_nIsland[_kBPos[k]] == islandSlot) net -= rt.OutJ;   // B receives from the coupler
        }
        return net;
    }

    // Coupler heat attributed to this island's input-side couplers (informational).
    private double BoundaryHeat(int islandSlot)
    {
        var heat = 0.0;
        for (var k = 0; k < _kCount; k++)
        {
            if (!_kAlive[k] || _kSpec[k].IsGalvanic) continue;
            var rt = k < _kRuntime.Length ? _kRuntime[k] : null;
            if (rt is null || _nIsland[_kAPos[k]] != islandSlot) continue;
            var surplus = rt.InJ - rt.OutJ - rt.ModeledLossJ;
            heat += rt.ModeledLossJ + (surplus > 0.0 ? surplus : 0.0);
        }
        return heat;
    }

    internal EnergyAudit EnergyAuditOf(int islandSlot, uint gen)
    {
        if (!IslandGenLive(islandSlot, gen)) return default;
        var stored = StoredEnergy(islandSlot);
        var boundaryOut = BoundaryNetOut(islandSlot);
        var heat = BoundaryHeat(islandSlot);
        var source = _iEnergySourceJ[islandSlot];
        var diss = _iEnergyDissJ[islandSlot];
        var deltaStored = stored - _iEnergyStored0[islandSlot];
        var residual = source - diss - deltaStored - boundaryOut;
        return new EnergyAudit(source, diss, stored, boundaryOut, heat, residual);
    }

    internal double EnergyResidualOf(int islandSlot)
        => EnergyAuditOf(islandSlot, _iGen[islandSlot]).ResidualJ;
}
