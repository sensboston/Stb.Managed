using System.Runtime.CompilerServices;

namespace Stb.Managed.Internal;

internal struct ResizeContributor
{
    public int N0;
    public int N1;
}

internal sealed class ResizeSampler
{
    internal const int ForceGatherScanlinesThreshold = 128;

    private const double TieEpsilon = 1.0 / (1L << 36);
    private const double TooSmallCoefficient = 1.0 / (1 << 26);
    private const double TooSmallCoefficientSum = 1.0 / (1 << 16);
    private const double RenormTolerance = 1.0 / (1 << 26);

    internal ResizeScaleInfo ScaleInfo { get; }

    internal ResizeFilter ResolvedFilter { get; }

    internal ResizeFilterKernel? CustomKernel { get; }

    internal ResizeEdge Edge { get; }

    internal int FilterPixelWidth { get; }

    internal int FilterPixelMargin { get; }

    // 1 = gather upsample, 2 = gather downsample, 0 = scatter downsample
    internal int GatherMode { get; }

    internal bool IsGather => GatherMode != 0;

    internal int CoefficientWidth { get; }

    internal int NumContributors { get; }

    internal ResizeContributor[] Contributors { get; }

    internal float[] Coefficients { get; }

    internal int ExtentsLowest { get; private set; }

    internal int ExtentsHighest { get; private set; }

    internal int ExtentsWidest { get; private set; }

    internal bool IsPeriod1 { get; }

    internal int Period1Start { get; private set; }

    internal int Period1End { get; private set; }

    internal int Period1Count { get; private set; }

    internal int Period1Step { get; private set; }

    internal int RingEntriesCount { get; }

    private ResizeSampler(
        ResizeScaleInfo scaleInfo,
        ResizeFilter resolvedFilter,
        ResizeFilterKernel? customKernel,
        ResizeEdge edge,
        int filterPixelWidth,
        int filterPixelMargin,
        int gatherMode,
        int coefficientWidth,
        int numContributors,
        ResizeContributor[] contributors,
        float[] coefficients,
        bool isPeriod1,
        int ringEntriesCount)
    {
        ScaleInfo = scaleInfo;
        ResolvedFilter = resolvedFilter;
        CustomKernel = customKernel;
        Edge = edge;
        FilterPixelWidth = filterPixelWidth;
        FilterPixelMargin = filterPixelMargin;
        GatherMode = gatherMode;
        CoefficientWidth = coefficientWidth;
        NumContributors = numContributors;
        Contributors = contributors;
        Coefficients = coefficients;
        IsPeriod1 = isPeriod1;
        RingEntriesCount = ringEntriesCount;
    }

