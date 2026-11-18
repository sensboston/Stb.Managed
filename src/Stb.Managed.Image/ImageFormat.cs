namespace Stb.Managed;

/// <summary>
/// Identifies an image container format supported by <see cref="StbImage"/>.
/// </summary>
public enum ImageFormat
{
    /// <summary>
    /// The image format could not be recognized.
    /// </summary>
    Unknown = 0,

    /// <summary>
    /// Portable Network Graphics (PNG).
    /// </summary>
    Png = 1,

    /// <summary>
    /// Joint Photographic Experts Group (JPEG).
    /// </summary>
    Jpeg = 2,

    /// <summary>
    /// Windows Bitmap (BMP).
    /// </summary>
    Bmp = 3,

    /// <summary>
    /// Truevision TGA.
    /// </summary>
    Tga = 4,

    /// <summary>
    /// Graphics Interchange Format (GIF).
    /// </summary>
    Gif = 5,

    /// <summary>
    /// Adobe Photoshop Document (PSD).
    /// </summary>
    Psd = 6,

    /// <summary>
    /// Radiance RGBE high-dynamic-range image (HDR).
    /// </summary>
    Hdr = 7,

    /// <summary>
    /// Softimage PIC.
    /// </summary>
    Pic = 8,

    /// <summary>
    /// Portable Anymap (PGM/PPM).
    /// </summary>
    Pnm = 9,
}
