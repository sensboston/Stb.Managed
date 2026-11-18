using System.Buffers.Binary;
using System.Globalization;
using Stb.Managed;
using Stb.Managed.Dxt;
using Stb.Managed.Perlin;

if (args.Length == 0 || args[0] is "-h" or "--help")
{
    PrintUsage();
    return 0;
}

if (string.Equals(args[0], "--self-test", StringComparison.Ordinal))
{
    RunSelfTest();
    return 0;
}

string command = args[0].ToUpperInvariant();
switch (command)
{
    case "INFO":
        return RunInfo(args);
    case "CONVERT":
        return RunConvert(args);
    case "PERLIN":
        return RunPerlin(args);
    case "DXT":
        return RunDxt(args);
    default:
        Console.Error.WriteLine($"Unknown command '{args[0]}'.");
        PrintUsage();
        return 1;
}

static void PrintUsage()
{
    Console.WriteLine("Stb.Managed.Samples.ImageTool — Native AOT-ready image converter, resizer, DXT & Perlin CLI");
    Console.WriteLine("Usage:");
    Console.WriteLine("  info <input>                                                  Inspect image metadata");
    Console.WriteLine("  convert <input> <out.(png|jpg|bmp|tga|hdr)> [W H] [quality]   Decode, optionally resize, and encode");
    Console.WriteLine("  perlin <out.png> [width] [height] [noise|fbm|ridge|turb]      Generate a procedural Perlin PNG");
    Console.WriteLine("  dxt <input> <out.dds> [bc1|bc3|bc4|bc5] [--hq]                Compress image to DDS (BC1/BC3/BC4/BC5)");
    Console.WriteLine("  --self-test                                                   Run end-to-end AOT self-verification");
}

static int RunInfo(string[] args)
{
    if (args.Length < 2)
    {
        Console.Error.WriteLine("Usage: info <input>");
        return 1;
    }

    byte[] bytes = File.ReadAllBytes(args[1]);
    ImageFormat format = StbImage.DetectFormat(bytes);
    ImageInfo info = StbImage.GetInfo(bytes);
    Console.WriteLine($"File: {args[1]} ({bytes.Length} bytes)");
    Console.WriteLine($"Format: {format}, Dimensions: {info.Width}x{info.Height}, Components: {info.Components}, Bits: {info.BitsPerChannel}, HDR: {info.IsHdr}");
    return 0;
}

static int RunConvert(string[] args)
{
    if (args.Length < 3)
    {
        Console.Error.WriteLine("Usage: convert <input> <output.(png|jpg|bmp|tga|hdr)> [width height] [quality]");
        return 1;
    }

    string inputPath = args[1];
    string outputPath = args[2];
    int targetW = args.Length >= 5 ? int.Parse(args[3], CultureInfo.InvariantCulture) : 0;
    int targetH = args.Length >= 5 ? int.Parse(args[4], CultureInfo.InvariantCulture) : 0;
    int quality = args.Length >= 6 ? int.Parse(args[5], CultureInfo.InvariantCulture) : 90;

    byte[] inputBytes = File.ReadAllBytes(inputPath);
    string ext = Path.GetExtension(outputPath).ToUpperInvariant();

    if (ext == ".HDR")
    {
        ImageBuffer<float> hdr = StbImage.LoadFloat(inputBytes, ColorComponents.RedGreenBlue);
        if (targetW > 0 && targetH > 0)
        {
            hdr = StbImageResize.Resize(hdr, targetW, targetH);
        }

        byte[] encoded = StbImageWrite.WriteHdr(hdr);
        File.WriteAllBytes(outputPath, encoded);
        Console.WriteLine($"Wrote HDR {hdr.Width}x{hdr.Height} -> {outputPath} ({encoded.Length} bytes).");
        return 0;
    }

    ImageBuffer<byte> img = StbImage.Load(inputBytes, ColorComponents.RedGreenBlueAlpha);
    if (targetW > 0 && targetH > 0)
    {
        img = StbImageResize.Resize(img, targetW, targetH);
    }

    byte[] outputBytes = ext switch
    {
        ".PNG" => StbImageWrite.WritePng(img),
        ".JPG" or ".JPEG" => StbImageWrite.WriteJpeg(img, quality),
        ".BMP" => StbImageWrite.WriteBmp(img),
        ".TGA" => StbImageWrite.WriteTga(img),
        _ => throw new InvalidOperationException($"Unsupported output extension '{ext}'."),
    };

    File.WriteAllBytes(outputPath, outputBytes);
    Console.WriteLine($"Converted {img.Width}x{img.Height} -> {outputPath} ({outputBytes.Length} bytes).");
    return 0;
}

