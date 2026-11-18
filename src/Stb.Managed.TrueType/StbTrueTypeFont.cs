using System.Runtime.CompilerServices;
using Stb.Managed.Internal;
using Stb.Managed.TrueType.Internal;

namespace Stb.Managed.TrueType;

/// <summary>
/// Represents a parsed TrueType (<c>.ttf</c>), OpenType CFF (<c>.otf</c>), or TrueType Collection (<c>.ttc</c>) font face.
/// Instances are immutable after creation and safe for concurrent reads, metric queries, rasterization, and SDF generation across threads.
/// </summary>
public sealed class StbTrueTypeFont
{
    private const int MaxCompoundGlyphDepth = 64;
    private const int MaxGlyphPoints = 65_536;

    private readonly ReadOnlyMemory<byte> _dataMemory;
    private readonly int _fontStart;
    private readonly int _numGlyphs;
    private readonly int _loca;
    private readonly int _head;
    private readonly int _glyf;
    private readonly int _hhea;
    private readonly int _hmtx;
    private readonly int _vhea;
    private readonly int _vmtx;
    private readonly int _kern;
    private readonly int _gpos;
    private readonly int _os2;
    private readonly int _post;
    private readonly int _svgDocListOffset;
    private readonly int _indexMap;
    private readonly int _indexToLocFormat;
    private readonly CffBuffer _cff;
    private readonly CffBuffer _charstrings;
    private readonly CffBuffer _gsubrs;
    private readonly CffBuffer _subrs;
    private readonly CffBuffer _fontDicts;
    private readonly CffBuffer _fdSelect;

    private StbTrueTypeFont(
        ReadOnlyMemory<byte> dataMemory,
        int fontStart,
        int numGlyphs,
        int loca,
        int head,
        int glyf,
        int hhea,
        int hmtx,
        int vhea,
        int vmtx,
        int kern,
        int gpos,
        int os2,
        int post,
        int svgDocListOffset,
        int indexMap,
        int indexToLocFormat,
        CffBuffer cff,
        CffBuffer charstrings,
        CffBuffer gsubrs,
        CffBuffer subrs,
        CffBuffer fontDicts,
        CffBuffer fdSelect)
    {
        _dataMemory = dataMemory;
        _fontStart = fontStart;
        _numGlyphs = numGlyphs;
        _loca = loca;
        _head = head;
        _glyf = glyf;
        _hhea = hhea;
        _hmtx = hmtx;
        _vhea = vhea;
        _vmtx = vmtx;
        _kern = kern;
        _gpos = gpos;
        _os2 = os2;
        _post = post;
        _svgDocListOffset = svgDocListOffset;
        _indexMap = indexMap;
        _indexToLocFormat = indexToLocFormat;
        _cff = cff;
        _charstrings = charstrings;
        _gsubrs = gsubrs;
        _subrs = subrs;
        _fontDicts = fontDicts;
        _fdSelect = fdSelect;
    }

    /// <summary>Gets the number of glyphs defined in the font.</summary>
    public int NumGlyphs => _numGlyphs;

    /// <summary>Gets a value indicating whether the font uses OpenType CFF (<c>CFF </c>) outlines rather than TrueType (<c>glyf</c>).</summary>
    public bool IsCff => _cff.Size > 0;

    /// <summary>Gets the byte offset of this font face within the underlying font buffer.</summary>
    public int FontOffset => _fontStart;

    /// <summary>Gets the underlying read-only font bytes.</summary>
    public ReadOnlySpan<byte> Data => _dataMemory.Span;

    /// <summary>
    /// Loads a font face at the given byte offset from <paramref name="fontData"/>.
    /// The returned instance references <paramref name="fontData"/> without copying; the caller must not mutate the underlying buffer.
    /// </summary>
    /// <param name="fontData">The raw <c>.ttf</c>, <c>.otf</c>, or <c>.ttc</c> data.</param>
    /// <param name="fontOffset">Byte offset of the font face (0 for standalone <c>.ttf</c>/<c>.otf</c>; see <see cref="StbTrueType.GetFontOffsetForIndex"/>).</param>
    /// <param name="options">Optional safety limits.</param>
    /// <returns>The parsed <see cref="StbTrueTypeFont"/>.</returns>
    /// <exception cref="StbException">Thrown if the font data is invalid, truncated, or unsupported.</exception>
    public static StbTrueTypeFont Load(ReadOnlyMemory<byte> fontData, int fontOffset = 0, StbOptions? options = null)
    {
        if (!TryLoadInternal(fontData, fontOffset, options, out StbTrueTypeFont? font, out StbErrorCode error, out string reason))
        {
            throw new StbException(error, reason);
        }

        return font!;
    }

    /// <summary>
    /// Loads a font face by copying <paramref name="fontData"/> into a managed buffer.
    /// </summary>
    /// <param name="fontData">The raw <c>.ttf</c>, <c>.otf</c>, or <c>.ttc</c> bytes.</param>
    /// <param name="fontOffset">Byte offset of the font face (0 for standalone files).</param>
    /// <param name="options">Optional safety limits.</param>
    /// <returns>The parsed <see cref="StbTrueTypeFont"/>.</returns>
    /// <exception cref="StbException">Thrown if the font data is invalid, truncated, or unsupported.</exception>
    public static StbTrueTypeFont Load(ReadOnlySpan<byte> fontData, int fontOffset = 0, StbOptions? options = null) =>
        Load((ReadOnlyMemory<byte>)fontData.ToArray(), fontOffset, options);

    /// <summary>
    /// Loads the <paramref name="fontIndex"/>-th font face from a <c>.ttf</c>, <c>.otf</c>, or <c>.ttc</c> buffer.
    /// </summary>
    /// <param name="fontData">The raw font or collection bytes.</param>
    /// <param name="fontIndex">Zero-based font index.</param>
    /// <param name="options">Optional safety limits.</param>
    /// <returns>The parsed <see cref="StbTrueTypeFont"/>.</returns>
    /// <exception cref="StbException">Thrown if <paramref name="fontIndex"/> is out of range or the font data is invalid.</exception>
    public static StbTrueTypeFont LoadIndex(ReadOnlyMemory<byte> fontData, int fontIndex = 0, StbOptions? options = null)
    {
        int offset = TrueTypeBinary.GetFontOffsetForIndex(fontData.Span, fontIndex);
        if (offset < 0)
        {
            throw new StbException(
                StbErrorCode.InvalidData,
                $"Font index {fontIndex} is out of range or the data is not a recognized TrueType/OpenType font.");
        }

        return Load(fontData, offset, options);
    }

    /// <summary>
    /// Attempts to load a font face at the specified byte offset without throwing on malformed input.
    /// </summary>
    /// <param name="fontData">The raw font bytes.</param>
    /// <param name="fontOffset">Byte offset of the font face.</param>
    /// <param name="font">When this method returns <c>true</c>, contains the parsed font.</param>
    /// <param name="options">Optional safety limits.</param>
    /// <returns><c>true</c> if the font was loaded; otherwise <c>false</c>.</returns>
    public static bool TryLoad(
        ReadOnlyMemory<byte> fontData,
        int fontOffset,
        out StbTrueTypeFont? font,
        StbOptions? options = null) =>
        TryLoadInternal(fontData, fontOffset, options, out font, out _, out _);

