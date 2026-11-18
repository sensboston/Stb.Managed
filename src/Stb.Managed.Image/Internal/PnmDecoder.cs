using System;
using Stb.Managed.Internal;

namespace Stb.Managed.Image.Internal;

/// <summary>
/// Portable GrayMap (PGM: P2/P5) and Portable PixMap (PPM: P3/P6) decoder matching <c>stb_image.h</c>.
/// </summary>
internal static class PnmDecoder
{
    private readonly struct PnmHeader
    {
        public readonly int Width;
        public readonly int Height;
        public readonly int Components;
        public readonly int MaxValue;
        public readonly bool IsAscii;
        public readonly bool Is16Bit;
        public readonly int BodyOffset;

        public PnmHeader(int width, int height, int components, int maxValue, bool isAscii, int bodyOffset)
        {
            Width = width;
            Height = height;
            Components = components;
            MaxValue = maxValue;
            IsAscii = isAscii;
            Is16Bit = maxValue > 255;
            BodyOffset = bodyOffset;
        }
    }

    internal static bool IsPnm(ReadOnlySpan<byte> data)
    {
        return data.Length >= 2
            && data[0] == (byte)'P'
            && (data[1] == (byte)'2' || data[1] == (byte)'3' || data[1] == (byte)'5' || data[1] == (byte)'6');
    }

    internal static bool TryGetInfo(ReadOnlySpan<byte> data, StbLimits limits, out ImageInfo info)
    {
        if (!TryParseHeader(data, out PnmHeader header, out _, out _))
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

        info = new ImageInfo(
            header.Width,
            header.Height,
            (ColorComponents)header.Components,
            header.Is16Bit ? 16 : 8,
            IsHdr: false);
        return true;
    }

    internal static ImageInfo GetInfo(ReadOnlySpan<byte> data, StbLimits limits)
    {
        if (!TryParseHeader(data, out PnmHeader header, out StbErrorCode errCode, out string errMsg))
        {
            throw new StbException(errCode, errMsg);
        }

        _ = limits.ValidateDimensions(header.Width, header.Height, header.Components * (header.Is16Bit ? 2 : 1));
        return new ImageInfo(
            header.Width,
            header.Height,
            (ColorComponents)header.Components,
            header.Is16Bit ? 16 : 8,
            IsHdr: false);
    }

