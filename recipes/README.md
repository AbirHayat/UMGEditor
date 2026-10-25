# Recipes

A recipe is one mod described as data (JSON, format version 1). This folder is in git and holds only text and
hashes: no keys, no game bytes, no art. The art and third-party inputs stay untracked under `work\out\art` and
`work\out\recipes\_inputs`. A recipe pins them by sha256.

```
umgmod recipe-check recipes\C12.json [--ack-danger]
```
```
umgmod recipe-build recipes\C18.json [--name MOD] [--ack-danger]
umgmod --set C18 recipe-from-set [--name RECIPE] [--container C]
```
`recipe-build` runs the check, then the export's own request checks (mod and container names, retoc, the .pak stub,
every Danger warning acknowledged), then the same build path as the app's export (`ExportRunner`: pack, retoc verify,
CUE4Parse read-back, new textures mounted on the game, predictions, `TEST_INSTRUCTIONS.md`), into
`work\out\mods\<Name or --name>`, with a copy of the recipe as `recipe.json`. Then `Expect.Files`: the device files must
be exactly the pinned ones (no file missing, none extra), or `mod\` is removed, `FAILED.txt` says why, and the exit code
is 4. Exit codes: 0 built, 2 refused, 3 Danger not acknowledged, 4 the device files aren't the pinned ones.

Every successful `recipe-build` also writes `DISABLE.ps1` next to `mod\` (`umgmod disable-script <mod>` writes one for
any built mod). Files reach the device by hand, so it never touches the phone:
```
.\DISABLE.ps1                                  the files to delete in the phone's Paks folder (name, size, sha256)
.\DISABLE.ps1 -Dir <folder>                    dry run: what would move
.\DISABLE.ps1 -Dir <folder> -Apply             move this mod's files into <folder>\_disabled\<mod>\
.\DISABLE.ps1 -Dir <folder> -Restore [-Apply]  move them back
```
It moves a file only when its name AND sha256 are one of this build's device files (read from `mod\`, and required to
equal `recipe.json`'s `Expect.Files` when there is one). Files sharing a base name (a container's `.pak` stub, `.ucas`,
`.utoc`) move only together: every mod built into `pc9999_mobile_and_P` has the same stub, so another mod's container is
never left without its stub. It never deletes or overwrites (a target that exists = that group is left alone, exit 1),
never lists or moves a stock file name (`pc0010…`, `global.*`, `pakchunk*`, every name in the stock Paks folder), and
refuses the stock Paks folder itself (exit 2). Run it with `powershell -ExecutionPolicy Bypass -File DISABLE.ps1 …`
if scripts are blocked.

`recipe-from-set` writes an app project's saved edits and texture edits as `work\out\recipes\<name>.json`. It is
refused while anything is unsaved or a saved file no longer equals a rebuild of its edits. Images become inputs pinned by
sha256, pointing into the project's `_art` folder. It adds no `Expect`: pins come from a device-tested build. Copy the
file into `recipes\` to keep it.

`recipe-check` is read only. It checks the data, every input and every stock chunk by sha256, and each value against
the stock value it names. It then builds every edit in memory with the same checks as a save (exact byte diff and
re-parse against stock), plans the texture edits with their own checks, computes the layout warnings, and compares
the result with `Expect.Chunks`. It writes nothing: only the shared caches under `work\cache` may be built. Exit codes:
0 = ok, 2 = refused, 3 = a Danger layout warning (rerun with `--ack-danger` after reading it).

## Rules
- **Data only.** The reader is strict: an unknown or duplicate field, a missing one, a comment, a trailing comma, a
  number written as a string, or an enum written as a number is refused. No field turns a check off or widens a
  window. A Danger warning can only be acknowledged on the command line.
- **Everything is pinned.** Each input carries a `Sha256`. Each package carries its `StockSha256` (a game update is a
  refusal). Each value carries its `Stock` value, its export's name (`Slot`), and `Inserted` exactly when stock
  doesn't store it. A `Value` must be what gets stored (float32-exact), and an unused input is refused.
- **Inputs** are paths relative to the workspace root (the folder holding `work\`), inside it, not links.
  Third-party inputs are copied once into `work\out\recipes\_inputs` (untracked) and pinned there: C14's UCL image
  comes from `umgcompare recipe-input C14` (read from the PC mod, written as a PNG that must decode back to the
  texture's exact bytes; an existing file is only accepted when byte-identical), UCL's font (C17a/C17b) from
  `umgcompare recipe-input C17` (read by our pak reader and by CUE4Parse, which must agree, and required to be the
  font C17a pinned).

## Format (version 1)
```json
{
  "Recipe": 1, "Name": "C18", "Container": "pc9999_mobile_and_P",
  "Inputs":   { "<id>": { "Path": "work/out/art/C18/x.png", "Sha256": "<64 hex>" } },
  "Packages": [ { "Package": "/Game/…/MatchTimeGoal", "StockSha256": "<64 hex>",
                  "Values": [ { "Export": 10, "Slot": "CanvasPanelSlot_11", "Path": "LayoutData.Offsets.Left",
                                "Stock": 0, "Value": 60, "Inserted": true } ],
                  "Layers": [ { "Op": "Rename", "EnumSwaps": [ { "Export": "Text_PlayerName", "Property": "color",
                                                                 "From": "ColorEnum_String::NewEnumerator87", "To": "…86" } ] },
                              { "Op": "ClassSwap", "Export": "MatchTimeBase_1", "To": "/Game/…/DemoTitle/DemoTitleBase" } ] } ],
  "Textures": [ { "Package": "…", "Widget": "Img_PlateBlue", "Texture": "…", "Mode": "Image", "NewName": "…",
                  "Scope": "Widget", "Input": "<id>", "Source": "…", "MarginsZero": false } ],
  "Clones":   [ { "From": "/Game/…/Tex720/DemoTitlePlate", "StockSha256": "<64 hex>", "NewName": "DemoTitleUCL01",
                  "Solid": "#00FF00" } ],
  "Repoints": [ { "Package": "/Game/…/DemoTitleBase", "StockSha256": "<64 hex>",
                  "From": "/Game/…/Tex720/DemoTitlePlate.DemoTitlePlate", "To": "/Game/…/Tex720/DemoTitleUCL01.DemoTitleUCL01",
                  "Values": [ { "Export": "Img_Plate", "Path": "Margin.Left", "Stock": 0.33, "Value": 0 } ] } ],
  "Expect":   { "Chunks": { "<package>": "<sha256>" }, "Files": { "pc9999_mobile_and_P.utoc": "<sha256>" } }
}
```
- `Values[].Path` takes the edit set's paths: layout values (`LayoutData.Offsets.Left`), animation keys
  (`anim:<channel>#<k>`, `…@frame`, `…@interp`) and range bounds (`range@<frame>`). `Export` is the slot export, or
  the section or animation export for keys and ranges.
