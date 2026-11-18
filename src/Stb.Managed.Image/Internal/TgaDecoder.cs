using System;
using System.Buffers.Binary;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Stb.Managed.Internal;

namespace Stb.Managed.Image.Internal;

/// <summary>
/// Truevision TGA decoder (uncompressed and RLE, truecolor 15/16/24/32-bit, grayscale 8/16-bit,
/// and 8/16-bit colormapped images) matching <c>stb_image.h</c>.
/// </summary>
internal static class TgaDecoder
{
    private struct TgaHeader
    {
        public int IdLength;
        public bool Indexed;
        public bool IsRle;
        public int PaletteStart;
        public int PaletteLength;
        public int Width;
        public int Height;
        public int BitsPerPixel;
        public int Descriptor;
        public int SourceComponents;
        public bool IsRgb16;
    }

    internal static bool IsTga(ReadOnlySpan<byte> data)
    {
        return TryParseHeader(data, out _, out _, out _);
    }

    internal static bool TryGetInfo(ReadOnlySpan<byte> data, StbLimits limits, out ImageInfo info)
    {
        if (!TryParseHeader(data, out TgaHeader header, out _, out _))
        {
            info = default;
            return false;
        }

        if ((uint)header.Width > (uint)limits.MaxWidth
            || (uint)header.Height > (uint)limits.MaxHeight
            || (long)header.Width * header.Height > limits.MaxPixels)
        {
            info = default;
            return false;
        }

        info = new ImageInfo(header.Width, header.Height, (ColorComponents)header.SourceComponents, 8, IsHdr: false);
        return true;
    }

    internal static ImageInfo GetInfo(ReadOnlySpan<byte> data, StbLimits limits)
    {
        if (!TryParseHeader(data, out TgaHeader header, out StbErrorCode errCode, out string errMsg))
        {
            throw new StbException(errCode, errMsg);
        }

        _ = limits.ValidateDimensions(header.Width, header.Height, header.SourceComponents);
        return new ImageInfo(header.Width, header.Height, (ColorComponents)header.SourceComponents, 8, IsHdr: false);
    }

    internal static bool TryDecode8(
        ReadOnlySpan<byte> data,
        Span<byte> destination,
        ColorComponents desiredComponents,
        ImageDecodeOptions options,
        out ImageInfo info,
        out ColorComponents outputComponents)
    {
        if (!TryParseHeader(data, out TgaHeader header, out StbErrorCode errCode, out string errMsg))
        {
            throw new StbException(errCode, errMsg);
        }

        StbLimits limits = options.Options.Limits;
        info = new ImageInfo(header.Width, header.Height, (ColorComponents)header.SourceComponents, 8, IsHdr: false);

        int dstChannels = desiredComponents == ColorComponents.Default ? header.SourceComponents : (int)desiredComponents;
        outputComponents = (ColorComponents)dstChannels;

        int requiredBytes = limits.ValidateDimensions(header.Width, header.Height, dstChannels);
        if (destination.Length < requiredBytes)
        {
            return false;
        }

        Span<byte> outSlice = destination.Slice(0, requiredBytes);

        // Single-pass zero-copy SIMD fast path for uncompressed 24/32-bit truecolor TGA without H-flip.
        if (!header.Indexed
            && !header.IsRle
            && !header.IsRgb16
            && (header.Descriptor & 0x10) == 0
            && header.SourceComponents is 3 or 4
            && dstChannels is 3 or 4)
        {
            DecodeUncompressedTruecolorDirect(data, header, dstChannels, outSlice, options.FlipVertically);
            return true;
        }

        // Direct RLE truecolor fast path when writing 3->3, 3->4, 4->4, or 4->3 without extra conversion buffer.
        if (!header.Indexed
            && header.IsRle
            && !header.IsRgb16
            && header.SourceComponents is 3 or 4
            && dstChannels is 3 or 4)
        {
            DecodeRleTruecolorDirect(data, header, dstChannels, outSlice, options.FlipVertically);
            return true;
        }

        int srcBytes = limits.ValidateDimensions(header.Width, header.Height, header.SourceComponents);

        if (dstChannels == header.SourceComponents)
        {
            DecodeRawPixels(data, header, outSlice, options.FlipVertically);
        }
        else
        {
            using PooledBuffer<byte> rawBuf = new(srcBytes);
            DecodeRawPixels(data, header, rawBuf.Span, options.FlipVertically);
            PixelConverter.ConvertFormat8(
                rawBuf.Span,
                outSlice,
                header.SourceComponents,
                dstChannels,
                header.Width * header.Height);
        }

        return true;
    }

