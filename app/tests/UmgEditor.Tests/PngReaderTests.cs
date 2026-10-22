using UmgEditor.Core.Textures;

namespace UmgEditor.Tests;

/// <summary>
/// Step 3 (textures): the built-in PNG reader. Ground truth is PIL (work/scripts/make_png_fixtures.py): each fixture's
/// .bgra is what PIL decodes it to; filters_rgba8 is written by hand with every row filter in turn.
/// </summary>
[TestClass]
public class PngReaderTests
{
    private static readonly string Dir = Path.Combine(AppContext.BaseDirectory, "Fixtures", "png");

    [TestMethod]
    [DataRow("rgba8"), DataRow("rgb8"), DataRow("grey8"), DataRow("greyalpha8"), DataRow("grey1"),
     DataRow("palette8_trns"), DataRow("palette4"), DataRow("rgb8_trns"), DataRow("filters_rgba8")]
    public void Read_MatchesPil(string name)
    {
        var (image, why) = PngReader.Read(Path.Combine(Dir, name + ".png"));
        Assert.IsNotNull(image, why);
        Assert.AreEqual((37, 23), (image.Width, image.Height));
        var expected = File.ReadAllBytes(Path.Combine(Dir, name + ".bgra"));
        var first = Enumerable.Range(0, expected.Length).FirstOrDefault(i => image.Bgra[i] != expected[i], -1);
        Assert.AreEqual(-1, first, $"{name} ({image.Format}): first differing byte at {first} (pixel {first / 4})");
        Console.WriteLine($"{name}: {image.Format}, {image.Bgra.Length} bytes as PIL");
    }

    [TestMethod]
    public void Read_RoundTripsPngWriter()
    {
        var rnd = new Random(3);
        var bgra = new byte[61 * 17 * 4];
        rnd.NextBytes(bgra);
        var path = Path.Combine(Path.GetTempPath(), $"pngreader_{Guid.NewGuid():N}.png");
        try
        {
            PngWriter.Write(path, new DecodedImage(61, 17, bgra, "test"));
            var (image, why) = PngReader.Read(path);
            Assert.IsNotNull(image, why);
            CollectionAssert.AreEqual(bgra, image.Bgra);
        }
        finally { File.Delete(path); }
    }

    [TestMethod]
    public void Refuses_16Bit_Interlaced_BadCrc_AndNotAPng()
    {
        StringAssert.Contains(PngReader.Read(Path.Combine(Dir, "bad_grey16.png")).Why, "16-bit");
        StringAssert.Contains(PngReader.Read(Path.Combine(Dir, "bad_interlaced.png")).Why, "interlaced");
        var bytes = File.ReadAllBytes(Path.Combine(Dir, "rgb8.png"));
        bytes[40] ^= 0xFF; // inside the first IDAT
        StringAssert.Contains(PngReader.Read(bytes).Why, "CRC");
        StringAssert.Contains(PngReader.Read("GIF89a........"u8.ToArray()).Why, "not a PNG");
        StringAssert.Contains(PngReader.Read(Path.Combine(Dir, "missing.png")).Why, "doesn't exist");
    }
}
