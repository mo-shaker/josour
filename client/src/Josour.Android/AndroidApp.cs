using Android.App;
using Android.Runtime;
using Josour.Droid.Platform;
using Josour.Infrastructure.Settings;
using Josour.Mobile;

namespace Josour.Droid;

/// <summary>The process: supplies the Android platform to the shared phone interface before anything else runs.</summary>
[Application]
public sealed class AndroidApp : Application
{
    public AndroidApp(IntPtr handle, JniHandleOwnership ownership) : base(handle, ownership) { }

    public override void OnCreate()
    {
        base.OnCreate();
        // Before the platform: the notification channels are named in the interface language.
        MobileLanguage.Apply(MobileLanguage.FromSettings(new AppSettingsStore().Current));
        MobileApp.Platform = AndroidPlatform.Create(this);
    }
}
