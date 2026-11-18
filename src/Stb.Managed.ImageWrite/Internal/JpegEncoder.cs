using System;
using System.Buffers;
using System.Buffers.Binary;
using System.Runtime.CompilerServices;
#if NET8_0_OR_GREATER
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.Arm;
using System.Runtime.Intrinsics.X86;
#endif
using System.Threading.Tasks;
using Stb.Managed.Internal;

namespace Stb.Managed.ImageWrite.Internal;

/// <summary>
/// Baseline JPEG encoder over bounds-checked spans, matching <c>stbi_write_jpg_core</c>
/// byte-for-byte: vectorized RGB-&gt;YCbCr, 2x2 chroma subsampling, 8x8 AAN float forward DCT
/// (without FMA so floating-point rounding is 0-ULP identical to scalar C), 64-bit SWAR bit
/// writer with fast 0xFF detection, and opt-in deterministic multi-threaded MCU processing.
/// </summary>
internal static class JpegEncoder
{
    internal const int MaxDimension = 65535;
    private const long ParallelPixelThreshold = 65_536;

    private static ReadOnlySpan<byte> ZigZag =>
    [
        0,  1,  5,  6, 14, 15, 27, 28,
        2,  4,  7, 13, 16, 26, 29, 42,
        3,  8, 12, 17, 25, 30, 41, 43,
        9, 11, 18, 24, 31, 40, 44, 53,
        10, 19, 23, 32, 39, 45, 52, 54,
        20, 22, 33, 38, 46, 51, 55, 60,
        21, 34, 37, 47, 50, 56, 59, 61,
        35, 36, 48, 49, 57, 58, 62, 63,
    ];

    private static ReadOnlySpan<byte> StdDcLumNrCodes =>
        [0, 1, 5, 1, 1, 1, 1, 1, 1, 0, 0, 0, 0, 0, 0, 0];

    private static ReadOnlySpan<byte> StdDcLumValues =>
        [0, 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11];

    private static ReadOnlySpan<byte> StdAcLumNrCodes =>
        [0, 2, 1, 3, 3, 2, 4, 3, 5, 5, 4, 4, 0, 0, 1, 0x7d];

    private static ReadOnlySpan<byte> StdAcLumValues =>
    [
        0x01, 0x02, 0x03, 0x00, 0x04, 0x11, 0x05, 0x12, 0x21, 0x31, 0x41, 0x06, 0x13, 0x51, 0x61, 0x07,
        0x22, 0x71, 0x14, 0x32, 0x81, 0x91, 0xa1, 0x08, 0x23, 0x42, 0xb1, 0xc1, 0x15, 0x52, 0xd1, 0xf0,
        0x24, 0x33, 0x62, 0x72, 0x82, 0x09, 0x0a, 0x16, 0x17, 0x18, 0x19, 0x1a, 0x25, 0x26, 0x27, 0x28,
        0x29, 0x2a, 0x34, 0x35, 0x36, 0x37, 0x38, 0x39, 0x3a, 0x43, 0x44, 0x45, 0x46, 0x47, 0x48, 0x49,
        0x4a, 0x53, 0x54, 0x55, 0x56, 0x57, 0x58, 0x59, 0x5a, 0x63, 0x64, 0x65, 0x66, 0x67, 0x68, 0x69,
        0x6a, 0x73, 0x74, 0x75, 0x76, 0x77, 0x78, 0x79, 0x7a, 0x83, 0x84, 0x85, 0x86, 0x87, 0x88, 0x89,
        0x8a, 0x92, 0x93, 0x94, 0x95, 0x96, 0x97, 0x98, 0x99, 0x9a, 0xa2, 0xa3, 0xa4, 0xa5, 0xa6, 0xa7,
        0xa8, 0xa9, 0xaa, 0xb2, 0xb3, 0xb4, 0xb5, 0xb6, 0xb7, 0xb8, 0xb9, 0xba, 0xc2, 0xc3, 0xc4, 0xc5,
        0xc6, 0xc7, 0xc8, 0xc9, 0xca, 0xd2, 0xd3, 0xd4, 0xd5, 0xd6, 0xd7, 0xd8, 0xd9, 0xda, 0xe1, 0xe2,
        0xe3, 0xe4, 0xe5, 0xe6, 0xe7, 0xe8, 0xe9, 0xea, 0xf1, 0xf2, 0xf3, 0xf4, 0xf5, 0xf6, 0xf7, 0xf8,
        0xf9, 0xfa,
    ];

    private static ReadOnlySpan<byte> StdDcChromNrCodes =>
        [0, 3, 1, 1, 1, 1, 1, 1, 1, 1, 1, 0, 0, 0, 0, 0];

    private static ReadOnlySpan<byte> StdDcChromValues =>
        [0, 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11];

    private static ReadOnlySpan<byte> StdAcChromNrCodes =>
        [0, 2, 1, 2, 4, 4, 3, 4, 7, 5, 4, 4, 0, 1, 2, 0x77];

    private static ReadOnlySpan<byte> StdAcChromValues =>
    [
        0x00, 0x01, 0x02, 0x03, 0x11, 0x04, 0x05, 0x21, 0x31, 0x06, 0x12, 0x41, 0x51, 0x07, 0x61, 0x71,
        0x13, 0x22, 0x32, 0x81, 0x08, 0x14, 0x42, 0x91, 0xa1, 0xb1, 0xc1, 0x09, 0x23, 0x33, 0x52, 0xf0,
        0x15, 0x62, 0x72, 0xd1, 0x0a, 0x16, 0x24, 0x34, 0xe1, 0x25, 0xf1, 0x17, 0x18, 0x19, 0x1a, 0x26,
        0x27, 0x28, 0x29, 0x2a, 0x35, 0x36, 0x37, 0x38, 0x39, 0x3a, 0x43, 0x44, 0x45, 0x46, 0x47, 0x48,
        0x49, 0x4a, 0x53, 0x54, 0x55, 0x56, 0x57, 0x58, 0x59, 0x5a, 0x63, 0x64, 0x65, 0x66, 0x67, 0x68,
        0x69, 0x6a, 0x73, 0x74, 0x75, 0x76, 0x77, 0x78, 0x79, 0x7a, 0x82, 0x83, 0x84, 0x85, 0x86, 0x87,
        0x88, 0x89, 0x8a, 0x92, 0x93, 0x94, 0x95, 0x96, 0x97, 0x98, 0x99, 0x9a, 0xa2, 0xa3, 0xa4, 0xa5,
        0xa6, 0xa7, 0xa8, 0xa9, 0xaa, 0xb2, 0xb3, 0xb4, 0xb5, 0xb6, 0xb7, 0xb8, 0xb9, 0xba, 0xc2, 0xc3,
        0xc4, 0xc5, 0xc6, 0xc7, 0xc8, 0xc9, 0xca, 0xd2, 0xd3, 0xd4, 0xd5, 0xd6, 0xd7, 0xd8, 0xd9, 0xda,
        0xe2, 0xe3, 0xe4, 0xe5, 0xe6, 0xe7, 0xe8, 0xe9, 0xea, 0xf2, 0xf3, 0xf4, 0xf5, 0xf6, 0xf7, 0xf8,
        0xf9, 0xfa,
    ];

    private static ReadOnlySpan<byte> BaseYqt =>
    [
        16, 11, 10, 16,  24,  40,  51,  61,
        12, 12, 14, 19,  26,  58,  60,  55,
        14, 13, 16, 24,  40,  57,  69,  56,
        14, 17, 22, 29,  51,  87,  80,  62,
        18, 22, 37, 56,  68, 109, 103,  77,
        24, 35, 55, 64,  81, 104, 113,  92,
        49, 64, 78, 87, 103, 121, 120, 101,
        72, 92, 95, 98, 112, 100, 103,  99,
    ];

    private static ReadOnlySpan<byte> BaseUvqt =>
    [
        17, 18, 24, 47, 99, 99, 99, 99,
        18, 21, 26, 66, 99, 99, 99, 99,
        24, 26, 56, 99, 99, 99, 99, 99,
        47, 66, 99, 99, 99, 99, 99, 99,
        99, 99, 99, 99, 99, 99, 99, 99,
        99, 99, 99, 99, 99, 99, 99, 99,
        99, 99, 99, 99, 99, 99, 99, 99,
        99, 99, 99, 99, 99, 99, 99, 99,
    ];

    private static readonly float[] Aasf =
    [
        1.0f * 2.828427125f,
        1.387039845f * 2.828427125f,
        1.306562965f * 2.828427125f,
        1.175875602f * 2.828427125f,
        1.0f * 2.828427125f,
        0.785694958f * 2.828427125f,
        0.541196100f * 2.828427125f,
        0.275899379f * 2.828427125f,
    ];

    private static ReadOnlySpan<byte> HeaderHead0 =>
    [
        0xFF, 0xD8, 0xFF, 0xE0, 0, 0x10, (byte)'J', (byte)'F', (byte)'I', (byte)'F', 0, 1, 1, 0, 0, 1, 0, 1, 0, 0,
        0xFF, 0xDB, 0, 0x84, 0,
    ];

    private static ReadOnlySpan<byte> HeaderHead2 =>
        [0xFF, 0xDA, 0, 0x0C, 3, 1, 0, 2, 0x11, 3, 0x11, 0, 0x3F, 0];

    // Packed Huffman lookup tables: (code << 5) | bitLength.
    private static readonly uint[] YdcHt = BuildHuffmanTable(StdDcLumNrCodes, StdDcLumValues);
    private static readonly uint[] UvdcHt = BuildHuffmanTable(StdDcChromNrCodes, StdDcChromValues);
    private static readonly uint[] YacHt = BuildHuffmanTable(StdAcLumNrCodes, StdAcLumValues);
    private static readonly uint[] UvacHt = BuildHuffmanTable(StdAcChromNrCodes, StdAcChromValues);

    private static uint[] BuildHuffmanTable(ReadOnlySpan<byte> nrCodes, ReadOnlySpan<byte> values)
    {
        uint[] table = new uint[256];
        uint code = 0;
        int idx = 0;
        for (int len = 1; len <= 16; len++)
        {
            int count = nrCodes[len - 1];
            for (int i = 0; i < count; i++)
            {
                table[values[idx++]] = (code << 5) | (uint)len;
                code++;
            }

            code <<= 1;
        }

        return table;
    }

