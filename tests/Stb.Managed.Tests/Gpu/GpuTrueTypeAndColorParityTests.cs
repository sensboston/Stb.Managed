using Stb.Managed.Gpu;
using Stb.Managed.Oracle;
using Stb.Managed.TrueType;
using Xunit;

namespace Stb.Managed.Tests.Gpu;

[Collection(GpuTestSuite.Name)]
public sealed class GpuTrueTypeAndColorParityTests(GpuTestFixture fixture)
{
    [Theory]
    [InlineData("fonts/imgui/misc/fonts/Roboto-Medium.ttf")]
    [InlineData("fonts/imgui/misc/fonts/ProggyClean.ttf")]
    [InlineData("fonts/imgui/misc/fonts/Karla-Regular.ttf")]
    public void RenderCodepointSdfBatchMatchesCpuAndNativeOracle(string relativeFontPath)
    {
        string? fullPath = CorpusHelper.TryGetFontPath(relativeFontPath);
        if (fullPath is null)
        {
            Assert.False(CorpusHelper.IsRunningInCi, $"Corpus font '{relativeFontPath}' is missing in CI.");
            return;
        }

        byte[] fontBytes = File.ReadAllBytes(fullPath);
        StbTrueTypeFont font = StbTrueTypeFont.Load(fontBytes);
        using OracleTrueTypeFont oracle = StbOracle.OpenFont(fontBytes, 0)!;

        // Printable ASCII subset covering straight lines, quadratic curves, holes, and whitespace (' ').
        const string chars = " ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789@#%&?!";
        int[] codepoints = new int[chars.Length];
        for (int i = 0; i < chars.Length; ++i)
        {
            codepoints[i] = chars[i];
        }

        float scale = font.ScaleForPixelHeight(48.0f);
        const int padding = 5;
        const byte onEdge = 180;
        const float pixelDistScale = 36.0f;

        GpuSdfGlyphResult[] gpuResults = StbGpuTrueType.RenderCodepointSdfBatch(
            fixture.Context, font, scale, codepoints, padding, onEdge, pixelDistScale, StbGpuExecutionMode.ForceGpu);
        Assert.Equal(codepoints.Length, gpuResults.Length);

        for (int i = 0; i < codepoints.Length; ++i)
        {
            GpuSdfGlyphResult r = gpuResults[i];
            byte[] cpuPixels = font.GetCodepointSdf(scale, codepoints[i], padding, onEdge, pixelDistScale, out GlyphBitmapMetrics cpuMetrics);

            Assert.Equal(cpuMetrics, r.Metrics);
            Assert.Equal(cpuPixels.Length, r.Pixels.Length);

            if (cpuPixels.Length == 0)
            {
                continue;
            }

            AssertBytesClose(cpuPixels, r.Pixels, maxAllowedDiff: 1);

            OracleGlyphBitmap? native = oracle.GetGlyphSdf(scale, r.GlyphIndex, padding, onEdge, pixelDistScale);
            Assert.NotNull(native);
            Assert.Equal(native.Width, r.Metrics.Width);
            Assert.Equal(native.Height, r.Metrics.Height);
            AssertBytesClose(native.Pixels, r.Pixels, maxAllowedDiff: 1);
        }
    }

