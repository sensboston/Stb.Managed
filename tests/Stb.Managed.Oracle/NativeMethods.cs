using System.Runtime.InteropServices;

[assembly: DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)]

namespace Stb.Managed.Oracle;

internal static unsafe partial class NativeMethods
{
    private const string LibName = "stb_oracle";

    static NativeMethods()
    {
        NativeLibraryResolver.EnsureRegistered();
    }

    [LibraryImport(LibName, EntryPoint = "oracle_abi_version")]
    internal static partial int AbiVersion();

    [LibraryImport(LibName, EntryPoint = "oracle_free")]
    internal static partial void Free(void* ptr);

    /* stb_image */

    [LibraryImport(LibName, EntryPoint = "oracle_stbi_load_from_memory")]
    internal static partial byte* StbiLoadFromMemory(
        byte* buffer,
        int len,
        out int x,
        out int y,
        out int channelsInFile,
        int desiredChannels);

    [LibraryImport(LibName, EntryPoint = "oracle_stbi_load_16_from_memory")]
    internal static partial ushort* StbiLoad16FromMemory(
        byte* buffer,
        int len,
        out int x,
        out int y,
        out int channelsInFile,
        int desiredChannels);

    [LibraryImport(LibName, EntryPoint = "oracle_stbi_loadf_from_memory")]
    internal static partial float* StbiLoadfFromMemory(
        byte* buffer,
        int len,
        out int x,
        out int y,
        out int channelsInFile,
        int desiredChannels);

    [LibraryImport(LibName, EntryPoint = "oracle_stbi_load_gif_from_memory")]
    internal static partial byte* StbiLoadGifFromMemory(
        byte* buffer,
        int len,
        out int* delays,
        out int x,
        out int y,
        out int z,
        out int comp,
        int reqComp);

    [LibraryImport(LibName, EntryPoint = "oracle_stbi_info_from_memory")]
    internal static partial int StbiInfoFromMemory(
        byte* buffer,
        int len,
        out int x,
        out int y,
        out int comp);

    [LibraryImport(LibName, EntryPoint = "oracle_stbi_info2_from_memory")]
    internal static partial int StbiInfo2FromMemory(
        byte* buffer,
        int len,
        out int outType,
        out int outX,
        out int outY,
        out int outComp,
        out int outIs16Bit,
        out int outIsHdr,
        out int outIsIphonePng);

    [LibraryImport(LibName, EntryPoint = "oracle_stbi_is_16_bit_from_memory")]
    internal static partial int StbiIs16BitFromMemory(byte* buffer, int len);

    [LibraryImport(LibName, EntryPoint = "oracle_stbi_is_hdr_from_memory")]
    internal static partial int StbiIsHdrFromMemory(byte* buffer, int len);

    [LibraryImport(LibName, EntryPoint = "oracle_stbi_failure_reason")]
    internal static partial nint StbiFailureReason();

    [LibraryImport(LibName, EntryPoint = "oracle_stbi_image_free")]
    internal static partial void StbiImageFree(void* ptr);

    [LibraryImport(LibName, EntryPoint = "oracle_stbi_set_flip_vertically_on_load_thread")]
    internal static partial void StbiSetFlipVerticallyOnLoadThread(int flag);

    [LibraryImport(LibName, EntryPoint = "oracle_stbi_set_unpremultiply_on_load_thread")]
    internal static partial void StbiSetUnpremultiplyOnLoadThread(int flag);

    [LibraryImport(LibName, EntryPoint = "oracle_stbi_convert_iphone_png_to_rgb_thread")]
    internal static partial void StbiConvertIphonePngToRgbThread(int flag);

    [LibraryImport(LibName, EntryPoint = "oracle_stbi_zlib_decode_malloc")]
    internal static partial byte* StbiZlibDecodeMalloc(byte* buffer, int len, out int outLen);

    [LibraryImport(LibName, EntryPoint = "oracle_stbi_zlib_decode_noheader_malloc")]
    internal static partial byte* StbiZlibDecodeNoHeaderMalloc(byte* buffer, int len, out int outLen);

    /* stb_image_write */

