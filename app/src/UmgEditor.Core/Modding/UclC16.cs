using CUE4Parse.UE4.Assets.Exports.Texture;
using UmgEditor.Core.Patching;

namespace UmgEditor.Core.Modding;

/// <summary>
/// Canary C16 (branch experiment): the complete UCL look on mobile, as packages ready for ModBuilder. Three groups, so a
/// failure can be isolated (each group builds alone):
/// - board: MatchTime (edit set + the bar's and the logo's texture paths, scores 87 → 86, Normal_Display's Clipping
///   removed), the two ColorTables (STR_SWITCH_BRAND_1 → white, _2 → black), the yellow and pink plate widgets
///   re-pointed to white copies, and the new textures (the bar art without the watermark, the UCL ball logo, white
///   plates);
/// - banners: MatchTimeGoal (edit set, the player name 87 → 86, its plate MatchTimeBase_1 → class DemoTitleBase showing
///   the goal art), MatchTimeCard and MatchTimeMemberChange (edit sets), MatchTimeBase → the navy gradient (margins 0),
///   DemoTitleBase → the goal art (margins 0), and those textures;
/// - replay: DemoTitle's plate → class MatchTimeBase (the navy gradient), plus MatchTimeBase and its texture.
/// Every package is built in layers, each verified by its own tool, then <see cref="LayerUnion.Check"/> against stock.
/// The art comes from work/out/art/C16 (make_bar_texture.py art, make_gradient.py, image_to_bgra.py).
/// C16b (<c>centreNames</c>): the goal scorer's and the card player's names centred, as UCL has them: two more layers on
/// MatchTimeGoal and MatchTimeCard, the names a Justification tag needs appended to the name map
/// (<see cref="EnumTagInsert.AppendNames"/>), then Justification = ETextJustify::Center inserted on the text
/// (<see cref="EnumTagInsert.Insert"/>, its builder first checked against stock's own tag).
/// </summary>
public static class UclC16
{
    public const string Tv = "/Game/Assets/ui/Data/Widget/TVGraphics/SwitchAssets_Original/";
    public const string MT = Tv + "MatchTime/", DT = Tv + "DemoTitle/";
    public static readonly string[] AllGroups = ["board", "banners", "replay"];
    private const string Dark = "ColorEnum_String::NewEnumerator87", White = "ColorEnum_String::NewEnumerator86";
    public const string LogoSource = "/Game/Assets/ui/Data/Widget/General/Modals/ExtraLoginBonus/Tex720/ExtraLoginBonusInfo", LogoName = "ExtraLoginBonusUCLb";
    public const string LogoFrom = MT + "Tex720/MatchTimeLogo.MatchTimeLogo";
    public static readonly string LogoTo = $"/Game/Assets/ui/Data/Widget/General/Modals/ExtraLoginBonus/Tex720/{LogoName}.{LogoName}";

    public sealed record Step(string What, bool Ok, string Detail);
    /// <summary>A value the mounted mod must read back: the flattened key of an export contains the text.</summary>
    public sealed record Expect(string PackagePath, string Export, string Key, string Contains);

    public sealed class Output
    {
        public List<ModPackage> Packages { get; } = [];
        public List<Step> Steps { get; } = [];
        public List<string> Log { get; } = [];
        public List<Expect> Expects { get; } = [];
        /// <summary>New texture packages and the mip 0 they must decode to</summary>
        public List<(string PackagePath, byte[] Mip0)> Textures { get; } = [];
        public bool Ok => Steps.All(s => s.Ok);
    }

