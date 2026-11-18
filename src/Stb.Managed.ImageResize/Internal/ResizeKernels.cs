using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
#if NET8_0_OR_GREATER
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.Arm;
using System.Runtime.Intrinsics.X86;
#endif

namespace Stb.Managed.Internal;

/// <summary>
/// Safe-span SIMD resampling kernels (vertical gather 1..8, vertical scatter, horizontal gather for 1/2/3/4/7
/// effective channels) across AVX-512F, AVX2/AVX, SSE2/SSE, Arm64 AdvSimd, and portable Vector&lt;float&gt;
/// (netstandard2.1). Preserves strict left-to-right IEEE-754 summation order (zero FMA, zero reassociation)
/// while extracting 2x ILP by unrolling across independent output lanes/pixels.
/// </summary>
internal static class ResizeKernels
{
    // Extra padding floats required at the end of row buffers so 7-channel AVX loads (8 floats)
    // and 3-channel 128-bit loads (4 floats) never read past the rented span.
    internal const int RowPaddingFloats = 4;

    // =========================================================================
    // Vertical Gather (1..8 scanlines per fused pass)
    // =========================================================================

    internal static void ResampleVerticalGather(
        Span<float> output,
        ReadOnlySpan<float> ringBuffer,
        int ringRowStrideFloats,
        int widthFloats,
        int ringFirstScanline,
        int ringBeginIndex,
        int ringEntries,
        int n0,
        int n1,
        ReadOnlySpan<float> coeffs)
    {
        int total = n1 - n0 + 1;
        int k = 0;
        bool firstGroup = true;

        while (total > 0)
        {
            int n = Math.Min(total, 8);
            int o0 = GetRingSlot(n0 + k, ringFirstScanline, ringBeginIndex, ringEntries) * ringRowStrideFloats;

            if (firstGroup)
            {
                firstGroup = false;
                switch (n)
                {
                    case 1:
                        VerticalSet1(output, ringBuffer.Slice(o0, widthFloats), coeffs[k], widthFloats);
                        break;
                    case 2:
                        VerticalSet2(
                            output,
                            ringBuffer.Slice(o0, widthFloats),
                            ringBuffer.Slice(GetRingSlot(n0 + k + 1, ringFirstScanline, ringBeginIndex, ringEntries) * ringRowStrideFloats, widthFloats),
                            coeffs[k], coeffs[k + 1], widthFloats);
                        break;
                    case 3:
                        VerticalSet3(
                            output,
                            ringBuffer.Slice(o0, widthFloats),
                            ringBuffer.Slice(GetRingSlot(n0 + k + 1, ringFirstScanline, ringBeginIndex, ringEntries) * ringRowStrideFloats, widthFloats),
                            ringBuffer.Slice(GetRingSlot(n0 + k + 2, ringFirstScanline, ringBeginIndex, ringEntries) * ringRowStrideFloats, widthFloats),
                            coeffs[k], coeffs[k + 1], coeffs[k + 2], widthFloats);
                        break;
                    case 4:
                        VerticalSet4(
                            output,
                            ringBuffer.Slice(o0, widthFloats),
                            ringBuffer.Slice(GetRingSlot(n0 + k + 1, ringFirstScanline, ringBeginIndex, ringEntries) * ringRowStrideFloats, widthFloats),
                            ringBuffer.Slice(GetRingSlot(n0 + k + 2, ringFirstScanline, ringBeginIndex, ringEntries) * ringRowStrideFloats, widthFloats),
                            ringBuffer.Slice(GetRingSlot(n0 + k + 3, ringFirstScanline, ringBeginIndex, ringEntries) * ringRowStrideFloats, widthFloats),
                            coeffs[k], coeffs[k + 1], coeffs[k + 2], coeffs[k + 3], widthFloats);
                        break;
                    case 8:
                        VerticalSet8(
                            output,
                            ringBuffer.Slice(o0, widthFloats),
                            ringBuffer.Slice(GetRingSlot(n0 + k + 1, ringFirstScanline, ringBeginIndex, ringEntries) * ringRowStrideFloats, widthFloats),
                            ringBuffer.Slice(GetRingSlot(n0 + k + 2, ringFirstScanline, ringBeginIndex, ringEntries) * ringRowStrideFloats, widthFloats),
                            ringBuffer.Slice(GetRingSlot(n0 + k + 3, ringFirstScanline, ringBeginIndex, ringEntries) * ringRowStrideFloats, widthFloats),
                            ringBuffer.Slice(GetRingSlot(n0 + k + 4, ringFirstScanline, ringBeginIndex, ringEntries) * ringRowStrideFloats, widthFloats),
                            ringBuffer.Slice(GetRingSlot(n0 + k + 5, ringFirstScanline, ringBeginIndex, ringEntries) * ringRowStrideFloats, widthFloats),
                            ringBuffer.Slice(GetRingSlot(n0 + k + 6, ringFirstScanline, ringBeginIndex, ringEntries) * ringRowStrideFloats, widthFloats),
                            ringBuffer.Slice(GetRingSlot(n0 + k + 7, ringFirstScanline, ringBeginIndex, ringEntries) * ringRowStrideFloats, widthFloats),
                            coeffs[k], coeffs[k + 1], coeffs[k + 2], coeffs[k + 3],
                            coeffs[k + 4], coeffs[k + 5], coeffs[k + 6], coeffs[k + 7],
                            widthFloats);
                        break;
                    default:
                        VerticalSet4(
                            output,
                            ringBuffer.Slice(o0, widthFloats),
                            ringBuffer.Slice(GetRingSlot(n0 + k + 1, ringFirstScanline, ringBeginIndex, ringEntries) * ringRowStrideFloats, widthFloats),
                            ringBuffer.Slice(GetRingSlot(n0 + k + 2, ringFirstScanline, ringBeginIndex, ringEntries) * ringRowStrideFloats, widthFloats),
                            ringBuffer.Slice(GetRingSlot(n0 + k + 3, ringFirstScanline, ringBeginIndex, ringEntries) * ringRowStrideFloats, widthFloats),
                            coeffs[k], coeffs[k + 1], coeffs[k + 2], coeffs[k + 3], widthFloats);
                        AddRemainingRows(
                            output, ringBuffer, ringRowStrideFloats, widthFloats,
                            ringFirstScanline, ringBeginIndex, ringEntries,
                            n0 + k + 4, n - 4, coeffs.Slice(k + 4));
                        break;
                }
            }
            else if (n == 8)
            {
                VerticalAdd8(
                    output,
                    ringBuffer.Slice(o0, widthFloats),
                    ringBuffer.Slice(GetRingSlot(n0 + k + 1, ringFirstScanline, ringBeginIndex, ringEntries) * ringRowStrideFloats, widthFloats),
                    ringBuffer.Slice(GetRingSlot(n0 + k + 2, ringFirstScanline, ringBeginIndex, ringEntries) * ringRowStrideFloats, widthFloats),
                    ringBuffer.Slice(GetRingSlot(n0 + k + 3, ringFirstScanline, ringBeginIndex, ringEntries) * ringRowStrideFloats, widthFloats),
                    ringBuffer.Slice(GetRingSlot(n0 + k + 4, ringFirstScanline, ringBeginIndex, ringEntries) * ringRowStrideFloats, widthFloats),
                    ringBuffer.Slice(GetRingSlot(n0 + k + 5, ringFirstScanline, ringBeginIndex, ringEntries) * ringRowStrideFloats, widthFloats),
                    ringBuffer.Slice(GetRingSlot(n0 + k + 6, ringFirstScanline, ringBeginIndex, ringEntries) * ringRowStrideFloats, widthFloats),
                    ringBuffer.Slice(GetRingSlot(n0 + k + 7, ringFirstScanline, ringBeginIndex, ringEntries) * ringRowStrideFloats, widthFloats),
                    coeffs[k], coeffs[k + 1], coeffs[k + 2], coeffs[k + 3],
                    coeffs[k + 4], coeffs[k + 5], coeffs[k + 6], coeffs[k + 7],
                    widthFloats);
            }
            else
            {
                AddRemainingRows(
                    output, ringBuffer, ringRowStrideFloats, widthFloats,
                    ringFirstScanline, ringBeginIndex, ringEntries,
                    n0 + k, n, coeffs.Slice(k));
            }

            k += n;
            total -= n;
        }
    }