    [LibraryImport(LibName, EntryPoint = "oracle_stbi_write_png_to_mem")]
    internal static partial byte* StbiWritePngToMem(
        int w,
        int h,
        int comp,
        byte* data,
        int strideBytes,
        int compressionLevel,
        int forceFilter,
        int flipVertically,
        out int outLen);

    [LibraryImport(LibName, EntryPoint = "oracle_stbi_write_bmp_to_mem")]
    internal static partial byte* StbiWriteBmpToMem(
        int w,
        int h,
        int comp,
        byte* data,
        int flipVertically,
        out int outLen);

    [LibraryImport(LibName, EntryPoint = "oracle_stbi_write_tga_to_mem")]
    internal static partial byte* StbiWriteTgaToMem(
        int w,
        int h,
        int comp,
        byte* data,
        int withRle,
        int flipVertically,
        out int outLen);

    [LibraryImport(LibName, EntryPoint = "oracle_stbi_write_jpg_to_mem")]
    internal static partial byte* StbiWriteJpgToMem(
        int w,
        int h,
        int comp,
        byte* data,
        int quality,
        int flipVertically,
        out int outLen);

    [LibraryImport(LibName, EntryPoint = "oracle_stbi_write_hdr_to_mem")]
    internal static partial byte* StbiWriteHdrToMem(
        int w,
        int h,
        int comp,
        float* data,
        int flipVertically,
        out int outLen);

    /* stb_image_resize2 */

    [LibraryImport(LibName, EntryPoint = "oracle_stbir_resize")]
    internal static partial int StbirResize(
        void* inputPixels,
        int inputW,
        int inputH,
        long inputStride,
        void* outputPixels,
        int outputW,
        int outputH,
        long outputStride,
        int pixelLayout,
        int dataType,
        int edge,
        int filter);

    [LibraryImport(LibName, EntryPoint = "oracle_stbir_resize_extended")]
    internal static partial int StbirResizeExtended(
        void* inputPixels,
        int inputW,
        int inputH,
        long inputStride,
        void* outputPixels,
        int outputW,
        int outputH,
        long outputStride,
        int inputLayout,
        int outputLayout,
        int inputType,
        int outputType,
        int horizontalEdge,
        int verticalEdge,
        int horizontalFilter,
        int verticalFilter,
        int fastAlpha,
        int hasInputSubrect,
        double s0,
        double t0,
        double s1,
        double t1,
        int hasOutputSubrect,
        int outSubx,
        int outSuby,
        int outSubw,
        int outSubh,
        int hasPixelSubrect,
        int pxSubx,
        int pxSuby,
        int pxSubw,
        int pxSubh);

    /* stb_rect_pack */

    [LibraryImport(LibName, EntryPoint = "oracle_stbrp_pack_rects")]
    internal static partial int StbrpPackRects(
        int width,
        int height,
        int numNodes,
        int heuristic,
        int allowOutOfMem,
        OracleRect* rects,
        int numRects);

    /* stb_dxt */

    [LibraryImport(LibName, EntryPoint = "oracle_stb_compress_dxt_block")]
    internal static partial void StbCompressDxtBlock(byte* dest, byte* srcRgba, int alpha, int mode);

    [LibraryImport(LibName, EntryPoint = "oracle_stb_compress_bc4_block")]
    internal static partial void StbCompressBc4Block(byte* dest, byte* srcR);

    [LibraryImport(LibName, EntryPoint = "oracle_stb_compress_bc5_block")]
    internal static partial void StbCompressBc5Block(byte* dest, byte* srcRg);

    /* stb_perlin */

    [LibraryImport(LibName, EntryPoint = "oracle_stb_perlin_noise3_seed")]
    internal static partial float StbPerlinNoise3Seed(
        float x, float y, float z,
        int xWrap, int yWrap, int zWrap,
        int seed);

    [LibraryImport(LibName, EntryPoint = "oracle_stb_perlin_noise3_wrap_nonpow2")]
    internal static partial float StbPerlinNoise3WrapNonPow2(
        float x, float y, float z,
        int xWrap, int yWrap, int zWrap,
        byte seed);

