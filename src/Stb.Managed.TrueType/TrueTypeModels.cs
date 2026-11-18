namespace Stb.Managed.TrueType;

/// <summary>
/// Specifies the segment type of a <see cref="GlyphVertex"/> in a glyph outline.
/// </summary>
public enum GlyphVertexType : byte
{
    /// <summary>Moves the pen to a new contour starting point (<c>X</c>, <c>Y</c>).</summary>
    Move = 1,

    /// <summary>Draws a straight line segment to (<c>X</c>, <c>Y</c>).</summary>
    Line = 2,

    /// <summary>Draws a quadratic Bezier curve to (<c>X</c>, <c>Y</c>) via control point (<c>Cx</c>, <c>Cy</c>).</summary>
    Curve = 3,

    /// <summary>Draws a cubic Bezier curve to (<c>X</c>, <c>Y</c>) via control points (<c>Cx</c>, <c>Cy</c>) and (<c>Cx1</c>, <c>Cy1</c>).</summary>
    Cubic = 4,
}

/// <summary>
/// Represents a single outline vertex in unscaled font coordinates (y increases upward).
/// </summary>
/// <param name="X">Endpoint X coordinate in font design units.</param>
/// <param name="Y">Endpoint Y coordinate in font design units.</param>
/// <param name="Cx">First Bezier control point X coordinate.</param>
/// <param name="Cy">First Bezier control point Y coordinate.</param>
/// <param name="Cx1">Second cubic Bezier control point X coordinate.</param>
/// <param name="Cy1">Second cubic Bezier control point Y coordinate.</param>
/// <param name="Type">The outline segment command type.</param>
public readonly record struct GlyphVertex(
    short X,
    short Y,
    short Cx,
    short Cy,
    short Cx1,
    short Cy1,
    GlyphVertexType Type);

/// <summary>
/// Vertical typographic metrics for a font in unscaled font design units.
/// </summary>
/// <param name="Ascent">Coordinate above the baseline that the font extends.</param>
/// <param name="Descent">Coordinate below the baseline that the font extends (typically negative).</param>
/// <param name="LineGap">Spacing between one row's descent and the next row's ascent.</param>
public readonly record struct FontVMetrics(int Ascent, int Descent, int LineGap);

/// <summary>
/// Vertical layout metrics from the font's <c>vhea</c> table in unscaled font design units.
/// </summary>
/// <param name="VertTypoAscender">Vertical typographic ascender.</param>
/// <param name="VertTypoDescender">Vertical typographic descender.</param>
/// <param name="VertTypoLineGap">Vertical typographic line gap.</param>
public readonly record struct FontHMetrics(int VertTypoAscender, int VertTypoDescender, int VertTypoLineGap);

/// <summary>
/// Scaled vertical metrics for a font at a requested pixel or em size.
/// </summary>
/// <param name="Ascent">Scaled ascent in pixels.</param>
/// <param name="Descent">Scaled descent in pixels.</param>
/// <param name="LineGap">Scaled line gap in pixels.</param>
public readonly record struct ScaledFontVMetrics(float Ascent, float Descent, float LineGap);

/// <summary>
/// Underline position and thickness metrics from the font's <c>post</c> table in unscaled font units.
/// </summary>
/// <param name="Position">Suggested distance of the top of the underline from the baseline.</param>
/// <param name="Thickness">Suggested underline stroke thickness.</param>
public readonly record struct FontUnderlineMetrics(int Position, int Thickness);

/// <summary>
/// Horizontal metrics for a single glyph in unscaled font design units.
/// </summary>
/// <param name="AdvanceWidth">Offset from the current horizontal position to the next horizontal position.</param>
/// <param name="LeftSideBearing">Offset from the current horizontal position to the left edge of the glyph.</param>
public readonly record struct GlyphHMetrics(int AdvanceWidth, int LeftSideBearing);

/// <summary>
/// Vertical metrics for a single glyph from the <c>vmtx</c> table in unscaled font design units.
/// </summary>
/// <param name="AdvanceHeight">Vertical advance height.</param>
/// <param name="TopSideBearing">Vertical top side bearing.</param>
public readonly record struct GlyphVMetrics(int AdvanceHeight, int TopSideBearing);

