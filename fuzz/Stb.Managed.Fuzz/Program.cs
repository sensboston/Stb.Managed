using System.Diagnostics;
using SharpFuzz;
using Stb.Managed.Fuzz;

if (args.Length >= 1 && string.Equals(args[0], "--self-test", StringComparison.Ordinal))
{
    string? corpusDir = args.Length >= 2 ? args[1] : Environment.GetEnvironmentVariable("STB_CORPUS_DIR");
    return RunSelfTest(corpusDir);
}

if (args.Length >= 3 && string.Equals(args[0], "--mutate", StringComparison.Ordinal))
{
    string targetName = args[1];
    int seconds = int.Parse(args[2], System.Globalization.CultureInfo.InvariantCulture);
    string? seedDir = args.Length >= 4 ? args[3] : null;
    string? crashDir = args.Length >= 5 ? args[4] : "fuzz-crashes";
    return RunMutationLoop(targetName, seconds, seedDir, crashDir);
}

if (args.Length >= 2 && string.Equals(args[0], "--afl", StringComparison.Ordinal))
{
    IFuzzTarget target = FuzzTargets.GetByName(args[1]);
    Fuzzer.OutOfProcess.Run(stream =>
    {
        using var ms = new MemoryStream();
        stream.CopyTo(ms);
        target.Run(ms.GetBuffer().AsSpan(0, (int)ms.Length));
    });
    return 0;
}

string libFuzzerTargetName =
    (args.Length >= 2 && string.Equals(args[0], "--libfuzzer", StringComparison.Ordinal))
        ? args[1]
        : Environment.GetEnvironmentVariable("STB_FUZZ_TARGET") ?? "image";

IFuzzTarget libFuzzerTarget = FuzzTargets.GetByName(libFuzzerTargetName);
Fuzzer.LibFuzzer.Run(span => libFuzzerTarget.Run(span));
return 0;

static int RunSelfTest(string? corpusDir)
{
    var rng = new Random(0x57B_F022);
    List<byte[]> seeds = BuildBuiltinSeeds(corpusDir, "all");
    Console.WriteLine($"Running SharpFuzz harness self-test across {FuzzTargets.All.Length} targets ({seeds.Count} seeds)...");

    foreach (IFuzzTarget target in FuzzTargets.All)
    {
        int execs = 0;
        foreach (byte[] seed in seeds)
        {
            target.Run(seed);
            execs++;

            // Also run 4 deterministic mutations per seed.
            for (int m = 0; m < 4; ++m)
            {
                byte[] mutated = Mutate(seed, rng);
                target.Run(mutated);
                execs++;
            }
        }

        // Also feed 64 pure random buffers of varying lengths.
        for (int i = 0; i < 64; ++i)
        {
            byte[] buf = new byte[rng.Next(0, 512)];
            rng.NextBytes(buf);
            target.Run(buf);
            execs++;
        }

        Console.WriteLine($"  [OK] fuzz target '{target.Name}': {execs} inputs executed with 0 unhandled exceptions.");
    }

    return 0;
}

static int RunMutationLoop(string targetName, int seconds, string? seedDir, string? crashDir)
{
    IFuzzTarget target = FuzzTargets.GetByName(targetName);
    List<byte[]> seeds = BuildBuiltinSeeds(seedDir, targetName);
    var rng = new Random(Environment.TickCount ^ targetName.GetHashCode(StringComparison.Ordinal));
    var sw = Stopwatch.StartNew();
    long execs = 0;

    Console.WriteLine($"Fuzzing target '{target.Name}' for {seconds}s with {seeds.Count} initial seeds...");
    while (sw.Elapsed.TotalSeconds < seconds)
    {
        byte[] seed = seeds[rng.Next(seeds.Count)];
        byte[] mutated = Mutate(seed, rng);
        try
        {
            target.Run(mutated);
            execs++;
        }
        catch (Exception ex)
        {
            Directory.CreateDirectory(crashDir ?? "fuzz-crashes");
            string crashPath = Path.Combine(crashDir ?? "fuzz-crashes", $"crash-{target.Name}-{execs}.bin");
            File.WriteAllBytes(crashPath, mutated);
            Console.Error.WriteLine($"CRASH on target '{target.Name}' after {execs} execs! Saved {mutated.Length} bytes to '{crashPath}':");
            Console.Error.WriteLine(ex);
            return 1;
        }
    }

    Console.WriteLine($"Fuzz target '{target.Name}' completed cleanly: {execs:N0} executions in {sw.Elapsed.TotalSeconds:F1}s ({execs / Math.Max(0.01, sw.Elapsed.TotalSeconds):N0} exec/s).");
    foreach ((string outcome, long count) in FuzzStats.Outcomes.OrderByDescending(o => o.Value).Take(25))
    {
        Console.WriteLine($"  {count,14:N0}  {outcome}");
    }

    return 0;
}

