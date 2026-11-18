using System;
using System.Runtime.InteropServices;
using Stb.Managed.Internal;
using Xunit;

namespace Stb.Managed.Tests.ImageResize;

public sealed class ResizeValidationTests
{
    [Theory]
    [InlineData(0, 10, 10, 10)]
    [InlineData(10, -1, 10, 10)]
    [InlineData(10, 10, 0, 10)]
    [InlineData(10, 10, 10, -5)]
    public void NonPositiveDimensionsThrowInvalidDimensions(int inW, int inH, int outW, int outH)
    {
        StbException ex = Assert.Throws<StbException>(() =>
            StbImageResizer.Create(inW, inH, outW, outH, ResizePixelLayout.Rgba, ResizeDataType.UInt8Linear));
        Assert.Equal(StbErrorCode.InvalidArgument, ex.Code);
    }

    [Fact]
    public void DimensionsExceedingLimitsThrowLimitExceeded()
    {
        StbOptions tightOptions = new()
        {
            Limits = new StbLimits { MaxWidth = 64, MaxHeight = 64, MaxPixels = 1024 },
        };
        ImageResizeOptions opts = new() { Options = tightOptions };

        StbException ex1 = Assert.Throws<StbException>(() =>
            StbImageResizer.Create(65, 10, 10, 10, ResizePixelLayout.Rgb, ResizeDataType.UInt8Linear, opts));
        Assert.Equal(StbErrorCode.LimitExceeded, ex1.Code);

        StbException ex2 = Assert.Throws<StbException>(() =>
            StbImageResizer.Create(10, 10, 40, 40, ResizePixelLayout.Rgb, ResizeDataType.UInt8Linear, opts));
        Assert.Equal(StbErrorCode.LimitExceeded, ex2.Code);
    }

    [Fact]
    public void MismatchedChannelCountThrowsInvalidArgument()
    {
        StbException ex = Assert.Throws<StbException>(() =>
            StbImageResizer.Create(
                16, 16, 8, 8,
                ResizePixelLayout.Rgb,
                ResizeDataType.UInt8Linear,
                new ImageResizeOptions { OutputLayout = ResizePixelLayout.Rgba }));
        Assert.Equal(StbErrorCode.InvalidArgument, ex.Code);
    }

