using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;

namespace Bbs.Host.Diagnostics;

/// <summary>
/// A live, in-memory set of per-category minimum-level overrides — the runtime log-level switch.
/// A singleton consulted at log time (see <see cref="DynamicLogLevelProvider"/>) so the sysop can
/// raise <c>Bbs.Host.*</c> to Debug/Trace without a restart and without editing <c>appsettings.json</c>
/// (read-only under <c>ProtectSystem=strict</c> in production). Default = empty ⇒ logging behaves
/// exactly as configured by appsettings/filters; an override only ever <b>lowers the threshold</b>
/// (raises verbosity) for a category prefix — it never silences below the configured level.
/// </summary>
/// <remarks>
/// Matching is by longest case-insensitive category prefix (the same most-specific-wins rule the
/// built-in <c>Logging:LogLevel</c> config uses), so an override on <c>"Bbs.Host"</c> covers every
/// <c>Bbs.Host.*</c> logger. Thread-safe: overrides are read on every log call from many threads and
/// mutated from the web handler.
/// </remarks>
public sealed class LogLevelOverrides
{
    private readonly ConcurrentDictionary<string, LogLevel> _overrides =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Sets (or replaces) the minimum level for a category prefix. A subsequent log call on any
    /// logger whose category starts with <paramref name="category"/> is enabled at this level or
    /// above, even if the configured filters would have dropped it.
    /// </summary>
    public void Set(string category, LogLevel level)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(category);
        _overrides[category.Trim()] = level;
    }

    /// <summary>Removes a single category override; returns whether one was present.</summary>
    public bool Clear(string category)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(category);
        return _overrides.TryRemove(category.Trim(), out _);
    }

    /// <summary>Removes every override (back to the default = configured behaviour).</summary>
    public void ClearAll() => _overrides.Clear();

    /// <summary>
    /// The override minimum level that applies to <paramref name="category"/>, or null when no
    /// override matches. The longest (most specific) matching prefix wins, mirroring the built-in
    /// config rule, so a broad <c>"Bbs.Host"</c> override and a narrow
    /// <c>"Bbs.Host.Forwarding.ForwardingScheduler"</c> override can coexist with the narrow one
    /// taking precedence for its category.
    /// </summary>
    public LogLevel? ResolveFor(string category)
    {
        ArgumentNullException.ThrowIfNull(category);
        if (_overrides.IsEmpty)
        {
            return null;
        }

        LogLevel? best = null;
        int bestLength = -1;
        foreach (KeyValuePair<string, LogLevel> entry in _overrides)
        {
            if (category.StartsWith(entry.Key, StringComparison.OrdinalIgnoreCase)
                && entry.Key.Length > bestLength)
            {
                best = entry.Value;
                bestLength = entry.Key.Length;
            }
        }

        return best;
    }

    /// <summary>A point-in-time snapshot of the active overrides (category → level), for the GET view.</summary>
    public IReadOnlyDictionary<string, LogLevel> Snapshot() =>
        new Dictionary<string, LogLevel>(_overrides, StringComparer.OrdinalIgnoreCase);
}
