using System;
using System.IO;
using Stb.Managed.Oracle;
using Xunit;

namespace Stb.Managed.Tests.ImageWrite;

public sealed class PngWriterOracleTests
{
    [Theory]
    [InlineData(1, 1, ColorComponents.Grey, PngFilterMode.Auto, 8, false)]
    [InlineData(17, 13, ColorComponents.Grey, PngFilterMode.None, 8, false)]
    [InlineData(17, 13, ColorComponents.GreyAlpha, PngFilterMode.Sub, 5, true)]
    [InlineData(23, 19, ColorComponents.RedGreenBlue, PngFilterMode.Up, 8, false)]
    [InlineData(23, 19, ColorComponents.RedGreenBlue, PngFilterMode.Average, 8, true)]
    [InlineData(29, 21, ColorComponents.RedGreenBlueAlpha, PngFilterMode.Paeth, 8, false)]
    [InlineData(37, 29, ColorComponents.RedGreenBlueAlpha, PngFilterMode.Auto, 8, false)]
    [InlineData(37, 29, ColorComponents.RedGreenBlueAlpha, PngFilterMode.Auto, 10, true)]
    [InlineData(64, 48, ColorComponents.RedGreenBlueAlpha, PngFilterMode.Auto, 3, false)] // clamps < 5 to 5
    public void WritePngStbCompatibleMatchesOracleByteExact(
        int width,
        int height,
        ColorComponents components,
        PngFilterMode filter,
        int compressionLevel,
        bool flip)
    {
        int comp = (int)components;
        byte[] pixels = ImageWriteTestHelpers.CreateGradientPixels(width, height, comp, seed: (width * 53) + (height * 19) + comp);

        byte[] expected = StbOracle.WritePng(
            width,
            height,
            comp,
            pixels,
            strideBytes: 0,
            compressionLevel: compressionLevel,
            forceFilter: (int)filter,
            flipVertically: flip);

        ImageWriteOptions opts = new()
        {
            FlipVertically = flip,
            PngFilter = filter,
            PngCompressionLevel = compressionLevel,
            PngCompressionEngine = PngCompressionEngine.StbCompatible,
        };

        byte[] actual = StbImageWrite.WritePng(pixels, width, height, components, options: opts);
        ImageWriteTestHelpers.AssertBytesEqual(expected, actual, $"PNG StbCompatible {width}x{height}x{comp} filter={filter} lvl={compressionLevel} flip={flip}");

        // Stream overload
        using MemoryStream ms = new();
        StbImageWrite.WritePng(ms, pixels, width, height, components, options: opts);
        ImageWriteTestHelpers.AssertBytesEqual(expected, ms.ToArray(), $"PNG Stream {width}x{height}x{comp}");

        // TryWrite exact-fit & too-small
        byte[] dest = new byte[expected.Length];
        Assert.True(StbImageWrite.TryWritePng(pixels, width, height, components, dest, out int written, options: opts));
        Assert.Equal(expected.Length, written);
        ImageWriteTestHelpers.AssertBytesEqual(expected, dest, $"PNG TryWrite {width}x{height}x{comp}");

        byte[] small = new byte[expected.Length - 1];
        Assert.False(StbImageWrite.TryWritePng(pixels, width, height, components, small, out int smallWritten, options: opts));
        Assert.Equal(0, smallWritten);

        // Strided input matches tight output
        byte[] strided = ImageWriteTestHelpers.AddRowPadding(pixels, width, height, comp, extraPadBytes: 13);
        byte[] fromStrided = StbImageWrite.WritePng(strided, width, height, components, strideBytes: (width * comp) + 13, options: opts);
        ImageWriteTestHelpers.AssertBytesEqual(expected, fromStrided, $"PNG strided {width}x{height}x{comp}");
    }

