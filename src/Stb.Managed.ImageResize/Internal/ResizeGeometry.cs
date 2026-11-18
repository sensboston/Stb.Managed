using System.Runtime.CompilerServices;

namespace Stb.Managed.Internal;

internal enum InternalPixelLayout
{
    OneChannel = 0,
    TwoChannel = 1,
    Rgb = 2,
    Bgr = 3,
    FourChannel = 4,

    Rgba = 5,
    Bgra = 6,
    Argb = 7,
    Abgr = 8,
    Ra = 9,
    Ar = 10,

    RgbaPm = 11,
    BgraPm = 12,
    ArgbPm = 13,
    AbgrPm = 14,
    RaPm = 15,
    ArPm = 16,
}

internal enum AlphaWeightingKind
{
    None = 0,
    WeightInOnly = 1,
    FancyBoth = 2,
    UnweightOutOnly = 3,
    SimpleBoth = 4,
}

internal enum ChannelSwizzle
{
    Identity = 0,
    Bgra = 1,
    Argb = 2,
    Abgr = 3,
    Ar = 4,
}

internal struct ResizeScaleInfo
{
    public int InputFullSize;
    public int OutputSubSize;
    public double Scale;
    public double InvScale;
    public double PixelShift;
    public bool ScaleIsRational;
    public uint ScaleNumerator;
    public uint ScaleDenominator;
    public int ApproximatedDrift;
    public bool PositionsExact;
    public int ShiftPixels;
    public double TapScaleIn;
    public double TapScaleOut;

    public readonly bool IsUpsample => Scale >= 1.0;

    public readonly bool IsCopy => PositionsExact && ScaleNumerator == ScaleDenominator;
}

internal static class ResizeGeometry
{
    private const ulong PolyphaseDriftDiv = 8192;

    private static readonly InternalPixelLayout[] PublicToInternalLayout =
    [
        InternalPixelLayout.Bgr,       // 0: Bgr
        InternalPixelLayout.OneChannel,// 1: OneChannel
        InternalPixelLayout.TwoChannel,// 2: TwoChannel
        InternalPixelLayout.Rgb,       // 3: Rgb
        InternalPixelLayout.Rgba,      // 4: Rgba
        InternalPixelLayout.FourChannel,// 5: FourChannel
        InternalPixelLayout.Bgra,      // 6: Bgra
        InternalPixelLayout.Argb,      // 7: Argb
        InternalPixelLayout.Abgr,      // 8: Abgr
        InternalPixelLayout.Ra,        // 9: Ra
        InternalPixelLayout.Ar,        // 10: Ar
        InternalPixelLayout.RgbaPm,    // 11: RgbaPremultiplied
        InternalPixelLayout.BgraPm,    // 12: BgraPremultiplied
        InternalPixelLayout.ArgbPm,    // 13: ArgbPremultiplied
        InternalPixelLayout.AbgrPm,    // 14: AbgrPremultiplied
        InternalPixelLayout.RaPm,      // 15: RaPremultiplied
        InternalPixelLayout.ArPm,      // 16: ArPremultiplied
    ];

    private static readonly int[] InternalChannels =
    [
        1, 2, 3, 3, 4,
        4, 4, 4, 4, 2, 2,
        4, 4, 4, 4, 2, 2,
    ];

    // [1, 2, 3, 4, 7 channels][9 buckets] from stb_image_resize2.h v2.50.
    private static readonly float[,] HorizontalCost =
    {
        { 12.472f, 6.718f, 6.656f, 8.008f, 5.957f, 6.068f, 6.474f, 7.646f, 10.184f },
        {  8.067f, 4.192f, 5.091f, 4.906f, 4.816f, 5.584f, 5.741f, 6.971f,  6.182f },
        {  5.495f, 3.718f, 3.206f, 7.861f, 4.816f, 5.584f, 5.741f, 5.584f,  6.068f },
        {  3.651f, 2.135f, 2.791f, 3.583f, 2.950f, 3.583f, 4.154f, 4.192f,  4.861f },
        {  2.434f, 2.001f, 2.521f, 3.583f, 2.817f, 3.236f, 3.550f, 3.822f,  4.271f },
    };

    private static readonly float[,] HorizontalCostPeriod1 =
    {
        { 12.406f, 6.718f, 6.534f, 5.186f, 5.114f, 5.138f, 6.656f, 5.431f, 10.374f },
        {  8.001f, 4.154f, 3.583f, 4.003f, 3.276f, 4.473f, 3.683f, 4.231f,  6.013f },
        {  5.024f, 3.033f, 3.583f, 3.683f, 3.268f, 4.432f, 3.650f, 4.231f,  5.283f },
        {  2.976f, 2.076f, 2.665f, 2.452f, 2.354f, 2.766f, 2.715f, 3.327f,  4.684f },
        {  2.184f, 1.928f, 2.342f, 1.983f, 2.233f, 3.005f, 2.715f, 3.421f,  3.858f },
    };

