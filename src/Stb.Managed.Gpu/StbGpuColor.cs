using Stb.Managed.Gpu.Internal;
using Stb.Managed.Internal;

namespace Stb.Managed.Gpu;

/// <summary>
/// GPU-accelerated batch and VRAM-resident color space and channel layout conversions
/// (RGBA↔BGRA, RGB↔RGBA, RGBA↔Grey, premultiply/unpremultiply alpha, sRGB↔Linear Fp32,
/// and float heightmap visualization), with automatic CPU fallback.
/// </summary>
public static class StbGpuColor
{
    /// <summary>
    /// Swaps R and B channels (RGBA ↔ BGRA) on an 8-bit 4-channel buffer.
    /// </summary>
    public static void SwapRedBlueRgba(
        StbGpuContext context,
        ReadOnlyMemory<byte> sourceRgba,
        Memory<byte> destinationBgra,
        int pixelCount,
        StbGpuExecutionMode? modeOverride = null)
    {
        ArgumentNullException.ThrowIfNull(context);
        ValidateCount(pixelCount, sourceRgba.Length, 4, destinationBgra.Length, 4);

        if (!context.ShouldUseGpu(pixelCount, context.Thresholds.ColorConvertMinPixels, modeOverride))
        {
            context.RecordCpuFallback();
            ReadOnlySpan<byte> src = sourceRgba.Span;
            Span<byte> dst = destinationBgra.Span;
            for (int i = 0; i < pixelCount; ++i)
            {
                int o = i * 4;
                byte r = src[o];
                byte g = src[o + 1];
                byte b = src[o + 2];
                byte a = src[o + 3];
                dst[o] = b;
                dst[o + 1] = g;
                dst[o + 2] = r;
                dst[o + 3] = a;
            }

            return;
        }

        long bytes = (long)pixelCount * 4;
        using PooledGpuBuffer<byte> srcBuf = context.Pool.RentByte(bytes);
        using PooledGpuBuffer<byte> dstBuf = context.Pool.RentByte(bytes);
        lock (context.SyncRoot)
        {
            context.UploadMemoryLocked(srcBuf.View, sourceRgba, bytes);
            var kernel = context.GetSwapRBKernelLocked();
            kernel(pixelCount, srcBuf.View, dstBuf.View);
            context.Accelerator.Synchronize();
        }

        context.RecordGpuDispatch();
        context.DownloadSpan(dstBuf.View, destinationBgra.Span.Slice(0, (int)bytes));
    }

    /// <summary>
    /// Swaps R and B channels directly in VRAM, returning a new <see cref="StbGpuImage{T}"/>.
    /// </summary>
    public static StbGpuImage<byte> SwapRedBlueGpuImage(StbGpuContext context, StbGpuImage<byte> source)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(source);
        if (source.ChannelCount != 4)
        {
            throw new StbException(StbErrorCode.InvalidArgument, "SwapRedBlueGpuImage requires a 4-channel image.");
        }

