using Stb.Managed.Oracle;
using Stb.Managed.RectPack;
using Xunit;

namespace Stb.Managed.Tests.RectPack;

public sealed class RectPackTests
{
    [Theory]
    [InlineData(256, 256, 256, RectPackHeuristic.SkylineBottomLeft, false, 100, 1001)]
    [InlineData(256, 256, 256, RectPackHeuristic.SkylineBestFit, false, 100, 1002)]
    [InlineData(512, 512, 512, RectPackHeuristic.SkylineBottomLeft, false, 400, 1003)]
    [InlineData(512, 512, 512, RectPackHeuristic.SkylineBestFit, false, 400, 1004)]
    [InlineData(1024, 1024, 1024, RectPackHeuristic.SkylineBottomLeft, false, 1200, 1005)]
    [InlineData(1024, 1024, 1024, RectPackHeuristic.SkylineBestFit, false, 1200, 1006)]
    [InlineData(300, 180, 300, RectPackHeuristic.SkylineBottomLeft, false, 200, 1007)]
    [InlineData(300, 180, 300, RectPackHeuristic.SkylineBestFit, false, 200, 1008)]
    [InlineData(256, 256, 64, RectPackHeuristic.SkylineBottomLeft, false, 150, 1009)]
    [InlineData(256, 256, 64, RectPackHeuristic.SkylineBestFit, false, 150, 1010)]
    [InlineData(256, 256, 16, RectPackHeuristic.SkylineBottomLeft, true, 150, 1011)]
    [InlineData(256, 256, 16, RectPackHeuristic.SkylineBestFit, true, 150, 1012)]
    [InlineData(64, 64, 64, RectPackHeuristic.SkylineBottomLeft, false, 80, 1013)] // Partial overflow
    [InlineData(64, 64, 64, RectPackHeuristic.SkylineBestFit, false, 80, 1014)] // Partial overflow
    public void PackRectsMatchesNativeOracleBitForBit(
        int width,
        int height,
        int nodeCount,
        RectPackHeuristic heuristic,
        bool allowOutOfMem,
        int rectCount,
        int seed)
    {
        var rng = new Random(seed);
        PackRect[] managedRects = new PackRect[rectCount];
        OracleRect[] nativeRects = new OracleRect[rectCount];

        for (int i = 0; i < rectCount; ++i)
        {
            int rw = rng.Next(0, 48);
            int rh = rng.Next(0, 48);
            if (i % 29 == 0)
            {
                rw = 0; // test zero-width
            }
            else if (i % 31 == 0)
            {
                rh = 0; // test zero-height
            }

            managedRects[i] = new PackRect(rw, rh, id: 10000 + i);
            nativeRects[i] = new OracleRect(10000 + i, rw, rh, 0, 0, 0);
        }

        bool nativeAllPacked = StbOracle.PackRects(
            width,
            height,
            nativeRects,
            numNodes: nodeCount,
            heuristic: (int)heuristic,
            allowOutOfMem: allowOutOfMem);

        bool managedAllPacked = StbRectPack.PackRects(
            width,
            height,
            managedRects,
            heuristic,
            nodeCount,
            allowOutOfMem);

        Assert.Equal(nativeAllPacked, managedAllPacked);

        for (int i = 0; i < rectCount; ++i)
        {
            bool nativePacked = nativeRects[i].WasPacked != 0;
            Assert.Equal(10000 + i, managedRects[i].Id);
            Assert.Equal(nativePacked, managedRects[i].WasPacked);
            if (nativePacked)
            {
                Assert.Equal(nativeRects[i].X, managedRects[i].X);
                Assert.Equal(nativeRects[i].Y, managedRects[i].Y);
            }
        }

        AssertNoOverlapsAndInBounds(managedRects, width, height);
    }

    [Fact]
    public void PackRectsEdgeCasesMatchOracle()
    {
        // Identical sizes, oversized rects, zero-sized rects, exact-fit bin.
        PackRect[] managed =
        [
            new PackRect(0, 0, id: 1),
            new PackRect(0, 10, id: 2),
            new PackRect(10, 0, id: 3),
            new PackRect(129, 10, id: 4), // wider than bin
            new PackRect(10, 129, id: 5), // taller than bin
            new PackRect(64, 64, id: 6),
            new PackRect(64, 64, id: 7),
            new PackRect(64, 64, id: 8),
            new PackRect(64, 64, id: 9),
            new PackRect(1, 1, id: 10), // won't fit after 4x 64x64 fill 128x128
        ];

        OracleRect[] native = new OracleRect[managed.Length];
        for (int i = 0; i < managed.Length; ++i)
        {
            native[i] = new OracleRect(managed[i].Id, managed[i].Width, managed[i].Height, 0, 0, 0);
        }

        bool nativeOk = StbOracle.PackRects(128, 128, native, numNodes: 128, heuristic: 1, allowOutOfMem: false);
        bool managedOk = StbRectPack.PackRects(128, 128, managed, RectPackHeuristic.SkylineBestFit);

        Assert.Equal(nativeOk, managedOk);
        Assert.False(managedOk);
        for (int i = 0; i < managed.Length; ++i)
        {
            Assert.Equal(native[i].WasPacked != 0, managed[i].WasPacked);
            if (managed[i].WasPacked)
            {
                Assert.Equal(native[i].X, managed[i].X);
                Assert.Equal(native[i].Y, managed[i].Y);
            }
        }

        AssertNoOverlapsAndInBounds(managed, 128, 128);
    }

