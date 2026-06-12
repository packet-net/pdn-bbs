using System.Globalization;

namespace Bbs.Fbb;

/// <summary>
/// One line of an FBB proposal block — spec §3.3. Concrete shapes:
/// <see cref="FaProposal"/> (the 7-field <c>FA</c>/<c>FB</c> form) and
/// <see cref="FcProposal"/> (the B2F <c>FC</c> form). FA/FB/FC may be
/// intermixed in one block [WL-B2F].
/// </summary>
public abstract record Proposal
{
    /// <summary>Renders the proposal as its wire line (no CR/LF terminator).</summary>
    public abstract string ToWireLine();

    /// <summary>
    /// Parses a proposal line, dispatching on the verb.
    /// </summary>
    /// <exception cref="FbbProtocolException">
    /// The line is not an FA/FB/FC proposal or violates a per-session limit
    /// ("If a field is missing upon receipt, an error message will be sent
    /// immediately followed by a disconnection" [FBB-PROTO, spec §3.3]).
    /// </exception>
    public static Proposal Parse(string line)
    {
        ArgumentNullException.ThrowIfNull(line);
        var t = line.TrimEnd();
        if (t.StartsWith("FA ", StringComparison.OrdinalIgnoreCase)
            || t.StartsWith("FB ", StringComparison.OrdinalIgnoreCase))
        {
            return FaProposal.ParseLine(t);
        }

        return t.StartsWith("FC ", StringComparison.OrdinalIgnoreCase)
            ? FcProposal.ParseLine(t)
            : throw new FbbProtocolException($"Not a proposal line: \"{t}\"");
    }
}

/// <summary>
/// The 7-field FA/FB proposal — spec §3.3:
/// <c>FA &lt;type&gt; &lt;from&gt; &lt;atbbs&gt; &lt;to&gt; &lt;bid&gt; &lt;size&gt;</c>.
/// <c>FA</c> = compressed-mode message, <c>FB</c> = ASCII mode; LinBPQ emits
/// FA when compression was negotiated [BPQ-SRC FBBRoutines.c:1040].
/// </summary>
/// <param name="Verb">'A' (compressed) or 'B' (ASCII) — the letter after <c>F</c>.</param>
/// <param name="MessageType">Message type: <c>P</c>, <c>B</c> or <c>T</c> (spec §2.1).</param>
/// <param name="From">Originating callsign, ≤6 chars SSID-stripped on emit (spec §3.3).</param>
/// <param name="AtBbs">The @BBS routing field, ≤40 chars (spec §2.4).</param>
/// <param name="To">Destination callsign, ≤6 chars on emit (spec §3.3).</param>
/// <param name="Bid">Message BID/MID, ≤12 chars on emit (spec §2.3).</param>
/// <param name="Size">
/// Advisory UNCOMPRESSED size in bytes, including the R: line the sender
/// will prepend [BPQ-SRC BBSUtilities.c:6886, spec §3.3].
/// </param>
public sealed record FaProposal(
    char Verb,
    char MessageType,
    string From,
    string AtBbs,
    string To,
    string Bid,
    int Size) : Proposal
{
    /// <summary>
    /// Whether a conforming receiver answers this proposal with a polite
    /// <c>-</c> rather than a protocol error: "A TO &gt;6 chars in a received
    /// FA gets a polite <c>-</c>" [BPQ-SRC, spec §3.3]. This is the
    /// per-proposal rejection class, as opposed to the per-session fatal
    /// missing-field case.
    /// </summary>
    public bool RequiresPoliteReject => To.Length > 6;

    /// <inheritdoc/>
    /// <exception cref="FbbProtocolException">A field violates the emit limits of spec §3.3/§2.3/§2.4.</exception>
    public override string ToWireLine()
    {
        ValidateField(From, 6, nameof(From));
        ValidateField(To, 6, nameof(To));
        ValidateField(AtBbs, 40, nameof(AtBbs));
        ValidateField(Bid, 12, nameof(Bid));
        if (Verb is not ('A' or 'B'))
        {
            throw new FbbProtocolException($"Proposal verb must be 'A' or 'B', not '{Verb}'.");
        }

        if (MessageType is not ('P' or 'B' or 'T'))
        {
            throw new FbbProtocolException($"Proposal type must be P, B or T, not '{MessageType}' (spec §2.1).");
        }

        return Size < 0
            ? throw new FbbProtocolException("Proposal size must be non-negative.")
            : string.Create(
                CultureInfo.InvariantCulture,
                $"F{Verb} {MessageType} {From} {AtBbs} {To} {Bid} {Size}");
    }

    /// <summary>
    /// Normalises a callsign for proposal emission: SSID stripped, truncated
    /// to 6, upper-cased ("From/To in proposals are ≤6 chars, SSID stripped"
    /// — spec §3.3).
    /// </summary>
    public static string NormalizeCallsign(string callsign)
    {
        ArgumentNullException.ThrowIfNull(callsign);
        var c = callsign.Trim().ToUpperInvariant();
        var dash = c.IndexOf('-', StringComparison.Ordinal);
        if (dash >= 0)
        {
            c = c[..dash];
        }

        return c.Length > 6 ? c[..6] : c;
    }

    internal static FaProposal ParseLine(string trimmedLine)
    {
        var fields = trimmedLine.Split(' ', StringSplitOptions.RemoveEmptyEntries);

        // "ALL the fields are necessary … must hold seven fields" [FBB-PROTO];
        // extras beyond seven are tolerated and ignored.
        if (fields.Length < 7)
        {
            throw new FbbProtocolException(
                $"FA/FB proposal has {fields.Length} fields; 7 are required (spec §3.3).");
        }

        if (fields[1].Length != 1)
        {
            throw new FbbProtocolException($"Proposal type field must be one character, got \"{fields[1]}\".");
        }

        if (!int.TryParse(fields[6], NumberStyles.None, CultureInfo.InvariantCulture, out var size))
        {
            throw new FbbProtocolException($"Proposal size field is not a non-negative integer: \"{fields[6]}\".");
        }

        return new FaProposal(
            char.ToUpperInvariant(fields[0][1]),
            char.ToUpperInvariant(fields[1][0]),
            fields[2],
            fields[3],
            fields[4],
            fields[5],
            size);
    }

    private static void ValidateField(string value, int maxLength, string name)
    {
        if (string.IsNullOrEmpty(value) || value.Length > maxLength || value.Contains(' ', StringComparison.Ordinal))
        {
            throw new FbbProtocolException(
                $"Proposal field {name} must be 1-{maxLength} chars without spaces, got \"{value}\".");
        }
    }
}

