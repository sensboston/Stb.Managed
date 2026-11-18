using System.Buffers;
using System.Numerics;
using System.Runtime.InteropServices;
using ILGPU;
using ILGPU.Runtime;

namespace Stb.Managed.Gpu.Internal;

/// <summary>
/// Rentable 1D dense GPU buffer wrapper returning its underlying <see cref="MemoryBuffer1D{T, TStride}"/>
/// back to <see cref="GpuBufferPool"/> upon disposal.
/// </summary>
internal sealed class PooledGpuBuffer<T> : IDisposable
    where T : unmanaged
{
    private readonly GpuBufferPool _pool;
    private MemoryBuffer1D<T, Stride1D.Dense>? _buffer;
    private readonly int _bucketIndex;

    internal PooledGpuBuffer(
        GpuBufferPool pool,
        MemoryBuffer1D<T, Stride1D.Dense> buffer,
        long logicalLength,
        int bucketIndex)
    {
        _pool = pool;
        _buffer = buffer;
        LogicalLength = logicalLength;
        _bucketIndex = bucketIndex;
    }

    public long LogicalLength { get; }

    public MemoryBuffer1D<T, Stride1D.Dense> Buffer
    {
        get
        {
            MemoryBuffer1D<T, Stride1D.Dense>? b = _buffer;
            ObjectDisposedException.ThrowIf(b is null, this);
            return b;
        }
    }

    public ArrayView1D<T, Stride1D.Dense> View => Buffer.View.SubView(0, LogicalLength);

    public void Dispose()
    {
        MemoryBuffer1D<T, Stride1D.Dense>? buf = Interlocked.Exchange(ref _buffer, null);
        if (buf is not null)
        {
            _pool.Return(buf, _bucketIndex);
        }
    }
}

/// <summary>
/// Thread-safe power-of-two bucketed VRAM pool for <c>byte</c>, <c>int</c>, <c>uint</c>, and <c>float</c>
/// device buffers, plus safe zero-<c>unsafe</c> pinned host-device DMA transfers.
/// </summary>
internal sealed class GpuBufferPool : IDisposable
{
    private const int MinBucketShift = 10; // 1024 elements min bucket
    private const int MaxBucketShift = 29; // 512M elements max pooled bucket
    private const int BucketCount = MaxBucketShift - MinBucketShift + 1;

    private readonly Accelerator _accelerator;
    private readonly long _maxPooledBytes;
    private readonly object _lock = new();
    private readonly Stack<MemoryBuffer1D<byte, Stride1D.Dense>>[] _byteBuckets;
    private readonly Stack<MemoryBuffer1D<int, Stride1D.Dense>>[] _intBuckets;
    private readonly Stack<MemoryBuffer1D<uint, Stride1D.Dense>>[] _uintBuckets;
    private readonly Stack<MemoryBuffer1D<float, Stride1D.Dense>>[] _floatBuckets;
    private long _pooledBytes;
    private bool _disposed;

    public GpuBufferPool(Accelerator accelerator, long maxPooledBytes = 256L * 1024 * 1024)
    {
        _accelerator = accelerator;
        _maxPooledBytes = Math.Max(0L, maxPooledBytes);
        _byteBuckets = CreateBuckets<byte>();
        _intBuckets = CreateBuckets<int>();
        _uintBuckets = CreateBuckets<uint>();
        _floatBuckets = CreateBuckets<float>();
    }

    private static Stack<MemoryBuffer1D<T, Stride1D.Dense>>[] CreateBuckets<T>()
        where T : unmanaged
    {
        var buckets = new Stack<MemoryBuffer1D<T, Stride1D.Dense>>[BucketCount];
        for (int i = 0; i < BucketCount; ++i)
        {
            buckets[i] = new Stack<MemoryBuffer1D<T, Stride1D.Dense>>();
        }

        return buckets;
    }

    public PooledGpuBuffer<byte> RentByte(long length, bool zeroInitialize = false) =>
        RentCore(_byteBuckets, length, sizeof(byte), zeroInitialize);

    public PooledGpuBuffer<int> RentInt32(long length, bool zeroInitialize = false) =>
        RentCore(_intBuckets, length, sizeof(int), zeroInitialize);

    public PooledGpuBuffer<uint> RentUInt32(long length, bool zeroInitialize = false) =>
        RentCore(_uintBuckets, length, sizeof(uint), zeroInitialize);

    public PooledGpuBuffer<float> RentFloat32(long length, bool zeroInitialize = false) =>
        RentCore(_floatBuckets, length, sizeof(float), zeroInitialize);

    public PooledGpuBuffer<T> RentUnpooled<T>(long length, bool zeroInitialize = false)
        where T : unmanaged
    {
        long actualLen = Math.Max(1L, length);
        MemoryBuffer1D<T, Stride1D.Dense> buf = _accelerator.Allocate1D<T>(actualLen);
        if (zeroInitialize)
        {
            buf.MemSetToZero();
        }

        return new PooledGpuBuffer<T>(this, buf, actualLen, bucketIndex: -1);
    }

    private PooledGpuBuffer<T> RentCore<T>(
        Stack<MemoryBuffer1D<T, Stride1D.Dense>>[] buckets,
        long length,
        int elemBytes,
        bool zeroInitialize)
        where T : unmanaged
    {
        long logicalLen = Math.Max(1L, length);
        int bucket = GetBucketIndex(logicalLen, out long capacity);
        MemoryBuffer1D<T, Stride1D.Dense>? buf = null;

        if (bucket >= 0)
        {
            lock (_lock)
            {
                ObjectDisposedException.ThrowIf(_disposed, this);
                if (buckets[bucket].Count > 0)
                {
                    buf = buckets[bucket].Pop();
                    _pooledBytes -= capacity * elemBytes;
                }
            }
        }

        buf ??= _accelerator.Allocate1D<T>(capacity);
        if (zeroInitialize)
        {
            buf.MemSetToZero();
        }

        return new PooledGpuBuffer<T>(this, buf, logicalLen, bucket);
    }

