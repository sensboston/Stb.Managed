using System.Buffers;
using System.Runtime.CompilerServices;
#if NET8_0_OR_GREATER
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.Arm;
using System.Runtime.Intrinsics.X86;
#endif
using Stb.Managed.Internal;
using Stb.Managed.Perlin.Internal;

namespace Stb.Managed.Perlin;

/// <summary>
/// Fully managed 3D Perlin noise, derivatives, fractal noise (fBm, ridge, turbulence), and SIMD-accelerated
/// 2D grid / batch evaluators, a port of Sean Barrett's <c>stb_perlin.h</c> v0.6.
/// </summary>
public static class StbPerlin
{
    private const int ParallelRowThreshold = 32;
    private const int ParallelPixelThreshold = 16384;

    /// <summary>
    /// Computes Ken Perlin's revised (2002) 3D gradient noise at <c>(x, y, z)</c> with optional power-of-two wrapping.
    /// </summary>
    /// <param name="x">X coordinate.</param>
    /// <param name="y">Y coordinate.</param>
    /// <param name="z">Z coordinate.</param>
    /// <param name="xWrap">Power-of-two wrap period along X (<c>0</c> = 256).</param>
    /// <param name="yWrap">Power-of-two wrap period along Y (<c>0</c> = 256).</param>
    /// <param name="zWrap">Power-of-two wrap period along Z (<c>0</c> = 256).</param>
    /// <returns>Noise value.</returns>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static float Noise3(float x, float y, float z, int xWrap = 0, int yWrap = 0, int zWrap = 0) =>
        Noise3Internal(x, y, z, xWrap, yWrap, zWrap, seed: 0);

    /// <summary>
    /// Computes 3D Perlin noise with a seed (low 8 bits select permutation variation) and optional power-of-two wrapping.
    /// </summary>
    /// <param name="x">X coordinate.</param>
    /// <param name="y">Y coordinate.</param>
    /// <param name="z">Z coordinate.</param>
    /// <param name="xWrap">Power-of-two wrap period along X (<c>0</c> = 256).</param>
    /// <param name="yWrap">Power-of-two wrap period along Y (<c>0</c> = 256).</param>
    /// <param name="zWrap">Power-of-two wrap period along Z (<c>0</c> = 256).</param>
    /// <param name="seed">Seed value (low 8 bits used).</param>
    /// <returns>Noise value.</returns>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static float Noise3Seed(float x, float y, float z, int xWrap, int yWrap, int zWrap, int seed) =>
        Noise3Internal(x, y, z, xWrap, yWrap, zWrap, (byte)seed);

    /// <summary>
    /// Computes 3D Perlin noise supporting arbitrary (non-power-of-two) wrap periods in <c>[1..256]</c>.
    /// Note that per upstream <c>stb_perlin.h</c>, the permutation lookup differs from <see cref="Noise3Seed"/> even for power-of-two wraps.
    /// </summary>
    /// <param name="x">X coordinate.</param>
    /// <param name="y">Y coordinate.</param>
    /// <param name="z">Z coordinate.</param>
    /// <param name="xWrap">Wrap period along X (values outside <c>[1..255]</c> wrap at 256).</param>
    /// <param name="yWrap">Wrap period along Y (values outside <c>[1..255]</c> wrap at 256).</param>
    /// <param name="zWrap">Wrap period along Z (values outside <c>[1..255]</c> wrap at 256).</param>
    /// <param name="seed">Seed byte.</param>
    /// <returns>Noise value.</returns>
    public static float Noise3WrapNonPow2(float x, float y, float z, int xWrap, int yWrap, int zWrap, byte seed = 0)
    {
        ResolveNonPow2Coords(x, y, z, xWrap, yWrap, zWrap, seed, out int y0, out int z0, out int y1, out int z1, out int r0, out int r1, out float fx, out float fy, out float fz);
        return Noise3Impl(y0, z0, y1, z1, r0, r1, fx, fy, fz);
    }

    /// <summary>
    /// Computes 3D Perlin noise (matching <see cref="Noise3WrapNonPow2"/>) along with its analytical partial derivatives.
    /// </summary>
    /// <param name="x">X coordinate.</param>
    /// <param name="y">Y coordinate.</param>
    /// <param name="z">Z coordinate.</param>
    /// <param name="xWrap">Wrap period along X (values outside <c>[1..255]</c> wrap at 256).</param>
    /// <param name="yWrap">Wrap period along Y (values outside <c>[1..255]</c> wrap at 256).</param>
    /// <param name="zWrap">Wrap period along Z (values outside <c>[1..255]</c> wrap at 256).</param>
    /// <param name="seed">Seed value (low 8 bits used).</param>
    /// <param name="dx">Partial derivative with respect to X.</param>
    /// <param name="dy">Partial derivative with respect to Y.</param>
    /// <param name="dz">Partial derivative with respect to Z.</param>
    /// <returns>Noise value.</returns>
    public static float Noise3Derivatives(
        float x,
        float y,
        float z,
        int xWrap,
        int yWrap,
        int zWrap,
        int seed,
        out float dx,
        out float dy,
        out float dz)
    {
        ResolveNonPow2Coords(x, y, z, xWrap, yWrap, zWrap, seed & 255, out int y0, out int z0, out int y1, out int z1, out int r0, out int r1, out float fx, out float fy, out float fz);
        return Noise3ImplDerivatives(y0, z0, y1, z1, r0, r1, fx, fy, fz, out dx, out dy, out dz);
    }

    /// <summary>
    /// Computes ridged multifractal 3D Perlin noise.
    /// </summary>
    /// <param name="x">X coordinate.</param>
    /// <param name="y">Y coordinate.</param>
    /// <param name="z">Z coordinate.</param>
    /// <param name="lacunarity">Frequency multiplier per octave (typically <c>2.0f</c>).</param>
    /// <param name="gain">Amplitude multiplier per octave (typically <c>0.5f</c>).</param>
    /// <param name="offset">Ridge inversion offset (typically <c>1.0f</c>).</param>
    /// <param name="octaves">Number of octaves to accumulate.</param>
    /// <returns>Ridged multifractal noise value.</returns>
    public static float RidgeNoise3(
        float x,
        float y,
        float z,
        float lacunarity = 2.0f,
        float gain = 0.5f,
        float offset = 1.0f,
        int octaves = 6)
    {
        float frequency = 1.0f;
        float prev = 1.0f;
        float amplitude = 0.5f;
        float sum = 0.0f;

        for (int i = 0; i < octaves; ++i)
        {
            float r = Noise3Internal(x * frequency, y * frequency, z * frequency, 0, 0, 0, (byte)i);
            r = offset - MathF.Abs(r);
            r *= r;
            sum += r * amplitude * prev;
            prev = r;
            frequency *= lacunarity;
            amplitude *= gain;
        }

        return sum;
    }

    /// <summary>
    /// Computes fractal Brownian motion (fBm) 3D Perlin noise.
    /// </summary>
    /// <param name="x">X coordinate.</param>
    /// <param name="y">Y coordinate.</param>
    /// <param name="z">Z coordinate.</param>
    /// <param name="lacunarity">Frequency multiplier per octave (typically <c>2.0f</c>).</param>
    /// <param name="gain">Amplitude multiplier per octave (typically <c>0.5f</c>).</param>
    /// <param name="octaves">Number of octaves to accumulate.</param>
    /// <returns>fBm noise value.</returns>
    public static float FbmNoise3(
        float x,
        float y,
        float z,
        float lacunarity = 2.0f,
        float gain = 0.5f,
        int octaves = 6)
    {
        float frequency = 1.0f;
        float amplitude = 1.0f;
        float sum = 0.0f;

        for (int i = 0; i < octaves; ++i)
        {
            sum += Noise3Internal(x * frequency, y * frequency, z * frequency, 0, 0, 0, (byte)i) * amplitude;
            frequency *= lacunarity;
            amplitude *= gain;
        }

        return sum;
    }

