using System;
using System.IO;
using Stb.Managed.Oracle;
using Xunit;

namespace Stb.Managed.Tests.ImageWrite;

public sealed class BmpWriterOracleTests
{
    [Theory]
    [InlineData(1, 1, ColorComponents.Grey, false)]
    [InlineData(2, 3, ColorComponents.Grey, true)]
    [InlineData(3, 5, ColorComponents.GreyAlpha, false)]
    [InlineData(4, 4, ColorComponents.GreyAlpha, true)]
    [InlineData(5, 7, ColorComponents.RedGreenBlue, false)]
    [InlineData(6, 5, ColorComponents.RedGreenBlue, true)]
    [InlineData(7, 9, ColorComponents.RedGreenBlue, false)]
    [InlineData(13, 11, ColorComponents.RedGreenBlue, true)]
    [InlineData(1, 1, ColorComponents.RedGreenBlueAlpha, false)]
    [InlineData(7, 5, ColorComponents.RedGreenBlueAlpha, false)]
    [InlineData(16, 12, ColorComponents.RedGreenBlueAlpha, true)]
    [InlineData(33, 29, ColorComponents.RedGreenBlueAlpha, false)]
    public void WriteBmpMatchesOracleByteExact(int width, int height, ColorComponents components, bool flip)
    {
        int comp = (int)components;
        byte[] pixels = ImageWriteTestHelpers.CreateGradientPixels(width, height, comp, seed: (width * 97) + (height * 31) + comp);

        byte[] expected = StbOracle.WriteBmp(width, height, comp, pixels, flipVertically: flip);
        ImageWriteOptions opts = new() { FlipVertically = flip };

        byte[] actual = StbImageWrite.WriteBmp(pixels, width, height, components, options: opts);
        ImageWriteTestHelpers.AssertBytesEqual(expected, actual, $"BMP {width}x{height}x{comp} flip={flip}");

        // Stream overload
        using MemoryStream ms = new();
        StbImageWrite.WriteBmp(ms, pixels, width, height, components, options: opts);
        ImageWriteTestHelpers.AssertBytesEqual(expected, ms.ToArray(), $"BMP Stream {width}x{height}x{comp}");

        // TryWrite exact-fit & too-small
        byte[] dest = new byte[expected.Length];
        Assert.True(StbImageWrite.TryWriteBmp(pixels, width, height, components, dest, out int written, options: opts));
        Assert.Equal(expected.Length, written);
        ImageWriteTestHelpers.AssertBytesEqual(expected, dest, $"BMP TryWrite {width}x{height}x{comp}");

        if (expected.Length > 0)
        {
            byte[] small = new byte[expected.Length - 1];
            Assert.False(StbImageWrite.TryWriteBmp(pixels, width, height, components, small, out int smallWritten, options: opts));
            Assert.Equal(0, smallWritten);
        }

        // Strided input matches tight output byte-for-byte
        byte[] strided = ImageWriteTestHelpers.AddRowPadding(pixels, width, height, comp, extraPadBytes: 7);
        byte[] fromStrided = StbImageWrite.WriteBmp(strided, width, height, components, strideBytes: (width * comp) + 7, options: opts);
        ImageWriteTestHelpers.AssertBytesEqual(expected, fromStrided, $"BMP strided {width}x{height}x{comp}");

        // Round-trip decode with StbImage matches native stb_image
        OracleImage<byte>? oracleDecoded = StbOracle.LoadImage(actual);
        Assert.NotNull(oracleDecoded);
        ImageBuffer<byte> managedDecoded = StbImage.Load(actual);
        Assert.Equal(width, managedDecoded.Width);
        Assert.Equal(height, managedDecoded.Height);
        ImageWriteTestHelpers.AssertBytesEqual(oracleDecoded.AsSpan(), managedDecoded.AsReadOnlySpan(), $"BMP round-trip {width}x{height}x{comp}");
    }

    [Fact]
    public void WriteBmpImageBufferOverloadsMatchOracle()
    {
        byte[] pixels = ImageWriteTestHelpers.CreateGradientPixels(19, 13, 4);
        ImageBuffer<byte> img = new(19, 13, ColorComponents.RedGreenBlueAlpha, ColorComponents.RedGreenBlueAlpha, pixels);

        byte[] expected = StbOracle.WriteBmp(19, 13, 4, pixels);
        ImageWriteTestHelpers.AssertBytesEqual(expected, StbImageWrite.WriteBmp(img), "BMP ImageBuffer byte[]");

        using MemoryStream ms = new();
        StbImageWrite.WriteBmp(ms, img);
        ImageWriteTestHelpers.AssertBytesEqual(expected, ms.ToArray(), "BMP ImageBuffer Stream");
    }
}