    internal static ResizeSampler Build(
        in ResizeScaleInfo scaleInfo,
        ResizeFilter filter,
        ResizeFilterKernel? customKernel,
        ResizeEdge edge,
        bool alwaysGather,
        StbLimits limits)
    {
        ResizeFilter resolvedFilter = customKernel is null
            ? ResizeFilters.ResolveDefault(filter, in scaleInfo)
            : ResizeFilter.Default;

        double inPixelsRadius = ResizeFilters.GetRadiusInInputPixels(resolvedFilter, customKernel, in scaleInfo);
        int filterPixelWidth = ResizeGeometry.CeilToInt(2.0 * inPixelsRadius);

        int gatherMode = 0;
        if (scaleInfo.IsUpsample)
        {
            gatherMode = 1;
        }
        else if (alwaysGather || filterPixelWidth <= ForceGatherScanlinesThreshold)
        {
            gatherMode = 2;
        }

        int coefficientWidth = gatherMode != 0
            ? ResizeGeometry.CeilToInt(2.0 * inPixelsRadius) + 1
            : ResizeGeometry.CeilToInt(2.0 * (inPixelsRadius / scaleInfo.InvScale));
        if (coefficientWidth < 1)
        {
            coefficientWidth = 1;
        }

        if (edge == ResizeEdge.Wrap && (filterPixelWidth / 3) > scaleInfo.InputFullSize)
        {
            filterPixelWidth = scaleInfo.InputFullSize * 3;
        }

        int filterPixelMargin = filterPixelWidth / 2;
        if (edge == ResizeEdge.Wrap && filterPixelMargin > scaleInfo.InputFullSize)
        {
            filterPixelMargin = scaleInfo.InputFullSize;
        }

        int numContributors = gatherMode != 0
            ? scaleInfo.OutputSubSize
            : scaleInfo.InputFullSize + (filterPixelMargin * 2);

        // Scatter needs build_width = coefficientWidth + 1 during calculation before packing.
        int allocCoeffWidth = gatherMode != 0 ? coefficientWidth : coefficientWidth + 1;
        long coeffsCountLong = (long)numContributors * allocCoeffWidth;
        limits.ValidateAllocation(coeffsCountLong * sizeof(float));
        limits.ValidateAllocation((long)numContributors * 8L);

        ResizeContributor[] contributors = new ResizeContributor[numContributors];
        float[] coefficients = new float[(int)coeffsCountLong];

        // Match stb_image_resize2's horizontal period-1 detection for ShouldDoVerticalFirst.
        bool isPeriod1 = gatherMode != 0
            && scaleInfo.ScaleIsRational
            && scaleInfo.ScaleNumerator == 1u
            && scaleInfo.OutputSubSize > 1;

        int ringEntriesCount = gatherMode != 0
            ? filterPixelWidth + 1
            : Math.Min(
                ResizeGeometry.CeilToInt(2.0 * (inPixelsRadius / scaleInfo.InvScale)) + 2,
                scaleInfo.OutputSubSize);
        if (ringEntriesCount < 1)
        {
            ringEntriesCount = 1;
        }

        ResizeSampler sampler = new(
            scaleInfo,
            resolvedFilter,
            customKernel,
            edge,
            filterPixelWidth,
            filterPixelMargin,
            gatherMode,
            coefficientWidth,
            numContributors,
            contributors,
            coefficients,
            isPeriod1,
            ringEntriesCount);

        sampler.BuildCoefficients(inPixelsRadius);
        return sampler;
    }

    internal int LowestScanlineNeeded(int y, int maxLook)
    {
        int startN0 = Contributors[y].N0;
        int lowestN0 = startN0;
        for (int j = 1; j <= maxLook; j++)
        {
            int n0 = Contributors[y + j].N0;
            if (n0 > startN0)
            {
                break;
            }

            if (n0 < lowestN0)
            {
                lowestN0 = n0;
            }
        }

        return lowestN0;
    }

    private void BuildCoefficients(double inPixelsRadius)
    {
        ResizeScaleInfo scaleInfo = ScaleInfo;

        if (GatherMode == 1)
        {
            double filterSupport = inPixelsRadius;
            double outPixelsRadius = filterSupport * scaleInfo.Scale;
            CalculateGatherUpsample(outPixelsRadius, filterSupport);
            CleanupGatheredCoefficients();
        }
        else if (GatherMode == 2)
        {
            double filterSupport = inPixelsRadius / scaleInfo.InvScale;
            CalculateGatherDownsample(-FilterPixelMargin, scaleInfo.InputFullSize + FilterPixelMargin, inPixelsRadius, filterSupport);
            CleanupGatheredCoefficients();
            DetectPeriod1Run();
        }
        else
        {
            double filterSupport = inPixelsRadius / scaleInfo.InvScale;
            CalculateScatterFilters(inPixelsRadius, filterSupport);
        }
    }

