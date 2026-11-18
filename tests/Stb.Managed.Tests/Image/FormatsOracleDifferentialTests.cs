using Stb.Managed.Oracle;
using Xunit;

namespace Stb.Managed.Tests.Image;

public sealed class FormatsOracleDifferentialTests
{
    private static readonly ColorComponents[] AllDesiredComponents =
    [
        ColorComponents.Default,
        ColorComponents.Grey,
        ColorComponents.GreyAlpha,
        ColorComponents.RedGreenBlue,
        ColorComponents.RedGreenBlueAlpha,
    ];

    private static readonly ColorComponents[] DefaultAndRgba =
    [
        ColorComponents.Default,
        ColorComponents.RedGreenBlueAlpha,
    ];

    private static readonly ColorComponents[] DefaultGreyAndRgba =
    [
        ColorComponents.Default,
        ColorComponents.Grey,
        ColorComponents.RedGreenBlueAlpha,
    ];

    private static readonly (string SubDir, string Pattern, ImageFormat ExpectedFormat)[] OracleValidGroups =
    [
        (Path.Combine("generated", "bmp_valid"), "*.bmp", ImageFormat.Bmp),
        (Path.Combine("generated", "tga_valid"), "*.tga", ImageFormat.Tga),
        (Path.Combine("generated", "gif_valid"), "*.gif", ImageFormat.Gif),
        (Path.Combine("generated", "psd_valid"), "*.psd", ImageFormat.Psd),
        (Path.Combine("generated", "hdr_valid"), "*.hdr", ImageFormat.Hdr),
        (Path.Combine("generated", "pnm_valid"), "*.*", ImageFormat.Pnm),
        (Path.Combine("stb", "tests", "local_fuzz", "image_seeds", "hdr"), "*.hdr", ImageFormat.Hdr),
        (Path.Combine("stb", "tests", "local_fuzz", "image_seeds", "psd"), "*.psd", ImageFormat.Psd),
        (Path.Combine("stb", "tests", "local_fuzz", "image_seeds", "pnm"), "*.*", ImageFormat.Pnm),
        (Path.Combine("stb", "tests", "pbm"), "*.*", ImageFormat.Pnm),
    ];

