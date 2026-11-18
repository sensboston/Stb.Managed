using System.Runtime.InteropServices;

namespace Stb.Managed.Oracle;

public enum OracleImageType
{
    Unsupported = 0,
    Jpeg = 1,
    Png = 2,
    Gif = 3,
    Bmp = 4,
    Psd = 5,
    Pnm = 6,
    Hdr = 7,
    Tga = 8,
}

public readonly record struct OracleImageInfo(
    OracleImageType Type,
    int Width,
    int Height,
    int Channels,
    bool Is16Bit,
    bool IsHdr,
    bool IsIphonePng);

public sealed class OracleImage<T>
    where T : unmanaged
{
    private readonly T[] _data;

    public OracleImage(int width, int height, int channels, int sourceChannels, T[] data)
    {
        Width = width;
        Height = height;
        Channels = channels;
        SourceChannels = sourceChannels;
        _data = data;
    }

    public int Width { get; }
    public int Height { get; }
    public int Channels { get; }
    public int SourceChannels { get; }
    public ReadOnlyMemory<T> Data => _data;
    public ReadOnlySpan<T> AsSpan() => _data;
    public T[] GetRawArray() => _data;
}

public sealed class OracleGif
{
    private readonly byte[] _data;
    private readonly int[] _delays;

    public OracleGif(int width, int height, int frameCount, int channels, int sourceChannels, byte[] data, int[] delays)
    {
        Width = width;
        Height = height;
        FrameCount = frameCount;
        Channels = channels;
        SourceChannels = sourceChannels;
        _data = data;
        _delays = delays;
    }

    public int Width { get; }
    public int Height { get; }
    public int FrameCount { get; }
    public int Channels { get; }
    public int SourceChannels { get; }
    public ReadOnlySpan<byte> AsSpan() => _data;
    public ReadOnlySpan<int> Delays => _delays;
}

[StructLayout(LayoutKind.Sequential)]
public record struct OracleRect(int Id, int Width, int Height, int X, int Y, int WasPacked);

[StructLayout(LayoutKind.Sequential)]
public record struct OracleFontVertex(short X, short Y, short Cx, short Cy, short Cx1, short Cy1, byte Type, byte Padding);

[StructLayout(LayoutKind.Sequential)]
public record struct OracleKerningEntry(int Glyph1, int Glyph2, int Advance);

[StructLayout(LayoutKind.Sequential)]
public record struct OracleBakedChar(ushort X0, ushort Y0, ushort X1, ushort Y1, float XOff, float YOff, float XAdvance);

[StructLayout(LayoutKind.Sequential)]
public record struct OraclePackedChar(ushort X0, ushort Y0, ushort X1, ushort Y1, float XOff, float YOff, float XAdvance, float XOff2, float YOff2);

[StructLayout(LayoutKind.Sequential)]
public record struct OracleAlignedQuad(float X0, float Y0, float S0, float T0, float X1, float Y1, float S1, float T1);

public sealed class OracleGlyphBitmap
{
    public OracleGlyphBitmap(int width, int height, int xOff, int yOff, byte[] pixels)
    {
        Width = width;
        Height = height;
        XOff = xOff;
        YOff = yOff;
        Pixels = pixels;
    }

    public int Width { get; }
    public int Height { get; }
    public int XOff { get; }
    public int YOff { get; }
    public byte[] Pixels { get; }
}

public sealed unsafe class OracleTrueTypeFont : IDisposable
{
    private nint _handle;

    internal OracleTrueTypeFont(nint handle)
    {
        _handle = handle;
    }

    ~OracleTrueTypeFont() => Dispose();

    public void Dispose()
    {
        nint handle = Interlocked.Exchange(ref _handle, nint.Zero);
        if (handle != nint.Zero)
        {
            NativeMethods.StbttFontDestroy(handle);
        }

        GC.SuppressFinalize(this);
    }

    private nint Handle
    {
        get
        {
            nint h = _handle;
            ObjectDisposedException.ThrowIf(h == nint.Zero, this);
            return h;
        }
    }

    public int NumGlyphs => NativeMethods.StbttGetNumGlyphs(Handle);

    public int FindGlyphIndex(int codepoint) => NativeMethods.StbttFindGlyphIndex(Handle, codepoint);

    public float ScaleForPixelHeight(float pixels) => NativeMethods.StbttScaleForPixelHeight(Handle, pixels);

    public float ScaleForPixelWidth(float pixels) => NativeMethods.StbttScaleForPixelWidth(Handle, pixels);

    public float ScaleForMappingEmToPixels(float pixels) => NativeMethods.StbttScaleForMappingEmToPixels(Handle, pixels);

    public (int Ascent, int Descent, int LineGap) GetFontVMetrics()
    {
        NativeMethods.StbttGetFontVMetrics(Handle, out int ascent, out int descent, out int lineGap);
        return (ascent, descent, lineGap);
    }

