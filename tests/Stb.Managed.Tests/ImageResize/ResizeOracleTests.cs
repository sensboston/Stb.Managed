using System.Collections.Generic;
using Stb.Managed.Oracle;
using Xunit;

namespace Stb.Managed.Tests.ImageResize;

public sealed class ResizeOracleTests
{
    public static IEnumerable<object[]> AllLayouts()
    {
        for (int i = 0; i <= (int)ResizePixelLayout.ArPremultiplied; i++)
        {
            yield return [(ResizePixelLayout)i];
        }
    }

    public static IEnumerable<object[]> AllDataTypes()
    {
        for (int i = 0; i <= (int)ResizeDataType.Fp16; i++)
        {
            yield return [(ResizeDataType)i];
        }
    }

    public static IEnumerable<object[]> AllFilters()
    {
        for (int i = 0; i <= (int)ResizeFilter.Muratori6; i++)
        {
            yield return [(ResizeFilter)i];
        }
    }

    public static IEnumerable<object[]> AllEdges()
    {
        for (int i = 0; i <= (int)ResizeEdge.Zero; i++)
        {
            yield return [(ResizeEdge)i];
        }
    }

    [Theory]
    [MemberData(nameof(AllLayouts))]
    public void AllPixelLayoutsMatchOracleUpsampleAndDownsample(ResizePixelLayout layout)
    {
        ResizeDataType[] dataTypes =
        [
            ResizeDataType.UInt8Linear,
            ResizeDataType.UInt8Srgb,
            ResizeDataType.UInt8SrgbAlpha,
            ResizeDataType.UInt16Linear,
            ResizeDataType.Fp32,
            ResizeDataType.Fp16,
        ];

        (int InW, int InH, int OutW, int OutH)[] sizes =
        [
            (23, 19, 47, 37), // upsample
            (47, 37, 21, 17), // downsample
            (19, 17, 19, 17), // 1:1 copy
        ];

        int channels = ResizeTestHelpers.GetChannelCount(layout);

        foreach (ResizeDataType dt in dataTypes)
        {
            int typeSize = ResizeTestHelpers.GetTypeSize(dt);
            foreach ((int inW, int inH, int outW, int outH) in sizes)
            {
                byte[] src = ResizeTestHelpers.CreateImageBytes(inW, inH, layout, dt, seed: 100 + ((int)layout * 31) + (int)dt, includeZeroAlpha: true);
                byte[] expected = new byte[outW * outH * channels * typeSize];
                byte[] actual = new byte[expected.Length];

                Assert.True(StbOracle.ResizeBytes(src, inW, inH, 0, expected, outW, outH, 0, (int)layout, (int)dt, (int)ResizeEdge.Clamp, (int)ResizeFilter.Default));
                StbImageResize.Resize(src, inW, inH, actual, outW, outH, layout, dt, ResizeEdge.Clamp, ResizeFilter.Default);

                bool expectExact = inW == outW && inH == outH;
                ResizeTestHelpers.AssertMatchesOracle(actual, expected, dt, exact: expectExact, context: $"{layout}/{dt} {inW}x{inH}->{outW}x{outH}");
            }
        }
    }

    [Theory]
    [MemberData(nameof(AllFilters))]
    public void AllFiltersMatchOracleAcrossScales(ResizeFilter filter)
    {
        (int InW, int InH, int OutW, int OutH)[] cases =
        [
            (31, 23, 73, 59), // non-integer upsample
            (73, 59, 29, 19), // non-integer downsample
            (64, 64, 32, 32), // exact 2:1 polyphase period-1
            (32, 32, 64, 64), // exact 1:2 upsample
            (25, 21, 25, 21), // 1:1
            (17, 65, 53, 19), // mixed: upsample H, downsample V
            (65, 17, 19, 53), // mixed: downsample H, upsample V
        ];

        ResizeDataType[] dts = [ResizeDataType.UInt8Srgb, ResizeDataType.UInt8Linear, ResizeDataType.Fp32];
        ResizePixelLayout[] layouts = [ResizePixelLayout.Rgba, ResizePixelLayout.Rgb, ResizePixelLayout.OneChannel];

        foreach (ResizePixelLayout layout in layouts)
        {
            int channels = ResizeTestHelpers.GetChannelCount(layout);
            foreach (ResizeDataType dt in dts)
            {
                int typeSize = ResizeTestHelpers.GetTypeSize(dt);
                foreach ((int inW, int inH, int outW, int outH) in cases)
                {
                    byte[] src = ResizeTestHelpers.CreateImageBytes(inW, inH, layout, dt, seed: 777 + (int)filter, includeZeroAlpha: true);
                    byte[] expected = new byte[outW * outH * channels * typeSize];
                    byte[] actual = new byte[expected.Length];

                    Assert.True(StbOracle.ResizeBytes(src, inW, inH, 0, expected, outW, outH, 0, (int)layout, (int)dt, (int)ResizeEdge.Clamp, (int)filter));
                    StbImageResize.Resize(src, inW, inH, actual, outW, outH, layout, dt, ResizeEdge.Clamp, filter);

                    bool expectExact = filter == ResizeFilter.PointSample;
                    ResizeTestHelpers.AssertMatchesOracle(actual, expected, dt, exact: expectExact, context: $"{filter}/{layout}/{dt} {inW}x{inH}->{outW}x{outH}");
                }
            }
        }
    }

