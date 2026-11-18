using System.Buffers.Binary;
using System.Text;
using Stb.Managed.TrueType;
using Xunit;

namespace Stb.Managed.Tests.TrueType;

public sealed class TrueTypeMalformedAndLimitsTests
{
    private static readonly int[] TruncationCuts = [0, 1, 4, 11, 12, 16, 28, 64, 128, 256, 512, 1024, 4096];

    private static readonly int[] SimdEdgeWidths = [1, 2, 3, 4, 5, 7, 8, 9, 15, 16, 17, 31, 32, 33, 63, 64, 65, 127, 128];

    private static readonly int[] ProbeCodepoints = ['A', 'g', '8', 0x00C9, 0x20AC];

    [Fact]
    public void EmptyAndGarbageInputsRejectCleanly()
    {
        Assert.Equal(0, StbTrueType.GetNumberOfFonts(ReadOnlySpan<byte>.Empty));
        Assert.Equal(-1, StbTrueType.GetFontOffsetForIndex(ReadOnlySpan<byte>.Empty, 0));
        Assert.Equal(-1, StbTrueType.FindMatchingFont(ReadOnlySpan<byte>.Empty, "Roboto", FontMacStyle.DontCare));
        Assert.False(StbTrueTypeFont.TryLoad(ReadOnlyMemory<byte>.Empty, 0, out StbTrueTypeFont? emptyFont));
        Assert.Null(emptyFont);
        Assert.Throws<StbException>(() => StbTrueTypeFont.Load(ReadOnlySpan<byte>.Empty));

        byte[] garbage = new byte[1024];
        new Random(42).NextBytes(garbage);
        Assert.Equal(0, StbTrueType.GetNumberOfFonts(garbage));
        Assert.Equal(-1, StbTrueType.GetFontOffsetForIndex(garbage, 0));
        Assert.False(StbTrueTypeFont.TryLoad(garbage, 0, out _));
        Assert.Throws<StbException>(() => StbTrueTypeFont.Load(garbage));

        // Invalid offset bounds.
        Assert.False(StbTrueTypeFont.TryLoad(garbage, -1, out _));
        Assert.False(StbTrueTypeFont.TryLoad(garbage, garbage.Length, out _));
        Assert.False(StbTrueTypeFont.TryLoad(garbage, int.MaxValue, out _));
    }

    [Theory]
    [InlineData("fonts/imgui/misc/fonts/Roboto-Medium.ttf")]
    [InlineData("fonts/source-code-pro/OTF/SourceCodePro-Regular.otf")]
    public void TruncatedFontsNeverCrashOrHang(string relativeFontPath)
    {
        string? fullPath = CorpusHelper.TryGetFontPath(relativeFontPath);
        if (fullPath is null)
        {
            Assert.False(CorpusHelper.IsRunningInCi, $"Corpus font '{relativeFontPath}' is missing in CI.");
            return;
        }

        byte[] valid = File.ReadAllBytes(fullPath);

        List<int> cuts = new(TruncationCuts)
        {
            valid.Length / 4,
            valid.Length / 2,
            (valid.Length * 3) / 4,
            valid.Length - 1,
        };

        foreach (int len in cuts)
        {
            if (len < 0 || len > valid.Length)
            {
                continue;
            }

            byte[] truncated = valid.AsSpan(0, len).ToArray();
            _ = StbTrueType.GetNumberOfFonts(truncated);
            _ = StbTrueType.GetFontOffsetForIndex(truncated, 0);
            _ = StbTrueType.FindMatchingFont(truncated, "Roboto", FontMacStyle.DontCare);

            if (StbTrueTypeFont.TryLoad(truncated, 0, out StbTrueTypeFont? font) && font is not null)
            {
                // Even if headers parsed, querying glyphs, shapes, metrics, kerning, rasterizing, and SDF must never crash.
                float scale = font.ScaleForPixelHeight(24f);
                foreach (int cp in ProbeCodepoints)
                {
                    int g = font.FindGlyphIndex(cp);
                    _ = font.GetGlyphHMetrics(g);
                    _ = font.TryGetGlyphBox(g, out _);
                    _ = font.GetGlyphShape(g);
                    _ = font.GetGlyphKernAdvance(g, g);
                    _ = font.GetGlyphBitmap(scale, scale, g, out _);
                    _ = font.GetGlyphSdf(scale, g, 4, 180, 36f, out _);
                }
            }
        }
    }