    private static void AddRemainingRows(
        Span<float> output,
        ReadOnlySpan<float> ringBuffer,
        int ringRowStrideFloats,
        int widthFloats,
        int ringFirstScanline,
        int ringBeginIndex,
        int ringEntries,
        int startScanline,
        int count,
        ReadOnlySpan<float> coeffs)
    {
        int idx = 0;
        while (count >= 4)
        {
            VerticalAdd4(
                output,
                ringBuffer.Slice(GetRingSlot(startScanline + idx, ringFirstScanline, ringBeginIndex, ringEntries) * ringRowStrideFloats, widthFloats),
                ringBuffer.Slice(GetRingSlot(startScanline + idx + 1, ringFirstScanline, ringBeginIndex, ringEntries) * ringRowStrideFloats, widthFloats),
                ringBuffer.Slice(GetRingSlot(startScanline + idx + 2, ringFirstScanline, ringBeginIndex, ringEntries) * ringRowStrideFloats, widthFloats),
                ringBuffer.Slice(GetRingSlot(startScanline + idx + 3, ringFirstScanline, ringBeginIndex, ringEntries) * ringRowStrideFloats, widthFloats),
                coeffs[idx], coeffs[idx + 1], coeffs[idx + 2], coeffs[idx + 3],
                widthFloats);
            idx += 4;
            count -= 4;
        }

        switch (count)
        {
            case 1:
                VerticalAdd1(
                    output,
                    ringBuffer.Slice(GetRingSlot(startScanline + idx, ringFirstScanline, ringBeginIndex, ringEntries) * ringRowStrideFloats, widthFloats),
                    coeffs[idx], widthFloats);
                break;
            case 2:
                VerticalAdd2(
                    output,
                    ringBuffer.Slice(GetRingSlot(startScanline + idx, ringFirstScanline, ringBeginIndex, ringEntries) * ringRowStrideFloats, widthFloats),
                    ringBuffer.Slice(GetRingSlot(startScanline + idx + 1, ringFirstScanline, ringBeginIndex, ringEntries) * ringRowStrideFloats, widthFloats),
                    coeffs[idx], coeffs[idx + 1], widthFloats);
                break;
            case 3:
                VerticalAdd3(
                    output,
                    ringBuffer.Slice(GetRingSlot(startScanline + idx, ringFirstScanline, ringBeginIndex, ringEntries) * ringRowStrideFloats, widthFloats),
                    ringBuffer.Slice(GetRingSlot(startScanline + idx + 1, ringFirstScanline, ringBeginIndex, ringEntries) * ringRowStrideFloats, widthFloats),
                    ringBuffer.Slice(GetRingSlot(startScanline + idx + 2, ringFirstScanline, ringBeginIndex, ringEntries) * ringRowStrideFloats, widthFloats),
                    coeffs[idx], coeffs[idx + 1], coeffs[idx + 2], widthFloats);
                break;
        }
    }

    // Vertical kernels: strict left-to-right sums; rows are sliced to one common length before the
    // vector cast so the JIT can prove every access in range.
    internal static void VerticalSet1(Span<float> dst, ReadOnlySpan<float> r0, float c0, int count)
    {
        int i = 0;
#if NET8_0_OR_GREATER
        if (Vector512.IsHardwareAccelerated && count >= Vector512<float>.Count)
        {
            int len = (count - i) / Vector512<float>.Count * Vector512<float>.Count;
            Span<Vector512<float>> d = MemoryMarshal.Cast<float, Vector512<float>>(dst.Slice(i, len));
            ReadOnlySpan<Vector512<float>> v_r0 = MemoryMarshal.Cast<float, Vector512<float>>(r0.Slice(i, len));
            Vector512<float> v0 = Vector512.Create(c0);
            for (int k = 0; k < d.Length; k++)
            {
                d[k] = v_r0[k] * v0;
            }

            i += len;
        }

#endif
        if (Vector.IsHardwareAccelerated && count - i >= Vector<float>.Count)
        {
            int len = (count - i) / Vector<float>.Count * Vector<float>.Count;
            Span<Vector<float>> d = MemoryMarshal.Cast<float, Vector<float>>(dst.Slice(i, len));
            ReadOnlySpan<Vector<float>> v_r0 = MemoryMarshal.Cast<float, Vector<float>>(r0.Slice(i, len));
            Vector<float> v0 = new Vector<float>(c0);
            for (int k = 0; k < d.Length; k++)
            {
                d[k] = v_r0[k] * v0;
            }

            i += len;
        }

        for (; i < count; i++)
        {
            dst[i] = r0[i] * c0;
        }
    }

    internal static void VerticalAdd1(Span<float> dst, ReadOnlySpan<float> r0, float c0, int count)
    {
        int i = 0;
#if NET8_0_OR_GREATER
        if (Vector512.IsHardwareAccelerated && count >= Vector512<float>.Count)
        {
            int len = (count - i) / Vector512<float>.Count * Vector512<float>.Count;
            Span<Vector512<float>> d = MemoryMarshal.Cast<float, Vector512<float>>(dst.Slice(i, len));
            ReadOnlySpan<Vector512<float>> v_r0 = MemoryMarshal.Cast<float, Vector512<float>>(r0.Slice(i, len));
            Vector512<float> v0 = Vector512.Create(c0);
            for (int k = 0; k < d.Length; k++)
            {
                d[k] = d[k] + (v_r0[k] * v0);
            }

            i += len;
        }

#endif
        if (Vector.IsHardwareAccelerated && count - i >= Vector<float>.Count)
        {
            int len = (count - i) / Vector<float>.Count * Vector<float>.Count;
            Span<Vector<float>> d = MemoryMarshal.Cast<float, Vector<float>>(dst.Slice(i, len));
            ReadOnlySpan<Vector<float>> v_r0 = MemoryMarshal.Cast<float, Vector<float>>(r0.Slice(i, len));
            Vector<float> v0 = new Vector<float>(c0);
            for (int k = 0; k < d.Length; k++)
            {
                d[k] = d[k] + (v_r0[k] * v0);
            }

            i += len;
        }

        for (; i < count; i++)
        {
            dst[i] = dst[i] + (r0[i] * c0);
        }
    }

    private static void VerticalSet2(Span<float> dst, ReadOnlySpan<float> r0, ReadOnlySpan<float> r1, float c0, float c1, int count)
    {
        int i = 0;
#if NET8_0_OR_GREATER
        if (Vector512.IsHardwareAccelerated && count >= Vector512<float>.Count)
        {
            int len = (count - i) / Vector512<float>.Count * Vector512<float>.Count;
            Span<Vector512<float>> d = MemoryMarshal.Cast<float, Vector512<float>>(dst.Slice(i, len));
            ReadOnlySpan<Vector512<float>> v_r0 = MemoryMarshal.Cast<float, Vector512<float>>(r0.Slice(i, len));
            ReadOnlySpan<Vector512<float>> v_r1 = MemoryMarshal.Cast<float, Vector512<float>>(r1.Slice(i, len));
            Vector512<float> v0 = Vector512.Create(c0);
            Vector512<float> v1 = Vector512.Create(c1);
            for (int k = 0; k < d.Length; k++)
            {
                d[k] = (v_r0[k] * v0) + (v_r1[k] * v1);
            }

            i += len;
        }

#endif
        if (Vector.IsHardwareAccelerated && count - i >= Vector<float>.Count)
        {
            int len = (count - i) / Vector<float>.Count * Vector<float>.Count;
            Span<Vector<float>> d = MemoryMarshal.Cast<float, Vector<float>>(dst.Slice(i, len));
            ReadOnlySpan<Vector<float>> v_r0 = MemoryMarshal.Cast<float, Vector<float>>(r0.Slice(i, len));
            ReadOnlySpan<Vector<float>> v_r1 = MemoryMarshal.Cast<float, Vector<float>>(r1.Slice(i, len));
            Vector<float> v0 = new Vector<float>(c0);
            Vector<float> v1 = new Vector<float>(c1);
            for (int k = 0; k < d.Length; k++)
            {
                d[k] = (v_r0[k] * v0) + (v_r1[k] * v1);
            }

            i += len;
        }

        for (; i < count; i++)
        {
            dst[i] = (r0[i] * c0) + (r1[i] * c1);
        }
    }

