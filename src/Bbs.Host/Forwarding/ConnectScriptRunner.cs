using System.Text;
using Bbs.Fbb;
using Bbs.Host.Rhp;
using Microsoft.Extensions.Logging;

namespace Bbs.Host.Forwarding;

/// <summary>
/// A connect script failed (failure text from the node, an expect that never arrived, or a
/// response/SID wait timing out). The message carries the attempt transcript — what we sent,
/// what came back, where it stopped (forwarding.md wart 4 / wave F-0).
/// </summary>
public sealed class ConnectScriptException(string message) : Exception(message);

/// <summary>
/// Executes a <see cref="ConnectPlan"/>'s post-connect steps over an open RHP child. The step
/// model is expect/send, ported from pdn-bpqchat's <c>RunWithScript</c>:
///
/// <list type="bullet">
/// <item>A step with a non-empty <c>Expect</c> reads the node stream until that case-insensitive
/// substring appears (a node prompt has no line terminator, so the match is against accumulated
/// bytes, not lines), bounded by the partner's ConTimeout; THEN, if <c>Send</c> is non-empty, it
/// sends it CR-terminated and logs "matched … sent …". An explicit <c>EXPECT=</c> is the ONLY
/// reliable way to wait for a node prompt — prompts are not standardised across
/// BPQ/URONode/XRouter/etc — so this is how a multi-hop walk confirms each hop before the next
/// command.</item>
/// <item>A send-only step (empty <c>Expect</c> — a bare script line, the legacy verbatim form) has
/// no known prompt to wait for, so it goes out verbatim, CR-terminated; between bare steps (not the
/// last) the runner makes a best-effort wait for the node's progress — a line containing
/// <c>" CONNECTED"</c> (the leading space is BPQ's own guard against DISCONNECTED), starting
/// <c>OK</c>, or ending <c>&gt;</c> — but for any node whose prompt differs, set an <c>EXPECT=</c>
/// ("the software knows what to look for" holds only for the common case, spec §4.4).</item>
/// <item>After the LAST step it waits for the partner's FBB SID — <c>[…-…]</c> ONLY, never a bare
/// <c>&gt;</c>-prompt: an intermediate gateway banner ending <c>&gt;</c> (e.g. a URONode
/// "…Help: ? <command>") must NOT be mistaken for the SID (the multi-hop wrong-banner capture). It
/// returns the SID line plus any unconsumed tail for the FBB caller session; node chatter on the way
/// (CTEXT, <c>*** Connected to …</c> progress lines, gateway prompts) is consumed here, where it
/// belongs, so the FBB FSM sees a clean stream.</item>
/// <item>Failure text at any point — BUSY / FAILURE / SORRY / INVALID / RETRIED /
/// <c>ERROR - </c> / UNABLE TO CONNECT / DISCONNECTED / FAILED TO CONNECT / REJECTED
/// (the spec §4.4 ELSE-detection list) — fails the cycle, as does a wait exceeding the
/// partner's ConTimeout. A failed cycle surfaces as <see cref="ConnectScriptException"/>;
/// the scheduler retries with backoff.</item>
/// </list>
///
/// Named deviations from LinBPQ (spec §4.4): the failure/progress/expect scans are
/// case-insensitive (a tolerant superset of BPQ's exact-case scan), and ConTimeout bounds
/// each wait rather than the whole handshake. With no steps at all the runner is a no-op —
/// the bare-open path (dialling a BBS application callsign directly) keeps today's behaviour,
/// where the FBB caller FSM itself waits out the SID.
/// </summary>
public static class ConnectScriptRunner
{
    /// <summary>Failure strings from the spec §4.4 ELSE-detection list.</summary>
    private static readonly string[] FailureMarkers =
    [
        "BUSY", "FAILURE", "SORRY", "INVALID", "RETRIED", "ERROR - ",
        "UNABLE TO CONNECT", "DISCONNECTED", "FAILED TO CONNECT", "REJECTED",
    ];

    /// <summary>
    /// Runs the plan's steps; returns the bytes the FBB caller session must see first
    /// (the SID line + tail once the post-script SID-wait completed; empty for a stepless
    /// plan). Throws <see cref="ConnectScriptException"/> on failure text, an expect that
    /// never arrives, or a timed-out wait.
    /// </summary>
    public static Task<byte[]> RunAsync(
        RhpChildConnection child,
        ConnectPlan plan,
        TimeSpan responseWait,
        TimeProvider time,
        ILogger logger,
        CancellationToken cancellationToken) =>
        RunAsync(child, plan, responseWait, time, logger, transcript: null, cancellationToken);