    [Fact]
    public void CyclicCompoundGlyphTerminatesWithoutStackOverflow()
    {
        // Build a minimal valid TTF whose glyph 1 is a compound glyph referencing glyph 1 (self-cycle).
        byte[] cyclicFont = BuildMinimalCyclicCompoundTtf();
        Assert.True(StbTrueTypeFont.TryLoad(cyclicFont, 0, out StbTrueTypeFont? font));
        Assert.NotNull(font);

        GlyphVertex[] shape = font!.GetGlyphShape(1);
        Assert.NotNull(shape);

        float scale = font.ScaleForPixelHeight(32f);
        _ = font.GetGlyphBitmap(scale, scale, 1, out _);
    }

    [Fact]
    public void StbLimitsAreEnforcedAcrossApis()
    {
        string? fullPath = CorpusHelper.TryGetFontPath("fonts/imgui/misc/fonts/Roboto-Medium.ttf");
        if (fullPath is null)
        {
            Assert.False(CorpusHelper.IsRunningInCi, "Roboto-Medium.ttf is missing in CI.");
            return;
        }

        byte[] data = File.ReadAllBytes(fullPath);

        // Input size limit enforced at load time.
        StbOptions tinyInput = new() { Limits = new StbLimits { MaxAllocationBytes = 1024 } };
        StbException loadEx = Assert.Throws<StbException>(() => StbTrueTypeFont.Load(data, 0, tinyInput));
        Assert.Equal(StbErrorCode.LimitExceeded, loadEx.Code);

        // Bitmap dimension limit enforced at rasterization and SDF.
        StbOptions tinyBitmap = new() { Limits = new StbLimits { MaxWidth = 8, MaxHeight = 8, MaxPixels = 64 } };
        StbTrueTypeFont font = StbTrueTypeFont.Load(data);
        int g = font.FindGlyphIndex('M');
        float largeScale = font.ScaleForPixelHeight(128f);

        StbException bmpEx = Assert.Throws<StbException>(() => font.GetGlyphBitmap(largeScale, largeScale, g, out _, tinyBitmap));
        Assert.Equal(StbErrorCode.LimitExceeded, bmpEx.Code);

        StbException sdfEx = Assert.Throws<StbException>(() => font.GetGlyphSdf(largeScale, g, 4, 180, 36f, out _, tinyBitmap));
        Assert.Equal(StbErrorCode.LimitExceeded, sdfEx.Code);

        byte[] atlas = new byte[256 * 256];
        BakedChar[] baked = new BakedChar[10];
        StbException bakeEx = Assert.Throws<StbException>(() =>
            StbTrueType.BakeFontBitmap(font, 32f, atlas, 256, 256, 32, baked, tinyBitmap));
        Assert.Equal(StbErrorCode.LimitExceeded, bakeEx.Code);

        StbException packEx = Assert.Throws<StbException>(() =>
            StbFontPacker.Begin(atlas, 256, 256, 0, 1, tinyBitmap));
        Assert.Equal(StbErrorCode.LimitExceeded, packEx.Code);
    }

    [Fact]
    public void ArgumentValidationAndDisposeSemantics()
    {
        byte[] atlas = new byte[64 * 64];
        Assert.Throws<StbException>(() => StbFontPacker.Begin(atlas, 0, 64));
        Assert.Throws<StbException>(() => StbFontPacker.Begin(atlas, 64, 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => StbFontPacker.Begin(atlas, 64, 64, strideInBytes: 32));
        Assert.Throws<ArgumentOutOfRangeException>(() => StbFontPacker.Begin(atlas, 64, 64, padding: -1));
        Assert.Throws<ArgumentException>(() => StbFontPacker.Begin(new byte[16], 64, 64));

        StbFontPacker packer = StbFontPacker.Begin(atlas, 64, 64);
        Assert.Throws<ArgumentOutOfRangeException>(() => packer.SetOversampling(0, 1));
        Assert.Throws<ArgumentOutOfRangeException>(() => packer.SetOversampling(1, 9));
        packer.Dispose();
        packer.Dispose(); // idempotent

        Assert.Throws<ObjectDisposedException>(() => packer.SetOversampling(2, 2));
        Assert.Throws<ObjectDisposedException>(() => packer.PackFontRangesPackRects(Span<FontPackRect>.Empty));

        // Direct Rasterize validates stride & buffer size.
        GlyphVertex[] tri =
        [
            new GlyphVertex(0, 0, 0, 0, 0, 0, GlyphVertexType.Move),
            new GlyphVertex(100, 0, 0, 0, 0, 0, GlyphVertexType.Line),
            new GlyphVertex(50, 100, 0, 0, 0, 0, GlyphVertexType.Line),
            new GlyphVertex(0, 0, 0, 0, 0, 0, GlyphVertexType.Line),
        ];
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            StbTrueType.Rasterize(new byte[100], 16, 16, stride: 8, 0.35f, tri, 0.1f, 0.1f, 0f, 0f, 0, 0, invert: true));
        Assert.Throws<ArgumentException>(() =>
            StbTrueType.Rasterize(new byte[10], 16, 16, stride: 16, 0.35f, tri, 0.1f, 0.1f, 0f, 0f, 0, 0, invert: true));
    }