    internal void Return<T>(MemoryBuffer1D<T, Stride1D.Dense> buffer, int bucketIndex)
        where T : unmanaged
    {
        if (bucketIndex < 0)
        {
            buffer.Dispose();
            return;
        }

        long bytes = buffer.LengthInBytes;
        bool returned = false;
        lock (_lock)
        {
            if (!_disposed && _pooledBytes + bytes <= _maxPooledBytes)
            {
                if (typeof(T) == typeof(byte))
                {
                    _byteBuckets[bucketIndex].Push((MemoryBuffer1D<byte, Stride1D.Dense>)(object)buffer);
                    _pooledBytes += bytes;
                    returned = true;
                }
                else if (typeof(T) == typeof(int))
                {
                    _intBuckets[bucketIndex].Push((MemoryBuffer1D<int, Stride1D.Dense>)(object)buffer);
                    _pooledBytes += bytes;
                    returned = true;
                }
                else if (typeof(T) == typeof(uint))
                {
                    _uintBuckets[bucketIndex].Push((MemoryBuffer1D<uint, Stride1D.Dense>)(object)buffer);
                    _pooledBytes += bytes;
                    returned = true;
                }
                else if (typeof(T) == typeof(float))
                {
                    _floatBuckets[bucketIndex].Push((MemoryBuffer1D<float, Stride1D.Dense>)(object)buffer);
                    _pooledBytes += bytes;
                    returned = true;
                }
            }
        }

        if (!returned)
        {
            buffer.Dispose();
        }
    }

    /// <summary>
    /// Safely pins <paramref name="src"/> without <c>unsafe</c> code for the entire host-to-device DMA transfer.
    /// </summary>
    internal static void CopyFromHostArray<T>(
        ArrayView1D<T, Stride1D.Dense> dst,
        T[] src,
        int srcOffset,
        long count)
        where T : unmanaged
    {
        if (count <= 0)
        {
            return;
        }

        GCHandle handle = GCHandle.Alloc(src, GCHandleType.Pinned);
        try
        {
            dst.SubView(0, count).CopyFromCPU(ref src[srcOffset], count);
        }
        finally
        {
            handle.Free();
        }
    }

    /// <summary>
    /// Safely pins <paramref name="dst"/> without <c>unsafe</c> code for the entire device-to-host DMA transfer.
    /// </summary>
    internal static void CopyToHostArray<T>(
        ArrayView1D<T, Stride1D.Dense> src,
        T[] dst,
        int dstOffset,
        long count)
        where T : unmanaged
    {
        if (count <= 0)
        {
            return;
        }

        GCHandle handle = GCHandle.Alloc(dst, GCHandleType.Pinned);
        try
        {
            src.SubView(0, count).CopyToCPU(ref dst[dstOffset], count);
        }
        finally
        {
            handle.Free();
        }
    }

    /// <summary>
    /// Transfers a <see cref="ReadOnlySpan{T}"/> to device VRAM via a pooled pinned staging array (zero <c>unsafe</c>).
    /// </summary>
    internal static void CopyFromHostSpan<T>(
        ArrayView1D<T, Stride1D.Dense> dst,
        ReadOnlySpan<T> src)
        where T : unmanaged
    {
        if (src.IsEmpty)
        {
            return;
        }

        T[] rented = ArrayPool<T>.Shared.Rent(src.Length);
        try
        {
            src.CopyTo(rented);
            CopyFromHostArray(dst, rented, 0, src.Length);
        }
        finally
        {
            ArrayPool<T>.Shared.Return(rented);
        }
    }

    /// <summary>
    /// Transfers device VRAM to a <see cref="Span{T}"/> via a pooled pinned staging array (zero <c>unsafe</c>).
    /// </summary>
    internal static void CopyToHostSpan<T>(
        ArrayView1D<T, Stride1D.Dense> src,
        Span<T> dst)
        where T : unmanaged
    {
        if (dst.IsEmpty)
        {
            return;
        }

        T[] rented = ArrayPool<T>.Shared.Rent(dst.Length);
        try
        {
            CopyToHostArray(src, rented, 0, dst.Length);
            rented.AsSpan(0, dst.Length).CopyTo(dst);
        }
        finally
        {
            ArrayPool<T>.Shared.Return(rented);
        }
    }

    private static int GetBucketIndex(long length, out long capacity)
    {
        ulong minCap = 1UL << MinBucketShift;
        if ((ulong)length <= minCap)
        {
            capacity = (long)minCap;
            return 0;
        }

        ulong maxCap = 1UL << MaxBucketShift;
        if ((ulong)length > maxCap)
        {
            capacity = length;
            return -1;
        }

        ulong pow2 = BitOperations.RoundUpToPowerOf2((ulong)length);
        int shift = BitOperations.TrailingZeroCount(pow2);
        capacity = (long)pow2;
        return shift - MinBucketShift;
    }

    public void Dispose()
    {
        lock (_lock)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            DisposeBuckets(_byteBuckets);
            DisposeBuckets(_intBuckets);
            DisposeBuckets(_uintBuckets);
            DisposeBuckets(_floatBuckets);
            _pooledBytes = 0;
        }
    }

    private static void DisposeBuckets<T>(Stack<MemoryBuffer1D<T, Stride1D.Dense>>[] buckets)
        where T : unmanaged
    {
        for (int i = 0; i < buckets.Length; ++i)
        {
            while (buckets[i].Count > 0)
            {
                buckets[i].Pop().Dispose();
            }
        }
    }
}