    /// <summary>
    /// Computes turbulence (sum of absolute octaves) 3D Perlin noise.
    /// </summary>
    /// <param name="x">X coordinate.</param>
    /// <param name="y">Y coordinate.</param>
    /// <param name="z">Z coordinate.</param>
    /// <param name="lacunarity">Frequency multiplier per octave (typically <c>2.0f</c>).</param>
    /// <param name="gain">Amplitude multiplier per octave (typically <c>0.5f</c>).</param>
    /// <param name="octaves">Number of octaves to accumulate.</param>
    /// <returns>Turbulence noise value.</returns>
    public static float TurbulenceNoise3(
        float x,
        float y,
        float z,
        float lacunarity = 2.0f,
        float gain = 0.5f,
        int octaves = 6)
    {
        float frequency = 1.0f;
        float amplitude = 1.0f;
        float sum = 0.0f;

        for (int i = 0; i < octaves; ++i)
        {
            float r = Noise3Internal(x * frequency, y * frequency, z * frequency, 0, 0, 0, (byte)i) * amplitude;
            sum += MathF.Abs(r);
            frequency *= lacunarity;
            amplitude *= gain;
        }

        return sum;
    }

    /// <summary>
    /// Evaluates <see cref="Noise3Seed"/> on a regular 2D grid at slice <paramref name="z"/> using SIMD acceleration
    /// (single-threaded zero-allocation span overload).
    /// </summary>
    /// <param name="destination">Output buffer of floats.</param>
    /// <param name="width">Grid width in samples.</param>
    /// <param name="height">Grid height in samples.</param>
    /// <param name="startX">Starting X coordinate.</param>
    /// <param name="startY">Starting Y coordinate.</param>
    /// <param name="stepX">X step per column.</param>
    /// <param name="stepY">Y step per row.</param>
    /// <param name="z">Constant Z coordinate.</param>
    /// <param name="xWrap">Power-of-two wrap period along X (<c>0</c> = 256).</param>
    /// <param name="yWrap">Power-of-two wrap period along Y (<c>0</c> = 256).</param>
    /// <param name="zWrap">Power-of-two wrap period along Z (<c>0</c> = 256).</param>
    /// <param name="seed">Seed value (low 8 bits used).</param>
    /// <param name="destinationStride">Row stride in floats (<c>0</c> = <paramref name="width"/>).</param>
    public static void FillNoise2D(
        Span<float> destination,
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
        int destinationStride = 0)
    {
        int stride = ValidateGridArgs(destination.Length, width, height, destinationStride, options: null);

        if (PerlinGpu.TryFill(destination, stride, new PerlinGrid(PerlinGpuKind.Noise, width, height, startX, startY, stepX, stepY, z, 0f, 0f, 0f, 1, xWrap, yWrap, zWrap, seed)))
        {
            return;
        }
        int xMask = (xWrap - 1) & 255;
        int yMask = (yWrap - 1) & 255;
        int zMask = (zWrap - 1) & 255;
        byte seedByte = (byte)seed;

        Span<float> aLut = stackalloc float[256];
        Span<float> bLut = stackalloc float[256];

        for (int iy = 0; iy < height; ++iy)
        {
            float y = startY + iy * stepY;
            EvalRowNoise(
                destination.Slice(iy * stride, width),
                width,
                startX,
                stepX,
                y,
                z,
                frequency: 1.0f,
                amplitude: 1.0f,
                xMask,
                yMask,
                zMask,
                seedByte,
                RowAccumMode.Overwrite,
                aLut,
                bLut);
        }
    }

    /// <summary>
    /// Evaluates <see cref="Noise3Seed"/> on a regular 2D grid at slice <paramref name="z"/> with SIMD acceleration
    /// and optional deterministic multithreading controlled by <see cref="StbOptions.MaxDegreeOfParallelism"/>.
    /// </summary>
    /// <param name="destination">Output buffer of floats.</param>
    /// <param name="width">Grid width in samples.</param>
    /// <param name="height">Grid height in samples.</param>
    /// <param name="startX">Starting X coordinate.</param>
    /// <param name="startY">Starting Y coordinate.</param>
    /// <param name="stepX">X step per column.</param>
    /// <param name="stepY">Y step per row.</param>
    /// <param name="z">Constant Z coordinate.</param>
    /// <param name="xWrap">Power-of-two wrap period along X (<c>0</c> = 256).</param>
    /// <param name="yWrap">Power-of-two wrap period along Y (<c>0</c> = 256).</param>
    /// <param name="zWrap">Power-of-two wrap period along Z (<c>0</c> = 256).</param>
    /// <param name="seed">Seed value (low 8 bits used).</param>
    /// <param name="destinationStride">Row stride in floats (<c>0</c> = <paramref name="width"/>).</param>
    /// <param name="options">Optional execution limits and parallelism configuration.</param>
    public static void FillNoise2DMemory(
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
        int destinationStride = 0,
        StbOptions? options = null)
    {
        StbOptions resolved = options ?? StbOptions.Default;
        int stride = ValidateGridArgs(destination.Length, width, height, destinationStride, resolved);

        if (PerlinGpu.TryFill(destination.Span, stride, new PerlinGrid(PerlinGpuKind.Noise, width, height, startX, startY, stepX, stepY, z, 0f, 0f, 0f, 1, xWrap, yWrap, zWrap, seed)))
        {
            return;
        }
        int xMask = (xWrap - 1) & 255;
        int yMask = (yWrap - 1) & 255;
        int zMask = (zWrap - 1) & 255;
        byte seedByte = (byte)seed;

        int dop = resolved.ResolveDegreeOfParallelism(height, ParallelRowThreshold);
        if (dop > 1 && height >= ParallelRowThreshold && (long)width * height >= ParallelPixelThreshold)
        {
            Parallel.For(0, height, new ParallelOptions { MaxDegreeOfParallelism = dop }, iy =>
            {
                Span<float> aLut = stackalloc float[256];
                Span<float> bLut = stackalloc float[256];
                EvalRowNoise(
                    destination.Span.Slice(iy * stride, width),
                    width,
                    startX,
                    stepX,
                    startY + iy * stepY,
                    z,
                    frequency: 1.0f,
                    amplitude: 1.0f,
                    xMask,
                    yMask,
                    zMask,
                    seedByte,
                    RowAccumMode.Overwrite,
                    aLut,
                    bLut);
            });
            return;
        }

        FillNoise2D(destination.Span, width, height, startX, startY, stepX, stepY, z, xWrap, yWrap, zWrap, seed, stride);
    }

    /// <summary>
    /// Evaluates <see cref="FbmNoise3"/> on a regular 2D grid at slice <paramref name="z"/> using SIMD acceleration
    /// (single-threaded span overload).
    /// </summary>
    /// <param name="destination">Output buffer of floats.</param>
    /// <param name="width">Grid width in samples.</param>
    /// <param name="height">Grid height in samples.</param>
    /// <param name="startX">Starting X coordinate.</param>
    /// <param name="startY">Starting Y coordinate.</param>
    /// <param name="stepX">X step per column.</param>
    /// <param name="stepY">Y step per row.</param>
    /// <param name="z">Constant Z coordinate.</param>
    /// <param name="lacunarity">Frequency multiplier per octave.</param>
    /// <param name="gain">Amplitude multiplier per octave.</param>
    /// <param name="octaves">Number of octaves.</param>
    /// <param name="destinationStride">Row stride in floats (<c>0</c> = <paramref name="width"/>).</param>
    public static void FillFbm2D(
        Span<float> destination,
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
        int destinationStride = 0)
    {
        int stride = ValidateGridArgs(destination.Length, width, height, destinationStride, options: null);

        if (PerlinGpu.TryFill(destination, stride, new PerlinGrid(PerlinGpuKind.Fbm, width, height, startX, startY, stepX, stepY, z, lacunarity, gain, 0f, octaves, 0, 0, 0, 0)))
        {
            return;
        }
        Span<float> aLut = stackalloc float[256];
        Span<float> bLut = stackalloc float[256];

        for (int iy = 0; iy < height; ++iy)
        {
            EvalRowFbm(destination.Slice(iy * stride, width), width, startX, stepX, startY + iy * stepY, z, lacunarity, gain, octaves, aLut, bLut);
        }
    }

