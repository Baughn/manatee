namespace Manatee.Core;

/// <summary>Per-node grounding role. The construction-time wiring policy (§5)
/// fires on <see cref="Return"/> nodes and source negatives — never per device.</summary>
public enum NodeRole : byte { Internal, Return, Reference }

/// <summary>Whether the core partitions by connectivity (VS/tablet) or the
/// client hands pre-partitioned networks whose cross-merges are illegal except
/// through a coupler (Stationeers).</summary>
public enum PartitioningMode : byte { SelfPartitioned, ClientPartitioned }

/// <summary>Debug enforcement level (api.md §5, §8).</summary>
public enum DebugLevel : byte { Off, Asserts, AllocationSentinel }

/// <summary>Analysis regime for the netlist. <see cref="Mixed"/> lets one
/// netlist host both DC and AC islands, the regime chosen per island by content
/// (api.md §5, Decision log #13). This stage carries the profile as document
/// metadata; the transient/AC stepping is phase 4.</summary>
public readonly struct SolverProfile
{
    /// <summary>Kind discriminant.</summary>
    public enum Regime : byte { Dc, Transient, Mixed }

    /// <summary>Which regime this profile selects.</summary>
    public Regime Kind { get; private init; }

    /// <summary>Backward-Euler step / tick size (seconds).</summary>
    public double Dt { get; private init; }

    /// <summary>AC subcycle sample target (Mixed only; solver-owned, §5).</summary>
    public int AcSamplesPerCycle { get; private init; }

    /// <summary>Stationeers: DC operating point + BE storage dynamics at dt.</summary>
    public static SolverProfile Dc(double dt)
        => new() { Kind = Regime.Dc, Dt = dt, AcSamplesPerCycle = 0 };

    /// <summary>VS DC-side / tablet: transient BE at dt.</summary>
    public static SolverProfile Transient(double dt)
        => new() { Kind = Regime.Transient, Dt = dt, AcSamplesPerCycle = 0 };

    /// <summary>Per-island regime by content: AC islands subcycle, DC islands
    /// step once (api.md §5).</summary>
    public static SolverProfile Mixed(double tickDt, int acSamplesPerCycle = 20)
        => new() { Kind = Regime.Mixed, Dt = tickDt, AcSamplesPerCycle = acSamplesPerCycle };
}

/// <summary>
/// The per-client wiring surface (api.md §5, OQ4). The core is agnostic to
/// wiring convention; the policy is fixed at Netlist birth and fires
/// declaratively at commit on source negatives, <see cref="NodeRole.Return"/>
/// nodes, and device return terminals — there is deliberately no
/// <c>BindNegative</c> call.
/// </summary>
public readonly struct WiringPolicy
{
    /// <summary>Which of the three modes this policy is.</summary>
    public enum Mode : byte { ReferenceBound, TwoWireLeak, ExplicitOnly }

    /// <summary>The selected mode.</summary>
    public Mode Kind { get; private init; }

    /// <summary>ReferenceBound: near-ideal return conductance (S). TwoWireLeak:
    /// the earth-leak resistance (Ω). Unused for ExplicitOnly.</summary>
    public double Parameter { get; private init; }

    /// <summary>Stationeers: every return terminal binds to its partition's
    /// reference through a near-ideal return conductance — numerically identical
    /// to modeling the return conductor. No leak stamped.</summary>
    public static WiringPolicy ReferenceBound(double returnConductanceSiemens = 1e3)
        => new() { Kind = Mode.ReferenceBound, Parameter = returnConductanceSiemens };

    /// <summary>VS: two-wire; at commit auto-stamp <paramref name="leakOhms"/>
    /// from every Return-role node to the island reference (the literal earth).
    /// The leak resistor is never hand-authored and has no client handle.</summary>
    public static WiringPolicy TwoWireLeak(double leakOhms = 1e6)
        => new() { Kind = Mode.TwoWireLeak, Parameter = leakOhms };

    /// <summary>Tablet: ideal nodes, no implicit stamps; ground is marked
    /// explicitly and deliberately floating lessons stay floating (gmin only).</summary>
    public static WiringPolicy ExplicitOnly()
        => new() { Kind = Mode.ExplicitOnly, Parameter = 0.0 };
}

/// <summary>
/// Immutable birth configuration for a <see cref="Netlist"/> (api.md §5). The
/// three named bundles are the misuse-resistant defaults.
/// </summary>
public readonly struct NetlistOptions
{
    /// <summary>Analysis regime.</summary>
    public SolverProfile Profile { get; init; }

    /// <summary>Construction-time grounding policy.</summary>
    public WiringPolicy Wiring { get; init; }

    /// <summary>Self- vs client-partitioning.</summary>
    public PartitioningMode Partitioning { get; init; }

    /// <summary>ε-no-op threshold (relative, log-conductance domain; api.md §9).
    /// 0 ⇒ the pinned default (1e-4).</summary>
    public double AdjustEpsilon { get; init; }

    /// <summary>Arena presize hint — number of islands (not a cap).</summary>
    public int ExpectedIslands { get; init; }

    /// <summary>Arena presize hint — number of nodes (not a cap).</summary>
    public int ExpectedNodes { get; init; }

    /// <summary>Fixed journal ring capacity; 0 ⇒ default.</summary>
    public int JournalCapacity { get; init; }

    /// <summary>Debug enforcement level.</summary>
    public DebugLevel Debug { get; init; }

    /// <summary>Deferred-op ring sizing hint for release-mode guard deferrals
    /// (api.md §8); 0 ⇒ default (64).</summary>
    public int DeferredOpCapacity { get; init; }

    /// <summary>Stationeers bundle: DC, ReferenceBound, ClientPartitioned.</summary>
    public static NetlistOptions Stationeers(double dt = 0.5) => new()
    {
        Profile = SolverProfile.Dc(dt),
        Wiring = WiringPolicy.ReferenceBound(),
        Partitioning = PartitioningMode.ClientPartitioned,
    };

    /// <summary>Vintage Story bundle: Mixed, TwoWireLeak(1e6), SelfPartitioned.</summary>
    public static NetlistOptions VintageStory(double tickDt = 0.05) => new()
    {
        Profile = SolverProfile.Mixed(tickDt),
        Wiring = WiringPolicy.TwoWireLeak(1e6),
        Partitioning = PartitioningMode.SelfPartitioned,
    };

    /// <summary>Tablet bundle: Mixed, ExplicitOnly, SelfPartitioned.</summary>
    public static NetlistOptions Tablet(double tickDt = 0.05) => new()
    {
        Profile = SolverProfile.Mixed(tickDt),
        Wiring = WiringPolicy.ExplicitOnly(),
        Partitioning = PartitioningMode.SelfPartitioned,
    };
}
