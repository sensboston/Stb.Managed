using System.Buffers;
using System.Buffers.Binary;
using System.Runtime.CompilerServices;
using System.Runtime.ExceptionServices;
#if NET8_0_OR_GREATER
using System.Runtime.InteropServices;
#endif
using Stb.Managed.Internal;

namespace Stb.Managed.Image.Internal;

/// <summary>
/// Faithful, bounds-checked JPEG decoder (baseline SOF0, extended sequential SOF1, progressive SOF2)
/// matching <c>stb_image.h</c> semantics bit-for-bit.
/// </summary>
internal static class JpegDecoder
{
    private const byte MarkerDnl = 0xDC;
    private const byte MarkerSoi = 0xD8;
    private const byte MarkerEoi = 0xD9;
    private const byte MarkerSos = 0xDA;
    private const byte MarkerDqt = 0xDB;
    private const byte MarkerDri = 0xDD;
    private const byte MarkerDht = 0xC4;
    private const byte MarkerCom = 0xFE;

    // Auto-parallel pixel thresholds when MaxDegreeOfParallelism == 0.
    // DRI entropy+IDCT and progressive Dequant+IDCT write directly to pooled byte[] planes with zero extra copy.
    // Parallel resample+YCbCr writes directly to byte[] (Load) and only activates on 2 MP+ images where bandwidth scales.
    private const long ParallelBlockPixelThreshold = 262_144;
    private const long ParallelResamplePixelThreshold = 2_097_152;

    [ThreadStatic]
    private static JpegWorkspace? t_cachedWorkspace;

    internal static bool IsJpeg(ReadOnlySpan<byte> data)
    {
        if (data.Length < 2 || data[0] != 0xFF)
        {
            return false;
        }

        int pos = 1;
        while (pos < data.Length && data[pos] == 0xFF)
        {
            pos++;
        }

        return pos < data.Length && data[pos] == MarkerSoi;
    }

    internal static bool TryGetInfo(
        ReadOnlySpan<byte> data,
        ImageDecodeOptions options,
        out ImageInfo info)
    {
        if (!IsJpeg(data))
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
        JpegWorkspace ws = RentWorkspace();
        try
        {
            ws.Reset();
            JpegReader reader = new(data);
            DecodeHeader(ref reader, ws, options.Options.Limits, headerOnly: true);
            int reportedComp = ws.ImgN >= 3 ? 3 : 1;
            return new ImageInfo(
                ws.Width,
                ws.Height,
                (ColorComponents)reportedComp,
                BitsPerChannel: 8,
                IsHdr: false);
        }
        finally
        {
            ReturnWorkspace(ws);
        }
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
            throw new StbException(StbErrorCode.InternalError, "Failed to allocate decoded 8-bit JPEG buffer.");
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
        ImageBuffer<byte> img8 = Decode8(data, desiredComponents, options);
        int sampleCount = checked(img8.Width * img8.Height * (int)img8.Components);
        options.Options.Limits.ValidateAllocation((long)sampleCount * sizeof(ushort));
        ushort[] pixels16 = new ushort[sampleCount];
        PixelConverter.Convert8To16(img8.AsSpan(), pixels16);
        return new ImageBuffer<ushort>(img8.Width, img8.Height, img8.Components, img8.SourceComponents, pixels16);
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
        info = GetInfo(data, options);
        int outN = desiredComponents == ColorComponents.Default ? (int)info.Components : (int)desiredComponents;
        outputComponents = (ColorComponents)outN;
        int requiredSamples = options.Options.Limits.ValidateDimensions(info.Width, info.Height, outN * sizeof(ushort)) / sizeof(ushort);
        if (destination.Length < requiredSamples)
        {
            return false;
        }

        using PooledBuffer<byte> temp8 = new(requiredSamples);
        bool ok = TryDecode8(data, temp8.Span, desiredComponents, options, out info, out outputComponents);
        if (!ok)
        {
            return false;
        }

        PixelConverter.Convert8To16(temp8.Span, destination.Slice(0, requiredSamples));
        return true;
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
        JpegWorkspace ws = RentWorkspace();
        try
        {
            ws.Reset();
            JpegReader reader = new(data);

            // Parse SOI .. SOF (validating frame header & dimensions)
            DecodeHeader(ref reader, ws, limits, headerOnly: false);

            int reportedComp = ws.ImgN >= 3 ? 3 : 1;
            int outN = desiredComponents == ColorComponents.Default ? reportedComp : (int)desiredComponents;
            info = new ImageInfo(ws.Width, ws.Height, (ColorComponents)reportedComp, BitsPerChannel: 8, IsHdr: false);
            outputComponents = (ColorComponents)outN;

            int outputBytes = limits.ValidateDimensions(ws.Width, ws.Height, outN);
            if (useExternalDestination && externalDestination.Length < outputBytes)
            {
                return false;
            }

            // Validate & allocate component planes (and progressive coefficient planes if SOF2)
            AllocateComponentPlanes(ws, limits);

            long pixelCount = (long)ws.Width * ws.Height;
            int blockDop = options.Options.ResolveDegreeOfParallelism(pixelCount, ParallelBlockPixelThreshold);
            int resampleDop = options.Options.ResolveDegreeOfParallelism(pixelCount, ParallelResamplePixelThreshold);

            // Decode scans until EOI
            DecodeScans(ref reader, ws, blockDop);

            // If progressive, dequantize + IDCT all component blocks
            if (ws.Progressive)
            {
                FinishProgressive(ws, blockDop);
            }

            // Prepare destination buffer and resample/color-convert scanlines
            if (useExternalDestination)
            {
                // Write directly into caller-supplied Span<byte> with zero intermediate copy.
                ResampleAndColorConvert(ws, externalDestination.Slice(0, outputBytes), outN, options.FlipVertically);
            }
            else
            {
                byte[] ownedOutput = new byte[outputBytes];
                if (resampleDop > 1 && ws.Height >= 32)
                {
                    ResampleAndColorConvertParallelToArray(ws, ownedOutput, outN, options.FlipVertically, resampleDop);
                }
                else
                {
                    ResampleAndColorConvert(ws, ownedOutput, outN, options.FlipVertically);
                }

                allocatedBuffer = new ImageBuffer<byte>(ws.Width, ws.Height, outputComponents, info.Components, ownedOutput);
            }

            return true;
        }
        finally
        {
            ws.ReleasePlanes();
            ReturnWorkspace(ws);
        }
    }

    private static void DecodeHeader(ref JpegReader reader, JpegWorkspace ws, StbLimits limits, bool headerOnly)
    {
        byte m = reader.GetMarker();
        if (m != MarkerSoi)
        {
            throw new StbException(StbErrorCode.InvalidData, "Missing JPEG SOI marker.");
        }

        m = reader.GetMarker();
        while (!IsSof(m))
        {
            ProcessMarker(ref reader, ws, m);
            m = reader.GetMarker();
            while (m == JpegTables.MarkerNone)
            {
                if (reader.AtEof)
                {
                    throw new StbException(StbErrorCode.TruncatedInput, "Unexpected EOF before JPEG SOF marker.");
                }

                m = reader.GetMarker();
            }
        }

        ws.Progressive = m == 0xC2;
        ProcessFrameHeader(ref reader, ws, limits, headerOnly);
    }

    private static void ProcessFrameHeader(ref JpegReader reader, JpegWorkspace ws, StbLimits limits, bool headerOnly)
    {
        if (reader.Remaining < 8)
        {
            throw new StbException(StbErrorCode.TruncatedInput, "Truncated JPEG SOF header.");
        }

        int lf = reader.ReadUInt16BE();
        if (lf < 11)
        {
            throw new StbException(StbErrorCode.InvalidData, "Invalid JPEG SOF length.");
        }

        int precision = reader.ReadByteOrZero();
        if (precision != 8)
        {
            throw new StbException(StbErrorCode.UnsupportedFeature, $"Unsupported JPEG sample precision: {precision}-bit (only 8-bit is supported).");
        }

        int height = reader.ReadUInt16BE();
        if (height == 0)
        {
            throw new StbException(StbErrorCode.UnsupportedFeature, "JPEG delayed height (DNL with SOF height 0) is not supported.");
        }

        int width = reader.ReadUInt16BE();
        if (width == 0)
        {
            throw new StbException(StbErrorCode.InvalidData, "JPEG width cannot be zero.");
        }

        int compCount = reader.ReadByteOrZero();
        if (compCount != 1 && compCount != 3 && compCount != 4)
        {
            throw new StbException(StbErrorCode.InvalidData, $"Unsupported JPEG component count: {compCount}.");
        }

        if (lf != 8 + (3 * compCount))
        {
            throw new StbException(StbErrorCode.InvalidData, "JPEG SOF length does not match component count.");
        }

        if (reader.Remaining < 3 * compCount)
        {
            throw new StbException(StbErrorCode.TruncatedInput, "Truncated JPEG SOF component table.");
        }

        // Enforce dimension & pixel limits right away (even for GetInfo)
        _ = limits.ValidateDimensions(width, height, compCount);

        ws.Width = width;
        ws.Height = height;
        ws.ImgN = compCount;
        ws.RgbTagCount = 0;

        for (int i = 0; i < compCount; i++)
        {
            byte id = reader.ReadByteOrZero();
            if (compCount == 3 && ((i == 0 && id == (byte)'R') || (i == 1 && id == (byte)'G') || (i == 2 && id == (byte)'B')))
            {
                ws.RgbTagCount++;
            }

            byte hv = reader.ReadByteOrZero();
            int h = hv >> 4;
            int v = hv & 15;
            if (h == 0 || h > 4)
            {
                throw new StbException(StbErrorCode.InvalidData, $"Invalid JPEG horizontal sampling factor: {h}.");
            }

            if (v == 0 || v > 4)
            {
                throw new StbException(StbErrorCode.InvalidData, $"Invalid JPEG vertical sampling factor: {v}.");
            }

            int tq = reader.ReadByteOrZero();
            if (tq > 3)
            {
                throw new StbException(StbErrorCode.InvalidData, $"Invalid JPEG quantization table selector: {tq}.");
            }

            ref JpegComponent c = ref ws.Components[i];
            c.Id = id;
            c.H = h;
            c.V = v;
            c.Tq = tq;
            c.Hd = 0;
            c.Ha = 0;
            c.DcPred = 0;
        }

        if (headerOnly)
        {
            return;
        }

        int hMax = 1;
        int vMax = 1;
        for (int i = 0; i < compCount; i++)
        {
            if (ws.Components[i].H > hMax)
            {
                hMax = ws.Components[i].H;
            }

            if (ws.Components[i].V > vMax)
            {
                vMax = ws.Components[i].V;
            }
        }

        for (int i = 0; i < compCount; i++)
        {
            if (hMax % ws.Components[i].H != 0)
            {
                throw new StbException(StbErrorCode.InvalidData, "Non-integer horizontal subsampling ratio.");
            }

            if (vMax % ws.Components[i].V != 0)
            {
                throw new StbException(StbErrorCode.InvalidData, "Non-integer vertical subsampling ratio.");
            }
        }

        ws.ImgHMax = hMax;
        ws.ImgVMax = vMax;
        ws.ImgMcuW = hMax * 8;
        ws.ImgMcuH = vMax * 8;
        ws.ImgMcuX = (width + ws.ImgMcuW - 1) / ws.ImgMcuW;
        ws.ImgMcuY = (height + ws.ImgMcuH - 1) / ws.ImgMcuH;
    }

    private static void AllocateComponentPlanes(JpegWorkspace ws, StbLimits limits)
    {
        long totalWorkingBytes = 0;
        for (int i = 0; i < ws.ImgN; i++)
        {
            ref JpegComponent c = ref ws.Components[i];
            c.X = (int)(((long)ws.Width * c.H + ws.ImgHMax - 1) / ws.ImgHMax);
            c.Y = (int)(((long)ws.Height * c.V + ws.ImgVMax - 1) / ws.ImgVMax);

            long w2Long = (long)ws.ImgMcuX * c.H * 8;
            long h2Long = (long)ws.ImgMcuY * c.V * 8;
            if (w2Long > int.MaxValue || h2Long > int.MaxValue || w2Long * h2Long > int.MaxValue)
            {
                throw new StbException(StbErrorCode.LimitExceeded, "JPEG component plane dimensions overflow.");
            }

            long planeBytes = w2Long * h2Long;
            limits.ValidateAllocation(planeBytes);
            totalWorkingBytes += planeBytes;

            if (ws.Progressive)
            {
                long coeffBytes = planeBytes * sizeof(short);
                limits.ValidateAllocation(coeffBytes);
                totalWorkingBytes += coeffBytes;
            }

            limits.ValidateAllocation(totalWorkingBytes);

            c.W2 = (int)w2Long;
            c.H2 = (int)h2Long;
            c.CoeffW = c.W2 >> 3;
            c.CoeffH = c.H2 >> 3;
        }

        for (int i = 0; i < ws.ImgN; i++)
        {
            ref JpegComponent c = ref ws.Components[i];
            int planeLen = c.W2 * c.H2;
            c.Data = ArrayPool<byte>.Shared.Rent(planeLen);
            if (ws.Progressive)
            {
                c.Coeff = ArrayPool<short>.Shared.Rent(planeLen);
                c.Coeff.AsSpan(0, planeLen).Clear();
            }
        }
    }

