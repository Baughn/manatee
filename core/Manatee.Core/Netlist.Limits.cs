using System;

namespace Manatee.Core;

// Post-solve limit evaluation (api.md §12; solver.md Limits). The solver NEVER
// mutates the circuit on a limit (R7): each solved island is SCANNED after it
// publishes, per-component observables (|I|, |V|, P) are compared against the
// component's LimitSpec, and a slow-overload i²t thermal accumulator integrates —
// events land in a fixed per-island ring (overflow COUNTED, never grown). Popping
// the fuse is the client's Adjust/Reconfigure on a following tick.
//
// ── i²t THERMAL MODEL (units documented; hand-verified by the fuse test) ──
// A per-component accumulator A [A²·s] tracks the melting integral relative to the
// continuous current rating (LimitSpec.MaxCurrent, in A):
//
//   over  rating (|I| >  rating):  A += (I² − rating²)·dt        [heating]
//   under rating (|I| <= rating):  A -= A·(dt/Tau)  (floored 0)  [cooling, τ = Tau s]
//
// A ThermalI2t event fires on the RISING EDGE A ≥ MeltI2t (I2tFraction = A/MeltI2t);
// the accumulator is NOT reset (the element stays "melted" until the client acts).
// Worked example (the fuse test): rating 10 A, MeltI2t 300 A²·s, at |I| = 20 A the
// heating rate is (400 − 100) = 300 A²/s, so the fuse pops at t = 300/300 = 1.0 s.
public sealed partial class Netlist
{
    private const int LimitRingCapacity = 16;

    // ── Per-component i²t state (parallel to the component SoA). ──
    private double[] _cI2t = new double[8];      // melting-integral accumulator (A²·s)
    private bool[] _cI2tTripped = new bool[8];   // rising-edge latch for the ThermalI2t event

    // ── Per-island limit-event ring (island-slot indexed; buffers lazily allocated). ──
    private LimitEvent[]?[] _iLimitRing = new LimitEvent[4][];
    private int[] _iLimitHead = new int[4];      // next write index (mod capacity)
    private int[] _iLimitCount = new int[4];     // live entries in the ring
    private long[] _iLimitOverflow = new long[4];// events dropped since last drain (counted)

    // Tick index of the last per-SUBSTEP limit evaluation for an island. A transient
    // (subcycled-AC / storage) island evaluates its limits inside the substep loop with
    // substepDt (a true i²t integral over the cycle, instantaneous checks see the peak);
    // the per-TICK limits pass then skips it (matching this stamp to the tick) so it is
    // not counted twice. −1 = never evaluated per substep (a static island; the per-tick
    // pass owns it). Monotonic tick indices make the stamp self-clearing across slots.
    private long[] _iLimitEvalTick = InitEvalTick(4);

    private static long[] InitEvalTick(int n)
    {
        var a = new long[n];
        for (var i = 0; i < n; i++) a[i] = -1;
        return a;
    }

    // Ambient-adjustable i²t threshold seam (phase 7): the per-component MeltI2t is
    // scaled by this factor before the trip test, so a hot room lowers the melt
    // point without touching any LimitSpec. 1.0 = nominal (300 K). Cold tier-0 write.
    private double _i2tThresholdScale = 1.0;

    private void EnsureLimitCap(int min)
    {
        if (_cI2t.Length >= min) return;
        var cap = Math.Max(min, _cI2t.Length * 2);
        Array.Resize(ref _cI2t, cap); Array.Resize(ref _cI2tTripped, cap);
    }

    private void EnsureIslandLimitCap(int min)
    {
        if (_iLimitRing.Length >= min) return;
        var cap = Math.Max(min, _iLimitRing.Length * 2);
        Array.Resize(ref _iLimitRing, cap); Array.Resize(ref _iLimitHead, cap);
        Array.Resize(ref _iLimitCount, cap); Array.Resize(ref _iLimitOverflow, cap);
    }

