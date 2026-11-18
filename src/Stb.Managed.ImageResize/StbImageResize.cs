using System.Runtime.InteropServices;
using Stb.Managed.Internal;

namespace Stb.Managed;

/// <summary>
/// Fully managed, zero-<c>unsafe</c> image resampler ported from <c>stb_image_resize2.h</c> v2.50.
/// Supports all pixel layouts, sample types (8-bit linear/sRGB, 16-bit linear, float32, float16),
/// all 13 built-in filters plus custom kernels, edge modes, sub-rectangles, fancy/fast alpha weighting,
/// and reusable <see cref="StbImageResizer"/> plans.
/// </summary>
public static class StbImageResize
{
    /// <summary>
    /// Resizes an 8-bit sRGB image (with linear alpha when using an alpha layout) into a caller-provided span.
    /// Equivalent to <c>stbir_resize_uint8_srgb</c>.
    /// </summary>
    /// <param name="input">Source pixel bytes.</param>
    /// <param name="inputWidth">Input width in pixels.</param>
    /// <param name="inputHeight">Input height in pixels.</param>
    /// <param name="output">Destination pixel bytes.</param>
    /// <param name="outputWidth">Output width in pixels.</param>
    /// <param name="outputHeight">Output height in pixels.</param>
    /// <param name="layout">Pixel channel layout.</param>
    /// <param name="inputStrideBytes">Input scanline stride in bytes (<c>0</c> = packed, negative = bottom-up).</param>
    /// <param name="outputStrideBytes">Output scanline stride in bytes (<c>0</c> = packed, negative = bottom-up).</param>
    /// <param name="options">Optional extended resampling options.</param>
    public static void ResizeUInt8Srgb(
        ReadOnlySpan<byte> input,
        int inputWidth,
        int inputHeight,
        Span<byte> output,
        int outputWidth,
        int outputHeight,
        ResizePixelLayout layout,
        int inputStrideBytes = 0,
        int outputStrideBytes = 0,
        ImageResizeOptions? options = null) =>
        Resize(
            input,
            inputWidth,
            inputHeight,
            output,
            outputWidth,
            outputHeight,
            layout,
            ResizeDataType.UInt8Srgb,
            inputStrideBytes,
            outputStrideBytes,
            options);

    /// <summary>
    /// Resizes an 8-bit linear image into a caller-provided span.
    /// Equivalent to <c>stbir_resize_uint8_linear</c>.
    /// </summary>
    /// <param name="input">Source pixel bytes.</param>
    /// <param name="inputWidth">Input width in pixels.</param>
    /// <param name="inputHeight">Input height in pixels.</param>
    /// <param name="output">Destination pixel bytes.</param>
    /// <param name="outputWidth">Output width in pixels.</param>
    /// <param name="outputHeight">Output height in pixels.</param>
    /// <param name="layout">Pixel channel layout.</param>
    /// <param name="inputStrideBytes">Input scanline stride in bytes (<c>0</c> = packed, negative = bottom-up).</param>
    /// <param name="outputStrideBytes">Output scanline stride in bytes (<c>0</c> = packed, negative = bottom-up).</param>
    /// <param name="options">Optional extended resampling options.</param>
    public static void ResizeUInt8Linear(
        ReadOnlySpan<byte> input,
        int inputWidth,
        int inputHeight,
        Span<byte> output,
        int outputWidth,
        int outputHeight,
        ResizePixelLayout layout,
        int inputStrideBytes = 0,
        int outputStrideBytes = 0,
        ImageResizeOptions? options = null) =>
        Resize(
            input,
            inputWidth,
            inputHeight,
            output,
            outputWidth,
            outputHeight,
            layout,
            ResizeDataType.UInt8Linear,
            inputStrideBytes,
            outputStrideBytes,
            options);

