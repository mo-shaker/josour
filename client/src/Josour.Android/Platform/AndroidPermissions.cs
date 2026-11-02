using Android;
using Android.Content;
using Android.Content.PM;
using Android.OS;
using AndroidX.Core.App;
using AndroidX.Core.Content;
using Josour.Mobile.Platform;

namespace Josour.Droid.Platform;

internal sealed class AndroidPermissions : IMobilePermissions
{
    private const int NotificationRequestCode = 4201;
    private readonly Context _context;

    public AndroidPermissions(Context context)
    {
        _context = context;
        Instance = this;
    }

    internal static AndroidPermissions? Instance { get; private set; }

    public event Action? Changed;

    public bool NeedsNotificationPermission =>
        OperatingSystem.IsAndroidVersionAtLeast(33)
        && ContextCompat.CheckSelfPermission(_context, Manifest.Permission.PostNotifications) != Permission.Granted;

    public void RequestNotificationPermission()
    {
        if (!OperatingSystem.IsAndroidVersionAtLeast(33) || !NeedsNotificationPermission || MainActivity.Current is not { } activity) return;
        ActivityCompat.RequestPermissions(activity, new[] { Manifest.Permission.PostNotifications }, NotificationRequestCode);
    }

    public bool IsBatteryOptimized =>
        _context.GetSystemService(Context.PowerService) is PowerManager power
        && !power.IsIgnoringBatteryOptimizations(_context.PackageName);

    public void RequestIgnoreBatteryOptimizations()
    {
        // The direct prompt ("let Josour run in the background?"), which the REQUEST_IGNORE_BATTERY_OPTIMIZATIONS permission
        // allows; it only asks, and the user decides.
        var intent = new Intent(Android.Provider.Settings.ActionRequestIgnoreBatteryOptimizations)
            .SetData(Android.Net.Uri.Parse("package:" + _context.PackageName))
            .AddFlags(ActivityFlags.NewTask);
        try
        {
            (MainActivity.Current ?? _context).StartActivity(intent);
        }
        catch (ActivityNotFoundException)
        {
            _context.StartActivity(new Intent(Android.Provider.Settings.ActionIgnoreBatteryOptimizationSettings).AddFlags(ActivityFlags.NewTask));
        }
    }

    internal void RaiseChanged() => Changed?.Invoke();
}
