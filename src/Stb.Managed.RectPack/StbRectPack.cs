namespace Stb.Managed.RectPack;

/// <summary>
/// Convenience one-shot entry points for 2D skyline rectangle packing (a fully managed port of <c>stb_rect_pack.h</c> v1.02).
/// </summary>
public static class StbRectPack
{
    /// <summary>
    /// Packs the supplied rectangles into a single bin of dimensions <paramref name="width"/> by <paramref name="height"/>.
    /// </summary>
    /// <param name="width">Target bin width (must be positive).</param>
    /// <param name="height">Target bin height (must be positive).</param>
    /// <param name="rects">Span of rectangles to pack in place.</param>
    /// <param name="heuristic">Skyline placement heuristic.</param>
    /// <param name="nodeCount">Optional skyline node budget (<c>0</c> defaults to <paramref name="width"/>).</param>
    /// <param name="allowOutOfMem">Whether to allow out-of-memory failures instead of rounding rectangle widths up.</param>
    /// <param name="options">Optional execution limits.</param>
    /// <returns><c>true</c> if all rectangles fit; otherwise <c>false</c>.</returns>
    public static bool PackRects(
        int width,
        int height,
        Span<PackRect> rects,
        RectPackHeuristic heuristic = RectPackHeuristic.SkylineBottomLeft,
        int nodeCount = 0,
        bool allowOutOfMem = false,
        StbOptions? options = null)
    {
        using StbRectPacker packer = StbRectPacker.Create(width, height, nodeCount, heuristic, allowOutOfMem, options);
        return packer.PackRects(rects);
    }
}
