using System;
using System.Buffers;
using System.IO;
using Stb.Managed.Image.Internal;
using Stb.Managed.Internal;

namespace Stb.Managed;

/// <summary>
/// Fully managed image decoder (port of <c>stb_image</c>) supporting PNG, JPEG, BMP, TGA, GIF
/// (including multi-frame animation), PSD, Radiance HDR, Softimage PIC, and PNM (PGM/PPM).
/// </summary>
public static class StbImage
{
    private const int InitialStreamProbeBytes = 4096;

    /// <summary>
    /// Probes the container format from the leading header bytes without decoding pixels.
    /// </summary>
    /// <param name="data">The encoded image bytes.</param>
    /// <returns>The detected <see cref="ImageFormat"/>, or <see cref="ImageFormat.Unknown"/> if unrecognized.</returns>
    public static ImageFormat DetectFormat(ReadOnlySpan<byte> data)
    {
        if (PngDecoder.IsPng(data))
        {
            return ImageFormat.Png;
        }

        if (BmpDecoder.IsBmp(data))
        {
            return ImageFormat.Bmp;
        }

        if (GifDecoder.IsGif(data))
        {
            return ImageFormat.Gif;
        }

        if (PsdDecoder.IsPsd(data))
        {
            return ImageFormat.Psd;
        }

        if (PicDecoder.IsPic(data))
        {
            return ImageFormat.Pic;
        }

        if (JpegDecoder.IsJpeg(data))
        {
            return ImageFormat.Jpeg;
        }

        if (PnmDecoder.IsPnm(data))
        {
            return ImageFormat.Pnm;
        }

        if (HdrDecoder.IsHdr(data))
        {
            return ImageFormat.Hdr;
        }

        if (TgaDecoder.IsTga(data))
        {
            return ImageFormat.Tga;
        }

        return ImageFormat.Unknown;
    }

    /// <summary>
    /// Probes the container format from a <see cref="Stream"/> without decoding pixels.
    /// If <paramref name="stream"/> is seekable, its original position is restored before returning.
    /// </summary>
    /// <param name="stream">The readable image stream.</param>
    /// <returns>The detected <see cref="ImageFormat"/>, or <see cref="ImageFormat.Unknown"/> if unrecognized.</returns>
    public static ImageFormat DetectFormat(Stream stream)
    {
        Guard.ThrowIfNull(stream, nameof(stream));

        if (TryGetMemoryStreamSpan(stream, out ReadOnlySpan<byte> msSpan, out _))
        {
            return DetectFormat(msSpan);
        }

        long startPos = stream.CanSeek ? stream.Position : 0;
        byte[] buffer = ArrayPool<byte>.Shared.Rent(InitialStreamProbeBytes);
        try
        {
            int read = ReadUpTo(stream, buffer, 0, InitialStreamProbeBytes);
            return DetectFormat(buffer.AsSpan(0, read));
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
            if (stream.CanSeek)
            {
                stream.Position = startPos;
            }
        }
    }

    /// <summary>
    /// Returns <see langword="true"/> if the encoded image natively stores 16 bits per channel
    /// (such as 16-bit PNG, 16-bit PSD, or 16-bit PNM), matching <c>stbi_is_16_bit_from_memory</c>.
    /// </summary>
    /// <param name="data">The encoded image bytes.</param>
    /// <param name="options">Optional decode options (safety limits).</param>
    /// <returns><see langword="true"/> for native 16-bit images; otherwise <see langword="false"/>.</returns>
    public static bool Is16Bit(ReadOnlySpan<byte> data, ImageDecodeOptions? options = null)
    {
        return TryGetInfo(data, out ImageInfo info, options) && info.BitsPerChannel == 16;
    }

    /// <summary>
    /// Returns <see langword="true"/> if the encoded stream natively stores 16 bits per channel.
    /// If <paramref name="stream"/> is seekable, its original position is restored before returning.
    /// </summary>
    /// <param name="stream">The readable image stream.</param>
    /// <param name="options">Optional decode options (safety limits).</param>
    /// <returns><see langword="true"/> for native 16-bit images; otherwise <see langword="false"/>.</returns>
    public static bool Is16Bit(Stream stream, ImageDecodeOptions? options = null)
    {
        return TryGetInfo(stream, out ImageInfo info, options) && info.BitsPerChannel == 16;
    }

    /// <summary>
    /// Returns <see langword="true"/> if the encoded image is a high-dynamic-range format (Radiance HDR),
    /// matching <c>stbi_is_hdr_from_memory</c>.
    /// </summary>
    /// <param name="data">The encoded image bytes.</param>
    /// <returns><see langword="true"/> if the data is an HDR image; otherwise <see langword="false"/>.</returns>
    public static bool IsHdr(ReadOnlySpan<byte> data)
    {
        return HdrDecoder.IsHdr(data);
    }

    /// <summary>
    /// Returns <see langword="true"/> if the encoded stream is a high-dynamic-range format (Radiance HDR).
    /// If <paramref name="stream"/> is seekable, its original position is restored before returning.
    /// </summary>
    /// <param name="stream">The readable image stream.</param>
    /// <returns><see langword="true"/> if the stream contains an HDR image; otherwise <see langword="false"/>.</returns>
    public static bool IsHdr(Stream stream)
    {
        return DetectFormat(stream) == ImageFormat.Hdr;
    }

