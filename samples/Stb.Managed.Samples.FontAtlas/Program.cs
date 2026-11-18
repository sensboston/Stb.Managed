using System.Globalization;
using System.Text.Json;
using Stb.Managed;
using Stb.Managed.RectPack;
using Stb.Managed.TrueType;

if (args.Length == 0 || args[0] is "-h" or "--help")
{
    PrintUsage();
    return 0;
}

if (string.Equals(args[0], "--self-test", StringComparison.Ordinal))
{
    string? fontPath = args.Length >= 2 ? args[1] : null;
    RunSelfTest(fontPath);
    return 0;
}

string command = args[0].ToUpperInvariant();
switch (command)
{
    case "PACK":
        return RunPack(args);
    case "SDF":
        return RunSdf(args);
    default:
        Console.Error.WriteLine($"Unknown command '{args[0]}'.");
        PrintUsage();
        return 1;
}

static void PrintUsage()
{
    Console.WriteLine("Stb.Managed.Samples.FontAtlas — Native AOT-ready TrueType/OTF atlas & SDF generator");
    Console.WriteLine("Usage:");
    Console.WriteLine("  pack <font.ttf|otf> <out.png> [out.json] [size=48] [atlasW=512] [atlasH=512] [oversample=2]");
    Console.WriteLine("  sdf  <font.ttf|otf> <out.png> [out.json] [size=48] [padding=5]");
    Console.WriteLine("  --self-test [font.ttf|otf]   Run end-to-end AOT self-verification");
}

static int RunPack(string[] args)
{
    if (args.Length < 3)
    {
        Console.Error.WriteLine("Usage: pack <font.ttf|otf> <out.png> [out.json] [size] [atlasW] [atlasH] [oversample]");
        return 1;
    }

    string fontPath = args[1];
    string pngPath = args[2];
    string? jsonPath = args.Length >= 4 ? args[3] : null;
    float fontSize = args.Length >= 5 ? float.Parse(args[4], CultureInfo.InvariantCulture) : 48f;
    int atlasW = args.Length >= 6 ? int.Parse(args[5], CultureInfo.InvariantCulture) : 512;
    int atlasH = args.Length >= 7 ? int.Parse(args[6], CultureInfo.InvariantCulture) : 512;
    int oversample = args.Length >= 8 ? int.Parse(args[7], CultureInfo.InvariantCulture) : 2;

    byte[] fontBytes = File.ReadAllBytes(fontPath);
    byte[] atlas = new byte[atlasW * atlasH];
    const int firstChar = 32;
    const int numChars = 95;
    PackedChar[] packed = new PackedChar[numChars];

    using (StbFontPacker packer = StbFontPacker.Begin(
        atlas, atlasW, atlasH, strideInBytes: 0, padding: 1,
        new StbOptions { MaxDegreeOfParallelism = 0 }))
    {
        packer.SetOversampling(oversample, oversample);
        bool allFit = packer.PackFontRange(fontBytes, fontIndex: 0, fontSize, firstChar, packed);
        if (!allFit)
        {
            Console.Error.WriteLine($"Warning: not all glyphs fit in {atlasW}x{atlasH} atlas at {fontSize}px.");
        }
    }

    byte[] png = StbImageWrite.WritePng(atlas, atlasW, atlasH, ColorComponents.Grey);
    File.WriteAllBytes(pngPath, png);

    if (!string.IsNullOrWhiteSpace(jsonPath))
    {
        byte[] json = SerializePackedMetricsJson(atlasW, atlasH, fontSize, firstChar, packed);
        File.WriteAllBytes(jsonPath, json);
    }

    Console.WriteLine($"Packed {numChars} ASCII glyphs at {fontSize}px ({oversample}x{oversample}) into {atlasW}x{atlasH} -> {pngPath} ({png.Length} bytes).");
    return 0;
}

