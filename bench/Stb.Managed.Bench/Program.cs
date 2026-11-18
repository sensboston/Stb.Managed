using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics.Arm;
using System.Runtime.Intrinsics.X86;
using System.Text;
using System.Text.Json;
using Stb.Managed;
using Stb.Managed.Dxt;
using Stb.Managed.Gpu;
using Stb.Managed.Perlin;
using Stb.Managed.RectPack;
using Stb.Managed.TrueType;

namespace Stb.Managed.Bench;

internal static class Program
{
    private static int Main(string[] args)
    {
        try
        {
            return Run(CommandLine.Parse(args));
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(ex.Message);
            return 2;
        }
    }

    private static int Run(CommandLine command)
    {
        if (command.Help)
        {
            Console.WriteLine("Usage: Stb.Managed.Bench --corpus DIR [--cases FILE] [--out FILE] [--min-time-ms N] [--filter TEXT] [--threads N] [--gpu] [--default]");
            Console.WriteLine(" Stb.Managed.Bench --sysinfo FILE [--threads N]");
            return 0;
        }

        var options = new StbOptions { MaxDegreeOfParallelism = command.Threads };

        // --default measures what a caller gets without configuration: all cores and the GPU mode from STB_GPU_MODE (Auto), with the GPU
        // initialized before timing starts. Every other run is CPU only.
        if (!command.Default)
        {
            StbGpu.Mode = StbGpuMode.CpuOnly;
        }

        if (command.Default)
        {
            options = new StbOptions { MaxDegreeOfParallelism = 0 };
            StbGpu.Warmup();
        }

        bool needsGpu = command.Gpu || command.Sysinfo is not null;
        using StbGpuContext? gpu = needsGpu
        ? StbGpuContext.TryCreateHardware(new StbGpuOptions
        {
            ExecutionMode = StbGpuExecutionMode.ForceGpu,
            CpuOptions = options,
        })
        : null;
        if (command.Sysinfo is not null)
        {
            SystemInfo.WriteJson(command.Sysinfo, gpu);
            return 0;
        }

        if (string.IsNullOrEmpty(command.Corpus))
        {
            throw new ArgumentException("--corpus is required");
        }

        string corpus = command.Corpus;
        using var outputFile = command.Output is null
        ? null
        : new StreamWriter(command.Output, false, new UTF8Encoding(false));
        TextWriter output = outputFile ?? Console.Out;
        SystemInfo.WriteHeader(output, command, gpu);
        bool failed = false;
        int lineNumber = 0;
        foreach (string line in File.ReadLines(command.Cases))
        {
            ++lineNumber;
            string trimmed = line.TrimStart(' ', '\t');
            if (trimmed.Length == 0 || trimmed[0] == '#') continue;

            CaseDefinition item;
            try
            {
                item = CaseDefinition.Parse(line);
            }
            catch (FormatException ex)
            {
                Console.Error.WriteLine($"{command.Cases}:{lineNumber}: {ex.Message}");
                failed = true;
                break;
            }

            if (command.Filter is not null && !item.Name.Contains(command.Filter, StringComparison.Ordinal)) continue;
            if (!Workload.IsSupported(item, command.Gpu) || (command.Gpu && gpu is null))
            {
                Csv.WriteEmpty(output, item, "skipped");
                continue;
            }

            try
            {
                using var workload = new Workload(item, corpus, options, command.Gpu ? gpu : null);
                BenchmarkResult result = Measure(workload, command.MinTimeMs);
                Csv.WriteResult(output, item, result, workload.IsInto ? "ok_no_native" : "ok");
            }
            catch (UnsupportedCaseException ex)
            {
                Console.Error.WriteLine($"{item.Name}: {ex.Message}");
                Csv.WriteEmpty(output, item, "skipped");
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"{item.Name}: {ex.GetType().Name}: {ex.Message}");
                Csv.WriteEmpty(output, item, "error");
                failed = true;
            }
            output.Flush();
        }
        output.Flush();
        return failed ? 1 : 0;
    }

    private static BenchmarkResult Measure(Workload workload, double minimumMs)
    {
        // Warm-up: at least 3 runs and at least 500 ms (lets the JIT reach optimized code), same rule as native.
        long warmStart = Stopwatch.GetTimestamp();
        for (int i = 0; i < 3 || Stopwatch.GetElapsedTime(warmStart).TotalMilliseconds < 500; ++i)
        {
            workload.RunChecked();
            workload.ReleaseIteration();
        }

        var samples = new List<double>();
        double total = 0;
        ulong checksum = 0;
        do
        {
            StbGpuStats? before = workload.Gpu?.Stats;
            long start = Stopwatch.GetTimestamp();
            workload.Run();
            long end = Stopwatch.GetTimestamp();
            workload.CheckGpuDispatch(before);

            if (workload.Ephemeral) checksum = workload.Checksum();
            long releaseStart = Stopwatch.GetTimestamp();
            workload.ReleaseIteration();
            long releaseEnd = Stopwatch.GetTimestamp();
            double elapsed = ((end - start) + (releaseEnd - releaseStart)) * 1000.0 / Stopwatch.Frequency;
            samples.Add(elapsed);
            total += elapsed;
        }
        while (samples.Count < 10 || total < minimumMs);

        if (!workload.Ephemeral) checksum = workload.Checksum();
        double[] sorted = samples.ToArray();
        Array.Sort(sorted);
        double minimum = sorted[0];
        double median = Median(sorted);
        for (int i = 0; i < sorted.Length; ++i) sorted[i] = Math.Abs(sorted[i] - median);
        Array.Sort(sorted);
        return new BenchmarkResult(sorted.Length, minimum, median, Median(sorted), checksum);
    }

    private static double Median(double[] sorted)
    {
        int middle = sorted.Length / 2;
        return (sorted.Length & 1) != 0 ? sorted[middle] : (sorted[middle - 1] + sorted[middle]) / 2.0;
    }
}

