namespace Manatee.Core;

/// <summary>Change-cost tier. A verb's tier is what a non-EE reviewer audits a
/// client's cost profile by (api.md §4). Not five values: <c>Reconfigure</c>
/// reports as <see cref="Topology"/>.</summary>
public enum Tier : byte { Metadata = 0, Rhs = 1, Conductance = 2, Topology = 3 }

/// <summary>Fingerprint granularity (api.md §14). <see cref="Full"/> adds tier-0
/// envelopes / probe weights.</summary>
public enum FingerprintScope : byte { Structural, Full }

/// <summary>Galvanic/boundary coupler membership state.</summary>
public enum CouplerState : byte { Open, Closed }

/// <summary>Deterministic per-tick clock — tick index for replay and dt for AC
/// phase continuity (api.md §4).</summary>
public readonly record struct TickClock(long TickIndex, double Dt)
{
    /// <summary>Next tick with a given dt. Convenience for client tick loops.</summary>
    public TickClock Next(double dt) => new(TickIndex + 1, dt);
}

/// <summary>Phase-continuous sine drive descriptor (api.md §4).</summary>
public readonly record struct SineDrive(double AmplitudeV, double FreqHz, double PhaseRad);

/// <summary>Diode model parameters — the SPICE-conventional exponential junction
/// (api.md §4/§23.4). The junction current is
/// <c>I = Is·(exp(V/(n·Vt)) − 1)</c> with the thermal voltage <c>Vt = k·T/q</c>
/// fixed at T = 300 K (≈ 0.025852 V; the model is isothermal — no self-heating).
/// The Newton driver stamps a per-iteration linearized companion (Geq ‖ Ieq) with
/// junction-voltage limiting (solver.md Analyses / Failure Handling).
/// <para><b><see cref="SeriesResistance"/> is not yet stamped</b> (it would need a
/// per-diode internal node; the phase-4 model is the ideal junction). The default
/// is 0, so default diodes are exact; a nonzero Rs is currently ignored — a
/// documented limitation, not silent for the default.</para></summary>
public readonly record struct DiodeParams(double SaturationCurrent, double Emission, double SeriesResistance)
{
    /// <summary>A silicon-ish default (Is=1e-14 A, n=1, Rs=0).</summary>
    public static DiodeParams Default => new(1e-14, 1.0, 0.0);
}

/// <summary>Decoupling-transformer coupling parameters (device layer; §7/§18).
/// <para><see cref="TurnsRatio"/> n is the primary:secondary voltage ratio: the
/// boundary exchange drives the secondary (B) port at V_A / n and reflects the
/// secondary current back as a primary (A) load of i_B / n (ampere-turns).</para>
/// <para>Leakage/magnetizing are pinned here as honest element parameters stamped
/// as ordinary resistors <b>around the port</b> (solver.md Islands: "modeled as
/// honest device elements but not relied on for stability"). <see cref="MagnetizingOhms"/>
/// (&gt; 0) stamps a shunt resistor across the primary port — the magnetizing /
/// core-loss branch, DC-consistent for the behavioral (frequency-agnostic)
/// coupling. <see cref="LeakageOhms"/> is pinned but <b>not yet stamped</b> (a
/// series leakage element needs an internal port node; deferred, mirroring the
/// documented <see cref="DiodeParams.SeriesResistance"/> limitation) — the default
/// 0 means an ideal, exactly-lossless core.</para></summary>
public readonly record struct TransformerParams(double TurnsRatio, double LeakageOhms = 0.0, double MagnetizingOhms = 0.0)
{
    /// <summary>An ideal decoupling transformer: turns ratio only, no port elements.</summary>
    public static TransformerParams Ideal(double turnsRatio) => new(turnsRatio);
}