    private static void DecodeUncompressedTruecolorDirect(
        ReadOnlySpan<byte> data,
        TgaHeader header,
        int dstChannels,
        Span<byte> outBuf,
        bool flipOnLoad)
    {
        int width = header.Width;
        int height = header.Height;
        int srcComp = header.SourceComponents;
        int pos = 18 + header.IdLength;
        if (pos > data.Length)
        {
            throw new StbException(StbErrorCode.TruncatedInput, "Unexpected end of file skipping TGA ID field.");
        }

        int srcRowBytes = width * srcComp;
        int totalSrcBytes = height * srcRowBytes;
        if (data.Length - pos < totalSrcBytes)
        {
            throw new StbException(StbErrorCode.TruncatedInput, "Premature end of file while reading TGA pixel data.");
        }

        bool bottomUp = (header.Descriptor & 0x20) == 0;
        bool effectiveFlip = bottomUp ^ flipOnLoad;
        int dstRowBytes = width * dstChannels;

        for (int y = 0; y < height; y++)
        {
            ReadOnlySpan<byte> srcRow = data.Slice(pos + (y * srcRowBytes), srcRowBytes);
            Span<byte> dstRow = outBuf.Slice((effectiveFlip ? (height - 1 - y) : y) * dstRowBytes, dstRowBytes);
            if (srcComp == 3)
            {
                if (dstChannels == 3)
                {
                    PixelConverter.Bgr24ToRgb24(srcRow, dstRow, width);
                }
                else
                {
                    PixelConverter.Bgr24ToRgba32(srcRow, dstRow, width);
                }
            }
            else if (dstChannels == 4)
            {
                PixelConverter.Bgra32ToRgba32(srcRow, dstRow, width, alphaOverride: 0, out _);
            }
            else
            {
                PixelConverter.Bgra32ToRgb24(srcRow, dstRow, width);
            }
        }
    }

