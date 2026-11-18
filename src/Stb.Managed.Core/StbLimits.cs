using Stb.Managed.Internal;

namespace Stb.Managed;

/// <summary>
/// Safety budgets that bound dimensions, pixel counts, memory allocations, decompression expansion,
/// and animation frame counts when processing untrusted input.
/// </summary>
public sealed record StbLimits
{
    /// <summary>
    /// Gets the default safety limits suitable for processing untrusted files.
    /// </summary>
    public static StbLimits Default { get; } = new();

    /// <summary>
    /// Gets limits constrained only by .NET array bounds (use only for trusted inputs).
    /// </summary>
    public static StbLimits Unlimited { get; } = new()
    {
        MaxWidth = int.MaxValue,
        MaxHeight = int.MaxValue,
        MaxPixels = long.MaxValue,
        MaxAllocationBytes = long.MaxValue,
        MaxDecompressedBytes = long.MaxValue,
        MaxFrames = int.MaxValue,
    };

    /// <summary>
    /// Gets the maximum permitted image width in pixels. Default is 65,536.
    /// </summary>
    public int MaxWidth { get; init; } = 65_536;

    /// <summary>
    /// Gets the maximum permitted image height in pixels. Default is 65,536.
    /// </summary>
    public int MaxHeight { get; init; } = 65_536;

    /// <summary>
    /// Gets the maximum permitted total pixel count (<c>width * height</c>). Default is 256,000,000.
    /// </summary>
    public long MaxPixels { get; init; } = 256_000_000;

    /// <summary>
    /// Gets the maximum permitted single buffer allocation in bytes. Default is 1 GiB.
    /// </summary>
    public long MaxAllocationBytes { get; init; } = 1L << 30;

    /// <summary>
    /// Gets the maximum permitted decompressed payload size in bytes. Default is 1 GiB.
    /// </summary>
    public long MaxDecompressedBytes { get; init; } = 1L << 30;

    /// <summary>
    /// Gets the maximum permitted number of frames in an animated image. Default is 10,000.
    /// </summary>
    public int MaxFrames { get; init; } = 10_000;

    /// <summary>
    /// Validates image dimensions and returns the checked total byte count for the pixel buffer.
    /// </summary>
    /// <param name="width">The image width in pixels.</param>
    /// <param name="height">The image height in pixels.</param>
    /// <param name="bytesPerPixel">The number of bytes per pixel (must be positive).</param>
    /// <returns>The total buffer size in bytes.</returns>
    /// <exception cref="StbException">
    /// Thrown with <see cref="StbErrorCode.InvalidData"/> if dimensions are non-positive,
    /// <see cref="StbErrorCode.InvalidArgument"/> if <paramref name="bytesPerPixel"/> is non-positive, or
    /// <see cref="StbErrorCode.LimitExceeded"/> if any configured limit or .NET array bound is exceeded.
    /// </exception>
    public int ValidateDimensions(int width, int height, int bytesPerPixel)
    {
        if (bytesPerPixel <= 0)
        {
            throw new StbException(
                StbErrorCode.InvalidArgument,
                $"Bytes per pixel must be positive, got {bytesPerPixel}.");
        }

        if (width <= 0 || height <= 0)
        {
            throw new StbException(
                StbErrorCode.InvalidData,
                $"Image dimensions must be positive, got {width}x{height}.");
        }

        if (width > MaxWidth || height > MaxHeight)
        {
            throw new StbException(
                StbErrorCode.LimitExceeded,
                $"Image dimensions {width}x{height} exceed configured limit {MaxWidth}x{MaxHeight}.");
        }

        long pixels = (long)width * height;
        if (pixels > MaxPixels)
        {
            throw new StbException(
                StbErrorCode.LimitExceeded,
                $"Image pixel count {pixels} exceeds configured limit {MaxPixels}.");
        }

        if (pixels > long.MaxValue / bytesPerPixel)
        {
            throw new StbException(
                StbErrorCode.LimitExceeded,
                $"Image buffer size for {width}x{height} ({bytesPerPixel} bytes/pixel) overflows 64-bit integer.");
        }

        long totalBytes = pixels * bytesPerPixel;
        ValidateAllocation(totalBytes);
        return (int)totalBytes;
    }

    /// <summary>
    /// Validates that a single allocation of <paramref name="byteCount"/> bytes is within
    /// <see cref="MaxAllocationBytes"/> and .NET's maximum array length.
    /// </summary>
    /// <param name="byteCount">The requested allocation size in bytes.</param>
    /// <exception cref="StbException">
    /// Thrown with <see cref="StbErrorCode.InvalidData"/> if <paramref name="byteCount"/> is negative, or
    /// <see cref="StbErrorCode.LimitExceeded"/> if the allocation exceeds limits.
    /// </exception>
    public void ValidateAllocation(long byteCount)
    {
        if (byteCount < 0)
        {
            throw new StbException(
                StbErrorCode.InvalidData,
                $"Allocation size cannot be negative ({byteCount}).");
        }

        if (byteCount > MaxAllocationBytes)
        {
            throw new StbException(
                StbErrorCode.LimitExceeded,
                $"Requested allocation of {byteCount} bytes exceeds configured limit {MaxAllocationBytes}.");
        }

        if (byteCount > ArrayAllocation.MaxByteArrayLength)
        {
            throw new StbException(
                StbErrorCode.LimitExceeded,
                $"Requested allocation of {byteCount} bytes exceeds .NET maximum array length ({ArrayAllocation.MaxByteArrayLength}).");
        }
    }

    /// <summary>
    /// Validates that a decompressed payload size is within <see cref="MaxDecompressedBytes"/>.
    /// </summary>
    /// <param name="byteCount">The decompressed payload size in bytes.</param>
    /// <exception cref="StbException">
    /// Thrown with <see cref="StbErrorCode.InvalidData"/> if <paramref name="byteCount"/> is negative, or
    /// <see cref="StbErrorCode.LimitExceeded"/> if <see cref="MaxDecompressedBytes"/> is exceeded.
    /// </exception>
    public void ValidateDecompressedSize(long byteCount)
    {
        if (byteCount < 0)
        {
            throw new StbException(
                StbErrorCode.InvalidData,
                $"Decompressed size cannot be negative ({byteCount}).");
        }

        if (byteCount > MaxDecompressedBytes)
        {
            throw new StbException(
                StbErrorCode.LimitExceeded,
                $"Decompressed payload size {byteCount} bytes exceeds configured limit {MaxDecompressedBytes}.");
        }
    }

    /// <summary>
    /// Validates that an animated image frame count is within <see cref="MaxFrames"/>.
    /// </summary>
    /// <param name="frameCount">The number of frames.</param>
    /// <exception cref="StbException">
    /// Thrown with <see cref="StbErrorCode.InvalidData"/> if <paramref name="frameCount"/> is negative, or
    /// <see cref="StbErrorCode.LimitExceeded"/> if <see cref="MaxFrames"/> is exceeded.
    /// </exception>
    public void ValidateFrameCount(int frameCount)
    {
        if (frameCount < 0)
        {
            throw new StbException(
                StbErrorCode.InvalidData,
                $"Frame count cannot be negative ({frameCount}).");
        }

        if (frameCount > MaxFrames)
        {
            throw new StbException(
                StbErrorCode.LimitExceeded,
                $"Frame count {frameCount} exceeds configured limit {MaxFrames}.");
        }
    }
}
