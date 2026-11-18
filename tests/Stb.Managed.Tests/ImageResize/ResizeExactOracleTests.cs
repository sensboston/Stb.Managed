using System.Collections.Generic;
using Stb.Managed.Oracle;
using Xunit;

namespace Stb.Managed.Tests.ImageResize;

/// <summary>
/// Byte-exact comparison with stb_image_resize2 for every pixel layout and the 8-bit, 16-bit and FP32
/// data types. The horizontal summation order (one to three channels: four lanes by slot index mod 4
/// over the kernel's whole read extent, then (l0 + l2) + (l1 + l3); four and eight channels: two
/// chains over even and odd slots, E + O) and the sequential vertical sums are part of the contract,
/// so these tests use no tolerance.
/// </summary>
public sealed class ResizeExactOracleTests
{
    public static IEnumerable<object[]> AllLayouts()
    {
        for (int i = 0; i <= (int)ResizePixelLayout.ArPremultiplied; i++)
        {
            yield return [(ResizePixelLayout)i];
        }
    }

    [Theory]
    [MemberData(nameof(AllLayouts))]
    public void ExactBytesMatchOracleForEveryLayout(ResizePixelLayout layout)
    {
        ResizeDataType[] dataTypes =
        [
            ResizeDataType.UInt8Linear,
            ResizeDataType.UInt8Srgb,
            ResizeDataType.UInt16Linear,
        ];

        (int InW, int InH, int OutW, int OutH)[] sizes =
        [
            (23, 19, 47, 37), // upsample both axes
            (47, 37, 21, 17), // downsample both axes
            (17, 65, 53, 19), // upsample H, downsample V
            (65, 17, 19, 53), // downsample H, upsample V
            (19, 17, 19, 17), // 1:1
        ];

        ResizeFilter[] filters = [ResizeFilter.Default];

        int channels = ResizeTestHelpers.GetChannelCount(layout);
        foreach (ResizeDataType dt in dataTypes)
        {
            int typeSize = ResizeTestHelpers.GetTypeSize(dt);
            foreach (ResizeFilter filter in filters)
            {
                foreach ((int inW, int inH, int outW, int outH) in sizes)
                {
                    byte[] src = ResizeTestHelpers.CreateImageBytes(inW, inH, layout, dt, seed: 4242 + ((int)layout * 17) + (int)dt, includeZeroAlpha: true);
                    byte[] expected = new byte[outW * outH * channels * typeSize];
                    byte[] actual = new byte[expected.Length];

                    Assert.True(StbOracle.ResizeBytes(src, inW, inH, 0, expected, outW, outH, 0, (int)layout, (int)dt, (int)ResizeEdge.Clamp, (int)filter));
                    StbImageResize.Resize(src, inW, inH, actual, outW, outH, layout, dt, ResizeEdge.Clamp, filter);

                    ResizeTestHelpers.AssertMatchesOracle(actual, expected, dt, exact: true, context: $"{layout}/{dt}/{filter} {inW}x{inH}->{outW}x{outH}");
                }
            }
        }
    }
}
