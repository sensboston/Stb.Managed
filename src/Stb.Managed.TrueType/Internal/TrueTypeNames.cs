using System.Text;

namespace Stb.Managed.TrueType.Internal;

internal static class TrueTypeNames
{
    internal static int CompareUtf8ToUtf16BigEndianPrefix(ReadOnlySpan<byte> s1, ReadOnlySpan<byte> s2)
    {
        int i = 0;
        int len1 = s1.Length;
        int off2 = 0;
        int len2 = s2.Length;

        while (len2 >= 2)
        {
            ushort ch = (ushort)((s2[off2] << 8) | s2[off2 + 1]);
            if (ch < 0x80)
            {
                if (i >= len1 || s1[i++] != ch) return -1;
            }
            else if (ch < 0x800)
            {
                if (i + 1 >= len1) return -1;
                if (s1[i++] != (byte)(0xC0 + (ch >> 6))) return -1;
                if (s1[i++] != (byte)(0x80 + (ch & 0x3F))) return -1;
            }
            else if (ch >= 0xD800 && ch < 0xDC00)
            {
                if (len2 < 4 || i + 3 >= len1) return -1;
                ushort ch2 = (ushort)((s2[off2 + 2] << 8) | s2[off2 + 3]);
                uint c = (uint)(((ch - 0xD800) << 10) + (ch2 - 0xDC00) + 0x10000);
                if (s1[i++] != (byte)(0xF0 + (c >> 18))) return -1;
                if (s1[i++] != (byte)(0x80 + ((c >> 12) & 0x3F))) return -1;
                if (s1[i++] != (byte)(0x80 + ((c >> 6) & 0x3F))) return -1;
                if (s1[i++] != (byte)(0x80 + (c & 0x3F))) return -1;
                off2 += 2;
                len2 -= 2;
            }
            else if (ch >= 0xDC00 && ch < 0xE000)
            {
                return -1;
            }
            else
            {
                if (i + 2 >= len1) return -1;
                if (s1[i++] != (byte)(0xE0 + (ch >> 12))) return -1;
                if (s1[i++] != (byte)(0x80 + ((ch >> 6) & 0x3F))) return -1;
                if (s1[i++] != (byte)(0x80 + (ch & 0x3F))) return -1;
            }

            off2 += 2;
            len2 -= 2;
        }

        return len2 == 0 ? i : -1;
    }

    internal static bool CompareUtf8ToUtf16BigEndian(ReadOnlySpan<byte> utf8, ReadOnlySpan<byte> utf16Be) =>
        utf8.Length == CompareUtf8ToUtf16BigEndianPrefix(utf8, utf16Be);

    internal static bool TryGetFontNameString(
        ReadOnlySpan<byte> data,
        int fontStart,
        int platformId,
        int encodingId,
        int languageId,
        int nameId,
        out ReadOnlySpan<byte> result)
    {
        result = default;
        int nm = TrueTypeBinary.FindTable(data, fontStart, TrueTypeBinary.TagName);
        if (nm <= 0 || !TrueTypeBinary.HasRange(data, nm, 6))
        {
            return false;
        }

        int count = TrueTypeBinary.ReadU16(data, nm + 2);
        int stringOffset = nm + TrueTypeBinary.ReadU16(data, nm + 4);
        if (!TrueTypeBinary.HasRange(data, nm + 6, count * 12))
        {
            return false;
        }

        for (int i = 0; i < count; ++i)
        {
            int loc = nm + 6 + 12 * i;
            if (platformId == TrueTypeBinary.ReadU16(data, loc) &&
                encodingId == TrueTypeBinary.ReadU16(data, loc + 2) &&
                languageId == TrueTypeBinary.ReadU16(data, loc + 4) &&
                nameId == TrueTypeBinary.ReadU16(data, loc + 6))
            {
                int length = TrueTypeBinary.ReadU16(data, loc + 8);
                int off = stringOffset + TrueTypeBinary.ReadU16(data, loc + 10);
                if (!TrueTypeBinary.HasRange(data, off, length))
                {
                    return false;
                }

                result = data.Slice(off, length);
                return true;
            }
        }

        return false;
    }

