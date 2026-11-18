using System;
using System.Runtime.CompilerServices;
#if NET8_0_OR_GREATER
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.Arm;
using System.Runtime.Intrinsics.X86;
#endif

namespace Stb.Managed.ImageWrite.Internal;

/// <summary>
/// Safe span SIMD PNG row-filtering kernels with fused signed-byte SAD computation
/// (<c>sum(abs((sbyte)filtered[i]))</c>). Because PNG encoding depends only on raw source rows
/// (<c>cur</c> and <c>prev</c>), all five filter types including Sub, Average, and Paeth are
/// completely data-parallel across 16-32 bytes per iteration with zero unsafe code.
/// </summary>
internal static class PngFilterKernels
{
    /// <summary>
    /// Encodes a single PNG scanline using <paramref name="filterType"/> into <paramref name="dst"/>
    /// and returns its exact signed-byte SAD score (<c>sum(abs((sbyte)dst[i]))</c>).
    /// </summary>
    internal static int EncodeLineAndSad(
        ReadOnlySpan<byte> cur,
        ReadOnlySpan<byte> prev,
        bool isFirstRow,
        int bpp,
        int filterType,
        Span<byte> dst)
    {
        if (filterType == 0 || (isFirstRow && filterType == 2))
        {
            cur.CopyTo(dst);
            return ComputeSignedAbsSum(cur);
        }

        if (isFirstRow)
        {
            return filterType == 3
                ? FilterFirstRowAverageAndSad(cur, bpp, dst)
                : FilterSubAndSad(cur, bpp, dst);
        }

        return filterType switch
        {
            1 => FilterSubAndSad(cur, bpp, dst),
            2 => FilterUpAndSad(cur, prev, dst),
            3 => FilterAverageAndSad(cur, prev, bpp, dst),
            _ => FilterPaethAndSad(cur, prev, bpp, dst),
        };
    }

    /// <summary>
    /// Encodes a single PNG scanline using <paramref name="filterType"/> into <paramref name="dst"/>
    /// without needing the SAD score (used for explicit <see cref="PngFilterMode"/>).
    /// </summary>
    internal static void EncodeLine(
        ReadOnlySpan<byte> cur,
        ReadOnlySpan<byte> prev,
        bool isFirstRow,
        int bpp,
        int filterType,
        Span<byte> dst)
    {
        _ = EncodeLineAndSad(cur, prev, isFirstRow, bpp, filterType, dst);
    }

    internal static int ComputeSignedAbsSum(ReadOnlySpan<byte> line)
    {
        int len = line.Length;
        int i = 0;
        int sum = 0;
#if NET8_0_OR_GREATER
        if (Avx2.IsSupported && len >= 32)
        {
            Vector256<byte> zero = Vector256<byte>.Zero;
            Vector256<ulong> acc = Vector256<ulong>.Zero;
            int limit = len - 31;
            for (; i < limit; i += 32)
            {
                Vector256<byte> v = Vector256.Create(line.Slice(i, 32));
                Vector256<byte> abs = Avx2.Abs(v.AsSByte());
                acc = Avx2.Add(acc, Avx2.SumAbsoluteDifferences(abs, zero).AsUInt64());
            }

            sum += SumUInt64Lanes(acc);
        }

        if (Sse2.IsSupported && len - i >= 16)
        {
            Vector128<byte> zero = Vector128<byte>.Zero;
            Vector128<ulong> acc = Vector128<ulong>.Zero;
            int limit = len - 15;
            for (; i < limit; i += 16)
            {
                Vector128<byte> v = Vector128.Create(line.Slice(i, 16));
                Vector128<byte> abs = Sse2.Min(v, Sse2.Subtract(zero, v));
                acc = Sse2.Add(acc, Sse2.SumAbsoluteDifferences(abs, zero).AsUInt64());
            }

            sum += SumUInt64Lanes(acc);
        }
        else if (AdvSimd.Arm64.IsSupported && len - i >= 16)
        {
            Vector128<uint> acc = Vector128<uint>.Zero;
            int limit = len - 15;
            for (; i < limit; i += 16)
            {
                Vector128<byte> v = Vector128.Create(line.Slice(i, 16));
                Vector128<byte> abs = AdvSimd.Abs(v.AsSByte()).AsByte();
                Vector128<ushort> p16 = AdvSimd.AddPairwiseWidening(abs);
                acc = AdvSimd.AddPairwiseWideningAndAdd(acc, p16);
            }

            sum += SumUInt32Lanes(acc);
        }
#endif
        for (; i < len; i++)
        {
            sum += Math.Abs((int)(sbyte)line[i]);
        }

        return sum;
    }

