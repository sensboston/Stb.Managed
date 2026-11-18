using System;
using System.IO;
using Stb.Managed.ImageWrite.Internal;
using Stb.Managed.Internal;

namespace Stb.Managed;

/// <summary>
/// Fully managed image encoder (port of <c>stb_image_write</c>) supporting PNG, BMP, TGA, JPEG,
/// and Radiance HDR output to byte arrays, <see cref="Stream"/>s, and caller-provided <see cref="Span{T}"/> buffers.
/// </summary>
public static class StbImageWrite
{
    // =========================================================================
    // PNG
    // =========================================================================

    /// <summary>
    /// Encodes tightly packed or strided 8-bit pixels as a PNG image and returns the encoded bytes.
    /// </summary>
    /// <param name="pixels">The row-major pixel buffer (1=Y, 2=YA, 3=RGB, 4=RGBA).</param>
    /// <param name="width">The image width in pixels.</param>
    /// <param name="height">The image height in pixels.</param>
    /// <param name="components">The number of color components per pixel.</param>
    /// <param name="strideBytes">The distance in bytes between consecutive scanlines, or <c>0</c> for tightly packed rows.</param>
    /// <param name="options">Optional encoding options.</param>
    /// <returns>The encoded PNG file bytes.</returns>
    public static byte[] WritePng(
        ReadOnlySpan<byte> pixels,
        int width,
        int height,
        ColorComponents components,
        int strideBytes = 0,
        ImageWriteOptions? options = null)
    {
        ImageWriteOptions opts = options ?? ImageWriteOptions.Default;
        int effectiveStride = WriteValidation.ValidateByteImage(pixels, width, height, components, strideBytes, opts.Options.Limits);
        int estCap = Math.Max(256, Math.Min(width * height * (int)components, 4 * 1024 * 1024));
        ByteSink sink = ByteSink.ForGrowableArray(opts.Options.Limits, estCap);
        try
        {
            PngEncoder.Encode(ref sink, pixels, width, height, (int)components, effectiveStride, opts);
            return sink.ToArray();
        }
        finally
        {
            sink.Dispose();
        }
    }

    /// <summary>
    /// Encodes an <see cref="ImageBuffer{T}"/> as a PNG image and returns the encoded bytes.
    /// </summary>
    /// <param name="image">The source 8-bit image buffer.</param>
    /// <param name="options">Optional encoding options.</param>
    /// <returns>The encoded PNG file bytes.</returns>
    public static byte[] WritePng(ImageBuffer<byte> image, ImageWriteOptions? options = null)
    {
        Guard.ThrowIfNull(image, nameof(image));
        ImageWriteOptions opts = options ?? ImageWriteOptions.Default;
        ReadOnlySpan<byte> pixels = image.AsReadOnlySpan();
        int effectiveStride = WriteValidation.ValidateByteImage(pixels, image.Width, image.Height, image.Components, 0, opts.Options.Limits);
        int estCap = Math.Max(256, Math.Min(image.Width * image.Height * (int)image.Components, 4 * 1024 * 1024));
        ByteSink sink = ByteSink.ForGrowableArray(opts.Options.Limits, estCap);
        try
        {
            PngEncoder.Encode(ref sink, pixels, image.Width, image.Height, (int)image.Components, effectiveStride, opts, image.GetRawArray());
            return sink.ToArray();
        }
        finally
        {
            sink.Dispose();
        }
    }

    /// <summary>
    /// Encodes 8-bit pixels as a PNG image directly to <paramref name="destination"/>.
    /// </summary>
    /// <param name="destination">The writable output stream.</param>
    /// <param name="pixels">The row-major pixel buffer (1=Y, 2=YA, 3=RGB, 4=RGBA).</param>
    /// <param name="width">The image width in pixels.</param>
    /// <param name="height">The image height in pixels.</param>
    /// <param name="components">The number of color components per pixel.</param>
    /// <param name="strideBytes">The distance in bytes between consecutive scanlines, or <c>0</c> for tightly packed rows.</param>
    /// <param name="options">Optional encoding options.</param>
    public static void WritePng(
        Stream destination,
        ReadOnlySpan<byte> pixels,
        int width,
        int height,
        ColorComponents components,
        int strideBytes = 0,
        ImageWriteOptions? options = null)
    {
        Guard.ThrowIfNull(destination, nameof(destination));
        ImageWriteOptions opts = options ?? ImageWriteOptions.Default;
        int effectiveStride = WriteValidation.ValidateByteImage(pixels, width, height, components, strideBytes, opts.Options.Limits);
        ByteSink sink = ByteSink.ForStream(destination, opts.Options.Limits);
        try
        {
            PngEncoder.Encode(ref sink, pixels, width, height, (int)components, effectiveStride, opts);
            sink.Flush();
        }
        finally
        {
            sink.Dispose();
        }
    }

