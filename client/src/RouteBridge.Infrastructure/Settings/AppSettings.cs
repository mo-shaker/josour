using System.Text.Json;
using System.Text.Json.Serialization;

namespace RouteBridge.Infrastructure.Settings;

/// <summary>Which browser to launch for the work profile (week 6: BrowserLocator honours it). Serialized as <c>auto</c> | <c>chrome</c> | <c>edge</c>.</summary>
[JsonConverter(typeof(BrowserPreferenceJsonConverter))]
public enum BrowserPreference
{
    Auto,
    Chrome,
    Edge,
}

/// <summary>Lower-case enum names on the wire (reads are case-insensitive; integers rejected).</summary>
public sealed class BrowserPreferenceJsonConverter : JsonStringEnumConverter<BrowserPreference>
{
    public BrowserPreferenceJsonConverter()
        : base(JsonNamingPolicy.CamelCase, allowIntegerValues: false)
    {
    }
}

/// <summary>Non-secret user settings persisted as <c>%LOCALAPPDATA%\RouteBridge\settings.json</c>. Immutable: change with <c>with</c> and save.</summary>
public sealed record AppSettings
{
    public static AppSettings Default { get; } = new();

    /// <summary>Server base URL, e.g. <c>https://routebridge.example.com</c>; empty until the first sign-in.</summary>
    public string ServerUrl { get; init; } = string.Empty;

    public BrowserPreference PreferredBrowser { get; init; } = BrowserPreference.Auto;

    /// <summary>Start hidden in the tray (mirrors the <c>--minimized</c> switch for launches without it).</summary>
    public bool StartMinimized { get; init; }
}