internal sealed class CommandLine
{
    public string? Corpus { get; private set; }
    public string Cases { get; private set; } = "bench/cases.tsv";
    public string? Output { get; private set; }
    public string? Filter { get; private set; }
    public string? Sysinfo { get; private set; }
    public int Threads { get; private set; } = 1;
    public double MinTimeMs { get; private set; } = 1000;
    public bool Gpu { get; private set; }

    public bool Default { get; private set; }
    public bool Help { get; private set; }

    public static CommandLine Parse(string[] args)
    {
        var result = new CommandLine();
        for (int i = 0; i < args.Length; ++i)
        {
            string arg = args[i];
            if (arg == "--help") { result.Help = true; continue; }
            if (arg == "--gpu") { result.Gpu = true; continue; }
            if (arg == "--default") { result.Default = true; result.Threads = 0; continue; }
            if (i + 1 == args.Length) throw new ArgumentException($"Missing value for {arg}");
            string value = args[++i];
            switch (arg)
            {
                case "--corpus": result.Corpus = value; break;
                case "--cases": result.Cases = value; break;
                case "--out": result.Output = value; break;
                case "--filter": result.Filter = value; break;
                case "--sysinfo": result.Sysinfo = value; break;
                case "--threads":
                    if (!int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int threads) || threads < 0)
                        throw new ArgumentException("Invalid --threads");
                    result.Threads = threads;
                    break;
                case "--min-time-ms":
                    if (!double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out double ms) || !double.IsFinite(ms) || ms < 0)
                        throw new ArgumentException("Invalid --min-time-ms");
                    result.MinTimeMs = ms;
                    break;
                default: throw new ArgumentException($"Unknown option {arg}");
            }
        }
        return result;
    }
}

internal sealed class CaseDefinition
{
    public string Name { get; }
    public string Op { get; }
    public string Input { get; }
    public Dictionary<string, string> Parameters { get; }

    private CaseDefinition(string name, string op, string input, Dictionary<string, string> parameters)
    {
        Name = name;
        Op = op;
        Input = input;
        Parameters = parameters;
    }

    public static CaseDefinition Parse(string line)
    {
        string[] columns = line.Split('\t');
        if (columns.Length != 4) throw new FormatException("expected four tab-separated fields");
        for (int i = 0; i < columns.Length; ++i) columns[i] = columns[i].Trim(' ', '\r', '\n');
        if (columns.Any(static s => s.Length == 0)) throw new FormatException("empty field");
        var parameters = new Dictionary<string, string>(StringComparer.Ordinal);
        if (columns[3] != "-")
        {
            foreach (string entry in columns[3].Split(';'))
            {
                int equal = entry.IndexOf('=');
                if (equal <= 0 || equal == entry.Length - 1 || entry.IndexOf('=', equal + 1) >= 0)
                    throw new FormatException("invalid parameter");
                string key = entry[..equal].Trim(' ');
                string value = entry[(equal + 1)..].Trim(' ');
                if (key.Length == 0 || value.Length == 0 || !parameters.TryAdd(key, value))
                    throw new FormatException("empty or duplicate parameter");
            }
        }
        return new CaseDefinition(columns[0], columns[1], columns[2], parameters);
    }

    public string Required(string key) => Parameters.TryGetValue(key, out string? value)
    ? value : throw new FormatException($"missing parameter {key}");

