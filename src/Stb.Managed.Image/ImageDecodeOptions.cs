using Stb.Managed.Internal;

namespace Stb.Managed;

/// <summary>
/// Options controlling image decoding behavior in <see cref="StbImage"/>.
/// </summary>
public sealed record ImageDecodeOptions
{
    private readonly StbOptions _options = StbOptions.Default;
    private readonly float _hdrToLdrGamma = 2.2f;
    private readonly float _hdrToLdrScale = 1.0f;
    private readonly float _ldrToHdrGamma = 2.2f;
    private readonly float _ldrToHdrScale = 1.0f;

    /// <summary>
    /// Gets the default decoding options (matching native <c>stb_image</c> defaults with safe limits).
    /// </summary>
    public static ImageDecodeOptions Default { get; } = new();

    /// <summary>
    /// Gets the shared execution options (safety limits and parallelism).
    /// </summary>
    public StbOptions Options
    {
        get => _options;
        init => _options = Guard.ThrowIfNull(value, nameof(value));
    }

    /// <summary>
    /// Gets a value indicating whether to flip the decoded image vertically so the first row in memory
    /// is the bottom scanline (matching <c>stbi_set_flip_vertically_on_load</c>).
    /// </summary>
    public bool FlipVertically { get; init; }

    /// <summary>
    /// Gets a value indicating whether to convert Apple CgBI (iPhone) PNGs from BGR/BGRA to RGB/RGBA
    /// (matching <c>stbi_convert_iphone_png_to_rgb</c>).
    /// </summary>
    public bool ConvertIphonePngToRgb { get; init; }

    /// <summary>
    /// Gets a value indicating whether to unpremultiply alpha when converting Apple CgBI PNGs
    /// (matching <c>stbi_set_unpremultiply_on_load</c>).
    /// </summary>
    public bool UnpremultiplyAlpha { get; init; }

    /// <summary>
    /// Gets a value indicating whether to verify format-level integrity checksums (PNG chunk CRC-32 and
    /// zlib Adler-32). Defaults to <see langword="false"/> to match <c>stb_image</c>.
    /// </summary>
    public bool VerifyChecksums { get; init; }

    /// <summary>
    /// Gets the gamma value applied when converting HDR float images to 8/16-bit LDR
    /// (matching <c>stbi_hdr_to_ldr_gamma</c>). Defaults to <c>2.2</c>.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">Thrown when the value is not a finite positive number.</exception>
    public float HdrToLdrGamma
    {
        get => _hdrToLdrGamma;
        init => _hdrToLdrGamma = ValidatePositiveFinite(value, nameof(HdrToLdrGamma));
    }

    /// <summary>
    /// Gets the scale factor applied when converting HDR float images to 8/16-bit LDR
    /// (matching <c>stbi_hdr_to_ldr_scale</c>). Defaults to <c>1.0</c>.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">Thrown when the value is not a finite positive number.</exception>
    public float HdrToLdrScale
    {
        get => _hdrToLdrScale;
        init => _hdrToLdrScale = ValidatePositiveFinite(value, nameof(HdrToLdrScale));
    }

    /// <summary>
    /// Gets the gamma value applied when converting 8-bit LDR images to linear HDR floats
    /// (matching <c>stbi_ldr_to_hdr_gamma</c>). Defaults to <c>2.2</c>.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">Thrown when the value is not a finite positive number.</exception>
    public float LdrToHdrGamma
    {
        get => _ldrToHdrGamma;
        init => _ldrToHdrGamma = ValidatePositiveFinite(value, nameof(LdrToHdrGamma));
    }

    /// <summary>
    /// Gets the scale factor applied when converting 8-bit LDR images to linear HDR floats
    /// (matching <c>stbi_ldr_to_hdr_scale</c>). Defaults to <c>1.0</c>.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">Thrown when the value is not a finite positive number.</exception>
    public float LdrToHdrScale
    {
        get => _ldrToHdrScale;
        init => _ldrToHdrScale = ValidatePositiveFinite(value, nameof(LdrToHdrScale));
    }

    private static float ValidatePositiveFinite(float value, string paramName)
    {
        if (float.IsNaN(value) || float.IsInfinity(value) || value <= 0f)
        {
            throw new ArgumentOutOfRangeException(paramName, value, "Value must be a finite positive number.");
        }

        return value;
    }
}
