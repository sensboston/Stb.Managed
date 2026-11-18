using System.Buffers.Binary;
using System.Runtime.CompilerServices;

namespace Stb.Managed.Internal;

/// <summary>
/// Bounds-checked forward reader over a <see cref="ReadOnlySpan{T}"/> of bytes.
/// Throws <see cref="StbException"/> with <see cref="StbErrorCode.TruncatedInput"/> on premature EOF.
/// </summary>
internal ref struct SpanReader
{
    private readonly ReadOnlySpan<byte> _data;
    private int _position;

    public SpanReader(ReadOnlySpan<byte> data)
    {
        _data = data;
        _position = 0;
    }

    public readonly int Length => _data.Length;

    public readonly int Position => _position;

    public readonly int Remaining => _data.Length - _position;

    public readonly bool IsEnd => _position >= _data.Length;

    public readonly ReadOnlySpan<byte> RemainingSpan => _data.Slice(_position);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public byte ReadByte()
    {
        if (_position >= _data.Length)
        {
            ThrowTruncated(1);
        }

        return _data[_position++];
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public ushort ReadUInt16BigEndian()
    {
        if (Remaining < 2)
        {
            ThrowTruncated(2);
        }

        ushort value = BinaryPrimitives.ReadUInt16BigEndian(_data.Slice(_position));
        _position += 2;
        return value;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public ushort ReadUInt16LittleEndian()
    {
        if (Remaining < 2)
        {
            ThrowTruncated(2);
        }

        ushort value = BinaryPrimitives.ReadUInt16LittleEndian(_data.Slice(_position));
        _position += 2;
        return value;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public short ReadInt16LittleEndian()
    {
        if (Remaining < 2)
        {
            ThrowTruncated(2);
        }

        short value = BinaryPrimitives.ReadInt16LittleEndian(_data.Slice(_position));
        _position += 2;
        return value;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public uint ReadUInt32BigEndian()
    {
        if (Remaining < 4)
        {
            ThrowTruncated(4);
        }

        uint value = BinaryPrimitives.ReadUInt32BigEndian(_data.Slice(_position));
        _position += 4;
        return value;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public uint ReadUInt32LittleEndian()
    {
        if (Remaining < 4)
        {
            ThrowTruncated(4);
        }

        uint value = BinaryPrimitives.ReadUInt32LittleEndian(_data.Slice(_position));
        _position += 4;
        return value;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public int ReadInt32BigEndian()
    {
        if (Remaining < 4)
        {
            ThrowTruncated(4);
        }

        int value = BinaryPrimitives.ReadInt32BigEndian(_data.Slice(_position));
        _position += 4;
        return value;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public int ReadInt32LittleEndian()
    {
        if (Remaining < 4)
        {
            ThrowTruncated(4);
        }

        int value = BinaryPrimitives.ReadInt32LittleEndian(_data.Slice(_position));
        _position += 4;
        return value;
    }

    public ReadOnlySpan<byte> ReadBytes(int count)
    {
        if (count < 0 || count > Remaining)
        {
            ThrowTruncated(count);
        }

        ReadOnlySpan<byte> slice = _data.Slice(_position, count);
        _position += count;
        return slice;
    }

    public readonly bool TryPeekBytes(int count, out ReadOnlySpan<byte> slice)
    {
        if (count < 0 || count > Remaining)
        {
            slice = default;
            return false;
        }

        slice = _data.Slice(_position, count);
        return true;
    }

    public void Skip(int count)
    {
        if (count < 0 || count > Remaining)
        {
            ThrowTruncated(count);
        }

        _position += count;
    }

    public void Seek(int position)
    {
        if ((uint)position > (uint)_data.Length)
        {
            throw new StbException(
                StbErrorCode.TruncatedInput,
                $"Seek target {position} is outside input length {_data.Length}.",
                position);
        }

        _position = position;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private readonly void ThrowTruncated(int requested) =>
        throw new StbException(
            StbErrorCode.TruncatedInput,
            $"Unexpected end of input at offset {_position}: needed {requested} byte(s), only {Remaining} remaining.",
            _position);
}
