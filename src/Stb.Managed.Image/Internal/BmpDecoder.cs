using System;
using System.Buffers.Binary;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Stb.Managed.Internal;

namespace Stb.Managed.Image.Internal;

/// <summary>
/// Windows and OS/2 BMP decoder (headers 12/40/56/108/124, 1/4/8/16/24/32 bpp, BI_RGB and BI_BITFIELDS)
/// matching <c>stb_image.h</c>.
/// </summary>
internal static class BmpDecoder
{
    private static readonly uint[] ShiftSignedMulTable =
    {
        0,
        0xFF, 0x55, 0x49, 0x11,
        0x21, 0x41, 0x81, 0x01,
    };

    private static readonly int[] ShiftSignedShiftTable =
    {
        0, 0, 0, 1, 0, 2, 4, 6, 0,
    };

    private struct BmpHeader
    {
        public int Width;
        public int Height;
        public bool BottomUp;
        public int Bpp;
        public int Offset;
        public int Hsz;
        public int ExtraRead;
        public uint Mr;
        public uint Mg;
        public uint Mb;
        public uint Ma;
        public uint AllA;
        public int SourceComponents;
    }

    internal static bool IsBmp(ReadOnlySpan<byte> data)
    {
        if (data.Length < 18 || data[0] != (byte)'B' || data[1] != (byte)'M')
        {
            return false;
        }

        uint hsz = BinaryPrimitives.ReadUInt32LittleEndian(data.Slice(14, 4));
        return hsz is 12 or 40 or 56 or 108 or 124;
    }

    internal static bool TryGetInfo(ReadOnlySpan<byte> data, StbLimits limits, out ImageInfo info)
    {
        if (!TryParseHeader(data, out BmpHeader header, out _, out _))
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
        if (!TryParseHeader(data, out BmpHeader header, out StbErrorCode errCode, out string errMsg))
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
        if (!TryParseHeader(data, out BmpHeader header, out StbErrorCode errCode, out string errMsg))
        {
            throw new StbException(errCode, errMsg);
        }

        StbLimits limits = options.Options.Limits;
        info = new ImageInfo(header.Width, header.Height, (ColorComponents)header.SourceComponents, 8, IsHdr: false);

        int reqComp = (int)desiredComponents;
        int dstChannels = reqComp == 0 ? header.SourceComponents : reqComp;
        outputComponents = (ColorComponents)dstChannels;

        int requiredBytes = limits.ValidateDimensions(header.Width, header.Height, dstChannels);
        if (destination.Length < requiredBytes)
        {
            return false;
        }

        int target = (reqComp >= 3) ? reqComp : header.SourceComponents;
        int targetBytes = limits.ValidateDimensions(header.Width, header.Height, target);
        Span<byte> outSlice = destination.Slice(0, requiredBytes);

        if (target == dstChannels)
        {
            DecodeIntoTarget(data, header, target, outSlice, options.FlipVertically);
        }
        else
        {
            using PooledBuffer<byte> targetBuf = new(targetBytes);
            DecodeIntoTarget(data, header, target, targetBuf.Span, options.FlipVertically);
            PixelConverter.ConvertFormat8(targetBuf.Span, outSlice, target, dstChannels, header.Width * header.Height);
        }

        return true;
    }

