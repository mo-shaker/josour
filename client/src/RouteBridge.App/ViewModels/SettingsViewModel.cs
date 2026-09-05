using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using RouteBridge.App.Services;
using RouteBridge.Infrastructure.Api;
using RouteBridge.Infrastructure.Localization;
using RouteBridge.Infrastructure.Settings;

namespace RouteBridge.App.ViewModels;

/// <summary>One entry of a settings drop-down: what the user reads, and the value that is stored.</summary>
/// <typeparam name="T">The stored value's type.</typeparam>
public sealed record SettingsChoice<T>(T Value, string Display)
{
    public override string ToString() => Display;
}

/// <summary>
/// The settings window: server address, interface language, work browser, "start with Windows" and "start minimized".
/// Until week 6 none of these could be changed by the person using the app — the address was whatever the sign-in window
/// happened to have been given, the language needed a command-line switch, and the browser preference existed in the
/// settings file but nothing wrote it.
/// <para>
/// The address is the one setting that can be wrong in a way the user cannot see, so it gets a real check: validated the
/// same way the API client validates it, then asked <c>GET /healthz</c> with a short budget. The result is stated in one
/// sentence — reachable, not a RouteBridge server, refused as insecure, or simply malformed — because "could not connect"
/// covering all four is what makes an address impossible to fix.
/// </para>
/// <para>
/// The values are only written to <see cref="IAppSettingsStore"/> on Save, so a window that is closed changes nothing.
/// "Start with Windows" is the exception: it is a registry value, not a setting in the file, and it is applied on Save
/// through <see cref="IStartupRegistration"/> like everything else.
/// </para>
/// </summary>
public sealed partial class SettingsViewModel : ObservableObject
{
    private readonly IAppSettingsStore _settings;
    private readonly IServerCheck _serverCheck;
    private readonly IStartupRegistration _startup;
    private readonly ILogger<SettingsViewModel> _logger;
    private readonly UiLanguage _languageAtOpen;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(TestConnectionCommand))]
    [NotifyCanExecuteChangedFor(nameof(SaveCommand))]
    private string _serverUrl;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsLanguageRestartNoteVisible))]
    private SettingsChoice<UiLanguage> _selectedLanguage;

    [ObservableProperty]
    private SettingsChoice<BrowserPreference> _selectedBrowser;

    [ObservableProperty]
    private bool _startWithWindows;

    [ObservableProperty]
    private bool _startMinimized;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(TestConnectionCommand))]
    [NotifyCanExecuteChangedFor(nameof(SaveCommand))]
    [NotifyPropertyChangedFor(nameof(IsIdle))]
    private bool _isBusy;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasServerMessage))]
    private string? _serverMessage;

    [ObservableProperty]
    private bool _isServerMessageAnError;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSaveMessage))]
    private string? _saveMessage;

    [ObservableProperty]
    private string _testButtonText = Strings.SettingsTestConnection;

    public SettingsViewModel(
        IAppSettingsStore settings,
        IServerCheck serverCheck,
        IStartupRegistration startup,
        ILogger<SettingsViewModel> logger)
    {
        _settings = settings;
        _serverCheck = serverCheck;
        _startup = startup;
        _logger = logger;

        var current = settings.Current;
        _serverUrl = current.ServerUrl;
        _languageAtOpen = UiLanguages.ParseOrDefault(current.Language);
        _selectedLanguage = Languages.First(l => l.Value == _languageAtOpen);
        _selectedBrowser = Browsers.First(b => b.Value == current.PreferredBrowser);
        _startMinimized = current.StartMinimized;
        _startWithWindows = SafeStartupState();
    }

    /// <summary>Each language in its own words: the point of the setting is to be findable by someone who cannot read the other one.</summary>
    public static IReadOnlyList<SettingsChoice<UiLanguage>> Languages { get; } = new[]
    {
        new SettingsChoice<UiLanguage>(UiLanguage.Arabic, Strings.SettingsLanguageArabic),
        new SettingsChoice<UiLanguage>(UiLanguage.English, Strings.SettingsLanguageEnglish),
    };

    public static IReadOnlyList<SettingsChoice<BrowserPreference>> Browsers { get; } = new[]
    {
        new SettingsChoice<BrowserPreference>(BrowserPreference.Auto, Strings.SettingsBrowserAuto),
        new SettingsChoice<BrowserPreference>(BrowserPreference.Chrome, Strings.SettingsBrowserChrome),
        new SettingsChoice<BrowserPreference>(BrowserPreference.Edge, Strings.SettingsBrowserEdge),
    };

    public bool IsIdle => !IsBusy;

    public bool HasServerMessage => !string.IsNullOrEmpty(ServerMessage);

    public bool HasSaveMessage => !string.IsNullOrEmpty(SaveMessage);

    /// <summary>The Run key is a Windows thing; off Windows the checkbox is disabled with a reason instead of lying.</summary>
    public bool IsStartWithWindowsSupported => _startup.IsSupported;

    /// <summary>The language only takes effect at the next start, so say so as soon as it is changed rather than after Save.</summary>
    public bool IsLanguageRestartNoteVisible => SelectedLanguage.Value != _languageAtOpen;

    /// <summary>Raised when the window should close (Save succeeded, or Cancel).</summary>
    public event EventHandler? Closed;

    private bool CanAct() => !IsBusy && !string.IsNullOrWhiteSpace(ServerUrl);

    /// <summary>Validate, then <c>GET /healthz</c>. Advisory: the user may still save an address that did not answer.</summary>
    [RelayCommand(CanExecute = nameof(CanAct))]
    private async Task TestConnectionAsync(CancellationToken ct)
    {
        IsBusy = true;
        TestButtonText = Strings.SettingsTesting;
        ServerMessage = null;
        try
        {
            var result = await _serverCheck.CheckAsync(ServerUrl, ct);
            if (result.IsOk && result.NormalizedUrl is { } normalized)
            {
                ServerUrl = normalized; // show what will actually be used
            }

            ShowServerResult(result);
        }
        catch (OperationCanceledException)
        {
            // the window closed while checking
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "The server check failed unexpectedly");
            ServerMessage = string.Format(UiFlow.Culture, Strings.SettingsServerDetailFormat, ex.Message);
            IsServerMessageAnError = true;
        }
        finally
        {
            IsBusy = false;
            TestButtonText = Strings.SettingsTestConnection;
        }
    }

    [RelayCommand(CanExecute = nameof(CanAct))]
    private async Task SaveAsync(CancellationToken ct)
    {
        SaveMessage = null;

        // Saving an address the API client would refuse would break the app silently, so validation is a gate here even
        // though the reachability check is not: an unreachable server may simply be off right now.
        var validated = HttpServerCheck.Validate(ServerUrl);
        if (!validated.IsOk)
        {
            ShowServerResult(validated);
            return;
        }

        IsBusy = true;
        try
        {
            ServerUrl = validated.NormalizedUrl!;
            await _settings.SaveAsync(
                _settings.Current with
                {
                    ServerUrl = validated.NormalizedUrl!,
                    Language = SelectedLanguage.Value.ToCode(),
                    PreferredBrowser = SelectedBrowser.Value,
                    StartMinimized = StartMinimized,
                },
                ct);

            ApplyStartupRegistration();
            _logger.LogInformation(
                "Settings saved: server={ServerUrl} language={Language} browser={Browser} startMinimized={StartMinimized} startWithWindows={StartWithWindows}",
                validated.NormalizedUrl,
                SelectedLanguage.Value.ToCode(),
                SelectedBrowser.Value,
                StartMinimized,
                StartWithWindows);

            Closed?.Invoke(this, EventArgs.Empty);
        }
        catch (OperationCanceledException)
        {
            // the window closed while saving
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "The settings could not be saved");
            SaveMessage = string.Format(UiFlow.Culture, Strings.SettingsSaveFailedFormat, ex.Message);
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private void Cancel() => Closed?.Invoke(this, EventArgs.Empty);

    private void ShowServerResult(ServerCheckResult result)
    {
        IsServerMessageAnError = !result.IsOk;
        var text = result.Status switch
        {
            ServerCheckStatus.Ok => Strings.SettingsServerOk,
            ServerCheckStatus.InvalidUrl => Strings.SettingsServerInvalidUrl,
            ServerCheckStatus.InsecureScheme => Strings.SettingsServerInsecure,
            ServerCheckStatus.NotRouteBridge => Strings.SettingsServerNotRouteBridge,
            _ => Strings.SettingsServerUnreachable,
        };

        ServerMessage = string.IsNullOrEmpty(result.Detail)
            ? text
            : text + " " + string.Format(UiFlow.Culture, Strings.SettingsServerDetailFormat, UiFlow.Ltr(result.Detail));
    }

    /// <summary>The Run key can refuse a write (policy, a locked hive); the checkbox then goes back to what is really there.</summary>
    private void ApplyStartupRegistration()
    {
        if (!_startup.IsSupported || _startup.IsEnabled == StartWithWindows)
        {
            return;
        }

        try
        {
            _startup.SetEnabled(StartWithWindows);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            _logger.LogError(ex, "Could not update Start with Windows");
        }
        finally
        {
            StartWithWindows = SafeStartupState();
        }
    }

    private bool SafeStartupState()
    {
        try
        {
            return _startup.IsEnabled;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            _logger.LogWarning(ex, "Could not read the Run key");
            return false;
        }
    }
}
