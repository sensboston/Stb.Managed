using System.Reflection;
using System.Text.RegularExpressions;
using Stb.Managed.Dxt;
using Stb.Managed.Gpu;
using Xunit;

namespace Stb.Managed.Tests.Gpu;

[Collection(GpuTestSuite.Name)]
public sealed class GpuContextAndArchitectureTests(GpuTestFixture fixture)
{
    private static readonly string[] CoreLibraryAssemblies =
    [
        "Stb.Managed.Core",
        "Stb.Managed.Image",
        "Stb.Managed.ImageWrite",
        "Stb.Managed.ImageResize",
        "Stb.Managed.TrueType",
        "Stb.Managed.RectPack",
        "Stb.Managed.Dxt",
        "Stb.Managed.Perlin",
    ];

    [Fact]
    public void CreateContextAutoSucceedsAndExposesValidDeviceInfo()
    {
        StbGpuDeviceInfo dev = fixture.Context.Device;
        Assert.NotEqual(StbGpuBackendKind.None, dev.Backend);
        Assert.False(string.IsNullOrWhiteSpace(dev.Name));
        Assert.True(dev.MaxNumThreadsPerGroup > 0);
        Assert.True(dev.NumMultiprocessors > 0);
        Assert.Equal(dev.Backend is StbGpuBackendKind.Cuda or StbGpuBackendKind.OpenCL, dev.IsHardwareGpu);
    }

    [Fact]
    public void CreateCpuBackendWarmupAndDisposeIsIdempotent()
    {
        using var ctx = StbGpuContext.Create(new StbGpuOptions
        {
            BackendPreference = StbGpuBackendPreference.Cpu,
            ExecutionMode = StbGpuExecutionMode.Auto,
            MaxPooledVramBytes = 16L * 1024 * 1024,
        });

        Assert.Equal(StbGpuBackendKind.Cpu, ctx.Device.Backend);
        Assert.False(ctx.Device.IsHardwareGpu);
        Assert.False(ctx.Device.IsLikelyDiscrete);
        Assert.Equal(StbGpuThresholds.IntegratedDefaults.DxtMinBlocks, ctx.Thresholds.DxtMinBlocks);

        ctx.Warmup();
        ctx.Dispose();
        ctx.Dispose(); // Must be idempotent.

        Assert.Throws<ObjectDisposedException>(() => ctx.Warmup());
    }

    [Fact]
    public void TryCreateHardwareDoesNotThrowWhenNoHardwareGpuPresent()
    {
        using StbGpuContext? hw = StbGpuContext.TryCreateHardware();
        if (hw is not null)
        {
            Assert.True(hw.Device.IsHardwareGpu);
            Assert.True(hw.Device.Backend is StbGpuBackendKind.Cuda or StbGpuBackendKind.OpenCL);
        }

        // Explicitly requesting Cpu preference must always return null from TryCreateHardware.
        using StbGpuContext? cpuReq = StbGpuContext.TryCreateHardware(new StbGpuOptions
        {
            BackendPreference = StbGpuBackendPreference.Cpu,
        });
        Assert.Null(cpuReq);
    }

    [Fact]
    public void AutoModeThresholdRoutingSelectsCpuBelowThresholdAndForceModesOverride()
    {
        using var cpuCtx = StbGpuContext.Create(new StbGpuOptions
        {
            BackendPreference = StbGpuBackendPreference.Cpu,
            ExecutionMode = StbGpuExecutionMode.Auto,
        });

        // In Auto mode on a non-hardware accelerator, CPU path is always preferred.
        Assert.False(cpuCtx.ShouldUseGpu(workUnits: 10_000_000, threshold: 1));
        // ForceGpu always routes to the ILGPU accelerator (including ILGPU CPU backend for testing).
        Assert.True(cpuCtx.ShouldUseGpu(workUnits: 1, threshold: 10_000_000, StbGpuExecutionMode.ForceGpu));
        // ForceCpu always routes to CPU.
        Assert.False(cpuCtx.ShouldUseGpu(workUnits: 10_000_000, threshold: 1, StbGpuExecutionMode.ForceCpu));

        // On hardware GPU (if available), Auto respects threshold boundaries.
        if (fixture.Context.Device.IsHardwareGpu)
        {
            Assert.False(fixture.Context.ShouldUseGpu(workUnits: 99, threshold: 100, StbGpuExecutionMode.Auto));
            Assert.True(fixture.Context.ShouldUseGpu(workUnits: 100, threshold: 100, StbGpuExecutionMode.Auto));
        }
    }

