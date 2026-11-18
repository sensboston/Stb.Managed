using Stb.Managed.Oracle;
using Stb.Managed.Perlin;
using Xunit;

namespace Stb.Managed.Tests.Perlin;

public sealed class PerlinTests
{
    private const float ScalarTolerance = 1e-6f;
    private const float GridTolerance = 2e-6f;

    [Fact]
    public void ScalarNoise3AndSeedMatchNativeOracle()
    {
        var rng = new Random(1337);
        int[] wraps = [0, 1, 2, 4, 8, 16, 64, 256];
        int[] seeds = [0, 1, 42, 127, 255, 300, -17];

        for (int i = 0; i < 400; ++i)
        {
            float x = (float)(rng.NextDouble() * 400.0 - 200.0);
            float y = (float)(rng.NextDouble() * 400.0 - 200.0);
            float z = (float)(rng.NextDouble() * 400.0 - 200.0);
            int xw = wraps[i % wraps.Length];
            int yw = wraps[(i / 3) % wraps.Length];
            int zw = wraps[(i / 7) % wraps.Length];
            int seed = seeds[i % seeds.Length];

            float expected = StbOracle.PerlinNoise3(x, y, z, xw, yw, zw, seed);
            float actual = StbPerlin.Noise3Seed(x, y, z, xw, yw, zw, seed);
            Assert.True(MathF.Abs(expected - actual) <= ScalarTolerance, $"Mismatch at ({x},{y},{z}) wrap=({xw},{yw},{zw}) seed={seed}: expected={expected} actual={actual}");

            if (seed == 0)
            {
                float actualDefault = StbPerlin.Noise3(x, y, z, xw, yw, zw);
                Assert.Equal(actual, actualDefault);
            }
        }
    }

    [Fact]
    public void WrapNonPow2AndDerivativesMatchNativeOracle()
    {
        var rng = new Random(2026);
        int[] wraps = [0, 1, 3, 5, 7, 13, 16, 100, 255, 256, 300, -4];

        for (int i = 0; i < 300; ++i)
        {
            float x = (float)(rng.NextDouble() * 100.0 - 50.0);
            float y = (float)(rng.NextDouble() * 100.0 - 50.0);
            float z = (float)(rng.NextDouble() * 100.0 - 50.0);
            int xw = wraps[i % wraps.Length];
            int yw = wraps[(i * 3) % wraps.Length];
            int zw = wraps[(i * 5) % wraps.Length];
            byte seed = (byte)(i * 19);

            float expectedNonPow2 = StbOracle.PerlinNoise3WrapNonPow2(x, y, z, xw, yw, zw, seed);
            float actualNonPow2 = StbPerlin.Noise3WrapNonPow2(x, y, z, xw, yw, zw, seed);
            Assert.True(MathF.Abs(expectedNonPow2 - actualNonPow2) <= ScalarTolerance);

            float expectedVal = StbOracle.PerlinNoise3Derivatives(x, y, z, xw, yw, zw, seed, out float edx, out float edy, out float edz);
            float actualVal = StbPerlin.Noise3Derivatives(x, y, z, xw, yw, zw, seed, out float adx, out float ady, out float adz);

            Assert.True(MathF.Abs(expectedVal - actualVal) <= ScalarTolerance);
            Assert.True(MathF.Abs(edx - adx) <= 2e-6f);
            Assert.True(MathF.Abs(edy - ady) <= 2e-6f);
            Assert.True(MathF.Abs(edz - adz) <= 2e-6f);
        }

        // Finite-difference sanity check for analytical derivatives at an interior point.
        const float px = 3.37f;
        const float py = -2.61f;
        const float pz = 1.43f;
        const float eps = 1e-3f;
        _ = StbPerlin.Noise3Derivatives(px, py, pz, 0, 0, 0, 7, out float dx, out float dy, out float dz);
        float fdX = (StbPerlin.Noise3WrapNonPow2(px + eps, py, pz, 0, 0, 0, 7) - StbPerlin.Noise3WrapNonPow2(px - eps, py, pz, 0, 0, 0, 7)) / (2f * eps);
        float fdY = (StbPerlin.Noise3WrapNonPow2(px, py + eps, pz, 0, 0, 0, 7) - StbPerlin.Noise3WrapNonPow2(px, py - eps, pz, 0, 0, 0, 7)) / (2f * eps);
        float fdZ = (StbPerlin.Noise3WrapNonPow2(px, py, pz + eps, 0, 0, 0, 7) - StbPerlin.Noise3WrapNonPow2(px, py, pz - eps, 0, 0, 0, 7)) / (2f * eps);
        Assert.InRange(MathF.Abs(dx - fdX), 0f, 1e-3f);
        Assert.InRange(MathF.Abs(dy - fdY), 0f, 1e-3f);
        Assert.InRange(MathF.Abs(dz - fdZ), 0f, 1e-3f);
    }