    internal static void Encode(
        ref ByteSink sink,
        ReadOnlySpan<byte> pixels,
        int width,
        int height,
        int comp,
        int strideBytes,
        int quality,
        bool flipVertically,
        StbOptions options,
        byte[]? sourceArray = null)
    {
        bool subsample = quality <= 90;
        int qScale = quality < 50 ? 5000 / quality : 200 - (quality * 2);

        Span<byte> yTable = stackalloc byte[64];
        Span<byte> uvTable = stackalloc byte[64];
        float[] fdtblYArr = ArrayPool<float>.Shared.Rent(64);
        float[] fdtblUvArr = ArrayPool<float>.Shared.Rent(64);

        try
        {
            Span<float> fdtblY = fdtblYArr.AsSpan(0, 64);
            Span<float> fdtblUv = fdtblUvArr.AsSpan(0, 64);

            ReadOnlySpan<byte> zigZag = ZigZag;
            ReadOnlySpan<byte> baseYqt = BaseYqt;
            ReadOnlySpan<byte> baseUvqt = BaseUvqt;

            for (int i = 0; i < 64; i++)
            {
                int yti = ((baseYqt[i] * qScale) + 50) / 100;
                yTable[zigZag[i]] = (byte)Math.Clamp(yti, 1, 255);
                int uvti = ((baseUvqt[i] * qScale) + 50) / 100;
                uvTable[zigZag[i]] = (byte)Math.Clamp(uvti, 1, 255);
            }

            float[] aasf = Aasf;
            for (int row = 0, k = 0; row < 8; row++)
            {
                float aRow = aasf[row];
                for (int col = 0; col < 8; col++, k++)
                {
                    int zz = zigZag[k];
                    fdtblY[k] = 1.0f / ((yTable[zz] * aRow) * aasf[col]);
                    fdtblUv[k] = 1.0f / ((uvTable[zz] * aRow) * aasf[col]);
                }
            }

            WriteHeaders(ref sink, width, height, subsample, yTable, uvTable);
            if (sink.Overflowed)
            {
                return;
            }

            int mcuSize = subsample ? 16 : 8;
            int mcusY = (height + mcuSize - 1) / mcuSize;
            int dop = options.ResolveDegreeOfParallelism((long)width * height, ParallelPixelThreshold);

            if (dop > 1 && mcusY >= 4)
            {
                EncodeParallel(
                    ref sink,
                    pixels,
                    sourceArray,
                    width,
                    height,
                    comp,
                    strideBytes,
                    subsample,
                    flipVertically,
                    fdtblYArr,
                    fdtblUvArr,
                    dop,
                    options.Limits);
            }
            else
            {
                EncodeSingleThreaded(
                    ref sink,
                    pixels,
                    width,
                    height,
                    comp,
                    strideBytes,
                    subsample,
                    flipVertically,
                    fdtblY,
                    fdtblUv);
            }
        }
        finally
        {
            ArrayPool<float>.Shared.Return(fdtblYArr);
            ArrayPool<float>.Shared.Return(fdtblUvArr);
        }
    }

    private static void EncodeSingleThreaded(
        ref ByteSink sink,
        ReadOnlySpan<byte> pixels,
        int width,
        int height,
        int comp,
        int strideBytes,
        bool subsample,
        bool flipVertically,
        scoped ReadOnlySpan<float> fdtblY,
        scoped ReadOnlySpan<float> fdtblUv)
    {
        int dcY = 0;
        int dcU = 0;
        int dcV = 0;
        ulong bitBuf = 0;
        int bitCnt = 0;

        Span<short> duScratch = stackalloc short[64];

        if (subsample)
        {
            Span<float> yBlock = stackalloc float[256];
            Span<float> uBlock = stackalloc float[256];
            Span<float> vBlock = stackalloc float[256];
            Span<float> subU = stackalloc float[64];
            Span<float> subV = stackalloc float[64];

            for (int y = 0; y < height; y += 16)
            {
                bool fullY = y + 16 <= height;
                for (int x = 0; x < width; x += 16)
                {
                    if (fullY && x + 16 <= width)
                    {
                        ConvertMcu16Interior(pixels, height, comp, strideBytes, x, y, flipVertically, yBlock, uBlock, vBlock);
                    }
                    else
                    {
                        ConvertMcuEdge(pixels, width, height, comp, strideBytes, x, y, 16, flipVertically, yBlock, uBlock, vBlock);
                    }

                    int end0 = DctAndQuantize(yBlock.Slice(0), 16, fdtblY, duScratch);
                    dcY = EncodeQuantizedBlock(ref sink, ref bitBuf, ref bitCnt, duScratch, end0, dcY, YdcHt, YacHt);
                    end0 = DctAndQuantize(yBlock.Slice(8), 16, fdtblY, duScratch);
                    dcY = EncodeQuantizedBlock(ref sink, ref bitBuf, ref bitCnt, duScratch, end0, dcY, YdcHt, YacHt);
                    end0 = DctAndQuantize(yBlock.Slice(128), 16, fdtblY, duScratch);
                    dcY = EncodeQuantizedBlock(ref sink, ref bitBuf, ref bitCnt, duScratch, end0, dcY, YdcHt, YacHt);
                    end0 = DctAndQuantize(yBlock.Slice(136), 16, fdtblY, duScratch);
                    dcY = EncodeQuantizedBlock(ref sink, ref bitBuf, ref bitCnt, duScratch, end0, dcY, YdcHt, YacHt);

                    Subsample2x2(uBlock, subU);
                    Subsample2x2(vBlock, subV);

                    end0 = DctAndQuantize(subU, 8, fdtblUv, duScratch);
                    dcU = EncodeQuantizedBlock(ref sink, ref bitBuf, ref bitCnt, duScratch, end0, dcU, UvdcHt, UvacHt);
                    end0 = DctAndQuantize(subV, 8, fdtblUv, duScratch);
                    dcV = EncodeQuantizedBlock(ref sink, ref bitBuf, ref bitCnt, duScratch, end0, dcV, UvdcHt, UvacHt);

                    if (sink.Overflowed)
                    {
                        return;
                    }
                }
            }
        }
        else
        {
            Span<float> yBlock = stackalloc float[64];
            Span<float> uBlock = stackalloc float[64];
            Span<float> vBlock = stackalloc float[64];

            for (int y = 0; y < height; y += 8)
            {
                bool fullY = y + 8 <= height;
                for (int x = 0; x < width; x += 8)
                {
                    if (fullY && x + 8 <= width)
                    {
                        ConvertMcu8Interior(pixels, height, comp, strideBytes, x, y, flipVertically, yBlock, uBlock, vBlock);
                    }
                    else
                    {
                        ConvertMcuEdge(pixels, width, height, comp, strideBytes, x, y, 8, flipVertically, yBlock, uBlock, vBlock);
                    }

                    int end0 = DctAndQuantize(yBlock, 8, fdtblY, duScratch);
                    dcY = EncodeQuantizedBlock(ref sink, ref bitBuf, ref bitCnt, duScratch, end0, dcY, YdcHt, YacHt);
                    end0 = DctAndQuantize(uBlock, 8, fdtblUv, duScratch);
                    dcU = EncodeQuantizedBlock(ref sink, ref bitBuf, ref bitCnt, duScratch, end0, dcU, UvdcHt, UvacHt);
                    end0 = DctAndQuantize(vBlock, 8, fdtblUv, duScratch);
                    dcV = EncodeQuantizedBlock(ref sink, ref bitBuf, ref bitCnt, duScratch, end0, dcV, UvdcHt, UvacHt);

                    if (sink.Overflowed)
                    {
                        return;
                    }
                }
            }
        }

        FinishBitstream(ref sink, ref bitBuf, ref bitCnt);
    }

    private static void EncodeParallel(
        ref ByteSink sink,
        ReadOnlySpan<byte> pixels,
        byte[]? sourceArray,
        int width,
        int height,
        int comp,
        int strideBytes,
        bool subsample,
        bool flipVertically,
        float[] fdtblYArr,
        float[] fdtblUvArr,
        int dop,
        StbLimits limits)
    {
        int mcuSize = subsample ? 16 : 8;
        int mcusX = (width + mcuSize - 1) / mcuSize;
        int mcusY = (height + mcuSize - 1) / mcuSize;
        int blocksPerMcu = subsample ? 6 : 3;
        long totalBlocks = (long)mcusX * mcusY * blocksPerMcu;
        long blockBufferBytes = totalBlocks * 64L * sizeof(short);
        if (blockBufferBytes > limits.MaxAllocationBytes || totalBlocks * 64L > int.MaxValue)
        {
            EncodeSingleThreaded(
                ref sink,
                pixels,
                width,
                height,
                comp,
                strideBytes,
                subsample,
                flipVertically,
                fdtblYArr.AsSpan(0, 64),
                fdtblUvArr.AsSpan(0, 64));
            return;
        }

        int workers = Math.Min(dop, Math.Max(1, mcusY / 2));
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

        short[] quantizedBlocks = ArrayPool<short>.Shared.Rent((int)(totalBlocks * 64));
        byte[] endPositions = ArrayPool<byte>.Shared.Rent((int)totalBlocks);

        try
        {
            // Phase 1: Parallel RGB->YCbCr + Subsample + SIMD 8x8 DCT + Quantize + ZigZag.
            ParallelOptions parallelOptions = new() { MaxDegreeOfParallelism = workers };
            Parallel.For(0, workers, parallelOptions, wIdx =>
            {
                int myStart = (int)((long)wIdx * mcusY / workers);
                int myEnd = (int)((long)(wIdx + 1) * mcusY / workers);
                if (myStart >= myEnd)
                {
                    return;
                }

                ReadOnlySpan<byte> srcSpan = srcArr;
                ReadOnlySpan<float> fdtblY = fdtblYArr.AsSpan(0, 64);
                ReadOnlySpan<float> fdtblUv = fdtblUvArr.AsSpan(0, 64);

                if (subsample)
                {
                    Span<float> yBlock = stackalloc float[256];
                    Span<float> uBlock = stackalloc float[256];
                    Span<float> vBlock = stackalloc float[256];
                    Span<float> subU = stackalloc float[64];
                    Span<float> subV = stackalloc float[64];

                    for (int my = myStart; my < myEnd; my++)
                    {
                        int y = my * 16;
                        bool fullY = y + 16 <= height;
                        int baseBlock = my * mcusX * 6;
                        for (int mx = 0; mx < mcusX; mx++)
                        {
                            int x = mx * 16;
                            if (fullY && x + 16 <= width)
                            {
                                ConvertMcu16Interior(srcSpan, height, comp, strideBytes, x, y, flipVertically, yBlock, uBlock, vBlock);
                            }
                            else
                            {
                                ConvertMcuEdge(srcSpan, width, height, comp, strideBytes, x, y, 16, flipVertically, yBlock, uBlock, vBlock);
                            }

                            int bi = baseBlock + (mx * 6);
                            endPositions[bi] = (byte)DctAndQuantize(yBlock.Slice(0), 16, fdtblY, quantizedBlocks.AsSpan(bi * 64, 64));
                            endPositions[bi + 1] = (byte)DctAndQuantize(yBlock.Slice(8), 16, fdtblY, quantizedBlocks.AsSpan((bi + 1) * 64, 64));
                            endPositions[bi + 2] = (byte)DctAndQuantize(yBlock.Slice(128), 16, fdtblY, quantizedBlocks.AsSpan((bi + 2) * 64, 64));
                            endPositions[bi + 3] = (byte)DctAndQuantize(yBlock.Slice(136), 16, fdtblY, quantizedBlocks.AsSpan((bi + 3) * 64, 64));

                            Subsample2x2(uBlock, subU);
                            Subsample2x2(vBlock, subV);

                            endPositions[bi + 4] = (byte)DctAndQuantize(subU, 8, fdtblUv, quantizedBlocks.AsSpan((bi + 4) * 64, 64));
                            endPositions[bi + 5] = (byte)DctAndQuantize(subV, 8, fdtblUv, quantizedBlocks.AsSpan((bi + 5) * 64, 64));
                        }
                    }
                }
                else
                {
                    Span<float> yBlock = stackalloc float[64];
                    Span<float> uBlock = stackalloc float[64];
                    Span<float> vBlock = stackalloc float[64];

                    for (int my = myStart; my < myEnd; my++)
                    {
                        int y = my * 8;
                        bool fullY = y + 8 <= height;
                        int baseBlock = my * mcusX * 3;
                        for (int mx = 0; mx < mcusX; mx++)
                        {
                            int x = mx * 8;
                            if (fullY && x + 8 <= width)
                            {
                                ConvertMcu8Interior(srcSpan, height, comp, strideBytes, x, y, flipVertically, yBlock, uBlock, vBlock);
                            }
                            else
                            {
                                ConvertMcuEdge(srcSpan, width, height, comp, strideBytes, x, y, 8, flipVertically, yBlock, uBlock, vBlock);
                            }

                            int bi = baseBlock + (mx * 3);
                            endPositions[bi] = (byte)DctAndQuantize(yBlock, 8, fdtblY, quantizedBlocks.AsSpan(bi * 64, 64));
                            endPositions[bi + 1] = (byte)DctAndQuantize(uBlock, 8, fdtblUv, quantizedBlocks.AsSpan((bi + 1) * 64, 64));
                            endPositions[bi + 2] = (byte)DctAndQuantize(vBlock, 8, fdtblUv, quantizedBlocks.AsSpan((bi + 2) * 64, 64));
                        }
                    }
                }
            });

            // Phase 2: Deterministic sequential Huffman coding with 64-bit SWAR bit-writer.
            int dcY = 0;
            int dcU = 0;
            int dcV = 0;
            ulong bitBuf = 0;
            int bitCnt = 0;
            int mcuCount = mcusX * mcusY;

            if (subsample)
            {
                for (int mcu = 0; mcu < mcuCount; mcu++)
                {
                    int bi = mcu * 6;
                    dcY = EncodeQuantizedBlock(ref sink, ref bitBuf, ref bitCnt, quantizedBlocks.AsSpan(bi * 64, 64), endPositions[bi], dcY, YdcHt, YacHt);
                    dcY = EncodeQuantizedBlock(ref sink, ref bitBuf, ref bitCnt, quantizedBlocks.AsSpan((bi + 1) * 64, 64), endPositions[bi + 1], dcY, YdcHt, YacHt);
                    dcY = EncodeQuantizedBlock(ref sink, ref bitBuf, ref bitCnt, quantizedBlocks.AsSpan((bi + 2) * 64, 64), endPositions[bi + 2], dcY, YdcHt, YacHt);
                    dcY = EncodeQuantizedBlock(ref sink, ref bitBuf, ref bitCnt, quantizedBlocks.AsSpan((bi + 3) * 64, 64), endPositions[bi + 3], dcY, YdcHt, YacHt);
                    dcU = EncodeQuantizedBlock(ref sink, ref bitBuf, ref bitCnt, quantizedBlocks.AsSpan((bi + 4) * 64, 64), endPositions[bi + 4], dcU, UvdcHt, UvacHt);
                    dcV = EncodeQuantizedBlock(ref sink, ref bitBuf, ref bitCnt, quantizedBlocks.AsSpan((bi + 5) * 64, 64), endPositions[bi + 5], dcV, UvdcHt, UvacHt);
                    if (sink.Overflowed)
                    {
                        return;
                    }
                }
            }
            else
            {
                for (int mcu = 0; mcu < mcuCount; mcu++)
                {
                    int bi = mcu * 3;
                    dcY = EncodeQuantizedBlock(ref sink, ref bitBuf, ref bitCnt, quantizedBlocks.AsSpan(bi * 64, 64), endPositions[bi], dcY, YdcHt, YacHt);
                    dcU = EncodeQuantizedBlock(ref sink, ref bitBuf, ref bitCnt, quantizedBlocks.AsSpan((bi + 1) * 64, 64), endPositions[bi + 1], dcU, UvdcHt, UvacHt);
                    dcV = EncodeQuantizedBlock(ref sink, ref bitBuf, ref bitCnt, quantizedBlocks.AsSpan((bi + 2) * 64, 64), endPositions[bi + 2], dcV, UvdcHt, UvacHt);
                    if (sink.Overflowed)
                    {
                        return;
                    }
                }
            }

            FinishBitstream(ref sink, ref bitBuf, ref bitCnt);
        }
        finally
        {
            ArrayPool<short>.Shared.Return(quantizedBlocks);
            ArrayPool<byte>.Shared.Return(endPositions);
            if (rentedSrc is not null)
            {
                ArrayPool<byte>.Shared.Return(rentedSrc);
            }
        }
    }

