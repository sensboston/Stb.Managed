using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
#if NET8_0_OR_GREATER
using System.Buffers.Binary;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.Arm;
using System.Runtime.Intrinsics.X86;
#endif

namespace Stb.Managed.Image.Internal;

/// <summary>
/// PNG scanline filter modes (including synthetic <see cref="AvgFirst"/> for the first row).
/// </summary>
internal enum PngFilterType : byte
{
    None = 0,
    Sub = 1,
    Up = 2,
    Average = 3,
    Paeth = 4,
    AvgFirst = 5,
}

/// <summary>
/// PNG scanline unfiltering routines.
/// <see cref="UnfilterScanlineScalar"/> is the permanent reference path tested against all SIMD paths.
/// </summary>
internal static class PngUnfilter
{
    private static ReadOnlySpan<byte> FirstRowFilterMap =>
    [
        (byte)PngFilterType.None,
        (byte)PngFilterType.Sub,
        (byte)PngFilterType.None,
        (byte)PngFilterType.AvgFirst,
        (byte)PngFilterType.Sub,
    ];

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static PngFilterType MapFirstRowFilter(byte rawFilter) =>
        (PngFilterType)FirstRowFilterMap[rawFilter];

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static int PaethPredictor(int a, int b, int c)
    {
        // Branch-free formulation from stb_image.h (equivalent to RFC 2083 PaethPredictor).
        int thresh = (c * 3) - (a + b);
        int lo = a < b ? a : b;
        int hi = a < b ? b : a;
        int t0 = hi <= thresh ? lo : c;
        return thresh <= lo ? hi : t0;
    }

    internal static void UnfilterScanline(
        PngFilterType filter,
        ReadOnlySpan<byte> raw,
        ReadOnlySpan<byte> prior,
        Span<byte> cur,
        int filterBytes)
    {
        switch (filter)
        {
            case PngFilterType.None:
                raw.Slice(0, cur.Length).CopyTo(cur);
                break;

            case PngFilterType.Sub:
                UnfilterSub(raw, cur, filterBytes);
                break;

            case PngFilterType.Up:
                UnfilterUp(raw, prior, cur);
                break;

            case PngFilterType.Average:
                UnfilterAverage(raw, prior, cur, filterBytes);
                break;

            case PngFilterType.Paeth:
                UnfilterPaeth(raw, prior, cur, filterBytes);
                break;

            case PngFilterType.AvgFirst:
                UnfilterAvgFirst(raw, cur, filterBytes);
                break;

            default:
                throw new StbException(
                    StbErrorCode.InvalidData,
                    $"Invalid PNG scanline filter type {(int)filter}.");
        }
    }

    internal static void UnfilterScanlineScalar(
        PngFilterType filter,
        ReadOnlySpan<byte> raw,
        ReadOnlySpan<byte> prior,
        Span<byte> cur,
        int filterBytes)
    {
        int length = cur.Length;
        switch (filter)
        {
            case PngFilterType.None:
                raw.Slice(0, length).CopyTo(cur);
                break;

            case PngFilterType.Sub:
                raw.Slice(0, filterBytes).CopyTo(cur);
                for (int k = filterBytes; k < length; k++)
                {
                    cur[k] = (byte)(raw[k] + cur[k - filterBytes]);
                }
                break;

            case PngFilterType.Up:
                for (int k = 0; k < length; k++)
                {
                    cur[k] = (byte)(raw[k] + prior[k]);
                }
                break;

            case PngFilterType.Average:
                for (int k = 0; k < filterBytes; k++)
                {
                    cur[k] = (byte)(raw[k] + (prior[k] >> 1));
                }

                for (int k = filterBytes; k < length; k++)
                {
                    cur[k] = (byte)(raw[k] + ((prior[k] + cur[k - filterBytes]) >> 1));
                }
                break;

            case PngFilterType.Paeth:
                for (int k = 0; k < filterBytes; k++)
                {
                    cur[k] = (byte)(raw[k] + prior[k]);
                }

                for (int k = filterBytes; k < length; k++)
                {
                    cur[k] = (byte)(raw[k] + PaethPredictor(cur[k - filterBytes], prior[k], prior[k - filterBytes]));
                }
                break;

            case PngFilterType.AvgFirst:
                raw.Slice(0, filterBytes).CopyTo(cur);
                for (int k = filterBytes; k < length; k++)
                {
                    cur[k] = (byte)(raw[k] + (cur[k - filterBytes] >> 1));
                }
                break;

            default:
                throw new StbException(
                    StbErrorCode.InvalidData,
                    $"Invalid PNG scanline filter type {(int)filter}.");
        }
    }

