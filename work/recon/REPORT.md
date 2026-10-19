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
