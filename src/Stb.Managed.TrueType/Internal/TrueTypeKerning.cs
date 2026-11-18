namespace Stb.Managed.TrueType.Internal;

internal static class TrueTypeKerning
{
    internal static int GetKerningTableLength(ReadOnlySpan<byte> data, int kernOffset)
    {
        if (kernOffset <= 0 || !TrueTypeBinary.HasRange(data, kernOffset, 12))
        {
            return 0;
        }

        if (TrueTypeBinary.ReadU16(data, kernOffset + 2) < 1)
        {
            return 0;
        }

        if (TrueTypeBinary.ReadU16(data, kernOffset + 8) != 1)
        {
            return 0;
        }

        int nPairs = TrueTypeBinary.ReadU16(data, kernOffset + 10);
        int maxPairs = Math.Max(0, (data.Length - (kernOffset + 18)) / 6);
        return Math.Min(nPairs, maxPairs);
    }

    internal static int GetKerningTable(ReadOnlySpan<byte> data, int kernOffset, Span<KerningEntry> destination)
    {
        int length = GetKerningTableLength(data, kernOffset);
        if (length <= 0)
        {
            return 0;
        }

        if (destination.Length < length)
        {
            length = destination.Length;
        }

        int baseOff = kernOffset + 18;
        for (int k = 0; k < length; k++)
        {
            int pos = baseOff + k * 6;
            destination[k] = new KerningEntry(
                TrueTypeBinary.ReadU16(data, pos),
                TrueTypeBinary.ReadU16(data, pos + 2),
                TrueTypeBinary.ReadS16(data, pos + 4));
        }

        return length;
    }

    internal static int GetGlyphKernInfoAdvance(ReadOnlySpan<byte> data, int kernOffset, int glyph1, int glyph2)
    {
        int nPairs = GetKerningTableLength(data, kernOffset);
        if (nPairs <= 0 || (uint)glyph1 > 0xFFFFu || (uint)glyph2 > 0xFFFFu)
        {
            return 0;
        }

        int l = 0;
        int r = nPairs - 1;
        uint needle = ((uint)glyph1 << 16) | (uint)glyph2;
        int baseOff = kernOffset + 18;

        while (l <= r)
        {
            int m = (l + r) >> 1;
            int pos = baseOff + m * 6;
            uint straw = TrueTypeBinary.ReadU32(data, pos);
            if (needle < straw)
            {
                r = m - 1;
            }
            else if (needle > straw)
            {
                l = m + 1;
            }
            else
            {
                return TrueTypeBinary.ReadS16(data, pos + 4);
            }
        }

        return 0;
    }

    private static int GetCoverageIndex(ReadOnlySpan<byte> data, int coverageOffset, int glyph)
    {
        if (!TrueTypeBinary.HasRange(data, coverageOffset, 4))
        {
            return -1;
        }

        ushort format = TrueTypeBinary.ReadU16(data, coverageOffset);
        switch (format)
        {
            case 1:
                {
                    ushort glyphCount = TrueTypeBinary.ReadU16(data, coverageOffset + 2);
                    int glyphArray = coverageOffset + 4;
                    if (!TrueTypeBinary.HasRange(data, glyphArray, glyphCount * 2))
                    {
                        return -1;
                    }

                    int l = 0;
                    int r = glyphCount - 1;
                    while (l <= r)
                    {
                        int m = (l + r) >> 1;
                        int straw = TrueTypeBinary.ReadU16(data, glyphArray + 2 * m);
                        if (glyph < straw)
                        {
                            r = m - 1;
                        }
                        else if (glyph > straw)
                        {
                            l = m + 1;
                        }
                        else
                        {
                            return m;
                        }
                    }

                    return -1;
                }

            case 2:
                {
                    ushort rangeCount = TrueTypeBinary.ReadU16(data, coverageOffset + 2);
                    int rangeArray = coverageOffset + 4;
                    if (!TrueTypeBinary.HasRange(data, rangeArray, rangeCount * 6))
                    {
                        return -1;
                    }

                    int l = 0;
                    int r = rangeCount - 1;
                    while (l <= r)
                    {
                        int m = (l + r) >> 1;
                        int rec = rangeArray + 6 * m;
                        int strawStart = TrueTypeBinary.ReadU16(data, rec);
                        int strawEnd = TrueTypeBinary.ReadU16(data, rec + 2);
                        if (glyph < strawStart)
                        {
                            r = m - 1;
                        }
                        else if (glyph > strawEnd)
                        {
                            l = m + 1;
                        }
                        else
                        {
                            int startCoverageIndex = TrueTypeBinary.ReadU16(data, rec + 4);
                            return startCoverageIndex + glyph - strawStart;
                        }
                    }

                    return -1;
                }

            default:
                return -1;
        }
    }

