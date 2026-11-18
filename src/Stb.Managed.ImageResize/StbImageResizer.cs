using System.Runtime.InteropServices;
using Stb.Managed.Internal;

namespace Stb.Managed;

/// <summary>
/// A precomputed, immutable, thread-safe image resampling plan matching <c>stbir_build_samplers</c> +
/// <c>stbir_resize_extended</c>. Reuse a single instance across many frames of the same geometry to avoid
/// recomputing filter coefficients.
/// </summary>
public sealed class StbImageResizer
{
    private readonly ResizePlan _plan;
    private readonly ImageResizeOptions _options;

    private StbImageResizer(ResizePlan plan, ImageResizeOptions options)
    {
        _plan = plan;
        _options = options;
    }

    /// <summary>
    /// Creates a precomputed resampling plan for the specified dimensions, layout, data type, and options.
    /// </summary>
    /// <param name="inputWidth">Input image width in pixels.</param>
    /// <param name="inputHeight">Input image height in pixels.</param>
    /// <param name="outputWidth">Output image width in pixels.</param>
    /// <param name="outputHeight">Output image height in pixels.</param>
    /// <param name="inputLayout">Input pixel layout.</param>
    /// <param name="inputDataType">Input sample data type.</param>
    /// <param name="options">Optional extended resampling options (filters, edges, subrects, output layout/type, fast alpha, limits).</param>
    /// <returns>A reusable, thread-safe <see cref="StbImageResizer"/> instance.</returns>
    /// <exception cref="StbException">Thrown if dimensions, subrects, layout channel counts, or safety limits are invalid.</exception>
    /// <exception cref="ArgumentOutOfRangeException">Thrown if an enum argument is outside its defined range.</exception>
    public static StbImageResizer Create(
        int inputWidth,
        int inputHeight,
        int outputWidth,
        int outputHeight,
        ResizePixelLayout inputLayout,
        ResizeDataType inputDataType,
        ImageResizeOptions? options = null)
    {
        ImageResizeOptions opts = options ?? ImageResizeOptions.Default;
        ResizePlan plan = ResizePlan.Create(
            inputWidth,
            inputHeight,
            outputWidth,
            outputHeight,
            inputLayout,
            inputDataType,
            opts);
        return new StbImageResizer(plan, opts);
    }

    /// <summary>
    /// Gets the full input image width in pixels.
    /// </summary>
    public int InputWidth => _plan.InputWidth;

    /// <summary>
    /// Gets the full input image height in pixels.
    /// </summary>
    public int InputHeight => _plan.InputHeight;

    /// <summary>
    /// Gets the full output image width in pixels.
    /// </summary>
    public int OutputWidth => _plan.OutputWidth;

    /// <summary>
    /// Gets the full output image height in pixels.
    /// </summary>
    public int OutputHeight => _plan.OutputHeight;

    /// <summary>
    /// Gets the input pixel layout.
    /// </summary>
    public ResizePixelLayout InputLayout => _plan.PublicInputLayout;

    /// <summary>
    /// Gets the output pixel layout.
    /// </summary>
    public ResizePixelLayout OutputLayout => _plan.PublicOutputLayout;

    /// <summary>
    /// Gets the input sample data type.
    /// </summary>
    public ResizeDataType InputDataType => _plan.PublicInputDataType;

    /// <summary>
    /// Gets the output sample data type.
    /// </summary>
    public ResizeDataType OutputDataType => _plan.PublicOutputDataType;

    /// <summary>
    /// Gets the number of channels per pixel.
    /// </summary>
    public int Channels => _plan.Channels;

    /// <summary>
    /// Gets the internal number of channels carried through the resampling filters (7 for fancy 4-channel alpha,
    /// 3 for fancy 2-channel alpha, otherwise equal to <see cref="Channels"/>).
    /// </summary>
    public int EffectiveChannels => _plan.EffectiveChannels;

    /// <summary>
    /// Gets the clipped X offset within the output image written by this plan.
    /// </summary>
    public int OutputOffsetX => _plan.OutputOffsetX;

    /// <summary>
    /// Gets the clipped Y offset within the output image written by this plan.
    /// </summary>
    public int OutputOffsetY => _plan.OutputOffsetY;

    /// <summary>
    /// Gets the clipped width within the output image written by this plan.
    /// </summary>
    public int OutputSubWidth => _plan.OutputSubWidth;

    /// <summary>
    /// Gets the clipped height within the output image written by this plan.
    /// </summary>
    public int OutputSubHeight => _plan.OutputSubHeight;

