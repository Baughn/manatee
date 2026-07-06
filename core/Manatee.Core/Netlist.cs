using System.Collections.Generic;
using System.Diagnostics;
using Manatee.Core.Devices;

namespace Manatee.Core;

/// <summary>
/// The retained-mode circuit DOCUMENT (api.md §4). The four verbs write the
/// document; matrices are DERIVED from it at Solve. This distinction carries the
/// whole OQ3 story (§17): a verb call is never lost even if the island is about
/// to rebuild, because the rebuild restamps from the document.
///
/// <para>This is the document + structural-machinery stage. Structure is
/// transactional and immediate (union-find membership, key registration,
/// journal, island lifecycle all settle at <see cref="StructuralEdit.Commit"/>
/// / <see cref="Reconfigure"/>); the per-member generation reissue for a rebuild
/// runs at <see cref="Solve"/>. The NUMERIC solve (matrix assembly / factor /
/// back-substitution) is the next stage and is reached through the internal
/// island-runtime seam — this stage advances the structural status machine and
/// leaves the numbers as last-good.</para>
/// </summary>
public sealed partial class Netlist
{
    private const uint FirstGen = 1;                 // gen 0 ⇒ default(TId) is never live
    private const int DefaultChangeCapacity = 256;

    private static int s_netCounter;

    private readonly ushort _netId;
    private readonly NetlistOptions _opts;
    private readonly bool _debug;
    private readonly double _profileDt;
    private readonly SolverProfile.Regime _profileKind;   // regime discriminant (§5)
    private readonly int _acSamplesPerCycle;              // Mixed: substep sample target (default 20)
    private readonly double _ratioLo, _ratioHi;      // ε-no-op bounds, pinned at construction (api.md §9)
    private readonly TopologyJournal _journal;

    private TickStats _lastTickStats;
    private bool _inSteadyState;

    // Guard-deferred structural ops live in a netlist-owned ring pre-allocated at
    // construction (api.md §8): 0B to defer within capacity; on overflow the ring
    // GROWS once (counted in TickStats.BytesAllocated), never drops an op.
    private DeferredOp[] _deferredRing;
    private int _deferredCount;

    private StructuralEdit? _editPool;
    private bool _editActive;
    // Set when an API staging call throws inside an open Edit (a debug stale-handle
    // resolve): the using-block's Dispose must Abort, not Commit a partial batch —
    // "a half-applied edit cannot be observed" (api.md §6). See StructuralEdit.Dispose.
    private bool _editFaulted;

    // ── Node SoA ──
    private uint[] _nGen; private bool[] _nAlive; private byte[] _nRole; private ulong[] _nPart;
    private ExternalKey[] _nKey; private int[] _nIsland; private int[] _nDegree;
    private long[] _nInvalidSeq; private byte[] _nInvalidKind;
    private int _nCount; private readonly Stack<int> _nFree = new();

    // ── Component SoA ──
    private uint[] _cGen; private bool[] _cAlive; private byte[] _cKind;
    private int[] _cA, _cB, _cC, _cD; private double[] _cValue;
    // Serializable dynamic state per stateful primitive (§14): capacitor voltage
    // (a→b), inductor current (a→b), or sine phase accumulator (radians). _cStatePrev
    // carries the value at the start of the last-integrated substep — the BE capacitor
    // current readback (C·(V−V_prev)/dt) needs it after _cStateVar has been advanced.
    private double[] _cStateVar; private double[] _cStatePrev;
    private ExternalKey[] _cKey; private StateKey[] _cState;
    private DiodeParams[] _cDiode; private SineDrive[] _cSine; private bool[] _cIsSine;
    private LimitSpec[] _cLimits;
    private long[] _cInvalidSeq; private byte[] _cInvalidKind;
    private int _cCount; private readonly Stack<int> _cFree = new();

    // ── Coupler SoA ──
    private uint[] _kGen; private bool[] _kAlive; private CouplerSpec[] _kSpec; private CouplerState[] _kStateA;
    private int[] _kAPos, _kANeg, _kBPos, _kBNeg; private ExternalKey[] _kKey;
    private int _kCount; private readonly Stack<int> _kFree = new();

    // ── Probe SoA ──
    private uint[] _pGen; private bool[] _pAlive; private int[] _pA, _pB; private double[] _pT; private ExternalKey[] _pKey;
    private int _pCount; private readonly Stack<int> _pFree = new();

    // ── Island SoA ──
    private uint[] _iGen; private bool[] _iAlive; private byte[] _iStatus; private bool[] _iNeedsRebuild;
    private int[] _iNodeCount; private int _iSlotCount; private int _iAliveCount;
    private readonly Stack<int> _iFree = new();