    internal static InternalPixelLayout ToInternalLayout(ResizePixelLayout layout) =>
        PublicToInternalLayout[(int)layout];

    internal static int GetChannelCount(ResizePixelLayout layout) =>
        InternalChannels[(int)PublicToInternalLayout[(int)layout]];

    internal static int GetChannelCount(InternalPixelLayout layout) =>
        InternalChannels[(int)layout];

    internal static int GetDataTypeSize(ResizeDataType dataType) => dataType switch
    {
        ResizeDataType.UInt8Linear => 1,
        ResizeDataType.UInt8Srgb => 1,
        ResizeDataType.UInt8SrgbAlpha => 1,
        ResizeDataType.UInt16Linear => 2,
        ResizeDataType.Fp32 => 4,
        ResizeDataType.Fp16 => 2,
        _ => 1,
    };

    internal static bool HasAlphaChannel(InternalPixelLayout layout) =>
        layout >= InternalPixelLayout.Rgba;

    internal static bool IsNonPremultipliedAlpha(InternalPixelLayout layout) =>
        layout >= InternalPixelLayout.Rgba && layout <= InternalPixelLayout.Ar;

    internal static bool IsPremultipliedAlpha(InternalPixelLayout layout) =>
        layout >= InternalPixelLayout.RgbaPm && layout <= InternalPixelLayout.ArPm;

    internal static ChannelSwizzle GetSwizzle(InternalPixelLayout layout) => layout switch
    {
        InternalPixelLayout.Bgra or InternalPixelLayout.BgraPm => ChannelSwizzle.Bgra,
        InternalPixelLayout.Argb or InternalPixelLayout.ArgbPm => ChannelSwizzle.Argb,
        InternalPixelLayout.Abgr or InternalPixelLayout.AbgrPm => ChannelSwizzle.Abgr,
        InternalPixelLayout.Ar or InternalPixelLayout.ArPm => ChannelSwizzle.Ar,
        _ => ChannelSwizzle.Identity,
    };

    internal static bool NeedsThreeChannelFlip(InternalPixelLayout inputLayout, InternalPixelLayout outputLayout) =>
        (inputLayout == InternalPixelLayout.Rgb && outputLayout == InternalPixelLayout.Bgr) ||
        (inputLayout == InternalPixelLayout.Bgr && outputLayout == InternalPixelLayout.Rgb);

    internal static AlphaWeightingKind GetAlphaWeighting(
        ResizeFilter resolvedHorizontalFilter,
        ResizeFilter resolvedVerticalFilter,
        bool hasCustomHorizontalFilter,
        bool hasCustomVerticalFilter,
        InternalPixelLayout inputLayout,
        InternalPixelLayout outputLayout,
        bool fastAlpha,
        int channels,
        out int effectiveChannels)
    {
        effectiveChannels = channels;
        bool inputHasAlpha = IsNonPremultipliedAlpha(inputLayout);
        bool inputPm = IsPremultipliedAlpha(inputLayout);
        bool outputHasAlpha = IsNonPremultipliedAlpha(outputLayout);
        bool outputPm = IsPremultipliedAlpha(outputLayout);

        bool pointSampled = !hasCustomHorizontalFilter
            && !hasCustomVerticalFilter
            && resolvedHorizontalFilter == ResizeFilter.PointSample
            && resolvedVerticalFilter == ResizeFilter.PointSample;

        if (inputHasAlpha && outputHasAlpha)
        {
            if (pointSampled)
            {
                return AlphaWeightingKind.None;
            }

            if (fastAlpha)
            {
                return AlphaWeightingKind.SimpleBoth;
            }

            effectiveChannels = channels == 4 ? ResizeCoders.FancyStride : 3;
            return AlphaWeightingKind.FancyBoth;
        }

        if (inputPm && outputHasAlpha)
        {
            return AlphaWeightingKind.UnweightOutOnly;
        }

        if (inputHasAlpha && outputPm)
        {
            return AlphaWeightingKind.WeightInOnly;
        }

        return AlphaWeightingKind.None;
    }

