using Manatee.Core.Solver;

namespace Manatee.Core.Tests.Solver;

/// <summary>
/// R8 / api.md §21: after warmup, the tier-1 (Solve) and tier-2 (Factorize) paths
/// allocate zero bytes. This is the in-process best-effort check; the binding CI
/// gate is BenchmarkDotNet MemoryDiagnoser (api.md §8). Where
/// GC.GetAllocatedBytesForCurrentThread is inert (historically on Mono), the test
/// probes the counter and skips its assertion rather than reporting a false pass.
/// </summary>
[Collection(Manatee.Core.Tests.ZeroAllocCollection.Name)]   // serialized: the GC counter reads phantom bytes under sibling compaction (see ZeroAllocCollection)
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

        // Min over sub-runs: process-wide background GC / tiered JIT can add phantom
        // bytes to the per-thread counter even inside the serialized collection (see
        // ZeroAllocCollection); the perturbation is strictly additive, so one clean
        // sub-run (min == 0) proves the 0-alloc property.
        long best = long.MaxValue;
        for (var run = 0; run < 8 && best != 0; run++)
        {
            var before = GC.GetAllocatedBytesForCurrentThread();
            for (var r = 0; r < 20; r++)
            {
                backend.Factorize(system.Values);
                for (var s = 0; s < 5; s++)
                    backend.Solve(system.Rhs, x);
            }
            var d = GC.GetAllocatedBytesForCurrentThread() - before;
            if (d < best) best = d;
        }

        Assert.Equal(1, backend.FullFactorizations); // still on the frozen path
        Assert.True(best == 0,
            $"tier-1/2 loop allocated {best} B over 20 refactor + 100 solve (min over 8 runs; expected 0)");
    }

    /// <summary>api.md §8 open item #2: every standing zero-alloc gate early-returns
    /// when the per-thread allocation counter is inert, so on an arbitrary runtime
    /// they can silently degrade to no-ops. The PINNED devshell/CI runtime (net8.0
    /// CoreCLR) has a live counter — assert it, so a future runtime bump that muted
    /// the counter turns the zero-alloc promise loudly red instead of
    /// green-by-vacuity. Category=Oracle: devshell-pinned, hard-fails elsewhere by
    /// design (same policy as the ngspice gates).</summary>
    [Fact]
    [Trait("Category", "Oracle")]
    public void Allocation_counter_is_live_on_the_pinned_ci_runtime()
        => Assert.True(CounterIsReliable(),
            "GC.GetAllocatedBytesForCurrentThread is inert on this runtime — every zero-alloc gate is silently skipping (api.md §8).");

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
