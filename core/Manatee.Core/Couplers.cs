namespace Manatee.Core;

/// <summary>
/// Coupling-device specification (api.md §7). A galvanic <see cref="Breaker"/>
/// merges its two sides into one matrix when Closed and splits them (an island
/// rebuild) when Open. The boundary variants
/// (<see cref="DecouplingTransformer"/>, <see cref="ConverterTwoPort"/>) ALWAYS
/// keep the sides on separate islands and exchange power across the boundary —
/// the exchange itself is phase 5; this stage carries the registration and the
/// galvanic merge/open rebuild semantics.
/// </summary>
public readonly struct CouplerSpec
{
    /// <summary>Galvanic vs boundary (power-transfer) coupler.</summary>
    public enum Family : byte { Breaker, DecouplingTransformer, ConverterTwoPort }

    /// <summary>Which family this spec selects.</summary>
    public Family Kind { get; private init; }

    /// <summary>True iff the coupler is galvanic — Closed unions the two ports
    /// into one island; Open schedules a split rebuild.</summary>
    public bool IsGalvanic => Kind == Family.Breaker;

    /// <summary>Boundary relaxation α — exponential-smoothing factor on the
    /// per-substep exchanged values (boundary families only). 1 ⇒ no smoothing.</summary>
    public double RelaxationAlpha { get; private init; }

    /// <summary>DC-link storage capacitance, farads (ConverterTwoPort only): the
    /// real boundary-storage capacitor stamped on the B side.</summary>
    public double DcLinkFarads { get; private init; }

    /// <summary>Regulated DC-link output voltage setpoint, volts (ConverterTwoPort
    /// only): the B-side port is held at this level (a real regulator's job).</summary>
    public double OutputVolts { get; private init; }

    /// <summary>Rated output power, watts (ConverterTwoPort only): the denominator
    /// for the efficiency curve's load fraction (P_out / rated).</summary>
    public double RatedWatts { get; private init; }

    /// <summary>Transformer coupling parameters (DecouplingTransformer only).</summary>
    public TransformerParams Transformer { get; private init; }

    /// <summary>Converter efficiency (ConverterTwoPort only).</summary>
    public EfficiencyCurve Efficiency { get; private init; }

    /// <summary>Galvanic bidirectional breaker (api.md §7): Closed = one shared
    /// matrix, Open = separate islands (an island rebuild — the honest cost).</summary>
    public static CouplerSpec Breaker()
        => new() { Kind = Family.Breaker };

    /// <summary>Power-transfer boundary: always separate islands, exchanged
    /// amplitude+phase per substep with relaxation α (phase 5).</summary>
    public static CouplerSpec DecouplingTransformer(in TransformerParams p, double relaxationAlpha = 0.5)
        => new() { Kind = Family.DecouplingTransformer, Transformer = p, RelaxationAlpha = relaxationAlpha };

    /// <summary>Behavioral P-transfer with efficiency curve and a real DC-link
    /// capacitor as boundary storage. Both sides stay linear: the B port is held at
    /// <paramref name="outputVolts"/>, P_out is measured, P_in = P_out / efficiency
    /// is drawn from A, and the efficiency loss becomes coupler heat. Load fraction
    /// for the curve is P_out / <paramref name="ratedWatts"/>.</summary>
    public static CouplerSpec ConverterTwoPort(in EfficiencyCurve e, double dcLinkFarads,
        double outputVolts, double ratedWatts, double relaxationAlpha = 0.5)
        => new()
        {
            Kind = Family.ConverterTwoPort, Efficiency = e, DcLinkFarads = dcLinkFarads,
            OutputVolts = outputVolts, RatedWatts = ratedWatts, RelaxationAlpha = relaxationAlpha,
        };
}

/// <summary>The four terminals a coupler bridges (api.md §7).</summary>
public readonly struct CouplerPorts
{
    public CouplerPorts(NodeId aPos, NodeId aNeg, NodeId bPos, NodeId bNeg)
    {
        APos = aPos; ANeg = aNeg; BPos = bPos; BNeg = bNeg;
    }

    public NodeId APos { get; }
    public NodeId ANeg { get; }
    public NodeId BPos { get; }
    public NodeId BNeg { get; }
}
