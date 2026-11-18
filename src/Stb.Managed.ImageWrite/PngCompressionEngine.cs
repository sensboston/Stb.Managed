namespace Stb.Managed;

/// <summary>
/// Selects the zlib compression backend used when encoding PNG <c>IDAT</c> payloads.
/// </summary>
public enum PngCompressionEngine
{
    /// <summary>
    /// Uses the platform zlib compressor (<c>ZLibStream</c> / zlib-ng on .NET 8+,
    /// RFC 1950 <see cref="System.IO.Compression.DeflateStream"/> + Adler-32 on .NET Standard 2.1).
    /// Faster and typically produces 20-50% smaller PNG files than <see cref="StbCompatible"/>.
    /// </summary>
    ZLib = 0,

    /// <summary>
    /// Uses a faithful port of <c>stb_image_write.h</c>'s built-in fixed-Huffman LZ77 compressor
    /// (<c>stbi_zlib_compress</c>), producing byte-for-byte identical PNG output to native <c>stb_image_write</c>.
    /// </summary>
    StbCompatible = 1,
}
