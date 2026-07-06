using System;
using System.Buffers;
using System.Collections.Generic;
using Manatee.Core.State;

namespace Manatee.Core;

// State-unit seam, snapshot/restore, and whole-netlist serialization (api.md §14;
// solver.md State). Three layers:
//
//   1. STATEFUL-UNIT SEAM — an internal registry of serializable dynamic state
//      keyed by StateKey. The built-in units are the stateful solver primitives
//      (capacitor V, inductor I, sine phase, diode junction V), whose state lives
//      in the component SoA (_cStateVar / _cStatePrev). Phase-8 devices register
//      their OWN blob via IDeviceStateUnit (RegisterDeviceState) — the documented
//      plug point, exercised end-to-end by the device-state round-trip test.
//
//   2. PER-ISLAND SNAPSHOT / RESTORE — versioned binary, restore ADDITIVE by
//      StateKey (matched / untouched / orphans; never resets an unmatched unit).
//
//   3. WHOLE-NETLIST SERIALIZATION — SaveCanonical (slot-preserving memento) /
//      SaveNormalized (ExternalKey-sorted minimal form) / FromCanonical, plus the
//      cheap structural Fingerprint. These are cold; snapshot's core-side buffer
//      is caller-owned (0 B core-side).
public sealed partial class Netlist
{
    // Snapshot container magic + version (bumped on any layout change; NOT stable
    // across versions — a versioned binary, not a wire contract).
    private const uint SnapshotMagic = 0x3153_544D;   // "MST1" little-endian
    private const byte SnapshotVersion = 1;
    private const uint CanonicalMagic = 0x314E_434D;   // "MCN1"
    // v2: added evolved limit/coupler state to the memento — per-component i²t melting
    // integral + trip latch, the global i²t ambient threshold scale, and each boundary
    // coupler's persistent CouplerRuntime scalars (DC-link cap voltage, debt-droop
    // integrators, relaxation smoothing, energy ledger). Without these a SaveCanonical/
    // FromCanonical round-trip silently un-heats a partway fuse and cold-starts a settled
    // converter/transformer at V_B=0 (a startup transient the never-saved run lacks).
    private const byte CanonicalVersion = 2;

    // Built-in state-unit payload: stateVar + statePrev (two IEEE-754 doubles).
    private const int BuiltinPayloadBytes = 16;

    // ── Phase-8 device-state registry (StateKey → provider + anchor node). ──
    private readonly struct DeviceStateEntry
    {
        public DeviceStateEntry(int anchorSlot, IDeviceStateUnit unit) { AnchorSlot = anchorSlot; Unit = unit; }
        public int AnchorSlot { get; }
        public IDeviceStateUnit Unit { get; }
    }

    private readonly Dictionary<StateKey, DeviceStateEntry> _deviceStates = new();
    private readonly List<StateKey> _deviceStateOrder = new();   // deterministic emission order

    // ── DrainOrphans backing store (per-netlist; a Restore is a cold, serial op). ──
    private StateKey[] _restoreOrphans = Array.Empty<StateKey>();
    private int _restoreOrphanCount;
    private long _restoreToken;   // bumped each Restore; guards a stale RestoreResult

    // Reusable island state-unit lookup for Restore (StateKey → component slot).
    private readonly Dictionary<StateKey, int> _islandUnitScratch = new();

    // =============================================================== seam

    /// <summary>Register a phase-8 device's serializable <c>Tick</c> state so it
    /// joins the snapshot/restore stream keyed by <see cref="StateKey"/> (api.md
    /// §14 plug point). <paramref name="anchor"/> pins the unit to an island — its
    /// blob is emitted by whichever island the anchor node currently belongs to,
    /// so the unit follows a merge/split exactly like a built-in primitive. A
    /// second registration for the same key replaces the first. Cold (setup).</summary>
    public void RegisterDeviceState(NodeId anchor, IDeviceStateUnit unit)
    {
        if (unit is null) throw new ArgumentNullException(nameof(unit));
        if (!ResolveNode(anchor, out var slot))
            throw new ArgumentException("RegisterDeviceState anchor node is not live.", nameof(anchor));
        var key = unit.Key;
        if (!_deviceStates.ContainsKey(key)) _deviceStateOrder.Add(key);
        _deviceStates[key] = new DeviceStateEntry(slot, unit);
    }

