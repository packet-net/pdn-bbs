using System.Net;

namespace Bbs.Interop.Tests;

/// <summary>
/// The external F6FBB interop rig (a QEMU/AX.25/F6FBB VM, repo github.com/M0LTE/f6fbb-interop) that
/// the real-F6FBB tests need. It is a HEAVY, out-of-band dependency: not built or booted by the test
/// run, never part of the PR gate. These tests are tagged <c>[Trait("Category","InteropF6fbb")]</c>
/// (excluded from the fast CI lane) and each one calls <see cref="RequireAsync"/>, so when the rig
/// is not reachable they <b>skip</b> (not fail) — a developer running the whole suite locally without
/// the VM just sees skips.
/// <para>
/// Bring it up: clone github.com/M0LTE/f6fbb-interop and <c>make run</c> (NET=tap; host 192.168.76.1
/// ↔ VM 192.168.76.2:10093). The endpoint is overridable via <c>PDNBBS_F6FBB_HOST</c> /
/// <c>PDNBBS_F6FBB_PORT</c> (mirrors the LinBPQ oracle's <c>PDNBBS_ORACLE_*</c>). Set
/// <c>PDNBBS_F6FBB_REQUIRED=1</c> to turn the skip into a hard failure (the on-demand CI lane
/// does this — it booted the rig, so a miss is a real fault). See <c>docs/interop-f6fbb.md</c>.
/// </para>
/// </summary>
internal static class F6fbbRig
{
    /// <summary>The rig's AXUDP endpoint (VM side). Override with PDNBBS_F6FBB_HOST / PDNBBS_F6FBB_PORT.</summary>
    public static readonly IPEndPoint Endpoint = ResolveEndpoint();

    // Probed exactly once for the whole test run (the result is shared across the serialised F6fbb lane).
    private static readonly Lazy<Task<(bool Ok, string Reason)>> Reachable = new(ProbeAsync);

    /// <summary>
    /// Skips the calling test unless the rig answers an AXUDP connect. Call it as the first line of a
    /// <c>[SkippableFact]</c>. The probe runs once and is cached.
    /// </summary>
    public static async Task RequireAsync()
    {
        (bool ok, string reason) = await Reachable.Value.ConfigureAwait(false);
        if (ok)
        {
            return;
        }

        // The dedicated on-demand lane (interop-f6fbb.yml) sets PDNBBS_F6FBB_REQUIRED=1: it has just
        // booted the rig, so an unreachable rig is a real failure — a misconfigured tap/bridge must not
        // masquerade as "all green" by skipping. Locally and in the fast `test` lane the rig is
        // legitimately absent, so there we skip.
        if (Environment.GetEnvironmentVariable("PDNBBS_F6FBB_REQUIRED") == "1")
        {
            Assert.Fail(reason);
        }

        Skip.If(true, reason);
    }

    private static IPEndPoint ResolveEndpoint()
    {
        string host = Environment.GetEnvironmentVariable("PDNBBS_F6FBB_HOST") is { Length: > 0 } h ? h : "192.168.76.2";
        int port = int.TryParse(Environment.GetEnvironmentVariable("PDNBBS_F6FBB_PORT"), out int p) ? p : 10093;
        return new IPEndPoint(IPAddress.Parse(host), port);
    }

    private static async Task<(bool, string)> ProbeAsync()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        try
        {
            // A completed SABM/UA to Q0FBB-1 proves the rig is up; release the port before the tests bind it.
            await using Ax25Endpoint endpoint =
                await Ax25Endpoint.AttachAxudpAsync(Endpoint, Endpoint.Port, "Q0PDN-1", cts.Token).ConfigureAwait(false);
            Ax25ByteSession link = await endpoint.ConnectAsync("Q0FBB-1", cts.Token).ConfigureAwait(false);
            await link.CloseAsync(cts.Token).ConfigureAwait(false);
            return (true, string.Empty);
        }
        catch (Exception ex)
        {
            return (false,
                $"F6FBB rig not reachable at {Endpoint} ({ex.GetType().Name}). This is an on-demand, " +
                "out-of-band dependency — bring it up: clone github.com/M0LTE/f6fbb-interop and `make run` " +
                "(NET=tap), or set PDNBBS_F6FBB_HOST / PDNBBS_F6FBB_PORT. See docs/interop-f6fbb.md.");
        }
    }
}
