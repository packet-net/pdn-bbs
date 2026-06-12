using System.Globalization;
using System.Text;
using Bbs.Core;
using Bbs.SevenPlus;
using Microsoft.Extensions.Logging;

namespace Bbs.Host.Forwarding;

/// <summary>
/// Decodes inbound 7plus files and surfaces them as ordinary messages with an attachment, hiding
/// the raw part-bulletins from the user (design.md "abstract 7plus away from the user when a 7plus
/// file is received").
///
/// A 7plus file arrives as a run of part-bulletins — each inbound message body carries one or more
/// <c>go_7+</c>…<c>stop_7+</c> parts, trickling in over time. After each inbound message is stored,
/// <see cref="ProcessInbound"/> scans its body for parts, records each against its file identity in
/// the store (schema v3 tracking tables), and — when a file's set is complete — reassembles it and
/// creates a synthesized <see cref="Message.LocalOnly"/> message carrying the decoded bytes as a
/// single <see cref="MessageAttachment"/>. The synthesized message lists normally; the raw parts are
/// hidden by the webmail listing (they stay in the store and still forward).
///
/// Forward-safety: the synthesized message is <c>local_only</c>, so it is never forwarded and its
/// (auto-allocated) BID is never recorded in the network dedup store — see <see cref="BbsStore"/>.
/// It is NOT re-scanned for 7plus (it carries an attachment, not 7plus text), so there is no
/// recursion.
///
/// Named deferrals (NOT built here — see the PR body):
///   * The send-side (webmail compose file-upload + a "7plus-encode?" toggle using SevenPlusEncoder)
///     is the next slice; the encoder already exists in the lib.
///   * 7plus error-correction recovery — the codec reports corruption/missing, it does not recover;
///     this matches that (a still-incomplete or corrupt set surfaces only the placeholder).
/// </summary>
public sealed class SevenPlusAssembler
{
    // The header magic substring (SevenPlusLines.HeaderMagic is " go_7+. "). A body without this
    // can carry no part, so the fast pre-check skips the full scan — the overwhelming common case.
    private static readonly byte[] HeaderMagic = "go_7+"u8.ToArray();

    private readonly BbsStore _store;
    private readonly ILogger _logger;

