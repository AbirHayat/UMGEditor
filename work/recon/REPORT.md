# Phase 1 recon report: eFootball Mobile UMG (2026-10-19)

Source: `E:\n3tron\patch\obb\mobile\org\all\PesMobile\Content\Paks` (read-only; sha256 of all 50 files identical before/after, see `stock_hashes_before.txt` / `stock_hashes_after.txt`).

## 1. Inventory
- 5,181 unique widget packages under `PesMobile/Content/Assets/ui/Data/Widget/` (5,239 directory entries; the rest are overridden copies in later containers). List: `widgets.csv`.
- In-match TVGraphics: 304 packages, mostly in `pc1000_mobile_and`; `pc0010_mobile_and` overrides 16 (MenuCall / NamePlateEmoji / InteractiveGoal).
- Reading: CUE4Parse with `EGame.GAME_eFootball` (it handles eFootball's non-standard package store entry). AES = the PC key, zero GUID.

## 2. Formats (answers the core questions)
- **Properties are TAGGED, not unversioned** (`PackageFlags = PKG_FilterEditorOnly`, no `PKG_UnversionedProperties`).
- **No mappings file needed.** CUE4Parse parsed 226,314 / 226,314 exports across all widget packages with no `.usmap`, with 0 errors (`survey.json`).
- **UAssetAPI can read and write it back without mappings:** after `retoc to-legacy`, UAssetAPI (`VER_UE4_26`, no usmap) loads MatchTime/MatchTimeTimer with 0 RawExports. An unchanged write is **byte-identical** (.uasset and .uexp). Changing one `LayoutData.Offsets.Left` (60→160) changes **exactly 2 bytes** in the .uexp, keeps the size, keeps the trailing magic `C1 83 2A 9E`, and the edit survives a reload (`work/scripts/UapTest`).
- Textures: Tex720 UI textures are `PF_ETC2_RGB` / `PF_ETC2_RGBA` (plus tiny `PF_B8G8R8A8` color swatches), 1 mip, payload **inline in .uexp** (`BULKDATA_ForceInlinePayload`, no .ubulk). Decoded to PNG with CUE4Parse's managed ETC2 decoder (`tex_test/`): the eFootball logo and the antenna bars render correctly. (The FModel cross-check was not run; the visual check was clear. Two `MatchTimeLogo` variants share one output name, so the 0164F one overwrote the base one.)

## 3. Widget types and properties
- Most common classes overall: CanvasPanelSlot (19,384), CanvasPanel (8,948), Konami BP widgets `/Game/Assets/ui/library/uiImage_C` (4,884) and `uiText_C` (3,100), ScaleBox/ScaleBoxSlot, VerticalBox/HorizontalBox slots, RetainerBox, `uiEmblem_C`, PesShared customs (`TileViewCustom`, `VerticalBoxCustom`, `HorizontalBoxCustom`), plus a lot of animation (WidgetAnimation 9,063 and MovieScene tracks). 2,188 distinct classes.
- Image/text are **Konami BP widgets, not UMG Image/TextBlock**: `uiImage_C.texture` (soft path to a Tex720 texture), `uiText_C._font.Size`, `color` (enum `ColorEnum_String`), `Justification`, `VerticalAlignment`, `CutOffOption`.
- Layout: `CanvasPanelSlot.LayoutData { Offsets{Left,Top,Right,Bottom}, Anchors{Minimum,Maximum}, Alignment }`, plus `bAutoSize` / `ZOrder` where set. Root: `WidgetTree.RootWidget` → CanvasPanel → ScaleBox → CanvasPanel "Main" → …

## 4. Scoreboard structure (`tvgraphics_graph.json`)
- Offline `MatchTime` embeds MatchTimeBase, MatchTimeBase_1, Timer, CardHome/Away, Compe, Aggregate and Additional.
- **Online `MatchTimeConnection` embeds the same offline MatchTime children** plus its own Connection* children (Timer, Antenna, Cards…). Editing a shared child (e.g. `MatchTimeBase`) changes **both** scoreboards.
- Halftime `MatchScore` → Base/_2/_3, Cell, Compe, uiEmblem. Stats `MatchStats` → Base/_2/_3, ListPlate, Manager, Compe.

## 5. Constraints for editing (feature 4)
1. **Only present fields can be patched in place.** Tagged serialization omits default values. In MatchTime, 11 of 24 CanvasPanelSlots have all four Offsets; the others are missing some (e.g. Left/Top = 0 are not stored). Changing a missing field needs a new property tag (size change), which means UAssetAPI re-serialization rather than a byte patch. Decision needed before F4.
2. **Animations can override layout at runtime** (the derived-values trap): MatchTime has 59 `MovieSceneMarginTrack` (animates slot offsets) and 104 `MovieScene2DTransformTrack`. Elements driven by a margin track will snap back to the animated keys, so the tool has to flag animated widgets and offer to shift their keys.
3. **Zen patch path:** the edited float plus ±32 bytes of context is found exactly once in the raw Zen package, at a different absolute offset (Zen stores exports in bundle order). Plan: map legacy offset → Zen offset per export (export start + relative offset), verify the context, then patch the Zen bytes in place. That avoids `to-zen` on a Blueprint entirely.

## 6. Tools status
- Prebuilt `E:\n3tron\patch\Tools\retoc-x86_64-pc-windows-msvc\retoc.exe`: `list`, `to-legacy` (needs `--override-container-header-version PreInitial --version UE4_26`), `unpack-raw` and `pack-raw` are all present. `to-legacy` converted 31/31 MatchTime assets.
- .NET 10 SDK 10.0.402 installed. CUE4Parse cloned to `tools/CUE4Parse` (built with `CUE4PARSE_SKIP_NATIVE=true`; the managed ETC2 decoder means no native DLL is needed).

## 7. Canary test for the device
Patch one present float far off screen (e.g. the `Text_Score_Away` slot `Offsets.Left` +600) in a copy, export it as `_P` (F5 path), install, and start an offline match. If the score moves, the package is live. If nothing changes, check mounting first, then animation tracks.

## Open decisions
- Missing-field edits: allow UAssetAPI re-serialization for those slots (size change, so it has to be proven with a canary), or limit edits to present fields?
- Zen export path: in-place patch of the raw Zen chunk + `pack-raw` (preferred) vs. `to-zen` from legacy (the tutorial forbids it for BPs).

---
# Addendum (after decisions 1 & 2)

## Widget count: 5,239 vs 5,181
- **5,239** came from a loose text grep (`Widget/.*\.uasset`). It also counted **55** packages under the older `Content/Assets/ui/Widget/` folder (facility, Rehearsal, Provisional/SaveSystem…).
- **5,184** packages are under `ui/Data/Widget/`. No path appears in more than one container, so there are no duplicates.
- **5,181** is the survey, which only looked under `PesMobile/Content/Assets/ui/Data/Widget/`. The missing **3** are Android platform overrides under `PesMobile/Content/Platforms/_Android/Assets/ui/Data/Widget/.../UiPlateOnBlur/Tex720/` (textures).
- Consequence for F1: browse `Assets/ui/Data/Widget`, `Assets/ui/Widget` and `Platforms/_Android/Assets/ui/...`, and show platform overrides as such.

## Keyframes: patchable in place, yes
- Section channels (`FMovieSceneFloatChannel`: Times[] int32 + Values[] fixed-size records) are parsed by UAssetAPI as typed data. Changing a margin key 48→148 changed **2 bytes**, kept the size, and survived a reload.
- **Margin tracks are stored twice**: the section, plus the compiled `MovieSceneCompiledData.EvaluationTemplate` (`MovieSceneMarginSectionTemplate`), which is what 4.26 evaluates at runtime. The copy was found exactly once inside the compiled export. UAssetAPI doesn't edit it, so it gets a byte patch inside the compiled export's range, with the same context rule.
- 2D-transform / float / color / byte tracks: no compiled copy in this asset (only margin + byte templates appear), so section-only.
- Limit: only existing keys can change (value/time). Adding or removing keys changes the size, which falls under the "experimental re-save" feature.

## Container facts learned building the canary
- eFootball container header (pre-version 4.26 layout): ContainerId u64, PackageCount u32, Names (empty), NameHashes (8 bytes `00 00 64 c1 00 00 00 00`), PackageIds[], StoreEntries blob (16-byte entries: ExportCount, ExportBundleCount, CArrayView imports with offset relative to the view), then CulturePackageMap = 0, Redirects = 0. Parser/builder: `work/scripts/hdr.py`.
- Konami's container ids are **not** CityHash(filename) (retoc's id for "pc0010_mobile_and" ≠ stock). Only internal consistency matters (TOC id = header chunk id = id in header). The canary uses retoc's id for its own name.
- `pack-raw` output: TOC v2, header 0x90, flags `Indexed` (uncompressed, unencrypted, truthful), zero AES GUID, ContainerHeader sorted first. `retoc verify` passes. retoc itself can't parse eFootball headers (same as stock); CUE4Parse (`GAME_eFootball`) can.
- Stock `.pak` stubs (365 B) are valid v11 paks with an encrypted index and no files.