    /// <summary>
    /// Reads image dimensions, channel count, bit depth, and HDR flag without decoding pixel data.
    /// </summary>
    /// <param name="data">The encoded image bytes.</param>
    /// <param name="options">Optional decode options (safety limits).</param>
    /// <returns>The decoded <see cref="ImageInfo"/>.</returns>
    /// <exception cref="StbException">Thrown if the format is unsupported or the header is malformed.</exception>
    public static ImageInfo GetInfo(ReadOnlySpan<byte> data, ImageDecodeOptions? options = null)
    {
        ImageDecodeOptions effective = options ?? ImageDecodeOptions.Default;
        StbLimits limits = effective.Options.Limits;

        return DetectFormat(data) switch
        {
            ImageFormat.Png => PngDecoder.GetInfo(data, effective),
            ImageFormat.Bmp => BmpDecoder.GetInfo(data, limits),
            ImageFormat.Gif => GifDecoder.GetInfo(data, limits),
            ImageFormat.Psd => PsdDecoder.GetInfo(data, limits),
            ImageFormat.Pic => PicDecoder.GetInfo(data, limits),
            ImageFormat.Jpeg => JpegDecoder.GetInfo(data, effective),
            ImageFormat.Pnm => PnmDecoder.GetInfo(data, limits),
            ImageFormat.Hdr => HdrDecoder.GetInfo(data, limits),
            ImageFormat.Tga => TgaDecoder.GetInfo(data, limits),
            _ => throw new StbException(StbErrorCode.UnsupportedFormat, "Unsupported or unrecognized image format."),
        };
    }

    /// <summary>
    /// Reads image dimensions, channel count, bit depth, and HDR flag from a <see cref="Stream"/> without decoding pixels.
    /// If <paramref name="stream"/> is seekable, its original position is restored before returning.
    /// </summary>
    /// <param name="stream">The readable image stream.</param>
    /// <param name="options">Optional decode options (safety limits).</param>
    /// <returns>The decoded <see cref="ImageInfo"/>.</returns>
    /// <exception cref="StbException">Thrown if the format is unsupported, malformed, or exceeds configured limits.</exception>
    public static ImageInfo GetInfo(Stream stream, ImageDecodeOptions? options = null)
    {
        Guard.ThrowIfNull(stream, nameof(stream));
        ImageDecodeOptions effective = options ?? ImageDecodeOptions.Default;

        if (TryGetMemoryStreamSpan(stream, out ReadOnlySpan<byte> msSpan, out _))
        {
            return GetInfo(msSpan, effective);
        }

        if (TryGetInfoFromStreamCore(stream, effective, out ImageInfo info, out byte[]? finalBuffer, out int finalLength))
        {
            return info;
        }

        try
        {
            ReadOnlySpan<byte> span = finalBuffer is null
                ? ReadOnlySpan<byte>.Empty
                : finalBuffer.AsSpan(0, finalLength);
            return GetInfo(span, effective);
        }
        finally
        {
            if (finalBuffer is not null)
            {
                ArrayPool<byte>.Shared.Return(finalBuffer);
            }
        }
    }

    /// <summary>
    /// Attempts to read image dimensions, channel count, bit depth, and HDR flag without decoding pixel data.
    /// </summary>
    /// <param name="data">The encoded image bytes.</param>
    /// <param name="info">When this method returns <see langword="true"/>, receives the parsed metadata.</param>
    /// <param name="options">Optional decode options (safety limits).</param>
    /// <returns><see langword="true"/> if the image header was parsed successfully; otherwise <see langword="false"/>.</returns>
    public static bool TryGetInfo(
        ReadOnlySpan<byte> data,
        out ImageInfo info,
        ImageDecodeOptions? options = null)
    {
        ImageDecodeOptions effective = options ?? ImageDecodeOptions.Default;
        StbLimits limits = effective.Options.Limits;

        switch (DetectFormat(data))
        {
            case ImageFormat.Png:
                return PngDecoder.TryGetInfo(data, effective, out info);
            case ImageFormat.Bmp:
                return BmpDecoder.TryGetInfo(data, limits, out info);
            case ImageFormat.Gif:
                return GifDecoder.TryGetInfo(data, limits, out info);
            case ImageFormat.Psd:
                return PsdDecoder.TryGetInfo(data, limits, out info);
            case ImageFormat.Pic:
                return PicDecoder.TryGetInfo(data, limits, out info);
            case ImageFormat.Jpeg:
                return JpegDecoder.TryGetInfo(data, effective, out info);
            case ImageFormat.Pnm:
                return PnmDecoder.TryGetInfo(data, limits, out info);
            case ImageFormat.Hdr:
                return HdrDecoder.TryGetInfo(data, limits, out info);
            case ImageFormat.Tga:
                return TgaDecoder.TryGetInfo(data, limits, out info);
            default:
                info = default;
                return false;
        }
    }