    /// <summary>
    /// Gets a value indicating whether the vertical resampling pass runs before the horizontal pass.
    /// </summary>
    public bool IsVerticalFirst => _plan.VerticalFirst;

    internal int ColumnSliceCount => _plan.ColumnSlices.Length;

    /// <summary>
    /// Executes the resampling plan on raw byte buffers.
    /// Safe to invoke concurrently from multiple threads on distinct output buffers.
    /// </summary>
    /// <param name="input">Source pixel buffer in raw bytes.</param>
    /// <param name="output">Destination pixel buffer in raw bytes.</param>
    /// <param name="inputStrideBytes">
    /// Distance in bytes between input scanlines: <c>0</c> for tightly packed top-to-bottom rows,
    /// positive for top-to-bottom strided rows, or negative for bottom-up rows starting at the last scanline.
    /// </param>
    /// <param name="outputStrideBytes">
    /// Distance in bytes between output scanlines: <c>0</c> for tightly packed top-to-bottom rows,
    /// positive for top-to-bottom strided rows, or negative for bottom-up rows starting at the last scanline.
    /// </param>
    public void Resize(
        ReadOnlySpan<byte> input,
        Span<byte> output,
        int inputStrideBytes = 0,
        int outputStrideBytes = 0) =>
        Run(input, output, inputStrideBytes, outputStrideBytes);

    /// <summary>
    /// Executes the resampling plan on <see cref="ReadOnlyMemory{T}"/> / <see cref="Memory{T}"/> buffers.
    /// When parallel execution is enabled via <see cref="StbOptions.MaxDegreeOfParallelism"/>, this overload
    /// processes bands directly in place without intermediate span-to-array copies.
    /// </summary>
    /// <param name="input">Source pixel buffer in raw bytes.</param>
    /// <param name="output">Destination pixel buffer in raw bytes.</param>
    /// <param name="inputStrideBytes">
    /// Distance in bytes between input scanlines: <c>0</c> for tightly packed top-to-bottom rows,
    /// positive for top-to-bottom strided rows, or negative for bottom-up rows starting at the last scanline.
    /// </param>
    /// <param name="outputStrideBytes">
    /// Distance in bytes between output scanlines: <c>0</c> for tightly packed top-to-bottom rows,
    /// positive for top-to-bottom strided rows, or negative for bottom-up rows starting at the last scanline.
    /// </param>
    public void ResizeMemory(
        ReadOnlyMemory<byte> input,
        Memory<byte> output,
        int inputStrideBytes = 0,
        int outputStrideBytes = 0) =>
        _plan.ExecuteMemory(input, output, inputStrideBytes, outputStrideBytes);

    internal void ExecuteScalarForTest(
        ReadOnlySpan<byte> input,
        Span<byte> output,
        int inputStrideBytes = 0,
        int outputStrideBytes = 0) =>
        _plan.ExecuteScalarForTest(input, output, inputStrideBytes, outputStrideBytes);

    /// <summary>
    /// Executes the resampling plan on 16-bit (<see cref="ResizeDataType.UInt16Linear"/> or
    /// <see cref="ResizeDataType.Fp16"/>) buffers.
    /// </summary>
    /// <param name="input">Source 16-bit sample buffer.</param>
    /// <param name="output">Destination 16-bit sample buffer.</param>
    /// <param name="inputStrideBytes">Input scanline stride in bytes, or <c>0</c> for tightly packed rows.</param>
    /// <param name="outputStrideBytes">Output scanline stride in bytes, or <c>0</c> for tightly packed rows.</param>
    public void Resize(
        ReadOnlySpan<ushort> input,
        Span<ushort> output,
        int inputStrideBytes = 0,
        int outputStrideBytes = 0)
    {
        if ((_plan.PublicInputDataType != ResizeDataType.UInt16Linear && _plan.PublicInputDataType != ResizeDataType.Fp16)
            || (_plan.PublicOutputDataType != ResizeDataType.UInt16Linear && _plan.PublicOutputDataType != ResizeDataType.Fp16))
        {
            throw new StbException(
                StbErrorCode.InvalidArgument,
                $"ushort Resize overload requires 16-bit input/output data types (plan uses {_plan.PublicInputDataType} -> {_plan.PublicOutputDataType}).");
        }

        Run(MemoryMarshal.AsBytes(input), MemoryMarshal.AsBytes(output), inputStrideBytes, outputStrideBytes);
    }

