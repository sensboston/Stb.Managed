using System;
using System.Buffers;
using System.Buffers.Binary;
using System.IO;
using System.Runtime.CompilerServices;
using Stb.Managed.Internal;

namespace Stb.Managed.ImageWrite.Internal;

/// <summary>
/// Stack-allocated buffered byte output sink supporting three targets without heap allocations on the
/// hot path: a caller-supplied <see cref="Span{T}"/> (<c>TryWrite*</c>), a <see cref="Stream"/>
/// (buffered through a pooled scratch array), or a growable pooled array (materialized into an exact
/// <see cref="byte"/>[] at completion).
/// </summary>
internal ref struct ByteSink
{
    private const int DefaultStreamBufferSize = 16 * 1024;
    private const int DefaultGrowInitialSize = 4096;

    private readonly SinkMode _mode;
    private readonly Stream? _stream;
    private readonly StbLimits _limits;
    private Span<byte> _buffer;
    private byte[]? _rented;
    private int _pos;
    private bool _overflowed;

    private enum SinkMode : byte
    {
        FixedSpan = 0,
        Stream = 1,
        GrowableArray = 2,
    }

    private ByteSink(SinkMode mode, Span<byte> fixedBuffer, Stream? stream, byte[]? rented, StbLimits limits)
    {
        _mode = mode;
        _stream = stream;
        _limits = limits;
        _rented = rented;
        _buffer = rented is not null ? rented.AsSpan() : fixedBuffer;
        _pos = 0;
        _overflowed = false;
    }

    public static ByteSink ForSpan(Span<byte> destination, StbLimits limits) =>
        new(SinkMode.FixedSpan, destination, null, null, limits);

    public static ByteSink ForStream(Stream destination, StbLimits limits, int bufferSize = DefaultStreamBufferSize)
    {
        byte[] rented = ArrayPool<byte>.Shared.Rent(Math.Max(256, bufferSize));
        return new(SinkMode.Stream, Span<byte>.Empty, destination, rented, limits);
    }

    public static ByteSink ForGrowableArray(StbLimits limits, int initialCapacity = DefaultGrowInitialSize)
    {
        int cap = Math.Max(256, initialCapacity);
        limits.ValidateAllocation(cap);
        byte[] rented = ArrayPool<byte>.Shared.Rent(cap);
        return new(SinkMode.GrowableArray, Span<byte>.Empty, null, rented, limits);
    }

    public readonly bool Overflowed => _overflowed;

    public readonly int Position => _pos;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void WriteByte(byte value)
    {
        if (_overflowed)
        {
            return;
        }

        if (_pos >= _buffer.Length && !MakeRoom(1))
        {
            return;
        }

        _buffer[_pos++] = value;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Write2(byte a, byte b)
    {
        if (_overflowed)
        {
            return;
        }

        if (_pos + 2 > _buffer.Length && !MakeRoom(2))
        {
            return;
        }

        _buffer[_pos] = a;
        _buffer[_pos + 1] = b;
        _pos += 2;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Write3(byte a, byte b, byte c)
    {
        if (_overflowed)
        {
            return;
        }

        if (_pos + 3 > _buffer.Length && !MakeRoom(3))
        {
            return;
        }

        _buffer[_pos] = a;
        _buffer[_pos + 1] = b;
        _buffer[_pos + 2] = c;
        _pos += 3;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Write4(byte a, byte b, byte c, byte d)
    {
        if (_overflowed)
        {
            return;
        }

        if (_pos + 4 > _buffer.Length && !MakeRoom(4))
        {
            return;
        }

        _buffer[_pos] = a;
        _buffer[_pos + 1] = b;
        _buffer[_pos + 2] = c;
        _buffer[_pos + 3] = d;
        _pos += 4;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void WriteUInt16LittleEndian(ushort value)
    {
        if (_overflowed)
        {
            return;
        }

        if (_pos + 2 > _buffer.Length && !MakeRoom(2))
        {
            return;
        }

        BinaryPrimitives.WriteUInt16LittleEndian(_buffer.Slice(_pos, 2), value);
        _pos += 2;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void WriteUInt32LittleEndian(uint value)
    {
        if (_overflowed)
        {
            return;
        }

        if (_pos + 4 > _buffer.Length && !MakeRoom(4))
        {
            return;
        }

        BinaryPrimitives.WriteUInt32LittleEndian(_buffer.Slice(_pos, 4), value);
        _pos += 4;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void WriteUInt32BigEndian(uint value)
    {
        if (_overflowed)
        {
            return;
        }

        if (_pos + 4 > _buffer.Length && !MakeRoom(4))
        {
            return;
        }

        BinaryPrimitives.WriteUInt32BigEndian(_buffer.Slice(_pos, 4), value);
        _pos += 4;
    }

    public void WriteBytes(scoped ReadOnlySpan<byte> data)
    {
        if (_overflowed || data.IsEmpty)
        {
            return;
        }

        if (_pos + data.Length <= _buffer.Length)
        {
            data.CopyTo(_buffer.Slice(_pos));
            _pos += data.Length;
            return;
        }

        if (_mode == SinkMode.FixedSpan)
        {
            _overflowed = true;
            return;
        }

        if (_mode == SinkMode.Stream)
        {
            Flush();
            if (data.Length >= _buffer.Length)
            {
                _stream!.Write(data);
            }
            else
            {
                data.CopyTo(_buffer);
                _pos = data.Length;
            }

            return;
        }

        // GrowableArray
        Grow(data.Length);
        data.CopyTo(_buffer.Slice(_pos));
        _pos += data.Length;
    }

    public void WriteZeros(int count)
    {
        if (_overflowed || count <= 0)
        {
            return;
        }

        if (_pos + count <= _buffer.Length)
        {
            _buffer.Slice(_pos, count).Clear();
            _pos += count;
            return;
        }

        if (_mode == SinkMode.FixedSpan)
        {
            _overflowed = true;
            return;
        }

        if (_mode == SinkMode.Stream)
        {
            Flush();
            int remaining = count;
            while (remaining > 0)
            {
                int chunk = Math.Min(remaining, _buffer.Length);
                _buffer.Slice(0, chunk).Clear();
                _stream!.Write(_buffer.Slice(0, chunk));
                remaining -= chunk;
            }

            return;
        }

        Grow(count);
        _buffer.Slice(_pos, count).Clear();
        _pos += count;
    }

    /// <summary>
    /// Reserves <paramref name="count"/> contiguous bytes in the buffer if supported and returns
    /// a writable slice, advancing the write cursor. Returns an empty span on FixedSpan overflow.
    /// </summary>
    public Span<byte> Reserve(int count)
    {
        if (_overflowed || count <= 0)
        {
            return Span<byte>.Empty;
        }

        if (_pos + count > _buffer.Length)
        {
            if (_mode == SinkMode.FixedSpan)
            {
                _overflowed = true;
                return Span<byte>.Empty;
            }

            if (_mode == SinkMode.Stream)
            {
                Flush();
                if (count > _buffer.Length)
                {
                    _limits.ValidateAllocation(count);
                    byte[] next = ArrayPool<byte>.Shared.Rent(count);
                    ArrayPool<byte>.Shared.Return(_rented!);
                    _rented = next;
                    _buffer = next;
                }
            }
            else
            {
                Grow(count);
            }
        }

        Span<byte> slice = _buffer.Slice(_pos, count);
        _pos += count;
        return slice;
    }

    public void Flush()
    {
        if (_mode == SinkMode.Stream && _pos > 0)
        {
            _stream!.Write(_buffer.Slice(0, _pos));
            _pos = 0;
        }
    }

    public byte[] ToArray()
    {
        byte[] result = ArrayAllocation.AllocateUninitialized<byte>(_pos);
        _buffer.Slice(0, _pos).CopyTo(result);
        return result;
    }

    public void Dispose()
    {
        byte[]? rented = _rented;
        if (rented is not null)
        {
            _rented = null;
            _buffer = Span<byte>.Empty;
            ArrayPool<byte>.Shared.Return(rented);
        }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private bool MakeRoom(int needed)
    {
        if (_mode == SinkMode.FixedSpan)
        {
            _overflowed = true;
            return false;
        }

        if (_mode == SinkMode.Stream)
        {
            Flush();
            if (needed > _buffer.Length)
            {
                _limits.ValidateAllocation(needed);
                byte[] next = ArrayPool<byte>.Shared.Rent(needed);
                ArrayPool<byte>.Shared.Return(_rented!);
                _rented = next;
                _buffer = next;
            }

            return true;
        }

        Grow(needed);
        return true;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private void Grow(int needed)
    {
        long minRequired = (long)_pos + needed;
        long doubled = Math.Max((long)_buffer.Length * 2L, DefaultGrowInitialSize);
        long nextSize = Math.Max(minRequired, doubled);

        if (nextSize > ArrayAllocation.MaxByteArrayLength)
        {
            nextSize = minRequired;
        }

        _limits.ValidateAllocation(nextSize);

        byte[] next = ArrayPool<byte>.Shared.Rent((int)nextSize);
        if (_pos > 0)
        {
            _buffer.Slice(0, _pos).CopyTo(next);
        }

        byte[]? old = _rented;
        _rented = next;
        _buffer = next;
        if (old is not null)
        {
            ArrayPool<byte>.Shared.Return(old);
        }
    }
}
