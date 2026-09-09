using System.Text.Json;
using System.Text.Json.Nodes;

namespace Josour.Browser;

/// <summary>ضبط exit_type=Normal وexited_cleanly=true في Default\Preferences حتى لا يظهر «لم يُغلق Chrome بشكل صحيح» عند التشغيل التالي.</summary>
public static class ProfilePreferences
{
    public static string PreferencesPath(string profileDirectory) => Path.Combine(profileDirectory, "Default", "Preferences");

    /// <summary>يعيد true إن كان الملف موجودًا وعُدّل. لا يرمي؛ الملف التالف أو غير الموجود = false.</summary>
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
