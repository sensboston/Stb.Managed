using System;
using System.Buffers;
using System.Buffers.Binary;
using System.IO;
using System.IO.Compression;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using Stb.Managed.Internal;

namespace Stb.Managed.ImageWrite.Internal;

internal static class PngEncoder
{
    private const long ParallelPixelThreshold = 65_536;

    private static ReadOnlySpan<byte> PngSignature => [137, 80, 78, 71, 13, 10, 26, 10];
    private static ReadOnlySpan<byte> IhdrTag => "IHDR"u8;
    private static ReadOnlySpan<byte> IdatTag => "IDAT"u8;
    private static ReadOnlySpan<byte> IendChunk => [0, 0, 0, 0, (byte)'I', (byte)'E', (byte)'N', (byte)'D', 0xAE, 0x42, 0x60, 0x82];
    private static ReadOnlySpan<byte> ColorTypes => [0, 0, 4, 2, 6]; // index by comp 1..4

    internal static void Encode(
        ref ByteSink sink,
        ReadOnlySpan<byte> pixels,
        int width,
        int height,
        int comp,
        int strideBytes,
        ImageWriteOptions options,
        byte[]? sourceArray = null)
    {
        StbLimits limits = options.Options.Limits;
        int rowBytes = checked(width * comp);
        long filteredTotal = (long)(rowBytes + 1) * height;
        limits.ValidateAllocation(filteredTotal);

        byte[] filteredArray = ArrayPool<byte>.Shared.Rent((int)filteredTotal);
        byte[]? compressedArray = null;
        try
        {
            Span<byte> filtered = filteredArray.AsSpan(0, (int)filteredTotal);
            int dop = options.Options.ResolveDegreeOfParallelism((long)width * height, ParallelPixelThreshold);
            if (dop > 1 && height >= 16)
            {
                FilterAllRowsParallel(
                    pixels,
                    sourceArray,
                    filteredArray,
                    width,
                    height,
                    comp,
                    strideBytes,
                    options.FlipVertically,
                    options.PngFilter,
                    dop);
            }
            else
            {
                FilterRowRange(
                    pixels,
                    filtered,
                    0,
                    height,
                    width,
                    height,
                    comp,
                    strideBytes,
                    options.FlipVertically,
                    options.PngFilter);
            }

            int compressedLen = options.PngCompressionEngine == PngCompressionEngine.StbCompatible
                ? StbZlibCompressor.Compress(filtered, options.ResolvedPngCompressionLevel, limits, out compressedArray)
                : CompressPlatformZlib(filtered, options.ResolvedPngCompressionLevel, limits, out compressedArray);

            // Total PNG size = 8 (sig) + 25 (IHDR) + 12 + compressedLen (IDAT) + 12 (IEND)
            long totalPngSize = 8L + 25L + 12L + compressedLen + 12L;
            limits.ValidateAllocation(totalPngSize);

            // Signature + IHDR (33 bytes)
            Span<byte> head = sink.Reserve(33);
            if (sink.Overflowed)
            {
                return;
            }

            PngSignature.CopyTo(head);
            BinaryPrimitives.WriteUInt32BigEndian(head.Slice(8, 4), 13u);
            IhdrTag.CopyTo(head.Slice(12, 4));
            BinaryPrimitives.WriteUInt32BigEndian(head.Slice(16, 4), (uint)width);
            BinaryPrimitives.WriteUInt32BigEndian(head.Slice(20, 4), (uint)height);
            head[24] = 8; // bit depth
            head[25] = ColorTypes[comp];
            head[26] = 0; // compression method
            head[27] = 0; // filter method
            head[28] = 0; // interlace method
            uint ihdrCrc = Checksums.ComputeCrc32(head.Slice(12, 17));
            BinaryPrimitives.WriteUInt32BigEndian(head.Slice(29, 4), ihdrCrc);

            // IDAT chunk header (8 bytes) + payload + CRC (4 bytes)
            Span<byte> idatHead = sink.Reserve(8);
            if (sink.Overflowed)
            {
                return;
            }

            BinaryPrimitives.WriteUInt32BigEndian(idatHead.Slice(0, 4), (uint)compressedLen);
            IdatTag.CopyTo(idatHead.Slice(4, 4));

            ReadOnlySpan<byte> payload = compressedArray.AsSpan(0, compressedLen);
            sink.WriteBytes(payload);
            if (sink.Overflowed)
            {
                return;
            }

            uint idatCrc = Checksums.ComputeCrc32(IdatTag, payload);
            sink.WriteUInt32BigEndian(idatCrc);

            // IEND chunk (12 bytes)
            sink.WriteBytes(IendChunk);
        }
        finally
        {
            if (compressedArray is not null)
            {
                ArrayPool<byte>.Shared.Return(compressedArray);
            }

            ArrayPool<byte>.Shared.Return(filteredArray);
        }
    }

