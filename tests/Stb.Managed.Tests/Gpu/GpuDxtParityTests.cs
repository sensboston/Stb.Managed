using Stb.Managed.Dxt;
using Stb.Managed.Gpu;
using Stb.Managed.Oracle;
using Xunit;

namespace Stb.Managed.Tests.Gpu;

[Collection(GpuTestSuite.Name)]
public sealed class GpuDxtParityTests(GpuTestFixture fixture)
{
    [Theory]
    [InlineData(64, 64, DxtFormat.Bc1, DxtCompressionMode.Normal)]
    [InlineData(64, 64, DxtFormat.Bc1, DxtCompressionMode.HighQuality)]
    [InlineData(131, 97, DxtFormat.Bc1, DxtCompressionMode.Normal)]
    [InlineData(131, 97, DxtFormat.Bc1, DxtCompressionMode.HighQuality)]
    [InlineData(64, 64, DxtFormat.Bc3, DxtCompressionMode.Normal)]
    [InlineData(64, 64, DxtFormat.Bc3, DxtCompressionMode.HighQuality)]
    [InlineData(131, 97, DxtFormat.Bc3, DxtCompressionMode.Normal)]
    [InlineData(131, 97, DxtFormat.Bc3, DxtCompressionMode.HighQuality)]
    public void CompressBc1AndBc3MatchCpuAndNativeOracle(int width, int height, DxtFormat format, DxtCompressionMode mode)
    {
        byte[] src = GeneratePatternRgba(width, height, seed: width * 31 + height);
        byte[] cpu = StbDxt.CompressImageToArray(src, width, height, format, 0, mode);
        byte[] gpu = StbGpuDxt.CompressImageToArray(fixture.Context, src, width, height, format, 0, mode, StbGpuExecutionMode.ForceGpu);

        Assert.Equal(cpu.Length, gpu.Length);

        // Compare block-by-block against CPU: constant blocks, alpha blocks, and non-borderline blocks must be bit-exact;
        // fp32 covariance scaling (OpenCL fp64-safe) may rarely flip an ambiguous tie-breaking direction by 1 bit.
        int blockSize = format == DxtFormat.Bc3 ? 16 : 8;
        int totalBlocks = cpu.Length / blockSize;
        int exactBlocks = 0;
        for (int b = 0; b < totalBlocks; ++b)
        {
            int off = b * blockSize;
            // BC3 alpha half (first 8 bytes) is integer-only, so it must be bit-exact.
            if (format == DxtFormat.Bc3)
            {
                for (int k = 0; k < 8; ++k)
                {
                    Assert.Equal(cpu[off + k], gpu[off + k]);
                }
            }

            if (cpu.AsSpan(off, blockSize).SequenceEqual(gpu.AsSpan(off, blockSize)))
            {
                exactBlocks++;
            }
        }

        double exactRatio = (double)exactBlocks / totalBlocks;
        Assert.True(exactRatio >= 0.99, $"Expected >= 99% bit-exact blocks for {format}/{mode} {width}x{height}, got {exactBlocks}/{totalBlocks} ({exactRatio:P2}).");

        // Also compare with native C oracle when dimensions are multiples of 4.
        if ((width & 3) == 0 && (height & 3) == 0)
        {
            byte[] native = new byte[cpu.Length];
            StbOracle.CompressDxtImage(src, width, height, native, alpha: format == DxtFormat.Bc3, mode: (int)mode);
            Assert.Equal(native, cpu);
        }
    }

    [Theory]
    [InlineData(64, 64)]
    [InlineData(131, 97)]
    [InlineData(128, 128)]
    public void CompressBc4AndBc5AreBitExactWithCpuAndNativeOracle(int width, int height)
    {
        // BC4 (1-channel) is integer-only: bit-exact across CPU, GPU, and C oracle.
        byte[] srcR = GeneratePatternBytes(width * height, seed: 101 + width);
        byte[] cpuBc4 = StbDxt.CompressImageToArray(srcR, width, height, DxtFormat.Bc4);
        byte[] gpuBc4 = StbGpuDxt.CompressImageToArray(fixture.Context, srcR, width, height, DxtFormat.Bc4, modeOverride: StbGpuExecutionMode.ForceGpu);
        Assert.Equal(cpuBc4, gpuBc4);

        if ((width & 3) == 0 && (height & 3) == 0)
        {
            byte[] nativeBc4 = new byte[cpuBc4.Length];
            StbOracle.CompressBc4Image(srcR, width, height, nativeBc4);
            Assert.Equal(nativeBc4, gpuBc4);
        }

        // BC5 (2-channel) is integer-only: bit-exact across CPU, GPU, and C oracle.
        byte[] srcRg = GeneratePatternBytes(width * height * 2, seed: 202 + height);
        byte[] cpuBc5 = StbDxt.CompressImageToArray(srcRg, width, height, DxtFormat.Bc5);
        byte[] gpuBc5 = StbGpuDxt.CompressImageToArray(fixture.Context, srcRg, width, height, DxtFormat.Bc5, modeOverride: StbGpuExecutionMode.ForceGpu);
        Assert.Equal(cpuBc5, gpuBc5);

        if ((width & 3) == 0 && (height & 3) == 0)
        {
            byte[] nativeBc5 = new byte[cpuBc5.Length];
            StbOracle.CompressBc5Image(srcRg, width, height, nativeBc5);
            Assert.Equal(nativeBc5, gpuBc5);
        }
    }

