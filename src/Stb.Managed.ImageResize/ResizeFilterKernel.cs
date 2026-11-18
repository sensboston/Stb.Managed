using Stb.Managed.Internal;

namespace Stb.Managed;

/// <summary>
/// Describes a user-defined 1D resampling filter kernel, matching <c>stbir_set_filter_callbacks_new</c>.
/// Supply either <see cref="Support"/> (radius in filter pixels) or <see cref="Radius"/> (radius in input pixels).
/// </summary>
public sealed class ResizeFilterKernel
{
    /// <summary>
    /// Initializes a custom filter kernel defined by its evaluation function and support radius in filter pixels.
    /// </summary>
    /// <param name="evaluate">
    /// Evaluates the filter weight given <c>(x, scale)</c>, where <c>x</c> is the distance in filter pixels and
    /// <c>scale &lt;= 1</c> is the smaller of the scale ratio and its inverse.
    /// </param>
    /// <param name="support">
    /// Returns the filter half-width in filter pixels given <c>scale &lt;= 1</c>.
    /// </param>
    /// <exception cref="ArgumentNullException">Thrown if <paramref name="evaluate"/> or <paramref name="support"/> is null.</exception>
    public ResizeFilterKernel(Func<double, double, double> evaluate, Func<double, double> support)
    {
        Evaluate = Guard.ThrowIfNull(evaluate, nameof(evaluate));
        Support = Guard.ThrowIfNull(support, nameof(support));
    }

    /// <summary>
    /// Initializes a custom filter kernel defined by its evaluation function and radius directly in input pixels.
    /// </summary>
    /// <param name="evaluate">
    /// Evaluates the filter weight given <c>(x, scale)</c>.
    /// </param>
    /// <param name="radius">
    /// Returns the filter half-width in input pixels given <c>(scale, invScale, isUpsample)</c>.
    /// </param>
    /// <exception cref="ArgumentNullException">Thrown if <paramref name="evaluate"/> or <paramref name="radius"/> is null.</exception>
    public ResizeFilterKernel(Func<double, double, double> evaluate, Func<double, double, bool, double> radius)
    {
        Evaluate = Guard.ThrowIfNull(evaluate, nameof(evaluate));
        Radius = Guard.ThrowIfNull(radius, nameof(radius));
    }

    /// <summary>
    /// Gets the kernel weight evaluation callback <c>(x, scale) =&gt; weight</c>.
    /// </summary>
    public Func<double, double, double> Evaluate { get; }

    /// <summary>
    /// Gets the support callback <c>(scale) =&gt; filterPixelRadius</c>, or <see langword="null"/> if <see cref="Radius"/> is used.
    /// </summary>
    public Func<double, double>? Support { get; }

    /// <summary>
    /// Gets the input-pixel radius callback <c>(scale, invScale, isUpsample) =&gt; inputPixelRadius</c>, or <see langword="null"/> if <see cref="Support"/> is used.
    /// </summary>
    public Func<double, double, bool, double>? Radius { get; }
}