        int pixelCount = source.Width * source.Height;
        PooledGpuBuffer<byte> dstBuf = context.Pool.RentByte(source.ElementCount);
        try
        {
            lock (context.SyncRoot)
            {
                var kernel = context.GetSwapRBKernelLocked();
                kernel(pixelCount, source.View, dstBuf.View);
                context.Accelerator.Synchronize();
            }

            context.RecordGpuDispatch();
            return new StbGpuImage<byte>(context, dstBuf, source.Width, source.Height, ColorComponents.RedGreenBlueAlpha);
        }
        catch
        {
            dstBuf.Dispose();
            throw;
        }
    }

    /// <summary>
    /// Expands 3-channel RGB8 pixels to 4-channel RGBA8 pixels with constant <paramref name="alpha"/>.
    /// </summary>
    public static void RgbToRgba(
        StbGpuContext context,
        ReadOnlyMemory<byte> sourceRgb,
        Memory<byte> destinationRgba,
        int pixelCount,
        byte alpha = 255,
        StbGpuExecutionMode? modeOverride = null)
    {
        ArgumentNullException.ThrowIfNull(context);
        ValidateCount(pixelCount, sourceRgb.Length, 3, destinationRgba.Length, 4);

        if (!context.ShouldUseGpu(pixelCount, context.Thresholds.ColorConvertMinPixels, modeOverride))
        {
            context.RecordCpuFallback();
            ReadOnlySpan<byte> src = sourceRgb.Span;
            Span<byte> dst = destinationRgba.Span;
            for (int i = 0; i < pixelCount; ++i)
            {
                int s = i * 3;
                int d = i * 4;
                dst[d] = src[s];
                dst[d + 1] = src[s + 1];
                dst[d + 2] = src[s + 2];
                dst[d + 3] = alpha;
            }

            return;
        }

        long srcBytes = (long)pixelCount * 3;
        long dstBytes = (long)pixelCount * 4;
        using PooledGpuBuffer<byte> srcBuf = context.Pool.RentByte(srcBytes);
        using PooledGpuBuffer<byte> dstBuf = context.Pool.RentByte(dstBytes);
        lock (context.SyncRoot)
        {
            context.UploadMemoryLocked(srcBuf.View, sourceRgb, srcBytes);
            var kernel = context.GetRgbToRgbaKernelLocked();
            kernel(pixelCount, srcBuf.View, dstBuf.View, alpha);
            context.Accelerator.Synchronize();
        }

        context.RecordGpuDispatch();
        context.DownloadSpan(dstBuf.View, destinationRgba.Span.Slice(0, (int)dstBytes));
    }

    /// <summary>
    /// Strips the alpha channel from 4-channel RGBA8 pixels into 3-channel RGB8 pixels.
    /// </summary>
    public static void RgbaToRgb(
        StbGpuContext context,
        ReadOnlyMemory<byte> sourceRgba,
        Memory<byte> destinationRgb,
        int pixelCount,
        StbGpuExecutionMode? modeOverride = null)
    {
        ArgumentNullException.ThrowIfNull(context);
        ValidateCount(pixelCount, sourceRgba.Length, 4, destinationRgb.Length, 3);

        if (!context.ShouldUseGpu(pixelCount, context.Thresholds.ColorConvertMinPixels, modeOverride))
        {
            context.RecordCpuFallback();
            ReadOnlySpan<byte> src = sourceRgba.Span;
            Span<byte> dst = destinationRgb.Span;
            for (int i = 0; i < pixelCount; ++i)
            {
                int s = i * 4;
                int d = i * 3;
                dst[d] = src[s];
                dst[d + 1] = src[s + 1];
                dst[d + 2] = src[s + 2];
            }

            return;
        }

        long srcBytes = (long)pixelCount * 4;
        long dstBytes = (long)pixelCount * 3;
        using PooledGpuBuffer<byte> srcBuf = context.Pool.RentByte(srcBytes);
        using PooledGpuBuffer<byte> dstBuf = context.Pool.RentByte(dstBytes);
        lock (context.SyncRoot)
        {
            context.UploadMemoryLocked(srcBuf.View, sourceRgba, srcBytes);
            var kernel = context.GetRgbaToRgbKernelLocked();
            kernel(pixelCount, srcBuf.View, dstBuf.View);
            context.Accelerator.Synchronize();
        }

        context.RecordGpuDispatch();
        context.DownloadSpan(dstBuf.View, destinationRgb.Span.Slice(0, (int)dstBytes));
    }

    /// <summary>
    /// Converts RGBA8 pixels to 8-bit greyscale using <c>stb_image</c>'s exact integer Rec.601 luma formula.
    /// </summary>
    public static void RgbaToGrey(
        StbGpuContext context,
        ReadOnlyMemory<byte> sourceRgba,
        Memory<byte> destinationGrey,
        int pixelCount,
        StbGpuExecutionMode? modeOverride = null)
    {
        ArgumentNullException.ThrowIfNull(context);
        ValidateCount(pixelCount, sourceRgba.Length, 4, destinationGrey.Length, 1);

        if (!context.ShouldUseGpu(pixelCount, context.Thresholds.ColorConvertMinPixels, modeOverride))
        {
            context.RecordCpuFallback();
            ReadOnlySpan<byte> src = sourceRgba.Span;
            Span<byte> dst = destinationGrey.Span;
            for (int i = 0; i < pixelCount; ++i)
            {
                int s = i * 4;
                dst[i] = (byte)((77 * src[s] + 150 * src[s + 1] + 29 * src[s + 2]) >> 8);
            }

            return;
        }

        long srcBytes = (long)pixelCount * 4;
        using PooledGpuBuffer<byte> srcBuf = context.Pool.RentByte(srcBytes);
        using PooledGpuBuffer<byte> dstBuf = context.Pool.RentByte(pixelCount);
        lock (context.SyncRoot)
        {
            context.UploadMemoryLocked(srcBuf.View, sourceRgba, srcBytes);
            var kernel = context.GetRgbaToGreyKernelLocked();
            kernel(pixelCount, srcBuf.View, dstBuf.View);
            context.Accelerator.Synchronize();
        }

        context.RecordGpuDispatch();
        context.DownloadSpan(dstBuf.View, destinationGrey.Span.Slice(0, pixelCount));
    }

    /// <summary>
    /// Expands 8-bit greyscale pixels to RGBA8 with constant <paramref name="alpha"/>.
    /// </summary>
    public static void GreyToRgba(
        StbGpuContext context,
        ReadOnlyMemory<byte> sourceGrey,
        Memory<byte> destinationRgba,
        int pixelCount,
        byte alpha = 255,
        StbGpuExecutionMode? modeOverride = null)
    {
        ArgumentNullException.ThrowIfNull(context);
        ValidateCount(pixelCount, sourceGrey.Length, 1, destinationRgba.Length, 4);

        if (!context.ShouldUseGpu(pixelCount, context.Thresholds.ColorConvertMinPixels, modeOverride))
        {
            context.RecordCpuFallback();
            ReadOnlySpan<byte> src = sourceGrey.Span;
            Span<byte> dst = destinationRgba.Span;
            for (int i = 0; i < pixelCount; ++i)
            {
                byte y = src[i];
                int d = i * 4;
                dst[d] = y;
                dst[d + 1] = y;
                dst[d + 2] = y;
                dst[d + 3] = alpha;
            }

            return;
        }

        long dstBytes = (long)pixelCount * 4;
        using PooledGpuBuffer<byte> srcBuf = context.Pool.RentByte(pixelCount);
        using PooledGpuBuffer<byte> dstBuf = context.Pool.RentByte(dstBytes);
        lock (context.SyncRoot)
        {
            context.UploadMemoryLocked(srcBuf.View, sourceGrey, pixelCount);
            var kernel = context.GetGreyToRgbaKernelLocked();
            kernel(pixelCount, srcBuf.View, dstBuf.View, alpha);
            context.Accelerator.Synchronize();
        }

        context.RecordGpuDispatch();
        context.DownloadSpan(dstBuf.View, destinationRgba.Span.Slice(0, (int)dstBytes));
    }

    /// <summary>
    /// Premultiplies RGB channels by alpha in RGBA8 pixels using exact rounded integer arithmetic.
    /// </summary>
    public static void PremultiplyAlpha(
        StbGpuContext context,
        ReadOnlyMemory<byte> sourceRgba,
        Memory<byte> destinationRgba,
        int pixelCount,
        StbGpuExecutionMode? modeOverride = null)
    {
        ArgumentNullException.ThrowIfNull(context);
        ValidateCount(pixelCount, sourceRgba.Length, 4, destinationRgba.Length, 4);

        if (!context.ShouldUseGpu(pixelCount, context.Thresholds.ColorConvertMinPixels, modeOverride))
        {
            context.RecordCpuFallback();
            ReadOnlySpan<byte> src = sourceRgba.Span;
            Span<byte> dst = destinationRgba.Span;
            for (int i = 0; i < pixelCount; ++i)
            {
                int o = i * 4;
                int a = src[o + 3];
                dst[o] = (byte)((src[o] * a + 127) / 255);
                dst[o + 1] = (byte)((src[o + 1] * a + 127) / 255);
                dst[o + 2] = (byte)((src[o + 2] * a + 127) / 255);
                dst[o + 3] = (byte)a;
            }

            return;
        }

        long bytes = (long)pixelCount * 4;
        using PooledGpuBuffer<byte> srcBuf = context.Pool.RentByte(bytes);
        using PooledGpuBuffer<byte> dstBuf = context.Pool.RentByte(bytes);
        lock (context.SyncRoot)
        {
            context.UploadMemoryLocked(srcBuf.View, sourceRgba, bytes);
            var kernel = context.GetPremulKernelLocked();
            kernel(pixelCount, srcBuf.View, dstBuf.View);
            context.Accelerator.Synchronize();
        }

        context.RecordGpuDispatch();
        context.DownloadSpan(dstBuf.View, destinationRgba.Span.Slice(0, (int)bytes));
    }

    /// <summary>
    /// Unpremultiplies RGB channels by alpha in RGBA8 pixels using exact rounded integer arithmetic.
    /// </summary>
    public static void UnpremultiplyAlpha(
        StbGpuContext context,
        ReadOnlyMemory<byte> sourceRgba,
        Memory<byte> destinationRgba,
        int pixelCount,
        StbGpuExecutionMode? modeOverride = null)
    {
        ArgumentNullException.ThrowIfNull(context);
        ValidateCount(pixelCount, sourceRgba.Length, 4, destinationRgba.Length, 4);

        if (!context.ShouldUseGpu(pixelCount, context.Thresholds.ColorConvertMinPixels, modeOverride))
        {
            context.RecordCpuFallback();
            ReadOnlySpan<byte> src = sourceRgba.Span;
            Span<byte> dst = destinationRgba.Span;
            for (int i = 0; i < pixelCount; ++i)
            {
                int o = i * 4;
                int a = src[o + 3];
                if (a == 0)
                {
                    dst[o] = 0;
                    dst[o + 1] = 0;
                    dst[o + 2] = 0;
                    dst[o + 3] = 0;
                }
                else
                {
                    int r = (src[o] * 255 + (a >> 1)) / a;
                    int g = (src[o + 1] * 255 + (a >> 1)) / a;
                    int b = (src[o + 2] * 255 + (a >> 1)) / a;
                    dst[o] = (byte)(r > 255 ? 255 : r);
                    dst[o + 1] = (byte)(g > 255 ? 255 : g);
                    dst[o + 2] = (byte)(b > 255 ? 255 : b);
                    dst[o + 3] = (byte)a;
                }
            }

            return;
        }

        long bytes = (long)pixelCount * 4;
        using PooledGpuBuffer<byte> srcBuf = context.Pool.RentByte(bytes);
        using PooledGpuBuffer<byte> dstBuf = context.Pool.RentByte(bytes);
        lock (context.SyncRoot)
        {
            context.UploadMemoryLocked(srcBuf.View, sourceRgba, bytes);
            var kernel = context.GetUnpremulKernelLocked();
            kernel(pixelCount, srcBuf.View, dstBuf.View);
            context.Accelerator.Synchronize();
        }

        context.RecordGpuDispatch();
        context.DownloadSpan(dstBuf.View, destinationRgba.Span.Slice(0, (int)bytes));
    }

    /// <summary>
    /// Converts 8-bit sRGB pixels to 32-bit linear floats using <c>stb_image_resize2</c>'s exact sRGB LUT.
    /// </summary>
    public static void SrgbToLinearFp32(
        StbGpuContext context,
        ReadOnlyMemory<byte> sourceSrgb,
        Memory<float> destinationLinear,
        int pixelCount,
        int channels = 4,
        bool linearAlpha = true,
        StbGpuExecutionMode? modeOverride = null)
    {
        ArgumentNullException.ThrowIfNull(context);
        ValidateCount(pixelCount, sourceSrgb.Length, channels, destinationLinear.Length, channels);
        int totalElems = pixelCount * channels;

        if (!context.ShouldUseGpu(pixelCount, context.Thresholds.ColorConvertMinPixels, modeOverride))
        {
            context.RecordCpuFallback();
            ReadOnlySpan<byte> src = sourceSrgb.Span;
            Span<float> dst = destinationLinear.Span;
            float[] lut = ResizeCoders.SrgbToLinear;
            for (int i = 0; i < totalElems; ++i)
            {
                dst[i] = (linearAlpha && channels == 4 && (i & 3) == 3)
                    ? src[i] * (1.0f / 255.0f)
                    : lut[src[i]];
            }

            return;
        }

        using PooledGpuBuffer<byte> srcBuf = context.Pool.RentByte(totalElems);
        using PooledGpuBuffer<float> dstBuf = context.Pool.RentFloat32(totalElems);
        lock (context.SyncRoot)
        {
            context.UploadMemoryLocked(srcBuf.View, sourceSrgb, totalElems);
            context.GetSrgbTablesLocked(out var srgbToLin, out _);
            var kernel = context.GetSrgbToLinearKernelLocked();
            kernel(totalElems, srcBuf.View, dstBuf.View, srgbToLin, channels, linearAlpha ? 1 : 0);
            context.Accelerator.Synchronize();
        }

        context.RecordGpuDispatch();
        context.DownloadSpan(dstBuf.View, destinationLinear.Span.Slice(0, totalElems));
    }

    /// <summary>
    /// Converts 32-bit linear floats to 8-bit sRGB using <c>stb_image_resize2</c>'s exact Fabian Giesen LUT.
    /// </summary>
    public static void LinearFp32ToSrgb(
        StbGpuContext context,
        ReadOnlyMemory<float> sourceLinear,
        Memory<byte> destinationSrgb,
        int pixelCount,
        int channels = 4,
        bool linearAlpha = true,
        StbGpuExecutionMode? modeOverride = null)
    {
        ArgumentNullException.ThrowIfNull(context);
        ValidateCount(pixelCount, sourceLinear.Length, channels, destinationSrgb.Length, channels);
        int totalElems = pixelCount * channels;

        if (!context.ShouldUseGpu(pixelCount, context.Thresholds.ColorConvertMinPixels, modeOverride))
        {
            context.RecordCpuFallback();
            ReadOnlySpan<float> src = sourceLinear.Span;
            Span<byte> dst = destinationSrgb.Span;
            for (int i = 0; i < totalElems; ++i)
            {
                float v = src[i];
                if (linearAlpha && channels == 4 && (i & 3) == 3)
                {
                    dst[i] = v <= 0.0f ? (byte)0 : v >= 1.0f ? (byte)255 : (byte)(v * 255.0f + 0.5f);
                }
                else
                {
                    dst[i] = ResizeCoders.LinearToSrgbByte(v);
                }
            }

            return;
        }

        using PooledGpuBuffer<float> srcBuf = context.Pool.RentFloat32(totalElems);
        using PooledGpuBuffer<byte> dstBuf = context.Pool.RentByte(totalElems);
        lock (context.SyncRoot)
        {
            context.UploadMemoryLocked(srcBuf.View, sourceLinear, totalElems);
            context.GetSrgbTablesLocked(out _, out var fp32ToSrgb);
            var kernel = context.GetLinearToSrgbKernelLocked();
            kernel(totalElems, srcBuf.View, dstBuf.View, fp32ToSrgb, channels, linearAlpha ? 1 : 0);
            context.Accelerator.Synchronize();
        }

        context.RecordGpuDispatch();
        context.DownloadSpan(dstBuf.View, destinationSrgb.Span.Slice(0, totalElems));
    }

    /// <summary>
    /// Converts a VRAM-resident 1-channel float heightmap directly into a VRAM-resident RGBA8 image (zero PCIe roundtrip).
    /// </summary>
    public static StbGpuImage<byte> FloatHeightmapToRgbaGpuImage(
        StbGpuContext context,
        StbGpuImage<float> heightmap,
        float minVal = -1.0f,
        float maxVal = 1.0f,
        byte alpha = 255)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(heightmap);
        if (heightmap.ChannelCount != 1)
        {
            throw new StbException(StbErrorCode.InvalidArgument, "FloatHeightmapToRgbaGpuImage requires a 1-channel float image.");
        }

        int pixelCount = heightmap.Width * heightmap.Height;
        float invRange = maxVal > minVal ? 1.0f / (maxVal - minVal) : 0.0f;
        PooledGpuBuffer<byte> dstBuf = context.Pool.RentByte((long)pixelCount * 4);
        try
        {
            lock (context.SyncRoot)
            {
                var kernel = context.GetHeightmapToRgbaKernelLocked();
                kernel(pixelCount, heightmap.View, dstBuf.View, minVal, invRange, alpha);
                context.Accelerator.Synchronize();
            }

            context.RecordGpuDispatch();
            return new StbGpuImage<byte>(context, dstBuf, heightmap.Width, heightmap.Height, ColorComponents.RedGreenBlueAlpha);
        }
        catch
        {
            dstBuf.Dispose();
            throw;
        }
    }

    /// <summary>
    /// Converts a VRAM-resident 1-channel float heightmap directly into a VRAM-resident 8-bit greyscale image.
    /// </summary>
    public static StbGpuImage<byte> FloatHeightmapToGreyGpuImage(
        StbGpuContext context,
        StbGpuImage<float> heightmap,
        float minVal = -1.0f,
        float maxVal = 1.0f)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(heightmap);
        if (heightmap.ChannelCount != 1)
        {
            throw new StbException(StbErrorCode.InvalidArgument, "FloatHeightmapToGreyGpuImage requires a 1-channel float image.");
        }

        int pixelCount = heightmap.Width * heightmap.Height;
        float invRange = maxVal > minVal ? 1.0f / (maxVal - minVal) : 0.0f;
        PooledGpuBuffer<byte> dstBuf = context.Pool.RentByte(pixelCount);
        try
        {
            lock (context.SyncRoot)
            {
                var kernel = context.GetHeightmapToGreyKernelLocked();
                kernel(pixelCount, heightmap.View, dstBuf.View, minVal, invRange);
                context.Accelerator.Synchronize();
            }

            context.RecordGpuDispatch();
            return new StbGpuImage<byte>(context, dstBuf, heightmap.Width, heightmap.Height, ColorComponents.Grey);
        }
        catch
        {
            dstBuf.Dispose();
            throw;
        }
    }

    private static void ValidateCount(int pixelCount, int srcLen, int srcCh, int dstLen, int dstCh)
    {
        if (pixelCount <= 0)
        {
            throw new StbException(StbErrorCode.InvalidArgument, "Pixel count must be positive.");
        }

        if (srcCh < 1 || srcCh > 4 || dstCh < 1 || dstCh > 4)
        {
            throw new StbException(StbErrorCode.InvalidArgument, "Channel count must be between 1 and 4.");
        }

        if (srcLen < (long)pixelCount * srcCh)
        {
            throw new StbException(StbErrorCode.InvalidArgument, $"Source buffer ({srcLen}) is too small for {pixelCount}×{srcCh}.");
        }

        if (dstLen < (long)pixelCount * dstCh)
        {
            throw new StbException(StbErrorCode.InvalidArgument, $"Destination buffer ({dstLen}) is too small for {pixelCount}×{dstCh}.");
        }
    }
}
