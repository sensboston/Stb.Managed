using System.Buffers;
using System.Runtime.CompilerServices;

namespace Stb.Managed.TrueType.Internal;

internal static class TrueTypeSdf
{
    private const float Eps = 1.0f / 1024.0f;
    private const float Eps2 = Eps * Eps;
    private const long ParallelWorkThreshold = 150_000;

    private readonly struct SdfLineSegment
    {
        public readonly float X0, Y0, X1, Y1;
        public readonly float Dx, Dy;
        public readonly float InvLen;
        public readonly float InvLen2;
        public readonly float BoxX0, BoxY0, BoxX1, BoxY1;

        public SdfLineSegment(float x0, float y0, float x1, float y1, float invLen)
        {
            X0 = x0;
            Y0 = y0;
            X1 = x1;
            Y1 = y1;
            Dx = x1 - x0;
            Dy = y1 - y0;
            InvLen = invLen;
            InvLen2 = 1.0f / (Dx * Dx + Dy * Dy);
            BoxX0 = MathF.Min(x0, x1);
            BoxY0 = MathF.Min(y0, y1);
            BoxX1 = MathF.Max(x0, x1);
            BoxY1 = MathF.Max(y0, y1);
        }
    }

    private readonly struct SdfCurveSegment
    {
        public readonly float X0, Y0, X1, Y1, X2, Y2;
        public readonly float Ax, Ay, Bx, By;
        public readonly float AInv;
        public readonly float BoxX0, BoxY0, BoxX1, BoxY1;
        public readonly float TwoDotAA;
        public readonly float ThreeDotAB;
        public readonly float BConst;

        public SdfCurveSegment(float x0, float y0, float x1, float y1, float x2, float y2, float aInv)
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

    private readonly struct SdfCrossingEdge
    {
        public readonly bool IsCurve;
        public readonly float X0, Y0, X1, Y1, X2, Y2;
        public readonly float MinX, MinY, MaxY;
        public readonly float InvDy;
        public readonly float A, B;
        public readonly int LineWindingDir;

        public SdfCrossingEdge(int x0, int y0, int x1, int y1)
        {
            IsCurve = false;
            X0 = x0;
            Y0 = y0;
            X1 = x1;
            Y1 = y1;
            X2 = 0;
            Y2 = 0;
            MinX = Math.Min(x0, x1);
            MinY = Math.Min(y0, y1);
            MaxY = Math.Max(y0, y1);
            InvDy = y1 != y0 ? 1.0f / (y1 - y0) : 0.0f;
            A = 0;
            B = 0;
            LineWindingDir = y0 < y1 ? 1 : -1;
        }

        public SdfCrossingEdge(int x0, int y0, int x1, int y1, int x2, int y2)
        {
            IsCurve = true;
            X0 = x0;
            Y0 = y0;
            X1 = x1;
            Y1 = y1;
            X2 = x2;
            Y2 = y2;
            MinX = Math.Min(x0, Math.Min(x1, x2));
            MinY = Math.Min(y0, Math.Min(y1, y2));
            MaxY = Math.Max(y0, Math.Max(y1, y2));
            InvDy = 0;
            // Ray is (1, 0), so qperp = q.y.
            A = y0 - 2.0f * y1 + y2;
            B = y1 - y0;
            LineWindingDir = 0;
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int ComputeCrossingsX(float x, float y, ReadOnlySpan<SdfCrossingEdge> edges)
    {
        float yFrac = y % 1.0f;
        if (yFrac < 0.01f)
        {
            y += 0.01f;
        }
        else if (yFrac > 0.99f)
        {
            y -= 0.01f;
        }

        int winding = 0;
        for (int i = 0; i < edges.Length; ++i)
        {
            ref readonly SdfCrossingEdge e = ref edges[i];
            if (y <= e.MinY || y >= e.MaxY || x <= e.MinX)
            {
                continue;
            }

            if (!e.IsCurve)
            {
                float xInter = (y - e.Y0) * e.InvDy * (e.X1 - e.X0) + e.X0;
                if (xInter < x)
                {
                    winding += e.LineWindingDir;
                }
            }
            else
            {
                // Ray intersection with ray = (1, 0), orig = (x, y).
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
                        float d = MathF.Sqrt(discr);
                        s0 = (b + d) * rcpna;
                        s1 = (b - d) * rcpna;
                        if (s0 >= 0.0f && s0 <= 1.0f)
                        {
                            numS = 1;
                        }

                        if (d > 0.0f && s1 >= 0.0f && s1 <= 1.0f)
                        {
                            if (numS == 0) s0 = s1;
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

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static float CubeRoot(float x) =>
        x < 0.0f ? -MathF.Pow(-x, 1.0f / 3.0f) : MathF.Pow(x, 1.0f / 3.0f);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int SolveCubic(float a, float b, float c, out float r0, out float r1, out float r2)
    {
        float s = -a / 3.0f;
        float p = b - a * a / 3.0f;
        float q = a * (2.0f * a * a - 9.0f * b) / 27.0f + c;
        float p3 = p * p * p;
        float d = q * q + 4.0f * p3 / 27.0f;

        if (d >= 0.0f)
        {
            float z = MathF.Sqrt(d);
            float u = CubeRoot((-q + z) * 0.5f);
            float v = CubeRoot((-q - z) * 0.5f);
            r0 = s + u + v;
            r1 = 0.0f;
            r2 = 0.0f;
            return 1;
        }
        else
        {
            float u = MathF.Sqrt(-p / 3.0f);
            float v = MathF.Acos(-MathF.Sqrt(-27.0f / p3) * q * 0.5f) / 3.0f;
            float m = MathF.Cos(v);
            float n = MathF.Cos(v - 3.141592f * 0.5f) * 1.732050808f;
            r0 = s + u * 2.0f * m;
            r1 = s - u * (m + n);
            r2 = s - u * (m - n);
            return 3;
        }
    }

    internal static void RenderSdf(
        byte[] output,
        int outputOffset,
        int w,
        int h,
        int stride,
        int ix0,
        int iy0,
        float scale,
        byte onEdgeValue,
        float pixelDistScale,
        ReadOnlySpan<GlyphVertex> verts,
        StbOptions? options)
    {
        float scaleX = scale;
        float scaleY = -scale;
        int numVerts = verts.Length;

        // Precompute compact segment arrays once per glyph (hoisting O(V) math out of the O(W*H*V) loop).
        SdfLineSegment[] linePool = ArrayPool<SdfLineSegment>.Shared.Rent(Math.Max(1, numVerts));
        SdfCurveSegment[] curvePool = ArrayPool<SdfCurveSegment>.Shared.Rent(Math.Max(1, numVerts));
        SdfCrossingEdge[] crossPool = ArrayPool<SdfCrossingEdge>.Shared.Rent(Math.Max(1, numVerts));

        int lineCount = 0;
        int curveCount = 0;
        int crossCount = 0;

        try
        {
            for (int i = 0; i < numVerts; ++i)
            {
                GlyphVertex vi = verts[i];
                if (vi.Type == GlyphVertexType.Line && i > 0)
                {
                    GlyphVertex prev = verts[i - 1];
                    float x0 = vi.X * scaleX;
                    float y0 = vi.Y * scaleY;
                    float x1 = prev.X * scaleX;
                    float y1 = prev.Y * scaleY;
                    float dist = MathF.Sqrt((x1 - x0) * (x1 - x0) + (y1 - y0) * (y1 - y0));
                    if (dist >= Eps)
                    {
                        linePool[lineCount++] = new SdfLineSegment(x0, y0, x1, y1, 1.0f / dist);
                    }

                    if (prev.Y != vi.Y)
                    {
                        crossPool[crossCount++] = new SdfCrossingEdge(prev.X, prev.Y, vi.X, vi.Y);
                    }
                }
                else if (vi.Type == GlyphVertexType.Curve && i > 0)
                {
                    GlyphVertex prev = verts[i - 1];
                    float x0 = vi.X * scaleX;
                    float y0 = vi.Y * scaleY;
                    float x1 = vi.Cx * scaleX;
                    float y1 = vi.Cy * scaleY;
                    float x2 = prev.X * scaleX;
                    float y2 = prev.Y * scaleY;
                    float bx = x0 - 2.0f * x1 + x2;
                    float by = y0 - 2.0f * y1 + y2;
                    float len2 = bx * bx + by * by;
                    float aInv = len2 >= Eps2 ? 1.0f / len2 : 0.0f;
                    curvePool[curveCount++] = new SdfCurveSegment(x0, y0, x1, y1, x2, y2, aInv);

                    if ((prev.X == vi.Cx && prev.Y == vi.Cy) || (vi.Cx == vi.X && vi.Cy == vi.Y))
                    {
                        if (prev.Y != vi.Y)
                        {
                            crossPool[crossCount++] = new SdfCrossingEdge(prev.X, prev.Y, vi.X, vi.Y);
                        }
                    }
                    else
                    {
                        crossPool[crossCount++] = new SdfCrossingEdge(prev.X, prev.Y, vi.Cx, vi.Cy, vi.X, vi.Y);
                    }
                }
            }

            long workUnits = (long)w * h * Math.Max(1, lineCount + curveCount);
            int dop = (options ?? StbOptions.Default).ResolveDegreeOfParallelism(workUnits, ParallelWorkThreshold);

            if (dop > 1 && h >= 4)
            {
                Parallel.For(0, h, new ParallelOptions { MaxDegreeOfParallelism = dop }, row =>
                {
                    RenderSdfRow(
                        output.AsSpan(outputOffset + row * stride, w),
                        ix0,
                        iy0 + row,
                        scaleX,
                        scaleY,
                        onEdgeValue,
                        pixelDistScale,
                        linePool.AsSpan(0, lineCount),
                        curvePool.AsSpan(0, curveCount),
                        crossPool.AsSpan(0, crossCount));
                });
            }
            else
            {
                for (int row = 0; row < h; ++row)
                {
                    RenderSdfRow(
                        output.AsSpan(outputOffset + row * stride, w),
                        ix0,
                        iy0 + row,
                        scaleX,
                        scaleY,
                        onEdgeValue,
                        pixelDistScale,
                        linePool.AsSpan(0, lineCount),
                        curvePool.AsSpan(0, curveCount),
                        crossPool.AsSpan(0, crossCount));
                }
            }
        }
        finally
        {
            ArrayPool<SdfLineSegment>.Shared.Return(linePool);
            ArrayPool<SdfCurveSegment>.Shared.Return(curvePool);
            ArrayPool<SdfCrossingEdge>.Shared.Return(crossPool);
        }
    }

    private static void RenderSdfRow(
        Span<byte> rowDst,
        int ix0,
        int y,
        float scaleX,
        float scaleY,
        byte onEdgeValue,
        float pixelDistScale,
        ReadOnlySpan<SdfLineSegment> lines,
        ReadOnlySpan<SdfCurveSegment> curves,
        ReadOnlySpan<SdfCrossingEdge> crossEdges)
    {
        float sy = y + 0.5f;
        float invScaleX = 1.0f / scaleX;
        float yGspace = sy / scaleY;
        int w = rowDst.Length;

        for (int col = 0; col < w; ++col)
        {
            float sx = (ix0 + col) + 0.5f;
            float xGspace = sx * invScaleX;

            int winding = ComputeCrossingsX(xGspace, yGspace, crossEdges);

            float minDist = 999999.0f;
            float minDist2 = minDist * minDist;

            // Process straight line segments.
            for (int i = 0; i < lines.Length; ++i)
            {
                ref readonly SdfLineSegment seg = ref lines[i];

                // Coarse bbox cull against current minDist.
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
                    minDist = MathF.Sqrt(dist2);
                }

                float dist = MathF.Abs(seg.Dx * py - seg.Dy * px) * seg.InvLen;
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

            // Process quadratic Bezier segments.
            for (int i = 0; i < curves.Length; ++i)
            {
                ref readonly SdfCurveSegment seg = ref curves[i];
                if (sx <= seg.BoxX0 - minDist || sx >= seg.BoxX1 + minDist ||
                    sy <= seg.BoxY0 - minDist || sy >= seg.BoxY1 + minDist)
                {
                    continue;
                }

                float mx = seg.X0 - sx;
                float my = seg.Y0 - sy;
                float r0 = 0.0f;
                float r1 = 0.0f;
                float r2 = 0.0f;
                int num = 0;

                if (seg.AInv == 0.0f)
                {
                    float a = seg.ThreeDotAB;
                    float b = seg.TwoDotAA + (mx * seg.Bx + my * seg.By);
                    float c = mx * seg.Ax + my * seg.Ay;
                    if (MathF.Abs(a) < Eps2)
                    {
                        if (MathF.Abs(b) >= Eps2)
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
                            float root = MathF.Sqrt(discriminant);
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
                    num = SolveCubic(b, c, d, out r0, out r1, out r2);
                }

                float dist2 = mx * mx + my * my;
                if (dist2 < minDist2)
                {
                    minDist2 = dist2;
                    minDist = MathF.Sqrt(dist2);
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
                        minDist = MathF.Sqrt(dist2);
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
                        minDist = MathF.Sqrt(dist2);
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
                        minDist = MathF.Sqrt(dist2);
                    }
                }
            }

            if (winding == 0)
            {
                minDist = -minDist;
            }

            float val = onEdgeValue + pixelDistScale * minDist;
            if (val < 0.0f)
            {
                val = 0.0f;
            }
            else if (val > 255.0f)
            {
                val = 255.0f;
            }

            rowDst[col] = (byte)val;
        }
    }
}