    private static void VerticalAdd2(Span<float> dst, ReadOnlySpan<float> r0, ReadOnlySpan<float> r1, float c0, float c1, int count)
    {
        int i = 0;
#if NET8_0_OR_GREATER
        if (Vector512.IsHardwareAccelerated && count >= Vector512<float>.Count)
        {
            int len = (count - i) / Vector512<float>.Count * Vector512<float>.Count;
            Span<Vector512<float>> d = MemoryMarshal.Cast<float, Vector512<float>>(dst.Slice(i, len));
            ReadOnlySpan<Vector512<float>> v_r0 = MemoryMarshal.Cast<float, Vector512<float>>(r0.Slice(i, len));
            ReadOnlySpan<Vector512<float>> v_r1 = MemoryMarshal.Cast<float, Vector512<float>>(r1.Slice(i, len));
            Vector512<float> v0 = Vector512.Create(c0);
            Vector512<float> v1 = Vector512.Create(c1);
            for (int k = 0; k < d.Length; k++)
            {
                d[k] = (d[k] + (v_r0[k] * v0)) + (v_r1[k] * v1);
            }

            i += len;
        }

#endif
        if (Vector.IsHardwareAccelerated && count - i >= Vector<float>.Count)
        {
            int len = (count - i) / Vector<float>.Count * Vector<float>.Count;
            Span<Vector<float>> d = MemoryMarshal.Cast<float, Vector<float>>(dst.Slice(i, len));
            ReadOnlySpan<Vector<float>> v_r0 = MemoryMarshal.Cast<float, Vector<float>>(r0.Slice(i, len));
            ReadOnlySpan<Vector<float>> v_r1 = MemoryMarshal.Cast<float, Vector<float>>(r1.Slice(i, len));
            Vector<float> v0 = new Vector<float>(c0);
            Vector<float> v1 = new Vector<float>(c1);
            for (int k = 0; k < d.Length; k++)
            {
                d[k] = (d[k] + (v_r0[k] * v0)) + (v_r1[k] * v1);
            }

            i += len;
        }

        for (; i < count; i++)
        {
            dst[i] = (dst[i] + (r0[i] * c0)) + (r1[i] * c1);
        }
    }

    private static void VerticalSet3(Span<float> dst, ReadOnlySpan<float> r0, ReadOnlySpan<float> r1, ReadOnlySpan<float> r2, float c0, float c1, float c2, int count)
    {
        int i = 0;
#if NET8_0_OR_GREATER
        if (Vector512.IsHardwareAccelerated && count >= Vector512<float>.Count)
        {
            int len = (count - i) / Vector512<float>.Count * Vector512<float>.Count;
            Span<Vector512<float>> d = MemoryMarshal.Cast<float, Vector512<float>>(dst.Slice(i, len));
            ReadOnlySpan<Vector512<float>> v_r0 = MemoryMarshal.Cast<float, Vector512<float>>(r0.Slice(i, len));
            ReadOnlySpan<Vector512<float>> v_r1 = MemoryMarshal.Cast<float, Vector512<float>>(r1.Slice(i, len));
            ReadOnlySpan<Vector512<float>> v_r2 = MemoryMarshal.Cast<float, Vector512<float>>(r2.Slice(i, len));
            Vector512<float> v0 = Vector512.Create(c0);
            Vector512<float> v1 = Vector512.Create(c1);
            Vector512<float> v2 = Vector512.Create(c2);
            for (int k = 0; k < d.Length; k++)
            {
                d[k] = ((v_r0[k] * v0) + (v_r1[k] * v1)) + (v_r2[k] * v2);
            }

            i += len;
        }

#endif
        if (Vector.IsHardwareAccelerated && count - i >= Vector<float>.Count)
        {
            int len = (count - i) / Vector<float>.Count * Vector<float>.Count;
            Span<Vector<float>> d = MemoryMarshal.Cast<float, Vector<float>>(dst.Slice(i, len));
            ReadOnlySpan<Vector<float>> v_r0 = MemoryMarshal.Cast<float, Vector<float>>(r0.Slice(i, len));
            ReadOnlySpan<Vector<float>> v_r1 = MemoryMarshal.Cast<float, Vector<float>>(r1.Slice(i, len));
            ReadOnlySpan<Vector<float>> v_r2 = MemoryMarshal.Cast<float, Vector<float>>(r2.Slice(i, len));
            Vector<float> v0 = new Vector<float>(c0);
            Vector<float> v1 = new Vector<float>(c1);
            Vector<float> v2 = new Vector<float>(c2);
            for (int k = 0; k < d.Length; k++)
            {
                d[k] = ((v_r0[k] * v0) + (v_r1[k] * v1)) + (v_r2[k] * v2);
            }

            i += len;
        }

        for (; i < count; i++)
        {
            dst[i] = ((r0[i] * c0) + (r1[i] * c1)) + (r2[i] * c2);
        }
    }

    private static void VerticalAdd3(Span<float> dst, ReadOnlySpan<float> r0, ReadOnlySpan<float> r1, ReadOnlySpan<float> r2, float c0, float c1, float c2, int count)
    {
        int i = 0;
#if NET8_0_OR_GREATER
        if (Vector512.IsHardwareAccelerated && count >= Vector512<float>.Count)
        {
            int len = (count - i) / Vector512<float>.Count * Vector512<float>.Count;
            Span<Vector512<float>> d = MemoryMarshal.Cast<float, Vector512<float>>(dst.Slice(i, len));
            ReadOnlySpan<Vector512<float>> v_r0 = MemoryMarshal.Cast<float, Vector512<float>>(r0.Slice(i, len));
            ReadOnlySpan<Vector512<float>> v_r1 = MemoryMarshal.Cast<float, Vector512<float>>(r1.Slice(i, len));
            ReadOnlySpan<Vector512<float>> v_r2 = MemoryMarshal.Cast<float, Vector512<float>>(r2.Slice(i, len));
            Vector512<float> v0 = Vector512.Create(c0);
            Vector512<float> v1 = Vector512.Create(c1);
            Vector512<float> v2 = Vector512.Create(c2);
            for (int k = 0; k < d.Length; k++)
            {
                d[k] = ((d[k] + (v_r0[k] * v0)) + (v_r1[k] * v1)) + (v_r2[k] * v2);
            }

            i += len;
        }

#endif
        if (Vector.IsHardwareAccelerated && count - i >= Vector<float>.Count)
        {
            int len = (count - i) / Vector<float>.Count * Vector<float>.Count;
            Span<Vector<float>> d = MemoryMarshal.Cast<float, Vector<float>>(dst.Slice(i, len));
            ReadOnlySpan<Vector<float>> v_r0 = MemoryMarshal.Cast<float, Vector<float>>(r0.Slice(i, len));
            ReadOnlySpan<Vector<float>> v_r1 = MemoryMarshal.Cast<float, Vector<float>>(r1.Slice(i, len));
            ReadOnlySpan<Vector<float>> v_r2 = MemoryMarshal.Cast<float, Vector<float>>(r2.Slice(i, len));
            Vector<float> v0 = new Vector<float>(c0);
            Vector<float> v1 = new Vector<float>(c1);
            Vector<float> v2 = new Vector<float>(c2);
            for (int k = 0; k < d.Length; k++)
            {
                d[k] = ((d[k] + (v_r0[k] * v0)) + (v_r1[k] * v1)) + (v_r2[k] * v2);
            }

            i += len;
        }

        for (; i < count; i++)
        {
            dst[i] = ((dst[i] + (r0[i] * c0)) + (r1[i] * c1)) + (r2[i] * c2);
        }
    }

