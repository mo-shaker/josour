using System.Runtime.Versioning;

namespace Josour.Browser.Registry;

public enum RegistryRoot { LocalMachine, CurrentUser }

/// <summary>قراءة السجل قابلة للحقن (اختبار محدد المتصفح وكاشف السياسات على أي نظام).</summary>
public interface IRegistryReader
{
    /// <summary>قيمة نصية (REG_SZ/REG_EXPAND_SZ). valueName فارغ = القيمة الافتراضية. null إن لم توجد.</summary>
    string? GetString(RegistryRoot root, string subKey, string? valueName);
    int? GetInt(RegistryRoot root, string subKey, string valueName);
    bool KeyExists(RegistryRoot root, string subKey);
}

/// <summary>لا سجل (غير Windows).</summary>
public sealed class NullRegistryReader : IRegistryReader
{
    public static readonly NullRegistryReader Instance = new();
    public string? GetString(RegistryRoot root, string subKey, string? valueName) => null;
    public int? GetInt(RegistryRoot root, string subKey, string valueName) => null;
    public bool KeyExists(RegistryRoot root, string subKey) => false;
}

/// <summary>WINDOWS-ONLY: Microsoft.Win32.Registry. على غير Windows يعيد null دائمًا.</summary>
public sealed class WindowsRegistryReader : IRegistryReader
{
    public static readonly WindowsRegistryReader Instance = new();

    public string? GetString(RegistryRoot root, string subKey, string? valueName)
    {
        if (!OperatingSystem.IsWindows()) return null;
        return Read(root, subKey, valueName) switch
        {
            string s => s,
            string[] lines => string.Join("\n", lines),
            null => null,
            var other => other.ToString(),
        };
    }

    public int? GetInt(RegistryRoot root, string subKey, string valueName)
    {
        if (!OperatingSystem.IsWindows()) return null;
        return Read(root, subKey, valueName) switch
        {
            int i => i,
            long l => unchecked((int)l),
            string s when int.TryParse(s, out var parsed) => parsed,
            _ => null,
        };
    }

    public bool KeyExists(RegistryRoot root, string subKey)
    {
        if (!OperatingSystem.IsWindows()) return false;
        try
        {
            using var key = Open(root).OpenSubKey(subKey);
            return key is not null;
        }
        catch { return false; }
    }

    [SupportedOSPlatform("windows")]
    private static object? Read(RegistryRoot root, string subKey, string? valueName)
    {
        try
        {
            using var key = Open(root).OpenSubKey(subKey);
            return key?.GetValue(valueName ?? string.Empty);
        }
        catch { return null; }
    }

    [SupportedOSPlatform("windows")]
    private static Microsoft.Win32.RegistryKey Open(RegistryRoot root)
        => root == RegistryRoot.LocalMachine ? Microsoft.Win32.Registry.LocalMachine : Microsoft.Win32.Registry.CurrentUser;
}
