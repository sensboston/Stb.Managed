using System;
using System.Buffers.Binary;
using System.Runtime.CompilerServices;
#if NET8_0_OR_GREATER
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.Arm;
using System.Runtime.Intrinsics.X86;
#endif
using Stb.Managed.Internal;

namespace Stb.Managed.ImageWrite.Internal;

internal static class HdrEncoder
{
    private static ReadOnlySpan<byte> HeaderPrefix =>
        "#?RADIANCE\n# Written by stb_image_write.h\nFORMAT=32-bit_rle_rgbe\nEXPOSURE=          1.0000000000000\n\n-Y "u8;

    private static ReadOnlySpan<byte> HeaderMid => " +X "u8;

    internal static void Encode(
        ref ByteSink sink,
        ReadOnlySpan<float> pixels,
        int width,
        int height,
        int comp,
        int strideFloats,
        bool flipVertically)
    {
        WriteHeader(ref sink, width, height);
        if (sink.Overflowed)
        {
            return;
        }

        bool useRle = width >= 8 && width < 32768;
        int rowFloats = width * comp;

        if (!useRle)
        {
            for (int rowIdx = 0; rowIdx < height; rowIdx++)
            {
                int srcRow = flipVertically ? height - 1 - rowIdx : rowIdx;
                ReadOnlySpan<float> scanline = pixels.Slice(srcRow * strideFloats, rowFloats);
                Span<byte> dst = sink.Reserve(width * 4);
                if (sink.Overflowed)
                {
                    return;
                }

                for (int x = 0; x < width; x++)
                {
                    LinearToRgbe(scanline.Slice(x * comp, comp), comp, out byte r, out byte g, out byte b, out byte e);
                    int off = x * 4;
                    dst[off] = r;
                    dst[off + 1] = g;
                    dst[off + 2] = b;
                    dst[off + 3] = e;
                }
            }

            return;
        }

        using PooledBuffer<byte> scratchBuf = new(width * 4);
        Span<byte> scratch = scratchBuf.Span;
        byte wHi = (byte)((width >> 8) & 0xFF);
        byte wLo = (byte)(width & 0xFF);

        for (int rowIdx = 0; rowIdx < height; rowIdx++)
        {
            int srcRow = flipVertically ? height - 1 - rowIdx : rowIdx;
            ReadOnlySpan<float> scanline = pixels.Slice(srcRow * strideFloats, rowFloats);

            Span<byte> planeR = scratch.Slice(0, width);
            Span<byte> planeG = scratch.Slice(width, width);
            Span<byte> planeB = scratch.Slice(width * 2, width);
            Span<byte> planeE = scratch.Slice(width * 3, width);

            ConvertScanlineToRgbePlanar(scanline, width, comp, planeR, planeG, planeB, planeE);

            sink.Write4(2, 2, wHi, wLo);
            if (sink.Overflowed)
            {
                return;
            }

            WriteRlePlane(ref sink, planeR);
            WriteRlePlane(ref sink, planeG);
            WriteRlePlane(ref sink, planeB);
            WriteRlePlane(ref sink, planeE);
            if (sink.Overflowed)
            {
                return;
            }
        }
    }

