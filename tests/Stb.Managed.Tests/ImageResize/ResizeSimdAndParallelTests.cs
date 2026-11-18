using System;
using Xunit;

namespace Stb.Managed.Tests.ImageResize;

public sealed class ResizeSimdAndParallelTests
{
    [Theory]
    [InlineData(ResizePixelLayout.OneChannel, ResizeDataType.UInt8Linear, 97, 83, 61, 53)]
    [InlineData(ResizePixelLayout.OneChannel, ResizeDataType.UInt8Linear, 53, 47, 113, 97)]
    [InlineData(ResizePixelLayout.TwoChannel, ResizeDataType.UInt8Linear, 101, 79, 64, 48)]
    [InlineData(ResizePixelLayout.Ra, ResizeDataType.UInt8Srgb, 89, 73, 55, 43)]
    [InlineData(ResizePixelLayout.Rgb, ResizeDataType.UInt8Srgb, 117, 91, 63, 49)]
    [InlineData(ResizePixelLayout.Rgb, ResizeDataType.UInt8Linear, 63, 49, 131, 103)]
    [InlineData(ResizePixelLayout.FourChannel, ResizeDataType.UInt8Linear, 129, 97, 71, 59)]
    [InlineData(ResizePixelLayout.Rgba, ResizeDataType.UInt8Srgb, 137, 101, 73, 57)]
    [InlineData(ResizePixelLayout.Rgba, ResizeDataType.UInt8Srgb, 57, 43, 139, 107)]
    [InlineData(ResizePixelLayout.Bgra, ResizeDataType.UInt8Linear, 95, 77, 51, 41)]
    [InlineData(ResizePixelLayout.Argb, ResizeDataType.UInt8Linear, 95, 77, 51, 41)]
    [InlineData(ResizePixelLayout.Abgr, ResizeDataType.UInt8Linear, 95, 77, 51, 41)]
    [InlineData(ResizePixelLayout.Rgba, ResizeDataType.UInt16Linear, 93, 71, 49, 39)]
    [InlineData(ResizePixelLayout.Rgba, ResizeDataType.Fp32, 79, 61, 45, 37)]
    [InlineData(ResizePixelLayout.Rgba, ResizeDataType.Fp16, 79, 61, 45, 37)]
    public void SimdMatchesScalarReferenceAcrossAllEffectiveChannelCounts(
        ResizePixelLayout layout,
        ResizeDataType dataType,
        int inW,
        int inH,
        int outW,
        int outH)
    {
        byte[] input = ResizeTestHelpers.CreateImageBytes(inW, inH, layout, dataType, seed: 4242, includeZeroAlpha: true);
        int outBytes = outW * outH * ResizeTestHelpers.GetChannelCount(layout) * ResizeTestHelpers.GetTypeSize(dataType);

        StbImageResizer resizer = StbImageResizer.Create(inW, inH, outW, outH, layout, dataType);

        byte[] simdOut = new byte[outBytes];
        byte[] scalarOut = new byte[outBytes];
        resizer.Resize(input, simdOut);
        resizer.ExecuteScalarForTest(input, scalarOut);

        ResizeTestHelpers.AssertMatchesOracle(
            simdOut,
            scalarOut,
            dataType,
            context: $"SIMD vs Scalar {layout}/{dataType} {inW}x{inH}->{outW}x{outH}");
    }