    private static void FilterAllRowsParallel(
        ReadOnlySpan<byte> pixels,
        byte[]? sourceArray,
        byte[] filteredArray,
        int width,
        int height,
        int comp,
        int strideBytes,
        bool flipVertically,
        PngFilterMode filterMode,
        int dop)
    {
        int workers = Math.Min(dop, Math.Max(1, height / 8));
        if (workers <= 1)
        {
            FilterRowRange(
                pixels,
                filteredArray.AsSpan(0, ((width * comp) + 1) * height),
                0,
                height,
                width,
                height,
                comp,
                strideBytes,
                flipVertically,
                filterMode);
            return;
        }

        byte[]? rentedSrc = null;
        byte[] srcArr;
        if (sourceArray is not null)
        {
            srcArr = sourceArray;
        }
        else
        {
            int requiredLen = ((height - 1) * strideBytes) + (width * comp);
            rentedSrc = ArrayPool<byte>.Shared.Rent(requiredLen);
            pixels.Slice(0, requiredLen).CopyTo(rentedSrc);
            srcArr = rentedSrc;
        }

        try
        {
            int filteredLen = ((width * comp) + 1) * height;
            ParallelOptions parallelOptions = new() { MaxDegreeOfParallelism = workers };
            Parallel.For(0, workers, parallelOptions, wIdx =>
            {
                int yStart = (int)((long)wIdx * height / workers);
                int yEnd = (int)((long)(wIdx + 1) * height / workers);
                if (yStart < yEnd)
                {
                    FilterRowRange(
                        srcArr,
                        filteredArray.AsSpan(0, filteredLen),
                        yStart,
                        yEnd,
                        width,
                        height,
                        comp,
                        strideBytes,
                        flipVertically,
                        filterMode);
                }
            });
        }
        finally
        {
            if (rentedSrc is not null)
            {
                ArrayPool<byte>.Shared.Return(rentedSrc);
            }
        }
    }

    internal static void FilterRowRange(
        ReadOnlySpan<byte> pixels,
        Span<byte> filtered,
        int yStart,
        int yEnd,
        int width,
        int height,
        int comp,
        int strideBytes,
        bool flipVertically,
        PngFilterMode filterMode)
    {
        int rowBytes = width * comp;
        int filtStride = rowBytes + 1;
        int forcedFilter = (int)filterMode;

        if (forcedFilter >= 0)
        {
            for (int y = yStart; y < yEnd; y++)
            {
                int srcRow = flipVertically ? height - 1 - y : y;
                ReadOnlySpan<byte> cur = pixels.Slice(srcRow * strideBytes, rowBytes);
                ReadOnlySpan<byte> prev = y > 0
                    ? pixels.Slice((flipVertically ? height - y : y - 1) * strideBytes, rowBytes)
                    : ReadOnlySpan<byte>.Empty;

                Span<byte> dstRow = filtered.Slice(y * filtStride, filtStride);
                dstRow[0] = (byte)forcedFilter;
                PngFilterKernels.EncodeLine(cur, prev, y == 0, comp, forcedFilter, dstRow.Slice(1));
            }

            return;
        }

        using PooledBuffer<byte> scratchBuf = new(rowBytes * 2);
        Span<byte> bestBuf = scratchBuf.Span.Slice(0, rowBytes);
        Span<byte> candBuf = scratchBuf.Span.Slice(rowBytes, rowBytes);

        for (int y = yStart; y < yEnd; y++)
        {
            int srcRow = flipVertically ? height - 1 - y : y;
            ReadOnlySpan<byte> cur = pixels.Slice(srcRow * strideBytes, rowBytes);
            bool isFirstRow = y == 0;
            ReadOnlySpan<byte> prev = !isFirstRow
                ? pixels.Slice((flipVertically ? height - y : y - 1) * strideBytes, rowBytes)
                : ReadOnlySpan<byte>.Empty;

            // Filter 0 (None): evaluate SAD directly on cur without copying.
            int bestFilter = 0;
            int bestSum = PngFilterKernels.ComputeSignedAbsSum(cur);

            if (isFirstRow)
            {
                // Row 0: Up == None and Paeth == Sub (so neither can beat < bestSum).
                int sumSub = PngFilterKernels.EncodeLineAndSad(cur, prev, true, comp, 1, candBuf);
                if (sumSub < bestSum)
                {
                    bestSum = sumSub;
                    bestFilter = 1;
                    Span<byte> tmp = bestBuf;
                    bestBuf = candBuf;
                    candBuf = tmp;
                }

                int sumAvg = PngFilterKernels.EncodeLineAndSad(cur, prev, true, comp, 3, candBuf);
                if (sumAvg < bestSum)
                {
                    bestFilter = 3;
                    Span<byte> tmp = bestBuf;
                    bestBuf = candBuf;
                    candBuf = tmp;
                }
            }
            else
            {
                for (int f = 1; f < 5; f++)
                {
                    int sum = PngFilterKernels.EncodeLineAndSad(cur, prev, false, comp, f, candBuf);
                    if (sum < bestSum)
                    {
                        bestSum = sum;
                        bestFilter = f;
                        Span<byte> tmp = bestBuf;
                        bestBuf = candBuf;
                        candBuf = tmp;
                    }
                }
            }

            Span<byte> dstRow = filtered.Slice(y * filtStride, filtStride);
            dstRow[0] = (byte)bestFilter;
            if (bestFilter == 0)
            {
                cur.CopyTo(dstRow.Slice(1));
            }
            else
            {
                bestBuf.CopyTo(dstRow.Slice(1));
            }
        }
    }

