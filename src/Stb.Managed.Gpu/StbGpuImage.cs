using ILGPU;
using ILGPU.Runtime;
using Stb.Managed.Dxt;
using Stb.Managed.Gpu.Internal;

namespace Stb.Managed.Gpu;

/// <summary>
/// VRAM-resident image buffer owned by an <see cref="StbGpuContext"/>, enabling chained GPU operations
/// (e.g., resize → color conversion → DXT compression, or Perlin → heightmap → resize → DXT)
/// without intermediate host↔device PCIe transfers.
/// </summary>
/// <typeparam name="T">Unmanaged element type (<see cref="byte"/> or <see cref="float"/>).</typeparam>
public sealed class StbGpuImage<T> : IDisposable
    where T : unmanaged
{
    private PooledGpuBuffer<T>? _buffer;

    internal StbGpuImage(
        StbGpuContext context,
        PooledGpuBuffer<T> buffer,
        int width,
        int height,
        ColorComponents components)
    {
        Context = context;
        _buffer = buffer;
        Width = width;
        Height = height;
        Components = components;
    }

    /// <summary>Gets the owning <see cref="StbGpuContext"/>.</summary>
    public StbGpuContext Context { get; }

    /// <summary>Gets the image width in pixels.</summary>
    public int Width { get; }

    /// <summary>Gets the image height in pixels.</summary>
    public int Height { get; }

    /// <summary>Gets the channel layout of the image.</summary>
    public ColorComponents Components { get; }

    /// <summary>Gets the number of channels per pixel.</summary>
    public int ChannelCount => (int)Components;

    /// <summary>Gets the total number of elements (<c>Width * Height * ChannelCount</c>).</summary>
    public long ElementCount => (long)Width * Height * ChannelCount;

    /// <summary>Gets whether this image has already been disposed.</summary>
    public bool IsDisposed => _buffer is null;

    internal ArrayView1D<T, Stride1D.Dense> View
    {
        get
        {
            PooledGpuBuffer<T>? b = _buffer;
            ObjectDisposedException.ThrowIf(b is null, this);
            return b.View;
        }
    }

    /// <summary>
    /// Downloads the VRAM image into a newly allocated host array.
    /// </summary>
    /// <returns>Host array containing all <see cref="ElementCount"/> elements.</returns>
    public T[] DownloadToArray()
    {
        PooledGpuBuffer<T>? b = _buffer;
        ObjectDisposedException.ThrowIf(b is null, this);
        return Context.DownloadArray(b.View, ElementCount);
    }

    /// <summary>
    /// Downloads the VRAM image into a caller-provided host span.
    /// </summary>
    /// <param name="destination">Destination span (must be at least <see cref="ElementCount"/> elements).</param>
    public void DownloadToSpan(Span<T> destination)
    {
        PooledGpuBuffer<T>? b = _buffer;
        ObjectDisposedException.ThrowIf(b is null, this);
        if (destination.Length < ElementCount)
        {
            throw new ArgumentException(
                $"Destination span length ({destination.Length}) is smaller than ElementCount ({ElementCount}).",
                nameof(destination));
        }

        Context.DownloadSpan(b.View, destination.Slice(0, (int)ElementCount));
    }

    /// <summary>
    /// Downloads the VRAM image into an <see cref="ImageBuffer{T}"/>.
    /// </summary>
    /// <returns>A new host <see cref="ImageBuffer{T}"/>.</returns>
    public ImageBuffer<T> DownloadToBuffer()
    {
        T[] data = DownloadToArray();
        return new ImageBuffer<T>(Width, Height, Components, Components, data);
    }

    /// <summary>
    /// Returns the underlying VRAM buffer to the owning context's <see cref="GpuBufferPool"/>.
    /// </summary>
    public void Dispose()
    {
        PooledGpuBuffer<T>? buf = Interlocked.Exchange(ref _buffer, null);
        buf?.Dispose();
    }
}

/// <summary>
/// Convenience VRAM-chaining extension methods for <see cref="StbGpuImage{T}"/>.
/// </summary>
public static class StbGpuImageExtensions
{
    /// <summary>
    /// Resizes a VRAM-resident 8-bit image directly into a new VRAM-resident image (zero host roundtrip).
    /// </summary>
    public static StbGpuImage<byte> ResizeOnGpu(
        this StbGpuImage<byte> source,
        int outputWidth,
        int outputHeight,
        ResizeDataType dataType = ResizeDataType.UInt8Srgb,
        ResizeFilter filter = ResizeFilter.Default,
        ResizeEdge edge = ResizeEdge.Clamp)
    {
        ArgumentNullException.ThrowIfNull(source);
        return StbGpuImageResize.ResizeGpuImage(source.Context, source, outputWidth, outputHeight, dataType, filter, edge);
    }

    /// <summary>
    /// Resizes a VRAM-resident 32-bit float image directly into a new VRAM-resident image (zero host roundtrip).
    /// </summary>
    public static StbGpuImage<float> ResizeOnGpu(
        this StbGpuImage<float> source,
        int outputWidth,
        int outputHeight,
        ResizeFilter filter = ResizeFilter.Default,
        ResizeEdge edge = ResizeEdge.Clamp)
    {
        ArgumentNullException.ThrowIfNull(source);
        return StbGpuImageResize.ResizeGpuImage(source.Context, source, outputWidth, outputHeight, filter, edge);
    }

    /// <summary>
    /// Compresses a VRAM-resident 8-bit image directly to BC1/BC3/BC4/BC5 blocks on GPU without re-uploading pixel data.
    /// </summary>
    public static byte[] CompressDxtOnGpu(
        this StbGpuImage<byte> source,
        DxtFormat format,
        DxtCompressionMode mode = DxtCompressionMode.Normal)
    {
        ArgumentNullException.ThrowIfNull(source);
        return StbGpuDxt.CompressGpuImage(source.Context, source, format, mode);
    }

    /// <summary>
    /// Swaps R and B channels (RGBA ↔ BGRA) directly in VRAM, returning a new VRAM-resident image.
    /// </summary>
    public static StbGpuImage<byte> SwapRedBlueOnGpu(this StbGpuImage<byte> source)
    {
        ArgumentNullException.ThrowIfNull(source);
        return StbGpuColor.SwapRedBlueGpuImage(source.Context, source);
    }

    /// <summary>
    /// Converts a VRAM-resident float heightmap (e.g. from <see cref="StbGpuPerlin"/>) into a VRAM-resident RGBA8 image.
    /// </summary>
    public static StbGpuImage<byte> ToRgbaOnGpu(
        this StbGpuImage<float> heightmap,
        float minVal = -1.0f,
        float maxVal = 1.0f,
        byte alpha = 255)
    {
        ArgumentNullException.ThrowIfNull(heightmap);
        return StbGpuColor.FloatHeightmapToRgbaGpuImage(heightmap.Context, heightmap, minVal, maxVal, alpha);
    }

    /// <summary>
    /// Converts a VRAM-resident float heightmap (e.g. from <see cref="StbGpuPerlin"/>) into a VRAM-resident 8-bit greyscale image
    /// (ready for BC4 compression via <see cref="CompressDxtOnGpu"/>).
    /// </summary>
    public static StbGpuImage<byte> ToGreyOnGpu(
        this StbGpuImage<float> heightmap,
        float minVal = -1.0f,
        float maxVal = 1.0f)
    {
        ArgumentNullException.ThrowIfNull(heightmap);
        return StbGpuColor.FloatHeightmapToGreyGpuImage(heightmap.Context, heightmap, minVal, maxVal);
    }
}
