using Stb.Managed.Gpu.Internal;
using Stb.Managed.Gpu.Kernels;
using Stb.Managed.RectPack;
using Stb.Managed.TrueType;

namespace Stb.Managed.Gpu;

/// <summary>
/// Single rendered Signed Distance Field (SDF) glyph bitmap and placement metrics.
/// </summary>
/// <param name="Codepoint">Unicode codepoint.</param>
/// <param name="GlyphIndex">Font glyph index.</param>
/// <param name="Metrics">Rendered SDF bitmap width, height, and origin offsets.</param>
/// <param name="Pixels">8-bit SDF bitmap pixels (empty array for whitespace/empty glyphs).</param>
public readonly record struct GpuSdfGlyphResult(
    int Codepoint,
    int GlyphIndex,
    GlyphBitmapMetrics Metrics,
    byte[] Pixels);

/// <summary>
/// Atlas entry metadata for a glyph placed inside a <see cref="GpuSdfAtlas"/>.
/// </summary>
/// <param name="Codepoint">Unicode codepoint.</param>
/// <param name="GlyphIndex">Font glyph index.</param>
/// <param name="AtlasX">Left pixel coordinate inside the atlas (or -1 if not packed / empty).</param>
/// <param name="AtlasY">Top pixel coordinate inside the atlas (or -1 if not packed / empty).</param>
/// <param name="Width">SDF bitmap width in pixels.</param>
/// <param name="Height">SDF bitmap height in pixels.</param>
/// <param name="XOffset">Horizontal pixel offset from glyph origin to SDF bitmap top-left.</param>
/// <param name="YOffset">Vertical pixel offset from glyph origin to SDF bitmap top-left.</param>
/// <param name="AdvanceWidth">Scaled horizontal advance in pixels.</param>
/// <param name="LeftSideBearing">Scaled left side bearing in pixels.</param>
/// <param name="WasPacked"><see langword="true"/> if the glyph was placed in the atlas (or is empty whitespace).</param>
public readonly record struct GpuSdfAtlasGlyph(
    int Codepoint,
    int GlyphIndex,
    int AtlasX,
    int AtlasY,
    int Width,
    int Height,
    int XOffset,
    int YOffset,
    float AdvanceWidth,
    float LeftSideBearing,
    bool WasPacked);

/// <summary>
/// Packed 8-bit Signed Distance Field (SDF) font atlas generated on GPU and packed via <see cref="StbRectPack"/>.
/// </summary>
/// <param name="Width">Atlas texture width in pixels.</param>
/// <param name="Height">Atlas texture height in pixels.</param>
/// <param name="Pixels">Single-channel 8-bit SDF atlas pixels (<c>Width * Height</c> bytes).</param>
/// <param name="Glyphs">Per-glyph placement and typographic metrics.</param>
/// <param name="AllPacked"><see langword="true"/> if every non-empty glyph fit inside the atlas.</param>
public sealed record GpuSdfAtlas(
    int Width,
    int Height,
    byte[] Pixels,
    GpuSdfAtlasGlyph[] Glyphs,
    bool AllPacked);

/// <summary>
/// GPU-accelerated TrueType / OpenType Signed Distance Field (SDF) glyph batch renderer and
/// SDF atlas generator backed by ILGPU, with automatic CPU fallback via <see cref="StbTrueTypeFont"/>.
/// </summary>
public static class StbGpuTrueType
{
    private const float Eps = 1.0f / 1024.0f;
    private const float Eps2 = Eps * Eps;

    /// <summary>
    /// Generates an 8-bit Signed Distance Field (SDF) bitmap for a single codepoint,
    /// dispatching to GPU when profitable or falling back to <see cref="StbTrueTypeFont.GetCodepointSdf"/>.
    /// </summary>
    public static byte[] GetCodepointSdf(
        StbGpuContext context,
        StbTrueTypeFont font,
        float scale,
        int codepoint,
        int padding,
        byte onEdgeValue,
        float pixelDistScale,
        out GlyphBitmapMetrics metrics,
        StbGpuExecutionMode? modeOverride = null)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(font);

