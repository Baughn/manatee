# Lesson front-matter schema

Each lesson directory contains `circuit.txt` (Falstad-format netlist,
importer subset per the format spec) and `lesson.md`. The Markdown file
opens with a YAML front-matter block carrying the machine-readable
expectations that CI checks against both manatee-core and ngspice
(design.md R20: a lesson that stops being true fails the build).

## Fields

```yaml
---
lesson: 1                      # int, position in the curriculum arc
slug: series-voltage-drops     # matches the directory name (nn-<slug>)
title: Series Voltage Drops    # human title, used in the tablet index
circuit: circuit.txt           # netlist path, relative to the lesson dir
analysis: dc                   # "dc" (operating point) | "transient"
expectations:
  - name: source node          # human label, used verbatim in CI failure messages
    probe: [0, 128]            # [x1, y1] of exactly one O element in circuit.txt
    value: 12.0                # expected node voltage, volts
    tol: 1.0e-3                # absolute tolerance, volts
---
```

For `analysis: transient` (first needed by lesson 04, RC charging),
each expectation additionally carries `time:` (seconds, the instant at
which the probe is sampled) and the file gains a top-level `stop:`
(seconds, transient duration); the timestep comes from the netlist's
`$` header (`maxTimeStep`), keeping the netlist the single source of
electrical truth. DC lessons omit both.

## Design decisions

- **Probes are referenced by coordinate, not file order.** `probe: [x, y]`
  must match the first post `(x1, y1)` of exactly one `O` element in the
  netlist — zero or multiple matches is a corpus lint error. Coordinates
  are already the probe's identity inside the netlist, survive line
  reordering and element insertion, and let a reader find the probe by
  looking at the schematic. A required `name` keeps CI output readable.
- **Voltage-only expectations.** `O` probes are node-voltage markers, so
  every machine check reduces to one measurement kind against one solver
  output. Currents are taught (and checked) via the sense-resistor
  pattern — a known small resistor turns a branch current into a node
  voltage (see lesson 02) — which mirrors how real shunt ammeters work
  and keeps the CI harness trivial. Narrative current claims must always
  be backed by a sense-voltage expectation or be pure arithmetic on
  checked values.
- **Tolerances are absolute, in volts.** Ideal-element DC circuits solve
  to machine precision in both manatee-core and ngspice; `1.0e-3` V
  exists only to absorb float noise and solver option differences.
  Transient lessons will need looser, per-expectation tolerances
  (integration method differences); that is why `tol` is per-expectation
  rather than global.
- **Plain SI units everywhere** (volts, seconds), matching the netlist
  format's units convention. No suffixed values (`5mV`) in front-matter;
  prefixes are a display concern (curriculum appendix "metric prefixes").
- **`analysis` is explicit** rather than inferred from the presence of
  `time:` fields, so a typo (a transient lesson with a forgotten `time`)
  fails loudly instead of silently becoming a DC check.

## Divergence from curriculum.md naming

curriculum.md currently names the files `lesson.txt` + `README.md` (EA
style); this corpus uses `circuit.txt` + `lesson.md` per the corpus
contract. One of the two documents should be amended before the corpus
lands in-repo.
