using System.Runtime.InteropServices;
using Stb.Managed.Dxt;
using Stb.Managed.Gpu;
using Stb.Managed.Oracle;
using Xunit;

namespace Stb.Managed.Tests.Gpu;

[Collection(GpuTestSuite.Name)]
public sealed class GpuImageResizeParityTests(GpuTestFixture fixture)
{
    [Theory]
    [InlineData(160, 120, 80, 60, ResizePixelLayout.Rgba, ResizeDataType.UInt8Srgb, ResizeFilter.Mitchell)]
    [InlineData(80, 60, 160, 120, ResizePixelLayout.Rgba, ResizeDataType.UInt8Srgb, ResizeFilter.CatmullRom)]
    [InlineData(128, 96, 53, 41, ResizePixelLayout.Rgba, ResizeDataType.UInt8Linear, ResizeFilter.Triangle)]
    [InlineData(128, 96, 64, 48, ResizePixelLayout.Rgba, ResizeDataType.UInt8SrgbAlpha, ResizeFilter.Box)]
    [InlineData(96, 80, 144, 112, ResizePixelLayout.Rgb, ResizeDataType.UInt8Srgb, ResizeFilter.CubicBSpline)]
    [InlineData(100, 75, 50, 38, ResizePixelLayout.OneChannel, ResizeDataType.UInt8Linear, ResizeFilter.PointSample)]
    [InlineData(96, 64, 48, 32, ResizePixelLayout.Ra, ResizeDataType.UInt8Srgb, ResizeFilter.Mitchell)]
    [InlineData(96, 64, 48, 32, ResizePixelLayout.RgbaPremultiplied, ResizeDataType.UInt8Srgb, ResizeFilter.Mitchell)]
    public void ResizeByteDownsampleAndUpsampleMatchesCpuAndNativeOracle(
        int inW, int inH, int outW, int outH,
        ResizePixelLayout layout, ResizeDataType dataType, ResizeFilter filter)
    {
        int ch = ChannelsFor(layout);
        byte[] src = GenerateImageBytes(inW, inH, ch, seed: inW * 13 + outW * 7);
        byte[] cpu = new byte[outW * outH * ch];
        byte[] gpu = new byte[outW * outH * ch];
        byte[] native = new byte[outW * outH * ch];

        var opts = new ImageResizeOptions
        {
            HorizontalFilter = filter,
            VerticalFilter = filter,
            HorizontalEdge = ResizeEdge.Clamp,
            VerticalEdge = ResizeEdge.Clamp,
        };

        StbImageResize.Resize(src, inW, inH, cpu, outW, outH, layout, dataType, 0, 0, opts);

        using StbGpuResizePlan plan = StbGpuResizePlan.Create(fixture.Context, inW, inH, outW, outH, layout, dataType, filter, ResizeEdge.Clamp);
        plan.Resize(src, gpu, StbGpuExecutionMode.ForceGpu);

        bool ok = StbOracle.ResizeBytes(src, inW, inH, 0, native, outW, outH, 0, (int)layout, (int)dataType, (int)ResizeEdge.Clamp, (int)filter);
        Assert.True(ok);

        // 8-bit resize uses exact sRGB LUT + Fabian Giesen encoder; allow <= 1 LSB for FP32 accumulation rounding.
        AssertBytesClose(cpu, gpu, maxAllowedDiff: 1);
        AssertBytesClose(native, gpu, maxAllowedDiff: 1);
    }

