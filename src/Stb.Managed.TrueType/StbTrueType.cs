using Stb.Managed.Internal;
using Stb.Managed.TrueType.Internal;

namespace Stb.Managed.TrueType;

/// <summary>
/// Static entry points for TrueType/OpenType font collection inspection, font matching,
/// legacy bitmap baking, quad calculation, and standalone shape rasterization.
/// </summary>
public static class StbTrueType
{
    /// <summary>
    /// Converts a point/EM size into the negative representation accepted by <see cref="StbFontPacker"/>
    /// (equivalent to <c>STBTT_POINT_SIZE(x)</c>).
    /// </summary>
    /// <param name="size">Positive EM size in pixels.</param>
    /// <returns>Negative size value signaling EM-based scaling.</returns>
    public static float PointSize(float size) => -size;

    /// <summary>
    /// Determines the number of fonts stored in a <c>.ttf</c>, <c>.otf</c>, or <c>.ttc</c> buffer.
    /// </summary>
    /// <param name="fontData">The raw font bytes.</param>
    /// <returns>The number of fonts, or <c>0</c> if the header is unrecognized or truncated.</returns>
    public static int GetNumberOfFonts(ReadOnlySpan<byte> fontData) =>
        TrueTypeBinary.GetNumberOfFonts(fontData);

    /// <summary>
    /// Gets the byte offset of the <paramref name="index"/>-th font in a font file or TrueType collection.
    /// </summary>
    /// <param name="fontData">The raw font bytes.</param>
    /// <param name="index">Zero-based font index.</param>
    /// <returns>The byte offset of the font, or <c>-1</c> if <paramref name="index"/> is out of range or the data is invalid.</returns>
    public static int GetFontOffsetForIndex(ReadOnlySpan<byte> fontData, int index) =>
        TrueTypeBinary.GetFontOffsetForIndex(fontData, index);

    /// <summary>
    /// Searches a font or TrueType collection for a font matching <paramref name="name"/> and <paramref name="flags"/>.
    /// </summary>
    /// <param name="fontData">The raw font or collection bytes.</param>
    /// <param name="name">Case-sensitive font name (UTF-8).</param>
    /// <param name="flags">Optional <c>head.macStyle</c> filter flags.</param>
    /// <returns>The byte offset of the matching font, or <c>-1</c> if no match was found.</returns>
    public static int FindMatchingFont(
        ReadOnlySpan<byte> fontData,
        string name,
        FontMacStyle flags = FontMacStyle.DontCare)
    {
        Guard.ThrowIfNull(name, nameof(name));
        return TrueTypeNames.FindMatchingFont(fontData, name, flags);
    }

    /// <summary>
    /// Compares a UTF-8 byte sequence against a big-endian UTF-16 byte sequence for exact equality.
    /// </summary>
    /// <param name="utf8">The UTF-8 encoded string bytes.</param>
    /// <param name="utf16BigEndian">The big-endian UTF-16 encoded string bytes (such as from <see cref="StbTrueTypeFont.TryGetFontNameString"/>).</param>
    /// <returns><c>true</c> if both sequences represent identical Unicode text; otherwise <c>false</c>.</returns>
    public static bool CompareUtf8ToUtf16BigEndian(ReadOnlySpan<byte> utf8, ReadOnlySpan<byte> utf16BigEndian) =>
        TrueTypeNames.CompareUtf8ToUtf16BigEndian(utf8, utf16BigEndian);

    /// <summary>
    /// Queries scaled vertical metrics directly from raw font bytes without retaining a <see cref="StbTrueTypeFont"/>.
    /// </summary>
    /// <param name="fontData">The raw font or collection bytes.</param>
    /// <param name="fontIndex">Zero-based font index.</param>
    /// <param name="size">Positive pixel height or negative EM size (via <see cref="PointSize"/>).</param>
    /// <param name="options">Optional safety limits.</param>
    /// <returns>Scaled ascent, descent, and line gap in pixels.</returns>
    public static ScaledFontVMetrics GetScaledFontVMetrics(
        ReadOnlyMemory<byte> fontData,
        int fontIndex,
        float size,
        StbOptions? options = null)
    {
        StbTrueTypeFont font = StbTrueTypeFont.LoadIndex(fontData, fontIndex, options);
        float scale = size > 0.0f ? font.ScaleForPixelHeight(size) : font.ScaleForMappingEmToPixels(-size);
        FontVMetrics vm = font.GetFontVMetrics();
        return new ScaledFontVMetrics(vm.Ascent * scale, vm.Descent * scale, vm.LineGap * scale);
    }

