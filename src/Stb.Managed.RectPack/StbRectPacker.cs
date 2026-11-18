using System.Buffers;
using System.Runtime.CompilerServices;
using Stb.Managed.Internal;

namespace Stb.Managed.RectPack;

/// <summary>
/// Stateful 2D skyline bin packer implementing Sean Barrett's <c>stb_rect_pack.h</c> v1.02 algorithm
/// with zero-allocation reuse via <see cref="Reset"/>.
/// </summary>
/// <remarks>
/// Instances are not thread-safe for concurrent packing; use one <see cref="StbRectPacker"/> per thread or synchronize access.
/// </remarks>
public sealed class StbRectPacker : IDisposable
{
    private const int NullIndex = -1;
    private const int SentinelY = 1 << 30;

    private struct Node
    {
        public int X;
        public int Y;
        public int Next;
    }

    private readonly struct FindResult
    {
        public readonly int X;
        public readonly int Y;
        public readonly int PrevNode; // NullIndex => _activeHead itself; InvalidPrev => not found.

        public const int InvalidPrev = -2;

        public FindResult(int x, int y, int prevNode)
        {
            X = x;
            Y = y;
            PrevNode = prevNode;
        }

        public bool Found => PrevNode != InvalidPrev;
    }

    private readonly int _width;
    private readonly int _height;
    private readonly int _numNodes;
    private Node[]? _nodes;
    private int _align;
    private RectPackHeuristic _heuristic;
    private bool _allowOutOfMem;
    private int _activeHead;
    private int _freeHead;

    private StbRectPacker(
        int width,
        int height,
        int numNodes,
        Node[] nodes,
        RectPackHeuristic heuristic,
        bool allowOutOfMem)
    {
        _width = width;
        _height = height;
        _numNodes = numNodes;
        _nodes = nodes;
        _heuristic = heuristic;
        _allowOutOfMem = allowOutOfMem;
        _align = allowOutOfMem ? 1 : (width + numNodes - 1) / numNodes;
        InitNodeLists(nodes);
    }

    /// <summary>Gets the target bin width.</summary>
    public int Width => _width;

    /// <summary>Gets the target bin height.</summary>
    public int Height => _height;

    /// <summary>Gets the number of skyline storage nodes configured for this packer.</summary>
    public int NodeCount => _numNodes;

    /// <summary>Gets the active packing heuristic.</summary>
    public RectPackHeuristic Heuristic => _heuristic;

    /// <summary>Gets a value indicating whether out-of-memory skyline degradation is enabled.</summary>
    public bool AllowOutOfMem => _allowOutOfMem;

    /// <summary>
    /// Creates a new <see cref="StbRectPacker"/> for a bin of dimensions <paramref name="width"/> by <paramref name="height"/>.
    /// </summary>
    /// <param name="width">Target bin width (must be positive).</param>
    /// <param name="height">Target bin height (must be positive).</param>
    /// <param name="nodeCount">
    /// Number of skyline nodes to allocate. Pass <c>0</c> (default) to use <paramref name="width"/>, which guarantees optimal
    /// skyline resolution without horizontal alignment rounding.
    /// </param>
    /// <param name="heuristic">Skyline placement heuristic.</param>
    /// <param name="allowOutOfMem">
    /// If <c>false</c> (default), widths are rounded up to ensure packing never runs out of skyline nodes.
    /// If <c>true</c>, widths are not rounded and packing may fail if <paramref name="nodeCount"/> is smaller than <paramref name="width"/>.
    /// </param>
    /// <param name="options">Optional execution limits.</param>
    /// <returns>A disposable <see cref="StbRectPacker"/> instance.</returns>
    public static StbRectPacker Create(
        int width,
        int height,
        int nodeCount = 0,
        RectPackHeuristic heuristic = RectPackHeuristic.SkylineBottomLeft,
        bool allowOutOfMem = false,
        StbOptions? options = null)
    {
        StbOptions resolvedOptions = options ?? StbOptions.Default;
        _ = resolvedOptions.Limits.ValidateDimensions(width, height, 1);

        int resolvedNodes = nodeCount == 0 ? width : nodeCount;
        Guard.ThrowIfNegativeOrZero(resolvedNodes, nameof(nodeCount));
        ValidateHeuristic(heuristic);

        long nodeBytes = (long)(resolvedNodes + 2) * 12L;
        if (nodeBytes > resolvedOptions.Limits.MaxAllocationBytes)
        {
            throw new StbException(
                StbErrorCode.LimitExceeded,
                $"RectPack node buffer ({nodeBytes} bytes) exceeds MaxAllocationBytes ({resolvedOptions.Limits.MaxAllocationBytes}).");
        }

        Node[] nodes = ArrayPool<Node>.Shared.Rent(resolvedNodes + 2);
        return new StbRectPacker(width, height, resolvedNodes, nodes, heuristic, allowOutOfMem);
    }