    [Fact]
    public void PackerIncrementalAndResetWorksDeterministically()
    {
        using StbRectPacker packer = StbRectPacker.Create(256, 256);

        PackRect[] batch1 = [new PackRect(128, 128, 1), new PackRect(128, 128, 2)];
        PackRect[] batch2 = [new PackRect(128, 128, 3), new PackRect(128, 128, 4)];
        PackRect[] batch3 = [new PackRect(1, 1, 5)];

        Assert.True(packer.PackRects(batch1));
        Assert.True(packer.PackRects(batch2));
        Assert.False(packer.PackRects(batch3));
        Assert.False(batch3[0].WasPacked);

        PackRect[] combined = [.. batch1, .. batch2];
        AssertNoOverlapsAndInBounds(combined, 256, 256);

        // Reset should allow packing all 4 quadrants again identically.
        packer.Reset();
        packer.SetHeuristic(RectPackHeuristic.SkylineBestFit);
        packer.SetAllowOutOfMem(true);
        Assert.Equal(RectPackHeuristic.SkylineBestFit, packer.Heuristic);
        Assert.True(packer.AllowOutOfMem);

        PackRect[] afterReset = [new PackRect(128, 128, 10), new PackRect(128, 128, 11), new PackRect(128, 128, 12), new PackRect(128, 128, 13)];
        Assert.True(packer.PackRects(afterReset));
        AssertNoOverlapsAndInBounds(afterReset, 256, 256);
    }

    [Fact]
    public void PackerEmptySpanReturnsTrue()
    {
        using StbRectPacker packer = StbRectPacker.Create(64, 64);
        Assert.True(packer.PackRects(Span<PackRect>.Empty));
    }

    [Fact]
    public void PackerInvalidArgumentsAndLimitsThrowExpectedExceptions()
    {
        Assert.Throws<StbException>(() => StbRectPacker.Create(0, 64));
        Assert.Throws<StbException>(() => StbRectPacker.Create(64, -1));
        Assert.Throws<ArgumentOutOfRangeException>(() => StbRectPacker.Create(64, 64, nodeCount: -5));
        Assert.Throws<ArgumentOutOfRangeException>(() => StbRectPacker.Create(64, 64, heuristic: (RectPackHeuristic)99));

        var tinyLimits = new StbOptions { Limits = new StbLimits { MaxAllocationBytes = 100 } };
        StbException limitEx = Assert.Throws<StbException>(() => StbRectPacker.Create(1024, 1024, options: tinyLimits));
        Assert.Equal(StbErrorCode.LimitExceeded, limitEx.Code);

        using StbRectPacker packer = StbRectPacker.Create(64, 64);
        Assert.Throws<ArgumentOutOfRangeException>(() => packer.SetHeuristic((RectPackHeuristic)42));
        Assert.Throws<ArgumentOutOfRangeException>(() => packer.PackRects([new PackRect(-1, 10)]));
        Assert.Throws<ArgumentOutOfRangeException>(() => packer.PackRects([new PackRect(10, -2)]));

        packer.Dispose();
        Assert.Throws<ObjectDisposedException>(() => packer.PackRects([new PackRect(4, 4)]));
        Assert.Throws<ObjectDisposedException>(() => packer.Reset());
        Assert.Throws<ObjectDisposedException>(() => packer.SetHeuristic(RectPackHeuristic.SkylineBestFit));
        Assert.Throws<ObjectDisposedException>(() => packer.SetAllowOutOfMem(true));
    }

    private static void AssertNoOverlapsAndInBounds(ReadOnlySpan<PackRect> rects, int width, int height)
    {
        for (int i = 0; i < rects.Length; ++i)
        {
            PackRect a = rects[i];
            if (!a.WasPacked || a.Width == 0 || a.Height == 0)
            {
                continue;
            }

            Assert.InRange(a.X, 0, width - a.Width);
            Assert.InRange(a.Y, 0, height - a.Height);

            for (int j = i + 1; j < rects.Length; ++j)
            {
                PackRect b = rects[j];
                if (!b.WasPacked || b.Width == 0 || b.Height == 0)
                {
                    continue;
                }

                bool separated = a.X + a.Width <= b.X
                    || b.X + b.Width <= a.X
                    || a.Y + a.Height <= b.Y
                    || b.Y + b.Height <= a.Y;
                Assert.True(separated, $"Rects {a.Id} ({a.X},{a.Y},{a.Width}x{a.Height}) and {b.Id} ({b.X},{b.Y},{b.Width}x{b.Height}) overlap.");
            }
        }
    }
}
