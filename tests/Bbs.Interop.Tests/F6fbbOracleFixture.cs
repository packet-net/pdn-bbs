using System.Diagnostics;
using System.Net.Sockets;

namespace Bbs.Interop.Tests;

/// <summary>
/// The xunit collection the F6FBB interop classes join: shares one <see cref="F6fbbOracleFixture"/>
/// and serialises the classes so only one forwarding session uses the LinFBB oracle (a stateful
/// singleton — one partner identity, one TCP forward port) at a time. Kept separate from
/// <see cref="OracleCollection"/> (LinBPQ): the two oracles are different containers on different
/// ports and are brought up by different CI jobs.
/// </summary>
[CollectionDefinition(Name)]
public class F6fbbOracleCollection : ICollectionFixture<F6fbbOracleFixture>
{
    /// <summary>The collection name.</summary>
    public const string Name = "F6fbbOracle";
}

/// <summary>
/// Asserts the LinFBB (F6FBB) oracle stack (docker/compose.f6fbb.yml) is up and SERVING on its
/// TCP/telnet forward port, then exposes the on-disk message-store inspection helpers the interop
/// assertions need (the store is the primary interop target, mirroring the LinBPQ
/// <see cref="OracleFixture"/>). Like that fixture it does NOT bring the stack up — CI does
/// (<c>up -d --wait</c> gating on the healthcheck) and local runs do it by hand.
/// </summary>
/// <remarks>
/// Endpoints/identity default to docker/compose.f6fbb.yml and can be re-pointed at a private
/// instance via <c>PDNBBS_F6FBB_TCP_PORT</c>, <c>PDNBBS_F6FBB_CONTAINER</c> and
/// <c>PDNBBS_F6FBB_MAILDIR</c>. The oracle is a stateful singleton, so a second developer/agent on
/// the same box should run their own stack and point these at it.
/// </remarks>
public sealed class F6fbbOracleFixture
{
    /// <summary>The host the published ports are bound on.</summary>
    public const string Host = "127.0.0.1";

    /// <summary>LinFBB's TCP/telnet forward port (the connect target for an outbound forward to F6FBB).</summary>
    public static readonly int TcpPort = EnvInt("PDNBBS_F6FBB_TCP_PORT", 8311);

    /// <summary>The LinFBB container name (compose.f6fbb.yml) — docker exec target for store assertions.</summary>
    public static readonly string Container =
        Environment.GetEnvironmentVariable("PDNBBS_F6FBB_CONTAINER") is { Length: > 0 } c ? c : "pdnbbs-f6fbb";

    /// <summary>The LinFBB on-disk mail store directory inside the container (where received message text lands).</summary>
    public static readonly string MailDir =
        Environment.GetEnvironmentVariable("PDNBBS_F6FBB_MAILDIR") is { Length: > 0 } d ? d : "/opt/fbb/var/ax25/fbb";

    /// <summary>The oracle BBS callsign (F6FBB itself).</summary>
    public const string OracleBbsCall = "F6FBB";

    /// <summary>Our forwarding identity as the LinFBB oracle knows it (passwd.sys entry in docker/f6fbb).</summary>
    public const string PartnerLogin = "PDNBBS";

    /// <summary>The forwarding password the LinFBB oracle expects from us (docker/f6fbb/passwd.sys).</summary>
    public const string PartnerPassword = "PDNBBSFWD";

    private static int EnvInt(string name, int fallback) =>
        int.TryParse(Environment.GetEnvironmentVariable(name), out int value) ? value : fallback;

    /// <summary>Creates the fixture, probing the stack.</summary>
    public F6fbbOracleFixture()
    {
        ProbeAsync().GetAwaiter().GetResult();
    }

    private static async Task ProbeAsync()
    {
        // Gate on the forward port actually accepting connections (the compose healthcheck gates
        // `up -d --wait`, but xfbbd can answer the socket a beat after the container is healthy).
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(90);
        while (true)
        {
            try
            {
                using var tcp = new TcpClient();
                await tcp.ConnectAsync(Host, TcpPort).WaitAsync(TimeSpan.FromSeconds(3)).ConfigureAwait(false);
                return;
            }
            catch (Exception ex) when (ex is SocketException or TimeoutException or IOException)
            {
                if (DateTime.UtcNow > deadline)
                {
                    throw new InvalidOperationException(
                        $"The LinFBB (F6FBB) oracle never accepted a TCP connection on {Host}:{TcpPort}. " +
                        "Bring the stack up first (docker compose -f docker/compose.f6fbb.yml up -d --wait).",
                        ex);
                }

                await Task.Delay(2000).ConfigureAwait(false);
            }
        }
    }

    /// <summary>
    /// Runs a shell command inside the oracle container and returns stdout (null on non-zero exit) —
    /// the on-disk assertion path (the store bind mount is container-owned, so inspect via docker exec).
    /// </summary>
    public static async Task<string?> ShellAsync(string shellCommand, CancellationToken cancellationToken)
    {
        var psi = new ProcessStartInfo
        {
            FileName = "docker",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        psi.ArgumentList.Add("exec");
        psi.ArgumentList.Add(Container);
        psi.ArgumentList.Add("sh");
        psi.ArgumentList.Add("-c");
        psi.ArgumentList.Add(shellCommand);

        using var process = Process.Start(psi)
            ?? throw new InvalidOperationException("could not start docker exec");
        string stdout = await process.StandardOutput.ReadToEndAsync(cancellationToken).ConfigureAwait(false);
        await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
        return process.ExitCode == 0 ? stdout : null;
    }

    /// <summary>
    /// Polls the oracle's mail store until a file containing <paramref name="needle"/> appears and
    /// returns that file's text. LinFBB writes the received message body into its mail area shortly
    /// after the FBB session; the search is recursive + fixed-string so the layout/nonce can't trip it.
    /// </summary>
    public static async Task<string> WaitForStoredMessageAsync(
        string needle, TimeSpan timeout, CancellationToken cancellationToken)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (true)
        {
            string? path = await ShellAsync(
                $"grep -rlF '{needle}' '{MailDir}' 2>/dev/null | head -1", cancellationToken).ConfigureAwait(false);
            if (!string.IsNullOrWhiteSpace(path))
            {
                string? content = await ShellAsync($"cat '{path.Trim()}'", cancellationToken).ConfigureAwait(false);
                if (content is not null)
                {
                    return content;
                }
            }

            if (DateTime.UtcNow > deadline)
            {
                string? listing = await ShellAsync(
                    $"ls -laR '{MailDir}' 2>/dev/null | tail -60; tail -40 '{MailDir}'/../fbb*.log 2>/dev/null",
                    cancellationToken).ConfigureAwait(false);
                throw new TimeoutException(
                    $"'{needle}' never appeared in the LinFBB store ({MailDir}). Oracle state:\n{listing}");
            }

            await Task.Delay(500, cancellationToken).ConfigureAwait(false);
        }
    }
}
