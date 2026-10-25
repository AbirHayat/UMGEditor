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
  texture's exact bytes; an existing file is only accepted when byte-identical).

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
- After a build, the mod is mounted on top of the game: every clone must decode to its pixels and every re-pointed
  package must name its new path (`TEST_INSTRUCTIONS.md` lists the re-points and the screens that show them).
- `Expect.Chunks`, when given, must name exactly the packages the recipe makes. `Expect.Files` (device file name →
  sha256) is checked by `recipe-build`.

## Recipes here
| Recipe | Device-tested | What it covers |
|---|---|---|
| `C10.json` | C10 | one inserted tag (MatchTime Text_Score_Home Left = 20) |
| `C12.json` | C12 | 23 in-place values, 2 inserted tags, 13 animation key/frame/interp/range edits; 5 Danger warnings by design |
| `C18.json` | C18 | two texture edits, This widget, appended + pointed route, 2 clones |
| `C13.json` | C13 | 2 solid-colour clones of DemoTitlePlate (Tex720 green, 0164F magenta) + DemoTitleBase re-pointed |
| `C14.json` | C14 | 2 clones resized to UCL's 696×192 image (input from `umgcompare recipe-input C14`) + the re-point with Img_Plate's margins 0.33 → 0 |
| `C15.json` | C15 | MatchTime: edit set C15 → Rename (Img_Line's path, scores white) → RemoveTag (Normal_Display's clipping); the bar art (2964×96 `.bgra`) in both theme folders; 3 Danger warnings |
| `C16.json` | C16 | the complete UCL look: 7 layered packages (MatchTime, both ColorTables, MatchTimeGoal with a ClassSwap, MatchTimeCard, MatchTimeMemberChange, DemoTitle with a ClassSwap), 11 clones (4 white, bar, logo, navy, goal art), 4 re-points; 15 Danger warnings |
| `C16b.json` | C16b | C16 with set C16b's values and art, plus AppendNames + EnumInsert (Justification = Center) on MatchTimeGoal and MatchTimeCard; 15 Danger warnings |

C15, C16 and C16b are written by `umgcompare recipe-ucl C15|C16|C16b` from the builders that made the device-tested
mods (`canary-scoreboard`, `UclC16`): the same ops in the same order, the values from the canary's edit set (the saved
file must equal a rebuild), stock values read from the game, the art as pinned `.bgra` inputs in `work\out\art`, and
`Expect` copied from `pins\device-tested.json`. Their Danger warnings come from the static layout of the edit sets'
values (umgcompare never computed them); the same values passed in game.
| `C16b_test.json` | C16b_test | `recipe-from-set` of project C16b: 8 packages, 255 values, 36 texture edits (version 1 scopes = Package), 74 chunks; Danger warnings acknowledged when it was built |

Each of these builds into exactly its device-tested files (`RecipeBuildTests`).

Every recipe of a device-tested canary must pin exactly what `pins/device-tested.json` holds
(`RecipePins_AreTheDeviceTestedPins`). `pins/device-tested.json` holds the sha256 of every device-tested canary's files and chunks (C13–C18, C16b_test).