    // Detects the maximal contiguous run [Period1Start, Period1End) of output columns that share identical
    // tap count, constant positive input step, and bit-identical filter coefficients (e.g. 2:1, 3:1, 4:1 downscales).
    private void DetectPeriod1Run()
    {
        if (NumContributors < 8)
        {
            return;
        }

        int mid = NumContributors >> 1;
        ResizeContributor cMid = Contributors[mid];
        ResizeContributor cNext = Contributors[mid + 1];
        int count = cMid.N1 - cMid.N0 + 1;
        int step = cNext.N0 - cMid.N0;

        if (count <= 0 || step <= 0 || (cNext.N1 - cNext.N0 + 1) != count)
        {
            return;
        }

        int cw = CoefficientWidth;
        int midOff = mid * cw;
        int nextOff = (mid + 1) * cw;
        for (int i = 0; i < count; i++)
        {
            if (BitConverter.SingleToInt32Bits(Coefficients[midOff + i])
                != BitConverter.SingleToInt32Bits(Coefficients[nextOff + i]))
            {
                return;
            }
        }

        int start = mid;
        while (start > 0)
        {
            int prev = start - 1;
            ResizeContributor cp = Contributors[prev];
            if ((cp.N1 - cp.N0 + 1) != count || (Contributors[start].N0 - cp.N0) != step)
            {
                break;
            }

            int pOff = prev * cw;
            bool eq = true;
            for (int i = 0; i < count; i++)
            {
                if (BitConverter.SingleToInt32Bits(Coefficients[pOff + i])
                    != BitConverter.SingleToInt32Bits(Coefficients[midOff + i]))
                {
                    eq = false;
                    break;
                }
            }

            if (!eq)
            {
                break;
            }

            start = prev;
        }

        int end = mid + 2;
        while (end < NumContributors)
        {
            ResizeContributor ce = Contributors[end];
            if ((ce.N1 - ce.N0 + 1) != count || (ce.N0 - Contributors[end - 1].N0) != step)
            {
                break;
            }

            int eOff = end * cw;
            bool eq = true;
            for (int i = 0; i < count; i++)
            {
                if (BitConverter.SingleToInt32Bits(Coefficients[eOff + i])
                    != BitConverter.SingleToInt32Bits(Coefficients[midOff + i]))
                {
                    eq = false;
                    break;
                }
            }

            if (!eq)
            {
                break;
            }

            end++;
        }

        if (end - start >= 4)
        {
            Period1Start = start;
            Period1End = end;
            Period1Count = count;
            Period1Step = step;
        }
    }

    private double TooSmallCoefficientSumFor() =>
        ScaleInfo.IsUpsample ? TooSmallCoefficientSum : TooSmallCoefficientSum * ScaleInfo.Scale;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static double TapOffset(in ResizeScaleInfo scaleInfo, int o, int i, bool inInputPixels)
    {
        if (scaleInfo.PositionsExact)
        {
            long numer = (((2L * o) + 1L + (2L * scaleInfo.ShiftPixels)) * scaleInfo.ScaleDenominator)
                - (((2L * i) + 1L) * scaleInfo.ScaleNumerator);
            return numer * (inInputPixels ? scaleInfo.TapScaleIn : scaleInfo.TapScaleOut);
        }

        if (inInputPixels)
        {
            return (((o + 0.5 + scaleInfo.PixelShift) * scaleInfo.InvScale) - (i + 0.5));
        }

        return (o + 0.5) - (((i + 0.5) * scaleInfo.Scale) - scaleInfo.PixelShift);
    }

    private static void ClipInPixelRange(int first, int last, int inputSize, ResizeEdge edge, out int firstPixel, out int lastPixel)
    {
        if (last < first)
        {
            last = first;
        }

        if (edge == ResizeEdge.Wrap)
        {
            if (first < -inputSize)
            {
                first = -inputSize;
            }

            if ((last - inputSize) >= inputSize)
            {
                last = (inputSize - 1) + inputSize;
            }
        }

        firstPixel = first;
        lastPixel = last;
    }

    private static void ClipOutPixelRange(int first, int last, int outSize, out int firstPixel, out int lastPixel)
    {
        if (first < 0)
        {
            first = 0;
        }

        if (last >= outSize)
        {
            last = outSize - 1;
        }

        firstPixel = first;
        lastPixel = last;
    }

    private static void ExactPixelRange(long numer, long den, double denReciprocal, double support, out int first, out int last)
    {
        int q = (int)(numer / den);
        double f = (numer % den) * denReciprocal;
        first = q + ResizeGeometry.FloorToInt(f - support + 0.5 + TieEpsilon);
        last = q + ResizeGeometry.FloorToInt(f + support - 0.5 + TieEpsilon);
    }

    private static void ExactOutputRange(long numer, long den, double denReciprocal, double support, out int first, out int last)
    {
        int q = (int)(numer / den);
        double f = (numer % den) * denReciprocal;
        first = q + ResizeGeometry.FloorToInt(f - support - 0.5 - TieEpsilon) + 1;
        last = q + ResizeGeometry.FloorToInt(f + support - 0.5 - TieEpsilon);
    }

