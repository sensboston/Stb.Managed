#if !defined(_WIN32) && !defined(__APPLE__) && !defined(_POSIX_C_SOURCE)
#define _POSIX_C_SOURCE 200809L
#endif

#include <errno.h>
#include <inttypes.h>
#include <limits.h>
#include <math.h>
#include <stdint.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <time.h>

#ifdef _WIN32
#define WIN32_LEAN_AND_MEAN
#include <windows.h>
#else
#include <sys/utsname.h>
#if defined(__APPLE__)
#include <sys/types.h>
#include <sys/sysctl.h>
#endif
#endif

#if defined(__AVX2__) || defined(_M_AVX2)
#define STBIR_AVX2
#endif
#if defined(__ARM_NEON) || defined(__ARM_NEON__) || defined(_M_ARM64)
#define STBIR_NEON
#define STBI_NEON
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

#ifndef STB_BENCH_STB_COMMIT
#define STB_BENCH_STB_COMMIT "unknown"
#endif
#ifndef STB_BENCH_RESIZE_COMMIT
#define STB_BENCH_RESIZE_COMMIT "unknown"
#endif
#ifndef STB_BENCH_BUILD_FLAGS
#define STB_BENCH_BUILD_FLAGS "not embedded; see CMake build command"
#endif

/*
 * Checksum serialization, independent of native struct layout:
 * - decode/resize/perlin: tightly packed samples, integers and IEEE float bits
 *   in little-endian byte order.
 * - encode: uint64 little-endian length, followed by encoded bytes.
 * - dxt: block bytes in row-major block order.
 * - bake/pack: complete atlas bytes only.
 * - sdf: for each codepoint, int32 LE width, height, x offset, y offset,
 *   then width*height bitmap bytes. Empty glyphs use four zero integers.
 * - rectpack: for each input rectangle, int32 LE packed flag, x, y; x and y
 *   are zero for unpacked rectangles.
 */

typedef struct {
    unsigned char *data;
    size_t size;
    size_t cap;
    int failed;
} Buffer;

typedef struct {
    char *key;
    char *value;
} Param;

typedef struct {
    char *name;
    char *op;
    char *input;
    Param params[32];
    int param_count;
} Case;

typedef enum {
    OP_DECODE8, OP_DECODE16, OP_DECODEF,
    OP_PNG, OP_JPEG, OP_BMP, OP_TGA, OP_HDR,
    OP_RESIZE, OP_BAKE, OP_PACK, OP_SDF, OP_RECT, OP_DXT, OP_PERLIN,
    OP_UNSUPPORTED
} Op;

typedef struct {
    unsigned char *pixels;
    int w, h, xoff, yoff;
} Glyph;

typedef struct {
    Case *item;
    Op op;
    unsigned char *file;
    size_t file_size;
    void *source;
    int source_is_stbi;
    int iw, ih, comp;
    int ow, oh, pixel_type;
    int quality, rle, px, first, count, padding, oversample, onedge;
    float scale, startx, starty, z, step, lacunarity, gain;
    int octaves;
    uint32_t seed;
    int rect_min, rect_max;
    void *output;
    size_t output_size;
    void *transient;
    int transient_w, transient_h;
    Buffer encoded;
    stbtt_fontinfo font;
    stbtt_bakedchar *baked;
    stbtt_packedchar *packed;
    Glyph *glyphs;
    stbrp_rect *rects;
    stbrp_rect *rect_template;
    stbrp_node *nodes;
} Bench;

static double now_seconds(void)
{
#ifdef _WIN32
    LARGE_INTEGER frequency, counter;
    if (!QueryPerformanceFrequency(&frequency) || !QueryPerformanceCounter(&counter)) {
        fprintf(stderr, "QueryPerformanceCounter failed\n");
        exit(2);
    }
    return (double)counter.QuadPart / (double)frequency.QuadPart;
#else
    struct timespec ts;
    if (clock_gettime(CLOCK_MONOTONIC, &ts) != 0) {
        perror("clock_gettime");
        exit(2);
    }
    return (double)ts.tv_sec + (double)ts.tv_nsec * 1e-9;
#endif
}

static int multiply_size(size_t a, size_t b, size_t *out)
{
    if (b != 0 && a > SIZE_MAX / b) return 0;
    *out = a * b;
    return 1;
}

static int image_bytes(int w, int h, int channels, size_t sample, size_t *out)
{
    size_t n;
    return w > 0 && h > 0 && channels > 0 &&
           multiply_size((size_t)w, (size_t)h, &n) &&
           multiply_size(n, (size_t)channels, &n) &&
           multiply_size(n, sample, out);
}

static uint64_t hash_bytes(uint64_t h, const void *data, size_t n)
{
    const unsigned char *p = (const unsigned char *)data;
    for (size_t i = 0; i < n; ++i) {
        h ^= p[i];
        h *= UINT64_C(1099511628211);
    }
    return h;
}

static uint64_t hash_u32(uint64_t h, uint32_t v)
{
    unsigned char b[4];
    for (int i = 0; i < 4; ++i) b[i] = (unsigned char)(v >> (8 * i));
    return hash_bytes(h, b, sizeof b);
}

static uint64_t hash_u64(uint64_t h, uint64_t v)
{
    unsigned char b[8];
    for (int i = 0; i < 8; ++i) b[i] = (unsigned char)(v >> (8 * i));
    return hash_bytes(h, b, sizeof b);
}

