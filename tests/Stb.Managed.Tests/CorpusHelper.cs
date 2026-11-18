namespace Stb.Managed.Tests;

internal static class CorpusHelper
{
    internal static string? TryGetCorpusRoot()
    {
        string? envDir = Environment.GetEnvironmentVariable("STB_CORPUS_DIR");
        if (!string.IsNullOrWhiteSpace(envDir) && Directory.Exists(envDir))
        {
            return envDir;
        }

        DirectoryInfo? dir = new(AppContext.BaseDirectory);
        while (dir is not null)
        {
            string candidate = Path.Combine(dir.FullName, "artifacts", "corpus");
            if (Directory.Exists(candidate))
            {
                return candidate;
            }

            dir = dir.Parent;
        }

        return null;
    }

    internal static bool IsRunningInCi =>
        string.Equals(Environment.GetEnvironmentVariable("CI"), "true", StringComparison.OrdinalIgnoreCase);

    internal static readonly string[] TrueTypeCorpusFontPaths =
    [
        "fonts/imgui/misc/fonts/Roboto-Medium.ttf",
        "fonts/imgui/misc/fonts/DroidSans.ttf",
        "fonts/imgui/misc/fonts/Karla-Regular.ttf",
        "fonts/imgui/misc/fonts/Cousine-Regular.ttf",
        "fonts/imgui/misc/fonts/ProggyClean.ttf",
        "fonts/source-code-pro/OTF/SourceCodePro-Regular.otf",
        "fonts/source-code-pro/OTF/SourceCodePro-It.otf",
    ];

    internal static string? TryGetFontPath(string relativeFontPath)
    {
        string? root = TryGetCorpusRoot();
        if (root is null)
        {
            return null;
        }

        string full = Path.Combine(root, relativeFontPath.Replace('/', Path.DirectorySeparatorChar));
        return File.Exists(full) ? full : null;
    }
}
