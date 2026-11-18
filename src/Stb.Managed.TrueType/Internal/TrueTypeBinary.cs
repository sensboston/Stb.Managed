using System.Buffers.Binary;
using System.Runtime.CompilerServices;

namespace Stb.Managed.TrueType.Internal;

internal static class TrueTypeBinary
{
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static byte ReadU8(ReadOnlySpan<byte> data, int offset) =>
        (uint)offset < (uint)data.Length ? data[offset] : (byte)0;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static sbyte ReadS8(ReadOnlySpan<byte> data, int offset) =>
        (uint)offset < (uint)data.Length ? unchecked((sbyte)data[offset]) : (sbyte)0;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static ushort ReadU16(ReadOnlySpan<byte> data, int offset) =>
        (uint)offset <= (uint)(data.Length - 2) && data.Length >= 2
            ? BinaryPrimitives.ReadUInt16BigEndian(data.Slice(offset, 2))
            : (ushort)0;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static short ReadS16(ReadOnlySpan<byte> data, int offset) =>
        (uint)offset <= (uint)(data.Length - 2) && data.Length >= 2
            ? BinaryPrimitives.ReadInt16BigEndian(data.Slice(offset, 2))
            : (short)0;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static uint ReadU32(ReadOnlySpan<byte> data, int offset) =>
        (uint)offset <= (uint)(data.Length - 4) && data.Length >= 4
            ? BinaryPrimitives.ReadUInt32BigEndian(data.Slice(offset, 4))
            : 0u;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static int ReadS32(ReadOnlySpan<byte> data, int offset) =>
        (uint)offset <= (uint)(data.Length - 4) && data.Length >= 4
            ? BinaryPrimitives.ReadInt32BigEndian(data.Slice(offset, 4))
            : 0;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static bool HasRange(ReadOnlySpan<byte> data, int offset, int length) =>
        offset >= 0 && length >= 0 && offset <= data.Length && length <= data.Length - offset;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static bool HasRangeU(ReadOnlySpan<byte> data, uint offset, uint length) =>
        offset <= (uint)data.Length && length <= (uint)data.Length - offset;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static bool TagEquals(ReadOnlySpan<byte> data, int offset, byte c0, byte c1, byte c2, byte c3) =>
        offset >= 0 &&
        offset <= data.Length - 4 &&
        data[offset] == c0 &&
        data[offset + 1] == c1 &&
        data[offset + 2] == c2 &&
        data[offset + 3] == c3;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static bool TagEquals(ReadOnlySpan<byte> data, int offset, uint tag) =>
        offset >= 0 &&
        offset <= data.Length - 4 &&
        BinaryPrimitives.ReadUInt32BigEndian(data.Slice(offset, 4)) == tag;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static uint MakeTag(byte c0, byte c1, byte c2, byte c3) =>
        ((uint)c0 << 24) | ((uint)c1 << 16) | ((uint)c2 << 8) | c3;

