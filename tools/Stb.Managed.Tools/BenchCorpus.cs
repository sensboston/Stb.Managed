using System.IO.Compression;
using System.Text.Json;

namespace Stb.Managed.Tools;

/// <summary>
/// Downloads the benchmark inputs (bench/corpus.json: a zip on the GitHub release page, see bench/CORPUS.md),
/// adds the two fonts from the test corpus and checks every file against bench/corpus/manifest.json.
/// </summary>
internal static class BenchCorpus
{
    private static readonly (string Name, string Source)[] Fonts =
    [
        ("Roboto-Medium.ttf", Path.Combine("fonts", "imgui", "misc", "fonts", "Roboto-Medium.ttf")),
        ("SourceCodePro-Regular.otf", Path.Combine("fonts", "source-code-pro", "OTF", "SourceCodePro-Regular.otf")),
    ];

    internal static int Run(string root, string[] args)
    {
        string output = Program.Option(args, "--output") ?? Path.Combine(root, "bench", "corpus");
        using JsonDocument spec = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(root, "bench", "corpus.json")));
        string url = spec.RootElement.GetProperty("url").GetString()!;
        string sha = spec.RootElement.GetProperty("sha256").GetString()!;

        string manifestPath = Path.Combine(output, "manifest.json");
        if (!File.Exists(manifestPath) || !Verify(output, manifestPath, quiet: true))
        {
            Console.WriteLine($"downloading {url}");
            byte[] zip = Program.Download(url);
            string actual = Program.Sha256(zip);
            if (actual != sha)
            {
                throw new InvalidDataException($"benchmark corpus: SHA-256 {actual}, expected {sha}");
            }

            Directory.CreateDirectory(output);
            using var archive = new ZipArchive(new MemoryStream(zip), ZipArchiveMode.Read);
            archive.ExtractToDirectory(output, overwriteFiles: true);

            string corpus = Path.Combine(root, "artifacts", "corpus");
            if (Fonts.Any(f => !File.Exists(Path.Combine(corpus, f.Source))))
            {
                FetchCorpus.Run(root, []);
            }

            foreach ((string name, string source) in Fonts)
            {
                File.Copy(Path.Combine(corpus, source), Path.Combine(output, name), overwrite: true);
            }
        }

        if (!Verify(output, manifestPath, quiet: false))
        {
            return 1;
        }

        Console.WriteLine($"Benchmark corpus ready at {output}");
        return 0;
    }

    private static bool Verify(string output, string manifestPath, bool quiet)
    {
        using JsonDocument manifest = JsonDocument.Parse(File.ReadAllBytes(manifestPath));
        bool ok = true;
        foreach (JsonProperty file in manifest.RootElement.GetProperty("files").EnumerateObject())
        {
            string path = Path.Combine(output, file.Name);
            if (!File.Exists(path) || Program.Sha256File(path) != file.Value.GetString())
            {
                ok = false;
                if (!quiet)
                {
                    Console.Error.WriteLine($"{file.Name}: missing or SHA-256 mismatch");
                }
            }
        }

        return ok;
    }
}
