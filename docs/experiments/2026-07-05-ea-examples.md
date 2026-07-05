# Electrical Age lesson-example inventory

Examples directory (verified): `/home/svein/dev/manatee/third_party/ElectricalAge/docs/examples/`
(the only `docs` tree in the checkout; confirmed by directory walk). Structure: one directory per example containing `README.md` + an importable `<name>.txt` Falstad netlist + `assets/*.png` (schematic and equation images), plus two top-level files: `README.md` (corpus index, tool usage, simulator caveats) and `metric-prefixes.md`.

## Inventory (4 examples + 2 support docs)

### 1. Series Voltage Drops
- Paths: `third_party/ElectricalAge/docs/examples/series-voltage-drops/README.md`, `.../series-voltage-drops.txt`
- Circuit: 3.0 V source through 100/200/300 ohm resistors in series to ground; 3 voltage probes (`O`) at the intermediate nodes. Expected: 5 mA loop current; node voltages 3.0 / 2.5 / 1.5 / 0 V.
- Teaching point: one current path; voltage drops proportional to R; voltage dividers; open circuit stops everything.
- Falstad-reusable: **yes.** Plain-text netlist using elements `$` (sim params), `v`, `w`, `r`, `g`, `O`. Netlist in `.txt` is byte-identical in content to the fenced block in the README (spot-checked).

### 2. Parallel Branch Currents
- Paths: `third_party/ElectricalAge/docs/examples/parallel-branch-currents/README.md`, `.../parallel-branch-currents.txt`
- Circuit: 3.0 V bus feeding three branches (90+10, 190+10, 290+10 ohm), each with a 10 ohm *sense resistor* so a voltage probe reads branch current indirectly (300/150/100 mV → 30/15/10 mA).
- Teaching point: multiple paths; lower-R branch draws more current; total draw is the sum; removing one branch doesn't break the others. The sense-resistor idiom is itself a reusable measurement pattern.
- Falstad-reusable: **yes** (`v`, `w`, `r`, `g`, `O`).

### 3. RC Charging Curve
- Paths: `third_party/ElectricalAge/docs/examples/rc-charging/README.md`, `.../rc-charging.txt`
- Circuit: 3.0 V → switch (`s`) → 20 ohm → 0.25 F capacitor, with a 200 ohm discharge resistor across the cap. τ_charge ≈ 4.5 s (~90 ticks at EA's 20 Hz), τ_discharge = 50 s; settles ≈ 2.7 V (divider effect of the bleed resistor).
- Teaching point: exponential charging, time constant, why the curve flattens, deliberate leakage path. Demonstrates EA's "scale L/C so dynamics are visible at game tick rate" trick, which curriculum.md already adopts.
- Falstad-reusable: **yes** (adds `s` switch and `c` capacitor elements).

### 4. RLC Ring-Down
- Paths: `third_party/ElectricalAge/docs/examples/rlc/README.md`, `.../rlc.txt`
- Circuit: 3.0 V charges C = 0.1 F through a switch; open it and the LC tank (L = 10 H, R = 1 ohm) rings down. Natural period ≈ 6.28 s (~126 ticks at 20 Hz); 2 probes.
- Teaching point: energy sloshing between electric and magnetic storage; resistive damping; period ∝ √(LC).
- Falstad-reusable: **yes** (adds `l` inductor element).

### Support docs (not circuits, but corpus format precedent)
- `third_party/ElectricalAge/docs/examples/metric-prefixes.md` — prefix table (µ/m/k/M) with readout examples. Maps directly to curriculum.md's Appendix IV "metric prefixes in readouts (EA precedent)".
- `third_party/ElectricalAge/docs/examples/README.md` — corpus index: suggested order, predict-before-import advice, and a "Simulator Caveats" section (rounding, wires-as-low-R-not-ideal-nodes, single-point voltage sources, ground reference, 20 Hz logger floor, oversized L/C rationale with a real-world safety warning). This framing is the template manatee's corpus index should mirror.

## Reusability notes for the R20 corpus seed

- All four netlists are Falstad-format text and directly parseable seeds. Element vocabulary a manatee importer must cover for the seed set: `$ v w r g O s c l`.
- **Caveat:** the netlists contain `#`-prefixed comment lines (e.g. `# Series resistor lesson:`). Stock Falstad exports do not emit comments — this is an EA importer extension. The manatee lesson parser must accept `#` comments (curriculum.md adopts EA style, so this is a feature, not a blocker).
- READMEs already follow the shape curriculum.md specifies (What This Shows / What To Observe with numeric expectations / Math / predict-then-observe Q&A / Import Text). Missing vs. manatee's format: the machine-readable front-matter expectations block (probe, time, value, tolerance) — that must be authored fresh for every seed.
- Assets are PNGs (schematics, equation renders, Minecraft tool icons). Equation/schematic images would need regeneration for the tablet; tool icons are EA-specific and not reusable.
- Values are tuned for EA's 20 Hz simulator; manatee lessons should re-derive tick counts from manatee's own rates (curriculum.md line 26–28 already anticipates this).

## Cross-reference with docs/curriculum.md (17-lesson arc)

Direct seeds (EA example → planned lesson):

| Planned lesson | EA seed | Fit |
| --- | --- | --- |
| 1. Series voltage drops | `series-voltage-drops/` | Near-verbatim: curriculum line 33 mirrors the EA teaching point exactly |
| 2. Parallel branch currents | `parallel-branch-currents/` | Near-verbatim (line 34) |
| 4. RC charging curve | `rc-charging/` | Near-verbatim (line 36) |
| 5. RLC ring-down | `rlc/` | Near-verbatim (line 37); curriculum's "energy sloshing" phrasing matches EA's |
| Appendix IV: metric prefixes | `metric-prefixes.md` | Explicitly cited as "EA precedent" (curriculum line 62–64) |
| Appendix IV: corpus index/caveats | `examples/README.md` | Format precedent for the index page (curriculum lines 12–15 name EA as the format reference) |

Partial seeds:
- Lesson 7 (switches, fuses): the `s` switch element and its usage appear in `rc-charging` and `rlc`, but no dedicated switch/fuse example exists.
- Lesson 11 (oscilloscope/AC waveform): EA's `O` probe + Industrial Data Logger convention (README of `rlc/`, lines 32–33 on symmetric voltage ranges) informs scope-display design, but there is no AC circuit example.

Gaps — no EA seed exists; must be authored from scratch (13 of 17 lessons):
- 3 (short circuits / first hazard), 6 (battery internal resistance) — DC-tier, feasible with existing element vocabulary (`v r w g O`), so cheap to author in the same format.
- 8–10 (relay, relay logic, elevator capstone) — need a controlled-switch/relay element beyond the seed vocabulary.
- 11–17 (entire AC tier: alternation, frequency-from-rotation, transformer, line loss, impedance, power factor, synchronization) — nothing in EA's corpus touches AC; needs AC source elements, transformer element, and probably scope-trace expectations in the front-matter format.

Summary: EA supplies exactly the DC-foundations backbone (4 of the 6 tier-I lessons plus both appendix precedents), tuned and battle-tested in the same "visible at game tick rate" regime manatee plans to use. The whole switching/control tier and the whole AC tier — the majority of the arc and its distinguishing content — have no seed and are original authoring work.