    private static void DecodeRleTruecolorDirect(
        ReadOnlySpan<byte> data,
        TgaHeader header,
        int dstChannels,
        Span<byte> outBuf,
        bool flipOnLoad)
    {
        int width = header.Width;
        int height = header.Height;
        int srcComp = header.SourceComponents;
        int pixelCount = width * height;
        int pos = 18 + header.IdLength;
        if (pos > data.Length)
        {
            throw new StbException(StbErrorCode.TruncatedInput, "Unexpected end of file skipping TGA ID field.");
        }

        int i = 0;
        while (i < pixelCount)
        {
            if (pos >= data.Length)
            {
                throw new StbException(StbErrorCode.TruncatedInput, "Premature end of file inside TGA RLE packet.");
            }

            byte cmd = data[pos++];
            int count = Math.Min(1 + (cmd & 127), pixelCount - i);
            if ((cmd & 0x80) != 0)
            {
                // Run-length packet: one pixel repeated `count` times (swizzle B<->R once).
                if (data.Length - pos < srcComp)
                {
                    throw new StbException(StbErrorCode.TruncatedInput, "Premature end of file reading TGA RLE pixel.");
                }

                byte b = data[pos];
                byte g = data[pos + 1];
                byte r = data[pos + 2];
                byte a = srcComp == 4 ? data[pos + 3] : (byte)255;
                pos += srcComp;

                if (dstChannels == 4)
                {
                    Span<byte> dstRun = outBuf.Slice(i * 4, count * 4);
                    if (BitConverter.IsLittleEndian)
                    {
                        uint rgba = (uint)r | ((uint)g << 8) | ((uint)b << 16) | ((uint)a << 24);
                        MemoryMarshal.Cast<byte, uint>(dstRun).Fill(rgba);
                    }
                    else
                    {
                        for (int k = 0; k < dstRun.Length; k += 4)
                        {
                            dstRun[k] = r;
                            dstRun[k + 1] = g;
                            dstRun[k + 2] = b;
                            dstRun[k + 3] = a;
                        }
                    }
                }
                else
                {
                    int d = i * 3;
                    for (int k = 0; k < count; k++, d += 3)
                    {
                        outBuf[d] = r;
                        outBuf[d + 1] = g;
                        outBuf[d + 2] = b;
                    }
                }
            }
            else
            {
                // Raw packet: `count` consecutive pixels.
                int srcBytesNeeded = count * srcComp;
                if (data.Length - pos < srcBytesNeeded)
                {
                    throw new StbException(StbErrorCode.TruncatedInput, "Premature end of file reading TGA RLE raw run.");
                }

                ReadOnlySpan<byte> srcRun = data.Slice(pos, srcBytesNeeded);
                Span<byte> dstRun = outBuf.Slice(i * dstChannels, count * dstChannels);
                if (srcComp == 3)
                {
                    if (dstChannels == 3)
                    {
                        PixelConverter.Bgr24ToRgb24(srcRun, dstRun, count);
                    }
                    else
                    {
                        PixelConverter.Bgr24ToRgba32(srcRun, dstRun, count);
                    }
                }
                else if (dstChannels == 4)
                {
                    PixelConverter.Bgra32ToRgba32(srcRun, dstRun, count, alphaOverride: 0, out _);
                }
                else
                {
                    PixelConverter.Bgra32ToRgb24(srcRun, dstRun, count);
                }

                pos += srcBytesNeeded;
            }

            i += count;
        }

        if ((header.Descriptor & 0x10) != 0)
        {
            PixelConverter.FlipHorizontallyInPlace(outBuf, width, height, dstChannels);
        }

        bool bottomUp = (header.Descriptor & 0x20) == 0;
        if (bottomUp ^ flipOnLoad)
        {
            PixelConverter.VerticalFlip(outBuf, height, width * dstChannels);
        }
    }

