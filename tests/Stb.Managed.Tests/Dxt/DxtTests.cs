using Stb.Managed.Dxt;
using Stb.Managed.Oracle;
using Xunit;

namespace Stb.Managed.Tests.Dxt;

public sealed class DxtTests
{
    [Theory]
    [InlineData(false, DxtCompressionMode.Normal)]
    [InlineData(false, DxtCompressionMode.Dither)]
    [InlineData(false, DxtCompressionMode.HighQuality)]
    [InlineData(true, DxtCompressionMode.Normal)]
    [InlineData(true, DxtCompressionMode.Dither)]
    [InlineData(true, DxtCompressionMode.HighQuality)]
    public void CompressDxtBlockRandomAndEdgeBlocksMatchNativeOracleBitForBit(bool includeAlpha, DxtCompressionMode mode)
    {
        int outLen = includeAlpha ? 16 : 8;
        byte[] managed = new byte[outLen];
        byte[] native = new byte[outLen];
        byte[] block = new byte[64];

        // Edge cases: all zero, all 255, constant color with varying alpha, constant alpha with varying color,
        // narrow alpha range (dist < 8), checkerboard, smooth gradient.
        byte[][] specialBlocks = BuildSpecialRgbaBlocks();
        foreach (byte[] special in specialBlocks)
        {
            StbOracle.CompressDxtBlock(native, special, includeAlpha, (int)mode);
            StbDxt.CompressDxtBlock(managed, special, includeAlpha, mode);
            Assert.Equal(native, managed);
        }

        var rng = new Random(202608 + (includeAlpha ? 100 : 0) + (int)mode);
        for (int iter = 0; iter < 250; ++iter)
        {
            rng.NextBytes(block);
            if (iter % 10 == 0)
            {
                // Narrow alpha range (dist 0..7)
                byte baseA = (byte)rng.Next(0, 248);
                int spread = iter % 8;
                for (int p = 0; p < 16; ++p)
                {
                    block[p * 4 + 3] = (byte)(baseA + rng.Next(0, spread + 1));
                }
            }

            StbOracle.CompressDxtBlock(native, block, includeAlpha, (int)mode);
            StbDxt.CompressDxtBlock(managed, block, includeAlpha, mode);
            Assert.Equal(native, managed);
        }
    }

    [Fact]
    public void CompressBc4AndBc5BlocksMatchNativeOracleBitForBit()
    {
        byte[] managedBc4 = new byte[8];
        byte[] nativeBc4 = new byte[8];
        byte[] blockR = new byte[16];

        // Constant blocks 0..255 (every constant value must emit exact stb_dxt 0x249249249249 indices).
        for (int v = 0; v < 256; v += 17)
        {
            Array.Fill(blockR, (byte)v);
            StbOracle.CompressBc4Block(nativeBc4, blockR);
            StbDxt.CompressBc4Block(managedBc4, blockR);
            Assert.Equal(nativeBc4, managedBc4);
        }

        // Narrow spreads (dist 1..10) and random blocks.
        var rng = new Random(7744);
        for (int iter = 0; iter < 300; ++iter)
        {
            if (iter < 100)
            {
                byte baseV = (byte)rng.Next(0, 240);
                int range = (iter % 12) + 1;
                for (int i = 0; i < 16; ++i)
                {
                    blockR[i] = (byte)(baseV + rng.Next(0, range));
                }
            }
            else
            {
                rng.NextBytes(blockR);
            }

            StbOracle.CompressBc4Block(nativeBc4, blockR);
            StbDxt.CompressBc4Block(managedBc4, blockR);
            Assert.Equal(nativeBc4, managedBc4);
        }

        byte[] managedBc5 = new byte[16];
        byte[] nativeBc5 = new byte[16];
        byte[] blockRg = new byte[32];

        for (int iter = 0; iter < 200; ++iter)
        {
            rng.NextBytes(blockRg);
            if (iter == 0)
            {
                Array.Fill(blockRg, (byte)128);
            }
            else if (iter == 1)
            {
                for (int i = 0; i < 16; ++i)
                {
                    blockRg[i * 2] = 42;
                    blockRg[i * 2 + 1] = (byte)(i * 17);
                }
            }

            StbOracle.CompressBc5Block(nativeBc5, blockRg);
            StbDxt.CompressBc5Block(managedBc5, blockRg);
            Assert.Equal(nativeBc5, managedBc5);
        }
    }

