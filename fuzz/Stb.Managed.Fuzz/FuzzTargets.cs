using System.Buffers.Binary;
using Stb.Managed.Dxt;
using Stb.Managed.Perlin;
using Stb.Managed.RectPack;
using Stb.Managed.TrueType;

namespace Stb.Managed.Fuzz;

/// <summary>How far fuzz inputs got: counts of outcomes per target, printed at the end of a run.</summary>
internal static class FuzzStats
{
    internal static readonly System.Collections.Concurrent.ConcurrentDictionary<string, long> Outcomes = new(StringComparer.Ordinal);

    internal static void Record(string outcome) => Outcomes.AddOrUpdate(outcome, 1, (_, n) => n + 1);
}

internal interface IFuzzTarget
{
    string Name { get; }

    void Run(ReadOnlySpan<byte> data);
}

internal static class FuzzTargets
{
    internal const int MaxFuzzInputBytes = 1 * 1024 * 1024;

    internal static readonly StbOptions FuzzOptions = new()
    {
        MaxDegreeOfParallelism = 1,
        Limits = new StbLimits
        {
            MaxWidth = 512,
            MaxHeight = 512,
            MaxPixels = 65_536,
            MaxAllocationBytes = 2L * 1024 * 1024,
            MaxDecompressedBytes = 2L * 1024 * 1024,
            MaxFrames = 32,
        },
    };

    internal static readonly ImageDecodeOptions FuzzDecodeOptions = new()
    {
        Options = FuzzOptions,
    };

    internal static readonly IFuzzTarget[] All =
    [
        new ImageFuzzTarget(),
        new TrueTypeFuzzTarget(),
        new ImageWriteFuzzTarget(),
        new ImageResizeFuzzTarget(),
        new RectPackFuzzTarget(),
        new DxtFuzzTarget(),
        new PerlinFuzzTarget(),
    ];

    internal static IFuzzTarget GetByName(string name)
    {
        foreach (IFuzzTarget target in All)
        {
            if (string.Equals(target.Name, name, StringComparison.OrdinalIgnoreCase))
            {
                return target;
            }
        }

        string available = string.Join(", ", All.Select(t => t.Name));
        throw new ArgumentException($"Unknown fuzz target '{name}'. Available: {available}.", nameof(name));
    }
}

/// <summary>Fuzzes all 9 decoders in Stb.Managed.Image (header probe, 8-bit, 16-bit, HDR, animated GIF).</summary>
internal sealed class ImageFuzzTarget : IFuzzTarget
{
    public string Name => "image";

    public void Run(ReadOnlySpan<byte> data)
    {
        if (data.IsEmpty || data.Length > FuzzTargets.MaxFuzzInputBytes)
        {
            return;
        }

        try
        {
            ImageFormat fmt = StbImage.DetectFormat(data);
            _ = StbImage.Is16Bit(data, FuzzTargets.FuzzDecodeOptions);
            _ = StbImage.IsHdr(data);
            if (StbImage.TryGetInfo(data, out ImageInfo info, FuzzTargets.FuzzDecodeOptions))
            {
                if (info.Width <= 0 || info.Height <= 0)
                {
                    throw new InvalidOperationException($"TryGetInfo succeeded with non-positive dimensions {info.Width}x{info.Height}.");
                }
            }

            _ = StbImage.Load(data, ColorComponents.Default, FuzzTargets.FuzzDecodeOptions);
            FuzzStats.Record($"decoded {fmt}");

            if (fmt == ImageFormat.Gif)
            {
                _ = StbImage.LoadGifFrames(data, ColorComponents.RedGreenBlueAlpha, FuzzTargets.FuzzDecodeOptions);
            }
            else if (fmt == ImageFormat.Hdr)
            {
                _ = StbImage.LoadFloat(data, ColorComponents.Default, FuzzTargets.FuzzDecodeOptions);
            }
            else if (fmt == ImageFormat.Png)
            {
                _ = StbImage.Load16(data, ColorComponents.Default, FuzzTargets.FuzzDecodeOptions);
            }
        }
        catch (StbException e)
        {
            // Expected on malformed or over-budget hostile inputs.
            FuzzStats.Record($"rejected {StbImage.DetectFormat(data)} {e.Code}");
        }
    }
}

