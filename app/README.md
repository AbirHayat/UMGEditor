# UMG editor (eFootball Mobile)

Moves and resizes widgets of eFootball Mobile's in-match UI (scoreboard, goal banner, card and substitution pop-ups)
by patching their stored layout values in place, and packs the edits into a mod container for the device. The game
files are only read. Edits go to `work\edits`, mods to `work\out\mods`.

## Quick start

### 1. Launch
- Needs the .NET 10 SDK, the stock Paks of the game (the OBB contents) and retoc (see *External tools* below).
- Build: `dotnet build app/UmgEditor.slnx -c Release`.
- Run: `app\src\UmgEditor.App\bin\Release\net10.0-windows\UmgEditor.App.exe`. Without a `umgeditor.json` next to the
  exe it uses the defaults in `Core/GameConfig.cs`. To point it elsewhere, put a `umgeditor.json` there with `PaksDir`
  (the stock Paks folder), `AesKey`, `WorkDir`, `RetocPath` and `ModStubPak`. The first start builds the UI index
  (`work\cache`), which takes a while once.

### 2. Make an edit
1. Pick a **project** in the top bar, or type a name and create one. A project is one set of edits
   (`work\edits\<name>`); keep one per experiment.
2. Search for a package (e.g. `MatchTime/MatchTime`, the offline scoreboard), select it and pick a widget in the
   **Tree**.
3. Change a value in the inspector (e.g. `Offsets.Left`, then Enter), or drag the widget or an edge in **Preview**
   (Ctrl = free, without unit snapping; arrow keys nudge 1 unit; zoom for finer drags). The first edit of a package shows
   which screens it affects and every warning; confirm to go on.
4. Check **Impact** (every screen the edit reaches, stock vs edited, and its warnings) and **Bytes** (exactly which
   bytes change). Undo/redo with Ctrl+Z / Ctrl+Y. **Save** (Ctrl+S) writes the patched copy.

