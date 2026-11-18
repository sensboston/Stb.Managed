using System.Runtime.CompilerServices;
using ILGPU;
using ILGPU.Algorithms;
using ILGPU.Runtime;

namespace Stb.Managed.Gpu.Kernels;

internal readonly struct GpuSdfLine
{
    public readonly float X0, Y0, Dx, Dy, InvLen, InvLen2, BoxX0, BoxY0, BoxX1, BoxY1;

    public GpuSdfLine(float x0, float y0, float x1, float y1, float invLen)
    {
        X0 = x0;
        Y0 = y0;
        Dx = x1 - x0;
        Dy = y1 - y0;
        InvLen = invLen;
        InvLen2 = 1.0f / (Dx * Dx + Dy * Dy);
        BoxX0 = x0 < x1 ? x0 : x1;
        BoxY0 = y0 < y1 ? y0 : y1;
        BoxX1 = x0 > x1 ? x0 : x1;
        BoxY1 = y0 > y1 ? y0 : y1;
    }
}

internal readonly struct GpuSdfCurve
{
    public readonly float X0, Y0, X1, Y1, X2, Y2;
    public readonly float Ax, Ay, Bx, By;
    public readonly float AInv;
    public readonly float BoxX0, BoxY0, BoxX1, BoxY1;
    public readonly float TwoDotAA;
    public readonly float ThreeDotAB;
    public readonly float BConst;

    public GpuSdfCurve(float x0, float y0, float x1, float y1, float x2, float y2, float aInv)
    {
        X0 = x0;
        Y0 = y0;
        X1 = x1;
        Y1 = y1;
        X2 = x2;
        Y2 = y2;
        Ax = x1 - x0;
        Ay = y1 - y0;
        Bx = x0 - 2.0f * x1 + x2;
        By = y0 - 2.0f * y1 + y2;
        AInv = aInv;
        BoxX0 = MathF.Min(MathF.Min(x0, x1), x2);
        BoxY0 = MathF.Min(MathF.Min(y0, y1), y2);
        BoxX1 = MathF.Max(MathF.Max(x0, x1), x2);
        BoxY1 = MathF.Max(MathF.Max(y0, y1), y2);
        TwoDotAA = 2.0f * (Ax * Ax + Ay * Ay);
        ThreeDotAB = 3.0f * (Ax * Bx + Ay * By);
        BConst = ThreeDotAB * aInv;
    }
}

internal readonly struct GpuSdfCrossEdge
{
    public readonly int IsCurve; // 0 = line, 1 = quadratic Bezier
    public readonly float X0, Y0, X1, Y1, X2, Y2;
    public readonly float MinX, MinY, MaxY;
    public readonly float InvDy;
    public readonly float A, B;
    public readonly int LineWindingDir;

    public GpuSdfCrossEdge(int x0, int y0, int x1, int y1)
    {
        IsCurve = 0;
        X0 = x0;
        Y0 = y0;
        X1 = x1;
        Y1 = y1;
        X2 = 0.0f;
        Y2 = 0.0f;
        MinX = x0 < x1 ? x0 : x1;
        MinY = y0 < y1 ? y0 : y1;
        MaxY = y0 > y1 ? y0 : y1;
        InvDy = y1 != y0 ? 1.0f / (y1 - y0) : 0.0f;
        A = 0.0f;
        B = 0.0f;
        LineWindingDir = y0 < y1 ? 1 : -1;
    }

    public GpuSdfCrossEdge(int x0, int y0, int x1, int y1, int x2, int y2)
    {
        IsCurve = 1;
        X0 = x0;
        Y0 = y0;
        X1 = x1;
        Y1 = y1;
        X2 = x2;
        Y2 = y2;
        MinX = Math.Min(x0, Math.Min(x1, x2));
        MinY = Math.Min(y0, Math.Min(y1, y2));
        MaxY = Math.Max(y0, Math.Max(y1, y2));
        InvDy = 0.0f;
        A = y0 - 2.0f * y1 + y2;
        B = y1 - y0;
        LineWindingDir = 0;
    }
}

