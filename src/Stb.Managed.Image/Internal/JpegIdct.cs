using System.Buffers.Binary;
using System.Runtime.CompilerServices;
#if NET8_0_OR_GREATER
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.Arm;
using System.Runtime.Intrinsics.X86;
#endif

namespace Stb.Managed.Image.Internal;

/// <summary>
/// 8x8 integer inverse discrete cosine transform (IDCT) matching <c>stb_image.h</c> bit-for-bit
/// across AVX2 (2 blocks at once), SSE2, Arm64 AdvSimd, and scalar paths.
/// </summary>
internal static class JpegIdct
{
    // Exact integer values of stb_image.h's stbi__f2f(x) = (int)((x) * 4096 + 0.5f)
    private const int C_0_5411961 = 2217;
    private const int C_M1_8477591 = -7567;
    private const int C_0_7653669 = 3135;
    private const int C_1_1758756 = 4816;
    private const int C_0_2986313 = 1223;
    private const int C_2_0531199 = 8410;
    private const int C_3_0727110 = 12586;
    private const int C_1_5013211 = 6149;
    private const int C_M0_8999762 = -3685;
    private const int C_M2_5629154 = -10497;
    private const int C_M1_9615706 = -8034;
    private const int C_M0_3901806 = -1597;

    private const int ColumnBias = 512;
    private const int RowBias = 65536 + (128 << 17);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static byte ClampToByte(int x)
    {
        if ((uint)x > 255u)
        {
            return x < 0 ? (byte)0 : (byte)255;
        }

        return (byte)x;
    }

    /// <summary>
    /// Fast path for DC-only blocks (all 63 AC coefficients are zero): fills all 8x8 pixels
    /// with the bit-exact IDCT output without running the 2D transform.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static void FillDcBlock(Span<byte> output, int outputOffset, int stride, short dcScaled)
    {
        byte v = ClampToByte(((dcScaled + 4) >> 3) + 128);
        if ((uint)outputOffset > (uint)output.Length || (long)outputOffset + ((long)stride * 7) + 8 > output.Length)
        {
            ThrowDestinationTooSmall();
        }

#if NET8_0_OR_GREATER
        ulong fill = v * 0x0101010101010101UL;
        StoreBlockRows(output, outputOffset, stride, fill, fill, fill, fill, fill, fill, fill, fill);
#else
        int pos = outputOffset;
        for (int row = 0; row < 8; row++, pos += stride)
        {
            output.Slice(pos, 8).Fill(v);
        }
#endif
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static void IdctBlock(Span<byte> output, int outputOffset, int stride, ReadOnlySpan<short> data)
    {
#if NET8_0_OR_GREATER
        if (Sse2.IsSupported)
        {
            IdctBlockSse2(output, outputOffset, stride, data);
            return;
        }

        if (AdvSimd.Arm64.IsSupported)
        {
            IdctBlockAdvSimd(output, outputOffset, stride, data);
            return;
        }
#endif
        IdctBlockScalar(output, outputOffset, stride, data);
    }

    /// <summary>
    /// Computes two independent 8x8 IDCTs simultaneously in 256-bit AVX2 registers when supported,
    /// falling back to two single-block IDCT calls otherwise.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static void IdctTwoBlocks(
        Span<byte> out0,
        int off0,
        int stride0,
        ReadOnlySpan<short> data0,
        Span<byte> out1,
        int off1,
        int stride1,
        ReadOnlySpan<short> data1)
    {
#if NET8_0_OR_GREATER
        if (Avx2.IsSupported)
        {
            IdctTwoBlocksAvx2(out0, off0, stride0, data0, out1, off1, stride1, data1);
            return;
        }
#endif
        IdctBlock(out0, off0, stride0, data0);
        IdctBlock(out1, off1, stride1, data1);
    }

    /// <summary>
    /// Fused dequantize + 8x8 IDCT for progressive JPEG (does not mutate <paramref name="coeff"/>).
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static void DequantAndIdctBlock(
        Span<byte> output,
        int outputOffset,
        int stride,
        ReadOnlySpan<short> coeff,
        ReadOnlySpan<ushort> dequant)
    {
#if NET8_0_OR_GREATER
        if (Sse2.IsSupported)
        {
            DequantAndIdctBlockSse2(output, outputOffset, stride, coeff, dequant);
            return;
        }

        if (AdvSimd.Arm64.IsSupported)
        {
            DequantAndIdctBlockAdvSimd(output, outputOffset, stride, coeff, dequant);
            return;
        }
#endif
        Span<short> temp = stackalloc short[64];
        for (int i = 0; i < 64; i++)
        {
            temp[i] = unchecked((short)(coeff[i] * dequant[i]));
        }

        IdctBlockScalar(output, outputOffset, stride, temp);
    }

    /// <summary>
    /// Fused dequantize + 2-block AVX2 IDCT for progressive JPEG (does not mutate coefficients).
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static void DequantAndIdctTwoBlocks(
        Span<byte> out0,
        int off0,
        int stride0,
        ReadOnlySpan<short> coeff0,
        Span<byte> out1,
        int off1,
        int stride1,
        ReadOnlySpan<short> coeff1,
        ReadOnlySpan<ushort> dequant)
    {
#if NET8_0_OR_GREATER
        if (Avx2.IsSupported)
        {
            DequantAndIdctTwoBlocksAvx2(out0, off0, stride0, coeff0, out1, off1, stride1, coeff1, dequant);
            return;
        }
#endif
        DequantAndIdctBlock(out0, off0, stride0, coeff0, dequant);
        DequantAndIdctBlock(out1, off1, stride1, coeff1, dequant);
    }

