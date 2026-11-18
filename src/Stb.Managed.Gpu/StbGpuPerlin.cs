using Stb.Managed.Gpu.Internal;
using Stb.Managed.Gpu.Kernels;
using Stb.Managed.Perlin;

namespace Stb.Managed.Gpu;

/// <summary>
/// GPU-accelerated 2D and 3D Perlin noise, fBm, ridge, and turbulence field generator backed by ILGPU,
/// with zero per-call input uploads and automatic CPU fallback via <see cref="StbPerlin"/>.
/// </summary>
public static class StbGpuPerlin
{
    /// <summary>
    /// Fills a 2D float grid with single-octave 3D Perlin noise at slice <paramref name="z"/>.
    /// </summary>
    public static void FillNoise2D(
        StbGpuContext context,
        Memory<float> destination,
        int width,
        int height,
        float startX,
        float startY,
        float stepX,
        float stepY,
        float z = 0.0f,
        int xWrap = 0,
        int yWrap = 0,
        int zWrap = 0,
        int seed = 0,
        StbGpuExecutionMode? modeOverride = null)
    {
        ArgumentNullException.ThrowIfNull(context);
        long samples = Validate2D(destination.Length, width, height);

        if (!context.ShouldUseGpu(samples, context.Thresholds.PerlinMinSampleOctaves, modeOverride))
        {
            context.RecordCpuFallback();
            StbPerlin.FillNoise2DMemory(destination, width, height, startX, startY, stepX, stepY, z, xWrap, yWrap, zWrap, seed, 0, context.CpuOptions);
            return;
        }

        var p = new Perlin2DParams(width, height, startX, startY, stepX, stepY, z, 2.0f, 0.5f, 1.0f, 1, xWrap, yWrap, zWrap, seed, mode: 0);
        Run2DToHost(context, destination.Span, samples, p);
    }

    /// <summary>
    /// Fills a 2D float grid with multi-octave fBm Perlin noise.
    /// </summary>
    public static void FillFbm2D(
        StbGpuContext context,
        Memory<float> destination,
        int width,
        int height,
        float startX,
        float startY,
        float stepX,
        float stepY,
        float z = 0.0f,
        float lacunarity = 2.0f,
        float gain = 0.5f,
        int octaves = 6,
        StbGpuExecutionMode? modeOverride = null)
    {
        ArgumentNullException.ThrowIfNull(context);
        long samples = Validate2D(destination.Length, width, height);
        long work = samples * Math.Max(1, octaves);

        if (!context.ShouldUseGpu(work, context.Thresholds.PerlinMinSampleOctaves, modeOverride))
        {
            context.RecordCpuFallback();
            StbPerlin.FillFbm2DMemory(destination, width, height, startX, startY, stepX, stepY, z, lacunarity, gain, octaves, 0, context.CpuOptions);
            return;
        }

        var p = new Perlin2DParams(width, height, startX, startY, stepX, stepY, z, lacunarity, gain, 1.0f, octaves, 0, 0, 0, 0, mode: 1);
        Run2DToHost(context, destination.Span, samples, p);
    }

    /// <summary>
    /// Fills a 2D float grid with multi-octave ridged multifractal Perlin noise.
    /// </summary>
    public static void FillRidge2D(
        StbGpuContext context,
        Memory<float> destination,
        int width,
        int height,
        float startX,
        float startY,
        float stepX,
        float stepY,
        float z = 0.0f,
        float lacunarity = 2.0f,
        float gain = 0.5f,
        float offset = 1.0f,
        int octaves = 6,
        StbGpuExecutionMode? modeOverride = null)
    {
        ArgumentNullException.ThrowIfNull(context);
        long samples = Validate2D(destination.Length, width, height);
        long work = samples * Math.Max(1, octaves);

        if (!context.ShouldUseGpu(work, context.Thresholds.PerlinMinSampleOctaves, modeOverride))
        {
            context.RecordCpuFallback();
            StbPerlin.FillRidge2DMemory(destination, width, height, startX, startY, stepX, stepY, z, lacunarity, gain, offset, octaves, 0, context.CpuOptions);
            return;
        }

        var p = new Perlin2DParams(width, height, startX, startY, stepX, stepY, z, lacunarity, gain, offset, octaves, 0, 0, 0, 0, mode: 2);
        Run2DToHost(context, destination.Span, samples, p);
    }

