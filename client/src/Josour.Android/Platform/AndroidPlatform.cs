using Android.Content;
using Josour.Mobile.Platform;

namespace Josour.Droid.Platform;

internal static class AndroidPlatform
{
    public static MobilePlatform Create(Context context)
    {
        var app = context.ApplicationContext ?? context;
        var notifier = new AndroidNotifier(app);
        return new MobilePlatform(
            new AndroidDeviceInfo(app),
            loggers => new AndroidSecretStore(loggers),
            new AndroidNetworkFacts(app),
            new AndroidHosting(app, notifier),
            notifier,
            new AndroidPermissions(app));
    }
}
