# Falstad circuit format — importer spec draft

Status: research complete, 2026-07-05. Sources are the circuitjs1 source itself (the dump/undump code is the only spec that exists — there is no written format document), the canonical example corpus shipped with it, and Electrical Age's importer + lesson corpus in `third_party/`.

**Primary sources (all verified by reading code, not memory):**

- `github.com/pfalstad/circuitjs1`, master @ `2a050e5fa4b7893b7c5194ebc48905a7c67228f0` (merge tag 4.1.2, 2026-05-22). Cloned to `/tmp/circuitjs1` for this research; cite paths below are relative to `src/com/lushprojects/circuitjs1/client/`.
- Pre-XML revision `ba02ba90dd0ceed8ceaffffb5985513b5c42d8e5` (2025-01-06) — the last generation whose *writer* emitted the classic text format (see "Versioning" below).
- Canonical example corpus: `src/com/lushprojects/circuitjs1/public/circuits/*.txt` (hundreds of files, the ones served at falstad.com).
- EA importer: `/home/svein/dev/manatee/third_party/ElectricalAge/src/main/kotlin/mods/eln/falstad/FalstadNetlist.kt`, `FalstadImporter.kt`; EA lesson corpus: `/home/svein/dev/manatee/third_party/ElectricalAge/docs/examples/{rc-charging,rlc,series-voltage-drops,parallel-branch-currents}/*.txt`.
- Web: https://github.com/pfalstad/circuitjs1 (GPLv2); deployed app https://www.falstad.com/circuit/circuitjs.html (falstad.com blocks non-browser fetches with 403; all format facts below come from the source tree instead).

---

## 1. File model and tokenization

The format is line-oriented plain text. The authoritative reader is `CircuitLoader.readCircuit(byte[], int)` (`CircuitLoader.java:123-215`):