    /// <summary>
    /// Resizes a 16-bit unsigned linear image into a caller-provided span.
    /// </summary>
    /// <param name="input">Source 16-bit samples.</param>
    /// <param name="inputWidth">Input width in pixels.</param>
    /// <param name="inputHeight">Input height in pixels.</param>
    /// <param name="output">Destination 16-bit samples.</param>
    /// <param name="outputWidth">Output width in pixels.</param>
    /// <param name="outputHeight">Output height in pixels.</param>
    /// <param name="layout">Pixel channel layout.</param>
    /// <param name="inputStrideBytes">Input scanline stride in bytes (<c>0</c> = packed, negative = bottom-up).</param>
    /// <param name="outputStrideBytes">Output scanline stride in bytes (<c>0</c> = packed, negative = bottom-up).</param>
    /// <param name="options">Optional extended resampling options.</param>
    public static void ResizeUInt16Linear(
        ReadOnlySpan<ushort> input,
        int inputWidth,
        int inputHeight,
        Span<ushort> output,
        int outputWidth,
        int outputHeight,
        ResizePixelLayout layout,
        int inputStrideBytes = 0,
        int outputStrideBytes = 0,
        ImageResizeOptions? options = null) =>
        Resize(
            MemoryMarshal.AsBytes(input),
            inputWidth,
            inputHeight,
            MemoryMarshal.AsBytes(output),
            outputWidth,
            outputHeight,
            layout,
            ResizeDataType.UInt16Linear,
            inputStrideBytes,
            outputStrideBytes,
            options);

    /// <summary>
    /// Resizes a 32-bit floating-point linear image into a caller-provided span.
    /// Equivalent to <c>stbir_resize_float_linear</c>.
    /// </summary>
    /// <param name="input">Source float samples.</param>
    /// <param name="inputWidth">Input width in pixels.</param>
    /// <param name="inputHeight">Input height in pixels.</param>
    /// <param name="output">Destination float samples.</param>
    /// <param name="outputWidth">Output width in pixels.</param>
    /// <param name="outputHeight">Output height in pixels.</param>
    /// <param name="layout">Pixel channel layout.</param>
    /// <param name="inputStrideBytes">Input scanline stride in bytes (<c>0</c> = packed, negative = bottom-up).</param>
    /// <param name="outputStrideBytes">Output scanline stride in bytes (<c>0</c> = packed, negative = bottom-up).</param>
    /// <param name="options">Optional extended resampling options.</param>
    public static void ResizeFloatLinear(
        ReadOnlySpan<float> input,
        int inputWidth,
        int inputHeight,
        Span<float> output,
        int outputWidth,
        int outputHeight,
        ResizePixelLayout layout,
        int inputStrideBytes = 0,
        int outputStrideBytes = 0,
        ImageResizeOptions? options = null) =>
        Resize(
            MemoryMarshal.AsBytes(input),
            inputWidth,
            inputHeight,
            MemoryMarshal.AsBytes(output),
            outputWidth,
            outputHeight,
            layout,
            ResizeDataType.Fp32,
            inputStrideBytes,
            outputStrideBytes,
            options);

