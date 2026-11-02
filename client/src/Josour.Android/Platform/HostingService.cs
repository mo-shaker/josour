using Android.App;
using Android.Content;
using Android.Content.PM;
using Android.Net.Wifi;
using Android.OS;
using Android.Runtime;
using AndroidX.Core.App;
using Avalonia.Threading;
using Josour.Infrastructure.Localization;
using Josour.Mobile;
using Josour.Mobile.Platform;
using Josour.Mobile.ViewModels;
using Microsoft.Extensions.DependencyInjection;

namespace Josour.Droid.Platform;

/// <summary>
/// The foreground service that keeps the phone a host with the screen off: Android leaves the process, its network and (with
/// the partial wake lock) its CPU alone while this runs, so the control channel keeps its heart-beat and a live session keeps
/// pumping. During a session a Wi-Fi lock also keeps the radio from dozing. It runs exactly while the user is available or a
/// session is live, and its notification says which, with the one action that stops it.
/// </summary>
[Register("com.mohamedshaker.josour.HostingService")]
public sealed class HostingService : Service
{
    internal const int NotificationId = 1001;
    internal const string ExtraTitle = "title";
    internal const string ExtraText = "text";
    internal const string ExtraSession = "session";
    private const string ActionStopHosting = "com.mohamedshaker.josour.STOP_HOSTING";
    private const string ActionEndSession = "com.mohamedshaker.josour.END_SESSION";

    private PowerManager.WakeLock? _wakeLock;
    private WifiManager.WifiLock? _wifiLock;

    internal static HostingService? Running { get; private set; }

    public override IBinder? OnBind(Intent? intent) => null;

    public override void OnCreate()
    {
        base.OnCreate();
        Running = this;
        if (GetSystemService(PowerService) is PowerManager power)
        {
            _wakeLock = power.NewWakeLock(WakeLockFlags.Partial, "Josour:hosting");
            _wakeLock?.SetReferenceCounted(false);
            _wakeLock?.Acquire();
        }
    }

    public override StartCommandResult OnStartCommand(Intent? intent, StartCommandFlags flags, int startId)
    {
        switch (intent?.Action)
        {
            case ActionStopHosting:
                Post(host => host.IsAvailable = false);
                return StartCommandResult.NotSticky;
            case ActionEndSession:
                Post(host => host.EndSessionCommand.Execute(null));
                return StartCommandResult.NotSticky;
        }

        // Started without the app's runtime (Android restarting a killed process for the service alone): there is nothing
        // to keep alive, and nothing must pretend the phone is available. The app comes back available when opened.
        if (!MobileRuntime.IsStarted || intent is null)
        {
            StopSelf();
            return StartCommandResult.NotSticky;
        }

        Update(
            intent.GetStringExtra(ExtraTitle) ?? Tr.Get(UiStringKeys.MobileServiceAvailableTitle),
            intent.GetStringExtra(ExtraText) ?? string.Empty,
            intent.GetBooleanExtra(ExtraSession, false),
            foreground: true);
        return StartCommandResult.NotSticky;
    }

    internal void Update(string title, string text, bool session, bool foreground = false)
    {
        var builder = new NotificationCompat.Builder(this, AndroidNotifier.HostingChannel)
            .SetSmallIcon(Resource.Drawable.ic_josour)
            .SetContentTitle(title)
            .SetOngoing(true)
            .SetOnlyAlertOnce(true)
            .SetSilent(true)
            .SetCategory(NotificationCompat.CategoryService)
            .SetContentIntent(PendingIntent.GetActivity(this, 0, MainActivity.OpenIntent(this), PendingIntentFlags.Immutable | PendingIntentFlags.UpdateCurrent))
            .AddAction(0, Tr.Get(session ? UiStringKeys.MobileEndSessionNow : UiStringKeys.MobileStopHosting), SelfIntent(session ? ActionEndSession : ActionStopHosting));
        if (text.Length > 0) builder.SetContentText(text);
        var notification = builder.Build()!;

        if (foreground)
        {
            if (OperatingSystem.IsAndroidVersionAtLeast(34))
                StartForeground(NotificationId, notification, ForegroundService.TypeSpecialUse);
            else
                StartForeground(NotificationId, notification);
        }
        else
        {
            try { NotificationManagerCompat.From(this).Notify(NotificationId, notification); }
            catch (Java.Lang.SecurityException) { }
        }

        HoldWifi(session);
    }

    private void HoldWifi(bool session)
    {
        if (session && _wifiLock is null && ApplicationContext?.GetSystemService(WifiService) is WifiManager wifi)
        {
#pragma warning disable CA1422 // the high-performance mode is what a live tunnel needs on Android 9 and older
            var mode = OperatingSystem.IsAndroidVersionAtLeast(29) ? Android.Net.WifiMode.FullLowLatency : Android.Net.WifiMode.FullHighPerf;
#pragma warning restore CA1422
            _wifiLock = wifi.CreateWifiLock(mode, "Josour:session");
            _wifiLock?.SetReferenceCounted(false);
            _wifiLock?.Acquire();
        }
        else if (!session && _wifiLock is not null)
        {
            _wifiLock.Release();
            _wifiLock = null;
        }
    }

    private PendingIntent SelfIntent(string action) =>
        PendingIntent.GetService(this, action.GetHashCode(), new Intent(this, typeof(HostingService)).SetAction(action), PendingIntentFlags.Immutable)!;

    private static void Post(Action<HostViewModel> action)
    {
        if (!MobileRuntime.IsStarted) return;
        var host = MobileRuntime.Services.GetRequiredService<HostViewModel>();
        Dispatcher.UIThread.Post(() => action(host));
    }

    public override void OnTaskRemoved(Intent? rootIntent)
    {
        // The app swiped away from the recent list: the service, and so availability, carries on. Nothing to do.
        base.OnTaskRemoved(rootIntent);
    }

    public override void OnDestroy()
    {
        Running = null;
        _wifiLock?.Release();
        _wifiLock = null;
        if (_wakeLock?.IsHeld == true) _wakeLock.Release();
        _wakeLock = null;
        base.OnDestroy();
    }
}

/// <summary><see cref="IHostingService"/> on Android: starts the service, updates its notification, stops it.</summary>
internal sealed class AndroidHosting : IHostingService
{
    private readonly Context _context;
    private string? _last;

    public AndroidHosting(Context context, AndroidNotifier _) => _context = context;

    public void Show(string title, string text, bool sessionActive)
    {
        var key = $"{title}\n{text}\n{sessionActive}";
        if (key == _last && HostingService.Running is not null) return;
        _last = key;

        if (HostingService.Running is { } running)
        {
            // Already in the foreground: only the notification changes. A second startForegroundService from the
            // background is what Android 12+ refuses, so it is not used for updates.
            running.Update(title, text, sessionActive);
            return;
        }

        var intent = new Intent(_context, typeof(HostingService))
            .PutExtra(HostingService.ExtraTitle, title)
            .PutExtra(HostingService.ExtraText, text)
            .PutExtra(HostingService.ExtraSession, sessionActive);
        try
        {
            if (Build.VERSION.SdkInt >= BuildVersionCodes.O) _context.StartForegroundService(intent);
            else _context.StartService(intent);
        }
        catch (Java.Lang.IllegalStateException)
        {
            // Android 12+ refuses to start a foreground service from the background (ForegroundServiceStartNotAllowedException
            // derives from this). The user turns "available" on with the app open, so this is only reached in edge cases;
            // the next update with the app in front starts it.
            _last = null;
        }
    }

    public void Stop()
    {
        _last = null;
        _context.StopService(new Intent(_context, typeof(HostingService)));
    }
}
