using System;
using System.Runtime.CompilerServices;
#if NET8_0_OR_GREATER
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.Arm;
using System.Runtime.Intrinsics.X86;
#endif

namespace Stb.Managed.ImageWrite.Internal;

/// <summary>
/// Safe span SIMD pixel-format conversion kernels shared by BMP and TGA encoders
/// (RGBA-&gt;BGRA, RGB-&gt;BGR, Grey-&gt;BGR, GreyAlpha-&gt;BGR). Zero unsafe code.
/// </summary>
internal static class PixelWriteKernels
{
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static void Rgba32ToBgra32(ReadOnlySpan<byte> src, Span<byte> dst, int pixelCount)
    {
        int len = pixelCount * 4;
        int i = 0;
#if NET8_0_OR_GREATER
        if (Avx2.IsSupported && len >= 32)
        {
            Vector256<byte> mask = Vector256.Create(
                (byte)2, 1, 0, 3, 6, 5, 4, 7, 10, 9, 8, 11, 14, 13, 12, 15,
                2, 1, 0, 3, 6, 5, 4, 7, 10, 9, 8, 11, 14, 13, 12, 15);
            int limit = len - 31;
            for (; i < limit; i += 32)
            {
                Vector256<byte> v = Vector256.Create(src.Slice(i, 32));
                Avx2.Shuffle(v, mask).CopyTo(dst.Slice(i, 32));
            }
        }

        if (Ssse3.IsSupported && len - i >= 16)
        {
            Vector128<byte> mask = Vector128.Create(
                (byte)2, 1, 0, 3, 6, 5, 4, 7, 10, 9, 8, 11, 14, 13, 12, 15);
            int limit = len - 15;
            for (; i < limit; i += 16)
            {
                Vector128<byte> v = Vector128.Create(src.Slice(i, 16));
                Ssse3.Shuffle(v, mask).CopyTo(dst.Slice(i, 16));
            }
        }
        else if (AdvSimd.Arm64.IsSupported && len - i >= 16)
        {
            Vector128<byte> mask = Vector128.Create(
                (byte)2, 1, 0, 3, 6, 5, 4, 7, 10, 9, 8, 11, 14, 13, 12, 15);
            int limit = len - 15;
            for (; i < limit; i += 16)
            {
                Vector128<byte> v = Vector128.Create(src.Slice(i, 16));
                AdvSimd.Arm64.VectorTableLookup(v, mask).CopyTo(dst.Slice(i, 16));
            }
        }
#endif
        for (; i < len; i += 4)
        {
            dst[i] = src[i + 2];
            dst[i + 1] = src[i + 1];
            dst[i + 2] = src[i];
            dst[i + 3] = src[i + 3];
        }
    }

