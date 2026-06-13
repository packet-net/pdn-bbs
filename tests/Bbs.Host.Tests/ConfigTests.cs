using Bbs.Core;
using Microsoft.Extensions.Time.Testing;

namespace Bbs.Host.Tests;

public sealed class ConfigTests : IDisposable
{
    private readonly DirectoryInfo _dir = Directory.CreateTempSubdirectory("bbs-config-test-");

    public void Dispose() => _dir.Delete(recursive: true);

    private static string? NoEnv(string _) => null;

    [Fact]
    public void LoadOrCreate_WritesCommentedDefaultOnFirstRun()
    {
        (BbsHostConfig config, bool created) = BbsHostConfigFile.LoadOrCreate(_dir.FullName, NoEnv);

        Assert.True(created);
        string path = Path.Combine(_dir.FullName, BbsHostConfigFile.FileName);
        Assert.True(File.Exists(path));
        Assert.Contains("# pdn-bbs configuration", File.ReadAllText(path), StringComparison.Ordinal);

        Assert.Equal(BbsHostConfig.PlaceholderCallsign, config.Callsign);
        Assert.Empty(config.Partners);
        Assert.Equal(30, config.DemuxFirstLineWaitSeconds);
        Assert.Equal("127.0.0.1", config.Web.Bind);
        Assert.Equal(18090, config.Web.Port);

        // Env-less default endpoint.
        Assert.Equal("127.0.0.1", config.Rhp.Host);
        Assert.Equal(9000, config.Rhp.Port);

        // Second load is not "created".
        (_, bool createdAgain) = BbsHostConfigFile.LoadOrCreate(_dir.FullName, NoEnv);
        Assert.False(createdAgain);
    }

    [Fact]
    public void RhpEndpoint_DefaultsFromSupervisorEnvironment()
    {
        (BbsHostConfig config, _) = BbsHostConfigFile.LoadOrCreate(_dir.FullName, key => key switch
        {
            "PDN_RHP_HOST" => "10.11.12.13",
            "PDN_RHP_PORT" => "9123",
            _ => null,
        });

        Assert.Equal("10.11.12.13", config.Rhp.Host);
        Assert.Equal(9123, config.Rhp.Port);
    }

    [Fact]
    public void ExplicitRhpEndpoint_BeatsEnvironment()
    {
        BbsHostConfig parsed = BbsHostConfigFile.Parse("""
            callsign: GB7PDN
            rhp:
              host: 192.168.1.50
              port: 9001
            """);
        BbsHostConfig config = BbsHostConfigFile.ApplyEnvironment(parsed, _ => "ignored");

        Assert.Equal("192.168.1.50", config.Rhp.Host);
        Assert.Equal(9001, config.Rhp.Port);
    }

    [Fact]
    public void FullConfig_RoundTripsIncludingPartners()
    {
        BbsHostConfig config = BbsHostConfigFile.Parse("""
            callsign: GB7PDN-4
            sysop: M0LTE
            hRoute: "#23.GBR.EURO"
            web:
              bind: 127.0.0.1
              port: 18091
            partners:
              - call: GB7BPQ
                connect: GB7BPQ-1
                intervalMinutes: 30
                sendImmediately: true
                to: [SYSOP]
                at: ["GB7BPQ", "*"]
                hr: [GBR.EURO]
                bbsHa: GB7BPQ.#23.GBR.EURO
                maxRx: 20000
                maxTx: 15000
            demuxFirstLineWaitSeconds: 10
            """);

        Assert.Equal("GB7PDN-4", config.Callsign);
        Assert.Equal("M0LTE", config.Sysop);
        Assert.Equal("#23.GBR.EURO", config.HRoute);
        Assert.Equal(18091, config.Web.Port);
        Assert.Equal(10, config.DemuxFirstLineWaitSeconds);

        PartnerConfig partner = Assert.Single(config.Partners);
        Assert.Equal("GB7BPQ", partner.Call);
        Assert.Equal("GB7BPQ-1", partner.Connect);
        Assert.Equal(30, partner.IntervalMinutes);
        Assert.True(partner.SendImmediately);

        // The store mapping (compat spec §4.1 keys).
        Partner mapped = partner.ToPartner();
        Assert.Equal(30 * 60, mapped.ForwardIntervalSeconds);
        Assert.True(mapped.ForwardNewImmediately);
        Assert.Equal(["C GB7BPQ-1"], mapped.ConnectScript);
        Assert.Equal(["SYSOP"], mapped.ToCalls);
        Assert.Equal(["GB7BPQ", "*"], mapped.AtCalls);
        Assert.Equal(["GBR.EURO"], mapped.HRoutes);
        Assert.Equal(["GBR.EURO"], mapped.HRoutesP); // v1: one hr list feeds both
        Assert.Equal("GB7BPQ.#23.GBR.EURO", mapped.BbsHa);
        Assert.Equal(20000, mapped.MaxRxSize);
        Assert.Equal(15000, mapped.MaxTxSize);
    }

