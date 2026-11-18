using System.Buffers;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Stb.Managed.Internal;

internal readonly struct ColumnSlice
{
    internal ColumnSlice(int outStart, int outWidth, int inN0, int inSpan)
    {
        OutStart = outStart;
        OutWidth = outWidth;
        InN0 = inN0;
        InSpan = inSpan;
    }

    internal int OutStart { get; }

    internal int OutWidth { get; }

    internal int InN0 { get; }

    internal int InSpan { get; }
}

internal sealed class ResizePlan
{
    // Target working-set size per ring buffer slice (~512 KB fits per-core L2 caches, matching stb2's
    // working-set budget and avoiding multi-pass horizontal overlap on 4K images while bounding 8K+ widths).
    private const int TargetRingBytes = 512 * 1024;
    private const int MaxSingleSliceRingBytes = TargetRingBytes + (TargetRingBytes / 8);

    // Minimum pixel count before MaxDegreeOfParallelism = 0 (Auto) engages Parallel.For.
    private const long AutoParallelPixelThreshold = 131_072;

    internal int InputWidth { get; }

    internal int InputHeight { get; }

    internal int OutputWidth { get; }

    internal int OutputHeight { get; }

    internal ResizePixelLayout PublicInputLayout { get; }

    internal ResizePixelLayout PublicOutputLayout { get; }

    internal InternalPixelLayout InputLayout { get; }

    internal InternalPixelLayout OutputLayout { get; }

    internal ResizeDataType PublicInputDataType { get; }

    internal ResizeDataType PublicOutputDataType { get; }

    internal ResizeDataType EffectiveInputDataType { get; }

    internal ResizeDataType EffectiveOutputDataType { get; }

    internal int Channels { get; }

    internal int EffectiveChannels { get; }

    internal int InputTypeSize { get; }

    internal int OutputTypeSize { get; }

    internal AlphaWeightingKind AlphaWeighting { get; }

    internal bool Flip3ChOnDecode { get; }

    internal bool Flip3ChOnEncode { get; }

    internal bool Unscaled { get; }

    internal int OutputOffsetX { get; }

    internal int OutputOffsetY { get; }

    internal int OutputSubWidth { get; }

    internal int OutputSubHeight { get; }

    internal bool IsEmptyRegion { get; }

    internal ResizeSampler? Horizontal { get; }

    internal ResizeSampler? Vertical { get; }

    internal bool VerticalFirst { get; }

    internal int SliceInN0 { get; }

    internal int SliceInSpan { get; }

    internal int RingEntries { get; }

    internal ColumnSlice[] ColumnSlices { get; }

    internal int MaxSliceInSpan { get; }

    internal int MaxSliceOutWidth { get; }

    internal StbOptions ExecutionOptions { get; }

    private ResizePlan(
        int inputWidth,
        int inputHeight,
        int outputWidth,
        int outputHeight,
        ResizePixelLayout publicInputLayout,
        ResizePixelLayout publicOutputLayout,
        InternalPixelLayout inputLayout,
        InternalPixelLayout outputLayout,
        ResizeDataType publicInputDataType,
        ResizeDataType publicOutputDataType,
        ResizeDataType effectiveInputDataType,
        ResizeDataType effectiveOutputDataType,
        int channels,
        int effectiveChannels,
        AlphaWeightingKind alphaWeighting,
        bool flip3ChOnDecode,
        bool flip3ChOnEncode,
        bool unscaled,
        int outputOffsetX,
        int outputOffsetY,
        int outputSubWidth,
        int outputSubHeight,
        bool isEmptyRegion,
        ResizeSampler? horizontal,
        ResizeSampler? vertical,
        bool verticalFirst,
        int sliceInN0,
        int sliceInSpan,
        int ringEntries,
        ColumnSlice[] columnSlices,
        int maxSliceInSpan,
        int maxSliceOutWidth,
        StbOptions executionOptions)
    {
        InputWidth = inputWidth;
        InputHeight = inputHeight;
        OutputWidth = outputWidth;
        OutputHeight = outputHeight;
        PublicInputLayout = publicInputLayout;
        PublicOutputLayout = publicOutputLayout;
        InputLayout = inputLayout;
        OutputLayout = outputLayout;
        PublicInputDataType = publicInputDataType;
        PublicOutputDataType = publicOutputDataType;
        EffectiveInputDataType = effectiveInputDataType;
        EffectiveOutputDataType = effectiveOutputDataType;
        Channels = channels;
        EffectiveChannels = effectiveChannels;
        InputTypeSize = ResizeGeometry.GetDataTypeSize(effectiveInputDataType);
        OutputTypeSize = ResizeGeometry.GetDataTypeSize(effectiveOutputDataType);
        AlphaWeighting = alphaWeighting;
        Flip3ChOnDecode = flip3ChOnDecode;
        Flip3ChOnEncode = flip3ChOnEncode;
        Unscaled = unscaled;
        OutputOffsetX = outputOffsetX;
        OutputOffsetY = outputOffsetY;
        OutputSubWidth = outputSubWidth;
        OutputSubHeight = outputSubHeight;
        IsEmptyRegion = isEmptyRegion;
        Horizontal = horizontal;
        Vertical = vertical;
        VerticalFirst = verticalFirst;
        SliceInN0 = sliceInN0;
        SliceInSpan = sliceInSpan;
        RingEntries = ringEntries;
        ColumnSlices = columnSlices;
        MaxSliceInSpan = maxSliceInSpan;
        MaxSliceOutWidth = maxSliceOutWidth;
        ExecutionOptions = executionOptions;
    }

