using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using Josour.App.Services;
using Josour.Core.Session;
using Josour.Infrastructure.Api;
using Josour.Infrastructure.Localization;
using Josour.Infrastructure.Settings;

namespace Josour.App.ViewModels;

/// <summary>One entry of a settings drop-down: what the user reads, and the value that is stored.</summary>
/// <typeparam name="T">The stored value's type.</typeparam>
public sealed record SettingsChoice<T>(T Value, string Display)
{
    public override string ToString() => Display;
}

/// <summary>
/// The settings window: server address, interface language, opening the check page when a guest session starts,
/// "start with Windows" and "start minimized". Until week 6 none of these could be changed by the person using the app —
/// the address was whatever the sign-in window happened to have been given, and the language needed a command-line switch.
/// <para>
/// The address is the one setting that can be wrong in a way the user cannot see, so it gets a real check: validated the
/// same way the API client validates it, then asked <c>GET /healthz</c> with a short budget. The result is stated in one
/// sentence — reachable, not a Josour server, refused as insecure, or simply malformed — because "could not connect"
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
    private readonly TimeProvider _time;
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
    private bool _openCheckPageOnConnect;

    [ObservableProperty]
    private bool _startAtLogin;

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
        ILogger<SettingsViewModel> logger,
        TimeProvider? time = null)
    {
        _settings = settings;
        _serverCheck = serverCheck;
        _startup = startup;
        _time = time ?? TimeProvider.System;
        _logger = logger;

        var current = settings.Current;
        _serverUrl = current.ServerUrl;
        _languageAtOpen = UiLanguages.ParseOrDefault(current.Language);
        _selectedLanguage = Languages.First(l => l.Value == _languageAtOpen);
        _openCheckPageOnConnect = current.OpenCheckPageOnConnect;
        _startMinimized = current.StartMinimized;
        _startAtLogin = SafeStartupState();
    }

    /// <summary>Each language in its own words: the point of the setting is to be findable by someone who cannot read the other one.</summary>
    public static IReadOnlyList<SettingsChoice<UiLanguage>> Languages { get; } = new[]
    {
        new SettingsChoice<UiLanguage>(UiLanguage.Arabic, Strings.SettingsLanguageArabic),
        new SettingsChoice<UiLanguage>(UiLanguage.English, Strings.SettingsLanguageEnglish),
    };

    public bool IsIdle => !IsBusy;

    public bool HasServerMessage => !string.IsNullOrEmpty(ServerMessage);

    public bool HasSaveMessage => !string.IsNullOrEmpty(SaveMessage);

    /// <summary>
    /// Windows has a Run key and macOS has a LaunchAgent; anywhere else the checkbox is disabled with a reason
    /// instead of pretending to do something.
    /// </summary>
    public bool IsStartAtLoginSupported => _startup.IsSupported;

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
                    OpenCheckPageOnConnect = OpenCheckPageOnConnect,
                    StartMinimized = StartMinimized,
                },
                ct);

            ApplyStartupRegistration();
            _logger.LogInformation(
                "Settings saved: server={ServerUrl} language={Language} openCheckPage={OpenCheckPage} startMinimized={StartMinimized} startAtLogin={StartAtLogin}",
                validated.NormalizedUrl,
                SelectedLanguage.Value.ToCode(),
                OpenCheckPageOnConnect,
                StartMinimized,
                StartAtLogin);

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
            ServerCheckStatus.NotJosour => Strings.SettingsServerNotJosour,
            _ => Strings.SettingsServerUnreachable,
        };

        ServerMessage = string.IsNullOrEmpty(result.Detail)
            ? text
            : text + " " + string.Format(UiFlow.Culture, Strings.SettingsServerDetailFormat, UiFlow.Ltr(result.Detail));
    }

    /// <summary>The Run key can refuse a write (policy, a locked hive); the checkbox then goes back to what is really there.</summary>
    private void ApplyStartupRegistration()
    {
        if (!_startup.IsSupported || _startup.IsEnabled == StartAtLogin)
        {
            return;
        }

        try
        {
            _startup.SetEnabled(StartAtLogin);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            _logger.LogError(ex, "Could not update Start with Windows");
        }
        finally
        {
            StartAtLogin = SafeStartupState();
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