    [Fact]
    public void GenerateSdfAtlasPacksAsciiAndMatchesIndividualGlyphs()
    {
        string? fullPath = CorpusHelper.TryGetFontPath("fonts/imgui/misc/fonts/Roboto-Medium.ttf");
        if (fullPath is null)
        {
            Assert.False(CorpusHelper.IsRunningInCi, "Roboto-Medium.ttf is missing in CI.");
            return;
        }

        StbTrueTypeFont font = StbTrueTypeFont.Load(File.ReadAllBytes(fullPath));
        int[] ascii = new int[95];
        for (int i = 0; i < 95; ++i)
        {
            ascii[i] = 32 + i;
        }

        GpuSdfAtlas atlas = StbGpuTrueType.GenerateSdfAtlas(
            fixture.Context, font, pixelHeight: 40.0f, ascii, atlasWidth: 512, atlasHeight: 512,
            padding: 4, onEdgeValue: 180, pixelDistScale: 36.0f, glyphGap: 1, modeOverride: StbGpuExecutionMode.ForceGpu);

        Assert.True(atlas.AllPacked);
        Assert.Equal(512, atlas.Width);
        Assert.Equal(512, atlas.Height);
        Assert.Equal(512 * 512, atlas.Pixels.Length);
        Assert.Equal(95, atlas.Glyphs.Length);

        // Verify single-codepoint helper (ForceGpu & ForceCpu fallback).
        byte[] singleGpu = StbGpuTrueType.GetCodepointSdf(fixture.Context, font, font.ScaleForPixelHeight(40.0f), 'G', 4, 180, 36.0f, out GlyphBitmapMetrics mGpu, StbGpuExecutionMode.ForceGpu);
        byte[] singleCpu = StbGpuTrueType.GetCodepointSdf(fixture.Context, font, font.ScaleForPixelHeight(40.0f), 'G', 4, 180, 36.0f, out GlyphBitmapMetrics mCpu, StbGpuExecutionMode.ForceCpu);
        Assert.Equal(mCpu, mGpu);
        AssertBytesClose(singleCpu, singleGpu, maxAllowedDiff: 1);

        float scale = font.ScaleForPixelHeight(40.0f);
        foreach (GpuSdfAtlasGlyph g in atlas.Glyphs)
        {
            Assert.True(g.WasPacked);
            if (g.Width == 0 || g.Height == 0)
            {
                continue;
            }

            Assert.True(g.AtlasX >= 0 && g.AtlasX + g.Width <= atlas.Width);
            Assert.True(g.AtlasY >= 0 && g.AtlasY + g.Height <= atlas.Height);

            byte[] cpu = font.GetCodepointSdf(scale, g.Codepoint, 4, 180, 36.0f, out GlyphBitmapMetrics m);
            Assert.Equal(m.Width, g.Width);
            Assert.Equal(m.Height, g.Height);

            for (int row = 0; row < g.Height; ++row)
            {
                for (int col = 0; col < g.Width; ++col)
                {
                    byte atlasPx = atlas.Pixels[(g.AtlasY + row) * atlas.Width + g.AtlasX + col];
                    byte cpuPx = cpu[row * g.Width + col];
                    Assert.True(Math.Abs(atlasPx - cpuPx) <= 1);
                }
            }
        }
    }