- Lines are split on `\n`/`\r`. Each line is tokenized by Java `StringTokenizer` with delimiters `" +\t\n\r\f"` (`CircuitLoader.java:142`). **`+` is a token delimiter** — a legacy of circuits being pasted out of URLs where space encodes as `+`. Consequence: a number written `1e+5` splits into two tokens and breaks parsing; circuitjs never emits `+` (Java `Double.toString` doesn't), and neither may we.
- Dispatch is on the **first character of the first token** (`CircuitLoader.java:145`): single-letter types are chars; a leading digit means the whole token is parsed as a decimal integer dump type (`CircuitLoader.java:172-173`), e.g. `172`, `403`.
- Unrecognized dump types log `unrecognized dump type` to the console and the line is skipped (`CircuitLoader.java:200-207`); any exception during a line likewise skips just that line. So upstream's own error posture is *silently lossy* — a lesson importer must not copy that.
- Non-element line types handled before element dispatch (`CircuitLoader.java:149-193`): `o` (scope config), `h` (hint: `h type item1 item2`), `$` (options header), `!` (custom logic model), `%` `?` `B` (ignored afilter leftovers), `34` (diode model definition), `32` (transistor model), `38` (adjustable slider), `.` (subcircuit model).
- If the whole file starts with `<`, it is parsed as the new XML dialect instead (`CircuitLoader.java:73-80`) — see Versioning.
- There is **no comment syntax** upstream. `#` lines are an Electrical Age extension (see §6); circuitjs would log an exception for them and continue.

URL forms (for completeness, not for the importer): falstad.com links carry the same text as `?cct=` (raw, `%24` = `$`) or `?ctz=` (LZW-compressed base64) — `CirSim.java:171-178`, `ExportAsUrlDialog.java:101`.

## 2. Header line

Written by `CirSim.dumpOptions()` (`CirSim.java:422-436`), read by `CircuitLoader.readOptions` (`CircuitLoader.java:248-267`):

```
$ flags maxTimeStep simSpeed currentSpeed voltageRange [powerBarValue [minTimeStep]]
```

Example from the canonical corpus (`public/circuits/lrc.txt:1`): `$ 1 0.000005 10.20027730826997 50 5 43 5e-11`.

- `flags` bitfield: 1 = show current dots, 2 = small grid (8 px vs 16 px, `UIManager.java:956`), 4 = do **not** show voltage color, 8 = show power, 16 = do **not** show values, 32 = afilter linear scale, 64 = adjustable timestep enabled, 128 = auto-DC on reset. Bits 4 and 16 are inverted-sense — a plain `$ 1 ...` means "dots + volts + values shown".
- `maxTimeStep`: transient timestep in **seconds** (the only header field with electrical meaning).
- `simSpeed`: a double mapped back to a UI slider via `log(10*sp)*24+61.5` (`CircuitLoader.java:258-260`) — display pacing only.
- `currentSpeed` (int), `voltageRange` (volts, display color scale), `powerBarValue`, `minTimeStep` — trailing two are optional, absorbed by try/catch (`CircuitLoader.java:262-265`).

## 3. Element line general form

Every element line is (`CircuitElm.dump()`, `CircuitElm.java:236-240`; reader `CircuitLoader.java:195-200`):

```
TYPE x1 y1 x2 y2 flags [params...]
```

- Coordinates are integer canvas pixels, by convention multiples of the 8/16 px grid (canonical corpus uses multiples of 16). `(x1,y1)`/`(x2,y2)` are the two posts for 2-terminal parts; for single-post parts (`g`, `R`, `O`, `172`) the second point is only the drawing direction of the stub; for `T` and `x` they span a bounding box.
- `flags` is a per-element-type bitfield; several types gate *the presence of later params* on flag bits (see per-element notes) — a parser cannot skip flags.
- Element constructors read params positionally with `StringTokenizer`; missing trailing params fall back to defaults (usually via try/catch or `hasMoreTokens`), extra trailing tokens are ignored. That is the entire versioning mechanism.

## 4. Element subset — authoritative field lists

Dump types confirmed from `getDumpType()` in each class. "Classic dump" bodies cite revision `ba02ba90` (last text-format writer); readers cite master `2a050e5`.

| Type | Element | Params after `flags` (in order) |
|---|---|---|
| `w` | wire | none (`WireElm.java`) |
| `g` | ground (1 post) | `[symbolType]` (`GroundElm.java:20-27`; older files omit it) |
| `r` | resistor | `resistance` in Ω (`ResistorElm.java:33,37-39`) |
| `c` | capacitor | `capacitance` F, `voltdiff` V (state), `[initialVoltage]`, `[seriesResistance if flags&4]` (`CapacitorElm.java:41-51,69-72`) |
| `l` | inductor | `inductance` H, `current` A (state), `[initialCurrent]`, `[saturationCurrent]` (`InductorElm.java:38-47,50-52`) |
| `v` | 2-terminal voltage source | `waveform freq maxVoltage bias phaseShift [dutyCycle]` (`VoltageElm.java:69-74`; classic dump `ba02ba90` VoltageElm.java:80-90) |
| `R` | rail (1-post voltage source) | same as `v` (`RailElm.java:22,38` — extends VoltageElm) |
| `172` | adjustable rail w/ slider | `v` params + slider label text (`VarRailElm.java:34-45,61`) |
| `i` | current source | `currentValue` A, `[maxVoltage]` compliance — the latter added post-2025 (`CurrentElm.java:37-41`; classic dump had only `currentValue`, `ba02ba90` CurrentElm.java:38-40) |
| `s` | SPST switch | `position momentary [label if flags&4]` (`SwitchElm.java:52-66`; classic dump `ba02ba90` SwitchElm.java:62-66). **position 0 = closed, 1 = open** (`SwitchElm.java:28`); legacy files may have `true`/`false` in the position slot (`SwitchElm.java:54-59`). `momentary` is `true`/`false` |
| `S` | SPDT switch | SPST params + `link [throwCount]` (`Switch2Elm.java:46-49`; classic dump `link throwCount`, `ba02ba90` Switch2Elm.java:50-52). Posts: `(x1,y1)` common, `(x2,y2)` throw 0; throw 1 mirrored across the axis. `link != 0` gangs switches; `flags&1` = center-off; `throwCount>2` = multi-throw |
| `d` | diode | if `flags&2` (FLAG_MODEL): escaped `modelName`; else if `flags&1` (FLAG_FWDROP): `fwdrop` volts; else nothing (default drop 0.805904783 V) (`DiodeElm.java:48-68`, dump `DiodeElm.java:70-73`) |
| `z` | zener | like `d`, legacy non-model form carries `zvoltage` (default 5.6) (`ZenerElm.java:36-43,88`) |
| `34` | diode model definition | `34 escapedName flags Is Rs N BV forwardCurrent` (`DiodeModel.java:336-339`); referenced by name from `d`/`z` FLAG_MODEL lines |
| `T` | transformer | `inductance ratio current0 current1 [couplingCoef [saturationCurrent]]` (`TransformerElm.java:53-62`; classic dump `ba02ba90` TransformerElm.java:79-82). `inductance` = primary L in henries; **file `ratio` = N2/N1** (UI shows N1/N2 = 1/ratio, `TransformerElm.java:331,355`; secondary L = `inductance*ratio²`, `TransformerElm.java:242`); `current0/1` are winding-current state; `couplingCoef` defaults 0.999. flags: 4 = reverse polarity (dot flip), 8 = vertical, 16 = flip. 4 posts at the bbox corners |
| `O` | output/probe marker (1 post) | `[scale]` (`OutputElm.java:21`, classic dump `scale`) — voltage readout at a node |
| `p` | probe / voltmeter (2 posts) | `[meter [scale [resistance]]]` (`ProbeElm.java:43-50`; `resistance` is a 2026 addition) — meter 0 = voltage |
| `o` | scope config (non-element) | `elmIndex speed ... ` — heavily versioned, display-only; read by `Scope.undump` via a serializer (`Scope.java:1226`). Example: `o 4 64 0 4099 20 0.05 0 2 4 3` (`public/circuits/lrc.txt`) |
| `x` | text annotation | `size text` — if `flags&4` (FLAG_ESCAPE) the text is a single escaped token; else old style: all remaining tokens joined with spaces, `%2b`→`+` (`TextElm.java:37,45-58`, dump `TextElm.java:81-85`) |
| `38` | adjustable slider (non-element) | `elmIndex [Fflags] editItem min max [shared] sliderLabel...` (`Adjustable.java:47-65`) — UI only |

**Voltage source semantics** (`VoltageElm.java:152-170`): waveforms `WF_DC=0, WF_AC=1, WF_SQUARE=2, WF_TRIANGLE=3, WF_SAWTOOTH=4, WF_PULSE=5, WF_NOISE=6, WF_VAR=7` (`VoltageElm.java:39-46`). DC output = `maxVoltage + bias` (`:158` — so a DC 5 V source is normally `... 0 40 5 0 0 0.5`: waveform 0, the *frequency field still present*, maxV 5). AC output = `maxVoltage*sin(2πft + phaseShift) + bias` (`:159`) — `maxVoltage` is the **peak amplitude, not RMS**; `phaseShift` is stored in **radians**; `frequency` in Hz. Polarity: post 2 `(x2,y2)` is the + terminal — the source constrains `V(post2) − V(post1) = value` (`VoltageElm.java:139-147,447`). Legacy `flags&2` (FLAG_COS) is rewritten to phaseShift π/2 on load (`VoltageElm.java:60-64`).

**Units convention:** all values are plain SI base units — ohms, farads, henries, volts, amps, hertz, seconds — as Java doubles (decimal point, `E`/`e` exponents, never `+` in exponents). No suffixes (`10k`, `5u`) anywhere in the file format; those exist only in the UI.

**Text escaping** (`CustomLogicModel.escape/unescape`, `CustomLogicModel.java:257-262`): `\\`→`\\\\`, newline→`\n`, space→`\s`, `+`→`\p`, `=`→`\q`, `#`→`\h`, `&`→`\a`, CR→`\r`; empty string is `\0`. Used by `x` (with FLAG_ESCAPE), diode model names, switch labels, `172` slider labels.

## 5. Versioning quirks (there is no version field)

1. **Compatibility is purely positional-with-defaults**: readers wrap trailing params in try/catch or `hasMoreTokens`; new params are only ever *appended*, sometimes gated on a new flag bit (capacitor `seriesResistance` on `flags&4`, switch label on `flags&4`, diode model name on `flags&2`, pulse duty on `flags&4`). Old files load in new versions; new files load in old versions with the tail ignored.
2. **The 4.x XML pivot (2025-06 onward, tagged 4.1.2 in 2026)**: `dumpCircuit` now emits an XML `<cir>` document (`CirSim.java:438-450`, `XMLSerializer.java:107+`); the text writer (`String dump()`) has been *deleted* from many elements (SwitchElm, VoltageElm, TransformerElm, OutputElm no longer have one at `2a050e5`) while the text *readers* all remain. XML attribute names: `r` (resistance), `c`/`iv` (cap), `l`/`ic` (inductor), `maxv`/`freq`/`wf`/`b`/`ph` (sources), `p`/`mm` (switch), etc. So: files exported from current falstad.com are XML; the installed base, all historical files, `cct=`/`ctz=` URLs, and the entire bundled example corpus are classic text. EA already parses both dialects (`FalstadNetlist.kt:184-272`).
3. **2025-2026 param additions to text readers**: probe `resistance` (`ProbeElm.java:45`), current-source `maxVoltage` compliance (`CurrentElm.java:41`), ground `symbolType`, inductor `initialCurrent`/`saturationCurrent`, capacitor `initialVoltage`/`seriesResistance`. Files in the wild mostly predate these.
4. **`+` as tokenizer delimiter** (`CircuitLoader.java:142`) — see §1.
5. `x` text elements have two encodings distinguished by `flags&4` (§4).
6. Old dumps write `true`/`false` where newer ones write `0`/`1` for switch position (`SwitchElm.java:54-59`); canonical corpus contains both (`S ... 0 1 false 0 2` and `S ... 0 false false 1` both occur).
7. Upstream is GPLv2 — reading the format is fine, but do not vendor their example `.txt` files into MIT manatee-core without treating them as GPL test fixtures (keep out of the shipped corpus; fetch-at-test-time or hand-write our own).

## 6. Electrical Age dialect (the corpus style R20 adopts)

EA's four lessons all use the identical classic-text subset. Files: `third_party/ElectricalAge/docs/examples/rc-charging/rc-charging.txt`, `rlc/rlc.txt`, `series-voltage-drops/series-voltage-drops.txt`, `parallel-branch-currents/parallel-branch-currents.txt`.

- **Elements used**: `v` (DC only, waveform 0), `w`, `r`, `c`, `l`, `s`, `g`, `O` — plus a `$` header (uniformly `$ 1 5.0E-6 10 50 5`, 5 fields, no powerBar) and **`# comment` lines carrying the teaching narrative**. `#` comments are an EA extension, skipped at `FalstadNetlist.kt:113`; upstream has no comment syntax.
- EA parser behavior worth knowing (`FalstadNetlist.kt`): ignores `$` entirely (`:113`); parses `g` as a single point (`:119-127`); rejects diagonal components (`:556-558`); lowercases codes, which **conflates scope-config `o` lines with `O` probes** (`:541`, `:617`) — a latent bug they get away with because their corpus never contains `o` lines; treats `p` as a *push switch* (`:618-621`) whereas upstream `p` is a voltmeter — a plain divergence from circuitjs, don't copy it; substitutes SPDT `S` with two complementary switches (`:349-413`), zener `z` with a plain diode (`:609-612`), and `172` with a fixed DC source (`:167-181`); additionally supports Falstad logic elements `150-154`, `I`, `L`, `M` (not needed for us). Their field indexing confirms the circuitjs param order: `params[0]`=flags, `[1]`=waveform, `[2]`=frequency, `[3]`=maxVoltage, `[4]`=bias (`FalstadImporter.kt:281-295,363-364`).
- EA also parses the new XML `<cir>` dialect (`FalstadNetlist.kt:184-272`), mapping the same attribute names listed in §5.2.

## 7. Recommendation: Manatee importer subset and error posture

The importer is core infrastructure (design.md R16, R20; testing-strategy.md:96) and the corpus is CI truth — so unlike upstream, **nothing may be silently dropped**. Proposed contract:

**Accept (full electrical semantics):**

- `$` header — validate arity/numerics; consume `maxTimeStep` as the transient-dt hint; ignore the display fields.
- `#` comment lines (EA extension, adopted — lessons carry narrative in them) and blank lines.
- `w`, `g` (ignore `symbolType`), `r`, `c` (honor `voltdiff`/`initialVoltage` as initial condition; honor `seriesResistance` as a real series resistor), `l` (honor initial current; **reject** nonzero `saturationCurrent` — nonlinear inductor is out of scope), `v` and `R` with waveform **0 (DC) or 1 (sine) only** — reject waveforms 2-7 loudly; honor bias and phase, `i` (reject nonzero `maxVoltage` compliance), `s` (honor position; accept `momentary` but treat as a normal toggle, warn), `S` (reject `link != 0`, `throwCount > 2`, and center-off flag), `d`/`z` (accept no-flag default, FLAG_FWDROP value, model names `default`/`default-zener`, and any name defined by a preceding `34` line — map Is/Rs/N/BV onto our diode params; reject other named models), `34` lines, `T` (honor ratio = N2/N1, coupling, FLAG_REVERSE; reject nonzero saturation), `O` and `p` → probe markers for the tablet/goldens (ignore meter/scale/resistance display params), `x` annotations (both escapings).

**Ignore with a logged notice (presentation-only, no electrical meaning):** `o` scope lines, `h` hints, `38` sliders, `%`/`?`/`B`. These appear in virtually every real falstad export; hard-rejecting them would make the format useless in practice, but they must still be *counted and reported* so a lesson author sees what was dropped.

**Reject loudly, with line number, offending token, and a one-line reason:** every other dump type (transistors, opamps, logic, `172`, `!`, `.` subcircuits, `32`), XML `<cir>` input (message: "export as classic text"; EA proves it's parseable if we ever want it), diagonal 2-terminal elements if the harness grid needs rectilinear, malformed coordinates/params, NaN/Inf, non-positive R/C/L, and any trailing tokens we don't recognize on an accepted element (stricter than upstream, which ignores them — silent-extra-tokens is how dialect drift starts). Parse doubles with InvariantCulture; when *writing*, never emit `+` in exponents (§1).

**Corpus rule for our own lessons** (curriculum.md `lesson.txt`): header `$ 1 <dt> 10 50 5` EA-style, `#` narrative comments, coordinates in multiples of 16, DC value in `maxVoltage` (not `bias`), probes as `O`, and only the accept-list above — so every lesson file also loads unmodified in falstad.com/circuitjs for authoring and debugging (modulo `#` lines, which circuitjs skips with console noise only).

**Oracle note:** the two facts most worth pinning with an ngspice/circuitjs cross-check rather than trusting this document: voltage-source polarity (post 2 = +) and current-source direction (positive `currentValue` drives current from post 1 to post 2 through the source, i.e. arrow toward post 2 — inferred from `stampCurrentSource(nodes[0], nodes[1], currentValue)`, `CurrentElm.java:107-110`, but sign conventions deserve a golden test, per the working agreement that math is untrusted input).

**Free test material:** circuitjs1's `public/circuits/` (~hundreds of files) exercises every quirk in §5 — old/new switch booleans, 4-param vs 6-param `v` lines, flag-gated params. GPLv2, so use as fetched-not-vendored fixtures for parser fuzz/round-trip tests only.
