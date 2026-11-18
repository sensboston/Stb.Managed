using System.Runtime.CompilerServices;
using ILGPU;
using ILGPU.Algorithms;
using ILGPU.Runtime;

namespace Stb.Managed.Gpu.Kernels;

internal readonly struct Perlin2DParams
{
    public readonly int Width;
    public readonly int Height;
    public readonly float StartX;
    public readonly float StartY;
    public readonly float StepX;
    public readonly float StepY;
    public readonly float Z;
    public readonly float Lacunarity;
    public readonly float Gain;
    public readonly float RidgeOffset;
    public readonly int Octaves;
    public readonly int WrapX;
    public readonly int WrapY;
    public readonly int WrapZ;
    public readonly int Seed;
    public readonly int Mode; // 0 = Noise, 1 = Fbm, 2 = Ridge, 3 = Turbulence

    public Perlin2DParams(
        int width,
        int height,
        float startX,
        float startY,
        float stepX,
        float stepY,
        float z,
        float lacunarity,
        float gain,
        float ridgeOffset,
        int octaves,
        int wrapX,
        int wrapY,
        int wrapZ,
        int seed,
        int mode)
    {
        Width = width;
        Height = height;
        StartX = startX;
        StartY = startY;
        StepX = stepX;
        StepY = stepY;
        Z = z;
        Lacunarity = lacunarity;
        Gain = gain;
        RidgeOffset = ridgeOffset;
        Octaves = octaves;
        WrapX = wrapX;
        WrapY = wrapY;
        WrapZ = wrapZ;
        Seed = seed;
        Mode = mode;
    }
}

internal readonly struct Perlin3DParams
{
    public readonly int Width;
    public readonly int Height;
    public readonly int Depth;
    public readonly float StartX;
    public readonly float StartY;
    public readonly float StartZ;
    public readonly float StepX;
    public readonly float StepY;
    public readonly float StepZ;
    public readonly float Lacunarity;
    public readonly float Gain;
    public readonly int Octaves;
    public readonly int WrapX;
    public readonly int WrapY;
    public readonly int WrapZ;
    public readonly int Seed;

    public Perlin3DParams(
        int width,
        int height,
        int depth,
        float startX,
        float startY,
        float startZ,
        float stepX,
        float stepY,
        float stepZ,
        float lacunarity,
        float gain,
        int octaves,
        int wrapX,
        int wrapY,
        int wrapZ,
        int seed)
    {
        Width = width;
        Height = height;
        Depth = depth;
        StartX = startX;
        StartY = startY;
        StartZ = startZ;
        StepX = stepX;
        StepY = stepY;
        StepZ = stepZ;
        Lacunarity = lacunarity;
        Gain = gain;
        Octaves = octaves;
        WrapX = wrapX;
        WrapY = wrapY;
        WrapZ = wrapZ;
        Seed = seed;
    }
}