    internal static readonly uint TagTyp1 = MakeTag((byte)'t', (byte)'y', (byte)'p', (byte)'1');
    internal static readonly uint TagOtto = MakeTag((byte)'O', (byte)'T', (byte)'T', (byte)'O');
    internal static readonly uint TagTrue = MakeTag((byte)'t', (byte)'r', (byte)'u', (byte)'e');
    internal static readonly uint TagTtcf = MakeTag((byte)'t', (byte)'t', (byte)'c', (byte)'f');
    internal static readonly uint TagCmap = MakeTag((byte)'c', (byte)'m', (byte)'a', (byte)'p');
    internal static readonly uint TagLoca = MakeTag((byte)'l', (byte)'o', (byte)'c', (byte)'a');
    internal static readonly uint TagHead = MakeTag((byte)'h', (byte)'e', (byte)'a', (byte)'d');
    internal static readonly uint TagGlyf = MakeTag((byte)'g', (byte)'l', (byte)'y', (byte)'f');
    internal static readonly uint TagHhea = MakeTag((byte)'h', (byte)'h', (byte)'e', (byte)'a');
    internal static readonly uint TagHmtx = MakeTag((byte)'h', (byte)'m', (byte)'t', (byte)'x');
    internal static readonly uint TagVhea = MakeTag((byte)'v', (byte)'h', (byte)'e', (byte)'a');
    internal static readonly uint TagVmtx = MakeTag((byte)'v', (byte)'m', (byte)'t', (byte)'x');
    internal static readonly uint TagKern = MakeTag((byte)'k', (byte)'e', (byte)'r', (byte)'n');
    internal static readonly uint TagGpos = MakeTag((byte)'G', (byte)'P', (byte)'O', (byte)'S');
    internal static readonly uint TagCff = MakeTag((byte)'C', (byte)'F', (byte)'F', (byte)' ');
    internal static readonly uint TagMaxp = MakeTag((byte)'m', (byte)'a', (byte)'x', (byte)'p');
    internal static readonly uint TagOs2 = MakeTag((byte)'O', (byte)'S', (byte)'/', (byte)'2');
    internal static readonly uint TagPost = MakeTag((byte)'p', (byte)'o', (byte)'s', (byte)'t');
    internal static readonly uint TagSvg = MakeTag((byte)'S', (byte)'V', (byte)'G', (byte)' ');
    internal static readonly uint TagName = MakeTag((byte)'n', (byte)'a', (byte)'m', (byte)'e');

    internal static bool IsFont(ReadOnlySpan<byte> data, int offset)
    {
        if (offset < 0 || offset > data.Length - 4)
        {
            return false;
        }

        uint tag = BinaryPrimitives.ReadUInt32BigEndian(data.Slice(offset, 4));
        return tag == 0x31000000u || // '1', 0, 0, 0 (TrueType 1)
               tag == TagTyp1 ||
               tag == TagOtto ||
               tag == 0x00010000u || // OpenType 1.0
               tag == TagTrue;
    }

    internal static int FindTable(ReadOnlySpan<byte> data, int fontStart, uint tag, out int tableLength)
    {
        tableLength = 0;
        if (!HasRange(data, fontStart, 12))
        {
            return 0;
        }

        int numTables = ReadU16(data, fontStart + 4);
        long dirEnd = (long)fontStart + 12 + (long)numTables * 16;
        if (dirEnd > data.Length)
        {
            numTables = (data.Length - (fontStart + 12)) / 16;
            if (numTables <= 0)
            {
                return 0;
            }
        }

        int tableDir = fontStart + 12;
        for (int i = 0; i < numTables; ++i)
        {
            int loc = tableDir + 16 * i;
            if (BinaryPrimitives.ReadUInt32BigEndian(data.Slice(loc, 4)) == tag)
            {
                uint offset = BinaryPrimitives.ReadUInt32BigEndian(data.Slice(loc + 8, 4));
                uint length = BinaryPrimitives.ReadUInt32BigEndian(data.Slice(loc + 12, 4));
                if (offset == 0 || offset > (uint)data.Length || offset > int.MaxValue)
                {
                    return 0;
                }

                uint maxLen = (uint)data.Length - offset;
                tableLength = (int)Math.Min(length, maxLen);
                return (int)offset;
            }
        }

        return 0;
    }

    internal static int FindTable(ReadOnlySpan<byte> data, int fontStart, uint tag) =>
        FindTable(data, fontStart, tag, out _);

    internal static int GetNumberOfFonts(ReadOnlySpan<byte> data)
    {
        if (data.Length < 4)
        {
            return 0;
        }

        if (IsFont(data, 0))
        {
            return 1;
        }

        if (data.Length >= 12 && TagEquals(data, 0, TagTtcf))
        {
            uint version = ReadU32(data, 4);
            if (version == 0x00010000u || version == 0x00020000u)
            {
                int count = ReadS32(data, 8);
                if (count < 0)
                {
                    return 0;
                }

                // Cap to what could physically fit in the TTC header offset table.
                int maxPossible = (data.Length - 12) / 4;
                return count <= maxPossible ? count : 0;
            }
        }

        return 0;
    }