    /// <summary>
    /// Attempts to read image metadata from a <see cref="Stream"/> without decoding pixels.
    /// If <paramref name="stream"/> is seekable, its original position is restored before returning.
    /// </summary>
    /// <param name="stream">The readable image stream.</param>
    /// <param name="info">When this method returns <see langword="true"/>, receives the parsed metadata.</param>
    /// <param name="options">Optional decode options (safety limits).</param>
    /// <returns><see langword="true"/> if the image header was parsed successfully; otherwise <see langword="false"/>.</returns>
    public static bool TryGetInfo(
        Stream stream,
        out ImageInfo info,
        ImageDecodeOptions? options = null)
    {
        Guard.ThrowIfNull(stream, nameof(stream));
        ImageDecodeOptions effective = options ?? ImageDecodeOptions.Default;

        if (TryGetMemoryStreamSpan(stream, out ReadOnlySpan<byte> msSpan, out _))
        {
            return TryGetInfo(msSpan, out info, effective);
        }

        if (TryGetInfoFromStreamCore(stream, effective, out info, out byte[]? finalBuffer, out _))
        {
            return true;
        }

        if (finalBuffer is not null)
        {
            ArrayPool<byte>.Shared.Return(finalBuffer);
        }

        info = default;
        return false;
    }

    /// <summary>
    /// Decodes an image into an 8-bit-per-channel <see cref="ImageBuffer{T}"/>.
    /// </summary>
    /// <param name="data">The encoded image bytes.</param>
    /// <param name="desiredComponents">
    /// The desired output channel count, or <see cref="ColorComponents.Default"/> to preserve the source image's channel count.
    /// </param>
    /// <param name="options">Optional decode options (limits, vertical flip, tone mapping, iPhone PNG conversion, checksums).</param>
    /// <returns>The decoded 8-bit pixel buffer.</returns>
    /// <exception cref="StbException">Thrown if the input is unsupported, malformed, or exceeds configured limits.</exception>
    public static ImageBuffer<byte> Load(
        ReadOnlySpan<byte> data,
        ColorComponents desiredComponents = ColorComponents.Default,
        ImageDecodeOptions? options = null)
    {
        ValidateDesiredComponents(desiredComponents);
        ImageDecodeOptions effective = options ?? ImageDecodeOptions.Default;

        ImageFormat format = DetectFormat(data);
        if (format == ImageFormat.Png)
        {
            return PngDecoder.Decode8(data, desiredComponents, effective);
        }

        if (format == ImageFormat.Jpeg)
        {
            return JpegDecoder.Decode8(data, desiredComponents, effective);
        }

        if (format == ImageFormat.Unknown)
        {
            throw new StbException(StbErrorCode.UnsupportedFormat, "Unsupported or unrecognized image format.");
        }

        ImageInfo info = GetInfoForFormat(format, data, effective);
        int dstChannels = desiredComponents == ColorComponents.Default ? (int)info.Components : (int)desiredComponents;
        int totalBytes = effective.Options.Limits.ValidateDimensions(info.Width, info.Height, dstChannels);
        byte[] pixels = new byte[totalBytes];

        bool ok = TryDecode8ForFormat(format, data, pixels, desiredComponents, effective, out ImageInfo decodedInfo, out ColorComponents outComp);
        if (!ok)
        {
            throw new StbException(StbErrorCode.InternalError, "Allocated buffer was unexpectedly too small for decoded image.");
        }

        return new ImageBuffer<byte>(decodedInfo.Width, decodedInfo.Height, outComp, decodedInfo.Components, pixels);
    }

    /// <summary>
    /// Reads and decodes an image from a <see cref="Stream"/> into an 8-bit-per-channel <see cref="ImageBuffer{T}"/>.
    /// </summary>
    /// <param name="stream">The readable image stream.</param>
    /// <param name="desiredComponents">
    /// The desired output channel count, or <see cref="ColorComponents.Default"/> to preserve the source image's channel count.
    /// </param>
    /// <param name="options">Optional decode options.</param>
    /// <returns>The decoded 8-bit pixel buffer.</returns>
    public static ImageBuffer<byte> Load(
        Stream stream,
        ColorComponents desiredComponents = ColorComponents.Default,
        ImageDecodeOptions? options = null)
    {
        Guard.ThrowIfNull(stream, nameof(stream));
        ValidateDesiredComponents(desiredComponents);
        ImageDecodeOptions effective = options ?? ImageDecodeOptions.Default;

        if (TryGetMemoryStreamSpan(stream, out ReadOnlySpan<byte> msSpan, out MemoryStream? ms))
        {
            ImageBuffer<byte> result = Load(msSpan, desiredComponents, effective);
            ms!.Position = ms.Length;
            return result;
        }

        byte[] rented = ReadEntireStream(stream, effective.Options.Limits, out int length);
        try
        {
            return Load(rented.AsSpan(0, length), desiredComponents, effective);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(rented);
        }
    }

