using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using Stb.Managed.Internal;

namespace Stb.Managed;

/// <summary>
/// Represents a single composited frame of an animated GIF image decoded by
/// <see cref="StbImage.LoadGifFrames(ReadOnlySpan{byte}, ColorComponents, ImageDecodeOptions?)"/>.
/// </summary>
public sealed class GifFrame
{
    private readonly ReadOnlyMemory<byte> _pixels;

    internal GifFrame(int width, int height, ColorComponents components, int delayMs, ReadOnlyMemory<byte> pixels)
    {
        Width = width;
        Height = height;
        Components = components;
        DelayMs = delayMs;
        _pixels = pixels;
    }

    /// <summary>
    /// Gets the frame width in pixels.
    /// </summary>
    public int Width { get; }

    /// <summary>
    /// Gets the frame height in pixels.
    /// </summary>
    public int Height { get; }

    /// <summary>
    /// Gets the number of color components per pixel stored in this frame.
    /// </summary>
    public ColorComponents Components { get; }

    /// <summary>
    /// Gets the frame display delay in milliseconds (matching <c>stbi_load_gif_from_memory</c>, which scales
    /// the GIF 1/100th-second delay by 10).
    /// </summary>
    public int DelayMs { get; }

    /// <summary>
    /// Gets a read-only memory view over this frame's tightly packed pixel bytes.
    /// </summary>
    public ReadOnlyMemory<byte> Pixels => _pixels;

    /// <summary>
    /// Returns a read-only span over this frame's tightly packed pixel bytes.
    /// </summary>
    public ReadOnlySpan<byte> AsSpan() => _pixels.Span;
}

/// <summary>
/// Represents all composited frames and per-frame delays decoded from a GIF image.
/// </summary>
public sealed class GifAnimation
{
    private readonly byte[] _allPixels;
    private readonly ReadOnlyCollection<GifFrame> _frames;

    internal GifAnimation(
        int width,
        int height,
        ColorComponents components,
        ColorComponents sourceComponents,
        byte[] allPixels,
        GifFrame[] frames)
    {
        Width = width;
        Height = height;
        Components = components;
        SourceComponents = sourceComponents;
        _allPixels = Guard.ThrowIfNull(allPixels, nameof(allPixels));
        _frames = new ReadOnlyCollection<GifFrame>(Guard.ThrowIfNull(frames, nameof(frames)));
    }

    /// <summary>
    /// Gets the logical screen width in pixels.
    /// </summary>
    public int Width { get; }

    /// <summary>
    /// Gets the logical screen height in pixels.
    /// </summary>
    public int Height { get; }

    /// <summary>
    /// Gets the number of color components per pixel stored in every frame.
    /// </summary>
    public ColorComponents Components { get; }

    /// <summary>
    /// Gets the source component count reported for the GIF (<see cref="ColorComponents.RedGreenBlueAlpha"/>).
    /// </summary>
    public ColorComponents SourceComponents { get; }

    /// <summary>
    /// Gets the decoded frames in playback order.
    /// </summary>
    public IReadOnlyList<GifFrame> Frames => _frames;

    /// <summary>
    /// Gets the number of decoded frames.
    /// </summary>
    public int FrameCount => _frames.Count;

    /// <summary>
    /// Gets a contiguous read-only memory view of all frames laid out consecutively (matching
    /// <c>stbi_load_gif_from_memory</c>'s flat output buffer).
    /// </summary>
    public ReadOnlyMemory<byte> AllFramesPixels => _allPixels;

    /// <summary>
    /// Returns a contiguous read-only span of all frames laid out consecutively.
    /// </summary>
    public ReadOnlySpan<byte> AsSpan() => _allPixels;
}