    private static void OutputInPixelRange(
        in ResizeScaleInfo scaleInfo,
        int o,
        double outFilterRadius,
        double support,
        ResizeEdge edge,
        out int firstPixel,
        out int lastPixel)
    {
        if (scaleInfo.PositionsExact)
        {
            long numer = ((2L * o) + 1L + (2L * scaleInfo.ShiftPixels)) * scaleInfo.ScaleDenominator;
            ExactPixelRange(numer, 2L * scaleInfo.ScaleNumerator, scaleInfo.TapScaleIn, support, out int first, out int last);
            ClipInPixelRange(first, last, scaleInfo.InputFullSize, edge, out firstPixel, out lastPixel);
        }
        else
        {
            double outCenter = o + 0.5;
            double lower = (outCenter - outFilterRadius + scaleInfo.PixelShift) * scaleInfo.InvScale;
            double upper = (outCenter + outFilterRadius + scaleInfo.PixelShift) * scaleInfo.InvScale;
            ClipInPixelRange(
                ResizeGeometry.FloorToInt(lower + 0.5 + TieEpsilon),
                ResizeGeometry.FloorToInt(upper - 0.5 + TieEpsilon),
                scaleInfo.InputFullSize,
                edge,
                out firstPixel,
                out lastPixel);
        }
    }

    private static void InputOutPixelRange(
        in ResizeScaleInfo scaleInfo,
        int i,
        double inPixelsRadius,
        double support,
        out int firstPixel,
        out int lastPixel)
    {
        if (scaleInfo.PositionsExact)
        {
            long numer = (((2L * i) + 1L) * scaleInfo.ScaleNumerator)
                - (2L * scaleInfo.ShiftPixels * scaleInfo.ScaleDenominator);
            ExactOutputRange(numer, 2L * scaleInfo.ScaleDenominator, scaleInfo.TapScaleOut, support, out int first, out int last);
            ClipOutPixelRange(first, last, scaleInfo.OutputSubSize, out firstPixel, out lastPixel);
        }
        else
        {
            double inCenter = i + 0.5;
            double lower = ((inCenter - inPixelsRadius) * scaleInfo.Scale) - scaleInfo.PixelShift;
            double upper = ((inCenter + inPixelsRadius) * scaleInfo.Scale) - scaleInfo.PixelShift;
            ClipOutPixelRange(
                ResizeGeometry.FloorToInt(lower - 0.5 - TieEpsilon) + 1,
                ResizeGeometry.FloorToInt(upper - 0.5 - TieEpsilon),
                scaleInfo.OutputSubSize,
                out firstPixel,
                out lastPixel);
        }
    }

    private void CalculateGatherUpsample(double outFilterRadius, double support)
    {
        ResizeScaleInfo scaleInfo = ScaleInfo;
        int numerator = (int)scaleInfo.ScaleNumerator;
        bool polyphase = scaleInfo.ScaleIsRational && numerator < NumContributors;
        int end = polyphase ? numerator : NumContributors;

        for (int n = 0; n < end; n++)
        {
            OutputInPixelRange(in scaleInfo, n, outFilterRadius, support, Edge, out int inFirstPixel, out int inLastPixel);

            if ((inLastPixel - inFirstPixel + 1) > CoefficientWidth)
            {
                inLastPixel = inFirstPixel + CoefficientWidth - 1;
            }

            int groupOffset = n * CoefficientWidth;
            int lastNonZero = -1;

            for (int i = 0; i <= inLastPixel - inFirstPixel; i++)
            {
                double weight = ResizeFilters.EvaluateKernel(
                    ResolvedFilter,
                    CustomKernel,
                    TapOffset(in scaleInfo, n, i + inFirstPixel, inInputPixels: true),
                    scaleInfo.InvScale);
                float coeff = (float)weight;

                if (weight < TooSmallCoefficient && weight > -TooSmallCoefficient)
                {
                    if (i == 0 && inFirstPixel < inLastPixel)
                    {
                        inFirstPixel++;
                        i--;
                        continue;
                    }

                    coeff = 0.0f;
                }
                else
                {
                    lastNonZero = i;
                }

                Coefficients[groupOffset + i] = coeff;
            }

            if (lastNonZero < 0)
            {
                Contributors[n].N0 = inFirstPixel;
                Contributors[n].N1 = inFirstPixel;
                Coefficients[groupOffset] = 0.0f;
            }
            else
            {
                Contributors[n].N0 = inFirstPixel;
                Contributors[n].N1 = inFirstPixel + lastNonZero;
            }
        }
    }

