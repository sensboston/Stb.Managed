using Stb.Managed.Dxt;
using Stb.Managed.Gpu;
using Stb.Managed.Oracle;
using Stb.Managed.Perlin;
using Xunit;

namespace Stb.Managed.Tests.Gpu;

[Collection(GpuTestSuite.Name)]
public sealed class GpuPerlinParityTests(GpuTestFixture fixture)
{
    private const float MaxFloatTolerance = 1e-5f;

    [Theory]
    [InlineData(128, 96, -3.5f, 1.25f, 0.07f, 0.09f, 0.42f, 0, 0, 0, 0)]
    [InlineData(128, 96, -10.0f, -5.0f, 0.13f, 0.11f, 2.7f, 64, 32, 16, 42)]
    [InlineData(97, 73, 0.0f, 0.0f, 0.05f, 0.05f, -1.5f, 256, 256, 0, 1337)]
    public void FillNoise2DMatchesCpuAndNativeOracle(
        int width, int height,
        float startX, float startY, float stepX, float stepY, float z,
        int xWrap, int yWrap, int zWrap, int seed)
    {
        int count = width * height;
        float[] cpu = new float[count];
        float[] gpu = new float[count];
        float[] native = new float[count];

        StbPerlin.FillNoise2D(cpu, width, height, startX, startY, stepX, stepY, z, xWrap, yWrap, zWrap, seed);
        StbGpuPerlin.FillNoise2D(fixture.Context, gpu, width, height, startX, startY, stepX, stepY, z, xWrap, yWrap, zWrap, seed, StbGpuExecutionMode.ForceGpu);
        StbOracle.PerlinFillNoise2D(native, width, height, startX, startY, stepX, stepY, z, xWrap, yWrap, zWrap, seed);

        AssertFloatsClose(cpu, gpu, MaxFloatTolerance);
        AssertFloatsClose(native, gpu, MaxFloatTolerance);
    }

    [Fact]
    public void FillFbmRidgeAndTurbulence2DMatchCpuAndNativeOracle()
    {
        const int width = 128;
        const int height = 96;
        const int count = width * height;
        const float startX = -2.25f;
        const float startY = 0.75f;
        const float stepX = 0.045f;
        const float stepY = 0.055f;
        const float z = 1.37f;

        float[] cpu = new float[count];
        float[] gpu = new float[count];
        float[] native = new float[count];

        // 1. fBm
        StbPerlin.FillFbm2D(cpu, width, height, startX, startY, stepX, stepY, z, lacunarity: 2.0f, gain: 0.5f, octaves: 6);
        StbGpuPerlin.FillFbm2D(fixture.Context, gpu, width, height, startX, startY, stepX, stepY, z, lacunarity: 2.0f, gain: 0.5f, octaves: 6, StbGpuExecutionMode.ForceGpu);
        StbOracle.PerlinFillFbm2D(native, width, height, startX, startY, stepX, stepY, z, lacunarity: 2.0f, gain: 0.5f, octaves: 6);
        AssertFloatsClose(cpu, gpu, MaxFloatTolerance);
        AssertFloatsClose(native, gpu, MaxFloatTolerance);

        // 2. Ridge
        StbPerlin.FillRidge2D(cpu, width, height, startX, startY, stepX, stepY, z, lacunarity: 2.1f, gain: 0.5f, offset: 1.0f, octaves: 5);
        StbGpuPerlin.FillRidge2D(fixture.Context, gpu, width, height, startX, startY, stepX, stepY, z, lacunarity: 2.1f, gain: 0.5f, offset: 1.0f, octaves: 5, StbGpuExecutionMode.ForceGpu);
        StbOracle.PerlinFillRidge2D(native, width, height, startX, startY, stepX, stepY, z, lacunarity: 2.1f, gain: 0.5f, offset: 1.0f, octaves: 5);
        AssertFloatsClose(cpu, gpu, MaxFloatTolerance);
        AssertFloatsClose(native, gpu, MaxFloatTolerance);

        // 3. Turbulence
        StbPerlin.FillTurbulence2D(cpu, width, height, startX, startY, stepX, stepY, z, lacunarity: 2.0f, gain: 0.5f, octaves: 5);
        StbGpuPerlin.FillTurbulence2D(fixture.Context, gpu, width, height, startX, startY, stepX, stepY, z, lacunarity: 2.0f, gain: 0.5f, octaves: 5, StbGpuExecutionMode.ForceGpu);
        StbOracle.PerlinFillTurbulence2D(native, width, height, startX, startY, stepX, stepY, z, lacunarity: 2.0f, gain: 0.5f, octaves: 5);
        AssertFloatsClose(cpu, gpu, MaxFloatTolerance);
        AssertFloatsClose(native, gpu, MaxFloatTolerance);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(4)]
    public void FillVolume3DMatchesCpuSliceBySlice(int octaves)
    {
        const int w = 32;
        const int h = 24;
        const int d = 6;
        int total = w * h * d;
        float[] cpu = new float[total];
        float[] gpu = new float[total];

        StbGpuPerlin.FillVolume3D(fixture.Context, cpu, w, h, d, -1.0f, 0.5f, -0.5f, 0.1f, 0.1f, 0.2f, 2.0f, 0.5f, octaves, modeOverride: StbGpuExecutionMode.ForceCpu);
        StbGpuPerlin.FillVolume3D(fixture.Context, gpu, w, h, d, -1.0f, 0.5f, -0.5f, 0.1f, 0.1f, 0.2f, 2.0f, 0.5f, octaves, modeOverride: StbGpuExecutionMode.ForceGpu);

        AssertFloatsClose(cpu, gpu, MaxFloatTolerance);
    }