    /// <summary>Remove a registered device-state unit. Returns false if absent.</summary>
    public bool UnregisterDeviceState(in StateKey key)
    {
        if (!_deviceStates.Remove(key)) return false;
        _deviceStateOrder.Remove(key);
        return true;
    }

    // Whether a component slot carries serializable dynamic state, and its kind.
    private bool IsStatefulComp(int slot)
    {
        if (!_cAlive[slot]) return false;
        var kind = (ComponentKind)_cKind[slot];
        return kind == ComponentKind.Capacitor || kind == ComponentKind.Inductor
            || kind == ComponentKind.Diode || (kind == ComponentKind.VSource && _cIsSine[slot]);
    }

    private StateUnitKind BuiltinKindOf(int slot)
    {
        var kind = (ComponentKind)_cKind[slot];
        return kind switch
        {
            ComponentKind.Capacitor => StateUnitKind.Capacitor,
            ComponentKind.Inductor => StateUnitKind.Inductor,
            ComponentKind.Diode => StateUnitKind.Diode,
            _ => StateUnitKind.SinePhase,   // sine VSource
        };
    }

    // ======================================================= snapshot / restore

    internal int StateUnitCount(int islandSlot, uint gen)
    {
        if (!IslandGenLive(islandSlot, gen)) return 0;
        var n = 0;
        for (var c = 0; c < _cCount; c++)
            if (_cAlive[c] && _nIsland[_cA[c]] == islandSlot && IsStatefulComp(c)) n++;
        foreach (var key in _deviceStateOrder)
        {
            var e = _deviceStates[key];
            if (_nAlive[e.AnchorSlot] && _nIsland[e.AnchorSlot] == islandSlot) n++;
        }
        return n;
    }

    internal int SnapshotSize(int islandSlot, uint gen)
    {
        if (!IslandGenLive(islandSlot, gen)) return 0;
        var size = 4 + 1 + 4;   // magic + version + count
        for (var c = 0; c < _cCount; c++)
            if (_cAlive[c] && _nIsland[_cA[c]] == islandSlot && IsStatefulComp(c))
                size += 1 + 16 + BuiltinPayloadBytes;   // kind + StateKey + payload
        foreach (var key in _deviceStateOrder)
        {
            var e = _deviceStates[key];
            if (_nAlive[e.AnchorSlot] && _nIsland[e.AnchorSlot] == islandSlot)
                size += 1 + 16 + 4 + e.Unit.BlobSize;   // kind + StateKey + blobLen + blob
        }
        return size;
    }

    internal void Snapshot(int islandSlot, uint gen, IBufferWriter<byte> into)
    {
        if (into is null) throw new ArgumentNullException(nameof(into));
        if (!IslandGenLive(islandSlot, gen))
            throw new InvalidOperationException("Snapshot on a stale/dead island handle.");

        var w = new SpanWriter(into);
        w.UInt32(SnapshotMagic);
        w.Byte(SnapshotVersion);
        w.Int32(StateUnitCount(islandSlot, gen));

        for (var c = 0; c < _cCount; c++)
        {
            if (!_cAlive[c] || _nIsland[_cA[c]] != islandSlot || !IsStatefulComp(c)) continue;
            w.Byte((byte)BuiltinKindOf(c));
            w.StateKey(_cState[c]);
            w.Double(_cStateVar[c]);
            w.Double(_cStatePrev[c]);
        }

        foreach (var key in _deviceStateOrder)
        {
            var e = _deviceStates[key];
            if (!_nAlive[e.AnchorSlot] || _nIsland[e.AnchorSlot] != islandSlot) continue;
            var size = e.Unit.BlobSize;
            var buf = new byte[size];   // cold device path; heap so the span escapes the writer cleanly
            e.Unit.Save(buf);
            w.Byte((byte)StateUnitKind.Device);
            w.StateKey(key);
            w.Int32(size);
            w.Bytes(buf);
        }

        w.Flush();
    }

