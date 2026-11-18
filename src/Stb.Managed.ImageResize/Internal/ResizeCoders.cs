using System.Buffers.Binary;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
#if NET8_0_OR_GREATER
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.Arm;
using System.Runtime.Intrinsics.X86;
#endif

namespace Stb.Managed.Internal;

internal static class ResizeCoders
{
    internal const float MaxUInt8AsFloat = 255.0f;
    internal const float MaxUInt16AsFloat = 65535.0f;
    internal const float InvMaxUInt8 = 3.9215689e-03f;
    internal const float InvMaxUInt16 = 1.5259022e-05f;
    internal const float AlphaTooSmallToDivide = 1.0f / (1 << 20) / (1 << 20) / (1 << 20) / (1 << 20) / (1 << 20) / (1 << 20);

    private const uint SrgbAlmostOneBits = 0x3F7FFFFFu;
    private const uint SrgbMinValBits = (127u - 13u) << 23;
    private const uint HalfMagicBits = (254u - 15u) << 23;
    private const uint HalfWasInfNanBits = (127u + 16u) << 23;
    private const uint Float32InfBits = 255u << 23;
    private const uint Float16MaxBits = (127u + 16u) << 23;
    private const uint HalfDenormMagicBits = ((127u - 15u) + (23u - 10u) + 1u) << 23;

    private static readonly float SrgbAlmostOne = UInt32BitsToSingle(SrgbAlmostOneBits);
    private static readonly float SrgbMinVal = UInt32BitsToSingle(SrgbMinValBits);
    private static readonly float HalfMagic = UInt32BitsToSingle(HalfMagicBits);
    private static readonly float HalfWasInfNan = UInt32BitsToSingle(HalfWasInfNanBits);
    private static readonly float HalfDenormMagic = UInt32BitsToSingle(HalfDenormMagicBits);
    private static readonly float[] ByteToLinearFloat = CreateByteToLinearTable();

    private static float[] CreateByteToLinearTable()
    {
        float[] table = new float[256];
        for (int i = 0; i < 256; i++)
        {
            table[i] = i * InvMaxUInt8;
        }

        return table;
    }

    // Exact stbir__srgb_uchar_to_linear_float[256] table from stb_image_resize2.h v2.50.
    internal static readonly float[] SrgbToLinear =
    [
        0.000000f, 0.000304f, 0.000607f, 0.000911f, 0.001214f, 0.001518f, 0.001821f, 0.002125f, 0.002428f, 0.002732f, 0.003035f,
        0.003347f, 0.003677f, 0.004025f, 0.004391f, 0.004777f, 0.005182f, 0.005605f, 0.006049f, 0.006512f, 0.006995f, 0.007499f,
        0.008023f, 0.008568f, 0.009134f, 0.009721f, 0.010330f, 0.010960f, 0.011612f, 0.012286f, 0.012983f, 0.013702f, 0.014444f,
        0.015209f, 0.015996f, 0.016807f, 0.017642f, 0.018500f, 0.019382f, 0.020289f, 0.021219f, 0.022174f, 0.023153f, 0.024158f,
        0.025187f, 0.026241f, 0.027321f, 0.028426f, 0.029557f, 0.030713f, 0.031896f, 0.033105f, 0.034340f, 0.035601f, 0.036889f,
        0.038204f, 0.039546f, 0.040915f, 0.042311f, 0.043735f, 0.045186f, 0.046665f, 0.048172f, 0.049707f, 0.051269f, 0.052861f,
        0.054480f, 0.056128f, 0.057805f, 0.059511f, 0.061246f, 0.063010f, 0.064803f, 0.066626f, 0.068478f, 0.070360f, 0.072272f,
        0.074214f, 0.076185f, 0.078187f, 0.080220f, 0.082283f, 0.084376f, 0.086500f, 0.088656f, 0.090842f, 0.093059f, 0.095307f,
        0.097587f, 0.099899f, 0.102242f, 0.104616f, 0.107023f, 0.109462f, 0.111932f, 0.114435f, 0.116971f, 0.119538f, 0.122139f,
        0.124772f, 0.127438f, 0.130136f, 0.132868f, 0.135633f, 0.138432f, 0.141263f, 0.144128f, 0.147027f, 0.149960f, 0.152926f,
        0.155926f, 0.158961f, 0.162029f, 0.165132f, 0.168269f, 0.171441f, 0.174647f, 0.177888f, 0.181164f, 0.184475f, 0.187821f,
        0.191202f, 0.194618f, 0.198069f, 0.201556f, 0.205079f, 0.208637f, 0.212231f, 0.215861f, 0.219526f, 0.223228f, 0.226966f,
        0.230740f, 0.234551f, 0.238398f, 0.242281f, 0.246201f, 0.250158f, 0.254152f, 0.258183f, 0.262251f, 0.266356f, 0.270498f,
        0.274677f, 0.278894f, 0.283149f, 0.287441f, 0.291771f, 0.296138f, 0.300544f, 0.304987f, 0.309469f, 0.313989f, 0.318547f,
        0.323143f, 0.327778f, 0.332452f, 0.337164f, 0.341914f, 0.346704f, 0.351533f, 0.356400f, 0.361307f, 0.366253f, 0.371238f,
        0.376262f, 0.381326f, 0.386430f, 0.391573f, 0.396755f, 0.401978f, 0.407240f, 0.412543f, 0.417885f, 0.423268f, 0.428691f,
        0.434154f, 0.439657f, 0.445201f, 0.450786f, 0.456411f, 0.462077f, 0.467784f, 0.473532f, 0.479320f, 0.485150f, 0.491021f,
        0.496933f, 0.502887f, 0.508881f, 0.514918f, 0.520996f, 0.527115f, 0.533276f, 0.539480f, 0.545725f, 0.552011f, 0.558340f,
        0.564712f, 0.571125f, 0.577581f, 0.584078f, 0.590619f, 0.597202f, 0.603827f, 0.610496f, 0.617207f, 0.623960f, 0.630757f,
        0.637597f, 0.644480f, 0.651406f, 0.658375f, 0.665387f, 0.672443f, 0.679543f, 0.686685f, 0.693872f, 0.701102f, 0.708376f,
        0.715694f, 0.723055f, 0.730461f, 0.737911f, 0.745404f, 0.752942f, 0.760525f, 0.768151f, 0.775822f, 0.783538f, 0.791298f,
        0.799103f, 0.806952f, 0.814847f, 0.822786f, 0.830770f, 0.838799f, 0.846873f, 0.854993f, 0.863157f, 0.871367f, 0.879622f,
        0.887923f, 0.896269f, 0.904661f, 0.913099f, 0.921582f, 0.930111f, 0.938686f, 0.947307f, 0.955974f, 0.964686f, 0.973445f,
        0.982251f, 0.991102f, 1.0f,
    ];