    [Fact]
    public void GenerateVramHeightmapChainsZeroCopyIntoColorResizeAndDxt()
    {
        // End-to-end VRAM-resident pipeline: Perlin fBm -> Heightmap RGBA8 -> Resize 128->64 -> BC1 DXT.
        using StbGpuImage<float> heightmap = StbGpuPerlin.GenerateFbm2DImage(
            fixture.Context, 128, 128, -2.0f, -2.0f, 0.05f, 0.05f, z: 0.3f, octaves: 5);
        Assert.Equal(128, heightmap.Width);
        Assert.Equal(128, heightmap.Height);
        Assert.Equal(ColorComponents.Grey, heightmap.Components);

        using StbGpuImage<byte> rgba128 = StbGpuColor.FloatHeightmapToRgbaGpuImage(fixture.Context, heightmap, minVal: -1.0f, maxVal: 1.0f);
        using StbGpuImage<byte> rgba64 = StbGpuImageResize.ResizeGpuImage(fixture.Context, rgba128, 64, 64, ResizeDataType.UInt8Linear, ResizeFilter.Triangle);
        byte[] dxtBlocks = StbGpuDxt.CompressGpuImage(fixture.Context, rgba64, DxtFormat.Bc1, DxtCompressionMode.Normal);

        Assert.Equal(StbDxt.GetCompressedSize(64, 64, DxtFormat.Bc1), dxtBlocks.Length);

        // Verify the VRAM-resident rgba64 matches downloading rgba128 and resizing on CPU.
        byte[] host128 = rgba128.DownloadToArray();
        byte[] host64Gpu = rgba64.DownloadToArray();
        byte[] host64Cpu = new byte[64 * 64 * 4];
        var opts = new ImageResizeOptions { HorizontalFilter = ResizeFilter.Triangle, VerticalFilter = ResizeFilter.Triangle };
        StbImageResize.Resize(host128, 128, 128, host64Cpu, 64, 64, ResizePixelLayout.Rgba, ResizeDataType.UInt8Linear, 0, 0, opts);

        int maxDiff = 0;
        for (int i = 0; i < host64Cpu.Length; ++i)
        {
            int d = Math.Abs(host64Cpu[i] - host64Gpu[i]);
            if (d > maxDiff) maxDiff = d;
        }

        Assert.True(maxDiff <= 1, $"VRAM chained resize differed from CPU by {maxDiff} LSB.");
    }

    private static void AssertFloatsClose(float[] expected, float[] actual, float tol)
    {
        Assert.Equal(expected.Length, actual.Length);
        float maxDiff = 0.0f;
        int worstIndex = -1;
        for (int i = 0; i < expected.Length; ++i)
        {
            Assert.False(float.IsNaN(actual[i]), $"NaN at index {i}.");
            float diff = MathF.Abs(expected[i] - actual[i]);
            if (diff > maxDiff)
            {
                maxDiff = diff;
                worstIndex = i;
            }
        }

        Assert.True(
            maxDiff <= tol,
            $"Max float delta {maxDiff} exceeded tolerance {tol} at index {worstIndex} (expected {expected[Math.Max(0, worstIndex)]}, actual {actual[Math.Max(0, worstIndex)]}).");
    }
}