    [Fact]
    public void FractalNoise3RidgeFbmTurbulenceMatchNativeOracle()
    {
        var rng = new Random(9001);
        int[] octaveCounts = [0, 1, 3, 6, 8];

        for (int i = 0; i < 150; ++i)
        {
            float x = (float)(rng.NextDouble() * 40.0 - 20.0);
            float y = (float)(rng.NextDouble() * 40.0 - 20.0);
            float z = (float)(rng.NextDouble() * 40.0 - 20.0);
            float lac = i % 2 == 0 ? 2.0f : 2.13f;
            float gain = i % 3 == 0 ? 0.5f : 0.45f;
            float offset = i % 4 == 0 ? 1.0f : 1.25f;
            int octaves = octaveCounts[i % octaveCounts.Length];

            float eFbm = StbOracle.PerlinFbmNoise3(x, y, z, lac, gain, octaves);
            float aFbm = StbPerlin.FbmNoise3(x, y, z, lac, gain, octaves);
            Assert.True(MathF.Abs(eFbm - aFbm) <= 3e-6f);

            float eRidge = StbOracle.PerlinRidgeNoise3(x, y, z, lac, gain, offset, octaves);
            float aRidge = StbPerlin.RidgeNoise3(x, y, z, lac, gain, offset, octaves);
            Assert.True(MathF.Abs(eRidge - aRidge) <= 3e-6f);

            float eTurb = StbOracle.PerlinTurbulenceNoise3(x, y, z, lac, gain, octaves);
            float aTurb = StbPerlin.TurbulenceNoise3(x, y, z, lac, gain, octaves);
            Assert.True(MathF.Abs(eTurb - aTurb) <= 3e-6f);
        }
    }

    [Fact]
    public void WrappingPeriodicityHoldsForPow2AndNonPow2()
    {
        Assert.Equal(
            StbPerlin.Noise3(1.25f, 2.5f, 3.75f, 16, 8, 4),
            StbPerlin.Noise3(1.25f + 16f, 2.5f - 8f, 3.75f + 12f, 16, 8, 4));

        Assert.Equal(
            StbPerlin.Noise3WrapNonPow2(1.25f, 2.5f, 3.75f, 5, 7, 3, seed: 9),
            StbPerlin.Noise3WrapNonPow2(1.25f + 5f, 2.5f - 14f, 3.75f + 9f, 5, 7, 3, seed: 9));
    }

    [Theory]
    [InlineData(1, 1, -1.5f, 2.25f, 0.05f, 0.05f, 0.3f, 0, 0, 0, 0)]
    [InlineData(7, 5, 0.1f, -3.4f, 0.37f, -0.29f, 1.1f, 8, 16, 4, 17)]
    [InlineData(65, 33, -10.25f, 4.75f, 0.03125f, 0.045f, -0.7f, 0, 0, 0, 42)]
    [InlineData(128, 64, 0.0f, 0.0f, 0.75f, 0.75f, 2.5f, 64, 64, 0, 99)]
    public void FillNoise2DMatchesNativeOracle(
        int width,
        int height,
        float startX,
        float startY,
        float stepX,
        float stepY,
        float z,
        int xWrap,
        int yWrap,
        int zWrap,
        int seed)
    {
        float[] native = new float[width * height];
        float[] managed = new float[width * height];

        StbOracle.PerlinFillNoise2D(native, width, height, startX, startY, stepX, stepY, z, xWrap, yWrap, zWrap, seed);
        StbPerlin.FillNoise2D(managed, width, height, startX, startY, stepX, stepY, z, xWrap, yWrap, zWrap, seed);

        for (int i = 0; i < native.Length; ++i)
        {
            Assert.True(
                MathF.Abs(native[i] - managed[i]) <= GridTolerance,
                $"FillNoise2D diff at {i}: native={native[i]} managed={managed[i]}");
        }
    }