    /// <summary>
    /// As <see cref="RunAsync(RhpChildConnection,ConnectPlan,TimeSpan,TimeProvider,ILogger,CancellationToken)"/>,
    /// but with the running attempt transcript exposed via <paramref name="transcript"/>: each line we
    /// sent (<c>&gt; …</c>) and each line/match we saw (<c>&lt; …</c>) is appended as it happens. The
    /// sysop test-connect tool passes a list so it can report the dialogue on SUCCESS too (the
    /// production forwarding path passes null — it only needs the transcript on failure, which the
    /// thrown <see cref="ConnectScriptException"/> already carries). Behaviour is otherwise identical.
    /// </summary>
    public static async Task<byte[]> RunAsync(
        RhpChildConnection child,
        ConnectPlan plan,
        TimeSpan responseWait,
        TimeProvider time,
        ILogger logger,
        List<string>? transcript,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(child);
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(time);
        ArgumentNullException.ThrowIfNull(logger);

        if (plan.Steps.Count == 0)
        {
            return [];
        }

        var buffer = new ScriptLineBuffer();
        transcript ??= [];
        int lastStep = plan.Steps.Count - 1;
        for (int i = 0; i < plan.Steps.Count; i++)
        {
            if (plan.Steps[i] is not ExpectSendStep step)
            {
                throw new InvalidOperationException($"Unknown step type {plan.Steps[i].GetType().Name}.");
            }

            // Expect/send (bpqchat ScriptStep): an explicit EXPECT= waits for that substring BEFORE
            // sending — the ONLY reliable way to gate on a node prompt, since prompts are not
            // standardised across BPQ/URONode/XRouter/etc. THIS is how a multi-hop walk confirms each
            // hop (its prompt seen) before issuing the next command.
            if (step.Expect.Length > 0)
            {
                await ExpectAsync(
                    child, buffer, transcript, step.Expect,
                    responseWait, time, cancellationToken).ConfigureAwait(false);
            }

            if (step.Send.Length > 0)
            {
                LogScriptSend(logger, child.RemoteCallsign, step.Send, null);
                transcript.Add("> " + step.Send);
                await child.SendAsync(Encoding.Latin1.GetBytes(step.Send + "\r"), cancellationToken)
                    .ConfigureAwait(false);
                if (step.Expect.Length > 0)
                {
                    LogScriptMatched(logger, child.RemoteCallsign, step.Expect, step.Send, null);
                }
            }

            // A bare send-only step (no EXPECT=, the legacy verbatim form) has no standardised prompt
            // to wait for, so it goes out verbatim; between bare steps (not the last) we make a
            // best-effort wait for node progress — a " CONNECTED"/"OK"/">" line — before the next. For
            // a node whose prompt does not fit that shape, set an explicit EXPECT= for that step.
            if (step.Expect.Length == 0 && step.Send.Length > 0 && i != lastStep)
            {
                await WaitForAsync(
                    child, buffer, transcript, IsProgress, "node progress",
                    responseWait, time, logger, cancellationToken).ConfigureAwait(false);
            }
        }

        // The hand-to-FBB wait: accept ONLY a real FBB SID ("[…-…]") — the one standardised signal in
        // this whole exchange — never a bare ">"-prompt. An intermediate gateway banner ending ">"
        // (e.g. a URONode "…Help: ? <command>") must NOT be mistaken for the partner's SID; that was
        // the multi-hop wrong-banner capture. Node chatter before the SID stays consumed.
        string final = await WaitForAsync(
            child, buffer, transcript, Sid.IsSidShaped,
            "the partner SID", responseWait, time, logger, cancellationToken).ConfigureAwait(false);

        byte[] tail = buffer.TakeRemaining();
        byte[] head = Encoding.Latin1.GetBytes(final + "\r");
        byte[] initial = new byte[head.Length + tail.Length];
        head.CopyTo(initial, 0);
        tail.CopyTo(initial, head.Length);
        return initial;
    }

