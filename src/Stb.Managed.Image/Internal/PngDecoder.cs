using System.Buffers;
using System.Buffers.Binary;
using System.IO.Compression;
using System.Runtime.ExceptionServices;
using Stb.Managed.Internal;

namespace Stb.Managed.Image.Internal;

/// <summary>
/// Faithful, bounds-checked PNG decoder matching <c>stb_image.h</c> semantics with SIMD unfiltering,
/// streaming L2-cache-hot inflate, and opt-in two-thread inflate/unfilter pipelining.
/// </summary>
internal static class PngDecoder
{
    private const uint ChunkCgBI = 0x43674249u; // "CgBI"
    private const uint ChunkIHDR = 0x49484452u; // "IHDR"
    private const uint ChunkPLTE = 0x504C5445u; // "PLTE"
    private const uint ChunkTRNS = 0x74524E53u; // "tRNS"
    private const uint ChunkIDAT = 0x49444154u; // "IDAT"
    private const uint ChunkIEND = 0x49454E44u; // "IEND"

    // Streaming batch target (~64 KB fits comfortably in L2 cache while amortizing deflate calls).
    private const int StreamingChunkTargetBytes = 64 * 1024;

    // Auto-parallel threshold for two-thread inflate+unfilter pipelining (~256x256 RGBA raw).
    private const long ParallelPipelineThresholdBytes = 256 * 1024;

    private static ReadOnlySpan<byte> PngSignature => [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];

    private static ReadOnlySpan<byte> DepthScaleTable => [0, 0xFF, 0x55, 0, 0x11, 0, 0, 0, 0x01];

    private static readonly int[] Adam7XOrigin = [0, 4, 0, 2, 0, 1, 0];
    private static readonly int[] Adam7YOrigin = [0, 0, 4, 0, 2, 0, 1];
    private static readonly int[] Adam7XSpacing = [8, 8, 4, 4, 2, 2, 1];
    private static readonly int[] Adam7YSpacing = [8, 8, 8, 4, 4, 2, 2];

    internal static bool IsPng(ReadOnlySpan<byte> data) =>
        data.Length >= 8 && data.Slice(0, 8).SequenceEqual(PngSignature);

    internal static bool TryGetInfo(
        ReadOnlySpan<byte> data,
        ImageDecodeOptions options,
        out ImageInfo info)
    {
        if (!IsPng(data))
        {
            info = default;
            return false;
        }

        try
        {
            info = GetInfo(data, options);
            return true;
        }
        catch (StbException)
        {
            info = default;
            return false;
        }
    }

    internal static ImageInfo GetInfo(ReadOnlySpan<byte> data, ImageDecodeOptions options)
    {
        PngMetadata meta = ParseMetadata(data, options, headerOnly: true, out _, out _, out _, out _);
        return new ImageInfo(
            meta.Width,
            meta.Height,
            (ColorComponents)meta.SourceChannels,
            meta.BitDepth == 16 ? 16 : 8,
            IsHdr: false);
    }

    internal static ImageBuffer<byte> Decode8(
        ReadOnlySpan<byte> data,
        ColorComponents desiredComponents,
        ImageDecodeOptions options)
    {
        ValidateDesiredComponents(desiredComponents);
        bool ok = Decode8Core(
            data,
            desiredComponents,
            options,
            externalDestination: Span<byte>.Empty,
            useExternalDestination: false,
            out ImageBuffer<byte>? allocated,
            out _,
            out _);

        if (!ok || allocated is null)
        {
            throw new StbException(StbErrorCode.InternalError, "Failed to allocate decoded 8-bit PNG buffer.");
        }

        return allocated;
    }

    internal static bool TryDecode8(
        ReadOnlySpan<byte> data,
        Span<byte> destination,
        ColorComponents desiredComponents,
        ImageDecodeOptions options,
        out ImageInfo info,
        out ColorComponents outputComponents)
    {
        ValidateDesiredComponents(desiredComponents);
        return Decode8Core(
            data,
            desiredComponents,
            options,
            destination,
            useExternalDestination: true,
            out _,
            out info,
            out outputComponents);
    }

    internal static ImageBuffer<ushort> Decode16(
        ReadOnlySpan<byte> data,
        ColorComponents desiredComponents,
        ImageDecodeOptions options)
    {
        ValidateDesiredComponents(desiredComponents);
        bool ok = Decode16Core(
            data,
            desiredComponents,
            options,
            externalDestination: Span<ushort>.Empty,
            useExternalDestination: false,
            out ImageBuffer<ushort>? allocated,
            out _,
            out _);

        if (!ok || allocated is null)
        {
            throw new StbException(StbErrorCode.InternalError, "Failed to allocate decoded 16-bit PNG buffer.");
        }

        return allocated;
    }

    internal static bool TryDecode16(
        ReadOnlySpan<byte> data,
        Span<ushort> destination,
        ColorComponents desiredComponents,
        ImageDecodeOptions options,
        out ImageInfo info,
        out ColorComponents outputComponents)
    {
        ValidateDesiredComponents(desiredComponents);
        return Decode16Core(
            data,
            desiredComponents,
            options,
            destination,
            useExternalDestination: true,
            out _,
            out info,
            out outputComponents);
    }

    private static void ValidateDesiredComponents(ColorComponents desiredComponents)
    {
        int req = (int)desiredComponents;
        if (req < 0 || req > 4)
        {
            throw new StbException(
                StbErrorCode.InvalidArgument,
                $"Desired components must be between Default (0) and RedGreenBlueAlpha (4), got {desiredComponents}.");
        }
    }

    private static bool Decode8Core(
        ReadOnlySpan<byte> data,
        ColorComponents desiredComponents,
        ImageDecodeOptions options,
        Span<byte> externalDestination,
        bool useExternalDestination,
        out ImageBuffer<byte>? allocatedBuffer,
        out ImageInfo info,
        out ColorComponents outputComponents)
    {
        allocatedBuffer = null;
        StbLimits limits = options.Options.Limits;
        int reqComp = (int)desiredComponents;

        PngMetadata meta = ParseMetadata(
            data,
            options,
            headerOnly: false,
            out IdatChunks idatChunks,
            out byte[]? palette,
            out byte[]? trans8,
            out ushort[]? trans16);

        int finalChannels = reqComp == 0 ? meta.SourceChannels : reqComp;
        info = new ImageInfo(
            meta.Width,
            meta.Height,
            (ColorComponents)meta.SourceChannels,
            meta.BitDepth == 16 ? 16 : 8,
            IsHdr: false);
        outputComponents = (ColorComponents)finalChannels;

        int finalSampleCount = limits.ValidateDimensions(meta.Width, meta.Height, finalChannels);
        if (useExternalDestination && externalDestination.Length < finalSampleCount)
        {
            return false;
        }

        if (meta.BitDepth == 16)
        {
            // Match stb_image.h ordering: decode + channel-convert in 16-bit, THEN reduce 16->8, THEN flip.
            int samples16Bytes = limits.ValidateDimensions(meta.Width, meta.Height, finalChannels * 2);
            int samples16Count = samples16Bytes / 2;
            using PooledBuffer<ushort> temp16 = new(samples16Count);
            DecodeToNative16(data, in meta, in idatChunks, trans16, reqComp, options, temp16.Span);

            Span<byte> dst;
            if (useExternalDestination)
            {
                dst = externalDestination.Slice(0, finalSampleCount);
            }
            else
            {
                allocatedBuffer = ImageBuffer.Allocate<byte>(
                    meta.Width,
                    meta.Height,
                    outputComponents,
                    info.Components,
                    limits);
                dst = allocatedBuffer.AsSpan();
            }

            PixelConverter.Convert16To8(temp16.Span, dst);
            if (options.FlipVertically)
            {
                PixelConverter.VerticalFlip(dst, meta.Height, meta.Width * finalChannels);
            }

            return true;
        }

        Span<byte> dest8;
        if (useExternalDestination)
        {
            dest8 = externalDestination.Slice(0, finalSampleCount);
        }
        else
        {
            allocatedBuffer = ImageBuffer.Allocate<byte>(
                meta.Width,
                meta.Height,
                outputComponents,
                info.Components,
                limits);
            dest8 = allocatedBuffer.AsSpan();
        }

        DecodeToNative8(data, in meta, in idatChunks, palette, trans8, reqComp, options, dest8);
        if (options.FlipVertically)
        {
            PixelConverter.VerticalFlip(dest8, meta.Height, meta.Width * finalChannels);
        }

        return true;
    }

