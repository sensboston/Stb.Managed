using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;
using Stb.Managed.Internal;
using Stb.Managed.Oracle;
using Xunit;

namespace Stb.Managed.Tests.Image;

public sealed class PngMalformedInputTests
{
    [Fact]
    public void PngSuiteCorruptMatchesOracleDefaultAndAllFailWithVerifyChecksums()
    {
        string? corpusRoot = CorpusHelper.TryGetCorpusRoot();
        if (corpusRoot is null)
        {
            Assert.False(CorpusHelper.IsRunningInCi, "Corpus directory is missing in CI.");
            return;
        }

        string corruptDir = Path.Combine(corpusRoot, "stb", "tests", "pngsuite", "corrupt");
        string[] files = Directory.GetFiles(corruptDir, "*.png");
        Array.Sort(files, StringComparer.Ordinal);
        Assert.NotEmpty(files);

        ImageDecodeOptions strict = new() { VerifyChecksums = true };

        foreach (string file in files)
        {
            string name = Path.GetFileName(file);
            byte[] data = File.ReadAllBytes(file);

            OracleImage<byte>? oracle = StbOracle.LoadImage(data);
            if (oracle is not null)
            {
                // Native stb ignores bad chunk CRCs by default (xcsn0g01.png, xhdn0g08.png).
                ImageBuffer<byte> managed = StbImage.Load(data);
                Assert.True(
                    oracle.AsSpan().SequenceEqual(managed.AsSpan()),
                    $"Default-mode output mismatch on '{name}'.");

                // With VerifyChecksums enabled, bad CRC must be rejected.
                Assert.Throws<StbException>(() => StbImage.Load(data, options: strict));
            }
            else
            {
                Assert.Throws<StbException>(() => StbImage.Load(data));
                Assert.Throws<StbException>(() => StbImage.Load(data, options: strict));
            }
        }
    }

    [Fact]
    public void GeneratedMalformedPngsAlwaysThrowStbException()
    {
        string? corpusRoot = CorpusHelper.TryGetCorpusRoot();
        if (corpusRoot is null)
        {
            Assert.False(CorpusHelper.IsRunningInCi, "Corpus directory is missing in CI.");
            return;
        }

        string malformedDir = Path.Combine(corpusRoot, "generated", "png_malformed");
        string[] files = Directory.GetFiles(malformedDir, "*.png");
        Array.Sort(files, StringComparer.Ordinal);
        Assert.NotEmpty(files);

        foreach (string file in files)
        {
            string name = Path.GetFileName(file);
            byte[] data = File.ReadAllBytes(file);

            StbException ex = Assert.Throws<StbException>(() => StbImage.Load(data));
            Assert.NotEqual(StbErrorCode.InternalError, ex.Code);

            // TryGetInfo must never throw an unhandled exception on malformed inputs.
            _ = StbImage.TryGetInfo(data, out _);
            Assert.Null(StbOracle.LoadImage(data));
            Assert.False(string.IsNullOrEmpty(name));
        }
    }

    [Fact]
    public void CorruptZlibAdler32IgnoredByDefaultAndCaughtWhenVerifyChecksumsEnabled()
    {
        byte[] pixels = [10, 20, 30, 255, 40, 50, 60, 255, 70, 80, 90, 255, 100, 110, 120, 255];
        byte[] png = StbOracle.WritePng(2, 2, 4, pixels);

        // Locate IDAT chunk, flip a bit in the final Adler-32 byte, and recompute IDAT chunk CRC-32.
        byte[] mutated = (byte[])png.Clone();
        int pos = 8;
        bool patched = false;
        while (pos + 12 <= mutated.Length)
        {
            int len = (int)BinaryPrimitives.ReadUInt32BigEndian(mutated.AsSpan(pos, 4));
            string type = Encoding.ASCII.GetString(mutated, pos + 4, 4);
            if (string.Equals(type, "IDAT", StringComparison.Ordinal))
            {
                mutated[pos + 8 + len - 1] ^= 0x01;
                uint newCrc = Checksums.ComputeCrc32(mutated.AsSpan(pos + 4, 4), mutated.AsSpan(pos + 8, len));
                BinaryPrimitives.WriteUInt32BigEndian(mutated.AsSpan(pos + 8 + len, 4), newCrc);
                patched = true;
                break;
            }

            pos += 12 + len;
        }

        Assert.True(patched);

        // Default mode matches stb_image (ignores Adler-32).
        ImageBuffer<byte> decoded = StbImage.Load(mutated);
        Assert.True(decoded.AsSpan().SequenceEqual(pixels));

        // VerifyChecksums catches the corrupt Adler-32.
        ImageDecodeOptions strict = new() { VerifyChecksums = true };
        StbException ex = Assert.Throws<StbException>(() => StbImage.Load(mutated, options: strict));
        Assert.Equal(StbErrorCode.CorruptStream, ex.Code);
    }