static int RunPerlin(string[] args)
{
    if (args.Length < 2)
    {
        Console.Error.WriteLine("Usage: perlin <out.png> [width] [height] [noise|fbm|ridge|turb]");
        return 1;
    }

    string outputPath = args[1];
    int width = args.Length >= 3 ? int.Parse(args[2], CultureInfo.InvariantCulture) : 512;
    int height = args.Length >= 4 ? int.Parse(args[3], CultureInfo.InvariantCulture) : 512;
    string mode = args.Length >= 5 ? args[4].ToUpperInvariant() : "FBM";

    float[] field = new float[width * height];
    var parallel = new StbOptions { MaxDegreeOfParallelism = 0 };
    switch (mode)
    {
        case "NOISE":
            StbPerlin.FillNoise2DMemory(field, width, height, 0f, 0f, 0.03f, 0.03f, options: parallel);
            break;
        case "FBM":
            StbPerlin.FillFbm2DMemory(field, width, height, 0f, 0f, 0.02f, 0.02f, options: parallel);
            break;
        case "RIDGE":
            StbPerlin.FillRidge2DMemory(field, width, height, 0f, 0f, 0.02f, 0.02f, options: parallel);
            break;
        case "TURB":
            StbPerlin.FillTurbulence2DMemory(field, width, height, 0f, 0f, 0.02f, 0.02f, options: parallel);
            break;
        default:
            Console.Error.WriteLine($"Unknown Perlin mode '{mode}'.");
            return 1;
    }

    float min = float.PositiveInfinity;
    float max = float.NegativeInfinity;
    foreach (float v in field)
    {
        if (v < min) min = v;
        if (v > max) max = v;
    }

    float invRange = max > min ? 255.0f / (max - min) : 0.0f;
    byte[] gray = new byte[width * height];
    for (int i = 0; i < field.Length; ++i)
    {
        gray[i] = (byte)Math.Clamp((int)((field[i] - min) * invRange + 0.5f), 0, 255);
    }

    byte[] png = StbImageWrite.WritePng(gray, width, height, ColorComponents.Grey);
    File.WriteAllBytes(outputPath, png);
    Console.WriteLine($"Generated {mode} Perlin {width}x{height} -> {outputPath} ({png.Length} bytes).");
    return 0;
}

static int RunDxt(string[] args)
{
    if (args.Length < 3)
    {
        Console.Error.WriteLine("Usage: dxt <input> <out.dds> [bc1|bc3|bc4|bc5] [--hq]");
        return 1;
    }

    string inputPath = args[1];
    string outputPath = args[2];
    string fmtName = args.Length >= 4 && !args[3].StartsWith("--", StringComparison.Ordinal)
        ? args[3].ToUpperInvariant()
        : "BC3";
    bool hq = args.Any(a => string.Equals(a, "--hq", StringComparison.OrdinalIgnoreCase));

    DxtFormat format = fmtName switch
    {
        "BC1" => DxtFormat.Bc1,
        "BC3" => DxtFormat.Bc3,
        "BC4" => DxtFormat.Bc4,
        "BC5" => DxtFormat.Bc5,
        _ => throw new InvalidOperationException($"Unknown DXT format '{fmtName}'."),
    };

    ColorComponents desired = format switch
    {
        DxtFormat.Bc4 => ColorComponents.Grey,
        DxtFormat.Bc5 => ColorComponents.GreyAlpha,
        _ => ColorComponents.RedGreenBlueAlpha,
    };

    ImageBuffer<byte> img = StbImage.Load(File.ReadAllBytes(inputPath), desired);
    DxtCompressionMode mode = hq ? DxtCompressionMode.HighQuality : DxtCompressionMode.Normal;
    byte[] blocks = StbDxt.CompressImageToArray(
        img.ReadOnlyMemory,
        img.Width,
        img.Height,
        format,
        mode: mode,
        options: new StbOptions { MaxDegreeOfParallelism = 0 });

    byte[] dds = BuildDds(img.Width, img.Height, format, blocks);
    File.WriteAllBytes(outputPath, dds);
    Console.WriteLine($"Compressed {img.Width}x{img.Height} as {format} ({mode}) -> {outputPath} ({dds.Length} bytes).");
    return 0;
}