    private static void DecodeScans(ref JpegReader reader, JpegWorkspace ws, int driDop)
    {
        byte m = reader.GetMarker();
        while (m != MarkerEoi)
        {
            if (m == MarkerSos)
            {
                ProcessScanHeader(ref reader, ws);
                if (driDop <= 1 || ws.Progressive || ws.RestartInterval <= 0 || !TryParseBaselineDriParallel(ref reader, ws, driDop))
                {
                    ParseEntropyCodedData(ref reader, ws);
                }

                if (reader.Marker == JpegTables.MarkerNone)
                {
                    reader.Marker = reader.SkipJunkAtEnd();
                }

                m = reader.GetMarker();
                if (IsRestart(m))
                {
                    m = reader.GetMarker();
                }
            }
            else if (m == MarkerDnl)
            {
                if (reader.Remaining < 4)
                {
                    throw new StbException(StbErrorCode.TruncatedInput, "Truncated JPEG DNL marker.");
                }

                int ld = reader.ReadUInt16BE();
                int nl = reader.ReadUInt16BE();
                if (ld != 4)
                {
                    throw new StbException(StbErrorCode.InvalidData, "Invalid JPEG DNL marker length.");
                }

                if (nl != ws.Height)
                {
                    throw new StbException(StbErrorCode.InvalidData, "JPEG DNL height does not match frame header.");
                }

                m = reader.GetMarker();
            }
            else
            {
                ProcessMarker(ref reader, ws, m);
                m = reader.GetMarker();
            }
        }
    }

    private static void ProcessMarker(ref JpegReader reader, JpegWorkspace ws, byte m)
    {
        switch (m)
        {
            case JpegTables.MarkerNone:
                throw new StbException(StbErrorCode.InvalidData, "Expected JPEG marker.");

            case MarkerDri:
            {
                if (reader.Remaining < 4)
                {
                    throw new StbException(StbErrorCode.TruncatedInput, "Truncated JPEG DRI marker.");
                }

                int len = reader.ReadUInt16BE();
                if (len != 4)
                {
                    throw new StbException(StbErrorCode.InvalidData, "Invalid JPEG DRI marker length.");
                }

                ws.RestartInterval = reader.ReadUInt16BE();
                return;
            }

            case MarkerDqt:
            {
                if (reader.Remaining < 2)
                {
                    throw new StbException(StbErrorCode.TruncatedInput, "Truncated JPEG DQT marker.");
                }

                int len = reader.ReadUInt16BE() - 2;
                if (len < 0 || reader.Remaining < len)
                {
                    throw new StbException(StbErrorCode.TruncatedInput, "Truncated JPEG DQT payload.");
                }

                ReadOnlySpan<byte> dezigzag = JpegTables.Dezigzag;
                while (len > 0)
                {
                    int q = reader.ReadByteOrZero();
                    int p = q >> 4;
                    int t = q & 15;
                    if (p != 0 && p != 1)
                    {
                        throw new StbException(StbErrorCode.InvalidData, $"Invalid JPEG DQT precision: {p}.");
                    }

                    if (t > 3)
                    {
                        throw new StbException(StbErrorCode.InvalidData, $"Invalid JPEG DQT table id: {t}.");
                    }

                    bool sixteen = p != 0;
                    int entrySize = sixteen ? 129 : 65;
                    if (len < entrySize)
                    {
                        throw new StbException(StbErrorCode.InvalidData, "Invalid JPEG DQT segment length.");
                    }

                    Span<ushort> dq = ws.GetDequantSpan(t);
                    if (sixteen)
                    {
                        for (int i = 0; i < 64; i++)
                        {
                            dq[dezigzag[i]] = (ushort)reader.ReadUInt16BE();
                        }
                    }
                    else
                    {
                        for (int i = 0; i < 64; i++)
                        {
                            dq[dezigzag[i]] = reader.ReadByteOrZero();
                        }
                    }

                    len -= entrySize;
                }

                if (len != 0)
                {
                    throw new StbException(StbErrorCode.InvalidData, "Malformed JPEG DQT segment length.");
                }

                return;
            }

            case MarkerDht:
            {
                if (reader.Remaining < 2)
                {
                    throw new StbException(StbErrorCode.TruncatedInput, "Truncated JPEG DHT marker.");
                }

                int len = reader.ReadUInt16BE() - 2;
                if (len < 0 || reader.Remaining < len)
                {
                    throw new StbException(StbErrorCode.TruncatedInput, "Truncated JPEG DHT payload.");
                }

                Span<byte> sizes = stackalloc byte[16];
                while (len > 0)
                {
                    if (len < 17)
                    {
                        throw new StbException(StbErrorCode.InvalidData, "Truncated JPEG DHT header.");
                    }

                    int q = reader.ReadByteOrZero();
                    int tc = q >> 4;
                    int th = q & 15;
                    if (tc > 1 || th > 3)
                    {
                        throw new StbException(StbErrorCode.InvalidData, $"Invalid JPEG DHT header (tc={tc}, th={th}).");
                    }

                    int n = 0;
                    for (int i = 0; i < 16; i++)
                    {
                        byte cnt = reader.ReadByteOrZero();
                        sizes[i] = cnt;
                        n += cnt;
                    }

                    if (n > 256)
                    {
                        throw new StbException(StbErrorCode.InvalidData, "JPEG DHT symbol count exceeds 256.");
                    }

                    len -= 17;
                    if (len < n)
                    {
                        throw new StbException(StbErrorCode.InvalidData, "JPEG DHT payload shorter than symbol count.");
                    }

                    JpegHuffmanTable table = tc == 0 ? ws.HuffDc[th] : ws.HuffAc[th];
                    if (!table.Build(sizes))
                    {
                        throw new StbException(StbErrorCode.InvalidData, "Invalid JPEG Huffman code lengths.");
                    }

                    for (int i = 0; i < n; i++)
                    {
                        table.Values[i] = reader.ReadByteOrZero();
                    }

                    table.IsDefined = true;
                    if (tc == 0)
                    {
                        table.BuildFastDc(ws.FastDc[th]);
                    }
                    else
                    {
                        table.BuildFastAc(ws.FastAc[th]);
                    }

                    len -= n;
                }

                if (len != 0)
                {
                    throw new StbException(StbErrorCode.InvalidData, "Malformed JPEG DHT segment length.");
                }

                return;
            }
        }

        // APP0..APP15 or COM
        if ((m >= 0xE0 && m <= 0xEF) || m == MarkerCom)
        {
            if (reader.Remaining < 2)
            {
                throw new StbException(StbErrorCode.TruncatedInput, "Truncated JPEG APP/COM marker.");
            }

            int len = reader.ReadUInt16BE();
            if (len < 2)
            {
                throw new StbException(StbErrorCode.InvalidData, "Invalid JPEG APP/COM marker length.");
            }

            len -= 2;
            if (m == 0xE0 && len >= 5)
            {
                bool ok = reader.ReadByteOrZero() == (byte)'J';
                ok &= reader.ReadByteOrZero() == (byte)'F';
                ok &= reader.ReadByteOrZero() == (byte)'I';
                ok &= reader.ReadByteOrZero() == (byte)'F';
                ok &= reader.ReadByteOrZero() == 0;
                len -= 5;
                if (ok)
                {
                    ws.Jfif = true;
                }
            }
            else if (m == 0xEE && len >= 12)
            {
                bool ok = reader.ReadByteOrZero() == (byte)'A';
                ok &= reader.ReadByteOrZero() == (byte)'d';
                ok &= reader.ReadByteOrZero() == (byte)'o';
                ok &= reader.ReadByteOrZero() == (byte)'b';
                ok &= reader.ReadByteOrZero() == (byte)'e';
                ok &= reader.ReadByteOrZero() == 0;
                len -= 6;
                if (ok)
                {
                    _ = reader.ReadByteOrZero(); // version
                    _ = reader.ReadUInt16BE();   // flags0
                    _ = reader.ReadUInt16BE();   // flags1
                    ws.App14ColorTransform = reader.ReadByteOrZero();
                    len -= 6;
                }
            }

            reader.Skip(len);
            return;
        }

        throw new StbException(StbErrorCode.UnsupportedFeature, $"Unknown or unsupported JPEG marker 0x{m:X2}.");
    }

    private static void ProcessScanHeader(ref JpegReader reader, JpegWorkspace ws)
    {
        if (reader.Remaining < 3)
        {
            throw new StbException(StbErrorCode.TruncatedInput, "Truncated JPEG SOS header.");
        }

        int ls = reader.ReadUInt16BE();
        int scanN = reader.ReadByteOrZero();
        if (scanN < 1 || scanN > 4 || scanN > ws.ImgN)
        {
            throw new StbException(StbErrorCode.InvalidData, $"Invalid JPEG SOS component count: {scanN}.");
        }

        if (ls != 6 + (2 * scanN))
        {
            throw new StbException(StbErrorCode.InvalidData, "Invalid JPEG SOS length.");
        }

        if (reader.Remaining < (2 * scanN) + 3)
        {
            throw new StbException(StbErrorCode.TruncatedInput, "Truncated JPEG SOS payload.");
        }

        ws.ScanN = scanN;
        for (int i = 0; i < scanN; i++)
        {
            int id = reader.ReadByteOrZero();
            int q = reader.ReadByteOrZero();

            int which = 0;
            while (which < ws.ImgN && ws.Components[which].Id != id)
            {
                which++;
            }

            if (which == ws.ImgN)
            {
                throw new StbException(StbErrorCode.InvalidData, $"Unknown component id {id} in JPEG SOS header.");
            }

            int hd = q >> 4;
            int ha = q & 15;
            if (hd > 3)
            {
                throw new StbException(StbErrorCode.InvalidData, $"Invalid JPEG DC Huffman table selector: {hd}.");
            }

            if (ha > 3)
            {
                throw new StbException(StbErrorCode.InvalidData, $"Invalid JPEG AC Huffman table selector: {ha}.");
            }

            ws.Components[which].Hd = hd;
            ws.Components[which].Ha = ha;
            ws.Order[i] = which;
        }

        int ss = reader.ReadByteOrZero();
        int se = reader.ReadByteOrZero();
        int aa = reader.ReadByteOrZero();
        int ah = aa >> 4;
        int al = aa & 15;

        if (ws.Progressive)
        {
            if (ss > 63 || se > 63 || ss > se || ah > 13 || al > 13)
            {
                throw new StbException(StbErrorCode.InvalidData, "Invalid JPEG progressive SOS parameters.");
            }

            ws.SpecStart = ss;
            ws.SpecEnd = se;
            ws.SuccHigh = ah;
            ws.SuccLow = al;
        }
        else
        {
            if (ss != 0 || ah != 0 || al != 0)
            {
                throw new StbException(StbErrorCode.InvalidData, "Invalid JPEG baseline SOS parameters.");
            }

            ws.SpecStart = 0;
            ws.SpecEnd = 63;
            ws.SuccHigh = 0;
            ws.SuccLow = 0;
        }
    }

    private static void ResetEntropyState(ref JpegReader reader, JpegWorkspace ws)
    {
        reader.ResetEntropy();
        for (int i = 0; i < 4; i++)
        {
            ws.Components[i].DcPred = 0;
        }

        ws.Todo = ws.RestartInterval != 0 ? ws.RestartInterval : int.MaxValue;
        ws.EobRun = 0;
    }

