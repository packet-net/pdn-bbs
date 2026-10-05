using System.Globalization;
using System.Text;

namespace Bbs.Fbb;

/// <summary>
/// The <c>Type:</c> field of a B2F message — spec §3.9: one of
/// <c>Private | Bulletin | Service | Inquiry | Position Report |
/// Position Request | Option | System</c>. LinBPQ stores all B2 arrivals
/// internally as type <c>P</c> regardless (spec §3.9).
/// </summary>
public enum B2MessageType
{
    /// <summary><c>Private</c>.</summary>
    Private = 0,

    /// <summary><c>Bulletin</c>.</summary>
    Bulletin = 1,

    /// <summary><c>Service</c>.</summary>
    Service = 2,

    /// <summary><c>Inquiry</c>.</summary>
    Inquiry = 3,

    /// <summary><c>Position Report</c>.</summary>
    PositionReport = 4,

    /// <summary><c>Position Request</c>.</summary>
    PositionRequest = 5,

    /// <summary><c>Option</c>.</summary>
    Option = 6,

    /// <summary><c>System</c>.</summary>
    System = 7,
}

/// <summary>
/// One attachment of a B2F message — a <c>File: &lt;count&gt; &lt;name&gt;</c>
/// header line plus the raw bytes that follow the body (spec §3.9). The count
/// excludes the mandatory additional terminating CRLF.
/// </summary>
/// <param name="Name">The file name as it appears after the count on the <c>File:</c> line.</param>
/// <param name="Content">The exact attachment bytes (the <c>File:</c> count).</param>
public sealed record B2Attachment(string Name, ReadOnlyMemory<byte> Content);

/// <summary>
/// The B2F (Winlink/FBB B2) message object — spec §3.9. This is the
/// <em>plaintext</em> that an <c>FC EM</c> proposal advertises: it is
/// LZHUF-compressed and shipped through the existing B1 framing
/// (<see cref="LzhufContainer"/> + the SOH/STX/EOT block framing), so this
/// type only produces/consumes the object bytes — it does not frame them
/// (spec §3.7: "For FC, the plaintext is the entire B2 message").
/// </summary>
/// <remarks>
/// Sans-IO and transcript-testable: <see cref="Encode"/> emits the exact
/// byte layout LinBPQ generates (header order [BPQ-SRC FBBRoutines.c:1816],
/// <c>Body:</c>/<c>File:</c> counts excluding the terminating CRLF), and
/// <see cref="Decode"/> is parse-tolerant per the §3.9 rules.
/// </remarks>
public sealed record B2Message
{
    private static readonly byte[] Crlf = [(byte)'\r', (byte)'\n'];

    /// <summary>The message ID (spec §2.3); the <c>Mid:</c> line and MUST be first on the wire (spec §3.9).</summary>
    public required string Mid { get; init; }

    /// <summary>The message type (spec §3.9). Spelled out on the wire (<c>Private</c>, …).</summary>
    public required B2MessageType Type { get; init; }

    /// <summary>The recipient callsigns/addresses — repeated <c>To:</c> lines (spec §3.9). At least one required on emit.</summary>
    public required IReadOnlyList<string> To { get; init; }

    /// <summary>The body bytes (the <c>Body:</c> count; the trailing CRLF is additional). Non-empty on emit (spec §3.9).</summary>
    public required ReadOnlyMemory<byte> Body { get; init; }

    /// <summary>The carbon-copy addresses — repeated <c>Cc:</c> lines (spec §3.9).</summary>
    public IReadOnlyList<string> Cc { get; init; } = [];

    /// <summary>The <c>Date:</c> value, format <c>YYYY/MM/DD HH:MM</c> (spec §3.9). Omitted from the header when empty.</summary>
    public string? Date { get; init; }

    /// <summary>The <c>From:</c> value (spec §3.9). Omitted from the header when empty.</summary>
    public string? From { get; init; }

    /// <summary>The <c>Subject:</c> value (spec §3.9). Omitted from the header when empty.</summary>
    public string? Subject { get; init; }

