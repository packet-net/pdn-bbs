namespace Bbs.Console;

/// <summary>
/// Why a console session ended — returned by <see cref="BbsConsoleSession.RunAsync(IBbsTerminal, Bbs.Core.BbsStore, BbsConsoleConfig, TimeProvider, CancellationToken)"/>
/// so the Host knows what to do with the underlying link.
/// </summary>
public enum BbsSessionEndReason
{
    /// <summary>
    /// The user signed off (B/Bye, compat spec §1.2/§1.3) — or the BBS closed the session
    /// itself ("Too many errors - closing", §1.3). The Host should disconnect the link.
    /// </summary>
    Bye,

    /// <summary>
    /// The user typed NODE (compat spec §1.3 "Exit BBS back to node"). The Host should
    /// return the link to the node command processor rather than disconnect.
    /// </summary>
    Node,

    /// <summary>The remote station disconnected (terminal returned null / closed) mid-session.</summary>
    Drop,
}