    [Fact]
    public void ByteByByteTruncationNeverThrowsUnhandledExceptions()
    {
        byte[] pixels = new byte[8 * 8 * 4];
        for (int i = 0; i < pixels.Length; i++)
        {
            pixels[i] = (byte)(i * 13);
        }

        byte[] png = StbOracle.WritePng(8, 8, 4, pixels);

        for (int len = 0; len < png.Length; len++)
        {
            int capturedLen = len;
            Assert.Throws<StbException>(() => StbImage.Load(png.AsSpan(0, capturedLen)));
            Assert.Throws<StbException>(() => StbImage.Load16(png.AsSpan(0, capturedLen)));
            _ = StbImage.TryGetInfo(png.AsSpan(0, capturedLen), out _);
        }
    }

    [Fact]
    public void SingleByteMutationsNeverThrowUnhandledExceptions()
    {
        byte[] pixels = new byte[9 * 7 * 4];
        uint state = 0x98765432u;
        for (int i = 0; i < pixels.Length; i++)
        {
            state = NextUInt32(state);
            pixels[i] = (byte)state;
        }

        byte[] png = StbOracle.WritePng(9, 7, 4, pixels);

        for (int iter = 0; iter < 250; iter++)
        {
            byte[] mutated = (byte[])png.Clone();
            state = NextUInt32(state);
            int index = (int)(state % (uint)mutated.Length);
            state = NextUInt32(state);
            mutated[index] ^= (byte)(1 << (int)(state & 7u));

            try
            {
                _ = StbImage.Load(mutated);
            }
            catch (StbException)
            {
                // Expected for malformed mutations.
            }
        }
    }

    [Fact]
    public void SafetyLimitsRejectOversizedHeadersAndZipBombs()
    {
        // 1. Small valid PNG rejected by strict dimension limits.
        byte[] validPng = StbOracle.WritePng(16, 16, 4, new byte[16 * 16 * 4]);
        ImageDecodeOptions tinyLimit = new()
        {
            Options = new StbOptions
            {
                Limits = new StbLimits { MaxWidth = 8, MaxHeight = 8 },
            },
        };
        StbException dimEx = Assert.Throws<StbException>(() => StbImage.Load(validPng, options: tinyLimit));
        Assert.Equal(StbErrorCode.LimitExceeded, dimEx.Code);

        // 2. Hostile IHDR claiming 100000x100000 rejected immediately without huge allocation.
        byte[] hostileHeader = BuildPngWithRawIhdrAndIdat(100_000, 100_000, [0x78, 0x01, 0x03, 0x00, 0x00, 0x00, 0x00, 0x01]);
        StbException hdrEx = Assert.Throws<StbException>(() => StbImage.Load(hostileHeader));
        Assert.Equal(StbErrorCode.LimitExceeded, hdrEx.Code);

        // 3. Zip bomb: 1x1 RGBA PNG whose zlib stream decompresses to 2 MB of zeros.
        byte[] bombRaw = new byte[2 * 1024 * 1024];
        byte[] bombZlib;
        using (MemoryStream ms = new())
        {
            using (ZLibStream zs = new(ms, CompressionLevel.Optimal, leaveOpen: true))
            {
                zs.Write(bombRaw, 0, bombRaw.Length);
            }

            bombZlib = ms.ToArray();
        }

        byte[] bombPng = BuildPngWithRawIhdrAndIdat(1, 1, bombZlib);
        ImageDecodeOptions tightDecomp = new()
        {
            Options = new StbOptions
            {
                Limits = new StbLimits { MaxDecompressedBytes = 64 * 1024 },
            },
        };

        StbException bombEx = Assert.Throws<StbException>(() => StbImage.Load(bombPng, options: tightDecomp));
        Assert.Equal(StbErrorCode.LimitExceeded, bombEx.Code);

        // 4. Pipelined parallel decoder must propagate consumer (bad filter) and producer (truncated stream) errors cleanly without hanging.
        byte[] bigRaw = new byte[512 * ((512 * 4) + 1)];
        bigRaw[100 * ((512 * 4) + 1)] = 99; // invalid filter byte mid-image (forces consumer abort while producer runs)
        byte[] bigZlib;
        using (MemoryStream ms = new())
        {
            using (ZLibStream zs = new(ms, CompressionLevel.Fastest, leaveOpen: true))
            {
                zs.Write(bigRaw, 0, bigRaw.Length);
            }

            bigZlib = ms.ToArray();
        }

        byte[] badFilterBigPng = BuildPngWithRawIhdrAndIdat(512, 512, bigZlib);
        ImageDecodeOptions parallelOpts = new() { Options = new StbOptions { MaxDegreeOfParallelism = 2 } };
        StbException filterEx = Assert.Throws<StbException>(() => StbImage.Load(badFilterBigPng, options: parallelOpts));
        Assert.Equal(StbErrorCode.InvalidData, filterEx.Code);

        // Truncated deflate stream in parallel mode (valid filter bytes, stream cut off mid-image).
        bigRaw[100 * ((512 * 4) + 1)] = 0;
        byte[] validBigZlib;
        using (MemoryStream ms = new())
        {
            using (ZLibStream zs = new(ms, CompressionLevel.Fastest, leaveOpen: true))
            {
                zs.Write(bigRaw, 0, bigRaw.Length);
            }

            validBigZlib = ms.ToArray();
        }

        byte[] truncatedZlib = validBigZlib.AsSpan(0, validBigZlib.Length / 2).ToArray();
        byte[] truncBigPng = BuildPngWithRawIhdrAndIdat(512, 512, truncatedZlib);
        StbException truncEx = Assert.Throws<StbException>(() => StbImage.Load(truncBigPng, options: parallelOpts));
        Assert.Equal(StbErrorCode.CorruptStream, truncEx.Code);
    }