## Canary result (2026-10-19, LDPlayer / Android)
`pc9999_mobile_and_P` (MatchTime, `CanvasPanelSlot_29`/Mask Left 129→829, Top 7→407) in an offline match: no crash, the scoreboard renders (team names, timer, logo), and **both score numbers are gone** from their boxes (`ss/ss_1.png`). Only the Mask slot changed, so the package was mounted and its layout was used; the moved panel ends up clipped outside its parent. The pipeline (in-place Zen patch → pack-raw → hand-built eFootball header → copied .pak stub) works on device.

## Canary 2 result (2026-10-19, LDPlayer)
Mask `Offsets.Left` 129→249 (+120 units, a 1-byte diff). `ss/ss_2.png`: both score digits are visible again (dark navy, score 2–0), shifted right onto the away name box: home digit centre ≈ x 238 → 352, away ≈ 294 → 411, so **≈ +115 px on screen for +120 units**. The game view in the 1920-px screenshot spans x 50–1836 (1786 px), i.e. 1786/1920 = 0.93 px/unit (predicted +112 px). Measured and predicted agree within reading error (±5 px).
Conclusion: slot offsets are in **1920×1080 design units, scaled uniformly to the render width**. Position edits behave exactly as the numbers say. Feature 3's preview can use this model directly.

