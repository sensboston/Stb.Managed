using System;
using System.Buffers;
using System.Buffers.Binary;
#if NET8_0_OR_GREATER
using System.Numerics;
#endif
using System.Runtime.CompilerServices;
using Stb.Managed.Internal;

namespace Stb.Managed.ImageWrite.Internal;

/// <summary>
/// Faithful, pooled, and optimized port of <c>stbi_zlib_compress</c> from <c>stb_image_write.h</c>
/// (fixed-Huffman LZ77 with 16K hash table, lazy matching, uncompressed fallback, and Adler-32).
/// Produces byte-for-byte identical output to native <c>stb_image_write</c>.
/// </summary>
internal static class StbZlibCompressor
{
    private const int ZHashSize = 16384;
    private const int ZHashMask = ZHashSize - 1;
    private const int MaxFlatQuality = 64;

    private static ReadOnlySpan<ushort> LengthC =>
    [
        3, 4, 5, 6, 7, 8, 9, 10, 11, 13, 15, 17, 19, 23, 27, 31,
        35, 43, 51, 59, 67, 83, 99, 115, 131, 163, 195, 227, 258, 259,
    ];

    private static ReadOnlySpan<byte> LengthEb =>
    [
        0, 0, 0, 0, 0, 0, 0, 0, 1, 1, 1, 1, 2, 2, 2, 2,
        3, 3, 3, 3, 4, 4, 4, 4, 5, 5, 5, 5, 0,
    ];

    private static ReadOnlySpan<ushort> DistC =>
    [
        1, 2, 3, 4, 5, 7, 9, 13, 17, 25, 33, 49, 65, 97, 129, 193,
        257, 385, 513, 769, 1025, 1537, 2049, 3073, 4097, 6145, 8193, 12289, 16385, 24577, 32768,
    ];

    private static ReadOnlySpan<byte> DistEb =>
    [
        0, 0, 0, 0, 1, 1, 2, 2, 3, 3, 4, 4, 5, 5, 6, 6,
        7, 7, 8, 8, 9, 9, 10, 10, 11, 11, 12, 12, 13, 13,
    ];

    // Precomputed bit-reversed fixed-Huffman codes and bit lengths for symbols 0..287,
    // plus direct length -> slot LUT (indices 3..258).
    private static readonly ushort[] FixedHuffCodes = BuildFixedHuffCodes();
    private static readonly byte[] FixedHuffBits = BuildFixedHuffBits();
    private static readonly byte[] BitRev5 = BuildBitRev5();
    private static readonly byte[] LengthToSlot = BuildLengthToSlot();

    private static ushort[] BuildFixedHuffCodes()
    {
        ushort[] codes = new ushort[288];
        for (int n = 0; n <= 143; n++)
        {
            codes[n] = (ushort)BitRev(0x30 + n, 8);
        }

        for (int n = 144; n <= 255; n++)
        {
            codes[n] = (ushort)BitRev(0x190 + (n - 144), 9);
        }

        for (int n = 256; n <= 279; n++)
        {
            codes[n] = (ushort)BitRev(n - 256, 7);
        }

        for (int n = 280; n <= 287; n++)
        {
            codes[n] = (ushort)BitRev(0xC0 + (n - 280), 8);
        }

        return codes;
    }

    private static byte[] BuildFixedHuffBits()
    {
        byte[] bits = new byte[288];
        bits.AsSpan(0, 144).Fill(8);
        bits.AsSpan(144, 112).Fill(9);
        bits.AsSpan(256, 24).Fill(7);
        bits.AsSpan(280, 8).Fill(8);
        return bits;
    }

    private static byte[] BuildBitRev5()
    {
        byte[] table = new byte[32];
        for (int i = 0; i < 32; i++)
        {
            table[i] = (byte)BitRev(i, 5);
        }

        return table;
    }