    private static void DecodeIntoTarget(
        ReadOnlySpan<byte> data,
        BmpHeader header,
        int target,
        Span<byte> outBuf,
        bool flipOnLoad)
    {
        int bytesReadSoFar = header.Hsz + header.ExtraRead;
        if (header.Offset < bytesReadSoFar)
        {
            throw new StbException(StbErrorCode.InvalidData, "Corrupt BMP: pixel offset precedes end of header.");
        }

        int psize = 0;
        if (header.Hsz == 12)
        {
            if (header.Bpp < 24)
            {
                psize = (header.Offset - bytesReadSoFar) / 3;
            }
        }
        else
        {
            if (header.Bpp < 16)
            {
                psize = (header.Offset - bytesReadSoFar) >> 2;
            }
        }

        int pos = bytesReadSoFar;
        if (psize == 0)
        {
            const int HeaderLimit = 1024;
            const int ExtraDataLimit = 256 * 4;
            if (bytesReadSoFar <= 0 || bytesReadSoFar > HeaderLimit)
            {
                throw new StbException(StbErrorCode.InvalidData, "Corrupt BMP header size.");
            }

            if (header.Offset < bytesReadSoFar || header.Offset - bytesReadSoFar > ExtraDataLimit)
            {
                throw new StbException(StbErrorCode.InvalidData, "Corrupt BMP pixel data offset.");
            }

            pos = header.Offset;
            if (pos > data.Length)
            {
                throw new StbException(StbErrorCode.TruncatedInput, "Unexpected end of file before BMP pixel data.");
            }
        }

        uint mr = header.Mr;
        uint mg = header.Mg;
        uint mb = header.Mb;
        uint ma = header.Ma;
        uint allA = header.AllA;
        int imgX = header.Width;
        int imgY = header.Height;
        bool effectiveFlip = header.BottomUp ^ flipOnLoad;
        int rowStride = imgX * target;

        if (header.Bpp < 16)
        {
            if (psize == 0 || psize > 256)
            {
                throw new StbException(StbErrorCode.InvalidData, $"Corrupt BMP palette size {psize}.");
            }

            int palEntryBytes = header.Hsz == 12 ? 3 : 4;
            if (data.Length - pos < psize * palEntryBytes)
            {
                throw new StbException(StbErrorCode.TruncatedInput, "Unexpected end of file reading BMP palette.");
            }

            Span<byte> pal = stackalloc byte[256 * 4];
            pal.Clear();
            for (int i = 0; i < psize; i++)
            {
                int pIdx = i * 4;
                pal[pIdx + 2] = data[pos++];
                pal[pIdx + 1] = data[pos++];
                pal[pIdx] = data[pos++];
                if (header.Hsz != 12)
                {
                    pos++;
                }

                pal[pIdx + 3] = 255;
            }

            for (int i = psize; i < 256; i++)
            {
                pal[(i * 4) + 3] = 255;
            }

            pos = header.Offset;
            if (pos > data.Length)
            {
                throw new StbException(StbErrorCode.TruncatedInput, "Unexpected end of file before indexed BMP pixel data.");
            }

            int widthBytes = header.Bpp switch
            {
                1 => (imgX + 7) >> 3,
                4 => (imgX + 1) >> 1,
                8 => imgX,
                _ => throw new StbException(StbErrorCode.InvalidData, $"Unsupported indexed BMP bit depth {header.Bpp}."),
            };
            int pad = (-widthBytes) & 3;

            if (header.Bpp == 1)
            {
                for (int j = 0; j < imgY; j++)
                {
                    int z = (effectiveFlip ? (imgY - 1 - j) : j) * rowStride;
                    int bitOffset = 7;
                    byte v = ReadByteOrZero(data, ref pos);
                    for (int i = 0; i < imgX; i++)
                    {
                        int color = (v >> bitOffset) & 1;
                        int pIdx = color * 4;
                        outBuf[z++] = pal[pIdx];
                        outBuf[z++] = pal[pIdx + 1];
                        outBuf[z++] = pal[pIdx + 2];
                        if (target == 4)
                        {
                            outBuf[z++] = 255;
                        }

                        if (i + 1 == imgX)
                        {
                            break;
                        }

                        if (--bitOffset < 0)
                        {
                            bitOffset = 7;
                            v = ReadByteOrZero(data, ref pos);
                        }
                    }

                    pos = AdvanceClamp(pos, pad, data.Length);
                }
            }
            else if (header.Bpp == 8 && target == 4)
            {
                ReadOnlySpan<uint> pal32 = MemoryMarshal.Cast<byte, uint>(pal);
                for (int j = 0; j < imgY; j++)
                {
                    int dstOff = (effectiveFlip ? (imgY - 1 - j) : j) * rowStride;
                    Span<uint> row32 = MemoryMarshal.Cast<byte, uint>(outBuf.Slice(dstOff, rowStride));
                    if (data.Length - pos >= imgX)
                    {
                        ReadOnlySpan<byte> rowSrc = data.Slice(pos, imgX);
                        for (int i = 0; i < imgX; i++)
                        {
                            row32[i] = pal32[rowSrc[i]];
                        }

                        pos += imgX;
                    }
                    else
                    {
                        for (int i = 0; i < imgX; i++)
                        {
                            row32[i] = pal32[ReadByteOrZero(data, ref pos)];
                        }
                    }

                    pos = AdvanceClamp(pos, pad, data.Length);
                }
            }
            else
            {
                for (int j = 0; j < imgY; j++)
                {
                    int z = (effectiveFlip ? (imgY - 1 - j) : j) * rowStride;
                    for (int i = 0; i < imgX; i += 2)
                    {
                        int v = ReadByteOrZero(data, ref pos);
                        int v2 = 0;
                        if (header.Bpp == 4)
                        {
                            v2 = v & 15;
                            v >>= 4;
                        }

                        int pIdx = v * 4;
                        outBuf[z++] = pal[pIdx];
                        outBuf[z++] = pal[pIdx + 1];
                        outBuf[z++] = pal[pIdx + 2];
                        if (target == 4)
                        {
                            outBuf[z++] = 255;
                        }

                        if (i + 1 == imgX)
                        {
                            break;
                        }

                        v = header.Bpp == 8 ? ReadByteOrZero(data, ref pos) : v2;
                        pIdx = v * 4;
                        outBuf[z++] = pal[pIdx];
                        outBuf[z++] = pal[pIdx + 1];
                        outBuf[z++] = pal[pIdx + 2];
                        if (target == 4)
                        {
                            outBuf[z++] = 255;
                        }
                    }

                    pos = AdvanceClamp(pos, pad, data.Length);
                }
            }
        }
        else
        {
            int widthBytes = header.Bpp switch
            {
                24 => 3 * imgX,
                16 => 2 * imgX,
                32 => 0,
                _ => throw new StbException(StbErrorCode.InvalidData, $"Unsupported BMP bit depth {header.Bpp}."),
            };
            int pad = (-widthBytes) & 3;
            int easy = 0;
            byte alphaOverride = 0;

            if (header.Bpp == 24)
            {
                easy = 1;
            }
            else if (header.Bpp == 32)
            {
                if (header.Hsz < 56)
                {
                    ma = 0;
                }

                if (mb == 0xFFu && mg == 0xFF00u && mr == 0x00FF0000u && (ma == 0xFF000000u || ma == 0))
                {
                    easy = 2;
                    alphaOverride = ma != 0 ? (byte)0 : (byte)255;
                }
            }

            int rshift = 0, gshift = 0, bshift = 0, ashift = 0;
            int rcount = 0, gcount = 0, bcount = 0, acount = 0;
            if (easy == 0)
            {
                if (mr == 0 || mg == 0 || mb == 0)
                {
                    throw new StbException(StbErrorCode.InvalidData, "Corrupt BMP bitfield masks.");
                }

                rshift = HighBit(mr) - 7;
                rcount = BitCount(mr);
                gshift = HighBit(mg) - 7;
                gcount = BitCount(mg);
                bshift = HighBit(mb) - 7;
                bcount = BitCount(mb);
                ashift = HighBit(ma) - 7;
                acount = BitCount(ma);
                if (rcount > 8 || gcount > 8 || bcount > 8 || acount > 8)
                {
                    throw new StbException(StbErrorCode.InvalidData, "Corrupt BMP bitfield mask width (> 8 bits).");
                }
            }

            for (int j = 0; j < imgY; j++)
            {
                int dstRowOff = (effectiveFlip ? (imgY - 1 - j) : j) * rowStride;
                if (easy != 0)
                {
                    if (easy == 1 && data.Length - pos >= imgX * 3)
                    {
                        ReadOnlySpan<byte> srcRow = data.Slice(pos, imgX * 3);
                        Span<byte> dstRow = outBuf.Slice(dstRowOff, rowStride);
                        if (target == 3)
                        {
                            PixelConverter.Bgr24ToRgb24(srcRow, dstRow, imgX);
                        }
                        else
                        {
                            PixelConverter.Bgr24ToRgba32(srcRow, dstRow, imgX);
                        }

                        pos += imgX * 3;
                        allA |= 255;
                    }
                    else if (easy == 2 && data.Length - pos >= imgX * 4)
                    {
                        ReadOnlySpan<byte> srcRow = data.Slice(pos, imgX * 4);
                        Span<byte> dstRow = outBuf.Slice(dstRowOff, rowStride);
                        if (target == 4)
                        {
                            PixelConverter.Bgra32ToRgba32(srcRow, dstRow, imgX, alphaOverride, out byte rowAlphaOr);
                            allA |= rowAlphaOr;
                        }
                        else
                        {
                            PixelConverter.Bgra32ToRgb24(srcRow, dstRow, imgX);
                            allA |= 255;
                        }

                        pos += imgX * 4;
                    }
                    else
                    {
                        int z = dstRowOff;
                        for (int i = 0; i < imgX; i++)
                        {
                            outBuf[z + 2] = ReadByteOrZero(data, ref pos);
                            outBuf[z + 1] = ReadByteOrZero(data, ref pos);
                            outBuf[z] = ReadByteOrZero(data, ref pos);
                            z += 3;
                            byte a = easy == 2 ? (byte)(ReadByteOrZero(data, ref pos) | alphaOverride) : (byte)255;
                            allA |= a;
                            if (target == 4)
                            {
                                outBuf[z++] = a;
                            }
                        }
                    }
                }
                else
                {
                    int z = dstRowOff;
                    int bpp = header.Bpp;
                    for (int i = 0; i < imgX; i++)
                    {
                        uint v = bpp == 16 ? ReadUInt16LeOrZero(data, ref pos) : ReadUInt32LeOrZero(data, ref pos);
                        outBuf[z++] = (byte)ShiftSigned(v & mr, rshift, rcount);
                        outBuf[z++] = (byte)ShiftSigned(v & mg, gshift, gcount);
                        outBuf[z++] = (byte)ShiftSigned(v & mb, bshift, bcount);
                        uint a = ma != 0 ? (uint)ShiftSigned(v & ma, ashift, acount) : 255u;
                        allA |= a;
                        if (target == 4)
                        {
                            outBuf[z++] = (byte)a;
                        }
                    }
                }

                pos = AdvanceClamp(pos, pad, data.Length);
            }
        }

        if (target == 4 && allA == 0)
        {
            for (int i = (4 * imgX * imgY) - 1; i >= 0; i -= 4)
            {
                outBuf[i] = 255;
            }
        }
    }

