using Bbs.Core;
using Bbs.Host;
using Bbs.Host.Forwarding;
using Microsoft.Extensions.Logging.Abstractions;

namespace Bbs.Interop.Tests;

/// <summary>
/// Our BBS composed for the interop lane: a temp-dir store plus the real W2/W5 components
/// (routing, inbound receiver, the FBB pump) under the PDNBBS identity the oracle's
/// linmail.cfg partner entry expects (<c>BBSUsers.PDNBBS-1</c> F_BBS,
/// <c>ATCalls = PDNBBS|PDNBBS-1</c>, <c>BBSHA = PDNBBS.#23.GBR.EURO</c>). Real time
/// throughout — the oracle is live.
/// </summary>
internal sealed class InteropBbsHost : IDisposable
{
    /// <summary>Our base BBS callsign (R: lines / BIDs carry the base call).</summary>
    public const string OwnCall = "PDNBBS";

    /// <summary>The AX.25 identity the oracle dials / we dial from (BBS-on-SSID--1).</summary>
    public const string AxCall = "PDNBBS-1";

    /// <summary>Our hierarchical route, matching the oracle's BBSHA for us.</summary>
    public const string HRoute = "#23.GBR.EURO";

    /// <summary>Our SID version field.</summary>
    public const string Version = "0.1.0";

    private readonly DirectoryInfo _dir;

    public InteropBbsHost()
    {
        _dir = Directory.CreateTempSubdirectory("bbs-interop-test-");
        Store = BbsStore.Open(Path.Combine(_dir.FullName, "bbs.db"), OwnCall, TimeProvider.System);
        Identity = new BbsIdentity { Callsign = OwnCall, HRoute = HRoute, SoftwareVersion = "PDN" + Version };
        var engine = new RoutingEngine(OwnCall, HRoute);
        Routing = new RoutingService(Store, engine, NullLogger<RoutingService>.Instance);
        var sevenPlus = new SevenPlusAssembler(Store, NullLogger<SevenPlusAssembler>.Instance);
        var whitePages = new WhitePagesConsumer(Store, NullLogger<WhitePagesConsumer>.Instance);
        Receiver = new InboundMessageReceiver(
            Store, Routing, engine, sevenPlus, whitePages, OwnCall, TimeProvider.System, NullLogger<InboundMessageReceiver>.Instance);
        Runner = new Ax25FbbSessionRunner(Store, Receiver, Identity, Version, TimeProvider.System);
    }

    public BbsStore Store { get; }

    public BbsIdentity Identity { get; }

    public RoutingService Routing { get; }

    public InboundMessageReceiver Receiver { get; }

    public Ax25FbbSessionRunner Runner { get; }

    /// <inheritdoc/>
    public void Dispose()
    {
        Store.Dispose();
        try
        {
            _dir.Delete(recursive: true);
        }
        catch (IOException)
        {
        }
    }
}