    public bool TryGetFontVMetricsOs2(out int typoAscent, out int typoDescent, out int typoLineGap) =>
        NativeMethods.StbttGetFontVMetricsOs2(Handle, out typoAscent, out typoDescent, out typoLineGap) != 0;

    public (int X0, int Y0, int X1, int Y1) GetFontBoundingBox()
    {
        NativeMethods.StbttGetFontBBox(Handle, out int x0, out int y0, out int x1, out int y1);
        return (x0, y0, x1, y1);
    }

    public bool TryGetFontUnderlineMetrics(out int position, out int thickness) =>
        NativeMethods.StbttGetFontUnderlineMetrics(Handle, out position, out thickness) != 0;

    public (int AdvanceWidth, int LeftSideBearing) GetGlyphHMetrics(int glyphIndex)
    {
        NativeMethods.StbttGetGlyphHMetrics(Handle, glyphIndex, out int adv, out int lsb);
        return (adv, lsb);
    }

    public int GetGlyphKernAdvance(int glyph1, int glyph2) =>
        NativeMethods.StbttGetGlyphKernAdvance(Handle, glyph1, glyph2);

    public int GetCodepointKernAdvance(int ch1, int ch2) =>
        NativeMethods.StbttGetCodepointKernAdvance(Handle, ch1, ch2);

    public int GetKerningTableLength() => NativeMethods.StbttGetKerningTableLength(Handle);

    public OracleKerningEntry[] GetKerningTable()
    {
        int len = GetKerningTableLength();
        if (len <= 0)
        {
            return [];
        }

        OracleKerningEntry[] entries = new OracleKerningEntry[len];
        fixed (OracleKerningEntry* ptr = entries)
        {
            int written = NativeMethods.StbttGetKerningTable(Handle, ptr, len);
            if (written != len)
            {
                Array.Resize(ref entries, written);
            }
        }

        return entries;
    }

    public bool TryGetGlyphBox(int glyphIndex, out int x0, out int y0, out int x1, out int y1) =>
        NativeMethods.StbttGetGlyphBox(Handle, glyphIndex, out x0, out y0, out x1, out y1) != 0;

    public bool IsGlyphEmpty(int glyphIndex) => NativeMethods.StbttIsGlyphEmpty(Handle, glyphIndex) != 0;

    public OracleFontVertex[] GetGlyphShape(int glyphIndex)
    {
        int count = NativeMethods.StbttGetGlyphShape(Handle, glyphIndex, out OracleFontVertex* ptr);
        if (ptr is null || count <= 0)
        {
            if (ptr is not null)
            {
                NativeMethods.Free(ptr);
            }

            return [];
        }

        try
        {
            OracleFontVertex[] result = new OracleFontVertex[count];
            new ReadOnlySpan<OracleFontVertex>(ptr, count).CopyTo(result);
            return result;
        }
        finally
        {
            NativeMethods.Free(ptr);
        }
    }

    public (int X0, int Y0, int X1, int Y1) GetGlyphBitmapBoxSubpixel(
        int glyphIndex,
        float scaleX,
        float scaleY,
        float shiftX = 0f,
        float shiftY = 0f)
    {
        NativeMethods.StbttGetGlyphBitmapBoxSubpixel(
            Handle, glyphIndex, scaleX, scaleY, shiftX, shiftY,
            out int ix0, out int iy0, out int ix1, out int iy1);
        return (ix0, iy0, ix1, iy1);
    }

    public OracleGlyphBitmap? GetGlyphBitmapSubpixel(
        float scaleX,
        float scaleY,
        float shiftX,
        float shiftY,
        int glyphIndex)
    {
        byte* ptr = NativeMethods.StbttGetGlyphBitmapSubpixel(
            Handle, scaleX, scaleY, shiftX, shiftY, glyphIndex,
            out int w, out int h, out int xoff, out int yoff);
        if (ptr is null)
        {
            return null;
        }

        try
        {
            int count = checked(w * h);
            byte[] pixels = GC.AllocateUninitializedArray<byte>(count);
            new ReadOnlySpan<byte>(ptr, count).CopyTo(pixels);
            return new OracleGlyphBitmap(w, h, xoff, yoff, pixels);
        }
        finally
        {
            NativeMethods.Free(ptr);
        }
    }

    public void MakeGlyphBitmapSubpixel(
        Span<byte> output,
        int outW,
        int outH,
        int outStride,
        float scaleX,
        float scaleY,
        float shiftX,
        float shiftY,
        int glyphIndex)
    {
        if (output.IsEmpty || outW <= 0 || outH <= 0)
        {
            return;
        }

        fixed (byte* dst = output)
        {
            NativeMethods.StbttMakeGlyphBitmapSubpixel(
                Handle, dst, outW, outH, outStride, scaleX, scaleY, shiftX, shiftY, glyphIndex);
        }
    }

