using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
#if NET8_0_OR_GREATER
using System.Numerics;
using System.Runtime.InteropServices;
#endif

namespace Stb.Managed.Internal;

internal static class ArrayAllocation
{
#if NET8_0_OR_GREATER
    internal static int MaxByteArrayLength => Array.MaxLength;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static T[] AllocateUninitialized<T>(int length)
        where T : unmanaged =>
        GC.AllocateUninitializedArray<T>(length);
#else
    internal const int MaxByteArrayLength = 0x7FFFFFC7;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static T[] AllocateUninitialized<T>(int length)
        where T : unmanaged =>
        new T[length];
#endif
}

internal static class Guard
{
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static T ThrowIfNull<T>([NotNull] T? value, string paramName)
        where T : class
    {
#if NET8_0_OR_GREATER
        ArgumentNullException.ThrowIfNull(value, paramName);
#else
        if (value is null)
        {
            throw new ArgumentNullException(paramName);
        }
#endif
        return value;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static void ThrowIfNegative(int value, string paramName)
    {
#if NET8_0_OR_GREATER
        ArgumentOutOfRangeException.ThrowIfNegative(value, paramName);
#else
        if (value < 0)
        {
            throw new ArgumentOutOfRangeException(paramName, value, "Value must be non-negative.");
        }
#endif
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static void ThrowIfNegativeOrZero(int value, string paramName)
    {
#if NET8_0_OR_GREATER
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(value, paramName);
#else
        if (value <= 0)
        {
            throw new ArgumentOutOfRangeException(paramName, value, "Value must be positive.");
        }
#endif
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static void ThrowIfIndexOutOfRange(int index, int length, string paramName)
    {
        if ((uint)index >= (uint)length)
        {
            ThrowIndexOutOfRange(index, length, paramName);
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static void ThrowIfLessThan(int value, int other, string paramName)
    {
#if NET8_0_OR_GREATER
        ArgumentOutOfRangeException.ThrowIfLessThan(value, other, paramName);
#else
        if (value < other)
        {
            throw new ArgumentOutOfRangeException(paramName, value, $"Value must be at least {other}.");
        }
#endif
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static void ThrowIfDisposed(bool condition, object instance)
    {
#if NET8_0_OR_GREATER
        ObjectDisposedException.ThrowIf(condition, instance);
#else
        if (condition)
        {
            throw new ObjectDisposedException(instance.GetType().FullName);
        }
#endif
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void ThrowIndexOutOfRange(int index, int length, string paramName) =>
        throw new ArgumentOutOfRangeException(paramName, index, $"Index must be in [0, {length}).");
}

internal static class BitOps
{
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static int LeadingZeroCount(uint value)
    {
#if NET8_0_OR_GREATER
        return BitOperations.LeadingZeroCount(value);
#else
        if (value == 0)
        {
            return 32;
        }

        int n = 0;
        if ((value & 0xFFFF0000u) == 0) { n += 16; value <<= 16; }
        if ((value & 0xFF000000u) == 0) { n += 8; value <<= 8; }
        if ((value & 0xF0000000u) == 0) { n += 4; value <<= 4; }
        if ((value & 0xC0000000u) == 0) { n += 2; value <<= 2; }
        if ((value & 0x80000000u) == 0) { n += 1; }
        return n;
#endif
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static int TrailingZeroCount(uint value)
    {
#if NET8_0_OR_GREATER
        return BitOperations.TrailingZeroCount(value);
#else
        if (value == 0)
        {
            return 32;
        }

        int n = 0;
        if ((value & 0x0000FFFFu) == 0) { n += 16; value >>= 16; }
        if ((value & 0x000000FFu) == 0) { n += 8; value >>= 8; }
        if ((value & 0x0000000Fu) == 0) { n += 4; value >>= 4; }
        if ((value & 0x00000003u) == 0) { n += 2; value >>= 2; }
        if ((value & 0x00000001u) == 0) { n += 1; }
        return n;
#endif
    }
}