## Feature 3 validation, part 1 (2026-10-19)
- **Scale gate (1786×1000, the measured LDPlayer game view):** UI scale from the game's `Engine/Config/BaseEngine.ini` (DefaultEngine.ini doesn't override it): ShortestSide, curve 480→0.444 / 720→0.666 / 1080→1.0 / 8640→8.0. DPI = 0.92578 at 1000 px. ScaleBox_0 (ScaleToFit, both directions): x 1.00479, y 1.00016 → **height limits**. Result 0.92593 px/unit = **0.93**, matching canary 2. Under a ScaleToFit ScaleBox the DPI cancels: px/unit = min(1786/1920, 1000/1080).
- **Early layout check against `ss/ss_1.png`** (stock except the canary-moved Mask): 28 visible edges of the home/away colour bars, base plate, score boxes, divider and timer plate: **all within ±1.2 px (≤1.32 design units)**, most within 0.6 px. Not scored: edges covered by other elements in game (plate left under the colour bars, timer right merging with the logo box).
- **CanvasPanelSlot default Bottom = 30 confirmed:** the colour bars store no Bottom and measure 27.8 px = 30 × 0.926.
- Expected differences (runtime state, not layout): aggregate/additional rows, red-card bars and the eFootball logo are hidden or animated in play; plate tints and fonts are applied at runtime.
- **Textures:** all 2,933 UI textures decode (ETC2_RGBA 2,434, ETC2_RGB 243, B8G8R8A8 252, G8 4). The FModel comparison is pending the user's exports.

## Feature 3 validation, part 2: stock screenshot + FModel (2026-10-19)

