using System;
using System.Runtime.InteropServices;
using Stb.Managed.Internal;
using Xunit;

namespace Stb.Managed.Tests.ImageResize;

internal static class ResizeTestHelpers
{
    internal static int GetChannelCount(ResizePixelLayout layout) =>
        ResizeGeometry.GetChannelCount(layout);

    internal static int GetTypeSize(ResizeDataType dataType) =>
        ResizeGeometry.GetDataTypeSize(dataType);

    internal static byte[] CreateImageBytes(
        int width,
        int height,
        ResizePixelLayout layout,
        ResizeDataType dataType,
        int seed = 1337,
        bool includeZeroAlpha = false)
    {
        int channels = GetChannelCount(layout);
        int typeSize = GetTypeSize(dataType);
        byte[] bytes = new byte[width * height * channels * typeSize];
        uint state = (uint)seed;

        int alphaIndex = GetAlphaChannelIndex(layout);

        switch (dataType)
        {
            case ResizeDataType.UInt8Linear:
            case ResizeDataType.UInt8Srgb:
            case ResizeDataType.UInt8SrgbAlpha:
                for (int y = 0; y < height; y++)
                {
                    for (int x = 0; x < width; x++)
                    {
                        int o = ((y * width) + x) * channels;
                        for (int c = 0; c < channels; c++)
                        {
                            state = NextRng(state);
                            byte v = (byte)(((x * 13) + (y * 29) + (c * 67) + (int)((state >> 24) & 0x3Fu)) & 0xFF);
                            if (includeZeroAlpha && c == alphaIndex && ((x + y) % 5 == 0 || (x < width / 3 && y < height / 3)))
                            {
                                v = 0;
                            }

                            bytes[o + c] = v;
                        }
                    }
                }

                break;

            case ResizeDataType.UInt16Linear:
            {
                Span<ushort> dst = MemoryMarshal.Cast<byte, ushort>(bytes.AsSpan());
                for (int y = 0; y < height; y++)
                {
                    for (int x = 0; x < width; x++)
                    {
                        int o = ((y * width) + x) * channels;
                        for (int c = 0; c < channels; c++)
                        {
                            state = NextRng(state);
                            ushort v = (ushort)(((x * 1031) + (y * 2053) + (c * 13107) + (int)((state >> 16) & 0x1FFFu)) & 0xFFFF);
                            if (includeZeroAlpha && c == alphaIndex && ((x + y) % 5 == 0))
                            {
                                v = 0;
                            }

                            dst[o + c] = v;
                        }
                    }
                }

                break;
            }

            case ResizeDataType.Fp32:
            {
                Span<float> dst = MemoryMarshal.Cast<byte, float>(bytes.AsSpan());
                for (int y = 0; y < height; y++)
                {
                    for (int x = 0; x < width; x++)
                    {
                        int o = ((y * width) + x) * channels;
                        for (int c = 0; c < channels; c++)
                        {
                            state = NextRng(state);
                            float v = ((state >> 8) & 0xFFFFu) / 65535.0f;
                            if (c != alphaIndex)
                            {
                                // Exercise values slightly outside [0, 1] for HDR float paths.
                                v = (v * 1.6f) - 0.2f;
                            }
                            else if (includeZeroAlpha && ((x + y) % 5 == 0))
                            {
                                v = 0.0f;
                            }

                            dst[o + c] = v;
                        }
                    }
                }

                break;
            }

            case ResizeDataType.Fp16:
            {
                Span<ushort> dst = MemoryMarshal.Cast<byte, ushort>(bytes.AsSpan());
                for (int y = 0; y < height; y++)
                {
                    for (int x = 0; x < width; x++)
                    {
                        int o = ((y * width) + x) * channels;
                        for (int c = 0; c < channels; c++)
                        {
                            state = NextRng(state);
                            float v = ((state >> 8) & 0xFFFFu) / 65535.0f;
                            if (includeZeroAlpha && c == alphaIndex && ((x + y) % 5 == 0))
                            {
                                v = 0.0f;
                            }

                            dst[o + c] = ResizeCoders.FloatToHalf(v);
                        }
                    }
                }

                break;
            }
        }

        return bytes;
    }