### 3. Export a mod
**Export mod…** → mod name, container (keep `pc9999_mobile_and_P`), tick the packages, check the retoc line (green =
the tested build). Read the **layout warnings**: each Danger one (something will vanish in game) needs its tick
before **Build mod** is enabled. The build packs and verifies the container, then lists the 3 files with their hashes.
`work\out\mods\<name>\` holds `mod\` (the device files), `predictions\` and `TEST_INSTRUCTIONS.md`.

Without the UI: `umgmod build <name> <package>... [--ack-danger]` (see below).

### 4. Test on the device (offline only)
Follow `TEST_INSTRUCTIONS.md` in the mod folder. In short:
1. Force-stop eFootball. In the folder that holds `pc0010_mobile_and.*`, delete any old `pc9999_mobile_and_P.*`, then
   copy the 3 files from `mod\`.
2. Play an **offline** match vs AI and screenshot each screen the instructions list (LDPlayer at 1920×1080).
3. Compare with the prediction: the `validate_layout.py` commands in the instructions (±3 px).

### 5. Remove the mod before playing online
**Delete the 3 files (`pc9999_mobile_and_P.pak`, `.ucas`, `.utoc`) before any online match.** The mod changes the game's
UI files, and edits to shared parts (e.g. MatchTimeBase) also change the online board and pop-ups. Put them back
afterwards for more offline tests.

## Known limits
- **Stored values only.** A value is edited where the cooked file already stores it, at the same size. A field the
  file doesn't store (the engine default) is greyed out, with the reason. Only CanvasPanelSlot position, size, anchors,
  alignment and ZOrder are editable.
- **No keyframes in the editor.** The editor doesn't edit animation tracks. An animation that keys a slot's margins
  (e.g. `KickOff_Show`) or moves a widget can override or shift your value while it plays. The pre-edit warnings name
  these. (Branch `experiment`: `umgmod anim-flatten` changes key values; see below.)
- **Preview placeholders.** Text is drawn as the widget's name in a placeholder font ("font not rendered"),
  materials and textures that can't be decoded are labelled boxes, and tint isn't applied. Positions and sizes match
  the game (checked against screenshots to ±3 px); colours and glyphs are not reproduced.
- **Library widgets are blocked.** The insides of `/Game/Assets/ui/library/` widgets (uiImage, uiText, …) are shared by
  much of the game's UI, so they can't be edited. An instance placed in your package (e.g. `Img_Logo`, a uiImage) is
  moved through its own slot as usual.
- **Online packages are untested.** The online board and pop-ups (`MatchTimeConnection*`) are only predicted, never
  checked in game. A mod is for offline testing and must not be taken online.
- Clipping is predicted, not avoided: moving a widget outside a parent that clips it hides it (canary C8). Watch the
  Impact warnings.

## Reference

See `../CLAUDE.md` for the build/test commands and `../work/recon/REPORT.md` for the format findings.

## External tools

### retoc (packing mods)
`ModBuilder` runs `retoc pack-raw` and `retoc verify`. The path is the `RetocPath` setting in `umgeditor.json`
(default below). This retoc build has no `--version` flag, so it is identified by its hash. `umgmod build` warns
when the configured exe differs from it (`ModBuilder.TestedRetocSha256`), and every build records the hash it
used in `work/out/mods/<name>/build.json`.

| | |
|---|---|
| Path | `E:\n3tron\patch\Tools\retoc-x86_64-pc-windows-msvc\retoc.exe` (prebuilt release, not the patched source in `tools/retoc`) |
| sha256 | `1587f00d57c5046dc51dbc5c0e39f65d937a57943cac1db4ce059335d3a1e3a4` |
| Size / date | 5,598,208 bytes, 2025-08-21 |
| Used for | canaries 1 and 2 (proven in game), the make_mod.py equivalence test, canaries C1–C3 |

The export dialog (below) shows the sha256 of the configured retoc next to its path, green when it is this build and
amber ("NOT the tested build", a warning, not a refusal) otherwise. The path is a setting: the dialog saves it to
`umgeditor.json` next to the exe when a build starts. Every export records the retoc path and sha256 it used in
`build.json` and in `TEST_INSTRUCTIONS.md`.

### Mod stub
`ModStubPak` (default `pc0100_mobile_and.pak`, a 365-byte stock mount stub in PaksDir) is copied as
`<container>.pak`. It is only read.

## Exporting a mod from the editor ("Export mod…")
Packs the project's **saved** edits into one container under `work\out\mods\<name>` (nothing else is written):

- Inputs: mod name (a plain folder name), container name (must end in `_P`, default `pc9999_mobile_and_P`), the
  packages with saved edits (ticked), the retoc path (see above).
- Refused (Build stays disabled, the reasons in red) while anything in the project is unsaved, for a bad mod or
  container name, no package, the same package twice, a package without saved edits, or a missing retoc / .pak stub.
- **Layout warnings:** the Impact panel's warnings for the saved edits of the ticked packages are listed (rescale,
  widgets outside the edit moving, a widget pushed out of a clipping ancestor). Each **Danger** one (something will
  vanish: completely outside its clip, or less than 3 px of it left inside, as canary C8's timer) has its own tick box,
  and Build stays disabled until every one is ticked. They go into the log and `TEST_INSTRUCTIONS.md` (with whether
  they were acknowledged).
- The build runs in the background with a live log (each retoc command and its output), then lists the files for the
  device with their sizes and sha256. `ModBuilder` does the packing (same steps as `umgmod build`); a failed
  `retoc verify` or CUE4Parse read-back aborts, removes `mod\` and writes `FAILED.txt`.
- On success the folder holds `mod\` (only the 3 files for the device), `predictions\` (stock and edited layout JSON
  of every package the edits reach, for `validate_layout.py`), `build.json`, and `TEST_INSTRUCTIONS.md` (like
  `work/out/mods/CANARY_C6.md`): the layout warnings, the files and hashes, the edits and their bytes, the steps, every
  affected screen (online ones marked "predicted only"), the predicted rects (a box a clip cuts off more than in stock
  is marked **cut off by X: w×h of W×H px visible** or **outside X: not visible**), the checking commands (edges that
  aren't on screen are left out, with a `#` line saying why), and the warning to remove the mod before playing online.

## Making a mod without the UI: `umgmod`
`app/tools/UmgEditor.ModCli/bin/Release/net10.0/umgmod.exe`:

```
umgmod [--set NAME] edit  <package> <widget> set <field> <value> [--yes]
umgmod [--set NAME] edit  <package> <widget> move <dx> <dy> [--yes]
umgmod [--set NAME] edit  <package> <widget> resize <Left|Top|Right|Bottom> <d> [--yes]
umgmod [--set NAME] show  <package>
umgmod [--set NAME] reset <package>
umgmod [--set NAME] build <modName> <package>... [--container pc9999_mobile_and_P] [--ack-danger]
umgmod [--set NAME] predict <package>...
umgmod show-anim <package> [--anim REGEX] [--widget NAME] [--bytes]
umgmod [--set NAME] anim-flatten <package> <anim,anim...> [--parts width,visibility,positions] [--normal-from Info_Hide] [--check Show] [--yes]
```

- `edit` applies one SlotEditor edit to the package's PatchSession and saves it (`work\edits[\NAME]\<package path>`).
  The first edit of a package prints the pre-edit warnings and stops (exit 3); add `--yes` once you've read them.
- `show` prints the saved edits and the bytes to patch (offset, old/new value, hex, ±32-byte context).
- Branch `experiment`: `edit … set Offsets.Left 20` also works on an Offsets value that isn't stored (shown as
  `default`), as long as another value of that Offsets struct is. Saving adds it as a 29-byte tag, so the package
  grows (`show` lists it as `NEW TAG` with the header values it changes). Device-tested by canary C10.
- `show-anim` prints every animation of the package and what each track does; `--bytes` adds every key with its
  byte offsets (section copy and compiled copy). Read-only.
- Branch `experiment`: `anim-flatten` makes the named animations keep the normal state. Every key of every channel is
  set to that channel's last key in `--normal-from` (default `Info_Hide`), cross-checked against its last key in
  `--check` (default `Show`). A mismatch, a missing value, or a float channel with a tangent above 1e-6 refuses the
  plan. `--parts` picks the tracks: `width` (slot margin tracks), `visibility` (visibility byte tracks), `positions`
  (2D transform and opacity tracks). Only key values change, at the same size, in the section copy **and** the
  compiled copy (`MovieSceneCompiledData`) when there is one. It is saved like any edit (first edit: exit 3 until
  `--yes`), and `show`/`build` list every write. Canary C11 was made this way and passed in game
  (both copies are read; `work/out/mods/CANARY_C11.md`).
- Animation keys have their own location rule. Key blocks repeat across widgets, and a key's two copies are
  byte-identical, so the ±32-byte window often isn't unique. Instead, each write records the smallest window around
  it, computed on the stock bytes, that occurs exactly once in the chunk (±32 in 16-byte steps). Beyond ±512 it is
  refused. The location must also follow from the section's own tags, the compiled copy must be inside the
  animation's compiled export with the same stock bytes, and the stock bytes must decode to the parsed value. The
  exact byte diff and the full re-parse of every export stay hard refusals. Layout values keep the ±32 rule.
- `build` is the export dialog without the window: the same plan (`PlanExport`: the same refusals) and run
  (`RunExport`), so it writes `mod\<container>.{utoc,ucas,pak}`, `predictions\` and `TEST_INSTRUCTIONS.md` under
  `work\out\mods\<modName>`. The packing steps are the same as `work/scripts/make_mod.py` (kept as the reference),
  followed by `retoc verify` and a CUE4Parse read-back; a failed step removes `mod\` and writes `FAILED.txt`. It
  prints the layout warnings first. A **Danger** one stops the build (exit 3, nothing written) until you rerun with
  `--ack-danger`, the dialog's tick boxes: it acknowledges every Danger warning listed. Any other refusal is exit 2
  (the flag doesn't get past it). Nothing under PaksDir is written or deleted.
- `predict` arranges, stock vs edited at 1786×1000, every package the saved edits reach: the edited packages plus
  every package that embeds them (e.g. MatchTimeBase → the goal/card/substitution pop-ups and the online board). It
  prints the Impact panel's warnings per package (`[Danger]` / `[Warning]`), every box that changes (cut-off boxes
  marked like in the instructions) and the warning count, and writes
  `work\out\mods\_predictions\<set>\<Package>[.stock].json` for `validate_layout.py`.
- `--set NAME` keeps independent edits of the same package apart (e.g. one per canary).
- `<package>` is a `/Game/...` path or a unique suffix (`MatchTime/MatchTimeBase`). `<field>` may omit
  `LayoutData.` (`Offsets.Right`).
