using System.Buffers.Binary;
using System.Text;
using Stb.Managed.Oracle;
using Stb.Managed.TrueType;
using Xunit;

namespace Stb.Managed.Tests.TrueType;

public sealed class TrueTypeOracleDifferentialTests
{
    private static readonly int[] ExtendedCodepoints =
    [
        0x00A0, 0x00A9, 0x00AE, 0x00B0, 0x00C0, 0x00C1, 0x00C4, 0x00C7, 0x00C9, 0x00D1,
        0x00D6, 0x00DC, 0x00DF, 0x00E0, 0x00E1, 0x00E4, 0x00E7, 0x00E9, 0x00F1, 0x00F6,
        0x00FC, 0x0410, 0x042F, 0x0430, 0x2014, 0x2026, 0x20AC, 0x25A1, 0xFFFD, 0x1F600,
    ];

    private static readonly string[] KerningPairs =
    [
        "AV", "AW", "Ay", "LT", "LV", "LW", "PA", "Ta", "Te", "Ti",
        "To", "Tr", "Ty", "VA", "Va", "Ve", "Vo", "WA", "Wa", "We",
        "Wo", "YA", "Ya", "Yo", "ff", "fi", "fl", "rt", "rv", "rw",
    ];

    private static readonly float[] RasterSizes = [12f, 24f, 48f];

    private static readonly char[] SampleGlyphChars =
    [
        'A', 'B', 'C', 'G', 'M', 'O', 'Q', 'S', 'W', 'a', 'e', 'g', 'm', 'q', 's', 'w',
        '0', '3', '8', '@', '&', '%', '$', '!', '?', '#', '~', ' ',
    ];

    private static readonly char[] SdfChars = ['A', 'B', 'O', 'Q', 'S', 'W', 'g', '8', '@', '&'];

    private static readonly char[] PrefilterChars = ['A', 'g', 'S', '8', '@'];

    private static readonly int[] AccentedCodepoints =
    [
        0x00C1, 0x00C4, 0x00C7, 0x00C9, 0x00D1, 0x00D6, 0x00DC, 0x00E1, 0x00E9, 0x00F1, 0x00F6,
    ];

    private static readonly bool[] BoolValues = [true, false];

    public static IEnumerable<object[]> CorpusFontRelativePaths()
    {
        foreach (string path in CorpusHelper.TrueTypeCorpusFontPaths)
        {
            yield return [path];
        }
    }

    [Theory]
    [MemberData(nameof(CorpusFontRelativePaths))]
    public void FontInfoMetricsCmapAndNamesMatchNativeOracle(string relativeFontPath)
    {
        byte[]? data = LoadCorpusFontOrSkip(relativeFontPath);
        if (data is null)
        {
            return;
        }

        Assert.Equal(StbOracle.GetNumberOfFonts(data), StbTrueType.GetNumberOfFonts(data));
        Assert.Equal(1, StbTrueType.GetNumberOfFonts(data));
        Assert.Equal(StbOracle.GetFontOffsetForIndex(data, 0), StbTrueType.GetFontOffsetForIndex(data, 0));
        Assert.Equal(-1, StbTrueType.GetFontOffsetForIndex(data, 1));
        Assert.Equal(-1, StbTrueType.GetFontOffsetForIndex(data, -1));

        (float oAsc, float oDesc, float oGap) = StbOracle.GetScaledFontVMetrics(data, 0, 32f);
        ScaledFontVMetrics mScaled = StbTrueType.GetScaledFontVMetrics(data, 0, 32f);
        Assert.Equal(oAsc, mScaled.Ascent, precision: 5);
        Assert.Equal(oDesc, mScaled.Descent, precision: 5);
        Assert.Equal(oGap, mScaled.LineGap, precision: 5);

        using OracleTrueTypeFont oracle = StbOracle.OpenFont(data, 0)!;
        Assert.NotNull(oracle);

        StbTrueTypeFont managed = StbTrueTypeFont.Load(data);
        Assert.Equal(oracle.NumGlyphs, managed.NumGlyphs);
        Assert.Equal(
            relativeFontPath.EndsWith(".otf", StringComparison.OrdinalIgnoreCase),
            managed.IsCff);

        Assert.Equal(oracle.ScaleForPixelHeight(32f), managed.ScaleForPixelHeight(32f), precision: 6);
        Assert.Equal(oracle.ScaleForPixelWidth(32f), managed.ScaleForPixelWidth(32f), precision: 6);
        Assert.Equal(oracle.ScaleForMappingEmToPixels(32f), managed.ScaleForMappingEmToPixels(32f), precision: 6);

        (int oa, int od, int og) = oracle.GetFontVMetrics();
        FontVMetrics mv = managed.GetFontVMetrics();
        Assert.Equal(oa, mv.Ascent);
        Assert.Equal(od, mv.Descent);
        Assert.Equal(og, mv.LineGap);

        bool oHasOs2 = oracle.TryGetFontVMetricsOs2(out int ota, out int otd, out int otg);
        bool mHasOs2 = managed.TryGetFontVMetricsOS2(out FontVMetrics mos2);
        Assert.Equal(oHasOs2, mHasOs2);
        if (oHasOs2)
        {
            Assert.Equal(ota, mos2.Ascent);
            Assert.Equal(otd, mos2.Descent);
            Assert.Equal(otg, mos2.LineGap);
        }

        (int ox0, int oy0, int ox1, int oy1) = oracle.GetFontBoundingBox();
        GlyphBox mbb = managed.GetFontBoundingBox();
        Assert.Equal(ox0, mbb.X0);
        Assert.Equal(oy0, mbb.Y0);
        Assert.Equal(ox1, mbb.X1);
        Assert.Equal(oy1, mbb.Y1);

        bool oHasUl = oracle.TryGetFontUnderlineMetrics(out int oPos, out int oThick);
        bool mHasUl = managed.TryGetFontUnderlineMetrics(out FontUnderlineMetrics mul);
        Assert.Equal(oHasUl, mHasUl);
        if (oHasUl)
        {
            Assert.Equal(oPos, mul.Position);
            Assert.Equal(oThick, mul.Thickness);
        }

        // Verify cmap, HMetrics, GlyphBox, IsGlyphEmpty across printable ASCII + extended codepoints.
        for (int cp = 32; cp <= 126; ++cp)
        {
            VerifyCodepointMetrics(oracle, managed, cp);
        }

        foreach (int cp in ExtendedCodepoints)
        {
            VerifyCodepointMetrics(oracle, managed, cp);
        }

        // Verify font name table queries.
        string? oracleDefaultName = oracle.GetFontNameDefault();
        bool managedHasDefaultName = managed.TryGetFontNameDefault(out string? managedDefaultName);
        Assert.Equal(oracleDefaultName is not null, managedHasDefaultName);
        Assert.Equal(oracleDefaultName, managedDefaultName);

        byte[]? oracleMsName = oracle.GetFontNameString(
            (int)FontPlatformId.Microsoft,
            (int)FontMicrosoftEncodingId.UnicodeBmp,
            (int)FontMicrosoftLanguageId.English,
            1);
        bool managedHasMsName = managed.TryGetFontNameString(
            (int)FontPlatformId.Microsoft,
            (int)FontMicrosoftEncodingId.UnicodeBmp,
            (int)FontMicrosoftLanguageId.English,
            1,
            out ReadOnlySpan<byte> managedMsName);
        Assert.Equal(oracleMsName is not null, managedHasMsName);
        if (oracleMsName is not null)
        {
            Assert.Equal(oracleMsName, managedMsName.ToArray());

            if (managedDefaultName is not null)
            {
                int oMatch = StbOracle.FindMatchingFont(data, managedDefaultName, (int)FontMacStyle.DontCare);
                int mMatch = StbTrueType.FindMatchingFont(data, managedDefaultName, FontMacStyle.DontCare);
                Assert.Equal(oMatch, mMatch);
            }
        }
    }

