using System.Diagnostics;
using System.Net.Sockets;

namespace Bbs.Interop.Tests;

/// <summary>
/// The xunit collection both interop test classes join: shares one <see cref="OracleFixture"/>
/// and — critically — serialises the classes, so only one FBB session uses the simulated RF
/// channel (and the PDNBBS-1 identity) at a time.
/// </summary>
[CollectionDefinition(Name)]
public class OracleCollection : ICollectionFixture<OracleFixture>
{
    /// <summary>The collection name.</summary>
    public const string Name = "Oracle";
}

/// <summary>
/// Asserts the LinBPQ+BPQMail oracle stack (docker/compose.oracle.yml) is up and reachable,
/// following the packet.net interop convention: the fixture does NOT bring the stack up —
/// CI does (with <c>up -d --wait</c> gating on the healthchecks) and local runs do it by
/// hand. It probes the two ports the tests use, with a deadline generous enough to absorb
/// a stack that is healthy-but-settling.
/// </summary>
public sealed class OracleFixture
{
    /// <summary>netsim node a — our KISS-TCP attach point (docker README port map).</summary>
    public const string KissHost = "127.0.0.1";

    /// <summary>netsim node a KISS-TCP port.</summary>
    public const int KissPort = 8200;

    /// <summary>LinBPQ telnet (node prompt → BBS).</summary>
    public const int TelnetPort = 8210;

    /// <summary>The LinBPQ container name (compose.oracle.yml) — docker exec target.</summary>
    public const string OracleContainer = "pdnbbs-gb7bpq";

    /// <summary>The oracle node callsign.</summary>
    public const string OracleNodeCall = "GB7BPQ";

    /// <summary>The oracle BBS application callsign (answers direct AX.25 connects).</summary>
    public const string OracleBbsCall = "GB7BPQ-1";

    /// <summary>Creates the fixture, probing the stack.</summary>
    public OracleFixture()
    {
        ProbeAsync().GetAwaiter().GetResult();
    }

    private static async Task ProbeAsync()
    {
        // The compose healthchecks gate `up -d --wait`, so a CI stack is already
        // healthy; the retry window here only absorbs listener-settle beats.
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(30);
        while (true)
        {
            try
            {
                using var kiss = new TcpClient();
                await kiss.ConnectAsync(KissHost, KissPort).WaitAsync(TimeSpan.FromSeconds(3)).ConfigureAwait(false);
                using var telnet = new TcpClient();
                await telnet.ConnectAsync(KissHost, TelnetPort).WaitAsync(TimeSpan.FromSeconds(3)).ConfigureAwait(false);
                return;
            }
            catch (Exception ex) when (ex is SocketException or TimeoutException or IOException)
            {
                if (DateTime.UtcNow > deadline)
                {
                    throw new InvalidOperationException(
                        "The LinBPQ oracle stack is not reachable on " +
                        $"{KissHost}:{KissPort} (netsim KISS) / :{TelnetPort} (telnet). " +
                        "Bring it up first: docker compose -f docker/compose.oracle.yml up -d --wait",
                        ex);
                }

                await Task.Delay(500).ConfigureAwait(false);
            }
        }
    }

    /// <summary>
    /// Runs a shell command inside the oracle container (the smoke.sh on-disk assertion
    /// path: the state bind mount is root-owned, so inspect it via docker exec rather
    /// than host reads). Returns stdout; a non-zero exit returns null.
    /// </summary>
    public static async Task<string?> OracleShellAsync(string shellCommand, CancellationToken cancellationToken)
    {
        var psi = new ProcessStartInfo
        {
            FileName = "docker",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        psi.ArgumentList.Add("exec");
        psi.ArgumentList.Add(OracleContainer);
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
    /// Polls the oracle's on-disk message store (<c>/data/Mail/m_*.mes</c> — the primary
    /// interop assertion target, compat spec §7.4) until a file containing
    /// <paramref name="needle"/> appears, and returns that file's full text. Store writes
    /// lag the FBB session by up to a few seconds — every wait is a poll-with-deadline.
    /// </summary>
    public static async Task<string> WaitForMailFileAsync(
        string needle, TimeSpan timeout, CancellationToken cancellationToken)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (true)
        {
            // grep -l writes the matching path; fixed-string match so the nonce
            // can never be misread as a pattern.
            string? path = await OracleShellAsync(
                $"grep -lF '{needle}' /data/Mail/m_*.mes 2>/dev/null | head -1",
                cancellationToken).ConfigureAwait(false);
            if (!string.IsNullOrWhiteSpace(path))
            {
                string? content = await OracleShellAsync(
                    $"cat '{path.Trim()}'", cancellationToken).ConfigureAwait(false);
                if (content is not null)
                {
                    return content;
                }
            }

            if (DateTime.UtcNow > deadline)
            {
                string? listing = await OracleShellAsync(
                    "ls -la /data/Mail/ 2>/dev/null; tail -40 /data/logs/log_*_BBS.txt 2>/dev/null",
                    cancellationToken).ConfigureAwait(false);
                throw new TimeoutException(
                    $"'{needle}' never appeared in the oracle store (/data/Mail/m_*.mes). " +
                    $"Oracle state:\n{listing}");
            }

            await Task.Delay(500, cancellationToken).ConfigureAwait(false);
        }
    }
}
