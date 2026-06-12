using Microsoft.Data.Sqlite;

namespace Bbs.Core.Tests;

/// <summary>
/// The BBS mail-password subsystem (schema v4): the Argon2id <see cref="PasswordHasher"/> and the
/// <c>mail_auth</c> store methods that back IMAP / external-mail-client login.
/// </summary>
public sealed class MailAuthTests : IDisposable
{
    private readonly TestStore _ts = new();

    public void Dispose() => _ts.Dispose();

    // ------------------------------------------------ PasswordHasher

    [Fact]
    public void Hasher_RoundTrips_AndRejectsWrongPassword()
    {
        string hash = PasswordHasher.Hash("correct horse battery staple");

        Assert.True(PasswordHasher.Verify("correct horse battery staple", hash));
        Assert.False(PasswordHasher.Verify("Correct Horse Battery Staple", hash)); // case-sensitive
        Assert.False(PasswordHasher.Verify("wrong", hash));
    }

    [Fact]
    public void Hasher_SamePassword_ProducesDistinctSaltedHashes()
    {
        string a = PasswordHasher.Hash("hunter2hunter2");
        string b = PasswordHasher.Hash("hunter2hunter2");

        Assert.NotEqual(a, b);                           // per-call CSPRNG salt
        Assert.True(PasswordHasher.Verify("hunter2hunter2", a));
        Assert.True(PasswordHasher.Verify("hunter2hunter2", b));
    }

    [Fact]
    public void Hasher_EncodesPhcArgon2idFormat()
    {
        string hash = PasswordHasher.Hash("a-good-password");
        Assert.StartsWith("$argon2id$v=19$m=19456,t=2,p=1$", hash, StringComparison.Ordinal);
        Assert.Equal(6, hash.Split('$').Length); // $argon2id$v=..$params$salt$hash → 6 segments
    }

    [Theory]
    [InlineData("")]
    [InlineData("not-a-real-hash")]
    [InlineData("$argon2id$v=19$m=19456,t=2,p=1$only-five-fields")]
    [InlineData("$argon2d$v=19$m=19456,t=2,p=1$c2FsdA$aGFzaA")] // wrong variant
    public void Hasher_Verify_ReturnsFalse_OnMalformedHash(string malformed)
    {
        Assert.False(PasswordHasher.Verify("whatever", malformed));
    }

    // ------------------------------------------------ store: set / verify

    [Fact]
    public void SetThenVerify_Succeeds_AndWrongPasswordFails()
    {
        _ts.Store.SetMailPassword("M0LTE", "s3cret-passphrase");

        Assert.True(_ts.Store.VerifyMailPassword("M0LTE", "s3cret-passphrase"));
        Assert.False(_ts.Store.VerifyMailPassword("M0LTE", "s3cret-passphras"));   // wrong
        Assert.True(_ts.Store.HasMailPassword("M0LTE"));
    }

    [Fact]
    public void Verify_NoPasswordSet_ReturnsFalse()
    {
        Assert.False(_ts.Store.VerifyMailPassword("G4XYZ", "anything-at-all"));
        Assert.False(_ts.Store.HasMailPassword("G4XYZ"));
    }

    [Fact]
    public void MailPassword_IsCaseInsensitive_AndSsidStripped()
    {
        // Set against the base call in lower case; verify against an SSID'd, upper-case form — one
        // mailbox, one password (M0LTE and M0LTE-7 are the same operator's mailbox).
        _ts.Store.SetMailPassword("m0lte", "shared-mailbox-pw");

        Assert.True(_ts.Store.VerifyMailPassword("M0LTE", "shared-mailbox-pw"));
        Assert.True(_ts.Store.VerifyMailPassword("M0LTE-7", "shared-mailbox-pw"));
        Assert.True(_ts.Store.HasMailPassword("M0LTE-7"));
    }

    [Fact]
    public void SetMailPassword_Replaces_PreviousPassword()
    {
        _ts.Store.SetMailPassword("M0LTE", "first-password");
        _ts.Store.SetMailPassword("M0LTE", "second-password");

        Assert.False(_ts.Store.VerifyMailPassword("M0LTE", "first-password"));
        Assert.True(_ts.Store.VerifyMailPassword("M0LTE", "second-password"));
    }

    [Fact]
    public void ClearMailPassword_RemovesIt()
    {
        _ts.Store.SetMailPassword("M0LTE", "to-be-cleared");
        Assert.True(_ts.Store.ClearMailPassword("M0LTE"));
        Assert.False(_ts.Store.HasMailPassword("M0LTE"));
        Assert.False(_ts.Store.VerifyMailPassword("M0LTE", "to-be-cleared"));

        Assert.False(_ts.Store.ClearMailPassword("M0LTE")); // already gone
    }

    [Theory]
    [InlineData("")]
    [InlineData("       ")]
    [InlineData("short")]    // < MinMailPasswordLength (8)
    [InlineData("  pad  ")]  // trims to 3
    public void SetMailPassword_RejectsTooShort(string weak)
    {
        Assert.Throws<ArgumentException>(() => _ts.Store.SetMailPassword("M0LTE", weak));
        Assert.False(_ts.Store.HasMailPassword("M0LTE")); // nothing stored on rejection
    }

    [Fact]
    public void SetMailPassword_RejectsUnusableCallsign()
    {
        Assert.Throws<ArgumentException>(() => _ts.Store.SetMailPassword("   ", "a-fine-password"));
    }

    [Fact]
    public void MailPassword_SurvivesReopen()
    {
        _ts.Store.SetMailPassword("M0LTE", "persisted-across-restart");
        _ts.Reopen();

        Assert.True(_ts.Store.VerifyMailPassword("M0LTE", "persisted-across-restart"));
    }

    // ------------------------------------------------ additive v4 migration

    [Fact]
    public void Migration_FromV3Database_AddsMailAuth_DataSurvives()
    {
        // A populated v3 db (the live lab schema once 7plus shipped) must upgrade additively: every
        // existing row intact and the new mail_auth table writable.
        string path = Path.Combine(Directory.CreateTempSubdirectory("bbs-migrate-v4-").FullName, "v3.db");
        long legacyNumber;
        using (BbsStore seed = BbsStore.Open(path, "GB7PDN", _ts.Time))
        {
            legacyNumber = seed.AddMessage(Drafts.Personal(from: "G4XYZ", to: "M0LTE", subject: "legacy v3")).Number;
        }

        DowngradeToV3(path);

        using BbsStore upgraded = BbsStore.Open(path, "GB7PDN", _ts.Time);
        Assert.Equal(4, upgraded.SchemaVersion);

        // The pre-existing message survived the upgrade.
        Assert.Equal("legacy v3", upgraded.GetMessage(legacyNumber)!.Subject);

        // The new mail_auth table is writable + queryable through the v4 code.
        upgraded.SetMailPassword("M0LTE", "post-v4-password");
        Assert.True(upgraded.VerifyMailPassword("M0LTE", "post-v4-password"));
    }

    /// <summary>Strips the v4 schema addition and resets the version stamp, leaving a genuine v3 db on disk.</summary>
    private static void DowngradeToV3(string path)
    {
        using var connection = new SqliteConnection($"Data Source={path};Mode=ReadWrite;Pooling=False");
        connection.Open();
        using (var drop = connection.CreateCommand())
        {
            drop.CommandText = "DROP TABLE IF EXISTS mail_auth;";
            drop.ExecuteNonQuery();
        }

        using var stamp = connection.CreateCommand();
        stamp.CommandText = "UPDATE meta SET value='3' WHERE key='schema_version';";
        stamp.ExecuteNonQuery();
    }
}
