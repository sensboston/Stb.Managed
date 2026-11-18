using Stb.Managed.Image.Internal;
using Stb.Managed.Oracle;
using Xunit;

namespace Stb.Managed.Tests.Image;

public sealed class PngOracleDifferentialTests
{
    private static readonly ColorComponents[] AllDesiredComponents =
    [
        ColorComponents.Default,
        ColorComponents.Grey,
        ColorComponents.GreyAlpha,
        ColorComponents.RedGreenBlue,
        ColorComponents.RedGreenBlueAlpha,
    ];

    private static readonly string[] ValidCorpusSubDirs =
    [
        Path.Combine("stb", "tests", "pngsuite", "primary"),
        Path.Combine("stb", "tests", "pngsuite", "primary_check"),
        Path.Combine("stb", "tests", "pngsuite", "16bit"),
        Path.Combine("stb", "tests", "pngsuite", "unused"),
        Path.Combine("stb", "data"),
        Path.Combine("stb", "data", "herringbone"),
        Path.Combine("stb", "tests", "sdf"),
        Path.Combine("generated", "png_valid"),
    ];

    [Fact]
    public void AllValidCorpusPngsMatchOracleByteExactIn8BitAnd16Bit()
    {
        string? corpusRoot = CorpusHelper.TryGetCorpusRoot();
        if (corpusRoot is null)
        {
            Assert.False(CorpusHelper.IsRunningInCi, "Corpus directory is missing in CI.");
            return;
        }

        ImageDecodeOptions strictOptions = new() { VerifyChecksums = true };
        int totalVerified = 0;

        foreach (string subDir in ValidCorpusSubDirs)
        {
            string fullDir = Path.Combine(corpusRoot, subDir);
            Assert.True(Directory.Exists(fullDir), $"Expected corpus directory '{fullDir}'.");

            string[] files = Directory.GetFiles(fullDir, "*.png");
            Array.Sort(files, StringComparer.Ordinal);
            Assert.NotEmpty(files);

            foreach (string file in files)
            {
                string label = Path.Combine(subDir, Path.GetFileName(file));
                byte[] data = File.ReadAllBytes(file);

                Assert.Equal(ImageFormat.Png, StbImage.DetectFormat(data));

                // Header info parity.
                OracleImageInfo? oracleInfo = StbOracle.GetImageInfo(data);
                Assert.True(oracleInfo.HasValue, $"Oracle GetImageInfo failed for '{label}'.");
                Assert.True(StbImage.TryGetInfo(data, out ImageInfo managedInfo), $"StbImage.TryGetInfo failed for '{label}'.");
                Assert.Equal(oracleInfo.Value.Width, managedInfo.Width);
                Assert.Equal(oracleInfo.Value.Height, managedInfo.Height);
                Assert.Equal(oracleInfo.Value.Channels, (int)managedInfo.Components);
                Assert.Equal(oracleInfo.Value.Is16Bit ? 16 : 8, managedInfo.BitsPerChannel);
                Assert.False(managedInfo.IsHdr);

                // 8-bit default-components parity (with checksum verification enabled).
                OracleImage<byte>? oracle8 = StbOracle.LoadImage(data, desiredChannels: 0);
                Assert.True(oracle8 is not null, $"Oracle LoadImage failed for '{label}'.");

                ImageBuffer<byte> managed8 = StbImage.Load(data, ColorComponents.Default, strictOptions);
                AssertMatchesOracle8(label, oracle8, managed8);

                // 16-bit default-components parity.
                OracleImage<ushort>? oracle16 = StbOracle.LoadImage16(data, desiredChannels: 0);
                Assert.True(oracle16 is not null, $"Oracle LoadImage16 failed for '{label}'.");

                ImageBuffer<ushort> managed16 = StbImage.Load16(data, ColorComponents.Default);
                AssertMatchesOracle16(label, oracle16, managed16);

                totalVerified++;
            }
        }

        Assert.True(totalVerified >= 290, $"Expected at least 290 valid corpus PNGs, verified {totalVerified}.");
    }