    private void CalculateGatherDownsample(int start, int end, double inPixelsRadius, double support)
    {
        ResizeScaleInfo scaleInfo = ScaleInfo;
        int outSize = scaleInfo.OutputSubSize;
        int numerator = (int)scaleInfo.ScaleNumerator;
        bool polyphase = scaleInfo.ScaleIsRational && numerator < outSize;
        int initCount = polyphase ? numerator : outSize;

        for (int o = 0; o < initCount; o++)
        {
            Contributors[o].N0 = 0;
            Contributors[o].N1 = -1;
        }

        for (int inPixel = start; inPixel < end; inPixel++)
        {
            InputOutPixelRange(in scaleInfo, inPixel, inPixelsRadius, support, out int outFirstPixel, out int outLastPixel);
            if (outFirstPixel > outLastPixel)
            {
                continue;
            }

            if (polyphase)
            {
                if (outFirstPixel >= numerator)
                {
                    break;
                }

                if (outLastPixel >= numerator)
                {
                    outLastPixel = numerator - 1;
                }
            }

            for (int i = 0; i <= outLastPixel - outFirstPixel; i++)
            {
                int outIdx = i + outFirstPixel;
                double weight = ResizeFilters.EvaluateKernel(
                    ResolvedFilter,
                    CustomKernel,
                    TapOffset(in scaleInfo, outIdx, inPixel, inInputPixels: false),
                    scaleInfo.Scale);
                float coeff = 0.0f;
                if (weight >= TooSmallCoefficient || weight <= -TooSmallCoefficient)
                {
                    coeff = (float)(weight * scaleInfo.Scale);
                }

                int groupOffset = outIdx * CoefficientWidth;
                ref ResizeContributor contrib = ref Contributors[outIdx];
                if (contrib.N1 < contrib.N0)
                {
                    contrib.N0 = inPixel;
                    contrib.N1 = inPixel;
                    Coefficients[groupOffset] = coeff;
                }
                else
                {
                    if (Coefficients[groupOffset] == 0.0f && (inPixel - contrib.N0) == 1)
                    {
                        contrib.N0 = inPixel;
                    }

                    int slot = inPixel - contrib.N0;
                    if (slot < CoefficientWidth)
                    {
                        contrib.N1 = inPixel;
                        Coefficients[groupOffset + slot] = coeff;
                    }
                }
            }
        }
    }

    private static void InsertCoeff(
        ref ResizeContributor contrib,
        Span<float> coeffs,
        int newPixel,
        float newCoeff,
        int maxWidth)
    {
        if (contrib.N1 < contrib.N0)
        {
            contrib.N0 = newPixel;
            contrib.N1 = newPixel;
            coeffs[0] = newCoeff;
        }
        else if (newPixel <= contrib.N1)
        {
            if (newPixel < contrib.N0)
            {
                if ((contrib.N1 - newPixel + 1) <= maxWidth)
                {
                    int o = contrib.N0 - newPixel;
                    for (int j = contrib.N1 - contrib.N0; j >= 0; j--)
                    {
                        coeffs[j + o] = coeffs[j];
                    }

                    for (int j = 1; j < o; j++)
                    {
                        coeffs[j] = 0.0f;
                    }

                    coeffs[0] = newCoeff;
                    contrib.N0 = newPixel;
                }
            }
            else
            {
                coeffs[newPixel - contrib.N0] += newCoeff;
            }
        }
        else
        {
            if ((newPixel - contrib.N0 + 1) <= maxWidth)
            {
                int e = newPixel - contrib.N0;
                for (int j = (contrib.N1 - contrib.N0) + 1; j < e; j++)
                {
                    coeffs[j] = 0.0f;
                }

                coeffs[e] = newCoeff;
                contrib.N1 = newPixel;
            }
        }
    }