    // ── Key maps (ExternalKey → slot). Uniqueness is a debug assert. ──
    private readonly Dictionary<ExternalKey, int> _nKeyMap = new();
    private readonly Dictionary<ExternalKey, int> _cKeyMap = new();
    private readonly Dictionary<ExternalKey, int> _kKeyMap = new();
    private readonly Dictionary<ExternalKey, int> _pKeyMap = new();

    // ── IslandChange ring (DrainChanges) ──
    private readonly IslandChange[] _chg;
    private long _chgHead; private int _chgCount; private bool _chgLost;

    // ── Ordered-ids cache (IslandTable.Ids) ──
    private IslandId[] _idsCache = System.Array.Empty<IslandId>();
    private bool _idsDirty = true;
    private int _idsCount;

    // Scratch reused by CollectDirty snapshots and Solve.
    private readonly List<int> _dirtyScratch = new();

    private readonly struct DeferredOp
    {
        public DeferredOp(CouplerId c, CouplerState s) { Coupler = c; State = s; }
        public CouplerId Coupler { get; }
        public CouplerState State { get; }
    }

    /// <param name="opts">Birth configuration — regime, wiring, partitioning,
    /// hints; fixed for the netlist's life.</param>
    public Netlist(in NetlistOptions opts)
    {
        _opts = opts;
        _netId = (ushort)System.Threading.Interlocked.Increment(ref s_netCounter);
        _debug = opts.Debug != DebugLevel.Off;
        _profileDt = opts.Profile.Dt;
        _profileKind = opts.Profile.Kind;
        _acSamplesPerCycle = opts.Profile.AcSamplesPerCycle > 0 ? opts.Profile.AcSamplesPerCycle : 20;

        var eps = opts.AdjustEpsilon > 0 ? opts.AdjustEpsilon : 1e-4;
        _ratioLo = System.Math.Exp(-eps);   // cold, construction-time only (never in the gate, §9)
        _ratioHi = System.Math.Exp(eps);

        _journal = new TopologyJournal(opts.JournalCapacity);
        _chg = new IslandChange[DefaultChangeCapacity];
        _deferredRing = new DeferredOp[opts.DeferredOpCapacity > 0 ? opts.DeferredOpCapacity : 64];

        var nc = System.Math.Max(4, opts.ExpectedNodes);
        var ic = System.Math.Max(4, opts.ExpectedIslands);
        _nGen = new uint[nc]; _nAlive = new bool[nc]; _nRole = new byte[nc]; _nPart = new ulong[nc];
        _nKey = new ExternalKey[nc]; _nIsland = new int[nc]; _nDegree = new int[nc];
        _nInvalidSeq = new long[nc]; _nInvalidKind = new byte[nc];

        var cc = System.Math.Max(8, opts.ExpectedNodes);
        _cGen = new uint[cc]; _cAlive = new bool[cc]; _cKind = new byte[cc];
        _cA = new int[cc]; _cB = new int[cc]; _cC = new int[cc]; _cD = new int[cc]; _cValue = new double[cc];
        _cStateVar = new double[cc]; _cStatePrev = new double[cc];
        _cKey = new ExternalKey[cc]; _cState = new StateKey[cc];
        _cDiode = new DiodeParams[cc]; _cSine = new SineDrive[cc]; _cIsSine = new bool[cc]; _cLimits = new LimitSpec[cc];
        _cInvalidSeq = new long[cc]; _cInvalidKind = new byte[cc];

        _kGen = new uint[4]; _kAlive = new bool[4]; _kSpec = new CouplerSpec[4]; _kStateA = new CouplerState[4];
        _kAPos = new int[4]; _kANeg = new int[4]; _kBPos = new int[4]; _kBNeg = new int[4]; _kKey = new ExternalKey[4];

        _pGen = new uint[4]; _pAlive = new bool[4]; _pA = new int[4]; _pB = new int[4]; _pT = new double[4]; _pKey = new ExternalKey[4];

        _iGen = new uint[ic]; _iAlive = new bool[ic]; _iStatus = new byte[ic]; _iNeedsRebuild = new bool[ic];
        _iNodeCount = new int[ic];

        // Numeric-runtime arenas (Netlist.Runtime.cs) sized to match the SoA.
        EnsureNodeMapCap(nc); EnsureStampCap(cc); EnsureRuntimeIslandCap(ic);
    }

    // ===================================================================== meta

    internal ushort NetId => _netId;
    internal double ProfileDt => _profileDt;
    internal uint CompGen(int slot) => _cGen[slot];

