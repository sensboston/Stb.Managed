namespace Stb.Managed;

/// <summary>
/// Controls scanline pre-filtering applied when encoding PNG images (matching <c>stbi_write_force_png_filter</c>).
/// </summary>
public enum PngFilterMode
{
    /// <summary>
    /// Automatically choose the filter with the lowest sum of absolute signed deltas for each scanline.
    /// </summary>
    Auto = -1,

    /// <summary>
    /// Force PNG filter type 0 (None).
    /// </summary>
    None = 0,

    /// <summary>
    /// Force PNG filter type 1 (Sub).
    /// </summary>
    Sub = 1,

    /// <summary>
    /// Force PNG filter type 2 (Up).
    /// </summary>
    Up = 2,

    /// <summary>
    /// Force PNG filter type 3 (Average).
    /// </summary>
    Average = 3,

    /// <summary>
    /// Force PNG filter type 4 (Paeth).
    /// </summary>
    Paeth = 4,
}