    /// <summary>The <c>Mbo:</c> value — the originating BBS (spec §3.9). Omitted from the header when empty.</summary>
    public string? Mbo { get; init; }

    /// <summary>The attachments, in <c>File:</c> order = wire order (spec §3.9).</summary>
    public IReadOnlyList<B2Attachment> Files { get; init; } = [];

    /// <summary>
    /// Encodes the message to its exact §3.9 byte layout, in the canonical
    /// LinBPQ header order [BPQ-SRC FBBRoutines.c:1816]:
    /// <c>MID, Date, Type, From, To…, Cc…, Subject, Mbo, Content-Type,
    /// Content-Transfer-Encoding, Body, File…</c>, a blank line, then the
    /// body and each attachment, every one followed by the mandatory
    /// additional terminating CRLF (which the <c>Body:</c>/<c>File:</c> counts
    /// exclude).
    /// </summary>
    /// <exception cref="FbbProtocolException">The body is empty or there is no recipient (spec §3.9).</exception>
    public byte[] Encode()
    {
        if (Body.Length == 0)
        {
            throw new FbbProtocolException("B2 message body may not be empty (spec §3.9).");
        }

        if (To.Count == 0)
        {
            throw new FbbProtocolException("B2 message requires at least one To: recipient (spec §3.9).");
        }

        var header = new StringBuilder();
        AppendField(header, "MID", Mid);
        AppendOptional(header, "Date", Date);
        AppendField(header, "Type", TypeWord(Type));
        AppendOptional(header, "From", From);
        foreach (var to in To)
        {
            AppendField(header, "To", to);
        }

        foreach (var cc in Cc)
        {
            AppendField(header, "Cc", cc);
        }

        AppendOptional(header, "Subject", Subject);
        AppendOptional(header, "Mbo", Mbo);
        AppendField(header, "Content-Type", "text/plain");
        AppendField(header, "Content-Transfer-Encoding", "8bit");
        AppendField(header, "Body", Body.Length.ToString(CultureInfo.InvariantCulture));
        foreach (var file in Files)
        {
            AppendField(
                header,
                "File",
                string.Create(CultureInfo.InvariantCulture, $"{file.Content.Length} {file.Name}"));
        }

        header.Append("\r\n"); // blank line: separates header from body

        using var ms = new MemoryStream();
        // UTF-8 egress for the header text: field names + structure are ASCII so
        // they are byte-identical on the wire; only a non-ASCII VALUE (e.g. a
        // UTF-8 Subject) carries multibyte bytes. ASCII subjects are unchanged;
        // a received UTF-8 Subject round-trips byte-for-byte (PacketText).
        var headerBytes = PacketText.EncodeHeader(header.ToString());
        ms.Write(headerBytes);
        ms.Write(Body.Span);
        ms.Write(Crlf); // mandatory additional terminating CRLF
        foreach (var file in Files)
        {
            ms.Write(file.Content.Span);
            ms.Write(Crlf);
        }

        return ms.ToArray();
    }

