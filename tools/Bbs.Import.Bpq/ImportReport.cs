using System.Globalization;
using System.Text;

namespace Bbs.Import.Bpq;

/// <summary>
/// A human-readable validation summary + source/target diff produced by an import (or a dry run).
/// Printed so a person can sanity-check the no-duplicate-transfer guarantees before cutover.
/// </summary>
internal sealed class ImportReport
{
    /// <summary>Non-fatal anomalies surfaced by the parsers + importer (orphans, count mismatches, …).</summary>
    public List<string> Warnings { get; } = [];

    // Source-side counts (from the BPQ dump).
    public string BbsName { get; set; } = string.Empty;
    public int SourceLatestMessageNumber { get; set; }
    public int SourceMessageHeaders { get; set; }
    public int SourceBids { get; set; }
    public int SourceBodiesOnDisk { get; set; }
    public int OrphanHeaders { get; set; }    // header with no .mes body
    public int OrphanBodies { get; set; }     // .mes body with no header
    public int SourcePartners { get; set; }
    public int SourceUsers { get; set; }
    public int SourceBbsPartnersWithNumber { get; set; }

    // Target-side counts (written into bbs.db).
    public SortedDictionary<char, int> MessagesByType { get; } = new();
    public SortedDictionary<char, int> MessagesByStatus { get; } = new();
    public int ImportedMessages { get; set; }
    public int ImportedBids { get; set; }
    public int VerbatimBidMessages { get; set; }
    public int TruncatedBids { get; set; }

    /// <summary>True when every BID in WFBID.SYS made it into the dedup store (the Rule-1 invariant).</summary>
    public bool AllWfbidBidsPresent { get; set; } = true;

    /// <summary>True when every imported message's verbatim BID is present in the dedup store.</summary>
    public bool AllMessageBidsPresent { get; set; } = true;
    public int HighWaterMark { get; set; }
    public int ImportedPartners { get; set; }
    public int ImportedUsers { get; set; }
    public int ImportedWhitePages { get; set; }

    /// <summary>BBSForwarding partners SKIPPED because no BBS-checked (F_BBS) user record exists for
    /// them — the "take only forwarding partners whose user record has BBS checked" rule. Each entry
    /// is "CALL (enabled|disabled)" so a dropped ACTIVE partner is visible, never silent.</summary>
    public List<string> SkippedPartners { get; } = [];

    // Per-partner forwarding legs.
    public SortedDictionary<string, (int Queued, int Sent)> PartnerLegs { get; } = new(StringComparer.Ordinal);

    /// <summary>Whether anything looks wrong enough that a human should look before cutover.</summary>
    public bool HasWarnings => Warnings.Count > 0;

    /// <summary>Renders the full summary as plain text.</summary>
    public string Render()
    {
        var sb = new StringBuilder();
        sb.AppendLine("============================================================");
        sb.AppendLine(Inv($"  BPQ -> pdn-bbs import summary  (BBS: {BbsName})"));
        sb.AppendLine("============================================================");
        sb.AppendLine();
        sb.AppendLine("Source (BPQ dump)");
        sb.AppendLine(Inv($"  Message headers (DIRMES.SYS)      : {SourceMessageHeaders}"));
        sb.AppendLine(Inv($"  Message bodies on disk (m_*.mes)  : {SourceBodiesOnDisk}"));
        sb.AppendLine(Inv($"  BID dedup records (WFBID.SYS)      : {SourceBids}"));
        sb.AppendLine(Inv($"  Latest message number (high-water) : {SourceLatestMessageNumber}"));
        sb.AppendLine(Inv($"  Forwarding partners (linmail.cfg)  : {SourcePartners}"));
        sb.AppendLine(Inv($"  Users (linmail.cfg)                : {SourceUsers}"));
        sb.AppendLine(Inv($"  Partners with a BBSNumber          : {SourceBbsPartnersWithNumber}"));
        sb.AppendLine();
        sb.AppendLine("Imported into bbs.db");
        sb.AppendLine(Inv($"  Messages                           : {ImportedMessages}"));
        sb.AppendLine(Inv($"    by type   : {RenderCounts(MessagesByType)}"));
        sb.AppendLine(Inv($"    by status : {RenderCounts(MessagesByStatus)}"));
        sb.AppendLine(Inv($"  BIDs (bids table)                  : {ImportedBids}"));
        sb.AppendLine(Inv($"    messages with a verbatim BID     : {VerbatimBidMessages}"));
        sb.AppendLine(Inv($"    BIDs truncated to 12 chars       : {TruncatedBids}"));
        sb.AppendLine(Inv($"  Message-number high-water (seq)    : {HighWaterMark}"));
        sb.AppendLine(Inv($"  Partners                           : {ImportedPartners}"));
        sb.AppendLine(Inv($"  Users                              : {ImportedUsers}"));
        sb.AppendLine(Inv($"  White-pages records                : {ImportedWhitePages}"));
        if (SkippedPartners.Count > 0)
        {
            sb.AppendLine(Inv($"  Partners SKIPPED (no F_BBS user)    : {SkippedPartners.Count}"));
            foreach (string s in SkippedPartners)
            {
                sb.AppendLine(Inv($"      - {s}"));
            }
        }
        sb.AppendLine();
        sb.AppendLine("Per-partner forwarding legs (queued / already-sent)");
        if (PartnerLegs.Count == 0)
        {
            sb.AppendLine("  (none)");
        }
        else
        {
            foreach ((string call, (int queued, int sent)) in PartnerLegs)
            {
                sb.AppendLine(Inv($"  {call,-12} queued={queued,-6} sent={sent}"));
            }
        }

        sb.AppendLine();
        sb.AppendLine("Diff / consistency checks (source vs imported)");
        sb.AppendLine(Inv($"  Orphan headers (header, no body)   : {OrphanHeaders}"));
        sb.AppendLine(Inv($"  Orphan bodies (.mes, no header)    : {OrphanBodies}   (ignored — already purged from DIRMES)"));
        sb.AppendLine(Inv($"  Headers imported == headers parsed : {(ImportedMessages == SourceMessageHeaders ? "yes" : "NO — see warnings")}"));
        sb.AppendLine(Inv($"  Every WFBID BID present in store   : {(AllWfbidBidsPresent ? "yes" : "NO — see warnings")}"));
        sb.AppendLine(Inv($"  Every message BID present in store : {(AllMessageBidsPresent ? "yes" : "NO — see warnings")}"));
        sb.AppendLine();

        if (Warnings.Count == 0)
        {
            sb.AppendLine("Warnings: none.");
        }
        else
        {
            sb.AppendLine(Inv($"Warnings ({Warnings.Count}):"));
            foreach (string w in Warnings)
            {
                sb.AppendLine($"  ! {w}");
            }
        }

        sb.AppendLine("============================================================");
        return sb.ToString();
    }

    private static string RenderCounts(SortedDictionary<char, int> counts)
    {
        if (counts.Count == 0)
        {
            return "(none)";
        }

        return string.Join("  ", counts.Select(kv => Inv($"{kv.Key}={kv.Value}")));
    }

    private static string Inv(FormattableString s) => s.ToString(CultureInfo.InvariantCulture);
}
