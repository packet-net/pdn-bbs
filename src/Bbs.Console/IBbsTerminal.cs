namespace Bbs.Console;

/// <summary>
/// The session seam between the console engine and whatever carries the bytes (Host will
/// implement this over an RHPv2 stream; tests implement it as a script). The engine is
/// sans-IO: it only ever awaits lines in and writes text out, and never touches sockets.
///
/// Text discipline: the engine emits CR-terminated lines (the RF side is CR-discipline,
/// compat spec §1.2); the terminal implementation owns any translation to the underlying
/// transport (e.g. CRLF for telnet) and MUST present received text to the engine one line
/// at a time with line terminators stripped. Text is Latin-1-safe: implementations should
/// decode/encode bytes with <see cref="System.Text.Encoding.Latin1"/> (byte-transparent)
/// so 8-bit user text survives end to end.
/// </summary>
public interface IBbsTerminal
{
    /// <summary>
    /// The connected (already authenticated by the link layer) callsign, exactly as it
    /// appeared on the connect — SSID included. Partner matching is by exact call incl.
    /// SSID (compat spec §2.5); user-facing identity strips the SSID.
    /// </summary>
    string RemoteCallsign { get; }

    /// <summary>
    /// Awaits the next received line, terminators stripped. Returns null when the remote
    /// station disconnected (the engine ends the session with
    /// <see cref="BbsSessionEndReason.Drop"/>).
    /// </summary>
    ValueTask<string?> ReadLineAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Writes text to the remote station. The engine passes CR-terminated lines (and the
    /// <c>de CALL&gt;</c> prompt with its CR LF, compat spec §1.2); implementations send
    /// it as-is apart from transport-mandated translation.
    /// </summary>
    ValueTask WriteAsync(string text, CancellationToken cancellationToken);
}

/// <summary>
/// Thrown by the engine internally (and allowed from <see cref="IBbsTerminal"/>
/// implementations) to signal that the remote station vanished mid-session.
/// <see cref="BbsConsoleSession.RunAsync(IBbsTerminal, Bbs.Core.BbsStore, BbsConsoleConfig, TimeProvider, CancellationToken)"/>
/// converts it to <see cref="BbsSessionEndReason.Drop"/>.
/// </summary>
public sealed class BbsTerminalClosedException : Exception
{
    /// <summary>Creates the exception with a default message.</summary>
    public BbsTerminalClosedException()
        : base("The remote station disconnected.")
    {
    }

    /// <summary>Creates the exception with a message.</summary>
    public BbsTerminalClosedException(string message)
        : base(message)
    {
    }

    /// <summary>Creates the exception with a message and inner exception.</summary>
    public BbsTerminalClosedException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
