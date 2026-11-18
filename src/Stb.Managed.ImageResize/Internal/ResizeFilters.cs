using System.Runtime.CompilerServices;

namespace Stb.Managed.Internal;

internal static class ResizeFilters
{
    private const double Pi = 3.14159265358979323846;

    private static readonly double[] Muratori6Knots =
    [
        19.0 / 32.0,
        1.0,
        19.0 / 32.0,
        0.0,
        -4.0 / 32.0,
        0.0,
        1.0 / 32.0,
        0.0,
        0.0,
    ];

    internal static ResizeFilter ResolveDefault(ResizeFilter filter, in ResizeScaleInfo scaleInfo)
    {
        if (filter != ResizeFilter.Default)
        {
            return filter;
        }

        if (scaleInfo.IsUpsample)
        {
            return scaleInfo.IsCopy ? ResizeFilter.PointSample : ResizeFilter.CatmullRom;
        }

        return ResizeFilter.Mitchell;
    }

    internal static double GetRadiusInInputPixels(
        ResizeFilter filter,
        ResizeFilterKernel? customKernel,
        in ResizeScaleInfo scaleInfo)
    {
        bool isUpsample = scaleInfo.IsUpsample;
        double r;

        if (customKernel is not null)
        {
            if (customKernel.Radius is not null)
            {
                r = customKernel.Radius(scaleInfo.Scale, scaleInfo.InvScale, isUpsample);
            }
            else
            {
                double ratio = isUpsample ? scaleInfo.InvScale : scaleInfo.Scale;
                r = customKernel.Support!(ratio);
                if (!isUpsample)
                {
                    r *= scaleInfo.InvScale;
                }
            }
        }
        else
        {
            switch (filter)
            {
                case ResizeFilter.Box:
                    r = (0.5 * scaleInfo.InvScale) + 0.5;
                    break;
                case ResizeFilter.PointSample:
                    r = 0.5;
                    break;
                default:
                    double support = GetBuiltinSupport(filter);
                    r = isUpsample ? support : support * scaleInfo.InvScale;
                    break;
            }
        }

        if (!(r >= 0.5))
        {
            r = 0.5;
        }

        return r;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static double GetRadiusInOutputPixels(
        ResizeFilter filter,
        ResizeFilterKernel? customKernel,
        in ResizeScaleInfo scaleInfo) =>
        GetRadiusInInputPixels(filter, customKernel, in scaleInfo) / scaleInfo.InvScale;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static double EvaluateKernel(
        ResizeFilter filter,
        ResizeFilterKernel? customKernel,
        double x,
        double scale)
    {
        if (customKernel is not null)
        {
            return customKernel.Evaluate(x, scale);
        }

        return filter switch
        {
            ResizeFilter.Box => FilterTrapezoid(x, scale),
            ResizeFilter.Triangle => FilterTriangle(x),
            ResizeFilter.CubicBSpline => MitchellNetravali(x, 1.0),
            ResizeFilter.CatmullRom => FilterCatmullRom(x),
            ResizeFilter.Mitchell => MitchellNetravali(x, 1.0 / 3.0),
            ResizeFilter.PointSample => 1.0,
            ResizeFilter.MitchellQuarter => MitchellNetravali(x, 0.25),
            ResizeFilter.MitchellSixth => MitchellNetravali(x, 1.0 / 6.0),
            ResizeFilter.MitchellNegSixth => MitchellNetravali(x, -1.0 / 6.0),
            ResizeFilter.MitchellNegThird => MitchellNetravali(x, -1.0 / 3.0),
            ResizeFilter.Lanczos4 => FilterLanczos(x, 2.0),
            ResizeFilter.Lanczos5 => FilterLanczos(x, 2.5),
            ResizeFilter.Muratori6 => FilterMuratori6(x),
            _ => 0.0,
        };
    }

    private static double GetBuiltinSupport(ResizeFilter filter) => filter switch
    {
        ResizeFilter.Triangle => 1.0,
        ResizeFilter.CubicBSpline => 2.0,
        ResizeFilter.CatmullRom => 2.0,
        ResizeFilter.Mitchell => 2.0,
        ResizeFilter.MitchellQuarter => 2.0,
        ResizeFilter.MitchellSixth => 2.0,
        ResizeFilter.MitchellNegSixth => 2.0,
        ResizeFilter.MitchellNegThird => 2.0,
        ResizeFilter.Lanczos4 => 2.0,
        ResizeFilter.Lanczos5 => 2.5,
        ResizeFilter.Muratori6 => 3.0,
        _ => 2.0,
    };

    private static double FilterTrapezoid(double x, double scale)
    {
        double halfScale = scale * 0.5;
        double t = 0.5 + halfScale;
        double r = 0.5 - halfScale;

        if (x < 0.0)
        {
            x = -x;
        }

        if (x >= t)
        {
            return 0.0;
        }

        if (x <= r)
        {
            return 1.0;
        }

        return (t - x) / scale;
    }

    private static double FilterTriangle(double x)
    {
        if (x < 0.0)
        {
            x = -x;
        }

        return x <= 1.0 ? 1.0 - x : 0.0;
    }

    private static double MitchellNetravali(double x, double b)
    {
        if (x < 0.0)
        {
            x = -x;
        }

        if (x < 1.0)
        {
            return ((6.0 - (2.0 * b)) + (x * x * (((9.0 - (6.0 * b)) * x) - (15.0 - (9.0 * b))))) / 6.0;
        }

        if (x < 2.0)
        {
            return ((12.0 - (4.0 * b)) + (x * (((12.0 * b) - 24.0) + (x * ((15.0 - (9.0 * b)) + (((2.0 * b) - 3.0) * x)))))) / 6.0;
        }

        return 0.0;
    }

    private static double FilterCatmullRom(double x)
    {
        if (x < 0.0)
        {
            x = -x;
        }

        if (x < 1.0)
        {
            return 1.0 - (x * x * (2.5 - (1.5 * x)));
        }

        if (x < 2.0)
        {
            return 2.0 - (x * (4.0 + (x * ((0.5 * x) - 2.5))));
        }

        return 0.0;
    }

    // Matches stbir__sinc_pi exactly (no libm sin, deterministic across platforms).
    private static double SincPi(double x)
    {
        if (x < 0.0)
        {
            x = -x;
        }

        if (x < 1e-4)
        {
            double px = Pi * x;
            return 1.0 - ((px * px) / 6.0);
        }

        int n = (int)(x + 0.5);
        double u = Pi * (x - n);
        double u2 = u * u;
        double s = 1.0 - (u2 / 156.0);
        s = 1.0 - ((u2 / 110.0) * s);
        s = 1.0 - ((u2 / 72.0) * s);
        s = 1.0 - ((u2 / 42.0) * s);
        s = 1.0 - ((u2 / 20.0) * s);
        s = 1.0 - ((u2 / 6.0) * s);
        s = u * s;
        if ((n & 1) != 0)
        {
            s = -s;
        }

        return s / (Pi * x);
    }

    private static double FilterLanczos(double x, double support)
    {
        if (x < 0.0)
        {
            x = -x;
        }

        return x < support ? SincPi(x) * SincPi(x / support) : 0.0;
    }

    private static double FilterMuratori6(double x)
    {
        if (x < 0.0)
        {
            x = -x;
        }

        if (x >= 3.0)
        {
            return 0.0;
        }

        double t = x * 2.0;
        int i = (int)t;
        t -= i;

        double p0 = Muratori6Knots[i];
        double p1 = Muratori6Knots[i + 1];
        double p2 = Muratori6Knots[i + 2];
        double p3 = Muratori6Knots[i + 3];

        return 0.5 * ((2.0 * p1) + (t * ((p2 - p0) + (t * (((2.0 * p0) - (5.0 * p1) + (4.0 * p2) - p3) + (t * (((3.0 * p1) - (3.0 * p2)) + (p3 - p0))))))));
    }
}