    [Theory]
    [MemberData(nameof(CorpusFontRelativePaths))]
    public void KerningMatchesNativeOracle(string relativeFontPath)
    {
        byte[]? data = LoadCorpusFontOrSkip(relativeFontPath);
        if (data is null)
        {
            return;
        }

        using OracleTrueTypeFont oracle = StbOracle.OpenFont(data, 0)!;
        StbTrueTypeFont managed = StbTrueTypeFont.Load(data);

        Assert.Equal(oracle.GetKerningTableLength(), managed.GetKerningTableLength());
        OracleKerningEntry[] oracleTable = oracle.GetKerningTable();
        KerningEntry[] managedTable = new KerningEntry[managed.GetKerningTableLength()];
        int written = managed.GetKerningTable(managedTable);
        Assert.Equal(oracleTable.Length, written);
        for (int i = 0; i < written; ++i)
        {
            Assert.Equal(oracleTable[i].Glyph1, managedTable[i].Glyph1);
            Assert.Equal(oracleTable[i].Glyph2, managedTable[i].Glyph2);
            Assert.Equal(oracleTable[i].Advance, managedTable[i].Advance);
        }

        foreach (string pair in KerningPairs)
        {
            int c1 = pair[0];
            int c2 = pair[1];
            Assert.Equal(oracle.GetCodepointKernAdvance(c1, c2), managed.GetCodepointKernAdvance(c1, c2));

            int g1 = managed.FindGlyphIndex(c1);
            int g2 = managed.FindGlyphIndex(c2);
            Assert.Equal(oracle.GetGlyphKernAdvance(g1, g2), managed.GetGlyphKernAdvance(g1, g2));
        }
    }

    [Fact]
    public void SyntheticLegacyKernTableMatchesNativeOracle()
    {
        byte[]? baseData = LoadCorpusFontOrSkip("fonts/imgui/misc/fonts/ProggyClean.ttf");
        if (baseData is null)
        {
            return;
        }

        // Append a valid legacy format-0 'kern' table to a real TTF so both kern lookup and GetKerningTable are tested.
        StbTrueTypeFont baseFont = StbTrueTypeFont.Load(baseData);
        int gA = baseFont.FindGlyphIndex('A');
        int gV = baseFont.FindGlyphIndex('V');
        int gT = baseFont.FindGlyphIndex('T');
        int go = baseFont.FindGlyphIndex('o');
        Assert.True(gA > 0 && gV > 0 && gT > 0 && go > 0);

        (int G1, int G2, short Adv)[] pairs =
        [
            (gA, gV, -75),
            (gT, go, -40),
            (gV, gA, -65),
        ];
        Array.Sort(pairs, (a, b) =>
        {
            uint ka = ((uint)a.G1 << 16) | (uint)a.G2;
            uint kb = ((uint)b.G1 << 16) | (uint)b.G2;
            return ka.CompareTo(kb);
        });

        byte[] kernTable = BuildLegacyKernFormat0Table(pairs);
        byte[] patchedTtf = AppendSfntTable(baseData, "kern", kernTable);

        using OracleTrueTypeFont oracle = StbOracle.OpenFont(patchedTtf, 0)!;
        Assert.NotNull(oracle);
        StbTrueTypeFont managed = StbTrueTypeFont.Load(patchedTtf);

        Assert.Equal(pairs.Length, oracle.GetKerningTableLength());
        Assert.Equal(pairs.Length, managed.GetKerningTableLength());

        OracleKerningEntry[] oTable = oracle.GetKerningTable();
        KerningEntry[] mTable = new KerningEntry[managed.GetKerningTableLength()];
        int written = managed.GetKerningTable(mTable);
        Assert.Equal(oTable.Length, written);
        for (int i = 0; i < written; ++i)
        {
            Assert.Equal(oTable[i].Glyph1, mTable[i].Glyph1);
            Assert.Equal(oTable[i].Glyph2, mTable[i].Glyph2);
            Assert.Equal(oTable[i].Advance, mTable[i].Advance);
        }

        Assert.Equal(-75, managed.GetGlyphKernAdvance(gA, gV));
        Assert.Equal(-40, managed.GetGlyphKernAdvance(gT, go));
        Assert.Equal(-65, managed.GetGlyphKernAdvance(gV, gA));
        Assert.Equal(0, managed.GetGlyphKernAdvance(gA, go));
        Assert.Equal(oracle.GetGlyphKernAdvance(gA, gV), managed.GetGlyphKernAdvance(gA, gV));
        Assert.Equal(oracle.GetGlyphKernAdvance(gT, go), managed.GetGlyphKernAdvance(gT, go));
        Assert.Equal(oracle.GetGlyphKernAdvance(gV, gA), managed.GetGlyphKernAdvance(gV, gA));
    }

