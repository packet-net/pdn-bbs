using Microsoft.Extensions.Logging;

namespace Bbs.Interop.Tests;

/// <summary>
/// Collects formatted log records at/above Debug into a list — used to surface the production
/// <c>FbbSessionRunner</c>'s Debug-level FBB wire logging (<c>FBB &gt; …</c> / <c>FBB &lt; …</c>)
/// in a test, so we exercise the real observability path rather than a test-only decorator.
/// </summary>
internal sealed class CapturingLogger<T>(List<string> sink) : ILogger<T>
{
    public IDisposable? BeginScope<TState>(TState state)
        where TState : notnull => null;

    public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Debug;

    public void Log<TState>(
        LogLevel logLevel, EventId eventId, TState state, Exception? exception,
        Func<TState, Exception?, string> formatter)
    {
        if (IsEnabled(logLevel))
        {
            sink.Add(formatter(state, exception));
        }
    }
}
