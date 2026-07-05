# Manatee

Real electrical simulation (MNA — Modified Nodal Analysis) for games:
a shared C# solver core (**manatee-core**) consumed by a Vintage Story mod
(**Manatee**: AC, voxel cables, an educational "gaming tablet") and by the
Stationeers **Re-Volt** mod (DC; that integration lives in Re-Volt's repo,
consuming manatee-core as a git submodule). Spiritual successor to
Minecraft's Electrical Age. Educational realism is the point: the game is
hard *because* real electricity is.

**Status:** design phase complete (2026-07); no code yet.
**Delivery order:** manatee-core + 2D schematic harness → Stationeers/Re-Volt
→ Vintage Story mod.

## Documents — read design.md first

| Doc | Contents |
| --- | --- |
| `docs/design.md` | Master doc: purpose, requirements R1–R20, non-goals, progression arcs, hazards, perf targets, delivery order. All major decisions live here. |
| `docs/solver.md` | manatee-core: netlist API, change-cost tiers, analyses (DC / transient / subcycled AC), islands, limits, numerics, failure handling. |
| `docs/compaction.md` | Reduction layer: series collapse, probe interpolation, limit attribution, incremental maintenance + resync backstop. |
| `docs/vintage-story.md` | VS client: microblock integration (verified engine facts with file:line), mechanical coupling, rooms/heat, tooltips, wire rendering. |
| `docs/stationeers.md` | Re-Volt integration: seams, legacy-device adaptor, islands via couplers, verified threading model of the game. |
| `docs/testing-strategy.md` | ngspice oracles, lesson corpus as CI goldens, invariants, equivalence tests. The math is treated as untrusted input. |
| `docs/curriculum.md` | Tablet lesson arc (17 lessons, DC → AC power) and authoring rules. |

`third_party/` holds reference checkouts and the Stationeers decompilation —
see `third_party/CLAUDE.md` for what's there and the decompile caveats.
`third_party/sparky/` is our earlier VS prototype: design reference
(layering, incremental voxel compaction), code not reused.

## Working agreements

- The user is an SRE/programmer, not an EE (ex-Electrical Age dev, strong
  circuit intuition). Explain circuit math in systems terms (state machines,
  invariants, representation changes), not EE jargon. Claude owns EE-specific
  content (stamps, companion models, convergence lore); its correctness is
  established by oracle tests against ngspice and conservation invariants —
  never by asking the user to check derivations.
- Design interviews: ask questions as numbered prose with stated leanings
  (so "yes" suffices), not interactive widgets — the user answers async.
- Big unknowns about game internals are settled by reading source:
  VS engine source and the Stationeers decompilation are in `third_party/`.
  Findings graduate into `docs/` with file:line references.
- Collaboration: Sukasa (Re-Volt's author) is the Stationeers-side partner;
  integration decisions affecting Re-Volt get relayed for his sign-off.
  Re-Volt is MIT → manatee-core is MIT (CSparse.NET stays a separate LGPL DLL).

## Practical notes

- VCS is **jj** (colocated git). `third_party/stationeers-decomp/` is
  gitignored proprietary output — keep it that way. Commit with `jj commit`
  at convenient points, e.g. after a logical unit of work.
- NixOS: missing tools via `nix run nixpkgs#<pkg> --`. ngspice (test oracle)
  and ilspycmd (decompiler) are used this way; neither ships with the
  project.
- `third_party/import.sh` clones/updates reference repos and regenerates the
  Stationeers decompilation when the game's DLL changes.