    [Fact]
    public void StatsTrackDispatchesFallbacksAndTransferredBytes()
    {
        using var ctx = StbGpuContext.Create(new StbGpuOptions
        {
            BackendPreference = StbGpuBackendPreference.Cpu,
            ExecutionMode = StbGpuExecutionMode.Auto,
        });

        byte[] rgba = new byte[32 * 32 * 4];
        for (int i = 0; i < rgba.Length; ++i)
        {
            rgba[i] = (byte)(i * 37);
        }

        // 1. ForceCpu increments CpuFallbacks only.
        byte[] cpuOut = StbGpuDxt.CompressImageToArray(ctx, rgba, 32, 32, DxtFormat.Bc1, modeOverride: StbGpuExecutionMode.ForceCpu);
        Assert.Equal(StbDxt.GetCompressedSize(32, 32, DxtFormat.Bc1), cpuOut.Length);
        StbGpuStats s1 = ctx.Stats;
        Assert.Equal(0, s1.GpuDispatches);
        Assert.Equal(1, s1.CpuFallbacks);
        Assert.Equal(0, s1.BytesUploaded);
        Assert.Equal(0, s1.BytesDownloaded);

        // 2. ForceGpu increments GpuDispatches and tracks uploaded/downloaded bytes.
        byte[] gpuOut = StbGpuDxt.CompressImageToArray(ctx, rgba, 32, 32, DxtFormat.Bc1, modeOverride: StbGpuExecutionMode.ForceGpu);
        Assert.Equal(cpuOut.Length, gpuOut.Length);
        StbGpuStats s2 = ctx.Stats;
        Assert.Equal(1, s2.GpuDispatches);
        Assert.Equal(1, s2.CpuFallbacks);
        Assert.Equal(rgba.Length, s2.BytesUploaded);
        Assert.Equal(gpuOut.Length, s2.BytesDownloaded);

        ctx.ResetStats();
        StbGpuStats s3 = ctx.Stats;
        Assert.Equal(0, s3.GpuDispatches);
        Assert.Equal(0, s3.CpuFallbacks);
        Assert.Equal(0, s3.BytesUploaded);
        Assert.Equal(0, s3.BytesDownloaded);
    }

    [Fact]
    public void VramImageUploadDownloadRoundTripMatchesHostBytesAndFloats()
    {
        byte[] srcBytes = new byte[64 * 48 * 4];
        for (int i = 0; i < srcBytes.Length; ++i)
        {
            srcBytes[i] = (byte)((i * 73 + 19) & 255);
        }

        using (StbGpuImage<byte> img = fixture.Context.Upload(srcBytes, 64, 48, ColorComponents.RedGreenBlueAlpha))
        {
            Assert.Equal(64, img.Width);
            Assert.Equal(48, img.Height);
            Assert.Equal(4, img.ChannelCount);
            Assert.Equal(srcBytes.Length, img.ElementCount);
            Assert.False(img.IsDisposed);

            byte[] roundTrip = img.DownloadToArray();
            Assert.Equal(srcBytes, roundTrip);

            ImageBuffer<byte> buf = img.DownloadToBuffer();
            Assert.Equal(64, buf.Width);
            Assert.Equal(48, buf.Height);
            Assert.Equal(ColorComponents.RedGreenBlueAlpha, buf.Components);
            Assert.True(srcBytes.AsSpan().SequenceEqual(buf.AsReadOnlySpan()));
        }

        float[] srcFloats = new float[32 * 24 * 3];
        for (int i = 0; i < srcFloats.Length; ++i)
        {
            srcFloats[i] = (i % 97) * 0.01f - 0.5f;
        }

        using StbGpuImage<float> fImg = fixture.Context.Upload(srcFloats, 32, 24, ColorComponents.RedGreenBlue);
        float[] dstFloats = new float[srcFloats.Length];
        fImg.DownloadToSpan(dstFloats);
        Assert.Equal(srcFloats, dstFloats);
    }

    [Fact]
    public void FluentVramExtensionsChainHeightmapResizeColorAndDxt()
    {
        using StbGpuImage<float> h128 = StbGpuPerlin.GenerateFbm2DImage(fixture.Context, 128, 128, -1.0f, -1.0f, 0.04f, 0.04f, octaves: 4);
        using StbGpuImage<float> h64 = h128.ResizeOnGpu(64, 64, ResizeFilter.Triangle);
        using StbGpuImage<byte> grey64 = h64.ToGreyOnGpu(-1.0f, 1.0f);
        byte[] bc4 = grey64.CompressDxtOnGpu(DxtFormat.Bc4);
        Assert.Equal(StbDxt.GetCompressedSize(64, 64, DxtFormat.Bc4), bc4.Length);

        // Verify BC4 output from fluent VRAM pipeline matches CPU BC4 on the downloaded grey image (bit-exact!).
        byte[] expectedBc4 = StbDxt.CompressImageToArray(grey64.DownloadToArray(), 64, 64, DxtFormat.Bc4);
        Assert.Equal(expectedBc4, bc4);

        // Also exercise ToRgbaOnGpu -> SwapRedBlueOnGpu -> ResizeOnGpu(byte) -> CompressDxtOnGpu(Bc3).
        using StbGpuImage<byte> rgba = h64.ToRgbaOnGpu(-1.0f, 1.0f);
        using StbGpuImage<byte> bgra = rgba.SwapRedBlueOnGpu();
        using StbGpuImage<byte> resized = bgra.ResizeOnGpu(32, 32, ResizeDataType.UInt8Linear, ResizeFilter.Box);
        byte[] bc3 = resized.CompressDxtOnGpu(DxtFormat.Bc3);
        Assert.Equal(StbDxt.GetCompressedSize(32, 32, DxtFormat.Bc3), bc3.Length);
    }

