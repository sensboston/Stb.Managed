using System.Buffers.Binary;
using System.IO.Compression;
using Stb.Managed.Oracle;
using Xunit;

namespace Stb.Managed.Tests.Oracle;

public sealed class OracleSmokeTests
{
    [Fact]
    public void NativeOracleLoadsAndReportsExpectedAbiVersion()
    {
        Assert.Equal(1, StbOracle.AbiVersion);
    }

    [Fact]
    public void NativeOracleDecodesPngAndMatchesExactPixels()
    {
        // Build a deterministic 2x2 RGBA PNG in memory so this test never depends on external files.
        byte[] expectedPixels =
        [
            255,   0,   0, 255,    0, 255,   0, 255,
              0,   0, 255, 128,   64, 128, 192, 255,
        ];
        byte[] png = BuildMinimalRgbaPng(2, 2, expectedPixels);

        OracleImageInfo? info = StbOracle.GetImageInfo(png);
        Assert.NotNull(info);
        Assert.Equal(OracleImageType.Png, info.Value.Type);
        Assert.Equal(2, info.Value.Width);
        Assert.Equal(2, info.Value.Height);
        Assert.Equal(4, info.Value.Channels);
        Assert.False(info.Value.Is16Bit);
        Assert.False(info.Value.IsHdr);

        OracleImage<byte>? decoded = StbOracle.LoadImage(png, desiredChannels: 4);
        Assert.NotNull(decoded);
        Assert.Equal(2, decoded.Width);
        Assert.Equal(2, decoded.Height);
        Assert.Equal(4, decoded.Channels);
        Assert.Equal(4, decoded.SourceChannels);
        Assert.True(decoded.AsSpan().SequenceEqual(expectedPixels));
    }

    [Fact]
    public void NativeOracleWritePngRoundTripsLosslessly()
    {
        byte[] pixels = new byte[16 * 12 * 4];
        for (int i = 0; i < pixels.Length; i++)
        {
            pixels[i] = (byte)((i * 37 + 11) & 0xFF);
        }

        byte[] encoded = StbOracle.WritePng(16, 12, 4, pixels);
        Assert.NotEmpty(encoded);

        OracleImage<byte>? decoded = StbOracle.LoadImage(encoded, desiredChannels: 4);
        Assert.NotNull(decoded);
        Assert.Equal(16, decoded.Width);
        Assert.Equal(12, decoded.Height);
        Assert.True(decoded.AsSpan().SequenceEqual(pixels));
    }

    [Fact]
    public void NativeOracleResizePackDxtPerlinAllWork()
    {
        // Resize 4x4 RGBA -> 2x2 RGBA (STBIR_RGBA = 4, STBIR_TYPE_UINT8 = 0).
        byte[] src = new byte[4 * 4 * 4];
        Array.Fill(src, (byte)200);
        byte[] dst = new byte[2 * 2 * 4];
        bool resized = StbOracle.ResizeBytes(src, 4, 4, 16, dst, 2, 2, 8, pixelLayout: 4, dataType: 0);
        Assert.True(resized);
        Assert.All(dst, b => Assert.Equal((byte)200, b));

        // RectPack.
        OracleRect[] rects =
        [
            new(Id: 1, Width: 10, Height: 10, X: 0, Y: 0, WasPacked: 0),
            new(Id: 2, Width: 20, Height: 15, X: 0, Y: 0, WasPacked: 0),
        ];
        bool packed = StbOracle.PackRects(64, 64, rects);
        Assert.True(packed);
        Assert.All(rects, r => Assert.NotEqual(0, r.WasPacked));

        // DXT / BC4 / BC5.
        byte[] rgbaBlock = new byte[64];
        Array.Fill(rgbaBlock, (byte)128);
        byte[] dxt1 = new byte[8];
        byte[] dxt5 = new byte[16];
        StbOracle.CompressDxtBlock(dxt1, rgbaBlock, alpha: false);
        StbOracle.CompressDxtBlock(dxt5, rgbaBlock, alpha: true);

        byte[] bc4 = new byte[8];
        byte[] bc5 = new byte[16];
        StbOracle.CompressBc4Block(bc4, rgbaBlock.AsSpan(0, 16));
        StbOracle.CompressBc5Block(bc5, rgbaBlock.AsSpan(0, 32));

        // Perlin.
        float noise = StbOracle.PerlinNoise3(1.25f, 2.5f, 3.75f, seed: 42);
        Assert.InRange(noise, -2.0f, 2.0f);
    }