    public int Integer(string key)
    {
        string value = Required(key);
        return int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int number)
        ? number : throw new FormatException($"invalid integer {key}={value}");
    }

    public float Float(string key)
    {
        string value = Required(key);
        return float.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out float number) && float.IsFinite(number)
        ? number : throw new FormatException($"invalid float {key}={value}");
    }
}

internal sealed class UnsupportedCaseException : Exception
{
    public UnsupportedCaseException(string message) : base(message) { }
}

internal readonly record struct BenchmarkResult(int Runs, double Min, double Median, double Mad, ulong Checksum);

internal static class Csv
{
    public static void WriteEmpty(TextWriter output, CaseDefinition item, string status)
    {
        output.WriteLine($"{Quote(item.Name)},{Quote(item.Op)},0,,,,,{status}");
    }

    public static void WriteResult(TextWriter output, CaseDefinition item, BenchmarkResult result, string status)
    {
        output.WriteLine(string.Create(CultureInfo.InvariantCulture,
        $"{Quote(item.Name)},{Quote(item.Op)},{result.Runs},{result.Min:F6},{result.Median:F6},{result.Mad:F6},{result.Checksum:x16},{status}"));
    }

    private static string Quote(string value) => '"' + value.Replace("\"", "\"\"", StringComparison.Ordinal) + '"';
}

internal static class SystemInfo
{
    private static readonly JsonSerializerOptions IndentedJson = new() { WriteIndented = true };

    private static string CpuName()
    {
        if (OperatingSystem.IsWindows())
        {
            using Microsoft.Win32.RegistryKey? key = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(@"HARDWARE\DESCRIPTION\System\CentralProcessor\0");
            if (key?.GetValue("ProcessorNameString") is string name && !string.IsNullOrWhiteSpace(name)) return name.Trim();
            string? env = Environment.GetEnvironmentVariable("PROCESSOR_IDENTIFIER");
            if (!string.IsNullOrWhiteSpace(env)) return env;
        }
        if (OperatingSystem.IsLinux() && File.Exists("/proc/cpuinfo"))
        {
            string? armPart = null;
            foreach (string line in File.ReadLines("/proc/cpuinfo"))
            {
                int colon = line.IndexOf(':');
                if (colon < 0) continue;
                string key = line[..colon].Trim();
                if (key is "model name" or "Hardware") return line[(colon + 1)..].Trim();
                if (key == "CPU part" && armPart is null) armPart = line[(colon + 1)..].Trim();
            }

            // Arm cores report no model name, only the part number.
            if (armPart is not null)
            {
                return armPart switch
                {
                    "0xd0c" => "Arm Neoverse-N1",
                    "0xd40" => "Arm Neoverse-V1",
                    "0xd49" => "Arm Neoverse-N2",
                    "0xd4f" => "Arm Neoverse-V2",
                    "0xd8e" => "Arm Neoverse-N3",
                    "0xd84" => "Arm Neoverse-V3",
                    "0xd0b" => "Arm Cortex-A76",
                    "0xd41" => "Arm Cortex-A78",
                    _ => $"Arm core {armPart}",
                };
            }
        }
        if (OperatingSystem.IsMacOS())
        {
            using Process? process = Process.Start(new ProcessStartInfo
            {
                FileName = "/usr/sbin/sysctl",
                ArgumentList = { "-n", "machdep.cpu.brand_string" },
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            });
            if (process is not null)
            {
                string text = process.StandardOutput.ReadToEnd().Trim();
                process.WaitForExit();
                if (process.ExitCode == 0 && text.Length > 0) return text;
            }
        }
        return "unknown";
    }

    public static void WriteHeader(TextWriter output, CommandLine command, StbGpuContext? gpu)
    {
        Line(output, "runtime", RuntimeInformation.FrameworkDescription);
        Line(output, "os", RuntimeInformation.OSDescription);
        Line(output, "cpu", CpuName());
        Line(output, "isa", $"SSE4.1={Sse41.IsSupported};AVX2={Avx2.IsSupported};AVX-512F={Avx512F.IsSupported};NEON={AdvSimd.IsSupported}");
        Line(output, "threads", command.Threads.ToString(CultureInfo.InvariantCulture));
        Line(output, "mode", command.Default ? $"default (all cores, GPU auto: {StbGpu.DeviceName ?? "none"})" : command.Gpu ? "gpu (ForceGpu)" : "cpu");
        Line(output, "gpu", gpu is null ? "none" : $"{gpu.Device.Name}; backend={gpu.Device.Backend}");
        Line(output, "checksum", "FNV-1a-64; serialization matches bench_native.c");
        Line(output, "into_status", "ok_no_native means no native counterpart");
        output.WriteLine("case,op,runs,min_ms,median_ms,mad_ms,checksum,status");
    }

