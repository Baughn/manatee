using System;
using System.Buffers.Binary;

namespace Manatee.Core.Devices;

/// <summary>
/// R18 constant-power load, adapted to the linear MNA solve as a conductance
/// <c>G = P/V_prev²</c> clamped to a legal range, with the full R18 stability
/// stack: brownout with HYSTERESIS, per-device STAGGERED rejoin (deterministic
/// from the StateKey — no RNG), recloser-style LOCKOUT after k brownouts in a
/// window (manual <see cref="Reset"/>), and an across-tick ENERGY LEDGER so an
/// oscillating advertised power cannot pump free energy out of the solve.
///
/// <para><b>Why a ledger.</b> Linearising a constant-power load at last tick's
/// voltage means the power actually drawn this tick is <c>G·V_now² =
/// P·(V_now/V_prev)²</c>. If the load makes its own voltage oscillate, the average
/// of <c>(V_now/V_prev)²</c> can exceed 1 and the load would harvest MORE than it
/// advertised — a free-energy pump. The ledger closes it: cumulative over-draw is
/// banked as a debt (J, floored ≥ 0), and the deliverable this tick is
/// <c>advertised − debt/dt</c>. Over any window the delivered energy is bounded by
/// the advertised energy plus at most one tick of slack (the pump test asserts
/// exactly this against a square-wave advertised power).</para>
///
/// <para><b>Precondition: FIXED timestep.</b> The ledger settles the PREVIOUS
/// interval's over-draw (<c>(actualP − grantedPrev)·dt</c>) and converts the debt
/// back to a power (<c>debt/dt</c>) both using the CURRENT tick's <c>dt</c>. That is
/// exact — and the "advertised + one tick" bound is tight — only when the device tick
/// interval is constant (the sole mode the codebase drives,
/// <c>SolverProfile.Mixed(dt)</c>). Under a varying <c>dt</c> the previous interval
/// would be settled against the wrong duration; a future variable-step driver must
/// store the granting tick's <c>dt</c> and settle the previous interval with it.</para>
///
/// <para><b>Component ordinals (key-allocation contract, api.md §18):</b>
/// 0 = the load conductance resistor. Terminals: 0 = positive (live) port,
/// 1 = negative (return) port.</para>
/// </summary>
public sealed class AdaptedLoad : Device
{
    // ── modes ──
    private const byte Live = 0, BrownedOut = 1, LockedOut = 2;

    // ── config (cold) ──
    private readonly double _advertisedNominal;
    private readonly double _gMin, _gMax;          // legal conductance range [S]
    private readonly double _vLow, _vHigh;         // brownout hysteresis thresholds [V]
    private readonly double _vFloor;               // V clamp for the G = P/V² linearisation
    private readonly int _lockoutK;                // brownouts-in-window → lockout
    private readonly long _windowTicks;            // lockout counting window
    private readonly int _staggerBase, _staggerSpread;

    // ── built handles ──
    private ResistorId _r;
    private NodeId _pos, _neg;

    // ── serialized runtime state ──
    private byte _mode;
    private int _rejoinCountdown;
    private int _brownoutCount;
    private long _windowStart;
    private long _tick;
    private double _debtJ;         // cumulative over-draw energy, floored ≥ 0 [J]
    private double _grantedPrev;   // power the load was granted last tick [W]
    private double _appliedG;      // conductance stamped last tick [S]
    private int _staggerDelay;     // per-device deterministic rejoin delay [ticks]

    // ── per-tick input ──
    private double _advertised;