static byte[] BuildDds(int width, int height, DxtFormat format, ReadOnlySpan<byte> blocks)
{
    uint fourCc = format switch
    {
        DxtFormat.Bc1 => 0x31545844u, // 'DXT1'
        DxtFormat.Bc3 => 0x35545844u, // 'DXT5'
        DxtFormat.Bc4 => 0x55344342u, // 'BC4U'
        DxtFormat.Bc5 => 0x55354342u, // 'BC5U'
        _ => throw new ArgumentOutOfRangeException(nameof(format)),
    };

    byte[] dds = new byte[128 + blocks.Length];
    Span<byte> hdr = dds.AsSpan(0, 128);
    BinaryPrimitives.WriteUInt32LittleEndian(hdr.Slice(0, 4), 0x20534444u); // 'DDS '
    BinaryPrimitives.WriteUInt32LittleEndian(hdr.Slice(4, 4), 124u);        // dwSize
    BinaryPrimitives.WriteUInt32LittleEndian(hdr.Slice(8, 4), 0x00081007u); // CAPS | HEIGHT | WIDTH | PIXELFORMAT | LINEARSIZE
    BinaryPrimitives.WriteUInt32LittleEndian(hdr.Slice(12, 4), (uint)height);
    BinaryPrimitives.WriteUInt32LittleEndian(hdr.Slice(16, 4), (uint)width);
    BinaryPrimitives.WriteUInt32LittleEndian(hdr.Slice(20, 4), (uint)blocks.Length);
    BinaryPrimitives.WriteUInt32LittleEndian(hdr.Slice(76, 4), 32u);        // ddspf.dwSize
    BinaryPrimitives.WriteUInt32LittleEndian(hdr.Slice(80, 4), 0x4u);       // DDPF_FOURCC
    BinaryPrimitives.WriteUInt32LittleEndian(hdr.Slice(84, 4), fourCc);
    BinaryPrimitives.WriteUInt32LittleEndian(hdr.Slice(108, 4), 0x1000u);   // DDSCAPS_TEXTURE
    blocks.CopyTo(dds.AsSpan(128));
    return dds;
}

