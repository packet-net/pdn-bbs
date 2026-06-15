namespace Bbs.Core.Tests;

/// <summary>The persisted per-partner forwarding health (schema v9): success/failure recording, the
/// consecutive-failure streak, and survival across a store reopen (a node restart).</summary>
public sealed class ForwardingStatusStoreTests : IDisposable
{
    private readonly TestStore _ts = new();

    public void Dispose() => _ts.Dispose();

    [Fact]
    public void Unknown_partner_hasNoStatus()
    {
        Assert.Null(_ts.Store.GetForwardingStatus("GB7RDG"));
    }

    [Fact]
    public void Failure_streakIncrements_andSuccessResets()
    {
        _ts.Store.RecordForwardingFailure("GB7RDG", "connect refused");
        PartnerForwardingState s1 = _ts.Store.GetForwardingStatus("GB7RDG")!;
        Assert.False(s1.Ok);
        Assert.Equal("connect refused", s1.Error);
        Assert.Equal(1, s1.ConsecutiveFailures);

        _ts.Store.RecordForwardingFailure("GB7RDG", "still refused");
        Assert.Equal(2, _ts.Store.GetForwardingStatus("GB7RDG")!.ConsecutiveFailures);

        _ts.Store.RecordForwardingSuccess("GB7RDG");
        PartnerForwardingState ok = _ts.Store.GetForwardingStatus("GB7RDG")!;
        Assert.True(ok.Ok);
        Assert.Null(ok.Error);
        Assert.Equal(0, ok.ConsecutiveFailures);

        _ts.Store.RecordForwardingFailure("GB7RDG", "refused");
        Assert.Equal(1, _ts.Store.GetForwardingStatus("GB7RDG")!.ConsecutiveFailures); // restarts after a success
    }

    [Fact]
    public void Status_survivesAReopen_theRestartCase()
    {
        _ts.Store.RecordForwardingFailure("GB7RDG", "connect refused");
        _ts.Store.RecordForwardingFailure("GB7RDG", "connect refused");

        BbsStore reopened = _ts.Reopen();   // simulate a node restart

        PartnerForwardingState s = reopened.GetForwardingStatus("GB7RDG")!;
        Assert.False(s.Ok);
        Assert.Equal("connect refused", s.Error);
        Assert.Equal(2, s.ConsecutiveFailures);   // the streak persisted, not reset to "—"
    }

    [Fact]
    public void Lookup_isCallsignCaseInsensitive()
    {
        _ts.Store.RecordForwardingFailure("gb7rdg", "x");
        Assert.NotNull(_ts.Store.GetForwardingStatus("GB7RDG"));
    }

    [Fact]
    public void Success_persistsNegotiatedMode_andPeerSid_readBack()
    {
        // The forwarding-observability fields (schema v11): a successful cycle records the negotiated
        // mode + the peer's raw SID, and GetForwardingStatus reads them back — across a reopen.
        _ts.Store.RecordForwardingSuccess("GB7RDG", "B2", "[BPQ-6.0.25.30-B12FWIHJM$]");

        PartnerForwardingState s = _ts.Store.GetForwardingStatus("GB7RDG")!;
        Assert.True(s.Ok);
        Assert.Equal("B2", s.LastMode);
        Assert.Equal("[BPQ-6.0.25.30-B12FWIHJM$]", s.LastPeerSid);

        PartnerForwardingState reopened = _ts.Reopen().GetForwardingStatus("GB7RDG")!;
        Assert.Equal("B2", reopened.LastMode);
        Assert.Equal("[BPQ-6.0.25.30-B12FWIHJM$]", reopened.LastPeerSid);
    }

    [Fact]
    public void Success_withNoMode_keepsLastNegotiatedMode()
    {
        // A reverse-collection poll that found nothing to dial reports a success with no mode — it
        // must not blank a previously recorded mode (COALESCE keeps the last actually negotiated).
        _ts.Store.RecordForwardingSuccess("GB7RDG", "B1", "[BPQ-6.0.24.44-B1FHM$]");
        _ts.Store.RecordForwardingSuccess("GB7RDG"); // a quiet success, no SID parsed

        PartnerForwardingState s = _ts.Store.GetForwardingStatus("GB7RDG")!;
        Assert.Equal("B1", s.LastMode);
        Assert.Equal("[BPQ-6.0.24.44-B1FHM$]", s.LastPeerSid);
    }

    [Fact]
    public void Unnegotiated_success_hasNullMode()
    {
        // A partner whose only success never parsed a SID has no mode recorded — the dashboard shows
        // nothing rather than a stale or invented value.
        _ts.Store.RecordForwardingSuccess("GB7RDG");
        PartnerForwardingState s = _ts.Store.GetForwardingStatus("GB7RDG")!;
        Assert.Null(s.LastMode);
        Assert.Null(s.LastPeerSid);
    }
}
