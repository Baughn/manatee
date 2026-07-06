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

    // ── Instantaneous-event coalescing (api.md §12 ruling 2026-07-06): at most ONE
    // OverCurrent/OverVoltage/OverPower event per (component, kind) per tick, carrying
    // the WORST observed substep value. Without this, a sustained AC overload on a
    // subcycled island emits N events/tick, saturating the 16-slot ring with one
    // component and dropping everyone else's events — the R9 legibility failure the
    // ruling closed. Slot-parallel, 0 B on the hot path: [c·3 + kind] holds the tick a
    // (component, kind) event was last recorded at and its ring position, so a later
    // substep of the same tick updates the ring entry in place. ThermalI2t keeps its
    // edge-latch semantics and never routes through this. ──
    private const int CoalescedKinds = 3;        // OverCurrent, OverVoltage, OverPower
    private long[] _limSeenTick = InitEvalTick(8 * CoalescedKinds);
    private int[] _limRingPos = new int[8 * CoalescedKinds];

    // ── Thermal ENVELOPES (api.md §12/§19 "i²t envelopes are Pareto sets", ruled
    // 2026-07-06). A component may carry 1..k (rating, melt, tau) pairs registered via
    // Meta.SetThermalEnvelope — flat SoA pool storage, one accumulator + trip latch per
    // pair; the component trips when ANY pair trips, and the LimitEvent names the pair
    // (PairIndex) so the reduction layer can attribute it to the culprit segment. A
    // component with a registered envelope evaluates thermal limits from the envelope
    // EXCLUSIVELY (its LimitSpec.Thermal is ignored); plain LimitSpec clients have
    // _cEnvCount == 0 and keep the exact single-accumulator path — zero change.
    //
    // Pool discipline: blocks are bump-allocated; re-registration reuses the block in
    // place when the new pair count fits its capacity (same count preserves the
    // accumulators BY INDEX — an ambient re-derate keeps partial melt; a count change
    // resets them, documented in api.md §19). Registration is cold/shape-time; the
    // per-substep evaluation is pure flat-array arithmetic, 0 B.
    //
    // KNOWN, ACCEPTED GROWTH (adjudicated 2026-07-06): a re-registration that OUTGROWS
    // its block strands the old range, a clear keeps the block tethered via _cEnvCap,
    // and only FromCanonical repacks the pool (_envUsed = 0). Under long structural
    // churn the five pool arrays therefore grow monotonically — bounded by
    // (count-growing re-registrations) × k where k is small (distinct materials per
    // chain) and registration is cold, so no tick loop touches this. Revisit with a
    // capacity-bucketed free list or a compaction pass (trigger: _envUsed ≳ 2× live
    // pair total) if profiling ever shows it; do NOT "fix" it on the hot path.
    private int[] _cEnvStart = new int[8];       // offset into the flat pool (undefined when count == 0)
    private int[] _cEnvCount = new int[8];       // live pair count; 0 = no envelope (plain spec path)
    private int[] _cEnvCap = new int[8];         // block capacity at allocation (for in-place reuse)
    private double[] _envRating = new double[8];
    private double[] _envMelt = new double[8];
    private double[] _envTau = new double[8];
    private double[] _envAcc = new double[8];
    private bool[] _envTripped = new bool[8];
    private int _envUsed;

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
        Array.Resize(ref _cEnvStart, cap); Array.Resize(ref _cEnvCount, cap); Array.Resize(ref _cEnvCap, cap);
        var oldLen = _limSeenTick.Length;
        Array.Resize(ref _limSeenTick, cap * CoalescedKinds);
        Array.Resize(ref _limRingPos, cap * CoalescedKinds);
        for (var i = oldLen; i < _limSeenTick.Length; i++) _limSeenTick[i] = -1;
    }

    private void EnsureEnvPoolCap(int min)
    {
        if (_envRating.Length >= min) return;
        var cap = Math.Max(min, _envRating.Length * 2);
        Array.Resize(ref _envRating, cap); Array.Resize(ref _envMelt, cap); Array.Resize(ref _envTau, cap);
        Array.Resize(ref _envAcc, cap); Array.Resize(ref _envTripped, cap);
    }

    // Meta.SetThermalEnvelope seam (api.md §4/§12): register/replace a component's
    // thermal envelope. Empty span clears it (back to the plain LimitSpec path).
    // Same-count re-registration updates thresholds in place and PRESERVES the
    // per-pair accumulators by index (an ambient re-derate keeps partial melt);
    // a count change re-allocates and resets them (api.md §19). Cold tier-0 write.
    internal void SetThermalEnvelopeImpl(in ComponentRef c, ReadOnlySpan<I2tPair> pairs)
    {
        if (!ResolveComp(c, out var slot)) return;
        EnsureLimitCap(slot + 1);
        if (pairs.Length == 0) { _cEnvCount[slot] = 0; return; }

        var sameCount = _cEnvCount[slot] == pairs.Length;
        int start;
        if (_cEnvCap[slot] >= pairs.Length)
        {
            start = _cEnvStart[slot];              // reuse the block in place
        }
        else
        {
            start = _envUsed;                      // bump-allocate a fresh block
            _envUsed += pairs.Length;
            EnsureEnvPoolCap(_envUsed);
            _cEnvStart[slot] = start;
            _cEnvCap[slot] = pairs.Length;
            sameCount = false;
        }
        for (var i = 0; i < pairs.Length; i++)
        {
            _envRating[start + i] = pairs[i].RatingAmps;
            _envMelt[start + i] = pairs[i].MeltI2t;
            _envTau[start + i] = pairs[i].Tau;
            if (!sameCount) { _envAcc[start + i] = 0.0; _envTripped[start + i] = false; }
        }
        _cEnvCount[slot] = pairs.Length;
    }

    /// <summary>Number of live envelope pairs on a component (0 = plain spec path).
    /// Test/diagnostic seam.</summary>
    internal int ThermalEnvelopeCount(in ComponentRef c)
        => ProbeComp(c, out var slot) ? _cEnvCount[slot] : 0;

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
            var envCount = c < _cEnvCount.Length ? _cEnvCount[c] : 0;
            if (!HasLimits(spec) && envCount == 0) return;

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

            // i²t slow-overload accumulation. A registered thermal ENVELOPE (api.md
            // §12/§19 Pareto-set ruling) evaluates one accumulator PER PAIR against
            // that pair's OWN rating — exactly what the raw per-segment graph would do,
            // since a series chain shares one current — and supersedes the spec's
            // Thermal path entirely. Plain components keep the single accumulator.
            if (envCount > 0)
            {
                var start = _cEnvStart[c];
                for (var p = 0; p < envCount; p++)
                {
                    var rating = _envRating[start + p];
                    var meltRaw = _envMelt[start + p];
                    if (!(rating > 0.0) || !(meltRaw > 0.0)) continue;
                    var acc = _envAcc[start + p];
                    if (absI > rating) acc += (absI * absI - rating * rating) * dt;
                    else
                    {
                        var tau = _envTau[start + p];
                        if (tau > 0.0) { acc -= acc * (dt / tau); if (acc < 0.0) acc = 0.0; }
                    }
                    _envAcc[start + p] = acc;

                    var melt = meltRaw * _i2tThresholdScale;
                    if (acc >= melt)
                    {
                        if (!_envTripped[start + p])
                        {
                            _envTripped[start + p] = true;
                            RecordLimit(islandSlot, c, LimitKind.ThermalI2t, acc, melt, acc / melt, dt, tickIndex, p);
                        }
                    }
                    else if (acc < melt * 0.5) _envTripped[start + p] = false;   // hysteretic re-arm
                }
            }
            else if (spec.Thermal.MeltI2t > 0.0 && spec.MaxCurrent > 0.0)
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
        double observed, double threshold, double i2tFraction, double substepTime, long tickIndex,
        int pairIndex = 0)
    {
        EnsureIslandLimitCap(_iSlotCount);
        var ring = _iLimitRing[islandSlot];
        if (ring is null) { ring = new LimitEvent[LimitRingCapacity]; _iLimitRing[islandSlot] = ring; }

        // Coalesce instantaneous kinds to one event per (component, kind) per tick
        // (api.md §12 ruling): a repeat within the tick updates the live ring entry in
        // place with the worst observed value instead of emitting again. ThermalI2t is
        // edge-latched by its accumulator and never re-enters within a tick.
        var li = -1;
        if (kind != LimitKind.ThermalI2t)
        {
            li = compSlot * CoalescedKinds + (int)kind;
            if (_limSeenTick[li] == tickIndex)
            {
                var pos = _limRingPos[li];
                if (pos < 0) return;   // this tick's event was already counted as dropped
                // Update only if the recorded slot is still LIVE in the ring (not
                // drained mid-tick and overwritten) and really is this tick's event.
                var count = _iLimitCount[islandSlot];
                var startIdx = ((_iLimitHead[islandSlot] - count) % LimitRingCapacity + LimitRingCapacity) % LimitRingCapacity;
                var live = ((pos - startIdx + LimitRingCapacity) % LimitRingCapacity) < count;
                if (live && ring[pos].TickIndex == tickIndex && ring[pos].Kind == kind
                    && ring[pos].Source.Slot == compSlot)
                {
                    if (observed > ring[pos].Observed)
                    {
                        ring[pos].Observed = observed;
                        ring[pos].SubstepTime = substepTime;
                    }
                    return;
                }
                // Drained mid-tick: fall through and emit a fresh event.
            }
        }

        if (_iLimitCount[islandSlot] >= LimitRingCapacity)
        {
            _iLimitOverflow[islandSlot]++;
            // One notional (coalesced) event per (component, kind) per tick ⇒ one drop.
            if (li >= 0) { _limSeenTick[li] = tickIndex; _limRingPos[li] = -1; }
            return;
        }

        var idx = _iLimitHead[islandSlot];
        ring[idx] = new LimitEvent
        {
            Source = new ComponentRef((ComponentKind)_cKind[compSlot], compSlot, _cGen[compSlot], _netId),
            Kind = kind, Observed = observed, Threshold = threshold,
            I2tFraction = i2tFraction, SubstepTime = substepTime, TickIndex = tickIndex,
            PairIndex = pairIndex,
        };
        _iLimitHead[islandSlot] = (idx + 1) % LimitRingCapacity;
        _iLimitCount[islandSlot]++;
        if (li >= 0) { _limSeenTick[li] = tickIndex; _limRingPos[li] = idx; }
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
