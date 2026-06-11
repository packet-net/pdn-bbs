namespace Bbs.Fbb;

/// <summary>
/// Raised when a peer's input (or a caller-supplied field destined for the
/// wire) violates the FBB forwarding protocol as pinned by
/// <c>docs/linbpq-mail-compat.md</c>.
/// </summary>
/// <remarks>
/// Where the compatibility spec documents an exact <c>*** …</c> error line
/// for the failure (spec §3.12), <see cref="WireErrorLine"/> carries it so
/// the session layer can transmit the canonical text before disconnecting.
/// </remarks>
public class FbbProtocolException : Exception
{
    /// <summary>Creates the exception with a default message.</summary>
    public FbbProtocolException()
    {
    }

    /// <summary>Creates the exception with a diagnostic message.</summary>
    public FbbProtocolException(string message)
        : base(message)
    {
    }

    /// <summary>Creates the exception with a message and inner cause.</summary>
    public FbbProtocolException(string message, Exception innerException)
        : base(message, innerException)
    {
    }

    /// <summary>
    /// Creates the exception with a diagnostic message and the exact
    /// <c>*** …</c> line that should be sent to the peer (spec §3.12).
    /// </summary>
    public FbbProtocolException(string message, string? wireErrorLine)
        : base(message)
    {
        WireErrorLine = wireErrorLine;
    }

    /// <summary>
    /// The exact <c>*** …</c> error line documented for this failure class,
    /// or <see langword="null"/> when the spec does not pin one.
    /// </summary>
    public string? WireErrorLine { get; }
}