/// <summary>
/// Axis-aligned bounding box in unscaled font coordinates (y increases upward).
/// </summary>
/// <param name="X0">Minimum X coordinate.</param>
/// <param name="Y0">Minimum Y coordinate.</param>
/// <param name="X1">Maximum X coordinate.</param>
/// <param name="Y1">Maximum Y coordinate.</param>
public readonly record struct GlyphBox(int X0, int Y0, int X1, int Y1);

/// <summary>
/// Integral pixel bounding box for a rasterized glyph bitmap (y increases downward).
/// </summary>
/// <param name="X0">Left pixel coordinate relative to glyph origin.</param>
/// <param name="Y0">Top pixel coordinate relative to glyph origin.</param>
/// <param name="X1">Right pixel coordinate (exclusive) relative to glyph origin.</param>
/// <param name="Y1">Bottom pixel coordinate (exclusive) relative to glyph origin.</param>
public readonly record struct GlyphBitmapBox(int X0, int Y0, int X1, int Y1)
{
    /// <summary>Gets the width of the bitmap box in pixels.</summary>
    public int Width => X1 - X0;

    /// <summary>Gets the height of the bitmap box in pixels.</summary>
    public int Height => Y1 - Y0;
}

/// <summary>
/// Dimensions and placement offset of a rendered glyph or SDF bitmap.
/// </summary>
/// <param name="Width">Bitmap width in pixels.</param>
/// <param name="Height">Bitmap height in pixels.</param>
/// <param name="XOffset">Horizontal pixel offset from the glyph origin to the bitmap top-left.</param>
/// <param name="YOffset">Vertical pixel offset from the glyph origin to the bitmap top-left.</param>
public readonly record struct GlyphBitmapMetrics(int Width, int Height, int XOffset, int YOffset);

/// <summary>
/// A single kerning pair entry from the legacy <c>kern</c> table.
/// </summary>
/// <param name="Glyph1">Left glyph index.</param>
/// <param name="Glyph2">Right glyph index.</param>
/// <param name="Advance">Additional horizontal advance in unscaled font units.</param>
public readonly record struct KerningEntry(int Glyph1, int Glyph2, int Advance);

/// <summary>
/// Atlas placement and advance metadata produced by <see cref="StbTrueType.BakeFontBitmap(StbTrueTypeFont, float, Span{byte}, int, int, int, Span{BakedChar}, StbOptions?)"/>.
/// </summary>
/// <param name="X0">Left texel coordinate in the atlas.</param>
/// <param name="Y0">Top texel coordinate in the atlas.</param>
/// <param name="X1">Right texel coordinate in the atlas.</param>
/// <param name="Y1">Bottom texel coordinate in the atlas.</param>
/// <param name="XOff">Horizontal pixel offset from glyph origin.</param>
/// <param name="YOff">Vertical pixel offset from glyph origin.</param>
/// <param name="XAdvance">Scaled horizontal advance in pixels.</param>
public record struct BakedChar(
    ushort X0,
    ushort Y0,
    ushort X1,
    ushort Y1,
    float XOff,
    float YOff,
    float XAdvance);

/// <summary>
/// Atlas placement and advance metadata produced by <see cref="StbFontPacker"/>.
/// </summary>
/// <param name="X0">Left texel coordinate in the atlas.</param>
/// <param name="Y0">Top texel coordinate in the atlas.</param>
/// <param name="X1">Right texel coordinate in the atlas.</param>
/// <param name="Y1">Bottom texel coordinate in the atlas.</param>
/// <param name="XOff">Top-left horizontal offset in pixels relative to the cursor.</param>
/// <param name="YOff">Top-left vertical offset in pixels relative to the cursor.</param>
/// <param name="XAdvance">Scaled horizontal advance in pixels.</param>
/// <param name="XOff2">Bottom-right horizontal offset in pixels relative to the cursor.</param>
/// <param name="YOff2">Bottom-right vertical offset in pixels relative to the cursor.</param>
public record struct PackedChar(
    ushort X0,
    ushort Y0,
    ushort X1,
    ushort Y1,
    float XOff,
    float YOff,
    float XAdvance,
    float XOff2,
    float YOff2);