    [Theory]
    [InlineData(ResizeEdge.Clamp)]
    [InlineData(ResizeEdge.Reflect)]
    [InlineData(ResizeEdge.Wrap)]
    [InlineData(ResizeEdge.Zero)]
    public void ResizeAllEdgeModesMatchCpu(ResizeEdge edge)
    {
        const int inW = 64, inH = 48, outW = 96, outH = 72, ch = 4;
        byte[] src = GenerateImageBytes(inW, inH, ch, seed: 300 + (int)edge);
        byte[] cpu = new byte[outW * outH * ch];
        byte[] gpu = new byte[outW * outH * ch];

        var opts = new ImageResizeOptions
        {
            HorizontalFilter = ResizeFilter.CatmullRom,
            VerticalFilter = ResizeFilter.CatmullRom,
            HorizontalEdge = edge,
            VerticalEdge = edge,
        };

        StbImageResize.Resize(src, inW, inH, cpu, outW, outH, ResizePixelLayout.Rgba, ResizeDataType.UInt8Srgb, 0, 0, opts);
        using StbGpuResizePlan plan = StbGpuResizePlan.Create(fixture.Context, inW, inH, outW, outH, ResizePixelLayout.Rgba, ResizeDataType.UInt8Srgb, ResizeFilter.CatmullRom, edge);
        plan.Resize(src, gpu, StbGpuExecutionMode.ForceGpu);

        AssertBytesClose(cpu, gpu, maxAllowedDiff: 1);
    }

    [Theory]
    [InlineData(ResizePixelLayout.Bgr)]
    [InlineData(ResizePixelLayout.Bgra)]
    public void ResizeBgrAndBgraMatchCpu(ResizePixelLayout layout)
    {
        int ch = ChannelsFor(layout);
        const int inW = 80, inH = 60, outW = 40, outH = 30;
        byte[] src = GenerateImageBytes(inW, inH, ch, seed: 444 + ch);
        byte[] cpu = new byte[outW * outH * ch];
        byte[] gpu = new byte[outW * outH * ch];

        StbImageResize.Resize(src, inW, inH, cpu, outW, outH, layout, ResizeDataType.UInt8Srgb);
        using StbGpuResizePlan plan = StbGpuResizePlan.Create(fixture.Context, inW, inH, outW, outH, layout, ResizeDataType.UInt8Srgb);
        plan.Resize(src, gpu, StbGpuExecutionMode.ForceGpu);

        AssertBytesClose(cpu, gpu, maxAllowedDiff: 1);
    }

    [Theory]
    [InlineData(ResizePixelLayout.OneChannel, 1)]
    [InlineData(ResizePixelLayout.Rgb, 3)]
    [InlineData(ResizePixelLayout.Rgba, 4)]
    public void ResizeFloatLinearMatchesCpuAndNativeOracle(ResizePixelLayout layout, int ch)
    {
        const int inW = 96, inH = 64, outW = 48, outH = 40;
        float[] src = new float[inW * inH * ch];
        for (int i = 0; i < src.Length; ++i)
        {
            src[i] = ((i * 37) % 1000) / 1000.0f;
            if (ch == 4 && (i & 3) == 3)
            {
                src[i] = 0.25f + 0.75f * src[i];
            }
        }

        float[] cpu = new float[outW * outH * ch];
        float[] gpu = new float[outW * outH * ch];
        float[] native = new float[outW * outH * ch];

        StbImageResize.ResizeFloatLinear(src, inW, inH, cpu, outW, outH, layout);
        using StbGpuResizePlan plan = StbGpuResizePlan.Create(fixture.Context, inW, inH, outW, outH, layout, ResizeDataType.Fp32);
        plan.ResizeFloat(src, gpu, StbGpuExecutionMode.ForceGpu);

        bool ok = StbOracle.ResizeBytes(
            MemoryMarshal.AsBytes(src.AsSpan()), inW, inH, 0,
            MemoryMarshal.AsBytes(native.AsSpan()), outW, outH, 0,
            (int)layout, (int)ResizeDataType.Fp32);
        Assert.True(ok);

        float maxDiffCpu = 0.0f;
        float maxDiffNative = 0.0f;
        for (int i = 0; i < cpu.Length; ++i)
        {
            maxDiffCpu = MathF.Max(maxDiffCpu, MathF.Abs(cpu[i] - gpu[i]));
            maxDiffNative = MathF.Max(maxDiffNative, MathF.Abs(native[i] - gpu[i]));
        }

        Assert.True(maxDiffCpu <= 1e-5f, $"Float resize GPU vs CPU delta {maxDiffCpu} > 1e-5.");
        Assert.True(maxDiffNative <= 1e-5f, $"Float resize GPU vs Native delta {maxDiffNative} > 1e-5.");
    }