    /// <summary>
    /// Encodes an <see cref="ImageBuffer{T}"/> as a PNG image directly to <paramref name="destination"/>.
    /// </summary>
    /// <param name="destination">The writable output stream.</param>
    /// <param name="image">The source 8-bit image buffer.</param>
    /// <param name="options">Optional encoding options.</param>
    public static void WritePng(Stream destination, ImageBuffer<byte> image, ImageWriteOptions? options = null)
    {
        Guard.ThrowIfNull(destination, nameof(destination));
        Guard.ThrowIfNull(image, nameof(image));
        ImageWriteOptions opts = options ?? ImageWriteOptions.Default;
        ReadOnlySpan<byte> pixels = image.AsReadOnlySpan();
        int effectiveStride = WriteValidation.ValidateByteImage(pixels, image.Width, image.Height, image.Components, 0, opts.Options.Limits);
        ByteSink sink = ByteSink.ForStream(destination, opts.Options.Limits);
        try
        {
            PngEncoder.Encode(ref sink, pixels, image.Width, image.Height, (int)image.Components, effectiveStride, opts, image.GetRawArray());
            sink.Flush();
        }
        finally
        {
            sink.Dispose();
        }
    }

    /// <summary>
    /// Attempts to encode 8-bit pixels as a PNG image into caller-provided <paramref name="destination"/>.
    /// Returns <see langword="false"/> if <paramref name="destination"/> is too small.
    /// </summary>
    /// <param name="pixels">The row-major pixel buffer (1=Y, 2=YA, 3=RGB, 4=RGBA).</param>
    /// <param name="width">The image width in pixels.</param>
    /// <param name="height">The image height in pixels.</param>
    /// <param name="components">The number of color components per pixel.</param>
    /// <param name="destination">The destination span receiving the encoded PNG bytes.</param>
    /// <param name="bytesWritten">When this method returns <see langword="true"/>, receives the number of bytes written; otherwise <c>0</c>.</param>
    /// <param name="strideBytes">The distance in bytes between consecutive scanlines, or <c>0</c> for tightly packed rows.</param>
    /// <param name="options">Optional encoding options.</param>
    /// <returns><see langword="true"/> if the encoded PNG fit in <paramref name="destination"/>; otherwise <see langword="false"/>.</returns>
    public static bool TryWritePng(
        ReadOnlySpan<byte> pixels,
        int width,
        int height,
        ColorComponents components,
        Span<byte> destination,
        out int bytesWritten,
        int strideBytes = 0,
        ImageWriteOptions? options = null)
    {
        ImageWriteOptions opts = options ?? ImageWriteOptions.Default;
        int effectiveStride = WriteValidation.ValidateByteImage(pixels, width, height, components, strideBytes, opts.Options.Limits);
        ByteSink sink = ByteSink.ForSpan(destination, opts.Options.Limits);
        PngEncoder.Encode(ref sink, pixels, width, height, (int)components, effectiveStride, opts);
        if (sink.Overflowed)
        {
            bytesWritten = 0;
            return false;
        }

        bytesWritten = sink.Position;
        return true;
    }

    // =========================================================================
    // BMP
    // =========================================================================

    /// <summary>
    /// Encodes 8-bit pixels as a BMP image (24bpp for 1-3 channels, 32bpp V4 BI_BITFIELDS for RGBA)
    /// and returns the encoded bytes.
    /// </summary>
    /// <param name="pixels">The row-major pixel buffer (1=Y, 2=YA, 3=RGB, 4=RGBA).</param>
    /// <param name="width">The image width in pixels.</param>
    /// <param name="height">The image height in pixels.</param>
    /// <param name="components">The number of color components per pixel.</param>
    /// <param name="strideBytes">The distance in bytes between consecutive scanlines, or <c>0</c> for tightly packed rows.</param>
    /// <param name="options">Optional encoding options.</param>
    /// <returns>The encoded BMP file bytes.</returns>
    public static byte[] WriteBmp(
        ReadOnlySpan<byte> pixels,
        int width,
        int height,
        ColorComponents components,
        int strideBytes = 0,
        ImageWriteOptions? options = null)
    {
        ImageWriteOptions opts = options ?? ImageWriteOptions.Default;
        int effectiveStride = WriteValidation.ValidateByteImage(pixels, width, height, components, strideBytes, opts.Options.Limits);
        int outputSize = BmpEncoder.ComputeOutputSize(width, height, (int)components, opts.Options.Limits);
        byte[] result = ArrayAllocation.AllocateUninitialized<byte>(outputSize);
        ByteSink sink = ByteSink.ForSpan(result, opts.Options.Limits);
        BmpEncoder.Encode(ref sink, pixels, width, height, (int)components, effectiveStride, opts.FlipVertically, outputSize);
        return result;
    }

