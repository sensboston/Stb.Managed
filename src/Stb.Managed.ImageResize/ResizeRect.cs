namespace Stb.Managed;

/// <summary>
/// Represents an integer pixel rectangle used for output or paired sub-rectangle resampling.
/// </summary>
/// <param name="X">The left pixel coordinate.</param>
/// <param name="Y">The top pixel coordinate.</param>
/// <param name="Width">The rectangle width in pixels.</param>
/// <param name="Height">The rectangle height in pixels.</param>
public readonly record struct ResizeRect(int X, int Y, int Width, int Height);

/// <summary>
/// Represents a normalized <c>[0, 1]</c> sub-rectangle within the input image for subpixel-accurate region resampling.
/// </summary>
/// <param name="S0">The left normalized coordinate.</param>
/// <param name="T0">The top normalized coordinate.</param>
/// <param name="S1">The right normalized coordinate.</param>
/// <param name="T1">The bottom normalized coordinate.</param>
public readonly record struct ResizeNormalizedRect(double S0, double T0, double S1, double T1);