static uint64_t hash_samples(uint64_t h, const void *data, size_t bytes, size_t sample)
{
    const uint16_t one = 1;
    if (*(const unsigned char *)&one == 1 || sample == 1) return hash_bytes(h, data, bytes);
    const unsigned char *p = (const unsigned char *)data;
    for (size_t i = 0; i < bytes; i += sample)
        for (size_t j = sample; j > 0; --j) h = hash_bytes(h, p + i + j - 1, 1);
    return h;
}

static void write_callback(void *context, void *data, int size)
{
    Buffer *b = (Buffer *)context;
    size_t need, capacity;
    unsigned char *next;
    if (b->failed || size < 0 || (size_t)size > SIZE_MAX - b->size) {
        b->failed = 1;
        return;
    }
    need = b->size + (size_t)size;
    if (need > b->cap) {
        capacity = b->cap ? b->cap : 4096;
        while (capacity < need) {
            if (capacity > SIZE_MAX / 2) { capacity = need; break; }
            capacity *= 2;
        }
        next = (unsigned char *)realloc(b->data, capacity);
        if (!next) { b->failed = 1; return; }
        b->data = next;
        b->cap = capacity;
    }
    if (size) memcpy(b->data + b->size, data, (size_t)size);
    b->size = need;
}

static char *strip(char *s)
{
    char *end;
    while (*s == ' ' || *s == '\r' || *s == '\n') ++s;
    end = s + strlen(s);
    while (end > s && (end[-1] == ' ' || end[-1] == '\r' || end[-1] == '\n')) --end;
    *end = '\0';
    return s;
}

static const char *param(const Case *c, const char *key)
{
    for (int i = 0; i < c->param_count; ++i)
        if (strcmp(c->params[i].key, key) == 0) return c->params[i].value;
    return NULL;
}

static int parse_int(const char *s, int *value)
{
    char *end;
    long v;
    if (!s || !*s) return 0;
    errno = 0;
    v = strtol(s, &end, 10);
    if (errno || *end || v < INT_MIN || v > INT_MAX) return 0;
    *value = (int)v;
    return 1;
}

static int get_int(const Case *c, const char *key, int *value)
{
    return parse_int(param(c, key), value);
}

static int get_float(const Case *c, const char *key, float *value)
{
    const char *s = param(c, key);
    char *end;
    float v;
    if (!s || !*s) return 0;
    errno = 0;
    v = strtof(s, &end);
    if (errno || *end || !isfinite(v)) return 0;
    *value = v;
    return 1;
}

static int parse_case(char *line, Case *c)
{
    char *fields[4];
    char *p = line;
    memset(c, 0, sizeof *c);
    for (int i = 0; i < 3; ++i) {
        char *tab;
        fields[i] = p;
        tab = strchr(p, '\t');
        if (!tab) return 0;
        *tab = '\0';
        p = tab + 1;
    }
    fields[3] = p;
    if (strchr(p, '\t')) return 0;
    c->name = strip(fields[0]);
    c->op = strip(fields[1]);
    c->input = strip(fields[2]);
    p = strip(fields[3]);
    if (!*c->name || !*c->op || !*c->input || !*p) return 0;
    if (strcmp(p, "-") == 0) return 1;
    while (*p) {
        char *semi = strchr(p, ';');
        char *eq;
        if (semi) *semi = '\0';
        eq = strchr(p, '=');
        if (!eq || eq == p || !eq[1] || strchr(eq + 1, '=') || c->param_count == 32) return 0;
        *eq = '\0';
        c->params[c->param_count].key = strip(p);
        c->params[c->param_count].value = strip(eq + 1);
        if (!*c->params[c->param_count].key || !*c->params[c->param_count].value) return 0;
        for (int i = 0; i < c->param_count; ++i)
            if (strcmp(c->params[i].key, c->params[c->param_count].key) == 0) return 0;
        ++c->param_count;
        if (!semi) break;
        p = semi + 1;
        if (!*p) return 0;
    }
    return 1;
}

static int only_keys(const Case *c, const char *const *keys, int key_count)
{
    for (int i = 0; i < c->param_count; ++i) {
        int found = 0;
        for (int j = 0; j < key_count; ++j)
            if (strcmp(c->params[i].key, keys[j]) == 0) { found = 1; break; }
        if (!found) return 0;
    }
    return 1;
}

#define KEYS(c, ...) only_keys((c), (const char *const[]){__VA_ARGS__}, \
    (int)(sizeof((const char *const[]){__VA_ARGS__}) / sizeof(char *)))

static Op get_op(const char *s)
{
    if (!strcmp(s, "decode")) return OP_DECODE8;
    if (!strcmp(s, "decode16")) return OP_DECODE16;
    if (!strcmp(s, "decodef")) return OP_DECODEF;
    if (!strcmp(s, "encode_png")) return OP_PNG;
    if (!strcmp(s, "encode_jpeg")) return OP_JPEG;
    if (!strcmp(s, "encode_bmp")) return OP_BMP;
    if (!strcmp(s, "encode_tga")) return OP_TGA;
    if (!strcmp(s, "encode_hdr")) return OP_HDR;
    if (!strcmp(s, "resize")) return OP_RESIZE;
    if (!strcmp(s, "tt_bake")) return OP_BAKE;
    if (!strcmp(s, "tt_pack")) return OP_PACK;
    if (!strcmp(s, "tt_sdf")) return OP_SDF;
    if (!strcmp(s, "rectpack")) return OP_RECT;
    if (!strcmp(s, "dxt")) return OP_DXT;
    if (!strcmp(s, "perlin_fbm")) return OP_PERLIN;
    return OP_UNSUPPORTED;
}

