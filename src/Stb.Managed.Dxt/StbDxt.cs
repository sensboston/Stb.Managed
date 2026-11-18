using Stb.Managed.Dxt.Internal;
using Stb.Managed.Internal;

namespace Stb.Managed.Dxt;

/// <summary>
/// Fully managed BC1/BC3/BC4/BC5 (DXT1/DXT5) GPU texture block compressor, a SIMD-accelerated port of
/// Fabian "ryg" Giesen and Sean Barrett's <c>stb_dxt.h</c> v1.13.
/// </summary>
public static class StbDxt
{
    private const int ParallelBlockRowThreshold = 16;

    /// <summary>
    /// Compresses a single 4x4 RGBA pixel block into BC1 (DXT1, 8 bytes) or BC3 (DXT5, 16 bytes).
    /// </summary>
    /// <param name="destination">Output buffer (at least 8 bytes when <paramref name="includeAlpha"/> is <c>false</c>, or 16 bytes when <c>true</c>).</param>
    /// <param name="sourceRgba">Row-major 4x4 RGBA block (at least 64 bytes; 4 bytes per pixel).</param>
    /// <param name="includeAlpha"><c>true</c> for BC3/DXT5 (with alpha block); <c>false</c> for BC1/DXT1 (opaque RGB).</param>
    /// <param name="mode">Compression quality mode.</param>
    public static void CompressDxtBlock(
        Span<byte> destination,
        ReadOnlySpan<byte> sourceRgba,
        bool includeAlpha = false,
        DxtCompressionMode mode = DxtCompressionMode.Normal)
    {
        int requiredDest = includeAlpha ? 16 : 8;
        if (destination.Length < requiredDest)
        {
            throw new ArgumentException($"Destination span must be at least {requiredDest} bytes.", nameof(destination));
        }

        if (sourceRgba.Length < 64)
        {
            throw new ArgumentException("Source 4x4 RGBA block must be at least 64 bytes.", nameof(sourceRgba));
        }

        DxtBlockEncoder.CompressDxtBlock(destination, sourceRgba, includeAlpha, mode);
    }

    /// <summary>
    /// Compresses a single 4x4 RGBA pixel block into BC1 / DXT1 (8 bytes).
    /// </summary>
    /// <param name="destination">Output buffer (at least 8 bytes).</param>
    /// <param name="sourceRgba">Row-major 4x4 RGBA block (at least 64 bytes).</param>
    /// <param name="mode">Compression quality mode.</param>
    public static void CompressBc1Block(
        Span<byte> destination,
        ReadOnlySpan<byte> sourceRgba,
        DxtCompressionMode mode = DxtCompressionMode.Normal) =>
        CompressDxtBlock(destination, sourceRgba, includeAlpha: false, mode);

    /// <summary>
    /// Compresses a single 4x4 RGBA pixel block into BC3 / DXT5 (16 bytes).
    /// </summary>
    /// <param name="destination">Output buffer (at least 16 bytes).</param>
    /// <param name="sourceRgba">Row-major 4x4 RGBA block (at least 64 bytes).</param>
    /// <param name="mode">Compression quality mode.</param>
    public static void CompressBc3Block(
        Span<byte> destination,
        ReadOnlySpan<byte> sourceRgba,
        DxtCompressionMode mode = DxtCompressionMode.Normal) =>
        CompressDxtBlock(destination, sourceRgba, includeAlpha: true, mode);

    /// <summary>
    /// Compresses a single 4x4 single-channel (R) pixel block into BC4 (8 bytes).
    /// </summary>
    /// <param name="destination">Output buffer (at least 8 bytes).</param>
    /// <param name="sourceR">Row-major 4x4 single-channel block (at least 16 bytes).</param>
    public static void CompressBc4Block(Span<byte> destination, ReadOnlySpan<byte> sourceR)
    {
        if (destination.Length < 8)
        {
            throw new ArgumentException("Destination span must be at least 8 bytes.", nameof(destination));
        }

        if (sourceR.Length < 16)
        {
            throw new ArgumentException("Source 4x4 R block must be at least 16 bytes.", nameof(sourceR));
        }

        DxtBlockEncoder.CompressBc4Block(destination, sourceR);
    }

