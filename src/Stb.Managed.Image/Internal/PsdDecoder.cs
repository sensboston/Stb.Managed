using System;
using System.Buffers.Binary;
using Stb.Managed.Internal;

namespace Stb.Managed.Image.Internal;

/// <summary>
/// Adobe Photoshop PSD decoder (8-bit and 16-bit RGB, uncompressed and PackBits RLE) matching <c>stb_image.h</c>.
/// </summary>
internal static class PsdDecoder
{
    private const uint PsdSignature = 0x38425053u; // "8BPS"

    private static readonly float[] MatteRa8 = BuildMatteRa8();
    private static readonly float[] MatteInvA8 = BuildMatteInvA8();

    private static float[] BuildMatteRa8()
    {
        float[] lut = new float[256];
        for (int alpha = 1; alpha < 255; alpha++)
        {
            float a = alpha / 255.0f;
            lut[alpha] = 1.0f / a;
        }

        return lut;
    }

    private static float[] BuildMatteInvA8()
    {
        float[] lut = new float[256];
        for (int alpha = 1; alpha < 255; alpha++)
        {
            float a = alpha / 255.0f;
            float ra = 1.0f / a;
            lut[alpha] = 255.0f * (1.0f - ra);
        }

        return lut;
    }

    private struct PsdHeader
    {
        public int Width;
        public int Height;
        public int ChannelCount;
        public int BitDepth;
    }

    internal static bool IsPsd(ReadOnlySpan<byte> data)
    {
        return data.Length >= 4 && BinaryPrimitives.ReadUInt32BigEndian(data.Slice(0, 4)) == PsdSignature;
    }

    internal static bool TryGetInfo(ReadOnlySpan<byte> data, StbLimits limits, out ImageInfo info)
    {
        if (!TryParseHeader(data, out PsdHeader header, out _, out _))
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

        info = new ImageInfo(header.Width, header.Height, ColorComponents.RedGreenBlueAlpha, header.BitDepth, IsHdr: false);
        return true;
    }

    internal static ImageInfo GetInfo(ReadOnlySpan<byte> data, StbLimits limits)
    {
        if (!TryParseHeader(data, out PsdHeader header, out StbErrorCode errCode, out string errMsg))
        {
            throw new StbException(errCode, errMsg);
        }

        _ = limits.ValidateDimensions(header.Width, header.Height, 4 * (header.BitDepth == 16 ? 2 : 1));
        return new ImageInfo(header.Width, header.Height, ColorComponents.RedGreenBlueAlpha, header.BitDepth, IsHdr: false);
    }

    internal static bool TryDecode8(
        ReadOnlySpan<byte> data,
        Span<byte> destination,
        ColorComponents desiredComponents,
        ImageDecodeOptions options,
        out ImageInfo info,
        out ColorComponents outputComponents)
    {
        if (!TryParseHeader(data, out PsdHeader header, out StbErrorCode errCode, out string errMsg))
        {
            throw new StbException(errCode, errMsg);
        }

        StbLimits limits = options.Options.Limits;
        info = new ImageInfo(header.Width, header.Height, ColorComponents.RedGreenBlueAlpha, header.BitDepth, IsHdr: false);

        int dstChannels = desiredComponents == ColorComponents.Default ? 4 : (int)desiredComponents;
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
            DecodeRgba8(data, header, outSlice);
        }
        else
        {
            using PooledBuffer<byte> rgbaBuf = new(rgbaBytes);
            DecodeRgba8(data, header, rgbaBuf.Span);
            PixelConverter.ConvertFormat8(rgbaBuf.Span, outSlice, 4, dstChannels, header.Width * header.Height);
        }

        if (options.FlipVertically)
        {
            PixelConverter.VerticalFlip(outSlice, header.Height, header.Width * dstChannels);
        }

