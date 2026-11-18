using System.Diagnostics;
using System.Globalization;
using Stb.Managed;
using Stb.Managed.Dxt;
using Stb.Managed.Gpu;
using Stb.Managed.Perlin;
using Stb.Managed.TrueType;

if (args.Length == 0 || args[0] is "-h" or "--help")
{
    PrintUsage();
    return 0;
}

try
{
    return args[0] switch
    {
        "info" => RunInfo(),
        "pipeline" => RunPipeline(args),
        "sdf" => RunSdf(args),
        "bench" => RunQuickBench(args),
        "--self-test" => RunSelfTest(args.Length > 1 ? args[1] : null),
        _ => UnknownCommand(args[0]),
    };
}
catch (Exception ex)
{
    Console.Error.WriteLine($"Error: {ex.Message}");
    return 1;
}

static void PrintUsage()
{
    Console.WriteLine("Stb.Managed.Samples.GpuDemo — ILGPU hardware-accelerated pipeline demo");
    Console.WriteLine("Usage:");
    Console.WriteLine("  info                                  Show detected GPU accelerator and thresholds");
    Console.WriteLine("  pipeline <out.png> [size=512]         Run a device-resident Perlin->RGBA->Resize->DXT pipeline");
    Console.WriteLine("  sdf <font.ttf> <out.png> [px=48]      Generate 95-glyph ASCII SDF atlas on GPU");
    Console.WriteLine("  bench [size=1024]                     Quick GPU vs Managed CPU (Auto-Par) comparison");
    Console.WriteLine("  --self-test [font.ttf]                Run non-interactive GPU verification suite");
}

static int UnknownCommand(string cmd)
{
    Console.Error.WriteLine($"Unknown command '{cmd}'.");
    PrintUsage();
    return 2;
}

static int RunInfo()
{
    using StbGpuContext gpu = StbGpuContext.Create();
    StbGpuDeviceInfo d = gpu.Device;
    Console.WriteLine($"Accelerator : {d.Name}");
    Console.WriteLine($"Backend     : {d.Backend} (HardwareGpu={d.IsHardwareGpu}, LikelyDiscrete={d.IsLikelyDiscrete})");
    Console.WriteLine($"Memory      : {d.MemoryBytes / (1024.0 * 1024.0):F1} MiB");
    Console.WriteLine($"Compute     : {d.NumMultiprocessors} MPs, {d.MaxNumThreadsPerGroup} max threads/group");
    Console.WriteLine($"Thresholds  : DxtBlocks>={gpu.Thresholds.DxtMinBlocks}, PerlinOctaves>={gpu.Thresholds.PerlinMinSampleOctaves}, ResizePx>={gpu.Thresholds.ResizeMinOutputPixels}, SdfWork>={gpu.Thresholds.SdfMinPixelSegments}, ColorPx>={gpu.Thresholds.ColorConvertMinPixels}");
    return 0;
}

static int RunPipeline(string[] args)
{
    if (args.Length < 2)
    {
        Console.Error.WriteLine("Usage: pipeline <out.png> [size=512]");
        return 2;
    }

    string outPng = args[1];
    int size = args.Length >= 3 ? int.Parse(args[2], CultureInfo.InvariantCulture) : 512;
    int half = Math.Max(4, size / 2);

    using StbGpuContext gpu = StbGpuContext.Create(new StbGpuOptions { ExecutionMode = StbGpuExecutionMode.ForceGpu });
    gpu.Warmup();
    gpu.ResetStats();

    var sw = Stopwatch.StartNew();
    using StbGpuImage<float> heightmap = StbGpuPerlin.GenerateFbm2DImage(gpu, size, size, -3.0f, -3.0f, 6.0f / size, 6.0f / size, octaves: 6);
    using StbGpuImage<byte> rgba = heightmap.ToRgbaOnGpu(minVal: -1.0f, maxVal: 1.0f);
    using StbGpuImage<byte> resized = rgba.ResizeOnGpu(half, half, ResizeDataType.UInt8Srgb, ResizeFilter.Mitchell);
    byte[] dxt = resized.CompressDxtOnGpu(DxtFormat.Bc3, DxtCompressionMode.HighQuality);
    ImageBuffer<byte> preview = resized.DownloadToBuffer();
    sw.Stop();

    byte[] png = StbImageWrite.WritePng(preview);
    File.WriteAllBytes(outPng, png);

    StbGpuStats st = gpu.Stats;
    Console.WriteLine($"VRAM pipeline ({gpu.Device.Backend} '{gpu.Device.Name}') completed in {sw.Elapsed.TotalMilliseconds:F2} ms:");
    Console.WriteLine($"  Perlin fBm6 {size}x{size} -> RGBA -> Resize {half}x{half} -> BC3 ({dxt.Length:N0} B) -> PNG '{outPng}' ({png.Length:N0} B)");
    Console.WriteLine($"  Stats: {st.GpuDispatches} GPU dispatches, {st.BytesUploaded:N0} B uploaded, {st.BytesDownloaded:N0} B downloaded");
    return 0;
}