    private static byte[] BuildLengthToSlot()
    {
        byte[] lut = new byte[259];
        ReadOnlySpan<ushort> lengthC = LengthC;
        for (int len = 3; len <= 258; len++)
        {
            int j = 0;
            while (len > lengthC[j + 1] - 1)
            {
                j++;
            }

            lut[len] = (byte)j;
        }

        return lut;
    }

    private static int BitRev(int code, int codebits)
    {
        int res = 0;
        while (codebits-- > 0)
        {
            res = (res << 1) | (code & 1);
            code >>= 1;
        }

        return res;
    }

    /// <summary>
    /// Compresses <paramref name="data"/> into a pooled buffer returned via <paramref name="rentedOutput"/>
    /// (caller must return it to <see cref="ArrayPool{T}.Shared"/>) and returns the encoded length.
    /// </summary>
    internal static int Compress(
        ReadOnlySpan<byte> data,
        int quality,
        StbLimits limits,
        out byte[] rentedOutput)
    {
        if (quality < 5)
        {
            quality = 5;
        }

        int dataLen = data.Length;
        long storedPayloadLen = 2L + (long)dataLen + ((((long)dataLen + 32766L) / 32767L) * 5L);
        long maxOutputEstimate = storedPayloadLen + 16L;
        limits.ValidateAllocation(maxOutputEstimate);

        int initialCap = (int)Math.Min(Math.Max(256L, (long)dataLen + 64L), ArrayAllocation.MaxByteArrayLength);
        byte[] output = ArrayPool<byte>.Shared.Rent(initialCap);
        int outPos = 0;

        try
        {
            // Zlib header: 0x78 (DEFLATE 32K window), 0x5E (FLEVEL = 1)
            output[outPos++] = 0x78;
            output[outPos++] = 0x5E;

            ulong bitBuf = 0;
            int bitCount = 0;

            // BFINAL = 1 (1 bit), BTYPE = 1 fixed Huffman (2 bits) -> 0b011 = 3 in 3 bits
            AddBits(ref output, ref outPos, ref bitBuf, ref bitCount, 3u, 3, limits);

            if (quality <= MaxFlatQuality)
            {
                CompressWithFlatHash(data, quality, limits, ref output, ref outPos, ref bitBuf, ref bitCount);
            }
            else
            {
                CompressWithJaggedHash(data, quality, limits, ref output, ref outPos, ref bitBuf, ref bitCount);
            }

            // Flush remaining complete bytes and pad tail with zero bits to byte boundary
            EnsureCapacity(ref output, outPos + 9, limits);
            while (bitCount >= 8)
            {
                output[outPos++] = (byte)bitBuf;
                bitBuf >>= 8;
                bitCount -= 8;
            }

            if (bitCount > 0)
            {
                output[outPos++] = (byte)bitBuf;
            }

            // Store uncompressed instead if fixed-Huffman compression expanded the data
            if (outPos > storedPayloadLen)
            {
                EnsureCapacity(ref output, (int)storedPayloadLen + 4, limits);
                outPos = 2; // preserve 0x78, 0x5E
                int j = 0;
                while (j < dataLen)
                {
                    int blockLen = dataLen - j;
                    if (blockLen > 32767)
                    {
                        blockLen = 32767;
                    }

                    output[outPos++] = (byte)(dataLen - j == blockLen ? 1 : 0);
                    output[outPos++] = (byte)blockLen;
                    output[outPos++] = (byte)(blockLen >> 8);
                    int nlen = ~blockLen;
                    output[outPos++] = (byte)nlen;
                    output[outPos++] = (byte)(nlen >> 8);
                    data.Slice(j, blockLen).CopyTo(output.AsSpan(outPos, blockLen));
                    outPos += blockLen;
                    j += blockLen;
                }
            }

            // Append big-endian Adler-32
            uint adler = Checksums.ComputeAdler32(data);
            EnsureCapacity(ref output, outPos + 4, limits);
            BinaryPrimitives.WriteUInt32BigEndian(output.AsSpan(outPos, 4), adler);
            outPos += 4;

            rentedOutput = output;
            return outPos;
        }
        catch
        {
            ArrayPool<byte>.Shared.Return(output);
            throw;
        }
    }