    private static void VerticalSet4(Span<float> dst, ReadOnlySpan<float> r0, ReadOnlySpan<float> r1, ReadOnlySpan<float> r2, ReadOnlySpan<float> r3, float c0, float c1, float c2, float c3, int count)
    {
        int i = 0;
#if NET8_0_OR_GREATER
        if (Vector512.IsHardwareAccelerated && count >= Vector512<float>.Count)
        {
            int len = (count - i) / Vector512<float>.Count * Vector512<float>.Count;
            Span<Vector512<float>> d = MemoryMarshal.Cast<float, Vector512<float>>(dst.Slice(i, len));
            ReadOnlySpan<Vector512<float>> v_r0 = MemoryMarshal.Cast<float, Vector512<float>>(r0.Slice(i, len));
            ReadOnlySpan<Vector512<float>> v_r1 = MemoryMarshal.Cast<float, Vector512<float>>(r1.Slice(i, len));
            ReadOnlySpan<Vector512<float>> v_r2 = MemoryMarshal.Cast<float, Vector512<float>>(r2.Slice(i, len));
            ReadOnlySpan<Vector512<float>> v_r3 = MemoryMarshal.Cast<float, Vector512<float>>(r3.Slice(i, len));
            Vector512<float> v0 = Vector512.Create(c0);
            Vector512<float> v1 = Vector512.Create(c1);
            Vector512<float> v2 = Vector512.Create(c2);
            Vector512<float> v3 = Vector512.Create(c3);
            for (int k = 0; k < d.Length; k++)
            {
                d[k] = (((v_r0[k] * v0) + (v_r1[k] * v1)) + (v_r2[k] * v2)) + (v_r3[k] * v3);
            }

            i += len;
        }

#endif
        if (Vector.IsHardwareAccelerated && count - i >= Vector<float>.Count)
        {
            int len = (count - i) / Vector<float>.Count * Vector<float>.Count;
            Span<Vector<float>> d = MemoryMarshal.Cast<float, Vector<float>>(dst.Slice(i, len));
            ReadOnlySpan<Vector<float>> v_r0 = MemoryMarshal.Cast<float, Vector<float>>(r0.Slice(i, len));
            ReadOnlySpan<Vector<float>> v_r1 = MemoryMarshal.Cast<float, Vector<float>>(r1.Slice(i, len));
            ReadOnlySpan<Vector<float>> v_r2 = MemoryMarshal.Cast<float, Vector<float>>(r2.Slice(i, len));
            ReadOnlySpan<Vector<float>> v_r3 = MemoryMarshal.Cast<float, Vector<float>>(r3.Slice(i, len));
            Vector<float> v0 = new Vector<float>(c0);
            Vector<float> v1 = new Vector<float>(c1);
            Vector<float> v2 = new Vector<float>(c2);
            Vector<float> v3 = new Vector<float>(c3);
            for (int k = 0; k < d.Length; k++)
            {
                d[k] = (((v_r0[k] * v0) + (v_r1[k] * v1)) + (v_r2[k] * v2)) + (v_r3[k] * v3);
            }

            i += len;
        }

        for (; i < count; i++)
        {
            dst[i] = (((r0[i] * c0) + (r1[i] * c1)) + (r2[i] * c2)) + (r3[i] * c3);
        }
    }

    private static void VerticalAdd4(Span<float> dst, ReadOnlySpan<float> r0, ReadOnlySpan<float> r1, ReadOnlySpan<float> r2, ReadOnlySpan<float> r3, float c0, float c1, float c2, float c3, int count)
    {
        int i = 0;
#if NET8_0_OR_GREATER
        if (Vector512.IsHardwareAccelerated && count >= Vector512<float>.Count)
        {
            int len = (count - i) / Vector512<float>.Count * Vector512<float>.Count;
            Span<Vector512<float>> d = MemoryMarshal.Cast<float, Vector512<float>>(dst.Slice(i, len));
            ReadOnlySpan<Vector512<float>> v_r0 = MemoryMarshal.Cast<float, Vector512<float>>(r0.Slice(i, len));
            ReadOnlySpan<Vector512<float>> v_r1 = MemoryMarshal.Cast<float, Vector512<float>>(r1.Slice(i, len));
            ReadOnlySpan<Vector512<float>> v_r2 = MemoryMarshal.Cast<float, Vector512<float>>(r2.Slice(i, len));
            ReadOnlySpan<Vector512<float>> v_r3 = MemoryMarshal.Cast<float, Vector512<float>>(r3.Slice(i, len));
            Vector512<float> v0 = Vector512.Create(c0);
            Vector512<float> v1 = Vector512.Create(c1);
            Vector512<float> v2 = Vector512.Create(c2);
            Vector512<float> v3 = Vector512.Create(c3);
            for (int k = 0; k < d.Length; k++)
            {
                d[k] = (((d[k] + (v_r0[k] * v0)) + (v_r1[k] * v1)) + (v_r2[k] * v2)) + (v_r3[k] * v3);
            }

            i += len;
        }

#endif
        if (Vector.IsHardwareAccelerated && count - i >= Vector<float>.Count)
        {
            int len = (count - i) / Vector<float>.Count * Vector<float>.Count;
            Span<Vector<float>> d = MemoryMarshal.Cast<float, Vector<float>>(dst.Slice(i, len));
            ReadOnlySpan<Vector<float>> v_r0 = MemoryMarshal.Cast<float, Vector<float>>(r0.Slice(i, len));
            ReadOnlySpan<Vector<float>> v_r1 = MemoryMarshal.Cast<float, Vector<float>>(r1.Slice(i, len));
            ReadOnlySpan<Vector<float>> v_r2 = MemoryMarshal.Cast<float, Vector<float>>(r2.Slice(i, len));
            ReadOnlySpan<Vector<float>> v_r3 = MemoryMarshal.Cast<float, Vector<float>>(r3.Slice(i, len));
            Vector<float> v0 = new Vector<float>(c0);
            Vector<float> v1 = new Vector<float>(c1);
            Vector<float> v2 = new Vector<float>(c2);
            Vector<float> v3 = new Vector<float>(c3);
            for (int k = 0; k < d.Length; k++)
            {
                d[k] = (((d[k] + (v_r0[k] * v0)) + (v_r1[k] * v1)) + (v_r2[k] * v2)) + (v_r3[k] * v3);
            }

            i += len;
        }

        for (; i < count; i++)
        {
            dst[i] = (((dst[i] + (r0[i] * c0)) + (r1[i] * c1)) + (r2[i] * c2)) + (r3[i] * c3);
        }
    }