    public (float SubX, float SubY) MakeGlyphBitmapSubpixelPrefilter(
        Span<byte> output,
        int outW,
        int outH,
        int outStride,
        float scaleX,
        float scaleY,
        float shiftX,
        float shiftY,
        int oversampleX,
        int oversampleY,
        int glyphIndex)
    {
        fixed (byte* dst = output)
        {
            NativeMethods.StbttMakeGlyphBitmapSubpixelPrefilter(
                Handle, dst, outW, outH, outStride,
                scaleX, scaleY, shiftX, shiftY,
                oversampleX, oversampleY,
                out float subX, out float subY,
                glyphIndex);
            return (subX, subY);
        }
    }

    public OracleGlyphBitmap? GetGlyphSdf(
        float scale,
        int glyphIndex,
        int padding,
        byte onEdgeValue,
        float pixelDistScale)
    {
        byte* ptr = NativeMethods.StbttGetGlyphSdf(
            Handle, scale, glyphIndex, padding, onEdgeValue, pixelDistScale,
            out int w, out int h, out int xoff, out int yoff);
        if (ptr is null)
        {
            return null;
        }

        try
        {
            int count = checked(w * h);
            byte[] pixels = GC.AllocateUninitializedArray<byte>(count);
            new ReadOnlySpan<byte>(ptr, count).CopyTo(pixels);
            return new OracleGlyphBitmap(w, h, xoff, yoff, pixels);
        }
        finally
        {
            NativeMethods.Free(ptr);
        }
    }

    public byte[]? GetFontNameString(int platformId, int encodingId, int languageId, int nameId)
    {
        byte* ptr = NativeMethods.StbttGetFontNameString(Handle, out int length, platformId, encodingId, languageId, nameId);
        if (ptr is null || length < 0)
        {
            return null;
        }

        byte[] result = new byte[length];
        new ReadOnlySpan<byte>(ptr, length).CopyTo(result);
        return result;
    }

    public string? GetFontNameDefault()
    {
        int required = NativeMethods.StbttGetFontNameDefault(Handle, null, 0);
        if (required <= 0)
        {
            return null;
        }

        byte[] buf = new byte[required + 1];
        fixed (byte* ptr = buf)
        {
            int written = NativeMethods.StbttGetFontNameDefault(Handle, ptr, buf.Length);
            if (written <= 0)
            {
                return null;
            }

            int nullPos = Array.IndexOf(buf, (byte)0);
            int strLen = nullPos >= 0 ? nullPos : Math.Min(written, buf.Length);
            return System.Text.Encoding.UTF8.GetString(buf, 0, strLen);
        }
    }
}

public static unsafe class StbOracle
{
    // stb_image_write uses process-global configuration variables; guard writes with a lock.
    private static readonly object WriteLock = new();

    public static int AbiVersion => NativeMethods.AbiVersion();

    public static string? GetLastFailureReason()
    {
        nint ptr = NativeMethods.StbiFailureReason();
        return ptr == nint.Zero ? null : Marshal.PtrToStringUTF8(ptr);
    }

    public static OracleImage<byte>? LoadImage(
        ReadOnlySpan<byte> encoded,
        int desiredChannels = 0,
        bool flipVertically = false,
        bool unpremultiply = false,
        bool convertIphonePngToRgb = false)
    {
        if (encoded.IsEmpty)
        {
            return null;
        }

        SetLoadThreadFlags(flipVertically, unpremultiply, convertIphonePngToRgb);
        try
        {
            fixed (byte* src = encoded)
            {
                byte* ptr = NativeMethods.StbiLoadFromMemory(
                    src,
                    encoded.Length,
                    out int width,
                    out int height,
                    out int sourceChannels,
                    desiredChannels);
                if (ptr is null)
                {
                    return null;
                }

                try
                {
                    int outChannels = desiredChannels > 0 ? desiredChannels : sourceChannels;
                    int count = checked(width * height * outChannels);
                    byte[] result = GC.AllocateUninitializedArray<byte>(count);
                    new ReadOnlySpan<byte>(ptr, count).CopyTo(result);
                    return new OracleImage<byte>(width, height, outChannels, sourceChannels, result);
                }
                finally
                {
                    NativeMethods.StbiImageFree(ptr);
                }
            }
        }
        finally
        {
            ResetLoadThreadFlags();
        }
    }

