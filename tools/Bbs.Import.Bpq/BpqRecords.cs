namespace Bbs.Import.Bpq;

/// <summary>
/// One decoded <c>struct MsgInfo</c> / <c>struct OldMsgInfo</c> record from <c>DIRMES.SYS</c>
/// (bpqmail.h:615 / :586). Field offsets are verified byte-for-byte against the LinBPQ source
/// at <c>/home/tf/src/linbpq/bpqmail.h</c> (see <see cref="DirmesReader"/> for the layout) and
/// against the real on-disk fixtures (docker/oracle/state, gb7rdg-config/bpq).
///
/// <para>
/// IMPORTANT: <see cref="Type"/> and <see cref="Status"/> are stored on disk as ASCII characters
/// (e.g. 'B'/'P'/'T' and 'N'/'Y'/'F'/'K'/'H'/'D'/'$'), NOT as the numeric <c>MSGTYPE_*</c>/
/// <c>MSGSTATUS_*</c> enum values that appear in bpqmail.h. The struct fields are declared
/// <c>char type; char status;</c>; the enums are an internal convenience only. The fixtures
/// confirm this (record 1 of docker/oracle/state/DIRMES.SYS begins <c>50 4B</c> = 'P','K').
/// </para>
/// </summary>
internal sealed record BpqMessageHeader
{
    /// <summary>Message type character ('B' bulletin, 'P' personal, 'T'/'N' traffic). 0 = empty slot.</summary>
    public required char Type { get; init; }

    /// <summary>Status character (N Y F K H D $). 0 for empty slots.</summary>
    public required char Status { get; init; }

    /// <summary>BPQ message number (DIRMES.SYS offset 2, int32 LE). Load-bearing: feeds the BID suffix.</summary>
    public required int Number { get; init; }

    /// <summary>Stored message length in bytes (offset 6, int32 LE). Advisory only.</summary>
    public required int Length { get; init; }

    /// <summary>BBS we received it from (offset 14, char[7], NUL-terminated).</summary>
    public required string BbsFrom { get; init; }

    /// <summary>The @BBS / hierarchical-address (AT) field (offset 21, char[41]).</summary>
    public required string Via { get; init; }

    /// <summary>FROM callsign (offset 62, char[7]).</summary>
    public required string From { get; init; }

    /// <summary>TO callsign / bulletin category (offset 69, char[7]).</summary>
    public required string To { get; init; }

    /// <summary>BID/MID (offset 76, char[13] = up to 12 chars + NUL). Preserved verbatim — Rule 1.</summary>
    public required string Bid { get; init; }

    /// <summary>Subject / title (offset 89, char[61]).</summary>
    public required string Title { get; init; }

    /// <summary>B2Flags byte (new layout offset 154). Bit 0 (<c>B2Msg</c>) marks a B2-formatted body file.</summary>
    public required byte B2Flags { get; init; }

    /// <summary>
    /// The "still to forward" bitmap (new layout offset 163, 20 bytes; legacy offset 182, 10 bytes).
    /// Bit n (1-based BBSNumber) → byte (n-1)/8, mask 1&lt;&lt;((n-1)%8) — verified against
    /// <c>check_fwd_bit</c> (BBSUtilities.c:2407). A set bit = a partner this message is still queued to.
    /// </summary>
    public required byte[] Fbbs { get; init; }

    /// <summary>The "already forwarded" bitmap (new layout offset 183, 20 bytes; legacy offset 192, 10 bytes).</summary>
    public required byte[] Forw { get; init; }

    /// <summary>Date received, Unix epoch seconds (new layout int64 LE offset 247; legacy int32 LE offset 10).</summary>
    public required long DateReceived { get; init; }

    /// <summary>Date created, Unix epoch seconds (new layout int64 LE offset 255; legacy int64 LE offset 166).</summary>
    public required long DateCreated { get; init; }

