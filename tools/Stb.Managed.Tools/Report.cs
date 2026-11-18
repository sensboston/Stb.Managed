using System.Globalization;
using System.IO.Compression;
using System.Text;
using System.Text.Json;

namespace Stb.Managed.Tools;

/// <summary>
/// docs/benchmarks.md and two SVG charts from bench/results/&lt;host&gt;/ folders or their zips
/// (native.csv, managed_1t.csv, managed_0t.csv, managed_default.csv, sysinfo.json). The output is deterministic.
/// </summary>
internal static class Report
{
    private static readonly (string Key, string Title, string File, string Color)[] Modes =
    [
        ("1t", "1 thread", "managed_1t.csv", "#64748b"),
        ("0t", "all cores", "managed_0t.csv", "#2563eb"),
        ("def", "default", "managed_default.csv", "#059669"),
    ];

    private static readonly string[] Categories = ["decode", "encode", "resize", "truetype", "other"];
    private static readonly string[] MachineColors = ["#2563eb", "#059669", "#d97706", "#7c3aed", "#db2777", "#0891b2", "#65a30d"];
    private static readonly HashSet<string> IntoOps = ["decode_into", "decode16_into", "decodef_into"];
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;
    private const string Dash = "—";

    internal static int Run(string root, string[] args)
    {
        string repo = Program.Option(args, "--repo") ?? root;
        var inputs = new List<string>();
        for (int i = 0; i < args.Length; i++)
        {
            if (args[i] == "--repo")
            {
                i++;
                continue;
            }

            inputs.Add(args[i]);
        }

        if (inputs.Count == 0)
        {
            Console.Error.WriteLine("usage: report <results folder or zip>... [--repo DIR]");
            return 2;
        }

        List<Machine> machines = [.. inputs.Select(Machine.Load).OrderBy(m => m.Cpu.ToLowerInvariant(), StringComparer.Ordinal).ThenBy(m => m.Host, StringComparer.Ordinal)];
        foreach (Machine m in machines)
        {
            m.Label = machines.Count(x => x.Cpu == m.Cpu) > 1 ? $"{m.Cpu} ({m.Host})" : m.Cpu;
        }

        var ops = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (Machine m in machines)
        {
            foreach ((string c, Row r) in m.Managed["1t"])
            {
                ops.TryAdd(c, r.Op);
            }
        }

        List<string> Ordered(bool into) => [.. ops.Where(o => IntoOps.Contains(o.Value) == into).Select(o => o.Key)
            .OrderBy(c => Array.IndexOf(Categories, Category(ops[c]))).ThenBy(c => c, StringComparer.Ordinal)];
        List<string> main = Ordered(false);
        List<string> into = Ordered(true);

        string images = Path.Combine(repo, "docs", "images");
        Directory.CreateDirectory(images);
        File.WriteAllText(Path.Combine(images, "benchmark-overview.svg"), Overview(machines, main.Count));
        File.WriteAllText(Path.Combine(images, "benchmark-operations.svg"), Operations(machines, main, ops));
        File.WriteAllText(Path.Combine(repo, "docs", "benchmarks.md"), Markdown(machines, main, into, ops));
        foreach (Machine m in machines)
        {
            Console.WriteLine($"{m.Label}: " + string.Join(", ", Modes.Where(k => m.Geomean(k.Key) is not null).Select(k => $"{k.Title} {F(m.Geomean(k.Key)!.Value)}x")));
        }

        return 0;
    }

    private static string Category(string op) =>
        op.StartsWith("decode", StringComparison.Ordinal) ? "decode"
        : op.StartsWith("encode", StringComparison.Ordinal) ? "encode"
        : op == "resize" ? "resize"
        : op.StartsWith("tt_", StringComparison.Ordinal) ? "truetype"
        : "other";

    private static string F(double v) => v.ToString("0.00", Inv);

    private static string X(string s) => s.Replace("&", "&amp;", StringComparison.Ordinal).Replace("<", "&lt;", StringComparison.Ordinal);