### Textures vs FModel (`work/scripts/compare_tex.py`, exports in `fmodel_exports/`)
| Texture | Size | Orientation | Red/blue order | Alpha | G, B, A | Red |
|---|---|---|---|---|---|---|
| AlertIconDialog_Progression_ImgUser (ETC2_RGBA) | 132×132 = | OK | OK | identical | identical | FModel +0..3 |
| SupportersRewardImgEffectBack (ETC2_RGBA) | 436×176 = | OK | OK | identical | identical | FModel −0..6 |
| BGeSportsGeneral1609 (ETC2_RGB) | 1280×720 = | OK | OK | opaque both | identical | FModel +0..3 |

The red difference is a ramp by x within each 4×4 block, and only in **ETC2 planar-mode** blocks. An independent decoder written from the Khronos spec (`work/scripts/etc2_block.py`) matches **ours in 61,331 of 61,331 planar blocks and FModel in 0**. RH=0 with bit 32 = 0 gives flat red, while FModel ramps as if it used bit 33 (the planar flag) as RH's low bit. Verdict: our decoder is correct; FModel's export has a planar-mode red error.

### Layout vs `ss/stock.png` (LDPlayer 1600×900 @240 dpi, maximized window; game area x 50–1835, y 32–1031)
- **Scale (checked first):** laying out at the 1600×900 setting with a non-uniform window stretch (x 1.11625, y 1.1111) passed but left a growing x bias (−0.9 → −2.3 px). A fit of the measured edges gives 1.1117 horizontally, the same as the vertical 1.1111. So the rendered viewport has the window area's aspect (1786:1000), not exactly 16:9. **The wrong input was the viewport size**, not the DPI curve (which cancels under ScaleToFit). Using the game view 1786×1000 as the viewport: fit 0.92631 px/unit vs model 0.92593 (0.04 %), offset −0.17 px.
- **Scored edges (game-view viewport), all PASS ±3 px:**

| Element | Edge | px | design units |
|---|---|---|---|
| Img_MatchTimeBg plate | top / bottom | 0.0 / −0.6 | 0.00 / −0.60 |
| Img_MatchTimeBg_1 (score boxes) | left / right / top / bottom | −0.1 / −0.2 / 0.0 / −0.6 | −0.12 / −0.24 / 0.00 / −0.60 |
| Img_Line (divider) | left / right | +0.3 / −0.6 | +0.28 / −0.64 |
| Time_Timer › Plate_MatchTimeBg | left / right / top / bottom | +0.1 / 0.0 / 0.0 / −0.6 | +0.12 / 0.00 / 0.00 / −0.60 |

  The −0.6 on bottom edges is the antialiased half pixel at y = 80.6.
- **Excluded from scoring (hidden, animated or tinted at runtime):**
  - Hidden (aggregate row): Time_Aggregate › Plate_MatchTimeBg_0 › Img_Plate, Img_Line, Text_Aggregate_Home, Text_Aggregate_Away
  - Hidden (added-time row): Time_Additional › Plate_MatchTimeBg › Img_Plate, Text_AdditionalTime
  - Hidden (red-card bars): MatchTimeCardHome › Img_RedCard_Home_1..4, MatchTimeCardAway › Img_RedCard_Away_1..4
  - Logo area (animated/hidden): Img_Logo (shown at about half size in game), MatchTimeCompe › Compe › MatchTimeBase_1 › Img_Plate, uiCompe
  - Tinted at runtime (team colours; white textures shown red/white and white/black): Img_HomeColor_1, Img_HomeColor_2, Img_AwayColor_1, Img_AwayColor_2. Measured for information: all 16 edges within 1.3 px.
- **Scored plates are not tinted:** the navy base plate and the yellow score/timer plates show their texture colours as-is (difference ≤ 12/765).
- **Not measurable (no box edges):** the text widgets (Text_TeamName_*, Text_Score_*, Text_Minute_3, Text_Colon, Text_Second_*) and pure containers. Plate edges hidden under other elements weren't scored: the base plate's left/right sit under the colour bars.