    internal static bool TryGetFontNameDefault(ReadOnlySpan<byte> data, int fontStart, out string name)
    {
        name = string.Empty;
        int nm = TrueTypeBinary.FindTable(data, fontStart, TrueTypeBinary.TagName);
        if (nm <= 0 || !TrueTypeBinary.HasRange(data, nm, 6))
        {
            return false;
        }

        int count = TrueTypeBinary.ReadU16(data, nm + 2);
        int stringOffset = nm + TrueTypeBinary.ReadU16(data, nm + 4);
        if (!TrueTypeBinary.HasRange(data, nm + 6, count * 12))
        {
            return false;
        }

        for (int i = 0; i < count; ++i)
        {
            int loc = nm + 6 + 12 * i;
            if (TrueTypeBinary.ReadU16(data, loc + 6) != 4)
            {
                continue;
            }

            ushort platformId = TrueTypeBinary.ReadU16(data, loc);
            ushort encodingId = TrueTypeBinary.ReadU16(data, loc + 2);
            ushort languageId = TrueTypeBinary.ReadU16(data, loc + 4);
            bool isMacEnglish = platformId == (ushort)FontPlatformId.Mac && languageId == (ushort)FontMacLanguageId.English;
            bool isMsEnglish = platformId == (ushort)FontPlatformId.Microsoft && languageId == (ushort)FontMicrosoftLanguageId.English;
            if (!isMacEnglish && !isMsEnglish)
            {
                continue;
            }

            int length = TrueTypeBinary.ReadU16(data, loc + 8);
            int off = stringOffset + TrueTypeBinary.ReadU16(data, loc + 10);
            if (!TrueTypeBinary.HasRange(data, off, length))
            {
                continue;
            }

            ReadOnlySpan<byte> slice = data.Slice(off, length);
            if (platformId == (ushort)FontPlatformId.Unicode ||
                (platformId == (ushort)FontPlatformId.Microsoft && (encodingId == 1 || encodingId == 10)))
            {
                if (!TryDecodeUtf16Be(slice, out name))
                {
                    return false;
                }

                return true;
            }

            name = Encoding.UTF8.GetString(slice);
            return true;
        }

        return false;
    }

    private static bool TryDecodeUtf16Be(ReadOnlySpan<byte> src, out string text)
    {
        if ((src.Length & 1) != 0)
        {
            text = string.Empty;
            return false;
        }

        int off = 0;
        var sb = new StringBuilder(src.Length / 2);
        while (off < src.Length)
        {
            ushort ch = (ushort)((src[off] << 8) | src[off + 1]);
            off += 2;
            if (ch >= 0xD800 && ch < 0xDC00)
            {
                if (off + 2 > src.Length)
                {
                    text = string.Empty;
                    return false;
                }

                ushort ch2 = (ushort)((src[off] << 8) | src[off + 1]);
                off += 2;
                sb.Append((char)ch);
                sb.Append((char)ch2);
            }
            else if (ch >= 0xDC00 && ch < 0xE000)
            {
                text = string.Empty;
                return false;
            }
            else
            {
                sb.Append((char)ch);
            }
        }

        text = sb.ToString();
        return true;
    }