        return true;
    }

    internal static bool TryDecode16(
        ReadOnlySpan<byte> data,
        Span<ushort> destination,
        ColorComponents desiredComponents,
        ImageDecodeOptions options,
        out ImageInfo info,
        out ColorComponents outputComponents)
    {
        if (!TryParseHeader(data, out PsdHeader header, out StbErrorCode errCode, out string errMsg))
        {
            throw new StbException(errCode, errMsg);
        }

        StbLimits limits = options.Options.Limits;
        info = new ImageInfo(header.Width, header.Height, ColorComponents.RedGreenBlueAlpha, header.BitDepth, IsHdr: false);

        int dstChannels = desiredComponents == ColorComponents.Default ? 4 : (int)desiredComponents;
        outputComponents = (ColorComponents)dstChannels;

        int requiredBytes = limits.ValidateDimensions(header.Width, header.Height, dstChannels * sizeof(ushort));
        int requiredSamples = requiredBytes / sizeof(ushort);
        if (destination.Length < requiredSamples)
        {
            return false;
        }

        int pos = SkipSectionsAndReadCompression(data, out int compression);
        Span<ushort> outSlice = destination.Slice(0, requiredSamples);

        if (compression == 0 && header.BitDepth == 16)
        {
            int rgbaBytes16 = limits.ValidateDimensions(header.Width, header.Height, 4 * sizeof(ushort));
            int rgbaSamples16 = rgbaBytes16 / sizeof(ushort);
            if (dstChannels == 4)
            {
                DecodeRawRgba16(data, pos, header, outSlice);
            }
            else
            {
                using PooledBuffer<ushort> rgbaBuf16 = new(rgbaSamples16);
                DecodeRawRgba16(data, pos, header, rgbaBuf16.Span);
                PixelConverter.ConvertFormat16(rgbaBuf16.Span, outSlice, 4, dstChannels, header.Width * header.Height);
            }
        }
        else
        {
            int rgbaBytes8 = limits.ValidateDimensions(header.Width, header.Height, 4);
            using PooledBuffer<byte> rgbaBuf8 = new(rgbaBytes8);
            DecodeRgba8At(data, pos, compression, header, rgbaBuf8.Span);
            if (dstChannels == 4)
            {
                PixelConverter.Convert8To16(rgbaBuf8.Span, outSlice);
            }
            else
            {
                using PooledBuffer<byte> conv8 = new(requiredSamples);
                PixelConverter.ConvertFormat8(rgbaBuf8.Span, conv8.Span, 4, dstChannels, header.Width * header.Height);
                PixelConverter.Convert8To16(conv8.Span, outSlice);
            }
        }

        if (options.FlipVertically)
        {
            PixelConverter.VerticalFlip(outSlice, header.Height, header.Width * dstChannels);
        }

        return true;
    }

    private static void DecodeRgba8(ReadOnlySpan<byte> data, PsdHeader header, Span<byte> dstRgba)
    {
        int pos = SkipSectionsAndReadCompression(data, out int compression);
        DecodeRgba8At(data, pos, compression, header, dstRgba);
    }

    private static void DecodeRgba8At(
        ReadOnlySpan<byte> data,
        int pos,
        int compression,
        PsdHeader header,
        Span<byte> dstRgba)
    {
        int pixelCount = header.Width * header.Height;

        if (compression == 1)
        {
            long rowCountTableBytes = (long)header.Height * header.ChannelCount * 2;
            if (data.Length - pos < rowCountTableBytes)
            {
                throw new StbException(StbErrorCode.TruncatedInput, "Unexpected end of file in PSD RLE row-length table.");
            }

            pos += (int)rowCountTableBytes;

            if (header.ChannelCount >= 3)
            {
                int planeCount = header.ChannelCount >= 4 ? 4 : 3;
                using PooledBuffer<byte> planesBuf = new(planeCount * pixelCount);
                Span<byte> planes = planesBuf.Span;
                for (int channel = 0; channel < planeCount; channel++)
                {
                    DecodeRleChannelPlanar(data, ref pos, planes.Slice(channel * pixelCount, pixelCount), pixelCount);
                }

                ReadOnlySpan<byte> rPlane = planes.Slice(0, pixelCount);
                ReadOnlySpan<byte> gPlane = planes.Slice(pixelCount, pixelCount);
                ReadOnlySpan<byte> bPlane = planes.Slice(pixelCount * 2, pixelCount);
                ReadOnlySpan<byte> aPlane = planeCount == 4
                    ? planes.Slice(pixelCount * 3, pixelCount)
                    : ReadOnlySpan<byte>.Empty;
                PixelConverter.Interleave4PlanesToRgba32(rPlane, gPlane, bPlane, aPlane, dstRgba, pixelCount);
            }
            else
            {
                for (int channel = 0; channel < 4; channel++)
                {
                    if (channel >= header.ChannelCount)
                    {
                        byte fill = channel == 3 ? (byte)255 : (byte)0;
                        for (int i = 0; i < pixelCount; i++)
                        {
                            dstRgba[(i * 4) + channel] = fill;
                        }
                    }
                    else
                    {
                        DecodeRleChannel(data, ref pos, dstRgba, channel, pixelCount);
                    }
                }
            }
        }
        else if (header.BitDepth == 8 && header.ChannelCount >= 3)
        {
            int planeCount = header.ChannelCount >= 4 ? 4 : 3;
            long totalNeeded = (long)planeCount * pixelCount;
            if (data.Length - pos < totalNeeded)
            {
                throw new StbException(StbErrorCode.TruncatedInput, "Unexpected end of file reading 8-bit PSD channel.");
            }

            ReadOnlySpan<byte> rPlane = data.Slice(pos, pixelCount);
            ReadOnlySpan<byte> gPlane = data.Slice(pos + pixelCount, pixelCount);
            ReadOnlySpan<byte> bPlane = data.Slice(pos + (pixelCount * 2), pixelCount);
            ReadOnlySpan<byte> aPlane = planeCount == 4
                ? data.Slice(pos + (pixelCount * 3), pixelCount)
                : ReadOnlySpan<byte>.Empty;
            PixelConverter.Interleave4PlanesToRgba32(rPlane, gPlane, bPlane, aPlane, dstRgba, pixelCount);
        }
        else
        {
            for (int channel = 0; channel < 4; channel++)
            {
                if (channel >= header.ChannelCount)
                {
                    byte fill = channel == 3 ? (byte)255 : (byte)0;
                    for (int i = 0; i < pixelCount; i++)
                    {
                        dstRgba[(i * 4) + channel] = fill;
                    }
                }
                else if (header.BitDepth == 16)
                {
                    long bytesNeeded = (long)pixelCount * 2;
                    if (data.Length - pos < bytesNeeded)
                    {
                        throw new StbException(StbErrorCode.TruncatedInput, "Unexpected end of file reading 16-bit PSD channel.");
                    }

                    for (int i = 0; i < pixelCount; i++)
                    {
                        dstRgba[(i * 4) + channel] = data[pos];
                        pos += 2;
                    }
                }
                else
                {
                    if (data.Length - pos < pixelCount)
                    {
                        throw new StbException(StbErrorCode.TruncatedInput, "Unexpected end of file reading 8-bit PSD channel.");
                    }

                    for (int i = 0; i < pixelCount; i++)
                    {
                        dstRgba[(i * 4) + channel] = data[pos++];
                    }
                }
            }
        }

        if (header.ChannelCount >= 4)
        {
            ReadOnlySpan<float> raLut = MatteRa8;
            ReadOnlySpan<float> invALut = MatteInvA8;
            for (int i = 0; i < pixelCount; i++)
            {
                int idx = i * 4;
                byte alpha = dstRgba[idx + 3];
                if (alpha != 0 && alpha != 255)
                {
                    float ra = raLut[alpha];
                    float invA = invALut[alpha];
                    dstRgba[idx] = unchecked((byte)(int)((dstRgba[idx] * ra) + invA));
                    dstRgba[idx + 1] = unchecked((byte)(int)((dstRgba[idx + 1] * ra) + invA));
                    dstRgba[idx + 2] = unchecked((byte)(int)((dstRgba[idx + 2] * ra) + invA));
                }
            }
        }
    }

    private static void DecodeRawRgba16(
        ReadOnlySpan<byte> data,
        int pos,
        PsdHeader header,
        Span<ushort> dstRgba)
    {
        int pixelCount = header.Width * header.Height;

        for (int channel = 0; channel < 4; channel++)
        {
            if (channel >= header.ChannelCount)
            {
                ushort fill = channel == 3 ? (ushort)65535 : (ushort)0;
                for (int i = 0; i < pixelCount; i++)
                {
                    dstRgba[(i * 4) + channel] = fill;
                }
            }
            else
            {
                long bytesNeeded = (long)pixelCount * 2;
                if (data.Length - pos < bytesNeeded)
                {
                    throw new StbException(StbErrorCode.TruncatedInput, "Unexpected end of file reading 16-bit PSD channel.");
                }

                for (int i = 0; i < pixelCount; i++)
                {
                    dstRgba[(i * 4) + channel] = (ushort)((data[pos] << 8) | data[pos + 1]);
                    pos += 2;
                }
            }
        }

        if (header.ChannelCount >= 4)
        {
            for (int i = 0; i < pixelCount; i++)
            {
                int idx = i * 4;
                ushort alpha = dstRgba[idx + 3];
                if (alpha != 0 && alpha != 65535)
                {
                    float a = alpha / 65535.0f;
                    float ra = 1.0f / a;
                    float invA = 65535.0f * (1.0f - ra);
                    dstRgba[idx] = unchecked((ushort)(int)((dstRgba[idx] * ra) + invA));
                    dstRgba[idx + 1] = unchecked((ushort)(int)((dstRgba[idx + 1] * ra) + invA));
                    dstRgba[idx + 2] = unchecked((ushort)(int)((dstRgba[idx + 2] * ra) + invA));
                }
            }
        }
    }

    private static void DecodeRleChannelPlanar(
        ReadOnlySpan<byte> data,
        ref int pos,
        Span<byte> dstPlane,
        int pixelCount)
    {
        int count = 0;
        while (count < pixelCount)
        {
            int nleft = pixelCount - count;
            if (pos >= data.Length)
            {
                throw new StbException(StbErrorCode.TruncatedInput, "Unexpected end of file inside PSD RLE stream.");
            }

            int len = data[pos++];
            if (len == 128)
            {
                // PackBits no-op
            }
            else if (len < 128)
            {
                len++;
                if (len > nleft)
                {
                    throw new StbException(StbErrorCode.CorruptStream, "Corrupt PSD RLE literal run exceeds channel size.");
                }

                if (data.Length - pos < len)
                {
                    throw new StbException(StbErrorCode.TruncatedInput, "Unexpected end of file inside PSD RLE literal run.");
                }

                data.Slice(pos, len).CopyTo(dstPlane.Slice(count, len));
                pos += len;
                count += len;
            }
            else
            {
                len = 257 - len;
                if (len > nleft)
                {
                    throw new StbException(StbErrorCode.CorruptStream, "Corrupt PSD RLE repeat run exceeds channel size.");
                }

                if (pos >= data.Length)
                {
                    throw new StbException(StbErrorCode.TruncatedInput, "Unexpected end of file inside PSD RLE repeat run.");
                }

                byte val = data[pos++];
                dstPlane.Slice(count, len).Fill(val);
                count += len;
            }
        }
    }

    private static void DecodeRleChannel(
        ReadOnlySpan<byte> data,
        ref int pos,
        Span<byte> dstRgba,
        int channelOffset,
        int pixelCount)
    {
        int count = 0;
        int dstIdx = channelOffset;
        while (count < pixelCount)
        {
            int nleft = pixelCount - count;
            if (pos >= data.Length)
            {
                throw new StbException(StbErrorCode.TruncatedInput, "Unexpected end of file inside PSD RLE stream.");
            }

            int len = data[pos++];
            if (len == 128)
            {
                // PackBits no-op
            }
            else if (len < 128)
            {
                len++;
                if (len > nleft)
                {
                    throw new StbException(StbErrorCode.CorruptStream, "Corrupt PSD RLE literal run exceeds channel size.");
                }

                if (data.Length - pos < len)
                {
                    throw new StbException(StbErrorCode.TruncatedInput, "Unexpected end of file inside PSD RLE literal run.");
                }

                count += len;
                while (len > 0)
                {
                    dstRgba[dstIdx] = data[pos++];
                    dstIdx += 4;
                    len--;
                }
            }
            else
            {
                len = 257 - len;
                if (len > nleft)
                {
                    throw new StbException(StbErrorCode.CorruptStream, "Corrupt PSD RLE repeat run exceeds channel size.");
                }

                if (pos >= data.Length)
                {
                    throw new StbException(StbErrorCode.TruncatedInput, "Unexpected end of file inside PSD RLE repeat run.");
                }

                byte val = data[pos++];
                count += len;
                while (len > 0)
                {
                    dstRgba[dstIdx] = val;
                    dstIdx += 4;
                    len--;
                }
            }
        }
    }

    private static int SkipSectionsAndReadCompression(ReadOnlySpan<byte> data, out int compression)
    {
        int pos = 26;
        // Skip Color Mode Data, Image Resources, and Layer/Mask Info sections
        for (int section = 0; section < 3; section++)
        {
            if (data.Length - pos < 4)
            {
                throw new StbException(StbErrorCode.TruncatedInput, "Unexpected end of file in PSD section header.");
            }

            uint sectionLen = BinaryPrimitives.ReadUInt32BigEndian(data.Slice(pos, 4));
            pos += 4;
            if (sectionLen > (uint)(data.Length - pos))
            {
                throw new StbException(StbErrorCode.TruncatedInput, "PSD section length extends past end of file.");
            }

            pos += (int)sectionLen;
        }

        if (data.Length - pos < 2)
        {
            throw new StbException(StbErrorCode.TruncatedInput, "Unexpected end of file reading PSD compression mode.");
        }

        compression = BinaryPrimitives.ReadUInt16BigEndian(data.Slice(pos, 2));
        pos += 2;
        if (compression > 1)
        {
            throw new StbException(StbErrorCode.UnsupportedFeature, $"Unsupported PSD compression mode {compression}.");
        }

        return pos;
    }

    private static bool TryParseHeader(
        ReadOnlySpan<byte> data,
        out PsdHeader header,
        out StbErrorCode errorCode,
        out string errorMessage)
    {
        header = default;
        if (data.Length < 26)
        {
            errorCode = StbErrorCode.TruncatedInput;
            errorMessage = "Truncated PSD header.";
            return false;
        }

        if (BinaryPrimitives.ReadUInt32BigEndian(data.Slice(0, 4)) != PsdSignature)
        {
            errorCode = StbErrorCode.InvalidData;
            errorMessage = "Not a PSD image.";
            return false;
        }

        int version = BinaryPrimitives.ReadUInt16BigEndian(data.Slice(4, 2));
        if (version != 1)
        {
            errorCode = StbErrorCode.UnsupportedFeature;
            errorMessage = $"Unsupported PSD version {version}.";
            return false;
        }

        int channelCount = BinaryPrimitives.ReadUInt16BigEndian(data.Slice(12, 2));
        if (channelCount > 16)
        {
            errorCode = StbErrorCode.UnsupportedFeature;
            errorMessage = $"Unsupported PSD channel count {channelCount}.";
            return false;
        }

        int height = BinaryPrimitives.ReadInt32BigEndian(data.Slice(14, 4));
        int width = BinaryPrimitives.ReadInt32BigEndian(data.Slice(18, 4));
        if (width <= 0 || height <= 0)
        {
            errorCode = StbErrorCode.InvalidData;
            errorMessage = $"Invalid PSD dimensions {width}x{height}.";
            return false;
        }

        int bitDepth = BinaryPrimitives.ReadUInt16BigEndian(data.Slice(22, 2));
        if (bitDepth != 8 && bitDepth != 16)
        {
            errorCode = StbErrorCode.UnsupportedFeature;
            errorMessage = $"Unsupported PSD bit depth {bitDepth} (only 8 and 16 are supported).";
            return false;
        }

        int colorMode = BinaryPrimitives.ReadUInt16BigEndian(data.Slice(24, 2));
        if (colorMode != 3)
        {
            errorCode = StbErrorCode.UnsupportedFeature;
            errorMessage = $"Unsupported PSD color mode {colorMode} (only RGB mode 3 is supported).";
            return false;
        }

        header = new PsdHeader
        {
            Width = width,
            Height = height,
            ChannelCount = channelCount,
            BitDepth = bitDepth,
        };
        errorCode = StbErrorCode.Unknown;
        errorMessage = string.Empty;
        return true;
    }
}