    private static void UnfilterUp(ReadOnlySpan<byte> raw, ReadOnlySpan<byte> prior, Span<byte> cur)
    {
        int length = cur.Length;
        int k = 0;

#if NET8_0_OR_GREATER
        if (Vector512.IsHardwareAccelerated && length >= Vector512<byte>.Count)
        {
            int limit = length - Vector512<byte>.Count;
            for (; k <= limit; k += Vector512<byte>.Count)
            {
                Vector512<byte> r = Vector512.Create(raw.Slice(k, Vector512<byte>.Count));
                Vector512<byte> p = Vector512.Create(prior.Slice(k, Vector512<byte>.Count));
                (r + p).CopyTo(cur.Slice(k, Vector512<byte>.Count));
            }
        }

        if (Vector256.IsHardwareAccelerated && k + Vector256<byte>.Count <= length)
        {
            int limit = length - Vector256<byte>.Count;
            for (; k <= limit; k += Vector256<byte>.Count)
            {
                Vector256<byte> r = Vector256.Create(raw.Slice(k, Vector256<byte>.Count));
                Vector256<byte> p = Vector256.Create(prior.Slice(k, Vector256<byte>.Count));
                (r + p).CopyTo(cur.Slice(k, Vector256<byte>.Count));
            }
        }

        if (Vector128.IsHardwareAccelerated && k + Vector128<byte>.Count <= length)
        {
            int limit = length - Vector128<byte>.Count;
            for (; k <= limit; k += Vector128<byte>.Count)
            {
                Vector128<byte> r = Vector128.Create(raw.Slice(k, Vector128<byte>.Count));
                Vector128<byte> p = Vector128.Create(prior.Slice(k, Vector128<byte>.Count));
                (r + p).CopyTo(cur.Slice(k, Vector128<byte>.Count));
            }
        }
#else
        if (length >= 8)
        {
            const ulong LowMask = 0x7F7F7F7F7F7F7F7FUL;
            const ulong HighMask = 0x8080808080808080UL;
            ReadOnlySpan<ulong> raw64 = MemoryMarshal.Cast<byte, ulong>(raw.Slice(0, length));
            ReadOnlySpan<ulong> prior64 = MemoryMarshal.Cast<byte, ulong>(prior.Slice(0, length));
            Span<ulong> cur64 = MemoryMarshal.Cast<byte, ulong>(cur);
            for (int i = 0; i < cur64.Length; i++)
            {
                ulong a = raw64[i];
                ulong b = prior64[i];
                cur64[i] = ((a & LowMask) + (b & LowMask)) ^ ((a ^ b) & HighMask);
            }

            k = cur64.Length * 8;
        }
#endif

        for (; k < length; k++)
        {
            cur[k] = (byte)(raw[k] + prior[k]);
        }
    }

    private static void UnfilterSub(ReadOnlySpan<byte> raw, Span<byte> cur, int filterBytes)
    {
        int length = cur.Length;
#if NET8_0_OR_GREATER
        if (length >= 16 && (Sse2.IsSupported || AdvSimd.IsSupported))
        {
            switch (filterBytes)
            {
                case 4:
                    UnfilterSub4Simd(raw, cur);
                    return;
                case 3:
                    if (Ssse3.IsSupported || AdvSimd.IsSupported)
                    {
                        UnfilterSub3Simd(raw, cur);
                        return;
                    }
                    break;
                case 8:
                    UnfilterSub8Simd(raw, cur);
                    return;
                case 2:
                    UnfilterSub2Simd(raw, cur);
                    return;
                case 1:
                    if (Ssse3.IsSupported || AdvSimd.IsSupported)
                    {
                        UnfilterSub1Simd(raw, cur);
                        return;
                    }
                    break;
            }
        }
#endif

        raw.Slice(0, filterBytes).CopyTo(cur);
        for (int k = filterBytes; k < length; k++)
        {
            cur[k] = (byte)(raw[k] + cur[k - filterBytes]);
        }
    }

