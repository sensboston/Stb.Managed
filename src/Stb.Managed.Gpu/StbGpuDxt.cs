using Stb.Managed.Dxt;
using Stb.Managed.Gpu.Internal;
using Stb.Managed.Gpu.Kernels;

namespace Stb.Managed.Gpu;

/// <summary>
/// GPU-accelerated BC1 / BC3 / BC4 / BC5 (DXT) texture compressor backed by ILGPU,
/// producing bit-exact output matching <see cref="StbDxt"/> and native C <c>stb_dxt.h</c> v1.13
/// with automatic CPU fallback for small textures or CPU-only machines.
/// </summary>
public static class StbGpuDxt
{
    /// <summary>
    /// Compresses a 2D image into caller-provided <paramref name="destination"/>, dispatching to GPU
    /// when profitable according to <paramref name="context"/> or falling back to <see cref="StbDxt"/>.
    /// </summary>
    /// <param name="context">GPU context owning the accelerator and pooled VRAM buffers.</param>
    /// <param name="sourcePixels">Source pixels (RGBA for BC1/BC3, R for BC4, RG for BC5).</param>
    /// <param name="width">Image width in pixels.</param>
    /// <param name="height">Image height in pixels.</param>
    /// <param name="destination">Destination buffer (at least <see cref="StbDxt.GetCompressedSize"/> bytes).</param>
    /// <param name="format">Target block compression format.</param>
    /// <param name="sourceStrideBytes">Source row stride in bytes (<c>0</c> = tightly packed <c>width * channels</c>).</param>
    /// <param name="mode">Compression quality mode for BC1/BC3.</param>
    /// <param name="modeOverride">Optional per-call execution mode override.</param>
    public static void CompressImage(
        StbGpuContext context,
        ReadOnlyMemory<byte> sourcePixels,
        int width,
        int height,
        Memory<byte> destination,
        DxtFormat format,
        int sourceStrideBytes = 0,
        DxtCompressionMode mode = DxtCompressionMode.Normal,
        StbGpuExecutionMode? modeOverride = null)
    {
        ArgumentNullException.ThrowIfNull(context);
        ValidateCommon(width, height, format, sourceStrideBytes, sourcePixels.Length, destination.Length, out int channels, out int stride, out int blocksX, out int blocksY, out long outBytes);

        long totalBlocks = (long)blocksX * blocksY;
        if (!context.ShouldUseGpu(totalBlocks, context.Thresholds.DxtMinBlocks, modeOverride))
        {
            context.RecordCpuFallback();
            StbDxt.CompressImageMemory(sourcePixels, width, height, stride, destination, format, mode, context.CpuOptions);
            return;
        }

        CompressOnGpu(context, sourcePixels.Span, width, height, stride, channels, blocksX, blocksY, outBytes, destination.Span, format, mode);
    }

    /// <summary>Compresses on the GPU without size checks (arguments already validated).</summary>
    internal static void CompressOnGpu(
        StbGpuContext context,
        ReadOnlySpan<byte> sourcePixels,
        int width,
        int height,
        int stride,
        int channels,
        int blocksX,
        int blocksY,
        long outBytes,
        Span<byte> destination,
        DxtFormat format,
        DxtCompressionMode mode)
    {
        long totalBlocks = (long)blocksX * blocksY;
        long srcBytes = (long)(height - 1) * stride + (long)width * channels;
        using PooledGpuBuffer<byte> srcBuf = context.Pool.RentByte(srcBytes);
        using PooledGpuBuffer<byte> dstBuf = context.Pool.RentByte(outBytes);

        var p = new DxtImageParams(
            width, height, stride, blocksX, blocksY,
            (int)srcBytes, (int)outBytes,
            includeAlpha: format == DxtFormat.Bc3 ? 1 : 0,
            highQuality: (mode & DxtCompressionMode.HighQuality) != 0 ? 1 : 0);

        lock (context.SyncRoot)
        {
            context.UploadSpanLocked(srcBuf.View, sourcePixels.Slice(0, (int)srcBytes));
            DispatchDxtLocked(context, srcBuf.View, dstBuf.View, (int)totalBlocks, format, p);
            context.Accelerator.Synchronize();
        }

        context.RecordGpuDispatch();
        context.DownloadSpan(dstBuf.View, destination.Slice(0, (int)outBytes));
    }

