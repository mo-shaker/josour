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
/// The settings window: server address, interface language, work browser, "start with Windows" and "start minimized".
/// Until week 6 none of these could be changed by the person using the app — the address was whatever the sign-in window
/// happened to have been given, the language needed a command-line switch, and the browser preference existed in the
/// settings file but nothing wrote it.
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
    private readonly IAutoAcceptStore _autoAccept;
    private readonly IServerCheck _serverCheck;
    private readonly IStartupRegistration _startup;
    private readonly TimeProvider _time;
    private readonly ILogger<SettingsViewModel> _logger;
    private readonly UiLanguage _languageAtOpen;
    private bool _loadingAutoAccept;

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

    /// <summary>
    /// The master switch of docs/ws-protocol.md section 5a. Unlike every other setting in this window it is applied the
    /// moment it is changed rather than on Save — see <see cref="OnIsAutoAcceptEnabledChanged"/>.
    /// </summary>
    [ObservableProperty]
    private bool _isAutoAcceptEnabled;

    public SettingsViewModel(
        IAppSettingsStore settings,
        IAutoAcceptStore autoAccept,
        IServerCheck serverCheck,
        IStartupRegistration startup,
        ILogger<SettingsViewModel> logger,
        TimeProvider? time = null)
    {
        _settings = settings;
        _autoAccept = autoAccept;
        _serverCheck = serverCheck;
        _startup = startup;
        _time = time ?? TimeProvider.System;
        _logger = logger;

        var current = settings.Current;
        _serverUrl = current.ServerUrl;
        _languageAtOpen = UiLanguages.ParseOrDefault(current.Language);
        _selectedLanguage = Languages.First(l => l.Value == _languageAtOpen);
        _selectedBrowser = Browsers.First(b => b.Value == current.PreferredBrowser);
        _startMinimized = current.StartMinimized;
        _startAtLogin = SafeStartupState();
        LoadAutoAccept();
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

    /// <summary>
    /// Windows has a Run key and macOS has a LaunchAgent; anywhere else the checkbox is disabled with a reason
    /// instead of pretending to do something.
    /// </summary>
    public bool IsStartAtLoginSupported => _startup.IsSupported;

    /// <summary>The language only takes effect at the next start, so say so as soon as it is changed rather than after Save.</summary>
    public bool IsLanguageRestartNoteVisible => SelectedLanguage.Value != _languageAtOpen;

    /// <summary>The trusted guests of docs/ws-protocol.md section 5a, newest decision first.</summary>
    public ObservableCollection<TrustedGuestRow> TrustedGuests { get; } = new();

    public bool HasTrustedGuests => TrustedGuests.Count > 0;

    /// <summary>Raised when the window should close (Save succeeded, or Cancel).</summary>
    public event EventHandler? Closed;

    // ---------------- auto-accept (docs/ws-protocol.md section 5a) ----------------

    private void LoadAutoAccept()
    {
        var current = _autoAccept.Current;
        var now = _time.GetUtcNow();

        _loadingAutoAccept = true;
        try
        {
            IsAutoAcceptEnabled = current.Enabled;
        }
        finally
        {
            _loadingAutoAccept = false;
        }

        TrustedGuests.Clear();
        foreach (var guest in current.Guests.OrderByDescending(g => g.GrantedAt))
        {
            TrustedGuests.Add(TrustedGuestRow.From(guest, now));
        }

        OnPropertyChanged(nameof(HasTrustedGuests));
    }

    /// <summary>
    /// Written at once, not on Save. Every other control in this window is a preference, and a preference that is
    /// forgotten when the window is closed is merely annoying; this one is a standing consent. A host who unticks it,
    /// closes the window and walks away must not still be accepting guests unasked — and the same, in reverse, is why
    /// Cancel does not put it back.
    /// </summary>
    partial void OnIsAutoAcceptEnabledChanged(bool value)
    {
        if (_loadingAutoAccept)
        {
            return;
        }

        _ = ApplyAutoAcceptAsync(_autoAccept.Current with { Enabled = value }, $"switch set to {value}");
    }

    /// <summary>Withdraws one rule, immediately and for the same reason.</summary>
    [RelayCommand]
    private Task RemoveTrustedGuestAsync(TrustedGuestRow? row)
    {
        if (row is null)
        {
            return Task.CompletedTask;
        }

        TrustedGuests.Remove(row);
        OnPropertyChanged(nameof(HasTrustedGuests));
        return ApplyAutoAcceptAsync(
            _autoAccept.Current.Without(row.GuestUserId, row.GuestDeviceId),
            "a trusted guest was removed");
    }

    private async Task ApplyAutoAcceptAsync(AutoAcceptSettings settings, string what)
    {
        try
        {
            await _autoAccept.SaveAsync(settings, CancellationToken.None).ConfigureAwait(true);
            _logger.LogInformation("Auto-accept updated: {What}", what);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // The file did not change, so neither did what the app will do. Put the window back in step with it
            // rather than leaving a switch that shows one thing and means another.
            _logger.LogError(ex, "Could not save the auto-accept settings ({What})", what);
            SaveMessage = string.Format(UiFlow.Culture, Strings.SettingsSaveFailedFormat, ex.Message);
            LoadAutoAccept();
        }
    }

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
                "Settings saved: server={ServerUrl} language={Language} browser={Browser} startMinimized={StartMinimized} startAtLogin={StartAtLogin}",
                validated.NormalizedUrl,
                SelectedLanguage.Value.ToCode(),
                SelectedBrowser.Value,
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