    private static void VerticalSet8(Span<float> dst, ReadOnlySpan<float> r0, ReadOnlySpan<float> r1, ReadOnlySpan<float> r2, ReadOnlySpan<float> r3, ReadOnlySpan<float> r4, ReadOnlySpan<float> r5, ReadOnlySpan<float> r6, ReadOnlySpan<float> r7, float c0, float c1, float c2, float c3, float c4, float c5, float c6, float c7, int count)
    {
        int i = 0;
#if NET8_0_OR_GREATER
        if (Vector512.IsHardwareAccelerated && count >= Vector512<float>.Count)
        {
            int len = (count - i) / Vector512<float>.Count * Vector512<float>.Count;
            Span<Vector512<float>> d = MemoryMarshal.Cast<float, Vector512<float>>(dst.Slice(i, len));
            ReadOnlySpan<Vector512<float>> v_r0 = MemoryMarshal.Cast<float, Vector512<float>>(r0.Slice(i, len));
            ReadOnlySpan<Vector512<float>> v_r1 = MemoryMarshal.Cast<float, Vector512<float>>(r1.Slice(i, len));
            ReadOnlySpan<Vector512<float>> v_r2 = MemoryMarshal.Cast<float, Vector512<float>>(r2.Slice(i, len));
            ReadOnlySpan<Vector512<float>> v_r3 = MemoryMarshal.Cast<float, Vector512<float>>(r3.Slice(i, len));
            ReadOnlySpan<Vector512<float>> v_r4 = MemoryMarshal.Cast<float, Vector512<float>>(r4.Slice(i, len));
            ReadOnlySpan<Vector512<float>> v_r5 = MemoryMarshal.Cast<float, Vector512<float>>(r5.Slice(i, len));
            ReadOnlySpan<Vector512<float>> v_r6 = MemoryMarshal.Cast<float, Vector512<float>>(r6.Slice(i, len));
            ReadOnlySpan<Vector512<float>> v_r7 = MemoryMarshal.Cast<float, Vector512<float>>(r7.Slice(i, len));
            Vector512<float> v0 = Vector512.Create(c0);
            Vector512<float> v1 = Vector512.Create(c1);
            Vector512<float> v2 = Vector512.Create(c2);
            Vector512<float> v3 = Vector512.Create(c3);
            Vector512<float> v4 = Vector512.Create(c4);
            Vector512<float> v5 = Vector512.Create(c5);
            Vector512<float> v6 = Vector512.Create(c6);
            Vector512<float> v7 = Vector512.Create(c7);
            for (int k = 0; k < d.Length; k++)
            {
                d[k] = (((((((v_r0[k] * v0) + (v_r1[k] * v1)) + (v_r2[k] * v2)) + (v_r3[k] * v3)) + (v_r4[k] * v4)) + (v_r5[k] * v5)) + (v_r6[k] * v6)) + (v_r7[k] * v7);
            }

            i += len;
        }

#endif
        if (Vector.IsHardwareAccelerated && count - i >= Vector<float>.Count)
        {
            int len = (count - i) / Vector<float>.Count * Vector<float>.Count;
            Span<Vector<float>> d = MemoryMarshal.Cast<float, Vector<float>>(dst.Slice(i, len));
            ReadOnlySpan<Vector<float>> v_r0 = MemoryMarshal.Cast<float, Vector<float>>(r0.Slice(i, len));
            ReadOnlySpan<Vector<float>> v_r1 = MemoryMarshal.Cast<float, Vector<float>>(r1.Slice(i, len));
            ReadOnlySpan<Vector<float>> v_r2 = MemoryMarshal.Cast<float, Vector<float>>(r2.Slice(i, len));
            ReadOnlySpan<Vector<float>> v_r3 = MemoryMarshal.Cast<float, Vector<float>>(r3.Slice(i, len));
            ReadOnlySpan<Vector<float>> v_r4 = MemoryMarshal.Cast<float, Vector<float>>(r4.Slice(i, len));
            ReadOnlySpan<Vector<float>> v_r5 = MemoryMarshal.Cast<float, Vector<float>>(r5.Slice(i, len));
            ReadOnlySpan<Vector<float>> v_r6 = MemoryMarshal.Cast<float, Vector<float>>(r6.Slice(i, len));
            ReadOnlySpan<Vector<float>> v_r7 = MemoryMarshal.Cast<float, Vector<float>>(r7.Slice(i, len));
            Vector<float> v0 = new Vector<float>(c0);
            Vector<float> v1 = new Vector<float>(c1);
            Vector<float> v2 = new Vector<float>(c2);
            Vector<float> v3 = new Vector<float>(c3);
            Vector<float> v4 = new Vector<float>(c4);
            Vector<float> v5 = new Vector<float>(c5);
            Vector<float> v6 = new Vector<float>(c6);
            Vector<float> v7 = new Vector<float>(c7);
            for (int k = 0; k < d.Length; k++)
            {
                d[k] = (((((((v_r0[k] * v0) + (v_r1[k] * v1)) + (v_r2[k] * v2)) + (v_r3[k] * v3)) + (v_r4[k] * v4)) + (v_r5[k] * v5)) + (v_r6[k] * v6)) + (v_r7[k] * v7);
            }

            i += len;
        }

        for (; i < count; i++)
        {
            dst[i] = (((((((r0[i] * c0) + (r1[i] * c1)) + (r2[i] * c2)) + (r3[i] * c3)) + (r4[i] * c4)) + (r5[i] * c5)) + (r6[i] * c6)) + (r7[i] * c7);
        }
    }

    private static void VerticalAdd8(Span<float> dst, ReadOnlySpan<float> r0, ReadOnlySpan<float> r1, ReadOnlySpan<float> r2, ReadOnlySpan<float> r3, ReadOnlySpan<float> r4, ReadOnlySpan<float> r5, ReadOnlySpan<float> r6, ReadOnlySpan<float> r7, float c0, float c1, float c2, float c3, float c4, float c5, float c6, float c7, int count)
    {
        int i = 0;
#if NET8_0_OR_GREATER
        if (Vector512.IsHardwareAccelerated && count >= Vector512<float>.Count)
        {
            int len = (count - i) / Vector512<float>.Count * Vector512<float>.Count;
            Span<Vector512<float>> d = MemoryMarshal.Cast<float, Vector512<float>>(dst.Slice(i, len));
            ReadOnlySpan<Vector512<float>> v_r0 = MemoryMarshal.Cast<float, Vector512<float>>(r0.Slice(i, len));
            ReadOnlySpan<Vector512<float>> v_r1 = MemoryMarshal.Cast<float, Vector512<float>>(r1.Slice(i, len));
            ReadOnlySpan<Vector512<float>> v_r2 = MemoryMarshal.Cast<float, Vector512<float>>(r2.Slice(i, len));
            ReadOnlySpan<Vector512<float>> v_r3 = MemoryMarshal.Cast<float, Vector512<float>>(r3.Slice(i, len));
            ReadOnlySpan<Vector512<float>> v_r4 = MemoryMarshal.Cast<float, Vector512<float>>(r4.Slice(i, len));
            ReadOnlySpan<Vector512<float>> v_r5 = MemoryMarshal.Cast<float, Vector512<float>>(r5.Slice(i, len));
            ReadOnlySpan<Vector512<float>> v_r6 = MemoryMarshal.Cast<float, Vector512<float>>(r6.Slice(i, len));
            ReadOnlySpan<Vector512<float>> v_r7 = MemoryMarshal.Cast<float, Vector512<float>>(r7.Slice(i, len));
            Vector512<float> v0 = Vector512.Create(c0);
            Vector512<float> v1 = Vector512.Create(c1);
            Vector512<float> v2 = Vector512.Create(c2);
            Vector512<float> v3 = Vector512.Create(c3);
            Vector512<float> v4 = Vector512.Create(c4);
            Vector512<float> v5 = Vector512.Create(c5);
            Vector512<float> v6 = Vector512.Create(c6);
            Vector512<float> v7 = Vector512.Create(c7);
            for (int k = 0; k < d.Length; k++)
            {
                d[k] = (((((((d[k] + (v_r0[k] * v0)) + (v_r1[k] * v1)) + (v_r2[k] * v2)) + (v_r3[k] * v3)) + (v_r4[k] * v4)) + (v_r5[k] * v5)) + (v_r6[k] * v6)) + (v_r7[k] * v7);
            }

            i += len;
        }

#endif
        if (Vector.IsHardwareAccelerated && count - i >= Vector<float>.Count)
        {
            int len = (count - i) / Vector<float>.Count * Vector<float>.Count;
            Span<Vector<float>> d = MemoryMarshal.Cast<float, Vector<float>>(dst.Slice(i, len));
            ReadOnlySpan<Vector<float>> v_r0 = MemoryMarshal.Cast<float, Vector<float>>(r0.Slice(i, len));
            ReadOnlySpan<Vector<float>> v_r1 = MemoryMarshal.Cast<float, Vector<float>>(r1.Slice(i, len));
            ReadOnlySpan<Vector<float>> v_r2 = MemoryMarshal.Cast<float, Vector<float>>(r2.Slice(i, len));
            ReadOnlySpan<Vector<float>> v_r3 = MemoryMarshal.Cast<float, Vector<float>>(r3.Slice(i, len));
            ReadOnlySpan<Vector<float>> v_r4 = MemoryMarshal.Cast<float, Vector<float>>(r4.Slice(i, len));
            ReadOnlySpan<Vector<float>> v_r5 = MemoryMarshal.Cast<float, Vector<float>>(r5.Slice(i, len));
            ReadOnlySpan<Vector<float>> v_r6 = MemoryMarshal.Cast<float, Vector<float>>(r6.Slice(i, len));
            ReadOnlySpan<Vector<float>> v_r7 = MemoryMarshal.Cast<float, Vector<float>>(r7.Slice(i, len));
            Vector<float> v0 = new Vector<float>(c0);
            Vector<float> v1 = new Vector<float>(c1);
            Vector<float> v2 = new Vector<float>(c2);
            Vector<float> v3 = new Vector<float>(c3);
            Vector<float> v4 = new Vector<float>(c4);
            Vector<float> v5 = new Vector<float>(c5);
            Vector<float> v6 = new Vector<float>(c6);
            Vector<float> v7 = new Vector<float>(c7);
            for (int k = 0; k < d.Length; k++)
            {
                d[k] = (((((((d[k] + (v_r0[k] * v0)) + (v_r1[k] * v1)) + (v_r2[k] * v2)) + (v_r3[k] * v3)) + (v_r4[k] * v4)) + (v_r5[k] * v5)) + (v_r6[k] * v6)) + (v_r7[k] * v7);
            }

            i += len;
        }

        for (; i < count; i++)
        {
            dst[i] = (((((((dst[i] + (r0[i] * c0)) + (r1[i] * c1)) + (r2[i] * c2)) + (r3[i] * c3)) + (r4[i] * c4)) + (r5[i] * c5)) + (r6[i] * c6)) + (r7[i] * c7);
        }
    }