/// <summary>Fuzzes TTF/OTF/TTC parsing, cmap lookup, outline extraction, rasterization, packing, and SDF.</summary>
internal sealed class TrueTypeFuzzTarget : IFuzzTarget
{
    private static readonly int[] SampleChars = ['A', 'g', '0', ' '];

    public string Name => "truetype";

    public void Run(ReadOnlySpan<byte> data)
    {
        if (data.Length < 12 || data.Length > FuzzTargets.MaxFuzzInputBytes)
        {
            return;
        }

        byte[] copy = data.ToArray();
        try
        {
            int count = StbTrueType.GetNumberOfFonts(copy);
            if (count <= 0)
            {
                FuzzStats.Record("not a font");
                return;
            }

            int offset = StbTrueType.GetFontOffsetForIndex(copy, 0);
            if (offset < 0 || !StbTrueTypeFont.TryLoad(copy, offset, out StbTrueTypeFont? font, FuzzTargets.FuzzOptions) || font is null)
            {
                FuzzStats.Record("font tables rejected");
                return;
            }

            float scale = font.ScaleForPixelHeight(24.0f);
            if (!float.IsFinite(scale) || scale <= 0.0f || scale > 10.0f)
            {
                return;
            }

            foreach (int cp in SampleChars)
            {
                int g = font.FindGlyphIndex(cp);
                _ = font.GetGlyphHMetrics(g);
                _ = font.GetGlyphShape(g);
                _ = font.GetGlyphBitmap(scale, scale, g, out _, FuzzTargets.FuzzOptions);
                _ = font.GetGlyphSdf(scale, g, padding: 2, onEdgeValue: 180, pixelDistScale: 36.0f, out _, FuzzTargets.FuzzOptions);
            }

            byte[] atlas = new byte[128 * 128];
            Span<BakedChar> baked = stackalloc BakedChar[8];
            _ = StbTrueType.BakeFontBitmap(copy, offset, 16.0f, atlas, 128, 128, 'A', baked, FuzzTargets.FuzzOptions);
            FuzzStats.Record("glyphs rendered and baked");
        }
        catch (StbException e)
        {
            // Expected on malformed font tables or budget exhaustion.
            FuzzStats.Record($"rejected {e.Code}");
        }
    }
}

/// <summary>Fuzzes PNG/JPEG/BMP/TGA encoding + decode round-trip invariants.</summary>
internal sealed class ImageWriteFuzzTarget : IFuzzTarget
{
    public string Name => "imagewrite";

    public void Run(ReadOnlySpan<byte> data)
    {
        if (data.Length < 5)
        {
            return;
        }

        int w = (data[0] & 63) + 1;
        int h = (data[1] & 63) + 1;
        ColorComponents comp = (ColorComponents)((data[2] & 3) + 1);
        int fmtIdx = data[3] & 3;
        int ch = (int)comp;

        byte[] pixels = new byte[w * h * ch];
        ReadOnlySpan<byte> payload = data[4..];
        for (int i = 0; i < pixels.Length; ++i)
        {
            pixels[i] = payload[i % payload.Length];
        }

        try
        {
            byte[] encoded = fmtIdx switch
            {
                0 => StbImageWrite.WritePng(pixels, w, h, comp),
                1 => StbImageWrite.WriteJpeg(pixels, w, h, comp, quality: (data[3] >> 2) + 20),
                2 => StbImageWrite.WriteBmp(pixels, w, h, comp),
                _ => StbImageWrite.WriteTga(pixels, w, h, comp),
            };

            // Encoded output must always be decodable back by StbImage.
            ImageBuffer<byte> decoded = StbImage.Load(encoded, comp, FuzzTargets.FuzzDecodeOptions);
            if (decoded.Width != w || decoded.Height != h)
            {
                throw new InvalidOperationException($"Write/Read round-trip dimension mismatch: {w}x{h} -> {decoded.Width}x{decoded.Height}.");
            }
        }
        catch (StbException)
        {
            // Expected if limits reject an unusual combination.
        }
    }
}