/// <summary>The <c>FC</c> control-type field — spec §3.9: "Inbound FC type field must be 2 chars (EM/CM)".</summary>
public enum FcType
{
    /// <summary><c>EM</c> — encapsulated message (the B2 object follows). The only type we generate.</summary>
    Em = 0,

    /// <summary><c>CM</c> — control message. Recognised and parsed; never generated by us (spec §3.9).</summary>
    Cm = 1,
}

/// <summary>
/// The B2F <c>FC</c> proposal — spec §3.3:
/// <c>FC EM &lt;mid≤12&gt; &lt;usize&gt; &lt;csize&gt; 0</c>. The trailing
/// <c>0</c> is emitted by real implementations and omitted by the F4HOF ABNF
/// — both are accepted on parse and we always emit it (matching LinBPQ's
/// <c>"FC EM %s %d %d %d\r"</c>). To a BPQ partner LinBPQ appends
/// <c>from at to type</c> extension fields after the <c>0</c>
/// [BPQ-SRC FBBRoutines.c:987]; these are parse-tolerantly preserved in
/// <see cref="BpqExtensionFields"/> and only re-emitted when that list is
/// populated (default: empty → standard FC; there is no from/to in the
/// standard FC — they live in the B2 header).
/// </summary>
/// <param name="Type">Control type: <see cref="FcType.Em"/> or <see cref="FcType.Cm"/> (spec §3.9).</param>
/// <param name="Mid">Message ID, ≤12 chars on emit (spec §2.3).</param>
/// <param name="UncompressedSize">Uncompressed B2 object size in bytes.</param>
/// <param name="CompressedSize">Compressed object size in bytes.</param>
public sealed record FcProposal(
    FcType Type,
    string Mid,
    int UncompressedSize,
    int CompressedSize) : Proposal
{
    /// <summary>
    /// The BPQ-only trailing extension fields (<c>from at to type</c>)
    /// [BPQ-SRC FBBRoutines.c:987], preserved verbatim on parse. Empty for a
    /// standard FC; populate it to emit the BPQ-partner form (the build
    /// option, default OFF). We never advertise the <c>BPQ</c> SID substring
    /// that would make a partner expect them (spec §3.13.3).
    /// </summary>
    public IReadOnlyList<string> BpqExtensionFields { get; init; } = [];

    /// <summary>The wire spelling of <see cref="Type"/> (<c>EM</c>/<c>CM</c>).</summary>
    private string TypeToken => Type == FcType.Cm ? "CM" : "EM";

    /// <inheritdoc/>
    /// <exception cref="FbbProtocolException">A field violates the emit limits of spec §3.3.</exception>
    public override string ToWireLine()
    {
        if (string.IsNullOrEmpty(Mid) || Mid.Length > 12 || Mid.Contains(' ', StringComparison.Ordinal))
        {
            throw new FbbProtocolException($"FC MID must be 1-12 chars without spaces, got \"{Mid}\".");
        }

        if (UncompressedSize < 0 || CompressedSize < 0)
        {
            throw new FbbProtocolException("FC sizes must be non-negative.");
        }

        var line = string.Create(
            CultureInfo.InvariantCulture,
            $"FC {TypeToken} {Mid} {UncompressedSize} {CompressedSize} 0");
        return BpqExtensionFields.Count == 0 ? line : $"{line} {string.Join(' ', BpqExtensionFields)}";
    }

    internal static FcProposal ParseLine(string trimmedLine)
    {
        var fields = trimmedLine.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (fields.Length < 5)
        {
            throw new FbbProtocolException(
                $"FC proposal has {fields.Length} fields; at least FC, type, MID and two sizes are required (spec §3.3).");
        }

        var type = fields[1].ToUpperInvariant() switch
        {
            "EM" => FcType.Em,
            "CM" => FcType.Cm,
            _ => throw new FbbProtocolException(
                $"FC type field must be EM or CM, got \"{fields[1]}\" (spec §3.9)."),
        };

        if (!int.TryParse(fields[3], NumberStyles.None, CultureInfo.InvariantCulture, out var usize)
            || !int.TryParse(fields[4], NumberStyles.None, CultureInfo.InvariantCulture, out var csize))
        {
            throw new FbbProtocolException("FC size fields must be non-negative integers.");
        }

        // Field 5 is the trailing 0 (when present); anything beyond it is the
        // BPQ "from at to type" extension, preserved verbatim (spec §3.13.3).
        var extension = fields.Length > 6 ? fields[6..] : [];
        return new FcProposal(type, fields[2], usize, csize) { BpqExtensionFields = extension };
    }
}