/// <summary>
/// Screen-space geometry and normalized UV coordinates for rendering a baked or packed glyph quad.
/// </summary>
/// <param name="X0">Top-left screen X coordinate.</param>
/// <param name="Y0">Top-left screen Y coordinate.</param>
/// <param name="S0">Top-left texture U coordinate.</param>
/// <param name="T0">Top-left texture V coordinate.</param>
/// <param name="X1">Bottom-right screen X coordinate.</param>
/// <param name="Y1">Bottom-right screen Y coordinate.</param>
/// <param name="S1">Bottom-right texture U coordinate.</param>
/// <param name="T1">Bottom-right texture V coordinate.</param>
public readonly record struct AlignedQuad(
    float X0,
    float Y0,
    float S0,
    float T0,
    float X1,
    float Y1,
    float S1,
    float T1);

/// <summary>
/// Rectangle descriptor used with the multi-phase <see cref="StbFontPacker"/> API.
/// </summary>
/// <param name="Id">Caller-defined identifier.</param>
/// <param name="Width">Rectangle width in pixels.</param>
/// <param name="Height">Rectangle height in pixels.</param>
/// <param name="X">Packed X coordinate.</param>
/// <param name="Y">Packed Y coordinate.</param>
/// <param name="WasPacked"><c>true</c> if the rectangle was successfully placed in the atlas.</param>
public record struct FontPackRect(
    int Id,
    int Width,
    int Height,
    int X,
    int Y,
    bool WasPacked);

/// <summary>
/// Describes a range or explicit list of Unicode codepoints to pack into a font atlas via <see cref="StbFontPacker"/>.
/// </summary>
public record struct FontPackRange
{
    /// <summary>
    /// Gets or sets the font height in pixels. Positive values use <see cref="StbTrueTypeFont.ScaleForPixelHeight"/>;
    /// negative values (see <see cref="StbTrueType.PointSize"/>) use <see cref="StbTrueTypeFont.ScaleForMappingEmToPixels"/>.
    /// </summary>
    public float FontSize { get; set; }

    /// <summary>
    /// Gets or sets the first Unicode codepoint when <see cref="Codepoints"/> is empty.
    /// </summary>
    public int FirstUnicodeCodepoint { get; set; }

    /// <summary>
    /// Gets or sets an optional explicit list of Unicode codepoints to pack.
    /// When empty, contiguous codepoints starting at <see cref="FirstUnicodeCodepoint"/> are used.
    /// </summary>
    public ReadOnlyMemory<int> Codepoints { get; set; }

    /// <summary>
    /// Gets or sets the destination buffer receiving packed character metadata.
    /// Its length determines the number of characters in this range.
    /// </summary>
    public Memory<PackedChar> CharData { get; set; }

    internal byte HOversample;
    internal byte VOversample;
}

/// <summary>
/// Style flags matching the TrueType <c>head.macStyle</c> field for <see cref="StbTrueType.FindMatchingFont"/>.
/// </summary>
[Flags]
public enum FontMacStyle
{
    /// <summary>Do not filter by <c>macStyle</c> flags.</summary>
    DontCare = 0,

    /// <summary>Match bold fonts.</summary>
    Bold = 1,

    /// <summary>Match italic fonts.</summary>
    Italic = 2,

    /// <summary>Match underlined fonts.</summary>
    Underscore = 4,

    /// <summary>Require that the <c>macStyle</c> bitfield is zero (regular style).</summary>
    None = 8,
}

/// <summary>
/// TrueType <c>name</c> and <c>cmap</c> platform identifiers.
/// </summary>
public enum FontPlatformId
{
    /// <summary>Unicode platform (0).</summary>
    Unicode = 0,

    /// <summary>Macintosh platform (1).</summary>
    Mac = 1,

    /// <summary>ISO platform (2).</summary>
    Iso = 2,

    /// <summary>Microsoft platform (3).</summary>
    Microsoft = 3,
}

/// <summary>
/// Encoding identifiers for <see cref="FontPlatformId.Unicode"/>.
/// </summary>
public enum FontUnicodeEncodingId
{
    /// <summary>Unicode 1.0 semantics (0).</summary>
    Unicode10 = 0,

