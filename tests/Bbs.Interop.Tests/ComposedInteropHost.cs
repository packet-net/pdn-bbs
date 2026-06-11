using Bbs.Core;
using Bbs.Host;
using Bbs.Host.Tests;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;

namespace Bbs.Interop.Tests;

/// <summary>
/// The REAL production composition booted for the interop lane: a temp state dir with a
/// <c>bbs.yaml</c> naming the PDNBBS-1 identity the oracle's partner entry expects, the
/// RHP endpoint pointed at a linked <see cref="FakeRhpServer"/> (the node seam — see
/// <see cref="RhpAx25Bridge"/>), and <see cref="HostComposition.Build"/> exactly as
/// Program runs it (every component loop: link, demux, scheduler, housekeeping, webmail
/// on an ephemeral loopback port). Nothing inside the host is faked.
/// </summary>
internal sealed class ComposedInteropHost : IAsyncDisposable
{
    private readonly DirectoryInfo _dir;

    private ComposedInteropHost(FakeRhpServer server, WebApplication app, DirectoryInfo dir)
    {
        Server = server;
        App = app;
        _dir = dir;
    }

    /// <summary>The fake pdn node the host is attached to.</summary>
    public FakeRhpServer Server { get; }

    /// <summary>The running production composition.</summary>
    public WebApplication App { get; }

    /// <summary>The host's live store (the singleton the composition opened).</summary>
    public BbsStore Store => App.Services.GetRequiredService<BbsStore>();

    /// <summary>The host's routing service (for injecting outbound traffic the way the console would).</summary>
    public RoutingService Routing => App.Services.GetRequiredService<RoutingService>();

    /// <summary>
    /// Builds and starts the composed host. <paramref name="partnersYaml"/> is the
    /// <c>partners:</c> block verbatim (callsign identity etc. are fixed to the oracle's
    /// expectations: bind PDNBBS-1, HA PDNBBS.#23.GBR.EURO).
    /// </summary>
    public static async Task<ComposedInteropHost> StartAsync(string partnersYaml)
    {
        var server = new FakeRhpServer();
        server.Start();
        DirectoryInfo dir = Directory.CreateTempSubdirectory("bbs-interop-host-");
        await File.WriteAllTextAsync(Path.Combine(dir.FullName, "bbs.yaml"), $"""
            callsign: {InteropBbsHost.AxCall}
            sysop: M0LTE
            hRoute: "{InteropBbsHost.HRoute}"
            web:
              bind: 127.0.0.1
              port: 0
            rhp:
              host: 127.0.0.1
              port: {server.Port}
            {partnersYaml}
            demuxFirstLineWaitSeconds: 30
            """).ConfigureAwait(false);

        // HostComposition.Build reads PDN_APP_STATE synchronously; restore straight after
        // (the OracleCollection serialises interop classes, so this cannot race a peer).
        string? previous = Environment.GetEnvironmentVariable("PDN_APP_STATE");
        WebApplication app;
        try
        {
            Environment.SetEnvironmentVariable("PDN_APP_STATE", dir.FullName);
            app = HostComposition.Build([]);
        }
        finally
        {
            Environment.SetEnvironmentVariable("PDN_APP_STATE", previous);
        }

        var host = new ComposedInteropHost(server, app, dir);
        await app.StartAsync().ConfigureAwait(false);
        await server.WaitForListenAsync().ConfigureAwait(false);
        return host;
    }

    /// <inheritdoc/>
    public async ValueTask DisposeAsync()
    {
        using (var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10)))
        {
            await App.StopAsync(cts.Token).ConfigureAwait(false);
        }

        await App.DisposeAsync().ConfigureAwait(false);
        await Server.DisposeAsync().ConfigureAwait(false);
        try
        {
            _dir.Delete(recursive: true);
        }
        catch (IOException)
        {
        }
    }
}