    public static OracleImage<ushort>? LoadImage16(
        ReadOnlySpan<byte> encoded,
        int desiredChannels = 0,
        bool flipVertically = false,
        bool unpremultiply = false,
        bool convertIphonePngToRgb = false)
    {
        if (encoded.IsEmpty)
        {
            return null;
        }

        SetLoadThreadFlags(flipVertically, unpremultiply, convertIphonePngToRgb);
        try
        {
            fixed (byte* src = encoded)
            {
                ushort* ptr = NativeMethods.StbiLoad16FromMemory(
                    src,
                    encoded.Length,
                    out int width,
                    out int height,
                    out int sourceChannels,
                    desiredChannels);
                if (ptr is null)
                {
                    return null;
                }

                try
                {
                    int outChannels = desiredChannels > 0 ? desiredChannels : sourceChannels;
                    int count = checked(width * height * outChannels);
                    ushort[] result = GC.AllocateUninitializedArray<ushort>(count);
                    new ReadOnlySpan<ushort>(ptr, count).CopyTo(result);
                    return new OracleImage<ushort>(width, height, outChannels, sourceChannels, result);
                }
                finally
                {
                    NativeMethods.StbiImageFree(ptr);
                }
            }
        }
        finally
        {
            ResetLoadThreadFlags();
        }
    }

    public static OracleImage<float>? LoadImageFloat(
        ReadOnlySpan<byte> encoded,
        int desiredChannels = 0,
        bool flipVertically = false,
        bool unpremultiply = false,
        bool convertIphonePngToRgb = false)
    {
        if (encoded.IsEmpty)
        {
            return null;
        }

        SetLoadThreadFlags(flipVertically, unpremultiply, convertIphonePngToRgb);
        try
        {
            fixed (byte* src = encoded)
            {
                float* ptr = NativeMethods.StbiLoadfFromMemory(
                    src,
                    encoded.Length,
                    out int width,
                    out int height,
                    out int sourceChannels,
                    desiredChannels);
                if (ptr is null)
                {
                    return null;
                }

                try
                {
                    int outChannels = desiredChannels > 0 ? desiredChannels : sourceChannels;
                    int count = checked(width * height * outChannels);
                    float[] result = GC.AllocateUninitializedArray<float>(count);
                    new ReadOnlySpan<float>(ptr, count).CopyTo(result);
                    return new OracleImage<float>(width, height, outChannels, sourceChannels, result);
                }
                finally
                {
                    NativeMethods.StbiImageFree(ptr);
                }
            }
        }
        finally
        {
            ResetLoadThreadFlags();
        }
    }

    public static OracleGif? LoadGif(ReadOnlySpan<byte> encoded, int desiredChannels = 0)
    {
        if (encoded.IsEmpty)
        {
            return null;
        }

        fixed (byte* src = encoded)
        {
            byte* ptr = NativeMethods.StbiLoadGifFromMemory(
                src,
                encoded.Length,
                out int* delaysPtr,
                out int width,
                out int height,
                out int frames,
                out int sourceChannels,
                desiredChannels);
            if (ptr is null)
            {
                if (delaysPtr is not null)
                {
                    NativeMethods.StbiImageFree(delaysPtr);
                }

                return null;
            }

            try
            {
                int outChannels = desiredChannels > 0 ? desiredChannels : sourceChannels;
                int count = checked(width * height * frames * outChannels);
                byte[] pixels = GC.AllocateUninitializedArray<byte>(count);
                new ReadOnlySpan<byte>(ptr, count).CopyTo(pixels);

                int[] delays = new int[frames];
                if (delaysPtr is not null && frames > 0)
                {
                    new ReadOnlySpan<int>(delaysPtr, frames).CopyTo(delays);
                }

                return new OracleGif(width, height, frames, outChannels, sourceChannels, pixels, delays);
            }
            finally
            {
                NativeMethods.StbiImageFree(ptr);
                if (delaysPtr is not null)
                {
                    NativeMethods.StbiImageFree(delaysPtr);
                }
            }
        }
    }

    public static OracleImageInfo? GetImageInfo(ReadOnlySpan<byte> encoded)
    {
        if (encoded.IsEmpty)
        {
            return null;
        }

        fixed (byte* src = encoded)
        {
            int ok = NativeMethods.StbiInfo2FromMemory(
                src,
                encoded.Length,
                out int type,
                out int x,
                out int y,
                out int comp,
                out int is16Bit,
                out int isHdr,
                out int isIphonePng);
            if (ok == 0)
            {
                return null;
            }

            return new OracleImageInfo(
                (OracleImageType)type,
                x,
                y,
                comp,
                is16Bit != 0,
                isHdr != 0,
                isIphonePng != 0);
        }
    }

    public static byte[]? ZlibDecode(ReadOnlySpan<byte> compressed, bool parseHeader = true)
    {
        if (compressed.IsEmpty)
        {
            return null;
        }

        fixed (byte* src = compressed)
        {
            byte* ptr = parseHeader
                ? NativeMethods.StbiZlibDecodeMalloc(src, compressed.Length, out int outLen)
                : NativeMethods.StbiZlibDecodeNoHeaderMalloc(src, compressed.Length, out outLen);
            if (ptr is null)
            {
                return null;
            }

            try
            {
                byte[] result = GC.AllocateUninitializedArray<byte>(outLen);
                new ReadOnlySpan<byte>(ptr, outLen).CopyTo(result);
                return result;
            }
            finally
            {
                NativeMethods.StbiImageFree(ptr);
            }
        }
    }