    [Fact]
    public void AllCorpusFilesMatchOracleIn8Bit16BitFloatAndInfo()
    {
        string? corpusRoot = CorpusHelper.TryGetCorpusRoot();
        if (corpusRoot is null)
        {
            Assert.False(CorpusHelper.IsRunningInCi, "Corpus directory is missing in CI.");
            return;
        }

        int totalTested = 0;

        foreach ((string subDir, string pattern, ImageFormat expectedFormat) in OracleValidGroups)
        {
            string fullDir = Path.Combine(corpusRoot, subDir);
            Assert.True(Directory.Exists(fullDir), $"Expected corpus directory '{fullDir}'.");

            string[] files = Directory.GetFiles(fullDir, pattern);
            Array.Sort(files, StringComparer.Ordinal);
            Assert.NotEmpty(files);

            foreach (string file in files)
            {
                string label = Path.Combine(subDir, Path.GetFileName(file));
                byte[] data = File.ReadAllBytes(file);

                // 1. Format detection and metadata
                ImageFormat detected = StbImage.DetectFormat(data);
                Assert.Equal(expectedFormat, detected);

                OracleImageInfo? oracleInfo = StbOracle.GetImageInfo(data);
                Assert.True(oracleInfo.HasValue, $"Oracle info failed for '{label}'.");
                Assert.Equal(MapOracleType(oracleInfo.Value.Type), detected);

                ImageInfo info = StbImage.GetInfo(data);
                Assert.True(StbImage.TryGetInfo(data, out ImageInfo tryInfo), $"TryGetInfo failed for '{label}'.");
                Assert.Equal(info, tryInfo);

                // Known upstream stb_image.h defect in stbi__bmp_info: omits abs(s->img_y) for top-down BMPs
                // whereas stbi__bmp_load uses abs(s->img_y). Stb.Managed consistently reports positive height.
                int expectedInfoHeight = oracleInfo.Value.Type == OracleImageType.Bmp
                    ? Math.Abs(oracleInfo.Value.Height)
                    : oracleInfo.Value.Height;
                Assert.Equal(oracleInfo.Value.Width, info.Width);
                Assert.Equal(expectedInfoHeight, info.Height);
                Assert.Equal(oracleInfo.Value.Channels, (int)info.Components);
                Assert.Equal(oracleInfo.Value.Is16Bit, StbImage.Is16Bit(data));
                Assert.Equal(oracleInfo.Value.Is16Bit, info.BitsPerChannel == 16);
                Assert.Equal(oracleInfo.Value.IsHdr, StbImage.IsHdr(data));
                Assert.Equal(oracleInfo.Value.IsHdr, info.IsHdr);

                // PSD 4-channel files use floating-point white-matte unpremultiplication
                // `(int)(pixel * ra + inv_a)` with truncation (no +0.5f), where C compilers with
                // `-O3` FP contraction (GCC x64, Apple Clang ARM64) differ by ±1 from strict
                // IEEE-754 float32 (MSVC / RyuJIT) on boundary values like 135.999999 vs 136.0.
                // All other formats (and 3-channel PSD) must remain strictly 0-diff byte-exact.
                bool isPsdFloatMatte = expectedFormat == ImageFormat.Psd && info.Components == ColorComponents.RedGreenBlueAlpha;
                int byteTolerance = isPsdFloatMatte ? 1 : 0;
                // When Load16 decodes an 8-bpc PSD, ±1 in 8-bit matte becomes ±257 after (v << 8) | v expansion.
                int ushortTolerance = isPsdFloatMatte ? (info.BitsPerChannel == 16 ? 1 : 257) : 0;

                // 2. 8-bit Load & TryLoad across all desired component counts
                foreach (ColorComponents desired in AllDesiredComponents)
                {
                    OracleImage<byte>? oracle8 = StbOracle.LoadImage(data, desiredChannels: (int)desired);
                    Assert.True(oracle8 is not null, $"Oracle 8-bit load failed for '{label}' desired={desired}.");

                    ImageBuffer<byte> managed8 = StbImage.Load(data, desired);
                    AssertBufferMetadata(oracle8!, managed8, label);
                    AssertBytesEqual(oracle8!.AsSpan(), managed8.AsSpan(), $"{label} (8-bit, desired={desired})", byteTolerance);

                    if (desired == ColorComponents.Default || desired == ColorComponents.RedGreenBlueAlpha)
                    {
                        byte[] tooSmall = new byte[Math.Max(0, managed8.AsSpan().Length - 1)];
                        Assert.False(StbImage.TryLoad(data, tooSmall, out ImageInfo sInfo, out ColorComponents sComp, desired));
                        Assert.Equal(info.Width, sInfo.Width);
                        Assert.Equal(info.Height, sInfo.Height);
                        Assert.Equal(managed8.Components, sComp);

                        byte[] exact = new byte[managed8.AsSpan().Length];
                        Assert.True(StbImage.TryLoad(data, exact, out ImageInfo eInfo, out ColorComponents eComp, desired));
                        Assert.Equal(info, eInfo);
                        Assert.Equal(managed8.Components, eComp);
                        AssertBytesEqual(managed8.AsSpan(), exact, $"{label} (TryLoad vs Load, desired={desired})");
                    }
                }

                // 3. 16-bit Load16 & TryLoad16
                foreach (ColorComponents desired in DefaultAndRgba)
                {
                    OracleImage<ushort>? oracle16 = StbOracle.LoadImage16(data, desiredChannels: (int)desired);
                    Assert.True(oracle16 is not null, $"Oracle 16-bit load failed for '{label}' desired={desired}.");

                    ImageBuffer<ushort> managed16 = StbImage.Load16(data, desired);
                    AssertBufferMetadata(oracle16!, managed16, label);
                    AssertUShortsEqual(oracle16!.AsSpan(), managed16.AsSpan(), $"{label} (16-bit, desired={desired})", ushortTolerance);

                    ushort[] exact16 = new ushort[managed16.AsSpan().Length];
                    Assert.True(StbImage.TryLoad16(data, exact16, out _, out _, desired));
                    AssertUShortsEqual(managed16.AsSpan(), exact16, $"{label} (TryLoad16 vs Load16, desired={desired})");
                }

                // 4. Float LoadFloat & TryLoadFloat
                float floatAbsTol = isPsdFloatMatte ? 0.01f : 0f;
                foreach (ColorComponents desired in DefaultGreyAndRgba)
                {
                    OracleImage<float>? oracleF = StbOracle.LoadImageFloat(data, desiredChannels: (int)desired);
                    Assert.True(oracleF is not null, $"Oracle float load failed for '{label}' desired={desired}.");

                    ImageBuffer<float> managedF = StbImage.LoadFloat(data, desired);
                    AssertBufferMetadata(oracleF!, managedF, label);
                    int maxUlp = expectedFormat == ImageFormat.Hdr ? 0 : 1;
                    AssertFloatsEqual(oracleF!.AsSpan(), managedF.AsSpan(), maxUlp, $"{label} (float, desired={desired})", floatAbsTol);

                    if (desired == ColorComponents.Default)
                    {
                        float[] tooSmallF = new float[Math.Max(0, managedF.AsSpan().Length - 1)];
                        Assert.False(StbImage.TryLoadFloat(data, tooSmallF, out _, out _, desired));

                        float[] exactF = new float[managedF.AsSpan().Length];
                        Assert.True(StbImage.TryLoadFloat(data, exactF, out _, out _, desired));
                        AssertFloatsEqual(managedF.AsSpan(), exactF, 0, $"{label} (TryLoadFloat vs LoadFloat)");
                    }
                }

                // 5. FlipVertically across 8-bit, 16-bit, and float
                ImageDecodeOptions flipOpts = new() { FlipVertically = true };
                OracleImage<byte>? oracleFlip8 = StbOracle.LoadImage(data, desiredChannels: 0, flipVertically: true);
                Assert.NotNull(oracleFlip8);
                ImageBuffer<byte> managedFlip8 = StbImage.Load(data, ColorComponents.Default, flipOpts);
                AssertBytesEqual(oracleFlip8!.AsSpan(), managedFlip8.AsSpan(), $"{label} (8-bit flip)", byteTolerance);

                OracleImage<ushort>? oracleFlip16 = StbOracle.LoadImage16(data, desiredChannels: 0, flipVertically: true);
                Assert.NotNull(oracleFlip16);
                ImageBuffer<ushort> managedFlip16 = StbImage.Load16(data, ColorComponents.Default, flipOpts);
                AssertUShortsEqual(oracleFlip16!.AsSpan(), managedFlip16.AsSpan(), $"{label} (16-bit flip)", ushortTolerance);

                OracleImage<float>? oracleFlipF = StbOracle.LoadImageFloat(data, desiredChannels: 0, flipVertically: true);
                Assert.NotNull(oracleFlipF);
                ImageBuffer<float> managedFlipF = StbImage.LoadFloat(data, ColorComponents.Default, flipOpts);
                AssertFloatsEqual(
                    oracleFlipF!.AsSpan(),
                    managedFlipF.AsSpan(),
                    expectedFormat == ImageFormat.Hdr ? 0 : 1,
                    $"{label} (float flip)",
                    floatAbsTol);

                totalTested++;
            }
        }

        Assert.True(totalTested >= 60, $"Expected at least 60 corpus files, tested {totalTested}.");
    }

