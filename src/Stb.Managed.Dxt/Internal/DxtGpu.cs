namespace Stb.Managed.Dxt.Internal;

/// <summary>GPU DXT compression, implemented by the optional GPU package (see <see cref="StbGpu"/>).</summary>
internal interface IDxtGpu
{
    /// <summary>Compresses on the GPU and returns <c>true</c>, or returns <c>false</c> (nothing written) to use the CPU.</summary>
    bool TryCompress(ReadOnlySpan<byte> source, int width, int height, int strideBytes, Span<byte> destination, DxtFormat format, DxtCompressionMode mode, bool force);
}

internal static class DxtGpu
{
    internal static bool TryCompress(ReadOnlySpan<byte> source, int width, int height, int strideBytes, Span<byte> destination, DxtFormat format, DxtCompressionMode mode) =>
        StbGpu.Acquire() is IDxtGpu gpu
        && gpu.TryCompress(source, width, height, strideBytes, destination, format, mode, StbGpu.Mode == StbGpuMode.ForceGpu);
}