    public static byte[] WritePng(
        int width,
        int height,
        int channels,
        ReadOnlySpan<byte> pixels,
        int strideBytes = 0,
        int compressionLevel = 8,
        int forceFilter = -1,
        bool flipVertically = false)
    {
        int effectiveStride = strideBytes == 0 ? checked(width * channels) : strideBytes;
        lock (WriteLock)
        {
            fixed (byte* src = pixels)
            {
                byte* ptr = NativeMethods.StbiWritePngToMem(
                    width,
                    height,
                    channels,
                    src,
                    effectiveStride,
                    compressionLevel,
                    forceFilter,
                    flipVertically ? 1 : 0,
                    out int outLen);
                return CopyAndFree(ptr, outLen, "stbi_write_png");
            }
        }
    }

    public static byte[] WriteBmp(
        int width,
        int height,
        int channels,
        ReadOnlySpan<byte> pixels,
        bool flipVertically = false)
    {
        lock (WriteLock)
        {
            fixed (byte* src = pixels)
            {
                byte* ptr = NativeMethods.StbiWriteBmpToMem(
                    width, height, channels, src, flipVertically ? 1 : 0, out int outLen);
                return CopyAndFree(ptr, outLen, "stbi_write_bmp");
            }
        }
    }

    public static byte[] WriteTga(
        int width,
        int height,
        int channels,
        ReadOnlySpan<byte> pixels,
        bool withRle = true,
        bool flipVertically = false)
    {
        lock (WriteLock)
        {
            fixed (byte* src = pixels)
            {
                byte* ptr = NativeMethods.StbiWriteTgaToMem(
                    width, height, channels, src, withRle ? 1 : 0, flipVertically ? 1 : 0, out int outLen);
                return CopyAndFree(ptr, outLen, "stbi_write_tga");
            }
        }
    }

    public static byte[] WriteJpg(
        int width,
        int height,
        int channels,
        ReadOnlySpan<byte> pixels,
        int quality = 90,
        bool flipVertically = false)
    {
        lock (WriteLock)
        {
            fixed (byte* src = pixels)
            {
                byte* ptr = NativeMethods.StbiWriteJpgToMem(
                    width, height, channels, src, quality, flipVertically ? 1 : 0, out int outLen);
                return CopyAndFree(ptr, outLen, "stbi_write_jpg");
            }
        }
    }

    public static byte[] WriteHdr(
        int width,
        int height,
        int channels,
        ReadOnlySpan<float> pixels,
        bool flipVertically = false)
    {
        lock (WriteLock)
        {
            fixed (float* src = pixels)
            {
                byte* ptr = NativeMethods.StbiWriteHdrToMem(
                    width, height, channels, src, flipVertically ? 1 : 0, out int outLen);
                return CopyAndFree(ptr, outLen, "stbi_write_hdr");
            }
        }
    }

    public static bool ResizeBytes(
        ReadOnlySpan<byte> input,
        int inputWidth,
        int inputHeight,
        long inputStride,
        Span<byte> output,
        int outputWidth,
        int outputHeight,
        long outputStride,
        int pixelLayout,
        int dataType,
        int edge = 0,
        int filter = 0)
    {
        fixed (byte* src = input)
        fixed (byte* dst = output)
        {
            return NativeMethods.StbirResize(
                src, inputWidth, inputHeight, inputStride,
                dst, outputWidth, outputHeight, outputStride,
                pixelLayout, dataType, edge, filter) != 0;
        }
    }

    public static bool ResizeBytesExtended(
        ReadOnlySpan<byte> input,
        int inputWidth,
        int inputHeight,
        long inputStride,
        Span<byte> output,
        int outputWidth,
        int outputHeight,
        long outputStride,
        int inputLayout,
        int outputLayout,
        int inputType,
        int outputType,
        int horizontalEdge = 0,
        int verticalEdge = 0,
        int horizontalFilter = 0,
        int verticalFilter = 0,
        bool fastAlpha = false,
        bool hasInputSubrect = false,
        double s0 = 0.0,
        double t0 = 0.0,
        double s1 = 1.0,
        double t1 = 1.0,
        bool hasOutputSubrect = false,
        int outSubx = 0,
        int outSuby = 0,
        int outSubw = 0,
        int outSubh = 0,
        bool hasPixelSubrect = false,
        int pxSubx = 0,
        int pxSuby = 0,
        int pxSubw = 0,
        int pxSubh = 0)
    {
        fixed (byte* srcBase = input)
        fixed (byte* dstBase = output)
        {
            byte* src = srcBase;
            byte* dst = dstBase;
            if (inputStride < 0)
            {
                src += (long)(inputHeight - 1) * (-inputStride);
            }

            if (outputStride < 0)
            {
                dst += (long)(outputHeight - 1) * (-outputStride);
            }

            return NativeMethods.StbirResizeExtended(
                src, inputWidth, inputHeight, inputStride,
                dst, outputWidth, outputHeight, outputStride,
                inputLayout, outputLayout, inputType, outputType,
                horizontalEdge, verticalEdge, horizontalFilter, verticalFilter,
                fastAlpha ? 1 : 0,
                hasInputSubrect ? 1 : 0, s0, t0, s1, t1,
                hasOutputSubrect ? 1 : 0, outSubx, outSuby, outSubw, outSubh,
                hasPixelSubrect ? 1 : 0, pxSubx, pxSuby, pxSubw, pxSubh) != 0;
        }
    }

