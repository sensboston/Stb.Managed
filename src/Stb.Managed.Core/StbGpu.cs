using System.Diagnostics.CodeAnalysis;
using System.Threading;
using System.Threading.Tasks;

namespace Stb.Managed;

/// <summary>Where GPU-capable operations (resize, DXT compression, Perlin noise) run.</summary>
public enum StbGpuMode
{
    /// <summary>
    /// Default. Uses a hardware GPU (CUDA or OpenCL) when the <c>Stb.Managed.Gpu</c> package is present,
    /// a device is found and the operation is large enough to benefit; otherwise the CPU.
    /// </summary>
    Auto = 0,

    /// <summary>Never uses the GPU.</summary>
    CpuOnly = 1,

    /// <summary>Uses the GPU for every GPU-capable operation regardless of size (the ILGPU CPU accelerator when no hardware GPU exists).</summary>
    ForceGpu = 2,
}

/// <summary>
/// GPU execution policy for the whole process. Nothing has to be configured: with <see cref="StbGpuMode.Auto"/>
/// the GPU package, when installed, is found and initialized in the background on the first GPU-capable call;
/// calls made before it is ready run on the CPU instead of waiting. GPU results can differ from the CPU in the
/// last bit of a float or a rounded 8-bit value.
/// </summary>
public static class StbGpu
{
    private const string ProviderTypeName = "Stb.Managed.Gpu.Internal.StbGpuProvider, Stb.Managed.Gpu";

    private static readonly object s_lock = new();
    private static volatile int s_mode = (int)ReadEnvironmentMode();
    private static volatile IStbGpuProvider? s_provider;
    private static Task<IStbGpuProvider?>? s_init;
    private static volatile bool s_probeFailed;

    /// <summary>
    /// Global GPU mode; the initial value comes from the <c>STB_GPU_MODE</c> environment variable
    /// (<c>auto</c>, <c>cpu</c>, <c>force</c>), <see cref="StbGpuMode.Auto"/> when unset.
    /// </summary>
    public static StbGpuMode Mode
    {
        get => (StbGpuMode)s_mode;
        set
        {
            if (value is not (StbGpuMode.Auto or StbGpuMode.CpuOnly or StbGpuMode.ForceGpu))
            {
                throw new ArgumentOutOfRangeException(nameof(value));
            }

            s_mode = (int)value;
        }
    }

    /// <summary>
    /// Initializes the GPU now and waits for it (driver start-up and kernel compilation take from a few hundred
    /// milliseconds to a few seconds), so later calls do not run on the CPU while it starts. Returns <c>true</c>
    /// when GPU-capable operations can run on a GPU under the current <see cref="Mode"/>.
    /// </summary>
    public static bool Warmup()
    {
        if (Mode == StbGpuMode.CpuOnly)
        {
            return false;
        }

        IStbGpuProvider? provider = StartInitialization()?.GetAwaiter().GetResult();
        return provider is not null && (Mode == StbGpuMode.ForceGpu || provider.IsHardwareGpu);
    }

    /// <summary>Name of the GPU in use, or <c>null</c> when none is initialized.</summary>
    public static string? DeviceName => s_provider?.DeviceName;

    /// <summary>Releases the GPU; it is initialized again on the next GPU-capable call.</summary>
    public static void Shutdown()
    {
        IStbGpuProvider? provider;
        lock (s_lock)
        {
            provider = s_provider;
            s_provider = null;
            s_init = null;
            s_probeFailed = false;
        }

        provider?.Dispose();
    }

    /// <summary>
    /// The provider to try for a GPU-capable operation, or <c>null</c> to run on the CPU (the provider applies size thresholds).
    /// In <see cref="StbGpuMode.Auto"/> it never waits: the first call starts initialization and gets <c>null</c>.
    /// </summary>
    internal static IStbGpuProvider? Acquire()
    {
        StbGpuMode mode = Mode;
        if (mode == StbGpuMode.CpuOnly || s_probeFailed)
        {
            return null;
        }

        IStbGpuProvider? provider = s_provider;
        if (provider is not null)
        {
            return mode == StbGpuMode.ForceGpu || provider.IsHardwareGpu ? provider : null;
        }

        Task<IStbGpuProvider?>? init = StartInitialization();
        if (init is null)
        {
            return null;
        }

        if (mode == StbGpuMode.ForceGpu)
        {
            return init.GetAwaiter().GetResult();
        }

        return null;
    }

    private static Task<IStbGpuProvider?>? StartInitialization()
    {
        if (s_probeFailed)
        {
            return null;
        }

        lock (s_lock)
        {
            if (s_provider is not null)
            {
                return Task.FromResult<IStbGpuProvider?>(s_provider);
            }

            s_init ??= Task.Run(CreateProvider);
            return s_init;
        }
    }

#if NET5_0_OR_GREATER
    [UnconditionalSuppressMessage("Trimming", "IL2057", Justification = "The GPU package is optional; when trimmed away the CPU is used.")]
    [UnconditionalSuppressMessage("Trimming", "IL2072", Justification = "The GPU package is optional; when trimmed away the CPU is used.")]
#endif
    private static IStbGpuProvider? CreateProvider()
    {
        IStbGpuProvider? provider = null;
        try
        {
            Type? type = Type.GetType(ProviderTypeName, throwOnError: false);
            if (type is not null)
            {
                provider = Activator.CreateInstance(type, nonPublic: true) as IStbGpuProvider;
            }
        }
        catch (Exception)
        {
            // No usable GPU runtime (missing driver, unsupported platform): stay on the CPU.
            provider = null;
        }

        lock (s_lock)
        {
            if (provider is null)
            {
                s_probeFailed = true;
            }
            else
            {
                s_provider = provider;
            }
        }

        return provider;
    }

    private static StbGpuMode ReadEnvironmentMode()
    {
        string? value = Environment.GetEnvironmentVariable("STB_GPU_MODE");
        return value?.Trim().ToUpperInvariant() switch
        {
            "1" or "CPU" or "CPUONLY" or "OFF" => StbGpuMode.CpuOnly,
            "2" or "FORCE" or "FORCEGPU" or "GPU" => StbGpuMode.ForceGpu,
            _ => StbGpuMode.Auto,
        };
    }
}

/// <summary>Implemented by the GPU package; each library casts it to its own operation interface.</summary>
internal interface IStbGpuProvider : IDisposable
{
    bool IsHardwareGpu { get; }

    string DeviceName { get; }
}