    /// <summary>Process-wide netlist id embedded in every handle (api.md §3).</summary>
    public ushort Id => _netId;

    /// <summary>The tier-0 Meta facade (api.md §4).</summary>
    public MetaFacade Meta => new(this);

    /// <summary>Per-island scheduling table (api.md §11).</summary>
    public IslandTable Islands => new(this);

    /// <summary>Committed values of the last published solve (api.md §10).</summary>
    public Solution Solution => new(this);

    /// <summary>The topology journal — compaction's sync channel (api.md §15).</summary>
    public TopologyJournal Journal => _journal;

    /// <summary>Last tick's counters (api.md §9). Reading is the readback
    /// barrier: it is a phase error (debug assert) to read while any island Step
    /// of this netlist is in flight, and the read seals the tick so the next
    /// mutation/Step starts a fresh counter window.</summary>
    public ref readonly TickStats LastTickStats
    {
        get
        {
            Debug.Assert(_stepInFlight == 0,
                "LastTickStats read while an island Step is in flight (api.md §9 phase error).");
            _tickStatsSealed = true;
            return ref _lastTickStats;
        }
    }

    // ==================================================================== verbs

    /// <summary>0B; independent voltage-source value (RHS only).</summary>
    [CostTier(1)]
    public void Drive(VSourceId id, double volts)
    {
        BeginTick();
        if (!ResolveComp(id.AsRef(), out var slot)) return;
        _cValue[slot] = volts; _cIsSine[slot] = false;
        PushVSource(slot, volts);
        MarkComponentDirty(slot);
    }

    /// <summary>0B; independent current-source value (RHS only).</summary>
    [CostTier(1)]
    public void Drive(ISourceId id, double amps)
    {
        BeginTick();
        if (!ResolveComp(id.AsRef(), out var slot)) return;
        _cValue[slot] = amps;
        PushISource(slot, amps);
        MarkComponentDirty(slot);
    }

    /// <summary>0B; phase-continuous sine driver. Amplitude/frequency/phase-offset
    /// update as a tier-1 write; the per-substep RHS evaluation reads them live and
    /// the accumulated phase is NOT reset — a frequency change keeps phase continuous
    /// (api.md §4; solver.md). Only the offset in a first Drive after construction can
    /// shift phase; steady frequency retracking never does.</summary>
    [CostTier(1)]
    public void Drive(VSourceId id, in SineDrive d)
    {
        BeginTick();
        if (!ResolveComp(id.AsRef(), out var slot)) return;
        _cSine[slot] = d; _cIsSine[slot] = true; _cValue[slot] = d.AmplitudeV;
        // Phase-continuous: _cStateVar (the accumulator) is deliberately left as-is so a
        // frequency change carries the running phase. The next substep advances from it.
        MarkComponentDirty(slot);
    }

    /// <summary>0B; resistor conductance. Degrades to a tier-0 document write on
    /// an ε-no-op (api.md §9), counted in <c>TickStats.AdjustNoOps</c>.</summary>
    [CostTier(2)]
    public void Adjust(ResistorId id, double ohms)
    {
        BeginTick();
        if (!ResolveComp(id.AsRef(), out var slot)) return;
        // No-op: the matrix conductance is NOT touched, so the document value must
        // stay at the last-APPLIED ohms too — advancing it here would (a) drift the
        // readback (BranchCurrent divides by _cValue) away from the solved matrix,
        // (b) ratchet: a sub-ε monotonic ramp would never cross the gate because each
        // call compares against the moved baseline, and (c) restamp the accumulated
        // jump at the next rebuild. Gate + readback + restamp all key off _cValue,
        // so the gate baseline is the in-matrix value by construction (api.md §9).
        if (IsResistanceNoOp(_cValue[slot], ohms)) { _lastTickStats.AdjustNoOps++; return; }
        _cValue[slot] = ohms;
        PushConductance(slot, ohms);
        MarkComponentDirty(slot);
    }

    /// <summary>0B; relay contact (in-matrix). No-op only on state equality.</summary>
    [CostTier(2)]
    public void Adjust(SwitchId id, bool closed)
    {
        BeginTick();
        if (!ResolveComp(id.AsRef(), out var slot)) return;
        var want = closed ? 1.0 : 0.0;
        if (_cValue[slot] == want) { _lastTickStats.AdjustNoOps++; return; }
        _cValue[slot] = want;
        PushSwitch(slot, closed);
        MarkComponentDirty(slot);
    }