    private static bool Decode16Core(
        ReadOnlySpan<byte> data,
        ColorComponents desiredComponents,
        ImageDecodeOptions options,
        Span<ushort> externalDestination,
        bool useExternalDestination,
        out ImageBuffer<ushort>? allocatedBuffer,
        out ImageInfo info,
        out ColorComponents outputComponents)
    {
        allocatedBuffer = null;
        StbLimits limits = options.Options.Limits;
        int reqComp = (int)desiredComponents;

        PngMetadata meta = ParseMetadata(
            data,
            options,
            headerOnly: false,
            out IdatChunks idatChunks,
            out byte[]? palette,
            out byte[]? trans8,
            out ushort[]? trans16);

        int finalChannels = reqComp == 0 ? meta.SourceChannels : reqComp;
        info = new ImageInfo(
            meta.Width,
            meta.Height,
            (ColorComponents)meta.SourceChannels,
            meta.BitDepth == 16 ? 16 : 8,
            IsHdr: false);
        outputComponents = (ColorComponents)finalChannels;

        int finalByteCount = limits.ValidateDimensions(meta.Width, meta.Height, finalChannels * 2);
        int finalSampleCount = finalByteCount / 2;
        if (useExternalDestination && externalDestination.Length < finalSampleCount)
        {
            return false;
        }

        Span<ushort> dest16;
        if (useExternalDestination)
        {
            dest16 = externalDestination.Slice(0, finalSampleCount);
        }
        else
        {
            allocatedBuffer = ImageBuffer.Allocate<ushort>(
                meta.Width,
                meta.Height,
                outputComponents,
                info.Components,
                limits);
            dest16 = allocatedBuffer.AsSpan();
        }

        if (meta.BitDepth == 16)
        {
            DecodeToNative16(data, in meta, in idatChunks, trans16, reqComp, options, dest16);
        }
        else
        {
            // Match stb_image.h ordering: decode + channel-convert in 8-bit, THEN expand 8->16, THEN flip.
            using PooledBuffer<byte> temp8 = new(finalSampleCount);
            DecodeToNative8(data, in meta, in idatChunks, palette, trans8, reqComp, options, temp8.Span);
            PixelConverter.Convert8To16(temp8.Span, dest16);
        }

        if (options.FlipVertically)
        {
            PixelConverter.VerticalFlip(dest16, meta.Height, meta.Width * finalChannels);
        }

        return true;
    }

    private static void DecodeToNative8(
        ReadOnlySpan<byte> data,
        in PngMetadata meta,
        in IdatChunks idatChunks,
        byte[]? palette,
        byte[]? trans8,
        int reqComp,
        ImageDecodeOptions options,
        Span<byte> finalDest)
    {
        StbLimits limits = options.Options.Limits;
        int expectedRawLen = ComputeExpectedInflatedLength(in meta, limits);

        int rawOutN = ((!meta.IsPaletted && reqComp == meta.RawChannels + 1 && reqComp != 3) || meta.HasTransparency)
            ? meta.RawChannels + 1
            : meta.RawChannels;

        int finalChannels = reqComp == 0 ? meta.SourceChannels : reqComp;
        bool directToFinal = !meta.IsPaletted && rawOutN == finalChannels;

        if (directToFinal)
        {
            InflateAndDecode8(data, in meta, in idatChunks, expectedRawLen, rawOutN, options, finalDest);
            if (meta.HasTransparency && trans8 is not null)
            {
                ComputeTransparency8(finalDest, trans8, rawOutN, meta.Width * meta.Height);
            }

            if (meta.IsIphone && options.ConvertIphonePngToRgb && rawOutN > 2)
            {
                DeIphone8(finalDest, rawOutN, meta.Width * meta.Height, options.UnpremultiplyAlpha);
            }

            return;
        }

        int rawPixelsLen = limits.ValidateDimensions(meta.Width, meta.Height, rawOutN);
        using PooledBuffer<byte> rawOut = new(rawPixelsLen);
        InflateAndDecode8(data, in meta, in idatChunks, expectedRawLen, rawOutN, options, rawOut.Span);

        if (meta.HasTransparency && trans8 is not null)
        {
            ComputeTransparency8(rawOut.Span, trans8, rawOutN, meta.Width * meta.Height);
        }

        if (meta.IsIphone && options.ConvertIphonePngToRgb && rawOutN > 2)
        {
            DeIphone8(rawOut.Span, rawOutN, meta.Width * meta.Height, options.UnpremultiplyAlpha);
        }

        int pixelCount = meta.Width * meta.Height;
        if (meta.IsPaletted)
        {
            byte[] pal = palette ?? throw new StbException(StbErrorCode.InvalidData, "Missing PLTE chunk for paletted PNG.");
            int palOutN = reqComp >= 3 ? reqComp : meta.PaletteChannels;
            if (palOutN == finalChannels)
            {
                ExpandPalette(rawOut.Span, pal, palOutN, pixelCount, finalDest);
            }
            else
            {
                int palBufLen = limits.ValidateDimensions(meta.Width, meta.Height, palOutN);
                using PooledBuffer<byte> palBuf = new(palBufLen);
                ExpandPalette(rawOut.Span, pal, palOutN, pixelCount, palBuf.Span);
                PixelConverter.ConvertFormat8(palBuf.Span, finalDest, palOutN, finalChannels, pixelCount);
            }
        }
        else
        {
            PixelConverter.ConvertFormat8(rawOut.Span, finalDest, rawOutN, finalChannels, pixelCount);
        }
    }

    private static void DecodeToNative16(
        ReadOnlySpan<byte> data,
        in PngMetadata meta,
        in IdatChunks idatChunks,
        ushort[]? trans16,
        int reqComp,
        ImageDecodeOptions options,
        Span<ushort> finalDest)
    {
        StbLimits limits = options.Options.Limits;
        int expectedRawLen = ComputeExpectedInflatedLength(in meta, limits);

        int rawOutN = ((reqComp == meta.RawChannels + 1 && reqComp != 3) || meta.HasTransparency)
            ? meta.RawChannels + 1
            : meta.RawChannels;

        int finalChannels = reqComp == 0 ? meta.SourceChannels : reqComp;
        int pixelCount = meta.Width * meta.Height;

        if (rawOutN == finalChannels)
        {
            InflateAndDecode16(data, in meta, in idatChunks, expectedRawLen, rawOutN, options, finalDest);
            if (meta.HasTransparency && trans16 is not null)
            {
                ComputeTransparency16(finalDest, trans16, rawOutN, pixelCount);
            }

            return;
        }

        int rawBytes = limits.ValidateDimensions(meta.Width, meta.Height, rawOutN * 2);
        using PooledBuffer<ushort> rawOut = new(rawBytes / 2);
        InflateAndDecode16(data, in meta, in idatChunks, expectedRawLen, rawOutN, options, rawOut.Span);
        if (meta.HasTransparency && trans16 is not null)
        {
            ComputeTransparency16(rawOut.Span, trans16, rawOutN, pixelCount);
        }

        PixelConverter.ConvertFormat16(rawOut.Span, finalDest, rawOutN, finalChannels, pixelCount);
    }

    private static void InflateAndDecode8(
        ReadOnlySpan<byte> data,
        in PngMetadata meta,
        in IdatChunks idatChunks,
        int expectedRawLen,
        int outN,
        ImageDecodeOptions options,
        Span<byte> dest)
    {
        if (meta.Interlaced)
        {
            using PooledBuffer<byte> inflated = new(expectedRawLen);
            InflateIdatFull(data, in idatChunks, meta.TotalIdatBytes, meta.IsIphone, options.VerifyChecksums, options.Options.Limits, inflated.Span);
            CreatePngImageInterlaced8(inflated.Span, in meta, outN, dest);
            return;
        }

        int rowBytes = ((meta.RawChannels * meta.Width * meta.BitDepth) + 7) >> 3;
        int rawRowStride = rowBytes + 1;
        int rowsPerChunk = Math.Max(1, Math.Min(meta.Height, StreamingChunkTargetBytes / rawRowStride));
        int dop = options.Options.ResolveDegreeOfParallelism(expectedRawLen, ParallelPipelineThresholdBytes);

        if (dop >= 2 && meta.Height > rowsPerChunk)
        {
            InflateAndDecode8Pipelined(data, in meta, in idatChunks, rowsPerChunk, rowBytes, outN, options, dest);
        }
        else
        {
            InflateAndDecode8Streaming(data, in meta, in idatChunks, rowsPerChunk, rowBytes, outN, options, dest);
        }
    }

    private static void InflateAndDecode16(
        ReadOnlySpan<byte> data,
        in PngMetadata meta,
        in IdatChunks idatChunks,
        int expectedRawLen,
        int outN,
        ImageDecodeOptions options,
        Span<ushort> dest)
    {
        if (meta.Interlaced)
        {
            using PooledBuffer<byte> inflated = new(expectedRawLen);
            InflateIdatFull(data, in idatChunks, meta.TotalIdatBytes, meta.IsIphone, options.VerifyChecksums, options.Options.Limits, inflated.Span);
            CreatePngImageInterlaced16(inflated.Span, in meta, outN, dest);
            return;
        }

        int rowBytes = meta.RawChannels * meta.Width * 2;
        int rawRowStride = rowBytes + 1;
        int rowsPerChunk = Math.Max(1, Math.Min(meta.Height, StreamingChunkTargetBytes / rawRowStride));
        int dop = options.Options.ResolveDegreeOfParallelism(expectedRawLen, ParallelPipelineThresholdBytes);

        if (dop >= 2 && meta.Height > rowsPerChunk)
        {
            InflateAndDecode16Pipelined(data, in meta, in idatChunks, rowsPerChunk, rowBytes, outN, options, dest);
        }
        else
        {
            InflateAndDecode16Streaming(data, in meta, in idatChunks, rowsPerChunk, rowBytes, outN, options, dest);
        }
    }