internal readonly struct GpuSdfGlyphDesc
{
    public readonly int PixelOffset;
    public readonly int PixelEnd;
    public readonly int Width;
    public readonly int Height;
    public readonly int Ix0;
    public readonly int Iy0;
    public readonly int LineOffset;
    public readonly int LineCount;
    public readonly int CurveOffset;
    public readonly int CurveCount;
    public readonly int CrossOffset;
    public readonly int CrossCount;
    public readonly float ScaleX;
    public readonly float ScaleY;
    public readonly float OnEdgeValue;
    public readonly float PixelDistScale;

    public GpuSdfGlyphDesc(
        int pixelOffset,
        int pixelEnd,
        int width,
        int height,
        int ix0,
        int iy0,
        int lineOffset,
        int lineCount,
        int curveOffset,
        int curveCount,
        int crossOffset,
        int crossCount,
        float scaleX,
        float scaleY,
        float onEdgeValue,
        float pixelDistScale)
    {
        PixelOffset = pixelOffset;
        PixelEnd = pixelEnd;
        Width = width;
        Height = height;
        Ix0 = ix0;
        Iy0 = iy0;
        LineOffset = lineOffset;
        LineCount = lineCount;
        CurveOffset = curveOffset;
        CurveCount = curveCount;
        CrossOffset = crossOffset;
        CrossCount = crossCount;
        ScaleX = scaleX;
        ScaleY = scaleY;
        OnEdgeValue = onEdgeValue;
        PixelDistScale = pixelDistScale;
    }
}