    // The per-substep evaluation stamp is grown eagerly with island capacity (from
    // EnsureIslandCap) — NOT lazily on the substep hot path — so stamping it never
    // allocates inside a zero-alloc region (api.md §21). The ring buffers above stay
    // lazy (a limit event firing is exceptional and already off the 0B guarantee).
    private void EnsureLimitEvalTickCap(int cap)
    {
        if (_iLimitEvalTick.Length >= cap) return;
        var oldLen = _iLimitEvalTick.Length;
        Array.Resize(ref _iLimitEvalTick, cap);
        for (var i = oldLen; i < cap; i++) _iLimitEvalTick[i] = -1;
    }

    /// <summary>Phase-7 ambient seam (api.md §12): scale every component's i²t melt
    /// threshold (1.0 = nominal). A room hotter than ambient trips fuses/cables
    /// sooner without editing any LimitSpec. Cold; affects the next evaluation.</summary>
    public void SetI2tThresholdScale(double scale)
        => _i2tThresholdScale = scale > 0.0 ? scale : 1.0;

    // True iff `islandSlot` was already limit-scanned this tick from inside a substep
    // loop (so the per-tick pass must not scan it again). Bounds-safe: an unsized slot
    // was never per-substep-evaluated → false → the per-tick pass owns it.
    private bool LimitsEvaluatedThisTick(int islandSlot, long tickIndex)
        => islandSlot < _iLimitEvalTick.Length && _iLimitEvalTick[islandSlot] == tickIndex;

    private static bool HasLimits(in LimitSpec s)
        => s.MaxCurrent > 0.0 || s.MaxVoltage > 0.0 || s.MaxPower > 0.0 || s.Thermal.MeltI2t > 0.0;

    // Scan one just-published island for limit violations. Called from the numeric
    // solve tail (solo) and the unit publish loop, once per tick, with that tick's
    // dt and index. Zero heap allocation once a ring buffer exists (a struct store
    // per event + scalar i²t arithmetic); islands with no limited components do
    // nothing. The solver state is untouched — this only READS the solution.
    private void EvaluateIslandLimits(int islandSlot, double dt, long tickIndex)
    {
        if (dt <= 0.0) return;   // operating point / DC init: no time integral
        // Stamp the evaluation so the per-tick limits pass skips an island already
        // scanned this tick from inside the substep loop (no double-integration). The
        // stamp array is pre-sized with island capacity (EnsureIslandCap), so this is
        // a plain store — never an allocating resize on the substep hot path.
        _iLimitEvalTick[islandSlot] = tickIndex;
        // Iterate only THIS island's members (island-independence): the full-arena scan
        // made a solo island's per-tick cost scale with the whole netlist. Fall back to
        // the arena only when there is no live runtime to name the members.
        var rt = (uint)islandSlot < (uint)_iRuntime.Length ? _iRuntime[islandSlot] : null;
        if (rt is not null && !_iRuntimeStale[islandSlot])
            for (var i = 0; i < rt.MemberCount; i++) EvaluateLimitComp(islandSlot, rt.MemberComps[i], dt, tickIndex);
        else
            for (var c = 0; c < _cCount; c++)
                if (_cAlive[c] && _nIsland[_cA[c]] == islandSlot) EvaluateLimitComp(islandSlot, c, dt, tickIndex);
    }