    [Fact]
    public void InvalidArgumentsAndNonPngInputsAreRejectedCleanly()
    {
        Assert.Equal(ImageFormat.Unknown, StbImage.DetectFormat(ReadOnlySpan<byte>.Empty));
        Assert.Equal(ImageFormat.Unknown, StbImage.DetectFormat("not a png"u8));

        Assert.False(StbImage.TryGetInfo(ReadOnlySpan<byte>.Empty, out _));
        Assert.False(StbImage.TryGetInfo("not a png"u8, out _));

        StbException fmtEx = Assert.Throws<StbException>(() => StbImage.Load("not a png"u8));
        Assert.Equal(StbErrorCode.UnsupportedFormat, fmtEx.Code);

        byte[] validPng = StbOracle.WritePng(2, 2, 4, new byte[16]);
        StbException argEx = Assert.Throws<StbException>(() => StbImage.Load(validPng, (ColorComponents)5));
        Assert.Equal(StbErrorCode.InvalidArgument, argEx.Code);

        StbException negArgEx = Assert.Throws<StbException>(() => StbImage.Load16(validPng, (ColorComponents)(-1)));
        Assert.Equal(StbErrorCode.InvalidArgument, negArgEx.Code);
    }

    private static uint NextUInt32(uint state)
    {
        state ^= state << 13;
        state ^= state >> 17;
        state ^= state << 5;
        return state;
    }

    private static byte[] BuildPngWithRawIhdrAndIdat(int width, int height, ReadOnlySpan<byte> idatPayload)
    {
        using MemoryStream ms = new();
        ms.Write([0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A]);

        byte[] ihdr = new byte[13];
        BinaryPrimitives.WriteInt32BigEndian(ihdr.AsSpan(0, 4), width);
        BinaryPrimitives.WriteInt32BigEndian(ihdr.AsSpan(4, 4), height);
        ihdr[8] = 8; // bit depth
        ihdr[9] = 6; // RGBA
        WriteChunk(ms, "IHDR"u8, ihdr);
        WriteChunk(ms, "IDAT"u8, idatPayload);
        WriteChunk(ms, "IEND"u8, ReadOnlySpan<byte>.Empty);
        return ms.ToArray();
    }

    private static void WriteChunk(Stream output, ReadOnlySpan<byte> type, ReadOnlySpan<byte> payload)
    {
        Span<byte> header = stackalloc byte[8];
        BinaryPrimitives.WriteInt32BigEndian(header.Slice(0, 4), payload.Length);
        type.CopyTo(header.Slice(4, 4));
        output.Write(header);
        output.Write(payload);

        Span<byte> crcBytes = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(crcBytes, Checksums.ComputeCrc32(type, payload));
        output.Write(crcBytes);
    }
}
