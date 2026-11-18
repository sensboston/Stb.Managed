using System;
using System.Buffers.Binary;
using Stb.Managed.Internal;

namespace Stb.Managed.ImageWrite.Internal;

internal static class BmpEncoder
{
    internal static int ComputeOutputSize(int width, int height, int comp, StbLimits limits)
    {
        long totalBytes;
        if (comp != 4)
        {
            int pad = (-width * 3) & 3;
            long rowBytes = ((long)width * 3L) + pad;
            totalBytes = 14L + 40L + (rowBytes * height);
        }
        else
        {
            totalBytes = 14L + 108L + ((long)width * height * 4L);
        }

        limits.ValidateAllocation(totalBytes);
        return (int)totalBytes;
    }

    internal static void Encode(
        ref ByteSink sink,
        ReadOnlySpan<byte> pixels,
        int width,
        int height,
        int comp,
        int strideBytes,
        bool flipVertically,
        int outputSize)
    {
        if (comp != 4)
        {
            int pad = (-width * 3) & 3;
            WriteHeader24(ref sink, width, height, (uint)outputSize);
            if (sink.Overflowed)
            {
                return;
            }

            int rowOutLen = (width * 3) + pad;
            for (int rowIdx = 0; rowIdx < height; rowIdx++)
            {
                int srcRow = flipVertically ? rowIdx : height - 1 - rowIdx;
                ReadOnlySpan<byte> src = pixels.Slice(srcRow * strideBytes, width * comp);
                Span<byte> dst = sink.Reserve(rowOutLen);
                if (sink.Overflowed)
                {
                    return;
                }

                WriteRow24(src, dst, width, comp, pad);
            }
        }
        else
        {
            WriteHeader32V4(ref sink, width, height, (uint)outputSize);
            if (sink.Overflowed)
            {
                return;
            }

            int rowOutLen = width * 4;
            for (int rowIdx = 0; rowIdx < height; rowIdx++)
            {
                int srcRow = flipVertically ? rowIdx : height - 1 - rowIdx;
                ReadOnlySpan<byte> src = pixels.Slice(srcRow * strideBytes, rowOutLen);
                Span<byte> dst = sink.Reserve(rowOutLen);
                if (sink.Overflowed)
                {
                    return;
                }

                PixelWriteKernels.Rgba32ToBgra32(src, dst, width);
            }
        }
    }

    private static void WriteHeader24(ref ByteSink sink, int width, int height, uint fileSize)
    {
        Span<byte> h = sink.Reserve(54);
        if (sink.Overflowed)
        {
            return;
        }

        h.Clear();
        // BITMAPFILEHEADER (14 bytes)
        h[0] = (byte)'B';
        h[1] = (byte)'M';
        BinaryPrimitives.WriteUInt32LittleEndian(h.Slice(2, 4), fileSize);
        BinaryPrimitives.WriteUInt32LittleEndian(h.Slice(10, 4), 54u);

        // BITMAPINFOHEADER (40 bytes)
        BinaryPrimitives.WriteUInt32LittleEndian(h.Slice(14, 4), 40u);
        BinaryPrimitives.WriteInt32LittleEndian(h.Slice(18, 4), width);
        BinaryPrimitives.WriteInt32LittleEndian(h.Slice(22, 4), height);
        BinaryPrimitives.WriteUInt16LittleEndian(h.Slice(26, 2), 1);
        BinaryPrimitives.WriteUInt16LittleEndian(h.Slice(28, 2), 24);
    }

    private static void WriteHeader32V4(ref ByteSink sink, int width, int height, uint fileSize)
    {
        Span<byte> h = sink.Reserve(122);
        if (sink.Overflowed)
        {
            return;
        }

        h.Clear();
        // BITMAPFILEHEADER (14 bytes)
        h[0] = (byte)'B';
        h[1] = (byte)'M';
        BinaryPrimitives.WriteUInt32LittleEndian(h.Slice(2, 4), fileSize);
        BinaryPrimitives.WriteUInt32LittleEndian(h.Slice(10, 4), 122u);

        // BITMAPV4HEADER (108 bytes)
        BinaryPrimitives.WriteUInt32LittleEndian(h.Slice(14, 4), 108u);
        BinaryPrimitives.WriteInt32LittleEndian(h.Slice(18, 4), width);
        BinaryPrimitives.WriteInt32LittleEndian(h.Slice(22, 4), height);
        BinaryPrimitives.WriteUInt16LittleEndian(h.Slice(26, 2), 1);
        BinaryPrimitives.WriteUInt16LittleEndian(h.Slice(28, 2), 32);
        BinaryPrimitives.WriteUInt32LittleEndian(h.Slice(30, 4), 3u); // BI_BITFIELDS
        // Channel masks: R, G, B, A
        BinaryPrimitives.WriteUInt32LittleEndian(h.Slice(54, 4), 0x00FF0000u);
        BinaryPrimitives.WriteUInt32LittleEndian(h.Slice(58, 4), 0x0000FF00u);
        BinaryPrimitives.WriteUInt32LittleEndian(h.Slice(62, 4), 0x000000FFu);
        BinaryPrimitives.WriteUInt32LittleEndian(h.Slice(66, 4), 0xFF000000u);
        // CSType = LCS_sRGB ('sRGB' = 0x73524742)
        BinaryPrimitives.WriteUInt32LittleEndian(h.Slice(70, 4), 0x73524742u);
    }

    internal static void WriteRow24(ReadOnlySpan<byte> src, Span<byte> dst, int width, int comp, int pad)
    {
        if (comp == 3)
        {
            PixelWriteKernels.Rgb24ToBgr24(src, dst, width);
        }
        else if (comp == 1)
        {
            PixelWriteKernels.Grey8ToBgr24(src, dst, width);
        }
        else
        {
            PixelWriteKernels.GreyAlpha16ToBgr24(src, dst, width);
        }

        int d = width * 3;
        for (int p = 0; p < pad; p++)
        {
            dst[d + p] = 0;
        }
    }

    internal static void WriteRow24Scalar(ReadOnlySpan<byte> src, Span<byte> dst, int width, int comp, int pad)
    {
        int d = 0;
        if (comp == 3)
        {
            int s = 0;
            for (int x = 0; x < width; x++)
            {
                dst[d] = src[s + 2];
                dst[d + 1] = src[s + 1];
                dst[d + 2] = src[s];
                s += 3;
                d += 3;
            }
        }
        else if (comp == 1)
        {
            for (int x = 0; x < width; x++)
            {
                byte v = src[x];
                dst[d] = v;
                dst[d + 1] = v;
                dst[d + 2] = v;
                d += 3;
            }
        }
        else
        {
            // comp == 2: stb_image_write expands grey channel and ignores alpha
            int s = 0;
            for (int x = 0; x < width; x++)
            {
                byte v = src[s];
                dst[d] = v;
                dst[d + 1] = v;
                dst[d + 2] = v;
                s += 2;
                d += 3;
            }
        }

        for (int p = 0; p < pad; p++)
        {
            dst[d + p] = 0;
        }
    }

    internal static void WriteRow32Scalar(ReadOnlySpan<byte> src, Span<byte> dst, int width)
    {
        int len = width * 4;
        for (int i = 0; i < len; i += 4)
        {
            dst[i] = src[i + 2];
            dst[i + 1] = src[i + 1];
            dst[i + 2] = src[i];
            dst[i + 3] = src[i + 3];
        }
    }
}