static int load_file(const char *corpus, const char *name, unsigned char **data, size_t *size)
{
    size_t a = strlen(corpus), b = strlen(name);
    char *path;
    FILE *f;
    long length;
    if (a > SIZE_MAX - b - 2) return 0;
    path = (char *)malloc(a + b + 2);
    if (!path) return 0;
    memcpy(path, corpus, a);
    path[a] = '/';
    memcpy(path + a + 1, name, b + 1);
    f = fopen(path, "rb");
    if (!f) { fprintf(stderr, "Cannot open %s: %s\n", path, strerror(errno)); free(path); return 0; }
    free(path);
    if (fseek(f, 0, SEEK_END) != 0 || (length = ftell(f)) <= 0 || length > INT_MAX ||
        fseek(f, 0, SEEK_SET) != 0) { fclose(f); return 0; }
    *data = (unsigned char *)malloc((size_t)length);
    if (!*data) { fclose(f); return 0; }
    if (fread(*data, 1, (size_t)length, f) != (size_t)length) {
        free(*data); *data = NULL; fclose(f); return 0;
    }
    if (fclose(f) != 0) { free(*data); *data = NULL; return 0; }
    *size = (size_t)length;
    return 1;
}

static void cleanup_iteration(Bench *b)
{
    if (b->transient) {
        stbi_image_free(b->transient);
        b->transient = NULL;
    }
    if (b->glyphs) {
        for (int i = 0; i < b->count; ++i) {
            if (b->glyphs[i].pixels) stbtt_FreeSDF(b->glyphs[i].pixels, NULL);
            b->glyphs[i].pixels = NULL;
        }
    }
}

static void cleanup_bench(Bench *b)
{
    cleanup_iteration(b);
    free(b->file);
    if (b->source_is_stbi) stbi_image_free(b->source); else free(b->source);
    free(b->output);
    free(b->encoded.data);
    free(b->baked);
    free(b->packed);
    free(b->glyphs);
    free(b->rects);
    free(b->rect_template);
    free(b->nodes);
    memset(b, 0, sizeof *b);
}

