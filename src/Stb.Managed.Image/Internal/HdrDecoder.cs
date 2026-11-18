using System;
using System.Runtime.CompilerServices;
using Stb.Managed.Internal;

namespace Stb.Managed.Image.Internal;

/// <summary>
/// Radiance RGBE HDR (<c>.hdr</c>) decoder matching <c>stb_image.h</c>.
/// </summary>
internal static class HdrDecoder
{
    private const int MaxTokenLength = 1024;

    private static ReadOnlySpan<byte> RadianceSignature => "#?RADIANCE\n"u8;
    private static ReadOnlySpan<byte> RgbeSignature => "#?RGBE\n"u8;
    private static ReadOnlySpan<byte> FormatLine => "FORMAT=32-bit_rle_rgbe"u8;

    private static readonly float[] ExpLutRgb = BuildExpLut(divideByThree: false);
    private static readonly float[] ExpLutGray = BuildExpLut(divideByThree: true);

    private readonly struct HdrHeader
    {
        public readonly int Width;
        public readonly int Height;
        public readonly int BodyOffset;

        public HdrHeader(int width, int height, int bodyOffset)
        {
            Width = width;
            Height = height;
            BodyOffset = bodyOffset;
        }
    }

    private static float[] BuildExpLut(bool divideByThree)
    {
        float[] lut = new float[256];
        lut[0] = 0.0f;
        for (int i = 1; i < 256; i++)
        {
            float v = (float)Math.Pow(2.0, i - 136);
            if (divideByThree)
            {
                v /= 3.0f;
            }

            lut[i] = v;
        }

        return lut;
    }

    internal static bool IsHdr(ReadOnlySpan<byte> data)
    {
        return data.StartsWith(RadianceSignature) || data.StartsWith(RgbeSignature);
    }

    internal static bool TryGetInfo(ReadOnlySpan<byte> data, StbLimits limits, out ImageInfo info)
    {
        if (!TryParseHeader(data, out HdrHeader header, out _, out _))
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

        info = new ImageInfo(header.Width, header.Height, ColorComponents.RedGreenBlue, 32, IsHdr: true);
        return true;
    }

    internal static ImageInfo GetInfo(ReadOnlySpan<byte> data, StbLimits limits)
    {
        if (!TryParseHeader(data, out HdrHeader header, out StbErrorCode errCode, out string errMsg))
        {
            throw new StbException(errCode, errMsg);
        }

        _ = limits.ValidateDimensions(header.Width, header.Height, 3 * sizeof(float));
        return new ImageInfo(header.Width, header.Height, ColorComponents.RedGreenBlue, 32, IsHdr: true);
    }

    internal static bool TryDecodeFloat(
        ReadOnlySpan<byte> data,
        Span<float> destination,
        ColorComponents desiredComponents,
        ImageDecodeOptions options,
        out ImageInfo info,
        out ColorComponents outputComponents)
    {
        if (!TryParseHeader(data, out HdrHeader header, out StbErrorCode errCode, out string errMsg))
        {
            throw new StbException(errCode, errMsg);
        }

        info = new ImageInfo(header.Width, header.Height, ColorComponents.RedGreenBlue, 32, IsHdr: true);
        int dstChannels = desiredComponents == ColorComponents.Default ? 3 : (int)desiredComponents;
        outputComponents = (ColorComponents)dstChannels;

        int requiredBytes = options.Options.Limits.ValidateDimensions(header.Width, header.Height, dstChannels * sizeof(float));
        int requiredFloats = requiredBytes / sizeof(float);
        if (destination.Length < requiredFloats)
        {
            return false;
        }

        Span<float> outSlice = destination.Slice(0, requiredFloats);
        DecodeBody(data, header, dstChannels, options.FlipVertically, outSlice);
        return true;
    }

