using System;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using Stb.Managed.Oracle;
using Xunit;

namespace Stb.Managed.Tests.ImageResize;

public sealed class ResizeExtendedOracleTests
{
    [Fact]
    public void CrossLayoutConversionsMatchOracle()
    {
        (ResizePixelLayout InLayout, ResizePixelLayout OutLayout)[] pairs =
        [
            (ResizePixelLayout.Rgb, ResizePixelLayout.Bgr),
            (ResizePixelLayout.Bgr, ResizePixelLayout.Rgb),
            (ResizePixelLayout.Rgba, ResizePixelLayout.Bgra),
            (ResizePixelLayout.Bgra, ResizePixelLayout.Argb),
            (ResizePixelLayout.Argb, ResizePixelLayout.Abgr),
            (ResizePixelLayout.Abgr, ResizePixelLayout.Rgba),
            (ResizePixelLayout.Rgba, ResizePixelLayout.RgbaPremultiplied),
            (ResizePixelLayout.RgbaPremultiplied, ResizePixelLayout.Rgba),
            (ResizePixelLayout.Bgra, ResizePixelLayout.ArgbPremultiplied),
            (ResizePixelLayout.Ra, ResizePixelLayout.Ar),
            (ResizePixelLayout.Ar, ResizePixelLayout.RaPremultiplied),
            (ResizePixelLayout.RaPremultiplied, ResizePixelLayout.Ar),
        ];

        (int InW, int InH, int OutW, int OutH)[] sizes =
        [
            (25, 19, 51, 39), // upsample (exercises 3ch flip on decode)
            (51, 39, 21, 15), // downsample (exercises 3ch flip on encode)
        ];

        foreach ((ResizePixelLayout inL, ResizePixelLayout outL) in pairs)
        {
            int channels = ResizeTestHelpers.GetChannelCount(inL);
            foreach ((int inW, int inH, int outW, int outH) in sizes)
            {
                byte[] src = ResizeTestHelpers.CreateImageBytes(inW, inH, inL, ResizeDataType.UInt8Srgb, seed: 4242, includeZeroAlpha: true);
                byte[] expected = new byte[outW * outH * channels];
                byte[] actual = new byte[expected.Length];

                Assert.True(StbOracle.ResizeBytesExtended(
                    src, inW, inH, 0,
                    expected, outW, outH, 0,
                    (int)inL, (int)outL,
                    (int)ResizeDataType.UInt8Srgb, (int)ResizeDataType.UInt8Srgb));

                StbImageResize.Resize(
                    src, inW, inH, actual, outW, outH, inL, ResizeDataType.UInt8Srgb,
                    options: new ImageResizeOptions { OutputLayout = outL });

                ResizeTestHelpers.AssertMatchesOracle(actual, expected, ResizeDataType.UInt8Srgb, context: $"{inL}->{outL} {inW}x{inH}->{outW}x{outH}");
            }
        }
    }

    [Fact]
    public void CrossDataTypeConversionsMatchOracle()
    {
        (ResizeDataType InType, ResizeDataType OutType)[] pairs =
        [
            (ResizeDataType.UInt8Srgb, ResizeDataType.Fp32),
            (ResizeDataType.Fp32, ResizeDataType.UInt8Srgb),
            (ResizeDataType.UInt8Linear, ResizeDataType.UInt16Linear),
            (ResizeDataType.UInt16Linear, ResizeDataType.UInt8Linear),
            (ResizeDataType.UInt8Srgb, ResizeDataType.Fp16),
            (ResizeDataType.Fp16, ResizeDataType.UInt8Srgb),
            (ResizeDataType.Fp32, ResizeDataType.Fp16),
            (ResizeDataType.Fp16, ResizeDataType.Fp32),
            (ResizeDataType.UInt8SrgbAlpha, ResizeDataType.UInt8Linear),
            (ResizeDataType.UInt8Linear, ResizeDataType.UInt8SrgbAlpha),
        ];

        const int inW = 37;
        const int inH = 29;
        const int outW = 23;
        const int outH = 41;

        foreach ((ResizeDataType inDt, ResizeDataType outDt) in pairs)
        {
            byte[] src = ResizeTestHelpers.CreateImageBytes(inW, inH, ResizePixelLayout.Rgba, inDt, seed: 888, includeZeroAlpha: true);
            byte[] expected = new byte[outW * outH * 4 * ResizeTestHelpers.GetTypeSize(outDt)];
            byte[] actual = new byte[expected.Length];

            Assert.True(StbOracle.ResizeBytesExtended(
                src, inW, inH, 0,
                expected, outW, outH, 0,
                (int)ResizePixelLayout.Rgba, (int)ResizePixelLayout.Rgba,
                (int)inDt, (int)outDt));

            StbImageResize.Resize(
                src, inW, inH, actual, outW, outH, ResizePixelLayout.Rgba, inDt,
                options: new ImageResizeOptions { OutputDataType = outDt });

            ResizeTestHelpers.AssertMatchesOracle(actual, expected, outDt, context: $"{inDt}->{outDt}");
        }
    }