    [Theory]
    [MemberData(nameof(CorpusFontRelativePaths))]
    public void GlyphOutlinesMatchNativeOracleExactly(string relativeFontPath)
    {
        byte[]? data = LoadCorpusFontOrSkip(relativeFontPath);
        if (data is null)
        {
            return;
        }

        using OracleTrueTypeFont oracle = StbOracle.OpenFont(data, 0)!;
        StbTrueTypeFont managed = StbTrueTypeFont.Load(data);

        GlyphVertex[] scratchBuf = new GlyphVertex[512];

        // Check all ASCII + accented compound glyphs (Á, É, Ö, Ü, ñ, ç, Ä).
        List<int> codepoints = new(120);
        for (int cp = 32; cp <= 126; ++cp)
        {
            codepoints.Add(cp);
        }

        codepoints.AddRange(AccentedCodepoints);

        foreach (int cp in codepoints)
        {
            int g = managed.FindGlyphIndex(cp);
            OracleFontVertex[] expected = oracle.GetGlyphShape(g);
            GlyphVertex[] actual = managed.GetGlyphShape(g);

            Assert.Equal(expected.Length, actual.Length);
            for (int i = 0; i < expected.Length; ++i)
            {
                Assert.Equal(expected[i].Type, (byte)actual[i].Type);
                Assert.Equal(expected[i].X, actual[i].X);
                Assert.Equal(expected[i].Y, actual[i].Y);
                if (actual[i].Type is GlyphVertexType.Curve or GlyphVertexType.Cubic)
                {
                    Assert.Equal(expected[i].Cx, actual[i].Cx);
                    Assert.Equal(expected[i].Cy, actual[i].Cy);
                }

                if (actual[i].Type == GlyphVertexType.Cubic)
                {
                    Assert.Equal(expected[i].Cx1, actual[i].Cx1);
                    Assert.Equal(expected[i].Cy1, actual[i].Cy1);
                }
            }

            if (actual.Length <= scratchBuf.Length)
            {
                Assert.True(managed.TryGetGlyphShape(g, scratchBuf, out int count));
                Assert.Equal(actual.Length, count);
                for (int i = 0; i < count; ++i)
                {
                    Assert.Equal(actual[i], scratchBuf[i]);
                }
            }
        }
    }

    [Theory]
    [MemberData(nameof(CorpusFontRelativePaths))]
    public void RasterizationSubpixelAndPrefilterMatchNativeOracle(string relativeFontPath)
    {
        byte[]? data = LoadCorpusFontOrSkip(relativeFontPath);
        if (data is null)
        {
            return;
        }

        using OracleTrueTypeFont oracle = StbOracle.OpenFont(data, 0)!;
        StbTrueTypeFont managed = StbTrueTypeFont.Load(data);

        foreach (float size in RasterSizes)
        {
            float scale = managed.ScaleForPixelHeight(size);

            foreach (char ch in SampleGlyphChars)
            {
                int g = managed.FindGlyphIndex(ch);

                // Standard + subpixel shifted + anisotropic scales.
                VerifyGlyphRaster(oracle, managed, g, scale, scale, 0f, 0f);
                VerifyGlyphRaster(oracle, managed, g, scale, scale, 0.35f, 0.65f);
                VerifyGlyphRaster(oracle, managed, g, scale * 1.25f, scale * 0.9f, 0.25f, 0.0f);
            }
        }

        // Prefilter oversampling checks (2x1, 2x2, 3x2, 4x4) on 32px glyphs.
        float pfScale = managed.ScaleForPixelHeight(32f);
        (int ox, int oy)[] oversamples = [(2, 1), (2, 2), (3, 2), (4, 4)];
        foreach ((int ox, int oy) in oversamples)
        {
            foreach (char ch in PrefilterChars)
            {
                int g = managed.FindGlyphIndex(ch);
                GlyphBitmapBox box = managed.GetGlyphBitmapBoxSubpixel(g, pfScale * ox, pfScale * oy, 0.2f, 0.3f);
                int w = box.Width + ox - 1;
                int h = box.Height + oy - 1;
                if (w <= 0 || h <= 0)
                {
                    continue;
                }

                int stride = w + 5; // test non-tight stride
                byte[] oracleBuf = new byte[stride * h];
                byte[] managedBuf = new byte[stride * h];
                Array.Fill(oracleBuf, (byte)0xCC);
                Array.Fill(managedBuf, (byte)0xCC);

                (float oSubX, float oSubY) = oracle.MakeGlyphBitmapSubpixelPrefilter(
                    oracleBuf, w, h, stride, pfScale * ox, pfScale * oy, 0.2f, 0.3f, ox, oy, g);
                managed.MakeGlyphBitmapSubpixelPrefilter(
                    managedBuf, w, h, stride, pfScale * ox, pfScale * oy, 0.2f, 0.3f, ox, oy, out float mSubX, out float mSubY, g);

                Assert.Equal(oSubX, mSubX, precision: 6);
                Assert.Equal(oSubY, mSubY, precision: 6);
                AssertBitmapsClose(oracleBuf, managedBuf, maxAllowedDiff: 1);
            }
        }
    }

