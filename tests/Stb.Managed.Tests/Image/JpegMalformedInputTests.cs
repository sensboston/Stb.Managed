using Xunit;

namespace Stb.Managed.Tests.Image;

public sealed class JpegMalformedInputTests
{
    [Fact]
    public void AllGeneratedMalformedJpegsAreRejectedCleanly()
    {
        string? corpusRoot = CorpusHelper.TryGetCorpusRoot();
        if (corpusRoot is null)
        {
            Assert.False(CorpusHelper.IsRunningInCi, "Corpus directory is missing in CI.");
            return;
        }

        string malDir = Path.Combine(corpusRoot, "generated", "jpeg_malformed");
        Assert.True(Directory.Exists(malDir), $"Expected malformed JPEG directory '{malDir}'.");

        string[] files = Directory.GetFiles(malDir, "*.jpg");
        Array.Sort(files, StringComparer.Ordinal);
        Assert.True(files.Length >= 18, $"Expected at least 18 malformed JPEG fixtures, found {files.Length}.");

        foreach (string file in files)
        {
            byte[] data = File.ReadAllBytes(file);
            Assert.Throws<StbException>(() => StbImage.Load(data));
        }
    }

    [Fact]
    public void ExhaustivePrefixTruncationsNeverCrashOrHang()
    {
        string? corpusRoot = CorpusHelper.TryGetCorpusRoot();
        if (corpusRoot is null)
        {
            Assert.False(CorpusHelper.IsRunningInCi, "Corpus directory is missing in CI.");
            return;
        }

        string[] targets =
        [
            Path.Combine("generated", "jpeg_valid", "baseline_420_32x32.jpg"),
            Path.Combine("generated", "jpeg_valid", "baseline_420_dri1_48x48.jpg"),
            Path.Combine("generated", "jpeg_valid", "prog_succ_approx_420_49x35.jpg"),
            Path.Combine("generated", "jpeg_valid", "sof1_16bit_dqt_32x24.jpg"),
        ];

        foreach (string relPath in targets)
        {
            byte[] full = File.ReadAllBytes(Path.Combine(corpusRoot, relPath));
            for (int len = 0; len < full.Length; len++)
            {
                byte[] truncated = new byte[len];
                Buffer.BlockCopy(full, 0, truncated, 0, len);

                // Must never crash with non-StbException or hang.
                try
                {
                    _ = StbImage.Load(truncated);
                }
                catch (StbException)
                {
                    // Expected for truncated inputs.
                }

                // TryGetInfo must never throw.
                _ = StbImage.TryGetInfo(truncated, out _);
            }
        }
    }

    [Fact]
    public void DeterministicMutationsNeverCrashOrHang()
    {
        string? corpusRoot = CorpusHelper.TryGetCorpusRoot();
        if (corpusRoot is null)
        {
            Assert.False(CorpusHelper.IsRunningInCi, "Corpus directory is missing in CI.");
            return;
        }

        string[] seeds =
        [
            Path.Combine("generated", "jpeg_valid", "baseline_420_32x32.jpg"),
            Path.Combine("generated", "jpeg_valid", "baseline_420_dri3_65x49.jpg"),
            Path.Combine("generated", "jpeg_valid", "prog_succ_approx_420_49x35.jpg"),
            Path.Combine("generated", "jpeg_valid", "color_ycck_24x24.jpg"),
        ];

        // Bound limits tightly so a mutated width/height never tries to allocate gigabytes.
        ImageDecodeOptions tightOptions = new()
        {
            Options = new StbOptions
            {
                Limits = new StbLimits
                {
                    MaxWidth = 512,
                    MaxHeight = 512,
                    MaxPixels = 512 * 512,
                    MaxAllocationBytes = 8 * 1024 * 1024,
                },
            },
        };

        uint rng = 0x5EED1234u;
        foreach (string seed in seeds)
        {
            byte[] original = File.ReadAllBytes(Path.Combine(corpusRoot, seed));
            byte[] mutated = new byte[original.Length];

            for (int trial = 0; trial < 80; trial++)
            {
                Buffer.BlockCopy(original, 0, mutated, 0, original.Length);
                int flips = 1 + (int)(NextUInt(ref rng) % 4u);
                for (int f = 0; f < flips; f++)
                {
                    int idx = (int)(NextUInt(ref rng) % (uint)mutated.Length);
                    mutated[idx] = (byte)NextUInt(ref rng);
                }

                try
                {
                    _ = StbImage.Load(mutated, ColorComponents.Default, tightOptions);
                }
                catch (StbException)
                {
                    // Expected for corrupt mutations.
                }
            }
        }
    }