    internal static ResizePlan Create(
        int inputWidth,
        int inputHeight,
        int outputWidth,
        int outputHeight,
        ResizePixelLayout inputLayoutPublic,
        ResizeDataType inputDataType,
        ImageResizeOptions options)
    {
        ImageResizeOptions.ValidatePixelLayout(inputLayoutPublic, nameof(inputLayoutPublic));
        ImageResizeOptions.ValidateDataType(inputDataType, nameof(inputDataType));

        ResizePixelLayout outputLayoutPublic = options.OutputLayout ?? inputLayoutPublic;
        ResizeDataType outputDataType = options.OutputDataType ?? inputDataType;

        InternalPixelLayout inputLayout = ResizeGeometry.ToInternalLayout(inputLayoutPublic);
        InternalPixelLayout outputLayout = ResizeGeometry.ToInternalLayout(outputLayoutPublic);

        int inChannels = ResizeGeometry.GetChannelCount(inputLayout);
        int outChannels = ResizeGeometry.GetChannelCount(outputLayout);
        if (inChannels != outChannels)
        {
            throw new StbException(
                StbErrorCode.InvalidArgument,
                $"Input layout {inputLayoutPublic} ({inChannels} channels) and output layout {outputLayoutPublic} ({outChannels} channels) must have the same channel count.");
        }

        StbLimits limits = options.Options.Limits;
        ValidateDimensions(inputWidth, inputHeight, inChannels * ResizeGeometry.GetDataTypeSize(inputDataType), "input", limits);
        ValidateDimensions(outputWidth, outputHeight, outChannels * ResizeGeometry.GetDataTypeSize(outputDataType), "output", limits);

        if (options.PixelSubrect.HasValue && (options.InputSubrect.HasValue || options.OutputSubrect.HasValue))
        {
            throw new StbException(
                StbErrorCode.InvalidArgument,
                "PixelSubrect cannot be combined with InputSubrect or OutputSubrect.");
        }

        // Resolve region transform matching stbir_set_pixel_subrect / stbir_set_input_subrect / stbir_set_output_pixel_subrect.
        double s0 = 0.0;
        double t0 = 0.0;
        double s1 = 1.0;
        double t1 = 1.0;
        int sN0 = 0;
        int sN1 = 1;
        int sD = 1;
        int tN0 = 0;
        int tN1 = 1;
        int tD = 1;
        int outSubX = 0;
        int outSubY = 0;
        int outSubW = outputWidth;
        int outSubH = outputHeight;

        if (options.PixelSubrect is { } pixelRect)
        {
            if (pixelRect.Width < 0 || pixelRect.Height < 0)
            {
                throw new StbException(
                    StbErrorCode.InvalidArgument,
                    $"PixelSubrect dimensions must be non-negative (got {pixelRect.Width}x{pixelRect.Height}).");
            }

            if (pixelRect.Width == 0 || pixelRect.Height == 0)
            {
                return CreateEmpty(
                    inputWidth, inputHeight, outputWidth, outputHeight,
                    inputLayoutPublic, outputLayoutPublic, inputLayout, outputLayout,
                    inputDataType, outputDataType, inChannels, options.Options);
            }

            outSubX = pixelRect.X;
            outSubY = pixelRect.Y;
            outSubW = pixelRect.Width;
            outSubH = pixelRect.Height;
            s0 = (double)pixelRect.X / outputWidth;
            t0 = (double)pixelRect.Y / outputHeight;
            s1 = (double)((long)pixelRect.X + pixelRect.Width) / outputWidth;
            t1 = (double)((long)pixelRect.Y + pixelRect.Height) / outputHeight;
            sN0 = pixelRect.X;
            sN1 = checked(pixelRect.X + pixelRect.Width);
            sD = outputWidth;
            tN0 = pixelRect.Y;
            tN1 = checked(pixelRect.Y + pixelRect.Height);
            tD = outputHeight;
        }
        else
        {
            if (options.InputSubrect is { } inRect)
            {
                if (double.IsNaN(inRect.S0) || double.IsNaN(inRect.T0) || double.IsNaN(inRect.S1) || double.IsNaN(inRect.T1)
                    || double.IsInfinity(inRect.S0) || double.IsInfinity(inRect.T0) || double.IsInfinity(inRect.S1) || double.IsInfinity(inRect.T1))
                {
                    throw new StbException(StbErrorCode.InvalidArgument, "InputSubrect coordinates must be finite numbers.");
                }

                if (inRect.S0 > inRect.S1 || inRect.T0 > inRect.T1)
                {
                    throw new StbException(
                        StbErrorCode.InvalidArgument,
                        $"InputSubrect requires S0 <= S1 and T0 <= T1 (got [{inRect.S0}, {inRect.T0}]..[{inRect.S1}, {inRect.T1}]).");
                }

                if (inRect.S0 == inRect.S1 || inRect.T0 == inRect.T1)
                {
                    return CreateEmpty(
                        inputWidth, inputHeight, outputWidth, outputHeight,
                        inputLayoutPublic, outputLayoutPublic, inputLayout, outputLayout,
                        inputDataType, outputDataType, inChannels, options.Options);
                }

                s0 = inRect.S0;
                t0 = inRect.T0;
                s1 = inRect.S1;
                t1 = inRect.T1;
                sD = 0;
                tD = 0;
            }

            if (options.OutputSubrect is { } outRect)
            {
                if (outRect.Width < 0 || outRect.Height < 0)
                {
                    throw new StbException(
                        StbErrorCode.InvalidArgument,
                        $"OutputSubrect dimensions must be non-negative (got {outRect.Width}x{outRect.Height}).");
                }

                if (outRect.Width == 0 || outRect.Height == 0)
                {
                    return CreateEmpty(
                        inputWidth, inputHeight, outputWidth, outputHeight,
                        inputLayoutPublic, outputLayoutPublic, inputLayout, outputLayout,
                        inputDataType, outputDataType, inChannels, options.Options);
                }

                outSubX = outRect.X;
                outSubY = outRect.Y;
                outSubW = outRect.Width;
                outSubH = outRect.Height;
            }
        }

        int offsetX = outSubX;
        int offsetY = outSubY;
        bool hOk = ResizeGeometry.CalculateRegionTransform(
            out ResizeScaleInfo hScale, outputWidth, ref offsetX, outSubW, inputWidth, s0, s1, sN0, sN1, sD);
        bool vOk = ResizeGeometry.CalculateRegionTransform(
            out ResizeScaleInfo vScale, outputHeight, ref offsetY, outSubH, inputHeight, t0, t1, tN0, tN1, tD);

        if (!hOk || !vOk)
        {
            return CreateEmpty(
                inputWidth, inputHeight, outputWidth, outputHeight,
                inputLayoutPublic, outputLayoutPublic, inputLayout, outputLayout,
                inputDataType, outputDataType, inChannels, options.Options);
        }

        ResizeSampler horizontal = ResizeSampler.Build(
            in hScale,
            options.HorizontalFilter,
            options.HorizontalCustomFilter,
            options.HorizontalEdge,
            alwaysGather: true,
            isHorizontal: true,
            limits);

        ResizeSampler vertical = ResizeSampler.Build(
            in vScale,
            options.VerticalFilter,
            options.VerticalCustomFilter,
            options.VerticalEdge,
            alwaysGather: false,
            isHorizontal: false,
            limits);

        AlphaWeightingKind alphaWeighting = ResizeGeometry.GetAlphaWeighting(
            horizontal.ResolvedFilter,
            vertical.ResolvedFilter,
            options.HorizontalCustomFilter is not null,
            options.VerticalCustomFilter is not null,
            inputLayout,
            outputLayout,
            options.FastAlpha,
            inChannels,
            out int effectiveChannels);

        bool flip3Ch = ResizeGeometry.NeedsThreeChannelFlip(inputLayout, outputLayout);
        bool flip3ChOnDecode = flip3Ch && !((float)hScale.Scale < 1.0f);
        bool flip3ChOnEncode = flip3Ch && ((float)hScale.Scale < 1.0f);

        bool pointSampled = options.HorizontalCustomFilter is null
            && options.VerticalCustomFilter is null
            && horizontal.ResolvedFilter == ResizeFilter.PointSample
            && vertical.ResolvedFilter == ResizeFilter.PointSample;

        ResizeDataType effectiveInputType = inputDataType;
        ResizeDataType effectiveOutputType = outputDataType;
        if (pointSampled
            && (effectiveInputType == ResizeDataType.UInt8Srgb || effectiveInputType == ResizeDataType.UInt8SrgbAlpha)
            && (effectiveOutputType == ResizeDataType.UInt8Srgb || effectiveOutputType == ResizeDataType.UInt8SrgbAlpha))
        {
            effectiveInputType = ResizeDataType.UInt8Linear;
            effectiveOutputType = ResizeDataType.UInt8Linear;
        }

        bool hasWeightPass = flip3ChOnDecode
            || alphaWeighting == AlphaWeightingKind.FancyBoth
            || alphaWeighting == AlphaWeightingKind.SimpleBoth
            || alphaWeighting == AlphaWeightingKind.WeightInOnly;
        bool hasUnweightPass = flip3ChOnEncode
            || alphaWeighting == AlphaWeightingKind.FancyBoth
            || alphaWeighting == AlphaWeightingKind.SimpleBoth
            || alphaWeighting == AlphaWeightingKind.UnweightOutOnly;

        bool unscaled = !hasWeightPass && !hasUnweightPass
            && ((effectiveInputType == ResizeDataType.UInt8Linear && effectiveOutputType == ResizeDataType.UInt8Linear)
                || (effectiveInputType == ResizeDataType.UInt16Linear && effectiveOutputType == ResizeDataType.UInt16Linear));

        bool verticalFirst = ResizeGeometry.ShouldDoVerticalFirst(
            effectiveChannels,
            horizontal.FilterPixelWidth,
            horizontal.ScaleInfo.InputFullSize,
            horizontal.ScaleInfo.OutputSubSize,
            horizontal.Edge,
            horizontal.IsPeriod1,
            vertical.FilterPixelWidth,
            vertical.ScaleInfo.InputFullSize,
            vertical.ScaleInfo.OutputSubSize,
            vertical.Edge);

        int sliceInN0 = horizontal.ExtentsLowest;
        int sliceInSpan = Math.Max(1, horizontal.ExtentsHighest - horizontal.ExtentsLowest + 1);
        int ringEntries = vertical.RingEntriesCount;

        ColumnSlice[] columnSlices = BuildColumnSlices(
            horizontal,
            hScale.OutputSubSize,
            sliceInN0,
            sliceInSpan,
            effectiveChannels,
            ringEntries,
            verticalFirst,
            out int maxSliceInSpan,
            out int maxSliceOutWidth);

        // Validate scratch working-memory footprint against StbLimits.
        long ringRowFloats = ((long)(verticalFirst ? maxSliceInSpan : maxSliceOutWidth) * effectiveChannels) + ResizeKernels.RowPaddingFloats;
        long scratchBytes = ((((long)maxSliceInSpan * effectiveChannels) + ResizeKernels.RowPaddingFloats)
            + (((long)maxSliceOutWidth * effectiveChannels) + ResizeKernels.RowPaddingFloats)
            + (ringRowFloats * ringEntries)) * sizeof(float);
        limits.ValidateAllocation(scratchBytes);

        return new ResizePlan(
            inputWidth,
            inputHeight,
            outputWidth,
            outputHeight,
            inputLayoutPublic,
            outputLayoutPublic,
            inputLayout,
            outputLayout,
            inputDataType,
            outputDataType,
            effectiveInputType,
            effectiveOutputType,
            inChannels,
            effectiveChannels,
            alphaWeighting,
            flip3ChOnDecode,
            flip3ChOnEncode,
            unscaled,
            offsetX,
            offsetY,
            hScale.OutputSubSize,
            vScale.OutputSubSize,
            isEmptyRegion: false,
            horizontal,
            vertical,
            verticalFirst,
            sliceInN0,
            sliceInSpan,
            ringEntries,
            columnSlices,
            maxSliceInSpan,
            maxSliceOutWidth,
            options.Options);
    }