    [Fact]
    public void ResizeBatchMatchesIndividualCpuCalls()
    {
        const int inW = 64, inH = 64, outW = 32, outH = 32, batch = 6;
        var inputs = new ReadOnlyMemory<byte>[batch];
        for (int i = 0; i < batch; ++i)
        {
            inputs[i] = GenerateImageBytes(inW, inH, 4, seed: 800 + i * 29);
        }

        using StbGpuResizePlan plan = StbGpuResizePlan.Create(fixture.Context, inW, inH, outW, outH, ResizePixelLayout.Rgba, ResizeDataType.UInt8Srgb, ResizeFilter.Mitchell);
        byte[][] gpuBatch = plan.ResizeBatch(inputs, StbGpuExecutionMode.ForceGpu);
        Assert.Equal(batch, gpuBatch.Length);

        var opts = new ImageResizeOptions { HorizontalFilter = ResizeFilter.Mitchell, VerticalFilter = ResizeFilter.Mitchell };
        for (int i = 0; i < batch; ++i)
        {
            byte[] cpu = new byte[outW * outH * 4];
            StbImageResize.Resize(inputs[i].Span, inW, inH, cpu, outW, outH, ResizePixelLayout.Rgba, ResizeDataType.UInt8Srgb, 0, 0, opts);
            AssertBytesClose(cpu, gpuBatch[i], maxAllowedDiff: 1);
        }
    }

    [Fact]
    public void GenerateMipmapsAndDxtMipmapsProduceValidFullPyramid()
    {
        const int w = 128, h = 96;
        byte[] pixels = GenerateImageBytes(w, h, 4, seed: 1234);
        var baseImg = new ImageBuffer<byte>(w, h, ColorComponents.RedGreenBlueAlpha, ColorComponents.RedGreenBlueAlpha, pixels);

        ImageBuffer<byte>[] gpuMips = StbGpuImageResize.GenerateMipmaps(fixture.Context, baseImg, ResizeDataType.UInt8Srgb, ResizeFilter.Mitchell, modeOverride: StbGpuExecutionMode.ForceGpu);
        ImageBuffer<byte>[] cpuMips = StbGpuImageResize.GenerateMipmaps(fixture.Context, baseImg, ResizeDataType.UInt8Srgb, ResizeFilter.Mitchell, modeOverride: StbGpuExecutionMode.ForceCpu);

        // 128x96 -> 64x48 -> 32x24 -> 16x12 -> 8x6 -> 4x3 -> 2x1 -> 1x1 = 8 levels.
        Assert.Equal(8, gpuMips.Length);
        Assert.Equal(cpuMips.Length, gpuMips.Length);
        Assert.Equal(1, gpuMips[^1].Width);
        Assert.Equal(1, gpuMips[^1].Height);

        for (int lvl = 0; lvl < gpuMips.Length; ++lvl)
        {
            Assert.Equal(cpuMips[lvl].Width, gpuMips[lvl].Width);
            Assert.Equal(cpuMips[lvl].Height, gpuMips[lvl].Height);
            // Compounding 7 levels of FP rounding can drift by at most 2 LSB on deepest levels.
            AssertBytesClose(cpuMips[lvl].AsReadOnlySpan().ToArray(), gpuMips[lvl].AsReadOnlySpan().ToArray(), maxAllowedDiff: 2);
        }

        // Device-resident mipmap + DXT compression pyramid.
        byte[][] dxtMips = StbGpuImageResize.GenerateDxtMipmaps(fixture.Context, baseImg, DxtFormat.Bc3, DxtCompressionMode.Normal, modeOverride: StbGpuExecutionMode.ForceGpu);
        Assert.Equal(gpuMips.Length, dxtMips.Length);
        for (int lvl = 0; lvl < dxtMips.Length; ++lvl)
        {
            long expectedSize = StbDxt.GetCompressedSize(gpuMips[lvl].Width, gpuMips[lvl].Height, DxtFormat.Bc3);
            Assert.Equal(expectedSize, dxtMips[lvl].Length);
        }

        // High-level ImageBuffer<byte> and ImageBuffer<float> one-shot GPU resize overloads.
        ImageBuffer<byte> halfByteGpu = StbGpuImageResize.Resize(fixture.Context, baseImg, 64, 48, ResizeDataType.UInt8Srgb, ResizeFilter.Mitchell, ResizeEdge.Clamp, StbGpuExecutionMode.ForceGpu);
        ImageBuffer<byte> halfByteCpu = StbGpuImageResize.Resize(fixture.Context, baseImg, 64, 48, ResizeDataType.UInt8Srgb, ResizeFilter.Mitchell, ResizeEdge.Clamp, StbGpuExecutionMode.ForceCpu);
        AssertBytesClose(halfByteCpu.AsReadOnlySpan().ToArray(), halfByteGpu.AsReadOnlySpan().ToArray(), maxAllowedDiff: 1);

        float[] fPixels = new float[64 * 48 * 4];
        for (int i = 0; i < fPixels.Length; ++i)
        {
            fPixels[i] = (i % 101) * 0.01f;
        }

        var fImg = new ImageBuffer<float>(64, 48, ColorComponents.RedGreenBlueAlpha, ColorComponents.RedGreenBlueAlpha, fPixels);
        ImageBuffer<float> halfFloatGpu = StbGpuImageResize.Resize(fixture.Context, fImg, 32, 24, ResizeFilter.Triangle, ResizeEdge.Clamp, StbGpuExecutionMode.ForceGpu);
        ImageBuffer<float> halfFloatCpu = StbGpuImageResize.Resize(fixture.Context, fImg, 32, 24, ResizeFilter.Triangle, ResizeEdge.Clamp, StbGpuExecutionMode.ForceCpu);
        Assert.Equal(halfFloatCpu.SampleCount, halfFloatGpu.SampleCount);
        for (int i = 0; i < halfFloatCpu.SampleCount; ++i)
        {
            Assert.True(MathF.Abs(halfFloatCpu.AsReadOnlySpan()[i] - halfFloatGpu.AsReadOnlySpan()[i]) <= 1e-5f);
        }
    }

