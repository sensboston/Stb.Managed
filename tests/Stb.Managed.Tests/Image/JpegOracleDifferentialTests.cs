using Stb.Managed.Oracle;
using Xunit;

namespace Stb.Managed.Tests.Image;

public sealed class JpegOracleDifferentialTests
{
    private static readonly ColorComponents[] AllDesiredComponents =
    [
        ColorComponents.Default,
        ColorComponents.Grey,
        ColorComponents.GreyAlpha,
        ColorComponents.RedGreenBlue,
        ColorComponents.RedGreenBlueAlpha,
    ];

    private static readonly string[] CorpusJpegSubDirs =
    [
        Path.Combine("libjpeg-turbo", "testimages"),
        Path.Combine("jpeg-decoder", "tests", "reftest", "images"),
        Path.Combine("jpeg-decoder", "tests", "reftest", "images", "mozilla"),
        Path.Combine("generated", "jpeg_valid"),
    ];

    [Fact]
    public void AllCorpusJpegsMatchOracleByteExactIn8BitAnd16Bit()
    {
        string? corpusRoot = CorpusHelper.TryGetCorpusRoot();
        if (corpusRoot is null)
        {
            Assert.False(CorpusHelper.IsRunningInCi, "Corpus directory is missing in CI.");
            return;
        }

        int totalAccepted = 0;
        int totalRejected = 0;

        foreach (string subDir in CorpusJpegSubDirs)
        {
            string fullDir = Path.Combine(corpusRoot, subDir);
            Assert.True(Directory.Exists(fullDir), $"Expected corpus directory '{fullDir}'.");

            string[] files = Directory.GetFiles(fullDir, "*.jpg");
            Array.Sort(files, StringComparer.Ordinal);
            Assert.NotEmpty(files);

            foreach (string file in files)
            {
                string label = Path.Combine(subDir, Path.GetFileName(file));
                byte[] data = File.ReadAllBytes(file);

                OracleImage<byte>? oracle8 = StbOracle.LoadImage(data, desiredChannels: 0);
                if (oracle8 is null)
                {
                    // Native stb rejects this file (e.g., 12-bit, arithmetic-coded, MJPEG without DHT, truncated).
                    // Stb.Managed must also reject cleanly with StbException.
                    Assert.Throws<StbException>(() => StbImage.Load(data));
                    totalRejected++;
                    continue;
                }

                // Format detection (note: mozilla/jpg-size-6x6.jpg is actually a PNG misnamed .jpg).
                ImageFormat detected = StbImage.DetectFormat(data);
                Assert.True(
                    detected is ImageFormat.Jpeg or ImageFormat.Png,
                    $"Unexpected format {detected} for '{label}'.");

                // Header info parity.
                OracleImageInfo? oracleInfo = StbOracle.GetImageInfo(data);
                Assert.True(oracleInfo.HasValue, $"Oracle GetImageInfo failed for '{label}'.");
                Assert.True(StbImage.TryGetInfo(data, out ImageInfo managedInfo), $"StbImage.TryGetInfo failed for '{label}'.");
                Assert.Equal(oracleInfo.Value.Width, managedInfo.Width);
                Assert.Equal(oracleInfo.Value.Height, managedInfo.Height);
                Assert.Equal(oracleInfo.Value.Channels, (int)managedInfo.Components);
                Assert.Equal(oracleInfo.Value.Is16Bit ? 16 : 8, managedInfo.BitsPerChannel);
                Assert.False(managedInfo.IsHdr);

                // 8-bit default-components parity.
                ImageBuffer<byte> managed8 = StbImage.Load(data, ColorComponents.Default);
                AssertMatchesOracle8(label, oracle8, managed8);

                // 16-bit default-components parity.
                OracleImage<ushort>? oracle16 = StbOracle.LoadImage16(data, desiredChannels: 0);
                Assert.True(oracle16 is not null, $"Oracle LoadImage16 failed for '{label}'.");
                ImageBuffer<ushort> managed16 = StbImage.Load16(data, ColorComponents.Default);
                AssertMatchesOracle16(label, oracle16, managed16);

                totalAccepted++;
            }
        }

        Assert.True(totalAccepted >= 72, $"Expected at least 72 valid corpus JPEGs, verified {totalAccepted}.");
        Assert.True(totalRejected >= 5, $"Expected at least 5 oracle-rejected corpus JPEGs, verified {totalRejected}.");
    }

