using System.IO.Compression;
using System.Xml.Linq;

namespace Stb.Managed.Tools;

/// <summary>Checks the packages from dotnet pack: assemblies and XML docs per target, README, MIT license, symbols, exact dependencies.</summary>
internal static class VerifyPackages
{
    private static readonly string[] Libraries =
    [
        "Stb.Managed.Core", "Stb.Managed.Image", "Stb.Managed.ImageWrite", "Stb.Managed.ImageResize",
        "Stb.Managed.TrueType", "Stb.Managed.RectPack", "Stb.Managed.Dxt", "Stb.Managed.Perlin",
    ];

    private static readonly string[] LibraryTargets = ["net8.0", "net10.0", "netstandard2.1"];
    private static readonly string[] GpuTargets = ["net8.0", "net10.0"];

    private static readonly string[] GpuDependencies =
    [
        "ILGPU", "ILGPU.Algorithms", "Stb.Managed.Core", "Stb.Managed.Dxt", "Stb.Managed.ImageResize",
        "Stb.Managed.Perlin", "Stb.Managed.RectPack", "Stb.Managed.TrueType",
    ];

    internal static int Run(string[] args)
    {
        if (args.Length != 1 || !Directory.Exists(args[0]))
        {
            Console.Error.WriteLine("usage: verify-packages <packages directory>");
            return 2;
        }

        string dir = args[0];
        foreach (string lib in Libraries)
        {
            CheckLibrary(dir, lib, LibraryTargets, lib == "Stb.Managed.Core" ? [] : ["Stb.Managed.Core"]);
        }

        CheckLibrary(dir, "Stb.Managed.Gpu", GpuTargets, GpuDependencies);

        using (ZipArchive meta = Open(dir, "Stb.Managed", "nupkg"))
        {
            Require(meta.GetEntry("README.md") is not null, "Stb.Managed: README.md missing");
            var groups = Dependencies(meta, "Stb.Managed");
            Require(groups.Count > 0, "Stb.Managed: no dependency groups");
            foreach ((string tfm, HashSet<string> deps) in groups)
            {
                var expected = new HashSet<string>(Libraries);
                if (!tfm.Contains("standard", StringComparison.OrdinalIgnoreCase))
                {
                    expected.Add("Stb.Managed.Gpu");
                }

                Require(deps.SetEquals(expected), $"Stb.Managed ({tfm}): dependencies {string.Join(", ", deps.Order())}");
            }
        }

        Console.WriteLine("OK Stb.Managed (metapackage)");
        Console.WriteLine("All 10 packages verified.");
        return 0;
    }

    private static void CheckLibrary(string dir, string id, string[] targets, string[] dependencies)
    {
        using (ZipArchive package = Open(dir, id, "nupkg"))
        {
            Require(package.GetEntry("README.md") is not null, $"{id}: README.md missing");
            foreach (string tfm in targets)
            {
                Require(package.GetEntry($"lib/{tfm}/{id}.dll") is not null, $"{id}: lib/{tfm}/{id}.dll missing");
                Require(package.GetEntry($"lib/{tfm}/{id}.xml") is not null, $"{id}: lib/{tfm}/{id}.xml missing");
            }

            foreach ((string tfm, HashSet<string> deps) in Dependencies(package, id))
            {
                Require(deps.SetEquals(dependencies), $"{id} ({tfm}): dependencies {string.Join(", ", deps.Order())}");
            }
        }

        using (ZipArchive symbols = Open(dir, id, "snupkg"))
        {
            int pdbs = symbols.Entries.Count(e => e.FullName.EndsWith(".pdb", StringComparison.Ordinal));
            Require(pdbs >= targets.Length, $"{id}: {pdbs} PDBs in the symbol package, expected {targets.Length}");
        }

        Console.WriteLine($"OK {id} ({targets.Length} targets, XML docs, symbols, dependencies)");
    }

    private static ZipArchive Open(string dir, string id, string extension)
    {
        var matches = Directory.GetFiles(dir, $"{id}.*.{extension}")
            .Where(p => Path.GetFileName(p).Length > id.Length + 1 && char.IsAsciiDigit(Path.GetFileName(p)[id.Length + 1]))
            .ToList();
        Require(matches.Count == 1, $"expected one {id} .{extension} in {dir}, found {matches.Count}");
        return ZipFile.OpenRead(matches[0]);
    }

    private static Dictionary<string, HashSet<string>> Dependencies(ZipArchive package, string id)
    {
        ZipArchiveEntry nuspec = package.Entries.Single(e => e.FullName.EndsWith(".nuspec", StringComparison.Ordinal));
        using Stream stream = nuspec.Open();
        XElement metadata = XDocument.Load(stream).Root!.Elements().Single(e => e.Name.LocalName == "metadata");
        string? Text(string name) => metadata.Elements().FirstOrDefault(e => e.Name.LocalName == name)?.Value;
        Require(Text("id") == id, $"{id}: nuspec id is {Text("id")}");
        Require(Text("license") == "MIT", $"{id}: license is not MIT");
        Require(Text("readme") == "README.md", $"{id}: readme is not README.md");
        return metadata.Descendants().Where(e => e.Name.LocalName == "group").ToDictionary(
            g => (string?)g.Attribute("targetFramework") ?? string.Empty,
            g => g.Elements().Where(d => d.Name.LocalName == "dependency").Select(d => (string)d.Attribute("id")!).ToHashSet(StringComparer.Ordinal));
    }

    private static void Require(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException(message);
        }
    }
}
