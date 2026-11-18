using System.Runtime.CompilerServices;

namespace Stb.Managed.Tests;

internal static class TestGpuMode
{
    // Oracle tests compare against the CPU code; GPU tests opt in explicitly.
    [ModuleInitializer]
    internal static void Initialize()
    {
        if (Environment.GetEnvironmentVariable("STB_GPU_MODE") is null)
        {
            StbGpu.Mode = StbGpuMode.CpuOnly;
        }
    }
}