    /// <summary>
    /// Decodes an 8-bit-per-channel image directly into a caller-supplied <paramref name="destination"/> span.
    /// </summary>
    /// <param name="data">The encoded image bytes.</param>
    /// <param name="destination">The caller-supplied output buffer.</param>
    /// <param name="info">Receives the source image metadata.</param>
    /// <param name="outputComponents">Receives the output channel count written to <paramref name="destination"/>.</param>
    /// <param name="desiredComponents">
    /// The desired output channel count, or <see cref="ColorComponents.Default"/> to preserve the source image's channel count.
    /// </param>
    /// <param name="options">Optional decode options.</param>
    /// <returns>
    /// <see langword="true"/> if <paramref name="destination"/> was large enough and decoding succeeded;
    /// <see langword="false"/> if <paramref name="destination"/> was too small (in which case <paramref name="info"/>
    /// and <paramref name="outputComponents"/> indicate the required capacity).
    /// </returns>
    /// <exception cref="StbException">Thrown if the input is unsupported, malformed, or exceeds configured limits.</exception>
    public static bool TryLoad(
        ReadOnlySpan<byte> data,
        Span<byte> destination,
        out ImageInfo info,
        out ColorComponents outputComponents,
        ColorComponents desiredComponents = ColorComponents.Default,
        ImageDecodeOptions? options = null)
    {
        ValidateDesiredComponents(desiredComponents);
        ImageDecodeOptions effective = options ?? ImageDecodeOptions.Default;
        ImageFormat format = DetectFormat(data);
        return TryDecode8ForFormat(format, data, destination, desiredComponents, effective, out info, out outputComponents);
    }

    /// <summary>
    /// Decodes an image into a 16-bit-per-channel <see cref="ImageBuffer{T}"/>.
    /// </summary>
    /// <param name="data">The encoded image bytes.</param>
    /// <param name="desiredComponents">
    /// The desired output channel count, or <see cref="ColorComponents.Default"/> to preserve the source image's channel count.
    /// </param>
    /// <param name="options">Optional decode options.</param>
    /// <returns>The decoded 16-bit pixel buffer.</returns>
    /// <exception cref="StbException">Thrown if the input is unsupported, malformed, or exceeds configured limits.</exception>
    public static ImageBuffer<ushort> Load16(
        ReadOnlySpan<byte> data,
        ColorComponents desiredComponents = ColorComponents.Default,
        ImageDecodeOptions? options = null)
    {
        ValidateDesiredComponents(desiredComponents);
        ImageDecodeOptions effective = options ?? ImageDecodeOptions.Default;

        ImageFormat format = DetectFormat(data);
        if (format == ImageFormat.Png)
        {
            return PngDecoder.Decode16(data, desiredComponents, effective);
        }

        if (format == ImageFormat.Jpeg)
        {
            return JpegDecoder.Decode16(data, desiredComponents, effective);
        }

        if (format == ImageFormat.Unknown)
        {
            throw new StbException(StbErrorCode.UnsupportedFormat, "Unsupported or unrecognized image format.");
        }

        ImageInfo info = GetInfoForFormat(format, data, effective);
        int dstChannels = desiredComponents == ColorComponents.Default ? (int)info.Components : (int)desiredComponents;
        int totalBytes = effective.Options.Limits.ValidateDimensions(info.Width, info.Height, dstChannels * sizeof(ushort));
        ushort[] pixels = new ushort[totalBytes / sizeof(ushort)];

        bool ok = TryDecode16ForFormat(format, data, pixels, desiredComponents, effective, out ImageInfo decodedInfo, out ColorComponents outComp);
        if (!ok)
        {
            throw new StbException(StbErrorCode.InternalError, "Allocated 16-bit buffer was unexpectedly too small for decoded image.");
        }

        return new ImageBuffer<ushort>(decodedInfo.Width, decodedInfo.Height, outComp, decodedInfo.Components, pixels);
    }

    /// <summary>
    /// Reads and decodes an image from a <see cref="Stream"/> into a 16-bit-per-channel <see cref="ImageBuffer{T}"/>.
    /// </summary>
    /// <param name="stream">The readable image stream.</param>
    /// <param name="desiredComponents">
    /// The desired output channel count, or <see cref="ColorComponents.Default"/> to preserve the source image's channel count.
    /// </param>
    /// <param name="options">Optional decode options.</param>
    /// <returns>The decoded 16-bit pixel buffer.</returns>
    public static ImageBuffer<ushort> Load16(
        Stream stream,
        ColorComponents desiredComponents = ColorComponents.Default,
        ImageDecodeOptions? options = null)
    {
        Guard.ThrowIfNull(stream, nameof(stream));
        ValidateDesiredComponents(desiredComponents);
        ImageDecodeOptions effective = options ?? ImageDecodeOptions.Default;

        if (TryGetMemoryStreamSpan(stream, out ReadOnlySpan<byte> msSpan, out MemoryStream? ms))
        {
            ImageBuffer<ushort> result = Load16(msSpan, desiredComponents, effective);
            ms!.Position = ms.Length;
            return result;
        }

        byte[] rented = ReadEntireStream(stream, effective.Options.Limits, out int length);
        try
        {
            return Load16(rented.AsSpan(0, length), desiredComponents, effective);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(rented);
        }
    }