    /// <param name="advertisedWatts">Nominal constant power the load requests.</param>
    /// <param name="gMin">Legal conductance floor (also the shed/brownout conductance).</param>
    /// <param name="gMax">Legal conductance ceiling (inrush / short-circuit clamp).</param>
    /// <param name="brownoutLowVolts">Drop out below this port voltage.</param>
    /// <param name="brownoutHighVolts">Rejoin only above this (hysteresis; must be ≥ low).</param>
    /// <param name="lockoutCount">Brownouts within <paramref name="lockoutWindowTicks"/> before lockout.</param>
    public AdaptedLoad(double advertisedWatts, double gMin = 1e-9, double gMax = 1e3,
        double brownoutLowVolts = 0.0, double brownoutHighVolts = 0.0,
        int lockoutCount = 3, long lockoutWindowTicks = 100,
        int staggerBaseTicks = 2, int staggerSpreadTicks = 8, double voltageFloor = 1e-3)
    {
        if (!(gMin > 0.0)) throw new ArgumentOutOfRangeException(nameof(gMin));
        if (!(gMax >= gMin)) throw new ArgumentOutOfRangeException(nameof(gMax));
        _advertisedNominal = advertisedWatts >= 0.0 ? advertisedWatts : 0.0;
        _advertised = _advertisedNominal;
        _gMin = gMin; _gMax = gMax;
        _vLow = brownoutLowVolts; _vHigh = brownoutHighVolts > brownoutLowVolts ? brownoutHighVolts : brownoutLowVolts;
        _vFloor = voltageFloor > 0.0 ? voltageFloor : 1e-3;
        _lockoutK = lockoutCount > 0 ? lockoutCount : int.MaxValue;
        _windowTicks = lockoutWindowTicks > 0 ? lockoutWindowTicks : long.MaxValue;
        _staggerBase = staggerBaseTicks < 0 ? 0 : staggerBaseTicks;
        _staggerSpread = staggerSpreadTicks < 1 ? 1 : staggerSpreadTicks;
    }

    private static readonly int[] ReturnIdx = { 1 };
    public override TerminalSpec Terminals => new(2, ReturnIdx);

    /// <summary>Set the advertised (requested) constant power for the coming tick.</summary>
    public void SetAdvertised(double watts) => _advertised = watts >= 0.0 ? watts : 0.0;

    /// <summary>Manual recloser reset after a lockout (R18). No-op unless locked out.</summary>
    public void Reset()
    {
        if (_mode == LockedOut) { _mode = Live; _brownoutCount = 0; _rejoinCountdown = 0; }
    }

    /// <summary>Current operating mode (0 live, 1 browned-out, 2 locked-out) — test/UI seam.</summary>
    public int Mode => _mode;

    /// <summary>Cumulative banked over-draw energy [J] — test/UI seam.</summary>
    public double DebtJoules => _debtJ;

    /// <summary>The load's conductance-resistor handle (power/current readback seam).</summary>
    public ResistorId ConductanceResistor => _r;

    /// <summary>The per-device deterministic rejoin delay [ticks] derived from the
    /// StateKey (no RNG) — the stagger that keeps a fleet from re-energising in lockstep.</summary>
    public int RejoinDelayTicks => _staggerDelay;

    protected override void Build(StructuralEdit e, ReadOnlySpan<NodeId> terminals,
                                  in ExternalKey baseKey, in StateKey state)
    {
        _pos = terminals[0];
        _neg = terminals[1];
        _appliedG = _gMin;
        _grantedPrev = 0.0;
        _mode = Live;
        _debtJ = 0.0;
        _tick = 0; _windowStart = 0; _brownoutCount = 0; _rejoinCountdown = 0;

        // Deterministic staggered rejoin delay from the StateKey (no RNG). A cheap
        // avalanche mix of the 128-bit key → an offset in [base, base+spread).
        var h = state.Lo * 0x9E3779B97F4A7C15UL ^ (state.Hi + 0x165667B19E3779F9UL);
        h ^= h >> 29; h *= 0xBF58476D1CE4E5B9UL; h ^= h >> 32;
        _staggerDelay = _staggerBase + (int)(h % (ulong)_staggerSpread);

        _r = e.AddResistor(_pos, _neg, 1.0 / _gMin, baseKey.Derive(0));   // ordinal 0
    }