    [Fact]
    public void DirectRasterizeMatchesMakeGlyphBitmap()
    {
        byte[]? data = LoadCorpusFontOrSkip("fonts/imgui/misc/fonts/Roboto-Medium.ttf");
        if (data is null)
        {
            return;
        }

        StbTrueTypeFont font = StbTrueTypeFont.Load(data);
        float scale = font.ScaleForPixelHeight(64f);
        int g = font.FindGlyphIndex('g');
        GlyphVertex[] verts = font.GetGlyphShape(g);
        GlyphBitmapBox box = font.GetGlyphBitmapBox(g, scale, scale);

        byte[] viaFont = new byte[box.Width * box.Height];
        byte[] viaDirect = new byte[box.Width * box.Height];
        font.MakeGlyphBitmap(viaFont, box.Width, box.Height, box.Width, scale, scale, g);
        StbTrueType.Rasterize(
            viaDirect,
            box.Width,
            box.Height,
            box.Width,
            0.35f,
            verts,
            scale,
            scale,
            0f,
            0f,
            box.X0,
            box.Y0,
            invert: true);

        Assert.Equal(viaFont, viaDirect);
    }

    [Fact]
    public void TrueTypeCollectionMatchesNativeOracle()
    {
        byte[]? font0 = LoadCorpusFontOrSkip("fonts/imgui/misc/fonts/Karla-Regular.ttf");
        byte[]? font1 = LoadCorpusFontOrSkip("fonts/imgui/misc/fonts/ProggyClean.ttf");
        if (font0 is null || font1 is null)
        {
            return;
        }

        byte[] ttc = BuildTrueTypeCollection(font0, font1);

        Assert.Equal(2, StbOracle.GetNumberOfFonts(ttc));
        Assert.Equal(2, StbTrueType.GetNumberOfFonts(ttc));

        for (int i = 0; i < 2; ++i)
        {
            int oOff = StbOracle.GetFontOffsetForIndex(ttc, i);
            int mOff = StbTrueType.GetFontOffsetForIndex(ttc, i);
            Assert.Equal(oOff, mOff);
            Assert.True(mOff > 0);

            using OracleTrueTypeFont oracle = StbOracle.OpenFont(ttc, oOff)!;
            StbTrueTypeFont managed = StbTrueTypeFont.Load(ttc, mOff);
            Assert.Equal(oracle.NumGlyphs, managed.NumGlyphs);

            float scale = managed.ScaleForPixelHeight(32f);
            int g = managed.FindGlyphIndex('R');
            VerifyGlyphRaster(oracle, managed, g, scale, scale, 0f, 0f);
        }

        Assert.Equal(-1, StbTrueType.GetFontOffsetForIndex(ttc, 2));
        Assert.Equal(
            StbOracle.FindMatchingFont(ttc, "Karla", (int)FontMacStyle.DontCare),
            StbTrueType.FindMatchingFont(ttc, "Karla", FontMacStyle.DontCare));
    }

    [Theory]
    [InlineData("fonts/imgui/misc/fonts/Roboto-Medium.ttf")]
    [InlineData("fonts/imgui/misc/fonts/DroidSans.ttf")]
    [InlineData("fonts/imgui/misc/fonts/Karla-Regular.ttf")]
    public void SignedDistanceFieldMatchesNativeOracleAndIsDeterministicAcrossParallelism(string relativeFontPath)
    {
        byte[]? data = LoadCorpusFontOrSkip(relativeFontPath);
        if (data is null)
        {
            return;
        }

        using OracleTrueTypeFont oracle = StbOracle.OpenFont(data, 0)!;
        StbTrueTypeFont managed = StbTrueTypeFont.Load(data);
        StbOptions parallelOpts = new() { MaxDegreeOfParallelism = 4 };
        StbOptions serialOpts = new() { MaxDegreeOfParallelism = 1 };

        (float size, int pad, byte onEdge, float distScale)[] configs =
        [
            (32f, 4, 180, 36.0f),
            (48f, 6, 128, 24.0f),
            (28f, 4, 128, -24.0f),
        ];

        foreach ((float size, int pad, byte onEdge, float distScale) in configs)
        {
            float scale = managed.ScaleForPixelHeight(size);
            foreach (char ch in SdfChars)
            {
                int g = managed.FindGlyphIndex(ch);
                OracleGlyphBitmap? expected = oracle.GetGlyphSdf(scale, g, pad, onEdge, distScale);
                byte[]? actualSerial = managed.GetGlyphSdf(scale, g, pad, onEdge, distScale, out GlyphBitmapMetrics mSerial, serialOpts);
                byte[]? actualParallel = managed.GetGlyphSdf(scale, g, pad, onEdge, distScale, out GlyphBitmapMetrics mParallel, parallelOpts);

                if (expected is null)
                {
                    Assert.Null(actualSerial);
                    Assert.Null(actualParallel);
                    continue;
                }

                Assert.NotNull(actualSerial);
                Assert.NotNull(actualParallel);
                Assert.Equal(expected.Width, mSerial.Width);
                Assert.Equal(expected.Height, mSerial.Height);
                Assert.Equal(expected.XOff, mSerial.XOffset);
                Assert.Equal(expected.YOff, mSerial.YOffset);
                Assert.Equal(mSerial, mParallel);

                // Serial and parallel output must be identical.
                Assert.Equal(actualSerial, actualParallel);

                // Match native C oracle within <= 1 LSB (FP rounding at distance boundaries).
                AssertBitmapsClose(expected.Pixels, actualSerial, maxAllowedDiff: 1);
            }
        }
    }

