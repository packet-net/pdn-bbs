using System.Globalization;
using System.Text;

namespace Bbs.Fbb;

/// <summary>Semantic disposition of one FS answer — spec §3.4 table.</summary>
public enum FsAnswerKind
{
    /// <summary>
    /// Send the message (<c>+</c>/<c>Y</c>; with a non-zero offset,
    /// <c>!n</c>/<c>An</c> — resume from byte <c>n</c> of the COMPRESSED
    /// image, spec §3.4/§3.8).
    /// </summary>
    Accept = 0,

    /// <summary>
    /// Receiver already has it (<c>-</c>/<c>N</c>): the proposer marks the
    /// message forwarded-elsewhere (P → status F) — spec §3.4.
    /// </summary>
    AlreadyHave,

    /// <summary>
    /// Try again later (<c>=</c>/<c>L</c>, and <c>H</c> which from a
    /// proposal-sender's point of view means defer — "RMS Express sends H
    /// for Defer" [BPQ-SRC comment, spec §3.4]).
    /// </summary>
    Defer,

    /// <summary>
    /// Rejected (<c>R</c>): the message is NOT marked forwarded and may
    /// still be routed to another BBS [FBB-APP9, spec §3.4]. Parse-only —
    /// never emitted (spec §8 LATER).
    /// </summary>
    Reject,

    /// <summary>
    /// Error in the proposal line (<c>E</c>) — spec §3.4. Parse-only —
    /// never emitted (spec §8 LATER).
    /// </summary>
    ProposalError,
}

/// <summary>One per-proposal answer inside an <c>FS</c> line — spec §3.4.</summary>
/// <param name="Kind">The semantic disposition.</param>
/// <param name="Offset">
/// For <see cref="FsAnswerKind.Accept"/>: the resume offset in bytes into
/// the compressed image (0 = send from the start). Always 0 for other kinds.
/// </param>
public sealed record FsAnswer(FsAnswerKind Kind, int Offset = 0)
{
    /// <summary>Plain accept (<c>+</c>).</summary>
    public static FsAnswer Accept { get; } = new(FsAnswerKind.Accept);

    /// <summary>Already-have reject (<c>-</c>).</summary>
    public static FsAnswer AlreadyHave { get; } = new(FsAnswerKind.AlreadyHave);

    /// <summary>Defer (<c>=</c>).</summary>
    public static FsAnswer Defer { get; } = new(FsAnswerKind.Defer);

    /// <summary>Accept resuming from <paramref name="offset"/> bytes into the compressed image (<c>!n</c>).</summary>
    public static FsAnswer AcceptFromOffset(int offset) =>
        offset >= 0
            ? new FsAnswer(FsAnswerKind.Accept, offset)
            : throw new ArgumentOutOfRangeException(nameof(offset), "Resume offset must be non-negative.");
}

/// <summary>
/// Emits and parses the <c>FS</c> proposal-response line — spec §3.4: one
/// character (or <c>!</c>/<c>A</c> + digits group) per proposal, in order;
/// "FS line MUST have as many +,-,=,R,E,H signs as lines in the proposal"
/// [FBB-APP9].
/// </summary>
public static class FsResponse
{
    /// <summary>
    /// The exact error line for an unacceptable FS, including LinBPQ's
    /// trailing quote (sic) — spec §3.12 [BPQ-SRC].
    /// </summary>
    public const string InvalidResponseErrorLine = "*** Protocol Error - Invalid Proposal Response'";