    public static void WriteJson(string path, StbGpuContext? gpu)
    {
        var data = new
        {
            hostname = Environment.MachineName,
            cpu = CpuName(),
            logical_processors = Environment.ProcessorCount,
            os = RuntimeInformation.OSDescription,
            architecture = RuntimeInformation.ProcessArchitecture.ToString(),
            dotnet = RuntimeInformation.FrameworkDescription,
            isa = new { sse41 = Sse41.IsSupported, avx2 = Avx2.IsSupported, avx512f = Avx512F.IsSupported, neon = AdvSimd.IsSupported },
            managed_memory_limit_bytes = GC.GetGCMemoryInfo().TotalAvailableMemoryBytes,
            gpu = gpu is null ? null : new { name = gpu.Device.Name, backend = gpu.Device.Backend.ToString(), memory_bytes = gpu.Device.MemoryBytes },
        };
        File.WriteAllText(path, JsonSerializer.Serialize(data, IndentedJson), new UTF8Encoding(false));
    }

    private static void Line(TextWriter output, string key, string value)
    {
        output.WriteLine($"# {key}: {value.Replace('\r', ' ').Replace('\n', ' ')}");
    }
}

internal struct Fnv64
{
    private const ulong Prime = 1099511628211UL;
    private ulong _value;
    public Fnv64() { _value = 14695981039346656037UL; }
    public readonly ulong Value => _value;
    public void AddByte(byte value) => _value = unchecked((_value ^ value) * Prime);
    public void Add(ReadOnlySpan<byte> bytes)
    {
        foreach (byte value in bytes) AddByte(value);
    }
    public void AddU32(uint value)
    {
        for (int i = 0; i < 4; ++i) AddByte((byte)(value >> (8 * i)));
    }
    public void AddU64(ulong value)
    {
        for (int i = 0; i < 8; ++i) AddByte((byte)(value >> (8 * i)));
    }
    public void AddSamples(ReadOnlySpan<byte> bytes, int sampleSize)
    {
        if (BitConverter.IsLittleEndian || sampleSize == 1) { Add(bytes); return; }
        for (int i = 0; i < bytes.Length; i += sampleSize)
            for (int j = sampleSize - 1; j >= 0; --j) AddByte(bytes[i + j]);
    }
}

internal sealed class Workload : IDisposable
{
    private readonly CaseDefinition _case;
    private readonly StbOptions _options;
    private readonly ImageDecodeOptions _decodeOptions;
    private readonly ImageResizeOptions _resizeOptions;
    private ImageWriteOptions _writeOptions;
    private byte[] _file = [];
    private byte[] _floatBytes = [];
    private float[] _floats = [];
    private byte[] _output = [];
    private ushort[] _output16 = [];
    private float[] _outputFloat = [];
    private ImageBuffer<byte>? _source8;
    private ImageBuffer<float>? _sourceFloat;
    private ImageBuffer<byte>? _decoded8;
    private ImageBuffer<ushort>? _decoded16;
    private ImageBuffer<float>? _decodedFloat;
    private MemoryStream? _encoded;
    private StbTrueTypeFont? _font;
    private BakedChar[] _baked = [];
    private PackedChar[] _packed = [];
    private PackRect[] _rectangles = [];
    private PackRect[] _rectTemplate = [];
    private SdfResult[] _sdf = [];
    private GpuSdfGlyphResult[]? _gpuSdf;
    private int[] _codepoints = [];
    private int _iw, _ih, _ow, _oh, _components, _quality, _px, _first, _count, _padding, _oversample, _onedge, _octaves;
    private float _distScale, _startX, _startY, _z, _step, _lacunarity, _gain;
    private ResizeDataType _resizeType;
    private DxtFormat _dxtFormat;

    public StbGpuContext? Gpu { get; }
    public bool IsInto => _case.Op is "decode_into" or "decode16_into" or "decodef_into";
    public bool Ephemeral => _case.Op is "decode" or "decode16" or "decodef" or "tt_sdf";

    private readonly record struct SdfResult(GlyphBitmapMetrics Metrics, byte[] Pixels);

    public Workload(CaseDefinition item, string corpus, StbOptions options, StbGpuContext? gpu)
    {
        _case = item;
        _options = options;
        Gpu = gpu;
        _decodeOptions = new ImageDecodeOptions { Options = options };
        _resizeOptions = new ImageResizeOptions { Options = options };
        _writeOptions = ImageWriteOptions.Default with { Options = options };
        Prepare(corpus);
    }