    /// <summary>
    /// Encodes an <see cref="ImageBuffer{T}"/> as a BMP image and returns the encoded bytes.
    /// </summary>
    /// <param name="image">The source 8-bit image buffer.</param>
    /// <param name="options">Optional encoding options.</param>
    /// <returns>The encoded BMP file bytes.</returns>
    public static byte[] WriteBmp(ImageBuffer<byte> image, ImageWriteOptions? options = null)
    {
        Guard.ThrowIfNull(image, nameof(image));
        return WriteBmp(image.AsReadOnlySpan(), image.Width, image.Height, image.Components, 0, options);
    }

    /// <summary>
    /// Encodes 8-bit pixels as a BMP image directly to <paramref name="destination"/>.
    /// </summary>
    /// <param name="destination">The writable output stream.</param>
    /// <param name="pixels">The row-major pixel buffer (1=Y, 2=YA, 3=RGB, 4=RGBA).</param>
    /// <param name="width">The image width in pixels.</param>
    /// <param name="height">The image height in pixels.</param>
    /// <param name="components">The number of color components per pixel.</param>
    /// <param name="strideBytes">The distance in bytes between consecutive scanlines, or <c>0</c> for tightly packed rows.</param>
    /// <param name="options">Optional encoding options.</param>
    public static void WriteBmp(
        Stream destination,
        ReadOnlySpan<byte> pixels,
        int width,
        int height,
        ColorComponents components,
        int strideBytes = 0,
        ImageWriteOptions? options = null)
    {
        Guard.ThrowIfNull(destination, nameof(destination));
        ImageWriteOptions opts = options ?? ImageWriteOptions.Default;
        int effectiveStride = WriteValidation.ValidateByteImage(pixels, width, height, components, strideBytes, opts.Options.Limits);
        int outputSize = BmpEncoder.ComputeOutputSize(width, height, (int)components, opts.Options.Limits);
        ByteSink sink = ByteSink.ForStream(destination, opts.Options.Limits);
        try
        {
            BmpEncoder.Encode(ref sink, pixels, width, height, (int)components, effectiveStride, opts.FlipVertically, outputSize);
            sink.Flush();
        }
        finally
        {
            sink.Dispose();
        }
    }

    /// <summary>
    /// Encodes an <see cref="ImageBuffer{T}"/> as a BMP image directly to <paramref name="destination"/>.
    /// </summary>
    /// <param name="destination">The writable output stream.</param>
    /// <param name="image">The source 8-bit image buffer.</param>
    /// <param name="options">Optional encoding options.</param>
    public static void WriteBmp(Stream destination, ImageBuffer<byte> image, ImageWriteOptions? options = null)
    {
        Guard.ThrowIfNull(image, nameof(image));
        WriteBmp(destination, image.AsReadOnlySpan(), image.Width, image.Height, image.Components, 0, options);
    }

    /// <summary>
    /// Attempts to encode 8-bit pixels as a BMP image into caller-provided <paramref name="destination"/>.
    /// Returns <see langword="false"/> if <paramref name="destination"/> is too small.
    /// </summary>
    /// <param name="pixels">The row-major pixel buffer (1=Y, 2=YA, 3=RGB, 4=RGBA).</param>
    /// <param name="width">The image width in pixels.</param>
    /// <param name="height">The image height in pixels.</param>
    /// <param name="components">The number of color components per pixel.</param>
    /// <param name="destination">The destination span receiving the encoded BMP bytes.</param>
    /// <param name="bytesWritten">When this method returns <see langword="true"/>, receives the number of bytes written; otherwise <c>0</c>.</param>
    /// <param name="strideBytes">The distance in bytes between consecutive scanlines, or <c>0</c> for tightly packed rows.</param>
    /// <param name="options">Optional encoding options.</param>
    /// <returns><see langword="true"/> if the encoded BMP fit in <paramref name="destination"/>; otherwise <see langword="false"/>.</returns>
    public static bool TryWriteBmp(
        ReadOnlySpan<byte> pixels,
        int width,
        int height,
        ColorComponents components,
        Span<byte> destination,
        out int bytesWritten,
        int strideBytes = 0,
        ImageWriteOptions? options = null)
    {
        ImageWriteOptions opts = options ?? ImageWriteOptions.Default;
        int effectiveStride = WriteValidation.ValidateByteImage(pixels, width, height, components, strideBytes, opts.Options.Limits);
        int outputSize = BmpEncoder.ComputeOutputSize(width, height, (int)components, opts.Options.Limits);
        if (destination.Length < outputSize)
        {
            bytesWritten = 0;
            return false;
        }

        ByteSink sink = ByteSink.ForSpan(destination, opts.Options.Limits);
        BmpEncoder.Encode(ref sink, pixels, width, height, (int)components, effectiveStride, opts.FlipVertically, outputSize);
        bytesWritten = sink.Position;
        return true;
    }