    private static void UnfilterAverage(ReadOnlySpan<byte> raw, ReadOnlySpan<byte> prior, Span<byte> cur, int filterBytes)
    {
        int length = cur.Length;
#if NET8_0_OR_GREATER
        if (Vector128.IsHardwareAccelerated)
        {
            if (filterBytes == 4 && length >= 4)
            {
                UnfilterAverage4Simd(raw, prior, cur);
                return;
            }

            if (filterBytes == 8 && length >= 8)
            {
                UnfilterAverage8Simd(raw, prior, cur);
                return;
            }

            if (filterBytes == 3 && length >= 4)
            {
                UnfilterAverage3Simd(raw, prior, cur);
                return;
            }
        }
#endif

        for (int k = 0; k < filterBytes; k++)
        {
            cur[k] = (byte)(raw[k] + (prior[k] >> 1));
        }

        for (int k = filterBytes; k < length; k++)
        {
            cur[k] = (byte)(raw[k] + ((prior[k] + cur[k - filterBytes]) >> 1));
        }
    }

    private static void UnfilterAvgFirst(ReadOnlySpan<byte> raw, Span<byte> cur, int filterBytes)
    {
        int length = cur.Length;
#if NET8_0_OR_GREATER
        if (Vector128.IsHardwareAccelerated)
        {
            if (filterBytes == 4 && length >= 4)
            {
                UnfilterAvgFirst4Simd(raw, cur);
                return;
            }

            if (filterBytes == 8 && length >= 8)
            {
                UnfilterAvgFirst8Simd(raw, cur);
                return;
            }
        }
#endif

        raw.Slice(0, filterBytes).CopyTo(cur);
        for (int k = filterBytes; k < length; k++)
        {
            cur[k] = (byte)(raw[k] + (cur[k - filterBytes] >> 1));
        }
    }

    private static void UnfilterPaeth(ReadOnlySpan<byte> raw, ReadOnlySpan<byte> prior, Span<byte> cur, int filterBytes)
    {
        int length = cur.Length;
#if NET8_0_OR_GREATER
        if (Vector128.IsHardwareAccelerated)
        {
            if (filterBytes == 4 && length >= 4)
            {
                UnfilterPaeth4Simd(raw, prior, cur);
                return;
            }

            if (filterBytes == 8 && length >= 8)
            {
                UnfilterPaeth8Simd(raw, prior, cur);
                return;
            }

            if (filterBytes == 3 && length >= 4)
            {
                UnfilterPaeth3Simd(raw, prior, cur);
                return;
            }

            if (filterBytes == 6 && length >= 8)
            {
                UnfilterPaeth6Simd(raw, prior, cur);
                return;
            }
        }
#endif

        for (int k = 0; k < filterBytes; k++)
        {
            cur[k] = (byte)(raw[k] + prior[k]);
        }

        for (int k = filterBytes; k < length; k++)
        {
            cur[k] = (byte)(raw[k] + PaethPredictor(cur[k - filterBytes], prior[k], prior[k - filterBytes]));
        }
    }

#if NET8_0_OR_GREATER
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector128<byte> PackLowBytes(Vector128<short> v) =>
        Sse2.IsSupported
            ? Sse2.PackUnsignedSaturate(v, v)
            : Vector128.Narrow(v.AsUInt16(), v.AsUInt16());

    private static void UnfilterSub4Simd(ReadOnlySpan<byte> raw, Span<byte> cur)
    {
        int length = cur.Length;
        int limit = length - 16;
        Vector128<byte> prev = Vector128<byte>.Zero;
        Vector128<uint> broadcastLane3 = Vector128.Create(3u);

        int k = 0;
        for (; k <= limit; k += 16)
        {
            Vector128<byte> v = Vector128.Create(raw.Slice(k, 16));
            if (Sse2.IsSupported)
            {
                v += Sse2.ShiftLeftLogical128BitLane(v, 4);
                v += Sse2.ShiftLeftLogical128BitLane(v, 8);
            }
            else
            {
                v += AdvSimd.ExtractVector128(Vector128<byte>.Zero, v, 12);
                v += AdvSimd.ExtractVector128(Vector128<byte>.Zero, v, 8);
            }

            v += prev;
            v.CopyTo(cur.Slice(k, 16));
            prev = Vector128.Shuffle(v.AsUInt32(), broadcastLane3).AsByte();
        }

        for (; k < length; k++)
        {
            cur[k] = (byte)(raw[k] + cur[k - 4]);
        }
    }

