using System;
using System.IO;
using Xunit;

namespace Stb.Managed.Tests.ImageWrite;

public sealed class ImageWriteValidationTests
{
    [Fact]
    public void InvalidComponentsThrowStbException()
    {
        byte[] buf = new byte[16];
        float[] fbuf = new float[16];

        foreach (ColorComponents bad in new[] { ColorComponents.Default, (ColorComponents)5, (ColorComponents)(-1) })
        {
            Assert.Equal(StbErrorCode.InvalidArgument, Assert.Throws<StbException>(() => StbImageWrite.WritePng(buf, 2, 2, bad)).Code);
            Assert.Equal(StbErrorCode.InvalidArgument, Assert.Throws<StbException>(() => StbImageWrite.WriteBmp(buf, 2, 2, bad)).Code);
            Assert.Equal(StbErrorCode.InvalidArgument, Assert.Throws<StbException>(() => StbImageWrite.WriteTga(buf, 2, 2, bad)).Code);
            Assert.Equal(StbErrorCode.InvalidArgument, Assert.Throws<StbException>(() => StbImageWrite.WriteJpeg(buf, 2, 2, bad)).Code);
            Assert.Equal(StbErrorCode.InvalidArgument, Assert.Throws<StbException>(() => StbImageWrite.WriteHdr(fbuf, 2, 2, bad)).Code);
        }
    }

    [Theory]
    [InlineData(0, 4)]
    [InlineData(4, 0)]
    [InlineData(-1, 4)]
    [InlineData(4, -2)]
    public void NonPositiveDimensionsThrowStbException(int width, int height)
    {
        byte[] buf = new byte[64];
        float[] fbuf = new float[64];

        Assert.Equal(StbErrorCode.InvalidArgument, Assert.Throws<StbException>(() => StbImageWrite.WritePng(buf, width, height, ColorComponents.RedGreenBlue)).Code);
        Assert.Equal(StbErrorCode.InvalidArgument, Assert.Throws<StbException>(() => StbImageWrite.WriteBmp(buf, width, height, ColorComponents.RedGreenBlue)).Code);
        Assert.Equal(StbErrorCode.InvalidArgument, Assert.Throws<StbException>(() => StbImageWrite.WriteTga(buf, width, height, ColorComponents.RedGreenBlue)).Code);
        Assert.Equal(StbErrorCode.InvalidArgument, Assert.Throws<StbException>(() => StbImageWrite.WriteJpeg(buf, width, height, ColorComponents.RedGreenBlue)).Code);
        Assert.Equal(StbErrorCode.InvalidArgument, Assert.Throws<StbException>(() => StbImageWrite.WriteHdr(fbuf, width, height, ColorComponents.RedGreenBlue)).Code);
    }

    [Fact]
    public void ShortBufferAndInvalidStrideThrowStbException()
    {
        byte[] buf = new byte[10];
        float[] fbuf = new float[10];

        Assert.Equal(StbErrorCode.InvalidArgument, Assert.Throws<StbException>(() => StbImageWrite.WritePng(buf, 4, 4, ColorComponents.RedGreenBlue)).Code);
        Assert.Equal(StbErrorCode.InvalidArgument, Assert.Throws<StbException>(() => StbImageWrite.WriteBmp(buf, 4, 4, ColorComponents.RedGreenBlue)).Code);
        Assert.Equal(StbErrorCode.InvalidArgument, Assert.Throws<StbException>(() => StbImageWrite.WriteTga(buf, 4, 4, ColorComponents.RedGreenBlue)).Code);
        Assert.Equal(StbErrorCode.InvalidArgument, Assert.Throws<StbException>(() => StbImageWrite.WriteJpeg(buf, 4, 4, ColorComponents.RedGreenBlue)).Code);
        Assert.Equal(StbErrorCode.InvalidArgument, Assert.Throws<StbException>(() => StbImageWrite.WriteHdr(fbuf, 4, 4, ColorComponents.RedGreenBlue)).Code);

        byte[] big = new byte[256];
        float[] fbig = new float[256];
        Assert.Equal(StbErrorCode.InvalidArgument, Assert.Throws<StbException>(() => StbImageWrite.WritePng(big, 4, 4, ColorComponents.RedGreenBlue, strideBytes: -1)).Code);
        Assert.Equal(StbErrorCode.InvalidArgument, Assert.Throws<StbException>(() => StbImageWrite.WritePng(big, 4, 4, ColorComponents.RedGreenBlue, strideBytes: 11)).Code);
        Assert.Equal(StbErrorCode.InvalidArgument, Assert.Throws<StbException>(() => StbImageWrite.WriteHdr(fbig, 4, 4, ColorComponents.RedGreenBlue, strideFloats: 11)).Code);
    }