    private static void DecodeRawPixels(
        ReadOnlySpan<byte> data,
        TgaHeader header,
        Span<byte> outBuf,
        bool flipOnLoad)
    {
        int width = header.Width;
        int height = header.Height;
        int comp = header.SourceComponents;
        int pixelCount = width * height;
        int pos = 18 + header.IdLength;
        if (pos > data.Length)
        {
            throw new StbException(StbErrorCode.TruncatedInput, "Unexpected end of file skipping TGA ID field.");
        }

        if (!header.Indexed && !header.IsRle && !header.IsRgb16)
        {
            int byteCount = pixelCount * comp;
            if (data.Length - pos < byteCount)
            {
                throw new StbException(StbErrorCode.TruncatedInput, "Premature end of file while reading TGA pixel data.");
            }

            data.Slice(pos, byteCount).CopyTo(outBuf);
        }
        else
        {
            using PooledBuffer<byte> paletteBuf = header.Indexed
                ? LoadPalette(data, ref pos, header)
                : default;
            ReadOnlySpan<byte> palette = header.Indexed ? paletteBuf.Span : ReadOnlySpan<byte>.Empty;

            Span<byte> rawPixel = stackalloc byte[4];
            rawPixel.Clear();
            int rleCount = 0;
            bool rleRepeating = false;

            for (int i = 0; i < pixelCount; i++)
            {
                bool readNextPixel = false;
                if (header.IsRle)
                {
                    if (rleCount == 0)
                    {
                        if (pos >= data.Length)
                        {
                            throw new StbException(StbErrorCode.TruncatedInput, "Premature end of file inside TGA RLE packet.");
                        }

                        byte cmd = data[pos++];
                        rleCount = 1 + (cmd & 127);
                        rleRepeating = (cmd >> 7) != 0;
                        readNextPixel = true;
                    }
                    else if (!rleRepeating)
                    {
                        readNextPixel = true;
                    }
                }
                else
                {
                    readNextPixel = true;
                }

                if (readNextPixel)
                {
                    if (header.Indexed)
                    {
                        int palIdx;
                        if (header.BitsPerPixel == 8)
                        {
                            if (pos >= data.Length)
                            {
                                throw new StbException(StbErrorCode.TruncatedInput, "Premature end of file reading 8-bit TGA index.");
                            }

                            palIdx = data[pos++];
                        }
                        else
                        {
                            if (data.Length - pos < 2)
                            {
                                throw new StbException(StbErrorCode.TruncatedInput, "Premature end of file reading 16-bit TGA index.");
                            }

                            palIdx = BinaryPrimitives.ReadUInt16LittleEndian(data.Slice(pos, 2));
                            pos += 2;
                        }

                        if (palIdx >= header.PaletteLength)
                        {
                            palIdx = 0;
                        }

                        int palOff = palIdx * comp;
                        for (int k = 0; k < comp; k++)
                        {
                            rawPixel[k] = palette[palOff + k];
                        }
                    }
                    else if (header.IsRgb16)
                    {
                        if (data.Length - pos < 2)
                        {
                            throw new StbException(StbErrorCode.TruncatedInput, "Premature end of file reading 16-bit TGA pixel.");
                        }

                        ReadRgb16(data, ref pos, rawPixel);
                    }
                    else
                    {
                        if (data.Length - pos < comp)
                        {
                            throw new StbException(StbErrorCode.TruncatedInput, "Premature end of file reading TGA pixel.");
                        }

                        for (int k = 0; k < comp; k++)
                        {
                            rawPixel[k] = data[pos++];
                        }
                    }
                }

                int dstOff = i * comp;
                for (int k = 0; k < comp; k++)
                {
                    outBuf[dstOff + k] = rawPixel[k];
                }

                rleCount--;
            }
        }

        if ((header.Descriptor & 0x10) != 0)
        {
            PixelConverter.FlipHorizontallyInPlace(outBuf, width, height, comp);
        }

        bool bottomUp = (header.Descriptor & 0x20) == 0;
        if (bottomUp ^ flipOnLoad)
        {
            PixelConverter.VerticalFlip(outBuf, height, width * comp);
        }

        if (comp >= 3 && !header.IsRgb16)
        {
            PixelConverter.SwapRedBlueInPlace(outBuf, pixelCount, comp);
        }
    }