    internal static bool TryDecode8(
        ReadOnlySpan<byte> data,
        Span<byte> destination,
        ColorComponents desiredComponents,
        ImageDecodeOptions options,
        out ImageInfo info,
        out ColorComponents outputComponents)
    {
        if (!TryParseHeader(data, out HdrHeader header, out StbErrorCode errCode, out string errMsg))
        {
            throw new StbException(errCode, errMsg);
        }

        info = new ImageInfo(header.Width, header.Height, ColorComponents.RedGreenBlue, 32, IsHdr: true);
        int dstChannels = desiredComponents == ColorComponents.Default ? 3 : (int)desiredComponents;
        outputComponents = (ColorComponents)dstChannels;

        int requiredBytes = options.Options.Limits.ValidateDimensions(header.Width, header.Height, dstChannels);
        if (destination.Length < requiredBytes)
        {
            return false;
        }

        int floatBytes = options.Options.Limits.ValidateDimensions(header.Width, header.Height, dstChannels * sizeof(float));
        int floatCount = floatBytes / sizeof(float);
        using PooledBuffer<float> floatBuf = new(floatCount);
        DecodeBody(data, header, dstChannels, options.FlipVertically, floatBuf.Span);

        PixelConverter.HdrToLdr8(
            floatBuf.Span,
            destination.Slice(0, requiredBytes),
            header.Width * header.Height,
            dstChannels,
            options.HdrToLdrGamma,
            options.HdrToLdrScale);

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
        if (!TryParseHeader(data, out HdrHeader header, out StbErrorCode errCode, out string errMsg))
        {
            throw new StbException(errCode, errMsg);
        }

        info = new ImageInfo(header.Width, header.Height, ColorComponents.RedGreenBlue, 32, IsHdr: true);
        int dstChannels = desiredComponents == ColorComponents.Default ? 3 : (int)desiredComponents;
        outputComponents = (ColorComponents)dstChannels;

        int requiredBytes = options.Options.Limits.ValidateDimensions(header.Width, header.Height, dstChannels * sizeof(ushort));
        int requiredSamples = requiredBytes / sizeof(ushort);
        if (destination.Length < requiredSamples)
        {
            return false;
        }

        using PooledBuffer<byte> ldr8 = new(requiredSamples);
        TryDecode8(data, ldr8.Span, desiredComponents, options, out _, out _);
        PixelConverter.Convert8To16(ldr8.Span, destination.Slice(0, requiredSamples));
        return true;
    }