    [LibraryImport(LibName, EntryPoint = "oracle_stb_perlin_noise3_derivatives")]
    internal static partial float StbPerlinNoise3Derivatives(
        float x, float y, float z,
        int xWrap, int yWrap, int zWrap,
        int seed,
        out float dx, out float dy, out float dz);

    [LibraryImport(LibName, EntryPoint = "oracle_stb_perlin_ridge_noise3")]
    internal static partial float StbPerlinRidgeNoise3(
        float x, float y, float z,
        float lacunarity, float gain, float offset,
        int octaves);

    [LibraryImport(LibName, EntryPoint = "oracle_stb_perlin_fbm_noise3")]
    internal static partial float StbPerlinFbmNoise3(
        float x, float y, float z,
        float lacunarity, float gain,
        int octaves);

    [LibraryImport(LibName, EntryPoint = "oracle_stb_perlin_turbulence_noise3")]
    internal static partial float StbPerlinTurbulenceNoise3(
        float x, float y, float z,
        float lacunarity, float gain,
        int octaves);

    /* stb_truetype */

    [LibraryImport(LibName, EntryPoint = "oracle_stbtt_get_number_of_fonts")]
    internal static partial int StbttGetNumberOfFonts(byte* data);

    [LibraryImport(LibName, EntryPoint = "oracle_stbtt_get_font_offset_for_index")]
    internal static partial int StbttGetFontOffsetForIndex(byte* data, int index);

    [LibraryImport(LibName, EntryPoint = "oracle_stbtt_find_matching_font")]
    internal static partial int StbttFindMatchingFont(byte* data, byte* nameUtf8NullTerminated, int flags);

    [LibraryImport(LibName, EntryPoint = "oracle_stbtt_compare_utf8_to_utf16_be")]
    internal static partial int StbttCompareUtf8ToUtf16Be(byte* s1, int len1, byte* s2, int len2);

    [LibraryImport(LibName, EntryPoint = "oracle_stbtt_get_scaled_font_vmetrics")]
    internal static partial void StbttGetScaledFontVMetrics(
        byte* fontData,
        int index,
        float size,
        out float ascent,
        out float descent,
        out float lineGap);

    [LibraryImport(LibName, EntryPoint = "oracle_stbtt_font_create")]
    internal static partial nint StbttFontCreate(byte* data, int dataLen, int offset);

    [LibraryImport(LibName, EntryPoint = "oracle_stbtt_font_destroy")]
    internal static partial void StbttFontDestroy(nint handle);

    [LibraryImport(LibName, EntryPoint = "oracle_stbtt_get_num_glyphs")]
    internal static partial int StbttGetNumGlyphs(nint handle);

    [LibraryImport(LibName, EntryPoint = "oracle_stbtt_find_glyph_index")]
    internal static partial int StbttFindGlyphIndex(nint handle, int codepoint);

    [LibraryImport(LibName, EntryPoint = "oracle_stbtt_scale_for_pixel_height")]
    internal static partial float StbttScaleForPixelHeight(nint handle, float pixels);

    [LibraryImport(LibName, EntryPoint = "oracle_stbtt_scale_for_pixel_width")]
    internal static partial float StbttScaleForPixelWidth(nint handle, float pixels);

    [LibraryImport(LibName, EntryPoint = "oracle_stbtt_scale_for_mapping_em_to_pixels")]
    internal static partial float StbttScaleForMappingEmToPixels(nint handle, float pixels);

    [LibraryImport(LibName, EntryPoint = "oracle_stbtt_get_font_vmetrics")]
    internal static partial void StbttGetFontVMetrics(nint handle, out int ascent, out int descent, out int lineGap);

    [LibraryImport(LibName, EntryPoint = "oracle_stbtt_get_font_vmetrics_os2")]
    internal static partial int StbttGetFontVMetricsOs2(nint handle, out int typoAscent, out int typoDescent, out int typoLineGap);

    [LibraryImport(LibName, EntryPoint = "oracle_stbtt_get_font_bbox")]
    internal static partial void StbttGetFontBBox(nint handle, out int x0, out int y0, out int x1, out int y1);

    [LibraryImport(LibName, EntryPoint = "oracle_stbtt_get_font_underline_metrics")]
    internal static partial int StbttGetFontUnderlineMetrics(nint handle, out int position, out int thickness);