    /// <summary>
    /// Decodes a 16-bit-per-channel image directly into a caller-supplied <paramref name="destination"/> span.
    /// </summary>
    /// <param name="data">The encoded image bytes.</param>
    /// <param name="destination">The caller-supplied output buffer.</param>
    /// <param name="info">Receives the source image metadata.</param>
    /// <param name="outputComponents">Receives the output channel count written to <paramref name="destination"/>.</param>
    /// <param name="desiredComponents">
    /// The desired output channel count, or <see cref="ColorComponents.Default"/> to preserve the source image's channel count.
    /// </param>
    /// <param name="options">Optional decode options.</param>
    /// <returns>
    /// <see langword="true"/> if <paramref name="destination"/> was large enough and decoding succeeded;
    /// <see langword="false"/> if <paramref name="destination"/> was too small.
    /// </returns>
    /// <exception cref="StbException">Thrown if the input is unsupported, malformed, or exceeds configured limits.</exception>
    public static bool TryLoad16(
        ReadOnlySpan<byte> data,
        Span<ushort> destination,
        out ImageInfo info,
        out ColorComponents outputComponents,
        ColorComponents desiredComponents = ColorComponents.Default,
        ImageDecodeOptions? options = null)
    {
        ValidateDesiredComponents(desiredComponents);
        ImageDecodeOptions effective = options ?? ImageDecodeOptions.Default;
        ImageFormat format = DetectFormat(data);
        return TryDecode16ForFormat(format, data, destination, desiredComponents, effective, out info, out outputComponents);
    }

    /// <summary>
    /// Decodes an image into a 32-bit floating-point <see cref="ImageBuffer{T}"/>, matching <c>stbi_loadf_from_memory</c>.
    /// Radiance HDR images are decoded natively; LDR formats are decoded as 8-bit and converted using
    /// <see cref="ImageDecodeOptions.LdrToHdrGamma"/> and <see cref="ImageDecodeOptions.LdrToHdrScale"/>.
    /// </summary>
    /// <param name="data">The encoded image bytes.</param>
    /// <param name="desiredComponents">
    /// The desired output channel count, or <see cref="ColorComponents.Default"/> to preserve the source image's channel count.
    /// </param>
    /// <param name="options">Optional decode options.</param>
    /// <returns>The decoded floating-point pixel buffer.</returns>
    /// <exception cref="StbException">Thrown if the input is unsupported, malformed, or exceeds configured limits.</exception>
    public static ImageBuffer<float> LoadFloat(
        ReadOnlySpan<byte> data,
        ColorComponents desiredComponents = ColorComponents.Default,
        ImageDecodeOptions? options = null)
    {
        ValidateDesiredComponents(desiredComponents);
        ImageDecodeOptions effective = options ?? ImageDecodeOptions.Default;

        ImageFormat format = DetectFormat(data);
        if (format == ImageFormat.Unknown)
        {
            throw new StbException(StbErrorCode.UnsupportedFormat, "Unsupported or unrecognized image format.");
        }

        ImageInfo info = GetInfoForFormat(format, data, effective);
        int dstChannels = desiredComponents == ColorComponents.Default ? (int)info.Components : (int)desiredComponents;
        int totalBytes = effective.Options.Limits.ValidateDimensions(info.Width, info.Height, dstChannels * sizeof(float));
        float[] pixels = new float[totalBytes / sizeof(float)];

        bool ok = TryDecodeFloatForFormat(format, data, pixels, desiredComponents, effective, out ImageInfo decodedInfo, out ColorComponents outComp);
        if (!ok)
        {
            throw new StbException(StbErrorCode.InternalError, "Allocated float buffer was unexpectedly too small for decoded image.");
        }

        return new ImageBuffer<float>(decodedInfo.Width, decodedInfo.Height, outComp, decodedInfo.Components, pixels);
    }

    /// <summary>
    /// Reads and decodes an image from a <see cref="Stream"/> into a 32-bit floating-point <see cref="ImageBuffer{T}"/>.
    /// </summary>
    /// <param name="stream">The readable image stream.</param>
    /// <param name="desiredComponents">
    /// The desired output channel count, or <see cref="ColorComponents.Default"/> to preserve the source image's channel count.
    /// </param>
    /// <param name="options">Optional decode options.</param>
    /// <returns>The decoded floating-point pixel buffer.</returns>
    public static ImageBuffer<float> LoadFloat(
        Stream stream,
        ColorComponents desiredComponents = ColorComponents.Default,
        ImageDecodeOptions? options = null)
    {
        Guard.ThrowIfNull(stream, nameof(stream));
        ValidateDesiredComponents(desiredComponents);
        ImageDecodeOptions effective = options ?? ImageDecodeOptions.Default;

        if (TryGetMemoryStreamSpan(stream, out ReadOnlySpan<byte> msSpan, out MemoryStream? ms))
        {
            ImageBuffer<float> result = LoadFloat(msSpan, desiredComponents, effective);
            ms!.Position = ms.Length;
            return result;
        }

        byte[] rented = ReadEntireStream(stream, effective.Options.Limits, out int length);
        try
        {
            return LoadFloat(rented.AsSpan(0, length), desiredComponents, effective);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(rented);
        }
    }

