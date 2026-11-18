namespace Stb.Managed.TrueType.Internal;

internal static class CffCharstring
{
    private const int MaxCharstringOps = 250_000;
    private const int MaxSubrDepth = 10;
    private const int MaxStackDepth = 48;

    internal struct CharstringContext
    {
        public bool BoundsOnly;
        public bool Started;
        public float FirstX;
        public float FirstY;
        public float X;
        public float Y;
        public int MinX;
        public int MaxX;
        public int MinY;
        public int MaxY;
        public int NumVertices;
        public RasterizerScratch? Scratch;
    }

    private static void TrackVertex(ref CharstringContext c, int x, int y)
    {
        if (x > c.MaxX || !c.Started) c.MaxX = x;
        if (y > c.MaxY || !c.Started) c.MaxY = y;
        if (x < c.MinX || !c.Started) c.MinX = x;
        if (y < c.MinY || !c.Started) c.MinY = y;
        c.Started = true;
    }

    private static void EmitVertex(
        ref CharstringContext c,
        GlyphVertexType type,
        int x,
        int y,
        int cx,
        int cy,
        int cx1,
        int cy1)
    {
        if (c.BoundsOnly)
        {
            TrackVertex(ref c, x, y);
            if (type == GlyphVertexType.Cubic)
            {
                TrackVertex(ref c, cx, cy);
                TrackVertex(ref c, cx1, cy1);
            }
        }
        else if (c.Scratch is not null)
        {
            c.Scratch.AppendVertex(new GlyphVertex(
                unchecked((short)x),
                unchecked((short)y),
                unchecked((short)cx),
                unchecked((short)cy),
                unchecked((short)cx1),
                unchecked((short)cy1),
                type));
        }

        c.NumVertices++;
    }

    private static void CloseShape(ref CharstringContext ctx)
    {
        if (ctx.FirstX != ctx.X || ctx.FirstY != ctx.Y)
        {
            EmitVertex(ref ctx, GlyphVertexType.Line, (int)ctx.FirstX, (int)ctx.FirstY, 0, 0, 0, 0);
        }
    }

    private static void RMoveTo(ref CharstringContext ctx, float dx, float dy)
    {
        CloseShape(ref ctx);
        ctx.FirstX = ctx.X = ctx.X + dx;
        ctx.FirstY = ctx.Y = ctx.Y + dy;
        EmitVertex(ref ctx, GlyphVertexType.Move, (int)ctx.X, (int)ctx.Y, 0, 0, 0, 0);
    }

    private static void RLineTo(ref CharstringContext ctx, float dx, float dy)
    {
        ctx.X += dx;
        ctx.Y += dy;
        EmitVertex(ref ctx, GlyphVertexType.Line, (int)ctx.X, (int)ctx.Y, 0, 0, 0, 0);
    }

    private static void RCurveTo(
        ref CharstringContext ctx,
        float dx1,
        float dy1,
        float dx2,
        float dy2,
        float dx3,
        float dy3)
    {
        float cx1 = ctx.X + dx1;
        float cy1 = ctx.Y + dy1;
        float cx2 = cx1 + dx2;
        float cy2 = cy1 + dy2;
        ctx.X = cx2 + dx3;
        ctx.Y = cy2 + dy3;
        EmitVertex(ref ctx, GlyphVertexType.Cubic, (int)ctx.X, (int)ctx.Y, (int)cx1, (int)cy1, (int)cx2, (int)cy2);
    }

    private static CffBuffer GetSubr(ReadOnlySpan<byte> data, CffBuffer idx, int n)
    {
        int count = CffBuffer.IndexCount(data, idx);
        int bias = 107;
        if (count >= 33900)
        {
            bias = 32768;
        }
        else if (count >= 1240)
        {
            bias = 1131;
        }

        n += bias;
        if (n < 0 || n >= count)
        {
            return CffBuffer.Empty;
        }

        return CffBuffer.IndexGet(data, idx, n);
    }

    private static CffBuffer CidGetGlyphSubrs(
        ReadOnlySpan<byte> data,
        CffBuffer cff,
        CffBuffer fontDicts,
        CffBuffer fdSelect,
        int glyphIndex)
    {
        fdSelect.Seek(0);
        int fmt = fdSelect.Get8(data);
        int fdSelector = -1;
        if (fmt == 0)
        {
            fdSelect.Skip(glyphIndex);
            fdSelector = fdSelect.Get8(data);
        }
        else if (fmt == 3)
        {
            int nranges = (int)fdSelect.Get16(data);
            int start = (int)fdSelect.Get16(data);
            for (int i = 0; i < nranges && fdSelect.Cursor < fdSelect.Size; i++)
            {
                int v = fdSelect.Get8(data);
                int end = (int)fdSelect.Get16(data);
                if (glyphIndex >= start && glyphIndex < end)
                {
                    fdSelector = v;
                    break;
                }

                start = end;
            }
        }

        if (fdSelector < 0)
        {
            return CffBuffer.Empty;
        }

        return CffBuffer.GetSubrs(data, cff, CffBuffer.IndexGet(data, fontDicts, fdSelector));
    }

