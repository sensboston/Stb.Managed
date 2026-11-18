/*
 * Thin C shim exporting the pinned stb single-file library entry points for
 * Stb.Managed.Oracle differential tests and benchmarks.
 *
 * No codec logic lives here — every exported function forwards directly to
 * the upstream stb implementation.
 */

#include <limits.h>
#include <math.h>
#include <stddef.h>
#include <stdint.h>
#include <stdlib.h>
#include <string.h>

#if defined(_WIN32) || defined(__CYGWIN__)
#  define STB_ORACLE_EXPORT __declspec(dllexport)
#elif defined(__GNUC__) || defined(__clang__)
#  define STB_ORACLE_EXPORT __attribute__((visibility("default")))
#else
#  define STB_ORACLE_EXPORT
#endif

#define STB_IMAGE_IMPLEMENTATION
#include "stb_image.h"

#define STB_IMAGE_WRITE_IMPLEMENTATION
#include "stb_image_write.h"

#define STB_IMAGE_RESIZE_IMPLEMENTATION
#include "stb_image_resize2.h"

#define STB_RECT_PACK_IMPLEMENTATION
#include "stb_rect_pack.h"

#define STB_TRUETYPE_IMPLEMENTATION
#include "stb_truetype.h"

#define STB_DXT_IMPLEMENTATION
#include "stb_dxt.h"

#define STB_PERLIN_IMPLEMENTATION
#include "stb_perlin.h"

/* -------------------------------------------------------------------------- */
/* ABI & memory management                                                    */
/* -------------------------------------------------------------------------- */

STB_ORACLE_EXPORT int oracle_abi_version(void) {
    return 1;
}

STB_ORACLE_EXPORT void oracle_free(void *ptr) {
    free(ptr);
}

/* -------------------------------------------------------------------------- */
/* stb_image                                                                  */
/* -------------------------------------------------------------------------- */

STB_ORACLE_EXPORT uint8_t *oracle_stbi_load_from_memory(
    const uint8_t *buffer,
    int len,
    int *x,
    int *y,
    int *channels_in_file,
    int desired_channels) {
    return stbi_load_from_memory(buffer, len, x, y, channels_in_file, desired_channels);
}

STB_ORACLE_EXPORT uint16_t *oracle_stbi_load_16_from_memory(
    const uint8_t *buffer,
    int len,
    int *x,
    int *y,
    int *channels_in_file,
    int desired_channels) {
    return stbi_load_16_from_memory(buffer, len, x, y, channels_in_file, desired_channels);
}

STB_ORACLE_EXPORT float *oracle_stbi_loadf_from_memory(
    const uint8_t *buffer,
    int len,
    int *x,
    int *y,
    int *channels_in_file,
    int desired_channels) {
    return stbi_loadf_from_memory(buffer, len, x, y, channels_in_file, desired_channels);
}

STB_ORACLE_EXPORT uint8_t *oracle_stbi_load_gif_from_memory(
    const uint8_t *buffer,
    int len,
    int **delays,
    int *x,
    int *y,
    int *z,
    int *comp,
    int req_comp) {
    return stbi_load_gif_from_memory(buffer, len, delays, x, y, z, comp, req_comp);
}

STB_ORACLE_EXPORT int oracle_stbi_info_from_memory(
    const uint8_t *buffer,
    int len,
    int *x,
    int *y,
    int *comp) {
    return stbi_info_from_memory(buffer, len, x, y, comp);
}

STB_ORACLE_EXPORT int oracle_stbi_info2_from_memory(
    const uint8_t *buffer,
    int len,
    int *out_type,
    int *out_x,
    int *out_y,
    int *out_comp,
    int *out_is_16_bit,
    int *out_is_hdr,
    int *out_is_iphone_png) {
    stbi_info_res res = stbi_info2_from_memory(buffer, len);
    if (out_type) *out_type = res.type;
    if (out_x) *out_x = res.x;
    if (out_y) *out_y = res.y;
    if (out_comp) *out_comp = res.comp;
    if (out_is_16_bit) *out_is_16_bit = res.is_16_bit;
    if (out_is_hdr) *out_is_hdr = res.is_hdr;
    if (out_is_iphone_png) *out_is_iphone_png = res.is_iphone_png;
    return res.type != STBI_type_unsupported ? 1 : 0;
}

STB_ORACLE_EXPORT int oracle_stbi_is_16_bit_from_memory(const uint8_t *buffer, int len) {
    return stbi_is_16_bit_from_memory(buffer, len);
}

STB_ORACLE_EXPORT int oracle_stbi_is_hdr_from_memory(const uint8_t *buffer, int len) {
    return stbi_is_hdr_from_memory(buffer, len);
}

