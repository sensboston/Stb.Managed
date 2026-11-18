using Stb.Managed.Internal;
using Xunit;

namespace Stb.Managed.Tests.Core;

public sealed class SpanReaderTests
{
    [Fact]
    public void ReadsBigAndLittleEndianIntegers()
    {
        ReadOnlySpan<byte> bytes =
        [
            0xAB,
            0x12, 0x34,
            0x34, 0x12,
            0xFE, 0xFF,
            0x01, 0x02, 0x03, 0x04,
            0x04, 0x03, 0x02, 0x01,
            0xFF, 0xFF, 0xFF, 0xFE,
            0xFD, 0xFF, 0xFF, 0xFF,
        ];

        SpanReader reader = new(bytes);
        Assert.Equal(bytes.Length, reader.Length);
        Assert.Equal(0xAB, reader.ReadByte());
        Assert.Equal((ushort)0x1234, reader.ReadUInt16BigEndian());
        Assert.Equal((ushort)0x1234, reader.ReadUInt16LittleEndian());
        Assert.Equal((short)-2, reader.ReadInt16LittleEndian());
        Assert.Equal(0x01020304u, reader.ReadUInt32BigEndian());
        Assert.Equal(0x01020304u, reader.ReadUInt32LittleEndian());
        Assert.Equal(-2, reader.ReadInt32BigEndian());
        Assert.Equal(-3, reader.ReadInt32LittleEndian());
        Assert.True(reader.IsEnd);
        Assert.Equal(0, reader.Remaining);
    }

    [Fact]
    public void TruncatedReadThrowsTypedStbExceptionWithOffset()
    {
        byte[] bytes = [1, 2, 3];
        StbException ex = Assert.Throws<StbException>(() =>
        {
            SpanReader reader = new(bytes);
            reader.Skip(1);
            _ = reader.ReadUInt32BigEndian();
        });

        Assert.Equal(StbErrorCode.TruncatedInput, ex.Code);
        Assert.Equal(1, ex.Offset);
    }

    [Fact]
    public void PeekAndSeekWorkWithinBounds()
    {
        byte[] bytes = [10, 20, 30, 40, 50];
        SpanReader reader = new(bytes);

        Assert.True(reader.TryPeekBytes(3, out ReadOnlySpan<byte> peek));
        Assert.True(peek.SequenceEqual((ReadOnlySpan<byte>)[10, 20, 30]));
        Assert.Equal(0, reader.Position);

        ReadOnlySpan<byte> taken = reader.ReadBytes(2);
        Assert.True(taken.SequenceEqual((ReadOnlySpan<byte>)[10, 20]));
        Assert.Equal(2, reader.Position);

        reader.Seek(4);
        Assert.Equal(50, reader.ReadByte());
        Assert.False(reader.TryPeekBytes(1, out _));
    }
}