    [Theory]
    [InlineData(13, 9, -2.5f, 1.3f, 0.04f, 0.04f, 0.5f, 2.0f, 0.5f, 1.0f, 6)]
    [InlineData(64, 37, 0.0f, 0.0f, 0.02f, -0.03f, -1.2f, 2.1f, 0.48f, 1.15f, 4)]
    [InlineData(17, 5, 1.0f, 1.0f, 0.1f, 0.1f, 0.0f, 2.0f, 0.5f, 1.0f, 0)]
    public void FillFractal2DFbmRidgeTurbulenceMatchNativeOracle(
        int width,
        int height,
        float startX,
        float startY,
        float stepX,
        float stepY,
        float z,
        float lacunarity,
        float gain,
        float offset,
        int octaves)
    {
        float[] native = new float[width * height];
        float[] managed = new float[width * height];

        StbOracle.PerlinFillFbm2D(native, width, height, startX, startY, stepX, stepY, z, lacunarity, gain, octaves);
        StbPerlin.FillFbm2D(managed, width, height, startX, startY, stepX, stepY, z, lacunarity, gain, octaves);
        for (int i = 0; i < native.Length; ++i)
        {
            Assert.True(MathF.Abs(native[i] - managed[i]) <= 5e-6f, $"FBM diff at {i}: native={native[i]} managed={managed[i]}");
        }

        StbOracle.PerlinFillRidge2D(native, width, height, startX, startY, stepX, stepY, z, lacunarity, gain, offset, octaves);
        StbPerlin.FillRidge2D(managed, width, height, startX, startY, stepX, stepY, z, lacunarity, gain, offset, octaves);
        for (int i = 0; i < native.Length; ++i)
        {
            Assert.True(MathF.Abs(native[i] - managed[i]) <= 1e-5f, $"Ridge diff at {i}: native={native[i]} managed={managed[i]}");
        }

        StbOracle.PerlinFillTurbulence2D(native, width, height, startX, startY, stepX, stepY, z, lacunarity, gain, octaves);
        StbPerlin.FillTurbulence2D(managed, width, height, startX, startY, stepX, stepY, z, lacunarity, gain, octaves);
        for (int i = 0; i < native.Length; ++i)
        {
            Assert.True(MathF.Abs(native[i] - managed[i]) <= 5e-6f, $"Turbulence diff at {i}: native={native[i]} managed={managed[i]}");
        }
    }