    internal RestoreResult Restore(int islandSlot, uint gen, ReadOnlySpan<byte> blob)
    {
        if (!IslandGenLive(islandSlot, gen))
            throw new InvalidOperationException("Restore on a stale/dead island handle.");

        // Build the island's live-unit lookup (StateKey → component slot). Cold.
        _islandUnitScratch.Clear();
        for (var c = 0; c < _cCount; c++)
            if (_cAlive[c] && _nIsland[_cA[c]] == islandSlot && IsStatefulComp(c))
                _islandUnitScratch[_cState[c]] = c;

        var r = new SpanReader(blob);
        var magic = r.UInt32();
        if (magic != SnapshotMagic)
            throw new InvalidOperationException($"Restore: bad snapshot magic 0x{magic:X8} (expected 0x{SnapshotMagic:X8}).");
        var ver = r.Byte();
        if (ver != SnapshotVersion)
            throw new InvalidOperationException($"Restore: snapshot version {ver} unsupported (this build writes {SnapshotVersion}).");
        var count = r.Int32();

        var matched = 0;
        _restoreOrphanCount = 0;
        _restoreToken++;

        for (var i = 0; i < count; i++)
        {
            var kind = (StateUnitKind)r.Byte();
            var key = r.StateKey();
            if (kind == StateUnitKind.Device)
            {
                var len = r.Int32();
                var bytes = r.Bytes(len);
                if (_deviceStates.TryGetValue(key, out var e)
                    && _nAlive[e.AnchorSlot] && _nIsland[e.AnchorSlot] == islandSlot
                    && e.Unit.BlobSize == len)
                {
                    e.Unit.Restore(bytes);
                    matched++;
                    // A device restore feeds new state that may change the device's matrix
                    // contribution; a settled Ready, non-transient island would otherwise
                    // skip its next Solve and never reflect it — mirror the built-in branch.
                    MarkIslandDirty(islandSlot);
                }
                else RecordOrphan(key);
            }
            else
            {
                var stateVar = r.Double();
                var statePrev = r.Double();
                if (_islandUnitScratch.TryGetValue(key, out var slot))
                {
                    _cStateVar[slot] = stateVar;
                    _cStatePrev[slot] = statePrev;
                    matched++;
                    // A restore feeds new history into the island's storage/warm state,
                    // so its next Solve must re-run (a settled Ready island would else
                    // skip the RHS solve and never reflect the restored state).
                    MarkIslandDirty(islandSlot);
                }
                else RecordOrphan(key);
            }
        }

        var live = _islandUnitScratch.Count + DeviceUnitsInIsland(islandSlot);
        var untouched = live - matched;
        if (untouched < 0) untouched = 0;   // more matched than live only if blob double-keys; defensive
        return new RestoreResult(this, _restoreToken, matched, untouched, _restoreOrphanCount);
    }

    private int DeviceUnitsInIsland(int islandSlot)
    {
        var n = 0;
        foreach (var key in _deviceStateOrder)
        {
            var e = _deviceStates[key];
            if (_nAlive[e.AnchorSlot] && _nIsland[e.AnchorSlot] == islandSlot) n++;
        }
        return n;
    }

    private void RecordOrphan(in StateKey key)
    {
        if (_restoreOrphanCount == _restoreOrphans.Length)
            Array.Resize(ref _restoreOrphans, Math.Max(4, _restoreOrphans.Length * 2));
        _restoreOrphans[_restoreOrphanCount++] = key;
    }

    internal int DrainOrphans(long token, Span<StateKey> into)
    {
        // Only the most recent Restore's orphans are addressable (a Restore is a
        // cold, serial op; the token catches a stale RestoreResult after a later
        // Restore overwrote the scratch).
        if (token != _restoreToken) return 0;
        var n = Math.Min(into.Length, _restoreOrphanCount);
        for (var i = 0; i < n; i++) into[i] = _restoreOrphans[i];
        return n;
    }

    // =================================================== whole-netlist canonical

