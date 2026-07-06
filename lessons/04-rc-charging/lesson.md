---
lesson: 4
slug: rc-charging
title: RC Charging
circuit: circuit.txt
analysis: transient
stop: 4.5
expectations:
  - name: source node stays pinned
    probe: [64, 128]
    time: 1.5
    value: 12.0
    tol: 1.0e-3
  - name: capacitor at one half tau
    probe: [192, 128]
    time: 1.5
    value: 4.7216
    tol: 0.02
  - name: capacitor at one tau
    probe: [192, 128]
    time: 3.0
    value: 7.5854
    tol: 0.02
  - name: capacitor at one and a half tau
    probe: [192, 128]
    time: 4.5
    value: 9.3224
    tol: 0.02
---

# Lesson 04 — RC Charging

## What This Shows

Time enters the story. Every circuit so far settled instantly: set the
source, read the answer. A **capacitor** refuses to be hurried — it is a
small tank for charge, and a tank takes time to fill. While it fills,
the voltage across it *climbs*, fast at first and ever slower, along the
famous charging curve.

**In-world payoff:** capacitors are why machines keep running for a
moment after you cut power, why big loads need a "soft start", and why
a freshly connected battery bank draws a violent inrush through a thin
wire. The *time constant* you compute here is the knob behind all of it.

## The Circuit

A `12.0 V` source charges a `1000 uF` capacitor through a `3.0 kohm`
resistor. One probe watches the source node (it stays pinned at
`12.0 V`); the other watches the capacitor's top plate. The product

```
tau = R x C = 3000 ohm x 0.001 F = 3.0 s
```

is the **time constant**: the natural unit of "how long this circuit
takes".

## Predict

1. **The start.** The cap begins empty (`0 V`), so at the first instant
   the FULL `12 V` sits across the resistor. How many amps flow at
   `t = 0`? (`amps = volts / ohms`.)
2. **The end.** Once the cap is full, no more current flows. What does
   the cap read then? What does the resistor drop?
3. **The middle.** The curve is `V = 12 x (1 - e^(-t/tau))`. With
   `tau = 3 s`, work out the reading at `t = 3 s` (one tau — the number
   worth memorizing is `63%`) and at `t = 4.5 s`.

## Observe

Run it and watch the second probe climb:

- At `t = 1.5 s` (half a tau): about `4.72 V` — already 39% of the way.
- At `t = 3.0 s` (one tau): about `7.59 V` — 63%, the universal
  one-tau checkpoint.
- At `t = 4.5 s`: about `9.32 V` on the cap, which leaves about
  `2.7 V` still across the resistor — the source's push not yet spent.
- The source probe never moves: `12.0 V` throughout. The *difference*
  between the two probes is the resistor's share, and it shrinks as the
  cap fills — which is exactly why the current (and the charging rate)
  dies away.

## Q/A

**Q: Why does charging slow down as the cap fills?**

A: The resistor only pushes current in proportion to the voltage left
across it: `amps = (12 - V_cap) / R`. As `V_cap` rises, that difference
shrinks, so the current shrinks, so the filling slows — each volt gained
makes the next volt slower. That self-throttling is what produces the
exponential curve.

**Q: Is the cap ever actually full?**

A: Mathematically it only approaches `12 V`; practically, after five
tau (`15 s` here) it is within 1% and we call it done. Rule of thumb:
one tau = 63%, five tau = finished.