    /// <summary>
    /// Decodes a floating-point image directly into a caller-supplied <paramref name="destination"/> span.
    /// </summary>
    /// <param name="data">The encoded image bytes.</param>
    /// <param name="destination">The caller-supplied output buffer.</param>
    /// <param name="info">Receives the source image metadata.</param>
    /// <param name="outputComponents">Receives the output channel count written to <paramref name="destination"/>.</param>
    /// <param name="desiredComponents">
    /// The desired output channel count, or <see cref="ColorComponents.Default"/> to preserve the source image's channel count.
    /// </param>
    /// <param name="options">Optional decode options.</param>
    /// <returns>
    /// <see langword="true"/> if <paramref name="destination"/> was large enough and decoding succeeded;
    /// <see langword="false"/> if <paramref name="destination"/> was too small.
    /// </returns>
    /// <exception cref="StbException">Thrown if the input is unsupported, malformed, or exceeds configured limits.</exception>
    public static bool TryLoadFloat(
        ReadOnlySpan<byte> data,
        Span<float> destination,
        out ImageInfo info,
        out ColorComponents outputComponents,
        ColorComponents desiredComponents = ColorComponents.Default,
        ImageDecodeOptions? options = null)
    {
        ValidateDesiredComponents(desiredComponents);
        ImageDecodeOptions effective = options ?? ImageDecodeOptions.Default;
        ImageFormat format = DetectFormat(data);
        return TryDecodeFloatForFormat(format, data, destination, desiredComponents, effective, out info, out outputComponents);
    }

    /// <summary>
    /// Decodes all frames and per-frame delays of a GIF image (matching <c>stbi_load_gif_from_memory</c>).
    /// </summary>
    /// <param name="data">The encoded GIF bytes.</param>
    /// <param name="desiredComponents">
    /// The desired output channel count per frame, or <see cref="ColorComponents.Default"/> for RGBA (4 components).
    /// </param>
    /// <param name="options">Optional decode options (limits including <see cref="StbLimits.MaxFrames"/>, vertical flip).</param>
    /// <returns>A <see cref="GifAnimation"/> containing all composited frames and millisecond delays.</returns>
    /// <exception cref="StbException">Thrown if the input is not a GIF, is malformed, or exceeds configured limits.</exception>
    public static GifAnimation LoadGifFrames(
        ReadOnlySpan<byte> data,
        ColorComponents desiredComponents = ColorComponents.Default,
        ImageDecodeOptions? options = null)
    {
        ValidateDesiredComponents(desiredComponents);
        if (!GifDecoder.IsGif(data))
        {
            throw new StbException(StbErrorCode.UnsupportedFormat, "Input is not a GIF image.");
        }

        ImageDecodeOptions effective = options ?? ImageDecodeOptions.Default;
        return GifDecoder.DecodeAnimation(data, desiredComponents, effective);
    }

    /// <summary>
    /// Reads and decodes all frames and per-frame delays of a GIF image from a <see cref="Stream"/>.
    /// </summary>
    /// <param name="stream">The readable GIF stream.</param>
    /// <param name="desiredComponents">
    /// The desired output channel count per frame, or <see cref="ColorComponents.Default"/> for RGBA (4 components).
    /// </param>
    /// <param name="options">Optional decode options.</param>
    /// <returns>A <see cref="GifAnimation"/> containing all composited frames and millisecond delays.</returns>
    public static GifAnimation LoadGifFrames(
        Stream stream,
        ColorComponents desiredComponents = ColorComponents.Default,
        ImageDecodeOptions? options = null)
    {
        Guard.ThrowIfNull(stream, nameof(stream));
        ValidateDesiredComponents(desiredComponents);
        ImageDecodeOptions effective = options ?? ImageDecodeOptions.Default;

        if (TryGetMemoryStreamSpan(stream, out ReadOnlySpan<byte> msSpan, out MemoryStream? ms))
        {
            GifAnimation result = LoadGifFrames(msSpan, desiredComponents, effective);
            ms!.Position = ms.Length;
            return result;
        }

        byte[] rented = ReadEntireStream(stream, effective.Options.Limits, out int length);
        try
        {
            return LoadGifFrames(rented.AsSpan(0, length), desiredComponents, effective);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(rented);
        }
    }

    private static ImageInfo GetInfoForFormat(ImageFormat format, ReadOnlySpan<byte> data, ImageDecodeOptions options)
    {
        StbLimits limits = options.Options.Limits;
        return format switch
        {
            ImageFormat.Png => PngDecoder.GetInfo(data, options),
            ImageFormat.Bmp => BmpDecoder.GetInfo(data, limits),
            ImageFormat.Gif => GifDecoder.GetInfo(data, limits),
            ImageFormat.Psd => PsdDecoder.GetInfo(data, limits),
            ImageFormat.Pic => PicDecoder.GetInfo(data, limits),
            ImageFormat.Jpeg => JpegDecoder.GetInfo(data, options),
            ImageFormat.Pnm => PnmDecoder.GetInfo(data, limits),
            ImageFormat.Hdr => HdrDecoder.GetInfo(data, limits),
            ImageFormat.Tga => TgaDecoder.GetInfo(data, limits),
            _ => throw new StbException(StbErrorCode.UnsupportedFormat, "Unsupported or unrecognized image format."),
        };
    }