    private static bool TryLoadInternal(
        ReadOnlyMemory<byte> fontData,
        int fontStart,
        StbOptions? options,
        out StbTrueTypeFont? font,
        out StbErrorCode errorCode,
        out string errorMessage)
    {
        font = null;
        errorCode = StbErrorCode.InvalidData;
        errorMessage = string.Empty;

        ReadOnlySpan<byte> data = fontData.Span;
        if (data.Length < 12 || fontStart < 0 || fontStart > data.Length - 12)
        {
            errorCode = StbErrorCode.TruncatedInput;
            errorMessage = "Font data is empty or font offset is out of range.";
            return false;
        }

        if (options is not null)
        {
            if (data.Length > options.Limits.MaxAllocationBytes)
            {
                errorCode = StbErrorCode.LimitExceeded;
                errorMessage = $"Font size ({data.Length} bytes) exceeds MaxAllocationBytes ({options.Limits.MaxAllocationBytes}).";
                return false;
            }
        }

        int cmap = TrueTypeBinary.FindTable(data, fontStart, TrueTypeBinary.TagCmap);
        int loca = TrueTypeBinary.FindTable(data, fontStart, TrueTypeBinary.TagLoca);
        int head = TrueTypeBinary.FindTable(data, fontStart, TrueTypeBinary.TagHead);
        int glyf = TrueTypeBinary.FindTable(data, fontStart, TrueTypeBinary.TagGlyf);
        int hhea = TrueTypeBinary.FindTable(data, fontStart, TrueTypeBinary.TagHhea);
        int hmtx = TrueTypeBinary.FindTable(data, fontStart, TrueTypeBinary.TagHmtx);
        int vhea = TrueTypeBinary.FindTable(data, fontStart, TrueTypeBinary.TagVhea);
        int vmtx = TrueTypeBinary.FindTable(data, fontStart, TrueTypeBinary.TagVmtx);
        int kern = TrueTypeBinary.FindTable(data, fontStart, TrueTypeBinary.TagKern);
        int gpos = TrueTypeBinary.FindTable(data, fontStart, TrueTypeBinary.TagGpos);
        int os2 = TrueTypeBinary.FindTable(data, fontStart, TrueTypeBinary.TagOs2);
        int post = TrueTypeBinary.FindTable(data, fontStart, TrueTypeBinary.TagPost);

        if (cmap == 0 || head == 0 || hhea == 0 || hmtx == 0)
        {
            errorCode = StbErrorCode.InvalidData;
            errorMessage = "Font is missing one or more required tables (cmap, head, hhea, hmtx).";
            return false;
        }

        if (!TrueTypeBinary.HasRange(data, head, 54) ||
            !TrueTypeBinary.HasRange(data, hhea, 36) ||
            !TrueTypeBinary.HasRange(data, cmap, 4))
        {
            errorCode = StbErrorCode.TruncatedInput;
            errorMessage = "Required TrueType table header is truncated.";
            return false;
        }

        CffBuffer cffBuf = CffBuffer.Empty;
        CffBuffer charstrings = CffBuffer.Empty;
        CffBuffer gsubrs = CffBuffer.Empty;
        CffBuffer subrs = CffBuffer.Empty;
        CffBuffer fontDicts = CffBuffer.Empty;
        CffBuffer fdSelect = CffBuffer.Empty;

        if (glyf != 0)
        {
            if (loca == 0)
            {
                errorCode = StbErrorCode.InvalidData;
                errorMessage = "TrueType font has a 'glyf' table but is missing 'loca'.";
                return false;
            }
        }
        else
        {
            int cffOffset = TrueTypeBinary.FindTable(data, fontStart, TrueTypeBinary.TagCff, out int cffLength);
            if (cffOffset == 0 || cffLength < 4)
            {
                errorCode = StbErrorCode.UnsupportedFormat;
                errorMessage = "Font has neither a 'glyf' table nor a valid 'CFF ' table.";
                return false;
            }

            // Use actual available bytes from cffOffset to end of table (clamped to data length).
            int usableCffLen = Math.Max(cffLength, 0);
            if (usableCffLen == 0)
            {
                usableCffLen = data.Length - cffOffset;
            }

            cffBuf = new CffBuffer(cffOffset, Math.Min(usableCffLen, data.Length - cffOffset));
            CffBuffer b = cffBuf;

            // Read CFF header.
            b.Skip(2);
            int hdrSize = b.Get8(data);
            if (hdrSize < 4 || hdrSize > b.Size)
            {
                errorCode = StbErrorCode.InvalidData;
                errorMessage = "CFF header size is out of bounds.";
                return false;
            }

            b.Seek(hdrSize);

            _ = CffBuffer.GetIndex(data, ref b); // Name INDEX
            CffBuffer topDictIdx = CffBuffer.GetIndex(data, ref b);
            CffBuffer topDict = CffBuffer.IndexGet(data, topDictIdx, 0);
            if (topDict.Size == 0)
            {
                errorCode = StbErrorCode.InvalidData;
                errorMessage = "CFF Top DICT INDEX is empty or malformed.";
                return false;
            }

            _ = CffBuffer.GetIndex(data, ref b); // String INDEX
            gsubrs = CffBuffer.GetIndex(data, ref b);

            Span<uint> tmp = stackalloc uint[1];
            uint csType = 2;
            uint charstringsOff = 0;
            uint fdArrayOff = 0;
            uint fdSelectOff = 0;

            tmp[0] = 0;
            CffBuffer.DictGetInts(data, topDict, 17, tmp);
            charstringsOff = tmp[0];

            tmp[0] = 2;
            CffBuffer.DictGetInts(data, topDict, 0x100 | 6, tmp);
            csType = tmp[0];

            tmp[0] = 0;
            CffBuffer.DictGetInts(data, topDict, 0x100 | 36, tmp);
            fdArrayOff = tmp[0];

            tmp[0] = 0;
            CffBuffer.DictGetInts(data, topDict, 0x100 | 37, tmp);
            fdSelectOff = tmp[0];

            subrs = CffBuffer.GetSubrs(data, b, topDict);

            if (csType != 2)
            {
                errorCode = StbErrorCode.UnsupportedFeature;
                errorMessage = $"Unsupported CFF charstring type {csType}; only Type 2 charstrings are supported.";
                return false;
            }

            if (charstringsOff == 0 || charstringsOff > (uint)b.Size)
            {
                errorCode = StbErrorCode.InvalidData;
                errorMessage = "CFF CharStrings offset is missing or out of bounds.";
                return false;
            }

            if (fdArrayOff != 0)
            {
                if (fdSelectOff == 0 || fdArrayOff > (uint)b.Size || fdSelectOff > (uint)b.Size)
                {
                    errorCode = StbErrorCode.InvalidData;
                    errorMessage = "CID-keyed CFF font has invalid FDArray or FDSelect offset.";
                    return false;
                }

                b.Seek((int)fdArrayOff);
                fontDicts = CffBuffer.GetIndex(data, ref b);
                fdSelect = b.Range((int)fdSelectOff, b.Size - (int)fdSelectOff);
            }

            b.Seek((int)charstringsOff);
            charstrings = CffBuffer.GetIndex(data, ref b);
            if (charstrings.Size == 0)
            {
                errorCode = StbErrorCode.InvalidData;
                errorMessage = "CFF CharStrings INDEX is empty or malformed.";
                return false;
            }
        }

        int maxp = TrueTypeBinary.FindTable(data, fontStart, TrueTypeBinary.TagMaxp);
        int numGlyphs = (maxp != 0 && TrueTypeBinary.HasRange(data, maxp, 6))
            ? TrueTypeBinary.ReadU16(data, maxp + 4)
            : 0xFFFF;

        int svgDocListOffset = 0;
        int svgTable = TrueTypeBinary.FindTable(data, fontStart, TrueTypeBinary.TagSvg);
        if (svgTable != 0 && TrueTypeBinary.HasRange(data, svgTable, 6))
        {
            uint docListRel = TrueTypeBinary.ReadU32(data, svgTable + 2);
            long docListAbs = (long)svgTable + docListRel;
            if (docListAbs > 0 && docListAbs <= data.Length - 2)
            {
                svgDocListOffset = (int)docListAbs;
            }
        }

        // Select cmap encoding subtable matching upstream stbtt_InitFont_internal.
        int numTables = TrueTypeBinary.ReadU16(data, cmap + 2);
        int maxCmapTables = (data.Length - (cmap + 4)) / 8;
        if (numTables > maxCmapTables)
        {
            numTables = maxCmapTables;
        }

        int indexMap = 0;
        int symbolMap = 0;
        for (int i = 0; i < numTables; ++i)
        {
            int encRec = cmap + 4 + 8 * i;
            ushort platformId = TrueTypeBinary.ReadU16(data, encRec);
            ushort encodingId = TrueTypeBinary.ReadU16(data, encRec + 2);
            uint subOff = TrueTypeBinary.ReadU32(data, encRec + 4);
            long target = (long)cmap + subOff;
            if (target <= 0 || target > data.Length - 2)
            {
                continue;
            }

            if (platformId == (ushort)FontPlatformId.Microsoft)
            {
                if (encodingId == (ushort)FontMicrosoftEncodingId.UnicodeBmp ||
                    encodingId == (ushort)FontMicrosoftEncodingId.UnicodeFull)
                {
                    indexMap = (int)target;
                }
                else if (encodingId == (ushort)FontMicrosoftEncodingId.Symbol)
                {
                    symbolMap = (int)target;
                }
            }
            else if (platformId == (ushort)FontPlatformId.Unicode)
            {
                indexMap = (int)target;
            }
        }

        if (indexMap == 0)
        {
            indexMap = symbolMap;
        }

        if (indexMap == 0)
        {
            errorCode = StbErrorCode.UnsupportedFeature;
            errorMessage = "Font contains no supported Unicode or Symbol 'cmap' subtable.";
            return false;
        }

        int indexToLocFormat = TrueTypeBinary.ReadU16(data, head + 50);

        font = new StbTrueTypeFont(
            fontData,
            fontStart,
            numGlyphs,
            loca,
            head,
            glyf,
            hhea,
            hmtx,
            vhea,
            vmtx,
            kern,
            gpos,
            os2,
            post,
            svgDocListOffset,
            indexMap,
            indexToLocFormat,
            cffBuf,
            charstrings,
            gsubrs,
            subrs,
            fontDicts,
            fdSelect);
        return true;
    }