    /// <summary>
    /// Resizes a 16-bit IEEE-754 half-float linear image stored as <see cref="ushort"/> bit patterns
    /// into a caller-provided span.
    /// </summary>
    /// <param name="input">Source half-float bit patterns.</param>
    /// <param name="inputWidth">Input width in pixels.</param>
    /// <param name="inputHeight">Input height in pixels.</param>
    /// <param name="output">Destination half-float bit patterns.</param>
    /// <param name="outputWidth">Output width in pixels.</param>
    /// <param name="outputHeight">Output height in pixels.</param>
    /// <param name="layout">Pixel channel layout.</param>
    /// <param name="inputStrideBytes">Input scanline stride in bytes (<c>0</c> = packed, negative = bottom-up).</param>
    /// <param name="outputStrideBytes">Output scanline stride in bytes (<c>0</c> = packed, negative = bottom-up).</param>
    /// <param name="options">Optional extended resampling options.</param>
    public static void ResizeHalfLinear(
        ReadOnlySpan<ushort> input,
        int inputWidth,
        int inputHeight,
        Span<ushort> output,
        int outputWidth,
        int outputHeight,
        ResizePixelLayout layout,
        int inputStrideBytes = 0,
        int outputStrideBytes = 0,
        ImageResizeOptions? options = null) =>
        Resize(
            MemoryMarshal.AsBytes(input),
            inputWidth,
            inputHeight,
            MemoryMarshal.AsBytes(output),
            outputWidth,
            outputHeight,
            layout,
            ResizeDataType.Fp16,
            inputStrideBytes,
            outputStrideBytes,
            options);

#if NET8_0_OR_GREATER
    /// <summary>
    /// Resizes a 16-bit IEEE-754 <see cref="Half"/> linear image into a caller-provided span.
    /// </summary>
    /// <param name="input">Source <see cref="Half"/> samples.</param>
    /// <param name="inputWidth">Input width in pixels.</param>
    /// <param name="inputHeight">Input height in pixels.</param>
    /// <param name="output">Destination <see cref="Half"/> samples.</param>
    /// <param name="outputWidth">Output width in pixels.</param>
    /// <param name="outputHeight">Output height in pixels.</param>
    /// <param name="layout">Pixel channel layout.</param>
    /// <param name="inputStrideBytes">Input scanline stride in bytes (<c>0</c> = packed, negative = bottom-up).</param>
    /// <param name="outputStrideBytes">Output scanline stride in bytes (<c>0</c> = packed, negative = bottom-up).</param>
    /// <param name="options">Optional extended resampling options.</param>
    public static void ResizeHalfLinear(
        ReadOnlySpan<Half> input,
        int inputWidth,
        int inputHeight,
        Span<Half> output,
        int outputWidth,
        int outputHeight,
        ResizePixelLayout layout,
        int inputStrideBytes = 0,
        int outputStrideBytes = 0,
        ImageResizeOptions? options = null) =>
        Resize(
            MemoryMarshal.AsBytes(input),
            inputWidth,
            inputHeight,
            MemoryMarshal.AsBytes(output),
            outputWidth,
            outputHeight,
            layout,
            ResizeDataType.Fp16,
            inputStrideBytes,
            outputStrideBytes,
            options);
#endif

    /// <summary>
    /// Resizes raw pixel bytes into a caller-provided span using explicit edge and filter settings
    /// (medium API, equivalent to <c>stbir_resize</c>).
    /// </summary>
    /// <param name="input">Source pixel bytes.</param>
    /// <param name="inputWidth">Input width in pixels.</param>
    /// <param name="inputHeight">Input height in pixels.</param>
    /// <param name="output">Destination pixel bytes.</param>
    /// <param name="outputWidth">Output width in pixels.</param>
    /// <param name="outputHeight">Output height in pixels.</param>
    /// <param name="layout">Pixel channel layout.</param>
    /// <param name="dataType">Sample data type.</param>
    /// <param name="edge">Edge handling mode applied to both axes.</param>
    /// <param name="filter">Filter kernel applied to both axes.</param>
    /// <param name="inputStrideBytes">Input scanline stride in bytes (<c>0</c> = packed, negative = bottom-up).</param>
    /// <param name="outputStrideBytes">Output scanline stride in bytes (<c>0</c> = packed, negative = bottom-up).</param>
    /// <param name="options">Optional shared execution options (limits and parallelism).</param>
    public static void Resize(
        ReadOnlySpan<byte> input,
        int inputWidth,
        int inputHeight,
        Span<byte> output,
        int outputWidth,
        int outputHeight,
        ResizePixelLayout layout,
        ResizeDataType dataType,
        ResizeEdge edge,
        ResizeFilter filter = ResizeFilter.Default,
        int inputStrideBytes = 0,
        int outputStrideBytes = 0,
        StbOptions? options = null)
    {
        ImageResizeOptions resizeOptions = new()
        {
            Options = options ?? StbOptions.Default,
            HorizontalEdge = edge,
            VerticalEdge = edge,
            HorizontalFilter = filter,
            VerticalFilter = filter,
        };
        Resize(input, inputWidth, inputHeight, output, outputWidth, outputHeight, layout, dataType, inputStrideBytes, outputStrideBytes, resizeOptions);
    }