    /// <summary>Date changed, Unix epoch seconds (new layout int64 LE offset 263; legacy int64 LE offset 174).</summary>
    public required long DateChanged { get; init; }

    /// <summary>Whether the body file is a formatted B2 message (B2Flags bit 0).</summary>
    public bool IsB2Message => (B2Flags & 0x01) != 0;

    /// <summary>The 1-based BBSNumbers whose "still to forward" (fbbs) bit is set.</summary>
    public IReadOnlyList<int> StillToForwardBbsNumbers => DecodeBits(Fbbs);

    /// <summary>The 1-based BBSNumbers whose "already forwarded" (forw) bit is set.</summary>
    public IReadOnlyList<int> AlreadyForwardedBbsNumbers => DecodeBits(Forw);

    private static List<int> DecodeBits(byte[] mask)
    {
        var result = new List<int>();
        int totalBits = mask.Length * 8;
        for (int n = 1; n <= totalBits; n++)
        {
            if ((mask[(n - 1) / 8] & (1 << ((n - 1) % 8))) != 0)
            {
                result.Add(n);
            }
        }

        return result;
    }
}

/// <summary>
/// One decoded <c>BIDRec</c> from <c>WFBID.SYS</c> (bpqmail.h:684) — the BID dedup database
/// (~60-day lifetime). The record size on disk is build-dependent: 18 bytes on a 32-bit LinBPQ
/// (pointer-sized union = 4) and 22 bytes on a 64-bit LinBPQ (pointer-sized union = 8); the
/// reader auto-detects which (gb7rdg = 18, docker/oracle = 22). See <see cref="WfbidReader"/>.
/// </summary>
internal sealed record BpqBidRecord
{
    /// <summary>Mode character (offset 0) — the message type ('B'/'P'/'T') that registered the BID.</summary>
    public required char Mode { get; init; }

    /// <summary>The BID string (offset 1, char[13]). Preserved verbatim — Rule 1.</summary>
    public required string Bid { get; init; }

    /// <summary>Low word of the message number that registered the BID (union offset 14, u16). Hint only — can wrap.</summary>
    public required ushort MsgNo { get; init; }

    /// <summary>Days since the Unix epoch when the BID was last seen (union offset 16, u16).</summary>
    public required ushort TimestampDays { get; init; }
}

/// <summary>
/// A BPQMail forwarding partner parsed from <c>linmail.cfg</c> group <c>BBSForwarding.&lt;CALL&gt;</c>.
/// Only the fields the importer maps into the pdn <c>partners</c> table are retained; unknown
/// libconfig keys are ignored.
/// </summary>
internal sealed record BpqPartner
{
    /// <summary>The partner callsign (the sub-group key, with any leading '*' digit-prefix stripped).</summary>
    public required string Call { get; init; }

    /// <summary>ConnectScript, pipe-joined on disk; stored here with the pipes already split into lines.</summary>
    public required IReadOnlyList<string> ConnectScript { get; init; }

    /// <summary>TOCalls distribution list (pipe-separated on disk).</summary>
    public required IReadOnlyList<string> ToCalls { get; init; }

    /// <summary>ATCalls list (pipe-separated on disk).</summary>
    public required IReadOnlyList<string> AtCalls { get; init; }

    /// <summary>HRoutes (flood bulletins) list.</summary>
    public required IReadOnlyList<string> HRoutes { get; init; }

    /// <summary>HRoutesP (personals + directed bulletins) list.</summary>
    public required IReadOnlyList<string> HRoutesP { get; init; }

    /// <summary>The partner's own hierarchical address (BBSHA), or null.</summary>
    public required string? BbsHa { get; init; }

    /// <summary>Whether auto-forwarding is enabled.</summary>
    public required bool Enabled { get; init; }

    /// <summary>Poll interval in seconds.</summary>
    public required int FwdInterval { get; init; }