/* Return 1 for ready, 0 for error, -1 for an unsupported case. */
static int prepare(Bench *b, Case *c, const char *corpus)
{
    const char *s;
    size_t n;
    int tmp;
    memset(b, 0, sizeof *b);
    b->item = c;
    b->op = get_op(c->op);
    if (b->op == OP_UNSUPPORTED) return -1;

    switch (b->op) {
    case OP_DECODE8: case OP_DECODE16: case OP_DECODEF: case OP_BMP: case OP_HDR:
        if (!KEYS(c, "comp")) return -1;
        break;
    case OP_PNG:
        if (!KEYS(c, "comp", "engine")) return -1;
        break;
    case OP_JPEG:
        if (!KEYS(c, "comp", "quality")) return -1;
        break;
    case OP_TGA:
        if (!KEYS(c, "comp", "rle")) return -1;
        break;
    case OP_RESIZE:
        if (!KEYS(c, "w", "h", "type", "comp")) return -1;
        break;
    case OP_BAKE:
        if (!KEYS(c, "px", "first", "count", "w", "h")) return -1;
        break;
    case OP_PACK:
        if (!KEYS(c, "px", "first", "count", "w", "h", "oversample", "padding")) return -1;
        break;
    case OP_SDF:
        if (!KEYS(c, "px", "first", "count", "padding", "onedge", "scale")) return -1;
        break;
    case OP_RECT:
        if (!KEYS(c, "count", "min", "max", "seed", "w", "h")) return -1;
        break;
    case OP_DXT:
        if (!KEYS(c, "comp", "format", "mode")) return -1;
        break;
    case OP_PERLIN:
        if (!KEYS(c, "w", "h", "startx", "starty", "z", "step", "lacunarity", "gain", "octaves")) return -1;
        break;
    default: return -1;
    }

    switch (b->op) {
    case OP_DECODE8: case OP_DECODE16: case OP_DECODEF:
        if (!KEYS(c, "comp") || !get_int(c, "comp", &b->comp) || b->comp < 1 || b->comp > 4) return 0;
        break;
    case OP_PNG:
        s = param(c, "engine");
        if (!KEYS(c, "comp", "engine") || !get_int(c, "comp", &b->comp) || b->comp < 1 || b->comp > 4 ||
            !s || (strcmp(s, "stb") && strcmp(s, "default"))) return 0;
        break;
    case OP_JPEG:
        if (!KEYS(c, "comp", "quality") || !get_int(c, "comp", &b->comp) || b->comp < 1 || b->comp > 4 ||
            !get_int(c, "quality", &b->quality) || b->quality < 1 || b->quality > 100) return 0;
        break;
    case OP_BMP: case OP_HDR:
        if (!KEYS(c, "comp") || !get_int(c, "comp", &b->comp) || b->comp < 1 || b->comp > 4) return 0;
        break;
    case OP_TGA:
        if (!KEYS(c, "comp", "rle") || !get_int(c, "comp", &b->comp) || b->comp < 1 || b->comp > 4 ||
            !get_int(c, "rle", &b->rle) || (b->rle != 0 && b->rle != 1)) return 0;
        break;
    case OP_RESIZE:
        s = param(c, "type");
        if (!KEYS(c, "w", "h", "type", "comp") || !get_int(c, "w", &b->ow) ||
            !get_int(c, "h", &b->oh) || !get_int(c, "comp", &b->comp) || b->comp != 4 || !s) return 0;
        if (!strcmp(s, "linear")) b->pixel_type = 0;
        else if (!strcmp(s, "srgb")) b->pixel_type = 1;
        else if (!strcmp(s, "float")) b->pixel_type = 2;
        else return -1;
        if (b->ow <= 0 || b->oh <= 0) return 0;
        break;
    case OP_BAKE: case OP_PACK:
        if (!(b->op == OP_BAKE ? KEYS(c, "px", "first", "count", "w", "h") :
                                 KEYS(c, "px", "first", "count", "w", "h", "oversample", "padding")) ||
            !get_int(c, "px", &b->px) || !get_int(c, "first", &b->first) || !get_int(c, "count", &b->count) ||
            !get_int(c, "w", &b->ow) || !get_int(c, "h", &b->oh) ||
            b->px <= 0 || b->first < 0 || b->count <= 0 || b->count > 65536 ||
            b->first > INT_MAX - b->count || b->ow <= 0 || b->oh <= 0) return 0;
        if (b->op == OP_PACK && (!get_int(c, "oversample", &b->oversample) ||
            !get_int(c, "padding", &b->padding) || b->oversample < 1 || b->oversample > 8 ||
            b->padding < 0 || b->padding >= b->ow || b->padding >= b->oh)) return 0;
        break;
    case OP_SDF:
        if (!KEYS(c, "px", "first", "count", "padding", "onedge", "scale") ||
            !get_int(c, "px", &b->px) || !get_int(c, "first", &b->first) || !get_int(c, "count", &b->count) ||
            !get_int(c, "padding", &b->padding) || !get_int(c, "onedge", &b->onedge) || !get_float(c, "scale", &b->scale) ||
            b->px <= 0 || b->first < 0 || b->count <= 0 || b->count > 65536 || b->first > INT_MAX - b->count ||
            b->padding < 0 || b->onedge < 0 || b->onedge > 255) return 0;
        break;
    case OP_RECT:
        if (!KEYS(c, "count", "min", "max", "seed", "w", "h") || strcmp(c->input, "-") ||
            !get_int(c, "count", &b->count) || !get_int(c, "min", &b->rect_min) ||
            !get_int(c, "max", &b->rect_max) || !get_int(c, "seed", &tmp) || tmp < 0 ||
            !get_int(c, "w", &b->ow) || !get_int(c, "h", &b->oh) ||
            b->count <= 0 || b->count > 1000000 || b->rect_min < 1 || b->rect_max < b->rect_min ||
            b->ow <= 0 || b->ow > 1000000 || b->oh <= 0) return 0;
        b->seed = (uint32_t)tmp;
        break;
    case OP_DXT:
        s = param(c, "format");
        if (!KEYS(c, "comp", "format", "mode") || !get_int(c, "comp", &b->comp) || b->comp != 4 ||
            !s || (strcmp(s, "bc1") && strcmp(s, "bc3")) || !param(c, "mode") || strcmp(param(c, "mode"), "normal")) return 0;
        b->pixel_type = !strcmp(s, "bc3");
        break;
    case OP_PERLIN:
        if (!KEYS(c, "w", "h", "startx", "starty", "z", "step", "lacunarity", "gain", "octaves") ||
            strcmp(c->input, "-") || !get_int(c, "w", &b->ow) || !get_int(c, "h", &b->oh) ||
            !get_float(c, "startx", &b->startx) || !get_float(c, "starty", &b->starty) ||
            !get_float(c, "z", &b->z) || !get_float(c, "step", &b->step) ||
            !get_float(c, "lacunarity", &b->lacunarity) || !get_float(c, "gain", &b->gain) ||
            !get_int(c, "octaves", &b->octaves) || b->ow <= 0 || b->oh <= 0 || b->octaves < 1 || b->octaves > 32) return 0;
        break;
    default: return -1;
    }

    if (b->op != OP_RECT && b->op != OP_PERLIN && !load_file(corpus, c->input, &b->file, &b->file_size)) return 0;
    if (b->op >= OP_PNG && b->op <= OP_RESIZE) {
        int source_components;
        if (b->op == OP_HDR)
            b->source = stbi_loadf_from_memory(b->file, (int)b->file_size, &b->iw, &b->ih, &source_components, b->comp);
        else
            b->source = stbi_load_from_memory(b->file, (int)b->file_size, &b->iw, &b->ih, &source_components, b->comp);
        if (!b->source) { fprintf(stderr, "%s: %s\n", c->name, stbi_failure_reason()); return 0; }
        b->source_is_stbi = 1;
        if (b->op == OP_RESIZE && b->pixel_type == 2) {
            float *f;
            const unsigned char *u = (const unsigned char *)b->source;
            if (!image_bytes(b->iw, b->ih, b->comp, 1, &n) || n > SIZE_MAX / sizeof(float)) return 0;
            f = (float *)malloc(n * sizeof(float));
            if (!f) return 0;
            for (size_t i = 0; i < n; ++i) f[i] = (float)u[i] / 255.0f;
            stbi_image_free(b->source);
            b->source = f;
            b->source_is_stbi = 0;
        }
    }
    if (b->op == OP_DXT) {
        int source_components;
        b->source = stbi_load_from_memory(b->file, (int)b->file_size, &b->iw, &b->ih, &source_components, 4);
        if (!b->source) { fprintf(stderr, "%s: %s\n", c->name, stbi_failure_reason()); return 0; }
        b->source_is_stbi = 1;
        if (b->iw > INT_MAX - 3 || b->ih > INT_MAX - 3 ||
            !multiply_size((size_t)((b->iw + 3) / 4), (size_t)((b->ih + 3) / 4), &n) ||
            !multiply_size(n, b->pixel_type ? 16u : 8u, &b->output_size)) return 0;
        b->output = malloc(b->output_size);
        if (!b->output) return 0;
    }
    if (b->op == OP_RESIZE || b->op == OP_BAKE || b->op == OP_PACK || b->op == OP_PERLIN) {
        int channels = b->op == OP_RESIZE ? 4 : 1;
        size_t sample = b->op == OP_PERLIN || (b->op == OP_RESIZE && b->pixel_type == 2) ? 4u : 1u;
        if (!image_bytes(b->ow, b->oh, channels, sample, &b->output_size)) return 0;
        b->output = malloc(b->output_size);
        if (!b->output) return 0;
    }
    if (b->op == OP_BAKE) {
        b->baked = (stbtt_bakedchar *)calloc((size_t)b->count, sizeof *b->baked);
        if (!b->baked) return 0;
    }
    if (b->op == OP_PACK) {
        b->packed = (stbtt_packedchar *)calloc((size_t)b->count, sizeof *b->packed);
        if (!b->packed) return 0;
    }
    if (b->op == OP_SDF) {
        int offset = stbtt_GetFontOffsetForIndex(b->file, 0);
        if (offset < 0 || !stbtt_InitFont(&b->font, b->file, offset)) return 0;
        b->glyphs = (Glyph *)calloc((size_t)b->count, sizeof *b->glyphs);
        if (!b->glyphs) return 0;
    }
    if (b->op == OP_RECT) {
        uint32_t state = b->seed;
        uint32_t range = (uint32_t)(b->rect_max - b->rect_min) + 1u;
        b->rects = (stbrp_rect *)calloc((size_t)b->count, sizeof *b->rects);
        b->rect_template = (stbrp_rect *)calloc((size_t)b->count, sizeof *b->rect_template);
        b->nodes = (stbrp_node *)calloc((size_t)b->ow, sizeof *b->nodes);
        if (!b->rects || !b->rect_template || !b->nodes) return 0;
        for (int i = 0; i < b->count; ++i) {
            state = state * UINT32_C(1664525) + UINT32_C(1013904223);
            b->rect_template[i].w = b->rect_min + (int)(state % range);
            state = state * UINT32_C(1664525) + UINT32_C(1013904223);
            b->rect_template[i].h = b->rect_min + (int)(state % range);
            b->rect_template[i].id = i;
        }
    }
    return 1;
}

