namespace Stb.Managed;

/// <summary>
/// Categorizes errors reported by <see cref="StbException"/>.
/// </summary>
public enum StbErrorCode
{
    /// <summary>
    /// An unspecified error occurred.
    /// </summary>
    Unknown = 0,

    /// <summary>
    /// The input data is malformed or violates the format specification.
    /// </summary>
    InvalidData = 1,

    /// <summary>
    /// The input format is not recognized or not supported.
    /// </summary>
    UnsupportedFormat = 2,

    /// <summary>
    /// The input uses a feature of a recognized format that is not supported.
    /// </summary>
    UnsupportedFeature = 3,

    /// <summary>
    /// The input ended unexpectedly before decoding could complete.
    /// </summary>
    TruncatedInput = 4,

    /// <summary>
    /// A compressed or entropy-coded stream inside the input is corrupt.
    /// </summary>
    CorruptStream = 5,

    /// <summary>
    /// The input exceeded a configured safety budget in <see cref="StbLimits"/>.
    /// </summary>
    LimitExceeded = 6,

    /// <summary>
    /// One or more caller-supplied arguments were invalid.
    /// </summary>
    InvalidArgument = 7,

    /// <summary>
    /// An unexpected internal invariant failure occurred.
    /// </summary>
    InternalError = 8,
}