    private static int FilterSubAndSad(ReadOnlySpan<byte> cur, int bpp, Span<byte> dst)
    {
        int len = cur.Length;
        int sum = 0;
        for (int k = 0; k < bpp; k++)
        {
            byte v = cur[k];
            dst[k] = v;
            sum += Math.Abs((int)(sbyte)v);
        }

        int i = bpp;
#if NET8_0_OR_GREATER
        if (Avx2.IsSupported && len - i >= 32)
        {
            Vector256<byte> zero = Vector256<byte>.Zero;
            Vector256<ulong> acc = Vector256<ulong>.Zero;
            int limit = len - 31;
            for (; i < limit; i += 32)
            {
                Vector256<byte> vc = Vector256.Create(cur.Slice(i, 32));
                Vector256<byte> va = Vector256.Create(cur.Slice(i - bpp, 32));
                Vector256<byte> diff = Avx2.Subtract(vc, va);
                diff.CopyTo(dst.Slice(i, 32));
                acc = Avx2.Add(acc, Avx2.SumAbsoluteDifferences(Avx2.Abs(diff.AsSByte()), zero).AsUInt64());
            }

            sum += SumUInt64Lanes(acc);
        }

        if (Sse2.IsSupported && len - i >= 16)
        {
            Vector128<byte> zero = Vector128<byte>.Zero;
            Vector128<ulong> acc = Vector128<ulong>.Zero;
            int limit = len - 15;
            for (; i < limit; i += 16)
            {
                Vector128<byte> vc = Vector128.Create(cur.Slice(i, 16));
                Vector128<byte> va = Vector128.Create(cur.Slice(i - bpp, 16));
                Vector128<byte> diff = Sse2.Subtract(vc, va);
                diff.CopyTo(dst.Slice(i, 16));
                Vector128<byte> abs = Sse2.Min(diff, Sse2.Subtract(zero, diff));
                acc = Sse2.Add(acc, Sse2.SumAbsoluteDifferences(abs, zero).AsUInt64());
            }

            sum += SumUInt64Lanes(acc);
        }
        else if (AdvSimd.Arm64.IsSupported && len - i >= 16)
        {
            Vector128<uint> acc = Vector128<uint>.Zero;
            int limit = len - 15;
            for (; i < limit; i += 16)
            {
                Vector128<byte> vc = Vector128.Create(cur.Slice(i, 16));
                Vector128<byte> va = Vector128.Create(cur.Slice(i - bpp, 16));
                Vector128<byte> diff = AdvSimd.Subtract(vc, va);
                diff.CopyTo(dst.Slice(i, 16));
                Vector128<byte> abs = AdvSimd.Abs(diff.AsSByte()).AsByte();
                acc = AdvSimd.AddPairwiseWideningAndAdd(acc, AdvSimd.AddPairwiseWidening(abs));
            }

            sum += SumUInt32Lanes(acc);
        }
#endif
        for (; i < len; i++)
        {
            byte d = unchecked((byte)(cur[i] - cur[i - bpp]));
            dst[i] = d;
            sum += Math.Abs((int)(sbyte)d);
        }

        return sum;
    }

