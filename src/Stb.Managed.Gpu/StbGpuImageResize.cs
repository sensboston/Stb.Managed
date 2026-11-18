using System.Runtime.InteropServices;
using ILGPU;
using ILGPU.Runtime;
using Stb.Managed.Dxt;
using Stb.Managed.Gpu.Internal;
using Stb.Managed.Gpu.Kernels;
using Stb.Managed.Internal;

namespace Stb.Managed.Gpu;

/// <summary>
/// Reusable VRAM-cached separable resampling plan storing precomputed horizontal and vertical
/// <c>stb_image_resize2</c> gather filter tables for zero-overhead repeated or batched GPU resizes.
/// </summary>
public sealed class StbGpuResizePlan : IDisposable
{
    private readonly StbGpuContext _context;
    private MemoryBuffer1D<int, Stride1D.Dense>? _hN0;
    private MemoryBuffer1D<int, Stride1D.Dense>? _hN1;
    private MemoryBuffer1D<float, Stride1D.Dense>? _hCoeffs;
    private MemoryBuffer1D<int, Stride1D.Dense>? _vN0;
    private MemoryBuffer1D<int, Stride1D.Dense>? _vN1;
    private MemoryBuffer1D<float, Stride1D.Dense>? _vCoeffs;
    private bool _disposed;

    private StbGpuResizePlan(
        StbGpuContext context,
        int inputWidth,
        int inputHeight,
        int outputWidth,
        int outputHeight,
        ResizePixelLayout layout,
        ResizeDataType dataType,
        ResizeFilter filter,
        ResizeEdge edge,
        ResizePassParams horizontalParams,
        ResizePassParams verticalParams,
        MemoryBuffer1D<int, Stride1D.Dense> hN0,
        MemoryBuffer1D<int, Stride1D.Dense> hN1,
        MemoryBuffer1D<float, Stride1D.Dense> hCoeffs,
        MemoryBuffer1D<int, Stride1D.Dense> vN0,
        MemoryBuffer1D<int, Stride1D.Dense> vN1,
        MemoryBuffer1D<float, Stride1D.Dense> vCoeffs)
    {
        _context = context;
        InputWidth = inputWidth;
        InputHeight = inputHeight;
        OutputWidth = outputWidth;
        OutputHeight = outputHeight;
        Layout = layout;
        DataType = dataType;
        Filter = filter;
        Edge = edge;
        HorizontalParams = horizontalParams;
        VerticalParams = verticalParams;
        _hN0 = hN0;
        _hN1 = hN1;
        _hCoeffs = hCoeffs;
        _vN0 = vN0;
        _vN1 = vN1;
        _vCoeffs = vCoeffs;
    }

    /// <summary>Gets the input image width in pixels.</summary>
    public int InputWidth { get; }

    /// <summary>Gets the input image height in pixels.</summary>
    public int InputHeight { get; }

    /// <summary>Gets the output image width in pixels.</summary>
    public int OutputWidth { get; }

    /// <summary>Gets the output image height in pixels.</summary>
    public int OutputHeight { get; }

    /// <summary>Gets the pixel channel layout.</summary>
    public ResizePixelLayout Layout { get; }

    /// <summary>Gets the sample data type / transfer curve.</summary>
    public ResizeDataType DataType { get; }

    /// <summary>Gets the resampling filter.</summary>
    public ResizeFilter Filter { get; }

    /// <summary>Gets the edge handling mode.</summary>
    public ResizeEdge Edge { get; }

    internal ResizePassParams HorizontalParams { get; }

    internal ResizePassParams VerticalParams { get; }

    internal StbGpuContext Context => _context;

    /// <summary>
    /// Creates a reusable VRAM-cached separable resampling plan for the specified dimensions and format.
    /// </summary>
    public static StbGpuResizePlan Create(
        StbGpuContext context,
        int inputWidth,
        int inputHeight,
        int outputWidth,
        int outputHeight,
        ResizePixelLayout layout,
        ResizeDataType dataType = ResizeDataType.UInt8Srgb,
        ResizeFilter filter = ResizeFilter.Default,
        ResizeEdge edge = ResizeEdge.Clamp)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (inputWidth <= 0 || inputHeight <= 0 || outputWidth <= 0 || outputHeight <= 0)
        {
            throw new StbException(StbErrorCode.InvalidArgument, "Resize dimensions must be positive.");
        }