STB_ORACLE_EXPORT const char *oracle_stbi_failure_reason(void) {
    return stbi_failure_reason();
}

STB_ORACLE_EXPORT void oracle_stbi_image_free(void *ptr) {
    stbi_image_free(ptr);
}

STB_ORACLE_EXPORT void oracle_stbi_set_flip_vertically_on_load_thread(int flag) {
#ifdef STBI_THREAD_LOCAL
    stbi_set_flip_vertically_on_load_thread(flag);
#else
    stbi_set_flip_vertically_on_load(flag);
#endif
}

STB_ORACLE_EXPORT void oracle_stbi_set_unpremultiply_on_load_thread(int flag) {
#ifdef STBI_THREAD_LOCAL
    stbi_set_unpremultiply_on_load_thread(flag);
#else
    stbi_set_unpremultiply_on_load(flag);
#endif
}

STB_ORACLE_EXPORT void oracle_stbi_convert_iphone_png_to_rgb_thread(int flag) {
#ifdef STBI_THREAD_LOCAL
    stbi_convert_iphone_png_to_rgb_thread(flag);
#else
    stbi_convert_iphone_png_to_rgb(flag);
#endif
}

STB_ORACLE_EXPORT uint8_t *oracle_stbi_zlib_decode_malloc(
    const uint8_t *buffer,
    int len,
    int *outlen) {
    return (uint8_t *)stbi_zlib_decode_malloc((const char *)buffer, len, outlen);
}

STB_ORACLE_EXPORT uint8_t *oracle_stbi_zlib_decode_noheader_malloc(
    const uint8_t *buffer,
    int len,
    int *outlen) {
    return (uint8_t *)stbi_zlib_decode_noheader_malloc((const char *)buffer, len, outlen);
}

/* -------------------------------------------------------------------------- */
/* stb_image_write (memory buffer helper)                                     */
/* -------------------------------------------------------------------------- */

typedef struct {
    uint8_t *data;
    size_t len;
    size_t cap;
    int failed;
} oracle_mem_buf;

static void oracle_write_callback(void *context, void *data, int size) {
    oracle_mem_buf *buf = (oracle_mem_buf *)context;
    if (buf->failed || size <= 0) {
        return;
    }
    size_t add = (size_t)size;
    if (add > SIZE_MAX - buf->len || buf->len + add > (size_t)INT_MAX) {
        buf->failed = 1;
        return;
    }
    size_t needed = buf->len + add;
    if (needed > buf->cap) {
        size_t new_cap = buf->cap ? buf->cap : 4096;
        while (new_cap < needed) {
            if (new_cap > SIZE_MAX / 2) {
                new_cap = needed;
                break;
            }
            new_cap *= 2;
        }
        uint8_t *next = (uint8_t *)realloc(buf->data, new_cap);
        if (!next) {
            buf->failed = 1;
            return;
        }
        buf->data = next;
        buf->cap = new_cap;
    }
    memcpy(buf->data + buf->len, data, add);
    buf->len += add;
}

static uint8_t *oracle_finish_mem_buf(oracle_mem_buf *buf, int ok, int *out_len) {
    if (!ok || buf->failed) {
        free(buf->data);
        if (out_len) *out_len = 0;
        return NULL;
    }
    if (out_len) *out_len = (int)buf->len;
    return buf->data;
}

STB_ORACLE_EXPORT uint8_t *oracle_stbi_write_png_to_mem(
    int w,
    int h,
    int comp,
    const void *data,
    int stride_bytes,
    int compression_level,
    int force_filter,
    int flip_vertically,
    int *out_len) {
    oracle_mem_buf buf = {0};
    stbi_write_png_compression_level = compression_level;
    stbi_write_force_png_filter = force_filter;
    stbi_flip_vertically_on_write(flip_vertically);
    int ok = stbi_write_png_to_func(oracle_write_callback, &buf, w, h, comp, data, stride_bytes);
    return oracle_finish_mem_buf(&buf, ok, out_len);
}

STB_ORACLE_EXPORT uint8_t *oracle_stbi_write_bmp_to_mem(
    int w,
    int h,
    int comp,
    const void *data,
    int flip_vertically,
    int *out_len) {
    oracle_mem_buf buf = {0};
    stbi_flip_vertically_on_write(flip_vertically);
    int ok = stbi_write_bmp_to_func(oracle_write_callback, &buf, w, h, comp, data);
    return oracle_finish_mem_buf(&buf, ok, out_len);
}