    public static Output Build(GameSource source, GameConfig config, string set, IReadOnlyCollection<string> groups, string? artDir = null, bool centreNames = false)
    {
        var o = new Output();
        artDir ??= Path.Combine(config.WorkDir, "out", "art", "C16");
        bool Check(string what, bool ok, string detail = "") { o.Steps.Add(new Step(what, ok, detail)); return ok; }
        void Log(string head, IEnumerable<string> lines) { o.Log.Add(head); o.Log.AddRange(lines.Select(l => "  " + l)); }
        var done = new HashSet<string>();
        TextureClone.Mip? Art(string file, int w, int h)
        {
            var path = Path.Combine(artDir, file);
            var bytes = File.Exists(path) ? File.ReadAllBytes(path) : [];
            return Check($"art {file} is {w}x{h} BGRA", bytes.Length == w * h * 4, $"{bytes.Length} bytes") ? new TextureClone.Mip(w, h, bytes) : null;
        }

        // a package built from its C16 edit set (layer 1), then more layers; finished by the union check against stock
        byte[]? EditSet(string packagePath)
        {
            var session = PatchSession.Open(source, packagePath, PatchSession.DefaultFolder(new WriteGuard(config), packagePath, set));
            var (chunk, why) = session.ReadSavedChunk();
            Check($"{Short(packagePath)}: edit set {set} re-plans and verifies against stock", chunk != null, why ?? $"{session.Saved.Count} edits");
            return chunk;
        }
        bool Finish(string packagePath, IReadOnlyList<byte[]> layers, IReadOnlyList<(ulong Old, ulong New)>? swaps = null)
        {
            var (fileKey, stock) = source.ReadPackageBytes(packagePath);
            var (problems, changes) = LayerUnion.Check(source, fileKey, stock, layers);
            if (!Check($"{Short(packagePath)}: final re-parse against stock = the union of {layers.Count} layer(s) ({changes} values)", problems.Count == 0, string.Join(" | ", problems.Take(5))))
                return false;
            o.Packages.Add(new ModPackage(packagePath, fileKey, source.ChunkIdHex(fileKey), layers[^1]) { ImportedPackageSwaps = swaps ?? [] });
            done.Add(packagePath);
            return true;
        }
        bool Repoint(string packagePath, string from, string to, bool marginsZero)
        {
            if (done.Contains(packagePath)) return true;
            var r = SoftPathRepoint.Build(source, packagePath, from, to,
                marginsZero ? new[] { "Left", "Top", "Right", "Bottom" }.Select(s => ("Img_Plate", "Margin." + s, 0.0)).ToList() : null);
            Log($"{Short(packagePath)}: texture → {to[(to.LastIndexOf('/') + 1)..]}{(marginsZero ? ", margins 0" : "")}", r.Log);
            if (!Check($"{Short(packagePath)} re-pointed{(marginsZero ? " + margins 0" : "")} (byte diff + re-parse)", r.Package != null, string.Join(" | ", r.Refusals))) return false;
            o.Expects.Add(new Expect(packagePath, "Img_Plate", "Properties.texture.AssetPathName", Full(to)));
            return Finish(packagePath, [r.Package!.Chunk]);
        }
        bool Clone(TextureClone.Result r, string what, byte[] mip0)
        {
            Log(what, r.Log);
            if (!Check(what + " planned and verified", r.Package != null, string.Join(" | ", r.Refusals))) return false;
            if (done.Add(r.Package!.PackagePath)) { o.Packages.Add(r.Package); o.Textures.Add((r.Package.PackagePath, mip0)); }
            return true;
        }
        // both theme copies, resized to the art (the 0164F copy keeps its own mip count)
        bool CloneBoth(string folderPath, string stockName, string newName, TextureClone.Mip art)
        {
            foreach (var folder in new[] { "Tex720", "0164F/Tex720" })
            {
                var stockPath = $"{folderPath}{folder}/{stockName}";
                var mips = MipCount(source, stockPath);
                var chain = new List<TextureClone.Mip> { art };
                while (chain.Count < mips) chain.Add(TextureClone.Half(chain[^1]));
                if (!Clone(TextureClone.BuildResized(source, stockPath, newName, chain), $"{folder}/{newName} ({art.Width}x{art.Height}, {mips} mip{(mips == 1 ? "" : "s")})", art.Bgra)) return false;
            }
            return true;
        }
        // both theme copies, same size, the stock pixels with RGB = white (alpha kept)
        bool WhiteBoth(string stockName, string newName)
        {
            foreach (var folder in new[] { "Tex720", "0164F/Tex720" })
            {
                var stockPath = $"{MT}{folder}/{stockName}";
                var stockMips = source.LoadPackage(stockPath).GetExports().OfType<UTexture2D>().Single().PlatformData.Mips;
                byte[] Whiten(byte[] bgra) { var w = bgra.ToArray(); for (int i = 0; i < w.Length; i += 4) w[i] = w[i + 1] = w[i + 2] = 255; return w; }
                var r = TextureClone.Build(source, stockPath, newName, (w, h) => Whiten(stockMips.Single(m => m.SizeX == w && m.SizeY == h).BulkData!.Data!));
                if (!Clone(r, $"{folder}/{newName} (white, stock alpha)", Whiten(stockMips[0].BulkData!.Data!))) return false;
            }
            return true;
        }

        // C16b: Justification = Center on a banner text (the names first, then the tag), as two more layers
        string[] justifyNames = ["Justification", "ETextJustify", "ETextJustify::Center"];
        bool anchored = false;
        List<byte[]>? Centre(string packagePath, byte[] chunk, string text)
        {
            if (!anchored)
            {
                var why = EnumTagInsert.CheckAgainst(source, MT + "MatchTime", "Text_TeamName_Home", "Justification", "ByteProperty", "ETextJustify", "ETextJustify::Center");
                if (!Check("Justification tag builder reproduces stock's (MatchTime Text_TeamName_Home) byte for byte", why == null, why ?? "41 bytes")) return null;
                anchored = true;
            }
            var (fileKey, _) = source.ReadPackageBytes(packagePath);
            var names = EnumTagInsert.AppendNames(source, fileKey, chunk, justifyNames);
            Log($"{Short(packagePath)}: names for Justification appended", names.Log);
            if (!Check($"{Short(packagePath)}: Justification names appended (old names and hashes kept, header shifted, every export re-parses identically)", names.Chunk != null, string.Join(" | ", names.Refusals))) return null;
            var tag = EnumTagInsert.Insert(source, fileKey, names.Chunk!, text, "VerticalAlignment", "Justification", "ByteProperty", "ETextJustify", "ETextJustify::Center");
            Log($"{Short(packagePath)}: {text}.Justification = Center", tag.Log);
            if (!Check($"{Short(packagePath)}: {text}.Justification = ETextJustify::Center inserted (cut out = input, re-parse, walk)", tag.Chunk != null, string.Join(" | ", tag.Refusals))) return null;
            o.Expects.Add(new Expect(packagePath, text, "Properties.Justification", "ETextJustify::Center"));
            return [names.Chunk!, tag.Chunk!];
        }

        if (groups.Contains("board") && !Board()) return o;
        if (groups.Contains("banners") && !Banners()) return o;
        if (groups.Contains("replay") && !Replay()) return o;
        return o;

        bool Board()
        {
            // MatchTime: edit set → bar + logo paths, scores white → Normal_Display's clipping removed
            const string barName = "MatchTimeBarUC_0";
            var layer1 = EditSet(MT + "MatchTime");
            if (layer1 == null) return false;
            var (fileKey, _) = source.ReadPackageBytes(MT + "MatchTime");
            var p1 = source.ParsePackage(fileKey, layer1);
            var names = p1.ExportsLazy.Select(e => e.Value.Name).ToList();
            var layout1 = ZenPackageLayout.Read(layer1, p1);
            var writes = new List<(int, byte[], string)>();
            var expected = new Dictionary<(int, string), string>();
            foreach (var score in new[] { "Text_Score_Home", "Text_Score_Away" })
            {
                var (at, bytes, why) = EnumValueSwap.Plan(layer1, layout1, names.IndexOf(score), "color", Dark, White);
                if (!Check($"MatchTime {score}.color {Dark} → {White} located", why == null, why ?? $"@0x{at:X}")) return false;
                writes.Add((at, bytes, $"{score}.color → {White}"));
                expected[(names.IndexOf(score), "Properties.color")] = White;
                o.Expects.Add(new Expect(MT + "MatchTime", score, "Properties.color", White));
            }
            string barFrom = MT + "Tex720/MatchTimePlate_0.MatchTimePlate", barTo = MT + $"Tex720/{barName}.{barName[..^2]}";
            var layer2 = NameRenamePatch.Apply(source, fileKey, layer1, [(barFrom, barTo), (LogoFrom, LogoTo)], writes, expected);
            Log("MatchTime layer 2: bar + logo texture paths, scores white", layer2.Log);
            if (!Check("MatchTime layer 2 (bar + logo paths, scores white): byte diff + re-parse", layer2.Ok, string.Join(" | ", layer2.Refusals))) return false;
            var layer3 = TagRemover.Remove(source, fileKey, layer2.Chunk!, "Normal_Display", "Clipping", "EWidgetClipping::ClipToBounds");
            Log("MatchTime layer 3: Normal_Display.Clipping removed", layer3.Log);
            if (!Check("MatchTime layer 3 (Normal_Display.Clipping removed): put back = input, re-parse, walk", layer3.Chunk != null, string.Join(" | ", layer3.Refusals))) return false;
            if (!Finish(MT + "MatchTime", [layer1, layer2.Chunk!, layer3.Chunk!])) return false;
            o.Expects.Add(new Expect(MT + "MatchTime", "Img_Line", "Properties.texture.AssetPathName", MT + $"Tex720/{barName}.{barName}"));
            o.Expects.Add(new Expect(MT + "MatchTime", "Img_Logo", "Properties.texture.AssetPathName", LogoTo));

            // the text colours
            foreach (var table in new[] { "/Game/Assets/ui/Color/ColorTable", "/Game/Assets/ui/Color/ColorTable0164F" })
            {
                var (tableKey, tableBytes) = source.ReadPackageBytes(table);
                var tw = new List<(int, byte[], string)>();
                var te = new Dictionary<(int, string), string>();
                foreach (var (row, rgba, hex) in new[] { ("STR_SWITCH_BRAND_1", ((byte) 255, (byte) 255, (byte) 255, (byte) 255), "FFFFFF"), ("STR_SWITCH_BRAND_2", ((byte) 0, (byte) 0, (byte) 0, (byte) 255), "000000") })
                {
                    var (plan, why) = DataTableColorEdit.Plan(source, tableKey, tableBytes, row, rgba);
                    if (!Check($"{Short(table)} {row} located", plan != null, why ?? plan!.What)) return false;
                    tw.Add((plan!.Offset, plan.Bgra, plan.What));
                    foreach (var kv in plan.Expected) te[kv.Key] = kv.Value;
                    o.Expects.Add(new Expect(table, Short(table), plan.Key + ".Hex", hex));
                }
                var r = NameRenamePatch.Apply(source, tableKey, tableBytes, [], tw, te);
                Log($"{Short(table)}: switch brand colours", r.Log);
                if (!Check($"{Short(table)}: 2 colours (byte diff + re-parse)", r.Ok, string.Join(" | ", r.Refusals))) return false;
                if (!Finish(table, [r.Chunk!])) return false;
            }

            // white plates (clock, score box, compe, banner title rows; extra time, aggregate, emblem squares)
            if (!Repoint(MT + "MatchTimeBase_1", MT + "Tex720/MatchTimePlate_1.MatchTimePlate", MT + "Tex720/MatchTimeWhite_1.MatchTimeWhite", false)) return false;
            if (!Repoint(MT + "MatchTimeBase_2", MT + "Tex720/MatchTimePlate_2.MatchTimePlate", MT + "Tex720/MatchTimeWhite_2.MatchTimeWhite", false)) return false;
            if (!WhiteBoth("MatchTimePlate_1", "MatchTimeWhite_1") || !WhiteBoth("MatchTimePlate_2", "MatchTimeWhite_2")) return false;

            // the bar without the watermark, and the logo
            if (Art($"{barName}_699x96.bgra", 699, 96) is not { } bar || !CloneBoth(MT, "MatchTimePlate_0", barName, bar)) return false;
            if (Art($"{LogoName}_80x80.bgra", 80, 80) is not { } logo) return false;
            return Clone(TextureClone.BuildResized(source, LogoSource, LogoName, [logo]), $"{LogoName} (80x80, the UCL ball logo)", logo.Bgra);
        }

        bool Banners()
        {
            // the goal banner: edit set → the player name white → its plate becomes a DemoTitleBase (the goal art)
            var layer1 = EditSet(MT + "MatchTimeGoal");
            if (layer1 == null) return false;
            var (fileKey, _) = source.ReadPackageBytes(MT + "MatchTimeGoal");
            var p1 = source.ParsePackage(fileKey, layer1);
            var names = p1.ExportsLazy.Select(e => e.Value.Name).ToList();
            var (at, bytes, why) = EnumValueSwap.Plan(layer1, ZenPackageLayout.Read(layer1, p1), names.IndexOf("Text_PlayerName"), "color", Dark, White);
            if (!Check($"MatchTimeGoal Text_PlayerName.color {Dark} → {White} located", why == null, why ?? $"@0x{at:X}")) return false;
            var layer2 = NameRenamePatch.Apply(source, fileKey, layer1, [], [(at, bytes, "Text_PlayerName.color → " + White)],
                new Dictionary<(int, string), string> { [(names.IndexOf("Text_PlayerName"), "Properties.color")] = White });
            Log("MatchTimeGoal layer 2: the player name white", layer2.Log);
            if (!Check("MatchTimeGoal layer 2 (player name white): byte diff + re-parse", layer2.Ok, string.Join(" | ", layer2.Refusals))) return false;
            var layer3 = ClassSwap.Swap(source, fileKey, layer2.Chunk!, "MatchTimeBase_1", DT + "DemoTitleBase");
            Log("MatchTimeGoal layer 3: MatchTimeBase_1 → DemoTitleBase_C", layer3.Log);
            if (!Check("MatchTimeGoal layer 3 (plate class → DemoTitleBase_C): slots, byte diff, re-parse", layer3.Chunk != null, string.Join(" | ", layer3.Refusals))) return false;
            var goalLayers = new List<byte[]> { layer1, layer2.Chunk!, layer3.Chunk! };
            if (centreNames)
            {
                if (Centre(MT + "MatchTimeGoal", layer3.Chunk!, "Text_PlayerName") is not { } extra) return false;
                goalLayers.AddRange(extra);
            }
            if (!Finish(MT + "MatchTimeGoal", goalLayers, [layer3.PackageSwap])) return false;
            o.Expects.Add(new Expect(MT + "MatchTimeGoal", "Text_PlayerName", "Properties.color", White));
            o.Expects.Add(new Expect(MT + "MatchTimeGoal", "MatchTimeBase_1", "Class", "DemoTitleBase_C"));

            foreach (var banner in new[] { "MatchTimeCard", "MatchTimeMemberChange" })
            {
                if (EditSet(MT + banner) is not { } chunk) return false;
                var layers = new List<byte[]> { chunk };
                if (centreNames && banner == "MatchTimeCard")
                {
                    if (Centre(MT + banner, chunk, "Text_Value") is not { } extra) return false;
                    layers.AddRange(extra);
                }
                if (!Finish(MT + banner, layers)) return false;
            }

            if (!Navy()) return false;
            if (!Repoint(DT + "DemoTitleBase", DT + "Tex720/DemoTitlePlate.DemoTitlePlate", DT + "Tex720/DemoTitleUCL01.DemoTitleUCL01", true)) return false;
            return Art("DemoTitleUCL01_696x192.bgra", 696, 192) is { } goal && CloneBoth(DT, "DemoTitlePlate", "DemoTitleUCL01", goal);
        }

        // the navy plates (banner name rows, the replay plate) → the gradient, stretched
        bool Navy()
        {
            if (done.Contains(MT + "MatchTimeBase")) return true;
            if (!Repoint(MT + "MatchTimeBase", MT + "Tex720/MatchTimePlate_0.MatchTimePlate", MT + "Tex720/MatchTimeNavyG_0.MatchTimeNavyG", true)) return false;
            return Art("MatchTimeNavyG_0_512x64.bgra", 512, 64) is { } navy && CloneBoth(MT, "MatchTimePlate_0", "MatchTimeNavyG_0", navy);
        }

        bool Replay()
        {
            var (fileKey, stock) = source.ReadPackageBytes(DT + "DemoTitle");
            var swap = ClassSwap.Swap(source, fileKey, stock, "Plate_DemoTitleBg", MT + "MatchTimeBase");
            Log("DemoTitle: Plate_DemoTitleBg → MatchTimeBase_C", swap.Log);
            if (!Check("DemoTitle (replay plate class → MatchTimeBase_C): slots, byte diff, re-parse", swap.Chunk != null, string.Join(" | ", swap.Refusals))) return false;
            if (!Finish(DT + "DemoTitle", [swap.Chunk!], [swap.PackageSwap])) return false;
            o.Expects.Add(new Expect(DT + "DemoTitle", "Plate_DemoTitleBg", "Class", "MatchTimeBase_C"));
            return Navy();
        }
    }

    private static int MipCount(GameSource source, string texturePath) =>
        source.LoadPackage(texturePath).GetExports().OfType<UTexture2D>().Single().PlatformData.Mips.Length;

    private static string Short(string path) => path[(path.LastIndexOf('/') + 1)..];

    /// <summary>A name-map soft path ("…/X_0.X") as the parse shows it ("…/X_0.X_0"): the object name keeps the package's suffix.</summary>
    private static string Full(string nameMapPath)
    {
        var dot = nameMapPath.LastIndexOf('.');
        var package = nameMapPath[..dot];
        var (_, suffix) = TextureClone.SplitNumber(package[(package.LastIndexOf('/') + 1)..]);
        return nameMapPath + suffix;
    }
}