    [LibraryImport(LibName, EntryPoint = "oracle_stbtt_get_glyph_hmetrics")]
    internal static partial void StbttGetGlyphHMetrics(nint handle, int glyphIndex, out int advanceWidth, out int leftSideBearing);

    [LibraryImport(LibName, EntryPoint = "oracle_stbtt_get_glyph_kern_advance")]
    internal static partial int StbttGetGlyphKernAdvance(nint handle, int glyph1, int glyph2);

    [LibraryImport(LibName, EntryPoint = "oracle_stbtt_get_codepoint_kern_advance")]
    internal static partial int StbttGetCodepointKernAdvance(nint handle, int ch1, int ch2);

    [LibraryImport(LibName, EntryPoint = "oracle_stbtt_get_kerning_table_length")]
    internal static partial int StbttGetKerningTableLength(nint handle);

    [LibraryImport(LibName, EntryPoint = "oracle_stbtt_get_kerning_table")]
    internal static partial int StbttGetKerningTable(nint handle, OracleKerningEntry* table, int tableLength);

    [LibraryImport(LibName, EntryPoint = "oracle_stbtt_get_glyph_box")]
    internal static partial int StbttGetGlyphBox(nint handle, int glyphIndex, out int x0, out int y0, out int x1, out int y1);

    [LibraryImport(LibName, EntryPoint = "oracle_stbtt_is_glyph_empty")]
    internal static partial int StbttIsGlyphEmpty(nint handle, int glyphIndex);

    [LibraryImport(LibName, EntryPoint = "oracle_stbtt_get_glyph_shape")]
    internal static partial int StbttGetGlyphShape(nint handle, int glyphIndex, out OracleFontVertex* outVertices);

    [LibraryImport(LibName, EntryPoint = "oracle_stbtt_get_glyph_bitmap_box_subpixel")]
    internal static partial void StbttGetGlyphBitmapBoxSubpixel(
        nint handle,
        int glyphIndex,
        float scaleX,
        float scaleY,
        float shiftX,
        float shiftY,
        out int ix0,
        out int iy0,
        out int ix1,
        out int iy1);

    [LibraryImport(LibName, EntryPoint = "oracle_stbtt_get_glyph_bitmap_subpixel")]
    internal static partial byte* StbttGetGlyphBitmapSubpixel(
        nint handle,
        float scaleX,
        float scaleY,
        float shiftX,
        float shiftY,
        int glyphIndex,
        out int width,
        out int height,
        out int xoff,
        out int yoff);

    [LibraryImport(LibName, EntryPoint = "oracle_stbtt_make_glyph_bitmap_subpixel")]
    internal static partial void StbttMakeGlyphBitmapSubpixel(
        nint handle,
        byte* output,
        int outW,
        int outH,
        int outStride,
        float scaleX,
        float scaleY,
        float shiftX,
        float shiftY,
        int glyphIndex);

    [LibraryImport(LibName, EntryPoint = "oracle_stbtt_make_glyph_bitmap_subpixel_prefilter")]
    internal static partial void StbttMakeGlyphBitmapSubpixelPrefilter(
        nint handle,
        byte* output,
        int outW,
        int outH,
        int outStride,
        float scaleX,
        float scaleY,
        float shiftX,
        float shiftY,
        int oversampleX,
        int oversampleY,
        out float subX,
        out float subY,
        int glyphIndex);

    [LibraryImport(LibName, EntryPoint = "oracle_stbtt_get_glyph_sdf")]
    internal static partial byte* StbttGetGlyphSdf(
        nint handle,
        float scale,
        int glyphIndex,
        int padding,
        byte onedgeValue,
        float pixelDistScale,
        out int width,
        out int height,
        out int xoff,
        out int yoff);

    [LibraryImport(LibName, EntryPoint = "oracle_stbtt_get_font_name_string")]
    internal static partial byte* StbttGetFontNameString(
        nint handle,
        out int length,
        int platformId,
        int encodingId,
        int languageId,
        int nameId);