    /// <summary>
    /// Bakes a contiguous range of characters into an 8-bit grayscale bitmap using the simple row-shelf layout
    /// matching <c>stbtt_BakeFontBitmap</c>.
    /// </summary>
    /// <param name="fontData">The raw font bytes.</param>
    /// <param name="offset">Byte offset of the font face (0 for standalone files).</param>
    /// <param name="pixelHeight">Font pixel height.</param>
    /// <param name="pixels">Destination 8-bit bitmap (<c>width * height</c> bytes).</param>
    /// <param name="width">Bitmap width in pixels.</param>
    /// <param name="height">Bitmap height in pixels.</param>
    /// <param name="firstChar">First Unicode codepoint to bake.</param>
    /// <param name="charData">Destination span receiving baked glyph metadata.</param>
    /// <param name="options">Optional safety limits.</param>
    /// <returns>
    /// Positive: the first unused row of the bitmap if all characters fit.
    /// Negative: <c>-count</c> where <c>count</c> is the number of characters that fit (or <c>-1</c> if font loading failed).
    /// Zero: no characters fit and no rows were used.
    /// </returns>
    public static int BakeFontBitmap(
        ReadOnlyMemory<byte> fontData,
        int offset,
        float pixelHeight,
        Span<byte> pixels,
        int width,
        int height,
        int firstChar,
        Span<BakedChar> charData,
        StbOptions? options = null)
    {
        StbOptions resolvedOptions = options ?? StbOptions.Default;
        int totalBytes = resolvedOptions.Limits.ValidateDimensions(width, height, 1);
        if (pixels.Length < totalBytes)
        {
            throw new ArgumentException(
                $"Pixel buffer length ({pixels.Length}) is smaller than required ({totalBytes}).",
                nameof(pixels));
        }

        if (!StbTrueTypeFont.TryLoad(fontData, offset, out StbTrueTypeFont? font, resolvedOptions) || font is null)
        {
            return -1;
        }

        return BakeFontBitmap(font, pixelHeight, pixels, width, height, firstChar, charData, resolvedOptions);
    }