STB_ORACLE_EXPORT uint8_t *oracle_stbi_write_tga_to_mem(
    int w,
    int h,
    int comp,
    const void *data,
    int with_rle,
    int flip_vertically,
    int *out_len) {
    oracle_mem_buf buf = {0};
    stbi_write_tga_with_rle = with_rle;
    stbi_flip_vertically_on_write(flip_vertically);
    int ok = stbi_write_tga_to_func(oracle_write_callback, &buf, w, h, comp, data);
    return oracle_finish_mem_buf(&buf, ok, out_len);
}

STB_ORACLE_EXPORT uint8_t *oracle_stbi_write_jpg_to_mem(
    int w,
    int h,
    int comp,
    const void *data,
    int quality,
    int flip_vertically,
    int *out_len) {
    oracle_mem_buf buf = {0};
    stbi_flip_vertically_on_write(flip_vertically);
    int ok = stbi_write_jpg_to_func(oracle_write_callback, &buf, w, h, comp, data, quality);
    return oracle_finish_mem_buf(&buf, ok, out_len);
}

STB_ORACLE_EXPORT uint8_t *oracle_stbi_write_hdr_to_mem(
    int w,
    int h,
    int comp,
    const float *data,
    int flip_vertically,
    int *out_len) {
    oracle_mem_buf buf = {0};
    stbi_flip_vertically_on_write(flip_vertically);
    int ok = stbi_write_hdr_to_func(oracle_write_callback, &buf, w, h, comp, data);
    return oracle_finish_mem_buf(&buf, ok, out_len);
}

/* -------------------------------------------------------------------------- */
/* stb_image_resize2                                                          */
/* -------------------------------------------------------------------------- */

STB_ORACLE_EXPORT int oracle_stbir_resize(
    const void *input_pixels,
    int input_w,
    int input_h,
    int64_t input_stride,
    void *output_pixels,
    int output_w,
    int output_h,
    int64_t output_stride,
    int pixel_layout,
    int data_type,
    int edge,
    int filter) {
    void *res = stbir_resize(
        input_pixels,
        input_w,
        input_h,
        (stbir_int64)input_stride,
        output_pixels,
        output_w,
        output_h,
        (stbir_int64)output_stride,
        (stbir_pixel_layout)pixel_layout,
        (stbir_datatype)data_type,
        (stbir_edge)edge,
        (stbir_filter)filter);
    return res != NULL ? 1 : 0;
}

STB_ORACLE_EXPORT int oracle_stbir_resize_extended(
    const void *input_pixels,
    int input_w,
    int input_h,
    int64_t input_stride,
    void *output_pixels,
    int output_w,
    int output_h,
    int64_t output_stride,
    int input_layout,
    int output_layout,
    int input_type,
    int output_type,
    int horizontal_edge,
    int vertical_edge,
    int horizontal_filter,
    int vertical_filter,
    int fast_alpha,
    int has_input_subrect,
    double s0,
    double t0,
    double s1,
    double t1,
    int has_output_subrect,
    int out_subx,
    int out_suby,
    int out_subw,
    int out_subh,
    int has_pixel_subrect,
    int px_subx,
    int px_suby,
    int px_subw,
    int px_subh) {
    STBIR_RESIZE resize;
    stbir_resize_init(
        &resize,
        input_pixels, input_w, input_h, (stbir_int64)input_stride,
        output_pixels, output_w, output_h, (stbir_int64)output_stride,
        (stbir_pixel_layout)input_layout,
        (stbir_datatype)input_type);
    stbir_set_pixel_layouts(&resize, (stbir_pixel_layout)input_layout, (stbir_pixel_layout)output_layout);
    stbir_set_datatypes(&resize, (stbir_datatype)input_type, (stbir_datatype)output_type);
    stbir_set_edgemodes(&resize, (stbir_edge)horizontal_edge, (stbir_edge)vertical_edge);
    stbir_set_filters(&resize, (stbir_filter)horizontal_filter, (stbir_filter)vertical_filter);
    stbir_set_non_pm_alpha_speed_over_quality(&resize, fast_alpha);
    if (has_pixel_subrect) {
        (void)stbir_set_pixel_subrect(&resize, px_subx, px_suby, px_subw, px_subh);
    } else {
        if (has_input_subrect) {
            (void)stbir_set_input_subrect(&resize, s0, t0, s1, t1);
        }
        if (has_output_subrect) {
            (void)stbir_set_output_pixel_subrect(&resize, out_subx, out_suby, out_subw, out_subh);
        }
    }
    return stbir_resize_extended(&resize) != 0 ? 1 : 0;
}