/// <summary>
/// Helpers for the proposal-block terminator <c>F&gt;</c> and its checksum —
/// spec §3.3: "sum every byte of every proposal line including each
/// terminating CR into an 8-bit accumulator; transmit its two's complement"
/// [FBB-APP9; BPQ-SRC FBBRoutines.c:796,1049]. LinBPQ always sends the
/// checksum; on receive it is optional (JNOS sends a bare <c>F&gt;</c> for
/// FA) and validated only when present.
/// </summary>
public static class ProposalBlock
{
    /// <summary>Hard cap of proposals per block — spec §3.3 / §3.13.5 ("never send more than 5").</summary>
    public const int MaxProposalsPerBlock = 5;

    /// <summary>
    /// Default cap on accumulated proposal sizes per block (BPQ
    /// <c>MaxFBBBlock</c> default, raw bytes — spec §4.1, [VERIFY-ORACLE #16]).
    /// </summary>
    public const int DefaultMaxBlockBytes = 10000;

    /// <summary>
    /// Computes the <c>F&gt;</c> checksum over proposal lines given without
    /// terminators: each line's bytes plus one CR are summed (LF is never
    /// included — [M0LTE-IT fbb_partner.py]) and the 8-bit two's complement
    /// is returned.
    /// </summary>
    public static byte ComputeChecksum(IEnumerable<string> proposalLines)
    {
        ArgumentNullException.ThrowIfNull(proposalLines);
        var sum = 0;
        foreach (var line in proposalLines)
        {
            foreach (var ch in line)
            {
                sum += ch & 0xFF;
            }

            sum += '\r';
        }

        return (byte)(-sum);
    }

    /// <summary>Builds the terminator line <c>F&gt; XX</c> (uppercase 2-digit hex) — spec §3.3.</summary>
    public static string BuildTerminator(byte checksum) =>
        string.Create(CultureInfo.InvariantCulture, $"F> {checksum:X2}");

    /// <summary>
    /// Recognises a proposal-block terminator. Accepts the bare <c>F&gt;</c>
    /// (checksum <see langword="null"/>; "HH is optional" [FBB-APP9]) and a
    /// hex checksum of any width (taken modulo 256). Returns
    /// <see langword="false"/> for a line that starts <c>F&gt;</c> but whose
    /// trailer is not valid hex.
    /// </summary>
    public static bool TryParseTerminator(string line, out byte? checksum)
    {
        ArgumentNullException.ThrowIfNull(line);
        checksum = null;
        var t = line.TrimEnd();
        if (!t.StartsWith("F>", StringComparison.Ordinal))
        {
            return false;
        }

        var rest = t[2..].Trim();
        if (rest.Length == 0)
        {
            return true; // bare form (JNOS) — accept without verification
        }

        if (!long.TryParse(rest, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var value))
        {
            return false;
        }

        checksum = (byte)(value & 0xFF);
        return true;
    }
}
