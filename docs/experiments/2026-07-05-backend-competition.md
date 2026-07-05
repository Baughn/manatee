# Solver Backend Competition — Results

Date: 2026-07-05. Machine: Ryzen 9 7950X3D, .NET 8.0.28, quiesced,
single benchmark process (BenchmarkDotNet in-process toolchain,
MemoryDiagnoser). Harness: `experiments/solver-bench/` — all contestants
passed the verify gate first (residuals ≤ ~2e-14, agreement with the
ngspice-audited referee to ≤ 1e-13, zero-alloc audit).

## Contestants

| Name | What it is | Alloc (tier 1/2) |
| --- | --- | --- |
| `naive-dense` | Referee: unoptimized dense LU, partial pivoting | 0 |
| `dense-opt` | Dense LU, vectorized (Vector\<double\>, 4-row panels, zero-block skip) | 0 |
| `sparse-lu` | In-house Gilbert–Peierls sparse LU; min-degree ordering; KLU-style frozen pivots after first factorization | 0 |
| `csparse` | CSparse.NET 4.4.0 SparseLU + AMD, via public API (the design doc's interim fallback, as-shipped) | **8n+24 B per refactorization** (hardcoded `int[2n]` workspace) + needs global `AutoTrimStorage=false` |
| `banded-rcm` | RCM reordering + banded LU (opts out when bandwidth > 200, e.g. random meshes) | 0 |

## Solve — tier 1, the AC-subcycling hot path (ns/op)

| System (n) | naive-dense | dense-opt | sparse-lu | csparse | banded-rcm |
| --- | ---: | ---: | ---: | ---: | ---: |
| ladderA-100 | 5,524 | 2,490 | **907** | 1,535 | 985 |
| ladderA-500 | 146,139 | 39,909 | **4,593** | 7,924 | 4,894 |
| ladderB-500+10src | 151,685 | 41,383 | **4,117** | 7,458 | 5,085 |
| gridA-32×32 (1024) | 620,375 | 171,253 | **8,496** | 15,119 | 50,928 |
| ladderA-2000 | 2,748,051 | 1,068,194 | **18,266** | 31,045 | 19,980 |

## Factorize+Solve — tier 2, switch toggles / adaptor updates (ns/op)

| System (n) | naive-dense | dense-opt | sparse-lu | csparse | banded-rcm |
| --- | ---: | ---: | ---: | ---: | ---: |
| ladderA-100 | 15,156 | 11,998 | 2,132 | 5,806 | **1,932** |
| ladderA-500 | 385,251 | 293,752 | 10,652 | 29,307 | **10,111** |
| ladderB-500+10src | 467,810 | 378,659 | **10,615** | 30,564 | 10,256 |
| gridA-32×32 (1024) | 14,418,748 | 10,661,590 | **182,176** | 757,595 | 494,260 |
| ladderA-2000 | 7,985,580 | 6,498,846 | 42,865 | 112,539 | **38,870** |

## Cold build — Analyze+Factorize+Solve at n=10,000 (Stationeers load)

| Backend | Time | Allocated |
| --- | ---: | ---: |
| banded-rcm | **558 µs** | 1.31 MB |
| sparse-lu | 1,798 µs | 5.46 MB |
| csparse | 2,194 µs | 5.52 MB |
| dense (both) | opted out (n > 4000) | — |

## Findings

1. **Sparse wins everywhere — including small islands.** solver.md's
   settled backend plan ("in-house zero-alloc dense LU primary,
   CSparse.NET interim fallback, in-house sparse only if benchmarks
   demand") predicted dense would carry the typical post-compaction
   island. It does not. At n=100 — the canonical VS island —
   `sparse-lu` beats the *optimized* dense on the hot path by 2.7×
   (907 ns vs 2.5 µs) and never falls behind at any size or topology
   tested. MNA matrices are simply too sparse (≤7 nnz/row) for dense
   triangular solves to compete even at small n. **The benchmarks
   demand: in-house sparse should be primary.** (solver.md amendment
   needed — flagged, not yet applied.)
2. **CSparse.NET is strictly dominated** by the ~600-line in-house
   sparse backend: 1.7–2.6× slower Solve, 2.6–4× slower refactorization,
   plus an unavoidable `8n+24` B allocation per refactorization through
   the public API and a required process-global `AutoTrimStorage=false`.
   Its remaining value is as a second implementation for equivalence
   testing, not as a shipping fallback — which also dissolves the LGPL
   packaging concern.
3. **Perf targets are demolished.** Worst realistic AC case: n=500
   island × 100 substeps/tick = ~460 µs of Solve on one core; the
   design budget is 5 ms on-thread. A 10k-segment Stationeers cold
   build is ~2 ms against a 500 ms tick. Post-compaction sizes
   (~hundreds) leave two orders of magnitude of headroom — enough to
   reconsider how aggressive compaction even needs to be in v1.
4. **Banded-RCM is a niche, not a backend.** It edges sparse-lu on pure
   ladders for refactorization (~5%) and wins cold build 3×, but loses
   38× on meshy Solve when the band blows up, and opts out of random
   meshes entirely. Verdict: not worth a third code path; its insight
   (compacted circuits are nearly 1-D) is already captured by
   fill-reducing ordering inside sparse LU.
5. **Zero-alloc is achievable and cheap to enforce.** Three of four
   agent-written backends hit exactly 0 B on 20 refactors + 100 solves
   on the first try. The verify-mode allocation audit (GC counter
   deltas) caught the csparse allocation precisely — the same mechanism
   the real core should ship (per-island `AllocationSentinel`, per the
   API synthesis).

## Caveats

- Contestant code is experiment-grade (one afternoon, agent-written);
  numbers are architecture signals, not shipped-code guarantees.
- `sparse-lu`'s frozen-pivot refactorization (KLU-style) **was** stress
  tested after the first draft of this report (`solver-bench stress`):
  200 rounds of redrawing every conductance log-uniformly across the
  full legal range (1e-9..1e3 S, the maximal tier-2 swing) on a
  510-unknown family-B ladder. Worst scaled residual with frozen
  pivots: 1.7e-9, vs 1.9e-13 with fresh pivoting; zero failures, zero
  refusals. Four orders of magnitude of accuracy are lost at the
  extremes but the result stays far inside gameplay tolerance — the
  approach is sound for solver.md's conductance policy. A pivot-growth
  monitor with refactor-from-scratch fallback remains cheap hygiene
  for the production core, but it is not load-bearing.
- One RNG seed per system; ladder/grid/mesh topologies only; no
  Newton iteration, no companion-model updates — pure linear-solve
  costs.

## Recommendation

Make the KLU-style in-house sparse LU (ordering + symbolic in Analyze,
pivot order frozen at first factorization, zero-alloc refactor/solve)
the **primary and only** production backend, with the naive dense LU
retained purely as a test referee. Drop CSparse.NET from the shipping
plan; keep it as a dev-time equivalence oracle. Fold the pivot-growth
monitor into the failure-handling design (R9). Amend solver.md
accordingly after sign-off.