    internal static void ConvertScanlineToRgbePlanar(
        ReadOnlySpan<float> scanline,
        int width,
        int comp,
        Span<byte> planeR,
        Span<byte> planeG,
        Span<byte> planeB,
        Span<byte> planeE)
    {
        int x = 0;
#if NET8_0_OR_GREATER
        if (Avx2.IsSupported && width >= 8)
        {
            Vector256<float> vFltMax = Vector256.Create(float.MaxValue);
            Vector256<float> vThresh = Vector256.Create(1e-32f);
            Vector256<float> vZero = Vector256<float>.Zero;
            Vector256<int> v261 = Vector256.Create(261);
            Vector256<int> vTwo = Vector256.Create(2);
            Vector256<int> vMask255 = Vector256.Create(0xFF);

            int limit = width - 7;
            for (; x < limit; x += 8)
            {
                LoadRgb8X86(scanline.Slice(x * comp), comp, out Vector256<float> r, out Vector256<float> g, out Vector256<float> b);

                // Check all 8 lanes for NaN (r == r && g == g && b == b).
                Vector256<float> notNan = Avx.And(
                    Avx.And(
                        Avx.Compare(r, r, FloatComparisonMode.OrderedEqualNonSignaling),
                        Avx.Compare(g, g, FloatComparisonMode.OrderedEqualNonSignaling)),
                    Avx.Compare(b, b, FloatComparisonMode.OrderedEqualNonSignaling));

                if (Avx.MoveMask(notNan) != 0xFF)
                {
                    ConvertScanlineRangeScalar(scanline, x, 8, comp, planeR, planeG, planeB, planeE);
                    continue;
                }

                r = Avx.Min(r, vFltMax);
                g = Avx.Min(g, vFltMax);
                b = Avx.Min(b, vFltMax);

                Vector256<float> maxComp = Avx.Max(Avx.Max(r, g), b);
                Vector256<int> activeMask = Avx.Compare(maxComp, vThresh, FloatComparisonMode.OrderedGreaterThanNonSignaling).AsInt32();
                Vector256<float> safeMax = Avx.Max(maxComp, vThresh);

                Vector256<int> biasedExp = Avx2.And(Avx2.ShiftRightLogical(safeMax.AsInt32(), 23), vMask255);
                Vector256<float> norm = Avx2.ShiftLeftLogical(Avx2.Subtract(v261, biasedExp), 23).AsSingle();

                Vector256<int> rInt = Avx2.And(Avx.ConvertToVector256Int32WithTruncation(Avx.Multiply(Avx.Max(r, vZero), norm)), activeMask);
                Vector256<int> gInt = Avx2.And(Avx.ConvertToVector256Int32WithTruncation(Avx.Multiply(Avx.Max(g, vZero), norm)), activeMask);
                Vector256<int> bInt = Avx2.And(Avx.ConvertToVector256Int32WithTruncation(Avx.Multiply(Avx.Max(b, vZero), norm)), activeMask);
                Vector256<int> eInt = Avx2.And(Avx2.And(Avx2.Add(biasedExp, vTwo), vMask255), activeMask);

                Store8BytesFromInt32Avx2(rInt, planeR.Slice(x, 8));
                Store8BytesFromInt32Avx2(gInt, planeG.Slice(x, 8));
                Store8BytesFromInt32Avx2(bInt, planeB.Slice(x, 8));
                Store8BytesFromInt32Avx2(eInt, planeE.Slice(x, 8));
            }
        }

        if (Sse2.IsSupported && width - x >= 4)
        {
            Vector128<float> vFltMax = Vector128.Create(float.MaxValue);
            Vector128<float> vThresh = Vector128.Create(1e-32f);
            Vector128<float> vZero = Vector128<float>.Zero;
            Vector128<int> v261 = Vector128.Create(261);
            Vector128<int> vTwo = Vector128.Create(2);
            Vector128<int> vMask255 = Vector128.Create(0xFF);

            int limit = width - 3;
            for (; x < limit; x += 4)
            {
                LoadRgb4X86(scanline.Slice(x * comp), comp, out Vector128<float> r, out Vector128<float> g, out Vector128<float> b);

                Vector128<float> notNan = Sse.And(
                    Sse.And(
                        Sse.CompareEqual(r, r),
                        Sse.CompareEqual(g, g)),
                    Sse.CompareEqual(b, b));

                if (Sse.MoveMask(notNan) != 0xF)
                {
                    ConvertScanlineRangeScalar(scanline, x, 4, comp, planeR, planeG, planeB, planeE);
                    continue;
                }

                r = Sse.Min(r, vFltMax);
                g = Sse.Min(g, vFltMax);
                b = Sse.Min(b, vFltMax);

                Vector128<float> maxComp = Sse.Max(Sse.Max(r, g), b);
                Vector128<int> activeMask = Sse.CompareGreaterThan(maxComp, vThresh).AsInt32();
                Vector128<float> safeMax = Sse.Max(maxComp, vThresh);

                Vector128<int> biasedExp = Sse2.And(Sse2.ShiftRightLogical(safeMax.AsInt32(), 23), vMask255);
                Vector128<float> norm = Sse2.ShiftLeftLogical(Sse2.Subtract(v261, biasedExp), 23).AsSingle();

                Vector128<int> rInt = Sse2.And(Sse2.ConvertToVector128Int32WithTruncation(Sse.Multiply(Sse.Max(r, vZero), norm)), activeMask);
                Vector128<int> gInt = Sse2.And(Sse2.ConvertToVector128Int32WithTruncation(Sse.Multiply(Sse.Max(g, vZero), norm)), activeMask);
                Vector128<int> bInt = Sse2.And(Sse2.ConvertToVector128Int32WithTruncation(Sse.Multiply(Sse.Max(b, vZero), norm)), activeMask);
                Vector128<int> eInt = Sse2.And(Sse2.And(Sse2.Add(biasedExp, vTwo), vMask255), activeMask);

                Store4BytesFromInt32Sse2(rInt, planeR.Slice(x, 4));
                Store4BytesFromInt32Sse2(gInt, planeG.Slice(x, 4));
                Store4BytesFromInt32Sse2(bInt, planeB.Slice(x, 4));
                Store4BytesFromInt32Sse2(eInt, planeE.Slice(x, 4));
            }
        }
        else if (AdvSimd.Arm64.IsSupported && width - x >= 4)
        {
            Vector128<float> vFltMax = Vector128.Create(float.MaxValue);
            Vector128<float> vThresh = Vector128.Create(1e-32f);
            Vector128<float> vZero = Vector128<float>.Zero;
            Vector128<int> v261 = Vector128.Create(261);
            Vector128<int> vTwo = Vector128.Create(2);
            Vector128<int> vMask255 = Vector128.Create(0xFF);

            int limit = width - 3;
            for (; x < limit; x += 4)
            {
                LoadRgb4Arm64(scanline.Slice(x * comp), comp, out Vector128<float> r, out Vector128<float> g, out Vector128<float> b);

                Vector128<uint> notNan = AdvSimd.And(
                    AdvSimd.And(
                        AdvSimd.CompareEqual(r, r).AsUInt32(),
                        AdvSimd.CompareEqual(g, g).AsUInt32()),
                    AdvSimd.CompareEqual(b, b).AsUInt32());

                if (AdvSimd.Arm64.MinAcross(notNan).ToScalar() == 0u)
                {
                    ConvertScanlineRangeScalar(scanline, x, 4, comp, planeR, planeG, planeB, planeE);
                    continue;
                }

                r = AdvSimd.Min(r, vFltMax);
                g = AdvSimd.Min(g, vFltMax);
                b = AdvSimd.Min(b, vFltMax);

                Vector128<float> maxComp = AdvSimd.Max(AdvSimd.Max(r, g), b);
                Vector128<int> activeMask = AdvSimd.CompareGreaterThan(maxComp, vThresh).AsInt32();
                Vector128<float> safeMax = AdvSimd.Max(maxComp, vThresh);

                Vector128<int> biasedExp = AdvSimd.And(AdvSimd.ShiftRightLogical(safeMax.AsUInt32(), 23).AsInt32(), vMask255);
                Vector128<float> norm = AdvSimd.ShiftLeftLogical(AdvSimd.Subtract(v261, biasedExp).AsUInt32(), 23).AsSingle();

                Vector128<int> rInt = AdvSimd.And(AdvSimd.ConvertToInt32RoundToZero(AdvSimd.Multiply(AdvSimd.Max(r, vZero), norm)), activeMask);
                Vector128<int> gInt = AdvSimd.And(AdvSimd.ConvertToInt32RoundToZero(AdvSimd.Multiply(AdvSimd.Max(g, vZero), norm)), activeMask);
                Vector128<int> bInt = AdvSimd.And(AdvSimd.ConvertToInt32RoundToZero(AdvSimd.Multiply(AdvSimd.Max(b, vZero), norm)), activeMask);
                Vector128<int> eInt = AdvSimd.And(AdvSimd.And(AdvSimd.Add(biasedExp, vTwo), vMask255), activeMask);

                Store4BytesFromInt32Arm64(rInt, planeR.Slice(x, 4));
                Store4BytesFromInt32Arm64(gInt, planeG.Slice(x, 4));
                Store4BytesFromInt32Arm64(bInt, planeB.Slice(x, 4));
                Store4BytesFromInt32Arm64(eInt, planeE.Slice(x, 4));
            }
        }
#endif
        ConvertScanlineRangeScalar(scanline, x, width - x, comp, planeR, planeG, planeB, planeE);
    }