    /// <summary>Releases pooled skyline storage back to <see cref="ArrayPool{T}.Shared"/>.</summary>
    public void Dispose()
    {
        Node[]? nodes = _nodes;
        if (nodes is not null)
        {
            _nodes = null;
            ArrayPool<Node>.Shared.Return(nodes);
        }
    }

    /// <summary>Selects the skyline packing heuristic for subsequent <see cref="PackRects"/> calls.</summary>
    /// <param name="heuristic">The heuristic to use.</param>
    public void SetHeuristic(RectPackHeuristic heuristic)
    {
        EnsureNotDisposed();
        ValidateHeuristic(heuristic);
        _heuristic = heuristic;
    }

    /// <summary>
    /// Configures whether the packer allows running out of skyline nodes instead of rounding rectangle widths upward.
    /// </summary>
    /// <param name="allowOutOfMem"><c>true</c> to disable alignment rounding and allow out-of-memory failures; <c>false</c> to round widths up.</param>
    public void SetAllowOutOfMem(bool allowOutOfMem)
    {
        EnsureNotDisposed();
        _allowOutOfMem = allowOutOfMem;
        if (allowOutOfMem)
        {
            _align = 1;
        }
        else
        {
            int align = (_width + _numNodes - 1) / _numNodes;
            _align = align;
        }
    }

    /// <summary>
    /// Resets the bin to completely empty without reallocating internal skyline storage.
    /// </summary>
    public void Reset()
    {
        Node[] nodes = EnsureNotDisposed();
        InitNodeLists(nodes);
    }

