using System;
using Stb.Managed.Internal;

namespace Stb.Managed.ImageWrite.Internal;

internal static class WriteValidation
{
    internal static int ValidateByteImage(
        ReadOnlySpan<byte> pixels,
        int width,
        int height,
        ColorComponents components,
        int strideBytes,
        StbLimits limits,
        int maxFormatWidth = int.MaxValue / 4,
        int maxFormatHeight = int.MaxValue / 4)
    {
        int comp = ValidateCommon(width, height, components, 1, limits, maxFormatWidth, maxFormatHeight);
        int rowBytes = checked(width * comp);
        int effectiveStride = ResolveStride(strideBytes, rowBytes, nameof(strideBytes));

        long minRequired = ((long)(height - 1) * effectiveStride) + rowBytes;
        if (pixels.Length < minRequired)
        {
            throw new StbException(
                StbErrorCode.InvalidArgument,
                $"Pixel buffer length {pixels.Length} is smaller than required {minRequired} bytes for {width}x{height}x{comp} (stride {effectiveStride}).");
        }

        return effectiveStride;
    }

    internal static int ValidateFloatImage(
        ReadOnlySpan<float> pixels,
        int width,
        int height,
        ColorComponents components,
        int strideFloats,
        StbLimits limits)
    {
        int comp = ValidateCommon(width, height, components, sizeof(float), limits, int.MaxValue / 4, int.MaxValue / 4);
        int rowFloats = checked(width * comp);
        int effectiveStride = ResolveStride(strideFloats, rowFloats, nameof(strideFloats));

        long minRequired = ((long)(height - 1) * effectiveStride) + rowFloats;
        if (pixels.Length < minRequired)
        {
            throw new StbException(
                StbErrorCode.InvalidArgument,
                $"Float pixel buffer length {pixels.Length} is smaller than required {minRequired} samples for {width}x{height}x{comp} (stride {effectiveStride}).");
        }

        return effectiveStride;
    }

    internal static int ValidateJpegQuality(int quality)
    {
        if (quality < 1 || quality > 100)
        {
            throw new ArgumentOutOfRangeException(
                nameof(quality),
                quality,
                "JPEG quality must be between 1 and 100.");
        }

        return quality;
    }

    private static int ValidateCommon(
        int width,
        int height,
        ColorComponents components,
        int elementSize,
        StbLimits limits,
        int maxFormatWidth,
        int maxFormatHeight)
    {
        int comp = (int)components;
        if (comp < 1 || comp > 4)
        {
            throw new StbException(
                StbErrorCode.InvalidArgument,
                $"Components must be between Grey (1) and RedGreenBlueAlpha (4), got {components}.");
        }

        if (width <= 0 || height <= 0)
        {
            throw new StbException(
                StbErrorCode.InvalidArgument,
                $"Image dimensions must be positive, got {width}x{height}.");
        }

        if (width > maxFormatWidth || height > maxFormatHeight)
        {
            throw new StbException(
                StbErrorCode.LimitExceeded,
                $"Image dimensions {width}x{height} exceed format maximum {maxFormatWidth}x{maxFormatHeight}.");
        }

        // Enforce StbLimits (MaxWidth, MaxHeight, MaxPixels, MaxAllocationBytes, .NET array bounds).
        _ = limits.ValidateDimensions(width, height, checked(comp * elementSize));
        return comp;
    }

    private static int ResolveStride(int stride, int minRowUnits, string paramName)
    {
        if (stride == 0)
        {
            return minRowUnits;
        }

        if (stride < minRowUnits)
        {
            throw new StbException(
                StbErrorCode.InvalidArgument,
                $"Row stride ({paramName}={stride}) must be 0 or at least {minRowUnits}.");
        }

        return stride;
    }
}