    /// <summary>Slot-preserving whole-netlist memento (api.md §14). Captures the
    /// document arenas verbatim (slots, generations, free lists, island
    /// membership) so a recorded command log or snapshot replays against the exact
    /// same slot layout. Excludes NETID (process-assigned; supplied at
    /// <see cref="FromCanonical"/>) and all derived/transient state (journal, change
    /// ring, numeric runtime) — which is what makes law 1 a byte-equality.</summary>
    internal void WriteCanonical(IBufferWriter<byte> dst)
    {
        var w = new SpanWriter(dst);
        w.UInt32(CanonicalMagic);
        w.Byte(CanonicalVersion);

        // Global evolved scalar: ambient i²t threshold scale (§12 phase-7 seam).
        w.Double(_i2tThresholdScale);

        // Nodes.
        w.Int32(_nCount);
        for (var i = 0; i < _nCount; i++)
        {
            w.UInt32(_nGen[i]); w.Bool(_nAlive[i]); w.Byte(_nRole[i]); w.UInt64(_nPart[i]);
            w.Key(_nKey[i]); w.Int32(_nIsland[i]); w.Int32(_nDegree[i]);
            w.Int64(_nInvalidSeq[i]); w.Byte(_nInvalidKind[i]);
        }
        WriteFreeStack(ref w, _nFree);

        // Components.
        w.Int32(_cCount);
        for (var i = 0; i < _cCount; i++)
        {
            w.UInt32(_cGen[i]); w.Bool(_cAlive[i]); w.Byte(_cKind[i]);
            w.Int32(_cA[i]); w.Int32(_cB[i]); w.Int32(_cC[i]); w.Int32(_cD[i]);
            w.Double(_cValue[i]); w.Double(_cStateVar[i]); w.Double(_cStatePrev[i]);
            w.Key(_cKey[i]); w.StateKey(_cState[i]);
            w.Double(_cDiode[i].SaturationCurrent); w.Double(_cDiode[i].Emission); w.Double(_cDiode[i].SeriesResistance);
            w.Double(_cSine[i].AmplitudeV); w.Double(_cSine[i].FreqHz); w.Double(_cSine[i].PhaseRad);
            w.Bool(_cIsSine[i]);
            WriteLimit(ref w, _cLimits[i]);
            // Evolved thermal state: the i²t melting integral and its rising-edge trip
            // latch (a partway-melted fuse must reload partway-melted, not cold).
            w.Double(i < _cI2t.Length ? _cI2t[i] : 0.0);
            w.Bool(i < _cI2tTripped.Length && _cI2tTripped[i]);
            w.Int64(_cInvalidSeq[i]); w.Byte(_cInvalidKind[i]);
        }
        WriteFreeStack(ref w, _cFree);

        // Couplers.
        w.Int32(_kCount);
        for (var i = 0; i < _kCount; i++)
        {
            w.UInt32(_kGen[i]); w.Bool(_kAlive[i]); w.Byte((byte)_kStateA[i]);
            WriteSpec(ref w, _kSpec[i]);
            w.Int32(_kAPos[i]); w.Int32(_kANeg[i]); w.Int32(_kBPos[i]); w.Int32(_kBNeg[i]);
            w.Key(_kKey[i]);
            WriteCouplerRuntime(ref w, i < _kRuntime.Length ? _kRuntime[i] : null);
        }
        WriteFreeStack(ref w, _kFree);

        // Probes.
        w.Int32(_pCount);
        for (var i = 0; i < _pCount; i++)
        {
            w.UInt32(_pGen[i]); w.Bool(_pAlive[i]); w.Int32(_pA[i]); w.Int32(_pB[i]); w.Double(_pT[i]); w.Key(_pKey[i]);
        }
        WriteFreeStack(ref w, _pFree);

        // Islands.
        w.Int32(_iSlotCount);
        for (var i = 0; i < _iSlotCount; i++)
        {
            w.UInt32(_iGen[i]); w.Bool(_iAlive[i]); w.Byte(_iStatus[i]); w.Bool(_iNeedsRebuild[i]); w.Int32(_iNodeCount[i]);
        }
        WriteFreeStack(ref w, _iFree);
        w.Int32(_iAliveCount);

        w.Flush();
    }

