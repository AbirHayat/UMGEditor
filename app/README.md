# UMG editor (eFootball Mobile)

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
- The build runs in the background with a live log (each retoc command and its output), then lists the files for the
  device with their sizes and sha256. `ModBuilder` does the packing (same steps as `umgmod build`); a failed
  `retoc verify` or CUE4Parse read-back aborts, removes `mod\` and writes `FAILED.txt`.
- On success the folder holds `mod\` (only the 3 files for the device), `predictions\` (stock and edited layout JSON
  of every package the edits reach, for `validate_layout.py`), `build.json`, and `TEST_INSTRUCTIONS.md` (like
  `work/out/mods/CANARY_C6.md`): the files and hashes, the edits and their bytes, the steps, every affected screen
  (online ones marked "predicted only"), the predicted rects, the checking commands, and the warning to remove the
  mod before playing online.

## Making a mod without the UI: `umgmod`
`app/tools/UmgEditor.ModCli/bin/Release/net10.0/umgmod.exe`:

```
umgmod [--set NAME] edit  <package> <widget> set <field> <value> [--yes]
umgmod [--set NAME] edit  <package> <widget> move <dx> <dy> [--yes]
umgmod [--set NAME] edit  <package> <widget> resize <Left|Top|Right|Bottom> <d> [--yes]
umgmod [--set NAME] show  <package>
umgmod [--set NAME] reset <package>
umgmod [--set NAME] build <modName> <package>... [--container pc9999_mobile_and_P]
umgmod [--set NAME] predict <package>...
```

- `edit` applies one SlotEditor edit to the package's PatchSession and saves it (`work\edits[\NAME]\<package path>`).
  The first edit of a package prints the pre-edit warnings and stops (exit 3); add `--yes` once you've read them.
- `show` prints the saved edits and the bytes to patch (offset, old/new value, hex, ±32-byte context).
- `build` packs the saved chunks of the given packages into `work\out\mods\<modName>\mod\<container>.{utoc,ucas,pak}`.
  The steps are the same as `work/scripts/make_mod.py` (kept as the reference), followed by `retoc verify` and a
  CUE4Parse read-back. A failed step removes `mod\` and writes `FAILED.txt`. Nothing under PaksDir is written or
  deleted.
- `predict` arranges, stock vs edited at 1786×1000, every package the saved edits reach: the edited packages plus
  every package that embeds them (e.g. MatchTimeBase → the goal/card/substitution pop-ups and the online board). It
  prints every box that changes and writes `work\out\mods\_predictions\<set>\<Package>[.stock].json` for
  `validate_layout.py`.
- `--set NAME` keeps independent edits of the same package apart (e.g. one per canary).
- `<package>` is a `/Game/...` path or a unique suffix (`MatchTime/MatchTimeBase`). `<field>` may omit
  `LayoutData.` (`Offsets.Right`).
