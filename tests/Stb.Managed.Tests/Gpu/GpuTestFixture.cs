using Stb.Managed.Gpu;
using Xunit;

namespace Stb.Managed.Tests.Gpu;

/// <summary>
/// Shared xUnit collection fixture owning a warmed-up <see cref="StbGpuContext"/> so kernel JIT/PTX
/// compilation runs once across all GPU parity tests.
/// </summary>
public sealed class GpuTestFixture : IDisposable
{
    public GpuTestFixture()
    {
        // Auto probes CUDA -> OpenCL -> ILGPU CPU accelerator, so tests run on real GPU when present
        // and deterministically via ILGPU's CPU accelerator in headless CI environments.
        Context = StbGpuContext.Create(new StbGpuOptions
        {
            BackendPreference = StbGpuBackendPreference.Auto,
            ExecutionMode = StbGpuExecutionMode.ForceGpu,
        });
        Context.Warmup();
    }

    public StbGpuContext Context { get; }

    public void Dispose() => Context.Dispose();
}

[CollectionDefinition(Name)]
public sealed class GpuTestSuite : ICollectionFixture<GpuTestFixture>
{
    public const string Name = "Gpu";
}