    // Persistent boundary-coupler runtime scalars (canonical v2). These are the
    // genuinely evolving, non-derived state: the DC-link cap's Backward-Euler history
    // (the converter's B port IS a capacitor — §7), the transformer debt-droop
    // integrators, the relaxation smoothing anchors, and the energy ledger. The
    // transient re-resolved fields (island slots, matrix stamps, local node indices)
    // are NOT serialized — they are rebuilt by StampBoundaryCouplers on the first solve
    // after load, which reads these scalars (e.g. the DC-link history) back in.
    private void WriteCouplerRuntime(ref SpanWriter w, CouplerRuntime? rt)
    {
        w.Bool(rt is not null);
        if (rt is null) return;
        w.Double(rt.DcLinkVPrev);
        w.Double(rt.DebtJ); w.Double(rt.DroopScale);
        w.Double(rt.TputPeakJ); w.Double(rt.TputEmaJ); w.Double(rt.OverEmaJ);
        w.Double(rt.VSmooth); w.Double(rt.ISmooth);
        w.Double(rt.LastVB); w.Double(rt.LastIA); w.Double(rt.LastICharge);
        w.Double(rt.InJ); w.Double(rt.OutJ); w.Double(rt.ModeledLossJ);
    }

    private CouplerRuntime? ReadCouplerRuntime(ref SpanReader r)
    {
        if (!r.Bool()) return null;
        var rt = new CouplerRuntime
        {
            DcLinkVPrev = r.Double(),
            DebtJ = r.Double(), DroopScale = r.Double(),
            TputPeakJ = r.Double(), TputEmaJ = r.Double(), OverEmaJ = r.Double(),
            VSmooth = r.Double(), ISmooth = r.Double(),
            LastVB = r.Double(), LastIA = r.Double(), LastICharge = r.Double(),
            InJ = r.Double(), OutJ = r.Double(), ModeledLossJ = r.Double(),
        };
        return rt;
    }

    private static void WriteFreeStack(ref SpanWriter w, Stack<int> free)
    {
        // Serialise the free-slot stack bottom-to-top so it reloads with identical
        // pop order (byte-equal round-trip).
        var arr = free.ToArray();            // top-first
        w.Int32(arr.Length);
        for (var i = arr.Length - 1; i >= 0; i--) w.Int32(arr[i]);   // push bottom-first
    }

    private static void ReadFreeStack(ref SpanReader r, Stack<int> free)
    {
        free.Clear();
        var n = r.Int32();
        for (var i = 0; i < n; i++) free.Push(r.Int32());
    }

    private static void WriteLimit(ref SpanWriter w, in LimitSpec s)
    {
        w.Double(s.MaxCurrent); w.Double(s.MaxVoltage); w.Double(s.MaxPower);
        w.Double(s.Thermal.MeltI2t); w.Double(s.Thermal.Tau);
    }

    private static LimitSpec ReadLimit(ref SpanReader r)
    {
        var mc = r.Double(); var mv = r.Double(); var mp = r.Double();
        var melt = r.Double(); var tau = r.Double();
        return new LimitSpec(mc, mv, mp, new I2tParams(melt, tau));
    }

    private static void WriteSpec(ref SpanWriter w, in CouplerSpec s)
    {
        w.Byte((byte)s.Kind);
        w.Double(s.RelaxationAlpha); w.Double(s.DcLinkFarads); w.Double(s.OutputVolts); w.Double(s.RatedWatts);
        w.Double(s.Transformer.TurnsRatio); w.Double(s.Transformer.LeakageOhms); w.Double(s.Transformer.MagnetizingOhms);
        Span<double> pts = stackalloc double[8];
        var n = s.Efficiency.Serialize(pts);
        w.Int32(n);
        for (var i = 0; i < n * 2; i++) w.Double(pts[i]);
    }