static int execute(Bench *b)
{
    int n, ok;
    switch (b->op) {
    case OP_DECODE8:
        b->transient = stbi_load_from_memory(b->file, (int)b->file_size, &b->transient_w, &b->transient_h, &n, b->comp);
        return b->transient != NULL;
    case OP_DECODE16:
        b->transient = stbi_load_16_from_memory(b->file, (int)b->file_size, &b->transient_w, &b->transient_h, &n, b->comp);
        return b->transient != NULL;
    case OP_DECODEF:
        b->transient = stbi_loadf_from_memory(b->file, (int)b->file_size, &b->transient_w, &b->transient_h, &n, b->comp);
        return b->transient != NULL;
    case OP_PNG: case OP_JPEG: case OP_BMP: case OP_TGA: case OP_HDR:
        b->encoded.size = 0;
        b->encoded.failed = 0;
        if (b->op == OP_PNG)
            ok = stbi_write_png_to_func(write_callback, &b->encoded, b->iw, b->ih, b->comp, b->source, 0);
        else if (b->op == OP_JPEG)
            ok = stbi_write_jpg_to_func(write_callback, &b->encoded, b->iw, b->ih, b->comp, b->source, b->quality);
        else if (b->op == OP_BMP)
            ok = stbi_write_bmp_to_func(write_callback, &b->encoded, b->iw, b->ih, b->comp, b->source);
        else if (b->op == OP_TGA) {
            stbi_write_tga_with_rle = b->rle;
            ok = stbi_write_tga_to_func(write_callback, &b->encoded, b->iw, b->ih, b->comp, b->source);
        } else
            ok = stbi_write_hdr_to_func(write_callback, &b->encoded, b->iw, b->ih, b->comp, (const float *)b->source);
        return ok && !b->encoded.failed;
    case OP_RESIZE:
        return stbir_resize(b->source, b->iw, b->ih, 0, b->output, b->ow, b->oh, 0,
                            STBIR_RGBA,
                            b->pixel_type == 0 ? STBIR_TYPE_UINT8 :
                            b->pixel_type == 1 ? STBIR_TYPE_UINT8_SRGB : STBIR_TYPE_FLOAT,
                            STBIR_EDGE_CLAMP, STBIR_FILTER_DEFAULT) != NULL;
    case OP_BAKE:
        n = stbtt_BakeFontBitmap(b->file, 0, (float)b->px, (unsigned char *)b->output,
                                 b->ow, b->oh, b->first, b->count, b->baked);
        return n > 0;
    case OP_PACK: {
        stbtt_pack_context pc;
        if (!stbtt_PackBegin(&pc, (unsigned char *)b->output, b->ow, b->oh, 0, b->padding, NULL)) return 0;
        stbtt_PackSetOversampling(&pc, (unsigned int)b->oversample, (unsigned int)b->oversample);
        ok = stbtt_PackFontRange(&pc, b->file, 0, (float)b->px, b->first, b->count, b->packed);
        stbtt_PackEnd(&pc);
        return ok != 0;
    }
    case OP_SDF: {
        float font_scale = stbtt_ScaleForPixelHeight(&b->font, (float)b->px);
        for (int i = 0; i < b->count; ++i) {
            Glyph *g = &b->glyphs[i];
            memset(g, 0, sizeof *g);
            g->pixels = stbtt_GetCodepointSDF(&b->font, font_scale, b->first + i, b->padding,
                                              (unsigned char)b->onedge, b->scale,
                                              &g->w, &g->h, &g->xoff, &g->yoff);
            if (!g->pixels) {
                int x0, y0, x1, y1;
                stbtt_GetCodepointBitmapBox(&b->font, b->first + i, font_scale, font_scale, &x0, &y0, &x1, &y1);
                if (x0 != x1 && y0 != y1) return 0;
                memset(g, 0, sizeof *g);
            }
        }
        return 1;
    }
    case OP_RECT: {
        stbrp_context ctx;
        memcpy(b->rects, b->rect_template, (size_t)b->count * sizeof *b->rects);
        stbrp_init_target(&ctx, b->ow, b->oh, b->nodes, b->ow);
        (void)stbrp_pack_rects(&ctx, b->rects, b->count);
        return 1;
    }
    case OP_DXT: {
        int bx_count = (b->iw + 3) / 4, by_count = (b->ih + 3) / 4;
        const unsigned char *src = (const unsigned char *)b->source;
        unsigned char *dst = (unsigned char *)b->output;
        unsigned char block[64];
        for (int by = 0; by < by_count; ++by) {
            for (int bx = 0; bx < bx_count; ++bx) {
                for (int y = 0; y < 4; ++y) {
                    int sy = by * 4 + y;
                    if (sy >= b->ih) sy = b->ih - 1;
                    for (int x = 0; x < 4; ++x) {
                        int sx = bx * 4 + x;
                        if (sx >= b->iw) sx = b->iw - 1;
                        memcpy(block + (y * 4 + x) * 4, src + ((size_t)sy * (size_t)b->iw + (size_t)sx) * 4, 4);
                    }
                }
                stb_compress_dxt_block(dst + ((size_t)by * (size_t)bx_count + (size_t)bx) * (b->pixel_type ? 16u : 8u),
                                       block, b->pixel_type, STB_DXT_NORMAL);
            }
        }
        return 1;
    }
    case OP_PERLIN: {
        float *dst = (float *)b->output;
        for (int y = 0; y < b->oh; ++y) {
            float fy = b->starty + (float)y * b->step;
            for (int x = 0; x < b->ow; ++x)
                dst[(size_t)y * (size_t)b->ow + (size_t)x] =
                    stb_perlin_fbm_noise3(b->startx + (float)x * b->step, fy, b->z,
                                          b->lacunarity, b->gain, b->octaves);
        }
        return 1;
    }
    default: return 0;
    }
}

