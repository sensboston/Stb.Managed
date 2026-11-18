using System.Reflection;
using System.Security;
using Stb.Managed.Dxt;
using Stb.Managed.Perlin;
using Stb.Managed.RectPack;
using Stb.Managed.TrueType;
using Xunit;

namespace Stb.Managed.Tests.Hardening;

public sealed class HardeningAndFuzzTests
{
    private static readonly string[] AllLibraryAssemblies =
    [
        "Stb.Managed.Core",
        "Stb.Managed.Image",
        "Stb.Managed.ImageWrite",
        "Stb.Managed.ImageResize",
        "Stb.Managed.TrueType",
        "Stb.Managed.RectPack",
        "Stb.Managed.Dxt",
        "Stb.Managed.Perlin",
    ];

    [Fact]
    public void AllLibraryAssembliesAreSafeManagedAndAotTrimCompatible()
    {
        foreach (string name in AllLibraryAssemblies)
        {
            string dllPath = Path.Combine(AppContext.BaseDirectory, name + ".dll");
            Assert.True(File.Exists(dllPath), $"Expected library assembly at '{dllPath}'.");
            Assembly asm = Assembly.LoadFrom(dllPath);

            // Verify AllowUnsafeBlocks=false at IL metadata level (no UnverifiableCodeAttribute).
            bool hasUnverifiable = asm.Modules.Any(m => m.GetCustomAttributes<UnverifiableCodeAttribute>().Any());
            Assert.False(hasUnverifiable, $"Assembly '{name}' contains UnverifiableCodeAttribute (unsafe code).");

#if !TEST_NETSTANDARD
            // Verify IsTrimmable=true and IsAotCompatible=true metadata attributes on net8.0 / net10.0.
            Dictionary<string, string?> metadata = asm
                .GetCustomAttributes<AssemblyMetadataAttribute>()
                .GroupBy(a => a.Key, StringComparer.Ordinal)
                .ToDictionary(g => g.Key, g => g.Last().Value, StringComparer.Ordinal);

            Assert.True(
                metadata.TryGetValue("IsTrimmable", out string? trimmable) && string.Equals(trimmable, "True", StringComparison.OrdinalIgnoreCase),
                $"Assembly '{name}' is missing IsTrimmable=True metadata.");
#if NET9_0_OR_GREATER
            // AssemblyMetadata("IsAotCompatible", "True") is emitted by the .NET SDK starting with net9.0+.
            Assert.True(
                metadata.TryGetValue("IsAotCompatible", out string? aot) && string.Equals(aot, "True", StringComparison.OrdinalIgnoreCase),
                $"Assembly '{name}' is missing IsAotCompatible=True metadata.");
#endif
#endif
        }
    }

    [Fact]
    public void ImageDecodersSurviveMutationFuzzAndTruncationWithoutUnhandledExceptions()
    {
        // Build valid seeds across PNG, JPEG, BMP, TGA, HDR using our own encoders.
        const int w = 32;
        const int h = 32;
        byte[] rgba = new byte[w * h * 4];
        float[] hdr = new float[w * h * 3];
        var seedRng = new Random(9911);
        seedRng.NextBytes(rgba);
        for (int i = 0; i < hdr.Length; ++i)
        {
            hdr[i] = (float)seedRng.NextDouble() * 4f;
        }

        byte[][] seeds =
        [
            StbImageWrite.WritePng(rgba, w, h, ColorComponents.RedGreenBlueAlpha),
            StbImageWrite.WriteJpeg(rgba, w, h, ColorComponents.RedGreenBlueAlpha, quality: 80),
            StbImageWrite.WriteBmp(rgba, w, h, ColorComponents.RedGreenBlueAlpha),
            StbImageWrite.WriteTga(rgba, w, h, ColorComponents.RedGreenBlueAlpha),
            StbImageWrite.WriteHdr(hdr, w, h, ColorComponents.RedGreenBlue),
        ];

        var strictOptions = new ImageDecodeOptions
        {
            Options = new StbOptions
            {
                Limits = new StbLimits
                {
                    MaxWidth = 512,
                    MaxHeight = 512,
                    MaxPixels = 512 * 512,
                    MaxAllocationBytes = 4 * 1024 * 1024,
                },
            },
        };

        var rng = new Random(20260901);

        // 1. Structural truncation sweep for each format.
        foreach (byte[] seed in seeds)
        {
            int step = Math.Max(1, seed.Length / 40);
            for (int len = 0; len < seed.Length; len += step)
            {
                ExerciseImageHostilePayload(seed.AsSpan(0, len), strictOptions);
            }

            // 2. Deterministic mutation fuzzing (bit flips, zero runs, 0xFF length bombs, splices).
            for (int iter = 0; iter < 120; ++iter)
            {
                byte[] mutated = seed.ToArray();
                int mutations = 1 + (iter % 6);
                for (int m = 0; m < mutations; ++m)
                {
                    int pos = rng.Next(0, mutated.Length);
                    switch (iter % 4)
                    {
                        case 0:
                            mutated[pos] ^= (byte)(1 << rng.Next(0, 8));
                            break;
                        case 1:
                            mutated[pos] = 0xFF;
                            break;
                        case 2:
                            mutated[pos] = 0x00;
                            break;
                        default:
                            mutated[pos] = (byte)rng.Next(0, 256);
                            break;
                    }
                }

                ExerciseImageHostilePayload(mutated, strictOptions);
            }
        }

        // 3. Pure random noise payloads of varied lengths.
        for (int iter = 0; iter < 100; ++iter)
        {
            byte[] noise = new byte[rng.Next(0, 1024)];
            rng.NextBytes(noise);
            ExerciseImageHostilePayload(noise, strictOptions);
        }
    }