    [Fact]
    public void WritePngStbCompatibleIncompressibleNoiseMatchesUncompressedFallbackByteExact()
    {
        // High-entropy noise > 32767 bytes exercises stb's multi-block stored DEFLATE fallback.
        const int width = 128;
        const int height = 80;
        const int comp = 4; // 41,040 filtered bytes -> 2 stored blocks (32767 + 8273)
        byte[] pixels = ImageWriteTestHelpers.CreateRandomPixels(width, height, comp, seed: 2026);

        byte[] expected = StbOracle.WritePng(width, height, comp, pixels, compressionLevel: 8, forceFilter: 0);
        ImageWriteOptions opts = new()
        {
            PngFilter = PngFilterMode.None,
            PngCompressionLevel = 8,
            PngCompressionEngine = PngCompressionEngine.StbCompatible,
        };

        byte[] actual = StbImageWrite.WritePng(pixels, width, height, ColorComponents.RedGreenBlueAlpha, options: opts);
        ImageWriteTestHelpers.AssertBytesEqual(expected, actual, "PNG StbCompatible uncompressed fallback (>32K noise)");
    }

    [Theory]
    [InlineData(ColorComponents.Grey, 0, false)]
    [InlineData(ColorComponents.GreyAlpha, 1, true)]
    [InlineData(ColorComponents.RedGreenBlue, 6, false)]
    [InlineData(ColorComponents.RedGreenBlueAlpha, 8, false)]
    [InlineData(ColorComponents.RedGreenBlueAlpha, 9, true)]
    public void WritePngDefaultZLibEngineRoundTripsLosslesslyWithChecksumVerification(
        ColorComponents components,
        int compressionLevel,
        bool flip)
    {
        const int width = 64;
        const int height = 48;
        int comp = (int)components;
        byte[] pixels = ImageWriteTestHelpers.CreateGradientPixels(width, height, comp, seed: 9001 + comp);

        ImageWriteOptions opts = new()
        {
            FlipVertically = flip,
            PngCompressionLevel = compressionLevel,
            PngCompressionEngine = PngCompressionEngine.ZLib,
        };

        byte[] pngBytes = StbImageWrite.WritePng(pixels, width, height, components, options: opts);

        // Verify strict CRC-32 and zlib Adler-32 via StbImage with VerifyChecksums=true
        ImageBuffer<byte> decoded = StbImage.Load(
            pngBytes,
            options: new ImageDecodeOptions { VerifyChecksums = true, FlipVertically = flip });
        Assert.Equal(width, decoded.Width);
        Assert.Equal(height, decoded.Height);
        Assert.Equal(components, decoded.Components);
        ImageWriteTestHelpers.AssertBytesEqual(pixels, decoded.AsReadOnlySpan(), $"PNG ZLib round-trip comp={comp} lvl={compressionLevel}");

        // Also verify native stb_image decodes identical pixels
        OracleImage<byte>? oracleDecoded = StbOracle.LoadImage(pngBytes, flipVertically: flip);
        Assert.NotNull(oracleDecoded);
        ImageWriteTestHelpers.AssertBytesEqual(pixels, oracleDecoded.AsSpan(), $"PNG ZLib native oracle decode comp={comp}");
    }

    [Fact]
    public void WritePngZLibEngineProducesSmallerOutputThanStbBuiltinOnNaturalGradient()
    {
        byte[] pixels = ImageWriteTestHelpers.CreateGradientPixels(128, 128, 4, seed: 123);
        ImageBuffer<byte> img = new(128, 128, ColorComponents.RedGreenBlueAlpha, ColorComponents.RedGreenBlueAlpha, pixels);

        byte[] zlibPng = StbImageWrite.WritePng(img, new ImageWriteOptions { PngCompressionEngine = PngCompressionEngine.ZLib });
        byte[] stbPng = StbImageWrite.WritePng(img, new ImageWriteOptions { PngCompressionEngine = PngCompressionEngine.StbCompatible });

        Assert.True(
            zlibPng.Length <= stbPng.Length,
            $"Expected platform ZLib PNG ({zlibPng.Length} B) to be <= StbCompatible ({stbPng.Length} B).");
    }
}