    private static string Overview(List<Machine> machines, int operations)
    {
        const int W = 1000, H = 480, L = 70, R = 20, T = 50, B = 70;
        double top = Math.Max(1.0, machines.SelectMany(m => Modes.Select(k => m.Geomean(k.Key) ?? 0)).Max()) * 1.4;
        double step = top > 4 ? 1.0 : 0.5;
        double Y(double v) => T + ((H - T - B) * (1 - (v / top)));
        var s = new StringBuilder();
        s.Append(Inv, $"<svg xmlns=\"http://www.w3.org/2000/svg\" viewBox=\"0 0 {W} {H}\" font-family=\"Segoe UI, Helvetica, Arial, sans-serif\" font-size=\"12\">\n");
        s.Append(Inv, $"<rect width=\"{W}\" height=\"{H}\" fill=\"#ffffff\"/>\n");
        s.Append(Inv, $"<text x=\"{W / 2}\" y=\"28\" text-anchor=\"middle\" font-size=\"17\" font-weight=\"bold\">Stb.Managed vs. native stb, {operations} operations</text>\n");
        for (double v = 0; v <= top + 1e-9; v += step)
        {
            s.Append(Inv, $"<line x1=\"{L}\" x2=\"{W - R}\" y1=\"{Y(v):0.#}\" y2=\"{Y(v):0.#}\" stroke=\"#e5e7eb\"/><text x=\"{L - 8}\" y=\"{Y(v) + 4:0.#}\" text-anchor=\"end\">{v:0.0}</text>\n");
        }

        s.Append(Inv, $"<text transform=\"translate(18 {(T + H - B) / 2}) rotate(-90)\" text-anchor=\"middle\">speedup over native C (geometric mean)</text>\n");
        double group = (W - L - R) / (double)machines.Count;
        double bar = group * 0.8 / Modes.Length;
        for (int i = 0; i < machines.Count; i++)
        {
            Machine m = machines[i];
            double x0 = L + (group * i) + (group * 0.1);
            for (int k = 0; k < Modes.Length; k++)
            {
                if (m.Geomean(Modes[k].Key) is not double v)
                {
                    continue;
                }

                double x = x0 + (bar * k);
                s.Append(Inv, $"<rect x=\"{x:0.#}\" y=\"{Y(v):0.#}\" width=\"{bar:0.#}\" height=\"{Y(0) - Y(v):0.#}\" fill=\"{Modes[k].Color}\"/>");
                s.Append(Inv, $"<text x=\"{x + (bar / 2):0.#}\" y=\"{Y(v) - 4:0.#}\" text-anchor=\"middle\" font-size=\"10\">{F(v)}x</text>\n");
            }

            double cx = L + (group * (i + 0.5));
            s.Append(Inv, $"<text x=\"{cx:0.#}\" y=\"{H - B + 18}\" text-anchor=\"middle\" font-size=\"11\">{X(m.Label)}</text>");
            s.Append(Inv, $"<text x=\"{cx:0.#}\" y=\"{H - B + 33}\" text-anchor=\"middle\" font-size=\"11\" fill=\"#4b5563\">{X(m.Gpu.Length > 0 ? m.Gpu : "no GPU")}</text>\n");
        }

        s.Append(Inv, $"<line x1=\"{L}\" x2=\"{W - R}\" y1=\"{Y(1):0.#}\" y2=\"{Y(1):0.#}\" stroke=\"#dc2626\" stroke-width=\"1.5\" stroke-dasharray=\"6 4\"/>\n");
        s.Append(Inv, $"<line x1=\"{L}\" x2=\"{W - R}\" y1=\"{Y(0):0.#}\" y2=\"{Y(0):0.#}\" stroke=\"#111827\"/>\n");
        string[] legend = ["native C = 1.00x", "1 thread", "multithreaded", "default (threads + GPU)"];
        string[] colors = ["#dc2626", Modes[0].Color, Modes[1].Color, Modes[2].Color];
        for (int i = 0; i < legend.Length; i++)
        {
            double y = T + 6 + (i * 18);
            s.Append(i == 0
                ? string.Create(Inv, $"<line x1=\"{W - 230}\" x2=\"{W - 206}\" y1=\"{y:0.#}\" y2=\"{y:0.#}\" stroke=\"{colors[i]}\" stroke-width=\"1.5\" stroke-dasharray=\"6 4\"/>")
                : string.Create(Inv, $"<rect x=\"{W - 230}\" y=\"{y - 6:0.#}\" width=\"24\" height=\"12\" fill=\"{colors[i]}\"/>"));
            s.Append(Inv, $"<text x=\"{W - 198}\" y=\"{y + 4:0.#}\">{legend[i]}</text>\n");
        }

        return s.Append("</svg>\n").ToString();
    }

