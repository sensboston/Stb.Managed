using Stb.Managed.Image.Internal;
using Xunit;

namespace Stb.Managed.Tests.Image;

public sealed class JpegKernelTests
{
    [Fact]
    public void IdctSimdAndTwoBlockMatchesScalarBitExact()
    {
        var rng = new Random(0x57B1DC7);
        short[] block0 = new short[64];
        short[] block1 = new short[64];
        byte[] ref0 = new byte[128];
        byte[] ref1 = new byte[128];
        byte[] got0 = new byte[128];
        byte[] got1 = new byte[128];

        for (int trial = 0; trial < 500; trial++)
        {
            // Cover DC-only, sparse, dense, and wide-range JPEG coefficients
            int mode = trial % 4;
            for (int i = 0; i < 64; i++)
            {
                if (mode == 0)
                {
                    block0[i] = i == 0 ? (short)rng.Next(-2048, 2048) : (short)0;
                    block1[i] = i == 0 ? (short)rng.Next(-2048, 2048) : (short)0;
                }
                else if (mode == 1)
                {
                    block0[i] = i < 16 ? (short)rng.Next(-512, 512) : (short)0;
                    block1[i] = i < 16 ? (short)rng.Next(-512, 512) : (short)0;
                }
                else if (mode == 2)
                {
                    block0[i] = (short)rng.Next(-512, 512);
                    block1[i] = (short)rng.Next(-512, 512);
                }
                else
                {
                    // Realistic decaying-energy JPEG DCT spectrum (DC up to +/-1024, high freq smaller)
                    int u = i & 7;
                    int v = i >> 3;
                    int amp = 1024 / (1 + u + v);
                    block0[i] = (short)rng.Next(-amp, amp);
                    block1[i] = (short)rng.Next(-amp, amp);
                }
            }

            const int stride = 16;
            const int off0 = 0;
            const int off1 = 8;

            ref0.AsSpan().Clear();
            ref1.AsSpan().Clear();
            got0.AsSpan().Clear();
            got1.AsSpan().Clear();

            JpegIdct.IdctBlockScalar(ref0, off0, stride, block0);
            JpegIdct.IdctBlockScalar(ref1, off1, stride, block1);

            // Single-block SIMD
            JpegIdct.IdctBlock(got0, off0, stride, block0);
            Assert.Equal(ref0, got0);

            // Two-block AVX2 / SIMD
            got0.AsSpan().Clear();
            JpegIdct.IdctTwoBlocks(got0, off0, stride, block0, got1, off1, stride, block1);
            Assert.Equal(ref0, got0);
            Assert.Equal(ref1, got1);

            // DC-only fast fill matches scalar IDCT bit-for-bit
            if (mode == 0)
            {
                got0.AsSpan().Clear();
                JpegIdct.FillDcBlock(got0, off0, stride, block0[0]);
                Assert.Equal(ref0, got0);
            }
        }
    }

    [Fact]
    public void DequantAndIdctFusedMatchesScalarBitExact()
    {
        var rng = new Random(0x57B9012);
        short[] c0 = new short[64];
        short[] c1 = new short[64];
        ushort[] dq = new ushort[64];
        short[] d0 = new short[64];
        short[] d1 = new short[64];
        byte[] ref0 = new byte[64];
        byte[] ref1 = new byte[64];
        byte[] got0 = new byte[64];
        byte[] got1 = new byte[64];

        for (int trial = 0; trial < 200; trial++)
        {
            for (int i = 0; i < 64; i++)
            {
                dq[i] = (ushort)rng.Next(1, 17);
                c0[i] = (short)rng.Next(-32, 32);
                c1[i] = (short)rng.Next(-32, 32);
                d0[i] = unchecked((short)(c0[i] * dq[i]));
                d1[i] = unchecked((short)(c1[i] * dq[i]));
            }

            JpegIdct.IdctBlockScalar(ref0, 0, 8, d0);
            JpegIdct.IdctBlockScalar(ref1, 0, 8, d1);

            JpegIdct.DequantAndIdctBlock(got0, 0, 8, c0, dq);
            Assert.Equal(ref0, got0);

            got0.AsSpan().Clear();
            got1.AsSpan().Clear();
            JpegIdct.DequantAndIdctTwoBlocks(got0, 0, 8, c0, got1, 0, 8, c1, dq);
            Assert.Equal(ref0, got0);
            Assert.Equal(ref1, got1);
        }
    }

    [Fact]
    public void ResampleRowsSimdMatchesScalarBitExactForAllWidths()
    {
        var rng = new Random(0x57B4E5);
        byte[] near = new byte[140];
        byte[] far = new byte[140];
        rng.NextBytes(near);
        rng.NextBytes(far);

        for (int w = 1; w <= 135; w++)
        {
            // V2
            byte[] v2Ref = new byte[w];
            byte[] v2Got = new byte[w];
            JpegColor.ResampleRowV2Scalar(v2Ref, near.AsSpan(0, w), far.AsSpan(0, w), w);
            JpegColor.ResampleRowV2(v2Got, near.AsSpan(0, w), far.AsSpan(0, w), w);
            Assert.Equal(v2Ref, v2Got);

            // H2
            byte[] h2Ref = new byte[w * 2];
            byte[] h2Got = new byte[w * 2];
            JpegColor.ResampleRowH2Scalar(h2Ref, near.AsSpan(0, w), w);
            JpegColor.ResampleRowH2(h2Got, near.AsSpan(0, w), w);
            Assert.Equal(h2Ref, h2Got);

            // HV2
            byte[] hv2Ref = new byte[w * 2];
            byte[] hv2Got = new byte[w * 2];
            JpegColor.ResampleRowHv2Scalar(hv2Ref, near.AsSpan(0, w), far.AsSpan(0, w), w);
            JpegColor.ResampleRowHv2(hv2Got, near.AsSpan(0, w), far.AsSpan(0, w), w);
            Assert.Equal(hv2Ref, hv2Got);
        }
    }

    [Theory]
    [InlineData(3)]
    [InlineData(4)]
    public void YCbCrToRgbRowSimdMatchesScalarBitExactForAllWidths(int step)
    {
        var rng = new Random(0x57BC010 + step);
        byte[] y = new byte[140];
        byte[] cb = new byte[140];
        byte[] cr = new byte[140];
        rng.NextBytes(y);
        rng.NextBytes(cb);
        rng.NextBytes(cr);

        // Include boundary extremes (0, 128, 255) across first 24 pixels
        for (int i = 0; i < 24; i++)
        {
            y[i] = (i % 3) switch { 0 => 0, 1 => 128, _ => 255 };
            cb[i] = ((i / 3) % 3) switch { 0 => 0, 1 => 128, _ => 255 };
            cr[i] = ((i / 9) % 3) switch { 0 => 0, 1 => 128, _ => 255 };
        }

        for (int w = 1; w <= 135; w++)
        {
            byte[] expected = new byte[w * step];
            byte[] actual = new byte[w * step];

            JpegColor.YCbCrToRgbRowScalar(expected, y.AsSpan(0, w), cb.AsSpan(0, w), cr.AsSpan(0, w), w, step);
            JpegColor.YCbCrToRgbRow(actual, y.AsSpan(0, w), cb.AsSpan(0, w), cr.AsSpan(0, w), w, step);

            Assert.Equal(expected, actual);
        }
    }
}