    private static void CompressWithFlatHash(
        ReadOnlySpan<byte> data,
        int quality,
        StbLimits limits,
        ref byte[] output,
        ref int outPos,
        ref ulong bitBuf,
        ref int bitCount)
    {
        int bucketStride = 2 * quality;
        int dataLen = data.Length;

        using PooledBuffer<int> countsBuf = new(ZHashSize, clear: true);
        using PooledBuffer<int> tableBuf = new(ZHashSize * bucketStride);
        Span<int> counts = countsBuf.Span;
        Span<int> table = tableBuf.Span;

        ushort[] huffCodes = FixedHuffCodes;
        byte[] huffBits = FixedHuffBits;
        byte[] rev5 = BitRev5;
        byte[] lenToSlot = LengthToSlot;
        ReadOnlySpan<ushort> lengthC = LengthC;
        ReadOnlySpan<byte> lengthEb = LengthEb;
        ReadOnlySpan<ushort> distC = DistC;
        ReadOnlySpan<byte> distEb = DistEb;

        int i = 0;
        while (i < dataLen - 3)
        {
            int h = ZHash(data, i);
            int bucketBase = h * bucketStride;
            int n = counts[h];
            int best = 3;
            int bestLoc = -1;
            int winMin = i - 32768;
            int remaining = dataLen - i;
            byte tailByte = data[i + 2];

            for (int j = 0; j < n; j++)
            {
                int entry = table[bucketBase + j];
                if (entry > winMin && data[entry + best - 1] == tailByte)
                {
                    int d = CountMatch(data, entry, i, remaining);
                    if (d >= best)
                    {
                        best = d;
                        bestLoc = entry;
                        if (best <= remaining)
                        {
                            tailByte = data[i + best - 1];
                        }
                    }
                }
            }

            if (n == bucketStride)
            {
                table.Slice(bucketBase + quality, quality).CopyTo(table.Slice(bucketBase, quality));
                n = quality;
            }

            table[bucketBase + n] = i;
            counts[h] = n + 1;

            if (bestLoc >= 0 && best < 258)
            {
                int h2 = ZHash(data, i + 1);
                int b2Base = h2 * bucketStride;
                int n2 = counts[h2];
                int winMin2 = i - 32767;
                int rem2 = remaining - 1;
                if (best < rem2)
                {
                    byte lazyTail = data[i + 1 + best];
                    for (int j = 0; j < n2; j++)
                    {
                        int entry = table[b2Base + j];
                        if (entry > winMin2 && data[entry + best] == lazyTail)
                        {
                            int e = CountMatch(data, entry, i + 1, rem2);
                            if (e > best)
                            {
                                bestLoc = -1;
                                break;
                            }
                        }
                    }
                }
            }

            if (bestLoc >= 0)
            {
                int d = i - bestLoc;
                int lenSlot = lenToSlot[best];
                AddBits(ref output, ref outPos, ref bitBuf, ref bitCount, huffCodes[lenSlot + 257], huffBits[lenSlot + 257], limits);
                int leb = lengthEb[lenSlot];
                if (leb != 0)
                {
                    AddBits(ref output, ref outPos, ref bitBuf, ref bitCount, (uint)(best - lengthC[lenSlot]), leb, limits);
                }

                int distSlot = FindDistSlot(d, distC);
                AddBits(ref output, ref outPos, ref bitBuf, ref bitCount, rev5[distSlot], 5, limits);
                int deb = distEb[distSlot];
                if (deb != 0)
                {
                    AddBits(ref output, ref outPos, ref bitBuf, ref bitCount, (uint)(d - distC[distSlot]), deb, limits);
                }

                i += best;
            }
            else
            {
                byte lit = data[i];
                AddBits(ref output, ref outPos, ref bitBuf, ref bitCount, huffCodes[lit], huffBits[lit], limits);
                i++;
            }
        }

        for (; i < dataLen; i++)
        {
            byte lit = data[i];
            AddBits(ref output, ref outPos, ref bitBuf, ref bitCount, huffCodes[lit], huffBits[lit], limits);
        }

        // End of block (symbol 256)
        AddBits(ref output, ref outPos, ref bitBuf, ref bitCount, huffCodes[256], huffBits[256], limits);
    }

