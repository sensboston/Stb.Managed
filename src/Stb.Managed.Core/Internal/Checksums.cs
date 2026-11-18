using System.Buffers.Binary;
#if NET8_0_OR_GREATER
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.Arm;
using System.Runtime.Intrinsics.X86;
#endif

namespace Stb.Managed.Internal;

/// <summary>
/// Zero-dependency CRC-32 (ISO 3309 / PNG) and Adler-32 (RFC 1950 / zlib) checksum implementations
/// with hardware-accelerated SIMD paths and permanent scalar reference implementations.
/// </summary>
internal static class Checksums
{
    private const uint ModAdler = 65521u;

    // NMAX is the largest n such that 255*n*(n+1)/2 + (n+1)*(BASE-1) <= 2^32-1.
    private const int AdlerNMax = 5552;

    // Slicing-by-8 CRC-32 tables (8 x 256 = 2048 uints = 8 KB, fits in L1 cache).
    private static readonly uint[] Crc32SliceTables = BuildCrc32SliceTables();

    private static uint[] BuildCrc32SliceTables()
    {
        uint[] tables = new uint[8 * 256];
        for (uint n = 0; n < 256; n++)
        {
            uint c = n;
            for (int k = 0; k < 8; k++)
            {
                c = (c & 1u) != 0 ? 0xEDB88320u ^ (c >> 1) : c >> 1;
            }

            tables[n] = c;
        }

        for (int t = 1; t < 8; t++)
        {
            int prevBase = (t - 1) * 256;
            int curBase = t * 256;
            for (int n = 0; n < 256; n++)
            {
                uint c = tables[prevBase + n];
                tables[curBase + n] = tables[c & 0xFFu] ^ (c >> 8);
            }
        }

        return tables;
    }

    internal static uint ComputeCrc32(ReadOnlySpan<byte> data) =>
        UpdateCrc32(0xFFFFFFFFu, data) ^ 0xFFFFFFFFu;

    internal static uint ComputeCrc32(ReadOnlySpan<byte> part1, ReadOnlySpan<byte> part2)
    {
        uint crc = UpdateCrc32(0xFFFFFFFFu, part1);
        crc = UpdateCrc32(crc, part2);
        return crc ^ 0xFFFFFFFFu;
    }

    internal static uint UpdateCrc32(uint crc, ReadOnlySpan<byte> data)
    {
#if NET8_0_OR_GREATER
        if (Crc32.Arm64.IsSupported && data.Length >= 8)
        {
            return UpdateCrc32Arm64(crc, data);
        }

        if (Pclmulqdq.IsSupported && Sse41.IsSupported && data.Length >= 64)
        {
            return UpdateCrc32Pclmulqdq(crc, data);
        }
#endif
        return UpdateCrc32SlicingBy8(crc, data);
    }

    internal static uint UpdateCrc32Scalar(uint crc, ReadOnlySpan<byte> data)
    {
        uint[] table = Crc32SliceTables;
        for (int i = 0; i < data.Length; i++)
        {
            crc = table[(crc ^ data[i]) & 0xFFu] ^ (crc >> 8);
        }

        return crc;
    }

    private static uint UpdateCrc32SlicingBy8(uint crc, ReadOnlySpan<byte> data)
    {
        uint[] t = Crc32SliceTables;
        int i = 0;
        int limit = data.Length - 8;
        for (; i <= limit; i += 8)
        {
            uint one = BinaryPrimitives.ReadUInt32LittleEndian(data.Slice(i, 4)) ^ crc;
            uint two = BinaryPrimitives.ReadUInt32LittleEndian(data.Slice(i + 4, 4));
            crc = t[(7 * 256) + (one & 0xFFu)]
                ^ t[(6 * 256) + ((one >> 8) & 0xFFu)]
                ^ t[(5 * 256) + ((one >> 16) & 0xFFu)]
                ^ t[(4 * 256) + (one >> 24)]
                ^ t[(3 * 256) + (two & 0xFFu)]
                ^ t[(2 * 256) + ((two >> 8) & 0xFFu)]
                ^ t[(1 * 256) + ((two >> 16) & 0xFFu)]
                ^ t[two >> 24];
        }

        for (; i < data.Length; i++)
        {
            crc = t[(crc ^ data[i]) & 0xFFu] ^ (crc >> 8);
        }

        return crc;
    }

#if NET8_0_OR_GREATER
    private static uint UpdateCrc32Arm64(uint crc, ReadOnlySpan<byte> data)
    {
        ReadOnlySpan<ulong> blocks64 = MemoryMarshal.Cast<byte, ulong>(data);
        for (int b = 0; b < blocks64.Length; b++)
        {
            crc = Crc32.Arm64.ComputeCrc32(crc, blocks64[b]);
        }

        int i = blocks64.Length * 8;
        if (i + 4 <= data.Length)
        {
            crc = Crc32.ComputeCrc32(crc, BinaryPrimitives.ReadUInt32LittleEndian(data.Slice(i, 4)));
            i += 4;
        }

        for (; i < data.Length; i++)
        {
            crc = Crc32.ComputeCrc32(crc, data[i]);
        }

        return crc;
    }

