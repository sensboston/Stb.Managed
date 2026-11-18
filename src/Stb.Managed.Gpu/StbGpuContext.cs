using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using ILGPU;
using ILGPU.Algorithms;
using ILGPU.Runtime;
using ILGPU.Runtime.CPU;
using ILGPU.Runtime.Cuda;
using ILGPU.Runtime.OpenCL;
using Stb.Managed.Dxt.Internal;
using Stb.Managed.Gpu.Internal;
using Stb.Managed.Gpu.Kernels;
using Stb.Managed.Internal;
using Stb.Managed.Perlin.Internal;

namespace Stb.Managed.Gpu;

/// <summary>
/// Manages an ILGPU <see cref="Context"/>, <see cref="Accelerator"/>, pooled VRAM buffers,
/// constant lookup tables, and lazily compiled kernels for all <c>Stb.Managed.Gpu</c> operations.
/// Thread-safe across concurrent callers; dispose when finished to release GPU resources.
/// </summary>
public sealed partial class StbGpuContext : IDisposable
{
    private readonly Context _context;
    private readonly Accelerator _accelerator;
    private readonly GpuBufferPool _pool;
    private readonly object _sync = new();
    private bool _disposed;

    private long _gpuDispatches;
    private long _cpuFallbacks;
    private long _bytesUploaded;
    private long _bytesDownloaded;

    // Constant VRAM lookup tables (uploaded lazily once per context lifetime).
    private MemoryBuffer1D<byte, Stride1D.Dense>? _dxtOMatch5;
    private MemoryBuffer1D<byte, Stride1D.Dense>? _dxtOMatch6;
    private MemoryBuffer1D<float, Stride1D.Dense>? _dxtMidpoints5;
    private MemoryBuffer1D<float, Stride1D.Dense>? _dxtMidpoints6;

    private MemoryBuffer1D<byte, Stride1D.Dense>? _perlinRandTab;
    private MemoryBuffer1D<byte, Stride1D.Dense>? _perlinRandTabGradIdx;
    private MemoryBuffer1D<float, Stride1D.Dense>? _perlinBasisX;
    private MemoryBuffer1D<float, Stride1D.Dense>? _perlinBasisY;
    private MemoryBuffer1D<float, Stride1D.Dense>? _perlinBasisZ;

    private MemoryBuffer1D<float, Stride1D.Dense>? _srgbToLinear;
    private MemoryBuffer1D<uint, Stride1D.Dense>? _fp32ToSrgbTab;

    // Lazily compiled & cached kernels.
    private Action<Index1D, ArrayView1D<byte, Stride1D.Dense>, ArrayView1D<byte, Stride1D.Dense>, ArrayView1D<byte, Stride1D.Dense>, ArrayView1D<byte, Stride1D.Dense>, ArrayView1D<float, Stride1D.Dense>, ArrayView1D<float, Stride1D.Dense>, DxtImageParams>? _dxtRgbaKernel;
    private Action<Index1D, ArrayView1D<byte, Stride1D.Dense>, ArrayView1D<byte, Stride1D.Dense>, DxtImageParams>? _dxtBc4Kernel;
    private Action<Index1D, ArrayView1D<byte, Stride1D.Dense>, ArrayView1D<byte, Stride1D.Dense>, DxtImageParams>? _dxtBc5Kernel;

    private Action<Index1D, ArrayView1D<float, Stride1D.Dense>, ArrayView1D<byte, Stride1D.Dense>, ArrayView1D<byte, Stride1D.Dense>, ArrayView1D<float, Stride1D.Dense>, ArrayView1D<float, Stride1D.Dense>, ArrayView1D<float, Stride1D.Dense>, Perlin2DParams>? _perlin2DKernel;
    private Action<Index1D, ArrayView1D<float, Stride1D.Dense>, ArrayView1D<byte, Stride1D.Dense>, ArrayView1D<byte, Stride1D.Dense>, ArrayView1D<float, Stride1D.Dense>, ArrayView1D<float, Stride1D.Dense>, ArrayView1D<float, Stride1D.Dense>, Perlin3DParams>? _perlin3DKernel;

    private Action<Index1D, ArrayView1D<byte, Stride1D.Dense>, ArrayView1D<float, Stride1D.Dense>, ArrayView1D<int, Stride1D.Dense>, ArrayView1D<int, Stride1D.Dense>, ArrayView1D<float, Stride1D.Dense>, ArrayView1D<float, Stride1D.Dense>, ResizePassParams>? _resizeHByteKernel;
    private Action<Index1D, ArrayView1D<float, Stride1D.Dense>, ArrayView1D<byte, Stride1D.Dense>, ArrayView1D<int, Stride1D.Dense>, ArrayView1D<int, Stride1D.Dense>, ArrayView1D<float, Stride1D.Dense>, ArrayView1D<uint, Stride1D.Dense>, ResizePassParams>? _resizeVByteKernel;
    private Action<Index1D, ArrayView1D<float, Stride1D.Dense>, ArrayView1D<float, Stride1D.Dense>, ArrayView1D<int, Stride1D.Dense>, ArrayView1D<int, Stride1D.Dense>, ArrayView1D<float, Stride1D.Dense>, ResizePassParams>? _resizeHFloatKernel;
    private Action<Index1D, ArrayView1D<float, Stride1D.Dense>, ArrayView1D<float, Stride1D.Dense>, ArrayView1D<int, Stride1D.Dense>, ArrayView1D<int, Stride1D.Dense>, ArrayView1D<float, Stride1D.Dense>, ResizePassParams>? _resizeVFloatKernel;

    private Action<Index1D, ArrayView1D<byte, Stride1D.Dense>, ArrayView1D<GpuSdfGlyphDesc, Stride1D.Dense>, ArrayView1D<GpuSdfLine, Stride1D.Dense>, ArrayView1D<GpuSdfCurve, Stride1D.Dense>, ArrayView1D<GpuSdfCrossEdge, Stride1D.Dense>, int>? _sdfBatchKernel;