    private static void UnfilterSub3Simd(ReadOnlySpan<byte> raw, Span<byte> cur)
    {
        int length = cur.Length;
        int limit = length - 16;
        Vector128<byte> prev = Vector128<byte>.Zero;
        Vector128<byte> broadcastRgb3 = Vector128.Create(
            (byte)9, 10, 11, 9, 10, 11, 9, 10, 11, 9, 10, 11, 9, 10, 11, 9);

        int k = 0;
        for (; k <= limit; k += 12)
        {
            Vector128<byte> v = Vector128.Create(raw.Slice(k, 16));
            if (Sse2.IsSupported)
            {
                v += Sse2.ShiftLeftLogical128BitLane(v, 3);
                v += Sse2.ShiftLeftLogical128BitLane(v, 6);
            }
            else
            {
                v += AdvSimd.ExtractVector128(Vector128<byte>.Zero, v, 13);
                v += AdvSimd.ExtractVector128(Vector128<byte>.Zero, v, 10);
            }

            v += prev;
            v.CopyTo(cur.Slice(k, 16));
            prev = Vector128.Shuffle(v, broadcastRgb3);
        }

        for (; k < length; k++)
        {
            cur[k] = (byte)(raw[k] + cur[k - 3]);
        }
    }

    private static void UnfilterSub8Simd(ReadOnlySpan<byte> raw, Span<byte> cur)
    {
        int length = cur.Length;
        int limit = length - 16;
        Vector128<byte> prev = Vector128<byte>.Zero;
        Vector128<ulong> broadcastLane1 = Vector128.Create(1UL);

        int k = 0;
        for (; k <= limit; k += 16)
        {
            Vector128<byte> v = Vector128.Create(raw.Slice(k, 16));
            if (Sse2.IsSupported)
            {
                v += Sse2.ShiftLeftLogical128BitLane(v, 8);
            }
            else
            {
                v += AdvSimd.ExtractVector128(Vector128<byte>.Zero, v, 8);
            }

            v += prev;
            v.CopyTo(cur.Slice(k, 16));
            prev = Vector128.Shuffle(v.AsUInt64(), broadcastLane1).AsByte();
        }

        for (; k < length; k++)
        {
            cur[k] = (byte)(raw[k] + cur[k - 8]);
        }
    }

    private static void UnfilterSub2Simd(ReadOnlySpan<byte> raw, Span<byte> cur)
    {
        int length = cur.Length;
        int limit = length - 16;
        Vector128<byte> prev = Vector128<byte>.Zero;
        Vector128<ushort> broadcastLane7 = Vector128.Create((ushort)7);

        int k = 0;
        for (; k <= limit; k += 16)
        {
            Vector128<byte> v = Vector128.Create(raw.Slice(k, 16));
            if (Sse2.IsSupported)
            {
                v += Sse2.ShiftLeftLogical128BitLane(v, 2);
                v += Sse2.ShiftLeftLogical128BitLane(v, 4);
                v += Sse2.ShiftLeftLogical128BitLane(v, 8);
            }
            else
            {
                v += AdvSimd.ExtractVector128(Vector128<byte>.Zero, v, 14);
                v += AdvSimd.ExtractVector128(Vector128<byte>.Zero, v, 12);
                v += AdvSimd.ExtractVector128(Vector128<byte>.Zero, v, 8);
            }

            v += prev;
            v.CopyTo(cur.Slice(k, 16));
            prev = Vector128.Shuffle(v.AsUInt16(), broadcastLane7).AsByte();
        }

        for (; k < length; k++)
        {
            cur[k] = (byte)(raw[k] + cur[k - 2]);
        }
    }

    private static void UnfilterSub1Simd(ReadOnlySpan<byte> raw, Span<byte> cur)
    {
        int length = cur.Length;
        int limit = length - 16;
        Vector128<byte> prev = Vector128<byte>.Zero;
        Vector128<byte> broadcastLane15 = Vector128.Create((byte)15);

        int k = 0;
        for (; k <= limit; k += 16)
        {
            Vector128<byte> v = Vector128.Create(raw.Slice(k, 16));
            if (Sse2.IsSupported)
            {
                v += Sse2.ShiftLeftLogical128BitLane(v, 1);
                v += Sse2.ShiftLeftLogical128BitLane(v, 2);
                v += Sse2.ShiftLeftLogical128BitLane(v, 4);
                v += Sse2.ShiftLeftLogical128BitLane(v, 8);
            }
            else
            {
                v += AdvSimd.ExtractVector128(Vector128<byte>.Zero, v, 15);
                v += AdvSimd.ExtractVector128(Vector128<byte>.Zero, v, 14);
                v += AdvSimd.ExtractVector128(Vector128<byte>.Zero, v, 12);
                v += AdvSimd.ExtractVector128(Vector128<byte>.Zero, v, 8);
            }

            v += prev;
            v.CopyTo(cur.Slice(k, 16));
            prev = Vector128.Shuffle(v, broadcastLane15);
        }

        for (; k < length; k++)
        {
            cur[k] = (byte)(raw[k] + cur[k - 1]);
        }
    }

