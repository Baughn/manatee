using System;

namespace Manatee.Core.Devices;

/// <summary>
/// A regulated source adapted to the linear solve: an ideal EMF behind an internal
/// series resistance, with an across-tick CURRENT CLAMP derived from an advertised
/// power. When last tick's output current exceeds <c>I_max = P_adv / |V_out|</c>
/// the EMF droops proportionally (<c>E ← E_nom·I_max/|I|</c>), so the source can
/// never deliver more than its advertised power for long — the supply-side dual of
/// <see cref="AdaptedLoad"/>. Stateless: the clamp reads the previous solution, so
/// a snapshot/restore of the surrounding island reproduces it bit-for-bit.
///
/// <para><b>Component ordinals (api.md §18):</b> 0 = the EMF voltage source
/// (internal node → negative terminal), 1 = the internal series resistance
/// (internal node → positive terminal), 2 = the internal EMF node. Terminals:
/// 0 = positive (output) port, 1 = negative (return) port.</para>
/// </summary>
public sealed class AdaptedSource : Device
{
    private const double VEps = 1e-9, IEps = 1e-12;

    private readonly double _emfNominal, _rint, _pMax;

    private VSourceId _vs;
    private ResistorId _rr;
    private NodeId _pos, _neg, _mid;

    /// <param name="emfVolts">Open-circuit EMF.</param>
    /// <param name="internalOhms">Internal series resistance (&gt; 0).</param>
    /// <param name="advertisedWatts">Power cap; the EMF droops to hold output ≤ this.</param>
    public AdaptedSource(double emfVolts, double internalOhms, double advertisedWatts)
    {
        if (!(internalOhms > 0.0)) throw new ArgumentOutOfRangeException(nameof(internalOhms));
        _emfNominal = emfVolts;
        _rint = internalOhms;
        _pMax = advertisedWatts > 0.0 ? advertisedWatts : double.PositiveInfinity;
    }

    public override TerminalSpec Terminals => new(2);

    protected override void Build(StructuralEdit e, ReadOnlySpan<NodeId> terminals,
                                  in ExternalKey baseKey, in StateKey state)
    {
        _pos = terminals[0];
        _neg = terminals[1];
        _mid = e.AddNode(baseKey.Derive(2));                                   // ordinal 2 (internal node)
        _vs = e.AddVoltageSource(_mid, _neg, _emfNominal, baseKey.Derive(0));  // ordinal 0
        _rr = e.AddResistor(_mid, _pos, _rint, baseKey.Derive(1));             // ordinal 1
    }

    public override void Tick(in DeviceTickContext ctx)
    {
        var i = ctx.Previous.Current(_rr);
        var absI = i < 0.0 ? -i : i;
        var vOut = ctx.Previous.Voltage(_pos) - ctx.Previous.Voltage(_neg);
        var absV = vOut < 0.0 ? -vOut : vOut;

        var emf = _emfNominal;
        if (!double.IsInfinity(_pMax) && absV > VEps && absI > IEps)
        {
            var pNow = absV * absI;
            if (pNow > _pMax)
            {
                // Observe the external load resistance R_ext = V_out/I from the last solve.
                // The EMF that delivers exactly P_max into it (through R_int) is
                //   P = E²·R_ext/(R_int+R_ext)²  ⇒  E = √(P_max/R_ext)·(R_int+R_ext).
                // For a resistive load this hits the cap in ONE step with no oscillation.
                var rExt = absV / absI;
                emf = Math.Sqrt(_pMax / rExt) * (_rint + rExt);
                if (emf > _emfNominal) emf = _emfNominal;   // never boost above nominal
                if (emf < 0.0) emf = 0.0;
            }
        }

        ctx.Drive(_vs, emf);
    }
}
