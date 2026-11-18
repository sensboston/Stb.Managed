using System.Runtime.CompilerServices;

namespace Stb.Managed.Image.Internal;

/// <summary>
/// Lookup tables, exact integer constants, and 11-bit accelerated Huffman tables matching <c>stb_image.h</c>.
/// </summary>
internal static class JpegTables
{
    // 11-bit primary fast lookup table (2,048 entries: covers ~96% of AC symbol+magnitude pairs in 1 lookup).
    internal const int FastBits = 11;
    internal const int FastSize = 1 << FastBits;
    internal const int FastMask = FastSize - 1;

    // Sentinel run codes stored in FastAc[i] bits 8..15 when val == 0 (baseline fast EOB / ZRL).
    internal const int FastAcEobRun = 64;
    internal const int FastAcZrlRun = 16;

    internal const byte MarkerNone = 0xFF;

    /// <summary>
    /// Maps zigzag index (0..63) to row-major 8x8 index, padded with 15 trailing 63s so corrupt
    /// AC runs up to k=63+15 safely clamp inside the block without extra branch overhead.
    /// </summary>
    internal static ReadOnlySpan<byte> Dezigzag =>
    [
         0,  1,  8, 16,  9,  2,  3, 10,
        17, 24, 32, 25, 18, 11,  4,  5,
        12, 19, 26, 33, 40, 48, 41, 34,
        27, 20, 13,  6,  7, 14, 21, 28,
        35, 42, 49, 56, 57, 50, 43, 36,
        29, 22, 15, 23, 30, 37, 44, 51,
        58, 59, 52, 45, 38, 31, 39, 46,
        53, 60, 61, 54, 47, 55, 62, 63,
        63, 63, 63, 63, 63, 63, 63, 63,
        63, 63, 63, 63, 63, 63, 63,
    ];

    internal static readonly uint[] BitMask =
    [
        0, 1, 3, 7, 15, 31, 63, 127, 255, 511, 1023, 2047, 4095, 8191, 16383, 32767, 65535,
    ];

    internal static readonly int[] JpegBias =
    [
        0, -1, -3, -7, -15, -31, -63, -127, -255, -511, -1023, -2047, -4095, -8191, -16383, -32767,
    ];
}

internal sealed class JpegHuffmanTable
{
    internal readonly byte[] Fast = new byte[JpegTables.FastSize];
    internal readonly ushort[] Code = new ushort[256];
    internal readonly byte[] Values = new byte[256];
    internal readonly byte[] Size = new byte[257];
    internal readonly uint[] MaxCode = new uint[18];
    internal readonly int[] Delta = new int[17];
    internal bool IsDefined;

    internal JpegHuffmanTable() => Reset();

    internal void Reset()
    {
        IsDefined = false;
        Fast.AsSpan().Fill(255);
        MaxCode.AsSpan().Clear();
        MaxCode[17] = 0xFFFFFFFFu;
    }

    internal bool Build(ReadOnlySpan<byte> counts)
    {
        int k = 0;
        for (int i = 0; i < 16; i++)
        {
            int count = counts[i];
            for (int j = 0; j < count; j++)
            {
                if (k >= 256)
                {
                    return false;
                }

                Size[k++] = (byte)(i + 1);
            }
        }

        Size[k] = 0;

        uint code = 0;
        k = 0;
        for (int j = 1; j <= 16; j++)
        {
            Delta[j] = k - (int)code;
            if (Size[k] == j)
            {
                while (Size[k] == j)
                {
                    Code[k++] = (ushort)code++;
                }

                if (code - 1 >= (1u << j))
                {
                    return false;
                }
            }

            MaxCode[j] = code << (16 - j);
            code <<= 1;
        }

        MaxCode[17] = 0xFFFFFFFFu;

        Fast.AsSpan().Fill(255);
        for (int i = 0; i < k; i++)
        {
            int s = Size[i];
            if (s <= JpegTables.FastBits)
            {
                int c = Code[i] << (JpegTables.FastBits - s);
                int m = 1 << (JpegTables.FastBits - s);
                for (int j = 0; j < m; j++)
                {
                    Fast[c + j] = (byte)i;
                }
            }
        }

        return true;
    }

    /// <summary>
    /// Builds an 11-bit combined DC Huffman + sign-extended diff lookup table.
    /// Layout: bits 16..31 = signed diff (short), bits 0..7 = totalBits (1..11, or 0 for slow path).
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal void BuildFastDc(int[] fastDc)
    {
        for (int i = 0; i < JpegTables.FastSize; i++)
        {
            byte fast = Fast[i];
            fastDc[i] = 0;
            if (fast < 255)
            {
                int cat = Values[fast];
                int len = Size[fast];
                if (cat == 0)
                {
                    fastDc[i] = len;
                }
                else if (cat <= 15 && len + cat <= JpegTables.FastBits)
                {
                    int k = ((i << len) & JpegTables.FastMask) >> (JpegTables.FastBits - cat);
                    int m = 1 << (cat - 1);
                    if (k < m)
                    {
                        k += (~0 << cat) + 1;
                    }

                    fastDc[i] = (k << 16) | (len + cat);
                }
            }
        }
    }

    /// <summary>
    /// Builds an 11-bit combined AC Huffman + run + sign-extended value + baseline EOB/ZRL lookup table.
    /// Layout:
    /// - Non-zero AC: bits 16..31 = signed value (non-zero), bits 8..15 = run (0..15), bits 0..7 = totalBits (2..11).
    /// - EOB (rs=0x00): bits 16..31 = 0, bits 8..15 = 64, bits 0..7 = len (1..11).
    /// - ZRL (rs=0xF0): bits 16..31 = 0, bits 8..15 = 16, bits 0..7 = len (1..11).
    /// - Slow path: 0.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal void BuildFastAc(int[] fastAc)
    {
        for (int i = 0; i < JpegTables.FastSize; i++)
        {
            byte fast = Fast[i];
            fastAc[i] = 0;
            if (fast < 255)
            {
                int rs = Values[fast];
                int run = (rs >> 4) & 15;
                int magbits = rs & 15;
                int len = Size[fast];

                if (magbits != 0)
                {
                    if (len + magbits <= JpegTables.FastBits)
                    {
                        int k = ((i << len) & JpegTables.FastMask) >> (JpegTables.FastBits - magbits);
                        int m = 1 << (magbits - 1);
                        if (k < m)
                        {
                            k += (~0 << magbits) + 1;
                        }

                        fastAc[i] = (k << 16) | (run << 8) | (len + magbits);
                    }
                }
                else if (rs == 0x00)
                {
                    fastAc[i] = (JpegTables.FastAcEobRun << 8) | len;
                }
                else if (rs == 0xF0)
                {
                    fastAc[i] = (JpegTables.FastAcZrlRun << 8) | len;
                }
            }
        }
    }
}
