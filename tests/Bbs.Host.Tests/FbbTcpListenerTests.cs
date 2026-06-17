using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text;
using Bbs.Core;
using Bbs.Fbb;
using Bbs.Host.Forwarding;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;

namespace Bbs.Host.Tests;

/// <summary>
/// The inbound FBB-over-TCP forwarding listener (BPQ FBBPORT, issue #40): a TCP partner authenticates
/// via the in-protocol callsign login and forwards a message in over the shared FBB engine; unknown
/// and disabled partners are rejected before any forwarding session begins. Drives a real loopback
/// TCP socket against a live listener so the whole login → SID → propose → transfer path is exercised.
/// </summary>
public sealed class FbbTcpListenerTests : IDisposable
{
    private const string OwnCall = "GB7PDN";
    private const string PartnerSid = "[BPQ-6.0.24.44-B1FHM$]";

    private readonly DirectoryInfo _dir = Directory.CreateTempSubdirectory("bbs-fbbtcp-test-");
    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 6, 17, 12, 0, 0, TimeSpan.Zero));
    private readonly BbsStore _store;
    private readonly FbbSessionRunner _runner;

    public FbbTcpListenerTests()
    {
        _store = BbsStore.Open(Path.Combine(_dir.FullName, "bbs.db"), OwnCall, _time);
        var identity = new BbsIdentity { Callsign = OwnCall, HRoute = "#23.GBR.EURO", SoftwareVersion = "PDN0.1.0" };
        var engine = new RoutingEngine(OwnCall, "#23.GBR.EURO");
        var routing = new RoutingService(_store, engine, NullLogger<RoutingService>.Instance);
        var sevenPlus = new SevenPlusAssembler(_store, NullLogger<SevenPlusAssembler>.Instance);
        var whitePages = new WhitePagesConsumer(_store, NullLogger<WhitePagesConsumer>.Instance);
        var receiver = new InboundMessageReceiver(_store, routing, engine, sevenPlus, whitePages, OwnCall, _time, NullLogger<InboundMessageReceiver>.Instance);
        _runner = new FbbSessionRunner(_store, receiver, identity, "0.1.0", _time, NullLogger<FbbSessionRunner>.Instance);
    }

    public void Dispose()
    {
        _store.Dispose();
        _dir.Delete(recursive: true);
    }

    private FbbTcpListener NewListener(int maxConnections = 8) => new(
        new FbbTcpConfig { Enabled = true, Bind = "127.0.0.1", Port = 0, MaxConnections = maxConnections },
        _store, _runner, "0.1.0", _time, NullLogger<FbbTcpListener>.Instance);

    /// <summary>Runs a single accepted connection through the listener against a connected loopback socket.</summary>
    private static async Task<TcpClient> ConnectToAsync(FbbTcpListener listener, Func<TcpClient, Task> drive)
    {
        // A bound loopback TcpListener to get a port, then we hand the accepted socket to the unit's
        // per-client handler — exercising the real login + auth + session path without the accept loop.
        var raw = new TcpListener(IPAddress.Loopback, 0);
        raw.Start();
        int port = ((IPEndPoint)raw.LocalEndpoint).Port;

        var client = new TcpClient();
        await client.ConnectAsync(IPAddress.Loopback, port);
        TcpClient server = await raw.AcceptTcpClientAsync();
        raw.Stop();

        Task handle = listener.HandleClientAsync(server, CancellationToken.None);
        await drive(client);
        await handle;
        return client;
    }

    private static async Task<string> ReadLineAsync(NetworkStream s, CancellationToken ct = default)
    {
        var bytes = new List<byte>();
        var one = new byte[1];
        while (true)
        {
            int n = await s.ReadAsync(one, ct);
            if (n == 0)
            {
                break;
            }

            if (one[0] == (byte)'\n')
            {
                break;
            }

            if (one[0] != (byte)'\r')
            {
                bytes.Add(one[0]);
            }
        }

        return Encoding.Latin1.GetString([.. bytes]);
    }

    private static Task WriteLineAsync(NetworkStream s, string line) =>
        s.WriteAsync(Encoding.Latin1.GetBytes(line + "\r\n")).AsTask();

    [Fact]
    public async Task KnownPartner_AuthenticatesViaCallsignLogin_AndForwardsAMessageIn()
    {
        _store.UpsertPartner(new Partner { Call = "GB7BPQ", AtCalls = ["*"] });
        FbbTcpListener listener = NewListener();

        const string body = "R:260617/1000Z 1@GB7BPQ.#23.GBR.EURO BPQ6.0.24\r\rHello over TCP.\r";
        string proposal = string.Create(CultureInfo.InvariantCulture, $"FA P M0XYZ GB7PDN G8ABC 77_GB7BPQ {body.Length}");

        await ConnectToAsync(listener, async client =>
        {
            NetworkStream s = client.GetStream();

            // The in-protocol login: the listener prompts for the callsign; we present ours.
            Assert.Equal("Callsign :", await ReadLineAsync(s));
            await WriteLineAsync(s, "GB7BPQ");

            // It greets with OUR SID (B1-only — this partner is not B2-enabled).
            Assert.Equal("[PDN-0.1.0-B1FHM$]", await ReadLineAsync(s));

            // We answer with our SID, then propose + transfer one message.
            await WriteLineAsync(s, PartnerSid);
            await WriteLineAsync(s, proposal);
            await WriteLineAsync(s, ProposalBlock.BuildTerminator(ProposalBlock.ComputeChecksum([proposal])));

            Assert.Equal("FS +", await ReadLineAsync(s));

            byte[] wire = BlockFraming.EncodeMessage(
                "Subject over TCP", 0, LzhufContainer.Encode(LzhufContainerKind.B1, Encoding.Latin1.GetBytes(body)));
            await s.WriteAsync(wire);

            // Receipt is implicitly acked by our turn — empty queue → FF; we FQ to end.
            Assert.Equal("FF", await ReadLineAsync(s));
            await WriteLineAsync(s, "FQ");
        });

        // The message landed through the SAME Core receive path the RHP inbound path uses.
        Message stored = Assert.Single(_store.ListMessages(new MessageQuery()));
        Assert.Equal("M0XYZ", stored.From);
        Assert.Equal("G8ABC", Assert.Single(stored.Recipients).ToCall);
        Assert.Equal("77_GB7BPQ", stored.Bid);
        Assert.Equal("Subject over TCP", stored.Subject);
        Assert.Equal("GB7BPQ", stored.ReceivedFrom); // partner identity came from the in-protocol login
        Assert.Equal(body, stored.GetBodyText());
    }

    [Fact]
    public async Task UnknownPartner_IsRejected_NoSessionNoMessage()
    {
        // No partner configured at all.
        FbbTcpListener listener = NewListener();

        await ConnectToAsync(listener, async client =>
        {
            NetworkStream s = client.GetStream();
            Assert.Equal("Callsign :", await ReadLineAsync(s));
            await WriteLineAsync(s, "GB7XYZ"); // not a configured partner

            // We are told off and the socket is closed — never our SID.
            string notice = await ReadLineAsync(s);
            Assert.Contains("Not authorised", notice, StringComparison.Ordinal);
            Assert.DoesNotContain("PDN", notice, StringComparison.Ordinal);
            Assert.Equal(string.Empty, await ReadLineAsync(s)); // EOF
        });

        Assert.Empty(_store.ListMessages(new MessageQuery()));
    }

    [Fact]
    public async Task DisabledPartner_IsRejected()
    {
        _store.UpsertPartner(new Partner { Call = "GB7OFF", Enabled = false });
        FbbTcpListener listener = NewListener();

        await ConnectToAsync(listener, async client =>
        {
            NetworkStream s = client.GetStream();
            Assert.Equal("Callsign :", await ReadLineAsync(s));
            await WriteLineAsync(s, "GB7OFF");
            Assert.Contains("Not authorised", await ReadLineAsync(s), StringComparison.Ordinal);
        });

        Assert.Empty(_store.ListMessages(new MessageQuery()));
    }

    [Fact]
    public async Task PartnerMatchedByBaseCallsign_SsidOnTheLoginIsTolerated()
    {
        // The partner is configured by base call; an inbound login under an SSID still authorizes.
        _store.UpsertPartner(new Partner { Call = "GB7BPQ", AtCalls = ["*"] });
        FbbTcpListener listener = NewListener();

        await ConnectToAsync(listener, async client =>
        {
            NetworkStream s = client.GetStream();
            Assert.Equal("Callsign :", await ReadLineAsync(s));
            await WriteLineAsync(s, "GB7BPQ-1"); // SSID'd login

            // Authorized → greeted with our SID, not the rejection notice.
            Assert.Equal("[PDN-0.1.0-B1FHM$]", await ReadLineAsync(s));

            // Nothing to forward this time: answer the SID and immediately FF; we FQ-close.
            await WriteLineAsync(s, PartnerSid);
            await WriteLineAsync(s, "FF");
            Assert.Equal("FQ", await ReadLineAsync(s));
        });

        Assert.Empty(_store.ListMessages(new MessageQuery()));
    }
}
