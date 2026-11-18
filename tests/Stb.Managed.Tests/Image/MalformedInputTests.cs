using Xunit;

namespace Stb.Managed.Tests.Image;

public sealed class MalformedInputTests
{
    private static readonly string[] MalformedSubDirs =
    [
        Path.Combine("generated", "bmp_malformed"),
        Path.Combine("generated", "tga_malformed"),
        Path.Combine("generated", "gif_malformed"),
        Path.Combine("generated", "psd_malformed"),
        Path.Combine("generated", "hdr_malformed"),
        Path.Combine("generated", "pnm_malformed"),
        Path.Combine("generated", "pic_malformed"),
    ];

    private static readonly string[] FuzzSeedRelativePaths =
    [
        Path.Combine("generated", "bmp_valid", "bmp_32bit_v3_rgba_12x10.bmp"),
        Path.Combine("generated", "bmp_valid", "bmp_8bit_15x11.bmp"),
        Path.Combine("generated", "tga_valid", "tga_rgba32_rle_15x11.tga"),
        Path.Combine("generated", "tga_valid", "tga_indexed8_pal24_rle_15x11.tga"),
        Path.Combine("generated", "gif_valid", "gif89a_animated_disposal_16x16.gif"),
        Path.Combine("generated", "psd_valid", "psd_8bit_4ch_rle_matte_13x9.psd"),
        Path.Combine("generated", "hdr_valid", "hdr_radiance_rle_32x24.hdr"),
        Path.Combine("generated", "pnm_valid", "pnm_p2_ascii_max15_11x9.pgm"),
        Path.Combine("generated", "pnm_valid", "pnm_p6_bin_16bit_11x9.ppm"),
        Path.Combine("generated", "pic_valid", "pic_mixed_rle_rgba_multipkt_15x11.pic"),
    ];

    [Fact]
    public void AllMalformedFixturesThrowStbExceptionCleanly()
    {
        string? corpusRoot = CorpusHelper.TryGetCorpusRoot();
        if (corpusRoot is null)
        {
            Assert.False(CorpusHelper.IsRunningInCi, "Corpus directory is missing in CI.");
            return;
        }

        int total = 0;
        foreach (string subDir in MalformedSubDirs)
        {
            string fullDir = Path.Combine(corpusRoot, subDir);
            Assert.True(Directory.Exists(fullDir), $"Expected malformed directory '{fullDir}'.");

            string[] files = Directory.GetFiles(fullDir);
            Array.Sort(files, StringComparer.Ordinal);
            Assert.NotEmpty(files);

            foreach (string file in files)
            {
                byte[] data = File.ReadAllBytes(file);

                // TryGetInfo and DetectFormat must never throw on malformed input.
                _ = StbImage.DetectFormat(data);
                _ = StbImage.TryGetInfo(data, out _);
                _ = StbImage.Is16Bit(data);
                _ = StbImage.IsHdr(data);

                Assert.Throws<StbException>(() => StbImage.Load(data));
                Assert.Throws<StbException>(() => StbImage.Load16(data));
                Assert.Throws<StbException>(() => StbImage.LoadFloat(data));

                if (subDir.Contains("gif_malformed", StringComparison.Ordinal))
                {
                    Assert.Throws<StbException>(() => StbImage.LoadGifFrames(data));
                }

                total++;
            }
        }

        Assert.True(total >= 35, $"Expected at least 35 malformed fixtures, tested {total}.");
    }

