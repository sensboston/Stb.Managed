using System.Buffers.Binary;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
#if NET8_0_OR_GREATER
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.Arm;
using System.Runtime.Intrinsics.X86;
#endif
using Stb.Managed.Internal;

namespace Stb.Managed.TrueType.Internal;

internal struct PointF
{
    public float X;
    public float Y;

    public PointF(float x, float y)
    {
        X = x;
        Y = y;
    }
}

internal struct RasterEdge
{
    public float X0;
    public float Y0;
    public float X1;
    public float Y1;
    public int Invert;
}

internal struct ActiveEdge
{
    public float Fx;
    public float Fdx;
    public float Fdy;
    public float Direction;
    public float Sy;
    public float Ey;
}

internal sealed class RasterizerScratch
{
    [ThreadStatic]
    private static RasterizerScratch? t_instance;

    internal static RasterizerScratch Get()
    {
        RasterizerScratch? scratch = t_instance;
        if (scratch is null)
        {
            scratch = new RasterizerScratch();
            t_instance = scratch;
        }

        return scratch;
    }

    internal GlyphVertex[] Vertices = new GlyphVertex[256];
    internal int VertexCount;

    internal GlyphVertex[] TempVertices = new GlyphVertex[256];

    internal PointF[] Points = new PointF[512];
    internal int PointCount;

    internal int[] ContourLengths = new int[32];
    internal int ContourCount;

    internal RasterEdge[] Edges = new RasterEdge[512];
    internal ActiveEdge[] ActiveEdges = new ActiveEdge[128];

    internal float[] Scanline = new float[258];

    internal int[] VPrefilterTotals = new int[256];
    internal byte[] VPrefilterRing = new byte[256 * 8];

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal void ResetVertices() => VertexCount = 0;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal void AppendVertex(GlyphVertex v)
    {
        if (VertexCount >= Vertices.Length)
        {
            GrowVertices(VertexCount + 1);
        }

        Vertices[VertexCount++] = v;
    }