        ReadOnlySpan<int> single = [codepoint];
        GpuSdfGlyphResult[] batch = RenderCodepointSdfBatch(context, font, scale, single, padding, onEdgeValue, pixelDistScale, modeOverride);
        metrics = batch[0].Metrics;
        return batch[0].Pixels;
    }

    /// <summary>
    /// Renders Signed Distance Field (SDF) bitmaps for an entire batch of Unicode codepoints in a single
    /// massively parallel GPU kernel launch (one GPU thread per output pixel across all glyphs).
    /// </summary>
    public static GpuSdfGlyphResult[] RenderCodepointSdfBatch(
        StbGpuContext context,
        StbTrueTypeFont font,
        float scale,
        ReadOnlySpan<int> codepoints,
        int padding,
        byte onEdgeValue,
        float pixelDistScale,
        StbGpuExecutionMode? modeOverride = null)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(font);

        int count = codepoints.Length;
        if (count == 0)
        {
            return [];
        }

        if (scale == 0.0f)
        {
            GpuSdfGlyphResult[] empty = new GpuSdfGlyphResult[count];
            for (int i = 0; i < count; ++i)
            {
                empty[i] = new GpuSdfGlyphResult(codepoints[i], font.FindGlyphIndex(codepoints[i]), default, []);
            }

            return empty;
        }

        ArgumentOutOfRangeException.ThrowIfNegative(padding);

        var results = new GpuSdfGlyphResult[count];
        var gpuDescs = new List<GpuSdfGlyphDesc>(count);
        var gpuResultIndices = new List<int>(count);
        var lines = new List<GpuSdfLine>(count * 16);
        var curves = new List<GpuSdfCurve>(count * 16);
        var crossEdges = new List<GpuSdfCrossEdge>(count * 24);

        float scaleX = scale;
        float scaleY = -scale;
        int totalPixels = 0;
        long totalWorkUnits = 0;

        for (int i = 0; i < count; ++i)
        {
            int cp = codepoints[i];
            int glyphIdx = font.FindGlyphIndex(cp);
            GlyphBitmapBox box = font.GetGlyphBitmapBoxSubpixel(glyphIdx, scale, scale, 0.0f, 0.0f);
            if (box.X0 == box.X1 || box.Y0 == box.Y1)
            {
                results[i] = new GpuSdfGlyphResult(cp, glyphIdx, default, []);
                continue;
            }

            int ix0 = box.X0 - padding;
            int iy0 = box.Y0 - padding;
            int ix1 = box.X1 + padding;
            int iy1 = box.Y1 + padding;
            int w = ix1 - ix0;
            int h = iy1 - iy0;
            if (w <= 0 || h <= 0)
            {
                results[i] = new GpuSdfGlyphResult(cp, glyphIdx, default, []);
                continue;
            }

            GlyphVertex[] verts = font.GetGlyphShape(glyphIdx);
            if (verts.Length == 0)
            {
                results[i] = new GpuSdfGlyphResult(cp, glyphIdx, default, []);
                continue;
            }

            int lineOff = lines.Count;
            int curveOff = curves.Count;
            int crossOff = crossEdges.Count;

            for (int v = 0; v < verts.Length; ++v)
            {
                GlyphVertex vi = verts[v];
                if (vi.Type == GlyphVertexType.Line && v > 0)
                {
                    GlyphVertex prev = verts[v - 1];
                    float x0 = vi.X * scaleX;
                    float y0 = vi.Y * scaleY;
                    float x1 = prev.X * scaleX;
                    float y1 = prev.Y * scaleY;
                    float dist = MathF.Sqrt((x1 - x0) * (x1 - x0) + (y1 - y0) * (y1 - y0));
                    if (dist >= Eps)
                    {
                        lines.Add(new GpuSdfLine(x0, y0, x1, y1, 1.0f / dist));
                    }

                    if (prev.Y != vi.Y)
                    {
                        crossEdges.Add(new GpuSdfCrossEdge(prev.X, prev.Y, vi.X, vi.Y));
                    }
                }
                else if (vi.Type == GlyphVertexType.Curve && v > 0)
                {
                    GlyphVertex prev = verts[v - 1];
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
                    curves.Add(new GpuSdfCurve(x0, y0, x1, y1, x2, y2, aInv));

                    if ((prev.X == vi.Cx && prev.Y == vi.Cy) || (vi.Cx == vi.X && vi.Cy == vi.Y))
                    {
                        if (prev.Y != vi.Y)
                        {
                            crossEdges.Add(new GpuSdfCrossEdge(prev.X, prev.Y, vi.X, vi.Y));
                        }
                    }
                    else
                    {
                        crossEdges.Add(new GpuSdfCrossEdge(prev.X, prev.Y, vi.Cx, vi.Cy, vi.X, vi.Y));
                    }
                }
            }

            int lineCount = lines.Count - lineOff;
            int curveCount = curves.Count - curveOff;
            int crossCount = crossEdges.Count - crossOff;
            int pixCount = w * h;

            gpuDescs.Add(new GpuSdfGlyphDesc(
                totalPixels,
                totalPixels + pixCount,
                w,
                h,
                ix0,
                iy0,
                lineOff,
                lineCount,
                curveOff,
                curveCount,
                crossOff,
                crossCount,
                scaleX,
                scaleY,
                onEdgeValue,
                pixelDistScale));
            gpuResultIndices.Add(i);

            results[i] = new GpuSdfGlyphResult(cp, glyphIdx, new GlyphBitmapMetrics(w, h, ix0, iy0), []);
            totalPixels += pixCount;
            totalWorkUnits += (long)pixCount * Math.Max(1, lineCount + curveCount);
        }

        if (totalPixels == 0 || gpuDescs.Count == 0)
        {
            return results;
        }

        if (!context.ShouldUseGpu(totalWorkUnits, context.Thresholds.SdfMinPixelSegments, modeOverride))
        {
            context.RecordCpuFallback();
            for (int k = 0; k < gpuResultIndices.Count; ++k)
            {
                int idx = gpuResultIndices[k];
                int cp = codepoints[idx];
                byte[] bmp = font.GetCodepointSdf(scale, cp, padding, onEdgeValue, pixelDistScale, out GlyphBitmapMetrics m, context.CpuOptions);
                results[idx] = new GpuSdfGlyphResult(cp, results[idx].GlyphIndex, m, bmp);
            }

            return results;
        }

        // Ensure non-empty dummy buffers for ILGPU views when a glyph batch has 0 lines or 0 curves.
        GpuSdfGlyphDesc[] descArr = gpuDescs.ToArray();
        GpuSdfLine[] lineArr = lines.Count > 0 ? lines.ToArray() : [default];
        GpuSdfCurve[] curveArr = curves.Count > 0 ? curves.ToArray() : [default];
        GpuSdfCrossEdge[] crossArr = crossEdges.Count > 0 ? crossEdges.ToArray() : [default];

        using PooledGpuBuffer<byte> dstBuf = context.Pool.RentByte(totalPixels);
        using PooledGpuBuffer<GpuSdfGlyphDesc> descBuf = context.Pool.RentUnpooled<GpuSdfGlyphDesc>(descArr.Length);
        using PooledGpuBuffer<GpuSdfLine> lineBuf = context.Pool.RentUnpooled<GpuSdfLine>(lineArr.Length);
        using PooledGpuBuffer<GpuSdfCurve> curveBuf = context.Pool.RentUnpooled<GpuSdfCurve>(curveArr.Length);
        using PooledGpuBuffer<GpuSdfCrossEdge> crossBuf = context.Pool.RentUnpooled<GpuSdfCrossEdge>(crossArr.Length);

        lock (context.SyncRoot)
        {
            GpuBufferPool.CopyFromHostArray(descBuf.View, descArr, 0, descArr.Length);
            GpuBufferPool.CopyFromHostArray(lineBuf.View, lineArr, 0, lineArr.Length);
            GpuBufferPool.CopyFromHostArray(curveBuf.View, curveArr, 0, curveArr.Length);
            GpuBufferPool.CopyFromHostArray(crossBuf.View, crossArr, 0, crossArr.Length);

            var kernel = context.GetSdfBatchKernelLocked();
            kernel(totalPixels, dstBuf.View, descBuf.View, lineBuf.View, curveBuf.View, crossBuf.View, descArr.Length);
            context.Accelerator.Synchronize();
        }

        context.RecordGpuDispatch();
        byte[] flatPixels = context.DownloadArray(dstBuf.View, totalPixels);

        for (int k = 0; k < gpuResultIndices.Count; ++k)
        {
            int idx = gpuResultIndices[k];
            GpuSdfGlyphDesc d = descArr[k];
            int len = d.Width * d.Height;
            byte[] glyphBytes = GC.AllocateUninitializedArray<byte>(len);
            Buffer.BlockCopy(flatPixels, d.PixelOffset, glyphBytes, 0, len);
            results[idx] = results[idx] with { Pixels = glyphBytes };
        }

        return results;
    }

    /// <summary>
    /// Renders an entire set of SDF glyphs on GPU in a single launch and packs them into a compact
    /// 8-bit SDF font atlas using <see cref="StbRectPack"/>.
    /// </summary>
    public static GpuSdfAtlas GenerateSdfAtlas(
        StbGpuContext context,
        StbTrueTypeFont font,
        float pixelHeight,
        ReadOnlySpan<int> codepoints,
        int atlasWidth,
        int atlasHeight,
        int padding = 5,
        byte onEdgeValue = 180,
        float pixelDistScale = 36.0f,
        int glyphGap = 1,
        StbGpuExecutionMode? modeOverride = null)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(font);
        if (atlasWidth <= 0 || atlasHeight <= 0)
        {
            throw new StbException(StbErrorCode.InvalidArgument, "Atlas dimensions must be positive.");
        }

        float scale = font.ScaleForPixelHeight(pixelHeight);
        GpuSdfGlyphResult[] rendered = RenderCodepointSdfBatch(context, font, scale, codepoints, padding, onEdgeValue, pixelDistScale, modeOverride);

        int count = rendered.Length;
        PackRect[] rects = new PackRect[count];
        for (int i = 0; i < count; ++i)
        {
            GlyphBitmapMetrics m = rendered[i].Metrics;
            int rw = m.Width > 0 ? m.Width + glyphGap : 0;
            int rh = m.Height > 0 ? m.Height + glyphGap : 0;
            rects[i] = new PackRect(rw, rh, id: i);
        }

        bool allPacked = StbRectPack.PackRects(atlasWidth, atlasHeight, rects, RectPackHeuristic.SkylineBestFit);
        byte[] atlasPixels = new byte[atlasWidth * atlasHeight];
        GpuSdfAtlasGlyph[] atlasGlyphs = new GpuSdfAtlasGlyph[count];

        for (int i = 0; i < count; ++i)
        {
            GpuSdfGlyphResult r = rendered[i];
            GlyphHMetrics hm = font.GetGlyphHMetrics(r.GlyphIndex);
            PackRect pr = rects[i];

            if (r.Pixels.Length > 0 && pr.WasPacked)
            {
                for (int row = 0; row < r.Metrics.Height; ++row)
                {
                    Buffer.BlockCopy(
                        r.Pixels,
                        row * r.Metrics.Width,
                        atlasPixels,
                        (pr.Y + row) * atlasWidth + pr.X,
                        r.Metrics.Width);
                }
            }

            atlasGlyphs[i] = new GpuSdfAtlasGlyph(
                r.Codepoint,
                r.GlyphIndex,
                r.Pixels.Length > 0 && pr.WasPacked ? pr.X : -1,
                r.Pixels.Length > 0 && pr.WasPacked ? pr.Y : -1,
                r.Metrics.Width,
                r.Metrics.Height,
                r.Metrics.XOffset,
                r.Metrics.YOffset,
                hm.AdvanceWidth * scale,
                hm.LeftSideBearing * scale,
                r.Pixels.Length == 0 || pr.WasPacked);
        }

        return new GpuSdfAtlas(atlasWidth, atlasHeight, atlasPixels, atlasGlyphs, allPacked);
    }
}
