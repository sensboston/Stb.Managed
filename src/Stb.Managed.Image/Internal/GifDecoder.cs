using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using Stb.Managed.Internal;

namespace Stb.Managed.Image.Internal;

/// <summary>
/// GIF87a / GIF89a decoder (single-frame and multi-frame animation with LZW, interlacing,
/// local/global color tables, transparency, and disposal modes 0–3) matching <c>stb_image.h</c>.
/// </summary>
internal static class GifDecoder
{
    private struct GifState
    {
        public int Width;
        public int Height;
        public int Flags;
        public int BgIndex;
        public int Transparent;
        public int EFlags;
        public int Delay;
        public int LineSize;
        public int StartX;
        public int StartY;
        public int MaxX;
        public int MaxY;
        public int CurX;
        public int CurY;
        public int Step;
        public int Parse;
        public bool HasCanvas;
    }

    internal static bool IsGif(ReadOnlySpan<byte> data)
    {
        return data.Length >= 6
            && data[0] == (byte)'G'
            && data[1] == (byte)'I'
            && data[2] == (byte)'F'
            && data[3] == (byte)'8'
            && (data[4] == (byte)'7' || data[4] == (byte)'9')
            && data[5] == (byte)'a';
    }

    internal static bool TryGetInfo(ReadOnlySpan<byte> data, StbLimits limits, out ImageInfo info)
    {
        if (!TryParseHeaderInfo(data, out int width, out int height, out _, out _, out _, out _))
        {
            info = default;
            return false;
        }

        if ((uint)width > (uint)limits.MaxWidth
            || (uint)height > (uint)limits.MaxHeight
            || (long)width * height > limits.MaxPixels)
        {
            info = default;
            return false;
        }

        info = new ImageInfo(width, height, ColorComponents.RedGreenBlueAlpha, 8, IsHdr: false);
        return true;
    }

    internal static ImageInfo GetInfo(ReadOnlySpan<byte> data, StbLimits limits)
    {
        if (!TryParseHeaderInfo(data, out int width, out int height, out _, out _, out StbErrorCode errCode, out string errMsg))
        {
            throw new StbException(errCode, errMsg);
        }

        _ = limits.ValidateDimensions(width, height, 4);
        return new ImageInfo(width, height, ColorComponents.RedGreenBlueAlpha, 8, IsHdr: false);
    }

    internal static bool TryDecode8(
        ReadOnlySpan<byte> data,
        Span<byte> destination,
        ColorComponents desiredComponents,
        ImageDecodeOptions options,
        out ImageInfo info,
        out ColorComponents outputComponents)
    {
        if (!TryParseHeaderInfo(data, out int width, out int height, out int flags, out int bgIndex, out StbErrorCode errCode, out string errMsg))
        {
            throw new StbException(errCode, errMsg);
        }

        StbLimits limits = options.Options.Limits;
        info = new ImageInfo(width, height, ColorComponents.RedGreenBlueAlpha, 8, IsHdr: false);

        int dstChannels = desiredComponents == ColorComponents.Default ? 4 : (int)desiredComponents;
        outputComponents = (ColorComponents)dstChannels;

        int requiredBytes = limits.ValidateDimensions(width, height, dstChannels);
        if (destination.Length < requiredBytes)
        {
            return false;
        }

        int rgbaBytes = limits.ValidateDimensions(width, height, 4);
        int pos = 13;
        Span<byte> globalPal = stackalloc byte[256 * 4];
        Span<byte> localPal = stackalloc byte[256 * 4];
        InitOpaqueBlackPalette(globalPal);
        InitOpaqueBlackPalette(localPal);

        GifState state = InitState(data, ref pos, width, height, flags, bgIndex, globalPal);
        using PooledBuffer<ushort> prefixBuf = new(4097);
        using PooledBuffer<byte> suffixBuf = new(4097);
        using PooledBuffer<byte> revBuf = new(4096);

        Span<byte> outSlice = destination.Slice(0, requiredBytes);
        if (dstChannels == 4)
        {
            bool gotFrame = DecodeNextFrame(
                data,
                ref pos,
                ref state,
                outSlice,
                ReadOnlySpan<byte>.Empty,
                globalPal,
                localPal,
                prefixBuf.Span,
                suffixBuf.Span,
                revBuf.Span);
            if (!gotFrame)
            {
                throw new StbException(StbErrorCode.InvalidData, "GIF image ended before any frame was found.");
            }
        }
        else
        {
            using PooledBuffer<byte> rgbaBuf = new(rgbaBytes);
            bool gotFrame = DecodeNextFrame(
                data,
                ref pos,
                ref state,
                rgbaBuf.Span,
                ReadOnlySpan<byte>.Empty,
                globalPal,
                localPal,
                prefixBuf.Span,
                suffixBuf.Span,
                revBuf.Span);
            if (!gotFrame)
            {
                throw new StbException(StbErrorCode.InvalidData, "GIF image ended before any frame was found.");
            }

            PixelConverter.ConvertFormat8(rgbaBuf.Span, outSlice, 4, dstChannels, width * height);
        }

        if (options.FlipVertically)
        {
            PixelConverter.VerticalFlip(outSlice, height, width * dstChannels);
        }

        return true;
    }

