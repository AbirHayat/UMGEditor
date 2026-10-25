# App parity: everything the command line does, in the app

Status: plan approved 2026-10-25, decisions in §8. Nothing here is built yet.

## Context

Every device-tested route (C9–C18) is reachable from `umgmod`, `umgcompare` or a recipe, but the WPF app only covers
slot layout edits (typed, dragged, nudged, inserted Offsets tags), texture replacements and the export dialog. The
advanced routes (animation keys, name-map appends, enum and name tag inserts, tag removal, class swaps, colour rows,
font faces, loose paks, clones and re-points, recipes, DISABLE.ps1, PC compare) are CLI or recipe only. The goal is
that a user can make every one of them in the app, behind an **Advanced** label, with exactly the same Core checks,
and that the app becomes usable by other people (setup, settings, packaging, docs).

Acceptance throughout: a rebuilt canary, byte-identical to `recipes/pins/device-tested.json`. Because every stage
reproduces device-tested bytes, no stage needs a new device canary.

---

## 1. Inventory

Legend: **app** = doable in the app today; **partial** = the app shows it or does part of it; **CLI** = CLI or recipe
only; **dev** = a historical canary builder or developer check, proposed to stay CLI only (decision D7).

### 1.1 `umgmod` (`app/tools/UmgEditor.ModCli/Program.cs`)

| Command | What it does | Today | Notes |
|---|---|---|---|
| `edit … set/move/resize` | slot field, move, resize edge (incl. inserted Offsets tag) | **app** | inspector fields, drag, nudge (`EditorController.SetValue/CommitDrag/Nudge`) |
| `show` | plan of a package: writes, insertions, anim writes, windows, refusals | **app** | Bytes tab (`BytesToPatch`) |
| `reset` | every edit of a package back to stock, saved | **partial** | app has Discard unsaved and undo, no "reset package to stock" |
| `build` | export with the dialog's checks, Danger needs `--ack-danger` | **app** | Export dialog; but app writes no `DISABLE.ps1` / `recipe.json` |
| `predict` | Impact for saved edits + layout JSON for `validate_layout.py` | **partial** | Impact tab; JSON only inside an export's `predictions\` |
| `show-anim [--anim --widget --bytes]` | every animation of a package, tracks, keys with byte offsets | **partial** | inspector Animations section, per widget, read-only |
| `anim-flatten` | keys set to the last value of `--normal-from`, checked against `--check` (C11) | **CLI** | |
| `anim-key … value/frame/interp` | one key, both copies (C11/C12) | **CLI** | logic for finding the track lives in Program.cs (must move to Core) |
| `anim-end` | range bounds at the old end → new end (C9) | **CLI** | |
| `tex-info` | texture of a uiImage, format, theme copies, margins, users | **app** | Texture section |
| `tex-users` | every widget naming a texture | **app** | Used by list |
| `tex-sources` | clonable B8G8R8A8 sources, best first | **app** | `TrialSourcePicker` |
| `tex-preview` | stock vs replaced PNG, `--in SCREEN` | **partial** | app draws the trial live; no PNG out, no other-screen choice |
| `tex` / `tex-white` | texture edit from a PNG / white copy, scope | **app** | Import image…, White copy, scope radios |
| `tex-show` | the project's texture plan | **app** | Bytes tab `TextureBytesList` |
| `tex-reset` | remove a texture edit | **app** | Remove replacement |
| `tex-coverage [--folder --list]` | white-copy plan for every uiImage, refusal reasons | **CLI** | loop lives in Program.cs (must move to Core) |
| `stock-chunk` | raw stock chunk to a file | **CLI** | |
| `recipe-check` | recipe checked and built in memory, warnings, pins | **CLI** | printing lives in Program.cs |
| `recipe-build` | check, pack, verify, pins, DISABLE.ps1 | **CLI** | |
| `recipe-from-set` | project → recipe JSON | **CLI** | |
| `disable-script` | DISABLE.ps1 + checklist for a built mod | **CLI** | |