/// <summary>Fuzzes all layouts, data types, filters, and edge modes in Stb.Managed.ImageResize.</summary>
internal sealed class ImageResizeFuzzTarget : IFuzzTarget
{
    public string Name => "imageresize";

    public void Run(ReadOnlySpan<byte> data)
    {
        if (data.Length < 8)
        {
            return;
        }

        int inW = (data[0] & 63) + 1;
        int inH = (data[1] & 63) + 1;
        int outW = (data[2] & 63) + 1;
        int outH = (data[3] & 63) + 1;
        ResizePixelLayout layout = (ResizePixelLayout)(data[4] % 17);
        ResizeDataType dataType = (data[5] % 3) switch
        {
            0 => ResizeDataType.UInt8Linear,
            1 => ResizeDataType.UInt8Srgb,
            _ => ResizeDataType.UInt8SrgbAlpha,
        };
        ResizeEdge edge = (ResizeEdge)(data[6] & 3);
        ResizeFilter filter = (ResizeFilter)(data[7] % 7);

        int ch = layout switch
        {
            ResizePixelLayout.OneChannel => 1,
            ResizePixelLayout.TwoChannel or ResizePixelLayout.Ra or ResizePixelLayout.Ar
                or ResizePixelLayout.RaPremultiplied or ResizePixelLayout.ArPremultiplied => 2,
            ResizePixelLayout.Rgb or ResizePixelLayout.Bgr => 3,
            _ => 4,
        };

        byte[] src = new byte[inW * inH * ch];
        ReadOnlySpan<byte> payload = data[8..];
        if (!payload.IsEmpty)
        {
            for (int i = 0; i < src.Length; ++i)
            {
                src[i] = payload[i % payload.Length];
            }
        }

        byte[] dst = new byte[outW * outH * ch];
        try
        {
            StbImageResize.Resize(src, inW, inH, dst, outW, outH, layout, dataType, edge, filter);
        }
        catch (StbException)
        {
            // Expected on unsupported edge combinations.
        }
    }
}

/// <summary>Fuzzes Skyline Bottom-Left & Best-Fit packing with arbitrary rectangle lists.</summary>
internal sealed class RectPackFuzzTarget : IFuzzTarget
{
    public string Name => "rectpack";

    public void Run(ReadOnlySpan<byte> data)
    {
        if (data.Length < 4)
        {
            return;
        }

        int binW = (data[0] & 127) + 1;
        int binH = (data[1] & 127) + 1;
        RectPackHeuristic heuristic = (data[2] & 1) == 0
            ? RectPackHeuristic.SkylineBottomLeft
            : RectPackHeuristic.SkylineBestFit;
        int rectCount = Math.Min((data.Length - 3) / 2, 128);
        if (rectCount == 0)
        {
            return;
        }

        PackRect[] rects = new PackRect[rectCount];
        for (int i = 0; i < rectCount; ++i)
        {
            rects[i] = new PackRect(data[3 + i * 2] & 63, data[4 + i * 2] & 63, id: i);
        }

        _ = StbRectPack.PackRects(binW, binH, rects, heuristic, options: FuzzTargets.FuzzOptions);

        // Invariant: every packed non-empty rectangle must stay strictly inside [0, binW) x [0, binH).
        foreach (PackRect r in rects)
        {
            if (r.WasPacked && r.Width > 0 && r.Height > 0)
            {
                if (r.X < 0 || r.Y < 0 || r.X + r.Width > binW || r.Y + r.Height > binH)
                {
                    throw new InvalidOperationException($"Packed rect ({r.X},{r.Y},{r.Width}x{r.Height}) out of bounds ({binW}x{binH}).");
                }
            }
        }
    }
}

