using System.Net.Http.Headers;
using System.Security.Cryptography;

namespace Stb.Managed.Tools;

/// <summary>Repository tooling: test and benchmark corpora, package checks, benchmark report.</summary>
internal static class Program
{
    private static int Main(string[] args)
    {
        if (args.Length == 0)
        {
            return Usage();
        }

        string root = RepoRoot();
        try
        {
            return args[0] switch
            {
                "fetch-corpus" => FetchCorpus.Run(root, args[1..]),
                "fetch-bench-corpus" => BenchCorpus.Run(root, args[1..]),
                "verify-packages" => VerifyPackages.Run(args[1..]),
                "report" => Report.Run(root, args[1..]),
                _ => Usage(),
            };
        }
        catch (Exception e) when (e is IOException or InvalidDataException or HttpRequestException or InvalidOperationException or UnauthorizedAccessException)
        {
            Console.Error.WriteLine(e.Message);
            return 1;
        }
    }

    private static int Usage()
    {
        Console.Error.WriteLine("""
            usage: Stb.Managed.Tools <command>
              fetch-corpus [--output DIR]        test images and fonts (tools/corpus_manifest.json) -> artifacts/corpus
              fetch-bench-corpus [--output DIR]  benchmark inputs (bench/corpus.json) -> bench/corpus
              verify-packages DIR                checks the .nupkg/.snupkg files produced by dotnet pack
              report RESULTS... [--repo DIR]     docs/benchmarks.md and charts from bench/results/<host> folders or zips
            """);
        return 2;
    }

    private static string RepoRoot()
    {
        for (string? dir = Directory.GetCurrentDirectory(); dir is not null; dir = Path.GetDirectoryName(dir))
        {
            if (File.Exists(Path.Combine(dir, "Stb.Managed.sln")))
            {
                return dir;
            }
        }

        return Directory.GetCurrentDirectory();
    }

    internal static string? Option(string[] args, string name)
    {
        int i = Array.IndexOf(args, name);
        return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
    }

    internal static byte[] Download(string url)
    {
        using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
        http.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("Stb.Managed-tools", "1.0"));
        http.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("(+https://github.com/sensboston/Stb.Managed)"));
        using HttpResponseMessage response = http.Send(new HttpRequestMessage(HttpMethod.Get, url));
        response.EnsureSuccessStatusCode();
        using Stream body = response.Content.ReadAsStream();
        using var buffer = new MemoryStream();
        body.CopyTo(buffer);
        return buffer.ToArray();
    }

    internal static string Sha256(ReadOnlySpan<byte> data) => Convert.ToHexStringLower(SHA256.HashData(data));

    internal static string Sha256File(string path) => Sha256(File.ReadAllBytes(path));
}