    public static bool IsSupported(CaseDefinition item, bool gpuMode)
    {
        string[]? allowed = item.Op switch
        {
            "decode" or "decode16" or "decodef" or "decode_into" or "decode16_into" or "decodef_into" or "encode_bmp" or "encode_hdr" => ["comp"],
            "encode_png" => ["comp", "engine"],
            "encode_jpeg" => ["comp", "quality"],
            "encode_tga" => ["comp", "rle"],
            "resize" => ["w", "h", "type", "comp"],
            "tt_bake" => ["px", "first", "count", "w", "h"],
            "tt_pack" => ["px", "first", "count", "w", "h", "oversample", "padding"],
            "tt_sdf" => ["px", "first", "count", "padding", "onedge", "scale"],
            "rectpack" => ["count", "min", "max", "seed", "w", "h"],
            "dxt" => ["comp", "format", "mode"],
            "perlin_fbm" => ["w", "h", "startx", "starty", "z", "step", "lacunarity", "gain", "octaves"],
            _ => null,
        };
        if (allowed is null || item.Parameters.Keys.Any(key => !allowed.Contains(key, StringComparer.Ordinal))) return false;
        return !gpuMode || item.Op is "resize" or "dxt" or "perlin_fbm" or "tt_sdf";
    }

    private void Prepare(string corpus)
    {
        if (_case.Op is not ("rectpack" or "perlin_fbm"))
            _file = File.ReadAllBytes(Path.Combine(corpus, _case.Input));
        switch (_case.Op)
        {
            case "decode":
            case "decode16":
            case "decodef":
            case "decode_into":
            case "decode16_into":
            case "decodef_into":
                _components = Components();
                if (IsInto)
                {
                    ImageInfo info = StbImage.GetInfo(_file, _decodeOptions);
                    int count = checked(info.Width * info.Height * _components);
                    if (_case.Op == "decode_into") _output = new byte[count];
                    else if (_case.Op == "decode16_into") _output16 = new ushort[count];
                    else _outputFloat = new float[count];
                }
                break;
            case "encode_png":
            case "encode_jpeg":
            case "encode_bmp":
            case "encode_tga":
            case "encode_hdr":
                PrepareEncode();
                break;
            case "resize": PrepareResize(); break;
            case "tt_bake": case "tt_pack": PrepareFontAtlas(); break;
            case "tt_sdf": PrepareSdf(); break;
            case "rectpack": PrepareRectangles(); break;
            case "dxt": PrepareDxt(); break;
            case "perlin_fbm": PreparePerlin(); break;
            default: throw new UnsupportedCaseException("unsupported operation");
        }
    }

    private int Components()
    {
        int value = _case.Integer("comp");
        if (value is < 1 or > 4) throw new FormatException("comp must be in 1..4");
        return value;
    }

    private void PrepareEncode()
    {
        _components = Components();
        if (_case.Op == "encode_hdr")
            _sourceFloat = StbImage.LoadFloat(_file, (ColorComponents)_components, _decodeOptions);
        else
            _source8 = StbImage.Load(_file, (ColorComponents)_components, _decodeOptions);
        _encoded = new MemoryStream();
        if (_case.Op == "encode_png")
        {
            string engine = _case.Required("engine");
            _writeOptions = engine switch
            {
                "default" => ImageWriteOptions.Default with { Options = _options },
                "stb" => ImageWriteOptions.Default with { Options = _options, PngCompressionEngine = PngCompressionEngine.StbCompatible },
                _ => throw new UnsupportedCaseException($"unsupported PNG engine {engine}"),
            };
        }
        else if (_case.Op == "encode_jpeg")
        {
            _quality = _case.Integer("quality");
            if (_quality is < 1 or > 100) throw new FormatException("quality must be in 1..100");
        }
        else if (_case.Op == "encode_tga")
        {
            int rle = _case.Integer("rle");
            if (rle is not (0 or 1)) throw new FormatException("rle must be 0 or 1");
            _writeOptions = _writeOptions with { TgaUseRle = rle == 1 };
        }
    }

