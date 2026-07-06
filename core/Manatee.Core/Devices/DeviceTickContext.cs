namespace Manatee.Core.Devices;

/// <summary>
/// Tier-≤2 capability object for a device's per-tick update (api.md §18). A ref
/// struct: 0B, cannot escape the tick. It mirrors the full <c>Netlist</c>
/// tier-1/2 verb set so a device owning a capacitor/inductor/diode/transformer
/// never reaches outside its capability — "cannot exceed its tier by type".
///
/// <para>The <see cref="Devices"/> layer proper (the <c>Device</c> base class and
/// built-in models) is phase 8; this capability object exists now because the
/// sec-4 <c>Netlist.TickContext</c> surface returns it.</para>
/// </summary>
public readonly ref struct DeviceTickContext
{
    private readonly Netlist _net;

    internal DeviceTickContext(Netlist net, double dt)
    {
        _net = net;
        Dt = dt;
    }

    /// <summary>Last published values (deterministic = previous tick for the
    /// device's OWN island; api.md §18, §21).</summary>
    public Solution Previous => _net.Solution;

    /// <summary>The tick / substep dt.</summary>
    public double Dt { get; }

    [CostTier(1)] public void Drive(VSourceId id, double volts) => _net.Drive(id, volts);
    [CostTier(1)] public void Drive(ISourceId id, double amps) => _net.Drive(id, amps);
    [CostTier(1)] public void Drive(VSourceId id, in SineDrive d) => _net.Drive(id, d);
    [CostTier(2)] public void Adjust(ResistorId id, double ohms) => _net.Adjust(id, ohms);
    [CostTier(2)] public void Adjust(SwitchId id, bool closed) => _net.Adjust(id, closed);
    [CostTier(2)] public void Adjust(CapacitorId id, double farads) => _net.Adjust(id, farads);
    [CostTier(2)] public void Adjust(InductorId id, double henries) => _net.Adjust(id, henries);
    [CostTier(2)] public void Adjust(DiodeId id, in DiodeParams p) => _net.Adjust(id, p);
    [CostTier(2)] public void Adjust(TransformerId id, double turnsRatio) => _net.Adjust(id, turnsRatio);
}