    // =========================================================================
    // TGA
    // =========================================================================

    /// <summary>
    /// Encodes 8-bit pixels as a TGA image (RLE or uncompressed) and returns the encoded bytes.
    /// </summary>
    /// <param name="pixels">The row-major pixel buffer (1=Y, 2=YA, 3=RGB, 4=RGBA).</param>
    /// <param name="width">The image width in pixels (1..65535).</param>
    /// <param name="height">The image height in pixels (1..65535).</param>
    /// <param name="components">The number of color components per pixel.</param>
    /// <param name="strideBytes">The distance in bytes between consecutive scanlines, or <c>0</c> for tightly packed rows.</param>
    /// <param name="options">Optional encoding options.</param>
    /// <returns>The encoded TGA file bytes.</returns>
    public static byte[] WriteTga(
        ReadOnlySpan<byte> pixels,
        int width,
        int height,
        ColorComponents components,
        int strideBytes = 0,
        ImageWriteOptions? options = null)
    {
        ImageWriteOptions opts = options ?? ImageWriteOptions.Default;
        int effectiveStride = WriteValidation.ValidateByteImage(
            pixels, width, height, components, strideBytes, opts.Options.Limits,
            TgaEncoder.MaxDimension, TgaEncoder.MaxDimension);

        if (!opts.TgaUseRle)
        {
            long exactLen = 18L + ((long)width * height * (int)components);
            opts.Options.Limits.ValidateAllocation(exactLen);
            byte[] result = ArrayAllocation.AllocateUninitialized<byte>((int)exactLen);
            ByteSink fixedSink = ByteSink.ForSpan(result, opts.Options.Limits);
            TgaEncoder.Encode(ref fixedSink, pixels, width, height, (int)components, effectiveStride, useRle: false, opts.FlipVertically);
            return result;
        }

        int estCap = Math.Max(256, Math.Min((width * height * (int)components) + 64, 4 * 1024 * 1024));
        ByteSink sink = ByteSink.ForGrowableArray(opts.Options.Limits, estCap);
        try
        {
            TgaEncoder.Encode(ref sink, pixels, width, height, (int)components, effectiveStride, useRle: true, opts.FlipVertically);
            return sink.ToArray();
        }
        finally
        {
            sink.Dispose();
        }
    }

    /// <summary>
    /// Encodes an <see cref="ImageBuffer{T}"/> as a TGA image and returns the encoded bytes.
    /// </summary>
    /// <param name="image">The source 8-bit image buffer.</param>
    /// <param name="options">Optional encoding options.</param>
    /// <returns>The encoded TGA file bytes.</returns>
    public static byte[] WriteTga(ImageBuffer<byte> image, ImageWriteOptions? options = null)
    {
        Guard.ThrowIfNull(image, nameof(image));
        return WriteTga(image.AsReadOnlySpan(), image.Width, image.Height, image.Components, 0, options);
    }

    /// <summary>
    /// Encodes 8-bit pixels as a TGA image directly to <paramref name="destination"/>.
    /// </summary>
    /// <param name="destination">The writable output stream.</param>
    /// <param name="pixels">The row-major pixel buffer (1=Y, 2=YA, 3=RGB, 4=RGBA).</param>
    /// <param name="width">The image width in pixels (1..65535).</param>
    /// <param name="height">The image height in pixels (1..65535).</param>
    /// <param name="components">The number of color components per pixel.</param>
    /// <param name="strideBytes">The distance in bytes between consecutive scanlines, or <c>0</c> for tightly packed rows.</param>
    /// <param name="options">Optional encoding options.</param>
    public static void WriteTga(
        Stream destination,
        ReadOnlySpan<byte> pixels,
        int width,
        int height,
        ColorComponents components,
        int strideBytes = 0,
        ImageWriteOptions? options = null)
    {
        Guard.ThrowIfNull(destination, nameof(destination));
        ImageWriteOptions opts = options ?? ImageWriteOptions.Default;
        int effectiveStride = WriteValidation.ValidateByteImage(
            pixels, width, height, components, strideBytes, opts.Options.Limits,
            TgaEncoder.MaxDimension, TgaEncoder.MaxDimension);
        ByteSink sink = ByteSink.ForStream(destination, opts.Options.Limits);
        try
        {
            TgaEncoder.Encode(ref sink, pixels, width, height, (int)components, effectiveStride, opts.TgaUseRle, opts.FlipVertically);
            sink.Flush();
        }
        finally
        {
            sink.Dispose();
        }
    }