    private void FoldAndTrimContributor(ref ResizeContributor contrib, Span<float> coeffs)
    {
        int inputSize = ScaleInfo.InputFullSize;
        int inputLastPixel = inputSize - 1;

        if (Edge == ResizeEdge.Zero)
        {
            if (contrib.N1 > inputLastPixel)
            {
                contrib.N1 = inputLastPixel;
            }

            if (contrib.N0 < 0)
            {
                int skip = -contrib.N0;
                contrib.N0 = 0;
                int remaining = contrib.N1 - contrib.N0 + 1;
                for (int i = 0; i < remaining; i++)
                {
                    coeffs[i] = coeffs[i + skip];
                }
            }
        }
        else if (Edge == ResizeEdge.Clamp || Edge == ResizeEdge.Reflect)
        {
            // Right hand side first.
            if (contrib.N1 > inputLastPixel)
            {
                int first = contrib.N0;
                int last = contrib.N1;
                contrib.N1 = inputLastPixel;
                for (int i = inputSize; i <= last; i++)
                {
                    int folded = ResizeGeometry.EdgeWrapSlow(Edge, i, inputSize);
                    InsertCoeff(ref contrib, coeffs, folded, coeffs[i - first], CoefficientWidth);
                }
            }

            // Then the left hand side.
            if (contrib.N0 < 0)
            {
                int first = contrib.N0;
                int startNeg = contrib.N1 < -1 ? contrib.N1 : -1;
                int coeffIdx = startNeg - first;
                for (int i = startNeg; i > first; i--)
                {
                    int folded = ResizeGeometry.EdgeWrapSlow(Edge, i, inputSize);
                    InsertCoeff(ref contrib, coeffs, folded, coeffs[coeffIdx--], CoefficientWidth);
                }

                float firstCoeff = coeffs[0];

                contrib.N0 = 0;
                for (int i = 0; i <= contrib.N1; i++)
                {
                    coeffs[i] = coeffs[i - first];
                }

                int foldedFirst = ResizeGeometry.EdgeWrapSlow(Edge, first, inputSize);
                InsertCoeff(ref contrib, coeffs, foldedFirst, firstCoeff, CoefficientWidth);
            }
        }

        if (contrib.N0 <= contrib.N1)
        {
            int count = contrib.N1 - contrib.N0 + 1;
            while (count > 0 && coeffs[count - 1] == 0.0f)
            {
                count--;
            }

            contrib.N1 = contrib.N0 + count - 1;

            if (count > 0)
            {
                if (contrib.N0 < ExtentsLowest)
                {
                    ExtentsLowest = contrib.N0;
                }

                if (contrib.N1 > ExtentsHighest)
                {
                    ExtentsHighest = contrib.N1;
                }

                if (count > ExtentsWidest)
                {
                    ExtentsWidest = count;
                }
            }

            for (int i = count; i < CoefficientWidth; i++)
            {
                coeffs[i] = 0.0f;
            }
        }
        else
        {
            coeffs.Clear();
        }
    }

    private void CleanupGatheredCoefficients()
    {
        int numerator = (int)ScaleInfo.ScaleNumerator;
        int denominator = (int)ScaleInfo.ScaleDenominator;
        bool polyphase = ScaleInfo.ScaleIsRational && numerator < NumContributors;
        int calculated = polyphase ? numerator : NumContributors;
        double tooSmallSum = TooSmallCoefficientSumFor();

        for (int n = 0; n < calculated; n++)
        {
            ref ResizeContributor contrib = ref Contributors[n];
            int groupOffset = n * CoefficientWidth;
            double totalFilter = 0.0;
            int last = contrib.N1 - contrib.N0;

            for (int i = 0; i <= last; i++)
            {
                totalFilter += Coefficients[groupOffset + i];
            }

            if (totalFilter < tooSmallSum && totalFilter > -tooSmallSum)
            {
                contrib.N1 = contrib.N0;
                Coefficients[groupOffset] = 0.0f;
            }
            else if (totalFilter < (1.0 - RenormTolerance) || totalFilter > (1.0 + RenormTolerance))
            {
                double filterScale = 1.0 / totalFilter;
                for (int i = 0; i <= last; i++)
                {
                    Coefficients[groupOffset + i] = (float)(Coefficients[groupOffset + i] * filterScale);
                }
            }
        }

        if (polyphase)
        {
            for (int n = numerator; n < NumContributors; n++)
            {
                Contributors[n].N0 = Contributors[n - numerator].N0 + denominator;
                Contributors[n].N1 = Contributors[n - numerator].N1 + denominator;
                Array.Copy(
                    Coefficients,
                    (n - numerator) * CoefficientWidth,
                    Coefficients,
                    n * CoefficientWidth,
                    CoefficientWidth);
            }
        }

        ExtentsLowest = int.MaxValue;
        ExtentsHighest = int.MinValue;
        ExtentsWidest = 0;

        for (int n = 0; n < NumContributors; n++)
        {
            FoldAndTrimContributor(
                ref Contributors[n],
                Coefficients.AsSpan(n * CoefficientWidth, CoefficientWidth));
        }

        if (ExtentsLowest > ExtentsHighest)
        {
            ExtentsLowest = 0;
            ExtentsHighest = 0;
        }
    }

