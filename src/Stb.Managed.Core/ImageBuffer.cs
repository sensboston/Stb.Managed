using System.Runtime.InteropServices;
using Stb.Managed.Internal;

namespace Stb.Managed;

/// <summary>
/// Owns a tightly packed, row-major pixel buffer decoded or produced by Stb.Managed.
/// </summary>
/// <typeparam name="T">The unmanaged channel sample type (typically <see cref="byte"/>, <see cref="ushort"/>, or <see cref="float"/>).</typeparam>
public sealed class ImageBuffer<T>
    where T : unmanaged
{
    private readonly T[] _data;

    /// <summary>
    /// Initializes a new instance of the <see cref="ImageBuffer{T}"/> class wrapping an existing array.
    /// </summary>
    /// <param name="width">The image width in pixels.</param>
    /// <param name="height">The image height in pixels.</param>
    /// <param name="components">The number of color components stored in <paramref name="data"/>.</param>
    /// <param name="sourceComponents">The original number of color components present in the source image.</param>
    /// <param name="data">The backing sample array (must have length at least <c>width * height * (int)components</c>).</param>
    /// <exception cref="ArgumentNullException">Thrown if <paramref name="data"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentOutOfRangeException">Thrown if dimensions or components are invalid.</exception>
    /// <exception cref="ArgumentException">Thrown if <paramref name="data"/> is too small for the image dimensions.</exception>
    public ImageBuffer(
        int width,
        int height,
        ColorComponents components,
        ColorComponents sourceComponents,
        T[] data)
    {
        Guard.ThrowIfNull(data, nameof(data));
        Guard.ThrowIfNegativeOrZero(width, nameof(width));
        Guard.ThrowIfNegativeOrZero(height, nameof(height));

        int channelCount = (int)components;
        if (channelCount < 1 || channelCount > 4)
        {
            throw new ArgumentOutOfRangeException(
                nameof(components),
                components,
                "Components must be between Grey (1) and RedGreenBlueAlpha (4).");
        }

        int sourceCount = (int)sourceComponents;
        if (sourceCount < 1 || sourceCount > 4)
        {
            throw new ArgumentOutOfRangeException(
                nameof(sourceComponents),
                sourceComponents,
                "SourceComponents must be between Grey (1) and RedGreenBlueAlpha (4).");
        }

        long expectedLength = (long)width * height * channelCount;
        if (data.Length < expectedLength)
        {
            throw new ArgumentException(
                $"Backing buffer length {data.Length} is smaller than required {expectedLength} samples for {width}x{height}x{channelCount}.",
                nameof(data));
        }

        Width = width;
        Height = height;
        Components = components;
        SourceComponents = sourceComponents;
        RowStride = width * channelCount;
        _data = data;
    }

    /// <summary>
    /// Gets the image width in pixels.
    /// </summary>
    public int Width { get; }

    /// <summary>
    /// Gets the image height in pixels.
    /// </summary>
    public int Height { get; }

    /// <summary>
    /// Gets the number of color components stored per pixel in this buffer.
    /// </summary>
    public ColorComponents Components { get; }

    /// <summary>
    /// Gets the original number of color components stored in the encoded source image.
    /// </summary>
    public ColorComponents SourceComponents { get; }

    /// <summary>
    /// Gets the number of <typeparamref name="T"/> samples per scanline (<c>Width * (int)Components</c>).
    /// </summary>
    public int RowStride { get; }

    /// <summary>
    /// Gets the total number of <typeparamref name="T"/> samples in the image (<c>RowStride * Height</c>).
    /// </summary>
    public int SampleCount => RowStride * Height;

    /// <summary>
    /// Gets a <see cref="Memory{T}"/> view over the tightly packed pixel samples.
    /// </summary>
    public Memory<T> Memory => _data.AsMemory(0, SampleCount);

    /// <summary>
    /// Gets a <see cref="ReadOnlyMemory{T}"/> view over the tightly packed pixel samples.
    /// </summary>
    public ReadOnlyMemory<T> ReadOnlyMemory => _data.AsMemory(0, SampleCount);

    /// <summary>
    /// Returns a writable <see cref="Span{T}"/> over all pixel samples in row-major order.
    /// </summary>
    /// <returns>A span of length <see cref="SampleCount"/>.</returns>
    public Span<T> AsSpan() => _data.AsSpan(0, SampleCount);

    /// <summary>
    /// Returns a read-only <see cref="ReadOnlySpan{T}"/> over all pixel samples in row-major order.
    /// </summary>
    /// <returns>A read-only span of length <see cref="SampleCount"/>.</returns>
    public ReadOnlySpan<T> AsReadOnlySpan() => _data.AsSpan(0, SampleCount);

    /// <summary>
    /// Returns a writable <see cref="Span{T}"/> for scanline <paramref name="row"/> (top-to-bottom).
    /// </summary>
    /// <param name="row">The zero-based scanline index (<c>0 &lt;= row &lt; Height</c>).</param>
    /// <returns>A span of length <see cref="RowStride"/>.</returns>
    /// <exception cref="ArgumentOutOfRangeException">Thrown when <paramref name="row"/> is outside <c>[0, Height)</c>.</exception>
    public Span<T> GetRow(int row)
    {
        Guard.ThrowIfIndexOutOfRange(row, Height, nameof(row));
        return _data.AsSpan(row * RowStride, RowStride);
    }

    /// <summary>
    /// Returns the underlying backing array.
    /// </summary>
    /// <returns>The backing <typeparamref name="T"/> array.</returns>
    public T[] GetRawArray() => _data;
}

