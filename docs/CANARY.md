# Canary mods: prove a widget edit reaches the game

A canary is a deliberately obvious edit to one **stored**, **non-animated** slot value. If it shows in game, the package is mounted and the value is live. Proven twice on 2026-10-19 (LDPlayer): `work/out/canary` (score numbers vanished, clipped) and `work/out/canary2` (numbers moved +120 units ≈ +115 px).

All commands run from `E:\n3tron\ai_tools\scoreboard\work` in Git Bash. Nothing writes to the stock Paks folder.

```sh
R=/e/n3tron/patch/Tools/retoc-x86_64-pc-windows-msvc/retoc.exe
K=$(grep -oE '0x[0-9A-Fa-f]{64}' "$APPDATA/UmgEditor/umgeditor.json")  # the AES key, never stored in the repo
P="E:/n3tron/patch/obb/mobile/org/all/PesMobile/Content/Paks"
```

## 1. Pick the target (in the editor)
Open the package and select the element. Use one where:
- the slot field you'll change is **black / "stored"** (grey fields can't be patched in place);
- the row has **no `anim`/`moves` badge** (otherwise an animation may overwrite your value);
- you know what a clipping parent (`clip` badge) does to it: moved outside a clipping panel, it **disappears** instead of moving.

Note: the slot name (e.g. `CanvasPanelSlot_29`), the content widget name (`Mask`), the container (`pc1000_mobile_and.utoc`), and the package path.

## 2. Get the stock bytes (once per container)
```sh
# raw chunks + manifest (the patch input and the container header)
$R -a $K --override-container-header-version PreInitial unpack-raw "$P/pc1000_mobile_and.utoc" extract/unpack_pc1000_mobile_and
# legacy copy of the package folder (UAssetAPI uses it to find the exact float offsets)
$R -a $K --override-container-header-version PreInitial to-legacy --version UE4_26 --no-shaders \
   -f "SwitchAssets_Original/MatchTime/" "$P" extract/legacy
# chunk id of the package (ExportBundleData row) and the container header chunk (ContainerHeader row)
$R -a $K --override-container-header-version PreInitial list --path "$P/pc1000_mobile_and.utoc" | grep -E "MatchTime/MatchTime.uasset|ContainerHeader"
```

## 3. Patch (refuses unless every rule holds)
```sh
mkdir -p out/mycanary
scripts/Canary/bin/Release/net10.0/Canary.exe \
  extract/legacy/PesMobile/Content/Assets/ui/Data/Widget/TVGraphics/SwitchAssets_Original/MatchTime/MatchTime.uasset \
  extract/unpack_pc1000_mobile_and/chunks/4ea5d5c18de5157900000002 \
  out/mycanary/4ea5d5c18de5157900000002 \
  CanvasPanelSlot_29 Mask Left=249
```
It prints `OK` only if the field is stored, its ±32-byte context matches **exactly once** in the Zen chunk, the size is unchanged and only the expected bytes differ. Otherwise it prints `REFUSED: …` and writes nothing.

## 4. Package
```sh
python scripts/make_mod.py --chunk out/mycanary/4ea5d5c18de5157900000002 \
  --path "../../../PesMobile/Content/Assets/ui/Data/Widget/TVGraphics/SwitchAssets_Original/MatchTime/MatchTime.uasset" \
  --source-header extract/unpack_pc1000_mobile_and/chunks/6473168f3157cb940000000a \
  --stub "$P/pc0100_mobile_and.pak" --out out/mycanary/pkg
```
Expect `verified` plus three sha256 lines. Output: `out/mycanary/pkg/mod/pc9999_mobile_and_P.{utoc,ucas,pak}`.

Optional check on the PC (CUE4Parse reads the package from the mod container, on top of stock):
```sh
RECON_EXTRA="$(cygpath -w out/mycanary/pkg/mod)" scripts/Recon/bin/Release/net10.0/Recon.exe slot \
  PesMobile/Content/Assets/ui/Data/Widget/TVGraphics/SwitchAssets_Original/MatchTime/MatchTime.uasset Mask
# → "served from pc9999_mobile_and_P.utoc" and the new value
```

## 5. Test on the device
1. Force-stop eFootball.
2. Copy the three `pc9999_mobile_and_P.*` files into the folder that holds `pc0010_mobile_and.utoc/.ucas/.pak` (overwrite older canary files with the same names). Don't touch anything else.
3. Start the game. If it offers a download or repair, note it; it may delete unknown files.
4. Open a screen that shows the widget: the offline scoreboard (`MatchTime`) needs an **offline** match vs AI; the online one is `MatchTimeConnection`.
5. Screenshot it next to a stock screenshot of the same moment.

| Result | Meaning |
|---|---|
| Element moved by the expected amount (≈ 0.93 px per unit at 1786 px game width) | Live. Pipeline OK |
| Element vanished | Live, but moved outside a clipping parent |
| Looks stock | Not mounted: check folder, file names, that a repair didn't delete them |
| Black screen / crash | Header or package rejected: delete the three files, report when it happened |

## 6. Remove
Delete the three `pc9999_mobile_and_P.*` files. Do this before playing online.