    private static CouplerSpec ReadSpec(ref SpanReader r)
    {
        var kind = (CouplerSpec.Family)r.Byte();
        var alpha = r.Double(); var dcLink = r.Double(); var vout = r.Double(); var rated = r.Double();
        var turns = r.Double(); var leak = r.Double(); var mag = r.Double();
        var n = r.Int32();
        var tp = new TransformerParams(turns, leak, mag);
        switch (kind)
        {
            case CouplerSpec.Family.Breaker:
                for (var i = 0; i < n * 2; i++) r.Double();   // no efficiency payload for a breaker
                return CouplerSpec.Breaker();
            case CouplerSpec.Family.DecouplingTransformer:
                for (var i = 0; i < n * 2; i++) r.Double();
                return CouplerSpec.DecouplingTransformer(tp, alpha);
            default:
                Span<(double, double)> pts = stackalloc (double, double)[4];
                for (var i = 0; i < n; i++) pts[i] = (r.Double(), r.Double());
                var eff = EfficiencyCurve.FromPoints(pts.Slice(0, n));
                return CouplerSpec.ConverterTwoPort(eff, dcLink, vout, rated, alpha);
        }
    }

    /// <summary>Reconstruct a netlist from a <see cref="WriteCanonical"/> memento.
    /// Options are supplied by the caller (netId/journal/runtime are re-minted).</summary>
    internal void LoadCanonical(ReadOnlySpan<byte> src)
    {
        var r = new SpanReader(src);
        var magic = r.UInt32();
        if (magic != CanonicalMagic)
            throw new InvalidOperationException($"FromCanonical: bad magic 0x{magic:X8}.");
        var ver = r.Byte();
        if (ver != CanonicalVersion)
            throw new InvalidOperationException($"FromCanonical: version {ver} unsupported.");

        _i2tThresholdScale = r.Double();

        // Nodes.
        _nCount = r.Int32();
        EnsureNodeCap(Math.Max(4, _nCount));
        _nKeyMap.Clear();
        for (var i = 0; i < _nCount; i++)
        {
            _nGen[i] = r.UInt32(); _nAlive[i] = r.Bool(); _nRole[i] = r.Byte(); _nPart[i] = r.UInt64();
            _nKey[i] = r.Key(); _nIsland[i] = r.Int32(); _nDegree[i] = r.Int32();
            _nInvalidSeq[i] = r.Int64(); _nInvalidKind[i] = r.Byte();
            _nRtSlot[i] = -1;
            if (_nAlive[i]) _nKeyMap[_nKey[i]] = i;
        }
        ReadFreeStack(ref r, _nFree);

        // Components.
        _cCount = r.Int32();
        EnsureCompCap(Math.Max(8, _cCount));
        EnsureLimitCap(Math.Max(8, _cCount));   // size the i²t arrays before rehydrating them
        _cKeyMap.Clear();
        for (var i = 0; i < _cCount; i++)
        {
            _cGen[i] = r.UInt32(); _cAlive[i] = r.Bool(); _cKind[i] = r.Byte();
            _cA[i] = r.Int32(); _cB[i] = r.Int32(); _cC[i] = r.Int32(); _cD[i] = r.Int32();
            _cValue[i] = r.Double(); _cStateVar[i] = r.Double(); _cStatePrev[i] = r.Double();
            _cKey[i] = r.Key(); _cState[i] = r.StateKey();
            _cDiode[i] = new DiodeParams(r.Double(), r.Double(), r.Double());
            _cSine[i] = new SineDrive(r.Double(), r.Double(), r.Double());
            _cIsSine[i] = r.Bool();
            _cLimits[i] = ReadLimit(ref r);
            _cI2t[i] = r.Double(); _cI2tTripped[i] = r.Bool();
            _cInvalidSeq[i] = r.Int64(); _cInvalidKind[i] = r.Byte();
            _cStampKind[i] = 0;
            if (_cAlive[i]) _cKeyMap[_cKey[i]] = i;
        }
        ReadFreeStack(ref r, _cFree);

        // Couplers.
        _kCount = r.Int32();
        EnsureCouplerCap(Math.Max(4, _kCount));
        if (_kRuntime.Length < _kCount) Array.Resize(ref _kRuntime, Math.Max(4, _kCount));
        _kKeyMap.Clear();
        for (var i = 0; i < _kCount; i++)
        {
            _kGen[i] = r.UInt32(); _kAlive[i] = r.Bool(); _kStateA[i] = (CouplerState)r.Byte();
            _kSpec[i] = ReadSpec(ref r);
            _kAPos[i] = r.Int32(); _kANeg[i] = r.Int32(); _kBPos[i] = r.Int32(); _kBNeg[i] = r.Int32();
            _kKey[i] = r.Key();
            // Rehydrate the persistent coupler runtime scalars (DC-link history, debt
            // droop, ledger). The transient stamps/local-node fields stay at defaults —
            // StampBoundaryCouplers reuses this same runtime object (EnsureCouplerRuntime
            // returns non-null) and re-resolves them on the first solve after load.
            _kRuntime[i] = ReadCouplerRuntime(ref r);
            if (_kAlive[i]) _kKeyMap[_kKey[i]] = i;
        }
        ReadFreeStack(ref r, _kFree);

        // Probes.
        _pCount = r.Int32();
        EnsureProbeCap(Math.Max(4, _pCount));
        _pKeyMap.Clear();
        for (var i = 0; i < _pCount; i++)
        {
            _pGen[i] = r.UInt32(); _pAlive[i] = r.Bool(); _pA[i] = r.Int32(); _pB[i] = r.Int32();
            _pT[i] = r.Double(); _pKey[i] = r.Key();
            if (_pAlive[i]) _pKeyMap[_pKey[i]] = i;
        }
        ReadFreeStack(ref r, _pFree);

        // Islands.
        _iSlotCount = r.Int32();
        EnsureIslandCap(Math.Max(4, _iSlotCount));
        for (var i = 0; i < _iSlotCount; i++)
        {
            _iGen[i] = r.UInt32(); _iAlive[i] = r.Bool(); _iStatus[i] = r.Byte();
            _iNeedsRebuild[i] = r.Bool(); _iNodeCount[i] = r.Int32();
            _iRuntime[i] = null; _iRuntimeStale[i] = _iAlive[i]; _iFault[i] = default;
            _iSubstepN[i] = 0; _iSubstepDt[i] = 0.0; _iSubstepRawRef[i] = 0.0;
        }
        ReadFreeStack(ref r, _iFree);
        _iAliveCount = r.Int32();

        _idsDirty = true; _unitsDirty = true;
        _deviceStates.Clear(); _deviceStateOrder.Clear();
    }