    /// <summary>
    /// Resizes raw pixel bytes into a caller-provided span using full extended options
    /// (equivalent to <c>stbir_resize_extended</c>).
    /// </summary>
    /// <param name="input">Source pixel bytes.</param>
    /// <param name="inputWidth">Input width in pixels.</param>
    /// <param name="inputHeight">Input height in pixels.</param>
    /// <param name="output">Destination pixel bytes.</param>
    /// <param name="outputWidth">Output width in pixels.</param>
    /// <param name="outputHeight">Output height in pixels.</param>
    /// <param name="layout">Input pixel layout (override output layout via <see cref="ImageResizeOptions.OutputLayout"/>).</param>
    /// <param name="dataType">Input sample data type (override output type via <see cref="ImageResizeOptions.OutputDataType"/>).</param>
    /// <param name="inputStrideBytes">Input scanline stride in bytes (<c>0</c> = packed, negative = bottom-up).</param>
    /// <param name="outputStrideBytes">Output scanline stride in bytes (<c>0</c> = packed, negative = bottom-up).</param>
    /// <param name="options">Optional extended resampling options.</param>
    public static void Resize(
        ReadOnlySpan<byte> input,
        int inputWidth,
        int inputHeight,
        Span<byte> output,
        int outputWidth,
        int outputHeight,
        ResizePixelLayout layout,
        ResizeDataType dataType,
        int inputStrideBytes = 0,
        int outputStrideBytes = 0,
        ImageResizeOptions? options = null)
    {
        ImageResizeOptions resolved = options ?? ImageResizeOptions.Default;
        if (ResizeGpu.TryResize(input, inputWidth, inputHeight, inputStrideBytes, output, outputWidth, outputHeight, outputStrideBytes, layout, dataType, resolved))
        {
            return;
        }

        ResizePlan plan = ResizePlan.Create(
            inputWidth,
            inputHeight,
            outputWidth,
            outputHeight,
            layout,
            dataType,
            resolved);
        plan.Execute(input, output, inputStrideBytes, outputStrideBytes);
    }

    /// <summary>
    /// Resizes raw pixel bytes in <see cref="ReadOnlyMemory{T}"/> / <see cref="Memory{T}"/> buffers using full extended
    /// options. Enables zero-copy parallel execution when <see cref="StbOptions.MaxDegreeOfParallelism"/> is enabled.
    /// </summary>
    /// <param name="input">Source pixel bytes.</param>
    /// <param name="inputWidth">Input width in pixels.</param>
    /// <param name="inputHeight">Input height in pixels.</param>
    /// <param name="output">Destination pixel bytes.</param>
    /// <param name="outputWidth">Output width in pixels.</param>
    /// <param name="outputHeight">Output height in pixels.</param>
    /// <param name="layout">Input pixel layout (override output layout via <see cref="ImageResizeOptions.OutputLayout"/>).</param>
    /// <param name="dataType">Input sample data type (override output type via <see cref="ImageResizeOptions.OutputDataType"/>).</param>
    /// <param name="inputStrideBytes">Input scanline stride in bytes (<c>0</c> = packed, negative = bottom-up).</param>
    /// <param name="outputStrideBytes">Output scanline stride in bytes (<c>0</c> = packed, negative = bottom-up).</param>
    /// <param name="options">Optional extended resampling options.</param>
    public static void ResizeMemory(
        ReadOnlyMemory<byte> input,
        int inputWidth,
        int inputHeight,
        Memory<byte> output,
        int outputWidth,
        int outputHeight,
        ResizePixelLayout layout,
        ResizeDataType dataType,
        int inputStrideBytes = 0,
        int outputStrideBytes = 0,
        ImageResizeOptions? options = null)
    {
        ImageResizeOptions resolved = options ?? ImageResizeOptions.Default;
        if (ResizeGpu.TryResize(input.Span, inputWidth, inputHeight, inputStrideBytes, output.Span, outputWidth, outputHeight, outputStrideBytes, layout, dataType, resolved))
        {
            return;
        }

        ResizePlan plan = ResizePlan.Create(
            inputWidth,
            inputHeight,
            outputWidth,
            outputHeight,
            layout,
            dataType,
            resolved);
        plan.ExecuteMemory(input, output, inputStrideBytes, outputStrideBytes);
    }