    [Fact]
    public void ColorConversionsIntegerAndLutPathsAreBitExactWithCpu()
    {
        const int pixelCount = 4096;
        byte[] rgba = new byte[pixelCount * 4];
        byte[] rgb = new byte[pixelCount * 3];
        byte[] grey = new byte[pixelCount];

        uint s = 0x12345678u;
        for (int i = 0; i < rgba.Length; ++i)
        {
            s = s * 1664525u + 1013904223u;
            rgba[i] = (byte)(s >> 24);
        }

        for (int i = 0; i < rgb.Length; ++i)
        {
            rgb[i] = rgba[i];
        }

        for (int i = 0; i < grey.Length; ++i)
        {
            grey[i] = rgba[i * 4];
        }

        // 1. SwapRedBlueRgba
        byte[] cpuBuf4 = new byte[pixelCount * 4];
        byte[] gpuBuf4 = new byte[pixelCount * 4];
        StbGpuColor.SwapRedBlueRgba(fixture.Context, rgba, cpuBuf4, pixelCount, StbGpuExecutionMode.ForceCpu);
        StbGpuColor.SwapRedBlueRgba(fixture.Context, rgba, gpuBuf4, pixelCount, StbGpuExecutionMode.ForceGpu);
        Assert.Equal(cpuBuf4, gpuBuf4);

        // 2. RgbToRgba
        StbGpuColor.RgbToRgba(fixture.Context, rgb, cpuBuf4, pixelCount, alpha: 200, StbGpuExecutionMode.ForceCpu);
        StbGpuColor.RgbToRgba(fixture.Context, rgb, gpuBuf4, pixelCount, alpha: 200, StbGpuExecutionMode.ForceGpu);
        Assert.Equal(cpuBuf4, gpuBuf4);

        // 3. RgbaToRgb
        byte[] cpuBuf3 = new byte[pixelCount * 3];
        byte[] gpuBuf3 = new byte[pixelCount * 3];
        StbGpuColor.RgbaToRgb(fixture.Context, rgba, cpuBuf3, pixelCount, StbGpuExecutionMode.ForceCpu);
        StbGpuColor.RgbaToRgb(fixture.Context, rgba, gpuBuf3, pixelCount, StbGpuExecutionMode.ForceGpu);
        Assert.Equal(cpuBuf3, gpuBuf3);

        // 4. RgbaToGrey
        byte[] cpuBuf1 = new byte[pixelCount];
        byte[] gpuBuf1 = new byte[pixelCount];
        StbGpuColor.RgbaToGrey(fixture.Context, rgba, cpuBuf1, pixelCount, StbGpuExecutionMode.ForceCpu);
        StbGpuColor.RgbaToGrey(fixture.Context, rgba, gpuBuf1, pixelCount, StbGpuExecutionMode.ForceGpu);
        Assert.Equal(cpuBuf1, gpuBuf1);

        // 5. GreyToRgba
        StbGpuColor.GreyToRgba(fixture.Context, grey, cpuBuf4, pixelCount, alpha: 180, StbGpuExecutionMode.ForceCpu);
        StbGpuColor.GreyToRgba(fixture.Context, grey, gpuBuf4, pixelCount, alpha: 180, StbGpuExecutionMode.ForceGpu);
        Assert.Equal(cpuBuf4, gpuBuf4);

        // 6. PremultiplyAlpha & UnpremultiplyAlpha
        StbGpuColor.PremultiplyAlpha(fixture.Context, rgba, cpuBuf4, pixelCount, StbGpuExecutionMode.ForceCpu);
        StbGpuColor.PremultiplyAlpha(fixture.Context, rgba, gpuBuf4, pixelCount, StbGpuExecutionMode.ForceGpu);
        Assert.Equal(cpuBuf4, gpuBuf4);

        byte[] cpuUnpre = new byte[pixelCount * 4];
        byte[] gpuUnpre = new byte[pixelCount * 4];
        StbGpuColor.UnpremultiplyAlpha(fixture.Context, cpuBuf4, cpuUnpre, pixelCount, StbGpuExecutionMode.ForceCpu);
        StbGpuColor.UnpremultiplyAlpha(fixture.Context, gpuBuf4, gpuUnpre, pixelCount, StbGpuExecutionMode.ForceGpu);
        Assert.Equal(cpuUnpre, gpuUnpre);

        // 7. SrgbToLinearFp32 & LinearFp32ToSrgb (exact LUT match -> bit-exact!).
        float[] cpuLin = new float[pixelCount * 4];
        float[] gpuLin = new float[pixelCount * 4];
        StbGpuColor.SrgbToLinearFp32(fixture.Context, rgba, cpuLin, pixelCount, channels: 4, linearAlpha: true, StbGpuExecutionMode.ForceCpu);
        StbGpuColor.SrgbToLinearFp32(fixture.Context, rgba, gpuLin, pixelCount, channels: 4, linearAlpha: true, StbGpuExecutionMode.ForceGpu);
        Assert.Equal(cpuLin, gpuLin);

        byte[] cpuSrgb = new byte[pixelCount * 4];
        byte[] gpuSrgb = new byte[pixelCount * 4];
        StbGpuColor.LinearFp32ToSrgb(fixture.Context, cpuLin, cpuSrgb, pixelCount, channels: 4, linearAlpha: true, StbGpuExecutionMode.ForceCpu);
        StbGpuColor.LinearFp32ToSrgb(fixture.Context, gpuLin, gpuSrgb, pixelCount, channels: 4, linearAlpha: true, StbGpuExecutionMode.ForceGpu);
        Assert.Equal(cpuSrgb, gpuSrgb);

        // 8. VRAM-resident SwapRedBlueGpuImage matches host SwapRedBlueRgba.
        using StbGpuImage<byte> vramRgba = fixture.Context.Upload(rgba, 64, 64, ColorComponents.RedGreenBlueAlpha);
        using StbGpuImage<byte> vramBgra = StbGpuColor.SwapRedBlueGpuImage(fixture.Context, vramRgba);
        StbGpuColor.SwapRedBlueRgba(fixture.Context, rgba, cpuBuf4, pixelCount, StbGpuExecutionMode.ForceCpu);
        Assert.Equal(cpuBuf4, vramBgra.DownloadToArray());
    }

    private static void AssertBytesClose(byte[] expected, byte[] actual, int maxAllowedDiff)
    {
        Assert.Equal(expected.Length, actual.Length);
        int maxDiff = 0;
        int worstIdx = -1;
        for (int i = 0; i < expected.Length; ++i)
        {
            int diff = Math.Abs(expected[i] - actual[i]);
            if (diff > maxDiff)
            {
                maxDiff = diff;
                worstIdx = i;
            }
        }

        Assert.True(
            maxDiff <= maxAllowedDiff,
            $"Max byte diff {maxDiff} exceeded {maxAllowedDiff} at index {worstIdx} (expected {expected[Math.Max(0, worstIdx)]}, actual {actual[Math.Max(0, worstIdx)]}).");
    }
}