    private static bool TryParseHeader(
        ReadOnlySpan<byte> data,
        out BmpHeader header,
        out StbErrorCode errorCode,
        out string errorMessage)
    {
        header = default;
        header.AllA = 255;

        if (data.Length < 18 || data[0] != (byte)'B' || data[1] != (byte)'M')
        {
            errorCode = StbErrorCode.InvalidData;
            errorMessage = "Not a BMP image.";
            return false;
        }

        int offset = BinaryPrimitives.ReadInt32LittleEndian(data.Slice(10, 4));
        int hsz = BinaryPrimitives.ReadInt32LittleEndian(data.Slice(14, 4));
        header.Offset = offset;
        header.Hsz = hsz;
        header.ExtraRead = 14;

        if (offset < 0)
        {
            errorCode = StbErrorCode.InvalidData;
            errorMessage = "Invalid negative BMP pixel offset.";
            return false;
        }

        if (hsz is not (12 or 40 or 56 or 108 or 124))
        {
            errorCode = StbErrorCode.UnsupportedFeature;
            errorMessage = $"Unsupported BMP header size {hsz}.";
            return false;
        }

        if (data.Length < 14 + hsz)
        {
            errorCode = StbErrorCode.TruncatedInput;
            errorMessage = "Truncated BMP DIB header.";
            return false;
        }

        int rawWidth;
        int rawHeight;
        int pos = 18;
        if (hsz == 12)
        {
            rawWidth = BinaryPrimitives.ReadUInt16LittleEndian(data.Slice(pos, 2));
            rawHeight = BinaryPrimitives.ReadUInt16LittleEndian(data.Slice(pos + 2, 2));
            pos += 4;
        }
        else
        {
            rawWidth = BinaryPrimitives.ReadInt32LittleEndian(data.Slice(pos, 4));
            rawHeight = BinaryPrimitives.ReadInt32LittleEndian(data.Slice(pos + 4, 4));
            pos += 8;
        }

        int planes = BinaryPrimitives.ReadUInt16LittleEndian(data.Slice(pos, 2));
        if (planes != 1)
        {
            errorCode = StbErrorCode.InvalidData;
            errorMessage = $"Invalid BMP plane count {planes} (expected 1).";
            return false;
        }

        header.Bpp = BinaryPrimitives.ReadUInt16LittleEndian(data.Slice(pos + 2, 2));
        pos += 4;

        if (rawWidth <= 0 || rawHeight == 0 || rawHeight == int.MinValue)
        {
            errorCode = StbErrorCode.InvalidData;
            errorMessage = $"Invalid BMP dimensions {rawWidth}x{rawHeight}.";
            return false;
        }

        header.Width = rawWidth;
        header.BottomUp = rawHeight > 0;
        header.Height = rawHeight > 0 ? rawHeight : -rawHeight;

        if (hsz != 12)
        {
            int compress = BinaryPrimitives.ReadInt32LittleEndian(data.Slice(pos, 4));
            pos += 4;
            if (compress is 1 or 2)
            {
                errorCode = StbErrorCode.UnsupportedFeature;
                errorMessage = "BMP RLE compression is not supported.";
                return false;
            }

            if (compress is < 0 or >= 4)
            {
                errorCode = StbErrorCode.UnsupportedFeature;
                errorMessage = $"Unsupported BMP compression mode {compress}.";
                return false;
            }

            if (compress == 3 && header.Bpp != 16 && header.Bpp != 32)
            {
                errorCode = StbErrorCode.InvalidData;
                errorMessage = "BMP BI_BITFIELDS requires 16 or 32 bits per pixel.";
                return false;
            }

            // Skip imageSize, hres, vres, colorsUsed, colorsImportant (5 * 4 = 20 bytes)
            pos += 20;

            if (hsz == 40)
            {
                if (header.Bpp == 16 || header.Bpp == 32)
                {
                    if (compress == 0)
                    {
                        SetMaskDefaults(ref header, compress);
                    }
                    else if (compress == 3)
                    {
                        if (data.Length - pos < 12)
                        {
                            errorCode = StbErrorCode.TruncatedInput;
                            errorMessage = "Truncated BMP BITFIELDS masks.";
                            return false;
                        }

                        header.Mr = BinaryPrimitives.ReadUInt32LittleEndian(data.Slice(pos, 4));
                        header.Mg = BinaryPrimitives.ReadUInt32LittleEndian(data.Slice(pos + 4, 4));
                        header.Mb = BinaryPrimitives.ReadUInt32LittleEndian(data.Slice(pos + 8, 4));
                        header.ExtraRead += 12;

                        if (header.Mr == header.Mg && header.Mg == header.Mb)
                        {
                            errorCode = StbErrorCode.InvalidData;
                            errorMessage = "Invalid identical BMP BITFIELDS masks.";
                            return false;
                        }
                    }
                    else
                    {
                        errorCode = StbErrorCode.InvalidData;
                        errorMessage = "Invalid BMP compression mode.";
                        return false;
                    }
                }
            }
            else
            {
                header.Mr = BinaryPrimitives.ReadUInt32LittleEndian(data.Slice(pos, 4));
                header.Mg = BinaryPrimitives.ReadUInt32LittleEndian(data.Slice(pos + 4, 4));
                header.Mb = BinaryPrimitives.ReadUInt32LittleEndian(data.Slice(pos + 8, 4));
                header.Ma = BinaryPrimitives.ReadUInt32LittleEndian(data.Slice(pos + 12, 4));

                if (compress != 3)
                {
                    SetMaskDefaults(ref header, compress);
                }
            }
        }

        header.SourceComponents = (header.Bpp == 24 && header.Ma == 0xFF000000u)
            ? 3
            : (header.Ma != 0 ? 4 : 3);

        errorCode = StbErrorCode.Unknown;
        errorMessage = string.Empty;
        return true;
    }