    [Fact]
    public void AllGifFilesMatchOracleAnimationFramesAndDelays()
    {
        string? corpusRoot = CorpusHelper.TryGetCorpusRoot();
        if (corpusRoot is null)
        {
            Assert.False(CorpusHelper.IsRunningInCi, "Corpus directory is missing in CI.");
            return;
        }

        string gifDir = Path.Combine(corpusRoot, "generated", "gif_valid");
        string[] files = Directory.GetFiles(gifDir, "*.gif");
        Array.Sort(files, StringComparer.Ordinal);
        Assert.NotEmpty(files);

        int multiFrameCount = 0;
        foreach (string file in files)
        {
            string name = Path.GetFileName(file);
            byte[] data = File.ReadAllBytes(file);

            foreach (ColorComponents desired in AllDesiredComponents)
            {
                OracleGif? oracle = StbOracle.LoadGif(data, desiredChannels: (int)desired);
                Assert.True(oracle is not null, $"Oracle LoadGif failed for '{name}' desired={desired}.");

                GifAnimation managed = StbImage.LoadGifFrames(data, desired);
                Assert.Equal(oracle!.Width, managed.Width);
                Assert.Equal(oracle.Height, managed.Height);
                Assert.Equal(oracle.FrameCount, managed.FrameCount);
                Assert.Equal(oracle.Channels, (int)managed.Components);
                Assert.Equal(oracle.SourceChannels, (int)managed.SourceComponents);
                Assert.Equal(oracle.Delays.ToArray(), managed.Frames.Select(f => f.DelayMs).ToArray());
                AssertBytesEqual(oracle.AsSpan(), managed.AsSpan(), $"{name} (gif anim, desired={desired})");

                int frameStride = managed.Width * managed.Height * (int)managed.Components;
                for (int f = 0; f < managed.FrameCount; f++)
                {
                    GifFrame frame = managed.Frames[f];
                    Assert.Equal(managed.Width, frame.Width);
                    Assert.Equal(managed.Height, frame.Height);
                    Assert.Equal(managed.Components, frame.Components);
                    AssertBytesEqual(
                        oracle.AsSpan().Slice(f * frameStride, frameStride),
                        frame.AsSpan(),
                        $"{name} frame {f} (desired={desired})");
                }

                if (desired == ColorComponents.Default && managed.FrameCount > 1)
                {
                    multiFrameCount++;
                    GifAnimation flipped = StbImage.LoadGifFrames(data, desired, new ImageDecodeOptions { FlipVertically = true });
                    Assert.Equal(managed.FrameCount, flipped.FrameCount);
                    int rowBytes = managed.Width * (int)managed.Components;
                    for (int f = 0; f < managed.FrameCount; f++)
                    {
                        ReadOnlySpan<byte> origFrame = managed.Frames[f].AsSpan();
                        ReadOnlySpan<byte> flipFrame = flipped.Frames[f].AsSpan();
                        for (int y = 0; y < managed.Height; y++)
                        {
                            AssertBytesEqual(
                                origFrame.Slice(y * rowBytes, rowBytes),
                                flipFrame.Slice((managed.Height - 1 - y) * rowBytes, rowBytes),
                                $"{name} frame {f} flipped row {y}");
                        }
                    }
                }
            }
        }

        Assert.True(multiFrameCount >= 2, $"Expected at least 2 multi-frame GIF fixtures, found {multiFrameCount}.");
    }