    [Fact]
    public void RasterizeCoversAllSimdEdgeWidthsCleanly()
    {
        // Exercise scalar, SSE2 (4-wide), AVX2 (8-wide), and Arm64 AdvSimd (4-wide) scanline tails across every edge width.
        GlyphVertex[] diamond =
        [
            new GlyphVertex(500, 1000, 0, 0, 0, 0, GlyphVertexType.Move),
            new GlyphVertex(1000, 500, 0, 0, 0, 0, GlyphVertexType.Line),
            new GlyphVertex(500, 0, 0, 0, 0, 0, GlyphVertexType.Line),
            new GlyphVertex(0, 500, 0, 0, 0, 0, GlyphVertexType.Line),
            new GlyphVertex(500, 1000, 0, 0, 0, 0, GlyphVertexType.Line),
        ];

        foreach (int w in SimdEdgeWidths)
        {
            int h = Math.Max(4, w / 2 + 2);
            int stride = w + 3;
            byte[] pixels = new byte[stride * h];
            Array.Fill(pixels, (byte)0x5A);

            float sx = w / 1000f;
            float sy = h / 1000f;
            StbTrueType.Rasterize(pixels, w, h, stride, 0.35f, diamond, sx, sy, 0f, 0f, 0, -h, invert: true);

            // Padding bytes beyond the width must stay untouched.
            for (int row = 0; row < h; ++row)
            {
                for (int pad = w; pad < stride; ++pad)
                {
                    Assert.Equal((byte)0x5A, pixels[row * stride + pad]);
                }
            }

            // Center pixel of the diamond should have non-zero coverage for widths >= 3.
            if (w >= 3)
            {
                byte center = pixels[(h / 2) * stride + (w / 2)];
                Assert.True(center > 128, $"Expected center pixel > 128 for width={w}, height={h}, got {center}.");
            }
        }
    }

    [Fact]
    public void MutatedTtfAndOtfFuzzNeverThrowsUnhandledException()
    {
        string? fullPath = CorpusHelper.TryGetFontPath("fonts/imgui/misc/fonts/ProggyClean.ttf");
        if (fullPath is null)
        {
            Assert.False(CorpusHelper.IsRunningInCi, "ProggyClean.ttf is missing in CI.");
            return;
        }

        byte[] original = File.ReadAllBytes(fullPath);
        Random rng = new(123456);

        for (int iter = 0; iter < 60; ++iter)
        {
            byte[] mutated = (byte[])original.Clone();
            int flips = rng.Next(1, 16);
            for (int f = 0; f < flips; ++f)
            {
                int pos = rng.Next(mutated.Length);
                mutated[pos] = (byte)rng.Next(256);
            }

            try
            {
                if (StbTrueTypeFont.TryLoad(mutated, 0, out StbTrueTypeFont? font) && font is not null)
                {
                    float scale = font.ScaleForPixelHeight(24f);
                    if (float.IsFinite(scale) && scale > 0f && scale < 10f)
                    {
                        int g = font.FindGlyphIndex('A');
                        _ = font.GetGlyphHMetrics(g);
                        _ = font.GetGlyphShape(g);
                        _ = font.GetGlyphBitmap(scale, scale, g, out _);
                    }
                }
            }
            catch (StbException)
            {
                // Expected for malformed tables or exceeded limits.
            }
        }
    }

