using Stb.Managed.Gpu.Kernels;
using Stb.Managed.Dxt;
using Stb.Managed.Dxt.Internal;
using Stb.Managed.Internal;
using Stb.Managed.Perlin.Internal;

namespace Stb.Managed.Gpu.Internal;

/// <summary>
/// Connects the ordinary CPU entry points (resize, DXT, Perlin) to the GPU. Created by <see cref="StbGpu"/>
/// through reflection on a background thread when the package is present; it opens the default
/// <see cref="StbGpuContext"/> and compiles the kernels up front, so the first routed call does not pay for it.
/// </summary>
internal sealed class StbGpuProvider : IStbGpuProvider, IResizeGpu, IDxtGpu, IPerlinGpu
{
    private const long MaxTransferBytesPerOutputPixel = 16;

    private readonly StbGpuContext _context;
    private readonly object _planLock = new();
    private StbGpuResizePlan? _lastPlan;
    private (int, int, int, int, ResizePixelLayout, ResizeDataType, ResizeFilter, ResizeEdge) _lastKey;

    public StbGpuProvider()
    {
        _context = StbGpuContext.Default;
        if (_context.Device.IsHardwareGpu)
        {
            WarmKernels();
        }
    }

    public bool IsHardwareGpu => _context.Device.IsHardwareGpu;

    public string DeviceName => _context.Device.Name;

    public bool TryResize(
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
        bool force)
    {
        // Measured on Intel HD 630 and N150 graphics: resizing on an integrated GPU loses to the CPU threads,
        // so automatic routing uses only discrete GPUs for it.
        if (!force && (!_context.Device.IsLikelyDiscrete || (long)outputWidth * outputHeight < _context.Thresholds.ResizeMinOutputPixels))
        {
            return false;
        }

        // Only what the GPU kernels implement: one filter and edge mode for both axes, whole images, same in/out format.
        if (options.HorizontalFilter != options.VerticalFilter
            || options.HorizontalEdge != options.VerticalEdge
            || options.HorizontalCustomFilter is not null
            || options.VerticalCustomFilter is not null
            || options.InputSubrect is not null
            || options.OutputSubrect is not null
            || options.PixelSubrect is not null
            || options.FastAlpha
            || (options.OutputLayout is { } ol && ol != layout)
            || (options.OutputDataType is { } od && od != dataType)
            || !StbGpuResizePlan.TryDescribeFormat(layout, dataType, out int channels, out _, out _, out _))
        {
            return false;
        }

        int pixelBytes = channels * ResizeGeometry.GetDataTypeSize(dataType);
        if ((inputStrideBytes != 0 && inputStrideBytes != inputWidth * pixelBytes)
            || (outputStrideBytes != 0 && outputStrideBytes != outputWidth * pixelBytes))
        {
            return false;
        }

        // The copies to and from the device dominate: when they exceed about 16 bytes per output pixel
        // (strong downscales, float data) the CPU threads finish first.
        long outPixels = (long)outputWidth * outputHeight;
        long transferBytes = ((long)inputWidth * inputHeight + outPixels) * pixelBytes;
        if (!force && transferBytes > MaxTransferBytesPerOutputPixel * outPixels)
        {
            return false;
        }

        var key = (inputWidth, inputHeight, outputWidth, outputHeight, layout, dataType, options.HorizontalFilter, options.HorizontalEdge);
        lock (_planLock)
        {
            if (_lastPlan is null || _lastKey != key)
            {
                _lastPlan?.Dispose();
                _lastPlan = StbGpuResizePlan.Create(_context, inputWidth, inputHeight, outputWidth, outputHeight, layout, dataType, options.HorizontalFilter, options.HorizontalEdge);
                _lastKey = key;
            }

            _lastPlan.ResizeOnGpu(input, output);
        }

        return true;
    }

    public bool TryCompress(ReadOnlySpan<byte> source, int width, int height, int strideBytes, Span<byte> destination, DxtFormat format, DxtCompressionMode mode, bool force)
    {
        StbGpuDxt.ValidateCommon(width, height, format, strideBytes, source.Length, destination.Length, out int channels, out int stride, out int blocksX, out int blocksY, out long outBytes);
        if (!force && (long)blocksX * blocksY < _context.Thresholds.DxtMinBlocks)
        {
            return false;
        }

        StbGpuDxt.CompressOnGpu(_context, source, width, height, stride, channels, blocksX, blocksY, outBytes, destination, format, mode);
        return true;
    }

    public bool TryFill(Span<float> destination, in PerlinGrid grid, bool force)
    {
        // The grid is generated faster by the CPU threads than it is copied back from the GPU,
        // so automatic routing never picks the GPU for it; only StbGpuMode.ForceGpu does.
        if (!force)
        {
            return false;
        }

        long samples = (long)grid.Width * grid.Height;

        int kind = grid.Kind switch
        {
            PerlinGpuKind.Noise => 0,
            PerlinGpuKind.Fbm => 1,
            PerlinGpuKind.Ridge => 2,
            _ => 3,
        };
        float offset = grid.Kind == PerlinGpuKind.Ridge ? grid.Offset : 1.0f;
        float lacunarity = grid.Kind == PerlinGpuKind.Noise ? 2.0f : grid.Lacunarity;
        float gain = grid.Kind == PerlinGpuKind.Noise ? 0.5f : grid.Gain;
        var p = new Perlin2DParams(
            grid.Width, grid.Height, grid.StartX, grid.StartY, grid.StepX, grid.StepY, grid.Z,
            lacunarity, gain, offset, grid.Octaves, grid.XWrap, grid.YWrap, grid.ZWrap, grid.Seed, mode: kind);
        StbGpuPerlin.Run2DToHost(_context, destination, samples, p);
        return true;
    }

    public void Dispose()
    {
        lock (_planLock)
        {
            _lastPlan?.Dispose();
            _lastPlan = null;
        }

        StbGpuContext.ShutdownDefault();
    }

    // Kernel compilation is the larger part of GPU start-up; doing it here keeps it off the caller's first call.
    private void WarmKernels()
    {
        try
        {
            byte[] rgba = new byte[64 * 64 * 4];
            byte[] small = new byte[32 * 32 * 4];
            using (StbGpuResizePlan plan = StbGpuResizePlan.Create(_context, 64, 64, 32, 32, ResizePixelLayout.Rgba, ResizeDataType.UInt8Srgb))
            {
                plan.ResizeOnGpu(rgba, small);
            }

            byte[] dxt = new byte[16 * 16 * 16];
            foreach (DxtFormat format in new[] { DxtFormat.Bc1, DxtFormat.Bc3 })
            {
                StbGpuDxt.CompressOnGpu(_context, rgba, 64, 64, 64 * 4, 4, 16, 16, format == DxtFormat.Bc1 ? 2048 : 4096, dxt, format, DxtCompressionMode.Normal);
            }

            float[] noise = new float[64 * 64];
            StbGpuPerlin.Run2DToHost(_context, noise, noise.Length, new Perlin2DParams(64, 64, 0, 0, 0.1f, 0.1f, 0, 2.0f, 0.5f, 1.0f, 2, 0, 0, 0, 0, mode: 1));
        }
        catch (Exception)
        {
            // A kernel that fails to compile here fails on use as well and is reported there.
        }
    }
}