    [Fact]
    public void ChannelConversionsFlipAndTryLoadMatchOracleAcrossColorSpacesAndSubsampling()
    {
        string? corpusRoot = CorpusHelper.TryGetCorpusRoot();
        if (corpusRoot is null)
        {
            Assert.False(CorpusHelper.IsRunningInCi, "Corpus directory is missing in CI.");
            return;
        }

        string[] sampleRelPaths =
        [
            Path.Combine("libjpeg-turbo", "testimages", "testorig.jpg"),
            Path.Combine("jpeg-decoder", "tests", "reftest", "images", "progressive3.jpg"),
            Path.Combine("jpeg-decoder", "tests", "reftest", "images", "restarts.jpg"),
            Path.Combine("jpeg-decoder", "tests", "reftest", "images", "rgb.jpg"),
            Path.Combine("jpeg-decoder", "tests", "reftest", "images", "ycck.jpg"),
            Path.Combine("jpeg-decoder", "tests", "reftest", "images", "mozilla", "jpg-cmyk-1.jpg"),
            Path.Combine("jpeg-decoder", "tests", "reftest", "images", "non-interleaved-mcu.jpg"),
            Path.Combine("generated", "jpeg_valid", "baseline_444_32x32.jpg"),
            Path.Combine("generated", "jpeg_valid", "baseline_422_17x13.jpg"),
            Path.Combine("generated", "jpeg_valid", "baseline_440_13x17.jpg"),
            Path.Combine("generated", "jpeg_valid", "baseline_420_1x1.jpg"),
            Path.Combine("generated", "jpeg_valid", "baseline_420_33x31.jpg"),
            Path.Combine("generated", "jpeg_valid", "baseline_4x2_generic_35x27.jpg"),
            Path.Combine("generated", "jpeg_valid", "gray_1x1_33x29.jpg"),
            Path.Combine("generated", "jpeg_valid", "gray_2x2_33x29.jpg"),
            Path.Combine("generated", "jpeg_valid", "baseline_420_dri3_65x49.jpg"),
            Path.Combine("generated", "jpeg_valid", "prog_succ_approx_420_49x35.jpg"),
            Path.Combine("generated", "jpeg_valid", "prog_succ_approx_dri3_48x48.jpg"),
            Path.Combine("generated", "jpeg_valid", "sof1_16bit_dqt_32x24.jpg"),
            Path.Combine("generated", "jpeg_valid", "color_rgb_ids_24x24.jpg"),
            Path.Combine("generated", "jpeg_valid", "color_adobe_rgb_24x24.jpg"),
            Path.Combine("generated", "jpeg_valid", "color_adobe_0_with_jfif_24x24.jpg"),
            Path.Combine("generated", "jpeg_valid", "color_cmyk_24x24.jpg"),
            Path.Combine("generated", "jpeg_valid", "color_ycck_24x24.jpg"),
            Path.Combine("generated", "jpeg_valid", "color_4comp_no_app14_24x24.jpg"),
        ];

        ImageDecodeOptions flipOptions = new() { FlipVertically = true };

        foreach (string relPath in sampleRelPaths)
        {
            string fullPath = Path.Combine(corpusRoot, relPath);
            byte[] data = File.ReadAllBytes(fullPath);

            foreach (ColorComponents desired in AllDesiredComponents)
            {
                int req = (int)desired;

                OracleImage<byte>? oracle8 = StbOracle.LoadImage(data, desiredChannels: req);
                Assert.NotNull(oracle8);

                ImageBuffer<byte> managed8 = StbImage.Load(data, desired);
                AssertMatchesOracle8($"{relPath}[8,req={req}]", oracle8, managed8);

                // TryLoad into exact-sized destination + too-small destination contract.
                byte[] exactDst = new byte[oracle8.AsSpan().Length];
                Assert.True(StbImage.TryLoad(data, exactDst, out ImageInfo info8, out ColorComponents outComp8, desired));
                Assert.Equal(oracle8.Width, info8.Width);
                Assert.Equal(oracle8.Height, info8.Height);
                Assert.Equal(oracle8.Channels, (int)outComp8);
                Assert.True(oracle8.AsSpan().SequenceEqual(exactDst), $"TryLoad pixel mismatch on '{relPath}' req={req}.");

                if (exactDst.Length > 0)
                {
                    byte[] smallDst = new byte[exactDst.Length - 1];
                    Assert.False(StbImage.TryLoad(data, smallDst, out ImageInfo smallInfo, out ColorComponents smallComp, desired));
                    Assert.Equal(oracle8.Width, smallInfo.Width);
                    Assert.Equal(oracle8.Height, smallInfo.Height);
                    Assert.Equal(oracle8.Channels, (int)smallComp);
                }

                // 16-bit TryLoad16 parity.
                OracleImage<ushort>? oracle16 = StbOracle.LoadImage16(data, desiredChannels: req);
                Assert.NotNull(oracle16);
                ushort[] exactDst16 = new ushort[oracle16.AsSpan().Length];
                Assert.True(StbImage.TryLoad16(data, exactDst16, out ImageInfo info16, out ColorComponents outComp16, desired));
                Assert.Equal(oracle16.Width, info16.Width);
                Assert.Equal(oracle16.Height, info16.Height);
                Assert.Equal(oracle16.Channels, (int)outComp16);
                Assert.True(oracle16.AsSpan().SequenceEqual(exactDst16), $"TryLoad16 pixel mismatch on '{relPath}' req={req}.");
            }

            // FlipVertically parity against flipped oracle rows.
            OracleImage<byte>? unflipped = StbOracle.LoadImage(data, desiredChannels: 4);
            Assert.NotNull(unflipped);
            ImageBuffer<byte> flipped = StbImage.Load(data, ColorComponents.RedGreenBlueAlpha, flipOptions);
            Assert.Equal(unflipped.Width, flipped.Width);
            Assert.Equal(unflipped.Height, flipped.Height);
            int stride = unflipped.Width * 4;
            for (int y = 0; y < unflipped.Height; y++)
            {
                ReadOnlySpan<byte> expectedRow = unflipped.AsSpan().Slice((unflipped.Height - 1 - y) * stride, stride);
                ReadOnlySpan<byte> actualRow = flipped.AsSpan().Slice(y * stride, stride);
                Assert.True(expectedRow.SequenceEqual(actualRow), $"FlipVertically row {y} mismatch on '{relPath}'.");
            }
        }
    }