    private static int FilterFirstRowAverageAndSad(ReadOnlySpan<byte> cur, int bpp, Span<byte> dst)
    {
        int len = cur.Length;
        int sum = 0;
        for (int k = 0; k < bpp; k++)
        {
            byte v = cur[k];
            dst[k] = v;
            sum += Math.Abs((int)(sbyte)v);
        }

        int i = bpp;
#if NET8_0_OR_GREATER
        if (Avx2.IsSupported && len - i >= 32)
        {
            Vector256<byte> zero = Vector256<byte>.Zero;
            Vector256<byte> mask7F = Vector256.Create((byte)0x7F);
            Vector256<ulong> acc = Vector256<ulong>.Zero;
            int limit = len - 31;
            for (; i < limit; i += 32)
            {
                Vector256<byte> vc = Vector256.Create(cur.Slice(i, 32));
                Vector256<byte> va = Vector256.Create(cur.Slice(i - bpp, 32));
                Vector256<byte> pred = Avx2.And(Avx2.ShiftRightLogical(va.AsUInt16(), 1).AsByte(), mask7F);
                Vector256<byte> diff = Avx2.Subtract(vc, pred);
                diff.CopyTo(dst.Slice(i, 32));
                acc = Avx2.Add(acc, Avx2.SumAbsoluteDifferences(Avx2.Abs(diff.AsSByte()), zero).AsUInt64());
            }

            sum += SumUInt64Lanes(acc);
        }

        if (Sse2.IsSupported && len - i >= 16)
        {
            Vector128<byte> zero = Vector128<byte>.Zero;
            Vector128<byte> mask7F = Vector128.Create((byte)0x7F);
            Vector128<ulong> acc = Vector128<ulong>.Zero;
            int limit = len - 15;
            for (; i < limit; i += 16)
            {
                Vector128<byte> vc = Vector128.Create(cur.Slice(i, 16));
                Vector128<byte> va = Vector128.Create(cur.Slice(i - bpp, 16));
                Vector128<byte> pred = Sse2.And(Sse2.ShiftRightLogical(va.AsUInt16(), 1).AsByte(), mask7F);
                Vector128<byte> diff = Sse2.Subtract(vc, pred);
                diff.CopyTo(dst.Slice(i, 16));
                Vector128<byte> abs = Sse2.Min(diff, Sse2.Subtract(zero, diff));
                acc = Sse2.Add(acc, Sse2.SumAbsoluteDifferences(abs, zero).AsUInt64());
            }

            sum += SumUInt64Lanes(acc);
        }
        else if (AdvSimd.Arm64.IsSupported && len - i >= 16)
        {
            Vector128<uint> acc = Vector128<uint>.Zero;
            int limit = len - 15;
            for (; i < limit; i += 16)
            {
                Vector128<byte> vc = Vector128.Create(cur.Slice(i, 16));
                Vector128<byte> va = Vector128.Create(cur.Slice(i - bpp, 16));
                Vector128<byte> pred = AdvSimd.ShiftRightLogical(va, 1);
                Vector128<byte> diff = AdvSimd.Subtract(vc, pred);
                diff.CopyTo(dst.Slice(i, 16));
                Vector128<byte> abs = AdvSimd.Abs(diff.AsSByte()).AsByte();
                acc = AdvSimd.AddPairwiseWideningAndAdd(acc, AdvSimd.AddPairwiseWidening(abs));
            }

            sum += SumUInt32Lanes(acc);
        }
#endif
        for (; i < len; i++)
        {
            byte d = unchecked((byte)(cur[i] - (cur[i - bpp] >> 1)));
            dst[i] = d;
            sum += Math.Abs((int)(sbyte)d);
        }

        return sum;
    }

