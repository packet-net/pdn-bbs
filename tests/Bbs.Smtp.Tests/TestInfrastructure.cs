using Bbs.Core;
using Microsoft.Extensions.Time.Testing;

namespace Bbs.Smtp.Tests;

/// <summary>
/// One <see cref="BbsStore"/> in its own temp directory with a fake clock — mirroring the IMAP test
/// suite's <c>TestStore</c> so the SMTP tests seed the same way. Dispose removes the directory.
/// </summary>
internal sealed class TestStore : IDisposable
{
    public const string BbsCall = "GB7PDN";

    private readonly DirectoryInfo _dir;

    public TestStore(string bbsCall = BbsCall)
    {
        _dir = Directory.CreateTempSubdirectory("bbs-smtp-test-");
        Time = new FakeTimeProvider(new DateTimeOffset(2026, 6, 11, 12, 0, 0, TimeSpan.Zero));
        Store = BbsStore.Open(Path.Combine(_dir.FullName, "bbs.db"), bbsCall, Time);
    }

    public FakeTimeProvider Time { get; }

    public BbsStore Store { get; }

    public void Dispose()
    {
        Store.Dispose();
        _dir.Delete(recursive: true);
    }
}