    internal static void ResampleHorizontalGather(
        ResizeSampler horizontal,
        Span<float> output,
        ReadOnlySpan<float> input,
        int sliceOutX,
        int sliceOutW,
        int sliceInN0,
        int eff)
    {
#if NET8_0_OR_GREATER
        switch (eff)
        {
            case ResizeCoders.FancyStride:
                if (Vector128.IsHardwareAccelerated)
                {
                    HorizontalGatherFancy8(horizontal, output, input, sliceOutX, sliceOutW, sliceInN0);
                    return;
                }

                break;

            case 4:
            case 3:
            case 2:
                if (Vector128.IsHardwareAccelerated)
                {
                    HorizontalGatherSmallV128(horizontal, output, input, sliceOutX, sliceOutW, sliceInN0, eff);
                    return;
                }

                break;

            case 1:
                if (Avx.IsSupported)
                {
                    HorizontalGather1ChAvx(horizontal, output, input, sliceOutX, sliceOutW, sliceInN0);
                    return;
                }

                if (Sse.IsSupported)
                {
                    HorizontalGather1ChSse(horizontal, output, input, sliceOutX, sliceOutW, sliceInN0);
                    return;
                }

                if (AdvSimd.IsSupported)
                {
                    HorizontalGather1ChArm64(horizontal, output, input, sliceOutX, sliceOutW, sliceInN0);
                    return;
                }

                break;
        }
#endif
        ResampleHorizontalGatherScalar(horizontal, output, input, sliceOutX, sliceOutW, sliceInN0, eff);
    }

    internal static void ResampleHorizontalGatherScalar(
        ResizeSampler horizontal,
        Span<float> output,
        ReadOnlySpan<float> input,
        int sliceOutX,
        int sliceOutW,
        int sliceInN0,
        int eff)
    {
        ResizeContributor[] contribs = horizontal.Contributors;
        float[] coeffs = horizontal.Coefficients;
        int coeffWidth = horizontal.CoefficientWidth;

        if (eff != 1)
        {
            int fourChains = eff is 2 or 3 ? 1 : 0;
            for (int lx = 0; lx < sliceOutW; lx++)
            {
                int x = sliceOutX + lx;
                ResizeContributor c = contribs[x];
                int dst = lx * eff;
                if (c.N1 < c.N0)
                {
                    output.Slice(dst, eff).Clear();
                    continue;
                }

                int src = (c.N0 - sliceInN0) * eff;
                ReadOnlySpan<float> w = coeffs.AsSpan(x * coeffWidth, c.N1 - c.N0 + 1);
                for (int ch = 0; ch < eff; ch++)
                {
                    output[dst + ch] = GatherOneChannel(input, src, eff, ch, w, fourChains);
                }
            }

            return;
        }

        switch (eff)
        {
            case 1:
                for (int lx = 0; lx < sliceOutW; lx++)
                {
                    int x = sliceOutX + lx;
                    ResizeContributor c = contribs[x];
                    if (c.N1 < c.N0)
                    {
                        output[lx] = 0.0f;
                        continue;
                    }

                    int src = c.N0 - sliceInN0;
                    int count = c.N1 - c.N0 + 1;
                    int co = x * coeffWidth;
                    float t0 = 0.0f;
                    for (int i = 0; i < count; i++)
                    {
                        t0 += coeffs[co + i] * input[src + i];
                    }

                    output[lx] = t0;
                }

                break;

        }
    }

    // stb_image_resize2 summation order for the horizontal gather of a pixel with n taps (SSE2 build):
    // fewer than 4 taps form one chain; otherwise 4- and 8-float pixels keep two chains (even and odd taps,
    // result E + O), 2- and 3-float pixels keep four chains (taps by index mod 4, result (A0 + A2) + (A1 + A3)).
    private static float GatherOneChannel(ReadOnlySpan<float> input, int src, int stride, int ch, ReadOnlySpan<float> w, int fourChains)
    {
        int count = w.Length;
        float a0 = w[0] * input[src + ch];
        if (count < 4)
        {
            for (int i = 1; i < count; i++)
            {
                a0 += w[i] * input[src + (i * stride) + ch];
            }

            return a0;
        }

        if (fourChains == 0)
        {
            float odd = w[1] * input[src + stride + ch];
            for (int i = 2; i < count; i++)
            {
                float term = w[i] * input[src + (i * stride) + ch];
                if ((i & 1) == 0)
                {
                    a0 += term;
                }
                else
                {
                    odd += term;
                }
            }

            return a0 + odd;
        }

        float a1 = w[1] * input[src + stride + ch];
        float a2 = w[2] * input[src + (2 * stride) + ch];
        float a3 = w[3] * input[src + (3 * stride) + ch];
        for (int i = 4; i < count; i++)
        {
            float term = w[i] * input[src + (i * stride) + ch];
            switch (i & 3)
            {
                case 0: a0 += term; break;
                case 1: a1 += term; break;
                case 2: a2 += term; break;
                default: a3 += term; break;
            }
        }

        return (a0 + a2) + (a1 + a3);
    }

#if NET8_0_OR_GREATER
    // Fancy-alpha RGBA pixels: eight floats per pixel ([R G B A R*A G*A B*A pad]), one Vector256 (or two
    // Vector128) per pixel. Summation follows stb_image_resize2: with four or more taps, even and odd taps
    // accumulate in separate chains that are added at the end; fewer taps form one chain. Rows are viewed as
    // vectors and every per-pixel window is sliced to the tap count, so the loops carry no bounds checks.
    private static void HorizontalGatherFancy8(
        ResizeSampler horizontal,
        Span<float> output,
        ReadOnlySpan<float> input,
        int sliceOutX,
        int sliceOutW,
        int sliceInN0)
    {
        if (Vector256.IsHardwareAccelerated)
        {
            HorizontalGatherFancy8V256(horizontal, output, input, sliceOutX, sliceOutW, sliceInN0);
        }
        else
        {
            HorizontalGatherFancy8V128(horizontal, output, input, sliceOutX, sliceOutW, sliceInN0);
        }
    }

    // Views of two consecutive taps (even, odd) used by the pair loops; never constructed directly.
    private readonly struct VPair(Vector256<float> e, Vector256<float> o)
    {
        public readonly Vector256<float> E = e;
        public readonly Vector256<float> O = o;
    }

    private readonly struct FPair(float e, float o)
    {
        public readonly float E = e;
        public readonly float O = o;
    }

