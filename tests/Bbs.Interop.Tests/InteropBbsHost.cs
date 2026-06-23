using Bbs.Core;
using Bbs.Host;
using Bbs.Host.Forwarding;
using Microsoft.Extensions.Logging.Abstractions;

namespace Bbs.Interop.Tests;

/// <summary>
/// Our BBS composed for the interop lane: a temp-dir store plus the real W2/W5 components
/// (routing, inbound receiver, the FBB pump). The default identity is the PDNBBS one the
/// LinBPQ oracle's linmail.cfg partner entry expects; the (ownCall, hRoute) overload lets
/// the real-F6FBB lane run as Q0PDN. Real time throughout — the peer is live.
/// </summary>
internal sealed class InteropBbsHost : IDisposable
{
    /// <summary>Default base BBS callsign (R: lines / BIDs carry the base call).</summary>
    public const string OwnCall = "PDNBBS";

    /// <summary>The default AX.25 identity the oracle dials / we dial from.</summary>
    public const string AxCall = "PDNBBS-1";

    /// <summary>Our default hierarchical route, matching the oracle's BBSHA for us.</summary>
    public const string HRoute = "#23.GBR.EURO";

    /// <summary>Our SID version field.</summary>
    public const string Version = "0.1.0";

    private readonly DirectoryInfo _dir;

    public InteropBbsHost()
        : this(OwnCall, HRoute)
    {
    }

    /// <summary>
    /// Composes the BBS under an arbitrary base callsign + hierarchical route — used by the
    /// real-F6FBB lane to run as Q0PDN (vs the LinBPQ oracle's expected PDNBBS).
    /// </summary>
    public InteropBbsHost(string ownCall, string hRoute)
    {
        Call = ownCall;
        _dir = Directory.CreateTempSubdirectory("bbs-interop-test-");
        Store = BbsStore.Open(Path.Combine(_dir.FullName, "bbs.db"), ownCall, TimeProvider.System);
        Identity = new BbsIdentity { Callsign = ownCall, HRoute = hRoute, SoftwareVersion = "PDN" + Version };
        var engine = new RoutingEngine(ownCall, hRoute);
        Routing = new RoutingService(Store, engine, NullLogger<RoutingService>.Instance);
        var sevenPlus = new SevenPlusAssembler(Store, NullLogger<SevenPlusAssembler>.Instance);
        var whitePages = new WhitePagesConsumer(Store, NullLogger<WhitePagesConsumer>.Instance);
        Receiver = new InboundMessageReceiver(
            Store, Routing, engine, sevenPlus, whitePages, ownCall, TimeProvider.System, NullLogger<InboundMessageReceiver>.Instance);
        Runner = new Ax25FbbSessionRunner(Store, Receiver, Identity, Version, TimeProvider.System);
    }

    /// <summary>The base callsign this host composed under.</summary>
    public string Call { get; }

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