/* -------------------------------------------------------------------------- */
/* stb_rect_pack                                                              */
/* -------------------------------------------------------------------------- */

STB_ORACLE_EXPORT int oracle_stbrp_pack_rects(
    int width,
    int height,
    int num_nodes,
    int heuristic,
    int allow_out_of_mem,
    stbrp_rect *rects,
    int num_rects) {
    if (width <= 0 || height <= 0 || num_nodes <= 0 || num_rects < 0) {
        return 0;
    }
    stbrp_node *nodes = (stbrp_node *)malloc((size_t)num_nodes * sizeof(stbrp_node));
    if (!nodes) {
        return 0;
    }
    stbrp_context ctx;
    stbrp_init_target(&ctx, width, height, nodes, num_nodes);
    stbrp_setup_heuristic(&ctx, heuristic);
    stbrp_setup_allow_out_of_mem(&ctx, allow_out_of_mem);
    int all_packed = stbrp_pack_rects(&ctx, rects, num_rects);
    free(nodes);
    return all_packed;
}

/* -------------------------------------------------------------------------- */
/* stb_dxt                                                                    */
/* -------------------------------------------------------------------------- */

STB_ORACLE_EXPORT void oracle_stb_compress_dxt_block(
    uint8_t *dest,
    const uint8_t *src_rgba,
    int alpha,
    int mode) {
    stb_compress_dxt_block(dest, src_rgba, alpha, mode);
}

STB_ORACLE_EXPORT void oracle_stb_compress_bc4_block(
    uint8_t *dest,
    const uint8_t *src_r) {
    stb_compress_bc4_block(dest, src_r);
}

STB_ORACLE_EXPORT void oracle_stb_compress_bc5_block(
    uint8_t *dest,
    const uint8_t *src_rg) {
    stb_compress_bc5_block(dest, src_rg);
}

/* -------------------------------------------------------------------------- */
/* stb_perlin                                                                 */
/* -------------------------------------------------------------------------- */

STB_ORACLE_EXPORT float oracle_stb_perlin_noise3_seed(
    float x, float y, float z,
    int x_wrap, int y_wrap, int z_wrap,
    int seed) {
    return stb_perlin_noise3_seed(x, y, z, x_wrap, y_wrap, z_wrap, seed);
}

STB_ORACLE_EXPORT float oracle_stb_perlin_noise3_wrap_nonpow2(
    float x, float y, float z,
    int x_wrap, int y_wrap, int z_wrap,
    uint8_t seed) {
    return stb_perlin_noise3_wrap_nonpow2(x, y, z, x_wrap, y_wrap, z_wrap, seed);
}

STB_ORACLE_EXPORT float oracle_stb_perlin_noise3_derivatives(
    float x, float y, float z,
    int x_wrap, int y_wrap, int z_wrap,
    int seed,
    float *dx, float *dy, float *dz) {
    return stb_perlin_noise3_derivatives(x, y, z, x_wrap, y_wrap, z_wrap, seed, dx, dy, dz);
}

STB_ORACLE_EXPORT float oracle_stb_perlin_ridge_noise3(
    float x, float y, float z,
    float lacunarity, float gain, float offset,
    int octaves) {
    return stb_perlin_ridge_noise3(x, y, z, lacunarity, gain, offset, octaves);
}

STB_ORACLE_EXPORT float oracle_stb_perlin_fbm_noise3(
    float x, float y, float z,
    float lacunarity, float gain,
    int octaves) {
    return stb_perlin_fbm_noise3(x, y, z, lacunarity, gain, octaves);
}

STB_ORACLE_EXPORT float oracle_stb_perlin_turbulence_noise3(
    float x, float y, float z,
    float lacunarity, float gain,
    int octaves) {
    return stb_perlin_turbulence_noise3(x, y, z, lacunarity, gain, octaves);
}

/* -------------------------------------------------------------------------- */
/* stb_truetype                                                               */
/* -------------------------------------------------------------------------- */

typedef struct {
    stbtt_fontinfo info;
    uint8_t *data;
    int data_len;
} oracle_stbtt_font;

STB_ORACLE_EXPORT int oracle_stbtt_get_number_of_fonts(const uint8_t *data) {
    return stbtt_GetNumberOfFonts(data);
}

STB_ORACLE_EXPORT int oracle_stbtt_get_font_offset_for_index(const uint8_t *data, int index) {
    return stbtt_GetFontOffsetForIndex(data, index);
}

STB_ORACLE_EXPORT int oracle_stbtt_find_matching_font(const uint8_t *data, const char *name, int flags) {
    return stbtt_FindMatchingFont(data, name, flags);
}

