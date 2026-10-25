// Phase 1 recon for the eFootball Mobile UMG editor.
// Reads the stock containers read-only via CUE4Parse; writes only under work/.
using System.Text;
using CUE4Parse.Encryption.Aes;
using CUE4Parse.FileProvider;
using CUE4Parse.MappingsProvider.Usmap;
using CUE4Parse.UE4.Assets;
using CUE4Parse.UE4.Assets.Exports.Texture;
using CUE4Parse.UE4.Objects.Core.Misc;
using CUE4Parse.UE4.Versions;
using CUE4Parse_Conversion.Textures;
using CUE4Parse_Conversion.Options;
using Newtonsoft.Json;

const string PaksDir = @"E:\n3tron\patch\obb\mobile\org\all\PesMobile\Content\Paks";
const string WidgetRoot = "PesMobile/Content/Assets/ui/Data/Widget/";
var workDir = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, @"..\..\..\..\..\"));
var reconDir = Path.Combine(workDir, "recon");

if (args.Length == 0)
{
    Console.WriteLine("usage: Recon survey|graph|dump <pkg>|tex <pkg> [--usmap file]");
    return 1;
}

// the AES key is never in the repo: it is "AesKey" in %APPDATA%\UmgEditor\umgeditor.json (as for the editor)
var settingsPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "UmgEditor", "umgeditor.json");
var AesKey = File.Exists(settingsPath) ? Newtonsoft.Json.Linq.JObject.Parse(File.ReadAllText(settingsPath))["AesKey"]?.ToString() : null;
if (string.IsNullOrWhiteSpace(AesKey))
{
    Console.WriteLine($"No AES key: put \"AesKey\" in {settingsPath}");
    return 1;
}

var extraDir = Environment.GetEnvironmentVariable("RECON_EXTRA"); // optional mod folder mounted on top of stock
var provider = extraDir == null
    ? new DefaultFileProvider(PaksDir, SearchOption.TopDirectoryOnly, false, new VersionContainer(EGame.GAME_eFootball))
    : new DefaultFileProvider(new DirectoryInfo(PaksDir), [new DirectoryInfo(extraDir)], SearchOption.TopDirectoryOnly, false, new VersionContainer(EGame.GAME_eFootball));
provider.Initialize();
provider.SubmitKey(new FGuid(), new FAesKey(AesKey));
var usmapIdx = Array.IndexOf(args, "--usmap");
if (usmapIdx >= 0) provider.MappingsContainer = new FileUsmapTypeMappingsProvider(args[usmapIdx + 1]);
TextureDecoder.UseAssetRipperTextureDecoder = true;
Console.WriteLine($"Mounted {provider.MountedVfs.Count} containers, {provider.Files.Count} files, mappings: {(provider.MappingsForGame != null ? "yes" : "none")}");

IEnumerable<string> WidgetPackages(string prefix) => provider.Files.Keys
    .Where(k => k.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) && k.EndsWith(".uasset", StringComparison.OrdinalIgnoreCase))
    .OrderBy(k => k, StringComparer.OrdinalIgnoreCase);

string ClassOf(IoPackage p, int i)
{
    try { return p.ResolveObjectIndex(p.ExportMap[i].ClassIndex)?.GetPathName() ?? "?"; }
    catch (Exception e) { return "?" + e.GetType().Name; }
}

List<string> Imports(IoPackage p)
{
    var list = new List<string>();
    foreach (var idx in p.ImportMap)
    {
        if (idx.IsNull) continue;
        try { list.Add(p.ResolveObjectIndex(idx)?.GetPathName() ?? $"<unresolved {idx}>"); }
        catch (Exception e) { list.Add($"<error {e.GetType().Name}>"); }
    }
    return list;
}

static string PackageOf(string objectPath)
{
    var dot = objectPath.IndexOf('.');
    return dot < 0 ? objectPath : objectPath[..dot];
}

string ContainerOf(string key) => provider.Files.TryGetValue(key, out var f) && f is CUE4Parse.UE4.VirtualFileSystem.VfsEntry v ? v.Vfs.Name : "?";