    private Action<Index1D, ArrayView1D<byte, Stride1D.Dense>, ArrayView1D<byte, Stride1D.Dense>>? _swapRBKernel;
    private Action<Index1D, ArrayView1D<byte, Stride1D.Dense>, ArrayView1D<byte, Stride1D.Dense>, byte>? _rgbToRgbaKernel;
    private Action<Index1D, ArrayView1D<byte, Stride1D.Dense>, ArrayView1D<byte, Stride1D.Dense>>? _rgbaToRgbKernel;
    private Action<Index1D, ArrayView1D<byte, Stride1D.Dense>, ArrayView1D<byte, Stride1D.Dense>>? _rgbaToGreyKernel;
    private Action<Index1D, ArrayView1D<byte, Stride1D.Dense>, ArrayView1D<byte, Stride1D.Dense>, byte>? _greyToRgbaKernel;
    private Action<Index1D, ArrayView1D<byte, Stride1D.Dense>, ArrayView1D<byte, Stride1D.Dense>>? _premulKernel;
    private Action<Index1D, ArrayView1D<byte, Stride1D.Dense>, ArrayView1D<byte, Stride1D.Dense>>? _unpremulKernel;
    private Action<Index1D, ArrayView1D<byte, Stride1D.Dense>, ArrayView1D<float, Stride1D.Dense>, ArrayView1D<float, Stride1D.Dense>, int, int>? _srgbToLinearKernel;
    private Action<Index1D, ArrayView1D<float, Stride1D.Dense>, ArrayView1D<byte, Stride1D.Dense>, ArrayView1D<uint, Stride1D.Dense>, int, int>? _linearToSrgbKernel;
    private Action<Index1D, ArrayView1D<float, Stride1D.Dense>, ArrayView1D<byte, Stride1D.Dense>, float, float, byte>? _heightmapToRgbaKernel;
    private Action<Index1D, ArrayView1D<float, Stride1D.Dense>, ArrayView1D<byte, Stride1D.Dense>, float, float>? _heightmapToGreyKernel;

    private StbGpuContext(
        Context context,
        Accelerator accelerator,
        StbGpuBackendKind backend,
        StbGpuOptions options)
    {
        _context = context;
        _accelerator = accelerator;
        _pool = new GpuBufferPool(accelerator, options.MaxPooledVramBytes);

        bool isHardware = backend is StbGpuBackendKind.Cuda or StbGpuBackendKind.OpenCL;
        bool isLikelyDiscrete = DetectLikelyDiscrete(backend, accelerator.Name, accelerator.MemorySize);

        Device = new StbGpuDeviceInfo(
            Name: accelerator.Name,
            Backend: backend,
            MemoryBytes: accelerator.MemorySize,
            MaxNumThreadsPerGroup: accelerator.MaxNumThreadsPerGroup,
            NumMultiprocessors: accelerator.NumMultiprocessors,
            IsHardwareGpu: isHardware,
            IsLikelyDiscrete: isLikelyDiscrete);

        ExecutionMode = options.ExecutionMode;
        Thresholds = options.Thresholds ?? (isLikelyDiscrete ? StbGpuThresholds.DiscreteDefaults : StbGpuThresholds.IntegratedDefaults);
        CpuOptions = options.CpuOptions ?? StbOptions.Default;
    }

    /// <summary>Gets hardware and backend metadata for this context's active accelerator.</summary>
    public StbGpuDeviceInfo Device { get; }

    /// <summary>Gets or sets the default execution routing mode for operations using this context.</summary>
    public StbGpuExecutionMode ExecutionMode { get; set; }

    /// <summary>Gets the active workload size thresholds for <see cref="StbGpuExecutionMode.Auto"/>.</summary>
    public StbGpuThresholds Thresholds { get; }

    /// <summary>Gets the <see cref="StbOptions"/> forwarded to CPU fallback paths.</summary>
    public StbOptions CpuOptions { get; }

    /// <summary>Gets a snapshot of cumulative dispatch and transfer statistics.</summary>
    public StbGpuStats Stats => new(
        Interlocked.Read(ref _gpuDispatches),
        Interlocked.Read(ref _cpuFallbacks),
        Interlocked.Read(ref _bytesUploaded),
        Interlocked.Read(ref _bytesDownloaded));

    /// <summary>Resets all diagnostic counters in <see cref="Stats"/> to zero.</summary>
    public void ResetStats()
    {
        Interlocked.Exchange(ref _gpuDispatches, 0L);
        Interlocked.Exchange(ref _cpuFallbacks, 0L);
        Interlocked.Exchange(ref _bytesUploaded, 0L);
        Interlocked.Exchange(ref _bytesDownloaded, 0L);
    }