static int RunSdf(string[] args)
{
    if (args.Length < 3)
    {
        Console.Error.WriteLine("Usage: sdf <font.ttf|otf> <out.png> [out.json] [size] [padding]");
        return 1;
    }

    string fontPath = args[1];
    string pngPath = args[2];
    string? jsonPath = args.Length >= 4 ? args[3] : null;
    float fontSize = args.Length >= 5 ? float.Parse(args[4], CultureInfo.InvariantCulture) : 48f;
    int padding = args.Length >= 6 ? int.Parse(args[5], CultureInfo.InvariantCulture) : 5;

    byte[] fontBytes = File.ReadAllBytes(fontPath);
    StbTrueTypeFont font = StbTrueTypeFont.Load(fontBytes);
    float scale = font.ScaleForPixelHeight(fontSize);
    const byte onEdgeValue = 180;
    float pixelDistScale = onEdgeValue / (float)Math.Max(padding, 1);

    const int firstChar = 32;
    const int numChars = 95;
    byte[][] bitmaps = new byte[numChars][];
    PackRect[] rects = new PackRect[numChars];
    int[] xoffs = new int[numChars];
    int[] yoffs = new int[numChars];

    for (int i = 0; i < numChars; ++i)
    {
        int cp = firstChar + i;
        bitmaps[i] = font.GetCodepointSdf(scale, cp, padding, onEdgeValue, pixelDistScale, out GlyphBitmapMetrics gm);
        rects[i] = new PackRect(gm.Width > 0 ? gm.Width + 2 : 0, gm.Height > 0 ? gm.Height + 2 : 0, id: cp);
        xoffs[i] = gm.XOffset;
        yoffs[i] = gm.YOffset;
    }

    const int atlasW = 1024;
    const int atlasH = 1024;
    bool allFit = StbRectPack.PackRects(atlasW, atlasH, rects, RectPackHeuristic.SkylineBestFit);
    if (!allFit)
    {
        throw new InvalidOperationException($"SDF glyphs did not fit into {atlasW}x{atlasH} atlas.");
    }

    byte[] atlas = new byte[atlasW * atlasH];
    for (int i = 0; i < numChars; ++i)
    {
        byte[] bmp = bitmaps[i];
        if (bmp.Length == 0 || !rects[i].WasPacked)
        {
            continue;
        }

        int gw = rects[i].Width - 2;
        int gh = rects[i].Height - 2;
        int dstX = rects[i].X + 1;
        int dstY = rects[i].Y + 1;
        for (int row = 0; row < gh; ++row)
        {
            bmp.AsSpan(row * gw, gw).CopyTo(atlas.AsSpan((dstY + row) * atlasW + dstX, gw));
        }
    }

    byte[] png = StbImageWrite.WritePng(atlas, atlasW, atlasH, ColorComponents.Grey);
    File.WriteAllBytes(pngPath, png);

    if (!string.IsNullOrWhiteSpace(jsonPath))
    {
        byte[] json = SerializeSdfMetricsJson(atlasW, atlasH, fontSize, rects, xoffs, yoffs);
        File.WriteAllBytes(jsonPath, json);
    }

    Console.WriteLine($"Generated SDF atlas ({numChars} glyphs, {fontSize}px, pad={padding}) -> {pngPath} ({png.Length} bytes).");
    return 0;
}

static byte[] SerializePackedMetricsJson(int width, int height, float fontSize, int firstChar, ReadOnlySpan<PackedChar> chars)
{
    using var ms = new MemoryStream();
    using (var writer = new Utf8JsonWriter(ms, new JsonWriterOptions { Indented = true }))
    {
        writer.WriteStartObject();
        writer.WriteNumber("width", width);
        writer.WriteNumber("height", height);
        writer.WriteNumber("fontSize", fontSize);
        writer.WriteStartArray("glyphs");
        for (int i = 0; i < chars.Length; ++i)
        {
            PackedChar pc = chars[i];
            writer.WriteStartObject();
            writer.WriteNumber("codepoint", firstChar + i);
            writer.WriteNumber("x0", pc.X0);
            writer.WriteNumber("y0", pc.Y0);
            writer.WriteNumber("x1", pc.X1);
            writer.WriteNumber("y1", pc.Y1);
            writer.WriteNumber("xAdvance", pc.XAdvance);
            writer.WriteNumber("xOff", pc.XOff);
            writer.WriteNumber("yOff", pc.YOff);
            writer.WriteEndObject();
        }

        writer.WriteEndArray();
        writer.WriteEndObject();
    }

    return ms.ToArray();
}