    [Fact]
    public void LimitsExceededThrowStbException()
    {
        byte[] buf = new byte[1024];
        float[] fbuf = new float[1024];
        ImageWriteOptions tinyLimits = new()
        {
            Options = new StbOptions
            {
                Limits = new StbLimits { MaxWidth = 8, MaxHeight = 8 },
            },
        };

        Assert.Equal(StbErrorCode.LimitExceeded, Assert.Throws<StbException>(() => StbImageWrite.WritePng(buf, 16, 4, ColorComponents.Grey, options: tinyLimits)).Code);
        Assert.Equal(StbErrorCode.LimitExceeded, Assert.Throws<StbException>(() => StbImageWrite.WriteBmp(buf, 16, 4, ColorComponents.Grey, options: tinyLimits)).Code);
        Assert.Equal(StbErrorCode.LimitExceeded, Assert.Throws<StbException>(() => StbImageWrite.WriteTga(buf, 16, 4, ColorComponents.Grey, options: tinyLimits)).Code);
        Assert.Equal(StbErrorCode.LimitExceeded, Assert.Throws<StbException>(() => StbImageWrite.WriteJpeg(buf, 16, 4, ColorComponents.Grey, options: tinyLimits)).Code);
        Assert.Equal(StbErrorCode.LimitExceeded, Assert.Throws<StbException>(() => StbImageWrite.WriteHdr(fbuf, 16, 4, ColorComponents.Grey, options: tinyLimits)).Code);

        // TGA and JPEG 16-bit header dimension caps (65535)
        ImageWriteOptions wideLimits = new()
        {
            Options = new StbOptions { Limits = StbLimits.Unlimited },
        };
        Assert.Equal(StbErrorCode.LimitExceeded, Assert.Throws<StbException>(() => StbImageWrite.WriteTga(buf, 65536, 1, ColorComponents.Grey, options: wideLimits)).Code);
        Assert.Equal(StbErrorCode.LimitExceeded, Assert.Throws<StbException>(() => StbImageWrite.WriteJpeg(buf, 1, 65536, ColorComponents.Grey, options: wideLimits)).Code);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-10)]
    [InlineData(101)]
    public void InvalidJpegQualityThrowsArgumentOutOfRange(int quality)
    {
        byte[] buf = new byte[16];
        Assert.Throws<ArgumentOutOfRangeException>(() => StbImageWrite.WriteJpeg(buf, 2, 2, ColorComponents.RedGreenBlueAlpha, quality: quality));
    }

    [Fact]
    public void InvalidOptionsAndNullArgsThrowExpectedExceptions()
    {
        Assert.Throws<ArgumentNullException>(() => new ImageWriteOptions { Options = null! });
        Assert.Throws<ArgumentOutOfRangeException>(() => new ImageWriteOptions { PngCompressionLevel = -2 });
        Assert.Throws<ArgumentOutOfRangeException>(() => new ImageWriteOptions { PngFilter = (PngFilterMode)5 });
        Assert.Throws<ArgumentOutOfRangeException>(() => new ImageWriteOptions { PngFilter = (PngFilterMode)(-2) });
        Assert.Throws<ArgumentOutOfRangeException>(() => new ImageWriteOptions { PngCompressionEngine = (PngCompressionEngine)99 });

        byte[] buf = new byte[16];
        float[] fbuf = new float[16];
        Assert.Throws<ArgumentNullException>(() => StbImageWrite.WritePng((Stream)null!, buf, 2, 2, ColorComponents.RedGreenBlueAlpha));
        Assert.Throws<ArgumentNullException>(() => StbImageWrite.WriteBmp((Stream)null!, buf, 2, 2, ColorComponents.RedGreenBlueAlpha));
        Assert.Throws<ArgumentNullException>(() => StbImageWrite.WriteTga((Stream)null!, buf, 2, 2, ColorComponents.RedGreenBlueAlpha));
        Assert.Throws<ArgumentNullException>(() => StbImageWrite.WriteJpeg((Stream)null!, buf, 2, 2, ColorComponents.RedGreenBlueAlpha));
        Assert.Throws<ArgumentNullException>(() => StbImageWrite.WriteHdr((Stream)null!, fbuf, 2, 2, ColorComponents.RedGreenBlueAlpha));

        Assert.Throws<ArgumentNullException>(() => StbImageWrite.WritePng((ImageBuffer<byte>)null!));
        Assert.Throws<ArgumentNullException>(() => StbImageWrite.WriteBmp((ImageBuffer<byte>)null!));
        Assert.Throws<ArgumentNullException>(() => StbImageWrite.WriteTga((ImageBuffer<byte>)null!));
        Assert.Throws<ArgumentNullException>(() => StbImageWrite.WriteJpeg((ImageBuffer<byte>)null!));
        Assert.Throws<ArgumentNullException>(() => StbImageWrite.WriteHdr((ImageBuffer<float>)null!));
    }
}