    private static void ParseEntropyCodedData(ref JpegReader reader, JpegWorkspace ws)
    {
        reader.MaxFillBits = ws.Progressive ? 32 : 63;
        ResetEntropyState(ref reader, ws);

        if (!ws.Progressive)
        {
            if (ws.ScanN == 1)
            {
                Span<short> block0 = stackalloc short[64];
                Span<short> block1 = stackalloc short[64];
                int n = ws.Order[0];
                ref JpegComponent comp = ref ws.Components[n];
                JpegHuffmanTable hdc = ws.HuffDc[comp.Hd];
                JpegHuffmanTable hac = ws.HuffAc[comp.Ha];
                if (!hdc.IsDefined || !hac.IsDefined)
                {
                    throw new StbException(StbErrorCode.InvalidData, "JPEG scan references an undefined Huffman table.");
                }

                int[] fdc = ws.FastDc[comp.Hd];
                int[] fac = ws.FastAc[comp.Ha];
                ReadOnlySpan<ushort> dq = ws.GetDequantSpan(comp.Tq);
                Span<byte> compData = comp.Data.AsSpan();
                int stride = comp.W2;
                int w = (comp.X + 7) >> 3;
                int h = (comp.Y + 7) >> 3;

                for (int j = 0; j < h; j++)
                {
                    int rowOffset = stride * (j * 8);
                    int i = 0;
                    while (i < w)
                    {
                        if (i + 1 < w && ws.Todo != 1)
                        {
                            bool dc0 = DecodeBlockBaseline(ref reader, block0, hdc, hac, fdc, fac, ref comp.DcPred, dq, out short s0);
                            ws.Todo--;
                            bool dc1 = DecodeBlockBaseline(ref reader, block1, hdc, hac, fdc, fac, ref comp.DcPred, dq, out short s1);
                            ws.Todo--;

                            int off0 = rowOffset + (i * 8);
                            DispatchTwoBlocks(compData, off0, stride, block0, dc0, s0, compData, off0 + 8, stride, block1, dc1, s1);
                            i += 2;

                            if (ws.Todo <= 0)
                            {
                                if (reader.Marker == JpegTables.MarkerNone && reader.CodeBits < 24)
                                {
                                    reader.GrowBufferUnsafe();
                                }

                                if (!IsRestart(reader.Marker))
                                {
                                    return;
                                }

                                ResetEntropyState(ref reader, ws);
                            }
                        }
                        else
                        {
                            bool dc0 = DecodeBlockBaseline(ref reader, block0, hdc, hac, fdc, fac, ref comp.DcPred, dq, out short s0);
                            int off0 = rowOffset + (i * 8);
                            if (dc0)
                            {
                                JpegIdct.FillDcBlock(compData, off0, stride, s0);
                            }
                            else
                            {
                                JpegIdct.IdctBlock(compData, off0, stride, block0);
                            }

                            i++;
                            if (--ws.Todo <= 0)
                            {
                                if (reader.Marker == JpegTables.MarkerNone && reader.CodeBits < 24)
                                {
                                    reader.GrowBufferUnsafe();
                                }

                                if (!IsRestart(reader.Marker))
                                {
                                    return;
                                }

                                ResetEntropyState(ref reader, ws);
                            }
                        }
                    }
                }
            }
            else
            {
                // Verify Huffman tables for all components in this interleaved scan
                for (int k = 0; k < ws.ScanN; k++)
                {
                    int n = ws.Order[k];
                    if (!ws.HuffDc[ws.Components[n].Hd].IsDefined || !ws.HuffAc[ws.Components[n].Ha].IsDefined)
                    {
                        throw new StbException(StbErrorCode.InvalidData, "JPEG scan references an undefined Huffman table.");
                    }
                }

                // Specialized interleaved 4:2:0 fast path (Y=2x2, Cb=1x1, Cr=1x1) with AVX2 2-block IDCT
                if (ws.ScanN == 3
                    && ws.Order[0] == 0 && ws.Order[1] == 1 && ws.Order[2] == 2
                    && ws.Components[0].H == 2 && ws.Components[0].V == 2
                    && ws.Components[1].H == 1 && ws.Components[1].V == 1
                    && ws.Components[2].H == 1 && ws.Components[2].V == 1)
                {
                    DecodeBaseline420Interleaved(ref reader, ws);
                    return;
                }

                Span<short> block0 = stackalloc short[64];
                Span<short> block1 = stackalloc short[64];
                for (int j = 0; j < ws.ImgMcuY; j++)
                {
                    for (int i = 0; i < ws.ImgMcuX; i++)
                    {
                        for (int k = 0; k < ws.ScanN; k++)
                        {
                            int n = ws.Order[k];
                            ref JpegComponent comp = ref ws.Components[n];
                            JpegHuffmanTable hdc = ws.HuffDc[comp.Hd];
                            JpegHuffmanTable hac = ws.HuffAc[comp.Ha];
                            int[] fdc = ws.FastDc[comp.Hd];
                            int[] fac = ws.FastAc[comp.Ha];
                            ReadOnlySpan<ushort> dq = ws.GetDequantSpan(comp.Tq);
                            Span<byte> compData = comp.Data.AsSpan();
                            int stride = comp.W2;

                            for (int y = 0; y < comp.V; y++)
                            {
                                int y2 = ((j * comp.V) + y) * 8;
                                int rowOffset = stride * y2;
                                int x = 0;
                                while (x + 1 < comp.H)
                                {
                                    int x2 = ((i * comp.H) + x) * 8;
                                    bool dc0 = DecodeBlockBaseline(ref reader, block0, hdc, hac, fdc, fac, ref comp.DcPred, dq, out short s0);
                                    bool dc1 = DecodeBlockBaseline(ref reader, block1, hdc, hac, fdc, fac, ref comp.DcPred, dq, out short s1);
                                    DispatchTwoBlocks(compData, rowOffset + x2, stride, block0, dc0, s0, compData, rowOffset + x2 + 8, stride, block1, dc1, s1);
                                    x += 2;
                                }

                                if (x < comp.H)
                                {
                                    int x2 = ((i * comp.H) + x) * 8;
                                    bool dc0 = DecodeBlockBaseline(ref reader, block0, hdc, hac, fdc, fac, ref comp.DcPred, dq, out short s0);
                                    if (dc0)
                                    {
                                        JpegIdct.FillDcBlock(compData, rowOffset + x2, stride, s0);
                                    }
                                    else
                                    {
                                        JpegIdct.IdctBlock(compData, rowOffset + x2, stride, block0);
                                    }
                                }
                            }
                        }

                        if (--ws.Todo <= 0)
                        {
                            if (reader.Marker == JpegTables.MarkerNone && reader.CodeBits < 24)
                            {
                                reader.GrowBufferUnsafe();
                            }

                            if (!IsRestart(reader.Marker))
                            {
                                return;
                            }

                            ResetEntropyState(ref reader, ws);
                        }
                    }
                }
            }
        }
        else
        {
            if (ws.ScanN == 1)
            {
                int n = ws.Order[0];
                ref JpegComponent comp = ref ws.Components[n];
                int w = (comp.X + 7) >> 3;
                int h = (comp.Y + 7) >> 3;
                short[] coeff = comp.Coeff!;
                int coeffW = comp.CoeffW;

                if (ws.SpecStart == 0)
                {
                    if (ws.SpecEnd != 0)
                    {
                        throw new StbException(StbErrorCode.InvalidData, "Progressive JPEG cannot merge DC and AC in a single scan.");
                    }

                    JpegHuffmanTable hdc = ws.HuffDc[comp.Hd];
                    if (ws.SuccHigh == 0 && !hdc.IsDefined)
                    {
                        throw new StbException(StbErrorCode.InvalidData, "Progressive DC scan references an undefined Huffman table.");
                    }

                    int[] fdc = ws.FastDc[comp.Hd];
                    for (int j = 0; j < h; j++)
                    {
                        for (int i = 0; i < w; i++)
                        {
                            if (!reader.NoMore)
                            {
                                Span<short> blk = coeff.AsSpan(64 * (i + (j * coeffW)), 64);
                                DecodeBlockProgDc(ref reader, blk, hdc, fdc, ref comp.DcPred, ws.SuccHigh, ws.SuccLow);
                            }

                            if (--ws.Todo <= 0)
                            {
                                if (reader.Marker == JpegTables.MarkerNone && reader.CodeBits < 24)
                                {
                                    reader.GrowBufferUnsafe();
                                }

                                if (!IsRestart(reader.Marker))
                                {
                                    return;
                                }

                                ResetEntropyState(ref reader, ws);
                            }
                        }
                    }
                }
                else
                {
                    JpegHuffmanTable hac = ws.HuffAc[comp.Ha];
                    if (!hac.IsDefined)
                    {
                        throw new StbException(StbErrorCode.InvalidData, "Progressive AC scan references an undefined Huffman table.");
                    }

                    int[] fac = ws.FastAc[comp.Ha];
                    for (int j = 0; j < h; j++)
                    {
                        for (int i = 0; i < w; i++)
                        {
                            if (!reader.NoMore)
                            {
                                Span<short> blk = coeff.AsSpan(64 * (i + (j * coeffW)), 64);
                                DecodeBlockProgAc(ref reader, ws, blk, hac, fac);
                            }

                            if (--ws.Todo <= 0)
                            {
                                if (reader.Marker == JpegTables.MarkerNone && reader.CodeBits < 24)
                                {
                                    reader.GrowBufferUnsafe();
                                }

                                if (!IsRestart(reader.Marker))
                                {
                                    return;
                                }

                                ResetEntropyState(ref reader, ws);
                            }
                        }
                    }
                }
            }
            else
            {
                if (ws.SpecEnd != 0)
                {
                    throw new StbException(StbErrorCode.InvalidData, "Interleaved progressive JPEG scan cannot contain AC coefficients.");
                }

                if (ws.SuccHigh == 0)
                {
                    for (int k = 0; k < ws.ScanN; k++)
                    {
                        int n = ws.Order[k];
                        if (!ws.HuffDc[ws.Components[n].Hd].IsDefined)
                        {
                            throw new StbException(StbErrorCode.InvalidData, "Progressive interleaved DC scan references an undefined Huffman table.");
                        }
                    }
                }

                for (int j = 0; j < ws.ImgMcuY; j++)
                {
                    for (int i = 0; i < ws.ImgMcuX; i++)
                    {
                        for (int k = 0; k < ws.ScanN; k++)
                        {
                            int n = ws.Order[k];
                            ref JpegComponent comp = ref ws.Components[n];
                            JpegHuffmanTable hdc = ws.HuffDc[comp.Hd];
                            int[] fdc = ws.FastDc[comp.Hd];
                            short[] coeff = comp.Coeff!;
                            int coeffW = comp.CoeffW;

                            for (int y = 0; y < comp.V; y++)
                            {
                                int y2 = (j * comp.V) + y;
                                for (int x = 0; x < comp.H; x++)
                                {
                                    int x2 = (i * comp.H) + x;
                                    Span<short> blk = coeff.AsSpan(64 * (x2 + (y2 * coeffW)), 64);
                                    DecodeBlockProgDc(ref reader, blk, hdc, fdc, ref comp.DcPred, ws.SuccHigh, ws.SuccLow);
                                }
                            }
                        }

                        if (--ws.Todo <= 0)
                        {
                            if (reader.Marker == JpegTables.MarkerNone && reader.CodeBits < 24)
                            {
                                reader.GrowBufferUnsafe();
                            }

                            if (!IsRestart(reader.Marker))
                            {
                                return;
                            }

                            ResetEntropyState(ref reader, ws);
                        }
                    }
                }
            }
        }
    }

