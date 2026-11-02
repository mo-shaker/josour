using Android.App;
using Android.Runtime;
using Josour.Droid.Platform;
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
        MobileApp.Platform = AndroidPlatform.Create(this);
    }
}
