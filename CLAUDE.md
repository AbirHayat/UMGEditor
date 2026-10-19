# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## What this workspace is
An eFootball modding workspace (scoreboard mod). eFootball runs on UE 4.26/4.27 with Zen Loader/IoStore: `.utoc` is the index, `.ucas` holds the ZLib + AES-256 data, and a legacy `.pak` acts as the mount stub. `tutorial.txt` is the authoritative field guide. Read it before any asset, container or exe work. `tools/` holds upstream clones of third-party tools. The workspace root is not a git repo, but each tool is its own git repo.

- `tools/retoc`: Rust workspace (`retoc` lib, `retoc_cli`, `load_logger`), the Zen ↔ Legacy converter and IoStore packer.
- `tools/UAssetAPI`: C# (.NET 10) library for reading and writing legacy .uasset files, with MSTest tests.
- `tools/UAssetGUI`: WinForms GUI on top of UAssetAPI. Its `UAssetAPI` git submodule is not initialized (`git submodule update --init` inside UAssetGUI).

## Working rules (from the user)
- **Never run calls that download files or binaries; ask first.** That includes library helpers that fetch at runtime (e.g. CUE4Parse `ZlibHelper.Initialize`/`DownloadDll`, `OodleHelper` downloads), `git clone`, `winget`, and NuGet restores that add new packages.
- Ask before adding any dependency (NuGet package, native DLL, tool).
- Never modify stock files; work on copies, and verify every edit by reloading and diffing.
- Patches: change stored values only, never `to-zen` a Blueprint, refuse a patch unless its ±32-byte context matches exactly once, check size + byte diff, run `retoc verify`.

## UMG editor app (`app/`, eFootball **Mobile**, net10.0)
- Projects: `src/UmgEditor.Core` (CUE4Parse-based, read-only access to stock containers), `src/UmgEditor.App` (WPF), `tools/UmgEditor.Smoke` (console checks against the stock containers; there's no test framework yet).
- Build: `dotnet build app/UmgEditor.slnx -c Release`. CUE4Parse is a project reference with `CUE4PARSE_SKIP_NATIVE=true`; don't add it to the solution (it would trigger the native CMake build).
- Verify: `app/tools/UmgEditor.Smoke/bin/Release/net10.0/UmgEditor.Smoke.exe [--rebuild]`. It exits 0 when every check passes.
- Config: `umgeditor.json` next to the exe (PaksDir, AesKey, WorkDir); defaults point at `E:\n3tron\patch\obb\mobile\...\Paks` and `work/`. The index cache is `work/cache/ui_index_v*_<fingerprint>.json`.
- Mobile facts (see `work/recon/REPORT.md`): tagged properties (no .usmap needed), `EGame.GAME_eFootball`, no native zlib needed, Ordinal path comparer (`ui/library/core` and `ui/library/Core` both exist). Edits = in-place Zen byte patch + `retoc pack-raw` + hand-built header (`work/scripts/hdr.py`); proven in game by two canaries.

## Commands
retoc (run from `tools/retoc`):
- Build: `cargo build --release` → `target/release/retoc.exe`. Add an AV exclusion for the folder first, because fresh builds get quarantined ("Access denied").
- Tests: `cargo test -p retoc`; one test: `cargo test -p retoc <name>`. Fixtures live in `retoc/tests/<UE version>/`.
- Usage: `retoc --aes-key <KEY> [--override-container-header-version PreInitial] <to-legacy|to-zen|unpack|list|info|verify|...>`. The AES key is in `tutorial.txt`. Mod containers must be TOC v2 (DirectoryIndex), generated natively with the override flags. Never hand-patch them.

UAssetAPI / UAssetGUI:
- Build: `dotnet build tools/UAssetAPI/UAssetAPI.sln` (or `UAssetGUI.sln`).
- Tests: `dotnet test tools/UAssetAPI/UAssetAPI.Tests`; one test: `dotnet test tools/UAssetAPI/UAssetAPI.Tests --filter "FullyQualifiedName~<TestName>"`.

## retoc state vs. tutorial
- The UE4.26 raw-exports patch IS applied in source: the `else` branch of `serialize_zen_asset()` in `retoc/src/zen_asset_conversion.rs` now writes the export buffer as-is (`write_all`) instead of calling `write_exports_in_bundle_order(...)`, which is now unused and causes a `dead_code` warning. Do not revert it, or every to-zen texture crashes in game, even an unmodified stock round-trip. It has not been built or tested with a stock round-trip yet.
- `copy-packages` / `--patch` (which the tutorial requires for Blueprints and Niagara) do not exist in this retoc. Don't assume them. They have to be implemented, or the work goes around them.

## Hard rules from tutorial.txt (summary)
- Edit cooked assets in place at the byte level (same size). Never save shipped assets with UAssetGUI or any re-serializer, because the game silently refuses them. Verify every offset against pristine stock bytes, print file size + offset + bounds proof, compute floats as IEEE754 LE, and disambiguate with ≥32 bytes of context.
- Renames must keep the same length and be patched in the `.uasset` name map/path strings. Check magic `C1 83 2A 9E` and an unchanged file size.
- Textures: never ship altered pixels under a stock package ID (crash at texture load). Rename to a new same-length package and re-point the consumer's `.uasset`. Encode with `texconv -f BC1_UNORM`.
- Never `to-zen` a Blueprint or Niagara asset.
- Mod containers: an extra `_P.utoc/.ucas` plus a companion `.pak` stub with the same name (binary-copied from a working mod), with a valid header (own container_id, non-zero AES GUID, partition_size `0xFFFFFFFFFFFFFFFF`, TOC v2, header 0x90, truthful flags). A legacy `.pak` cannot override cooked `.uasset` files.
- Keep a pristine stock copy, change one variable at a time, validate against stock, and use `getall <Class_C> <Prop>` and canary tests to confirm the mod loaded.
