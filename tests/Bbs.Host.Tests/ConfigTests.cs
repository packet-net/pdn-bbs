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
    public void Collect_DefaultsFalse_AndRoundTripsToThePartner()
    {
        // Default off → the scheduler never dials an empty queue (existing behaviour). An
        // explicit collect:true maps to Partner.Collect (the reverse-collection poll).
        Assert.False(new PartnerConfig { Call = "GB7BPQ" }.ToPartner().Collect);

        BbsHostConfig config = BbsHostConfigFile.Parse("""
            callsign: GB7PDN
            partners:
              - call: GB7CIP
                collect: true
                intervalMinutes: 30
            """);
        PartnerConfig partner = Assert.Single(config.Partners);
        Assert.True(partner.Collect);
        Partner mapped = partner.ToPartner();
        Assert.True(mapped.Collect);
        Assert.Equal(30 * 60, mapped.ForwardIntervalSeconds);
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
    public void SyncPartners_SeedsAnEmptyStoreFromConfig()
    {
        var time = new FakeTimeProvider();
        using var store = BbsStore.Open(Path.Combine(_dir.FullName, "bbs.db"), "GB7PDN", time);

        var config = new BbsHostConfig
        {
            Partners =
            [
                new PartnerConfig { Call = "GB7BPQ", IntervalMinutes = 15 },
                new PartnerConfig { Call = "GB7RDG", Enabled = false },
            ],
        };
        HostStartup.SyncPartners(store, config);

        Partner? bpq = store.GetPartner("GB7BPQ");
        Assert.NotNull(bpq);
        Assert.Equal(15 * 60, bpq.ForwardIntervalSeconds);
        Partner? rdg = store.GetPartner("GB7RDG");
        Assert.NotNull(rdg);
        Assert.False(rdg.Enabled);
    }

    [Fact]
    public void SyncPartners_LeavesAPopulatedStoreUntouched_StoreFirst()
    {
        // Store-first: a non-empty store is authoritative. bbs.yaml is a first-boot SEED only — it
        // must NOT re-import or prune once the store has partners, so editor edits persist across
        // restarts (a partner added in the editor survives; one removed in the editor stays gone).
        var time = new FakeTimeProvider();
        using var store = BbsStore.Open(Path.Combine(_dir.FullName, "bbs.db"), "GB7PDN", time);
        store.UpsertPartner(new Partner { Call = "GB7EDIT" }); // added via the editor

        var config = new BbsHostConfig
        {
            Partners = [new PartnerConfig { Call = "GB7SEED" }], // would-be seed
        };
        HostStartup.SyncPartners(store, config);

        Assert.NotNull(store.GetPartner("GB7EDIT")); // kept — not pruned
        Assert.Null(store.GetPartner("GB7SEED"));    // not seeded into a populated store
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
        Assert.Equal(993, config.Imap.Port); // standard implicit-TLS IMAP (the .deb grants CAP_NET_BIND_SERVICE)
        Assert.True(config.Imap.Tls.Enabled); // TLS defaults ON whenever IMAP is enabled
        Assert.True(config.Imap.Tls.GenerateSelfSigned);
        Assert.Null(config.Imap.Tls.CertificatePath);

        // SMTP submission is default-off in the standalone default too.
        Assert.False(config.Smtp.Enabled);
        Assert.Equal(465, config.Smtp.Port);
        Assert.Equal(587, config.Smtp.StartTlsPort);
        Assert.True(config.Smtp.Tls.Enabled);
    }

    [Fact]
    public void PdnDefaultYaml_EnablesImapAndSmtpPlaintextOnLoopback()
    {
        // Under pdn the tailnet sidecar owns the TLS edge: the BBS serves IMAP + SMTP
        // submission as PLAINTEXT on fixed loopback ports, never its own TLS / a public bind.
        BbsHostConfig config = BbsHostConfigFile.Parse(BbsHostConfigFile.PdnDefaultYaml);

        // The non-mail surface is unchanged from standalone.
        Assert.Equal(BbsHostConfig.PlaceholderCallsign, config.Callsign);
        Assert.Equal("127.0.0.1", config.Web.Bind);
        Assert.Equal(18090, config.Web.Port);
        Assert.Null(config.Rhp.Host);
        Assert.Null(config.Rhp.Port);

        // IMAP: on, plaintext, loopback, the pinned forward port.
        Assert.True(config.Imap.Enabled);
        Assert.Equal("127.0.0.1", config.Imap.Bind);
        Assert.Equal(BbsHostConfigFile.PdnImapLoopbackPort, config.Imap.Port);
        Assert.Equal(11430, config.Imap.Port); // matches pdn-app.yaml forward.target
        Assert.False(config.Imap.Tls.Enabled);
        Assert.False(config.Imap.Tls.GenerateSelfSigned);

        // SMTP submission: on, plaintext, loopback, the pinned forward port, no STARTTLS.
        Assert.True(config.Smtp.Enabled);
        Assert.Equal("127.0.0.1", config.Smtp.Bind);
        Assert.Equal(BbsHostConfigFile.PdnSmtpLoopbackPort, config.Smtp.Port);
        Assert.Equal(11465, config.Smtp.Port); // matches pdn-app.yaml forward.target
        Assert.Equal(0, config.Smtp.StartTlsPort); // pdn forwards only implicit-TLS :465
        Assert.False(config.Smtp.Tls.Enabled);
        Assert.False(config.Smtp.Tls.GenerateSelfSigned);
    }

    [Fact]
    public void LoadOrCreate_WritesPdnDefault_WhenAppIdPresent()
    {
        // PDN_APP_ID present (the supervisor sets it) → the pdn-flavoured first-run default.
        (BbsHostConfig config, bool created) = BbsHostConfigFile.LoadOrCreate(
            _dir.FullName, key => key == BbsHostConfigFile.PdnAppIdEnv ? "bbs" : null);

        Assert.True(created);
        string text = File.ReadAllText(Path.Combine(_dir.FullName, BbsHostConfigFile.FileName));
        Assert.Contains("created on first run UNDER PDN", text, StringComparison.Ordinal);

        // The mail listeners came up plaintext-on-loopback.
        Assert.True(config.Imap.Enabled);
        Assert.Equal(11430, config.Imap.Port);
        Assert.False(config.Imap.Tls.Enabled);
        Assert.True(config.Smtp.Enabled);
        Assert.Equal(11465, config.Smtp.Port);
        Assert.False(config.Smtp.Tls.Enabled);
    }

    [Fact]
    public void LoadOrCreate_WritesStandaloneDefault_WhenNoAppId()
    {
        // No PDN_APP_ID → the historical standalone default (mail off; its own TLS when opted in).
        (BbsHostConfig config, bool created) = BbsHostConfigFile.LoadOrCreate(_dir.FullName, NoEnv);

        Assert.True(created);
        string text = File.ReadAllText(Path.Combine(_dir.FullName, BbsHostConfigFile.FileName));
        Assert.DoesNotContain("UNDER PDN", text, StringComparison.Ordinal);

        Assert.False(config.Imap.Enabled);
        Assert.False(config.Smtp.Enabled);
    }

    // ----- Node-owned-callsign contract: PDN_APP_CALLSIGN is bound verbatim, ahead of all else. -----

    [Fact]
    public void ResolveCallsign_PdnAppCallsign_BindsVerbatim_NoProbe()
    {
        // The node injects the exact callsign — bind it as-is, no derivation, no probe.
        var config = new BbsHostConfig { Callsign = BbsHostConfig.PlaceholderCallsign };
        BbsHostConfigFile.ResolvedCallsign r = BbsHostConfigFile.ResolveCallsign(
            config, key => key == BbsHostConfigFile.PdnAppCallsignEnv ? "GB7XYZ-9" : null);

        Assert.Equal("GB7XYZ-9", r.Callsign);
        Assert.False(r.Probe);
        Assert.Null(r.NodeCallsign);
    }

    [Fact]
    public void ResolveCallsign_PdnAppCallsign_IsNormalised()
    {
        // Lower-case / whitespace from the env is normalised the same as any other callsign source.
        var config = new BbsHostConfig { Callsign = BbsHostConfig.PlaceholderCallsign };
        BbsHostConfigFile.ResolvedCallsign r = BbsHostConfigFile.ResolveCallsign(
            config, key => key == BbsHostConfigFile.PdnAppCallsignEnv ? "  gb7xyz-9  " : null);

        Assert.Equal("GB7XYZ-9", r.Callsign);
        Assert.False(r.Probe);
    }

    [Fact]
    public void ResolveCallsign_PdnAppCallsign_WinsOverExplicitConfigAndNodeEnv()
    {
        // The node is the authority: PDN_APP_CALLSIGN wins over an explicit bbs.yaml callsign AND
        // over the PDN_NODE_CALLSIGN derivation path — no derivation, no probe.
        var config = new BbsHostConfig { Callsign = "GB7ABC-5" };
        BbsHostConfigFile.ResolvedCallsign r = BbsHostConfigFile.ResolveCallsign(config, key => key switch
        {
            BbsHostConfigFile.PdnAppCallsignEnv => "GB7XYZ-9",
            BbsHostConfigFile.PdnNodeCallsignEnv => "M9YYY",
            _ => null,
        });

        Assert.Equal("GB7XYZ-9", r.Callsign);
        Assert.False(r.Probe);
        Assert.Null(r.NodeCallsign);
    }

    [Fact]
    public void ResolveCallsign_PdnAppCallsignEmpty_FallsBackToDerivation()
    {
        // An empty PDN_APP_CALLSIGN (older node / not set) falls back to the legacy derivation path.
        var config = new BbsHostConfig { Callsign = BbsHostConfig.PlaceholderCallsign };
        BbsHostConfigFile.ResolvedCallsign r = BbsHostConfigFile.ResolveCallsign(config, key => key switch
        {
            BbsHostConfigFile.PdnAppCallsignEnv => "",
            BbsHostConfigFile.PdnNodeCallsignEnv => "M9YYY",
            _ => null,
        });

        Assert.Equal("M9YYY-1", r.Callsign);
        Assert.True(r.Probe);
        Assert.Equal("M9YYY", r.NodeCallsign);
    }

    [Fact]
    public void ResolveCallsign_PdnAppCallsignAbsent_FallsBackToExplicitConfig()
    {
        // No PDN_APP_CALLSIGN at all → the explicit bbs.yaml callsign still wins (fallback path).
        var config = new BbsHostConfig { Callsign = "GB7ABC-5" };
        BbsHostConfigFile.ResolvedCallsign r = BbsHostConfigFile.ResolveCallsign(config, NoEnv);

        Assert.Equal("GB7ABC-5", r.Callsign);
        Assert.False(r.Probe);
        Assert.Null(r.NodeCallsign);
    }

    // ----- Brief change #1: callsign derivation + the free-SSID probe (ResolveCallsign). -----

    [Fact]
    public void ResolveCallsign_PlaceholderUnderPdn_DerivesNodeDashSsid_AndProbes()
    {
        // Placeholder callsign + PDN_NODE_CALLSIGN present → derive <node-base>-1 and mark to probe.
        var config = new BbsHostConfig { Callsign = BbsHostConfig.PlaceholderCallsign };
        BbsHostConfigFile.ResolvedCallsign r = BbsHostConfigFile.ResolveCallsign(
            config, key => key == BbsHostConfigFile.PdnNodeCallsignEnv ? "M9YYY" : null);

        Assert.Equal("M9YYY-1", r.Callsign); // DerivedDefaultSsid = 1
        Assert.True(r.Probe);
        Assert.Equal("M9YYY", r.NodeCallsign);
    }

    [Fact]
    public void ResolveCallsign_PlaceholderUnderPdn_StripsNodeOwnSsid()
    {
        // The node's own SSID is stripped before deriving (M9YYY-2 → BBS at M9YYY-1).
        var config = new BbsHostConfig { Callsign = BbsHostConfig.PlaceholderCallsign };
        BbsHostConfigFile.ResolvedCallsign r = BbsHostConfigFile.ResolveCallsign(
            config, key => key == BbsHostConfigFile.PdnNodeCallsignEnv ? "M9YYY-2" : null);

        Assert.Equal("M9YYY-1", r.Callsign);
        Assert.True(r.Probe);
        Assert.Equal("M9YYY-2", r.NodeCallsign); // kept whole so the probe can skip SSID 2
    }

    [Fact]
    public void ResolveCallsign_NodeOwnsTheDefaultSsid_DoesNotAdoptTheNodeOwnCallsign()
    {
        // PDN_NODE_CALLSIGN=M9YYY-1 (node at the default BBS SSID): the resolved primary must NOT be
        // the node's own callsign — it lands on the first free, non-node SSID instead.
        var config = new BbsHostConfig { Callsign = BbsHostConfig.PlaceholderCallsign };
        BbsHostConfigFile.ResolvedCallsign r = BbsHostConfigFile.ResolveCallsign(
            config, key => key == BbsHostConfigFile.PdnNodeCallsignEnv ? "M9YYY-1" : null);

        Assert.NotEqual("M9YYY-1", r.Callsign);
        Assert.Equal("M9YYY-2", r.Callsign);
        Assert.True(r.Probe);
        Assert.Equal("M9YYY-1", r.NodeCallsign);
    }

    [Fact]
    public void ResolveCallsign_ExplicitCallsign_WinsOutright_NoDerivationNoProbe()
    {
        // An explicit, non-placeholder callsign wins even with PDN_NODE_CALLSIGN set.
        var config = new BbsHostConfig { Callsign = "GB7ABC-5" };
        BbsHostConfigFile.ResolvedCallsign r = BbsHostConfigFile.ResolveCallsign(
            config, key => key == BbsHostConfigFile.PdnNodeCallsignEnv ? "M9YYY" : null);

        Assert.Equal("GB7ABC-5", r.Callsign);
        Assert.False(r.Probe);
        Assert.Null(r.NodeCallsign);
    }

    [Fact]
    public void ResolveCallsign_Standalone_KeepsPlaceholder_NoProbe()
    {
        // No PDN_NODE_CALLSIGN → standalone keeps the placeholder, never probes.
        var config = new BbsHostConfig { Callsign = BbsHostConfig.PlaceholderCallsign };
        BbsHostConfigFile.ResolvedCallsign r = BbsHostConfigFile.ResolveCallsign(config, NoEnv);

        Assert.Equal(BbsHostConfig.PlaceholderCallsign, r.Callsign);
        Assert.False(r.Probe);
        Assert.Null(r.NodeCallsign);
    }

    // ----- Brief change #2: the BBS service alias config knob. -----

    [Fact]
    public void ServiceCallsign_DefaultsToBbs_AndRoundTrips()
    {
        Assert.Equal("BBS", new BbsHostConfig().ServiceCallsign);

        BbsHostConfig parsed = BbsHostConfigFile.Parse("callsign: GB7PDN\nserviceCallsign: MAIL");
        Assert.Equal("MAIL", parsed.ServiceCallsign);

        // The default YAML documents and round-trips the default alias.
        Assert.Equal("BBS", BbsHostConfigFile.Parse(BbsHostConfigFile.DefaultYaml).ServiceCallsign);
        Assert.Equal("BBS", BbsHostConfigFile.Parse(BbsHostConfigFile.PdnDefaultYaml).ServiceCallsign);
    }

    [Fact]
    public void ServiceCallsign_EmptyDisablesTheAlias()
    {
        BbsHostConfig parsed = BbsHostConfigFile.Parse("callsign: GB7PDN\nserviceCallsign: \"\"");
        Assert.Equal("", parsed.ServiceCallsign);
    }
}
