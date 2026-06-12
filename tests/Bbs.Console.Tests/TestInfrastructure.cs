using System.Text;
using Bbs.Core;

namespace Bbs.Console.Tests;

/// <summary>Deterministic, manually-advanced clock for TimeProvider-driven code under test.</summary>
internal sealed class FakeTimeProvider : TimeProvider
{
    private DateTimeOffset _utcNow = new(2026, 6, 11, 12, 0, 0, TimeSpan.Zero);

    public override DateTimeOffset GetUtcNow() => _utcNow;

    public void Advance(TimeSpan delta) => _utcNow += delta;
}

/// <summary>
/// The scripted terminal fake: a fixed queue of input lines, all output captured. When the
/// script runs out the terminal reports a drop (ReadLineAsync → null), so a script that
/// doesn't end in B/BYE/NODE ends the session as <see cref="BbsSessionEndReason.Drop"/>.
/// </summary>
internal sealed class ScriptedTerminal : IBbsTerminal
{
    private readonly Queue<string> _input;
    private readonly StringBuilder _output = new();

    public ScriptedTerminal(string remoteCallsign, IEnumerable<string> lines)
    {
        RemoteCallsign = remoteCallsign;
        _input = new Queue<string>(lines);
    }

    public string RemoteCallsign { get; }

    public string Output => _output.ToString();

    /// <summary>Output split into lines: CR-discipline, with the prompt's CR LF folded in.</summary>
    public string[] OutputLines =>
        _output.ToString().Replace("\r\n", "\r", StringComparison.Ordinal).Split('\r');

    public ValueTask<string?> ReadLineAsync(CancellationToken cancellationToken) =>
        new(_input.Count > 0 ? _input.Dequeue() : null);

    public ValueTask WriteAsync(string text, CancellationToken cancellationToken)
    {
        _output.Append(text);
        return ValueTask.CompletedTask;
    }
}

/// <summary>
/// One store in its own temp directory plus the console config/settings a session needs.
/// Dispose removes the directory.
/// </summary>
internal sealed class SessionHarness : IDisposable
{
    public const string BbsCall = "GB7PDN";
    public const string SysopCall = "G4SYS";
    public const string PartnerCall = "GB7BPQ-1";

    private readonly DirectoryInfo _dir;

    /// <param name="defaultMode">
    /// The surface a never-set caller gets. Defaults to <see cref="InterfaceMode.Classic"/> so
    /// the existing 193-test classic suite (the "classic is kept whole" guarantee) keeps
    /// exercising the byte-exact W0RLI surface. The plain-surface tests pass
    /// <see cref="InterfaceMode.Plain"/> — the real production default.
    /// </param>
    public SessionHarness(InterfaceMode defaultMode = InterfaceMode.Classic)
    {
        _dir = Directory.CreateTempSubdirectory("bbs-console-test-");
        Time = new FakeTimeProvider();
        Store = BbsStore.Open(Path.Combine(_dir.FullName, "bbs.db"), BbsCall, Time);
        Settings = new InMemoryUserSettingsStore();
        Config = new BbsConsoleConfig
        {
            BbsCallsign = BbsCall,
            SysopCallsigns = [SysopCall],
            Version = "0.1.0",
            NodeVersion = "pdn 1.2.3",
            DefaultInterfaceMode = defaultMode,
        };
    }

    public FakeTimeProvider Time { get; }

    public BbsStore Store { get; }

    public InMemoryUserSettingsStore Settings { get; }

    public BbsConsoleConfig Config { get; set; }

    /// <summary>Pre-creates a user with a name (and optionally home BBS) so sessions skip the new-user flow.</summary>
    public void KnownUser(string call, string name = "Tom", string? homeBbs = "GB7PDN.#23.GBR.EURO")
    {
        Store.TouchLastLogin(call);
        Store.UpsertUser(Store.GetUser(call)! with { Name = name, HomeBbs = homeBbs });
    }

    /// <summary>Registers a partner record so the callsign is BBS-flagged (exact call incl. SSID).</summary>
    public void Partner(string call = PartnerCall)
    {
        Store.UpsertPartner(new Partner { Call = call });
    }

    /// <summary>Runs one full session over a scripted terminal; returns the end reason and the terminal.</summary>
    public async Task<(BbsSessionEndReason End, ScriptedTerminal Terminal)> RunAsync(string caller, params string[] lines)
    {
        var terminal = new ScriptedTerminal(caller, lines);
        BbsSessionEndReason end = await BbsConsoleSession.RunAsync(
            terminal, Store, Config, Time, Settings, CancellationToken.None);
        return (end, terminal);
    }

    public void Dispose()
    {
        Store.Dispose();
        _dir.Delete(recursive: true);
    }
}

/// <summary>Draft builders mirroring the Bbs.Core test conventions (seed stores via Core directly).</summary>
internal static class Drafts
{
    public static MessageDraft Personal(
        string from = "M0LTE",
        string to = "G8BPQ",
        string? at = null,
        string? bid = null,
        string subject = "test message",
        string body = "hello\r",
        string? receivedFrom = null,
        bool hold = false)
        => new()
        {
            Type = MessageType.Personal,
            From = from,
            Recipients = [to],
            At = at,
            Bid = bid,
            Subject = subject,
            Body = Encoding.Latin1.GetBytes(body),
            ReceivedFrom = receivedFrom,
            Hold = hold,
        };

    public static MessageDraft Bulletin(
        string from = "M0LTE",
        string to = "ALL",
        string? at = null,
        string? bid = null,
        string subject = "bulletin")
        => Personal(from, to, at, bid, subject) with { Type = MessageType.Bulletin };

    public static MessageDraft Traffic(
        string from = "K4CJX",
        string to = "32118",
        string? at = null,
        string? bid = null,
        string subject = "QTC 1")
        => Personal(from, to, at, bid, subject) with { Type = MessageType.Traffic };
}
