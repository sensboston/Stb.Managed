using Xunit;

namespace Stb.Managed.Tests.Core;

public sealed class ImageBufferTests
{
    [Fact]
    public void AllocateByteBufferComputesStrideAndRows()
    {
        ImageBuffer<byte> image = ImageBuffer.Allocate<byte>(
            width: 5,
            height: 3,
            components: ColorComponents.RedGreenBlueAlpha,
            sourceComponents: ColorComponents.RedGreenBlue,
            zeroInitialize: true);

        Assert.Equal(5, image.Width);
        Assert.Equal(3, image.Height);
        Assert.Equal(ColorComponents.RedGreenBlueAlpha, image.Components);
        Assert.Equal(ColorComponents.RedGreenBlue, image.SourceComponents);
        Assert.Equal(20, image.RowStride);
        Assert.Equal(60, image.SampleCount);
        Assert.Equal(60, image.AsSpan().Length);
        Assert.Equal(60, image.AsReadOnlySpan().Length);

        image.GetRow(1).Fill(42);
        Assert.Equal(0, image.GetRow(0)[0]);
        Assert.Equal(42, image.AsReadOnlySpan()[20]);
        Assert.Equal(42, image.AsReadOnlySpan()[39]);
        Assert.Equal(0, image.GetRow(2)[0]);
    }

    [Fact]
    public void AllocateSupportsUInt16AndFloat()
    {
        ImageBuffer<ushort> img16 = ImageBuffer.Allocate<ushort>(
            4, 2, ColorComponents.GreyAlpha, ColorComponents.GreyAlpha, zeroInitialize: true);
        Assert.Equal(16, img16.SampleCount);

        ImageBuffer<float> imgHdr = ImageBuffer.Allocate<float>(
            4, 2, ColorComponents.RedGreenBlue, ColorComponents.RedGreenBlue, zeroInitialize: true);
        Assert.Equal(24, imgHdr.SampleCount);
    }

    [Fact]
    public void AllocateEnforcesLimits()
    {
        StbLimits tiny = new() { MaxAllocationBytes = 32 };
        StbException ex = Assert.Throws<StbException>(() =>
            ImageBuffer.Allocate<ushort>(4, 4, ColorComponents.RedGreenBlueAlpha, ColorComponents.RedGreenBlueAlpha, tiny));
        Assert.Equal(StbErrorCode.LimitExceeded, ex.Code);
    }

    [Fact]
    public void AllocateRejectsDefaultComponents()
    {
        StbException ex = Assert.Throws<StbException>(() =>
            ImageBuffer.Allocate<byte>(4, 4, ColorComponents.Default, ColorComponents.RedGreenBlue));
        Assert.Equal(StbErrorCode.InvalidArgument, ex.Code);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(3)]
    [InlineData(100)]
    public void GetRowOutOfRangeThrows(int row)
    {
        ImageBuffer<byte> image = ImageBuffer.Allocate<byte>(
            2, 3, ColorComponents.Grey, ColorComponents.Grey);
        Assert.Throws<ArgumentOutOfRangeException>(() => image.GetRow(row));
    }
}