    private static void UnfilterPaeth4Simd(ReadOnlySpan<byte> raw, ReadOnlySpan<byte> prior, Span<byte> cur)
    {
        int length = cur.Length;
        ReadOnlySpan<uint> raw32 = MemoryMarshal.Cast<byte, uint>(raw.Slice(0, length));
        ReadOnlySpan<uint> prior32 = MemoryMarshal.Cast<byte, uint>(prior.Slice(0, length));
        Span<uint> cur32 = MemoryMarshal.Cast<byte, uint>(cur);

        Vector128<short> maskFF = Vector128.Create((short)0xFF);
        Vector128<short> r0 = Vector128.WidenLower(Vector128.CreateScalar(raw32[0]).AsByte()).AsInt16();
        Vector128<short> c = Vector128.WidenLower(Vector128.CreateScalar(prior32[0]).AsByte()).AsInt16();
        Vector128<short> a = (r0 + c) & maskFF;
        cur32[0] = PackLowBytes(a).AsUInt32().ToScalar();

        for (int i = 1; i < cur32.Length; i++)
        {
            Vector128<short> b = Vector128.WidenLower(Vector128.CreateScalar(prior32[i]).AsByte()).AsInt16();
            Vector128<short> r = Vector128.WidenLower(Vector128.CreateScalar(raw32[i]).AsByte()).AsInt16();

            Vector128<short> c3mb = ((c + c) + c) - b;
            Vector128<short> lo = Vector128.Min(a, b);
            Vector128<short> hi = Vector128.Max(a, b);
            Vector128<short> thresh = c3mb - a;

            Vector128<short> t0 = Vector128.ConditionalSelect(Vector128.GreaterThan(hi, thresh), c, lo);
            Vector128<short> pred = Vector128.ConditionalSelect(Vector128.GreaterThan(thresh, lo), t0, hi);

            a = (r + pred) & maskFF;
            c = b;
            cur32[i] = PackLowBytes(a).AsUInt32().ToScalar();
        }

        for (int k = cur32.Length * 4; k < length; k++)
        {
            cur[k] = (byte)(raw[k] + PaethPredictor(cur[k - 4], prior[k], prior[k - 4]));
        }
    }

    private static void UnfilterPaeth8Simd(ReadOnlySpan<byte> raw, ReadOnlySpan<byte> prior, Span<byte> cur)
    {
        int length = cur.Length;
        ReadOnlySpan<ulong> raw64 = MemoryMarshal.Cast<byte, ulong>(raw.Slice(0, length));
        ReadOnlySpan<ulong> prior64 = MemoryMarshal.Cast<byte, ulong>(prior.Slice(0, length));
        Span<ulong> cur64 = MemoryMarshal.Cast<byte, ulong>(cur);

        Vector128<short> maskFF = Vector128.Create((short)0xFF);
        Vector128<short> r0 = Vector128.WidenLower(Vector128.CreateScalar(raw64[0]).AsByte()).AsInt16();
        Vector128<short> c = Vector128.WidenLower(Vector128.CreateScalar(prior64[0]).AsByte()).AsInt16();
        Vector128<short> a = (r0 + c) & maskFF;
        cur64[0] = PackLowBytes(a).AsUInt64().ToScalar();

        for (int i = 1; i < cur64.Length; i++)
        {
            Vector128<short> b = Vector128.WidenLower(Vector128.CreateScalar(prior64[i]).AsByte()).AsInt16();
            Vector128<short> r = Vector128.WidenLower(Vector128.CreateScalar(raw64[i]).AsByte()).AsInt16();

            Vector128<short> c3mb = ((c + c) + c) - b;
            Vector128<short> lo = Vector128.Min(a, b);
            Vector128<short> hi = Vector128.Max(a, b);
            Vector128<short> thresh = c3mb - a;

            Vector128<short> t0 = Vector128.ConditionalSelect(Vector128.GreaterThan(hi, thresh), c, lo);
            Vector128<short> pred = Vector128.ConditionalSelect(Vector128.GreaterThan(thresh, lo), t0, hi);

            a = (r + pred) & maskFF;
            c = b;
            cur64[i] = PackLowBytes(a).AsUInt64().ToScalar();
        }

        for (int k = cur64.Length * 8; k < length; k++)
        {
            cur[k] = (byte)(raw[k] + PaethPredictor(cur[k - 8], prior[k], prior[k - 8]));
        }
    }

