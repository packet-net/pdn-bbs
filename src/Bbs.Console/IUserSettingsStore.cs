namespace Bbs.Console;

/// <summary>
/// Per-user console preferences. <c>Bbs.Core</c>'s user record carries name/home-BBS but not
/// the console-only state BPQMail also persists per user — expert mode (X, compat spec §1.3),
/// page length (OP, §1.7) and the QTH/ZIP fields (Q/Z, §1.3) — so the console owns this
/// sidecar record. Null members mean "never set; use the config default".
/// </summary>
public sealed record UserSettings
{
    /// <summary>Expert mode (X toggle, compat spec §1.3), or null for the config default.</summary>
    public bool? Expert { get; init; }

    /// <summary>Page length (OP n, compat spec §1.7; 0 = off), or null for the config default.</summary>
    public int? PageLength { get; init; }

    /// <summary>QTH (Q command, compat spec §1.3), ≤30 chars (§5 parser caps).</summary>
    public string? Qth { get; init; }

    /// <summary>ZIP/postcode (Z command, compat spec §1.3), ≤8 chars (§5 parser caps).</summary>
    public string? Zip { get; init; }
}

/// <summary>
/// Persistence seam for <see cref="UserSettings"/>, keyed by base (SSID-stripped) callsign.
/// The Host implements this over real storage; tests (and Hosts that don't care) can use
/// <see cref="InMemoryUserSettingsStore"/>. Implementations must be thread-safe.
/// </summary>
public interface IUserSettingsStore
{
    /// <summary>Fetches a user's settings; an all-null record when never stored.</summary>
    UserSettings Load(string callsign);

    /// <summary>Stores a user's settings, replacing the previous record.</summary>
    void Save(string callsign, UserSettings settings);
}

/// <summary>Dictionary-backed <see cref="IUserSettingsStore"/> (persistence = object lifetime).</summary>
public sealed class InMemoryUserSettingsStore : IUserSettingsStore
{
    private readonly Dictionary<string, UserSettings> _settings = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _gate = new();

    /// <inheritdoc/>
    public UserSettings Load(string callsign)
    {
        ArgumentNullException.ThrowIfNull(callsign);
        lock (_gate)
        {
            return _settings.TryGetValue(Key(callsign), out UserSettings? settings) ? settings : new UserSettings();
        }
    }

    /// <inheritdoc/>
    public void Save(string callsign, UserSettings settings)
    {
        ArgumentNullException.ThrowIfNull(callsign);
        ArgumentNullException.ThrowIfNull(settings);
        lock (_gate)
        {
            _settings[Key(callsign)] = settings;
        }
    }

    private static string Key(string callsign) => Bbs.Core.Callsigns.StripSsid(Bbs.Core.Callsigns.Normalize(callsign));
}