static int checksum(const Bench *b, uint64_t *result)
{
    uint64_t h = UINT64_C(14695981039346656037);
    size_t n;
    switch (b->op) {
    case OP_DECODE8: case OP_DECODE16: case OP_DECODEF: {
        size_t sample = b->op == OP_DECODE8 ? 1u : b->op == OP_DECODE16 ? 2u : 4u;
        if (!image_bytes(b->transient_w, b->transient_h, b->comp, sample, &n)) return 0;
        h = hash_samples(h, b->transient, n, sample);
        break;
    }
    case OP_PNG: case OP_JPEG: case OP_BMP: case OP_TGA: case OP_HDR:
        h = hash_u64(h, (uint64_t)b->encoded.size);
        h = hash_bytes(h, b->encoded.data, b->encoded.size);
        break;
    case OP_SDF:
        for (int i = 0; i < b->count; ++i) {
            const Glyph *g = &b->glyphs[i];
            h = hash_u32(h, (uint32_t)g->w);
            h = hash_u32(h, (uint32_t)g->h);
            h = hash_u32(h, (uint32_t)g->xoff);
            h = hash_u32(h, (uint32_t)g->yoff);
            if (g->pixels) {
                if (!image_bytes(g->w, g->h, 1, 1, &n)) return 0;
                h = hash_bytes(h, g->pixels, n);
            }
        }
        break;
    case OP_RECT:
        for (int i = 0; i < b->count; ++i) {
            const stbrp_rect *r = &b->rects[i];
            h = hash_u32(h, r->was_packed ? 1u : 0u);
            h = hash_u32(h, r->was_packed ? (uint32_t)r->x : 0u);
            h = hash_u32(h, r->was_packed ? (uint32_t)r->y : 0u);
        }
        break;
    default:
        h = hash_samples(h, b->output, b->output_size,
                         b->op == OP_PERLIN || (b->op == OP_RESIZE && b->pixel_type == 2) ? 4u : 1u);
        break;
    }
    *result = h;
    return 1;
}