    /// <summary>
    /// Encodes an <see cref="ImageBuffer{T}"/> as a TGA image directly to <paramref name="destination"/>.
    /// </summary>
    /// <param name="destination">The writable output stream.</param>
    /// <param name="image">The source 8-bit image buffer.</param>
    /// <param name="options">Optional encoding options.</param>
    public static void WriteTga(Stream destination, ImageBuffer<byte> image, ImageWriteOptions? options = null)
    {
        Guard.ThrowIfNull(image, nameof(image));
        WriteTga(destination, image.AsReadOnlySpan(), image.Width, image.Height, image.Components, 0, options);
    }

    /// <summary>
    /// Attempts to encode 8-bit pixels as a TGA image into caller-provided <paramref name="destination"/>.
    /// Returns <see langword="false"/> if <paramref name="destination"/> is too small.
    /// </summary>
    /// <param name="pixels">The row-major pixel buffer (1=Y, 2=YA, 3=RGB, 4=RGBA).</param>
    /// <param name="width">The image width in pixels (1..65535).</param>
    /// <param name="height">The image height in pixels (1..65535).</param>
    /// <param name="components">The number of color components per pixel.</param>
    /// <param name="destination">The destination span receiving the encoded TGA bytes.</param>
    /// <param name="bytesWritten">When this method returns <see langword="true"/>, receives the number of bytes written; otherwise <c>0</c>.</param>
    /// <param name="strideBytes">The distance in bytes between consecutive scanlines, or <c>0</c> for tightly packed rows.</param>
    /// <param name="options">Optional encoding options.</param>
    /// <returns><see langword="true"/> if the encoded TGA fit in <paramref name="destination"/>; otherwise <see langword="false"/>.</returns>
    public static bool TryWriteTga(
        ReadOnlySpan<byte> pixels,
        int width,
        int height,
        ColorComponents components,
        Span<byte> destination,
        out int bytesWritten,
        int strideBytes = 0,
        ImageWriteOptions? options = null)
    {
        ImageWriteOptions opts = options ?? ImageWriteOptions.Default;
        int effectiveStride = WriteValidation.ValidateByteImage(
            pixels, width, height, components, strideBytes, opts.Options.Limits,
            TgaEncoder.MaxDimension, TgaEncoder.MaxDimension);
        ByteSink sink = ByteSink.ForSpan(destination, opts.Options.Limits);
        TgaEncoder.Encode(ref sink, pixels, width, height, (int)components, effectiveStride, opts.TgaUseRle, opts.FlipVertically);
        if (sink.Overflowed)
        {
            bytesWritten = 0;
            return false;
        }

        bytesWritten = sink.Position;
        return true;
    }

    // =========================================================================
    // JPEG
    // =========================================================================

    /// <summary>
    /// Encodes 8-bit pixels as a baseline JFIF JPEG image and returns the encoded bytes.
    /// Alpha channels are ignored; quality &lt;= 90 uses 4:2:0 subsampling and quality &gt; 90 uses 4:4:4.
    /// </summary>
    /// <param name="pixels">The row-major pixel buffer (1=Y, 2=YA, 3=RGB, 4=RGBA).</param>
    /// <param name="width">The image width in pixels (1..65535).</param>
    /// <param name="height">The image height in pixels (1..65535).</param>
    /// <param name="components">The number of color components per pixel.</param>
    /// <param name="quality">The JPEG quality setting from <c>1</c> to <c>100</c> (default <c>90</c>).</param>
    /// <param name="strideBytes">The distance in bytes between consecutive scanlines, or <c>0</c> for tightly packed rows.</param>
    /// <param name="options">Optional encoding options.</param>
    /// <returns>The encoded JPEG file bytes.</returns>
    public static byte[] WriteJpeg(
        ReadOnlySpan<byte> pixels,
        int width,
        int height,
        ColorComponents components,
        int quality = 90,
        int strideBytes = 0,
        ImageWriteOptions? options = null)
    {
        ImageWriteOptions opts = options ?? ImageWriteOptions.Default;
        int validQuality = WriteValidation.ValidateJpegQuality(quality);
        int effectiveStride = WriteValidation.ValidateByteImage(
            pixels, width, height, components, strideBytes, opts.Options.Limits,
            JpegEncoder.MaxDimension, JpegEncoder.MaxDimension);
        int estCap = Math.Max(1024, Math.Min((width * height) + 1024, 4 * 1024 * 1024));
        ByteSink sink = ByteSink.ForGrowableArray(opts.Options.Limits, estCap);
        try
        {
            JpegEncoder.Encode(ref sink, pixels, width, height, (int)components, effectiveStride, validQuality, opts.FlipVertically, opts.Options);
            return sink.ToArray();
        }
        finally
        {
            sink.Dispose();
        }
    }