    /// <summary>
    /// Simple single-pass font atlas baking for a range of consecutive codepoints using an already loaded <see cref="StbTrueTypeFont"/>.
    /// </summary>
    /// <param name="font">Pre-loaded font.</param>
    /// <param name="pixelHeight">Target pixel height.</param>
    /// <param name="pixels">Caller-owned 8-bit bitmap (<c>width * height</c> bytes).</param>
    /// <param name="width">Atlas width in pixels.</param>
    /// <param name="height">Atlas height in pixels.</param>
    /// <param name="firstChar">First Unicode codepoint to bake.</param>
    /// <param name="charData">Receives baked glyph metadata (length determines count of characters).</param>
    /// <param name="options">Optional execution limits.</param>
    /// <returns>
    /// Positive: the first unused row of the bitmap if all characters fit.
    /// Negative: <c>-count</c> where <c>count</c> is the number of characters that fit.
    /// Zero: no characters fit and no rows were used.
    /// </returns>
    public static int BakeFontBitmap(
        StbTrueTypeFont font,
        float pixelHeight,
        Span<byte> pixels,
        int width,
        int height,
        int firstChar,
        Span<BakedChar> charData,
        StbOptions? options = null)
    {
        Guard.ThrowIfNull(font, nameof(font));
        StbOptions resolvedOptions = options ?? StbOptions.Default;
        int totalBytes = resolvedOptions.Limits.ValidateDimensions(width, height, 1);
        if (pixels.Length < totalBytes)
        {
            throw new ArgumentException(
                $"Pixel buffer length ({pixels.Length}) is smaller than required ({totalBytes}).",
                nameof(pixels));
        }

        pixels.Slice(0, totalBytes).Clear();

        int x = 1;
        int y = 1;
        int bottomY = 1;
        float scale = font.ScaleForPixelHeight(pixelHeight);

        for (int i = 0; i < charData.Length; ++i)
        {
            int g = font.FindGlyphIndex(firstChar + i);
            GlyphHMetrics hm = font.GetGlyphHMetrics(g);
            GlyphBitmapBox box = font.GetGlyphBitmapBox(g, scale, scale);
            int gw = box.Width;
            int gh = box.Height;

            if (x + gw + 1 >= width)
            {
                y = bottomY;
                x = 1;
            }

            if (y + gh + 1 >= height)
            {
                return -i;
            }

            if (gw > 0 && gh > 0)
            {
                font.MakeGlyphBitmapSubpixelWithOrigin(
                    pixels.Slice(x + y * width),
                    gw,
                    gh,
                    width,
                    scale,
                    scale,
                    0.0f,
                    0.0f,
                    g,
                    box.X0,
                    box.Y0);
            }

            charData[i] = new BakedChar(
                unchecked((ushort)x),
                unchecked((ushort)y),
                unchecked((ushort)(x + gw)),
                unchecked((ushort)(y + gh)),
                box.X0,
                box.Y0,
                scale * hm.AdvanceWidth);

            x = x + gw + 1;
            if (y + gh + 1 > bottomY)
            {
                bottomY = y + gh + 1;
            }
        }

        return bottomY;
    }

    /// <summary>
    /// Computes the screen-space quad and texture coordinates for a baked glyph and advances <paramref name="xPos"/>.
    /// </summary>
    /// <param name="charData">Baked character array produced by <see cref="BakeFontBitmap(StbTrueTypeFont, float, Span{byte}, int, int, int, Span{BakedChar}, StbOptions?)"/>.</param>
    /// <param name="bitmapWidth">Atlas width in pixels.</param>
    /// <param name="bitmapHeight">Atlas height in pixels.</param>
    /// <param name="charIndex">Index into <paramref name="charData"/>.</param>
    /// <param name="xPos">Current horizontal cursor position (updated with advance).</param>
    /// <param name="yPos">Current vertical baseline position.</param>
    /// <param name="openGlFillRule"><c>true</c> for OpenGL/DX10+ pixel centers; <c>false</c> for DX9 half-pixel bias.</param>
    /// <returns>The aligned quad for rendering.</returns>
    public static AlignedQuad GetBakedQuad(
        ReadOnlySpan<BakedChar> charData,
        int bitmapWidth,
        int bitmapHeight,
        int charIndex,
        ref float xPos,
        ref float yPos,
        bool openGlFillRule = true)
    {
        Guard.ThrowIfIndexOutOfRange(charIndex, charData.Length, nameof(charIndex));
        Guard.ThrowIfNegativeOrZero(bitmapWidth, nameof(bitmapWidth));
        Guard.ThrowIfNegativeOrZero(bitmapHeight, nameof(bitmapHeight));

        float d3dBias = openGlFillRule ? 0.0f : -0.5f;
        float ipw = 1.0f / bitmapWidth;
        float iph = 1.0f / bitmapHeight;
        ref readonly BakedChar b = ref charData[charIndex];

        int roundX = TrueTypeBinary.IFloor((xPos + b.XOff) + 0.5f);
        int roundY = TrueTypeBinary.IFloor((yPos + b.YOff) + 0.5f);

        var quad = new AlignedQuad(
            roundX + d3dBias,
            roundY + d3dBias,
            b.X0 * ipw,
            b.Y0 * iph,
            roundX + b.X1 - b.X0 + d3dBias,
            roundY + b.Y1 - b.Y0 + d3dBias,
            b.X1 * ipw,
            b.Y1 * iph);

        xPos += b.XAdvance;
        return quad;
    }