### Found and fixed during this run
- Asset paths differ in letter case from file keys (`MiniGame/CommonHud` vs `CommonHUD`): package loads now resolve case-insensitively, as the engine does (exact match still preferred).
- Three textures are referenced as `/Game/Platforms/_Windows/...` but ship as `Platforms/_Android/...`: the resolver maps them. All 4,369 uiImage texture references (2,236 distinct) now resolve.

## Canaries C1–C3 (2026-10-19, LDPlayer, built by `umgmod`; screenshots `ss/c1_a..c3_c.png`)
The user also had a scoreboard texture mod installed: the score/timer boxes are white instead of yellow and the navy
plate is a slightly lighter navy. That changes colours only; every geometry check below passes.
`validate_layout.py` against `work/out/canary_predictions/*.json` (crop 50/32), a = right after kick-off, b = in play, c = after a goal:

| Canary | Edit | a / b / c | Result |
|---|---|---|---|
| C1 | `Img_MatchTimeBg_1` Left 126→146 | left 180/180/180 (pred 179.6), right 291 ×3 (pred 290.7) | **stored value shown** ×3; the stock-position edges are gone |
| C2 | `Img_MatchTimeBg_1` Right 120→150 | right 300 ×3 (pred 300.0), left 161 (pred 161.1) | **stored value shown** ×3 |
| C3 | MatchTimeBase `Img_Plate` Right 0→100 | plate right 296/295/296 (pred 296.3); score box unchanged | **stored value shown** ×3; MatchTimeBase (a second package) loads from the mod |

- **Margin keys:** `KickOff_Show` keys this slot's Left = 126 and Right = 120, yet the stored 146/150 are on screen from
  the first screenshot on. After the ~2.9 s intro the animation doesn't hold its keys. The screenshots don't show the
  slot *during* the intro (a was taken at 0:00 with the board already in), so whether the keys apply while it plays
  is still open.
- **Not predicted:** in C3 the goal banner's navy "Today's Goals" plate (`MatchTimeGoal`, one of the 8 direct users of
  MatchTimeBase) also ends ≈ 92 px earlier (x ≈ 605 → 513 in the screenshot; 100 units × 0.926 = 92.6 px). The
  white name bar is a different plate and unchanged. MatchTimeCard / MatchTimeMemberChange and the online
  equivalents embed MatchTimeBase too, so they are presumably affected the same way. The predictions only arranged
  the MatchTime board. A shared-child edit needs a prediction for every direct user, and the warning should name them.

## Canary C6 (2026-10-19/20, LDPlayer, built by `umgmod`; screenshots `ss/c6_a..c6_e.png`)
First container with **two packages** (MatchTime + MatchTimeBase, two store entries in the header): C1's
`Img_MatchTimeBg_1` Left 126→146 plus C3's MatchTimeBase `Img_Plate` Right 0→100. Same texture mod as C1–C3 (white
boxes). `validate_layout.py` against `work/out/mods/_predictions/C6/*.json` (crop 50/32), game px:

| Shot | Screen | Element | Measured (pred) | Result |
|---|---|---|---|---|
| c6_a | kick-off, 0:00 | score box L / R | 180 / 291 (179.6 / 290.7) | PASS |
| | | navy plate R | 296 (296.3) | PASS |
| c6_c | in play, 11:04 | score box L / R | 180 / 291 (179.6 / 290.7) | PASS |
| | | navy plate R | 295 (296.3) | PASS |
| c6_b | goal banner | "Today's Goals" plate R | 463 (463.0) | PASS |
| c6_d | card pop-up ("Booked") | navy plate R | 452 (451.9) | PASS (first measurement) |
| c6_e | substitution pop-up | navy plate R | 452 (451.9) | PASS (first measurement), see below |

All top/bottom edges are within 2 px (tops +1.4…+2.0, bottoms the usual −0.6 half pixel).

- **Two-package container works:** both edits are on screen together, so ModBuilder's multi-entry header is accepted
  and both store entries load.
- **Card and substitution pop-ups confirmed:** the MatchTimeBase edit reaches them as EditImpact predicted
  (−92.6 px each). That closes the "presumably affected" note from C3 for the offline packages; the four online
  MatchTimeConnection* packages are still predicted only.
