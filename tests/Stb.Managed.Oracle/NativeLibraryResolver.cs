using System.Reflection;
using System.Runtime.InteropServices;

namespace Stb.Managed.Oracle;

internal static class NativeLibraryResolver
{
    private const string LibraryBaseName = "stb_oracle";
    private static int _initialized;

    internal static void EnsureRegistered()
    {
        if (Interlocked.Exchange(ref _initialized, 1) != 0)
        {
            return;
        }

        NativeLibrary.SetDllImportResolver(typeof(NativeLibraryResolver).Assembly, Resolve);
    }

    private static nint Resolve(string libraryName, Assembly assembly, DllImportSearchPath? searchPath)
    {
        if (!string.Equals(libraryName, LibraryBaseName, StringComparison.Ordinal))
        {
            return nint.Zero;
        }

        string fileName = GetPlatformLibraryFileName();

        // 1. Explicit STB_ORACLE_PATH environment variable (file path or directory).
        string? envPath = Environment.GetEnvironmentVariable("STB_ORACLE_PATH");
        if (!string.IsNullOrWhiteSpace(envPath))
        {
            string candidate = File.Exists(envPath) ? envPath : Path.Combine(envPath, fileName);
            if (File.Exists(candidate) && NativeLibrary.TryLoad(candidate, out nint envHandle))
            {
                return envHandle;
            }
        }

        // 2. Alongside the executing test/benchmark assembly.
        string baseDirCandidate = Path.Combine(AppContext.BaseDirectory, fileName);
        if (File.Exists(baseDirCandidate) && NativeLibrary.TryLoad(baseDirCandidate, out nint baseHandle))
        {
            return baseHandle;
        }

        // 3. Walk up from AppContext.BaseDirectory looking for native/build/out/<lib>.
        DirectoryInfo? dir = new(AppContext.BaseDirectory);
        while (dir is not null)
        {
            string buildOutCandidate = Path.Combine(dir.FullName, "native", "build", "out", fileName);
            if (File.Exists(buildOutCandidate) && NativeLibrary.TryLoad(buildOutCandidate, out nint repoHandle))
            {
                return repoHandle;
            }

            string buildCandidate = Path.Combine(dir.FullName, "native", "build", fileName);
            if (File.Exists(buildCandidate) && NativeLibrary.TryLoad(buildCandidate, out nint legacyHandle))
            {
                return legacyHandle;
            }

            dir = dir.Parent;
        }

        // 4. Fall back to standard OS loader search paths.
        return NativeLibrary.TryLoad(libraryName, assembly, searchPath, out nint defaultHandle)
            ? defaultHandle
            : nint.Zero;
    }

    private static string GetPlatformLibraryFileName()
    {
        if (OperatingSystem.IsWindows())
        {
            return "stb_oracle.dll";
        }

        if (OperatingSystem.IsMacOS())
        {
            return "libstb_oracle.dylib";
        }

        return "libstb_oracle.so";
    }
}