    private static void DecodeBody(
        ReadOnlySpan<byte> data,
        HdrHeader header,
        int reqComp,
        bool flipVertically,
        Span<float> dst)
    {
        int width = header.Width;
        int height = header.Height;
        int pos = header.BodyOffset;
        int rowFloats = width * reqComp;
        ReadOnlySpan<float> expLut = reqComp <= 2 ? ExpLutGray : ExpLutRgb;

        bool isRle = width >= 8 && width < 32768;
        bool useStackScanline = isRle && width <= 512;
        Span<byte> stackScanline = useStackScanline ? stackalloc byte[width * 4] : Span<byte>.Empty;
        using PooledBuffer<byte> pooledScanline = isRle && !useStackScanline ? new PooledBuffer<byte>(width * 4) : default;
        scoped Span<byte> scanline = useStackScanline ? stackScanline : (isRle ? pooledScanline.Span : Span<byte>.Empty);

        for (int j = 0; j < height; j++)
        {
            int dstRow = flipVertically ? (height - 1 - j) : j;
            Span<float> rowOut = dst.Slice(dstRow * rowFloats, rowFloats);
            if (!isRle)
            {
                DecodeUncompressedRow(data, ref pos, width, reqComp, expLut, rowOut);
                continue;
            }

            if (data.Length - pos < 4)
            {
                throw new StbException(StbErrorCode.TruncatedInput, "Unexpected end of file reading HDR scanline header.");
            }

            byte c0 = data[pos];
            byte c1 = data[pos + 1];
            byte c2 = data[pos + 2];
            byte c3 = data[pos + 3];
            pos += 4;

            if (c0 != 2 || c1 != 2 || (c2 & 0x80) != 0)
            {
                // Non-RLE scanline: permanently switch to uncompressed mode matching stb_image.h.
                isRle = false;
                ConvertPixel(rowOut.Slice(0, reqComp), c0, c1, c2, c3, reqComp, expLut);
                DecodeUncompressedRow(
                    data,
                    ref pos,
                    width - 1,
                    reqComp,
                    expLut,
                    rowOut.Slice(reqComp, (width - 1) * reqComp));
                continue;
            }

            int scanlineWidth = (c2 << 8) | c3;
            if (scanlineWidth != width)
            {
                throw new StbException(
                    StbErrorCode.CorruptStream,
                    $"HDR RLE scanline width {scanlineWidth} does not match header width {width}.");
            }

            for (int k = 0; k < 4; k++)
            {
                Span<byte> channelLine = scanline.Slice(k * width, width);
                int i = 0;
                while (i < width)
                {
                    if (pos >= data.Length)
                    {
                        throw new StbException(StbErrorCode.TruncatedInput, "Unexpected end of file inside HDR RLE scanline.");
                    }

                    byte countByte = data[pos++];
                    if (countByte > 128)
                    {
                        int count = countByte - 128;
                        if (count == 0 || count > width - i)
                        {
                            throw new StbException(StbErrorCode.CorruptStream, "Corrupt HDR RLE run length.");
                        }

                        if (pos >= data.Length)
                        {
                            throw new StbException(StbErrorCode.TruncatedInput, "Unexpected end of file inside HDR RLE run.");
                        }

                        byte value = data[pos++];
                        channelLine.Slice(i, count).Fill(value);
                        i += count;
                    }
                    else
                    {
                        int count = countByte;
                        if (count == 0 || count > width - i)
                        {
                            throw new StbException(StbErrorCode.CorruptStream, "Corrupt HDR RLE literal length.");
                        }

                        if (data.Length - pos < count)
                        {
                            throw new StbException(StbErrorCode.TruncatedInput, "Unexpected end of file inside HDR RLE literal.");
                        }

                        data.Slice(pos, count).CopyTo(channelLine.Slice(i, count));
                        pos += count;
                        i += count;
                    }
                }
            }

            ReadOnlySpan<byte> rLine = scanline.Slice(0, width);
            ReadOnlySpan<byte> gLine = scanline.Slice(width, width);
            ReadOnlySpan<byte> bLine = scanline.Slice(width * 2, width);
            ReadOnlySpan<byte> eLine = scanline.Slice(width * 3, width);

            ConvertPlanarRgbeRow(rLine, gLine, bLine, eLine, width, reqComp, expLut, rowOut);
        }
    }

    private static void ConvertPlanarRgbeRow(
        ReadOnlySpan<byte> rLine,
        ReadOnlySpan<byte> gLine,
        ReadOnlySpan<byte> bLine,
        ReadOnlySpan<byte> eLine,
        int width,
        int reqComp,
        ReadOnlySpan<float> expLut,
        Span<float> rowOut)
    {
        switch (reqComp)
        {
            case 3:
                for (int i = 0, d = 0; i < width; i++, d += 3)
                {
                    float scale = expLut[eLine[i]];
                    rowOut[d] = (rLine[i] + 0.5f) * scale;
                    rowOut[d + 1] = (gLine[i] + 0.5f) * scale;
                    rowOut[d + 2] = (bLine[i] + 0.5f) * scale;
                }
                break;

            case 4:
                for (int i = 0, d = 0; i < width; i++, d += 4)
                {
                    float scale = expLut[eLine[i]];
                    rowOut[d] = (rLine[i] + 0.5f) * scale;
                    rowOut[d + 1] = (gLine[i] + 0.5f) * scale;
                    rowOut[d + 2] = (bLine[i] + 0.5f) * scale;
                    rowOut[d + 3] = 1.0f;
                }
                break;

            case 1:
                for (int i = 0; i < width; i++)
                {
                    rowOut[i] = (rLine[i] + gLine[i] + bLine[i] + 1.5f) * expLut[eLine[i]];
                }
                break;

            case 2:
                for (int i = 0, d = 0; i < width; i++, d += 2)
                {
                    rowOut[d] = (rLine[i] + gLine[i] + bLine[i] + 1.5f) * expLut[eLine[i]];
                    rowOut[d + 1] = 1.0f;
                }
                break;
        }
    }