    private void PrepareResize()
    {
        _components = Components();
        if (_components != 4) throw new UnsupportedCaseException("resize currently supports RGBA only");
        _ow = Positive("w"); _oh = Positive("h");
        _resizeType = _case.Required("type") switch
        {
            "linear" => ResizeDataType.UInt8Linear,
            "srgb" => ResizeDataType.UInt8Srgb,
            "float" => ResizeDataType.Fp32,
            _ => throw new UnsupportedCaseException("unsupported resize type"),
        };
        _source8 = StbImage.Load(_file, (ColorComponents)_components, _decodeOptions);
        _iw = _source8.Width; _ih = _source8.Height;
        int outSamples = checked(_ow * _oh * 4);
        if (_resizeType == ResizeDataType.Fp32)
        {
            ReadOnlySpan<byte> source = _source8.AsReadOnlySpan();
            _floats = new float[source.Length];
            for (int i = 0; i < source.Length; ++i) _floats[i] = source[i] / 255.0f;
            if (Gpu is null)
            {
                _floatBytes = new byte[checked(_floats.Length * sizeof(float))];
                Buffer.BlockCopy(_floats, 0, _floatBytes, 0, _floatBytes.Length);
                _output = new byte[checked(outSamples * sizeof(float))];
                _floats = [];
            }
            else _outputFloat = new float[outSamples];
            _source8 = null;
        }
        else _output = new byte[outSamples];
    }

    private void PrepareFontAtlas()
    {
        _px = Positive("px"); _first = Nonnegative("first"); _count = Positive("count");
        _ow = Positive("w"); _oh = Positive("h");
        _ = checked(_first + _count);
        _output = new byte[checked(_ow * _oh)];
        if (_case.Op == "tt_bake") _baked = new BakedChar[_count];
        else
        {
            _oversample = Positive("oversample"); _padding = Nonnegative("padding");
            if (_oversample > 8 || _padding >= _ow || _padding >= _oh) throw new FormatException("invalid pack parameters");
            _packed = new PackedChar[_count];
        }
    }

    private void PrepareSdf()
    {
        _px = Positive("px"); _first = Nonnegative("first"); _count = Positive("count");
        _padding = Nonnegative("padding"); _onedge = Nonnegative("onedge");
        if (_onedge > 255) throw new FormatException("onedge must be in 0..255");
        _distScale = _case.Float("scale");
        _ = checked(_first + _count);
        _font = StbTrueTypeFont.LoadIndex(_file.AsMemory(), 0, _options);
        _codepoints = new int[_count];
        _sdf = new SdfResult[_count];
        for (int i = 0; i < _count; ++i) _codepoints[i] = _first + i;
    }

    private void PrepareRectangles()
    {
        if (_case.Input != "-") throw new FormatException("rectpack input must be -");
        _count = Positive("count"); _ow = Positive("w"); _oh = Positive("h");
        int min = Positive("min"), max = Positive("max"), seed = Nonnegative("seed");
        if (max < min) throw new FormatException("invalid rectangle range");
        uint range = checked((uint)(max - min) + 1u), state = (uint)seed;
        _rectangles = new PackRect[_count]; _rectTemplate = new PackRect[_count];
        for (int i = 0; i < _count; ++i)
        {
            state = unchecked(state * 1664525u + 1013904223u);
            int w = min + (int)(state % range);
            state = unchecked(state * 1664525u + 1013904223u);
            int h = min + (int)(state % range);
            _rectTemplate[i] = new PackRect(w, h, i);
        }
    }

    private void PrepareDxt()
    {
        if (Components() != 4) throw new UnsupportedCaseException("DXT currently supports RGBA only");
        _dxtFormat = _case.Required("format") switch
        {
            "bc1" => DxtFormat.Bc1,
            "bc3" => DxtFormat.Bc3,
            _ => throw new UnsupportedCaseException("unsupported DXT format"),
        };
        if (_case.Required("mode") != "normal") throw new UnsupportedCaseException("unsupported DXT mode");
        _source8 = StbImage.Load(_file, (ColorComponents)4, _decodeOptions);
        _iw = _source8.Width; _ih = _source8.Height;
        _output = new byte[checked((int)StbDxt.GetCompressedSize(_iw, _ih, _dxtFormat))];
    }

    private void PreparePerlin()
    {
        if (_case.Input != "-") throw new FormatException("perlin input must be -");
        _ow = Positive("w"); _oh = Positive("h");
        _startX = _case.Float("startx"); _startY = _case.Float("starty");
        _z = _case.Float("z"); _step = _case.Float("step");
        _lacunarity = _case.Float("lacunarity"); _gain = _case.Float("gain");
        _octaves = Positive("octaves");
        if (_octaves > 32) throw new FormatException("octaves exceeds 32");
        _outputFloat = new float[checked(_ow * _oh)];
    }

    private int Positive(string key)
    {
        int value = _case.Integer(key);
        return value > 0 ? value : throw new FormatException($"{key} must be positive");
    }

    private int Nonnegative(string key)
    {
        int value = _case.Integer(key);
        return value >= 0 ? value : throw new FormatException($"{key} must be nonnegative");
    }