    /// <summary>
    /// Evaluates <see cref="FbmNoise3"/> on a regular 2D grid at slice <paramref name="z"/> with SIMD acceleration
    /// and optional deterministic multithreading.
    /// </summary>
    /// <param name="destination">Output buffer of floats.</param>
    /// <param name="width">Grid width in samples.</param>
    /// <param name="height">Grid height in samples.</param>
    /// <param name="startX">Starting X coordinate.</param>
    /// <param name="startY">Starting Y coordinate.</param>
    /// <param name="stepX">X step per column.</param>
    /// <param name="stepY">Y step per row.</param>
    /// <param name="z">Constant Z coordinate.</param>
    /// <param name="lacunarity">Frequency multiplier per octave.</param>
    /// <param name="gain">Amplitude multiplier per octave.</param>
    /// <param name="octaves">Number of octaves.</param>
    /// <param name="destinationStride">Row stride in floats (<c>0</c> = <paramref name="width"/>).</param>
    /// <param name="options">Optional execution limits and parallelism configuration.</param>
    public static void FillFbm2DMemory(
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
        int destinationStride = 0,
        StbOptions? options = null)
    {
        StbOptions resolved = options ?? StbOptions.Default;
        int stride = ValidateGridArgs(destination.Length, width, height, destinationStride, resolved);

        if (PerlinGpu.TryFill(destination.Span, stride, new PerlinGrid(PerlinGpuKind.Fbm, width, height, startX, startY, stepX, stepY, z, lacunarity, gain, 0f, octaves, 0, 0, 0, 0)))
        {
            return;
        }
        int dop = resolved.ResolveDegreeOfParallelism(height, ParallelRowThreshold);
        if (dop > 1 && height >= ParallelRowThreshold && (long)width * height >= ParallelPixelThreshold && octaves > 0)
        {
            Parallel.For(0, height, new ParallelOptions { MaxDegreeOfParallelism = dop }, iy =>
            {
                Span<float> aLut = stackalloc float[256];
                Span<float> bLut = stackalloc float[256];
                EvalRowFbm(destination.Span.Slice(iy * stride, width), width, startX, stepX, startY + iy * stepY, z, lacunarity, gain, octaves, aLut, bLut);
            });
            return;
        }

        FillFbm2D(destination.Span, width, height, startX, startY, stepX, stepY, z, lacunarity, gain, octaves, stride);
    }

    /// <summary>
    /// Evaluates <see cref="TurbulenceNoise3"/> on a regular 2D grid at slice <paramref name="z"/> using SIMD acceleration
    /// (single-threaded span overload).
    /// </summary>
    /// <param name="destination">Output buffer of floats.</param>
    /// <param name="width">Grid width in samples.</param>
    /// <param name="height">Grid height in samples.</param>
    /// <param name="startX">Starting X coordinate.</param>
    /// <param name="startY">Starting Y coordinate.</param>
    /// <param name="stepX">X step per column.</param>
    /// <param name="stepY">Y step per row.</param>
    /// <param name="z">Constant Z coordinate.</param>
    /// <param name="lacunarity">Frequency multiplier per octave.</param>
    /// <param name="gain">Amplitude multiplier per octave.</param>
    /// <param name="octaves">Number of octaves.</param>
    /// <param name="destinationStride">Row stride in floats (<c>0</c> = <paramref name="width"/>).</param>
    public static void FillTurbulence2D(
        Span<float> destination,
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
        int destinationStride = 0)
    {
        int stride = ValidateGridArgs(destination.Length, width, height, destinationStride, options: null);

        if (PerlinGpu.TryFill(destination, stride, new PerlinGrid(PerlinGpuKind.Turbulence, width, height, startX, startY, stepX, stepY, z, lacunarity, gain, 0f, octaves, 0, 0, 0, 0)))
        {
            return;
        }
        Span<float> aLut = stackalloc float[256];
        Span<float> bLut = stackalloc float[256];

        for (int iy = 0; iy < height; ++iy)
        {
            EvalRowTurbulence(destination.Slice(iy * stride, width), width, startX, stepX, startY + iy * stepY, z, lacunarity, gain, octaves, aLut, bLut);
        }
    }

    /// <summary>
    /// Evaluates <see cref="TurbulenceNoise3"/> on a regular 2D grid at slice <paramref name="z"/> with SIMD acceleration
    /// and optional deterministic multithreading.
    /// </summary>
    /// <param name="destination">Output buffer of floats.</param>
    /// <param name="width">Grid width in samples.</param>
    /// <param name="height">Grid height in samples.</param>
    /// <param name="startX">Starting X coordinate.</param>
    /// <param name="startY">Starting Y coordinate.</param>
    /// <param name="stepX">X step per column.</param>
    /// <param name="stepY">Y step per row.</param>
    /// <param name="z">Constant Z coordinate.</param>
    /// <param name="lacunarity">Frequency multiplier per octave.</param>
    /// <param name="gain">Amplitude multiplier per octave.</param>
    /// <param name="octaves">Number of octaves.</param>
    /// <param name="destinationStride">Row stride in floats (<c>0</c> = <paramref name="width"/>).</param>
    /// <param name="options">Optional execution limits and parallelism configuration.</param>
    public static void FillTurbulence2DMemory(
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
        int destinationStride = 0,
        StbOptions? options = null)
    {
        StbOptions resolved = options ?? StbOptions.Default;
        int stride = ValidateGridArgs(destination.Length, width, height, destinationStride, resolved);

        if (PerlinGpu.TryFill(destination.Span, stride, new PerlinGrid(PerlinGpuKind.Turbulence, width, height, startX, startY, stepX, stepY, z, lacunarity, gain, 0f, octaves, 0, 0, 0, 0)))
        {
            return;
        }
        int dop = resolved.ResolveDegreeOfParallelism(height, ParallelRowThreshold);
        if (dop > 1 && height >= ParallelRowThreshold && (long)width * height >= ParallelPixelThreshold && octaves > 0)
        {
            Parallel.For(0, height, new ParallelOptions { MaxDegreeOfParallelism = dop }, iy =>
            {
                Span<float> aLut = stackalloc float[256];
                Span<float> bLut = stackalloc float[256];
                EvalRowTurbulence(destination.Span.Slice(iy * stride, width), width, startX, stepX, startY + iy * stepY, z, lacunarity, gain, octaves, aLut, bLut);
            });
            return;
        }

        FillTurbulence2D(destination.Span, width, height, startX, startY, stepX, stepY, z, lacunarity, gain, octaves, stride);
    }

    /// <summary>
    /// Evaluates <see cref="RidgeNoise3"/> on a regular 2D grid at slice <paramref name="z"/> using SIMD acceleration
    /// (single-threaded span overload).
    /// </summary>
    /// <param name="destination">Output buffer of floats.</param>
    /// <param name="width">Grid width in samples.</param>
    /// <param name="height">Grid height in samples.</param>
    /// <param name="startX">Starting X coordinate.</param>
    /// <param name="startY">Starting Y coordinate.</param>
    /// <param name="stepX">X step per column.</param>
    /// <param name="stepY">Y step per row.</param>
    /// <param name="z">Constant Z coordinate.</param>
    /// <param name="lacunarity">Frequency multiplier per octave.</param>
    /// <param name="gain">Amplitude multiplier per octave.</param>
    /// <param name="offset">Ridge inversion offset.</param>
    /// <param name="octaves">Number of octaves.</param>
    /// <param name="destinationStride">Row stride in floats (<c>0</c> = <paramref name="width"/>).</param>
    public static void FillRidge2D(
        Span<float> destination,
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
        int destinationStride = 0)
    {
        int stride = ValidateGridArgs(destination.Length, width, height, destinationStride, options: null);

        if (PerlinGpu.TryFill(destination, stride, new PerlinGrid(PerlinGpuKind.Ridge, width, height, startX, startY, stepX, stepY, z, lacunarity, gain, offset, octaves, 0, 0, 0, 0)))
        {
            return;
        }
        Span<float> aLut = stackalloc float[256];
        Span<float> bLut = stackalloc float[256];
        float[]? rentedPrev = width > 512 ? ArrayPool<float>.Shared.Rent(width) : null;
        try
        {
            Span<float> prevRow = rentedPrev is not null ? rentedPrev.AsSpan(0, width) : stackalloc float[512];
            prevRow = prevRow.Slice(0, width);
            for (int iy = 0; iy < height; ++iy)
            {
                EvalRowRidge(destination.Slice(iy * stride, width), prevRow, width, startX, stepX, startY + iy * stepY, z, lacunarity, gain, offset, octaves, aLut, bLut);
            }
        }
        finally
        {
            if (rentedPrev is not null)
            {
                ArrayPool<float>.Shared.Return(rentedPrev);
            }
        }
    }