    internal static void IdctBlockScalar(Span<byte> output, int outputOffset, int stride, ReadOnlySpan<short> data)
    {
        Span<int> val = stackalloc int[64];

        // Column pass
        for (int i = 0; i < 8; i++)
        {
            int d0 = data[i];
            int d1 = data[i + 8];
            int d2 = data[i + 16];
            int d3 = data[i + 24];
            int d4 = data[i + 32];
            int d5 = data[i + 40];
            int d6 = data[i + 48];
            int d7 = data[i + 56];

            if ((d1 | d2 | d3 | d4 | d5 | d6 | d7) == 0)
            {
                int dcterm = d0 << 2;
                val[i] = dcterm;
                val[i + 8] = dcterm;
                val[i + 16] = dcterm;
                val[i + 24] = dcterm;
                val[i + 32] = dcterm;
                val[i + 40] = dcterm;
                val[i + 48] = dcterm;
                val[i + 56] = dcterm;
            }
            else
            {
                int p2 = d2;
                int p3 = d6;
                int p1 = (p2 + p3) * C_0_5411961;
                int t2 = p1 + (p3 * C_M1_8477591);
                int t3 = p1 + (p2 * C_0_7653669);

                p2 = d0;
                p3 = d4;
                int t0 = (p2 + p3) << 12;
                int t1 = (p2 - p3) << 12;

                int x0 = t0 + t3 + ColumnBias;
                int x3 = t0 - t3 + ColumnBias;
                int x1 = t1 + t2 + ColumnBias;
                int x2 = t1 - t2 + ColumnBias;

                t0 = d7;
                t1 = d5;
                t2 = d3;
                t3 = d1;

                p3 = t0 + t2;
                int p4 = t1 + t3;
                p1 = t0 + t3;
                p2 = t1 + t2;
                int p5 = (p3 + p4) * C_1_1758756;

                t0 *= C_0_2986313;
                t1 *= C_2_0531199;
                t2 *= C_3_0727110;
                t3 *= C_1_5013211;

                p1 = p5 + (p1 * C_M0_8999762);
                p2 = p5 + (p2 * C_M2_5629154);
                p3 *= C_M1_9615706;
                p4 *= C_M0_3901806;

                t3 += p1 + p4;
                t2 += p2 + p3;
                t1 += p2 + p4;
                t0 += p1 + p3;

                val[i] = (x0 + t3) >> 10;
                val[i + 56] = (x0 - t3) >> 10;
                val[i + 8] = (x1 + t2) >> 10;
                val[i + 48] = (x1 - t2) >> 10;
                val[i + 16] = (x2 + t1) >> 10;
                val[i + 40] = (x2 - t1) >> 10;
                val[i + 24] = (x3 + t0) >> 10;
                val[i + 32] = (x3 - t0) >> 10;
            }
        }

        // Row pass
        int outPos = outputOffset;
        for (int i = 0; i < 64; i += 8, outPos += stride)
        {
            int v0 = val[i];
            int v1 = val[i + 1];
            int v2 = val[i + 2];
            int v3 = val[i + 3];
            int v4 = val[i + 4];
            int v5 = val[i + 5];
            int v6 = val[i + 6];
            int v7 = val[i + 7];

            int p2 = v2;
            int p3 = v6;
            int p1 = (p2 + p3) * C_0_5411961;
            int t2 = p1 + (p3 * C_M1_8477591);
            int t3 = p1 + (p2 * C_0_7653669);

            p2 = v0;
            p3 = v4;
            int t0 = (p2 + p3) << 12;
            int t1 = (p2 - p3) << 12;

            int x0 = t0 + t3 + RowBias;
            int x3 = t0 - t3 + RowBias;
            int x1 = t1 + t2 + RowBias;
            int x2 = t1 - t2 + RowBias;

            t0 = v7;
            t1 = v5;
            t2 = v3;
            t3 = v1;

            p3 = t0 + t2;
            int p4 = t1 + t3;
            p1 = t0 + t3;
            p2 = t1 + t2;
            int p5 = (p3 + p4) * C_1_1758756;

            t0 *= C_0_2986313;
            t1 *= C_2_0531199;
            t2 *= C_3_0727110;
            t3 *= C_1_5013211;

            p1 = p5 + (p1 * C_M0_8999762);
            p2 = p5 + (p2 * C_M2_5629154);
            p3 *= C_M1_9615706;
            p4 *= C_M0_3901806;

            t3 += p1 + p4;
            t2 += p2 + p3;
            t1 += p2 + p4;
            t0 += p1 + p3;

            output[outPos] = ClampToByte((x0 + t3) >> 17);
            output[outPos + 7] = ClampToByte((x0 - t3) >> 17);
            output[outPos + 1] = ClampToByte((x1 + t2) >> 17);
            output[outPos + 6] = ClampToByte((x1 - t2) >> 17);
            output[outPos + 2] = ClampToByte((x2 + t1) >> 17);
            output[outPos + 5] = ClampToByte((x2 - t1) >> 17);
            output[outPos + 3] = ClampToByte((x3 + t0) >> 17);
            output[outPos + 4] = ClampToByte((x3 - t0) >> 17);
        }
    }

#if NET8_0_OR_GREATER
    // -------------------------------------------------------------------------
    // AVX2 2-blocks-at-once IDCT (bit-identical to IdctBlockScalar)
    // -------------------------------------------------------------------------

    private static void IdctTwoBlocksAvx2(
        Span<byte> out0,
        int off0,
        int stride0,
        ReadOnlySpan<short> data0,
        Span<byte> out1,
        int off1,
        int stride1,
        ReadOnlySpan<short> data1)
    {
        ValidateInputs2(out0, off0, stride0, data0, out1, off1, stride1, data1);

        ReadOnlySpan<Vector128<short>> d0 = MemoryMarshal.Cast<short, Vector128<short>>(data0.Slice(0, 64));
        ReadOnlySpan<Vector128<short>> d1 = MemoryMarshal.Cast<short, Vector128<short>>(data1.Slice(0, 64));

        Vector256<short> row0 = Vector256.Create(d0[0], d1[0]);
        Vector256<short> row1 = Vector256.Create(d0[1], d1[1]);
        Vector256<short> row2 = Vector256.Create(d0[2], d1[2]);
        Vector256<short> row3 = Vector256.Create(d0[3], d1[3]);
        Vector256<short> row4 = Vector256.Create(d0[4], d1[4]);
        Vector256<short> row5 = Vector256.Create(d0[5], d1[5]);
        Vector256<short> row6 = Vector256.Create(d0[6], d1[6]);
        Vector256<short> row7 = Vector256.Create(d0[7], d1[7]);

        IdctTwoBlocksCoreAvx2(
            ref row0, ref row1, ref row2, ref row3, ref row4, ref row5, ref row6, ref row7,
            out0, off0, stride0, out1, off1, stride1);
    }