    private static uint UpdateCrc32Pclmulqdq(uint crc, ReadOnlySpan<byte> data)
    {
        // IEEE 802.3 reflected CRC-32 folding and Barrett reduction constants.
        Vector128<ulong> k1k2 = Vector128.Create(0x154442BD4UL, 0x1C6E41596UL);
        Vector128<ulong> k3k4 = Vector128.Create(0x1751997D0UL, 0x0CCAA009EUL);
        Vector128<ulong> k5 = Vector128.Create(0x163CD6124UL, 0UL);
        Vector128<ulong> muPoly = Vector128.Create(0x1F7011641UL, 0x1DB710641UL);
        Vector128<ulong> mask32 = Vector128.Create(0xFFFFFFFFUL, 0UL);

        Vector128<ulong> x0 = Vector128.Create(data.Slice(0, 16)).AsUInt64() ^ Vector128.CreateScalar((ulong)crc);
        Vector128<ulong> x1 = Vector128.Create(data.Slice(16, 16)).AsUInt64();
        Vector128<ulong> x2 = Vector128.Create(data.Slice(32, 16)).AsUInt64();
        Vector128<ulong> x3 = Vector128.Create(data.Slice(48, 16)).AsUInt64();

        int off = 64;
        int limit64 = data.Length - 64;
        while (off <= limit64)
        {
            Vector128<ulong> y0 = Vector128.Create(data.Slice(off, 16)).AsUInt64();
            Vector128<ulong> y1 = Vector128.Create(data.Slice(off + 16, 16)).AsUInt64();
            Vector128<ulong> y2 = Vector128.Create(data.Slice(off + 32, 16)).AsUInt64();
            Vector128<ulong> y3 = Vector128.Create(data.Slice(off + 48, 16)).AsUInt64();

            x0 = Pclmulqdq.CarrylessMultiply(x0, k1k2, 0x00) ^ Pclmulqdq.CarrylessMultiply(x0, k1k2, 0x11) ^ y0;
            x1 = Pclmulqdq.CarrylessMultiply(x1, k1k2, 0x00) ^ Pclmulqdq.CarrylessMultiply(x1, k1k2, 0x11) ^ y1;
            x2 = Pclmulqdq.CarrylessMultiply(x2, k1k2, 0x00) ^ Pclmulqdq.CarrylessMultiply(x2, k1k2, 0x11) ^ y2;
            x3 = Pclmulqdq.CarrylessMultiply(x3, k1k2, 0x00) ^ Pclmulqdq.CarrylessMultiply(x3, k1k2, 0x11) ^ y3;
            off += 64;
        }

        // Fold 4 vectors down to 1 vector.
        x1 = Pclmulqdq.CarrylessMultiply(x0, k3k4, 0x00) ^ Pclmulqdq.CarrylessMultiply(x0, k3k4, 0x11) ^ x1;
        x2 = Pclmulqdq.CarrylessMultiply(x1, k3k4, 0x00) ^ Pclmulqdq.CarrylessMultiply(x1, k3k4, 0x11) ^ x2;
        x3 = Pclmulqdq.CarrylessMultiply(x2, k3k4, 0x00) ^ Pclmulqdq.CarrylessMultiply(x2, k3k4, 0x11) ^ x3;

        int limit16 = data.Length - 16;
        while (off <= limit16)
        {
            Vector128<ulong> y = Vector128.Create(data.Slice(off, 16)).AsUInt64();
            x3 = Pclmulqdq.CarrylessMultiply(x3, k3k4, 0x00) ^ Pclmulqdq.CarrylessMultiply(x3, k3k4, 0x11) ^ y;
            off += 16;
        }

        // Fold 128 bits -> 96 bits -> 64 bits -> 32 bits (Barrett reduction).
        Vector128<ulong> t = Pclmulqdq.CarrylessMultiply(x3, k3k4, 0x10)
            ^ Sse2.ShiftRightLogical128BitLane(x3.AsByte(), 8).AsUInt64();
        t = Pclmulqdq.CarrylessMultiply(t & mask32, k5, 0x00)
            ^ Sse2.ShiftRightLogical128BitLane(t.AsByte(), 4).AsUInt64();

        Vector128<ulong> q = Pclmulqdq.CarrylessMultiply(t & mask32, muPoly, 0x00);
        q = Pclmulqdq.CarrylessMultiply(q & mask32, muPoly, 0x10);
        crc = (t ^ q).AsUInt32().GetElement(1);

        if (off < data.Length)
        {
            crc = UpdateCrc32SlicingBy8(crc, data.Slice(off));
        }

        return crc;
    }
#endif