    [Theory]
    [InlineData("fonts/imgui/misc/fonts/Roboto-Medium.ttf")]
    [InlineData("fonts/source-code-pro/OTF/SourceCodePro-Regular.otf")]
    public void BakeFontBitmapAndGetBakedQuadMatchNativeOracle(string relativeFontPath)
    {
        byte[]? data = LoadCorpusFontOrSkip(relativeFontPath);
        if (data is null)
        {
            return;
        }

        const int width = 512;
        const int height = 512;
        const int firstChar = 32;
        const int numChars = 95;

        byte[] oraclePixels = new byte[width * height];
        byte[] managedPixels = new byte[width * height];
        OracleBakedChar[] oracleChars = new OracleBakedChar[numChars];
        BakedChar[] managedChars = new BakedChar[numChars];

        int oRet = StbOracle.BakeFontBitmap(data, 0, 32f, oraclePixels, width, height, firstChar, oracleChars);
        int mRet = StbTrueType.BakeFontBitmap(data, 0, 32f, managedPixels, width, height, firstChar, managedChars);

        Assert.Equal(oRet, mRet);
        Assert.True(mRet > 0);
        AssertBitmapsClose(oraclePixels, managedPixels, maxAllowedDiff: 1);

        for (int i = 0; i < numChars; ++i)
        {
            Assert.Equal(oracleChars[i].X0, managedChars[i].X0);
            Assert.Equal(oracleChars[i].Y0, managedChars[i].Y0);
            Assert.Equal(oracleChars[i].X1, managedChars[i].X1);
            Assert.Equal(oracleChars[i].Y1, managedChars[i].Y1);
            Assert.Equal(oracleChars[i].XOff, managedChars[i].XOff, precision: 5);
            Assert.Equal(oracleChars[i].YOff, managedChars[i].YOff, precision: 5);
            Assert.Equal(oracleChars[i].XAdvance, managedChars[i].XAdvance, precision: 5);
        }

        foreach (bool opengl in BoolValues)
        {
            float ox = 10.25f, oy = 40.5f;
            float mx = 10.25f, my = 40.5f;
            foreach (char c in "Hello, World! 123")
            {
                int idx = c - firstChar;
                OracleAlignedQuad oq = StbOracle.GetBakedQuad(oracleChars, width, height, idx, ref ox, ref oy, opengl);
                AlignedQuad mq = StbTrueType.GetBakedQuad(managedChars, width, height, idx, ref mx, ref my, opengl);
                AssertQuadsEqual(oq, mq);
                Assert.Equal(ox, mx, precision: 5);
                Assert.Equal(oy, my, precision: 5);
            }
        }
    }

    [Theory]
    [InlineData("fonts/imgui/misc/fonts/Roboto-Medium.ttf", 1, 1, 1, false, 32f)]
    [InlineData("fonts/imgui/misc/fonts/Roboto-Medium.ttf", 2, 1, 1, false, 32f)]
    [InlineData("fonts/imgui/misc/fonts/Roboto-Medium.ttf", 2, 2, 2, true, 32f)]
    [InlineData("fonts/imgui/misc/fonts/Karla-Regular.ttf", 3, 2, 1, false, -24f)]
    [InlineData("fonts/source-code-pro/OTF/SourceCodePro-Regular.otf", 2, 2, 1, false, 32f)]
    [InlineData("fonts/source-code-pro/OTF/SourceCodePro-It.otf", 1, 1, 2, true, 28f)]
    public void FontPackerAndGetPackedQuadMatchNativeOracleAndParallelDeterministic(
        string relativeFontPath,
        int hOver,
        int vOver,
        int padding,
        bool skipMissing,
        float fontSize)
    {
        byte[]? data = LoadCorpusFontOrSkip(relativeFontPath);
        if (data is null)
        {
            return;
        }

        const int width = 1024;
        const int height = 1024;
        const int firstChar = 32;
        const int numChars = 95;

        byte[] oraclePixels = new byte[width * height];
        OraclePackedChar[] oracleChars = new OraclePackedChar[numChars];
        bool oOk = StbOracle.PackFontRange(
            data, 0, fontSize, oraclePixels, width, height, 0, padding,
            (uint)hOver, (uint)vOver, skipMissing, firstChar, oracleChars);
        Assert.True(oOk);

        StbTrueTypeFont font = StbTrueTypeFont.Load(data);

        // Serial pack.
        byte[] managedPixelsSerial = new byte[width * height];
        PackedChar[] managedCharsSerial = new PackedChar[numChars];
        using (StbFontPacker packer = StbFontPacker.Begin(
            managedPixelsSerial, width, height, 0, padding, new StbOptions { MaxDegreeOfParallelism = 1 }))
        {
            packer.SetOversampling(hOver, vOver);
            packer.SetSkipMissingCodepoints(skipMissing);
            bool mOk = packer.PackFontRange(font, fontSize, firstChar, managedCharsSerial);
            Assert.True(mOk);
        }

        // Parallel pack: must be identical to serial.
        byte[] managedPixelsParallel = new byte[width * height];
        PackedChar[] managedCharsParallel = new PackedChar[numChars];
        using (StbFontPacker packer = StbFontPacker.Begin(
            managedPixelsParallel, width, height, 0, padding, new StbOptions { MaxDegreeOfParallelism = 4 }))
        {
            packer.SetOversampling(hOver, vOver);
            packer.SetSkipMissingCodepoints(skipMissing);
            bool mOk = packer.PackFontRange(font, fontSize, firstChar, managedCharsParallel);
            Assert.True(mOk);
        }

        Assert.Equal(managedCharsSerial, managedCharsParallel);
        Assert.Equal(managedPixelsSerial, managedPixelsParallel);

        for (int i = 0; i < numChars; ++i)
        {
            Assert.Equal(oracleChars[i].X0, managedCharsSerial[i].X0);
            Assert.Equal(oracleChars[i].Y0, managedCharsSerial[i].Y0);
            Assert.Equal(oracleChars[i].X1, managedCharsSerial[i].X1);
            Assert.Equal(oracleChars[i].Y1, managedCharsSerial[i].Y1);
            Assert.Equal(oracleChars[i].XOff, managedCharsSerial[i].XOff, precision: 4);
            Assert.Equal(oracleChars[i].YOff, managedCharsSerial[i].YOff, precision: 4);
            Assert.Equal(oracleChars[i].XAdvance, managedCharsSerial[i].XAdvance, precision: 4);
            Assert.Equal(oracleChars[i].XOff2, managedCharsSerial[i].XOff2, precision: 4);
            Assert.Equal(oracleChars[i].YOff2, managedCharsSerial[i].YOff2, precision: 4);
        }

        AssertBitmapsClose(oraclePixels, managedPixelsSerial, maxAllowedDiff: 1);

        foreach (bool alignInt in BoolValues)
        {
            float ox = 5.3f, oy = 30.7f;
            float mx = 5.3f, my = 30.7f;
            foreach (char c in "Quick brown fox 0123456789!")
            {
                int idx = c - firstChar;
                OracleAlignedQuad oq = StbOracle.GetPackedQuad(oracleChars, width, height, idx, ref ox, ref oy, alignInt);
                AlignedQuad mq = StbTrueType.GetPackedQuad(managedCharsSerial, width, height, idx, ref mx, ref my, alignInt);
                AssertQuadsEqual(oq, mq);
                Assert.Equal(ox, mx, precision: 4);
                Assert.Equal(oy, my, precision: 4);
            }
        }
    }

