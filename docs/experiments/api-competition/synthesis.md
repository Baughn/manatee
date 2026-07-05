# Netlist API Competition — Synthesis

Date: 2026-07-05. Status: DECISION INPUT — the starting point for the real
API design, not yet canon. Three proposals (three lenses), three judge
personas (Re-Volt integrator, GC skeptic, long-term maintainer). Full
proposals and verdicts sit beside this file.

## Outcome

**Base: Proposal 1, "Four Verbs"** (client-ergonomics lens) — 2 of 3
judge votes, top-or-tied score from all three. Its core idea is R4
promoted from documentation to grammar: every mutation is `Drive`
(tier 1), `Adjust` (tier 2), `Reconfigure` (coupler open/close — the
one runtime-normal tier-3 op, so it gets its own verb), or a structural
edit that only exists on a `StructuralEdit` scope you must ask for
loudly. Supporting cast that every judge liked: the `EnterSteadyState`
guard region with a debug allocation tripwire, `TickStats` cost
counters as a public readback, the `Building` island state for
Stationeers' background 10k-segment load, and epsilon no-op `Adjust`
so converged G = P/V² adaptor loops stop paying tier-2 forever.

**The judges' steal lists converged almost perfectly** — treat these as
requirements on the merged design, not suggestions:

From Proposal 2 (compaction lens; won the maintainer's vote):
- **Pollable `TopologyJournal`** (fixed struct ring, cursors, explicit
  overflow ⇒ resync) as the compaction layer's sync mechanism — no
  callbacks anywhere.
- **Mandatory client-stable keys** (`StateKey`/`ExternalKey`) on every
  Add — restore-by-key is the only path, forgotten keys can't silently
  cold-start device state.
- **Generational handles that fail fast** (`StaleHandleException` in
  debug) — compaction bugs become named errors, not aliasing.
- **Two-level drift backstop** — cheap per-island fingerprint every N
  ticks; full canonical ExternalKey-sorted diff only on mismatch;
  typed `DriftReport` entries.
- **ClientPartitioned mode** — a commit that would merge across
  partition keys fails atomically (enforces Stationeers'
  networks-never-merge invariant in the API).
- Per-island **`AllocationSentinel`** in debug; explicit **tier-0
  "Meta" facade** (limit envelopes, probe weights — visibly free).

From Proposal 3 (testing lens):
- **`SpiceDeck.Emit`** with deterministic naming, a NodeId→rawfile
  column map, and an `Unrepresentable` list — the oracle differ knows
  its own fidelity. Deck text doubles as a Verify golden.
- **Invariants as public API** (KCL residual + worst node, energy
  audit, finiteness) — one code path for CI, the resync backstop, and
  the tablet's educational error messages.
- **ReproBundle / flight recorder** — but *gated to debug/opt-in* and
  with ring-wrap rebase moved off the sim thread (all three judges
  flagged the always-on version as an R8 violation). Boundary
  exchanges recorded as tier-1 commands so one island replays without
  its neighbors.
- **`SaveCanonical` vs `SaveNormalized`** split (replay-stable vs
  structural-equality golden).
- **Tier-budget assertions in CI** ("steady-state AC island:
  `Tier2Refactors == 0`") — solver.md's perf model as an executable
  contract. `CostOf(command)` query for Re-Volt's scheduler.

## Explicitly rejected

- **Always-on command recording in the hot path** (P3's default) — the
  facade→union→switch indirection plus 32 B/write in the tier-1 path,
  and rebase allocations mid-steady-state. Recorder becomes opt-in.
- **Journal-drain as a correctness obligation for game clients** (P2's
  default posture) — the journal serves the *compaction layer*;
  game-facing clients get the Four-Verbs surface and never babysit
  cursors.
- **Client-callable AC substep control** (P3) — contradicts solver.md's
  hysteresis-owned N.

## Open questions carried forward

1. P1's handle-survival contract is a landmine the maintainer judge
   called precisely: internal ids don't survive island rebuilds, and
   *any* removal rebuilds an island. The merge adopts P2's generational
   handles + mandatory keys, but the exact "which handle survives what"
   table must be written into solver.md before implementation.
2. `SteadyStateGuard` must be a struct (GC-skeptic: `using` must not
   allocate per tick).
3. P1's `Reconfigure` coalescing (split-rebuild deferred to next Solve)
   vs. P2's transactional commit semantics need one reconciled story
   for "what does the netlist look like between Reconfigure and Solve".