- **Validator false FAIL on c6_e (fixed):** it first reported 448 (−3.9 px) because its scanlines crossed the glyphs
  of "P. E. Aubameyang", which runs over the plate's edge. `validate_layout.py` now re-picks scanlines that cross a
  text widget's box when the inside of the edge has glyph ink (a step no image widget's edge explains) and prints a
  note with the lines used: c6_e now measures 452 (+0.1 px, PASS), the same as c6_d. Rerun over every image widget of
  the five C6 exports: every previously scored edge measures the same as before, except the c6_c timer plate's right
  edge (505 FAIL → 500 PASS, the "11:04" digits were next to it); only the hidden aggregate / added-time rows change
  otherwise (now n/a or different).
- **KickOff_Show still open:** c6_a again shows the board fully in at 0:00 with the stored value, as in C1. Catching
  the slot *during* the ~2.9 s intro needs a screen recording rather than a screenshot.

## Canary C8 = mod `ucl_c1` (2026-10-20, LDPlayer, built by the editor's "Export mod…"; screenshots `ss/c8_a..c8_c.png`)
First mod exported from the editor (step 6g). One package, MatchTime, 8 saved edits: `Img_Logo` moved to the board's
left end and halved (Left 492→0, Top −21→10, Right 120→60, Bottom 120→60); `Text_TeamName_Away` Left 252→371,
`Img_AwayColor_1`/`_2` Left 366→485, `Time_Timer` Left 368→491 (all four ≈ +110…114 px to the right). Same texture mod
as before (white score boxes). a = in play (0–0), b = goal banner, c = card pop-up (covers the board).
`validate_layout.py` against `work/out/mods/ucl_c1/predictions/MatchTime.json` (crop 50/32), game px:

| Shot | Element | Measured (pred) | Result |
|---|---|---|---|
| c8_a | `Img_Logo` L / T / B | 45 / 25 / 80 (44.4 / 25.9 / 81.5) | PASS |
| c8_b | `Img_Logo` L / T / B | 45 / 25 / 82 (44.4 / 25.9 / 81.5) | PASS |
| c8_a, c8_b | `Text_TeamName_Away` L | 389 ×2 (388.0) | PASS |
| c8_a, c8_b | `Img_AwayColor_1` T / B | 25 / 52, – / 52 (25.0 / 52.8) | PASS |
| c8_a, c8_b | `Img_MatchTimeBg` (not edited) L / R | 45 / 388 ×2 (44.4 / 388.9) | PASS, unchanged: "JCU" now sits on the stadium, not on the plate |
| c8_a, c8_b | `Time_Timer` | **not on screen** | timer gone |

- **Timer vanished: clipped by its parent.** `Normal/Normal_Display` (the CanvasPanel holding names, colours and the
  timer) is stored with `Clipping = ClipToBounds`; its box is 44..500 px. The edit moved `Time_Timer` to 499.1..617.6,
  leaving 0.9 px inside. In both shots every pixel column right of the away colour bars (screenshot x ≥ 549 = game 499)
  is stadium: the cut is exactly at the parent's edge. The validator's `Time_Timer left 499 PASS` is that cut (bars →
  stadium), not the timer.
- **Predicted:** the Impact panel for project `ucl_c1` gives exactly one warning, *"Time_Timer ends up partly outside
  Normal_Display: it will be cut off (visible 0.9×57.4 px of 118.5×59.3, stock showed 114.8×57.4 of 118.5×59.3)"*.
  The game agrees with the layout engine's clip rect. But the warning was only in the editor's Impact tab: the
  export dialog, `TEST_INSTRUCTIONS.md` and `umgmod predict` didn't carry it, and the predicted-rects table listed the
  timer's full rect as if it would show. Fixed in the next step (warnings carried everywhere, Danger warnings need a
  tick before Build, clipped boxes marked).
- To bring the timer back, either keep it inside x ≤ 500 or widen `Normal`/`Normal_Display` (and `Img_MatchTimeBg`
  for the plate behind the away name). Not done: C8 is recorded as is.
