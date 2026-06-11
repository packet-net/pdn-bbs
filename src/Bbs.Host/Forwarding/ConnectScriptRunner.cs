using System.Text;
using Bbs.Fbb;
using Bbs.Host.Rhp;
using Microsoft.Extensions.Logging;

namespace Bbs.Host.Forwarding;

/// <summary>
/// A connect script failed (failure text from the node, or a response/SID wait timing
/// out). The message carries the attempt transcript — what we sent, what came back,
/// where it stopped (forwarding.md wart 4 / wave F-0).
/// </summary>
public sealed class ConnectScriptException(string message) : Exception(message);

/// <summary>
/// Executes a <see cref="ConnectPlan"/>'s post-connect steps over an open RHP child —
/// the spec §4.4 implicit flow ("you don't need to program the node responses - the
/// software knows what to look for"):
///
/// <list type="bullet">
/// <item>Each <see cref="SendLineStep"/> goes out verbatim, CR-terminated (the node
/// command-line discipline). After every line except the last, the runner waits for the
/// node's progress before the next — a line containing <c>" CONNECTED"</c> (the leading
/// space is BPQ's own guard against matching DISCONNECTED), starting <c>OK</c>, or a
/// <c>&gt;</c>-terminated prompt line.</item>
/// <item>After the LAST line it waits for a SID or <c>&gt;</c> (spec §4.4 verbatim) and
/// returns the SID line plus any unconsumed tail for the FBB caller session; node chatter
/// observed on the way (CTEXT, <c>*** Connected to …</c> progress lines) is consumed
/// here, where it belongs, so the FBB FSM sees a clean stream.</item>
/// <item>Failure text at any point — BUSY / FAILURE / SORRY / INVALID / RETRIED /
/// <c>ERROR - </c> / UNABLE TO CONNECT / DISCONNECTED / FAILED TO CONNECT / REJECTED
/// (the spec §4.4 ELSE-detection list) — fails the cycle, as does a wait exceeding the
/// partner's ConTimeout. A failed cycle surfaces as <see cref="ConnectScriptException"/>;
/// the scheduler retries with backoff.</item>
/// </list>
///
/// Named deviations from LinBPQ (spec §4.4): the failure/progress scans are
/// case-insensitive (a tolerant superset of BPQ's exact-case scan), and ConTimeout bounds
/// each response wait rather than the whole handshake. With no steps at all the runner is
/// a no-op — the bare-open path (dialling a BBS application callsign directly) keeps
/// today's behaviour, where the FBB caller FSM itself waits out the SID.
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
    /// plan). Throws <see cref="ConnectScriptException"/> on failure text or a timed-out
    /// wait.
    /// </summary>
    public static async Task<byte[]> RunAsync(
        RhpChildConnection child,
        ConnectPlan plan,
        TimeSpan responseWait,
        TimeProvider time,
        ILogger logger,
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
        var transcript = new List<string>();
        int lastSend = LastSendIndex(plan.Steps);
        for (int i = 0; i < plan.Steps.Count; i++)
        {
            switch (plan.Steps[i])
            {
                case PauseStep pause:
                    await Task.Delay(pause.Delay, time, cancellationToken).ConfigureAwait(false);
                    break;

                case SendLineStep send:
                    LogScriptSend(logger, child.RemoteCallsign, send.Line, null);
                    transcript.Add("> " + send.Line);
                    await child.SendAsync(Encoding.Latin1.GetBytes(send.Line + "\r"), cancellationToken)
                        .ConfigureAwait(false);
                    if (i != lastSend)
                    {
                        await WaitForAsync(
                            child, buffer, transcript, IsProgress, "node progress",
                            responseWait, time, logger, cancellationToken).ConfigureAwait(false);
                    }

                    break;

                default:
                    throw new InvalidOperationException($"Unknown step type {plan.Steps[i].GetType().Name}.");
            }
        }

        // "after the last line BPQ waits for a SID or >" (spec §4.4). The SID line is
        // re-presented to the FBB session; chatter before it stays consumed.
        string final = await WaitForAsync(
            child, buffer, transcript, line => Sid.IsSidShaped(line) || line.TrimEnd().EndsWith('>'),
            "the partner SID (or prompt)", responseWait, time, logger, cancellationToken).ConfigureAwait(false);

        byte[] tail = buffer.TakeRemaining();
        byte[] head = Encoding.Latin1.GetBytes(final + "\r");
        byte[] initial = new byte[head.Length + tail.Length];
        head.CopyTo(initial, 0);
        tail.CopyTo(initial, head.Length);
        return initial;
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

    private static int LastSendIndex(IReadOnlyList<ConnectScriptStep> steps)
    {
        for (int i = steps.Count - 1; i >= 0; i--)
        {
            if (steps[i] is SendLineStep)
            {
                return i;
            }
        }

        return -1;
    }

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
    /// unconsumed remainder recoverable — the FBB handoff needs bytes, not lines.
    /// </summary>
    private sealed class ScriptLineBuffer
    {
        private readonly List<byte> _buffer = [];
        private bool _skipNextLf;

        public void Feed(byte[] data) => _buffer.AddRange(data);

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

            int idx = -1;
            for (int i = 0; i < _buffer.Count; i++)
            {
                if (_buffer[i] is 0x0D or 0x0A)
                {
                    idx = i;
                    break;
                }
            }

            if (idx < 0)
            {
                return false;
            }

            line = Encoding.Latin1.GetString([.. _buffer[..idx]]);
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
    }

    private static readonly Action<ILogger, string, string, Exception?> LogScriptSend =
        LoggerMessage.Define<string, string>(LogLevel.Information, new EventId(1, "ScriptSend"),
            "Connect script to {Remote}: sending \"{Line}\"");

    private static readonly Action<ILogger, string, string, Exception?> LogScriptLine =
        LoggerMessage.Define<string, string>(LogLevel.Debug, new EventId(2, "ScriptLine"),
            "Connect script to {Remote}: node said \"{Line}\"");
}