    [Fact]
    public void SeparateHorizontalAndVerticalFiltersEdgesAndFastAlphaMatchOracle()
    {
        const int inW = 43;
        const int inH = 35;
        const int outW = 61;
        const int outH = 21;

        ResizePixelLayout[] alphaLayouts = [ResizePixelLayout.Rgba, ResizePixelLayout.Bgra, ResizePixelLayout.Ra, ResizePixelLayout.Ar];

        foreach (ResizePixelLayout layout in alphaLayouts)
        {
            int channels = ResizeTestHelpers.GetChannelCount(layout);
            byte[] src = ResizeTestHelpers.CreateImageBytes(inW, inH, layout, ResizeDataType.UInt8Srgb, seed: 5150, includeZeroAlpha: true);
            byte[] expected = new byte[outW * outH * channels];
            byte[] actual = new byte[expected.Length];

            Assert.True(StbOracle.ResizeBytesExtended(
                src, inW, inH, 0,
                expected, outW, outH, 0,
                (int)layout, (int)layout,
                (int)ResizeDataType.UInt8Srgb, (int)ResizeDataType.UInt8Srgb,
                horizontalEdge: (int)ResizeEdge.Wrap,
                verticalEdge: (int)ResizeEdge.Reflect,
                horizontalFilter: (int)ResizeFilter.Lanczos5,
                verticalFilter: (int)ResizeFilter.Box,
                fastAlpha: true));

            StbImageResize.Resize(
                src, inW, inH, actual, outW, outH, layout, ResizeDataType.UInt8Srgb,
                options: new ImageResizeOptions
                {
                    HorizontalEdge = ResizeEdge.Wrap,
                    VerticalEdge = ResizeEdge.Reflect,
                    HorizontalFilter = ResizeFilter.Lanczos5,
                    VerticalFilter = ResizeFilter.Box,
                    FastAlpha = true,
                });

            ResizeTestHelpers.AssertMatchesOracle(actual, expected, ResizeDataType.UInt8Srgb, context: $"separate+fastAlpha {layout}");
        }
    }

    [Fact]
    public void InputSubrectAndOutputSubrectMatchOracleAndPreserveOutsideSentinel()
    {
        const int inW = 40;
        const int inH = 32;
        const int outW = 60;
        const int outH = 48;

        byte[] src = ResizeTestHelpers.CreateImageBytes(inW, inH, ResizePixelLayout.Rgba, ResizeDataType.UInt8Srgb, seed: 1234, includeZeroAlpha: true);
        byte[] expected = new byte[outW * outH * 4];
        byte[] actual = new byte[expected.Length];
        Array.Fill(expected, (byte)0xA5);
        Array.Fill(actual, (byte)0xA5);

        Assert.True(StbOracle.ResizeBytesExtended(
            src, inW, inH, 0,
            expected, outW, outH, 0,
            (int)ResizePixelLayout.Rgba, (int)ResizePixelLayout.Rgba,
            (int)ResizeDataType.UInt8Srgb, (int)ResizeDataType.UInt8Srgb,
            hasInputSubrect: true, s0: 0.15, t0: 0.10, s1: 0.85, t1: 0.90,
            hasOutputSubrect: true, outSubx: 7, outSuby: 5, outSubw: 39, outSubh: 31));

        StbImageResize.Resize(
            src, inW, inH, actual, outW, outH, ResizePixelLayout.Rgba, ResizeDataType.UInt8Srgb,
            options: new ImageResizeOptions
            {
                InputSubrect = new ResizeNormalizedRect(0.15, 0.10, 0.85, 0.90),
                OutputSubrect = new ResizeRect(7, 5, 39, 31),
            });

        ResizeTestHelpers.AssertMatchesOracle(actual, expected, ResizeDataType.UInt8Srgb, context: "InputSubrect+OutputSubrect");

        // Verify sentinel outside subrect was untouched.
        Assert.Equal(0xA5, actual[0]);
        Assert.Equal(0xA5, actual[^1]);
    }

