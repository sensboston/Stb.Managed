namespace Stb.Managed;

/// <summary>
/// Exception thrown when an Stb.Managed operation fails due to invalid input, unsupported features,
/// or exceeded safety limits.
/// </summary>
public sealed class StbException : Exception
{
    /// <summary>
    /// Initializes a new instance of the <see cref="StbException"/> class.
    /// </summary>
    public StbException()
        : base("An Stb.Managed operation failed.")
    {
        Code = StbErrorCode.Unknown;
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="StbException"/> class with a specified error message.
    /// </summary>
    /// <param name="message">The message that describes the error.</param>
    public StbException(string message)
        : base(message)
    {
        Code = StbErrorCode.Unknown;
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="StbException"/> class with a specified error message
    /// and a reference to the inner exception that is the cause of this exception.
    /// </summary>
    /// <param name="message">The message that describes the error.</param>
    /// <param name="innerException">The exception that is the cause of the current exception.</param>
    public StbException(string message, Exception? innerException)
        : base(message, innerException)
    {
        Code = StbErrorCode.Unknown;
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="StbException"/> class with a typed error code,
    /// message, optional byte offset, and optional inner exception.
    /// </summary>
    /// <param name="code">The categorized error code.</param>
    /// <param name="message">The human-readable error message.</param>
    /// <param name="offset">The optional input byte offset where the error was detected.</param>
    /// <param name="innerException">The optional underlying exception.</param>
    public StbException(
        StbErrorCode code,
        string message,
        long? offset = null,
        Exception? innerException = null)
        : base(message, innerException)
    {
        Code = code;
        Offset = offset;
    }

    /// <summary>
    /// Gets the categorized error code describing why the operation failed.
    /// </summary>
    public StbErrorCode Code { get; }

    /// <summary>
    /// Gets the byte offset in the input where the error was detected, if known.
    /// </summary>
    public long? Offset { get; }
}