        if (!TryDescribeFormat(layout, dataType, out int channels, out int colorMode, out int alphaChannel, out int swapRB))
        {
            throw new StbException(
                StbErrorCode.UnsupportedFormat,
                $"GPU resize plan does not support layout '{layout}' with dataType '{dataType}'.");
        }

        StbLimits limits = context.CpuOptions.Limits;
        int ox = 0;
        int oy = 0;
        bool hOk = ResizeGeometry.CalculateRegionTransform(out ResizeScaleInfo hScale, outputWidth, ref ox, outputWidth, inputWidth, 0.0, 1.0, 0, 1, 1);
        bool vOk = ResizeGeometry.CalculateRegionTransform(out ResizeScaleInfo vScale, outputHeight, ref oy, outputHeight, inputHeight, 0.0, 1.0, 0, 1, 1);
        if (!hOk || !vOk)
        {
            throw new StbException(StbErrorCode.InvalidArgument, "Invalid resize geometry.");
        }

        ResizeSampler hSampler = ResizeSampler.Build(in hScale, filter, customKernel: null, edge, alwaysGather: true, limits);
        ResizeSampler vSampler = ResizeSampler.Build(in vScale, filter, customKernel: null, edge, alwaysGather: true, limits);

        // Match stb_image_resize2's alpha weighting policy (PointSample disables alpha weighting).
        int effectiveAlphaCh = alphaChannel;
        if (hSampler.ResolvedFilter == ResizeFilter.PointSample && vSampler.ResolvedFilter == ResizeFilter.PointSample)
        {
            effectiveAlphaCh = -1;
        }

        int[] hN0Arr = new int[outputWidth];
        int[] hN1Arr = new int[outputWidth];
        for (int i = 0; i < outputWidth; ++i)
        {
            hN0Arr[i] = hSampler.Contributors[i].N0;
            hN1Arr[i] = hSampler.Contributors[i].N1;
        }

        int[] vN0Arr = new int[outputHeight];
        int[] vN1Arr = new int[outputHeight];
        for (int i = 0; i < outputHeight; ++i)
        {
            vN0Arr[i] = vSampler.Contributors[i].N0;
            vN1Arr[i] = vSampler.Contributors[i].N1;
        }

        int edgeInt = edge switch
        {
            ResizeEdge.Clamp => 0,
            ResizeEdge.Reflect => 1,
            ResizeEdge.Wrap => 2,
            ResizeEdge.Zero => 3,
            _ => 0,
        };

        var hParams = new ResizePassParams(inputWidth, inputHeight, outputWidth, outputHeight, channels, hSampler.CoefficientWidth, edgeInt, colorMode, effectiveAlphaCh, swapRB);
        var vParams = new ResizePassParams(inputWidth, inputHeight, outputWidth, outputHeight, channels, vSampler.CoefficientWidth, edgeInt, colorMode, effectiveAlphaCh, swapRB);

