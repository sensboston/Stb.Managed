using System;
using System.Buffers.Binary;
using Stb.Managed.Internal;

namespace Stb.Managed.Image.Internal;

/// <summary>
/// Softimage PIC (<c>.pic</c>) decoder (uncompressed, pure RLE, and mixed RLE RGB/RGBA channel packets).
/// </summary>
internal static class PicDecoder
{
    private const uint PicMagic = 0x5380F634u;
    private const uint PictTag = 0x50494354u; // "PICT"
    private const int HeaderSize = 104;
    private const int MaxPackets = 10;

    private struct PicPacket
    {
        public byte Type;
        public byte ChannelMask;
    }

    private struct PicHeader
    {
        public int Width;
        public int Height;
        public int SourceComponents;
        public int PacketCount;
        public int BodyOffset;
    }

    internal static bool IsPic(ReadOnlySpan<byte> data)
    {
        return data.Length >= 92
            && BinaryPrimitives.ReadUInt32BigEndian(data.Slice(0, 4)) == PicMagic
            && BinaryPrimitives.ReadUInt32BigEndian(data.Slice(88, 4)) == PictTag;
    }

    internal static bool TryGetInfo(ReadOnlySpan<byte> data, StbLimits limits, out ImageInfo info)
    {
        Span<PicPacket> packets = stackalloc PicPacket[MaxPackets];
        if (!TryParseHeader(data, packets, out PicHeader header, out _, out _))
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
        Span<PicPacket> packets = stackalloc PicPacket[MaxPackets];
        if (!TryParseHeader(data, packets, out PicHeader header, out StbErrorCode errCode, out string errMsg))
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
        Span<PicPacket> packets = stackalloc PicPacket[MaxPackets];
        if (!TryParseHeader(data, packets, out PicHeader header, out StbErrorCode errCode, out string errMsg))
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

        int rgbaBytes = limits.ValidateDimensions(header.Width, header.Height, 4);
        Span<byte> outSlice = destination.Slice(0, requiredBytes);

        if (dstChannels == 4)
        {
            DecodeRgba(data, header, packets.Slice(0, header.PacketCount), outSlice);
        }
        else
        {
            using PooledBuffer<byte> rgbaBuf = new(rgbaBytes);
            DecodeRgba(data, header, packets.Slice(0, header.PacketCount), rgbaBuf.Span);
            PixelConverter.ConvertFormat8(rgbaBuf.Span, outSlice, 4, dstChannels, header.Width * header.Height);
        }

        if (options.FlipVertically)
        {
            PixelConverter.VerticalFlip(outSlice, header.Height, header.Width * dstChannels);
        }

        return true;
    }

    private static void DecodeRgba(
        ReadOnlySpan<byte> data,
        PicHeader header,
        ReadOnlySpan<PicPacket> packets,
        Span<byte> dstRgba)
    {
        int width = header.Width;
        int height = header.Height;
        int pixelCount = width * height;

        // Default RGB = 0, Alpha = 255 matching stb_image PIC loader.
        dstRgba.Clear();
        for (int i = 0; i < pixelCount; i++)
        {
            dstRgba[(i * 4) + 3] = 255;
        }

        int pos = header.BodyOffset;
        Span<byte> value = stackalloc byte[4];

        for (int y = 0; y < height; y++)
        {
            int rowBase = y * width * 4;
            for (int p = 0; p < packets.Length; p++)
            {
                PicPacket pkt = packets[p];
                byte mask = pkt.ChannelMask;
                int dstOff = rowBase;
                int left = width;

                switch (pkt.Type)
                {
                    case 0: // Uncompressed
                        while (left > 0)
                        {
                            ReadChannelValue(data, ref pos, mask, dstRgba.Slice(dstOff, 4));
                            dstOff += 4;
                            left--;
                        }
                        break;

                    case 1: // Pure RLE
                        while (left > 0)
                        {
                            if (pos >= data.Length)
                            {
                                throw new StbException(StbErrorCode.TruncatedInput, "Unexpected end of file inside PIC pure RLE packet.");
                            }

                            int count = data[pos++];
                            if (count == 0)
                            {
                                count = 1;
                            }

                            if (count > left)
                            {
                                count = left;
                            }

                            ReadChannelValue(data, ref pos, mask, value);
                            for (int i = 0; i < count; i++)
                            {
                                WriteMaskedValue(mask, value, dstRgba.Slice(dstOff, 4));
                                dstOff += 4;
                            }

                            left -= count;
                        }
                        break;

                    case 2: // Mixed RLE
                        while (left > 0)
                        {
                            if (pos >= data.Length)
                            {
                                throw new StbException(StbErrorCode.TruncatedInput, "Unexpected end of file inside PIC mixed RLE packet.");
                            }

                            int count = data[pos++];
                            if (count >= 128)
                            {
                                if (count == 128)
                                {
                                    if (data.Length - pos < 2)
                                    {
                                        throw new StbException(StbErrorCode.TruncatedInput, "Truncated PIC extended RLE count.");
                                    }

                                    count = BinaryPrimitives.ReadUInt16BigEndian(data.Slice(pos, 2));
                                    pos += 2;
                                }
                                else
                                {
                                    count -= 127;
                                }

                                if (count <= 0 || count > left)
                                {
                                    throw new StbException(StbErrorCode.CorruptStream, "Corrupt PIC mixed RLE run length.");
                                }

                                ReadChannelValue(data, ref pos, mask, value);
                                for (int i = 0; i < count; i++)
                                {
                                    WriteMaskedValue(mask, value, dstRgba.Slice(dstOff, 4));
                                    dstOff += 4;
                                }
                            }
                            else
                            {
                                count++;
                                if (count > left)
                                {
                                    throw new StbException(StbErrorCode.CorruptStream, "Corrupt PIC mixed RLE literal length.");
                                }

                                for (int i = 0; i < count; i++)
                                {
                                    ReadChannelValue(data, ref pos, mask, dstRgba.Slice(dstOff, 4));
                                    dstOff += 4;
                                }
                            }

                            left -= count;
                        }
                        break;

                    default:
                        throw new StbException(StbErrorCode.UnsupportedFeature, $"Unsupported PIC packet compression type {pkt.Type}.");
                }
            }
        }
    }

