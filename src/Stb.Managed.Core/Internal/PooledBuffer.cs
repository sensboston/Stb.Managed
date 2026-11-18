using System.Buffers;

namespace Stb.Managed.Internal;

/// <summary>
/// Stack-allocated RAII handle over a scratch buffer rented from <see cref="ArrayPool{T}.Shared"/>.
/// Avoids heap allocations for the lease wrapper and ensures deterministic return via <c>using</c>.
/// </summary>
internal ref struct PooledBuffer<T>
{
    private T[]? _array;
    private readonly int _length;
    private readonly bool _clearOnReturn;

    public PooledBuffer(int length, bool clear = false, bool clearOnReturn = false)
    {
        Guard.ThrowIfNegative(length, nameof(length));
        _length = length;
        _clearOnReturn = clearOnReturn;

        if (length == 0)
        {
            _array = null;
            return;
        }

        _array = ArrayPool<T>.Shared.Rent(length);
        if (clear)
        {
            _array.AsSpan(0, length).Clear();
        }
    }

    public readonly Span<T> Span => _array is null ? Span<T>.Empty : _array.AsSpan(0, _length);

    public readonly int Length => _length;

    public void Dispose()
    {
        T[]? array = _array;
        if (array is not null)
        {
            _array = null;
            ArrayPool<T>.Shared.Return(array, _clearOnReturn);
        }
    }
}