    [Fact]
    public void CompressNonZeroStrideAndConstantBlocksAreBitExact()
    {
        const int width = 36;
        const int height = 28;
        const int stride = width * 4 + 24;
        byte[] padded = new byte[stride * height];
        // Fill alternating solid constant blocks & gradients to cover IsConstantBlock fast path.
        for (int y = 0; y < height; ++y)
        {
            int row = y * stride;
            for (int x = 0; x < width; ++x)
            {
                int bx = x >> 2;
                int by = y >> 2;
                bool constantBlock = ((bx + by) & 1) == 0;
                int o = row + x * 4;
                if (constantBlock)
                {
                    padded[o] = (byte)(bx * 23);
                    padded[o + 1] = (byte)(by * 37);
                    padded[o + 2] = 128;
                    padded[o + 3] = 255;
                }
                else
                {
                    padded[o] = (byte)((x * 7) & 255);
                    padded[o + 1] = (byte)((y * 9) & 255);
                    padded[o + 2] = (byte)(((x + y) * 5) & 255);
                    padded[o + 3] = (byte)((x * 3 + y * 5) & 255);
                }
            }
        }

        byte[] cpu = StbDxt.CompressImageToArray(padded, width, height, DxtFormat.Bc3, stride, DxtCompressionMode.HighQuality);
        byte[] gpu = StbGpuDxt.CompressImageToArray(fixture.Context, padded, width, height, DxtFormat.Bc3, stride, DxtCompressionMode.HighQuality, StbGpuExecutionMode.ForceGpu);
        Assert.Equal(cpu, gpu);
    }

    [Fact]
    public void CompressBatchAndGpuImageZeroCopyMatchSingleCalls()
    {
        const int width = 64;
        const int height = 64;
        const int batchCount = 4;

        var inputs = new ReadOnlyMemory<byte>[batchCount];
        for (int i = 0; i < batchCount; ++i)
        {
            inputs[i] = GeneratePatternBytes(width * height, seed: 500 + i * 17);
        }

        byte[][] batchGpu = StbGpuDxt.CompressBatch(fixture.Context, inputs, width, height, DxtFormat.Bc4, modeOverride: StbGpuExecutionMode.ForceGpu);
        Assert.Equal(batchCount, batchGpu.Length);

        for (int i = 0; i < batchCount; ++i)
        {
            byte[] cpu = StbDxt.CompressImageToArray(inputs[i], width, height, DxtFormat.Bc4);
            Assert.Equal(cpu, batchGpu[i]);
        }

        // VRAM-resident CompressGpuImage path (zero re-upload).
        byte[] rgba = GeneratePatternRgba(width, height, seed: 777);
        using StbGpuImage<byte> vramImg = fixture.Context.Upload(rgba, width, height, ColorComponents.RedGreenBlueAlpha);
        byte[] fromVram = StbGpuDxt.CompressGpuImage(fixture.Context, vramImg, DxtFormat.Bc3, DxtCompressionMode.HighQuality);
        byte[] fromHost = StbGpuDxt.CompressImageToArray(fixture.Context, rgba, width, height, DxtFormat.Bc3, 0, DxtCompressionMode.HighQuality, StbGpuExecutionMode.ForceGpu);
        Assert.Equal(fromHost, fromVram);
    }

    [Fact]
    public void CompressForceCpuFallbackMatchesStbDxtBitExact()
    {
        byte[] src = GeneratePatternRgba(48, 32, seed: 999);
        byte[] expected = StbDxt.CompressImageToArray(src, 48, 32, DxtFormat.Bc3, 0, DxtCompressionMode.HighQuality);
        byte[] actual = StbGpuDxt.CompressImageToArray(fixture.Context, src, 48, 32, DxtFormat.Bc3, 0, DxtCompressionMode.HighQuality, StbGpuExecutionMode.ForceCpu);
        Assert.Equal(expected, actual);
    }

    private static byte[] GeneratePatternRgba(int width, int height, int seed)
    {
        byte[] data = new byte[width * height * 4];
        uint s = (uint)seed;
        for (int y = 0; y < height; ++y)
        {
            for (int x = 0; x < width; ++x)
            {
                s = s * 1664525u + 1013904223u;
                int o = (y * width + x) * 4;
                data[o] = (byte)(((x * 255) / Math.Max(1, width - 1)) ^ ((s >> 24) & 31));
                data[o + 1] = (byte)(((y * 255) / Math.Max(1, height - 1)) ^ ((s >> 16) & 31));
                data[o + 2] = (byte)((((x ^ y) * 3) + (s >> 8)) & 255);
                data[o + 3] = (byte)((x * 5 + y * 3 + (s & 63)) & 255);
            }
        }

        return data;
    }

    private static byte[] GeneratePatternBytes(int length, int seed)
    {
        byte[] data = new byte[length];
        uint s = (uint)seed;
        for (int i = 0; i < length; ++i)
        {
            s = s * 1664525u + 1013904223u;
            data[i] = (byte)(s >> 24);
        }

        return data;
    }
}