    private static int GetGlyphClass(ReadOnlySpan<byte> data, int classDefOffset, int glyph)
    {
        if (!TrueTypeBinary.HasRange(data, classDefOffset, 4))
        {
            return -1;
        }

        ushort format = TrueTypeBinary.ReadU16(data, classDefOffset);
        switch (format)
        {
            case 1:
                {
                    if (!TrueTypeBinary.HasRange(data, classDefOffset, 6))
                    {
                        return -1;
                    }

                    int startGlyphId = TrueTypeBinary.ReadU16(data, classDefOffset + 2);
                    int glyphCount = TrueTypeBinary.ReadU16(data, classDefOffset + 4);
                    int valuesOff = classDefOffset + 6;
                    if (!TrueTypeBinary.HasRange(data, valuesOff, glyphCount * 2))
                    {
                        return -1;
                    }

                    if (glyph >= startGlyphId && glyph < startGlyphId + glyphCount)
                    {
                        return TrueTypeBinary.ReadU16(data, valuesOff + 2 * (glyph - startGlyphId));
                    }

                    return 0;
                }

            case 2:
                {
                    int rangeCount = TrueTypeBinary.ReadU16(data, classDefOffset + 2);
                    int rangesOff = classDefOffset + 4;
                    if (!TrueTypeBinary.HasRange(data, rangesOff, rangeCount * 6))
                    {
                        return -1;
                    }

                    int l = 0;
                    int r = rangeCount - 1;
                    while (l <= r)
                    {
                        int m = (l + r) >> 1;
                        int rec = rangesOff + 6 * m;
                        int strawStart = TrueTypeBinary.ReadU16(data, rec);
                        int strawEnd = TrueTypeBinary.ReadU16(data, rec + 2);
                        if (glyph < strawStart)
                        {
                            r = m - 1;
                        }
                        else if (glyph > strawEnd)
                        {
                            l = m + 1;
                        }
                        else
                        {
                            return TrueTypeBinary.ReadU16(data, rec + 4);
                        }
                    }

                    return 0;
                }

            default:
                return -1;
        }
    }