    private static bool TryDecode8ForFormat(
        ImageFormat format,
        ReadOnlySpan<byte> data,
        Span<byte> destination,
        ColorComponents desiredComponents,
        ImageDecodeOptions options,
        out ImageInfo info,
        out ColorComponents outputComponents)
    {
        return format switch
        {
            ImageFormat.Png => PngDecoder.TryDecode8(data, destination, desiredComponents, options, out info, out outputComponents),
            ImageFormat.Bmp => BmpDecoder.TryDecode8(data, destination, desiredComponents, options, out info, out outputComponents),
            ImageFormat.Gif => GifDecoder.TryDecode8(data, destination, desiredComponents, options, out info, out outputComponents),
            ImageFormat.Psd => PsdDecoder.TryDecode8(data, destination, desiredComponents, options, out info, out outputComponents),
            ImageFormat.Pic => PicDecoder.TryDecode8(data, destination, desiredComponents, options, out info, out outputComponents),
            ImageFormat.Jpeg => JpegDecoder.TryDecode8(data, destination, desiredComponents, options, out info, out outputComponents),
            ImageFormat.Pnm => PnmDecoder.TryDecode8(data, destination, desiredComponents, options, out info, out outputComponents),
            ImageFormat.Hdr => HdrDecoder.TryDecode8(data, destination, desiredComponents, options, out info, out outputComponents),
            ImageFormat.Tga => TgaDecoder.TryDecode8(data, destination, desiredComponents, options, out info, out outputComponents),
            _ => throw new StbException(StbErrorCode.UnsupportedFormat, "Unsupported or unrecognized image format."),
        };
    }

    private static bool TryDecode16ForFormat(
        ImageFormat format,
        ReadOnlySpan<byte> data,
        Span<ushort> destination,
        ColorComponents desiredComponents,
        ImageDecodeOptions options,
        out ImageInfo info,
        out ColorComponents outputComponents)
    {
        switch (format)
        {
            case ImageFormat.Png:
                return PngDecoder.TryDecode16(data, destination, desiredComponents, options, out info, out outputComponents);
            case ImageFormat.Jpeg:
                return JpegDecoder.TryDecode16(data, destination, desiredComponents, options, out info, out outputComponents);
            case ImageFormat.Pnm:
                return PnmDecoder.TryDecode16(data, destination, desiredComponents, options, out info, out outputComponents);
            case ImageFormat.Psd:
                return PsdDecoder.TryDecode16(data, destination, desiredComponents, options, out info, out outputComponents);
            case ImageFormat.Hdr:
                return HdrDecoder.TryDecode16(data, destination, desiredComponents, options, out info, out outputComponents);
            case ImageFormat.Bmp:
            case ImageFormat.Gif:
            case ImageFormat.Pic:
            case ImageFormat.Tga:
            {
                info = GetInfoForFormat(format, data, options);
                int dstChannels = desiredComponents == ColorComponents.Default ? (int)info.Components : (int)desiredComponents;
                outputComponents = (ColorComponents)dstChannels;
                int requiredBytes16 = options.Options.Limits.ValidateDimensions(info.Width, info.Height, dstChannels * sizeof(ushort));
                int requiredSamples = requiredBytes16 / sizeof(ushort);
                if (destination.Length < requiredSamples)
                {
                    return false;
                }

                using PooledBuffer<byte> buf8 = new(requiredSamples);
                TryDecode8ForFormat(format, data, buf8.Span, desiredComponents, options, out info, out outputComponents);
                PixelConverter.Convert8To16(buf8.Span, destination.Slice(0, requiredSamples));
                return true;
            }
            default:
                throw new StbException(StbErrorCode.UnsupportedFormat, "Unsupported or unrecognized image format.");
        }
    }

    private static bool TryDecodeFloatForFormat(
        ImageFormat format,
        ReadOnlySpan<byte> data,
        Span<float> destination,
        ColorComponents desiredComponents,
        ImageDecodeOptions options,
        out ImageInfo info,
        out ColorComponents outputComponents)
    {
        if (format == ImageFormat.Hdr)
        {
            return HdrDecoder.TryDecodeFloat(data, destination, desiredComponents, options, out info, out outputComponents);
        }

        if (format == ImageFormat.Unknown)
        {
            throw new StbException(StbErrorCode.UnsupportedFormat, "Unsupported or unrecognized image format.");
        }

        info = GetInfoForFormat(format, data, options);
        int dstChannels = desiredComponents == ColorComponents.Default ? (int)info.Components : (int)desiredComponents;
        outputComponents = (ColorComponents)dstChannels;

        int requiredBytes = options.Options.Limits.ValidateDimensions(info.Width, info.Height, dstChannels * sizeof(float));
        int requiredFloats = requiredBytes / sizeof(float);
        if (destination.Length < requiredFloats)
        {
            return false;
        }

        using PooledBuffer<byte> buf8 = new(requiredFloats);
        TryDecode8ForFormat(format, data, buf8.Span, desiredComponents, options, out info, out outputComponents);
        PixelConverter.Ldr8ToHdr(
            buf8.Span,
            destination.Slice(0, requiredFloats),
            info.Width * info.Height,
            dstChannels,
            options.LdrToHdrGamma,
            options.LdrToHdrScale);
        return true;
    }

    private static void ValidateDesiredComponents(ColorComponents desiredComponents)
    {
        int req = (int)desiredComponents;
        if (req < 0 || req > 4)
        {
            throw new StbException(
                StbErrorCode.InvalidArgument,
                $"Desired components must be between Default (0) and RedGreenBlueAlpha (4), got {desiredComponents}.");
        }
    }