    internal Accelerator Accelerator
    {
        get
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            return _accelerator;
        }
    }

    internal GpuBufferPool Pool
    {
        get
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            return _pool;
        }
    }

    internal object SyncRoot => _sync;

    // ILGPU 1.5.x issue #1155 workaround:
    // Context.Builder.DebugSymbols validates its argument but does not store it, leaving mode=Auto
    // which causes spurious BadImageFormatException under a debugger on assemblies without portable PDBs.
    private static readonly System.Reflection.MethodInfo? DebugSymbolsModeSetter =
        typeof(ContextProperties).GetProperty(
            "DebugSymbolsMode",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic)
        ?.GetSetMethod(nonPublic: true);

    private static void ConfigureContext(Context.Builder builder)
    {
        builder.Default();
        builder.EnableAlgorithms();
        builder.Math(MathMode.Default);
        builder.Optimize(OptimizationLevel.O1);
        if (DebugSymbolsModeSetter is not null)
        {
            DebugSymbolsModeSetter.Invoke(builder, [DebugSymbolsMode.Disabled]);
        }
        else
        {
            builder.DebugSymbols(DebugSymbolsMode.Disabled);
        }
    }

    /// <summary>
    /// Creates a new <see cref="StbGpuContext"/> according to <paramref name="options"/>.
    /// In <see cref="StbGpuBackendPreference.Auto"/> mode, probes CUDA → OpenCL → ILGPU CPU fallback.
    /// </summary>
    /// <param name="options">Optional GPU configuration.</param>
    /// <returns>An initialized <see cref="StbGpuContext"/>.</returns>
    public static StbGpuContext Create(StbGpuOptions? options = null)
    {
        StbGpuOptions opts = options ?? StbGpuOptions.Default;
        Context context = Context.Create(ConfigureContext);

        try
        {
            Accelerator? acc = null;
            StbGpuBackendKind kind = StbGpuBackendKind.None;

            if (opts.BackendPreference is StbGpuBackendPreference.Auto or StbGpuBackendPreference.Cuda)
            {
                var cudaDevices = context.GetCudaDevices();
                if (cudaDevices.Count > 0)
                {
                    try
                    {
                        acc = cudaDevices[0].CreateAccelerator(context);
                        kind = StbGpuBackendKind.Cuda;
                    }
                    catch when (opts.BackendPreference == StbGpuBackendPreference.Auto)
                    {
                        acc = null;
                    }
                }
                else if (opts.BackendPreference == StbGpuBackendPreference.Cuda)
                {
                    throw new StbException(StbErrorCode.UnsupportedFormat, "No NVIDIA CUDA device was detected by ILGPU.");
                }
            }

            if (acc is null && opts.BackendPreference is StbGpuBackendPreference.Auto or StbGpuBackendPreference.OpenCL)
            {
                var clDevices = context.GetCLDevices();
                if (clDevices.Count > 0)
                {
                    try
                    {
                        acc = clDevices[0].CreateAccelerator(context);
                        kind = StbGpuBackendKind.OpenCL;
                    }
                    catch when (opts.BackendPreference == StbGpuBackendPreference.Auto)
                    {
                        acc = null;
                    }
                }
                else if (opts.BackendPreference == StbGpuBackendPreference.OpenCL)
                {
                    throw new StbException(StbErrorCode.UnsupportedFormat, "No OpenCL device was detected by ILGPU.");
                }
            }

            if (acc is null)
            {
                var cpuDevice = context.GetCPUDevice(0);
                acc = cpuDevice.CreateAccelerator(context);
                kind = StbGpuBackendKind.Cpu;
            }

            return new StbGpuContext(context, acc, kind, opts);
        }
        catch
        {
            context.Dispose();
            throw;
        }
    }

    /// <summary>
    /// Probes for a real hardware GPU (NVIDIA CUDA or OpenCL) without falling back to CPU emulation.
    /// Returns <see langword="null"/> cleanly if no hardware GPU or driver is available.
    /// </summary>
    /// <param name="options">Optional GPU configuration.</param>
    /// <returns>A hardware-backed <see cref="StbGpuContext"/>, or <see langword="null"/> if none was found.</returns>
    public static StbGpuContext? TryCreateHardware(StbGpuOptions? options = null)
    {
        StbGpuOptions opts = options ?? StbGpuOptions.Default;
        if (opts.BackendPreference == StbGpuBackendPreference.Cpu)
        {
            return null;
        }

        Context? context = null;
        try
        {
            context = Context.Create(ConfigureContext);
            Accelerator? acc = null;
            StbGpuBackendKind kind = StbGpuBackendKind.None;

            if (opts.BackendPreference is StbGpuBackendPreference.Auto or StbGpuBackendPreference.Cuda)
            {
                try
                {
                    var cudaDevices = context.GetCudaDevices();
                    if (cudaDevices.Count > 0)
                    {
                        acc = cudaDevices[0].CreateAccelerator(context);
                        kind = StbGpuBackendKind.Cuda;
                    }
                }
                catch
                {
                    acc = null;
                }
            }

            if (acc is null && opts.BackendPreference is StbGpuBackendPreference.Auto or StbGpuBackendPreference.OpenCL)
            {
                try
                {
                    var clDevices = context.GetCLDevices();
                    if (clDevices.Count > 0)
                    {
                        acc = clDevices[0].CreateAccelerator(context);
                        kind = StbGpuBackendKind.OpenCL;
                    }
                }
                catch
                {
                    acc = null;
                }
            }

            if (acc is null)
            {
                context.Dispose();
                return null;
            }

            var gpuContext = new StbGpuContext(context, acc, kind, opts);
            context = null;
            return gpuContext;
        }
        catch
        {
            context?.Dispose();
            return null;
        }
    }

    /// <summary>
    /// Pre-compiles and caches GPU kernels and uploads constant lookup tables so the first real
    /// operation runs with zero JIT/PTX compilation latency.
    /// </summary>
    public void Warmup()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        lock (_sync)
        {
            EnsureDxtTablesLocked();
            EnsurePerlinTablesLocked();
            EnsureSrgbTablesLocked();
            _ = GetDxtRgbaKernelLocked();
            _ = GetDxtBc4KernelLocked();
            _ = GetDxtBc5KernelLocked();
            _ = GetPerlin2DKernelLocked();
            _ = GetPerlin3DKernelLocked();
            _ = GetResizeHByteKernelLocked();
            _ = GetResizeVByteKernelLocked();
            _ = GetResizeHFloatKernelLocked();
            _ = GetResizeVFloatKernelLocked();
            _ = GetSdfBatchKernelLocked();
            _ = GetSwapRBKernelLocked();
            _ = GetRgbToRgbaKernelLocked();
            _ = GetRgbaToRgbKernelLocked();
            _ = GetRgbaToGreyKernelLocked();
            _ = GetGreyToRgbaKernelLocked();
            _ = GetPremulKernelLocked();
            _ = GetUnpremulKernelLocked();
            _ = GetSrgbToLinearKernelLocked();
            _ = GetLinearToSrgbKernelLocked();
            _ = GetHeightmapToRgbaKernelLocked();
            _ = GetHeightmapToGreyKernelLocked();
            _accelerator.Synchronize();
        }
    }

    /// <summary>
    /// Evaluates whether an operation with <paramref name="workUnits"/> should execute on GPU
    /// given the effective execution mode and threshold.
    /// </summary>
    /// <param name="workUnits">Measured workload metric (blocks, pixels, sample-octaves, or pixel-segments).</param>
    /// <param name="threshold">Configured minimum threshold for <see cref="StbGpuExecutionMode.Auto"/>.</param>
    /// <param name="modeOverride">Optional per-call mode override.</param>
    /// <returns><see langword="true"/> to dispatch on GPU; <see langword="false"/> to use CPU fallback.</returns>
    public bool ShouldUseGpu(long workUnits, long threshold, StbGpuExecutionMode? modeOverride = null)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        StbGpuExecutionMode mode = modeOverride ?? ExecutionMode;
        return mode switch
        {
            StbGpuExecutionMode.ForceCpu => false,
            StbGpuExecutionMode.ForceGpu => true,
            _ => Device.IsHardwareGpu && workUnits >= threshold,
        };
    }

    /// <summary>
    /// Uploads an 8-bit <see cref="ImageBuffer{T}"/> into pooled VRAM.
    /// </summary>
    public StbGpuImage<byte> Upload(ImageBuffer<byte> image)
    {
        ArgumentNullException.ThrowIfNull(image);
        return Upload(image.ReadOnlyMemory, image.Width, image.Height, image.Components);
    }

    /// <summary>
    /// Uploads a 32-bit float <see cref="ImageBuffer{T}"/> into pooled VRAM.
    /// </summary>
    public StbGpuImage<float> Upload(ImageBuffer<float> image)
    {
        ArgumentNullException.ThrowIfNull(image);
        return Upload(image.ReadOnlyMemory, image.Width, image.Height, image.Components);
    }

    /// <summary>
    /// Uploads 8-bit pixel memory into pooled VRAM (zero staging copy when backed by a byte array).
    /// </summary>
    public StbGpuImage<byte> Upload(ReadOnlyMemory<byte> pixels, int width, int height, ColorComponents components)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        long count = ValidateImageDimensions(width, height, components, pixels.Length);
        PooledGpuBuffer<byte> buf = _pool.RentByte(count);
        try
        {
            lock (_sync)
            {
                UploadMemoryLocked(buf.View, pixels, count);
                _accelerator.Synchronize();
            }

            return new StbGpuImage<byte>(this, buf, width, height, components);
        }
        catch
        {
            buf.Dispose();
            throw;
        }
    }

    /// <summary>
    /// Uploads 8-bit pixel span data into pooled VRAM.
    /// </summary>
    public StbGpuImage<byte> Upload(ReadOnlySpan<byte> pixels, int width, int height, ColorComponents components)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        long count = ValidateImageDimensions(width, height, components, pixels.Length);
        PooledGpuBuffer<byte> buf = _pool.RentByte(count);
        try
        {
            lock (_sync)
            {
                GpuBufferPool.CopyFromHostSpan(buf.View, pixels.Slice(0, (int)count));
                AddBytesUploaded(count);
                _accelerator.Synchronize();
            }

            return new StbGpuImage<byte>(this, buf, width, height, components);
        }
        catch
        {
            buf.Dispose();
            throw;
        }
    }

    /// <summary>
    /// Uploads 32-bit float pixel memory into pooled VRAM (zero staging copy when backed by a float array).
    /// </summary>
    public StbGpuImage<float> Upload(ReadOnlyMemory<float> pixels, int width, int height, ColorComponents components)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        long count = ValidateImageDimensions(width, height, components, pixels.Length);
        PooledGpuBuffer<float> buf = _pool.RentFloat32(count);
        try
        {
            lock (_sync)
            {
                UploadMemoryLocked(buf.View, pixels, count);
                _accelerator.Synchronize();
            }

            return new StbGpuImage<float>(this, buf, width, height, components);
        }
        catch
        {
            buf.Dispose();
            throw;
        }
    }

    /// <summary>
    /// Allocates an uninitialized VRAM-resident 8-bit image.
    /// </summary>
    public StbGpuImage<byte> AllocateByteImage(int width, int height, ColorComponents components, bool zeroInitialize = false)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        long count = ValidateImageDimensions(width, height, components, int.MaxValue);
        PooledGpuBuffer<byte> buf = _pool.RentByte(count, zeroInitialize);
        return new StbGpuImage<byte>(this, buf, width, height, components);
    }

    /// <summary>
    /// Allocates an uninitialized VRAM-resident 32-bit float image.
    /// </summary>
    public StbGpuImage<float> AllocateFloatImage(int width, int height, ColorComponents components, bool zeroInitialize = false)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        long count = ValidateImageDimensions(width, height, components, int.MaxValue);
        PooledGpuBuffer<float> buf = _pool.RentFloat32(count, zeroInitialize);
        return new StbGpuImage<float>(this, buf, width, height, components);
    }

    internal T[] DownloadArray<T>(ArrayView1D<T, Stride1D.Dense> srcView, long count)
        where T : unmanaged
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        T[] dst = GC.AllocateUninitializedArray<T>((int)count);
        lock (_sync)
        {
            GpuBufferPool.CopyToHostArray(srcView, dst, 0, count);
            _accelerator.Synchronize();
        }

        AddBytesDownloaded(count * ArrayView1D<T, Stride1D.Dense>.ElementSize);
        return dst;
    }

    internal void DownloadSpan<T>(ArrayView1D<T, Stride1D.Dense> srcView, Span<T> dst)
        where T : unmanaged
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        lock (_sync)
        {
            GpuBufferPool.CopyToHostSpan(srcView, dst);
            _accelerator.Synchronize();
        }

        AddBytesDownloaded((long)dst.Length * ArrayView1D<T, Stride1D.Dense>.ElementSize);
    }

    internal void UploadSpanLocked<T>(ArrayView1D<T, Stride1D.Dense> dstView, ReadOnlySpan<T> src)
        where T : unmanaged
    {
        GpuBufferPool.CopyFromHostSpan(dstView, src);
        AddBytesUploaded((long)src.Length * ArrayView1D<T, Stride1D.Dense>.ElementSize);
    }

    internal void UploadMemoryLocked<T>(ArrayView1D<T, Stride1D.Dense> dstView, ReadOnlyMemory<T> src, long count)
        where T : unmanaged
    {
        if (MemoryMarshal.TryGetArray(src, out ArraySegment<T> seg) && seg.Array is not null)
        {
            GpuBufferPool.CopyFromHostArray(dstView, seg.Array, seg.Offset, count);
        }
        else
        {
            GpuBufferPool.CopyFromHostSpan(dstView, src.Span.Slice(0, (int)count));
        }

        AddBytesUploaded(count * ArrayView1D<T, Stride1D.Dense>.ElementSize);
    }

    internal void RecordGpuDispatch() => Interlocked.Increment(ref _gpuDispatches);

    internal void RecordCpuFallback() => Interlocked.Increment(ref _cpuFallbacks);

    internal void AddBytesUploaded(long bytes) => Interlocked.Add(ref _bytesUploaded, bytes);

    internal void AddBytesDownloaded(long bytes) => Interlocked.Add(ref _bytesDownloaded, bytes);

    // =========================================================================
    // Constant VRAM Table Accessors (must be called under _sync)
    // =========================================================================

    internal void GetDxtTablesLocked(
        out ArrayView1D<byte, Stride1D.Dense> omatch5,
        out ArrayView1D<byte, Stride1D.Dense> omatch6,
        out ArrayView1D<float, Stride1D.Dense> midpoints5,
        out ArrayView1D<float, Stride1D.Dense> midpoints6)
    {
        EnsureDxtTablesLocked();
        omatch5 = _dxtOMatch5!.View;
        omatch6 = _dxtOMatch6!.View;
        midpoints5 = _dxtMidpoints5!.View;
        midpoints6 = _dxtMidpoints6!.View;
    }

    private void EnsureDxtTablesLocked()
    {
        if (_dxtOMatch5 is not null)
        {
            return;
        }

        _dxtOMatch5 = _accelerator.Allocate1D(DxtTables.OMatch5);
        _dxtOMatch6 = _accelerator.Allocate1D(DxtTables.OMatch6);
        _dxtMidpoints5 = _accelerator.Allocate1D(DxtTables.Midpoints5);
        _dxtMidpoints6 = _accelerator.Allocate1D(DxtTables.Midpoints6);
    }

    internal void GetPerlinTablesLocked(
        out ArrayView1D<byte, Stride1D.Dense> randTab,
        out ArrayView1D<byte, Stride1D.Dense> randTabGradIdx,
        out ArrayView1D<float, Stride1D.Dense> basisX,
        out ArrayView1D<float, Stride1D.Dense> basisY,
        out ArrayView1D<float, Stride1D.Dense> basisZ)
    {
        EnsurePerlinTablesLocked();
        randTab = _perlinRandTab!.View;
        randTabGradIdx = _perlinRandTabGradIdx!.View;
        basisX = _perlinBasisX!.View;
        basisY = _perlinBasisY!.View;
        basisZ = _perlinBasisZ!.View;
    }

    private void EnsurePerlinTablesLocked()
    {
        if (_perlinRandTab is not null)
        {
            return;
        }

        _perlinRandTab = _accelerator.Allocate1D(PerlinTables.RandTab);
        _perlinRandTabGradIdx = _accelerator.Allocate1D(PerlinTables.RandTabGradIdx);
        _perlinBasisX = _accelerator.Allocate1D(PerlinTables.BasisX);
        _perlinBasisY = _accelerator.Allocate1D(PerlinTables.BasisY);
        _perlinBasisZ = _accelerator.Allocate1D(PerlinTables.BasisZ);
    }

    internal void GetSrgbTablesLocked(
        out ArrayView1D<float, Stride1D.Dense> srgbToLinear,
        out ArrayView1D<uint, Stride1D.Dense> fp32ToSrgbTab)
    {
        EnsureSrgbTablesLocked();
        srgbToLinear = _srgbToLinear!.View;
        fp32ToSrgbTab = _fp32ToSrgbTab!.View;
    }

    private void EnsureSrgbTablesLocked()
    {
        if (_srgbToLinear is not null)
        {
            return;
        }

        _srgbToLinear = _accelerator.Allocate1D(ResizeCoders.SrgbToLinear);
        _fp32ToSrgbTab = _accelerator.Allocate1D(ResizeCoders.Fp32ToSrgb8Tab4);
    }

    // =========================================================================
    // Lazily Compiled Kernel Accessors (must be called under _sync)
    // =========================================================================

    internal Action<Index1D, ArrayView1D<byte, Stride1D.Dense>, ArrayView1D<byte, Stride1D.Dense>, ArrayView1D<byte, Stride1D.Dense>, ArrayView1D<byte, Stride1D.Dense>, ArrayView1D<float, Stride1D.Dense>, ArrayView1D<float, Stride1D.Dense>, DxtImageParams> GetDxtRgbaKernelLocked() =>
        _dxtRgbaKernel ??= _accelerator.LoadAutoGroupedStreamKernel<Index1D, ArrayView1D<byte, Stride1D.Dense>, ArrayView1D<byte, Stride1D.Dense>, ArrayView1D<byte, Stride1D.Dense>, ArrayView1D<byte, Stride1D.Dense>, ArrayView1D<float, Stride1D.Dense>, ArrayView1D<float, Stride1D.Dense>, DxtImageParams>(DxtGpuKernels.CompressRgbaKernel);

    internal Action<Index1D, ArrayView1D<byte, Stride1D.Dense>, ArrayView1D<byte, Stride1D.Dense>, DxtImageParams> GetDxtBc4KernelLocked() =>
        _dxtBc4Kernel ??= _accelerator.LoadAutoGroupedStreamKernel<Index1D, ArrayView1D<byte, Stride1D.Dense>, ArrayView1D<byte, Stride1D.Dense>, DxtImageParams>(DxtGpuKernels.CompressBc4Kernel);

    internal Action<Index1D, ArrayView1D<byte, Stride1D.Dense>, ArrayView1D<byte, Stride1D.Dense>, DxtImageParams> GetDxtBc5KernelLocked() =>
        _dxtBc5Kernel ??= _accelerator.LoadAutoGroupedStreamKernel<Index1D, ArrayView1D<byte, Stride1D.Dense>, ArrayView1D<byte, Stride1D.Dense>, DxtImageParams>(DxtGpuKernels.CompressBc5Kernel);

    internal Action<Index1D, ArrayView1D<float, Stride1D.Dense>, ArrayView1D<byte, Stride1D.Dense>, ArrayView1D<byte, Stride1D.Dense>, ArrayView1D<float, Stride1D.Dense>, ArrayView1D<float, Stride1D.Dense>, ArrayView1D<float, Stride1D.Dense>, Perlin2DParams> GetPerlin2DKernelLocked() =>
        _perlin2DKernel ??= _accelerator.LoadAutoGroupedStreamKernel<Index1D, ArrayView1D<float, Stride1D.Dense>, ArrayView1D<byte, Stride1D.Dense>, ArrayView1D<byte, Stride1D.Dense>, ArrayView1D<float, Stride1D.Dense>, ArrayView1D<float, Stride1D.Dense>, ArrayView1D<float, Stride1D.Dense>, Perlin2DParams>(PerlinGpuKernels.Fill2DKernel);

    internal Action<Index1D, ArrayView1D<float, Stride1D.Dense>, ArrayView1D<byte, Stride1D.Dense>, ArrayView1D<byte, Stride1D.Dense>, ArrayView1D<float, Stride1D.Dense>, ArrayView1D<float, Stride1D.Dense>, ArrayView1D<float, Stride1D.Dense>, Perlin3DParams> GetPerlin3DKernelLocked() =>
        _perlin3DKernel ??= _accelerator.LoadAutoGroupedStreamKernel<Index1D, ArrayView1D<float, Stride1D.Dense>, ArrayView1D<byte, Stride1D.Dense>, ArrayView1D<byte, Stride1D.Dense>, ArrayView1D<float, Stride1D.Dense>, ArrayView1D<float, Stride1D.Dense>, ArrayView1D<float, Stride1D.Dense>, Perlin3DParams>(PerlinGpuKernels.Fill3DKernel);

    internal Action<Index1D, ArrayView1D<byte, Stride1D.Dense>, ArrayView1D<float, Stride1D.Dense>, ArrayView1D<int, Stride1D.Dense>, ArrayView1D<int, Stride1D.Dense>, ArrayView1D<float, Stride1D.Dense>, ArrayView1D<float, Stride1D.Dense>, ResizePassParams> GetResizeHByteKernelLocked() =>
        _resizeHByteKernel ??= _accelerator.LoadAutoGroupedStreamKernel<Index1D, ArrayView1D<byte, Stride1D.Dense>, ArrayView1D<float, Stride1D.Dense>, ArrayView1D<int, Stride1D.Dense>, ArrayView1D<int, Stride1D.Dense>, ArrayView1D<float, Stride1D.Dense>, ArrayView1D<float, Stride1D.Dense>, ResizePassParams>(ResizeGpuKernels.HorizontalByteKernel);

    internal Action<Index1D, ArrayView1D<float, Stride1D.Dense>, ArrayView1D<byte, Stride1D.Dense>, ArrayView1D<int, Stride1D.Dense>, ArrayView1D<int, Stride1D.Dense>, ArrayView1D<float, Stride1D.Dense>, ArrayView1D<uint, Stride1D.Dense>, ResizePassParams> GetResizeVByteKernelLocked() =>
        _resizeVByteKernel ??= _accelerator.LoadAutoGroupedStreamKernel<Index1D, ArrayView1D<float, Stride1D.Dense>, ArrayView1D<byte, Stride1D.Dense>, ArrayView1D<int, Stride1D.Dense>, ArrayView1D<int, Stride1D.Dense>, ArrayView1D<float, Stride1D.Dense>, ArrayView1D<uint, Stride1D.Dense>, ResizePassParams>(ResizeGpuKernels.VerticalByteKernel);

    internal Action<Index1D, ArrayView1D<float, Stride1D.Dense>, ArrayView1D<float, Stride1D.Dense>, ArrayView1D<int, Stride1D.Dense>, ArrayView1D<int, Stride1D.Dense>, ArrayView1D<float, Stride1D.Dense>, ResizePassParams> GetResizeHFloatKernelLocked() =>
        _resizeHFloatKernel ??= _accelerator.LoadAutoGroupedStreamKernel<Index1D, ArrayView1D<float, Stride1D.Dense>, ArrayView1D<float, Stride1D.Dense>, ArrayView1D<int, Stride1D.Dense>, ArrayView1D<int, Stride1D.Dense>, ArrayView1D<float, Stride1D.Dense>, ResizePassParams>(ResizeGpuKernels.HorizontalFloatKernel);

    internal Action<Index1D, ArrayView1D<float, Stride1D.Dense>, ArrayView1D<float, Stride1D.Dense>, ArrayView1D<int, Stride1D.Dense>, ArrayView1D<int, Stride1D.Dense>, ArrayView1D<float, Stride1D.Dense>, ResizePassParams> GetResizeVFloatKernelLocked() =>
        _resizeVFloatKernel ??= _accelerator.LoadAutoGroupedStreamKernel<Index1D, ArrayView1D<float, Stride1D.Dense>, ArrayView1D<float, Stride1D.Dense>, ArrayView1D<int, Stride1D.Dense>, ArrayView1D<int, Stride1D.Dense>, ArrayView1D<float, Stride1D.Dense>, ResizePassParams>(ResizeGpuKernels.VerticalFloatKernel);

    internal Action<Index1D, ArrayView1D<byte, Stride1D.Dense>, ArrayView1D<GpuSdfGlyphDesc, Stride1D.Dense>, ArrayView1D<GpuSdfLine, Stride1D.Dense>, ArrayView1D<GpuSdfCurve, Stride1D.Dense>, ArrayView1D<GpuSdfCrossEdge, Stride1D.Dense>, int> GetSdfBatchKernelLocked() =>
        _sdfBatchKernel ??= _accelerator.LoadAutoGroupedStreamKernel<Index1D, ArrayView1D<byte, Stride1D.Dense>, ArrayView1D<GpuSdfGlyphDesc, Stride1D.Dense>, ArrayView1D<GpuSdfLine, Stride1D.Dense>, ArrayView1D<GpuSdfCurve, Stride1D.Dense>, ArrayView1D<GpuSdfCrossEdge, Stride1D.Dense>, int>(TrueTypeSdfGpuKernels.RenderSdfBatchKernel);

    internal Action<Index1D, ArrayView1D<byte, Stride1D.Dense>, ArrayView1D<byte, Stride1D.Dense>> GetSwapRBKernelLocked() =>
        _swapRBKernel ??= _accelerator.LoadAutoGroupedStreamKernel<Index1D, ArrayView1D<byte, Stride1D.Dense>, ArrayView1D<byte, Stride1D.Dense>>(ColorGpuKernels.SwapRedBlueRgbaKernel);

    internal Action<Index1D, ArrayView1D<byte, Stride1D.Dense>, ArrayView1D<byte, Stride1D.Dense>, byte> GetRgbToRgbaKernelLocked() =>
        _rgbToRgbaKernel ??= _accelerator.LoadAutoGroupedStreamKernel<Index1D, ArrayView1D<byte, Stride1D.Dense>, ArrayView1D<byte, Stride1D.Dense>, byte>(ColorGpuKernels.RgbToRgbaKernel);

    internal Action<Index1D, ArrayView1D<byte, Stride1D.Dense>, ArrayView1D<byte, Stride1D.Dense>> GetRgbaToRgbKernelLocked() =>
        _rgbaToRgbKernel ??= _accelerator.LoadAutoGroupedStreamKernel<Index1D, ArrayView1D<byte, Stride1D.Dense>, ArrayView1D<byte, Stride1D.Dense>>(ColorGpuKernels.RgbaToRgbKernel);

    internal Action<Index1D, ArrayView1D<byte, Stride1D.Dense>, ArrayView1D<byte, Stride1D.Dense>> GetRgbaToGreyKernelLocked() =>
        _rgbaToGreyKernel ??= _accelerator.LoadAutoGroupedStreamKernel<Index1D, ArrayView1D<byte, Stride1D.Dense>, ArrayView1D<byte, Stride1D.Dense>>(ColorGpuKernels.RgbaToGreyKernel);

    internal Action<Index1D, ArrayView1D<byte, Stride1D.Dense>, ArrayView1D<byte, Stride1D.Dense>, byte> GetGreyToRgbaKernelLocked() =>
        _greyToRgbaKernel ??= _accelerator.LoadAutoGroupedStreamKernel<Index1D, ArrayView1D<byte, Stride1D.Dense>, ArrayView1D<byte, Stride1D.Dense>, byte>(ColorGpuKernels.GreyToRgbaKernel);

    internal Action<Index1D, ArrayView1D<byte, Stride1D.Dense>, ArrayView1D<byte, Stride1D.Dense>> GetPremulKernelLocked() =>
        _premulKernel ??= _accelerator.LoadAutoGroupedStreamKernel<Index1D, ArrayView1D<byte, Stride1D.Dense>, ArrayView1D<byte, Stride1D.Dense>>(ColorGpuKernels.PremultiplyAlphaKernel);

    internal Action<Index1D, ArrayView1D<byte, Stride1D.Dense>, ArrayView1D<byte, Stride1D.Dense>> GetUnpremulKernelLocked() =>
        _unpremulKernel ??= _accelerator.LoadAutoGroupedStreamKernel<Index1D, ArrayView1D<byte, Stride1D.Dense>, ArrayView1D<byte, Stride1D.Dense>>(ColorGpuKernels.UnpremultiplyAlphaKernel);

    internal Action<Index1D, ArrayView1D<byte, Stride1D.Dense>, ArrayView1D<float, Stride1D.Dense>, ArrayView1D<float, Stride1D.Dense>, int, int> GetSrgbToLinearKernelLocked() =>
        _srgbToLinearKernel ??= _accelerator.LoadAutoGroupedStreamKernel<Index1D, ArrayView1D<byte, Stride1D.Dense>, ArrayView1D<float, Stride1D.Dense>, ArrayView1D<float, Stride1D.Dense>, int, int>(ColorGpuKernels.SrgbToLinearFp32Kernel);

    internal Action<Index1D, ArrayView1D<float, Stride1D.Dense>, ArrayView1D<byte, Stride1D.Dense>, ArrayView1D<uint, Stride1D.Dense>, int, int> GetLinearToSrgbKernelLocked() =>
        _linearToSrgbKernel ??= _accelerator.LoadAutoGroupedStreamKernel<Index1D, ArrayView1D<float, Stride1D.Dense>, ArrayView1D<byte, Stride1D.Dense>, ArrayView1D<uint, Stride1D.Dense>, int, int>(ColorGpuKernels.LinearFp32ToSrgbKernel);

    internal Action<Index1D, ArrayView1D<float, Stride1D.Dense>, ArrayView1D<byte, Stride1D.Dense>, float, float, byte> GetHeightmapToRgbaKernelLocked() =>
        _heightmapToRgbaKernel ??= _accelerator.LoadAutoGroupedStreamKernel<Index1D, ArrayView1D<float, Stride1D.Dense>, ArrayView1D<byte, Stride1D.Dense>, float, float, byte>(ColorGpuKernels.FloatHeightmapToRgbaKernel);

    internal Action<Index1D, ArrayView1D<float, Stride1D.Dense>, ArrayView1D<byte, Stride1D.Dense>, float, float> GetHeightmapToGreyKernelLocked() =>
        _heightmapToGreyKernel ??= _accelerator.LoadAutoGroupedStreamKernel<Index1D, ArrayView1D<float, Stride1D.Dense>, ArrayView1D<byte, Stride1D.Dense>, float, float>(ColorGpuKernels.FloatHeightmapToGreyKernel);

    private static long ValidateImageDimensions(int width, int height, ColorComponents components, int availableElements)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(width);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(height);

        int channels = (int)components;
        if (channels < 1 || channels > 4)
        {
            throw new ArgumentOutOfRangeException(nameof(components), "Components must specify 1 to 4 channels.");
        }

        long total = (long)width * height * channels;
        if (total > availableElements)
        {
            throw new ArgumentException(
                $"Pixel buffer length ({availableElements}) is smaller than required ({total}).",
                nameof(availableElements));
        }

        return total;
    }

    private static bool DetectLikelyDiscrete(StbGpuBackendKind backend, string deviceName, long memoryBytes)
    {
        if (backend == StbGpuBackendKind.Cuda)
        {
            return true;
        }

        if (backend != StbGpuBackendKind.OpenCL)
        {
            return false;
        }

        string upper = (deviceName ?? string.Empty).ToUpperInvariant();
        if (upper.Contains("UHD", StringComparison.Ordinal)
            || upper.Contains("IRIS", StringComparison.Ordinal)
            || upper.Contains("HD GRAPHICS", StringComparison.Ordinal)
            || upper.Contains("APPLE M", StringComparison.Ordinal))
        {
            return false;
        }

        if (upper.Contains("NVIDIA", StringComparison.Ordinal)
            || upper.Contains("GEFORCE", StringComparison.Ordinal)
            || upper.Contains("RTX", StringComparison.Ordinal)
            || upper.Contains("GTX", StringComparison.Ordinal)
            || upper.Contains("QUADRO", StringComparison.Ordinal)
            || upper.Contains("TESLA", StringComparison.Ordinal)
            || upper.Contains("RADEON RX", StringComparison.Ordinal)
            || upper.Contains("RADEON PRO", StringComparison.Ordinal)
            || upper.Contains("ARC A", StringComparison.Ordinal)
            || upper.Contains("ARC B", StringComparison.Ordinal))
        {
            return true;
        }

        // Heuristic fallback: >= 3 GiB dedicated memory strongly suggests a discrete GPU.
        return memoryBytes >= 3L * 1024 * 1024 * 1024;
    }

    /// <summary>
    /// Releases all pooled VRAM buffers, constant lookup tables, the accelerator, and the ILGPU context.
    /// </summary>
    public void Dispose()
    {
        lock (_sync)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _dxtOMatch5?.Dispose();
            _dxtOMatch6?.Dispose();
            _dxtMidpoints5?.Dispose();
            _dxtMidpoints6?.Dispose();
            _perlinRandTab?.Dispose();
            _perlinRandTabGradIdx?.Dispose();
            _perlinBasisX?.Dispose();
            _perlinBasisY?.Dispose();
            _perlinBasisZ?.Dispose();
            _srgbToLinear?.Dispose();
            _fp32ToSrgbTab?.Dispose();
            _pool.Dispose();
            _accelerator.Dispose();
            _context.Dispose();
        }
    }
}

