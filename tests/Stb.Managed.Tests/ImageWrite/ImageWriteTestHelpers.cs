using System;
using Xunit;

namespace Stb.Managed.Tests.ImageWrite;

internal static class ImageWriteTestHelpers
{
    internal static byte[] CreateGradientPixels(int width, int height, int comp, int seed = 1337)
    {
        byte[] pixels = new byte[width * height * comp];
        uint rng = (uint)seed;
        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                int off = ((y * width) + x) * comp;
                for (int c = 0; c < comp; c++)
                {
                    rng = (rng * 1664525u) + 1013904223u;
                    int val = ((x * 7) + (y * 11) + (c * 53) + (int)((rng >> 24) & 0x1Fu)) & 0xFF;
                    pixels[off + c] = (byte)val;
                }
            }
        }

        return pixels;
    }

    internal static byte[] CreateRandomPixels(int width, int height, int comp, int seed = 42)
    {
        byte[] pixels = new byte[width * height * comp];
        uint state = (uint)seed;
        for (int i = 0; i < pixels.Length; i++)
        {
            state ^= state << 13;
            state ^= state >> 17;
            state ^= state << 5;
            pixels[i] = (byte)state;
        }

        return pixels;
    }

    internal static byte[] AddRowPadding(byte[] tight, int width, int height, int comp, int extraPadBytes)
    {
        int rowBytes = width * comp;
        int stride = rowBytes + extraPadBytes;
        byte[] strided = new byte[stride * height];
        // Fill padding with distinct sentinel bytes to detect accidental reads beyond rowBytes.
        strided.AsSpan().Fill(0xA5);
        for (int y = 0; y < height; y++)
        {
            tight.AsSpan(y * rowBytes, rowBytes).CopyTo(strided.AsSpan(y * stride, rowBytes));
        }

        return strided;
    }

    internal static float[] AddFloatRowPadding(float[] tight, int width, int height, int comp, int extraPadFloats)
    {
        int rowFloats = width * comp;
        int stride = rowFloats + extraPadFloats;
        float[] strided = new float[stride * height];
        strided.AsSpan().Fill(9999.5f);
        for (int y = 0; y < height; y++)
        {
            tight.AsSpan(y * rowFloats, rowFloats).CopyTo(strided.AsSpan(y * stride, rowFloats));
        }

        return strided;
    }

    internal static void AssertBytesEqual(ReadOnlySpan<byte> expected, ReadOnlySpan<byte> actual, string context)
    {
        if (expected.SequenceEqual(actual))
        {
            return;
        }

        int minLen = Math.Min(expected.Length, actual.Length);
        int firstDiff = -1;
        for (int i = 0; i < minLen; i++)
        {
            if (expected[i] != actual[i])
            {
                firstDiff = i;
                break;
            }
        }

        if (firstDiff >= 0)
        {
            Assert.Fail(
                $"{context}: byte mismatch at offset {firstDiff} (expected 0x{expected[firstDiff]:X2}, actual 0x{actual[firstDiff]:X2}); " +
                $"lengths expected={expected.Length}, actual={actual.Length}.");
        }

        Assert.Fail($"{context}: length mismatch (expected {expected.Length}, actual {actual.Length}).");
    }
}