    /// <summary>
    /// Evaluates <see cref="RidgeNoise3"/> on a regular 2D grid at slice <paramref name="z"/> with SIMD acceleration
    /// and optional deterministic multithreading.
    /// </summary>
    /// <param name="destination">Output buffer of floats.</param>
    /// <param name="width">Grid width in samples.</param>
    /// <param name="height">Grid height in samples.</param>
    /// <param name="startX">Starting X coordinate.</param>
    /// <param name="startY">Starting Y coordinate.</param>
    /// <param name="stepX">X step per column.</param>
    /// <param name="stepY">Y step per row.</param>
    /// <param name="z">Constant Z coordinate.</param>
    /// <param name="lacunarity">Frequency multiplier per octave.</param>
    /// <param name="gain">Amplitude multiplier per octave.</param>
    /// <param name="offset">Ridge inversion offset.</param>
    /// <param name="octaves">Number of octaves.</param>
    /// <param name="destinationStride">Row stride in floats (<c>0</c> = <paramref name="width"/>).</param>
    /// <param name="options">Optional execution limits and parallelism configuration.</param>
    public static void FillRidge2DMemory(
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
        int destinationStride = 0,
        StbOptions? options = null)
    {
        StbOptions resolved = options ?? StbOptions.Default;
        int stride = ValidateGridArgs(destination.Length, width, height, destinationStride, resolved);

        if (PerlinGpu.TryFill(destination.Span, stride, new PerlinGrid(PerlinGpuKind.Ridge, width, height, startX, startY, stepX, stepY, z, lacunarity, gain, offset, octaves, 0, 0, 0, 0)))
        {
            return;
        }
        int dop = resolved.ResolveDegreeOfParallelism(height, ParallelRowThreshold);
        if (dop > 1 && height >= ParallelRowThreshold && (long)width * height >= ParallelPixelThreshold && octaves > 0)
        {
            Parallel.For(0, height, new ParallelOptions { MaxDegreeOfParallelism = dop }, iy =>
            {
                Span<float> aLut = stackalloc float[256];
                Span<float> bLut = stackalloc float[256];
                float[]? rentedPrev = width > 512 ? ArrayPool<float>.Shared.Rent(width) : null;
                try
                {
                    Span<float> prevRow = rentedPrev is not null ? rentedPrev.AsSpan(0, width) : stackalloc float[512];
                    prevRow = prevRow.Slice(0, width);
                    EvalRowRidge(destination.Span.Slice(iy * stride, width), prevRow, width, startX, stepX, startY + iy * stepY, z, lacunarity, gain, offset, octaves, aLut, bLut);
                }
                finally
                {
                    if (rentedPrev is not null)
                    {
                        ArrayPool<float>.Shared.Return(rentedPrev);
                    }
                }
            });
            return;
        }

        FillRidge2D(destination.Span, width, height, startX, startY, stepX, stepY, z, lacunarity, gain, offset, octaves, stride);
    }

    /// <summary>
    /// Evaluates <see cref="Noise3Seed"/> for an arbitrary batch of 3D coordinates.
    /// </summary>
    /// <param name="xs">X coordinates.</param>
    /// <param name="ys">Y coordinates (must match <paramref name="xs"/> length).</param>
    /// <param name="zs">Z coordinates (must match <paramref name="xs"/> length).</param>
    /// <param name="destination">Output buffer (at least <paramref name="xs"/> length).</param>
    /// <param name="xWrap">Power-of-two wrap period along X (<c>0</c> = 256).</param>
    /// <param name="yWrap">Power-of-two wrap period along Y (<c>0</c> = 256).</param>
    /// <param name="zWrap">Power-of-two wrap period along Z (<c>0</c> = 256).</param>
    /// <param name="seed">Seed value (low 8 bits used).</param>
    public static void FillNoise3Batch(
        ReadOnlySpan<float> xs,
        ReadOnlySpan<float> ys,
        ReadOnlySpan<float> zs,
        Span<float> destination,
        int xWrap = 0,
        int yWrap = 0,
        int zWrap = 0,
        int seed = 0)
    {
        int count = xs.Length;
        if (ys.Length != count)
        {
            throw new ArgumentException("Y coordinate span length must match X coordinate span length.", nameof(ys));
        }

        if (zs.Length != count)
        {
            throw new ArgumentException("Z coordinate span length must match X coordinate span length.", nameof(zs));
        }

        if (destination.Length < count)
        {
            throw new ArgumentException("Destination span is shorter than input coordinate spans.", nameof(destination));
        }

        int xMask = (xWrap - 1) & 255;
        int yMask = (yWrap - 1) & 255;
        int zMask = (zWrap - 1) & 255;
        byte seedByte = (byte)seed;

        for (int i = 0; i < count; ++i)
        {
            destination[i] = Noise3Masked(
                xs[i],
                ys[i],
                zs[i],
                xMask,
                yMask,
                zMask,
                seedByte);
        }
    }

    private enum RowAccumMode
    {
        Overwrite = 0,
        AddScaled = 1,
        AddAbsScaled = 2,
    }

    private static int ValidateGridArgs(int destLength, int width, int height, int destinationStride, StbOptions? options, string destParamName = "destination")
    {
        StbOptions resolved = options ?? StbOptions.Default;
        _ = resolved.Limits.ValidateDimensions(width, height, 4);
        int stride = destinationStride == 0 ? width : destinationStride;
        Guard.ThrowIfLessThan(stride, width, nameof(destinationStride));
        long required = (long)(height - 1) * stride + width;
        if (destLength < required)
        {
            throw new ArgumentException(
                $"Destination buffer ({destLength} floats) is smaller than required ({required} floats).",
                destParamName);
        }

        return stride;
    }

    private static void EvalRowFbm(
        Span<float> row,
        int width,
        float startX,
        float stepX,
        float y,
        float z,
        float lacunarity,
        float gain,
        int octaves,
        Span<float> aLut,
        Span<float> bLut)
    {
        if (octaves <= 0)
        {
            row.Clear();
            return;
        }

        float freq = 1.0f;
        float amp = 1.0f;
        for (int i = 0; i < octaves; ++i)
        {
            EvalRowNoise(
                row,
                width,
                startX,
                stepX,
                y * freq,
                z * freq,
                freq,
                amp,
                255,
                255,
                255,
                (byte)i,
                i == 0 ? RowAccumMode.Overwrite : RowAccumMode.AddScaled,
                aLut,
                bLut);
            freq *= lacunarity;
            amp *= gain;
        }
    }

    private static void EvalRowTurbulence(
        Span<float> row,
        int width,
        float startX,
        float stepX,
        float y,
        float z,
        float lacunarity,
        float gain,
        int octaves,
        Span<float> aLut,
        Span<float> bLut)
    {
        row.Clear();
        if (octaves <= 0)
        {
            return;
        }

        float freq = 1.0f;
        float amp = 1.0f;
        for (int i = 0; i < octaves; ++i)
        {
            EvalRowNoise(
                row,
                width,
                startX,
                stepX,
                y * freq,
                z * freq,
                freq,
                amp,
                255,
                255,
                255,
                (byte)i,
                RowAccumMode.AddAbsScaled,
                aLut,
                bLut);
            freq *= lacunarity;
            amp *= gain;
        }
    }

