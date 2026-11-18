namespace Stb.Managed;

/// <summary>
/// Basic header metadata for an encoded image obtained without decoding its pixel data.
/// </summary>
/// <param name="Width">The image width in pixels.</param>
/// <param name="Height">The image height in pixels.</param>
/// <param name="Components">The number of color components in the encoded image.</param>
/// <param name="BitsPerChannel">The bit depth per channel (typically 8, 16, or 32 for HDR).</param>
/// <param name="IsHdr">Whether the image stores high-dynamic-range floating-point data.</param>
public readonly record struct ImageInfo(
    int Width,
    int Height,
    ColorComponents Components,
    int BitsPerChannel,
    bool IsHdr);