    /// <summary>
    /// Compresses a 2D image and returns a newly allocated byte array of compressed blocks.
    /// </summary>
    public static byte[] CompressImageToArray(
        StbGpuContext context,
        ReadOnlyMemory<byte> sourcePixels,
        int width,
        int height,
        DxtFormat format,
        int sourceStrideBytes = 0,
        DxtCompressionMode mode = DxtCompressionMode.Normal,
        StbGpuExecutionMode? modeOverride = null)
    {
        ArgumentNullException.ThrowIfNull(context);
        long outBytes = StbDxt.GetCompressedSize(width, height, format);
        byte[] output = GC.AllocateUninitializedArray<byte>((int)outBytes);
        CompressImage(context, sourcePixels, width, height, output, format, sourceStrideBytes, mode, modeOverride);
        return output;
    }

    /// <summary>
    /// Compresses a VRAM-resident <see cref="StbGpuImage{T}"/> directly on GPU without re-uploading source pixels.
    /// </summary>
    /// <param name="context">GPU context owning <paramref name="source"/>.</param>
    /// <param name="source">VRAM-resident image whose channel count matches <paramref name="format"/>.</param>
    /// <param name="format">Target block compression format.</param>
    /// <param name="mode">Compression quality mode for BC1/BC3.</param>
    /// <returns>Host array containing the compressed DXT blocks.</returns>
    public static byte[] CompressGpuImage(
        StbGpuContext context,
        StbGpuImage<byte> source,
        DxtFormat format,
        DxtCompressionMode mode = DxtCompressionMode.Normal)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(source);

        int expectedChannels = GetSourceChannels(format);
        if (source.ChannelCount != expectedChannels)
        {
            throw new StbException(
                StbErrorCode.InvalidArgument,
                $"Format {format} requires {expectedChannels} source channel(s), got {source.ChannelCount}.");
        }

        int width = source.Width;
        int height = source.Height;
        int stride = width * expectedChannels;
        int blocksX = (width + 3) >> 2;
        int blocksY = (height + 3) >> 2;
        int totalBlocks = blocksX * blocksY;
        long outBytes = StbDxt.GetCompressedSize(width, height, format);

        using PooledGpuBuffer<byte> dstBuf = context.Pool.RentByte(outBytes);
        var p = new DxtImageParams(
            width, height, stride, blocksX, blocksY,
            (int)source.ElementCount, (int)outBytes,
            includeAlpha: format == DxtFormat.Bc3 ? 1 : 0,
            highQuality: (mode & DxtCompressionMode.HighQuality) != 0 ? 1 : 0);

        lock (context.SyncRoot)
        {
            DispatchDxtLocked(context, source.View, dstBuf.View, totalBlocks, format, p);
            context.Accelerator.Synchronize();
        }