    internal static GifAnimation DecodeAnimation(
        ReadOnlySpan<byte> data,
        ColorComponents desiredComponents,
        ImageDecodeOptions options)
    {
        if (!TryParseHeaderInfo(data, out int width, out int height, out int flags, out int bgIndex, out StbErrorCode errCode, out string errMsg))
        {
            throw new StbException(errCode, errMsg);
        }

        StbLimits limits = options.Options.Limits;
        int dstChannels = desiredComponents == ColorComponents.Default ? 4 : (int)desiredComponents;
        int rgbaStride = limits.ValidateDimensions(width, height, 4);
        int dstStride = limits.ValidateDimensions(width, height, dstChannels);

        int pos = 13;
        Span<byte> globalPal = stackalloc byte[256 * 4];
        Span<byte> localPal = stackalloc byte[256 * 4];
        InitOpaqueBlackPalette(globalPal);
        InitOpaqueBlackPalette(localPal);

        GifState state = InitState(data, ref pos, width, height, flags, bgIndex, globalPal);
        using PooledBuffer<byte> canvasBuf = new(rgbaStride);
        using PooledBuffer<ushort> prefixBuf = new(4097);
        using PooledBuffer<byte> suffixBuf = new(4097);
        using PooledBuffer<byte> revBuf = new(4096);

        List<byte[]> rgbaFrames = new();
        List<int> delays = new();
        byte[]? savedPixels = null;
        long totalRgbaBytes = 0;

        while (true)
        {
            bool gotFrame = DecodeNextFrame(
                data,
                ref pos,
                ref state,
                canvasBuf.Span,
                savedPixels is null ? ReadOnlySpan<byte>.Empty : savedPixels,
                globalPal,
                localPal,
                prefixBuf.Span,
                suffixBuf.Span,
                revBuf.Span);
            if (!gotFrame)
            {
                break;
            }

            limits.ValidateFrameCount(rgbaFrames.Count + 1);
            totalRgbaBytes += rgbaStride;
            limits.ValidateAllocation(totalRgbaBytes);
            limits.ValidateDecompressedSize(totalRgbaBytes);

            byte[] frameCopy = new byte[rgbaStride];
            canvasBuf.Span.CopyTo(frameCopy);
            rgbaFrames.Add(frameCopy);
            delays.Add(state.Delay);

            if ((state.EFlags & 0x1C) != 0x0C)
            {
                savedPixels = frameCopy;
            }
        }

        int frameCount = rgbaFrames.Count;
        long totalDstBytes = (long)frameCount * dstStride;
        limits.ValidateAllocation(totalDstBytes);

        byte[] allPixels = totalDstBytes == 0 ? Array.Empty<byte>() : new byte[(int)totalDstBytes];
        GifFrame[] frames = new GifFrame[frameCount];
        int pixelCountPerFrame = width * height;

        for (int i = 0; i < frameCount; i++)
        {
            Span<byte> frameSrc = rgbaFrames[i];
            if (options.FlipVertically)
            {
                PixelConverter.VerticalFlip(frameSrc, height, width * 4);
            }

            int offset = i * dstStride;
            Span<byte> frameDst = allPixels.AsSpan(offset, dstStride);
            if (dstChannels == 4)
            {
                frameSrc.CopyTo(frameDst);
            }
            else
            {
                PixelConverter.ConvertFormat8(frameSrc, frameDst, 4, dstChannels, pixelCountPerFrame);
            }

            frames[i] = new GifFrame(
                width,
                height,
                (ColorComponents)dstChannels,
                delays[i],
                new ReadOnlyMemory<byte>(allPixels, offset, dstStride));
        }

        return new GifAnimation(
            width,
            height,
            (ColorComponents)dstChannels,
            ColorComponents.RedGreenBlueAlpha,
            allPixels,
            frames);
    }