    private static void EvalRowRidge(
        Span<float> row,
        Span<float> prevRow,
        int width,
        float startX,
        float stepX,
        float y,
        float z,
        float lacunarity,
        float gain,
        float offset,
        int octaves,
        Span<float> aLut,
        Span<float> bLut)
    {
        if (octaves <= 0)
        {
            row.Clear();
            return;
        }

        float freq = 1.0f;
        float amp = 0.5f;
        for (int oct = 0; oct < octaves; ++oct)
        {
            float yf = y * freq;
            float zf = z * freq;

            if (!CanUseRowFastPath(startX, stepX, freq, width, yf, zf))
            {
                if (oct == 0)
                {
                    for (int ix = 0; ix < width; ++ix)
                    {
                        row[ix] = RidgeNoise3(startX + ix * stepX, y, z, lacunarity, gain, offset, octaves);
                    }

                    return;
                }

                for (int ix = 0; ix < width; ++ix)
                {
                    float r = Noise3Internal((startX + ix * stepX) * freq, yf, zf, 0, 0, 0, (byte)oct);
                    r = offset - MathF.Abs(r);
                    r *= r;
                    row[ix] += r * amp * prevRow[ix];
                    prevRow[ix] = r;
                }
            }
            else
            {
                BuildRowLinearLuts(startX, stepX, freq, width, yf, zf, 255, 255, 255, (byte)oct, aLut, bLut);
                EvalRowRidgeFast(row, prevRow, width, startX, stepX, freq, amp, offset, isFirstOctave: oct == 0, aLut, bLut);
            }

            freq *= lacunarity;
            amp *= gain;
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool CanUseRowFastPath(float startX, float stepX, float freq, int width, float yf, float zf)
    {
        if (!float.IsFinite(startX) || !float.IsFinite(stepX) || !float.IsFinite(freq) || !float.IsFinite(yf) || !float.IsFinite(zf))
        {
            return false;
        }

        float endX = (startX + (width - 1) * stepX) * freq;
        float beginX = startX * freq;
        const float MaxSafeCoord = 1e8f;
        return MathF.Abs(beginX) < MaxSafeCoord
            && MathF.Abs(endX) < MaxSafeCoord
            && MathF.Abs(yf) < MaxSafeCoord
            && MathF.Abs(zf) < MaxSafeCoord;
    }

    private static void BuildRowLinearLuts(
        float startX,
        float stepX,
        float freq,
        int width,
        float yf,
        float zf,
        int xMask,
        int yMask,
        int zMask,
        byte seed,
        Span<float> aLut,
        Span<float> bLut)
    {
        int py = FastFloor(yf);
        int pz = FastFloor(zf);
        int y0 = py & yMask;
        int y1 = (py + 1) & yMask;
        int z0 = pz & zMask;
        int z1 = (pz + 1) & zMask;
        float fy = yf - py;
        float fz = zf - pz;
        float fy1 = fy - 1.0f;
        float fz1 = fz - 1.0f;
        float v = Ease(fy);
        float w = Ease(fz);

        // Precompute the 12 basis contributions for the 4 (y, z) corners so each x entry is pure table lookups.
        Span<float> c00 = stackalloc float[12];
        Span<float> c01 = stackalloc float[12];
        Span<float> c10 = stackalloc float[12];
        Span<float> c11 = stackalloc float[12];
        float[] bx = PerlinTables.BasisX;
        float[] by = PerlinTables.BasisY;
        float[] bz = PerlinTables.BasisZ;
        for (int g = 0; g < 12; ++g)
        {
            float gy = by[g];
            float gz = bz[g];
            c00[g] = gy * fy + gz * fz;
            c01[g] = gy * fy + gz * fz1;
            c10[g] = gy * fy1 + gz * fz;
            c11[g] = gy * fy1 + gz * fz1;
        }

        byte[] rand = PerlinTables.RandTab;
        byte[] gradIdx = PerlinTables.RandTabGradIdx;

        // Determine whether only a small subset of x0 values are visited on this row.
        float xFirst = startX * freq;
        float xLast = (startX + (width - 1) * stepX) * freq;
        int pMin = FastFloor(xFirst < xLast ? xFirst : xLast);
        int pMax = FastFloor(xFirst > xLast ? xFirst : xLast) + 1;
        long spanCells = (long)pMax - pMin + 1L;
        int maxEntries = xMask + 1;

        if (spanCells < maxEntries)
        {
            for (int px = pMin; px <= pMax; ++px)
            {
                int xIdx = px & xMask;
                ComputeRowLutEntry(xIdx, seed, y0, y1, z0, z1, v, w, rand, gradIdx, bx, c00, c01, c10, c11, aLut, bLut);
            }
        }
        else
        {
            for (int xIdx = 0; xIdx < maxEntries; ++xIdx)
            {
                ComputeRowLutEntry(xIdx, seed, y0, y1, z0, z1, v, w, rand, gradIdx, bx, c00, c01, c10, c11, aLut, bLut);
            }
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void ComputeRowLutEntry(
        int xIdx,
        int seed,
        int y0,
        int y1,
        int z0,
        int z1,
        float v,
        float w,
        byte[] rand,
        byte[] gradIdx,
        float[] bx,
        Span<float> c00,
        Span<float> c01,
        Span<float> c10,
        Span<float> c11,
        Span<float> aLut,
        Span<float> bLut)
    {
        int r = rand[xIdx + seed];
        int r0 = rand[r + y0];
        int r1 = rand[r + y1];

        int g00 = gradIdx[r0 + z0];
        int g01 = gradIdx[r0 + z1];
        int g10 = gradIdx[r1 + z0];
        int g11 = gradIdx[r1 + z1];

        float a0 = Lerp(bx[g00], bx[g01], w);
        float a1 = Lerp(bx[g10], bx[g11], w);
        aLut[xIdx] = Lerp(a0, a1, v);

        float b0 = Lerp(c00[g00], c01[g01], w);
        float b1 = Lerp(c10[g10], c11[g11], w);
        bLut[xIdx] = Lerp(b0, b1, v);
    }

    private static void EvalRowNoise(
        Span<float> row,
        int width,
        float startX,
        float stepX,
        float yf,
        float zf,
        float frequency,
        float amplitude,
        int xMask,
        int yMask,
        int zMask,
        byte seed,
        RowAccumMode mode,
        Span<float> aLut,
        Span<float> bLut)
    {
        if (!CanUseRowFastPath(startX, stepX, frequency, width, yf, zf))
        {
            for (int ix = 0; ix < width; ++ix)
            {
                float n = Noise3Masked((startX + ix * stepX) * frequency, yf, zf, xMask, yMask, zMask, seed) * amplitude;
                switch (mode)
                {
                    case RowAccumMode.Overwrite:
                        row[ix] = n;
                        break;
                    case RowAccumMode.AddScaled:
                        row[ix] += n;
                        break;
                    case RowAccumMode.AddAbsScaled:
                        row[ix] += MathF.Abs(n);
                        break;
                }
            }

            return;
        }

        BuildRowLinearLuts(startX, stepX, frequency, width, yf, zf, xMask, yMask, zMask, seed, aLut, bLut);

        int x = 0;
#if NET8_0_OR_GREATER
        if (Avx2.IsSupported && width >= 8)
        {
            Vector256<float> vStartX = Vector256.Create(startX);
            Vector256<float> vStepX = Vector256.Create(stepX);
            Vector256<float> vFreq = Vector256.Create(frequency);
            Vector256<float> vAmp = Vector256.Create(amplitude);
            Vector256<float> vSix = Vector256.Create(6.0f);
            Vector256<float> vNeg15 = Vector256.Create(-15.0f);
            Vector256<float> vTen = Vector256.Create(10.0f);
            Vector256<float> vOneF = Vector256.Create(1.0f);
            Vector256<float> vSignMask = Vector256.Create(0x7FFFFFFFu).AsSingle();
            Vector256<float> vIx = Vector256.Create(0f, 1f, 2f, 3f, 4f, 5f, 6f, 7f);
            Vector256<float> vEight = Vector256.Create(8.0f);

            Span<Vector256<float>> rowV = MemoryMarshal.Cast<float, Vector256<float>>(row.Slice(0, width));
            for (int k = 0; k < rowV.Length; k++, x += 8)
            {
                Vector256<float> vx = Avx.Multiply(Avx.Add(vStartX, Avx.Multiply(vIx, vStepX)), vFreq);
                Vector256<float> vPxF = Avx.Floor(vx);
                Vector256<float> fx = Avx.Subtract(vx, vPxF);
                Vector256<int> px = Avx.ConvertToVector256Int32(vPxF);

                int p0 = px.GetElement(0);
                int p7 = px.GetElement(7);
                Vector256<float> a0, b0, a1, b1;
                if (p0 == p7)
                {
                    int i0 = p0 & xMask;
                    int i1 = (p0 + 1) & xMask;
                    a0 = Vector256.Create(aLut[i0]);
                    b0 = Vector256.Create(bLut[i0]);
                    a1 = Vector256.Create(aLut[i1]);
                    b1 = Vector256.Create(bLut[i1]);
                }
                else
                {
                    Gather8Linear(px, xMask, aLut, bLut, out a0, out b0, out a1, out b1);
                }

                Vector256<float> u = Avx.Multiply(
                    Avx.Multiply(
                        Avx.Multiply(
                            Avx.Add(Avx.Multiply(Avx.Add(Avx.Multiply(fx, vSix), vNeg15), fx), vTen),
                            fx),
                        fx),
                    fx);

                Vector256<float> n0 = Avx.Add(Avx.Multiply(a0, fx), b0);
                Vector256<float> n1 = Avx.Add(Avx.Multiply(a1, Avx.Subtract(fx, vOneF)), b1);
                Vector256<float> n = Avx.Add(n0, Avx.Multiply(Avx.Subtract(n1, n0), u));
                Vector256<float> scaled = Avx.Multiply(n, vAmp);

                ref Vector256<float> dst = ref rowV[k];
                if (mode == RowAccumMode.Overwrite)
                {
                    dst = scaled;
                }
                else if (mode == RowAccumMode.AddScaled)
                {
                    dst = Avx.Add(dst, scaled);
                }
                else
                {
                    dst = Avx.Add(dst, Avx.And(scaled, vSignMask));
                }

                vIx = Avx.Add(vIx, vEight);
            }
        }
        else if ((Sse41.IsSupported || AdvSimd.Arm64.IsSupported) && width >= 4)
        {
            Vector128<float> vStartX = Vector128.Create(startX);
            Vector128<float> vStepX = Vector128.Create(stepX);
            Vector128<float> vFreq = Vector128.Create(frequency);
            Vector128<float> vAmp = Vector128.Create(amplitude);
            Vector128<float> vSix = Vector128.Create(6.0f);
            Vector128<float> vNeg15 = Vector128.Create(-15.0f);
            Vector128<float> vTen = Vector128.Create(10.0f);
            Vector128<float> vOneF = Vector128.Create(1.0f);
            Vector128<float> vSignMask = Vector128.Create(0x7FFFFFFFu).AsSingle();
            Vector128<float> vIx = Vector128.Create(0f, 1f, 2f, 3f);
            Vector128<float> vFour = Vector128.Create(4.0f);

            Span<Vector128<float>> rowV = MemoryMarshal.Cast<float, Vector128<float>>(row.Slice(0, width));
            for (int k = 0; k < rowV.Length; k++, x += 4)
            {
                Vector128<float> vx = (vStartX + vIx * vStepX) * vFreq;
                Vector128<float> vPxF = Sse41.IsSupported ? Sse41.Floor(vx) : AdvSimd.Floor(vx);
                Vector128<float> fx = vx - vPxF;
                Vector128<int> px = Sse41.IsSupported ? Sse2.ConvertToVector128Int32(vPxF) : AdvSimd.ConvertToInt32RoundToZero(vPxF);

                int p0 = px.GetElement(0);
                int p3 = px.GetElement(3);
                Vector128<float> a0, b0, a1, b1;
                if (p0 == p3)
                {
                    int i0 = p0 & xMask;
                    int i1 = (p0 + 1) & xMask;
                    a0 = Vector128.Create(aLut[i0]);
                    b0 = Vector128.Create(bLut[i0]);
                    a1 = Vector128.Create(aLut[i1]);
                    b1 = Vector128.Create(bLut[i1]);
                }
                else
                {
                    Gather4Linear(px, xMask, aLut, bLut, out a0, out b0, out a1, out b1);
                }

                Vector128<float> u = (((fx * vSix + vNeg15) * fx + vTen) * fx) * fx * fx;
                Vector128<float> n0 = a0 * fx + b0;
                Vector128<float> n1 = a1 * (fx - vOneF) + b1;
                Vector128<float> n = n0 + (n1 - n0) * u;
                Vector128<float> scaled = n * vAmp;

                ref Vector128<float> dst = ref rowV[k];
                if (mode == RowAccumMode.Overwrite)
                {
                    dst = scaled;
                }
                else if (mode == RowAccumMode.AddScaled)
                {
                    dst = (dst + scaled);
                }
                else
                {
                    dst = (dst + (scaled & vSignMask));
                }

                vIx += vFour;
            }
        }
#endif

        for (; x < width; ++x)
        {
            float vx = (startX + x * stepX) * frequency;
            int px = FastFloor(vx);
            float fx = vx - px;
            int i0 = px & xMask;
            int i1 = (px + 1) & xMask;
            float n0 = aLut[i0] * fx + bLut[i0];
            float n1 = aLut[i1] * (fx - 1.0f) + bLut[i1];
            float n = Lerp(n0, n1, Ease(fx)) * amplitude;

            if (mode == RowAccumMode.Overwrite)
            {
                row[x] = n;
            }
            else if (mode == RowAccumMode.AddScaled)
            {
                row[x] += n;
            }
            else
            {
                row[x] += MathF.Abs(n);
            }
        }
    }

    private static void EvalRowRidgeFast(
        Span<float> row,
        Span<float> prevRow,
        int width,
        float startX,
        float stepX,
        float frequency,
        float amplitude,
        float offset,
        bool isFirstOctave,
        Span<float> aLut,
        Span<float> bLut)
    {
        int x = 0;
#if NET8_0_OR_GREATER
        if (Avx2.IsSupported && width >= 8)
        {
            Vector256<float> vStartX = Vector256.Create(startX);
            Vector256<float> vStepX = Vector256.Create(stepX);
            Vector256<float> vFreq = Vector256.Create(frequency);
            Vector256<float> vAmp = Vector256.Create(amplitude);
            Vector256<float> vOffset = Vector256.Create(offset);
            Vector256<float> vSix = Vector256.Create(6.0f);
            Vector256<float> vNeg15 = Vector256.Create(-15.0f);
            Vector256<float> vTen = Vector256.Create(10.0f);
            Vector256<float> vOneF = Vector256.Create(1.0f);
            Vector256<float> vSignMask = Vector256.Create(0x7FFFFFFFu).AsSingle();
            Vector256<float> vIx = Vector256.Create(0f, 1f, 2f, 3f, 4f, 5f, 6f, 7f);
            Vector256<float> vEight = Vector256.Create(8.0f);

            Span<Vector256<float>> rowV = MemoryMarshal.Cast<float, Vector256<float>>(row.Slice(0, width));
            Span<Vector256<float>> prevV = MemoryMarshal.Cast<float, Vector256<float>>(prevRow.Slice(0, width));
            for (int k = 0; k < rowV.Length; k++, x += 8)
            {
                Vector256<float> vx = Avx.Multiply(Avx.Add(vStartX, Avx.Multiply(vIx, vStepX)), vFreq);
                Vector256<float> vPxF = Avx.Floor(vx);
                Vector256<float> fx = Avx.Subtract(vx, vPxF);
                Vector256<int> px = Avx.ConvertToVector256Int32(vPxF);

                int p0 = px.GetElement(0);
                int p7 = px.GetElement(7);
                Vector256<float> a0, b0, a1, b1;
                if (p0 == p7)
                {
                    int i0 = p0 & 255;
                    int i1 = (p0 + 1) & 255;
                    a0 = Vector256.Create(aLut[i0]);
                    b0 = Vector256.Create(bLut[i0]);
                    a1 = Vector256.Create(aLut[i1]);
                    b1 = Vector256.Create(bLut[i1]);
                }
                else
                {
                    Gather8Linear(px, 255, aLut, bLut, out a0, out b0, out a1, out b1);
                }

                Vector256<float> u = Avx.Multiply(
                    Avx.Multiply(
                        Avx.Multiply(
                            Avx.Add(Avx.Multiply(Avx.Add(Avx.Multiply(fx, vSix), vNeg15), fx), vTen),
                            fx),
                        fx),
                    fx);

                Vector256<float> n0 = Avx.Add(Avx.Multiply(a0, fx), b0);
                Vector256<float> n1 = Avx.Add(Avx.Multiply(a1, Avx.Subtract(fx, vOneF)), b1);
                Vector256<float> n = Avx.Add(n0, Avx.Multiply(Avx.Subtract(n1, n0), u));

                Vector256<float> r = Avx.Subtract(vOffset, Avx.And(n, vSignMask));
                r = Avx.Multiply(r, r);

                ref Vector256<float> dst = ref rowV[k];
                ref Vector256<float> prv = ref prevV[k];
                if (isFirstOctave)
                {
                    dst = Avx.Multiply(r, vAmp);
                }
                else
                {
                    Vector256<float> prev = prv;
                    Vector256<float> cur = dst;
                    dst = Avx.Add(cur, Avx.Multiply(Avx.Multiply(r, vAmp), prev));
                }

                prv = r;
                vIx = Avx.Add(vIx, vEight);
            }
        }
        else if ((Sse41.IsSupported || AdvSimd.Arm64.IsSupported) && width >= 4)
        {
            Vector128<float> vStartX = Vector128.Create(startX);
            Vector128<float> vStepX = Vector128.Create(stepX);
            Vector128<float> vFreq = Vector128.Create(frequency);
            Vector128<float> vAmp = Vector128.Create(amplitude);
            Vector128<float> vOffset = Vector128.Create(offset);
            Vector128<float> vSix = Vector128.Create(6.0f);
            Vector128<float> vNeg15 = Vector128.Create(-15.0f);
            Vector128<float> vTen = Vector128.Create(10.0f);
            Vector128<float> vOneF = Vector128.Create(1.0f);
            Vector128<float> vSignMask = Vector128.Create(0x7FFFFFFFu).AsSingle();
            Vector128<float> vIx = Vector128.Create(0f, 1f, 2f, 3f);
            Vector128<float> vFour = Vector128.Create(4.0f);

            Span<Vector128<float>> rowV = MemoryMarshal.Cast<float, Vector128<float>>(row.Slice(0, width));
            Span<Vector128<float>> prevV = MemoryMarshal.Cast<float, Vector128<float>>(prevRow.Slice(0, width));
            for (int k = 0; k < rowV.Length; k++, x += 4)
            {
                Vector128<float> vx = (vStartX + vIx * vStepX) * vFreq;
                Vector128<float> vPxF = Sse41.IsSupported ? Sse41.Floor(vx) : AdvSimd.Floor(vx);
                Vector128<float> fx = vx - vPxF;
                Vector128<int> px = Sse41.IsSupported ? Sse2.ConvertToVector128Int32(vPxF) : AdvSimd.ConvertToInt32RoundToZero(vPxF);

                int p0 = px.GetElement(0);
                int p3 = px.GetElement(3);
                Vector128<float> a0, b0, a1, b1;
                if (p0 == p3)
                {
                    int i0 = p0 & 255;
                    int i1 = (p0 + 1) & 255;
                    a0 = Vector128.Create(aLut[i0]);
                    b0 = Vector128.Create(bLut[i0]);
                    a1 = Vector128.Create(aLut[i1]);
                    b1 = Vector128.Create(bLut[i1]);
                }
                else
                {
                    Gather4Linear(px, 255, aLut, bLut, out a0, out b0, out a1, out b1);
                }

                Vector128<float> u = (((fx * vSix + vNeg15) * fx + vTen) * fx) * fx * fx;
                Vector128<float> n0 = a0 * fx + b0;
                Vector128<float> n1 = a1 * (fx - vOneF) + b1;
                Vector128<float> n = n0 + (n1 - n0) * u;

                Vector128<float> r = vOffset - (n & vSignMask);
                r *= r;

                ref Vector128<float> dst = ref rowV[k];
                ref Vector128<float> prv = ref prevV[k];
                if (isFirstOctave)
                {
                    dst = (r * vAmp);
                }
                else
                {
                    dst = (dst + r * vAmp * prv);
                }

                prv = r;
                vIx += vFour;
            }
        }
#endif

        for (; x < width; ++x)
        {
            float vx = (startX + x * stepX) * frequency;
            int px = FastFloor(vx);
            float fx = vx - px;
            int i0 = px & 255;
            int i1 = (px + 1) & 255;
            float n0 = aLut[i0] * fx + bLut[i0];
            float n1 = aLut[i1] * (fx - 1.0f) + bLut[i1];
            float r = offset - MathF.Abs(Lerp(n0, n1, Ease(fx)));
            r *= r;

            if (isFirstOctave)
            {
                row[x] = r * amplitude;
            }
            else
            {
                row[x] += r * amplitude * prevRow[x];
            }

            prevRow[x] = r;
        }
    }

#if NET8_0_OR_GREATER
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void Gather8Linear(
        Vector256<int> px,
        int xMask,
        Span<float> aLut,
        Span<float> bLut,
        out Vector256<float> a0,
        out Vector256<float> b0,
        out Vector256<float> a1,
        out Vector256<float> b1)
    {
        aLut = aLut.Slice(0, 256);
        bLut = bLut.Slice(0, 256);
        int p0 = px.GetElement(0), p1 = px.GetElement(1), p2 = px.GetElement(2), p3 = px.GetElement(3);
        int p4 = px.GetElement(4), p5 = px.GetElement(5), p6 = px.GetElement(6), p7 = px.GetElement(7);

        byte i00 = (byte)(p0 & xMask), i01 = (byte)(p1 & xMask), i02 = (byte)(p2 & xMask), i03 = (byte)(p3 & xMask);
        byte i04 = (byte)(p4 & xMask), i05 = (byte)(p5 & xMask), i06 = (byte)(p6 & xMask), i07 = (byte)(p7 & xMask);
        byte i10 = (byte)((p0 + 1) & xMask), i11 = (byte)((p1 + 1) & xMask), i12 = (byte)((p2 + 1) & xMask), i13 = (byte)((p3 + 1) & xMask);
        byte i14 = (byte)((p4 + 1) & xMask), i15 = (byte)((p5 + 1) & xMask), i16 = (byte)((p6 + 1) & xMask), i17 = (byte)((p7 + 1) & xMask);

        a0 = Vector256.Create(
            aLut[i00], aLut[i01], aLut[i02], aLut[i03],
            aLut[i04], aLut[i05], aLut[i06], aLut[i07]);
        b0 = Vector256.Create(
            bLut[i00], bLut[i01], bLut[i02], bLut[i03],
            bLut[i04], bLut[i05], bLut[i06], bLut[i07]);
        a1 = Vector256.Create(
            aLut[i10], aLut[i11], aLut[i12], aLut[i13],
            aLut[i14], aLut[i15], aLut[i16], aLut[i17]);
        b1 = Vector256.Create(
            bLut[i10], bLut[i11], bLut[i12], bLut[i13],
            bLut[i14], bLut[i15], bLut[i16], bLut[i17]);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void Gather4Linear(
        Vector128<int> px,
        int xMask,
        Span<float> aLut,
        Span<float> bLut,
        out Vector128<float> a0,
        out Vector128<float> b0,
        out Vector128<float> a1,
        out Vector128<float> b1)
    {
        aLut = aLut.Slice(0, 256);
        bLut = bLut.Slice(0, 256);
        int p0 = px.GetElement(0), p1 = px.GetElement(1), p2 = px.GetElement(2), p3 = px.GetElement(3);
        byte i00 = (byte)(p0 & xMask), i01 = (byte)(p1 & xMask), i02 = (byte)(p2 & xMask), i03 = (byte)(p3 & xMask);
        byte i10 = (byte)((p0 + 1) & xMask), i11 = (byte)((p1 + 1) & xMask), i12 = (byte)((p2 + 1) & xMask), i13 = (byte)((p3 + 1) & xMask);

        a0 = Vector128.Create(aLut[i00], aLut[i01], aLut[i02], aLut[i03]);
        b0 = Vector128.Create(bLut[i00], bLut[i01], bLut[i02], bLut[i03]);
        a1 = Vector128.Create(aLut[i10], aLut[i11], aLut[i12], aLut[i13]);
        b1 = Vector128.Create(bLut[i10], bLut[i11], bLut[i12], bLut[i13]);
    }
#endif

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static float Lerp(float a, float b, float t) => a + (b - a) * t;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int FastFloor(float a)
    {
        int ai = (int)a;
        return a < ai ? ai - 1 : ai;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static float Ease(float a) => ((a * 6.0f - 15.0f) * a + 10.0f) * a * a * a;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static float Dease(float a) => ((a * 30.0f - 60.0f) * a + 30.0f) * a * a;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static float Grad(int gradIdx, float x, float y, float z) =>
        PerlinTables.BasisX[gradIdx] * x
        + PerlinTables.BasisY[gradIdx] * y
        + PerlinTables.BasisZ[gradIdx] * z;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static float Noise3Internal(float x, float y, float z, int xWrap, int yWrap, int zWrap, byte seed)
    {
        int xMask = (xWrap - 1) & 255;
        int yMask = (yWrap - 1) & 255;
        int zMask = (zWrap - 1) & 255;
        return Noise3Masked(x, y, z, xMask, yMask, zMask, seed);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static float Noise3Masked(float x, float y, float z, int xMask, int yMask, int zMask, byte seed)
    {
        int px = FastFloor(x);
        int py = FastFloor(y);
        int pz = FastFloor(z);
        int x0 = px & xMask, x1 = (px + 1) & xMask;
        int y0 = py & yMask, y1 = (py + 1) & yMask;
        int z0 = pz & zMask, z1 = (pz + 1) & zMask;

        byte[] rand = PerlinTables.RandTab;
        int r0 = rand[x0 + seed];
        int r1 = rand[x1 + seed];

        return Noise3Impl(y0, z0, y1, z1, r0, r1, x - px, y - py, z - pz);
    }

    private static void ResolveNonPow2Coords(
        float x,
        float y,
        float z,
        int xWrap,
        int yWrap,
        int zWrap,
        int seed,
        out int y0,
        out int z0,
        out int y1,
        out int z1,
        out int r0,
        out int r1,
        out float fx,
        out float fy,
        out float fz)
    {
        int px = FastFloor(x);
        int py = FastFloor(y);
        int pz = FastFloor(z);
        fx = x - px;
        fy = y - py;
        fz = z - pz;
        seed &= 255;

        int x0, x1;
        if (((xWrap & (xWrap - 1)) | (yWrap & (yWrap - 1)) | (zWrap & (zWrap - 1))) != 0)
        {
            int xWrap2 = (xWrap > 0 && xWrap < 256) ? xWrap : 256;
            int yWrap2 = (yWrap > 0 && yWrap < 256) ? yWrap : 256;
            int zWrap2 = (zWrap > 0 && zWrap < 256) ? zWrap : 256;

            x0 = px % xWrap2;
            y0 = py % yWrap2;
            z0 = pz % zWrap2;
            if (x0 < 0) x0 += xWrap2;
            if (y0 < 0) y0 += yWrap2;
            if (z0 < 0) z0 += zWrap2;

            x1 = (x0 + 1) % xWrap2;
            y1 = (y0 + 1) % yWrap2;
            z1 = (z0 + 1) % zWrap2;
        }
        else
        {
            int xMask = (xWrap - 1) & 255;
            int yMask = (yWrap - 1) & 255;
            int zMask = (zWrap - 1) & 255;
            x0 = px & xMask;
            x1 = (px + 1) & xMask;
            y0 = py & yMask;
            y1 = (py + 1) & yMask;
            z0 = pz & zMask;
            z1 = (pz + 1) & zMask;
        }

        byte[] rand = PerlinTables.RandTab;
        r0 = rand[rand[x0] + seed];
        r1 = rand[rand[x1] + seed];
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static float Noise3Impl(int y0, int z0, int y1, int z1, int r0, int r1, float x, float y, float z)
    {
        float u = Ease(x);
        float v = Ease(y);
        float w = Ease(z);

        byte[] rand = PerlinTables.RandTab;
        byte[] gradIdx = PerlinTables.RandTabGradIdx;

        int r00 = rand[r0 + y0];
        int r01 = rand[r0 + y1];
        int r10 = rand[r1 + y0];
        int r11 = rand[r1 + y1];

        float x1 = x - 1.0f;
        float y1f = y - 1.0f;
        float z1f = z - 1.0f;

        float n000 = Grad(gradIdx[r00 + z0], x, y, z);
        float n001 = Grad(gradIdx[r00 + z1], x, y, z1f);
        float n010 = Grad(gradIdx[r01 + z0], x, y1f, z);
        float n011 = Grad(gradIdx[r01 + z1], x, y1f, z1f);
        float n100 = Grad(gradIdx[r10 + z0], x1, y, z);
        float n101 = Grad(gradIdx[r10 + z1], x1, y, z1f);
        float n110 = Grad(gradIdx[r11 + z0], x1, y1f, z);
        float n111 = Grad(gradIdx[r11 + z1], x1, y1f, z1f);

        float n00 = Lerp(n000, n001, w);
        float n01 = Lerp(n010, n011, w);
        float n10 = Lerp(n100, n101, w);
        float n11 = Lerp(n110, n111, w);

        float n0 = Lerp(n00, n01, v);
        float n1 = Lerp(n10, n11, v);

        return Lerp(n0, n1, u);
    }

    private static float Noise3ImplDerivatives(
        int y0,
        int z0,
        int y1,
        int z1,
        int r0,
        int r1,
        float x,
        float y,
        float z,
        out float dx,
        out float dy,
        out float dz)
    {
        float u = Ease(x);
        float v = Ease(y);
        float w = Ease(z);

        byte[] rand = PerlinTables.RandTab;
        byte[] gradIdx = PerlinTables.RandTabGradIdx;

        int r00 = rand[r0 + y0];
        int r01 = rand[r0 + y1];
        int r10 = rand[r1 + y0];
        int r11 = rand[r1 + y1];

        int g000 = gradIdx[r00 + z0];
        int g001 = gradIdx[r00 + z1];
        int g010 = gradIdx[r01 + z0];
        int g011 = gradIdx[r01 + z1];
        int g100 = gradIdx[r10 + z0];
        int g101 = gradIdx[r10 + z1];
        int g110 = gradIdx[r11 + z0];
        int g111 = gradIdx[r11 + z1];

        float x1 = x - 1.0f;
        float y1f = y - 1.0f;
        float z1f = z - 1.0f;

        float n000 = Grad(g000, x, y, z);
        float n001 = Grad(g001, x, y, z1f);
        float n010 = Grad(g010, x, y1f, z);
        float n011 = Grad(g011, x, y1f, z1f);
        float n100 = Grad(g100, x1, y, z);
        float n101 = Grad(g101, x1, y, z1f);
        float n110 = Grad(g110, x1, y1f, z);
        float n111 = Grad(g111, x1, y1f, z1f);

        float n00 = Lerp(n000, n001, w);
        float n01 = Lerp(n010, n011, w);
        float n10 = Lerp(n100, n101, w);
        float n11 = Lerp(n110, n111, w);

        float n0 = Lerp(n00, n01, v);
        float n1 = Lerp(n10, n11, v);

        float n = Lerp(n0, n1, u);

        float du = Dease(x);
        float dv = Dease(y);
        float dw = Dease(z);

        dx = ComputeDerivativeAxis(PerlinTables.BasisX, g000, g001, g010, g011, g100, g101, g110, g111, n000, n001, n010, n011, n100, n101, n110, n111, u, v, w, -du, du, 1.0f - v, v, 1.0f - w, w);
        dy = ComputeDerivativeAxis(PerlinTables.BasisY, g000, g001, g010, g011, g100, g101, g110, g111, n000, n001, n010, n011, n100, n101, n110, n111, u, v, w, 1.0f - u, u, -dv, dv, 1.0f - w, w);
        dz = ComputeDerivativeAxis(PerlinTables.BasisZ, g000, g001, g010, g011, g100, g101, g110, g111, n000, n001, n010, n011, n100, n101, n110, n111, u, v, w, 1.0f - u, u, 1.0f - v, v, -dw, dw);

        return n;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static float ComputeDerivativeAxis(
        float[] basis,
        int g000, int g001, int g010, int g011, int g100, int g101, int g110, int g111,
        float n000, float n001, float n010, float n011, float n100, float n101, float n110, float n111,
        float u, float v, float w,
        float u0, float u1, float v0, float v1, float w0, float w1)
    {
        float d00 = Lerp(basis[g000], basis[g001], w);
        float d01 = Lerp(basis[g010], basis[g011], w);
        float d10 = Lerp(basis[g100], basis[g101], w);
        float d11 = Lerp(basis[g110], basis[g111], w);
        float d = Lerp(Lerp(d00, d01, v), Lerp(d10, d11, v), u);

        float t00 = n000 * w0 + n001 * w1;
        float t01 = n010 * w0 + n011 * w1;
        float t10 = n100 * w0 + n101 * w1;
        float t11 = n110 * w0 + n111 * w1;
        float t0 = t00 * v0 + t01 * v1;
        float t1 = t10 * v0 + t11 * v1;
        float t = t0 * u0 + t1 * u1;

        return d + t;
    }
}
