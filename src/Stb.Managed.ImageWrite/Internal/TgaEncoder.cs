using System;
using System.Buffers.Binary;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Stb.Managed.ImageWrite.Internal;

internal static class TgaEncoder
{
    internal const int MaxDimension = 65535;

    internal static void Encode(
        ref ByteSink sink,
        ReadOnlySpan<byte> pixels,
        int width,
        int height,
        int comp,
        int strideBytes,
        bool useRle,
        bool flipVertically)
    {
        bool hasAlpha = comp == 2 || comp == 4;
        int colorBytes = hasAlpha ? comp - 1 : comp;
        int format = colorBytes < 2 ? 3 : 2; // 3 = grayscale, 2 = truecolor
        byte imageType = (byte)(useRle ? format + 8 : format);

        WriteHeader(ref sink, width, height, imageType, (byte)(comp * 8), (byte)(hasAlpha ? 8 : 0));
        if (sink.Overflowed)
        {
            return;
        }

        int rowBytes = width * comp;
        if (!useRle)
        {
            for (int rowIdx = 0; rowIdx < height; rowIdx++)
            {
                int srcRow = flipVertically ? rowIdx : height - 1 - rowIdx;
                ReadOnlySpan<byte> src = pixels.Slice(srcRow * strideBytes, rowBytes);
                Span<byte> dst = sink.Reserve(rowBytes);
                if (sink.Overflowed)
                {
                    return;
                }

                WriteRawRow(src, dst, width, comp);
            }

            return;
        }

        for (int rowIdx = 0; rowIdx < height; rowIdx++)
        {
            int srcRow = flipVertically ? rowIdx : height - 1 - rowIdx;
            ReadOnlySpan<byte> row = pixels.Slice(srcRow * strideBytes, rowBytes);
            switch (comp)
            {
                case 4:
                    WriteRleRow32(ref sink, row, width);
                    break;
                case 3:
                    WriteRleRow24(ref sink, row, width);
                    break;
                case 2:
                    WriteRleRow16(ref sink, row, width);
                    break;
                default:
                    WriteRleRow8(ref sink, row, width);
                    break;
            }

            if (sink.Overflowed)
            {
                return;
            }
        }
    }