    private static GifState InitState(
        ReadOnlySpan<byte> data,
        ref int pos,
        int width,
        int height,
        int flags,
        int bgIndex,
        Span<byte> globalPal)
    {
        GifState state = new()
        {
            Width = width,
            Height = height,
            Flags = flags,
            BgIndex = bgIndex,
            Transparent = -1,
        };

        if ((flags & 0x80) != 0)
        {
            int numEntries = 2 << (flags & 7);
            ParseColorTable(data, ref pos, globalPal, numEntries);
        }
        else
        {
            state.BgIndex = -1;
        }

        return state;
    }

    private static bool DecodeNextFrame(
        ReadOnlySpan<byte> data,
        ref int pos,
        ref GifState g,
        Span<byte> canvas,
        ReadOnlySpan<byte> saved,
        ReadOnlySpan<byte> globalPal,
        Span<byte> localPal,
        Span<ushort> prefix,
        Span<byte> suffix,
        Span<byte> revBuf)
    {
        bool firstFrame = false;
        if (!g.HasCanvas)
        {
            g.HasCanvas = true;
            firstFrame = true;
            canvas.Clear();
        }
        else
        {
            int dispose = (g.EFlags & 0x1C) >> 2;
            if (dispose == 3 && saved.IsEmpty)
            {
                dispose = 2;
            }

            if (dispose == 3)
            {
                int copyLen = g.MaxX - g.StartX;
                for (int y = g.StartY; y < g.MaxY; y += g.LineSize)
                {
                    saved.Slice(y + g.StartX, copyLen).CopyTo(canvas.Slice(y + g.StartX, copyLen));
                }
            }
            else if (dispose == 2)
            {
                uint bgPixel = 0u;
                if (g.BgIndex >= 0 && g.Transparent < 0)
                {
                    bgPixel = MemoryMarshal.Cast<byte, uint>(globalPal)[g.BgIndex];
                }

                Span<uint> canvas32 = MemoryMarshal.Cast<byte, uint>(canvas);
                int startPxX = g.StartX >> 2;
                int widthPx = (g.MaxX - g.StartX) >> 2;
                int linePx = g.LineSize >> 2;
                int maxRowPx = g.MaxY >> 2;
                for (int y = g.StartY >> 2; y < maxRowPx; y += linePx)
                {
                    canvas32.Slice(y + startPxX, widthPx).Fill(bgPixel);
                }
            }
        }

        while (true)
        {
            if (pos >= data.Length)
            {
                throw new StbException(StbErrorCode.TruncatedInput, "Unexpected end of file inside GIF stream.");
            }

            byte tag = data[pos++];
            switch (tag)
            {
                case 0x2C: // Image Descriptor
                {
                    if (data.Length - pos < 9)
                    {
                        throw new StbException(StbErrorCode.TruncatedInput, "Truncated GIF image descriptor.");
                    }

                    int x = BinaryPrimitives.ReadUInt16LittleEndian(data.Slice(pos, 2));
                    int y = BinaryPrimitives.ReadUInt16LittleEndian(data.Slice(pos + 2, 2));
                    int w = BinaryPrimitives.ReadUInt16LittleEndian(data.Slice(pos + 4, 2));
                    int h = BinaryPrimitives.ReadUInt16LittleEndian(data.Slice(pos + 6, 2));
                    int lflags = data[pos + 8];
                    pos += 9;

                    if (x + w > g.Width || y + h > g.Height)
                    {
                        throw new StbException(StbErrorCode.InvalidData, "Corrupt GIF: image descriptor exceeds logical screen bounds.");
                    }

                    g.LineSize = g.Width * 4;
                    g.StartX = x * 4;
                    g.StartY = y * g.LineSize;
                    g.MaxX = g.StartX + (w * 4);
                    g.MaxY = g.StartY + (h * g.LineSize);
                    g.CurX = g.StartX;
                    g.CurY = w == 0 ? g.MaxY : g.StartY;

                    if ((lflags & 0x40) != 0)
                    {
                        g.Step = 8 * g.LineSize;
                        g.Parse = 3;
                    }
                    else
                    {
                        g.Step = g.LineSize;
                        g.Parse = 0;
                    }

                    ReadOnlySpan<byte> colorTable;
                    if ((lflags & 0x80) != 0)
                    {
                        int numEntries = 2 << (lflags & 7);
                        ParseColorTable(data, ref pos, localPal, numEntries);
                        colorTable = localPal;
                    }
                    else if ((g.Flags & 0x80) != 0)
                    {
                        colorTable = globalPal;
                    }
                    else
                    {
                        throw new StbException(StbErrorCode.InvalidData, "Corrupt GIF: missing color table.");
                    }

                    if (firstFrame && g.BgIndex >= 0 && g.Transparent < 0)
                    {
                        uint bgPixel = MemoryMarshal.Cast<byte, uint>(globalPal)[g.BgIndex];
                        MemoryMarshal.Cast<byte, uint>(canvas).Fill(bgPixel);
                    }

                    ProcessGifRaster(data, ref pos, ref g, canvas, colorTable, prefix, suffix, revBuf);
                    return true;
                }

                case 0x21: // Extension
                {
                    if (pos >= data.Length)
                    {
                        throw new StbException(StbErrorCode.TruncatedInput, "Truncated GIF extension.");
                    }

                    byte ext = data[pos++];
                    if (ext == 0xF9)
                    {
                        if (pos >= data.Length)
                        {
                            throw new StbException(StbErrorCode.TruncatedInput, "Truncated GIF Graphic Control Extension.");
                        }

                        byte len = data[pos++];
                        if (len == 4)
                        {
                            if (data.Length - pos < 4)
                            {
                                throw new StbException(StbErrorCode.TruncatedInput, "Truncated GIF Graphic Control Extension payload.");
                            }

                            g.EFlags = data[pos];
                            g.Delay = 10 * BinaryPrimitives.ReadUInt16LittleEndian(data.Slice(pos + 1, 2));
                            g.Transparent = data[pos + 3];
                            pos += 4;
                            if ((g.EFlags & 0x01) == 0)
                            {
                                g.Transparent = -1;
                            }
                        }
                        else
                        {
                            if (data.Length - pos < len)
                            {
                                throw new StbException(StbErrorCode.TruncatedInput, "Truncated GIF Graphic Control Extension block.");
                            }

                            pos += len;
                            break;
                        }
                    }

                    SkipSubBlocks(data, ref pos);
                    break;
                }

                case 0x3B: // Trailer
                    return false;

                default:
                    throw new StbException(StbErrorCode.InvalidData, $"Corrupt GIF: unknown block introducer 0x{tag:X2}.");
            }
        }
    }