    internal static int GetFontOffsetForIndex(ReadOnlySpan<byte> data, int index)
    {
        if (index < 0 || data.Length < 4)
        {
            return -1;
        }

        if (IsFont(data, 0))
        {
            return index == 0 ? 0 : -1;
        }

        if (data.Length >= 12 && TagEquals(data, 0, TagTtcf))
        {
            uint version = ReadU32(data, 4);
            if (version == 0x00010000u || version == 0x00020000u)
            {
                int n = ReadS32(data, 8);
                if (index >= n)
                {
                    return -1;
                }

                long entryOff = 12L + (long)index * 4;
                if (entryOff + 4 > data.Length)
                {
                    return -1;
                }

                uint fontOff = ReadU32(data, (int)entryOff);
                if (fontOff > (uint)data.Length || fontOff > int.MaxValue)
                {
                    return -1;
                }

                return (int)fontOff;
            }
        }

        return -1;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static int IFloor(float x) => (int)MathF.Floor(x);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static int ICeil(float x) => (int)MathF.Ceiling(x);
}

internal struct CffBuffer
{
    public int Offset;
    public int Size;
    public int Cursor;

    public static CffBuffer Empty => default;

    public CffBuffer(int offset, int size)
    {
        Offset = offset;
        Size = size;
        Cursor = 0;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public byte Get8(ReadOnlySpan<byte> data)
    {
        if (Cursor >= Size)
        {
            return 0;
        }

        int pos = Offset + Cursor;
        Cursor++;
        return (uint)pos < (uint)data.Length ? data[pos] : (byte)0;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public readonly byte Peek8(ReadOnlySpan<byte> data)
    {
        if (Cursor >= Size)
        {
            return 0;
        }

        int pos = Offset + Cursor;
        return (uint)pos < (uint)data.Length ? data[pos] : (byte)0;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Seek(int position)
    {
        Cursor = (position > Size || position < 0) ? Size : position;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Skip(int delta)
    {
        long next = (long)Cursor + delta;
        Cursor = (next > Size || next < 0) ? Size : (int)next;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public uint Get(ReadOnlySpan<byte> data, int count)
    {
        uint v = 0;
        for (int i = 0; i < count; i++)
        {
            v = (v << 8) | Get8(data);
        }

        return v;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public uint Get16(ReadOnlySpan<byte> data) => Get(data, 2);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public uint Get32(ReadOnlySpan<byte> data) => Get(data, 4);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public readonly CffBuffer Range(int relativeOffset, int length)
    {
        if (relativeOffset < 0 || length < 0 || relativeOffset > Size || length > Size - relativeOffset)
        {
            return Empty;
        }

        return new CffBuffer(Offset + relativeOffset, length);
    }

    public static CffBuffer GetIndex(ReadOnlySpan<byte> data, ref CffBuffer b)
    {
        int start = b.Cursor;
        int count = (int)b.Get16(data);
        if (count != 0)
        {
            int offSize = b.Get8(data);
            if (offSize < 1 || offSize > 4)
            {
                return Empty;
            }

            long skipBytes = (long)offSize * count;
            if (skipBytes > b.Size - b.Cursor)
            {
                b.Cursor = b.Size;
                return Empty;
            }

            b.Skip((int)skipBytes);
            uint lastOffset = b.Get(data, offSize);
            if (lastOffset < 1 || lastOffset - 1 > (uint)(b.Size - b.Cursor))
            {
                b.Cursor = b.Size;
                return Empty;
            }

            b.Skip((int)(lastOffset - 1));
        }

        return b.Range(start, b.Cursor - start);
    }

    public static uint ReadCffInt(ReadOnlySpan<byte> data, ref CffBuffer b)
    {
        int b0 = b.Get8(data);
        if (b0 >= 32 && b0 <= 246)
        {
            return unchecked((uint)(b0 - 139));
        }

        if (b0 >= 247 && b0 <= 250)
        {
            return unchecked((uint)((b0 - 247) * 256 + b.Get8(data) + 108));
        }

        if (b0 >= 251 && b0 <= 254)
        {
            return unchecked((uint)(-(b0 - 251) * 256 - b.Get8(data) - 108));
        }

        if (b0 == 28)
        {
            return b.Get16(data);
        }

        if (b0 == 29)
        {
            return b.Get32(data);
        }

        return 0;
    }

    public static void SkipOperand(ReadOnlySpan<byte> data, ref CffBuffer b)
    {
        int b0 = b.Peek8(data);
        if (b0 < 28)
        {
            // Malformed DICT byte: advance to avoid infinite loop.
            b.Skip(1);
            return;
        }

        if (b0 == 30)
        {
            b.Skip(1);
            while (b.Cursor < b.Size)
            {
                int v = b.Get8(data);
                if ((v & 0xF) == 0xF || (v >> 4) == 0xF)
                {
                    break;
                }
            }
        }
        else
        {
            _ = ReadCffInt(data, ref b);
        }
    }

    public static CffBuffer DictGet(ReadOnlySpan<byte> data, CffBuffer b, int key)
    {
        b.Seek(0);
        while (b.Cursor < b.Size)
        {
            int start = b.Cursor;
            while (b.Cursor < b.Size && b.Peek8(data) >= 28)
            {
                SkipOperand(data, ref b);
            }

            int end = b.Cursor;
            if (b.Cursor >= b.Size)
            {
                break;
            }

            int op = b.Get8(data);
            if (op == 12)
            {
                op = b.Get8(data) | 0x100;
            }

            if (op == key)
            {
                return b.Range(start, end - start);
            }
        }

        return Empty;
    }

    public static void DictGetInts(ReadOnlySpan<byte> data, CffBuffer b, int key, Span<uint> output)
    {
        CffBuffer operands = DictGet(data, b, key);
        for (int i = 0; i < output.Length && operands.Cursor < operands.Size; i++)
        {
            output[i] = ReadCffInt(data, ref operands);
        }
    }

    public static int IndexCount(ReadOnlySpan<byte> data, CffBuffer b)
    {
        if (b.Size < 2)
        {
            return 0;
        }

        b.Seek(0);
        return (int)b.Get16(data);
    }

    public static CffBuffer IndexGet(ReadOnlySpan<byte> data, CffBuffer b, int index)
    {
        if (b.Size < 3 || index < 0)
        {
            return Empty;
        }

        b.Seek(0);
        int count = (int)b.Get16(data);
        int offSize = b.Get8(data);
        if (index >= count || offSize < 1 || offSize > 4)
        {
            return Empty;
        }

        long offsetPos = (long)index * offSize;
        if (offsetPos > b.Size - b.Cursor)
        {
            return Empty;
        }

        b.Skip((int)offsetPos);
        uint start = b.Get(data, offSize);
        uint end = b.Get(data, offSize);
        if (end < start)
        {
            return Empty;
        }

        long relOffset = 2L + (long)(count + 1) * offSize + start;
        long len = end - start;
        if (relOffset < 0 || relOffset > b.Size || len > b.Size - relOffset)
        {
            return Empty;
        }

        return b.Range((int)relOffset, (int)len);
    }

    public static CffBuffer GetSubrs(ReadOnlySpan<byte> data, CffBuffer cff, CffBuffer fontDict)
    {
        Span<uint> privateLoc = stackalloc uint[2];
        DictGetInts(data, fontDict, 18, privateLoc);
        if (privateLoc[1] == 0 || privateLoc[0] == 0)
        {
            return Empty;
        }

        if (privateLoc[1] > int.MaxValue || privateLoc[0] > int.MaxValue)
        {
            return Empty;
        }

        CffBuffer pdict = cff.Range((int)privateLoc[1], (int)privateLoc[0]);
        if (pdict.Size == 0)
        {
            return Empty;
        }

        Span<uint> subrsOff = stackalloc uint[1];
        DictGetInts(data, pdict, 19, subrsOff);
        if (subrsOff[0] == 0)
        {
            return Empty;
        }

        long seekPos = (long)privateLoc[1] + subrsOff[0];
        if (seekPos > cff.Size)
        {
            return Empty;
        }

        cff.Seek((int)seekPos);
        return GetIndex(data, ref cff);
    }
}