    /// <summary>0B; capacitance. At DC a capacitor is open; in a transient island the
    /// Backward-Euler companion conductance G = C/dt is restamped (tier 2, one refactor).</summary>
    [CostTier(2)]
    public void Adjust(CapacitorId id, double farads)
    {
        BeginTick();
        if (!ResolveComp(id.AsRef(), out var slot)) return;
        _cValue[slot] = farads;
        PushStorageValue(slot);
    }

    /// <summary>0B; inductance. At DC an inductor is a fixed ~1 mΩ short; in a transient
    /// island the Backward-Euler companion conductance G = dt/L is restamped (tier 2).</summary>
    [CostTier(2)]
    public void Adjust(InductorId id, double henries)
    {
        BeginTick();
        if (!ResolveComp(id.AsRef(), out var slot)) return;
        _cValue[slot] = henries;
        PushStorageValue(slot);
    }

    /// <summary>0B; diode parameters. The Newton loop reads the new params on its next
    /// relinearization; the island is dirtied so a settled DC island re-solves.</summary>
    [CostTier(2)]
    public void Adjust(DiodeId id, in DiodeParams p)
    {
        BeginTick();
        if (!ResolveComp(id.AsRef(), out var slot)) return;
        _cDiode[slot] = p;
        MarkComponentDirty(slot);
    }

    /// <summary>0B; transformer turns ratio (coupled-row values change).</summary>
    [CostTier(2)]
    public void Adjust(TransformerId id, double turnsRatio)
    {
        BeginTick();
        if (!ResolveComp(id.AsRef(), out var slot)) return;
        _cValue[slot] = turnsRatio;
        PushTransformerRatio(slot, turnsRatio);
        MarkComponentDirty(slot);
    }

    /// <summary>Membership of an EXISTING coupler (api.md §4). Galvanic: Closed ⇒
    /// incremental merge (immediate); Open ⇒ split-rebuild coalesced to next
    /// Solve. Boundary: toggles exchange only (phase 5). Barred inside
    /// EnterSteadyState (§8): debug fails fast, release defers to the guard's
    /// Dispose and counts it.</summary>
    [CostTier(3)]
    public void Reconfigure(CouplerId id, CouplerState state)
    {
        BeginTick();
        if (_inSteadyState)
        {
            if (_debug) throw new InvalidOperationException("Reconfigure is barred inside EnterSteadyState (api.md §8).");
            DeferOp(new DeferredOp(id, state));
            _lastTickStats.DeferredStructuralOps++;
            return;
        }
        ApplyReconfigure(id, state);
    }

    /// <summary>Pooled structural edit scope (api.md §6). Nesting throws.</summary>
    [CostTier(3)]
    public StructuralEdit Edit()
    {
        if (_editActive) throw new InvalidOperationException("StructuralEdit is already open (nesting is forbidden).");
        _editFaulted = false;
        // Intentional deviation from §8's "tier-3-in-region defers to Dispose": that
        // deferral models only Reconfigure (a CouplerId+State record). Edit() returns
        // a live StructuralEdit scope that cannot be coherently deferred, so it always
        // fails fast in-region, in release too — never a silent no-op.
        if (_inSteadyState) throw new InvalidOperationException(
            "Edit() is barred inside EnterSteadyState and is intentionally non-deferrable, unlike Reconfigure (api.md §8).");
        _editActive = true;
        var e = _editPool ??= new StructuralEdit(this);
        e.Reopen();
        return e;
    }

    // ============================================================ solve & seams

    /// <summary>DC operating point (caps open, inductors ~1 mΩ; solver.md). Runs
    /// the structural rebuild (generation reissue, island lifecycle) then a
    /// forced numeric solve of every live island — the energize / lesson-start
    /// path.</summary>
    public void SolveOperatingPoint() => RunSolve(operatingPoint: true, dt: _profileDt);

    /// <summary>One game tick: rebuilds/refactors/solves the dirty scheduling
    /// units serially (small clients), publishing each island's solution. Storage
    /// islands integrate one Backward-Euler step (AC islands subcycle N substeps).</summary>
    public void Solve(in TickClock clock) => RunSolve(operatingPoint: false, dt: clock.Dt);

    // ====================================================== identity resolution

    /// <summary>0B; false on miss, never throws (api.md §4).</summary>
    public bool TryResolve(in ExternalKey key, out ComponentRef c)
    {
        if (_cKeyMap.TryGetValue(key, out var slot) && _cAlive[slot])
        {
            c = new ComponentRef((ComponentKind)_cKind[slot], slot, _cGen[slot], _netId);
            return true;
        }
        c = default; return false;
    }