    private static void DecodeBaseline420Interleaved(ref JpegReader reader, JpegWorkspace ws)
    {
        ref JpegComponent cY = ref ws.Components[0];
        ref JpegComponent cCb = ref ws.Components[1];
        ref JpegComponent cCr = ref ws.Components[2];

        JpegHuffmanTable yHdc = ws.HuffDc[cY.Hd];
        JpegHuffmanTable yHac = ws.HuffAc[cY.Ha];
        int[] yFdc = ws.FastDc[cY.Hd];
        int[] yFac = ws.FastAc[cY.Ha];
        ReadOnlySpan<ushort> yDq = ws.GetDequantSpan(cY.Tq);
        Span<byte> yData = cY.Data.AsSpan();
        int yStride = cY.W2;

        JpegHuffmanTable cbHdc = ws.HuffDc[cCb.Hd];
        JpegHuffmanTable cbHac = ws.HuffAc[cCb.Ha];
        int[] cbFdc = ws.FastDc[cCb.Hd];
        int[] cbFac = ws.FastAc[cCb.Ha];
        ReadOnlySpan<ushort> cbDq = ws.GetDequantSpan(cCb.Tq);
        Span<byte> cbData = cCb.Data.AsSpan();
        int cbStride = cCb.W2;

        JpegHuffmanTable crHdc = ws.HuffDc[cCr.Hd];
        JpegHuffmanTable crHac = ws.HuffAc[cCr.Ha];
        int[] crFdc = ws.FastDc[cCr.Hd];
        int[] crFac = ws.FastAc[cCr.Ha];
        ReadOnlySpan<ushort> crDq = ws.GetDequantSpan(cCr.Tq);
        Span<byte> crData = cCr.Data.AsSpan();
        int crStride = cCr.W2;

        Span<short> b0 = stackalloc short[64];
        Span<short> b1 = stackalloc short[64];

        int mcuX = ws.ImgMcuX;
        int mcuY = ws.ImgMcuY;

        for (int j = 0; j < mcuY; j++)
        {
            int yRow0 = j * 16 * yStride;
            int yRow1 = yRow0 + (8 * yStride);
            int cbRow = j * 8 * cbStride;
            int crRow = j * 8 * crStride;

            for (int i = 0; i < mcuX; i++)
            {
                int yCol = i * 16;
                int cCol = i * 8;

                // Y top pair (Y0, Y1)
                bool y0Dc = DecodeBlockBaseline(ref reader, b0, yHdc, yHac, yFdc, yFac, ref cY.DcPred, yDq, out short y0S);
                bool y1Dc = DecodeBlockBaseline(ref reader, b1, yHdc, yHac, yFdc, yFac, ref cY.DcPred, yDq, out short y1S);
                DispatchTwoBlocks(yData, yRow0 + yCol, yStride, b0, y0Dc, y0S, yData, yRow0 + yCol + 8, yStride, b1, y1Dc, y1S);

                // Y bottom pair (Y2, Y3)
                bool y2Dc = DecodeBlockBaseline(ref reader, b0, yHdc, yHac, yFdc, yFac, ref cY.DcPred, yDq, out short y2S);
                bool y3Dc = DecodeBlockBaseline(ref reader, b1, yHdc, yHac, yFdc, yFac, ref cY.DcPred, yDq, out short y3S);
                DispatchTwoBlocks(yData, yRow1 + yCol, yStride, b0, y2Dc, y2S, yData, yRow1 + yCol + 8, yStride, b1, y3Dc, y3S);

                // Chroma pair (Cb, Cr)
                bool cbDc = DecodeBlockBaseline(ref reader, b0, cbHdc, cbHac, cbFdc, cbFac, ref cCb.DcPred, cbDq, out short cbS);
                bool crDc = DecodeBlockBaseline(ref reader, b1, crHdc, crHac, crFdc, crFac, ref cCr.DcPred, crDq, out short crS);
                DispatchTwoBlocks(cbData, cbRow + cCol, cbStride, b0, cbDc, cbS, crData, crRow + cCol, crStride, b1, crDc, crS);

                if (--ws.Todo <= 0)
                {
                    if (reader.Marker == JpegTables.MarkerNone && reader.CodeBits < 24)
                    {
                        reader.GrowBufferUnsafe();
                    }

                    if (!IsRestart(reader.Marker))
                    {
                        return;
                    }

                    ResetEntropyState(ref reader, ws);
                }
            }
        }
    }

    private static bool TryParseBaselineDriParallel(ref JpegReader reader, JpegWorkspace ws, int driDop)
    {
        if (reader.Marker != JpegTables.MarkerNone || reader.CodeBits != 0)
        {
            return false;
        }

        // Validate all Huffman tables referenced by this scan before spawning workers.
        for (int k = 0; k < ws.ScanN; k++)
        {
            int n = ws.Order[k];
            if (!ws.HuffDc[ws.Components[n].Hd].IsDefined || !ws.HuffAc[ws.Components[n].Ha].IsDefined)
            {
                throw new StbException(StbErrorCode.InvalidData, "JPEG scan references an undefined Huffman table.");
            }
        }

        int totalMcus;
        if (ws.ScanN == 1)
        {
            ref JpegComponent c0 = ref ws.Components[ws.Order[0]];
            int w = (c0.X + 7) >> 3;
            int h = (c0.Y + 7) >> 3;
            totalMcus = w * h;
        }
        else
        {
            totalMcus = ws.ImgMcuX * ws.ImgMcuY;
        }

        int rst = ws.RestartInterval;
        if (totalMcus <= rst)
        {
            return false;
        }

        int numIntervals = (totalMcus + rst - 1) / rst;
        if (numIntervals < 2)
        {
            return false;
        }

        ReadOnlySpan<byte> tail = reader.RemainingSpan;
        int[] bounds = ArrayPool<int>.Shared.Rent(numIntervals * 2);
        try
        {
            int p = 0;
            int iv = 0;
            int expectedRst = 0;
            bool foundEnd = false;
            int scanEndOffset = 0;
            byte endMarker = JpegTables.MarkerNone;

            bounds[0] = 0;
            while (p < tail.Length)
            {
                int rel = tail.Slice(p).IndexOf((byte)0xFF);
                if (rel < 0)
                {
                    break;
                }

                p += rel + 1;
                while (p < tail.Length && tail[p] == 0xFF)
                {
                    p++;
                }

                if (p >= tail.Length)
                {
                    break;
                }

                byte marker = tail[p++];
                if (marker == 0x00)
                {
                    continue;
                }

                if (IsRestart(marker))
                {
                    if (marker != (byte)(0xD0 + (expectedRst & 7)) || iv + 1 >= numIntervals)
                    {
                        return false;
                    }

                    bounds[numIntervals + iv] = p;
                    iv++;
                    bounds[iv] = p;
                    expectedRst++;
                    continue;
                }

                if (iv != numIntervals - 1)
                {
                    return false;
                }

                bounds[numIntervals + iv] = p;
                scanEndOffset = p;
                endMarker = marker;
                foundEnd = true;
                break;
            }

            if (!foundEnd || iv != numIntervals - 1 || scanEndOffset <= 0)
            {
                return false;
            }

            byte[] scanBuf = ws.EnsureScanBuffer(scanEndOffset);
            tail.Slice(0, scanEndOffset).CopyTo(scanBuf);

            bool isSingle = ws.ScanN == 1;
            bool is420 = ws.ScanN == 3
                && ws.Order[0] == 0 && ws.Order[1] == 1 && ws.Order[2] == 2
                && ws.Components[0].H == 2 && ws.Components[0].V == 2
                && ws.Components[1].H == 1 && ws.Components[1].V == 1
                && ws.Components[2].H == 1 && ws.Components[2].V == 1;

            int workers = Math.Min(driDop, numIntervals);
            ParallelOptions parallelOptions = new() { MaxDegreeOfParallelism = workers };

            try
            {
                Parallel.For(0, workers, parallelOptions, wIdx =>
                {
                    int ivStart = (int)((long)wIdx * numIntervals / workers);
                    int ivEnd = (int)((long)(wIdx + 1) * numIntervals / workers);
                    if (ivStart >= ivEnd)
                    {
                        return;
                    }

                    if (is420)
                    {
                        DecodeDriChunk420(ws, scanBuf, bounds, numIntervals, ivStart, ivEnd, rst, totalMcus);
                    }
                    else if (isSingle)
                    {
                        DecodeDriChunkNonInterleaved(ws, scanBuf, bounds, numIntervals, ivStart, ivEnd, rst, totalMcus);
                    }
                    else
                    {
                        DecodeDriChunkGenericInterleaved(ws, scanBuf, bounds, numIntervals, ivStart, ivEnd, rst, totalMcus);
                    }
                });
            }
            catch (AggregateException ae)
            {
                ThrowUnwrapped(ae);
            }

            reader.Advance(scanEndOffset);
            reader.CodeBuffer = 0;
            reader.CodeBits = 0;
            reader.Marker = endMarker;
            reader.NoMore = true;
            return true;
        }
        finally
        {
            ArrayPool<int>.Shared.Return(bounds);
        }
    }

    private static void DecodeDriChunk420(
        JpegWorkspace ws,
        byte[] scanBuf,
        int[] bounds,
        int numIntervals,
        int ivStart,
        int ivEnd,
        int rst,
        int totalMcus)
    {
        ref JpegComponent cY = ref ws.Components[0];
        ref JpegComponent cCb = ref ws.Components[1];
        ref JpegComponent cCr = ref ws.Components[2];

        JpegHuffmanTable yHdc = ws.HuffDc[cY.Hd];
        JpegHuffmanTable yHac = ws.HuffAc[cY.Ha];
        int[] yFdc = ws.FastDc[cY.Hd];
        int[] yFac = ws.FastAc[cY.Ha];
        ReadOnlySpan<ushort> yDq = ws.GetDequantSpan(cY.Tq);
        Span<byte> yData = cY.Data.AsSpan();
        int yStride = cY.W2;

        JpegHuffmanTable cbHdc = ws.HuffDc[cCb.Hd];
        JpegHuffmanTable cbHac = ws.HuffAc[cCb.Ha];
        int[] cbFdc = ws.FastDc[cCb.Hd];
        int[] cbFac = ws.FastAc[cCb.Ha];
        ReadOnlySpan<ushort> cbDq = ws.GetDequantSpan(cCb.Tq);
        Span<byte> cbData = cCb.Data.AsSpan();
        int cbStride = cCb.W2;

        JpegHuffmanTable crHdc = ws.HuffDc[cCr.Hd];
        JpegHuffmanTable crHac = ws.HuffAc[cCr.Ha];
        int[] crFdc = ws.FastDc[cCr.Hd];
        int[] crFac = ws.FastAc[cCr.Ha];
        ReadOnlySpan<ushort> crDq = ws.GetDequantSpan(cCr.Tq);
        Span<byte> crData = cCr.Data.AsSpan();
        int crStride = cCr.W2;

        Span<short> b0 = stackalloc short[64];
        Span<short> b1 = stackalloc short[64];
        int mcuX = ws.ImgMcuX;

        for (int iv = ivStart; iv < ivEnd; iv++)
        {
            int start = bounds[iv];
            int end = bounds[numIntervals + iv];
            JpegReader localReader = new(scanBuf.AsSpan(start, end - start));

            int yDc = 0;
            int cbDc = 0;
            int crDc = 0;

            int mcuStart = iv * rst;
            int mcuEnd = Math.Min(totalMcus, mcuStart + rst);
            int j = mcuStart / mcuX;
            int i = mcuStart - (j * mcuX);

            int yRow0 = j * 16 * yStride;
            int yRow1 = yRow0 + (8 * yStride);
            int cbRow = j * 8 * cbStride;
            int crRow = j * 8 * crStride;

            for (int m = mcuStart; m < mcuEnd; m++)
            {
                int yCol = i * 16;
                int cCol = i * 8;

                bool y0Dc = DecodeBlockBaseline(ref localReader, b0, yHdc, yHac, yFdc, yFac, ref yDc, yDq, out short y0S);
                bool y1Dc = DecodeBlockBaseline(ref localReader, b1, yHdc, yHac, yFdc, yFac, ref yDc, yDq, out short y1S);
                DispatchTwoBlocks(yData, yRow0 + yCol, yStride, b0, y0Dc, y0S, yData, yRow0 + yCol + 8, yStride, b1, y1Dc, y1S);

                bool y2Dc = DecodeBlockBaseline(ref localReader, b0, yHdc, yHac, yFdc, yFac, ref yDc, yDq, out short y2S);
                bool y3Dc = DecodeBlockBaseline(ref localReader, b1, yHdc, yHac, yFdc, yFac, ref yDc, yDq, out short y3S);
                DispatchTwoBlocks(yData, yRow1 + yCol, yStride, b0, y2Dc, y2S, yData, yRow1 + yCol + 8, yStride, b1, y3Dc, y3S);

                bool cbDcOnly = DecodeBlockBaseline(ref localReader, b0, cbHdc, cbHac, cbFdc, cbFac, ref cbDc, cbDq, out short cbS);
                bool crDcOnly = DecodeBlockBaseline(ref localReader, b1, crHdc, crHac, crFdc, crFac, ref crDc, crDq, out short crS);
                DispatchTwoBlocks(cbData, cbRow + cCol, cbStride, b0, cbDcOnly, cbS, crData, crRow + cCol, crStride, b1, crDcOnly, crS);

                if (++i == mcuX)
                {
                    i = 0;
                    j++;
                    yRow0 = j * 16 * yStride;
                    yRow1 = yRow0 + (8 * yStride);
                    cbRow = j * 8 * cbStride;
                    crRow = j * 8 * crStride;
                }
            }
        }
    }

