using System.Buffers.Binary;
using System.Runtime.CompilerServices;
#if NET8_0_OR_GREATER
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.Arm;
using System.Runtime.Intrinsics.X86;
#endif

namespace Stb.Managed.Image.Internal;

/// <summary>
/// Chroma upsampling and YCbCr-to-RGB color conversion matching <c>stb_image.h</c> bit-for-bit
/// across AVX2, SSE2/SSSE3, Arm64 AdvSimd, and scalar paths.
/// </summary>
internal static class JpegColor
{
    // Exact integer values of stb_image.h's stbi__float2fixed(x) = ((int)((x) * 4096.0f + 0.5f)) << 8
    private const int CrToR = 5743 << 8;    // 1.40200f
    private const int CrToG = -2925 << 8;   // -0.71414f
    private const int CbToG = -1410 << 8;   // -0.34414f
    private const int CbToB = 7258 << 8;    // 1.77200f

    // 16-bit fixed-point equivalents used by SIMD mulhi_epi16 / vqdmulhq_s16 (0 differences over all 2^24 inputs)
    private const short CrToR16 = 5743;
    private const short CrToG16 = -2925;
    private const short CbToG16 = -1410;
    private const short CbToB16 = 7258;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static void ResampleRowV2(Span<byte> output, ReadOnlySpan<byte> inNear, ReadOnlySpan<byte> inFar, int w)
    {
#if NET8_0_OR_GREATER
        if (Avx2.IsSupported && w >= 16)
        {
            ResampleRowV2Avx2(output, inNear, inFar, w);
            return;
        }

        if (Sse2.IsSupported && w >= 8)
        {
            ResampleRowV2Sse2(output, inNear, inFar, w);
            return;
        }

        if (AdvSimd.Arm64.IsSupported && w >= 8)
        {
            ResampleRowV2AdvSimd(output, inNear, inFar, w);
            return;
        }
#endif
        ResampleRowV2Scalar(output, inNear, inFar, w);
    }