        lock (context.SyncRoot)
        {
            Accelerator acc = context.Accelerator;
            var hN0 = acc.Allocate1D(hN0Arr);
            var hN1 = acc.Allocate1D(hN1Arr);
            var hCoeffs = acc.Allocate1D(hSampler.Coefficients);
            var vN0 = acc.Allocate1D(vN0Arr);
            var vN1 = acc.Allocate1D(vN1Arr);
            var vCoeffs = acc.Allocate1D(vSampler.Coefficients);
            acc.Synchronize();

            return new StbGpuResizePlan(
                context,
                inputWidth, inputHeight, outputWidth, outputHeight,
                layout, dataType, filter, edge,
                hParams, vParams,
                hN0, hN1, hCoeffs, vN0, vN1, vCoeffs);
        }
    }

    internal static bool TryDescribeFormat(
        ResizePixelLayout layout,
        ResizeDataType dataType,
        out int channels,
        out int colorMode,
        out int alphaChannel,
        out int swapRB)
    {
        channels = 0;
        colorMode = 0;
        alphaChannel = -1;
        swapRB = 0;

        switch (dataType)
        {
            case ResizeDataType.UInt8Linear:
                colorMode = 0;
                break;
            case ResizeDataType.UInt8Srgb:
                colorMode = 1;
                break;
            case ResizeDataType.UInt8SrgbAlpha:
                colorMode = 2;
                break;
            case ResizeDataType.Fp32:
                colorMode = 0;
                break;
            default:
                return false;
        }

        switch (layout)
        {
            case ResizePixelLayout.OneChannel:
                channels = 1;
                return true;
            case ResizePixelLayout.TwoChannel:
                channels = 2;
                return true;
            case ResizePixelLayout.Ra:
                channels = 2;
                alphaChannel = 1;
                return true;
            case ResizePixelLayout.RaPremultiplied:
                channels = 2;
                alphaChannel = -1;
                return true;
            case ResizePixelLayout.Rgb:
                channels = 3;
                return true;
            case ResizePixelLayout.Bgr:
                channels = 3;
                swapRB = 1;
                return true;
            case ResizePixelLayout.FourChannel:
            case ResizePixelLayout.RgbaPremultiplied:
                channels = 4;
                alphaChannel = -1;
                return true;
            case ResizePixelLayout.BgraPremultiplied:
                channels = 4;
                alphaChannel = -1;
                swapRB = 1;
                return true;
            case ResizePixelLayout.Rgba:
                channels = 4;
                alphaChannel = 3;
                return true;
            case ResizePixelLayout.Bgra:
                channels = 4;
                alphaChannel = 3;
                swapRB = 1;
                return true;
            default:
                return false;
        }
    }

    internal void ExecuteByteLocked(
        ArrayView1D<byte, Stride1D.Dense> srcView,
        ArrayView1D<byte, Stride1D.Dense> dstView,
        int batchCount)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        long tmpElems = (long)batchCount * InputHeight * OutputWidth * HorizontalParams.Channels;
        using PooledGpuBuffer<float> tmpBuf = _context.Pool.RentFloat32(tmpElems);

        _context.GetSrgbTablesLocked(out var srgbToLin, out var fp32ToSrgb);
        var hKernel = _context.GetResizeHByteKernelLocked();
        var vKernel = _context.GetResizeVByteKernelLocked();

        int hWork = batchCount * InputHeight * OutputWidth;
        int vWork = batchCount * OutputHeight * OutputWidth;

        hKernel(hWork, srcView, tmpBuf.View, _hN0!.View, _hN1!.View, _hCoeffs!.View, srgbToLin, HorizontalParams);
        vKernel(vWork, tmpBuf.View, dstView, _vN0!.View, _vN1!.View, _vCoeffs!.View, fp32ToSrgb, VerticalParams);
        _context.Accelerator.Synchronize();
    }

    internal void ExecuteFloatLocked(
        ArrayView1D<float, Stride1D.Dense> srcView,
        ArrayView1D<float, Stride1D.Dense> dstView,
        int batchCount)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        long tmpElems = (long)batchCount * InputHeight * OutputWidth * HorizontalParams.Channels;
        using PooledGpuBuffer<float> tmpBuf = _context.Pool.RentFloat32(tmpElems);

        var hKernel = _context.GetResizeHFloatKernelLocked();
        var vKernel = _context.GetResizeVFloatKernelLocked();

        int hWork = batchCount * InputHeight * OutputWidth;
        int vWork = batchCount * OutputHeight * OutputWidth;

        hKernel(hWork, srcView, tmpBuf.View, _hN0!.View, _hN1!.View, _hCoeffs!.View, HorizontalParams);
        vKernel(vWork, tmpBuf.View, dstView, _vN0!.View, _vN1!.View, _vCoeffs!.View, VerticalParams);
        _context.Accelerator.Synchronize();
    }

    /// <summary>Runs the plan on the GPU for dense buffers of exactly the plan's size (no size checks or fallback).</summary>
    internal void ResizeOnGpu(ReadOnlySpan<byte> source, Span<byte> destination)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        int ch = HorizontalParams.Channels;
        long srcElems = (long)InputWidth * InputHeight * ch;
        long dstElems = (long)OutputWidth * OutputHeight * ch;
        if (DataType == ResizeDataType.Fp32)
        {
            using PooledGpuBuffer<float> srcF = _context.Pool.RentFloat32(srcElems);
            using PooledGpuBuffer<float> dstF = _context.Pool.RentFloat32(dstElems);
            lock (_context.SyncRoot)
            {
                _context.UploadSpanLocked(srcF.View, MemoryMarshal.Cast<byte, float>(source).Slice(0, (int)srcElems));
                ExecuteFloatLocked(srcF.View, dstF.View, batchCount: 1);
            }

            _context.RecordGpuDispatch();
            _context.DownloadSpan(dstF.View, MemoryMarshal.Cast<byte, float>(destination).Slice(0, (int)dstElems));
            return;
        }

        using PooledGpuBuffer<byte> srcBuf = _context.Pool.RentByte(srcElems);
        using PooledGpuBuffer<byte> dstBuf = _context.Pool.RentByte(dstElems);
        lock (_context.SyncRoot)
        {
            _context.UploadSpanLocked(srcBuf.View, source.Slice(0, (int)srcElems));
            ExecuteByteLocked(srcBuf.View, dstBuf.View, batchCount: 1);
        }

        _context.RecordGpuDispatch();
        _context.DownloadSpan(dstBuf.View, destination.Slice(0, (int)dstElems));
    }

    /// <summary>
    /// Resizes a single 8-bit image using this cached VRAM plan (no filter rebuild overhead).
    /// </summary>
    public void Resize(ReadOnlyMemory<byte> source, Memory<byte> destination, StbGpuExecutionMode? modeOverride = null)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (DataType == ResizeDataType.Fp32)
        {
            throw new StbException(StbErrorCode.InvalidArgument, "This plan was created for Fp32; call ResizeFloat instead.");
        }

        int ch = HorizontalParams.Channels;
        long srcBytes = (long)InputWidth * InputHeight * ch;
        long dstBytes = (long)OutputWidth * OutputHeight * ch;
        if (source.Length < srcBytes || destination.Length < dstBytes)
        {
            throw new StbException(StbErrorCode.InvalidArgument, "Source or destination buffer is too small for plan dimensions.");
        }

        long outPixels = (long)OutputWidth * OutputHeight;
        if (!_context.ShouldUseGpu(outPixels, _context.Thresholds.ResizeMinOutputPixels, modeOverride))
        {
            _context.RecordCpuFallback();
            var opts = new ImageResizeOptions
            {
                HorizontalFilter = Filter,
                VerticalFilter = Filter,
                HorizontalEdge = Edge,
                VerticalEdge = Edge,
                Options = _context.CpuOptions,
            };
            StbImageResize.Resize(source.Span, InputWidth, InputHeight, destination.Span, OutputWidth, OutputHeight, Layout, DataType, 0, 0, opts);
            return;
        }

        using PooledGpuBuffer<byte> srcBuf = _context.Pool.RentByte(srcBytes);
        using PooledGpuBuffer<byte> dstBuf = _context.Pool.RentByte(dstBytes);
        lock (_context.SyncRoot)
        {
            _context.UploadMemoryLocked(srcBuf.View, source, srcBytes);
            ExecuteByteLocked(srcBuf.View, dstBuf.View, batchCount: 1);
        }

        _context.RecordGpuDispatch();
        _context.DownloadSpan(dstBuf.View, destination.Span.Slice(0, (int)dstBytes));
    }

    /// <summary>
    /// Resizes a single 32-bit float image using this cached VRAM plan.
    /// </summary>
    public void ResizeFloat(ReadOnlyMemory<float> source, Memory<float> destination, StbGpuExecutionMode? modeOverride = null)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (DataType != ResizeDataType.Fp32)
        {
            throw new StbException(StbErrorCode.InvalidArgument, "This plan was created for 8-bit images; call Resize instead.");
        }

        int ch = HorizontalParams.Channels;
        long srcElems = (long)InputWidth * InputHeight * ch;
        long dstElems = (long)OutputWidth * OutputHeight * ch;
        if (source.Length < srcElems || destination.Length < dstElems)
        {
            throw new StbException(StbErrorCode.InvalidArgument, "Source or destination float buffer is too small for plan dimensions.");
        }

        long outPixels = (long)OutputWidth * OutputHeight;
        if (!_context.ShouldUseGpu(outPixels, _context.Thresholds.ResizeMinOutputPixels, modeOverride))
        {
            _context.RecordCpuFallback();
            var opts = new ImageResizeOptions
            {
                HorizontalFilter = Filter,
                VerticalFilter = Filter,
                HorizontalEdge = Edge,
                VerticalEdge = Edge,
                Options = _context.CpuOptions,
            };
            StbImageResize.ResizeFloatLinear(source.Span, InputWidth, InputHeight, destination.Span, OutputWidth, OutputHeight, Layout, 0, 0, opts);
            return;
        }

        using PooledGpuBuffer<float> srcBuf = _context.Pool.RentFloat32(srcElems);
        using PooledGpuBuffer<float> dstBuf = _context.Pool.RentFloat32(dstElems);
        lock (_context.SyncRoot)
        {
            _context.UploadMemoryLocked(srcBuf.View, source, srcElems);
            ExecuteFloatLocked(srcBuf.View, dstBuf.View, batchCount: 1);
        }

        _context.RecordGpuDispatch();
        _context.DownloadSpan(dstBuf.View, destination.Span.Slice(0, (int)dstElems));
    }

    /// <summary>
    /// Resizes a batch of equal-sized 8-bit images in a single batched GPU dispatch, amortizing PCIe transfer
    /// and kernel launch overhead across the entire batch.
    /// </summary>
    public byte[][] ResizeBatch(IReadOnlyList<ReadOnlyMemory<byte>> images, StbGpuExecutionMode? modeOverride = null)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(images);
        if (DataType == ResizeDataType.Fp32)
        {
            throw new StbException(StbErrorCode.InvalidArgument, "ResizeBatch requires an 8-bit resize plan.");
        }

        int count = images.Count;
        if (count == 0)
        {
            return [];
        }

        int ch = HorizontalParams.Channels;
        long srcImgBytes = (long)InputWidth * InputHeight * ch;
        long dstImgBytes = (long)OutputWidth * OutputHeight * ch;
        for (int i = 0; i < count; ++i)
        {
            if (images[i].Length < srcImgBytes)
            {
                throw new ArgumentException($"Batch image [{i}] is too small ({images[i].Length} < {srcImgBytes}).", nameof(images));
            }
        }

        long totalOutPixels = (long)count * OutputWidth * OutputHeight;
        if (!_context.ShouldUseGpu(totalOutPixels, _context.Thresholds.ResizeMinOutputPixels, modeOverride))
        {
            _context.RecordCpuFallback();
            var opts = new ImageResizeOptions
            {
                HorizontalFilter = Filter,
                VerticalFilter = Filter,
                HorizontalEdge = Edge,
                VerticalEdge = Edge,
                Options = _context.CpuOptions,
            };
            StbImageResizer resizer = StbImageResizer.Create(InputWidth, InputHeight, OutputWidth, OutputHeight, Layout, DataType, opts);
            byte[][] cpuOut = new byte[count][];
            for (int i = 0; i < count; ++i)
            {
                byte[] dst = GC.AllocateUninitializedArray<byte>((int)dstImgBytes);
                resizer.Resize(images[i].Span, dst);
                cpuOut[i] = dst;
            }

            return cpuOut;
        }

        long totalSrcBytes = srcImgBytes * count;
        long totalDstBytes = dstImgBytes * count;
        using PooledGpuBuffer<byte> srcBuf = _context.Pool.RentByte(totalSrcBytes);
        using PooledGpuBuffer<byte> dstBuf = _context.Pool.RentByte(totalDstBytes);

        lock (_context.SyncRoot)
        {
            for (int i = 0; i < count; ++i)
            {
                _context.UploadMemoryLocked(srcBuf.View.SubView(i * srcImgBytes, srcImgBytes), images[i], srcImgBytes);
            }

            ExecuteByteLocked(srcBuf.View, dstBuf.View, batchCount: count);
        }

        _context.RecordGpuDispatch();
        byte[] combined = _context.DownloadArray(dstBuf.View, totalDstBytes);
        byte[][] results = new byte[count][];
        for (int i = 0; i < count; ++i)
        {
            byte[] slice = GC.AllocateUninitializedArray<byte>((int)dstImgBytes);
            Buffer.BlockCopy(combined, (int)(i * dstImgBytes), slice, 0, (int)dstImgBytes);
            results[i] = slice;
        }

        return results;
    }

    /// <summary>Releases all VRAM coefficient tables owned by this resize plan.</summary>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        lock (_context.SyncRoot)
        {
            _hN0?.Dispose();
            _hN1?.Dispose();
            _hCoeffs?.Dispose();
            _vN0?.Dispose();
            _vN1?.Dispose();
            _vCoeffs?.Dispose();
            _hN0 = null;
            _hN1 = null;
            _hCoeffs = null;
            _vN0 = null;
            _vN1 = null;
            _vCoeffs = null;
        }
    }
}