/// <summary>
/// ILGPU batch Signed Distance Field (SDF) kernel matching <c>stb_truetype.h</c> v1.26 and <c>TrueTypeSdf</c>.
/// Evaluates all pixels of all glyphs in a font atlas in a single massively parallel GPU launch.
/// </summary>
internal static class TrueTypeSdfGpuKernels
{
    private const float Eps = 1.0f / 1024.0f;
    private const float Eps2 = Eps * Eps;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static float CubeRootDevice(float x) =>
        x < 0.0f ? -XMath.Pow(-x, 1.0f / 3.0f) : XMath.Pow(x, 1.0f / 3.0f);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int SolveCubicDevice(float a, float b, float c, out float r0, out float r1, out float r2)
    {
        float s = -a / 3.0f;
        float p = b - a * a / 3.0f;
        float q = a * (2.0f * a * a - 9.0f * b) / 27.0f + c;
        float p3 = p * p * p;
        float d = q * q + 4.0f * p3 / 27.0f;

        if (d >= 0.0f)
        {
            float z = XMath.Sqrt(d);
            float u = CubeRootDevice((-q + z) * 0.5f);
            float v = CubeRootDevice((-q - z) * 0.5f);
            r0 = s + u + v;
            r1 = 0.0f;
            r2 = 0.0f;
            return 1;
        }
        else
        {
            float u = XMath.Sqrt(-p / 3.0f);
            float arg = -XMath.Sqrt(-27.0f / p3) * q * 0.5f;
            arg = XMath.Clamp(arg, -1.0f, 1.0f);
            float v = XMath.Acos(arg) / 3.0f;
            float m = XMath.Cos(v);
            float n = XMath.Cos(v - 3.141592f * 0.5f) * 1.732050808f;
            r0 = s + u * 2.0f * m;
            r1 = s - u * (m + n);
            r2 = s - u * (m - n);
            return 3;
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int ComputeCrossingsXDevice(
        float x,
        float y,
        ArrayView1D<GpuSdfCrossEdge, Stride1D.Dense> edges,
        int crossOffset,
        int crossCount)
    {
        float yFrac = y - (int)y;
        if (yFrac < 0.01f)
        {
            y += 0.01f;
        }
        else if (yFrac > 0.99f)
        {
            y -= 0.01f;
        }

        int winding = 0;
        int end = crossOffset + crossCount;
        for (int i = crossOffset; i < end; ++i)
        {
            GpuSdfCrossEdge e = edges[i];
            if (y <= e.MinY || y >= e.MaxY || x <= e.MinX)
            {
                continue;
            }

            if (e.IsCurve == 0)
            {
                float xInter = (y - e.Y0) * e.InvDy * (e.X1 - e.X0) + e.X0;
                if (xInter < x)
                {
                    winding += e.LineWindingDir;
                }
            }
            else
            {
                float a = e.A;
                float b = e.B;
                float c = e.Y0 - y;

                float s0 = 0.0f;
                float s1 = 0.0f;
                int numS = 0;

                if (a != 0.0f)
                {
                    float discr = b * b - a * c;
                    if (discr > 0.0f)
                    {
                        float rcpna = -1.0f / a;
                        float d = XMath.Sqrt(discr);
                        s0 = (b + d) * rcpna;
                        s1 = (b - d) * rcpna;
                        if (s0 >= 0.0f && s0 <= 1.0f)
                        {
                            numS = 1;
                        }

                        if (d > 0.0f && s1 >= 0.0f && s1 <= 1.0f)
                        {
                            if (numS == 0)
                            {
                                s0 = s1;
                            }

                            ++numS;
                        }
                    }
                }
                else
                {
                    s0 = c / (-2.0f * b);
                    if (s0 >= 0.0f && s0 <= 1.0f)
                    {
                        numS = 1;
                    }
                }

                if (numS != 0)
                {
                    float q10d = e.X1 - e.X0;
                    float q20d = e.X2 - e.X0;
                    float q0rd = e.X0 - x;

                    float hit0 = q0rd + s0 * (2.0f - 2.0f * s0) * q10d + s0 * s0 * q20d;
                    if (hit0 < 0.0f)
                    {
                        winding += (a * s0 + b) < 0.0f ? -1 : 1;
                    }

                    if (numS > 1)
                    {
                        float hit1 = q0rd + s1 * (2.0f - 2.0f * s1) * q10d + s1 * s1 * q20d;
                        if (hit1 < 0.0f)
                        {
                            winding += (a * s1 + b) < 0.0f ? -1 : 1;
                        }
                    }
                }
            }
        }

        return winding;
    }

    /// <summary>
    /// Renders one SDF pixel across an entire batch of glyphs (binary-searching the glyph descriptor table).
    /// </summary>
    public static void RenderSdfBatchKernel(
        Index1D pixelIdx,
        ArrayView1D<byte, Stride1D.Dense> dst,
        ArrayView1D<GpuSdfGlyphDesc, Stride1D.Dense> glyphs,
        ArrayView1D<GpuSdfLine, Stride1D.Dense> lines,
        ArrayView1D<GpuSdfCurve, Stride1D.Dense> curves,
        ArrayView1D<GpuSdfCrossEdge, Stride1D.Dense> crossEdges,
        int glyphCount)
    {
        // Binary search to locate the glyph containing pixelIdx.
        int lo = 0;
        int hi = glyphCount - 1;
        int gIdx = 0;
        while (lo <= hi)
        {
            int mid = (lo + hi) >> 1;
            GpuSdfGlyphDesc m = glyphs[mid];
            if (pixelIdx < m.PixelOffset)
            {
                hi = mid - 1;
            }
            else if (pixelIdx >= m.PixelEnd)
            {
                lo = mid + 1;
            }
            else
            {
                gIdx = mid;
                break;
            }
        }

        GpuSdfGlyphDesc g = glyphs[gIdx];
        int localPix = pixelIdx - g.PixelOffset;
        int row = localPix / g.Width;
        int col = localPix - row * g.Width;

        float sx = (g.Ix0 + col) + 0.5f;
        float sy = (g.Iy0 + row) + 0.5f;
        float xGspace = sx / g.ScaleX;
        float yGspace = sy / g.ScaleY;

        int winding = ComputeCrossingsXDevice(xGspace, yGspace, crossEdges, g.CrossOffset, g.CrossCount);

        float minDist = 999999.0f;
        float minDist2 = minDist * minDist;

        int lineEnd = g.LineOffset + g.LineCount;
        for (int i = g.LineOffset; i < lineEnd; ++i)
        {
            GpuSdfLine seg = lines[i];
            if (sx <= seg.BoxX0 - minDist || sx >= seg.BoxX1 + minDist ||
                sy <= seg.BoxY0 - minDist || sy >= seg.BoxY1 + minDist)
            {
                continue;
            }

            float px = seg.X0 - sx;
            float py = seg.Y0 - sy;
            float dist2 = px * px + py * py;
            if (dist2 < minDist2)
            {
                minDist2 = dist2;
                minDist = XMath.Sqrt(dist2);
            }

            float dist = XMath.Abs(seg.Dx * py - seg.Dy * px) * seg.InvLen;
            if (dist < minDist)
            {
                float t = -(px * seg.Dx + py * seg.Dy) * seg.InvLen2;
                if (t >= 0.0f && t <= 1.0f)
                {
                    minDist = dist;
                    minDist2 = dist * dist;
                }
            }
        }

        int curveEnd = g.CurveOffset + g.CurveCount;
        for (int i = g.CurveOffset; i < curveEnd; ++i)
        {
            GpuSdfCurve seg = curves[i];
            if (sx <= seg.BoxX0 - minDist || sx >= seg.BoxX1 + minDist ||
                sy <= seg.BoxY0 - minDist || sy >= seg.BoxY1 + minDist)
            {
                continue;
            }

            float mx = seg.X0 - sx;
            float my = seg.Y0 - sy;
            float r0 = 0.0f, r1 = 0.0f, r2 = 0.0f;
            int num = 0;

            if (seg.AInv == 0.0f)
            {
                float a = seg.ThreeDotAB;
                float b = seg.TwoDotAA + (mx * seg.Bx + my * seg.By);
                float c = mx * seg.Ax + my * seg.Ay;
                if (XMath.Abs(a) < Eps2)
                {
                    if (XMath.Abs(b) >= Eps2)
                    {
                        r0 = -c / b;
                        num = 1;
                    }
                }
                else
                {
                    float discriminant = b * b - 4.0f * a * c;
                    if (discriminant >= 0.0f)
                    {
                        float root = XMath.Sqrt(discriminant);
                        float inv2a = 1.0f / (2.0f * a);
                        r0 = (-b - root) * inv2a;
                        r1 = (-b + root) * inv2a;
                        num = 2;
                    }
                }
            }
            else
            {
                float b = seg.BConst;
                float c = (seg.TwoDotAA + (mx * seg.Bx + my * seg.By)) * seg.AInv;
                float d = (mx * seg.Ax + my * seg.Ay) * seg.AInv;
                num = SolveCubicDevice(b, c, d, out r0, out r1, out r2);
            }

            float dist2 = mx * mx + my * my;
            if (dist2 < minDist2)
            {
                minDist2 = dist2;
                minDist = XMath.Sqrt(dist2);
            }

            if (num >= 1 && r0 >= 0.0f && r0 <= 1.0f)
            {
                float it = 1.0f - r0;
                float px = it * it * seg.X0 + 2.0f * r0 * it * seg.X1 + r0 * r0 * seg.X2 - sx;
                float py = it * it * seg.Y0 + 2.0f * r0 * it * seg.Y1 + r0 * r0 * seg.Y2 - sy;
                dist2 = px * px + py * py;
                if (dist2 < minDist2)
                {
                    minDist2 = dist2;
                    minDist = XMath.Sqrt(dist2);
                }
            }

            if (num >= 2 && r1 >= 0.0f && r1 <= 1.0f)
            {
                float it = 1.0f - r1;
                float px = it * it * seg.X0 + 2.0f * r1 * it * seg.X1 + r1 * r1 * seg.X2 - sx;
                float py = it * it * seg.Y0 + 2.0f * r1 * it * seg.Y1 + r1 * r1 * seg.Y2 - sy;
                dist2 = px * px + py * py;
                if (dist2 < minDist2)
                {
                    minDist2 = dist2;
                    minDist = XMath.Sqrt(dist2);
                }
            }

            if (num >= 3 && r2 >= 0.0f && r2 <= 1.0f)
            {
                float it = 1.0f - r2;
                float px = it * it * seg.X0 + 2.0f * r2 * it * seg.X1 + r2 * r2 * seg.X2 - sx;
                float py = it * it * seg.Y0 + 2.0f * r2 * it * seg.Y1 + r2 * r2 * seg.Y2 - sy;
                dist2 = px * px + py * py;
                if (dist2 < minDist2)
                {
                    minDist2 = dist2;
                    minDist = XMath.Sqrt(dist2);
                }
            }
        }

        if (winding == 0)
        {
            minDist = -minDist;
        }

        float val = g.OnEdgeValue + g.PixelDistScale * minDist;
        if (val < 0.0f)
        {
            val = 0.0f;
        }
        else if (val > 255.0f)
        {
            val = 255.0f;
        }

        dst[pixelIdx] = (byte)val;
    }
}
