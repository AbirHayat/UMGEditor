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

## Format (version 1)
```json
{
  "Recipe": 1, "Name": "C18", "Container": "pc9999_mobile_and_P",
  "Inputs":   { "<id>": { "Path": "work/out/art/C18/x.png", "Sha256": "<64 hex>" } },
  "Packages": [ { "Package": "/Game/…/MatchTimeGoal", "StockSha256": "<64 hex>",
                  "Values": [ { "Export": 10, "Slot": "CanvasPanelSlot_11", "Path": "LayoutData.Offsets.Left",
                                "Stock": 0, "Value": 60, "Inserted": true } ] } ],
  "Textures": [ { "Package": "…", "Widget": "Img_PlateBlue", "Texture": "…", "Mode": "Image", "NewName": "…",
                  "Scope": "Widget", "Input": "<id>", "Source": "…", "MarginsZero": false } ],
  "Expect":   { "Chunks": { "<package>": "<sha256>" }, "Files": { "pc9999_mobile_and_P.utoc": "<sha256>" } }
}
```
- `Values[].Path` takes the edit set's paths: layout values (`LayoutData.Offsets.Left`), animation keys
  (`anim:<channel>#<k>`, `…@frame`, `…@interp`) and range bounds (`range@<frame>`). `Export` is the slot export, or
  the section or animation export for keys and ranges.
- `Textures` are the app's texture edits (`Mode` Image/White, `Scope` Widget/Package/AllUsers).
- `Expect.Chunks`, when given, must name exactly the packages the recipe makes. `Expect.Files` (device file name →
  sha256) is checked by `recipe-build`.

## Recipes here
| Recipe | Device-tested | What it covers |
|---|---|---|
| `C10.json` | C10 | one inserted tag (MatchTime Text_Score_Home Left = 20) |
| `C12.json` | C12 | 23 in-place values, 2 inserted tags, 13 animation key/frame/interp/range edits; 5 Danger warnings by design |
| `C18.json` | C18 | two texture edits, This widget, appended + pointed route, 2 clones |
| `C16b_test.json` | C16b_test | `recipe-from-set` of project C16b: 8 packages, 255 values, 36 texture edits (version 1 scopes = Package), 74 chunks; Danger warnings acknowledged when it was built |

Each of these builds into exactly its device-tested files (`RecipeBuildTests`).

`pins/device-tested.json` holds the sha256 of every device-tested canary's files and chunks (C13–C18, C16b_test).