    [Fact]
    public void OutOfBoundsClippedSubrectAndEmptySubrectMatchOracle()
    {
        const int inW = 32;
        const int inH = 24;
        const int outW = 48;
        const int outH = 36;

        byte[] src = ResizeTestHelpers.CreateImageBytes(inW, inH, ResizePixelLayout.Rgb, ResizeDataType.UInt8Linear, seed: 707);

        // Partially out-of-bounds output + input subrect gets clipped identically to oracle.
        byte[] expected = new byte[outW * outH * 3];
        byte[] actual = new byte[expected.Length];
        Array.Fill(expected, (byte)0x5A);
        Array.Fill(actual, (byte)0x5A);

        Assert.True(StbOracle.ResizeBytesExtended(
            src, inW, inH, 0,
            expected, outW, outH, 0,
            (int)ResizePixelLayout.Rgb, (int)ResizePixelLayout.Rgb,
            (int)ResizeDataType.UInt8Linear, (int)ResizeDataType.UInt8Linear,
            hasInputSubrect: true, s0: -0.25, t0: -0.1, s1: 1.2, t1: 1.15,
            hasOutputSubrect: true, outSubx: -4, outSuby: -2, outSubw: 56, outSubh: 40));

        StbImageResize.Resize(
            src, inW, inH, actual, outW, outH, ResizePixelLayout.Rgb, ResizeDataType.UInt8Linear,
            options: new ImageResizeOptions
            {
                InputSubrect = new ResizeNormalizedRect(-0.25, -0.1, 1.2, 1.15),
                OutputSubrect = new ResizeRect(-4, -2, 56, 40),
            });

        ResizeTestHelpers.AssertMatchesOracle(actual, expected, ResizeDataType.UInt8Linear, context: "ClippedSubrect");

        // Completely out-of-bounds subrect leaves destination untouched.
        byte[] untouched = new byte[outW * outH * 3];
        Array.Fill(untouched, (byte)0x33);
        StbImageResize.Resize(
            src, inW, inH, untouched, outW, outH, ResizePixelLayout.Rgb, ResizeDataType.UInt8Linear,
            options: new ImageResizeOptions { OutputSubrect = new ResizeRect(outW + 10, 0, 10, 10) });
        Assert.All(untouched, b => Assert.Equal((byte)0x33, b));
    }

    [Fact]
    public void PixelSubrectTilingMatchesFullResizeAndOracle()
    {
        const int inW = 53;
        const int inH = 41;
        const int outW = 84;
        const int outH = 66;

        byte[] src = ResizeTestHelpers.CreateImageBytes(inW, inH, ResizePixelLayout.Rgba, ResizeDataType.UInt8Srgb, seed: 999, includeZeroAlpha: true);
        byte[] full = StbImageResize.Resize(src, inW, inH, outW, outH, ResizePixelLayout.Rgba, ResizeDataType.UInt8Srgb);

        byte[] tiled = new byte[outW * outH * 4];
        byte[] tiledOracle = new byte[outW * outH * 4];

        int halfW = outW / 2;
        int halfH = outH / 2;
        ResizeRect[] tiles =
        [
            new ResizeRect(0, 0, halfW, halfH),
            new ResizeRect(halfW, 0, outW - halfW, halfH),
            new ResizeRect(0, halfH, halfW, outH - halfH),
            new ResizeRect(halfW, halfH, outW - halfW, outH - halfH),
        ];

        foreach (ResizeRect tile in tiles)
        {
            Assert.True(StbOracle.ResizeBytesExtended(
                src, inW, inH, 0,
                tiledOracle, outW, outH, 0,
                (int)ResizePixelLayout.Rgba, (int)ResizePixelLayout.Rgba,
                (int)ResizeDataType.UInt8Srgb, (int)ResizeDataType.UInt8Srgb,
                hasPixelSubrect: true, pxSubx: tile.X, pxSuby: tile.Y, pxSubw: tile.Width, pxSubh: tile.Height));

            StbImageResize.Resize(
                src, inW, inH, tiled, outW, outH, ResizePixelLayout.Rgba, ResizeDataType.UInt8Srgb,
                options: new ImageResizeOptions { PixelSubrect = tile });
        }

        ResizeTestHelpers.AssertMatchesOracle(tiled, tiledOracle, ResizeDataType.UInt8Srgb, context: "PixelSubrect vs oracle");
        ResizeTestHelpers.AssertMatchesOracle(tiled, full, ResizeDataType.UInt8Srgb, exact: true, context: "PixelSubrect 2x2 vs full resize");
    }