    // =============================================================== normalized

    /// <summary>ExternalKey-sorted minimal form (api.md §14): endpoints are named
    /// by their nodes' ExternalKeys (never slots), so two build paths that made
    /// the same logical circuit compare byte-equal — R11's drift detector stated
    /// as an equality. Slots, generations, free lists, island membership and
    /// evolved dynamic state are all excluded (volatile / derived).</summary>
    internal void WriteNormalized(IBufferWriter<byte> dst)
    {
        var w = new SpanWriter(dst);
        w.UInt32(CanonicalMagic); w.Byte(CanonicalVersion);

        // Nodes sorted by key.
        var nodes = new List<int>();
        for (var i = 0; i < _nCount; i++) if (_nAlive[i]) nodes.Add(i);
        nodes.Sort((x, y) => KeyCompare(_nKey[x], _nKey[y]));
        w.Int32(nodes.Count);
        foreach (var i in nodes) { w.Key(_nKey[i]); w.Byte(_nRole[i]); w.UInt64(_nPart[i]); }

        // Components sorted by key; endpoints by node key.
        var comps = new List<int>();
        for (var i = 0; i < _cCount; i++) if (_cAlive[i]) comps.Add(i);
        comps.Sort((x, y) => KeyCompare(_cKey[x], _cKey[y]));
        w.Int32(comps.Count);
        foreach (var i in comps)
        {
            w.Key(_cKey[i]); w.Byte(_cKind[i]);
            w.Key(_nKey[_cA[i]]);
            w.Key(_cB[i] >= 0 ? _nKey[_cB[i]] : default);
            w.Key(_cC[i] >= 0 ? _nKey[_cC[i]] : default);
            w.Key(_cD[i] >= 0 ? _nKey[_cD[i]] : default);
            w.Double(_cValue[i]);
            w.Double(_cDiode[i].SaturationCurrent); w.Double(_cDiode[i].Emission); w.Double(_cDiode[i].SeriesResistance);
            w.Double(_cSine[i].AmplitudeV); w.Double(_cSine[i].FreqHz); w.Double(_cSine[i].PhaseRad);
            w.Bool(_cIsSine[i]);
            WriteLimit(ref w, _cLimits[i]);
        }

        // Couplers sorted by key; ports by node key.
        var coups = new List<int>();
        for (var i = 0; i < _kCount; i++) if (_kAlive[i]) coups.Add(i);
        coups.Sort((x, y) => KeyCompare(_kKey[x], _kKey[y]));
        w.Int32(coups.Count);
        foreach (var i in coups)
        {
            w.Key(_kKey[i]); w.Byte((byte)_kStateA[i]);
            WriteSpec(ref w, _kSpec[i]);
            w.Key(_nKey[_kAPos[i]]); w.Key(_nKey[_kANeg[i]]); w.Key(_nKey[_kBPos[i]]); w.Key(_nKey[_kBNeg[i]]);
        }

        // Probes sorted by key; endpoints by node key.
        var probes = new List<int>();
        for (var i = 0; i < _pCount; i++) if (_pAlive[i]) probes.Add(i);
        probes.Sort((x, y) => KeyCompare(_pKey[x], _pKey[y]));
        w.Int32(probes.Count);
        foreach (var i in probes)
        {
            w.Key(_pKey[i]); w.Key(_nKey[_pA[i]]); w.Key(_nKey[_pB[i]]); w.Double(_pT[i]);
        }

        w.Flush();
    }