    private static int FilterUpAndSad(ReadOnlySpan<byte> cur, ReadOnlySpan<byte> prev, Span<byte> dst)
    {
        int len = cur.Length;
        int i = 0;
        int sum = 0;
#if NET8_0_OR_GREATER
        if (Avx2.IsSupported && len >= 32)
        {
            Vector256<byte> zero = Vector256<byte>.Zero;
            Vector256<ulong> acc = Vector256<ulong>.Zero;
            int limit = len - 31;
            for (; i < limit; i += 32)
            {
                Vector256<byte> vc = Vector256.Create(cur.Slice(i, 32));
                Vector256<byte> vb = Vector256.Create(prev.Slice(i, 32));
                Vector256<byte> diff = Avx2.Subtract(vc, vb);
                diff.CopyTo(dst.Slice(i, 32));
                acc = Avx2.Add(acc, Avx2.SumAbsoluteDifferences(Avx2.Abs(diff.AsSByte()), zero).AsUInt64());
            }

            sum += SumUInt64Lanes(acc);
        }

        if (Sse2.IsSupported && len - i >= 16)
        {
            Vector128<byte> zero = Vector128<byte>.Zero;
            Vector128<ulong> acc = Vector128<ulong>.Zero;
            int limit = len - 15;
            for (; i < limit; i += 16)
            {
                Vector128<byte> vc = Vector128.Create(cur.Slice(i, 16));
                Vector128<byte> vb = Vector128.Create(prev.Slice(i, 16));
                Vector128<byte> diff = Sse2.Subtract(vc, vb);
                diff.CopyTo(dst.Slice(i, 16));
                Vector128<byte> abs = Sse2.Min(diff, Sse2.Subtract(zero, diff));
                acc = Sse2.Add(acc, Sse2.SumAbsoluteDifferences(abs, zero).AsUInt64());
            }

            sum += SumUInt64Lanes(acc);
        }
        else if (AdvSimd.Arm64.IsSupported && len - i >= 16)
        {
            Vector128<uint> acc = Vector128<uint>.Zero;
            int limit = len - 15;
            for (; i < limit; i += 16)
            {
                Vector128<byte> vc = Vector128.Create(cur.Slice(i, 16));
                Vector128<byte> vb = Vector128.Create(prev.Slice(i, 16));
                Vector128<byte> diff = AdvSimd.Subtract(vc, vb);
                diff.CopyTo(dst.Slice(i, 16));
                Vector128<byte> abs = AdvSimd.Abs(diff.AsSByte()).AsByte();
                acc = AdvSimd.AddPairwiseWideningAndAdd(acc, AdvSimd.AddPairwiseWidening(abs));
            }

            sum += SumUInt32Lanes(acc);
        }
#endif
        for (; i < len; i++)
        {
            byte d = unchecked((byte)(cur[i] - prev[i]));
            dst[i] = d;
            sum += Math.Abs((int)(sbyte)d);
        }

        return sum;
    }

    private static int FilterAverageAndSad(ReadOnlySpan<byte> cur, ReadOnlySpan<byte> prev, int bpp, Span<byte> dst)
    {
        int len = cur.Length;
        int sum = 0;
        for (int k = 0; k < bpp; k++)
        {
            byte d = unchecked((byte)(cur[k] - (prev[k] >> 1)));
            dst[k] = d;
            sum += Math.Abs((int)(sbyte)d);
        }

        int i = bpp;
#if NET8_0_OR_GREATER
        if (Avx2.IsSupported && len - i >= 32)
        {
            Vector256<byte> zero = Vector256<byte>.Zero;
            Vector256<byte> mask7F = Vector256.Create((byte)0x7F);
            Vector256<ulong> acc = Vector256<ulong>.Zero;
            int limit = len - 31;
            for (; i < limit; i += 32)
            {
                Vector256<byte> vc = Vector256.Create(cur.Slice(i, 32));
                Vector256<byte> va = Vector256.Create(cur.Slice(i - bpp, 32));
                Vector256<byte> vb = Vector256.Create(prev.Slice(i, 32));
                // Exact floor-average (a + b) >> 1 without widening: (a & b) + (((a ^ b) >> 1) & 0x7F)
                Vector256<byte> xorHalf = Avx2.And(Avx2.ShiftRightLogical(Avx2.Xor(va, vb).AsUInt16(), 1).AsByte(), mask7F);
                Vector256<byte> pred = Avx2.Add(Avx2.And(va, vb), xorHalf);
                Vector256<byte> diff = Avx2.Subtract(vc, pred);
                diff.CopyTo(dst.Slice(i, 32));
                acc = Avx2.Add(acc, Avx2.SumAbsoluteDifferences(Avx2.Abs(diff.AsSByte()), zero).AsUInt64());
            }

            sum += SumUInt64Lanes(acc);
        }

        if (Sse2.IsSupported && len - i >= 16)
        {
            Vector128<byte> zero = Vector128<byte>.Zero;
            Vector128<byte> mask7F = Vector128.Create((byte)0x7F);
            Vector128<ulong> acc = Vector128<ulong>.Zero;
            int limit = len - 15;
            for (; i < limit; i += 16)
            {
                Vector128<byte> vc = Vector128.Create(cur.Slice(i, 16));
                Vector128<byte> va = Vector128.Create(cur.Slice(i - bpp, 16));
                Vector128<byte> vb = Vector128.Create(prev.Slice(i, 16));
                Vector128<byte> xorHalf = Sse2.And(Sse2.ShiftRightLogical(Sse2.Xor(va, vb).AsUInt16(), 1).AsByte(), mask7F);
                Vector128<byte> pred = Sse2.Add(Sse2.And(va, vb), xorHalf);
                Vector128<byte> diff = Sse2.Subtract(vc, pred);
                diff.CopyTo(dst.Slice(i, 16));
                Vector128<byte> abs = Sse2.Min(diff, Sse2.Subtract(zero, diff));
                acc = Sse2.Add(acc, Sse2.SumAbsoluteDifferences(abs, zero).AsUInt64());
            }

            sum += SumUInt64Lanes(acc);
        }
        else if (AdvSimd.Arm64.IsSupported && len - i >= 16)
        {
            Vector128<uint> acc = Vector128<uint>.Zero;
            int limit = len - 15;
            for (; i < limit; i += 16)
            {
                Vector128<byte> vc = Vector128.Create(cur.Slice(i, 16));
                Vector128<byte> va = Vector128.Create(cur.Slice(i - bpp, 16));
                Vector128<byte> vb = Vector128.Create(prev.Slice(i, 16));
                // Exact unsigned floor average (a + b) >> 1: (a & b) + ((a ^ b) >> 1)
                Vector128<byte> pred = AdvSimd.Add(
                    AdvSimd.And(va, vb),
                    AdvSimd.ShiftRightLogical(AdvSimd.Xor(va, vb), 1));
                Vector128<byte> diff = AdvSimd.Subtract(vc, pred);
                diff.CopyTo(dst.Slice(i, 16));
                Vector128<byte> abs = AdvSimd.Abs(diff.AsSByte()).AsByte();
                acc = AdvSimd.AddPairwiseWideningAndAdd(acc, AdvSimd.AddPairwiseWidening(abs));
            }

            sum += SumUInt32Lanes(acc);
        }
#endif
        for (; i < len; i++)
        {
            byte d = unchecked((byte)(cur[i] - ((cur[i - bpp] + prev[i]) >> 1)));
            dst[i] = d;
            sum += Math.Abs((int)(sbyte)d);
        }

        return sum;
    }