    [Fact]
    public void StbSlowFuzzSeedIsRejectedCleanlyWithoutHang()
    {
        string? corpusRoot = CorpusHelper.TryGetCorpusRoot();
        if (corpusRoot is null)
        {
            Assert.False(CorpusHelper.IsRunningInCi, "Corpus directory is missing in CI.");
            return;
        }

        string slowPath = Path.Combine(corpusRoot, "stb", "tests", "local_fuzz", "image_seeds", "slow.jpg");
        byte[] data = File.ReadAllBytes(slowPath);

        Assert.Equal(ImageFormat.Jpeg, StbImage.DetectFormat(data));
        Assert.True(StbImage.TryGetInfo(data, out ImageInfo info));
        Assert.Equal(65370, info.Width);
        Assert.Equal(196, info.Height);

        Assert.Throws<StbException>(() => StbImage.Load(data));
    }

    [Fact]
    public void ParallelDecodeMatchesSingleThreadedBitExactAcrossModesAndFlip()
    {
        string? corpusRoot = CorpusHelper.TryGetCorpusRoot();
        if (corpusRoot is null)
        {
            Assert.False(CorpusHelper.IsRunningInCi, "Corpus directory is missing in CI.");
            return;
        }

        string[] parallelRelPaths =
        [
            Path.Combine("jpeg-decoder", "tests", "reftest", "images", "restarts.jpg"),
            Path.Combine("jpeg-decoder", "tests", "reftest", "images", "progressive3.jpg"),
            Path.Combine("jpeg-decoder", "tests", "reftest", "images", "non-interleaved-mcu.jpg"),
            Path.Combine("jpeg-decoder", "tests", "reftest", "images", "ycck.jpg"),
            Path.Combine("jpeg-decoder", "tests", "reftest", "images", "mozilla", "jpg-cmyk-1.jpg"),
            Path.Combine("generated", "jpeg_valid", "baseline_420_dri1_48x48.jpg"),
            Path.Combine("generated", "jpeg_valid", "baseline_420_dri3_65x49.jpg"),
            Path.Combine("generated", "jpeg_valid", "baseline_noninterleaved_dri4_35x29.jpg"),
            Path.Combine("generated", "jpeg_valid", "baseline_4x2_generic_35x27.jpg"),
            Path.Combine("generated", "jpeg_valid", "gray_1x1_33x29.jpg"),
            Path.Combine("generated", "jpeg_valid", "prog_succ_approx_420_49x35.jpg"),
            Path.Combine("generated", "jpeg_valid", "prog_succ_approx_dri3_48x48.jpg"),
            Path.Combine("generated", "jpeg_valid", "bench_1024x1024_420.jpg"),
            Path.Combine("generated", "jpeg_valid", "bench_1024x1024_420_dri.jpg"),
            Path.Combine("generated", "jpeg_valid", "bench_1024x1024_420_prog.jpg"),
            Path.Combine("generated", "jpeg_valid", "bench_2048x2048_420_dri.jpg"),
        ];

        ImageDecodeOptions par4 = new() { Options = new StbOptions { MaxDegreeOfParallelism = 4 } };
        ImageDecodeOptions parAuto = new() { Options = new StbOptions { MaxDegreeOfParallelism = 0 } };
        ImageDecodeOptions flip1T = new() { FlipVertically = true };
        ImageDecodeOptions flipPar4 = new() { FlipVertically = true, Options = new StbOptions { MaxDegreeOfParallelism = 4 } };

        foreach (string relPath in parallelRelPaths)
        {
            string fullPath = Path.Combine(corpusRoot, relPath);
            Assert.True(File.Exists(fullPath), $"Missing expected corpus fixture: {fullPath}");

            byte[] data = File.ReadAllBytes(fullPath);

            ImageBuffer<byte> ref1T = StbImage.Load(data, ColorComponents.RedGreenBlueAlpha);
            ImageBuffer<byte> loadPar4 = StbImage.Load(data, ColorComponents.RedGreenBlueAlpha, par4);
            Assert.True(ref1T.AsSpan().SequenceEqual(loadPar4.AsSpan()), $"Load(dop=4) mismatch on '{relPath}'.");

            byte[] dst = new byte[ref1T.AsSpan().Length];
            Assert.True(StbImage.TryLoad(data, dst, out _, out _, ColorComponents.RedGreenBlueAlpha, par4));
            Assert.True(ref1T.AsSpan().SequenceEqual(dst), $"TryLoad(dop=4) mismatch on '{relPath}'.");

            dst.AsSpan().Clear();
            Assert.True(StbImage.TryLoad(data, dst, out _, out _, ColorComponents.RedGreenBlueAlpha, parAuto));
            Assert.True(ref1T.AsSpan().SequenceEqual(dst), $"TryLoad(dop=0) mismatch on '{relPath}'.");

            // FlipVertically parity across both Load (byte[]) and TryLoad (double-buffered Span<byte>).
            ImageBuffer<byte> refFlip = StbImage.Load(data, ColorComponents.RedGreenBlueAlpha, flip1T);
            ImageBuffer<byte> loadFlipPar4 = StbImage.Load(data, ColorComponents.RedGreenBlueAlpha, flipPar4);
            Assert.True(refFlip.AsSpan().SequenceEqual(loadFlipPar4.AsSpan()), $"Load(flip,dop=4) mismatch on '{relPath}'.");

            dst.AsSpan().Clear();
            Assert.True(StbImage.TryLoad(data, dst, out _, out _, ColorComponents.RedGreenBlueAlpha, flipPar4));
            Assert.True(refFlip.AsSpan().SequenceEqual(dst), $"TryLoad(flip,dop=4) mismatch on '{relPath}'.");
        }

        // Malformed JPEG with DRI must still surface StbException (never AggregateException) under parallel decode.
        string malformedDir = Path.Combine(corpusRoot, "generated", "jpeg_malformed");
        if (Directory.Exists(malformedDir))
        {
            foreach (string file in Directory.GetFiles(malformedDir, "*.jpg"))
            {
                byte[] bad = File.ReadAllBytes(file);
                Assert.Throws<StbException>(() => StbImage.Load(bad, ColorComponents.Default, par4));
            }
        }
    }