    [Fact]
    public void NativeWriterRoundTripBmpTgaHdrMatchesOracle()
    {
        const int w = 23;
        const int h = 17;

        byte[] rgba = new byte[w * h * 4];
        byte[] rgb = new byte[w * h * 3];
        float[] hdr = new float[w * h * 3];
        for (int y = 0; y < h; y++)
        {
            for (int x = 0; x < w; x++)
            {
                int p = y * w + x;
                byte r = (byte)((x * 11 + y * 5) & 255);
                byte g = (byte)((x * 7 + y * 13) & 255);
                byte b = (byte)((255 - x * 9 - y * 3) & 255);
                byte a = (byte)(64 + ((x * 17 + y * 19) & 191));
                rgba[p * 4] = r;
                rgba[p * 4 + 1] = g;
                rgba[p * 4 + 2] = b;
                rgba[p * 4 + 3] = a;
                rgb[p * 3] = r;
                rgb[p * 3 + 1] = g;
                rgb[p * 3 + 2] = b;
                hdr[p * 3] = (r / 255f) * 3.5f;
                hdr[p * 3 + 1] = (g / 255f) * 2.0f;
                hdr[p * 3 + 2] = (b / 255f) * 0.75f;
            }
        }

        byte[] bmpData = StbOracle.WriteBmp(w, h, 3, rgb);
        Assert.Equal(ImageFormat.Bmp, StbImage.DetectFormat(bmpData));
        AssertBytesEqual(StbOracle.LoadImage(bmpData)!.AsSpan(), StbImage.Load(bmpData).AsSpan(), "WriteBmp roundtrip");

        byte[] tgaRaw = StbOracle.WriteTga(w, h, 4, rgba, withRle: false);
        Assert.Equal(ImageFormat.Tga, StbImage.DetectFormat(tgaRaw));
        AssertBytesEqual(StbOracle.LoadImage(tgaRaw)!.AsSpan(), StbImage.Load(tgaRaw).AsSpan(), "WriteTga raw roundtrip");

        byte[] tgaRle = StbOracle.WriteTga(w, h, 4, rgba, withRle: true);
        Assert.Equal(ImageFormat.Tga, StbImage.DetectFormat(tgaRle));
        AssertBytesEqual(StbOracle.LoadImage(tgaRle)!.AsSpan(), StbImage.Load(tgaRle).AsSpan(), "WriteTga RLE roundtrip");

        byte[] hdrData = StbOracle.WriteHdr(w, h, 3, hdr);
        Assert.Equal(ImageFormat.Hdr, StbImage.DetectFormat(hdrData));
        AssertFloatsEqual(
            StbOracle.LoadImageFloat(hdrData)!.AsSpan(),
            StbImage.LoadFloat(hdrData).AsSpan(),
            0,
            "WriteHdr float roundtrip");
        AssertBytesEqual(
            StbOracle.LoadImage(hdrData)!.AsSpan(),
            StbImage.Load(hdrData).AsSpan(),
            "WriteHdr LDR tone-mapped roundtrip");
    }

    [Fact]
    public void LoadFloatOnPngAndJpegMatchesOracle()
    {
        string? corpusRoot = CorpusHelper.TryGetCorpusRoot();
        if (corpusRoot is null)
        {
            Assert.False(CorpusHelper.IsRunningInCi, "Corpus directory is missing in CI.");
            return;
        }

        string[] samplePaths =
        [
            Path.Combine(corpusRoot, "generated", "png_valid", "filter_mixed_19x13_rgba.png"),
            Path.Combine(corpusRoot, "generated", "png_valid", "rgb_16bit_13x11.png"),
            Path.Combine(corpusRoot, "generated", "jpeg_valid", "baseline_420_32x32.jpg"),
            Path.Combine(corpusRoot, "generated", "jpeg_valid", "gray_1x1_33x29.jpg"),
        ];

        foreach (string path in samplePaths)
        {
            Assert.True(File.Exists(path), $"Expected sample file '{path}'.");
            byte[] data = File.ReadAllBytes(path);

            foreach (ColorComponents desired in DefaultAndRgba)
            {
                OracleImage<float>? oracle = StbOracle.LoadImageFloat(data, desiredChannels: (int)desired);
                Assert.NotNull(oracle);
                ImageBuffer<float> managed = StbImage.LoadFloat(data, desired);
                AssertBufferMetadata(oracle!, managed, Path.GetFileName(path));
                AssertFloatsEqual(oracle!.AsSpan(), managed.AsSpan(), 1, $"{Path.GetFileName(path)} float desired={desired}");
            }
        }
    }