    internal static uint ComputeAdler32(ReadOnlySpan<byte> data) =>
        UpdateAdler32(1u, data);

    internal static uint UpdateAdler32(uint adler, ReadOnlySpan<byte> data)
    {
#if NET8_0_OR_GREATER
        if (Avx2.IsSupported && data.Length >= 32)
        {
            return UpdateAdler32Avx2(adler, data);
        }

        if (Ssse3.IsSupported && data.Length >= 16)
        {
            return UpdateAdler32Ssse3(adler, data);
        }

        if (AdvSimd.IsSupported && data.Length >= 16)
        {
            return UpdateAdler32AdvSimd(adler, data);
        }
#endif
        return UpdateAdler32Scalar(adler, data);
    }

    internal static uint UpdateAdler32Scalar(uint adler, ReadOnlySpan<byte> data)
    {
        uint s1 = adler & 0xFFFFu;
        uint s2 = (adler >> 16) & 0xFFFFu;

        int offset = 0;
        while (offset < data.Length)
        {
            int chunk = Math.Min(AdlerNMax, data.Length - offset);
            ReadOnlySpan<byte> slice = data.Slice(offset, chunk);
            for (int i = 0; i < slice.Length; i++)
            {
                s1 += slice[i];
                s2 += s1;
            }

            s1 %= ModAdler;
            s2 %= ModAdler;
            offset += chunk;
        }

        return (s2 << 16) | s1;
    }

#if NET8_0_OR_GREATER
    private static uint UpdateAdler32Avx2(uint adler, ReadOnlySpan<byte> data)
    {
        const int MaxIters = AdlerNMax / 32; // 173 * 32 = 5536 bytes
        uint s1 = adler & 0xFFFFu;
        uint s2 = (adler >> 16) & 0xFFFFu;

        Vector256<byte> zero = Vector256<byte>.Zero;
        Vector256<short> ones16 = Vector256.Create((short)1);
        Vector256<sbyte> weights = Vector256.Create(
            (sbyte)32, 31, 30, 29, 28, 27, 26, 25, 24, 23, 22, 21, 20, 19, 18, 17,
            16, 15, 14, 13, 12, 11, 10, 9, 8, 7, 6, 5, 4, 3, 2, 1);

        int offset = 0;
        while (data.Length - offset >= 32)
        {
            int iters = Math.Min(MaxIters, (data.Length - offset) >> 5);
            Vector256<uint> vS1 = Vector256<uint>.Zero;
            Vector256<uint> vPs = Vector256<uint>.Zero;
            Vector256<uint> vS2 = Vector256<uint>.Zero;

            for (int n = 0; n < iters; n++)
            {
                Vector256<byte> v = Vector256.Create(data.Slice(offset, 32));
                offset += 32;

                vPs += vS1;
                vS1 += Avx2.SumAbsoluteDifferences(v, zero).AsUInt32();

                Vector256<short> mad16 = Avx2.MultiplyAddAdjacent(v, weights);
                Vector256<int> mad32 = Avx2.MultiplyAddAdjacent(mad16, ones16);
                vS2 += mad32.AsUInt32();
            }

            uint sumS1 = Vector256.Sum(vS1);
            ulong s2Total = s2
                + ((ulong)s1 * (uint)(iters * 32))
                + ((ulong)Vector256.Sum(vPs) * 32UL)
                + Vector256.Sum(vS2);

            s1 = (s1 + sumS1) % ModAdler;
            s2 = (uint)(s2Total % ModAdler);
        }

        if (offset < data.Length)
        {
            return UpdateAdler32Scalar((s2 << 16) | s1, data.Slice(offset));
        }

        return (s2 << 16) | s1;
    }