static int RunSdf(string[] args)
{
    if (args.Length < 3)
    {
        Console.Error.WriteLine("Usage: sdf <font.ttf> <out.png> [pixelHeight=48]");
        return 2;
    }

    string fontPath = args[1];
    string outPng = args[2];
    float px = args.Length >= 4 ? float.Parse(args[3], CultureInfo.InvariantCulture) : 48.0f;

    StbTrueTypeFont font = StbTrueTypeFont.Load(File.ReadAllBytes(fontPath));
    int[] ascii = Enumerable.Range(32, 95).ToArray();

    using StbGpuContext gpu = StbGpuContext.Create(new StbGpuOptions { ExecutionMode = StbGpuExecutionMode.ForceGpu });
    gpu.Warmup();

    var sw = Stopwatch.StartNew();
    GpuSdfAtlas atlas = StbGpuTrueType.GenerateSdfAtlas(gpu, font, px, ascii, 512, 512, padding: 5, onEdgeValue: 180, pixelDistScale: 36.0f);
    sw.Stop();

    byte[] png = StbImageWrite.WritePng(atlas.Pixels, atlas.Width, atlas.Height, ColorComponents.Grey);
    File.WriteAllBytes(outPng, png);

    Console.WriteLine($"GPU SDF atlas ({gpu.Device.Backend}): 95 ASCII glyphs at {px:F0}px -> {atlas.Width}x{atlas.Height} (AllPacked={atlas.AllPacked}) in {sw.Elapsed.TotalMilliseconds:F2} ms -> '{outPng}' ({png.Length:N0} B)");
    return 0;
}

static int RunQuickBench(string[] args)
{
    int size = args.Length >= 2 ? int.Parse(args[1], CultureInfo.InvariantCulture) : 1024;
    using StbGpuContext gpu = StbGpuContext.Create(new StbGpuOptions { ExecutionMode = StbGpuExecutionMode.ForceGpu });
    gpu.Warmup();

    Console.WriteLine($"Quick benchmark ({size}x{size}) on {gpu.Device.Backend}: '{gpu.Device.Name}' (HardwareGpu={gpu.Device.IsHardwareGpu})");

    var autoPar = new StbOptions { MaxDegreeOfParallelism = 0 };
    byte[] rgba = new byte[size * size * 4];
    new Random(42).NextBytes(rgba);
    byte[] dxtOut = new byte[StbDxt.GetCompressedSize(size, size, DxtFormat.Bc3)];

    double cpuDxtMs = MeasureMs(() => StbDxt.CompressImageMemory(rgba, size, size, 0, dxtOut, DxtFormat.Bc3, DxtCompressionMode.Normal, autoPar));
    double gpuDxtMs = MeasureMs(() => StbGpuDxt.CompressImage(gpu, rgba, size, size, dxtOut, DxtFormat.Bc3, 0, DxtCompressionMode.Normal, StbGpuExecutionMode.ForceGpu));
    using StbGpuImage<byte> vramRgba = gpu.Upload(rgba, size, size, ColorComponents.RedGreenBlueAlpha);
    double vramDxtMs = MeasureMs(() => _ = StbGpuDxt.CompressGpuImage(gpu, vramRgba, DxtFormat.Bc3, DxtCompressionMode.Normal));
    Console.WriteLine($"  DXT BC3       : CPU(Auto-Par)={cpuDxtMs,7:F2} ms | GPU(E2E)={gpuDxtMs,7:F2} ms | GPU(VRAM)={vramDxtMs,7:F2} ms");

    float[] noise = new float[size * size];
    double cpuNoiseMs = MeasureMs(() => StbPerlin.FillFbm2DMemory(noise, size, size, -2f, -2f, 0.01f, 0.01f, 0.5f, 2f, 0.5f, 6, 0, autoPar));
    double gpuNoiseMs = MeasureMs(() => StbGpuPerlin.FillFbm2D(gpu, noise, size, size, -2f, -2f, 0.01f, 0.01f, 0.5f, 2f, 0.5f, 6, StbGpuExecutionMode.ForceGpu));
    Console.WriteLine($"  Perlin fBm6   : CPU(Auto-Par)={cpuNoiseMs,7:F2} ms | GPU(E2E)={gpuNoiseMs,7:F2} ms");

    int half = size / 2;
    byte[] resized = new byte[half * half * 4];
    StbImageResizer cpuResizer = StbImageResizer.Create(size, size, half, half, ResizePixelLayout.Rgba, ResizeDataType.UInt8Srgb, new ImageResizeOptions { Options = autoPar });
    using StbGpuResizePlan gpuPlan = StbGpuResizePlan.Create(gpu, size, size, half, half, ResizePixelLayout.Rgba, ResizeDataType.UInt8Srgb);
    double cpuResizeMs = MeasureMs(() => cpuResizer.Resize(rgba, resized));
    double gpuResizeMs = MeasureMs(() => gpuPlan.Resize(rgba, resized, StbGpuExecutionMode.ForceGpu));
    Console.WriteLine($"  Resize sRGB   : CPU(Auto-Par)={cpuResizeMs,7:F2} ms | GPU(E2E)={gpuResizeMs,7:F2} ms");
    return 0;
}