    [Fact]
    public void TrueTypeParserAndRasterizerSurviveMutationFuzzWithoutUnhandledExceptions()
    {
        string? ttfPath = CorpusHelper.TryGetFontPath("fonts/imgui/misc/fonts/Roboto-Medium.ttf");
        string? otfPath = CorpusHelper.TryGetFontPath("fonts/source-code-pro/OTF/SourceCodePro-Regular.otf");
        Assert.NotNull(ttfPath);
        Assert.NotNull(otfPath);
        byte[][] fontSeeds = [File.ReadAllBytes(ttfPath), File.ReadAllBytes(otfPath)];

        var strictOptions = new StbOptions
        {
            Limits = new StbLimits
            {
                MaxWidth = 512,
                MaxHeight = 512,
                MaxPixels = 512 * 512,
                MaxAllocationBytes = 4 * 1024 * 1024,
            },
        };

        var rng = new Random(20260902);
        foreach (byte[] seed in fontSeeds)
        {
            // Truncation boundaries across header, table directory, cmap, glyf/CFF.
            int[] cutLengths = [0, 1, 4, 12, 64, 256, 1024, 4096, seed.Length / 4, seed.Length / 2];
            foreach (int cut in cutLengths)
            {
                if (cut < seed.Length)
                {
                    ExerciseFontHostilePayload(seed.AsSpan(0, cut).ToArray(), strictOptions);
                }
            }

            // Targeted mutations in header/table area and random offsets.
            for (int iter = 0; iter < 80; ++iter)
            {
                byte[] mutated = seed.ToArray();
                int mutations = 1 + (iter % 5);
                for (int m = 0; m < mutations; ++m)
                {
                    int maxOffset = iter < 40 ? Math.Min(mutated.Length, 512) : mutated.Length;
                    int pos = rng.Next(0, maxOffset);
                    mutated[pos] = iter % 3 == 0 ? (byte)0xFF : (byte)rng.Next(0, 256);
                }

                ExerciseFontHostilePayload(mutated, strictOptions);
            }
        }
    }

