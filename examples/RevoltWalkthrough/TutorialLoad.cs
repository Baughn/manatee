using Manatee.Core;
using Manatee.Core.Devices;
using Manatee.Core.Reduction;
using Manatee.Core.State;

namespace Manatee.Example.RevoltWalkthrough;

/// <summary>
/// The key-re-resolving load adaptor — the device shape a game integration wraps
/// around anything that lives on a network whose topology churns (cable cuts,
/// breaker trips). Tutorial §4 and §6; the pattern is api.md §16/§18 and the
/// test suite's <c>RevoltAdaptor</c>.
///
/// It presents a constant-power game device to the linear DC solve as a
/// conductance G = P/V_prev² clamped to a legal range — the R18 "legacy device
/// adaptor" linearization: instead of making the solve nonlinear, re-linearize
/// once per tick at last tick's voltage. Once V settles, the per-tick Adjust
/// falls below AdjustEpsilon and degrades to a free tier-0 document write
/// (TickStats.AdjustNoOps counts them), so a settled fleet costs zero
/// refactorizations.
///
/// Contrast with the built-in <see cref="AdaptedLoad"/>: that class caches its
/// component AND terminal-node handles privately at Build and exposes no re-pin
/// surface, so it is only safe on islands whose topology never churns (api.md
/// §18). THIS class holds its identity as keys (ExternalKey / JunctionKey /
/// PartitionKey) and can re-resolve every handle it owns after any island
/// rebuild — which is the whole trick.
/// </summary>
internal sealed class TutorialLoad : IDeviceStateUnit
{
    private const double GMin = 1e-6, GMax = 1e3;
    private const double LiveFloorVolts = 1.0;   // below this the bus reads "dead" — shed, don't seek

    // ── Durable identity: keys survive everything (api.md §16). ──
    public PartitionKey Partition { get; }
    public JunctionKey Port { get; }              // where on the cable graph we attach
    public ExternalKey Key { get; }               // the load resistor's topological identity
    public StateKey State { get; }                // the serializable-state identity
    public double Watts { get; }

    // ── Volatile identity: handles die on island rebuild; re-minted by Repin. ──
    private ResistorId _r;
    private NodeId _pos, _neg;
    private bool _retired;

    // ── Serializable runtime state (rides the island snapshot, restored by StateKey). ──
    private long _ticks;
    private double _appliedG = GMin;

    public TutorialLoad(PartitionKey partition, in JunctionKey port, in ExternalKey key, double watts)
    {
        Partition = partition;
        Port = port;
        Key = key;
        State = StateKey.From(key);              // bare single-resistor device: reuse the key bits
        Watts = watts;
    }

    public bool Retired => _retired;
    public ResistorId Resistor => _r;
    public double AppliedOhms => 1.0 / _appliedG;
    public long TicksSeen => _ticks;             // restore demo: jumps back after a Restore

    /// <summary>Tier-3 build: resolve the terminals through the reduction layer, stamp
    /// the load resistor, register the state unit. NOTE: PortNode/ReferenceNode are
    /// resolved BEFORE the Edit — PortNode protects the junction, and protecting a
    /// mid-chain junction recompacts the run (see the sharp-edges appendix).</summary>
    public void Build(Netlist net, ConductorGraph graph)
    {
        _pos = graph.PortNode(Port);
        _neg = graph.ReferenceNode(Partition);   // ReferenceBound wiring: the partition's return rail
        using (var e = net.Edit())
            _r = e.AddResistor(_pos, _neg, 1.0 / GMin, Key);
        net.RegisterDeviceState(_pos, this);     // anchored on the live port node
    }

