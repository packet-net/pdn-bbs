using Bbs.Import.Bpq;
using static Bbs.Import.Bpq.Tests.BpqBinaryBuilders;

namespace Bbs.Import.Bpq.Tests;

/// <summary>
/// Parser tests for DIRMES.SYS (<c>struct MsgInfo</c> / <c>struct OldMsgInfo</c>). Offsets are
/// verified against bpqmail.h:615/:586 and the real fixtures.
/// </summary>
public sealed class DirmesReaderTests
{
    [Fact]
    public void Decode_NewLayout_ReadsFieldsAndLatestNumber()
    {
        byte[] image = BuildDirmesNew(
            latestNumber: 1119,
            messages:
            [
                new MsgSpec
                {
                    Type = 'B', Status = '$', Number = 930,
                    From = "LU9DCE", To = "NEWS", Bid = "14986_LU9DCE", Title = "Daily news",
                    DateReceived = 1782086407, DateCreated = 1782000000, DateChanged = 1782090000,
                    FbbsBits = [3], ForwBits = [1, 4, 5],
                },
            ]);

        DirmesReader.Result result = DirmesReader.Decode(image);

        Assert.False(result.LegacyLayout);
        Assert.Equal(1119, result.LatestMessageNumber);
        BpqMessageHeader m = Assert.Single(result.Messages);
        Assert.Equal('B', m.Type);
        Assert.Equal('$', m.Status);
        Assert.Equal(930, m.Number);
        Assert.Equal("LU9DCE", m.From);
        Assert.Equal("NEWS", m.To);
        Assert.Equal("14986_LU9DCE", m.Bid);
        Assert.Equal("Daily news", m.Title);
        Assert.Equal(1782086407, m.DateReceived);
        Assert.Equal([3], m.StillToForwardBbsNumbers);
        Assert.Equal([1, 4, 5], m.AlreadyForwardedBbsNumbers);
    }

    [Fact]
    public void Decode_BitmapMath_MatchesBpqCheckFwdBit()
    {
        // Bit n -> byte (n-1)/8, mask 1<<((n-1)%8). Exercise across a byte boundary (bit 9 = byte 1, bit 0).
        byte[] image = BuildDirmesNew(10,
        [
            new MsgSpec { Type = 'B', Status = '$', Number = 5, ForwBits = [1, 8, 9, 16, 17, 160] },
        ]);

        BpqMessageHeader m = Assert.Single(DirmesReader.Decode(image).Messages);
        Assert.Equal([1, 8, 9, 16, 17, 160], m.AlreadyForwardedBbsNumbers);
    }

    [Fact]
    public void Decode_LegacyLayout_DetectedAndReadsOldOffsets()
    {
        byte[] image = BuildDirmesLegacy(
            latestNumber: 42,
            messages:
            [
                new MsgSpec
                {
                    Type = 'P', Status = 'Y', Number = 40, From = "M0LTE", To = "G8BPQ",
                    Bid = "40_GB7OLD", Title = "Legacy", DateReceived = 1700000000,
                    DateCreated = 1699990000, DateChanged = 1700001000, ForwBits = [2],
                },
            ]);

        DirmesReader.Result result = DirmesReader.Decode(image);

        Assert.True(result.LegacyLayout);
        Assert.Equal(42, result.LatestMessageNumber);
        BpqMessageHeader m = Assert.Single(result.Messages);
        Assert.Equal('P', m.Type);
        Assert.Equal('Y', m.Status);
        Assert.Equal(40, m.Number);
        Assert.Equal("40_GB7OLD", m.Bid);
        Assert.Equal(1700000000, m.DateReceived);
        Assert.Equal([2], m.AlreadyForwardedBbsNumbers);
    }

    [Fact]
    public void Decode_SkipsEmptySlots_ZeroTypeOrNumber()
    {
        // A record with type byte 0 (deleted slot) must be skipped, as BPQ does (BBSUtilities.c:1430).
        var control = new byte[NewRecordSize];
        control[1] = 2;
        var empty = new byte[NewRecordSize]; // all-zero -> type 0
        byte[] good = BuildDirmesNew(1, [new MsgSpec { Type = 'B', Number = 1, Bid = "1_X" }])[NewRecordSize..];
        byte[] image = [.. control, .. empty, .. good];

        DirmesReader.Result result = DirmesReader.Decode(image);
        Assert.Single(result.Messages);
        Assert.Equal(1, result.Messages[0].Number);
    }

    [Fact]
    public void Decode_InconsistentSize_DoesNotThrow_WarnsOnTrailingBytes()
    {
        byte[] image = BuildDirmesNew(1, [new MsgSpec { Type = 'B', Number = 1, Bid = "1_X" }]);
        byte[] truncated = image[..^7]; // chop 7 bytes off the last record

        DirmesReader.Result result = DirmesReader.Decode(truncated);
        Assert.NotEmpty(result.Warnings);
    }

    [Fact]
    public void Decode_EmptyFile_ReturnsNoMessages()
    {
        DirmesReader.Result result = DirmesReader.Decode([]);
        Assert.Empty(result.Messages);
        Assert.Equal(0, result.LatestMessageNumber);
        Assert.NotEmpty(result.Warnings);
    }

    [Fact]
    public void Decode_RealOracleFixture_ParsesCleanly()
    {
        if (!Fixtures.HasOracleState)
        {
            return; // oracle fixture is a gitignored docker-runtime artifact; present locally/in docker, absent in CI
        }

        string path = Fixtures.OracleDirmes();
        DirmesReader.Result result = DirmesReader.Read(path);

        // docker/oracle/state: control + 2 message records, both housekeeping-results bodies.
        Assert.Equal(2, result.Messages.Count);
        Assert.All(result.Messages, m => Assert.Equal('P', m.Type));
        Assert.Contains(result.Messages, m => m.Status == 'K');
        Assert.Equal("2_GB7BPQ-1", result.Messages[0].Bid);
    }

    [Fact]
    public void Decode_RealStaleSnapshot_DoesNotThrowAndReportsForwardBits()
    {
        // The gb7rdg snapshot is real-but-stale; the parser must read it without crashing.
        if (!Fixtures.HasGb7rdgSnapshot)
        {
            return; // snapshot not present in this checkout; skip silently.
        }

        DirmesReader.Result result = DirmesReader.Read(Fixtures.Gb7rdgDirmes());
        Assert.False(result.LegacyLayout);
        Assert.Equal(192, result.Messages.Count);
        Assert.Equal(1119, result.LatestMessageNumber);
        Assert.Contains(result.Messages, m => m.AlreadyForwardedBbsNumbers.Count > 0);
    }
}