STB_ORACLE_EXPORT int oracle_stbtt_compare_utf8_to_utf16_be(const char *s1, int len1, const char *s2, int len2) {
    return stbtt_CompareUTF8toUTF16_bigendian(s1, len1, s2, len2);
}

STB_ORACLE_EXPORT void oracle_stbtt_get_scaled_font_vmetrics(
    const uint8_t *fontdata,
    int index,
    float size,
    float *ascent,
    float *descent,
    float *line_gap) {
    stbtt_GetScaledFontVMetrics(fontdata, index, size, ascent, descent, line_gap);
}

STB_ORACLE_EXPORT oracle_stbtt_font *oracle_stbtt_font_create(const uint8_t *data, int data_len, int offset) {
    if (!data || data_len <= 0 || offset < 0 || offset >= data_len) {
        return NULL;
    }
    oracle_stbtt_font *handle = (oracle_stbtt_font *)calloc(1, sizeof(oracle_stbtt_font));
    if (!handle) {
        return NULL;
    }
    handle->data = (uint8_t *)malloc((size_t)data_len);
    if (!handle->data) {
        free(handle);
        return NULL;
    }
    memcpy(handle->data, data, (size_t)data_len);
    handle->data_len = data_len;
    if (!stbtt_InitFont(&handle->info, handle->data, offset)) {
        free(handle->data);
        free(handle);
        return NULL;
    }
    return handle;
}

STB_ORACLE_EXPORT void oracle_stbtt_font_destroy(oracle_stbtt_font *handle) {
    if (!handle) {
        return;
    }
    free(handle->data);
    free(handle);
}

STB_ORACLE_EXPORT int oracle_stbtt_get_num_glyphs(const oracle_stbtt_font *handle) {
    return handle ? handle->info.numGlyphs : 0;
}

STB_ORACLE_EXPORT int oracle_stbtt_find_glyph_index(const oracle_stbtt_font *handle, int codepoint) {
    return stbtt_FindGlyphIndex(&handle->info, codepoint);
}

STB_ORACLE_EXPORT float oracle_stbtt_scale_for_pixel_height(const oracle_stbtt_font *handle, float pixels) {
    return stbtt_ScaleForPixelHeight(&handle->info, pixels);
}

STB_ORACLE_EXPORT float oracle_stbtt_scale_for_pixel_width(const oracle_stbtt_font *handle, float pixels) {
    return stbtt_ScaleForPixelWidth(&handle->info, pixels);
}

STB_ORACLE_EXPORT float oracle_stbtt_scale_for_mapping_em_to_pixels(const oracle_stbtt_font *handle, float pixels) {
    return stbtt_ScaleForMappingEmToPixels(&handle->info, pixels);
}

STB_ORACLE_EXPORT void oracle_stbtt_get_font_vmetrics(
    const oracle_stbtt_font *handle,
    int *ascent,
    int *descent,
    int *line_gap) {
    stbtt_GetFontVMetrics(&handle->info, ascent, descent, line_gap);
}

STB_ORACLE_EXPORT int oracle_stbtt_get_font_vmetrics_os2(
    const oracle_stbtt_font *handle,
    int *typo_ascent,
    int *typo_descent,
    int *typo_line_gap) {
    return stbtt_GetFontVMetricsOS2(&handle->info, typo_ascent, typo_descent, typo_line_gap);
}

STB_ORACLE_EXPORT void oracle_stbtt_get_font_bbox(
    const oracle_stbtt_font *handle,
    int *x0,
    int *y0,
    int *x1,
    int *y1) {
    stbtt_GetFontBoundingBox(&handle->info, x0, y0, x1, y1);
}

STB_ORACLE_EXPORT int oracle_stbtt_get_font_underline_metrics(
    const oracle_stbtt_font *handle,
    int *position,
    int *thickness) {
    return stbtt_GetFontUnderlineMetrics(&handle->info, position, thickness);
}

STB_ORACLE_EXPORT void oracle_stbtt_get_glyph_hmetrics(
    const oracle_stbtt_font *handle,
    int glyph_index,
    int *advance_width,
    int *left_side_bearing) {
    stbtt_GetGlyphHMetrics(&handle->info, glyph_index, advance_width, left_side_bearing);
}

STB_ORACLE_EXPORT int oracle_stbtt_get_glyph_kern_advance(
    const oracle_stbtt_font *handle,
    int glyph1,
    int glyph2) {
    return stbtt_GetGlyphKernAdvance(&handle->info, glyph1, glyph2);
}