    private static ColumnSlice[] BuildColumnSlices(
        ResizeSampler horizontal,
        int outSubW,
        int fullInN0,
        int fullInSpan,
        int eff,
        int ringEntries,
        bool verticalFirst,
        out int maxSliceInSpan,
        out int maxSliceOutWidth)
    {
        long fullRingRowFloats = (long)(verticalFirst ? fullInSpan : outSubW) * eff;
        long fullRingBytes = fullRingRowFloats * ringEntries * sizeof(float);

        if (fullRingBytes <= MaxSingleSliceRingBytes || outSubW <= 64)
        {
            maxSliceInSpan = fullInSpan;
            maxSliceOutWidth = outSubW;
            return [new ColumnSlice(0, outSubW, fullInN0, fullInSpan)];
        }

        double bytesPerOutCol = (double)fullRingBytes / outSubW;
        int targetCols = Math.Max(64, (int)(TargetRingBytes / bytesPerOutCol));
        targetCols = (targetCols + 15) & ~15;

        if (targetCols >= outSubW)
        {
            maxSliceInSpan = fullInSpan;
            maxSliceOutWidth = outSubW;
            return [new ColumnSlice(0, outSubW, fullInN0, fullInSpan)];
        }

        ResizeContributor[] contribs = horizontal.Contributors;
        int widest = horizontal.ExtentsWidest;
        List<ColumnSlice> slices = new((outSubW / targetCols) + 2);
        int maxIn = 1;
        int maxOut = 1;

        int cur = 0;
        while (cur < outSubW)
        {
            int w = Math.Min(targetCols, outSubW - cur);
            int remaining = outSubW - (cur + w);
            if (remaining > 0 && remaining < 32)
            {
                w += remaining;
            }

            int minN0 = int.MaxValue;
            int maxN1 = int.MinValue;
            for (int x = cur; x < cur + w; x++)
            {
                ResizeContributor c = contribs[x];
                if (c.N1 >= c.N0)
                {
                    if (c.N0 < minN0)
                    {
                        minN0 = c.N0;
                    }

                    // The fixed-width horizontal kernels read past a short contributor's n1, so the
                    // slice has to decode up to the furthest read (stbir__build_column_slices).
                    int lastRead = c.N0 + ResizeSampler.HorizontalReadExtent(c.N1 - c.N0 + 1, widest) - 1;
                    if (lastRead > maxN1)
                    {
                        maxN1 = lastRead;
                    }
                }
            }

            int inN0;
            int inSpan;
            if (maxN1 < minN0)
            {
                inN0 = 0;
                inSpan = 0;
            }
            else
            {
                inN0 = minN0;
                inSpan = maxN1 - minN0 + 1;
            }

            if (inSpan > maxIn)
            {
                maxIn = inSpan;
            }

            if (w > maxOut)
            {
                maxOut = w;
            }

            slices.Add(new ColumnSlice(cur, w, inN0, inSpan));
            cur += w;
        }

        maxSliceInSpan = maxIn;
        maxSliceOutWidth = maxOut;
        return [.. slices];
    }

    private static ResizePlan CreateEmpty(
        int inputWidth,
        int inputHeight,
        int outputWidth,
        int outputHeight,
        ResizePixelLayout inputLayoutPublic,
        ResizePixelLayout outputLayoutPublic,
        InternalPixelLayout inputLayout,
        InternalPixelLayout outputLayout,
        ResizeDataType inputDataType,
        ResizeDataType outputDataType,
        int channels,
        StbOptions executionOptions) =>
        new(
            inputWidth,
            inputHeight,
            outputWidth,
            outputHeight,
            inputLayoutPublic,
            outputLayoutPublic,
            inputLayout,
            outputLayout,
            inputDataType,
            outputDataType,
            inputDataType,
            outputDataType,
            channels,
            channels,
            AlphaWeightingKind.None,
            flip3ChOnDecode: false,
            flip3ChOnEncode: false,
            unscaled: false,
            outputOffsetX: 0,
            outputOffsetY: 0,
            outputSubWidth: 0,
            outputSubHeight: 0,
            isEmptyRegion: true,
            horizontal: null,
            vertical: null,
            verticalFirst: false,
            sliceInN0: 0,
            sliceInSpan: 0,
            ringEntries: 0,
            columnSlices: [],
            maxSliceInSpan: 0,
            maxSliceOutWidth: 0,
            executionOptions: executionOptions);

    internal int ResolveDegreeOfParallelism()
    {
        if (IsEmptyRegion || OutputSubHeight < 8)
        {
            return 1;
        }

        long workPixels = Math.Max((long)InputWidth * InputHeight, (long)OutputSubWidth * OutputSubHeight);
        int dop = ExecutionOptions.ResolveDegreeOfParallelism(workPixels, AutoParallelPixelThreshold);
        if (dop <= 1)
        {
            return 1;
        }

        int maxBands = Math.Max(1, OutputSubHeight / 4);
        return Math.Min(dop, maxBands);
    }

    internal static void ValidateDimensions(int width, int height, int bytesPerPixel, string role, StbLimits limits)
    {
        if (width <= 0 || height <= 0)
        {
            throw new StbException(
                StbErrorCode.InvalidArgument,
                $"Resize {role} dimensions must be positive (got {width}x{height}).");
        }

        limits.ValidateDimensions(width, height, bytesPerPixel);
    }

    internal static int ResolveAndValidateBufferStride(
        int bufferLengthBytes,
        int width,
        int height,
        int channels,
        int typeSize,
        int strideBytes,
        string role)
    {
        long rowBytesLong = (long)width * channels * typeSize;
        if (rowBytesLong > int.MaxValue)
        {
            throw new StbException(
                StbErrorCode.LimitExceeded,
                $"Resize {role} row byte size {rowBytesLong} exceeds supported range.");
        }

        int rowBytes = (int)rowBytesLong;
        int effectiveStride;
        int absStride;

        if (strideBytes == 0)
        {
            effectiveStride = rowBytes;
            absStride = rowBytes;
        }
        else if (strideBytes == int.MinValue)
        {
            throw new StbException(
                StbErrorCode.InvalidArgument,
                $"Resize {role} stride cannot be int.MinValue.");
        }
        else
        {
            effectiveStride = strideBytes;
            absStride = strideBytes < 0 ? -strideBytes : strideBytes;
            if (absStride < rowBytes)
            {
                throw new StbException(
                    StbErrorCode.InvalidArgument,
                    $"Resize {role} |stride| ({absStride}) must be at least row byte width ({rowBytes}).");
            }
        }

        long minRequired = ((long)(height - 1) * absStride) + rowBytes;
        if (bufferLengthBytes < minRequired)
        {
            throw new StbException(
                StbErrorCode.InvalidArgument,
                $"Resize {role} buffer length ({bufferLengthBytes} bytes) is smaller than required {minRequired} bytes for {width}x{height}x{channels} (stride {effectiveStride}).");
        }

        return effectiveStride;
    }

