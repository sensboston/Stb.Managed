using System;
using Stb.Managed.ImageWrite.Internal;
using Stb.Managed.Oracle;
using Xunit;

namespace Stb.Managed.Tests.ImageWrite;

public sealed class ImageWriteSimdAndParallelTests
{
    [Fact]
    public void PixelWriteKernelsMatchScalarAcrossAllTailLengths()
    {
        int[] widths = [1, 2, 3, 4, 5, 7, 8, 9, 15, 16, 17, 23, 24, 31, 32, 33, 47, 48, 49, 63, 64, 65, 257];
        foreach (int w in widths)
        {
            byte[] rgba = ImageWriteTestHelpers.CreateRandomPixels(w, 1, 4, seed: w * 17 + 1);
            byte[] dstSimd4 = new byte[w * 4];
            byte[] dstScal4 = new byte[w * 4];
            PixelWriteKernels.Rgba32ToBgra32(rgba, dstSimd4, w);
            PixelWriteKernels.Rgba32ToBgra32Scalar(rgba, dstScal4, w);
            ImageWriteTestHelpers.AssertBytesEqual(dstScal4, dstSimd4, $"Rgba32ToBgra32 w={w}");

            byte[] rgb = ImageWriteTestHelpers.CreateRandomPixels(w, 1, 3, seed: w * 19 + 2);
            byte[] dstSimd3 = new byte[w * 3];
            byte[] dstScal3 = new byte[w * 3];
            PixelWriteKernels.Rgb24ToBgr24(rgb, dstSimd3, w);
            PixelWriteKernels.Rgb24ToBgr24Scalar(rgb, dstScal3, w);
            ImageWriteTestHelpers.AssertBytesEqual(dstScal3, dstSimd3, $"Rgb24ToBgr24 w={w}");

            byte[] grey = ImageWriteTestHelpers.CreateRandomPixels(w, 1, 1, seed: w * 23 + 3);
            byte[] dstSimd1 = new byte[w * 3];
            byte[] dstScal1 = new byte[w * 3];
            PixelWriteKernels.Grey8ToBgr24(grey, dstSimd1, w);
            PixelWriteKernels.Grey8ToBgr24Scalar(grey, dstScal1, w);
            ImageWriteTestHelpers.AssertBytesEqual(dstScal1, dstSimd1, $"Grey8ToBgr24 w={w}");

            byte[] ga = ImageWriteTestHelpers.CreateRandomPixels(w, 1, 2, seed: w * 29 + 4);
            byte[] dstSimd2 = new byte[w * 3];
            byte[] dstScal2 = new byte[w * 3];
            PixelWriteKernels.GreyAlpha16ToBgr24(ga, dstSimd2, w);
            PixelWriteKernels.GreyAlpha16ToBgr24Scalar(ga, dstScal2, w);
            ImageWriteTestHelpers.AssertBytesEqual(dstScal2, dstSimd2, $"GreyAlpha16ToBgr24 w={w}");
        }
    }