    /// <summary>
    /// Maps a Unicode codepoint to its glyph index in this font, or returns <c>0</c> if the codepoint is not defined.
    /// </summary>
    /// <param name="unicodeCodepoint">The Unicode scalar value / codepoint.</param>
    /// <returns>The glyph index, or <c>0</c> for the missing-character glyph.</returns>
    public int FindGlyphIndex(int unicodeCodepoint)
    {
        if (unicodeCodepoint < 0)
        {
            return 0;
        }

        ReadOnlySpan<byte> data = _dataMemory.Span;
        int indexMap = _indexMap;
        if (!TrueTypeBinary.HasRange(data, indexMap, 2))
        {
            return 0;
        }

        ushort format = TrueTypeBinary.ReadU16(data, indexMap);

        if (format == 0)
        {
            if (!TrueTypeBinary.HasRange(data, indexMap, 6)) return 0;
            int bytes = TrueTypeBinary.ReadU16(data, indexMap + 2);
            if (unicodeCodepoint < bytes - 6 && TrueTypeBinary.HasRange(data, indexMap + 6 + unicodeCodepoint, 1))
            {
                return data[indexMap + 6 + unicodeCodepoint];
            }

            return 0;
        }

        if (format == 6)
        {
            if (!TrueTypeBinary.HasRange(data, indexMap, 10)) return 0;
            uint first = TrueTypeBinary.ReadU16(data, indexMap + 6);
            uint count = TrueTypeBinary.ReadU16(data, indexMap + 8);
            uint cp = (uint)unicodeCodepoint;
            if (cp >= first && cp < first + count)
            {
                int pos = indexMap + 10 + (int)(cp - first) * 2;
                return TrueTypeBinary.ReadU16(data, pos);
            }

            return 0;
        }

        if (format == 4)
        {
            if (unicodeCodepoint > 0xFFFF || !TrueTypeBinary.HasRange(data, indexMap, 14))
            {
                return 0;
            }

            int segCount = TrueTypeBinary.ReadU16(data, indexMap + 6) >> 1;
            int searchRange = TrueTypeBinary.ReadU16(data, indexMap + 8) >> 1;
            int entrySelector = TrueTypeBinary.ReadU16(data, indexMap + 10);
            int rangeShift = TrueTypeBinary.ReadU16(data, indexMap + 12) >> 1;

            if (segCount <= 0)
            {
                return 0;
            }

            int endCount = indexMap + 14;
            if (!TrueTypeBinary.HasRange(data, endCount, segCount * 8 + 2))
            {
                return 0;
            }

            int search = endCount;
            if (rangeShift >= 0 && rangeShift < segCount &&
                unicodeCodepoint >= TrueTypeBinary.ReadU16(data, search + rangeShift * 2))
            {
                search += rangeShift * 2;
            }

            search -= 2;
            int maxSearch = endCount + (segCount - 1) * 2;
            while (entrySelector > 0 && searchRange > 0)
            {
                searchRange >>= 1;
                int probe = search + searchRange * 2;
                if (probe >= endCount && probe <= maxSearch)
                {
                    ushort end = TrueTypeBinary.ReadU16(data, probe);
                    if (unicodeCodepoint > end)
                    {
                        search = probe;
                    }
                }

                --entrySelector;
            }

            search += 2;
            int item = (search - endCount) >> 1;
            if ((uint)item >= (uint)segCount)
            {
                return 0;
            }

            int start = TrueTypeBinary.ReadU16(data, indexMap + 14 + segCount * 2 + 2 + 2 * item);
            int last = TrueTypeBinary.ReadU16(data, endCount + 2 * item);
            if (unicodeCodepoint < start || unicodeCodepoint > last)
            {
                return 0;
            }

            int idRangeOffsetLoc = indexMap + 14 + segCount * 6 + 2 + 2 * item;
            ushort offset = TrueTypeBinary.ReadU16(data, idRangeOffsetLoc);
            short delta = TrueTypeBinary.ReadS16(data, indexMap + 14 + segCount * 4 + 2 + 2 * item);

            if (offset == 0)
            {
                return (ushort)(unicodeCodepoint + delta);
            }

            int glyphIdxLoc = idRangeOffsetLoc + offset + (unicodeCodepoint - start) * 2;
            if (!TrueTypeBinary.HasRange(data, glyphIdxLoc, 2))
            {
                return 0;
            }

            ushort index = TrueTypeBinary.ReadU16(data, glyphIdxLoc);
            if (index != 0)
            {
                index = (ushort)(index + delta);
            }

            return index;
        }

        if (format == 12 || format == 13)
        {
            if (!TrueTypeBinary.HasRange(data, indexMap, 16))
            {
                return 0;
            }

            uint ngroups = TrueTypeBinary.ReadU32(data, indexMap + 12);
            int maxGroups = (data.Length - (indexMap + 16)) / 12;
            if (ngroups > (uint)maxGroups)
            {
                ngroups = (uint)Math.Max(0, maxGroups);
            }

            int low = 0;
            int high = (int)ngroups;
            uint cp = (uint)unicodeCodepoint;
            while (low < high)
            {
                int mid = low + ((high - low) >> 1);
                int groupOff = indexMap + 16 + mid * 12;
                uint startChar = TrueTypeBinary.ReadU32(data, groupOff);
                uint endChar = TrueTypeBinary.ReadU32(data, groupOff + 4);
                if (cp < startChar)
                {
                    high = mid;
                }
                else if (cp > endChar)
                {
                    low = mid + 1;
                }
                else
                {
                    uint startGlyph = TrueTypeBinary.ReadU32(data, groupOff + 8);
                    return format == 12 ? (int)(startGlyph + cp - startChar) : (int)startGlyph;
                }
            }

            return 0;
        }

        return 0;
    }

    /// <summary>
    /// Computes the scale factor that produces a font whose ascender-to-descender height is <paramref name="pixels"/> tall.
    /// </summary>
    /// <param name="pixels">Desired pixel height.</param>
    /// <returns>The scale factor to apply to unscaled font coordinates.</returns>
    public float ScaleForPixelHeight(float pixels)
    {
        ReadOnlySpan<byte> data = _dataMemory.Span;
        int fheight = TrueTypeBinary.ReadS16(data, _hhea + 4) - TrueTypeBinary.ReadS16(data, _hhea + 6);
        return fheight != 0 ? pixels / fheight : 0.0f;
    }

    /// <summary>
    /// Computes the scale factor for a target pixel width based on <c>hhea</c> horizontal metrics.
    /// </summary>
    /// <param name="pixels">Desired pixel width.</param>
    /// <returns>The scale factor.</returns>
    public float ScaleForPixelWidth(float pixels)
    {
        ReadOnlySpan<byte> data = _dataMemory.Span;
        int fwidth = TrueTypeBinary.ReadS16(data, _hhea + 10) - TrueTypeBinary.ReadS16(data, _hhea + 12);
        return fwidth != 0 ? pixels / fwidth : 0.0f;
    }

    /// <summary>
    /// Computes the scale factor that maps the font's EM square (<c>head.unitsPerEm</c>) to <paramref name="pixels"/>.
    /// </summary>
    /// <param name="pixels">Desired EM size in pixels.</param>
    /// <returns>The scale factor.</returns>
    public float ScaleForMappingEmToPixels(float pixels)
    {
        int unitsPerEm = TrueTypeBinary.ReadU16(_dataMemory.Span, _head + 18);
        return unitsPerEm != 0 ? pixels / unitsPerEm : 0.0f;
    }

    /// <summary>Gets the font's unscaled vertical metrics from the <c>hhea</c> table.</summary>
    /// <returns>Unscaled ascent, descent, and line gap.</returns>
    public FontVMetrics GetFontVMetrics()
    {
        ReadOnlySpan<byte> data = _dataMemory.Span;
        return new FontVMetrics(
            TrueTypeBinary.ReadS16(data, _hhea + 4),
            TrueTypeBinary.ReadS16(data, _hhea + 6),
            TrueTypeBinary.ReadS16(data, _hhea + 8));
    }

    /// <summary>Gets the font's vertical layout metrics from the <c>vhea</c> table, or zeroes if <c>vhea</c> is absent.</summary>
    /// <returns>Unscaled vertical typographic ascender, descender, and line gap.</returns>
    public FontHMetrics GetFontHMetrics()
    {
        ReadOnlySpan<byte> data = _dataMemory.Span;
        if (_vhea == 0 || !TrueTypeBinary.HasRange(data, _vhea, 10))
        {
            return default;
        }

        return new FontHMetrics(
            TrueTypeBinary.ReadS16(data, _vhea + 4),
            TrueTypeBinary.ReadS16(data, _vhea + 6),
            TrueTypeBinary.ReadS16(data, _vhea + 8));
    }

    /// <summary>
    /// Gets the typographic vertical metrics from the <c>OS/2</c> table if present.
    /// </summary>
    /// <param name="metrics">Receives the typographic ascent, descent, and line gap when present.</param>
    /// <returns><c>true</c> if the <c>OS/2</c> table is present; otherwise <c>false</c>.</returns>
    public bool TryGetFontVMetricsOS2(out FontVMetrics metrics)
    {
        ReadOnlySpan<byte> data = _dataMemory.Span;
        if (_os2 == 0 || !TrueTypeBinary.HasRange(data, _os2, 74))
        {
            metrics = default;
            return false;
        }

        metrics = new FontVMetrics(
            TrueTypeBinary.ReadS16(data, _os2 + 68),
            TrueTypeBinary.ReadS16(data, _os2 + 70),
            TrueTypeBinary.ReadS16(data, _os2 + 72));
        return true;
    }

    /// <summary>Gets the bounding box encompassing all glyphs in the font from the <c>head</c> table.</summary>
    /// <returns>The font bounding box in unscaled font coordinates.</returns>
    public GlyphBox GetFontBoundingBox()
    {
        ReadOnlySpan<byte> data = _dataMemory.Span;
        return new GlyphBox(
            TrueTypeBinary.ReadS16(data, _head + 36),
            TrueTypeBinary.ReadS16(data, _head + 38),
            TrueTypeBinary.ReadS16(data, _head + 40),
            TrueTypeBinary.ReadS16(data, _head + 42));
    }

    /// <summary>Gets the underline position and thickness from the <c>post</c> table if present.</summary>
    /// <param name="metrics">Receives underline position and thickness.</param>
    /// <returns><c>true</c> if the <c>post</c> table is present; otherwise <c>false</c>.</returns>
    public bool TryGetFontUnderlineMetrics(out FontUnderlineMetrics metrics)
    {
        ReadOnlySpan<byte> data = _dataMemory.Span;
        if (_post == 0 || !TrueTypeBinary.HasRange(data, _post, 12))
        {
            metrics = default;
            return false;
        }

        metrics = new FontUnderlineMetrics(
            TrueTypeBinary.ReadS16(data, _post + 8),
            TrueTypeBinary.ReadS16(data, _post + 10));
        return true;
    }

    /// <summary>Gets unscaled horizontal advance width and left side bearing for a glyph index.</summary>
    /// <param name="glyphIndex">The glyph index.</param>
    /// <returns>Unscaled advance width and left side bearing.</returns>
    public GlyphHMetrics GetGlyphHMetrics(int glyphIndex)
    {
        if (glyphIndex < 0)
        {
            return default;
        }

        ReadOnlySpan<byte> data = _dataMemory.Span;
        ushort numOfLongHorMetrics = TrueTypeBinary.ReadU16(data, _hhea + 34);
        if (numOfLongHorMetrics == 0)
        {
            return default;
        }

        if (glyphIndex < numOfLongHorMetrics)
        {
            int pos = _hmtx + 4 * glyphIndex;
            return new GlyphHMetrics(
                TrueTypeBinary.ReadS16(data, pos),
                TrueTypeBinary.ReadS16(data, pos + 2));
        }

        int advPos = _hmtx + 4 * (numOfLongHorMetrics - 1);
        int lsbPos = _hmtx + 4 * numOfLongHorMetrics + 2 * (glyphIndex - numOfLongHorMetrics);
        return new GlyphHMetrics(
            TrueTypeBinary.ReadS16(data, advPos),
            TrueTypeBinary.ReadS16(data, lsbPos));
    }

