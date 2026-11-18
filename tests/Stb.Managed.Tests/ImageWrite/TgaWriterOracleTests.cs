using System;
using System.IO;
using Stb.Managed.Oracle;
using Xunit;

namespace Stb.Managed.Tests.ImageWrite;

public sealed class TgaWriterOracleTests
{
    [Theory]
    [InlineData(1, 1, ColorComponents.Grey, false, false)]
    [InlineData(1, 1, ColorComponents.RedGreenBlueAlpha, true, false)]
    [InlineData(7, 5, ColorComponents.Grey, true, false)]
    [InlineData(7, 5, ColorComponents.Grey, false, true)]
    [InlineData(9, 11, ColorComponents.GreyAlpha, true, false)]
    [InlineData(9, 11, ColorComponents.GreyAlpha, false, true)]
    [InlineData(15, 13, ColorComponents.RedGreenBlue, true, false)]
    [InlineData(15, 13, ColorComponents.RedGreenBlue, false, true)]
    [InlineData(19, 17, ColorComponents.RedGreenBlueAlpha, true, false)]
    [InlineData(19, 17, ColorComponents.RedGreenBlueAlpha, false, true)]
    [InlineData(135, 9, ColorComponents.RedGreenBlueAlpha, true, false)]
    public void WriteTgaMatchesOracleByteExact(int width, int height, ColorComponents components, bool useRle, bool flip)
    {
        int comp = (int)components;
        byte[] pixels = ImageWriteTestHelpers.CreateGradientPixels(width, height, comp, seed: (width * 41) + (height * 17) + comp);

        byte[] expected = StbOracle.WriteTga(width, height, comp, pixels, withRle: useRle, flipVertically: flip);
        ImageWriteOptions opts = new() { TgaUseRle = useRle, FlipVertically = flip };

        byte[] actual = StbImageWrite.WriteTga(pixels, width, height, components, options: opts);
        ImageWriteTestHelpers.AssertBytesEqual(expected, actual, $"TGA {width}x{height}x{comp} rle={useRle} flip={flip}");

        // Stream overload
        using MemoryStream ms = new();
        StbImageWrite.WriteTga(ms, pixels, width, height, components, options: opts);
        ImageWriteTestHelpers.AssertBytesEqual(expected, ms.ToArray(), $"TGA Stream {width}x{height}x{comp}");

        // TryWrite exact-fit & too-small
        byte[] dest = new byte[expected.Length];
        Assert.True(StbImageWrite.TryWriteTga(pixels, width, height, components, dest, out int written, options: opts));
        Assert.Equal(expected.Length, written);
        ImageWriteTestHelpers.AssertBytesEqual(expected, dest, $"TGA TryWrite {width}x{height}x{comp}");

        byte[] small = new byte[expected.Length - 1];
        Assert.False(StbImageWrite.TryWriteTga(pixels, width, height, components, small, out int smallWritten, options: opts));
        Assert.Equal(0, smallWritten);

        // Strided input matches tight output
        byte[] strided = ImageWriteTestHelpers.AddRowPadding(pixels, width, height, comp, extraPadBytes: 11);
        byte[] fromStrided = StbImageWrite.WriteTga(strided, width, height, components, strideBytes: (width * comp) + 11, options: opts);
        ImageWriteTestHelpers.AssertBytesEqual(expected, fromStrided, $"TGA strided {width}x{height}x{comp}");

        // Round-trip decode with StbImage matches native stb_image
        OracleImage<byte>? oracleDecoded = StbOracle.LoadImage(actual);
        Assert.NotNull(oracleDecoded);
        ImageBuffer<byte> managedDecoded = StbImage.Load(actual);
        ImageWriteTestHelpers.AssertBytesEqual(oracleDecoded.AsSpan(), managedDecoded.AsReadOnlySpan(), $"TGA round-trip {width}x{height}x{comp}");
    }

    [Theory]
    [InlineData(ColorComponents.Grey)]
    [InlineData(ColorComponents.GreyAlpha)]
    [InlineData(ColorComponents.RedGreenBlue)]
    [InlineData(ColorComponents.RedGreenBlueAlpha)]
    public void WriteTgaRleEdgePatternsMatchOracleByteExact(ColorComponents components)
    {
        // Construct a single wide image containing every tricky RLE boundary on separate rows:
        // row 0: all identical (> 128 pixels -> splits at 128 + remainder)
        // row 1: strictly alternating A B A B ... (exercises stb's prev=begin, k=i+2 raw-run check!)
        // row 2: strictly unique pixels (> 128 -> raw run capped at 128)
        // row 3: mixed runs of 1, 2, 3, 127, 128, 129
        // row 4: pairs A A B B C C ...
        // row 5: high-entropy random noise
        const int width = 300;
        const int height = 6;
        int comp = (int)components;
        byte[] pixels = new byte[width * height * comp];

        void SetPixel(int y, int x, byte v0, byte v1, byte v2, byte v3)
        {
            int off = ((y * width) + x) * comp;
            pixels[off] = v0;
            if (comp >= 2) pixels[off + 1] = v1;
            if (comp >= 3) pixels[off + 2] = v2;
            if (comp >= 4) pixels[off + 3] = v3;
        }

        for (int x = 0; x < width; x++)
        {
            SetPixel(0, x, 42, 84, 126, 200);

            if ((x & 1) == 0) SetPixel(1, x, 10, 20, 30, 40);
            else SetPixel(1, x, 99, 88, 77, 66);

            SetPixel(2, x, (byte)x, (byte)(x >> 1), (byte)(x * 3), (byte)(255 - x));

            byte r3 = x < 1 ? (byte)1 : x < 3 ? (byte)2 : x < 6 ? (byte)3 : x < 133 ? (byte)4 : x < 261 ? (byte)5 : (byte)6;
            SetPixel(3, x, r3, (byte)(r3 * 10), (byte)(r3 * 20), 255);

            byte pair = (byte)(x / 2);
            SetPixel(4, x, pair, (byte)(pair + 1), (byte)(pair + 2), 128);
        }

        byte[] noise = ImageWriteTestHelpers.CreateRandomPixels(width, 1, comp, seed: 777);
        noise.CopyTo(pixels.AsSpan(5 * width * comp));

        byte[] expected = StbOracle.WriteTga(width, height, comp, pixels, withRle: true, flipVertically: false);
        byte[] actual = StbImageWrite.WriteTga(pixels, width, height, components, options: new ImageWriteOptions { TgaUseRle = true });
        ImageWriteTestHelpers.AssertBytesEqual(expected, actual, $"TGA RLE edge patterns comp={comp}");
    }
}