    public static bool PackRects(
        int width,
        int height,
        Span<OracleRect> rects,
        int numNodes = 0,
        int heuristic = 0,
        bool allowOutOfMem = false)
    {
        int effectiveNodes = numNodes <= 0 ? width : numNodes;
        fixed (OracleRect* rectPtr = rects)
        {
            return NativeMethods.StbrpPackRects(
                width,
                height,
                effectiveNodes,
                heuristic,
                allowOutOfMem ? 1 : 0,
                rectPtr,
                rects.Length) != 0;
        }
    }

    public static void CompressDxtBlock(Span<byte> dest, ReadOnlySpan<byte> srcRgba, bool alpha, int mode = 0)
    {
        int requiredDest = alpha ? 16 : 8;
        if (dest.Length < requiredDest)
        {
            throw new ArgumentException($"Destination must be at least {requiredDest} bytes.", nameof(dest));
        }

        if (srcRgba.Length < 64)
        {
            throw new ArgumentException("Source RGBA 4x4 block must be at least 64 bytes.", nameof(srcRgba));
        }

        fixed (byte* dst = dest)
        fixed (byte* src = srcRgba)
        {
            NativeMethods.StbCompressDxtBlock(dst, src, alpha ? 1 : 0, mode);
        }
    }

    public static void CompressBc4Block(Span<byte> dest, ReadOnlySpan<byte> srcR)
    {
        if (dest.Length < 8)
        {
            throw new ArgumentException("Destination must be at least 8 bytes.", nameof(dest));
        }

        if (srcR.Length < 16)
        {
            throw new ArgumentException("Source R 4x4 block must be at least 16 bytes.", nameof(srcR));
        }

        fixed (byte* dst = dest)
        fixed (byte* src = srcR)
        {
            NativeMethods.StbCompressBc4Block(dst, src);
        }
    }

    public static void CompressBc5Block(Span<byte> dest, ReadOnlySpan<byte> srcRg)
    {
        if (dest.Length < 16)
        {
            throw new ArgumentException("Destination must be at least 16 bytes.", nameof(dest));
        }

        if (srcRg.Length < 32)
        {
            throw new ArgumentException("Source RG 4x4 block must be at least 32 bytes.", nameof(srcRg));
        }

        fixed (byte* dst = dest)
        fixed (byte* src = srcRg)
        {
            NativeMethods.StbCompressBc5Block(dst, src);
        }
    }

    public static float PerlinNoise3(
        float x, float y, float z,
        int xWrap = 0, int yWrap = 0, int zWrap = 0,
        int seed = 0) =>
        NativeMethods.StbPerlinNoise3Seed(x, y, z, xWrap, yWrap, zWrap, seed);

    public static float PerlinNoise3WrapNonPow2(
        float x, float y, float z,
        int xWrap, int yWrap, int zWrap,
        byte seed = 0) =>
        NativeMethods.StbPerlinNoise3WrapNonPow2(x, y, z, xWrap, yWrap, zWrap, seed);

    public static float PerlinNoise3Derivatives(
        float x, float y, float z,
        int xWrap, int yWrap, int zWrap,
        int seed,
        out float dx, out float dy, out float dz) =>
        NativeMethods.StbPerlinNoise3Derivatives(x, y, z, xWrap, yWrap, zWrap, seed, out dx, out dy, out dz);

    public static float PerlinRidgeNoise3(
        float x, float y, float z,
        float lacunarity, float gain, float offset,
        int octaves) =>
        NativeMethods.StbPerlinRidgeNoise3(x, y, z, lacunarity, gain, offset, octaves);

    public static float PerlinFbmNoise3(
        float x, float y, float z,
        float lacunarity, float gain,
        int octaves) =>
        NativeMethods.StbPerlinFbmNoise3(x, y, z, lacunarity, gain, octaves);

