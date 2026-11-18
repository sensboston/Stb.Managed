using System.Buffers.Binary;
using System.Runtime.CompilerServices;

namespace Stb.Managed.Internal;

/// <summary>
/// Safe MSB-first 64-bit buffered bit reader (used by JPEG entropy coding and sub-byte unpacking).
/// Invariant: <c>0 &lt;= BitCount &lt;= 63</c>, buffered bits sit at the top of the 64-bit register,
/// and all bits below <c>64 - BitCount</c> are 0.
/// </summary>
internal ref struct BitReaderMsb
{
    private readonly ReadOnlySpan<byte> _data;
    private int _bytePos;
    private ulong _bitBuffer;
    private int _bitCount;

    public BitReaderMsb(ReadOnlySpan<byte> data)
    {
        _data = data;
        _bytePos = 0;
        _bitBuffer = 0;
        _bitCount = 0;
    }

    public readonly int BitCount => _bitCount;

    /// <summary>
    /// Gets the total unread bits remaining across both the internal bit buffer and backing span.
    /// </summary>
    public readonly long RemainingBits => ((long)(_data.Length - _bytePos) << 3) + _bitCount;

    /// <summary>
    /// Refills the bit buffer to at least 56 bits whenever enough bytes remain in the input.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Refill()
    {
        if (_bitCount <= 55)
        {
            if (_bytePos <= _data.Length - 8)
            {
                ulong next = BinaryPrimitives.ReadUInt64BigEndian(_data.Slice(_bytePos));
                int consumed = (63 - _bitCount) >> 3;
                int addedBits = consumed << 3;
                ulong mask = ulong.MaxValue << (64 - addedBits);
                _bitBuffer |= (next & mask) >> _bitCount;
                _bytePos += consumed;
                _bitCount += addedBits;
                return;
            }

            RefillSlow();
        }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private void RefillSlow()
    {
        while (_bitCount <= 55 && _bytePos < _data.Length)
        {
            _bitBuffer |= (ulong)_data[_bytePos++] << (56 - _bitCount);
            _bitCount += 8;
        }
    }

    /// <summary>
    /// Ensures at least <paramref name="count"/> bits are buffered (up to 32), returning <see langword="false"/> on EOF.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool TryEnsureBits(int count)
    {
        if (_bitCount >= count)
        {
            return true;
        }

        Refill();
        return _bitCount >= count;
    }

    /// <summary>
    /// Peeks the next <paramref name="count"/> MSB-first bits (0..32) without consuming them.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public readonly uint PeekBits(int count)
    {
        if (count == 0)
        {
            return 0;
        }

        return (uint)(_bitBuffer >> (64 - count));
    }

    /// <summary>
    /// Consumes <paramref name="count"/> previously buffered bits (0..32).
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void ConsumeBits(int count)
    {
        if ((uint)count > (uint)_bitCount)
        {
            ThrowTruncated(count);
        }

        _bitBuffer <<= count;
        _bitCount -= count;
    }

    /// <summary>
    /// Reads and consumes <paramref name="count"/> MSB-first bits (0..32), throwing <see cref="StbException"/> on EOF.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public uint ReadBits(int count)
    {
        if ((uint)count > 32u)
        {
            ThrowInvalidBitCount(count);
        }

        if (_bitCount < count)
        {
            Refill();
            if (_bitCount < count)
            {
                ThrowTruncated(count);
            }
        }

        uint value = PeekBits(count);
        _bitBuffer <<= count;
        _bitCount -= count;
        return value;
    }

    /// <summary>
    /// Discards up to 7 bits to align the bit stream to the next byte boundary.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void AlignToByte()
    {
        int discard = _bitCount & 7;
        if (discard != 0)
        {
            _bitBuffer <<= discard;
            _bitCount -= discard;
        }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private readonly void ThrowTruncated(int requestedBits) =>
        throw new StbException(
            StbErrorCode.TruncatedInput,
            $"Unexpected end of bit stream near byte offset {_bytePos}: needed {requestedBits} bit(s), only {RemainingBits} remaining.",
            _bytePos);

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void ThrowInvalidBitCount(int count) =>
        throw new StbException(
            StbErrorCode.InvalidArgument,
            $"Bit count must be in [0, 32], got {count}.");
}
