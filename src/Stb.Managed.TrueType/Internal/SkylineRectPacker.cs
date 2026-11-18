using System.Buffers;
using Stb.Managed.Internal;

namespace Stb.Managed.TrueType.Internal;

/// <summary>
/// Internal skyline bottom-left rectangle packer matching upstream <c>stb_rect_pack.h</c> v1.02
/// so <see cref="StbFontPacker"/> produces bit-identical packing without adding a cross-library
/// assembly dependency on <c>Stb.Managed.RectPack</c>.
/// </summary>
internal sealed class SkylineRectPacker : IDisposable
{
    private const int MaxCoord = 0x7FFFFFFF;
    private const int NullIndex = -1;

    private struct Node
    {
        public int X;
        public int Y;
        public int Next;
    }

    private Node[]? _nodes;
    private int _width;
    private int _height;
    private int _align;
    private int _activeHead;
    private int _freeHead;

    internal SkylineRectPacker(int width, int height, int numNodes)
    {
        _width = width;
        _height = height;
        int totalNodes = numNodes + 2;
        Node[] nodes = ArrayPool<Node>.Shared.Rent(totalNodes);
        _nodes = nodes;

        // Extra sentinel nodes live at indices 0 and 1; free pool lives at 2 .. numNodes + 1.
        for (int i = 0; i < numNodes - 1; ++i)
        {
            nodes[2 + i].Next = 2 + i + 1;
        }

        if (numNodes > 0)
        {
            nodes[2 + numNodes - 1].Next = NullIndex;
            _freeHead = 2;
        }
        else
        {
            _freeHead = NullIndex;
        }

        nodes[0] = new Node { X = 0, Y = 0, Next = 1 };
        nodes[1] = new Node { X = width, Y = 1 << 30, Next = NullIndex };
        _activeHead = 0;

        _align = numNodes > 0 ? (width + numNodes - 1) / numNodes : 1;
    }

    public void Dispose()
    {
        Node[]? nodes = _nodes;
        if (nodes is not null)
        {
            _nodes = null;
            ArrayPool<Node>.Shared.Return(nodes);
        }
    }

    private readonly struct FindResult
    {
        public readonly int X;
        public readonly int Y;
        public readonly bool Found;
        public readonly int PrevNode; // NullIndex means _activeHead itself.

        public FindResult(int x, int y, bool found, int prevNode)
        {
            X = x;
            Y = y;
            Found = found;
            PrevNode = prevNode;
        }
    }

    private static int FindMinY(Node[] nodes, int first, int x0, int width)
    {
        int nodeIdx = first;
        int x1 = x0 + width;
        int minY = 0;

        while (nodes[nodeIdx].X < x1)
        {
            if (nodes[nodeIdx].Y > minY)
            {
                minY = nodes[nodeIdx].Y;
            }

            nodeIdx = nodes[nodeIdx].Next;
        }

        return minY;
    }

    private FindResult FindBestPos(Node[] nodes, int width, int height)
    {
        int bestY = 1 << 30;
        bool hasBest = false;
        int bestPrev = NullIndex;

        width = width + _align - 1;
        width -= width % _align;

        if (width > _width || height > _height)
        {
            return default;
        }

        int nodeIdx = _activeHead;
        int prevIdx = NullIndex;

        while (nodes[nodeIdx].X + width <= _width)
        {
            int y = FindMinY(nodes, nodeIdx, nodes[nodeIdx].X, width);
            if (y < bestY)
            {
                bestY = y;
                bestPrev = prevIdx;
                hasBest = true;
            }

            prevIdx = nodeIdx;
            nodeIdx = nodes[nodeIdx].Next;
        }

        int bestX = !hasBest
            ? 0
            : (bestPrev == NullIndex ? nodes[_activeHead].X : nodes[nodes[bestPrev].Next].X);

        return new FindResult(bestX, bestY, hasBest, bestPrev);
    }

    private FindResult PackOne(Node[] nodes, int width, int height)
    {
        FindResult res = FindBestPos(nodes, width, height);
        if (!res.Found || res.Y + height > _height || _freeHead == NullIndex)
        {
            return default;
        }

        int newNode = _freeHead;
        _freeHead = nodes[newNode].Next;
        nodes[newNode].X = res.X;
        nodes[newNode].Y = res.Y + height;

        int cur = res.PrevNode == NullIndex ? _activeHead : nodes[res.PrevNode].Next;
        if (nodes[cur].X < res.X)
        {
            int next = nodes[cur].Next;
            nodes[cur].Next = newNode;
            cur = next;
        }
        else if (res.PrevNode == NullIndex)
        {
            _activeHead = newNode;
        }
        else
        {
            nodes[res.PrevNode].Next = newNode;
        }

        while (nodes[cur].Next != NullIndex && nodes[nodes[cur].Next].X <= res.X + width)
        {
            int next = nodes[cur].Next;
            nodes[cur].Next = _freeHead;
            _freeHead = cur;
            cur = next;
        }

        nodes[newNode].Next = cur;

        if (nodes[cur].X < res.X + width)
        {
            nodes[cur].X = res.X + width;
        }

        return res;
    }