    private static void InflateAndDecode8Streaming(
        ReadOnlySpan<byte> data,
        in PngMetadata meta,
        in IdatChunks idatChunks,
        int rowsPerChunk,
        int rowBytes,
        int outN,
        ImageDecodeOptions options,
        Span<byte> dest)
    {
        byte[] streamArray = RentAndPrepareCompressedStream(
            data,
            in idatChunks,
            meta.TotalIdatBytes,
            meta.IsIphone,
            out int deflateOffset,
            out int deflateLength);

        int rawRowStride = rowBytes + 1;
        int chunkBufBytes = rowsPerChunk * rawRowStride;
        bool directToDest = meta.BitDepth == 8 && meta.RawChannels == outN;

        try
        {
            using PooledBuffer<byte> chunkBuf = new(chunkBufBytes);
            using PooledBuffer<byte> filterBuf = directToDest ? default : new PooledBuffer<byte>(rowBytes * 2);
            using Stream decompressor = CreateDecompressor(
                streamArray,
                meta.TotalIdatBytes,
                deflateOffset,
                deflateLength,
                meta.IsIphone,
                options.VerifyChecksums,
                out bool computeManualAdler);

            uint adler = 1u;
            long totalDecompressed = 0;

            try
            {
                int row = 0;
                while (row < meta.Height)
                {
                    int batchRows = Math.Min(rowsPerChunk, meta.Height - row);
                    int batchBytes = batchRows * rawRowStride;
                    Span<byte> batchSpan = chunkBuf.Span.Slice(0, batchBytes);

                    ReadExactChunk(decompressor, batchSpan, totalDecompressed);
                    totalDecompressed += batchBytes;
                    if (computeManualAdler)
                    {
                        adler = Checksums.UpdateAdler32(adler, batchSpan);
                    }

                    DecodeScanlineBatch8(batchSpan, row, batchRows, in meta, rowBytes, outN, directToDest, filterBuf.Span, dest);
                    row += batchRows;
                }

                DrainAndVerifyTrailer(
                    decompressor,
                    streamArray.AsSpan(0, meta.TotalIdatBytes),
                    totalDecompressed,
                    options.Options.Limits,
                    computeManualAdler,
                    adler);
            }
            catch (InvalidDataException ex)
            {
                throw new StbException(StbErrorCode.CorruptStream, "Corrupt PNG deflate/zlib stream.", innerException: ex);
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(streamArray);
        }
    }

    private static void InflateAndDecode8Pipelined(
        ReadOnlySpan<byte> data,
        in PngMetadata meta,
        in IdatChunks idatChunks,
        int rowsPerChunk,
        int rowBytes,
        int outN,
        ImageDecodeOptions options,
        Span<byte> dest)
    {
        byte[] streamArray = RentAndPrepareCompressedStream(
            data,
            in idatChunks,
            meta.TotalIdatBytes,
            meta.IsIphone,
            out int deflateOffset,
            out int deflateLength);

        int rawRowStride = rowBytes + 1;
        int chunkBufBytes = rowsPerChunk * rawRowStride;
        bool directToDest = meta.BitDepth == 8 && meta.RawChannels == outN;
        int totalCompressedBytes = meta.TotalIdatBytes;
        int totalRows = meta.Height;
        bool isIphone = meta.IsIphone;
        bool verifyChecksums = options.VerifyChecksums;
        StbLimits limits = options.Options.Limits;

        using InflateRingBuffer ring = new(chunkBufBytes);
        Task producer = Task.Run(() => RunInflateProducer(
            ring,
            streamArray,
            totalCompressedBytes,
            deflateOffset,
            deflateLength,
            isIphone,
            verifyChecksums,
            limits,
            totalRows,
            rowsPerChunk,
            rawRowStride));

        Exception? consumerError = null;
        try
        {
            using PooledBuffer<byte> filterBuf = directToDest ? default : new PooledBuffer<byte>(rowBytes * 2);
            int row = 0;
            while (ring.TryTake(out byte[]? slot, out int batchBytes))
            {
                try
                {
                    int batchRows = batchBytes / rawRowStride;
                    DecodeScanlineBatch8(
                        slot!.AsSpan(0, batchBytes),
                        row,
                        batchRows,
                        in meta,
                        rowBytes,
                        outN,
                        directToDest,
                        filterBuf.Span,
                        dest);
                    row += batchRows;
                }
                finally
                {
                    ring.ReleaseSlot();
                }
            }
        }
        catch (Exception ex)
        {
            consumerError = ex;
            ring.CancelFromConsumer();
        }
        finally
        {
            // Always wait for producer to finish BEFORE returning pooled arrays.
            producer.GetAwaiter().GetResult();
            ArrayPool<byte>.Shared.Return(streamArray);
        }

        if (consumerError is not null)
        {
            ExceptionDispatchInfo.Capture(consumerError).Throw();
        }

        ring.ThrowIfProducerFailed();
    }

    private static void InflateAndDecode16Streaming(
        ReadOnlySpan<byte> data,
        in PngMetadata meta,
        in IdatChunks idatChunks,
        int rowsPerChunk,
        int rowBytes,
        int outN,
        ImageDecodeOptions options,
        Span<ushort> dest)
    {
        byte[] streamArray = RentAndPrepareCompressedStream(
            data,
            in idatChunks,
            meta.TotalIdatBytes,
            meta.IsIphone,
            out int deflateOffset,
            out int deflateLength);

        int rawRowStride = rowBytes + 1;
        int chunkBufBytes = rowsPerChunk * rawRowStride;

        try
        {
            using PooledBuffer<byte> chunkBuf = new(chunkBufBytes);
            using PooledBuffer<byte> filterBuf = new(rowBytes * 2);
            using Stream decompressor = CreateDecompressor(
                streamArray,
                meta.TotalIdatBytes,
                deflateOffset,
                deflateLength,
                meta.IsIphone,
                options.VerifyChecksums,
                out bool computeManualAdler);

            uint adler = 1u;
            long totalDecompressed = 0;

            try
            {
                int row = 0;
                while (row < meta.Height)
                {
                    int batchRows = Math.Min(rowsPerChunk, meta.Height - row);
                    int batchBytes = batchRows * rawRowStride;
                    Span<byte> batchSpan = chunkBuf.Span.Slice(0, batchBytes);

                    ReadExactChunk(decompressor, batchSpan, totalDecompressed);
                    totalDecompressed += batchBytes;
                    if (computeManualAdler)
                    {
                        adler = Checksums.UpdateAdler32(adler, batchSpan);
                    }

                    DecodeScanlineBatch16(batchSpan, row, batchRows, in meta, rowBytes, outN, filterBuf.Span, dest);
                    row += batchRows;
                }

                DrainAndVerifyTrailer(
                    decompressor,
                    streamArray.AsSpan(0, meta.TotalIdatBytes),
                    totalDecompressed,
                    options.Options.Limits,
                    computeManualAdler,
                    adler);
            }
            catch (InvalidDataException ex)
            {
                throw new StbException(StbErrorCode.CorruptStream, "Corrupt PNG deflate/zlib stream.", innerException: ex);
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(streamArray);
        }
    }

    private static void InflateAndDecode16Pipelined(
        ReadOnlySpan<byte> data,
        in PngMetadata meta,
        in IdatChunks idatChunks,
        int rowsPerChunk,
        int rowBytes,
        int outN,
        ImageDecodeOptions options,
        Span<ushort> dest)
    {
        byte[] streamArray = RentAndPrepareCompressedStream(
            data,
            in idatChunks,
            meta.TotalIdatBytes,
            meta.IsIphone,
            out int deflateOffset,
            out int deflateLength);

        int rawRowStride = rowBytes + 1;
        int chunkBufBytes = rowsPerChunk * rawRowStride;
        int totalCompressedBytes = meta.TotalIdatBytes;
        int totalRows = meta.Height;
        bool isIphone = meta.IsIphone;
        bool verifyChecksums = options.VerifyChecksums;
        StbLimits limits = options.Options.Limits;

        using InflateRingBuffer ring = new(chunkBufBytes);
        Task producer = Task.Run(() => RunInflateProducer(
            ring,
            streamArray,
            totalCompressedBytes,
            deflateOffset,
            deflateLength,
            isIphone,
            verifyChecksums,
            limits,
            totalRows,
            rowsPerChunk,
            rawRowStride));

        Exception? consumerError = null;
        try
        {
            using PooledBuffer<byte> filterBuf = new(rowBytes * 2);
            int row = 0;
            while (ring.TryTake(out byte[]? slot, out int batchBytes))
            {
                try
                {
                    int batchRows = batchBytes / rawRowStride;
                    DecodeScanlineBatch16(
                        slot!.AsSpan(0, batchBytes),
                        row,
                        batchRows,
                        in meta,
                        rowBytes,
                        outN,
                        filterBuf.Span,
                        dest);
                    row += batchRows;
                }
                finally
                {
                    ring.ReleaseSlot();
                }
            }
        }
        catch (Exception ex)
        {
            consumerError = ex;
            ring.CancelFromConsumer();
        }
        finally
        {
            producer.GetAwaiter().GetResult();
            ArrayPool<byte>.Shared.Return(streamArray);
        }

        if (consumerError is not null)
        {
            ExceptionDispatchInfo.Capture(consumerError).Throw();
        }

        ring.ThrowIfProducerFailed();
    }

    private static void RunInflateProducer(
        InflateRingBuffer ring,
        byte[] streamArray,
        int totalCompressedBytes,
        int deflateOffset,
        int deflateLength,
        bool isIphone,
        bool verifyChecksums,
        StbLimits limits,
        int totalRows,
        int rowsPerChunk,
        int rawRowStride)
    {
        try
        {
            using Stream decompressor = CreateDecompressor(
                streamArray,
                totalCompressedBytes,
                deflateOffset,
                deflateLength,
                isIphone,
                verifyChecksums,
                out bool computeManualAdler);

            uint adler = 1u;
            long totalDecompressed = 0;
            int row = 0;

            try
            {
                while (row < totalRows)
                {
                    int batchRows = Math.Min(rowsPerChunk, totalRows - row);
                    int batchBytes = batchRows * rawRowStride;
                    if (!ring.TryAcquireWriteSlot(out byte[]? slot))
                    {
                        return;
                    }

                    Span<byte> target = slot!.AsSpan(0, batchBytes);
                    ReadExactChunk(decompressor, target, totalDecompressed);
                    totalDecompressed += batchBytes;
                    if (computeManualAdler)
                    {
                        adler = Checksums.UpdateAdler32(adler, target);
                    }

                    ring.PublishWriteSlot(batchBytes);
                    row += batchRows;
                }

                DrainAndVerifyTrailer(
                    decompressor,
                    streamArray.AsSpan(0, totalCompressedBytes),
                    totalDecompressed,
                    limits,
                    computeManualAdler,
                    adler);
            }
            catch (InvalidDataException ex)
            {
                throw new StbException(StbErrorCode.CorruptStream, "Corrupt PNG deflate/zlib stream.", innerException: ex);
            }
        }
        catch (Exception ex)
        {
            ring.CompleteWithError(ex);
            return;
        }

        ring.Complete();
    }

    private static void DecodeScanlineBatch8(
        ReadOnlySpan<byte> rawBatch,
        int startRow,
        int batchRows,
        in PngMetadata meta,
        int rowBytes,
        int outN,
        bool directToDest,
        Span<byte> filterBuf,
        Span<byte> dest)
    {
        int imgN = meta.RawChannels;
        int depth = meta.BitDepth;
        int filterBytes = depth < 8 ? 1 : imgN;
        int outStride = meta.Width * outN;

        if (directToDest)
        {
            int rawPos = 0;
            for (int r = 0; r < batchRows; r++)
            {
                int j = startRow + r;
                byte filterByte = rawBatch[rawPos++];
                if (filterByte > 4)
                {
                    throw new StbException(StbErrorCode.InvalidData, $"Invalid PNG scanline filter {filterByte} on row {j}.");
                }

                ReadOnlySpan<byte> rawRow = rawBatch.Slice(rawPos, rowBytes);
                rawPos += rowBytes;

                Span<byte> destRow = dest.Slice(j * outStride, outStride);
                if (j == 0)
                {
                    PngUnfilter.UnfilterScanline(
                        PngUnfilter.MapFirstRowFilter(filterByte),
                        rawRow,
                        ReadOnlySpan<byte>.Empty,
                        destRow,
                        filterBytes);
                }
                else
                {
                    ReadOnlySpan<byte> priorRow = dest.Slice((j - 1) * outStride, outStride);
                    PngUnfilter.UnfilterScanline((PngFilterType)filterByte, rawRow, priorRow, destRow, filterBytes);
                }
            }

            return;
        }

        Span<byte> rowBuf0 = filterBuf.Slice(0, rowBytes);
        Span<byte> rowBuf1 = filterBuf.Slice(rowBytes, rowBytes);
        byte scale = meta.ColorType == 0 ? DepthScaleTable[depth] : (byte)1;
        int sampleCount = meta.Width * imgN;

        int pos = 0;
        for (int r = 0; r < batchRows; r++)
        {
            int j = startRow + r;
            byte filterByte = rawBatch[pos++];
            if (filterByte > 4)
            {
                throw new StbException(StbErrorCode.InvalidData, $"Invalid PNG scanline filter {filterByte} on row {j}.");
            }

            PngFilterType filter = j == 0
                ? PngUnfilter.MapFirstRowFilter(filterByte)
                : (PngFilterType)filterByte;

            Span<byte> cur = (j & 1) == 0 ? rowBuf0 : rowBuf1;
            ReadOnlySpan<byte> prior = (j & 1) == 0 ? rowBuf1 : rowBuf0;
            ReadOnlySpan<byte> rawRow = rawBatch.Slice(pos, rowBytes);
            pos += rowBytes;

            PngUnfilter.UnfilterScanline(filter, rawRow, prior, cur, filterBytes);

            Span<byte> destRow = dest.Slice(j * outStride, outStride);
            if (depth == 8)
            {
                ExpandAlpha8(cur, destRow, meta.Width, imgN);
            }
            else
            {
                UnpackSubByteRow(cur, destRow, sampleCount, depth, scale);
                if (imgN != outN)
                {
                    ExpandAlpha8InPlace(destRow, meta.Width, imgN);
                }
            }
        }
    }

    private static void DecodeScanlineBatch16(
        ReadOnlySpan<byte> rawBatch,
        int startRow,
        int batchRows,
        in PngMetadata meta,
        int rowBytes,
        int outN,
        Span<byte> filterBuf,
        Span<ushort> dest)
    {
        int imgN = meta.RawChannels;
        int width = meta.Width;
        int filterBytes = imgN * 2;
        int outStride = width * outN;

        Span<byte> rowBuf0 = filterBuf.Slice(0, rowBytes);
        Span<byte> rowBuf1 = filterBuf.Slice(rowBytes, rowBytes);

        int pos = 0;
        for (int r = 0; r < batchRows; r++)
        {
            int j = startRow + r;
            byte filterByte = rawBatch[pos++];
            if (filterByte > 4)
            {
                throw new StbException(StbErrorCode.InvalidData, $"Invalid PNG scanline filter {filterByte} on row {j}.");
            }

            PngFilterType filter = j == 0
                ? PngUnfilter.MapFirstRowFilter(filterByte)
                : (PngFilterType)filterByte;

            Span<byte> cur = (j & 1) == 0 ? rowBuf0 : rowBuf1;
            ReadOnlySpan<byte> prior = (j & 1) == 0 ? rowBuf1 : rowBuf0;
            ReadOnlySpan<byte> rawRow = rawBatch.Slice(pos, rowBytes);
            pos += rowBytes;

            PngUnfilter.UnfilterScanline(filter, rawRow, prior, cur, filterBytes);

            Span<ushort> destRow = dest.Slice(j * outStride, outStride);
            WriteRow16(cur, destRow, width, imgN, outN);
        }
    }

    private static PngMetadata ParseMetadata(
        ReadOnlySpan<byte> data,
        ImageDecodeOptions options,
        bool headerOnly,
        out IdatChunks idatChunks,
        out byte[]? palette,
        out byte[]? trans8,
        out ushort[]? trans16)
    {
        StbLimits limits = options.Options.Limits;
        idatChunks = default;
        palette = null;
        trans8 = null;
        trans16 = null;

        if (!IsPng(data))
        {
            throw new StbException(StbErrorCode.UnsupportedFormat, "Input does not start with a valid PNG signature.", 0);
        }

        SpanReader reader = new(data);
        reader.Skip(8);

        bool first = true;
        bool isIphone = false;
        int width = 0;
        int height = 0;
        int depth = 0;
        int color = 0;
        int interlace = 0;
        int rawChannels = 0;
        int palChannels = 0;
        int palLen = 0;
        bool hasTrans = false;
        bool seenIdat = false;
        long totalIdatBytes = 0;

        while (!reader.IsEnd)
        {
            int chunkHeaderOffset = reader.Position;
            uint lengthU = reader.ReadUInt32BigEndian();
            int typeOffset = reader.Position;
            uint chunkType = reader.ReadUInt32BigEndian();

            if (lengthU > int.MaxValue || (long)lengthU + 4 > reader.Remaining)
            {
                throw new StbException(
                    StbErrorCode.TruncatedInput,
                    $"PNG chunk at offset {chunkHeaderOffset} declares length {lengthU}, exceeding remaining input ({reader.Remaining} bytes).",
                    chunkHeaderOffset);
            }

            int length = (int)lengthU;
            int payloadOffset = reader.Position;
            ReadOnlySpan<byte> payload = reader.ReadBytes(length);
            uint storedCrc = reader.ReadUInt32BigEndian();

            if (options.VerifyChecksums)
            {
                uint computedCrc = Checksums.ComputeCrc32(data.Slice(typeOffset, 4), payload);
                if (computedCrc != storedCrc)
                {
                    throw new StbException(
                        StbErrorCode.InvalidData,
                        $"PNG chunk CRC-32 mismatch at offset {chunkHeaderOffset}: expected 0x{storedCrc:X8}, computed 0x{computedCrc:X8}.",
                        chunkHeaderOffset);
                }
            }

            switch (chunkType)
            {
                case ChunkCgBI:
                    isIphone = true;
                    break;

                case ChunkIHDR:
                {
                    if (!first)
                    {
                        throw new StbException(StbErrorCode.InvalidData, "Multiple IHDR chunks in PNG.", chunkHeaderOffset);
                    }

                    first = false;
                    if (length != 13)
                    {
                        throw new StbException(StbErrorCode.InvalidData, $"Invalid IHDR length {length}; expected 13.", chunkHeaderOffset);
                    }

                    uint wU = BinaryPrimitives.ReadUInt32BigEndian(payload.Slice(0, 4));
                    uint hU = BinaryPrimitives.ReadUInt32BigEndian(payload.Slice(4, 4));
                    if (wU == 0 || hU == 0)
                    {
                        throw new StbException(StbErrorCode.InvalidData, $"PNG has zero dimension ({wU}x{hU}).", payloadOffset);
                    }

                    if (wU > int.MaxValue || hU > int.MaxValue)
                    {
                        throw new StbException(StbErrorCode.LimitExceeded, $"PNG dimensions {wU}x{hU} exceed 32-bit signed range.", payloadOffset);
                    }

                    width = (int)wU;
                    height = (int)hU;
                    depth = payload[8];
                    color = payload[9];
                    byte compression = payload[10];
                    byte filter = payload[11];
                    interlace = payload[12];

                    if (depth != 1 && depth != 2 && depth != 4 && depth != 8 && depth != 16)
                    {
                        throw new StbException(StbErrorCode.InvalidData, $"Unsupported PNG bit depth {depth}.", payloadOffset + 8);
                    }

                    if (color > 6 || (color == 3 && depth == 16))
                    {
                        throw new StbException(StbErrorCode.InvalidData, $"Invalid PNG color type {color} for bit depth {depth}.", payloadOffset + 9);
                    }

                    if (color == 3)
                    {
                        palChannels = 3;
                    }
                    else if ((color & 1) != 0)
                    {
                        throw new StbException(StbErrorCode.InvalidData, $"Invalid PNG color type {color}.", payloadOffset + 9);
                    }

                    if (depth < 8 && color != 0 && color != 3)
                    {
                        throw new StbException(StbErrorCode.InvalidData, $"Sub-byte PNG bit depth {depth} is invalid for color type {color}.", payloadOffset + 9);
                    }

                    if (compression != 0)
                    {
                        throw new StbException(StbErrorCode.InvalidData, $"Unsupported PNG compression method {compression}.", payloadOffset + 10);
                    }

                    if (filter != 0)
                    {
                        throw new StbException(StbErrorCode.InvalidData, $"Unsupported PNG filter method {filter}.", payloadOffset + 11);
                    }

                    if (interlace > 1)
                    {
                        throw new StbException(StbErrorCode.InvalidData, $"Unsupported PNG interlace method {interlace}.", payloadOffset + 12);
                    }

                    if (palChannels == 0)
                    {
                        rawChannels = ((color & 2) != 0 ? 3 : 1) + ((color & 4) != 0 ? 1 : 0);
                    }
                    else
                    {
                        rawChannels = 1;
                        if (!headerOnly)
                        {
                            palette = new byte[256 * 4];
                        }
                    }

                    int minBytesPerPixel = palChannels != 0 ? 4 : rawChannels * (depth == 16 ? 2 : 1);
                    limits.ValidateDimensions(width, height, minBytesPerPixel);
                    break;
                }

                case ChunkPLTE:
                {
                    if (first)
                    {
                        throw new StbException(StbErrorCode.InvalidData, "PLTE chunk appeared before IHDR.", chunkHeaderOffset);
                    }

                    if (length > 256 * 3 || (length % 3) != 0)
                    {
                        throw new StbException(StbErrorCode.InvalidData, $"Invalid PLTE chunk length {length}.", chunkHeaderOffset);
                    }

                    palLen = length / 3;
                    if (!headerOnly)
                    {
                        palette ??= new byte[256 * 4];
                        for (int i = 0; i < palLen; i++)
                        {
                            int srcIdx = i * 3;
                            int dstIdx = i * 4;
                            palette[dstIdx] = payload[srcIdx];
                            palette[dstIdx + 1] = payload[srcIdx + 1];
                            palette[dstIdx + 2] = payload[srcIdx + 2];
                            palette[dstIdx + 3] = 255;
                        }
                    }

                    break;
                }

                case ChunkTRNS:
                {
                    if (first)
                    {
                        throw new StbException(StbErrorCode.InvalidData, "tRNS chunk appeared before IHDR.", chunkHeaderOffset);
                    }

                    if (seenIdat)
                    {
                        throw new StbException(StbErrorCode.InvalidData, "tRNS chunk appeared after IDAT.", chunkHeaderOffset);
                    }

                    if (palChannels != 0)
                    {
                        if (headerOnly)
                        {
                            return new PngMetadata(
                                width, height, depth, color, interlace != 0, isIphone,
                                rawChannels, PaletteChannels: 4, SourceChannels: 4,
                                HasTransparency: false, TotalIdatBytes: 0);
                        }

                        if (palLen == 0)
                        {
                            throw new StbException(StbErrorCode.InvalidData, "tRNS chunk appeared before PLTE.", chunkHeaderOffset);
                        }

                        if (length > palLen)
                        {
                            throw new StbException(StbErrorCode.InvalidData, $"tRNS length {length} exceeds PLTE entry count {palLen}.", chunkHeaderOffset);
                        }

                        palChannels = 4;
                        byte[] pal = palette!;
                        for (int i = 0; i < length; i++)
                        {
                            pal[(i * 4) + 3] = payload[i];
                        }
                    }
                    else
                    {
                        if ((rawChannels & 1) == 0)
                        {
                            throw new StbException(StbErrorCode.InvalidData, "tRNS chunk is invalid for PNG color types with alpha.", chunkHeaderOffset);
                        }

                        if (length != rawChannels * 2)
                        {
                            throw new StbException(StbErrorCode.InvalidData, $"Invalid tRNS length {length}; expected {rawChannels * 2}.", chunkHeaderOffset);
                        }

                        hasTrans = true;
                        if (headerOnly)
                        {
                            return new PngMetadata(
                                width, height, depth, color, interlace != 0, isIphone,
                                rawChannels, PaletteChannels: 0, SourceChannels: rawChannels + 1,
                                HasTransparency: true, TotalIdatBytes: 0);
                        }

                        if (depth == 16)
                        {
                            trans16 = new ushort[3];
                            for (int k = 0; k < rawChannels; k++)
                            {
                                trans16[k] = BinaryPrimitives.ReadUInt16BigEndian(payload.Slice(k * 2, 2));
                            }
                        }
                        else
                        {
                            trans8 = new byte[3];
                            byte scale = DepthScaleTable[depth];
                            for (int k = 0; k < rawChannels; k++)
                            {
                                ushort val = BinaryPrimitives.ReadUInt16BigEndian(payload.Slice(k * 2, 2));
                                trans8[k] = (byte)((val & 0xFF) * scale);
                            }
                        }
                    }

                    break;
                }

                case ChunkIDAT:
                {
                    if (first)
                    {
                        throw new StbException(StbErrorCode.InvalidData, "IDAT chunk appeared before IHDR.", chunkHeaderOffset);
                    }

                    if (palChannels != 0 && palLen == 0)
                    {
                        throw new StbException(StbErrorCode.InvalidData, "Paletted PNG is missing PLTE chunk.", chunkHeaderOffset);
                    }

                    if (headerOnly)
                    {
                        int srcCh = palChannels != 0 ? palChannels : (hasTrans ? rawChannels + 1 : rawChannels);
                        return new PngMetadata(
                            width, height, depth, color, interlace != 0, isIphone,
                            rawChannels, palChannels, srcCh, hasTrans, TotalIdatBytes: 0);
                    }

                    seenIdat = true;
                    totalIdatBytes += length;
                    limits.ValidateAllocation(totalIdatBytes);
                    if (length > 0)
                    {
                        idatChunks.Add(payloadOffset, length);
                    }

                    break;
                }

                case ChunkIEND:
                {
                    if (first)
                    {
                        throw new StbException(StbErrorCode.InvalidData, "IEND chunk appeared before IHDR.", chunkHeaderOffset);
                    }

                    int srcCh = palChannels != 0 ? palChannels : (hasTrans ? rawChannels + 1 : rawChannels);
                    if (headerOnly)
                    {
                        return new PngMetadata(
                            width, height, depth, color, interlace != 0, isIphone,
                            rawChannels, palChannels, srcCh, hasTrans, TotalIdatBytes: 0);
                    }

                    if (!seenIdat)
                    {
                        throw new StbException(StbErrorCode.InvalidData, "PNG contains no IDAT chunks.", chunkHeaderOffset);
                    }

                    return new PngMetadata(
                        width, height, depth, color, interlace != 0, isIphone,
                        rawChannels, palChannels, srcCh, hasTrans, (int)totalIdatBytes);
                }

                default:
                {
                    if (first)
                    {
                        throw new StbException(StbErrorCode.InvalidData, "First PNG chunk is not IHDR.", chunkHeaderOffset);
                    }

                    // Bit 5 of the first type byte (bit 29 of big-endian uint) is 0 for critical chunks.
                    if ((chunkType & (1u << 29)) == 0)
                    {
                        throw new StbException(
                            StbErrorCode.UnsupportedFeature,
                            $"Unknown critical PNG chunk 0x{chunkType:X8} at offset {chunkHeaderOffset}.",
                            chunkHeaderOffset);
                    }

                    break;
                }
            }
        }

        throw new StbException(StbErrorCode.TruncatedInput, "PNG ended unexpectedly before IEND chunk.", reader.Position);
    }

    private static int ComputeExpectedInflatedLength(in PngMetadata meta, StbLimits limits)
    {
        long total = 0;
        if (!meta.Interlaced)
        {
            long rowBytes = (((long)meta.RawChannels * meta.Width * meta.BitDepth) + 7) >> 3;
            total = (rowBytes + 1) * meta.Height;
        }
        else
        {
            for (int p = 0; p < 7; p++)
            {
                int passW = meta.Width > Adam7XOrigin[p]
                    ? (meta.Width - Adam7XOrigin[p] + Adam7XSpacing[p] - 1) / Adam7XSpacing[p]
                    : 0;
                int passH = meta.Height > Adam7YOrigin[p]
                    ? (meta.Height - Adam7YOrigin[p] + Adam7YSpacing[p] - 1) / Adam7YSpacing[p]
                    : 0;
                if (passW > 0 && passH > 0)
                {
                    long rowBytes = (((long)meta.RawChannels * passW * meta.BitDepth) + 7) >> 3;
                    total += (rowBytes + 1) * passH;
                }
            }
        }

        limits.ValidateDecompressedSize(total);
        limits.ValidateAllocation(total);
        return (int)total;
    }

    private static byte[] RentAndPrepareCompressedStream(
        ReadOnlySpan<byte> data,
        in IdatChunks chunks,
        int totalCompressedBytes,
        bool isIphone,
        out int deflateOffset,
        out int deflateLength)
    {
        if (totalCompressedBytes <= 0)
        {
            throw new StbException(StbErrorCode.CorruptStream, "PNG IDAT stream is empty.");
        }

        byte[] streamArray = ArrayPool<byte>.Shared.Rent(totalCompressedBytes);
        try
        {
            Span<byte> compressed = streamArray.AsSpan(0, totalCompressedBytes);
            int pos = 0;
            if (chunks.Count > 0)
            {
                data.Slice(chunks.First.Offset, chunks.First.Length).CopyTo(compressed.Slice(pos, chunks.First.Length));
                pos += chunks.First.Length;
                if (chunks.Additional is not null)
                {
                    for (int i = 0; i < chunks.Additional.Count; i++)
                    {
                        ChunkRange r = chunks.Additional[i];
                        data.Slice(r.Offset, r.Length).CopyTo(compressed.Slice(pos, r.Length));
                        pos += r.Length;
                    }
                }
            }

            deflateOffset = 0;
            deflateLength = totalCompressedBytes;
            if (!isIphone)
            {
                if (totalCompressedBytes < 2)
                {
                    throw new StbException(StbErrorCode.CorruptStream, "PNG zlib stream is too short for header.");
                }

                byte cmf = compressed[0];
                byte flg = compressed[1];
                int cm = cmf & 0x0F;
                if (((cmf << 8) | flg) % 31 != 0 || cm != 8)
                {
                    throw new StbException(StbErrorCode.CorruptStream, "Invalid PNG zlib header.");
                }

                if ((flg & 0x20) != 0)
                {
                    throw new StbException(StbErrorCode.UnsupportedFeature, "Preset zlib dictionary is not allowed in PNG.");
                }

                deflateOffset = 2;
                deflateLength = totalCompressedBytes - 2;
            }

            return streamArray;
        }
        catch
        {
            ArrayPool<byte>.Shared.Return(streamArray);
            throw;
        }
    }

#if NET8_0_OR_GREATER
    private static Stream CreateDecompressor(
#else
    private static DeflateStream CreateDecompressor(
#endif
        byte[] streamArray,
        int totalCompressedBytes,
        int deflateOffset,
        int deflateLength,
        bool isIphone,
        bool verifyChecksums,
        out bool computeManualAdler)
    {
#if NET8_0_OR_GREATER
        if (verifyChecksums && !isIphone)
        {
            computeManualAdler = false;
            MemoryStream ms = new(streamArray, 0, totalCompressedBytes, writable: false);
            try
            {
                return new ZLibStream(ms, CompressionMode.Decompress, leaveOpen: false);
            }
            catch
            {
                ms.Dispose();
                throw;
            }
        }
#endif
        computeManualAdler = verifyChecksums && !isIphone;
        MemoryStream rawMs = new(streamArray, deflateOffset, deflateLength, writable: false);
        try
        {
            return new DeflateStream(rawMs, CompressionMode.Decompress, leaveOpen: false);
        }
        catch
        {
            rawMs.Dispose();
            throw;
        }
    }

    private static void InflateIdatFull(
        ReadOnlySpan<byte> data,
        in IdatChunks chunks,
        int totalCompressedBytes,
        bool isIphone,
        bool verifyChecksums,
        StbLimits limits,
        Span<byte> destination)
    {
        byte[] streamArray = RentAndPrepareCompressedStream(
            data,
            in chunks,
            totalCompressedBytes,
            isIphone,
            out int deflateOffset,
            out int deflateLength);
        try
        {
            try
            {
                using Stream decompressor = CreateDecompressor(
                    streamArray,
                    totalCompressedBytes,
                    deflateOffset,
                    deflateLength,
                    isIphone,
                    verifyChecksums,
                    out bool computeManualAdler);

                ReadExactChunk(decompressor, destination, 0);
                uint adler = computeManualAdler ? Checksums.UpdateAdler32(1u, destination) : 1u;
                DrainAndVerifyTrailer(
                    decompressor,
                    streamArray.AsSpan(0, totalCompressedBytes),
                    destination.Length,
                    limits,
                    computeManualAdler,
                    adler);
            }
            catch (InvalidDataException ex)
            {
                throw new StbException(StbErrorCode.CorruptStream, "Corrupt PNG deflate/zlib stream.", innerException: ex);
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(streamArray);
        }
    }

    private static void ReadExactChunk(Stream decompressor, Span<byte> destination, long bytesSoFar)
    {
#if NET8_0_OR_GREATER
        int read = decompressor.ReadAtLeast(destination, destination.Length, throwOnEndOfStream: false);
        if (read < destination.Length)
        {
            throw new StbException(
                StbErrorCode.CorruptStream,
                $"PNG deflate stream ended early: decompressed {bytesSoFar + read} bytes, expected at least {bytesSoFar + destination.Length}.");
        }
#else
        int totalRead = 0;
        while (totalRead < destination.Length)
        {
            int n = decompressor.Read(destination.Slice(totalRead));
            if (n == 0)
            {
                throw new StbException(
                    StbErrorCode.CorruptStream,
                    $"PNG deflate stream ended early: decompressed {bytesSoFar + totalRead} bytes, expected at least {bytesSoFar + destination.Length}.");
            }

            totalRead += n;
        }
#endif
    }

    private static void DrainAndVerifyTrailer(
        Stream decompressor,
        ReadOnlySpan<byte> compressed,
        long totalDecompressed,
        StbLimits limits,
        bool computeManualAdler,
        uint adler)
    {
        // Drain any trailing bytes (e.g. stb issue #276) while still enforcing MaxDecompressedBytes
        // and allowing the decompressor to validate the end-of-block / Adler-32 trailer.
        Span<byte> drain = stackalloc byte[256];
        while (true)
        {
#if NET8_0_OR_GREATER
            int extra = decompressor.ReadAtLeast(drain, drain.Length, throwOnEndOfStream: false);
#else
            int extra = decompressor.Read(drain);
#endif
            if (extra == 0)
            {
                break;
            }

            totalDecompressed += extra;
            limits.ValidateDecompressedSize(totalDecompressed);
            if (computeManualAdler)
            {
                adler = Checksums.UpdateAdler32(adler, drain.Slice(0, extra));
            }
        }

        if (computeManualAdler)
        {
            if (compressed.Length < 6)
            {
                throw new StbException(StbErrorCode.CorruptStream, "PNG zlib stream is missing Adler-32 trailer.");
            }

            uint expectedAdler = BinaryPrimitives.ReadUInt32BigEndian(compressed.Slice(compressed.Length - 4, 4));
            if (adler != expectedAdler)
            {
                throw new StbException(
                    StbErrorCode.CorruptStream,
                    $"PNG zlib Adler-32 mismatch: expected 0x{expectedAdler:X8}, computed 0x{adler:X8}.");
            }
        }
    }

    private static void CreatePngImageInterlaced8(
        ReadOnlySpan<byte> raw,
        in PngMetadata meta,
        int outN,
        Span<byte> dest)
    {
        int rawOffset = 0;
        for (int p = 0; p < 7; p++)
        {
            int xOrig = Adam7XOrigin[p];
            int yOrig = Adam7YOrigin[p];
            int xSpc = Adam7XSpacing[p];
            int ySpc = Adam7YSpacing[p];

            int passW = meta.Width > xOrig ? (meta.Width - xOrig + xSpc - 1) / xSpc : 0;
            int passH = meta.Height > yOrig ? (meta.Height - yOrig + ySpc - 1) / ySpc : 0;
            if (passW <= 0 || passH <= 0)
            {
                continue;
            }

            int passRowBytes = ((meta.RawChannels * passW * meta.BitDepth) + 7) >> 3;
            int passRawLen = (passRowBytes + 1) * passH;
            using PooledBuffer<byte> passBuf = new(passW * passH * outN);
            CreatePngImageRaw8(
                raw.Slice(rawOffset, passRawLen),
                passW,
                passH,
                meta.RawChannels,
                outN,
                meta.BitDepth,
                meta.ColorType,
                passBuf.Span);

            ReadOnlySpan<byte> passPixels = passBuf.Span;
            int fullStride = meta.Width * outN;
            for (int j = 0; j < passH; j++)
            {
                int outRowStart = ((j * ySpc) + yOrig) * fullStride;
                int srcRowStart = j * passW * outN;
                for (int i = 0; i < passW; i++)
                {
                    int outIdx = outRowStart + (((i * xSpc) + xOrig) * outN);
                    int srcIdx = srcRowStart + (i * outN);
                    for (int c = 0; c < outN; c++)
                    {
                        dest[outIdx + c] = passPixels[srcIdx + c];
                    }
                }
            }

            rawOffset += passRawLen;
        }
    }

    private static void CreatePngImageInterlaced16(
        ReadOnlySpan<byte> raw,
        in PngMetadata meta,
        int outN,
        Span<ushort> dest)
    {
        int rawOffset = 0;
        for (int p = 0; p < 7; p++)
        {
            int xOrig = Adam7XOrigin[p];
            int yOrig = Adam7YOrigin[p];
            int xSpc = Adam7XSpacing[p];
            int ySpc = Adam7YSpacing[p];

            int passW = meta.Width > xOrig ? (meta.Width - xOrig + xSpc - 1) / xSpc : 0;
            int passH = meta.Height > yOrig ? (meta.Height - yOrig + ySpc - 1) / ySpc : 0;
            if (passW <= 0 || passH <= 0)
            {
                continue;
            }

            int passRowBytes = meta.RawChannels * passW * 2;
            int passRawLen = (passRowBytes + 1) * passH;
            using PooledBuffer<ushort> passBuf = new(passW * passH * outN);
            CreatePngImageRaw16(
                raw.Slice(rawOffset, passRawLen),
                passW,
                passH,
                meta.RawChannels,
                outN,
                passBuf.Span);

            ReadOnlySpan<ushort> passPixels = passBuf.Span;
            int fullStride = meta.Width * outN;
            for (int j = 0; j < passH; j++)
            {
                int outRowStart = ((j * ySpc) + yOrig) * fullStride;
                int srcRowStart = j * passW * outN;
                for (int i = 0; i < passW; i++)
                {
                    int outIdx = outRowStart + (((i * xSpc) + xOrig) * outN);
                    int srcIdx = srcRowStart + (i * outN);
                    for (int c = 0; c < outN; c++)
                    {
                        dest[outIdx + c] = passPixels[srcIdx + c];
                    }
                }
            }

            rawOffset += passRawLen;
        }
    }

    private static void CreatePngImageRaw8(
        ReadOnlySpan<byte> raw,
        int width,
        int height,
        int imgN,
        int outN,
        int depth,
        int color,
        Span<byte> dest)
    {
        int rowBytes = ((imgN * width * depth) + 7) >> 3;
        int filterBytes = depth < 8 ? 1 : imgN;
        int outStride = width * outN;

        if (depth == 8 && imgN == outN)
        {
            int rawPosDirect = 0;
            for (int j = 0; j < height; j++)
            {
                byte filterByte = raw[rawPosDirect++];
                if (filterByte > 4)
                {
                    throw new StbException(StbErrorCode.InvalidData, $"Invalid PNG scanline filter {filterByte} on row {j}.");
                }

                ReadOnlySpan<byte> rawRow = raw.Slice(rawPosDirect, rowBytes);
                rawPosDirect += rowBytes;

                Span<byte> destRow = dest.Slice(j * outStride, outStride);
                if (j == 0)
                {
                    PngUnfilter.UnfilterScanline(
                        PngUnfilter.MapFirstRowFilter(filterByte),
                        rawRow,
                        ReadOnlySpan<byte>.Empty,
                        destRow,
                        filterBytes);
                }
                else
                {
                    PngUnfilter.UnfilterScanline(
                        (PngFilterType)filterByte,
                        rawRow,
                        dest.Slice((j - 1) * outStride, outStride),
                        destRow,
                        filterBytes);
                }
            }

            return;
        }

        using PooledBuffer<byte> filterBuf = new(rowBytes * 2);
        Span<byte> rowBuf0 = filterBuf.Span.Slice(0, rowBytes);
        Span<byte> rowBuf1 = filterBuf.Span.Slice(rowBytes, rowBytes);
        byte scale = color == 0 ? DepthScaleTable[depth] : (byte)1;
        int sampleCount = width * imgN;

        int rawPos = 0;
        for (int j = 0; j < height; j++)
        {
            byte filterByte = raw[rawPos++];
            if (filterByte > 4)
            {
                throw new StbException(StbErrorCode.InvalidData, $"Invalid PNG scanline filter {filterByte} on row {j}.");
            }

            PngFilterType filter = j == 0
                ? PngUnfilter.MapFirstRowFilter(filterByte)
                : (PngFilterType)filterByte;

            Span<byte> cur = (j & 1) == 0 ? rowBuf0 : rowBuf1;
            ReadOnlySpan<byte> prior = (j & 1) == 0 ? rowBuf1 : rowBuf0;
            ReadOnlySpan<byte> rawRow = raw.Slice(rawPos, rowBytes);
            rawPos += rowBytes;

            PngUnfilter.UnfilterScanline(filter, rawRow, prior, cur, filterBytes);

            Span<byte> destRow = dest.Slice(j * outStride, outStride);
            if (depth == 8)
            {
                ExpandAlpha8(cur, destRow, width, imgN);
            }
            else
            {
                UnpackSubByteRow(cur, destRow, sampleCount, depth, scale);
                if (imgN != outN)
                {
                    ExpandAlpha8InPlace(destRow, width, imgN);
                }
            }
        }
    }

    private static void CreatePngImageRaw16(
        ReadOnlySpan<byte> raw,
        int width,
        int height,
        int imgN,
        int outN,
        Span<ushort> dest)
    {
        int rowBytes = imgN * width * 2;
        int filterBytes = imgN * 2;
        int outStride = width * outN;

        using PooledBuffer<byte> filterBuf = new(rowBytes * 2);
        Span<byte> rowBuf0 = filterBuf.Span.Slice(0, rowBytes);
        Span<byte> rowBuf1 = filterBuf.Span.Slice(rowBytes, rowBytes);

        int rawPos = 0;
        for (int j = 0; j < height; j++)
        {
            byte filterByte = raw[rawPos++];
            if (filterByte > 4)
            {
                throw new StbException(StbErrorCode.InvalidData, $"Invalid PNG scanline filter {filterByte} on row {j}.");
            }

            PngFilterType filter = j == 0
                ? PngUnfilter.MapFirstRowFilter(filterByte)
                : (PngFilterType)filterByte;

            Span<byte> cur = (j & 1) == 0 ? rowBuf0 : rowBuf1;
            ReadOnlySpan<byte> prior = (j & 1) == 0 ? rowBuf1 : rowBuf0;
            ReadOnlySpan<byte> rawRow = raw.Slice(rawPos, rowBytes);
            rawPos += rowBytes;

            PngUnfilter.UnfilterScanline(filter, rawRow, prior, cur, filterBytes);

            Span<ushort> destRow = dest.Slice(j * outStride, outStride);
            WriteRow16(cur, destRow, width, imgN, outN);
        }
    }

    private static void WriteRow16(ReadOnlySpan<byte> cur, Span<ushort> destRow, int width, int imgN, int outN)
    {
        if (imgN == outN)
        {
            int sampleCount = width * imgN;
            for (int i = 0; i < sampleCount; i++)
            {
                destRow[i] = (ushort)((cur[i * 2] << 8) | cur[(i * 2) + 1]);
            }
        }
        else if (imgN == 1)
        {
            for (int i = 0; i < width; i++)
            {
                destRow[i * 2] = (ushort)((cur[i * 2] << 8) | cur[(i * 2) + 1]);
                destRow[(i * 2) + 1] = 0xFFFF;
            }
        }
        else
        {
            for (int i = 0; i < width; i++)
            {
                int s = i * 6;
                int d = i * 4;
                destRow[d] = (ushort)((cur[s] << 8) | cur[s + 1]);
                destRow[d + 1] = (ushort)((cur[s + 2] << 8) | cur[s + 3]);
                destRow[d + 2] = (ushort)((cur[s + 4] << 8) | cur[s + 5]);
                destRow[d + 3] = 0xFFFF;
            }
        }
    }

    private static void UnpackSubByteRow(
        ReadOnlySpan<byte> cur,
        Span<byte> dest,
        int sampleCount,
        int depth,
        byte scale)
    {
        int srcIdx = 0;
        byte inb = 0;
        if (depth == 4)
        {
            for (int i = 0; i < sampleCount; i++)
            {
                if ((i & 1) == 0)
                {
                    inb = cur[srcIdx++];
                }

                dest[i] = (byte)(scale * (inb >> 4));
                inb <<= 4;
            }
        }
        else if (depth == 2)
        {
            for (int i = 0; i < sampleCount; i++)
            {
                if ((i & 3) == 0)
                {
                    inb = cur[srcIdx++];
                }

                dest[i] = (byte)(scale * (inb >> 6));
                inb <<= 2;
            }
        }
        else
        {
            for (int i = 0; i < sampleCount; i++)
            {
                if ((i & 7) == 0)
                {
                    inb = cur[srcIdx++];
                }

                dest[i] = (byte)(scale * (inb >> 7));
                inb <<= 1;
            }
        }
    }

    private static void ExpandAlpha8(ReadOnlySpan<byte> src, Span<byte> dest, int width, int imgN)
    {
        if (imgN == 1)
        {
            for (int i = 0; i < width; i++)
            {
                dest[i * 2] = src[i];
                dest[(i * 2) + 1] = 255;
            }
        }
        else
        {
            for (int i = 0; i < width; i++)
            {
                int s = i * 3;
                int d = i * 4;
                dest[d] = src[s];
                dest[d + 1] = src[s + 1];
                dest[d + 2] = src[s + 2];
                dest[d + 3] = 255;
            }
        }
    }

    private static void ExpandAlpha8InPlace(Span<byte> buffer, int width, int imgN)
    {
        if (imgN == 1)
        {
            for (int i = width - 1; i >= 0; i--)
            {
                buffer[(i * 2) + 1] = 255;
                buffer[i * 2] = buffer[i];
            }
        }
        else
        {
            for (int i = width - 1; i >= 0; i--)
            {
                int s = i * 3;
                int d = i * 4;
                buffer[d + 3] = 255;
                buffer[d + 2] = buffer[s + 2];
                buffer[d + 1] = buffer[s + 1];
                buffer[d] = buffer[s];
            }
        }
    }

    private static void ComputeTransparency8(Span<byte> pixels, byte[] tc, int outN, int pixelCount)
    {
        if (outN == 2)
        {
            byte t0 = tc[0];
            for (int i = 0; i < pixelCount; i++)
            {
                int idx = i * 2;
                pixels[idx + 1] = pixels[idx] == t0 ? (byte)0 : (byte)255;
            }
        }
        else
        {
            byte t0 = tc[0];
            byte t1 = tc[1];
            byte t2 = tc[2];
            for (int i = 0; i < pixelCount; i++)
            {
                int idx = i * 4;
                if (pixels[idx] == t0 && pixels[idx + 1] == t1 && pixels[idx + 2] == t2)
                {
                    pixels[idx + 3] = 0;
                }
            }
        }
    }

    private static void ComputeTransparency16(Span<ushort> pixels, ushort[] tc, int outN, int pixelCount)
    {
        if (outN == 2)
        {
            ushort t0 = tc[0];
            for (int i = 0; i < pixelCount; i++)
            {
                int idx = i * 2;
                pixels[idx + 1] = pixels[idx] == t0 ? (ushort)0 : (ushort)0xFFFF;
            }
        }
        else
        {
            ushort t0 = tc[0];
            ushort t1 = tc[1];
            ushort t2 = tc[2];
            for (int i = 0; i < pixelCount; i++)
            {
                int idx = i * 4;
                if (pixels[idx] == t0 && pixels[idx + 1] == t1 && pixels[idx + 2] == t2)
                {
                    pixels[idx + 3] = 0;
                }
            }
        }
    }

    private static void ExpandPalette(
        ReadOnlySpan<byte> indices,
        byte[] palette,
        int palOutN,
        int pixelCount,
        Span<byte> dest)
    {
        if (palOutN == 3)
        {
            for (int i = 0; i < pixelCount; i++)
            {
                int n = indices[i] * 4;
                int d = i * 3;
                dest[d] = palette[n];
                dest[d + 1] = palette[n + 1];
                dest[d + 2] = palette[n + 2];
            }
        }
        else
        {
            for (int i = 0; i < pixelCount; i++)
            {
                int n = indices[i] * 4;
                int d = i * 4;
                dest[d] = palette[n];
                dest[d + 1] = palette[n + 1];
                dest[d + 2] = palette[n + 2];
                dest[d + 3] = palette[n + 3];
            }
        }
    }

    private static void DeIphone8(Span<byte> pixels, int channels, int pixelCount, bool unpremultiply)
    {
        if (channels == 3)
        {
            for (int i = 0; i < pixelCount; i++)
            {
                int idx = i * 3;
                (pixels[idx], pixels[idx + 2]) = (pixels[idx + 2], pixels[idx]);
            }

            return;
        }

        if (unpremultiply)
        {
            for (int i = 0; i < pixelCount; i++)
            {
                int idx = i * 4;
                byte a = pixels[idx + 3];
                byte t = pixels[idx];
                if (a != 0)
                {
                    int half = a >> 1;
                    pixels[idx] = (byte)(((pixels[idx + 2] * 255) + half) / a);
                    pixels[idx + 1] = (byte)(((pixels[idx + 1] * 255) + half) / a);
                    pixels[idx + 2] = (byte)(((t * 255) + half) / a);
                }
                else
                {
                    pixels[idx] = pixels[idx + 2];
                    pixels[idx + 2] = t;
                }
            }
        }
        else
        {
            for (int i = 0; i < pixelCount; i++)
            {
                int idx = i * 4;
                (pixels[idx], pixels[idx + 2]) = (pixels[idx + 2], pixels[idx]);
            }
        }
    }

    private readonly record struct ChunkRange(int Offset, int Length);

    private struct IdatChunks
    {
        public ChunkRange First;
        public List<ChunkRange>? Additional;
        public int Count;

        public void Add(int offset, int length)
        {
            if (Count == 0)
            {
                First = new ChunkRange(offset, length);
            }
            else
            {
                Additional ??= [];
                Additional.Add(new ChunkRange(offset, length));
            }

            Count++;
        }
    }

    private readonly record struct PngMetadata(
        int Width,
        int Height,
        int BitDepth,
        int ColorType,
        bool Interlaced,
        bool IsIphone,
        int RawChannels,
        int PaletteChannels,
        int SourceChannels,
        bool HasTransparency,
        int TotalIdatBytes)
    {
        public bool IsPaletted => PaletteChannels != 0;
    }

    private sealed class InflateRingBuffer : IDisposable
    {
        private const int SlotCount = 3;
        private readonly byte[][] _slots = new byte[SlotCount][];
        private readonly int[] _lengths = new int[SlotCount];
        private readonly object _sync = new();
        private int _head;
        private int _tail;
        private int _count;
        private bool _producerDone;
        private bool _consumerCancelled;
        private Exception? _producerException;

        public InflateRingBuffer(int slotSize)
        {
            for (int i = 0; i < SlotCount; i++)
            {
                _slots[i] = ArrayPool<byte>.Shared.Rent(slotSize);
            }
        }

        public bool TryAcquireWriteSlot(out byte[]? slot)
        {
            lock (_sync)
            {
                while (_count == SlotCount && !_consumerCancelled)
                {
                    Monitor.Wait(_sync);
                }

                if (_consumerCancelled)
                {
                    slot = null;
                    return false;
                }

                slot = _slots[_tail];
                return true;
            }
        }

        public void PublishWriteSlot(int byteLength)
        {
            lock (_sync)
            {
                _lengths[_tail] = byteLength;
                _tail = (_tail + 1) % SlotCount;
                _count++;
                Monitor.Pulse(_sync);
            }
        }

        public void Complete()
        {
            lock (_sync)
            {
                _producerDone = true;
                Monitor.PulseAll(_sync);
            }
        }

        public void CompleteWithError(Exception ex)
        {
            lock (_sync)
            {
                _producerException = ex;
                _producerDone = true;
                Monitor.PulseAll(_sync);
            }
        }

        public bool TryTake(out byte[]? slot, out int byteLength)
        {
            lock (_sync)
            {
                while (_count == 0 && !_producerDone)
                {
                    Monitor.Wait(_sync);
                }

                if (_count == 0)
                {
                    slot = null;
                    byteLength = 0;
                    return false;
                }

                slot = _slots[_head];
                byteLength = _lengths[_head];
                return true;
            }
        }

        public void ReleaseSlot()
        {
            lock (_sync)
            {
                _head = (_head + 1) % SlotCount;
                _count--;
                Monitor.Pulse(_sync);
            }
        }

        public void CancelFromConsumer()
        {
            lock (_sync)
            {
                _consumerCancelled = true;
                Monitor.PulseAll(_sync);
            }
        }

        public void ThrowIfProducerFailed()
        {
            if (_producerException is not null)
            {
                ExceptionDispatchInfo.Capture(_producerException).Throw();
            }
        }

        public void Dispose()
        {
            for (int i = 0; i < SlotCount; i++)
            {
                byte[]? buf = _slots[i];
                if (buf is not null)
                {
                    _slots[i] = null!;
                    ArrayPool<byte>.Shared.Return(buf);
                }
            }
        }
    }
}