    // =========================================================================
    // RGB -> YCbCr conversion (vectorized interior + scalar edge)
    // =========================================================================

    private static void ConvertMcuEdge(
        ReadOnlySpan<byte> pixels,
        int width,
        int height,
        int comp,
        int strideBytes,
        int x,
        int y,
        int size,
        bool flipVertically,
        Span<float> yBlock,
        Span<float> uBlock,
        Span<float> vBlock)
    {
        int ofsG = comp > 2 ? 1 : 0;
        int ofsB = comp > 2 ? 2 : 0;
        int pos = 0;
        for (int row = y; row < y + size; row++)
        {
            int clampedRow = row < height ? row : height - 1;
            int srcRow = flipVertically ? height - 1 - clampedRow : clampedRow;
            int baseP = srcRow * strideBytes;
            for (int col = x; col < x + size; col++, pos++)
            {
                int clampedCol = col < width ? col : width - 1;
                int p = baseP + (clampedCol * comp);
                float r = pixels[p];
                float g = pixels[p + ofsG];
                float b = pixels[p + ofsB];
                yBlock[pos] = (((+0.29900f * r) + (0.58700f * g)) + (0.11400f * b)) - 128f;
                uBlock[pos] = ((-0.16874f * r) - (0.33126f * g)) + (0.50000f * b);
                vBlock[pos] = ((+0.50000f * r) - (0.41869f * g)) - (0.08131f * b);
            }
        }
    }

    private static void ConvertMcu16Interior(
        ReadOnlySpan<byte> pixels,
        int height,
        int comp,
        int strideBytes,
        int x,
        int y,
        bool flipVertically,
        Span<float> yBlock,
        Span<float> uBlock,
        Span<float> vBlock)
    {
#if NET8_0_OR_GREATER
        if (Avx2.IsSupported)
        {
            ConvertMcuRowsAvx2(pixels, height, comp, strideBytes, x, y, 16, flipVertically, yBlock, uBlock, vBlock);
            return;
        }

        if (Sse2.IsSupported)
        {
            ConvertMcuRowsSse2(pixels, height, comp, strideBytes, x, y, 16, flipVertically, yBlock, uBlock, vBlock);
            return;
        }

        if (AdvSimd.Arm64.IsSupported)
        {
            ConvertMcuRowsArm64(pixels, height, comp, strideBytes, x, y, 16, flipVertically, yBlock, uBlock, vBlock);
            return;
        }
#endif
        ConvertMcuInteriorScalar(pixels, height, comp, strideBytes, x, y, 16, flipVertically, yBlock, uBlock, vBlock);
    }

    private static void ConvertMcu8Interior(
        ReadOnlySpan<byte> pixels,
        int height,
        int comp,
        int strideBytes,
        int x,
        int y,
        bool flipVertically,
        Span<float> yBlock,
        Span<float> uBlock,
        Span<float> vBlock)
    {
#if NET8_0_OR_GREATER
        if (Avx2.IsSupported)
        {
            ConvertMcuRowsAvx2(pixels, height, comp, strideBytes, x, y, 8, flipVertically, yBlock, uBlock, vBlock);
            return;
        }

        if (Sse2.IsSupported)
        {
            ConvertMcuRowsSse2(pixels, height, comp, strideBytes, x, y, 8, flipVertically, yBlock, uBlock, vBlock);
            return;
        }

        if (AdvSimd.Arm64.IsSupported)
        {
            ConvertMcuRowsArm64(pixels, height, comp, strideBytes, x, y, 8, flipVertically, yBlock, uBlock, vBlock);
            return;
        }
#endif
        ConvertMcuInteriorScalar(pixels, height, comp, strideBytes, x, y, 8, flipVertically, yBlock, uBlock, vBlock);
    }