    internal void Execute(
        ReadOnlySpan<byte> input,
        Span<byte> output,
        int inputStrideBytes,
        int outputStrideBytes)
    {
        int resolvedInStride = ResolveAndValidateBufferStride(
            input.Length, InputWidth, InputHeight, Channels, InputTypeSize, inputStrideBytes, "input");
        int resolvedOutStride = ResolveAndValidateBufferStride(
            output.Length, OutputWidth, OutputHeight, Channels, OutputTypeSize, outputStrideBytes, "output");

        if (IsEmptyRegion)
        {
            return;
        }

        int dop = ResolveDegreeOfParallelism();
        if (dop <= 1)
        {
            ResizeEngine.ExecuteSimdBand(this, input, output, resolvedInStride, resolvedOutStride, 0, OutputSubHeight);
            return;
        }

        // Safe zero-unsafe parallel bridge for Span inputs: rent pooled arrays covering the active buffer ranges
        // and write back only the output sub-rectangle rows so padding bytes and untouched regions are preserved.
        int absInStride = resolvedInStride < 0 ? -resolvedInStride : resolvedInStride;
        int inRowBytes = InputWidth * Channels * InputTypeSize;
        int requiredInBytes = ((InputHeight - 1) * absInStride) + inRowBytes;

        int absOutStride = resolvedOutStride < 0 ? -resolvedOutStride : resolvedOutStride;
        int outRowBytes = OutputWidth * Channels * OutputTypeSize;
        int requiredOutBytes = ((OutputHeight - 1) * absOutStride) + outRowBytes;

        byte[] inRent = ArrayPool<byte>.Shared.Rent(requiredInBytes);
        byte[] outRent = ArrayPool<byte>.Shared.Rent(requiredOutBytes);
        try
        {
            input.Slice(0, requiredInBytes).CopyTo(inRent);

            ResizeEngine.ExecuteSimdParallel<byte>(
                this,
                inRent.AsMemory(0, requiredInBytes),
                outRent.AsMemory(0, requiredOutBytes),
                resolvedInStride,
                resolvedOutStride,
                dop);

            // Copy back only the modified output sub-rectangle scanlines to preserve any untouched pixels/padding.
            int pixelBytes = Channels * OutputTypeSize;
            int subRowBytes = OutputSubWidth * pixelBytes;
            if (OutputSubWidth == OutputWidth && OutputSubHeight == OutputHeight && absOutStride == outRowBytes)
            {
                outRent.AsSpan(0, requiredOutBytes).CopyTo(output);
            }
            else
            {
                int xByteOffset = OutputOffsetX * pixelBytes;
                ReadOnlySpan<byte> srcOut = outRent.AsSpan(0, requiredOutBytes);
                for (int subY = 0; subY < OutputSubHeight; subY++)
                {
                    int dstY = OutputOffsetY + subY;
                    int rowByteOffset = resolvedOutStride > 0
                        ? dstY * resolvedOutStride
                        : (OutputHeight - 1 - dstY) * (-resolvedOutStride);
                    int off = rowByteOffset + xByteOffset;
                    srcOut.Slice(off, subRowBytes).CopyTo(output.Slice(off, subRowBytes));
                }
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(inRent);
            ArrayPool<byte>.Shared.Return(outRent);
        }
    }

    internal void ExecuteMemory<T>(
        ReadOnlyMemory<T> input,
        Memory<T> output,
        int inputStrideBytes,
        int outputStrideBytes)
        where T : unmanaged
    {
        int inBytes = MemoryMarshal.AsBytes(input.Span).Length;
        int outBytes = MemoryMarshal.AsBytes(output.Span).Length;

        int resolvedInStride = ResolveAndValidateBufferStride(
            inBytes, InputWidth, InputHeight, Channels, InputTypeSize, inputStrideBytes, "input");
        int resolvedOutStride = ResolveAndValidateBufferStride(
            outBytes, OutputWidth, OutputHeight, Channels, OutputTypeSize, outputStrideBytes, "output");

        if (IsEmptyRegion)
        {
            return;
        }

        int dop = ResolveDegreeOfParallelism();
        if (dop <= 1)
        {
            ResizeEngine.ExecuteSimdBand(
                this,
                MemoryMarshal.AsBytes(input.Span),
                MemoryMarshal.AsBytes(output.Span),
                resolvedInStride,
                resolvedOutStride,
                0,
                OutputSubHeight);
            return;
        }

        ResizeEngine.ExecuteSimdParallel(this, input, output, resolvedInStride, resolvedOutStride, dop);
    }

    internal void ExecuteScalarForTest(
        ReadOnlySpan<byte> input,
        Span<byte> output,
        int inputStrideBytes,
        int outputStrideBytes)
    {
        int resolvedInStride = ResolveAndValidateBufferStride(
            input.Length, InputWidth, InputHeight, Channels, InputTypeSize, inputStrideBytes, "input");
        int resolvedOutStride = ResolveAndValidateBufferStride(
            output.Length, OutputWidth, OutputHeight, Channels, OutputTypeSize, outputStrideBytes, "output");

        if (IsEmptyRegion)
        {
            return;
        }

        ResizeEngine.ExecuteScalar(this, input, output, resolvedInStride, resolvedOutStride);
    }
}

internal static class ResizeEngine
{
    // =========================================================================
    // SIMD + L2 Column-Sliced + Deterministic Parallel Execution
    // =========================================================================

    internal static void ExecuteSimdParallel<T>(
        ResizePlan plan,
        ReadOnlyMemory<T> input,
        Memory<T> output,
        int inputStrideBytes,
        int outputStrideBytes,
        int bandCount)
        where T : unmanaged
    {
        int outSubH = plan.OutputSubHeight;
        ParallelOptions parallelOptions = new() { MaxDegreeOfParallelism = bandCount };

        Parallel.For(0, bandCount, parallelOptions, band =>
        {
            int yStart = (int)((long)band * outSubH / bandCount);
            int yEnd = (int)((long)(band + 1) * outSubH / bandCount);
            if (yStart < yEnd)
            {
                ExecuteSimdBand(
                    plan,
                    MemoryMarshal.AsBytes(input.Span),
                    MemoryMarshal.AsBytes(output.Span),
                    inputStrideBytes,
                    outputStrideBytes,
                    yStart,
                    yEnd);
            }
        });
    }

    internal static void ExecuteSimdBand(
        ResizePlan plan,
        ReadOnlySpan<byte> input,
        Span<byte> output,
        int inputStrideBytes,
        int outputStrideBytes,
        int yStart,
        int yEnd)
    {
        ResizeSampler vertical = plan.Vertical!;
        if (vertical.IsGather)
        {
            ExecuteVerticalGatherSimdBand(plan, input, output, inputStrideBytes, outputStrideBytes, yStart, yEnd);
        }
        else
        {
            ExecuteVerticalScatterSimdBand(plan, input, output, inputStrideBytes, outputStrideBytes, yStart, yEnd);
        }
    }