    internal static bool RunCharstring(
        ReadOnlySpan<byte> data,
        CffBuffer cff,
        CffBuffer charstrings,
        CffBuffer gsubrs,
        CffBuffer defaultSubrs,
        CffBuffer fontDicts,
        CffBuffer fdSelect,
        int glyphIndex,
        ref CharstringContext c)
    {
        bool inHeader = true;
        int maskbits = 0;
        int subrStackHeight = 0;
        int sp = 0;
        bool hasSubrs = false;
        Span<float> s = stackalloc float[MaxStackDepth];
        Span<CffBuffer> subrStack = stackalloc CffBuffer[MaxSubrDepth];
        CffBuffer subrs = defaultSubrs;
        CffBuffer b = CffBuffer.IndexGet(data, charstrings, glyphIndex);
        if (b.Size == 0)
        {
            return false;
        }

        int ops = 0;
        while (b.Cursor < b.Size)
        {
            if (++ops > MaxCharstringOps)
            {
                return false;
            }

            int i = 0;
            bool clearStack = true;
            int b0 = b.Get8(data);
            switch (b0)
            {
                case 0x13: // hintmask
                case 0x14: // cntrmask
                    if (inHeader)
                    {
                        maskbits += sp / 2;
                    }

                    inHeader = false;
                    b.Skip((maskbits + 7) / 8);
                    break;

                case 0x01: // hstem
                case 0x03: // vstem
                case 0x12: // hstemhm
                case 0x17: // vstemhm
                    maskbits += sp / 2;
                    break;

                case 0x15: // rmoveto
                    inHeader = false;
                    if (sp < 2) return false;
                    RMoveTo(ref c, s[sp - 2], s[sp - 1]);
                    break;

                case 0x04: // vmoveto
                    inHeader = false;
                    if (sp < 1) return false;
                    RMoveTo(ref c, 0, s[sp - 1]);
                    break;

                case 0x16: // hmoveto
                    inHeader = false;
                    if (sp < 1) return false;
                    RMoveTo(ref c, s[sp - 1], 0);
                    break;

                case 0x05: // rlineto
                    if (sp < 2) return false;
                    for (; i + 1 < sp; i += 2)
                    {
                        RLineTo(ref c, s[i], s[i + 1]);
                    }

                    break;

                case 0x06: // hlineto
                case 0x07: // vlineto
                {
                    if (sp < 1) return false;
                    bool horiz = b0 == 0x06;
                    for (; i < sp; i++, horiz = !horiz)
                    {
                        if (horiz)
                        {
                            RLineTo(ref c, s[i], 0);
                        }
                        else
                        {
                            RLineTo(ref c, 0, s[i]);
                        }
                    }

                    break;
                }

                case 0x1E: // vhcurveto
                case 0x1F: // hvcurveto
                {
                    if (sp < 4) return false;
                    bool vh = b0 == 0x1E;
                    for (; i + 3 < sp; i += 4, vh = !vh)
                    {
                        if (vh)
                        {
                            RCurveTo(ref c, 0, s[i], s[i + 1], s[i + 2], s[i + 3], (sp - i == 5) ? s[i + 4] : 0.0f);
                        }
                        else
                        {
                            RCurveTo(ref c, s[i], 0, s[i + 1], s[i + 2], (sp - i == 5) ? s[i + 4] : 0.0f, s[i + 3]);
                        }
                    }

                    break;
                }

                case 0x08: // rrcurveto
                    if (sp < 6) return false;
                    for (; i + 5 < sp; i += 6)
                    {
                        RCurveTo(ref c, s[i], s[i + 1], s[i + 2], s[i + 3], s[i + 4], s[i + 5]);
                    }

                    break;

                case 0x18: // rcurveline
                    if (sp < 8) return false;
                    for (; i + 5 < sp - 2; i += 6)
                    {
                        RCurveTo(ref c, s[i], s[i + 1], s[i + 2], s[i + 3], s[i + 4], s[i + 5]);
                    }

                    if (i + 1 >= sp) return false;
                    RLineTo(ref c, s[i], s[i + 1]);
                    break;

                case 0x19: // rlinecurve
                    if (sp < 8) return false;
                    for (; i + 1 < sp - 6; i += 2)
                    {
                        RLineTo(ref c, s[i], s[i + 1]);
                    }

                    if (i + 5 >= sp) return false;
                    RCurveTo(ref c, s[i], s[i + 1], s[i + 2], s[i + 3], s[i + 4], s[i + 5]);
                    break;

                case 0x1A: // vvcurveto
                case 0x1B: // hhcurveto
                    if (sp < 4) return false;
                    {
                        float f = 0.0f;
                        if ((sp & 1) != 0)
                        {
                            f = s[i];
                            i++;
                        }

                        for (; i + 3 < sp; i += 4)
                        {
                            if (b0 == 0x1B)
                            {
                                RCurveTo(ref c, s[i], f, s[i + 1], s[i + 2], s[i + 3], 0.0f);
                            }
                            else
                            {
                                RCurveTo(ref c, f, s[i], s[i + 1], s[i + 2], 0.0f, s[i + 3]);
                            }

                            f = 0.0f;
                        }
                    }

                    break;

                case 0x0A: // callsubr
                    if (!hasSubrs)
                    {
                        if (fdSelect.Size != 0)
                        {
                            subrs = CidGetGlyphSubrs(data, cff, fontDicts, fdSelect, glyphIndex);
                        }

                        hasSubrs = true;
                    }

                    goto case 0x1D;

                case 0x1D: // callgsubr
                    if (sp < 1) return false;
                    {
                        int v = (int)s[--sp];
                        if (subrStackHeight >= MaxSubrDepth) return false;
                        subrStack[subrStackHeight++] = b;
                        b = GetSubr(data, b0 == 0x0A ? subrs : gsubrs, v);
                        if (b.Size == 0) return false;
                        b.Cursor = 0;
                        clearStack = false;
                    }

                    break;

                case 0x0B: // return
                    if (subrStackHeight <= 0) return false;
                    b = subrStack[--subrStackHeight];
                    clearStack = false;
                    break;

                case 0x0E: // endchar
                    CloseShape(ref c);
                    return true;

                case 0x0C: // two-byte escape
                    {
                        int b1 = b.Get8(data);
                        switch (b1)
                        {
                            case 0x22: // hflex
                                if (sp < 7) return false;
                                RCurveTo(ref c, s[0], 0, s[1], s[2], s[3], 0);
                                RCurveTo(ref c, s[4], 0, s[5], -s[2], s[6], 0);
                                break;

                            case 0x23: // flex
                                if (sp < 13) return false;
                                RCurveTo(ref c, s[0], s[1], s[2], s[3], s[4], s[5]);
                                RCurveTo(ref c, s[6], s[7], s[8], s[9], s[10], s[11]);
                                break;

                            case 0x24: // hflex1
                                if (sp < 9) return false;
                                RCurveTo(ref c, s[0], s[1], s[2], s[3], s[4], 0);
                                RCurveTo(ref c, s[5], 0, s[6], s[7], s[8], -(s[1] + s[3] + s[7]));
                                break;

                            case 0x25: // flex1
                                if (sp < 11) return false;
                                {
                                    float dx6 = s[10];
                                    float dy6 = s[10];
                                    float dx = s[0] + s[2] + s[4] + s[6] + s[8];
                                    float dy = s[1] + s[3] + s[5] + s[7] + s[9];
                                    if (MathF.Abs(dx) > MathF.Abs(dy))
                                    {
                                        dy6 = -dy;
                                    }
                                    else
                                    {
                                        dx6 = -dx;
                                    }

                                    RCurveTo(ref c, s[0], s[1], s[2], s[3], s[4], s[5]);
                                    RCurveTo(ref c, s[6], s[7], s[8], s[9], dx6, dy6);
                                }

                                break;

                            default:
                                return false;
                        }
                    }

                    break;

                default:
                    if (b0 != 255 && b0 != 28 && b0 < 32)
                    {
                        return false;
                    }

                    float val;
                    if (b0 == 255)
                    {
                        val = (int)b.Get32(data) / 65536.0f;
                    }
                    else
                    {
                        b.Skip(-1);
                        val = unchecked((short)CffBuffer.ReadCffInt(data, ref b));
                    }

                    if (sp >= MaxStackDepth) return false;
                    s[sp++] = val;
                    clearStack = false;
                    break;
            }

            if (clearStack)
            {
                sp = 0;
            }
        }

        return false;
    }
}
