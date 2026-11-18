namespace Stb.Managed;

/// <summary>
/// Specifies the number of color channels per pixel.
/// </summary>
public enum ColorComponents
{
    /// <summary>
    /// Keep the source image's native channel count.
    /// </summary>
    Default = 0,

    /// <summary>
    /// Single-channel greyscale (Y).
    /// </summary>
    Grey = 1,

    /// <summary>
    /// Two-channel greyscale with alpha (YA).
    /// </summary>
    GreyAlpha = 2,

    /// <summary>
    /// Three-channel red, green, blue (RGB).
    /// </summary>
    RedGreenBlue = 3,

    /// <summary>
    /// Four-channel red, green, blue, alpha (RGBA).
    /// </summary>
    RedGreenBlueAlpha = 4,
}
