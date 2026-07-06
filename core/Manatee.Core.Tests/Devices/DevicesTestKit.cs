using Manatee.Core;
using Manatee.Core.Devices;

namespace Manatee.Core.Tests.Devices;

/// <summary>Shared rig for the device-layer tests (api.md §18). Devices are pure
/// consumers of the public Core API, so these helpers build ordinary netlists and a
/// <see cref="DeviceHost"/> over them.</summary>
internal static class DevicesTestKit
{
    public static ExternalKey K(ulong id) => new(id);

    public static Core.Netlist Net(double dt = 0.05)
        => new(new NetlistOptions
        {
            Profile = SolverProfile.Mixed(dt),
            Wiring = WiringPolicy.ExplicitOnly(),
            Partitioning = PartitioningMode.SelfPartitioned,
            Debug = DebugLevel.Asserts,
        });

    /// <summary>One device tick + one solve, advancing the tick clock.</summary>
    public static void Step(Core.Netlist net, DeviceHost host, double dt, ref long tick)
    {
        host.Tick(dt);
        net.Solve(new TickClock(++tick, dt));
    }
}
