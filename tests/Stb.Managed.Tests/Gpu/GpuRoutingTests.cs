#if !TEST_NETSTANDARD
using Stb.Managed.Dxt;
using Stb.Managed.Gpu;
using Xunit;

namespace Stb.Managed.Tests.Gpu;

// StbGpu.Mode is process-wide: these tests must not overlap with any other test.
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class GlobalGpuModeTests
{
    public const string Name = "GlobalGpuMode";
}

[Collection(GlobalGpuModeTests.Name)]
public sealed class GpuRoutingTests
{
    [Fact]
    public void ForceGpuRoutesOrdinaryCallsAndStaysCloseToCpu()
    {
        const int w = 256, h = 192, ow = 512, oh = 384;
        byte[] src = new byte[w * h * 4];
        new Random(7).NextBytes(src);
        byte[] cpu = new byte[ow * oh * 4];
        byte[] gpu = new byte[cpu.Length];
        byte[] dxtCpu = new byte[(w / 4) * (h / 4) * 16];
        byte[] dxtGpu = new byte[dxtCpu.Length];

        StbGpuMode saved = StbGpu.Mode;
        try
        {
            StbGpu.Mode = StbGpuMode.CpuOnly;
            StbImageResize.Resize(src, w, h, cpu, ow, oh, ResizePixelLayout.Rgba, ResizeDataType.UInt8Srgb, ResizeEdge.Clamp);
            StbDxt.CompressImage(src, w, h, 0, dxtCpu, DxtFormat.Bc3);

            StbGpu.Mode = StbGpuMode.ForceGpu;
            Assert.True(StbGpu.Warmup());
            StbImageResize.Resize(src, w, h, gpu, ow, oh, ResizePixelLayout.Rgba, ResizeDataType.UInt8Srgb, ResizeEdge.Clamp);
            StbDxt.CompressImage(src, w, h, 0, dxtGpu, DxtFormat.Bc3);
        }
        finally
        {
            StbGpu.Mode = saved;
        }

        int maxDiff = 0, big = 0, bigLowAlpha = 0;
        for (int i = 0; i < cpu.Length; i++)
        {
            int d = Math.Abs(cpu[i] - gpu[i]);
            maxDiff = Math.Max(maxDiff, d);
            if (d > 1)
            {
                big++;
                if (cpu[(i & ~3) + 3] < 8) bigLowAlpha++;
            }
        }

        // Fully transparent output pixels: the CPU keeps stb's unweighted colour, the GPU writes zero.
        Assert.True(big == bigLowAlpha, $"{big - bigLowAlpha} visible bytes differ by more than 1 (max {maxDiff})");
        int sameBlocks = 0;
        for (int b = 0; b < dxtCpu.Length; b += 16)
        {
            sameBlocks += dxtCpu.AsSpan(b, 16).SequenceEqual(dxtGpu.AsSpan(b, 16)) ? 1 : 0;
        }

        Assert.True(sameBlocks * 16 >= dxtCpu.Length * 0.99, $"{sameBlocks} of {dxtCpu.Length / 16} DXT blocks equal");
    }

    [Fact]
    public void CpuOnlyNeverInitializesTheGpuForOrdinaryCalls()
    {
        StbGpuMode saved = StbGpu.Mode;
        try
        {
            StbGpu.Mode = StbGpuMode.CpuOnly;
            Assert.False(StbGpu.Warmup());
        }
        finally
        {
            StbGpu.Mode = saved;
        }
    }
}
#endif