    private static void DecodeDriChunkNonInterleaved(
        JpegWorkspace ws,
        byte[] scanBuf,
        int[] bounds,
        int numIntervals,
        int ivStart,
        int ivEnd,
        int rst,
        int totalMcus)
    {
        ref JpegComponent comp = ref ws.Components[ws.Order[0]];
        JpegHuffmanTable hdc = ws.HuffDc[comp.Hd];
        JpegHuffmanTable hac = ws.HuffAc[comp.Ha];
        int[] fdc = ws.FastDc[comp.Hd];
        int[] fac = ws.FastAc[comp.Ha];
        ReadOnlySpan<ushort> dq = ws.GetDequantSpan(comp.Tq);
        Span<byte> compData = comp.Data.AsSpan();
        int stride = comp.W2;
        int w = (comp.X + 7) >> 3;

        Span<short> b0 = stackalloc short[64];
        Span<short> b1 = stackalloc short[64];

        for (int iv = ivStart; iv < ivEnd; iv++)
        {
            int start = bounds[iv];
            int end = bounds[numIntervals + iv];
            JpegReader localReader = new(scanBuf.AsSpan(start, end - start));

            int dcPred = 0;
            int mcuStart = iv * rst;
            int mcuEnd = Math.Min(totalMcus, mcuStart + rst);
            int j = mcuStart / w;
            int i = mcuStart - (j * w);
            int rowOffset = stride * (j * 8);

            int m = mcuStart;
            while (m < mcuEnd)
            {
                if (i + 1 < w && m + 1 < mcuEnd)
                {
                    bool dc0 = DecodeBlockBaseline(ref localReader, b0, hdc, hac, fdc, fac, ref dcPred, dq, out short s0);
                    bool dc1 = DecodeBlockBaseline(ref localReader, b1, hdc, hac, fdc, fac, ref dcPred, dq, out short s1);
                    int off0 = rowOffset + (i * 8);
                    DispatchTwoBlocks(compData, off0, stride, b0, dc0, s0, compData, off0 + 8, stride, b1, dc1, s1);
                    i += 2;
                    m += 2;
                }
                else
                {
                    bool dc0 = DecodeBlockBaseline(ref localReader, b0, hdc, hac, fdc, fac, ref dcPred, dq, out short s0);
                    int off0 = rowOffset + (i * 8);
                    if (dc0)
                    {
                        JpegIdct.FillDcBlock(compData, off0, stride, s0);
                    }
                    else
                    {
                        JpegIdct.IdctBlock(compData, off0, stride, b0);
                    }

                    i++;
                    m++;
                }

                if (i == w)
                {
                    i = 0;
                    j++;
                    rowOffset = stride * (j * 8);
                }
            }
        }
    }

    private static void DecodeDriChunkGenericInterleaved(
        JpegWorkspace ws,
        byte[] scanBuf,
        int[] bounds,
        int numIntervals,
        int ivStart,
        int ivEnd,
        int rst,
        int totalMcus)
    {
        Span<short> b0 = stackalloc short[64];
        Span<short> b1 = stackalloc short[64];
        Span<int> dcPreds = stackalloc int[4];
        int mcuX = ws.ImgMcuX;

        for (int iv = ivStart; iv < ivEnd; iv++)
        {
            int start = bounds[iv];
            int end = bounds[numIntervals + iv];
            JpegReader localReader = new(scanBuf.AsSpan(start, end - start));
            dcPreds.Clear();

            int mcuStart = iv * rst;
            int mcuEnd = Math.Min(totalMcus, mcuStart + rst);
            int j = mcuStart / mcuX;
            int i = mcuStart - (j * mcuX);

            for (int m = mcuStart; m < mcuEnd; m++)
            {
                for (int k = 0; k < ws.ScanN; k++)
                {
                    int n = ws.Order[k];
                    ref JpegComponent comp = ref ws.Components[n];
                    JpegHuffmanTable hdc = ws.HuffDc[comp.Hd];
                    JpegHuffmanTable hac = ws.HuffAc[comp.Ha];
                    int[] fdc = ws.FastDc[comp.Hd];
                    int[] fac = ws.FastAc[comp.Ha];
                    ReadOnlySpan<ushort> dq = ws.GetDequantSpan(comp.Tq);
                    Span<byte> compData = comp.Data.AsSpan();
                    int stride = comp.W2;

                    for (int y = 0; y < comp.V; y++)
                    {
                        int rowOffset = stride * (((j * comp.V) + y) * 8);
                        int x = 0;
                        while (x + 1 < comp.H)
                        {
                            int x2 = ((i * comp.H) + x) * 8;
                            bool dc0 = DecodeBlockBaseline(ref localReader, b0, hdc, hac, fdc, fac, ref dcPreds[n], dq, out short s0);
                            bool dc1 = DecodeBlockBaseline(ref localReader, b1, hdc, hac, fdc, fac, ref dcPreds[n], dq, out short s1);
                            DispatchTwoBlocks(compData, rowOffset + x2, stride, b0, dc0, s0, compData, rowOffset + x2 + 8, stride, b1, dc1, s1);
                            x += 2;
                        }

                        if (x < comp.H)
                        {
                            int x2 = ((i * comp.H) + x) * 8;
                            bool dc0 = DecodeBlockBaseline(ref localReader, b0, hdc, hac, fdc, fac, ref dcPreds[n], dq, out short s0);
                            if (dc0)
                            {
                                JpegIdct.FillDcBlock(compData, rowOffset + x2, stride, s0);
                            }
                            else
                            {
                                JpegIdct.IdctBlock(compData, rowOffset + x2, stride, b0);
                            }
                        }
                    }
                }

                if (++i == mcuX)
                {
                    i = 0;
                    j++;
                }
            }
        }
    }