/// <summary>
/// GPU-accelerated 2-pass separable image resampler, batch resizer, and VRAM mipmap / DXT mipmap pyramid generator
/// matching <see cref="StbImageResize"/> and <c>stb_image_resize2.h</c> v2.50 with automatic CPU fallback.
/// </summary>
public static class StbGpuImageResize
{
    /// <summary>
    /// Resizes an 8-bit <see cref="ImageBuffer{T}"/> on GPU (or falls back to <see cref="StbImageResize"/>).
    /// </summary>
    public static ImageBuffer<byte> Resize(
        StbGpuContext context,
        ImageBuffer<byte> source,
        int outputWidth,
        int outputHeight,
        ResizeDataType dataType = ResizeDataType.UInt8Srgb,
        ResizeFilter filter = ResizeFilter.Default,
        ResizeEdge edge = ResizeEdge.Clamp,
        StbGpuExecutionMode? modeOverride = null)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(source);

        ResizePixelLayout layout = ResizeGeometry.FromColorComponents(source.Components);
        long outPixels = (long)outputWidth * outputHeight;
        if (!StbGpuResizePlan.TryDescribeFormat(layout, dataType, out int channels, out _, out _, out _)
            || !context.ShouldUseGpu(outPixels, context.Thresholds.ResizeMinOutputPixels, modeOverride))
        {
            context.RecordCpuFallback();
            var opts = new ImageResizeOptions
            {
                HorizontalFilter = filter,
                VerticalFilter = filter,
                HorizontalEdge = edge,
                VerticalEdge = edge,
                Options = context.CpuOptions,
            };
            return StbImageResize.Resize(source, outputWidth, outputHeight, dataType, opts);
        }