static void RunSelfTest()
{
    const int w = 256;
    const int h = 256;
    var parallel = new StbOptions { MaxDegreeOfParallelism = 0 };

    // 1. Perlin 2D fBm (1T vs Auto-Par bit-exact).
    float[] fbm1T = new float[w * h];
    float[] fbmPar = new float[w * h];
    StbPerlin.FillFbm2D(fbm1T, w, h, -2.0f, 1.5f, 0.025f, 0.025f, 0.3f, 2.0f, 0.5f, 6);
    StbPerlin.FillFbm2DMemory(fbmPar, w, h, -2.0f, 1.5f, 0.025f, 0.025f, 0.3f, 2.0f, 0.5f, 6, options: parallel);
    if (!fbm1T.AsSpan().SequenceEqual(fbmPar))
    {
        throw new InvalidOperationException("Perlin 1T vs Auto-Par mismatch.");
    }

    // 2. Build RGBA image + resize 256x256 -> 128x128 (1T vs Auto-Par bit-exact).
    byte[] rgba = new byte[w * h * 4];
    float[] hdrRgb = new float[w * h * 3];
    for (int i = 0; i < w * h; ++i)
    {
        float n = Math.Clamp(fbm1T[i] * 0.5f + 0.5f, 0f, 1f);
        byte v = (byte)(n * 255f);
        rgba[i * 4] = v;
        rgba[i * 4 + 1] = (byte)(255 - v);
        rgba[i * 4 + 2] = (byte)((v + i) & 255);
        rgba[i * 4 + 3] = (byte)(128 + (v >> 1));

        hdrRgb[i * 3] = n * 2.5f;
        hdrRgb[i * 3 + 1] = (1f - n) * 1.5f;
        hdrRgb[i * 3 + 2] = 0.25f + n;
    }

    const int rw = 128;
    const int rh = 128;
    byte[] resized1T = new byte[rw * rh * 4];
    byte[] resizedPar = new byte[rw * rh * 4];
    StbImageResizer resizer1T = StbImageResizer.Create(w, h, rw, rh, ResizePixelLayout.Rgba, ResizeDataType.UInt8Srgb);
    StbImageResizer resizerPar = StbImageResizer.Create(
        w, h, rw, rh, ResizePixelLayout.Rgba, ResizeDataType.UInt8Srgb,
        new ImageResizeOptions { Options = parallel });
    resizer1T.Resize(rgba, resized1T);
    resizerPar.ResizeMemory(rgba, resizedPar);
    if (!resized1T.AsSpan().SequenceEqual(resizedPar))
    {
        throw new InvalidOperationException("ImageResize 1T vs Auto-Par mismatch.");
    }

    // 3. Roundtrip PNG, BMP, TGA, JPEG, HDR via StbImageWrite -> StbImage.
    byte[] png = StbImageWrite.WritePng(resized1T, rw, rh, ColorComponents.RedGreenBlueAlpha);
    ImageBuffer<byte> decPng = StbImage.Load(png, ColorComponents.RedGreenBlueAlpha);
    if (!decPng.AsSpan().SequenceEqual(resized1T))
    {
        throw new InvalidOperationException("PNG roundtrip pixel mismatch.");
    }

    byte[] bmp = StbImageWrite.WriteBmp(resized1T, rw, rh, ColorComponents.RedGreenBlueAlpha);
    ImageBuffer<byte> decBmp = StbImage.Load(bmp, ColorComponents.RedGreenBlueAlpha);
    if (!decBmp.AsSpan().SequenceEqual(resized1T))
    {
        throw new InvalidOperationException("BMP roundtrip pixel mismatch.");
    }

    byte[] tga = StbImageWrite.WriteTga(resized1T, rw, rh, ColorComponents.RedGreenBlueAlpha);
    ImageBuffer<byte> decTga = StbImage.Load(tga, ColorComponents.RedGreenBlueAlpha);
    if (!decTga.AsSpan().SequenceEqual(resized1T))
    {
        throw new InvalidOperationException("TGA roundtrip pixel mismatch.");
    }

    byte[] jpg = StbImageWrite.WriteJpeg(resized1T, rw, rh, ColorComponents.RedGreenBlueAlpha, quality: 92);
    ImageInfo jpgInfo = StbImage.GetInfo(jpg);
    if (jpgInfo.Width != rw || jpgInfo.Height != rh || StbImage.DetectFormat(jpg) != ImageFormat.Jpeg)
    {
        throw new InvalidOperationException("JPEG header verification failed.");
    }

    _ = StbImage.Load(jpg, ColorComponents.RedGreenBlueAlpha);

    byte[] hdrBytes = StbImageWrite.WriteHdr(hdrRgb, w, h, ColorComponents.RedGreenBlue);
    if (!StbImage.IsHdr(hdrBytes))
    {
        throw new InvalidOperationException("HDR detection failed.");
    }

    ImageBuffer<float> decHdr = StbImage.LoadFloat(hdrBytes, ColorComponents.RedGreenBlue);
    if (decHdr.Width != w || decHdr.Height != h)
    {
        throw new InvalidOperationException("HDR decode dimensions mismatch.");
    }

    // 4. DXT BC1/BC3/BC4/BC5 (1T vs Auto-Par bit-exact).
    byte[] bc3Single = StbDxt.CompressImageToArray(rgba, w, h, DxtFormat.Bc3);
    byte[] bc3Par = StbDxt.CompressImageToArray(rgba, w, h, DxtFormat.Bc3, options: parallel);
    if (!bc3Single.AsSpan().SequenceEqual(bc3Par))
    {
        throw new InvalidOperationException("DXT BC3 1T vs Auto-Par mismatch.");
    }

    byte[] dds = BuildDds(w, h, DxtFormat.Bc3, bc3Par);
    if (dds.Length != 128 + bc3Par.Length)
    {
        throw new InvalidOperationException("DDS size mismatch.");
    }

    Console.WriteLine($"ImageTool self-test OK: Perlin {w}x{h} -> Resize {rw}x{rh} -> PNG({png.Length}B)/BMP({bmp.Length}B)/TGA({tga.Length}B)/JPEG({jpg.Length}B)/HDR({hdrBytes.Length}B) -> DDS BC3({dds.Length}B).");
}