    private static void ExecuteVerticalGatherSimdBand(
        ResizePlan plan,
        ReadOnlySpan<byte> input,
        Span<byte> output,
        int inputStrideBytes,
        int outputStrideBytes,
        int yStart,
        int yEnd)
    {
        ResizeSampler horizontal = plan.Horizontal!;
        ResizeSampler vertical = plan.Vertical!;

        int eff = plan.EffectiveChannels;
        int ringEntries = plan.RingEntries;
        ColumnSlice[] slices = plan.ColumnSlices;

        int maxDecodeFloats = (plan.MaxSliceInSpan * eff) + ResizeKernels.RowPaddingFloats;
        int maxOutRowFloats = (plan.MaxSliceOutWidth * eff) + ResizeKernels.RowPaddingFloats;
        int ringRowStrideFloats = ((plan.VerticalFirst ? plan.MaxSliceInSpan : plan.MaxSliceOutWidth) * eff)
            + ResizeKernels.RowPaddingFloats;

        using PooledBuffer<float> decodeLease = new(maxDecodeFloats);
        using PooledBuffer<float> verticalLease = new(maxOutRowFloats);
        using PooledBuffer<float> ringLease = new(ringRowStrideFloats * ringEntries);

        Span<float> decodeBuffer = decodeLease.Span;
        Span<float> verticalBuffer = verticalLease.Span;
        Span<float> ringBuffer = ringLease.Span;

        ResizeContributor[] vContribs = vertical.Contributors;
        float[] vCoeffs = vertical.Coefficients;
        int vCoeffWidth = vertical.CoefficientWidth;
        int vMargin3 = vertical.FilterPixelMargin * 3;

        for (int s = 0; s < slices.Length; s++)
        {
            ColumnSlice slice = slices[s];
            int sliceOutX = slice.OutStart;
            int sliceOutW = slice.OutWidth;
            int sliceInN0 = slice.InN0;
            int sliceInSpan = slice.InSpan;

            int sliceOutFloats = sliceOutW * eff;
            if (sliceInSpan == 0)
            {
                verticalBuffer.Slice(0, sliceOutFloats).Clear();
                for (int y = yStart; y < yEnd; y++)
                {
                    EncodeScanlineSlice(plan, output, outputStrideBytes, verticalBuffer, y, sliceOutX, sliceOutW);
                }

                continue;
            }

            int sliceDecodeFloats = sliceInSpan * eff;
            int activeRingFloats = plan.VerticalFirst ? sliceDecodeFloats : sliceOutFloats;
            int sliceRingStride = activeRingFloats + ResizeKernels.RowPaddingFloats;

            int firstNonEmptyY = yStart;
            while (firstNonEmptyY < yEnd && vContribs[firstNonEmptyY].N1 < vContribs[firstNonEmptyY].N0)
            {
                firstNonEmptyY++;
            }

            int ringFirstScanline = firstNonEmptyY < yEnd
                ? vertical.LowestScanlineNeeded(firstNonEmptyY, Math.Min(vMargin3, (yEnd - 1) - firstNonEmptyY))
                : 0;
            int ringLastScanline = ringFirstScanline - 1;
            int ringBeginIndex = 0;

            for (int y = yStart; y < yEnd; y++)
            {
                ResizeContributor contrib = vContribs[y];
                int inFirst = contrib.N0;
                int inLast = contrib.N1;

                if (inLast < inFirst)
                {
                    verticalBuffer.Slice(0, sliceOutFloats).Clear();
                    EncodeScanlineSlice(plan, output, outputStrideBytes, verticalBuffer, y, sliceOutX, sliceOutW);
                    continue;
                }

                // Skip unneeded gap scanlines on steep downsamples without decoding them.
                // LowestScanlineNeeded(y, ...) is always <= inFirst, so skip the lookahead scan when caught up.
                if (ringLastScanline < inFirst - 1)
                {
                    int minNeeded = vertical.LowestScanlineNeeded(y, Math.Min(vMargin3, (yEnd - 1) - y));
                    if (ringLastScanline < minNeeded - 1)
                    {
                        ringFirstScanline = minNeeded;
                        ringLastScanline = minNeeded - 1;
                        ringBeginIndex = 0;
                    }
                }

                while (ringLastScanline < inLast)
                {
                    ringLastScanline++;
                    if ((ringLastScanline - ringFirstScanline) >= ringEntries)
                    {
                        ringFirstScanline++;
                        ringBeginIndex++;
                        if (ringBeginIndex == ringEntries)
                        {
                            ringBeginIndex = 0;
                        }
                    }

                    int slot = ResizeKernels.GetRingSlot(ringLastScanline, ringFirstScanline, ringBeginIndex, ringEntries);
                    Span<float> ringRow = ringBuffer.Slice(slot * sliceRingStride, sliceRingStride);

                    if (plan.VerticalFirst)
                    {
                        DecodeScanlineSlice(plan, input, inputStrideBytes, ringRow, ringLastScanline, sliceInN0, sliceInSpan);
                    }
                    else
                    {
                        DecodeScanlineSlice(plan, input, inputStrideBytes, decodeBuffer, ringLastScanline, sliceInN0, sliceInSpan);
                        ResizeKernels.ResampleHorizontalGather(horizontal, ringRow, decodeBuffer, sliceOutX, sliceOutW, sliceInN0, eff);
                    }
                }

                ReadOnlySpan<float> rowCoeffs = vCoeffs.AsSpan(y * vCoeffWidth, inLast - inFirst + 1);

                if (plan.VerticalFirst)
                {
                    ResizeKernels.ResampleVerticalGather(
                        decodeBuffer,
                        ringBuffer,
                        sliceRingStride,
                        activeRingFloats,
                        ringFirstScanline,
                        ringBeginIndex,
                        ringEntries,
                        inFirst,
                        inLast,
                        rowCoeffs);
                    ResizeKernels.ResampleHorizontalGather(horizontal, verticalBuffer, decodeBuffer, sliceOutX, sliceOutW, sliceInN0, eff);
                }
                else
                {
                    ResizeKernels.ResampleVerticalGather(
                        verticalBuffer,
                        ringBuffer,
                        sliceRingStride,
                        activeRingFloats,
                        ringFirstScanline,
                        ringBeginIndex,
                        ringEntries,
                        inFirst,
                        inLast,
                        rowCoeffs);
                }

                EncodeScanlineSlice(plan, output, outputStrideBytes, verticalBuffer, y, sliceOutX, sliceOutW);
            }
        }
    }