    private static bool MatchPair(
        ReadOnlySpan<byte> data,
        int nm,
        ReadOnlySpan<byte> nameUtf8,
        int targetId,
        int nextId)
    {
        if (!TrueTypeBinary.HasRange(data, nm, 6))
        {
            return false;
        }

        int count = TrueTypeBinary.ReadU16(data, nm + 2);
        int stringOffset = nm + TrueTypeBinary.ReadU16(data, nm + 4);
        if (!TrueTypeBinary.HasRange(data, nm + 6, count * 12))
        {
            return false;
        }

        for (int i = 0; i < count; ++i)
        {
            int loc = nm + 6 + 12 * i;
            int id = TrueTypeBinary.ReadU16(data, loc + 6);
            if (id != targetId)
            {
                continue;
            }

            int platform = TrueTypeBinary.ReadU16(data, loc);
            int encoding = TrueTypeBinary.ReadU16(data, loc + 2);
            int language = TrueTypeBinary.ReadU16(data, loc + 4);

            if (platform == 0 || (platform == 3 && (encoding == 1 || encoding == 10)))
            {
                int slen = TrueTypeBinary.ReadU16(data, loc + 8);
                int off = stringOffset + TrueTypeBinary.ReadU16(data, loc + 10);
                if (!TrueTypeBinary.HasRange(data, off, slen))
                {
                    continue;
                }

                int matchLen = CompareUtf8ToUtf16BigEndianPrefix(nameUtf8, data.Slice(off, slen));
                if (matchLen >= 0)
                {
                    if (i + 1 < count &&
                        TrueTypeBinary.ReadU16(data, loc + 12 + 6) == nextId &&
                        TrueTypeBinary.ReadU16(data, loc + 12) == platform &&
                        TrueTypeBinary.ReadU16(data, loc + 12 + 2) == encoding &&
                        TrueTypeBinary.ReadU16(data, loc + 12 + 4) == language)
                    {
                        int slen2 = TrueTypeBinary.ReadU16(data, loc + 12 + 8);
                        int off2 = stringOffset + TrueTypeBinary.ReadU16(data, loc + 12 + 10);
                        if (!TrueTypeBinary.HasRange(data, off2, slen2))
                        {
                            continue;
                        }

                        if (slen2 == 0)
                        {
                            if (matchLen == nameUtf8.Length)
                            {
                                return true;
                            }
                        }
                        else if (matchLen < nameUtf8.Length && nameUtf8[matchLen] == (byte)' ')
                        {
                            matchLen++;
                            if (CompareUtf8ToUtf16BigEndian(nameUtf8.Slice(matchLen), data.Slice(off2, slen2)))
                            {
                                return true;
                            }
                        }
                    }
                    else if (matchLen == nameUtf8.Length)
                    {
                        return true;
                    }
                }
            }
        }

        return false;
    }

    private static bool Matches(ReadOnlySpan<byte> data, int offset, ReadOnlySpan<byte> nameUtf8, int flags)
    {
        if (!TrueTypeBinary.IsFont(data, offset))
        {
            return false;
        }

        if (flags != 0)
        {
            int hd = TrueTypeBinary.FindTable(data, offset, TrueTypeBinary.TagHead);
            if (hd <= 0 || !TrueTypeBinary.HasRange(data, hd, 46))
            {
                return false;
            }

            if ((TrueTypeBinary.ReadU16(data, hd + 44) & 7) != (flags & 7))
            {
                return false;
            }
        }

        int nm = TrueTypeBinary.FindTable(data, offset, TrueTypeBinary.TagName);
        if (nm <= 0)
        {
            return false;
        }

        if (flags != 0)
        {
            if (MatchPair(data, nm, nameUtf8, 16, -1)) return true;
            if (MatchPair(data, nm, nameUtf8, 1, -1)) return true;
            if (MatchPair(data, nm, nameUtf8, 3, -1)) return true;
        }
        else
        {
            if (MatchPair(data, nm, nameUtf8, 16, 17)) return true;
            if (MatchPair(data, nm, nameUtf8, 1, 2)) return true;
            if (MatchPair(data, nm, nameUtf8, 3, -1)) return true;
        }

        return false;
    }

    internal static int FindMatchingFont(ReadOnlySpan<byte> fontData, string name, FontMacStyle flags)
    {
        byte[] nameUtf8 = Encoding.UTF8.GetBytes(name);
        int flagBits = (int)flags;
        for (int i = 0; ; ++i)
        {
            int off = TrueTypeBinary.GetFontOffsetForIndex(fontData, i);
            if (off < 0)
            {
                return off;
            }

            if (Matches(fontData, off, nameUtf8, flagBits))
            {
                return off;
            }
        }
    }
}
