using System.Buffers.Binary;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
#if NET8_0_OR_GREATER
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.Arm;
using System.Runtime.Intrinsics.X86;
#endif
using Stb.Managed.Internal;

namespace Stb.Managed.Dxt.Internal;

internal static class DxtBlockEncoder
{
    private const ulong ConstantAlphaIndices = 0x249249249249UL;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int Mul8Bit(int a, int b)
    {
        int t = a * b + 128;
        return (t + (t >> 8)) >> 8;
    }

    // Expands a 565 colour to 8-bit R, G, B exactly as stb_dxt's stb__From16Bit.
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void From16Bit(ushort v, out int r, out int g, out int b)
    {
        r = (((v & 0xF800) >> 11) * 33) >> 2;
        g = (((v & 0x07E0) >> 5) * 65) >> 4;
        b = ((v & 0x001F) * 33) >> 2;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static ushort As16Bit(int r, int g, int b) =>
        (ushort)((Mul8Bit(r, 31) << 11) + (Mul8Bit(g, 63) << 5) + Mul8Bit(b, 31));

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int Lerp13(int a, int b) => (2 * a + b) / 3;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static ushort Quantize5(float x)
    {
        x = x < 0.0f ? 0.0f : x > 1.0f ? 1.0f : x;
        int q = (int)(x * 31.0f);
        if (x > DxtTables.Midpoints5[q])
        {
            q++;
        }

        return (ushort)q;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static ushort Quantize6(float x)
    {
        x = x < 0.0f ? 0.0f : x > 1.0f ? 1.0f : x;
        int q = (int)(x * 63.0f);
        if (x > DxtTables.Midpoints6[q])
        {
            q++;
        }

        return (ushort)q;
    }

    internal static void CompressDxtBlock(Span<byte> dest, ReadOnlySpan<byte> srcRgba, bool includeAlpha, DxtCompressionMode mode)
    {
        if (includeAlpha)
        {
            CompressAlphaStride4(dest, srcRgba);
            CompressColorBlock(dest.Slice(8), srcRgba, ignoreAlphaForConstancy: true, mode);
        }
        else
        {
            CompressColorBlock(dest, srcRgba, ignoreAlphaForConstancy: false, mode);
        }
    }

    internal static void CompressBc4Block(Span<byte> dest, ReadOnlySpan<byte> srcR)
    {
#if NET8_0_OR_GREATER
        if (Sse2.IsSupported || AdvSimd.Arm64.IsSupported)
        {
            Vector128<byte> v = Vector128.Create(srcR);
            CompressAlphaVector128(dest, v);
            return;
        }
#endif
        CompressAlphaScalar(dest, srcR, 0, 1);
    }

    internal static void CompressBc5Block(Span<byte> dest, ReadOnlySpan<byte> srcRg)
    {
#if NET8_0_OR_GREATER
        if (AdvSimd.Arm64.IsSupported)
        {
            Vector128<byte> rg0 = Vector128.Create(srcRg);
            Vector128<byte> rg1 = Vector128.Create(srcRg.Slice(16));
            Vector128<byte> r = AdvSimd.Arm64.UnzipEven(rg0, rg1);
            Vector128<byte> g = AdvSimd.Arm64.UnzipOdd(rg0, rg1);
            CompressAlphaVector128(dest, r);
            CompressAlphaVector128(dest.Slice(8), g);
            return;
        }

        if (Ssse3.IsSupported)
        {
            Vector128<byte> rg0 = Vector128.Create(srcRg);
            Vector128<byte> rg1 = Vector128.Create(srcRg.Slice(16));
            Vector128<byte> deinterleave = Vector128.Create(
                (byte)0, 2, 4, 6, 8, 10, 12, 14,
                1, 3, 5, 7, 9, 11, 13, 15);
            Vector128<byte> s0 = Ssse3.Shuffle(rg0, deinterleave);
            Vector128<byte> s1 = Ssse3.Shuffle(rg1, deinterleave);
            Vector128<byte> r = Sse2.UnpackLow(s0.AsUInt64(), s1.AsUInt64()).AsByte();
            Vector128<byte> g = Sse2.UnpackHigh(s0.AsUInt64(), s1.AsUInt64()).AsByte();
            CompressAlphaVector128(dest, r);
            CompressAlphaVector128(dest.Slice(8), g);
            return;
        }
#endif
        CompressAlphaScalar(dest, srcRg, 0, 2);
        CompressAlphaScalar(dest.Slice(8), srcRg, 1, 2);
    }

    private static void CompressAlphaStride4(Span<byte> dest, ReadOnlySpan<byte> srcRgba)
    {
#if NET8_0_OR_GREATER
        if (AdvSimd.Arm64.IsSupported)
        {
            Vector128<byte> r0 = Vector128.Create(srcRgba);
            Vector128<byte> r1 = Vector128.Create(srcRgba.Slice(16));
            Vector128<byte> r2 = Vector128.Create(srcRgba.Slice(32));
            Vector128<byte> r3 = Vector128.Create(srcRgba.Slice(48));
            Vector128<byte> ga01 = AdvSimd.Arm64.UnzipOdd(r0, r1);
            Vector128<byte> ga23 = AdvSimd.Arm64.UnzipOdd(r2, r3);
            Vector128<byte> a = AdvSimd.Arm64.UnzipOdd(ga01, ga23);
            CompressAlphaVector128(dest, a);
            return;
        }

        if (Ssse3.IsSupported)
        {
            Vector128<byte> shuf = Vector128.Create(
                (byte)3, 7, 11, 15, 0x80, 0x80, 0x80, 0x80,
                0x80, 0x80, 0x80, 0x80, 0x80, 0x80, 0x80, 0x80);
            uint a0 = Ssse3.Shuffle(Vector128.Create(srcRgba), shuf).AsUInt32().GetElement(0);
            uint a1 = Ssse3.Shuffle(Vector128.Create(srcRgba.Slice(16)), shuf).AsUInt32().GetElement(0);
            uint a2 = Ssse3.Shuffle(Vector128.Create(srcRgba.Slice(32)), shuf).AsUInt32().GetElement(0);
            uint a3 = Ssse3.Shuffle(Vector128.Create(srcRgba.Slice(48)), shuf).AsUInt32().GetElement(0);
            Vector128<byte> a = Vector128.Create(a0, a1, a2, a3).AsByte();
            CompressAlphaVector128(dest, a);
            return;
        }
#endif
        CompressAlphaScalar(dest, srcRgba, 3, 4);
    }

#if NET8_0_OR_GREATER
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void CompressAlphaVector128(Span<byte> dest, Vector128<byte> v)
    {
        byte mn;
        byte mx;

        if (AdvSimd.Arm64.IsSupported)
        {
            mn = AdvSimd.Arm64.MinAcross(v).ToScalar();
            mx = AdvSimd.Arm64.MaxAcross(v).ToScalar();
        }
        else
        {
            Vector128<byte> tMin = Sse2.Min(v, Sse2.ShiftRightLogical128BitLane(v, 8));
            tMin = Sse2.Min(tMin, Sse2.ShiftRightLogical128BitLane(tMin, 4));
            tMin = Sse2.Min(tMin, Sse2.ShiftRightLogical128BitLane(tMin, 2));
            tMin = Sse2.Min(tMin, Sse2.ShiftRightLogical128BitLane(tMin, 1));
            mn = tMin.GetElement(0);

            Vector128<byte> tMax = Sse2.Max(v, Sse2.ShiftRightLogical128BitLane(v, 8));
            tMax = Sse2.Max(tMax, Sse2.ShiftRightLogical128BitLane(tMax, 4));
            tMax = Sse2.Max(tMax, Sse2.ShiftRightLogical128BitLane(tMax, 2));
            tMax = Sse2.Max(tMax, Sse2.ShiftRightLogical128BitLane(tMax, 1));
            mx = tMax.GetElement(0);
        }

        int dist = mx - mn;
        if (dist == 0)
        {
            WriteAlphaBlock(dest, mx, mn, ConstantAlphaIndices);
            return;
        }

        short dist1 = (short)dist;
        short dist2 = (short)(dist * 2);
        short dist4 = (short)(dist * 4);
        short bias = (short)(((dist < 8) ? (dist - 1) : ((dist >> 1) + 2)) - mn * 7);

        Vector128<byte> idxBytes;
        if (Avx2.IsSupported)
        {
            Vector256<short> v16 = Avx2.ConvertToVector256Int16(v);
            Vector256<short> a = Avx2.Add(Avx2.MultiplyLow(v16, Vector256.Create((short)7)), Vector256.Create(bias));

            Vector256<short> t4 = Avx2.CompareGreaterThan(a, Vector256.Create((short)(dist4 - 1)));
            Vector256<short> ind = Avx2.And(t4, Vector256.Create((short)4));
            a = Avx2.Subtract(a, Avx2.And(t4, Vector256.Create(dist4)));

            Vector256<short> t2 = Avx2.CompareGreaterThan(a, Vector256.Create((short)(dist2 - 1)));
            ind = Avx2.Add(ind, Avx2.And(t2, Vector256.Create((short)2)));
            a = Avx2.Subtract(a, Avx2.And(t2, Vector256.Create(dist2)));

            Vector256<short> t1 = Avx2.CompareGreaterThan(a, Vector256.Create((short)(dist1 - 1)));
            ind = Avx2.Subtract(ind, t1);

            ind = Avx2.And(Avx2.Subtract(Vector256<short>.Zero, ind), Vector256.Create((short)7));
            Vector256<short> lt2 = Avx2.CompareGreaterThan(Vector256.Create((short)2), ind);
            ind = Avx2.Xor(ind, Avx2.And(lt2, Vector256.Create((short)1)));

            idxBytes = Sse2.PackUnsignedSaturate(ind.GetLower(), ind.GetUpper());
        }
        else if (Sse2.IsSupported)
        {
            Vector128<byte> zero = Vector128<byte>.Zero;
            Vector128<short> lo = QuantizeAlpha8Sse2(Sse2.UnpackLow(v, zero).AsInt16(), dist1, dist2, dist4, bias);
            Vector128<short> hi = QuantizeAlpha8Sse2(Sse2.UnpackHigh(v, zero).AsInt16(), dist1, dist2, dist4, bias);
            idxBytes = Sse2.PackUnsignedSaturate(lo, hi);
        }
        else
        {
            Vector128<short> lo = QuantizeAlpha8AdvSimd(AdvSimd.ZeroExtendWideningLower(v.GetLower()).AsInt16(), dist1, dist2, dist4, bias);
            Vector128<short> hi = QuantizeAlpha8AdvSimd(AdvSimd.ZeroExtendWideningUpper(v).AsInt16(), dist1, dist2, dist4, bias);
            Vector64<byte> bLo = AdvSimd.ExtractNarrowingSaturateUnsignedLower(lo);
            idxBytes = AdvSimd.ExtractNarrowingSaturateUnsignedUpper(bLo, hi);
        }

        ulong lo8 = idxBytes.AsUInt64().GetElement(0);
        ulong hi8 = idxBytes.AsUInt64().GetElement(1);
        uint low24 = Pack8x3Bits(lo8);
        uint high24 = Pack8x3Bits(hi8);
        WriteAlphaBlock(dest, mx, mn, low24 | ((ulong)high24 << 24));
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector128<short> QuantizeAlpha8Sse2(Vector128<short> v8, short dist1, short dist2, short dist4, short bias)
    {
        Vector128<short> a = Sse2.Add(Sse2.MultiplyLow(v8, Vector128.Create((short)7)), Vector128.Create(bias));

        Vector128<short> t4 = Sse2.CompareGreaterThan(a, Vector128.Create((short)(dist4 - 1)));
        Vector128<short> ind = Sse2.And(t4, Vector128.Create((short)4));
        a = Sse2.Subtract(a, Sse2.And(t4, Vector128.Create(dist4)));

        Vector128<short> t2 = Sse2.CompareGreaterThan(a, Vector128.Create((short)(dist2 - 1)));
        ind = Sse2.Add(ind, Sse2.And(t2, Vector128.Create((short)2)));
        a = Sse2.Subtract(a, Sse2.And(t2, Vector128.Create(dist2)));

        Vector128<short> t1 = Sse2.CompareGreaterThan(a, Vector128.Create((short)(dist1 - 1)));
        ind = Sse2.Subtract(ind, t1);

        ind = Sse2.And(Sse2.Subtract(Vector128<short>.Zero, ind), Vector128.Create((short)7));
        Vector128<short> lt2 = Sse2.CompareGreaterThan(Vector128.Create((short)2), ind);
        return Sse2.Xor(ind, Sse2.And(lt2, Vector128.Create((short)1)));
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector128<short> QuantizeAlpha8AdvSimd(Vector128<short> v8, short dist1, short dist2, short dist4, short bias)
    {
        Vector128<short> a = AdvSimd.Add(AdvSimd.Multiply(v8, Vector128.Create((short)7)), Vector128.Create(bias));

        Vector128<short> t4 = AdvSimd.CompareGreaterThanOrEqual(a, Vector128.Create(dist4));
        Vector128<short> ind = AdvSimd.And(t4, Vector128.Create((short)4));
        a = AdvSimd.Subtract(a, AdvSimd.And(t4, Vector128.Create(dist4)));

        Vector128<short> t2 = AdvSimd.CompareGreaterThanOrEqual(a, Vector128.Create(dist2));
        ind = AdvSimd.Add(ind, AdvSimd.And(t2, Vector128.Create((short)2)));
        a = AdvSimd.Subtract(a, AdvSimd.And(t2, Vector128.Create(dist2)));

        Vector128<short> t1 = AdvSimd.CompareGreaterThanOrEqual(a, Vector128.Create(dist1));
        ind = AdvSimd.Subtract(ind, t1);

        ind = AdvSimd.And(AdvSimd.Negate(ind), Vector128.Create((short)7));
        Vector128<short> lt2 = AdvSimd.CompareGreaterThan(Vector128.Create((short)2), ind);
        return AdvSimd.Xor(ind, AdvSimd.And(lt2, Vector128.Create((short)1)));
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static uint Pack8x3Bits(ulong bytes8)
    {
        ulong pairs = (bytes8 & 0x0007000700070007UL) | ((bytes8 >> 5) & 0x0038003800380038UL);
        uint p0 = (uint)pairs;
        uint p1 = (uint)(pairs >> 32);
        return (p0 & 0x3Fu)
            | ((p0 >> 10) & 0xFC0u)
            | ((p1 & 0x3Fu) << 12)
            | (((p1 >> 10) & 0xFC0u) << 12);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void WriteAlphaBlock(Span<byte> dest, byte max, byte min, ulong bits48) =>
        BinaryPrimitives.WriteUInt64LittleEndian(dest, max | ((ulong)min << 8) | (bits48 << 16));
#endif

    private static void CompressAlphaScalar(Span<byte> dest, ReadOnlySpan<byte> src, int offset, int stride)
    {
        int mn = src[offset];
        int mx = mn;
        for (int i = 1; i < 16; ++i)
        {
            int v = src[offset + i * stride];
            if (v < mn)
            {
                mn = v;
            }
            else if (v > mx)
            {
                mx = v;
            }
        }

        dest[0] = (byte)mx;
        dest[1] = (byte)mn;

        int dist = mx - mn;
        int dist4 = dist * 4;
        int dist2 = dist * 2;
        int bias = (dist < 8) ? (dist - 1) : (dist / 2 + 2);
        bias -= mn * 7;

        int bits = 0;
        int mask = 0;
        int dstIdx = 2;

        for (int i = 0; i < 16; ++i)
        {
            int a = src[offset + i * stride] * 7 + bias;
            int t = (a >= dist4) ? -1 : 0;
            int ind = t & 4;
            a -= dist4 & t;

            t = (a >= dist2) ? -1 : 0;
            ind += t & 2;
            a -= dist2 & t;

            if (a >= dist)
            {
                ind++;
            }

            ind = (-ind) & 7;
            if (ind < 2)
            {
                ind ^= 1;
            }

            mask |= ind << bits;
            bits += 3;
            if (bits >= 8)
            {
                dest[dstIdx++] = (byte)mask;
                mask >>= 8;
                bits -= 8;
            }
        }
    }

    private static void CompressColorBlock(Span<byte> dest, ReadOnlySpan<byte> block, bool ignoreAlphaForConstancy, DxtCompressionMode mode)
    {
        int refineCount = (mode & DxtCompressionMode.HighQuality) != 0 ? 2 : 1;
        ushort max16;
        ushort min16;
        uint mask;

        if (IsConstantBlock(block, ignoreAlphaForConstancy))
        {
            int r = block[0];
            int g = block[1];
            int b = block[2];
            mask = 0xAAAAAAAAu;
            max16 = (ushort)((DxtTables.OMatch5[r * 2] << 11)
                | (DxtTables.OMatch6[g * 2] << 5)
                | DxtTables.OMatch5[b * 2]);
            min16 = (ushort)((DxtTables.OMatch5[r * 2 + 1] << 11)
                | (DxtTables.OMatch6[g * 2 + 1] << 5)
                | DxtTables.OMatch5[b * 2 + 1]);
        }
        else
        {
            OptimizeColorsBlock(block, out max16, out min16);
            if (max16 != min16)
            {
                mask = MatchColorsBlock(block, max16, min16);
            }
            else
            {
                mask = 0;
            }

            for (int i = 0; i < refineCount; ++i)
            {
                uint lastMask = mask;
                if (RefineBlock(block, ref max16, ref min16, mask))
                {
                    if (max16 != min16)
                    {
                        mask = MatchColorsBlock(block, max16, min16);
                    }
                    else
                    {
                        mask = 0;
                        break;
                    }
                }

                if (mask == lastMask)
                {
                    break;
                }
            }
        }

        if (max16 < min16)
        {
            (max16, min16) = (min16, max16);
            mask ^= 0x55555555u;
        }

        BinaryPrimitives.WriteUInt64LittleEndian(dest, max16 | ((ulong)min16 << 16) | ((ulong)mask << 32));
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool IsConstantBlock(ReadOnlySpan<byte> block, bool ignoreAlpha)
    {
#if NET8_0_OR_GREATER
        if (Avx2.IsSupported)
        {
            Vector256<uint> v0 = Vector256.Create(block).AsUInt32();
            Vector256<uint> v1 = Vector256.Create(block.Slice(32)).AsUInt32();
            if (ignoreAlpha)
            {
                Vector256<uint> rgbMask = Vector256.Create(0x00FFFFFFu);
                v0 = Avx2.And(v0, rgbMask);
                v1 = Avx2.And(v1, rgbMask);
            }

            Vector256<uint> first = Vector256.Create(v0.GetElement(0));
            Vector256<uint> eq = Avx2.And(Avx2.CompareEqual(v0, first), Avx2.CompareEqual(v1, first));
            return Avx2.MoveMask(eq.AsByte()) == -1;
        }

        if (Sse2.IsSupported)
        {
            Vector128<uint> v0 = Vector128.Create(block).AsUInt32();
            Vector128<uint> v1 = Vector128.Create(block.Slice(16)).AsUInt32();
            Vector128<uint> v2 = Vector128.Create(block.Slice(32)).AsUInt32();
            Vector128<uint> v3 = Vector128.Create(block.Slice(48)).AsUInt32();
            if (ignoreAlpha)
            {
                Vector128<uint> rgbMask = Vector128.Create(0x00FFFFFFu);
                v0 = Sse2.And(v0, rgbMask);
                v1 = Sse2.And(v1, rgbMask);
                v2 = Sse2.And(v2, rgbMask);
                v3 = Sse2.And(v3, rgbMask);
            }

            Vector128<uint> first = Vector128.Create(v0.GetElement(0));
            Vector128<uint> eq = Sse2.And(
                Sse2.And(Sse2.CompareEqual(v0, first), Sse2.CompareEqual(v1, first)),
                Sse2.And(Sse2.CompareEqual(v2, first), Sse2.CompareEqual(v3, first)));
            return Sse2.MoveMask(eq.AsByte()) == 0xFFFF;
        }

        if (AdvSimd.Arm64.IsSupported)
        {
            Vector128<uint> v0 = Vector128.Create(block).AsUInt32();
            Vector128<uint> v1 = Vector128.Create(block.Slice(16)).AsUInt32();
            Vector128<uint> v2 = Vector128.Create(block.Slice(32)).AsUInt32();
            Vector128<uint> v3 = Vector128.Create(block.Slice(48)).AsUInt32();
            if (ignoreAlpha)
            {
                Vector128<uint> rgbMask = Vector128.Create(0x00FFFFFFu);
                v0 = AdvSimd.And(v0, rgbMask);
                v1 = AdvSimd.And(v1, rgbMask);
                v2 = AdvSimd.And(v2, rgbMask);
                v3 = AdvSimd.And(v3, rgbMask);
            }

            Vector128<uint> first = Vector128.Create(v0.GetElement(0));
            Vector128<uint> eq = AdvSimd.And(
                AdvSimd.And(AdvSimd.CompareEqual(v0, first), AdvSimd.CompareEqual(v1, first)),
                AdvSimd.And(AdvSimd.CompareEqual(v2, first), AdvSimd.CompareEqual(v3, first)));
            return AdvSimd.Arm64.MinAcross(eq).ToScalar() == 0xFFFFFFFFu;
        }
#endif
        uint keep = ignoreAlpha ? 0x00FFFFFFu : 0xFFFFFFFFu;
        ReadOnlySpan<uint> px = Pixels(block);
        uint first0 = px[0] & keep;
        foreach (uint p in px)
        {
            if ((p & keep) != first0)
            {
                return false;
            }
        }

        return true;
    }

    private static uint MatchColorsBlock(ReadOnlySpan<byte> block, ushort c0, ushort c1)
    {
        From16Bit(c0, out int r0, out int g0, out int b0);
        From16Bit(c1, out int r1, out int g1, out int b1);
        int r2 = Lerp13(r0, r1), g2 = Lerp13(g0, g1), b2 = Lerp13(b0, b1);
        int r3 = Lerp13(r1, r0), g3 = Lerp13(g1, g0), b3 = Lerp13(b1, b0);

        int dirr = r0 - r1;
        int dirg = g0 - g1;
        int dirb = b0 - b1;

        int stop0 = r0 * dirr + g0 * dirg + b0 * dirb;
        int stop1 = r1 * dirr + g1 * dirg + b1 * dirb;
        int stop2 = r2 * dirr + g2 * dirg + b2 * dirb;
        int stop3 = r3 * dirr + g3 * dirg + b3 * dirb;

        int c0Point = stop1 + stop3;
        int halfPoint = stop3 + stop2;
        int c3Point = stop2 + stop0;

#if NET8_0_OR_GREATER
        if (Avx2.IsSupported)
        {
            Vector256<short> dir16 = Vector256.Create(
                (short)dirr, (short)dirg, (short)dirb, (short)0,
                (short)dirr, (short)dirg, (short)dirb, (short)0,
                (short)dirr, (short)dirg, (short)dirb, (short)0,
                (short)dirr, (short)dirg, (short)dirb, (short)0);
            Vector256<int> laneFix = Vector256.Create(0, 1, 4, 5, 2, 3, 6, 7);
            Vector256<int> vHalf = Vector256.Create(halfPoint);
            Vector256<int> vC0 = Vector256.Create(c0Point);
            Vector256<int> vC3 = Vector256.Create(c3Point);

            uint lo16 = Match8ColorsAvx2(block, dir16, laneFix, vHalf, vC0, vC3);
            uint hi16 = Match8ColorsAvx2(block.Slice(32), dir16, laneFix, vHalf, vC0, vC3);
            return lo16 | (hi16 << 16);
        }

        if (AdvSimd.Arm64.IsSupported)
        {
            Vector128<int> vDir = Vector128.Create(dirr);
            Vector128<int> vDirG = Vector128.Create(dirg);
            Vector128<int> vDirB = Vector128.Create(dirb);
            Vector128<int> vHalf = Vector128.Create(halfPoint);
            Vector128<int> vC0 = Vector128.Create(c0Point);
            Vector128<int> vC3 = Vector128.Create(c3Point);
            uint m0 = Match4ColorsArm64(block.Slice(0, 16), vDir, vDirG, vDirB, vHalf, vC0, vC3);
            uint m1 = Match4ColorsArm64(block.Slice(16, 16), vDir, vDirG, vDirB, vHalf, vC0, vC3);
            uint m2 = Match4ColorsArm64(block.Slice(32, 16), vDir, vDirG, vDirB, vHalf, vC0, vC3);
            uint m3 = Match4ColorsArm64(block.Slice(48, 16), vDir, vDirG, vDirB, vHalf, vC0, vC3);
            return m0 | (m1 << 8) | (m2 << 16) | (m3 << 24);
        }

        if (Ssse3.IsSupported)
        {
            Vector128<byte> zero = Vector128<byte>.Zero;
            Vector128<short> dir16 = Vector128.Create(
                (short)dirr, (short)dirg, (short)dirb, (short)0,
                (short)dirr, (short)dirg, (short)dirb, (short)0);
            Vector128<int> vHalf = Vector128.Create(halfPoint);
            Vector128<int> vC0 = Vector128.Create(c0Point);
            Vector128<int> vC3 = Vector128.Create(c3Point);

            uint m0 = Match4ColorsSsse3(block, zero, dir16, vHalf, vC0, vC3);
            uint m1 = Match4ColorsSsse3(block.Slice(16), zero, dir16, vHalf, vC0, vC3);
            uint m2 = Match4ColorsSsse3(block.Slice(32), zero, dir16, vHalf, vC0, vC3);
            uint m3 = Match4ColorsSsse3(block.Slice(48), zero, dir16, vHalf, vC0, vC3);
            return m0 | (m1 << 8) | (m2 << 16) | (m3 << 24);
        }
#endif
        uint mask = 0;
        ReadOnlySpan<uint> px = Pixels(block);
        for (int i = px.Length - 1; i >= 0; --i)
        {
            int dot = Dot(px[i], dirr, dirg, dirb) * 2;
            mask <<= 2;
            if (dot < halfPoint)
            {
                mask |= (dot < c0Point) ? 1u : 3u;
            }
            else
            {
                mask |= (dot < c3Point) ? 2u : 0u;
            }
        }

        return mask;
    }

#if NET8_0_OR_GREATER
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static uint Match8ColorsAvx2(
        ReadOnlySpan<byte> src,
        Vector256<short> dir16,
        Vector256<int> laneFix,
        Vector256<int> vHalf,
        Vector256<int> vC0,
        Vector256<int> vC3)
    {
        Vector256<short> p0 = Avx2.ConvertToVector256Int16(Vector128.Create(src));
        Vector256<short> p1 = Avx2.ConvertToVector256Int16(Vector128.Create(src.Slice(16)));
        Vector256<int> m0 = Avx2.MultiplyAddAdjacent(p0, dir16);
        Vector256<int> m1 = Avx2.MultiplyAddAdjacent(p1, dir16);
        Vector256<int> dots = Avx2.PermuteVar8x32(Avx2.HorizontalAdd(m0, m1), laneFix);
        Vector256<int> dot2 = Avx2.Add(dots, dots);

        Vector256<int> ltHalf = Avx2.CompareGreaterThan(vHalf, dot2);
        Vector256<int> ltC0 = Avx2.CompareGreaterThan(vC0, dot2);
        Vector256<int> ltC3 = Avx2.CompareGreaterThan(vC3, dot2);

        int b0 = Avx.MoveMask(ltHalf.AsSingle());
        Vector256<int> bit1Vec = Avx2.Or(Avx2.AndNot(ltC0, ltHalf), Avx2.AndNot(ltHalf, ltC3));
        int b1 = Avx.MoveMask(bit1Vec.AsSingle());

        return SpreadBits8(b0) | (SpreadBits8(b1) << 1);
    }

    // Same decisions as the scalar loop: index 1 or 3 below the half point (split at c0Point), 2 or 0 above (split at c3Point).
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static uint Match4ColorsArm64(
        ReadOnlySpan<byte> src,
        Vector128<int> dirR,
        Vector128<int> dirG,
        Vector128<int> dirB,
        Vector128<int> vHalf,
        Vector128<int> vC0,
        Vector128<int> vC3)
    {
        Vector128<int> q = Vector128.Create(src).AsInt32();
        Vector128<int> m = Vector128.Create(0xFF);
        Vector128<int> dots = ((q & m) * dirR) + ((Vector128.ShiftRightLogical(q, 8) & m) * dirG) + ((Vector128.ShiftRightLogical(q, 16) & m) * dirB);
        Vector128<int> dot2 = dots + dots;
        Vector128<int> ltHalf = Vector128.GreaterThan(vHalf, dot2);
        Vector128<int> ltC0 = Vector128.GreaterThan(vC0, dot2);
        Vector128<int> ltC3 = Vector128.GreaterThan(vC3, dot2);

        // bit 0: below the half point; bit 1: (below half and not below c0) or (not below half and below c3).
        Vector128<int> bit1 = Vector128.AndNot(ltHalf, ltC0) | Vector128.AndNot(ltC3, ltHalf);
        Vector128<int> idx = (ltHalf & Vector128.Create(1)) | (bit1 & Vector128.Create(2));
        Vector128<int> shifted = idx * Vector128.Create(1, 4, 16, 64);
        return (uint)Vector128.Sum(shifted);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static uint Match4ColorsSsse3(
        ReadOnlySpan<byte> src,
        Vector128<byte> zero,
        Vector128<short> dir16,
        Vector128<int> vHalf,
        Vector128<int> vC0,
        Vector128<int> vC3)
    {
        Vector128<byte> raw = Vector128.Create(src);
        Vector128<short> p0 = Sse2.UnpackLow(raw, zero).AsInt16();
        Vector128<short> p1 = Sse2.UnpackHigh(raw, zero).AsInt16();
        Vector128<int> m0 = Sse2.MultiplyAddAdjacent(p0, dir16);
        Vector128<int> m1 = Sse2.MultiplyAddAdjacent(p1, dir16);
        Vector128<int> dots = Ssse3.HorizontalAdd(m0, m1);
        Vector128<int> dot2 = Sse2.Add(dots, dots);

        Vector128<int> ltHalf = Sse2.CompareGreaterThan(vHalf, dot2);
        Vector128<int> ltC0 = Sse2.CompareGreaterThan(vC0, dot2);
        Vector128<int> ltC3 = Sse2.CompareGreaterThan(vC3, dot2);

        int b0 = Sse.MoveMask(ltHalf.AsSingle());
        Vector128<int> bit1Vec = Sse2.Or(Sse2.AndNot(ltC0, ltHalf), Sse2.AndNot(ltHalf, ltC3));
        int b1 = Sse.MoveMask(bit1Vec.AsSingle());

        return SpreadBits8(b0) | (SpreadBits8(b1) << 1);
    }
#endif

    private static void OptimizeColorsBlock(ReadOnlySpan<byte> block, out ushort max16, out ushort min16)
    {
        ReadOnlySpan<uint> px = Pixels(block);
        int rSum, gSum, bSum, rMin, rMax, gMin, gMax, bMin, bMax;
        int sRR, sRG, sRB, sGG, sGB, sBB;
#if NET8_0_OR_GREATER
        if (Avx2.IsSupported)
        {
            Vector256<int> raw0 = Vector256.Create(block.Slice(0, 32)).AsInt32();
            Vector256<int> raw1 = Vector256.Create(block.Slice(32, 32)).AsInt32();
            Vector256<int> m = Vector256.Create(0xFF);
            Vector256<int> r0 = raw0 & m, g0 = Avx2.ShiftRightLogical(raw0, 8) & m, b0 = Avx2.ShiftRightLogical(raw0, 16) & m;
            Vector256<int> r1 = raw1 & m, g1 = Avx2.ShiftRightLogical(raw1, 8) & m, b1 = Avx2.ShiftRightLogical(raw1, 16) & m;
            rSum = Vector256.Sum(r0 + r1);
            gSum = Vector256.Sum(g0 + g1);
            bSum = Vector256.Sum(b0 + b1);
            rMin = MinAcross(Avx2.Min(r0, r1));
            rMax = MaxAcross(Avx2.Max(r0, r1));
            gMin = MinAcross(Avx2.Min(g0, g1));
            gMax = MaxAcross(Avx2.Max(g0, g1));
            bMin = MinAcross(Avx2.Min(b0, b1));
            bMax = MaxAcross(Avx2.Max(b0, b1));
            sRR = Vector256.Sum((r0 * r0) + (r1 * r1));
            sRG = Vector256.Sum((r0 * g0) + (r1 * g1));
            sRB = Vector256.Sum((r0 * b0) + (r1 * b1));
            sGG = Vector256.Sum((g0 * g0) + (g1 * g1));
            sGB = Vector256.Sum((g0 * b0) + (g1 * b1));
            sBB = Vector256.Sum((b0 * b0) + (b1 * b1));
        }
        else if (Sse41.IsSupported || AdvSimd.Arm64.IsSupported)
        {
            Vector128<int> m = Vector128.Create(0xFF);
            Vector128<int> q0 = Vector128.Create(block.Slice(0, 16)).AsInt32();
            Vector128<int> q1 = Vector128.Create(block.Slice(16, 16)).AsInt32();
            Vector128<int> q2 = Vector128.Create(block.Slice(32, 16)).AsInt32();
            Vector128<int> q3 = Vector128.Create(block.Slice(48, 16)).AsInt32();
            Vector128<int> r0 = q0 & m, g0 = Vector128.ShiftRightLogical(q0, 8) & m, b0 = Vector128.ShiftRightLogical(q0, 16) & m;
            Vector128<int> r1 = q1 & m, g1 = Vector128.ShiftRightLogical(q1, 8) & m, b1 = Vector128.ShiftRightLogical(q1, 16) & m;
            Vector128<int> r2 = q2 & m, g2 = Vector128.ShiftRightLogical(q2, 8) & m, b2 = Vector128.ShiftRightLogical(q2, 16) & m;
            Vector128<int> r3 = q3 & m, g3 = Vector128.ShiftRightLogical(q3, 8) & m, b3 = Vector128.ShiftRightLogical(q3, 16) & m;
            rSum = Vector128.Sum(r0 + r1 + r2 + r3);
            gSum = Vector128.Sum(g0 + g1 + g2 + g3);
            bSum = Vector128.Sum(b0 + b1 + b2 + b3);
            rMin = MinAcross(Vector128.Min(Vector128.Min(r0, r1), Vector128.Min(r2, r3)));
            rMax = MaxAcross(Vector128.Max(Vector128.Max(r0, r1), Vector128.Max(r2, r3)));
            gMin = MinAcross(Vector128.Min(Vector128.Min(g0, g1), Vector128.Min(g2, g3)));
            gMax = MaxAcross(Vector128.Max(Vector128.Max(g0, g1), Vector128.Max(g2, g3)));
            bMin = MinAcross(Vector128.Min(Vector128.Min(b0, b1), Vector128.Min(b2, b3)));
            bMax = MaxAcross(Vector128.Max(Vector128.Max(b0, b1), Vector128.Max(b2, b3)));
            sRR = Vector128.Sum((r0 * r0) + (r1 * r1) + (r2 * r2) + (r3 * r3));
            sRG = Vector128.Sum((r0 * g0) + (r1 * g1) + (r2 * g2) + (r3 * g3));
            sRB = Vector128.Sum((r0 * b0) + (r1 * b1) + (r2 * b2) + (r3 * b3));
            sGG = Vector128.Sum((g0 * g0) + (g1 * g1) + (g2 * g2) + (g3 * g3));
            sGB = Vector128.Sum((g0 * b0) + (g1 * b1) + (g2 * b2) + (g3 * b3));
            sBB = Vector128.Sum((b0 * b0) + (b1 * b1) + (b2 * b2) + (b3 * b3));
        }
        else
#endif
        {
            rSum = gSum = bSum = 0;
            rMin = gMin = bMin = 255;
            rMax = gMax = bMax = 0;
            sRR = sRG = sRB = sGG = sGB = sBB = 0;
            foreach (uint p in px)
            {
                int r = (int)(p & 0xFF);
                int g = (int)((p >> 8) & 0xFF);
                int b = (int)((p >> 16) & 0xFF);
                rSum += r;
                gSum += g;
                bSum += b;
                rMin = Math.Min(rMin, r);
                rMax = Math.Max(rMax, r);
                gMin = Math.Min(gMin, g);
                gMax = Math.Max(gMax, g);
                bMin = Math.Min(bMin, b);
                bMax = Math.Max(bMax, b);
                sRR += r * r;
                sRG += r * g;
                sRB += r * b;
                sGG += g * g;
                sGB += g * b;
                sBB += b * b;
            }
        }

        int muR = (rSum + 8) >> 4;
        int muG = (gSum + 8) >> 4;
        int muB = (bSum + 8) >> 4;

        // sum((x - mx)(y - my)) over 16 pixels, expanded; exact in integers, so equal to stb's two-pass form.
        int cov0 = sRR - (2 * muR * rSum) + (16 * muR * muR);
        int cov1 = sRG - (muG * rSum) - (muR * gSum) + (16 * muR * muG);
        int cov2 = sRB - (muB * rSum) - (muR * bSum) + (16 * muR * muB);
        int cov3 = sGG - (2 * muG * gSum) + (16 * muG * muG);
        int cov4 = sGB - (muB * gSum) - (muG * bSum) + (16 * muG * muB);
        int cov5 = sBB - (2 * muB * bSum) + (16 * muB * muB);

        const float Inv255 = 1.0f / 255.0f;
        float covf0 = cov0 * Inv255;
        float covf1 = cov1 * Inv255;
        float covf2 = cov2 * Inv255;
        float covf3 = cov3 * Inv255;
        float covf4 = cov4 * Inv255;
        float covf5 = cov5 * Inv255;

        float vfr = rMax - rMin;
        float vfg = gMax - gMin;
        float vfb = bMax - bMin;

        for (int iter = 0; iter < 4; ++iter)
        {
            float r = vfr * covf0 + vfg * covf1 + vfb * covf2;
            float g = vfr * covf1 + vfg * covf3 + vfb * covf4;
            float b = vfr * covf2 + vfg * covf4 + vfb * covf5;
            vfr = r;
            vfg = g;
            vfb = b;
        }

        double magn = Math.Abs((double)vfr);
        double absG = Math.Abs((double)vfg);
        double absB = Math.Abs((double)vfb);
        if (absG > magn) magn = absG;
        if (absB > magn) magn = absB;

        int vr, vg, vb;
        if (magn < 4.0)
        {
            vr = 299;
            vg = 587;
            vb = 114;
        }
        else
        {
            magn = 512.0 / magn;
            vr = (int)(vfr * magn);
            vg = (int)(vfg * magn);
            vb = (int)(vfb * magn);
        }

        uint minPx = px[0];
        uint maxPx = minPx;
        int minD = Dot(minPx, vr, vg, vb);
        int maxD = minD;

        for (int i = 1; i < px.Length; i++)
        {
            uint p = px[i];
            int dot = Dot(p, vr, vg, vb);
            if (dot < minD)
            {
                minD = dot;
                minPx = p;
            }

            if (dot > maxD)
            {
                maxD = dot;
                maxPx = p;
            }
        }

        max16 = As16Bit((int)(maxPx & 0xFF), (int)((maxPx >> 8) & 0xFF), (int)((maxPx >> 16) & 0xFF));
        min16 = As16Bit((int)(minPx & 0xFF), (int)((minPx >> 8) & 0xFF), (int)((minPx >> 16) & 0xFF));
    }

#if NET8_0_OR_GREATER
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int MinAcross(Vector128<int> x)
    {
        if (AdvSimd.Arm64.IsSupported)
        {
            return AdvSimd.Arm64.MinAcross(x).ToScalar();
        }

        x = Sse41.Min(x, Sse2.Shuffle(x, 0x4E));
        return Sse41.Min(x, Sse2.Shuffle(x, 0xB1)).ToScalar();
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int MaxAcross(Vector128<int> x)
    {
        if (AdvSimd.Arm64.IsSupported)
        {
            return AdvSimd.Arm64.MaxAcross(x).ToScalar();
        }

        x = Sse41.Max(x, Sse2.Shuffle(x, 0x4E));
        return Sse41.Max(x, Sse2.Shuffle(x, 0xB1)).ToScalar();
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int MinAcross(Vector256<int> v)
    {
        Vector128<int> x = Sse41.Min(v.GetLower(), v.GetUpper());
        x = Sse41.Min(x, Sse2.Shuffle(x, 0x4E));
        return Sse41.Min(x, Sse2.Shuffle(x, 0xB1)).ToScalar();
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int MaxAcross(Vector256<int> v)
    {
        Vector128<int> x = Sse41.Max(v.GetLower(), v.GetUpper());
        x = Sse41.Max(x, Sse2.Shuffle(x, 0x4E));
        return Sse41.Max(x, Sse2.Shuffle(x, 0xB1)).ToScalar();
    }
#endif

    private static bool RefineBlock(ReadOnlySpan<byte> block, ref ushort pMax16, ref ushort pMin16, uint mask)
    {
        ushort oldMin = pMin16;
        ushort oldMax = pMax16;
        ushort min16;
        ushort max16;

        if ((mask ^ (mask << 2)) < 4u)
        {
            int r = 8;
            int g = 8;
            int b = 8;
            foreach (uint p in Pixels(block))
            {
                r += (int)(p & 0xFF);
                g += (int)((p >> 8) & 0xFF);
                b += (int)((p >> 16) & 0xFF);
            }

            r >>= 4;
            g >>= 4;
            b >>= 4;

            max16 = (ushort)((DxtTables.OMatch5[r * 2] << 11)
                | (DxtTables.OMatch6[g * 2] << 5)
                | DxtTables.OMatch5[b * 2]);
            min16 = (ushort)((DxtTables.OMatch5[r * 2 + 1] << 11)
                | (DxtTables.OMatch6[g * 2 + 1] << 5)
                | DxtTables.OMatch5[b * 2 + 1]);
        }
        else
        {
            int akku = 0;
            int at1R = 0, at1G = 0, at1B = 0;
            int at2R = 0, at2G = 0, at2B = 0;
            uint cm = mask;

            foreach (uint p in Pixels(block))
            {
                int step = (int)(cm & 3u);
                int w1;
                int prod;
                if (step == 0) { w1 = 3; prod = 0x090000; }
                else if (step == 1) { w1 = 0; prod = 0x000900; }
                else if (step == 2) { w1 = 2; prod = 0x040102; }
                else { w1 = 1; prod = 0x010402; }

                int r = (int)(p & 0xFF);
                int g = (int)((p >> 8) & 0xFF);
                int b = (int)((p >> 16) & 0xFF);
                cm >>= 2;

                akku += prod;
                at1R += w1 * r;
                at1G += w1 * g;
                at1B += w1 * b;
                at2R += r;
                at2G += g;
                at2B += b;
            }

            at2R = 3 * at2R - at1R;
            at2G = 3 * at2G - at1G;
            at2B = 3 * at2B - at1B;

            int xx = akku >> 16;
            int yy = (akku >> 8) & 0xFF;
            int xy = akku & 0xFF;

            float f = 3.0f / 255.0f / (xx * yy - xy * xy);

            max16 = (ushort)((Quantize5((at1R * yy - at2R * xy) * f) << 11)
                | (Quantize6((at1G * yy - at2G * xy) * f) << 5)
                | Quantize5((at1B * yy - at2B * xy) * f));

            min16 = (ushort)((Quantize5((at2R * xx - at1R * xy) * f) << 11)
                | (Quantize6((at2G * xx - at1G * xy) * f) << 5)
                | Quantize5((at2B * xx - at1B * xy) * f));
        }

        pMin16 = min16;
        pMax16 = max16;
        return oldMin != min16 || oldMax != max16;
    }

    // A 4x4 RGBA block viewed as 16 little-endian pixels (R in the low byte); bounds are checked once here.
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static ReadOnlySpan<uint> Pixels(ReadOnlySpan<byte> block) =>
        MemoryMarshal.Cast<byte, uint>(block.Slice(0, 64));

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int Dot(uint p, int vr, int vg, int vb) =>
        (int)(p & 0xFF) * vr + (int)((p >> 8) & 0xFF) * vg + (int)((p >> 16) & 0xFF) * vb;

    // Moves bit i of an 8-bit value to bit 2*i (same result as the former 256-entry table).
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static uint SpreadBits8(int v)
    {
        uint x = (uint)v & 0xFF;
        x = (x | (x << 4)) & 0x0F0F;
        x = (x | (x << 2)) & 0x3333;
        return (x | (x << 1)) & 0x5555;
    }
}