static int compare_double(const void *a, const void *b)
{
    double x = *(const double *)a, y = *(const double *)b;
    return (x > y) - (x < y);
}

static double median(double *values, size_t count)
{
    qsort(values, count, sizeof *values, compare_double);
    return count % 2 ? values[count / 2] : (values[count / 2 - 1] + values[count / 2]) / 2.0;
}

static void csv_field(FILE *f, const char *s)
{
    fputc('"', f);
    while (*s) {
        if (*s == '"') fputc('"', f);
        fputc((unsigned char)*s++, f);
    }
    fputc('"', f);
}

static void row_prefix(FILE *f, const Case *c)
{
    csv_field(f, c->name);
    fputc(',', f);
    csv_field(f, c->op);
    fputc(',', f);
}

static int benchmark(FILE *out, Bench *b, double minimum_ms)
{
    double *samples = NULL, total = 0.0;
    size_t count = 0, cap = 0;
    uint64_t digest = 0;
    int success = 0;
    {
        /* Warm-up: at least 3 runs and at least 500 ms, the same rule as the managed harness. */
        double warm_start = now_seconds();
        for (int i = 0; i < 3 || (now_seconds() - warm_start) * 1000.0 < 500.0; ++i) {
            if (!execute(b)) { cleanup_iteration(b); goto done; }
            cleanup_iteration(b);
        }
    }
    for (;;) {
        double start, elapsed, free_start;
        int ephemeral;
        if (count == cap) {
            size_t next_cap = cap ? cap * 2 : 64;
            double *next;
            if (next_cap < cap || next_cap > SIZE_MAX / sizeof *samples) goto done;
            next = (double *)realloc(samples, next_cap * sizeof *samples);
            if (!next) goto done;
            samples = next;
            cap = next_cap;
        }
        start = now_seconds();
        if (!execute(b)) { cleanup_iteration(b); goto done; }
        elapsed = (now_seconds() - start) * 1000.0;
        ephemeral = b->op == OP_DECODE8 || b->op == OP_DECODE16 ||
                    b->op == OP_DECODEF || b->op == OP_SDF;
        if (ephemeral && !checksum(b, &digest)) { cleanup_iteration(b); goto done; }
        free_start = now_seconds();
        cleanup_iteration(b);
        elapsed += (now_seconds() - free_start) * 1000.0;
        samples[count++] = elapsed;
        total += elapsed;
        if (count >= 10 && total >= minimum_ms) {
            if (!ephemeral && !checksum(b, &digest)) goto done;
            break;
        }
    }
    {
        double med = median(samples, count);
        double min = samples[0];
        for (size_t i = 0; i < count; ++i) samples[i] = fabs(samples[i] - med);
        row_prefix(out, b->item);
        fprintf(out, "%zu,%.6f,%.6f,%.6f,%016" PRIx64 ",ok\n", count, min, med, median(samples, count), digest);
    }
    success = 1;
done:
    free(samples);
    return success;
}

static void metadata_line(FILE *out, const char *key, const char *value)
{
    fprintf(out, "# %s: ", key);
    while (*value) {
        unsigned char c = (unsigned char)*value++;
        fputc(c == '\r' || c == '\n' ? ' ' : c, out);
    }
    fputc('\n', out);
}