    internal static ResizePixelLayout FromColorComponents(ColorComponents components) => components switch
    {
        ColorComponents.Grey => ResizePixelLayout.OneChannel,
        ColorComponents.GreyAlpha => ResizePixelLayout.Ra,
        ColorComponents.RedGreenBlue => ResizePixelLayout.Rgb,
        ColorComponents.RedGreenBlueAlpha => ResizePixelLayout.Rgba,
        _ => throw new StbException(
            StbErrorCode.InvalidArgument,
            $"Unsupported ColorComponents value for resizing: {components}."),
    };

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static int FloorToInt(double x)
    {
        int i = (int)x;
        return i > x ? i - 1 : i;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static int CeilToInt(double x)
    {
        int i = (int)x;
        return i < x ? i + 1 : i;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static int EdgeWrap(ResizeEdge edge, int n, int max)
    {
        if ((uint)n < (uint)max)
        {
            return n;
        }

        return EdgeWrapSlow(edge, n, max);
    }

    internal static int EdgeWrapSlow(ResizeEdge edge, int n, int max)
    {
        switch (edge)
        {
            case ResizeEdge.Clamp:
                if (n < 0)
                {
                    return 0;
                }

                if (n >= max)
                {
                    return max - 1;
                }

                return n;

            case ResizeEdge.Reflect:
                if (n < 0)
                {
                    return n > -max ? -n : max - 1;
                }

                if (n >= max)
                {
                    return (n - max) >= max ? 0 : (max - 1) - (n - max);
                }

                return n;

            case ResizeEdge.Wrap:
                if (n >= 0)
                {
                    return n % max;
                }

                int m = (-n) % max;
                return m != 0 ? max - m : 0;

            case ResizeEdge.Zero:
            default:
                return 0;
        }
    }

    internal static bool CalculateRegionTransform(
        out ResizeScaleInfo scaleInfo,
        int outputFullRange,
        ref int outputOffset,
        int outputSubRange,
        int inputFullRange,
        double inputS0,
        double inputS1,
        int sN0,
        int sN1,
        int sD)
    {
        scaleInfo = default;
        double inputS = inputS1 - inputS0;
        double unclippedInputS = inputS;
        int unclippedOutputSubRange = outputSubRange;
        int unclippedOutputOffset = outputOffset;

        if (outputFullRange <= 0 || inputFullRange <= 0 || outputSubRange <= 0 || !(inputS > 0.0))
        {
            return false;
        }

        if (outputOffset >= outputFullRange || (outputOffset + outputSubRange) <= 0 || inputS0 >= 1.0 || inputS1 <= 0.0)
        {
            return false;
        }

        double outputRange = outputFullRange;
        double inputRange = inputFullRange;
        double outputS = outputSubRange / outputRange;
        double ratio = outputS / inputS;
        double inputPixels = inputRange * inputS;

        scaleInfo.Scale = outputSubRange / inputPixels;
        scaleInfo.InvScale = inputPixels / outputSubRange;

        Clip(ref outputOffset, ref outputSubRange, outputFullRange, ref inputS0, ref inputS1);
        ClipInput(ref outputOffset, ref outputSubRange, ref inputS0, ref inputS1);

        inputS = inputS1 - inputS0;
        if (!(inputS > 0.0) || outputSubRange <= 0)
        {
            return false;
        }

        scaleInfo.PixelShift = inputS0 * ratio * outputRange;

        scaleInfo.ScaleIsRational = ScaleToRational(
            unclippedOutputSubRange,
            inputFullRange,
            unclippedInputS,
            sN0,
            sN1,
            sD,
            out scaleInfo.ScaleNumerator,
            out scaleInfo.ScaleDenominator,
            out scaleInfo.ApproximatedDrift);

        if (scaleInfo.ScaleIsRational && scaleInfo.ApproximatedDrift == 0)
        {
            scaleInfo.Scale = (double)scaleInfo.ScaleNumerator / scaleInfo.ScaleDenominator;
            scaleInfo.InvScale = (double)scaleInfo.ScaleDenominator / scaleInfo.ScaleNumerator;
        }

        scaleInfo.PositionsExact = false;
        scaleInfo.ShiftPixels = 0;

        if (scaleInfo.ScaleIsRational
            && scaleInfo.ApproximatedDrift == 0
            && sD > 0
            && (sN1 - sN0) != 0
            && (((long)sN0 * unclippedOutputSubRange) % (sN1 - sN0)) == 0
            && scaleInfo.ScaleNumerator < (1u << 30)
            && scaleInfo.ScaleDenominator < (1u << 30))
        {
            long shift = (((long)sN0 * unclippedOutputSubRange) / (sN1 - sN0)) + (outputOffset - unclippedOutputOffset);
            if (shift > -(1L << 30) && shift < (1L << 30))
            {
                scaleInfo.ShiftPixels = (int)shift;
                scaleInfo.TapScaleIn = 1.0 / (2.0 * scaleInfo.ScaleNumerator);
                scaleInfo.TapScaleOut = 1.0 / (2.0 * scaleInfo.ScaleDenominator);
                scaleInfo.PixelShift = shift;
                scaleInfo.PositionsExact = true;
            }
        }

        scaleInfo.InputFullSize = inputFullRange;
        scaleInfo.OutputSubSize = outputSubRange;
        return true;
    }

    internal static bool ShouldDoVerticalFirst(
        int effectiveChannels,
        int horizontalFilterPixelWidth,
        int horizontalInputSize,
        int horizontalOutputSize,
        ResizeEdge horizontalEdge,
        bool horizontalIsPeriod1,
        int verticalFilterPixelWidth,
        int verticalInputSize,
        int verticalOutputSize,
        ResizeEdge verticalEdge)
    {
        int channelIndex = effectiveChannels switch
        {
            1 => 0,
            2 => 1,
            3 => 2,
            4 => 3,
            7 => 4,
            ResizeCoders.FancyStride => 4,
            _ => 3,
        };

        int bucket = HorizontalCostBucket(horizontalFilterPixelWidth);
        float rawCost = horizontalIsPeriod1
            ? HorizontalCostPeriod1[channelIndex, bucket]
            : HorizontalCost[channelIndex, bucket];
        long costRatio = (long)((rawCost * 1000.0f) + 0.5f);

        long horizontalInput = horizontalInputSize + (horizontalEdge == ResizeEdge.Wrap ? horizontalFilterPixelWidth : 0);
        long verticalInput = verticalInputSize + (verticalEdge == ResizeEdge.Wrap ? verticalFilterPixelWidth : 0);
        long horizontalOutput = horizontalOutputSize;
        long verticalOutput = verticalOutputSize;
        long verticalTaps = verticalFilterPixelWidth + 1L;

        return ProductsLessOrEqual(
            verticalTaps * verticalOutput,
            (horizontalInput - horizontalOutput) * 1000L,
            costRatio * (horizontalInput + horizontalOutput),
            verticalInput - verticalOutput);
    }

    private static int HorizontalCostBucket(int taps)
    {
        if (taps <= 4)
        {
            return taps < 1 ? 0 : taps - 1;
        }

        if (taps <= 6)
        {
            return 4;
        }

        if (taps <= 8)
        {
            return 5;
        }

        if (taps <= 12)
        {
            return 6;
        }

        if (taps <= 17)
        {
            return 7;
        }

        return 8;
    }

    private static void Mul64x64(long a, long b, out long hi, out ulong lo)
    {
        ulong ua = (ulong)a;
        ulong ub = (ulong)b;
        ulong a0 = ua & 0xFFFFFFFFu;
        ulong a1 = ua >> 32;
        ulong b0 = ub & 0xFFFFFFFFu;
        ulong b1 = ub >> 32;
        ulong p00 = a0 * b0;
        ulong p01 = a0 * b1;
        ulong p10 = a1 * b0;
        ulong p11 = a1 * b1;
        ulong mid = (p00 >> 32) + (p01 & 0xFFFFFFFFu) + (p10 & 0xFFFFFFFFu);
        ulong uhi = p11 + (p01 >> 32) + (p10 >> 32) + (mid >> 32);
        lo = (mid << 32) | (p00 & 0xFFFFFFFFu);
        if (a < 0)
        {
            uhi -= ub;
        }

        if (b < 0)
        {
            uhi -= ua;
        }

        hi = (long)uhi;
    }

    private static bool ProductsLessOrEqual(long a, long b, long c, long d)
    {
        Mul64x64(a, b, out long lhi, out ulong llo);
        Mul64x64(c, d, out long rhi, out ulong rlo);
        return lhi < rhi || (lhi == rhi && llo <= rlo);
    }

    private static void Clip(ref int outX, ref int outSubW, int outW, ref double u0, ref double u1)
    {
        if (outX < 0)
        {
            double per = (double)outX / outSubW;
            double adj = per * (u1 - u0);
            u0 -= adj;
            outSubW += outX;
            outX = 0;
        }

        int over = outW - (outX + outSubW);
        if (over < 0)
        {
            double per = (double)over / outSubW;
            double adj = per * (u1 - u0);
            u1 += adj;
            outSubW = outW - outX;
        }
    }

    private static void ClipInput(ref int outX, ref int outSubW, ref double u0, ref double u1)
    {
        double perPixel = (u1 - u0) / outSubW;

        if (u0 < 0.0)
        {
            int drop = CeilToInt((-u0 / perPixel) - 0.5);
            if (drop > outSubW)
            {
                drop = outSubW;
            }

            if (drop > 0)
            {
                u0 += drop * perPixel;
                outX += drop;
                outSubW -= drop;
            }
        }

        if (u1 > 1.0)
        {
            int drop = CeilToInt(((u1 - 1.0) / perPixel) - 0.5);
            if (drop > outSubW)
            {
                drop = outSubW;
            }

            if (drop > 0)
            {
                u1 -= drop * perPixel;
                outSubW -= drop;
            }
        }
    }

    private static ulong Gcd(ulong a, ulong b)
    {
        while (b != 0)
        {
            ulong remainder = a % b;
            a = b;
            b = remainder;
        }

        return a;
    }

    private static bool RealToFraction(double f, double tolerance, out uint outNumer, out uint outDenom)
    {
        outNumer = 0;
        outDenom = 0;
        if (f <= 0.0)
        {
            return false;
        }

        double rem = f;
        ulong numer = 1;
        ulong denom = 0;
        ulong numerLast = 0;
        ulong denomLast = 1;

        for (int i = 0; i < 64; i++)
        {
            ulong term = (ulong)rem;
            if (term > 0x7FFFFFFFu
                || ((term * numer) + numerLast) > 0x7FFFFFFFu
                || ((term * denom) + denomLast) > 0x7FFFFFFFu)
            {
                return false;
            }

            ulong next = (term * numer) + numerLast;
            numerLast = numer;
            numer = next;

            next = (term * denom) + denomLast;
            denomLast = denom;
            denom = next;

            double err = ((double)numer / denom) - f;
            if (err < 0.0)
            {
                err = -err;
            }

            if (err <= tolerance)
            {
                outNumer = (uint)numer;
                outDenom = (uint)denom;
                return true;
            }

            double frac = rem - term;
            if (frac <= 0.0)
            {
                return false;
            }

            rem = 1.0 / frac;
        }

        return false;
    }

    private static void ApproximatePolyphase(
        ulong numer,
        ulong denom,
        ulong outputs,
        ref ulong outNumer,
        ref ulong outDenom,
        ref int outDriftInPixels)
    {
        ulong top = numer;
        ulong bot = denom;
        ulong p = 1;
        ulong q = 0;
        ulong pLast = 0;
        ulong qLast = 1;

        while (bot != 0)
        {
            ulong est = top / bot;
            ulong remainder = top % bot;
            top = bot;
            bot = remainder;

            ulong next = (est * p) + pLast;
            pLast = p;
            p = next;

            next = (est * q) + qLast;
            qLast = q;
            q = next;

            if (p >= outputs)
            {
                return;
            }

            ulong pd = p * denom;
            ulong qn = q * numer;
            ulong diff = pd > qn ? pd - qn : qn - pd;
            if (diff <= qn / (outputs * PolyphaseDriftDiv))
            {
                double driftIn = (double)outputs * diff * denom / ((double)qn * numer);
                outNumer = p;
                outDenom = q;
                outDriftInPixels = (int)driftIn + 1;
                return;
            }
        }
    }

    private static bool ScaleToRational(
        int outputSubRange,
        int inputFullRange,
        double inputS,
        int sN0,
        int sN1,
        int sD,
        out uint outNumer,
        out uint outDenom,
        out int outApproximatedDrift)
    {
        outNumer = 0;
        outDenom = 0;
        outApproximatedDrift = 0;

        if (sD > 0 && sN1 > sN0)
        {
            ulong numer = (ulong)outputSubRange * (ulong)sD;
            ulong denom = (ulong)inputFullRange * (ulong)(sN1 - sN0);
            ulong g = Gcd(numer, denom);
            numer /= g;
            denom /= g;
            if (numer <= 0x7FFFFFFFu && denom <= 0x7FFFFFFFu)
            {
                if (numer >= (ulong)outputSubRange)
                {
                    ApproximatePolyphase(numer, denom, (ulong)outputSubRange, ref numer, ref denom, ref outApproximatedDrift);
                }

                outNumer = (uint)numer;
                outDenom = (uint)denom;
                return true;
            }
        }

        double scale = outputSubRange / (inputFullRange * inputS);
        if (!RealToFraction(scale, scale / ((double)outputSubRange * PolyphaseDriftDiv), out outNumer, out outDenom))
        {
            return false;
        }

        double err = ((double)outNumer / outDenom) - scale;
        if (err < 0.0)
        {
            err = -err;
        }

        if (err > 0.0)
        {
            outApproximatedDrift = (int)(outputSubRange * err / (scale * scale)) + 1;
        }

        return true;
    }
}