    private static void SetMaskDefaults(ref BmpHeader header, int compress)
    {
        if (compress == 3)
        {
            return;
        }

        if (compress == 0)
        {
            if (header.Bpp == 16)
            {
                header.Mr = 31u << 10;
                header.Mg = 31u << 5;
                header.Mb = 31u;
                header.Ma = 0;
            }
            else if (header.Bpp == 32)
            {
                header.Mr = 0xFFu << 16;
                header.Mg = 0xFFu << 8;
                header.Mb = 0xFFu;
                header.Ma = 0;
                header.AllA = 0;
            }
            else
            {
                header.Mr = 0;
                header.Mg = 0;
                header.Mb = 0;
                header.Ma = 0;
            }
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int HighBit(uint z)
    {
        int n = 0;
        if (z == 0)
        {
            return -1;
        }

        if (z >= 0x10000) { n += 16; z >>= 16; }
        if (z >= 0x00100) { n += 8; z >>= 8; }
        if (z >= 0x00010) { n += 4; z >>= 4; }
        if (z >= 0x00004) { n += 2; z >>= 2; }
        if (z >= 0x00002) { n += 1; }
        return n;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int BitCount(uint a)
    {
        a = (a & 0x55555555u) + ((a >> 1) & 0x55555555u);
        a = (a & 0x33333333u) + ((a >> 2) & 0x33333333u);
        a = (a + (a >> 4)) & 0x0F0F0F0Fu;
        a += a >> 8;
        a += a >> 16;
        return (int)(a & 0xFFu);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int ShiftSigned(uint v, int shift, int bits)
    {
        if (shift < 0)
        {
            v <<= -shift;
        }
        else
        {
            v >>= shift;
        }

        v >>= 8 - bits;
        return (int)((v * ShiftSignedMulTable[bits]) >> ShiftSignedShiftTable[bits]);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static byte ReadByteOrZero(ReadOnlySpan<byte> data, ref int pos)
    {
        if (pos < data.Length)
        {
            return data[pos++];
        }

        return 0;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static uint ReadUInt16LeOrZero(ReadOnlySpan<byte> data, ref int pos)
    {
        uint lo = ReadByteOrZero(data, ref pos);
        uint hi = ReadByteOrZero(data, ref pos);
        return lo | (hi << 8);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static uint ReadUInt32LeOrZero(ReadOnlySpan<byte> data, ref int pos)
    {
        uint b0 = ReadByteOrZero(data, ref pos);
        uint b1 = ReadByteOrZero(data, ref pos);
        uint b2 = ReadByteOrZero(data, ref pos);
        uint b3 = ReadByteOrZero(data, ref pos);
        return b0 | (b1 << 8) | (b2 << 16) | (b3 << 24);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int AdvanceClamp(int pos, int count, int length)
    {
        if (count <= 0)
        {
            return pos;
        }

        int next = pos + count;
        return next > length ? length : next;
    }
}