static void metadata(FILE *out)
{
    char cpu[512] = "unknown", os[512] = "unknown";
    char compiler[256];
#if defined(__clang__)
    snprintf(compiler, sizeof compiler, "Clang %s", __clang_version__);
#elif defined(_MSC_VER)
    snprintf(compiler, sizeof compiler, "MSVC %d", _MSC_VER);
#elif defined(__GNUC__)
    snprintf(compiler, sizeof compiler, "GCC %s", __VERSION__);
#else
    snprintf(compiler, sizeof compiler, "unknown");
#endif
#ifdef _WIN32
    {
        DWORD size = (DWORD)sizeof cpu;
        if (RegGetValueA(HKEY_LOCAL_MACHINE, "HARDWARE\\DESCRIPTION\\System\\CentralProcessor\\0",
                         "ProcessorNameString", RRF_RT_REG_SZ, NULL, cpu, &size) != ERROR_SUCCESS) {
            const char *env = getenv("PROCESSOR_IDENTIFIER");
            snprintf(cpu, sizeof cpu, "%s", env && *env ? env : "unknown");
        }
        snprintf(os, sizeof os, "Windows");
    }
#else
    {
        struct utsname u;
        if (uname(&u) == 0) snprintf(os, sizeof os, "%s %s %s", u.sysname, u.release, u.machine);
#if defined(__APPLE__)
        size_t len = sizeof cpu;
        if (sysctlbyname("machdep.cpu.brand_string", cpu, &len, NULL, 0) != 0)
            snprintf(cpu, sizeof cpu, "unknown");
#elif defined(__linux__)
        FILE *f = fopen("/proc/cpuinfo", "r");
        if (f) {
            char line[1024];
            while (fgets(line, sizeof line, f)) {
                char *colon = strchr(line, ':');
                if (!colon) continue;
                *colon = '\0';
                if (!strcmp(strip(line), "model name") || !strcmp(strip(line), "Hardware")) {
                    snprintf(cpu, sizeof cpu, "%s", strip(colon + 1));
                    break;
                }
            }
            fclose(f);
        }
#endif
    }
#endif
    metadata_line(out, "compiler", compiler);
    metadata_line(out, "flags", STB_BENCH_BUILD_FLAGS);
#if defined(STBIR_AVX2)
    metadata_line(out, "compiled_isa", "STBIR_AVX2");
#elif defined(STBIR_NEON)
    metadata_line(out, "compiled_isa", "STBIR_NEON STBI_NEON");
#else
    metadata_line(out, "compiled_isa", "no explicitly selected AVX2/NEON");
#endif
    metadata_line(out, "stb_commit", STB_BENCH_STB_COMMIT);
    metadata_line(out, "stb_image_resize_commit", STB_BENCH_RESIZE_COMMIT);
    metadata_line(out, "cpu", cpu);
    metadata_line(out, "os", os);
    metadata_line(out, "checksum", "FNV-1a-64; serialization documented in bench_native.c");
    metadata_line(out, "png_default", "native uses stb default; managed default may use a different encoder");
    fprintf(out, "case,op,runs,min_ms,median_ms,mad_ms,checksum,status\n");
}

static int parse_min_time(const char *s, double *v)
{
    char *end;
    errno = 0;
    *v = strtod(s, &end);
    return *s && !*end && !errno && isfinite(*v) && *v >= 0.0;
}

int main(int argc, char **argv)
{
    const char *corpus = NULL, *cases_path = "bench/cases.tsv", *output_path = NULL, *filter = NULL;
    double minimum_ms = 1000.0;
    FILE *cases, *out;
    char line[8192];
    unsigned long line_number = 0;
    int failed = 0;
    for (int i = 1; i < argc; ++i) {
        const char *arg = argv[i];
        if (!strcmp(arg, "--help")) {
            fprintf(stdout, "Usage: %s --corpus DIR [--cases FILE] [--out FILE] [--min-time-ms N] [--filter TEXT]\n", argv[0]);
            return 0;
        }
        if (i + 1 >= argc) { fprintf(stderr, "Missing value for %s\n", arg); return 2; }
        if (!strcmp(arg, "--corpus")) corpus = argv[++i];
        else if (!strcmp(arg, "--cases")) cases_path = argv[++i];
        else if (!strcmp(arg, "--out")) output_path = argv[++i];
        else if (!strcmp(arg, "--filter")) filter = argv[++i];
        else if (!strcmp(arg, "--min-time-ms")) {
            if (!parse_min_time(argv[++i], &minimum_ms)) { fprintf(stderr, "Invalid minimum time\n"); return 2; }
        } else { fprintf(stderr, "Unknown option %s\n", arg); return 2; }
    }
    if (!corpus || !*corpus) { fprintf(stderr, "--corpus is required\n"); return 2; }
    cases = fopen(cases_path, "rb");
    if (!cases) { fprintf(stderr, "Cannot open %s: %s\n", cases_path, strerror(errno)); return 2; }
    out = output_path ? fopen(output_path, "wb") : stdout;
    if (!out) { fprintf(stderr, "Cannot open %s: %s\n", output_path, strerror(errno)); fclose(cases); return 2; }
    metadata(out);
    while (fgets(line, sizeof line, cases)) {
        Case item;
        Bench bench;
        char *p = line;
        int ready;
        ++line_number;
        if (!strchr(line, '\n') && !feof(cases)) {
            fprintf(stderr, "%s:%lu: line too long\n", cases_path, line_number);
            failed = 1;
            break;
        }
        while (*p == ' ' || *p == '\t') ++p;
        if (*p == '#' || *p == '\r' || *p == '\n' || !*p) continue;
        if (!parse_case(line, &item)) {
            fprintf(stderr, "%s:%lu: invalid case\n", cases_path, line_number);
            failed = 1;
            break;
        }
        if (filter && !strstr(item.name, filter)) continue;
        ready = prepare(&bench, &item, corpus);
        if (ready < 0) {
            row_prefix(out, &item);
            fprintf(out, "0,,,,,skipped\n");
        } else if (!ready || !benchmark(out, &bench, minimum_ms)) {
            fprintf(stderr, "%s: preparation or operation failed\n", item.name);
            row_prefix(out, &item);
            fprintf(out, "0,,,,,error\n");
            failed = 1;
        }
        cleanup_bench(&bench);
        if (fflush(out) != 0) { fprintf(stderr, "CSV write failed\n"); failed = 1; break; }
    }
    if (ferror(cases)) { fprintf(stderr, "Error reading cases\n"); failed = 1; }
    if (fclose(cases) != 0) failed = 1;
    if (out == stdout) { if (fflush(out) != 0) failed = 1; }
    else if (fclose(out) != 0) failed = 1;
    return failed ? 1 : 0;
}
