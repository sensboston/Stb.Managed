using System;
using System.IO;
using Stb.Managed.Oracle;
using Xunit;

namespace Stb.Managed.Tests.ImageWrite;

public sealed class HdrWriterOracleTests
{
    [Theory]
    [InlineData(1, 1, ColorComponents.RedGreenBlue, false)]
    [InlineData(7, 4, ColorComponents.RedGreenBlue, false)] // width < 8 -> raw non-RLE scanlines
    [InlineData(7, 4, ColorComponents.Grey, true)]
    [InlineData(8, 5, ColorComponents.Grey, false)]         // width == 8 -> RLE scanlines
    [InlineData(8, 5, ColorComponents.GreyAlpha, true)]
    [InlineData(17, 9, ColorComponents.RedGreenBlue, false)]
    [InlineData(33, 12, ColorComponents.RedGreenBlueAlpha, true)]
    [InlineData(270, 4, ColorComponents.RedGreenBlue, false)] // > 128 literals & > 127 runs
    public void WriteHdrMatchesOracleByteExact(int width, int height, ColorComponents components, bool flip)
    {
        int comp = (int)components;
        float[] pixels = CreateHdrTestImage(width, height, comp);

        byte[] expected = StbOracle.WriteHdr(width, height, comp, pixels, flipVertically: flip);
        ImageWriteOptions opts = new() { FlipVertically = flip };

        byte[] actual = StbImageWrite.WriteHdr(pixels, width, height, components, options: opts);
        ImageWriteTestHelpers.AssertBytesEqual(expected, actual, $"HDR {width}x{height}x{comp} flip={flip}");

        // Stream overload
        using MemoryStream ms = new();
        StbImageWrite.WriteHdr(ms, pixels, width, height, components, options: opts);
        ImageWriteTestHelpers.AssertBytesEqual(expected, ms.ToArray(), $"HDR Stream {width}x{height}x{comp}");

        // TryWrite exact-fit & too-small
        byte[] dest = new byte[expected.Length];
        Assert.True(StbImageWrite.TryWriteHdr(pixels, width, height, components, dest, out int written, options: opts));
        Assert.Equal(expected.Length, written);
        ImageWriteTestHelpers.AssertBytesEqual(expected, dest, $"HDR TryWrite {width}x{height}x{comp}");

        byte[] small = new byte[expected.Length - 1];
        Assert.False(StbImageWrite.TryWriteHdr(pixels, width, height, components, small, out int smallWritten, options: opts));
        Assert.Equal(0, smallWritten);

        // Strided input matches tight output
        float[] strided = ImageWriteTestHelpers.AddFloatRowPadding(pixels, width, height, comp, extraPadFloats: 5);
        byte[] fromStrided = StbImageWrite.WriteHdr(strided, width, height, components, strideFloats: (width * comp) + 5, options: opts);
        ImageWriteTestHelpers.AssertBytesEqual(expected, fromStrided, $"HDR strided {width}x{height}x{comp}");

        // Round-trip decode with StbImage.LoadFloat matches native stb_image LoadImageFloat (0 ULP)
        OracleImage<float>? oracleDecoded = StbOracle.LoadImageFloat(actual, desiredChannels: 3);
        Assert.NotNull(oracleDecoded);
        ImageBuffer<float> managedDecoded = StbImage.LoadFloat(actual, ColorComponents.RedGreenBlue);
        Assert.Equal(oracleDecoded.AsSpan().ToArray(), managedDecoded.AsReadOnlySpan().ToArray());
    }

    [Fact]
    public void WriteHdrExtremeFloatValuesMatchOracleByteExact()
    {
        // Tests 0, -0, negative, sub-threshold (<= 1e-32f), just above threshold, huge, FLT_MAX, +Infinity.
        float[] pixels =
        [
            0f, 0f, 0f,
            -0f, -1f, -100f,
            1e-35f, 1e-33f, 1e-32f,
            1.0001e-32f, 5e-33f, 0f,
            1e-10f, 2e-10f, 3e-10f,
            0.25f, 0.5f, 1.0f,
            100.5f, 1000.25f, 0.01f,
            1e20f, 1e19f, 1e18f,
            float.MaxValue, float.MaxValue * 0.5f, 0f,
            float.PositiveInfinity, 10f, -5f,
        ];

        const int width = 10;
        const int height = 1;
        byte[] expected = StbOracle.WriteHdr(width, height, 3, pixels);
        byte[] actual = StbImageWrite.WriteHdr(pixels, width, height, ColorComponents.RedGreenBlue);
        ImageWriteTestHelpers.AssertBytesEqual(expected, actual, "HDR extreme float values");

        ImageBuffer<float> buf = new(width, height, ColorComponents.RedGreenBlue, ColorComponents.RedGreenBlue, pixels);
        ImageWriteTestHelpers.AssertBytesEqual(expected, StbImageWrite.WriteHdr(buf), "HDR ImageBuffer<float>");
    }

    private static float[] CreateHdrTestImage(int width, int height, int comp)
    {
        float[] pixels = new float[width * height * comp];
        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                int off = ((y * width) + x) * comp;
                float r;
                float g;
                float b;

                if (y == 0 && x >= 10 && x < 160)
                {
                    // Long constant run (> 127)
                    r = 1.5f;
                    g = 0.75f;
                    b = 0.25f;
                }
                else if (y == 1)
                {
                    // Strictly varying values (> 128 literals)
                    r = (x + 1) * 0.137f;
                    g = (x + 1) * 0.073f;
                    b = (x + 1) * 0.031f;
                }
                else
                {
                    r = ((x % 17) * 0.35f) + (y * 0.2f);
                    g = ((x % 13) * 0.25f) + 0.05f;
                    b = ((x + y) % 11) * 0.5f;
                }

                pixels[off] = r;
                if (comp >= 2) pixels[off + 1] = comp == 2 ? 0.5f : g;
                if (comp >= 3) pixels[off + 2] = b;
                if (comp >= 4) pixels[off + 3] = 1.0f;
            }
        }

        return pixels;
    }
}