- `Textures` are the app's texture edits (`Mode` Image/White, `Scope` Widget/Package/AllUsers).
- `Layers` (stage 2d) follow a package's `Values` (layer 1, on stock; may be empty), in order, each on the one before
  and each through its own tool with that tool's checks; then the union check against stock (`LayerUnion`: every
  layer re-parses, and no layer changes what an earlier one changed). A layer is an object whose **first** field is
  `"Op"`:
  - `Rename`: `Renames` (same-length name-map soft paths; the new package must be made by the recipe or be in the
    game, and the mounted mod must name it) and `EnumSwaps` (an export's top-level enum property, e.g. `color`, from
    its stock name to another one already in the name map), together through `NameRenamePatch` (exact diff, re-parse).
  - `RemoveTag`: `Export`, `Tag`, `Stock` (the value it holds) → `TagRemover` (back to the class default).
  - `ClassSwap`: `Export`, `To` (a stock package whose class it takes) → `ClassSwap`; the store entry's imported
    package is swapped in the mod.
  - `ColorRows`: `Rows` of `Row`, `Stock`, `Color` (`#RRGGBBAA`, upper-case) → `DataTableColorEdit` + `NameRenamePatch`.
  - `AppendNames`: `Names` → `EnumTagInsert.AppendNames` (old names and hashes kept, every export re-parses the same).
  - `EnumInsert`: `Export`, `After`, `Tag`, `Type`, `Enum`, `Value` and an `Anchor` (`Package`, `Export`: a stock
    export that stores the same tag): the tag builder must reproduce the anchor's bytes, then `EnumTagInsert.Insert`.
  - `NameTag` (stage 2e): `Export`, `Struct` (format 1: `_font` only), `Member`, `Value`, and `Stock` = the name stock
    stores there, or absent when stock doesn't store it. Present: swapped in place (`NameTagEdit.Swap`). Absent: inserted
    in FSlateFontInfo's declaration order (`NameTagEdit.Insert`), after the tag builder reproduces an `Anchor`'s stock
    tag (`Package`, `Export`, the `Value` it stores); required then, refused on a swap. Either way the stock value is
    checked against the package.
  - `FontFace` (stage 2e): `Entry` (e.g. `CompositeFont.SubTypefaces[9].Typeface.Fonts[4]`), `Name` (its typeface
    name), `From`, `To` (faces) → `FontFaceRepoint` (every entry decoded and checked against the parse).
  A tool that throws on a recipe's input refuses the layer. Re-points and texture edits aren't layered on a package
  that has `Layers` (format 1).