    /// <summary>
    /// Executes the resampling plan on 32-bit floating-point (<see cref="ResizeDataType.Fp32"/>) buffers.
    /// </summary>
    /// <param name="input">Source float sample buffer.</param>
    /// <param name="output">Destination float sample buffer.</param>
    /// <param name="inputStrideBytes">Input scanline stride in bytes, or <c>0</c> for tightly packed rows.</param>
    /// <param name="outputStrideBytes">Output scanline stride in bytes, or <c>0</c> for tightly packed rows.</param>
    public void Resize(
        ReadOnlySpan<float> input,
        Span<float> output,
        int inputStrideBytes = 0,
        int outputStrideBytes = 0)
    {
        if (_plan.PublicInputDataType != ResizeDataType.Fp32 || _plan.PublicOutputDataType != ResizeDataType.Fp32)
        {
            throw new StbException(
                StbErrorCode.InvalidArgument,
                $"float Resize overload requires Fp32 input/output data types (plan uses {_plan.PublicInputDataType} -> {_plan.PublicOutputDataType}).");
        }

        Run(MemoryMarshal.AsBytes(input), MemoryMarshal.AsBytes(output), inputStrideBytes, outputStrideBytes);
    }

#if NET8_0_OR_GREATER
    /// <summary>
    /// Executes the resampling plan on IEEE-754 binary16 (<see cref="Half"/>) buffers.
    /// </summary>
    /// <param name="input">Source <see cref="Half"/> sample buffer.</param>
    /// <param name="output">Destination <see cref="Half"/> sample buffer.</param>
    /// <param name="inputStrideBytes">Input scanline stride in bytes, or <c>0</c> for tightly packed rows.</param>
    /// <param name="outputStrideBytes">Output scanline stride in bytes, or <c>0</c> for tightly packed rows.</param>
    public void Resize(
        ReadOnlySpan<Half> input,
        Span<Half> output,
        int inputStrideBytes = 0,
        int outputStrideBytes = 0)
    {
        if (_plan.PublicInputDataType != ResizeDataType.Fp16 || _plan.PublicOutputDataType != ResizeDataType.Fp16)
        {
            throw new StbException(
                StbErrorCode.InvalidArgument,
                $"Half Resize overload requires Fp16 input/output data types (plan uses {_plan.PublicInputDataType} -> {_plan.PublicOutputDataType}).");
        }

        Run(MemoryMarshal.AsBytes(input), MemoryMarshal.AsBytes(output), inputStrideBytes, outputStrideBytes);
    }
#endif

    /// <summary>
    /// Executes the resampling plan and allocates a tightly packed output byte array.
    /// </summary>
    /// <param name="input">Source pixel buffer in raw bytes.</param>
    /// <param name="inputStrideBytes">Input scanline stride in bytes, or <c>0</c> for tightly packed rows.</param>
    /// <returns>A newly allocated tightly packed output byte array.</returns>
    public byte[] ResizeToArray(ReadOnlySpan<byte> input, int inputStrideBytes = 0)
    {
        long outputBytesLong = (long)_plan.OutputWidth * _plan.OutputHeight * _plan.Channels * _plan.OutputTypeSize;
        if (outputBytesLong > ArrayAllocation.MaxByteArrayLength)
        {
            throw new StbException(
                StbErrorCode.LimitExceeded,
                $"Output buffer size {outputBytesLong} exceeds maximum array length.");
        }

        byte[] output = _plan.IsEmptyRegion
            ? new byte[(int)outputBytesLong]
            : (_plan.OutputSubWidth == _plan.OutputWidth && _plan.OutputSubHeight == _plan.OutputHeight
                ? ArrayAllocation.AllocateUninitialized<byte>((int)outputBytesLong)
                : new byte[(int)outputBytesLong]);

        Run(input, output, inputStrideBytes, 0);
        return output;
    }

    private void Run(ReadOnlySpan<byte> input, Span<byte> output, int inputStrideBytes, int outputStrideBytes)
    {
        if (ResizeGpu.TryResize(
            input,
            _plan.InputWidth,
            _plan.InputHeight,
            inputStrideBytes,
            output,
            _plan.OutputWidth,
            _plan.OutputHeight,
            outputStrideBytes,
            _plan.PublicInputLayout,
            _plan.PublicInputDataType,
            _options))
        {
            return;
        }

        _plan.Execute(input, output, inputStrideBytes, outputStrideBytes);
    }
}