    [Theory]
    [InlineData(ResizeFilter.Default, ResizeEdge.Clamp, false)]
    [InlineData(ResizeFilter.Mitchell, ResizeEdge.Reflect, false)]
    [InlineData(ResizeFilter.CatmullRom, ResizeEdge.Wrap, false)]
    [InlineData(ResizeFilter.Lanczos4, ResizeEdge.Zero, false)]
    [InlineData(ResizeFilter.Mitchell, ResizeEdge.Clamp, true)]
    public void L2ColumnSlicingEngagesAndMatchesScalarReference(
        ResizeFilter filter,
        ResizeEdge edge,
        bool fastAlpha)
    {
        // Wide RGBA frame with large vertical filter footprint exceeds the 576 KB L2 ring threshold
        // even for FastAlpha (4ch) and engages multi-slice column tiling.
        const int inW = 6000;
        const int inH = 32;
        const int outW = 4500;
        const int outH = 12;

        ImageResizeOptions options = new()
        {
            HorizontalFilter = filter,
            VerticalFilter = filter,
            HorizontalEdge = edge,
            VerticalEdge = edge,
            FastAlpha = fastAlpha,
        };

        StbImageResizer resizer = StbImageResizer.Create(
            inW, inH, outW, outH, ResizePixelLayout.Rgba, ResizeDataType.UInt8Srgb, options);
        Assert.True(resizer.ColumnSliceCount > 1, $"Expected multiple L2 column slices, got {resizer.ColumnSliceCount}.");

        byte[] input = ResizeTestHelpers.CreateImageBytes(inW, inH, ResizePixelLayout.Rgba, ResizeDataType.UInt8Srgb, seed: 777, includeZeroAlpha: true);
        byte[] simdOut = new byte[outW * outH * 4];
        byte[] scalarOut = new byte[outW * outH * 4];

        resizer.Resize(input, simdOut);
        resizer.ExecuteScalarForTest(input, scalarOut);

        ResizeTestHelpers.AssertMatchesOracle(
            simdOut,
            scalarOut,
            ResizeDataType.UInt8Srgb,
            context: $"L2 column slicing {filter}/{edge}/fastAlpha={fastAlpha} slices={resizer.ColumnSliceCount}");
    }

    [Theory]
    [InlineData(256, 64, 128, 32, ResizePixelLayout.Rgba, ResizeDataType.UInt8Srgb, false)] // 2x Period-1 (8 taps, step 2) 7ch
    [InlineData(288, 72, 96, 24, ResizePixelLayout.Rgba, ResizeDataType.UInt8Srgb, false)] // 3x Period-1 (12 taps, step 3) 7ch
    [InlineData(256, 64, 64, 16, ResizePixelLayout.Rgba, ResizeDataType.UInt8Linear, false)] // 4x Period-1 (16 taps, step 4) 7ch
    [InlineData(256, 64, 128, 32, ResizePixelLayout.Rgba, ResizeDataType.Fp32, false)] // 2x Period-1 Fp32 7ch
    [InlineData(128, 32, 256, 64, ResizePixelLayout.Rgba, ResizeDataType.UInt8Srgb, false)] // 2x Upscale (4 taps) 7ch
    [InlineData(256, 64, 128, 32, ResizePixelLayout.Rgba, ResizeDataType.UInt8Srgb, true)] // 2x Period-1 4ch
    [InlineData(288, 72, 96, 24, ResizePixelLayout.FourChannel, ResizeDataType.UInt8Linear, false)] // 3x Period-1 4ch
    [InlineData(256, 64, 64, 16, ResizePixelLayout.Rgba, ResizeDataType.Fp32, true)] // 4x Period-1 Fp32 4ch
    public void Period1AndSpecializedGatherPathsMatchNativeAndScalar(
        int inW,
        int inH,
        int outW,
        int outH,
        ResizePixelLayout layout,
        ResizeDataType dataType,
        bool fastAlpha)
    {
        byte[] input = ResizeTestHelpers.CreateImageBytes(inW, inH, layout, dataType, seed: 8080, includeZeroAlpha: true);
        int outBytes = outW * outH * ResizeTestHelpers.GetChannelCount(layout) * ResizeTestHelpers.GetTypeSize(dataType);
        ImageResizeOptions options = new() { FastAlpha = fastAlpha };

        StbImageResizer resizer = StbImageResizer.Create(inW, inH, outW, outH, layout, dataType, options);

        byte[] simdOut = new byte[outBytes];
        byte[] scalarOut = new byte[outBytes];
        resizer.Resize(input, simdOut);
        resizer.ExecuteScalarForTest(input, scalarOut);

        ResizeTestHelpers.AssertMatchesOracle(
            simdOut,
            scalarOut,
            dataType,
            exact: dataType != ResizeDataType.Fp32,
            context: $"Period-1 SIMD vs Scalar {layout}/{dataType}/fast={fastAlpha} {inW}x{inH}->{outW}x{outH}");

        byte[] oracleOut = new byte[outBytes];
        bool ok = Stb.Managed.Oracle.StbOracle.ResizeBytesExtended(
            input, inW, inH, 0,
            oracleOut, outW, outH, 0,
            (int)layout, (int)layout,
            (int)dataType, (int)dataType,
            (int)ResizeEdge.Clamp, (int)ResizeEdge.Clamp,
            (int)ResizeFilter.Default, (int)ResizeFilter.Default,
            fastAlpha);
        Assert.True(ok);

        ResizeTestHelpers.AssertMatchesOracle(
            simdOut,
            oracleOut,
            dataType,
            context: $"Period-1 SIMD vs Native {layout}/{dataType}/fast={fastAlpha} {inW}x{inH}->{outW}x{outH}");
    }