static double MeasureMs(Action action)
{
    action(); // warmup
    const int iters = 3;
    var sw = Stopwatch.StartNew();
    for (int i = 0; i < iters; ++i)
    {
        action();
    }

    sw.Stop();
    return sw.Elapsed.TotalMilliseconds / iters;
}

static int RunSelfTest(string? fontPath)
{
    using StbGpuContext gpu = StbGpuContext.Create(new StbGpuOptions { ExecutionMode = StbGpuExecutionMode.ForceGpu });
    gpu.Warmup();
    gpu.ResetStats();

    // 1. Device-resident pipeline: Perlin fBm -> Grey -> Resize -> BC4 (checked bit-exact against CPU BC4).
    using StbGpuImage<float> heightmap = StbGpuPerlin.GenerateFbm2DImage(gpu, 128, 128, -2.0f, -2.0f, 0.05f, 0.05f, octaves: 5);
    using StbGpuImage<byte> grey = heightmap.ToGreyOnGpu(-1.0f, 1.0f);
    using StbGpuImage<byte> halfGrey = grey.ResizeOnGpu(64, 64, ResizeDataType.UInt8Linear, ResizeFilter.Triangle);
    byte[] gpuBc4 = halfGrey.CompressDxtOnGpu(DxtFormat.Bc4);
    byte[] cpuBc4 = StbDxt.CompressImageToArray(halfGrey.DownloadToArray(), 64, 64, DxtFormat.Bc4);
    if (!gpuBc4.AsSpan().SequenceEqual(cpuBc4))
    {
        throw new InvalidOperationException("GpuDemo self-test: VRAM BC4 mismatch vs CPU.");
    }

    // 2. PNG write + decode round-trip of VRAM heightmap.
    using StbGpuImage<byte> rgba = heightmap.ToRgbaOnGpu(-1.0f, 1.0f);
    byte[] png = StbImageWrite.WritePng(rgba.DownloadToBuffer());
    ImageBuffer<byte> decoded = StbImage.Load(png, ColorComponents.RedGreenBlueAlpha);
    if (decoded.Width != 128 || decoded.Height != 128)
    {
        throw new InvalidOperationException($"GpuDemo self-test: unexpected PNG decode dimensions {decoded.Width}x{decoded.Height}.");
    }

    // 3. Optional GPU SDF atlas test when font path is supplied.
    if (!string.IsNullOrEmpty(fontPath) && File.Exists(fontPath))
    {
        StbTrueTypeFont font = StbTrueTypeFont.Load(File.ReadAllBytes(fontPath));
        int[] ascii = Enumerable.Range(32, 95).ToArray();
        GpuSdfAtlas atlas = StbGpuTrueType.GenerateSdfAtlas(gpu, font, 40.0f, ascii, 512, 512, padding: 4);
        if (!atlas.AllPacked || atlas.Pixels.Length != 512 * 512)
        {
            throw new InvalidOperationException("GpuDemo self-test: SDF atlas failed to pack.");
        }
    }

    StbGpuStats st = gpu.Stats;
    if (st.GpuDispatches < 4)
    {
        throw new InvalidOperationException($"GpuDemo self-test: expected >= 4 GPU dispatches, got {st.GpuDispatches}.");
    }

    Console.WriteLine($"GpuDemo self-test OK ({gpu.Device.Backend} '{gpu.Device.Name}', {st.GpuDispatches} GPU dispatches, BC4 bit-exact).");
    return 0;
}