    internal static void FilterAllRowsScalar(
        ReadOnlySpan<byte> pixels,
        Span<byte> filtered,
        int width,
        int height,
        int comp,
        int strideBytes,
        bool flipVertically,
        PngFilterMode filterMode)
    {
        int rowBytes = width * comp;
        int filtStride = rowBytes + 1;
        int forcedFilter = (int)filterMode;

        using PooledBuffer<byte> lineBuf = forcedFilter < 0 ? new PooledBuffer<byte>(rowBytes) : default;
        Span<byte> lineScratch = lineBuf.Span;

        for (int y = 0; y < height; y++)
        {
            int srcRow = flipVertically ? height - 1 - y : y;
            ReadOnlySpan<byte> cur = pixels.Slice(srcRow * strideBytes, rowBytes);
            ReadOnlySpan<byte> prev = y > 0
                ? pixels.Slice((flipVertically ? height - y : y - 1) * strideBytes, rowBytes)
                : ReadOnlySpan<byte>.Empty;

            Span<byte> dstRow = filtered.Slice(y * filtStride, filtStride);
            Span<byte> dstPixels = dstRow.Slice(1);

            if (forcedFilter >= 0)
            {
                dstRow[0] = (byte)forcedFilter;
                EncodeLineScalar(cur, prev, y == 0, comp, forcedFilter, dstPixels);
            }
            else
            {
                int bestFilter = 0;
                int bestSum = int.MaxValue;

                for (int f = 0; f < 5; f++)
                {
                    EncodeLineScalar(cur, prev, y == 0, comp, f, lineScratch);
                    int sum = ComputeSignedAbsSumScalar(lineScratch);
                    if (sum < bestSum)
                    {
                        bestSum = sum;
                        bestFilter = f;
                    }
                }

                dstRow[0] = (byte)bestFilter;
                if (bestFilter == 4)
                {
                    lineScratch.CopyTo(dstPixels);
                }
                else
                {
                    EncodeLineScalar(cur, prev, y == 0, comp, bestFilter, dstPixels);
                }
            }
        }
    }

