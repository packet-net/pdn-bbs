using System.Text;
using System.Text.RegularExpressions;
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
            await RunStepAsync(child, buffer, transcript, plan.Steps[i], responseWait, time, logger, i == lastStep, cancellationToken).ConfigureAwait(false);
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
    /// Runs one expect/send step: wait for its prompt (if any), then send (if any); a bare send-only
    /// step that is not the last makes a best-effort node-progress wait before the next. Shared by the
    /// full <see cref="RunAsync(RhpChildConnection,ConnectPlan,TimeSpan,TimeProvider,ILogger,List{string},CancellationToken)"/>
    /// and the editor <see cref="ProbeStreamAsync"/>.
    /// </summary>
    private static async Task RunStepAsync(
        RhpChildConnection child,
        ScriptLineBuffer buffer,
        List<string> transcript,
        ConnectScriptStep rawStep,
        TimeSpan responseWait,
        TimeProvider time,
        ILogger logger,
        bool isLast,
        CancellationToken cancellationToken)
    {
        if (rawStep is not ExpectSendStep step)
        {
            throw new InvalidOperationException($"Unknown step type {rawStep.GetType().Name}.");
        }

        // The per-step Timeout overrides the partner ConTimeout for this wait; Match/IgnoreCase/ExpectAny
        // pick how the prompt is matched. An explicit expect gates the send on the node prompt.
        TimeSpan wait = step.Timeout ?? responseWait;
        bool hasWait = step.Expect.Length > 0 || step.ExpectAny is { Count: > 0 };
        string matched = string.Empty;
        if (hasWait)
        {
            matched = await ExpectAsync(child, buffer, transcript, step, wait, time, cancellationToken).ConfigureAwait(false);
        }

        if (step.Send.Length > 0)
        {
            LogScriptSend(logger, child.RemoteCallsign, step.Send, null);
            transcript.Add("> " + StepLabel(step) + step.Send);
            string payload = step.Raw ? ExpandEscapes(step.Send) : step.Send;
            await child.SendAsync(Encoding.Latin1.GetBytes(payload + EolBytes(step.Eol)), cancellationToken)
                .ConfigureAwait(false);
            if (hasWait)
            {
                LogScriptMatched(logger, child.RemoteCallsign, matched, step.Send, null);
            }
        }

        // A send-only step (no expect) has no standardised prompt to wait for, so it goes out verbatim;
        // between such steps (not the last) make a best-effort wait for node progress before the next.
        if (!hasWait && step.Send.Length > 0 && !isLast)
        {
            await WaitForAsync(
                child, buffer, transcript, IsProgress, "node progress",
                wait, time, logger, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Runs the plan's steps up to (but not including) <paramref name="stopBefore"/>, then STREAMS every
    /// byte the node emits at that point live via <paramref name="emit"/> — so the sysop step editor can
    /// show the dialogue and let the operator judge, by eye, when the prompt has settled (auto-detecting
    /// "the far end has finished sending" is unreliable on packet RF). The prefix dialogue is emitted as
    /// <c>line</c> events; the live capture as <c>chunk</c> events; a prefix failure as an <c>error</c>
    /// event; the cap or link-drop as <c>end</c>. Streaming continues until <paramref name="cancellationToken"/>
    /// (the operator closed the stream / pressed the button) or <paramref name="maxHold"/>. Moves no mail.
    /// </summary>
    public static async Task ProbeStreamAsync(
        RhpChildConnection child,
        ConnectPlan plan,
        int stopBefore,
        TimeSpan responseWait,
        TimeProvider time,
        ILogger logger,
        List<string> transcript,
        Func<ProbeEvent, Task> emit,
        TimeSpan maxHold,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(child);
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(time);
        ArgumentNullException.ThrowIfNull(logger);
        ArgumentNullException.ThrowIfNull(transcript);
        ArgumentNullException.ThrowIfNull(emit);

        var buffer = new ScriptLineBuffer();
        int upto = Math.Clamp(stopBefore, 0, plan.Steps.Count);
        int emitted = 0;
        for (int i = 0; i < upto; i++)
        {
            try
            {
                await RunStepAsync(child, buffer, transcript, plan.Steps[i], responseWait, time, logger, i == upto - 1, cancellationToken).ConfigureAwait(false);
            }
            catch (ConnectScriptException ex)
            {
                for (; emitted < transcript.Count; emitted++)
                {
                    await emit(new ProbeEvent("line", transcript[emitted])).ConfigureAwait(false);
                }

                await emit(new ProbeEvent("error", ex.Message)).ConfigureAwait(false);
                return;
            }

            for (; emitted < transcript.Count; emitted++)
            {
                await emit(new ProbeEvent("line", transcript[emitted])).ConfigureAwait(false);
            }
        }

        // The live capture — every byte the node emits here is streamed so the operator can WATCH and
        // decide when the prompt has settled (the awaited prompt has no expect yet, so nothing can gate it).
        byte[] leftover = buffer.TakeRemaining();
        if (leftover.Length > 0)
        {
            await emit(new ProbeEvent("chunk", Encoding.Latin1.GetString(leftover))).ConfigureAwait(false);
        }

        using var holdCts = new CancellationTokenSource(maxHold, time);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, holdCts.Token);
        try
        {
            while (true)
            {
                byte[]? data = await child.ReceiveAsync(linked.Token).ConfigureAwait(false);
                if (data is null)
                {
                    break; // link dropped
                }

                await emit(new ProbeEvent("chunk", Encoding.Latin1.GetString(data))).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return; // the operator closed the stream (pressed the button / cancelled) — nothing more to say
        }
        catch (OperationCanceledException)
        {
            // maxHold reached — fall through to end.
        }

        await emit(new ProbeEvent("end", "")).ConfigureAwait(false);
    }

    /// <summary>
    /// Runs the WHOLE plan (every step, then the hand-to-FBB SID wait) for the sysop "test connect" tool,
    /// emitting each transcript line LIVE via <paramref name="emit"/> as it happens, and returns the
    /// verdict (Ok + the observed SID, or the failure reason). A plan with steps waits for a real FBB SID
    /// (never a bare prompt — the multi-hop wrong-banner guard); a STEPLESS plan accepts the SID-or-prompt.
    /// Moves no mail (no FBB session, no queue). Like <see cref="ProbeStreamAsync"/> but for the full test.
    /// </summary>
    public static async Task<(bool Ok, string? Sid, string? Error)> RunStreamAsync(
        RhpChildConnection child,
        ConnectPlan plan,
        TimeSpan responseWait,
        TimeProvider time,
        ILogger logger,
        List<string> transcript,
        Func<ProbeEvent, Task> emit,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(child);
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(time);
        ArgumentNullException.ThrowIfNull(logger);
        ArgumentNullException.ThrowIfNull(transcript);
        ArgumentNullException.ThrowIfNull(emit);

        var buffer = new ScriptLineBuffer();
        int emitted = 0;
        async Task Flush()
        {
            for (; emitted < transcript.Count; emitted++)
            {
                await emit(new ProbeEvent("line", transcript[emitted])).ConfigureAwait(false);
            }
        }

        int lastStep = plan.Steps.Count - 1;
        try
        {
            for (int i = 0; i < plan.Steps.Count; i++)
            {
                await RunStepAsync(child, buffer, transcript, plan.Steps[i], responseWait, time, logger, i == lastStep, cancellationToken).ConfigureAwait(false);
                await Flush().ConfigureAwait(false);
            }

            // The hand-to-FBB wait: a plan WITH steps holds out for a real FBB SID (never a bare ">"
            // gateway banner); a STEPLESS plan (a bare open) surfaces the SID-or-prompt the answerer sends.
            string sid = plan.Steps.Count == 0
                ? await WaitForAsync(child, buffer, transcript, line => Sid.IsSidShaped(line) || line.TrimEnd().EndsWith('>'), "the partner SID (or prompt)", responseWait, time, logger, cancellationToken).ConfigureAwait(false)
                : await WaitForAsync(child, buffer, transcript, Sid.IsSidShaped, "the partner SID", responseWait, time, logger, cancellationToken).ConfigureAwait(false);
            await Flush().ConfigureAwait(false);
            return (true, sid, null);
        }
        catch (ConnectScriptException ex)
        {
            await Flush().ConfigureAwait(false);
            return (false, null, ex.Message);
        }
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
    /// Reads inbound bytes until the step's prompt appears in the accumulated stream (a node prompt
    /// carries no line terminator — bpqchat's byte-window expect). The match honours the step's
    /// <see cref="ExpectSendStep.Match"/> (substring / exact-line / regex), <see cref="ExpectSendStep.IgnoreCase"/>,
    /// and <see cref="ExpectSendStep.ExpectAny"/> (first alternative to appear wins). Bytes up to and
    /// including the match are consumed; the remainder stays buffered for the next step or the SID wait.
    /// Failure text or an empty <paramref name="wait"/> window fail the cycle, carrying the attempt transcript.
    /// </summary>
    private static async Task<string> ExpectAsync(
        RhpChildConnection child,
        ScriptLineBuffer buffer,
        List<string> transcript,
        ExpectSendStep step,
        TimeSpan wait,
        TimeProvider time,
        CancellationToken cancellationToken)
    {
        IReadOnlyList<string> wants = step.ExpectAny is { Count: > 0 } any ? any : [step.Expect];
        Regex[]? regexes = null;
        if (step.Match == "regex")
        {
            RegexOptions options = step.IgnoreCase ? RegexOptions.IgnoreCase : RegexOptions.None;
            try
            {
                // A bounded match timeout caps catastrophic backtracking from an operator-supplied
                // pattern — a synchronous Regex.Match cannot be cancelled, so without this a pathological
                // pattern would wedge the forwarding loop with the link held open, immune to ConTimeout.
                regexes = [.. wants.Select(w => new Regex(w, options, TimeSpan.FromSeconds(2)))];
            }
            catch (ArgumentException ex)
            {
                throw new ConnectScriptException(
                    $"connect script{StepSuffix(step)}: invalid regex — {ex.Message}{Render(transcript)}");
            }
        }

        string desc = WantDescription(step);
        using var timeout = new CancellationTokenSource(wait, time);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeout.Token);
        while (true)
        {
            // A failure marker at the head of the buffer fails the cycle before we wait further.
            while (buffer.TryPeekFailingLine(out string failing))
            {
                transcript.Add("< " + failing);
                throw new ConnectScriptException(
                    $"connect script failed{StepSuffix(step)}: node said \"{failing}\"{Render(transcript)}");
            }

            bool ok;
            string matched, before;
            try
            {
                ok = TryMatch(buffer, step, wants, regexes, out matched, out before);
            }
            catch (RegexMatchTimeoutException)
            {
                throw new ConnectScriptException(
                    $"connect script{StepSuffix(step)}: regex match timed out — the pattern is too costly{Render(transcript)}");
            }

            if (ok)
            {
                // A failure marker among the lines consumed BEFORE the awaited prompt (e.g. a BUSY line
                // batched ahead of the prompt in one read) fails the cycle rather than being swallowed.
                if (FirstFailingLine(before) is { } failingBefore)
                {
                    transcript.Add("< " + failingBefore);
                    throw new ConnectScriptException(
                        $"connect script failed{StepSuffix(step)}: node said \"{failingBefore}\"{Render(transcript)}");
                }

                transcript.Add("< (matched " + StepLabel(step) + "\"" + matched + "\")");
                return matched;
            }

            byte[]? data;
            try
            {
                data = await child.ReceiveAsync(linked.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (timeout.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
            {
                throw new ConnectScriptException(
                    $"connect script timed out after {wait.TotalSeconds:0}s waiting for {desc}{StepSuffix(step)}{Render(transcript)}");
            }

            if (data is null)
            {
                throw new ConnectScriptException(
                    $"the link dropped while waiting for {desc}{StepSuffix(step)}{Render(transcript)}");
            }

            buffer.Feed(data);
        }
    }

    /// <summary>
    /// Tries each want against the buffer per the step's match mode; consumes through the first that
    /// matches. <paramref name="before"/> receives the text consumed BEFORE the match (for a failure scan).
    /// </summary>
    private static bool TryMatch(ScriptLineBuffer buffer, ExpectSendStep step, IReadOnlyList<string> wants, Regex[]? regexes, out string matched, out string before)
    {
        before = string.Empty;
        for (int i = 0; i < wants.Count; i++)
        {
            bool ok = step.Match switch
            {
                "regex" => buffer.TryConsumeRegex(regexes![i], out before),
                "exact-line" => buffer.TryConsumeExactLine(wants[i], step.IgnoreCase, out before),
                _ => buffer.TryConsumeThrough(wants[i], step.IgnoreCase, out before),
            };
            if (ok)
            {
                matched = wants[i];
                return true;
            }
        }

        matched = string.Empty;
        return false;
    }

    /// <summary>The first complete line within <paramref name="text"/> that contains a failure marker, or null.</summary>
    private static string? FirstFailingLine(string text)
    {
        foreach (string line in text.Split('\r', '\n'))
        {
            if (line.Length > 0 && IsFailure(line))
            {
                return line;
            }
        }

        return null;
    }

    /// <summary>The send terminator bytes for the step's <see cref="ExpectSendStep.Eol"/> (default CR).</summary>
    internal static string EolBytes(string eol) => eol switch
    {
        "lf" => "\n",
        "crlf" => "\r\n",
        "none" => "",
        _ => "\r",
    };

    /// <summary>Expands C-style escapes (<c>\r \n \t \xNN \\</c>) so raw control bytes (e.g. Ctrl-Z = <c>\x1a</c>) can be sent. Lenient: an unrecognised escape keeps the following character verbatim.</summary>
    internal static string ExpandEscapes(string send)
    {
        var sb = new StringBuilder(send.Length);
        for (int i = 0; i < send.Length; i++)
        {
            char c = send[i];
            if (c != '\\' || i + 1 >= send.Length)
            {
                sb.Append(c);
                continue;
            }

            char next = send[++i];
            switch (next)
            {
                case 'r': sb.Append('\r'); break;
                case 'n': sb.Append('\n'); break;
                case 't': sb.Append('\t'); break;
                case '\\': sb.Append('\\'); break;
                case 'x':
                case 'X':
                    int start = i + 1;
                    int len = 0;
                    while (len < 2 && start + len < send.Length && Uri.IsHexDigit(send[start + len]))
                    {
                        len++;
                    }

                    if (len > 0)
                    {
                        sb.Append((char)Convert.ToInt32(send.Substring(start, len), 16));
                        i = start + len - 1;
                    }
                    else
                    {
                        sb.Append(next);
                    }

                    break;
                default: sb.Append(next); break;
            }
        }

        return sb.ToString();
    }

    private static string StepLabel(ExpectSendStep step) => step.Name is null ? string.Empty : $"[{step.Name}] ";

    private static string StepSuffix(ExpectSendStep step) => step.Name is null ? string.Empty : $" at step \"{step.Name}\"";

    private static string WantDescription(ExpectSendStep step) =>
        step.ExpectAny is { Count: > 0 } any
            ? "one of " + string.Join(", ", any.Select(w => $"\"{w}\""))
            : $"\"{step.Expect}\"";

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
        /// If the accumulated bytes contain <paramref name="want"/> as a substring, consumes
        /// everything up to AND including the match and returns true; the bytes after the match
        /// stay buffered. Otherwise leaves the buffer untouched and returns false.
        /// </summary>
        public bool TryConsumeThrough(string want, bool ignoreCase, out string before)
        {
            before = string.Empty;
            if (want.Length == 0)
            {
                return true;
            }

            string haystack = Encoding.Latin1.GetString([.. _buffer]);
            int at = haystack.IndexOf(want, ignoreCase ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
            if (at < 0)
            {
                return false;
            }

            before = haystack[..at];
            ConsumeBytes(at + want.Length);
            return true;
        }

        /// <summary>
        /// If <paramref name="pattern"/> matches anywhere in the accumulated bytes, consumes through the
        /// end of the match and returns true; otherwise leaves the buffer untouched and returns false. A
        /// zero-length match is NOT accepted (a degenerate pattern must not fire the gate before any data).
        /// </summary>
        public bool TryConsumeRegex(Regex pattern, out string before)
        {
            before = string.Empty;
            string haystack = Encoding.Latin1.GetString([.. _buffer]);
            System.Text.RegularExpressions.Match m = pattern.Match(haystack);
            if (!m.Success || m.Length == 0)
            {
                return false;
            }

            before = haystack[..m.Index];
            ConsumeBytes(m.Index + m.Length);
            return true;
        }

        /// <summary>
        /// If a complete line whose text equals <paramref name="want"/> is buffered, consumes everything
        /// up to AND including that line (its terminator) and returns true; otherwise leaves the buffer
        /// untouched and returns false. Unlike the substring/regex matches, this requires a full line
        /// (so the prompt must end in CR/LF). <paramref name="before"/> receives the lines preceding the match.
        /// </summary>
        public bool TryConsumeExactLine(string want, bool ignoreCase, out string before)
        {
            before = string.Empty;
            StringComparison cmp = ignoreCase ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
            int pos = 0;
            while (true)
            {
                int term = -1;
                for (int i = pos; i < _buffer.Count; i++)
                {
                    if (_buffer[i] is 0x0D or 0x0A)
                    {
                        term = i;
                        break;
                    }
                }

                if (term < 0)
                {
                    return false;
                }

                string line = Encoding.Latin1.GetString([.. _buffer.GetRange(pos, term - pos)]);
                if (line.Equals(want, cmp))
                {
                    before = Encoding.Latin1.GetString([.. _buffer.GetRange(0, pos)]);
                    ConsumeLineAt(term);
                    return true;
                }

                pos = term + 1;
                if (_buffer[term] == 0x0D && pos < _buffer.Count && _buffer[pos] == 0x0A)
                {
                    pos++;
                }
            }
        }

        private void ConsumeBytes(int count)
        {
            _buffer.RemoveRange(0, count);
            _skipNextLf = false;
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