    private static void ExecuteVerticalScatterSimdBand(
        ResizePlan plan,
        ReadOnlySpan<byte> input,
        Span<byte> output,
        int inputStrideBytes,
        int outputStrideBytes,
        int yStart,
        int yEnd)
    {
        ResizeSampler horizontal = plan.Horizontal!;
        ResizeSampler vertical = plan.Vertical!;

        int eff = plan.EffectiveChannels;
        int bandHeight = yEnd - yStart;
        int bandRingEntries = Math.Min(plan.RingEntries, bandHeight);
        ColumnSlice[] slices = plan.ColumnSlices;

        int maxDecodeFloats = (plan.MaxSliceInSpan * eff) + ResizeKernels.RowPaddingFloats;
        int maxOutRowFloats = (plan.MaxSliceOutWidth * eff) + ResizeKernels.RowPaddingFloats;
        int ringRowStrideFloats = ((plan.VerticalFirst ? plan.MaxSliceInSpan : plan.MaxSliceOutWidth) * eff)
            + ResizeKernels.RowPaddingFloats;

        using PooledBuffer<float> decodeLease = new(maxDecodeFloats);
        using PooledBuffer<float> verticalLease = new(maxOutRowFloats);
        using PooledBuffer<float> ringLease = new(ringRowStrideFloats * bandRingEntries);
        using PooledBuffer<byte> initLease = new(bandRingEntries, clear: true);

        Span<float> decodeBuffer = decodeLease.Span;
        Span<float> verticalBuffer = verticalLease.Span;
        Span<float> ringBuffer = ringLease.Span;
        Span<byte> ringInitialized = initLease.Span;

        ResizeContributor[] vContribs = vertical.Contributors;
        float[] vCoeffs = vertical.Coefficients;
        int vCoeffWidth = vertical.CoefficientWidth;
        int vMargin = vertical.FilterPixelMargin;
        int startInputY = vertical.ExtentsLowest;
        int endInputY = vertical.ExtentsHighest + 1;

        for (int s = 0; s < slices.Length; s++)
        {
            ColumnSlice slice = slices[s];
            int sliceOutX = slice.OutStart;
            int sliceOutW = slice.OutWidth;
            int sliceInN0 = slice.InN0;
            int sliceInSpan = slice.InSpan;

            int sliceOutFloats = sliceOutW * eff;
            if (sliceInSpan == 0)
            {
                verticalBuffer.Slice(0, sliceOutFloats).Clear();
                for (int y = yStart; y < yEnd; y++)
                {
                    EncodeScanlineSlice(plan, output, outputStrideBytes, verticalBuffer, y, sliceOutX, sliceOutW);
                }

                continue;
            }

            int sliceDecodeFloats = sliceInSpan * eff;
            int activeRingFloats = plan.VerticalFirst ? sliceDecodeFloats : sliceOutFloats;
            int sliceRingStride = activeRingFloats + ResizeKernels.RowPaddingFloats;

            ringInitialized.Clear();
            int ringFirstScanline = yStart;
            int ringBeginIndex = 0;

            for (int inY = startInputY; inY < endInputY; inY++)
            {
                int idx = inY + vMargin;
                if ((uint)idx >= (uint)vContribs.Length)
                {
                    continue;
                }

                ResizeContributor contrib = vContribs[idx];
                int outFirst = contrib.N0;
                int outLast = contrib.N1;

                if (outLast < outFirst || outLast < yStart || outFirst >= yEnd)
                {
                    continue;
                }

                int coeffOffset = idx * vCoeffWidth;
                if (outFirst < yStart)
                {
                    coeffOffset += yStart - outFirst;
                    outFirst = yStart;
                }

                if (outLast >= yEnd)
                {
                    outLast = yEnd - 1;
                }

                while (outLast >= ringFirstScanline + bandRingEntries)
                {
                    EvictScatterRowSimd(
                        plan,
                        horizontal,
                        output,
                        outputStrideBytes,
                        ringBuffer,
                        ringInitialized,
                        verticalBuffer,
                        sliceRingStride,
                        activeRingFloats,
                        ringFirstScanline,
                        ringBeginIndex,
                        sliceOutX,
                        sliceOutW,
                        sliceInN0,
                        eff);

                    ringFirstScanline++;
                    ringBeginIndex++;
                    if (ringBeginIndex == bandRingEntries)
                    {
                        ringBeginIndex = 0;
                    }
                }

                DecodeScanlineSlice(plan, input, inputStrideBytes, decodeBuffer, inY, sliceInN0, sliceInSpan);

                ReadOnlySpan<float> scatterSource;
                if (plan.VerticalFirst)
                {
                    scatterSource = decodeBuffer;
                }
                else
                {
                    ResizeKernels.ResampleHorizontalGather(horizontal, verticalBuffer, decodeBuffer, sliceOutX, sliceOutW, sliceInN0, eff);
                    scatterSource = verticalBuffer;
                }

                for (int outY = outFirst; outY <= outLast; outY++)
                {
                    float c = vCoeffs[coeffOffset + (outY - outFirst)];
                    int slot = ResizeKernels.GetRingSlot(outY, ringFirstScanline, ringBeginIndex, bandRingEntries);
                    Span<float> dstRow = ringBuffer.Slice(slot * sliceRingStride, sliceRingStride);

                    if (ringInitialized[slot] == 0)
                    {
                        ResizeKernels.VerticalSet1(dstRow, scatterSource, c, activeRingFloats);
                        ringInitialized[slot] = 1;
                    }
                    else
                    {
                        ResizeKernels.VerticalAdd1(dstRow, scatterSource, c, activeRingFloats);
                    }
                }
            }

            while (ringFirstScanline < yEnd)
            {
                EvictScatterRowSimd(
                    plan,
                    horizontal,
                    output,
                    outputStrideBytes,
                    ringBuffer,
                    ringInitialized,
                    verticalBuffer,
                    sliceRingStride,
                    activeRingFloats,
                    ringFirstScanline,
                    ringBeginIndex,
                    sliceOutX,
                    sliceOutW,
                    sliceInN0,
                    eff);

                ringFirstScanline++;
                ringBeginIndex++;
                if (ringBeginIndex == bandRingEntries)
                {
                    ringBeginIndex = 0;
                }
            }
        }
    }

    private static void EvictScatterRowSimd(
        ResizePlan plan,
        ResizeSampler horizontal,
        Span<byte> output,
        int outputStrideBytes,
        Span<float> ringBuffer,
        Span<byte> ringInitialized,
        Span<float> verticalBuffer,
        int sliceRingStride,
        int activeRingFloats,
        int outY,
        int slot,
        int sliceOutX,
        int sliceOutW,
        int sliceInN0,
        int eff)
    {
        Span<float> ringRow = ringBuffer.Slice(slot * sliceRingStride, sliceRingStride);
        if (ringInitialized[slot] == 0)
        {
            ringRow.Slice(0, activeRingFloats).Clear();
        }

        if (plan.VerticalFirst)
        {
            ResizeKernels.ResampleHorizontalGather(horizontal, verticalBuffer, ringRow, sliceOutX, sliceOutW, sliceInN0, eff);
            EncodeScanlineSlice(plan, output, outputStrideBytes, verticalBuffer, outY, sliceOutX, sliceOutW);
        }
        else
        {
            EncodeScanlineSlice(plan, output, outputStrideBytes, ringRow, outY, sliceOutX, sliceOutW);
        }

        ringInitialized[slot] = 0;
    }

    // =========================================================================
    // Scalar Reference Execution (retained for SIMD/slicing differential tests)
    // =========================================================================

    internal static void ExecuteScalar(
        ResizePlan plan,
        ReadOnlySpan<byte> input,
        Span<byte> output,
        int inputStrideBytes,
        int outputStrideBytes)
    {
        ResizeSampler vertical = plan.Vertical!;
        if (vertical.IsGather)
        {
            ExecuteVerticalGatherScalar(plan, input, output, inputStrideBytes, outputStrideBytes);
        }
        else
        {
            ExecuteVerticalScatterScalar(plan, input, output, inputStrideBytes, outputStrideBytes);
        }
    }

    private static void ExecuteVerticalGatherScalar(
        ResizePlan plan,
        ReadOnlySpan<byte> input,
        Span<byte> output,
        int inputStrideBytes,
        int outputStrideBytes)
    {
        ResizeSampler horizontal = plan.Horizontal!;
        ResizeSampler vertical = plan.Vertical!;

        int eff = plan.EffectiveChannels;
        int outSubW = plan.OutputSubWidth;
        int outSubH = plan.OutputSubHeight;
        int sliceInN0 = plan.SliceInN0;
        int sliceInSpan = plan.SliceInSpan;
        int ringEntries = plan.RingEntries;

        int decodeFloats = sliceInSpan * eff;
        int outRowFloats = outSubW * eff;
        int ringRowFloats = plan.VerticalFirst ? decodeFloats : outRowFloats;
        int ringRowStride = ringRowFloats + ResizeKernels.RowPaddingFloats;

        using PooledBuffer<float> decodeLease = new(decodeFloats + ResizeKernels.RowPaddingFloats);
        using PooledBuffer<float> verticalLease = new(outRowFloats + ResizeKernels.RowPaddingFloats);
        using PooledBuffer<float> ringLease = new(ringRowStride * ringEntries);

        Span<float> decodeBuffer = decodeLease.Span;
        Span<float> verticalBuffer = verticalLease.Span;
        Span<float> ringBuffer = ringLease.Span;

        int maxLook = Math.Min(vertical.FilterPixelMargin * 3, outSubH - 1);
        int ringFirstScanline = vertical.LowestScanlineNeeded(0, maxLook);
        int ringLastScanline = ringFirstScanline - 1;
        int ringBeginIndex = 0;

        ResizeContributor[] vContribs = vertical.Contributors;
        float[] vCoeffs = vertical.Coefficients;
        int vCoeffWidth = vertical.CoefficientWidth;

        for (int y = 0; y < outSubH; y++)
        {
            ResizeContributor contrib = vContribs[y];
            int inFirst = contrib.N0;
            int inLast = contrib.N1;

            if (inLast < inFirst)
            {
                verticalBuffer.Slice(0, outRowFloats).Clear();
                EncodeScanlineSlice(plan, output, outputStrideBytes, verticalBuffer, y, 0, outSubW);
                continue;
            }

            while (ringLastScanline < inLast)
            {
                ringLastScanline++;
                if ((ringLastScanline - ringFirstScanline) >= ringEntries)
                {
                    ringFirstScanline++;
                    ringBeginIndex++;
                    if (ringBeginIndex == ringEntries)
                    {
                        ringBeginIndex = 0;
                    }
                }

                int slot = ResizeKernels.GetRingSlot(ringLastScanline, ringFirstScanline, ringBeginIndex, ringEntries);
                Span<float> ringRow = ringBuffer.Slice(slot * ringRowStride, ringRowStride);

                if (plan.VerticalFirst)
                {
                    DecodeScanlineSlice(plan, input, inputStrideBytes, ringRow, ringLastScanline, sliceInN0, sliceInSpan);
                }
                else
                {
                    DecodeScanlineSlice(plan, input, inputStrideBytes, decodeBuffer, ringLastScanline, sliceInN0, sliceInSpan);
                    ResizeKernels.ResampleHorizontalGatherScalar(horizontal, ringRow, decodeBuffer, 0, outSubW, sliceInN0, eff);
                }
            }

            ReadOnlySpan<float> rowCoeffs = vCoeffs.AsSpan(y * vCoeffWidth, inLast - inFirst + 1);

            if (plan.VerticalFirst)
            {
                ResampleVerticalGatherScalar(
                    decodeBuffer,
                    ringBuffer,
                    ringRowStride,
                    ringRowFloats,
                    ringFirstScanline,
                    ringBeginIndex,
                    ringEntries,
                    inFirst,
                    inLast,
                    rowCoeffs);
                ResizeKernels.ResampleHorizontalGatherScalar(horizontal, verticalBuffer, decodeBuffer, 0, outSubW, sliceInN0, eff);
            }
            else
            {
                ResampleVerticalGatherScalar(
                    verticalBuffer,
                    ringBuffer,
                    ringRowStride,
                    ringRowFloats,
                    ringFirstScanline,
                    ringBeginIndex,
                    ringEntries,
                    inFirst,
                    inLast,
                    rowCoeffs);
            }

            EncodeScanlineSlice(plan, output, outputStrideBytes, verticalBuffer, y, 0, outSubW);
        }
    }