    [Theory]
    [InlineData(1, 1, DxtFormat.Bc1, DxtCompressionMode.Normal)]
    [InlineData(3, 5, DxtFormat.Bc3, DxtCompressionMode.Normal)]
    [InlineData(13, 19, DxtFormat.Bc1, DxtCompressionMode.HighQuality)]
    [InlineData(64, 64, DxtFormat.Bc1, DxtCompressionMode.Normal)]
    [InlineData(64, 64, DxtFormat.Bc3, DxtCompressionMode.HighQuality)]
    [InlineData(65, 63, DxtFormat.Bc4, DxtCompressionMode.Normal)]
    [InlineData(33, 47, DxtFormat.Bc5, DxtCompressionMode.Normal)]
    public void CompressImageAlignedAndUnalignedDimensionsMatchNativeOracleBitForBit(
        int width,
        int height,
        DxtFormat format,
        DxtCompressionMode mode)
    {
        int channels = format switch
        {
            DxtFormat.Bc1 or DxtFormat.Bc3 => 4,
            DxtFormat.Bc4 => 1,
            DxtFormat.Bc5 => 2,
            _ => throw new ArgumentOutOfRangeException(nameof(format)),
        };

        byte[] src = new byte[width * height * channels];
        var rng = new Random(width * 997 + height * 31 + (int)format * 7 + (int)mode);
        rng.NextBytes(src);

        int outBytes = (int)StbDxt.GetCompressedSize(width, height, format);
        byte[] native = new byte[outBytes];
        byte[] managed = new byte[outBytes];

        switch (format)
        {
            case DxtFormat.Bc1:
                StbOracle.CompressDxtImage(src, width, height, native, alpha: false, (int)mode);
                break;
            case DxtFormat.Bc3:
                StbOracle.CompressDxtImage(src, width, height, native, alpha: true, (int)mode);
                break;
            case DxtFormat.Bc4:
                StbOracle.CompressBc4Image(src, width, height, native);
                break;
            case DxtFormat.Bc5:
                StbOracle.CompressBc5Image(src, width, height, native);
                break;
        }

        StbDxt.CompressImage(src, width, height, 0, managed, format, mode);
        Assert.Equal(native, managed);

        // Also verify padded stride produces identical output.
        int paddedStride = width * channels + 13;
        byte[] paddedSrc = new byte[height * paddedStride];
        Array.Fill(paddedSrc, (byte)0xCC);
        for (int y = 0; y < height; ++y)
        {
            src.AsSpan(y * width * channels, width * channels).CopyTo(paddedSrc.AsSpan(y * paddedStride));
        }

        byte[] managedPadded = new byte[outBytes];
        StbDxt.CompressImage(paddedSrc, width, height, paddedStride, managedPadded, format, mode);
        Assert.Equal(native, managedPadded);
    }

    [Theory]
    [InlineData(DxtFormat.Bc1)]
    [InlineData(DxtFormat.Bc3)]
    [InlineData(DxtFormat.Bc4)]
    [InlineData(DxtFormat.Bc5)]
    public void CompressImageParallelMatchesSingleThreadedBitForBit(DxtFormat format)
    {
        const int width = 256;
        const int height = 256;
        int channels = format is DxtFormat.Bc1 or DxtFormat.Bc3 ? 4 : format == DxtFormat.Bc4 ? 1 : 2;
        byte[] src = new byte[width * height * channels];
        new Random(4242 + (int)format).NextBytes(src);

        byte[] single = StbDxt.CompressImageToArray(
            src,
            width,
            height,
            format,
            options: new StbOptions { MaxDegreeOfParallelism = 1 });

        byte[] parallel = StbDxt.CompressImageToArray(
            src,
            width,
            height,
            format,
            options: new StbOptions { MaxDegreeOfParallelism = 0 });

        Assert.Equal(single, parallel);
    }