    [Fact]
    public void DeterministicMutationsOfAllFormatsNeverCrashOrHang()
    {
        string? corpusRoot = CorpusHelper.TryGetCorpusRoot();
        if (corpusRoot is null)
        {
            Assert.False(CorpusHelper.IsRunningInCi, "Corpus directory is missing in CI.");
            return;
        }

        ImageDecodeOptions tightOpts = new()
        {
            Options = new StbOptions
            {
                Limits = new StbLimits
                {
                    MaxWidth = 512,
                    MaxHeight = 512,
                    MaxPixels = 65_536,
                    MaxAllocationBytes = 1 << 20,
                    MaxDecompressedBytes = 1 << 20,
                    MaxFrames = 32,
                },
            },
        };

        Random rng = new(20261116);
        foreach (string relPath in FuzzSeedRelativePaths)
        {
            byte[] seed = File.ReadAllBytes(Path.Combine(corpusRoot, relPath));
            bool isGif = relPath.EndsWith(".gif", StringComparison.Ordinal);

            // Progressive truncation sweep
            for (int cut = 0; cut < seed.Length; cut += Math.Max(1, seed.Length / 24))
            {
                ReadOnlySpan<byte> truncated = seed.AsSpan(0, cut);
                ExerciseUntrustedInput(truncated, tightOpts, isGif);
            }

            // Deterministic random bit/byte mutations
            byte[] mutated = new byte[seed.Length];
            for (int iter = 0; iter < 60; iter++)
            {
                Buffer.BlockCopy(seed, 0, mutated, 0, seed.Length);
                int flips = 1 + rng.Next(4);
                for (int f = 0; f < flips; f++)
                {
                    int idx = rng.Next(mutated.Length);
                    mutated[idx] = (byte)rng.Next(256);
                }

                ExerciseUntrustedInput(mutated, tightOpts, isGif);
            }
        }
    }

    [Fact]
    public void LimitsAreEnforcedAcrossFormatsAnimationsAndStreams()
    {
        string? corpusRoot = CorpusHelper.TryGetCorpusRoot();
        if (corpusRoot is null)
        {
            Assert.False(CorpusHelper.IsRunningInCi, "Corpus directory is missing in CI.");
            return;
        }

        byte[] animGif = File.ReadAllBytes(Path.Combine(corpusRoot, "generated", "gif_valid", "gif89a_animated_disposal_16x16.gif"));
        ImageDecodeOptions maxFrames2 = new()
        {
            Options = new StbOptions { Limits = new StbLimits { MaxFrames = 2 } },
        };
        StbException frameEx = Assert.Throws<StbException>(() => StbImage.LoadGifFrames(animGif, ColorComponents.Default, maxFrames2));
        Assert.Equal(StbErrorCode.LimitExceeded, frameEx.Code);

        byte[] bmp = File.ReadAllBytes(Path.Combine(corpusRoot, "generated", "bmp_valid", "bmp_24bit_bottomup_17x13.bmp"));
        ImageDecodeOptions maxWidth10 = new()
        {
            Options = new StbOptions { Limits = new StbLimits { MaxWidth = 10 } },
        };
        Assert.False(StbImage.TryGetInfo(bmp, out _, maxWidth10));
        StbException dimEx = Assert.Throws<StbException>(() => StbImage.Load(bmp, ColorComponents.Default, maxWidth10));
        Assert.Equal(StbErrorCode.LimitExceeded, dimEx.Code);

        // Stream allocation limit enforcement on non-seekable stream
        ImageDecodeOptions maxAlloc64 = new()
        {
            Options = new StbOptions { Limits = new StbLimits { MaxAllocationBytes = 64 } },
        };
        using NonSeekableWrapperStream ns = new(bmp);
        StbException streamEx = Assert.Throws<StbException>(() => StbImage.Load(ns, ColorComponents.Default, maxAlloc64));
        Assert.Equal(StbErrorCode.LimitExceeded, streamEx.Code);
    }

