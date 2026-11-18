namespace Stb.Managed.Perlin.Internal;

internal enum PerlinGpuKind
{
    Noise,
    Fbm,
    Ridge,
    Turbulence,
}

/// <summary>Arguments of a 2D Perlin grid fill.</summary>
internal readonly record struct PerlinGrid(
    PerlinGpuKind Kind,
    int Width,
    int Height,
    float StartX,
    float StartY,
    float StepX,
    float StepY,
    float Z,
    float Lacunarity,
    float Gain,
    float Offset,
    int Octaves,
    int XWrap,
    int YWrap,
    int ZWrap,
    int Seed);

/// <summary>GPU Perlin grids, implemented by the optional GPU package (see <see cref="StbGpu"/>).</summary>
internal interface IPerlinGpu
{
    /// <summary>Fills a dense grid on the GPU and returns <c>true</c>, or returns <c>false</c> (nothing written) to use the CPU.</summary>
    bool TryFill(Span<float> destination, in PerlinGrid grid, bool force);
}

internal static class PerlinGpu
{
    internal static bool TryFill(Span<float> destination, int stride, in PerlinGrid grid) =>
        stride == grid.Width
        && StbGpu.Acquire() is IPerlinGpu gpu
        && gpu.TryFill(destination, grid, StbGpu.Mode == StbGpuMode.ForceGpu);
}