    [Fact]
    public void FontPackerExplicitCodepointsMatchNativeOracle()
    {
        byte[]? data = LoadCorpusFontOrSkip("fonts/imgui/misc/fonts/Roboto-Medium.ttf");
        if (data is null)
        {
            return;
        }

        int[] codepoints = ['A', 'B', 'g', 'Q', 0x00C9, 0x00F1, 0x20AC, 0x1F600]; // includes missing emoji
        const int width = 256;
        const int height = 256;

        byte[] oraclePixels = new byte[width * height];
        OraclePackedChar[] oracleChars = new OraclePackedChar[codepoints.Length];
        // Note: upstream stb_truetype returns 0 (false) when skip_missing=true skips any codepoint,
        // while still packing all present codepoints.
        bool oOk = StbOracle.PackFontCodepoints(
            data, 0, 36f, oraclePixels, width, height, 0, 2, 2, 2, true, codepoints, oracleChars);
        Assert.False(oOk);

        StbTrueTypeFont font = StbTrueTypeFont.Load(data);
        byte[] managedPixels = new byte[width * height];
        PackedChar[] managedChars = new PackedChar[codepoints.Length];
        using (StbFontPacker packer = StbFontPacker.Begin(managedPixels, width, height, 0, 2))
        {
            packer.SetOversampling(2, 2);
            packer.SetSkipMissingCodepoints(true);
            FontPackRange[] ranges =
            [
                new FontPackRange
                {
                    FontSize = 36f,
                    Codepoints = codepoints,
                    CharData = managedChars,
                },
            ];
            bool mOk = packer.PackFontRanges(font, ranges);
            Assert.Equal(oOk, mOk);
        }

        for (int i = 0; i < codepoints.Length; ++i)
        {
            Assert.Equal(oracleChars[i].X0, managedChars[i].X0);
            Assert.Equal(oracleChars[i].Y0, managedChars[i].Y0);
            Assert.Equal(oracleChars[i].X1, managedChars[i].X1);
            Assert.Equal(oracleChars[i].Y1, managedChars[i].Y1);
            Assert.Equal(oracleChars[i].XAdvance, managedChars[i].XAdvance, precision: 4);
        }

        AssertBitmapsClose(oraclePixels, managedPixels, maxAllowedDiff: 1);
    }

    [Fact]
    public void CompareUtf8ToUtf16BigEndianMatchesNativeOracle()
    {
        string[] samples = ["Roboto", "Source Code Pro", "Café", "日本語", ""];
        foreach (string s in samples)
        {
            byte[] utf8 = Encoding.UTF8.GetBytes(s);
            byte[] utf16Be = Encoding.BigEndianUnicode.GetBytes(s);
            Assert.Equal(
                StbOracle.CompareUtf8ToUtf16BigEndian(utf8, utf16Be),
                StbTrueType.CompareUtf8ToUtf16BigEndian(utf8, utf16Be));
            Assert.True(StbTrueType.CompareUtf8ToUtf16BigEndian(utf8, utf16Be));

            if (s.Length > 0)
            {
                byte[] wrongUtf16 = Encoding.BigEndianUnicode.GetBytes(s + "!");
                Assert.Equal(
                    StbOracle.CompareUtf8ToUtf16BigEndian(utf8, wrongUtf16),
                    StbTrueType.CompareUtf8ToUtf16BigEndian(utf8, wrongUtf16));
                Assert.False(StbTrueType.CompareUtf8ToUtf16BigEndian(utf8, wrongUtf16));
            }
        }
    }