    [LibraryImport(LibName, EntryPoint = "oracle_stbtt_get_font_name_default")]
    internal static partial int StbttGetFontNameDefault(nint handle, byte* outputBuffer, int outputBufferLen);

    [LibraryImport(LibName, EntryPoint = "oracle_stbtt_bake_font_bitmap")]
    internal static partial int StbttBakeFontBitmap(
        byte* data,
        int offset,
        float pixelHeight,
        byte* pixels,
        int pw,
        int ph,
        int firstChar,
        int numChars,
        OracleBakedChar* charData);

    [LibraryImport(LibName, EntryPoint = "oracle_stbtt_get_baked_quad")]
    internal static partial void StbttGetBakedQuad(
        OracleBakedChar* charData,
        int pw,
        int ph,
        int charIndex,
        ref float xpos,
        ref float ypos,
        out OracleAlignedQuad q,
        int openGlFillRule);

    [LibraryImport(LibName, EntryPoint = "oracle_stbtt_pack_font_range")]
    internal static partial int StbttPackFontRange(
        byte* fontData,
        int fontIndex,
        float fontSize,
        byte* pixels,
        int pw,
        int ph,
        int strideInBytes,
        int padding,
        uint hOversample,
        uint vOversample,
        int skipMissing,
        int firstChar,
        int numChars,
        OraclePackedChar* charData);

    [LibraryImport(LibName, EntryPoint = "oracle_stbtt_pack_font_codepoints")]
    internal static partial int StbttPackFontCodepoints(
        byte* fontData,
        int fontIndex,
        float fontSize,
        byte* pixels,
        int pw,
        int ph,
        int strideInBytes,
        int padding,
        uint hOversample,
        uint vOversample,
        int skipMissing,
        int* codepoints,
        int numChars,
        OraclePackedChar* charData);

    [LibraryImport(LibName, EntryPoint = "oracle_stbtt_get_packed_quad")]
    internal static partial void StbttGetPackedQuad(
        OraclePackedChar* charData,
        int pw,
        int ph,
        int charIndex,
        ref float xpos,
        ref float ypos,
        out OracleAlignedQuad q,
        int alignToInteger);

    [LibraryImport(LibName, EntryPoint = "oracle_stb_compress_dxt_image")]
    internal static partial void StbCompressDxtImage(
        byte* srcRgba,
        int width,
        int height,
        byte* dst,
        int alpha,
        int mode);

    [LibraryImport(LibName, EntryPoint = "oracle_stb_compress_bc4_image")]
    internal static partial void StbCompressBc4Image(
        byte* srcR,
        int width,
        int height,
        byte* dst);

    [LibraryImport(LibName, EntryPoint = "oracle_stb_compress_bc5_image")]
    internal static partial void StbCompressBc5Image(
        byte* srcRg,
        int width,
        int height,
        byte* dst);

    [LibraryImport(LibName, EntryPoint = "oracle_stb_perlin_fill_noise2d")]
    internal static partial void StbPerlinFillNoise2D(
        float* dst,
        int width,
        int height,
        float startX,
        float startY,
        float stepX,
        float stepY,
        float z,
        int xWrap,
        int yWrap,
        int zWrap,
        int seed);

    [LibraryImport(LibName, EntryPoint = "oracle_stb_perlin_fill_fbm2d")]
    internal static partial void StbPerlinFillFbm2D(
        float* dst,
        int width,
        int height,
        float startX,
        float startY,
        float stepX,
        float stepY,
        float z,
        float lacunarity,
        float gain,
        int octaves);

    [LibraryImport(LibName, EntryPoint = "oracle_stb_perlin_fill_ridge2d")]
    internal static partial void StbPerlinFillRidge2D(
        float* dst,
        int width,
        int height,
        float startX,
        float startY,
        float stepX,
        float stepY,
        float z,
        float lacunarity,
        float gain,
        float offset,
        int octaves);

    [LibraryImport(LibName, EntryPoint = "oracle_stb_perlin_fill_turbulence2d")]
    internal static partial void StbPerlinFillTurbulence2D(
        float* dst,
        int width,
        int height,
        float startX,
        float startY,
        float stepX,
        float stepY,
        float z,
        float lacunarity,
        float gain,
        int octaves);
}