    /// <summary>Creates the assembler.</summary>
    public SevenPlusAssembler(BbsStore store, ILogger<SevenPlusAssembler> logger)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(logger);
        _store = store;
        _logger = logger;
    }

    /// <summary>
    /// Processes one just-stored inbound message: scans its body for 7plus parts, records them, and
    /// assembles + surfaces any file whose set is now complete. A no-op (cheap pre-check, no scan)
    /// for a body without the 7plus magic — the common case. A <c>local_only</c> message (e.g. one we
    /// synthesized) is never scanned. Returns the synthesized message(s) created this call (usually
    /// none); the caller may use them for logging/tests.
    /// </summary>
    public IReadOnlyList<Message> ProcessInbound(Message stored)
    {
        ArgumentNullException.ThrowIfNull(stored);

        // Never re-scan our own synthesized artifact (it carries an attachment, not 7plus text).
        if (stored.LocalOnly)
        {
            return [];
        }

        ReadOnlySpan<byte> body = stored.Body.Span;
        if (!ContainsMagic(body))
        {
            return []; // fast path: no 7plus magic → nothing to do
        }

        IReadOnlyList<SevenPlusPart> parts = SevenPlusScanner.ExtractParts(body);
        if (parts.Count == 0)
        {
            return []; // magic-shaped text that did not parse to any well-formed part
        }

        // Record every part, collecting the distinct file identities this message touched so we
        // attempt assembly once per file (a single message can carry parts of several files).
        var touched = new Dictionary<string, SevenPlusPartIdentity>(StringComparer.Ordinal);
        foreach (SevenPlusPart part in parts)
        {
            string key = IdentityKey(part.Identity);
            _store.RecordSevenPlusPart(
                key,
                part.Identity.HeaderName,
                part.Identity.FileSize,
                part.Identity.TotalParts,
                part.Identity.BlockLines,
                part.PartNumber,
                stored.Number);
            touched[key] = part.Identity;
        }

        var synthesized = new List<Message>();
        foreach ((string key, SevenPlusPartIdentity identity) in touched)
        {
            Message? message = TryAssembleAndSurface(key, identity, stored);
            if (message is not null)
            {
                synthesized.Add(message);
            }
        }

        return synthesized;
    }

    /// <summary>
    /// Attempts to assemble one file by identity and, on a complete byte-exact decode, creates the
    /// synthesized <c>local_only</c> message. Returns it, or null when the set is still incomplete /
    /// failed to assemble / was already assembled by a prior message.
    /// </summary>
    private Message? TryAssembleAndSurface(string key, SevenPlusPartIdentity identity, Message sourceBulletin)
    {
        SevenPlusProgress? progress = _store.GetSevenPlusProgress(key);
        if (progress is null || progress.AssembledMessageNumber is not null || !progress.IsComplete)
        {
            // Still accumulating, or already assembled — the placeholder shows progress meanwhile.
            return null;
        }

        // Re-scan every recorded source part body and reassemble. ExtractParts is tolerant of the
        // surrounding mail text, so storing the whole bodies (not just the part lines) is fine. Keep
        // ONLY parts matching this file's identity: a single source message can carry parts of
        // several files, and TryAssemble's most-represented-identity heuristic would otherwise pick
        // the wrong one when the counts tie. Filtering pins assembly to exactly this file.
        var matchingParts = new List<SevenPlusPart>();
        foreach (ReadOnlyMemory<byte> partBody in _store.GetSevenPlusPartBodies(key))
        {
            foreach (SevenPlusPart part in SevenPlusScanner.ExtractParts(partBody.Span))
            {
                if (part.Identity.Equals(identity))
                {
                    matchingParts.Add(part);
                }
            }
        }

        (bool complete, byte[]? content, AssemblyReport report) = SevenPlusFile.TryAssemble(matchingParts);
        if (!complete || content is null)
        {
            // The count says complete but a code line is missing/corrupt (7plus has no recovery).
            // Leave the tracking as-is; the placeholder keeps showing N/M and a re-sent part may
            // complete it later. No synthesized message.
            LogIncomplete(_logger, report.FileName, report.CorruptedLines, report.MissingLines, null);
            return null;
        }

        string fileName = string.IsNullOrWhiteSpace(report.FileName) ? identity.HeaderName.Trim() : report.FileName;
        DateTimeOffset assembledAt = AssembledTimestamp(report);
        Message synthesized = _store.AddMessage(new MessageDraft
        {
            // Same Type (B/P) + category/AT as the source bulletin run, so the assembled file lists
            // alongside the messages it arrived as. From is the part-messages' From.
            Type = sourceBulletin.Type,
            From = sourceBulletin.From,
            Recipients = SourceRecipients(sourceBulletin),
            At = sourceBulletin.At,
            Subject = Truncate(fileName, Message.MaxSubjectLength),
            Body = Encoding.Latin1.GetBytes(BodyText(fileName, identity.TotalParts, assembledAt)),
            Attachments = [new MessageAttachment(fileName, content)],
            LocalOnly = true,
        });

        // Guarded link: only the first completer wins (MarkSevenPlusAssembled sets the link only
        // while NULL). A loser would have created a duplicate synthesized message — kill it so the
        // store never shows two. In practice the per-store lock serialises inbound, so the loser
        // path is the defensive belt-and-braces case, not the hot path.
        if (!_store.MarkSevenPlusAssembled(key, synthesized.Number))
        {
            _store.Kill(synthesized.Number);
            return null;
        }

        LogAssembled(_logger, fileName, identity.TotalParts, synthesized.Number, null);
        return synthesized;
    }

    /// <summary>The To-recipients of the source bulletin (the assembled file goes to the same audience).</summary>
    private static List<string> SourceRecipients(Message source)
    {
        var to = new List<string>();
        foreach (MessageRecipient r in source.Recipients)
        {
            if (!r.Cc)
            {
                to.Add(r.ToCall);
            }
        }

        // A part-bulletin always has at least one To-recipient (the store requires one); fall back to
        // From only in the impossible all-Cc case so AddMessage never throws.
        return to.Count > 0 ? to : [source.From];
    }

    private static string BodyText(string fileName, int totalParts, DateTimeOffset assembledAt)
    {
        string date = assembledAt.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        return string.Create(
            CultureInfo.InvariantCulture,
            $"7plus file {fileName} — {totalParts} parts, assembled {date}.\r");
    }

    /// <summary>The footer timestamp when the parts carried one, else now is unknown so use epoch-free fallback.</summary>
    private static DateTimeOffset AssembledTimestamp(AssemblyReport report)
        => report.Timestamp is { } ts
            ? DateTimeOffset.FromUnixTimeSeconds(ts)
            : DateTimeOffset.UnixEpoch;

    /// <summary>
    /// The stable identity string for a <see cref="SevenPlusPartIdentity"/> — the cross-message
    /// grouping key. Components are joined with a separator that cannot appear in a numeric field and
    /// the header name is length-prefixed so a name containing the separator can never alias another
    /// identity.
    /// </summary>
    internal static string IdentityKey(SevenPlusPartIdentity id)
        => string.Create(
            CultureInfo.InvariantCulture,
            $"7p|{id.HeaderName.Length}|{id.HeaderName}|{id.FileSize}|{id.TotalParts}|{id.BlockLines}");

    private static bool ContainsMagic(ReadOnlySpan<byte> body) => body.IndexOf(HeaderMagic) >= 0;

    private static string Truncate(string value, int max) => value.Length <= max ? value : value[..max];

    private static readonly Action<ILogger, string, int, long, Exception?> LogAssembled =
        LoggerMessage.Define<string, int, long>(LogLevel.Information, new EventId(1, "SevenPlusAssembled"),
            "Assembled 7plus file {FileName} ({Parts} parts) → local message {Number}");

    private static readonly Action<ILogger, string, int, int, Exception?> LogIncomplete =
        LoggerMessage.Define<string, int, int>(LogLevel.Information, new EventId(2, "SevenPlusIncomplete"),
            "7plus file {FileName} has all parts but {Corrupt} corrupt + {Missing} missing lines — not surfaced (no recovery)");
}