    [Fact]
    public void SafetyLimitsAreEnforcedOnGetInfoAndLoad()
    {
        string? corpusRoot = CorpusHelper.TryGetCorpusRoot();
        if (corpusRoot is null)
        {
            Assert.False(CorpusHelper.IsRunningInCi, "Corpus directory is missing in CI.");
            return;
        }

        byte[] jpeg = File.ReadAllBytes(Path.Combine(corpusRoot, "generated", "jpeg_valid", "baseline_420_63x65.jpg"));

        ImageDecodeOptions maxWidthLimit = new()
        {
            Options = new StbOptions { Limits = new StbLimits { MaxWidth = 32 } },
        };
        StbException exW = Assert.Throws<StbException>(() => StbImage.GetInfo(jpeg, maxWidthLimit));
        Assert.Equal(StbErrorCode.LimitExceeded, exW.Code);
        Assert.False(StbImage.TryGetInfo(jpeg, out _, maxWidthLimit));

        StbException exLoadW = Assert.Throws<StbException>(() => StbImage.Load(jpeg, ColorComponents.Default, maxWidthLimit));
        Assert.Equal(StbErrorCode.LimitExceeded, exLoadW.Code);

        ImageDecodeOptions maxHeightLimit = new()
        {
            Options = new StbOptions { Limits = new StbLimits { MaxHeight = 32 } },
        };
        StbException exH = Assert.Throws<StbException>(() => StbImage.Load(jpeg, ColorComponents.Default, maxHeightLimit));
        Assert.Equal(StbErrorCode.LimitExceeded, exH.Code);

        ImageDecodeOptions maxPixelsLimit = new()
        {
            Options = new StbOptions { Limits = new StbLimits { MaxPixels = 1000 } },
        };
        StbException exP = Assert.Throws<StbException>(() => StbImage.Load(jpeg, ColorComponents.Default, maxPixelsLimit));
        Assert.Equal(StbErrorCode.LimitExceeded, exP.Code);

        ImageDecodeOptions maxAllocLimit = new()
        {
            Options = new StbOptions { Limits = new StbLimits { MaxAllocationBytes = 4096 } },
        };
        StbException exA = Assert.Throws<StbException>(() => StbImage.Load(jpeg, ColorComponents.Default, maxAllocLimit));
        Assert.Equal(StbErrorCode.LimitExceeded, exA.Code);
    }

    [Fact]
    public void InvalidDesiredComponentsThrowsInvalidArgument()
    {
        string? corpusRoot = CorpusHelper.TryGetCorpusRoot();
        if (corpusRoot is null)
        {
            Assert.False(CorpusHelper.IsRunningInCi, "Corpus directory is missing in CI.");
            return;
        }

        byte[] jpeg = File.ReadAllBytes(Path.Combine(corpusRoot, "generated", "jpeg_valid", "baseline_420_1x1.jpg"));

        StbException exNeg = Assert.Throws<StbException>(() => StbImage.Load(jpeg, (ColorComponents)(-1)));
        Assert.Equal(StbErrorCode.InvalidArgument, exNeg.Code);

        StbException exHigh = Assert.Throws<StbException>(() => StbImage.Load(jpeg, (ColorComponents)5));
        Assert.Equal(StbErrorCode.InvalidArgument, exHigh.Code);
    }

    [Fact]
    public void DetectFormatHandlesLeadingFillBytesAndRejectsAllFf()
    {
        Assert.Equal(ImageFormat.Jpeg, StbImage.DetectFormat([0xFF, 0xD8]));
        Assert.Equal(ImageFormat.Jpeg, StbImage.DetectFormat([0xFF, 0xFF, 0xFF, 0xD8]));
        Assert.Equal(ImageFormat.Unknown, StbImage.DetectFormat([0xFF, 0xFF, 0xFF]));
        Assert.Equal(ImageFormat.Unknown, StbImage.DetectFormat([0xFF]));
        Assert.Equal(ImageFormat.Unknown, StbImage.DetectFormat([]));
    }

    private static uint NextUInt(ref uint state)
    {
        uint x = state;
        x ^= x << 13;
        x ^= x >> 17;
        x ^= x << 5;
        state = x;
        return x;
    }
}
