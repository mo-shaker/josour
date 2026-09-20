using System.Text.Json;
using System.Text.Json.Nodes;

namespace Josour.Browser;

/// <summary>Setting exit_type=Normal and exited_cleanly=true in Default\Preferences so "Chrome did not shut down correctly" does not appear on the next launch.</summary>
public static class ProfilePreferences
{
    public static string PreferencesPath(string profileDirectory) => Path.Combine(profileDirectory, "Default", "Preferences");

    /// <summary>Returns true if the file existed and was modified. It does not throw; a corrupt or missing file = false.</summary>
    public static bool SetExitTypeNormal(string profileDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(profileDirectory);
        var path = PreferencesPath(profileDirectory);
        if (!File.Exists(path)) return false;
        try
        {
            var root = JsonNode.Parse(File.ReadAllText(path)) as JsonObject;
            if (root is null) return false;
            if (root["profile"] is not JsonObject profile)
            {
                profile = new JsonObject();
                root["profile"] = profile;
            }
            var alreadyNormal = (string?)profile["exit_type"] == "Normal" && (bool?)profile["exited_cleanly"] == true;
            if (alreadyNormal) return true;
            profile["exit_type"] = "Normal";
            profile["exited_cleanly"] = true;
            File.WriteAllText(path, root.ToJsonString(new JsonSerializerOptions { WriteIndented = false }));
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }
}