    /// <summary>
    /// Encodes an <see cref="ImageBuffer{T}"/> as a baseline JPEG image and returns the encoded bytes.
    /// </summary>
    /// <param name="image">The source 8-bit image buffer.</param>
    /// <param name="quality">The JPEG quality setting from <c>1</c> to <c>100</c> (default <c>90</c>).</param>
    /// <param name="options">Optional encoding options.</param>
    /// <returns>The encoded JPEG file bytes.</returns>
    public static byte[] WriteJpeg(ImageBuffer<byte> image, int quality = 90, ImageWriteOptions? options = null)
    {
        Guard.ThrowIfNull(image, nameof(image));
        ImageWriteOptions opts = options ?? ImageWriteOptions.Default;
        int validQuality = WriteValidation.ValidateJpegQuality(quality);
        ReadOnlySpan<byte> pixels = image.AsReadOnlySpan();
        int effectiveStride = WriteValidation.ValidateByteImage(
            pixels, image.Width, image.Height, image.Components, 0, opts.Options.Limits,
            JpegEncoder.MaxDimension, JpegEncoder.MaxDimension);
        int estCap = Math.Max(1024, Math.Min((image.Width * image.Height) + 1024, 4 * 1024 * 1024));
        ByteSink sink = ByteSink.ForGrowableArray(opts.Options.Limits, estCap);
        try
        {
            JpegEncoder.Encode(ref sink, pixels, image.Width, image.Height, (int)image.Components, effectiveStride, validQuality, opts.FlipVertically, opts.Options, image.GetRawArray());
            return sink.ToArray();
        }
        finally
        {
            sink.Dispose();
        }
    }

    /// <summary>
    /// Encodes 8-bit pixels as a baseline JPEG image directly to <paramref name="destination"/>.
    /// </summary>
    /// <param name="destination">The writable output stream.</param>
    /// <param name="pixels">The row-major pixel buffer (1=Y, 2=YA, 3=RGB, 4=RGBA).</param>
    /// <param name="width">The image width in pixels (1..65535).</param>
    /// <param name="height">The image height in pixels (1..65535).</param>
    /// <param name="components">The number of color components per pixel.</param>
    /// <param name="quality">The JPEG quality setting from <c>1</c> to <c>100</c> (default <c>90</c>).</param>
    /// <param name="strideBytes">The distance in bytes between consecutive scanlines, or <c>0</c> for tightly packed rows.</param>
    /// <param name="options">Optional encoding options.</param>
    public static void WriteJpeg(
        Stream destination,
        ReadOnlySpan<byte> pixels,
        int width,
        int height,
        ColorComponents components,
        int quality = 90,
        int strideBytes = 0,
        ImageWriteOptions? options = null)
    {
        Guard.ThrowIfNull(destination, nameof(destination));
        ImageWriteOptions opts = options ?? ImageWriteOptions.Default;
        int validQuality = WriteValidation.ValidateJpegQuality(quality);
        int effectiveStride = WriteValidation.ValidateByteImage(
            pixels, width, height, components, strideBytes, opts.Options.Limits,
            JpegEncoder.MaxDimension, JpegEncoder.MaxDimension);
        ByteSink sink = ByteSink.ForStream(destination, opts.Options.Limits);
        try
        {
            JpegEncoder.Encode(ref sink, pixels, width, height, (int)components, effectiveStride, validQuality, opts.FlipVertically, opts.Options);
            sink.Flush();
        }
        finally
        {
            sink.Dispose();
        }
    }

    /// <summary>
    /// Encodes an <see cref="ImageBuffer{T}"/> as a baseline JPEG image directly to <paramref name="destination"/>.
    /// </summary>
    /// <param name="destination">The writable output stream.</param>
    /// <param name="image">The source 8-bit image buffer.</param>
    /// <param name="quality">The JPEG quality setting from <c>1</c> to <c>100</c> (default <c>90</c>).</param>
    /// <param name="options">Optional encoding options.</param>
    public static void WriteJpeg(Stream destination, ImageBuffer<byte> image, int quality = 90, ImageWriteOptions? options = null)
    {
        Guard.ThrowIfNull(destination, nameof(destination));
        Guard.ThrowIfNull(image, nameof(image));
        ImageWriteOptions opts = options ?? ImageWriteOptions.Default;
        int validQuality = WriteValidation.ValidateJpegQuality(quality);
        ReadOnlySpan<byte> pixels = image.AsReadOnlySpan();
        int effectiveStride = WriteValidation.ValidateByteImage(
            pixels, image.Width, image.Height, image.Components, 0, opts.Options.Limits,
            JpegEncoder.MaxDimension, JpegEncoder.MaxDimension);
        ByteSink sink = ByteSink.ForStream(destination, opts.Options.Limits);
        try
        {
            JpegEncoder.Encode(ref sink, pixels, image.Width, image.Height, (int)image.Components, effectiveStride, validQuality, opts.FlipVertically, opts.Options, image.GetRawArray());
            sink.Flush();
        }
        finally
        {
            sink.Dispose();
        }
    }