    private static void ReadChannelValue(ReadOnlySpan<byte> data, ref int pos, byte mask, Span<byte> dstPixel)
    {
        int maskBit = 0x80;
        for (int c = 0; c < 4; c++, maskBit >>= 1)
        {
            if ((mask & maskBit) != 0)
            {
                if (pos >= data.Length)
                {
                    throw new StbException(StbErrorCode.TruncatedInput, "Unexpected end of file reading PIC channel sample.");
                }

                dstPixel[c] = data[pos++];
            }
        }
    }

    private static void WriteMaskedValue(byte mask, ReadOnlySpan<byte> srcPixel, Span<byte> dstPixel)
    {
        int maskBit = 0x80;
        for (int c = 0; c < 4; c++, maskBit >>= 1)
        {
            if ((mask & maskBit) != 0)
            {
                dstPixel[c] = srcPixel[c];
            }
        }
    }

    private static bool TryParseHeader(
        ReadOnlySpan<byte> data,
        Span<PicPacket> packets,
        out PicHeader header,
        out StbErrorCode errorCode,
        out string errorMessage)
    {
        header = default;
        if (!IsPic(data))
        {
            errorCode = StbErrorCode.InvalidData;
            errorMessage = "Not a Softimage PIC image.";
            return false;
        }

        if (data.Length < HeaderSize)
        {
            errorCode = StbErrorCode.TruncatedInput;
            errorMessage = "Truncated Softimage PIC header.";
            return false;
        }

        int width = BinaryPrimitives.ReadUInt16BigEndian(data.Slice(92, 2));
        int height = BinaryPrimitives.ReadUInt16BigEndian(data.Slice(94, 2));
        if (width <= 0 || height <= 0)
        {
            errorCode = StbErrorCode.InvalidData;
            errorMessage = $"Invalid PIC dimensions {width}x{height}.";
            return false;
        }

        int pos = HeaderSize;
        int count = 0;
        bool hasAlpha = false;
        byte chained;
        do
        {
            if (count >= MaxPackets)
            {
                errorCode = StbErrorCode.InvalidData;
                errorMessage = "Too many Softimage PIC channel packets.";
                return false;
            }

            if (data.Length - pos < 4)
            {
                errorCode = StbErrorCode.TruncatedInput;
                errorMessage = "Truncated Softimage PIC channel packet header.";
                return false;
            }

            chained = data[pos];
            byte size = data[pos + 1];
            byte type = data[pos + 2];
            byte channel = data[pos + 3];
            pos += 4;

            if (size != 8)
            {
                errorCode = StbErrorCode.UnsupportedFeature;
                errorMessage = $"Unsupported PIC channel bit depth {size} (expected 8).";
                return false;
            }

            if (type > 2)
            {
                errorCode = StbErrorCode.UnsupportedFeature;
                errorMessage = $"Unsupported PIC packet type {type}.";
                return false;
            }

            if ((channel & 0x10) != 0)
            {
                hasAlpha = true;
            }

            packets[count++] = new PicPacket
            {
                Type = type,
                ChannelMask = channel,
            };
        }
        while (chained != 0);

        header = new PicHeader
        {
            Width = width,
            Height = height,
            SourceComponents = hasAlpha ? 4 : 3,
            PacketCount = count,
            BodyOffset = pos,
        };
        errorCode = StbErrorCode.Unknown;
        errorMessage = string.Empty;
        return true;
    }
}
