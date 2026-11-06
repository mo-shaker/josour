using Android.App;
using Android.Content;
using Android.Content.PM;
using Android.Content.Res;
using Android.OS;
using Avalonia;
using Avalonia.Android;
using Josour.Droid.Platform;
using Josour.Mobile;
using Josour.Mobile.ViewModels;
using Microsoft.Extensions.DependencyInjection;

namespace Josour.Droid;

[Activity(
    Label = "@string/app_name",
    Theme = "@style/JosourTheme",
    Icon = "@mipmap/ic_launcher",
    RoundIcon = "@mipmap/ic_launcher_round",
    MainLauncher = true,
    LaunchMode = LaunchMode.SingleTask,
    ConfigurationChanges = ConfigChanges.Orientation | ConfigChanges.ScreenSize | ConfigChanges.UiMode | ConfigChanges.ScreenLayout | ConfigChanges.SmallestScreenSize)]
public sealed class MainActivity : AvaloniaMainActivity<MobileApp>
{
    /// <summary>The <see cref="ExtraPage"/> value that opens the contacts page (an invitation's notification).</summary>
    internal const string ContactsPage = "contacts";
    private const string ExtraPage = "com.mohamedshaker.josour.PAGE";

    /// <summary>The activity on screen, for the permission prompts; null while none is.</summary>
    internal static MainActivity? Current { get; private set; }

    protected override AppBuilder CustomizeAppBuilder(AppBuilder builder) => MobileApp.UseFonts(base.CustomizeAppBuilder(builder));

    protected override void OnCreate(Bundle? savedInstanceState)
    {
        Current = this;
        // The phone's font size setting (text only, never the layout) and "remove animations", before the view is built;
        // a change while the app runs recreates the activity and lands here again.
        MobileApp.ApplyFontScale(Resources?.Configuration?.FontScale ?? 1f);
        MobileTheme.ReduceMotion = Android.Provider.Settings.Global.GetFloat(ContentResolver, Android.Provider.Settings.Global.AnimatorDurationScale, 1f) == 0f;
        base.OnCreate(savedInstanceState);
        OpenPage(Intent);
    }

    protected override void OnNewIntent(Intent? intent)
    {
        base.OnNewIntent(intent);
        OpenPage(intent);
    }

    protected override void OnResume()
    {
        base.OnResume();
        Current = this;
        // The user may be back from the notification or battery prompt.
        AndroidPermissions.Instance?.RaiseChanged();
        // Whatever the phone went through in the background, the user now looks at the truth: a socket that died quietly
        // is found and replaced at once instead of at the next heart-beat, and the lists are read again.
        if (MobileRuntime.IsStarted)
        {
            MobileRuntime.Services.GetRequiredService<MainViewModel>().OnAppResumed();
        }
    }

    /// <summary>
    /// The activity handles <c>uiMode</c> changes itself (it is not recreated), and Avalonia reads light or dark only at
    /// start: follow the phone's setting while the app runs too.
    /// </summary>
    public override void OnConfigurationChanged(Configuration newConfig)
    {
        base.OnConfigurationChanged(newConfig);
        var night = (newConfig.UiMode & UiMode.NightMask) == UiMode.NightYes;
        Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            if (Avalonia.Application.Current is { } app)
            {
                app.RequestedThemeVariant = night ? Avalonia.Styling.ThemeVariant.Dark : Avalonia.Styling.ThemeVariant.Light;
            }
        });
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

    internal static Intent OpenIntent(Context context, string? page = null)
    {
        var intent = new Intent(context, typeof(MainActivity)).AddFlags(ActivityFlags.NewTask | ActivityFlags.SingleTop);
        if (page is not null) intent.PutExtra(ExtraPage, page);
        return intent;
    }

    private static void OpenPage(Intent? intent)
    {
        if (intent?.GetStringExtra(ExtraPage) == ContactsPage && MobileRuntime.IsStarted)
        {
            var main = MobileRuntime.Services.GetRequiredService<MainViewModel>();
            Avalonia.Threading.Dispatcher.UIThread.Post(main.ShowContacts);
        }
    }
}