    /// <summary>
    /// Attempts to encode 8-bit pixels as a baseline JPEG image into caller-provided <paramref name="destination"/>.
    /// Returns <see langword="false"/> if <paramref name="destination"/> is too small.
    /// </summary>
    /// <param name="pixels">The row-major pixel buffer (1=Y, 2=YA, 3=RGB, 4=RGBA).</param>
    /// <param name="width">The image width in pixels (1..65535).</param>
    /// <param name="height">The image height in pixels (1..65535).</param>
    /// <param name="components">The number of color components per pixel.</param>
    /// <param name="destination">The destination span receiving the encoded JPEG bytes.</param>
    /// <param name="bytesWritten">When this method returns <see langword="true"/>, receives the number of bytes written; otherwise <c>0</c>.</param>
    /// <param name="quality">The JPEG quality setting from <c>1</c> to <c>100</c> (default <c>90</c>).</param>
    /// <param name="strideBytes">The distance in bytes between consecutive scanlines, or <c>0</c> for tightly packed rows.</param>
    /// <param name="options">Optional encoding options.</param>
    /// <returns><see langword="true"/> if the encoded JPEG fit in <paramref name="destination"/>; otherwise <see langword="false"/>.</returns>
    public static bool TryWriteJpeg(
        ReadOnlySpan<byte> pixels,
        int width,
        int height,
        ColorComponents components,
        Span<byte> destination,
        out int bytesWritten,
        int quality = 90,
        int strideBytes = 0,
        ImageWriteOptions? options = null)
    {
        ImageWriteOptions opts = options ?? ImageWriteOptions.Default;
        int validQuality = WriteValidation.ValidateJpegQuality(quality);
        int effectiveStride = WriteValidation.ValidateByteImage(
            pixels, width, height, components, strideBytes, opts.Options.Limits,
            JpegEncoder.MaxDimension, JpegEncoder.MaxDimension);
        ByteSink sink = ByteSink.ForSpan(destination, opts.Options.Limits);
        JpegEncoder.Encode(ref sink, pixels, width, height, (int)components, effectiveStride, validQuality, opts.FlipVertically, opts.Options);
        if (sink.Overflowed)
        {
            bytesWritten = 0;
            return false;
        }

        bytesWritten = sink.Position;
        return true;
    }

    // =========================================================================
    // Radiance HDR
    // =========================================================================

    /// <summary>
    /// Encodes linear floating-point pixels as a Radiance RGBE HDR image and returns the encoded bytes.
    /// Alpha channels are discarded; monochrome channels are replicated across RGB.
    /// </summary>
    /// <param name="pixels">The row-major linear float pixel buffer (1=Y, 2=YA, 3=RGB, 4=RGBA).</param>
    /// <param name="width">The image width in pixels.</param>
    /// <param name="height">The image height in pixels.</param>
    /// <param name="components">The number of color components per pixel.</param>
    /// <param name="strideFloats">The distance in <see cref="float"/> samples between consecutive scanlines, or <c>0</c> for tightly packed rows.</param>
    /// <param name="options">Optional encoding options.</param>
    /// <returns>The encoded HDR file bytes.</returns>
    public static byte[] WriteHdr(
        ReadOnlySpan<float> pixels,
        int width,
        int height,
        ColorComponents components,
        int strideFloats = 0,
        ImageWriteOptions? options = null)
    {
        ImageWriteOptions opts = options ?? ImageWriteOptions.Default;
        int effectiveStride = WriteValidation.ValidateFloatImage(pixels, width, height, components, strideFloats, opts.Options.Limits);
        int estCap = Math.Max(256, Math.Min((width * height * 4) + 128, 4 * 1024 * 1024));
        ByteSink sink = ByteSink.ForGrowableArray(opts.Options.Limits, estCap);
        try
        {
            HdrEncoder.Encode(ref sink, pixels, width, height, (int)components, effectiveStride, opts.FlipVertically);
            return sink.ToArray();
        }
        finally
        {
            sink.Dispose();
        }
    }

