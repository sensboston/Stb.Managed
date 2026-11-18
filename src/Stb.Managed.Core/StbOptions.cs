using Stb.Managed.Internal;

namespace Stb.Managed;

/// <summary>
/// Common execution options shared across Stb.Managed operations.
/// </summary>
public sealed record StbOptions
{
    private readonly StbLimits _limits = StbLimits.Default;
    private readonly int _maxDegreeOfParallelism = 1;

    /// <summary>
    /// Gets the default options (<see cref="StbLimits.Default"/>, single-threaded execution).
    /// </summary>
    public static StbOptions Default { get; } = new();

    /// <summary>
    /// Gets the safety limits enforced during decoding, encoding, or processing.
    /// </summary>
    public StbLimits Limits
    {
        get => _limits;
        init => _limits = Guard.ThrowIfNull(value, nameof(value));
    }

    /// <summary>
    /// Gets the maximum degree of parallelism.
    /// <list type="bullet">
    /// <item><description><c>1</c> (default): strictly single-threaded.</description></item>
    /// <item><description><c>0</c>: automatically enable multi-threading above the operation's work threshold.</description></item>
    /// <item><description><c>&gt; 1</c>: cap worker parallelism at this value.</description></item>
    /// </list>
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">Thrown when set to a negative value.</exception>
    public int MaxDegreeOfParallelism
    {
        get => _maxDegreeOfParallelism;
        init
        {
            Guard.ThrowIfNegative(value, nameof(value));
            _maxDegreeOfParallelism = value;
        }
    }

    /// <summary>
    /// Resolves the concrete worker count to use for an operation processing <paramref name="workUnits"/>
    /// units of work (for example, pixels or blocks), given an <paramref name="autoThreshold"/>.
    /// </summary>
    /// <param name="workUnits">The number of work units to process.</param>
    /// <param name="autoThreshold">The minimum work units required before automatic parallelism activates.</param>
    /// <returns>The resolved degree of parallelism (always at least 1).</returns>
    public int ResolveDegreeOfParallelism(long workUnits, long autoThreshold)
    {
        if (_maxDegreeOfParallelism == 1)
        {
            return 1;
        }

        if (_maxDegreeOfParallelism == 0)
        {
            return workUnits >= autoThreshold ? Math.Max(1, Environment.ProcessorCount) : 1;
        }

        return Math.Min(_maxDegreeOfParallelism, Math.Max(1, Environment.ProcessorCount));
    }
}
