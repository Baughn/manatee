# Curriculum

Last updated: 2026-07-05
Status: DRAFT — arc and format settled; individual lessons to be authored.

The tablet's lesson set, per design.md R16/R20. Ceiling: **AC power**
(transformers, impedance, power factor, synchronization). Audience: curious
adult gamers, no math assumed; rigor emerges through predict-then-observe
play.

## Format

One directory per lesson, EA `docs/examples` style
(`third_party/ElectricalAge/docs/examples` is the seed corpus and format
reference):

- `lesson.txt` — Falstad-format netlist (importable in the tablet, parseable
  by CI).
- `README.md` — narrative: what this shows, what to observe, Q/A in
  predict-then-observe form, with **numeric expectations** stated in the
  text.
- Front-matter block — machine-readable expectations (probe, time, value,
  tolerance) so CI can verify the narrative against the solver
  (testing-strategy.md). *A lesson that stops being true fails the build.*

Component values follow the EA trick: scaled so time constants are visible
at game tick rates (documented per lesson).

## Lesson Arc (draft sequence)

**I. DC foundations** — mirrors EA's proven order:
1. Series voltage drops (one path; dividers; bigger R drops more).
2. Parallel branch currents (lower R carries more).
3. The shocking truth about short circuits (why the wire got hot — first
   hazard lesson, safe inside the tablet).
4. RC charging curve.
5. RLC ring-down (energy sloshing between C and L).
6. Batteries are not ideal (internal resistance; why the pile sags under
   load; why zinc disappears).

**II. Switching and control:**
7. Switches, fuses by design (the weakest link is a choice).
8. The relay (a switch driven by a circuit — control as circuitry).
9. Relay logic: latch, interlock (the elevator-brain prerequisite).
10. The elevator controller (capstone: call buttons, limit switches,
    latching relay — mirrors the in-world build).

**III. AC power:**
11. What alternation looks like (oscilloscope lesson; the 5 Hz flicker
    explained).
12. Frequency comes from rotation (shaft speed, pole pairs; why 50 Hz).
13. The transformer (turns ratio; why it only works on AC; why 5 Hz
    transformers are enormous).
14. Line loss and why we transmit high-voltage (ties to the 12 V cottage
    problem the player has personally suffered). Includes the
    ground-return coda: "copper is expensive — when can you get away
    with one wire?" SWER's electrode-loss arithmetic shows it works at
    240 V distribution and for milliamp signalling, and fails for 12 V
    loads (design.md Grounding model, revised 2026-07-05).
15. Impedance: capacitors and inductors on AC (phase, without the word
    "phasor").
16. Power factor (the sloshing current that heats cables but does no work).
17. Synchronization (two generators, one grid; the spring between rotors —
    capstone).

**IV. Appendices** (unlocked, non-sequential): metric prefixes in readouts
(EA precedent), reading the multimeter/oscilloscope, the hazard reference
(what voltage does to you; lockout-tagout; the verified-floating ritual —
"one wire of a floating system is safe to touch" holds only after the
wire-to-earth multimeter check, because wet bare spans re-ground silently).

## Authoring Rules

- Every lesson names the in-world payoff ("this is your electric fence")
  where one exists — the tablet teaches the mod while teaching reality.
- Predict-then-observe: ask for the number *before* showing it.
- No lesson requires math beyond arithmetic and ratios; formulas appear as
  patterns to verify against the meter, not derivations.
- Real-world truthfulness check: any statement about reality (not just the
  sim) gets reviewed against a real reference before shipping — the
  educational mission makes errors here worse than bugs.
- Transformer lessons use the *idealized* transformer class (design.md,
  Simulation Model): a decoupling boundary launders reactive power, so
  power-factor and impedance behavior must be demonstrated same-island.
  The tablet uses idealized transformers throughout.