    /// <summary>
    /// Compresses a single 4x4 two-channel (RG) pixel block into BC5 (16 bytes).
    /// </summary>
    /// <param name="destination">Output buffer (at least 16 bytes).</param>
    /// <param name="sourceRg">Row-major 4x4 two-channel interleaved RG block (at least 32 bytes).</param>
    public static void CompressBc5Block(Span<byte> destination, ReadOnlySpan<byte> sourceRg)
    {
        if (destination.Length < 16)
        {
            throw new ArgumentException("Destination span must be at least 16 bytes.", nameof(destination));
        }

        if (sourceRg.Length < 32)
        {
            throw new ArgumentException("Source 4x4 RG block must be at least 32 bytes.", nameof(sourceRg));
        }

        DxtBlockEncoder.CompressBc5Block(destination, sourceRg);
    }

    /// <summary>
    /// Computes the exact compressed byte size for an image of dimensions <paramref name="width"/> by <paramref name="height"/>.
    /// Non-multiple-of-4 dimensions are padded upward to full 4x4 blocks.
    /// </summary>
    /// <param name="width">Image width in pixels (must be positive).</param>
    /// <param name="height">Image height in pixels (must be positive).</param>
    /// <param name="format">Target block compression format.</param>
    /// <returns>The total compressed size in bytes.</returns>
    public static long GetCompressedSize(int width, int height, DxtFormat format)
    {
        Guard.ThrowIfNegativeOrZero(width, nameof(width));
        Guard.ThrowIfNegativeOrZero(height, nameof(height));
        int blockBytes = GetBlockSizeBytes(format);
        long blocksX = ((long)width + 3L) >> 2;
        long blocksY = ((long)height + 3L) >> 2;
        return checked(blocksX * blocksY * blockBytes);
    }

    /// <summary>
    /// Compresses a 2D image into caller-owned compressed block memory (single-threaded span overload).
    /// Edge blocks on non-multiple-of-4 images use clamp-to-edge padding.
    /// </summary>
    /// <param name="sourcePixels">Source image pixels (RGBA for BC1/BC3, R for BC4, RG for BC5).</param>
    /// <param name="width">Image width in pixels.</param>
    /// <param name="height">Image height in pixels.</param>
    /// <param name="sourceStrideBytes">Source row stride in bytes (<c>0</c> uses tightly packed <c>width * channels</c>).</param>
    /// <param name="destination">Destination buffer for compressed blocks.</param>
    /// <param name="format">Target block compression format.</param>
    /// <param name="mode">Compression quality mode (used for BC1/BC3).</param>
    /// <param name="options">Optional execution limits.</param>
    public static void CompressImage(
        ReadOnlySpan<byte> sourcePixels,
        int width,
        int height,
        int sourceStrideBytes,
        Span<byte> destination,
        DxtFormat format,
        DxtCompressionMode mode = DxtCompressionMode.Normal,
        StbOptions? options = null)
    {
        ValidateImageCompressionArgs(
            sourcePixels.Length,
            width,
            height,
            sourceStrideBytes,
            destination.Length,
            format,
            options,
            out int channels,
            out int stride,
            out int blocksX,
            out int blocksY,
            out int blockBytes);

        if (DxtGpu.TryCompress(sourcePixels, width, height, stride, destination, format, mode))
        {
            return;
        }

        for (int by = 0; by < blocksY; ++by)
        {
            CompressBlockRow(sourcePixels, width, height, stride, channels, destination, by, blocksX, blockBytes, format, mode);
        }
    }