STB_ORACLE_EXPORT int oracle_stbtt_get_codepoint_kern_advance(
    const oracle_stbtt_font *handle,
    int ch1,
    int ch2) {
    return stbtt_GetCodepointKernAdvance(&handle->info, ch1, ch2);
}

STB_ORACLE_EXPORT int oracle_stbtt_get_kerning_table_length(const oracle_stbtt_font *handle) {
    return stbtt_GetKerningTableLength(&handle->info);
}

STB_ORACLE_EXPORT int oracle_stbtt_get_kerning_table(
    const oracle_stbtt_font *handle,
    stbtt_kerningentry *table,
    int table_length) {
    return stbtt_GetKerningTable(&handle->info, table, table_length);
}

STB_ORACLE_EXPORT int oracle_stbtt_get_glyph_box(
    const oracle_stbtt_font *handle,
    int glyph_index,
    int *x0,
    int *y0,
    int *x1,
    int *y1) {
    return stbtt_GetGlyphBox(&handle->info, glyph_index, x0, y0, x1, y1);
}

STB_ORACLE_EXPORT int oracle_stbtt_is_glyph_empty(const oracle_stbtt_font *handle, int glyph_index) {
    return stbtt_IsGlyphEmpty(&handle->info, glyph_index);
}

STB_ORACLE_EXPORT int oracle_stbtt_get_glyph_shape(
    const oracle_stbtt_font *handle,
    int glyph_index,
    stbtt_vertex **out_vertices) {
    return stbtt_GetGlyphShape(&handle->info, glyph_index, out_vertices);
}

STB_ORACLE_EXPORT void oracle_stbtt_get_glyph_bitmap_box_subpixel(
    const oracle_stbtt_font *handle,
    int glyph_index,
    float scale_x,
    float scale_y,
    float shift_x,
    float shift_y,
    int *ix0,
    int *iy0,
    int *ix1,
    int *iy1) {
    stbtt_GetGlyphBitmapBoxSubpixel(
        &handle->info, glyph_index, scale_x, scale_y, shift_x, shift_y, ix0, iy0, ix1, iy1);
}

STB_ORACLE_EXPORT uint8_t *oracle_stbtt_get_glyph_bitmap_subpixel(
    const oracle_stbtt_font *handle,
    float scale_x,
    float scale_y,
    float shift_x,
    float shift_y,
    int glyph_index,
    int *width,
    int *height,
    int *xoff,
    int *yoff) {
    return stbtt_GetGlyphBitmapSubpixel(
        &handle->info, scale_x, scale_y, shift_x, shift_y, glyph_index, width, height, xoff, yoff);
}

STB_ORACLE_EXPORT void oracle_stbtt_make_glyph_bitmap_subpixel(
    const oracle_stbtt_font *handle,
    uint8_t *output,
    int out_w,
    int out_h,
    int out_stride,
    float scale_x,
    float scale_y,
    float shift_x,
    float shift_y,
    int glyph_index) {
    stbtt_MakeGlyphBitmapSubpixel(
        &handle->info, output, out_w, out_h, out_stride, scale_x, scale_y, shift_x, shift_y, glyph_index);
}

STB_ORACLE_EXPORT void oracle_stbtt_make_glyph_bitmap_subpixel_prefilter(
    const oracle_stbtt_font *handle,
    uint8_t *output,
    int out_w,
    int out_h,
    int out_stride,
    float scale_x,
    float scale_y,
    float shift_x,
    float shift_y,
    int oversample_x,
    int oversample_y,
    float *sub_x,
    float *sub_y,
    int glyph_index) {
    stbtt_MakeGlyphBitmapSubpixelPrefilter(
        &handle->info, output, out_w, out_h, out_stride,
        scale_x, scale_y, shift_x, shift_y,
        oversample_x, oversample_y, sub_x, sub_y, glyph_index);
}

STB_ORACLE_EXPORT uint8_t *oracle_stbtt_get_glyph_sdf(
    const oracle_stbtt_font *handle,
    float scale,
    int glyph_index,
    int padding,
    uint8_t onedge_value,
    float pixel_dist_scale,
    int *width,
    int *height,
    int *xoff,
    int *yoff) {
    return stbtt_GetGlyphSDF(
        &handle->info, scale, glyph_index, padding, onedge_value, pixel_dist_scale,
        width, height, xoff, yoff);
}

STB_ORACLE_EXPORT const char *oracle_stbtt_get_font_name_string(
    const oracle_stbtt_font *handle,
    int *length,
    int platform_id,
    int encoding_id,
    int language_id,
    int name_id) {
    return stbtt_GetFontNameString(&handle->info, length, platform_id, encoding_id, language_id, name_id);
}