    [Fact]
    public void PngFilterKernelsAndSadMatchScalarAcrossFiltersBppAndEdgeValues()
    {
        int[] widths = [1, 2, 3, 7, 15, 16, 17, 31, 32, 33, 63, 64, 65, 131];
        byte[] edgePalette = [0x00, 0x01, 0x7E, 0x7F, 0x80, 0x81, 0xFE, 0xFF];

        foreach (int bpp in new[] { 1, 2, 3, 4 })
        {
            foreach (int w in widths)
            {
                int rowBytes = w * bpp;
                byte[] cur = ImageWriteTestHelpers.CreateRandomPixels(w, 1, bpp, seed: (w * 31) + bpp);
                byte[] prev = ImageWriteTestHelpers.CreateRandomPixels(w, 1, bpp, seed: (w * 37) + bpp + 99);

                // Inject edge values (including 0x80 == sbyte.MinValue and 0xFF)
                for (int i = 0; i < Math.Min(rowBytes, 16); i++)
                {
                    cur[i] = edgePalette[(i + bpp) % edgePalette.Length];
                    prev[i] = edgePalette[(i * 3 + 1) % edgePalette.Length];
                }

                byte[] simdDst = new byte[rowBytes];
                byte[] scalarDst = new byte[rowBytes];

                foreach (bool isFirstRow in new[] { true, false })
                {
                    for (int filter = 0; filter < 5; filter++)
                    {
                        simdDst.AsSpan().Fill(0xCC);
                        scalarDst.AsSpan().Fill(0xDD);

                        int simdSad = PngFilterKernels.EncodeLineAndSad(cur, prev, isFirstRow, bpp, filter, simdDst);
                        PngEncoder.EncodeLineScalar(cur, prev, isFirstRow, bpp, filter, scalarDst);
                        int scalarSad = PngEncoder.ComputeSignedAbsSumScalar(scalarDst);

                        ImageWriteTestHelpers.AssertBytesEqual(
                            scalarDst,
                            simdDst,
                            $"PngFilter filter={filter} first={isFirstRow} bpp={bpp} w={w}");
                        Assert.Equal(scalarSad, simdSad);
                    }
                }
            }
        }
    }

    [Fact]
    public void HdrLinearToRgbePlanarSimdMatchesScalarIncludingEdgeValuesAndNaNs()
    {
        int[] widths = [1, 3, 4, 5, 7, 8, 9, 15, 16, 17, 33, 129];
        float[] specials =
        [
            0.0f, -0.0f, -5.0f, 1e-38f, 1e-32f,
            BitConverter.Int32BitsToSingle(BitConverter.SingleToInt32Bits(1e-32f) + 1),
            0.25f, 1.0f, 123.456f, float.MaxValue, float.PositiveInfinity, float.NegativeInfinity, float.NaN,
        ];

        foreach (int comp in new[] { 1, 2, 3, 4 })
        {
            foreach (int w in widths)
            {
                float[] row = new float[w * comp];
                uint rng = (uint)((w * 101) + (comp * 7) + 1);
                for (int i = 0; i < row.Length; i++)
                {
                    rng = (rng * 1664525u) + 1013904223u;
                    row[i] = ((rng & 0xFFFFu) / 4096.0f) - 1.0f;
                }

                // Inject edge values (including NaNs in various channels & positions)
                for (int i = 0; i < Math.Min(row.Length, specials.Length); i++)
                {
                    row[i] = specials[i];
                }

                if (w >= 12 && comp >= 3)
                {
                    // Place NaN specifically in R, G, and B channels separately in lanes 8..11
                    row[(8 * comp) + 0] = float.NaN;
                    row[(8 * comp) + 1] = 1.5f;
                    row[(8 * comp) + 2] = 0.5f;

                    row[(9 * comp) + 0] = 1.5f;
                    row[(9 * comp) + 1] = float.NaN;
                    row[(9 * comp) + 2] = 0.5f;

                    row[(10 * comp) + 0] = 1.5f;
                    row[(10 * comp) + 1] = 0.5f;
                    row[(10 * comp) + 2] = float.NaN;
                }

                byte[] sR = new byte[w], sG = new byte[w], sB = new byte[w], sE = new byte[w];
                byte[] vR = new byte[w], vG = new byte[w], vB = new byte[w], vE = new byte[w];

                HdrEncoder.ConvertScanlineToRgbePlanarScalar(row, w, comp, sR, sG, sB, sE);
                HdrEncoder.ConvertScanlineToRgbePlanar(row, w, comp, vR, vG, vB, vE);

                ImageWriteTestHelpers.AssertBytesEqual(sR, vR, $"HDR R comp={comp} w={w}");
                ImageWriteTestHelpers.AssertBytesEqual(sG, vG, $"HDR G comp={comp} w={w}");
                ImageWriteTestHelpers.AssertBytesEqual(sB, vB, $"HDR B comp={comp} w={w}");
                ImageWriteTestHelpers.AssertBytesEqual(sE, vE, $"HDR E comp={comp} w={w}");
            }
        }
    }