    [Fact]
    public void CustomToneMappingGammaAndScaleMatchExactFormula()
    {
        string? corpusRoot = CorpusHelper.TryGetCorpusRoot();
        if (corpusRoot is null)
        {
            Assert.False(CorpusHelper.IsRunningInCi, "Corpus directory is missing in CI.");
            return;
        }

        byte[] hdrData = File.ReadAllBytes(Path.Combine(corpusRoot, "generated", "hdr_valid", "hdr_radiance_rle_32x24.hdr"));
        ImageBuffer<float> rawHdr = StbImage.LoadFloat(hdrData);

        const float h2lGamma = 1.8f;
        const float h2lScale = 2.5f;
        ImageDecodeOptions hdrOpts = new() { HdrToLdrGamma = h2lGamma, HdrToLdrScale = h2lScale };
        ImageBuffer<byte> toneMapped = StbImage.Load(hdrData, ColorComponents.Default, hdrOpts);

        ReadOnlySpan<float> srcF = rawHdr.AsSpan();
        ReadOnlySpan<byte> dst8 = toneMapped.AsSpan();
        Assert.Equal(srcF.Length, dst8.Length);
        float scaleInv = 1.0f / h2lScale;
        float gammaInv = 1.0f / h2lGamma;
        for (int i = 0; i < srcF.Length; i++)
        {
            float v = srcF[i] * scaleInv;
            float z = v <= 0f ? 0f : ((float)Math.Pow(v, gammaInv) * 255f) + 0.5f;
            byte expected = z <= 0f ? (byte)0 : (z >= 255f ? (byte)255 : (byte)(int)z);
            Assert.Equal(expected, dst8[i]);
        }

        byte[] pnmData = File.ReadAllBytes(Path.Combine(corpusRoot, "generated", "pnm_valid", "pnm_p6_bin_8bit_11x9.ppm"));
        ImageBuffer<byte> rawLdr = StbImage.Load(pnmData, ColorComponents.RedGreenBlueAlpha);

        const float l2hGamma = 1.6f;
        const float l2hScale = 0.75f;
        ImageDecodeOptions ldrOpts = new() { LdrToHdrGamma = l2hGamma, LdrToHdrScale = l2hScale };
        ImageBuffer<float> hdrFromLdr = StbImage.LoadFloat(pnmData, ColorComponents.RedGreenBlueAlpha, ldrOpts);

        ReadOnlySpan<byte> ldrSpan = rawLdr.AsSpan();
        ReadOnlySpan<float> floatSpan = hdrFromLdr.AsSpan();
        Assert.Equal(ldrSpan.Length, floatSpan.Length);
        for (int i = 0; i < ldrSpan.Length; i += 4)
        {
            for (int c = 0; c < 3; c++)
            {
                float expected = (float)(Math.Pow(ldrSpan[i + c] / 255.0f, l2hGamma) * l2hScale);
                Assert.Equal(expected, floatSpan[i + c]);
            }

            Assert.Equal(ldrSpan[i + 3] / 255.0f, floatSpan[i + 3]);
        }
    }