    internal bool PackRects(Span<FontPackRect> rects)
    {
        Node[]? nodes = _nodes;
        Guard.ThrowIfDisposed(nodes is null, this);
        Node[] activeNodes = nodes!;

        int n = rects.Length;
        if (n == 0)
        {
            return true;
        }

        // Indirect index sort matching upstream rect_height_compare: (h desc, w desc, originalIndex asc).
        // Because originalIndex breaks all ties, the order is strictly unique and identical to C qsort.
        int[] order = ArrayPool<int>.Shared.Rent(n);
        try
        {
            for (int i = 0; i < n; ++i)
            {
                order[i] = i;
            }

            SortOrder(order.AsSpan(0, n), rects);

            bool allPacked = true;
            for (int k = 0; k < n; ++k)
            {
                int idx = order[k];
                ref FontPackRect r = ref rects[idx];
                if (r.Width == 0 || r.Height == 0)
                {
                    r.X = 0;
                    r.Y = 0;
                    r.WasPacked = true;
                }
                else
                {
                    FindResult fr = PackOne(activeNodes, r.Width, r.Height);
                    if (fr.Found)
                    {
                        r.X = fr.X;
                        r.Y = fr.Y;
                        r.WasPacked = true;
                    }
                    else
                    {
                        r.X = MaxCoord;
                        r.Y = MaxCoord;
                        r.WasPacked = false;
                        allPacked = false;
                    }
                }
            }

            return allPacked;
        }
        finally
        {
            ArrayPool<int>.Shared.Return(order);
        }
    }

    private static bool ComesBefore(int idxA, int idxB, ReadOnlySpan<FontPackRect> rects)
    {
        ref readonly FontPackRect a = ref rects[idxA];
        ref readonly FontPackRect b = ref rects[idxB];
        if (a.Height != b.Height) return a.Height > b.Height;
        if (a.Width != b.Width) return a.Width > b.Width;
        return idxA < idxB;
    }

    private static void SortOrder(Span<int> order, ReadOnlySpan<FontPackRect> rects)
    {
        int n = order.Length;
        if (n <= 1) return;

        int maxDepth = 0;
        for (int t = n; t > 0; t >>= 1) maxDepth++;
        IntroSort(order, rects, maxDepth * 2);
        InsertionSort(order, rects);
    }

    private static void IntroSort(Span<int> order, ReadOnlySpan<FontPackRect> rects, int depthLimit)
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
            int mid = n >> 1;
            if (ComesBefore(order[mid], order[0], rects)) (order[0], order[mid]) = (order[mid], order[0]);
            if (ComesBefore(order[n - 1], order[0], rects)) (order[0], order[n - 1]) = (order[n - 1], order[0]);
            if (ComesBefore(order[n - 1], order[mid], rects)) (order[mid], order[n - 1]) = (order[n - 1], order[mid]);

            int pivot = order[mid];
            (order[mid], order[n - 2]) = (order[n - 2], order[mid]);

            int i = 0;
            int j = n - 2;
            while (true)
            {
                while (ComesBefore(order[++i], pivot, rects)) { }
                while (ComesBefore(pivot, order[--j], rects)) { }
                if (i >= j) break;
                (order[i], order[j]) = (order[j], order[i]);
            }

            (order[i], order[n - 2]) = (order[n - 2], order[i]);

            if (i < n - 1 - i)
            {
                if (i > 1) IntroSort(order.Slice(0, i), rects, depthLimit);
                order = order.Slice(i + 1);
            }
            else
            {
                if (n - 1 - i > 1) IntroSort(order.Slice(i + 1), rects, depthLimit);
                order = order.Slice(0, i);
            }
        }
    }

    private static void HeapSort(Span<int> order, ReadOnlySpan<FontPackRect> rects)
    {
        int n = order.Length;
        for (int i = n / 2 - 1; i >= 0; --i) SiftDown(order, rects, i, n);
        for (int i = n - 1; i > 0; --i)
        {
            (order[0], order[i]) = (order[i], order[0]);
            SiftDown(order, rects, 0, i);
        }
    }

    private static void SiftDown(Span<int> order, ReadOnlySpan<FontPackRect> rects, int root, int count)
    {
        while (true)
        {
            int child = root * 2 + 1;
            if (child >= count) break;
            if (child + 1 < count && ComesBefore(order[child], order[child + 1], rects)) child++;
            if (!ComesBefore(order[root], order[child], rects)) break;
            (order[root], order[child]) = (order[child], order[root]);
            root = child;
        }
    }

    private static void InsertionSort(Span<int> order, ReadOnlySpan<FontPackRect> rects)
    {
        for (int i = 1; i < order.Length; ++i)
        {
            int key = order[i];
            int j = i - 1;
            while (j >= 0 && ComesBefore(key, order[j], rects))
            {
                order[j + 1] = order[j];
                j--;
            }

            order[j + 1] = key;
        }
    }
}