    private static void ConvertMcuInteriorScalar(
        ReadOnlySpan<byte> pixels,
        int height,
        int comp,
        int strideBytes,
        int x,
        int y,
        int size,
        bool flipVertically,
        Span<float> yBlock,
        Span<float> uBlock,
        Span<float> vBlock)
    {
        int ofsG = comp > 2 ? 1 : 0;
        int ofsB = comp > 2 ? 2 : 0;
        int pos = 0;
        for (int row = 0; row < size; row++)
        {
            int srcRow = flipVertically ? height - 1 - (y + row) : y + row;
            int p = (srcRow * strideBytes) + (x * comp);
            for (int col = 0; col < size; col++, pos++, p += comp)
            {
                float r = pixels[p];
                float g = pixels[p + ofsG];
                float b = pixels[p + ofsB];
                yBlock[pos] = (((+0.29900f * r) + (0.58700f * g)) + (0.11400f * b)) - 128f;
                uBlock[pos] = ((-0.16874f * r) - (0.33126f * g)) + (0.50000f * b);
                vBlock[pos] = ((+0.50000f * r) - (0.41869f * g)) - (0.08131f * b);
            }
        }
    }

#if NET8_0_OR_GREATER
    private static void ConvertMcuRowsAvx2(
        ReadOnlySpan<byte> pixels,
        int height,
        int comp,
        int strideBytes,
        int x,
        int y,
        int size,
        bool flipVertically,
        Span<float> yBlock,
        Span<float> uBlock,
        Span<float> vBlock)
    {
        Vector256<float> cYR = Vector256.Create(0.29900f);
        Vector256<float> cYG = Vector256.Create(0.58700f);
        Vector256<float> cYB = Vector256.Create(0.11400f);
        Vector256<float> c128 = Vector256.Create(128f);

        Vector256<float> cUR = Vector256.Create(-0.16874f);
        Vector256<float> cUG = Vector256.Create(0.33126f);
        Vector256<float> cUB = Vector256.Create(0.50000f);

        Vector256<float> cVR = Vector256.Create(0.50000f);
        Vector256<float> cVG = Vector256.Create(0.41869f);
        Vector256<float> cVB = Vector256.Create(0.08131f);

        Vector256<uint> maskFF = Vector256.Create(0xFFu);

        Vector256<byte> rgbMaskR = Vector256.Create(
            (byte)0, 0x80, 0x80, 0x80, 3, 0x80, 0x80, 0x80, 6, 0x80, 0x80, 0x80, 9, 0x80, 0x80, 0x80,
            4, 0x80, 0x80, 0x80, 7, 0x80, 0x80, 0x80, 10, 0x80, 0x80, 0x80, 13, 0x80, 0x80, 0x80);
        Vector256<byte> rgbMaskG = Vector256.Create(
            (byte)1, 0x80, 0x80, 0x80, 4, 0x80, 0x80, 0x80, 7, 0x80, 0x80, 0x80, 10, 0x80, 0x80, 0x80,
            5, 0x80, 0x80, 0x80, 8, 0x80, 0x80, 0x80, 11, 0x80, 0x80, 0x80, 14, 0x80, 0x80, 0x80);
        Vector256<byte> rgbMaskB = Vector256.Create(
            (byte)2, 0x80, 0x80, 0x80, 5, 0x80, 0x80, 0x80, 8, 0x80, 0x80, 0x80, 11, 0x80, 0x80, 0x80,
            6, 0x80, 0x80, 0x80, 9, 0x80, 0x80, 0x80, 12, 0x80, 0x80, 0x80, 15, 0x80, 0x80, 0x80);

        for (int row = 0; row < size; row++)
        {
            int srcRow = flipVertically ? height - 1 - (y + row) : y + row;
            int rowBase = (srcRow * strideBytes) + (x * comp);
            int dstRow = row * size;

            for (int col = 0; col < size; col += 8)
            {
                int p = rowBase + (col * comp);
                Vector256<float> r;
                Vector256<float> g;
                Vector256<float> b;

                if (comp == 4)
                {
                    Vector256<uint> v = Vector256.Create(pixels.Slice(p, 32)).AsUInt32();
                    r = Avx.ConvertToVector256Single(Avx2.And(v, maskFF).AsInt32());
                    g = Avx.ConvertToVector256Single(Avx2.And(Avx2.ShiftRightLogical(v, 8), maskFF).AsInt32());
                    b = Avx.ConvertToVector256Single(Avx2.And(Avx2.ShiftRightLogical(v, 16), maskFF).AsInt32());
                }
                else if (comp == 3)
                {
                    Vector128<byte> vLo = Vector128.Create(pixels.Slice(p, 16));
                    Vector128<byte> vHi = Vector128.Create(pixels.Slice(p + 8, 16));
                    Vector256<byte> v = Vector256.Create(vLo, vHi);
                    r = Avx.ConvertToVector256Single(Avx2.Shuffle(v, rgbMaskR).AsInt32());
                    g = Avx.ConvertToVector256Single(Avx2.Shuffle(v, rgbMaskG).AsInt32());
                    b = Avx.ConvertToVector256Single(Avx2.Shuffle(v, rgbMaskB).AsInt32());
                }
                else if (comp == 2)
                {
                    Vector128<ushort> v16 = Vector128.Create(pixels.Slice(p, 16)).AsUInt16();
                    Vector256<int> v32 = Avx2.And(Avx2.ConvertToVector256Int32(v16), maskFF.AsInt32());
                    r = g = b = Avx.ConvertToVector256Single(v32);
                }
                else
                {
                    // comp == 1: read 8 bytes safely in one 64-bit load
                    Vector128<byte> v8 = Vector128.CreateScalar(BinaryPrimitives.ReadUInt64LittleEndian(pixels.Slice(p, 8))).AsByte();
                    r = g = b = Avx.ConvertToVector256Single(Avx2.ConvertToVector256Int32(v8));
                }

                // Exact left-associative float arithmetic (no FMA) for 0-ULP parity with scalar C.
                Vector256<float> vy = Avx.Subtract(
                    Avx.Add(
                        Avx.Add(Avx.Multiply(cYR, r), Avx.Multiply(cYG, g)),
                        Avx.Multiply(cYB, b)),
                    c128);
                Vector256<float> vu = Avx.Add(
                    Avx.Subtract(Avx.Multiply(cUR, r), Avx.Multiply(cUG, g)),
                    Avx.Multiply(cUB, b));
                Vector256<float> vv = Avx.Subtract(
                    Avx.Subtract(Avx.Multiply(cVR, r), Avx.Multiply(cVG, g)),
                    Avx.Multiply(cVB, b));

                int dstOff = dstRow + col;
                vy.CopyTo(yBlock.Slice(dstOff, 8));
                vu.CopyTo(uBlock.Slice(dstOff, 8));
                vv.CopyTo(vBlock.Slice(dstOff, 8));
            }
        }
    }

    private static void ConvertMcuRowsSse2(
        ReadOnlySpan<byte> pixels,
        int height,
        int comp,
        int strideBytes,
        int x,
        int y,
        int size,
        bool flipVertically,
        Span<float> yBlock,
        Span<float> uBlock,
        Span<float> vBlock)
    {
        Vector128<float> cYR = Vector128.Create(0.29900f);
        Vector128<float> cYG = Vector128.Create(0.58700f);
        Vector128<float> cYB = Vector128.Create(0.11400f);
        Vector128<float> c128 = Vector128.Create(128f);

        Vector128<float> cUR = Vector128.Create(-0.16874f);
        Vector128<float> cUG = Vector128.Create(0.33126f);
        Vector128<float> cUB = Vector128.Create(0.50000f);

        Vector128<float> cVR = Vector128.Create(0.50000f);
        Vector128<float> cVG = Vector128.Create(0.41869f);
        Vector128<float> cVB = Vector128.Create(0.08131f);

        Vector128<uint> maskFF = Vector128.Create(0xFFu);

        for (int row = 0; row < size; row++)
        {
            int srcRow = flipVertically ? height - 1 - (y + row) : y + row;
            int rowBase = (srcRow * strideBytes) + (x * comp);
            int dstRow = row * size;

            for (int col = 0; col < size; col += 4)
            {
                int p = rowBase + (col * comp);
                Vector128<float> r;
                Vector128<float> g;
                Vector128<float> b;

                if (comp == 4)
                {
                    Vector128<uint> v = Vector128.Create(pixels.Slice(p, 16)).AsUInt32();
                    r = Sse2.ConvertToVector128Single(Sse2.And(v, maskFF).AsInt32());
                    g = Sse2.ConvertToVector128Single(Sse2.And(Sse2.ShiftRightLogical(v, 8), maskFF).AsInt32());
                    b = Sse2.ConvertToVector128Single(Sse2.And(Sse2.ShiftRightLogical(v, 16), maskFF).AsInt32());
                }
                else if (comp == 3)
                {
                    r = Vector128.Create((float)pixels[p], pixels[p + 3], pixels[p + 6], pixels[p + 9]);
                    g = Vector128.Create((float)pixels[p + 1], pixels[p + 4], pixels[p + 7], pixels[p + 10]);
                    b = Vector128.Create((float)pixels[p + 2], pixels[p + 5], pixels[p + 8], pixels[p + 11]);
                }
                else if (comp == 2)
                {
                    r = g = b = Vector128.Create((float)pixels[p], pixels[p + 2], pixels[p + 4], pixels[p + 6]);
                }
                else
                {
                    r = g = b = Vector128.Create((float)pixels[p], pixels[p + 1], pixels[p + 2], pixels[p + 3]);
                }

                Vector128<float> vy = Sse.Subtract(
                    Sse.Add(
                        Sse.Add(Sse.Multiply(cYR, r), Sse.Multiply(cYG, g)),
                        Sse.Multiply(cYB, b)),
                    c128);
                Vector128<float> vu = Sse.Add(
                    Sse.Subtract(Sse.Multiply(cUR, r), Sse.Multiply(cUG, g)),
                    Sse.Multiply(cUB, b));
                Vector128<float> vv = Sse.Subtract(
                    Sse.Subtract(Sse.Multiply(cVR, r), Sse.Multiply(cVG, g)),
                    Sse.Multiply(cVB, b));

                int dstOff = dstRow + col;
                vy.CopyTo(yBlock.Slice(dstOff, 4));
                vu.CopyTo(uBlock.Slice(dstOff, 4));
                vv.CopyTo(vBlock.Slice(dstOff, 4));
            }
        }
    }

    private static void ConvertMcuRowsArm64(
        ReadOnlySpan<byte> pixels,
        int height,
        int comp,
        int strideBytes,
        int x,
        int y,
        int size,
        bool flipVertically,
        Span<float> yBlock,
        Span<float> uBlock,
        Span<float> vBlock)
    {
        Vector128<float> cYR = Vector128.Create(0.29900f);
        Vector128<float> cYG = Vector128.Create(0.58700f);
        Vector128<float> cYB = Vector128.Create(0.11400f);
        Vector128<float> c128 = Vector128.Create(128f);

        Vector128<float> cUR = Vector128.Create(-0.16874f);
        Vector128<float> cUG = Vector128.Create(0.33126f);
        Vector128<float> cUB = Vector128.Create(0.50000f);

        Vector128<float> cVR = Vector128.Create(0.50000f);
        Vector128<float> cVG = Vector128.Create(0.41869f);
        Vector128<float> cVB = Vector128.Create(0.08131f);

        Vector128<uint> maskFF = Vector128.Create(0xFFu);

        for (int row = 0; row < size; row++)
        {
            int srcRow = flipVertically ? height - 1 - (y + row) : y + row;
            int rowBase = (srcRow * strideBytes) + (x * comp);
            int dstRow = row * size;

            for (int col = 0; col < size; col += 4)
            {
                int p = rowBase + (col * comp);
                Vector128<float> r;
                Vector128<float> g;
                Vector128<float> b;

                if (comp == 4)
                {
                    Vector128<uint> v = Vector128.Create(pixels.Slice(p, 16)).AsUInt32();
                    r = AdvSimd.ConvertToSingle(AdvSimd.And(v, maskFF));
                    g = AdvSimd.ConvertToSingle(AdvSimd.And(AdvSimd.ShiftRightLogical(v, 8), maskFF));
                    b = AdvSimd.ConvertToSingle(AdvSimd.And(AdvSimd.ShiftRightLogical(v, 16), maskFF));
                }
                else if (comp == 3)
                {
                    r = Vector128.Create((float)pixels[p], pixels[p + 3], pixels[p + 6], pixels[p + 9]);
                    g = Vector128.Create((float)pixels[p + 1], pixels[p + 4], pixels[p + 7], pixels[p + 10]);
                    b = Vector128.Create((float)pixels[p + 2], pixels[p + 5], pixels[p + 8], pixels[p + 11]);
                }
                else if (comp == 2)
                {
                    r = g = b = Vector128.Create((float)pixels[p], pixels[p + 2], pixels[p + 4], pixels[p + 6]);
                }
                else
                {
                    r = g = b = Vector128.Create((float)pixels[p], pixels[p + 1], pixels[p + 2], pixels[p + 3]);
                }

                Vector128<float> vy = AdvSimd.Subtract(
                    AdvSimd.Add(
                        AdvSimd.Add(AdvSimd.Multiply(cYR, r), AdvSimd.Multiply(cYG, g)),
                        AdvSimd.Multiply(cYB, b)),
                    c128);
                Vector128<float> vu = AdvSimd.Add(
                    AdvSimd.Subtract(AdvSimd.Multiply(cUR, r), AdvSimd.Multiply(cUG, g)),
                    AdvSimd.Multiply(cUB, b));
                Vector128<float> vv = AdvSimd.Subtract(
                    AdvSimd.Subtract(AdvSimd.Multiply(cVR, r), AdvSimd.Multiply(cVG, g)),
                    AdvSimd.Multiply(cVB, b));

                int dstOff = dstRow + col;
                vy.CopyTo(yBlock.Slice(dstOff, 4));
                vu.CopyTo(uBlock.Slice(dstOff, 4));
                vv.CopyTo(vBlock.Slice(dstOff, 4));
            }
        }
    }
#endif