    private static string Operations(List<Machine> machines, List<string> cases, Dictionary<string, string> ops)
    {
        const int W = 1000, L = 210, R = 190, T = 50;
        double rowH = 8 + (10 * machines.Count);
        var ys = new List<double>();
        var separators = new List<double>();
        double y = T;
        string? previous = null;
        foreach (string c in cases)
        {
            string cat = Category(ops[c]);
            if (previous is not null && cat != previous)
            {
                separators.Add(y + 4);
                y += 10;
            }

            ys.Add(y);
            previous = cat;
            y += rowH;
        }

        int height = (int)y + 60;
        var values = machines.SelectMany(m => cases.Select(c => m.Speedup("1t", c))).OfType<double>().ToList();
        double lo = Math.Max(Math.Floor(Math.Log2(Math.Min(values.Min(), 0.8))), -3);
        double hi = Math.Min(Math.Ceiling(Math.Log2(Math.Max(values.Max(), 2.0))), 6);
        double Px(double v) => L + ((W - L - R) * (Math.Log2(v) - lo) / (hi - lo));
        var s = new StringBuilder();
        s.Append(Inv, $"<svg xmlns=\"http://www.w3.org/2000/svg\" viewBox=\"0 0 {W} {height}\" font-family=\"Segoe UI, Helvetica, Arial, sans-serif\" font-size=\"11\">\n");
        s.Append(Inv, $"<rect width=\"{W}\" height=\"{height}\" fill=\"#ffffff\"/>\n");
        s.Append(Inv, $"<text x=\"{W / 2}\" y=\"26\" text-anchor=\"middle\" font-size=\"15\" font-weight=\"bold\">speedup over native C per operation (1 thread)</text>\n");
        for (double e = lo; e <= hi; e++)
        {
            double v = Math.Pow(2, e);
            s.Append(Inv, $"<line x1=\"{Px(v):0.#}\" x2=\"{Px(v):0.#}\" y1=\"{T - 6}\" y2=\"{y}\" stroke=\"#e5e7eb\"/><text x=\"{Px(v):0.#}\" y=\"{y + 16:0.#}\" text-anchor=\"middle\">{v.ToString("0.###", Inv)}</text>\n");
        }

        foreach (double sep in separators)
        {
            s.Append(Inv, $"<line x1=\"10\" x2=\"{W - R}\" y1=\"{sep:0.#}\" y2=\"{sep:0.#}\" stroke=\"#cbd5e1\"/>\n");
        }

        for (int r = 0; r < cases.Count; r++)
        {
            string c = cases[r];
            s.Append(Inv, $"<text x=\"{L - 8}\" y=\"{ys[r] + (rowH / 2) + 3:0.#}\" text-anchor=\"end\" font-family=\"Consolas, monospace\">{Category(ops[c])} {X(c)}</text>\n");
            for (int i = 0; i < machines.Count; i++)
            {
                if (machines[i].Speedup("1t", c) is not double v)
                {
                    continue;
                }

                double by = ys[r] + 4 + (10 * i);
                double x0 = Px(Math.Pow(2, lo));
                s.Append(Inv, $"<rect x=\"{x0:0.#}\" y=\"{by:0.#}\" width=\"{Math.Max(1, Px(v) - x0):0.#}\" height=\"8\" fill=\"{MachineColors[i % MachineColors.Length]}\"/><text x=\"{Px(v) + 4:0.#}\" y=\"{by + 7:0.#}\" font-size=\"8\">{F(v)}</text>\n");
            }
        }

        s.Append(Inv, $"<line x1=\"{Px(1):0.#}\" x2=\"{Px(1):0.#}\" y1=\"{T - 6}\" y2=\"{y}\" stroke=\"#dc2626\" stroke-width=\"1.5\" stroke-dasharray=\"6 4\"/>\n");
        s.Append(Inv, $"<text x=\"{(L + W - R) / 2}\" y=\"{y + 34:0.#}\" text-anchor=\"middle\">speedup over native C, T(native) / T(managed)</text>\n");
        for (int i = 0; i < machines.Count; i++)
        {
            double ly = T + 4 + (i * 14);
            s.Append(Inv, $"<rect x=\"{W - 175}\" y=\"{ly:0.#}\" width=\"14\" height=\"9\" fill=\"{MachineColors[i % MachineColors.Length]}\"/><text x=\"{W - 156}\" y=\"{ly + 8:0.#}\">{X(machines[i].Label)}</text>\n");
        }

        return s.Append("</svg>\n").ToString();
    }

    private static string Markdown(List<Machine> machines, List<string> main, List<string> into, Dictionary<string, string> ops)
    {
        var s = new StringBuilder();
        s.Append("""
            # Benchmarks

            Stb.Managed compared with the native stb libraries on the same machine and the same inputs.
            The numbers are speedups `T(native) / T(managed)` of the median time of each operation;
            above 1.00 the managed code is faster. Generated by `Stb.Managed.Tools report` from the
            `bench/results/<host>/` folders (or their zips) produced by `bench/run.ps1` / `bench/run.sh`.

            - **native**: the pinned stb C sources, Release build, one thread; the compiler and flags
              are listed per machine below (taken from the native run header).
            - **1 thread**: Stb.Managed with `MaxDegreeOfParallelism = 1` and no GPU: the same work as
              the native code.
            - **all cores**: Stb.Managed with automatic parallelism (`MaxDegreeOfParallelism = 0`), no GPU.
            - **default**: what a caller gets without configuration: all cores and `StbGpuMode.Auto`, which
              sends DXT compression, and large resizes on a discrete GPU, to a CUDA or OpenCL device when
              one is found (initialized before timing). GPU results can differ from the CPU in the last
              bit; on machines without a GPU this equals all cores.
            - each case is warmed up for at least 3 runs (and at least 500 ms), then measured until at
              least 10 runs (and at least 1000 ms) on a monotonic clock; the tables show the median.
            - `skipped` and `error` cases are shown as dashes and excluded from the geometric means.

            ![overview](images/benchmark-overview.svg)

            ## Machines

            | CPU | ISA | OS | .NET | GPU | native build | 1 thread | all cores | default |
            |---|---|---|---|---|---|---:|---:|---:|

            """.Replace("\r\n", "\n", StringComparison.Ordinal));
        foreach (Machine m in machines)
        {
            string gpu = m.Gpu.Length > 0 ? $"{m.Gpu} ({m.GpuBackend})" : Dash;
            string geo = string.Join(" | ", Modes.Select(k => m.Geomean(k.Key) is double g ? $"{F(g)}x" : Dash));
            s.Append(Inv, $"| {m.Cpu} | {m.Isa} | {m.Os} | {m.Dotnet} | {gpu} | {m.NativeBuild} | {geo} |\n");
        }

        s.Append("\n## Where the managed code is slower\n\nOperations that are more than 10% slower than the native code with one thread:\n\n");
        foreach (Machine m in machines)
        {
            var slow = main.Select(c => (c, v: m.Speedup("1t", c))).Where(t => t.v < 0.9).OrderBy(t => t.v).Select(t => $"`{t.c}` {F(t.v!.Value)}x").ToList();
            s.Append(Inv, $"- **{m.Label}**: {(slow.Count > 0 ? string.Join(", ", slow) : "none")}\n");
        }

        s.Append("\n## Operations\n\n![operations](images/benchmark-operations.svg)\n\n| operation | category | ");
        s.Append(string.Join(" | ", machines.Select(m => $"{m.Label}: native ms / 1 thread / all cores / default")));
        s.Append(" |\n|---|---|").Append(string.Concat(Enumerable.Repeat("---|", machines.Count))).Append('\n');
        foreach (string c in main)
        {
            var cells = machines.Select(m =>
            {
                var parts = new List<string> { m.Native.TryGetValue(c, out Row? n) && n.Ok ? n.Median!.Value.ToString("0.000", Inv) : Dash };
                foreach (var k in Modes)
                {
                    string cell = m.Speedup(k.Key, c) is double v ? $"{F(v)}x" : Dash;
                    if (k.Key == "1t" && m.ChecksumMark(c) is { } mark)
                    {
                        cell += $" ({mark})";
                    }

                    parts.Add(cell);
                }

                return string.Join(" / ", parts);
            });
            s.Append(Inv, $"| `{c}` | {Category(ops[c])} | {string.Join(" | ", cells)} |\n");
        }

        s.Append("""

            `match` / `differ` compares the FNV-1a-64 output checksum of the native run with the managed
            one-thread run. A difference is expected for the PNG `engine=default` case (the managed default
            encoder is zlib-based, not stb's own) and for operations with float arithmetic (DXT, Perlin,
            JPEG encode, the PSD matte correction) where the native compiler folds multiplies and adds into
            FMA instructions; it is a diagnostic, not a failure.

            """.Replace("\r\n", "\n", StringComparison.Ordinal));
        if (into.Count > 0)
        {
            s.Append("""
                ## Managed only

                The `*_into` cases decode directly into caller-provided buffers, which the stb API has no
                equivalent for, so there is no native counterpart and no speedup. Times are medians in ms.

                """.Replace("\r\n", "\n", StringComparison.Ordinal));
            s.Append("| operation | ").Append(string.Join(" | ", machines.Select(m => $"{m.Label}: 1 thread / all cores / default"))).Append(" |\n|---|");
            s.Append(string.Concat(Enumerable.Repeat("---|", machines.Count))).Append('\n');
            foreach (string c in into)
            {
                var cells = machines.Select(m => string.Join(" / ", Modes.Select(k =>
                    m.Managed.TryGetValue(k.Key, out var rows) && rows.TryGetValue(c, out Row? r) && r.Ok ? r.Median!.Value.ToString("0.000", Inv) : Dash)));
                s.Append(Inv, $"| `{c}` | {string.Join(" | ", cells)} |\n");
            }
        }

        return s.ToString();
    }

    private sealed record Row(string Op, string Status, double? Median, string Checksum)
    {
        public bool Ok => Status is "ok" or "ok_no_native" && Median > 0;
    }

    private sealed class Machine
    {
        public string Host { get; init; } = string.Empty;
        public string Cpu { get; init; } = string.Empty;
        public string Isa { get; init; } = string.Empty;
        public string Os { get; init; } = string.Empty;
        public string Dotnet { get; init; } = string.Empty;
        public string Gpu { get; init; } = string.Empty;
        public string GpuBackend { get; init; } = string.Empty;
        public string NativeBuild { get; init; } = string.Empty;
        public Dictionary<string, Row> Native { get; init; } = [];
        public Dictionary<string, Dictionary<string, Row>> Managed { get; init; } = [];
        public List<string> Order { get; init; } = [];
        public string Label { get; set; } = string.Empty;

        public double? Speedup(string mode, string c) =>
            Native.TryGetValue(c, out Row? n) && n.Ok && Managed.TryGetValue(mode, out var rows) && rows.TryGetValue(c, out Row? m) && m.Ok
                ? n.Median!.Value / m.Median!.Value
                : null;

        public double? Geomean(string mode)
        {
            var values = Order.Select(c => Speedup(mode, c)).OfType<double>().ToList();
            return values.Count == 0 ? null : Math.Exp(values.Average(Math.Log));
        }

        public string? ChecksumMark(string c) =>
            Native.TryGetValue(c, out Row? n) && n.Ok && n.Checksum.Length > 0 && Managed["1t"].TryGetValue(c, out Row? m) && m.Ok && m.Checksum.Length > 0
                ? (n.Checksum == m.Checksum ? "match" : "differ")
                : null;

        public static Machine Load(string path)
        {
            Dictionary<string, string> files = Read(path);
            string Need(string name) => files.TryGetValue(name, out string? text) ? text : throw new InvalidDataException($"{path}: missing {name}");
            using JsonDocument info = JsonDocument.Parse(Need("sysinfo.json"));
            JsonElement r = info.RootElement;
            string Str(JsonElement e, string name) => e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out JsonElement v) && v.ValueKind == JsonValueKind.String ? v.GetString()! : string.Empty;
            (Dictionary<string, string> nativeMeta, Dictionary<string, Row> native) = ReadCsv(Need("native.csv"));
            var managed = new Dictionary<string, Dictionary<string, Row>>(StringComparer.Ordinal);
            foreach (var k in Modes)
            {
                if (files.TryGetValue(k.File, out string? text))
                {
                    managed[k.Key] = ReadCsv(text).Rows;
                }
            }

            if (!managed.TryGetValue("1t", out Dictionary<string, Row>? oneThread))
            {
                throw new InvalidDataException($"{path}: missing managed_1t.csv");
            }

            string isa = r.TryGetProperty("isa", out JsonElement isaEl)
                ? string.Join(", ", new[] { ("sse41", "SSE4.1"), ("avx2", "AVX2"), ("avx512f", "AVX-512F"), ("neon", "NEON") }
                    .Where(f => isaEl.TryGetProperty(f.Item1, out JsonElement b) && b.ValueKind == JsonValueKind.True).Select(f => f.Item2))
                : string.Empty;
            JsonElement gpu = r.TryGetProperty("gpu", out JsonElement g) ? g : default;
            string compiler = nativeMeta.GetValueOrDefault("compiler", "unknown");
            string flags = nativeMeta.GetValueOrDefault("flags", string.Empty);
            return new Machine
            {
                Host = Str(r, "hostname") is { Length: > 0 } h ? h : Path.GetFileNameWithoutExtension(path),
                Cpu = ShortCpu(Str(r, "cpu") is { Length: > 0 } c ? c : "unknown"),
                Isa = isa.Length > 0 ? isa : "none",
                Os = Str(r, "os"),
                Dotnet = Str(r, "dotnet"),
                Gpu = ShortGpu(Str(gpu, "name")),
                GpuBackend = Str(gpu, "backend"),
                NativeBuild = flags.Length > 0 ? $"{compiler}; {flags}" : compiler,
                Native = native,
                Managed = managed,
                Order = [.. oneThread.Keys],
            };
        }

        private static Dictionary<string, string> Read(string path)
        {
            var files = new Dictionary<string, string>(StringComparer.Ordinal);
            if (Directory.Exists(path))
            {
                foreach (string f in Directory.GetFiles(path).Order(StringComparer.Ordinal))
                {
                    files[Path.GetFileName(f)] = File.ReadAllText(f);
                }

                return files;
            }

            using ZipArchive zip = ZipFile.OpenRead(path);
            foreach (ZipArchiveEntry e in zip.Entries.Where(e => e.Name.Length > 0))
            {
                using var reader = new StreamReader(e.Open());
                files.TryAdd(e.Name, reader.ReadToEnd());
            }

            return files;
        }

        private static (Dictionary<string, string> Meta, Dictionary<string, Row> Rows) ReadCsv(string text)
        {
            var meta = new Dictionary<string, string>(StringComparer.Ordinal);
            var rows = new Dictionary<string, Row>(StringComparer.Ordinal);
            string[]? header = null;
            foreach (string raw in text.Split('\n'))
            {
                string line = raw.TrimEnd('\r');
                if (line.StartsWith('#'))
                {
                    int colon = line.IndexOf(':', StringComparison.Ordinal);
                    if (colon > 0)
                    {
                        meta[line[1..colon].Trim()] = line[(colon + 1)..].Trim();
                    }

                    continue;
                }

                if (line.Length == 0)
                {
                    continue;
                }

                string[] cells = [.. line.Split(',').Select(v => v.Trim('"'))];
                if (header is null)
                {
                    header = cells;
                    continue;
                }

                string Cell(string name) => cells[Array.IndexOf(header, name)];
                string status = Cell("status");
                double? median = status is "ok" or "ok_no_native" && double.TryParse(Cell("median_ms"), NumberStyles.Float, Inv, out double v) ? v : null;
                rows[Cell("case")] = new Row(Cell("op"), status, median, Cell("checksum"));
            }

            return (meta, rows);
        }

        private static string ShortCpu(string name)
        {
            string s = name.Replace("(R)", string.Empty, StringComparison.Ordinal).Replace("(TM)", string.Empty, StringComparison.Ordinal)
                .Replace(" CPU", string.Empty, StringComparison.Ordinal).Replace("Processor", string.Empty, StringComparison.Ordinal);
            int at = s.IndexOf('@', StringComparison.Ordinal);
            if (at >= 0)
            {
                s = s[..at];
            }

            s = s.Replace("Intel Core ", "Core ", StringComparison.Ordinal).Replace("Intel ", string.Empty, StringComparison.Ordinal)
                .Replace(", GenuineIntel", string.Empty, StringComparison.Ordinal).Replace(", AuthenticAMD", string.Empty, StringComparison.Ordinal);
            return string.Join(' ', s.Split(' ', StringSplitOptions.RemoveEmptyEntries));
        }

        private static string ShortGpu(string name) =>
            name.Replace("NVIDIA GeForce ", string.Empty, StringComparison.Ordinal).Replace("NVIDIA ", string.Empty, StringComparison.Ordinal)
                .Replace("(R)", string.Empty, StringComparison.Ordinal).Replace("(TM)", string.Empty, StringComparison.Ordinal).Trim();
    }
}