    /// <summary>
    /// Renders an <c>FS</c> line. Only <c>+</c>, <c>-</c>, <c>=</c> and
    /// <c>!offset</c> are ever emitted; H/R/E emission is deliberately
    /// unsupported (spec §8 LATER: "LinBPQ never emits them").
    /// </summary>
    /// <exception cref="ArgumentException">
    /// An answer has a parse-only kind (<see cref="FsAnswerKind.Reject"/> or
    /// <see cref="FsAnswerKind.ProposalError"/>), or the list is empty.
    /// </exception>
    public static string Emit(IReadOnlyList<FsAnswer> answers)
    {
        ArgumentNullException.ThrowIfNull(answers);
        if (answers.Count == 0)
        {
            throw new ArgumentException("An FS line must answer at least one proposal.", nameof(answers));
        }

        var sb = new StringBuilder("FS ");
        foreach (var answer in answers)
        {
            switch (answer.Kind)
            {
                case FsAnswerKind.Accept when answer.Offset == 0:
                    sb.Append('+');
                    break;
                case FsAnswerKind.Accept:
                    sb.Append('!').Append(answer.Offset.ToString(CultureInfo.InvariantCulture));
                    break;
                case FsAnswerKind.AlreadyHave:
                    sb.Append('-');
                    break;
                case FsAnswerKind.Defer:
                    sb.Append('=');
                    break;
                case FsAnswerKind.Reject:
                case FsAnswerKind.ProposalError:
                default:
                    throw new ArgumentException(
                        $"FS answer kind {answer.Kind} is parse-only and never emitted (spec §8).",
                        nameof(answers));
            }
        }

        return sb.ToString();
    }

    /// <summary>
    /// Parses an <c>FS</c> line accepting the full alphabet
    /// <c>+ Y - N R = L H E !n An</c>, case-insensitively; digits after
    /// <c>!</c>/<c>A</c> are a decimal offset into the COMPRESSED image
    /// (spec §3.4).
    /// </summary>
    /// <exception cref="FbbProtocolException">
    /// The line is not an FS line or contains an invalid answer character —
    /// the wire error is <see cref="InvalidResponseErrorLine"/>.
    /// </exception>
    public static IReadOnlyList<FsAnswer> Parse(string line)
    {
        ArgumentNullException.ThrowIfNull(line);
        var t = line.TrimEnd();
        if (t.Length < 2 || !t.StartsWith("FS", StringComparison.OrdinalIgnoreCase))
        {
            throw new FbbProtocolException($"Not an FS line: \"{t}\"", InvalidResponseErrorLine);
        }

        var answers = new List<FsAnswer>();
        var i = 2;
        while (i < t.Length)
        {
            var c = t[i];
            if (char.IsWhiteSpace(c))
            {
                i++;
                continue;
            }

            switch (char.ToUpperInvariant(c))
            {
                case '+' or 'Y':
                    answers.Add(FsAnswer.Accept);
                    i++;
                    break;
                case '-' or 'N':
                    answers.Add(FsAnswer.AlreadyHave);
                    i++;
                    break;
                case '=' or 'L' or 'H':
                    answers.Add(FsAnswer.Defer);
                    i++;
                    break;
                case 'R':
                    answers.Add(new FsAnswer(FsAnswerKind.Reject));
                    i++;
                    break;
                case 'E':
                    answers.Add(new FsAnswer(FsAnswerKind.ProposalError));
                    i++;
                    break;
                case '!' or 'A':
                    var start = ++i;
                    while (i < t.Length && char.IsAsciiDigit(t[i]))
                    {
                        i++;
                    }

                    if (i == start
                        || !int.TryParse(t[start..i], NumberStyles.None, CultureInfo.InvariantCulture, out var offset))
                    {
                        throw new FbbProtocolException(
                            $"FS '{c}' must be followed by a decimal offset (spec §3.4).",
                            InvalidResponseErrorLine);
                    }

                    answers.Add(FsAnswer.AcceptFromOffset(offset));
                    break;
                default:
                    throw new FbbProtocolException(
                        $"Invalid FS answer character '{c}' (spec §3.4).",
                        InvalidResponseErrorLine);
            }
        }

        return answers;
    }

    /// <summary>
    /// Parses an <c>FS</c> line and enforces the answers-must-match-proposal-count
    /// rule [FBB-APP9, spec §3.4].
    /// </summary>
    /// <exception cref="FbbProtocolException">
    /// Parse failure, or the answer count differs from
    /// <paramref name="expectedCount"/>.
    /// </exception>
    public static IReadOnlyList<FsAnswer> Parse(string line, int expectedCount)
    {
        var answers = Parse(line);
        return answers.Count != expectedCount
            ? throw new FbbProtocolException(
                $"FS line answers {answers.Count} proposals but {expectedCount} were proposed (spec §3.4).",
                InvalidResponseErrorLine)
            : answers;
    }
}
