using System.Numerics;
using Stb.Managed.Internal;
using Xunit;

namespace Stb.Managed.Tests.Core;

public sealed class BitReaderTests
{
    private static ReadOnlySpan<int> LsbWidths => [0, 1, 3, 5, 7, 8, 11, 13, 16, 24, 32, 2, 4, 6, 9];
    private static ReadOnlySpan<int> MsbWidths => [0, 1, 2, 5, 7, 8, 10, 15, 16, 23, 32, 3, 6, 9];

    [Theory]
    [InlineData(1)]
    [InlineData(3)]
    [InlineData(7)]
    [InlineData(8)]
    [InlineData(9)]
    [InlineData(15)]
    [InlineData(16)]
    [InlineData(31)]
    [InlineData(64)]
    public void BitReaderLsbMatchesReferenceBitStream(int byteLength)
    {
        byte[] data = new byte[byteLength];
        for (int i = 0; i < byteLength; i++)
        {
            data[i] = (byte)((i * 73 + 29) & 0xFF);
        }

        // Verify variable-width bit reads spanning refill boundaries and tail bytes.
        ReadOnlySpan<int> widths = LsbWidths;
        BitReaderLsb reader = new(data);
        int bitOffset = 0;
        int totalBits = byteLength * 8;
        int wIdx = 0;

        while (bitOffset < totalBits)
        {
            int count = Math.Min(widths[wIdx % widths.Length], totalBits - bitOffset);
            wIdx++;

            uint expected = ReadLsbReference(data, bitOffset, count);
            uint actual = reader.ReadBits(count);
            Assert.Equal(expected, actual);
            bitOffset += count;
            Assert.Equal(totalBits - bitOffset, reader.RemainingBits);
        }

        Assert.Equal(0, reader.RemainingBits);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(3)]
    [InlineData(7)]
    [InlineData(8)]
    [InlineData(9)]
    [InlineData(15)]
    [InlineData(16)]
    [InlineData(31)]
    [InlineData(64)]
    public void BitReaderMsbMatchesReferenceBitStream(int byteLength)
    {
        byte[] data = new byte[byteLength];
        for (int i = 0; i < byteLength; i++)
        {
            data[i] = (byte)((i * 97 + 13) & 0xFF);
        }

        ReadOnlySpan<int> widths = MsbWidths;
        BitReaderMsb reader = new(data);
        int bitOffset = 0;
        int totalBits = byteLength * 8;
        int wIdx = 0;

        while (bitOffset < totalBits)
        {
            int count = Math.Min(widths[wIdx % widths.Length], totalBits - bitOffset);
            wIdx++;

            uint expected = ReadMsbReference(data, bitOffset, count);
            uint actual = reader.ReadBits(count);
            Assert.Equal(expected, actual);
            bitOffset += count;
            Assert.Equal(totalBits - bitOffset, reader.RemainingBits);
        }

        Assert.Equal(0, reader.RemainingBits);
    }

    [Fact]
    public void BitReaderLsbAlignAndReadAlignedBytesRewindsBufferedBytes()
    {
        byte[] data = new byte[20];
        for (int i = 0; i < data.Length; i++)
        {
            data[i] = (byte)(i + 10);
        }

        BitReaderLsb reader = new(data);
        // Read 3 bits from byte 0 (forces a 64-bit fast refill!).
        uint low3 = reader.ReadBits(3);
        Assert.Equal((uint)(data[0] & 0b111), low3);

        // Align skips remaining 5 bits of byte 0; ReadAlignedBytes must start cleanly at byte 1.
        ReadOnlySpan<byte> aligned = reader.ReadAlignedBytes(5);
        Assert.True(aligned.SequenceEqual(data.AsSpan(1, 5)));
        Assert.Equal(6, reader.AlignedBytePosition);

        // Continuing bit reads after ReadAlignedBytes must resume at byte 6.
        Assert.Equal((uint)data[6], reader.ReadBits(8));
    }

    [Fact]
    public void BitReadersThrowTruncatedInputOnEof()
    {
        byte[] data = [0xAA, 0x55];

        StbException lsbEx = Assert.Throws<StbException>(() =>
        {
            BitReaderLsb reader = new(data);
            _ = reader.ReadBits(12);
            _ = reader.ReadBits(8);
        });
        Assert.Equal(StbErrorCode.TruncatedInput, lsbEx.Code);

        StbException msbEx = Assert.Throws<StbException>(() =>
        {
            BitReaderMsb reader = new(data);
            _ = reader.ReadBits(12);
            _ = reader.ReadBits(8);
        });
        Assert.Equal(StbErrorCode.TruncatedInput, msbEx.Code);
    }

    [Fact]
    public void PooledBufferRentsAndClearsSafely()
    {
        using PooledBuffer<byte> empty = new(0);
        Assert.True(empty.Span.IsEmpty);

        using PooledBuffer<byte> cleared = new(128, clear: true, clearOnReturn: true);
        Assert.Equal(128, cleared.Length);
        foreach (byte b in cleared.Span)
        {
            Assert.Equal(0, b);
        }

        cleared.Span.Fill(0x7F);
    }

    [Theory]
    [InlineData(0u)]
    [InlineData(1u)]
    [InlineData(2u)]
    [InlineData(0x80000000u)]
    [InlineData(0x00F00010u)]
    [InlineData(uint.MaxValue)]
    public void BitOpsMatchBclBitOperations(uint value)
    {
        Assert.Equal(BitOperations.LeadingZeroCount(value), BitOps.LeadingZeroCount(value));
        Assert.Equal(BitOperations.TrailingZeroCount(value), BitOps.TrailingZeroCount(value));
    }

    private static uint ReadLsbReference(byte[] data, int bitOffset, int count)
    {
        uint result = 0;
        for (int i = 0; i < count; i++)
        {
            int pos = bitOffset + i;
            uint bit = (uint)((data[pos >> 3] >> (pos & 7)) & 1);
            result |= bit << i;
        }

        return result;
    }

    private static uint ReadMsbReference(byte[] data, int bitOffset, int count)
    {
        uint result = 0;
        for (int i = 0; i < count; i++)
        {
            int pos = bitOffset + i;
            uint bit = (uint)((data[pos >> 3] >> (7 - (pos & 7))) & 1);
            result = (result << 1) | bit;
        }

        return result;
    }
}
