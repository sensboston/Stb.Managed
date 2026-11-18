using System.Runtime.CompilerServices;
#if NET8_0_OR_GREATER
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.Arm;
using System.Runtime.Intrinsics.X86;
#endif
using Stb.Managed.Internal;

namespace Stb.Managed.Image.Internal;

/// <summary>
/// Channel count conversion, 8/16-bit depth conversion, BGR/RGB swizzling, planar interleaving,
/// HDR/LDR conversion, and flips matching <c>stb_image.h</c> exactly.
/// </summary>
internal static class PixelConverter
{
    private static readonly float[] DefaultLdrToHdrLut = BuildLdrToHdrLut(2.2f, 1.0f);
    private static readonly float[] AlphaByteToFloatLut = BuildAlphaByteToFloatLut();

    private static float[] BuildLdrToHdrLut(float gamma, float scale)
    {
        float[] lut = new float[256];
        for (int i = 0; i < 256; i++)
        {
            lut[i] = (float)(Math.Pow(i / 255.0f, gamma) * scale);
        }

        return lut;
    }

    private static float[] BuildAlphaByteToFloatLut()
    {
        float[] lut = new float[256];
        for (int i = 0; i < 256; i++)
        {
            lut[i] = i / 255.0f;
        }

        return lut;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static byte ComputeY8(int r, int g, int b) =>
        (byte)(((r * 77) + (g * 150) + (29 * b)) >> 8);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static ushort ComputeY16(int r, int g, int b) =>
        (ushort)(((r * 77) + (g * 150) + (29 * b)) >> 8);

    internal static void ConvertFormat8(
        ReadOnlySpan<byte> src,
        Span<byte> dst,
        int srcChannels,
        int dstChannels,
        int pixelCount)
    {
        if (srcChannels == dstChannels)
        {
            src.Slice(0, pixelCount * srcChannels).CopyTo(dst);
            return;
        }

        int s = 0;
        int d = 0;
        switch ((srcChannels << 3) | dstChannels)
        {
            case (1 << 3) | 2:
                for (int i = 0; i < pixelCount; i++, s += 1, d += 2)
                {
                    dst[d] = src[s];
                    dst[d + 1] = 255;
                }
                break;

            case (1 << 3) | 3:
                for (int i = 0; i < pixelCount; i++, s += 1, d += 3)
                {
                    byte y = src[s];
                    dst[d] = y;
                    dst[d + 1] = y;
                    dst[d + 2] = y;
                }
                break;

            case (1 << 3) | 4:
                Grey8ToRgba32(src, dst, pixelCount);
                break;

            case (2 << 3) | 1:
                for (int i = 0; i < pixelCount; i++, s += 2, d += 1)
                {
                    dst[d] = src[s];
                }
                break;

            case (2 << 3) | 3:
                for (int i = 0; i < pixelCount; i++, s += 2, d += 3)
                {
                    byte y = src[s];
                    dst[d] = y;
                    dst[d + 1] = y;
                    dst[d + 2] = y;
                }
                break;

            case (2 << 3) | 4:
                for (int i = 0; i < pixelCount; i++, s += 2, d += 4)
                {
                    byte y = src[s];
                    dst[d] = y;
                    dst[d + 1] = y;
                    dst[d + 2] = y;
                    dst[d + 3] = src[s + 1];
                }
                break;

            case (3 << 3) | 1:
                for (int i = 0; i < pixelCount; i++, s += 3, d += 1)
                {
                    dst[d] = ComputeY8(src[s], src[s + 1], src[s + 2]);
                }
                break;

            case (3 << 3) | 2:
                for (int i = 0; i < pixelCount; i++, s += 3, d += 2)
                {
                    dst[d] = ComputeY8(src[s], src[s + 1], src[s + 2]);
                    dst[d + 1] = 255;
                }
                break;

            case (3 << 3) | 4:
                Rgb24ToRgba32(src, dst, pixelCount);
                break;

            case (4 << 3) | 1:
                for (int i = 0; i < pixelCount; i++, s += 4, d += 1)
                {
                    dst[d] = ComputeY8(src[s], src[s + 1], src[s + 2]);
                }
                break;

            case (4 << 3) | 2:
                for (int i = 0; i < pixelCount; i++, s += 4, d += 2)
                {
                    dst[d] = ComputeY8(src[s], src[s + 1], src[s + 2]);
                    dst[d + 1] = src[s + 3];
                }
                break;

            case (4 << 3) | 3:
                for (int i = 0; i < pixelCount; i++, s += 4, d += 3)
                {
                    dst[d] = src[s];
                    dst[d + 1] = src[s + 1];
                    dst[d + 2] = src[s + 2];
                }
                break;

            default:
                throw new StbException(
                    StbErrorCode.UnsupportedFeature,
                    $"Unsupported format conversion from {srcChannels} to {dstChannels} channels.");
        }
    }

    internal static void Grey8ToRgba32(ReadOnlySpan<byte> src, Span<byte> dst, int pixelCount)
    {
        int i = 0;
#if NET8_0_OR_GREATER
        if (Ssse3.IsSupported && pixelCount >= 16)
        {
            Vector128<byte> m0 = Vector128.Create((byte)0, 0, 0, 0x80, 1, 1, 1, 0x80, 2, 2, 2, 0x80, 3, 3, 3, 0x80);
            Vector128<byte> m1 = Vector128.Create((byte)4, 4, 4, 0x80, 5, 5, 5, 0x80, 6, 6, 6, 0x80, 7, 7, 7, 0x80);
            Vector128<byte> m2 = Vector128.Create((byte)8, 8, 8, 0x80, 9, 9, 9, 0x80, 10, 10, 10, 0x80, 11, 11, 11, 0x80);
            Vector128<byte> m3 = Vector128.Create((byte)12, 12, 12, 0x80, 13, 13, 13, 0x80, 14, 14, 14, 0x80, 15, 15, 15, 0x80);
            Vector128<byte> alpha = Vector128.Create(0xFF000000u).AsByte();
            int limit = pixelCount - 15;
            for (; i < limit; i += 16)
            {
                Vector128<byte> v = Vector128.Create(src.Slice(i, 16));
                int d = i * 4;
                Sse2.Or(Ssse3.Shuffle(v, m0), alpha).CopyTo(dst.Slice(d, 16));
                Sse2.Or(Ssse3.Shuffle(v, m1), alpha).CopyTo(dst.Slice(d + 16, 16));
                Sse2.Or(Ssse3.Shuffle(v, m2), alpha).CopyTo(dst.Slice(d + 32, 16));
                Sse2.Or(Ssse3.Shuffle(v, m3), alpha).CopyTo(dst.Slice(d + 48, 16));
            }
        }
        else if (AdvSimd.Arm64.IsSupported && pixelCount >= 16)
        {
            Vector128<byte> m0 = Vector128.Create((byte)0, 0, 0, 16, 1, 1, 1, 16, 2, 2, 2, 16, 3, 3, 3, 16);
            Vector128<byte> m1 = Vector128.Create((byte)4, 4, 4, 16, 5, 5, 5, 16, 6, 6, 6, 16, 7, 7, 7, 16);
            Vector128<byte> m2 = Vector128.Create((byte)8, 8, 8, 16, 9, 9, 9, 16, 10, 10, 10, 16, 11, 11, 11, 16);
            Vector128<byte> m3 = Vector128.Create((byte)12, 12, 12, 16, 13, 13, 13, 16, 14, 14, 14, 16, 15, 15, 15, 16);
            Vector128<byte> alpha = Vector128.Create(0xFF000000u).AsByte();
            int limit = pixelCount - 15;
            for (; i < limit; i += 16)
            {
                Vector128<byte> v = Vector128.Create(src.Slice(i, 16));
                int d = i * 4;
                AdvSimd.Or(AdvSimd.Arm64.VectorTableLookup(v, m0), alpha).CopyTo(dst.Slice(d, 16));
                AdvSimd.Or(AdvSimd.Arm64.VectorTableLookup(v, m1), alpha).CopyTo(dst.Slice(d + 16, 16));
                AdvSimd.Or(AdvSimd.Arm64.VectorTableLookup(v, m2), alpha).CopyTo(dst.Slice(d + 32, 16));
                AdvSimd.Or(AdvSimd.Arm64.VectorTableLookup(v, m3), alpha).CopyTo(dst.Slice(d + 48, 16));
            }
        }
#endif
        for (; i < pixelCount; i++)
        {
            byte y = src[i];
            int d = i * 4;
            dst[d] = y;
            dst[d + 1] = y;
            dst[d + 2] = y;
            dst[d + 3] = 255;
        }
    }

    internal static void Rgb24ToRgba32(ReadOnlySpan<byte> src, Span<byte> dst, int pixelCount)
    {
        int i = 0;
#if NET8_0_OR_GREATER
        if (Ssse3.IsSupported && pixelCount >= 6)
        {
            Vector128<byte> mask = Vector128.Create(
                (byte)0, 1, 2, 0x80, 3, 4, 5, 0x80, 6, 7, 8, 0x80, 9, 10, 11, 0x80);
            Vector128<byte> alpha = Vector128.Create(0xFF000000u).AsByte();
            int srcLimit = (pixelCount * 3) - 16;
            int s = 0;
            int d = 0;
            for (; s <= srcLimit; s += 12, d += 16, i += 4)
            {
                Vector128<byte> v = Vector128.Create(src.Slice(s, 16));
                Sse2.Or(Ssse3.Shuffle(v, mask), alpha).CopyTo(dst.Slice(d, 16));
            }
        }
        else if (AdvSimd.Arm64.IsSupported && pixelCount >= 6)
        {
            Vector128<byte> mask = Vector128.Create(
                (byte)0, 1, 2, 16, 3, 4, 5, 16, 6, 7, 8, 16, 9, 10, 11, 16);
            Vector128<byte> alpha = Vector128.Create(0xFF000000u).AsByte();
            int srcLimit = (pixelCount * 3) - 16;
            int s = 0;
            int d = 0;
            for (; s <= srcLimit; s += 12, d += 16, i += 4)
            {
                Vector128<byte> v = Vector128.Create(src.Slice(s, 16));
                AdvSimd.Or(AdvSimd.Arm64.VectorTableLookup(v, mask), alpha).CopyTo(dst.Slice(d, 16));
            }
        }
#endif
        int st = i * 3;
        int dt = i * 4;
        for (; i < pixelCount; i++, st += 3, dt += 4)
        {
            dst[dt] = src[st];
            dst[dt + 1] = src[st + 1];
            dst[dt + 2] = src[st + 2];
            dst[dt + 3] = 255;
        }
    }

    internal static void Bgr24ToRgb24(ReadOnlySpan<byte> src, Span<byte> dst, int pixelCount)
    {
        int i = 0;
#if NET8_0_OR_GREATER
        if (Ssse3.IsSupported && pixelCount >= 6)
        {
            Vector128<byte> mask = Vector128.Create(
                (byte)2, 1, 0, 5, 4, 3, 8, 7, 6, 11, 10, 9, 12, 13, 14, 15);
            int limit = (pixelCount * 3) - 16;
            int off = 0;
            for (; off <= limit; off += 12, i += 4)
            {
                Vector128<byte> v = Vector128.Create(src.Slice(off, 16));
                Ssse3.Shuffle(v, mask).CopyTo(dst.Slice(off, 16));
            }
        }
        else if (AdvSimd.Arm64.IsSupported && pixelCount >= 6)
        {
            Vector128<byte> mask = Vector128.Create(
                (byte)2, 1, 0, 5, 4, 3, 8, 7, 6, 11, 10, 9, 12, 13, 14, 15);
            int limit = (pixelCount * 3) - 16;
            int off = 0;
            for (; off <= limit; off += 12, i += 4)
            {
                Vector128<byte> v = Vector128.Create(src.Slice(off, 16));
                AdvSimd.Arm64.VectorTableLookup(v, mask).CopyTo(dst.Slice(off, 16));
            }
        }
#endif
        int t = i * 3;
        for (; i < pixelCount; i++, t += 3)
        {
            byte b = src[t];
            byte g = src[t + 1];
            byte r = src[t + 2];
            dst[t] = r;
            dst[t + 1] = g;
            dst[t + 2] = b;
        }
    }

    internal static void Bgr24ToRgba32(ReadOnlySpan<byte> src, Span<byte> dst, int pixelCount)
    {
        int i = 0;
#if NET8_0_OR_GREATER
        if (Ssse3.IsSupported && pixelCount >= 6)
        {
            Vector128<byte> mask = Vector128.Create(
                (byte)2, 1, 0, 0x80, 5, 4, 3, 0x80, 8, 7, 6, 0x80, 11, 10, 9, 0x80);
            Vector128<byte> alpha = Vector128.Create(0xFF000000u).AsByte();
            int srcLimit = (pixelCount * 3) - 16;
            int s = 0;
            int d = 0;
            for (; s <= srcLimit; s += 12, d += 16, i += 4)
            {
                Vector128<byte> v = Vector128.Create(src.Slice(s, 16));
                Sse2.Or(Ssse3.Shuffle(v, mask), alpha).CopyTo(dst.Slice(d, 16));
            }
        }
        else if (AdvSimd.Arm64.IsSupported && pixelCount >= 6)
        {
            Vector128<byte> mask = Vector128.Create(
                (byte)2, 1, 0, 16, 5, 4, 3, 16, 8, 7, 6, 16, 11, 10, 9, 16);
            Vector128<byte> alpha = Vector128.Create(0xFF000000u).AsByte();
            int srcLimit = (pixelCount * 3) - 16;
            int s = 0;
            int d = 0;
            for (; s <= srcLimit; s += 12, d += 16, i += 4)
            {
                Vector128<byte> v = Vector128.Create(src.Slice(s, 16));
                AdvSimd.Or(AdvSimd.Arm64.VectorTableLookup(v, mask), alpha).CopyTo(dst.Slice(d, 16));
            }
        }
#endif
        int st = i * 3;
        int dt = i * 4;
        for (; i < pixelCount; i++, st += 3, dt += 4)
        {
            dst[dt] = src[st + 2];
            dst[dt + 1] = src[st + 1];
            dst[dt + 2] = src[st];
            dst[dt + 3] = 255;
        }
    }

    internal static void Bgra32ToRgba32(
        ReadOnlySpan<byte> src,
        Span<byte> dst,
        int pixelCount,
        byte alphaOverride,
        out byte alphaOr)
    {
        int i = 0;
        byte aOr = alphaOverride;
#if NET8_0_OR_GREATER
        if (Avx2.IsSupported && pixelCount >= 8)
        {
            Vector256<byte> mask = Vector256.Create(
                (byte)2, 1, 0, 3, 6, 5, 4, 7, 10, 9, 8, 11, 14, 13, 12, 15,
                2, 1, 0, 3, 6, 5, 4, 7, 10, 9, 8, 11, 14, 13, 12, 15);
            int limit = pixelCount - 7;
            if (alphaOverride == 255)
            {
                Vector256<byte> alphaFF = Vector256.Create(0xFF000000u).AsByte();
                for (; i < limit; i += 8)
                {
                    int off = i * 4;
                    Vector256<byte> v = Vector256.Create(src.Slice(off, 32));
                    Avx2.Or(Avx2.Shuffle(v, mask), alphaFF).CopyTo(dst.Slice(off, 32));
                }
            }
            else
            {
                Vector256<byte> alphaOv = Vector256.Create((uint)alphaOverride << 24).AsByte();
                Vector256<byte> orAccum = Vector256<byte>.Zero;
                for (; i < limit; i += 8)
                {
                    int off = i * 4;
                    Vector256<byte> v = Vector256.Create(src.Slice(off, 32));
                    Vector256<byte> r = alphaOverride != 0
                        ? Avx2.Or(Avx2.Shuffle(v, mask), alphaOv)
                        : Avx2.Shuffle(v, mask);
                    orAccum = Avx2.Or(orAccum, r);
                    r.CopyTo(dst.Slice(off, 32));
                }

                Vector128<byte> lo = Sse2.Or(orAccum.GetLower(), orAccum.GetUpper());
                uint u = lo.AsUInt32().GetElement(0) | lo.AsUInt32().GetElement(1)
                    | lo.AsUInt32().GetElement(2) | lo.AsUInt32().GetElement(3);
                aOr |= (byte)(u >> 24);
            }
        }
        else if (Ssse3.IsSupported && pixelCount >= 4)
        {
            Vector128<byte> mask = Vector128.Create(
                (byte)2, 1, 0, 3, 6, 5, 4, 7, 10, 9, 8, 11, 14, 13, 12, 15);
            int limit = pixelCount - 3;
            if (alphaOverride == 255)
            {
                Vector128<byte> alphaFF = Vector128.Create(0xFF000000u).AsByte();
                for (; i < limit; i += 4)
                {
                    int off = i * 4;
                    Vector128<byte> v = Vector128.Create(src.Slice(off, 16));
                    Sse2.Or(Ssse3.Shuffle(v, mask), alphaFF).CopyTo(dst.Slice(off, 16));
                }
            }
            else
            {
                Vector128<byte> alphaOv = Vector128.Create((uint)alphaOverride << 24).AsByte();
                Vector128<byte> orAccum = Vector128<byte>.Zero;
                for (; i < limit; i += 4)
                {
                    int off = i * 4;
                    Vector128<byte> v = Vector128.Create(src.Slice(off, 16));
                    Vector128<byte> r = alphaOverride != 0
                        ? Sse2.Or(Ssse3.Shuffle(v, mask), alphaOv)
                        : Ssse3.Shuffle(v, mask);
                    orAccum = Sse2.Or(orAccum, r);
                    r.CopyTo(dst.Slice(off, 16));
                }

                uint u = orAccum.AsUInt32().GetElement(0) | orAccum.AsUInt32().GetElement(1)
                    | orAccum.AsUInt32().GetElement(2) | orAccum.AsUInt32().GetElement(3);
                aOr |= (byte)(u >> 24);
            }
        }
        else if (AdvSimd.Arm64.IsSupported && pixelCount >= 4)
        {
            Vector128<byte> mask = Vector128.Create(
                (byte)2, 1, 0, 3, 6, 5, 4, 7, 10, 9, 8, 11, 14, 13, 12, 15);
            int limit = pixelCount - 3;
            if (alphaOverride == 255)
            {
                Vector128<byte> alphaFF = Vector128.Create(0xFF000000u).AsByte();
                for (; i < limit; i += 4)
                {
                    int off = i * 4;
                    Vector128<byte> v = Vector128.Create(src.Slice(off, 16));
                    AdvSimd.Or(AdvSimd.Arm64.VectorTableLookup(v, mask), alphaFF).CopyTo(dst.Slice(off, 16));
                }
            }
            else
            {
                Vector128<byte> alphaOv = Vector128.Create((uint)alphaOverride << 24).AsByte();
                Vector128<byte> orAccum = Vector128<byte>.Zero;
                for (; i < limit; i += 4)
                {
                    int off = i * 4;
                    Vector128<byte> v = Vector128.Create(src.Slice(off, 16));
                    Vector128<byte> r = alphaOverride != 0
                        ? AdvSimd.Or(AdvSimd.Arm64.VectorTableLookup(v, mask), alphaOv)
                        : AdvSimd.Arm64.VectorTableLookup(v, mask);
                    orAccum = AdvSimd.Or(orAccum, r);
                    r.CopyTo(dst.Slice(off, 16));
                }

                uint u = orAccum.AsUInt32().GetElement(0) | orAccum.AsUInt32().GetElement(1)
                    | orAccum.AsUInt32().GetElement(2) | orAccum.AsUInt32().GetElement(3);
                aOr |= (byte)(u >> 24);
            }
        }
#endif
        int offTail = i * 4;
        for (; i < pixelCount; i++, offTail += 4)
        {
            byte b = src[offTail];
            byte g = src[offTail + 1];
            byte r = src[offTail + 2];
            byte a = (byte)(src[offTail + 3] | alphaOverride);
            aOr |= a;
            dst[offTail] = r;
            dst[offTail + 1] = g;
            dst[offTail + 2] = b;
            dst[offTail + 3] = a;
        }

        alphaOr = aOr;
    }

    internal static void Bgra32ToRgb24(ReadOnlySpan<byte> src, Span<byte> dst, int pixelCount)
    {
        int i = 0;
#if NET8_0_OR_GREATER
        if (Ssse3.IsSupported && pixelCount >= 6)
        {
            Vector128<byte> mask = Vector128.Create(
                (byte)2, 1, 0, 6, 5, 4, 10, 9, 8, 14, 13, 12, 0x80, 0x80, 0x80, 0x80);
            int dstLimit = (pixelCount * 3) - 16;
            int s = 0;
            int d = 0;
            for (; d <= dstLimit; s += 16, d += 12, i += 4)
            {
                Vector128<byte> v = Vector128.Create(src.Slice(s, 16));
                Ssse3.Shuffle(v, mask).CopyTo(dst.Slice(d, 16));
            }
        }
        else if (AdvSimd.Arm64.IsSupported && pixelCount >= 6)
        {
            Vector128<byte> mask = Vector128.Create(
                (byte)2, 1, 0, 6, 5, 4, 10, 9, 8, 14, 13, 12, 16, 16, 16, 16);
            int dstLimit = (pixelCount * 3) - 16;
            int s = 0;
            int d = 0;
            for (; d <= dstLimit; s += 16, d += 12, i += 4)
            {
                Vector128<byte> v = Vector128.Create(src.Slice(s, 16));
                AdvSimd.Arm64.VectorTableLookup(v, mask).CopyTo(dst.Slice(d, 16));
            }
        }
#endif
        int st = i * 4;
        int dt = i * 3;
        for (; i < pixelCount; i++, st += 4, dt += 3)
        {
            dst[dt] = src[st + 2];
            dst[dt + 1] = src[st + 1];
            dst[dt + 2] = src[st];
        }
    }

    internal static void Interleave4PlanesToRgba32(
        ReadOnlySpan<byte> rPlane,
        ReadOnlySpan<byte> gPlane,
        ReadOnlySpan<byte> bPlane,
        ReadOnlySpan<byte> aPlane,
        Span<byte> dstRgba,
        int pixelCount)
    {
        bool hasAlpha = aPlane.Length >= pixelCount;
        int i = 0;
#if NET8_0_OR_GREATER
        if (Sse2.IsSupported && pixelCount >= 16)
        {
            Vector128<byte> alpha255 = Vector128.Create((byte)255);
            int limit = pixelCount - 15;
            for (; i < limit; i += 16)
            {
                Vector128<byte> vr = Vector128.Create(rPlane.Slice(i, 16));
                Vector128<byte> vg = Vector128.Create(gPlane.Slice(i, 16));
                Vector128<byte> vb = Vector128.Create(bPlane.Slice(i, 16));
                Vector128<byte> va = hasAlpha ? Vector128.Create(aPlane.Slice(i, 16)) : alpha255;

                Vector128<ushort> rgLo = Sse2.UnpackLow(vr, vg).AsUInt16();
                Vector128<ushort> rgHi = Sse2.UnpackHigh(vr, vg).AsUInt16();
                Vector128<ushort> baLo = Sse2.UnpackLow(vb, va).AsUInt16();
                Vector128<ushort> baHi = Sse2.UnpackHigh(vb, va).AsUInt16();

                int d = i * 4;
                Sse2.UnpackLow(rgLo, baLo).AsByte().CopyTo(dstRgba.Slice(d, 16));
                Sse2.UnpackHigh(rgLo, baLo).AsByte().CopyTo(dstRgba.Slice(d + 16, 16));
                Sse2.UnpackLow(rgHi, baHi).AsByte().CopyTo(dstRgba.Slice(d + 32, 16));
                Sse2.UnpackHigh(rgHi, baHi).AsByte().CopyTo(dstRgba.Slice(d + 48, 16));
            }
        }
        else if (AdvSimd.Arm64.IsSupported && pixelCount >= 16)
        {
            Vector128<byte> alpha255 = Vector128.Create((byte)255);
            int limit = pixelCount - 15;
            for (; i < limit; i += 16)
            {
                Vector128<byte> vr = Vector128.Create(rPlane.Slice(i, 16));
                Vector128<byte> vg = Vector128.Create(gPlane.Slice(i, 16));
                Vector128<byte> vb = Vector128.Create(bPlane.Slice(i, 16));
                Vector128<byte> va = hasAlpha ? Vector128.Create(aPlane.Slice(i, 16)) : alpha255;

                Vector128<ushort> rgLo = AdvSimd.Arm64.ZipLow(vr, vg).AsUInt16();
                Vector128<ushort> rgHi = AdvSimd.Arm64.ZipHigh(vr, vg).AsUInt16();
                Vector128<ushort> baLo = AdvSimd.Arm64.ZipLow(vb, va).AsUInt16();
                Vector128<ushort> baHi = AdvSimd.Arm64.ZipHigh(vb, va).AsUInt16();

                int d = i * 4;
                AdvSimd.Arm64.ZipLow(rgLo, baLo).AsByte().CopyTo(dstRgba.Slice(d, 16));
                AdvSimd.Arm64.ZipHigh(rgLo, baLo).AsByte().CopyTo(dstRgba.Slice(d + 16, 16));
                AdvSimd.Arm64.ZipLow(rgHi, baHi).AsByte().CopyTo(dstRgba.Slice(d + 32, 16));
                AdvSimd.Arm64.ZipHigh(rgHi, baHi).AsByte().CopyTo(dstRgba.Slice(d + 48, 16));
            }
        }
#endif
        if (hasAlpha)
        {
            for (; i < pixelCount; i++)
            {
                int d = i * 4;
                dstRgba[d] = rPlane[i];
                dstRgba[d + 1] = gPlane[i];
                dstRgba[d + 2] = bPlane[i];
                dstRgba[d + 3] = aPlane[i];
            }
        }
        else
        {
            for (; i < pixelCount; i++)
            {
                int d = i * 4;
                dstRgba[d] = rPlane[i];
                dstRgba[d + 1] = gPlane[i];
                dstRgba[d + 2] = bPlane[i];
                dstRgba[d + 3] = 255;
            }
        }
    }

    internal static void ReverseEndianness16FromBytes(ReadOnlySpan<byte> srcBigEndian, Span<ushort> dst)
    {
        int count = dst.Length;
        int i = 0;
#if NET8_0_OR_GREATER
        if (BitConverter.IsLittleEndian)
        {
            Span<byte> dstBytes = MemoryMarshal.Cast<ushort, byte>(dst);
            if (Avx2.IsSupported && count >= 16)
            {
                Vector256<byte> mask = Vector256.Create(
                    (byte)1, 0, 3, 2, 5, 4, 7, 6, 9, 8, 11, 10, 13, 12, 15, 14,
                    1, 0, 3, 2, 5, 4, 7, 6, 9, 8, 11, 10, 13, 12, 15, 14);
                int limit = count - 15;
                for (; i < limit; i += 16)
                {
                    int off = i * 2;
                    Vector256<byte> v = Vector256.Create(srcBigEndian.Slice(off, 32));
                    Avx2.Shuffle(v, mask).CopyTo(dstBytes.Slice(off, 32));
                }
            }
            else if (Ssse3.IsSupported && count >= 8)
            {
                Vector128<byte> mask = Vector128.Create(
                    (byte)1, 0, 3, 2, 5, 4, 7, 6, 9, 8, 11, 10, 13, 12, 15, 14);
                int limit = count - 7;
                for (; i < limit; i += 8)
                {
                    int off = i * 2;
                    Vector128<byte> v = Vector128.Create(srcBigEndian.Slice(off, 16));
                    Ssse3.Shuffle(v, mask).CopyTo(dstBytes.Slice(off, 16));
                }
            }
            else if (AdvSimd.Arm64.IsSupported && count >= 8)
            {
                Vector128<byte> mask = Vector128.Create(
                    (byte)1, 0, 3, 2, 5, 4, 7, 6, 9, 8, 11, 10, 13, 12, 15, 14);
                int limit = count - 7;
                for (; i < limit; i += 8)
                {
                    int off = i * 2;
                    Vector128<byte> v = Vector128.Create(srcBigEndian.Slice(off, 16));
                    AdvSimd.Arm64.VectorTableLookup(v, mask).CopyTo(dstBytes.Slice(off, 16));
                }
            }
        }
#endif
        for (; i < count; i++)
        {
            int off = i * 2;
            dst[i] = (ushort)((srcBigEndian[off] << 8) | srcBigEndian[off + 1]);
        }
    }

    internal static void ConvertFormat16(
        ReadOnlySpan<ushort> src,
        Span<ushort> dst,
        int srcChannels,
        int dstChannels,
        int pixelCount)
    {
        if (srcChannels == dstChannels)
        {
            src.Slice(0, pixelCount * srcChannels).CopyTo(dst);
            return;
        }

        int s = 0;
        int d = 0;
        switch ((srcChannels << 3) | dstChannels)
        {
            case (1 << 3) | 2:
                for (int i = 0; i < pixelCount; i++, s += 1, d += 2)
                {
                    dst[d] = src[s];
                    dst[d + 1] = 0xFFFF;
                }
                break;

            case (1 << 3) | 3:
                for (int i = 0; i < pixelCount; i++, s += 1, d += 3)
                {
                    ushort y = src[s];
                    dst[d] = y;
                    dst[d + 1] = y;
                    dst[d + 2] = y;
                }
                break;

            case (1 << 3) | 4:
                for (int i = 0; i < pixelCount; i++, s += 1, d += 4)
                {
                    ushort y = src[s];
                    dst[d] = y;
                    dst[d + 1] = y;
                    dst[d + 2] = y;
                    dst[d + 3] = 0xFFFF;
                }
                break;

            case (2 << 3) | 1:
                for (int i = 0; i < pixelCount; i++, s += 2, d += 1)
                {
                    dst[d] = src[s];
                }
                break;

            case (2 << 3) | 3:
                for (int i = 0; i < pixelCount; i++, s += 2, d += 3)
                {
                    ushort y = src[s];
                    dst[d] = y;
                    dst[d + 1] = y;
                    dst[d + 2] = y;
                }
                break;

            case (2 << 3) | 4:
                for (int i = 0; i < pixelCount; i++, s += 2, d += 4)
                {
                    ushort y = src[s];
                    dst[d] = y;
                    dst[d + 1] = y;
                    dst[d + 2] = y;
                    dst[d + 3] = src[s + 1];
                }
                break;

            case (3 << 3) | 1:
                for (int i = 0; i < pixelCount; i++, s += 3, d += 1)
                {
                    dst[d] = ComputeY16(src[s], src[s + 1], src[s + 2]);
                }
                break;

            case (3 << 3) | 2:
                for (int i = 0; i < pixelCount; i++, s += 3, d += 2)
                {
                    dst[d] = ComputeY16(src[s], src[s + 1], src[s + 2]);
                    dst[d + 1] = 0xFFFF;
                }
                break;

            case (3 << 3) | 4:
                for (int i = 0; i < pixelCount; i++, s += 3, d += 4)
                {
                    dst[d] = src[s];
                    dst[d + 1] = src[s + 1];
                    dst[d + 2] = src[s + 2];
                    dst[d + 3] = 0xFFFF;
                }
                break;

            case (4 << 3) | 1:
                for (int i = 0; i < pixelCount; i++, s += 4, d += 1)
                {
                    dst[d] = ComputeY16(src[s], src[s + 1], src[s + 2]);
                }
                break;

            case (4 << 3) | 2:
                for (int i = 0; i < pixelCount; i++, s += 4, d += 2)
                {
                    dst[d] = ComputeY16(src[s], src[s + 1], src[s + 2]);
                    dst[d + 1] = src[s + 3];
                }
                break;

            case (4 << 3) | 3:
                for (int i = 0; i < pixelCount; i++, s += 4, d += 3)
                {
                    dst[d] = src[s];
                    dst[d + 1] = src[s + 1];
                    dst[d + 2] = src[s + 2];
                }
                break;

            default:
                throw new StbException(
                    StbErrorCode.UnsupportedFeature,
                    $"Unsupported 16-bit format conversion from {srcChannels} to {dstChannels} channels.");
        }
    }

    internal static void Convert16To8(ReadOnlySpan<ushort> src, Span<byte> dst)
    {
        int count = src.Length;
        int i = 0;
#if NET8_0_OR_GREATER
        if (Sse2.IsSupported && count >= 16)
        {
            int limit = count - 15;
            for (; i < limit; i += 16)
            {
                Vector128<short> hi0 = Sse2.ShiftRightLogical(Vector128.Create(src.Slice(i, 8)), 8).AsInt16();
                Vector128<short> hi1 = Sse2.ShiftRightLogical(Vector128.Create(src.Slice(i + 8, 8)), 8).AsInt16();
                Sse2.PackUnsignedSaturate(hi0, hi1).CopyTo(dst.Slice(i, 16));
            }
        }
        else if (AdvSimd.IsSupported && count >= 16)
        {
            int limit = count - 15;
            for (; i < limit; i += 16)
            {
                Vector64<byte> lo = AdvSimd.ShiftRightLogicalNarrowingLower(Vector128.Create(src.Slice(i, 8)), 8);
                Vector64<byte> hi = AdvSimd.ShiftRightLogicalNarrowingLower(Vector128.Create(src.Slice(i + 8, 8)), 8);
                Vector128.Create(lo, hi).CopyTo(dst.Slice(i, 16));
            }
        }
#endif
        for (; i < count; i++)
        {
            dst[i] = (byte)(src[i] >> 8);
        }
    }

    internal static void Convert8To16(ReadOnlySpan<byte> src, Span<ushort> dst)
    {
        int count = src.Length;
        int i = 0;
#if NET8_0_OR_GREATER
        if (Sse2.IsSupported && count >= 16)
        {
            int limit = count - 15;
            for (; i < limit; i += 16)
            {
                Vector128<byte> v = Vector128.Create(src.Slice(i, 16));
                Sse2.UnpackLow(v, v).AsUInt16().CopyTo(dst.Slice(i, 8));
                Sse2.UnpackHigh(v, v).AsUInt16().CopyTo(dst.Slice(i + 8, 8));
            }
        }
        else if (AdvSimd.Arm64.IsSupported && count >= 16)
        {
            int limit = count - 15;
            for (; i < limit; i += 16)
            {
                Vector128<byte> v = Vector128.Create(src.Slice(i, 16));
                AdvSimd.Arm64.ZipLow(v, v).AsUInt16().CopyTo(dst.Slice(i, 8));
                AdvSimd.Arm64.ZipHigh(v, v).AsUInt16().CopyTo(dst.Slice(i + 8, 8));
            }
        }
#endif
        for (; i < count; i++)
        {
            uint v = src[i];
            dst[i] = (ushort)((v << 8) | v);
        }
    }

    internal static void VerticalFlip<T>(Span<T> pixels, int height, int rowStride)
        where T : unmanaged
    {
        if (height <= 1 || rowStride <= 0)
        {
            return;
        }

        using PooledBuffer<T> temp = new(rowStride);
        Span<T> scratch = temp.Span;
        int half = height >> 1;
        for (int y = 0; y < half; y++)
        {
            Span<T> top = pixels.Slice(y * rowStride, rowStride);
            Span<T> bottom = pixels.Slice((height - 1 - y) * rowStride, rowStride);
            top.CopyTo(scratch);
            bottom.CopyTo(top);
            scratch.CopyTo(bottom);
        }
    }

    internal static void FlipHorizontallyInPlace(Span<byte> pixels, int width, int height, int channels)
    {
        if (width <= 1 || height <= 0 || channels <= 0)
        {
            return;
        }

        int rowStride = width * channels;
        int halfWidth = width >> 1;
        for (int y = 0; y < height; y++)
        {
            int rowBase = y * rowStride;
            for (int x = 0; x < halfWidth; x++)
            {
                int p = rowBase + (x * channels);
                int q = rowBase + ((width - 1 - x) * channels);
                for (int k = 0; k < channels; k++)
                {
                    (pixels[p + k], pixels[q + k]) = (pixels[q + k], pixels[p + k]);
                }
            }
        }
    }

    internal static void SwapRedBlueInPlace(Span<byte> pixels, int pixelCount, int channels)
    {
        if (channels == 4)
        {
            Bgra32ToRgba32(pixels, pixels, pixelCount, alphaOverride: 0, out _);
        }
        else if (channels == 3)
        {
            Bgr24ToRgb24(pixels, pixels, pixelCount);
        }
    }

    internal static void Ldr8ToHdr(
        ReadOnlySpan<byte> src,
        Span<float> dst,
        int pixelCount,
        int channels,
        float gamma,
        float scale)
    {
        Span<float> customLut = stackalloc float[256];
        scoped ReadOnlySpan<float> colorLut = DefaultLdrToHdrLut;
        if (gamma != 2.2f || scale != 1.0f)
        {
            for (int i = 0; i < 256; i++)
            {
                customLut[i] = (float)(Math.Pow(i / 255.0f, gamma) * scale);
            }

            colorLut = customLut;
        }

        ReadOnlySpan<float> alphaLut = AlphaByteToFloatLut;
        switch (channels)
        {
            case 1:
                for (int i = 0; i < pixelCount; i++)
                {
                    dst[i] = colorLut[src[i]];
                }
                break;

            case 2:
                for (int i = 0; i < pixelCount; i++)
                {
                    int idx = i * 2;
                    dst[idx] = colorLut[src[idx]];
                    dst[idx + 1] = alphaLut[src[idx + 1]];
                }
                break;

            case 3:
                for (int i = 0; i < pixelCount; i++)
                {
                    int idx = i * 3;
                    dst[idx] = colorLut[src[idx]];
                    dst[idx + 1] = colorLut[src[idx + 1]];
                    dst[idx + 2] = colorLut[src[idx + 2]];
                }
                break;

            case 4:
                for (int i = 0; i < pixelCount; i++)
                {
                    int idx = i * 4;
                    dst[idx] = colorLut[src[idx]];
                    dst[idx + 1] = colorLut[src[idx + 1]];
                    dst[idx + 2] = colorLut[src[idx + 2]];
                    dst[idx + 3] = alphaLut[src[idx + 3]];
                }
                break;
        }
    }

    internal static void HdrToLdr8(
        ReadOnlySpan<float> src,
        Span<byte> dst,
        int pixelCount,
        int channels,
        float gamma,
        float scale)
    {
        float scaleInv = 1.0f / scale;
        float gammaInv = 1.0f / gamma;
        int colorChannels = (channels & 1) != 0 ? channels : channels - 1;
        bool hasAlpha = colorChannels < channels;

        for (int i = 0; i < pixelCount; i++)
        {
            int baseIdx = i * channels;
            for (int k = 0; k < colorChannels; k++)
            {
                float v = src[baseIdx + k] * scaleInv;
                float z = v <= 0f ? 0f : ((float)Math.Pow(v, gammaInv) * 255.0f) + 0.5f;
                dst[baseIdx + k] = ClampFloatToByte(z);
            }

            if (hasAlpha)
            {
                float z = (src[baseIdx + colorChannels] * 255.0f) + 0.5f;
                dst[baseIdx + colorChannels] = ClampFloatToByte(z);
            }
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static byte ClampFloatToByte(float z)
    {
        if (z <= 0f || float.IsNaN(z))
        {
            return 0;
        }

        if (z >= 255f)
        {
            return 255;
        }

        return (byte)(int)z;
    }
}