    /// <summary>
    /// Computes the screen-space quad and texture coordinates for a packed glyph and advances <paramref name="xPos"/>.
    /// </summary>
    /// <param name="charData">Packed character array produced by <see cref="StbFontPacker"/>.</param>
    /// <param name="bitmapWidth">Atlas width in pixels.</param>
    /// <param name="bitmapHeight">Atlas height in pixels.</param>
    /// <param name="charIndex">Index into <paramref name="charData"/>.</param>
    /// <param name="xPos">Current horizontal cursor position (updated with advance).</param>
    /// <param name="yPos">Current vertical baseline position.</param>
    /// <param name="alignToInteger"><c>true</c> to snap quad positions to integer pixel coordinates.</param>
    /// <returns>The aligned quad for rendering.</returns>
    public static AlignedQuad GetPackedQuad(
        ReadOnlySpan<PackedChar> charData,
        int bitmapWidth,
        int bitmapHeight,
        int charIndex,
        ref float xPos,
        ref float yPos,
        bool alignToInteger = false)
    {
        Guard.ThrowIfIndexOutOfRange(charIndex, charData.Length, nameof(charIndex));
        Guard.ThrowIfNegativeOrZero(bitmapWidth, nameof(bitmapWidth));
        Guard.ThrowIfNegativeOrZero(bitmapHeight, nameof(bitmapHeight));

        float ipw = 1.0f / bitmapWidth;
        float iph = 1.0f / bitmapHeight;
        ref readonly PackedChar b = ref charData[charIndex];

        float qx0, qy0, qx1, qy1;
        if (alignToInteger)
        {
            float x = TrueTypeBinary.IFloor((xPos + b.XOff) + 0.5f);
            float y = TrueTypeBinary.IFloor((yPos + b.YOff) + 0.5f);
            qx0 = x;
            qy0 = y;
            qx1 = x + b.XOff2 - b.XOff;
            qy1 = y + b.YOff2 - b.YOff;
        }
        else
        {
            qx0 = xPos + b.XOff;
            qy0 = yPos + b.YOff;
            qx1 = xPos + b.XOff2;
            qy1 = yPos + b.YOff2;
        }

        xPos += b.XAdvance;

        return new AlignedQuad(
            qx0,
            qy0,
            b.X0 * ipw,
            b.Y0 * iph,
            qx1,
            qy1,
            b.X1 * ipw,
            b.Y1 * iph);
    }

    /// <summary>
    /// Rasterizes an arbitrary outline defined by <paramref name="vertices"/> into an 8-bit coverage bitmap.
    /// </summary>
    /// <param name="pixels">Destination 8-bit bitmap.</param>
    /// <param name="width">Bitmap width in pixels.</param>
    /// <param name="height">Bitmap height in pixels.</param>
    /// <param name="stride">Row stride in bytes.</param>
    /// <param name="flatnessInPixels">Allowable curve flattening error in pixels (typically <c>0.35f</c>).</param>
    /// <param name="vertices">Outline vertices.</param>
    /// <param name="scaleX">Horizontal scale factor.</param>
    /// <param name="scaleY">Vertical scale factor.</param>
    /// <param name="shiftX">Horizontal translation applied before rasterization.</param>
    /// <param name="shiftY">Vertical translation applied before rasterization.</param>
    /// <param name="xOffset">Integer horizontal pixel origin offset.</param>
    /// <param name="yOffset">Integer vertical pixel origin offset.</param>
    /// <param name="invert"><c>true</c> to flip vertically (font coordinates y-up to bitmap coordinates y-down).</param>
    public static void Rasterize(
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
        int xOffset,
        int yOffset,
        bool invert)
    {
        if (width <= 0 || height <= 0)
        {
            return;
        }

        StbTrueTypeFont.ValidateOutputBuffer(pixels, width, height, stride);
        TrueTypeRasterizer.Rasterize(
            pixels,
            width,
            height,
            stride,
            flatnessInPixels,
            vertices,
            scaleX,
            scaleY,
            shiftX,
            shiftY,
            xOffset,
            yOffset,
            invert,
            RasterizerScratch.Get());
    }
}
