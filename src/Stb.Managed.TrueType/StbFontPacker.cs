using System.Buffers;
using Stb.Managed.Internal;
using Stb.Managed.TrueType.Internal;

namespace Stb.Managed.TrueType;

/// <summary>
/// Packs multiple font ranges and oversampled glyphs into a single 8-bit grayscale texture atlas
/// using the skyline bottom-left rectangle packer.
/// </summary>
public sealed class StbFontPacker : IDisposable
{
    private const long ParallelGlyphThreshold = 16;

    private readonly Memory<byte> _pixels;
    private readonly int _width;
    private readonly int _height;
    private readonly int _strideInBytes;
    private readonly int _padding;
    private readonly StbOptions _options;
    private readonly FontPackRange[] _singleRange = new FontPackRange[1];
    private SkylineRectPacker? _packer;
    private int _hOversample = 1;
    private int _vOversample = 1;
    private bool _skipMissing;

    private StbFontPacker(
        Memory<byte> pixels,
        int width,
        int height,
        int strideInBytes,
        int padding,
        StbOptions options)
    {
        _pixels = pixels;
        _width = width;
        _height = height;
        _strideInBytes = strideInBytes;
        _padding = padding;
        _options = options;
        _packer = new SkylineRectPacker(width - padding, height - padding, width - padding);

        if (!pixels.IsEmpty)
        {
            if (strideInBytes == width)
            {
                pixels.Span.Slice(0, width * height).Clear();
            }
            else
            {
                for (int row = 0; row < height; ++row)
                {
                    pixels.Span.Slice(row * strideInBytes, width).Clear();
                }
            }
        }
    }

    /// <summary>Gets the width of the target atlas in pixels.</summary>
    public int Width => _width;

    /// <summary>Gets the height of the target atlas in pixels.</summary>
    public int Height => _height;

    /// <summary>Gets the stride between consecutive rows of the target atlas in bytes.</summary>
    public int StrideInBytes => _strideInBytes;

    /// <summary>Gets the padding in pixels inserted between packed glyphs.</summary>
    public int Padding => _padding;

    /// <summary>Gets the current horizontal oversampling factor.</summary>
    public int HorizontalOversample => _hOversample;

    /// <summary>Gets the current vertical oversampling factor.</summary>
    public int VerticalOversample => _vOversample;

    /// <summary>Gets a value indicating whether missing codepoints are skipped instead of rendered with glyph 0.</summary>
    public bool SkipMissingCodepoints => _skipMissing;

    /// <summary>
    /// Initializes a new font atlas packer targeting <paramref name="pixels"/>.
    /// </summary>
    /// <param name="pixels">Destination 8-bit atlas buffer (cleared to zero if non-empty).</param>
    /// <param name="width">Atlas width in pixels.</param>
    /// <param name="height">Atlas height in pixels.</param>
    /// <param name="strideInBytes">Row stride in bytes, or <c>0</c> for tightly packed rows (<paramref name="width"/>).</param>
    /// <param name="padding">Padding pixels left between packed glyphs (typically <c>1</c> for bilinear filtering).</param>
    /// <param name="options">Optional safety limits and parallelism settings.</param>
    /// <returns>A disposable <see cref="StbFontPacker"/> instance.</returns>
    public static StbFontPacker Begin(
        Memory<byte> pixels,
        int width,
        int height,
        int strideInBytes = 0,
        int padding = 1,
        StbOptions? options = null)
    {
        StbOptions resolvedOptions = options ?? StbOptions.Default;
        Guard.ThrowIfNegative(padding, nameof(padding));
        resolvedOptions.Limits.ValidateDimensions(width, height, 1);

        if (width - padding <= 0 || height - padding <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(padding),
                padding,
                $"Padding ({padding}) must be smaller than atlas dimensions ({width}x{height}).");
        }

        int stride = strideInBytes != 0 ? strideInBytes : width;
        Guard.ThrowIfLessThan(stride, width, nameof(strideInBytes));

        if (!pixels.IsEmpty)
        {
            long required = (long)(height - 1) * stride + width;
            if (pixels.Length < required)
            {
                throw new ArgumentException(
                    $"Atlas pixel buffer length ({pixels.Length}) is smaller than required ({required}).",
                    nameof(pixels));
            }
        }