    internal static void AssertMatchesOracle(
        ReadOnlySpan<byte> actual,
        ReadOnlySpan<byte> expected,
        ResizeDataType outputDataType,
        bool exact = false,
        string? context = null)
    {
        Assert.Equal(expected.Length, actual.Length);

        if (exact)
        {
            for (int i = 0; i < expected.Length; i++)
            {
                if (actual[i] != expected[i])
                {
                    Assert.Fail($"Exact byte mismatch at offset {i}: expected {expected[i]}, actual {actual[i]}. {context}");
                }
            }

            return;
        }

        switch (outputDataType)
        {
            case ResizeDataType.UInt8Linear:
            case ResizeDataType.UInt8Srgb:
            case ResizeDataType.UInt8SrgbAlpha:
            {
                int maxDiff = 0;
                int worstIndex = -1;
                for (int i = 0; i < expected.Length; i++)
                {
                    int diff = Math.Abs(actual[i] - expected[i]);
                    if (diff > maxDiff)
                    {
                        maxDiff = diff;
                        worstIndex = i;
                    }
                }

                if (maxDiff > 1)
                {
                    Assert.Fail($"8-bit mismatch at byte {worstIndex}: expected {expected[worstIndex]}, actual {actual[worstIndex]} (maxDiff={maxDiff}). {context}");
                }

                break;
            }

            case ResizeDataType.UInt16Linear:
            {
                ReadOnlySpan<ushort> a16 = MemoryMarshal.Cast<byte, ushort>(actual);
                ReadOnlySpan<ushort> e16 = MemoryMarshal.Cast<byte, ushort>(expected);
                int maxDiff = 0;
                int worstIndex = -1;
                for (int i = 0; i < e16.Length; i++)
                {
                    int diff = Math.Abs(a16[i] - e16[i]);
                    if (diff > maxDiff)
                    {
                        maxDiff = diff;
                        worstIndex = i;
                    }
                }

                if (maxDiff > 1)
                {
                    Assert.Fail($"16-bit mismatch at sample {worstIndex}: expected {e16[worstIndex]}, actual {a16[worstIndex]} (maxDiff={maxDiff}). {context}");
                }

                break;
            }

            case ResizeDataType.Fp32:
            {
                ReadOnlySpan<float> af = MemoryMarshal.Cast<byte, float>(actual);
                ReadOnlySpan<float> ef = MemoryMarshal.Cast<byte, float>(expected);
                for (int i = 0; i < ef.Length; i++)
                {
                    float diff = Math.Abs(af[i] - ef[i]);
                    // Ringing filters over near-zero alpha can divide by tiny alpha (~1e-3), amplifying
                    // 1-ULP SIMD vs scalar rounding differences proportionally to output magnitude.
                    float tol = 1e-5f + (5e-5f * Math.Abs(ef[i]));
                    if (float.IsNaN(diff) || diff > tol)
                    {
                        Assert.Fail($"Float32 mismatch at sample {i}: expected {ef[i]}, actual {af[i]} (diff={diff}, tol={tol}). {context}");
                    }
                }

                break;
            }

            case ResizeDataType.Fp16:
            {
                ReadOnlySpan<ushort> ah = MemoryMarshal.Cast<byte, ushort>(actual);
                ReadOnlySpan<ushort> eh = MemoryMarshal.Cast<byte, ushort>(expected);
                float maxDiff = 0.0f;
                int worstIndex = -1;
                for (int i = 0; i < eh.Length; i++)
                {
                    float fa = ResizeCoders.HalfToFloat(ah[i]);
                    float fe = ResizeCoders.HalfToFloat(eh[i]);
                    float diff = Math.Abs(fa - fe);
                    if (float.IsNaN(diff) || diff > maxDiff)
                    {
                        maxDiff = diff;
                        worstIndex = i;
                    }
                }

                if (float.IsNaN(maxDiff) || maxDiff > 1e-3f)
                {
                    Assert.Fail($"Float16 mismatch at sample {worstIndex}: expected 0x{eh[worstIndex]:X4} ({ResizeCoders.HalfToFloat(eh[worstIndex])}), actual 0x{ah[worstIndex]:X4} ({ResizeCoders.HalfToFloat(ah[worstIndex])}). {context}");
                }

                break;
            }
        }
    }

    private static int GetAlphaChannelIndex(ResizePixelLayout layout) => layout switch
    {
        ResizePixelLayout.Rgba or ResizePixelLayout.Bgra or ResizePixelLayout.RgbaPremultiplied or ResizePixelLayout.BgraPremultiplied => 3,
        ResizePixelLayout.Argb or ResizePixelLayout.Abgr or ResizePixelLayout.ArgbPremultiplied or ResizePixelLayout.AbgrPremultiplied => 0,
        ResizePixelLayout.Ra or ResizePixelLayout.RaPremultiplied => 1,
        ResizePixelLayout.Ar or ResizePixelLayout.ArPremultiplied => 0,
        _ => -1,
    };

    private static uint NextRng(uint s) => (s * 1664525u) + 1013904223u;
}