    /// <summary>
    /// Parses a B2F object per the §3.9 tolerance rules: header text is decoded
    /// UTF-8-when-valid else Latin-1 (field names are ASCII; this only matters
    /// for non-ASCII values such as a UTF-8 <c>Subject:</c>, see
    /// <see cref="PacketText"/>), case-insensitive field <em>names</em>
    /// (values keep case), <c>Mid:</c>
    /// MUST be the first header line, unknown fields ignored, a blank line
    /// separates header from body, the body may not be empty, <c>To:</c>/
    /// <c>Cc:</c> may repeat, and the <c>@MPS@R</c> suffix is stripped from
    /// the MID (spec §2.3 [FBBRoutines.c:708]). CRLF is canonical but bare
    /// CR/LF line endings are tolerated (spec §3.13.2). When a <c>Body:</c>
    /// count is present it (and any <c>File:</c> counts) delimit the body and
    /// attachments exactly; otherwise the whole remainder after the blank
    /// line is the body.
    /// </summary>
    /// <exception cref="FbbProtocolException">
    /// <c>Mid:</c> is absent or not first, the body is empty, or a declared
    /// <c>Body:</c>/<c>File:</c> count runs past the end of the object.
    /// </exception>
    public static B2Message Decode(ReadOnlySpan<byte> obj)
    {
        var (headerLines, bodyStart) = SplitHeader(obj);
        if (headerLines.Count == 0)
        {
            throw new FbbProtocolException("B2 message has no header (spec §3.9).");
        }

        string? mid = null;
        var to = new List<string>();
        var cc = new List<string>();
        string? date = null;
        string? from = null;
        string? subject = null;
        string? mbo = null;
        var type = B2MessageType.Private;
        int? bodyCount = null;
        var fileHeaders = new List<(int Count, string Name)>();

        for (var i = 0; i < headerLines.Count; i++)
        {
            var (name, value) = SplitField(headerLines[i]);
            var lower = name.ToLowerInvariant();

            // "Mid: MUST be the first line" (spec §3.9).
            if (i == 0 && lower != "mid")
            {
                throw new FbbProtocolException($"B2 message first header line must be Mid:, got \"{name}:\" (spec §3.9).");
            }

            switch (lower)
            {
                case "mid":
                    mid = StripMpsR(value);
                    break;
                case "to":
                    to.Add(value);
                    break;
                case "cc":
                    cc.Add(value);
                    break;
                case "date":
                    date = value;
                    break;
                case "from":
                    from = value;
                    break;
                case "subject":
                    subject = value;
                    break;
                case "mbo":
                    mbo = value;
                    break;
                case "type":
                    type = ParseType(value);
                    break;
                case "body":
                    bodyCount = ParseCount(value, "Body");
                    break;
                case "file":
                    fileHeaders.Add(ParseFileHeader(value));
                    break;
                default:
                    break; // unknown / Content-* fields ignored (spec §3.9)
            }
        }

        if (mid is null)
        {
            throw new FbbProtocolException("B2 message is missing the mandatory Mid: header (spec §3.9).");
        }

        var (body, files) = ExtractPayload(obj, bodyStart, bodyCount, fileHeaders);
        if (body.Length == 0)
        {
            throw new FbbProtocolException("B2 message body may not be empty (spec §3.9).");
        }

        return new B2Message
        {
            Mid = mid,
            Type = type,
            To = to,
            Cc = cc,
            Date = date,
            From = from,
            Subject = subject,
            Mbo = mbo,
            Body = body,
            Files = files,
        };
    }

    private static (byte[] Body, List<B2Attachment> Files) ExtractPayload(
        ReadOnlySpan<byte> obj,
        int start,
        int? bodyCount,
        List<(int Count, string Name)> fileHeaders)
    {
        var payload = obj[start..];

        // No Body: count → the whole remainder after the blank line is the body
        // (tolerant minimal form); attachments require an explicit Body: count.
        if (bodyCount is not { } count)
        {
            return (payload.ToArray(), []);
        }

        var pos = 0;
        var body = ReadCounted(payload, ref pos, count, "Body");
        var files = new List<B2Attachment>(fileHeaders.Count);
        foreach (var (fileCount, name) in fileHeaders)
        {
            files.Add(new B2Attachment(name, ReadCounted(payload, ref pos, fileCount, $"File {name}")));
        }

        return (body, files);
    }

    private static byte[] ReadCounted(ReadOnlySpan<byte> payload, ref int pos, int count, string what)
    {
        if (pos + count > payload.Length)
        {
            throw new FbbProtocolException(
                $"B2 {what} declares {count} bytes but only {payload.Length - pos} remain (spec §3.9).");
        }

        var slice = payload.Slice(pos, count).ToArray();
        pos += count;

        // The terminating CRLF is mandatory and additional — skip it if present
        // (tolerate a bare CR or LF, or its absence at the very end).
        if (pos < payload.Length && payload[pos] == '\r')
        {
            pos++;
        }

        if (pos < payload.Length && payload[pos] == '\n')
        {
            pos++;
        }

        return slice;
    }

