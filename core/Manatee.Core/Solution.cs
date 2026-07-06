namespace Manatee.Core;

/// <summary>
/// Published solution snapshot (api.md §10). Safe to read while OTHER islands
/// Step. The numeric solve is stage 2: this stage returns last-published values
/// (0 before any solve) and honors the stale-handle sentinel; probe
/// <see cref="Read"/> throws until phase 4/6.
/// </summary>
public readonly struct Solution
{
    private readonly Netlist _net;

    internal Solution(Netlist net) => _net = net;

    /// <summary>0B; node potential (interpolated if compacted away). Reference
    /// nodes read exactly 0.</summary>
    [CostTier(1)]
    public double Voltage(NodeId n) => _net.ReadVoltage(n);

    /// <summary>Domain-neutral alias (thermal-RC reuse).</summary>
    public double Potential(NodeId n) => Voltage(n);

    /// <summary>0B; signed branch flow through a component.</summary>
    [CostTier(1)]
    public double Current<TId>(TId branch) where TId : struct, IComponentId
        => _net.ReadCurrent(branch.AsRef());

    /// <summary>0B; power into a component.</summary>
    [CostTier(1)]
    public double Power<TId>(TId c) where TId : struct, IComponentId
        => _net.ReadPower(c.AsRef());

    /// <summary>0B; probe readback (interpolated inside compacted runs):
    /// V = Va + t·(Vb − Va) for an interpolated probe, or a single node's
    /// potential for a node probe (api.md §13).</summary>
    [CostTier(1)]
    public double Read(ProbeId p) => _net.ReadProbe(p);

    /// <summary>false ⇒ last-good (Building/Dirty) or de-energized (Faulted).</summary>
    public bool IsLive(IslandId i) => _net.IslandIsLive(i);

    /// <summary>MNA-order raw vector (the bit-for-bit comparison unit, api.md
    /// §10). Empty until the solve pipeline publishes.</summary>
    public ReadOnlySpan<double> RawVector(IslandId i) => _net.RawVector(i);
}

/// <summary>
/// The solver as a monitorable system (api.md §9). Read via
/// <c>Netlist.LastTickStats</c> after every Solve; the tier-budget CI assertions
/// read these fields.
/// </summary>
public struct TickStats
{
    public int Substeps;
    public int RhsSolves;
    public int Refactorizations;
    public int IslandRebuilds;
    public int MergesApplied;
    public int NewtonIterations;
    public int AdjustNoOps;
    public int StaleHandleReads;
    public int DeferredStructuralOps;
    public long BytesAllocated;
}

/// <summary>
/// The one tier-0 facade (api.md §4). Never touches matrix or RHS; visibly free.
/// </summary>
public readonly struct MetaFacade
{
    private readonly Netlist _net;

    internal MetaFacade(Netlist net) => _net = net;

    /// <summary>0B; envelope/ambient recompute (same LimitSpec as Add-time).</summary>
    [CostTier(0)]
    public void SetLimits(ComponentRef c, in LimitSpec cfg) => _net.SetLimits(c, cfg);

    /// <summary>Register/replace a component's thermal ENVELOPE — the Pareto-minimal
    /// set of (rating, melt, tau) accumulators a collapsed series chain carries
    /// (api.md §12/§19, ruled 2026-07-06). One melting integral runs per pair; the
    /// component trips when any pair trips, and the event's <c>PairIndex</c> names it.
    /// A registered envelope supersedes the LimitSpec's own <c>Thermal</c>; an empty
    /// span clears it. Same-count re-registration (ambient re-derate) preserves the
    /// accumulators by index; a count change resets them (api.md §19).</summary>
    [CostTier(0)]
    public void SetThermalEnvelope(ComponentRef c, System.ReadOnlySpan<I2tPair> pairs)
        => _net.SetThermalEnvelopeImpl(c, pairs);

    /// <summary>0B; re-aim an interior interpolated probe.</summary>
    [CostTier(0)]
    public void SetProbeInterpolation(ProbeId p, NodeId a, NodeId b, double t)
        => _net.SetProbeInterpolation(p, a, b, t);

    /// <summary>Debug builds only; may allocate.</summary>
    [CostTier(0)]
    public void SetDebugName(ComponentRef c, string name) => _net.SetDebugName(c, name);
}
