using System;

namespace Manatee.Core.Devices;

/// <summary>
/// A same-matrix two-port transformer composed over the primitive
/// <see cref="StructuralEdit.AddIdealTransformer"/> (the exact turns-ratio
/// constraint) PLUS ordinary leakage/magnetizing elements per
/// <see cref="TransformerParams"/> — NOT a boundary coupler (api.md §7 vs §18). The
/// ideal core enforces <c>V_secondary = V_primary / n</c> and ampere-turns
/// <c>I_primary = I_secondary / n</c> to machine precision; the leakage series
/// resistance and magnetizing shunt are honest device elements stamped around it
/// (solver.md: "modeled as honest device elements but not relied on for stability").
///
/// <para><b>Component ordinals (api.md §18):</b> 0 = the ideal transformer,
/// 1 = the magnetizing shunt resistor (only if <see cref="TransformerParams.MagnetizingOhms"/>
/// &gt; 0), 2 = the primary leakage series resistor (only if
/// <see cref="TransformerParams.LeakageOhms"/> &gt; 0), 3 = the internal leakage node.
/// Terminals: 0 = primary +, 1 = primary −, 2 = secondary +, 3 = secondary −.</para>
/// </summary>
public sealed class IdealizedTransformer : Device
{
    private readonly TransformerParams _p;

    private TransformerId _xfmr;
    private ResistorId _mag, _leak;
    private bool _hasMag, _hasLeak;
    private NodeId _aPos, _aNeg, _bPos, _bNeg;

    public IdealizedTransformer(in TransformerParams parameters)
    {
        if (!(Math.Abs(parameters.TurnsRatio) > 0.0))
            throw new ArgumentOutOfRangeException(nameof(parameters), "TurnsRatio must be non-zero.");
        _p = parameters;
    }

    public override TerminalSpec Terminals => new(4);

    /// <summary>The composed ideal-transformer primitive handle (readback/adjust seam).</summary>
    public TransformerId Core => _xfmr;

    protected override void Build(StructuralEdit e, ReadOnlySpan<NodeId> terminals,
                                  in ExternalKey baseKey, in StateKey state)
    {
        _aPos = terminals[0]; _aNeg = terminals[1];
        _bPos = terminals[2]; _bNeg = terminals[3];

        // Primary side: an optional series leakage resistance feeds the ideal core
        // through an internal node (an honest series element, DC-consistent).
        var primaryPos = _aPos;
        if (_p.LeakageOhms > 0.0)
        {
            var aInt = e.AddNode(baseKey.Derive(3));                              // ordinal 3
            _leak = e.AddResistor(_aPos, aInt, _p.LeakageOhms, baseKey.Derive(2)); // ordinal 2
            _hasLeak = true;
            primaryPos = aInt;
        }

        _xfmr = e.AddIdealTransformer(primaryPos, _aNeg, _bPos, _bNeg, _p.TurnsRatio, baseKey.Derive(0)); // ordinal 0

        // Magnetizing / core-loss branch: an honest shunt across the primary winding.
        if (_p.MagnetizingOhms > 0.0)
        {
            _mag = e.AddResistor(primaryPos, _aNeg, _p.MagnetizingOhms, baseKey.Derive(1)); // ordinal 1
            _hasMag = true;
        }
    }

    /// <summary>Retune the turns ratio (tier-2). No-op if the core was not built.</summary>
    public void SetTurnsRatio(in DeviceTickContext ctx, double turnsRatio)
    {
        if (Math.Abs(turnsRatio) > 0.0) ctx.Adjust(_xfmr, turnsRatio);
    }

    // Suppress unused-field warnings for the optional element handles (used by callers
    // via reflection-free readback in tests; kept as documented device state).
    internal bool HasMagnetizing => _hasMag;
    internal bool HasLeakage => _hasLeak;
    internal ResistorId Magnetizing => _mag;
    internal ResistorId Leakage => _leak;
}
