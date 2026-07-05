---
lesson: 2
slug: parallel-branch-currents
title: Parallel Branch Currents
circuit: circuit.txt
analysis: dc
expectations:
  - name: bus voltage
    probe: [128, 128]
    value: 12.0
    tol: 1.0e-3
  - name: branch A sense node (100 ohm branch, 0.12 A)
    probe: [128, 192]
    value: 1.2
    tol: 1.0e-3
  - name: branch B sense node (200 ohm branch, 0.06 A)
    probe: [256, 192]
    value: 0.6
    tol: 1.0e-3
  - name: branch C sense node (300 ohm branch, 0.04 A)
    probe: [384, 192]
    value: 0.4
    tol: 1.0e-3
---

# Lesson 02 — Parallel Branch Currents

## What This Shows

Lesson 01 had one path, one shared current, divided voltage. Flip it:
give the current *several* paths side by side and the roles swap. Now
every branch gets the same **voltage** — they all hang between the same
`12.0 V` bus and ground — and it is the **current** that divides.
The branch with the least resistance carries the most current.

**In-world payoff:** this is your power bus. Every machine you wire
across the same pair of cables is a parallel branch. Each one takes
the current it takes, the hungriest (lowest-resistance) machine takes
the most, and your generator feels the *sum* of all of them.

## The Circuit

A `12.0 V` source feeds a top rail; three branches drop from it to the
bottom rail (ground). The branches total `100 ohm`, `200 ohm`, and
`300 ohm` — the same trio of values as lesson 01, now side by side
instead of in a chain.

One trick to learn here: the probes on the tablet read *voltage*, not
current. So each branch is built as a big resistor plus a small
`10 ohm` **sense resistor** at the bottom (`90+10`, `190+10`,
`290+10`). Run `volts = amps × ohms` backwards: read the volts across
a known `10 ohm`, divide by `10`, and you have that branch's amps.
This is not a classroom hack — real ammeters measure current exactly
this way, with a small built-in sense resistor called a shunt.

## Predict

1. **Branch currents.** Each branch has the full `12.0 V` across its
   total resistance. `amps = volts ÷ ohms`:
   `12.0 ÷ 100 = ?`, `12.0 ÷ 200 = ?`, `12.0 ÷ 300 = ?`
2. **Sense probes.** Each sense probe should read its branch current
   `× 10 ohm`. What are the three readings?
3. **Total draw.** Whatever flows out into the branches must add back
   up where the paths rejoin — flow in equals flow out at every
   junction, always. What current does the source supply?
4. **The surprise.** The source sees all three branches at once, as if
   they were one single resistor. Before computing it: is that combined
   resistance *bigger* or *smaller* than `100 ohm`, the smallest
   branch?

## Observe

- Branch currents: `0.12 A`, `0.06 A`, `0.04 A` (`120`, `60`, `40 mA`).
  Lowest resistance, highest current — twice the ohms, half the amps.
- Sense probes: `1.2 V`, `0.6 V`, `0.4 V`. Divide each by `10` and you
  get the currents above.
- Total draw: `0.12 + 0.06 + 0.04 = 0.22 A`. The junction bookkeeping
  balances exactly, just like the voltage bookkeeping did in lesson 01.
- Combined resistance: `12.0 ÷ 0.22 ≈ 54.5 ohm` — *smaller than any
  single branch.* Every path you add makes the total easier to push
  through, never harder: more open checkout lanes, faster the crowd
  drains. Adding a machine to your bus always *increases* the load on
  the generator, even though you added resistance.

## Q/A

**Q: Why does every branch get the full `12.0 V`?**

A: Each branch's top connects straight to the source's `+` rail and
its bottom straight to ground. Voltage is a difference between two
points; all three branches span the same two points, so they all see
the same difference. (Real cables have a little resistance, so distant
branches see slightly less — that is lesson 14's problem.)

**Q: The `100 ohm` branch carries three times the current of the
`300 ohm` branch. Why exactly three?**

A: Same volts, so `amps = volts ÷ ohms` makes current inversely
proportional to resistance. One third the ohms, three times the amps.

**Q: Doesn't the `10 ohm` sense resistor mess up the measurement?**

A: It *is* part of what it measures — we made it part of the plan. The
branch totals (`100`, `200`, `300 ohm`) already include it, so the
numbers come out exact. Kept small next to the rest of the branch, a
shunt disturbs the circuit as little as possible; that trade-off is
real instrument design.

**Q: What happens to branches A and B if branch C is disconnected?**

A: Nothing. Each branch answers only to the `12.0 V` across it, and
that doesn't change. A and B still carry `0.12 A` and `0.06 A`; only
the total falls, to `0.18 A`. This independence is exactly why
buildings are wired in parallel — one lamp off doesn't black out the
house. (Contrast lesson 01, where touching any part changed every
number.)

**Q: If adding branches always lowers the combined resistance, what is
the limit of adding more and more?**

A: The combined resistance heads toward zero and the total current
climbs toward "everything the source can give". Overloading a bus is
just this lesson taken too far — lesson 03 pushes it all the way to a
short circuit, safely inside the tablet.
