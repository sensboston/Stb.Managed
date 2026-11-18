using System.Runtime.CompilerServices;
using ILGPU;
using ILGPU.Algorithms;
using ILGPU.Runtime;

namespace Stb.Managed.Gpu.Kernels;

internal readonly struct DxtImageParams
{
    public readonly int Width;
    public readonly int Height;
    public readonly int SourceStrideBytes;
    public readonly int BlocksX;
    public readonly int BlocksY;
    public readonly int SourceImageBytes;
    public readonly int DestImageBytes;
    public readonly int IncludeAlpha;
    public readonly int HighQuality;

    public DxtImageParams(
        int width,
        int height,
        int sourceStrideBytes,
        int blocksX,
        int blocksY,
        int sourceImageBytes,
        int destImageBytes,
        int includeAlpha,
        int highQuality)
    {
        Width = width;
        Height = height;
        SourceStrideBytes = sourceStrideBytes;
        BlocksX = blocksX;
        BlocksY = blocksY;
        SourceImageBytes = sourceImageBytes;
        DestImageBytes = destImageBytes;
        IncludeAlpha = includeAlpha;
        HighQuality = highQuality;
    }
}

/// <summary>
/// ILGPU device kernels implementing bit-exact BC1 (Normal/HQ), BC3 (Normal/HQ), BC4, and BC5
/// compression matching <c>stb_dxt.h</c> v1.13 and <c>Stb.Managed.Dxt</c>.
/// One GPU thread processes one 4×4 block with zero heap or local-array allocations.
/// </summary>
internal static class DxtGpuKernels
{
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int ClampCoord(int c, int maxExclusive) =>
        c < maxExclusive ? c : maxExclusive - 1;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int Mul8Bit(int a, int b)
    {
        int t = a * b + 128;
        return (t + (t >> 8)) >> 8;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int As16Bit(int r, int g, int b) =>
        (Mul8Bit(r, 31) << 11) + (Mul8Bit(g, 63) << 5) + Mul8Bit(b, 31);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int Quantize5(float x, ArrayView1D<float, Stride1D.Dense> midpoints5)
    {
        x = x < 0.0f ? 0.0f : x > 1.0f ? 1.0f : x;
        int q = (int)(x * 31.0f);
        if (x > midpoints5[q])
        {
            q++;
        }

        return q;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int Quantize6(float x, ArrayView1D<float, Stride1D.Dense> midpoints6)
    {
        x = x < 0.0f ? 0.0f : x > 1.0f ? 1.0f : x;
        int q = (int)(x * 63.0f);
        if (x > midpoints6[q])
        {
            q++;
        }

        return q;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int PixelOffset(int imgBase, int bx, int by, int i, int width, int height, int stride, int channels)
    {
        int x = ClampCoord((bx << 2) + (i & 3), width);
        int y = ClampCoord((by << 2) + (i >> 2), height);
        return imgBase + y * stride + x * channels;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void CompressAlphaBlockDevice(
        ArrayView1D<byte, Stride1D.Dense> dst,
        int dstBase,
        ArrayView1D<byte, Stride1D.Dense> src,
        int imgBase,
        int bx,
        int by,
        int width,
        int height,
        int stride,
        int channels,
        int channelOffset)
    {
        int p0 = PixelOffset(imgBase, bx, by, 0, width, height, stride, channels) + channelOffset;
        int mn = src[p0];
        int mx = mn;

        for (int i = 1; i < 16; ++i)
        {
            int v = src[PixelOffset(imgBase, bx, by, i, width, height, stride, channels) + channelOffset];
            if (v < mn)
            {
                mn = v;
            }
            else if (v > mx)
            {
                mx = v;
            }
        }

        dst[dstBase] = (byte)mx;
        dst[dstBase + 1] = (byte)mn;

        int dist = mx - mn;
        if (dist == 0)
        {
            // Exact stb_dxt.h constant-alpha 48-bit index pattern 0x249249249249UL.
            dst[dstBase + 2] = 0x49;
            dst[dstBase + 3] = 0x92;
            dst[dstBase + 4] = 0x24;
            dst[dstBase + 5] = 0x49;
            dst[dstBase + 6] = 0x92;
            dst[dstBase + 7] = 0x24;
            return;
        }

        int dist4 = dist * 4;
        int dist2 = dist * 2;
        int bias = (dist < 8) ? (dist - 1) : ((dist >> 1) + 2);
        bias -= mn * 7;

        int bits = 0;
        int mask = 0;
        int dstIdx = dstBase + 2;

        for (int i = 0; i < 16; ++i)
        {
            int v = src[PixelOffset(imgBase, bx, by, i, width, height, stride, channels) + channelOffset];
            int a = v * 7 + bias;
            int t = (a >= dist4) ? -1 : 0;
            int ind = t & 4;
            a -= dist4 & t;

            t = (a >= dist2) ? -1 : 0;
            ind += t & 2;
            a -= dist2 & t;

            if (a >= dist)
            {
                ind++;
            }

            ind = (-ind) & 7;
            if (ind < 2)
            {
                ind ^= 1;
            }

            mask |= ind << bits;
            bits += 3;
            if (bits >= 8)
            {
                dst[dstIdx++] = (byte)mask;
                mask >>= 8;
                bits -= 8;
            }
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static uint MatchColorsDevice(
        ArrayView1D<byte, Stride1D.Dense> src,
        int imgBase,
        int bx,
        int by,
        int width,
        int height,
        int stride,
        int max16,
        int min16)
    {
        int c0r = (((max16 & 0xF800) >> 11) * 33) >> 2;
        int c0g = (((max16 & 0x07E0) >> 5) * 65) >> 4;
        int c0b = ((max16 & 0x001F) * 33) >> 2;

        int c1r = (((min16 & 0xF800) >> 11) * 33) >> 2;
        int c1g = (((min16 & 0x07E0) >> 5) * 65) >> 4;
        int c1b = ((min16 & 0x001F) * 33) >> 2;

        int c2r = (2 * c0r + c1r) / 3;
        int c2g = (2 * c0g + c1g) / 3;
        int c2b = (2 * c0b + c1b) / 3;

        int c3r = (2 * c1r + c0r) / 3;
        int c3g = (2 * c1g + c0g) / 3;
        int c3b = (2 * c1b + c0b) / 3;

        int dirr = c0r - c1r;
        int dirg = c0g - c1g;
        int dirb = c0b - c1b;

        int stop0 = c0r * dirr + c0g * dirg + c0b * dirb;
        int stop1 = c1r * dirr + c1g * dirg + c1b * dirb;
        int stop2 = c2r * dirr + c2g * dirg + c2b * dirb;
        int stop3 = c3r * dirr + c3g * dirg + c3b * dirb;

        int c0Point = stop1 + stop3;
        int halfPoint = stop3 + stop2;
        int c3Point = stop2 + stop0;

        uint mask = 0u;
        for (int i = 15; i >= 0; --i)
        {
            int o = PixelOffset(imgBase, bx, by, i, width, height, stride, 4);
            int dot = (src[o] * dirr + src[o + 1] * dirg + src[o + 2] * dirb) * 2;
            mask <<= 2;
            if (dot < halfPoint)
            {
                mask |= (dot < c0Point) ? 1u : 3u;
            }
            else
            {
                mask |= (dot < c3Point) ? 2u : 0u;
            }
        }

        return mask;
    }

    /// <summary>
    /// Compresses BC1 (DXT1) or BC3 (DXT5) blocks for a single image or a contiguous batch of equal-sized RGBA images.
    /// </summary>
    public static void CompressRgbaKernel(
        Index1D totalBlockIdx,
        ArrayView1D<byte, Stride1D.Dense> src,
        ArrayView1D<byte, Stride1D.Dense> dst,
        ArrayView1D<byte, Stride1D.Dense> omatch5,
        ArrayView1D<byte, Stride1D.Dense> omatch6,
        ArrayView1D<float, Stride1D.Dense> midpoints5,
        ArrayView1D<float, Stride1D.Dense> midpoints6,
        DxtImageParams p)
    {
        int blocksPerImage = p.BlocksX * p.BlocksY;
        int batchIdx = totalBlockIdx / blocksPerImage;
        int localBlock = totalBlockIdx - batchIdx * blocksPerImage;
        int by = localBlock / p.BlocksX;
        int bx = localBlock - by * p.BlocksX;

        int imgBase = batchIdx * p.SourceImageBytes;
        int blockBytes = p.IncludeAlpha != 0 ? 16 : 8;
        int dstBase = batchIdx * p.DestImageBytes + localBlock * blockBytes;

        int colorDstBase = dstBase;
        if (p.IncludeAlpha != 0)
        {
            CompressAlphaBlockDevice(dst, dstBase, src, imgBase, bx, by, p.Width, p.Height, p.SourceStrideBytes, 4, 3);
            colorDstBase = dstBase + 8;
        }

        // Check constant block (matching stb_dxt.h: ignore alpha when includeAlpha != 0).
        int p0 = PixelOffset(imgBase, bx, by, 0, p.Width, p.Height, p.SourceStrideBytes, 4);
        int r0 = src[p0];
        int g0 = src[p0 + 1];
        int b0 = src[p0 + 2];
        int a0 = src[p0 + 3];
        int isConstant = 1;
        for (int i = 1; i < 16; ++i)
        {
            int o = PixelOffset(imgBase, bx, by, i, p.Width, p.Height, p.SourceStrideBytes, 4);
            if (src[o] != r0 || src[o + 1] != g0 || src[o + 2] != b0 || (p.IncludeAlpha == 0 && src[o + 3] != a0))
            {
                isConstant = 0;
                break;
            }
        }

        int max16;
        int min16;
        uint mask;

        if (isConstant != 0)
        {
            mask = 0xAAAAAAAAu;
            max16 = (omatch5[r0 * 2] << 11) | (omatch6[g0 * 2] << 5) | omatch5[b0 * 2];
            min16 = (omatch5[r0 * 2 + 1] << 11) | (omatch6[g0 * 2 + 1] << 5) | omatch5[b0 * 2 + 1];
        }
        else
        {
            // OptimizeColorsBlock: PCA principal axis via 4 power iterations.
            int rSum = r0, gSum = g0, bSum = b0;
            int rMin = r0, rMax = r0;
            int gMin = g0, gMax = g0;
            int bMin = b0, bMax = b0;

            for (int i = 1; i < 16; ++i)
            {
                int o = PixelOffset(imgBase, bx, by, i, p.Width, p.Height, p.SourceStrideBytes, 4);
                int r = src[o];
                int g = src[o + 1];
                int b = src[o + 2];
                rSum += r;
                gSum += g;
                bSum += b;
                if (r < rMin) rMin = r;
                else if (r > rMax) rMax = r;
                if (g < gMin) gMin = g;
                else if (g > gMax) gMax = g;
                if (b < bMin) bMin = b;
                else if (b > bMax) bMax = b;
            }

            int muR = (rSum + 8) >> 4;
            int muG = (gSum + 8) >> 4;
            int muB = (bSum + 8) >> 4;

            int cov0 = 0, cov1 = 0, cov2 = 0, cov3 = 0, cov4 = 0, cov5 = 0;
            for (int i = 0; i < 16; ++i)
            {
                int o = PixelOffset(imgBase, bx, by, i, p.Width, p.Height, p.SourceStrideBytes, 4);
                int r = src[o] - muR;
                int g = src[o + 1] - muG;
                int b = src[o + 2] - muB;
                cov0 += r * r;
                cov1 += r * g;
                cov2 += r * b;
                cov3 += g * g;
                cov4 += g * b;
                cov5 += b * b;
            }

            const float Inv255 = 1.0f / 255.0f;
            float covf0 = cov0 * Inv255;
            float covf1 = cov1 * Inv255;
            float covf2 = cov2 * Inv255;
            float covf3 = cov3 * Inv255;
            float covf4 = cov4 * Inv255;
            float covf5 = cov5 * Inv255;

            float vfr = rMax - rMin;
            float vfg = gMax - gMin;
            float vfb = bMax - bMin;

            for (int iter = 0; iter < 4; ++iter)
            {
                float r = vfr * covf0 + vfg * covf1 + vfb * covf2;
                float g = vfr * covf1 + vfg * covf3 + vfb * covf4;
                float b = vfr * covf2 + vfg * covf4 + vfb * covf5;
                vfr = r;
                vfg = g;
                vfb = b;
            }

            float magn = XMath.Abs(vfr);
            float absG = XMath.Abs(vfg);
            float absB = XMath.Abs(vfb);
            if (absG > magn) magn = absG;
            if (absB > magn) magn = absB;

            int vr, vg, vb;
            if (magn < 4.0f)
            {
                vr = 299;
                vg = 587;
                vb = 114;
            }
            else
            {
                // Keep fp32-only math so OpenCL devices without cl_khr_fp64 compile cleanly.
                float invMagn = 512.0f / magn;
                vr = (int)(vfr * invMagn);
                vg = (int)(vfg * invMagn);
                vb = (int)(vfb * invMagn);
            }

            int minOff = p0;
            int maxOff = p0;
            int minD = r0 * vr + g0 * vg + b0 * vb;
            int maxD = minD;

            for (int i = 1; i < 16; ++i)
            {
                int o = PixelOffset(imgBase, bx, by, i, p.Width, p.Height, p.SourceStrideBytes, 4);
                int dot = src[o] * vr + src[o + 1] * vg + src[o + 2] * vb;
                if (dot < minD)
                {
                    minD = dot;
                    minOff = o;
                }

                if (dot > maxD)
                {
                    maxD = dot;
                    maxOff = o;
                }
            }

            max16 = As16Bit(src[maxOff], src[maxOff + 1], src[maxOff + 2]);
            min16 = As16Bit(src[minOff], src[minOff + 1], src[minOff + 2]);

            mask = max16 != min16
                ? MatchColorsDevice(src, imgBase, bx, by, p.Width, p.Height, p.SourceStrideBytes, max16, min16)
                : 0u;

            int refineCount = p.HighQuality != 0 ? 2 : 1;
            for (int iter = 0; iter < refineCount; ++iter)
            {
                uint lastMask = mask;
                int oldMin = min16;
                int oldMax = max16;

                if ((mask ^ (mask << 2)) < 4u)
                {
                    int r = 8, g = 8, b = 8;
                    for (int i = 0; i < 16; ++i)
                    {
                        int o = PixelOffset(imgBase, bx, by, i, p.Width, p.Height, p.SourceStrideBytes, 4);
                        r += src[o];
                        g += src[o + 1];
                        b += src[o + 2];
                    }

                    r >>= 4;
                    g >>= 4;
                    b >>= 4;

                    max16 = (omatch5[r * 2] << 11) | (omatch6[g * 2] << 5) | omatch5[b * 2];
                    min16 = (omatch5[r * 2 + 1] << 11) | (omatch6[g * 2 + 1] << 5) | omatch5[b * 2 + 1];
                }
                else
                {
                    int akku = 0;
                    int at1R = 0, at1G = 0, at1B = 0;
                    int at2R = 0, at2G = 0, at2B = 0;
                    uint cm = mask;

                    for (int i = 0; i < 16; ++i)
                    {
                        int step = (int)(cm & 3u);
                        cm >>= 2;

                        int w1;
                        int prod;
                        if (step == 0) { w1 = 3; prod = 0x090000; }
                        else if (step == 1) { w1 = 0; prod = 0x000900; }
                        else if (step == 2) { w1 = 2; prod = 0x040102; }
                        else { w1 = 1; prod = 0x010402; }

                        int o = PixelOffset(imgBase, bx, by, i, p.Width, p.Height, p.SourceStrideBytes, 4);
                        int r = src[o];
                        int g = src[o + 1];
                        int b = src[o + 2];

                        akku += prod;
                        at1R += w1 * r;
                        at1G += w1 * g;
                        at1B += w1 * b;
                        at2R += r;
                        at2G += g;
                        at2B += b;
                    }

                    at2R = 3 * at2R - at1R;
                    at2G = 3 * at2G - at1G;
                    at2B = 3 * at2B - at1B;

                    int xx = akku >> 16;
                    int yy = (akku >> 8) & 0xFF;
                    int xy = akku & 0xFF;

                    float f = 3.0f / 255.0f / (xx * yy - xy * xy);

                    max16 = (Quantize5((at1R * yy - at2R * xy) * f, midpoints5) << 11)
                        | (Quantize6((at1G * yy - at2G * xy) * f, midpoints6) << 5)
                        | Quantize5((at1B * yy - at2B * xy) * f, midpoints5);

                    min16 = (Quantize5((at2R * xx - at1R * xy) * f, midpoints5) << 11)
                        | (Quantize6((at2G * xx - at1G * xy) * f, midpoints6) << 5)
                        | Quantize5((at2B * xx - at1B * xy) * f, midpoints5);
                }

                if (oldMin != min16 || oldMax != max16)
                {
                    if (max16 != min16)
                    {
                        mask = MatchColorsDevice(src, imgBase, bx, by, p.Width, p.Height, p.SourceStrideBytes, max16, min16);
                    }
                    else
                    {
                        mask = 0u;
                        break;
                    }
                }

                if (mask == lastMask)
                {
                    break;
                }
            }
        }

        if (max16 < min16)
        {
            int tmp = max16;
            max16 = min16;
            min16 = tmp;
            mask ^= 0x55555555u;
        }

        dst[colorDstBase] = (byte)max16;
        dst[colorDstBase + 1] = (byte)(max16 >> 8);
        dst[colorDstBase + 2] = (byte)min16;
        dst[colorDstBase + 3] = (byte)(min16 >> 8);
        dst[colorDstBase + 4] = (byte)mask;
        dst[colorDstBase + 5] = (byte)(mask >> 8);
        dst[colorDstBase + 6] = (byte)(mask >> 16);
        dst[colorDstBase + 7] = (byte)(mask >> 24);
    }

    /// <summary>
    /// Compresses BC4 (1-channel) blocks for a single image or a contiguous batch.
    /// </summary>
    public static void CompressBc4Kernel(
        Index1D totalBlockIdx,
        ArrayView1D<byte, Stride1D.Dense> src,
        ArrayView1D<byte, Stride1D.Dense> dst,
        DxtImageParams p)
    {
        int blocksPerImage = p.BlocksX * p.BlocksY;
        int batchIdx = totalBlockIdx / blocksPerImage;
        int localBlock = totalBlockIdx - batchIdx * blocksPerImage;
        int by = localBlock / p.BlocksX;
        int bx = localBlock - by * p.BlocksX;

        int imgBase = batchIdx * p.SourceImageBytes;
        int dstBase = batchIdx * p.DestImageBytes + localBlock * 8;
        CompressAlphaBlockDevice(dst, dstBase, src, imgBase, bx, by, p.Width, p.Height, p.SourceStrideBytes, 1, 0);
    }

    /// <summary>
    /// Compresses BC5 (2-channel) blocks for a single image or a contiguous batch.
    /// </summary>
    public static void CompressBc5Kernel(
        Index1D totalBlockIdx,
        ArrayView1D<byte, Stride1D.Dense> src,
        ArrayView1D<byte, Stride1D.Dense> dst,
        DxtImageParams p)
    {
        int blocksPerImage = p.BlocksX * p.BlocksY;
        int batchIdx = totalBlockIdx / blocksPerImage;
        int localBlock = totalBlockIdx - batchIdx * blocksPerImage;
        int by = localBlock / p.BlocksX;
        int bx = localBlock - by * p.BlocksX;

        int imgBase = batchIdx * p.SourceImageBytes;
        int dstBase = batchIdx * p.DestImageBytes + localBlock * 16;
        CompressAlphaBlockDevice(dst, dstBase, src, imgBase, bx, by, p.Width, p.Height, p.SourceStrideBytes, 2, 0);
        CompressAlphaBlockDevice(dst, dstBase + 8, src, imgBase, bx, by, p.Width, p.Height, p.SourceStrideBytes, 2, 1);
    }
}