    /// <summary>
    /// Waits for the peer's SID-or-prompt line on a freshly-opened child, returning it (the same
    /// "after the last line BPQ waits for a SID or &gt;" wait the step runner does, spec §4.4). Used
    /// by the sysop test-connect tool for a STEPLESS plan (a bare open dialling the partner/BBS call
    /// directly), where the runner returns no bytes — the tester still wants to surface the prompt so
    /// the operator can tighten <c>EXPECT=</c> strings. Failure text or a timeout throws
    /// <see cref="ConnectScriptException"/>, exactly as the step path. Read-only: it consumes inbound
    /// bytes only; it never moves mail (no FBB session).
    /// </summary>
    public static async Task<string> WaitForPeerSidAsync(
        RhpChildConnection child,
        TimeSpan responseWait,
        TimeProvider time,
        ILogger logger,
        List<string> transcript,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(child);
        ArgumentNullException.ThrowIfNull(time);
        ArgumentNullException.ThrowIfNull(logger);
        ArgumentNullException.ThrowIfNull(transcript);

        var buffer = new ScriptLineBuffer();
        return await WaitForAsync(
            child, buffer, transcript, line => Sid.IsSidShaped(line) || line.TrimEnd().EndsWith('>'),
            "the partner SID (or prompt)", responseWait, time, logger, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Reads inbound bytes until the case-insensitive substring <paramref name="want"/> appears
    /// in the accumulated stream (a node prompt carries no line terminator — bpqchat's
    /// byte-window expect). Bytes up to and including the match are consumed (any complete lines
    /// within them are logged and failure-checked); the remainder stays buffered for the next
    /// step or the SID wait. Failure text or an empty <paramref name="wait"/> window fail the
    /// cycle, carrying the attempt transcript.
    /// </summary>
    private static async Task ExpectAsync(
        RhpChildConnection child,
        ScriptLineBuffer buffer,
        List<string> transcript,
        string want,
        TimeSpan wait,
        TimeProvider time,
        CancellationToken cancellationToken)
    {
        using var timeout = new CancellationTokenSource(wait, time);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeout.Token);
        while (true)
        {
            // Failure text anywhere in the complete lines seen so far fails the cycle, even if
            // the awaited prompt would otherwise be matched later.
            while (buffer.TryPeekFailingLine(out string failing))
            {
                transcript.Add("< " + failing);
                throw new ConnectScriptException(
                    $"connect script failed: node said \"{failing}\"{Render(transcript)}");
            }

            if (buffer.TryConsumeThrough(want))
            {
                transcript.Add("< (matched \"" + want + "\")");
                return;
            }

            byte[]? data;
            try
            {
                data = await child.ReceiveAsync(linked.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (timeout.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
            {
                throw new ConnectScriptException(
                    $"connect script timed out after {wait.TotalSeconds:0}s waiting for \"{want}\"{Render(transcript)}");
            }

            if (data is null)
            {
                throw new ConnectScriptException(
                    $"the link dropped while waiting for \"{want}\"{Render(transcript)}");
            }

            buffer.Feed(data);
        }
    }

    /// <summary>
    /// Consumes inbound lines until <paramref name="accept"/> matches one (returned);
    /// failure text or an empty <paramref name="wait"/> window fail the cycle, carrying
    /// the attempt transcript.
    /// </summary>
    private static async Task<string> WaitForAsync(
        RhpChildConnection child,
        ScriptLineBuffer buffer,
        List<string> transcript,
        Func<string, bool> accept,
        string expectation,
        TimeSpan wait,
        TimeProvider time,
        ILogger logger,
        CancellationToken cancellationToken)
    {
        using var timeout = new CancellationTokenSource(wait, time);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeout.Token);
        while (true)
        {
            while (buffer.TryTakeLine(out string line))
            {
                if (line.Length == 0)
                {
                    continue;
                }

                LogScriptLine(logger, child.RemoteCallsign, line, null);
                transcript.Add("< " + line);
                if (IsFailure(line))
                {
                    throw new ConnectScriptException(
                        $"connect script failed: node said \"{line}\"{Render(transcript)}");
                }

                if (accept(line))
                {
                    return line;
                }
            }

            byte[]? data;
            try
            {
                data = await child.ReceiveAsync(linked.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (timeout.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
            {
                throw new ConnectScriptException(
                    $"connect script timed out after {wait.TotalSeconds:0}s waiting for {expectation}{Render(transcript)}");
            }

            if (data is null)
            {
                throw new ConnectScriptException(
                    $"the link dropped while waiting for {expectation}{Render(transcript)}");
            }

            buffer.Feed(data);
        }
    }

    /// <summary>The attempt transcript, rendered for a failure message (wart 4: where did it stop?).</summary>
    private static string Render(List<string> transcript) =>
        transcript.Count == 0 ? "; transcript: (nothing exchanged)" : "; transcript: " + string.Join(" | ", transcript);

    private static bool IsFailure(string line) =>
        FailureMarkers.Any(m => line.Contains(m, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Progress per spec §4.4: <c>" CONNECTED"</c> (leading space — BPQ's own
    /// DISCONNECTED guard; failure markers are checked first anyway), an <c>OK</c>
    /// response, or a prompt line ending <c>&gt;</c>.
    /// </summary>
    private static bool IsProgress(string line) =>
        line.Contains(" CONNECTED", StringComparison.OrdinalIgnoreCase)
        || line.StartsWith("OK", StringComparison.OrdinalIgnoreCase)
        || line.TrimEnd().EndsWith('>');

    /// <summary>
    /// Line framing over a raw byte tail: CR/LF/CRLF-tolerant line pops with the
    /// unconsumed remainder recoverable — the FBB handoff needs bytes, not lines. Also
    /// supports the substring-based expect (a node prompt has no line terminator).
    /// </summary>
    private sealed class ScriptLineBuffer
    {
        private readonly List<byte> _buffer = [];
        private bool _skipNextLf;

        public void Feed(byte[] data) => _buffer.AddRange(data);

        /// <summary>
        /// If a complete line whose text contains a failure marker is already buffered (anywhere
        /// before the next terminator), pops and returns it. Lets the expect wait surface node
        /// failure text instead of blocking until ConTimeout.
        /// </summary>
        public bool TryPeekFailingLine(out string failing)
        {
            failing = "";
            int idx = TerminatorIndex();
            if (idx < 0)
            {
                return false;
            }

            string line = Encoding.Latin1.GetString([.. _buffer[..idx]]);
            if (!IsFailure(line))
            {
                return false;
            }

            ConsumeLineAt(idx);
            failing = line;
            return true;
        }

        /// <summary>
        /// If the accumulated bytes contain <paramref name="want"/> (case-insensitive), consumes
        /// everything up to AND including the match and returns true; the bytes after the match
        /// stay buffered. Otherwise leaves the buffer untouched and returns false.
        /// </summary>
        public bool TryConsumeThrough(string want)
        {
            if (want.Length == 0)
            {
                return true;
            }

            string haystack = Encoding.Latin1.GetString([.. _buffer]);
            int at = haystack.IndexOf(want, StringComparison.OrdinalIgnoreCase);
            if (at < 0)
            {
                return false;
            }

            int through = at + want.Length;
            _buffer.RemoveRange(0, through);
            _skipNextLf = false;
            return true;
        }

        public bool TryTakeLine(out string line)
        {
            line = "";
            if (_skipNextLf && _buffer.Count > 0)
            {
                if (_buffer[0] == 0x0A)
                {
                    _buffer.RemoveAt(0);
                }

                _skipNextLf = false;
            }

            int idx = TerminatorIndex();
            if (idx < 0)
            {
                return false;
            }

            line = Encoding.Latin1.GetString([.. _buffer[..idx]]);
            ConsumeLineAt(idx);
            return true;
        }

        public byte[] TakeRemaining()
        {
            if (_skipNextLf && _buffer.Count > 0 && _buffer[0] == 0x0A)
            {
                _buffer.RemoveAt(0);
            }

            byte[] rest = [.. _buffer];
            _buffer.Clear();
            return rest;
        }

        private int TerminatorIndex()
        {
            for (int i = 0; i < _buffer.Count; i++)
            {
                if (_buffer[i] is 0x0D or 0x0A)
                {
                    return i;
                }
            }

            return -1;
        }

        private void ConsumeLineAt(int idx)
        {
            int remove = idx + 1;
            if (_buffer[idx] == 0x0D)
            {
                if (idx + 1 < _buffer.Count)
                {
                    if (_buffer[idx + 1] == 0x0A)
                    {
                        remove++;
                    }
                }
                else
                {
                    _skipNextLf = true;
                }
            }

            _buffer.RemoveRange(0, remove);
        }
    }

    private static readonly Action<ILogger, string, string, Exception?> LogScriptSend =
        LoggerMessage.Define<string, string>(LogLevel.Information, new EventId(1, "ScriptSend"),
            "Connect script to {Remote}: sending \"{Line}\"");

    private static readonly Action<ILogger, string, string, Exception?> LogScriptLine =
        LoggerMessage.Define<string, string>(LogLevel.Debug, new EventId(2, "ScriptLine"),
            "Connect script to {Remote}: node said \"{Line}\"");

    private static readonly Action<ILogger, string, string, string, Exception?> LogScriptMatched =
        LoggerMessage.Define<string, string, string>(LogLevel.Information, new EventId(3, "ScriptMatched"),
            "Connect script to {Remote}: matched \"{Expect}\", sent \"{Send}\"");
}