    private static void HorizontalGatherFancy8V256(
        ResizeSampler horizontal,
        Span<float> output,
        ReadOnlySpan<float> input,
        int sliceOutX,
        int sliceOutW,
        int sliceInN0)
    {
        ReadOnlySpan<ResizeContributor> contribs = horizontal.Contributors;
        ReadOnlySpan<float> coeffs = horizontal.Coefficients;
        int coeffWidth = horizontal.CoefficientWidth;
        ReadOnlySpan<Vector256<float>> inV = MemoryMarshal.Cast<float, Vector256<float>>(input);
        Span<Vector256<float>> outV = MemoryMarshal.Cast<float, Vector256<float>>(output).Slice(0, sliceOutW);

        int lx = 0;
        while (lx < outV.Length)
        {
            int x = sliceOutX + lx;
            ResizeContributor c0 = contribs[x];
            int count = c0.N1 - c0.N0 + 1;
            if (count <= 0)
            {
                outV[lx] = Vector256<float>.Zero;
                lx++;
                continue;
            }

            ReadOnlySpan<Vector256<float>> a = inV.Slice(c0.N0 - sliceInN0, count);
            ReadOnlySpan<float> wa = coeffs.Slice(x * coeffWidth, count);

            if (count >= 4 && lx + 1 < outV.Length)
            {
                ResizeContributor c1 = contribs[x + 1];
                if (c1.N1 - c1.N0 + 1 == count)
                {
                    // Two output pixels in lockstep: four independent chains.
                    ReadOnlySpan<Vector256<float>> b = inV.Slice(c1.N0 - sliceInN0, count);
                    ReadOnlySpan<float> wb = coeffs.Slice((x + 1) * coeffWidth, count);
                    Vector256<float> ae = a[0] * Vector256.Create(wa[0]);
                    Vector256<float> ao = a[1] * Vector256.Create(wa[1]);
                    Vector256<float> be = b[0] * Vector256.Create(wb[0]);
                    Vector256<float> bo = b[1] * Vector256.Create(wb[1]);

                    // Taps 2.. as (even, odd) pairs; every view is sliced to the same length, so the loop
                    // carries no bounds checks.
                    int pairTaps = (count - 2) & ~1;
                    ReadOnlySpan<VPair> pa = MemoryMarshal.Cast<Vector256<float>, VPair>(a.Slice(2, pairTaps));
                    ReadOnlySpan<VPair> pb = MemoryMarshal.Cast<Vector256<float>, VPair>(b.Slice(2, pairTaps)).Slice(0, pa.Length);
                    ReadOnlySpan<FPair> pwa = MemoryMarshal.Cast<float, FPair>(wa.Slice(2, pairTaps)).Slice(0, pa.Length);
                    ReadOnlySpan<FPair> pwb = MemoryMarshal.Cast<float, FPair>(wb.Slice(2, pairTaps)).Slice(0, pa.Length);
                    for (int k = 0; k < pa.Length; k++)
                    {
                        ae += pa[k].E * Vector256.Create(pwa[k].E);
                        be += pb[k].E * Vector256.Create(pwb[k].E);
                        ao += pa[k].O * Vector256.Create(pwa[k].O);
                        bo += pb[k].O * Vector256.Create(pwb[k].O);
                    }

                    if ((count & 1) != 0)
                    {
                        ae += a[count - 1] * Vector256.Create(wa[count - 1]);
                        be += b[count - 1] * Vector256.Create(wb[count - 1]);
                    }

                    outV[lx] = ae + ao;
                    outV[lx + 1] = be + bo;
                    lx += 2;
                    continue;
                }
            }

            outV[lx] = GatherFancy8One(a, wa);
            lx++;
        }
    }

    private static Vector256<float> GatherFancy8One(ReadOnlySpan<Vector256<float>> a, ReadOnlySpan<float> w)
    {
        Vector256<float> even = a[0] * Vector256.Create(w[0]);
        if (a.Length < 4)
        {
            for (int i = 1; i < a.Length; i++)
            {
                even += a[i] * Vector256.Create(w[i]);
            }

            return even;
        }

        Vector256<float> odd = a[1] * Vector256.Create(w[1]);
        int pairTaps = (a.Length - 2) & ~1;
        ReadOnlySpan<VPair> pa = MemoryMarshal.Cast<Vector256<float>, VPair>(a.Slice(2, pairTaps));
        ReadOnlySpan<FPair> pw = MemoryMarshal.Cast<float, FPair>(w.Slice(2, pairTaps)).Slice(0, pa.Length);
        for (int k = 0; k < pa.Length; k++)
        {
            even += pa[k].E * Vector256.Create(pw[k].E);
            odd += pa[k].O * Vector256.Create(pw[k].O);
        }

        if ((a.Length & 1) != 0)
        {
            even += a[a.Length - 1] * Vector256.Create(w[a.Length - 1]);
        }

        return even + odd;
    }

    private static void HorizontalGatherFancy8V128(
        ResizeSampler horizontal,
        Span<float> output,
        ReadOnlySpan<float> input,
        int sliceOutX,
        int sliceOutW,
        int sliceInN0)
    {
        ReadOnlySpan<ResizeContributor> contribs = horizontal.Contributors;
        ReadOnlySpan<float> coeffs = horizontal.Coefficients;
        int coeffWidth = horizontal.CoefficientWidth;
        ReadOnlySpan<Vector128<float>> inV = MemoryMarshal.Cast<float, Vector128<float>>(input);
        Span<Vector128<float>> outV = MemoryMarshal.Cast<float, Vector128<float>>(output).Slice(0, sliceOutW * 2);

        for (int lx = 0; lx < sliceOutW; lx++)
        {
            int x = sliceOutX + lx;
            ResizeContributor c = contribs[x];
            int count = c.N1 - c.N0 + 1;
            if (count <= 0)
            {
                outV[2 * lx] = Vector128<float>.Zero;
                outV[(2 * lx) + 1] = Vector128<float>.Zero;
                continue;
            }

            // Pixel n occupies vectors 2n (R G B A) and 2n+1 (R*A G*A B*A pad).
            ReadOnlySpan<Vector128<float>> a = inV.Slice((c.N0 - sliceInN0) * 2, count * 2);
            ReadOnlySpan<float> w = coeffs.Slice(x * coeffWidth, count);
            Vector128<float> w0 = Vector128.Create(w[0]);
            Vector128<float> loE = a[0] * w0;
            Vector128<float> hiE = a[1] * w0;
            if (count < 4)
            {
                for (int i = 1; i < count; i++)
                {
                    Vector128<float> wi = Vector128.Create(w[i]);
                    loE += a[2 * i] * wi;
                    hiE += a[(2 * i) + 1] * wi;
                }

                outV[2 * lx] = loE;
                outV[(2 * lx) + 1] = hiE;
                continue;
            }

            Vector128<float> w1 = Vector128.Create(w[1]);
            Vector128<float> loO = a[2] * w1;
            Vector128<float> hiO = a[3] * w1;
            int k = 2;
            for (; k + 1 < count; k += 2)
            {
                Vector128<float> we = Vector128.Create(w[k]);
                Vector128<float> wo = Vector128.Create(w[k + 1]);
                loE += a[2 * k] * we;
                hiE += a[(2 * k) + 1] * we;
                loO += a[(2 * k) + 2] * wo;
                hiO += a[(2 * k) + 3] * wo;
            }

            if (k < count)
            {
                Vector128<float> we = Vector128.Create(w[k]);
                loE += a[2 * k] * we;
                hiE += a[(2 * k) + 1] * we;
            }

            outV[2 * lx] = loE + loO;
            outV[(2 * lx) + 1] = hiE + hiO;
        }
    }