    /// <summary>0B; node re-resolution across rebuilds.</summary>
    public bool TryResolveNode(in ExternalKey key, out NodeId n)
    {
        if (_nKeyMap.TryGetValue(key, out var slot) && _nAlive[slot])
        {
            n = new NodeId(slot, _nGen[slot], _netId);
            return true;
        }
        n = default; return false;
    }

    /// <summary>0B; document-stable coupler re-resolution.</summary>
    public bool TryResolveCoupler(in ExternalKey key, out CouplerId c)
    {
        if (_kKeyMap.TryGetValue(key, out var slot) && _kAlive[slot])
        {
            c = new CouplerId(slot, _kGen[slot], _netId);
            return true;
        }
        c = default; return false;
    }

    /// <summary>0B; document-stable probe re-resolution (drift resync / reload).</summary>
    public bool TryResolveProbe(in ExternalKey key, out ProbeId p)
    {
        if (_pKeyMap.TryGetValue(key, out var slot) && _pAlive[slot])
        {
            p = new ProbeId(slot, _pGen[slot], _netId);
            return true;
        }
        p = default; return false;
    }

    /// <summary>0B; gen-checked island membership.</summary>
    public IslandId IslandOf(NodeId n)
    {
        if (!ResolveNode(n, out var slot)) return default;
        var isl = _nIsland[slot];
        return new IslandId(isl, _iGen[isl], _netId);
    }

    // ==================================================== cost queries (api §9)

    /// <summary>Metadata(0) if ε-no-op, else Conductance(2).</summary>
    public Tier CostOfAdjust(ResistorId id, double ohms)
    {
        if (!ProbeComp(id.AsRef(), out var slot)) return Tier.Metadata;
        return IsResistanceNoOp(_cValue[slot], ohms) ? Tier.Metadata : Tier.Conductance;
    }

    /// <summary>Rhs(1) on no-change, else Topology(3) for a galvanic merge/split.</summary>
    public Tier CostOfReconfigure(CouplerId id, CouplerState s)
    {
        if (!ProbeCoupler(id, out var slot)) return Tier.Rhs;
        if (_kStateA[slot] == s) return Tier.Rhs;
        return _kSpec[slot].IsGalvanic ? Tier.Topology : Tier.Rhs;
    }

    // ============================================ serialization / drift (phase)

    /// <summary>Drift backstop level 1 (api.md §14). Phase 6/7.</summary>
    public ulong Fingerprint(IslandId island, FingerprintScope scope)
        => throw Deferred("Fingerprint", "6");

    /// <summary>Slot-preserving whole-netlist save (api.md §14). Phase 6.</summary>
    public void SaveCanonical(System.Buffers.IBufferWriter<byte> dst)
        => throw Deferred("SaveCanonical", "6");

    /// <summary>Key-sorted minimal save (api.md §14). Phase 6.</summary>
    public void SaveNormalized(System.Buffers.IBufferWriter<byte> dst)
        => throw Deferred("SaveNormalized", "6");

    /// <summary>Load a slot-preserving save (api.md §14). Phase 6.</summary>
    public static Netlist FromCanonical(ReadOnlySpan<byte> src, in NetlistOptions opts)
        => throw Deferred("FromCanonical", "6");

    // ============================================================ legibility

    /// <summary>Enter the no-structural-change region (api.md §8). 0B.</summary>
    public SteadyStateGuard EnterSteadyState()
    {
        _inSteadyState = true;
        return new SteadyStateGuard(this);
    }

    // Append a release-deferred structural op; grow once on overflow (counted,
    // never dropped — api.md §8). A plain append-and-drain buffer: it is drained in
    // full at ExitSteadyState and never wraps, so no ring head is needed.
    private void DeferOp(in DeferredOp op)
    {
        if (_deferredCount == _deferredRing.Length)
        {
            var len = _deferredRing.Length;
            System.Array.Resize(ref _deferredRing, len * 2);
            _lastTickStats.BytesAllocated += (long)len * 16;   // one-time growth, outside the 0B claim (§8)
        }
        _deferredRing[_deferredCount++] = op;
    }

    internal void ExitSteadyState()
    {
        _inSteadyState = false;
        // Apply release-deferred structural ops in order (never dropped; api.md §8).
        for (var i = 0; i < _deferredCount; i++)
            ApplyReconfigure(_deferredRing[i].Coupler, _deferredRing[i].State);
        _deferredCount = 0;
    }

    /// <summary>A tier-≤2 capability object for device Tick (api.md §18). Legal
    /// at any time (capability wrapper; single-writer-per-island governs writes).</summary>
    public DeviceTickContext TickContext(double dt) => new(this, dt);

    private static NotSupportedException Deferred(string member, string phase)
        => new($"{member} is phase {phase}; not implemented in the document stage.");
}
