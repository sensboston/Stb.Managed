namespace Stb.Managed;

/// <summary>
/// Specifies channel count, channel ordering, and alpha premultiplication for image resampling,
/// matching <c>stbir_pixel_layout</c> in <c>stb_image_resize2.h</c>.
/// </summary>
public enum ResizePixelLayout
{
    /// <summary>
    /// Three color channels in BGR order without alpha.
    /// </summary>
    Bgr = 0,

    /// <summary>
    /// Single-channel image (no alpha weighting).
    /// </summary>
    OneChannel = 1,

    /// <summary>
    /// Two generic channels (no alpha weighting).
    /// </summary>
    TwoChannel = 2,

    /// <summary>
    /// Three color channels in RGB order without alpha.
    /// </summary>
    Rgb = 3,

    /// <summary>
    /// Four channels in RGBA order with straight (non-premultiplied) alpha.
    /// </summary>
    Rgba = 4,

    /// <summary>
    /// Four generic channels (no alpha weighting).
    /// </summary>
    FourChannel = 5,

    /// <summary>
    /// Four channels in BGRA order with straight (non-premultiplied) alpha.
    /// </summary>
    Bgra = 6,

    /// <summary>
    /// Four channels in ARGB order with straight (non-premultiplied) alpha.
    /// </summary>
    Argb = 7,

    /// <summary>
    /// Four channels in ABGR order with straight (non-premultiplied) alpha.
    /// </summary>
    Abgr = 8,

    /// <summary>
    /// Two channels in greyscale + straight (non-premultiplied) alpha order.
    /// </summary>
    Ra = 9,

    /// <summary>
    /// Two channels in straight (non-premultiplied) alpha + greyscale order.
    /// </summary>
    Ar = 10,

    /// <summary>
    /// Four channels in RGBA order with premultiplied alpha (or alpha with no alpha weighting).
    /// </summary>
    RgbaPremultiplied = 11,

    /// <summary>
    /// Four channels in BGRA order with premultiplied alpha (or alpha with no alpha weighting).
    /// </summary>
    BgraPremultiplied = 12,

    /// <summary>
    /// Four channels in ARGB order with premultiplied alpha (or alpha with no alpha weighting).
    /// </summary>
    ArgbPremultiplied = 13,

    /// <summary>
    /// Four channels in ABGR order with premultiplied alpha (or alpha with no alpha weighting).
    /// </summary>
    AbgrPremultiplied = 14,

    /// <summary>
    /// Two channels in greyscale + premultiplied alpha order (or alpha with no alpha weighting).
    /// </summary>
    RaPremultiplied = 15,

    /// <summary>
    /// Two channels in premultiplied alpha + greyscale order (or alpha with no alpha weighting).
    /// </summary>
    ArPremultiplied = 16,
}