    // 2-, 3- and 4-float pixels: one Vector128 per tap (lanes beyond the pixel are never stored as results),
    // chains as in GatherOneChannel. Pixels whose loads or store would leave the buffers use the scalar form.
    private static void HorizontalGatherSmallV128(
        ResizeSampler horizontal,
        Span<float> output,
        ReadOnlySpan<float> input,
        int sliceOutX,
        int sliceOutW,
        int sliceInN0,
        int eff)
    {
        ReadOnlySpan<ResizeContributor> contribs = horizontal.Contributors;
        ReadOnlySpan<float> coeffs = horizontal.Coefficients;
        int coeffWidth = horizontal.CoefficientWidth;
        int fourChains = eff == 4 ? 0 : 1;
        for (int lx = 0; lx < sliceOutW; lx++)
        {
            int x = sliceOutX + lx;
            ResizeContributor c = contribs[x];
            int dst = lx * eff;
            int count = c.N1 - c.N0 + 1;
            if (count <= 0)
            {
                output.Slice(dst, eff).Clear();
                continue;
            }

            int src = (c.N0 - sliceInN0) * eff;
            ReadOnlySpan<float> w = coeffs.Slice(x * coeffWidth, count);
            if (src + ((count - 1) * eff) + 4 > input.Length || dst + 4 > output.Length)
            {
                for (int ch = 0; ch < eff; ch++)
                {
                    output[dst + ch] = GatherOneChannel(input, src, eff, ch, w, fourChains);
                }

                continue;
            }

            ReadOnlySpan<float> win = input.Slice(src, ((count - 1) * eff) + 4);
            if (fourChains == 0 && count >= 4 && Vector256.IsHardwareAccelerated)
            {
                // taps i and i + 1 are adjacent: one 8-float load, even chain in the low half, odd in the high half
                Vector256<float> acc = Vector256.Create(win.Slice(0, 8)) * Vector256.Create(Vector128.Create(w[0]), Vector128.Create(w[1]));
                int k = 2;
                for (; k + 1 < count; k += 2)
                {
                    acc += Vector256.Create(win.Slice(k * 4, 8)) * Vector256.Create(Vector128.Create(w[k]), Vector128.Create(w[k + 1]));
                }

                Vector128<float> even = acc.GetLower();
                if (k < count)
                {
                    even += Vector128.Create(win.Slice(k * 4, 4)) * w[k];
                }

                (even + acc.GetUpper()).CopyTo(output.Slice(dst, 4));
                continue;
            }

            Vector128<float> a0 = Vector128.Create(win.Slice(0, 4)) * w[0];
            Vector128<float> result;
            if (count < 4)
            {
                for (int i = 1; i < count; i++)
                {
                    a0 += Vector128.Create(win.Slice(i * eff, 4)) * w[i];
                }

                result = a0;
            }
            else if (fourChains == 0)
            {
                Vector128<float> odd = Vector128.Create(win.Slice(eff, 4)) * w[1];
                int i = 2;
                for (; i + 1 < count; i += 2)
                {
                    a0 += Vector128.Create(win.Slice(i * eff, 4)) * w[i];
                    odd += Vector128.Create(win.Slice((i + 1) * eff, 4)) * w[i + 1];
                }

                if (i < count)
                {
                    a0 += Vector128.Create(win.Slice(i * eff, 4)) * w[i];
                }

                result = a0 + odd;
            }
            else
            {
                Vector128<float> a1 = Vector128.Create(win.Slice(eff, 4)) * w[1];
                Vector128<float> a2 = Vector128.Create(win.Slice(2 * eff, 4)) * w[2];
                Vector128<float> a3 = Vector128.Create(win.Slice(3 * eff, 4)) * w[3];
                int i = 4;
                for (; i + 3 < count; i += 4)
                {
                    a0 += Vector128.Create(win.Slice(i * eff, 4)) * w[i];
                    a1 += Vector128.Create(win.Slice((i + 1) * eff, 4)) * w[i + 1];
                    a2 += Vector128.Create(win.Slice((i + 2) * eff, 4)) * w[i + 2];
                    a3 += Vector128.Create(win.Slice((i + 3) * eff, 4)) * w[i + 3];
                }

                if (i < count)
                {
                    a0 += Vector128.Create(win.Slice(i * eff, 4)) * w[i];
                }

                if (i + 1 < count)
                {
                    a1 += Vector128.Create(win.Slice((i + 1) * eff, 4)) * w[i + 1];
                }

                if (i + 2 < count)
                {
                    a2 += Vector128.Create(win.Slice((i + 2) * eff, 4)) * w[i + 2];
                }

                result = (a0 + a2) + (a1 + a3);
            }

            result.CopyTo(output.Slice(dst, 4));
        }
    }

    private static void HorizontalGather1ChAvx(
        ResizeSampler horizontal,
        Span<float> output,
        ReadOnlySpan<float> input,
        int sliceOutX,
        int sliceOutW,
        int sliceInN0)
    {
        ReadOnlySpan<ResizeContributor> cRef = horizontal.Contributors;
        ReadOnlySpan<float> coeffRef = horizontal.Coefficients;
        var inRef = input;
        var outRef = output;
        int coeffWidth = horizontal.CoefficientWidth;

        for (int lx = 0; lx < sliceOutW; lx++)
        {
            int x = sliceOutX + lx;
            ResizeContributor c = cRef[x];
            if (c.N1 < c.N0)
            {
                outRef[lx] = 0.0f;
                continue;
            }

            int src = c.N0 - sliceInN0;
            int count = c.N1 - c.N0 + 1;
            int co = x * coeffWidth;
            int i = 0;
            Vector128<float> acc128 = Vector128<float>.Zero;

            if (count >= 8)
            {
                Vector256<float> acc256 = Vector256<float>.Zero;
                for (; i <= count - 8; i += 8)
                {
                    acc256 = Avx.Add(
                        acc256,
                        Avx.Multiply(
                            Vector256.Create(inRef.Slice(src + i, 8)),
                            Vector256.Create(coeffRef.Slice(co + i, 8))));
                }

                acc128 = Sse.Add(acc256.GetLower(), acc256.GetUpper());
            }

            if (i <= count - 4)
            {
                acc128 = Sse.Add(
                    acc128,
                    Sse.Multiply(
                        Vector128.Create(inRef.Slice(src + i, 4)),
                        Vector128.Create(coeffRef.Slice(co + i, 4))));
                i += 4;
            }

            float sum = (acc128.GetElement(0) + acc128.GetElement(1)) + (acc128.GetElement(2) + acc128.GetElement(3));
            for (; i < count; i++)
            {
                sum += coeffRef[co + i] * inRef[src + i];
            }

            outRef[lx] = sum;
        }
    }

    private static void HorizontalGather1ChSse(
        ResizeSampler horizontal,
        Span<float> output,
        ReadOnlySpan<float> input,
        int sliceOutX,
        int sliceOutW,
        int sliceInN0)
    {
        ReadOnlySpan<ResizeContributor> cRef = horizontal.Contributors;
        ReadOnlySpan<float> coeffRef = horizontal.Coefficients;
        var inRef = input;
        var outRef = output;
        int coeffWidth = horizontal.CoefficientWidth;

        for (int lx = 0; lx < sliceOutW; lx++)
        {
            int x = sliceOutX + lx;
            ResizeContributor c = cRef[x];
            if (c.N1 < c.N0)
            {
                outRef[lx] = 0.0f;
                continue;
            }

            int src = c.N0 - sliceInN0;
            int count = c.N1 - c.N0 + 1;
            int co = x * coeffWidth;
            int i = 0;
            Vector128<float> acc = Vector128<float>.Zero;

            for (; i <= count - 4; i += 4)
            {
                acc = Sse.Add(
                    acc,
                    Sse.Multiply(
                        Vector128.Create(inRef.Slice(src + i, 4)),
                        Vector128.Create(coeffRef.Slice(co + i, 4))));
            }

            float sum = (acc.GetElement(0) + acc.GetElement(1)) + (acc.GetElement(2) + acc.GetElement(3));
            for (; i < count; i++)
            {
                sum += coeffRef[co + i] * inRef[src + i];
            }

            outRef[lx] = sum;
        }
    }

    private static void HorizontalGather1ChArm64(
        ResizeSampler horizontal,
        Span<float> output,
        ReadOnlySpan<float> input,
        int sliceOutX,
        int sliceOutW,
        int sliceInN0)
    {
        ReadOnlySpan<ResizeContributor> cRef = horizontal.Contributors;
        ReadOnlySpan<float> coeffRef = horizontal.Coefficients;
        var inRef = input;
        var outRef = output;
        int coeffWidth = horizontal.CoefficientWidth;

        for (int lx = 0; lx < sliceOutW; lx++)
        {
            int x = sliceOutX + lx;
            ResizeContributor c = cRef[x];
            if (c.N1 < c.N0)
            {
                outRef[lx] = 0.0f;
                continue;
            }

            int src = c.N0 - sliceInN0;
            int count = c.N1 - c.N0 + 1;
            int co = x * coeffWidth;
            int i = 0;
            Vector128<float> acc = Vector128<float>.Zero;

            for (; i <= count - 4; i += 4)
            {
                acc = AdvSimd.Add(
                    acc,
                    AdvSimd.Multiply(
                        Vector128.Create(inRef.Slice(src + i, 4)),
                        Vector128.Create(coeffRef.Slice(co + i, 4))));
            }

            float sum = (acc.GetElement(0) + acc.GetElement(1)) + (acc.GetElement(2) + acc.GetElement(3));
            for (; i < count; i++)
            {
                sum += coeffRef[co + i] * inRef[src + i];
            }

            outRef[lx] = sum;
        }
    }
#endif

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static int GetRingSlot(int scanline, int firstScanline, int beginIndex, int ringEntries)
    {
        int offset = scanline - firstScanline;
        int idx = beginIndex + offset;
        if (idx >= ringEntries)
        {
            idx -= ringEntries;
        }

        return idx;
    }
}