    /// <summary>Unicode 1.1 semantics (1).</summary>
    Unicode11 = 1,

    /// <summary>ISO/IEC 10646 semantics (2).</summary>
    Iso10646 = 2,

    /// <summary>Unicode 2.0 BMP only (3).</summary>
    Unicode20Bmp = 3,

    /// <summary>Unicode 2.0 full repertoire (4).</summary>
    Unicode20Full = 4,
}

/// <summary>
/// Encoding identifiers for <see cref="FontPlatformId.Microsoft"/>.
/// </summary>
public enum FontMicrosoftEncodingId
{
    /// <summary>Symbol encoding (0).</summary>
    Symbol = 0,

    /// <summary>Unicode BMP (UCS-2) encoding (1).</summary>
    UnicodeBmp = 1,

    /// <summary>ShiftJIS encoding (2).</summary>
    ShiftJis = 2,

    /// <summary>Unicode full repertoire (UCS-4) encoding (10).</summary>
    UnicodeFull = 10,
}

/// <summary>
/// Encoding identifiers for <see cref="FontPlatformId.Mac"/>.
/// </summary>
public enum FontMacEncodingId
{
    /// <summary>Roman (0).</summary>
    Roman = 0,

    /// <summary>Japanese (1).</summary>
    Japanese = 1,

    /// <summary>Chinese Traditional (2).</summary>
    ChineseTraditional = 2,

    /// <summary>Korean (3).</summary>
    Korean = 3,

    /// <summary>Arabic (4).</summary>
    Arabic = 4,

    /// <summary>Hebrew (5).</summary>
    Hebrew = 5,

    /// <summary>Greek (6).</summary>
    Greek = 6,

    /// <summary>Russian (7).</summary>
    Russian = 7,
}

/// <summary>
/// Common language identifiers for <see cref="FontPlatformId.Microsoft"/>.
/// </summary>
public enum FontMicrosoftLanguageId
{
    /// <summary>English (US) (0x0409).</summary>
    English = 0x0409,

    /// <summary>Italian (0x0410).</summary>
    Italian = 0x0410,

    /// <summary>Chinese (PRC) (0x0804).</summary>
    Chinese = 0x0804,

    /// <summary>Japanese (0x0411).</summary>
    Japanese = 0x0411,

    /// <summary>Dutch (0x0413).</summary>
    Dutch = 0x0413,

    /// <summary>Korean (0x0412).</summary>
    Korean = 0x0412,

    /// <summary>French (0x040C).</summary>
    French = 0x040C,

    /// <summary>Russian (0x0419).</summary>
    Russian = 0x0419,

    /// <summary>German (0x0407).</summary>
    German = 0x0407,

    /// <summary>Hebrew (0x040D).</summary>
    Hebrew = 0x040D,

    /// <summary>Swedish (0x041D).</summary>
    Swedish = 0x041D,
}

/// <summary>
/// Common language identifiers for <see cref="FontPlatformId.Mac"/>.
/// </summary>
public enum FontMacLanguageId
{
    /// <summary>English (0).</summary>
    English = 0,

    /// <summary>French (1).</summary>
    French = 1,

    /// <summary>German (2).</summary>
    German = 2,

    /// <summary>Italian (3).</summary>
    Italian = 3,

    /// <summary>Dutch (4).</summary>
    Dutch = 4,

    /// <summary>Swedish (5).</summary>
    Swedish = 5,

    /// <summary>Spanish (6).</summary>
    Spanish = 6,

    /// <summary>Hebrew (10).</summary>
    Hebrew = 10,

    /// <summary>Japanese (11).</summary>
    Japanese = 11,

    /// <summary>Arabic (12).</summary>
    Arabic = 12,

    /// <summary>Chinese Traditional (19).</summary>
    ChineseTraditional = 19,

    /// <summary>Korean (23).</summary>
    Korean = 23,

    /// <summary>Russian (32).</summary>
    Russian = 32,

    /// <summary>Chinese Simplified (33).</summary>
    ChineseSimplified = 33,
}