    internal static void Rgba32ToBgra32Scalar(ReadOnlySpan<byte> src, Span<byte> dst, int pixelCount)
    {
        int len = pixelCount * 4;
        for (int i = 0; i < len; i += 4)
        {
            dst[i] = src[i + 2];
            dst[i + 1] = src[i + 1];
            dst[i + 2] = src[i];
            dst[i + 3] = src[i + 3];
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static void Rgb24ToBgr24(ReadOnlySpan<byte> src, Span<byte> dst, int pixelCount)
    {
        int len = pixelCount * 3;
        int i = 0;
#if NET8_0_OR_GREATER
        if (Ssse3.IsSupported && len >= 48)
        {
            Vector128<byte> m0V0 = Vector128.Create((byte)2, 1, 0, 5, 4, 3, 8, 7, 6, 11, 10, 9, 14, 13, 12, 0x80);
            Vector128<byte> m0V1 = Vector128.Create((byte)0x80, 0x80, 0x80, 0x80, 0x80, 0x80, 0x80, 0x80, 0x80, 0x80, 0x80, 0x80, 0x80, 0x80, 0x80, 1);
            Vector128<byte> m1V0 = Vector128.Create((byte)0x80, 15, 0x80, 0x80, 0x80, 0x80, 0x80, 0x80, 0x80, 0x80, 0x80, 0x80, 0x80, 0x80, 0x80, 0x80);
            Vector128<byte> m1V1 = Vector128.Create((byte)0, 0x80, 4, 3, 2, 7, 6, 5, 10, 9, 8, 13, 12, 11, 0x80, 15);
            Vector128<byte> m1V2 = Vector128.Create((byte)0x80, 0x80, 0x80, 0x80, 0x80, 0x80, 0x80, 0x80, 0x80, 0x80, 0x80, 0x80, 0x80, 0x80, 0, 0x80);
            Vector128<byte> m2V1 = Vector128.Create((byte)14, 0x80, 0x80, 0x80, 0x80, 0x80, 0x80, 0x80, 0x80, 0x80, 0x80, 0x80, 0x80, 0x80, 0x80, 0x80);
            Vector128<byte> m2V2 = Vector128.Create((byte)0x80, 3, 2, 1, 6, 5, 4, 9, 8, 7, 12, 11, 10, 15, 14, 13);

            int limit = len - 47;
            for (; i < limit; i += 48)
            {
                Vector128<byte> v0 = Vector128.Create(src.Slice(i, 16));
                Vector128<byte> v1 = Vector128.Create(src.Slice(i + 16, 16));
                Vector128<byte> v2 = Vector128.Create(src.Slice(i + 32, 16));

                Vector128<byte> o0 = Sse2.Or(Ssse3.Shuffle(v0, m0V0), Ssse3.Shuffle(v1, m0V1));
                Vector128<byte> o1 = Sse2.Or(Sse2.Or(Ssse3.Shuffle(v0, m1V0), Ssse3.Shuffle(v1, m1V1)), Ssse3.Shuffle(v2, m1V2));
                Vector128<byte> o2 = Sse2.Or(Ssse3.Shuffle(v1, m2V1), Ssse3.Shuffle(v2, m2V2));

                o0.CopyTo(dst.Slice(i, 16));
                o1.CopyTo(dst.Slice(i + 16, 16));
                o2.CopyTo(dst.Slice(i + 32, 16));
            }
        }
        else if (AdvSimd.Arm64.IsSupported && len >= 48)
        {
            Vector128<byte> m0V0 = Vector128.Create((byte)2, 1, 0, 5, 4, 3, 8, 7, 6, 11, 10, 9, 14, 13, 12, 0x80);
            Vector128<byte> m0V1 = Vector128.Create((byte)0x80, 0x80, 0x80, 0x80, 0x80, 0x80, 0x80, 0x80, 0x80, 0x80, 0x80, 0x80, 0x80, 0x80, 0x80, 1);
            Vector128<byte> m1V0 = Vector128.Create((byte)0x80, 15, 0x80, 0x80, 0x80, 0x80, 0x80, 0x80, 0x80, 0x80, 0x80, 0x80, 0x80, 0x80, 0x80, 0x80);
            Vector128<byte> m1V1 = Vector128.Create((byte)0, 0x80, 4, 3, 2, 7, 6, 5, 10, 9, 8, 13, 12, 11, 0x80, 15);
            Vector128<byte> m1V2 = Vector128.Create((byte)0x80, 0x80, 0x80, 0x80, 0x80, 0x80, 0x80, 0x80, 0x80, 0x80, 0x80, 0x80, 0x80, 0x80, 0, 0x80);
            Vector128<byte> m2V1 = Vector128.Create((byte)14, 0x80, 0x80, 0x80, 0x80, 0x80, 0x80, 0x80, 0x80, 0x80, 0x80, 0x80, 0x80, 0x80, 0x80, 0x80);
            Vector128<byte> m2V2 = Vector128.Create((byte)0x80, 3, 2, 1, 6, 5, 4, 9, 8, 7, 12, 11, 10, 15, 14, 13);

            int limit = len - 47;
            for (; i < limit; i += 48)
            {
                Vector128<byte> v0 = Vector128.Create(src.Slice(i, 16));
                Vector128<byte> v1 = Vector128.Create(src.Slice(i + 16, 16));
                Vector128<byte> v2 = Vector128.Create(src.Slice(i + 32, 16));

                Vector128<byte> o0 = AdvSimd.Or(
                    AdvSimd.Arm64.VectorTableLookup(v0, m0V0),
                    AdvSimd.Arm64.VectorTableLookup(v1, m0V1));
                Vector128<byte> o1 = AdvSimd.Or(
                    AdvSimd.Or(
                        AdvSimd.Arm64.VectorTableLookup(v0, m1V0),
                        AdvSimd.Arm64.VectorTableLookup(v1, m1V1)),
                    AdvSimd.Arm64.VectorTableLookup(v2, m1V2));
                Vector128<byte> o2 = AdvSimd.Or(
                    AdvSimd.Arm64.VectorTableLookup(v1, m2V1),
                    AdvSimd.Arm64.VectorTableLookup(v2, m2V2));

                o0.CopyTo(dst.Slice(i, 16));
                o1.CopyTo(dst.Slice(i + 16, 16));
                o2.CopyTo(dst.Slice(i + 32, 16));
            }
        }
#endif
        for (; i < len; i += 3)
        {
            dst[i] = src[i + 2];
            dst[i + 1] = src[i + 1];
            dst[i + 2] = src[i];
        }
    }

    internal static void Rgb24ToBgr24Scalar(ReadOnlySpan<byte> src, Span<byte> dst, int pixelCount)
    {
        int len = pixelCount * 3;
        for (int i = 0; i < len; i += 3)
        {
            dst[i] = src[i + 2];
            dst[i + 1] = src[i + 1];
            dst[i + 2] = src[i];
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static void Grey8ToBgr24(ReadOnlySpan<byte> src, Span<byte> dst, int pixelCount)
    {
        int x = 0;
#if NET8_0_OR_GREATER
        if (Ssse3.IsSupported && pixelCount >= 16)
        {
            Vector128<byte> g0 = Vector128.Create((byte)0, 0, 0, 1, 1, 1, 2, 2, 2, 3, 3, 3, 4, 4, 4, 5);
            Vector128<byte> g1 = Vector128.Create((byte)5, 5, 6, 6, 6, 7, 7, 7, 8, 8, 8, 9, 9, 9, 10, 10);
            Vector128<byte> g2 = Vector128.Create((byte)10, 11, 11, 11, 12, 12, 12, 13, 13, 13, 14, 14, 14, 15, 15, 15);
            int limit = pixelCount - 15;
            for (; x < limit; x += 16)
            {
                Vector128<byte> v = Vector128.Create(src.Slice(x, 16));
                int d = x * 3;
                Ssse3.Shuffle(v, g0).CopyTo(dst.Slice(d, 16));
                Ssse3.Shuffle(v, g1).CopyTo(dst.Slice(d + 16, 16));
                Ssse3.Shuffle(v, g2).CopyTo(dst.Slice(d + 32, 16));
            }
        }
        else if (AdvSimd.Arm64.IsSupported && pixelCount >= 16)
        {
            Vector128<byte> g0 = Vector128.Create((byte)0, 0, 0, 1, 1, 1, 2, 2, 2, 3, 3, 3, 4, 4, 4, 5);
            Vector128<byte> g1 = Vector128.Create((byte)5, 5, 6, 6, 6, 7, 7, 7, 8, 8, 8, 9, 9, 9, 10, 10);
            Vector128<byte> g2 = Vector128.Create((byte)10, 11, 11, 11, 12, 12, 12, 13, 13, 13, 14, 14, 14, 15, 15, 15);
            int limit = pixelCount - 15;
            for (; x < limit; x += 16)
            {
                Vector128<byte> v = Vector128.Create(src.Slice(x, 16));
                int d = x * 3;
                AdvSimd.Arm64.VectorTableLookup(v, g0).CopyTo(dst.Slice(d, 16));
                AdvSimd.Arm64.VectorTableLookup(v, g1).CopyTo(dst.Slice(d + 16, 16));
                AdvSimd.Arm64.VectorTableLookup(v, g2).CopyTo(dst.Slice(d + 32, 16));
            }
        }
#endif
        int dstOff = x * 3;
        for (; x < pixelCount; x++)
        {
            byte v = src[x];
            dst[dstOff] = v;
            dst[dstOff + 1] = v;
            dst[dstOff + 2] = v;
            dstOff += 3;
        }
    }

    internal static void Grey8ToBgr24Scalar(ReadOnlySpan<byte> src, Span<byte> dst, int pixelCount)
    {
        int d = 0;
        for (int x = 0; x < pixelCount; x++)
        {
            byte v = src[x];
            dst[d] = v;
            dst[d + 1] = v;
            dst[d + 2] = v;
            d += 3;
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static void GreyAlpha16ToBgr24(ReadOnlySpan<byte> src, Span<byte> dst, int pixelCount)
    {
        int x = 0;
#if NET8_0_OR_GREATER
        if (Ssse3.IsSupported && pixelCount >= 16)
        {
            Vector128<byte> m0V0 = Vector128.Create((byte)0, 0, 0, 2, 2, 2, 4, 4, 4, 6, 6, 6, 8, 8, 8, 10);
            Vector128<byte> m1V0 = Vector128.Create((byte)10, 10, 12, 12, 12, 14, 14, 14, 0x80, 0x80, 0x80, 0x80, 0x80, 0x80, 0x80, 0x80);
            Vector128<byte> m1V1 = Vector128.Create((byte)0x80, 0x80, 0x80, 0x80, 0x80, 0x80, 0x80, 0x80, 0, 0, 0, 2, 2, 2, 4, 4);
            Vector128<byte> m2V1 = Vector128.Create((byte)4, 6, 6, 6, 8, 8, 8, 10, 10, 10, 12, 12, 12, 14, 14, 14);
            int limit = pixelCount - 15;
            for (; x < limit; x += 16)
            {
                int s = x * 2;
                int d = x * 3;
                Vector128<byte> v0 = Vector128.Create(src.Slice(s, 16));
                Vector128<byte> v1 = Vector128.Create(src.Slice(s + 16, 16));
                Ssse3.Shuffle(v0, m0V0).CopyTo(dst.Slice(d, 16));
                Sse2.Or(Ssse3.Shuffle(v0, m1V0), Ssse3.Shuffle(v1, m1V1)).CopyTo(dst.Slice(d + 16, 16));
                Ssse3.Shuffle(v1, m2V1).CopyTo(dst.Slice(d + 32, 16));
            }
        }
        else if (AdvSimd.Arm64.IsSupported && pixelCount >= 16)
        {
            Vector128<byte> m0V0 = Vector128.Create((byte)0, 0, 0, 2, 2, 2, 4, 4, 4, 6, 6, 6, 8, 8, 8, 10);
            Vector128<byte> m1V0 = Vector128.Create((byte)10, 10, 12, 12, 12, 14, 14, 14, 0x80, 0x80, 0x80, 0x80, 0x80, 0x80, 0x80, 0x80);
            Vector128<byte> m1V1 = Vector128.Create((byte)0x80, 0x80, 0x80, 0x80, 0x80, 0x80, 0x80, 0x80, 0, 0, 0, 2, 2, 2, 4, 4);
            Vector128<byte> m2V1 = Vector128.Create((byte)4, 6, 6, 6, 8, 8, 8, 10, 10, 10, 12, 12, 12, 14, 14, 14);
            int limit = pixelCount - 15;
            for (; x < limit; x += 16)
            {
                int s = x * 2;
                int d = x * 3;
                Vector128<byte> v0 = Vector128.Create(src.Slice(s, 16));
                Vector128<byte> v1 = Vector128.Create(src.Slice(s + 16, 16));
                AdvSimd.Arm64.VectorTableLookup(v0, m0V0).CopyTo(dst.Slice(d, 16));
                AdvSimd.Or(
                    AdvSimd.Arm64.VectorTableLookup(v0, m1V0),
                    AdvSimd.Arm64.VectorTableLookup(v1, m1V1)).CopyTo(dst.Slice(d + 16, 16));
                AdvSimd.Arm64.VectorTableLookup(v1, m2V1).CopyTo(dst.Slice(d + 32, 16));
            }
        }
#endif
        int srcOff = x * 2;
        int dstOff = x * 3;
        for (; x < pixelCount; x++)
        {
            byte v = src[srcOff];
            dst[dstOff] = v;
            dst[dstOff + 1] = v;
            dst[dstOff + 2] = v;
            srcOff += 2;
            dstOff += 3;
        }
    }

    internal static void GreyAlpha16ToBgr24Scalar(ReadOnlySpan<byte> src, Span<byte> dst, int pixelCount)
    {
        int s = 0;
        int d = 0;
        for (int x = 0; x < pixelCount; x++)
        {
            byte v = src[s];
            dst[d] = v;
            dst[d + 1] = v;
            dst[d + 2] = v;
            s += 2;
            d += 3;
        }
    }
}
