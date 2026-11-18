using System.Numerics;
#if NET8_0_OR_GREATER
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.Arm;
using System.Runtime.Intrinsics.X86;
#endif

namespace Stb.Managed.Internal;

/// <summary>
/// Runtime SIMD capability queries folded to constants by the JIT on .NET 8+.
/// </summary>
internal static class SimdCapabilities
{
    public static bool HasPortableVector => Vector.IsHardwareAccelerated;

    public static int PortableByteWidth => Vector<byte>.Count;

#if NET8_0_OR_GREATER
    public static bool HasVector128 => Vector128.IsHardwareAccelerated;
    public static bool HasVector256 => Vector256.IsHardwareAccelerated;
    public static bool HasVector512 => Vector512.IsHardwareAccelerated;
    public static bool HasSsse3 => Ssse3.IsSupported;
    public static bool HasAvx2 => Avx2.IsSupported;
    public static bool HasAvx512BW => Avx512BW.IsSupported;
    public static bool HasAdvSimd => AdvSimd.IsSupported;
#else
    public static bool HasVector128 => false;
    public static bool HasVector256 => false;
    public static bool HasVector512 => false;
    public static bool HasSsse3 => false;
    public static bool HasAvx2 => false;
    public static bool HasAvx512BW => false;
    public static bool HasAdvSimd => false;
#endif
}