    [Fact]
    public void PicDecoderDecodesAllValidFixturesCorrectly()
    {
        string? corpusRoot = CorpusHelper.TryGetCorpusRoot();
        if (corpusRoot is null)
        {
            Assert.False(CorpusHelper.IsRunningInCi, "Corpus directory is missing in CI.");
            return;
        }

        string picDir = Path.Combine(corpusRoot, "generated", "pic_valid");
        Assert.True(Directory.Exists(picDir));

        // 1. Uncompressed RGB 13x9
        byte[] rawPic = File.ReadAllBytes(Path.Combine(picDir, "pic_raw_rgb_13x9.pic"));
        Assert.Equal(ImageFormat.Pic, StbImage.DetectFormat(rawPic));
        ImageInfo rawInfo = StbImage.GetInfo(rawPic);
        Assert.Equal(13, rawInfo.Width);
        Assert.Equal(9, rawInfo.Height);
        Assert.Equal(ColorComponents.RedGreenBlue, rawInfo.Components);
        Assert.Equal(8, rawInfo.BitsPerChannel);
        Assert.False(rawInfo.IsHdr);

        ImageBuffer<byte> rawDecoded = StbImage.Load(rawPic);
        ReadOnlySpan<byte> rawPx = rawDecoded.AsSpan();
        for (int y = 0; y < 9; y++)
        {
            for (int x = 0; x < 13; x++)
            {
                int off = ((y * 13) + x) * 3;
                Assert.Equal((byte)((x * 19) & 255), rawPx[off]);
                Assert.Equal((byte)((y * 27) & 255), rawPx[off + 1]);
                Assert.Equal((byte)(((x + y) * 11) & 255), rawPx[off + 2]);
            }
        }

        // 2. Pure RLE RGBA 12x8
        byte[] purePic = File.ReadAllBytes(Path.Combine(picDir, "pic_pure_rle_rgba_12x8.pic"));
        ImageInfo pureInfo = StbImage.GetInfo(purePic);
        Assert.Equal(ColorComponents.RedGreenBlueAlpha, pureInfo.Components);
        ImageBuffer<byte> pureDecoded = StbImage.Load(purePic);
        ReadOnlySpan<byte> purePx = pureDecoded.AsSpan();
        for (int y = 0; y < 8; y++)
        {
            for (int x = 0; x < 12; x++)
            {
                int blk = (x / 4) * 4;
                int off = ((y * 12) + x) * 4;
                Assert.Equal((byte)((blk * 20) & 255), purePx[off]);
                Assert.Equal((byte)((y * 30) & 255), purePx[off + 1]);
                Assert.Equal((byte)180, purePx[off + 2]);
                Assert.Equal((byte)200, purePx[off + 3]);
            }
        }

        // 3. Mixed RLE multi-packet RGBA 15x11
        byte[] mixPic = File.ReadAllBytes(Path.Combine(picDir, "pic_mixed_rle_rgba_multipkt_15x11.pic"));
        ImageBuffer<byte> mixDecoded = StbImage.Load(mixPic);
        Assert.Equal(ColorComponents.RedGreenBlueAlpha, mixDecoded.Components);
        ReadOnlySpan<byte> mixPx = mixDecoded.AsSpan();
        for (int y = 0; y < 11; y++)
        {
            for (int x = 0; x < 15; x++)
            {
                int off = ((y * 15) + x) * 4;
                Assert.Equal((byte)(((x / 2) * 35) & 255), mixPx[off]);
                Assert.Equal((byte)((y * 23) & 255), mixPx[off + 1]);
                Assert.Equal((byte)(((x + y) * 13) & 255), mixPx[off + 2]);
                Assert.Equal((byte)(x < 7 ? 255 : 128), mixPx[off + 3]);
            }
        }

        // 4. Mixed RLE extended >128 run 200x4 + TryLoad + Load16 + LoadFloat + FlipVertically
        byte[] extPic = File.ReadAllBytes(Path.Combine(picDir, "pic_mixed_rle_ext_run_200x4.pic"));
        ImageBuffer<byte> extDecoded = StbImage.Load(extPic);
        Assert.Equal(200, extDecoded.Width);
        Assert.Equal(4, extDecoded.Height);
        Assert.Equal(ColorComponents.RedGreenBlue, extDecoded.Components);
        ReadOnlySpan<byte> extPx = extDecoded.AsSpan();
        for (int y = 0; y < 4; y++)
        {
            for (int x = 0; x < 150; x++)
            {
                int off = ((y * 200) + x) * 3;
                Assert.Equal((byte)((y * 60) & 255), extPx[off]);
                Assert.Equal((byte)100, extPx[off + 1]);
                Assert.Equal((byte)200, extPx[off + 2]);
            }

            for (int x = 0; x < 50; x++)
            {
                int off = ((y * 200) + 150 + x) * 3;
                Assert.Equal((byte)((x * 5) & 255), extPx[off]);
                Assert.Equal((byte)((y * 40) & 255), extPx[off + 1]);
                Assert.Equal((byte)50, extPx[off + 2]);
            }
        }

        ImageBuffer<ushort> ext16 = StbImage.Load16(extPic);
        Assert.Equal(extPx.Length, ext16.AsSpan().Length);
        Assert.Equal((ushort)((extPx[0] << 8) | extPx[0]), ext16.AsSpan()[0]);

        ImageBuffer<float> extF = StbImage.LoadFloat(extPic);
        Assert.Equal(extPx.Length, extF.AsSpan().Length);

        ImageBuffer<byte> extFlip = StbImage.Load(extPic, ColorComponents.Default, new ImageDecodeOptions { FlipVertically = true });
        int rowStride = 200 * 3;
        AssertBytesEqual(extPx.Slice(0, rowStride), extFlip.AsSpan().Slice(3 * rowStride, rowStride), "PIC flip row 0");
    }