    // =========================================================================
    // 2x2 Chroma Subsampling (exact left-associative float order)
    // =========================================================================

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static void Subsample2x2(ReadOnlySpan<float> src16x16, Span<float> dst8x8)
    {
#if NET8_0_OR_GREATER
        if (Avx2.IsSupported)
        {
            Vector256<float> q = Vector256.Create(0.25f);
            for (int yy = 0; yy < 8; yy++)
            {
                int row0 = yy * 32;
                int row1 = row0 + 16;

                Vector256<float> r0a = Vector256.Create(src16x16.Slice(row0, 8));
                Vector256<float> r0b = Vector256.Create(src16x16.Slice(row0 + 8, 8));
                Vector256<float> r1a = Vector256.Create(src16x16.Slice(row1, 8));
                Vector256<float> r1b = Vector256.Create(src16x16.Slice(row1 + 8, 8));

                Vector256<float> ev0 = Avx.Shuffle(r0a, r0b, 0x88);
                Vector256<float> od0 = Avx.Shuffle(r0a, r0b, 0xDD);
                Vector256<float> ev1 = Avx.Shuffle(r1a, r1b, 0x88);
                Vector256<float> od1 = Avx.Shuffle(r1a, r1b, 0xDD);

                // Exact C order: (((src[j] + src[j+1]) + src[j+16]) + src[j+17]) * 0.25f
                Vector256<float> avg = Avx.Multiply(Avx.Add(Avx.Add(Avx.Add(ev0, od0), ev1), od1), q);
                Avx2.Permute4x64(avg.AsUInt64(), 0xD8).AsSingle().CopyTo(dst8x8.Slice(yy * 8, 8));
            }

            return;
        }

        if (Sse.IsSupported)
        {
            Vector128<float> q = Vector128.Create(0.25f);
            for (int yy = 0; yy < 8; yy++)
            {
                int row0 = yy * 32;
                int row1 = row0 + 16;

                for (int half = 0; half < 2; half++)
                {
                    int hOff = half * 8;
                    Vector128<float> r0a = Vector128.Create(src16x16.Slice(row0 + hOff, 4));
                    Vector128<float> r0b = Vector128.Create(src16x16.Slice(row0 + hOff + 4, 4));
                    Vector128<float> r1a = Vector128.Create(src16x16.Slice(row1 + hOff, 4));
                    Vector128<float> r1b = Vector128.Create(src16x16.Slice(row1 + hOff + 4, 4));

                    Vector128<float> ev0 = Sse.Shuffle(r0a, r0b, 0x88);
                    Vector128<float> od0 = Sse.Shuffle(r0a, r0b, 0xDD);
                    Vector128<float> ev1 = Sse.Shuffle(r1a, r1b, 0x88);
                    Vector128<float> od1 = Sse.Shuffle(r1a, r1b, 0xDD);

                    Vector128<float> avg = Sse.Multiply(Sse.Add(Sse.Add(Sse.Add(ev0, od0), ev1), od1), q);
                    avg.CopyTo(dst8x8.Slice((yy * 8) + (half * 4), 4));
                }
            }

            return;
        }

        if (AdvSimd.Arm64.IsSupported)
        {
            Vector128<float> q = Vector128.Create(0.25f);
            for (int yy = 0; yy < 8; yy++)
            {
                int row0 = yy * 32;
                int row1 = row0 + 16;

                for (int half = 0; half < 2; half++)
                {
                    int hOff = half * 8;
                    Vector128<float> r0a = Vector128.Create(src16x16.Slice(row0 + hOff, 4));
                    Vector128<float> r0b = Vector128.Create(src16x16.Slice(row0 + hOff + 4, 4));
                    Vector128<float> r1a = Vector128.Create(src16x16.Slice(row1 + hOff, 4));
                    Vector128<float> r1b = Vector128.Create(src16x16.Slice(row1 + hOff + 4, 4));

                    Vector128<float> ev0 = AdvSimd.Arm64.UnzipEven(r0a, r0b);
                    Vector128<float> od0 = AdvSimd.Arm64.UnzipOdd(r0a, r0b);
                    Vector128<float> ev1 = AdvSimd.Arm64.UnzipEven(r1a, r1b);
                    Vector128<float> od1 = AdvSimd.Arm64.UnzipOdd(r1a, r1b);

                    Vector128<float> avg = AdvSimd.Multiply(AdvSimd.Add(AdvSimd.Add(AdvSimd.Add(ev0, od0), ev1), od1), q);
                    avg.CopyTo(dst8x8.Slice((yy * 8) + (half * 4), 4));
                }
            }

            return;
        }
#endif
        int pos = 0;
        for (int yy = 0; yy < 8; yy++)
        {
            int rowBase = yy * 32;
            for (int xx = 0; xx < 8; xx++, pos++)
            {
                int j = rowBase + (xx * 2);
                dst8x8[pos] = (((src16x16[j] + src16x16[j + 1]) + src16x16[j + 16]) + src16x16[j + 17]) * 0.25f;
            }
        }
    }

    // =========================================================================
    // 8x8 AAN Forward DCT + Quantize + ZigZag (Avx / Sse2 / AdvSimd / Scalar)
    // Returns last non-zero AC index in zigzag order (0..63).
    // =========================================================================

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static int DctAndQuantize(
        scoped Span<float> cdu,
        int duStride,
        scoped ReadOnlySpan<float> fdtbl,
        scoped Span<short> du)
    {
#if NET8_0_OR_GREATER
        if (Avx.IsSupported)
        {
            return DctAndQuantizeAvx(cdu, duStride, fdtbl, du);
        }

        if (Sse2.IsSupported)
        {
            return DctAndQuantizeSse2(cdu, duStride, fdtbl, du);
        }

        if (AdvSimd.Arm64.IsSupported)
        {
            return DctAndQuantizeArm64(cdu, duStride, fdtbl, du);
        }
#endif
        return DctAndQuantizeScalar(cdu, duStride, fdtbl, du);
    }