    [Fact]
    public void ChannelConversionsAndFlipMatchOracleAcrossColorTypesAndBitDepths()
    {
        string? corpusRoot = CorpusHelper.TryGetCorpusRoot();
        if (corpusRoot is null)
        {
            Assert.False(CorpusHelper.IsRunningInCi, "Corpus directory is missing in CI.");
            return;
        }

        string[] sampleRelPaths =
        [
            Path.Combine("stb", "tests", "pngsuite", "primary", "basn0g01.png"),
            Path.Combine("stb", "tests", "pngsuite", "primary", "basn0g02.png"),
            Path.Combine("stb", "tests", "pngsuite", "primary", "basn0g04.png"),
            Path.Combine("stb", "tests", "pngsuite", "primary", "basn0g08.png"),
            Path.Combine("stb", "tests", "pngsuite", "16bit", "basn0g16.png"),
            Path.Combine("stb", "tests", "pngsuite", "primary", "basn2c08.png"),
            Path.Combine("stb", "tests", "pngsuite", "16bit", "basn2c16.png"),
            Path.Combine("stb", "tests", "pngsuite", "primary", "basn3p01.png"),
            Path.Combine("stb", "tests", "pngsuite", "primary", "basn3p02.png"),
            Path.Combine("stb", "tests", "pngsuite", "primary", "basn3p04.png"),
            Path.Combine("stb", "tests", "pngsuite", "primary", "basn3p08.png"),
            Path.Combine("stb", "tests", "pngsuite", "primary", "basn4a08.png"),
            Path.Combine("stb", "tests", "pngsuite", "16bit", "basn4a16.png"),
            Path.Combine("stb", "tests", "pngsuite", "primary", "basn6a08.png"),
            Path.Combine("stb", "tests", "pngsuite", "16bit", "basn6a16.png"),
            Path.Combine("stb", "tests", "pngsuite", "primary", "basi0g01.png"),
            Path.Combine("stb", "tests", "pngsuite", "primary", "basi3p04.png"),
            Path.Combine("stb", "tests", "pngsuite", "16bit", "basi6a16.png"),
            Path.Combine("stb", "tests", "pngsuite", "primary", "tbbn0g04.png"),
            Path.Combine("stb", "tests", "pngsuite", "primary", "tbrn2c08.png"),
            Path.Combine("stb", "tests", "pngsuite", "primary", "tbbn3p08.png"),
            Path.Combine("stb", "tests", "pngsuite", "16bit", "tbbn2c16.png"),
            Path.Combine("stb", "tests", "pngsuite", "primary", "s01i3p01.png"),
            Path.Combine("stb", "tests", "pngsuite", "primary", "s05n3p02.png"),
            Path.Combine("generated", "png_valid", "tiny_1x1_rgba.png"),
            Path.Combine("generated", "png_valid", "multi_idat_19x13_rgba.png"),
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

                OracleImage<ushort>? oracle16 = StbOracle.LoadImage16(data, desiredChannels: req);
                Assert.NotNull(oracle16);
                ImageBuffer<ushort> managed16 = StbImage.Load16(data, desired);
                AssertMatchesOracle16($"{relPath}[16,req={req}]", oracle16, managed16);
            }

            // Flipped 8-bit and 16-bit.
            OracleImage<byte>? oracleFlip8 = StbOracle.LoadImage(data, desiredChannels: 4, flipVertically: true);
            Assert.NotNull(oracleFlip8);
            ImageBuffer<byte> managedFlip8 = StbImage.Load(data, ColorComponents.RedGreenBlueAlpha, flipOptions);
            AssertMatchesOracle8($"{relPath}[8,flip]", oracleFlip8, managedFlip8);

            OracleImage<ushort>? oracleFlip16 = StbOracle.LoadImage16(data, desiredChannels: 4, flipVertically: true);
            Assert.NotNull(oracleFlip16);
            ImageBuffer<ushort> managedFlip16 = StbImage.Load16(data, ColorComponents.RedGreenBlueAlpha, flipOptions);
            AssertMatchesOracle16($"{relPath}[16,flip]", oracleFlip16, managedFlip16);
        }
    }

    [Fact]
    public void IphoneCgbiPngsMatchOracleForAllConversionFlags()
    {
        string? corpusRoot = CorpusHelper.TryGetCorpusRoot();
        if (corpusRoot is null)
        {
            Assert.False(CorpusHelper.IsRunningInCi, "Corpus directory is missing in CI.");
            return;
        }

        string iphoneDir = Path.Combine(corpusRoot, "stb", "tests", "pngsuite", "iphone");
        string[] files = Directory.GetFiles(iphoneDir, "*.png");
        Array.Sort(files, StringComparer.Ordinal);
        Assert.NotEmpty(files);

        bool[] flags = [false, true];
        ColorComponents[] desiredList = [ColorComponents.Default, ColorComponents.RedGreenBlue, ColorComponents.RedGreenBlueAlpha];

        foreach (string file in files)
        {
            string name = Path.GetFileName(file);
            byte[] data = File.ReadAllBytes(file);

            foreach (bool convertRgb in flags)
            {
                foreach (bool unpremul in flags)
                {
                    ImageDecodeOptions opts = new()
                    {
                        ConvertIphonePngToRgb = convertRgb,
                        UnpremultiplyAlpha = unpremul,
                    };

                    foreach (ColorComponents desired in desiredList)
                    {
                        int req = (int)desired;
                        OracleImage<byte>? oracle8 = StbOracle.LoadImage(
                            data,
                            desiredChannels: req,
                            unpremultiply: unpremul,
                            convertIphonePngToRgb: convertRgb);
                        Assert.NotNull(oracle8);

                        ImageBuffer<byte> managed8 = StbImage.Load(data, desired, opts);
                        AssertMatchesOracle8($"{name}[8,rgb={convertRgb},unp={unpremul},req={req}]", oracle8, managed8);

                        OracleImage<ushort>? oracle16 = StbOracle.LoadImage16(
                            data,
                            desiredChannels: req,
                            unpremultiply: unpremul,
                            convertIphonePngToRgb: convertRgb);
                        Assert.NotNull(oracle16);

                        ImageBuffer<ushort> managed16 = StbImage.Load16(data, desired, opts);
                        AssertMatchesOracle16($"{name}[16,rgb={convertRgb},unp={unpremul},req={req}]", oracle16, managed16);
                    }
                }
            }
        }
    }

    [Fact]
    public void TryLoadAndTryLoad16HandleCallerSuppliedSpansWithoutOverwritingSentinel()
    {
        byte[] pixels = new byte[11 * 7 * 4];
        for (int i = 0; i < pixels.Length; i++)
        {
            pixels[i] = (byte)((i * 53 + 7) & 0xFF);
        }

        byte[] png = StbOracle.WritePng(11, 7, 4, pixels);
        int expectedLen = 11 * 7 * 4;

        // Too-small buffer returns false and populates metadata.
        byte[] tooSmall8 = new byte[expectedLen - 1];
        Assert.False(StbImage.TryLoad(png, tooSmall8, out ImageInfo info8, out ColorComponents outComp8));
        Assert.Equal(11, info8.Width);
        Assert.Equal(7, info8.Height);
        Assert.Equal(ColorComponents.RedGreenBlueAlpha, info8.Components);
        Assert.Equal(ColorComponents.RedGreenBlueAlpha, outComp8);

        // Oversized buffer writes exact prefix and leaves sentinel untouched.
        byte[] dest8 = new byte[expectedLen + 16];
        Array.Fill(dest8, (byte)0xA5);
        Assert.True(StbImage.TryLoad(png, dest8, out ImageInfo okInfo8, out ColorComponents okComp8));
        Assert.Equal(info8, okInfo8);
        Assert.Equal(ColorComponents.RedGreenBlueAlpha, okComp8);
        Assert.True(dest8.AsSpan(0, expectedLen).SequenceEqual(pixels));
        for (int i = expectedLen; i < dest8.Length; i++)
        {
            Assert.Equal((byte)0xA5, dest8[i]);
        }

        // 16-bit TryLoad16 with channel conversion (4 -> 3).
        int expected16Len = 11 * 7 * 3;
        ushort[] tooSmall16 = new ushort[expected16Len - 1];
        Assert.False(StbImage.TryLoad16(png, tooSmall16, out ImageInfo info16, out ColorComponents outComp16, ColorComponents.RedGreenBlue));
        Assert.Equal(11, info16.Width);
        Assert.Equal(7, info16.Height);
        Assert.Equal(ColorComponents.RedGreenBlue, outComp16);

        ushort[] dest16 = new ushort[expected16Len + 8];
        Array.Fill(dest16, (ushort)0xBEEF);
        Assert.True(StbImage.TryLoad16(png, dest16, out _, out ColorComponents okComp16, ColorComponents.RedGreenBlue));
        Assert.Equal(ColorComponents.RedGreenBlue, okComp16);

        ImageBuffer<ushort> ref16 = StbImage.Load16(png, ColorComponents.RedGreenBlue);
        Assert.True(dest16.AsSpan(0, expected16Len).SequenceEqual(ref16.AsSpan()));
        for (int i = expected16Len; i < dest16.Length; i++)
        {
            Assert.Equal((ushort)0xBEEF, dest16[i]);
        }
    }

    [Fact]
    public void PipelinedParallelDecodeMatchesSingleThreadedAndOracleByteExact()
    {
        string? corpusRoot = CorpusHelper.TryGetCorpusRoot();
        if (corpusRoot is null)
        {
            Assert.False(CorpusHelper.IsRunningInCi, "Corpus directory is missing in CI.");
            return;
        }

        string[] largeFixtures =
        [
            Path.Combine("generated", "png_valid", "bench_512x512_rgba.png"),
            Path.Combine("generated", "png_valid", "bench_1024x1024_rgb.png"),
            Path.Combine("generated", "png_valid", "bench_1024x1024_rgba.png"),
        ];

        ImageDecodeOptions singleOpts = new()
        {
            Options = new StbOptions { MaxDegreeOfParallelism = 1 },
            VerifyChecksums = true,
        };
        ImageDecodeOptions autoOpts = new()
        {
            Options = new StbOptions { MaxDegreeOfParallelism = 0 },
            VerifyChecksums = true,
        };
        ImageDecodeOptions explicit2Opts = new()
        {
            Options = new StbOptions { MaxDegreeOfParallelism = 2 },
            VerifyChecksums = false,
        };

        foreach (string relPath in largeFixtures)
        {
            byte[] data = File.ReadAllBytes(Path.Combine(corpusRoot, relPath));
            OracleImage<byte>? oracle = StbOracle.LoadImage(data);
            Assert.NotNull(oracle);

            ImageBuffer<byte> single = StbImage.Load(data, ColorComponents.Default, singleOpts);
            ImageBuffer<byte> auto = StbImage.Load(data, ColorComponents.Default, autoOpts);
            ImageBuffer<byte> par2 = StbImage.Load(data, ColorComponents.Default, explicit2Opts);

            AssertMatchesOracle8($"{relPath}[dop=1]", oracle, single);
            AssertMatchesOracle8($"{relPath}[dop=0]", oracle, auto);
            AssertMatchesOracle8($"{relPath}[dop=2]", oracle, par2);

            // Also verify 16-bit pipelined decode path.
            OracleImage<ushort>? oracle16 = StbOracle.LoadImage16(data);
            Assert.NotNull(oracle16);
            ImageBuffer<ushort> par16 = StbImage.Load16(data, ColorComponents.Default, autoOpts);
            AssertMatchesOracle16($"{relPath}[16,dop=0]", oracle16, par16);
        }
    }

    [Fact]
    public void PngUnfilterMatchesScalarReferenceAcrossAllFiltersStridesAndWidths()
    {
        PngFilterType[] filters =
        [
            PngFilterType.None,
            PngFilterType.Sub,
            PngFilterType.Up,
            PngFilterType.Average,
            PngFilterType.Paeth,
            PngFilterType.AvgFirst,
        ];
        int[] filterByteCounts = [1, 2, 3, 4, 6, 8];
        int[] widths = [1, 2, 3, 7, 15, 16, 17, 31, 32, 33, 63, 64, 65, 129, 257];

        uint rngState = 0x12345678u;
        foreach (int bpp in filterByteCounts)
        {
            foreach (int width in widths)
            {
                int rowBytes = width * bpp;
                byte[] raw = new byte[rowBytes];
                byte[] prior = new byte[rowBytes];
                FillPseudoRandom(raw, ref rngState);
                FillPseudoRandom(prior, ref rngState);

                byte[] expected = new byte[rowBytes];
                byte[] actual = new byte[rowBytes];

                foreach (PngFilterType filter in filters)
                {
                    PngUnfilter.UnfilterScanlineScalar(filter, raw, prior, expected, bpp);
                    PngUnfilter.UnfilterScanline(filter, raw, prior, actual, bpp);
                    Assert.True(
                        expected.AsSpan().SequenceEqual(actual),
                        $"Unfilter mismatch for filter={filter}, bpp={bpp}, width={width}.");
                }
            }
        }
    }

    private static void AssertMatchesOracle8(string label, OracleImage<byte> oracle, ImageBuffer<byte> managed)
    {
        Assert.True(oracle.Width == managed.Width, $"{label}: width mismatch (oracle={oracle.Width}, managed={managed.Width}).");
        Assert.True(oracle.Height == managed.Height, $"{label}: height mismatch (oracle={oracle.Height}, managed={managed.Height}).");
        Assert.True(oracle.Channels == (int)managed.Components, $"{label}: channels mismatch (oracle={oracle.Channels}, managed={(int)managed.Components}).");
        Assert.True(oracle.SourceChannels == (int)managed.SourceComponents, $"{label}: source channels mismatch (oracle={oracle.SourceChannels}, managed={(int)managed.SourceComponents}).");

        ReadOnlySpan<byte> expected = oracle.AsSpan();
        ReadOnlySpan<byte> actual = managed.AsSpan();
        if (!expected.SequenceEqual(actual))
        {
            int idx = FindFirstDifference(expected, actual);
            Assert.Fail($"{label}: 8-bit pixel mismatch at byte {idx} (oracle=0x{expected[idx]:X2}, managed=0x{actual[idx]:X2}).");
        }
    }

    private static void AssertMatchesOracle16(string label, OracleImage<ushort> oracle, ImageBuffer<ushort> managed)
    {
        Assert.True(oracle.Width == managed.Width, $"{label}: width mismatch (oracle={oracle.Width}, managed={managed.Width}).");
        Assert.True(oracle.Height == managed.Height, $"{label}: height mismatch (oracle={oracle.Height}, managed={managed.Height}).");
        Assert.True(oracle.Channels == (int)managed.Components, $"{label}: channels mismatch (oracle={oracle.Channels}, managed={(int)managed.Components}).");
        Assert.True(oracle.SourceChannels == (int)managed.SourceComponents, $"{label}: source channels mismatch (oracle={oracle.SourceChannels}, managed={(int)managed.SourceComponents}).");

        ReadOnlySpan<ushort> expected = oracle.AsSpan();
        ReadOnlySpan<ushort> actual = managed.AsSpan();
        if (!expected.SequenceEqual(actual))
        {
            int idx = FindFirstDifference(expected, actual);
            Assert.Fail($"{label}: 16-bit pixel mismatch at sample {idx} (oracle=0x{expected[idx]:X4}, managed=0x{actual[idx]:X4}).");
        }
    }

    private static int FindFirstDifference<T>(ReadOnlySpan<T> a, ReadOnlySpan<T> b)
        where T : IEquatable<T>
    {
        int len = Math.Min(a.Length, b.Length);
        for (int i = 0; i < len; i++)
        {
            if (!a[i].Equals(b[i]))
            {
                return i;
            }
        }

        return len;
    }

    private static void FillPseudoRandom(Span<byte> buffer, ref uint state)
    {
        for (int i = 0; i < buffer.Length; i++)
        {
            state ^= state << 13;
            state ^= state >> 17;
            state ^= state << 5;
            buffer[i] = (byte)state;
        }
    }
}