    public void RunChecked()
    {
        StbGpuStats? before = Gpu?.Stats;
        Run();
        CheckGpuDispatch(before);
    }

    public void CheckGpuDispatch(StbGpuStats? before)
    {
        if (Gpu is null || before is null) return;
        StbGpuStats after = Gpu.Stats;
        if (after.CpuFallbacks != before.Value.CpuFallbacks || after.GpuDispatches <= before.Value.GpuDispatches)
            throw new InvalidOperationException("GPU operation did not dispatch on GPU or used CPU fallback");
    }

    public void Run()
    {
        ColorComponents components = (ColorComponents)_components;
        switch (_case.Op)
        {
            case "decode": _decoded8 = StbImage.Load(_file, components, _decodeOptions); break;
            case "decode16": _decoded16 = StbImage.Load16(_file, components, _decodeOptions); break;
            case "decodef": _decodedFloat = StbImage.LoadFloat(_file, components, _decodeOptions); break;
            case "decode_into":
                if (!StbImage.TryLoad(_file, _output, out _, out _, components, _decodeOptions)) throw new InvalidOperationException("TryLoad failed");
                break;
            case "decode16_into":
                if (!StbImage.TryLoad16(_file, _output16, out _, out _, components, _decodeOptions)) throw new InvalidOperationException("TryLoad16 failed");
                break;
            case "decodef_into":
                if (!StbImage.TryLoadFloat(_file, _outputFloat, out _, out _, components, _decodeOptions)) throw new InvalidOperationException("TryLoadFloat failed");
                break;
            case "encode_png": case "encode_jpeg": case "encode_bmp": case "encode_tga": case "encode_hdr": RunEncode(); break;
            case "resize": RunResize(); break;
            case "tt_bake":
                if (StbTrueType.BakeFontBitmap(_file, 0, _px, _output, _ow, _oh, _first, _baked, _options) <= 0)
                    throw new InvalidOperationException("font bake failed or atlas is too small");
                break;
            case "tt_pack":
                using (StbFontPacker packer = StbFontPacker.Begin(_output, _ow, _oh, padding: _padding, options: _options))
                {
                    packer.SetOversampling(_oversample, _oversample);
                    if (!packer.PackFontRange(_file, 0, _px, _first, _packed)) throw new InvalidOperationException("font pack failed or atlas is too small");
                }
                break;
            case "tt_sdf": RunSdf(); break;
            case "rectpack":
                Array.Copy(_rectTemplate, _rectangles, _count);
                _ = StbRectPack.PackRects(_ow, _oh, _rectangles, nodeCount: _ow, options: _options);
                break;
            case "dxt": RunDxt(); break;
            case "perlin_fbm": RunPerlin(); break;
            default: throw new UnsupportedCaseException("unsupported operation");
        }
    }

    private void RunEncode()
    {
        MemoryStream stream = _encoded ?? throw new InvalidOperationException("missing encode stream");
        stream.Position = 0;
        stream.SetLength(0);
        switch (_case.Op)
        {
            case "encode_png": StbImageWrite.WritePng(stream, _source8!, _writeOptions); break;
            case "encode_jpeg": StbImageWrite.WriteJpeg(stream, _source8!, _quality, _writeOptions); break;
            case "encode_bmp": StbImageWrite.WriteBmp(stream, _source8!, _writeOptions); break;
            case "encode_tga": StbImageWrite.WriteTga(stream, _source8!, _writeOptions); break;
            case "encode_hdr": StbImageWrite.WriteHdr(stream, _sourceFloat!, _writeOptions); break;
            default: throw new InvalidOperationException("not an encode operation");
        }
    }

    private void RunResize()
    {
        if (Gpu is null)
        {
            ReadOnlyMemory<byte> source = _resizeType == ResizeDataType.Fp32 ? _floatBytes : _source8!.ReadOnlyMemory;
            StbImageResize.ResizeMemory(source, _iw, _ih, _output, _ow, _oh, ResizePixelLayout.Rgba, _resizeType, options: _resizeOptions);
        }
        else
        {
            using StbGpuResizePlan plan = StbGpuResizePlan.Create(Gpu, _iw, _ih, _ow, _oh, ResizePixelLayout.Rgba, _resizeType);
            if (_resizeType == ResizeDataType.Fp32) plan.ResizeFloat(_floats, _outputFloat, StbGpuExecutionMode.ForceGpu);
            else plan.Resize(_source8!.ReadOnlyMemory, _output, StbGpuExecutionMode.ForceGpu);
        }
    }