static List<byte[]> BuildBuiltinSeeds(string? corpusDir, string targetName)
{
    List<byte[]> seeds =
    [
        [],
        [0],
        [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A], // PNG signature
        [0xFF, 0xD8, 0xFF, 0xE0, 0x00, 0x10, 0x4A, 0x46, 0x49, 0x46, 0x00], // JPEG SOI/APP0
        [0x47, 0x49, 0x46, 0x38, 0x39, 0x61, 0x01, 0x00, 0x01, 0x00], // GIF89a
        [0x42, 0x4D, 0x36, 0x00, 0x00, 0x00], // BMP
        [0x38, 0x42, 0x50, 0x53, 0x00, 0x01], // PSD
        [0x00, 0x01, 0x00, 0x00, 0x00, 0x01, 0x00, 0x10, 0x00, 0x00, 0x00, 0x00], // TTF header
    ];

    // Synthesize valid tiny PNG, JPEG, BMP, TGA seeds using StbImageWrite.
    byte[] tiny = new byte[16 * 16 * 4];
    for (int i = 0; i < tiny.Length; ++i)
    {
        tiny[i] = (byte)(i * 19);
    }

    seeds.Add(Stb.Managed.StbImageWrite.WritePng(tiny, 16, 16, Stb.Managed.ColorComponents.RedGreenBlueAlpha));
    seeds.Add(Stb.Managed.StbImageWrite.WriteJpeg(tiny, 16, 16, Stb.Managed.ColorComponents.RedGreenBlueAlpha, quality: 75));
    seeds.Add(Stb.Managed.StbImageWrite.WriteBmp(tiny, 16, 16, Stb.Managed.ColorComponents.RedGreenBlueAlpha));
    seeds.Add(Stb.Managed.StbImageWrite.WriteTga(tiny, 16, 16, Stb.Managed.ColorComponents.RedGreenBlueAlpha));

    // Every corpus file up to 1 MB that fits the target: images for the decoders, fonts for TrueType.
    if (!string.IsNullOrEmpty(corpusDir) && Directory.Exists(corpusDir))
    {
        foreach (string path in Directory.EnumerateFiles(corpusDir, "*", SearchOption.AllDirectories).Order(StringComparer.Ordinal))
        {
            var fi = new FileInfo(path);
            if (fi.Length == 0 || fi.Length > 1024 * 1024)
            {
                continue;
            }

            byte[] data = File.ReadAllBytes(path);
            bool fits = targetName switch
            {
                "image" => Stb.Managed.StbImage.DetectFormat(data) != Stb.Managed.ImageFormat.Unknown,
                "truetype" => path.EndsWith(".ttf", StringComparison.OrdinalIgnoreCase) || path.EndsWith(".otf", StringComparison.OrdinalIgnoreCase) || path.EndsWith(".ttc", StringComparison.OrdinalIgnoreCase),
                _ => true,
            };
            if (fits)
            {
                seeds.Add(data);
            }
        }
    }

    return seeds;
}

static byte[] Mutate(byte[] src, Random rng)
{
    if (src.Length == 0)
    {
        byte[] fresh = new byte[rng.Next(1, 64)];
        rng.NextBytes(fresh);
        return fresh;
    }

    int op = rng.Next(5);
    switch (op)
    {
        case 0: // Flip 1..4 random bytes
        {
            byte[] copy = (byte[])src.Clone();
            int flips = rng.Next(1, 5);
            for (int i = 0; i < flips; ++i)
            {
                copy[rng.Next(copy.Length)] ^= (byte)(1 << rng.Next(8));
            }

            return copy;
        }
        case 1: // Truncate at random boundary
        {
            int newLen = rng.Next(0, src.Length);
            byte[] copy = new byte[newLen];
            Buffer.BlockCopy(src, 0, copy, 0, newLen);
            return copy;
        }
        case 2: // Overwrite 4-byte integer with interesting boundary values
        {
            byte[] copy = (byte[])src.Clone();
            if (copy.Length >= 4)
            {
                int pos = rng.Next(copy.Length - 3);
                uint interesting = rng.Next(6) switch
                {
                    0 => 0u,
                    1 => 1u,
                    2 => 0x7FFFFFFFu,
                    3 => 0x80000000u,
                    4 => 0xFFFFFFFFu,
                    _ => 0x0000FFFFu,
                };
                System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(copy.AsSpan(pos, 4), interesting);
            }

            return copy;
        }
        case 3: // Duplicate/splice slice
        {
            int cut = rng.Next(0, src.Length);
            int extra = Math.Min(64, src.Length - cut);
            byte[] copy = new byte[Math.Min(src.Length + extra, 512 * 1024)];
            Buffer.BlockCopy(src, 0, copy, 0, Math.Min(src.Length, copy.Length));
            int rem = copy.Length - src.Length;
            if (rem > 0)
            {
                Buffer.BlockCopy(src, cut, copy, src.Length, rem);
            }

            return copy;
        }
        default: // Replace a byte with extreme value (0x00, 0xFF, 0x7F, 0x80)
        {
            byte[] copy = (byte[])src.Clone();
            ReadOnlySpan<byte> interesting = [0x00, 0x01, 0x7F, 0x80, 0xFF];
            copy[rng.Next(copy.Length)] = interesting[rng.Next(interesting.Length)];
            return copy;
        }
    }
}