    internal static void EncodeLineScalar(
        ReadOnlySpan<byte> cur,
        ReadOnlySpan<byte> prev,
        bool isFirstRow,
        int bpp,
        int filterType,
        Span<byte> dst)
    {
        int len = cur.Length;
        if (filterType == 0 || (isFirstRow && filterType == 2))
        {
            cur.CopyTo(dst);
            return;
        }

        if (isFirstRow)
        {
            cur.Slice(0, bpp).CopyTo(dst.Slice(0, bpp));
            if (filterType == 3)
            {
                for (int i = bpp; i < len; i++)
                {
                    dst[i] = unchecked((byte)(cur[i] - (cur[i - bpp] >> 1)));
                }
            }
            else
            {
                for (int i = bpp; i < len; i++)
                {
                    dst[i] = unchecked((byte)(cur[i] - cur[i - bpp]));
                }
            }

            return;
        }

        switch (filterType)
        {
            case 1:
                cur.Slice(0, bpp).CopyTo(dst.Slice(0, bpp));
                for (int i = bpp; i < len; i++)
                {
                    dst[i] = unchecked((byte)(cur[i] - cur[i - bpp]));
                }

                break;

            case 2:
                for (int i = 0; i < len; i++)
                {
                    dst[i] = unchecked((byte)(cur[i] - prev[i]));
                }

                break;

            case 3:
                for (int i = 0; i < bpp; i++)
                {
                    dst[i] = unchecked((byte)(cur[i] - (prev[i] >> 1)));
                }

                for (int i = bpp; i < len; i++)
                {
                    dst[i] = unchecked((byte)(cur[i] - ((cur[i - bpp] + prev[i]) >> 1)));
                }

                break;

            default:
                for (int i = 0; i < bpp; i++)
                {
                    dst[i] = unchecked((byte)(cur[i] - prev[i]));
                }

                for (int i = bpp; i < len; i++)
                {
                    dst[i] = unchecked((byte)(cur[i] - PaethPredictor(cur[i - bpp], prev[i], prev[i - bpp])));
                }

                break;
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int PaethPredictor(int a, int b, int c)
    {
        int p = a + b - c;
        int pa = Math.Abs(p - a);
        int pb = Math.Abs(p - b);
        int pc = Math.Abs(p - c);
        if (pa <= pb && pa <= pc)
        {
            return a;
        }

        return pb <= pc ? b : c;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static int ComputeSignedAbsSumScalar(ReadOnlySpan<byte> line)
    {
        int sum = 0;
        for (int i = 0; i < line.Length; i++)
        {
            sum += Math.Abs((int)(sbyte)line[i]);
        }

        return sum;
    }

    private static int CompressPlatformZlib(
        ReadOnlySpan<byte> filtered,
        int level,
        StbLimits limits,
        out byte[] rentedOutput)
    {
        using PooledMemoryStream ms = new(Math.Max(256, filtered.Length / 2), limits);

#if NET9_0_OR_GREATER
        ZLibCompressionOptions zlibOpts = new()
        {
            CompressionLevel = Math.Clamp(level, 0, 9),
        };
        using (ZLibStream zs = new(ms, zlibOpts, leaveOpen: true))
        {
            zs.Write(filtered);
        }
#elif NET8_0_OR_GREATER
        CompressionLevel netLevel = level switch
        {
            0 => CompressionLevel.NoCompression,
            <= 3 => CompressionLevel.Fastest,
            <= 8 => CompressionLevel.Optimal,
            _ => CompressionLevel.SmallestSize,
        };
        using (ZLibStream zs = new(ms, netLevel, leaveOpen: true))
        {
            zs.Write(filtered);
        }
#else
        CompressionLevel netLevel = level switch
        {
            0 => CompressionLevel.NoCompression,
            <= 3 => CompressionLevel.Fastest,
            _ => CompressionLevel.Optimal,
        };
        byte flg = netLevel == CompressionLevel.Optimal ? (byte)0x9C : (byte)0x01;
        ms.WriteByte(0x78);
        ms.WriteByte(flg);
        using (DeflateStream ds = new(ms, netLevel, leaveOpen: true))
        {
            ds.Write(filtered);
        }

        uint adler = Checksums.ComputeAdler32(filtered);
        Span<byte> adlerBytes = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(adlerBytes, adler);
        ms.Write(adlerBytes);
#endif

        return ms.DetachBuffer(out rentedOutput);
    }

    private sealed class PooledMemoryStream : Stream
    {
        private readonly StbLimits _limits;
        private byte[]? _buffer;
        private int _length;

        public PooledMemoryStream(int initialCapacity, StbLimits limits)
        {
            _limits = limits;
            int cap = Math.Max(256, initialCapacity);
            limits.ValidateAllocation(cap);
            _buffer = ArrayPool<byte>.Shared.Rent(cap);
        }

        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => _length;
        public override long Position
        {
            get => _length;
            set => throw new NotSupportedException();
        }

        public override void Flush()
        {
        }

        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) =>
            Write(buffer.AsSpan(offset, count));

        public override void Write(ReadOnlySpan<byte> buffer)
        {
            if (buffer.IsEmpty)
            {
                return;
            }

            EnsureCapacity(_length + buffer.Length);
            buffer.CopyTo(_buffer.AsSpan(_length));
            _length += buffer.Length;
        }

        public override void WriteByte(byte value)
        {
            EnsureCapacity(_length + 1);
            _buffer![_length++] = value;
        }

        public int DetachBuffer(out byte[] rentedBuffer)
        {
            rentedBuffer = _buffer!;
            _buffer = null;
            return _length;
        }

        private void EnsureCapacity(int needed)
        {
            if (needed <= _buffer!.Length)
            {
                return;
            }

            long nextSize = Math.Max((long)needed, (long)_buffer.Length * 2L);
            if (nextSize > ArrayAllocation.MaxByteArrayLength)
            {
                nextSize = needed;
            }

            _limits.ValidateAllocation(nextSize);
            byte[] next = ArrayPool<byte>.Shared.Rent((int)nextSize);
            _buffer.AsSpan(0, _length).CopyTo(next);
            ArrayPool<byte>.Shared.Return(_buffer);
            _buffer = next;
        }

        protected override void Dispose(bool disposing)
        {
            byte[]? buf = _buffer;
            if (buf is not null)
            {
                _buffer = null;
                ArrayPool<byte>.Shared.Return(buf);
            }

            base.Dispose(disposing);
        }
    }
}
