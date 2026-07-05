# The 2D Schematic Harness (and Tablet Engine)

Last updated: 2026-07-05
Status: Architecture settled; implementation not started.

The 2D schematic client is three things at once (design.md, Delivery
Order): the dev/test bed for manatee-core, the engine inside the VS
tablet, and the lesson authoring tool. This doc pins the architecture
that lets one codebase be all three — and be testable without a display.

## Table of Contents

1. [Layering](#layering)
2. [Testing Model](#testing-model)
3. [Rendering Backends](#rendering-backends)
4. [The Desktop Shell](#the-desktop-shell)

---

## Layering

`Manatee.Schematic` (net8.0, referenced by both the desktop harness and
later the VS mod) contains three **pure** layers. None of them import a
UI framework; that rule is what makes the tablet testable.

1. **Document model.** The schematic itself: components, wires, junctions,
   probe placements; the Falstad importer (core infrastructure per
   testing-strategy.md — it feeds the lesson corpus); and the binding to
   manatee-core (netlist extraction, solution readback for meters and
   scope traces).

2. **Interaction state machine.** Consumes *abstract* input events —
   pointer down/move/up, wheel, key — and holds every stateful part of
   editing: active tool, drag-in-progress, selection, hover, snapping.
   Output is document mutations plus transient view state. It never
   touches a widget toolkit; the harness and the VS tablet both adapt
   their native input into these events.

3. **View layer.** Renders document + interaction state into a **display
   list**: a flat sequence of draw commands (stroke path, fill, glyph
   run, ...) in schematic coordinates. The display list is data — it can
   be snapshotted, diffed, and rasterized by any backend.

The lesson engine (narrative pages, predict-then-observe Q/A,
curriculum.md) sits beside these as document-plus-content orchestration,
equally UI-free.

## Testing Model

This is the "Playwright-like" answer, without a browser in sight:

- **Interaction tests** are scripts of synthetic events fed to layer 2:
  "pointer down at the resistor palette, drag to (140, 60), release,
  expect a resistor in the document and an undo entry." Deterministic,
  headless, CI-friendly. These carry the tablet's behavioral coverage.
- **Display-list snapshots** (Verify) are the *primary* visual golden:
  stable text, reviewable diffs, no font or GPU nondeterminism. Most
  rendering regressions are caught here.
- **Pixel snapshots** are a deliberately *small* secondary suite: the
  display list rasterized through headless SkiaSharp with a font bundled
  in test assets, compared against golden PNGs. This catches
  rasterizer-level regressions without the classic failure mode where
  every hinting change invalidates hundreds of goldens.
- **Lesson corpus integration**: scripted harness scenarios (build, edit,
  save/load, fault) run headless in CI per testing-strategy.md,
  exercising netlist extraction, solving, and probe readback end to end.

## Rendering Backends

The display list has one contract, multiple rasterizers:

- **SkiaSharp** — the desktop harness canvas and the headless pixel
  tests. (Added as a dependency when the view layer starts, not before.)
- **Vintage Story GUI** — in-game tablet rendering to a texture/dialog,
  written when the VS layer starts. It gets equivalence spot-checks
  against the Skia output plus manual protocols; the pure layers beneath
  are already covered.

## The Desktop Shell

`Manatee.Harness` is an Avalonia app hosting the Skia canvas plus the
authoring conveniences (lesson text pane, palette, probe readouts).
Avalonia is deliberately *outside* the tested tablet stack — it is
swappable, and nothing in `Manatee.Schematic` may reference it. Shell
end-to-end tests via Avalonia.Headless are possible but expected to stay
rare; the layers below carry the coverage.