    [Fact]
    public void Fill2DParallelMatchesSingleThreadedBitForBit()
    {
        const int width = 256;
        const int height = 256;
        var singleOpt = new StbOptions { MaxDegreeOfParallelism = 1 };
        var parOpt = new StbOptions { MaxDegreeOfParallelism = 0 };

        float[] s1 = new float[width * height];
        float[] p1 = new float[width * height];

        StbPerlin.FillNoise2DMemory(s1, width, height, -3.2f, 1.4f, 0.03f, 0.03f, 0.7f, 0, 0, 0, 11, 0, singleOpt);
        StbPerlin.FillNoise2DMemory(p1, width, height, -3.2f, 1.4f, 0.03f, 0.03f, 0.7f, 0, 0, 0, 11, 0, parOpt);
        Assert.Equal(s1, p1);

        StbPerlin.FillFbm2DMemory(s1, width, height, -3.2f, 1.4f, 0.02f, 0.02f, 0.7f, 2.0f, 0.5f, 6, 0, singleOpt);
        StbPerlin.FillFbm2DMemory(p1, width, height, -3.2f, 1.4f, 0.02f, 0.02f, 0.7f, 2.0f, 0.5f, 6, 0, parOpt);
        Assert.Equal(s1, p1);

        StbPerlin.FillRidge2DMemory(s1, width, height, -3.2f, 1.4f, 0.02f, 0.02f, 0.7f, 2.0f, 0.5f, 1.0f, 6, 0, singleOpt);
        StbPerlin.FillRidge2DMemory(p1, width, height, -3.2f, 1.4f, 0.02f, 0.02f, 0.7f, 2.0f, 0.5f, 1.0f, 6, 0, parOpt);
        Assert.Equal(s1, p1);

        StbPerlin.FillTurbulence2DMemory(s1, width, height, -3.2f, 1.4f, 0.02f, 0.02f, 0.7f, 2.0f, 0.5f, 6, 0, singleOpt);
        StbPerlin.FillTurbulence2DMemory(p1, width, height, -3.2f, 1.4f, 0.02f, 0.02f, 0.7f, 2.0f, 0.5f, 6, 0, parOpt);
        Assert.Equal(s1, p1);
    }

    [Fact]
    public void FillNoise3BatchAndStridedGridWorkCorrectly()
    {
        var rng = new Random(555);
        float[] xs = new float[128];
        float[] ys = new float[128];
        float[] zs = new float[128];
        float[] dst = new float[128];
        for (int i = 0; i < 128; ++i)
        {
            xs[i] = (float)(rng.NextDouble() * 20.0 - 10.0);
            ys[i] = (float)(rng.NextDouble() * 20.0 - 10.0);
            zs[i] = (float)(rng.NextDouble() * 20.0 - 10.0);
        }

        StbPerlin.FillNoise3Batch(xs, ys, zs, dst, 16, 16, 16, seed: 5);
        for (int i = 0; i < 128; ++i)
        {
            float expected = StbOracle.PerlinNoise3(xs[i], ys[i], zs[i], 16, 16, 16, seed: 5);
            Assert.True(MathF.Abs(expected - dst[i]) <= ScalarTolerance);
        }

        // Strided grid preserves padding values untouched.
        const int w = 10;
        const int h = 6;
        const int stride = 14;
        float[] strided = new float[h * stride];
        Array.Fill(strided, -999f);
        StbPerlin.FillNoise2D(strided, w, h, 0f, 0f, 0.1f, 0.1f, destinationStride: stride);
        for (int y = 0; y < h; ++y)
        {
            for (int x = w; x < stride; ++x)
            {
                Assert.Equal(-999f, strided[y * stride + x]);
            }
        }
    }

    [Fact]
    public void PerlinInvalidArgumentsThrowExpectedExceptions()
    {
        Assert.Throws<StbException>(() => StbPerlin.FillNoise2D(new float[16], 0, 4, 0f, 0f, 0.1f, 0.1f));
        Assert.Throws<StbException>(() => StbPerlin.FillNoise2D(new float[16], 4, -1, 0f, 0f, 0.1f, 0.1f));
        Assert.Throws<ArgumentException>(() => StbPerlin.FillNoise2D(new float[15], 4, 4, 0f, 0f, 0.1f, 0.1f));
        Assert.Throws<ArgumentOutOfRangeException>(() => StbPerlin.FillNoise2D(new float[32], 8, 4, 0f, 0f, 0.1f, 0.1f, destinationStride: 4));

        Assert.Throws<ArgumentException>(() => StbPerlin.FillNoise3Batch(new float[4], new float[3], new float[4], new float[4]));
        Assert.Throws<ArgumentException>(() => StbPerlin.FillNoise3Batch(new float[4], new float[4], new float[3], new float[4]));
        Assert.Throws<ArgumentException>(() => StbPerlin.FillNoise3Batch(new float[4], new float[4], new float[4], new float[3]));
    }
}