    [Fact]
    public void PositivePaddedAndNegativeBottomUpStridesMatchOracle()
    {
        const int inW = 29;
        const int inH = 23;
        const int outW = 43;
        const int outH = 31;
        const int inRowBytes = inW * 4;
        const int outRowBytes = outW * 4;
        const int inStride = inRowBytes + 19;
        const int outStride = outRowBytes + 23;

        byte[] srcPacked = ResizeTestHelpers.CreateImageBytes(inW, inH, ResizePixelLayout.Rgba, ResizeDataType.UInt8Srgb, seed: 2026);
        byte[] srcPadded = new byte[inH * inStride];
        Array.Fill(srcPadded, (byte)0xEE);
        for (int y = 0; y < inH; y++)
        {
            Buffer.BlockCopy(srcPacked, y * inRowBytes, srcPadded, y * inStride, inRowBytes);
        }

        (int InStride, int OutStride)[] stridePairs =
        [
            (inStride, outStride),
            (-inStride, outStride),
            (inStride, -outStride),
            (-inStride, -outStride),
        ];

        foreach ((int sIn, int sOut) in stridePairs)
        {
            byte[] expected = new byte[outH * outStride];
            byte[] actual = new byte[outH * outStride];
            Array.Fill(expected, (byte)0xCC);
            Array.Fill(actual, (byte)0xCC);

            Assert.True(StbOracle.ResizeBytesExtended(
                srcPadded, inW, inH, sIn,
                expected, outW, outH, sOut,
                (int)ResizePixelLayout.Rgba, (int)ResizePixelLayout.Rgba,
                (int)ResizeDataType.UInt8Srgb, (int)ResizeDataType.UInt8Srgb));

            StbImageResize.Resize(
                srcPadded, inW, inH, actual, outW, outH,
                ResizePixelLayout.Rgba, ResizeDataType.UInt8Srgb,
                inputStrideBytes: sIn,
                outputStrideBytes: sOut);

            ResizeTestHelpers.AssertMatchesOracle(actual, expected, ResizeDataType.UInt8Srgb, context: $"strides ({sIn}, {sOut})");
        }
    }

    [Fact]
    public void CustomFilterKernelsSupportAndRadiusMatchBuiltinFilters()
    {
        const int inW = 31;
        const int inH = 27;
        const int outW = 53;
        const int outH = 19;

        byte[] src = ResizeTestHelpers.CreateImageBytes(inW, inH, ResizePixelLayout.Rgb, ResizeDataType.UInt8Linear, seed: 606);

        // Custom triangle using Support callback must match built-in Triangle bit-for-bit.
        ResizeFilterKernel customTriangle = new(
            evaluate: static (x, _) =>
            {
                double ax = x < 0.0 ? -x : x;
                return ax <= 1.0 ? 1.0 - ax : 0.0;
            },
            support: static _ => 1.0);

        byte[] builtinTri = StbImageResize.Resize(
            src, inW, inH, outW, outH, ResizePixelLayout.Rgb, ResizeDataType.UInt8Linear,
            options: new ImageResizeOptions { HorizontalFilter = ResizeFilter.Triangle, VerticalFilter = ResizeFilter.Triangle });

        byte[] customTriOut = StbImageResize.Resize(
            src, inW, inH, outW, outH, ResizePixelLayout.Rgb, ResizeDataType.UInt8Linear,
            options: new ImageResizeOptions { HorizontalCustomFilter = customTriangle, VerticalCustomFilter = customTriangle });

        Assert.Equal(builtinTri, customTriOut);

        // Custom box using Radius callback must match built-in Box bit-for-bit.
        ResizeFilterKernel customBox = new(
            evaluate: static (x, scale) =>
            {
                double halfScale = scale * 0.5;
                double ax = x < 0.0 ? -x : x;
                if (ax >= 0.5 + halfScale)
                {
                    return 0.0;
                }

                if (ax <= 0.5 - halfScale)
                {
                    return 1.0;
                }

                return (0.5 + halfScale - ax) / scale;
            },
            radius: static (_, invScale, _) => (0.5 * invScale) + 0.5);

        byte[] builtinBox = StbImageResize.Resize(
            src, inW, inH, outW, outH, ResizePixelLayout.Rgb, ResizeDataType.UInt8Linear,
            options: new ImageResizeOptions { HorizontalFilter = ResizeFilter.Box, VerticalFilter = ResizeFilter.Box });

        byte[] customBoxOut = StbImageResize.Resize(
            src, inW, inH, outW, outH, ResizePixelLayout.Rgb, ResizeDataType.UInt8Linear,
            options: new ImageResizeOptions { HorizontalCustomFilter = customBox, VerticalCustomFilter = customBox });

        Assert.Equal(builtinBox, customBoxOut);
    }

