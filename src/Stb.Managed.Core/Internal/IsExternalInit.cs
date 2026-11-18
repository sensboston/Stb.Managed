#if !NET5_0_OR_GREATER
namespace System.Runtime.CompilerServices;

/// <summary>
/// Polyfill enabling C# 9+ init-only setters and records on netstandard2.1.
/// </summary>
internal static class IsExternalInit
{
}
#endif
