namespace Stb.Managed;

/// <summary>
/// Specifies the 1D resampling filter kernel, matching <c>stbir_filter</c> in <c>stb_image_resize2.h</c> v2.50.
/// </summary>
public enum ResizeFilter
{
    /// <summary>
    /// Automatic default filter: <see cref="CatmullRom"/> when upsampling, <see cref="Mitchell"/> when downsampling,
    /// and <see cref="PointSample"/> for an exact 1:1 grid-aligned copy (<c>STBIR_FILTER_DEFAULT</c>).
    /// </summary>
    Default = 0,

    /// <summary>
    /// Trapezoid filter with 1-pixel ramps; identical to box averaging for integer ratios (<c>STBIR_FILTER_BOX</c>).
    /// </summary>
    Box = 1,

    /// <summary>
    /// Tent / bilinear filter (<c>STBIR_FILTER_TRIANGLE</c>).
    /// </summary>
    Triangle = 2,

    /// <summary>
    /// Cubic B-spline filter (Mitchell-Netravali B=1, C=0), smooth and Gaussian-like (<c>STBIR_FILTER_CUBICBSPLINE</c>).
    /// </summary>
    CubicBSpline = 3,

    /// <summary>
    /// Catmull-Rom interpolating cubic spline (B=0, C=1/2), default for upsampling (<c>STBIR_FILTER_CATMULLROM</c>).
    /// </summary>
    CatmullRom = 4,

    /// <summary>
    /// Mitchell-Netravali cubic filter (B=1/3, C=1/3), default for downsampling (<c>STBIR_FILTER_MITCHELL</c>).
    /// </summary>
    Mitchell = 5,

    /// <summary>
    /// Nearest-neighbor point sampling in both upsampling and downsampling (<c>STBIR_FILTER_POINT_SAMPLE</c>).
    /// </summary>
    PointSample = 6,

    /// <summary>
    /// Mitchell-Netravali with B=1/4, C=3/8 (Robidoux), slightly sharper than <see cref="Mitchell"/>
    /// (<c>STBIR_FILTER_MITCHELL_QUARTER</c>).
    /// </summary>
    MitchellQuarter = 7,

    /// <summary>
    /// Mitchell-Netravali with B=1/6, C=5/12 (<c>STBIR_FILTER_MITCHELL_SIXTH</c>).
    /// </summary>
    MitchellSixth = 8,

    /// <summary>
    /// Mitchell-Netravali with B=-1/6, C=7/12, sharpening (<c>STBIR_FILTER_MITCHELL_NEG_SIXTH</c>).
    /// </summary>
    MitchellNegSixth = 9,

    /// <summary>
    /// Mitchell-Netravali with B=-1/3, C=2/3, stronger sharpening (<c>STBIR_FILTER_MITCHELL_NEG_THIRD</c>).
    /// </summary>
    MitchellNegThird = 10,

    /// <summary>
    /// Two-lobe Lanczos filter (support 2.0, 4 taps at 1:1) (<c>STBIR_FILTER_LANCZOS4</c>).
    /// </summary>
    Lanczos4 = 11,

    /// <summary>
    /// 2.5-lobe Lanczos filter (support 2.5, 5 taps at 1:1) (<c>STBIR_FILTER_LANCZOS5</c>).
    /// </summary>
    Lanczos5 = 12,

    /// <summary>
    /// Casey Muratori's Bink 2 6-tap spline kernel (support 3.0) (<c>STBIR_FILTER_MURATORI6</c>).
    /// </summary>
    Muratori6 = 13,
}