    internal static int DctAndQuantizeScalar(
        scoped Span<float> cdu,
        int duStride,
        scoped ReadOnlySpan<float> fdtbl,
        scoped Span<short> du)
    {
        for (int dataOff = 0, n = duStride * 8; dataOff < n; dataOff += duStride)
        {
            Dct1DScalar(
                ref cdu[dataOff],
                ref cdu[dataOff + 1],
                ref cdu[dataOff + 2],
                ref cdu[dataOff + 3],
                ref cdu[dataOff + 4],
                ref cdu[dataOff + 5],
                ref cdu[dataOff + 6],
                ref cdu[dataOff + 7]);
        }

        for (int dataOff = 0; dataOff < 8; dataOff++)
        {
            Dct1DScalar(
                ref cdu[dataOff],
                ref cdu[dataOff + duStride],
                ref cdu[dataOff + (duStride * 2)],
                ref cdu[dataOff + (duStride * 3)],
                ref cdu[dataOff + (duStride * 4)],
                ref cdu[dataOff + (duStride * 5)],
                ref cdu[dataOff + (duStride * 6)],
                ref cdu[dataOff + (duStride * 7)]);
        }

        ReadOnlySpan<byte> zigZag = ZigZag;
        for (int y = 0, j = 0; y < 8; y++)
        {
            int rowOff = y * duStride;
            for (int x = 0; x < 8; x++, j++)
            {
                float v = cdu[rowOff + x] * fdtbl[j];
                du[zigZag[j]] = (short)(int)(v < 0f ? v - 0.5f : v + 0.5f);
            }
        }

        int end0pos = 63;
        while (end0pos > 0 && du[end0pos] == 0)
        {
            end0pos--;
        }

        return end0pos;
    }

#if NET8_0_OR_GREATER
    private static int DctAndQuantizeAvx(
        scoped ReadOnlySpan<float> cdu,
        int duStride,
        scoped ReadOnlySpan<float> fdtbl,
        scoped Span<short> du)
    {
        Vector256<float> r0 = Vector256.Create(cdu.Slice(0, 8));
        Vector256<float> r1 = Vector256.Create(cdu.Slice(duStride, 8));
        Vector256<float> r2 = Vector256.Create(cdu.Slice(duStride * 2, 8));
        Vector256<float> r3 = Vector256.Create(cdu.Slice(duStride * 3, 8));
        Vector256<float> r4 = Vector256.Create(cdu.Slice(duStride * 4, 8));
        Vector256<float> r5 = Vector256.Create(cdu.Slice(duStride * 5, 8));
        Vector256<float> r6 = Vector256.Create(cdu.Slice(duStride * 6, 8));
        Vector256<float> r7 = Vector256.Create(cdu.Slice(duStride * 7, 8));

        // Transpose -> row DCT -> transpose -> column DCT
        Transpose8x8Avx(ref r0, ref r1, ref r2, ref r3, ref r4, ref r5, ref r6, ref r7);
        Dct1DAvx(ref r0, ref r1, ref r2, ref r3, ref r4, ref r5, ref r6, ref r7);
        Transpose8x8Avx(ref r0, ref r1, ref r2, ref r3, ref r4, ref r5, ref r6, ref r7);
        Dct1DAvx(ref r0, ref r1, ref r2, ref r3, ref r4, ref r5, ref r6, ref r7);

        // Quantize and round to nearest (ties away from zero, identical to v < 0 ? v - 0.5f : v + 0.5f)
        Vector256<float> half = Vector256.Create(0.5f);
        Vector256<float> signMask = Vector256.Create(-0.0f);

        Span<int> q = stackalloc int[64];
        QuantizeRowAvx(r0, Vector256.Create(fdtbl.Slice(0, 8)), half, signMask).CopyTo(q.Slice(0, 8));
        QuantizeRowAvx(r1, Vector256.Create(fdtbl.Slice(8, 8)), half, signMask).CopyTo(q.Slice(8, 8));
        QuantizeRowAvx(r2, Vector256.Create(fdtbl.Slice(16, 8)), half, signMask).CopyTo(q.Slice(16, 8));
        QuantizeRowAvx(r3, Vector256.Create(fdtbl.Slice(24, 8)), half, signMask).CopyTo(q.Slice(24, 8));
        QuantizeRowAvx(r4, Vector256.Create(fdtbl.Slice(32, 8)), half, signMask).CopyTo(q.Slice(32, 8));
        QuantizeRowAvx(r5, Vector256.Create(fdtbl.Slice(40, 8)), half, signMask).CopyTo(q.Slice(40, 8));
        QuantizeRowAvx(r6, Vector256.Create(fdtbl.Slice(48, 8)), half, signMask).CopyTo(q.Slice(48, 8));
        QuantizeRowAvx(r7, Vector256.Create(fdtbl.Slice(56, 8)), half, signMask).CopyTo(q.Slice(56, 8));

        return ScatterZigZagAndFindEnd(q, du);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector256<int> QuantizeRowAvx(
        Vector256<float> row,
        Vector256<float> fdtblRow,
        Vector256<float> half,
        Vector256<float> signMask)
    {
        Vector256<float> v = Avx.Multiply(row, fdtblRow);
        Vector256<float> signedHalf = Avx.Or(half, Avx.And(v, signMask));
        return Avx.ConvertToVector256Int32WithTruncation(Avx.Add(v, signedHalf));
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void Transpose8x8Avx(
        ref Vector256<float> r0,
        ref Vector256<float> r1,
        ref Vector256<float> r2,
        ref Vector256<float> r3,
        ref Vector256<float> r4,
        ref Vector256<float> r5,
        ref Vector256<float> r6,
        ref Vector256<float> r7)
    {
        Vector256<float> t0 = Avx.UnpackLow(r0, r1);
        Vector256<float> t1 = Avx.UnpackHigh(r0, r1);
        Vector256<float> t2 = Avx.UnpackLow(r2, r3);
        Vector256<float> t3 = Avx.UnpackHigh(r2, r3);
        Vector256<float> t4 = Avx.UnpackLow(r4, r5);
        Vector256<float> t5 = Avx.UnpackHigh(r4, r5);
        Vector256<float> t6 = Avx.UnpackLow(r6, r7);
        Vector256<float> t7 = Avx.UnpackHigh(r6, r7);

        Vector256<float> s0 = Avx.Shuffle(t0, t2, 0x44);
        Vector256<float> s1 = Avx.Shuffle(t0, t2, 0xEE);
        Vector256<float> s2 = Avx.Shuffle(t1, t3, 0x44);
        Vector256<float> s3 = Avx.Shuffle(t1, t3, 0xEE);
        Vector256<float> s4 = Avx.Shuffle(t4, t6, 0x44);
        Vector256<float> s5 = Avx.Shuffle(t4, t6, 0xEE);
        Vector256<float> s6 = Avx.Shuffle(t5, t7, 0x44);
        Vector256<float> s7 = Avx.Shuffle(t5, t7, 0xEE);

        r0 = Avx.Permute2x128(s0, s4, 0x20);
        r1 = Avx.Permute2x128(s1, s5, 0x20);
        r2 = Avx.Permute2x128(s2, s6, 0x20);
        r3 = Avx.Permute2x128(s3, s7, 0x20);
        r4 = Avx.Permute2x128(s0, s4, 0x31);
        r5 = Avx.Permute2x128(s1, s5, 0x31);
        r6 = Avx.Permute2x128(s2, s6, 0x31);
        r7 = Avx.Permute2x128(s3, s7, 0x31);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void Dct1DAvx(
        ref Vector256<float> d0,
        ref Vector256<float> d1,
        ref Vector256<float> d2,
        ref Vector256<float> d3,
        ref Vector256<float> d4,
        ref Vector256<float> d5,
        ref Vector256<float> d6,
        ref Vector256<float> d7)
    {
        Vector256<float> tmp0 = Avx.Add(d0, d7);
        Vector256<float> tmp7 = Avx.Subtract(d0, d7);
        Vector256<float> tmp1 = Avx.Add(d1, d6);
        Vector256<float> tmp6 = Avx.Subtract(d1, d6);
        Vector256<float> tmp2 = Avx.Add(d2, d5);
        Vector256<float> tmp5 = Avx.Subtract(d2, d5);
        Vector256<float> tmp3 = Avx.Add(d3, d4);
        Vector256<float> tmp4 = Avx.Subtract(d3, d4);

        Vector256<float> tmp10 = Avx.Add(tmp0, tmp3);
        Vector256<float> tmp13 = Avx.Subtract(tmp0, tmp3);
        Vector256<float> tmp11 = Avx.Add(tmp1, tmp2);
        Vector256<float> tmp12 = Avx.Subtract(tmp1, tmp2);

        d0 = Avx.Add(tmp10, tmp11);
        d4 = Avx.Subtract(tmp10, tmp11);

        Vector256<float> c0707 = Vector256.Create(0.707106781f);
        Vector256<float> z1 = Avx.Multiply(Avx.Add(tmp12, tmp13), c0707);
        d2 = Avx.Add(tmp13, z1);
        d6 = Avx.Subtract(tmp13, z1);

        tmp10 = Avx.Add(tmp4, tmp5);
        tmp11 = Avx.Add(tmp5, tmp6);
        tmp12 = Avx.Add(tmp6, tmp7);

        Vector256<float> z5 = Avx.Multiply(Avx.Subtract(tmp10, tmp12), Vector256.Create(0.382683433f));
        Vector256<float> z2 = Avx.Add(Avx.Multiply(tmp10, Vector256.Create(0.541196100f)), z5);
        Vector256<float> z4 = Avx.Add(Avx.Multiply(tmp12, Vector256.Create(1.306562965f)), z5);
        Vector256<float> z3 = Avx.Multiply(tmp11, c0707);

        Vector256<float> z11 = Avx.Add(tmp7, z3);
        Vector256<float> z13 = Avx.Subtract(tmp7, z3);

        d5 = Avx.Add(z13, z2);
        d3 = Avx.Subtract(z13, z2);
        d1 = Avx.Add(z11, z4);
        d7 = Avx.Subtract(z11, z4);
    }

    private static int DctAndQuantizeSse2(
        scoped ReadOnlySpan<float> cdu,
        int duStride,
        scoped ReadOnlySpan<float> fdtbl,
        scoped Span<short> du)
    {
        // Top half (rows 0..3): TL = cols 0..3, TR = cols 4..7
        Vector128<float> tl0 = Vector128.Create(cdu.Slice(0, 4));
        Vector128<float> tl1 = Vector128.Create(cdu.Slice(duStride, 4));
        Vector128<float> tl2 = Vector128.Create(cdu.Slice(duStride * 2, 4));
        Vector128<float> tl3 = Vector128.Create(cdu.Slice(duStride * 3, 4));

        Vector128<float> tr0 = Vector128.Create(cdu.Slice(4, 4));
        Vector128<float> tr1 = Vector128.Create(cdu.Slice(duStride + 4, 4));
        Vector128<float> tr2 = Vector128.Create(cdu.Slice((duStride * 2) + 4, 4));
        Vector128<float> tr3 = Vector128.Create(cdu.Slice((duStride * 3) + 4, 4));

        Transpose4x4Sse(ref tl0, ref tl1, ref tl2, ref tl3);
        Transpose4x4Sse(ref tr0, ref tr1, ref tr2, ref tr3);
        Dct1DSse(ref tl0, ref tl1, ref tl2, ref tl3, ref tr0, ref tr1, ref tr2, ref tr3);
        Transpose4x4Sse(ref tl0, ref tl1, ref tl2, ref tl3);
        Transpose4x4Sse(ref tr0, ref tr1, ref tr2, ref tr3);

        // Bottom half (rows 4..7): BL = cols 0..3, BR = cols 4..7
        Vector128<float> bl0 = Vector128.Create(cdu.Slice(duStride * 4, 4));
        Vector128<float> bl1 = Vector128.Create(cdu.Slice(duStride * 5, 4));
        Vector128<float> bl2 = Vector128.Create(cdu.Slice(duStride * 6, 4));
        Vector128<float> bl3 = Vector128.Create(cdu.Slice(duStride * 7, 4));

        Vector128<float> br0 = Vector128.Create(cdu.Slice((duStride * 4) + 4, 4));
        Vector128<float> br1 = Vector128.Create(cdu.Slice((duStride * 5) + 4, 4));
        Vector128<float> br2 = Vector128.Create(cdu.Slice((duStride * 6) + 4, 4));
        Vector128<float> br3 = Vector128.Create(cdu.Slice((duStride * 7) + 4, 4));

        Transpose4x4Sse(ref bl0, ref bl1, ref bl2, ref bl3);
        Transpose4x4Sse(ref br0, ref br1, ref br2, ref br3);
        Dct1DSse(ref bl0, ref bl1, ref bl2, ref bl3, ref br0, ref br1, ref br2, ref br3);
        Transpose4x4Sse(ref bl0, ref bl1, ref bl2, ref bl3);
        Transpose4x4Sse(ref br0, ref br1, ref br2, ref br3);

        // Column DCT (left 4 cols, then right 4 cols)
        Dct1DSse(ref tl0, ref tl1, ref tl2, ref tl3, ref bl0, ref bl1, ref bl2, ref bl3);
        Dct1DSse(ref tr0, ref tr1, ref tr2, ref tr3, ref br0, ref br1, ref br2, ref br3);

        Vector128<float> half = Vector128.Create(0.5f);
        Vector128<float> signMask = Vector128.Create(-0.0f);

        Span<int> q = stackalloc int[64];
        Quantize4Sse2(tl0, Vector128.Create(fdtbl.Slice(0, 4)), half, signMask).CopyTo(q.Slice(0, 4));
        Quantize4Sse2(tr0, Vector128.Create(fdtbl.Slice(4, 4)), half, signMask).CopyTo(q.Slice(4, 4));
        Quantize4Sse2(tl1, Vector128.Create(fdtbl.Slice(8, 4)), half, signMask).CopyTo(q.Slice(8, 4));
        Quantize4Sse2(tr1, Vector128.Create(fdtbl.Slice(12, 4)), half, signMask).CopyTo(q.Slice(12, 4));
        Quantize4Sse2(tl2, Vector128.Create(fdtbl.Slice(16, 4)), half, signMask).CopyTo(q.Slice(16, 4));
        Quantize4Sse2(tr2, Vector128.Create(fdtbl.Slice(20, 4)), half, signMask).CopyTo(q.Slice(20, 4));
        Quantize4Sse2(tl3, Vector128.Create(fdtbl.Slice(24, 4)), half, signMask).CopyTo(q.Slice(24, 4));
        Quantize4Sse2(tr3, Vector128.Create(fdtbl.Slice(28, 4)), half, signMask).CopyTo(q.Slice(28, 4));
        Quantize4Sse2(bl0, Vector128.Create(fdtbl.Slice(32, 4)), half, signMask).CopyTo(q.Slice(32, 4));
        Quantize4Sse2(br0, Vector128.Create(fdtbl.Slice(36, 4)), half, signMask).CopyTo(q.Slice(36, 4));
        Quantize4Sse2(bl1, Vector128.Create(fdtbl.Slice(40, 4)), half, signMask).CopyTo(q.Slice(40, 4));
        Quantize4Sse2(br1, Vector128.Create(fdtbl.Slice(44, 4)), half, signMask).CopyTo(q.Slice(44, 4));
        Quantize4Sse2(bl2, Vector128.Create(fdtbl.Slice(48, 4)), half, signMask).CopyTo(q.Slice(48, 4));
        Quantize4Sse2(br2, Vector128.Create(fdtbl.Slice(52, 4)), half, signMask).CopyTo(q.Slice(52, 4));
        Quantize4Sse2(bl3, Vector128.Create(fdtbl.Slice(56, 4)), half, signMask).CopyTo(q.Slice(56, 4));
        Quantize4Sse2(br3, Vector128.Create(fdtbl.Slice(60, 4)), half, signMask).CopyTo(q.Slice(60, 4));

        return ScatterZigZagAndFindEnd(q, du);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector128<int> Quantize4Sse2(
        Vector128<float> row,
        Vector128<float> fdtblRow,
        Vector128<float> half,
        Vector128<float> signMask)
    {
        Vector128<float> v = Sse.Multiply(row, fdtblRow);
        Vector128<float> signedHalf = Sse.Or(half, Sse.And(v, signMask));
        return Sse2.ConvertToVector128Int32WithTruncation(Sse.Add(v, signedHalf));
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void Transpose4x4Sse(
        ref Vector128<float> r0,
        ref Vector128<float> r1,
        ref Vector128<float> r2,
        ref Vector128<float> r3)
    {
        Vector128<float> t0 = Sse.UnpackLow(r0, r1);
        Vector128<float> t1 = Sse.UnpackHigh(r0, r1);
        Vector128<float> t2 = Sse.UnpackLow(r2, r3);
        Vector128<float> t3 = Sse.UnpackHigh(r2, r3);

        r0 = Sse.Shuffle(t0, t2, 0x44);
        r1 = Sse.Shuffle(t0, t2, 0xEE);
        r2 = Sse.Shuffle(t1, t3, 0x44);
        r3 = Sse.Shuffle(t1, t3, 0xEE);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void Dct1DSse(
        ref Vector128<float> d0,
        ref Vector128<float> d1,
        ref Vector128<float> d2,
        ref Vector128<float> d3,
        ref Vector128<float> d4,
        ref Vector128<float> d5,
        ref Vector128<float> d6,
        ref Vector128<float> d7)
    {
        Vector128<float> tmp0 = Sse.Add(d0, d7);
        Vector128<float> tmp7 = Sse.Subtract(d0, d7);
        Vector128<float> tmp1 = Sse.Add(d1, d6);
        Vector128<float> tmp6 = Sse.Subtract(d1, d6);
        Vector128<float> tmp2 = Sse.Add(d2, d5);
        Vector128<float> tmp5 = Sse.Subtract(d2, d5);
        Vector128<float> tmp3 = Sse.Add(d3, d4);
        Vector128<float> tmp4 = Sse.Subtract(d3, d4);

        Vector128<float> tmp10 = Sse.Add(tmp0, tmp3);
        Vector128<float> tmp13 = Sse.Subtract(tmp0, tmp3);
        Vector128<float> tmp11 = Sse.Add(tmp1, tmp2);
        Vector128<float> tmp12 = Sse.Subtract(tmp1, tmp2);

        d0 = Sse.Add(tmp10, tmp11);
        d4 = Sse.Subtract(tmp10, tmp11);

        Vector128<float> c0707 = Vector128.Create(0.707106781f);
        Vector128<float> z1 = Sse.Multiply(Sse.Add(tmp12, tmp13), c0707);
        d2 = Sse.Add(tmp13, z1);
        d6 = Sse.Subtract(tmp13, z1);

        tmp10 = Sse.Add(tmp4, tmp5);
        tmp11 = Sse.Add(tmp5, tmp6);
        tmp12 = Sse.Add(tmp6, tmp7);

        Vector128<float> z5 = Sse.Multiply(Sse.Subtract(tmp10, tmp12), Vector128.Create(0.382683433f));
        Vector128<float> z2 = Sse.Add(Sse.Multiply(tmp10, Vector128.Create(0.541196100f)), z5);
        Vector128<float> z4 = Sse.Add(Sse.Multiply(tmp12, Vector128.Create(1.306562965f)), z5);
        Vector128<float> z3 = Sse.Multiply(tmp11, c0707);

        Vector128<float> z11 = Sse.Add(tmp7, z3);
        Vector128<float> z13 = Sse.Subtract(tmp7, z3);

        d5 = Sse.Add(z13, z2);
        d3 = Sse.Subtract(z13, z2);
        d1 = Sse.Add(z11, z4);
        d7 = Sse.Subtract(z11, z4);
    }

    private static int DctAndQuantizeArm64(
        scoped ReadOnlySpan<float> cdu,
        int duStride,
        scoped ReadOnlySpan<float> fdtbl,
        scoped Span<short> du)
    {
        Vector128<float> tl0 = Vector128.Create(cdu.Slice(0, 4));
        Vector128<float> tl1 = Vector128.Create(cdu.Slice(duStride, 4));
        Vector128<float> tl2 = Vector128.Create(cdu.Slice(duStride * 2, 4));
        Vector128<float> tl3 = Vector128.Create(cdu.Slice(duStride * 3, 4));

        Vector128<float> tr0 = Vector128.Create(cdu.Slice(4, 4));
        Vector128<float> tr1 = Vector128.Create(cdu.Slice(duStride + 4, 4));
        Vector128<float> tr2 = Vector128.Create(cdu.Slice((duStride * 2) + 4, 4));
        Vector128<float> tr3 = Vector128.Create(cdu.Slice((duStride * 3) + 4, 4));

        Transpose4x4Arm64(ref tl0, ref tl1, ref tl2, ref tl3);
        Transpose4x4Arm64(ref tr0, ref tr1, ref tr2, ref tr3);
        Dct1DArm64(ref tl0, ref tl1, ref tl2, ref tl3, ref tr0, ref tr1, ref tr2, ref tr3);
        Transpose4x4Arm64(ref tl0, ref tl1, ref tl2, ref tl3);
        Transpose4x4Arm64(ref tr0, ref tr1, ref tr2, ref tr3);

        Vector128<float> bl0 = Vector128.Create(cdu.Slice(duStride * 4, 4));
        Vector128<float> bl1 = Vector128.Create(cdu.Slice(duStride * 5, 4));
        Vector128<float> bl2 = Vector128.Create(cdu.Slice(duStride * 6, 4));
        Vector128<float> bl3 = Vector128.Create(cdu.Slice(duStride * 7, 4));

        Vector128<float> br0 = Vector128.Create(cdu.Slice((duStride * 4) + 4, 4));
        Vector128<float> br1 = Vector128.Create(cdu.Slice((duStride * 5) + 4, 4));
        Vector128<float> br2 = Vector128.Create(cdu.Slice((duStride * 6) + 4, 4));
        Vector128<float> br3 = Vector128.Create(cdu.Slice((duStride * 7) + 4, 4));

        Transpose4x4Arm64(ref bl0, ref bl1, ref bl2, ref bl3);
        Transpose4x4Arm64(ref br0, ref br1, ref br2, ref br3);
        Dct1DArm64(ref bl0, ref bl1, ref bl2, ref bl3, ref br0, ref br1, ref br2, ref br3);
        Transpose4x4Arm64(ref bl0, ref bl1, ref bl2, ref bl3);
        Transpose4x4Arm64(ref br0, ref br1, ref br2, ref br3);

        Dct1DArm64(ref tl0, ref tl1, ref tl2, ref tl3, ref bl0, ref bl1, ref bl2, ref bl3);
        Dct1DArm64(ref tr0, ref tr1, ref tr2, ref tr3, ref br0, ref br1, ref br2, ref br3);

        Vector128<uint> halfBits = Vector128.Create(0.5f).AsUInt32();
        Vector128<uint> signMask = Vector128.Create(0x80000000u);

        Span<int> q = stackalloc int[64];
        Quantize4Arm64(tl0, Vector128.Create(fdtbl.Slice(0, 4)), halfBits, signMask).CopyTo(q.Slice(0, 4));
        Quantize4Arm64(tr0, Vector128.Create(fdtbl.Slice(4, 4)), halfBits, signMask).CopyTo(q.Slice(4, 4));
        Quantize4Arm64(tl1, Vector128.Create(fdtbl.Slice(8, 4)), halfBits, signMask).CopyTo(q.Slice(8, 4));
        Quantize4Arm64(tr1, Vector128.Create(fdtbl.Slice(12, 4)), halfBits, signMask).CopyTo(q.Slice(12, 4));
        Quantize4Arm64(tl2, Vector128.Create(fdtbl.Slice(16, 4)), halfBits, signMask).CopyTo(q.Slice(16, 4));
        Quantize4Arm64(tr2, Vector128.Create(fdtbl.Slice(20, 4)), halfBits, signMask).CopyTo(q.Slice(20, 4));
        Quantize4Arm64(tl3, Vector128.Create(fdtbl.Slice(24, 4)), halfBits, signMask).CopyTo(q.Slice(24, 4));
        Quantize4Arm64(tr3, Vector128.Create(fdtbl.Slice(28, 4)), halfBits, signMask).CopyTo(q.Slice(28, 4));
        Quantize4Arm64(bl0, Vector128.Create(fdtbl.Slice(32, 4)), halfBits, signMask).CopyTo(q.Slice(32, 4));
        Quantize4Arm64(br0, Vector128.Create(fdtbl.Slice(36, 4)), halfBits, signMask).CopyTo(q.Slice(36, 4));
        Quantize4Arm64(bl1, Vector128.Create(fdtbl.Slice(40, 4)), halfBits, signMask).CopyTo(q.Slice(40, 4));
        Quantize4Arm64(br1, Vector128.Create(fdtbl.Slice(44, 4)), halfBits, signMask).CopyTo(q.Slice(44, 4));
        Quantize4Arm64(bl2, Vector128.Create(fdtbl.Slice(48, 4)), halfBits, signMask).CopyTo(q.Slice(48, 4));
        Quantize4Arm64(br2, Vector128.Create(fdtbl.Slice(52, 4)), halfBits, signMask).CopyTo(q.Slice(52, 4));
        Quantize4Arm64(bl3, Vector128.Create(fdtbl.Slice(56, 4)), halfBits, signMask).CopyTo(q.Slice(56, 4));
        Quantize4Arm64(br3, Vector128.Create(fdtbl.Slice(60, 4)), halfBits, signMask).CopyTo(q.Slice(60, 4));

        return ScatterZigZagAndFindEnd(q, du);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector128<int> Quantize4Arm64(
        Vector128<float> row,
        Vector128<float> fdtblRow,
        Vector128<uint> halfBits,
        Vector128<uint> signMask)
    {
        Vector128<float> v = AdvSimd.Multiply(row, fdtblRow);
        Vector128<float> signedHalf = AdvSimd.Or(halfBits, AdvSimd.And(v.AsUInt32(), signMask)).AsSingle();
        return AdvSimd.ConvertToInt32RoundToZero(AdvSimd.Add(v, signedHalf));
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void Transpose4x4Arm64(
        ref Vector128<float> r0,
        ref Vector128<float> r1,
        ref Vector128<float> r2,
        ref Vector128<float> r3)
    {
        Vector128<ulong> t0 = AdvSimd.Arm64.ZipLow(r0, r1).AsUInt64();
        Vector128<ulong> t1 = AdvSimd.Arm64.ZipHigh(r0, r1).AsUInt64();
        Vector128<ulong> t2 = AdvSimd.Arm64.ZipLow(r2, r3).AsUInt64();
        Vector128<ulong> t3 = AdvSimd.Arm64.ZipHigh(r2, r3).AsUInt64();

        r0 = AdvSimd.Arm64.ZipLow(t0, t2).AsSingle();
        r1 = AdvSimd.Arm64.ZipHigh(t0, t2).AsSingle();
        r2 = AdvSimd.Arm64.ZipLow(t1, t3).AsSingle();
        r3 = AdvSimd.Arm64.ZipHigh(t1, t3).AsSingle();
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void Dct1DArm64(
        ref Vector128<float> d0,
        ref Vector128<float> d1,
        ref Vector128<float> d2,
        ref Vector128<float> d3,
        ref Vector128<float> d4,
        ref Vector128<float> d5,
        ref Vector128<float> d6,
        ref Vector128<float> d7)
    {
        Vector128<float> tmp0 = AdvSimd.Add(d0, d7);
        Vector128<float> tmp7 = AdvSimd.Subtract(d0, d7);
        Vector128<float> tmp1 = AdvSimd.Add(d1, d6);
        Vector128<float> tmp6 = AdvSimd.Subtract(d1, d6);
        Vector128<float> tmp2 = AdvSimd.Add(d2, d5);
        Vector128<float> tmp5 = AdvSimd.Subtract(d2, d5);
        Vector128<float> tmp3 = AdvSimd.Add(d3, d4);
        Vector128<float> tmp4 = AdvSimd.Subtract(d3, d4);

        Vector128<float> tmp10 = AdvSimd.Add(tmp0, tmp3);
        Vector128<float> tmp13 = AdvSimd.Subtract(tmp0, tmp3);
        Vector128<float> tmp11 = AdvSimd.Add(tmp1, tmp2);
        Vector128<float> tmp12 = AdvSimd.Subtract(tmp1, tmp2);

        d0 = AdvSimd.Add(tmp10, tmp11);
        d4 = AdvSimd.Subtract(tmp10, tmp11);

        Vector128<float> c0707 = Vector128.Create(0.707106781f);
        Vector128<float> z1 = AdvSimd.Multiply(AdvSimd.Add(tmp12, tmp13), c0707);
        d2 = AdvSimd.Add(tmp13, z1);
        d6 = AdvSimd.Subtract(tmp13, z1);

        tmp10 = AdvSimd.Add(tmp4, tmp5);
        tmp11 = AdvSimd.Add(tmp5, tmp6);
        tmp12 = AdvSimd.Add(tmp6, tmp7);

        Vector128<float> z5 = AdvSimd.Multiply(AdvSimd.Subtract(tmp10, tmp12), Vector128.Create(0.382683433f));
        Vector128<float> z2 = AdvSimd.Add(AdvSimd.Multiply(tmp10, Vector128.Create(0.541196100f)), z5);
        Vector128<float> z4 = AdvSimd.Add(AdvSimd.Multiply(tmp12, Vector128.Create(1.306562965f)), z5);
        Vector128<float> z3 = AdvSimd.Multiply(tmp11, c0707);

        Vector128<float> z11 = AdvSimd.Add(tmp7, z3);
        Vector128<float> z13 = AdvSimd.Subtract(tmp7, z3);

        d5 = AdvSimd.Add(z13, z2);
        d3 = AdvSimd.Subtract(z13, z2);
        d1 = AdvSimd.Add(z11, z4);
        d7 = AdvSimd.Subtract(z11, z4);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int ScatterZigZagAndFindEnd(scoped ReadOnlySpan<int> q, scoped Span<short> du)
    {
        ReadOnlySpan<byte> zigZag = ZigZag;
        for (int j = 0; j < 64; j++)
        {
            du[zigZag[j]] = (short)q[j];
        }

        int end0pos = 63;
        while (end0pos > 0 && du[end0pos] == 0)
        {
            end0pos--;
        }

        return end0pos;
    }
#endif

    // =========================================================================
    // Headers, Huffman coding, and 64-bit SWAR Bit Writer
    // =========================================================================

    private static void WriteHeaders(
        ref ByteSink sink,
        int width,
        int height,
        bool subsample,
        scoped ReadOnlySpan<byte> yTable,
        scoped ReadOnlySpan<byte> uvTable)
    {
        sink.WriteBytes(HeaderHead0);
        sink.WriteBytes(yTable);
        sink.WriteByte(1);
        sink.WriteBytes(uvTable);

        Span<byte> head1 = stackalloc byte[24];
        head1[0] = 0xFF;
        head1[1] = 0xC0;
        head1[2] = 0x00;
        head1[3] = 0x11;
        head1[4] = 8;
        head1[5] = (byte)(height >> 8);
        head1[6] = (byte)height;
        head1[7] = (byte)(width >> 8);
        head1[8] = (byte)width;
        head1[9] = 3;
        head1[10] = 1;
        head1[11] = (byte)(subsample ? 0x22 : 0x11);
        head1[12] = 0;
        head1[13] = 2;
        head1[14] = 0x11;
        head1[15] = 1;
        head1[16] = 3;
        head1[17] = 0x11;
        head1[18] = 1;
        head1[19] = 0xFF;
        head1[20] = 0xC4;
        head1[21] = 0x01;
        head1[22] = 0xA2;
        head1[23] = 0x00;

        sink.WriteBytes(head1);
        sink.WriteBytes(StdDcLumNrCodes);
        sink.WriteBytes(StdDcLumValues);
        sink.WriteByte(0x10);
        sink.WriteBytes(StdAcLumNrCodes);
        sink.WriteBytes(StdAcLumValues);
        sink.WriteByte(0x01);
        sink.WriteBytes(StdDcChromNrCodes);
        sink.WriteBytes(StdDcChromValues);
        sink.WriteByte(0x11);
        sink.WriteBytes(StdAcChromNrCodes);
        sink.WriteBytes(StdAcChromValues);
        sink.WriteBytes(HeaderHead2);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int EncodeQuantizedBlock(
        ref ByteSink sink,
        ref ulong bitBuf,
        ref int bitCnt,
        scoped ReadOnlySpan<short> du,
        int end0pos,
        int prevDc,
        uint[] htdc,
        uint[] htac)
    {
        // Encode DC
        int dc = du[0];
        int diff = dc - prevDc;
        if (diff == 0)
        {
            uint h0 = htdc[0];
            WriteBits64(ref sink, ref bitBuf, ref bitCnt, h0 >> 5, (int)(h0 & 31u));
        }
        else
        {
            CalcBits(diff, out uint code, out int nbits);
            uint h = htdc[nbits];
            int hLen = (int)(h & 31u);
            WriteBits64(ref sink, ref bitBuf, ref bitCnt, ((ulong)(h >> 5) << nbits) | code, hLen + nbits);
        }

        // Encode ACs
        uint eob = htac[0x00];
        if (end0pos == 0)
        {
            WriteBits64(ref sink, ref bitBuf, ref bitCnt, eob >> 5, (int)(eob & 31u));
            return dc;
        }

        uint m16Zeroes = htac[0xF0];
        uint m16Code = m16Zeroes >> 5;
        int m16Len = (int)(m16Zeroes & 31u);

        for (int i = 1; i <= end0pos; i++)
        {
            int val = du[i];
            if (val == 0)
            {
                int startPos = i;
                do
                {
                    i++;
                }
                while (i <= end0pos && du[i] == 0);

                int nrZeroes = i - startPos;
                while (nrZeroes >= 16)
                {
                    WriteBits64(ref sink, ref bitBuf, ref bitCnt, m16Code, m16Len);
                    nrZeroes -= 16;
                }

                val = du[i];
                CalcBits(val, out uint code, out int nbits);
                uint h = htac[(nrZeroes << 4) + nbits];
                int hLen = (int)(h & 31u);
                WriteBits64(ref sink, ref bitBuf, ref bitCnt, ((ulong)(h >> 5) << nbits) | code, hLen + nbits);
            }
            else
            {
                CalcBits(val, out uint code, out int nbits);
                uint h = htac[nbits];
                int hLen = (int)(h & 31u);
                WriteBits64(ref sink, ref bitBuf, ref bitCnt, ((ulong)(h >> 5) << nbits) | code, hLen + nbits);
            }
        }

        if (end0pos != 63)
        {
            WriteBits64(ref sink, ref bitBuf, ref bitCnt, eob >> 5, (int)(eob & 31u));
        }

        return dc;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void Dct1DScalar(
        ref float d0p,
        ref float d1p,
        ref float d2p,
        ref float d3p,
        ref float d4p,
        ref float d5p,
        ref float d6p,
        ref float d7p)
    {
        float d0 = d0p, d1 = d1p, d2 = d2p, d3 = d3p, d4 = d4p, d5 = d5p, d6 = d6p, d7 = d7p;

        float tmp0 = d0 + d7;
        float tmp7 = d0 - d7;
        float tmp1 = d1 + d6;
        float tmp6 = d1 - d6;
        float tmp2 = d2 + d5;
        float tmp5 = d2 - d5;
        float tmp3 = d3 + d4;
        float tmp4 = d3 - d4;

        float tmp10 = tmp0 + tmp3;
        float tmp13 = tmp0 - tmp3;
        float tmp11 = tmp1 + tmp2;
        float tmp12 = tmp1 - tmp2;

        d0 = tmp10 + tmp11;
        d4 = tmp10 - tmp11;

        float z1 = (tmp12 + tmp13) * 0.707106781f;
        d2 = tmp13 + z1;
        d6 = tmp13 - z1;

        tmp10 = tmp4 + tmp5;
        tmp11 = tmp5 + tmp6;
        tmp12 = tmp6 + tmp7;

        float z5 = (tmp10 - tmp12) * 0.382683433f;
        float z2 = (tmp10 * 0.541196100f) + z5;
        float z4 = (tmp12 * 1.306562965f) + z5;
        float z3 = tmp11 * 0.707106781f;

        float z11 = tmp7 + z3;
        float z13 = tmp7 - z3;

        d5p = z13 + z2;
        d3p = z13 - z2;
        d1p = z11 + z4;
        d7p = z11 - z4;
        d0p = d0;
        d2p = d2;
        d4p = d4;
        d6p = d6;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void CalcBits(int val, out uint code, out int nbits)
    {
        int absVal = val < 0 ? -val : val;
        int adjusted = val < 0 ? val - 1 : val;
        nbits = 32 - BitOps.LeadingZeroCount((uint)absVal);
        code = (uint)adjusted & ((1u << nbits) - 1u);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void WriteBits64(ref ByteSink sink, ref ulong bitBuf, ref int bitCnt, ulong code, int nbits)
    {
        bitCnt += nbits;
        bitBuf |= code << (64 - bitCnt);
        if (bitCnt >= 32)
        {
            Flush32Bits(ref sink, ref bitBuf, ref bitCnt);
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void Flush32Bits(ref ByteSink sink, ref ulong bitBuf, ref int bitCnt)
    {
        uint word = (uint)(bitBuf >> 32);
        bitBuf <<= 32;
        bitCnt -= 32;

        // SWAR test for any 0xFF byte among the 4 big-endian bytes.
        uint z = ~word;
        if (((z - 0x01010101u) & ~z & 0x80808080u) == 0u)
        {
            sink.WriteUInt32BigEndian(word);
        }
        else
        {
            WriteStuffedWord(ref sink, word);
        }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void WriteStuffedWord(ref ByteSink sink, uint word)
    {
        byte b0 = (byte)(word >> 24);
        sink.WriteByte(b0);
        if (b0 == 0xFF)
        {
            sink.WriteByte(0x00);
        }

        byte b1 = (byte)(word >> 16);
        sink.WriteByte(b1);
        if (b1 == 0xFF)
        {
            sink.WriteByte(0x00);
        }

        byte b2 = (byte)(word >> 8);
        sink.WriteByte(b2);
        if (b2 == 0xFF)
        {
            sink.WriteByte(0x00);
        }

        byte b3 = (byte)word;
        sink.WriteByte(b3);
        if (b3 == 0xFF)
        {
            sink.WriteByte(0x00);
        }
    }

    private static void FinishBitstream(ref ByteSink sink, ref ulong bitBuf, ref int bitCnt)
    {
        // Emit 7 one-bits to align any partial byte, matching stb_image_write.h ({0x7F, 7}).
        WriteBits64(ref sink, ref bitBuf, ref bitCnt, 0x7Fu, 7);
        while (bitCnt >= 8)
        {
            byte c = (byte)(bitBuf >> 56);
            sink.WriteByte(c);
            if (c == 0xFF)
            {
                sink.WriteByte(0x00);
            }

            bitBuf <<= 8;
            bitCnt -= 8;
        }

        // EOI marker
        sink.Write2(0xFF, 0xD9);
    }
}