    private static (List<string> Lines, int BodyStart) SplitHeader(ReadOnlySpan<byte> obj)
    {
        var lines = new List<string>();
        var i = 0;
        while (i < obj.Length)
        {
            var lineStart = i;
            while (i < obj.Length && obj[i] != '\r' && obj[i] != '\n')
            {
                i++;
            }

            // Line boundaries are found in the byte domain (CR/LF are single
            // ASCII bytes that cannot occur inside a UTF-8 multibyte sequence),
            // so decoding each header line as UTF-8-when-valid else Latin-1 keeps
            // offsets correct while preserving non-ASCII values such as a UTF-8
            // Subject (PacketText). Field names + the colon/space are ASCII.
            var line = PacketText.DecodeHeader(obj.Slice(lineStart, i - lineStart));

            // Consume the line terminator (CRLF, or a bare CR/LF — spec §3.13.2).
            if (i < obj.Length && obj[i] == '\r')
            {
                i++;
            }

            if (i < obj.Length && obj[i] == '\n')
            {
                i++;
            }

            if (line.Length == 0)
            {
                return (lines, i); // blank line: header ends, body begins
            }

            lines.Add(line);
        }

        return (lines, obj.Length); // no blank line found (degenerate)
    }

    private static (string Name, string Value) SplitField(string line)
    {
        var colon = line.IndexOf(':', StringComparison.Ordinal);
        if (colon < 0)
        {
            throw new FbbProtocolException($"B2 header line has no colon: \"{line}\" (spec §3.9).");
        }

        var name = line[..colon].Trim();
        // One optional space after the colon is the convention; trim leading
        // whitespace from the value but preserve its internal/trailing case.
        var value = line[(colon + 1)..].TrimStart(' ');
        return (name, value);
    }

    private static (int Count, string Name) ParseFileHeader(string value)
    {
        var space = value.IndexOf(' ', StringComparison.Ordinal);
        if (space < 0)
        {
            throw new FbbProtocolException($"B2 File: header must be \"<count> <name>\", got \"{value}\" (spec §3.9).");
        }

        var count = ParseCount(value[..space], "File");
        return (count, value[(space + 1)..]);
    }

    private static int ParseCount(string value, string what)
    {
        return int.TryParse(value.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var n)
            ? n
            : throw new FbbProtocolException($"B2 {what}: count is not a non-negative integer: \"{value}\" (spec §3.9).");
    }

    private static B2MessageType ParseType(string value) => value.Trim().ToLowerInvariant() switch
    {
        "private" => B2MessageType.Private,
        "bulletin" => B2MessageType.Bulletin,
        "service" => B2MessageType.Service,
        "inquiry" => B2MessageType.Inquiry,
        "position report" => B2MessageType.PositionReport,
        "position request" => B2MessageType.PositionRequest,
        "option" => B2MessageType.Option,
        "system" => B2MessageType.System,
        _ => B2MessageType.Private, // unknown/legacy spellings: store as Private (BPQ stores all as P)
    };

    private static string TypeWord(B2MessageType type) => type switch
    {
        B2MessageType.Private => "Private",
        B2MessageType.Bulletin => "Bulletin",
        B2MessageType.Service => "Service",
        B2MessageType.Inquiry => "Inquiry",
        B2MessageType.PositionReport => "Position Report",
        B2MessageType.PositionRequest => "Position Request",
        B2MessageType.Option => "Option",
        B2MessageType.System => "System",
        _ => "Private",
    };

    private static string StripMpsR(string mid)
    {
        // "B2F MIDs may arrive with @MPS@R suffixes — strip them"
        // [BPQ-SRC FBBRoutines.c:708, spec §2.3].
        const string suffix = "@MPS@R";
        var trimmed = mid.Trim();
        return trimmed.EndsWith(suffix, StringComparison.OrdinalIgnoreCase)
            ? trimmed[..^suffix.Length]
            : trimmed;
    }

    private static void AppendField(StringBuilder sb, string name, string value) =>
        sb.Append(name).Append(": ").Append(value).Append("\r\n");

    private static void AppendOptional(StringBuilder sb, string name, string? value)
    {
        if (!string.IsNullOrEmpty(value))
        {
            AppendField(sb, name, value);
        }
    }
}