    /// <summary>
    /// Fills a 2D float grid with multi-octave turbulence Perlin noise.
    /// </summary>
    public static void FillTurbulence2D(
        StbGpuContext context,
        Memory<float> destination,
        int width,
        int height,
        float startX,
        float startY,
        float stepX,
        float stepY,
        float z = 0.0f,
        float lacunarity = 2.0f,
        float gain = 0.5f,
        int octaves = 6,
        StbGpuExecutionMode? modeOverride = null)
    {
        ArgumentNullException.ThrowIfNull(context);
        long samples = Validate2D(destination.Length, width, height);
        long work = samples * Math.Max(1, octaves);

        if (!context.ShouldUseGpu(work, context.Thresholds.PerlinMinSampleOctaves, modeOverride))
        {
            context.RecordCpuFallback();
            StbPerlin.FillTurbulence2DMemory(destination, width, height, startX, startY, stepX, stepY, z, lacunarity, gain, octaves, 0, context.CpuOptions);
            return;
        }

        var p = new Perlin2DParams(width, height, startX, startY, stepX, stepY, z, lacunarity, gain, 1.0f, octaves, 0, 0, 0, 0, mode: 3);
        Run2DToHost(context, destination.Span, samples, p);
    }

    /// <summary>
    /// Generates a 2D fBm heightmap directly into a VRAM-resident <see cref="StbGpuImage{T}"/>
    /// (zero host transfer — ready to chain into <see cref="StbGpuColor.FloatHeightmapToRgbaGpuImage"/>, resize, or DXT).
    /// </summary>
    public static StbGpuImage<float> GenerateFbm2DImage(
        StbGpuContext context,
        int width,
        int height,
        float startX,
        float startY,
        float stepX,
        float stepY,
        float z = 0.0f,
        float lacunarity = 2.0f,
        float gain = 0.5f,
        int octaves = 6)
    {
        ArgumentNullException.ThrowIfNull(context);
        long samples = Validate2D(int.MaxValue, width, height);
        var p = new Perlin2DParams(width, height, startX, startY, stepX, stepY, z, lacunarity, gain, 1.0f, octaves, 0, 0, 0, 0, mode: 1);
        return Run2DToVram(context, width, height, samples, p);
    }

    /// <summary>
    /// Generates a 2D single-octave Perlin noise field directly into a VRAM-resident <see cref="StbGpuImage{T}"/>.
    /// </summary>
    public static StbGpuImage<float> GenerateNoise2DImage(
        StbGpuContext context,
        int width,
        int height,
        float startX,
        float startY,
        float stepX,
        float stepY,
        float z = 0.0f,
        int xWrap = 0,
        int yWrap = 0,
        int zWrap = 0,
        int seed = 0)
    {
        ArgumentNullException.ThrowIfNull(context);
        long samples = Validate2D(int.MaxValue, width, height);
        var p = new Perlin2DParams(width, height, startX, startY, stepX, stepY, z, 2.0f, 0.5f, 1.0f, 1, xWrap, yWrap, zWrap, seed, mode: 0);
        return Run2DToVram(context, width, height, samples, p);
    }

    /// <summary>
    /// Fills a 3D float volume (<c>depth * height * width</c>) with Perlin noise or fBm on GPU
    /// (or deterministic CPU fallback).
    /// </summary>
    public static void FillVolume3D(
        StbGpuContext context,
        Memory<float> destination,
        int width,
        int height,
        int depth,
        float startX,
        float startY,
        float startZ,
        float stepX,
        float stepY,
        float stepZ,
        float lacunarity = 2.0f,
        float gain = 0.5f,
        int octaves = 1,
        int xWrap = 0,
        int yWrap = 0,
        int zWrap = 0,
        int seed = 0,
        StbGpuExecutionMode? modeOverride = null)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (width <= 0 || height <= 0 || depth <= 0)
        {
            throw new StbException(StbErrorCode.InvalidArgument, "3D volume dimensions must be positive.");
        }