        byte[] dst = GC.AllocateUninitializedArray<byte>(outputWidth * outputHeight * channels);
        using StbGpuResizePlan plan = StbGpuResizePlan.Create(context, source.Width, source.Height, outputWidth, outputHeight, layout, dataType, filter, edge);
        plan.Resize(source.ReadOnlyMemory, dst, StbGpuExecutionMode.ForceGpu);
        return new ImageBuffer<byte>(outputWidth, outputHeight, source.Components, source.SourceComponents, dst);
    }

    /// <summary>
    /// Resizes a 32-bit float <see cref="ImageBuffer{T}"/> on GPU (or falls back to <see cref="StbImageResize"/>).
    /// </summary>
    public static ImageBuffer<float> Resize(
        StbGpuContext context,
        ImageBuffer<float> source,
        int outputWidth,
        int outputHeight,
        ResizeFilter filter = ResizeFilter.Default,
        ResizeEdge edge = ResizeEdge.Clamp,
        StbGpuExecutionMode? modeOverride = null)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(source);

        ResizePixelLayout layout = ResizeGeometry.FromColorComponents(source.Components);
        long outPixels = (long)outputWidth * outputHeight;
        if (!context.ShouldUseGpu(outPixels, context.Thresholds.ResizeMinOutputPixels, modeOverride))
        {
            context.RecordCpuFallback();
            var opts = new ImageResizeOptions
            {
                HorizontalFilter = filter,
                VerticalFilter = filter,
                HorizontalEdge = edge,
                VerticalEdge = edge,
                Options = context.CpuOptions,
            };
            return StbImageResize.Resize(source, outputWidth, outputHeight, opts);
        }

        int channels = (int)source.Components;
        float[] dst = GC.AllocateUninitializedArray<float>(outputWidth * outputHeight * channels);
        using StbGpuResizePlan plan = StbGpuResizePlan.Create(context, source.Width, source.Height, outputWidth, outputHeight, layout, ResizeDataType.Fp32, filter, edge);
        plan.ResizeFloat(source.ReadOnlyMemory, dst, StbGpuExecutionMode.ForceGpu);
        return new ImageBuffer<float>(outputWidth, outputHeight, source.Components, source.SourceComponents, dst);
    }

    /// <summary>
    /// Resizes a VRAM-resident 8-bit image directly into a new VRAM-resident image (zero PCIe roundtrip).
    /// </summary>
    public static StbGpuImage<byte> ResizeGpuImage(
        StbGpuContext context,
        StbGpuImage<byte> source,
        int outputWidth,
        int outputHeight,
        ResizeDataType dataType = ResizeDataType.UInt8Srgb,
        ResizeFilter filter = ResizeFilter.Default,
        ResizeEdge edge = ResizeEdge.Clamp)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(source);

        ResizePixelLayout layout = ResizeGeometry.FromColorComponents(source.Components);
        using StbGpuResizePlan plan = StbGpuResizePlan.Create(context, source.Width, source.Height, outputWidth, outputHeight, layout, dataType, filter, edge);
        StbGpuImage<byte> dst = context.AllocateByteImage(outputWidth, outputHeight, source.Components);
        try
        {
            lock (context.SyncRoot)
            {
                plan.ExecuteByteLocked(source.View, dst.View, batchCount: 1);
            }

            context.RecordGpuDispatch();
            return dst;
        }
        catch
        {
            dst.Dispose();
            throw;
        }
    }

    /// <summary>
    /// Resizes a VRAM-resident 32-bit float image directly into a new VRAM-resident image (zero PCIe roundtrip).
    /// </summary>
    public static StbGpuImage<float> ResizeGpuImage(
        StbGpuContext context,
        StbGpuImage<float> source,
        int outputWidth,
        int outputHeight,
        ResizeFilter filter = ResizeFilter.Default,
        ResizeEdge edge = ResizeEdge.Clamp)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(source);

        ResizePixelLayout layout = ResizeGeometry.FromColorComponents(source.Components);
        using StbGpuResizePlan plan = StbGpuResizePlan.Create(context, source.Width, source.Height, outputWidth, outputHeight, layout, ResizeDataType.Fp32, filter, edge);
        StbGpuImage<float> dst = context.AllocateFloatImage(outputWidth, outputHeight, source.Components);
        try
        {
            lock (context.SyncRoot)
            {
                plan.ExecuteFloatLocked(source.View, dst.View, batchCount: 1);
            }

            context.RecordGpuDispatch();
            return dst;
        }
        catch
        {
            dst.Dispose();
            throw;
        }
    }

    /// <summary>
    /// Generates a full mipmap chain (from level 0 down to <c>1×1</c>) keeping all intermediate downsamples in VRAM.
    /// </summary>
    /// <param name="context">GPU context.</param>
    /// <param name="baseLevel">Level-0 source image.</param>
    /// <param name="dataType">Transfer curve (defaults to <see cref="ResizeDataType.UInt8Srgb"/>).</param>
    /// <param name="filter">Downsampling filter (defaults to <see cref="ResizeFilter.Default"/>).</param>
    /// <param name="maxLevels">Maximum number of mip levels (<c>0</c> = full chain down to <c>1×1</c>).</param>
    /// <param name="modeOverride">Optional execution mode override.</param>
    /// <returns>Array of mipmap <see cref="ImageBuffer{T}"/> levels starting with level 0.</returns>
    public static ImageBuffer<byte>[] GenerateMipmaps(
        StbGpuContext context,
        ImageBuffer<byte> baseLevel,
        ResizeDataType dataType = ResizeDataType.UInt8Srgb,
        ResizeFilter filter = ResizeFilter.Default,
        int maxLevels = 0,
        StbGpuExecutionMode? modeOverride = null)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(baseLevel);

        int levelCount = ComputeMipLevelCount(baseLevel.Width, baseLevel.Height, maxLevels);
        ImageBuffer<byte>[] levels = new ImageBuffer<byte>[levelCount];
        levels[0] = baseLevel;
        if (levelCount == 1)
        {
            return levels;
        }

        long basePixels = (long)baseLevel.Width * baseLevel.Height;
        if (!context.ShouldUseGpu(basePixels, context.Thresholds.ResizeMinOutputPixels, modeOverride))
        {
            context.RecordCpuFallback();
            var opts = new ImageResizeOptions { HorizontalFilter = filter, VerticalFilter = filter, Options = context.CpuOptions };
            for (int lvl = 1; lvl < levelCount; ++lvl)
            {
                ImageBuffer<byte> prev = levels[lvl - 1];
                int nw = Math.Max(1, prev.Width >> 1);
                int nh = Math.Max(1, prev.Height >> 1);
                levels[lvl] = StbImageResize.Resize(prev, nw, nh, dataType, opts);
            }

            return levels;
        }

        StbGpuImage<byte> current = context.Upload(baseLevel);
        try
        {
            for (int lvl = 1; lvl < levelCount; ++lvl)
            {
                int nw = Math.Max(1, current.Width >> 1);
                int nh = Math.Max(1, current.Height >> 1);
                StbGpuImage<byte> next = ResizeGpuImage(context, current, nw, nh, dataType, filter, ResizeEdge.Clamp);
                current.Dispose();
                current = next;
                levels[lvl] = current.DownloadToBuffer();
            }
        }
        finally
        {
            current.Dispose();
        }

        return levels;
    }

    /// <summary>
    /// Uploads a base texture once to VRAM, generates the full mipmap chain entirely in VRAM,
    /// compresses every mip level to BC1/BC3/BC4/BC5 directly in VRAM, and downloads only the compact DXT blocks.
    /// </summary>
    public static byte[][] GenerateDxtMipmaps(
        StbGpuContext context,
        ImageBuffer<byte> baseLevel,
        DxtFormat format,
        DxtCompressionMode mode = DxtCompressionMode.Normal,
        ResizeDataType dataType = ResizeDataType.UInt8Srgb,
        ResizeFilter filter = ResizeFilter.Default,
        int maxLevels = 0,
        StbGpuExecutionMode? modeOverride = null)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(baseLevel);

        int levelCount = ComputeMipLevelCount(baseLevel.Width, baseLevel.Height, maxLevels);
        long basePixels = (long)baseLevel.Width * baseLevel.Height;

        if (!context.ShouldUseGpu(basePixels, context.Thresholds.ResizeMinOutputPixels, modeOverride))
        {
            context.RecordCpuFallback();
            ImageBuffer<byte>[] cpuMips = GenerateMipmaps(context, baseLevel, dataType, filter, maxLevels, StbGpuExecutionMode.ForceCpu);
            byte[][] cpuDxt = new byte[cpuMips.Length][];
            for (int i = 0; i < cpuMips.Length; ++i)
            {
                cpuDxt[i] = StbDxt.CompressImageToArray(cpuMips[i].ReadOnlyMemory, cpuMips[i].Width, cpuMips[i].Height, format, 0, mode, context.CpuOptions);
            }

            return cpuDxt;
        }

        byte[][] dxtLevels = new byte[levelCount][];
        StbGpuImage<byte> current = context.Upload(baseLevel);
        try
        {
            dxtLevels[0] = StbGpuDxt.CompressGpuImage(context, current, format, mode);
            for (int lvl = 1; lvl < levelCount; ++lvl)
            {
                int nw = Math.Max(1, current.Width >> 1);
                int nh = Math.Max(1, current.Height >> 1);
                StbGpuImage<byte> next = ResizeGpuImage(context, current, nw, nh, dataType, filter, ResizeEdge.Clamp);
                current.Dispose();
                current = next;
                dxtLevels[lvl] = StbGpuDxt.CompressGpuImage(context, current, format, mode);
            }
        }
        finally
        {
            current.Dispose();
        }

        return dxtLevels;
    }

    private static int ComputeMipLevelCount(int width, int height, int maxLevels)
    {
        int count = 1;
        int w = width;
        int h = height;
        while (w > 1 || h > 1)
        {
            w = Math.Max(1, w >> 1);
            h = Math.Max(1, h >> 1);
            count++;
        }

        return maxLevels > 0 ? Math.Min(count, maxLevels) : count;
    }
}