    /// <summary>Gets unscaled horizontal advance width and left side bearing for a Unicode codepoint.</summary>
    /// <param name="codepoint">The Unicode codepoint.</param>
    /// <returns>Unscaled advance width and left side bearing.</returns>
    public GlyphHMetrics GetCodepointHMetrics(int codepoint) =>
        GetGlyphHMetrics(FindGlyphIndex(codepoint));

    /// <summary>Gets unscaled vertical advance height and top side bearing for a glyph index from <c>vmtx</c>.</summary>
    /// <param name="glyphIndex">The glyph index.</param>
    /// <returns>Unscaled advance height and top side bearing, or zeroes if <c>vmtx</c> is absent.</returns>
    public GlyphVMetrics GetGlyphVMetrics(int glyphIndex)
    {
        if (_vmtx == 0 || glyphIndex < 0)
        {
            return default;
        }

        ReadOnlySpan<byte> data = _dataMemory.Span;
        int pos = _vmtx + 4 * glyphIndex;
        return new GlyphVMetrics(
            TrueTypeBinary.ReadS16(data, pos),
            TrueTypeBinary.ReadS16(data, pos + 2));
    }

    /// <summary>Gets unscaled vertical advance height and top side bearing for a Unicode codepoint.</summary>
    /// <param name="codepoint">The Unicode codepoint.</param>
    /// <returns>Unscaled vertical metrics.</returns>
    public GlyphVMetrics GetCodepointVMetrics(int codepoint) =>
        GetGlyphVMetrics(FindGlyphIndex(codepoint));

    /// <summary>Gets the unscaled horizontal kerning adjustment between two glyph indices using <c>GPOS</c> and <c>kern</c>.</summary>
    /// <param name="glyph1">First glyph index.</param>
    /// <param name="glyph2">Second glyph index.</param>
    /// <returns>Additional horizontal advance in unscaled font units.</returns>
    public int GetGlyphKernAdvance(int glyph1, int glyph2)
    {
        ReadOnlySpan<byte> data = _dataMemory.Span;
        int xAdvance = 0;

        if (_gpos != 0)
        {
            xAdvance = TrueTypeKerning.GetGlyphGposInfoAdvance(data, _gpos, glyph1, glyph2);
        }

        if (_kern != 0 && xAdvance == 0)
        {
            xAdvance = TrueTypeKerning.GetGlyphKernInfoAdvance(data, _kern, glyph1, glyph2);
        }

        return xAdvance;
    }

    /// <summary>Gets the unscaled horizontal kerning adjustment between two Unicode codepoints.</summary>
    /// <param name="codepoint1">First Unicode codepoint.</param>
    /// <param name="codepoint2">Second Unicode codepoint.</param>
    /// <returns>Additional horizontal advance in unscaled font units.</returns>
    public int GetCodepointKernAdvance(int codepoint1, int codepoint2)
    {
        if (_kern == 0 && _gpos == 0)
        {
            return 0;
        }

        return GetGlyphKernAdvance(FindGlyphIndex(codepoint1), FindGlyphIndex(codepoint2));
    }

    /// <summary>Gets the number of entries in the font's legacy horizontal format-0 <c>kern</c> table.</summary>
    /// <returns>The number of kerning pairs, or <c>0</c> if none are present.</returns>
    public int GetKerningTableLength() =>
        TrueTypeKerning.GetKerningTableLength(_dataMemory.Span, _kern);

    /// <summary>Copies kerning pairs from the legacy <c>kern</c> table into <paramref name="destination"/>.</summary>
    /// <param name="destination">Destination span for kerning entries.</param>
    /// <returns>The number of entries written.</returns>
    public int GetKerningTable(Span<KerningEntry> destination) =>
        TrueTypeKerning.GetKerningTable(_dataMemory.Span, _kern, destination);

    /// <summary>Returns all kerning pairs from the legacy <c>kern</c> table.</summary>
    /// <returns>An array of kerning entries (empty if no format-0 <c>kern</c> table is present).</returns>
    public KerningEntry[] GetKerningTable()
    {
        int len = GetKerningTableLength();
        if (len <= 0)
        {
            return [];
        }

        KerningEntry[] table = new KerningEntry[len];
        int written = GetKerningTable(table);
        if (written != len)
        {
            Array.Resize(ref table, written);
        }

        return table;
    }

    /// <summary>Gets the unscaled bounding box of a glyph's visible outline.</summary>
    /// <param name="glyphIndex">The glyph index.</param>
    /// <param name="box">Receives the bounding box when the glyph is non-empty.</param>
    /// <returns><c>true</c> if the glyph has a non-empty outline; otherwise <c>false</c>.</returns>
    public bool TryGetGlyphBox(int glyphIndex, out GlyphBox box)
    {
        if (_cff.Size != 0)
        {
            var ctx = new CffCharstring.CharstringContext { BoundsOnly = true };
            bool ok = CffCharstring.RunCharstring(
                _dataMemory.Span, _cff, _charstrings, _gsubrs, _subrs, _fontDicts, _fdSelect, glyphIndex, ref ctx);
            if (!ok || ctx.NumVertices == 0)
            {
                box = default;
                return false;
            }

            box = new GlyphBox(ctx.MinX, ctx.MinY, ctx.MaxX, ctx.MaxY);
            return true;
        }

        int g = GetGlyfOffset(glyphIndex);
        if (g < 0 || !TrueTypeBinary.HasRange(_dataMemory.Span, g, 10))
        {
            box = default;
            return false;
        }

        ReadOnlySpan<byte> data = _dataMemory.Span;
        box = new GlyphBox(
            TrueTypeBinary.ReadS16(data, g + 2),
            TrueTypeBinary.ReadS16(data, g + 4),
            TrueTypeBinary.ReadS16(data, g + 6),
            TrueTypeBinary.ReadS16(data, g + 8));
        return true;
    }

    /// <summary>Gets the unscaled bounding box of a Unicode codepoint's glyph.</summary>
    /// <param name="codepoint">The Unicode codepoint.</param>
    /// <param name="box">Receives the bounding box when the glyph is non-empty.</param>
    /// <returns><c>true</c> if the glyph has a non-empty outline; otherwise <c>false</c>.</returns>
    public bool TryGetCodepointBox(int codepoint, out GlyphBox box) =>
        TryGetGlyphBox(FindGlyphIndex(codepoint), out box);

    /// <summary>Determines whether nothing is drawn for the specified glyph index.</summary>
    /// <param name="glyphIndex">The glyph index.</param>
    /// <returns><c>true</c> if the glyph is empty; otherwise <c>false</c>.</returns>
    public bool IsGlyphEmpty(int glyphIndex)
    {
        if (_cff.Size != 0)
        {
            var ctx = new CffCharstring.CharstringContext { BoundsOnly = true };
            bool ok = CffCharstring.RunCharstring(
                _dataMemory.Span, _cff, _charstrings, _gsubrs, _subrs, _fontDicts, _fdSelect, glyphIndex, ref ctx);
            return !ok || ctx.NumVertices == 0;
        }

        int g = GetGlyfOffset(glyphIndex);
        if (g < 0 || !TrueTypeBinary.HasRange(_dataMemory.Span, g, 2))
        {
            return true;
        }

        return TrueTypeBinary.ReadS16(_dataMemory.Span, g) == 0;
    }

    /// <summary>Extracts the outline vertices of a glyph in unscaled font coordinates.</summary>
    /// <param name="glyphIndex">The glyph index.</param>
    /// <returns>An array of outline vertices (empty for empty glyphs).</returns>
    public GlyphVertex[] GetGlyphShape(int glyphIndex)
    {
        RasterizerScratch scratch = RasterizerScratch.Get();
        scratch.ResetVertices();
        AppendGlyphShape(glyphIndex, scratch, depth: 0);
        if (scratch.VertexCount == 0)
        {
            return [];
        }

        GlyphVertex[] result = new GlyphVertex[scratch.VertexCount];
        scratch.Vertices.AsSpan(0, scratch.VertexCount).CopyTo(result);
        return result;
    }

    /// <summary>Extracts the outline vertices of a Unicode codepoint in unscaled font coordinates.</summary>
    /// <param name="codepoint">The Unicode codepoint.</param>
    /// <returns>An array of outline vertices (empty for empty glyphs).</returns>
    public GlyphVertex[] GetCodepointShape(int codepoint) =>
        GetGlyphShape(FindGlyphIndex(codepoint));

    /// <summary>
    /// Copies the outline vertices of a glyph into <paramref name="destination"/> without allocating.
    /// </summary>
    /// <param name="glyphIndex">The glyph index.</param>
    /// <param name="destination">Destination span for vertices.</param>
    /// <param name="vertexCount">Receives the total number of vertices in the glyph outline.</param>
    /// <returns><c>true</c> if <paramref name="destination"/> was large enough and all vertices were copied; otherwise <c>false</c>.</returns>
    public bool TryGetGlyphShape(int glyphIndex, Span<GlyphVertex> destination, out int vertexCount)
    {
        RasterizerScratch scratch = RasterizerScratch.Get();
        scratch.ResetVertices();
        AppendGlyphShape(glyphIndex, scratch, depth: 0);
        vertexCount = scratch.VertexCount;
        if (vertexCount > destination.Length)
        {
            return false;
        }

        scratch.Vertices.AsSpan(0, vertexCount).CopyTo(destination);
        return true;
    }

