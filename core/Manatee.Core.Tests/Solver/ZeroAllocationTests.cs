using Manatee.Core.Solver;

namespace Manatee.Core.Tests.Solver;

/// <summary>
/// R8 / api.md §21: after warmup, the tier-1 (Solve) and tier-2 (Factorize) paths
/// allocate zero bytes. This is the in-process best-effort check; the binding CI
/// gate is BenchmarkDotNet MemoryDiagnoser (api.md §8). Where
/// GC.GetAllocatedBytesForCurrentThread is inert (historically on Mono), the test
/// probes the counter and skips its assertion rather than reporting a false pass.
/// </summary>
public sealed class ZeroAllocationTests
{
    [Fact]
    public void Warmed_up_factorize_and_solve_allocate_nothing()
    {
        if (!CounterIsReliable())
            return; // counter inert on this runtime — best-effort check skipped

        var system = SystemFixtures.LadderA(200); // SPD, well-conditioned ⇒ frozen path
        var backend = new SparseLuBackend();
        backend.Analyze(system.Dimension, system.Pattern);

        var x = new double[system.Dimension];

        // Warmup: first Factorize allocates (symbolic); a second lands on the
        // zero-alloc frozen refactor path, plus a Solve to warm that too.
        backend.Factorize(system.Values);
        backend.Factorize(system.Values);
        backend.Solve(system.Rhs, x);

        // No fallback must have fired — otherwise we'd be measuring the cold path.
        Assert.Equal(1, backend.FullFactorizations);

        var before = GC.GetAllocatedBytesForCurrentThread();
        for (var r = 0; r < 20; r++)
        {
            backend.Factorize(system.Values);
            for (var s = 0; s < 5; s++)
                backend.Solve(system.Rhs, x);
        }
        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        Assert.Equal(1, backend.FullFactorizations); // still on the frozen path
        Assert.True(allocated == 0,
            $"tier-1/2 loop allocated {allocated} B over 20 refactor + 100 solve (expected 0)");
    }

    // One-time capability probe (api.md §8 AllocationSentinel pattern): allocate a
    // small object and confirm the per-thread counter observed it.
    private static bool CounterIsReliable()
    {
        var before = GC.GetAllocatedBytesForCurrentThread();
        var probe = new byte[4096];
        GC.KeepAlive(probe);
        return GC.GetAllocatedBytesForCurrentThread() - before > 0;
    }
}