    private static void VerifyCodepointMetrics(OracleTrueTypeFont oracle, StbTrueTypeFont managed, int cp)
    {
        int og = oracle.FindGlyphIndex(cp);
        int mg = managed.FindGlyphIndex(cp);
        Assert.Equal(og, mg);

        (int oAdv, int oLsb) = oracle.GetGlyphHMetrics(og);
        GlyphHMetrics mhm = managed.GetGlyphHMetrics(mg);
        Assert.Equal(oAdv, mhm.AdvanceWidth);
        Assert.Equal(oLsb, mhm.LeftSideBearing);

        bool oHasBox = oracle.TryGetGlyphBox(og, out int ox0, out int oy0, out int ox1, out int oy1);
        bool mHasBox = managed.TryGetGlyphBox(mg, out GlyphBox mbox);
        Assert.Equal(oHasBox, mHasBox);
        if (oHasBox)
        {
            Assert.Equal(ox0, mbox.X0);
            Assert.Equal(oy0, mbox.Y0);
            Assert.Equal(ox1, mbox.X1);
            Assert.Equal(oy1, mbox.Y1);
        }

        Assert.Equal(oracle.IsGlyphEmpty(og), managed.IsGlyphEmpty(mg));
    }

    private static void VerifyGlyphRaster(
        OracleTrueTypeFont oracle,
        StbTrueTypeFont managed,
        int glyphIndex,
        float scaleX,
        float scaleY,
        float shiftX,
        float shiftY)
    {
        (int ox0, int oy0, int ox1, int oy1) = oracle.GetGlyphBitmapBoxSubpixel(glyphIndex, scaleX, scaleY, shiftX, shiftY);
        GlyphBitmapBox mbox = managed.GetGlyphBitmapBoxSubpixel(glyphIndex, scaleX, scaleY, shiftX, shiftY);
        Assert.Equal(ox0, mbox.X0);
        Assert.Equal(oy0, mbox.Y0);
        Assert.Equal(ox1, mbox.X1);
        Assert.Equal(oy1, mbox.Y1);

        OracleGlyphBitmap? oBmp = oracle.GetGlyphBitmapSubpixel(scaleX, scaleY, shiftX, shiftY, glyphIndex);
        byte[]? mBmp = managed.GetGlyphBitmapSubpixel(scaleX, scaleY, shiftX, shiftY, glyphIndex, out GlyphBitmapMetrics mMetrics);

        if (oBmp is null)
        {
            Assert.Empty(mBmp);
            Assert.Equal(0, mMetrics.Width);
            Assert.Equal(0, mMetrics.Height);
            return;
        }

        Assert.NotNull(mBmp);
        Assert.Equal(oBmp.Width, mMetrics.Width);
        Assert.Equal(oBmp.Height, mMetrics.Height);
        Assert.Equal(oBmp.XOff, mMetrics.XOffset);
        Assert.Equal(oBmp.YOff, mMetrics.YOffset);
        AssertBitmapsClose(oBmp.Pixels, mBmp, maxAllowedDiff: 1);

        // Also test caller-owned MakeGlyphBitmapSubpixel with non-tight stride.
        int stride = mMetrics.Width + 3;
        byte[] oCaller = new byte[stride * mMetrics.Height];
        byte[] mCaller = new byte[stride * mMetrics.Height];
        oracle.MakeGlyphBitmapSubpixel(oCaller, mMetrics.Width, mMetrics.Height, stride, scaleX, scaleY, shiftX, shiftY, glyphIndex);
        managed.MakeGlyphBitmapSubpixel(mCaller, mMetrics.Width, mMetrics.Height, stride, scaleX, scaleY, shiftX, shiftY, glyphIndex);
        AssertBitmapsClose(oCaller, mCaller, maxAllowedDiff: 1);
    }

    private static void AssertBitmapsClose(ReadOnlySpan<byte> expected, ReadOnlySpan<byte> actual, int maxAllowedDiff)
    {
        Assert.Equal(expected.Length, actual.Length);
        int maxDiff = 0;
        int diffCount = 0;
        for (int i = 0; i < expected.Length; ++i)
        {
            int d = Math.Abs(expected[i] - actual[i]);
            if (d > 0)
            {
                diffCount++;
                if (d > maxDiff)
                {
                    maxDiff = d;
                }
            }
        }

        Assert.True(
            maxDiff <= maxAllowedDiff,
            $"Bitmap mismatch: maxDiff={maxDiff} (allowed {maxAllowedDiff}), diffCount={diffCount}/{expected.Length}.");
    }

    private static void AssertQuadsEqual(OracleAlignedQuad expected, AlignedQuad actual)
    {
        Assert.Equal(expected.X0, actual.X0, precision: 4);
        Assert.Equal(expected.Y0, actual.Y0, precision: 4);
        Assert.Equal(expected.S0, actual.S0, precision: 5);
        Assert.Equal(expected.T0, actual.T0, precision: 5);
        Assert.Equal(expected.X1, actual.X1, precision: 4);
        Assert.Equal(expected.Y1, actual.Y1, precision: 4);
        Assert.Equal(expected.S1, actual.S1, precision: 5);
        Assert.Equal(expected.T1, actual.T1, precision: 5);
    }

    private static byte[]? LoadCorpusFontOrSkip(string relativeFontPath)
    {
        string? fullPath = CorpusHelper.TryGetFontPath(relativeFontPath);
        if (fullPath is null)
        {
            Assert.False(CorpusHelper.IsRunningInCi, $"Corpus font '{relativeFontPath}' is missing in CI.");
            return null;
        }

        return File.ReadAllBytes(fullPath);
    }

