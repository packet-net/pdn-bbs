using Bbs.Import.Bpq;
using Microsoft.Extensions.Time.Testing;
using static Bbs.Import.Bpq.Tests.BpqBinaryBuilders;

namespace Bbs.Import.Bpq.Tests;

/// <summary>
/// The forwarding-partner F_BBS filter (Tom's rule): a BBSForwarding entry is imported ONLY if it has
/// a BBS-checked (F_BBS, flags &amp; 0x10) user record. Disabled stubs AND active partners that share a
/// BBSNumber slot with the real F_BBS BBS (GB7MNK/GB7BRK vs GB7BPQ on slot 7 in the live data) are
/// dropped — but a dropped ACTIVE partner is reported, never silent.
/// </summary>
public sealed class BpqPartnerFilterTests
{
    // GB7KEP has an F_BBS user (flags 16) -> KEEP. GB7DRP has a non-F_BBS user (flags 0) and is
    // enabled -> SKIP (the active-collision analog). GB7OLD has NO user record and is disabled ->
    // SKIP (the stub analog). The GB7RDG self-entry is dropped silently.
    private const string Linmail =
        """
        main :
        {
          BBSName = "GB7RDG";
          SYSOPCall = "GB7RDG";
        };
        BBSForwarding :
        {
          GB7RDG :
          {
            Enabled = 0;
          };
          GB7KEP :
          {
            ConnectScript = "C 1 GB7KEP";
            Enabled = 1;
          };
          GB7DRP :
          {
            ConnectScript = "C 1 GB7DRP";
            Enabled = 1;
          };
          GB7OLD :
          {
            ConnectScript = "";
            Enabled = 0;
          };
        };
        BBSUsers :
        {
          GB7KEP = "BBS^^^^^^^0^16^0^1^0^0^0^^";
          GB7DRP = "BBS^^^^^^^0^0^0^0^0^0^0^^";
        };
        """;

    private static BpqDumpFixture BuildDump()
    {
        var f = new BpqDumpFixture();
        f.WriteDirmes(BuildDirmesNew(latestNumber: 100, []));
        f.WriteWfbid(BuildWfbid(WfbidReader.Size64, []));
        f.WriteLinmail(Linmail);
        return f;
    }

    [Fact]
    public void OnlyPartnersWithFbbsUserAreImported()
    {
        using BpqDumpFixture dump = BuildDump();

        ImportReport report = BpqImporter.Run(
            new ImportOptions { SourceDirectory = dump.Dir, TargetDatabase = System.IO.Path.Combine(dump.Dir, "unused.db"), DryRun = true },
            new FakeTimeProvider());

        Assert.Equal(1, report.ImportedPartners); // GB7KEP only
    }

    [Fact]
    public void SkippedPartnersAreReportedWithEnabledState()
    {
        using BpqDumpFixture dump = BuildDump();

        ImportReport report = BpqImporter.Run(
            new ImportOptions { SourceDirectory = dump.Dir, TargetDatabase = System.IO.Path.Combine(dump.Dir, "unused.db"), DryRun = true },
            new FakeTimeProvider());

        // The active partner is dropped but VISIBLE (so a no-rollback migration never silently
        // stops forwarding to a live BBS); the disabled stub is dropped too.
        Assert.Contains("GB7DRP (enabled)", report.SkippedPartners);
        Assert.Contains("GB7OLD (disabled)", report.SkippedPartners);
        Assert.Equal(2, report.SkippedPartners.Count);
        Assert.DoesNotContain(report.SkippedPartners, s => s.StartsWith("GB7KEP", System.StringComparison.Ordinal));
    }
}