    private static void UnfilterPaeth3Simd(ReadOnlySpan<byte> raw, ReadOnlySpan<byte> prior, Span<byte> cur)
    {
        int length = cur.Length;
        Vector128<short> maskFF = Vector128.Create((short)0xFF);

        uint r0Px = BinaryPrimitives.ReadUInt32LittleEndian(raw.Slice(0, 4));
        uint c0Px = BinaryPrimitives.ReadUInt32LittleEndian(prior.Slice(0, 4));
        Vector128<short> c = Vector128.WidenLower(Vector128.CreateScalar(c0Px).AsByte()).AsInt16();
        Vector128<short> a = (Vector128.WidenLower(Vector128.CreateScalar(r0Px).AsByte()).AsInt16() + c) & maskFF;
        BinaryPrimitives.WriteUInt32LittleEndian(cur.Slice(0, 4), PackLowBytes(a).AsUInt32().ToScalar());

        int k = 3;
        int limit = length - 4;
        for (; k <= limit; k += 3)
        {
            uint bPx = BinaryPrimitives.ReadUInt32LittleEndian(prior.Slice(k, 4));
            uint rPx = BinaryPrimitives.ReadUInt32LittleEndian(raw.Slice(k, 4));
            Vector128<short> b = Vector128.WidenLower(Vector128.CreateScalar(bPx).AsByte()).AsInt16();
            Vector128<short> r = Vector128.WidenLower(Vector128.CreateScalar(rPx).AsByte()).AsInt16();

            Vector128<short> c3mb = ((c + c) + c) - b;
            Vector128<short> lo = Vector128.Min(a, b);
            Vector128<short> hi = Vector128.Max(a, b);
            Vector128<short> thresh = c3mb - a;

            Vector128<short> t0 = Vector128.ConditionalSelect(Vector128.GreaterThan(hi, thresh), c, lo);
            Vector128<short> pred = Vector128.ConditionalSelect(Vector128.GreaterThan(thresh, lo), t0, hi);

            a = (r + pred) & maskFF;
            c = b;
            BinaryPrimitives.WriteUInt32LittleEndian(cur.Slice(k, 4), PackLowBytes(a).AsUInt32().ToScalar());
        }

        for (; k < length; k++)
        {
            cur[k] = (byte)(raw[k] + PaethPredictor(cur[k - 3], prior[k], prior[k - 3]));
        }
    }

    private static void UnfilterPaeth6Simd(ReadOnlySpan<byte> raw, ReadOnlySpan<byte> prior, Span<byte> cur)
    {
        int length = cur.Length;
        Vector128<short> maskFF = Vector128.Create((short)0xFF);

        ulong r0Px = BinaryPrimitives.ReadUInt64LittleEndian(raw.Slice(0, 8));
        ulong c0Px = BinaryPrimitives.ReadUInt64LittleEndian(prior.Slice(0, 8));
        Vector128<short> c = Vector128.WidenLower(Vector128.CreateScalar(c0Px).AsByte()).AsInt16();
        Vector128<short> a = (Vector128.WidenLower(Vector128.CreateScalar(r0Px).AsByte()).AsInt16() + c) & maskFF;
        BinaryPrimitives.WriteUInt64LittleEndian(cur.Slice(0, 8), PackLowBytes(a).AsUInt64().ToScalar());

        int k = 6;
        int limit = length - 8;
        for (; k <= limit; k += 6)
        {
            ulong bPx = BinaryPrimitives.ReadUInt64LittleEndian(prior.Slice(k, 8));
            ulong rPx = BinaryPrimitives.ReadUInt64LittleEndian(raw.Slice(k, 8));
            Vector128<short> b = Vector128.WidenLower(Vector128.CreateScalar(bPx).AsByte()).AsInt16();
            Vector128<short> r = Vector128.WidenLower(Vector128.CreateScalar(rPx).AsByte()).AsInt16();

            Vector128<short> c3mb = ((c + c) + c) - b;
            Vector128<short> lo = Vector128.Min(a, b);
            Vector128<short> hi = Vector128.Max(a, b);
            Vector128<short> thresh = c3mb - a;

            Vector128<short> t0 = Vector128.ConditionalSelect(Vector128.GreaterThan(hi, thresh), c, lo);
            Vector128<short> pred = Vector128.ConditionalSelect(Vector128.GreaterThan(thresh, lo), t0, hi);

            a = (r + pred) & maskFF;
            c = b;
            BinaryPrimitives.WriteUInt64LittleEndian(cur.Slice(k, 8), PackLowBytes(a).AsUInt64().ToScalar());
        }

        for (; k < length; k++)
        {
            cur[k] = (byte)(raw[k] + PaethPredictor(cur[k - 6], prior[k], prior[k - 6]));
        }
    }