    public static float PerlinTurbulenceNoise3(
        float x, float y, float z,
        float lacunarity, float gain,
        int octaves) =>
        NativeMethods.StbPerlinTurbulenceNoise3(x, y, z, lacunarity, gain, octaves);

    public static void CompressDxtImage(ReadOnlySpan<byte> srcRgba, int width, int height, Span<byte> dest, bool alpha, int mode = 0)
    {
        fixed (byte* src = srcRgba)
        fixed (byte* dst = dest)
        {
            NativeMethods.StbCompressDxtImage(src, width, height, dst, alpha ? 1 : 0, mode);
        }
    }

    public static void CompressBc4Image(ReadOnlySpan<byte> srcR, int width, int height, Span<byte> dest)
    {
        fixed (byte* src = srcR)
        fixed (byte* dst = dest)
        {
            NativeMethods.StbCompressBc4Image(src, width, height, dst);
        }
    }

    public static void CompressBc5Image(ReadOnlySpan<byte> srcRg, int width, int height, Span<byte> dest)
    {
        fixed (byte* src = srcRg)
        fixed (byte* dst = dest)
        {
            NativeMethods.StbCompressBc5Image(src, width, height, dst);
        }
    }

    public static void PerlinFillNoise2D(
        Span<float> dest,
        int width,
        int height,
        float startX,
        float startY,
        float stepX,
        float stepY,
        float z = 0f,
        int xWrap = 0,
        int yWrap = 0,
        int zWrap = 0,
        int seed = 0)
    {
        fixed (float* dst = dest)
        {
            NativeMethods.StbPerlinFillNoise2D(dst, width, height, startX, startY, stepX, stepY, z, xWrap, yWrap, zWrap, seed);
        }
    }

    public static void PerlinFillFbm2D(
        Span<float> dest,
        int width,
        int height,
        float startX,
        float startY,
        float stepX,
        float stepY,
        float z,
        float lacunarity,
        float gain,
        int octaves)
    {
        fixed (float* dst = dest)
        {
            NativeMethods.StbPerlinFillFbm2D(dst, width, height, startX, startY, stepX, stepY, z, lacunarity, gain, octaves);
        }
    }

    public static void PerlinFillRidge2D(
        Span<float> dest,
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
        int octaves)
    {
        fixed (float* dst = dest)
        {
            NativeMethods.StbPerlinFillRidge2D(dst, width, height, startX, startY, stepX, stepY, z, lacunarity, gain, offset, octaves);
        }
    }

    public static void PerlinFillTurbulence2D(
        Span<float> dest,
        int width,
        int height,
        float startX,
        float startY,
        float stepX,
        float stepY,
        float z,
        float lacunarity,
        float gain,
        int octaves)
    {
        fixed (float* dst = dest)
        {
            NativeMethods.StbPerlinFillTurbulence2D(dst, width, height, startX, startY, stepX, stepY, z, lacunarity, gain, octaves);
        }
    }

    public static int GetNumberOfFonts(ReadOnlySpan<byte> fontData)
    {
        if (fontData.IsEmpty)
        {
            return 0;
        }

        fixed (byte* src = fontData)
        {
            return NativeMethods.StbttGetNumberOfFonts(src);
        }
    }

    public static int GetFontOffsetForIndex(ReadOnlySpan<byte> fontData, int index)
    {
        if (fontData.IsEmpty)
        {
            return -1;
        }

        fixed (byte* src = fontData)
        {
            return NativeMethods.StbttGetFontOffsetForIndex(src, index);
        }
    }

    public static int FindMatchingFont(ReadOnlySpan<byte> fontData, string name, int flags)
    {
        if (fontData.IsEmpty)
        {
            return -1;
        }

        byte[] utf8 = new byte[System.Text.Encoding.UTF8.GetByteCount(name) + 1];
        System.Text.Encoding.UTF8.GetBytes(name, utf8);
        fixed (byte* src = fontData)
        fixed (byte* namePtr = utf8)
        {
            return NativeMethods.StbttFindMatchingFont(src, namePtr, flags);
        }
    }

    public static bool CompareUtf8ToUtf16BigEndian(ReadOnlySpan<byte> utf8, ReadOnlySpan<byte> utf16Be)
    {
        fixed (byte* p1 = utf8)
        fixed (byte* p2 = utf16Be)
        {
            return NativeMethods.StbttCompareUtf8ToUtf16Be(p1, utf8.Length, p2, utf16Be.Length) != 0;
        }
    }

    public static (float Ascent, float Descent, float LineGap) GetScaledFontVMetrics(
        ReadOnlySpan<byte> fontData,
        int index,
        float size)
    {
        fixed (byte* src = fontData)
        {
            NativeMethods.StbttGetScaledFontVMetrics(src, index, size, out float ascent, out float descent, out float lineGap);
            return (ascent, descent, lineGap);
        }
    }