    private static uint UpdateAdler32Ssse3(uint adler, ReadOnlySpan<byte> data)
    {
        const int MaxIters = AdlerNMax / 16; // 347 * 16 = 5552 bytes
        uint s1 = adler & 0xFFFFu;
        uint s2 = (adler >> 16) & 0xFFFFu;

        Vector128<byte> zero = Vector128<byte>.Zero;
        Vector128<short> ones16 = Vector128.Create((short)1);
        Vector128<sbyte> weights = Vector128.Create(
            (sbyte)16, 15, 14, 13, 12, 11, 10, 9, 8, 7, 6, 5, 4, 3, 2, 1);

        int offset = 0;
        while (data.Length - offset >= 16)
        {
            int iters = Math.Min(MaxIters, (data.Length - offset) >> 4);
            Vector128<uint> vS1 = Vector128<uint>.Zero;
            Vector128<uint> vPs = Vector128<uint>.Zero;
            Vector128<uint> vS2 = Vector128<uint>.Zero;

            for (int n = 0; n < iters; n++)
            {
                Vector128<byte> v = Vector128.Create(data.Slice(offset, 16));
                offset += 16;

                vPs += vS1;
                vS1 += Sse2.SumAbsoluteDifferences(v, zero).AsUInt32();

                Vector128<short> mad16 = Ssse3.MultiplyAddAdjacent(v, weights);
                Vector128<int> mad32 = Sse2.MultiplyAddAdjacent(mad16, ones16);
                vS2 += mad32.AsUInt32();
            }

            uint sumS1 = Vector128.Sum(vS1);
            ulong s2Total = s2
                + ((ulong)s1 * (uint)(iters * 16))
                + ((ulong)Vector128.Sum(vPs) * 16UL)
                + Vector128.Sum(vS2);

            s1 = (s1 + sumS1) % ModAdler;
            s2 = (uint)(s2Total % ModAdler);
        }

        if (offset < data.Length)
        {
            return UpdateAdler32Scalar((s2 << 16) | s1, data.Slice(offset));
        }

        return (s2 << 16) | s1;
    }

    private static uint UpdateAdler32AdvSimd(uint adler, ReadOnlySpan<byte> data)
    {
        const int MaxIters = AdlerNMax / 16;
        uint s1 = adler & 0xFFFFu;
        uint s2 = (adler >> 16) & 0xFFFFu;

        Vector128<ushort> weightsLo = Vector128.Create((ushort)16, 15, 14, 13, 12, 11, 10, 9);
        Vector128<ushort> weightsHi = Vector128.Create((ushort)8, 7, 6, 5, 4, 3, 2, 1);

        int offset = 0;
        while (data.Length - offset >= 16)
        {
            int iters = Math.Min(MaxIters, (data.Length - offset) >> 4);
            Vector128<uint> vS1 = Vector128<uint>.Zero;
            Vector128<uint> vPs = Vector128<uint>.Zero;
            Vector128<uint> vS2 = Vector128<uint>.Zero;

            for (int n = 0; n < iters; n++)
            {
                Vector128<byte> v = Vector128.Create(data.Slice(offset, 16));
                offset += 16;

                vPs += vS1;
                (Vector128<ushort> lo, Vector128<ushort> hi) = Vector128.Widen(v);
                vS1 = AdvSimd.AddPairwiseWideningAndAdd(vS1, lo + hi);
                vS2 = AdvSimd.AddPairwiseWideningAndAdd(vS2, (lo * weightsLo) + (hi * weightsHi));
            }

            uint sumS1 = Vector128.Sum(vS1);
            ulong s2Total = s2
                + ((ulong)s1 * (uint)(iters * 16))
                + ((ulong)Vector128.Sum(vPs) * 16UL)
                + Vector128.Sum(vS2);

            s1 = (s1 + sumS1) % ModAdler;
            s2 = (uint)(s2Total % ModAdler);
        }

        if (offset < data.Length)
        {
            return UpdateAdler32Scalar((s2 << 16) | s1, data.Slice(offset));
        }

        return (s2 << 16) | s1;
    }
#endif
}