    public override void Tick(in DeviceTickContext ctx)
    {
        _tick++;
        var dt = ctx.Dt;
        var v = ctx.Previous.Voltage(_pos) - ctx.Previous.Voltage(_neg);
        var absV = v < 0.0 ? -v : v;

        // ── settle the ledger from last tick's result ──
        // Power actually drawn last tick at the stamped conductance: P = G·V².
        var actualP = _appliedG * v * v;
        // Settle the previous interval. Exact under a FIXED dt (see the ledger note in
        // the class doc); a variable-step driver would need the granting tick's own dt.
        _debtJ += (actualP - _grantedPrev) * dt;   // over-draw accumulates
        if (_debtJ < 0.0) _debtJ = 0.0;             // never bank under-draw as spendable credit

        // ── brownout state machine (hysteresis + stagger + lockout) ──
        switch (_mode)
        {
            case Live:
                if (absV < _vLow) EnterBrownout();
                break;
            case BrownedOut:
                // Rejoin only after the port has stayed above V_high for the full
                // (per-device, deterministic) stagger delay — otherwise re-arm.
                if (absV >= _vHigh)
                {
                    if (--_rejoinCountdown <= 0) _mode = Live;
                }
                else _rejoinCountdown = _staggerDelay;
                break;
            case LockedOut:
                break;   // until Reset()
        }

        // ── deliverable power (energy ledger) ──
        double granted;
        if (_mode != Live) granted = 0.0;
        else
        {
            granted = _advertised - _debtJ / dt;
            if (granted < 0.0) granted = 0.0;
        }

        // conductance from granted power at V_prev, clamped to the legal range.
        var vRef = absV < _vFloor ? _vFloor : absV;
        var g = granted / (vRef * vRef);
        if (_mode != Live) g = _gMin;   // shed to the floor while browned/locked out
        if (g < _gMin) g = _gMin;
        else if (g > _gMax) g = _gMax;

        ctx.Adjust(_r, 1.0 / g);
        _appliedG = g;
        _grantedPrev = _mode == Live ? granted : 0.0;
    }

    private void EnterBrownout()
    {
        // Sliding-window brownout count → recloser lockout after k in a window.
        if (_tick - _windowStart > _windowTicks) { _windowStart = _tick; _brownoutCount = 0; }
        _brownoutCount++;
        if (_brownoutCount >= _lockoutK) { _mode = LockedOut; return; }
        _mode = BrownedOut;
        _rejoinCountdown = _staggerDelay;
    }

    // ── state serialization (all fields that influence a future Tick) ──
    public override int StateSize => 1 + 4 + 4 + 8 + 8 + 8 + 8 + 8 + 4;   // 53 bytes

    public override void SaveState(Span<byte> dst)
    {
        dst[0] = _mode;
        BinaryPrimitives.WriteInt32LittleEndian(dst.Slice(1), _rejoinCountdown);
        BinaryPrimitives.WriteInt32LittleEndian(dst.Slice(5), _brownoutCount);
        BinaryPrimitives.WriteInt64LittleEndian(dst.Slice(9), _windowStart);
        BinaryPrimitives.WriteInt64LittleEndian(dst.Slice(17), _tick);
        BinaryPrimitives.WriteInt64LittleEndian(dst.Slice(25), BitConverter.DoubleToInt64Bits(_debtJ));
        BinaryPrimitives.WriteInt64LittleEndian(dst.Slice(33), BitConverter.DoubleToInt64Bits(_grantedPrev));
        BinaryPrimitives.WriteInt64LittleEndian(dst.Slice(41), BitConverter.DoubleToInt64Bits(_appliedG));
        BinaryPrimitives.WriteInt32LittleEndian(dst.Slice(49), _staggerDelay);
    }

    public override void RestoreState(ReadOnlySpan<byte> src)
    {
        _mode = src[0];
        _rejoinCountdown = BinaryPrimitives.ReadInt32LittleEndian(src.Slice(1));
        _brownoutCount = BinaryPrimitives.ReadInt32LittleEndian(src.Slice(5));
        _windowStart = BinaryPrimitives.ReadInt64LittleEndian(src.Slice(9));
        _tick = BinaryPrimitives.ReadInt64LittleEndian(src.Slice(17));
        _debtJ = BitConverter.Int64BitsToDouble(BinaryPrimitives.ReadInt64LittleEndian(src.Slice(25)));
        _grantedPrev = BitConverter.Int64BitsToDouble(BinaryPrimitives.ReadInt64LittleEndian(src.Slice(33)));
        _appliedG = BitConverter.Int64BitsToDouble(BinaryPrimitives.ReadInt64LittleEndian(src.Slice(41)));
        _staggerDelay = BinaryPrimitives.ReadInt32LittleEndian(src.Slice(49));
    }
}