    private void CalculateScatterFilters(double inPixelsRadius, double support)
    {
        ResizeScaleInfo scaleInfo = ScaleInfo;
        int outputSize = scaleInfo.OutputSubSize;
        int buildWidth = CoefficientWidth + 1;
        bool polyphase = scaleInfo.ScaleIsRational && (int)scaleInfo.ScaleNumerator < outputSize;
        int numerator = polyphase ? (int)scaleInfo.ScaleNumerator : outputSize;
        int calculatedEnd = NumContributors;
        int firstWeighted = -1;

        for (int n = 0; n < NumContributors; n++)
        {
            int inPixel = n - FilterPixelMargin;
            InputOutPixelRange(in scaleInfo, inPixel, inPixelsRadius, support, out int outFirstPixel, out int outLastPixel);

            if (outFirstPixel >= numerator)
            {
                calculatedEnd = n;
                break;
            }

            if (outLastPixel >= numerator)
            {
                outLastPixel = numerator - 1;
            }

            Contributors[n].N0 = outFirstPixel;
            Contributors[n].N1 = outLastPixel;

            int groupOffset = n * buildWidth;
            for (int i = 0; i <= outLastPixel - outFirstPixel; i++)
            {
                double weight = ResizeFilters.EvaluateKernel(
                    ResolvedFilter,
                    CustomKernel,
                    TapOffset(in scaleInfo, i + outFirstPixel, inPixel, inInputPixels: false),
                    scaleInfo.Scale);
                float coeff = 0.0f;
                if (weight >= TooSmallCoefficient || weight <= -TooSmallCoefficient)
                {
                    coeff = (float)(weight * scaleInfo.Scale);
                }

                Coefficients[groupOffset + i] = coeff;
            }

            if (firstWeighted < 0 && outLastPixel >= outFirstPixel)
            {
                firstWeighted = n;
            }
        }

        for (int n = calculatedEnd; n < NumContributors; n++)
        {
            Contributors[n].N0 = 0;
            Contributors[n].N1 = -1;
        }

        NormalizeScatterCoefficients(buildWidth, calculatedEnd, numerator, TooSmallCoefficientSumFor());

        if (polyphase && firstWeighted >= 0)
        {
            ReplicateScatterCoefficients(
                buildWidth,
                firstWeighted + (int)scaleInfo.ScaleDenominator,
                calculatedEnd,
                numerator,
                (int)scaleInfo.ScaleDenominator,
                outputSize);
        }

        FoldScatterEdges(buildWidth);
        PackScatterCoefficients(buildWidth);
    }

    private void NormalizeScatterCoefficients(int buildWidth, int numContributors, int outputSize, double tooSmallSum)
    {
        int first = 0;

        for (int i = 0; i < outputSize; i++)
        {
            double total = 0.0;

            while (first < numContributors && Contributors[first].N1 < i)
            {
                first++;
            }

            for (int j = first; j < numContributors; j++)
            {
                if (i >= Contributors[j].N0 && i <= Contributors[j].N1)
                {
                    total += Coefficients[(j * buildWidth) + i - Contributors[j].N0];
                }
                else if (i < Contributors[j].N0)
                {
                    break;
                }
            }

            double filterScale;
            if (total < tooSmallSum && total > -tooSmallSum)
            {
                filterScale = 0.0;
            }
            else if (total < (1.0 - RenormTolerance) || total > (1.0 + RenormTolerance))
            {
                filterScale = 1.0 / total;
            }
            else
            {
                continue;
            }

            for (int j = first; j < numContributors; j++)
            {
                if (i >= Contributors[j].N0 && i <= Contributors[j].N1)
                {
                    int idx = (j * buildWidth) + i - Contributors[j].N0;
                    Coefficients[idx] = (float)(Coefficients[idx] * filterScale);
                }
                else if (i < Contributors[j].N0)
                {
                    break;
                }
            }
        }
    }