### 1.2 `umgcompare` (`app/tools/UmgEditor.Compare`)

| Command | What it does | Today |
|---|---|---|
| `check-pc` | opens PC containers, basic checks | **CLI** |
| `compare [--focus]` | PC vs mobile widget packages → REPORT.md + compare.json | **CLI** |
| `anim-keys [--pc]` | keys of every channel checked against the compiled copy | **partial** (app: mobile, per widget) |
| `anim-dump` / `export-dump` | exports as JSON | **CLI** |
| `export-tex [--pc / --mod]` | textures decoded to PNG | **partial** (app: thumbnails only) |
| `anim-report` | mobile vs PC vs UCL animation report | **CLI** |
| `mod-diff --mod` | what a PC mod folder changes vs PC stock | **CLI** |
| `font-usage` | packages whose name map holds a font name; Font assets using a face | **CLI** |
| `recipe-input C14 / C17` | third-party input (UCL image / font) from a PC mod into `_inputs`, sha256 | **CLI** |
| `canary-goal-slide` (C9), `canary-insert` (C10), `canary-texture` (C13/C14), `canary-scoreboard` (C15), `canary-ucl` (C16/C16b/C17b), `canary-font` (C17a), `recipe-ucl` | historical canary builders and their recipe writers | **dev** (superseded by recipes) |

### 1.3 Recipe sections and layer operations (`Core/Recipes/Recipe.cs`)

| Part | Tool behind it | Today |
|---|---|---|
| `Packages[].Values` (stored, in place) | `PatchSession` / `BytePlanner` | **app** |
| `Values` with `Inserted` (Offsets members) | `TagInserter` | **app** |
| `Values` with `anim:` keys, `@frame`, `@interp`, `range@` | `BytePlanner.PlanAnimKey`, `AnimRanges` | **partial**: the app saves, shows (Bytes tab) and builds them if the edit set has them; can't make them |
| `Textures` | `TextureEdits.Plan` (rename / point) | **app** |
| `Clones` (Solid, PNG input, White, raw .bgra) | `TextureClone.Build/BuildResized`, `TextureEdits.CloneImage/CloneWhite` | **partial**: only implied by a texture edit |
| `Repoints` (+ `Values`, e.g. C14 margins) | `SoftPathRepoint.Build` | **partial**: only implied by a texture edit (MarginsZero) |
| `LoosePaks` | `LegacyPak`, `Modding/LoosePak` | **CLI** |
| `Inputs`, `Expect.Chunks/Files` | `RecipeCheck` / `RecipeBuild` | **CLI** |
| Layer `Rename` (+ `EnumSwaps`) | `NameRenamePatch`, `EnumValueSwap` | **CLI** |
| Layer `RemoveTag` | `TagRemover` | **CLI** |
| Layer `ClassSwap` | `ClassSwap` (+ `ImportedPackageSwaps`) | **CLI** |
| Layer `ColorRows` | `DataTableColorEdit` | **CLI** |
| Layer `AppendNames` | `ZenNameMap.Append` / `EnumTagInsert.AppendNames` | **CLI** |
| Layer `EnumInsert` | `EnumTagInsert.Insert` (anchored on a stock tag) | **CLI** |
| Layer `NameTag` (insert / swap) | `NameTagEdit` | **CLI** |
| Layer `FontFace` | `FontFaceRepoint` | **CLI** |
| `LayerUnion` across layers | `Patching/LayerUnion.Check` | **CLI** (app never stacks layers) |

### 1.4 App-only today

Preview with UMG layout, drag/nudge/zoom presets (Fit…400 %), Impact tab with clip warnings and hidden-on-purpose
boxes, typed-text state, first-edit gate, project undo/redo, export dialog with the Danger tick, texture trial
preview. These stay and are reused.

---

## 2. Architecture rule

**One code path.** The app (`MainViewModel` → `EditorController`) and the CLIs call the same Core entry points. A
CLI `Program.cs` and an App ViewModel may only parse arguments / bind controls and format results. No check, lookup or
loop lives in either. Consequences:

1. **Extract before exposing.** Logic that sits in a CLI today moves to Core in the stage that exposes it:
   - `ResolvePackage` / `ResolveTexture` → `Core/PackageResolver`.
   - `anim-key`'s track/channel/key lookup → `AnimKeyIndex.Find(anim, widget, channel, key, field)`.
   - `tex-coverage`'s loop → `Textures/TextureCoverage.Run`.
   - `predict`'s JSON + report → `Editing/PredictionWriter` (shared with `ExportRunner`'s `predictions\`).
   - `recipe-check` printing → `RecipeCheckResult.ToText()` (the app shows the same text).
   - `umgcompare` internals (`CheckPc`, `Compare`, `AnimReport`, `ModDiff`, `FontUsage`, `AnimKeys`, `ExportDump`,
     `ExportTex`, `RecipeInput`) → `Core/Compare/*` and `Core/Inspect/*`; the PC and UCL paths become arguments.
2. **A project is a recipe in progress.** Besides the edit set (values, inserted tags, anim keys) and `textures.json`,
   a project gets `layers.json`: the recipe records it already has (`RecipePackage.Layers`, `Clones`, `Repoints`,
   `LoosePaks`, `Inputs`), read and written by the strict `RecipeReader` (no second format). Inputs live in the
   project's `_inputs\<sha256>` like `_art`. Undo gets a `LayersStep` next to `TexturesStep`.
3. **One build path.** `RecipeCheck.ApplyLayer` becomes public as `Patching/LayerStack.Apply(source, fileKey, chunk,
   layers)` (each tool's own checks, then `LayerUnion.Check`). The app's export becomes: request checks
   (`PlanExport`, unchanged) → `RecipeFromSet.Make` of the saved project → `RecipeCheck.Run` → `RecipeBuild.Plan` →
   `ExportRunner.Run`. So every app export *is* a recipe build, and the mod folder always holds the `recipe.json` it
   was built from plus `DISABLE.ps1`. Stage 1 adds this as a cross-check first (the recipe must reproduce the chunks
   the export built, else refused); stage 4a adds the recipe path for building, the old path stays beside it (C6 and
   C18 pins guard both), and it is removed once stage 4b passes (D4).
4. **Checks apply identically because they live below the entry points**: stock-value check, ±32 / smallest unique
   window (≤ ±512), exact byte diff and cut-out, full re-parse against stock (`ReparseDiff`), `LayerUnion`, retoc
   verify, CUE4Parse read-back and mount check (`TextureMountCheck`), `Expect` pins, `WriteGuard`. Danger: the
   export tick (`ExportRequest.Acknowledged`) and `--ack-danger` both feed `RecipeBuild.Plan`. No UI setting reaches
   them.
5. **Parity tests.** For each exposed tool an MSTest `Parity_<Tool>` drives the `EditorController` method and the
   CLI's Core call with the same input and requires the same bytes / report text.

### Exposure table

| Tool (Core) | Controller entry (new unless noted) | App place | CLI |
|---|---|---|---|
| Slot edits, inserts | `SetValue`, `CommitDrag`, `Nudge` (exist) + `ApplyGroup` | inspector, preview | `edit` |
| Recipe out / check / build / viewer | `ExportAsRecipe`, `CheckRecipe`, `OpenRecipe`, `ProjectFromRecipe` | Recipe menu, viewer window, export dialog | `recipe-*` |
| DISABLE.ps1 | via `ExportRunner` | export dialog output | `disable-script` |
| Clones, re-points | `AddClone`, `AddRepoint` | Textures panel (Advanced) | recipe |
| ColorRows | `SetColorRow` | Colour table panel | recipe |
| EnumValueSwap (colour enum) | `SwapEnumValue` | Text section | recipe |
| NameTag, FontFace, LoosePaks | `SetFontName`, `RepointFontFace`, `AddLooseFile` | Text section, Fonts panel | recipe |
| ClassSwap | `SwapClass` | Class section | recipe |
| Rename, AppendNames, RemoveTag, EnumInsert | `Rename`, `AppendNames`, `RemoveTag`, `InsertEnumTag` | Name map panel, Tags section, Text section | recipe |
| Anim key / frame / interp / range / flatten | `SetAnimKey`, `ChangeAnimationEnd`, `FlattenAnimations` | Timeline tab | `anim-*` |
| Compare, dumps, coverage, stock chunk, font usage, inputs from a PC mod | `Core/Compare`, `Core/Inspect` | Tools window, Compare window | `umgcompare`, `umgmod` |

---

## 3. UI design

### Layout

- **Toolbar**: project picker, Undo/Redo/Save (exist), **Advanced** toggle (off by default, remembered per user),
  Recipe menu (Open recipe…, Make project from recipe…, Export as recipe…), Export… (exists), Tools ▸.
- **Middle tabs**: Tree, Preview, **Timeline** (new), Bytes, Impact, **Layers** (new: per package the layer stack in
  order, each layer's tool, status, bytes, `LayerUnion` result; remove / reorder where the order is free).
- **Inspector (right)** for the selected widget: Slot (exists), Texture (exists; + Clone and Re-point under
  Advanced), **Text** (font name `HeadlineBoldSub`…, Justification, colour enum), **Class** (swap to a class from
  another stock package, refusal reasons listed), **Tags** (stored top-level tags, "Back to class default"),
  Animations (exists; "Edit in Timeline").
- **Package panels** (from the package list's context menu or a Package header in the inspector): **Name map**
  (names, appended names, same-length renames), **Colour table** (only ColorTable / ColorTable0164F: row, swatch,
  stock → new, `#RRGGBBAA`), **Fonts** (pes CompositeFont faces; loose font files; the `font-usage` side-effect list).
- **Windows**: Recipe viewer, Compare (PC vs mobile), Tools (stock chunk, export dump, texture PNG export, coverage,
  mod diff, inputs from a PC mod). All read-only on the game.

### Building, checking and exporting a recipe

1. Work in a project. Bytes, Impact and Layers update live; every refusal shows where it happened (status line +
   the section) as today.
2. **Check** (export dialog or Recipe ▸ Check): `RecipeCheck.Run` in memory, nothing written; shows its log, layout
   warnings, hidden-on-purpose list, pins status ("as pinned" / "no pins" / "differs: chunk …").
3. **Build**: Danger warnings ticked one by one (unchanged rule), then pack, verify, mount check. Output
   `work\out\mods\<name>\`: `mod\`, `recipe.json`, `DISABLE.ps1`, `TEST_INSTRUCTIONS.md`, `predictions\`.
4. **Export as recipe…** without building writes `work\out\recipes\<name>.json` (`WriteGuard.ForRecipes`).
5. **Recipe viewer** (read-only): sections, inputs found/missing by sha256, stock hashes, Check, pins. **Make project
   from recipe** creates a project; it is refused unless `RecipeFromSet.Make` of the new project equals the opened
   recipe (minus `Expect`), so import can't silently drop anything. The app never writes pins (D6).

### Advanced label and first-use confirmation

- Core classifies each edit: `EditCategory` = InPlace | SizeChange(tool) | Animation | Layer(kind). The label is UI
  only; it never loosens or adds a Core check.
- **Advanced off**: advanced fields and sections are visible but greyed with "Advanced edit (turn on Advanced in the
  toolbar)". A project that already holds advanced edits opens normally; its rows carry an `Advanced · <tool>` badge
  and can be undone or removed.
- **Advanced on**: the first use of each category in a project opens a confirmation (`AdvancedGate.Text(category)`,
  shared with the CLI's `--yes` text): what the tool changes (bytes, header fields, packages that grow), its checks,
  the canary that proved it (e.g. "device-tested by C10"), the packages it reaches (Impact). Confirmed categories are
  recorded in the project's `project.json` with the date. The existing first-edit-of-a-package gate stays as is.
- Every advanced row in Bytes, Layers, the export dialog and `TEST_INSTRUCTIONS.md` carries the badge.

---

## 4. Stages

Every stage ends with: `dotnet build` clean, `dotnet test app/tests/UmgEditor.Tests` all pass, Smoke exit 0, the
stock-hash check (`GameFixture`) unchanged, its UI-drive script passing, the acceptance canary byte-identical to the
pins, a commit on the work branch (D1), a resume note in CLAUDE.md, then stop and report.

**Acceptance pattern "remove and redo"**: for a canary whose recipe also needs tools from later stages, the drive
script makes a project from the canary's recipe, removes every layer of this stage's kind, re-enters them through
the new UI, builds, and compares with the pins. Only this stage's UI produced those bytes.

| # | Stage | Acceptance canary |
|---|---|---|
| 1 | Recipe out: export writes `recipe.json` + `DISABLE.ps1`, Export as recipe, read-only recipe viewer with Check | C18 made by `driveC18.ps1` → files = pins; `recipe.json` = `recipes/C18.json` (minus Expect); `DISABLE.ps1` = `umgmod disable-script`'s; viewer checks C13–C18 recipes "as pinned" |
| 2 | Preview: selection, panning, zoom (5a–c) | C18 regression rebuild; drags at 10 %, 100 %, 1600 % give the typed values |
| 3 | Multi-select and group edits (5d–e) | equivalence tests; C18 regression rebuild; C12's in-place values re-entered by group moves where they share a delta |
| 4a | Project layers + one build path + Make project from recipe | recipes C10–C18 round-trip (recipe → project → recipe equal); C15, C16, C16b, C17a, C17b imported and built in the app = pins |
| 4b | Clones and re-points panel | C13 and C14 made from scratch in the app = pins |
| 5 | Colour table (ColorRows) + colour enum swap | C16: remove and redo ColorRows; C15: redo its colour EnumSwap |
| 6 | Fonts: NameTag, FontFace, loose files, font usage | C17a from scratch; C17b: remove and redo NameTag (21 texts, multi-select), FontFace, LoosePak |
| 7 | Class swaps | C16: remove and redo both ClassSwaps |
| 8 | Renames, name appends, tag removal, enum inserts | C15: redo Rename + RemoveTag; C16b: redo AppendNames + EnumInsert |
| 9a | Animation keys in a table: value / frame / interp, range end, flatten | C12 from scratch in the app = pins; C11 flatten = edit set C11's saved chunk; C9 range edit = `AnimFrameRangeEditTests` sha `e31086c0…` |
| 9b | Simple timeline (tracks, keys, drag a key, both copies shown) | timeline drag of C12's keys = typed values; C12 rebuilt |
| 10 | Tools + PC-vs-mobile compare + inputs from a PC mod | `compare` REPORT.md identical from app and CLI; inputs C14 `334a47de…` / C17 `be267157…` reproduced, then C14 and C17a built = pins (Inconclusive without the PC install) |
| 11a | Settings, key out of source, hardcoded-path audit | all canaries C13–C18 rebuild with a settings file in a fresh location |
| 11b | Setup wizard, refusal help texts | wizard run on an empty profile; C18 rebuilt |
| 11c | Packaging + docs | packaged build on a clean folder rebuilds C18 = pins |

### Releases (cut lines)

| Release | Stages | What a user gets |
|---|---|---|
| **Release 1** | 1, 2, 3, 11a, 11b, 11c | today's editor + recipe export, DISABLE.ps1, recipe viewer, better preview, multi-select, setup wizard, zip package |
| **Release 2** | 4a, 4b, 5, 6 | project layers and recipe import, clones and re-points, colour table, fonts |
| **Release 3** | 7, 8, 9a | class swaps, renames / name appends / tag removal / enum inserts, animation keys in a table |
| Optional | 9b, 10 | timeline drawing, PC-vs-mobile compare and tools |

Stage 11a–c run right after stage 3 (they ship Release 1); the stage numbers stay as they are.

Order notes: stage 1 is small and immediately useful; usability (2–3) comes early because every later stage edits
through the preview; 4a is the foundation for every layer UI and proves the build path before any layer UI exists;
anim keys come after layers because C15–C17b only need their keys imported, while C12 needs them edited.

### 5. Preview and editing usability (stages 2 and 3)

**a. Selection** (stage 2). Today `PreviewCanvas.SelectAt` picks the smallest visible box under the cursor, which
can be a read-only child of an embedded package. New `Core/Editing/PreviewPick.Pick(layout, point, deep)`:
- click = the outermost box on the hit chain owned by the opened package (the embedding widget);
- Alt+click = the deepest box (the read-only shared child), as today;
- the read-only message reads "<Widget> belongs to <Package>; it is shared by N packages (an edit there would change
  N packages, see Impact)" with a **Select the embedding widget** button (`SelectEmbeddingButton`). N comes from
  `UsageGraph.AllUsers` + 1.

**b. Panning** (stage 2). Middle-mouse drag and Space+drag pan; scrollbars stay; `Home` resets the view (fit).

**c. Zoom** (stage 2). Continuous 10 %–1600 % (×1.25 per wheel notch), zoom around the cursor (the viewport point
under the cursor stays put), zoom to selection (`F`), zoom to fit (`Ctrl+0`, = Fit), 100 % (`Ctrl+1`),
`Ctrl+=`/`Ctrl+-`; Fit and Ctrl+wheel keep working; the `ZoomPicker` keeps its presets and shows the current %. The
canvas stops growing with zoom (1786 × 16 px is too big to draw): it keeps the viewport size and draws through a
pan/zoom transform with its own scroll extent (`IScrollInfo`). `SlotEditor.PixelsToUnits` is unchanged; only the
screen → viewport factor changes.

**d. Multi-select** (stage 3), one package at a time.
- Ctrl+click toggles, Shift+click adds (preview); Ctrl/Shift in the tree (custom selection on the `TreeView`, which
  has no multi-select). Drag-a-box: a press that isn't on a handle and moves ≥ 3 px draws a band; boxes of the opened
  package fully inside are selected (outermost rule from a); Shift+band adds. A click stays a click.
- Core `Editing/GroupEdit`: `Normalize` drops any selected widget whose ancestor is selected (only the parent
  moves); a selection across packages is refused.
- Move: each element shifted by the same amount (D3: same design units for typed/nudge; a drag's screen delta goes
  through each element's parent transform, as a single drag does). Resize: the group box's edge drag applies the
  same edge change to each element. Proportional group scaling later.
- Any blocked element (library widget, embedded read-only, no CanvasPanel slot, refused insertion such as an unstored
  Anchors struct, ZOrder) refuses the whole operation and names it.
- `EditorController.ApplyGroup` = one undo step, one first-edit gate per package (one package), combined Bytes,
  warnings (`EditWarnings.ForPendingEdit` with the combined set) and Impact.

**e. Tests.** `GroupMove_EqualsTypingEachValue` (stored values, bytes, saved file, undo history; stretched and
non-stretched), `GroupResize_EqualsTypingEachEdge`, `ParentAndChild_MovesOnlyParent`, one test per blocked case
(library, embedded, no slot, unstored Anchors, ZOrder, two packages), `ZoomAndPan_ChangeNoStoredValue` (no session
dirty, edit set unchanged), `DragAtZoom_EqualsTypedValue` at 10 / 100 / 1600 %, `Pick_DefaultSelectsEmbeddingWidget`,
`Pick_AltSelectsChild`. UI-drive scripts `driveP2.ps1` (pick, Alt, the button, pan, zoom keys, zoom around the
cursor) and `driveP3.ps1` (Ctrl/Shift/band select, group move/nudge/resize, each refusal's message, one Ctrl+Z).

---

## 6. For other people to use it (stage 11)

- **Settings file**: `%APPDATA%\UmgEditor\umgeditor.json` (D9; it already holds the key) with PaksDir, AesKey, WorkDir, RetocPath, stub pak;
  `umgeditor.json` next to the exe stays as an override for tests and UI drives.
- **Key out of source**: done before stage 1 (the key lives only in `%APPDATA%\UmgEditor\umgeditor.json`; history
  is kept, D8). Stage 11a empties the remaining `E:\…` path defaults; a missing value starts the wizard.
- **Setup wizard** (first run or invalid settings): game Paks folder (platform detected from the file keys, opened
  with the key as a check), AES key typed by the user (format check, then a test decrypt of one stock entry), retoc
  path with sha256 checked against the tested list (`RetocInfo`; an unknown hash is refused for building, allowed for
  reading), work folder (outside the game folder, never under PaksDir), display-scale note.
- **Hardcoded-path audit**: GameConfig defaults; `umgcompare`'s `F:\SteamLibrary…` and UCL mod paths (become
  settings or arguments); `RecipeContext.RootOf` (assumes `recipes\` next to `work\`); test fixtures; ui-drive
  scripts; `work/scripts/*.py`. An MSTest `NoHardcodedPaths` fails on any drive-letter literal outside an allow list.
- **Error messages**: every refusal becomes a `Refusal(code, message, whatToDo)`; the app shows the help line;
  `docs/REFUSALS.md` lists each code. A test requires a non-empty help text for every code the tests hit.
- **Packaging**: `dotnet publish` self-contained win-x64 for the App + `umgmod`, zipped (D10), with the third-party
  licence notices below. retoc is not bundled; the user points to their own copy and its sha256 is checked. The
  canary builders and `recipe-ucl` (`umgcompare`) are excluded from the package (D7). No licence for the project's
  own code until the user decides (D10).
- **Third-party licences** (read from the clones' LICENSE/NOTICE files and the NuGet nuspec, 2026-10-25; not legal
  advice):

  | Component | How we use it | Licence | Copyright | Obligations if redistributed |
  |---|---|---|---|---|
  | CUE4Parse (`tools/CUE4Parse`) | project reference of Core, ships inside the package | Apache-2.0 (+ NOTICE) | FabianFG and contributors (LICENSE has no single line) | ship LICENSE and NOTICE; state changes to its files; no trademark use. Its NOTICE lists bundled libraries (mostly MIT; DotNetZip MS-PL, Serilog Apache-2.0, Blake3 BSD-2); their notices go with it. Oodle is not used (`CUE4PARSE_SKIP_NATIVE`, no downloads) |
  | AssetRipper.TextureDecoder 2.6.2 (NuGet) | texture decoding in Core, ships | MIT | (c) 2022–2026 ds5678 | include the copyright + MIT text |
  | retoc (`tools/retoc`) | external exe the user supplies (not bundled) | MIT | (c) 2025 Truman Kilen and Archengius | none while not bundled; if bundled later, include the MIT text |
  | UAssetAPI (`tools/UAssetAPI`) | reference/tests only, not used by the app, not shipped | MIT (+ NOTICE.md: repak and others) | (c) 2020–2026 atenfyr | none while not shipped |

  All four allow commercial use and selling, given the notices. What the licences can't settle: mods and tools for
  eFootball also fall under Konami's terms of use, and the game's AES key, game bytes, UCL art and fonts are not
  ours to distribute (see "Never bundled"). That part is the user's call (D10).
- **Docs**: `docs/USER_GUIDE.md` (setup, first edit, export, copying to the phone by hand, DISABLE.ps1), Advanced
  tools page (each tool, its checks, its canary), REFUSALS.md, FAQ (online packages untested, never go online).
- **Never bundled**: the AES key; any game bytes (stock chunks, `work\edits\**` stock copies and `.edited.uasset`,
  `work\cache` index/texture/usage caches, built mods `work\out\mods`, `ss\` footage, `tutorial.txt`); third-party art
  (UCL images, `custom Design\`, `work\out\art`, `work\out\recipes\_inputs`); fonts (Champions, eFootball, Noto
  copies); PC mod files. Recipes are text and hashes; shipping examples is D11. A publish step checks the output
  folder against this list.

---

## 7. Effort, risk, sessions

One session ≈ one working block within the usage limit. S = under a session, M = one session, L = two.

| # | Effort | Risk | What could go wrong | One session? |
|---|---|---|---|---|
| 1 | S–M | Low | `recipe.json` from the app differs from `recipes/C18.json` in field order or defaults | Yes |
| 2 | M | Medium | `PreviewCanvas` rework (transform + `IScrollInfo`) breaks drive6f's mouse coordinates; zoom around cursor off by rounding | Yes |
| 3 | M–L | Medium–High | WPF TreeView multi-select; band select clashing with click/drag thresholds; per-parent unit conversion surprises | Maybe (split tree multi-select off if needed) |
| 4a | L | High | build-path switch changes the bytes of an existing export (C6, C18 pins catch it); import loses a field; layer order | No: 2 sessions (store + import; then build-path switch) |
| 4b | M | Medium | clone naming/length rules in a UI; Solid vs PNG vs .bgra inputs | Yes |
| 5 | S–M | Low–Medium | ColorTable0164F override rows; enum value picker listing names not in the name map | Yes |
| 6 | M–L | Medium | 21 NameTag inserts' order; AppendNames grouping must match the recipe's 9 layers, or bytes differ | Maybe |
| 7 | M | Medium | candidate-class list is large; refusal reasons (other references) must be readable | Yes |
| 8 | M–L | Medium–High | automatic AppendNames for an insert must reproduce the recipes' grouping; RemoveTag stock-value display | Maybe (split appends/inserts from renames/removals) |
| 9a | M | Medium | key identity across both copies; frames vs seconds display; flatten's tangent refusal message | Yes |
| 9b | L | Medium–High | custom drawing, drag rounding to frames, performance with 1,832 channels | No: 2 sessions |
| 10 | M | Low–Medium | PC install missing (Inconclusive); Compare logic move changes REPORT.md text | Yes |
| 11a | M | Medium | removing defaults breaks tests and drives that relied on them | Yes |
| 11b | M | Low | key test-decrypt on a wrong folder; messages for every refusal is a long tail | Yes |
| 11c | M | Low–Medium | self-contained publish size; CUE4Parse native flags; a clean-machine test needs a second folder/profile | Yes |

**Resume note** (CLAUDE.md, one short block under "App parity", replaced each stage): stage done, commit, what the
next stage starts with, open decisions, any drive script to clean up (projects `zzP<n>`, mods).

---

## 8. Decisions (made by the user, 2026-10-25)

- **D1 Branch**: after `experiment` is merged into `main` (`--no-ff`, on the user's go-ahead), branch `app-parity`
  off `main`; merge each finished stage back into `main` after its tests pass.
- **D2 Advanced confirmation**: per project. `umgmod --yes` is enough, but it prints the same confirmation text
  (`AdvancedGate.Text`) as the app.
- **D3 Group move unit**: the same design units for each element (a group drag's delta is rounded to whole units
  once and applied to each element in units).
- **D4 Build path**: yes, every app export goes through the recipe path, but the old export path stays until stage 4b
  passes; the C6 and C18 pins guard the switch. Then the old path is removed.
- **D5 Import**: yes; inputs are copied by sha256 into the project's `_inputs` (untracked, under `work\edits`),
  never packaged.
- **D6 Pins**: the app never writes `Expect` pins or `recipes/pins`.
- **D7 Canary builders**: `canary-*` and `recipe-ucl` stay CLI only and are excluded from any package.
- **D8 Key in git history**: no history rewrite. This repo stays private; a public release is a fresh repo made from
  the final tree. The key is removed from every tracked file now (see Execution step 2 / stage 11a).
- **D9 Settings location**: `%APPDATA%\UmgEditor`.
- **D10 Packaging**: a zip for now; retoc not bundled; no licence for the project's own code until the user has seen
  the licence table (§6) and decided about permission and selling.
- **D11 Examples**: no recipes with third-party inputs are shipped.
- **D12 Order**: stage 1 first, then usability (stages 2–3), as proposed.
- **D13 PC**: compare only; no PC editing in the app.