/// <summary>
/// Converter efficiency curve (behavioral two-port; §7): up to four
/// (load-fraction, efficiency) breakpoints with linear interpolation, clamped flat
/// beyond the ends. Load fraction is P_out / rated power (the converter carries the
/// rating). Blittable readonly struct — storable in <see cref="CouplerSpec"/>.
/// </summary>
public readonly struct EfficiencyCurve
{
    private readonly double _l0, _e0, _l1, _e1, _l2, _e2, _l3, _e3;
    private readonly int _count;

    private EfficiencyCurve(int count,
        double l0, double e0, double l1, double e1, double l2, double e2, double l3, double e3)
    {
        _count = count;
        _l0 = l0; _e0 = e0; _l1 = l1; _e1 = e1; _l2 = l2; _e2 = e2; _l3 = l3; _e3 = e3;
    }

    /// <summary>A constant efficiency at every load.</summary>
    public static EfficiencyCurve Flat(double efficiency)
        => new(1, 0.0, efficiency, 0, 0, 0, 0, 0, 0);

    /// <summary>1–4 (loadFraction, efficiency) breakpoints, strictly ascending in
    /// load fraction. Allocates at construction (shape-time); the readback
    /// <see cref="EfficiencyAt"/> is allocation-free.</summary>
    public static EfficiencyCurve Points(params (double loadFraction, double efficiency)[] points)
    {
        if (points is null || points.Length < 1 || points.Length > 4)
            throw new ArgumentException("EfficiencyCurve needs 1..4 breakpoints.", nameof(points));
        for (var i = 1; i < points.Length; i++)
            if (!(points[i].loadFraction > points[i - 1].loadFraction))
                throw new ArgumentException("EfficiencyCurve breakpoints must be strictly ascending in load fraction.", nameof(points));
        (double l, double e) p0 = points[0];
        (double l, double e) p1 = points.Length > 1 ? points[1] : default;
        (double l, double e) p2 = points.Length > 2 ? points[2] : default;
        (double l, double e) p3 = points.Length > 3 ? points[3] : default;
        return new EfficiencyCurve(points.Length, p0.l, p0.e, p1.l, p1.e, p2.l, p2.e, p3.l, p3.e);
    }

    /// <summary>Number of breakpoints (1 ⇒ flat).</summary>
    public int Count => _count;

    /// <summary>Linear-interpolated efficiency at a load fraction, clamped flat past
    /// the first/last breakpoint. Allocation-free; two compares + one lerp.</summary>
    public double EfficiencyAt(double loadFraction)
    {
        if (_count <= 1) return _e0;
        if (loadFraction <= _l0) return _e0;
        double lPrev = _l0, ePrev = _e0;
        for (var i = 1; i < _count; i++)
        {
            double li = i == 1 ? _l1 : i == 2 ? _l2 : _l3;
            double ei = i == 1 ? _e1 : i == 2 ? _e2 : _e3;
            if (loadFraction <= li)
            {
                var t = (loadFraction - lPrev) / (li - lPrev);
                return ePrev + t * (ei - ePrev);
            }
            lPrev = li; ePrev = ei;
        }
        return ePrev;   // beyond the last breakpoint ⇒ clamp flat
    }
}

/// <summary>i²t slow-overload accumulator parameters (api.md §12).</summary>
public readonly record struct I2tParams(double MeltI2t, double Tau);

/// <summary>Per-component limit envelope (api.md §12). Same struct at Add-time
/// and via <c>Meta.SetLimits</c>.</summary>
public readonly record struct LimitSpec(double MaxCurrent, double MaxVoltage, double MaxPower, I2tParams Thermal);

/// <summary>Limit-violation classes (api.md §12).</summary>
public enum LimitKind : byte { OverCurrent, OverVoltage, OverPower, ThermalI2t }

/// <summary>A post-solve limit event, drained per island (api.md §12).
/// Attribution to geometry is the reduction layer's job (§19).</summary>
public struct LimitEvent
{
    public ComponentRef Source;
    public LimitKind Kind;
    public double Observed, Threshold, I2tFraction, SubstepTime;
    public long TickIndex;
}

/// <summary>Boundary-coupler exchange instrumentation (api.md §7). Zeroed until
/// the phase-5 boundary exchange lands.</summary>
public readonly record struct ExchangeView(
    double AmplitudeA, double PhaseA, double AmplitudeB, double PhaseB, double PowerA2B);

/// <summary>Boundary-coupler running energy ledger (api.md §7).
/// <para><see cref="InJ"/>/<see cref="OutJ"/>/<see cref="ModeledLossJ"/> are the
/// integrated boundary primitives; <see cref="SurplusJ"/> = In − Out − ModeledLoss
/// (≥ 0 by the recording clamp) becomes <see cref="HeatDumpedJ"/> = ModeledLoss +
/// max(Surplus,0) (≥ 0 by construction — design.md energy rule).</para>
/// <para><b><see cref="Residual"/> is a CLOSURE IDENTITY, not a conservation signal</b>
/// (ruled 2026-07-06). It equals In − Out − HeatDumped ≈ 0 <i>by construction</i> and
/// therefore can NEVER report a physical violation — do not use it as one. The ledger
/// RECORDS; it does not license. Whether a boundary actually conserved energy is
/// established by a <b>windowed physical audit</b> (source energy = dissipation +
/// Δstored + coupler heat, summed over both islands from public node-voltage
/// readbacks), and conservation itself is enforced upstream by the transformer
/// debt-droop and the sagging converter DC-link capacitor — not by the recording
/// clamp.</para></summary>
public readonly record struct EnergyLedger(
    double InJ, double OutJ, double ModeledLossJ, double SurplusJ, double HeatDumpedJ, double Residual);

/// <summary>Documentation attribute tagging a member's change-cost tier so a
/// client's cost profile is mechanically auditable (api.md §4).</summary>
[AttributeUsage(AttributeTargets.Method)]
public sealed class CostTierAttribute : Attribute
{
    public CostTierAttribute(int tier) => Tier = tier;

    /// <summary>The declared tier (0 meta / 1 RHS / 2 conductance / 3 topology).</summary>
    public int Tier { get; }
}