    /// <summary>The vestigial "send new immediately" flag (kept for fidelity; see compat spec §4.1).</summary>
    public required bool FwdNewImmediately { get; init; }

    /// <summary>Whether B2F is allowed for this partner (UseB2Protocol).</summary>
    public required bool UseB2 { get; init; }

    /// <summary>Connection timeout in seconds.</summary>
    public required int ConTimeout { get; init; }
}

/// <summary>
/// A BPQMail user parsed from <c>linmail.cfg</c> group <c>BBSUsers</c>. The value is a
/// caret('^')-delimited field list; field ordering is verified against
/// <c>GetUserDatabase</c> (BBSUtilities.c:687–740):
/// [0]=Name [1]=Address [2]=HomeBBS [3]=QRA [4]=pass [5]=ZIP [6]=CMSPass [7]=lastmsg
/// [8]=flags [9]=PageLen [10]=BBSNumber [11]=RMSSSIDBits [12]=WebSeqNo [13]=TimeLastConnected …
/// </summary>
internal sealed record BpqUser
{
    /// <summary>User callsign (the record key, leading '*' digit-prefix stripped).</summary>
    public required string Call { get; init; }

    /// <summary>Display name (field 0).</summary>
    public required string Name { get; init; }

    /// <summary>Home BBS (field 2).</summary>
    public required string HomeBbs { get; init; }

    /// <summary>QRA / Maidenhead locator (field 3) — surfaced as QTH for white pages.</summary>
    public required string Qra { get; init; }

    /// <summary>ZIP / postcode (field 5).</summary>
    public required string Zip { get; init; }

    /// <summary>The "last listed" message-number pointer (field 7, BPQ <c>lastmsg</c>): the highest
    /// message number this user has already been shown, so an interactive "list new" only surfaces
    /// numbers above it. Mapped to pdn's <c>users.last_listed_number</c> so migrated users don't see
    /// the whole back-catalogue as new on first connect. 0 = never listed.</summary>
    public required long LastListed { get; init; }

    /// <summary>Flags bitfield (field 8). 0x10 = F_BBS marks a forwarding partner.</summary>
    public required int Flags { get; init; }

    /// <summary>BBSNumber (field 10) — the 1-based bit index into the fbbs/forw bitmaps. 0 = unassigned.</summary>
    public required int BbsNumber { get; init; }

    /// <summary>Last login time (field 13, Unix epoch seconds), or 0.</summary>
    public required long TimeLastConnected { get; init; }

    /// <summary>F_BBS flag (0x10): this record is a forwarding partner.</summary>
    public bool IsBbs => (Flags & 0x10) != 0;
}

/// <summary>The <c>main</c> + <c>Housekeeping</c> sections of <c>linmail.cfg</c> (the values the importer needs).</summary>
internal sealed record BpqMailConfig
{
    /// <summary>main.BBSName — the BBS callsign (the @BBS identity, e.g. GB7RDG).</summary>
    public required string BbsName { get; init; }

    /// <summary>main.SYSOPCall — the sysop callsign.</summary>
    public required string SysopCall { get; init; }

    /// <summary>main.H-Route — the BBS hierarchical route suffix (e.g. #42.GBR.EURO).</summary>
    public required string HRoute { get; init; }

    /// <summary>Housekeeping.BidLifetime in days (0 = BPQ default of 60).</summary>
    public required int BidLifetime { get; init; }

    /// <summary>Housekeeping.MaxMsgno (the renumber threshold), or 0.</summary>
    public required int MaxMsgno { get; init; }

    /// <summary>Housekeeping.MaxAge in days, or 0.</summary>
    public required int MaxAge { get; init; }

    /// <summary>The forwarding partners (BBSForwarding sub-groups).</summary>
    public required IReadOnlyList<BpqPartner> Partners { get; init; }

    /// <summary>The users (BBSUsers entries).</summary>
    public required IReadOnlyList<BpqUser> Users { get; init; }
}