    [Fact]
    public void NativeOracleDecodesCorpusPngsAndRejectsMalformedOnes()
    {
        string? corpusRoot = CorpusHelper.TryGetCorpusRoot();
        if (corpusRoot is null)
        {
            Assert.False(
                CorpusHelper.IsRunningInCi,
                "Corpus directory 'artifacts/corpus' is missing in CI (run: dotnet run --project tools/Stb.Managed.Tools -- fetch-corpus).");
            return;
        }

        string primaryDir = Path.Combine(corpusRoot, "stb", "tests", "pngsuite", "primary");
        string[] primaryFiles = Directory.GetFiles(primaryDir, "*.png");
        Assert.NotEmpty(primaryFiles);
        foreach (string file in primaryFiles)
        {
            byte[] data = File.ReadAllBytes(file);
            OracleImage<byte>? img = StbOracle.LoadImage(data);
            Assert.True(img is not null, $"Oracle failed on PngSuite primary '{Path.GetFileName(file)}': {StbOracle.GetLastFailureReason()}");
            Assert.True(img.Width > 0 && img.Height > 0);
        }

        string bit16Dir = Path.Combine(corpusRoot, "stb", "tests", "pngsuite", "16bit");
        string[] bit16Files = Directory.GetFiles(bit16Dir, "*.png");
        Assert.NotEmpty(bit16Files);
        foreach (string file in bit16Files)
        {
            byte[] data = File.ReadAllBytes(file);
            OracleImage<ushort>? img16 = StbOracle.LoadImage16(data);
            Assert.True(img16 is not null, $"Oracle failed on PngSuite 16-bit '{Path.GetFileName(file)}': {StbOracle.GetLastFailureReason()}");
        }

        string genValidDir = Path.Combine(corpusRoot, "generated", "png_valid");
        string[] validFiles = Directory.GetFiles(genValidDir, "*.png");
        Assert.NotEmpty(validFiles);
        foreach (string file in validFiles)
        {
            byte[] data = File.ReadAllBytes(file);
            OracleImage<byte>? img = StbOracle.LoadImage(data);
            Assert.True(img is not null, $"Oracle failed on generated valid PNG '{Path.GetFileName(file)}': {StbOracle.GetLastFailureReason()}");
        }

        string genMalformedDir = Path.Combine(corpusRoot, "generated", "png_malformed");
        string[] malformedFiles = Directory.GetFiles(genMalformedDir, "*.png");
        Assert.NotEmpty(malformedFiles);
        foreach (string file in malformedFiles)
        {
            byte[] data = File.ReadAllBytes(file);
            OracleImage<byte>? img = StbOracle.LoadImage(data);
            Assert.True(img is null, $"Oracle unexpectedly decoded malformed PNG '{Path.GetFileName(file)}'.");
        }
    }

    private static byte[] BuildMinimalRgbaPng(int width, int height, ReadOnlySpan<byte> rgba)
    {
        using MemoryStream ms = new();
        // PNG signature.
        ms.Write([0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A]);

        // IHDR.
        byte[] ihdr = new byte[13];
        BinaryPrimitives.WriteUInt32BigEndian(ihdr.AsSpan(0, 4), (uint)width);
        BinaryPrimitives.WriteUInt32BigEndian(ihdr.AsSpan(4, 4), (uint)height);
        ihdr[8] = 8; // bit depth
        ihdr[9] = 6; // RGBA
        WriteChunk(ms, "IHDR"u8, ihdr);

        // Filtered scanlines (filter 0 = None).
        int rowBytes = width * 4;
        byte[] raw = new byte[height * (rowBytes + 1)];
        for (int y = 0; y < height; y++)
        {
            raw[y * (rowBytes + 1)] = 0;
            rgba.Slice(y * rowBytes, rowBytes).CopyTo(raw.AsSpan(y * (rowBytes + 1) + 1, rowBytes));
        }

        byte[] compressed;
        using (MemoryStream zms = new())
        {
            using (ZLibStream zlib = new(zms, CompressionLevel.Optimal, leaveOpen: true))
            {
                zlib.Write(raw);
            }

            compressed = zms.ToArray();
        }

        WriteChunk(ms, "IDAT"u8, compressed);
        WriteChunk(ms, "IEND"u8, []);
        return ms.ToArray();
    }

    private static void WriteChunk(Stream output, ReadOnlySpan<byte> type, ReadOnlySpan<byte> data)
    {
        Span<byte> lenBytes = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(lenBytes, (uint)data.Length);
        output.Write(lenBytes);
        output.Write(type);
        output.Write(data);

        uint crc = Crc32(type, data);
        Span<byte> crcBytes = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(crcBytes, crc);
        output.Write(crcBytes);
    }

    private static uint Crc32(ReadOnlySpan<byte> type, ReadOnlySpan<byte> data)
    {
        uint crc = 0xFFFFFFFFu;
        crc = UpdateCrc(crc, type);
        crc = UpdateCrc(crc, data);
        return crc ^ 0xFFFFFFFFu;
    }

    private static uint UpdateCrc(uint crc, ReadOnlySpan<byte> bytes)
    {
        foreach (byte b in bytes)
        {
            crc ^= b;
            for (int k = 0; k < 8; k++)
            {
                crc = (crc & 1) != 0 ? (crc >> 1) ^ 0xEDB88320u : crc >> 1;
            }
        }

        return crc;
    }
}
