using System.Diagnostics;

namespace Manatee.Core;

/// <summary>
/// Stack-only legibility rail (api.md §8). Inside the region the netlist is in
/// "no structural change" mode: tier-3 / <c>Reconfigure</c> attempts fail fast
/// in debug and, in release, are deferred to <see cref="Dispose"/> (same tick,
/// before Solve) and counted in <c>TickStats.DeferredStructuralOps</c> — never
/// dropped (api.md §8, Decision log #5). Zero heap allocation per tick.
/// </summary>
public ref struct SteadyStateGuard
{
    private Netlist? _net;
    private AllocationSentinel _sentinel;

    internal SteadyStateGuard(Netlist net)
    {
        _net = net;
        _sentinel = AllocationSentinel.Arm();
    }

    /// <summary>Restores the flag, asserts the allocation delta is zero (debug,
    /// where measurable), then runs any release-deferred structural ops.</summary>
    public void Dispose()
    {
        var net = _net;
        if (net is null) return;
        _net = null;
        _sentinel.Dispose();
        net.ExitSteadyState();
    }
}

/// <summary>
/// Standalone allocation tripwire (api.md §8), auto-armed inside
/// <see cref="SteadyStateGuard"/>. Best-effort: the binding zero-alloc gate is
/// BenchmarkDotNet MemoryDiagnoser in CI. Self-probes once and disarms
/// process-wide on runtimes where <c>GC.GetAllocatedBytesForCurrentThread</c> is
/// inert (Unity/Mono risk).
/// </summary>
public ref struct AllocationSentinel
{
    private long _armedBytes;
    private bool _armed;

    private static int s_reliability; // 0 unknown, 1 reliable, 2 inert

    /// <summary>Captures the per-thread allocated-bytes baseline.</summary>
    public static AllocationSentinel Arm()
    {
        var s = new AllocationSentinel();
        if (Probe())
        {
            s._armedBytes = GC.GetAllocatedBytesForCurrentThread();
            s._armed = true;
        }
        return s;
    }

    /// <summary>One-time runtime capability probe result.</summary>
    public static bool IsReliable => Probe();

    /// <summary>Debug: assert the allocation delta is zero.</summary>
    [Conditional("DEBUG")]
    public void Dispose()
    {
        if (!_armed) return;
        var delta = GC.GetAllocatedBytesForCurrentThread() - _armedBytes;
        Debug.Assert(delta == 0, $"AllocationSentinel: {delta} bytes allocated in a zero-alloc region.");
    }

    private static bool Probe()
    {
        var r = s_reliability;
        if (r != 0) return r == 1;

        var before = GC.GetAllocatedBytesForCurrentThread();
        var probe = new object[1];
        GC.KeepAlive(probe);
        var moved = GC.GetAllocatedBytesForCurrentThread() != before;
        s_reliability = moved ? 1 : 2;
        return moved;
    }
}