    internal void EnsureVertices(int capacity)
    {
        if (capacity > Vertices.Length)
        {
            GrowVertices(capacity);
        }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private void GrowVertices(int needed)
    {
        int newCap = Math.Max(Vertices.Length * 2, needed);
        Array.Resize(ref Vertices, newCap);
    }

    internal void EnsureTempVertices(int capacity)
    {
        if (capacity > TempVertices.Length)
        {
            TempVertices = new GlyphVertex[Math.Max(TempVertices.Length * 2, capacity)];
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal void AppendPoint(float x, float y)
    {
        if (PointCount >= Points.Length)
        {
            GrowPoints(PointCount + 1);
        }

        Points[PointCount++] = new PointF(x, y);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private void GrowPoints(int needed)
    {
        int newCap = Math.Max(Points.Length * 2, needed);
        Array.Resize(ref Points, newCap);
    }

    internal void EnsureContours(int count)
    {
        if (count > ContourLengths.Length)
        {
            ContourLengths = new int[Math.Max(ContourLengths.Length * 2, count)];
        }
    }

    internal void EnsureEdges(int count)
    {
        if (count > Edges.Length)
        {
            Edges = new RasterEdge[Math.Max(Edges.Length * 2, count)];
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal void EnsureActiveEdges(int count)
    {
        if (count > ActiveEdges.Length)
        {
            Array.Resize(ref ActiveEdges, Math.Max(ActiveEdges.Length * 2, count));
        }
    }

    internal void EnsureScanline(int width)
    {
        int needed = width * 2 + 1;
        if (needed > Scanline.Length)
        {
            Scanline = new float[Math.Max(Scanline.Length * 2, needed)];
        }
    }

    internal void EnsureVPrefilter(int width)
    {
        if (width > VPrefilterTotals.Length)
        {
            int newCap = Math.Max(VPrefilterTotals.Length * 2, width);
            VPrefilterTotals = new int[newCap];
            VPrefilterRing = new byte[newCap * 8];
        }
    }
}

internal static class TrueTypeRasterizer
{
    internal const int MaxOversample = 8;
    private const int OverMask = MaxOversample - 1;

    internal static void Rasterize(
        Span<byte> pixels,
        int width,
        int height,
        int stride,
        float flatnessInPixels,
        ReadOnlySpan<GlyphVertex> vertices,
        float scaleX,
        float scaleY,
        float shiftX,
        float shiftY,
        int xOff,
        int yOff,
        bool invert,
        RasterizerScratch scratch)
    {
        if (width <= 0 || height <= 0 || vertices.IsEmpty)
        {
            return;
        }

        float scale = scaleX > scaleY ? scaleY : scaleX;
        if (scale == 0.0f)
        {
            return;
        }

        if (!FlattenCurvesSinglePass(vertices, flatnessInPixels / scale, scratch))
        {
            return;
        }

        RasterizeFlattened(pixels, width, height, stride, scaleX, scaleY, shiftX, shiftY, xOff, yOff, invert, scratch);
    }

    private static void TesselateCurve(
        RasterizerScratch scratch,
        float x0,
        float y0,
        float x1,
        float y1,
        float x2,
        float y2,
        float objspaceFlatnessSquared,
        int depth)
    {
        float mx = (x0 + 2.0f * x1 + x2) * 0.25f;
        float my = (y0 + 2.0f * y1 + y2) * 0.25f;
        float dx = (x0 + x2) * 0.5f - mx;
        float dy = (y0 + y2) * 0.5f - my;

        if (depth > 16)
        {
            return;
        }

        if (dx * dx + dy * dy > objspaceFlatnessSquared)
        {
            TesselateCurve(scratch, x0, y0, (x0 + x1) * 0.5f, (y0 + y1) * 0.5f, mx, my, objspaceFlatnessSquared, depth + 1);
            TesselateCurve(scratch, mx, my, (x1 + x2) * 0.5f, (y1 + y2) * 0.5f, x2, y2, objspaceFlatnessSquared, depth + 1);
        }
        else
        {
            scratch.AppendPoint(x2, y2);
        }
    }

    private static void TesselateCubic(
        RasterizerScratch scratch,
        float x0,
        float y0,
        float x1,
        float y1,
        float x2,
        float y2,
        float x3,
        float y3,
        float objspaceFlatnessSquared,
        int depth)
    {
        float dx0 = x1 - x0;
        float dy0 = y1 - y0;
        float dx1 = x2 - x1;
        float dy1 = y2 - y1;
        float dx2 = x3 - x2;
        float dy2 = y3 - y2;
        float dx = x3 - x0;
        float dy = y3 - y0;

        float longlen = MathF.Sqrt(dx0 * dx0 + dy0 * dy0) +
                        MathF.Sqrt(dx1 * dx1 + dy1 * dy1) +
                        MathF.Sqrt(dx2 * dx2 + dy2 * dy2);
        float shortlen = MathF.Sqrt(dx * dx + dy * dy);
        float flatnessSquared = longlen * longlen - shortlen * shortlen;

        if (depth > 16)
        {
            return;
        }

        if (flatnessSquared > objspaceFlatnessSquared)
        {
            float x01 = (x0 + x1) * 0.5f;
            float y01 = (y0 + y1) * 0.5f;
            float x12 = (x1 + x2) * 0.5f;
            float y12 = (y1 + y2) * 0.5f;
            float x23 = (x2 + x3) * 0.5f;
            float y23 = (y2 + y3) * 0.5f;

            float xa = (x01 + x12) * 0.5f;
            float ya = (y01 + y12) * 0.5f;
            float xb = (x12 + x23) * 0.5f;
            float yb = (y12 + y23) * 0.5f;

            float mx = (xa + xb) * 0.5f;
            float my = (ya + yb) * 0.5f;

            TesselateCubic(scratch, x0, y0, x01, y01, xa, ya, mx, my, objspaceFlatnessSquared, depth + 1);
            TesselateCubic(scratch, mx, my, xb, yb, x23, y23, x3, y3, objspaceFlatnessSquared, depth + 1);
        }
        else
        {
            scratch.AppendPoint(x3, y3);
        }
    }

    private static bool FlattenCurvesSinglePass(
        ReadOnlySpan<GlyphVertex> vertices,
        float objspaceFlatness,
        RasterizerScratch scratch)
    {
        float objspaceFlatnessSquared = objspaceFlatness * objspaceFlatness;
        int contourCount = 0;
        for (int i = 0; i < vertices.Length; ++i)
        {
            if (vertices[i].Type == GlyphVertexType.Move)
            {
                contourCount++;
            }
        }

        scratch.ContourCount = contourCount;
        if (contourCount == 0)
        {
            return false;
        }

        scratch.EnsureContours(contourCount);
        scratch.PointCount = 0;

        int n = -1;
        int start = 0;
        float x = 0.0f;
        float y = 0.0f;

        for (int i = 0; i < vertices.Length; ++i)
        {
            GlyphVertex v = vertices[i];
            switch (v.Type)
            {
                case GlyphVertexType.Move:
                    if (n >= 0)
                    {
                        scratch.ContourLengths[n] = scratch.PointCount - start;
                    }

                    ++n;
                    start = scratch.PointCount;
                    x = v.X;
                    y = v.Y;
                    scratch.AppendPoint(x, y);
                    break;

                case GlyphVertexType.Line:
                    x = v.X;
                    y = v.Y;
                    scratch.AppendPoint(x, y);
                    break;

                case GlyphVertexType.Curve:
                    TesselateCurve(scratch, x, y, v.Cx, v.Cy, v.X, v.Y, objspaceFlatnessSquared, 0);
                    x = v.X;
                    y = v.Y;
                    break;

                case GlyphVertexType.Cubic:
                    TesselateCubic(scratch, x, y, v.Cx, v.Cy, v.Cx1, v.Cy1, v.X, v.Y, objspaceFlatnessSquared, 0);
                    x = v.X;
                    y = v.Y;
                    break;
            }
        }

        if (n >= 0)
        {
            scratch.ContourLengths[n] = scratch.PointCount - start;
        }

        return scratch.PointCount > 0;
    }

    private static void RasterizeFlattened(
        Span<byte> pixels,
        int width,
        int height,
        int stride,
        float scaleX,
        float scaleY,
        float shiftX,
        float shiftY,
        int offX,
        int offY,
        bool invert,
        RasterizerScratch scratch)
    {
        float yScaleInv = invert ? -scaleY : scaleY;
        int windings = scratch.ContourCount;
        int[] wcount = scratch.ContourLengths;
        PointF[] pts = scratch.Points;

        int maxEdges = 0;
        for (int i = 0; i < windings; ++i)
        {
            maxEdges += wcount[i];
        }

        scratch.EnsureEdges(maxEdges + 1);
        RasterEdge[] edges = scratch.Edges;

        int edgeCount = 0;
        int m = 0;
        for (int i = 0; i < windings; ++i)
        {
            int count = wcount[i];
            int baseIdx = m;
            m += count;
            int j = count - 1;
            for (int k = 0; k < count; j = k++)
            {
                PointF pj = pts[baseIdx + j];
                PointF pk = pts[baseIdx + k];
                if (pj.Y == pk.Y)
                {
                    continue;
                }

                int a = k;
                int b = j;
                int inv = 0;
                if (invert ? pj.Y > pk.Y : pj.Y < pk.Y)
                {
                    inv = 1;
                    a = j;
                    b = k;
                }

                PointF pa = pts[baseIdx + a];
                PointF pb = pts[baseIdx + b];
                edges[edgeCount++] = new RasterEdge
                {
                    X0 = pa.X * scaleX + shiftX,
                    Y0 = pa.Y * yScaleInv + shiftY,
                    X1 = pb.X * scaleX + shiftX,
                    Y1 = pb.Y * yScaleInv + shiftY,
                    Invert = inv,
                };
            }
        }

        if (edgeCount > 1)
        {
            SortEdges(edges.AsSpan(0, edgeCount));
        }

        RasterizeSortedEdges(pixels, width, height, stride, edges, edgeCount, offX, offY, scratch);
    }

    private static void SortEdges(Span<RasterEdge> edges)
    {
        int n = edges.Length;
        int maxDepth = 0;
        for (int t = n; t > 0; t >>= 1)
        {
            maxDepth++;
        }

        IntroSortEdges(edges, maxDepth * 2);
        InsertionSortEdges(edges);
    }

    private static void IntroSortEdges(Span<RasterEdge> p, int depthLimit)
    {
        while (p.Length > 12)
        {
            if (depthLimit == 0)
            {
                HeapSortEdges(p);
                return;
            }

            depthLimit--;
            int n = p.Length;
            int m = n >> 1;
            bool c01 = p[0].Y0 < p[m].Y0;
            bool c12 = p[m].Y0 < p[n - 1].Y0;
            if (c01 != c12)
            {
                bool c = p[0].Y0 < p[n - 1].Y0;
                int z = (c == c12) ? 0 : n - 1;
                (p[z], p[m]) = (p[m], p[z]);
            }

            (p[0], p[m]) = (p[m], p[0]);

            float pivot = p[0].Y0;
            int i = 1;
            int j = n - 1;
            for (; ; )
            {
                while (i < n && p[i].Y0 < pivot) ++i;
                while (j > 0 && pivot < p[j].Y0) --j;
                if (i >= j) break;
                (p[i], p[j]) = (p[j], p[i]);
                ++i;
                --j;
            }

            if (j < (n - i))
            {
                if (j > 1)
                {
                    IntroSortEdges(p.Slice(0, j), depthLimit);
                }

                p = p.Slice(i);
            }
            else
            {
                if (n - i > 1)
                {
                    IntroSortEdges(p.Slice(i), depthLimit);
                }

                p = p.Slice(0, j);
            }
        }
    }

    private static void HeapSortEdges(Span<RasterEdge> p)
    {
        int n = p.Length;
        for (int i = n / 2 - 1; i >= 0; --i)
        {
            SiftDown(p, i, n);
        }

        for (int i = n - 1; i > 0; --i)
        {
            (p[0], p[i]) = (p[i], p[0]);
            SiftDown(p, 0, i);
        }
    }

    private static void SiftDown(Span<RasterEdge> p, int root, int count)
    {
        while (true)
        {
            int child = root * 2 + 1;
            if (child >= count) break;
            if (child + 1 < count && p[child].Y0 < p[child + 1].Y0)
            {
                child++;
            }

            if (p[root].Y0 >= p[child].Y0) break;
            (p[root], p[child]) = (p[child], p[root]);
            root = child;
        }
    }

    private static void InsertionSortEdges(Span<RasterEdge> p)
    {
        for (int i = 1; i < p.Length; ++i)
        {
            RasterEdge t = p[i];
            int j = i;
            while (j > 0 && t.Y0 < p[j - 1].Y0)
            {
                p[j] = p[j - 1];
                --j;
            }

            if (i != j)
            {
                p[j] = t;
            }
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void HandleClippedEdge(
        Span<float> scanline,
        int x,
        in ActiveEdge e,
        float x0,
        float y0,
        float x1,
        float y1)
    {
        if (y0 == y1) return;
        if (y0 > e.Ey) return;
        if (y1 < e.Sy) return;

        if (y0 < e.Sy)
        {
            x0 += (x1 - x0) * (e.Sy - y0) / (y1 - y0);
            y0 = e.Sy;
        }

        if (y1 > e.Ey)
        {
            x1 += (x1 - x0) * (e.Ey - y1) / (y1 - y0);
            y1 = e.Ey;
        }

        if (x0 <= x && x1 <= x)
        {
            scanline[x] += e.Direction * (y1 - y0);
        }
        else if (x0 >= x + 1 && x1 >= x + 1)
        {
            // Strictly right of pixel x: contributes zero coverage to pixel x.
        }
        else
        {
            scanline[x] += e.Direction * (y1 - y0) * (1.0f - ((x0 - x) + (x1 - x)) * 0.5f);
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static float PositionTrapezoidArea(float height, float tx0, float tx1, float bx0, float bx1) =>
        ((tx1 - tx0) + (bx1 - bx0)) * 0.5f * height;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static float SizedTriangleArea(float height, float width) =>
        height * width * 0.5f;

    private static void FillActiveEdgesNew(
        Span<float> scanline,
        Span<float> scanline2,
        int len,
        ReadOnlySpan<ActiveEdge> active,
        float yTop)
    {
        float yBottom = yTop + 1.0f;
        int activeLen = active.Length;

        for (int idx = 0; idx < activeLen; ++idx)
        {
            ref readonly ActiveEdge e = ref active[idx];

            if (e.Fdx == 0.0f)
            {
                float x0 = e.Fx;
                if (x0 < len)
                {
                    if (x0 >= 0.0f)
                    {
                        int ix0 = (int)x0;
                        HandleClippedEdge(scanline, ix0, in e, x0, yTop, x0, yBottom);
                        HandleClippedEdge(scanline2, ix0 + 1, in e, x0, yTop, x0, yBottom);
                    }
                    else
                    {
                        HandleClippedEdge(scanline2, 0, in e, x0, yTop, x0, yBottom);
                    }
                }
            }
            else
            {
                float x0 = e.Fx;
                float dx = e.Fdx;
                float xb = x0 + dx;
                float dy = e.Fdy;
                float xTop;
                float xBottom;
                float sy0;
                float sy1;

                if (e.Sy > yTop)
                {
                    xTop = x0 + dx * (e.Sy - yTop);
                    sy0 = e.Sy;
                }
                else
                {
                    xTop = x0;
                    sy0 = yTop;
                }

                if (e.Ey < yBottom)
                {
                    xBottom = x0 + dx * (e.Ey - yTop);
                    sy1 = e.Ey;
                }
                else
                {
                    xBottom = xb;
                    sy1 = yBottom;
                }

                if (xTop >= 0.0f && xBottom >= 0.0f && xTop < len && xBottom < len)
                {
                    if ((int)xTop == (int)xBottom)
                    {
                        int x = (int)xTop;
                        float h = (sy1 - sy0) * e.Direction;
                        scanline[x] += PositionTrapezoidArea(h, xTop, x + 1.0f, xBottom, x + 1.0f);
                        scanline2[x + 1] += h;
                    }
                    else
                    {
                        if (xTop > xBottom)
                        {
                            sy0 = yBottom - (sy0 - yTop);
                            sy1 = yBottom - (sy1 - yTop);
                            (sy0, sy1) = (sy1, sy0);
                            (xBottom, xTop) = (xTop, xBottom);
                            dx = -dx;
                            dy = -dy;
                            (x0, xb) = (xb, x0);
                        }

                        int x1 = (int)xTop;
                        int x2 = (int)xBottom;
                        float yCrossing = yTop + dy * (x1 + 1 - x0);
                        float yFinal = yTop + dy * (x2 - x0);

                        if (yCrossing > yBottom)
                        {
                            yCrossing = yBottom;
                        }

                        float sign = e.Direction;
                        float area = sign * (yCrossing - sy0);

                        scanline[x1] += SizedTriangleArea(area, x1 + 1 - xTop);

                        if (yFinal > yBottom)
                        {
                            int denom = x2 - (x1 + 1);
                            yFinal = yBottom;
                            if (denom != 0)
                            {
                                dy = (yFinal - yCrossing) / denom;
                            }
                        }

                        float step = sign * dy;
                        float halfStep = step * 0.5f;
                        for (int x = x1 + 1; x < x2; ++x)
                        {
                            scanline[x] += area + halfStep;
                            area += step;
                        }

                        scanline[x2] += area + sign * PositionTrapezoidArea(sy1 - yFinal, x2, x2 + 1.0f, xBottom, x2 + 1.0f);
                        scanline2[x2 + 1] += sign * (sy1 - sy0);
                    }
                }
                else
                {
                    float invDx = 1.0f / dx;
                    for (int x = 0; x < len; ++x)
                    {
                        float y0 = yTop;
                        float fx1 = x;
                        float fx2 = x + 1;
                        float fx3 = xb;
                        float fy3 = yBottom;

                        float fy1 = (x - x0) * invDx + yTop;
                        float fy2 = (x + 1 - x0) * invDx + yTop;

                        if (x0 < fx1 && fx3 > fx2)
                        {
                            HandleClippedEdge(scanline, x, in e, x0, y0, fx1, fy1);
                            HandleClippedEdge(scanline, x, in e, fx1, fy1, fx2, fy2);
                            HandleClippedEdge(scanline, x, in e, fx2, fy2, fx3, fy3);
                        }
                        else if (fx3 < fx1 && x0 > fx2)
                        {
                            HandleClippedEdge(scanline, x, in e, x0, y0, fx2, fy2);
                            HandleClippedEdge(scanline, x, in e, fx2, fy2, fx1, fy1);
                            HandleClippedEdge(scanline, x, in e, fx1, fy1, fx3, fy3);
                        }
                        else if (x0 < fx1 && fx3 > fx1)
                        {
                            HandleClippedEdge(scanline, x, in e, x0, y0, fx1, fy1);
                            HandleClippedEdge(scanline, x, in e, fx1, fy1, fx3, fy3);
                        }
                        else if (fx3 < fx1 && x0 > fx1)
                        {
                            HandleClippedEdge(scanline, x, in e, x0, y0, fx1, fy1);
                            HandleClippedEdge(scanline, x, in e, fx1, fy1, fx3, fy3);
                        }
                        else if (x0 < fx2 && fx3 > fx2)
                        {
                            HandleClippedEdge(scanline, x, in e, x0, y0, fx2, fy2);
                            HandleClippedEdge(scanline, x, in e, fx2, fy2, fx3, fy3);
                        }
                        else if (fx3 < fx2 && x0 > fx2)
                        {
                            HandleClippedEdge(scanline, x, in e, x0, y0, fx2, fy2);
                            HandleClippedEdge(scanline, x, in e, fx2, fy2, fx3, fy3);
                        }
                        else
                        {
                            HandleClippedEdge(scanline, x, in e, x0, y0, fx3, fy3);
                        }
                    }
                }
            }
        }
    }

    private static void RasterizeSortedEdges(
        Span<byte> pixels,
        int width,
        int height,
        int stride,
        RasterEdge[] edges,
        int edgeCount,
        int offX,
        int offY,
        RasterizerScratch scratch)
    {
        scratch.EnsureScanline(width);
        scratch.EnsureActiveEdges(edgeCount);
        Span<float> scanlineBoth = scratch.Scanline.AsSpan(0, width * 2 + 1);
        Span<float> scanline = scanlineBoth.Slice(0, width);
        Span<float> scanline2 = scanlineBoth.Slice(width, width + 1);
        ActiveEdge[] active = scratch.ActiveEdges;

        int activeCount = 0;
        int edgeIndex = 0;
        int y = offY;

        for (int j = 0; j < height; ++j)
        {
            float scanYTop = y;
            float scanYBottom = y + 1.0f;

            // Remove active edges that terminate before the top of this scanline.
            int writeIdx = 0;
            for (int k = 0; k < activeCount; ++k)
            {
                ref ActiveEdge ak = ref active[k];
                if (ak.Ey > scanYTop)
                {
                    if (writeIdx != k)
                    {
                        active[writeIdx] = ak;
                    }

                    writeIdx++;
                }
            }

            activeCount = writeIdx;

            // Insert all edges that start before the bottom of this scanline.
            while (edgeIndex < edgeCount && edges[edgeIndex].Y0 <= scanYBottom)
            {
                ref readonly RasterEdge re = ref edges[edgeIndex++];
                if (re.Y0 != re.Y1)
                {
                    float dxdy = (re.X1 - re.X0) / (re.Y1 - re.Y0);
                    float ey = re.Y1;
                    if (j == 0 && offY != 0 && ey < scanYTop)
                    {
                        ey = scanYTop;
                    }

                    active[activeCount++] = new ActiveEdge
                    {
                        Fdx = dxdy,
                        Fdy = dxdy != 0.0f ? (1.0f / dxdy) : 0.0f,
                        Fx = re.X0 + dxdy * (scanYTop - re.Y0) - offX,
                        Direction = re.Invert != 0 ? 1.0f : -1.0f,
                        Sy = re.Y0,
                        Ey = ey,
                    };
                }
            }

            Span<byte> rowDst = pixels.Slice(j * stride, width);
            if (activeCount > 0)
            {
                scanlineBoth.Clear();
                FillActiveEdgesNew(scanline, scanline2, width, active.AsSpan(0, activeCount), scanYTop);
                AccumulateAndQuantizeScanline(scanline, scanline2, rowDst);

                // Advance all active edges to next scanline.
                for (int k = 0; k < activeCount; ++k)
                {
                    ref ActiveEdge ak = ref active[k];
                    ak.Fx += ak.Fdx;
                }
            }
            else
            {
                rowDst.Clear();
            }

            ++y;
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static void AccumulateAndQuantizeScanline(
        ReadOnlySpan<float> scanline,
        ReadOnlySpan<float> scanline2,
        Span<byte> dst)
    {
        int w = dst.Length;
        int i = 0;
        float sum = 0.0f;

#if NET8_0_OR_GREATER
        if (Avx2.IsSupported && w >= 8)
        {
            Vector256<float> signMask = Vector256.Create(-0.0f);
            Vector256<float> v255 = Vector256.Create(255.0f);
            Vector256<float> vHalf = Vector256.Create(0.5f);

            int end8 = w - 8;
            for (; i <= end8; i += 8)
            {
                Vector256<float> v = Vector256.Create(scanline2.Slice(i));
                v = Avx.Add(v, Avx2.ShiftLeftLogical128BitLane(v.AsByte(), 4).AsSingle());
                v = Avx.Add(v, Avx2.ShiftLeftLogical128BitLane(v.AsByte(), 8).AsSingle());
                Vector128<float> loSum = Sse.Shuffle(v.GetLower(), v.GetLower(), 0xFF);
                v = Avx.Add(v, Vector256.Create(Vector128<float>.Zero, loSum));
                v = Avx.Add(v, Vector256.Create(sum));
                sum = v.GetUpper().GetElement(3);

                Vector256<float> k = Avx.Add(Vector256.Create(scanline.Slice(i)), v);
                k = Avx.AndNot(signMask, k);
                k = Avx.Add(Avx.Multiply(k, v255), vHalf);
                k = Avx.Min(k, v255);

                Vector256<int> vi = Avx.ConvertToVector256Int32WithTruncation(k);
                Vector128<short> s16 = Sse2.PackSignedSaturate(vi.GetLower(), vi.GetUpper());
                Vector128<byte> u8 = Sse2.PackUnsignedSaturate(s16, s16);
                BinaryPrimitives.WriteUInt64LittleEndian(dst.Slice(i), u8.AsUInt64().GetElement(0));
            }
        }
        else if (Sse2.IsSupported && w >= 4)
        {
            Vector128<float> signMask = Vector128.Create(-0.0f);
            Vector128<float> v255 = Vector128.Create(255.0f);
            Vector128<float> vHalf = Vector128.Create(0.5f);

            int end4 = w - 4;
            for (; i <= end4; i += 4)
            {
                Vector128<float> v = Vector128.Create(scanline2.Slice(i));
                v = Sse.Add(v, Sse2.ShiftLeftLogical128BitLane(v.AsByte(), 4).AsSingle());
                v = Sse.Add(v, Sse2.ShiftLeftLogical128BitLane(v.AsByte(), 8).AsSingle());
                v = Sse.Add(v, Vector128.Create(sum));
                sum = v.GetElement(3);

                Vector128<float> k = Sse.Add(Vector128.Create(scanline.Slice(i)), v);
                k = Sse.AndNot(signMask, k);
                k = Sse.Add(Sse.Multiply(k, v255), vHalf);
                k = Sse.Min(k, v255);

                Vector128<int> vi = Sse2.ConvertToVector128Int32WithTruncation(k);
                Vector128<short> s16 = Sse2.PackSignedSaturate(vi, vi);
                Vector128<byte> u8 = Sse2.PackUnsignedSaturate(s16, s16);
                BinaryPrimitives.WriteUInt32LittleEndian(dst.Slice(i), u8.AsUInt32().GetElement(0));
            }
        }
        else if (AdvSimd.IsSupported && w >= 4)
        {
            Vector128<float> zero = Vector128<float>.Zero;
            Vector128<float> v255 = Vector128.Create(255.0f);
            Vector128<float> vHalf = Vector128.Create(0.5f);

            int end4 = w - 4;
            for (; i <= end4; i += 4)
            {
                Vector128<float> v = Vector128.Create(scanline2.Slice(i));
                v = AdvSimd.Add(v, AdvSimd.ExtractVector128(zero, v, 3));
                v = AdvSimd.Add(v, AdvSimd.ExtractVector128(zero, v, 2));
                v = AdvSimd.Add(v, Vector128.Create(sum));
                sum = v.GetElement(3);

                Vector128<float> k = AdvSimd.Add(Vector128.Create(scanline.Slice(i)), v);
                k = AdvSimd.Abs(k);
                k = AdvSimd.Add(AdvSimd.Multiply(k, v255), vHalf);
                k = AdvSimd.MinNumber(k, v255);

                Vector128<uint> vu = AdvSimd.ConvertToUInt32RoundToZero(k);
                Vector64<ushort> u16 = AdvSimd.ExtractNarrowingSaturateLower(vu);
                Vector64<byte> u8 = AdvSimd.ExtractNarrowingSaturateLower(Vector128.Create(u16, u16));
                BinaryPrimitives.WriteUInt32LittleEndian(dst.Slice(i), u8.AsUInt32().GetElement(0));
            }
        }
#endif

        for (; i < w; ++i)
        {
            sum += scanline2[i];
            float k = MathF.Abs(scanline[i] + sum) * 255.0f + 0.5f;
            int m = (int)k;
            if (m > 255) m = 255;
            dst[i] = (byte)m;
        }
    }

    internal static void HPrefilter(Span<byte> pixels, int w, int h, int strideInBytes, int kernelWidth)
    {
        if (w <= 0 || h <= 0 || kernelWidth <= 1)
        {
            return;
        }

        Span<byte> buffer = stackalloc byte[MaxOversample];
        int safeW = w - kernelWidth;

        for (int j = 0; j < h; ++j)
        {
            Span<byte> row = pixels.Slice(j * strideInBytes, w);
            buffer.Slice(0, kernelWidth).Clear();
            uint total = 0;
            int i = 0;

            switch (kernelWidth)
            {
                case 2:
                    for (; i <= safeW; ++i)
                    {
                        byte px = row[i];
                        total = total + px - buffer[i & OverMask];
                        buffer[(i + 2) & OverMask] = px;
                        row[i] = (byte)(total >> 1);
                    }

                    break;

                case 3:
                    for (; i <= safeW; ++i)
                    {
                        byte px = row[i];
                        total = total + px - buffer[i & OverMask];
                        buffer[(i + 3) & OverMask] = px;
                        row[i] = (byte)(total / 3u);
                    }

                    break;

                case 4:
                    for (; i <= safeW; ++i)
                    {
                        byte px = row[i];
                        total = total + px - buffer[i & OverMask];
                        buffer[(i + 4) & OverMask] = px;
                        row[i] = (byte)(total >> 2);
                    }

                    break;

                case 5:
                    for (; i <= safeW; ++i)
                    {
                        byte px = row[i];
                        total = total + px - buffer[i & OverMask];
                        buffer[(i + 5) & OverMask] = px;
                        row[i] = (byte)(total / 5u);
                    }

                    break;

                default:
                    {
                        uint kw = (uint)kernelWidth;
                        for (; i <= safeW; ++i)
                        {
                            byte px = row[i];
                            total = total + px - buffer[i & OverMask];
                            buffer[(i + kernelWidth) & OverMask] = px;
                            row[i] = (byte)(total / kw);
                        }

                        break;
                    }
            }

            uint kwd = (uint)kernelWidth;
            for (; i < w; ++i)
            {
                total -= buffer[i & OverMask];
                row[i] = (byte)(total / kwd);
            }
        }
    }

    /// <summary>
    /// Row-major vertical box prefilter, bit-for-bit identical to upstream stbtt__v_prefilter
    /// while eliminating strided column cache misses.
    /// </summary>
    internal static void VPrefilter(Span<byte> pixels, int w, int h, int strideInBytes, int kernelWidth, RasterizerScratch scratch)
    {
        if (w <= 0 || h <= 0 || kernelWidth <= 1)
        {
            return;
        }

        scratch.EnsureVPrefilter(w);
        int[] totals = scratch.VPrefilterTotals;
        byte[] ring = scratch.VPrefilterRing;

        totals.AsSpan(0, w).Clear();
        ring.AsSpan(0, kernelWidth * w).Clear();

        int safeH = h - kernelWidth;
        int i = 0;

        switch (kernelWidth)
        {
            case 2:
                for (; i <= safeH; ++i)
                {
                    Span<byte> row = pixels.Slice(i * strideInBytes, w);
                    int rRead = (i & OverMask) * w;
                    int rWrite = ((i + 2) & OverMask) * w;
                    for (int j = 0; j < w; ++j)
                    {
                        byte px = row[j];
                        int total = totals[j] + px - ring[rRead + j];
                        totals[j] = total;
                        ring[rWrite + j] = px;
                        row[j] = (byte)((uint)total >> 1);
                    }
                }

                break;

            case 3:
                for (; i <= safeH; ++i)
                {
                    Span<byte> row = pixels.Slice(i * strideInBytes, w);
                    int rRead = (i & OverMask) * w;
                    int rWrite = ((i + 3) & OverMask) * w;
                    for (int j = 0; j < w; ++j)
                    {
                        byte px = row[j];
                        int total = totals[j] + px - ring[rRead + j];
                        totals[j] = total;
                        ring[rWrite + j] = px;
                        row[j] = (byte)((uint)total / 3u);
                    }
                }

                break;

            case 4:
                for (; i <= safeH; ++i)
                {
                    Span<byte> row = pixels.Slice(i * strideInBytes, w);
                    int rRead = (i & OverMask) * w;
                    int rWrite = ((i + 4) & OverMask) * w;
                    for (int j = 0; j < w; ++j)
                    {
                        byte px = row[j];
                        int total = totals[j] + px - ring[rRead + j];
                        totals[j] = total;
                        ring[rWrite + j] = px;
                        row[j] = (byte)((uint)total >> 2);
                    }
                }

                break;

            case 5:
                for (; i <= safeH; ++i)
                {
                    Span<byte> row = pixels.Slice(i * strideInBytes, w);
                    int rRead = (i & OverMask) * w;
                    int rWrite = ((i + 5) & OverMask) * w;
                    for (int j = 0; j < w; ++j)
                    {
                        byte px = row[j];
                        int total = totals[j] + px - ring[rRead + j];
                        totals[j] = total;
                        ring[rWrite + j] = px;
                        row[j] = (byte)((uint)total / 5u);
                    }
                }

                break;

            default:
                {
                    uint kw = (uint)kernelWidth;
                    for (; i <= safeH; ++i)
                    {
                        Span<byte> row = pixels.Slice(i * strideInBytes, w);
                        int rRead = (i & OverMask) * w;
                        int rWrite = ((i + kernelWidth) & OverMask) * w;
                        for (int j = 0; j < w; ++j)
                        {
                            byte px = row[j];
                            int total = totals[j] + px - ring[rRead + j];
                            totals[j] = total;
                            ring[rWrite + j] = px;
                            row[j] = (byte)((uint)total / kw);
                        }
                    }

                    break;
                }
        }

        uint kwd = (uint)kernelWidth;
        for (; i < h; ++i)
        {
            Span<byte> row = pixels.Slice(i * strideInBytes, w);
            int rRead = (i & OverMask) * w;
            for (int j = 0; j < w; ++j)
            {
                int total = totals[j] - ring[rRead + j];
                totals[j] = total;
                row[j] = (byte)((uint)total / kwd);
            }
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static float OversampleShift(int oversample) =>
        oversample == 0 ? 0.0f : -(oversample - 1) / (2.0f * oversample);
}