    /// <summary>Re-pin (api.md §16/§18): re-resolve every held handle by key after an
    /// island rebuild reissued generations. NO structural edit happens here — key
    /// re-resolution is tier-0 and cannot itself trigger the rebuild it recovers from.
    /// Idempotent: calling it when nothing changed is a cheap no-op.</summary>
    public void Repin(Netlist net, ConductorGraph graph)
    {
        if (_retired) return;
        if (net.TryResolve(Key, out var c))
            _r = new ResistorId(c.Slot, c.Gen, c.Net);
        _pos = graph.PortNode(Port);
        _neg = graph.ReferenceNode(Partition);
        net.RegisterDeviceState(_pos, this);     // re-registration replaces the stale anchor
    }

    /// <summary>Tier-3 retire (device removed from the world): remove the resistor by
    /// key and unregister the state unit. After this, never Tick or read it again —
    /// the re-pin contract's "retire" half (api.md §18).</summary>
    public void Retire(Netlist net)
    {
        if (_retired) return;
        _retired = true;
        using (var e = net.Edit())
            if (net.TryResolve(Key, out var c))
                e.Remove(new ResistorId(c.Slot, c.Gen, c.Net));
        net.UnregisterDeviceState(State);
    }

    /// <summary>The per-tick update, capped at tier ≤2 by the context's TYPE: a
    /// DeviceTickContext exposes Drive/Adjust only, so a structural change cannot be
    /// written into the hot loop — it does not compile (api.md §18).</summary>
    public void Tick(in DeviceTickContext ctx)
    {
        if (_retired) return;
        _ticks++;
        // Re-linearize at last tick's published voltage (Previous is deterministic
        // for the device's own island).
        var v = ctx.Previous.Voltage(_pos) - ctx.Previous.Voltage(_neg);
        var absV = v < 0.0 ? -v : v;
        double g;
        if (absV < LiveFloorVolts)
        {
            // SHED on a dead-reading bus — never seek G = P/V² toward GMax at V ≈ 0,
            // where the linearization is meaningless: slamming to GMax stamps a
            // fictional dead short that pops real fuses. Merge ticks no longer read
            // 0 here (both sides hold last-good until the merged island first
            // publishes — api.md §17 rule 4, fixed 2026-07-07), so this floor is
            // pure defense for buses that are GENUINELY dead: de-energized
            // islands, islands that are Faulted right now (de-energized reads are
            // status-scoped — a merge or retry flips them to Dirty and their
            // last-published values return), and the pre-first-publish boot
            // window (sharp-edges appendix, edge 4, docs/integration-tutorial.md).
            g = GMin;
        }
        else
        {
            g = Watts / (absV * absV);
            if (g < GMin) g = GMin; else if (g > GMax) g = GMax;
        }
        ctx.Adjust(_r, 1.0 / g);                 // ε-no-op once converged: tier 0, not tier 2
        _appliedG = g;
    }

    /// <summary>Readback: power the load actually drew last solve (the ApplyState value
    /// the game hands to the vanilla device). Read, never recompute.</summary>
    public double Power(in Solution sol) => _retired ? 0.0 : sol.Power(_r);

    // ── IDeviceStateUnit: one fixed-size blob keyed on StateKey, riding the island
    //    snapshot. Restore is ADDITIVE by key (api.md §14) — a blob only ever
    //    overwrites units it carries entries for. ──
    StateKey IDeviceStateUnit.Key => State;
    public int BlobSize => 16;

    public void Save(Span<byte> dst)
    {
        System.Buffers.Binary.BinaryPrimitives.WriteInt64LittleEndian(dst, _ticks);
        System.Buffers.Binary.BinaryPrimitives.WriteInt64LittleEndian(
            dst.Slice(8), BitConverter.DoubleToInt64Bits(_appliedG));
    }

    public void Restore(ReadOnlySpan<byte> src)
    {
        _ticks = System.Buffers.Binary.BinaryPrimitives.ReadInt64LittleEndian(src);
        _appliedG = BitConverter.Int64BitsToDouble(
            System.Buffers.Binary.BinaryPrimitives.ReadInt64LittleEndian(src.Slice(8)));
    }
}