    [Fact]
    public void ReusableResizerIsThreadSafeAcrossConcurrentCalls()
    {
        const int inW = 48;
        const int inH = 36;
        const int outW = 72;
        const int outH = 24;

        StbImageResizer resizer = StbImageResizer.Create(
            inW, inH, outW, outH, ResizePixelLayout.Rgba, ResizeDataType.UInt8Srgb);

        byte[][] inputs = new byte[8][];
        byte[][] expected = new byte[8][];
        byte[][] actual = new byte[8][];

        for (int i = 0; i < 8; i++)
        {
            inputs[i] = ResizeTestHelpers.CreateImageBytes(inW, inH, ResizePixelLayout.Rgba, ResizeDataType.UInt8Srgb, seed: 1000 + i, includeZeroAlpha: true);
            expected[i] = resizer.ResizeToArray(inputs[i]);
            actual[i] = new byte[outW * outH * 4];
        }

        Parallel.For(0, 8, i =>
        {
            resizer.Resize(inputs[i], actual[i]);
        });

        for (int i = 0; i < 8; i++)
        {
            Assert.Equal(expected[i], actual[i]);
        }
    }

    [Fact]
    public void EasyApiAndImageBufferOverloadsMatchOracle()
    {
        const int inW = 24;
        const int inH = 20;
        const int outW = 36;
        const int outH = 14;

        // UInt8 sRGB & Linear
        byte[] src8 = ResizeTestHelpers.CreateImageBytes(inW, inH, ResizePixelLayout.Rgba, ResizeDataType.UInt8Srgb, seed: 11);
        byte[] out8Srgb = new byte[outW * outH * 4];
        byte[] exp8Srgb = new byte[out8Srgb.Length];
        StbImageResize.ResizeUInt8Srgb(src8, inW, inH, out8Srgb, outW, outH, ResizePixelLayout.Rgba);
        Assert.True(StbOracle.ResizeBytes(src8, inW, inH, 0, exp8Srgb, outW, outH, 0, (int)ResizePixelLayout.Rgba, (int)ResizeDataType.UInt8Srgb));
        ResizeTestHelpers.AssertMatchesOracle(out8Srgb, exp8Srgb, ResizeDataType.UInt8Srgb);

        byte[] out8Lin = new byte[outW * outH * 4];
        byte[] exp8Lin = new byte[out8Lin.Length];
        StbImageResize.ResizeUInt8Linear(src8, inW, inH, out8Lin, outW, outH, ResizePixelLayout.Rgba);
        Assert.True(StbOracle.ResizeBytes(src8, inW, inH, 0, exp8Lin, outW, outH, 0, (int)ResizePixelLayout.Rgba, (int)ResizeDataType.UInt8Linear));
        ResizeTestHelpers.AssertMatchesOracle(out8Lin, exp8Lin, ResizeDataType.UInt8Linear);

        ImageBuffer<byte> img8 = new(inW, inH, ColorComponents.RedGreenBlueAlpha, ColorComponents.RedGreenBlueAlpha, src8);
        ImageBuffer<byte> resizedImg8 = StbImageResize.Resize(img8, outW, outH, ResizeDataType.UInt8Srgb);
        Assert.Equal(outW, resizedImg8.Width);
        Assert.Equal(outH, resizedImg8.Height);
        Assert.Equal(out8Srgb, resizedImg8.GetRawArray());

        // UInt16 Linear
        byte[] src16Bytes = ResizeTestHelpers.CreateImageBytes(inW, inH, ResizePixelLayout.Rgb, ResizeDataType.UInt16Linear, seed: 22);
        ushort[] src16 = MemoryMarshal.Cast<byte, ushort>(src16Bytes.AsSpan()).ToArray();
        ushort[] out16 = new ushort[outW * outH * 3];
        byte[] exp16 = new byte[out16.Length * 2];
        StbImageResize.ResizeUInt16Linear(src16, inW, inH, out16, outW, outH, ResizePixelLayout.Rgb);
        Assert.True(StbOracle.ResizeBytes(src16Bytes, inW, inH, 0, exp16, outW, outH, 0, (int)ResizePixelLayout.Rgb, (int)ResizeDataType.UInt16Linear));
        ResizeTestHelpers.AssertMatchesOracle(MemoryMarshal.AsBytes(out16.AsSpan()), exp16, ResizeDataType.UInt16Linear);

        ImageBuffer<ushort> img16 = new(inW, inH, ColorComponents.RedGreenBlue, ColorComponents.RedGreenBlue, src16);
        ImageBuffer<ushort> resizedImg16 = StbImageResize.Resize(img16, outW, outH);
        Assert.Equal(out16, resizedImg16.GetRawArray());

        // Float32 Linear
        byte[] srcFBytes = ResizeTestHelpers.CreateImageBytes(inW, inH, ResizePixelLayout.Rgba, ResizeDataType.Fp32, seed: 33);
        float[] srcF = MemoryMarshal.Cast<byte, float>(srcFBytes.AsSpan()).ToArray();
        float[] outF = new float[outW * outH * 4];
        byte[] expF = new byte[outF.Length * 4];
        StbImageResize.ResizeFloatLinear(srcF, inW, inH, outF, outW, outH, ResizePixelLayout.Rgba);
        Assert.True(StbOracle.ResizeBytes(srcFBytes, inW, inH, 0, expF, outW, outH, 0, (int)ResizePixelLayout.Rgba, (int)ResizeDataType.Fp32));
        ResizeTestHelpers.AssertMatchesOracle(MemoryMarshal.AsBytes(outF.AsSpan()), expF, ResizeDataType.Fp32);

        ImageBuffer<float> imgF = new(inW, inH, ColorComponents.RedGreenBlueAlpha, ColorComponents.RedGreenBlueAlpha, srcF);
        ImageBuffer<float> resizedImgF = StbImageResize.Resize(imgF, outW, outH);
        Assert.Equal(outF, resizedImgF.GetRawArray());

        // Float16 Linear (ushort bits)
        byte[] srcHBytes = ResizeTestHelpers.CreateImageBytes(inW, inH, ResizePixelLayout.Rgba, ResizeDataType.Fp16, seed: 44);
        ushort[] srcH = MemoryMarshal.Cast<byte, ushort>(srcHBytes.AsSpan()).ToArray();
        ushort[] outH16 = new ushort[outW * outH * 4];
        byte[] expH16 = new byte[outH16.Length * 2];
        StbImageResize.ResizeHalfLinear(srcH, inW, inH, outH16, outW, outH, ResizePixelLayout.Rgba);
        Assert.True(StbOracle.ResizeBytes(srcHBytes, inW, inH, 0, expH16, outW, outH, 0, (int)ResizePixelLayout.Rgba, (int)ResizeDataType.Fp16));
        ResizeTestHelpers.AssertMatchesOracle(MemoryMarshal.AsBytes(outH16.AsSpan()), expH16, ResizeDataType.Fp16);

#if !TEST_NETSTANDARD
        Half[] srcHalf = MemoryMarshal.Cast<ushort, Half>(srcH.AsSpan()).ToArray();
        Half[] outHalf = new Half[outW * outH * 4];
        StbImageResize.ResizeHalfLinear(srcHalf, inW, inH, outHalf, outW, outH, ResizePixelLayout.Rgba);
        Assert.Equal(outH16, MemoryMarshal.Cast<Half, ushort>(outHalf.AsSpan()).ToArray());
#endif
    }
}