/// <summary>Fuzzes BC1/BC3/BC4/BC5 compression with unaligned dimensions and arbitrary strides.</summary>
internal sealed class DxtFuzzTarget : IFuzzTarget
{
    public string Name => "dxt";

    public void Run(ReadOnlySpan<byte> data)
    {
        if (data.Length < 4)
        {
            return;
        }

        int w = (data[0] & 63) + 1;
        int h = (data[1] & 63) + 1;
        DxtFormat fmt = (DxtFormat)(data[2] & 3);
        DxtCompressionMode mode = (data[3] & 1) == 0 ? DxtCompressionMode.Normal : DxtCompressionMode.HighQuality;
        int ch = fmt switch
        {
            DxtFormat.Bc4 => 1,
            DxtFormat.Bc5 => 2,
            _ => 4,
        };

        byte[] src = new byte[w * h * ch];
        ReadOnlySpan<byte> payload = data[4..];
        if (!payload.IsEmpty)
        {
            for (int i = 0; i < src.Length; ++i)
            {
                src[i] = payload[i % payload.Length];
            }
        }

        byte[] compressed = StbDxt.CompressImageToArray(src, w, h, fmt, 0, mode, FuzzTargets.FuzzOptions);
        long expected = StbDxt.GetCompressedSize(w, h, fmt);
        if (compressed.Length != expected)
        {
            throw new InvalidOperationException($"DXT size mismatch: expected {expected}, got {compressed.Length}.");
        }
    }
}

/// <summary>Fuzzes 3D Perlin, wrapping, derivatives, and 2D SIMD grids with arbitrary float inputs.</summary>
internal sealed class PerlinFuzzTarget : IFuzzTarget
{
    public string Name => "perlin";

    public void Run(ReadOnlySpan<byte> data)
    {
        if (data.Length < 16)
        {
            return;
        }

        float x = ReadFiniteFloat(data, 0);
        float y = ReadFiniteFloat(data, 4);
        float z = ReadFiniteFloat(data, 8);
        int pow2Wrap = (data[12] & 3) switch { 0 => 0, 1 => 16, 2 => 64, _ => 256 };
        int nonPow2Wrap = (data[12] & 63) + 1;
        int seed = data[13];
        int octaves = (data[14] & 7) + 1;
        int grid = (data[15] & 31) + 1;

        float n = StbPerlin.Noise3Seed(x, y, z, pow2Wrap, pow2Wrap, pow2Wrap, seed);
        if (float.IsNaN(n))
        {
            throw new InvalidOperationException($"Perlin.Noise3Seed returned NaN for ({x}, {y}, {z}).");
        }

        _ = StbPerlin.Noise3WrapNonPow2(x, y, z, nonPow2Wrap, nonPow2Wrap, nonPow2Wrap, (byte)seed);
        _ = StbPerlin.FbmNoise3(x, y, z, 2.0f, 0.5f, octaves);
        _ = StbPerlin.RidgeNoise3(x, y, z, 2.0f, 0.5f, 1.0f, octaves);
        _ = StbPerlin.TurbulenceNoise3(x, y, z, 2.0f, 0.5f, octaves);

        float[] buf = new float[grid * grid];
        StbPerlin.FillNoise2D(buf, grid, grid, x, y, 0.05f, 0.05f, z, pow2Wrap, pow2Wrap, pow2Wrap, seed, 0);
    }

    private static float ReadFiniteFloat(ReadOnlySpan<byte> data, int offset)
    {
        float v = BitConverter.Int32BitsToSingle(BinaryPrimitives.ReadInt32LittleEndian(data.Slice(offset, 4)));
        if (!float.IsFinite(v) || MathF.Abs(v) > 1e6f)
        {
            return 0.0f;
        }

        return v;
    }
}