    private static void WriteHeader(
        ref ByteSink sink,
        int width,
        int height,
        byte imageType,
        byte pixelDepth,
        byte descriptor)
    {
        Span<byte> h = sink.Reserve(18);
        if (sink.Overflowed)
        {
            return;
        }

        h.Clear();
        h[2] = imageType;
        BinaryPrimitives.WriteUInt16LittleEndian(h.Slice(12, 2), (ushort)width);
        BinaryPrimitives.WriteUInt16LittleEndian(h.Slice(14, 2), (ushort)height);
        h[16] = pixelDepth;
        h[17] = descriptor;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static void WriteRawRow(ReadOnlySpan<byte> src, Span<byte> dst, int width, int comp)
    {
        if (comp <= 2)
        {
            src.CopyTo(dst);
        }
        else if (comp == 3)
        {
            PixelWriteKernels.Rgb24ToBgr24(src, dst, width);
        }
        else
        {
            PixelWriteKernels.Rgba32ToBgra32(src, dst, width);
        }
    }

    internal static void WriteRawRowScalar(ReadOnlySpan<byte> src, Span<byte> dst, int width, int comp)
    {
        if (comp <= 2)
        {
            src.CopyTo(dst);
            return;
        }

        if (comp == 3)
        {
            PixelWriteKernels.Rgb24ToBgr24Scalar(src, dst, width);
        }
        else
        {
            PixelWriteKernels.Rgba32ToBgra32Scalar(src, dst, width);
        }
    }

    private static void WriteRleRow32(ref ByteSink sink, ReadOnlySpan<byte> row, int width)
    {
        ReadOnlySpan<uint> px = MemoryMarshal.Cast<byte, uint>(row);
        for (int i = 0; i < width;)
        {
            uint first = px[i];
            bool diff = true;
            int len = 1;

            if (i < width - 1)
            {
                len = 2;
                diff = first != px[i + 1];
                int maxK = Math.Min(width, i + 128);
                if (diff)
                {
                    int prev = i;
                    for (int k = i + 2; k < maxK; k++)
                    {
                        if (px[prev] != px[k])
                        {
                            prev++;
                            len++;
                        }
                        else
                        {
                            len--;
                            break;
                        }
                    }
                }
                else
                {
                    for (int k = i + 2; k < maxK; k++)
                    {
                        if (first == px[k])
                        {
                            len++;
                        }
                        else
                        {
                            break;
                        }
                    }
                }
            }

            int byteOff = i * 4;
            if (diff)
            {
                int packetBytes = len * 4;
                Span<byte> dst = sink.Reserve(1 + packetBytes);
                if (sink.Overflowed)
                {
                    return;
                }

                dst[0] = (byte)(len - 1);
                PixelWriteKernels.Rgba32ToBgra32(row.Slice(byteOff, packetBytes), dst.Slice(1), len);
            }
            else
            {
                Span<byte> dst = sink.Reserve(5);
                if (sink.Overflowed)
                {
                    return;
                }

                dst[0] = (byte)(len - 129);
                dst[1] = row[byteOff + 2];
                dst[2] = row[byteOff + 1];
                dst[3] = row[byteOff];
                dst[4] = row[byteOff + 3];
            }

            i += len;
        }
    }

    private static void WriteRleRow24(ref ByteSink sink, ReadOnlySpan<byte> row, int width)
    {
        for (int i = 0; i < width;)
        {
            int begin = i * 3;
            byte r0 = row[begin];
            byte g0 = row[begin + 1];
            byte b0 = row[begin + 2];
            bool diff = true;
            int len = 1;

            if (i < width - 1)
            {
                len = 2;
                diff = r0 != row[begin + 3] || g0 != row[begin + 4] || b0 != row[begin + 5];
                int maxK = Math.Min(width, i + 128);
                if (diff)
                {
                    int prev = begin;
                    for (int k = i + 2; k < maxK; k++)
                    {
                        int ko = k * 3;
                        if (row[prev] != row[ko] || row[prev + 1] != row[ko + 1] || row[prev + 2] != row[ko + 2])
                        {
                            prev += 3;
                            len++;
                        }
                        else
                        {
                            len--;
                            break;
                        }
                    }
                }
                else
                {
                    for (int k = i + 2; k < maxK; k++)
                    {
                        int ko = k * 3;
                        if (r0 == row[ko] && g0 == row[ko + 1] && b0 == row[ko + 2])
                        {
                            len++;
                        }
                        else
                        {
                            break;
                        }
                    }
                }
            }

            if (diff)
            {
                int packetBytes = len * 3;
                Span<byte> dst = sink.Reserve(1 + packetBytes);
                if (sink.Overflowed)
                {
                    return;
                }

                dst[0] = (byte)(len - 1);
                PixelWriteKernels.Rgb24ToBgr24(row.Slice(begin, packetBytes), dst.Slice(1), len);
            }
            else
            {
                sink.Write4((byte)(len - 129), b0, g0, r0);
                if (sink.Overflowed)
                {
                    return;
                }
            }

            i += len;
        }
    }

    private static void WriteRleRow16(ref ByteSink sink, ReadOnlySpan<byte> row, int width)
    {
        ReadOnlySpan<ushort> px = MemoryMarshal.Cast<byte, ushort>(row);
        for (int i = 0; i < width;)
        {
            ushort first = px[i];
            bool diff = true;
            int len = 1;

            if (i < width - 1)
            {
                len = 2;
                diff = first != px[i + 1];
                int maxK = Math.Min(width, i + 128);
                if (diff)
                {
                    int prev = i;
                    for (int k = i + 2; k < maxK; k++)
                    {
                        if (px[prev] != px[k])
                        {
                            prev++;
                            len++;
                        }
                        else
                        {
                            len--;
                            break;
                        }
                    }
                }
                else
                {
                    for (int k = i + 2; k < maxK; k++)
                    {
                        if (first == px[k])
                        {
                            len++;
                        }
                        else
                        {
                            break;
                        }
                    }
                }
            }

            int byteOff = i * 2;
            if (diff)
            {
                int packetBytes = len * 2;
                Span<byte> dst = sink.Reserve(1 + packetBytes);
                if (sink.Overflowed)
                {
                    return;
                }

                dst[0] = (byte)(len - 1);
                row.Slice(byteOff, packetBytes).CopyTo(dst.Slice(1));
            }
            else
            {
                sink.Write3((byte)(len - 129), row[byteOff], row[byteOff + 1]);
                if (sink.Overflowed)
                {
                    return;
                }
            }

            i += len;
        }
    }

    private static void WriteRleRow8(ref ByteSink sink, ReadOnlySpan<byte> row, int width)
    {
        for (int i = 0; i < width;)
        {
            byte first = row[i];
            bool diff = true;
            int len = 1;

            if (i < width - 1)
            {
                len = 2;
                diff = first != row[i + 1];
                int maxK = Math.Min(width, i + 128);
                if (diff)
                {
                    int prev = i;
                    for (int k = i + 2; k < maxK; k++)
                    {
                        if (row[prev] != row[k])
                        {
                            prev++;
                            len++;
                        }
                        else
                        {
                            len--;
                            break;
                        }
                    }
                }
                else
                {
                    for (int k = i + 2; k < maxK; k++)
                    {
                        if (first == row[k])
                        {
                            len++;
                        }
                        else
                        {
                            break;
                        }
                    }
                }
            }

            if (diff)
            {
                Span<byte> dst = sink.Reserve(1 + len);
                if (sink.Overflowed)
                {
                    return;
                }

                dst[0] = (byte)(len - 1);
                row.Slice(i, len).CopyTo(dst.Slice(1));
            }
            else
            {
                sink.Write2((byte)(len - 129), first);
                if (sink.Overflowed)
                {
                    return;
                }
            }

            i += len;
        }
    }

    internal static void WriteRleRowScalar(ref ByteSink sink, ReadOnlySpan<byte> row, int width, int comp)
    {
        for (int i = 0; i < width;)
        {
            int begin = i * comp;
            bool diff = true;
            int len = 1;

            if (i < width - 1)
            {
                len++;
                diff = !PixelsEqual(row, begin, (i + 1) * comp, comp);
                if (diff)
                {
                    int prev = begin;
                    for (int k = i + 2; k < width && len < 128; k++)
                    {
                        if (!PixelsEqual(row, prev, k * comp, comp))
                        {
                            prev += comp;
                            len++;
                        }
                        else
                        {
                            len--;
                            break;
                        }
                    }
                }
                else
                {
                    for (int k = i + 2; k < width && len < 128; k++)
                    {
                        if (PixelsEqual(row, begin, k * comp, comp))
                        {
                            len++;
                        }
                        else
                        {
                            break;
                        }
                    }
                }
            }

            if (diff)
            {
                sink.WriteByte((byte)(len - 1));
                int packetBytes = len * comp;
                Span<byte> dst = sink.Reserve(packetBytes);
                if (sink.Overflowed)
                {
                    return;
                }

                WriteRawRowScalar(row.Slice(begin, packetBytes), dst, len, comp);
            }
            else
            {
                sink.WriteByte((byte)(len - 129));
                WriteSinglePixel(ref sink, row, begin, comp);
                if (sink.Overflowed)
                {
                    return;
                }
            }

            i += len;
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool PixelsEqual(ReadOnlySpan<byte> row, int a, int b, int comp)
    {
        return comp switch
        {
            1 => row[a] == row[b],
            2 => row[a] == row[b] && row[a + 1] == row[b + 1],
            3 => row[a] == row[b] && row[a + 1] == row[b + 1] && row[a + 2] == row[b + 2],
            _ => row[a] == row[b] && row[a + 1] == row[b + 1] && row[a + 2] == row[b + 2] && row[a + 3] == row[b + 3],
        };
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void WriteSinglePixel(ref ByteSink sink, ReadOnlySpan<byte> row, int off, int comp)
    {
        switch (comp)
        {
            case 1:
                sink.WriteByte(row[off]);
                break;
            case 2:
                sink.Write2(row[off], row[off + 1]);
                break;
            case 3:
                sink.Write3(row[off + 2], row[off + 1], row[off]);
                break;
            default:
                sink.Write4(row[off + 2], row[off + 1], row[off], row[off + 3]);
                break;
        }
    }
}
