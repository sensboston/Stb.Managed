using System.Runtime.CompilerServices;
using ILGPU;
using ILGPU.Runtime;

namespace Stb.Managed.Gpu.Kernels;

internal readonly struct ResizePassParams
{
    public readonly int SrcW;
    public readonly int SrcH;
    public readonly int DstW;
    public readonly int DstH;
    public readonly int Channels;
    public readonly int CoeffWidth;
    public readonly int EdgeMode; // 0=Clamp, 1=Reflect, 2=Wrap, 3=Zero
    public readonly int ColorMode; // 0=LinearByte, 1=SrgbByte (alpha linear), 2=SrgbAlphaByte (all sRGB)
    public readonly int AlphaChannel; // -1 = none/no alpha weighting, 3 = RGBA/BGRA, 1 = RA
    public readonly int SwapRB; // 1 for BGR/BGRA

    public ResizePassParams(
        int srcW,
        int srcH,
        int dstW,
        int dstH,
        int channels,
        int coeffWidth,
        int edgeMode,
        int colorMode,
        int alphaChannel,
        int swapRB)
    {
        SrcW = srcW;
        SrcH = srcH;
        DstW = dstW;
        DstH = dstH;
        Channels = channels;
        CoeffWidth = coeffWidth;
        EdgeMode = edgeMode;
        ColorMode = colorMode;
        AlphaChannel = alphaChannel;
        SwapRB = swapRB;
    }
}