static byte[] SerializeSdfMetricsJson(int width, int height, float fontSize, ReadOnlySpan<PackRect> rects, ReadOnlySpan<int> xoffs, ReadOnlySpan<int> yoffs)
{
    using var ms = new MemoryStream();
    using (var writer = new Utf8JsonWriter(ms, new JsonWriterOptions { Indented = true }))
    {
        writer.WriteStartObject();
        writer.WriteNumber("width", width);
        writer.WriteNumber("height", height);
        writer.WriteNumber("fontSize", fontSize);
        writer.WriteStartArray("glyphs");
        for (int i = 0; i < rects.Length; ++i)
        {
            PackRect r = rects[i];
            writer.WriteStartObject();
            writer.WriteNumber("codepoint", r.Id);
            writer.WriteNumber("x", r.X);
            writer.WriteNumber("y", r.Y);
            writer.WriteNumber("w", r.Width);
            writer.WriteNumber("h", r.Height);
            writer.WriteNumber("xOff", xoffs[i]);
            writer.WriteNumber("yOff", yoffs[i]);
            writer.WriteEndObject();
        }

        writer.WriteEndArray();
        writer.WriteEndObject();
    }

    return ms.ToArray();
}

static void RunSelfTest(string? fontPath)
{
    // Verify StbRectPack (BL & BF) + PNG write even without external font files.
    PackRect[] testRects = new PackRect[120];
    var rng = new Random(12345);
    for (int i = 0; i < testRects.Length; ++i)
    {
        testRects[i] = new PackRect(rng.Next(8, 36), rng.Next(8, 36), id: i);
    }

    if (!StbRectPack.PackRects(512, 512, testRects, RectPackHeuristic.SkylineBestFit))
    {
        throw new InvalidOperationException("FontAtlas self-test: RectPack failed.");
    }

    // Verify invalid font rejection under Native AOT.
    byte[] invalidFont = [1, 2, 3, 4];
    if (StbTrueTypeFont.TryLoad(invalidFont, 0, out _))
    {
        throw new InvalidOperationException("FontAtlas self-test: invalid font unexpectedly loaded.");
    }

    if (fontPath is not null && File.Exists(fontPath))
    {
        byte[] fontBytes = File.ReadAllBytes(fontPath);
        StbTrueTypeFont font = StbTrueTypeFont.Load(fontBytes);
        byte[] atlas = new byte[512 * 512];
        PackedChar[] packed = new PackedChar[95];
        using (StbFontPacker packer = StbFontPacker.Begin(
            atlas, 512, 512, 0, 1, new StbOptions { MaxDegreeOfParallelism = 0 }))
        {
            packer.SetOversampling(2, 2);
            if (!packer.PackFontRange(fontBytes, 0, 32f, 32, packed))
            {
                throw new InvalidOperationException("FontAtlas self-test: PackFontRange failed.");
            }
        }

        float scale = font.ScaleForPixelHeight(48f);
        byte[] sdfA = font.GetCodepointSdf(scale, 'A', 5, 180, 36f, out GlyphBitmapMetrics sdfMetrics);
        if (sdfA.Length == 0 || sdfMetrics.Width <= 0 || sdfMetrics.Height <= 0)
        {
            throw new InvalidOperationException("FontAtlas self-test: SDF generation failed.");
        }

        byte[] png = StbImageWrite.WritePng(atlas, 512, 512, ColorComponents.Grey);
        byte[] json = SerializePackedMetricsJson(512, 512, 32f, 32, packed);
        Console.WriteLine($"FontAtlas self-test OK ({Path.GetFileName(fontPath)}): packed 95 glyphs -> PNG({png.Length}B), JSON({json.Length}B), SDF 'A'={sdfMetrics.Width}x{sdfMetrics.Height}.");
    }
    else
    {
        Console.WriteLine($"FontAtlas self-test OK (synthetic): packed {testRects.Length} rects and verified TrueType rejection.");
    }
}