    private static void ExecuteVerticalScatterScalar(
        ResizePlan plan,
        ReadOnlySpan<byte> input,
        Span<byte> output,
        int inputStrideBytes,
        int outputStrideBytes)
    {
        ResizeSampler horizontal = plan.Horizontal!;
        ResizeSampler vertical = plan.Vertical!;

        int eff = plan.EffectiveChannels;
        int outSubW = plan.OutputSubWidth;
        int outSubH = plan.OutputSubHeight;
        int sliceInN0 = plan.SliceInN0;
        int sliceInSpan = plan.SliceInSpan;
        int ringEntries = plan.RingEntries;

        int decodeFloats = sliceInSpan * eff;
        int outRowFloats = outSubW * eff;
        int ringRowFloats = plan.VerticalFirst ? decodeFloats : outRowFloats;
        int ringRowStride = ringRowFloats + ResizeKernels.RowPaddingFloats;

        using PooledBuffer<float> decodeLease = new(decodeFloats + ResizeKernels.RowPaddingFloats);
        using PooledBuffer<float> verticalLease = new(outRowFloats + ResizeKernels.RowPaddingFloats);
        using PooledBuffer<float> ringLease = new(ringRowStride * ringEntries);
        using PooledBuffer<byte> initLease = new(ringEntries, clear: true);

        Span<float> decodeBuffer = decodeLease.Span;
        Span<float> verticalBuffer = verticalLease.Span;
        Span<float> ringBuffer = ringLease.Span;
        Span<byte> ringInitialized = initLease.Span;

        int ringFirstScanline = 0;
        int ringBeginIndex = 0;

        ResizeContributor[] vContribs = vertical.Contributors;
        float[] vCoeffs = vertical.Coefficients;
        int vCoeffWidth = vertical.CoefficientWidth;
        int vMargin = vertical.FilterPixelMargin;

        int startInputY = vertical.ExtentsLowest;
        int endInputY = vertical.ExtentsHighest + 1;

        for (int inY = startInputY; inY < endInputY; inY++)
        {
            int idx = inY + vMargin;
            if ((uint)idx >= (uint)vContribs.Length)
            {
                continue;
            }

            ResizeContributor contrib = vContribs[idx];
            int outFirst = contrib.N0;
            int outLast = contrib.N1;

            if (outLast < outFirst || outLast < 0 || outFirst >= outSubH)
            {
                continue;
            }

            int coeffOffset = idx * vCoeffWidth;
            if (outFirst < 0)
            {
                coeffOffset += -outFirst;
                outFirst = 0;
            }

            if (outLast >= outSubH)
            {
                outLast = outSubH - 1;
            }

            while (outLast >= ringFirstScanline + ringEntries)
            {
                EvictScatterRowScalar(
                    plan,
                    horizontal,
                    output,
                    outputStrideBytes,
                    ringBuffer,
                    ringInitialized,
                    verticalBuffer,
                    ringRowStride,
                    ringRowFloats,
                    ringFirstScanline,
                    ringBeginIndex,
                    sliceInN0,
                    outSubW,
                    eff);

                ringFirstScanline++;
                ringBeginIndex++;
                if (ringBeginIndex == ringEntries)
                {
                    ringBeginIndex = 0;
                }
            }

            DecodeScanlineSlice(plan, input, inputStrideBytes, decodeBuffer, inY, sliceInN0, sliceInSpan);

            ReadOnlySpan<float> scatterSource;
            if (plan.VerticalFirst)
            {
                scatterSource = decodeBuffer;
            }
            else
            {
                ResizeKernels.ResampleHorizontalGatherScalar(horizontal, verticalBuffer, decodeBuffer, 0, outSubW, sliceInN0, eff);
                scatterSource = verticalBuffer;
            }

            for (int outY = outFirst; outY <= outLast; outY++)
            {
                float c = vCoeffs[coeffOffset + (outY - outFirst)];
                int slot = ResizeKernels.GetRingSlot(outY, ringFirstScanline, ringBeginIndex, ringEntries);
                Span<float> dstRow = ringBuffer.Slice(slot * ringRowStride, ringRowFloats);

                if (ringInitialized[slot] == 0)
                {
                    for (int i = 0; i < ringRowFloats; i++)
                    {
                        dstRow[i] = scatterSource[i] * c;
                    }

                    ringInitialized[slot] = 1;
                }
                else
                {
                    for (int i = 0; i < ringRowFloats; i++)
                    {
                        dstRow[i] += scatterSource[i] * c;
                    }
                }
            }
        }

        while (ringFirstScanline < outSubH)
        {
            EvictScatterRowScalar(
                plan,
                horizontal,
                output,
                outputStrideBytes,
                ringBuffer,
                ringInitialized,
                verticalBuffer,
                ringRowStride,
                ringRowFloats,
                ringFirstScanline,
                ringBeginIndex,
                sliceInN0,
                outSubW,
                eff);

            ringFirstScanline++;
            ringBeginIndex++;
            if (ringBeginIndex == ringEntries)
            {
                ringBeginIndex = 0;
            }
        }
    }

    private static void EvictScatterRowScalar(
        ResizePlan plan,
        ResizeSampler horizontal,
        Span<byte> output,
        int outputStrideBytes,
        Span<float> ringBuffer,
        Span<byte> ringInitialized,
        Span<float> verticalBuffer,
        int ringRowStride,
        int ringRowFloats,
        int outY,
        int slot,
        int sliceInN0,
        int outSubW,
        int eff)
    {
        Span<float> ringRow = ringBuffer.Slice(slot * ringRowStride, ringRowStride);
        if (ringInitialized[slot] == 0)
        {
            ringRow.Slice(0, ringRowFloats).Clear();
        }

        if (plan.VerticalFirst)
        {
            ResizeKernels.ResampleHorizontalGatherScalar(horizontal, verticalBuffer, ringRow, 0, outSubW, sliceInN0, eff);
            EncodeScanlineSlice(plan, output, outputStrideBytes, verticalBuffer, outY, 0, outSubW);
        }
        else
        {
            EncodeScanlineSlice(plan, output, outputStrideBytes, ringRow, outY, 0, outSubW);
        }

        ringInitialized[slot] = 0;
    }

