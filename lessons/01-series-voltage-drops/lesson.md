---
lesson: 1
slug: series-voltage-drops
title: Series Voltage Drops
circuit: circuit.txt
analysis: dc
expectations:
  - name: source node
    probe: [0, 128]
    value: 12.0
    tol: 1.0e-3
  - name: junction after the 100 ohm resistor
    probe: [128, 128]
    value: 10.0
    tol: 1.0e-3
  - name: junction after the 200 ohm resistor
    probe: [192, 128]
    value: 6.0
    tol: 1.0e-3
---

# Lesson 01 — Series Voltage Drops

## What This Shows

One loop, one path. When current has only a single way to go, the same
flow passes through every part in the chain — there is nowhere else for
it to be. The source's voltage, though, gets *divided up* along the way:
each resistor takes a share, and the bigger the resistance, the bigger
the share.

**In-world payoff:** every meter of cable between your generator and
your machine is a small resistor sitting in series with it. Series
drops are why a lamp at the far end of a long thin wire glows dim —
the wire quietly took its cut first. (Lesson 14 turns this into the
long-distance transmission problem.)

## The Circuit

A `12.0 V` source — think car battery — drives a single chain of three
resistors: `100 ohm`, `200 ohm`, `300 ohm`. Three probes read the
voltage at the start of the chain and at the two junctions between
resistors. The far end of the chain returns to the battery's other
terminal, which we call `0 V` (ground) — voltages are always measured
*relative to* somewhere, and ground is our somewhere.

## Predict

Work these out before pressing run. Arithmetic only.

1. **Total resistance.** One path means resistances simply add:
   `100 + 200 + 300 = ?`
2. **Current.** The pattern to test all lesson long:
   `amps = volts ÷ ohms`. With `12.0 V` across your total from step 1,
   how many amps flow around the loop?
3. **The middle probes.** Each resistor eats voltage according to
   `volts = amps × ohms` — same amps everywhere (one path!), so each
   resistor's share is proportional to its size. Starting from `12.0 V`
   at the first probe, what should the second and third probes read?

## Observe

Run the circuit and check yourself:

- Total resistance: `600 ohm`.
- Loop current: `12.0 ÷ 600 = 0.02 A` — the tablet shows it as `20 mA`
  (`1 A = 1000 mA`).
- Probes, left to right: `12.0 V`, `10.0 V`, `6.0 V`.
- So the drops are `2.0 V` across `100 ohm`, `4.0 V` across `200 ohm`,
  `6.0 V` across `300 ohm`. Ratio `1 : 2 : 3` — exactly the ratio of
  the resistances. Add the drops back up: `2 + 4 + 6 = 12`. Everything
  the source pushed out got used; nothing is left over and nothing is
  missing. That bookkeeping always balances, in the tablet and in the
  real world.

## Q/A

**Q: Why is the current the same through all three resistors?**

A: There is only one path. Whatever flows into a resistor must flow out
the other side — charge doesn't pile up and doesn't leak. One path in a
loop means one flow rate everywhere, the way one queue has one
throughput no matter which point of the queue you watch.

**Q: Why does the `300 ohm` resistor take three times the voltage of
the `100 ohm` one?**

A: `volts = amps × ohms`, and the amps are identical for both. Triple
the ohms, triple the volts. Resistance is each part's claim on the
source's voltage; in series, shares are dealt out in proportion.

**Q: Which resistor runs warmest?**

A: The `300 ohm`. Heat is `volts × amps`: `6.0 V × 0.02 A = 0.12 W`,
against `0.08 W` for the `200 ohm` and `0.04 W` for the `100 ohm`.
Same current, biggest drop, most heat — remember this when a cable
run is doing the dropping instead of a resistor.

**Q: What happens if one resistor is removed, leaving a gap?**

A: Everything stops. One path means one point of failure — break it
anywhere and the current everywhere is zero. That is why old
series-wired holiday lights all died when one bulb did.

**Q: I doubled the `100 ohm` to `200 ohm`. Why did *every* number
change?**

A: The chain is one system. Total resistance became `700 ohm`, so the
shared current fell to about `17 mA`, so every drop changed. In series,
no part can be changed in isolation — a fact you will feel again when
a long cable and a machine share one path.