    internal static void ResampleRowV2Scalar(Span<byte> output, ReadOnlySpan<byte> inNear, ReadOnlySpan<byte> inFar, int w)
    {
        for (int i = 0; i < w; i++)
        {
            output[i] = (byte)((3 * inNear[i] + inFar[i] + 2) >> 2);
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static void ResampleRowH2(Span<byte> output, ReadOnlySpan<byte> inNear, int w)
    {
#if NET8_0_OR_GREATER
        if (Avx2.IsSupported && w >= 18)
        {
            ResampleRowH2Avx2(output, inNear, w);
            return;
        }

        if (Sse2.IsSupported && w >= 10)
        {
            ResampleRowH2Sse2(output, inNear, w);
            return;
        }

        if (AdvSimd.Arm64.IsSupported && w >= 10)
        {
            ResampleRowH2AdvSimd(output, inNear, w);
            return;
        }
#endif
        ResampleRowH2Scalar(output, inNear, w);
    }

    internal static void ResampleRowH2Scalar(Span<byte> output, ReadOnlySpan<byte> inNear, int w)
    {
        if (w == 1)
        {
            output[0] = output[1] = inNear[0];
            return;
        }

        output[0] = inNear[0];
        output[1] = (byte)((inNear[0] * 3 + inNear[1] + 2) >> 2);
        for (int i = 1; i < w - 1; i++)
        {
            int n = 3 * inNear[i] + 2;
            output[i * 2] = (byte)((n + inNear[i - 1]) >> 2);
            output[i * 2 + 1] = (byte)((n + inNear[i + 1]) >> 2);
        }

        output[w * 2 - 2] = (byte)((inNear[w - 2] * 3 + inNear[w - 1] + 2) >> 2);
        output[w * 2 - 1] = inNear[w - 1];
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static void ResampleRowHv2(Span<byte> output, ReadOnlySpan<byte> inNear, ReadOnlySpan<byte> inFar, int w)
    {
#if NET8_0_OR_GREATER
        if (Avx2.IsSupported && w >= 18)
        {
            ResampleRowHv2Avx2(output, inNear, inFar, w);
            return;
        }

        if (Sse2.IsSupported && w >= 10)
        {
            ResampleRowHv2Sse2(output, inNear, inFar, w);
            return;
        }

        if (AdvSimd.Arm64.IsSupported && w >= 10)
        {
            ResampleRowHv2AdvSimd(output, inNear, inFar, w);
            return;
        }
#endif
        ResampleRowHv2Scalar(output, inNear, inFar, w);
    }

    internal static void ResampleRowHv2Scalar(Span<byte> output, ReadOnlySpan<byte> inNear, ReadOnlySpan<byte> inFar, int w)
    {
        if (w == 1)
        {
            output[0] = output[1] = (byte)((3 * inNear[0] + inFar[0] + 2) >> 2);
            return;
        }

        int t1 = 3 * inNear[0] + inFar[0];
        output[0] = (byte)((t1 + 2) >> 2);
        for (int i = 1; i < w; i++)
        {
            int t0 = t1;
            t1 = 3 * inNear[i] + inFar[i];
            output[i * 2 - 1] = (byte)((3 * t0 + t1 + 8) >> 4);
            output[i * 2] = (byte)((3 * t1 + t0 + 8) >> 4);
        }

        output[w * 2 - 1] = (byte)((t1 + 2) >> 2);
    }

    internal static void ResampleRowGeneric(Span<byte> output, ReadOnlySpan<byte> inNear, int w, int hs)
    {
        for (int i = 0; i < w; i++)
        {
            byte val = inNear[i];
            int baseIdx = i * hs;
            for (int j = 0; j < hs; j++)
            {
                output[baseIdx + j] = val;
            }
        }
    }

    /// <summary>
    /// Converts a row of Y, Cb, Cr samples into interleaved RGB (<paramref name="step"/> = 3)
    /// or RGBA (<paramref name="step"/> = 4, with alpha = 255).
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static void YCbCrToRgbRow(
        Span<byte> output,
        ReadOnlySpan<byte> y,
        ReadOnlySpan<byte> cb,
        ReadOnlySpan<byte> cr,
        int count,
        int step)
    {
#if NET8_0_OR_GREATER
        if (step == 4)
        {
            if (Avx2.IsSupported && count >= 16)
            {
                YCbCrToRgbaRowAvx2(output, y, cb, cr, count);
                return;
            }

            if (Sse2.IsSupported && count >= 8)
            {
                YCbCrToRgbaRowSse2(output, y, cb, cr, count);
                return;
            }

            if (AdvSimd.Arm64.IsSupported && count >= 8)
            {
                YCbCrToRgbaRowAdvSimd(output, y, cb, cr, count);
                return;
            }
        }
        else if (step == 3)
        {
            if (Avx2.IsSupported && Ssse3.IsSupported && count >= 16)
            {
                YCbCrToRgbRowAvx2(output, y, cb, cr, count);
                return;
            }

            if (Ssse3.IsSupported && count >= 8)
            {
                YCbCrToRgbRowSsse3(output, y, cb, cr, count);
                return;
            }

            if (AdvSimd.Arm64.IsSupported && count >= 8)
            {
                YCbCrToRgbRowAdvSimd(output, y, cb, cr, count);
                return;
            }
        }
#endif
        YCbCrToRgbRowScalar(output, y, cb, cr, count, step);
    }

    internal static void YCbCrToRgbRowScalar(
        Span<byte> output,
        ReadOnlySpan<byte> y,
        ReadOnlySpan<byte> cb,
        ReadOnlySpan<byte> cr,
        int count,
        int step)
    {
        int dst = 0;
        for (int i = 0; i < count; i++, dst += step)
        {
            int yFixed = (y[i] << 20) + (1 << 19);
            int crVal = cr[i] - 128;
            int cbVal = cb[i] - 128;

            int r = yFixed + (crVal * CrToR);
            int g = yFixed + (crVal * CrToG) + ((cbVal * CbToG) & unchecked((int)0xFFFF0000));
            int b = yFixed + (cbVal * CbToB);

            r >>= 20;
            g >>= 20;
            b >>= 20;

            output[dst] = JpegIdct.ClampToByte(r);
            output[dst + 1] = JpegIdct.ClampToByte(g);
            output[dst + 2] = JpegIdct.ClampToByte(b);
            if (step == 4)
            {
                output[dst + 3] = 255;
            }
        }
    }

    internal static void ConvertScanline(
        Span<byte> dst,
        ReadOnlySpan<byte> c0,
        ReadOnlySpan<byte> c1,
        ReadOnlySpan<byte> c2,
        ReadOnlySpan<byte> c3,
        int width,
        int imgN,
        int outN,
        bool isRgb,
        int app14ColorTransform)
    {
        if (outN >= 3)
        {
            if (imgN == 3)
            {
                if (isRgb)
                {
                    int o = 0;
                    if (outN == 4)
                    {
                        for (int i = 0; i < width; i++, o += 4)
                        {
                            dst[o] = c0[i];
                            dst[o + 1] = c1[i];
                            dst[o + 2] = c2[i];
                            dst[o + 3] = 255;
                        }
                    }
                    else
                    {
                        for (int i = 0; i < width; i++, o += 3)
                        {
                            dst[o] = c0[i];
                            dst[o + 1] = c1[i];
                            dst[o + 2] = c2[i];
                        }
                    }
                }
                else
                {
                    YCbCrToRgbRow(dst, c0, c1, c2, width, outN);
                }
            }
            else if (imgN == 4)
            {
                if (app14ColorTransform == 0)
                {
                    // CMYK
                    int o = 0;
                    if (outN == 4)
                    {
                        for (int i = 0; i < width; i++, o += 4)
                        {
                            byte m = c3[i];
                            dst[o] = Blinn8x8(c0[i], m);
                            dst[o + 1] = Blinn8x8(c1[i], m);
                            dst[o + 2] = Blinn8x8(c2[i], m);
                            dst[o + 3] = 255;
                        }
                    }
                    else
                    {
                        for (int i = 0; i < width; i++, o += 3)
                        {
                            byte m = c3[i];
                            dst[o] = Blinn8x8(c0[i], m);
                            dst[o + 1] = Blinn8x8(c1[i], m);
                            dst[o + 2] = Blinn8x8(c2[i], m);
                        }
                    }
                }
                else if (app14ColorTransform == 2)
                {
                    // YCCK
                    YCbCrToRgbRow(dst, c0, c1, c2, width, outN);
                    int o = 0;
                    for (int i = 0; i < width; i++, o += outN)
                    {
                        byte m = c3[i];
                        dst[o] = Blinn8x8((byte)(255 - dst[o]), m);
                        dst[o + 1] = Blinn8x8((byte)(255 - dst[o + 1]), m);
                        dst[o + 2] = Blinn8x8((byte)(255 - dst[o + 2]), m);
                    }
                }
                else
                {
                    // YCbCr + ignored 4th channel (matches stb_image.h)
                    YCbCrToRgbRow(dst, c0, c1, c2, width, outN);
                }
            }
            else
            {
                // Grayscale expanded to RGB or RGBA
                int o = 0;
                if (outN == 4)
                {
                    for (int i = 0; i < width; i++, o += 4)
                    {
                        byte y = c0[i];
                        dst[o] = y;
                        dst[o + 1] = y;
                        dst[o + 2] = y;
                        dst[o + 3] = 255;
                    }
                }
                else
                {
                    for (int i = 0; i < width; i++, o += 3)
                    {
                        byte y = c0[i];
                        dst[o] = y;
                        dst[o + 1] = y;
                        dst[o + 2] = y;
                    }
                }
            }
        }
        else
        {
            if (isRgb)
            {
                if (outN == 1)
                {
                    for (int i = 0; i < width; i++)
                    {
                        dst[i] = PixelConverter.ComputeY8(c0[i], c1[i], c2[i]);
                    }
                }
                else
                {
                    int o = 0;
                    for (int i = 0; i < width; i++, o += 2)
                    {
                        dst[o] = PixelConverter.ComputeY8(c0[i], c1[i], c2[i]);
                        dst[o + 1] = 255;
                    }
                }
            }
            else if (imgN == 4 && app14ColorTransform == 0)
            {
                int o = 0;
                if (outN == 2)
                {
                    for (int i = 0; i < width; i++, o += 2)
                    {
                        byte m = c3[i];
                        byte r = Blinn8x8(c0[i], m);
                        byte g = Blinn8x8(c1[i], m);
                        byte b = Blinn8x8(c2[i], m);
                        dst[o] = PixelConverter.ComputeY8(r, g, b);
                        dst[o + 1] = 255;
                    }
                }
                else
                {
                    for (int i = 0; i < width; i++)
                    {
                        byte m = c3[i];
                        byte r = Blinn8x8(c0[i], m);
                        byte g = Blinn8x8(c1[i], m);
                        byte b = Blinn8x8(c2[i], m);
                        dst[i] = PixelConverter.ComputeY8(r, g, b);
                    }
                }
            }
            else if (imgN == 4 && app14ColorTransform == 2)
            {
                if (outN == 2)
                {
                    int o = 0;
                    for (int i = 0; i < width; i++, o += 2)
                    {
                        dst[o] = Blinn8x8((byte)(255 - c0[i]), c3[i]);
                        dst[o + 1] = 255;
                    }
                }
                else
                {
                    for (int i = 0; i < width; i++)
                    {
                        dst[i] = Blinn8x8((byte)(255 - c0[i]), c3[i]);
                    }
                }
            }
            else
            {
                if (outN == 1)
                {
                    c0.Slice(0, width).CopyTo(dst);
                }
                else
                {
                    int o = 0;
                    for (int i = 0; i < width; i++, o += 2)
                    {
                        dst[o] = c0[i];
                        dst[o + 1] = 255;
                    }
                }
            }
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static byte Blinn8x8(byte x, byte y)
    {
        uint t = (uint)(x * y) + 128u;
        return (byte)((t + (t >> 8)) >> 8);
    }

#if NET8_0_OR_GREATER
    // -------------------------------------------------------------------------
    // AVX2 Resamplers
    // -------------------------------------------------------------------------

    private static void ResampleRowV2Avx2(Span<byte> output, ReadOnlySpan<byte> inNear, ReadOnlySpan<byte> inFar, int w)
    {
        if (w > output.Length || w > inNear.Length || w > inFar.Length)
        {
            ThrowBounds();
        }

        Span<byte> dst = output;
        ReadOnlySpan<byte> nearRef = inNear;
        ReadOnlySpan<byte> farRef = inFar;
        Vector256<short> c2 = Vector256.Create((short)2);

        int i = 0;
        for (; i + 16 <= w; i += 16)
        {
            Vector256<short> n = Avx2.ConvertToVector256Int16(Vector128.Create(nearRef.Slice(i, 16)));
            Vector256<short> f = Avx2.ConvertToVector256Int16(Vector128.Create(farRef.Slice(i, 16)));
            Vector256<short> res = Avx2.ShiftRightArithmetic(Avx2.Add(Avx2.Add(Avx2.Add(Avx2.ShiftLeftLogical(n, 1), n), f), c2), 2);
            Vector256<byte> packed = Avx2.PackUnsignedSaturate(res, res);
            // Low 8 bytes of lane 0 = pixels 0..7, low 8 bytes of lane 1 = pixels 8..15
            Vector256<ulong> u = packed.AsUInt64();
            BinaryPrimitives.WriteUInt64LittleEndian(dst.Slice(i, 8), u.GetElement(0));
            BinaryPrimitives.WriteUInt64LittleEndian(dst.Slice(i + 8, 8), u.GetElement(2));
        }

        for (; i < w; i++)
        {
            output[i] = (byte)((3 * inNear[i] + inFar[i] + 2) >> 2);
        }
    }

    private static void ResampleRowH2Avx2(Span<byte> output, ReadOnlySpan<byte> inNear, int w)
    {
        if (w * 2 > output.Length || w > inNear.Length)
        {
            ThrowBounds();
        }

        Span<byte> dst = output;
        ReadOnlySpan<byte> nearRef = inNear;
        Vector256<short> c2 = Vector256.Create((short)2);

        output[0] = inNear[0];
        output[1] = (byte)((inNear[0] * 3 + inNear[1] + 2) >> 2);

        int k = 1;
        for (; k + 16 < w; k += 16)
        {
            Vector256<short> prev = Avx2.ConvertToVector256Int16(Vector128.Create(nearRef.Slice(k - 1, 16)));
            Vector256<short> curr = Avx2.ConvertToVector256Int16(Vector128.Create(nearRef.Slice(k, 16)));
            Vector256<short> next = Avx2.ConvertToVector256Int16(Vector128.Create(nearRef.Slice(k + 1, 16)));

            Vector256<short> c3p2 = Avx2.Add(Avx2.Add(Avx2.ShiftLeftLogical(curr, 1), curr), c2);
            Vector256<short> even = Avx2.ShiftRightArithmetic(Avx2.Add(c3p2, prev), 2);
            Vector256<short> odd = Avx2.ShiftRightArithmetic(Avx2.Add(c3p2, next), 2);

            // In-lane UnpackLow/UnpackHigh + PackUnsignedSaturate produces exact linear order [e0,o0..e15,o15]!
            Vector256<short> lo = Avx2.UnpackLow(even, odd);
            Vector256<short> hi = Avx2.UnpackHigh(even, odd);
            Vector256<byte> packed = Avx2.PackUnsignedSaturate(lo, hi);
            packed.CopyTo(dst.Slice(k * 2, 32));
        }

        for (int i = k; i < w - 1; i++)
        {
            int n = 3 * inNear[i] + 2;
            output[i * 2] = (byte)((n + inNear[i - 1]) >> 2);
            output[i * 2 + 1] = (byte)((n + inNear[i + 1]) >> 2);
        }

        output[w * 2 - 2] = (byte)((inNear[w - 2] * 3 + inNear[w - 1] + 2) >> 2);
        output[w * 2 - 1] = inNear[w - 1];
    }

    private static void ResampleRowHv2Avx2(Span<byte> output, ReadOnlySpan<byte> inNear, ReadOnlySpan<byte> inFar, int w)
    {
        if (w * 2 > output.Length || w > inNear.Length || w > inFar.Length)
        {
            ThrowBounds();
        }

        Span<byte> dst = output;
        ReadOnlySpan<byte> nearRef = inNear;
        ReadOnlySpan<byte> farRef = inFar;
        Vector256<short> c8 = Vector256.Create((short)8);

        int t0 = 3 * inNear[0] + inFar[0];
        int t1 = 3 * inNear[1] + inFar[1];
        output[0] = (byte)((t0 + 2) >> 2);
        output[1] = (byte)((3 * t0 + t1 + 8) >> 4);

        int k = 1;
        for (; k + 16 < w; k += 16)
        {
            Vector256<short> nPrev = Avx2.ConvertToVector256Int16(Vector128.Create(nearRef.Slice(k - 1, 16)));
            Vector256<short> fPrev = Avx2.ConvertToVector256Int16(Vector128.Create(farRef.Slice(k - 1, 16)));
            Vector256<short> prev = Avx2.Add(Avx2.Add(Avx2.ShiftLeftLogical(nPrev, 1), nPrev), fPrev);

            Vector256<short> nCurr = Avx2.ConvertToVector256Int16(Vector128.Create(nearRef.Slice(k, 16)));
            Vector256<short> fCurr = Avx2.ConvertToVector256Int16(Vector128.Create(farRef.Slice(k, 16)));
            Vector256<short> curr = Avx2.Add(Avx2.Add(Avx2.ShiftLeftLogical(nCurr, 1), nCurr), fCurr);

            Vector256<short> nNext = Avx2.ConvertToVector256Int16(Vector128.Create(nearRef.Slice(k + 1, 16)));
            Vector256<short> fNext = Avx2.ConvertToVector256Int16(Vector128.Create(farRef.Slice(k + 1, 16)));
            Vector256<short> next = Avx2.Add(Avx2.Add(Avx2.ShiftLeftLogical(nNext, 1), nNext), fNext);

            Vector256<short> c3p8 = Avx2.Add(Avx2.Add(Avx2.ShiftLeftLogical(curr, 1), curr), c8);
            Vector256<short> even = Avx2.ShiftRightArithmetic(Avx2.Add(c3p8, prev), 4);
            Vector256<short> odd = Avx2.ShiftRightArithmetic(Avx2.Add(c3p8, next), 4);

            Vector256<short> lo = Avx2.UnpackLow(even, odd);
            Vector256<short> hi = Avx2.UnpackHigh(even, odd);
            Vector256<byte> packed = Avx2.PackUnsignedSaturate(lo, hi);
            packed.CopyTo(dst.Slice(k * 2, 32));
        }

        int prevScalar = 3 * inNear[k - 1] + inFar[k - 1];
        for (int i = k; i < w; i++)
        {
            int curScalar = 3 * inNear[i] + inFar[i];
            output[i * 2 - 1] = (byte)((3 * prevScalar + curScalar + 8) >> 4);
            output[i * 2] = (byte)((3 * curScalar + prevScalar + 8) >> 4);
            prevScalar = curScalar;
        }

        output[w * 2 - 1] = (byte)((prevScalar + 2) >> 2);
    }

    // -------------------------------------------------------------------------
    // SSE2 Resamplers
    // -------------------------------------------------------------------------

    private static void ResampleRowV2Sse2(Span<byte> output, ReadOnlySpan<byte> inNear, ReadOnlySpan<byte> inFar, int w)
    {
        if (w > output.Length || w > inNear.Length || w > inFar.Length)
        {
            ThrowBounds();
        }

        Span<byte> dst = output;
        ReadOnlySpan<byte> nearRef = inNear;
        ReadOnlySpan<byte> farRef = inFar;
        Vector128<byte> zero = Vector128<byte>.Zero;
        Vector128<short> c2 = Vector128.Create((short)2);

        int i = 0;
        for (; i + 8 <= w; i += 8)
        {
            Vector128<short> n = Sse2.UnpackLow(Load8Bytes(nearRef, i), zero).AsInt16();
            Vector128<short> f = Sse2.UnpackLow(Load8Bytes(farRef, i), zero).AsInt16();
            Vector128<short> res = Sse2.ShiftRightArithmetic(Sse2.Add(Sse2.Add(Sse2.Add(Sse2.ShiftLeftLogical(n, 1), n), f), c2), 2);
            Vector128<byte> packed = Sse2.PackUnsignedSaturate(res, res);
            BinaryPrimitives.WriteUInt64LittleEndian(dst.Slice(i, 8), packed.AsUInt64().GetElement(0));
        }

        for (; i < w; i++)
        {
            output[i] = (byte)((3 * inNear[i] + inFar[i] + 2) >> 2);
        }
    }

    private static void ResampleRowH2Sse2(Span<byte> output, ReadOnlySpan<byte> inNear, int w)
    {
        if (w * 2 > output.Length || w > inNear.Length)
        {
            ThrowBounds();
        }

        Span<byte> dst = output;
        ReadOnlySpan<byte> nearRef = inNear;
        Vector128<byte> zero = Vector128<byte>.Zero;
        Vector128<short> c2 = Vector128.Create((short)2);

        output[0] = inNear[0];
        output[1] = (byte)((inNear[0] * 3 + inNear[1] + 2) >> 2);

        int k = 1;
        for (; k + 8 < w; k += 8)
        {
            Vector128<short> prev = Sse2.UnpackLow(Load8Bytes(nearRef, k - 1), zero).AsInt16();
            Vector128<short> curr = Sse2.UnpackLow(Load8Bytes(nearRef, k), zero).AsInt16();
            Vector128<short> next = Sse2.UnpackLow(Load8Bytes(nearRef, k + 1), zero).AsInt16();

            Vector128<short> c3p2 = Sse2.Add(Sse2.Add(Sse2.ShiftLeftLogical(curr, 1), curr), c2);
            Vector128<short> even = Sse2.ShiftRightArithmetic(Sse2.Add(c3p2, prev), 2);
            Vector128<short> odd = Sse2.ShiftRightArithmetic(Sse2.Add(c3p2, next), 2);

            Vector128<byte> packed = Sse2.PackUnsignedSaturate(Sse2.UnpackLow(even, odd), Sse2.UnpackHigh(even, odd));
            packed.CopyTo(dst.Slice(k * 2, 16));
        }

        for (int i = k; i < w - 1; i++)
        {
            int n = 3 * inNear[i] + 2;
            output[i * 2] = (byte)((n + inNear[i - 1]) >> 2);
            output[i * 2 + 1] = (byte)((n + inNear[i + 1]) >> 2);
        }

        output[w * 2 - 2] = (byte)((inNear[w - 2] * 3 + inNear[w - 1] + 2) >> 2);
        output[w * 2 - 1] = inNear[w - 1];
    }

    private static void ResampleRowHv2Sse2(Span<byte> output, ReadOnlySpan<byte> inNear, ReadOnlySpan<byte> inFar, int w)
    {
        if (w * 2 > output.Length || w > inNear.Length || w > inFar.Length)
        {
            ThrowBounds();
        }

        Span<byte> dst = output;
        ReadOnlySpan<byte> nearRef = inNear;
        ReadOnlySpan<byte> farRef = inFar;
        Vector128<byte> zero = Vector128<byte>.Zero;
        Vector128<short> c8 = Vector128.Create((short)8);

        int t0 = 3 * inNear[0] + inFar[0];
        int t1 = 3 * inNear[1] + inFar[1];
        output[0] = (byte)((t0 + 2) >> 2);
        output[1] = (byte)((3 * t0 + t1 + 8) >> 4);

        int k = 1;
        for (; k + 8 < w; k += 8)
        {
            Vector128<short> nPrev = Sse2.UnpackLow(Load8Bytes(nearRef, k - 1), zero).AsInt16();
            Vector128<short> fPrev = Sse2.UnpackLow(Load8Bytes(farRef, k - 1), zero).AsInt16();
            Vector128<short> prev = Sse2.Add(Sse2.Add(Sse2.ShiftLeftLogical(nPrev, 1), nPrev), fPrev);

            Vector128<short> nCurr = Sse2.UnpackLow(Load8Bytes(nearRef, k), zero).AsInt16();
            Vector128<short> fCurr = Sse2.UnpackLow(Load8Bytes(farRef, k), zero).AsInt16();
            Vector128<short> curr = Sse2.Add(Sse2.Add(Sse2.ShiftLeftLogical(nCurr, 1), nCurr), fCurr);

            Vector128<short> nNext = Sse2.UnpackLow(Load8Bytes(nearRef, k + 1), zero).AsInt16();
            Vector128<short> fNext = Sse2.UnpackLow(Load8Bytes(farRef, k + 1), zero).AsInt16();
            Vector128<short> next = Sse2.Add(Sse2.Add(Sse2.ShiftLeftLogical(nNext, 1), nNext), fNext);

            Vector128<short> c3p8 = Sse2.Add(Sse2.Add(Sse2.ShiftLeftLogical(curr, 1), curr), c8);
            Vector128<short> even = Sse2.ShiftRightArithmetic(Sse2.Add(c3p8, prev), 4);
            Vector128<short> odd = Sse2.ShiftRightArithmetic(Sse2.Add(c3p8, next), 4);

            Vector128<byte> packed = Sse2.PackUnsignedSaturate(Sse2.UnpackLow(even, odd), Sse2.UnpackHigh(even, odd));
            packed.CopyTo(dst.Slice(k * 2, 16));
        }

        int prevScalar = 3 * inNear[k - 1] + inFar[k - 1];
        for (int i = k; i < w; i++)
        {
            int curScalar = 3 * inNear[i] + inFar[i];
            output[i * 2 - 1] = (byte)((3 * prevScalar + curScalar + 8) >> 4);
            output[i * 2] = (byte)((3 * curScalar + prevScalar + 8) >> 4);
            prevScalar = curScalar;
        }

        output[w * 2 - 1] = (byte)((prevScalar + 2) >> 2);
    }

    // -------------------------------------------------------------------------
    // Arm64 AdvSimd Resamplers
    // -------------------------------------------------------------------------

    private static void ResampleRowV2AdvSimd(Span<byte> output, ReadOnlySpan<byte> inNear, ReadOnlySpan<byte> inFar, int w)
    {
        if (w > output.Length || w > inNear.Length || w > inFar.Length)
        {
            ThrowBounds();
        }

        Span<byte> dst = output;
        ReadOnlySpan<byte> nearRef = inNear;
        ReadOnlySpan<byte> farRef = inFar;
        Vector128<short> c2 = Vector128.Create((short)2);

        int i = 0;
        for (; i + 8 <= w; i += 8)
        {
            Vector128<short> n = AdvSimd.ZeroExtendWideningLower(Load8Bytes64(nearRef, i)).AsInt16();
            Vector128<short> f = AdvSimd.ZeroExtendWideningLower(Load8Bytes64(farRef, i)).AsInt16();
            Vector128<short> res = AdvSimd.ShiftRightArithmetic(AdvSimd.Add(AdvSimd.Add(AdvSimd.Add(AdvSimd.ShiftLeftLogical(n, 1), n), f), c2), 2);
            Vector64<byte> packed = AdvSimd.ExtractNarrowingSaturateUnsignedLower(res);
            BinaryPrimitives.WriteUInt64LittleEndian(dst.Slice(i, 8), packed.AsUInt64().GetElement(0));
        }

        for (; i < w; i++)
        {
            output[i] = (byte)((3 * inNear[i] + inFar[i] + 2) >> 2);
        }
    }

    private static void ResampleRowH2AdvSimd(Span<byte> output, ReadOnlySpan<byte> inNear, int w)
    {
        if (w * 2 > output.Length || w > inNear.Length)
        {
            ThrowBounds();
        }

        Span<byte> dst = output;
        ReadOnlySpan<byte> nearRef = inNear;
        Vector128<short> c2 = Vector128.Create((short)2);

        output[0] = inNear[0];
        output[1] = (byte)((inNear[0] * 3 + inNear[1] + 2) >> 2);

        int k = 1;
        for (; k + 8 < w; k += 8)
        {
            Vector128<short> prev = AdvSimd.ZeroExtendWideningLower(Load8Bytes64(nearRef, k - 1)).AsInt16();
            Vector128<short> curr = AdvSimd.ZeroExtendWideningLower(Load8Bytes64(nearRef, k)).AsInt16();
            Vector128<short> next = AdvSimd.ZeroExtendWideningLower(Load8Bytes64(nearRef, k + 1)).AsInt16();

            Vector128<short> c3p2 = AdvSimd.Add(AdvSimd.Add(AdvSimd.ShiftLeftLogical(curr, 1), curr), c2);
            Vector64<byte> e8 = AdvSimd.ExtractNarrowingSaturateUnsignedLower(AdvSimd.ShiftRightArithmetic(AdvSimd.Add(c3p2, prev), 2));
            Vector64<byte> o8 = AdvSimd.ExtractNarrowingSaturateUnsignedLower(AdvSimd.ShiftRightArithmetic(AdvSimd.Add(c3p2, next), 2));

            Vector128<byte> packed = AdvSimd.Arm64.ZipLow(e8.ToVector128Unsafe(), o8.ToVector128Unsafe());
            packed.CopyTo(dst.Slice(k * 2, 16));
        }

        for (int i = k; i < w - 1; i++)
        {
            int n = 3 * inNear[i] + 2;
            output[i * 2] = (byte)((n + inNear[i - 1]) >> 2);
            output[i * 2 + 1] = (byte)((n + inNear[i + 1]) >> 2);
        }

        output[w * 2 - 2] = (byte)((inNear[w - 2] * 3 + inNear[w - 1] + 2) >> 2);
        output[w * 2 - 1] = inNear[w - 1];
    }

    private static void ResampleRowHv2AdvSimd(Span<byte> output, ReadOnlySpan<byte> inNear, ReadOnlySpan<byte> inFar, int w)
    {
        if (w * 2 > output.Length || w > inNear.Length || w > inFar.Length)
        {
            ThrowBounds();
        }

        Span<byte> dst = output;
        ReadOnlySpan<byte> nearRef = inNear;
        ReadOnlySpan<byte> farRef = inFar;
        Vector128<short> c8 = Vector128.Create((short)8);

        int t0 = 3 * inNear[0] + inFar[0];
        int t1 = 3 * inNear[1] + inFar[1];
        output[0] = (byte)((t0 + 2) >> 2);
        output[1] = (byte)((3 * t0 + t1 + 8) >> 4);

        int k = 1;
        for (; k + 8 < w; k += 8)
        {
            Vector128<short> nPrev = AdvSimd.ZeroExtendWideningLower(Load8Bytes64(nearRef, k - 1)).AsInt16();
            Vector128<short> fPrev = AdvSimd.ZeroExtendWideningLower(Load8Bytes64(farRef, k - 1)).AsInt16();
            Vector128<short> prev = AdvSimd.Add(AdvSimd.Add(AdvSimd.ShiftLeftLogical(nPrev, 1), nPrev), fPrev);

            Vector128<short> nCurr = AdvSimd.ZeroExtendWideningLower(Load8Bytes64(nearRef, k)).AsInt16();
            Vector128<short> fCurr = AdvSimd.ZeroExtendWideningLower(Load8Bytes64(farRef, k)).AsInt16();
            Vector128<short> curr = AdvSimd.Add(AdvSimd.Add(AdvSimd.ShiftLeftLogical(nCurr, 1), nCurr), fCurr);

            Vector128<short> nNext = AdvSimd.ZeroExtendWideningLower(Load8Bytes64(nearRef, k + 1)).AsInt16();
            Vector128<short> fNext = AdvSimd.ZeroExtendWideningLower(Load8Bytes64(farRef, k + 1)).AsInt16();
            Vector128<short> next = AdvSimd.Add(AdvSimd.Add(AdvSimd.ShiftLeftLogical(nNext, 1), nNext), fNext);

            Vector128<short> c3p8 = AdvSimd.Add(AdvSimd.Add(AdvSimd.ShiftLeftLogical(curr, 1), curr), c8);
            Vector64<byte> e8 = AdvSimd.ExtractNarrowingSaturateUnsignedLower(AdvSimd.ShiftRightArithmetic(AdvSimd.Add(c3p8, prev), 4));
            Vector64<byte> o8 = AdvSimd.ExtractNarrowingSaturateUnsignedLower(AdvSimd.ShiftRightArithmetic(AdvSimd.Add(c3p8, next), 4));

            Vector128<byte> packed = AdvSimd.Arm64.ZipLow(e8.ToVector128Unsafe(), o8.ToVector128Unsafe());
            packed.CopyTo(dst.Slice(k * 2, 16));
        }

        int prevScalar = 3 * inNear[k - 1] + inFar[k - 1];
        for (int i = k; i < w; i++)
        {
            int curScalar = 3 * inNear[i] + inFar[i];
            output[i * 2 - 1] = (byte)((3 * prevScalar + curScalar + 8) >> 4);
            output[i * 2] = (byte)((3 * curScalar + prevScalar + 8) >> 4);
            prevScalar = curScalar;
        }

        output[w * 2 - 1] = (byte)((prevScalar + 2) >> 2);
    }

    // -------------------------------------------------------------------------
    // AVX2 YCbCr -> RGBA (step=4) & RGB (step=3) (16 pixels / iteration)
    // -------------------------------------------------------------------------

    private static void YCbCrToRgbaRowAvx2(Span<byte> output, ReadOnlySpan<byte> y, ReadOnlySpan<byte> cb, ReadOnlySpan<byte> cr, int count)
    {
        if (count * 4 > output.Length || count > y.Length || count > cb.Length || count > cr.Length)
        {
            ThrowBounds();
        }

        Span<byte> dst = output;
        ReadOnlySpan<byte> yRef = y;
        ReadOnlySpan<byte> cbRef = cb;
        ReadOnlySpan<byte> crRef = cr;

        Vector256<short> c8 = Vector256.Create((short)8);
        Vector256<short> signFlip = Vector256.Create(unchecked((short)0x8000));
        Vector256<short> cCrR = Vector256.Create(CrToR16);
        Vector256<short> cCrG = Vector256.Create(CrToG16);
        Vector256<short> cCbG = Vector256.Create(CbToG16);
        Vector256<short> cCbB = Vector256.Create(CbToB16);
        Vector256<short> c255 = Vector256.Create((short)255);

        int i = 0;
        for (; i + 16 <= count; i += 16)
        {
            Vector256<short> yw = Avx2.Add(Avx2.ShiftLeftLogical(Avx2.ConvertToVector256Int16(Vector128.Create(yRef.Slice(i, 16))), 4), c8);
            Vector256<short> cbw = Avx2.Xor(Avx2.ShiftLeftLogical(Avx2.ConvertToVector256Int16(Vector128.Create(cbRef.Slice(i, 16))), 8), signFlip);
            Vector256<short> crw = Avx2.Xor(Avx2.ShiftLeftLogical(Avx2.ConvertToVector256Int16(Vector128.Create(crRef.Slice(i, 16))), 8), signFlip);

            Vector256<short> crR = Avx2.MultiplyHigh(crw, cCrR);
            Vector256<short> crG = Avx2.MultiplyHigh(crw, cCrG);
            Vector256<short> cbG = Avx2.MultiplyHigh(cbw, cCbG);
            Vector256<short> cbB = Avx2.MultiplyHigh(cbw, cCbB);

            Vector256<short> rw = Avx2.ShiftRightArithmetic(Avx2.Add(yw, crR), 4);
            Vector256<short> gw = Avx2.ShiftRightArithmetic(Avx2.Add(yw, Avx2.Add(crG, cbG)), 4);
            Vector256<short> bw = Avx2.ShiftRightArithmetic(Avx2.Add(yw, cbB), 4);

            Vector256<byte> brb = Avx2.PackUnsignedSaturate(rw, bw);
            Vector256<byte> gxa = Avx2.PackUnsignedSaturate(gw, c255);
            Vector256<byte> t0 = Avx2.UnpackLow(brb, gxa);
            Vector256<byte> t1 = Avx2.UnpackHigh(brb, gxa);

            Vector256<byte> o0 = Avx2.UnpackLow(t0.AsUInt16(), t1.AsUInt16()).AsByte();
            Vector256<byte> o1 = Avx2.UnpackHigh(t0.AsUInt16(), t1.AsUInt16()).AsByte();

            Vector256<byte> out0 = Avx2.Permute2x128(o0, o1, 0x20);
            Vector256<byte> out1 = Avx2.Permute2x128(o0, o1, 0x31);

            int dstOff = i * 4;
            out0.CopyTo(dst.Slice(dstOff, 32));
            out1.CopyTo(dst.Slice(dstOff + 32, 32));
        }

        if (i < count)
        {
            YCbCrToRgbRowScalar(output.Slice(i * 4), y.Slice(i), cb.Slice(i), cr.Slice(i), count - i, 4);
        }
    }

    private static void YCbCrToRgbRowAvx2(Span<byte> output, ReadOnlySpan<byte> y, ReadOnlySpan<byte> cb, ReadOnlySpan<byte> cr, int count)
    {
        if (count * 3 > output.Length || count > y.Length || count > cb.Length || count > cr.Length)
        {
            ThrowBounds();
        }

        Span<byte> dst = output;
        ReadOnlySpan<byte> yRef = y;
        ReadOnlySpan<byte> cbRef = cb;
        ReadOnlySpan<byte> crRef = cr;

        Vector256<short> c8 = Vector256.Create((short)8);
        Vector256<short> signFlip = Vector256.Create(unchecked((short)0x8000));
        Vector256<short> cCrR = Vector256.Create(CrToR16);
        Vector256<short> cCrG = Vector256.Create(CrToG16);
        Vector256<short> cCbG = Vector256.Create(CbToG16);
        Vector256<short> cCbB = Vector256.Create(CbToB16);
        Vector256<short> c255 = Vector256.Create((short)255);
        Vector128<byte> rgbMask = Vector128.Create(
            (byte)0, 1, 2, 4, 5, 6, 8, 9, 10, 12, 13, 14, 0x80, 0x80, 0x80, 0x80);

        int i = 0;
        int dstOff = 0;
        for (; i + 16 <= count; i += 16, dstOff += 48)
        {
            Vector256<short> yw = Avx2.Add(Avx2.ShiftLeftLogical(Avx2.ConvertToVector256Int16(Vector128.Create(yRef.Slice(i, 16))), 4), c8);
            Vector256<short> cbw = Avx2.Xor(Avx2.ShiftLeftLogical(Avx2.ConvertToVector256Int16(Vector128.Create(cbRef.Slice(i, 16))), 8), signFlip);
            Vector256<short> crw = Avx2.Xor(Avx2.ShiftLeftLogical(Avx2.ConvertToVector256Int16(Vector128.Create(crRef.Slice(i, 16))), 8), signFlip);

            Vector256<short> crR = Avx2.MultiplyHigh(crw, cCrR);
            Vector256<short> crG = Avx2.MultiplyHigh(crw, cCrG);
            Vector256<short> cbG = Avx2.MultiplyHigh(cbw, cCbG);
            Vector256<short> cbB = Avx2.MultiplyHigh(cbw, cCbB);

            Vector256<short> rw = Avx2.ShiftRightArithmetic(Avx2.Add(yw, crR), 4);
            Vector256<short> gw = Avx2.ShiftRightArithmetic(Avx2.Add(yw, Avx2.Add(crG, cbG)), 4);
            Vector256<short> bw = Avx2.ShiftRightArithmetic(Avx2.Add(yw, cbB), 4);

            Vector256<byte> brb = Avx2.PackUnsignedSaturate(rw, bw);
            Vector256<byte> gxa = Avx2.PackUnsignedSaturate(gw, c255);
            Vector256<byte> t0 = Avx2.UnpackLow(brb, gxa);
            Vector256<byte> t1 = Avx2.UnpackHigh(brb, gxa);

            Vector256<byte> o0 = Avx2.UnpackLow(t0.AsUInt16(), t1.AsUInt16()).AsByte();
            Vector256<byte> o1 = Avx2.UnpackHigh(t0.AsUInt16(), t1.AsUInt16()).AsByte();

            // 4 chunks of 4 RGBA pixels -> compact to 12 RGB bytes each
            Vector128<byte> c0 = Ssse3.Shuffle(o0.GetLower(), rgbMask);
            Vector128<byte> c1 = Ssse3.Shuffle(o1.GetLower(), rgbMask);
            Vector128<byte> c2 = Ssse3.Shuffle(o0.GetUpper(), rgbMask);
            Vector128<byte> c3 = Ssse3.Shuffle(o1.GetUpper(), rgbMask);

            c0.CopyTo(dst.Slice(dstOff, 16));
            c1.CopyTo(dst.Slice(dstOff + 12, 16));
            c2.CopyTo(dst.Slice(dstOff + 24, 16));
            if (dstOff + 52 <= output.Length)
            {
                c3.CopyTo(dst.Slice(dstOff + 36, 16));
            }
            else
            {
                Store12Bytes(dst, dstOff + 36, c3);
            }
        }

        if (i < count)
        {
            YCbCrToRgbRowScalar(output.Slice(dstOff), y.Slice(i), cb.Slice(i), cr.Slice(i), count - i, 3);
        }
    }

    // -------------------------------------------------------------------------
    // SSE2 / SSSE3 YCbCr -> RGBA (step=4) & RGB (step=3) (8 pixels / iteration)
    // -------------------------------------------------------------------------

    private static void YCbCrToRgbaRowSse2(Span<byte> output, ReadOnlySpan<byte> y, ReadOnlySpan<byte> cb, ReadOnlySpan<byte> cr, int count)
    {
        if (count * 4 > output.Length || count > y.Length || count > cb.Length || count > cr.Length)
        {
            ThrowBounds();
        }

        Span<byte> dst = output;
        ReadOnlySpan<byte> yRef = y;
        ReadOnlySpan<byte> cbRef = cb;
        ReadOnlySpan<byte> crRef = cr;

        Vector128<byte> zero = Vector128<byte>.Zero;
        Vector128<short> c8 = Vector128.Create((short)8);
        Vector128<short> signFlip = Vector128.Create(unchecked((short)0x8000));
        Vector128<short> cCrR = Vector128.Create(CrToR16);
        Vector128<short> cCrG = Vector128.Create(CrToG16);
        Vector128<short> cCbG = Vector128.Create(CbToG16);
        Vector128<short> cCbB = Vector128.Create(CbToB16);
        Vector128<short> c255 = Vector128.Create((short)255);

        int i = 0;
        for (; i + 8 <= count; i += 8)
        {
            Vector128<short> yw = Sse2.Add(Sse2.ShiftLeftLogical(Sse2.UnpackLow(Load8Bytes(yRef, i), zero).AsInt16(), 4), c8);
            Vector128<short> cbw = Sse2.Xor(Sse2.UnpackLow(zero, Load8Bytes(cbRef, i)).AsInt16(), signFlip);
            Vector128<short> crw = Sse2.Xor(Sse2.UnpackLow(zero, Load8Bytes(crRef, i)).AsInt16(), signFlip);

            Vector128<short> crR = Sse2.MultiplyHigh(crw, cCrR);
            Vector128<short> crG = Sse2.MultiplyHigh(crw, cCrG);
            Vector128<short> cbG = Sse2.MultiplyHigh(cbw, cCbG);
            Vector128<short> cbB = Sse2.MultiplyHigh(cbw, cCbB);

            Vector128<short> rw = Sse2.ShiftRightArithmetic(Sse2.Add(yw, crR), 4);
            Vector128<short> gw = Sse2.ShiftRightArithmetic(Sse2.Add(yw, Sse2.Add(crG, cbG)), 4);
            Vector128<short> bw = Sse2.ShiftRightArithmetic(Sse2.Add(yw, cbB), 4);

            Vector128<byte> brb = Sse2.PackUnsignedSaturate(rw, bw);
            Vector128<byte> gxa = Sse2.PackUnsignedSaturate(gw, c255);
            Vector128<byte> t0 = Sse2.UnpackLow(brb, gxa);
            Vector128<byte> t1 = Sse2.UnpackHigh(brb, gxa);

            Vector128<byte> out0 = Sse2.UnpackLow(t0.AsUInt16(), t1.AsUInt16()).AsByte();
            Vector128<byte> out1 = Sse2.UnpackHigh(t0.AsUInt16(), t1.AsUInt16()).AsByte();

            int dstOff = i * 4;
            out0.CopyTo(dst.Slice(dstOff, 16));
            out1.CopyTo(dst.Slice(dstOff + 16, 16));
        }

        if (i < count)
        {
            YCbCrToRgbRowScalar(output.Slice(i * 4), y.Slice(i), cb.Slice(i), cr.Slice(i), count - i, 4);
        }
    }

    private static void YCbCrToRgbRowSsse3(Span<byte> output, ReadOnlySpan<byte> y, ReadOnlySpan<byte> cb, ReadOnlySpan<byte> cr, int count)
    {
        if (count * 3 > output.Length || count > y.Length || count > cb.Length || count > cr.Length)
        {
            ThrowBounds();
        }

        Span<byte> dst = output;
        ReadOnlySpan<byte> yRef = y;
        ReadOnlySpan<byte> cbRef = cb;
        ReadOnlySpan<byte> crRef = cr;

        Vector128<byte> zero = Vector128<byte>.Zero;
        Vector128<short> c8 = Vector128.Create((short)8);
        Vector128<short> signFlip = Vector128.Create(unchecked((short)0x8000));
        Vector128<short> cCrR = Vector128.Create(CrToR16);
        Vector128<short> cCrG = Vector128.Create(CrToG16);
        Vector128<short> cCbG = Vector128.Create(CbToG16);
        Vector128<short> cCbB = Vector128.Create(CbToB16);
        Vector128<short> c255 = Vector128.Create((short)255);
        Vector128<byte> rgbMask = Vector128.Create(
            (byte)0, 1, 2, 4, 5, 6, 8, 9, 10, 12, 13, 14, 0x80, 0x80, 0x80, 0x80);

        int i = 0;
        int dstOff = 0;
        for (; i + 8 <= count; i += 8, dstOff += 24)
        {
            Vector128<short> yw = Sse2.Add(Sse2.ShiftLeftLogical(Sse2.UnpackLow(Load8Bytes(yRef, i), zero).AsInt16(), 4), c8);
            Vector128<short> cbw = Sse2.Xor(Sse2.UnpackLow(zero, Load8Bytes(cbRef, i)).AsInt16(), signFlip);
            Vector128<short> crw = Sse2.Xor(Sse2.UnpackLow(zero, Load8Bytes(crRef, i)).AsInt16(), signFlip);

            Vector128<short> crR = Sse2.MultiplyHigh(crw, cCrR);
            Vector128<short> crG = Sse2.MultiplyHigh(crw, cCrG);
            Vector128<short> cbG = Sse2.MultiplyHigh(cbw, cCbG);
            Vector128<short> cbB = Sse2.MultiplyHigh(cbw, cCbB);

            Vector128<short> rw = Sse2.ShiftRightArithmetic(Sse2.Add(yw, crR), 4);
            Vector128<short> gw = Sse2.ShiftRightArithmetic(Sse2.Add(yw, Sse2.Add(crG, cbG)), 4);
            Vector128<short> bw = Sse2.ShiftRightArithmetic(Sse2.Add(yw, cbB), 4);

            Vector128<byte> brb = Sse2.PackUnsignedSaturate(rw, bw);
            Vector128<byte> gxa = Sse2.PackUnsignedSaturate(gw, c255);
            Vector128<byte> t0 = Sse2.UnpackLow(brb, gxa);
            Vector128<byte> t1 = Sse2.UnpackHigh(brb, gxa);

            Vector128<byte> c0 = Ssse3.Shuffle(Sse2.UnpackLow(t0.AsUInt16(), t1.AsUInt16()).AsByte(), rgbMask);
            Vector128<byte> c1 = Ssse3.Shuffle(Sse2.UnpackHigh(t0.AsUInt16(), t1.AsUInt16()).AsByte(), rgbMask);

            c0.CopyTo(dst.Slice(dstOff, 16));
            if (dstOff + 28 <= output.Length)
            {
                c1.CopyTo(dst.Slice(dstOff + 12, 16));
            }
            else
            {
                Store12Bytes(dst, dstOff + 12, c1);
            }
        }

        if (i < count)
        {
            YCbCrToRgbRowScalar(output.Slice(dstOff), y.Slice(i), cb.Slice(i), cr.Slice(i), count - i, 3);
        }
    }

    // -------------------------------------------------------------------------
    // Arm64 AdvSimd YCbCr -> RGBA (step=4) & RGB (step=3) (8 pixels / iteration)
    // -------------------------------------------------------------------------

    private static void YCbCrToRgbaRowAdvSimd(Span<byte> output, ReadOnlySpan<byte> y, ReadOnlySpan<byte> cb, ReadOnlySpan<byte> cr, int count)
    {
        if (count * 4 > output.Length || count > y.Length || count > cb.Length || count > cr.Length)
        {
            ThrowBounds();
        }

        Span<byte> dst = output;
        ReadOnlySpan<byte> yRef = y;
        ReadOnlySpan<byte> cbRef = cb;
        ReadOnlySpan<byte> crRef = cr;

        Vector128<short> c8 = Vector128.Create((short)8);
        Vector128<short> c128 = Vector128.Create((short)128);
        Vector128<short> cCrR = Vector128.Create(CrToR16);
        Vector128<short> cCrG = Vector128.Create(CrToG16);
        Vector128<short> cCbG = Vector128.Create(CbToG16);
        Vector128<short> cCbB = Vector128.Create(CbToB16);
        Vector128<byte> a8 = Vector128.Create((byte)255);

        int i = 0;
        for (; i + 8 <= count; i += 8)
        {
            Vector128<short> yw = AdvSimd.Add(AdvSimd.ShiftLeftLogical(AdvSimd.ZeroExtendWideningLower(Load8Bytes64(yRef, i)).AsInt16(), 4), c8);
            Vector128<short> cbw = AdvSimd.ShiftLeftLogical(AdvSimd.Subtract(AdvSimd.ZeroExtendWideningLower(Load8Bytes64(cbRef, i)).AsInt16(), c128), 7);
            Vector128<short> crw = AdvSimd.ShiftLeftLogical(AdvSimd.Subtract(AdvSimd.ZeroExtendWideningLower(Load8Bytes64(crRef, i)).AsInt16(), c128), 7);

            Vector128<short> crR = AdvSimd.MultiplyDoublingSaturateHigh(crw, cCrR);
            Vector128<short> crG = AdvSimd.MultiplyDoublingSaturateHigh(crw, cCrG);
            Vector128<short> cbG = AdvSimd.MultiplyDoublingSaturateHigh(cbw, cCbG);
            Vector128<short> cbB = AdvSimd.MultiplyDoublingSaturateHigh(cbw, cCbB);

            Vector64<byte> r8 = AdvSimd.ExtractNarrowingSaturateUnsignedLower(AdvSimd.ShiftRightArithmetic(AdvSimd.Add(yw, crR), 4));
            Vector64<byte> g8 = AdvSimd.ExtractNarrowingSaturateUnsignedLower(AdvSimd.ShiftRightArithmetic(AdvSimd.Add(yw, AdvSimd.Add(crG, cbG)), 4));
            Vector64<byte> b8 = AdvSimd.ExtractNarrowingSaturateUnsignedLower(AdvSimd.ShiftRightArithmetic(AdvSimd.Add(yw, cbB), 4));

            Vector128<byte> rg = AdvSimd.Arm64.ZipLow(r8.ToVector128Unsafe(), g8.ToVector128Unsafe());
            Vector128<byte> ba = AdvSimd.Arm64.ZipLow(b8.ToVector128Unsafe(), a8);
            Vector128<byte> out0 = AdvSimd.Arm64.ZipLow(rg.AsUInt16(), ba.AsUInt16()).AsByte();
            Vector128<byte> out1 = AdvSimd.Arm64.ZipHigh(rg.AsUInt16(), ba.AsUInt16()).AsByte();

            int dstOff = i * 4;
            out0.CopyTo(dst.Slice(dstOff, 16));
            out1.CopyTo(dst.Slice(dstOff + 16, 16));
        }

        if (i < count)
        {
            YCbCrToRgbRowScalar(output.Slice(i * 4), y.Slice(i), cb.Slice(i), cr.Slice(i), count - i, 4);
        }
    }

    private static void YCbCrToRgbRowAdvSimd(Span<byte> output, ReadOnlySpan<byte> y, ReadOnlySpan<byte> cb, ReadOnlySpan<byte> cr, int count)
    {
        if (count * 3 > output.Length || count > y.Length || count > cb.Length || count > cr.Length)
        {
            ThrowBounds();
        }

        Span<byte> dst = output;
        ReadOnlySpan<byte> yRef = y;
        ReadOnlySpan<byte> cbRef = cb;
        ReadOnlySpan<byte> crRef = cr;

        Vector128<short> c8 = Vector128.Create((short)8);
        Vector128<short> c128 = Vector128.Create((short)128);
        Vector128<short> cCrR = Vector128.Create(CrToR16);
        Vector128<short> cCrG = Vector128.Create(CrToG16);
        Vector128<short> cCbG = Vector128.Create(CbToG16);
        Vector128<short> cCbB = Vector128.Create(CbToB16);
        Vector128<byte> a8 = Vector128.Create((byte)255);
        Vector128<byte> rgbMask = Vector128.Create(
            (byte)0, 1, 2, 4, 5, 6, 8, 9, 10, 12, 13, 14, 0xFF, 0xFF, 0xFF, 0xFF);

        int i = 0;
        int dstOff = 0;
        for (; i + 8 <= count; i += 8, dstOff += 24)
        {
            Vector128<short> yw = AdvSimd.Add(AdvSimd.ShiftLeftLogical(AdvSimd.ZeroExtendWideningLower(Load8Bytes64(yRef, i)).AsInt16(), 4), c8);
            Vector128<short> cbw = AdvSimd.ShiftLeftLogical(AdvSimd.Subtract(AdvSimd.ZeroExtendWideningLower(Load8Bytes64(cbRef, i)).AsInt16(), c128), 7);
            Vector128<short> crw = AdvSimd.ShiftLeftLogical(AdvSimd.Subtract(AdvSimd.ZeroExtendWideningLower(Load8Bytes64(crRef, i)).AsInt16(), c128), 7);

            Vector128<short> crR = AdvSimd.MultiplyDoublingSaturateHigh(crw, cCrR);
            Vector128<short> crG = AdvSimd.MultiplyDoublingSaturateHigh(crw, cCrG);
            Vector128<short> cbG = AdvSimd.MultiplyDoublingSaturateHigh(cbw, cCbG);
            Vector128<short> cbB = AdvSimd.MultiplyDoublingSaturateHigh(cbw, cCbB);

            Vector64<byte> r8 = AdvSimd.ExtractNarrowingSaturateUnsignedLower(AdvSimd.ShiftRightArithmetic(AdvSimd.Add(yw, crR), 4));
            Vector64<byte> g8 = AdvSimd.ExtractNarrowingSaturateUnsignedLower(AdvSimd.ShiftRightArithmetic(AdvSimd.Add(yw, AdvSimd.Add(crG, cbG)), 4));
            Vector64<byte> b8 = AdvSimd.ExtractNarrowingSaturateUnsignedLower(AdvSimd.ShiftRightArithmetic(AdvSimd.Add(yw, cbB), 4));

            Vector128<byte> rg = AdvSimd.Arm64.ZipLow(r8.ToVector128Unsafe(), g8.ToVector128Unsafe());
            Vector128<byte> ba = AdvSimd.Arm64.ZipLow(b8.ToVector128Unsafe(), a8);
            Vector128<byte> c0 = AdvSimd.Arm64.VectorTableLookup(AdvSimd.Arm64.ZipLow(rg.AsUInt16(), ba.AsUInt16()).AsByte(), rgbMask);
            Vector128<byte> c1 = AdvSimd.Arm64.VectorTableLookup(AdvSimd.Arm64.ZipHigh(rg.AsUInt16(), ba.AsUInt16()).AsByte(), rgbMask);

            c0.CopyTo(dst.Slice(dstOff, 16));
            if (dstOff + 28 <= output.Length)
            {
                c1.CopyTo(dst.Slice(dstOff + 12, 16));
            }
            else
            {
                Store12Bytes(dst, dstOff + 12, c1);
            }
        }

        if (i < count)
        {
            YCbCrToRgbRowScalar(output.Slice(dstOff), y.Slice(i), cb.Slice(i), cr.Slice(i), count - i, 3);
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector128<byte> Load8Bytes(ReadOnlySpan<byte> src, int offset) =>
        Vector128.CreateScalar(BinaryPrimitives.ReadUInt64LittleEndian(src.Slice(offset, 8))).AsByte();

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector64<byte> Load8Bytes64(ReadOnlySpan<byte> src, int offset) =>
        Vector64.CreateScalar(BinaryPrimitives.ReadUInt64LittleEndian(src.Slice(offset, 8))).AsByte();

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void Store12Bytes(Span<byte> dst, int offset, Vector128<byte> v)
    {
        BinaryPrimitives.WriteUInt64LittleEndian(dst.Slice(offset, 8), v.AsUInt64().GetElement(0));
        BinaryPrimitives.WriteUInt32LittleEndian(dst.Slice(offset + 8, 4), v.AsUInt32().GetElement(2));
    }

    // -------------------------------------------------------------------------
    // Arm64 AdvSimd fused 4:2:0 path: h2v2 chroma upsampling + YCbCr -> RGB(A) in one pass.
    // Produces exactly the bytes of ResampleRowHv2 (Cb, Cr) followed by YCbCrToRgb(a)Row.
    // -------------------------------------------------------------------------

    internal static bool SupportsFused420 => AdvSimd.Arm64.IsSupported;

    private readonly struct YccConsts
    {
        public readonly Vector128<short> C8;
        public readonly Vector128<short> C128;
        public readonly Vector128<short> CrR;
        public readonly Vector128<short> CrG;
        public readonly Vector128<short> CbG;
        public readonly Vector128<short> CbB;
        public readonly Vector128<byte> A8;
        public readonly Vector128<byte> RgbMask;

        public YccConsts()
        {
            C8 = Vector128.Create((short)8);
            C128 = Vector128.Create((short)128);
            CrR = Vector128.Create(CrToR16);
            CrG = Vector128.Create(CrToG16);
            CbG = Vector128.Create(CbToG16);
            CbB = Vector128.Create(CbToB16);
            A8 = Vector128.Create((byte)255);
            RgbMask = Vector128.Create(
                (byte)0, 1, 2, 4, 5, 6, 8, 9, 10, 12, 13, 14, 0xFF, 0xFF, 0xFF, 0xFF);
        }
    }

    /// <summary>
    /// Converts one 4:2:0 row: Y is full width, Cb/Cr are half width (<paramref name="wLores"/> = ceil(width / 2)).
    /// </summary>
    internal static void ConvertYCbCr420RowFused(
        Span<byte> output,
        ReadOnlySpan<byte> y,
        ReadOnlySpan<byte> cbNear,
        ReadOnlySpan<byte> cbFar,
        ReadOnlySpan<byte> crNear,
        ReadOnlySpan<byte> crFar,
        int width,
        int wLores,
        int outN)
    {
        if (wLores < 16
            || width < (2 * wLores) - 1
            || width > 2 * wLores
            || outN < 3
            || outN > 4
            || y.Length < width
            || output.Length < width * outN
            || cbNear.Length < wLores
            || cbFar.Length < wLores
            || crNear.Length < wLores
            || crFar.Length < wLores)
        {
            ThrowBounds();
        }

        YccConsts k = new();

        // Pixels 0..1 use the exact edge formulas.
        Yuv420ScalarRange(output, y, cbNear, cbFar, crNear, crFar, wLores, 0, 2, outN);

        // Interior input columns i..i+7 produce output pixels 2i..2i+15; all of them are fully interior.
        int i = 1;
        for (; i + 7 <= wLores - 2; i += 8)
        {
            Vector128<byte> cb16 = HvUp16(cbNear, cbFar, i, k.C8);
            Vector128<byte> cr16 = HvUp16(crNear, crFar, i, k.C8);
            int p = 2 * i;
            YccHalfStore(output, p * outN, outN, k, Load8Bytes64(y, p), cb16.GetLower(), cr16.GetLower());
            YccHalfStore(output, (p + 8) * outN, outN, k, Load8Bytes64(y, p + 8), cb16.GetUpper(), cr16.GetUpper());
        }

        // Remaining pixels (at most 16, including the last edge pixel) use the scalar formulas.
        Yuv420ScalarRange(output, y, cbNear, cbFar, crNear, crFar, wLores, 2 * i, width, outN);
    }

    /// <summary>
    /// Upsamples 8 input columns starting at <paramref name="i"/> into 16 interleaved output chroma bytes.
    /// Requires 1 &lt;= i and i + 8 &lt;= wLores - 1.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector128<byte> HvUp16(ReadOnlySpan<byte> near, ReadOnlySpan<byte> far, int i, Vector128<short> c8)
    {
        Vector128<short> nPrev = AdvSimd.ZeroExtendWideningLower(Load8Bytes64(near, i - 1)).AsInt16();
        Vector128<short> fPrev = AdvSimd.ZeroExtendWideningLower(Load8Bytes64(far, i - 1)).AsInt16();
        Vector128<short> prev = AdvSimd.Add(AdvSimd.Add(AdvSimd.ShiftLeftLogical(nPrev, 1), nPrev), fPrev);

        Vector128<short> nCurr = AdvSimd.ZeroExtendWideningLower(Load8Bytes64(near, i)).AsInt16();
        Vector128<short> fCurr = AdvSimd.ZeroExtendWideningLower(Load8Bytes64(far, i)).AsInt16();
        Vector128<short> curr = AdvSimd.Add(AdvSimd.Add(AdvSimd.ShiftLeftLogical(nCurr, 1), nCurr), fCurr);

        Vector128<short> nNext = AdvSimd.ZeroExtendWideningLower(Load8Bytes64(near, i + 1)).AsInt16();
        Vector128<short> fNext = AdvSimd.ZeroExtendWideningLower(Load8Bytes64(far, i + 1)).AsInt16();
        Vector128<short> next = AdvSimd.Add(AdvSimd.Add(AdvSimd.ShiftLeftLogical(nNext, 1), nNext), fNext);

        Vector128<short> c3p8 = AdvSimd.Add(AdvSimd.Add(AdvSimd.ShiftLeftLogical(curr, 1), curr), c8);
        Vector64<byte> e8 = AdvSimd.ExtractNarrowingSaturateUnsignedLower(AdvSimd.ShiftRightArithmetic(AdvSimd.Add(c3p8, prev), 4));
        Vector64<byte> o8 = AdvSimd.ExtractNarrowingSaturateUnsignedLower(AdvSimd.ShiftRightArithmetic(AdvSimd.Add(c3p8, next), 4));
        return AdvSimd.Arm64.ZipLow(e8.ToVector128Unsafe(), o8.ToVector128Unsafe());
    }

    /// <summary>
    /// Converts 8 pixels (Y, Cb, Cr already expanded to 8 lanes) and stores them as RGBA or RGB at <paramref name="off"/>.
    /// Same arithmetic as <see cref="YCbCrToRgbaRowAdvSimd"/> / <see cref="YCbCrToRgbRowAdvSimd"/>.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void YccHalfStore(
        Span<byte> dst,
        int off,
        int outN,
        in YccConsts k,
        Vector64<byte> y8,
        Vector64<byte> cb8,
        Vector64<byte> cr8)
    {
        Vector128<short> yw = AdvSimd.Add(AdvSimd.ShiftLeftLogical(AdvSimd.ZeroExtendWideningLower(y8).AsInt16(), 4), k.C8);
        Vector128<short> cbw = AdvSimd.ShiftLeftLogical(AdvSimd.Subtract(AdvSimd.ZeroExtendWideningLower(cb8).AsInt16(), k.C128), 7);
        Vector128<short> crw = AdvSimd.ShiftLeftLogical(AdvSimd.Subtract(AdvSimd.ZeroExtendWideningLower(cr8).AsInt16(), k.C128), 7);

        Vector128<short> crR = AdvSimd.MultiplyDoublingSaturateHigh(crw, k.CrR);
        Vector128<short> crG = AdvSimd.MultiplyDoublingSaturateHigh(crw, k.CrG);
        Vector128<short> cbG = AdvSimd.MultiplyDoublingSaturateHigh(cbw, k.CbG);
        Vector128<short> cbB = AdvSimd.MultiplyDoublingSaturateHigh(cbw, k.CbB);

        Vector64<byte> r8 = AdvSimd.ExtractNarrowingSaturateUnsignedLower(AdvSimd.ShiftRightArithmetic(AdvSimd.Add(yw, crR), 4));
        Vector64<byte> g8 = AdvSimd.ExtractNarrowingSaturateUnsignedLower(AdvSimd.ShiftRightArithmetic(AdvSimd.Add(yw, AdvSimd.Add(crG, cbG)), 4));
        Vector64<byte> b8 = AdvSimd.ExtractNarrowingSaturateUnsignedLower(AdvSimd.ShiftRightArithmetic(AdvSimd.Add(yw, cbB), 4));

        Vector128<byte> rg = AdvSimd.Arm64.ZipLow(r8.ToVector128Unsafe(), g8.ToVector128Unsafe());
        Vector128<byte> ba = AdvSimd.Arm64.ZipLow(b8.ToVector128Unsafe(), k.A8);
        Vector128<byte> rgba0 = AdvSimd.Arm64.ZipLow(rg.AsUInt16(), ba.AsUInt16()).AsByte();
        Vector128<byte> rgba1 = AdvSimd.Arm64.ZipHigh(rg.AsUInt16(), ba.AsUInt16()).AsByte();

        if (outN == 4)
        {
            rgba0.CopyTo(dst.Slice(off, 16));
            rgba1.CopyTo(dst.Slice(off + 16, 16));
            return;
        }

        Vector128<byte> c0 = AdvSimd.Arm64.VectorTableLookup(rgba0, k.RgbMask);
        Vector128<byte> c1 = AdvSimd.Arm64.VectorTableLookup(rgba1, k.RgbMask);
        c0.CopyTo(dst.Slice(off, 16));
        if (off + 28 <= dst.Length)
        {
            c1.CopyTo(dst.Slice(off + 12, 16));
        }
        else
        {
            Store12Bytes(dst, off + 12, c1);
        }
    }

    /// <summary>
    /// Scalar fused conversion for output pixels [<paramref name="start"/>, <paramref name="end"/>).
    /// </summary>
    private static void Yuv420ScalarRange(
        Span<byte> output,
        ReadOnlySpan<byte> y,
        ReadOnlySpan<byte> cbNear,
        ReadOnlySpan<byte> cbFar,
        ReadOnlySpan<byte> crNear,
        ReadOnlySpan<byte> crFar,
        int wLores,
        int start,
        int end,
        int outN)
    {
        int count = end - start;
        if (count <= 0)
        {
            return;
        }

        Span<byte> cb = stackalloc byte[count];
        Span<byte> cr = stackalloc byte[count];
        for (int n = 0; n < count; n++)
        {
            int x = start + n;
            cb[n] = HvUpScalar(cbNear, cbFar, x, wLores);
            cr[n] = HvUpScalar(crNear, crFar, x, wLores);
        }

        YCbCrToRgbRowScalar(output.Slice(start * outN), y.Slice(start, count), cb, cr, count, outN);
    }

    /// <summary>
    /// One output chroma sample of the h2v2 "triangle" filter, using the exact edge rules of ResampleRowHv2.
    /// </summary>
    private static byte HvUpScalar(ReadOnlySpan<byte> near, ReadOnlySpan<byte> far, int x, int w)
    {
        if (x == 0)
        {
            return (byte)((((3 * near[0]) + far[0]) + 2) >> 2);
        }

        if (x == (2 * w) - 1)
        {
            return (byte)((((3 * near[w - 1]) + far[w - 1]) + 2) >> 2);
        }

        int i = x >> 1;
        int cur = (3 * near[i]) + far[i];
        if ((x & 1) == 0)
        {
            int prev = (3 * near[i - 1]) + far[i - 1];
            return (byte)(((3 * cur) + prev + 8) >> 4);
        }

        int next = (3 * near[i + 1]) + far[i + 1];
        return (byte)(((3 * cur) + next + 8) >> 4);
    }

    private static void ThrowBounds() =>
        throw new StbException(StbErrorCode.InternalError, "Color conversion buffer bounds check failed.");
#endif
}