    private static byte[] BuildMinimalCyclicCompoundTtf()
    {
        // Minimal SFNT tables required by StbTrueTypeFont: cmap, glyf, head, hhea, hmtx, loca, maxp
        byte[] head = new byte[54];
        BinaryPrimitives.WriteUInt16BigEndian(head.AsSpan(18), 1000); // unitsPerEm
        BinaryPrimitives.WriteInt16BigEndian(head.AsSpan(36), 0); // xMin
        BinaryPrimitives.WriteInt16BigEndian(head.AsSpan(38), 0); // yMin
        BinaryPrimitives.WriteInt16BigEndian(head.AsSpan(40), 500); // xMax
        BinaryPrimitives.WriteInt16BigEndian(head.AsSpan(42), 500); // yMax
        BinaryPrimitives.WriteInt16BigEndian(head.AsSpan(50), 1); // indexToLocFormat = 1 (long 32-bit offsets)

        byte[] maxp = new byte[6];
        BinaryPrimitives.WriteUInt32BigEndian(maxp.AsSpan(0), 0x00010000);
        BinaryPrimitives.WriteUInt16BigEndian(maxp.AsSpan(4), 2); // numGlyphs = 2

        byte[] hhea = new byte[36];
        BinaryPrimitives.WriteInt16BigEndian(hhea.AsSpan(4), 800); // ascent
        BinaryPrimitives.WriteInt16BigEndian(hhea.AsSpan(6), -200); // descent
        BinaryPrimitives.WriteUInt16BigEndian(hhea.AsSpan(34), 2); // numberOfHMetrics

        byte[] hmtx = new byte[8];
        BinaryPrimitives.WriteUInt16BigEndian(hmtx.AsSpan(0), 500);
        BinaryPrimitives.WriteUInt16BigEndian(hmtx.AsSpan(4), 500);

        // cmap format 0 (Microsoft Unicode or Apple) mapping 'A' -> glyph 1
        byte[] cmap = new byte[4 + 8 + 262];
        BinaryPrimitives.WriteUInt16BigEndian(cmap.AsSpan(2), 1); // numTables = 1
        BinaryPrimitives.WriteUInt16BigEndian(cmap.AsSpan(4), 0); // platform = Unicode
        BinaryPrimitives.WriteUInt16BigEndian(cmap.AsSpan(6), 0);
        BinaryPrimitives.WriteUInt32BigEndian(cmap.AsSpan(8), 12); // offset
        BinaryPrimitives.WriteUInt16BigEndian(cmap.AsSpan(12), 0); // format 0
        BinaryPrimitives.WriteUInt16BigEndian(cmap.AsSpan(14), 262);
        cmap[12 + 6 + 'A'] = 1;

        // glyf: glyph 0 empty, glyph 1 is compound referencing glyph 1!
        byte[] glyf = new byte[16];
        BinaryPrimitives.WriteInt16BigEndian(glyf.AsSpan(0), -1); // numberOfContours = -1 (compound)
        BinaryPrimitives.WriteInt16BigEndian(glyf.AsSpan(2), 0);
        BinaryPrimitives.WriteInt16BigEndian(glyf.AsSpan(4), 0);
        BinaryPrimitives.WriteInt16BigEndian(glyf.AsSpan(6), 500);
        BinaryPrimitives.WriteInt16BigEndian(glyf.AsSpan(8), 500);
        BinaryPrimitives.WriteUInt16BigEndian(glyf.AsSpan(10), 0x0002); // flags: ARGS_ARE_XY_VALUES, no MORE_COMPONENTS
        BinaryPrimitives.WriteUInt16BigEndian(glyf.AsSpan(12), 1); // gidx = 1 (SELF CYCLE!)
        glyf[14] = 0; // dx
        glyf[15] = 0; // dy

        // loca (long format, numGlyphs + 1 = 3 entries): glyph 0 [0..0), glyph 1 [0..16)
        byte[] loca = new byte[12];
        BinaryPrimitives.WriteUInt32BigEndian(loca.AsSpan(0), 0);
        BinaryPrimitives.WriteUInt32BigEndian(loca.AsSpan(4), 0);
        BinaryPrimitives.WriteUInt32BigEndian(loca.AsSpan(8), 16);

        (string Tag, byte[] Data)[] tables =
        [
            ("cmap", cmap),
            ("glyf", glyf),
            ("head", head),
            ("hhea", hhea),
            ("hmtx", hmtx),
            ("loca", loca),
            ("maxp", maxp),
        ];

        int total = 12 + tables.Length * 16;
        foreach ((_, byte[] d) in tables)
        {
            total += (d.Length + 3) & ~3;
        }

        byte[] sfnt = new byte[total];
        BinaryPrimitives.WriteUInt32BigEndian(sfnt.AsSpan(0), 0x00010000);
        BinaryPrimitives.WriteUInt16BigEndian(sfnt.AsSpan(4), (ushort)tables.Length);

        int dataOff = 12 + tables.Length * 16;
        for (int i = 0; i < tables.Length; ++i)
        {
            (string tag, byte[] d) = tables[i];
            int entry = 12 + i * 16;
            Encoding.ASCII.GetBytes(tag, sfnt.AsSpan(entry, 4));
            BinaryPrimitives.WriteUInt32BigEndian(sfnt.AsSpan(entry + 8), (uint)dataOff);
            BinaryPrimitives.WriteUInt32BigEndian(sfnt.AsSpan(entry + 12), (uint)d.Length);
            d.CopyTo(sfnt.AsSpan(dataOff));
            dataOff += (d.Length + 3) & ~3;
        }

        return sfnt;
    }
}