    /// <summary>Gets the raw SVG document bytes for a glyph from the OpenType <c>SVG </c> table, if present.</summary>
    /// <param name="glyphIndex">The glyph index.</param>
    /// <param name="svgData">Receives a read-only span over the glyph's SVG document.</param>
    /// <returns><c>true</c> if an SVG document was found; otherwise <c>false</c>.</returns>
    public bool TryGetGlyphSvg(int glyphIndex, out ReadOnlySpan<byte> svgData)
    {
        svgData = default;
        if (_svgDocListOffset == 0 || glyphIndex < 0)
        {
            return false;
        }

        ReadOnlySpan<byte> data = _dataMemory.Span;
        int numEntries = TrueTypeBinary.ReadU16(data, _svgDocListOffset);
        int docsOff = _svgDocListOffset + 2;
        if (!TrueTypeBinary.HasRange(data, docsOff, numEntries * 12))
        {
            return false;
        }

        for (int i = 0; i < numEntries; ++i)
        {
            int doc = docsOff + i * 12;
            int startGlyph = TrueTypeBinary.ReadU16(data, doc);
            int endGlyph = TrueTypeBinary.ReadU16(data, doc + 2);
            if (glyphIndex >= startGlyph && glyphIndex <= endGlyph)
            {
                uint relOff = TrueTypeBinary.ReadU32(data, doc + 4);
                uint len = TrueTypeBinary.ReadU32(data, doc + 8);
                long absOff = (long)_svgDocListOffset + relOff;
                if (absOff >= 0 && len <= int.MaxValue && TrueTypeBinary.HasRange(data, (int)absOff, (int)len))
                {
                    svgData = data.Slice((int)absOff, (int)len);
                    return true;
                }

                return false;
            }
        }

        return false;
    }

    /// <summary>Gets the raw SVG document bytes for a Unicode codepoint from the OpenType <c>SVG </c> table, if present.</summary>
    /// <param name="codepoint">The Unicode codepoint.</param>
    /// <param name="svgData">Receives a read-only span over the glyph's SVG document.</param>
    /// <returns><c>true</c> if an SVG document was found; otherwise <c>false</c>.</returns>
    public bool TryGetCodepointSvg(int codepoint, out ReadOnlySpan<byte> svgData) =>
        TryGetGlyphSvg(FindGlyphIndex(codepoint), out svgData);

    /// <summary>
    /// Computes the integral pixel bounding box of a rasterized glyph bitmap with an optional subpixel shift.
    /// </summary>
    /// <param name="glyphIndex">The glyph index.</param>
    /// <param name="scaleX">Horizontal scale factor.</param>
    /// <param name="scaleY">Vertical scale factor.</param>
    /// <param name="shiftX">Subpixel horizontal shift.</param>
    /// <param name="shiftY">Subpixel vertical shift.</param>
    /// <returns>The integral bitmap box (y increases downward).</returns>
    public GlyphBitmapBox GetGlyphBitmapBoxSubpixel(
        int glyphIndex,
        float scaleX,
        float scaleY,
        float shiftX,
        float shiftY)
    {
        if (!TryGetGlyphBox(glyphIndex, out GlyphBox b))
        {
            return default;
        }

        return new GlyphBitmapBox(
            TrueTypeBinary.IFloor(b.X0 * scaleX + shiftX),
            TrueTypeBinary.IFloor(-b.Y1 * scaleY + shiftY),
            TrueTypeBinary.ICeil(b.X1 * scaleX + shiftX),
            TrueTypeBinary.ICeil(-b.Y0 * scaleY + shiftY));
    }

    /// <summary>Computes the integral pixel bounding box of a rasterized glyph bitmap.</summary>
    /// <param name="glyphIndex">The glyph index.</param>
    /// <param name="scaleX">Horizontal scale factor.</param>
    /// <param name="scaleY">Vertical scale factor.</param>
    /// <returns>The integral bitmap box (y increases downward).</returns>
    public GlyphBitmapBox GetGlyphBitmapBox(int glyphIndex, float scaleX, float scaleY) =>
        GetGlyphBitmapBoxSubpixel(glyphIndex, scaleX, scaleY, 0.0f, 0.0f);

    /// <summary>Computes the integral pixel bounding box for a Unicode codepoint with an optional subpixel shift.</summary>
    /// <param name="codepoint">The Unicode codepoint.</param>
    /// <param name="scaleX">Horizontal scale factor.</param>
    /// <param name="scaleY">Vertical scale factor.</param>
    /// <param name="shiftX">Subpixel horizontal shift.</param>
    /// <param name="shiftY">Subpixel vertical shift.</param>
    /// <returns>The integral bitmap box (y increases downward).</returns>
    public GlyphBitmapBox GetCodepointBitmapBoxSubpixel(
        int codepoint,
        float scaleX,
        float scaleY,
        float shiftX,
        float shiftY) =>
        GetGlyphBitmapBoxSubpixel(FindGlyphIndex(codepoint), scaleX, scaleY, shiftX, shiftY);

    /// <summary>Computes the integral pixel bounding box for a Unicode codepoint.</summary>
    /// <param name="codepoint">The Unicode codepoint.</param>
    /// <param name="scaleX">Horizontal scale factor.</param>
    /// <param name="scaleY">Vertical scale factor.</param>
    /// <returns>The integral bitmap box (y increases downward).</returns>
    public GlyphBitmapBox GetCodepointBitmapBox(int codepoint, float scaleX, float scaleY) =>
        GetGlyphBitmapBoxSubpixel(FindGlyphIndex(codepoint), scaleX, scaleY, 0.0f, 0.0f);

    /// <summary>
    /// Allocates and renders an antialiased 8-bit coverage bitmap for a glyph with subpixel positioning.
    /// </summary>
    /// <param name="scaleX">Horizontal scale factor (0 copies <paramref name="scaleY"/>).</param>
    /// <param name="scaleY">Vertical scale factor (0 copies <paramref name="scaleX"/>).</param>
    /// <param name="shiftX">Subpixel horizontal shift.</param>
    /// <param name="shiftY">Subpixel vertical shift.</param>
    /// <param name="glyphIndex">The glyph index.</param>
    /// <param name="metrics">Receives the bitmap width, height, and origin offsets.</param>
    /// <param name="options">Optional safety limits.</param>
    /// <returns>An 8-bit coverage bitmap (<c>Width * Height</c> bytes), or an empty array for empty glyphs or zero scale.</returns>
    public byte[] GetGlyphBitmapSubpixel(
        float scaleX,
        float scaleY,
        float shiftX,
        float shiftY,
        int glyphIndex,
        out GlyphBitmapMetrics metrics,
        StbOptions? options = null)
    {
        if (scaleX == 0.0f) scaleX = scaleY;
        if (scaleY == 0.0f)
        {
            if (scaleX == 0.0f)
            {
                metrics = default;
                return [];
            }

            scaleY = scaleX;
        }

        GlyphBitmapBox box = GetGlyphBitmapBoxSubpixel(glyphIndex, scaleX, scaleY, shiftX, shiftY);
        int w = box.Width;
        int h = box.Height;
        metrics = new GlyphBitmapMetrics(w, h, box.X0, box.Y0);
        if (w <= 0 || h <= 0)
        {
            return [];
        }

        StbLimits limits = (options ?? StbOptions.Default).Limits;
        int totalBytes = limits.ValidateDimensions(w, h, 1);

        RasterizerScratch scratch = RasterizerScratch.Get();
        scratch.ResetVertices();
        AppendGlyphShape(glyphIndex, scratch, depth: 0);

        byte[] pixels = new byte[totalBytes];
        TrueTypeRasterizer.Rasterize(
            pixels,
            w,
            h,
            w,
            0.35f,
            scratch.Vertices.AsSpan(0, scratch.VertexCount),
            scaleX,
            scaleY,
            shiftX,
            shiftY,
            box.X0,
            box.Y0,
            invert: true,
            scratch);
        return pixels;
    }

    /// <summary>Allocates and renders an antialiased 8-bit coverage bitmap for a glyph.</summary>
    /// <param name="scaleX">Horizontal scale factor.</param>
    /// <param name="scaleY">Vertical scale factor.</param>
    /// <param name="glyphIndex">The glyph index.</param>
    /// <param name="metrics">Receives the bitmap width, height, and origin offsets.</param>
    /// <param name="options">Optional safety limits.</param>
    /// <returns>An 8-bit coverage bitmap, or an empty array for empty glyphs.</returns>
    public byte[] GetGlyphBitmap(
        float scaleX,
        float scaleY,
        int glyphIndex,
        out GlyphBitmapMetrics metrics,
        StbOptions? options = null) =>
        GetGlyphBitmapSubpixel(scaleX, scaleY, 0.0f, 0.0f, glyphIndex, out metrics, options);

    /// <summary>Allocates and renders an antialiased 8-bit coverage bitmap for a Unicode codepoint with subpixel shift.</summary>
    /// <param name="scaleX">Horizontal scale factor.</param>
    /// <param name="scaleY">Vertical scale factor.</param>
    /// <param name="shiftX">Subpixel horizontal shift.</param>
    /// <param name="shiftY">Subpixel vertical shift.</param>
    /// <param name="codepoint">The Unicode codepoint.</param>
    /// <param name="metrics">Receives the bitmap width, height, and origin offsets.</param>
    /// <param name="options">Optional safety limits.</param>
    /// <returns>An 8-bit coverage bitmap, or an empty array for empty glyphs.</returns>
    public byte[] GetCodepointBitmapSubpixel(
        float scaleX,
        float scaleY,
        float shiftX,
        float shiftY,
        int codepoint,
        out GlyphBitmapMetrics metrics,
        StbOptions? options = null) =>
        GetGlyphBitmapSubpixel(scaleX, scaleY, shiftX, shiftY, FindGlyphIndex(codepoint), out metrics, options);