    /// <summary>
    /// Resizes raw pixel bytes and returns a newly allocated tightly packed output byte array.
    /// </summary>
    /// <param name="input">Source pixel bytes.</param>
    /// <param name="inputWidth">Input width in pixels.</param>
    /// <param name="inputHeight">Input height in pixels.</param>
    /// <param name="outputWidth">Output width in pixels.</param>
    /// <param name="outputHeight">Output height in pixels.</param>
    /// <param name="layout">Input pixel layout.</param>
    /// <param name="dataType">Input sample data type.</param>
    /// <param name="inputStrideBytes">Input scanline stride in bytes (<c>0</c> = packed, negative = bottom-up).</param>
    /// <param name="options">Optional extended resampling options.</param>
    /// <returns>The resized pixel bytes.</returns>
    public static byte[] Resize(
        ReadOnlySpan<byte> input,
        int inputWidth,
        int inputHeight,
        int outputWidth,
        int outputHeight,
        ResizePixelLayout layout,
        ResizeDataType dataType,
        int inputStrideBytes = 0,
        ImageResizeOptions? options = null)
    {
        StbImageResizer resizer = StbImageResizer.Create(
            inputWidth,
            inputHeight,
            outputWidth,
            outputHeight,
            layout,
            dataType,
            options);
        return resizer.ResizeToArray(input, inputStrideBytes);
    }

    /// <summary>
    /// Resizes an 8-bit <see cref="ImageBuffer{T}"/> and returns a new <see cref="ImageBuffer{T}"/>.
    /// </summary>
    /// <param name="source">Source 8-bit image buffer.</param>
    /// <param name="outputWidth">Output width in pixels.</param>
    /// <param name="outputHeight">Output height in pixels.</param>
    /// <param name="dataType">
    /// Sample transfer curve (defaults to <see cref="ResizeDataType.UInt8Srgb"/>; must be an 8-bit type).
    /// </param>
    /// <param name="options">Optional extended resampling options.</param>
    /// <returns>A newly allocated resized <see cref="ImageBuffer{T}"/>.</returns>
    public static ImageBuffer<byte> Resize(
        ImageBuffer<byte> source,
        int outputWidth,
        int outputHeight,
        ResizeDataType dataType = ResizeDataType.UInt8Srgb,
        ImageResizeOptions? options = null)
    {
        Guard.ThrowIfNull(source, nameof(source));
        if (dataType != ResizeDataType.UInt8Linear
            && dataType != ResizeDataType.UInt8Srgb
            && dataType != ResizeDataType.UInt8SrgbAlpha)
        {
            throw new StbException(
                StbErrorCode.InvalidArgument,
                $"ImageBuffer<byte> Resize requires an 8-bit dataType (got {dataType}).");
        }

        ImageResizeOptions opts = options ?? ImageResizeOptions.Default;
        if (opts.OutputDataType is { } outType
            && outType != ResizeDataType.UInt8Linear
            && outType != ResizeDataType.UInt8Srgb
            && outType != ResizeDataType.UInt8SrgbAlpha)
        {
            throw new StbException(
                StbErrorCode.InvalidArgument,
                $"ImageBuffer<byte> Resize requires an 8-bit OutputDataType (got {outType}).");
        }

        ResizePixelLayout layout = ResizeGeometry.FromColorComponents(source.Components);
        ResizePlan plan = ResizePlan.Create(
            source.Width,
            source.Height,
            outputWidth,
            outputHeight,
            layout,
            dataType,
            opts);

        long outputBytesLong = (long)outputWidth * outputHeight * plan.Channels * plan.OutputTypeSize;
        if (outputBytesLong > ArrayAllocation.MaxByteArrayLength)
        {
            throw new StbException(
                StbErrorCode.LimitExceeded,
                $"Output buffer size {outputBytesLong} exceeds maximum array length.");
        }

        byte[] dst = plan.IsEmptyRegion || plan.OutputSubWidth != outputWidth || plan.OutputSubHeight != outputHeight
            ? new byte[(int)outputBytesLong]
            : ArrayAllocation.AllocateUninitialized<byte>((int)outputBytesLong);

        plan.ExecuteMemory(source.ReadOnlyMemory, dst.AsMemory(), 0, 0);
        return new ImageBuffer<byte>(outputWidth, outputHeight, source.Components, source.SourceComponents, dst);
    }

