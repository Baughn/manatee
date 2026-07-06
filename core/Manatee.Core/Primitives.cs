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

/// <summary>Diode model parameters (api.md §23.4 — roles fixed, fields fill in
/// at implementation). Values are placeholders for the phase-4 Newton stamp.</summary>
public readonly record struct DiodeParams(double SaturationCurrent, double Emission, double SeriesResistance)
{
    /// <summary>A silicon-ish default (Is=1e-14 A, n=1, Rs=0).</summary>
    public static DiodeParams Default => new(1e-14, 1.0, 0.0);
}

/// <summary>Idealized-transformer coupling parameters (device layer; §7/§18).</summary>
public readonly record struct TransformerParams(double TurnsRatio, double LeakageHenries, double MagnetizingHenries);

/// <summary>Converter efficiency curve (behavioral two-port; §7). Placeholder
/// piecewise model — a single flat efficiency for now.</summary>
public readonly record struct EfficiencyCurve(double PeakEfficiency, double KneeWatts);

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

/// <summary>Boundary-coupler running energy ledger (api.md §7). Zeroed until
/// phase 5.</summary>
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