    [Fact]
    public void InvalidEnumValuesThrowArgumentOutOfRange()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new ImageResizeOptions { HorizontalFilter = (ResizeFilter)99 });
        Assert.Throws<ArgumentOutOfRangeException>(() => new ImageResizeOptions { VerticalFilter = (ResizeFilter)(-1) });
        Assert.Throws<ArgumentOutOfRangeException>(() => new ImageResizeOptions { HorizontalEdge = (ResizeEdge)4 });
        Assert.Throws<ArgumentOutOfRangeException>(() => new ImageResizeOptions { VerticalEdge = (ResizeEdge)(-1) });
        Assert.Throws<ArgumentOutOfRangeException>(() => new ImageResizeOptions { OutputLayout = (ResizePixelLayout)42 });
        Assert.Throws<ArgumentOutOfRangeException>(() => new ImageResizeOptions { OutputDataType = (ResizeDataType)99 });
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            StbImageResizer.Create(4, 4, 2, 2, (ResizePixelLayout)99, ResizeDataType.UInt8Linear));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            StbImageResizer.Create(4, 4, 2, 2, ResizePixelLayout.Rgba, (ResizeDataType)99));
    }

    [Fact]
    public void UndersizedBuffersAndInvalidStridesThrowInvalidArgument()
    {
        StbImageResizer resizer = StbImageResizer.Create(8, 8, 4, 4, ResizePixelLayout.Rgba, ResizeDataType.UInt8Linear);
        byte[] validIn = new byte[8 * 8 * 4];
        byte[] validOut = new byte[4 * 4 * 4];

        StbException smallIn = Assert.Throws<StbException>(() => resizer.Resize(validIn.AsSpan(0, validIn.Length - 1), validOut));
        Assert.Equal(StbErrorCode.InvalidArgument, smallIn.Code);

        StbException smallOut = Assert.Throws<StbException>(() => resizer.Resize(validIn, validOut.AsSpan(0, validOut.Length - 1)));
        Assert.Equal(StbErrorCode.InvalidArgument, smallOut.Code);

        StbException badStride = Assert.Throws<StbException>(() => resizer.Resize(validIn, validOut, inputStrideBytes: 16));
        Assert.Equal(StbErrorCode.InvalidArgument, badStride.Code);

        StbException minIntStride = Assert.Throws<StbException>(() => resizer.Resize(validIn, validOut, inputStrideBytes: int.MinValue));
        Assert.Equal(StbErrorCode.InvalidArgument, minIntStride.Code);
    }

    [Fact]
    public void InvalidSubrectConfigurationsThrowInvalidArgument()
    {
        // Combining PixelSubrect with InputSubrect
        StbException ex1 = Assert.Throws<StbException>(() =>
            StbImageResizer.Create(8, 8, 8, 8, ResizePixelLayout.Rgba, ResizeDataType.UInt8Linear,
                new ImageResizeOptions
                {
                    PixelSubrect = new ResizeRect(0, 0, 4, 4),
                    InputSubrect = new ResizeNormalizedRect(0, 0, 1, 1),
                }));
        Assert.Equal(StbErrorCode.InvalidArgument, ex1.Code);

        // NaN InputSubrect
        StbException ex2 = Assert.Throws<StbException>(() =>
            StbImageResizer.Create(8, 8, 8, 8, ResizePixelLayout.Rgba, ResizeDataType.UInt8Linear,
                new ImageResizeOptions { InputSubrect = new ResizeNormalizedRect(double.NaN, 0, 1, 1) }));
        Assert.Equal(StbErrorCode.InvalidArgument, ex2.Code);

        // Reversed InputSubrect
        StbException ex3 = Assert.Throws<StbException>(() =>
            StbImageResizer.Create(8, 8, 8, 8, ResizePixelLayout.Rgba, ResizeDataType.UInt8Linear,
                new ImageResizeOptions { InputSubrect = new ResizeNormalizedRect(0.8, 0, 0.2, 1) }));
        Assert.Equal(StbErrorCode.InvalidArgument, ex3.Code);

        // Negative OutputSubrect dimensions
        StbException ex4 = Assert.Throws<StbException>(() =>
            StbImageResizer.Create(8, 8, 8, 8, ResizePixelLayout.Rgba, ResizeDataType.UInt8Linear,
                new ImageResizeOptions { OutputSubrect = new ResizeRect(0, 0, -2, 4) }));
        Assert.Equal(StbErrorCode.InvalidArgument, ex4.Code);
    }

    [Fact]
    public void SpecialFloatValuesNanAndInfinityDoNotCrashIntegerEncoders()
    {
        float[] src =
        [
            float.NaN, float.PositiveInfinity, float.NegativeInfinity, 1.0f,
            -10.0f, 10.0f, 0.5f, 0.0f,
            0.0f, 0.25f, 0.75f, float.NaN,
            1.0f, 0.0f, float.NegativeZero, 0.5f,
        ];

        ResizeDataType[] targets =
        [
            ResizeDataType.UInt8Linear,
            ResizeDataType.UInt8Srgb,
            ResizeDataType.UInt8SrgbAlpha,
            ResizeDataType.UInt16Linear,
            ResizeDataType.Fp16,
        ];

        foreach (ResizeDataType target in targets)
        {
            byte[] dst = new byte[2 * 2 * 4 * ResizeTestHelpers.GetTypeSize(target)];
            StbImageResize.Resize(
                MemoryMarshal.AsBytes(src.AsSpan()),
                2, 2,
                dst, 2, 2,
                ResizePixelLayout.Rgba,
                ResizeDataType.Fp32,
                options: new ImageResizeOptions
                {
                    OutputDataType = target,
                    HorizontalFilter = ResizeFilter.PointSample,
                    VerticalFilter = ResizeFilter.PointSample,
                });

            if (target == ResizeDataType.UInt8Linear || target == ResizeDataType.UInt8Srgb)
            {
                // NaN -> 0, +Inf -> 255, -Inf -> 0
                Assert.Equal(0, dst[0]);
                Assert.Equal(255, dst[1]);
                Assert.Equal(0, dst[2]);
            }
        }
    }

    [Fact]
    public void HalfFloatBitConversionsMatchIeee754Binary16()
    {
        Assert.Equal(0x0000, ResizeCoders.FloatToHalf(0.0f));
        Assert.Equal(0x8000, ResizeCoders.FloatToHalf(-0.0f));
        Assert.Equal(0x3C00, ResizeCoders.FloatToHalf(1.0f));
        Assert.Equal(0xBC00, ResizeCoders.FloatToHalf(-1.0f));
        Assert.Equal(0xC100, ResizeCoders.FloatToHalf(-2.5f));
        Assert.Equal(0x7BFF, ResizeCoders.FloatToHalf(65504.0f));
        Assert.Equal(0x7C00, ResizeCoders.FloatToHalf(float.PositiveInfinity));
        Assert.Equal(0xFC00, ResizeCoders.FloatToHalf(float.NegativeInfinity));
        ushort nanHalf = ResizeCoders.FloatToHalf(float.NaN);
        Assert.Equal(0x7E00, nanHalf & 0x7FFF);
        Assert.True(float.IsNaN(ResizeCoders.HalfToFloat(nanHalf)));

        Assert.Equal(0.0f, ResizeCoders.HalfToFloat(0x0000));
        Assert.Equal(1.0f, ResizeCoders.HalfToFloat(0x3C00));
        Assert.Equal(-2.5f, ResizeCoders.HalfToFloat(0xC100));
        Assert.Equal(65504.0f, ResizeCoders.HalfToFloat(0x7BFF));
        Assert.True(float.IsPositiveInfinity(ResizeCoders.HalfToFloat(0x7C00)));
        Assert.True(float.IsNegativeInfinity(ResizeCoders.HalfToFloat(0xFC00)));
        Assert.True(float.IsNaN(ResizeCoders.HalfToFloat(0x7E00)));

        // Subnormal round-trip (smallest positive subnormal 2^-24).
        float minSubnormal = ResizeCoders.HalfToFloat(0x0001);
        Assert.Equal(1.0f / (1 << 24), minSubnormal);
        Assert.Equal(0x0001, ResizeCoders.FloatToHalf(minSubnormal));
    }
}
