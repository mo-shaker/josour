using Android.App;
using Android.Content;
using Android.Content.PM;
using Android.OS;
using Avalonia;
using Avalonia.Android;
using Josour.Droid.Platform;
using Josour.Mobile;

namespace Josour.Droid;

[Activity(
    Label = "Josour",
    Theme = "@style/JosourTheme",
    Icon = "@drawable/ic_josour",
    MainLauncher = true,
    LaunchMode = LaunchMode.SingleTask,
    ConfigurationChanges = ConfigChanges.Orientation | ConfigChanges.ScreenSize | ConfigChanges.UiMode | ConfigChanges.ScreenLayout | ConfigChanges.SmallestScreenSize)]
public sealed class MainActivity : AvaloniaMainActivity<MobileApp>
{
    /// <summary>The activity on screen, for the permission prompts; null while none is.</summary>
    internal static MainActivity? Current { get; private set; }

    protected override AppBuilder CustomizeAppBuilder(AppBuilder builder) => MobileApp.UseFonts(base.CustomizeAppBuilder(builder));

    protected override void OnCreate(Bundle? savedInstanceState)
    {
        Current = this;
        base.OnCreate(savedInstanceState);
    }

    protected override void OnResume()
    {
        base.OnResume();
        Current = this;
        // The user may be back from the notification or battery prompt.
        AndroidPermissions.Instance?.RaiseChanged();
    }

    public override void OnRequestPermissionsResult(int requestCode, string[] permissions, Permission[] grantResults)
    {
        base.OnRequestPermissionsResult(requestCode, permissions, grantResults);
        AndroidPermissions.Instance?.RaiseChanged();
    }

    protected override void OnDestroy()
    {
        if (ReferenceEquals(Current, this)) Current = null;
        base.OnDestroy();
    }

    internal static Intent OpenIntent(Context context) =>
        new Intent(context, typeof(MainActivity)).AddFlags(ActivityFlags.NewTask | ActivityFlags.SingleTop);
}