switch (args[0])
{
    case "slot":
    {
        // print which container serves the package and the LayoutData of the CanvasPanelSlot holding <content>
        var key = args[1];
        Console.WriteLine($"{key} served from {ContainerOf(key)}");
        foreach (var obj in provider.LoadPackage(key).GetExports().Where(o => o.ExportType == "CanvasPanelSlot"))
        {
            var json = JsonConvert.SerializeObject(obj);
            if (!json.Contains($".{args[2]}'\"")) continue;
            Console.WriteLine($"{obj.Name}: {JsonConvert.SerializeObject(Newtonsoft.Json.Linq.JObject.Parse(json)["Properties"]!["LayoutData"])}");
        }
        break;
    }
    case "find":
    {
        foreach (var k in provider.Files.Keys.Where(k => k.Contains(args[1], StringComparison.OrdinalIgnoreCase)))
            Console.WriteLine($"{ContainerOf(k)}  {k}");
        break;
    }
    case "survey":
    {
        // export classes + parse status across every widget package (property parse attempted on each export)
        var classCounts = new Dictionary<string, int>();
        var errorCounts = new Dictionary<string, int>();
        var failedPackages = new List<string>();
        var rows = new StringBuilder("container,path,exports,widgetClass,propertyErrors\n");
        int n = 0, okProps = 0, total = 0;
        foreach (var key in WidgetPackages(WidgetRoot))
        {
            n++;
            if (!provider.TryLoadPackage(key, out var pkg) || pkg is not IoPackage p) { failedPackages.Add(key); continue; }
            var main = "";
            int errs = 0;
            for (int i = 0; i < p.ExportMap.Length; i++)
            {
                var cls = ClassOf(p, i);
                classCounts[cls] = classCounts.GetValueOrDefault(cls) + 1;
                if (cls.EndsWith("WidgetBlueprintGeneratedClass")) main = cls;
                total++;
                try { _ = p.ExportsLazy[i].Value; okProps++; }
                catch (Exception e)
                {
                    errs++;
                    var msg = e.Message.Length > 120 ? e.Message[..120] : e.Message;
                    errorCounts[msg] = errorCounts.GetValueOrDefault(msg) + 1;
                }
            }
            rows.Append($"{ContainerOf(key)},{key},{p.ExportMap.Length},{main},{errs}\n");
            if (n % 500 == 0) Console.WriteLine($"  {n} packages...");
        }
        Directory.CreateDirectory(reconDir);
        File.WriteAllText(Path.Combine(reconDir, "widgets.csv"), rows.ToString());
        File.WriteAllText(Path.Combine(reconDir, "survey.json"), JsonConvert.SerializeObject(new
        {
            packages = n, failedToOpen = failedPackages, exports = total, exportsParsed = okProps,
            classes = classCounts.OrderByDescending(kv => kv.Value).ToDictionary(),
            errors = errorCounts.OrderByDescending(kv => kv.Value).Take(40).ToDictionary()
        }, Formatting.Indented));
        Console.WriteLine($"packages {n}, open failures {failedPackages.Count}, exports {total}, parsed {okProps}");
        break;
    }
    case "graph":
    {
        // package-level dependency graph for TVGraphics: widget -> imported packages (outside /Script)
        var prefix = WidgetRoot + "TVGraphics/";
        var graph = new SortedDictionary<string, object>();
        foreach (var key in WidgetPackages(prefix))
        {
            if (!provider.TryLoadPackage(key, out var pkg) || pkg is not IoPackage p) continue;
            var deps = Imports(p).Select(PackageOf).Where(x => !x.StartsWith("/Script/")).Distinct().OrderBy(x => x).ToList();
            var scripts = Imports(p).Select(PackageOf).Where(x => x.StartsWith("/Script/")).Distinct().OrderBy(x => x).ToList();
            graph[p.Name] = new { container = ContainerOf(key), dependsOn = deps, scriptModules = scripts };
        }
        Directory.CreateDirectory(reconDir);
        File.WriteAllText(Path.Combine(reconDir, "tvgraphics_graph.json"), JsonConvert.SerializeObject(graph, Formatting.Indented));
        Console.WriteLine($"{graph.Count} TVGraphics packages written");
        break;
    }
    case "dump":
    {
        var key = args[1];
        var p = (IoPackage) provider.LoadPackage(key);
        var sb = new StringBuilder();
        sb.AppendLine($"# {key}  (container {ContainerOf(key)})");
        sb.AppendLine($"PackageFlags: {p.Summary.PackageFlags}");
        sb.AppendLine($"\n## NameMap ({p.NameMap.Length})");
        for (int i = 0; i < p.NameMap.Length; i++) sb.AppendLine($"{i,4} {p.NameMap[i].Name}");
        sb.AppendLine("\n## Imports");
        foreach (var imp in Imports(p)) sb.AppendLine("  " + imp);
        sb.AppendLine("\n## Exports (index, name, class, outer, cookedOffset, size, parse)");
        var objs = new List<object>();
        for (int i = 0; i < p.ExportMap.Length; i++)
        {
            var e = p.ExportMap[i];
            var name = p.CreateFNameFromMappedName(e.ObjectName).Text;
            string outer;
            try { outer = e.OuterIndex.IsNull ? "-" : p.ResolveObjectIndex(e.OuterIndex)?.Name.Text ?? "?"; } catch { outer = "?"; }
            string status;
            try { objs.Add(p.ExportsLazy[i].Value); status = "ok"; }
            catch (Exception ex) { status = "FAIL: " + ex.Message; }
            sb.AppendLine($"{i,4} {name} | {ClassOf(p, i)} | outer={outer} | off={e.CookedSerialOffset} size={e.CookedSerialSize} | {status}");
        }
        var outDir = Path.Combine(reconDir, "dumps");
        Directory.CreateDirectory(outDir);
        var baseName = Path.GetFileNameWithoutExtension(key);
        File.WriteAllText(Path.Combine(outDir, baseName + ".txt"), sb.ToString());
        File.WriteAllText(Path.Combine(outDir, baseName + ".json"), JsonConvert.SerializeObject(objs, Formatting.Indented));
        // raw zen package bytes, for the later in-place patch path
        var rawDir = Path.Combine(workDir, "extract", "raw", Path.GetDirectoryName(key)!);
        Directory.CreateDirectory(rawDir);
        if (provider.TrySaveAsset(key, out var bytes)) File.WriteAllBytes(Path.Combine(rawDir, baseName + ".uasset"), bytes);
        Console.WriteLine(sb.ToString().Split('\n').Where(l => l.Contains("FAIL") || l.StartsWith("#")).Take(30).Aggregate("", (a, l) => a + l + "\n"));
        Console.WriteLine($"exports {p.ExportMap.Length}, parsed {objs.Count}; written to {outDir}");
        break;
    }
    case "tex":
    {
        var key = args[1];
        var p = provider.LoadPackage(key);
        var tex = p.GetExports().OfType<UTexture2D>().First();
        var mip = tex.GetFirstMip();
        Console.WriteLine($"{tex.Name}: format {tex.Format}, {tex.PlatformData.SizeX}x{tex.PlatformData.SizeY}, mips {tex.PlatformData.Mips.Length}, " +
                          $"mip0 bulk flags {mip?.BulkData.Header.BulkDataFlags}, bulk size {mip?.BulkData.Header.SizeOnDisk}");
        var decoded = tex.Decode() ?? throw new Exception("decode returned null");
        var png = decoded.Encode(ETextureFormat.Png, false, out var ext);
        var outDir = Path.Combine(reconDir, "tex_test");
        Directory.CreateDirectory(outDir);
        var outPath = Path.Combine(outDir, tex.Name + "." + ext);
        File.WriteAllBytes(outPath, png);
        Console.WriteLine($"wrote {outPath} ({png.Length} bytes)");
        break;
    }
}
return 0;
