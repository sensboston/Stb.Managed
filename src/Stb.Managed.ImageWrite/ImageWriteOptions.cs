using Stb.Managed.Internal;

namespace Stb.Managed;

/// <summary>
/// Options controlling image encoding behavior in <see cref="StbImageWrite"/>.
/// </summary>
public sealed record ImageWriteOptions
{
    private readonly StbOptions _options = StbOptions.Default;
    private readonly int _pngCompressionLevel = -1;
    private readonly PngFilterMode _pngFilter = PngFilterMode.Auto;
    private readonly PngCompressionEngine _pngCompressionEngine = PngCompressionEngine.ZLib;

    /// <summary>
    /// Gets the default encoding options.
    /// </summary>
    public static ImageWriteOptions Default { get; } = new();

    /// <summary>
    /// Gets the shared execution options (safety limits and parallelism).
    /// </summary>
    public StbOptions Options
    {
        get => _options;
        init => _options = Guard.ThrowIfNull(value, nameof(value));
    }

    /// <summary>
    /// Gets a value indicating whether to flip rows vertically while writing so the last row in the input
    /// buffer becomes the top scanline of the output image (matching <c>stbi_flip_vertically_on_write</c>).
    /// </summary>
    public bool FlipVertically { get; init; }

    /// <summary>
    /// Gets the PNG zlib compression effort level. The default, <c>-1</c>, selects the engine's own default:
    /// <c>6</c> for <see cref="PngCompressionEngine.ZLib"/> (the standard zlib default, which already compresses
    /// noticeably smaller than <c>stb</c> at a fraction of the time of higher levels) and <c>8</c> for
    /// <see cref="PngCompressionEngine.StbCompatible"/> (matching <c>stbi_write_png_compression_level</c>, so the
    /// output is byte-identical to native <c>stb</c>).
    /// For <see cref="PngCompressionEngine.ZLib"/>, <c>0</c> disables compression and <c>1..9</c> increase effort;
    /// for <see cref="PngCompressionEngine.StbCompatible"/>, values below <c>5</c> are clamped to <c>5</c> as in native <c>stb</c>.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">Thrown when less than <c>-1</c>.</exception>
    public int PngCompressionLevel
    {
        get => _pngCompressionLevel;
        init
        {
            if (value < -1)
            {
                throw new ArgumentOutOfRangeException(nameof(PngCompressionLevel), value, "Use -1 for the engine default or 0..9.");
            }

            _pngCompressionLevel = value;
        }
    }

    internal int ResolvedPngCompressionLevel =>
        _pngCompressionLevel >= 0 ? _pngCompressionLevel : _pngCompressionEngine == PngCompressionEngine.StbCompatible ? 8 : 6;

    /// <summary>
    /// Gets the PNG scanline filter selection mode. Defaults to <see cref="PngFilterMode.Auto"/>.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">Thrown when outside the defined <see cref="PngFilterMode"/> values.</exception>
    public PngFilterMode PngFilter
    {
        get => _pngFilter;
        init
        {
            if ((int)value < (int)PngFilterMode.Auto || (int)value > (int)PngFilterMode.Paeth)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(PngFilter),
                    value,
                    "PngFilter must be Auto (-1) or between None (0) and Paeth (4).");
            }

            _pngFilter = value;
        }
    }

    /// <summary>
    /// Gets the zlib compression backend used for PNG encoding.
    /// Defaults to <see cref="PngCompressionEngine.ZLib"/>; set to <see cref="PngCompressionEngine.StbCompatible"/>
    /// for byte-for-byte identical output with native <c>stb_image_write</c>.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">Thrown when outside the defined <see cref="PngCompressionEngine"/> values.</exception>
    public PngCompressionEngine PngCompressionEngine
    {
        get => _pngCompressionEngine;
        init
        {
            if (value != PngCompressionEngine.ZLib && value != PngCompressionEngine.StbCompatible)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(PngCompressionEngine),
                    value,
                    "PngCompressionEngine must be ZLib or StbCompatible.");
            }

            _pngCompressionEngine = value;
        }
    }

    /// <summary>
    /// Gets a value indicating whether to enable RLE packet compression when writing TGA images
    /// (default <see langword="true"/>, matching <c>stbi_write_tga_with_rle</c>).
    /// </summary>
    public bool TgaUseRle { get; init; } = true;
}