        context.RecordGpuDispatch();
        return context.DownloadArray(dstBuf.View, outBytes);
    }

    /// <summary>
    /// Compresses a batch of equal-sized textures in a single batched GPU upload, kernel dispatch, and download.
    /// </summary>
    /// <param name="context">GPU context.</param>
    /// <param name="images">List of tightly packed source images of dimensions <paramref name="width"/>×<paramref name="height"/>.</param>
    /// <param name="width">Width of every image in pixels.</param>
    /// <param name="height">Height of every image in pixels.</param>
    /// <param name="format">Target block compression format.</param>
    /// <param name="mode">Compression quality mode for BC1/BC3.</param>
    /// <param name="modeOverride">Optional per-call execution mode override.</param>
    /// <returns>Array of compressed block buffers, one per input image.</returns>
    public static byte[][] CompressBatch(
        StbGpuContext context,
        IReadOnlyList<ReadOnlyMemory<byte>> images,
        int width,
        int height,
        DxtFormat format,
        DxtCompressionMode mode = DxtCompressionMode.Normal,
        StbGpuExecutionMode? modeOverride = null)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(images);

        int count = images.Count;
        if (count == 0)
        {
            return [];
        }

        int channels = GetSourceChannels(format);
        int stride = width * channels;
        long srcImgBytes = (long)height * stride;
        long dstImgBytes = StbDxt.GetCompressedSize(width, height, format);
        int blocksX = (width + 3) >> 2;
        int blocksY = (height + 3) >> 2;
        long totalBlocks = (long)count * blocksX * blocksY;

        for (int i = 0; i < count; ++i)
        {
            if (images[i].Length < srcImgBytes)
            {
                throw new ArgumentException($"Batch image [{i}] is too small ({images[i].Length} < {srcImgBytes}).", nameof(images));
            }
        }

        if (!context.ShouldUseGpu(totalBlocks, context.Thresholds.DxtMinBlocks, modeOverride))
        {
            context.RecordCpuFallback();
            byte[][] cpuResults = new byte[count][];
            for (int i = 0; i < count; ++i)
            {
                cpuResults[i] = StbDxt.CompressImageToArray(images[i], width, height, format, stride, mode, context.CpuOptions);
            }

            return cpuResults;
        }

        long totalSrcBytes = srcImgBytes * count;
        long totalDstBytes = dstImgBytes * count;

        using PooledGpuBuffer<byte> srcBuf = context.Pool.RentByte(totalSrcBytes);
        using PooledGpuBuffer<byte> dstBuf = context.Pool.RentByte(totalDstBytes);

        var p = new DxtImageParams(
            width, height, stride, blocksX, blocksY,
            (int)srcImgBytes, (int)dstImgBytes,
            includeAlpha: format == DxtFormat.Bc3 ? 1 : 0,
            highQuality: (mode & DxtCompressionMode.HighQuality) != 0 ? 1 : 0);

        lock (context.SyncRoot)
        {
            for (int i = 0; i < count; ++i)
            {
                context.UploadMemoryLocked(srcBuf.View.SubView(i * srcImgBytes, srcImgBytes), images[i], srcImgBytes);
            }

            DispatchDxtLocked(context, srcBuf.View, dstBuf.View, (int)totalBlocks, format, p);
            context.Accelerator.Synchronize();
        }

        context.RecordGpuDispatch();

        byte[] combined = context.DownloadArray(dstBuf.View, totalDstBytes);
        byte[][] results = new byte[count][];
        for (int i = 0; i < count; ++i)
        {
            byte[] slice = GC.AllocateUninitializedArray<byte>((int)dstImgBytes);
            Buffer.BlockCopy(combined, (int)(i * dstImgBytes), slice, 0, (int)dstImgBytes);
            results[i] = slice;
        }

        return results;
    }

    private static void DispatchDxtLocked(
        StbGpuContext context,
        ILGPU.Runtime.ArrayView1D<byte, ILGPU.Stride1D.Dense> srcView,
        ILGPU.Runtime.ArrayView1D<byte, ILGPU.Stride1D.Dense> dstView,
        int totalBlocks,
        DxtFormat format,
        DxtImageParams p)
    {
        switch (format)
        {
            case DxtFormat.Bc1:
            case DxtFormat.Bc3:
            {
                context.GetDxtTablesLocked(out var om5, out var om6, out var mp5, out var mp6);
                var kernel = context.GetDxtRgbaKernelLocked();
                kernel(totalBlocks, srcView, dstView, om5, om6, mp5, mp6, p);
                break;
            }
            case DxtFormat.Bc4:
            {
                var kernel = context.GetDxtBc4KernelLocked();
                kernel(totalBlocks, srcView, dstView, p);
                break;
            }
            case DxtFormat.Bc5:
            {
                var kernel = context.GetDxtBc5KernelLocked();
                kernel(totalBlocks, srcView, dstView, p);
                break;
            }
            default:
                throw new StbException(StbErrorCode.UnsupportedFormat, $"Unsupported DXT format '{format}'.");
        }
    }

    private static int GetSourceChannels(DxtFormat format) => format switch
    {
        DxtFormat.Bc1 or DxtFormat.Bc3 => 4,
        DxtFormat.Bc4 => 1,
        DxtFormat.Bc5 => 2,
        _ => throw new StbException(StbErrorCode.UnsupportedFormat, $"Unsupported DXT format '{format}'."),
    };

    internal static void ValidateCommon(
        int width,
        int height,
        DxtFormat format,
        int sourceStrideBytes,
        int sourceLength,
        int destLength,
        out int channels,
        out int stride,
        out int blocksX,
        out int blocksY,
        out long outBytes)
    {
        if (width <= 0 || height <= 0)
        {
            throw new StbException(StbErrorCode.InvalidArgument, "Image dimensions must be positive.");
        }

        channels = GetSourceChannels(format);
        int minStride = width * channels;
        stride = sourceStrideBytes == 0 ? minStride : sourceStrideBytes;
        if (stride < minStride)
        {
            throw new StbException(StbErrorCode.InvalidArgument, $"Source stride ({stride}) is smaller than width * channels ({minStride}).");
        }

        long requiredSrc = (long)(height - 1) * stride + minStride;
        if (sourceLength < requiredSrc)
        {
            throw new StbException(StbErrorCode.InvalidArgument, $"Source pixel buffer ({sourceLength} bytes) is too small (need {requiredSrc}).");
        }

        blocksX = (width + 3) >> 2;
        blocksY = (height + 3) >> 2;
        outBytes = StbDxt.GetCompressedSize(width, height, format);
        if (destLength < outBytes)
        {
            throw new StbException(StbErrorCode.InvalidArgument, $"Destination buffer ({destLength} bytes) is too small (need {outBytes}).");
        }
    }
}
