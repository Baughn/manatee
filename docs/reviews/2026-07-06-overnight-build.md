# Overnight Build Report — R1–R11 (2026-07-06 → 07)

**Outcome: manatee-core is built.** All of R1–R11 plus the Falstad importer,
lesson-corpus CI, fake-client integration tests, and benchmark suites.
362 tests green including 34 live-ngspice oracle tests, zero warnings, zero
skips, ~25.5k lines of C# across ten commits. Nothing is stuck; several
items below want your judgment before the next integration step.

## Commit ledger

| Commit | Contents |
| --- | --- |
| `eb1c8507` | API canon: docs/api.md (4 open questions resolved) |
| `136d0846` | Solver layer: sparse LU, stamps, Circuit |
| `62b7e9fa` | Netlist layer: Four Verbs document + DC pipeline |
| `f1fd550f` | Analyses: BE transient, subcycled AC, Newton, oracle wave |
| `1efd097e` | Island coupling: lockstep units, exchange, ledgers |
| `85cb9f36` | Coupling conservation: debt droop + cap-as-port converter |
| `6fcbba62` | State/limits/probes + Falstad importer + lesson goldens |
| `d9fc3d3e` | Reduction layer + devices layer |
| `802397f0` | Hardening (Fable track) + fake clients + benchmarks |
| (this)     | Final wave: whole-codebase review, simplification, docs sync |

## The night's headline catches (the multi-agent review machinery working)

1. **Two free-energy pumps** in the boundary exchange (oscillating loads
   laundering sub-deadband over-delivery, ~0.017 J/tick) — root-fixed to
   machine precision (5e-12 J/tick across the attack sweep).
2. **Debt-droop numerical instability at α ≥ 0.8** (divergence to 1e73 J on
   a 1152-point grid) — found by the *final* wave; fixed with an exchange
   clamp `TransformerAlphaMax = 0.7`, and the grid is now a standing gate.
3. **Four phase-6 blockers**: saves loading to 0 V; coupler runtime surviving
   nothing; limits evaluated tick-boundary-only (phase-blind — a fuse on a
   sine zero-crossing never heats); a miswired Falstad rail.
4. **i²t hybrid-envelope canon contradiction**: the spec'd per-limit-type
   minimum stamped a fictional component (X's rating + Y's melt) that
   tripped when no raw segment would — resolved by ruling (below).
5. **An engine bug found by audit, not test**: reused component slots
   inherited the previous occupant's i²t accumulator.
6. The flaky zero-alloc tests were **root-caused to a real GC phenomenon**
   (allocation-context retirement under concurrent compacting GC over-reports
   by up to ~8 KB), proven standalone, and isolated properly.

## Canon rulings I made under delegated authority

All recorded in docs/api.md with rationale; decision log at §24.

- The four api-competition open questions (handle-survival table with
  document-stable couplers/probes; ref-struct guard; transactional-document/
  coalesced-matrix Reconfigure story; three-mode WiringPolicy).
- **Conservation is physical, not clerical** (§7): transformer boundaries
  carry an R18-style energy-debt droop; the converter's B port *is* the
  DC-link capacitor (sags = honest brownout). Ledgers record, never license.
- **i²t envelopes are Pareto sets** (§19): per-pair accumulation is exactly
  raw-equivalent under shared series current; limit-event equivalence
  raw-vs-reduced is now a standing test.
- Instantaneous limit events **coalesce** to one per (component, kind) per
  tick, worst value (§12). Evaluation stays per-substep.
- Sine sources in non-Mixed profiles: legal, single-sampled, debug-warned.
- `Edit()` inside the steady-state guard throws in all build modes (§8).
- Reduction shadow is deliberately **not serialized** — geometry is the
  client's truth, re-driven at load; probe keys mint deterministically from
  (SegmentKey, along) so re-driven intake converges (§13/§19).

## Wants your judgment (morning review list)

**Sukasa relay items** (api.md §23.1 + new): guard-barred Reconfigure
timing; the one-global-tick execution model; closed-breaker
bidirectionality (back-feed vs vanilla's directional transfer);
ClientPartitioned atomic rejection ergonomics; **new tonight:** the
`TransformerAlphaMax = 0.7` clamp (integration-visible physics change for
any α > 0.7 config).

**Design-review items, all conservative-direction but user-visible:**
- Droop **choke latches under continuous pump-drive**: a perpetually
  blinking load through a decoupling transformer decays its output toward
  ~0 until the blinking stops. Never invents energy; possibly confusing.
- One-time pre-choke over-delivery is bounded at ~15–25 J worst case (not
  the "one substep of lag" the first draft claimed; api.md states the
  honest bound).
- Droop/choke constants are **audit-tuned, not first-principles** — a
  principled loop-gain observer is a candidate replacement.
- **EnvI2t positional reset**: a snapshot taken across a Pareto-frontier
  membership change (e.g. ambient flip) reloads a hot cable cold.
- **Floating nonlinear islands** can reach spurious Newton operating points
  under `ExplicitOnly` wiring (api.md §23.8a; real wiring policies immune).
- The 0.7 stability bound is grid-proven only over the declared domain
  (α ∈ [0.3,1.0], loop gain ≤ 4, the two-transformer rig).

**Deferred contracts (documented, not defects):**
- **Parallel-Step** (per-unit TickStats, off-thread compute/commit split) is
  implemented serial-only; the binding contract must be closed by whichever
  phase first enables concurrent Step (api.md §9/§21 carry the spec).
- Recorder/ReproBundle remain minimal opt-in stubs (canon-deferred).
- Lesson corpus holds 01, 02, 04; lessons 03 and 05–17 are unauthored
  (curriculum.md work, not core work).
- Reduction `AddSegment` recompacts the whole shadow per edit (correct,
  coalesced; the incremental fast path is a perf item against the
  10k-segment target — benchmark exists to measure it).
- Mono/Unity allocation-counter behavior needs on-device verification
  (§23.2); CI MemoryDiagnoser is the binding gate regardless.
- One CsCheck gain-loop property flaked twice early in the night; after the
  threads:1 determinism fix and the α clamp it has not reproduced. If it
  ever does, the seed now actually reproduces it.

## Verification state

- `nix develop -c dotnet test Manatee.sln`: **362/362 green** (34 oracle
  tests spawning real ngspice; hard-fail-if-absent verified).
- Zero-warning build on netstandard2.1 + net8.0.
- Serialization laws 1–4 as property tests (law 4 bit-for-bit, including
  converter-coupled islands and mid-melt fuses).
- Conservation: windowed physical audits + the 1152-point stability grid +
  pump-attack suites, all standing.
- Fake Re-Volt (40 partitions, forced overflow re-pin, faulted fallback) and
  fake VS (AC+DC, live-run chisel edit, attribute→melt) clients run the full
  documented tick orders with per-tick assertions.
- Benchmarks: suites in place with MemoryDiagnoser + tier-budget gates;
  smoke-verified only — full BDN runs not executed tonight (`scripts/bench.sh`).

## Suggested next steps

1. Your review of api.md §23/§24 + this report's judgment list.
2. Relay the Sukasa items.
3. Per delivery order: the 2D schematic harness (harness.md) — the tablet
   engine consuming the now-real core, and the lesson-authoring loop.
