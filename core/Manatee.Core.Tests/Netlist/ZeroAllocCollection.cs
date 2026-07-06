using Xunit;

namespace Manatee.Core.Tests;

/// <summary>
/// Serialized home for every test that measures <c>GC.GetAllocatedBytesForCurrentThread</c>
/// around a code region (the zero-alloc gates and the AllocationSentinel-armed guard
/// tests).
///
/// WHY (2026-07-06 flake root-cause, REVISED after the first fix shipped and the flake
/// persisted): the per-thread allocated-bytes counter can over-report when GC/JIT
/// machinery retires this thread's partially-used allocation context mid-measurement.
/// There are two perturbation sources, and serialization only removes one of them:
///
///   1. Concurrent compacting GCs triggered by promotion-heavy sibling tests on other
///      xunit worker threads — removed by this collection's DisableParallelization.
///   2. PROCESS-WIDE background GC and tiered-JIT recompilation, which outlive any
///      xunit collection boundary: heavy sibling collections (e.g. the ngspice Oracle
///      suites) leave background GC threads that fire DURING this collection's
///      serialized turn. Observed as small (hundreds-of-bytes) phantom deltas that
///      reproduce only in full-assembly order, never in isolation.
///
/// Serialization is therefore BEST-EFFORT noise reduction, not the correctness
/// mechanism. The sound mechanism — REQUIRED for every gate in this collection — is
/// min-over-N-sub-runs (the TierBudgetGateTests pattern): the perturbation is strictly
/// additive, so a single clean sub-run (min == 0) proves the 0-alloc property. The
/// product paths under test allocate zero bytes, verified over hundreds of isolated
/// passes; a single-shot delta assertion is a flake, not a gate.
/// </summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class ZeroAllocCollection
{
    public const string Name = "ZeroAlloc";
}
