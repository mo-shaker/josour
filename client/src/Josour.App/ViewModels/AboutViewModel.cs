using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using Josour.App.Services;
using Josour.Core.Control;
using Josour.Infrastructure.Api;
using Josour.Infrastructure.Device;
using Josour.Infrastructure.Diagnostics;
using Josour.Infrastructure.Settings;

namespace Josour.App.ViewModels;

/// <summary>
/// The About / diagnostics window: what a support conversation needs in order to start with facts instead of guesses —
/// which build, which machine (name and the id an administrator can look up), which server, what the connection is doing,
/// and a button that opens the log folder.
/// <para>
/// <b>Nothing secret appears.</b> The values come from <see cref="SupportInfo"/>, which never reads a token or the device
/// secret at all; the window says so out loud, because a person about to paste this into a message deserves to know it is
/// safe to.
/// </para>
/// </summary>
public sealed partial class AboutViewModel : ObservableObject
{
    private readonly IDeviceInfoProvider _device;
    private readonly IAuthSession _auth;
    private readonly IAppSettingsStore _settings;
    private readonly IControlChannel _channel;
    private readonly ControlChannelConnector _connector;
    private readonly IShellService _shell;
    private readonly IClipboardService _clipboard;
    private readonly ILogger<AboutViewModel> _logger;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasMessage))]
    private string? _message;

    [ObservableProperty]
    private bool _isMessageAnError;

    public AboutViewModel(
        IDeviceInfoProvider device,
        IAuthSession auth,
        IAppSettingsStore settings,
        IControlChannel channel,
        ControlChannelConnector connector,
        IShellService shell,
        IClipboardService clipboard,
        ILogger<AboutViewModel> logger)
    {
        _device = device;
        _auth = auth;
        _settings = settings;
        _channel = channel;
        _connector = connector;
        _shell = shell;
        _clipboard = clipboard;
        _logger = logger;
        Refresh();
    }

    /// <summary>The snapshot the window shows; replaced wholesale by <see cref="Refresh"/>.</summary>
    public SupportInfo Info { get; private set; } = null!;

    // Every technical value is bidi-isolated so a version, a GUID or a path reads left to right inside Arabic prose.
    public string AppVersion => UiFlow.Ltr(Info.AppVersion);

    public string DeviceName => UiFlow.Ltr(Info.DeviceName);

    public string DeviceId => Value(Info.DeviceId);

    public string OperatingSystem => UiFlow.Ltr($"{Info.OsVersion} ({Info.OsBuild})");

    public string ServerUrl => Value(Info.ServerUrl);

    public string SignedInAs => Value(Info.SignedInAs);

    public string ConnectionState => Info.ConnectionState;

    public string LogDirectory => UiFlow.Ltr(Info.LogDirectory);

    public bool HasMessage => !string.IsNullOrEmpty(Message);

    /// <summary>Re-reads everything (the window may stay open across a reconnect or a sign-out).</summary>
    public void Refresh()
    {
        Info = SupportInfo.Build(
            _device,
            _auth,
            _settings.Current,
            ConnectionStatusText.Describe(_channel.State, _connector.LastClose, _connector.CanConnect));

        OnPropertyChanged(nameof(AppVersion));
        OnPropertyChanged(nameof(DeviceName));
        OnPropertyChanged(nameof(DeviceId));
        OnPropertyChanged(nameof(OperatingSystem));
        OnPropertyChanged(nameof(ServerUrl));
        OnPropertyChanged(nameof(SignedInAs));
        OnPropertyChanged(nameof(ConnectionState));
        OnPropertyChanged(nameof(LogDirectory));
    }

    [RelayCommand]
    private void OpenLogFolder()
    {
        if (_shell.TryOpenFolder(Info.LogDirectory, out var error))
        {
            Message = null;
            return;
        }

        _logger.LogWarning("The log folder {Path} could not be opened: {Error}", Info.LogDirectory, error);
        Message = string.Format(UiFlow.Culture, Strings.AboutOpenLogFolderFailedFormat, error);
        IsMessageAnError = true;
    }

    [RelayCommand]
    private async Task CopyDetailsAsync()
    {
        try
        {
            if (!await _clipboard.SetTextAsync(Info.ToText()).ConfigureAwait(true))
            {
                // No window to copy through — the app can be running entirely in the tray.
                Message = Strings.AboutCopyFailed;
                IsMessageAnError = true;
                return;
            }

            Message = Strings.AboutCopied;
            IsMessageAnError = false;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // The clipboard is owned by whatever window last grabbed it; a refusal is ordinary, not an error to shout about.
            _logger.LogWarning(ex, "The support details could not be copied to the clipboard");
            Message = ex.Message;
            IsMessageAnError = true;
        }
    }

    /// <summary>An empty field is stated as "not available" rather than left blank, which reads as a rendering bug.</summary>
    private static string Value(string? value) => string.IsNullOrEmpty(value) ? Strings.AboutNotAvailable : UiFlow.Ltr(value);
}