    [Theory]
    [InlineData(ResizeDataType.UInt8Srgb, 17, 9)]
    [InlineData(ResizeDataType.UInt8Srgb, 63, 31)]
    [InlineData(ResizeDataType.UInt8Linear, 19, 11)]
    [InlineData(ResizeDataType.UInt8Linear, 65, 33)]
    [InlineData(ResizeDataType.Fp32, 17, 9)]
    [InlineData(ResizeDataType.Fp32, 63, 31)]
    public void FusedFancyUnweightAndEncodeMatchesNativeOnEdgeAlphaAndUnalignedWidths(
        ResizeDataType dataType,
        int inW,
        int outW)
    {
        const int inH = 11;
        const int outH = 7;
        int typeSize = ResizeTestHelpers.GetTypeSize(dataType);
        byte[] input = new byte[inW * inH * 4 * typeSize];

        if (dataType == ResizeDataType.Fp32)
        {
            Span<float> floats = System.Runtime.InteropServices.MemoryMarshal.Cast<byte, float>(input.AsSpan());
            ReadOnlySpan<float> alphas = [0.0f, 1e-6f, 1e-4f, 0.001f, 0.25f, 0.75f, 1.0f];
            for (int i = 0; i < inW * inH; i++)
            {
                floats[i * 4] = ((i * 13) % 101) / 100.0f;
                floats[(i * 4) + 1] = ((i * 29) % 101) / 100.0f;
                floats[(i * 4) + 2] = ((i * 47) % 101) / 100.0f;
                floats[(i * 4) + 3] = alphas[i % alphas.Length];
            }
        }
        else
        {
            ReadOnlySpan<byte> alphas = [0, 0, 1, 2, 17, 128, 254, 255];
            for (int i = 0; i < inW * inH; i++)
            {
                input[i * 4] = (byte)((i * 37) & 0xFF);
                input[(i * 4) + 1] = (byte)((i * 73) & 0xFF);
                input[(i * 4) + 2] = (byte)((i * 113) & 0xFF);
                input[(i * 4) + 3] = alphas[i % alphas.Length];
            }
        }

        byte[] managedOut = new byte[outW * outH * 4 * typeSize];
        byte[] oracleOut = new byte[outW * outH * 4 * typeSize];

        StbImageResize.Resize(input, inW, inH, managedOut, outW, outH, ResizePixelLayout.Rgba, dataType);
        bool ok = Stb.Managed.Oracle.StbOracle.ResizeBytes(
            input, inW, inH, 0,
            oracleOut, outW, outH, 0,
            (int)ResizePixelLayout.Rgba, (int)dataType);
        Assert.True(ok);

        ResizeTestHelpers.AssertMatchesOracle(
            managedOut,
            oracleOut,
            dataType,
            context: $"Fused fancy unweight+encode {dataType} {inW}x{inH}->{outW}x{outH}");
    }

