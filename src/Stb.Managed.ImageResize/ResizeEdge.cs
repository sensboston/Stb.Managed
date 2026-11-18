namespace Stb.Managed;

/// <summary>
/// Specifies how samples outside the input image bounds are handled during resampling,
/// matching <c>stbir_edge</c> in <c>stb_image_resize2.h</c>.
/// </summary>
public enum ResizeEdge
{
    /// <summary>
    /// Clamp out-of-bounds coordinates to the nearest edge pixel (<c>STBIR_EDGE_CLAMP</c>).
    /// </summary>
    Clamp = 0,

    /// <summary>
    /// Mirror out-of-bounds coordinates across the image border (<c>STBIR_EDGE_REFLECT</c>).
    /// </summary>
    Reflect = 1,

    /// <summary>
    /// Wrap out-of-bounds coordinates periodically around the image (<c>STBIR_EDGE_WRAP</c>).
    /// </summary>
    Wrap = 2,

    /// <summary>
    /// Treat out-of-bounds samples as zero (<c>STBIR_EDGE_ZERO</c>).
    /// </summary>
    Zero = 3,
}