/// <summary>
/// Process-wide lazily initialized default <see cref="StbGpuContext"/> honoring
/// <c>STB_GPU_MODE</c> (<c>auto</c>, <c>cpu</c>, <c>force</c>) and
/// <c>STB_GPU_BACKEND</c> (<c>auto</c>, <c>cuda</c>, <c>opencl</c>, <c>cpu</c>) environment variables.
/// </summary>
public sealed partial class StbGpuContext
{
    private static readonly object s_lock = new();
    private static StbGpuContext? s_default;
    private static bool s_exitRegistered;

    /// <summary>
    /// Gets the lazily initialized process-wide default <see cref="StbGpuContext"/>.
    /// </summary>
    public static StbGpuContext Default
    {
        get
        {
            StbGpuContext? existing = Volatile.Read(ref s_default);
            if (existing is not null)
            {
                return existing;
            }

            lock (s_lock)
            {
                if (s_default is null)
                {
                    s_default = StbGpuContext.Create(BuildEnvironmentOptions());
                    RegisterProcessExitLocked();
                }

                return s_default;
            }
        }
    }

    /// <summary>
    /// Replaces the process-wide default <see cref="StbGpuContext"/> using <paramref name="options"/>,
    /// disposing any previously created default context.
    /// </summary>
    /// <param name="options">Configuration for the new default context.</param>
    /// <returns>The newly installed default <see cref="StbGpuContext"/>.</returns>
    public static StbGpuContext Configure(StbGpuOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        StbGpuContext created = StbGpuContext.Create(options);
        StbGpuContext? previous;
        lock (s_lock)
        {
            previous = s_default;
            s_default = created;
            RegisterProcessExitLocked();
        }

        previous?.Dispose();
        return created;
    }