    [Fact]
    public void StreamOverloadsMatchSpanOverloadsAcrossAllNineFormats()
    {
        string? corpusRoot = CorpusHelper.TryGetCorpusRoot();
        if (corpusRoot is null)
        {
            Assert.False(CorpusHelper.IsRunningInCi, "Corpus directory is missing in CI.");
            return;
        }

        string[] samples =
        [
            Path.Combine(corpusRoot, "generated", "png_valid", "rgb_16bit_13x11.png"),
            Path.Combine(corpusRoot, "generated", "jpeg_valid", "baseline_420_32x32.jpg"),
            Path.Combine(corpusRoot, "generated", "bmp_valid", "bmp_32bit_v3_rgba_12x10.bmp"),
            Path.Combine(corpusRoot, "generated", "tga_valid", "tga_rgba32_rle_15x11.tga"),
            Path.Combine(corpusRoot, "generated", "gif_valid", "gif89a_animated_disposal_16x16.gif"),
            Path.Combine(corpusRoot, "generated", "psd_valid", "psd_16bit_4ch_raw_matte_13x9.psd"),
            Path.Combine(corpusRoot, "generated", "hdr_valid", "hdr_radiance_rle_32x24.hdr"),
            Path.Combine(corpusRoot, "generated", "pnm_valid", "pnm_p6_bin_16bit_11x9.ppm"),
            Path.Combine(corpusRoot, "generated", "pic_valid", "pic_mixed_rle_rgba_multipkt_15x11.pic"),
        ];

        foreach (string path in samples)
        {
            byte[] data = File.ReadAllBytes(path);
            string label = Path.GetFileName(path);

            ImageFormat expectedFmt = StbImage.DetectFormat(data);
            ImageInfo expectedInfo = StbImage.GetInfo(data);
            bool expected16 = StbImage.Is16Bit(data);
            bool expectedHdr = StbImage.IsHdr(data);
            ImageBuffer<byte> expected8 = StbImage.Load(data);
            ImageBuffer<ushort> expected16Buf = StbImage.Load16(data);
            ImageBuffer<float> expectedFloat = StbImage.LoadFloat(data);

            // A) Public-buffer MemoryStream with non-zero starting offset (zero-copy fast path)
            byte[] prefixed = new byte[13 + data.Length];
            Array.Copy(data, 0, prefixed, 13, data.Length);
            using (MemoryStream ms = new(prefixed, 0, prefixed.Length, writable: false, publiclyVisible: true))
            {
                ms.Position = 13;
                Assert.Equal(expectedFmt, StbImage.DetectFormat(ms));
                Assert.Equal(13, ms.Position);
                Assert.Equal(expected16, StbImage.Is16Bit(ms));
                Assert.Equal(13, ms.Position);
                Assert.Equal(expectedHdr, StbImage.IsHdr(ms));
                Assert.Equal(13, ms.Position);
                Assert.Equal(expectedInfo, StbImage.GetInfo(ms));
                Assert.Equal(13, ms.Position);
                Assert.True(StbImage.TryGetInfo(ms, out ImageInfo msTryInfo));
                Assert.Equal(expectedInfo, msTryInfo);
                Assert.Equal(13, ms.Position);

                AssertBytesEqual(expected8.AsSpan(), StbImage.Load(ms).AsSpan(), $"{label} public MemoryStream Load");
                Assert.Equal(ms.Length, ms.Position);

                ms.Position = 13;
                AssertUShortsEqual(expected16Buf.AsSpan(), StbImage.Load16(ms).AsSpan(), $"{label} public MemoryStream Load16");

                ms.Position = 13;
                AssertFloatsEqual(expectedFloat.AsSpan(), StbImage.LoadFloat(ms).AsSpan(), 0, $"{label} public MemoryStream LoadFloat");

                if (expectedFmt == ImageFormat.Gif)
                {
                    ms.Position = 13;
                    GifAnimation spanAnim = StbImage.LoadGifFrames(data);
                    GifAnimation msAnim = StbImage.LoadGifFrames(ms);
                    Assert.Equal(spanAnim.FrameCount, msAnim.FrameCount);
                    AssertBytesEqual(spanAnim.AsSpan(), msAnim.AsSpan(), $"{label} public MemoryStream LoadGifFrames");
                }
            }

            // B) Non-public MemoryStream (seekable, TryGetBuffer == false -> chunked probe + position restore)
            using (MemoryStream hiddenMs = new(data, 0, data.Length, writable: false, publiclyVisible: false))
            {
                Assert.Equal(expectedFmt, StbImage.DetectFormat(hiddenMs));
                Assert.Equal(0, hiddenMs.Position);
                Assert.Equal(expectedInfo, StbImage.GetInfo(hiddenMs));
                Assert.Equal(0, hiddenMs.Position);
                Assert.True(StbImage.TryGetInfo(hiddenMs, out ImageInfo hInfo));
                Assert.Equal(expectedInfo, hInfo);
                Assert.Equal(0, hiddenMs.Position);

                AssertBytesEqual(expected8.AsSpan(), StbImage.Load(hiddenMs).AsSpan(), $"{label} hidden MemoryStream Load");
            }

            // C) Non-seekable small-chunk stream (17-byte reads)
            using (ChunkedNonSeekableStream ns = new(data, maxChunkSize: 17))
            {
                AssertBytesEqual(expected8.AsSpan(), StbImage.Load(ns).AsSpan(), $"{label} non-seekable Load");
            }

            using (ChunkedNonSeekableStream ns16 = new(data, maxChunkSize: 17))
            {
                AssertUShortsEqual(expected16Buf.AsSpan(), StbImage.Load16(ns16).AsSpan(), $"{label} non-seekable Load16");
            }

            using (ChunkedNonSeekableStream nsF = new(data, maxChunkSize: 17))
            {
                AssertFloatsEqual(expectedFloat.AsSpan(), StbImage.LoadFloat(nsF).AsSpan(), 0, $"{label} non-seekable LoadFloat");
            }

            using (ChunkedNonSeekableStream nsInfo = new(data, maxChunkSize: 17))
            {
                Assert.Equal(expectedInfo, StbImage.GetInfo(nsInfo));
            }

            if (expectedFmt == ImageFormat.Gif)
            {
                using ChunkedNonSeekableStream nsGif = new(data, maxChunkSize: 17);
                GifAnimation spanAnim = StbImage.LoadGifFrames(data);
                GifAnimation nsAnim = StbImage.LoadGifFrames(nsGif);
                Assert.Equal(spanAnim.FrameCount, nsAnim.FrameCount);
                AssertBytesEqual(spanAnim.AsSpan(), nsAnim.AsSpan(), $"{label} non-seekable LoadGifFrames");
            }
        }
    }