    private static byte[] BuildLegacyKernFormat0Table((int G1, int G2, short Adv)[] sortedPairs)
    {
        int subtableLen = 14 + sortedPairs.Length * 6;
        byte[] table = new byte[4 + subtableLen];
        BinaryPrimitives.WriteUInt16BigEndian(table.AsSpan(0), 0); // version
        BinaryPrimitives.WriteUInt16BigEndian(table.AsSpan(2), 1); // nTables
        BinaryPrimitives.WriteUInt16BigEndian(table.AsSpan(4), 0); // subtable version
        BinaryPrimitives.WriteUInt16BigEndian(table.AsSpan(6), (ushort)subtableLen);
        BinaryPrimitives.WriteUInt16BigEndian(table.AsSpan(8), 0x0001); // format 0, horizontal
        BinaryPrimitives.WriteUInt16BigEndian(table.AsSpan(10), (ushort)sortedPairs.Length);
        BinaryPrimitives.WriteUInt16BigEndian(table.AsSpan(12), 0); // searchRange
        BinaryPrimitives.WriteUInt16BigEndian(table.AsSpan(14), 0); // entrySelector
        BinaryPrimitives.WriteUInt16BigEndian(table.AsSpan(16), 0); // rangeShift
        int off = 18;
        foreach ((int g1, int g2, short adv) in sortedPairs)
        {
            BinaryPrimitives.WriteUInt16BigEndian(table.AsSpan(off), (ushort)g1);
            BinaryPrimitives.WriteUInt16BigEndian(table.AsSpan(off + 2), (ushort)g2);
            BinaryPrimitives.WriteInt16BigEndian(table.AsSpan(off + 4), adv);
            off += 6;
        }

        return table;
    }

    private static byte[] AppendSfntTable(byte[] originalTtf, string tag, byte[] tablePayload)
    {
        int numTables = BinaryPrimitives.ReadUInt16BigEndian(originalTtf.AsSpan(4));

        // Collect all existing tables and add the new one (replacing if tag already exists).
        List<(string Tag, byte[] Data)> tables = new(numTables + 1);
        for (int i = 0; i < numTables; ++i)
        {
            int entry = 12 + i * 16;
            string t = Encoding.ASCII.GetString(originalTtf, entry, 4);
            if (string.Equals(t, tag, StringComparison.Ordinal))
            {
                continue;
            }

            uint offset = BinaryPrimitives.ReadUInt32BigEndian(originalTtf.AsSpan(entry + 8));
            uint length = BinaryPrimitives.ReadUInt32BigEndian(originalTtf.AsSpan(entry + 12));
            byte[] slice = originalTtf.AsSpan((int)offset, (int)length).ToArray();
            tables.Add((t, slice));
        }

        tables.Add((tag, tablePayload));
        tables.Sort((a, b) => string.CompareOrdinal(a.Tag, b.Tag));

        int totalSize = 12 + tables.Count * 16;
        foreach ((_, byte[] d) in tables)
        {
            totalSize += (d.Length + 3) & ~3;
        }

        byte[] output = new byte[totalSize];
        originalTtf.AsSpan(0, 4).CopyTo(output); // sfnt version
        BinaryPrimitives.WriteUInt16BigEndian(output.AsSpan(4), (ushort)tables.Count);
        BinaryPrimitives.WriteUInt16BigEndian(output.AsSpan(6), 0);
        BinaryPrimitives.WriteUInt16BigEndian(output.AsSpan(8), 0);
        BinaryPrimitives.WriteUInt16BigEndian(output.AsSpan(10), 0);

        int dataOffset = 12 + tables.Count * 16;
        for (int i = 0; i < tables.Count; ++i)
        {
            (string t, byte[] d) = tables[i];
            int entry = 12 + i * 16;
            Encoding.ASCII.GetBytes(t, output.AsSpan(entry, 4));
            BinaryPrimitives.WriteUInt32BigEndian(output.AsSpan(entry + 4), 0); // checksum
            BinaryPrimitives.WriteUInt32BigEndian(output.AsSpan(entry + 8), (uint)dataOffset);
            BinaryPrimitives.WriteUInt32BigEndian(output.AsSpan(entry + 12), (uint)d.Length);
            d.CopyTo(output.AsSpan(dataOffset));
            dataOffset += (d.Length + 3) & ~3;
        }

        return output;
    }

    private static byte[] BuildTrueTypeCollection(params byte[][] fonts)
    {
        // TTC header (v1.0): "ttcf" (4) + major(2) + minor(2) + numFonts(4) + offsets[numFonts]*4
        int headerSize = 12 + fonts.Length * 4;
        int total = headerSize;
        int[] fontOffsets = new int[fonts.Length];
        for (int i = 0; i < fonts.Length; ++i)
        {
            fontOffsets[i] = total;
            total += (fonts[i].Length + 3) & ~3;
        }

        byte[] ttc = new byte[total];
        Encoding.ASCII.GetBytes("ttcf", ttc.AsSpan(0, 4));
        BinaryPrimitives.WriteUInt16BigEndian(ttc.AsSpan(4), 1);
        BinaryPrimitives.WriteUInt16BigEndian(ttc.AsSpan(6), 0);
        BinaryPrimitives.WriteUInt32BigEndian(ttc.AsSpan(8), (uint)fonts.Length);

        for (int i = 0; i < fonts.Length; ++i)
        {
            int baseOff = fontOffsets[i];
            BinaryPrimitives.WriteUInt32BigEndian(ttc.AsSpan(12 + i * 4), (uint)baseOff);
            byte[] src = fonts[i];
            src.CopyTo(ttc.AsSpan(baseOff));

            // In a TTC, every table offset in the embedded SFNT directory is relative to the start of the TTC file!
            int numTables = BinaryPrimitives.ReadUInt16BigEndian(src.AsSpan(4));
            for (int t = 0; t < numTables; ++t)
            {
                int entryOff = baseOff + 12 + t * 16 + 8;
                uint oldTableOff = BinaryPrimitives.ReadUInt32BigEndian(ttc.AsSpan(entryOff));
                BinaryPrimitives.WriteUInt32BigEndian(ttc.AsSpan(entryOff), oldTableOff + (uint)baseOff);
            }
        }

        return ttc;
    }
}
