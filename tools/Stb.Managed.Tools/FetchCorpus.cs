using System.Formats.Tar;
using System.IO.Compression;
using System.Text.Json;

namespace Stb.Managed.Tools;

/// <summary>Downloads the pinned source archives listed in tools/corpus_manifest.json and keeps the listed files, each checked against its SHA-256.</summary>
internal static class FetchCorpus
{
    internal static int Run(string root, string[] args)
    {
        string output = Program.Option(args, "--output") ?? Path.Combine(root, "artifacts", "corpus");
        using JsonDocument manifest = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(root, "tools", "corpus_manifest.json")));
        int total = 0;
        foreach (JsonElement archive in manifest.RootElement.GetProperty("archives").EnumerateArray())
        {
            total += Fetch(archive, output);
        }

        Console.WriteLine($"Corpus ready ({total} files verified) at {output}");
        return 0;
    }

    private static int Fetch(JsonElement archive, string output)
    {
        string name = archive.GetProperty("name").GetString()!;
        string url = archive.GetProperty("url").GetString()!;
        string prefix = archive.GetProperty("strip_prefix").GetString()!;
        string target = Path.Combine(output, archive.GetProperty("target_dir").GetString()!);
        var files = archive.GetProperty("files").EnumerateObject().ToDictionary(p => p.Name, p => p.Value.GetString()!, StringComparer.Ordinal);

        if (files.All(f => File.Exists(Path.Combine(target, f.Key)) && Program.Sha256File(Path.Combine(target, f.Key)) == f.Value))
        {
            Console.WriteLine($"[{name}] {files.Count} files already present.");
            return files.Count;
        }

        Console.WriteLine($"[{name}] downloading {url}");
        using var gzip = new GZipStream(new MemoryStream(Program.Download(url)), CompressionMode.Decompress);
        using var tar = new TarReader(gzip);
        var found = new HashSet<string>(StringComparer.Ordinal);
        while (tar.GetNextEntry() is { } entry)
        {
            if (entry.EntryType is not (TarEntryType.RegularFile or TarEntryType.V7RegularFile) || !entry.Name.StartsWith(prefix, StringComparison.Ordinal))
            {
                continue;
            }

            string rel = entry.Name[prefix.Length..];
            if (!files.TryGetValue(rel, out string? expected))
            {
                continue;
            }

            using var data = new MemoryStream();
            entry.DataStream?.CopyTo(data); // null for empty files
            string actual = Program.Sha256(data.GetBuffer().AsSpan(0, (int)data.Length));
            if (actual != expected)
            {
                throw new InvalidDataException($"[{name}] {rel}: SHA-256 {actual}, expected {expected}");
            }

            string dest = Path.Combine(target, rel);
            Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
            File.WriteAllBytes(dest, data.ToArray());
            found.Add(rel);
        }

        var missing = files.Keys.Where(k => !found.Contains(k)).ToList();
        if (missing.Count > 0)
        {
            throw new InvalidDataException($"[{name}] missing from the archive: {string.Join(", ", missing)}");
        }

        Console.WriteLine($"[{name}] extracted and verified {found.Count} files.");
        return found.Count;
    }
}