    private static void DequantAndIdctTwoBlocksAvx2(
        Span<byte> out0,
        int off0,
        int stride0,
        ReadOnlySpan<short> coeff0,
        Span<byte> out1,
        int off1,
        int stride1,
        ReadOnlySpan<short> coeff1,
        ReadOnlySpan<ushort> dequant)
    {
        ValidateInputs2(out0, off0, stride0, coeff0, out1, off1, stride1, coeff1);
        if (dequant.Length < 64)
        {
            ThrowDestinationTooSmall();
        }

        ReadOnlySpan<Vector128<short>> d0 = MemoryMarshal.Cast<short, Vector128<short>>(coeff0.Slice(0, 64));
        ReadOnlySpan<Vector128<short>> d1 = MemoryMarshal.Cast<short, Vector128<short>>(coeff1.Slice(0, 64));
        ReadOnlySpan<Vector128<short>> dq = MemoryMarshal.Cast<ushort, Vector128<short>>(dequant.Slice(0, 64));

        Vector128<short> q0 = dq[0];
        Vector128<short> q1 = dq[1];
        Vector128<short> q2 = dq[2];
        Vector128<short> q3 = dq[3];
        Vector128<short> q4 = dq[4];
        Vector128<short> q5 = dq[5];
        Vector128<short> q6 = dq[6];
        Vector128<short> q7 = dq[7];

        Vector256<short> row0 = Avx2.MultiplyLow(Vector256.Create(d0[0], d1[0]), Vector256.Create(q0, q0));
        Vector256<short> row1 = Avx2.MultiplyLow(Vector256.Create(d0[1], d1[1]), Vector256.Create(q1, q1));
        Vector256<short> row2 = Avx2.MultiplyLow(Vector256.Create(d0[2], d1[2]), Vector256.Create(q2, q2));
        Vector256<short> row3 = Avx2.MultiplyLow(Vector256.Create(d0[3], d1[3]), Vector256.Create(q3, q3));
        Vector256<short> row4 = Avx2.MultiplyLow(Vector256.Create(d0[4], d1[4]), Vector256.Create(q4, q4));
        Vector256<short> row5 = Avx2.MultiplyLow(Vector256.Create(d0[5], d1[5]), Vector256.Create(q5, q5));
        Vector256<short> row6 = Avx2.MultiplyLow(Vector256.Create(d0[6], d1[6]), Vector256.Create(q6, q6));
        Vector256<short> row7 = Avx2.MultiplyLow(Vector256.Create(d0[7], d1[7]), Vector256.Create(q7, q7));

        IdctTwoBlocksCoreAvx2(
            ref row0, ref row1, ref row2, ref row3, ref row4, ref row5, ref row6, ref row7,
            out0, off0, stride0, out1, off1, stride1);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void IdctTwoBlocksCoreAvx2(
        ref Vector256<short> row0,
        ref Vector256<short> row1,
        ref Vector256<short> row2,
        ref Vector256<short> row3,
        ref Vector256<short> row4,
        ref Vector256<short> row5,
        ref Vector256<short> row6,
        ref Vector256<short> row7,
        Span<byte> out0,
        int off0,
        int stride0,
        Span<byte> out1,
        int off1,
        int stride1)
    {
        // Column pass
        DctPassAvx2(
            ref row0, ref row1, ref row2, ref row3, ref row4, ref row5, ref row6, ref row7,
            Vector256.Create(ColumnBias), isRowPass: false);

        // In-lane 16-bit 8x8 transpose (operates on both blocks simultaneously!)
        Interleave16Avx2(ref row0, ref row4);
        Interleave16Avx2(ref row1, ref row5);
        Interleave16Avx2(ref row2, ref row6);
        Interleave16Avx2(ref row3, ref row7);

        Interleave16Avx2(ref row0, ref row2);
        Interleave16Avx2(ref row1, ref row3);
        Interleave16Avx2(ref row4, ref row6);
        Interleave16Avx2(ref row5, ref row7);

        Interleave16Avx2(ref row0, ref row1);
        Interleave16Avx2(ref row2, ref row3);
        Interleave16Avx2(ref row4, ref row5);
        Interleave16Avx2(ref row6, ref row7);

        // Row pass
        DctPassAvx2(
            ref row0, ref row1, ref row2, ref row3, ref row4, ref row5, ref row6, ref row7,
            Vector256.Create(RowBias), isRowPass: true);

        // Saturate to byte + in-lane 8-bit 8x8 transpose
        Vector256<byte> p0 = Avx2.PackUnsignedSaturate(row0, row1);
        Vector256<byte> p1 = Avx2.PackUnsignedSaturate(row2, row3);
        Vector256<byte> p2 = Avx2.PackUnsignedSaturate(row4, row5);
        Vector256<byte> p3 = Avx2.PackUnsignedSaturate(row6, row7);

        Interleave8Avx2(ref p0, ref p2);
        Interleave8Avx2(ref p1, ref p3);

        Interleave8Avx2(ref p0, ref p1);
        Interleave8Avx2(ref p2, ref p3);

        Interleave8Avx2(ref p0, ref p2);
        Interleave8Avx2(ref p1, ref p3);

        Vector256<ulong> u0 = p0.AsUInt64();
        Vector256<ulong> u1 = p1.AsUInt64();
        Vector256<ulong> u2 = p2.AsUInt64();
        Vector256<ulong> u3 = p3.AsUInt64();

        // Store Block 0 (low 128-bit lane)
        StoreBlockRows(
            out0, off0, stride0,
            u0.GetElement(0), u0.GetElement(1),
            u2.GetElement(0), u2.GetElement(1),
            u1.GetElement(0), u1.GetElement(1),
            u3.GetElement(0), u3.GetElement(1));

        // Store Block 1 (high 128-bit lane)
        StoreBlockRows(
            out1, off1, stride1,
            u0.GetElement(2), u0.GetElement(3),
            u2.GetElement(2), u2.GetElement(3),
            u1.GetElement(2), u1.GetElement(3),
            u3.GetElement(2), u3.GetElement(3));
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector256<short> DctConst256(short x, short y) =>
        Vector256.Create(x, y, x, y, x, y, x, y, x, y, x, y, x, y, x, y);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void DctPassAvx2(
        ref Vector256<short> row0,
        ref Vector256<short> row1,
        ref Vector256<short> row2,
        ref Vector256<short> row3,
        ref Vector256<short> row4,
        ref Vector256<short> row5,
        ref Vector256<short> row6,
        ref Vector256<short> row7,
        Vector256<int> bias,
        bool isRowPass)
    {
        Vector256<short> rot0_0 = DctConst256(C_0_5411961, C_0_5411961 + C_M1_8477591);
        Vector256<short> rot0_1 = DctConst256(C_0_5411961 + C_0_7653669, C_0_5411961);
        Vector256<short> rot1_0 = DctConst256(C_1_1758756 + C_M0_8999762, C_1_1758756);
        Vector256<short> rot1_1 = DctConst256(C_1_1758756, C_1_1758756 + C_M2_5629154);
        Vector256<short> rot2_0 = DctConst256(C_M1_9615706 + C_0_2986313, C_M1_9615706);
        Vector256<short> rot2_1 = DctConst256(C_M1_9615706, C_M1_9615706 + C_3_0727110);
        Vector256<short> rot3_0 = DctConst256(C_M0_3901806 + C_2_0531199, C_M0_3901806);
        Vector256<short> rot3_1 = DctConst256(C_M0_3901806, C_M0_3901806 + C_1_5013211);

        // Even part
        Vector256<short> lo26 = Avx2.UnpackLow(row2, row6);
        Vector256<short> hi26 = Avx2.UnpackHigh(row2, row6);
        Vector256<int> t2eL = Avx2.MultiplyAddAdjacent(lo26, rot0_0);
        Vector256<int> t2eH = Avx2.MultiplyAddAdjacent(hi26, rot0_0);
        Vector256<int> t3eL = Avx2.MultiplyAddAdjacent(lo26, rot0_1);
        Vector256<int> t3eH = Avx2.MultiplyAddAdjacent(hi26, rot0_1);

        Vector256<short> sum04 = Avx2.Add(row0, row4);
        Vector256<short> dif04 = Avx2.Subtract(row0, row4);
        Vector256<short> zero = Vector256<short>.Zero;
        Vector256<int> t0eL = Avx2.ShiftRightArithmetic(Avx2.UnpackLow(zero, sum04).AsInt32(), 4);
        Vector256<int> t0eH = Avx2.ShiftRightArithmetic(Avx2.UnpackHigh(zero, sum04).AsInt32(), 4);
        Vector256<int> t1eL = Avx2.ShiftRightArithmetic(Avx2.UnpackLow(zero, dif04).AsInt32(), 4);
        Vector256<int> t1eH = Avx2.ShiftRightArithmetic(Avx2.UnpackHigh(zero, dif04).AsInt32(), 4);

        Vector256<int> x0L = Avx2.Add(Avx2.Add(t0eL, t3eL), bias);
        Vector256<int> x0H = Avx2.Add(Avx2.Add(t0eH, t3eH), bias);
        Vector256<int> x3L = Avx2.Add(Avx2.Subtract(t0eL, t3eL), bias);
        Vector256<int> x3H = Avx2.Add(Avx2.Subtract(t0eH, t3eH), bias);
        Vector256<int> x1L = Avx2.Add(Avx2.Add(t1eL, t2eL), bias);
        Vector256<int> x1H = Avx2.Add(Avx2.Add(t1eH, t2eH), bias);
        Vector256<int> x2L = Avx2.Add(Avx2.Subtract(t1eL, t2eL), bias);
        Vector256<int> x2H = Avx2.Add(Avx2.Subtract(t1eH, t2eH), bias);

        // Odd part
        Vector256<short> lo73 = Avx2.UnpackLow(row7, row3);
        Vector256<short> hi73 = Avx2.UnpackHigh(row7, row3);
        Vector256<int> y0oL = Avx2.MultiplyAddAdjacent(lo73, rot2_0);
        Vector256<int> y0oH = Avx2.MultiplyAddAdjacent(hi73, rot2_0);
        Vector256<int> y2oL = Avx2.MultiplyAddAdjacent(lo73, rot2_1);
        Vector256<int> y2oH = Avx2.MultiplyAddAdjacent(hi73, rot2_1);

        Vector256<short> lo51 = Avx2.UnpackLow(row5, row1);
        Vector256<short> hi51 = Avx2.UnpackHigh(row5, row1);
        Vector256<int> y1oL = Avx2.MultiplyAddAdjacent(lo51, rot3_0);
        Vector256<int> y1oH = Avx2.MultiplyAddAdjacent(hi51, rot3_0);
        Vector256<int> y3oL = Avx2.MultiplyAddAdjacent(lo51, rot3_1);
        Vector256<int> y3oH = Avx2.MultiplyAddAdjacent(hi51, rot3_1);

        Vector256<short> sum17 = Avx2.Add(row1, row7);
        Vector256<short> sum35 = Avx2.Add(row3, row5);
        Vector256<short> loOdd = Avx2.UnpackLow(sum17, sum35);
        Vector256<short> hiOdd = Avx2.UnpackHigh(sum17, sum35);
        Vector256<int> y4oL = Avx2.MultiplyAddAdjacent(loOdd, rot1_0);
        Vector256<int> y4oH = Avx2.MultiplyAddAdjacent(hiOdd, rot1_0);
        Vector256<int> y5oL = Avx2.MultiplyAddAdjacent(loOdd, rot1_1);
        Vector256<int> y5oH = Avx2.MultiplyAddAdjacent(hiOdd, rot1_1);

        Vector256<int> x4L = Avx2.Add(y0oL, y4oL);
        Vector256<int> x4H = Avx2.Add(y0oH, y4oH);
        Vector256<int> x5L = Avx2.Add(y1oL, y5oL);
        Vector256<int> x5H = Avx2.Add(y1oH, y5oH);
        Vector256<int> x6L = Avx2.Add(y2oL, y5oL);
        Vector256<int> x6H = Avx2.Add(y2oH, y5oH);
        Vector256<int> x7L = Avx2.Add(y3oL, y4oL);
        Vector256<int> x7H = Avx2.Add(y3oH, y4oH);

        // Final butterflies + constant shift + saturating pack
        if (isRowPass)
        {
            row0 = Avx2.PackSignedSaturate(Avx2.ShiftRightArithmetic(Avx2.Add(x0L, x7L), 17), Avx2.ShiftRightArithmetic(Avx2.Add(x0H, x7H), 17));
            row7 = Avx2.PackSignedSaturate(Avx2.ShiftRightArithmetic(Avx2.Subtract(x0L, x7L), 17), Avx2.ShiftRightArithmetic(Avx2.Subtract(x0H, x7H), 17));
            row1 = Avx2.PackSignedSaturate(Avx2.ShiftRightArithmetic(Avx2.Add(x1L, x6L), 17), Avx2.ShiftRightArithmetic(Avx2.Add(x1H, x6H), 17));
            row6 = Avx2.PackSignedSaturate(Avx2.ShiftRightArithmetic(Avx2.Subtract(x1L, x6L), 17), Avx2.ShiftRightArithmetic(Avx2.Subtract(x1H, x6H), 17));
            row2 = Avx2.PackSignedSaturate(Avx2.ShiftRightArithmetic(Avx2.Add(x2L, x5L), 17), Avx2.ShiftRightArithmetic(Avx2.Add(x2H, x5H), 17));
            row5 = Avx2.PackSignedSaturate(Avx2.ShiftRightArithmetic(Avx2.Subtract(x2L, x5L), 17), Avx2.ShiftRightArithmetic(Avx2.Subtract(x2H, x5H), 17));
            row3 = Avx2.PackSignedSaturate(Avx2.ShiftRightArithmetic(Avx2.Add(x3L, x4L), 17), Avx2.ShiftRightArithmetic(Avx2.Add(x3H, x4H), 17));
            row4 = Avx2.PackSignedSaturate(Avx2.ShiftRightArithmetic(Avx2.Subtract(x3L, x4L), 17), Avx2.ShiftRightArithmetic(Avx2.Subtract(x3H, x4H), 17));
        }
        else
        {
            row0 = Avx2.PackSignedSaturate(Avx2.ShiftRightArithmetic(Avx2.Add(x0L, x7L), 10), Avx2.ShiftRightArithmetic(Avx2.Add(x0H, x7H), 10));
            row7 = Avx2.PackSignedSaturate(Avx2.ShiftRightArithmetic(Avx2.Subtract(x0L, x7L), 10), Avx2.ShiftRightArithmetic(Avx2.Subtract(x0H, x7H), 10));
            row1 = Avx2.PackSignedSaturate(Avx2.ShiftRightArithmetic(Avx2.Add(x1L, x6L), 10), Avx2.ShiftRightArithmetic(Avx2.Add(x1H, x6H), 10));
            row6 = Avx2.PackSignedSaturate(Avx2.ShiftRightArithmetic(Avx2.Subtract(x1L, x6L), 10), Avx2.ShiftRightArithmetic(Avx2.Subtract(x1H, x6H), 10));
            row2 = Avx2.PackSignedSaturate(Avx2.ShiftRightArithmetic(Avx2.Add(x2L, x5L), 10), Avx2.ShiftRightArithmetic(Avx2.Add(x2H, x5H), 10));
            row5 = Avx2.PackSignedSaturate(Avx2.ShiftRightArithmetic(Avx2.Subtract(x2L, x5L), 10), Avx2.ShiftRightArithmetic(Avx2.Subtract(x2H, x5H), 10));
            row3 = Avx2.PackSignedSaturate(Avx2.ShiftRightArithmetic(Avx2.Add(x3L, x4L), 10), Avx2.ShiftRightArithmetic(Avx2.Add(x3H, x4H), 10));
            row4 = Avx2.PackSignedSaturate(Avx2.ShiftRightArithmetic(Avx2.Subtract(x3L, x4L), 10), Avx2.ShiftRightArithmetic(Avx2.Subtract(x3H, x4H), 10));
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void Interleave16Avx2(ref Vector256<short> a, ref Vector256<short> b)
    {
        Vector256<short> tmp = a;
        a = Avx2.UnpackLow(tmp, b);
        b = Avx2.UnpackHigh(tmp, b);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void Interleave8Avx2(ref Vector256<byte> a, ref Vector256<byte> b)
    {
        Vector256<byte> tmp = a;
        a = Avx2.UnpackLow(tmp, b);
        b = Avx2.UnpackHigh(tmp, b);
    }

    // -------------------------------------------------------------------------
    // SSE2 1-block IDCT (bit-identical to IdctBlockScalar)
    // -------------------------------------------------------------------------

    private static void IdctBlockSse2(Span<byte> output, int outputOffset, int stride, ReadOnlySpan<short> data)
    {
        ValidateInputs1(output, outputOffset, stride, data);
        ReadOnlySpan<Vector128<short>> d = MemoryMarshal.Cast<short, Vector128<short>>(data.Slice(0, 64));

        Vector128<short> row0 = d[0];
        Vector128<short> row1 = d[1];
        Vector128<short> row2 = d[2];
        Vector128<short> row3 = d[3];
        Vector128<short> row4 = d[4];
        Vector128<short> row5 = d[5];
        Vector128<short> row6 = d[6];
        Vector128<short> row7 = d[7];

        IdctBlockCoreSse2(ref row0, ref row1, ref row2, ref row3, ref row4, ref row5, ref row6, ref row7, output, outputOffset, stride);
    }

    private static void DequantAndIdctBlockSse2(
        Span<byte> output,
        int outputOffset,
        int stride,
        ReadOnlySpan<short> coeff,
        ReadOnlySpan<ushort> dequant)
    {
        ValidateInputs1(output, outputOffset, stride, coeff);
        if (dequant.Length < 64)
        {
            ThrowDestinationTooSmall();
        }

        ReadOnlySpan<Vector128<short>> d = MemoryMarshal.Cast<short, Vector128<short>>(coeff.Slice(0, 64));
        ReadOnlySpan<Vector128<short>> dq = MemoryMarshal.Cast<ushort, Vector128<short>>(dequant.Slice(0, 64));

        Vector128<short> row0 = Sse2.MultiplyLow(d[0], dq[0]);
        Vector128<short> row1 = Sse2.MultiplyLow(d[1], dq[1]);
        Vector128<short> row2 = Sse2.MultiplyLow(d[2], dq[2]);
        Vector128<short> row3 = Sse2.MultiplyLow(d[3], dq[3]);
        Vector128<short> row4 = Sse2.MultiplyLow(d[4], dq[4]);
        Vector128<short> row5 = Sse2.MultiplyLow(d[5], dq[5]);
        Vector128<short> row6 = Sse2.MultiplyLow(d[6], dq[6]);
        Vector128<short> row7 = Sse2.MultiplyLow(d[7], dq[7]);

        IdctBlockCoreSse2(ref row0, ref row1, ref row2, ref row3, ref row4, ref row5, ref row6, ref row7, output, outputOffset, stride);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void IdctBlockCoreSse2(
        ref Vector128<short> row0,
        ref Vector128<short> row1,
        ref Vector128<short> row2,
        ref Vector128<short> row3,
        ref Vector128<short> row4,
        ref Vector128<short> row5,
        ref Vector128<short> row6,
        ref Vector128<short> row7,
        Span<byte> output,
        int outputOffset,
        int stride)
    {
        DctPassSse2(
            ref row0, ref row1, ref row2, ref row3, ref row4, ref row5, ref row6, ref row7,
            Vector128.Create(ColumnBias), isRowPass: false);

        Interleave16Sse2(ref row0, ref row4);
        Interleave16Sse2(ref row1, ref row5);
        Interleave16Sse2(ref row2, ref row6);
        Interleave16Sse2(ref row3, ref row7);

        Interleave16Sse2(ref row0, ref row2);
        Interleave16Sse2(ref row1, ref row3);
        Interleave16Sse2(ref row4, ref row6);
        Interleave16Sse2(ref row5, ref row7);

        Interleave16Sse2(ref row0, ref row1);
        Interleave16Sse2(ref row2, ref row3);
        Interleave16Sse2(ref row4, ref row5);
        Interleave16Sse2(ref row6, ref row7);

        DctPassSse2(
            ref row0, ref row1, ref row2, ref row3, ref row4, ref row5, ref row6, ref row7,
            Vector128.Create(RowBias), isRowPass: true);

        Vector128<byte> p0 = Sse2.PackUnsignedSaturate(row0, row1);
        Vector128<byte> p1 = Sse2.PackUnsignedSaturate(row2, row3);
        Vector128<byte> p2 = Sse2.PackUnsignedSaturate(row4, row5);
        Vector128<byte> p3 = Sse2.PackUnsignedSaturate(row6, row7);

        Interleave8Sse2(ref p0, ref p2);
        Interleave8Sse2(ref p1, ref p3);

        Interleave8Sse2(ref p0, ref p1);
        Interleave8Sse2(ref p2, ref p3);

        Interleave8Sse2(ref p0, ref p2);
        Interleave8Sse2(ref p1, ref p3);

        Vector128<ulong> u0 = p0.AsUInt64();
        Vector128<ulong> u1 = p1.AsUInt64();
        Vector128<ulong> u2 = p2.AsUInt64();
        Vector128<ulong> u3 = p3.AsUInt64();

        StoreBlockRows(
            output, outputOffset, stride,
            u0.GetElement(0), u0.GetElement(1),
            u2.GetElement(0), u2.GetElement(1),
            u1.GetElement(0), u1.GetElement(1),
            u3.GetElement(0), u3.GetElement(1));
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector128<short> DctConst128(short x, short y) =>
        Vector128.Create(x, y, x, y, x, y, x, y);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void DctPassSse2(
        ref Vector128<short> row0,
        ref Vector128<short> row1,
        ref Vector128<short> row2,
        ref Vector128<short> row3,
        ref Vector128<short> row4,
        ref Vector128<short> row5,
        ref Vector128<short> row6,
        ref Vector128<short> row7,
        Vector128<int> bias,
        bool isRowPass)
    {
        Vector128<short> rot0_0 = DctConst128(C_0_5411961, C_0_5411961 + C_M1_8477591);
        Vector128<short> rot0_1 = DctConst128(C_0_5411961 + C_0_7653669, C_0_5411961);
        Vector128<short> rot1_0 = DctConst128(C_1_1758756 + C_M0_8999762, C_1_1758756);
        Vector128<short> rot1_1 = DctConst128(C_1_1758756, C_1_1758756 + C_M2_5629154);
        Vector128<short> rot2_0 = DctConst128(C_M1_9615706 + C_0_2986313, C_M1_9615706);
        Vector128<short> rot2_1 = DctConst128(C_M1_9615706, C_M1_9615706 + C_3_0727110);
        Vector128<short> rot3_0 = DctConst128(C_M0_3901806 + C_2_0531199, C_M0_3901806);
        Vector128<short> rot3_1 = DctConst128(C_M0_3901806, C_M0_3901806 + C_1_5013211);

        Vector128<short> lo26 = Sse2.UnpackLow(row2, row6);
        Vector128<short> hi26 = Sse2.UnpackHigh(row2, row6);
        Vector128<int> t2eL = Sse2.MultiplyAddAdjacent(lo26, rot0_0);
        Vector128<int> t2eH = Sse2.MultiplyAddAdjacent(hi26, rot0_0);
        Vector128<int> t3eL = Sse2.MultiplyAddAdjacent(lo26, rot0_1);
        Vector128<int> t3eH = Sse2.MultiplyAddAdjacent(hi26, rot0_1);

        Vector128<short> sum04 = Sse2.Add(row0, row4);
        Vector128<short> dif04 = Sse2.Subtract(row0, row4);
        Vector128<short> zero = Vector128<short>.Zero;
        Vector128<int> t0eL = Sse2.ShiftRightArithmetic(Sse2.UnpackLow(zero, sum04).AsInt32(), 4);
        Vector128<int> t0eH = Sse2.ShiftRightArithmetic(Sse2.UnpackHigh(zero, sum04).AsInt32(), 4);
        Vector128<int> t1eL = Sse2.ShiftRightArithmetic(Sse2.UnpackLow(zero, dif04).AsInt32(), 4);
        Vector128<int> t1eH = Sse2.ShiftRightArithmetic(Sse2.UnpackHigh(zero, dif04).AsInt32(), 4);

        Vector128<int> x0L = Sse2.Add(Sse2.Add(t0eL, t3eL), bias);
        Vector128<int> x0H = Sse2.Add(Sse2.Add(t0eH, t3eH), bias);
        Vector128<int> x3L = Sse2.Add(Sse2.Subtract(t0eL, t3eL), bias);
        Vector128<int> x3H = Sse2.Add(Sse2.Subtract(t0eH, t3eH), bias);
        Vector128<int> x1L = Sse2.Add(Sse2.Add(t1eL, t2eL), bias);
        Vector128<int> x1H = Sse2.Add(Sse2.Add(t1eH, t2eH), bias);
        Vector128<int> x2L = Sse2.Add(Sse2.Subtract(t1eL, t2eL), bias);
        Vector128<int> x2H = Sse2.Add(Sse2.Subtract(t1eH, t2eH), bias);

        Vector128<short> lo73 = Sse2.UnpackLow(row7, row3);
        Vector128<short> hi73 = Sse2.UnpackHigh(row7, row3);
        Vector128<int> y0oL = Sse2.MultiplyAddAdjacent(lo73, rot2_0);
        Vector128<int> y0oH = Sse2.MultiplyAddAdjacent(hi73, rot2_0);
        Vector128<int> y2oL = Sse2.MultiplyAddAdjacent(lo73, rot2_1);
        Vector128<int> y2oH = Sse2.MultiplyAddAdjacent(hi73, rot2_1);

        Vector128<short> lo51 = Sse2.UnpackLow(row5, row1);
        Vector128<short> hi51 = Sse2.UnpackHigh(row5, row1);
        Vector128<int> y1oL = Sse2.MultiplyAddAdjacent(lo51, rot3_0);
        Vector128<int> y1oH = Sse2.MultiplyAddAdjacent(hi51, rot3_0);
        Vector128<int> y3oL = Sse2.MultiplyAddAdjacent(lo51, rot3_1);
        Vector128<int> y3oH = Sse2.MultiplyAddAdjacent(hi51, rot3_1);

        Vector128<short> sum17 = Sse2.Add(row1, row7);
        Vector128<short> sum35 = Sse2.Add(row3, row5);
        Vector128<short> loOdd = Sse2.UnpackLow(sum17, sum35);
        Vector128<short> hiOdd = Sse2.UnpackHigh(sum17, sum35);
        Vector128<int> y4oL = Sse2.MultiplyAddAdjacent(loOdd, rot1_0);
        Vector128<int> y4oH = Sse2.MultiplyAddAdjacent(hiOdd, rot1_0);
        Vector128<int> y5oL = Sse2.MultiplyAddAdjacent(loOdd, rot1_1);
        Vector128<int> y5oH = Sse2.MultiplyAddAdjacent(hiOdd, rot1_1);

        Vector128<int> x4L = Sse2.Add(y0oL, y4oL);
        Vector128<int> x4H = Sse2.Add(y0oH, y4oH);
        Vector128<int> x5L = Sse2.Add(y1oL, y5oL);
        Vector128<int> x5H = Sse2.Add(y1oH, y5oH);
        Vector128<int> x6L = Sse2.Add(y2oL, y5oL);
        Vector128<int> x6H = Sse2.Add(y2oH, y5oH);
        Vector128<int> x7L = Sse2.Add(y3oL, y4oL);
        Vector128<int> x7H = Sse2.Add(y3oH, y4oH);

        if (isRowPass)
        {
            row0 = Sse2.PackSignedSaturate(Sse2.ShiftRightArithmetic(Sse2.Add(x0L, x7L), 17), Sse2.ShiftRightArithmetic(Sse2.Add(x0H, x7H), 17));
            row7 = Sse2.PackSignedSaturate(Sse2.ShiftRightArithmetic(Sse2.Subtract(x0L, x7L), 17), Sse2.ShiftRightArithmetic(Sse2.Subtract(x0H, x7H), 17));
            row1 = Sse2.PackSignedSaturate(Sse2.ShiftRightArithmetic(Sse2.Add(x1L, x6L), 17), Sse2.ShiftRightArithmetic(Sse2.Add(x1H, x6H), 17));
            row6 = Sse2.PackSignedSaturate(Sse2.ShiftRightArithmetic(Sse2.Subtract(x1L, x6L), 17), Sse2.ShiftRightArithmetic(Sse2.Subtract(x1H, x6H), 17));
            row2 = Sse2.PackSignedSaturate(Sse2.ShiftRightArithmetic(Sse2.Add(x2L, x5L), 17), Sse2.ShiftRightArithmetic(Sse2.Add(x2H, x5H), 17));
            row5 = Sse2.PackSignedSaturate(Sse2.ShiftRightArithmetic(Sse2.Subtract(x2L, x5L), 17), Sse2.ShiftRightArithmetic(Sse2.Subtract(x2H, x5H), 17));
            row3 = Sse2.PackSignedSaturate(Sse2.ShiftRightArithmetic(Sse2.Add(x3L, x4L), 17), Sse2.ShiftRightArithmetic(Sse2.Add(x3H, x4H), 17));
            row4 = Sse2.PackSignedSaturate(Sse2.ShiftRightArithmetic(Sse2.Subtract(x3L, x4L), 17), Sse2.ShiftRightArithmetic(Sse2.Subtract(x3H, x4H), 17));
        }
        else
        {
            row0 = Sse2.PackSignedSaturate(Sse2.ShiftRightArithmetic(Sse2.Add(x0L, x7L), 10), Sse2.ShiftRightArithmetic(Sse2.Add(x0H, x7H), 10));
            row7 = Sse2.PackSignedSaturate(Sse2.ShiftRightArithmetic(Sse2.Subtract(x0L, x7L), 10), Sse2.ShiftRightArithmetic(Sse2.Subtract(x0H, x7H), 10));
            row1 = Sse2.PackSignedSaturate(Sse2.ShiftRightArithmetic(Sse2.Add(x1L, x6L), 10), Sse2.ShiftRightArithmetic(Sse2.Add(x1H, x6H), 10));
            row6 = Sse2.PackSignedSaturate(Sse2.ShiftRightArithmetic(Sse2.Subtract(x1L, x6L), 10), Sse2.ShiftRightArithmetic(Sse2.Subtract(x1H, x6H), 10));
            row2 = Sse2.PackSignedSaturate(Sse2.ShiftRightArithmetic(Sse2.Add(x2L, x5L), 10), Sse2.ShiftRightArithmetic(Sse2.Add(x2H, x5H), 10));
            row5 = Sse2.PackSignedSaturate(Sse2.ShiftRightArithmetic(Sse2.Subtract(x2L, x5L), 10), Sse2.ShiftRightArithmetic(Sse2.Subtract(x2H, x5H), 10));
            row3 = Sse2.PackSignedSaturate(Sse2.ShiftRightArithmetic(Sse2.Add(x3L, x4L), 10), Sse2.ShiftRightArithmetic(Sse2.Add(x3H, x4H), 10));
            row4 = Sse2.PackSignedSaturate(Sse2.ShiftRightArithmetic(Sse2.Subtract(x3L, x4L), 10), Sse2.ShiftRightArithmetic(Sse2.Subtract(x3H, x4H), 10));
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void Interleave16Sse2(ref Vector128<short> a, ref Vector128<short> b)
    {
        Vector128<short> tmp = a;
        a = Sse2.UnpackLow(tmp, b);
        b = Sse2.UnpackHigh(tmp, b);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void Interleave8Sse2(ref Vector128<byte> a, ref Vector128<byte> b)
    {
        Vector128<byte> tmp = a;
        a = Sse2.UnpackLow(tmp, b);
        b = Sse2.UnpackHigh(tmp, b);
    }

    // -------------------------------------------------------------------------
    // Arm64 AdvSimd 1-block IDCT (bit-identical to IdctBlockScalar)
    // -------------------------------------------------------------------------

    private static void IdctBlockAdvSimd(Span<byte> output, int outputOffset, int stride, ReadOnlySpan<short> data)
    {
        ValidateInputs1(output, outputOffset, stride, data);
        ReadOnlySpan<Vector128<short>> d = MemoryMarshal.Cast<short, Vector128<short>>(data.Slice(0, 64));

        Vector128<short> row0 = d[0];
        Vector128<short> row1 = d[1];
        Vector128<short> row2 = d[2];
        Vector128<short> row3 = d[3];
        Vector128<short> row4 = d[4];
        Vector128<short> row5 = d[5];
        Vector128<short> row6 = d[6];
        Vector128<short> row7 = d[7];

        IdctBlockCoreAdvSimd(ref row0, ref row1, ref row2, ref row3, ref row4, ref row5, ref row6, ref row7, output, outputOffset, stride);
    }

    private static void DequantAndIdctBlockAdvSimd(
        Span<byte> output,
        int outputOffset,
        int stride,
        ReadOnlySpan<short> coeff,
        ReadOnlySpan<ushort> dequant)
    {
        ValidateInputs1(output, outputOffset, stride, coeff);
        if (dequant.Length < 64)
        {
            ThrowDestinationTooSmall();
        }

        ReadOnlySpan<Vector128<short>> d = MemoryMarshal.Cast<short, Vector128<short>>(coeff.Slice(0, 64));
        ReadOnlySpan<Vector128<short>> dq = MemoryMarshal.Cast<ushort, Vector128<short>>(dequant.Slice(0, 64));

        Vector128<short> row0 = AdvSimd.Multiply(d[0], dq[0]);
        Vector128<short> row1 = AdvSimd.Multiply(d[1], dq[1]);
        Vector128<short> row2 = AdvSimd.Multiply(d[2], dq[2]);
        Vector128<short> row3 = AdvSimd.Multiply(d[3], dq[3]);
        Vector128<short> row4 = AdvSimd.Multiply(d[4], dq[4]);
        Vector128<short> row5 = AdvSimd.Multiply(d[5], dq[5]);
        Vector128<short> row6 = AdvSimd.Multiply(d[6], dq[6]);
        Vector128<short> row7 = AdvSimd.Multiply(d[7], dq[7]);

        IdctBlockCoreAdvSimd(ref row0, ref row1, ref row2, ref row3, ref row4, ref row5, ref row6, ref row7, output, outputOffset, stride);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void IdctBlockCoreAdvSimd(
        ref Vector128<short> row0,
        ref Vector128<short> row1,
        ref Vector128<short> row2,
        ref Vector128<short> row3,
        ref Vector128<short> row4,
        ref Vector128<short> row5,
        ref Vector128<short> row6,
        ref Vector128<short> row7,
        Span<byte> output,
        int outputOffset,
        int stride)
    {
        DctPassAdvSimd(
            ref row0, ref row1, ref row2, ref row3, ref row4, ref row5, ref row6, ref row7,
            Vector128.Create(ColumnBias), isRowPass: false);

        // 16-bit 8x8 transpose via ZipLow/ZipHigh (matches SSE2 UnpackLow/UnpackHigh)
        Zip16AdvSimd(ref row0, ref row4);
        Zip16AdvSimd(ref row1, ref row5);
        Zip16AdvSimd(ref row2, ref row6);
        Zip16AdvSimd(ref row3, ref row7);

        Zip16AdvSimd(ref row0, ref row2);
        Zip16AdvSimd(ref row1, ref row3);
        Zip16AdvSimd(ref row4, ref row6);
        Zip16AdvSimd(ref row5, ref row7);

        Zip16AdvSimd(ref row0, ref row1);
        Zip16AdvSimd(ref row2, ref row3);
        Zip16AdvSimd(ref row4, ref row5);
        Zip16AdvSimd(ref row6, ref row7);

        DctPassAdvSimd(
            ref row0, ref row1, ref row2, ref row3, ref row4, ref row5, ref row6, ref row7,
            Vector128.Create(RowBias), isRowPass: true);

        Vector128<byte> p0 = AdvSimd.ExtractNarrowingSaturateUnsignedUpper(AdvSimd.ExtractNarrowingSaturateUnsignedLower(row0), row1);
        Vector128<byte> p1 = AdvSimd.ExtractNarrowingSaturateUnsignedUpper(AdvSimd.ExtractNarrowingSaturateUnsignedLower(row2), row3);
        Vector128<byte> p2 = AdvSimd.ExtractNarrowingSaturateUnsignedUpper(AdvSimd.ExtractNarrowingSaturateUnsignedLower(row4), row5);
        Vector128<byte> p3 = AdvSimd.ExtractNarrowingSaturateUnsignedUpper(AdvSimd.ExtractNarrowingSaturateUnsignedLower(row6), row7);

        Zip8AdvSimd(ref p0, ref p2);
        Zip8AdvSimd(ref p1, ref p3);

        Zip8AdvSimd(ref p0, ref p1);
        Zip8AdvSimd(ref p2, ref p3);

        Zip8AdvSimd(ref p0, ref p2);
        Zip8AdvSimd(ref p1, ref p3);

        Vector128<ulong> u0 = p0.AsUInt64();
        Vector128<ulong> u1 = p1.AsUInt64();
        Vector128<ulong> u2 = p2.AsUInt64();
        Vector128<ulong> u3 = p3.AsUInt64();

        StoreBlockRows(
            output, outputOffset, stride,
            u0.GetElement(0), u0.GetElement(1),
            u2.GetElement(0), u2.GetElement(1),
            u1.GetElement(0), u1.GetElement(1),
            u3.GetElement(0), u3.GetElement(1));
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void DctPassAdvSimd(
        ref Vector128<short> row0,
        ref Vector128<short> row1,
        ref Vector128<short> row2,
        ref Vector128<short> row3,
        ref Vector128<short> row4,
        ref Vector128<short> row5,
        ref Vector128<short> row6,
        ref Vector128<short> row7,
        Vector128<int> bias,
        bool isRowPass)
    {
        Vector128<short> c0_0 = Vector128.Create((short)C_0_5411961);
        Vector128<short> c0_1 = Vector128.Create((short)C_M1_8477591);
        Vector128<short> c0_2 = Vector128.Create((short)C_0_7653669);
        Vector128<short> c1_0 = Vector128.Create((short)C_1_1758756);
        Vector128<short> c1_1 = Vector128.Create((short)C_M0_8999762);
        Vector128<short> c1_2 = Vector128.Create((short)C_M2_5629154);
        Vector128<short> c2_0 = Vector128.Create((short)C_M1_9615706);
        Vector128<short> c2_1 = Vector128.Create((short)C_M0_3901806);
        Vector128<short> c3_0 = Vector128.Create((short)C_0_2986313);
        Vector128<short> c3_1 = Vector128.Create((short)C_2_0531199);
        Vector128<short> c3_2 = Vector128.Create((short)C_3_0727110);
        Vector128<short> c3_3 = Vector128.Create((short)C_1_5013211);

        // Even part
        Vector128<short> sum26 = AdvSimd.Add(row2, row6);
        Vector128<int> p1eL = AdvSimd.MultiplyWideningLower(sum26.GetLower(), c0_0.GetLower());
        Vector128<int> p1eH = AdvSimd.MultiplyWideningUpper(sum26, c0_0);
        Vector128<int> t2eL = AdvSimd.MultiplyWideningLowerAndAdd(p1eL, row6.GetLower(), c0_1.GetLower());
        Vector128<int> t2eH = AdvSimd.MultiplyWideningUpperAndAdd(p1eH, row6, c0_1);
        Vector128<int> t3eL = AdvSimd.MultiplyWideningLowerAndAdd(p1eL, row2.GetLower(), c0_2.GetLower());
        Vector128<int> t3eH = AdvSimd.MultiplyWideningUpperAndAdd(p1eH, row2, c0_2);

        Vector128<short> sum04 = AdvSimd.Add(row0, row4);
        Vector128<short> dif04 = AdvSimd.Subtract(row0, row4);
        Vector128<int> t0eL = AdvSimd.ShiftLeftLogicalWideningLower(sum04.GetLower(), 12);
        Vector128<int> t0eH = AdvSimd.ShiftLeftLogicalWideningUpper(sum04, 12);
        Vector128<int> t1eL = AdvSimd.ShiftLeftLogicalWideningLower(dif04.GetLower(), 12);
        Vector128<int> t1eH = AdvSimd.ShiftLeftLogicalWideningUpper(dif04, 12);

        Vector128<int> x0L = AdvSimd.Add(AdvSimd.Add(t0eL, t3eL), bias);
        Vector128<int> x0H = AdvSimd.Add(AdvSimd.Add(t0eH, t3eH), bias);
        Vector128<int> x3L = AdvSimd.Add(AdvSimd.Subtract(t0eL, t3eL), bias);
        Vector128<int> x3H = AdvSimd.Add(AdvSimd.Subtract(t0eH, t3eH), bias);
        Vector128<int> x1L = AdvSimd.Add(AdvSimd.Add(t1eL, t2eL), bias);
        Vector128<int> x1H = AdvSimd.Add(AdvSimd.Add(t1eH, t2eH), bias);
        Vector128<int> x2L = AdvSimd.Add(AdvSimd.Subtract(t1eL, t2eL), bias);
        Vector128<int> x2H = AdvSimd.Add(AdvSimd.Subtract(t1eH, t2eH), bias);

        // Odd part
        Vector128<short> sum15 = AdvSimd.Add(row1, row5);
        Vector128<short> sum17 = AdvSimd.Add(row1, row7);
        Vector128<short> sum35 = AdvSimd.Add(row3, row5);
        Vector128<short> sum37 = AdvSimd.Add(row3, row7);
        Vector128<short> sumOdd = AdvSimd.Add(sum17, sum35);

        Vector128<int> p5oL = AdvSimd.MultiplyWideningLower(sumOdd.GetLower(), c1_0.GetLower());
        Vector128<int> p5oH = AdvSimd.MultiplyWideningUpper(sumOdd, c1_0);
        Vector128<int> p1oL = AdvSimd.MultiplyWideningLowerAndAdd(p5oL, sum17.GetLower(), c1_1.GetLower());
        Vector128<int> p1oH = AdvSimd.MultiplyWideningUpperAndAdd(p5oH, sum17, c1_1);
        Vector128<int> p2oL = AdvSimd.MultiplyWideningLowerAndAdd(p5oL, sum35.GetLower(), c1_2.GetLower());
        Vector128<int> p2oH = AdvSimd.MultiplyWideningUpperAndAdd(p5oH, sum35, c1_2);

        Vector128<int> p3oL = AdvSimd.MultiplyWideningLower(sum37.GetLower(), c2_0.GetLower());
        Vector128<int> p3oH = AdvSimd.MultiplyWideningUpper(sum37, c2_0);
        Vector128<int> p4oL = AdvSimd.MultiplyWideningLower(sum15.GetLower(), c2_1.GetLower());
        Vector128<int> p4oH = AdvSimd.MultiplyWideningUpper(sum15, c2_1);

        Vector128<int> x4L = AdvSimd.MultiplyWideningLowerAndAdd(AdvSimd.Add(p1oL, p3oL), row7.GetLower(), c3_0.GetLower());
        Vector128<int> x4H = AdvSimd.MultiplyWideningUpperAndAdd(AdvSimd.Add(p1oH, p3oH), row7, c3_0);
        Vector128<int> x5L = AdvSimd.MultiplyWideningLowerAndAdd(AdvSimd.Add(p2oL, p4oL), row5.GetLower(), c3_1.GetLower());
        Vector128<int> x5H = AdvSimd.MultiplyWideningUpperAndAdd(AdvSimd.Add(p2oH, p4oH), row5, c3_1);
        Vector128<int> x6L = AdvSimd.MultiplyWideningLowerAndAdd(AdvSimd.Add(p2oL, p3oL), row3.GetLower(), c3_2.GetLower());
        Vector128<int> x6H = AdvSimd.MultiplyWideningUpperAndAdd(AdvSimd.Add(p2oH, p3oH), row3, c3_2);
        Vector128<int> x7L = AdvSimd.MultiplyWideningLowerAndAdd(AdvSimd.Add(p1oL, p4oL), row1.GetLower(), c3_3.GetLower());
        Vector128<int> x7H = AdvSimd.MultiplyWideningUpperAndAdd(AdvSimd.Add(p1oH, p4oH), row1, c3_3);

        if (isRowPass)
        {
            row0 = PackShifted17AdvSimd(AdvSimd.Add(x0L, x7L), AdvSimd.Add(x0H, x7H));
            row7 = PackShifted17AdvSimd(AdvSimd.Subtract(x0L, x7L), AdvSimd.Subtract(x0H, x7H));
            row1 = PackShifted17AdvSimd(AdvSimd.Add(x1L, x6L), AdvSimd.Add(x1H, x6H));
            row6 = PackShifted17AdvSimd(AdvSimd.Subtract(x1L, x6L), AdvSimd.Subtract(x1H, x6H));
            row2 = PackShifted17AdvSimd(AdvSimd.Add(x2L, x5L), AdvSimd.Add(x2H, x5H));
            row5 = PackShifted17AdvSimd(AdvSimd.Subtract(x2L, x5L), AdvSimd.Subtract(x2H, x5H));
            row3 = PackShifted17AdvSimd(AdvSimd.Add(x3L, x4L), AdvSimd.Add(x3H, x4H));
            row4 = PackShifted17AdvSimd(AdvSimd.Subtract(x3L, x4L), AdvSimd.Subtract(x3H, x4H));
        }
        else
        {
            row0 = PackShifted10AdvSimd(AdvSimd.Add(x0L, x7L), AdvSimd.Add(x0H, x7H));
            row7 = PackShifted10AdvSimd(AdvSimd.Subtract(x0L, x7L), AdvSimd.Subtract(x0H, x7H));
            row1 = PackShifted10AdvSimd(AdvSimd.Add(x1L, x6L), AdvSimd.Add(x1H, x6H));
            row6 = PackShifted10AdvSimd(AdvSimd.Subtract(x1L, x6L), AdvSimd.Subtract(x1H, x6H));
            row2 = PackShifted10AdvSimd(AdvSimd.Add(x2L, x5L), AdvSimd.Add(x2H, x5H));
            row5 = PackShifted10AdvSimd(AdvSimd.Subtract(x2L, x5L), AdvSimd.Subtract(x2H, x5H));
            row3 = PackShifted10AdvSimd(AdvSimd.Add(x3L, x4L), AdvSimd.Add(x3H, x4H));
            row4 = PackShifted10AdvSimd(AdvSimd.Subtract(x3L, x4L), AdvSimd.Subtract(x3H, x4H));
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector128<short> PackShifted10AdvSimd(Vector128<int> lo, Vector128<int> hi)
    {
        Vector64<short> l = AdvSimd.ExtractNarrowingSaturateLower(AdvSimd.ShiftRightArithmetic(lo, 10));
        return AdvSimd.ExtractNarrowingSaturateUpper(l, AdvSimd.ShiftRightArithmetic(hi, 10));
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector128<short> PackShifted17AdvSimd(Vector128<int> lo, Vector128<int> hi)
    {
        Vector64<short> l = AdvSimd.ExtractNarrowingSaturateLower(AdvSimd.ShiftRightArithmetic(lo, 17));
        return AdvSimd.ExtractNarrowingSaturateUpper(l, AdvSimd.ShiftRightArithmetic(hi, 17));
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void Zip16AdvSimd(ref Vector128<short> a, ref Vector128<short> b)
    {
        Vector128<short> tmp = a;
        a = AdvSimd.Arm64.ZipLow(tmp, b);
        b = AdvSimd.Arm64.ZipHigh(tmp, b);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void Zip8AdvSimd(ref Vector128<byte> a, ref Vector128<byte> b)
    {
        Vector128<byte> tmp = a;
        a = AdvSimd.Arm64.ZipLow(tmp, b);
        b = AdvSimd.Arm64.ZipHigh(tmp, b);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void ValidateInputs1(Span<byte> output, int outputOffset, int stride, ReadOnlySpan<short> data)
    {
        if (data.Length < 64 || (uint)outputOffset > (uint)output.Length || (long)outputOffset + ((long)stride * 7) + 8 > output.Length)
        {
            ThrowDestinationTooSmall();
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void ValidateInputs2(
        Span<byte> out0,
        int off0,
        int stride0,
        ReadOnlySpan<short> data0,
        Span<byte> out1,
        int off1,
        int stride1,
        ReadOnlySpan<short> data1)
    {
        if (data0.Length < 64
            || data1.Length < 64
            || (uint)off0 > (uint)out0.Length
            || (long)off0 + ((long)stride0 * 7) + 8 > out0.Length
            || (uint)off1 > (uint)out1.Length
            || (long)off1 + ((long)stride1 * 7) + 8 > out1.Length)
        {
            ThrowDestinationTooSmall();
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void StoreBlockRows(
        Span<byte> output,
        int outputOffset,
        int stride,
        ulong r0,
        ulong r1,
        ulong r2,
        ulong r3,
        ulong r4,
        ulong r5,
        ulong r6,
        ulong r7)
    {
        Span<byte> dst = output.Slice(outputOffset);
        BinaryPrimitives.WriteUInt64LittleEndian(dst.Slice(0, 8), r0);
        BinaryPrimitives.WriteUInt64LittleEndian(dst.Slice(stride, 8), r1);
        BinaryPrimitives.WriteUInt64LittleEndian(dst.Slice(stride * 2, 8), r2);
        BinaryPrimitives.WriteUInt64LittleEndian(dst.Slice(stride * 3, 8), r3);
        BinaryPrimitives.WriteUInt64LittleEndian(dst.Slice(stride * 4, 8), r4);
        BinaryPrimitives.WriteUInt64LittleEndian(dst.Slice(stride * 5, 8), r5);
        BinaryPrimitives.WriteUInt64LittleEndian(dst.Slice(stride * 6, 8), r6);
        BinaryPrimitives.WriteUInt64LittleEndian(dst.Slice(stride * 7, 8), r7);
    }
#endif

    private static void ThrowDestinationTooSmall() =>
        throw new StbException(StbErrorCode.InternalError, "IDCT buffer bounds check failed.");
}
