using System.Runtime.CompilerServices;
using ILGPU;
using ILGPU.Runtime;

namespace Stb.Managed.Gpu.Kernels;

/// <summary>
/// ILGPU device kernels for batch &amp; VRAM-resident color conversions, alpha premultiplication,
/// sRGB↔Linear transfers, and float heightmap visualization.
/// </summary>
internal static class ColorGpuKernels
{
    private const uint SrgbAlmostOneBits = 0x3F7FFFFFu;
    private const uint SrgbMinValBits = (127u - 13u) << 23;

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

    public static void SwapRedBlueRgbaKernel(
        Index1D pixelIdx,
        ArrayView1D<byte, Stride1D.Dense> src,
        ArrayView1D<byte, Stride1D.Dense> dst)
    {
        int o = pixelIdx * 4;
        byte r = src[o];
        byte g = src[o + 1];
        byte b = src[o + 2];
        byte a = src[o + 3];
        dst[o] = b;
        dst[o + 1] = g;
        dst[o + 2] = r;
        dst[o + 3] = a;
    }

    public static void RgbToRgbaKernel(
        Index1D pixelIdx,
        ArrayView1D<byte, Stride1D.Dense> src,
        ArrayView1D<byte, Stride1D.Dense> dst,
        byte alpha)
    {
        int s = pixelIdx * 3;
        int d = pixelIdx * 4;
        dst[d] = src[s];
        dst[d + 1] = src[s + 1];
        dst[d + 2] = src[s + 2];
        dst[d + 3] = alpha;
    }

    public static void RgbaToRgbKernel(
        Index1D pixelIdx,
        ArrayView1D<byte, Stride1D.Dense> src,
        ArrayView1D<byte, Stride1D.Dense> dst)
    {
        int s = pixelIdx * 4;
        int d = pixelIdx * 3;
        dst[d] = src[s];
        dst[d + 1] = src[s + 1];
        dst[d + 2] = src[s + 2];
    }

    public static void RgbaToGreyKernel(
        Index1D pixelIdx,
        ArrayView1D<byte, Stride1D.Dense> src,
        ArrayView1D<byte, Stride1D.Dense> dst)
    {
        int s = pixelIdx * 4;
        // Exact stb_image Rec.601 integer luma: (77*r + 150*g + 29*b) >> 8
        int y = (77 * src[s] + 150 * src[s + 1] + 29 * src[s + 2]) >> 8;
        dst[pixelIdx] = (byte)y;
    }

    public static void GreyToRgbaKernel(
        Index1D pixelIdx,
        ArrayView1D<byte, Stride1D.Dense> src,
        ArrayView1D<byte, Stride1D.Dense> dst,
        byte alpha)
    {
        byte y = src[pixelIdx];
        int d = pixelIdx * 4;
        dst[d] = y;
        dst[d + 1] = y;
        dst[d + 2] = y;
        dst[d + 3] = alpha;
    }

    public static void PremultiplyAlphaKernel(
        Index1D pixelIdx,
        ArrayView1D<byte, Stride1D.Dense> src,
        ArrayView1D<byte, Stride1D.Dense> dst)
    {
        int o = pixelIdx * 4;
        int a = src[o + 3];
        dst[o] = (byte)((src[o] * a + 127) / 255);
        dst[o + 1] = (byte)((src[o + 1] * a + 127) / 255);
        dst[o + 2] = (byte)((src[o + 2] * a + 127) / 255);
        dst[o + 3] = (byte)a;
    }

    public static void UnpremultiplyAlphaKernel(
        Index1D pixelIdx,
        ArrayView1D<byte, Stride1D.Dense> src,
        ArrayView1D<byte, Stride1D.Dense> dst)
    {
        int o = pixelIdx * 4;
        int a = src[o + 3];
        if (a == 0)
        {
            dst[o] = 0;
            dst[o + 1] = 0;
            dst[o + 2] = 0;
            dst[o + 3] = 0;
            return;
        }

        int r = (src[o] * 255 + (a >> 1)) / a;
        int g = (src[o + 1] * 255 + (a >> 1)) / a;
        int b = (src[o + 2] * 255 + (a >> 1)) / a;
        dst[o] = (byte)(r > 255 ? 255 : r);
        dst[o + 1] = (byte)(g > 255 ? 255 : g);
        dst[o + 2] = (byte)(b > 255 ? 255 : b);
        dst[o + 3] = (byte)a;
    }

    public static void SrgbToLinearFp32Kernel(
        Index1D idx,
        ArrayView1D<byte, Stride1D.Dense> src,
        ArrayView1D<float, Stride1D.Dense> dst,
        ArrayView1D<float, Stride1D.Dense> srgbToLinear,
        int channels,
        int linearAlpha)
    {
        if (linearAlpha != 0 && channels == 4 && (idx & 3) == 3)
        {
            dst[idx] = src[idx] * (1.0f / 255.0f);
        }
        else
        {
            dst[idx] = srgbToLinear[src[idx]];
        }
    }

    public static void LinearFp32ToSrgbKernel(
        Index1D idx,
        ArrayView1D<float, Stride1D.Dense> src,
        ArrayView1D<byte, Stride1D.Dense> dst,
        ArrayView1D<uint, Stride1D.Dense> fp32ToSrgbTab,
        int channels,
        int linearAlpha)
    {
        float v = src[idx];
        if (linearAlpha != 0 && channels == 4 && (idx & 3) == 3)
        {
            if (v <= 0.0f) dst[idx] = 0;
            else if (v >= 1.0f) dst[idx] = 255;
            else dst[idx] = (byte)(v * 255.0f + 0.5f);
        }
        else
        {
            dst[idx] = LinearToSrgbByteDevice(v, fp32ToSrgbTab);
        }
    }

    public static void FloatHeightmapToRgbaKernel(
        Index1D pixelIdx,
        ArrayView1D<float, Stride1D.Dense> src,
        ArrayView1D<byte, Stride1D.Dense> dst,
        float minVal,
        float invRange,
        byte alpha)
    {
        float t = (src[pixelIdx] - minVal) * invRange;
        if (t < 0.0f) t = 0.0f;
        else if (t > 1.0f) t = 1.0f;
        byte g = (byte)(t * 255.0f + 0.5f);
        int d = pixelIdx * 4;
        dst[d] = g;
        dst[d + 1] = g;
        dst[d + 2] = g;
        dst[d + 3] = alpha;
    }

    public static void FloatHeightmapToGreyKernel(
        Index1D pixelIdx,
        ArrayView1D<float, Stride1D.Dense> src,
        ArrayView1D<byte, Stride1D.Dense> dst,
        float minVal,
        float invRange)
    {
        float t = (src[pixelIdx] - minVal) * invRange;
        if (t < 0.0f) t = 0.0f;
        else if (t > 1.0f) t = 1.0f;
        dst[pixelIdx] = (byte)(t * 255.0f + 0.5f);
    }
}
