namespace Stb.Managed.Gpu;

/// <summary>
/// Execution backend family reported by an active <see cref="StbGpuContext"/>.
/// </summary>
public enum StbGpuBackendKind
{
    /// <summary>No accelerator active.</summary>
    None = 0,

    /// <summary>ILGPU managed CPU accelerator (ideal for deterministic CI verification and debugging).</summary>
    Cpu = 1,

    /// <summary>OpenCL GPU accelerator (AMD, Intel, Apple, or NVIDIA via OpenCL).</summary>
    OpenCL = 2,

    /// <summary>NVIDIA CUDA GPU accelerator.</summary>
    Cuda = 3,
}

/// <summary>
/// Preferred backend discovery order when creating a <see cref="StbGpuContext"/>.
/// </summary>
public enum StbGpuBackendPreference
{
    /// <summary>Probe NVIDIA CUDA first, then OpenCL, then fall back to ILGPU CPU.</summary>
    Auto = 0,

    /// <summary>Require an NVIDIA CUDA device.</summary>
    Cuda = 1,

    /// <summary>Require an OpenCL device.</summary>
    OpenCL = 2,

    /// <summary>Use ILGPU's managed CPU accelerator (always available without GPU drivers).</summary>
    Cpu = 3,
}

/// <summary>
/// Execution routing policy controlling when operations dispatch to GPU vs CPU.
/// </summary>
public enum StbGpuExecutionMode
{
    /// <summary>
    /// Automatically dispatches to a hardware GPU when one is available and the workload
    /// meets the configured size/compute thresholds; otherwise runs on the SIMD/parallel CPU path.
    /// </summary>
    Auto = 0,

    /// <summary>
    /// Always runs on the managed CPU path without transferring data to or from the GPU.
    /// </summary>
    ForceCpu = 1,

    /// <summary>
    /// Always dispatches supported operations through the ILGPU kernels regardless of size
    /// thresholds (including on the ILGPU CPU accelerator when no hardware GPU is present).
    /// </summary>
    ForceGpu = 2,
}

/// <summary>
/// Hardware and capability description for an <see cref="StbGpuContext"/> accelerator.
/// </summary>
/// <param name="Name">Human-readable device name reported by the driver.</param>
/// <param name="Backend">Active backend family (CUDA, OpenCL, or CPU).</param>
/// <param name="MemoryBytes">Reported device memory size in bytes.</param>
/// <param name="MaxNumThreadsPerGroup">Maximum threads per workgroup/block.</param>
/// <param name="NumMultiprocessors">Number of streaming multiprocessors / compute units.</param>
/// <param name="IsHardwareGpu"><see langword="true"/> when backed by CUDA or OpenCL hardware.</param>
/// <param name="IsLikelyDiscrete"><see langword="true"/> when heuristics identify a discrete GPU rather than an integrated GPU.</param>
public readonly record struct StbGpuDeviceInfo(
    string Name,
    StbGpuBackendKind Backend,
    long MemoryBytes,
    int MaxNumThreadsPerGroup,
    int NumMultiprocessors,
    bool IsHardwareGpu,
    bool IsLikelyDiscrete);

/// <summary>
/// Cumulative dispatch and host-device transfer statistics recorded by a <see cref="StbGpuContext"/>.
/// </summary>
/// <param name="GpuDispatches">Total number of GPU kernel dispatches executed.</param>
/// <param name="CpuFallbacks">Total number of calls routed to the CPU fallback path.</param>
/// <param name="BytesUploaded">Total bytes transferred from host memory to device VRAM.</param>
/// <param name="BytesDownloaded">Total bytes transferred from device VRAM to host memory.</param>
public readonly record struct StbGpuStats(
    long GpuDispatches,
    long CpuFallbacks,
    long BytesUploaded,
    long BytesDownloaded);