    /// <summary>
    /// Packs the supplied rectangles into the remaining space of this bin.
    /// Can be called multiple times incrementally; call <see cref="Reset"/> to start a new bin.
    /// </summary>
    /// <param name="rects">Span of rectangles to pack; <c>X</c>, <c>Y</c>, and <c>WasPacked</c> are populated in place.</param>
    /// <returns><c>true</c> if every rectangle in <paramref name="rects"/> was packed; otherwise <c>false</c>.</returns>
    public bool PackRects(Span<PackRect> rects)
    {
        Node[] nodes = EnsureNotDisposed();
        int count = rects.Length;
        if (count == 0)
        {
            return true;
        }

        // Validate dimensions upfront so partial mutations cannot occur on invalid negative sizes.
        for (int i = 0; i < count; ++i)
        {
            ref readonly PackRect r = ref rects[i];
            if (r.Width < 0 || r.Height < 0)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(rects),
                    $"Rectangle at index {i} has negative dimensions ({r.Width}x{r.Height}).");
            }
        }

        // Sort an index permutation (height desc, width desc, original index asc) so we never have to permute
        // and un-permute the caller's PackRect structs twice.
        int[] orderPool = ArrayPool<int>.Shared.Rent(count);
        try
        {
            Span<int> order = orderPool.AsSpan(0, count);
            for (int i = 0; i < count; ++i)
            {
                order[i] = i;
            }

            if (count > 1)
            {
                SortRectIndices(order, rects);
            }

            bool allPacked = true;
            for (int i = 0; i < count; ++i)
            {
                int idx = order[i];
                ref PackRect r = ref rects[idx];

                if (r.Width == 0 || r.Height == 0)
                {
                    r.X = 0;
                    r.Y = 0;
                    r.WasPacked = true;
                }
                else
                {
                    FindResult fr = PackOne(nodes, r.Width, r.Height);
                    if (fr.Found)
                    {
                        r.X = fr.X;
                        r.Y = fr.Y;
                        r.WasPacked = true;
                    }
                    else
                    {
                        r.X = 0;
                        r.Y = 0;
                        r.WasPacked = false;
                        allPacked = false;
                    }
                }
            }

            return allPacked;
        }
        finally
        {
            ArrayPool<int>.Shared.Return(orderPool);
        }
    }

    private void InitNodeLists(Node[] nodes)
    {
        int n = _numNodes;
        for (int i = 0; i < n - 1; ++i)
        {
            nodes[i].Next = i + 1;
        }

        nodes[n - 1].Next = NullIndex;
        _freeHead = 0;

        int extra0 = n;
        int extra1 = n + 1;
        nodes[extra0] = new Node { X = 0, Y = 0, Next = extra1 };
        nodes[extra1] = new Node { X = _width, Y = SentinelY, Next = NullIndex };
        _activeHead = extra0;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int FindMinY(Node[] nodes, int first, int x0, int width, out int wasteOut)
    {
        int node = first;
        int x1 = x0 + width;
        int minY = 0;
        int wasteArea = 0;
        int visitedWidth = 0;

        while (nodes[node].X < x1)
        {
            ref readonly Node cur = ref nodes[node];
            if (cur.Y > minY)
            {
                wasteArea += visitedWidth * (cur.Y - minY);
                minY = cur.Y;
                if (cur.X < x0)
                {
                    visitedWidth += nodes[cur.Next].X - x0;
                }
                else
                {
                    visitedWidth += nodes[cur.Next].X - cur.X;
                }
            }
            else
            {
                int underWidth = nodes[cur.Next].X - cur.X;
                if (underWidth + visitedWidth > width)
                {
                    underWidth = width - visitedWidth;
                }

                wasteArea += underWidth * (minY - cur.Y);
                visitedWidth += underWidth;
            }

            node = cur.Next;
        }

        wasteOut = wasteArea;
        return minY;
    }

    private FindResult FindBestPos(Node[] nodes, int width, int height)
    {
        int bestWaste = 1 << 30;
        int bestY = 1 << 30;
        int bestPrev = FindResult.InvalidPrev;

        // Align width up to _align (no-op when _align == 1).
        width = width + _align - 1;
        width -= width % _align;

        if (width > _width || height > _height)
        {
            return new FindResult(0, 0, FindResult.InvalidPrev);
        }

        int node = _activeHead;
        int prev = NullIndex; // NullIndex represents &_activeHead
        bool isBestFit = _heuristic == RectPackHeuristic.SkylineBestFit;

        while (nodes[node].X + width <= _width)
        {
            int y = FindMinY(nodes, node, nodes[node].X, width, out int waste);
            if (!isBestFit)
            {
                // Bottom-left
                if (y < bestY)
                {
                    bestY = y;
                    bestPrev = prev;
                }
            }
            else
            {
                // Best-fit
                if (y + height <= _height)
                {
                    if (y < bestY || (y == bestY && waste < bestWaste))
                    {
                        bestY = y;
                        bestWaste = waste;
                        bestPrev = prev;
                    }
                }
            }

            prev = node;
            node = nodes[node].Next;
        }

        int bestX = bestPrev == FindResult.InvalidPrev
            ? 0
            : nodes[bestPrev == NullIndex ? _activeHead : nodes[bestPrev].Next].X;

        if (isBestFit)
        {
            int tail = _activeHead;
            node = _activeHead;
            prev = NullIndex;

            while (tail != NullIndex && nodes[tail].X < width)
            {
                tail = nodes[tail].Next;
            }

            while (tail != NullIndex)
            {
                int xpos = nodes[tail].X - width;
                while (nodes[nodes[node].Next].X <= xpos)
                {
                    prev = node;
                    node = nodes[node].Next;
                }

                int y = FindMinY(nodes, node, xpos, width, out int waste);
                if (y + height <= _height)
                {
                    if (y <= bestY)
                    {
                        if (y < bestY || waste < bestWaste || (waste == bestWaste && xpos < bestX))
                        {
                            bestX = xpos;
                            bestY = y;
                            bestWaste = waste;
                            bestPrev = prev;
                        }
                    }
                }

                tail = nodes[tail].Next;
            }
        }

        return new FindResult(bestX, bestY, bestPrev);
    }

    private FindResult PackOne(Node[] nodes, int width, int height)
    {
        FindResult res = FindBestPos(nodes, width, height);
        if (!res.Found || res.Y + height > _height || _freeHead == NullIndex)
        {
            return new FindResult(0, 0, FindResult.InvalidPrev);
        }

        int node = _freeHead;
        _freeHead = nodes[node].Next;
        nodes[node].X = res.X;
        nodes[node].Y = res.Y + height;

        int cur = res.PrevNode == NullIndex ? _activeHead : nodes[res.PrevNode].Next;
        if (nodes[cur].X < res.X)
        {
            int next = nodes[cur].Next;
            nodes[cur].Next = node;
            cur = next;
        }
        else if (res.PrevNode == NullIndex)
        {
            _activeHead = node;
        }
        else
        {
            nodes[res.PrevNode].Next = node;
        }

        while (nodes[cur].Next != NullIndex &&
               nodes[nodes[cur].Next].X <= res.X + width)
        {
            int next = nodes[cur].Next;
            nodes[cur].Next = _freeHead;
            _freeHead = cur;
            cur = next;
        }

        nodes[node].Next = cur;
        if (nodes[cur].X < res.X + width)
        {
            nodes[cur].X = res.X + width;
        }

        return res;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool ComesBefore(ReadOnlySpan<PackRect> rects, int idxA, int idxB)
    {
        ref readonly PackRect a = ref rects[idxA];
        ref readonly PackRect b = ref rects[idxB];
        if (a.Height != b.Height)
        {
            return a.Height > b.Height;
        }

        if (a.Width != b.Width)
        {
            return a.Width > b.Width;
        }

        return idxA < idxB;
    }

    private static void SortRectIndices(Span<int> order, ReadOnlySpan<PackRect> rects)
    {
        int n = order.Length;
        int depthLimit = 0;
        for (int t = n; t > 0; t >>= 1)
        {
            depthLimit++;
        }

        IntroSort(order, rects, depthLimit * 2);
        InsertionSort(order, rects);
    }

    private static void IntroSort(Span<int> order, ReadOnlySpan<PackRect> rects, int depthLimit)
    {
        while (order.Length > 16)
        {
            if (depthLimit == 0)
            {
                HeapSort(order, rects);
                return;
            }

            depthLimit--;
            int n = order.Length;
            int m = n >> 1;

            // Median-of-three pivot selection moved to index 0.
            if (ComesBefore(rects, order[m], order[0]))
            {
                (order[0], order[m]) = (order[m], order[0]);
            }

            if (ComesBefore(rects, order[n - 1], order[0]))
            {
                (order[0], order[n - 1]) = (order[n - 1], order[0]);
            }

            if (ComesBefore(rects, order[n - 1], order[m]))
            {
                (order[m], order[n - 1]) = (order[n - 1], order[m]);
            }

            (order[0], order[m]) = (order[m], order[0]);
            int pivot = order[0];

            int i = 1;
            int j = n - 1;
            for (; ; )
            {
                while (i < n && ComesBefore(rects, order[i], pivot))
                {
                    ++i;
                }

                while (j > 0 && ComesBefore(rects, pivot, order[j]))
                {
                    --j;
                }

                if (i >= j)
                {
                    break;
                }

                (order[i], order[j]) = (order[j], order[i]);
                ++i;
                --j;
            }

            (order[0], order[j]) = (order[j], order[0]);

            int leftLen = j;
            int rightStart = j + 1;
            int rightLen = n - rightStart;
            if (leftLen < rightLen)
            {
                if (leftLen > 1)
                {
                    IntroSort(order.Slice(0, leftLen), rects, depthLimit);
                }

                order = order.Slice(rightStart, rightLen);
            }
            else
            {
                if (rightLen > 1)
                {
                    IntroSort(order.Slice(rightStart, rightLen), rects, depthLimit);
                }

                order = order.Slice(0, leftLen);
            }
        }
    }

    private static void HeapSort(Span<int> order, ReadOnlySpan<PackRect> rects)
    {
        int n = order.Length;
        for (int i = n / 2 - 1; i >= 0; --i)
        {
            SiftDown(order, rects, i, n);
        }

        for (int i = n - 1; i > 0; --i)
        {
            (order[0], order[i]) = (order[i], order[0]);
            SiftDown(order, rects, 0, i);
        }
    }

    private static void SiftDown(Span<int> order, ReadOnlySpan<PackRect> rects, int root, int count)
    {
        while (true)
        {
            int child = root * 2 + 1;
            if (child >= count)
            {
                break;
            }

            if (child + 1 < count && ComesBefore(rects, order[child], order[child + 1]))
            {
                child++;
            }

            if (!ComesBefore(rects, order[root], order[child]))
            {
                break;
            }

            (order[root], order[child]) = (order[child], order[root]);
            root = child;
        }
    }

    private static void InsertionSort(Span<int> order, ReadOnlySpan<PackRect> rects)
    {
        int n = order.Length;
        for (int i = 1; i < n; ++i)
        {
            int key = order[i];
            int j = i;
            while (j > 0 && ComesBefore(rects, key, order[j - 1]))
            {
                order[j] = order[j - 1];
                --j;
            }

            if (j != i)
            {
                order[j] = key;
            }
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private Node[] EnsureNotDisposed()
    {
        Node[]? nodes = _nodes;
        Guard.ThrowIfDisposed(nodes is null, this);
        return nodes!;
    }

    private static void ValidateHeuristic(RectPackHeuristic heuristic)
    {
        if (heuristic is not (RectPackHeuristic.SkylineBottomLeft or RectPackHeuristic.SkylineBestFit))
        {
            throw new ArgumentOutOfRangeException(
                nameof(heuristic),
                heuristic,
                "Heuristic must be SkylineBottomLeft or SkylineBestFit.");
        }
    }
}