    [Fact]
    public void InvalidArgumentsAndEmptyInputsAreRejectedProperly()
    {
        Assert.Equal(ImageFormat.Unknown, StbImage.DetectFormat(ReadOnlySpan<byte>.Empty));
        Assert.False(StbImage.Is16Bit(ReadOnlySpan<byte>.Empty));
        Assert.False(StbImage.IsHdr(ReadOnlySpan<byte>.Empty));
        Assert.False(StbImage.TryGetInfo(ReadOnlySpan<byte>.Empty, out _));

        Assert.Throws<StbException>(() => StbImage.GetInfo(ReadOnlySpan<byte>.Empty));
        Assert.Throws<StbException>(() => StbImage.Load(ReadOnlySpan<byte>.Empty));
        Assert.Throws<StbException>(() => StbImage.Load16(ReadOnlySpan<byte>.Empty));
        Assert.Throws<StbException>(() => StbImage.LoadFloat(ReadOnlySpan<byte>.Empty));
        Assert.Throws<StbException>(() => StbImage.LoadGifFrames(ReadOnlySpan<byte>.Empty));

        // Non-GIF passed to LoadGifFrames
        StbException nonGifEx = Assert.Throws<StbException>(() => StbImage.LoadGifFrames("P5\n1 1\n255\nA"u8));
        Assert.Equal(StbErrorCode.UnsupportedFormat, nonGifEx.Code);

        // Out-of-range ColorComponents
        StbException badComp = Assert.Throws<StbException>(() => StbImage.Load("P5\n1 1\n255\nA"u8, (ColorComponents)5));
        Assert.Equal(StbErrorCode.InvalidArgument, badComp.Code);
        StbException badCompNeg = Assert.Throws<StbException>(() => StbImage.LoadFloat("P5\n1 1\n255\nA"u8, (ColorComponents)(-1)));
        Assert.Equal(StbErrorCode.InvalidArgument, badCompNeg.Code);

        // Null streams
        Assert.Throws<ArgumentNullException>(() => StbImage.DetectFormat((Stream)null!));
        Assert.Throws<ArgumentNullException>(() => StbImage.Is16Bit((Stream)null!));
        Assert.Throws<ArgumentNullException>(() => StbImage.IsHdr((Stream)null!));
        Assert.Throws<ArgumentNullException>(() => StbImage.GetInfo((Stream)null!));
        Assert.Throws<ArgumentNullException>(() => StbImage.TryGetInfo((Stream)null!, out _));
        Assert.Throws<ArgumentNullException>(() => StbImage.Load((Stream)null!));
        Assert.Throws<ArgumentNullException>(() => StbImage.Load16((Stream)null!));
        Assert.Throws<ArgumentNullException>(() => StbImage.LoadFloat((Stream)null!));
        Assert.Throws<ArgumentNullException>(() => StbImage.LoadGifFrames((Stream)null!));

        // Invalid gamma / scale options
        Assert.Throws<ArgumentOutOfRangeException>(() => new ImageDecodeOptions { HdrToLdrGamma = 0f });
        Assert.Throws<ArgumentOutOfRangeException>(() => new ImageDecodeOptions { HdrToLdrScale = -1f });
        Assert.Throws<ArgumentOutOfRangeException>(() => new ImageDecodeOptions { LdrToHdrGamma = float.NaN });
        Assert.Throws<ArgumentOutOfRangeException>(() => new ImageDecodeOptions { LdrToHdrScale = float.PositiveInfinity });
    }

    private static void ExerciseUntrustedInput(ReadOnlySpan<byte> data, ImageDecodeOptions options, bool isGif)
    {
        _ = StbImage.DetectFormat(data);
        _ = StbImage.TryGetInfo(data, out _, options);

        try
        {
            _ = StbImage.Load(data, ColorComponents.Default, options);
        }
        catch (StbException)
        {
        }

        try
        {
            _ = StbImage.LoadFloat(data, ColorComponents.Default, options);
        }
        catch (StbException)
        {
        }

        if (isGif)
        {
            try
            {
                _ = StbImage.LoadGifFrames(data, ColorComponents.Default, options);
            }
            catch (StbException)
            {
            }
        }
    }

    private sealed class NonSeekableWrapperStream : Stream
    {
        private readonly byte[] _data;
        private int _pos;

        public NonSeekableWrapperStream(byte[] data) => _data = data;
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
            int rem = _data.Length - _pos;
            if (rem <= 0)
            {
                return 0;
            }

            int take = Math.Min(count, rem);
            Buffer.BlockCopy(_data, _pos, buffer, offset, take);
            _pos += take;
            return take;
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