    [Theory]
    [MemberData(nameof(AllEdges))]
    public void AllEdgeModesMatchOracleIncludingTinyAndWrapWideFilters(ResizeEdge edge)
    {
        (int InW, int InH, int OutW, int OutH, ResizeFilter Filter)[] cases =
        [
            (1, 1, 7, 5, ResizeFilter.CatmullRom),
            (7, 5, 1, 1, ResizeFilter.Mitchell),
            (1, 17, 9, 5, ResizeFilter.Lanczos4),
            (17, 1, 5, 9, ResizeFilter.Lanczos5),
            (3, 3, 19, 17, ResizeFilter.Muratori6),
            (19, 17, 3, 3, ResizeFilter.Muratori6),
            (29, 23, 41, 13, ResizeFilter.Mitchell),
            (15, 15, 31, 31, ResizeFilter.PointSample),
        ];

        foreach ((int inW, int inH, int outW, int outH, ResizeFilter filter) in cases)
        {
            byte[] src = ResizeTestHelpers.CreateImageBytes(inW, inH, ResizePixelLayout.Rgba, ResizeDataType.UInt8Srgb, seed: 901 + (int)edge, includeZeroAlpha: true);
            byte[] expected = new byte[outW * outH * 4];
            byte[] actual = new byte[expected.Length];

            Assert.True(StbOracle.ResizeBytes(src, inW, inH, 0, expected, outW, outH, 0, (int)ResizePixelLayout.Rgba, (int)ResizeDataType.UInt8Srgb, (int)edge, (int)filter));
            StbImageResize.Resize(src, inW, inH, actual, outW, outH, ResizePixelLayout.Rgba, ResizeDataType.UInt8Srgb, edge, filter);

            ResizeTestHelpers.AssertMatchesOracle(actual, expected, ResizeDataType.UInt8Srgb, exact: filter == ResizeFilter.PointSample, context: $"{edge}/{filter} {inW}x{inH}->{outW}x{outH}");
        }
    }

    [Theory]
    [MemberData(nameof(AllEdges))]
    public void VerticalScatterDownsampleOver128TapsMatchesOracle(ResizeEdge edge)
    {
        // 600 -> 7 with Mitchell (support 2.0): input radius ~ 2 * 600/7 = 171 -> filterPixelWidth ~ 343 (> 128) => scatter path!
        // Test both horizontal-first and vertical-first geometries.
        (int InW, int InH, int OutW, int OutH, ResizeFilter Filter)[] cases =
        [
            (33, 600, 17, 7, ResizeFilter.Mitchell),     // h-downsample + v-scatter
            (13, 520, 41, 5, ResizeFilter.Mitchell),     // h-upsample + v-scatter (vertical-first!)
            (24, 512, 12, 8, ResizeFilter.Muratori6),    // exact power-of-two polyphase scatter
            (19, 450, 19, 4, ResizeFilter.Lanczos4),     // 1:1 H + v-scatter
        ];

        foreach ((int inW, int inH, int outW, int outH, ResizeFilter filter) in cases)
        {
            byte[] src = ResizeTestHelpers.CreateImageBytes(inW, inH, ResizePixelLayout.Rgba, ResizeDataType.UInt8Srgb, seed: 303 + (int)edge, includeZeroAlpha: true);
            byte[] expected = new byte[outW * outH * 4];
            byte[] actual = new byte[expected.Length];

            Assert.True(StbOracle.ResizeBytes(src, inW, inH, 0, expected, outW, outH, 0, (int)ResizePixelLayout.Rgba, (int)ResizeDataType.UInt8Srgb, (int)edge, (int)filter));
            StbImageResize.Resize(src, inW, inH, actual, outW, outH, ResizePixelLayout.Rgba, ResizeDataType.UInt8Srgb, edge, filter);

            ResizeTestHelpers.AssertMatchesOracle(actual, expected, ResizeDataType.UInt8Srgb, context: $"scatter {edge}/{filter} {inW}x{inH}->{outW}x{outH}");
        }
    }

    [Fact]
    public void BothVerticalFirstAndHorizontalFirstOrdersAreExercisedAndMatchOracle()
    {
        // Strong horizontal upsample with vertical downsample -> vertical-first.
        StbImageResizer vFirst = StbImageResizer.Create(32, 256, 256, 32, ResizePixelLayout.Rgba, ResizeDataType.UInt8Srgb);
        // Strong horizontal downsample with vertical upsample -> horizontal-first.
        StbImageResizer hFirst = StbImageResizer.Create(256, 32, 32, 256, ResizePixelLayout.Rgba, ResizeDataType.UInt8Srgb);

        Assert.True(vFirst.IsVerticalFirst);
        Assert.False(hFirst.IsVerticalFirst);

        foreach (StbImageResizer resizer in new[] { vFirst, hFirst })
        {
            byte[] src = ResizeTestHelpers.CreateImageBytes(resizer.InputWidth, resizer.InputHeight, ResizePixelLayout.Rgba, ResizeDataType.UInt8Srgb, seed: 555, includeZeroAlpha: true);
            byte[] expected = new byte[resizer.OutputWidth * resizer.OutputHeight * 4];
            byte[] actual = new byte[expected.Length];

            Assert.True(StbOracle.ResizeBytes(src, resizer.InputWidth, resizer.InputHeight, 0, expected, resizer.OutputWidth, resizer.OutputHeight, 0, (int)ResizePixelLayout.Rgba, (int)ResizeDataType.UInt8Srgb, 0, 0));
            resizer.Resize(src, actual);

            ResizeTestHelpers.AssertMatchesOracle(actual, expected, ResizeDataType.UInt8Srgb, context: $"verticalFirst={resizer.IsVerticalFirst}");
        }
    }
}