    private static void ThrowUnwrapped(AggregateException ae)
    {
        AggregateException flat = ae.Flatten();
        for (int i = 0; i < flat.InnerExceptions.Count; i++)
        {
            if (flat.InnerExceptions[i] is StbException stbEx)
            {
                ExceptionDispatchInfo.Capture(stbEx).Throw();
            }
        }

        if (flat.InnerExceptions.Count > 0)
        {
            ExceptionDispatchInfo.Capture(flat.InnerExceptions[0]).Throw();
        }

        throw ae;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void DispatchTwoBlocks(
        Span<byte> out0,
        int off0,
        int stride0,
        scoped ReadOnlySpan<short> b0,
        bool dc0,
        short s0,
        Span<byte> out1,
        int off1,
        int stride1,
        scoped ReadOnlySpan<short> b1,
        bool dc1,
        short s1)
    {
        if (!dc0 && !dc1)
        {
            JpegIdct.IdctTwoBlocks(out0, off0, stride0, b0, out1, off1, stride1, b1);
        }
        else if (dc0 && dc1)
        {
            JpegIdct.FillDcBlock(out0, off0, stride0, s0);
            JpegIdct.FillDcBlock(out1, off1, stride1, s1);
        }
        else if (dc0)
        {
            JpegIdct.FillDcBlock(out0, off0, stride0, s0);
            JpegIdct.IdctBlock(out1, off1, stride1, b1);
        }
        else
        {
            JpegIdct.IdctBlock(out0, off0, stride0, b0);
            JpegIdct.FillDcBlock(out1, off1, stride1, s1);
        }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static bool DecodeBlockBaseline(
        ref JpegReader reader,
        scoped Span<short> data,
        JpegHuffmanTable hdc,
        JpegHuffmanTable hac,
        int[] fdc,
        int[] fac,
        ref int dcPred,
        scoped ReadOnlySpan<ushort> dequant,
        out short dcScaledOut)
    {
        // Fixed 64-entry views let the JIT drop range checks on the masked zig-zag indices below.
        data = data.Slice(0, 64);
        dequant = dequant.Slice(0, 64);
        ulong cb = reader.CodeBuffer;
        int bits = reader.CodeBits;
        if (bits < 16)
        {
            reader.CodeBuffer = cb;
            reader.CodeBits = bits;
            reader.GrowBufferUnsafe();
            cb = reader.CodeBuffer;
            bits = reader.CodeBits;
        }

        // 11-bit fast DC lookup
        int dcEntry = fdc[(int)(cb >> (64 - JpegTables.FastBits))];
        int dcLen = dcEntry & 0xFF;
        int diff;
        if (dcLen != 0 && dcLen <= bits)
        {
            diff = dcEntry >> 16;
            cb <<= dcLen;
            bits -= dcLen;
        }
        else
        {
            reader.CodeBuffer = cb;
            reader.CodeBits = bits;
            int t = reader.HuffDecode(hdc);
            if ((uint)t > 15u)
            {
                throw new StbException(StbErrorCode.CorruptStream, "Invalid JPEG DC Huffman code.");
            }

            diff = t != 0 ? reader.ExtendReceive(t) : 0;
            cb = reader.CodeBuffer;
            bits = reader.CodeBits;
        }

        long dcLong = (long)dcPred + diff;
        if (dcLong < int.MinValue || dcLong > int.MaxValue)
        {
            throw new StbException(StbErrorCode.CorruptStream, "JPEG DC delta overflow.");
        }

        int dc = (int)dcLong;
        dcPred = dc;

        long dcScaled = dcLong * dequant[0];
        if (dcScaled < short.MinValue || dcScaled > short.MaxValue)
        {
            throw new StbException(StbErrorCode.CorruptStream, "JPEG DC coefficient overflow.");
        }

        dcScaledOut = (short)dcScaled;

        // Fast DC-only check BEFORE clearing 128 bytes of data[]
        if (bits < 16)
        {
            reader.CodeBuffer = cb;
            reader.CodeBits = bits;
            reader.GrowBufferUnsafe();
            cb = reader.CodeBuffer;
            bits = reader.CodeBits;
        }

        int entry = fac[(int)(cb >> (64 - JpegTables.FastBits))];
        if ((entry & 0xFFFFFF00) == (JpegTables.FastAcEobRun << 8))
        {
            int eobLen = entry & 0xFF;
            if (eobLen <= bits)
            {
                reader.CodeBuffer = cb << eobLen;
                reader.CodeBits = bits - eobLen;
                return true;
            }
        }

        data.Clear();
        data[0] = dcScaledOut;

        ReadOnlySpan<byte> dezigzag = JpegTables.Dezigzag;
        int k = 1;
        for (; ; )
        {
            if (entry != 0)
            {
                int s = entry & 0xFF;
                int val = entry >> 16;
                if (s <= bits)
                {
                    cb <<= s;
                    bits -= s;
                    if (val != 0)
                    {
                        k += (entry >> 8) & 0xFF;
                        int zig = dezigzag[k++] & 63;
                        data[zig] = unchecked((short)(val * dequant[zig]));
                        if (k >= 64)
                        {
                            break;
                        }

                        if (bits < 16)
                        {
                            reader.CodeBuffer = cb;
                            reader.CodeBits = bits;
                            reader.GrowBufferUnsafe();
                            cb = reader.CodeBuffer;
                            bits = reader.CodeBits;
                        }

                        entry = fac[(int)(cb >> (64 - JpegTables.FastBits))];
                        continue;
                    }

                    if (((entry >> 8) & 0xFF) == JpegTables.FastAcEobRun)
                    {
                        break;
                    }

                    k += 16;
                    if (k >= 64)
                    {
                        break;
                    }

                    if (bits < 16)
                    {
                        reader.CodeBuffer = cb;
                        reader.CodeBits = bits;
                        reader.GrowBufferUnsafe();
                        cb = reader.CodeBuffer;
                        bits = reader.CodeBits;
                    }

                    entry = fac[(int)(cb >> (64 - JpegTables.FastBits))];
                    continue;
                }

                if (s <= 9 || val == 0)
                {
                    reader.CodeBuffer = cb;
                    reader.CodeBits = bits;
                    throw new StbException(StbErrorCode.CorruptStream, "JPEG fast-AC code exceeds remaining bits.");
                }
            }

            reader.CodeBuffer = cb;
            reader.CodeBits = bits;
            int rs = reader.HuffDecode(hac);
            if (rs < 0)
            {
                throw new StbException(StbErrorCode.CorruptStream, "Invalid JPEG AC Huffman code.");
            }

            int magBits = rs & 15;
            int run = rs >> 4;
            if (magBits == 0)
            {
                if (rs != 0xF0)
                {
                    cb = reader.CodeBuffer;
                    bits = reader.CodeBits;
                    break;
                }

                k += 16;
            }
            else
            {
                k += run;
                int zig = dezigzag[k++] & 63;
                data[zig] = unchecked((short)(reader.ExtendReceive(magBits) * dequant[zig]));
            }

            cb = reader.CodeBuffer;
            bits = reader.CodeBits;
            if (k >= 64)
            {
                break;
            }

            if (bits < 16)
            {
                reader.CodeBuffer = cb;
                reader.CodeBits = bits;
                reader.GrowBufferUnsafe();
                cb = reader.CodeBuffer;
                bits = reader.CodeBits;
            }

            entry = fac[(int)(cb >> (64 - JpegTables.FastBits))];
        }

        reader.CodeBuffer = cb;
        reader.CodeBits = bits;
        return false;
    }

    private static void DecodeBlockProgDc(
        ref JpegReader reader,
        Span<short> data,
        JpegHuffmanTable hdc,
        int[] fdc,
        ref int dcPred,
        int succHigh,
        int succLow)
    {
        if (reader.CodeBits < 16)
        {
            reader.GrowBufferUnsafe();
        }

        if (succHigh == 0)
        {
            int dcEntry = fdc[(int)(reader.CodeBuffer >> (64 - JpegTables.FastBits))];
            int dcLen = dcEntry & 0xFF;
            int diff;
            if (dcLen != 0 && dcLen <= reader.CodeBits)
            {
                diff = dcEntry >> 16;
                reader.ConsumeBits(dcLen);
            }
            else
            {
                int t = reader.HuffDecode(hdc);
                if ((uint)t > 15u)
                {
                    throw new StbException(StbErrorCode.CorruptStream, "Invalid progressive JPEG DC Huffman code.");
                }

                diff = t != 0 ? reader.ExtendReceive(t) : 0;
            }

            long dcLong = (long)dcPred + diff;
            if (dcLong < int.MinValue || dcLong > int.MaxValue)
            {
                throw new StbException(StbErrorCode.CorruptStream, "Progressive JPEG DC delta overflow.");
            }

            int dc = (int)dcLong;
            dcPred = dc;

            long scaled = dcLong * (1 << succLow);
            if (scaled < short.MinValue || scaled > short.MaxValue)
            {
                throw new StbException(StbErrorCode.CorruptStream, "Progressive JPEG DC coefficient overflow.");
            }

            data[0] = (short)scaled;
        }
        else
        {
            if (reader.GetBit())
            {
                data[0] = unchecked((short)(data[0] + (1 << succLow)));
            }
        }
    }

    private static void DecodeBlockProgAc(
        ref JpegReader reader,
        JpegWorkspace ws,
        Span<short> data,
        JpegHuffmanTable hac,
        int[] fac)
    {
        data = data.Slice(0, 64);
        ReadOnlySpan<byte> dezigzag = JpegTables.Dezigzag;
        int specStart = ws.SpecStart;
        int specEnd = ws.SpecEnd;

        if (ws.SuccHigh == 0)
        {
            if (ws.EobRun != 0)
            {
                ws.EobRun--;
                return;
            }

            int scale = 1 << ws.SuccLow;
            ulong cb = reader.CodeBuffer;
            int bits = reader.CodeBits;
            int k = specStart;
            do
            {
                if (bits < 16)
                {
                    reader.CodeBuffer = cb;
                    reader.CodeBits = bits;
                    reader.GrowBufferUnsafe();
                    cb = reader.CodeBuffer;
                    bits = reader.CodeBits;
                }

                int entry = fac[(int)(cb >> (64 - JpegTables.FastBits))];
                int val = entry >> 16;
                if (val != 0)
                {
                    int s = entry & 0xFF;
                    if (s <= bits)
                    {
                        cb <<= s;
                        bits -= s;
                        k += (entry >> 8) & 0xFF;
                        int zig = dezigzag[k++] & 63;
                        data[zig] = unchecked((short)(val * scale));
                        continue;
                    }

                    if (s <= 9)
                    {
                        reader.CodeBuffer = cb;
                        reader.CodeBits = bits;
                        throw new StbException(StbErrorCode.CorruptStream, "Progressive fast-AC code exceeds remaining bits.");
                    }
                }

                reader.CodeBuffer = cb;
                reader.CodeBits = bits;
                int rs = reader.HuffDecode(hac);
                if (rs < 0)
                {
                    throw new StbException(StbErrorCode.CorruptStream, "Invalid progressive JPEG AC Huffman code.");
                }

                int magBits = rs & 15;
                int run = rs >> 4;
                if (magBits == 0)
                {
                    if (run < 15)
                    {
                        ws.EobRun = 1 << run;
                        if (run != 0)
                        {
                            ws.EobRun += reader.GetBits(run);
                        }

                        ws.EobRun--;
                        return;
                    }

                    k += 16;
                }
                else
                {
                    k += run;
                    int zig = dezigzag[k++] & 63;
                    data[zig] = unchecked((short)(reader.ExtendReceive(magBits) * scale));
                }

                cb = reader.CodeBuffer;
                bits = reader.CodeBits;
            }
            while (k <= specEnd);

            reader.CodeBuffer = cb;
            reader.CodeBits = bits;
        }
        else
        {
            short bit = (short)(1 << ws.SuccLow);
            ulong cb = reader.CodeBuffer;
            int bits = reader.CodeBits;
            if (ws.EobRun != 0)
            {
                ws.EobRun--;
                for (int k = specStart; k <= specEnd; k++)
                {
                    ref short p = ref data[dezigzag[k] & 63];
                    if (p != 0)
                    {
                        if (bits < 1)
                        {
                            reader.CodeBuffer = cb;
                            reader.CodeBits = bits;
                            reader.GrowBufferUnsafe();
                            cb = reader.CodeBuffer;
                            bits = reader.CodeBits;
                        }

                        if (bits >= 1)
                        {
                            ulong top = cb;
                            cb <<= 1;
                            bits--;
                            if ((top & 0x8000000000000000UL) != 0 && (p & bit) == 0)
                            {
                                p = p > 0 ? unchecked((short)(p + bit)) : unchecked((short)(p - bit));
                            }
                        }
                    }
                }

                reader.CodeBuffer = cb;
                reader.CodeBits = bits;
            }
            else
            {
                int k = specStart;
                do
                {
                    reader.CodeBuffer = cb;
                    reader.CodeBits = bits;
                    int rs = reader.HuffDecode(hac);
                    if (rs < 0)
                    {
                        throw new StbException(StbErrorCode.CorruptStream, "Invalid progressive AC refinement Huffman code.");
                    }

                    int s = rs & 15;
                    int run = rs >> 4;
                    if (s == 0)
                    {
                        if (run < 15)
                        {
                            ws.EobRun = (1 << run) - 1;
                            if (run != 0)
                            {
                                ws.EobRun += reader.GetBits(run);
                            }

                            run = 64; // force end of block
                        }
                    }
                    else
                    {
                        if (s != 1)
                        {
                            throw new StbException(StbErrorCode.CorruptStream, "Invalid progressive AC refinement symbol.");
                        }

                        s = reader.GetBit() ? bit : -bit;
                    }

                    cb = reader.CodeBuffer;
                    bits = reader.CodeBits;

                    while (k <= specEnd)
                    {
                        ref short p = ref data[dezigzag[k++] & 63];
                        if (p != 0)
                        {
                            if (bits < 1)
                            {
                                reader.CodeBuffer = cb;
                                reader.CodeBits = bits;
                                reader.GrowBufferUnsafe();
                                cb = reader.CodeBuffer;
                                bits = reader.CodeBits;
                            }

                            if (bits >= 1)
                            {
                                ulong top = cb;
                                cb <<= 1;
                                bits--;
                                if ((top & 0x8000000000000000UL) != 0 && (p & bit) == 0)
                                {
                                    p = p > 0 ? unchecked((short)(p + bit)) : unchecked((short)(p - bit));
                                }
                            }
                        }
                        else
                        {
                            if (run == 0)
                            {
                                p = (short)s;
                                break;
                            }

                            run--;
                        }
                    }
                }
                while (k <= specEnd);

                reader.CodeBuffer = cb;
                reader.CodeBits = bits;
            }
        }
    }

    private static void FinishProgressive(JpegWorkspace ws, int dop)
    {
        if (dop <= 1)
        {
            for (int n = 0; n < ws.ImgN; n++)
            {
                FinishProgressiveComponentRows(ws, n, 0, (ws.Components[n].Y + 7) >> 3);
            }

            return;
        }

        int maxBlockRows = 0;
        for (int n = 0; n < ws.ImgN; n++)
        {
            int h = (ws.Components[n].Y + 7) >> 3;
            if (h > maxBlockRows)
            {
                maxBlockRows = h;
            }
        }

        int workers = Math.Min(dop, maxBlockRows);
        if (workers <= 1)
        {
            for (int n = 0; n < ws.ImgN; n++)
            {
                FinishProgressiveComponentRows(ws, n, 0, (ws.Components[n].Y + 7) >> 3);
            }

            return;
        }

        ParallelOptions parallelOptions = new() { MaxDegreeOfParallelism = workers };
        try
        {
            Parallel.For(0, workers, parallelOptions, wIdx =>
            {
                for (int n = 0; n < ws.ImgN; n++)
                {
                    int h = (ws.Components[n].Y + 7) >> 3;
                    int rStart = (int)((long)wIdx * h / workers);
                    int rEnd = (int)((long)(wIdx + 1) * h / workers);
                    if (rStart < rEnd)
                    {
                        FinishProgressiveComponentRows(ws, n, rStart, rEnd);
                    }
                }
            });
        }
        catch (AggregateException ae)
        {
            ThrowUnwrapped(ae);
        }
    }

    private static void FinishProgressiveComponentRows(JpegWorkspace ws, int compIndex, int rowStart, int rowEnd)
    {
        ref JpegComponent comp = ref ws.Components[compIndex];
        int w = (comp.X + 7) >> 3;
        short[] coeff = comp.Coeff!;
        int coeffW = comp.CoeffW;
        ReadOnlySpan<ushort> dq = ws.GetDequantSpan(comp.Tq);
        Span<byte> compData = comp.Data.AsSpan();
        int stride = comp.W2;

        for (int j = rowStart; j < rowEnd; j++)
        {
            int rowOffset = stride * (j * 8);
            int coeffRow = j * coeffW;
            int i = 0;
            for (; i + 1 < w; i += 2)
            {
                int idx0 = 64 * (i + coeffRow);
                JpegIdct.DequantAndIdctTwoBlocks(
                    compData, rowOffset + (i * 8), stride, coeff.AsSpan(idx0, 64),
                    compData, rowOffset + ((i + 1) * 8), stride, coeff.AsSpan(idx0 + 64, 64),
                    dq);
            }

            if (i < w)
            {
                int idx = 64 * (i + coeffRow);
                JpegIdct.DequantAndIdctBlock(compData, rowOffset + (i * 8), stride, coeff.AsSpan(idx, 64), dq);
            }
        }
    }

    private static ResampleState CreateResampleState(JpegWorkspace ws, int k, int width, int startRow)
    {
        ref JpegComponent comp = ref ws.Components[k];
        int hs = ws.ImgHMax / comp.H;
        int vs = ws.ImgVMax / comp.V;
        ResampleMode mode = (hs, vs) switch
        {
            (1, 1) => ResampleMode.None,
            (1, 2) => ResampleMode.V2,
            (2, 1) => ResampleMode.H2,
            (2, 2) => ResampleMode.Hv2,
            _ => ResampleMode.Generic,
        };

        int t = (vs >> 1) + startRow;
        int rolls = t / vs;
        int maxRow = comp.Y > 0 ? comp.Y - 1 : 0;
        int line1Row = rolls < maxRow ? rolls : maxRow;
        int line0Row = rolls == 0 ? 0 : ((rolls - 1) < maxRow ? (rolls - 1) : maxRow);

        return new ResampleState
        {
            Mode = mode,
            Hs = hs,
            Vs = vs,
            WLores = (width + hs - 1) / hs,
            YStep = t % vs,
            YPos = rolls,
            Line0Offset = line0Row * comp.W2,
            Line1Offset = line1Row * comp.W2,
        };
    }

    private static void ResampleAndColorConvertParallelToArray(
        JpegWorkspace ws,
        byte[] output,
        int outN,
        bool flipVertically,
        int dop)
    {
        ResampleAndColorConvertParallelRange(ws, output, 0, ws.Height, outN, flipVertically, dop);
    }

    private static void ResampleAndColorConvertParallelRange(
        JpegWorkspace ws,
        byte[] dstArray,
        int rangeStartRow,
        int rangeEndRow,
        int outN,
        bool flipVertically,
        int dop)
    {
        int rangeRows = rangeEndRow - rangeStartRow;
        int workers = Math.Min(dop, Math.Max(1, rangeRows / 8));
        if (workers <= 1)
        {
            ResampleStripToArray(ws, dstArray, rangeStartRow, rangeEndRow, rangeStartRow, rangeEndRow, outN, flipVertically);
            return;
        }

        ParallelOptions parallelOptions = new() { MaxDegreeOfParallelism = workers };
        try
        {
            Parallel.For(0, workers, parallelOptions, wIdx =>
            {
                int rStart = rangeStartRow + (int)((long)wIdx * rangeRows / workers);
                int rEnd = rangeStartRow + (int)((long)(wIdx + 1) * rangeRows / workers);
                if (rStart < rEnd)
                {
                    ResampleStripToArray(ws, dstArray, rStart, rEnd, rangeStartRow, rangeEndRow, outN, flipVertically);
                }
            });
        }
        catch (AggregateException ae)
        {
            ThrowUnwrapped(ae);
        }
    }

    private static void ResampleStripToArray(
        JpegWorkspace ws,
        byte[] dstArray,
        int startRow,
        int endRow,
        int rangeStartRow,
        int rangeEndRow,
        int outN,
        bool flipVertically)
    {
        bool isRgb = ws.ImgN == 3 && (ws.RgbTagCount == 3 || (ws.App14ColorTransform == 0 && !ws.Jfif));
        int decodeN = (ws.ImgN == 3 && outN < 3 && !isRgb) ? 1 : ws.ImgN;
        int width = ws.Width;
        int lineBufLen = width + 3;

        ResampleState r0 = decodeN > 0 ? CreateResampleState(ws, 0, width, startRow) : default;
        ResampleState r1 = decodeN > 1 ? CreateResampleState(ws, 1, width, startRow) : default;
        ResampleState r2 = decodeN > 2 ? CreateResampleState(ws, 2, width, startRow) : default;
        ResampleState r3 = decodeN > 3 ? CreateResampleState(ws, 3, width, startRow) : default;

        bool need0 = decodeN > 0 && r0.Mode != ResampleMode.None;
        bool need1 = decodeN > 1 && r1.Mode != ResampleMode.None;
        bool need2 = decodeN > 2 && r2.Mode != ResampleMode.None;
        bool need3 = decodeN > 3 && r3.Mode != ResampleMode.None;
        int neededCount = (need0 ? 1 : 0) + (need1 ? 1 : 0) + (need2 ? 1 : 0) + (need3 ? 1 : 0);

        byte[]? scratch = neededCount > 0 ? ArrayPool<byte>.Shared.Rent(neededCount * lineBufLen) : null;
        try
        {
            int off = 0;
            Span<byte> lb0 = need0 ? scratch.AsSpan(off, lineBufLen) : Span<byte>.Empty;
            if (need0)
            {
                off += lineBufLen;
            }

            Span<byte> lb1 = need1 ? scratch.AsSpan(off, lineBufLen) : Span<byte>.Empty;
            if (need1)
            {
                off += lineBufLen;
            }

            Span<byte> lb2 = need2 ? scratch.AsSpan(off, lineBufLen) : Span<byte>.Empty;
            if (need2)
            {
                off += lineBufLen;
            }

            Span<byte> lb3 = need3 ? scratch.AsSpan(off, lineBufLen) : Span<byte>.Empty;

            int rowStride = width * outN;
            bool fused420 = CanFuse420(ws, isRgb, decodeN, outN, width, r0, r1, r2);
            for (int j = startRow; j < endRow; j++)
            {
                int localRow = flipVertically ? (rangeEndRow - 1 - j) : (j - rangeStartRow);
                Span<byte> dst = dstArray.AsSpan(localRow * rowStride, rowStride);

                if (fused420)
                {
                    ConvertRowFused420(ws, ref r0, ref r1, ref r2, lb0, dst, width, outN);
                    continue;
                }

                ReadOnlySpan<byte> c0 = decodeN > 0 ? ResampleComponentRowSpan(ref r0, ref ws.Components[0], lb0) : ReadOnlySpan<byte>.Empty;
                ReadOnlySpan<byte> c1 = decodeN > 1 ? ResampleComponentRowSpan(ref r1, ref ws.Components[1], lb1) : ReadOnlySpan<byte>.Empty;
                ReadOnlySpan<byte> c2 = decodeN > 2 ? ResampleComponentRowSpan(ref r2, ref ws.Components[2], lb2) : ReadOnlySpan<byte>.Empty;
                ReadOnlySpan<byte> c3 = decodeN > 3 ? ResampleComponentRowSpan(ref r3, ref ws.Components[3], lb3) : ReadOnlySpan<byte>.Empty;

                JpegColor.ConvertScanline(dst, c0, c1, c2, c3, width, ws.ImgN, outN, isRgb, ws.App14ColorTransform);
            }
        }
        finally
        {
            if (scratch is not null)
            {
                ArrayPool<byte>.Shared.Return(scratch);
            }
        }
    }

    /// <summary>
    /// Returns the near/far source row offsets for the next output row and advances the resampler state.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void NextRowOffsets(
        ref ResampleState r,
        ref JpegComponent comp,
        out int nearOffset,
        out int farOffset)
    {
        bool yBot = r.YStep >= (r.Vs >> 1);
        nearOffset = yBot ? r.Line1Offset : r.Line0Offset;
        farOffset = yBot ? r.Line0Offset : r.Line1Offset;

        if (++r.YStep >= r.Vs)
        {
            r.YStep = 0;
            r.Line0Offset = r.Line1Offset;
            if (++r.YPos < comp.Y)
            {
                r.Line1Offset += comp.W2;
            }
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static ReadOnlySpan<byte> ResampleComponentRowSpan(
        ref ResampleState r,
        ref JpegComponent comp,
        Span<byte> lineBuf)
    {
        NextRowOffsets(ref r, ref comp, out int nearOffset, out int farOffset);
        byte[] data = comp.Data!;
        int wLores = r.WLores;

        ReadOnlySpan<byte> result;
        switch (r.Mode)
        {
            case ResampleMode.None:
                result = data.AsSpan(nearOffset);
                break;

            case ResampleMode.V2:
                JpegColor.ResampleRowV2(lineBuf, data.AsSpan(nearOffset), data.AsSpan(farOffset), wLores);
                result = lineBuf;
                break;

            case ResampleMode.H2:
                JpegColor.ResampleRowH2(lineBuf, data.AsSpan(nearOffset), wLores);
                result = lineBuf;
                break;

            case ResampleMode.Hv2:
                JpegColor.ResampleRowHv2(lineBuf, data.AsSpan(nearOffset), data.AsSpan(farOffset), wLores);
                result = lineBuf;
                break;

            default:
                JpegColor.ResampleRowGeneric(lineBuf, data.AsSpan(nearOffset), wLores, r.Hs);
                result = lineBuf;
                break;
        }

        return result;
    }

    /// <summary>
    /// True when a 4:2:0 image (Y full-size, Cb/Cr h2v2) can use the fused upsample+color path.
    /// Only Arm64 AdvSimd has the fused kernel; every other target keeps the existing two-pass code.
    /// </summary>
    private static bool CanFuse420(JpegWorkspace ws, bool isRgb, int decodeN, int outN, int width, ResampleState r0, ResampleState r1, ResampleState r2)
    {
#if NET8_0_OR_GREATER
        return JpegColor.SupportsFused420
            && decodeN == 3
            && ws.ImgN == 3
            && !isRgb
            && outN >= 3
            && width >= 32
            && r0.Mode == ResampleMode.None
            && r1.Mode == ResampleMode.Hv2
            && r2.Mode == ResampleMode.Hv2;
#else
        return false;
#endif
    }

    /// <summary>
    /// Converts one 4:2:0 output row through the fused kernel. The caller must have checked <see cref="CanFuse420"/>.
    /// </summary>
    private static void ConvertRowFused420(
        JpegWorkspace ws,
        ref ResampleState r0,
        ref ResampleState r1,
        ref ResampleState r2,
        Span<byte> lineBuf0,
        Span<byte> dst,
        int width,
        int outN)
    {
#if NET8_0_OR_GREATER
        ReadOnlySpan<byte> y = ResampleComponentRowSpan(ref r0, ref ws.Components[0], lineBuf0);
        NextRowOffsets(ref r1, ref ws.Components[1], out int cbNear, out int cbFar);
        NextRowOffsets(ref r2, ref ws.Components[2], out int crNear, out int crFar);
        byte[] cbData = ws.Components[1].Data!;
        byte[] crData = ws.Components[2].Data!;

        JpegColor.ConvertYCbCr420RowFused(
            dst,
            y,
            cbData.AsSpan(cbNear),
            cbData.AsSpan(cbFar),
            crData.AsSpan(crNear),
            crData.AsSpan(crFar),
            width,
            r1.WLores,
            outN);
#endif
    }

    private static void ResampleAndColorConvert(JpegWorkspace ws, Span<byte> output, int outN, bool flipVertically)
    {
        bool isRgb = ws.ImgN == 3 && (ws.RgbTagCount == 3 || (ws.App14ColorTransform == 0 && !ws.Jfif));
        int decodeN = (ws.ImgN == 3 && outN < 3 && !isRgb) ? 1 : ws.ImgN;

        int width = ws.Width;
        int height = ws.Height;
        int lineBufLen = width + 3;

        ResampleState r0 = decodeN > 0 ? CreateResampleState(ws, 0, width, 0) : default;
        ResampleState r1 = decodeN > 1 ? CreateResampleState(ws, 1, width, 0) : default;
        ResampleState r2 = decodeN > 2 ? CreateResampleState(ws, 2, width, 0) : default;
        ResampleState r3 = decodeN > 3 ? CreateResampleState(ws, 3, width, 0) : default;

        bool need0 = decodeN > 0 && r0.Mode != ResampleMode.None;
        bool need1 = decodeN > 1 && r1.Mode != ResampleMode.None;
        bool need2 = decodeN > 2 && r2.Mode != ResampleMode.None;
        bool need3 = decodeN > 3 && r3.Mode != ResampleMode.None;
        int neededCount = (need0 ? 1 : 0) + (need1 ? 1 : 0) + (need2 ? 1 : 0) + (need3 ? 1 : 0);

        byte[]? scratch = neededCount > 0 ? ArrayPool<byte>.Shared.Rent(neededCount * lineBufLen) : null;
        try
        {
            int off = 0;
            Span<byte> lb0 = need0 ? scratch.AsSpan(off, lineBufLen) : Span<byte>.Empty;
            if (need0)
            {
                off += lineBufLen;
            }

            Span<byte> lb1 = need1 ? scratch.AsSpan(off, lineBufLen) : Span<byte>.Empty;
            if (need1)
            {
                off += lineBufLen;
            }

            Span<byte> lb2 = need2 ? scratch.AsSpan(off, lineBufLen) : Span<byte>.Empty;
            if (need2)
            {
                off += lineBufLen;
            }

            Span<byte> lb3 = need3 ? scratch.AsSpan(off, lineBufLen) : Span<byte>.Empty;

            int rowStride = width * outN;
            bool fused420 = CanFuse420(ws, isRgb, decodeN, outN, width, r0, r1, r2);
            for (int j = 0; j < height; j++)
            {
                int dstRow = flipVertically ? (height - 1 - j) : j;
                Span<byte> dst = output.Slice(dstRow * rowStride, rowStride);

                if (fused420)
                {
                    ConvertRowFused420(ws, ref r0, ref r1, ref r2, lb0, dst, width, outN);
                    continue;
                }

                ReadOnlySpan<byte> c0 = decodeN > 0 ? ResampleComponentRowSpan(ref r0, ref ws.Components[0], lb0) : ReadOnlySpan<byte>.Empty;
                ReadOnlySpan<byte> c1 = decodeN > 1 ? ResampleComponentRowSpan(ref r1, ref ws.Components[1], lb1) : ReadOnlySpan<byte>.Empty;
                ReadOnlySpan<byte> c2 = decodeN > 2 ? ResampleComponentRowSpan(ref r2, ref ws.Components[2], lb2) : ReadOnlySpan<byte>.Empty;
                ReadOnlySpan<byte> c3 = decodeN > 3 ? ResampleComponentRowSpan(ref r3, ref ws.Components[3], lb3) : ReadOnlySpan<byte>.Empty;

                JpegColor.ConvertScanline(dst, c0, c1, c2, c3, width, ws.ImgN, outN, isRgb, ws.App14ColorTransform);
            }
        }
        finally
        {
            if (scratch is not null)
            {
                ArrayPool<byte>.Shared.Return(scratch);
            }
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool IsSof(byte m) => m is 0xC0 or 0xC1 or 0xC2;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool IsRestart(byte m) => m is >= 0xD0 and <= 0xD7;

    private static void ValidateDesiredComponents(ColorComponents desiredComponents)
    {
        if ((int)desiredComponents is < 0 or > 4)
        {
            throw new StbException(
                StbErrorCode.InvalidArgument,
                $"Invalid desiredComponents value: {(int)desiredComponents}.");
        }
    }

    private static JpegWorkspace RentWorkspace()
    {
        JpegWorkspace? ws = t_cachedWorkspace;
        if (ws is not null)
        {
            t_cachedWorkspace = null;
            return ws;
        }

        return new JpegWorkspace();
    }

    private static void ReturnWorkspace(JpegWorkspace ws)
    {
        t_cachedWorkspace ??= ws;
    }

    private enum ResampleMode : byte
    {
        None = 0,
        V2 = 1,
        H2 = 2,
        Hv2 = 3,
        Generic = 4,
    }

    private struct ResampleState
    {
        public ResampleMode Mode;
        public int Hs;
        public int Vs;
        public int WLores;
        public int YStep;
        public int YPos;
        public int Line0Offset;
        public int Line1Offset;
    }

    private struct JpegComponent
    {
        public byte Id;
        public int H;
        public int V;
        public int Tq;
        public int Hd;
        public int Ha;
        public int DcPred;
        public int X;
        public int Y;
        public int W2;
        public int H2;
        public int CoeffW;
        public int CoeffH;
        public byte[]? Data;
        public short[]? Coeff;
    }

    private sealed class JpegWorkspace
    {
        internal readonly JpegHuffmanTable[] HuffDc =
        [
            new JpegHuffmanTable(),
            new JpegHuffmanTable(),
            new JpegHuffmanTable(),
            new JpegHuffmanTable(),
        ];

        internal readonly JpegHuffmanTable[] HuffAc =
        [
            new JpegHuffmanTable(),
            new JpegHuffmanTable(),
            new JpegHuffmanTable(),
            new JpegHuffmanTable(),
        ];

        internal readonly int[][] FastDc =
        [
            new int[JpegTables.FastSize],
            new int[JpegTables.FastSize],
            new int[JpegTables.FastSize],
            new int[JpegTables.FastSize],
        ];

        internal readonly int[][] FastAc =
        [
            new int[JpegTables.FastSize],
            new int[JpegTables.FastSize],
            new int[JpegTables.FastSize],
            new int[JpegTables.FastSize],
        ];

        internal readonly ushort[] Dequant = new ushort[4 * 64];
        internal readonly JpegComponent[] Components = new JpegComponent[4];
        internal readonly int[] Order = new int[4];

        internal int Width;
        internal int Height;
        internal int ImgN;
        internal int ImgHMax;
        internal int ImgVMax;
        internal int ImgMcuX;
        internal int ImgMcuY;
        internal int ImgMcuW;
        internal int ImgMcuH;

        internal bool Progressive;
        internal int SpecStart;
        internal int SpecEnd;
        internal int SuccHigh;
        internal int SuccLow;
        internal int EobRun;
        internal bool Jfif;
        internal int App14ColorTransform;
        internal int RgbTagCount;

        internal int ScanN;
        internal int RestartInterval;
        internal int Todo;
        private byte[]? _scanBuffer;

        internal Span<ushort> GetDequantSpan(int tableIndex) => Dequant.AsSpan(tableIndex * 64, 64);

        internal byte[] EnsureScanBuffer(int minLength)
        {
            if (_scanBuffer is not null && _scanBuffer.Length >= minLength)
            {
                return _scanBuffer;
            }

            if (_scanBuffer is not null)
            {
                ArrayPool<byte>.Shared.Return(_scanBuffer);
            }

            _scanBuffer = ArrayPool<byte>.Shared.Rent(minLength);
            return _scanBuffer;
        }

        internal void Reset()
        {
            for (int i = 0; i < 4; i++)
            {
                if (HuffDc[i].IsDefined)
                {
                    HuffDc[i].Reset();
                    FastDc[i].AsSpan().Clear();
                }

                if (HuffAc[i].IsDefined)
                {
                    HuffAc[i].Reset();
                    FastAc[i].AsSpan().Clear();
                }

                Components[i] = default;
                Order[i] = 0;
            }

            Dequant.AsSpan().Clear();
            Width = 0;
            Height = 0;
            ImgN = 0;
            ImgHMax = 0;
            ImgVMax = 0;
            ImgMcuX = 0;
            ImgMcuY = 0;
            ImgMcuW = 0;
            ImgMcuH = 0;
            Progressive = false;
            SpecStart = 0;
            SpecEnd = 0;
            SuccHigh = 0;
            SuccLow = 0;
            EobRun = 0;
            Jfif = false;
            App14ColorTransform = -1;
            RgbTagCount = 0;
            ScanN = 0;
            RestartInterval = 0;
            Todo = 0;
        }

        internal void ReleasePlanes()
        {
            for (int i = 0; i < 4; i++)
            {
                byte[]? data = Components[i].Data;
                if (data is not null)
                {
                    Components[i].Data = null;
                    ArrayPool<byte>.Shared.Return(data);
                }

                short[]? coeff = Components[i].Coeff;
                if (coeff is not null)
                {
                    Components[i].Coeff = null;
                    ArrayPool<short>.Shared.Return(coeff);
                }
            }

            byte[]? scanBuf = _scanBuffer;
            if (scanBuf is not null)
            {
                _scanBuffer = null;
                ArrayPool<byte>.Shared.Return(scanBuf);
            }
        }
    }

    private ref struct JpegReader
    {
        private readonly ReadOnlySpan<byte> _data;
        private int _pos;

        internal ulong CodeBuffer;
        internal int CodeBits;
        internal int MaxFillBits;
        internal byte Marker;
        internal bool NoMore;

        internal JpegReader(ReadOnlySpan<byte> data)
        {
            _data = data;
            _pos = 0;
            CodeBuffer = 0;
            CodeBits = 0;
            MaxFillBits = 63;
            Marker = JpegTables.MarkerNone;
            NoMore = false;
        }

        internal readonly bool AtEof => _pos >= _data.Length;

        internal readonly int Remaining => _data.Length - _pos;

        internal readonly ReadOnlySpan<byte> RemainingSpan => _pos < _data.Length ? _data.Slice(_pos) : ReadOnlySpan<byte>.Empty;

        internal void Advance(int count) => _pos += count;

        internal void ResetEntropy()
        {
            CodeBuffer = 0;
            CodeBits = 0;
            NoMore = false;
            Marker = JpegTables.MarkerNone;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal byte ReadByteOrZero() => _pos < _data.Length ? _data[_pos++] : (byte)0;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal int ReadUInt16BE()
        {
            int hi = ReadByteOrZero();
            int lo = ReadByteOrZero();
            return (hi << 8) | lo;
        }

        internal void Skip(int count)
        {
            if (count <= 0)
            {
                return;
            }

            if (count >= _data.Length - _pos)
            {
                _pos = _data.Length;
            }
            else
            {
                _pos += count;
            }
        }

        internal byte GetMarker()
        {
            if (Marker != JpegTables.MarkerNone)
            {
                byte cached = Marker;
                Marker = JpegTables.MarkerNone;
                return cached;
            }

            byte x = ReadByteOrZero();
            if (x != 0xFF)
            {
                return JpegTables.MarkerNone;
            }

            while (x == 0xFF)
            {
                x = ReadByteOrZero();
            }

            return x;
        }

        internal byte SkipJunkAtEnd()
        {
            while (_pos < _data.Length)
            {
                byte x = _data[_pos++];
                while (x == 0xFF)
                {
                    if (_pos >= _data.Length)
                    {
                        return JpegTables.MarkerNone;
                    }

                    x = _data[_pos++];
                    if (x != 0x00 && x != 0xFF)
                    {
                        return x;
                    }
                }
            }

            return JpegTables.MarkerNone;
        }

        /// <summary>
        /// Refills the 64-bit MSB-aligned bit buffer using an 8-byte SWAR <c>0xFF</c>-free fast path
        /// when safe, falling back to exact byte-by-byte stuffing and marker detection otherwise.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal void GrowBufferUnsafe()
        {
            if (!NoMore && (uint)(_data.Length - _pos) >= 8u)
            {
                ulong next8 = BinaryPrimitives.ReadUInt64BigEndian(_data.Slice(_pos, 8));
                ulong inv = ~next8;
                if (((inv - 0x0101010101010101UL) & ~inv & 0x8080808080808080UL) == 0)
                {
                    int loadBits = (MaxFillBits - CodeBits) & ~7;
                    if (loadBits > 0)
                    {
                        CodeBuffer |= (next8 >> (64 - loadBits)) << (64 - CodeBits - loadBits);
                        CodeBits += loadBits;
                        _pos += loadBits >> 3;
                    }

                    return;
                }
            }

            GrowBufferSlow();
        }

        private void GrowBufferSlow()
        {
            do
            {
                ulong b = NoMore ? 0UL : ReadByteOrZero();
                if (b == 0xFFUL)
                {
                    byte c = ReadByteOrZero();
                    while (c == 0xFF)
                    {
                        c = ReadByteOrZero();
                    }

                    if (c != 0)
                    {
                        Marker = c;
                        NoMore = true;
                        return;
                    }
                }

                CodeBuffer |= b << (56 - CodeBits);
                CodeBits += 8;
            }
            while (CodeBits <= 24);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal void ConsumeBits(int count)
        {
            CodeBuffer <<= count;
            CodeBits -= count;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal int HuffDecode(JpegHuffmanTable h)
        {
            if (CodeBits < 16)
            {
                GrowBufferUnsafe();
            }

            int c = (int)(CodeBuffer >> (64 - JpegTables.FastBits));
            int k = h.Fast[c];
            if (k < 255)
            {
                int s = h.Size[k];
                if (s > CodeBits)
                {
                    return -1;
                }

                CodeBuffer <<= s;
                CodeBits -= s;
                return h.Values[k];
            }

            return HuffDecodeSlow(h);
        }

        private int HuffDecodeSlow(JpegHuffmanTable h)
        {
            uint temp = (uint)(CodeBuffer >> 48);
            int k = JpegTables.FastBits + 1;
            while (temp >= h.MaxCode[k])
            {
                k++;
            }

            if (k == 17)
            {
                CodeBits -= 16;
                return -1;
            }

            if (k > CodeBits)
            {
                return -1;
            }

            int symIdx = (int)(CodeBuffer >> (64 - k)) + h.Delta[k];
            if ((uint)symIdx >= 256u)
            {
                return -1;
            }

            CodeBits -= k;
            CodeBuffer <<= k;
            return h.Values[symIdx];
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal int ExtendReceive(int n)
        {
            if (CodeBits < n)
            {
                GrowBufferUnsafe();
            }

            if (CodeBits < n)
            {
                return 0;
            }

            int sgn = (int)((long)CodeBuffer >> 63);
            int bits = (int)(CodeBuffer >> (64 - n));
            CodeBuffer <<= n;
            CodeBits -= n;
            return bits + (JpegTables.JpegBias[n] & ~sgn);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal int GetBits(int n)
        {
            if (CodeBits < n)
            {
                GrowBufferUnsafe();
            }

            if (CodeBits < n)
            {
                return 0;
            }

            int bits = (int)(CodeBuffer >> (64 - n));
            CodeBuffer <<= n;
            CodeBits -= n;
            return bits;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal bool GetBit()
        {
            if (CodeBits < 1)
            {
                GrowBufferUnsafe();
            }

            if (CodeBits < 1)
            {
                return false;
            }

            ulong k = CodeBuffer;
            CodeBuffer <<= 1;
            CodeBits--;
            return (k & 0x8000000000000000UL) != 0;
        }
    }
}