    /// <summary>Allocates and renders an antialiased 8-bit coverage bitmap for a Unicode codepoint.</summary>
    /// <param name="scaleX">Horizontal scale factor.</param>
    /// <param name="scaleY">Vertical scale factor.</param>
    /// <param name="codepoint">The Unicode codepoint.</param>
    /// <param name="metrics">Receives the bitmap width, height, and origin offsets.</param>
    /// <param name="options">Optional safety limits.</param>
    /// <returns>An 8-bit coverage bitmap, or an empty array for empty glyphs.</returns>
    public byte[] GetCodepointBitmap(
        float scaleX,
        float scaleY,
        int codepoint,
        out GlyphBitmapMetrics metrics,
        StbOptions? options = null) =>
        GetGlyphBitmapSubpixel(scaleX, scaleY, 0.0f, 0.0f, FindGlyphIndex(codepoint), out metrics, options);

    /// <summary>
    /// Renders a glyph directly into caller-provided memory with subpixel positioning and zero heap allocations on steady-state calls.
    /// </summary>
    /// <param name="output">Destination 8-bit pixel buffer.</param>
    /// <param name="outWidth">Target rectangle width in pixels.</param>
    /// <param name="outHeight">Target rectangle height in pixels.</param>
    /// <param name="outStride">Row stride in bytes.</param>
    /// <param name="scaleX">Horizontal scale factor.</param>
    /// <param name="scaleY">Vertical scale factor.</param>
    /// <param name="shiftX">Subpixel horizontal shift.</param>
    /// <param name="shiftY">Subpixel vertical shift.</param>
    /// <param name="glyphIndex">The glyph index.</param>
    public void MakeGlyphBitmapSubpixel(
        Span<byte> output,
        int outWidth,
        int outHeight,
        int outStride,
        float scaleX,
        float scaleY,
        float shiftX,
        float shiftY,
        int glyphIndex)
    {
        if (outWidth <= 0 || outHeight <= 0)
        {
            return;
        }

        ValidateOutputBuffer(output, outWidth, outHeight, outStride);

        GlyphBitmapBox box = GetGlyphBitmapBoxSubpixel(glyphIndex, scaleX, scaleY, shiftX, shiftY);
        MakeGlyphBitmapSubpixelWithOrigin(output, outWidth, outHeight, outStride, scaleX, scaleY, shiftX, shiftY, glyphIndex, box.X0, box.Y0);
    }

    internal void MakeGlyphBitmapSubpixelWithOrigin(
        Span<byte> output,
        int outWidth,
        int outHeight,
        int outStride,
        float scaleX,
        float scaleY,
        float shiftX,
        float shiftY,
        int glyphIndex,
        int originX,
        int originY)
    {
        RasterizerScratch scratch = RasterizerScratch.Get();
        scratch.ResetVertices();
        AppendGlyphShape(glyphIndex, scratch, depth: 0);

        TrueTypeRasterizer.Rasterize(
            output,
            outWidth,
            outHeight,
            outStride,
            0.35f,
            scratch.Vertices.AsSpan(0, scratch.VertexCount),
            scaleX,
            scaleY,
            shiftX,
            shiftY,
            originX,
            originY,
            invert: true,
            scratch);
    }

    /// <summary>Renders a glyph directly into caller-provided memory.</summary>
    /// <param name="output">Destination 8-bit pixel buffer.</param>
    /// <param name="outWidth">Target rectangle width in pixels.</param>
    /// <param name="outHeight">Target rectangle height in pixels.</param>
    /// <param name="outStride">Row stride in bytes.</param>
    /// <param name="scaleX">Horizontal scale factor.</param>
    /// <param name="scaleY">Vertical scale factor.</param>
    /// <param name="glyphIndex">The glyph index.</param>
    public void MakeGlyphBitmap(
        Span<byte> output,
        int outWidth,
        int outHeight,
        int outStride,
        float scaleX,
        float scaleY,
        int glyphIndex) =>
        MakeGlyphBitmapSubpixel(output, outWidth, outHeight, outStride, scaleX, scaleY, 0.0f, 0.0f, glyphIndex);

    /// <summary>
    /// Renders an oversampled glyph directly into caller-provided memory and applies horizontal/vertical box prefiltering.
    /// </summary>
    /// <param name="output">Destination 8-bit pixel buffer (caller should zero the rightmost <c>oversampleX - 1</c> columns and bottom <c>oversampleY - 1</c> rows).</param>
    /// <param name="outWidth">Total destination width including prefilter padding.</param>
    /// <param name="outHeight">Total destination height including prefilter padding.</param>
    /// <param name="outStride">Row stride in bytes.</param>
    /// <param name="scaleX">Horizontal scale factor (already multiplied by <paramref name="oversampleX"/>).</param>
    /// <param name="scaleY">Vertical scale factor (already multiplied by <paramref name="oversampleY"/>).</param>
    /// <param name="shiftX">Subpixel horizontal shift.</param>
    /// <param name="shiftY">Subpixel vertical shift.</param>
    /// <param name="oversampleX">Horizontal oversampling factor in [1, 8].</param>
    /// <param name="oversampleY">Vertical oversampling factor in [1, 8].</param>
    /// <param name="subX">Receives the horizontal subpixel compensation shift.</param>
    /// <param name="subY">Receives the vertical subpixel compensation shift.</param>
    /// <param name="glyphIndex">The glyph index.</param>
    public void MakeGlyphBitmapSubpixelPrefilter(
        Span<byte> output,
        int outWidth,
        int outHeight,
        int outStride,
        float scaleX,
        float scaleY,
        float shiftX,
        float shiftY,
        int oversampleX,
        int oversampleY,
        out float subX,
        out float subY,
        int glyphIndex)
    {
        if (oversampleX < 1 || oversampleX > TrueTypeRasterizer.MaxOversample)
        {
            throw new ArgumentOutOfRangeException(nameof(oversampleX), oversampleX, "Oversampling factor must be in [1, 8].");
        }

        if (oversampleY < 1 || oversampleY > TrueTypeRasterizer.MaxOversample)
        {
            throw new ArgumentOutOfRangeException(nameof(oversampleY), oversampleY, "Oversampling factor must be in [1, 8].");
        }

        if (outWidth > 0 && outHeight > 0)
        {
            ValidateOutputBuffer(output, outWidth, outHeight, outStride);

            int renderW = outWidth - (oversampleX - 1);
            int renderH = outHeight - (oversampleY - 1);
            if (renderW > 0 && renderH > 0)
            {
                MakeGlyphBitmapSubpixel(output, renderW, renderH, outStride, scaleX, scaleY, shiftX, shiftY, glyphIndex);
            }

            if (oversampleX > 1)
            {
                TrueTypeRasterizer.HPrefilter(output, outWidth, outHeight, outStride, oversampleX);
            }

            if (oversampleY > 1)
            {
                TrueTypeRasterizer.VPrefilter(output, outWidth, outHeight, outStride, oversampleY, RasterizerScratch.Get());
            }
        }

        subX = TrueTypeRasterizer.OversampleShift(oversampleX);
        subY = TrueTypeRasterizer.OversampleShift(oversampleY);
    }

    /// <summary>Renders a Unicode codepoint directly into caller-provided memory with subpixel positioning.</summary>
    /// <param name="output">Destination 8-bit pixel buffer.</param>
    /// <param name="outWidth">Target rectangle width in pixels.</param>
    /// <param name="outHeight">Target rectangle height in pixels.</param>
    /// <param name="outStride">Row stride in bytes.</param>
    /// <param name="scaleX">Horizontal scale factor.</param>
    /// <param name="scaleY">Vertical scale factor.</param>
    /// <param name="shiftX">Subpixel horizontal shift.</param>
    /// <param name="shiftY">Subpixel vertical shift.</param>
    /// <param name="codepoint">The Unicode codepoint.</param>
    public void MakeCodepointBitmapSubpixel(
        Span<byte> output,
        int outWidth,
        int outHeight,
        int outStride,
        float scaleX,
        float scaleY,
        float shiftX,
        float shiftY,
        int codepoint) =>
        MakeGlyphBitmapSubpixel(output, outWidth, outHeight, outStride, scaleX, scaleY, shiftX, shiftY, FindGlyphIndex(codepoint));

    /// <summary>Renders a Unicode codepoint directly into caller-provided memory.</summary>
    /// <param name="output">Destination 8-bit pixel buffer.</param>
    /// <param name="outWidth">Target rectangle width in pixels.</param>
    /// <param name="outHeight">Target rectangle height in pixels.</param>
    /// <param name="outStride">Row stride in bytes.</param>
    /// <param name="scaleX">Horizontal scale factor.</param>
    /// <param name="scaleY">Vertical scale factor.</param>
    /// <param name="codepoint">The Unicode codepoint.</param>
    public void MakeCodepointBitmap(
        Span<byte> output,
        int outWidth,
        int outHeight,
        int outStride,
        float scaleX,
        float scaleY,
        int codepoint) =>
        MakeGlyphBitmapSubpixel(output, outWidth, outHeight, outStride, scaleX, scaleY, 0.0f, 0.0f, FindGlyphIndex(codepoint));

    /// <summary>Renders an oversampled Unicode codepoint with box prefiltering directly into caller-provided memory.</summary>
    /// <param name="output">Destination 8-bit pixel buffer.</param>
    /// <param name="outWidth">Total destination width including prefilter padding.</param>
    /// <param name="outHeight">Total destination height including prefilter padding.</param>
    /// <param name="outStride">Row stride in bytes.</param>
    /// <param name="scaleX">Horizontal scale factor.</param>
    /// <param name="scaleY">Vertical scale factor.</param>
    /// <param name="shiftX">Subpixel horizontal shift.</param>
    /// <param name="shiftY">Subpixel vertical shift.</param>
    /// <param name="oversampleX">Horizontal oversampling factor in [1, 8].</param>
    /// <param name="oversampleY">Vertical oversampling factor in [1, 8].</param>
    /// <param name="subX">Receives the horizontal subpixel compensation shift.</param>
    /// <param name="subY">Receives the vertical subpixel compensation shift.</param>
    /// <param name="codepoint">The Unicode codepoint.</param>
    public void MakeCodepointBitmapSubpixelPrefilter(
        Span<byte> output,
        int outWidth,
        int outHeight,
        int outStride,
        float scaleX,
        float scaleY,
        float shiftX,
        float shiftY,
        int oversampleX,
        int oversampleY,
        out float subX,
        out float subY,
        int codepoint) =>
        MakeGlyphBitmapSubpixelPrefilter(
            output, outWidth, outHeight, outStride, scaleX, scaleY, shiftX, shiftY,
            oversampleX, oversampleY, out subX, out subY, FindGlyphIndex(codepoint));