    private static void UnfilterAverage4Simd(ReadOnlySpan<byte> raw, ReadOnlySpan<byte> prior, Span<byte> cur)
    {
        int length = cur.Length;
        ReadOnlySpan<uint> raw32 = MemoryMarshal.Cast<byte, uint>(raw.Slice(0, length));
        ReadOnlySpan<uint> prior32 = MemoryMarshal.Cast<byte, uint>(prior.Slice(0, length));
        Span<uint> cur32 = MemoryMarshal.Cast<byte, uint>(cur);

        Vector128<ushort> maskFF = Vector128.Create((ushort)0xFF);
        Vector128<ushort> r0 = Vector128.WidenLower(Vector128.CreateScalar(raw32[0]).AsByte());
        Vector128<ushort> b0 = Vector128.WidenLower(Vector128.CreateScalar(prior32[0]).AsByte());
        Vector128<ushort> a = (r0 + Vector128.ShiftRightLogical(b0, 1)) & maskFF;
        cur32[0] = PackLowBytes(a.AsInt16()).AsUInt32().ToScalar();

        for (int i = 1; i < cur32.Length; i++)
        {
            Vector128<ushort> b = Vector128.WidenLower(Vector128.CreateScalar(prior32[i]).AsByte());
            Vector128<ushort> r = Vector128.WidenLower(Vector128.CreateScalar(raw32[i]).AsByte());
            a = (r + Vector128.ShiftRightLogical(a + b, 1)) & maskFF;
            cur32[i] = PackLowBytes(a.AsInt16()).AsUInt32().ToScalar();
        }

        for (int k = cur32.Length * 4; k < length; k++)
        {
            cur[k] = (byte)(raw[k] + ((prior[k] + cur[k - 4]) >> 1));
        }
    }

    private static void UnfilterAverage8Simd(ReadOnlySpan<byte> raw, ReadOnlySpan<byte> prior, Span<byte> cur)
    {
        int length = cur.Length;
        ReadOnlySpan<ulong> raw64 = MemoryMarshal.Cast<byte, ulong>(raw.Slice(0, length));
        ReadOnlySpan<ulong> prior64 = MemoryMarshal.Cast<byte, ulong>(prior.Slice(0, length));
        Span<ulong> cur64 = MemoryMarshal.Cast<byte, ulong>(cur);

        Vector128<ushort> maskFF = Vector128.Create((ushort)0xFF);
        Vector128<ushort> r0 = Vector128.WidenLower(Vector128.CreateScalar(raw64[0]).AsByte());
        Vector128<ushort> b0 = Vector128.WidenLower(Vector128.CreateScalar(prior64[0]).AsByte());
        Vector128<ushort> a = (r0 + Vector128.ShiftRightLogical(b0, 1)) & maskFF;
        cur64[0] = PackLowBytes(a.AsInt16()).AsUInt64().ToScalar();

        for (int i = 1; i < cur64.Length; i++)
        {
            Vector128<ushort> b = Vector128.WidenLower(Vector128.CreateScalar(prior64[i]).AsByte());
            Vector128<ushort> r = Vector128.WidenLower(Vector128.CreateScalar(raw64[i]).AsByte());
            a = (r + Vector128.ShiftRightLogical(a + b, 1)) & maskFF;
            cur64[i] = PackLowBytes(a.AsInt16()).AsUInt64().ToScalar();
        }

        for (int k = cur64.Length * 8; k < length; k++)
        {
            cur[k] = (byte)(raw[k] + ((prior[k] + cur[k - 8]) >> 1));
        }
    }