    [Fact]
    public void ConnectScriptAndConTimeout_RoundTripToThePartner()
    {
        // The full connect-script keys (compat spec §4.4 / §4.1 ConTimeout).
        BbsHostConfig config = BbsHostConfigFile.Parse("""
            callsign: GB7PDN
            partners:
              - call: GB7BPQ
                connectScript:
                  - C GB7BPQ
                  - BBS
                conTimeoutSeconds: 30
            """);

        PartnerConfig partner = Assert.Single(config.Partners);
        Assert.Equal(["C GB7BPQ", "BBS"], partner.ConnectScript);
        Assert.Equal(30, partner.ConTimeoutSeconds);

        Partner mapped = partner.ToPartner();
        Assert.Equal(["C GB7BPQ", "BBS"], mapped.ConnectScript);
        Assert.Equal(30, mapped.ConTimeoutSeconds);
    }

    [Fact]
    public void AllowB2_DefaultsFalse_AndRoundTripsToThePartner()
    {
        // Default off → B1 unchanged. An explicit allowB2:true maps to Partner.AllowB2F.
        Assert.False(new PartnerConfig { Call = "GB7BPQ" }.ToPartner().AllowB2F);

        BbsHostConfig config = BbsHostConfigFile.Parse("""
            callsign: GB7PDN
            partners:
              - call: GB7RDG
                allowB2: true
            """);
        Partner mapped = Assert.Single(config.Partners).ToPartner();
        Assert.True(mapped.AllowB2F);
    }

    [Fact]
    public void ConnectScript_TakesPrecedenceOverSimpleConnect()
    {
        BbsHostConfig config = BbsHostConfigFile.Parse("""
            callsign: GB7PDN
            partners:
              - call: GB7BPQ
                connect: GB7BPQ-1
                connectScript:
                  - C GB7RDG
                  - BBS
            """);

        // Both forms set → the full script wins (documented in DefaultYaml).
        Partner mapped = Assert.Single(config.Partners).ToPartner();
        Assert.Equal(["C GB7RDG", "BBS"], mapped.ConnectScript);
    }

    [Fact]
    public void ConTimeout_DefaultsTo60AndClampsToAtLeastOneSecond()
    {
        Assert.Equal(60, new PartnerConfig { Call = "GB7BPQ" }.ToPartner().ConTimeoutSeconds);
        Assert.Equal(1, new PartnerConfig { Call = "GB7BPQ", ConTimeoutSeconds = 0 }.ToPartner().ConTimeoutSeconds);
    }

    [Fact]
    public void SyncPartners_UpsertsConfiguredAndPrunesStale()
    {
        var time = new FakeTimeProvider();
        using var store = BbsStore.Open(Path.Combine(_dir.FullName, "bbs.db"), "GB7PDN", time);
        store.UpsertPartner(new Partner { Call = "GB7OLD" });

        var config = new BbsHostConfig
        {
            Partners =
            [
                new PartnerConfig { Call = "GB7BPQ", IntervalMinutes = 15 },
                new PartnerConfig { Call = "GB7RDG", Enabled = false },
            ],
        };
        HostStartup.SyncPartners(store, config);

        Assert.Null(store.GetPartner("GB7OLD")); // config is the source of truth (v1)
        Partner? bpq = store.GetPartner("GB7BPQ");
        Assert.NotNull(bpq);
        Assert.Equal(15 * 60, bpq.ForwardIntervalSeconds);
        Partner? rdg = store.GetPartner("GB7RDG");
        Assert.NotNull(rdg);
        Assert.False(rdg.Enabled);
    }

    [Fact]
    public void DefaultYaml_ParsesBackToDefaults()
    {
        BbsHostConfig config = BbsHostConfigFile.Parse(BbsHostConfigFile.DefaultYaml);
        Assert.Equal(BbsHostConfig.PlaceholderCallsign, config.Callsign);
        Assert.Equal("", config.Sysop);
        Assert.Equal("", config.HRoute);
        Assert.Empty(config.Partners);
        Assert.Null(config.Rhp.Host);
        Assert.Null(config.Rhp.Port);
        Assert.Equal(18090, config.Web.Port);

        // IMAP is default-off: a node that does not configure it behaves exactly as before.
        Assert.False(config.Imap.Enabled);
        Assert.Equal("127.0.0.1", config.Imap.Bind);
        Assert.Equal(1143, config.Imap.Port);
        Assert.True(config.Imap.Tls.Enabled); // TLS defaults ON whenever IMAP is enabled
        Assert.True(config.Imap.Tls.GenerateSelfSigned);
        Assert.Null(config.Imap.Tls.CertificatePath);
    }
}