    internal static int GetGlyphGposInfoAdvance(ReadOnlySpan<byte> data, int gposOffset, int glyph1, int glyph2)
    {
        if (gposOffset <= 0 || !TrueTypeBinary.HasRange(data, gposOffset, 10))
        {
            return 0;
        }

        if (TrueTypeBinary.ReadU16(data, gposOffset) != 1)
        {
            return 0;
        }

        if (TrueTypeBinary.ReadU16(data, gposOffset + 2) > 1)
        {
            return 0;
        }

        int lookupListOffset = gposOffset + TrueTypeBinary.ReadU16(data, gposOffset + 8);
        if (!TrueTypeBinary.HasRange(data, lookupListOffset, 2))
        {
            return 0;
        }

        int lookupCount = TrueTypeBinary.ReadU16(data, lookupListOffset);
        if (!TrueTypeBinary.HasRange(data, lookupListOffset + 2, lookupCount * 2))
        {
            return 0;
        }

        for (int i = 0; i < lookupCount; ++i)
        {
            int lookupTable = lookupListOffset + TrueTypeBinary.ReadU16(data, lookupListOffset + 2 + 2 * i);
            if (!TrueTypeBinary.HasRange(data, lookupTable, 6))
            {
                continue;
            }

            ushort lookupType = TrueTypeBinary.ReadU16(data, lookupTable);
            ushort subTableCount = TrueTypeBinary.ReadU16(data, lookupTable + 4);
            int subTableOffsets = lookupTable + 6;
            if (lookupType != 2 && lookupType != 9)
            {
                continue;
            }

            if (!TrueTypeBinary.HasRange(data, subTableOffsets, subTableCount * 2))
            {
                continue;
            }

            for (int sti = 0; sti < subTableCount; sti++)
            {
                int table = lookupTable + TrueTypeBinary.ReadU16(data, subTableOffsets + 2 * sti);
                if (lookupType == 9)
                {
                    if (!TrueTypeBinary.HasRange(data, table, 8))
                    {
                        continue;
                    }

                    if (TrueTypeBinary.ReadU16(data, table) != 1 || TrueTypeBinary.ReadU16(data, table + 2) != 2)
                    {
                        continue;
                    }

                    uint exOffset = TrueTypeBinary.ReadU32(data, table + 4);
                    long nextTable = (long)table + exOffset;
                    if (nextTable > data.Length)
                    {
                        continue;
                    }

                    table = (int)nextTable;
                }

                if (!TrueTypeBinary.HasRange(data, table, 8))
                {
                    continue;
                }

                ushort posFormat = TrueTypeBinary.ReadU16(data, table);
                ushort coverageOffset = TrueTypeBinary.ReadU16(data, table + 2);
                int coverageIndex = GetCoverageIndex(data, table + coverageOffset, glyph1);
                if (coverageIndex == -1)
                {
                    continue;
                }

                switch (posFormat)
                {
                    case 1:
                        {
                            ushort valueFormat1 = TrueTypeBinary.ReadU16(data, table + 4);
                            ushort valueFormat2 = TrueTypeBinary.ReadU16(data, table + 6);
                            if ((valueFormat1 == 4 || valueFormat1 == 68) && valueFormat2 == 0)
                            {
                                if (!TrueTypeBinary.HasRange(data, table, 10))
                                {
                                    return 0;
                                }

                                int valueRecordPairSize = valueFormat1 == 68 ? 4 : 2;
                                ushort pairSetCount = TrueTypeBinary.ReadU16(data, table + 8);
                                if (coverageIndex >= pairSetCount)
                                {
                                    return 0;
                                }

                                if (!TrueTypeBinary.HasRange(data, table + 10 + 2 * coverageIndex, 2))
                                {
                                    return 0;
                                }

                                int pairValueTable = table + TrueTypeBinary.ReadU16(data, table + 10 + 2 * coverageIndex);
                                if (!TrueTypeBinary.HasRange(data, pairValueTable, 2))
                                {
                                    return 0;
                                }

                                ushort pairValueCount = TrueTypeBinary.ReadU16(data, pairValueTable);
                                int pairValueArray = pairValueTable + 2;
                                int entryStride = 2 + valueRecordPairSize;
                                if (!TrueTypeBinary.HasRange(data, pairValueArray, pairValueCount * entryStride))
                                {
                                    return 0;
                                }

                                int l = 0;
                                int r = pairValueCount - 1;
                                while (l <= r)
                                {
                                    int m = (l + r) >> 1;
                                    int pairValue = pairValueArray + entryStride * m;
                                    int secondGlyph = TrueTypeBinary.ReadU16(data, pairValue);
                                    if (glyph2 < secondGlyph)
                                    {
                                        r = m - 1;
                                    }
                                    else if (glyph2 > secondGlyph)
                                    {
                                        l = m + 1;
                                    }
                                    else
                                    {
                                        return TrueTypeBinary.ReadS16(data, pairValue + 2);
                                    }
                                }
                            }
                            else
                            {
                                return 0;
                            }

                            break;
                        }

                    case 2:
                        {
                            ushort valueFormat1 = TrueTypeBinary.ReadU16(data, table + 4);
                            ushort valueFormat2 = TrueTypeBinary.ReadU16(data, table + 6);
                            if ((valueFormat1 == 4 || valueFormat1 == 68) && valueFormat2 == 0)
                            {
                                if (!TrueTypeBinary.HasRange(data, table, 16))
                                {
                                    return 0;
                                }

                                ushort classDef1Offset = TrueTypeBinary.ReadU16(data, table + 8);
                                ushort classDef2Offset = TrueTypeBinary.ReadU16(data, table + 10);
                                int glyph1Class = GetGlyphClass(data, table + classDef1Offset, glyph1);
                                int glyph2Class = GetGlyphClass(data, table + classDef2Offset, glyph2);

                                ushort class1Count = TrueTypeBinary.ReadU16(data, table + 12);
                                ushort class2Count = TrueTypeBinary.ReadU16(data, table + 14);
                                if (glyph1Class < 0 || glyph1Class >= class1Count)
                                {
                                    return 0;
                                }

                                if (glyph2Class < 0 || glyph2Class >= class2Count)
                                {
                                    return 0;
                                }

                                int valueRecordPairSize = valueFormat1 == 68 ? 4 : 2;
                                long recordOff = (long)table + 16 + (long)valueRecordPairSize * ((long)glyph1Class * class2Count + glyph2Class);
                                if (recordOff + 2 > data.Length)
                                {
                                    return 0;
                                }

                                return TrueTypeBinary.ReadS16(data, (int)recordOff);
                            }

                            return 0;
                        }

                    default:
                        return 0;
                }
            }
        }

        return 0;
    }
}