    /// <summary>
    /// Computes an analytic 8-bit Signed Distance Field (SDF) bitmap for a glyph.
    /// </summary>
    /// <param name="scale">Uniform scale factor controlling glyph size.</param>
    /// <param name="glyphIndex">The glyph index.</param>
    /// <param name="padding">Extra border pixels around the glyph bounding box.</param>
    /// <param name="onEdgeValue">Byte value (0..255) representing the exact glyph contour.</param>
    /// <param name="pixelDistScale">SDF value change per SDF pixel of distance from the contour.</param>
    /// <param name="metrics">Receives the SDF bitmap width, height, and origin offsets.</param>
    /// <param name="options">Optional safety limits and parallelism settings.</param>
    /// <returns>The SDF pixel buffer (<c>Width * Height</c> bytes), or an empty array for empty glyphs or zero scale.</returns>
    public byte[] GetGlyphSdf(
        float scale,
        int glyphIndex,
        int padding,
        byte onEdgeValue,
        float pixelDistScale,
        out GlyphBitmapMetrics metrics,
        StbOptions? options = null)
    {
        if (scale == 0.0f)
        {
            metrics = default;
            return [];
        }

        Guard.ThrowIfNegative(padding, nameof(padding));

        GlyphBitmapBox box = GetGlyphBitmapBoxSubpixel(glyphIndex, scale, scale, 0.0f, 0.0f);
        if (box.X0 == box.X1 || box.Y0 == box.Y1)
        {
            metrics = default;
            return [];
        }

        int ix0 = box.X0 - padding;
        int iy0 = box.Y0 - padding;
        int ix1 = box.X1 + padding;
        int iy1 = box.Y1 + padding;
        int w = ix1 - ix0;
        int h = iy1 - iy0;

        metrics = new GlyphBitmapMetrics(w, h, ix0, iy0);

        StbLimits limits = (options ?? StbOptions.Default).Limits;
        int totalBytes = limits.ValidateDimensions(w, h, 1);

        RasterizerScratch scratch = RasterizerScratch.Get();
        scratch.ResetVertices();
        AppendGlyphShape(glyphIndex, scratch, depth: 0);

        byte[] data = ArrayAllocation.AllocateUninitialized<byte>(totalBytes);
        TrueTypeSdf.RenderSdf(
            data,
            0,
            w,
            h,
            w,
            ix0,
            iy0,
            scale,
            onEdgeValue,
            pixelDistScale,
            scratch.Vertices.AsSpan(0, scratch.VertexCount),
            options);
        return data;
    }

    /// <summary>Computes an analytic 8-bit Signed Distance Field (SDF) bitmap for a Unicode codepoint.</summary>
    /// <param name="scale">Uniform scale factor controlling glyph size.</param>
    /// <param name="codepoint">The Unicode codepoint.</param>
    /// <param name="padding">Extra border pixels around the glyph bounding box.</param>
    /// <param name="onEdgeValue">Byte value representing the exact glyph contour.</param>
    /// <param name="pixelDistScale">SDF value change per SDF pixel of distance from the contour.</param>
    /// <param name="metrics">Receives the SDF bitmap width, height, and origin offsets.</param>
    /// <param name="options">Optional safety limits and parallelism settings.</param>
    /// <returns>The SDF pixel buffer, or an empty array for empty glyphs.</returns>
    public byte[] GetCodepointSdf(
        float scale,
        int codepoint,
        int padding,
        byte onEdgeValue,
        float pixelDistScale,
        out GlyphBitmapMetrics metrics,
        StbOptions? options = null) =>
        GetGlyphSdf(scale, FindGlyphIndex(codepoint), padding, onEdgeValue, pixelDistScale, out metrics, options);

    /// <summary>
    /// Looks up a raw string entry from the font's <c>name</c> table matching the specified identifiers.
    /// Note that Unicode entries are encoded in big-endian UTF-16.
    /// </summary>
    /// <param name="platformId">Platform identifier (see <see cref="FontPlatformId"/>).</param>
    /// <param name="encodingId">Platform-specific encoding identifier.</param>
    /// <param name="languageId">Language identifier.</param>
    /// <param name="nameId">Name identifier (e.g. 1 = Family, 2 = Subfamily, 4 = Full Name).</param>
    /// <param name="rawBytes">Receives the raw string bytes from the font table.</param>
    /// <returns><c>true</c> if a matching entry was found; otherwise <c>false</c>.</returns>
    public bool TryGetFontNameString(
        int platformId,
        int encodingId,
        int languageId,
        int nameId,
        out ReadOnlySpan<byte> rawBytes) =>
        TrueTypeNames.TryGetFontNameString(_dataMemory.Span, _fontStart, platformId, encodingId, languageId, nameId, out rawBytes);

    /// <summary>Gets the default English full name of the font from its <c>name</c> table.</summary>
    /// <param name="name">Receives the decoded font name.</param>
    /// <returns><c>true</c> if a default English full name was found; otherwise <c>false</c>.</returns>
    public bool TryGetFontNameDefault(out string name) =>
        TrueTypeNames.TryGetFontNameDefault(_dataMemory.Span, _fontStart, out name);