    internal static bool TryDecode8(
        ReadOnlySpan<byte> data,
        Span<byte> destination,
        ColorComponents desiredComponents,
        ImageDecodeOptions options,
        out ImageInfo info,
        out ColorComponents outputComponents)
    {
        PnmHeader header = ParseAndValidate(data, options.Options.Limits);
        info = new ImageInfo(
            header.Width,
            header.Height,
            (ColorComponents)header.Components,
            header.Is16Bit ? 16 : 8,
            IsHdr: false);

        int dstChannels = desiredComponents == ColorComponents.Default ? header.Components : (int)desiredComponents;
        outputComponents = (ColorComponents)dstChannels;

        int requiredBytes = options.Options.Limits.ValidateDimensions(header.Width, header.Height, dstChannels);
        if (destination.Length < requiredBytes)
        {
            return false;
        }

        Span<byte> outSlice = destination.Slice(0, requiredBytes);
        int pixelCount = header.Width * header.Height;

        if (header.Is16Bit)
        {
            int srcBytes = options.Options.Limits.ValidateDimensions(header.Width, header.Height, header.Components * sizeof(ushort));
            int srcSamples = srcBytes / sizeof(ushort);
            using PooledBuffer<ushort> raw16 = new(srcSamples);
            DecodeSamples16(data, header, raw16.Span);

            if (dstChannels == header.Components)
            {
                PixelConverter.Convert16To8(raw16.Span, outSlice);
            }
            else
            {
                int convBytes = options.Options.Limits.ValidateDimensions(header.Width, header.Height, dstChannels * sizeof(ushort));
                using PooledBuffer<ushort> conv16 = new(convBytes / sizeof(ushort));
                PixelConverter.ConvertFormat16(raw16.Span, conv16.Span, header.Components, dstChannels, pixelCount);
                PixelConverter.Convert16To8(conv16.Span, outSlice);
            }
        }
        else if (!header.IsAscii && header.MaxValue == 255)
        {
            int srcSamples = options.Options.Limits.ValidateDimensions(header.Width, header.Height, header.Components);
            if (data.Length - header.BodyOffset < srcSamples)
            {
                throw new StbException(StbErrorCode.TruncatedInput, "Unexpected end of file while reading binary PNM pixel data.");
            }

            ReadOnlySpan<byte> body = data.Slice(header.BodyOffset, srcSamples);
            if (!options.FlipVertically)
            {
                PixelConverter.ConvertFormat8(body, outSlice, header.Components, dstChannels, pixelCount);
            }
            else
            {
                int srcRowBytes = header.Width * header.Components;
                int dstRowBytes = header.Width * dstChannels;
                for (int y = 0; y < header.Height; y++)
                {
                    ReadOnlySpan<byte> srcRow = body.Slice(y * srcRowBytes, srcRowBytes);
                    Span<byte> dstRow = outSlice.Slice((header.Height - 1 - y) * dstRowBytes, dstRowBytes);
                    PixelConverter.ConvertFormat8(srcRow, dstRow, header.Components, dstChannels, header.Width);
                }
            }

            return true;
        }
        else if (dstChannels == header.Components)
        {
            DecodeSamples8(data, header, outSlice);
        }
        else
        {
            int srcSamples = options.Options.Limits.ValidateDimensions(header.Width, header.Height, header.Components);
            using PooledBuffer<byte> raw8 = new(srcSamples);
            DecodeSamples8(data, header, raw8.Span);
            PixelConverter.ConvertFormat8(raw8.Span, outSlice, header.Components, dstChannels, pixelCount);
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
        PnmHeader header = ParseAndValidate(data, options.Options.Limits);
        info = new ImageInfo(
            header.Width,
            header.Height,
            (ColorComponents)header.Components,
            header.Is16Bit ? 16 : 8,
            IsHdr: false);

        int dstChannels = desiredComponents == ColorComponents.Default ? header.Components : (int)desiredComponents;
        outputComponents = (ColorComponents)dstChannels;

        int requiredBytes = options.Options.Limits.ValidateDimensions(header.Width, header.Height, dstChannels * sizeof(ushort));
        int requiredSamples = requiredBytes / sizeof(ushort);
        if (destination.Length < requiredSamples)
        {
            return false;
        }

        Span<ushort> outSlice = destination.Slice(0, requiredSamples);
        int pixelCount = header.Width * header.Height;

        if (header.Is16Bit)
        {
            if (dstChannels == header.Components)
            {
                DecodeSamples16(data, header, outSlice);
            }
            else
            {
                int srcBytes = options.Options.Limits.ValidateDimensions(header.Width, header.Height, header.Components * sizeof(ushort));
                using PooledBuffer<ushort> raw16 = new(srcBytes / sizeof(ushort));
                DecodeSamples16(data, header, raw16.Span);
                PixelConverter.ConvertFormat16(raw16.Span, outSlice, header.Components, dstChannels, pixelCount);
            }
        }
        else
        {
            int srcSamples = options.Options.Limits.ValidateDimensions(header.Width, header.Height, header.Components);
            using PooledBuffer<byte> raw8 = new(srcSamples);
            DecodeSamples8(data, header, raw8.Span);
            if (dstChannels == header.Components)
            {
                PixelConverter.Convert8To16(raw8.Span, outSlice);
            }
            else
            {
                using PooledBuffer<byte> conv8 = new(requiredSamples);
                PixelConverter.ConvertFormat8(raw8.Span, conv8.Span, header.Components, dstChannels, pixelCount);
                PixelConverter.Convert8To16(conv8.Span, outSlice);
            }
        }

        if (options.FlipVertically)
        {
            PixelConverter.VerticalFlip(outSlice, header.Height, header.Width * dstChannels);
        }

        return true;
    }

    private static PnmHeader ParseAndValidate(ReadOnlySpan<byte> data, StbLimits limits)
    {
        if (!TryParseHeader(data, out PnmHeader header, out StbErrorCode errCode, out string errMsg))
        {
            throw new StbException(errCode, errMsg);
        }

        _ = limits.ValidateDimensions(header.Width, header.Height, header.Components);
        return header;
    }

    private static void DecodeSamples8(ReadOnlySpan<byte> data, PnmHeader header, Span<byte> dst)
    {
        int sampleCount = dst.Length;
        int maxv = header.MaxValue;
        int pos = header.BodyOffset;

        if (!header.IsAscii)
        {
            if (data.Length - pos < sampleCount)
            {
                throw new StbException(StbErrorCode.TruncatedInput, "Unexpected end of file while reading binary PNM pixel data.");
            }

            ReadOnlySpan<byte> body = data.Slice(pos, sampleCount);
            if (maxv == 255)
            {
                body.CopyTo(dst);
            }
            else
            {
                for (int i = 0; i < sampleCount; i++)
                {
                    int val = body[i];
                    if (val >= maxv)
                    {
                        val = maxv - 1;
                    }

                    dst[i] = (byte)((val * 255) / maxv);
                }
            }
        }
        else
        {
            for (int i = 0; i < sampleCount; i++)
            {
                if (!TryReadAsciiValue(data, ref pos, out int val))
                {
                    throw new StbException(StbErrorCode.TruncatedInput, "Unexpected end of file while reading ASCII PNM pixel data.");
                }

                if (maxv != 255)
                {
                    if (val >= maxv)
                    {
                        val = maxv - 1;
                    }

                    val = (val * 255) / maxv;
                }

                dst[i] = (byte)val;
            }
        }
    }

    private static void DecodeSamples16(ReadOnlySpan<byte> data, PnmHeader header, Span<ushort> dst)
    {
        int sampleCount = dst.Length;
        int maxv = header.MaxValue;
        int pos = header.BodyOffset;

        if (!header.IsAscii)
        {
            long byteCount = (long)sampleCount * 2;
            if (data.Length - pos < byteCount)
            {
                throw new StbException(StbErrorCode.TruncatedInput, "Unexpected end of file while reading 16-bit binary PNM pixel data.");
            }

            if (maxv == 65535)
            {
                PixelConverter.ReverseEndianness16FromBytes(data.Slice(pos, (int)byteCount), dst);
            }
            else
            {
                for (int i = 0; i < sampleCount; i++)
                {
                    int off = pos + (i * 2);
                    int val = (data[off] << 8) | data[off + 1];
                    if (val >= maxv)
                    {
                        val = maxv - 1;
                    }

                    dst[i] = (ushort)((val * 65535) / maxv);
                }
            }
        }
        else
        {
            for (int i = 0; i < sampleCount; i++)
            {
                if (!TryReadAsciiValue(data, ref pos, out int val))
                {
                    throw new StbException(StbErrorCode.TruncatedInput, "Unexpected end of file while reading 16-bit ASCII PNM pixel data.");
                }

                if (maxv != 65535)
                {
                    if (val >= maxv)
                    {
                        val = maxv - 1;
                    }

                    val = (val * 65535) / maxv;
                }

                dst[i] = (ushort)val;
            }
        }
    }

    private static bool TryParseHeader(
        ReadOnlySpan<byte> data,
        out PnmHeader header,
        out StbErrorCode errorCode,
        out string errorMessage)
    {
        header = default;
        if (!IsPnm(data))
        {
            errorCode = StbErrorCode.InvalidData;
            errorMessage = "Invalid PNM signature.";
            return false;
        }

        byte type = data[1];
        int components = (type == (byte)'2' || type == (byte)'5') ? 1 : 3;
        bool isAscii = type == (byte)'2' || type == (byte)'3';

        int pos = 2;
        if (!TryGetHeaderInteger(data, ref pos, out int width)
            || !TryGetHeaderInteger(data, ref pos, out int height)
            || !TryGetHeaderInteger(data, ref pos, out int maxv))
        {
            errorCode = StbErrorCode.InvalidData;
            errorMessage = "Malformed or truncated PNM header.";
            return false;
        }

        if (width <= 0 || height <= 0)
        {
            errorCode = StbErrorCode.InvalidData;
            errorMessage = $"Invalid PNM dimensions {width}x{height}.";
            return false;
        }

        if (maxv <= 0 || maxv > 65535)
        {
            errorCode = StbErrorCode.UnsupportedFeature;
            errorMessage = $"Unsupported PNM max value {maxv} (must be in range 1..65535).";
            return false;
        }

        errorCode = StbErrorCode.Unknown;
        errorMessage = string.Empty;
        header = new PnmHeader(width, height, components, maxv, isAscii, pos);
        return true;
    }

    /// <summary>
    /// Reads an unsigned integer from the PNM header, consuming whitespace/comments before it and
    /// exactly one trailing delimiter byte (matching <c>stbi__pnm_getinteger</c>).
    /// </summary>
    private static bool TryGetHeaderInteger(ReadOnlySpan<byte> data, ref int pos, out int value)
    {
        value = 0;
        if (pos >= data.Length)
        {
            return false;
        }

        byte c = data[pos++];
        if (!SkipWhitespaceAndComments(data, ref pos, ref c))
        {
            return false;
        }

        if (!IsDigit(c))
        {
            return false;
        }

        long acc = 0;
        while (IsDigit(c))
        {
            acc = (acc * 10) + (c - (byte)'0');
            if (acc > int.MaxValue)
            {
                return false;
            }

            if (pos >= data.Length)
            {
                value = (int)acc;
                return true;
            }

            c = data[pos++];
        }

        // Matches stb_image: the single delimiter byte after the integer has been consumed.
        // If that delimiter started a '#' comment right before the next header token, rewind 1 byte so
        // the next header read sees '#' and skips the comment line cleanly.
        if (c == (byte)'#')
        {
            pos--;
        }

        value = (int)acc;
        return true;
    }

    /// <summary>
    /// Reads an unsigned integer from an ASCII PNM body (P2/P3), matching <c>stbi__pnm_load</c>.
    /// </summary>
    private static bool TryReadAsciiValue(ReadOnlySpan<byte> data, ref int pos, out int value)
    {
        value = 0;
        if (pos >= data.Length)
        {
            return false;
        }

        byte c = data[pos++];
        if (!SkipWhitespaceAndComments(data, ref pos, ref c))
        {
            return false;
        }

        if (!IsDigit(c))
        {
            return false;
        }

        long acc = 0;
        while (IsDigit(c))
        {
            acc = (acc * 10) + (c - (byte)'0');
            if (acc > 65535)
            {
                acc = 65535;
            }

            if (pos >= data.Length)
            {
                value = (int)acc;
                return true;
            }

            c = data[pos++];
        }

        if (c == (byte)'#')
        {
            pos--;
        }

        value = (int)acc;
        return true;
    }

    private static bool SkipWhitespaceAndComments(ReadOnlySpan<byte> data, ref int pos, ref byte c)
    {
        while (true)
        {
            while (IsWhitespace(c))
            {
                if (pos >= data.Length)
                {
                    return false;
                }

                c = data[pos++];
            }

            if (c != (byte)'#')
            {
                return true;
            }

            while (c != (byte)'\n' && c != (byte)'\r')
            {
                if (pos >= data.Length)
                {
                    return false;
                }

                c = data[pos++];
            }
        }
    }

    private static bool IsWhitespace(byte c) =>
        c == (byte)' ' || c == (byte)'\t' || c == (byte)'\r' || c == (byte)'\n';

    private static bool IsDigit(byte c) =>
        c >= (byte)'0' && c <= (byte)'9';
}