    // =============================================================== fingerprint

    /// <summary>Cheap structural hash of one island (api.md §14 level-1 drift
    /// backstop). FNV-1a over the island's node keys and, per member component,
    /// its (key, kind, endpoint keys); <see cref="FingerprintScope.Full"/> mixes
    /// in values and limit envelopes too. STABLE WITHIN A PROCESS RUN, not across
    /// versions — a mismatch is the trigger for the (expensive) SaveNormalized
    /// diff, never an identity persisted to disk.</summary>
    internal ulong FingerprintIsland(int islandSlot, uint gen, FingerprintScope scope)
    {
        if (!IslandGenLive(islandSlot, gen)) return 0;
        const ulong offset = 1469598103934665603UL, prime = 1099511628211UL;
        var h = offset;
        void Mix(ulong v) { unchecked { h = (h ^ v) * prime; } }

        // Nodes in the island, ordered by key (order-independent structural hash).
        var nodes = new List<int>();
        for (var n = 0; n < _nCount; n++)
            if (_nAlive[n] && _nIsland[n] == islandSlot) nodes.Add(n);
        nodes.Sort((x, y) => KeyCompare(_nKey[x], _nKey[y]));
        Mix((ulong)nodes.Count);
        foreach (var n in nodes) { Mix(_nKey[n].Hi); Mix(_nKey[n].Lo); Mix(_nRole[n]); }

        var comps = new List<int>();
        for (var c = 0; c < _cCount; c++)
            if (_cAlive[c] && _nIsland[_cA[c]] == islandSlot) comps.Add(c);
        comps.Sort((x, y) => KeyCompare(_cKey[x], _cKey[y]));
        Mix((ulong)comps.Count);
        foreach (var c in comps)
        {
            Mix(_cKey[c].Hi); Mix(_cKey[c].Lo); Mix(_cKind[c]);
            Mix(_nKey[_cA[c]].Lo);
            if (_cB[c] >= 0) Mix(_nKey[_cB[c]].Lo);
            if (scope == FingerprintScope.Full)
            {
                Mix((ulong)BitConverter.DoubleToInt64Bits(_cValue[c]));
                Mix((ulong)BitConverter.DoubleToInt64Bits(_cLimits[c].MaxCurrent));
                Mix((ulong)BitConverter.DoubleToInt64Bits(_cLimits[c].MaxPower));
                Mix(_cIsSine[c] ? 1UL : 0UL);
            }
        }
        return h;
    }
}
