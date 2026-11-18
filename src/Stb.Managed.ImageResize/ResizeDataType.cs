namespace Stb.Managed;

/// <summary>
/// Specifies the per-channel sample encoding and transfer curve for image resampling,
/// matching <c>stbir_datatype</c> in <c>stb_image_resize2.h</c>.
/// </summary>
public enum ResizeDataType
{
    /// <summary>
    /// Unsigned 8-bit linear samples (<c>STBIR_TYPE_UINT8</c>).
    /// </summary>
    UInt8Linear = 0,

    /// <summary>
    /// Unsigned 8-bit sRGB color samples with linear alpha when an alpha layout is used
    /// (<c>STBIR_TYPE_UINT8_SRGB</c>).
    /// </summary>
    UInt8Srgb = 1,

    /// <summary>
    /// Unsigned 8-bit sRGB samples across all channels including alpha
    /// (<c>STBIR_TYPE_UINT8_SRGB_ALPHA</c>).
    /// </summary>
    UInt8SrgbAlpha = 2,

    /// <summary>
    /// Unsigned 16-bit linear samples (<c>STBIR_TYPE_UINT16</c>).
    /// </summary>
    UInt16Linear = 3,

    /// <summary>
    /// Single-precision 32-bit IEEE-754 floating-point linear samples (<c>STBIR_TYPE_FLOAT</c>).
    /// </summary>
    Fp32 = 4,

    /// <summary>
    /// Half-precision 16-bit IEEE-754 floating-point linear samples stored as 16-bit bit patterns
    /// (<c>STBIR_TYPE_HALF_FLOAT</c>).
    /// </summary>
    Fp16 = 5,
}
