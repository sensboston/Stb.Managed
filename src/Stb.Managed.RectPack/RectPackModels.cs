namespace Stb.Managed.RectPack;

/// <summary>
/// Packing heuristic used by <see cref="StbRectPacker"/>.
/// </summary>
public enum RectPackHeuristic
{
    /// <summary>
    /// Skyline Bottom-Left heuristic sorted by descending height (default; faster).
    /// Matches <c>STBRP_HEURISTIC_Skyline_BL_sortHeight</c>.
    /// </summary>
    SkylineBottomLeft = 0,

    /// <summary>
    /// Skyline Best-Fit heuristic sorted by descending height (tries both left and right edge alignment to minimize wasted area).
    /// Matches <c>STBRP_HEURISTIC_Skyline_BF_sortHeight</c>.
    /// </summary>
    SkylineBestFit = 1,
}

/// <summary>
/// Represents a rectangle to be packed into a 2D bin by <see cref="StbRectPacker"/> or <see cref="StbRectPack"/>.
/// </summary>
public record struct PackRect
{
    /// <summary>
    /// Creates a new unpacked rectangle descriptor with the given dimensions and optional caller identifier.
    /// </summary>
    /// <param name="width">Rectangle width in pixels (must be non-negative).</param>
    /// <param name="height">Rectangle height in pixels (must be non-negative).</param>
    /// <param name="id">Optional caller-defined identifier (preserved untouched by the packer).</param>
    public PackRect(int width, int height, int id = 0)
    {
        Id = id;
        Width = width;
        Height = height;
        X = 0;
        Y = 0;
        WasPacked = false;
    }

    /// <summary>
    /// Creates a rectangle descriptor with all fields explicitly initialized.
    /// </summary>
    /// <param name="id">Caller-defined identifier.</param>
    /// <param name="width">Rectangle width in pixels.</param>
    /// <param name="height">Rectangle height in pixels.</param>
    /// <param name="x">Packed X coordinate.</param>
    /// <param name="y">Packed Y coordinate.</param>
    /// <param name="wasPacked">Whether the rectangle was successfully packed.</param>
    public PackRect(int id, int width, int height, int x, int y, bool wasPacked)
    {
        Id = id;
        Width = width;
        Height = height;
        X = x;
        Y = y;
        WasPacked = wasPacked;
    }

    /// <summary>Gets or sets the caller-defined identifier (reserved for user use, never modified by packing).</summary>
    public int Id { get; set; }

    /// <summary>Gets or sets the input rectangle width.</summary>
    public int Width { get; set; }

    /// <summary>Gets or sets the input rectangle height.</summary>
    public int Height { get; set; }

    /// <summary>Gets or sets the output X coordinate (valid when <see cref="WasPacked"/> is <c>true</c>).</summary>
    public int X { get; set; }

    /// <summary>Gets or sets the output Y coordinate (valid when <see cref="WasPacked"/> is <c>true</c>).</summary>
    public int Y { get; set; }

    /// <summary>Gets or sets a value indicating whether the rectangle was successfully placed in the bin.</summary>
    public bool WasPacked { get; set; }
}