/// <summary>
/// Workload size thresholds governing when <see cref="StbGpuExecutionMode.Auto"/> selects GPU execution
/// over the SIMD/parallel managed CPU path.
/// </summary>
public sealed class StbGpuThresholds
{
    /// <summary>
    /// Minimum number of 4×4 blocks for automatic GPU DXT/BC compression
    /// (default: 4,096 blocks = 256×256 on discrete GPUs).
    /// </summary>
    public long DxtMinBlocks { get; init; } = 4_096;

    /// <summary>
    /// Minimum sample count × octaves for automatic GPU Perlin noise field generation
    /// (default: 65,536 sample-octaves = 256×256 single-octave or 128×128 4-octave fBm).
    /// </summary>
    public long PerlinMinSampleOctaves { get; init; } = 65_536;

    /// <summary>
    /// Minimum output pixel count for automatic GPU separable image resizing
    /// (default: 262,144 pixels = 512×512).
    /// </summary>
    public long ResizeMinOutputPixels { get; init; } = 262_144;

    /// <summary>
    /// Minimum pixel × segment work units for automatic GPU TrueType SDF rendering
    /// (default: 100,000 pixel-segments, e.g., ~20 glyphs at 32–48 px).
    /// </summary>
    public long SdfMinPixelSegments { get; init; } = 100_000;

    /// <summary>
    /// Minimum pixel count for standalone host-to-GPU-to-host color conversions
    /// (default: 1,048,576 pixels = 1024×1024). VRAM-resident conversions always run on GPU.
    /// </summary>
    public long ColorConvertMinPixels { get; init; } = 1_048_576;

    /// <summary>
    /// Tuned defaults for discrete GPUs (e.g. NVIDIA GeForce/RTX/GTX/Tesla, AMD Radeon RX).
    /// </summary>
    public static StbGpuThresholds DiscreteDefaults { get; } = new();

    /// <summary>
    /// More conservative defaults for integrated GPUs where shared memory bandwidth reduces
    /// the advantage over AVX2/AdvSimd multi-core CPUs on small images.
    /// </summary>
    public static StbGpuThresholds IntegratedDefaults { get; } = new()
    {
        DxtMinBlocks = 16_384,
        PerlinMinSampleOctaves = 262_144,
        ResizeMinOutputPixels = 1_048_576,
        SdfMinPixelSegments = 400_000,
        ColorConvertMinPixels = 4_194_304,
    };
}

/// <summary>
/// Configuration options used to construct a <see cref="StbGpuContext"/>.
/// </summary>
public sealed class StbGpuOptions
{
    /// <summary>Gets or sets the preferred backend discovery order. Defaults to <see cref="StbGpuBackendPreference.Auto"/>.</summary>
    public StbGpuBackendPreference BackendPreference { get; init; } = StbGpuBackendPreference.Auto;

    /// <summary>Gets or sets the execution routing mode. Defaults to <see cref="StbGpuExecutionMode.Auto"/>.</summary>
    public StbGpuExecutionMode ExecutionMode { get; init; } = StbGpuExecutionMode.Auto;

    /// <summary>Gets or sets the maximum bytes retained in the pooled VRAM allocator. Defaults to 256 MiB.</summary>
    public long MaxPooledVramBytes { get; init; } = 256L * 1024 * 1024;

    /// <summary>
    /// Gets or sets custom auto-dispatch thresholds. When <see langword="null"/>, automatically chooses
    /// <see cref="StbGpuThresholds.DiscreteDefaults"/> or <see cref="StbGpuThresholds.IntegratedDefaults"/>
    /// based on the detected GPU device.
    /// </summary>
    public StbGpuThresholds? Thresholds { get; init; }

    /// <summary>Gets or sets the <see cref="StbOptions"/> forwarded to CPU fallback paths.</summary>
    public StbOptions? CpuOptions { get; init; }

    /// <summary>Gets a shared default instance.</summary>
    public static StbGpuOptions Default { get; } = new();
}
