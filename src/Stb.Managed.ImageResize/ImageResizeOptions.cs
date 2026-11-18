using Stb.Managed.Internal;

namespace Stb.Managed;

/// <summary>
/// Configures resampling behavior for <see cref="StbImageResize"/> and <see cref="StbImageResizer"/>.
/// </summary>
public sealed record ImageResizeOptions
{
    private readonly StbOptions _options = StbOptions.Default;
    private readonly ResizeFilter _horizontalFilter = ResizeFilter.Default;
    private readonly ResizeFilter _verticalFilter = ResizeFilter.Default;
    private readonly ResizeEdge _horizontalEdge = ResizeEdge.Clamp;
    private readonly ResizeEdge _verticalEdge = ResizeEdge.Clamp;
    private readonly ResizePixelLayout? _outputLayout;
    private readonly ResizeDataType? _outputDataType;

    /// <summary>
    /// Gets the default resampling options (default filter, clamped edges, matching output layout/type,
    /// high-quality fancy alpha weighting, single-threaded).
    /// </summary>
    public static ImageResizeOptions Default { get; } = new();

    /// <summary>
    /// Gets the shared execution options (safety limits and parallelism).
    /// </summary>
    public StbOptions Options
    {
        get => _options;
        init => _options = Guard.ThrowIfNull(value, nameof(value));
    }

    /// <summary>
    /// Gets the horizontal resampling filter. Defaults to <see cref="ResizeFilter.Default"/>.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">Thrown when outside the defined <see cref="ResizeFilter"/> values.</exception>
    public ResizeFilter HorizontalFilter
    {
        get => _horizontalFilter;
        init
        {
            ValidateFilter(value, nameof(HorizontalFilter));
            _horizontalFilter = value;
        }
    }

    /// <summary>
    /// Gets the vertical resampling filter. Defaults to <see cref="ResizeFilter.Default"/>.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">Thrown when outside the defined <see cref="ResizeFilter"/> values.</exception>
    public ResizeFilter VerticalFilter
    {
        get => _verticalFilter;
        init
        {
            ValidateFilter(value, nameof(VerticalFilter));
            _verticalFilter = value;
        }
    }

    /// <summary>
    /// Gets the horizontal edge handling mode. Defaults to <see cref="ResizeEdge.Clamp"/>.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">Thrown when outside the defined <see cref="ResizeEdge"/> values.</exception>
    public ResizeEdge HorizontalEdge
    {
        get => _horizontalEdge;
        init
        {
            ValidateEdge(value, nameof(HorizontalEdge));
            _horizontalEdge = value;
        }
    }

    /// <summary>
    /// Gets the vertical edge handling mode. Defaults to <see cref="ResizeEdge.Clamp"/>.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">Thrown when outside the defined <see cref="ResizeEdge"/> values.</exception>
    public ResizeEdge VerticalEdge
    {
        get => _verticalEdge;
        init
        {
            ValidateEdge(value, nameof(VerticalEdge));
            _verticalEdge = value;
        }
    }

    /// <summary>
    /// Gets an optional output pixel layout differing from the input layout (for example,
    /// <see cref="ResizePixelLayout.Rgb"/> to <see cref="ResizePixelLayout.Bgr"/> or
    /// <see cref="ResizePixelLayout.Rgba"/> to <see cref="ResizePixelLayout.RgbaPremultiplied"/>).
    /// Channel counts must match. When <see langword="null"/>, the input layout is preserved.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">Thrown when outside the defined <see cref="ResizePixelLayout"/> values.</exception>
    public ResizePixelLayout? OutputLayout
    {
        get => _outputLayout;
        init
        {
            if (value.HasValue)
            {
                ValidatePixelLayout(value.Value, nameof(OutputLayout));
            }

            _outputLayout = value;
        }
    }

    /// <summary>
    /// Gets an optional output sample data type differing from the input data type
    /// (for example, <see cref="ResizeDataType.UInt8Srgb"/> to <see cref="ResizeDataType.Fp32"/>).
    /// When <see langword="null"/>, the input data type is preserved.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">Thrown when outside the defined <see cref="ResizeDataType"/> values.</exception>
    public ResizeDataType? OutputDataType
    {
        get => _outputDataType;
        init
        {
            if (value.HasValue)
            {
                ValidateDataType(value.Value, nameof(OutputDataType));
            }

            _outputDataType = value;
        }
    }

    /// <summary>
    /// Gets a value indicating whether to trade zero-alpha RGB reconstruction quality for speed when both
    /// input and output use non-premultiplied alpha (matching <c>stbir_set_non_pm_alpha_speed_over_quality</c>).
    /// Default is <see langword="false"/> (high-quality 7-channel / 3-channel fancy alpha weighting).
    /// </summary>
    public bool FastAlpha { get; init; }

    /// <summary>
    /// Gets an optional user-defined horizontal filter kernel, overriding <see cref="HorizontalFilter"/>.
    /// </summary>
    public ResizeFilterKernel? HorizontalCustomFilter { get; init; }

    /// <summary>
    /// Gets an optional user-defined vertical filter kernel, overriding <see cref="VerticalFilter"/>.
    /// </summary>
    public ResizeFilterKernel? VerticalCustomFilter { get; init; }

    /// <summary>
    /// Gets an optional normalized input sub-rectangle (<c>stbir_set_input_subrect</c>).
    /// Mutually exclusive with <see cref="PixelSubrect"/>.
    /// </summary>
    public ResizeNormalizedRect? InputSubrect { get; init; }

    /// <summary>
    /// Gets an optional output pixel sub-rectangle (<c>stbir_set_output_pixel_subrect</c>).
    /// Mutually exclusive with <see cref="PixelSubrect"/>.
    /// </summary>
    public ResizeRect? OutputSubrect { get; init; }

    /// <summary>
    /// Gets an optional paired pixel sub-rectangle (<c>stbir_set_pixel_subrect</c>) that sets both
    /// the output region and the matching rational input sub-region for exact tile resampling.
    /// Mutually exclusive with <see cref="InputSubrect"/> and <see cref="OutputSubrect"/>.
    /// </summary>
    public ResizeRect? PixelSubrect { get; init; }

    internal static void ValidateFilter(ResizeFilter value, string paramName)
    {
        if ((uint)value > (uint)ResizeFilter.Muratori6)
        {
            throw new ArgumentOutOfRangeException(
                paramName,
                value,
                $"ResizeFilter must be between {ResizeFilter.Default} (0) and {ResizeFilter.Muratori6} (13).");
        }
    }

    internal static void ValidateEdge(ResizeEdge value, string paramName)
    {
        if ((uint)value > (uint)ResizeEdge.Zero)
        {
            throw new ArgumentOutOfRangeException(
                paramName,
                value,
                $"ResizeEdge must be between {ResizeEdge.Clamp} (0) and {ResizeEdge.Zero} (3).");
        }
    }

    internal static void ValidatePixelLayout(ResizePixelLayout value, string paramName)
    {
        if ((uint)value > (uint)ResizePixelLayout.ArPremultiplied)
        {
            throw new ArgumentOutOfRangeException(
                paramName,
                value,
                $"ResizePixelLayout must be between {ResizePixelLayout.Bgr} (0) and {ResizePixelLayout.ArPremultiplied} (16).");
        }
    }

    internal static void ValidateDataType(ResizeDataType value, string paramName)
    {
        if ((uint)value > (uint)ResizeDataType.Fp16)
        {
            throw new ArgumentOutOfRangeException(
                paramName,
                value,
                $"ResizeDataType must be between {ResizeDataType.UInt8Linear} (0) and {ResizeDataType.Fp16} (5).");
        }
    }
}