STB_ORACLE_EXPORT int oracle_stbtt_get_font_name_default(
    const oracle_stbtt_font *handle,
    char *output_buffer,
    int output_buffer_len) {
    return stbtt_GetFontNameDefault(&handle->info, output_buffer, (size_t)output_buffer_len);
}

STB_ORACLE_EXPORT int oracle_stbtt_bake_font_bitmap(
    const uint8_t *data,
    int offset,
    float pixel_height,
    uint8_t *pixels,
    int pw,
    int ph,
    int first_char,
    int num_chars,
    stbtt_bakedchar *chardata) {
    return stbtt_BakeFontBitmap(data, offset, pixel_height, pixels, pw, ph, first_char, num_chars, chardata);
}

STB_ORACLE_EXPORT void oracle_stbtt_get_baked_quad(
    const stbtt_bakedchar *chardata,
    int pw,
    int ph,
    int char_index,
    float *xpos,
    float *ypos,
    stbtt_aligned_quad *q,
    int opengl_fillrule) {
    stbtt_GetBakedQuad(chardata, pw, ph, char_index, xpos, ypos, q, opengl_fillrule);
}

STB_ORACLE_EXPORT int oracle_stbtt_pack_font_range(
    const uint8_t *fontdata,
    int font_index,
    float font_size,
    uint8_t *pixels,
    int pw,
    int ph,
    int stride_in_bytes,
    int padding,
    unsigned int h_oversample,
    unsigned int v_oversample,
    int skip_missing,
    int first_char,
    int num_chars,
    stbtt_packedchar *chardata) {
    stbtt_pack_context spc;
    if (!stbtt_PackBegin(&spc, pixels, pw, ph, stride_in_bytes, padding, NULL)) {
        return 0;
    }
    stbtt_PackSetOversampling(&spc, h_oversample, v_oversample);
    stbtt_PackSetSkipMissingCodepoints(&spc, skip_missing);
    int ok = stbtt_PackFontRange(&spc, fontdata, font_index, font_size, first_char, num_chars, chardata);
    stbtt_PackEnd(&spc);
    return ok;
}

STB_ORACLE_EXPORT int oracle_stbtt_pack_font_codepoints(
    const uint8_t *fontdata,
    int font_index,
    float font_size,
    uint8_t *pixels,
    int pw,
    int ph,
    int stride_in_bytes,
    int padding,
    unsigned int h_oversample,
    unsigned int v_oversample,
    int skip_missing,
    const int *codepoints,
    int num_chars,
    stbtt_packedchar *chardata) {
    stbtt_pack_context spc;
    if (!stbtt_PackBegin(&spc, pixels, pw, ph, stride_in_bytes, padding, NULL)) {
        return 0;
    }
    stbtt_PackSetOversampling(&spc, h_oversample, v_oversample);
    stbtt_PackSetSkipMissingCodepoints(&spc, skip_missing);
    stbtt_pack_range range;
    range.font_size = font_size;
    range.first_unicode_codepoint_in_range = 0;
    range.array_of_unicode_codepoints = codepoints;
    range.num_chars = num_chars;
    range.chardata_for_range = chardata;
    range.h_oversample = 0;
    range.v_oversample = 0;
    int ok = stbtt_PackFontRanges(&spc, fontdata, font_index, &range, 1);
    stbtt_PackEnd(&spc);
    return ok;
}

STB_ORACLE_EXPORT void oracle_stbtt_get_packed_quad(
    const stbtt_packedchar *chardata,
    int pw,
    int ph,
    int char_index,
    float *xpos,
    float *ypos,
    stbtt_aligned_quad *q,
    int align_to_integer) {
    stbtt_GetPackedQuad(chardata, pw, ph, char_index, xpos, ypos, q, align_to_integer);
}

/* ---------- Batch helpers (for fair image/grid benchmarks & differential tests) ---------- */

static void oracle_gather_block(
    const uint8_t *src,
    int width,
    int height,
    int channels,
    int bx,
    int by,
    uint8_t *block) {
    int y0 = by * 4;
    int x0 = bx * 4;
    int row_stride = width * channels;
    for (int py = 0; py < 4; ++py) {
        int sy = y0 + py;
        if (sy >= height) sy = height - 1;
        for (int px = 0; px < 4; ++px) {
            int sx = x0 + px;
            if (sx >= width) sx = width - 1;
            const uint8_t *sp = src + sy * row_stride + sx * channels;
            uint8_t *dp = block + (py * 4 + px) * channels;
            for (int c = 0; c < channels; ++c) {
                dp[c] = sp[c];
            }
        }
    }
}

