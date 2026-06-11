using System.Text.Json;
using Bbs.Console;

namespace Bbs.Host.Sessions;

/// <summary>
/// File-backed <see cref="IUserSettingsStore"/>: one JSON document in the app state dir,
/// keyed by base callsign. Console preferences (X/OP/Q/Z) survive restarts. Writes are
/// whole-file (small data, atomic-enough via temp + move).
/// </summary>
public sealed class JsonUserSettingsStore : IUserSettingsStore
{
    private static readonly JsonSerializerOptions SerializerOptions = new() { WriteIndented = true };

    private readonly string _path;
    private readonly object _gate = new();
    private readonly Dictionary<string, UserSettings> _settings;

    /// <summary>Opens (or creates) the store at <paramref name="path"/>.</summary>
    public JsonUserSettingsStore(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        _path = path;
        _settings = LoadFile(path);
    }

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
            string tmp = _path + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(_settings, SerializerOptions));
            File.Move(tmp, _path, overwrite: true);
        }
    }

    private static Dictionary<string, UserSettings> LoadFile(string path)
    {
        if (!File.Exists(path))
        {
            return new Dictionary<string, UserSettings>(StringComparer.OrdinalIgnoreCase);
        }

        try
        {
            Dictionary<string, UserSettings>? parsed =
                JsonSerializer.Deserialize<Dictionary<string, UserSettings>>(File.ReadAllText(path));
            return parsed is null
                ? new Dictionary<string, UserSettings>(StringComparer.OrdinalIgnoreCase)
                : new Dictionary<string, UserSettings>(parsed, StringComparer.OrdinalIgnoreCase);
        }
        catch (JsonException)
        {
            // A corrupt sidecar must never stop the BBS; preferences reset.
            return new Dictionary<string, UserSettings>(StringComparer.OrdinalIgnoreCase);
        }
    }

    private static string Key(string callsign) =>
        Bbs.Core.Callsigns.StripSsid(Bbs.Core.Callsigns.Normalize(callsign));
}
