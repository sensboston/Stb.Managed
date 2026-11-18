using Xunit;

namespace Stb.Managed.Tests.Core;

public sealed class StbLimitsTests
{
    [Fact]
    public void ValidateDimensionsReturnsExactByteCount()
    {
        int total = StbLimits.Default.ValidateDimensions(1920, 1080, 4);
        Assert.Equal(1920 * 1080 * 4, total);
    }

    [Theory]
    [InlineData(0, 10)]
    [InlineData(10, 0)]
    [InlineData(-1, 10)]
    [InlineData(10, -5)]
    public void ValidateDimensionsRejectsNonPositiveDimensions(int width, int height)
    {
        StbException ex = Assert.Throws<StbException>(() => StbLimits.Default.ValidateDimensions(width, height, 4));
        Assert.Equal(StbErrorCode.InvalidData, ex.Code);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-4)]
    public void ValidateDimensionsRejectsNonPositiveBytesPerPixel(int bpp)
    {
        StbException ex = Assert.Throws<StbException>(() => StbLimits.Default.ValidateDimensions(16, 16, bpp));
        Assert.Equal(StbErrorCode.InvalidArgument, ex.Code);
    }

    [Fact]
    public void ValidateDimensionsEnforcesMaxWidthAndHeight()
    {
        StbLimits limits = new() { MaxWidth = 100, MaxHeight = 200 };

        StbException widthEx = Assert.Throws<StbException>(() => limits.ValidateDimensions(101, 50, 4));
        Assert.Equal(StbErrorCode.LimitExceeded, widthEx.Code);

        StbException heightEx = Assert.Throws<StbException>(() => limits.ValidateDimensions(50, 201, 4));
        Assert.Equal(StbErrorCode.LimitExceeded, heightEx.Code);
    }

    [Fact]
    public void ValidateDimensionsEnforcesMaxPixels()
    {
        StbLimits limits = new() { MaxPixels = 1000 };
        StbException ex = Assert.Throws<StbException>(() => limits.ValidateDimensions(40, 30, 1));
        Assert.Equal(StbErrorCode.LimitExceeded, ex.Code);
    }

    [Fact]
    public void ValidateDimensionsEnforcesMaxAllocationBytes()
    {
        StbLimits limits = new() { MaxAllocationBytes = 1024 };
        StbException ex = Assert.Throws<StbException>(() => limits.ValidateDimensions(32, 16, 4));
        Assert.Equal(StbErrorCode.LimitExceeded, ex.Code);
    }

    [Fact]
    public void ValidateDimensionsRejectsOverflowEvenWhenUnlimited()
    {
        StbException ex = Assert.Throws<StbException>(() =>
            StbLimits.Unlimited.ValidateDimensions(int.MaxValue, int.MaxValue, 4));
        Assert.Equal(StbErrorCode.LimitExceeded, ex.Code);
    }

    [Fact]
    public void ValidateAllocationRejectsNegativeAndOverLimit()
    {
        StbLimits limits = new() { MaxAllocationBytes = 256 };

        StbException neg = Assert.Throws<StbException>(() => limits.ValidateAllocation(-1));
        Assert.Equal(StbErrorCode.InvalidData, neg.Code);

        StbException over = Assert.Throws<StbException>(() => limits.ValidateAllocation(257));
        Assert.Equal(StbErrorCode.LimitExceeded, over.Code);

        limits.ValidateAllocation(256);
    }

    [Fact]
    public void ValidateDecompressedSizeEnforcesBudget()
    {
        StbLimits limits = new() { MaxDecompressedBytes = 512 };

        StbException neg = Assert.Throws<StbException>(() => limits.ValidateDecompressedSize(-1));
        Assert.Equal(StbErrorCode.InvalidData, neg.Code);

        StbException over = Assert.Throws<StbException>(() => limits.ValidateDecompressedSize(513));
        Assert.Equal(StbErrorCode.LimitExceeded, over.Code);

        limits.ValidateDecompressedSize(512);
    }

    [Fact]
    public void ValidateFrameCountEnforcesBudget()
    {
        StbLimits limits = new() { MaxFrames = 10 };

        StbException neg = Assert.Throws<StbException>(() => limits.ValidateFrameCount(-1));
        Assert.Equal(StbErrorCode.InvalidData, neg.Code);

        StbException over = Assert.Throws<StbException>(() => limits.ValidateFrameCount(11));
        Assert.Equal(StbErrorCode.LimitExceeded, over.Code);

        limits.ValidateFrameCount(10);
    }
}