/// <summary>
/// ILGPU device kernels implementing <c>stb_perlin.h</c> v0.6 2D/3D Perlin noise, fBm, ridge, and turbulence.
/// Zero input upload per dispatch (only 512-byte permutation tables resident in VRAM).
/// </summary>
internal static class PerlinGpuKernels
{
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static float Lerp(float t, float a, float b) => a + t * (b - a);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static float Grad(
        int idx,
        float x,
        float y,
        float z,
        ArrayView1D<byte, Stride1D.Dense> randTabGradIdx,
        ArrayView1D<float, Stride1D.Dense> basisX,
        ArrayView1D<float, Stride1D.Dense> basisY,
        ArrayView1D<float, Stride1D.Dense> basisZ)
    {
        int g = randTabGradIdx[idx];
        return basisX[g] * x + basisY[g] * y + basisZ[g] * z;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static float Noise3SeedDevice(
        float x,
        float y,
        float z,
        int xWrap,
        int yWrap,
        int zWrap,
        byte seed,
        ArrayView1D<byte, Stride1D.Dense> randTab,
        ArrayView1D<byte, Stride1D.Dense> randTabGradIdx,
        ArrayView1D<float, Stride1D.Dense> basisX,
        ArrayView1D<float, Stride1D.Dense> basisY,
        ArrayView1D<float, Stride1D.Dense> basisZ)
    {
        int xMask = (xWrap - 1) & 255;
        int yMask = (yWrap - 1) & 255;
        int zMask = (zWrap - 1) & 255;

        float fx = XMath.Floor(x);
        float fy = XMath.Floor(y);
        float fz = XMath.Floor(z);

        int px = (int)fx;
        int py = (int)fy;
        int pz = (int)fz;

        x -= fx;
        y -= fy;
        z -= fz;

        int x0 = px & xMask;
        int x1 = (px + 1) & xMask;
        int y0 = py & yMask;
        int y1 = (py + 1) & yMask;
        int z0 = pz & zMask;
        int z1 = (pz + 1) & zMask;

        int r1 = randTab[randTab[x0 + seed] + y0];
        int r2 = randTab[randTab[x0 + seed] + y1];
        int r3 = randTab[randTab[x1 + seed] + y0];
        int r4 = randTab[randTab[x1 + seed] + y1];

        float u = x * x * x * (x * (x * 6.0f - 15.0f) + 10.0f);
        float v = y * y * y * (y * (y * 6.0f - 15.0f) + 10.0f);
        float w = z * z * z * (z * (z * 6.0f - 15.0f) + 10.0f);

        float n000 = Grad(r1 + z0, x, y, z, randTabGradIdx, basisX, basisY, basisZ);
        float n001 = Grad(r1 + z1, x, y, z - 1.0f, randTabGradIdx, basisX, basisY, basisZ);
        float n010 = Grad(r2 + z0, x, y - 1.0f, z, randTabGradIdx, basisX, basisY, basisZ);
        float n011 = Grad(r2 + z1, x, y - 1.0f, z - 1.0f, randTabGradIdx, basisX, basisY, basisZ);
        float n100 = Grad(r3 + z0, x - 1.0f, y, z, randTabGradIdx, basisX, basisY, basisZ);
        float n101 = Grad(r3 + z1, x - 1.0f, y, z - 1.0f, randTabGradIdx, basisX, basisY, basisZ);
        float n110 = Grad(r4 + z0, x - 1.0f, y - 1.0f, z, randTabGradIdx, basisX, basisY, basisZ);
        float n111 = Grad(r4 + z1, x - 1.0f, y - 1.0f, z - 1.0f, randTabGradIdx, basisX, basisY, basisZ);

        float nz00 = Lerp(w, n000, n001);
        float nz01 = Lerp(w, n010, n011);
        float nz10 = Lerp(w, n100, n101);
        float nz11 = Lerp(w, n110, n111);

        float ny0 = Lerp(v, nz00, nz01);
        float ny1 = Lerp(v, nz10, nz11);

        return Lerp(u, ny0, ny1);
    }

    /// <summary>
    /// Evaluates a 2D field of Perlin noise, fBm, ridge, or turbulence on GPU.
    /// </summary>
    public static void Fill2DKernel(
        Index1D idx,
        ArrayView1D<float, Stride1D.Dense> dst,
        ArrayView1D<byte, Stride1D.Dense> randTab,
        ArrayView1D<byte, Stride1D.Dense> randTabGradIdx,
        ArrayView1D<float, Stride1D.Dense> basisX,
        ArrayView1D<float, Stride1D.Dense> basisY,
        ArrayView1D<float, Stride1D.Dense> basisZ,
        Perlin2DParams p)
    {
        int py = idx / p.Width;
        int px = idx - py * p.Width;
        float x = p.StartX + px * p.StepX;
        float y = p.StartY + py * p.StepY;
        float z = p.Z;

        if (p.Mode == 0)
        {
            dst[idx] = Noise3SeedDevice(
                x, y, z, p.WrapX, p.WrapY, p.WrapZ, (byte)p.Seed,
                randTab, randTabGradIdx, basisX, basisY, basisZ);
            return;
        }

        if (p.Mode == 1)
        {
            float sum = 0.0f;
            float freq = 1.0f;
            float amp = 1.0f;
            for (int i = 0; i < p.Octaves; ++i)
            {
                sum += Noise3SeedDevice(
                    x * freq, y * freq, z * freq, p.WrapX, p.WrapY, p.WrapZ, (byte)(p.Seed + i),
                    randTab, randTabGradIdx, basisX, basisY, basisZ) * amp;
                freq *= p.Lacunarity;
                amp *= p.Gain;
            }

            dst[idx] = sum;
            return;
        }

        if (p.Mode == 2)
        {
            float sum = 0.0f;
            float freq = 1.0f;
            float amp = 0.5f;
            float prev = 1.0f;
            for (int i = 0; i < p.Octaves; ++i)
            {
                float n = Noise3SeedDevice(
                    x * freq, y * freq, z * freq, p.WrapX, p.WrapY, p.WrapZ, (byte)(p.Seed + i),
                    randTab, randTabGradIdx, basisX, basisY, basisZ);
                float r = p.RidgeOffset - XMath.Abs(n);
                r *= r;
                sum += r * amp * prev;
                prev = r;
                freq *= p.Lacunarity;
                amp *= p.Gain;
            }

            dst[idx] = sum;
            return;
        }

        // Mode == 3: Turbulence
        {
            float sum = 0.0f;
            float freq = 1.0f;
            float amp = 1.0f;
            for (int i = 0; i < p.Octaves; ++i)
            {
                float n = Noise3SeedDevice(
                    x * freq, y * freq, z * freq, p.WrapX, p.WrapY, p.WrapZ, (byte)(p.Seed + i),
                    randTab, randTabGradIdx, basisX, basisY, basisZ);
                sum += XMath.Abs(n) * amp;
                freq *= p.Lacunarity;
                amp *= p.Gain;
            }

            dst[idx] = sum;
        }
    }

    /// <summary>
    /// Evaluates a 3D volume of Perlin noise or fBm on GPU.
    /// </summary>
    public static void Fill3DKernel(
        Index1D idx,
        ArrayView1D<float, Stride1D.Dense> dst,
        ArrayView1D<byte, Stride1D.Dense> randTab,
        ArrayView1D<byte, Stride1D.Dense> randTabGradIdx,
        ArrayView1D<float, Stride1D.Dense> basisX,
        ArrayView1D<float, Stride1D.Dense> basisY,
        ArrayView1D<float, Stride1D.Dense> basisZ,
        Perlin3DParams p)
    {
        int slice = p.Width * p.Height;
        int pz = idx / slice;
        int rem = idx - pz * slice;
        int py = rem / p.Width;
        int px = rem - py * p.Width;

        float x = p.StartX + px * p.StepX;
        float y = p.StartY + py * p.StepY;
        float z = p.StartZ + pz * p.StepZ;

        if (p.Octaves <= 1)
        {
            dst[idx] = Noise3SeedDevice(
                x, y, z, p.WrapX, p.WrapY, p.WrapZ, (byte)p.Seed,
                randTab, randTabGradIdx, basisX, basisY, basisZ);
            return;
        }

        float sum = 0.0f;
        float freq = 1.0f;
        float amp = 1.0f;
        for (int i = 0; i < p.Octaves; ++i)
        {
            sum += Noise3SeedDevice(
                x * freq, y * freq, z * freq, p.WrapX, p.WrapY, p.WrapZ, (byte)(p.Seed + i),
                randTab, randTabGradIdx, basisX, basisY, basisZ) * amp;
            freq *= p.Lacunarity;
            amp *= p.Gain;
        }

        dst[idx] = sum;
    }
}