    private static void AssertMatchesOracle8(string label, OracleImage<byte> expected, ImageBuffer<byte> actual)
    {
        Assert.Equal(expected.Width, actual.Width);
        Assert.Equal(expected.Height, actual.Height);
        Assert.Equal(expected.Channels, (int)actual.Components);

        ReadOnlySpan<byte> exp = expected.AsSpan();
        ReadOnlySpan<byte> act = actual.AsSpan();
        if (!exp.SequenceEqual(act))
        {
            int idx = FindFirstMismatch(exp, act);
            int maxDiff = ComputeMaxAbsDiff(exp, act);
            Assert.Fail(
                $"8-bit JPEG pixel mismatch on '{label}' at byte {idx}: expected {exp[idx]}, actual {act[idx]} (maxAbsDiff={maxDiff}, totalBytes={exp.Length}).");
        }
    }

    private static void AssertMatchesOracle16(string label, OracleImage<ushort> expected, ImageBuffer<ushort> actual)
    {
        Assert.Equal(expected.Width, actual.Width);
        Assert.Equal(expected.Height, actual.Height);
        Assert.Equal(expected.Channels, (int)actual.Components);

        ReadOnlySpan<ushort> exp = expected.AsSpan();
        ReadOnlySpan<ushort> act = actual.AsSpan();
        if (!exp.SequenceEqual(act))
        {
            for (int i = 0; i < exp.Length; i++)
            {
                if (exp[i] != act[i])
                {
                    Assert.Fail(
                        $"16-bit JPEG pixel mismatch on '{label}' at sample {i}: expected {exp[i]}, actual {act[i]}.");
                }
            }
        }
    }

    private static int FindFirstMismatch(ReadOnlySpan<byte> a, ReadOnlySpan<byte> b)
    {
        int len = Math.Min(a.Length, b.Length);
        for (int i = 0; i < len; i++)
        {
            if (a[i] != b[i])
            {
                return i;
            }
        }

        return len;
    }

    private static int ComputeMaxAbsDiff(ReadOnlySpan<byte> a, ReadOnlySpan<byte> b)
    {
        int len = Math.Min(a.Length, b.Length);
        int maxDiff = 0;
        for (int i = 0; i < len; i++)
        {
            int diff = Math.Abs(a[i] - b[i]);
            if (diff > maxDiff)
            {
                maxDiff = diff;
            }
        }

        return maxDiff;
    }
}