    [Theory]
    [InlineData(256, 256, 128, 128, ResizePixelLayout.Rgba, ResizeDataType.UInt8Srgb, ResizeFilter.Default)]
    [InlineData(128, 128, 320, 240, ResizePixelLayout.Rgb, ResizeDataType.UInt8Linear, ResizeFilter.CatmullRom)]
    [InlineData(320, 320, 17, 19, ResizePixelLayout.Rgba, ResizeDataType.UInt8Srgb, ResizeFilter.Mitchell)] // Vertical scatter path
    [InlineData(320, 320, 19, 17, ResizePixelLayout.FourChannel, ResizeDataType.UInt16Linear, ResizeFilter.Lanczos4)] // Vertical scatter 16-bit
    [InlineData(256, 256, 128, 128, ResizePixelLayout.Rgba, ResizeDataType.Fp32, ResizeFilter.CubicBSpline)]
    [InlineData(2048, 48, 1536, 32, ResizePixelLayout.Rgba, ResizeDataType.UInt8Srgb, ResizeFilter.Mitchell)] // Multi-slice + multi-band
    public void ParallelExecutionIsBitExactWithSingleThreaded(
        int inW,
        int inH,
        int outW,
        int outH,
        ResizePixelLayout layout,
        ResizeDataType dataType,
        ResizeFilter filter)
    {
        byte[] input = ResizeTestHelpers.CreateImageBytes(inW, inH, layout, dataType, seed: 9191, includeZeroAlpha: true);
        int outBytes = outW * outH * ResizeTestHelpers.GetChannelCount(layout) * ResizeTestHelpers.GetTypeSize(dataType);

        ImageResizeOptions MakeOpts(int dop) => new()
        {
            HorizontalFilter = filter,
            VerticalFilter = filter,
            Options = new StbOptions { MaxDegreeOfParallelism = dop },
        };

        byte[] out1T = new byte[outBytes];
        byte[] out2TMem = new byte[outBytes];
        byte[] out4TMem = new byte[outBytes];
        byte[] out4TSpan = new byte[outBytes];
        byte[] outAutoMem = new byte[outBytes];

        StbImageResize.ResizeMemory(input, inW, inH, out1T, outW, outH, layout, dataType, options: MakeOpts(1));
        StbImageResize.ResizeMemory(input, inW, inH, out2TMem, outW, outH, layout, dataType, options: MakeOpts(2));
        StbImageResize.ResizeMemory(input, inW, inH, out4TMem, outW, outH, layout, dataType, options: MakeOpts(4));
        StbImageResize.Resize(input, inW, inH, out4TSpan, outW, outH, layout, dataType, options: MakeOpts(4));
        StbImageResize.ResizeMemory(input, inW, inH, outAutoMem, outW, outH, layout, dataType, options: MakeOpts(0));

        ResizeTestHelpers.AssertMatchesOracle(out2TMem, out1T, dataType, exact: true, context: $"2T Memory vs 1T ({inW}x{inH}->{outW}x{outH})");
        ResizeTestHelpers.AssertMatchesOracle(out4TMem, out1T, dataType, exact: true, context: $"4T Memory vs 1T ({inW}x{inH}->{outW}x{outH})");
        ResizeTestHelpers.AssertMatchesOracle(out4TSpan, out1T, dataType, exact: true, context: $"4T Span vs 1T ({inW}x{inH}->{outW}x{outH})");
        ResizeTestHelpers.AssertMatchesOracle(outAutoMem, out1T, dataType, exact: true, context: $"Auto Memory vs 1T ({inW}x{inH}->{outW}x{outH})");
    }

