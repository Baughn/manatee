using System;
using System.Buffers.Binary;

namespace Manatee.Core.Devices;

/// <summary>
/// Chemistry parameters for a <see cref="Battery"/> (data-only; the electrochemistry
/// itself is stubbed — design.md: build the STRUCTURE now, the chemistry arc later).
/// The open-circuit voltage is a linear interpolation between the empty and full
/// terminal voltages over state-of-charge; distinct chemistries differ only in these
/// scalars until the real discharge model lands.
/// </summary>
public readonly struct BatteryParams
{
    public BatteryParams(double ocvFull, double ocvEmpty, double internalOhms, double capacityCoulombs)
    {
        OcvFull = ocvFull; OcvEmpty = ocvEmpty;
        InternalOhms = internalOhms; CapacityCoulombs = capacityCoulombs;
    }

    /// <summary>Open-circuit terminal voltage at SoC = 1.</summary>
    public double OcvFull { get; }

    /// <summary>Open-circuit terminal voltage at SoC = 0.</summary>
    public double OcvEmpty { get; }

    /// <summary>Internal series resistance.</summary>
    public double InternalOhms { get; }

    /// <summary>Charge capacity in coulombs (amp-seconds).</summary>
    public double CapacityCoulombs { get; }

    /// <summary>Open-circuit voltage at a given state-of-charge (clamped 0..1).</summary>
    public double Ocv(double soc)
    {
        if (soc < 0.0) soc = 0.0; else if (soc > 1.0) soc = 1.0;
        return OcvEmpty + (OcvFull - OcvEmpty) * soc;
    }

    // Representative preset parameter sets (chemistry stubbed as three data points).
    /// <summary>A single voltaic pile cell (~1.1 V), small capacity, high Rint.</summary>
    public static BatteryParams VoltaicPile => new(ocvFull: 1.1, ocvEmpty: 0.8, internalOhms: 2.0, capacityCoulombs: 360.0);

    /// <summary>A 12 V lead-acid block: 12.8 V full / 11.8 V empty, low Rint, large capacity.</summary>
    public static BatteryParams LeadAcid => new(ocvFull: 12.8, ocvEmpty: 11.8, internalOhms: 0.02, capacityCoulombs: 3_600_000.0);

    /// <summary>A single Li-ion cell: 4.2 V full / 3.2 V empty, low Rint.</summary>
    public static BatteryParams LiIon => new(ocvFull: 4.2, ocvEmpty: 3.2, internalOhms: 0.05, capacityCoulombs: 10_800.0);
}

/// <summary>
/// EMF + internal resistance + state-of-charge integrator (api.md §18; design.md
/// battery arc). Structurally a source (internal EMF node behind <c>Rint</c>) whose
/// EMF tracks the open-circuit voltage of the modelled chemistry as SoC drains under
/// load: <c>SoC ← SoC − ∫I dt / Q</c>. The SoC is the device's ONE serialized state
/// unit, so a save/load resumes a partway-drained pack.
///
/// <para><b>Component ordinals (api.md §18):</b> 0 = the EMF source, 1 = the internal
/// resistance, 2 = the internal EMF node. Terminals: 0 = positive port, 1 = negative
/// (return) port.</para>
/// </summary>
public sealed class Battery : Device
{
    private readonly BatteryParams _p;

    private VSourceId _vs;
    private ResistorId _rr;
    private NodeId _pos, _neg, _mid;

    private double _soc;   // state-of-charge in [0,1] (serialized)

    public Battery(in BatteryParams parameters, double initialSoc = 1.0)
    {
        if (!(parameters.CapacityCoulombs > 0.0)) throw new ArgumentOutOfRangeException(nameof(parameters));
        if (!(parameters.InternalOhms > 0.0)) throw new ArgumentOutOfRangeException(nameof(parameters));
        _p = parameters;
        _soc = Clamp01(initialSoc);
    }

    /// <summary>Current state-of-charge in [0,1].</summary>
    public double StateOfCharge => _soc;

    /// <summary>Present open-circuit voltage of the modelled chemistry.</summary>
    public double OpenCircuitVoltage => _p.Ocv(_soc);

    public override TerminalSpec Terminals => new(2);

    protected override void Build(StructuralEdit e, ReadOnlySpan<NodeId> terminals,
                                  in ExternalKey baseKey, in StateKey state)
    {
        _pos = terminals[0];
        _neg = terminals[1];
        _mid = e.AddNode(baseKey.Derive(2));                                       // ordinal 2 (internal node)
        _vs = e.AddVoltageSource(_mid, _neg, _p.Ocv(_soc), baseKey.Derive(0));     // ordinal 0
        _rr = e.AddResistor(_mid, _pos, _p.InternalOhms, baseKey.Derive(1));       // ordinal 1
    }

    public override void Tick(in DeviceTickContext ctx)
    {
        // Discharge current is the flow out of the EMF through Rint (mid → pos). A
        // positive reading drains the pack; a negative reading (external charging)
        // replenishes it. Coulomb-count into SoC and re-aim the EMF at the new OCV.
        var i = ctx.Previous.Current(_rr);
        _soc = Clamp01(_soc - i * ctx.Dt / _p.CapacityCoulombs);
        ctx.Drive(_vs, _p.Ocv(_soc));
    }

    private static double Clamp01(double x) => x < 0.0 ? 0.0 : x > 1.0 ? 1.0 : x;

    public override int StateSize => 8;

    public override void SaveState(Span<byte> dst)
        => BinaryPrimitives.WriteInt64LittleEndian(dst, BitConverter.DoubleToInt64Bits(_soc));

    public override void RestoreState(ReadOnlySpan<byte> src)
        => _soc = BitConverter.Int64BitsToDouble(BinaryPrimitives.ReadInt64LittleEndian(src));
}