    private static bool TryGetMemoryStreamSpan(Stream stream, out ReadOnlySpan<byte> span, out MemoryStream? memoryStream)
    {
        if (stream is MemoryStream ms
            && ms.TryGetBuffer(out ArraySegment<byte> segment)
            && segment.Array is not null)
        {
            long pos = ms.Position;
            if (pos >= 0 && pos <= segment.Count)
            {
                span = segment.Array.AsSpan(segment.Offset + (int)pos, segment.Count - (int)pos);
                memoryStream = ms;
                return true;
            }
        }

        span = default;
        memoryStream = null;
        return false;
    }

    private static bool TryGetInfoFromStreamCore(
        Stream stream,
        ImageDecodeOptions options,
        out ImageInfo info,
        out byte[]? finalBuffer,
        out int finalLength)
    {
        long startPos = stream.CanSeek ? stream.Position : 0;
        long maxBytes = Math.Min(options.Options.Limits.MaxAllocationBytes, ArrayAllocation.MaxByteArrayLength);
        int capacity = (int)Math.Min(InitialStreamProbeBytes, Math.Max(1L, maxBytes));
        byte[] buffer = ArrayPool<byte>.Shared.Rent(capacity);
        int filled = 0;

        try
        {
            while (true)
            {
                int targetRead = capacity - filled;
                int justRead = ReadUpTo(stream, buffer, filled, targetRead);
                filled += justRead;

                if (TryGetInfo(buffer.AsSpan(0, filled), out info, options))
                {
                    ArrayPool<byte>.Shared.Return(buffer);
                    finalBuffer = null;
                    finalLength = 0;
                    return true;
                }

                // If stream hit EOF or we have a fixed-size header format where InitialStreamProbeBytes was enough, stop.
                if (justRead < targetRead)
                {
                    break;
                }

                ImageFormat fmt = DetectFormat(buffer.AsSpan(0, filled));
                if (fmt is not (ImageFormat.Jpeg or ImageFormat.Hdr or ImageFormat.Pnm or ImageFormat.Png))
                {
                    break;
                }

                if (filled >= maxBytes)
                {
                    break;
                }

                long nextCapLong = Math.Min((long)capacity * 2, maxBytes);
                int nextCap = (int)nextCapLong;
                if (nextCap <= capacity)
                {
                    break;
                }

                byte[] larger = ArrayPool<byte>.Shared.Rent(nextCap);
                Buffer.BlockCopy(buffer, 0, larger, 0, filled);
                ArrayPool<byte>.Shared.Return(buffer);
                buffer = larger;
                capacity = nextCap;
            }

            finalBuffer = buffer;
            finalLength = filled;
            info = default;
            return false;
        }
        catch
        {
            ArrayPool<byte>.Shared.Return(buffer);
            throw;
        }
        finally
        {
            if (stream.CanSeek)
            {
                stream.Position = startPos;
            }
        }
    }

    private static byte[] ReadEntireStream(Stream stream, StbLimits limits, out int length)
    {
        long maxBytes = Math.Min(limits.MaxAllocationBytes, ArrayAllocation.MaxByteArrayLength);
        int initialCapacity = (int)Math.Min(8192L, Math.Max(1L, maxBytes));
        if (stream.CanSeek)
        {
            long remaining = stream.Length - stream.Position;
            if (remaining < 0)
            {
                remaining = 0;
            }

            limits.ValidateAllocation(remaining);
            initialCapacity = remaining == 0 ? 1 : (int)remaining;
        }

        byte[] buffer = ArrayPool<byte>.Shared.Rent(initialCapacity);
        int totalRead = 0;
        try
        {
            while (true)
            {
                int remainingAllowed = (int)Math.Min((long)buffer.Length - totalRead, maxBytes - totalRead);
                if (remainingAllowed <= 0)
                {
                    int nextByte = stream.ReadByte();
                    if (nextByte < 0)
                    {
                        break;
                    }

                    if ((long)totalRead + 1 > maxBytes)
                    {
                        throw new StbException(
                            StbErrorCode.LimitExceeded,
                            $"Stream size exceeds configured allocation limit of {limits.MaxAllocationBytes} bytes.");
                    }

                    long grownLong = Math.Min(Math.Max((long)buffer.Length * 2, (long)totalRead + 1), maxBytes);
                    int grown = (int)grownLong;
                    byte[] nextBuf = ArrayPool<byte>.Shared.Rent(grown);
                    Buffer.BlockCopy(buffer, 0, nextBuf, 0, totalRead);
                    ArrayPool<byte>.Shared.Return(buffer);
                    buffer = nextBuf;
                    buffer[totalRead++] = (byte)nextByte;
                    continue;
                }

                int read = stream.Read(buffer, totalRead, remainingAllowed);
                if (read == 0)
                {
                    break;
                }

                totalRead += read;
            }

            length = totalRead;
            return buffer;
        }
        catch
        {
            ArrayPool<byte>.Shared.Return(buffer);
            throw;
        }
    }

    private static int ReadUpTo(Stream stream, byte[] buffer, int offset, int count)
    {
        int total = 0;
        while (total < count)
        {
            int read = stream.Read(buffer, offset + total, count - total);
            if (read == 0)
            {
                break;
            }

            total += read;
        }

        return total;
    }
}