    private static int FilterPaethAndSad(ReadOnlySpan<byte> cur, ReadOnlySpan<byte> prev, int bpp, Span<byte> dst)
    {
        int len = cur.Length;
        int sum = 0;
        for (int k = 0; k < bpp; k++)
        {
            byte d = unchecked((byte)(cur[k] - prev[k]));
            dst[k] = d;
            sum += Math.Abs((int)(sbyte)d);
        }

        int i = bpp;
#if NET8_0_OR_GREATER
        if (Avx2.IsSupported && len - i >= 32)
        {
            Vector256<byte> zero = Vector256<byte>.Zero;
            Vector256<ulong> acc = Vector256<ulong>.Zero;
            int limit = len - 31;
            for (; i < limit; i += 32)
            {
                Vector256<byte> vc = Vector256.Create(cur.Slice(i, 32));
                Vector256<byte> vaBytes = Vector256.Create(cur.Slice(i - bpp, 32));
                Vector256<byte> vbBytes = Vector256.Create(prev.Slice(i, 32));
                Vector256<byte> vcBytes = Vector256.Create(prev.Slice(i - bpp, 32));

                Vector256<short> predLo = Paeth16Avx2(vaBytes.GetLower(), vbBytes.GetLower(), vcBytes.GetLower());
                Vector256<short> predHi = Paeth16Avx2(vaBytes.GetUpper(), vbBytes.GetUpper(), vcBytes.GetUpper());
                Vector256<byte> pred = Avx2.Permute4x64(
                    Avx2.PackUnsignedSaturate(predLo, predHi).AsUInt64(),
                    0xD8).AsByte();

                Vector256<byte> diff = Avx2.Subtract(vc, pred);
                diff.CopyTo(dst.Slice(i, 32));
                acc = Avx2.Add(acc, Avx2.SumAbsoluteDifferences(Avx2.Abs(diff.AsSByte()), zero).AsUInt64());
            }

            sum += SumUInt64Lanes(acc);
        }

        if (Sse2.IsSupported && len - i >= 16)
        {
            Vector128<byte> zero = Vector128<byte>.Zero;
            Vector128<ulong> acc = Vector128<ulong>.Zero;
            int limit = len - 15;
            for (; i < limit; i += 16)
            {
                Vector128<byte> vc = Vector128.Create(cur.Slice(i, 16));
                Vector128<byte> vaBytes = Vector128.Create(cur.Slice(i - bpp, 16));
                Vector128<byte> vbBytes = Vector128.Create(prev.Slice(i, 16));
                Vector128<byte> vcBytes = Vector128.Create(prev.Slice(i - bpp, 16));

                Vector128<short> predLo = Paeth8Sse2(
                    Sse2.UnpackLow(vaBytes, zero).AsInt16(),
                    Sse2.UnpackLow(vbBytes, zero).AsInt16(),
                    Sse2.UnpackLow(vcBytes, zero).AsInt16());
                Vector128<short> predHi = Paeth8Sse2(
                    Sse2.UnpackHigh(vaBytes, zero).AsInt16(),
                    Sse2.UnpackHigh(vbBytes, zero).AsInt16(),
                    Sse2.UnpackHigh(vcBytes, zero).AsInt16());
                Vector128<byte> pred = Sse2.PackUnsignedSaturate(predLo, predHi);

                Vector128<byte> diff = Sse2.Subtract(vc, pred);
                diff.CopyTo(dst.Slice(i, 16));
                Vector128<byte> abs = Sse2.Min(diff, Sse2.Subtract(zero, diff));
                acc = Sse2.Add(acc, Sse2.SumAbsoluteDifferences(abs, zero).AsUInt64());
            }

            sum += SumUInt64Lanes(acc);
        }
        else if (AdvSimd.Arm64.IsSupported && len - i >= 16)
        {
            Vector128<uint> acc = Vector128<uint>.Zero;
            int limit = len - 15;
            for (; i < limit; i += 16)
            {
                Vector128<byte> vc = Vector128.Create(cur.Slice(i, 16));
                Vector128<byte> vaBytes = Vector128.Create(cur.Slice(i - bpp, 16));
                Vector128<byte> vbBytes = Vector128.Create(prev.Slice(i, 16));
                Vector128<byte> vcBytes = Vector128.Create(prev.Slice(i - bpp, 16));

                Vector128<short> predLo = Paeth8Arm64(
                    AdvSimd.ZeroExtendWideningLower(vaBytes.GetLower()).AsInt16(),
                    AdvSimd.ZeroExtendWideningLower(vbBytes.GetLower()).AsInt16(),
                    AdvSimd.ZeroExtendWideningLower(vcBytes.GetLower()).AsInt16());
                Vector128<short> predHi = Paeth8Arm64(
                    AdvSimd.ZeroExtendWideningLower(vaBytes.GetUpper()).AsInt16(),
                    AdvSimd.ZeroExtendWideningLower(vbBytes.GetUpper()).AsInt16(),
                    AdvSimd.ZeroExtendWideningLower(vcBytes.GetUpper()).AsInt16());

                Vector64<byte> pLo = AdvSimd.ExtractNarrowingLower(predLo.AsUInt16());
                Vector64<byte> pHi = AdvSimd.ExtractNarrowingLower(predHi.AsUInt16());
                Vector128<byte> pred = Vector128.Create(pLo, pHi);

                Vector128<byte> diff = AdvSimd.Subtract(vc, pred);
                diff.CopyTo(dst.Slice(i, 16));
                Vector128<byte> abs = AdvSimd.Abs(diff.AsSByte()).AsByte();
                acc = AdvSimd.AddPairwiseWideningAndAdd(acc, AdvSimd.AddPairwiseWidening(abs));
            }

            sum += SumUInt32Lanes(acc);
        }
#endif
        for (; i < len; i++)
        {
            int a = cur[i - bpp];
            int b = prev[i];
            int c = prev[i - bpp];
            int p = a + b - c;
            int pa = Math.Abs(p - a);
            int pb = Math.Abs(p - b);
            int pc = Math.Abs(p - c);
            int pred = (pa <= pb && pa <= pc) ? a : (pb <= pc ? b : c);
            byte d = unchecked((byte)(cur[i] - pred));
            dst[i] = d;
            sum += Math.Abs((int)(sbyte)d);
        }

        return sum;
    }

#if NET8_0_OR_GREATER
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector256<short> Paeth16Avx2(Vector128<byte> aBytes, Vector128<byte> bBytes, Vector128<byte> cBytes)
    {
        Vector256<short> a = Avx2.ConvertToVector256Int16(aBytes);
        Vector256<short> b = Avx2.ConvertToVector256Int16(bBytes);
        Vector256<short> c = Avx2.ConvertToVector256Int16(cBytes);

        Vector256<short> dBc = Avx2.Subtract(b, c);
        Vector256<short> dAc = Avx2.Subtract(a, c);
        Vector256<short> pa = Avx2.Abs(dBc).AsInt16();
        Vector256<short> pb = Avx2.Abs(dAc).AsInt16();
        Vector256<short> pc = Avx2.Abs(Avx2.Add(dBc, dAc)).AsInt16();

        Vector256<short> notA = Avx2.Or(Avx2.CompareGreaterThan(pa, pb), Avx2.CompareGreaterThan(pa, pc));
        Vector256<short> notB = Avx2.CompareGreaterThan(pb, pc);
        Vector256<byte> bc = Avx2.BlendVariable(b.AsByte(), c.AsByte(), notB.AsByte());
        return Avx2.BlendVariable(a.AsByte(), bc, notA.AsByte()).AsInt16();
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector128<short> Paeth8Sse2(Vector128<short> a, Vector128<short> b, Vector128<short> c)
    {
        Vector128<short> zero = Vector128<short>.Zero;
        Vector128<short> dBc = Sse2.Subtract(b, c);
        Vector128<short> dAc = Sse2.Subtract(a, c);
        Vector128<short> dSum = Sse2.Add(dBc, dAc);

        Vector128<short> pa = Sse2.Max(dBc, Sse2.Subtract(zero, dBc));
        Vector128<short> pb = Sse2.Max(dAc, Sse2.Subtract(zero, dAc));
        Vector128<short> pc = Sse2.Max(dSum, Sse2.Subtract(zero, dSum));

        Vector128<short> notA = Sse2.Or(Sse2.CompareGreaterThan(pa, pb), Sse2.CompareGreaterThan(pa, pc));
        Vector128<short> notB = Sse2.CompareGreaterThan(pb, pc);
        Vector128<short> bc = Sse2.Or(Sse2.AndNot(notB, b), Sse2.And(notB, c));
        return Sse2.Or(Sse2.AndNot(notA, a), Sse2.And(notA, bc));
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector128<short> Paeth8Arm64(Vector128<short> a, Vector128<short> b, Vector128<short> c)
    {
        Vector128<short> dBc = AdvSimd.Subtract(b, c);
        Vector128<short> dAc = AdvSimd.Subtract(a, c);
        Vector128<ushort> pa = AdvSimd.Abs(dBc);
        Vector128<ushort> pb = AdvSimd.Abs(dAc);
        Vector128<ushort> pc = AdvSimd.Abs(AdvSimd.Add(dBc, dAc));

        Vector128<short> notA = AdvSimd.Or(AdvSimd.CompareGreaterThan(pa, pb), AdvSimd.CompareGreaterThan(pa, pc)).AsInt16();
        Vector128<short> notB = AdvSimd.CompareGreaterThan(pb, pc).AsInt16();
        Vector128<short> bc = AdvSimd.BitwiseSelect(notB, c, b);
        return AdvSimd.BitwiseSelect(notA, bc, a);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int SumUInt64Lanes(Vector256<ulong> v) =>
        (int)(v.GetElement(0) + v.GetElement(1) + v.GetElement(2) + v.GetElement(3));

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int SumUInt64Lanes(Vector128<ulong> v) =>
        (int)(v.GetElement(0) + v.GetElement(1));

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int SumUInt32Lanes(Vector128<uint> v) =>
        (int)(v.GetElement(0) + v.GetElement(1) + v.GetElement(2) + v.GetElement(3));
#endif
}