    private static void UnfilterAverage3Simd(ReadOnlySpan<byte> raw, ReadOnlySpan<byte> prior, Span<byte> cur)
    {
        int length = cur.Length;
        Vector128<ushort> maskFF = Vector128.Create((ushort)0xFF);

        uint r0Px = BinaryPrimitives.ReadUInt32LittleEndian(raw.Slice(0, 4));
        uint b0Px = BinaryPrimitives.ReadUInt32LittleEndian(prior.Slice(0, 4));
        Vector128<ushort> a = (Vector128.WidenLower(Vector128.CreateScalar(r0Px).AsByte())
            + Vector128.ShiftRightLogical(Vector128.WidenLower(Vector128.CreateScalar(b0Px).AsByte()), 1)) & maskFF;
        BinaryPrimitives.WriteUInt32LittleEndian(cur.Slice(0, 4), PackLowBytes(a.AsInt16()).AsUInt32().ToScalar());

        int k = 3;
        int limit = length - 4;
        for (; k <= limit; k += 3)
        {
            uint bPx = BinaryPrimitives.ReadUInt32LittleEndian(prior.Slice(k, 4));
            uint rPx = BinaryPrimitives.ReadUInt32LittleEndian(raw.Slice(k, 4));
            Vector128<ushort> b = Vector128.WidenLower(Vector128.CreateScalar(bPx).AsByte());
            Vector128<ushort> r = Vector128.WidenLower(Vector128.CreateScalar(rPx).AsByte());
            a = (r + Vector128.ShiftRightLogical(a + b, 1)) & maskFF;
            BinaryPrimitives.WriteUInt32LittleEndian(cur.Slice(k, 4), PackLowBytes(a.AsInt16()).AsUInt32().ToScalar());
        }

        for (; k < length; k++)
        {
            cur[k] = (byte)(raw[k] + ((prior[k] + cur[k - 3]) >> 1));
        }
    }

    private static void UnfilterAvgFirst4Simd(ReadOnlySpan<byte> raw, Span<byte> cur)
    {
        int length = cur.Length;
        ReadOnlySpan<uint> raw32 = MemoryMarshal.Cast<byte, uint>(raw.Slice(0, length));
        Span<uint> cur32 = MemoryMarshal.Cast<byte, uint>(cur);

        Vector128<ushort> maskFF = Vector128.Create((ushort)0xFF);
        Vector128<ushort> a = Vector128.WidenLower(Vector128.CreateScalar(raw32[0]).AsByte());
        cur32[0] = raw32[0];

        for (int i = 1; i < cur32.Length; i++)
        {
            Vector128<ushort> r = Vector128.WidenLower(Vector128.CreateScalar(raw32[i]).AsByte());
            a = (r + Vector128.ShiftRightLogical(a, 1)) & maskFF;
            cur32[i] = PackLowBytes(a.AsInt16()).AsUInt32().ToScalar();
        }

        for (int k = cur32.Length * 4; k < length; k++)
        {
            cur[k] = (byte)(raw[k] + (cur[k - 4] >> 1));
        }
    }

    private static void UnfilterAvgFirst8Simd(ReadOnlySpan<byte> raw, Span<byte> cur)
    {
        int length = cur.Length;
        ReadOnlySpan<ulong> raw64 = MemoryMarshal.Cast<byte, ulong>(raw.Slice(0, length));
        Span<ulong> cur64 = MemoryMarshal.Cast<byte, ulong>(cur);

        Vector128<ushort> maskFF = Vector128.Create((ushort)0xFF);
        Vector128<ushort> a = Vector128.WidenLower(Vector128.CreateScalar(raw64[0]).AsByte());
        cur64[0] = raw64[0];

        for (int i = 1; i < cur64.Length; i++)
        {
            Vector128<ushort> r = Vector128.WidenLower(Vector128.CreateScalar(raw64[i]).AsByte());
            a = (r + Vector128.ShiftRightLogical(a, 1)) & maskFF;
            cur64[i] = PackLowBytes(a.AsInt16()).AsUInt64().ToScalar();
        }

        for (int k = cur64.Length * 8; k < length; k++)
        {
            cur[k] = (byte)(raw[k] + (cur[k - 8] >> 1));
        }
    }
#endif
}
