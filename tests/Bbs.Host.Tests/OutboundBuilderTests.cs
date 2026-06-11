using System.Text;
using Bbs.Core;
using Bbs.Host.Forwarding;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;

namespace Bbs.Host.Tests;

public sealed class OutboundBuilderTests : IDisposable
{
    private readonly DirectoryInfo _dir;
    private readonly FakeTimeProvider _time;
    private readonly BbsStore _store;

    private static readonly BbsIdentity Identity = new()
    {
        Callsign = "GB7PDN",
        HRoute = "#23.GBR.EURO",
        SoftwareVersion = "PDN0.1.0",
    };

    public OutboundBuilderTests()
    {
        _dir = Directory.CreateTempSubdirectory("bbs-builder-test-");
        _time = new FakeTimeProvider(new DateTimeOffset(2026, 6, 11, 12, 0, 0, TimeSpan.Zero));
        _store = BbsStore.Open(Path.Combine(_dir.FullName, "bbs.db"), "GB7PDN", _time);
    }

    public void Dispose()
    {
        _store.Dispose();
        _dir.Delete(recursive: true);
    }

    private Message Add(string body, string? at = null, string from = "M0LTE", string to = "G8ABC") =>
        _store.AddMessage(new MessageDraft
        {
            Type = MessageType.Personal,
            From = from,
            Recipients = [to],
            At = at,
            Subject = "Subject",
            Body = Encoding.Latin1.GetBytes(body),
        });

    private static Partner Bpq(int maxTx = 99999) => new() { Call = "GB7BPQ-2", MaxTxSize = maxTx };

    [Fact]
    public void LocallyOriginated_GetsRLinePlusBlankSeparator()
    {
        Message message = Add("Hello.\r");
        OutboundItem item = Assert.Single(
            OutboundBuilder.Build([message], Bpq(), Identity, _time, NullLogger.Instance));

        Assert.Equal(message.Number, item.Number);
        string payload = Encoding.Latin1.GetString(item.Wire.Body.Span);

        // R: line, blank separator (first hop), then the body — spec §3.7/§3.14.
        Assert.Equal("R:260611/1200Z 1@GB7PDN.#23.GBR.EURO PDN0.1.0\r\rHello.\r", payload);
        Assert.Equal(payload.Length, item.Wire.Body.Length); // the advisory FA size includes the R: line
    }

    [Fact]
    public void RelayedMessage_GetsRLineWithoutASecondBlank()
    {
        const string relayedBody = "R:260610/0900Z 7@GB7BPQ.#23.GBR.EURO BPQ6.0.24\r\rOriginal.\r";
        Message message = Add(relayedBody);
        OutboundItem item = Assert.Single(
            OutboundBuilder.Build([message], Bpq(), Identity, _time, NullLogger.Instance));

        string payload = Encoding.Latin1.GetString(item.Wire.Body.Span);
        Assert.Equal("R:260611/1200Z 1@GB7PDN.#23.GBR.EURO PDN0.1.0\r" + relayedBody, payload);
    }

    [Fact]
    public void MissingAt_FallsBackToThePartnerBaseCall()
    {
        Message message = Add("x\r");
        OutboundItem item = Assert.Single(
            OutboundBuilder.Build([message], Bpq(), Identity, _time, NullLogger.Instance));

        // "senders use the partner's own callsign" — SSID-stripped for the proposal (§3.3).
        Assert.Equal("GB7BPQ", item.Wire.AtBbs);
    }

    [Fact]
    public void ExplicitAt_IsCarriedVerbatim()
    {
        Message message = Add("x\r", at: "GB7XXX.#45.GBR.EURO");
        OutboundItem item = Assert.Single(
            OutboundBuilder.Build([message], Bpq(), Identity, _time, NullLogger.Instance));
        Assert.Equal("GB7XXX.#45.GBR.EURO", item.Wire.AtBbs);
    }

    [Fact]
    public void OversizeMessage_IsSkippedNotProposed()
    {
        Message big = Add(new string('x', 500) + "\r");
        Message small = Add("ok\r");
        IReadOnlyList<OutboundItem> items =
            OutboundBuilder.Build([big, small], Bpq(maxTx: 200), Identity, _time, NullLogger.Instance);

        OutboundItem item = Assert.Single(items);
        Assert.Equal(small.Number, item.Number);
    }

    [Fact]
    public void EmptyHRoute_FallsBackToWw()
    {
        var identity = new BbsIdentity { Callsign = "GB7PDN", HRoute = "", SoftwareVersion = "PDN0.1.0" };
        Message message = Add("x\r");
        OutboundItem item = Assert.Single(
            OutboundBuilder.Build([message], Bpq(), identity, _time, NullLogger.Instance));
        string payload = Encoding.Latin1.GetString(item.Wire.Body.Span);
        Assert.StartsWith("R:260611/1200Z 1@GB7PDN.WW PDN0.1.0\r", payload, StringComparison.Ordinal);
    }
}