    /// <summary>
    /// Resizes a 16-bit linear <see cref="ImageBuffer{T}"/> and returns a new <see cref="ImageBuffer{T}"/>.
    /// </summary>
    /// <param name="source">Source 16-bit image buffer.</param>
    /// <param name="outputWidth">Output width in pixels.</param>
    /// <param name="outputHeight">Output height in pixels.</param>
    /// <param name="options">Optional extended resampling options.</param>
    /// <returns>A newly allocated resized <see cref="ImageBuffer{T}"/>.</returns>
    public static ImageBuffer<ushort> Resize(
        ImageBuffer<ushort> source,
        int outputWidth,
        int outputHeight,
        ImageResizeOptions? options = null)
    {
        Guard.ThrowIfNull(source, nameof(source));
        ImageResizeOptions opts = options ?? ImageResizeOptions.Default;
        if (opts.OutputDataType is { } outType && outType != ResizeDataType.UInt16Linear && outType != ResizeDataType.Fp16)
        {
            throw new StbException(
                StbErrorCode.InvalidArgument,
                $"ImageBuffer<ushort> Resize requires a 16-bit OutputDataType (got {outType}).");
        }

        ResizePixelLayout layout = ResizeGeometry.FromColorComponents(source.Components);
        ResizePlan plan = ResizePlan.Create(
            source.Width,
            source.Height,
            outputWidth,
            outputHeight,
            layout,
            ResizeDataType.UInt16Linear,
            opts);

        long sampleCount = (long)outputWidth * outputHeight * plan.Channels;
        if (sampleCount > ArrayAllocation.MaxByteArrayLength / sizeof(ushort))
        {
            throw new StbException(StbErrorCode.LimitExceeded, $"Output sample count {sampleCount} exceeds supported range.");
        }

        ushort[] dst = plan.IsEmptyRegion || plan.OutputSubWidth != outputWidth || plan.OutputSubHeight != outputHeight
            ? new ushort[(int)sampleCount]
            : ArrayAllocation.AllocateUninitialized<ushort>((int)sampleCount);

        plan.ExecuteMemory(source.ReadOnlyMemory, dst.AsMemory(), 0, 0);
        return new ImageBuffer<ushort>(outputWidth, outputHeight, source.Components, source.SourceComponents, dst);
    }

    /// <summary>
    /// Resizes a 32-bit float linear <see cref="ImageBuffer{T}"/> and returns a new <see cref="ImageBuffer{T}"/>.
    /// </summary>
    /// <param name="source">Source float image buffer.</param>
    /// <param name="outputWidth">Output width in pixels.</param>
    /// <param name="outputHeight">Output height in pixels.</param>
    /// <param name="options">Optional extended resampling options.</param>
    /// <returns>A newly allocated resized <see cref="ImageBuffer{T}"/>.</returns>
    public static ImageBuffer<float> Resize(
        ImageBuffer<float> source,
        int outputWidth,
        int outputHeight,
        ImageResizeOptions? options = null)
    {
        Guard.ThrowIfNull(source, nameof(source));
        ImageResizeOptions opts = options ?? ImageResizeOptions.Default;
        if (opts.OutputDataType is { } outType && outType != ResizeDataType.Fp32)
        {
            throw new StbException(
                StbErrorCode.InvalidArgument,
                $"ImageBuffer<float> Resize requires OutputDataType.Fp32 (got {outType}).");
        }

        ResizePixelLayout layout = ResizeGeometry.FromColorComponents(source.Components);
        ResizePlan plan = ResizePlan.Create(
            source.Width,
            source.Height,
            outputWidth,
            outputHeight,
            layout,
            ResizeDataType.Fp32,
            opts);

        long sampleCount = (long)outputWidth * outputHeight * plan.Channels;
        if (sampleCount > ArrayAllocation.MaxByteArrayLength / sizeof(float))
        {
            throw new StbException(StbErrorCode.LimitExceeded, $"Output sample count {sampleCount} exceeds supported range.");
        }

        float[] dst = plan.IsEmptyRegion || plan.OutputSubWidth != outputWidth || plan.OutputSubHeight != outputHeight
            ? new float[(int)sampleCount]
            : ArrayAllocation.AllocateUninitialized<float>((int)sampleCount);

        plan.ExecuteMemory(source.ReadOnlyMemory, dst.AsMemory(), 0, 0);
        return new ImageBuffer<float>(outputWidth, outputHeight, source.Components, source.SourceComponents, dst);
    }
}