    private static void CompressWithJaggedHash(
        ReadOnlySpan<byte> data,
        int quality,
        StbLimits limits,
        ref byte[] output,
        ref int outPos,
        ref ulong bitBuf,
        ref int bitCount)
    {
        int bucketCap = checked(2 * quality);
        int dataLen = data.Length;
        int[][] buckets = new int[ZHashSize][];
        int[] counts = new int[ZHashSize];

        ushort[] huffCodes = FixedHuffCodes;
        byte[] huffBits = FixedHuffBits;
        byte[] rev5 = BitRev5;
        byte[] lenToSlot = LengthToSlot;
        ReadOnlySpan<ushort> lengthC = LengthC;
        ReadOnlySpan<byte> lengthEb = LengthEb;
        ReadOnlySpan<ushort> distC = DistC;
        ReadOnlySpan<byte> distEb = DistEb;

        int i = 0;
        while (i < dataLen - 3)
        {
            int h = ZHash(data, i);
            int[]? hlist = buckets[h];
            int n = counts[h];
            int best = 3;
            int bestLoc = -1;
            int winMin = i - 32768;
            int remaining = dataLen - i;

            if (hlist is not null)
            {
                byte tailByte = data[i + 2];
                for (int j = 0; j < n; j++)
                {
                    int entry = hlist[j];
                    if (entry > winMin && data[entry + best - 1] == tailByte)
                    {
                        int d = CountMatch(data, entry, i, remaining);
                        if (d >= best)
                        {
                            best = d;
                            bestLoc = entry;
                            if (best <= remaining)
                            {
                                tailByte = data[i + best - 1];
                            }
                        }
                    }
                }
            }

            if (hlist is null)
            {
                hlist = new int[Math.Min(16, bucketCap)];
                buckets[h] = hlist;
            }
            else if (n == bucketCap)
            {
                Array.Copy(hlist, quality, hlist, 0, quality);
                n = quality;
            }
            else if (n == hlist.Length)
            {
                int newLen = Math.Min(hlist.Length * 2, bucketCap);
                Array.Resize(ref hlist, newLen);
                buckets[h] = hlist;
            }

            hlist[n] = i;
            counts[h] = n + 1;

            if (bestLoc >= 0 && best < 258)
            {
                int h2 = ZHash(data, i + 1);
                int[]? hlist2 = buckets[h2];
                int n2 = counts[h2];
                int winMin2 = i - 32767;
                int rem2 = remaining - 1;
                if (hlist2 is not null && best < rem2)
                {
                    byte lazyTail = data[i + 1 + best];
                    for (int j = 0; j < n2; j++)
                    {
                        int entry = hlist2[j];
                        if (entry > winMin2 && data[entry + best] == lazyTail)
                        {
                            int e = CountMatch(data, entry, i + 1, rem2);
                            if (e > best)
                            {
                                bestLoc = -1;
                                break;
                            }
                        }
                    }
                }
            }

            if (bestLoc >= 0)
            {
                int d = i - bestLoc;
                int lenSlot = lenToSlot[best];
                AddBits(ref output, ref outPos, ref bitBuf, ref bitCount, huffCodes[lenSlot + 257], huffBits[lenSlot + 257], limits);
                int leb = lengthEb[lenSlot];
                if (leb != 0)
                {
                    AddBits(ref output, ref outPos, ref bitBuf, ref bitCount, (uint)(best - lengthC[lenSlot]), leb, limits);
                }

                int distSlot = FindDistSlot(d, distC);
                AddBits(ref output, ref outPos, ref bitBuf, ref bitCount, rev5[distSlot], 5, limits);
                int deb = distEb[distSlot];
                if (deb != 0)
                {
                    AddBits(ref output, ref outPos, ref bitBuf, ref bitCount, (uint)(d - distC[distSlot]), deb, limits);
                }

                i += best;
            }
            else
            {
                byte lit = data[i];
                AddBits(ref output, ref outPos, ref bitBuf, ref bitCount, huffCodes[lit], huffBits[lit], limits);
                i++;
            }
        }

        for (; i < dataLen; i++)
        {
            byte lit = data[i];
            AddBits(ref output, ref outPos, ref bitBuf, ref bitCount, huffCodes[lit], huffBits[lit], limits);
        }

        AddBits(ref output, ref outPos, ref bitBuf, ref bitCount, huffCodes[256], huffBits[256], limits);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int FindDistSlot(int d, ReadOnlySpan<ushort> distC)
    {
        if (d <= 4)
        {
            return d - 1;
        }

        int j = 4;
        while (d > distC[j + 1] - 1)
        {
            j++;
        }

        return j;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int ZHash(ReadOnlySpan<byte> data, int offset)
    {
        uint hash = (uint)(data[offset] + (data[offset + 1] << 8) + (data[offset + 2] << 16));
        hash ^= hash << 3;
        hash += hash >> 5;
        hash ^= hash << 4;
        hash += hash >> 17;
        hash ^= hash << 25;
        hash += hash >> 6;
        return (int)(hash & ZHashMask);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int CountMatch(ReadOnlySpan<byte> data, int a, int b, int limit)
    {
        int max = limit < 258 ? limit : 258;
        int i = 0;
#if NET8_0_OR_GREATER
        while (i + 8 <= max)
        {
            ulong va = BinaryPrimitives.ReadUInt64LittleEndian(data.Slice(a + i, 8));
            ulong vb = BinaryPrimitives.ReadUInt64LittleEndian(data.Slice(b + i, 8));
            ulong diff = va ^ vb;
            if (diff != 0)
            {
                return i + (BitOperations.TrailingZeroCount(diff) >> 3);
            }

            i += 8;
        }
#endif
        while (i < max && data[a + i] == data[b + i])
        {
            i++;
        }

        return i;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void AddBits(
        ref byte[] output,
        ref int outPos,
        ref ulong bitBuf,
        ref int bitCount,
        uint code,
        int codeBits,
        StbLimits limits)
    {
        bitBuf |= (ulong)code << bitCount;
        bitCount += codeBits;
        if (bitCount >= 32)
        {
            Flush32Bits(ref output, ref outPos, ref bitBuf, ref bitCount, limits);
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void Flush32Bits(
        ref byte[] output,
        ref int outPos,
        ref ulong bitBuf,
        ref int bitCount,
        StbLimits limits)
    {
        if (outPos + 4 > output.Length)
        {
            EnsureCapacity(ref output, outPos + 4, limits);
        }

        BinaryPrimitives.WriteUInt32LittleEndian(output.AsSpan(outPos, 4), (uint)bitBuf);
        outPos += 4;
        bitBuf >>= 32;
        bitCount -= 32;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void EnsureCapacity(ref byte[] output, int needed, StbLimits limits)
    {
        if (needed <= output.Length)
        {
            return;
        }

        long doubled = (long)output.Length * 2L;
        long nextSize = Math.Max((long)needed, doubled);
        if (nextSize > ArrayAllocation.MaxByteArrayLength)
        {
            nextSize = needed;
        }

        limits.ValidateAllocation(nextSize);
        byte[] next = ArrayPool<byte>.Shared.Rent((int)nextSize);
        output.AsSpan().CopyTo(next);
        ArrayPool<byte>.Shared.Return(output);
        output = next;
    }
}