    [Fact]
    public void ConcurrentOperationsOnSharedContextAreThreadSafeAndDeterministic()
    {
        byte[] srcR = new byte[64 * 64];
        for (int i = 0; i < srcR.Length; ++i)
        {
            srcR[i] = (byte)((i * 53 + 17) & 255);
        }

        byte[] expectedBc4 = StbDxt.CompressImageToArray(srcR, 64, 64, DxtFormat.Bc4);
        float[] expectedNoise = new float[64 * 64];
        StbGpuPerlin.FillNoise2D(fixture.Context, expectedNoise, 64, 64, 0.0f, 0.0f, 0.08f, 0.08f, modeOverride: StbGpuExecutionMode.ForceGpu);

        Parallel.For(0, 8, idx =>
        {
            if ((idx & 1) == 0)
            {
                byte[] actual = StbGpuDxt.CompressImageToArray(fixture.Context, srcR, 64, 64, DxtFormat.Bc4, modeOverride: StbGpuExecutionMode.ForceGpu);
                Assert.Equal(expectedBc4, actual);
            }
            else
            {
                float[] actual = new float[64 * 64];
                StbGpuPerlin.FillNoise2D(fixture.Context, actual, 64, 64, 0.0f, 0.0f, 0.08f, 0.08f, modeOverride: StbGpuExecutionMode.ForceGpu);
                Assert.Equal(expectedNoise, actual);
            }
        });
    }

    [Fact]
    public void StaticDefaultConfigureAndShutdownLifecycleWorks()
    {
        try
        {
            StbGpuContext configured = StbGpuContext.Configure(new StbGpuOptions
            {
                BackendPreference = StbGpuBackendPreference.Cpu,
                ExecutionMode = StbGpuExecutionMode.Auto,
            });
            Assert.Same(configured, StbGpuContext.Default);
            Assert.Equal(StbGpuBackendKind.Cpu, StbGpuContext.Default.Device.Backend);
        }
        finally
        {
            StbGpuContext.ShutdownDefault();
        }
    }

    [Fact]
    public void CorePackagesHaveZeroGpuOrIlgpuDependencyAndGpuLibraryHasZeroUnsafeCode()
    {
        // 1. Verify zero GPU / ILGPU dependency in every core library.
        foreach (string name in CoreLibraryAssemblies)
        {
            string dllPath = Path.Combine(AppContext.BaseDirectory, name + ".dll");
            Assert.True(File.Exists(dllPath), $"Expected assembly '{dllPath}'.");
            Assembly asm = Assembly.LoadFrom(dllPath);
            foreach (AssemblyName refAsm in asm.GetReferencedAssemblies())
            {
                string refName = refAsm.Name ?? string.Empty;
                Assert.False(
                    refName.StartsWith("ILGPU", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(refName, "Stb.Managed.Gpu", StringComparison.OrdinalIgnoreCase),
                    $"Core assembly '{name}' must never depend on '{refName}'.");
            }
        }

        // 2. Verify Stb.Managed.Gpu has zero 'unsafe' blocks across all source files.
        string? repoRoot = FindRepoRoot();
        if (repoRoot is not null)
        {
            string gpuSrcDir = Path.Combine(repoRoot, "src", "Stb.Managed.Gpu");
            Assert.True(Directory.Exists(gpuSrcDir));
            string[] csFiles = Directory.GetFiles(gpuSrcDir, "*.cs", SearchOption.AllDirectories);
            Assert.NotEmpty(csFiles);

            foreach (string file in csFiles)
            {
                string[] lines = File.ReadAllLines(file);
                for (int i = 0; i < lines.Length; ++i)
                {
                    string code = StripLineComment(lines[i]);
                    Assert.False(
                        Regex.IsMatch(code, @"\bunsafe\b"),
                        $"Forbidden 'unsafe' keyword in '{file}:{i + 1}': {lines[i]}");
                    Assert.False(
                        Regex.IsMatch(code, @"\bfixed\s*\("),
                        $"Forbidden 'fixed (...)' statement in '{file}:{i + 1}': {lines[i]}");
                }
            }
        }
    }

    private static string StripLineComment(string line)
    {
        int idx = line.IndexOf("//", StringComparison.Ordinal);
        return idx >= 0 ? line[..idx] : line;
    }

    private static string? FindRepoRoot()
    {
        DirectoryInfo? dir = new(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "Stb.Managed.sln")))
            {
                return dir.FullName;
            }

            dir = dir.Parent;
        }

        return null;
    }
}