    [Fact]
    public void ParallelSubrectAndStridesPreserveUntouchedBytesAndMatchSingleThreaded()
    {
        const int inW = 192;
        const int inH = 160;
        const int outW = 160;
        const int outH = 144;
        const int channels = 4;

        int inStride = -((inW * channels) + 24);
        int outStride = -((outW * channels) + 32);
        int absInStride = -inStride;
        int absOutStride = -outStride;

        byte[] input = new byte[absInStride * inH];
        byte[] packed = ResizeTestHelpers.CreateImageBytes(inW, inH, ResizePixelLayout.Rgba, ResizeDataType.UInt8Srgb, seed: 3030, includeZeroAlpha: true);
        for (int y = 0; y < inH; y++)
        {
            packed.AsSpan(y * inW * channels, inW * channels).CopyTo(input.AsSpan(y * absInStride, inW * channels));
        }

        ResizeRect subrect = new(17, 13, 99, 87);
        ImageResizeOptions opts1T = new()
        {
            OutputSubrect = subrect,
            Options = new StbOptions { MaxDegreeOfParallelism = 1 },
        };
        ImageResizeOptions opts4T = new()
        {
            OutputSubrect = subrect,
            Options = new StbOptions { MaxDegreeOfParallelism = 4 },
        };

        byte[] out1T = new byte[absOutStride * outH];
        byte[] out4TSpan = new byte[absOutStride * outH];
        byte[] out4TMem = new byte[absOutStride * outH];
        out1T.AsSpan().Fill(0xA5);
        out4TSpan.AsSpan().Fill(0xA5);
        out4TMem.AsSpan().Fill(0xA5);

        StbImageResize.Resize(input, inW, inH, out1T, outW, outH, ResizePixelLayout.Rgba, ResizeDataType.UInt8Srgb, inStride, outStride, opts1T);
        StbImageResize.Resize(input, inW, inH, out4TSpan, outW, outH, ResizePixelLayout.Rgba, ResizeDataType.UInt8Srgb, inStride, outStride, opts4T);
        StbImageResize.ResizeMemory(input, inW, inH, out4TMem, outW, outH, ResizePixelLayout.Rgba, ResizeDataType.UInt8Srgb, inStride, outStride, opts4T);

        ResizeTestHelpers.AssertMatchesOracle(out4TSpan, out1T, ResizeDataType.UInt8Srgb, exact: true, context: "4T Span subrect+neg-stride vs 1T");
        ResizeTestHelpers.AssertMatchesOracle(out4TMem, out1T, ResizeDataType.UInt8Srgb, exact: true, context: "4T Memory subrect+neg-stride vs 1T");

        // Confirm untouched border sentinel byte was preserved.
        Assert.Equal((byte)0xA5, out4TSpan[0]);
        Assert.Equal((byte)0xA5, out4TMem[(outW * channels) + 5]);
    }

    [Fact]
    public void ParallelImageBuffersMatchSingleThreadedBitExactly()
    {
        const int inW = 200;
        const int inH = 160;
        const int outW = 111;
        const int outH = 97;

        byte[] raw8 = ResizeTestHelpers.CreateImageBytes(inW, inH, ResizePixelLayout.Rgba, ResizeDataType.UInt8Srgb, seed: 5151);
        ImageBuffer<byte> img8 = new(inW, inH, ColorComponents.RedGreenBlueAlpha, ColorComponents.RedGreenBlueAlpha, raw8);

        ImageResizeOptions opts1T = new() { Options = new StbOptions { MaxDegreeOfParallelism = 1 } };
        ImageResizeOptions opts4T = new() { Options = new StbOptions { MaxDegreeOfParallelism = 4 } };

        ImageBuffer<byte> res8_1T = StbImageResize.Resize(img8, outW, outH, ResizeDataType.UInt8Srgb, opts1T);
        ImageBuffer<byte> res8_4T = StbImageResize.Resize(img8, outW, outH, ResizeDataType.UInt8Srgb, opts4T);
        Assert.Equal(res8_1T.GetRawArray(), res8_4T.GetRawArray());

        ushort[] raw16 = new ushort[inW * inH * 3];
        for (int i = 0; i < raw16.Length; i++)
        {
            raw16[i] = (ushort)((i * 257) & 0xFFFF);
        }

        ImageBuffer<ushort> img16 = new(inW, inH, ColorComponents.RedGreenBlue, ColorComponents.RedGreenBlue, raw16);
        ImageBuffer<ushort> res16_1T = StbImageResize.Resize(img16, outW, outH, opts1T);
        ImageBuffer<ushort> res16_4T = StbImageResize.Resize(img16, outW, outH, opts4T);
        Assert.Equal(res16_1T.GetRawArray(), res16_4T.GetRawArray());

        float[] rawF = new float[inW * inH * 4];
        for (int i = 0; i < rawF.Length; i++)
        {
            rawF[i] = ((i % 97) / 96.0f) * 1.5f;
        }

        ImageBuffer<float> imgF = new(inW, inH, ColorComponents.RedGreenBlueAlpha, ColorComponents.RedGreenBlueAlpha, rawF);
        ImageBuffer<float> resF_1T = StbImageResize.Resize(imgF, outW, outH, opts1T);
        ImageBuffer<float> resF_4T = StbImageResize.Resize(imgF, outW, outH, opts4T);
        Assert.Equal(resF_1T.GetRawArray(), resF_4T.GetRawArray());
    }
}