    internal static void ValidateOutputBuffer(Span<byte> output, int outWidth, int outHeight, int outStride)
    {
        Guard.ThrowIfLessThan(outStride, outWidth, nameof(outStride));

        long required = (long)(outHeight - 1) * outStride + outWidth;
        if (output.Length < required)
        {
            throw new ArgumentException(
                $"Output buffer length ({output.Length}) is smaller than required ({required}).",
                nameof(output));
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private int GetGlyfOffset(int glyphIndex)
    {
        if ((uint)glyphIndex >= (uint)_numGlyphs || _indexToLocFormat >= 2)
        {
            return -1;
        }

        ReadOnlySpan<byte> data = _dataMemory.Span;
        long g1;
        long g2;

        if (_indexToLocFormat == 0)
        {
            int pos = _loca + glyphIndex * 2;
            if (!TrueTypeBinary.HasRange(data, pos, 4))
            {
                return -1;
            }

            g1 = _glyf + (long)TrueTypeBinary.ReadU16(data, pos) * 2;
            g2 = _glyf + (long)TrueTypeBinary.ReadU16(data, pos + 2) * 2;
        }
        else
        {
            int pos = _loca + glyphIndex * 4;
            if (!TrueTypeBinary.HasRange(data, pos, 8))
            {
                return -1;
            }

            g1 = _glyf + (long)TrueTypeBinary.ReadU32(data, pos);
            g2 = _glyf + (long)TrueTypeBinary.ReadU32(data, pos + 4);
        }

        if (g1 == g2 || g1 < 0 || g1 >= data.Length || g1 > int.MaxValue)
        {
            return -1;
        }

        return (int)g1;
    }

    private void AppendGlyphShape(int glyphIndex, RasterizerScratch scratch, int depth)
    {
        if (depth > MaxCompoundGlyphDepth)
        {
            return;
        }

        if (_cff.Size == 0)
        {
            AppendGlyphShapeTT(glyphIndex, scratch, depth);
        }
        else
        {
            AppendGlyphShapeT2(glyphIndex, scratch);
        }
    }

    private void AppendGlyphShapeT2(int glyphIndex, RasterizerScratch scratch)
    {
        int startCount = scratch.VertexCount;
        var ctx = new CffCharstring.CharstringContext
        {
            BoundsOnly = false,
            Scratch = scratch,
        };

        if (!CffCharstring.RunCharstring(
                _dataMemory.Span, _cff, _charstrings, _gsubrs, _subrs, _fontDicts, _fdSelect, glyphIndex, ref ctx))
        {
            scratch.VertexCount = startCount;
        }
    }

    private static void CloseShapeTT(
        RasterizerScratch scratch,
        bool wasOff,
        bool startOff,
        int sx,
        int sy,
        int scx,
        int scy,
        int cx,
        int cy)
    {
        if (startOff)
        {
            if (wasOff)
            {
                scratch.AppendVertex(new GlyphVertex(
                    unchecked((short)((cx + scx) >> 1)),
                    unchecked((short)((cy + scy) >> 1)),
                    unchecked((short)cx),
                    unchecked((short)cy),
                    0,
                    0,
                    GlyphVertexType.Curve));
            }

            scratch.AppendVertex(new GlyphVertex(
                unchecked((short)sx),
                unchecked((short)sy),
                unchecked((short)scx),
                unchecked((short)scy),
                0,
                0,
                GlyphVertexType.Curve));
        }
        else if (wasOff)
        {
            scratch.AppendVertex(new GlyphVertex(
                unchecked((short)sx),
                unchecked((short)sy),
                unchecked((short)cx),
                unchecked((short)cy),
                0,
                0,
                GlyphVertexType.Curve));
        }
        else
        {
            scratch.AppendVertex(new GlyphVertex(
                unchecked((short)sx),
                unchecked((short)sy),
                0,
                0,
                0,
                0,
                GlyphVertexType.Line));
        }
    }

    private void AppendGlyphShapeTT(int glyphIndex, RasterizerScratch scratch, int depth)
    {
        int g = GetGlyfOffset(glyphIndex);
        if (g < 0)
        {
            return;
        }

        ReadOnlySpan<byte> data = _dataMemory.Span;
        if (!TrueTypeBinary.HasRange(data, g, 10))
        {
            return;
        }

        short numberOfContours = TrueTypeBinary.ReadS16(data, g);

        if (numberOfContours > 0)
        {
            int endPtsOff = g + 10;
            int insLenOff = endPtsOff + numberOfContours * 2;
            if (!TrueTypeBinary.HasRange(data, insLenOff, 2))
            {
                return;
            }

            int ins = TrueTypeBinary.ReadU16(data, insLenOff);
            int pt = insLenOff + 2 + ins;
            if (pt > data.Length)
            {
                return;
            }

            int n = 1 + TrueTypeBinary.ReadU16(data, endPtsOff + numberOfContours * 2 - 2);
            if (n <= 0 || n > MaxGlyphPoints)
            {
                return;
            }

            scratch.EnsureTempVertices(n);
            GlyphVertex[] raw = scratch.TempVertices;

            // Load flags.
            byte flags = 0;
            byte flagCount = 0;
            for (int i = 0; i < n; ++i)
            {
                if (flagCount == 0)
                {
                    if (pt >= data.Length) return;
                    flags = data[pt++];
                    if ((flags & 8) != 0)
                    {
                        if (pt >= data.Length) return;
                        flagCount = data[pt++];
                    }
                }
                else
                {
                    --flagCount;
                }

                raw[i] = new GlyphVertex(0, 0, 0, 0, 0, 0, (GlyphVertexType)flags);
            }

            // Load x coordinates.
            int x = 0;
            for (int i = 0; i < n; ++i)
            {
                flags = (byte)raw[i].Type;
                if ((flags & 2) != 0)
                {
                    if (pt >= data.Length) return;
                    short dx = data[pt++];
                    x += (flags & 16) != 0 ? dx : -dx;
                }
                else if ((flags & 16) == 0)
                {
                    if (pt + 2 > data.Length) return;
                    x += unchecked((short)((data[pt] << 8) | data[pt + 1]));
                    pt += 2;
                }

                raw[i] = new GlyphVertex(unchecked((short)x), 0, 0, 0, 0, 0, (GlyphVertexType)flags);
            }

            // Load y coordinates.
            int y = 0;
            for (int i = 0; i < n; ++i)
            {
                flags = (byte)raw[i].Type;
                if ((flags & 4) != 0)
                {
                    if (pt >= data.Length) return;
                    short dy = data[pt++];
                    y += (flags & 32) != 0 ? dy : -dy;
                }
                else if ((flags & 32) == 0)
                {
                    if (pt + 2 > data.Length) return;
                    y += unchecked((short)((data[pt] << 8) | data[pt + 1]));
                    pt += 2;
                }

                raw[i] = new GlyphVertex(raw[i].X, unchecked((short)y), 0, 0, 0, 0, (GlyphVertexType)flags);
            }

            // Convert points to contour vertices directly into scratch.Vertices.
            int nextMove = 0;
            int j = 0;
            bool wasOff = false;
            bool startOff = false;
            int sx = 0, sy = 0, cx = 0, cy = 0, scx = 0, scy = 0;

            for (int i = 0; i < n; ++i)
            {
                flags = (byte)raw[i].Type;
                x = raw[i].X;
                y = raw[i].Y;

                if (nextMove == i)
                {
                    if (i != 0)
                    {
                        CloseShapeTT(scratch, wasOff, startOff, sx, sy, scx, scy, cx, cy);
                    }

                    if (j >= numberOfContours)
                    {
                        break;
                    }

                    int contourEnd = TrueTypeBinary.ReadU16(data, endPtsOff + j * 2);
                    startOff = (flags & 1) == 0 && i < contourEnd && i + 1 < n;
                    if (startOff)
                    {
                        scx = x;
                        scy = y;
                        byte nextFlags = (byte)raw[i + 1].Type;
                        if ((nextFlags & 1) == 0)
                        {
                            sx = (x + raw[i + 1].X) >> 1;
                            sy = (y + raw[i + 1].Y) >> 1;
                        }
                        else
                        {
                            sx = raw[i + 1].X;
                            sy = raw[i + 1].Y;
                            ++i;
                        }
                    }
                    else
                    {
                        sx = x;
                        sy = y;
                    }

                    scratch.AppendVertex(new GlyphVertex(unchecked((short)sx), unchecked((short)sy), 0, 0, 0, 0, GlyphVertexType.Move));
                    wasOff = false;
                    nextMove = 1 + contourEnd;
                    ++j;
                }
                else
                {
                    if ((flags & 1) == 0)
                    {
                        if (wasOff)
                        {
                            scratch.AppendVertex(new GlyphVertex(
                                unchecked((short)((cx + x) >> 1)),
                                unchecked((short)((cy + y) >> 1)),
                                unchecked((short)cx),
                                unchecked((short)cy),
                                0,
                                0,
                                GlyphVertexType.Curve));
                        }

                        cx = x;
                        cy = y;
                        wasOff = true;
                    }
                    else
                    {
                        if (wasOff)
                        {
                            scratch.AppendVertex(new GlyphVertex(
                                unchecked((short)x),
                                unchecked((short)y),
                                unchecked((short)cx),
                                unchecked((short)cy),
                                0,
                                0,
                                GlyphVertexType.Curve));
                        }
                        else
                        {
                            scratch.AppendVertex(new GlyphVertex(
                                unchecked((short)x),
                                unchecked((short)y),
                                0,
                                0,
                                0,
                                0,
                                GlyphVertexType.Line));
                        }

                        wasOff = false;
                    }
                }
            }

            CloseShapeTT(scratch, wasOff, startOff, sx, sy, scx, scy, cx, cy);
        }
        else if (numberOfContours < 0)
        {
            // Compound glyph.
            int comp = g + 10;
            bool more = true;
            int componentGuard = 0;

            while (more && ++componentGuard <= 1024)
            {
                if (!TrueTypeBinary.HasRange(data, comp, 4))
                {
                    break;
                }

                ushort flags = TrueTypeBinary.ReadU16(data, comp);
                ushort gidx = TrueTypeBinary.ReadU16(data, comp + 2);
                comp += 4;

                float m0 = 1.0f, m1 = 0.0f, m2 = 0.0f, m3 = 1.0f, m4 = 0.0f, m5 = 0.0f;

                if ((flags & 2) != 0)
                {
                    if ((flags & 1) != 0)
                    {
                        if (!TrueTypeBinary.HasRange(data, comp, 4)) break;
                        m4 = TrueTypeBinary.ReadS16(data, comp);
                        m5 = TrueTypeBinary.ReadS16(data, comp + 2);
                        comp += 4;
                    }
                    else
                    {
                        if (!TrueTypeBinary.HasRange(data, comp, 2)) break;
                        m4 = TrueTypeBinary.ReadS8(data, comp);
                        m5 = TrueTypeBinary.ReadS8(data, comp + 1);
                        comp += 2;
                    }
                }
                else
                {
                    // Matching point indices (unsupported by upstream stb_truetype; skip safely).
                    int skip = (flags & 1) != 0 ? 4 : 2;
                    if (!TrueTypeBinary.HasRange(data, comp, skip)) break;
                    comp += skip;
                }

                if ((flags & (1 << 3)) != 0)
                {
                    if (!TrueTypeBinary.HasRange(data, comp, 2)) break;
                    m0 = m3 = TrueTypeBinary.ReadS16(data, comp) / 16384.0f;
                    m1 = m2 = 0.0f;
                    comp += 2;
                }
                else if ((flags & (1 << 6)) != 0)
                {
                    if (!TrueTypeBinary.HasRange(data, comp, 4)) break;
                    m0 = TrueTypeBinary.ReadS16(data, comp) / 16384.0f;
                    m1 = m2 = 0.0f;
                    m3 = TrueTypeBinary.ReadS16(data, comp + 2) / 16384.0f;
                    comp += 4;
                }
                else if ((flags & (1 << 7)) != 0)
                {
                    if (!TrueTypeBinary.HasRange(data, comp, 8)) break;
                    m0 = TrueTypeBinary.ReadS16(data, comp) / 16384.0f;
                    m1 = TrueTypeBinary.ReadS16(data, comp + 2) / 16384.0f;
                    m2 = TrueTypeBinary.ReadS16(data, comp + 4) / 16384.0f;
                    m3 = TrueTypeBinary.ReadS16(data, comp + 6) / 16384.0f;
                    comp += 8;
                }

                // Match upstream nothings/stb@9600522: only rescale offsets when SCALED_COMPONENT_OFFSET (bit 11)
                // is set without UNSCALED_COMPONENT_OFFSET (bit 12); never double-scale the linear matrix.
                float m = MathF.Sqrt(m0 * m0 + m1 * m1);
                float n = MathF.Sqrt(m2 * m2 + m3 * m3);
                if ((flags & (1 << 11)) != 0 && (flags & (1 << 12)) == 0)
                {
                    m4 *= m;
                    m5 *= n;
                }

                int startIdx = scratch.VertexCount;
                AppendGlyphShape(gidx, scratch, depth + 1);
                int endIdx = scratch.VertexCount;

                GlyphVertex[] verts = scratch.Vertices;
                for (int i = startIdx; i < endIdx; ++i)
                {
                    GlyphVertex v = verts[i];
                    short vx = unchecked((short)(m0 * v.X + m2 * v.Y + m4));
                    short vy = unchecked((short)(m1 * v.X + m3 * v.Y + m5));
                    short vcx = unchecked((short)(m0 * v.Cx + m2 * v.Cy + m4));
                    short vcy = unchecked((short)(m1 * v.Cx + m3 * v.Cy + m5));
                    verts[i] = new GlyphVertex(vx, vy, vcx, vcy, v.Cx1, v.Cy1, v.Type);
                }

                more = (flags & (1 << 5)) != 0;
            }
        }
    }
}
