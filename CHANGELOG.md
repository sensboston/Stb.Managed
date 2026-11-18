# Changelog

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