- `Clones` (stage 2c) make one new texture package each from one stock B8G8R8A8 texture, in the same folder, with a
  `NewName` as long as the stock short name (`TextureClone`: format, name map, rename, payloads, re-parse). Exactly one
  of `Solid` (`#RRGGBB`, upper-case: every pixel of every mip, stock size), `White` (`true`: the stock pixels with RGB
  white, alpha kept) and `Input` (an image: mip 0 at the image's size, each further mip half the one before, as many
  mips as stock, as the app does; a PNG, or raw B, G, R, A bytes in a `.bgra` file with `Width` and `Height`). The
  clone's source is pinned by `StockSha256`, and the new package must not exist in the game.
- `Repoints` (stage 2c) rename one soft path in a stock package to another of the same length (`SoftPathRepoint`: exact
  byte diff, re-parse), with stored floats changed in the same patch. Each value names its export, its path and the
  stock value it replaces; only stored values (no new tags). `To` must name a package the recipe makes or the game has.
  One entry per package, and not on a package that `Packages` or `Textures` also change (format 1 doesn't layer them).
- `LoosePaks` (stage 2e): pak-only legacy paks next to the container, each `{ "Pak", "MountPoint", "Files": [ { "Path",
  "Input" } ] }`. `Pak` ends in `_P.pak` and isn't a stock file or the container's own stub; `MountPoint` is
  `../../../<folder>/`; each file is a plain name and must replace a stock loose file (a file of a stock legacy pak) at
  `<folder><Path>`. `LegacyPak` writes it (twice: the same bytes; v11, index encrypted as stock's), our reader reads it
  back (every hash), and after the build CUE4Parse mounts it alone and reads each file back, or `mod\` is removed. A
  recipe with only `LoosePaks` builds a mod without a container (C17a).
- After a build, the mod is mounted on top of the game: every clone must decode to its pixels and every re-pointed
  package must name its new path (`TEST_INSTRUCTIONS.md` lists the re-points and the screens that show them).
- `Expect.Chunks`, when given, must name exactly the packages the recipe makes. `Expect.Files` (device file name →
  sha256) is checked by `recipe-build`.

## Recipes here
| Recipe | Device-tested | What it covers |
|---|---|---|
| `C10.json` | C10 | one inserted tag (MatchTime Text_Score_Home Left = 20) |
| `C12.json` | C12 | 23 in-place values, 2 inserted tags, 13 animation key/frame/interp/range edits; no Danger warning (see Layout warnings below) |
| `C18.json` | C18 | two texture edits, This widget, appended + pointed route, 2 clones |
| `C13.json` | C13 | 2 solid-colour clones of DemoTitlePlate (Tex720 green, 0164F magenta) + DemoTitleBase re-pointed |
| `C14.json` | C14 | 2 clones resized to UCL's 696×192 image (input from `umgcompare recipe-input C14`) + the re-point with Img_Plate's margins 0.33 → 0 |
| `C15.json` | C15 | MatchTime: edit set C15 → Rename (Img_Line's path, scores white) → RemoveTag (Normal_Display's clipping); the bar art (2964×96 `.bgra`) in both theme folders |
| `C16.json` | C16 | the complete UCL look: 7 layered packages (MatchTime, both ColorTables, MatchTimeGoal with a ClassSwap, MatchTimeCard, MatchTimeMemberChange, DemoTitle with a ClassSwap), 11 clones (4 white, bar, logo, navy, goal art), 4 re-points |
| `C16b.json` | C16b | C16 with set C16b's values and art, plus AppendNames + EnumInsert (Justification = Center) on MatchTimeGoal and MatchTimeCard |
| `C17a.json` | C17a | no container: `pc9998_mobile_and_P.pak` with UCL's font as `eFootballStencil-Regular.ufont` |
| `C17b.json` | C17b | C16b + UCL's font on the scoreboard: NameTag layers on 21 texts in 7 packages (MatchTimeTimer/Additional/Aggregate font-only), `pes` FontFace (Latin HeadlineBoldSub → NotoSansThai-ExtraBold), and the font pak with UCL's font as `NotoSansThai-ExtraBold.ufont`; 26 chunks + 4 device files |
| `C16b_test.json` | C16b_test | `recipe-from-set` of project C16b: 8 packages, 255 values, 36 texture edits (version 1 scopes = Package), 74 chunks; no Danger since 2026-10-25: MatchCard `Img_VI_1` (was below `Mask_1`, the one Danger) is hidden by the user's decision, parked off-screen (Top 737 → 847) like `Img_VI_2`–`4`. That changes the MatchCard chunk, the `.ucas` and the `.utoc` against the device-tested build: `pins/changed-since-device-test.json` (not device-tested yet) |

C15, C16, C16b, C17a and C17b are written by `umgcompare recipe-ucl <name>` from the builders that made the
device-tested mods (`canary-scoreboard`, `UclC16`, `canary-font`): the same ops in the same order, the values from the canary's edit set (the saved
file must equal a rebuild), stock values read from the game, the art as pinned `.bgra` inputs in `work\out\art`, and
`Expect` copied from `pins\device-tested.json`.

## Layout warnings
`recipe-check` lays out the value edits (stock vs edited) and lists what the export dialog would. A widget that ends up
(almost) completely outside its clip is **Danger** (canary C8: the timer kept 0.9 px inside `Normal_Display` and was gone;
canary 1: Mask at Left 829), except when the layout itself can't tell, which is said as a Warning:
- **collapsed to 0 px** wide or high by the edits (it won't show; usually on purpose: the UCL look hides plates that way);
- **moved completely off-screen** by the edits (parked, e.g. +3000 units);
- **uncertain: an animation moves it**: a margin or 2D transform track on the widget or an ancestor below its clip has
  keys the edits change (C16: Goal's `Translation[1]` keys hold the goal row 60 units lower than its slot), so the
  stored layout isn't where it rests at runtime. Stock animations whose keys aren't edited don't count: C8's timer has
  its own Info_On translation (−372) and still vanished.
A `RemoveTag` layer that takes a widget's `ClipToBounds` away (C15–C17b: `Normal_Display`) is laid out without that
clip. With these rules no canary that passed on the phone has a Danger warning; C8's edit still has one.

`recipe-check` and every build also list the widgets **hidden on purpose** (collapsed to 0 px or moved off-screen by the
edits, clipped or not), and `TEST_INSTRUCTIONS.md` turns each Warning, Danger and hidden widget into a `- [ ] Confirm on
the device` line (online packages are listed without a box: the mod never goes online).

Each of these builds into exactly its device-tested files (`RecipeBuildTests`).

Every recipe of a device-tested canary must pin exactly what `pins/device-tested.json` holds, except the deliberate
changes listed in `pins/changed-since-device-test.json` (each replaces an existing pin and says why and when;
`RecipePins_AreTheDeviceTestedPins`). `device-tested.json` itself stays the record of what was on the phone. `pins/device-tested.json` holds the sha256 of every device-tested canary's files and chunks (C13–C18, C16b_test).