/// <summary>
/// Factory methods for creating safety-checked <see cref="ImageBuffer{T}"/> instances.
/// </summary>
public static class ImageBuffer
{
    /// <summary>
    /// Allocates a new <see cref="ImageBuffer{T}"/> after validating dimensions and allocation size
    /// against <paramref name="limits"/>.
    /// </summary>
    /// <typeparam name="T">The unmanaged channel sample type.</typeparam>
    /// <param name="width">The image width in pixels.</param>
    /// <param name="height">The image height in pixels.</param>
    /// <param name="components">The output color components per pixel.</param>
    /// <param name="sourceComponents">The original source image color components.</param>
    /// <param name="limits">Optional safety limits; defaults to <see cref="StbLimits.Default"/>.</param>
    /// <param name="zeroInitialize">
    /// When <see langword="true"/>, returns a zero-initialized array; when <see langword="false"/>,
    /// allocates an uninitialized array on .NET 8+ for callers that overwrite every sample.
    /// </param>
    /// <returns>The newly allocated <see cref="ImageBuffer{T}"/>.</returns>
    /// <exception cref="StbException">Thrown when dimensions, components, or allocation size violate limits.</exception>
    public static ImageBuffer<T> Allocate<T>(
        int width,
        int height,
        ColorComponents components,
        ColorComponents sourceComponents,
        StbLimits? limits = null,
        bool zeroInitialize = false)
        where T : unmanaged
    {
        int channelCount = (int)components;
        if (channelCount < 1 || channelCount > 4)
        {
            throw new StbException(
                StbErrorCode.InvalidArgument,
                $"Components must be between Grey (1) and RedGreenBlueAlpha (4), got {components}.");
        }

        int sourceCount = (int)sourceComponents;
        if (sourceCount < 1 || sourceCount > 4)
        {
            throw new StbException(
                StbErrorCode.InvalidArgument,
                $"SourceComponents must be between Grey (1) and RedGreenBlueAlpha (4), got {sourceComponents}.");
        }

        StbLimits effectiveLimits = limits ?? StbLimits.Default;
        int elementSize = ElementSizeOf<T>();
        int bytesPerPixel = checked(channelCount * elementSize);
        int totalBytes = effectiveLimits.ValidateDimensions(width, height, bytesPerPixel);
        int sampleCount = totalBytes / elementSize;

        T[] data = zeroInitialize
            ? new T[sampleCount]
            : ArrayAllocation.AllocateUninitialized<T>(sampleCount);

        return new ImageBuffer<T>(width, height, components, sourceComponents, data);
    }

    private static int ElementSizeOf<T>()
        where T : unmanaged
    {
        if (typeof(T) == typeof(byte) || typeof(T) == typeof(sbyte))
        {
            return 1;
        }

        if (typeof(T) == typeof(ushort) || typeof(T) == typeof(short))
        {
            return 2;
        }

        if (typeof(T) == typeof(uint) || typeof(T) == typeof(int) || typeof(T) == typeof(float))
        {
            return 4;
        }

        if (typeof(T) == typeof(ulong) || typeof(T) == typeof(long) || typeof(T) == typeof(double))
        {
            return 8;
        }

        return MemoryMarshal.AsBytes(new T[1].AsSpan()).Length;
    }
}