/// <summary>
/// ILGPU 2-pass separable image resampling kernels matching <c>stb_image_resize2.h</c> v2.50:
/// supports all built-in filters via precomputed gather contributor tables, all 4 edge modes,
/// exact sRGB LUT decoding and Fabian Giesen linear-to-sRGB encoding, alpha weighting, and batching.
/// </summary>
internal static class ResizeGpuKernels
{
    private const float InvMaxUInt8 = 3.9215689e-03f;
    private const float MaxUInt8AsFloat = 255.0f;
    private const float AlphaTooSmallToDivide = 1.0f / (1 << 20) / (1 << 20) / (1 << 20) / (1 << 20) / (1 << 20) / (1 << 20);
    private const uint SrgbAlmostOneBits = 0x3F7FFFFFu;
    private const uint SrgbMinValBits = (127u - 13u) << 23;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int EdgeWrapDevice(int edgeMode, int n, int max)
    {
        if (n >= 0 && n < max)
        {
            return n;
        }

        if (edgeMode == 0) // Clamp
        {
            return n < 0 ? 0 : max - 1;
        }

        if (edgeMode == 1) // Reflect
        {
            if (n < 0)
            {
                return n > -max ? -n : max - 1;
            }

            return (n - max) >= max ? 0 : (max - 1) - (n - max);
        }

        if (edgeMode == 2) // Wrap
        {
            if (n >= 0)
            {
                return n % max;
            }

            int m = (-n) % max;
            return m != 0 ? max - m : 0;
        }

        // Zero edge mode returns -1 sentinel.
        return -1;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static byte FloatToLinearByte(float v)
    {
        if (v <= 0.0f)
        {
            return 0;
        }

        if (v >= 1.0f)
        {
            return 255;
        }

        return (byte)(v * MaxUInt8AsFloat + 0.5f);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static byte LinearToSrgbByteDevice(float value, ArrayView1D<uint, Stride1D.Dense> fp32ToSrgbTab)
    {
        float srgbMinVal = Interop.IntAsFloat(unchecked((int)SrgbMinValBits));
        float srgbAlmostOne = Interop.IntAsFloat(unchecked((int)SrgbAlmostOneBits));

        if (!(value > srgbMinVal))
        {
            return 0;
        }

        if (value > srgbAlmostOne)
        {
            return 255;
        }

        uint bits = unchecked((uint)Interop.FloatAsInt(value));
        uint tab = fp32ToSrgbTab[(int)((bits - SrgbMinValBits) >> 20)];
        uint bias = (tab >> 16) << 9;
        uint scale = tab & 0xFFFFu;
        uint t = (bits >> 12) & 0xFFu;
        return (byte)((bias + (scale * t)) >> 16);
    }

    /// <summary>
    /// Pass 1 (Horizontal) for 8-bit images (single or batched): reads <c>byte</c> rows, applies sRGB LUT
    /// and alpha weighting if requested, and writes linear <c>float</c> intermediate rows (<c>batch * srcH * dstW * ch</c>).
    /// </summary>
    public static void HorizontalByteKernel(
        Index1D idx,
        ArrayView1D<byte, Stride1D.Dense> src,
        ArrayView1D<float, Stride1D.Dense> tmp,
        ArrayView1D<int, Stride1D.Dense> hN0,
        ArrayView1D<int, Stride1D.Dense> hN1,
        ArrayView1D<float, Stride1D.Dense> hCoeffs,
        ArrayView1D<float, Stride1D.Dense> srgbToLinear,
        ResizePassParams p)
    {
        int rowPixels = p.DstW;
        int imgRows = p.SrcH;
        int totalRow = idx / rowPixels;
        int dx = idx - totalRow * rowPixels;
        int batchIdx = totalRow / imgRows;
        int sy = totalRow - batchIdx * imgRows;

        int n0 = hN0[dx];
        int n1 = hN1[dx];
        int coeffBase = dx * p.CoeffWidth;
        int srcRowBase = (batchIdx * p.SrcH + sy) * p.SrcW * p.Channels;
        int dstBase = idx * p.Channels;

        float acc0 = 0.0f, acc1 = 0.0f, acc2 = 0.0f, acc3 = 0.0f;

        for (int sx = n0; sx <= n1; ++sx)
        {
            float w = hCoeffs[coeffBase + (sx - n0)];
            int wrappedX = EdgeWrapDevice(p.EdgeMode, sx, p.SrcW);
            if (wrappedX < 0)
            {
                continue;
            }

            int pOff = srcRowBase + wrappedX * p.Channels;
            if (p.Channels == 4)
            {
                int i0 = p.SwapRB != 0 ? 2 : 0;
                int i2 = p.SwapRB != 0 ? 0 : 2;
                byte b0 = src[pOff + i0];
                byte b1 = src[pOff + 1];
                byte b2 = src[pOff + i2];
                byte b3 = src[pOff + 3];

                float c0 = p.ColorMode != 0 ? srgbToLinear[b0] : b0 * InvMaxUInt8;
                float c1 = p.ColorMode != 0 ? srgbToLinear[b1] : b1 * InvMaxUInt8;
                float c2 = p.ColorMode != 0 ? srgbToLinear[b2] : b2 * InvMaxUInt8;
                float c3 = p.ColorMode == 2 ? srgbToLinear[b3] : b3 * InvMaxUInt8;

                if (p.AlphaChannel == 3)
                {
                    c0 *= c3;
                    c1 *= c3;
                    c2 *= c3;
                }

                acc0 += c0 * w;
                acc1 += c1 * w;
                acc2 += c2 * w;
                acc3 += c3 * w;
            }
            else if (p.Channels == 3)
            {
                int i0 = p.SwapRB != 0 ? 2 : 0;
                int i2 = p.SwapRB != 0 ? 0 : 2;
                byte b0 = src[pOff + i0];
                byte b1 = src[pOff + 1];
                byte b2 = src[pOff + i2];

                float c0 = p.ColorMode != 0 ? srgbToLinear[b0] : b0 * InvMaxUInt8;
                float c1 = p.ColorMode != 0 ? srgbToLinear[b1] : b1 * InvMaxUInt8;
                float c2 = p.ColorMode != 0 ? srgbToLinear[b2] : b2 * InvMaxUInt8;

                acc0 += c0 * w;
                acc1 += c1 * w;
                acc2 += c2 * w;
            }
            else if (p.Channels == 2)
            {
                byte b0 = src[pOff];
                byte b1 = src[pOff + 1];
                float c0 = p.ColorMode != 0 ? srgbToLinear[b0] : b0 * InvMaxUInt8;
                float c1 = p.ColorMode == 2 ? srgbToLinear[b1] : b1 * InvMaxUInt8;
                if (p.AlphaChannel == 1)
                {
                    c0 *= c1;
                }

                acc0 += c0 * w;
                acc1 += c1 * w;
            }
            else
            {
                byte b0 = src[pOff];
                float c0 = p.ColorMode != 0 ? srgbToLinear[b0] : b0 * InvMaxUInt8;
                acc0 += c0 * w;
            }
        }

        tmp[dstBase] = acc0;
        if (p.Channels >= 2) tmp[dstBase + 1] = acc1;
        if (p.Channels >= 3) tmp[dstBase + 2] = acc2;
        if (p.Channels == 4) tmp[dstBase + 3] = acc3;
    }

    /// <summary>
    /// Pass 2 (Vertical) for 8-bit images (single or batched): gathers from <c>tmp</c>, unpremultiplies
    /// alpha if needed, converts linear float back to sRGB or linear <c>byte</c>, and writes <c>dst</c>.
    /// </summary>
    public static void VerticalByteKernel(
        Index1D idx,
        ArrayView1D<float, Stride1D.Dense> tmp,
        ArrayView1D<byte, Stride1D.Dense> dst,
        ArrayView1D<int, Stride1D.Dense> vN0,
        ArrayView1D<int, Stride1D.Dense> vN1,
        ArrayView1D<float, Stride1D.Dense> vCoeffs,
        ArrayView1D<uint, Stride1D.Dense> fp32ToSrgbTab,
        ResizePassParams p)
    {
        int dstRowPixels = p.DstW;
        int totalRow = idx / dstRowPixels;
        int dx = idx - totalRow * dstRowPixels;
        int batchIdx = totalRow / p.DstH;
        int dy = totalRow - batchIdx * p.DstH;

        int n0 = vN0[dy];
        int n1 = vN1[dy];
        int coeffBase = dy * p.CoeffWidth;
        int tmpImgBase = batchIdx * p.SrcH * p.DstW * p.Channels;
        int dstBase = idx * p.Channels;

        float acc0 = 0.0f, acc1 = 0.0f, acc2 = 0.0f, acc3 = 0.0f;

        for (int sy = n0; sy <= n1; ++sy)
        {
            float w = vCoeffs[coeffBase + (sy - n0)];
            int wrappedY = EdgeWrapDevice(p.EdgeMode, sy, p.SrcH);
            if (wrappedY < 0)
            {
                continue;
            }

            int tOff = tmpImgBase + (wrappedY * p.DstW + dx) * p.Channels;
            acc0 += tmp[tOff] * w;
            if (p.Channels >= 2) acc1 += tmp[tOff + 1] * w;
            if (p.Channels >= 3) acc2 += tmp[tOff + 2] * w;
            if (p.Channels == 4) acc3 += tmp[tOff + 3] * w;
        }

        if (p.Channels == 4)
        {
            if (p.AlphaChannel == 3)
            {
                if (acc3 > AlphaTooSmallToDivide)
                {
                    float invA = 1.0f / acc3;
                    acc0 *= invA;
                    acc1 *= invA;
                    acc2 *= invA;
                }
                else
                {
                    acc0 = 0.0f;
                    acc1 = 0.0f;
                    acc2 = 0.0f;
                }
            }

            byte o0 = p.ColorMode != 0 ? LinearToSrgbByteDevice(acc0, fp32ToSrgbTab) : FloatToLinearByte(acc0);
            byte o1 = p.ColorMode != 0 ? LinearToSrgbByteDevice(acc1, fp32ToSrgbTab) : FloatToLinearByte(acc1);
            byte o2 = p.ColorMode != 0 ? LinearToSrgbByteDevice(acc2, fp32ToSrgbTab) : FloatToLinearByte(acc2);
            byte o3 = p.ColorMode == 2 ? LinearToSrgbByteDevice(acc3, fp32ToSrgbTab) : FloatToLinearByte(acc3);

            int i0 = p.SwapRB != 0 ? 2 : 0;
            int i2 = p.SwapRB != 0 ? 0 : 2;
            dst[dstBase + i0] = o0;
            dst[dstBase + 1] = o1;
            dst[dstBase + i2] = o2;
            dst[dstBase + 3] = o3;
        }
        else if (p.Channels == 3)
        {
            byte o0 = p.ColorMode != 0 ? LinearToSrgbByteDevice(acc0, fp32ToSrgbTab) : FloatToLinearByte(acc0);
            byte o1 = p.ColorMode != 0 ? LinearToSrgbByteDevice(acc1, fp32ToSrgbTab) : FloatToLinearByte(acc1);
            byte o2 = p.ColorMode != 0 ? LinearToSrgbByteDevice(acc2, fp32ToSrgbTab) : FloatToLinearByte(acc2);
            int i0 = p.SwapRB != 0 ? 2 : 0;
            int i2 = p.SwapRB != 0 ? 0 : 2;
            dst[dstBase + i0] = o0;
            dst[dstBase + 1] = o1;
            dst[dstBase + i2] = o2;
        }
        else if (p.Channels == 2)
        {
            if (p.AlphaChannel == 1)
            {
                if (acc1 > AlphaTooSmallToDivide)
                {
                    acc0 /= acc1;
                }
                else
                {
                    acc0 = 0.0f;
                }
            }

            dst[dstBase] = p.ColorMode != 0 ? LinearToSrgbByteDevice(acc0, fp32ToSrgbTab) : FloatToLinearByte(acc0);
            dst[dstBase + 1] = p.ColorMode == 2 ? LinearToSrgbByteDevice(acc1, fp32ToSrgbTab) : FloatToLinearByte(acc1);
        }
        else
        {
            dst[dstBase] = p.ColorMode != 0 ? LinearToSrgbByteDevice(acc0, fp32ToSrgbTab) : FloatToLinearByte(acc0);
        }
    }

    /// <summary>
    /// Pass 1 (Horizontal) for 32-bit floating-point images (single or batched).
    /// </summary>
    public static void HorizontalFloatKernel(
        Index1D idx,
        ArrayView1D<float, Stride1D.Dense> src,
        ArrayView1D<float, Stride1D.Dense> tmp,
        ArrayView1D<int, Stride1D.Dense> hN0,
        ArrayView1D<int, Stride1D.Dense> hN1,
        ArrayView1D<float, Stride1D.Dense> hCoeffs,
        ResizePassParams p)
    {
        int totalRow = idx / p.DstW;
        int dx = idx - totalRow * p.DstW;
        int batchIdx = totalRow / p.SrcH;
        int sy = totalRow - batchIdx * p.SrcH;

        int n0 = hN0[dx];
        int n1 = hN1[dx];
        int coeffBase = dx * p.CoeffWidth;
        int srcRowBase = (batchIdx * p.SrcH + sy) * p.SrcW * p.Channels;
        int dstBase = idx * p.Channels;

        float acc0 = 0.0f, acc1 = 0.0f, acc2 = 0.0f, acc3 = 0.0f;

        for (int sx = n0; sx <= n1; ++sx)
        {
            float w = hCoeffs[coeffBase + (sx - n0)];
            int wrappedX = EdgeWrapDevice(p.EdgeMode, sx, p.SrcW);
            if (wrappedX < 0)
            {
                continue;
            }

            int pOff = srcRowBase + wrappedX * p.Channels;
            if (p.Channels == 4)
            {
                float c0 = src[pOff];
                float c1 = src[pOff + 1];
                float c2 = src[pOff + 2];
                float c3 = src[pOff + 3];
                if (p.AlphaChannel == 3)
                {
                    c0 *= c3;
                    c1 *= c3;
                    c2 *= c3;
                }

                acc0 += c0 * w;
                acc1 += c1 * w;
                acc2 += c2 * w;
                acc3 += c3 * w;
            }
            else if (p.Channels == 3)
            {
                acc0 += src[pOff] * w;
                acc1 += src[pOff + 1] * w;
                acc2 += src[pOff + 2] * w;
            }
            else if (p.Channels == 2)
            {
                float c0 = src[pOff];
                float c1 = src[pOff + 1];
                if (p.AlphaChannel == 1)
                {
                    c0 *= c1;
                }

                acc0 += c0 * w;
                acc1 += c1 * w;
            }
            else
            {
                acc0 += src[pOff] * w;
            }
        }

        tmp[dstBase] = acc0;
        if (p.Channels >= 2) tmp[dstBase + 1] = acc1;
        if (p.Channels >= 3) tmp[dstBase + 2] = acc2;
        if (p.Channels == 4) tmp[dstBase + 3] = acc3;
    }

    /// <summary>
    /// Pass 2 (Vertical) for 32-bit floating-point images (single or batched).
    /// </summary>
    public static void VerticalFloatKernel(
        Index1D idx,
        ArrayView1D<float, Stride1D.Dense> tmp,
        ArrayView1D<float, Stride1D.Dense> dst,
        ArrayView1D<int, Stride1D.Dense> vN0,
        ArrayView1D<int, Stride1D.Dense> vN1,
        ArrayView1D<float, Stride1D.Dense> vCoeffs,
        ResizePassParams p)
    {
        int totalRow = idx / p.DstW;
        int dx = idx - totalRow * p.DstW;
        int batchIdx = totalRow / p.DstH;
        int dy = totalRow - batchIdx * p.DstH;

        int n0 = vN0[dy];
        int n1 = vN1[dy];
        int coeffBase = dy * p.CoeffWidth;
        int tmpImgBase = batchIdx * p.SrcH * p.DstW * p.Channels;
        int dstBase = idx * p.Channels;

        float acc0 = 0.0f, acc1 = 0.0f, acc2 = 0.0f, acc3 = 0.0f;

        for (int sy = n0; sy <= n1; ++sy)
        {
            float w = vCoeffs[coeffBase + (sy - n0)];
            int wrappedY = EdgeWrapDevice(p.EdgeMode, sy, p.SrcH);
            if (wrappedY < 0)
            {
                continue;
            }

            int tOff = tmpImgBase + (wrappedY * p.DstW + dx) * p.Channels;
            acc0 += tmp[tOff] * w;
            if (p.Channels >= 2) acc1 += tmp[tOff + 1] * w;
            if (p.Channels >= 3) acc2 += tmp[tOff + 2] * w;
            if (p.Channels == 4) acc3 += tmp[tOff + 3] * w;
        }

        if (p.Channels == 4 && p.AlphaChannel == 3)
        {
            if (acc3 > AlphaTooSmallToDivide)
            {
                float invA = 1.0f / acc3;
                acc0 *= invA;
                acc1 *= invA;
                acc2 *= invA;
            }
            else
            {
                acc0 = 0.0f;
                acc1 = 0.0f;
                acc2 = 0.0f;
            }
        }
        else if (p.Channels == 2 && p.AlphaChannel == 1)
        {
            if (acc1 > AlphaTooSmallToDivide)
            {
                acc0 /= acc1;
            }
            else
            {
                acc0 = 0.0f;
            }
        }

        dst[dstBase] = acc0;
        if (p.Channels >= 2) dst[dstBase + 1] = acc1;
        if (p.Channels >= 3) dst[dstBase + 2] = acc2;
        if (p.Channels == 4) dst[dstBase + 3] = acc3;
    }
}