    internal static void ConvertScanlineToRgbePlanarScalar(
        ReadOnlySpan<float> scanline,
        int width,
        int comp,
        Span<byte> planeR,
        Span<byte> planeG,
        Span<byte> planeB,
        Span<byte> planeE)
    {
        ConvertScanlineRangeScalar(scanline, 0, width, comp, planeR, planeG, planeB, planeE);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void ConvertScanlineRangeScalar(
        ReadOnlySpan<float> scanline,
        int startX,
        int count,
        int comp,
        Span<byte> planeR,
        Span<byte> planeG,
        Span<byte> planeB,
        Span<byte> planeE)
    {
        int endX = startX + count;
        for (int x = startX; x < endX; x++)
        {
            LinearToRgbe(scanline.Slice(x * comp, comp), comp, out byte r, out byte g, out byte b, out byte e);
            planeR[x] = r;
            planeG[x] = g;
            planeB[x] = b;
            planeE[x] = e;
        }
    }

#if NET8_0_OR_GREATER
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void LoadRgb4X86(
        ReadOnlySpan<float> src,
        int comp,
        out Vector128<float> r,
        out Vector128<float> g,
        out Vector128<float> b)
    {
        if (comp == 3)
        {
            Vector128<float> v0 = Vector128.Create(src.Slice(0, 4));
            Vector128<float> v1 = Vector128.Create(src.Slice(4, 4));
            Vector128<float> v2 = Vector128.Create(src.Slice(8, 4));

            Vector128<float> tR = Sse.Shuffle(v1, v2, 0x5A);
            r = Sse.Shuffle(v0, tR, 0x8C);

            Vector128<float> tG1 = Sse.Shuffle(v0, v1, 0x05);
            Vector128<float> tG2 = Sse.Shuffle(v1, v2, 0xAF);
            g = Sse.Shuffle(tG1, tG2, 0x88);

            Vector128<float> tB = Sse.Shuffle(v0, v1, 0x5A);
            b = Sse.Shuffle(tB, v2, 0xC8);
        }
        else if (comp == 4)
        {
            Vector128<float> v0 = Vector128.Create(src.Slice(0, 4));
            Vector128<float> v1 = Vector128.Create(src.Slice(4, 4));
            Vector128<float> v2 = Vector128.Create(src.Slice(8, 4));
            Vector128<float> v3 = Vector128.Create(src.Slice(12, 4));

            Vector128<float> t0 = Sse.UnpackLow(v0, v1);
            Vector128<float> t1 = Sse.UnpackHigh(v0, v1);
            Vector128<float> t2 = Sse.UnpackLow(v2, v3);
            Vector128<float> t3 = Sse.UnpackHigh(v2, v3);

            r = Sse.Shuffle(t0, t2, 0x44);
            g = Sse.Shuffle(t0, t2, 0xEE);
            b = Sse.Shuffle(t1, t3, 0x44);
        }
        else if (comp == 2)
        {
            Vector128<float> v0 = Vector128.Create(src.Slice(0, 4));
            Vector128<float> v1 = Vector128.Create(src.Slice(4, 4));
            r = g = b = Sse.Shuffle(v0, v1, 0x88);
        }
        else
        {
            r = g = b = Vector128.Create(src.Slice(0, 4));
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void LoadRgb8X86(
        ReadOnlySpan<float> src,
        int comp,
        out Vector256<float> r,
        out Vector256<float> g,
        out Vector256<float> b)
    {
        if (comp == 1)
        {
            r = g = b = Vector256.Create(src.Slice(0, 8));
            return;
        }

        LoadRgb4X86(src, comp, out Vector128<float> rLo, out Vector128<float> gLo, out Vector128<float> bLo);
        LoadRgb4X86(src.Slice(4 * comp), comp, out Vector128<float> rHi, out Vector128<float> gHi, out Vector128<float> bHi);
        r = Vector256.Create(rLo, rHi);
        g = Vector256.Create(gLo, gHi);
        b = Vector256.Create(bLo, bHi);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void LoadRgb4Arm64(
        ReadOnlySpan<float> src,
        int comp,
        out Vector128<float> r,
        out Vector128<float> g,
        out Vector128<float> b)
    {
        if (comp == 3)
        {
            r = Vector128.Create(src[0], src[3], src[6], src[9]);
            g = Vector128.Create(src[1], src[4], src[7], src[10]);
            b = Vector128.Create(src[2], src[5], src[8], src[11]);
        }
        else if (comp == 4)
        {
            Vector128<float> v0 = Vector128.Create(src.Slice(0, 4));
            Vector128<float> v1 = Vector128.Create(src.Slice(4, 4));
            Vector128<float> v2 = Vector128.Create(src.Slice(8, 4));
            Vector128<float> v3 = Vector128.Create(src.Slice(12, 4));

            Vector128<ulong> t0 = AdvSimd.Arm64.ZipLow(v0, v1).AsUInt64();
            Vector128<ulong> t1 = AdvSimd.Arm64.ZipHigh(v0, v1).AsUInt64();
            Vector128<ulong> t2 = AdvSimd.Arm64.ZipLow(v2, v3).AsUInt64();
            Vector128<ulong> t3 = AdvSimd.Arm64.ZipHigh(v2, v3).AsUInt64();

            r = AdvSimd.Arm64.ZipLow(t0, t2).AsSingle();
            g = AdvSimd.Arm64.ZipHigh(t0, t2).AsSingle();
            b = AdvSimd.Arm64.ZipLow(t1, t3).AsSingle();
        }
        else if (comp == 2)
        {
            Vector128<float> v0 = Vector128.Create(src.Slice(0, 4));
            Vector128<float> v1 = Vector128.Create(src.Slice(4, 4));
            r = g = b = AdvSimd.Arm64.UnzipEven(v0, v1);
        }
        else
        {
            r = g = b = Vector128.Create(src.Slice(0, 4));
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void Store8BytesFromInt32Avx2(Vector256<int> v, Span<byte> dst)
    {
        Vector128<short> p16 = Sse2.PackSignedSaturate(v.GetLower(), v.GetUpper());
        Vector128<byte> p8 = Sse2.PackUnsignedSaturate(p16, p16);
        BinaryPrimitives.WriteUInt64LittleEndian(dst, p8.AsUInt64().ToScalar());
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void Store4BytesFromInt32Sse2(Vector128<int> v, Span<byte> dst)
    {
        Vector128<short> p16 = Sse2.PackSignedSaturate(v, v);
        Vector128<byte> p8 = Sse2.PackUnsignedSaturate(p16, p16);
        BinaryPrimitives.WriteUInt32LittleEndian(dst, p8.AsUInt32().ToScalar());
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void Store4BytesFromInt32Arm64(Vector128<int> v, Span<byte> dst)
    {
        Vector64<ushort> p16 = AdvSimd.ExtractNarrowingLower(v.AsUInt32());
        Vector64<byte> p8 = AdvSimd.ExtractNarrowingLower(Vector128.Create(p16, p16));
        BinaryPrimitives.WriteUInt32LittleEndian(dst, p8.AsUInt32().ToScalar());
    }
#endif

    private static void WriteHeader(ref ByteSink sink, int width, int height)
    {
        sink.WriteBytes(HeaderPrefix);
        WriteUInt32Ascii(ref sink, (uint)height);
        sink.WriteBytes(HeaderMid);
        WriteUInt32Ascii(ref sink, (uint)width);
        sink.WriteByte((byte)'\n');
    }

    private static void WriteUInt32Ascii(ref ByteSink sink, uint value)
    {
        Span<byte> digits = stackalloc byte[10];
        int pos = digits.Length;
        do
        {
            digits[--pos] = (byte)('0' + (value % 10u));
            value /= 10u;
        }
        while (value != 0u);

        sink.WriteBytes(digits.Slice(pos));
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static void LinearToRgbe(
        ReadOnlySpan<float> pixel,
        int ncomp,
        out byte outR,
        out byte outG,
        out byte outB,
        out byte outE)
    {
        float r;
        float g;
        float b;
        float maxcomp;

        if (ncomp >= 3)
        {
            r = pixel[0];
            g = pixel[1];
            b = pixel[2];
            maxcomp = r > g ? r : g;
            maxcomp = maxcomp > b ? maxcomp : b;
        }
        else
        {
            r = g = b = maxcomp = pixel[0];
        }

        if (float.IsNaN(maxcomp))
        {
            outR = 0;
            outG = 0;
            outB = 0;
            outE = 0;
            return;
        }

        if (maxcomp >= float.MaxValue)
        {
            r = r < float.MaxValue ? r : float.MaxValue;
            g = g < float.MaxValue ? g : float.MaxValue;
            b = b < float.MaxValue ? b : float.MaxValue;
            maxcomp = float.MaxValue;
        }

        if (maxcomp <= 1e-32f)
        {
            outR = 0;
            outG = 0;
            outB = 0;
            outE = 0;
            return;
        }

        int biasedExp = (BitConverter.SingleToInt32Bits(maxcomp) >> 23) & 0xFF;
        float normalize = BitConverter.Int32BitsToSingle((261 - biasedExp) << 23);

        outR = r > 0f ? (byte)(int)(r * normalize) : (byte)0;
        outG = g > 0f ? (byte)(int)(g * normalize) : (byte)0;
        outB = b > 0f ? (byte)(int)(b * normalize) : (byte)0;
        outE = unchecked((byte)(biasedExp + 2));
    }

    internal static void WriteRlePlane(ref ByteSink sink, ReadOnlySpan<byte> comp)
    {
        int width = comp.Length;
        int limit = width - 2;
        int x = 0;

        while (x < width)
        {
            int r = x;
            while (r < limit)
            {
                byte v = comp[r];
                if (v == comp[r + 1] && v == comp[r + 2])
                {
                    break;
                }

                r++;
            }

            if (r >= limit)
            {
                r = width;
            }

            while (x < r)
            {
                int len = r - x;
                if (len > 128)
                {
                    len = 128;
                }

                Span<byte> dst = sink.Reserve(1 + len);
                if (sink.Overflowed)
                {
                    return;
                }

                dst[0] = (byte)len;
                comp.Slice(x, len).CopyTo(dst.Slice(1));
                x += len;
            }

            if (r < limit)
            {
                byte runVal = comp[x];
#if NET8_0_OR_GREATER
                int relEnd = comp.Slice(r + 3).IndexOfAnyExcept(runVal);
                r = relEnd < 0 ? width : r + 3 + relEnd;
#else
                r += 3;
                while (r < width && comp[r] == runVal)
                {
                    r++;
                }
#endif
                while (x < r)
                {
                    int len = r - x;
                    if (len > 127)
                    {
                        len = 127;
                    }

                    sink.Write2((byte)(len + 128), runVal);
                    if (sink.Overflowed)
                    {
                        return;
                    }

                    x += len;
                }
            }
        }
    }

    internal static void WriteRlePlaneScalar(ref ByteSink sink, ReadOnlySpan<byte> comp)
    {
        int width = comp.Length;
        int x = 0;
        while (x < width)
        {
            int r = x;
            while (r + 2 < width)
            {
                byte v = comp[r];
                if (v == comp[r + 1] && v == comp[r + 2])
                {
                    break;
                }

                r++;
            }

            if (r + 2 >= width)
            {
                r = width;
            }

            while (x < r)
            {
                int len = r - x;
                if (len > 128)
                {
                    len = 128;
                }

                sink.WriteByte((byte)len);
                sink.WriteBytes(comp.Slice(x, len));
                x += len;
            }

            if (r + 2 < width)
            {
                byte runVal = comp[x];
                while (r < width && comp[r] == runVal)
                {
                    r++;
                }

                while (x < r)
                {
                    int len = r - x;
                    if (len > 127)
                    {
                        len = 127;
                    }

                    sink.Write2((byte)(len + 128), runVal);
                    x += len;
                }
            }
        }
    }
}