    /// <summary>
    /// Compresses a 2D image into caller-owned compressed block memory with optional deterministic multithreading
    /// controlled by <see cref="StbOptions.MaxDegreeOfParallelism"/>.
    /// </summary>
    /// <param name="sourcePixels">Source image pixels (RGBA for BC1/BC3, R for BC4, RG for BC5).</param>
    /// <param name="width">Image width in pixels.</param>
    /// <param name="height">Image height in pixels.</param>
    /// <param name="sourceStrideBytes">Source row stride in bytes (<c>0</c> uses tightly packed <c>width * channels</c>).</param>
    /// <param name="destination">Destination buffer for compressed blocks.</param>
    /// <param name="format">Target block compression format.</param>
    /// <param name="mode">Compression quality mode (used for BC1/BC3).</param>
    /// <param name="options">Optional execution limits and parallelism configuration.</param>
    public static void CompressImageMemory(
        ReadOnlyMemory<byte> sourcePixels,
        int width,
        int height,
        int sourceStrideBytes,
        Memory<byte> destination,
        DxtFormat format,
        DxtCompressionMode mode = DxtCompressionMode.Normal,
        StbOptions? options = null)
    {
        StbOptions resolvedOptions = options ?? StbOptions.Default;
        ValidateImageCompressionArgs(
            sourcePixels.Length,
            width,
            height,
            sourceStrideBytes,
            destination.Length,
            format,
            resolvedOptions,
            out int channels,
            out int stride,
            out int blocksX,
            out int blocksY,
            out int blockBytes);

        if (DxtGpu.TryCompress(sourcePixels.Span, width, height, stride, destination.Span, format, mode))
        {
            return;
        }

        int dop = resolvedOptions.ResolveDegreeOfParallelism(blocksY, ParallelBlockRowThreshold);
        if (dop > 1 && blocksY >= ParallelBlockRowThreshold && (long)blocksX * blocksY >= 256)
        {
            Parallel.For(0, blocksY, new ParallelOptions { MaxDegreeOfParallelism = dop }, by =>
            {
                CompressBlockRow(
                    sourcePixels.Span,
                    width,
                    height,
                    stride,
                    channels,
                    destination.Span,
                    by,
                    blocksX,
                    blockBytes,
                    format,
                    mode);
            });
            return;
        }

        ReadOnlySpan<byte> srcSpan = sourcePixels.Span;
        Span<byte> dstSpan = destination.Span;
        for (int by = 0; by < blocksY; ++by)
        {
            CompressBlockRow(srcSpan, width, height, stride, channels, dstSpan, by, blocksX, blockBytes, format, mode);
        }
    }

    /// <summary>
    /// Compresses a 2D image and returns a newly allocated byte array of compressed blocks.
    /// </summary>
    /// <param name="sourcePixels">Source image pixels (RGBA for BC1/BC3, R for BC4, RG for BC5).</param>
    /// <param name="width">Image width in pixels.</param>
    /// <param name="height">Image height in pixels.</param>
    /// <param name="format">Target block compression format.</param>
    /// <param name="sourceStrideBytes">Source row stride in bytes (<c>0</c> uses tightly packed <c>width * channels</c>).</param>
    /// <param name="mode">Compression quality mode (used for BC1/BC3).</param>
    /// <param name="options">Optional execution limits and parallelism configuration.</param>
    /// <returns>The compressed block buffer.</returns>
    public static byte[] CompressImageToArray(
        ReadOnlyMemory<byte> sourcePixels,
        int width,
        int height,
        DxtFormat format,
        int sourceStrideBytes = 0,
        DxtCompressionMode mode = DxtCompressionMode.Normal,
        StbOptions? options = null)
    {
        StbOptions resolvedOptions = options ?? StbOptions.Default;
        int channels = GetSourceChannels(format);
        _ = resolvedOptions.Limits.ValidateDimensions(width, height, channels);
        long outBytes = GetCompressedSize(width, height, format);
        if (outBytes > resolvedOptions.Limits.MaxAllocationBytes || outBytes > ArrayAllocation.MaxByteArrayLength)
        {
            throw new StbException(
                StbErrorCode.LimitExceeded,
                $"Compressed output ({outBytes} bytes) exceeds MaxAllocationBytes ({resolvedOptions.Limits.MaxAllocationBytes}).");
        }

        byte[] output = ArrayAllocation.AllocateUninitialized<byte>((int)outBytes);
        CompressImageMemory(sourcePixels, width, height, sourceStrideBytes, output, format, mode, resolvedOptions);
        return output;
    }

