# manatee-core: Reduction Layer ("compaction")

Last updated: 2026-07-05
Status: DRAFT.

Implements design.md R10–R11. Sits between clients and the netlist: clients
describe conductor geometry at natural granularity (voxels, cable segments,
schematic wires); this layer delivers a minimal netlist plus the maps needed
to route observations and events back to geometry. Sparky's
`TopologyBuilder` is the proven design reference for the VS side.

## Responsibilities

1. **Region building** — group equipotential conductor geometry into single
   nodes (union-find). Perfect conductors union; resistive materials form
   their own regions with inter-region resistances from material resistivity
   and contact cross-section (which is what makes a lead segment in a copper
   run *be* a fuse, with zero special-casing).
2. **Series-chain collapse** — runs of resistor edges with no taps reduce to
   single equivalent resistors. Shared by VS (voxel runs) and Stationeers
   (cable segments): ~10k segments → low-hundreds of nodes.
3. **Probe interpolation** — eliminated interior nodes remain observable:
   voltages interpolate by resistance ratio along the collapsed chain, so
   instruments can read "inside" a compacted run.
4. **Limit attribution** — each collapsed element carries a **limit
   envelope**: the per-limit-type minimum over its constituents.
   Instantaneous ampacity, i²t thermal mass, and melting threshold can
   each pick a *different* segment in a mixed-material chain — which is
   exactly the lead-fuse-in-a-copper-run case. Thresholds are
   environment-adjusted per segment (ambient temperature; Stationeers
   spans −150 °C on Europa to +800 °C on Vulcan): an environment change
   dirties the envelope, a metadata-only recompute — never a matrix
   change. When the solver raises a limit event on the equivalent
   resistor, this layer answers *which voxel/segment* melts, burns, or
   pops; current density at the narrowest cross-section is the
   attribution rule, evaluated per limit type.
5. **Island bookkeeping** — connectivity union-find feeding solver.md's
   islands: incremental merge on additions; removal invalidates and rebuilds
   the affected island (no incremental split detection — design.md R11).

## Incremental Maintenance

Carried over from sparky (design, not code):

- Dirty-region tracking at the client's natural chunk size (VS: 16³ blocks;
  Stationeers: per-CableNetwork).
- Merge pre-check: would the new geometry bridge >1 existing region? If so,
  full rebuild of the affected island; otherwise the incremental path.
- Pure-addition fast path: growing an existing region without removals
  touches only the dirty area.
- Any removal ⇒ island rebuild, **coalesced**: removals mark the island
  dirty and the rebuild runs at most once per tick, at solve time — a
  deconstruction burst costs one rebuild, not one per removed segment.

**Resync backstop:** the from-scratch build is always available and cheap at
island scale. A validation mode rebuilds in the background and diffs against
incrementally-maintained state, logging discrepancies — shipped as a debug
config in-game and run continuously in CI (testing-strategy.md, incremental
equivalence). Incremental maintenance against a live game mutation stream
WILL have edge-case bugs; this converts them from mystery corruption into
bug reports.

## Client Intake Contracts

- **VS voxel world**: sparse voxel storage → greedy-meshed prisms → regions
  (sparky's pipeline). Three block representations feed one intake
  (vintage-story.md R17): microblock-hosted cable voxels, dedicated cable
  block entities, decor-layer surface wiring.
- **Stationeers**: `NetworkExport` adjacency (cable segments + ports +
  fuses) per stationeers.md.
- **Tablet/2D harness**: schematic wires are already near-minimal; the layer
  passes them through (wire = ideal node per Falstad convention, with
  optional per-wire resistance for advanced lessons).

## Invariants

Reduction must be *semantically invisible*: raw and reduced graphs produce
identical terminal behavior, and probes agree with the raw interior. Both
are standing equivalence tests (testing-strategy.md).