    /// <summary>
    /// Disposes and clears the process-wide default context (if initialized).
    /// </summary>
    public static void ShutdownDefault()
    {
        StbGpuContext? previous;
        lock (s_lock)
        {
            previous = s_default;
            s_default = null;
        }

        previous?.Dispose();
    }

    private static void RegisterProcessExitLocked()
    {
        if (s_exitRegistered)
        {
            return;
        }

        s_exitRegistered = true;
        AppDomain.CurrentDomain.ProcessExit += (_, _) => ShutdownDefault();
    }

    private static StbGpuOptions BuildEnvironmentOptions()
    {
        string? modeEnv = Environment.GetEnvironmentVariable("STB_GPU_MODE");
        StbGpuExecutionMode mode = string.IsNullOrWhiteSpace(modeEnv)
            ? StbGpuExecutionMode.Auto
            : modeEnv.Trim().ToUpperInvariant() switch
            {
                "0" or "AUTO" => StbGpuExecutionMode.Auto,
                "1" or "CPU" or "CPUONLY" or "OFF" or "FALSE" or "DISABLED" => StbGpuExecutionMode.ForceCpu,
                "2" or "FORCE" or "FORCEGPU" => StbGpuExecutionMode.ForceGpu,
                _ => StbGpuExecutionMode.Auto,
            };

        string? backendEnv = Environment.GetEnvironmentVariable("STB_GPU_BACKEND");
        StbGpuBackendPreference backend = string.IsNullOrWhiteSpace(backendEnv)
            ? StbGpuBackendPreference.Auto
            : backendEnv.Trim().ToUpperInvariant() switch
            {
                "CUDA" => StbGpuBackendPreference.Cuda,
                "OPENCL" or "CL" => StbGpuBackendPreference.OpenCL,
                "CPU" => StbGpuBackendPreference.Cpu,
                _ => StbGpuBackendPreference.Auto,
            };

        return new StbGpuOptions
        {
            ExecutionMode = mode,
            BackendPreference = backend,
        };
    }
}
