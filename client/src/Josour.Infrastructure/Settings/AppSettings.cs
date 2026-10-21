namespace Josour.Infrastructure.Settings;

/// <summary>Non-secret user settings persisted as <c>%LOCALAPPDATA%\Josour\settings.json</c>. Immutable: change with <c>with</c> and save.</summary>
public sealed record AppSettings
{
    public static AppSettings Default { get; } = new();

    /// <summary>Server base URL, e.g. <c>https://josour.example.com</c>; empty until the first sign-in.</summary>
    public string ServerUrl { get; init; } = string.Empty;

    /// <summary>
    /// Guest: open the check page (http://check.josour/) in the default browser when a session starts, which shows the
    /// address sites will see and confirms that browser follows the system proxy. On by default.
    /// </summary>
    public bool OpenCheckPageOnConnect { get; init; } = true;

    /// <summary>Start hidden in the tray (mirrors the <c>--minimized</c> switch for launches without it).</summary>
    public bool StartMinimized { get; init; }

    /// <summary>
    /// Interface language: <c>ar</c> (the default) or <c>en</c>. Empty — the shipped state — means Arabic, so an existing
    /// settings file needs no migration. Anything unrecognised also falls back to Arabic, and the <c>--lang</c> switch
    /// wins over this value for a single run (see <c>Localization.UiLanguages.TryParse</c>).
    /// </summary>
    public string Language { get; init; } = string.Empty;
}
