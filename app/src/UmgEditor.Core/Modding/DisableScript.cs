using System.Security.Cryptography;
using System.Text;
using UmgEditor.Core.Patching;

namespace UmgEditor.Core.Modding;

/// <summary>
/// Stage 2f: DISABLE.ps1 next to a built mod (work\out\mods\&lt;name&gt;\DISABLE.ps1). Files reach the device by hand, so
/// the script never touches the phone. It carries the mod's files (name, size, sha256: read from mod\, and when a
/// recipe.json with Expect.Files sits next to it, required to be exactly those pins), the stock file names and the
/// stock Paks folder. Without -Dir it prints the files to delete on the device. With -Dir it moves only files whose name
/// AND sha256 match into &lt;Dir&gt;\_disabled\&lt;name&gt;\ (dry run unless -Apply; -Restore moves them back): it never
/// deletes, never overwrites, never moves a file with a stock name, and refuses the stock Paks folder itself.
/// </summary>
public static class DisableScript
{
    public const string FileName = "DISABLE.ps1";

    /// <summary>Name prefixes of stock mobile paks and containers, refused even when the stock folder isn't there to list.</summary>
    public static readonly string[] StockPrefixes = ["pc0010", "pc0100", "pc7000", "global.", "pakchunk"];

    /// <summary>The files in a built mod's mod\ folder, checked against its recipe.json pins when there is one.</summary>
    public static (List<ModFile>? Files, string? Why) FilesOf(string outDir)
    {
        var modDir = Path.Combine(outDir, "mod");
        if (!Directory.Exists(modDir)) return (null, $"{modDir} doesn't exist (build the mod first)");
        var files = new DirectoryInfo(modDir).GetFiles().OrderBy(f => f.Name, StringComparer.Ordinal)
            .Select(f => new ModFile(f.Name, f.Length, Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(f.FullName))).ToLowerInvariant())).ToList();
        if (files.Count == 0) return (null, $"{modDir} is empty");
        if (Directory.GetDirectories(modDir).Length > 0) return (null, $"{modDir} holds folders; a mod is only files");
        var recipePath = Path.Combine(outDir, Recipes.RecipeBuild.RecipeCopy);
        if (File.Exists(recipePath))
        {
            var (recipe, bad) = Recipes.RecipeReader.Read(File.ReadAllText(recipePath));
            if (recipe == null) return (null, $"{recipePath} doesn't read: {string.Join("; ", bad)}");
            if (recipe.Expect?.Files is { } pinned)
            {
                var built = files.Select(f => $"{f.Name} {f.Sha256}").Order(StringComparer.Ordinal);
                var pins = pinned.Select(kv => $"{kv.Key} {kv.Value}").Order(StringComparer.Ordinal);
                if (!built.SequenceEqual(pins)) return (null, $"the files in {modDir} aren't the ones {recipePath} pins (Expect.Files); rebuild the mod");
            }
        }
        return (files, null);
    }

    /// <summary>Why a mod file may not be listed (a stock name), or null.</summary>
    public static string? StockRefusal(string name, IReadOnlyCollection<string> stockNames) =>
        stockNames.Contains(name, StringComparer.OrdinalIgnoreCase) || StockPrefixes.Any(p => name.StartsWith(p, StringComparison.OrdinalIgnoreCase))
            ? $"{name} is a stock file name; a disable script never lists one" : null;

    /// <summary>The by-hand checklist: what to delete in the phone's Paks folder (the folder with pc0010_mobile_and.*).</summary>
    public static string Checklist(string modName, IReadOnlyList<ModFile> files)
    {
        var o = new StringBuilder();
        o.AppendLine($"Mod {modName}: delete these {files.Count} file(s) from the phone's Paks folder (the one with pc0010_mobile_and.*),");
        o.AppendLine("only when name AND size match (a file of another mod can share a name; check sha256 if in doubt):");
        foreach (var f in files) o.AppendLine($"  {f.Name}  {f.Size:N0} bytes  sha256 {f.Sha256}");
        return o.ToString();
    }

    /// <summary>The script's text (ASCII only: PowerShell 5.1 reads a file without BOM as ANSI), or why not.</summary>
    public static (string? Script, string? Why) Make(string modName, IReadOnlyList<ModFile> files, IReadOnlyCollection<string> stockNames, string stockPaksDir, DateTime when)
    {
        if (ModBuilder.ModNameRefusal(modName) is { } badName) return (null, badName);
        if (files.Count == 0) return (null, "no files");
        foreach (var f in files)
        {
            if (StockRefusal(f.Name, stockNames) is { } stock) return (null, stock);
            if (f.Name.IndexOfAny(['/', '\\', ':', '\'', '"']) >= 0 || f.Name is "." or ".." || f.Name.Any(c => c > 126 || c < 32)) return (null, $"{f.Name}: not a plain ASCII file name");
            if (f.Sha256.Length != 64 || !f.Sha256.All(Uri.IsHexDigit)) return (null, $"{f.Name}: sha256 {f.Sha256}");
        }
        if (stockNames.Concat([stockPaksDir]).Any(s => s.Any(c => c > 126 || c < 32))) return (null, "a stock name or the stock folder isn't plain ASCII");
        static string Q(string s) => "'" + s.Replace("'", "''") + "'";

        var o = new StringBuilder();
        void L(string line = "") => o.Append(line).Append("\r\n");
        L($"# DISABLE.ps1 for mod {modName}, written by umgmod on {when:yyyy-MM-dd HH:mm}. Files reach the device by hand, so this never");
        L("# touches the phone. It moves only this mod's files (name AND sha256 must match) into <Dir>\\_disabled\\" + modName + "\\ and back;");
        L("# it never deletes or overwrites anything, never moves a file with a stock name, and refuses the stock Paks folder.");
        L("#   .\\DISABLE.ps1                               the files to delete on the phone by hand");
        L("#   .\\DISABLE.ps1 -Dir <folder>                 dry run: what would move");
        L("#   .\\DISABLE.ps1 -Dir <folder> -Apply          move them to <folder>\\_disabled\\" + modName);
        L("#   .\\DISABLE.ps1 -Dir <folder> -Restore [-Apply]  move them back");
        L("# Exit codes: 0 done (or nothing to do), 1 a file was left alone because its target exists, 2 refused.");
        L("param([string]$Dir, [switch]$Apply, [switch]$Restore)");
        L("$ErrorActionPreference = 'Stop'");
        L($"$Mod = {Q(modName)}");
        L("$Files = @(");
        for (int i = 0; i < files.Count; i++)
            L($"    [pscustomobject]@{{ Name = {Q(files[i].Name)}; Size = {files[i].Size}; Sha256 = {Q(files[i].Sha256)} }}{(i < files.Count - 1 ? "," : "")}");
        L(")");
        L($"$StockNames = @({string.Join(", ", stockNames.Order(StringComparer.OrdinalIgnoreCase).Select(Q))})");
        L($"$StockPrefixes = @({string.Join(", ", StockPrefixes.Select(Q))})");
        L($"$StockPaks = {Q(stockPaksDir.TrimEnd('\\', '/'))}");
        L();
        L("function Test-Stock([string]$name) {");
        L("    if ($StockNames -icontains $name) { return $true }");
        L("    foreach ($p in $StockPrefixes) { if ($name.StartsWith($p, [System.StringComparison]::OrdinalIgnoreCase)) { return $true } }");
        L("    return $false");
        L("}");
        L("foreach ($f in $Files) { if (Test-Stock $f.Name) { Write-Output \"REFUSED: $($f.Name) is a stock file name\"; exit 2 } }");
        L();
        L("if (-not $Dir) {");
        L("    if ($Restore -or $Apply) { Write-Output 'REFUSED: -Apply and -Restore need -Dir <folder>'; exit 2 }");
        L("    Write-Output \"Mod ${Mod}: delete these $($Files.Count) file(s) from the phone's Paks folder (the one with pc0010_mobile_and.*),\"");
        L("    Write-Output 'only when name AND size match (a file of another mod can share a name; check sha256 if in doubt):'");
        L("    foreach ($f in $Files) { Write-Output (\"  {0}  {1:N0} bytes  sha256 {2}\" -f $f.Name, $f.Size, $f.Sha256) }");
        L("    Write-Output 'To move them out of a folder on this PC instead: .\\DISABLE.ps1 -Dir <folder> (dry run), then add -Apply.'");
        L("    exit 0");
        L("}");
        L("if (-not (Test-Path -LiteralPath $Dir -PathType Container)) { Write-Output \"REFUSED: $Dir is not a folder\"; exit 2 }");
        L("$full = (Resolve-Path -LiteralPath $Dir).ProviderPath.TrimEnd('\\')");
        L("if ($StockPaks -and ($full -ieq $StockPaks -or $full.StartsWith($StockPaks + '\\', [System.StringComparison]::OrdinalIgnoreCase))) {");
        L("    Write-Output \"REFUSED: $full is the stock Paks folder (or inside it)\"; exit 2");
        L("}");
        L("$parked = Join-Path $full (Join-Path '_disabled' $Mod)");
        L("if ($Restore) { $from = $parked; $to = $full } else { $from = $full; $to = $parked }");
        L("# files sharing a base name (a container: .pak stub + .ucas + .utoc) move only together: every mod built into the");
        L("# same container name has the same stub, so a stub is never moved away from another mod's .ucas/.utoc");
        L("$moves = @(); $blocked = 0");
        L("foreach ($group in ($Files | Group-Object { [System.IO.Path]::GetFileNameWithoutExtension($_.Name) })) {");
        L("    $found = @(); $absent = @(); $other = @()");
        L("    foreach ($f in $group.Group) {");
        L("        $src = Join-Path $from $f.Name");
        L("        if (-not (Test-Path -LiteralPath $src -PathType Leaf)) { $absent += $f.Name; continue }");
        L("        $sha = (Get-FileHash -LiteralPath $src -Algorithm SHA256).Hash.ToLowerInvariant()");
        L("        if ($sha -ne $f.Sha256) { $other += \"$($f.Name) (sha256 $sha, not this mod's $($f.Sha256))\"; continue }");
        L("        $found += [pscustomobject]@{ Name = $f.Name; Src = $src; Dst = (Join-Path $to $f.Name) }");
        L("    }");
        L("    $names = ($group.Group | ForEach-Object { $_.Name }) -join ', '");
        L("    if ($found.Count -eq 0 -and $other.Count -eq 0) { Write-Output \"  not there: $names\"; continue }");
        L("    if ($other.Count -gt 0 -or $absent.Count -gt 0) {");
        L("        Write-Output \"  left alone: $names (they move only together; not this mod's: $($other -join '; ')$(if ($absent.Count -gt 0) { \"; missing: $($absent -join ', ')\" }))\"");
        L("        continue");
        L("    }");
        L("    $taken = @($found | Where-Object { Test-Path -LiteralPath $_.Dst })");
        L("    if ($taken.Count -gt 0) { Write-Output \"  left alone: $names, because $(($taken | ForEach-Object { $_.Dst }) -join ', ') already exists\"; $blocked++; continue }");
        L("    $moves += $found");
        L("}");
        L("$verb = if ($Restore) { 'restore' } else { 'disable' }");
        L("if (-not $Apply) {");
        L("    foreach ($m in $moves) { Write-Output \"  would move $($m.Src) -> $($m.Dst)\" }");
        L("    Write-Output \"Dry run ($verb): $($moves.Count) file(s) would move. Nothing was changed; add -Apply to move them.\"");
        L("    if ($blocked -gt 0) { exit 1 } else { exit 0 }");
        L("}");
        L("if ($moves.Count -gt 0 -and -not (Test-Path -LiteralPath $to)) { New-Item -ItemType Directory -Path $to | Out-Null }");
        L("foreach ($m in $moves) { Move-Item -LiteralPath $m.Src -Destination $m.Dst; Write-Output \"  moved $($m.Src) -> $($m.Dst)\" }");
        L("Write-Output \"$verb`: moved $($moves.Count) file(s).\"");
        L("if (-not $Restore -and $moves.Count -gt 0) { Write-Output \"Restore with: & '$($MyInvocation.MyCommand.Path)' -Dir '$full' -Restore -Apply\" }");
        L("if ($blocked -gt 0) { exit 1 } else { exit 0 }");
        return (o.ToString(), null);
    }

    /// <summary>Writes DISABLE.ps1 for the mod in <paramref name="outDir"/> (under work\out\mods), or says why not.</summary>
    public static (string? Path, IReadOnlyList<ModFile>? Files, string? Why) Write(GameConfig config, string outDir, DateTime when)
    {
        var mods = WriteGuard.ForMods(config);
        if (mods.Refusal(outDir) is { } outside) return (null, null, outside);
        var (files, why) = FilesOf(outDir);
        if (files == null) return (null, null, why);
        var stockNames = Directory.Exists(config.PaksDir) ? Directory.GetFiles(config.PaksDir).Select(Path.GetFileName).OfType<string>().ToList() : [];
        var (script, bad) = Make(Path.GetFileName(outDir.TrimEnd('\\', '/')), files, stockNames, Path.GetFullPath(config.PaksDir), when);
        if (script == null) return (null, null, bad);
        var path = Path.Combine(outDir, FileName);
        mods.WriteAllText(path, script);
        return (path, files, null);
    }
}
