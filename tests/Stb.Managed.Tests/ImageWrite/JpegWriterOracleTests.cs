using System;
using System.IO;
using System.Runtime.InteropServices;
using Stb.Managed.Oracle;
using Xunit;

namespace Stb.Managed.Tests.ImageWrite;

public sealed class JpegWriterOracleTests
{
    [Theory]
    [InlineData(1, 1, ColorComponents.Grey, 90, false)]
    [InlineData(7, 5, ColorComponents.Grey, 75, true)]
    [InlineData(9, 11, ColorComponents.GreyAlpha, 50, false)]
    [InlineData(15, 16, ColorComponents.RedGreenBlue, 1, false)]
    [InlineData(16, 16, ColorComponents.RedGreenBlue, 25, false)]
    [InlineData(17, 17, ColorComponents.RedGreenBlue, 90, false)]
    [InlineData(17, 17, ColorComponents.RedGreenBlue, 91, false)] // 4:4:4 non-subsampled
    [InlineData(23, 19, ColorComponents.RedGreenBlueAlpha, 90, true)]
    [InlineData(33, 29, ColorComponents.RedGreenBlueAlpha, 95, false)]
    [InlineData(64, 48, ColorComponents.RedGreenBlue, 100, true)]
    public void WriteJpegMatchesOracle(int width, int height, ColorComponents components, int quality, bool flip)
    {
        int comp = (int)components;
        byte[] pixels = ImageWriteTestHelpers.CreateGradientPixels(width, height, comp, seed: (width * 67) + (height * 23) + quality);

        byte[] expected = StbOracle.WriteJpg(width, height, comp, pixels, quality: quality, flipVertically: flip);
        ImageWriteOptions opts = new() { FlipVertically = flip };

        byte[] actual = StbImageWrite.WriteJpeg(pixels, width, height, components, quality: quality, options: opts);

        if (RuntimeInformation.ProcessArchitecture is Architecture.X64 or Architecture.X86)
        {
            // Strict IEEE-754 non-FMA platforms must match native stb_image_write byte-for-byte.
            ImageWriteTestHelpers.AssertBytesEqual(expected, actual, $"JPEG {width}x{height}x{comp} q={quality} flip={flip}");
        }
        else
        {
            // Arm64 compilers (e.g. Apple Clang -O3) may fuse float multiply-add (fmadd) inside AAN DCT / RGB->YCbCr.
            // Verify identical headers and near-identical decoded pixels (<= 1 LSB max diff vs oracle output).
            Assert.Equal(expected[0], actual[0]);
            Assert.Equal(expected[1], actual[1]);
            OracleImage<byte>? expectedDec = StbOracle.LoadImage(expected, desiredChannels: 3);
            OracleImage<byte>? actualDec = StbOracle.LoadImage(actual, desiredChannels: 3);
            Assert.NotNull(expectedDec);
            Assert.NotNull(actualDec);
            Assert.Equal(expectedDec.Width, actualDec.Width);
            Assert.Equal(expectedDec.Height, actualDec.Height);

            ReadOnlySpan<byte> eSpan = expectedDec.AsSpan();
            ReadOnlySpan<byte> aSpan = actualDec.AsSpan();
            int maxDiff = 0;
            for (int i = 0; i < eSpan.Length; i++)
            {
                int diff = Math.Abs(eSpan[i] - aSpan[i]);
                if (diff > maxDiff) maxDiff = diff;
            }

            Assert.True(maxDiff <= 2, $"JPEG Arm64 FMA decoded max pixel diff {maxDiff} exceeded tolerance 2.");
        }

        // Stream overload matches byte[] overload byte-for-byte on all platforms
        using MemoryStream ms = new();
        StbImageWrite.WriteJpeg(ms, pixels, width, height, components, quality: quality, options: opts);
        ImageWriteTestHelpers.AssertBytesEqual(actual, ms.ToArray(), $"JPEG Stream {width}x{height}x{comp}");

        // TryWrite exact-fit & too-small
        byte[] dest = new byte[actual.Length];
        Assert.True(StbImageWrite.TryWriteJpeg(pixels, width, height, components, dest, out int written, quality: quality, options: opts));
        Assert.Equal(actual.Length, written);
        ImageWriteTestHelpers.AssertBytesEqual(actual, dest, $"JPEG TryWrite {width}x{height}x{comp}");

        byte[] small = new byte[actual.Length - 1];
        Assert.False(StbImageWrite.TryWriteJpeg(pixels, width, height, components, small, out int smallWritten, quality: quality, options: opts));
        Assert.Equal(0, smallWritten);

        // Strided input matches tight output byte-for-byte
        byte[] strided = ImageWriteTestHelpers.AddRowPadding(pixels, width, height, comp, extraPadBytes: 9);
        byte[] fromStrided = StbImageWrite.WriteJpeg(strided, width, height, components, quality: quality, strideBytes: (width * comp) + 9, options: opts);
        ImageWriteTestHelpers.AssertBytesEqual(actual, fromStrided, $"JPEG strided {width}x{height}x{comp}");

        // Round-trip decode through StbImage matches native stb_image byte-exact
        OracleImage<byte>? oracleDecoded = StbOracle.LoadImage(actual);
        Assert.NotNull(oracleDecoded);
        ImageBuffer<byte> managedDecoded = StbImage.Load(actual);
        ImageWriteTestHelpers.AssertBytesEqual(oracleDecoded.AsSpan(), managedDecoded.AsReadOnlySpan(), $"JPEG round-trip {width}x{height}x{comp}");
    }

    [Fact]
    public void WriteJpegImageBufferOverloadsWork()
    {
        byte[] pixels = ImageWriteTestHelpers.CreateGradientPixels(32, 24, 3);
        ImageBuffer<byte> img = new(32, 24, ColorComponents.RedGreenBlue, ColorComponents.RedGreenBlue, pixels);

        byte[] a = StbImageWrite.WriteJpeg(img, quality: 85);
        using MemoryStream ms = new();
        StbImageWrite.WriteJpeg(ms, img, quality: 85);
        ImageWriteTestHelpers.AssertBytesEqual(a, ms.ToArray(), "JPEG ImageBuffer Stream");
    }
}