    [Fact]
    public void AllLibrariesAreThreadSafeAndDeterministicUnderConcurrentExecution()
    {
        const int w = 64;
        const int h = 64;
        byte[] rgba = new byte[w * h * 4];
        for (int i = 0; i < rgba.Length; ++i)
        {
            rgba[i] = (byte)((i * 37 + 13) & 255);
        }

        byte[] pngBytes = StbImageWrite.WritePng(rgba, w, h, ColorComponents.RedGreenBlueAlpha);
        byte[] jpgBytes = StbImageWrite.WriteJpeg(rgba, w, h, ColorComponents.RedGreenBlueAlpha, quality: 88);
        string? robotoPath = CorpusHelper.TryGetFontPath("fonts/imgui/misc/fonts/Roboto-Medium.ttf");
        Assert.NotNull(robotoPath);
        byte[] fontBytes = File.ReadAllBytes(robotoPath);

        // Shared immutable instances that must be safe for concurrent multi-threaded calls.
        StbImageResizer sharedResizer = StbImageResizer.Create(w, h, 32, 32, ResizePixelLayout.Rgba, ResizeDataType.UInt8Srgb);
        StbTrueTypeFont sharedFont = StbTrueTypeFont.Load(fontBytes);
        float fontScale = sharedFont.ScaleForPixelHeight(32f);

        // Compute single-threaded reference baselines.
        byte[] expectedPngDec = StbImage.Load(pngBytes, ColorComponents.RedGreenBlueAlpha).GetRawArray();
        byte[] expectedJpgDec = StbImage.Load(jpgBytes, ColorComponents.RedGreenBlueAlpha).GetRawArray();
        byte[] expectedResize = new byte[32 * 32 * 4];
        sharedResizer.Resize(rgba, expectedResize);
        byte[] expectedGlyph = sharedFont.GetCodepointBitmap(fontScale, fontScale, 'G', out GlyphBitmapMetrics expectedGlyphMetrics);
        byte[] expectedSdf = sharedFont.GetCodepointSdf(fontScale, 'R', 4, 180, 45f, out GlyphBitmapMetrics expectedSdfMetrics);
        byte[] expectedDxt = StbDxt.CompressImageToArray(rgba, w, h, DxtFormat.Bc3);
        float[] expectedPerlin = new float[w * h];
        StbPerlin.FillFbm2D(expectedPerlin, w, h, -1f, 2f, 0.05f, 0.05f, 0.5f, 2f, 0.5f, 4);

        PackRect[] templateRects = new PackRect[80];
        var rng = new Random(707);
        for (int i = 0; i < templateRects.Length; ++i)
        {
            templateRects[i] = new PackRect(rng.Next(4, 24), rng.Next(4, 24), id: i);
        }

        PackRect[] expectedPacked = templateRects.ToArray();
        Assert.True(StbRectPack.PackRects(256, 256, expectedPacked, RectPackHeuristic.SkylineBestFit));

        // Execute all 7 libraries simultaneously across 8 parallel workers.
        Parallel.For(0, 8, new ParallelOptions { MaxDegreeOfParallelism = 8 }, _ =>
        {
            for (int rep = 0; rep < 3; ++rep)
            {
                Assert.Equal(expectedPngDec, StbImage.Load(pngBytes, ColorComponents.RedGreenBlueAlpha).GetRawArray());
                Assert.Equal(expectedJpgDec, StbImage.Load(jpgBytes, ColorComponents.RedGreenBlueAlpha).GetRawArray());
                Assert.Equal(pngBytes, StbImageWrite.WritePng(rgba, w, h, ColorComponents.RedGreenBlueAlpha));

                byte[] resOut = new byte[32 * 32 * 4];
                sharedResizer.Resize(rgba, resOut);
                Assert.Equal(expectedResize, resOut);

                byte[] glyph = sharedFont.GetCodepointBitmap(fontScale, fontScale, 'G', out GlyphBitmapMetrics glyphMetrics);
                Assert.Equal(expectedGlyphMetrics, glyphMetrics);
                Assert.Equal(expectedGlyph, glyph);

                byte[] sdf = sharedFont.GetCodepointSdf(fontScale, 'R', 4, 180, 45f, out GlyphBitmapMetrics sdfMetrics);
                Assert.Equal(expectedSdfMetrics, sdfMetrics);
                Assert.Equal(expectedSdf, sdf);

                byte[] dxt = StbDxt.CompressImageToArray(rgba, w, h, DxtFormat.Bc3);
                Assert.Equal(expectedDxt, dxt);

                float[] perlin = new float[w * h];
                StbPerlin.FillFbm2D(perlin, w, h, -1f, 2f, 0.05f, 0.05f, 0.5f, 2f, 0.5f, 4);
                Assert.Equal(expectedPerlin, perlin);

                PackRect[] rects = templateRects.ToArray();
                Assert.True(StbRectPack.PackRects(256, 256, rects, RectPackHeuristic.SkylineBestFit));
                Assert.Equal(expectedPacked, rects);
            }
        });
    }

    private static void ExerciseImageHostilePayload(ReadOnlySpan<byte> payload, ImageDecodeOptions options)
    {
        _ = StbImage.DetectFormat(payload);
        _ = StbImage.IsHdr(payload);
        _ = StbImage.Is16Bit(payload, options);
        _ = StbImage.TryGetInfo(payload, out _, options);

        try
        {
            _ = StbImage.Load(payload, ColorComponents.Default, options);
        }
        catch (StbException)
        {
            // Expected on malformed or limit-exceeding inputs.
        }

        try
        {
            _ = StbImage.LoadFloat(payload, ColorComponents.Default, options);
        }
        catch (StbException)
        {
            // Expected on malformed or limit-exceeding inputs.
        }
    }

    private static void ExerciseFontHostilePayload(byte[] payload, StbOptions options)
    {
        try
        {
            if (StbTrueTypeFont.TryLoad(payload, 0, out StbTrueTypeFont? font, options) && font is not null)
            {
                float scale = font.ScaleForPixelHeight(24f);
                if (float.IsFinite(scale) && scale > 0f && scale < 10f)
                {
                    _ = font.GetFontVMetrics();
                    int g = font.FindGlyphIndex('A');
                    if (g >= 0 && g < font.NumGlyphs)
                    {
                        _ = font.GetGlyphHMetrics(g);
                        _ = font.GetGlyphBitmap(scale, scale, g, out _, options);
                    }
                }
            }
        }
        catch (StbException)
        {
            // Expected on corrupted glyph outlines / tables / limit violations.
        }
    }
}