    [Fact]
    public void JpegDctAndQuantizeAndSubsampleMatchScalarBitForBit()
    {
        float[] fdtbl = new float[64];
        for (int i = 0; i < 64; i++)
        {
            fdtbl[i] = 1.0f / (8.0f * (1 + (i % 17)));
        }

        uint rng = 0x12345678u;
        float[] src16 = new float[256];
        for (int i = 0; i < 256; i++)
        {
            rng = (rng * 1664525u) + 1013904223u;
            src16[i] = ((rng & 0xFFu) - 128.0f) * 0.99f;
        }

        // Verify Subsample2x2 exact bit-for-bit float parity
        float[] subSimd = new float[64];
        float[] subScal = new float[64];
        JpegEncoder.Subsample2x2(src16, subSimd);
        int pos = 0;
        for (int yy = 0; yy < 8; yy++)
        {
            int rowBase = yy * 32;
            for (int xx = 0; xx < 8; xx++, pos++)
            {
                int j = rowBase + (xx * 2);
                subScal[pos] = (((src16[j] + src16[j + 1]) + src16[j + 16]) + src16[j + 17]) * 0.25f;
            }
        }

        for (int i = 0; i < 64; i++)
        {
            Assert.Equal(BitConverter.SingleToInt32Bits(subScal[i]), BitConverter.SingleToInt32Bits(subSimd[i]));
        }

        // Verify DctAndQuantize for both stride=8 and stride=16 (including extreme blocks)
        foreach (int stride in new[] { 8, 16 })
        {
            float[] blkSimd = (float[])src16.Clone();
            float[] blkScal = (float[])src16.Clone();
            short[] duSimd = new short[64];
            short[] duScal = new short[64];

            int endSimd = JpegEncoder.DctAndQuantize(blkSimd, stride, fdtbl, duSimd);
            int endScal = JpegEncoder.DctAndQuantizeScalar(blkScal, stride, fdtbl, duScal);

            Assert.Equal(endScal, endSimd);
            Assert.Equal(duScal, duSimd);
        }
    }

    [Theory]
    [InlineData(ColorComponents.Grey)]
    [InlineData(ColorComponents.GreyAlpha)]
    [InlineData(ColorComponents.RedGreenBlue)]
    [InlineData(ColorComponents.RedGreenBlueAlpha)]
    public void PngParallelFilteringMatchesSingleThreadedAndOracleBitForBit(ColorComponents comp)
    {
        const int width = 259;
        const int height = 257;
        byte[] pixels = ImageWriteTestHelpers.CreateGradientPixels(width, height, (int)comp, seed: (int)comp * 71);
        ImageBuffer<byte> buffer = new(width, height, comp, comp, pixels);

        byte[] oracle = StbOracle.WritePng(width, height, (int)comp, pixels, strideBytes: 0, compressionLevel: 8, forceFilter: -1, flipVertically: false);

        ImageWriteOptions stCompat = new()
        {
            PngCompressionEngine = PngCompressionEngine.StbCompatible,
            Options = new StbOptions { MaxDegreeOfParallelism = 1 },
        };
        ImageWriteOptions parAutoCompat = stCompat with
        {
            Options = new StbOptions { MaxDegreeOfParallelism = 0 },
        };
        ImageWriteOptions par4Compat = stCompat with
        {
            Options = new StbOptions { MaxDegreeOfParallelism = 4 },
        };

        byte[] single = StbImageWrite.WritePng(pixels, width, height, comp, options: stCompat);
        byte[] parAutoSpan = StbImageWrite.WritePng(pixels, width, height, comp, options: parAutoCompat);
        byte[] par4Buf = StbImageWrite.WritePng(buffer, options: par4Compat);

        ImageWriteTestHelpers.AssertBytesEqual(oracle, single, $"PNG StbCompatible 1T {comp}");
        ImageWriteTestHelpers.AssertBytesEqual(oracle, parAutoSpan, $"PNG StbCompatible Auto-Par span {comp}");
        ImageWriteTestHelpers.AssertBytesEqual(oracle, par4Buf, $"PNG StbCompatible Par4 buffer {comp}");

        // Also check ZLib engine with flip + stride determinism between 1T and Par4
        byte[] strided = ImageWriteTestHelpers.AddRowPadding(pixels, width, height, (int)comp, 13);
        int stride = (width * (int)comp) + 13;
        ImageWriteOptions zlib1T = new()
        {
            PngCompressionEngine = PngCompressionEngine.ZLib,
            FlipVertically = true,
            Options = new StbOptions { MaxDegreeOfParallelism = 1 },
        };
        ImageWriteOptions zlibPar = zlib1T with
        {
            Options = new StbOptions { MaxDegreeOfParallelism = 4 },
        };

        byte[] zSingle = StbImageWrite.WritePng(strided, width, height, comp, strideBytes: stride, options: zlib1T);
        byte[] zPar = StbImageWrite.WritePng(strided, width, height, comp, strideBytes: stride, options: zlibPar);
        ImageWriteTestHelpers.AssertBytesEqual(zSingle, zPar, $"PNG ZLib flip+stride 1T vs Par4 {comp}");
    }

