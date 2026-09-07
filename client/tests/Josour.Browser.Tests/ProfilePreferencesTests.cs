using System.Text.Json.Nodes;

namespace Josour.Browser.Tests;

public class ProfilePreferencesTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "rb-prefs-" + Guid.NewGuid().ToString("N"));

    public ProfilePreferencesTests() => Directory.CreateDirectory(Path.Combine(_dir, "Default"));

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch { }
    }

    private string PrefsPath => ProfilePreferences.PreferencesPath(_dir);

    [Fact]
    public void MissingFile_ReturnsFalse()
    {
        Assert.False(ProfilePreferences.SetExitTypeNormal(_dir));
        Assert.False(File.Exists(PrefsPath));
    }

    [Fact]
    public void CrashedExitType_IsRewrittenToNormal_KeepingOtherValues()
    {
        File.WriteAllText(PrefsPath, """{"profile":{"exit_type":"Crashed","exited_cleanly":false,"name":"Person 1"},"other":{"k":1}}""");
        Assert.True(ProfilePreferences.SetExitTypeNormal(_dir));
        var root = JsonNode.Parse(File.ReadAllText(PrefsPath))!.AsObject();
        Assert.Equal("Normal", (string?)root["profile"]!["exit_type"]);
        Assert.True((bool?)root["profile"]!["exited_cleanly"]);
        Assert.Equal("Person 1", (string?)root["profile"]!["name"]);
        Assert.Equal(1, (int?)root["other"]!["k"]);
    }

    [Fact]
    public void MissingProfileObject_IsCreated()
    {
        File.WriteAllText(PrefsPath, """{"browser":{}}""");
        Assert.True(ProfilePreferences.SetExitTypeNormal(_dir));
        var root = JsonNode.Parse(File.ReadAllText(PrefsPath))!.AsObject();
        Assert.Equal("Normal", (string?)root["profile"]!["exit_type"]);
    }

    [Fact]
    public void AlreadyNormal_ReturnsTrue_WithoutRewrite()
    {
        File.WriteAllText(PrefsPath, """{"profile":{"exit_type":"Normal","exited_cleanly":true}}""");
        var before = File.GetLastWriteTimeUtc(PrefsPath);
        Assert.True(ProfilePreferences.SetExitTypeNormal(_dir));
        Assert.Equal(before, File.GetLastWriteTimeUtc(PrefsPath));
    }

    [Fact]
    public void MalformedJson_ReturnsFalse_AndLeavesFile()
    {
        File.WriteAllText(PrefsPath, "{not json");
        Assert.False(ProfilePreferences.SetExitTypeNormal(_dir));
        Assert.Equal("{not json", File.ReadAllText(PrefsPath));
    }
}
