using System.Net;
using System.Text;
using Bbs.Core;
using Bbs.Host.Forwarding;
using Bbs.Host.Rhp;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit.Abstractions;

namespace Bbs.Interop.Tests;

/// <summary>
/// Diagnostic: connect to real xfbbd over AXUDP, drive the production FbbSessionRunner, and
/// capture every byte both directions to see where a forwarding cycle stalls. Not a real
/// test — it dumps the transcript to /tmp/f6fbb-transcript.txt and the test output.
/// </summary>
[Trait("Category", "InteropF6fbbDebug")]
[Collection(F6fbbCollection.Name)]
public class F6fbbDebugTests
{
    private static readonly IPEndPoint Vm = new(IPAddress.Parse("192.168.76.2"), 10093);
    private readonly ITestOutputHelper _out;

    public F6fbbDebugTests(ITestOutputHelper output) => _out = output;

    [Fact]
    public async Task Debug_CaptureForwardingTranscript()
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(45));
        CancellationToken ct = deadline.Token;
        using var host = new InteropBbsHost("Q0PDN", "#42.GBR.EURO");

        string bid = $"{DateTimeOffset.UtcNow.ToUnixTimeSeconds() % 100000:D5}_Q0PDN";
        string body = $"debug body {DateTimeOffset.UtcNow.ToUnixTimeSeconds()}";
        var partner = new Partner { Call = "Q0FBB", AtCalls = ["Q0FBB"] };
        host.Store.UpsertPartner(partner);
        Message stored = host.Store.AddMessage(new MessageDraft
        {
            Type = MessageType.Personal, From = "M0LTE", Recipients = ["Q0FBB"], At = "Q0FBB",
            Bid = bid, Subject = "dbg", Body = Encoding.Latin1.GetBytes(body + "\r"),
        });
        host.Routing.RouteMessage(stored);

        var log = new List<string>();
        string outcome;
        try
        {
            await using var endpoint = await Ax25Endpoint.AttachAxudpAsync(Vm, 10093, "Q0PDN-1", ct);
            Ax25ByteSession raw = await endpoint.ConnectAsync("Q0FBB-1", ct);
            log.Add($"== CONNECTED to {raw.RemoteCallsign} ==");

            var link = new LoggingFbbConnection(raw, log);
            var runner = new FbbSessionRunner(
                host.Store, host.Receiver, host.Identity, InteropBbsHost.Version,
                TimeProvider.System, NullLogger<FbbSessionRunner>.Instance);
            IReadOnlyList<OutboundItem> outbound = OutboundBuilder.Build(
                host.Store.GetForwardQueue("Q0FBB"), partner, host.Identity, TimeProvider.System, NullLogger.Instance);

            FbbSessionResult result = await runner.RunCallerAsync(link, partner, outbound, ct);
            outcome = $"RESULT Completed={result.Completed} Graceful={result.Graceful} SID={result.PeerSidRaw} B2={result.B2Active}";
        }
        catch (Exception ex)
        {
            outcome = $"EXCEPTION {ex.GetType().Name}: {ex.Message}";
        }

        log.Add("== " + outcome + " ==");
        await File.WriteAllLinesAsync("/tmp/f6fbb-transcript.txt", log, CancellationToken.None);
        foreach (string l in log)
        {
            _out.WriteLine(l);
        }
    }
}

internal sealed class LoggingFbbConnection(IFbbConnection inner, List<string> log) : IFbbConnection
{
    public string RemoteCallsign => inner.RemoteCallsign;

    public async ValueTask<byte[]?> ReceiveAsync(CancellationToken cancellationToken)
    {
        byte[]? b = await inner.ReceiveAsync(cancellationToken).ConfigureAwait(false);
        log.Add("RX " + Show(b));
        return b;
    }

    public Task SendAsync(ReadOnlyMemory<byte> data, CancellationToken cancellationToken)
    {
        log.Add("TX " + Show(data.ToArray()));
        return inner.SendAsync(data, cancellationToken);
    }

    public Task CloseAsync(CancellationToken cancellationToken) => inner.CloseAsync(cancellationToken);

    private static string Show(byte[]? b)
    {
        if (b is null)
        {
            return "<null/closed>";
        }

        var sb = new StringBuilder($"[{b.Length}] ");
        foreach (byte x in b)
        {
            sb.Append(x is >= 32 and < 127 ? ((char)x).ToString()
                : x == 13 ? "\\r" : x == 10 ? "\\n" : $"<{x:X2}>");
        }

        return sb.ToString();
    }
}