    private static ImageFormat MapOracleType(OracleImageType type) => type switch
    {
        OracleImageType.Jpeg => ImageFormat.Jpeg,
        OracleImageType.Png => ImageFormat.Png,
        OracleImageType.Gif => ImageFormat.Gif,
        OracleImageType.Bmp => ImageFormat.Bmp,
        OracleImageType.Psd => ImageFormat.Psd,
        OracleImageType.Pnm => ImageFormat.Pnm,
        OracleImageType.Hdr => ImageFormat.Hdr,
        OracleImageType.Tga => ImageFormat.Tga,
        _ => ImageFormat.Unknown,
    };

    private static void AssertBufferMetadata<T>(OracleImage<T> oracle, ImageBuffer<T> managed, string label)
        where T : unmanaged
    {
        Assert.True(oracle.Width == managed.Width, $"{label}: width mismatch {oracle.Width} vs {managed.Width}.");
        Assert.True(oracle.Height == managed.Height, $"{label}: height mismatch {oracle.Height} vs {managed.Height}.");
        Assert.True(oracle.Channels == (int)managed.Components, $"{label}: channels mismatch {oracle.Channels} vs {(int)managed.Components}.");
        Assert.True(oracle.SourceChannels == (int)managed.SourceComponents, $"{label}: source channels mismatch {oracle.SourceChannels} vs {(int)managed.SourceComponents}.");
    }

    private static void AssertBytesEqual(ReadOnlySpan<byte> expected, ReadOnlySpan<byte> actual, string context, int maxAbsDiff = 0)
    {
        Assert.Equal(expected.Length, actual.Length);
        if (expected.SequenceEqual(actual))
        {
            return;
        }

        for (int i = 0; i < expected.Length; i++)
        {
            int diff = Math.Abs(expected[i] - actual[i]);
            if (diff > maxAbsDiff)
            {
                Assert.Fail($"{context}: byte mismatch at index {i}: expected {expected[i]}, got {actual[i]} (diff={diff}, tol={maxAbsDiff}).");
            }
        }
    }

    private static void AssertUShortsEqual(ReadOnlySpan<ushort> expected, ReadOnlySpan<ushort> actual, string context, int maxAbsDiff = 0)
    {
        Assert.Equal(expected.Length, actual.Length);
        if (expected.SequenceEqual(actual))
        {
            return;
        }

        for (int i = 0; i < expected.Length; i++)
        {
            int diff = Math.Abs(expected[i] - actual[i]);
            if (diff > maxAbsDiff)
            {
                Assert.Fail($"{context}: ushort mismatch at index {i}: expected {expected[i]}, got {actual[i]} (diff={diff}, tol={maxAbsDiff}).");
            }
        }
    }

    private static void AssertFloatsEqual(
        ReadOnlySpan<float> expected,
        ReadOnlySpan<float> actual,
        int maxUlp,
        string context,
        float maxAbsDiff = 0f)
    {
        Assert.Equal(expected.Length, actual.Length);
        for (int i = 0; i < expected.Length; i++)
        {
            float e = expected[i];
            float a = actual[i];
            int eBits = BitConverter.SingleToInt32Bits(e);
            int aBits = BitConverter.SingleToInt32Bits(a);
            if (eBits == aBits)
            {
                continue;
            }

            if (e == 0f && a == 0f)
            {
                continue;
            }

            if (float.IsNaN(e) || float.IsNaN(a))
            {
                Assert.Fail($"{context}: NaN float mismatch at index {i}: expected {e:R}, got {a:R}.");
            }

            long ulp = Math.Abs((long)eBits - aBits);
            if (ulp <= maxUlp)
            {
                continue;
            }

            if (maxAbsDiff > 0f && MathF.Abs(e - a) <= maxAbsDiff)
            {
                continue;
            }

            Assert.Fail($"{context}: float mismatch at index {i}: expected {e:R} (0x{eBits:X8}), got {a:R} (0x{aBits:X8}), ulp={ulp}, absDiff={MathF.Abs(e - a):R}.");
        }
    }

    private sealed class ChunkedNonSeekableStream : Stream
    {
        private readonly byte[] _data;
        private readonly int _maxChunkSize;
        private int _pos;

        public ChunkedNonSeekableStream(byte[] data, int maxChunkSize)
        {
            _data = data;
            _maxChunkSize = maxChunkSize;
        }

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override void Flush()
        {
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            int remaining = _data.Length - _pos;
            if (remaining <= 0)
            {
                return 0;
            }

            int toCopy = Math.Min(Math.Min(count, _maxChunkSize), remaining);
            Buffer.BlockCopy(_data, _pos, buffer, offset, toCopy);
            _pos += toCopy;
            return toCopy;
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