        return new StbFontPacker(pixels, width, height, stride, padding, resolvedOptions);
    }

    /// <summary>Releases pooled internal packing storage.</summary>
    public void Dispose()
    {
        SkylineRectPacker? packer = _packer;
        if (packer is not null)
        {
            _packer = null;
            packer.Dispose();
        }
    }

    /// <summary>Sets horizontal and vertical oversampling factors (each in [1, 8]) for subsequent pack calls.</summary>
    /// <param name="hOversample">Horizontal oversampling factor (1..8).</param>
    /// <param name="vOversample">Vertical oversampling factor (1..8).</param>
    public void SetOversampling(int hOversample, int vOversample)
    {
        EnsureNotDisposed();
        if (hOversample < 1 || hOversample > TrueTypeRasterizer.MaxOversample)
        {
            throw new ArgumentOutOfRangeException(nameof(hOversample), hOversample, "Oversampling factor must be in [1, 8].");
        }

        if (vOversample < 1 || vOversample > TrueTypeRasterizer.MaxOversample)
        {
            throw new ArgumentOutOfRangeException(nameof(vOversample), vOversample, "Oversampling factor must be in [1, 8].");
        }

        _hOversample = hOversample;
        _vOversample = vOversample;
    }

    /// <summary>Configures whether codepoints absent from the font are skipped instead of mapped to glyph 0.</summary>
    /// <param name="skip"><c>true</c> to skip missing codepoints; <c>false</c> to render the missing-character glyph.</param>
    public void SetSkipMissingCodepoints(bool skip)
    {
        EnsureNotDisposed();
        _skipMissing = skip;
    }

    /// <summary>
    /// Gathers the rectangle dimensions required for every character in <paramref name="ranges"/>.
    /// </summary>
    /// <param name="font">The source font.</param>
    /// <param name="ranges">The character ranges to measure.</param>
    /// <param name="rects">Destination span receiving one <see cref="FontPackRect"/> per character.</param>
    /// <returns>The total number of rectangles written.</returns>
    public int PackFontRangesGatherRects(
        StbTrueTypeFont font,
        Span<FontPackRange> ranges,
        Span<FontPackRect> rects)
    {
        Guard.ThrowIfNull(font, nameof(font));
        EnsureNotDisposed();

        int k = 0;
        bool missingGlyphAdded = false;

        for (int i = 0; i < ranges.Length; ++i)
        {
            ref FontPackRange range = ref ranges[i];
            float fh = range.FontSize;
            float scale = fh > 0.0f ? font.ScaleForPixelHeight(fh) : font.ScaleForMappingEmToPixels(-fh);
            range.HOversample = (byte)_hOversample;
            range.VOversample = (byte)_vOversample;

            int numChars = range.CharData.Length;
            ReadOnlySpan<int> codepoints = range.Codepoints.Span;
            bool hasArray = !range.Codepoints.IsEmpty;
            if (hasArray && codepoints.Length < numChars)
            {
                throw new ArgumentException("Codepoints length is smaller than CharData length.", nameof(ranges));
            }

            if (k + numChars > rects.Length)
            {
                throw new ArgumentException("Destination rects span is too small for the requested ranges.", nameof(rects));
            }

            for (int j = 0; j < numChars; ++j)
            {
                int cp = hasArray ? codepoints[j] : range.FirstUnicodeCodepoint + j;
                int glyph = font.FindGlyphIndex(cp);
                if (glyph == 0 && (_skipMissing || missingGlyphAdded))
                {
                    rects[k] = new FontPackRect(rects[k].Id, 0, 0, 0, 0, false);
                }
                else
                {
                    GlyphBitmapBox box = font.GetGlyphBitmapBoxSubpixel(
                        glyph,
                        scale * _hOversample,
                        scale * _vOversample,
                        0.0f,
                        0.0f);
                    int rw = box.X1 - box.X0 + _padding + _hOversample - 1;
                    int rh = box.Y1 - box.Y0 + _padding + _vOversample - 1;
                    rects[k] = new FontPackRect(rects[k].Id, rw, rh, 0, 0, false);
                    if (glyph == 0)
                    {
                        missingGlyphAdded = true;
                    }
                }

                ++k;
            }
        }

        return k;
    }

    /// <summary>Packs the gathered rectangles into the atlas using the skyline bottom-left packer.</summary>
    /// <param name="rects">Rectangles to pack in-place.</param>
    public void PackFontRangesPackRects(Span<FontPackRect> rects)
    {
        SkylineRectPacker packer = EnsureNotDisposed();
        _ = packer.PackRects(rects);
    }

    private readonly struct RenderJob
    {
        public readonly int Glyph;
        public readonly int RectX;
        public readonly int RectY;
        public readonly int RectW;
        public readonly int RectH;
        public readonly int OriginX;
        public readonly int OriginY;
        public readonly float ScaleX;
        public readonly float ScaleY;
        public readonly int HOversample;
        public readonly int VOversample;

        public RenderJob(
            int glyph,
            int rectX,
            int rectY,
            int rectW,
            int rectH,
            int originX,
            int originY,
            float scaleX,
            float scaleY,
            int hOversample,
            int vOversample)
        {
            Glyph = glyph;
            RectX = rectX;
            RectY = rectY;
            RectW = rectW;
            RectH = rectH;
            OriginX = originX;
            OriginY = originY;
            ScaleX = scaleX;
            ScaleY = scaleY;
            HOversample = hOversample;
            VOversample = vOversample;
        }
    }

    /// <summary>
    /// Renders all packed glyphs from <paramref name="ranges"/> into their assigned rectangles in the atlas.
    /// </summary>
    /// <param name="font">The source font.</param>
    /// <param name="ranges">The character ranges whose <see cref="FontPackRange.CharData"/> entries will be populated.</param>
    /// <param name="rects">The packed rectangles corresponding to <paramref name="ranges"/>.</param>
    /// <returns><c>true</c> if all characters fit and were rendered; otherwise <c>false</c>.</returns>
    public bool PackFontRangesRenderIntoRects(
        StbTrueTypeFont font,
        Span<FontPackRange> ranges,
        Span<FontPackRect> rects)
    {
        Guard.ThrowIfNull(font, nameof(font));
        EnsureNotDisposed();

        bool returnValue = true;
        int missingRange = -1;
        int missingGlyph = -1;

        int oldHOver = _hOversample;
        int oldVOver = _vOversample;

        int totalChars = 0;
        for (int i = 0; i < ranges.Length; ++i)
        {
            totalChars += ranges[i].CharData.Length;
        }

        int dop = _options.ResolveDegreeOfParallelism(totalChars, ParallelGlyphThreshold);
        bool useParallel = dop > 1 && totalChars >= ParallelGlyphThreshold && !_pixels.IsEmpty;

        RenderJob[]? jobs = useParallel ? ArrayPool<RenderJob>.Shared.Rent(totalChars) : null;
        int jobCount = 0;

        try
        {
            int k = 0;
            for (int i = 0; i < ranges.Length; ++i)
            {
                ref FontPackRange range = ref ranges[i];
                float fh = range.FontSize;
                float scale = fh > 0.0f ? font.ScaleForPixelHeight(fh) : font.ScaleForMappingEmToPixels(-fh);
                int hOver = range.HOversample == 0 ? 1 : range.HOversample;
                int vOver = range.VOversample == 0 ? 1 : range.VOversample;
                _hOversample = hOver;
                _vOversample = vOver;

                float recipH = 1.0f / hOver;
                float recipV = 1.0f / vOver;
                float subX = TrueTypeRasterizer.OversampleShift(hOver);
                float subY = TrueTypeRasterizer.OversampleShift(vOver);

                Span<PackedChar> charData = range.CharData.Span;
                ReadOnlySpan<int> codepoints = range.Codepoints.Span;
                bool hasArray = !range.Codepoints.IsEmpty;

                for (int j = 0; j < charData.Length; ++j)
                {
                    ref FontPackRect r = ref rects[k];
                    if (r.WasPacked && r.Width != 0 && r.Height != 0)
                    {
                        int cp = hasArray ? codepoints[j] : range.FirstUnicodeCodepoint + j;
                        int glyph = font.FindGlyphIndex(cp);

                        r.X += _padding;
                        r.Y += _padding;
                        r.Width -= _padding;
                        r.Height -= _padding;

                        GlyphHMetrics hm = font.GetGlyphHMetrics(glyph);
                        GlyphBitmapBox box = font.GetGlyphBitmapBox(glyph, scale * hOver, scale * vOver);

                        if (useParallel)
                        {
                            jobs![jobCount++] = new RenderJob(
                                glyph,
                                r.X,
                                r.Y,
                                r.Width,
                                r.Height,
                                box.X0,
                                box.Y0,
                                scale * hOver,
                                scale * vOver,
                                hOver,
                                vOver);
                        }
                        else if (!_pixels.IsEmpty)
                        {
                            RenderPackedGlyph(
                                font,
                                _pixels.Span,
                                _strideInBytes,
                                glyph,
                                r.X,
                                r.Y,
                                r.Width,
                                r.Height,
                                box.X0,
                                box.Y0,
                                scale * hOver,
                                scale * vOver,
                                hOver,
                                vOver);
                        }

                        charData[j] = new PackedChar(
                            unchecked((ushort)r.X),
                            unchecked((ushort)r.Y),
                            unchecked((ushort)(r.X + r.Width)),
                            unchecked((ushort)(r.Y + r.Height)),
                            box.X0 * recipH + subX,
                            box.Y0 * recipV + subY,
                            scale * hm.AdvanceWidth,
                            (box.X0 + r.Width) * recipH + subX,
                            (box.Y0 + r.Height) * recipV + subY);

                        if (glyph == 0)
                        {
                            missingRange = i;
                            missingGlyph = j;
                        }
                    }
                    else if (_skipMissing)
                    {
                        returnValue = false;
                    }
                    else if (r.WasPacked && r.Width == 0 && r.Height == 0 && missingGlyph >= 0)
                    {
                        charData[j] = ranges[missingRange].CharData.Span[missingGlyph];
                    }
                    else
                    {
                        returnValue = false;
                    }

                    ++k;
                }
            }

            if (useParallel && jobCount > 0)
            {
                Memory<byte> pixelsMem = _pixels;
                int stride = _strideInBytes;
                RenderJob[] jobArr = jobs!;
                Parallel.For(0, jobCount, new ParallelOptions { MaxDegreeOfParallelism = dop }, idx =>
                {
                    RenderJob job = jobArr[idx];
                    RenderPackedGlyph(
                        font,
                        pixelsMem.Span,
                        stride,
                        job.Glyph,
                        job.RectX,
                        job.RectY,
                        job.RectW,
                        job.RectH,
                        job.OriginX,
                        job.OriginY,
                        job.ScaleX,
                        job.ScaleY,
                        job.HOversample,
                        job.VOversample);
                });
            }
        }
        finally
        {
            _hOversample = oldHOver;
            _vOversample = oldVOver;
            if (jobs is not null)
            {
                ArrayPool<RenderJob>.Shared.Return(jobs);
            }
        }

        return returnValue;
    }

    private static void RenderPackedGlyph(
        StbTrueTypeFont font,
        Span<byte> atlasPixels,
        int stride,
        int glyph,
        int rx,
        int ry,
        int rw,
        int rh,
        int originX,
        int originY,
        float scaleX,
        float scaleY,
        int hOver,
        int vOver)
    {
        Span<byte> sub = atlasPixels.Slice(rx + ry * stride);
        int renderW = rw - hOver + 1;
        int renderH = rh - vOver + 1;
        if (renderW > 0 && renderH > 0)
        {
            font.MakeGlyphBitmapSubpixelWithOrigin(
                sub, renderW, renderH, stride, scaleX, scaleY, 0.0f, 0.0f, glyph, originX, originY);
        }

        if (hOver > 1)
        {
            TrueTypeRasterizer.HPrefilter(sub, rw, rh, stride, hOver);
        }

        if (vOver > 1)
        {
            TrueTypeRasterizer.VPrefilter(sub, rw, rh, stride, vOver, RasterizerScratch.Get());
        }
    }

    /// <summary>Packs multiple character ranges from a parsed font into this atlas.</summary>
    /// <param name="font">The source font.</param>
    /// <param name="ranges">The character ranges to pack.</param>
    /// <returns><c>true</c> if all characters fit; otherwise <c>false</c>.</returns>
    public bool PackFontRanges(StbTrueTypeFont font, Span<FontPackRange> ranges)
    {
        Guard.ThrowIfNull(font, nameof(font));
        EnsureNotDisposed();

        int total = 0;
        for (int i = 0; i < ranges.Length; ++i)
        {
            Span<PackedChar> cd = ranges[i].CharData.Span;
            for (int j = 0; j < cd.Length; ++j)
            {
                cd[j].X0 = 0;
                cd[j].Y0 = 0;
                cd[j].X1 = 0;
                cd[j].Y1 = 0;
            }

            total += cd.Length;
        }

        if (total == 0)
        {
            return true;
        }

        FontPackRect[] rects = ArrayPool<FontPackRect>.Shared.Rent(total);
        try
        {
            Span<FontPackRect> rectSpan = rects.AsSpan(0, total);
            rectSpan.Clear();
            int gathered = PackFontRangesGatherRects(font, ranges, rectSpan);
            PackFontRangesPackRects(rectSpan.Slice(0, gathered));
            return PackFontRangesRenderIntoRects(font, ranges, rectSpan.Slice(0, gathered));
        }
        finally
        {
            ArrayPool<FontPackRect>.Shared.Return(rects);
        }
    }

    /// <summary>Packs multiple character ranges from raw font bytes into this atlas.</summary>
    /// <param name="fontData">The raw font bytes.</param>
    /// <param name="fontIndex">Zero-based font index within a collection (0 for standalone fonts).</param>
    /// <param name="ranges">The character ranges to pack.</param>
    /// <returns><c>true</c> if all characters fit; otherwise <c>false</c>.</returns>
    public bool PackFontRanges(ReadOnlyMemory<byte> fontData, int fontIndex, Span<FontPackRange> ranges)
    {
        StbTrueTypeFont font = StbTrueTypeFont.LoadIndex(fontData, fontIndex, _options);
        return PackFontRanges(font, ranges);
    }

    /// <summary>Packs a single contiguous Unicode codepoint range from a parsed font into this atlas.</summary>
    /// <param name="font">The source font.</param>
    /// <param name="fontSize">Pixel height (positive) or point/EM size via <see cref="StbTrueType.PointSize"/> (negative).</param>
    /// <param name="firstUnicodeCodepoint">First Unicode codepoint in the range.</param>
    /// <param name="charData">Destination buffer whose length determines the number of codepoints to pack.</param>
    /// <returns><c>true</c> if all characters fit; otherwise <c>false</c>.</returns>
    public bool PackFontRange(
        StbTrueTypeFont font,
        float fontSize,
        int firstUnicodeCodepoint,
        Memory<PackedChar> charData)
    {
        _singleRange[0] = new FontPackRange
        {
            FontSize = fontSize,
            FirstUnicodeCodepoint = firstUnicodeCodepoint,
            CharData = charData,
        };
        return PackFontRanges(font, _singleRange);
    }

    /// <summary>Packs a single contiguous Unicode codepoint range from raw font bytes into this atlas.</summary>
    /// <param name="fontData">The raw font bytes.</param>
    /// <param name="fontIndex">Zero-based font index.</param>
    /// <param name="fontSize">Pixel height (positive) or negative point size.</param>
    /// <param name="firstUnicodeCodepoint">First Unicode codepoint in the range.</param>
    /// <param name="charData">Destination buffer whose length determines the number of codepoints to pack.</param>
    /// <returns><c>true</c> if all characters fit; otherwise <c>false</c>.</returns>
    public bool PackFontRange(
        ReadOnlyMemory<byte> fontData,
        int fontIndex,
        float fontSize,
        int firstUnicodeCodepoint,
        Memory<PackedChar> charData)
    {
        StbTrueTypeFont font = StbTrueTypeFont.LoadIndex(fontData, fontIndex, _options);
        return PackFontRange(font, fontSize, firstUnicodeCodepoint, charData);
    }

    private SkylineRectPacker EnsureNotDisposed()
    {
        SkylineRectPacker? packer = _packer;
        Guard.ThrowIfDisposed(packer is null, this);
        return packer!;
    }
}
