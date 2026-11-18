using Xunit;

namespace Stb.Managed.Tests.Core;

public sealed class StbOptionsTests
{
    [Fact]
    public void DefaultOptionsAreSingleThreadedWithDefaultLimits()
    {
        Assert.Equal(StbLimits.Default, StbOptions.Default.Limits);
        Assert.Equal(1, StbOptions.Default.MaxDegreeOfParallelism);
        Assert.Equal(1, StbOptions.Default.ResolveDegreeOfParallelism(10_000_000, 1_000));
    }

    [Fact]
    public void NullLimitsThrowsArgumentNullException()
    {
        Assert.Throws<ArgumentNullException>(() => new StbOptions { Limits = null! });
    }

    [Fact]
    public void NegativeParallelismThrowsArgumentOutOfRangeException()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new StbOptions { MaxDegreeOfParallelism = -1 });
    }

    [Fact]
    public void AutoParallelismActivatesAtThreshold()
    {
        StbOptions options = new() { MaxDegreeOfParallelism = 0 };
        Assert.Equal(1, options.ResolveDegreeOfParallelism(999, 1000));
        Assert.Equal(Math.Max(1, Environment.ProcessorCount), options.ResolveDegreeOfParallelism(1000, 1000));
    }

    [Fact]
    public void ExplicitParallelismIsBoundedByProcessorCount()
    {
        StbOptions options = new() { MaxDegreeOfParallelism = 1024 };
        Assert.Equal(Math.Max(1, Environment.ProcessorCount), options.ResolveDegreeOfParallelism(1, 1000));
    }
}
