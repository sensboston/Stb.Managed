namespace Stb.Managed.Dxt;

/// <summary>
/// Compression mode flags for BC1/BC3 (DXT1/DXT5) color block compression, matching <c>stb_dxt.h</c> v1.13.
/// </summary>
[Flags]
public enum DxtCompressionMode
{
    /// <summary>Default quality (one least-squares refinement pass).</summary>
    Normal = 0,

    /// <summary>
    /// Legacy dithering flag (<c>STB_DXT_DITHER</c>). Deprecated no-op in upstream <c>stb_dxt.h</c> v1.11+;
    /// preserved for API compatibility.
    /// </summary>
    Dither = 1,

    /// <summary>
    /// High-quality mode (<c>STB_DXT_HIGHQUAL</c>): performs up to two least-squares refinement passes (~30-40% slower).
    /// </summary>
    HighQuality = 2,
}

/// <summary>
/// GPU block-compressed texture format supported by <see cref="StbDxt"/>.
/// </summary>
public enum DxtFormat
{
    /// <summary>BC1 / DXT1 opaque RGB (8 bytes per 4x4 block; source pixels are 4-byte RGBA).</summary>
    Bc1 = 0,

    /// <summary>BC3 / DXT5 RGBA with interpolated alpha (16 bytes per 4x4 block; source pixels are 4-byte RGBA).</summary>
    Bc3 = 1,

    /// <summary>BC4 single-channel unsigned normalized (8 bytes per 4x4 block; source pixels are 1-byte R).</summary>
    Bc4 = 2,

    /// <summary>BC5 two-channel unsigned normalized (16 bytes per 4x4 block; source pixels are 2-byte RG).</summary>
    Bc5 = 3,
}