    private void ReplicateScatterCoefficients(
        int buildWidth,
        int firstCopy,
        int calculatedEnd,
        int numerator,
        int denominator,
        int outputSize)
    {
        for (int j = firstCopy; j < NumContributors; j++)
        {
            ref ResizeContributor contrib = ref Contributors[j];
            ResizeContributor source = Contributors[j - denominator];
            int dstOffset = j * buildWidth;
            int srcOffset = (j - denominator) * buildWidth;

            if (j >= calculatedEnd)
            {
                contrib.N0 = 0;
                contrib.N1 = -1;

                if (source.N1 >= source.N0 && source.N1 + numerator < outputSize)
                {
                    contrib.N0 = source.N0 + numerator;
                    contrib.N1 = source.N1 + numerator;
                    Array.Copy(Coefficients, srcOffset, Coefficients, dstOffset, source.N1 - source.N0 + 1);
                    continue;
                }
            }

            for (int i = 0; i <= source.N1 - source.N0; i++)
            {
                int outIdx = source.N0 + i + numerator;
                if (outIdx >= outputSize)
                {
                    break;
                }

                if (contrib.N1 < contrib.N0)
                {
                    contrib.N0 = outIdx;
                    contrib.N1 = outIdx;
                    Coefficients[dstOffset] = Coefficients[srcOffset + i];
                }
                else
                {
                    InsertCoeff(
                        ref contrib,
                        Coefficients.AsSpan(dstOffset, buildWidth),
                        outIdx,
                        Coefficients[srcOffset + i],
                        buildWidth);
                }
            }
        }
    }

    private void FoldScatterEdges(int buildWidth)
    {
        int inputSize = ScaleInfo.InputFullSize;

        if (Edge == ResizeEdge.Clamp || Edge == ResizeEdge.Reflect)
        {
            for (int pass = 0; pass < 2; pass++)
            {
                int from = pass != 0 ? -1 : inputSize;
                int to = pass != 0 ? -FilterPixelMargin - 1 : inputSize + FilterPixelMargin;
                int step = pass != 0 ? -1 : 1;

                for (int n = from; n != to; n += step)
                {
                    int srcIdx = n + FilterPixelMargin;
                    ref ResizeContributor contrib = ref Contributors[srcIdx];
                    int srcOffset = srcIdx * buildWidth;
                    int foldedIdx = ResizeGeometry.EdgeWrapSlow(Edge, n, inputSize) + FilterPixelMargin;
                    int foldedOffset = foldedIdx * buildWidth;

                    for (int i = 0; i <= contrib.N1 - contrib.N0; i++)
                    {
                        InsertCoeff(
                            ref Contributors[foldedIdx],
                            Coefficients.AsSpan(foldedOffset, buildWidth),
                            contrib.N0 + i,
                            Coefficients[srcOffset + i],
                            buildWidth);
                    }

                    contrib.N0 = 0;
                    contrib.N1 = -1;
                }
            }
        }
        else if (Edge == ResizeEdge.Zero)
        {
            for (int n = 0; n < FilterPixelMargin; n++)
            {
                Contributors[n].N0 = 0;
                Contributors[n].N1 = -1;
                int tail = inputSize + (2 * FilterPixelMargin) - 1 - n;
                Contributors[tail].N0 = 0;
                Contributors[tail].N1 = -1;
            }
        }
    }

    private void PackScatterCoefficients(int buildWidth)
    {
        ExtentsLowest = int.MaxValue;
        ExtentsHighest = int.MinValue;
        ExtentsWidest = CoefficientWidth;

        for (int j = 0; j < NumContributors; j++)
        {
            ref ResizeContributor contrib = ref Contributors[j];
            int srcOffset = j * buildWidth;
            int dstOffset = j * CoefficientWidth;
            int range = contrib.N1 - contrib.N0 + 1;
            int skip = 0;

            while (skip < range && Coefficients[srcOffset + skip] == 0.0f)
            {
                skip++;
            }

            range -= skip;
            if (range > CoefficientWidth)
            {
                range = CoefficientWidth;
            }

            while (range > 0 && Coefficients[srcOffset + skip + range - 1] == 0.0f)
            {
                range--;
            }

            for (int i = 0; i < range; i++)
            {
                Coefficients[dstOffset + i] = Coefficients[srcOffset + skip + i];
            }

            if (range <= 0)
            {
                contrib.N0 = 0;
                contrib.N1 = -1;
                continue;
            }

            contrib.N0 += skip;
            contrib.N1 = contrib.N0 + range - 1;

            int inputRow = j - FilterPixelMargin;
            if (inputRow < ExtentsLowest)
            {
                ExtentsLowest = inputRow;
            }

            ExtentsHighest = inputRow;
        }

        if (ExtentsLowest > ExtentsHighest)
        {
            ExtentsLowest = 0;
            ExtentsHighest = -1;
        }
    }
}