    private static void DecodeUncompressedRow(
        ReadOnlySpan<byte> data,
        ref int pos,
        int pixelCount,
        int reqComp,
        ReadOnlySpan<float> expLut,
        Span<float> rowOut)
    {
        if (pixelCount <= 0)
        {
            return;
        }

        long bytesNeeded = (long)pixelCount * 4;
        if (data.Length - pos < bytesNeeded)
        {
            throw new StbException(StbErrorCode.TruncatedInput, "Unexpected end of file reading uncompressed HDR pixels.");
        }

        ReadOnlySpan<byte> src = data.Slice(pos, (int)bytesNeeded);
        pos += (int)bytesNeeded;

        switch (reqComp)
        {
            case 3:
                for (int i = 0, s = 0, d = 0; i < pixelCount; i++, s += 4, d += 3)
                {
                    float scale = expLut[src[s + 3]];
                    rowOut[d] = (src[s] + 0.5f) * scale;
                    rowOut[d + 1] = (src[s + 1] + 0.5f) * scale;
                    rowOut[d + 2] = (src[s + 2] + 0.5f) * scale;
                }
                break;

            case 4:
                for (int i = 0, s = 0, d = 0; i < pixelCount; i++, s += 4, d += 4)
                {
                    float scale = expLut[src[s + 3]];
                    rowOut[d] = (src[s] + 0.5f) * scale;
                    rowOut[d + 1] = (src[s + 1] + 0.5f) * scale;
                    rowOut[d + 2] = (src[s + 2] + 0.5f) * scale;
                    rowOut[d + 3] = 1.0f;
                }
                break;

            case 1:
                for (int i = 0, s = 0; i < pixelCount; i++, s += 4)
                {
                    rowOut[i] = (src[s] + src[s + 1] + src[s + 2] + 1.5f) * expLut[src[s + 3]];
                }
                break;

            case 2:
                for (int i = 0, s = 0, d = 0; i < pixelCount; i++, s += 4, d += 2)
                {
                    rowOut[d] = (src[s] + src[s + 1] + src[s + 2] + 1.5f) * expLut[src[s + 3]];
                    rowOut[d + 1] = 1.0f;
                }
                break;
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void ConvertPixel(
        Span<float> dst,
        byte r,
        byte g,
        byte b,
        byte e,
        int reqComp,
        ReadOnlySpan<float> expLut)
    {
        float scale = expLut[e];
        switch (reqComp)
        {
            case 1:
                dst[0] = (r + g + b + 1.5f) * scale;
                break;
            case 2:
                dst[0] = (r + g + b + 1.5f) * scale;
                dst[1] = 1.0f;
                break;
            case 3:
                dst[0] = (r + 0.5f) * scale;
                dst[1] = (g + 0.5f) * scale;
                dst[2] = (b + 0.5f) * scale;
                break;
            case 4:
                dst[0] = (r + 0.5f) * scale;
                dst[1] = (g + 0.5f) * scale;
                dst[2] = (b + 0.5f) * scale;
                dst[3] = 1.0f;
                break;
        }
    }

    private static bool TryParseHeader(
        ReadOnlySpan<byte> data,
        out HdrHeader header,
        out StbErrorCode errorCode,
        out string errorMessage)
    {
        header = default;
        int pos;
        if (data.StartsWith(RadianceSignature))
        {
            pos = RadianceSignature.Length;
        }
        else if (data.StartsWith(RgbeSignature))
        {
            pos = RgbeSignature.Length;
        }
        else
        {
            errorCode = StbErrorCode.InvalidData;
            errorMessage = "Not a Radiance HDR image.";
            return false;
        }

        bool validFormat = false;
        Span<byte> tokenBuf = stackalloc byte[MaxTokenLength];
        while (true)
        {
            if (!TryReadToken(data, ref pos, tokenBuf, out int tokenLen))
            {
                errorCode = StbErrorCode.TruncatedInput;
                errorMessage = "Truncated HDR header.";
                return false;
            }

            if (tokenLen == 0)
            {
                break;
            }

            if (tokenBuf.Slice(0, tokenLen).SequenceEqual(FormatLine))
            {
                validFormat = true;
            }
        }

        if (!validFormat)
        {
            errorCode = StbErrorCode.UnsupportedFeature;
            errorMessage = "Unsupported HDR format (expected FORMAT=32-bit_rle_rgbe).";
            return false;
        }

        if (!TryReadToken(data, ref pos, tokenBuf, out int dimLen))
        {
            errorCode = StbErrorCode.TruncatedInput;
            errorMessage = "Truncated HDR resolution string.";
            return false;
        }

        ReadOnlySpan<byte> dimLine = tokenBuf.Slice(0, dimLen);
        if (dimLine.Length < 8 || dimLine[0] != (byte)'-' || dimLine[1] != (byte)'Y' || dimLine[2] != (byte)' ')
        {
            errorCode = StbErrorCode.UnsupportedFeature;
            errorMessage = "Unsupported HDR image orientation (expected -Y <height> +X <width>).";
            return false;
        }

        int cursor = 3;
        if (!TryParsePositiveInt(dimLine, ref cursor, out int height))
        {
            errorCode = StbErrorCode.InvalidData;
            errorMessage = "Invalid HDR height.";
            return false;
        }

        while (cursor < dimLine.Length && dimLine[cursor] == (byte)' ')
        {
            cursor++;
        }

        if (cursor + 3 > dimLine.Length
            || dimLine[cursor] != (byte)'+'
            || dimLine[cursor + 1] != (byte)'X'
            || dimLine[cursor + 2] != (byte)' ')
        {
            errorCode = StbErrorCode.UnsupportedFeature;
            errorMessage = "Unsupported HDR image orientation (expected +X <width>).";
            return false;
        }

        cursor += 3;
        if (!TryParsePositiveInt(dimLine, ref cursor, out int width))
        {
            errorCode = StbErrorCode.InvalidData;
            errorMessage = "Invalid HDR width.";
            return false;
        }

        errorCode = StbErrorCode.Unknown;
        errorMessage = string.Empty;
        header = new HdrHeader(width, height, pos);
        return true;
    }

    private static bool TryReadToken(ReadOnlySpan<byte> data, ref int pos, Span<byte> buffer, out int length)
    {
        length = 0;
        if (pos >= data.Length)
        {
            return false;
        }

        while (pos < data.Length)
        {
            byte c = data[pos++];
            if (c == (byte)'\n')
            {
                return true;
            }

            if (length < buffer.Length)
            {
                buffer[length++] = c;
            }
        }

        return false;
    }

    private static bool TryParsePositiveInt(ReadOnlySpan<byte> span, ref int cursor, out int value)
    {
        value = 0;
        while (cursor < span.Length && span[cursor] == (byte)' ')
        {
            cursor++;
        }

        if (cursor >= span.Length || span[cursor] < (byte)'0' || span[cursor] > (byte)'9')
        {
            return false;
        }

        long acc = 0;
        while (cursor < span.Length && span[cursor] >= (byte)'0' && span[cursor] <= (byte)'9')
        {
            acc = (acc * 10) + (span[cursor++] - (byte)'0');
            if (acc > int.MaxValue)
            {
                return false;
            }
        }

        if (acc <= 0)
        {
            return false;
        }

        value = (int)acc;
        return true;
    }
}
