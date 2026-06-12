using Bbs.Console;
using Bbs.Host.Sessions;

namespace Bbs.Host.Tests;

/// <summary>
/// <see cref="JsonUserSettingsStore"/> persistence — in particular that the plain-language
/// <see cref="UserSettings.InterfaceMode"/> survives the JSON round-trip (write, reopen, read),
/// so a user's classic/plain choice sticks across a host restart (the plain-language mandate).
/// </summary>
public sealed class JsonUserSettingsStoreTests : IDisposable
{
    private readonly DirectoryInfo _dir = Directory.CreateTempSubdirectory("bbs-settings-test-");

    public void Dispose() => _dir.Delete(recursive: true);

    private string Path() => System.IO.Path.Combine(_dir.FullName, "user-settings.json");

    [Fact]
    public void InterfaceMode_RoundTripsThroughTheFile()
    {
        string path = Path();
        new JsonUserSettingsStore(path).Save("M0LTE", new UserSettings { InterfaceMode = InterfaceMode.Classic });

        // A fresh store re-reads the file from disk: the choice persists across a restart.
        var reopened = new JsonUserSettingsStore(path);
        Assert.Equal(InterfaceMode.Classic, reopened.Load("M0LTE").InterfaceMode);
    }

    [Fact]
    public void PlainInterfaceMode_RoundTrips()
    {
        string path = Path();
        new JsonUserSettingsStore(path).Save("M0LTE", new UserSettings { InterfaceMode = InterfaceMode.Plain });
        Assert.Equal(InterfaceMode.Plain, new JsonUserSettingsStore(path).Load("M0LTE").InterfaceMode);
    }

    [Fact]
    public void NeverSetInterfaceMode_IsNull_SoTheConfigDefaultApplies()
    {
        string path = Path();
        new JsonUserSettingsStore(path).Save("M0LTE", new UserSettings { Qth = "Ipswich" });
        UserSettings loaded = new JsonUserSettingsStore(path).Load("M0LTE");
        Assert.Null(loaded.InterfaceMode);
        Assert.Equal("Ipswich", loaded.Qth);
    }

    [Fact]
    public void InterfaceMode_CoexistsWithTheOtherPreferences()
    {
        string path = Path();
        new JsonUserSettingsStore(path).Save("M0LTE", new UserSettings
        {
            Expert = true,
            PageLength = 20,
            Qth = "Reading",
            Zip = "RG1",
            InterfaceMode = InterfaceMode.Classic,
        });

        UserSettings loaded = new JsonUserSettingsStore(path).Load("M0LTE");
        Assert.True(loaded.Expert);
        Assert.Equal(20, loaded.PageLength);
        Assert.Equal("Reading", loaded.Qth);
        Assert.Equal("RG1", loaded.Zip);
        Assert.Equal(InterfaceMode.Classic, loaded.InterfaceMode);
    }
}