    /// <summary>
    /// Encodes a floating-point <see cref="ImageBuffer{T}"/> as a Radiance HDR image and returns the encoded bytes.
    /// </summary>
    /// <param name="image">The source linear float image buffer.</param>
    /// <param name="options">Optional encoding options.</param>
    /// <returns>The encoded HDR file bytes.</returns>
    public static byte[] WriteHdr(ImageBuffer<float> image, ImageWriteOptions? options = null)
    {
        Guard.ThrowIfNull(image, nameof(image));
        return WriteHdr(image.AsReadOnlySpan(), image.Width, image.Height, image.Components, 0, options);
    }

    /// <summary>
    /// Encodes linear floating-point pixels as a Radiance HDR image directly to <paramref name="destination"/>.
    /// </summary>
    /// <param name="destination">The writable output stream.</param>
    /// <param name="pixels">The row-major linear float pixel buffer (1=Y, 2=YA, 3=RGB, 4=RGBA).</param>
    /// <param name="width">The image width in pixels.</param>
    /// <param name="height">The image height in pixels.</param>
    /// <param name="components">The number of color components per pixel.</param>
    /// <param name="strideFloats">The distance in <see cref="float"/> samples between consecutive scanlines, or <c>0</c> for tightly packed rows.</param>
    /// <param name="options">Optional encoding options.</param>
    public static void WriteHdr(
        Stream destination,
        ReadOnlySpan<float> pixels,
        int width,
        int height,
        ColorComponents components,
        int strideFloats = 0,
        ImageWriteOptions? options = null)
    {
        Guard.ThrowIfNull(destination, nameof(destination));
        ImageWriteOptions opts = options ?? ImageWriteOptions.Default;
        int effectiveStride = WriteValidation.ValidateFloatImage(pixels, width, height, components, strideFloats, opts.Options.Limits);
        ByteSink sink = ByteSink.ForStream(destination, opts.Options.Limits);
        try
        {
            HdrEncoder.Encode(ref sink, pixels, width, height, (int)components, effectiveStride, opts.FlipVertically);
            sink.Flush();
        }
        finally
        {
            sink.Dispose();
        }
    }

    /// <summary>
    /// Encodes a floating-point <see cref="ImageBuffer{T}"/> as a Radiance HDR image directly to <paramref name="destination"/>.
    /// </summary>
    /// <param name="destination">The writable output stream.</param>
    /// <param name="image">The source linear float image buffer.</param>
    /// <param name="options">Optional encoding options.</param>
    public static void WriteHdr(Stream destination, ImageBuffer<float> image, ImageWriteOptions? options = null)
    {
        Guard.ThrowIfNull(image, nameof(image));
        WriteHdr(destination, image.AsReadOnlySpan(), image.Width, image.Height, image.Components, 0, options);
    }

    /// <summary>
    /// Attempts to encode linear floating-point pixels as a Radiance HDR image into caller-provided <paramref name="destination"/>.
    /// Returns <see langword="false"/> if <paramref name="destination"/> is too small.
    /// </summary>
    /// <param name="pixels">The row-major linear float pixel buffer (1=Y, 2=YA, 3=RGB, 4=RGBA).</param>
    /// <param name="width">The image width in pixels.</param>
    /// <param name="height">The image height in pixels.</param>
    /// <param name="components">The number of color components per pixel.</param>
    /// <param name="destination">The destination span receiving the encoded HDR bytes.</param>
    /// <param name="bytesWritten">When this method returns <see langword="true"/>, receives the number of bytes written; otherwise <c>0</c>.</param>
    /// <param name="strideFloats">The distance in <see cref="float"/> samples between consecutive scanlines, or <c>0</c> for tightly packed rows.</param>
    /// <param name="options">Optional encoding options.</param>
    /// <returns><see langword="true"/> if the encoded HDR fit in <paramref name="destination"/>; otherwise <see langword="false"/>.</returns>
    public static bool TryWriteHdr(
        ReadOnlySpan<float> pixels,
        int width,
        int height,
        ColorComponents components,
        Span<byte> destination,
        out int bytesWritten,
        int strideFloats = 0,
        ImageWriteOptions? options = null)
    {
        ImageWriteOptions opts = options ?? ImageWriteOptions.Default;
        int effectiveStride = WriteValidation.ValidateFloatImage(pixels, width, height, components, strideFloats, opts.Options.Limits);
        ByteSink sink = ByteSink.ForSpan(destination, opts.Options.Limits);
        HdrEncoder.Encode(ref sink, pixels, width, height, (int)components, effectiveStride, opts.FlipVertically);
        if (sink.Overflowed)
        {
            bytesWritten = 0;
            return false;
        }

        bytesWritten = sink.Position;
        return true;
    }
}