STB_ORACLE_EXPORT void oracle_stb_compress_dxt_image(
    const uint8_t *src_rgba,
    int width,
    int height,
    uint8_t *dst,
    int alpha,
    int mode) {
    int blocks_x = (width + 3) >> 2;
    int blocks_y = (height + 3) >> 2;
    int block_bytes = alpha ? 16 : 8;
    uint8_t block[64];
    for (int by = 0; by < blocks_y; ++by) {
        for (int bx = 0; bx < blocks_x; ++bx) {
            oracle_gather_block(src_rgba, width, height, 4, bx, by, block);
            stb_compress_dxt_block(dst + (size_t)(by * blocks_x + bx) * (size_t)block_bytes, block, alpha, mode);
        }
    }
}

STB_ORACLE_EXPORT void oracle_stb_compress_bc4_image(
    const uint8_t *src_r,
    int width,
    int height,
    uint8_t *dst) {
    int blocks_x = (width + 3) >> 2;
    int blocks_y = (height + 3) >> 2;
    uint8_t block[16];
    for (int by = 0; by < blocks_y; ++by) {
        for (int bx = 0; bx < blocks_x; ++bx) {
            oracle_gather_block(src_r, width, height, 1, bx, by, block);
            stb_compress_bc4_block(dst + (size_t)(by * blocks_x + bx) * 8u, block);
        }
    }
}

STB_ORACLE_EXPORT void oracle_stb_compress_bc5_image(
    const uint8_t *src_rg,
    int width,
    int height,
    uint8_t *dst) {
    int blocks_x = (width + 3) >> 2;
    int blocks_y = (height + 3) >> 2;
    uint8_t block[32];
    for (int by = 0; by < blocks_y; ++by) {
        for (int bx = 0; bx < blocks_x; ++bx) {
            oracle_gather_block(src_rg, width, height, 2, bx, by, block);
            stb_compress_bc5_block(dst + (size_t)(by * blocks_x + bx) * 16u, block);
        }
    }
}

STB_ORACLE_EXPORT void oracle_stb_perlin_fill_noise2d(
    float *dst,
    int width,
    int height,
    float start_x,
    float start_y,
    float step_x,
    float step_y,
    float z,
    int x_wrap,
    int y_wrap,
    int z_wrap,
    int seed) {
    for (int iy = 0; iy < height; ++iy) {
        float y = start_y + (float)iy * step_y;
        float *row = dst + (size_t)iy * (size_t)width;
        for (int ix = 0; ix < width; ++ix) {
            row[ix] = stb_perlin_noise3_seed(start_x + (float)ix * step_x, y, z, x_wrap, y_wrap, z_wrap, seed);
        }
    }
}

STB_ORACLE_EXPORT void oracle_stb_perlin_fill_fbm2d(
    float *dst,
    int width,
    int height,
    float start_x,
    float start_y,
    float step_x,
    float step_y,
    float z,
    float lacunarity,
    float gain,
    int octaves) {
    for (int iy = 0; iy < height; ++iy) {
        float y = start_y + (float)iy * step_y;
        float *row = dst + (size_t)iy * (size_t)width;
        for (int ix = 0; ix < width; ++ix) {
            row[ix] = stb_perlin_fbm_noise3(start_x + (float)ix * step_x, y, z, lacunarity, gain, octaves);
        }
    }
}

STB_ORACLE_EXPORT void oracle_stb_perlin_fill_ridge2d(
    float *dst,
    int width,
    int height,
    float start_x,
    float start_y,
    float step_x,
    float step_y,
    float z,
    float lacunarity,
    float gain,
    float offset,
    int octaves) {
    for (int iy = 0; iy < height; ++iy) {
        float y = start_y + (float)iy * step_y;
        float *row = dst + (size_t)iy * (size_t)width;
        for (int ix = 0; ix < width; ++ix) {
            row[ix] = stb_perlin_ridge_noise3(start_x + (float)ix * step_x, y, z, lacunarity, gain, offset, octaves);
        }
    }
}

STB_ORACLE_EXPORT void oracle_stb_perlin_fill_turbulence2d(
    float *dst,
    int width,
    int height,
    float start_x,
    float start_y,
    float step_x,
    float step_y,
    float z,
    float lacunarity,
    float gain,
    int octaves) {
    for (int iy = 0; iy < height; ++iy) {
        float y = start_y + (float)iy * step_y;
        float *row = dst + (size_t)iy * (size_t)width;
        for (int ix = 0; ix < width; ++ix) {
            row[ix] = stb_perlin_turbulence_noise3(start_x + (float)ix * step_x, y, z, lacunarity, gain, octaves);
        }
    }
}