        long total = (long)width * height * depth;
        if (destination.Length < total)
        {
            throw new StbException(StbErrorCode.InvalidArgument, $"Destination buffer ({destination.Length}) is smaller than volume size ({total}).");
        }

        int effOctaves = Math.Max(1, octaves);
        long work = total * effOctaves;

        if (!context.ShouldUseGpu(work, context.Thresholds.PerlinMinSampleOctaves, modeOverride))
        {
            context.RecordCpuFallback();
            int sliceLen = width * height;
            for (int iz = 0; iz < depth; ++iz)
            {
                float zVal = startZ + iz * stepZ;
                Memory<float> slice = destination.Slice(iz * sliceLen, sliceLen);
                if (effOctaves == 1)
                {
                    StbPerlin.FillNoise2DMemory(slice, width, height, startX, startY, stepX, stepY, zVal, xWrap, yWrap, zWrap, seed, 0, context.CpuOptions);
                }
                else
                {
                    StbPerlin.FillFbm2DMemory(slice, width, height, startX, startY, stepX, stepY, zVal, lacunarity, gain, effOctaves, 0, context.CpuOptions);
                }
            }

            return;
        }

        var p = new Perlin3DParams(width, height, depth, startX, startY, startZ, stepX, stepY, stepZ, lacunarity, gain, effOctaves, xWrap, yWrap, zWrap, seed);
        using PooledGpuBuffer<float> dstBuf = context.Pool.RentFloat32(total);
        lock (context.SyncRoot)
        {
            context.GetPerlinTablesLocked(out var rt, out var gi, out var bx, out var by, out var bz);
            var kernel = context.GetPerlin3DKernelLocked();
            kernel((int)total, dstBuf.View, rt, gi, bx, by, bz, p);
            context.Accelerator.Synchronize();
        }

        context.RecordGpuDispatch();
        context.DownloadSpan(dstBuf.View, destination.Span.Slice(0, (int)total));
    }

    internal static void Run2DToHost(StbGpuContext context, Span<float> destination, long samples, Perlin2DParams p)
    {
        using PooledGpuBuffer<float> dstBuf = context.Pool.RentFloat32(samples);
        lock (context.SyncRoot)
        {
            context.GetPerlinTablesLocked(out var rt, out var gi, out var bx, out var by, out var bz);
            var kernel = context.GetPerlin2DKernelLocked();
            kernel((int)samples, dstBuf.View, rt, gi, bx, by, bz, p);
            context.Accelerator.Synchronize();
        }

        context.RecordGpuDispatch();
        context.DownloadSpan(dstBuf.View, destination.Slice(0, (int)samples));
    }

    private static StbGpuImage<float> Run2DToVram(StbGpuContext context, int width, int height, long samples, Perlin2DParams p)
    {
        PooledGpuBuffer<float> dstBuf = context.Pool.RentFloat32(samples);
        try
        {
            lock (context.SyncRoot)
            {
                context.GetPerlinTablesLocked(out var rt, out var gi, out var bx, out var by, out var bz);
                var kernel = context.GetPerlin2DKernelLocked();
                kernel((int)samples, dstBuf.View, rt, gi, bx, by, bz, p);
                context.Accelerator.Synchronize();
            }

            context.RecordGpuDispatch();
            return new StbGpuImage<float>(context, dstBuf, width, height, ColorComponents.Grey);
        }
        catch
        {
            dstBuf.Dispose();
            throw;
        }
    }

    private static long Validate2D(int availableLength, int width, int height)
    {
        if (width <= 0 || height <= 0)
        {
            throw new StbException(StbErrorCode.InvalidArgument, "Grid dimensions must be positive.");
        }

        long total = (long)width * height;
        if (availableLength < total)
        {
            throw new StbException(StbErrorCode.InvalidArgument, $"Destination length ({availableLength}) is smaller than width * height ({total}).");
        }

        return total;
    }
}