    // Exact Fabian Giesen fp32_to_srgb8_tab4[104] table from stb_image_resize2.h v2.50.
    internal static readonly uint[] Fp32ToSrgb8Tab4 =
    [
        0x0073000du, 0x007a000du, 0x0080000du, 0x0087000du, 0x008d000du, 0x0094000du, 0x009a000du, 0x00a1000du,
        0x00a7001au, 0x00b4001au, 0x00c1001au, 0x00ce001au, 0x00da001au, 0x00e7001au, 0x00f4001au, 0x0101001au,
        0x010e0033u, 0x01280033u, 0x01410033u, 0x015b0033u, 0x01750033u, 0x018f0033u, 0x01a80033u, 0x01c20033u,
        0x01dc0067u, 0x020f0067u, 0x02430067u, 0x02760067u, 0x02aa0067u, 0x02dd0067u, 0x03110067u, 0x03440067u,
        0x037800ceu, 0x03df00ceu, 0x044600ceu, 0x04ad00ceu, 0x051400ceu, 0x057b00c5u, 0x05dd00bcu, 0x063b00b5u,
        0x06970158u, 0x07420142u, 0x07e30130u, 0x087b0120u, 0x090b0112u, 0x09940106u, 0x0a1700fcu, 0x0a9500f2u,
        0x0b0f01cbu, 0x0bf401aeu, 0x0ccb0195u, 0x0d950180u, 0x0e56016eu, 0x0f0d015eu, 0x0fbc0150u, 0x10630143u,
        0x11070264u, 0x1238023eu, 0x1357021du, 0x14660201u, 0x156601e9u, 0x165a01d3u, 0x174401c0u, 0x182401afu,
        0x18fe0331u, 0x1a9602feu, 0x1c1502d2u, 0x1d7e02adu, 0x1ed4028du, 0x201a0270u, 0x21520256u, 0x227d0240u,
        0x239f0443u, 0x25c003feu, 0x27bf03c4u, 0x29a10392u, 0x2b6a0367u, 0x2d1d0341u, 0x2ebe031fu, 0x304d0300u,
        0x31d105b0u, 0x34a80555u, 0x37520507u, 0x39d504c5u, 0x3c37048bu, 0x3e7c0458u, 0x40a8042au, 0x42bd0401u,
        0x44c20798u, 0x488e071eu, 0x4c1c06b6u, 0x4f76065du, 0x52a50610u, 0x55ac05ccu, 0x5892058fu, 0x5b590559u,
        0x5e0c0a23u, 0x631c0980u, 0x67db08f6u, 0x6c55087fu, 0x70940818u, 0x74a007bdu, 0x787d076cu, 0x7c330723u,

        // Padding to 128 entries so a masked index is provably in range; never reached (inputs are clamped).
        0u, 0u, 0u, 0u, 0u, 0u, 0u, 0u,
        0u, 0u, 0u, 0u, 0u, 0u, 0u, 0u,
        0u, 0u, 0u, 0u, 0u, 0u, 0u, 0u,
    ];

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static uint SingleToUInt32Bits(float value) =>
        unchecked((uint)BitConverter.SingleToInt32Bits(value));

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static float UInt32BitsToSingle(uint bits) =>
        BitConverter.Int32BitsToSingle(unchecked((int)bits));

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static byte LinearToSrgbByte(float value)
    {
        if (!(value > SrgbMinVal))
        {
            return 0;
        }

        if (value > SrgbAlmostOne)
        {
            return 255;
        }

        uint bits = SingleToUInt32Bits(value);
        uint tab = Fp32ToSrgb8Tab4[(bits - SrgbMinValBits) >> 20];
        uint bias = (tab >> 16) << 9;
        uint scale = tab & 0xFFFFu;
        uint t = (bits >> 12) & 0xFFu;
        return (byte)((bias + (scale * t)) >> 16);
    }

#if NET8_0_OR_GREATER
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static byte LinearToSrgbByteFast(float value, ReadOnlySpan<uint> tabRef)
    {
        if (!(value > SrgbMinVal))
        {
            return 0;
        }

        if (value > SrgbAlmostOne)
        {
            return 255;
        }

        uint bits = BitConverter.SingleToUInt32Bits(value);
        uint tab = tabRef[(int)((bits - SrgbMinValBits) >> 20) & 127];
        uint bias = (tab >> 16) << 9;
        uint scale = tab & 0xFFFFu;
        uint t = (bits >> 12) & 0xFFu;
        return (byte)((bias + (scale * t)) >> 16);
    }
#endif

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static float HalfToFloat(ushort h)
    {
        uint u = (uint)(h & 0x7FFF) << 13;
        float f = UInt32BitsToSingle(u) * HalfMagic;
        u = SingleToUInt32Bits(f);
        if (f >= HalfWasInfNan)
        {
            u |= 255u << 23;
        }

        u |= (uint)(h & 0x8000) << 16;
        return UInt32BitsToSingle(u);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static ushort FloatToHalf(float value)
    {
        uint fu = SingleToUInt32Bits(value);
        uint sign = fu & 0x80000000u;
        fu ^= sign;
        ushort o;

        if (fu >= Float16MaxBits)
        {
            o = fu > Float32InfBits ? (ushort)0x7E00 : (ushort)0x7C00;
        }
        else if (fu < (113u << 23))
        {
            float shifted = UInt32BitsToSingle(fu) + HalfDenormMagic;
            o = (ushort)(SingleToUInt32Bits(shifted) - HalfDenormMagicBits);
        }
        else
        {
            uint mantOdd = (fu >> 13) & 1u;
            fu = fu + (unchecked((uint)(15 - 127)) << 23) + 0xFFFu + mantOdd;
            o = (ushort)(fu >> 13);
        }

        return (ushort)(o | (sign >> 16));
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static byte FloatToUInt8Scaled(float value)
    {
        float v = (value * MaxUInt8AsFloat) + 0.5f;
        if (!(v > 0.0f))
        {
            return 0;
        }

        if (v >= MaxUInt8AsFloat)
        {
            return 255;
        }

        return (byte)(int)v;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static byte FloatToUInt8Unscaled(float value)
    {
        float v = value + 0.5f;
        if (!(v > 0.0f))
        {
            return 0;
        }

        if (v >= MaxUInt8AsFloat)
        {
            return 255;
        }

        return (byte)(int)v;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static ushort FloatToUInt16Scaled(float value)
    {
        float v = (value * MaxUInt16AsFloat) + 0.5f;
        if (!(v > 0.0f))
        {
            return 0;
        }

        if (v >= MaxUInt16AsFloat)
        {
            return 65535;
        }

        return (ushort)(int)v;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static ushort FloatToUInt16Unscaled(float value)
    {
        float v = value + 0.5f;
        if (!(v > 0.0f))
        {
            return 0;
        }

        if (v >= MaxUInt16AsFloat)
        {
            return 65535;
        }

        return (ushort)(int)v;
    }

    internal static bool TryDecodeAndFancyWeight4Ch(
        Span<float> dst,
        ReadOnlySpan<byte> srcBytes,
        int pixelCount,
        InternalPixelLayout layout,
        ResizeDataType dataType)
    {
        if (layout != InternalPixelLayout.Rgba || pixelCount <= 0)
        {
            return false;
        }

        switch (dataType)
        {
            case ResizeDataType.UInt8Srgb:
#if NET8_0_OR_GREATER
                if (TryDecodeRgba8ToFancyVector(dst, srcBytes, pixelCount, srgb: true))
                {
                    return true;
                }
#endif
                DecodeRgba8SrgbToFancy(dst, srcBytes, pixelCount);
                return true;
            case ResizeDataType.UInt8Linear:
#if NET8_0_OR_GREATER
                if (TryDecodeRgba8ToFancyVector(dst, srcBytes, pixelCount, srgb: false))
                {
                    return true;
                }
#endif
                DecodeRgba8LinearToFancy(dst, srcBytes, pixelCount);
                return true;
            case ResizeDataType.Fp32:
                DecodeRgbaFp32ToFancy(dst, MemoryMarshal.Cast<byte, float>(srcBytes), pixelCount);
                return true;
            default:
                return false;
        }
    }

#if NET8_0_OR_GREATER
    // Vector decoders for the eight-float fancy layout: one Vector256 per pixel. The source is viewed as
    // one uint per pixel and sliced to the destination length, so the loop has no bounds checks.
    private static bool TryDecodeRgba8ToFancyVector(Span<float> dst, ReadOnlySpan<byte> src, int pixelCount, bool srgb)
    {
        if (!Vector256.IsHardwareAccelerated || !BitConverter.IsLittleEndian)
        {
            return false;
        }

        Span<Vector256<float>> d = MemoryMarshal.Cast<float, Vector256<float>>(dst.Slice(0, pixelCount * FancyStride));
        ReadOnlySpan<uint> s = MemoryMarshal.Cast<byte, uint>(src.Slice(0, pixelCount * 4)).Slice(0, d.Length);
        if (srgb)
        {
            ReadOnlySpan<float> lut = SrgbToLinear.AsSpan(0, 256);
            ReadOnlySpan<float> lin = ByteToLinearFloat.AsSpan(0, 256);
            for (int p = 0; p < d.Length; p++)
            {
                uint v = s[p];
                float a = lin[(byte)(v >> 24)];
                d[p] = Fancy(Vector128.Create(lut[(byte)v], lut[(byte)(v >> 8)], lut[(byte)(v >> 16)], a));
            }
        }
        else
        {
            Vector128<float> scale = Vector128.Create(InvMaxUInt8);
            ReadOnlySpan<Vector128<byte>> s16 = MemoryMarshal.Cast<byte, Vector128<byte>>(src.Slice(0, pixelCount * 4));
            Span<Vector256<float>> d4 = d.Slice(0, s16.Length * 4);
            for (int q = 0; q < s16.Length; q++)
            {
                Vector128<byte> b16 = s16[q];
                Vector128<ushort> lo = Vector128.WidenLower(b16);
                Vector128<ushort> hi = Vector128.WidenUpper(b16);
                Span<Vector256<float>> o = d4.Slice(q * 4, 4);
                o[0] = Fancy(Vector128.ConvertToSingle(Vector128.WidenLower(lo).AsInt32()) * scale);
                o[1] = Fancy(Vector128.ConvertToSingle(Vector128.WidenUpper(lo).AsInt32()) * scale);
                o[2] = Fancy(Vector128.ConvertToSingle(Vector128.WidenLower(hi).AsInt32()) * scale);
                o[3] = Fancy(Vector128.ConvertToSingle(Vector128.WidenUpper(hi).AsInt32()) * scale);
            }

            for (int p = d4.Length; p < d.Length; p++)
            {
                uint v = s[p];
                d[p] = Fancy(Vector128.ConvertToSingle(
                    Vector128.Create((int)(v & 0xFF), (int)((v >> 8) & 0xFF), (int)((v >> 16) & 0xFF), (int)(v >> 24))) * scale);
            }
        }

        return true;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector256<float> Fancy(Vector128<float> rgba) =>
        Vector256.Create(rgba, rgba * Vector128.Create(rgba.GetElement(3)));

    // Straight RGBA of one fancy pixel given its reciprocal alpha (computed as 1.0f / alpha).
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector128<float> Unweight(Vector256<float> v, float alpha, float inv) =>
        alpha < AlphaTooSmallToDivide ? v.GetLower() : (v.GetUpper() * inv).WithElement(3, alpha);

    // Fused fancy unweight + RGBA8 encode (sRGB or linear) for the eight-float layout on AVX2.
    private static bool TryFancyUnweightAndEncodeRgba8(Span<byte> dst, ReadOnlySpan<float> src, int pixelCount, bool srgb)
    {
        if (!Avx2.IsSupported)
        {
            return false;
        }

        ReadOnlySpan<Vector256<float>> px = MemoryMarshal.Cast<float, Vector256<float>>(src.Slice(0, pixelCount * FancyStride));
        Span<byte> d = dst.Slice(0, pixelCount * 4);
        ReadOnlySpan<uint> tab = Fp32ToSrgb8Tab4.AsSpan(0, 128);
        Vector256<float> vMin = Vector256.Create(SrgbMinVal);
        Vector256<float> vMax = Vector256.Create(SrgbAlmostOne);
        Vector256<uint> vMinBits = Vector256.Create(SrgbMinValBits);
        Vector256<uint> vMask16 = Vector256.Create(0xFFFFu);
        Vector256<uint> vMask8 = Vector256.Create(0xFFu);
        Vector256<float> vZero = Vector256<float>.Zero;
        Vector256<float> v255 = Vector256.Create(MaxUInt8AsFloat);
        Vector256<float> vHalf = Vector256.Create(0.5f);
        int p = 0;
        for (; p + 8 <= pixelCount; p += 8)
        {
            ReadOnlySpan<Vector256<float>> b = px.Slice(p, 8);
            Vector256<float> a8 = Vector256.Create(
                b[0].GetElement(3), b[1].GetElement(3), b[2].GetElement(3), b[3].GetElement(3),
                b[4].GetElement(3), b[5].GetElement(3), b[6].GetElement(3), b[7].GetElement(3));
            Vector256<float> inv8 = Vector256<float>.One / a8;
            Vector256<float> p01 = Vector256.Create(Unweight(b[0], a8.GetElement(0), inv8.GetElement(0)), Unweight(b[1], a8.GetElement(1), inv8.GetElement(1)));
            Vector256<float> p23 = Vector256.Create(Unweight(b[2], a8.GetElement(2), inv8.GetElement(2)), Unweight(b[3], a8.GetElement(3), inv8.GetElement(3)));
            Vector256<float> p45 = Vector256.Create(Unweight(b[4], a8.GetElement(4), inv8.GetElement(4)), Unweight(b[5], a8.GetElement(5), inv8.GetElement(5)));
            Vector256<float> p67 = Vector256.Create(Unweight(b[6], a8.GetElement(6), inv8.GetElement(6)), Unweight(b[7], a8.GetElement(7), inv8.GetElement(7)));
            Vector256<int> q0, q1, q2, q3;
            if (srgb)
            {
                q0 = ConvertRgbaPairToSrgbInt32Avx2(p01, tab, vMin, vMax, vMinBits, vMask16, vMask8, vZero, v255, vHalf);
                q1 = ConvertRgbaPairToSrgbInt32Avx2(p23, tab, vMin, vMax, vMinBits, vMask16, vMask8, vZero, v255, vHalf);
                q2 = ConvertRgbaPairToSrgbInt32Avx2(p45, tab, vMin, vMax, vMinBits, vMask16, vMask8, vZero, v255, vHalf);
                q3 = ConvertRgbaPairToSrgbInt32Avx2(p67, tab, vMin, vMax, vMinBits, vMask16, vMask8, vZero, v255, vHalf);
            }
            else
            {
                q0 = Avx.ConvertToVector256Int32WithTruncation(Avx.Min(Avx.Max(Avx.Add(Avx.Multiply(p01, v255), vHalf), vZero), v255));
                q1 = Avx.ConvertToVector256Int32WithTruncation(Avx.Min(Avx.Max(Avx.Add(Avx.Multiply(p23, v255), vHalf), vZero), v255));
                q2 = Avx.ConvertToVector256Int32WithTruncation(Avx.Min(Avx.Max(Avx.Add(Avx.Multiply(p45, v255), vHalf), vZero), v255));
                q3 = Avx.ConvertToVector256Int32WithTruncation(Avx.Min(Avx.Max(Avx.Add(Avx.Multiply(p67, v255), vHalf), vZero), v255));
            }

            Span<byte> o = d.Slice(p * 4, 32);
            Vector256<short> a16 = Avx2.Permute4x64(Avx2.PackSignedSaturate(q0, q1).AsInt64(), 0xD8).AsInt16();
            Avx2.Permute4x64(Avx2.PackUnsignedSaturate(a16, a16).AsInt64(), 0xD8).AsByte().GetLower().CopyTo(o.Slice(0, 16));
            Vector256<short> b16 = Avx2.Permute4x64(Avx2.PackSignedSaturate(q2, q3).AsInt64(), 0xD8).AsInt16();
            Avx2.Permute4x64(Avx2.PackUnsignedSaturate(b16, b16).AsInt64(), 0xD8).AsByte().GetLower().CopyTo(o.Slice(16, 16));
        }

        for (; p < pixelCount; p++)
        {
            Vector256<float> v = px[p];
            float alpha = v.GetElement(3);
            Vector128<float> c = alpha < AlphaTooSmallToDivide
                ? v.GetLower()
                : (v.GetUpper() * (1.0f / alpha)).WithElement(3, alpha);
            Span<byte> o = d.Slice(p * 4, 4);
            if (srgb)
            {
                o[0] = LinearToSrgbByteFast(c.GetElement(0), tab);
                o[1] = LinearToSrgbByteFast(c.GetElement(1), tab);
                o[2] = LinearToSrgbByteFast(c.GetElement(2), tab);
            }
            else
            {
                o[0] = FloatToUInt8Scaled(c.GetElement(0));
                o[1] = FloatToUInt8Scaled(c.GetElement(1));
                o[2] = FloatToUInt8Scaled(c.GetElement(2));
            }

            o[3] = FloatToUInt8Scaled(alpha);
        }

        return true;
    }
#endif

    // Fused fancy-alpha unweight + encode directly from 7-channel float rows into RGBA destination bytes.
    // Eliminates the intermediate 7->4 rewrite of the vertical buffer, eliminates 4-byte alpha STLF stalls,
    // and computes 1/alpha across 8 pixels (AVX) or 4 pixels (Arm64) per single vector divide instruction.
    internal static bool TryFancyUnweightAndEncode4Ch(
        Span<byte> dstBytes,
        ReadOnlySpan<float> src7,
        int pixelCount,
        InternalPixelLayout outputLayout,
        ResizeDataType outputDataType)
    {
        if (outputLayout != InternalPixelLayout.Rgba || pixelCount <= 0)
        {
            return false;
        }

#if NET8_0_OR_GREATER
        switch (outputDataType)
        {
            case ResizeDataType.UInt8Srgb:
                return TryFancyUnweightAndEncodeRgba8(dstBytes, src7, pixelCount, srgb: true);
            case ResizeDataType.UInt8Linear:
                return TryFancyUnweightAndEncodeRgba8(dstBytes, src7, pixelCount, srgb: false);
        }
#endif
        return false;
    }

#if NET8_0_OR_GREATER
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector256<int> ConvertRgbaPairToSrgbInt32Avx2(
        Vector256<float> v,
        ReadOnlySpan<uint> tabRef,
        Vector256<float> vMin,
        Vector256<float> vMax,
        Vector256<uint> vMinBits,
        Vector256<uint> vMask16,
        Vector256<uint> vMask8,
        Vector256<float> vZero,
        Vector256<float> v255,
        Vector256<float> vHalf)
    {
        Vector256<float> vc = Avx.Min(Avx.Max(v, vMin), vMax);
        Vector256<uint> bits = vc.AsUInt32();
        Vector256<uint> idx = Avx2.ShiftRightLogical(Avx2.Subtract(bits, vMinBits), 20);
        Vector256<uint> tab = Vector256.Create(
            tabRef[(int)idx.GetElement(0) & 127],
            tabRef[(int)idx.GetElement(1) & 127],
            tabRef[(int)idx.GetElement(2) & 127],
            0u,
            tabRef[(int)idx.GetElement(4) & 127],
            tabRef[(int)idx.GetElement(5) & 127],
            tabRef[(int)idx.GetElement(6) & 127],
            0u);
        Vector256<uint> bias = Avx2.ShiftLeftLogical(Avx2.ShiftRightLogical(tab, 16), 9);
        Vector256<uint> scale = Avx2.And(tab, vMask16);
        Vector256<uint> t = Avx2.And(Avx2.ShiftRightLogical(bits, 12), vMask8);
        Vector256<int> srgbRes = Avx2.ShiftRightLogical(Avx2.Add(bias, Avx2.MultiplyLow(scale, t)), 16).AsInt32();

        Vector256<float> linF = Avx.Min(Avx.Max(Avx.Add(Avx.Multiply(v, v255), vHalf), vZero), v255);
        Vector256<int> linRes = Avx.ConvertToVector256Int32WithTruncation(linF);
        return Avx.Blend(srgbRes.AsSingle(), linRes.AsSingle(), 0x88).AsInt32();
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector128<float> Unweight1PixelSafeArm64(ReadOnlySpan<float> sRef, int sOff)
    {
        Vector128<float> raw = Vector128.Create(sRef.Slice(sOff, 4));
        float alpha = raw.GetElement(3);
        if (alpha < AlphaTooSmallToDivide)
        {
            return raw;
        }

        Vector128<float> pm = Vector128.Create(sRef.Slice(sOff + 4, 4));
        return AdvSimd.Multiply(pm, Vector128.Create(1.0f / alpha)).WithElement(3, alpha);
    }
#endif

    internal static void DecodePixels(
        Span<float> dst,
        ReadOnlySpan<byte> srcBytes,
        int pixelCount,
        int channels,
        InternalPixelLayout layout,
        ResizeDataType dataType,
        bool unscaled)
    {
        if (pixelCount <= 0)
        {
            return;
        }

        bool hasAlpha = ResizeGeometry.HasAlphaChannel(layout);
        ChannelSwizzle swizzle = ResizeGeometry.GetSwizzle(layout);
        int sampleCount = pixelCount * channels;

        switch (dataType)
        {
            case ResizeDataType.UInt8Linear:
                DecodeUInt8Linear(dst, srcBytes, pixelCount, sampleCount, swizzle, unscaled);
                break;
            case ResizeDataType.UInt8Srgb:
                DecodeUInt8Srgb(dst, srcBytes, pixelCount, channels, sampleCount, hasAlpha, swizzle);
                break;
            case ResizeDataType.UInt8SrgbAlpha:
                DecodeUInt8SrgbAll(dst, srcBytes, pixelCount, sampleCount, swizzle);
                break;
            case ResizeDataType.UInt16Linear:
                DecodeUInt16Linear(dst, MemoryMarshal.Cast<byte, ushort>(srcBytes), pixelCount, sampleCount, swizzle, unscaled);
                break;
            case ResizeDataType.Fp32:
                DecodeFloat32(dst, MemoryMarshal.Cast<byte, float>(srcBytes), pixelCount, sampleCount, swizzle);
                break;
            case ResizeDataType.Fp16:
                DecodeFloat16(dst, MemoryMarshal.Cast<byte, ushort>(srcBytes), pixelCount, sampleCount, swizzle);
                break;
        }
    }

    internal static void EncodePixels(
        Span<byte> dstBytes,
        ReadOnlySpan<float> src,
        int pixelCount,
        int channels,
        InternalPixelLayout layout,
        ResizeDataType dataType,
        bool unscaled)
    {
        if (pixelCount <= 0)
        {
            return;
        }

        bool hasAlpha = ResizeGeometry.HasAlphaChannel(layout);
        ChannelSwizzle swizzle = ResizeGeometry.GetSwizzle(layout);
        int sampleCount = pixelCount * channels;

        switch (dataType)
        {
            case ResizeDataType.UInt8Linear:
                EncodeUInt8Linear(dstBytes, src, pixelCount, sampleCount, swizzle, unscaled);
                break;
            case ResizeDataType.UInt8Srgb:
                EncodeUInt8Srgb(dstBytes, src, pixelCount, channels, sampleCount, hasAlpha, swizzle);
                break;
            case ResizeDataType.UInt8SrgbAlpha:
                EncodeUInt8SrgbAll(dstBytes, src, pixelCount, sampleCount, swizzle);
                break;
            case ResizeDataType.UInt16Linear:
                EncodeUInt16Linear(MemoryMarshal.Cast<byte, ushort>(dstBytes), src, pixelCount, sampleCount, swizzle, unscaled);
                break;
            case ResizeDataType.Fp32:
                EncodeFloat32(MemoryMarshal.Cast<byte, float>(dstBytes), src, pixelCount, sampleCount, swizzle);
                break;
            case ResizeDataType.Fp16:
                EncodeFloat16(MemoryMarshal.Cast<byte, ushort>(dstBytes), src, pixelCount, sampleCount, swizzle);
                break;
        }
    }

    internal static void ApplyAlphaWeight(Span<float> buffer, int pixelCount, AlphaWeightingKind kind, int channels, bool flip3Ch)
    {
        if (flip3Ch)
        {
            SimpleFlip3Ch(buffer, pixelCount);
            return;
        }

        switch (kind)
        {
            case AlphaWeightingKind.FancyBoth:
                if (channels == 4)
                {
                    FancyAlphaWeight4Ch(buffer, pixelCount);
                }
                else
                {
                    FancyAlphaWeight2Ch(buffer, pixelCount);
                }

                break;

            case AlphaWeightingKind.WeightInOnly:
            case AlphaWeightingKind.SimpleBoth:
                if (channels == 4)
                {
                    SimpleAlphaWeight4Ch(buffer, pixelCount);
                }
                else
                {
                    SimpleAlphaWeight2Ch(buffer, pixelCount);
                }

                break;
        }
    }

    internal static void ApplyAlphaUnweight(Span<float> buffer, int pixelCount, AlphaWeightingKind kind, int channels, bool flip3Ch)
    {
        if (flip3Ch)
        {
            SimpleFlip3Ch(buffer, pixelCount);
            return;
        }

        switch (kind)
        {
            case AlphaWeightingKind.FancyBoth:
                if (channels == 4)
                {
                    FancyAlphaUnweight4Ch(buffer, pixelCount);
                }
                else
                {
                    FancyAlphaUnweight2Ch(buffer, pixelCount);
                }

                break;

            case AlphaWeightingKind.UnweightOutOnly:
            case AlphaWeightingKind.SimpleBoth:
                if (channels == 4)
                {
                    SimpleAlphaUnweight4Ch(buffer, pixelCount);
                }
                else
                {
                    SimpleAlphaUnweight2Ch(buffer, pixelCount);
                }

                break;
        }
    }

    private static void DecodeUInt8Linear(
        Span<float> dst,
        ReadOnlySpan<byte> src,
        int pixelCount,
        int sampleCount,
        ChannelSwizzle swizzle,
        bool unscaled)
    {
        if (swizzle == ChannelSwizzle.Identity)
        {
            int i = 0;
#if NET8_0_OR_GREATER
            var sRef = src;
            var dRef = dst;
            if (Avx2.IsSupported && sampleCount >= 16)
            {
                Vector256<float> vScale = Vector256.Create(InvMaxUInt8);
                int limit = sampleCount - 15;
                if (unscaled)
                {
                    for (; i < limit; i += 16)
                    {
                        var dRef_i_w12 = dRef.Slice(i, 16);
                        Vector128<byte> b16 = Vector128.Create(sRef.Slice(i, 16));
                        Vector256<float> f0 = Avx.ConvertToVector256Single(Avx2.ConvertToVector256Int32(b16));
                        Vector256<float> f1 = Avx.ConvertToVector256Single(Avx2.ConvertToVector256Int32(Sse2.ShiftRightLogical128BitLane(b16, 8)));
                        f0.CopyTo(dRef_i_w12.Slice(0, 8));
                        f1.CopyTo(dRef_i_w12.Slice(8, 8));
                    }
                }
                else
                {
                    for (; i < limit; i += 16)
                    {
                        var dRef_i_w13 = dRef.Slice(i, 16);
                        Vector128<byte> b16 = Vector128.Create(sRef.Slice(i, 16));
                        Vector256<float> f0 = Avx.Multiply(Avx.ConvertToVector256Single(Avx2.ConvertToVector256Int32(b16)), vScale);
                        Vector256<float> f1 = Avx.Multiply(Avx.ConvertToVector256Single(Avx2.ConvertToVector256Int32(Sse2.ShiftRightLogical128BitLane(b16, 8))), vScale);
                        f0.CopyTo(dRef_i_w13.Slice(0, 8));
                        f1.CopyTo(dRef_i_w13.Slice(8, 8));
                    }
                }
            }
            else if (Sse2.IsSupported && sampleCount >= 16)
            {
                Vector128<byte> zeroB = Vector128<byte>.Zero;
                Vector128<ushort> zeroU = Vector128<ushort>.Zero;
                Vector128<float> vScale = Vector128.Create(InvMaxUInt8);
                int limit = sampleCount - 15;
                for (; i < limit; i += 16)
                {
                    var dRef_i_w14 = dRef.Slice(i, 16);
                    Vector128<byte> b16 = Vector128.Create(sRef.Slice(i, 16));
                    Vector128<ushort> u0 = Sse2.UnpackLow(b16, zeroB).AsUInt16();
                    Vector128<ushort> u1 = Sse2.UnpackHigh(b16, zeroB).AsUInt16();
                    Vector128<float> f0 = Sse2.ConvertToVector128Single(Sse2.UnpackLow(u0, zeroU).AsInt32());
                    Vector128<float> f1 = Sse2.ConvertToVector128Single(Sse2.UnpackHigh(u0, zeroU).AsInt32());
                    Vector128<float> f2 = Sse2.ConvertToVector128Single(Sse2.UnpackLow(u1, zeroU).AsInt32());
                    Vector128<float> f3 = Sse2.ConvertToVector128Single(Sse2.UnpackHigh(u1, zeroU).AsInt32());
                    if (!unscaled)
                    {
                        f0 = Sse.Multiply(f0, vScale);
                        f1 = Sse.Multiply(f1, vScale);
                        f2 = Sse.Multiply(f2, vScale);
                        f3 = Sse.Multiply(f3, vScale);
                    }

                    f0.CopyTo(dRef_i_w14.Slice(0, 4));
                    f1.CopyTo(dRef_i_w14.Slice(4, 4));
                    f2.CopyTo(dRef_i_w14.Slice(8, 4));
                    f3.CopyTo(dRef_i_w14.Slice(12, 4));
                }
            }
            else if (AdvSimd.IsSupported && sampleCount >= 8)
            {
                Vector128<float> vScale = Vector128.Create(InvMaxUInt8);
                int limit = sampleCount - 7;
                for (; i < limit; i += 8)
                {
                    var dRef_i_w15 = dRef.Slice(i, 8);
                    Vector128<ushort> u16 = AdvSimd.ZeroExtendWideningLower(Vector64.Create(sRef.Slice(i, 8)));
                    Vector128<float> f0 = AdvSimd.ConvertToSingle(AdvSimd.ZeroExtendWideningLower(u16.GetLower()).AsInt32());
                    Vector128<float> f1 = AdvSimd.ConvertToSingle(AdvSimd.ZeroExtendWideningLower(u16.GetUpper()).AsInt32());
                    if (!unscaled)
                    {
                        f0 = AdvSimd.Multiply(f0, vScale);
                        f1 = AdvSimd.Multiply(f1, vScale);
                    }

                    f0.CopyTo(dRef_i_w15.Slice(0, 4));
                    f1.CopyTo(dRef_i_w15.Slice(4, 4));
                }
            }
#endif
            if (unscaled)
            {
                for (; i < sampleCount; i++)
                {
                    dst[i] = src[i];
                }
            }
            else
            {
                for (; i < sampleCount; i++)
                {
                    dst[i] = src[i] * InvMaxUInt8;
                }
            }

            return;
        }

        if (swizzle == ChannelSwizzle.Ar)
        {
            for (int p = 0; p < pixelCount; p++)
            {
                int o = p * 2;
                if (unscaled)
                {
                    dst[o] = src[o + 1];
                    dst[o + 1] = src[o];
                }
                else
                {
                    dst[o] = src[o + 1] * InvMaxUInt8;
                    dst[o + 1] = src[o] * InvMaxUInt8;
                }
            }

            return;
        }

        int pStart = 0;
#if NET8_0_OR_GREATER
        if (Avx2.IsSupported && Ssse3.IsSupported && pixelCount >= 4)
        {
            Vector128<byte> mask = GetDecodeSwizzleMask16(swizzle);
            Vector256<float> vScale = Vector256.Create(InvMaxUInt8);
            var sRef = src;
            var dRef = dst;
            int limit = pixelCount - 3;
            for (; pStart < limit; pStart += 4)
            {
                int off = (pStart * 4);
                Vector128<byte> b16 = Ssse3.Shuffle(Vector128.Create(sRef.Slice(off, 16)), mask);
                Vector256<float> f0 = Avx.ConvertToVector256Single(Avx2.ConvertToVector256Int32(b16));
                Vector256<float> f1 = Avx.ConvertToVector256Single(Avx2.ConvertToVector256Int32(Sse2.ShiftRightLogical128BitLane(b16, 8)));
                if (!unscaled)
                {
                    f0 = Avx.Multiply(f0, vScale);
                    f1 = Avx.Multiply(f1, vScale);
                }

                f0.CopyTo(dRef.Slice(off, 8));
                f1.CopyTo(dRef.Slice(off + 8, 8));
            }
        }
#endif
        GetDecodeOrder4(swizzle, out int o0, out int o1, out int o2, out int o3);
        for (int p = pStart; p < pixelCount; p++)
        {
            int o = p * 4;
            if (unscaled)
            {
                dst[o] = src[o + o0];
                dst[o + 1] = src[o + o1];
                dst[o + 2] = src[o + o2];
                dst[o + 3] = src[o + o3];
            }
            else
            {
                dst[o] = src[o + o0] * InvMaxUInt8;
                dst[o + 1] = src[o + o1] * InvMaxUInt8;
                dst[o + 2] = src[o + o2] * InvMaxUInt8;
                dst[o + 3] = src[o + o3] * InvMaxUInt8;
            }
        }
    }

    private static void DecodeUInt8Srgb(
        Span<float> dst,
        ReadOnlySpan<byte> src,
        int pixelCount,
        int channels,
        int sampleCount,
        bool hasAlpha,
        ChannelSwizzle swizzle)
    {
#if NET8_0_OR_GREATER
        ReadOnlySpan<float> lutRef = SrgbToLinear.AsSpan(0, 256);
        var sRef = src;
        var dRef = dst;

        if (!hasAlpha)
        {
            int i = 0;
            int limit = sampleCount - 3;
            for (; i < limit; i += 4)
            {
                dRef[i] = lutRef[sRef[i]];
                dRef[i + 1] = lutRef[sRef[i + 1]];
                dRef[i + 2] = lutRef[sRef[i + 2]];
                dRef[i + 3] = lutRef[sRef[i + 3]];
            }

            for (; i < sampleCount; i++)
            {
                dRef[i] = lutRef[sRef[i]];
            }

            return;
        }

        if (channels == 4 && swizzle == ChannelSwizzle.Identity)
        {
            int p = 0;
            int limit = pixelCount - 1;
            for (; p < limit; p += 2)
            {
                int o = p * 4;
                dRef[o] = lutRef[sRef[o]];
                dRef[o + 1] = lutRef[sRef[o + 1]];
                dRef[o + 2] = lutRef[sRef[o + 2]];
                dRef[o + 3] = sRef[o + 3] * InvMaxUInt8;
                dRef[o + 4] = lutRef[sRef[o + 4]];
                dRef[o + 5] = lutRef[sRef[o + 5]];
                dRef[o + 6] = lutRef[sRef[o + 6]];
                dRef[o + 7] = sRef[o + 7] * InvMaxUInt8;
            }

            if (p < pixelCount)
            {
                int o = p * 4;
                dRef[o] = lutRef[sRef[o]];
                dRef[o + 1] = lutRef[sRef[o + 1]];
                dRef[o + 2] = lutRef[sRef[o + 2]];
                dRef[o + 3] = sRef[o + 3] * InvMaxUInt8;
            }

            return;
        }
#endif
        float[] lut = SrgbToLinear;

        if (!hasAlpha)
        {
            for (int i = 0; i < sampleCount; i++)
            {
                dst[i] = lut[src[i]];
            }

            return;
        }

        if (channels == 2)
        {
            if (swizzle == ChannelSwizzle.Ar)
            {
                for (int p = 0; p < pixelCount; p++)
                {
                    int o = p * 2;
                    dst[o] = lut[src[o + 1]];
                    dst[o + 1] = src[o] * InvMaxUInt8;
                }
            }
            else
            {
                for (int p = 0; p < pixelCount; p++)
                {
                    int o = p * 2;
                    dst[o] = lut[src[o]];
                    dst[o + 1] = src[o + 1] * InvMaxUInt8;
                }
            }

            return;
        }

        GetDecodeOrder4(swizzle, out int o0, out int o1, out int o2, out int o3);
        for (int p = 0; p < pixelCount; p++)
        {
            int o = p * 4;
            dst[o] = lut[src[o + o0]];
            dst[o + 1] = lut[src[o + o1]];
            dst[o + 2] = lut[src[o + o2]];
            dst[o + 3] = src[o + o3] * InvMaxUInt8;
        }
    }

    private static void DecodeUInt8SrgbAll(
        Span<float> dst,
        ReadOnlySpan<byte> src,
        int pixelCount,
        int sampleCount,
        ChannelSwizzle swizzle)
    {
        float[] lut = SrgbToLinear;

        if (swizzle == ChannelSwizzle.Identity)
        {
            for (int i = 0; i < sampleCount; i++)
            {
                dst[i] = lut[src[i]];
            }

            return;
        }

        if (swizzle == ChannelSwizzle.Ar)
        {
            for (int p = 0; p < pixelCount; p++)
            {
                int o = p * 2;
                dst[o] = lut[src[o + 1]];
                dst[o + 1] = lut[src[o]];
            }

            return;
        }

        GetDecodeOrder4(swizzle, out int o0, out int o1, out int o2, out int o3);
        for (int p = 0; p < pixelCount; p++)
        {
            int o = p * 4;
            dst[o] = lut[src[o + o0]];
            dst[o + 1] = lut[src[o + o1]];
            dst[o + 2] = lut[src[o + o2]];
            dst[o + 3] = lut[src[o + o3]];
        }
    }

    private static void DecodeUInt16Linear(
        Span<float> dst,
        ReadOnlySpan<ushort> src,
        int pixelCount,
        int sampleCount,
        ChannelSwizzle swizzle,
        bool unscaled)
    {
        if (swizzle == ChannelSwizzle.Identity)
        {
            int i = 0;
#if NET8_0_OR_GREATER
            var sRef = src;
            var dRef = dst;
            if (Avx2.IsSupported && sampleCount >= 8)
            {
                Vector256<float> vScale = Vector256.Create(InvMaxUInt16);
                int limit = sampleCount - 7;
                if (unscaled)
                {
                    for (; i < limit; i += 8)
                    {
                        Vector256<float> f0 = Avx.ConvertToVector256Single(Avx2.ConvertToVector256Int32(Vector128.Create(sRef.Slice(i, 8))));
                        f0.CopyTo(dRef.Slice(i, 8));
                    }
                }
                else
                {
                    for (; i < limit; i += 8)
                    {
                        Vector256<float> f0 = Avx.Multiply(Avx.ConvertToVector256Single(Avx2.ConvertToVector256Int32(Vector128.Create(sRef.Slice(i, 8)))), vScale);
                        f0.CopyTo(dRef.Slice(i, 8));
                    }
                }
            }
            else if (Sse2.IsSupported && sampleCount >= 8)
            {
                Vector128<ushort> zero = Vector128<ushort>.Zero;
                Vector128<float> vScale = Vector128.Create(InvMaxUInt16);
                int limit = sampleCount - 7;
                for (; i < limit; i += 8)
                {
                    var dRef_i_w16 = dRef.Slice(i, 8);
                    Vector128<ushort> u8 = Vector128.Create(sRef.Slice(i, 8));
                    Vector128<float> f0 = Sse2.ConvertToVector128Single(Sse2.UnpackLow(u8, zero).AsInt32());
                    Vector128<float> f1 = Sse2.ConvertToVector128Single(Sse2.UnpackHigh(u8, zero).AsInt32());
                    if (!unscaled)
                    {
                        f0 = Sse.Multiply(f0, vScale);
                        f1 = Sse.Multiply(f1, vScale);
                    }

                    f0.CopyTo(dRef_i_w16.Slice(0, 4));
                    f1.CopyTo(dRef_i_w16.Slice(4, 4));
                }
            }
            else if (AdvSimd.IsSupported && sampleCount >= 4)
            {
                Vector128<float> vScale = Vector128.Create(InvMaxUInt16);
                int limit = sampleCount - 3;
                for (; i < limit; i += 4)
                {
                    Vector128<float> f0 = AdvSimd.ConvertToSingle(AdvSimd.ZeroExtendWideningLower(Vector64.Create(sRef.Slice(i, 4))).AsInt32());
                    if (!unscaled)
                    {
                        f0 = AdvSimd.Multiply(f0, vScale);
                    }

                    f0.CopyTo(dRef.Slice(i, 4));
                }
            }
#endif
            if (unscaled)
            {
                for (; i < sampleCount; i++)
                {
                    dst[i] = src[i];
                }
            }
            else
            {
                for (; i < sampleCount; i++)
                {
                    dst[i] = src[i] * InvMaxUInt16;
                }
            }

            return;
        }

        if (swizzle == ChannelSwizzle.Ar)
        {
            for (int p = 0; p < pixelCount; p++)
            {
                int o = p * 2;
                if (unscaled)
                {
                    dst[o] = src[o + 1];
                    dst[o + 1] = src[o];
                }
                else
                {
                    dst[o] = src[o + 1] * InvMaxUInt16;
                    dst[o + 1] = src[o] * InvMaxUInt16;
                }
            }

            return;
        }

        GetDecodeOrder4(swizzle, out int o0, out int o1, out int o2, out int o3);
        for (int p = 0; p < pixelCount; p++)
        {
            int o = p * 4;
            if (unscaled)
            {
                dst[o] = src[o + o0];
                dst[o + 1] = src[o + o1];
                dst[o + 2] = src[o + o2];
                dst[o + 3] = src[o + o3];
            }
            else
            {
                dst[o] = src[o + o0] * InvMaxUInt16;
                dst[o + 1] = src[o + o1] * InvMaxUInt16;
                dst[o + 2] = src[o + o2] * InvMaxUInt16;
                dst[o + 3] = src[o + o3] * InvMaxUInt16;
            }
        }
    }

    private static void DecodeFloat32(
        Span<float> dst,
        ReadOnlySpan<float> src,
        int pixelCount,
        int sampleCount,
        ChannelSwizzle swizzle)
    {
        if (swizzle == ChannelSwizzle.Identity)
        {
            src.Slice(0, sampleCount).CopyTo(dst);
            return;
        }

        if (swizzle == ChannelSwizzle.Ar)
        {
            for (int p = 0; p < pixelCount; p++)
            {
                int o = p * 2;
                dst[o] = src[o + 1];
                dst[o + 1] = src[o];
            }

            return;
        }

        GetDecodeOrder4(swizzle, out int o0, out int o1, out int o2, out int o3);
        for (int p = 0; p < pixelCount; p++)
        {
            int o = p * 4;
            dst[o] = src[o + o0];
            dst[o + 1] = src[o + o1];
            dst[o + 2] = src[o + o2];
            dst[o + 3] = src[o + o3];
        }
    }

    private static void DecodeFloat16(
        Span<float> dst,
        ReadOnlySpan<ushort> src,
        int pixelCount,
        int sampleCount,
        ChannelSwizzle swizzle)
    {
        if (swizzle == ChannelSwizzle.Identity)
        {
            for (int i = 0; i < sampleCount; i++)
            {
                dst[i] = HalfToFloat(src[i]);
            }

            return;
        }

        if (swizzle == ChannelSwizzle.Ar)
        {
            for (int p = 0; p < pixelCount; p++)
            {
                int o = p * 2;
                dst[o] = HalfToFloat(src[o + 1]);
                dst[o + 1] = HalfToFloat(src[o]);
            }

            return;
        }

        GetDecodeOrder4(swizzle, out int o0, out int o1, out int o2, out int o3);
        for (int p = 0; p < pixelCount; p++)
        {
            int o = p * 4;
            dst[o] = HalfToFloat(src[o + o0]);
            dst[o + 1] = HalfToFloat(src[o + o1]);
            dst[o + 2] = HalfToFloat(src[o + o2]);
            dst[o + 3] = HalfToFloat(src[o + o3]);
        }
    }

    private static void EncodeUInt8Linear(
        Span<byte> dst,
        ReadOnlySpan<float> src,
        int pixelCount,
        int sampleCount,
        ChannelSwizzle swizzle,
        bool unscaled)
    {
        if (swizzle == ChannelSwizzle.Identity)
        {
            int i = 0;
#if NET8_0_OR_GREATER
            var sRef = src;
            var dRef = dst;
            if (Avx2.IsSupported && sampleCount >= 16)
            {
                Vector256<float> vZero = Vector256<float>.Zero;
                Vector256<float> vMax = Vector256.Create(MaxUInt8AsFloat);
                Vector256<float> vHalf = Vector256.Create(0.5f);
                int limit = sampleCount - 15;
                for (; i < limit; i += 16)
                {
                    var sRef_i_w17 = sRef.Slice(i, 16);
                    Vector256<float> f0 = Vector256.Create(sRef_i_w17.Slice(0, 8));
                    Vector256<float> f1 = Vector256.Create(sRef_i_w17.Slice(8, 8));
                    if (!unscaled)
                    {
                        f0 = Avx.Multiply(f0, vMax);
                        f1 = Avx.Multiply(f1, vMax);
                    }

                    f0 = Avx.Min(Avx.Max(Avx.Add(f0, vHalf), vZero), vMax);
                    f1 = Avx.Min(Avx.Max(Avx.Add(f1, vHalf), vZero), vMax);
                    Vector256<int> i0 = Avx.ConvertToVector256Int32WithTruncation(f0);
                    Vector256<int> i1 = Avx.ConvertToVector256Int32WithTruncation(f1);
                    Vector256<short> p16 = Avx2.Permute4x64(Avx2.PackSignedSaturate(i0, i1).AsInt64(), 0xD8).AsInt16();
                    Vector256<byte> p8 = Avx2.Permute4x64(Avx2.PackUnsignedSaturate(p16, p16).AsInt64(), 0xD8).AsByte();
                    p8.GetLower().CopyTo(dRef.Slice(i, 16));
                }
            }
            else if (Sse2.IsSupported && sampleCount >= 8)
            {
                Vector128<float> vZero = Vector128<float>.Zero;
                Vector128<float> vMax = Vector128.Create(MaxUInt8AsFloat);
                Vector128<float> vHalf = Vector128.Create(0.5f);
                int limit = sampleCount - 7;
                for (; i < limit; i += 8)
                {
                    var sRef_i_w18 = sRef.Slice(i, 8);
                    Vector128<float> f0 = Vector128.Create(sRef_i_w18.Slice(0, 4));
                    Vector128<float> f1 = Vector128.Create(sRef_i_w18.Slice(4, 4));
                    if (!unscaled)
                    {
                        f0 = Sse.Multiply(f0, vMax);
                        f1 = Sse.Multiply(f1, vMax);
                    }

                    f0 = Sse.Min(Sse.Max(Sse.Add(f0, vHalf), vZero), vMax);
                    f1 = Sse.Min(Sse.Max(Sse.Add(f1, vHalf), vZero), vMax);
                    Vector128<short> p16 = Sse2.PackSignedSaturate(
                        Sse2.ConvertToVector128Int32WithTruncation(f0),
                        Sse2.ConvertToVector128Int32WithTruncation(f1));
                    Vector128<byte> p8 = Sse2.PackUnsignedSaturate(p16, p16);
                    p8.GetLower().CopyTo(dRef.Slice(i, 8));
                }
            }
            else if (AdvSimd.IsSupported && sampleCount >= 8)
            {
                Vector128<float> vZero = Vector128<float>.Zero;
                Vector128<float> vMax = Vector128.Create(MaxUInt8AsFloat);
                Vector128<float> vHalf = Vector128.Create(0.5f);
                int limit = sampleCount - 7;
                for (; i < limit; i += 8)
                {
                    var sRef_i_w19 = sRef.Slice(i, 8);
                    Vector128<float> f0 = Vector128.Create(sRef_i_w19.Slice(0, 4));
                    Vector128<float> f1 = Vector128.Create(sRef_i_w19.Slice(4, 4));
                    if (!unscaled)
                    {
                        f0 = AdvSimd.Multiply(f0, vMax);
                        f1 = AdvSimd.Multiply(f1, vMax);
                    }

                    f0 = AdvSimd.Min(AdvSimd.Max(AdvSimd.Add(f0, vHalf), vZero), vMax);
                    f1 = AdvSimd.Min(AdvSimd.Max(AdvSimd.Add(f1, vHalf), vZero), vMax);
                    Vector64<ushort> u0 = AdvSimd.ExtractNarrowingLower(AdvSimd.ConvertToInt32RoundToZero(f0).AsUInt32());
                    Vector64<ushort> u1 = AdvSimd.ExtractNarrowingLower(AdvSimd.ConvertToInt32RoundToZero(f1).AsUInt32());
                    Vector64<byte> p8 = AdvSimd.ExtractNarrowingLower(Vector128.Create(u0, u1));
                    p8.CopyTo(dRef.Slice(i, 8));
                }
            }
#endif
            if (unscaled)
            {
                for (; i < sampleCount; i++)
                {
                    dst[i] = FloatToUInt8Unscaled(src[i]);
                }
            }
            else
            {
                for (; i < sampleCount; i++)
                {
                    dst[i] = FloatToUInt8Scaled(src[i]);
                }
            }

            return;
        }

        if (swizzle == ChannelSwizzle.Ar)
        {
            for (int p = 0; p < pixelCount; p++)
            {
                int o = p * 2;
                if (unscaled)
                {
                    dst[o] = FloatToUInt8Unscaled(src[o + 1]);
                    dst[o + 1] = FloatToUInt8Unscaled(src[o]);
                }
                else
                {
                    dst[o] = FloatToUInt8Scaled(src[o + 1]);
                    dst[o + 1] = FloatToUInt8Scaled(src[o]);
                }
            }

            return;
        }

        int pStart = 0;
#if NET8_0_OR_GREATER
        if (Avx2.IsSupported && Ssse3.IsSupported && pixelCount >= 4)
        {
            Vector128<byte> mask = GetEncodeSwizzleMask16(swizzle);
            Vector256<float> vZero = Vector256<float>.Zero;
            Vector256<float> vMax = Vector256.Create(MaxUInt8AsFloat);
            Vector256<float> vHalf = Vector256.Create(0.5f);
            var sRef = src;
            var dRef = dst;
            int limit = pixelCount - 3;
            for (; pStart < limit; pStart += 4)
            {
                int off = (pStart * 4);
                Vector256<float> f0 = Vector256.Create(sRef.Slice(off, 8));
                Vector256<float> f1 = Vector256.Create(sRef.Slice(off + 8, 8));
                if (!unscaled)
                {
                    f0 = Avx.Multiply(f0, vMax);
                    f1 = Avx.Multiply(f1, vMax);
                }

                f0 = Avx.Min(Avx.Max(Avx.Add(f0, vHalf), vZero), vMax);
                f1 = Avx.Min(Avx.Max(Avx.Add(f1, vHalf), vZero), vMax);
                Vector256<int> i0 = Avx.ConvertToVector256Int32WithTruncation(f0);
                Vector256<int> i1 = Avx.ConvertToVector256Int32WithTruncation(f1);
                Vector256<short> p16 = Avx2.Permute4x64(Avx2.PackSignedSaturate(i0, i1).AsInt64(), 0xD8).AsInt16();
                Vector256<byte> p8 = Avx2.Permute4x64(Avx2.PackUnsignedSaturate(p16, p16).AsInt64(), 0xD8).AsByte();
                Ssse3.Shuffle(p8.GetLower(), mask).CopyTo(dRef.Slice(off, 16));
            }
        }
#endif
        GetEncodeOrder4(swizzle, out int e0, out int e1, out int e2, out int e3);
        for (int p = pStart; p < pixelCount; p++)
        {
            int o = p * 4;
            if (unscaled)
            {
                dst[o] = FloatToUInt8Unscaled(src[o + e0]);
                dst[o + 1] = FloatToUInt8Unscaled(src[o + e1]);
                dst[o + 2] = FloatToUInt8Unscaled(src[o + e2]);
                dst[o + 3] = FloatToUInt8Unscaled(src[o + e3]);
            }
            else
            {
                dst[o] = FloatToUInt8Scaled(src[o + e0]);
                dst[o + 1] = FloatToUInt8Scaled(src[o + e1]);
                dst[o + 2] = FloatToUInt8Scaled(src[o + e2]);
                dst[o + 3] = FloatToUInt8Scaled(src[o + e3]);
            }
        }
    }

    private static void EncodeUInt8Srgb(
        Span<byte> dst,
        ReadOnlySpan<float> src,
        int pixelCount,
        int channels,
        int sampleCount,
        bool hasAlpha,
        ChannelSwizzle swizzle)
    {
#if NET8_0_OR_GREATER
        ReadOnlySpan<uint> tabRef = Fp32ToSrgb8Tab4.AsSpan(0, 128);
        var sRef = src;
        var dRef = dst;

        if (!hasAlpha)
        {
            int i = 0;
            if (Avx2.IsSupported && sampleCount >= 8)
            {
                Vector256<float> vMin = Vector256.Create(SrgbMinVal);
                Vector256<float> vMax = Vector256.Create(SrgbAlmostOne);
                Vector256<uint> vMinBits = Vector256.Create(SrgbMinValBits);
                Vector256<uint> vMask16 = Vector256.Create(0xFFFFu);
                Vector256<uint> vMask8 = Vector256.Create(0xFFu);
                int limit8 = sampleCount - 7;
                for (; i < limit8; i += 8)
                {
                    Vector256<float> vc = Avx.Min(Avx.Max(Vector256.Create(sRef.Slice(i, 8)), vMin), vMax);
                    Vector256<uint> bits = vc.AsUInt32();
                    Vector256<uint> idx = Avx2.ShiftRightLogical(Avx2.Subtract(bits, vMinBits), 20);
                    Vector256<uint> tab = Vector256.Create(
                        tabRef[(int)idx.GetElement(0) & 127],
                        tabRef[(int)idx.GetElement(1) & 127],
                        tabRef[(int)idx.GetElement(2) & 127],
                        tabRef[(int)idx.GetElement(3) & 127],
                        tabRef[(int)idx.GetElement(4) & 127],
                        tabRef[(int)idx.GetElement(5) & 127],
                        tabRef[(int)idx.GetElement(6) & 127],
                        tabRef[(int)idx.GetElement(7) & 127]);
                    Vector256<uint> bias = Avx2.ShiftLeftLogical(Avx2.ShiftRightLogical(tab, 16), 9);
                    Vector256<uint> scale = Avx2.And(tab, vMask16);
                    Vector256<uint> t = Avx2.And(Avx2.ShiftRightLogical(bits, 12), vMask8);
                    Vector256<int> res = Avx2.ShiftRightLogical(Avx2.Add(bias, Avx2.MultiplyLow(scale, t)), 16).AsInt32();
                    Vector256<short> p16 = Avx2.Permute4x64(Avx2.PackSignedSaturate(res, res).AsInt64(), 0xD8).AsInt16();
                    Vector128<byte> p8 = Sse2.PackUnsignedSaturate(p16.GetLower(), p16.GetLower());
                    p8.GetLower().CopyTo(dRef.Slice(i, 8));
                }
            }

            for (; i < sampleCount; i++)
            {
                dRef[i] = LinearToSrgbByteFast(sRef[i], tabRef);
            }

            return;
        }

        if (channels == 4 && swizzle == ChannelSwizzle.Identity)
        {
            int p = 0;
            if (Avx2.IsSupported && pixelCount >= 4)
            {
                Vector256<float> vMin = Vector256.Create(SrgbMinVal);
                Vector256<float> vMax = Vector256.Create(SrgbAlmostOne);
                Vector256<uint> vMinBits = Vector256.Create(SrgbMinValBits);
                Vector256<uint> vMask16 = Vector256.Create(0xFFFFu);
                Vector256<uint> vMask8 = Vector256.Create(0xFFu);
                Vector256<float> vZero = Vector256<float>.Zero;
                Vector256<float> v255 = Vector256.Create(MaxUInt8AsFloat);
                Vector256<float> vHalf = Vector256.Create(0.5f);

                int limit4 = pixelCount - 3;
                for (; p < limit4; p += 4)
                {
                    int o = (p * 4);
                    Vector256<int> r01 = ConvertRgbaPairToSrgbInt32Avx2(
                        Vector256.Create(sRef.Slice(o, 8)),
                        tabRef, vMin, vMax, vMinBits, vMask16, vMask8, vZero, v255, vHalf);
                    Vector256<int> r23 = ConvertRgbaPairToSrgbInt32Avx2(
                        Vector256.Create(sRef.Slice(o + 8, 8)),
                        tabRef, vMin, vMax, vMinBits, vMask16, vMask8, vZero, v255, vHalf);
                    Vector256<short> p16 = Avx2.Permute4x64(Avx2.PackSignedSaturate(r01, r23).AsInt64(), 0xD8).AsInt16();
                    Vector256<byte> p8 = Avx2.Permute4x64(Avx2.PackUnsignedSaturate(p16, p16).AsInt64(), 0xD8).AsByte();
                    p8.GetLower().CopyTo(dRef.Slice(o, 16));
                }
            }

            for (; p < pixelCount; p++)
            {
                int o = p * 4;
                dRef[o] = LinearToSrgbByteFast(sRef[o], tabRef);
                dRef[o + 1] = LinearToSrgbByteFast(sRef[o + 1], tabRef);
                dRef[o + 2] = LinearToSrgbByteFast(sRef[o + 2], tabRef);
                dRef[o + 3] = FloatToUInt8Scaled(sRef[o + 3]);
            }

            return;
        }
#endif
        if (!hasAlpha)
        {
            for (int i = 0; i < sampleCount; i++)
            {
                dst[i] = LinearToSrgbByte(src[i]);
            }

            return;
        }

        if (channels == 2)
        {
            if (swizzle == ChannelSwizzle.Ar)
            {
                for (int p = 0; p < pixelCount; p++)
                {
                    int o = p * 2;
                    dst[o] = FloatToUInt8Scaled(src[o + 1]);
                    dst[o + 1] = LinearToSrgbByte(src[o]);
                }
            }
            else
            {
                for (int p = 0; p < pixelCount; p++)
                {
                    int o = p * 2;
                    dst[o] = LinearToSrgbByte(src[o]);
                    dst[o + 1] = FloatToUInt8Scaled(src[o + 1]);
                }
            }

            return;
        }

        switch (swizzle)
        {
            case ChannelSwizzle.Identity:
                for (int p = 0; p < pixelCount; p++)
                {
                    int o = p * 4;
                    dst[o] = LinearToSrgbByte(src[o]);
                    dst[o + 1] = LinearToSrgbByte(src[o + 1]);
                    dst[o + 2] = LinearToSrgbByte(src[o + 2]);
                    dst[o + 3] = FloatToUInt8Scaled(src[o + 3]);
                }

                break;

            case ChannelSwizzle.Bgra:
                for (int p = 0; p < pixelCount; p++)
                {
                    int o = p * 4;
                    dst[o] = LinearToSrgbByte(src[o + 2]);
                    dst[o + 1] = LinearToSrgbByte(src[o + 1]);
                    dst[o + 2] = LinearToSrgbByte(src[o]);
                    dst[o + 3] = FloatToUInt8Scaled(src[o + 3]);
                }

                break;

            case ChannelSwizzle.Argb:
                for (int p = 0; p < pixelCount; p++)
                {
                    int o = p * 4;
                    dst[o] = FloatToUInt8Scaled(src[o + 3]);
                    dst[o + 1] = LinearToSrgbByte(src[o]);
                    dst[o + 2] = LinearToSrgbByte(src[o + 1]);
                    dst[o + 3] = LinearToSrgbByte(src[o + 2]);
                }

                break;

            case ChannelSwizzle.Abgr:
                for (int p = 0; p < pixelCount; p++)
                {
                    int o = p * 4;
                    dst[o] = FloatToUInt8Scaled(src[o + 3]);
                    dst[o + 1] = LinearToSrgbByte(src[o + 2]);
                    dst[o + 2] = LinearToSrgbByte(src[o + 1]);
                    dst[o + 3] = LinearToSrgbByte(src[o]);
                }

                break;
        }
    }

    private static void EncodeUInt8SrgbAll(
        Span<byte> dst,
        ReadOnlySpan<float> src,
        int pixelCount,
        int sampleCount,
        ChannelSwizzle swizzle)
    {
        if (swizzle == ChannelSwizzle.Identity)
        {
            for (int i = 0; i < sampleCount; i++)
            {
                dst[i] = LinearToSrgbByte(src[i]);
            }

            return;
        }

        if (swizzle == ChannelSwizzle.Ar)
        {
            for (int p = 0; p < pixelCount; p++)
            {
                int o = p * 2;
                dst[o] = LinearToSrgbByte(src[o + 1]);
                dst[o + 1] = LinearToSrgbByte(src[o]);
            }

            return;
        }

        GetEncodeOrder4(swizzle, out int e0, out int e1, out int e2, out int e3);
        for (int p = 0; p < pixelCount; p++)
        {
            int o = p * 4;
            dst[o] = LinearToSrgbByte(src[o + e0]);
            dst[o + 1] = LinearToSrgbByte(src[o + e1]);
            dst[o + 2] = LinearToSrgbByte(src[o + e2]);
            dst[o + 3] = LinearToSrgbByte(src[o + e3]);
        }
    }

    private static void EncodeUInt16Linear(
        Span<ushort> dst,
        ReadOnlySpan<float> src,
        int pixelCount,
        int sampleCount,
        ChannelSwizzle swizzle,
        bool unscaled)
    {
        if (swizzle == ChannelSwizzle.Identity)
        {
            int i = 0;
#if NET8_0_OR_GREATER
            var sRef = src;
            var dRef = dst;
            if (Avx2.IsSupported && sampleCount >= 8)
            {
                Vector256<float> vZero = Vector256<float>.Zero;
                Vector256<float> vMax = Vector256.Create(MaxUInt16AsFloat);
                Vector256<float> vHalf = Vector256.Create(0.5f);
                int limit = sampleCount - 7;
                for (; i < limit; i += 8)
                {
                    Vector256<float> f0 = Vector256.Create(sRef.Slice(i, 8));
                    if (!unscaled)
                    {
                        f0 = Avx.Multiply(f0, vMax);
                    }

                    f0 = Avx.Min(Avx.Max(Avx.Add(f0, vHalf), vZero), vMax);
                    Vector256<int> i0 = Avx.ConvertToVector256Int32WithTruncation(f0);
                    Vector256<ushort> u16 = Avx2.Permute4x64(Avx2.PackUnsignedSaturate(i0, i0).AsInt64(), 0xD8).AsUInt16();
                    u16.GetLower().CopyTo(dRef.Slice(i, 8));
                }
            }
            else if (AdvSimd.IsSupported && sampleCount >= 4)
            {
                Vector128<float> vZero = Vector128<float>.Zero;
                Vector128<float> vMax = Vector128.Create(MaxUInt16AsFloat);
                Vector128<float> vHalf = Vector128.Create(0.5f);
                int limit = sampleCount - 3;
                for (; i < limit; i += 4)
                {
                    Vector128<float> f0 = Vector128.Create(sRef.Slice(i, 4));
                    if (!unscaled)
                    {
                        f0 = AdvSimd.Multiply(f0, vMax);
                    }

                    f0 = AdvSimd.Min(AdvSimd.Max(AdvSimd.Add(f0, vHalf), vZero), vMax);
                    Vector64<ushort> u0 = AdvSimd.ExtractNarrowingLower(AdvSimd.ConvertToInt32RoundToZero(f0).AsUInt32());
                    u0.CopyTo(dRef.Slice(i, 4));
                }
            }
#endif
            if (unscaled)
            {
                for (; i < sampleCount; i++)
                {
                    dst[i] = FloatToUInt16Unscaled(src[i]);
                }
            }
            else
            {
                for (; i < sampleCount; i++)
                {
                    dst[i] = FloatToUInt16Scaled(src[i]);
                }
            }

            return;
        }

        if (swizzle == ChannelSwizzle.Ar)
        {
            for (int p = 0; p < pixelCount; p++)
            {
                int o = p * 2;
                if (unscaled)
                {
                    dst[o] = FloatToUInt16Unscaled(src[o + 1]);
                    dst[o + 1] = FloatToUInt16Unscaled(src[o]);
                }
                else
                {
                    dst[o] = FloatToUInt16Scaled(src[o + 1]);
                    dst[o + 1] = FloatToUInt16Scaled(src[o]);
                }
            }

            return;
        }

        GetEncodeOrder4(swizzle, out int e0, out int e1, out int e2, out int e3);
        for (int p = 0; p < pixelCount; p++)
        {
            int o = p * 4;
            if (unscaled)
            {
                dst[o] = FloatToUInt16Unscaled(src[o + e0]);
                dst[o + 1] = FloatToUInt16Unscaled(src[o + e1]);
                dst[o + 2] = FloatToUInt16Unscaled(src[o + e2]);
                dst[o + 3] = FloatToUInt16Unscaled(src[o + e3]);
            }
            else
            {
                dst[o] = FloatToUInt16Scaled(src[o + e0]);
                dst[o + 1] = FloatToUInt16Scaled(src[o + e1]);
                dst[o + 2] = FloatToUInt16Scaled(src[o + e2]);
                dst[o + 3] = FloatToUInt16Scaled(src[o + e3]);
            }
        }
    }

    private static void EncodeFloat32(
        Span<float> dst,
        ReadOnlySpan<float> src,
        int pixelCount,
        int sampleCount,
        ChannelSwizzle swizzle)
    {
        if (swizzle == ChannelSwizzle.Identity)
        {
            src.Slice(0, sampleCount).CopyTo(dst);
            return;
        }

        if (swizzle == ChannelSwizzle.Ar)
        {
            for (int p = 0; p < pixelCount; p++)
            {
                int o = p * 2;
                dst[o] = src[o + 1];
                dst[o + 1] = src[o];
            }

            return;
        }

        GetEncodeOrder4(swizzle, out int e0, out int e1, out int e2, out int e3);
        for (int p = 0; p < pixelCount; p++)
        {
            int o = p * 4;
            dst[o] = src[o + e0];
            dst[o + 1] = src[o + e1];
            dst[o + 2] = src[o + e2];
            dst[o + 3] = src[o + e3];
        }
    }

    private static void EncodeFloat16(
        Span<ushort> dst,
        ReadOnlySpan<float> src,
        int pixelCount,
        int sampleCount,
        ChannelSwizzle swizzle)
    {
        if (swizzle == ChannelSwizzle.Identity)
        {
            for (int i = 0; i < sampleCount; i++)
            {
                dst[i] = FloatToHalf(src[i]);
            }

            return;
        }

        if (swizzle == ChannelSwizzle.Ar)
        {
            for (int p = 0; p < pixelCount; p++)
            {
                int o = p * 2;
                dst[o] = FloatToHalf(src[o + 1]);
                dst[o + 1] = FloatToHalf(src[o]);
            }

            return;
        }

        GetEncodeOrder4(swizzle, out int e0, out int e1, out int e2, out int e3);
        for (int p = 0; p < pixelCount; p++)
        {
            int o = p * 4;
            dst[o] = FloatToHalf(src[o + e0]);
            dst[o + 1] = FloatToHalf(src[o + e1]);
            dst[o + 2] = FloatToHalf(src[o + e2]);
            dst[o + 3] = FloatToHalf(src[o + e3]);
        }
    }

#if NET8_0_OR_GREATER
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector128<byte> GetDecodeSwizzleMask16(ChannelSwizzle swizzle) => swizzle switch
    {
        ChannelSwizzle.Bgra => Vector128.Create((byte)2, 1, 0, 3, 6, 5, 4, 7, 10, 9, 8, 11, 14, 13, 12, 15),
        ChannelSwizzle.Argb => Vector128.Create((byte)1, 2, 3, 0, 5, 6, 7, 4, 9, 10, 11, 8, 13, 14, 15, 12),
        ChannelSwizzle.Abgr => Vector128.Create((byte)3, 2, 1, 0, 7, 6, 5, 4, 11, 10, 9, 8, 15, 14, 13, 12),
        _ => Vector128.Create((byte)0, 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15),
    };

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector128<byte> GetEncodeSwizzleMask16(ChannelSwizzle swizzle) => swizzle switch
    {
        ChannelSwizzle.Bgra => Vector128.Create((byte)2, 1, 0, 3, 6, 5, 4, 7, 10, 9, 8, 11, 14, 13, 12, 15),
        ChannelSwizzle.Argb => Vector128.Create((byte)3, 0, 1, 2, 7, 4, 5, 6, 11, 8, 9, 10, 15, 12, 13, 14),
        ChannelSwizzle.Abgr => Vector128.Create((byte)3, 2, 1, 0, 7, 6, 5, 4, 11, 10, 9, 8, 15, 14, 13, 12),
        _ => Vector128.Create((byte)0, 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15),
    };
#endif

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void GetDecodeOrder4(ChannelSwizzle swizzle, out int o0, out int o1, out int o2, out int o3)
    {
        switch (swizzle)
        {
            case ChannelSwizzle.Bgra:
                o0 = 2; o1 = 1; o2 = 0; o3 = 3;
                return;
            case ChannelSwizzle.Argb:
                o0 = 1; o1 = 2; o2 = 3; o3 = 0;
                return;
            case ChannelSwizzle.Abgr:
                o0 = 3; o1 = 2; o2 = 1; o3 = 0;
                return;
            default:
                o0 = 0; o1 = 1; o2 = 2; o3 = 3;
                return;
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void GetEncodeOrder4(ChannelSwizzle swizzle, out int e0, out int e1, out int e2, out int e3)
    {
        switch (swizzle)
        {
            case ChannelSwizzle.Bgra:
                e0 = 2; e1 = 1; e2 = 0; e3 = 3;
                return;
            case ChannelSwizzle.Argb:
                e0 = 3; e1 = 0; e2 = 1; e3 = 2;
                return;
            case ChannelSwizzle.Abgr:
                e0 = 3; e1 = 2; e2 = 1; e3 = 0;
                return;
            default:
                e0 = 0; e1 = 1; e2 = 2; e3 = 3;
                return;
        }
    }

    // Fancy-alpha pixels are stored as eight floats: [R G B A R*A G*A B*A A*A]. The last lane only pads the
    // pixel to a whole vector; it is carried through the resampler and never read back.
    internal const int FancyStride = 8;

    private static void DecodeRgba8SrgbToFancy(Span<float> dst, ReadOnlySpan<byte> src, int pixelCount)
    {
        ReadOnlySpan<float> lut = SrgbToLinear.AsSpan(0, 256);
        ReadOnlySpan<float> lin = ByteToLinearFloat.AsSpan(0, 256);
        ReadOnlySpan<byte> s = src.Slice(0, pixelCount * 4);
        Span<float> d = dst.Slice(0, pixelCount * FancyStride);
        for (int p = 0; p < pixelCount; p++)
        {
            ReadOnlySpan<byte> px = s.Slice(p * 4, 4);
            float a = lin[px[3]];
            WriteFancy(d.Slice(p * FancyStride, FancyStride), lut[px[0]], lut[px[1]], lut[px[2]], a);
        }
    }

    private static void DecodeRgba8LinearToFancy(Span<float> dst, ReadOnlySpan<byte> src, int pixelCount)
    {
        ReadOnlySpan<byte> s = src.Slice(0, pixelCount * 4);
        Span<float> d = dst.Slice(0, pixelCount * FancyStride);
        for (int p = 0; p < pixelCount; p++)
        {
            ReadOnlySpan<byte> px = s.Slice(p * 4, 4);
            WriteFancy(
                d.Slice(p * FancyStride, FancyStride),
                px[0] * InvMaxUInt8,
                px[1] * InvMaxUInt8,
                px[2] * InvMaxUInt8,
                px[3] * InvMaxUInt8);
        }
    }

    private static void DecodeRgbaFp32ToFancy(Span<float> dst, ReadOnlySpan<float> src, int pixelCount)
    {
        ReadOnlySpan<float> s = src.Slice(0, pixelCount * 4);
        Span<float> d = dst.Slice(0, pixelCount * FancyStride);
        for (int p = 0; p < pixelCount; p++)
        {
            ReadOnlySpan<float> px = s.Slice(p * 4, 4);
            WriteFancy(d.Slice(p * FancyStride, FancyStride), px[0], px[1], px[2], px[3]);
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void WriteFancy(Span<float> pixel, float r, float g, float b, float a)
    {
#if NET8_0_OR_GREATER
        if (Vector128.IsHardwareAccelerated)
        {
            Span<Vector128<float>> v = MemoryMarshal.Cast<float, Vector128<float>>(pixel);
            Vector128<float> rgba = Vector128.Create(r, g, b, a);
            v[0] = rgba;
            v[1] = rgba * a;
            return;
        }
#endif
        pixel[0] = r;
        pixel[1] = g;
        pixel[2] = b;
        pixel[3] = a;
        pixel[4] = r * a;
        pixel[5] = g * a;
        pixel[6] = b * a;
        pixel[7] = a * a;
    }

    // Expands [R G B A] in place (back to front) to the eight-float fancy layout.
    private static void FancyAlphaWeight4Ch(Span<float> buffer, int pixelCount)
    {
        for (int p = pixelCount - 1; p >= 0; p--)
        {
            ReadOnlySpan<float> src = buffer.Slice(p * 4, 4);
            float r = src[0];
            float g = src[1];
            float b = src[2];
            float a = src[3];
            WriteFancy(buffer.Slice(p * FancyStride, FancyStride), r, g, b, a);
        }
    }

    // Compacts the eight-float fancy layout in place (front to back) to [R G B A].
    private static void FancyAlphaUnweight4Ch(Span<float> buffer, int pixelCount)
    {
        for (int p = 0; p < pixelCount; p++)
        {
            ReadOnlySpan<float> src = buffer.Slice(p * FancyStride, FancyStride);
            float alpha = src[3];
            float r;
            float g;
            float b;
            if (alpha < AlphaTooSmallToDivide)
            {
                r = src[0];
                g = src[1];
                b = src[2];
            }
            else
            {
                float invAlpha = 1.0f / alpha;
                r = src[4] * invAlpha;
                g = src[5] * invAlpha;
                b = src[6] * invAlpha;
            }

            Span<float> dst = buffer.Slice(p * 4, 4);
            dst[0] = r;
            dst[1] = g;
            dst[2] = b;
            dst[3] = alpha;
        }
    }

    // Expands [X A] in-place (back-to-front) to [X A X*A].
    private static void FancyAlphaWeight2Ch(Span<float> buffer, int pixelCount)
    {
        for (int p = pixelCount - 1; p >= 0; p--)
        {
            int src = p * 2;
            int dst = p * 3;
            float x = buffer[src];
            float a = buffer[src + 1];
            buffer[dst] = x;
            buffer[dst + 1] = a;
            buffer[dst + 2] = x * a;
        }
    }

    // Compacts [R G B A Rpm Gpm Bpm] in-place (front-to-back) to [R G B A].
    // Compacts [X A Xpm] in-place (front-to-back) to [X A].
    private static void FancyAlphaUnweight2Ch(Span<float> buffer, int pixelCount)
    {
        for (int p = 0; p < pixelCount; p++)
        {
            int src = p * 3;
            int dst = p * 2;
            float alpha = buffer[src + 1];
            float x = buffer[src];
            if (alpha >= AlphaTooSmallToDivide)
            {
                x = buffer[src + 2] / alpha;
            }

            buffer[dst] = x;
            buffer[dst + 1] = alpha;
        }
    }

    private static void SimpleAlphaWeight4Ch(Span<float> buffer, int pixelCount)
    {
        int i = 0;
        int end = pixelCount * 4;
#if NET8_0_OR_GREATER
        var bRef = buffer;
        if (Avx.IsSupported && end >= 8)
        {
            Vector256<float> ones = Vector256.Create(1.0f);
            int limit = end - 7;
            for (; i < limit; i += 8)
            {
                var bRef_i_w23 = bRef.Slice(i, 8);
                Vector256<float> d = Vector256.Create(bRef_i_w23.Slice(0, 8));
                Vector256<float> a = Avx.Blend(Avx.Permute(d, 0xFF), ones, 0x88);
                Avx.Multiply(d, a).CopyTo(bRef_i_w23.Slice(0, 8));
            }
        }
#endif
        for (; i < end; i += 4)
        {
            float alpha = buffer[i + 3];
            buffer[i] *= alpha;
            buffer[i + 1] *= alpha;
            buffer[i + 2] *= alpha;
        }
    }

    private static void SimpleAlphaWeight2Ch(Span<float> buffer, int pixelCount)
    {
        int end = pixelCount * 2;
        for (int i = 0; i < end; i += 2)
        {
            buffer[i] *= buffer[i + 1];
        }
    }

    private static void SimpleAlphaUnweight4Ch(Span<float> buffer, int pixelCount)
    {
        int end = pixelCount * 4;
#if NET8_0_OR_GREATER
        if (Sse.IsSupported || AdvSimd.IsSupported)
        {
            var bRef = buffer;
            for (int i = 0; i < end; i += 4)
            {
                var bRef_i_w24 = bRef.Slice(i, 4);
                float alpha = bRef[i + 3];
                if (alpha >= AlphaTooSmallToDivide)
                {
                    Vector128<float> d = Vector128.Create(bRef_i_w24.Slice(0, 4));
                    Vector128<float> inv = Vector128.Create(1.0f / alpha);
                    Vector128<float> r = Sse.IsSupported ? Sse.Multiply(d, inv) : AdvSimd.Multiply(d, inv);
                    r.CopyTo(bRef_i_w24.Slice(0, 4));
                    bRef[i + 3] = alpha;
                }
            }

            return;
        }
#endif
        for (int i = 0; i < end; i += 4)
        {
            float alpha = buffer[i + 3];
            if (alpha >= AlphaTooSmallToDivide)
            {
                float invAlpha = 1.0f / alpha;
                buffer[i] *= invAlpha;
                buffer[i + 1] *= invAlpha;
                buffer[i + 2] *= invAlpha;
            }
        }
    }

    private static void SimpleAlphaUnweight2Ch(Span<float> buffer, int pixelCount)
    {
        int end = pixelCount * 2;
        for (int i = 0; i < end; i += 2)
        {
            float alpha = buffer[i + 1];
            if (alpha >= AlphaTooSmallToDivide)
            {
                buffer[i] /= alpha;
            }
        }
    }

    private static void SimpleFlip3Ch(Span<float> buffer, int pixelCount)
    {
        int end = pixelCount * 3;
        for (int i = 0; i < end; i += 3)
        {
            (buffer[i], buffer[i + 2]) = (buffer[i + 2], buffer[i]);
        }
    }
}