    private static PooledBuffer<byte> LoadPalette(ReadOnlySpan<byte> data, ref int pos, TgaHeader header)
    {
        if (header.PaletteLength <= 0)
        {
            throw new StbException(StbErrorCode.InvalidData, "Corrupt TGA: indexed image has empty palette.");
        }

        pos += header.PaletteStart;
        if (pos > data.Length)
        {
            throw new StbException(StbErrorCode.TruncatedInput, "Unexpected end of file skipping TGA palette start.");
        }

        int comp = header.SourceComponents;
        int paletteBytes = checked(header.PaletteLength * comp);
        PooledBuffer<byte> paletteBuf = new(paletteBytes);
        Span<byte> pal = paletteBuf.Span;

        if (header.IsRgb16)
        {
            if (data.Length - pos < header.PaletteLength * 2)
            {
                paletteBuf.Dispose();
                throw new StbException(StbErrorCode.TruncatedInput, "Unexpected end of file reading 16-bit TGA palette.");
            }

            for (int i = 0; i < header.PaletteLength; i++)
            {
                ReadRgb16(data, ref pos, pal.Slice(i * comp, comp));
            }
        }
        else
        {
            if (data.Length - pos < paletteBytes)
            {
                paletteBuf.Dispose();
                throw new StbException(StbErrorCode.TruncatedInput, "Unexpected end of file reading TGA palette.");
            }

            data.Slice(pos, paletteBytes).CopyTo(pal);
            pos += paletteBytes;
        }

        return paletteBuf;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void ReadRgb16(ReadOnlySpan<byte> data, ref int pos, Span<byte> dst)
    {
        ushort px = BinaryPrimitives.ReadUInt16LittleEndian(data.Slice(pos, 2));
        pos += 2;
        int r = (px >> 10) & 31;
        int g = (px >> 5) & 31;
        int b = px & 31;
        dst[0] = (byte)((r * 255) / 31);
        dst[1] = (byte)((g * 255) / 31);
        dst[2] = (byte)((b * 255) / 31);
    }

    private static int GetComponents(int bitsPerPixel, bool isGrey, out bool isRgb16)
    {
        isRgb16 = false;
        if (isGrey && bitsPerPixel == 16)
        {
            return 2;
        }

        if (bitsPerPixel is 15 or 16)
        {
            isRgb16 = true;
            return 3;
        }

        return bitsPerPixel switch
        {
            8 => 1,
            24 => 3,
            32 => 4,
            _ => 0,
        };
    }

    private static bool TryParseHeader(
        ReadOnlySpan<byte> data,
        out TgaHeader header,
        out StbErrorCode errorCode,
        out string errorMessage)
    {
        header = default;
        if (data.Length < 18)
        {
            errorCode = StbErrorCode.InvalidData;
            errorMessage = "Input is too short to be a valid TGA header.";
            return false;
        }

        int idLength = data[0];
        int colormapType = data[1];
        if (colormapType > 1)
        {
            errorCode = StbErrorCode.InvalidData;
            errorMessage = $"Unsupported TGA colormap type {colormapType}.";
            return false;
        }

        int imageType = data[2];
        int paletteStart = BinaryPrimitives.ReadUInt16LittleEndian(data.Slice(3, 2));
        int paletteLen = BinaryPrimitives.ReadUInt16LittleEndian(data.Slice(5, 2));
        int paletteBits = data[7];
        int width = BinaryPrimitives.ReadUInt16LittleEndian(data.Slice(12, 2));
        int height = BinaryPrimitives.ReadUInt16LittleEndian(data.Slice(14, 2));
        int bpp = data[16];
        int desc = data[17];

        if (colormapType == 1)
        {
            if (imageType is not (1 or 9))
            {
                errorCode = StbErrorCode.InvalidData;
                errorMessage = $"Invalid TGA colormapped image type {imageType}.";
                return false;
            }

            if (paletteBits is not (8 or 15 or 16 or 24 or 32))
            {
                errorCode = StbErrorCode.InvalidData;
                errorMessage = $"Invalid TGA palette entry size {paletteBits}.";
                return false;
            }

            if (bpp is not (8 or 16))
            {
                errorCode = StbErrorCode.InvalidData;
                errorMessage = $"Invalid TGA indexed pixel depth {bpp}.";
                return false;
            }
        }
        else
        {
            if (imageType is not (2 or 3 or 10 or 11))
            {
                errorCode = StbErrorCode.InvalidData;
                errorMessage = $"Unsupported TGA image type {imageType}.";
                return false;
            }
        }

        if (width < 1 || height < 1)
        {
            errorCode = StbErrorCode.InvalidData;
            errorMessage = $"Invalid TGA dimensions {width}x{height}.";
            return false;
        }

        bool isRle = imageType >= 8;
        int baseType = isRle ? imageType - 8 : imageType;
        int comp = colormapType == 1
            ? GetComponents(paletteBits, isGrey: false, out bool isRgb16)
            : GetComponents(bpp, isGrey: baseType == 3, out isRgb16);

        if (comp == 0)
        {
            errorCode = StbErrorCode.InvalidData;
            errorMessage = $"Unsupported TGA pixel depth {bpp}.";
            return false;
        }

        header = new TgaHeader
        {
            IdLength = idLength,
            Indexed = colormapType == 1,
            IsRle = isRle,
            PaletteStart = paletteStart,
            PaletteLength = paletteLen,
            Width = width,
            Height = height,
            BitsPerPixel = bpp,
            Descriptor = desc,
            SourceComponents = comp,
            IsRgb16 = isRgb16,
        };
        errorCode = StbErrorCode.Unknown;
        errorMessage = string.Empty;
        return true;
    }
}