    private static void ProcessGifRaster(
        ReadOnlySpan<byte> data,
        ref int pos,
        ref GifState g,
        Span<byte> canvas,
        ReadOnlySpan<byte> colorTable,
        Span<ushort> prefix,
        Span<byte> suffix,
        Span<byte> revBuf)
    {
        if (pos >= data.Length)
        {
            throw new StbException(StbErrorCode.TruncatedInput, "Truncated GIF raster LZW code size.");
        }

        byte lzwCs = data[pos++];
        if (lzwCs > 11)
        {
            throw new StbException(StbErrorCode.CorruptStream, $"Corrupt GIF LZW minimum code size {lzwCs}.");
        }

        int clear = 1 << lzwCs;
        bool first = true;
        int codesize = lzwCs + 1;
        uint codemask = (1u << codesize) - 1u;
        uint bits = 0;
        int validBits = 0;

        for (int initCode = 0; initCode < clear; initCode++)
        {
            prefix[initCode] = 0;
            suffix[initCode] = (byte)initCode;
        }

        int avail = clear + 2;
        int oldCode = -1;
        int blockLen = 0;

        Span<uint> canvas32 = MemoryMarshal.Cast<byte, uint>(canvas);
        ReadOnlySpan<uint> pal32 = MemoryMarshal.Cast<byte, uint>(colorTable);
        int startX = g.StartX >> 2;
        int maxX = g.MaxX >> 2;
        int startY = g.StartY >> 2;
        int maxY = g.MaxY >> 2;
        int lineSize = g.LineSize >> 2;
        int curX = g.CurX >> 2;
        int curY = g.CurY >> 2;
        int step = g.Step >> 2;
        int parse = g.Parse;
        int transparent = g.Transparent;

        while (true)
        {
            while (validBits < codesize)
            {
                if (blockLen == 0)
                {
                    if (pos >= data.Length)
                    {
                        throw new StbException(StbErrorCode.TruncatedInput, "Unexpected end of file inside GIF LZW sub-blocks.");
                    }

                    blockLen = data[pos++];
                    if (blockLen == 0)
                    {
                        return;
                    }
                }

                if (blockLen >= 2 && data.Length - pos >= 2)
                {
                    bits |= (uint)BinaryPrimitives.ReadUInt16LittleEndian(data.Slice(pos, 2)) << validBits;
                    pos += 2;
                    blockLen -= 2;
                    validBits += 16;
                    break;
                }

                if (pos >= data.Length)
                {
                    throw new StbException(StbErrorCode.TruncatedInput, "Unexpected end of file inside GIF LZW data block.");
                }

                blockLen--;
                bits |= (uint)data[pos++] << validBits;
                validBits += 8;
            }

            int code = (int)(bits & codemask);
            bits >>= codesize;
            validBits -= codesize;

            if (code == clear)
            {
                codesize = lzwCs + 1;
                codemask = (1u << codesize) - 1u;
                avail = clear + 2;
                oldCode = -1;
                first = false;
            }
            else if (code == clear + 1)
            {
                if (data.Length - pos < blockLen)
                {
                    throw new StbException(StbErrorCode.TruncatedInput, "Truncated GIF LZW block after EOI code.");
                }

                pos += blockLen;
                SkipSubBlocks(data, ref pos);
                return;
            }
            else if (code <= avail)
            {
                if (first)
                {
                    throw new StbException(StbErrorCode.CorruptStream, "Corrupt GIF: missing initial LZW clear code.");
                }

                int revInd = 4096;
                int cur = code;
                if (code == avail)
                {
                    if (oldCode < 0)
                    {
                        throw new StbException(StbErrorCode.CorruptStream, "Corrupt GIF: illegal LZW code in raster.");
                    }

                    cur = oldCode;
                    while (cur >= clear)
                    {
                        cur = prefix[cur];
                    }

                    revBuf[--revInd] = (byte)cur;
                    cur = oldCode;
                }

                for (; cur >= clear; cur = prefix[cur])
                {
                    revBuf[--revInd] = suffix[cur];
                }

                revBuf[--revInd] = (byte)cur;
                if (oldCode >= 0)
                {
                    prefix[avail] = (ushort)oldCode;
                    suffix[avail] = (byte)cur;
                    if (avail < 4096)
                    {
                        avail++;
                        if (avail > (int)codemask && avail < 4096)
                        {
                            codesize++;
                            codemask = (1u << codesize) - 1u;
                        }
                    }
                }

                oldCode = code;

                int strLen = 4096 - revInd;
                if (parse == 0 && curY < maxY && curX + strLen <= maxX)
                {
                    Span<uint> dstRun = canvas32.Slice(curY + curX, strLen);
                    ReadOnlySpan<byte> indices = revBuf.Slice(revInd, strLen);
                    if (transparent < 0)
                    {
                        for (int k = 0; k < strLen; k++)
                        {
                            dstRun[k] = pal32[indices[k]];
                        }
                    }
                    else
                    {
                        byte transp = (byte)transparent;
                        for (int k = 0; k < strLen; k++)
                        {
                            byte col = indices[k];
                            if (col != transp)
                            {
                                dstRun[k] = pal32[col];
                            }
                        }
                    }

                    curX += strLen;
                    if (curX == maxX)
                    {
                        curX = startX;
                        curY += step;
                    }
                }
                else
                {
                    for (; revInd < 4096 && curY < maxY; revInd++)
                    {
                        byte col = revBuf[revInd];
                        if (col != transparent)
                        {
                            canvas32[curY + curX] = pal32[col];
                        }

                        curX++;
                        if (curX >= maxX)
                        {
                            curX = startX;
                            curY += step;

                            while (curY >= maxY && parse > 0)
                            {
                                step = (1 << parse) * lineSize;
                                curY = startY + (step >> 1);
                                parse--;
                            }
                        }
                    }
                }
            }
            else
            {
                throw new StbException(StbErrorCode.CorruptStream, $"Corrupt GIF: illegal LZW code {code} (avail={avail}).");
            }
        }
    }