    private static void DecodeScanlineSlice(
        ResizePlan plan,
        ReadOnlySpan<byte> input,
        int inputStrideBytes,
        Span<float> decodeBuffer,
        int y,
        int inN0,
        int inCount)
    {
        int inW = plan.InputWidth;
        int inH = plan.InputHeight;
        int channels = plan.Channels;
        int pixelBytes = channels * plan.InputTypeSize;
        int srcY = ResizeGeometry.EdgeWrap(plan.Vertical!.Edge, y, inH);
        int rowByteOffset = inputStrideBytes > 0
            ? srcY * inputStrideBytes
            : (inH - 1 - srcY) * (-inputStrideBytes);

        int inN1 = inN0 + inCount - 1;

        if (inN0 >= 0 && inN1 < inW)
        {
            ReadOnlySpan<byte> srcRow = input.Slice(rowByteOffset + (inN0 * pixelBytes), inCount * pixelBytes);
            if (plan.AlphaWeighting == AlphaWeightingKind.FancyBoth
                && !plan.Flip3ChOnDecode
                && ResizeCoders.TryDecodeAndFancyWeight4Ch(
                    decodeBuffer,
                    srcRow,
                    inCount,
                    plan.InputLayout,
                    plan.EffectiveInputDataType))
            {
                return;
            }

            ResizeCoders.DecodePixels(
                decodeBuffer,
                srcRow,
                inCount,
                channels,
                plan.InputLayout,
                plan.EffectiveInputDataType,
                plan.Unscaled);
        }
        else
        {
            // EDGE_WRAP path: decode contiguous runs wrapped modulo input width.
            int cur = inN0;
            int dstPixel = 0;
            ResizeEdge hEdge = plan.Horizontal!.Edge;
            while (cur <= inN1)
            {
                int wrapped = ResizeGeometry.EdgeWrap(hEdge, cur, inW);
                int run = Math.Min(inN1 - cur + 1, inW - wrapped);
                ResizeCoders.DecodePixels(
                    decodeBuffer.Slice(dstPixel * channels, run * channels),
                    input.Slice(rowByteOffset + (wrapped * pixelBytes), run * pixelBytes),
                    run,
                    channels,
                    plan.InputLayout,
                    plan.EffectiveInputDataType,
                    plan.Unscaled);
                cur += run;
                dstPixel += run;
            }
        }

        ResizeCoders.ApplyAlphaWeight(decodeBuffer, inCount, plan.AlphaWeighting, channels, plan.Flip3ChOnDecode);
    }

    private static void EncodeScanlineSlice(
        ResizePlan plan,
        Span<byte> output,
        int outputStrideBytes,
        Span<float> encodeBuffer,
        int outSubY,
        int sliceOutX,
        int sliceOutW)
    {
        int channels = plan.Channels;
        int pixelBytes = channels * plan.OutputTypeSize;

        int dstY = plan.OutputOffsetY + outSubY;
        int rowByteOffset = outputStrideBytes > 0
            ? dstY * outputStrideBytes
            : (plan.OutputHeight - 1 - dstY) * (-outputStrideBytes);
        int xByteOffset = (plan.OutputOffsetX + sliceOutX) * pixelBytes;
        Span<byte> dstSlice = output.Slice(rowByteOffset + xByteOffset, sliceOutW * pixelBytes);

        if (plan.AlphaWeighting == AlphaWeightingKind.FancyBoth
            && !plan.Flip3ChOnEncode
            && ResizeCoders.TryFancyUnweightAndEncode4Ch(
                dstSlice,
                encodeBuffer,
                sliceOutW,
                plan.OutputLayout,
                plan.EffectiveOutputDataType))
        {
            return;
        }

        ResizeCoders.ApplyAlphaUnweight(encodeBuffer, sliceOutW, plan.AlphaWeighting, channels, plan.Flip3ChOnEncode);

        ResizeCoders.EncodePixels(
            dstSlice,
            encodeBuffer,
            sliceOutW,
            channels,
            plan.OutputLayout,
            plan.EffectiveOutputDataType,
            plan.Unscaled);
    }

    private static void ResampleVerticalGatherScalar(
        Span<float> output,
        ReadOnlySpan<float> ringBuffer,
        int ringRowStride,
        int ringRowFloats,
        int ringFirstScanline,
        int ringBeginIndex,
        int ringEntries,
        int n0,
        int n1,
        ReadOnlySpan<float> coeffs)
    {
        int total = n1 - n0 + 1;
        int k = 0;

        bool firstGroup = true;
        while (total > 0)
        {
            int n = Math.Min(total, 8);
            if (firstGroup)
            {
                firstGroup = false;
                if (n == 1)
                {
                    ReadOnlySpan<float> r0 = ringBuffer.Slice(ResizeKernels.GetRingSlot(n0 + k, ringFirstScanline, ringBeginIndex, ringEntries) * ringRowStride, ringRowFloats);
                    float c0 = coeffs[k];
                    for (int i = 0; i < ringRowFloats; i++)
                    {
                        output[i] = r0[i] * c0;
                    }
                }
                else if (n == 2)
                {
                    ReadOnlySpan<float> r0 = ringBuffer.Slice(ResizeKernels.GetRingSlot(n0 + k, ringFirstScanline, ringBeginIndex, ringEntries) * ringRowStride, ringRowFloats);
                    ReadOnlySpan<float> r1 = ringBuffer.Slice(ResizeKernels.GetRingSlot(n0 + k + 1, ringFirstScanline, ringBeginIndex, ringEntries) * ringRowStride, ringRowFloats);
                    float c0 = coeffs[k];
                    float c1 = coeffs[k + 1];
                    for (int i = 0; i < ringRowFloats; i++)
                    {
                        output[i] = (r0[i] * c0) + (r1[i] * c1);
                    }
                }
                else if (n == 4)
                {
                    ReadOnlySpan<float> r0 = ringBuffer.Slice(ResizeKernels.GetRingSlot(n0 + k, ringFirstScanline, ringBeginIndex, ringEntries) * ringRowStride, ringRowFloats);
                    ReadOnlySpan<float> r1 = ringBuffer.Slice(ResizeKernels.GetRingSlot(n0 + k + 1, ringFirstScanline, ringBeginIndex, ringEntries) * ringRowStride, ringRowFloats);
                    ReadOnlySpan<float> r2 = ringBuffer.Slice(ResizeKernels.GetRingSlot(n0 + k + 2, ringFirstScanline, ringBeginIndex, ringEntries) * ringRowStride, ringRowFloats);
                    ReadOnlySpan<float> r3 = ringBuffer.Slice(ResizeKernels.GetRingSlot(n0 + k + 3, ringFirstScanline, ringBeginIndex, ringEntries) * ringRowStride, ringRowFloats);
                    float c0 = coeffs[k];
                    float c1 = coeffs[k + 1];
                    float c2 = coeffs[k + 2];
                    float c3 = coeffs[k + 3];
                    for (int i = 0; i < ringRowFloats; i++)
                    {
                        output[i] = (((r0[i] * c0) + (r1[i] * c1)) + (r2[i] * c2)) + (r3[i] * c3);
                    }
                }
                else
                {
                    ReadOnlySpan<float> r0 = ringBuffer.Slice(ResizeKernels.GetRingSlot(n0 + k, ringFirstScanline, ringBeginIndex, ringEntries) * ringRowStride, ringRowFloats);
                    float c0 = coeffs[k];
                    for (int i = 0; i < ringRowFloats; i++)
                    {
                        output[i] = r0[i] * c0;
                    }

                    for (int j = 1; j < n; j++)
                    {
                        ReadOnlySpan<float> rj = ringBuffer.Slice(ResizeKernels.GetRingSlot(n0 + k + j, ringFirstScanline, ringBeginIndex, ringEntries) * ringRowStride, ringRowFloats);
                        float cj = coeffs[k + j];
                        for (int i = 0; i < ringRowFloats; i++)
                        {
                            output[i] += rj[i] * cj;
                        }
                    }
                }
            }
            else
            {
                for (int j = 0; j < n; j++)
                {
                    ReadOnlySpan<float> rj = ringBuffer.Slice(ResizeKernels.GetRingSlot(n0 + k + j, ringFirstScanline, ringBeginIndex, ringEntries) * ringRowStride, ringRowFloats);
                    float cj = coeffs[k + j];
                    for (int i = 0; i < ringRowFloats; i++)
                    {
                        output[i] += rj[i] * cj;
                    }
                }
            }

            k += n;
            total -= n;
        }
    }
}