    private static int ChannelsFor(ResizePixelLayout layout) => layout switch
    {
        ResizePixelLayout.OneChannel => 1,
        ResizePixelLayout.TwoChannel or ResizePixelLayout.Ra or ResizePixelLayout.Ar or ResizePixelLayout.RaPremultiplied or ResizePixelLayout.ArPremultiplied => 2,
        ResizePixelLayout.Rgb or ResizePixelLayout.Bgr => 3,
        _ => 4,
    };

    private static byte[] GenerateImageBytes(int w, int h, int ch, int seed)
    {
        byte[] data = new byte[w * h * ch];
        uint s = (uint)seed;
        for (int y = 0; y < h; ++y)
        {
            for (int x = 0; x < w; ++x)
            {
                int o = (y * w + x) * ch;
                for (int c = 0; c < ch; ++c)
                {
                    s = s * 1664525u + 1013904223u;
                    byte v = (byte)(((x * 3 + y * 5 + c * 47) ^ (s >> 26)) & 255);
                    if (ch == 4 && c == 3)
                    {
                        v = (byte)(64 + (v >> 1)); // keep non-trivial alpha
                    }

                    data[o + c] = v;
                }
            }
        }

        return data;
    }

    private static void AssertBytesClose(byte[] expected, byte[] actual, int maxAllowedDiff)
    {
        Assert.Equal(expected.Length, actual.Length);
        int maxDiff = 0;
        int worstIdx = -1;
        for (int i = 0; i < expected.Length; ++i)
        {
            int diff = Math.Abs(expected[i] - actual[i]);
            if (diff > maxDiff)
            {
                maxDiff = diff;
                worstIdx = i;
            }
        }

        Assert.True(
            maxDiff <= maxAllowedDiff,
            $"Max byte diff {maxDiff} exceeded {maxAllowedDiff} at index {worstIdx} (expected {expected[Math.Max(0, worstIdx)]}, actual {actual[Math.Max(0, worstIdx)]}).");
    }
}