    private static void InitOpaqueBlackPalette(Span<byte> pal)
    {
        pal.Clear();
        for (int i = 3; i < pal.Length; i += 4)
        {
            pal[i] = 255;
        }
    }

    private static void ParseColorTable(ReadOnlySpan<byte> data, ref int pos, Span<byte> pal, int numEntries)
    {
        int bytes = numEntries * 3;
        if (data.Length - pos < bytes)
        {
            throw new StbException(StbErrorCode.TruncatedInput, "Unexpected end of file reading GIF color table.");
        }

        PixelConverter.Rgb24ToRgba32(data.Slice(pos, bytes), pal.Slice(0, numEntries * 4), numEntries);
        if (numEntries < 256)
        {
            InitOpaqueBlackPalette(pal.Slice(numEntries * 4));
        }

        pos += bytes;
    }

    private static void SkipSubBlocks(ReadOnlySpan<byte> data, ref int pos)
    {
        while (true)
        {
            if (pos >= data.Length)
            {
                throw new StbException(StbErrorCode.TruncatedInput, "Unexpected end of file skipping GIF sub-blocks.");
            }

            byte len = data[pos++];
            if (len == 0)
            {
                return;
            }

            if (data.Length - pos < len)
            {
                throw new StbException(StbErrorCode.TruncatedInput, "Truncated GIF extension sub-block.");
            }

            pos += len;
        }
    }

    private static bool TryParseHeaderInfo(
        ReadOnlySpan<byte> data,
        out int width,
        out int height,
        out int flags,
        out int bgIndex,
        out StbErrorCode errorCode,
        out string errorMessage)
    {
        width = 0;
        height = 0;
        flags = 0;
        bgIndex = 0;

        if (!IsGif(data))
        {
            errorCode = StbErrorCode.InvalidData;
            errorMessage = "Not a GIF image.";
            return false;
        }

        if (data.Length < 13)
        {
            errorCode = StbErrorCode.TruncatedInput;
            errorMessage = "Truncated GIF logical screen descriptor.";
            return false;
        }

        width = BinaryPrimitives.ReadUInt16LittleEndian(data.Slice(6, 2));
        height = BinaryPrimitives.ReadUInt16LittleEndian(data.Slice(8, 2));
        flags = data[10];
        bgIndex = data[11];

        if (width == 0 || height == 0)
        {
            errorCode = StbErrorCode.InvalidData;
            errorMessage = $"Invalid GIF dimensions {width}x{height}.";
            return false;
        }

        errorCode = StbErrorCode.Unknown;
        errorMessage = string.Empty;
        return true;
    }
}