    public static OracleTrueTypeFont? OpenFont(ReadOnlySpan<byte> fontData, int offset = 0)
    {
        if (fontData.IsEmpty)
        {
            return null;
        }

        fixed (byte* src = fontData)
        {
            nint handle = NativeMethods.StbttFontCreate(src, fontData.Length, offset);
            return handle == nint.Zero ? null : new OracleTrueTypeFont(handle);
        }
    }

    public static int BakeFontBitmap(
        ReadOnlySpan<byte> fontData,
        int offset,
        float pixelHeight,
        Span<byte> pixels,
        int width,
        int height,
        int firstChar,
        Span<OracleBakedChar> charData)
    {
        fixed (byte* src = fontData)
        fixed (byte* dst = pixels)
        fixed (OracleBakedChar* chars = charData)
        {
            return NativeMethods.StbttBakeFontBitmap(
                src, offset, pixelHeight, dst, width, height, firstChar, charData.Length, chars);
        }
    }

    public static OracleAlignedQuad GetBakedQuad(
        ReadOnlySpan<OracleBakedChar> charData,
        int pw,
        int ph,
        int charIndex,
        ref float xpos,
        ref float ypos,
        bool openGlFillRule = true)
    {
        fixed (OracleBakedChar* chars = charData)
        {
            NativeMethods.StbttGetBakedQuad(chars, pw, ph, charIndex, ref xpos, ref ypos, out OracleAlignedQuad q, openGlFillRule ? 1 : 0);
            return q;
        }
    }

    public static bool PackFontRange(
        ReadOnlySpan<byte> fontData,
        int fontIndex,
        float fontSize,
        Span<byte> pixels,
        int width,
        int height,
        int strideInBytes,
        int padding,
        uint hOversample,
        uint vOversample,
        bool skipMissing,
        int firstChar,
        Span<OraclePackedChar> charData)
    {
        fixed (byte* src = fontData)
        fixed (byte* dst = pixels)
        fixed (OraclePackedChar* chars = charData)
        {
            return NativeMethods.StbttPackFontRange(
                src, fontIndex, fontSize, dst, width, height, strideInBytes, padding,
                hOversample, vOversample, skipMissing ? 1 : 0, firstChar, charData.Length, chars) != 0;
        }
    }

    public static bool PackFontCodepoints(
        ReadOnlySpan<byte> fontData,
        int fontIndex,
        float fontSize,
        Span<byte> pixels,
        int width,
        int height,
        int strideInBytes,
        int padding,
        uint hOversample,
        uint vOversample,
        bool skipMissing,
        ReadOnlySpan<int> codepoints,
        Span<OraclePackedChar> charData)
    {
        fixed (byte* src = fontData)
        fixed (byte* dst = pixels)
        fixed (int* cps = codepoints)
        fixed (OraclePackedChar* chars = charData)
        {
            return NativeMethods.StbttPackFontCodepoints(
                src, fontIndex, fontSize, dst, width, height, strideInBytes, padding,
                hOversample, vOversample, skipMissing ? 1 : 0, cps, codepoints.Length, chars) != 0;
        }
    }

    public static OracleAlignedQuad GetPackedQuad(
        ReadOnlySpan<OraclePackedChar> charData,
        int pw,
        int ph,
        int charIndex,
        ref float xpos,
        ref float ypos,
        bool alignToInteger = false)
    {
        fixed (OraclePackedChar* chars = charData)
        {
            NativeMethods.StbttGetPackedQuad(chars, pw, ph, charIndex, ref xpos, ref ypos, out OracleAlignedQuad q, alignToInteger ? 1 : 0);
            return q;
        }
    }

    private static void SetLoadThreadFlags(bool flipVertically, bool unpremultiply, bool convertIphonePngToRgb)
    {
        NativeMethods.StbiSetFlipVerticallyOnLoadThread(flipVertically ? 1 : 0);
        NativeMethods.StbiSetUnpremultiplyOnLoadThread(unpremultiply ? 1 : 0);
        NativeMethods.StbiConvertIphonePngToRgbThread(convertIphonePngToRgb ? 1 : 0);
    }

    private static void ResetLoadThreadFlags()
    {
        NativeMethods.StbiSetFlipVerticallyOnLoadThread(0);
        NativeMethods.StbiSetUnpremultiplyOnLoadThread(0);
        NativeMethods.StbiConvertIphonePngToRgbThread(0);
    }

    private static byte[] CopyAndFree(byte* ptr, int length, string operation)
    {
        if (ptr is null)
        {
            throw new InvalidOperationException($"Native oracle {operation} failed.");
        }

        try
        {
            byte[] result = GC.AllocateUninitializedArray<byte>(length);
            new ReadOnlySpan<byte>(ptr, length).CopyTo(result);
            return result;
        }
        finally
        {
            NativeMethods.Free(ptr);
        }
    }
}