    private static void CompressBlockRow(
        ReadOnlySpan<byte> srcPixels,
        int width,
        int height,
        int stride,
        int channels,
        Span<byte> dst,
        int by,
        int blocksX,
        int blockBytes,
        DxtFormat format,
        DxtCompressionMode mode)
    {
        Span<byte> blockBuf = stackalloc byte[64];
        int y0 = by * 4;
        int rowDstBase = by * blocksX * blockBytes;
        int copyBytesPerRow = 4 * channels;
        bool fullHeight = y0 + 3 < height;

        for (int bx = 0; bx < blocksX; ++bx)
        {
            int x0 = bx * 4;
            if (fullHeight && x0 + 3 < width)
            {
                // Fast interior block gather: 4 contiguous rows of 4*channels bytes.
                int srcOff = y0 * stride + x0 * channels;
                srcPixels.Slice(srcOff, copyBytesPerRow).CopyTo(blockBuf);
                srcPixels.Slice(srcOff + stride, copyBytesPerRow).CopyTo(blockBuf.Slice(copyBytesPerRow));
                srcPixels.Slice(srcOff + stride * 2, copyBytesPerRow).CopyTo(blockBuf.Slice(copyBytesPerRow * 2));
                srcPixels.Slice(srcOff + stride * 3, copyBytesPerRow).CopyTo(blockBuf.Slice(copyBytesPerRow * 3));
            }
            else
            {
                // Clamp-to-edge gather for partial boundary blocks.
                for (int py = 0; py < 4; ++py)
                {
                    int sy = y0 + py;
                    if (sy >= height)
                    {
                        sy = height - 1;
                    }

                    int rowOff = sy * stride;
                    int dstRow = py * copyBytesPerRow;
                    for (int px = 0; px < 4; ++px)
                    {
                        int sx = x0 + px;
                        if (sx >= width)
                        {
                            sx = width - 1;
                        }

                        int sOff = rowOff + sx * channels;
                        int dOff = dstRow + px * channels;
                        for (int c = 0; c < channels; ++c)
                        {
                            blockBuf[dOff + c] = srcPixels[sOff + c];
                        }
                    }
                }
            }

            Span<byte> blockDst = dst.Slice(rowDstBase + bx * blockBytes, blockBytes);
            switch (format)
            {
                case DxtFormat.Bc1:
                    DxtBlockEncoder.CompressDxtBlock(blockDst, blockBuf, includeAlpha: false, mode);
                    break;
                case DxtFormat.Bc3:
                    DxtBlockEncoder.CompressDxtBlock(blockDst, blockBuf, includeAlpha: true, mode);
                    break;
                case DxtFormat.Bc4:
                    DxtBlockEncoder.CompressBc4Block(blockDst, blockBuf);
                    break;
                case DxtFormat.Bc5:
                    DxtBlockEncoder.CompressBc5Block(blockDst, blockBuf);
                    break;
            }
        }
    }

    private static int GetSourceChannels(DxtFormat format) => format switch
    {
        DxtFormat.Bc1 or DxtFormat.Bc3 => 4,
        DxtFormat.Bc4 => 1,
        DxtFormat.Bc5 => 2,
        _ => throw new ArgumentOutOfRangeException(nameof(format), format, "Unsupported DXT format."),
    };

    private static int GetBlockSizeBytes(DxtFormat format) => format switch
    {
        DxtFormat.Bc1 or DxtFormat.Bc4 => 8,
        DxtFormat.Bc3 or DxtFormat.Bc5 => 16,
        _ => throw new ArgumentOutOfRangeException(nameof(format), format, "Unsupported DXT format."),
    };

    private static void ValidateImageCompressionArgs(
        int srcLength,
        int width,
        int height,
        int sourceStrideBytes,
        int dstLength,
        DxtFormat format,
        StbOptions? options,
        out int channels,
        out int stride,
        out int blocksX,
        out int blocksY,
        out int blockBytes,
        string sourceParamName = "sourcePixels",
        string destParamName = "destination")
    {
        StbOptions resolvedOptions = options ?? StbOptions.Default;
        channels = GetSourceChannels(format);
        blockBytes = GetBlockSizeBytes(format);
        _ = resolvedOptions.Limits.ValidateDimensions(width, height, channels);

        int minStride = checked(width * channels);
        stride = sourceStrideBytes == 0 ? minStride : sourceStrideBytes;
        Guard.ThrowIfLessThan(stride, minStride, nameof(sourceStrideBytes));

        long requiredSrc = (long)(height - 1) * stride + minStride;
        if (srcLength < requiredSrc)
        {
            throw new ArgumentException(
                $"Source pixel buffer ({srcLength} bytes) is smaller than required ({requiredSrc} bytes).",
                sourceParamName);
        }

        blocksX = (width + 3) >> 2;
        blocksY = (height + 3) >> 2;
        long requiredDst = (long)blocksX * blocksY * blockBytes;
        if (dstLength < requiredDst)
        {
            throw new ArgumentException(
                $"Destination buffer ({dstLength} bytes) is smaller than required ({requiredDst} bytes).",
                destParamName);
        }
    }
}