    [Theory]
    [InlineData(ColorComponents.Grey, 85, false)]
    [InlineData(ColorComponents.GreyAlpha, 90, true)]
    [InlineData(ColorComponents.RedGreenBlue, 85, false)]
    [InlineData(ColorComponents.RedGreenBlueAlpha, 90, false)]
    [InlineData(ColorComponents.RedGreenBlueAlpha, 95, true)]
    public void JpegParallelEncodingMatchesSingleThreadedAndOracleBitForBit(
        ColorComponents comp,
        int quality,
        bool flip)
    {
        const int width = 261;
        const int height = 259;
        byte[] pixels = ImageWriteTestHelpers.CreateGradientPixels(width, height, (int)comp, seed: ((int)comp * 131) + quality);
        ImageBuffer<byte> buffer = new(width, height, comp, comp, pixels);

        byte[] oracle = StbOracle.WriteJpg(width, height, (int)comp, pixels, quality, flip);

        ImageWriteOptions stOpts = new()
        {
            FlipVertically = flip,
            Options = new StbOptions { MaxDegreeOfParallelism = 1 },
        };
        ImageWriteOptions parAutoOpts = stOpts with
        {
            Options = new StbOptions { MaxDegreeOfParallelism = 0 },
        };
        ImageWriteOptions par4Opts = stOpts with
        {
            Options = new StbOptions { MaxDegreeOfParallelism = 4 },
        };

        byte[] single = StbImageWrite.WriteJpeg(pixels, width, height, comp, quality, options: stOpts);
        byte[] parAutoSpan = StbImageWrite.WriteJpeg(pixels, width, height, comp, quality, options: parAutoOpts);
        byte[] par4Buf = StbImageWrite.WriteJpeg(buffer, quality, options: par4Opts);

        ImageWriteTestHelpers.AssertBytesEqual(oracle, single, $"JPEG 1T {comp} q={quality} flip={flip}");
        ImageWriteTestHelpers.AssertBytesEqual(oracle, parAutoSpan, $"JPEG Auto-Par span {comp} q={quality} flip={flip}");
        ImageWriteTestHelpers.AssertBytesEqual(oracle, par4Buf, $"JPEG Par4 buf {comp} q={quality} flip={flip}");

        // Strided parallel check
        byte[] strided = ImageWriteTestHelpers.AddRowPadding(pixels, width, height, (int)comp, 11);
        int stride = (width * (int)comp) + 11;
        byte[] parStrided = StbImageWrite.WriteJpeg(strided, width, height, comp, quality, strideBytes: stride, options: par4Opts);
        ImageWriteTestHelpers.AssertBytesEqual(oracle, parStrided, $"JPEG Par4 strided {comp} q={quality}");
    }
}