    [Fact]
    public void DxtInvalidArgumentsAndLimitsThrowExpectedExceptions()
    {
        Assert.Throws<ArgumentException>(() => StbDxt.CompressBc1Block(new byte[7], new byte[64]));
        Assert.Throws<ArgumentException>(() => StbDxt.CompressBc1Block(new byte[8], new byte[63]));
        Assert.Throws<ArgumentException>(() => StbDxt.CompressBc3Block(new byte[15], new byte[64]));
        Assert.Throws<ArgumentException>(() => StbDxt.CompressBc4Block(new byte[7], new byte[16]));
        Assert.Throws<ArgumentException>(() => StbDxt.CompressBc4Block(new byte[8], new byte[15]));
        Assert.Throws<ArgumentException>(() => StbDxt.CompressBc5Block(new byte[15], new byte[32]));
        Assert.Throws<ArgumentException>(() => StbDxt.CompressBc5Block(new byte[16], new byte[31]));

        Assert.Throws<ArgumentOutOfRangeException>(() => StbDxt.GetCompressedSize(0, 4, DxtFormat.Bc1));
        Assert.Throws<ArgumentOutOfRangeException>(() => StbDxt.GetCompressedSize(4, -4, DxtFormat.Bc1));
        Assert.Throws<ArgumentOutOfRangeException>(() => StbDxt.GetCompressedSize(4, 4, (DxtFormat)99));

        Assert.Throws<ArgumentException>(() => StbDxt.CompressImage(new byte[10], 4, 4, 0, new byte[8], DxtFormat.Bc1));
        Assert.Throws<ArgumentException>(() => StbDxt.CompressImage(new byte[64], 4, 4, 0, new byte[4], DxtFormat.Bc1));
        Assert.Throws<ArgumentOutOfRangeException>(() => StbDxt.CompressImage(new byte[64], 4, 4, 8, new byte[8], DxtFormat.Bc1));

        var tinyLimits = new StbOptions { Limits = new StbLimits { MaxAllocationBytes = 32 } };
        StbException ex = Assert.Throws<StbException>(() =>
            StbDxt.CompressImageToArray(new byte[64 * 64 * 4], 64, 64, DxtFormat.Bc1, options: tinyLimits));
        Assert.Equal(StbErrorCode.LimitExceeded, ex.Code);
    }

    private static byte[][] BuildSpecialRgbaBlocks()
    {
        byte[] allZero = new byte[64];
        byte[] allMax = new byte[64];
        Array.Fill(allMax, (byte)255);

        byte[] constRgbVaryingAlpha = new byte[64];
        for (int p = 0; p < 16; ++p)
        {
            constRgbVaryingAlpha[p * 4] = 123;
            constRgbVaryingAlpha[p * 4 + 1] = 45;
            constRgbVaryingAlpha[p * 4 + 2] = 200;
            constRgbVaryingAlpha[p * 4 + 3] = (byte)(p * 17);
        }

        byte[] constAlphaVaryingRgb = new byte[64];
        for (int p = 0; p < 16; ++p)
        {
            constAlphaVaryingRgb[p * 4] = (byte)(p * 16);
            constAlphaVaryingRgb[p * 4 + 1] = (byte)(255 - p * 16);
            constAlphaVaryingRgb[p * 4 + 2] = (byte)((p * 37) & 255);
            constAlphaVaryingRgb[p * 4 + 3] = 180;
        }

        byte[] checker = new byte[64];
        for (int p = 0; p < 16; ++p)
        {
            byte c = ((p + (p >> 2)) & 1) == 0 ? (byte)10 : (byte)245;
            checker[p * 4] = c;
            checker[p * 4 + 1] = (byte)(255 - c);
            checker[p * 4 + 2] = 128;
            checker[p * 4 + 3] = c;
        }

        return [allZero, allMax, constRgbVaryingAlpha, constAlphaVaryingRgb, checker];
    }
}