    private void RunSdf()
    {
        StbTrueTypeFont font = _font ?? throw new InvalidOperationException("missing font");
        float scale = font.ScaleForPixelHeight(_px);
        if (Gpu is not null)
        {
            _gpuSdf = StbGpuTrueType.RenderCodepointSdfBatch(Gpu, font, scale, _codepoints, _padding,
            (byte)_onedge, _distScale, StbGpuExecutionMode.ForceGpu);
        }
        else
        {
            for (int i = 0; i < _count; ++i)
            {
                byte[] pixels = font.GetCodepointSdf(scale, _codepoints[i], _padding, (byte)_onedge, _distScale,
                out GlyphBitmapMetrics metrics, _options);
                _sdf[i] = new SdfResult(metrics, pixels);
            }
        }
    }

    private void RunDxt()
    {
        if (Gpu is null)
            StbDxt.CompressImageMemory(_source8!.ReadOnlyMemory, _iw, _ih, 0, _output, _dxtFormat, DxtCompressionMode.Normal, _options);
        else
            StbGpuDxt.CompressImage(Gpu, _source8!.ReadOnlyMemory, _iw, _ih, _output, _dxtFormat,
            mode: DxtCompressionMode.Normal, modeOverride: StbGpuExecutionMode.ForceGpu);
    }

    private void RunPerlin()
    {
        if (Gpu is null)
            StbPerlin.FillFbm2DMemory(_outputFloat, _ow, _oh, _startX, _startY, _step, _step, _z, _lacunarity, _gain, _octaves, options: _options);
        else
            StbGpuPerlin.FillFbm2D(Gpu, _outputFloat, _ow, _oh, _startX, _startY, _step, _step, _z, _lacunarity, _gain, _octaves,
            StbGpuExecutionMode.ForceGpu);
    }

    public ulong Checksum()
    {
        var hash = new Fnv64();
        switch (_case.Op)
        {
            case "decode": hash.Add(_decoded8!.AsReadOnlySpan()); break;
            case "decode16": hash.AddSamples(MemoryMarshal.AsBytes(_decoded16!.AsReadOnlySpan()), 2); break;
            case "decodef": hash.AddSamples(MemoryMarshal.AsBytes(_decodedFloat!.AsReadOnlySpan()), 4); break;
            case "decode_into": hash.Add(_output); break;
            case "decode16_into": hash.AddSamples(MemoryMarshal.AsBytes(_output16.AsSpan()), 2); break;
            case "decodef_into": case "perlin_fbm": hash.AddSamples(MemoryMarshal.AsBytes(_outputFloat.AsSpan()), 4); break;
            case "encode_png":
            case "encode_jpeg":
            case "encode_bmp":
            case "encode_tga":
            case "encode_hdr":
                MemoryStream stream = _encoded ?? throw new InvalidOperationException("missing encode stream");
                hash.AddU64((ulong)stream.Length);
                hash.Add(stream.GetBuffer().AsSpan(0, checked((int)stream.Length)));
                break;
            case "tt_sdf":
                for (int i = 0; i < _count; ++i)
                {
                    GlyphBitmapMetrics metrics = Gpu is null ? _sdf[i].Metrics : _gpuSdf![i].Metrics;
                    byte[] pixels = Gpu is null ? _sdf[i].Pixels : _gpuSdf![i].Pixels;
                    if (pixels.Length == 0) metrics = default;
                    hash.AddU32(unchecked((uint)metrics.Width)); hash.AddU32(unchecked((uint)metrics.Height));
                    hash.AddU32(unchecked((uint)metrics.XOffset)); hash.AddU32(unchecked((uint)metrics.YOffset));
                    hash.Add(pixels);
                }
                break;
            case "rectpack":
                foreach (PackRect rect in _rectangles)
                {
                    hash.AddU32(rect.WasPacked ? 1u : 0u);
                    hash.AddU32(rect.WasPacked ? unchecked((uint)rect.X) : 0u);
                    hash.AddU32(rect.WasPacked ? unchecked((uint)rect.Y) : 0u);
                }
                break;
            case "resize" when _resizeType == ResizeDataType.Fp32:
                if (Gpu is null) hash.AddSamples(_output, 4);
                else hash.AddSamples(MemoryMarshal.AsBytes(_outputFloat.AsSpan()), 4);
                break;
            default: hash.Add(_output); break;
        }
        return hash.Value;
    }

    public void ReleaseIteration()
    {
        _decoded8 = null; _decoded16 = null; _decodedFloat = null;
        _gpuSdf = null;
        if (_case.Op == "tt_sdf" && Gpu is null) Array.Clear(_sdf);
    }

    public void Dispose() => _encoded?.Dispose();
}