    private void EvaluateLimitComp(int islandSlot, int c, double dt, long tickIndex)
    {
            ref readonly var spec = ref _cLimits[c];
            if (!HasLimits(spec)) return;

            var current = BranchCurrent(c);
            var absI = current < 0.0 ? -current : current;
            var v = NodePotential(_cA[c]) - (_cB[c] >= 0 ? NodePotential(_cB[c]) : 0.0);
            var absV = v < 0.0 ? -v : v;
            var power = v * current;
            var absP = power < 0.0 ? -power : power;

            if (spec.MaxCurrent > 0.0 && absI > spec.MaxCurrent)
                RecordLimit(islandSlot, c, LimitKind.OverCurrent, absI, spec.MaxCurrent, 0.0, dt, tickIndex);
            if (spec.MaxVoltage > 0.0 && absV > spec.MaxVoltage)
                RecordLimit(islandSlot, c, LimitKind.OverVoltage, absV, spec.MaxVoltage, 0.0, dt, tickIndex);
            if (spec.MaxPower > 0.0 && absP > spec.MaxPower)
                RecordLimit(islandSlot, c, LimitKind.OverPower, absP, spec.MaxPower, 0.0, dt, tickIndex);

            // i²t slow-overload accumulator (needs a current rating to be meaningful).
            if (spec.Thermal.MeltI2t > 0.0 && spec.MaxCurrent > 0.0)
            {
                var rating = spec.MaxCurrent;
                var acc = _cI2t[c];
                if (absI > rating) acc += (absI * absI - rating * rating) * dt;
                else if (spec.Thermal.Tau > 0.0) { acc -= acc * (dt / spec.Thermal.Tau); if (acc < 0.0) acc = 0.0; }
                _cI2t[c] = acc;

                var melt = spec.Thermal.MeltI2t * _i2tThresholdScale;
                if (acc >= melt)
                {
                    if (!_cI2tTripped[c])
                    {
                        _cI2tTripped[c] = true;
                        RecordLimit(islandSlot, c, LimitKind.ThermalI2t, acc, melt, acc / melt, dt, tickIndex);
                    }
                }
                else if (acc < melt * 0.5) _cI2tTripped[c] = false;   // hysteretic re-arm once cooled well below melt
            }
    }

    private void RecordLimit(int islandSlot, int compSlot, LimitKind kind,
        double observed, double threshold, double i2tFraction, double substepTime, long tickIndex)
    {
        EnsureIslandLimitCap(_iSlotCount);
        var ring = _iLimitRing[islandSlot];
        if (ring is null) { ring = new LimitEvent[LimitRingCapacity]; _iLimitRing[islandSlot] = ring; }

        if (_iLimitCount[islandSlot] >= LimitRingCapacity) { _iLimitOverflow[islandSlot]++; return; }

        var idx = _iLimitHead[islandSlot];
        ring[idx] = new LimitEvent
        {
            Source = new ComponentRef((ComponentKind)_cKind[compSlot], compSlot, _cGen[compSlot], _netId),
            Kind = kind, Observed = observed, Threshold = threshold,
            I2tFraction = i2tFraction, SubstepTime = substepTime, TickIndex = tickIndex,
        };
        _iLimitHead[islandSlot] = (idx + 1) % LimitRingCapacity;
        _iLimitCount[islandSlot]++;
    }

    // Drain (api.md §12): oldest-first, up to the caller span; a too-small span
    // drains partially (call again until 0). Overflow is counted, not surfaced as a
    // grown ring — R9 degrades legibly.
    internal int DrainLimitEventsImpl(int slot, uint gen, Span<LimitEvent> into)
        => DrainLimitEventsImpl(slot, gen, into, out _);

    internal int DrainLimitEventsImpl(int slot, uint gen, Span<LimitEvent> into, out long dropped)
    {
        dropped = 0;
        if (!IslandGenLive(slot, gen)) return 0;
        if ((uint)slot >= (uint)_iLimitRing.Length) return 0;
        var ring = _iLimitRing[slot];
        if (ring is null) return 0;
        var count = _iLimitCount[slot];
        var n = Math.Min(into.Length, count);
        var start = ((_iLimitHead[slot] - count) % LimitRingCapacity + LimitRingCapacity) % LimitRingCapacity;
        for (var i = 0; i < n; i++) into[i] = ring[(start + i) % LimitRingCapacity];
        _iLimitCount[slot] -= n;
        // R9 "degrades legibly" (api.md §12): report how many events the fixed ring
        // dropped, and clear the counter once the client has caught up (the ring is
        // empty) — the "since last drain" contract in the field's own comment.
        dropped = _iLimitOverflow[slot];
        if (_iLimitCount[slot] == 0) _iLimitOverflow[slot] = 0;
        return n;
    }
}
