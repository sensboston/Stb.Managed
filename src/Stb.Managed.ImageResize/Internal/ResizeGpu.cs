namespace Stb.Managed.Internal;

/// <summary>GPU resize, implemented by the optional GPU package (see <see cref="StbGpu"/>).</summary>
internal interface IResizeGpu
{
    /// <summary>Resizes on the GPU and returns <c>true</c>, or returns <c>false</c> (nothing written) to use the CPU.</summary>
    bool TryResize(
        ReadOnlySpan<byte> input,
        int inputWidth,
        int inputHeight,
        int inputStrideBytes,
        Span<byte> output,
        int outputWidth,
        int outputHeight,
        int outputStrideBytes,
        ResizePixelLayout layout,
        ResizeDataType dataType,
        ImageResizeOptions options,
        bool force);
}

internal static class ResizeGpu
{
    internal static bool TryResize(
        ReadOnlySpan<byte> input,
        int inputWidth,
        int inputHeight,
        int inputStrideBytes,
        Span<byte> output,
        int outputWidth,
        int outputHeight,
        int outputStrideBytes,
        ResizePixelLayout layout,
        ResizeDataType dataType,
        ImageResizeOptions options) =>
        StbGpu.Acquire() is IResizeGpu gpu
        && gpu.TryResize(input, inputWidth, inputHeight, inputStrideBytes, output, outputWidth, outputHeight, outputStrideBytes, layout, dataType, options, StbGpu.Mode == StbGpuMode.ForceGpu);
}
