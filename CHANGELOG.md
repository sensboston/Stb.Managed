# Changelog

## 1.0.3 - 2026-11-19

- Resize: horizontal filters read the same fixed-width windows as stb (packed coefficients, clipped
  reads, zero-weight slots), so 8-bit, 16-bit and float output now equals `stb_image_resize2` exactly
  for every pixel layout, including one channel; an exact oracle test covers all of them.
- JPEG on Arm64: 4:2:0 chroma upsampling fused with YCbCr conversion and widened once per row, baseline
  block decoder kept out of line (about 20% faster on Neoverse-N1; 3–4% on x64).
- PNG: palette expansion without per-byte range checks (about 40% faster for palette images on Arm64).
- Resize: rows with one window length use a shorter inner path (about 3% on downscales).
- Weekly coverage-guided fuzzing with libFuzzer in CI; the `--libfuzzer` and `--afl` harness modes now
  attach the trace before any instrumented code runs.

## 1.0.2 - 2026-11-18

- TrueType: `BakeFontBitmap` treats a glyph whose box is inverted (x1 < x0 or y1 < y0) as empty instead of
  throwing `ArgumentOutOfRangeException` (found by fuzzing a font with three changed bytes).
- Fuzzing seeds every target with the whole test corpus and reports how far the inputs get.

## 1.0.1 - 2026-11-17

- Package icon.
- Resize: faster fancy-alpha unweighting and RGBA8 encoding, and a direct path for four-tap horizontal
  filters (upsampling about 16% faster on one thread); output unchanged.

## 1.0.0 - 2026-11-17

First release.

- `Stb.Managed.Image` (`stb_image.h`): JPEG baseline and progressive, PNG (all bit depths, palette,
  interlaced), BMP, TGA, GIF with animation, PSD, HDR, PIC, PNM; 8-bit, 16-bit and float decoding,
  header inspection, decoding into caller buffers.
- `Stb.Managed.ImageWrite` (`stb_image_write.h`): PNG (zlib or byte-exact stb deflate), JPEG, BMP,
  TGA, HDR.
- `Stb.Managed.ImageResize` (`stb_image_resize2.h`): all pixel layouts and data types, built-in and
  custom filters, edge modes, alpha weighting, sub-rectangles, reusable `StbImageResizer`.
- `Stb.Managed.TrueType` (`stb_truetype.h`): TrueType, OpenType CFF and collections, kerning,
  bitmaps, baked and packed atlases with oversampling, signed distance fields.
- `Stb.Managed.RectPack` (`stb_rect_pack.h`), `Stb.Managed.Dxt` (`stb_dxt.h`, BC1/BC3/BC4/BC5),
  `Stb.Managed.Perlin` (`stb_perlin.h`).
- `Stb.Managed.Gpu`: CUDA and OpenCL through ILGPU. With `StbGpu.Mode = Auto` (the default) DXT
  compression and large resizes on discrete GPUs run on the GPU; the GPU starts in the background.
- Resource limits (`StbLimits`) and typed errors (`StbException`) for untrusted input; no `unsafe`
  code; SIMD on x64 and Arm64; multithreading with the same output as one thread.
- Benchmark stand comparing every operation with the native stb build (`bench/`, `docs/benchmarks.md`).
